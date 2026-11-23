# AI gameplay presets — bounded multi-setting patch, 2026-11-23

**Status: source-only draft, tests authored but NOT RUN, no Windows/.NET 10 build or real game test.**

## What changed

PaRuX now has a *proposal-only* `game_mod_batch_patch` tool and a desktop-owner API:
`POST /api/v1/gaming/mods/temporary/batch-patch-preview`.
Both delegate to `TemporaryGameModService.PreviewBatchSettingPatch` and
`GameSettingBatchPatch.Apply`. The batch contains **2–8** existing scalar
settings in **one** INI, CFG or JSON file belonging to an owner-approved
offline single-player game inside a PaRuX-managed portable installation.

Examples of eligible values *when the game itself supports these keys*:
difficulty, stamina consumption, enemy damage, camera FOV, UI thresholds
or accessibility/gameplay toggles. This does NOT grant a game a mod API,
change game logic not exposed by its configuration, or guarantee that a
suspended process reloads its config without restarting.

One invalid operation rejects the **entire batch** before proposing a write;
duplicate paths (including differing capitalization), absent fields, JSON
object/array replacement, type changes, multiline strings, too-large files,
unsupported extensions and unverified/active processes are refused. No new
keys, executable/DLL patching, scripts or game-memory/online/DRM/anti-cheat
modification is implemented.

## Approval flow

1. The model suggests named edits via `game_mod_batch_patch`, referencing a
   game returned by the existing library and a paused PID + start time from
   `quick_resume_list`. No original game-file text is returned to the model.
2. The agent consent store binds the requested game id, paused process identity,
   relative file path and the SHA-256 of the **ordered full edits array**.
   This confirmation approves only *building a preview*, never game changes.
   A changed value/key cannot spend the previously approved confirmation.
3. Backend rejects wrong/unpaused/online games, reads the existing file with
   bounded strict UTF-8 parsing, constructs a full before/after diff in memory
   and checks the file SHA again before registering a five-minute preview.
4. A desktop owner reviews that preview in the existing PaRuX Game Mods UI and
   separately approves application. The existing journal with original bytes is
   saved first; PaRuX rechecks the exact process + SHA before the single
   atomic file replacement. The model has no apply or restore tool.
5. One rollback receipt reverts the *whole* file on owner action or verified
   process exit. Conflicts preserve the backup for manual recovery.

This feature depends on the single-key JSON work in **draft PR #14**. This
branch/PR must be reviewed/compiled atop #14 first, then rebased only after
the dependency is ready. The new Tools page in draft PR #15 already contains
the review and rollback dialogs; no UI has been replaced in this branch.

## Changes to inspect

- `src/PaRuX.Contracts/GameTemporaryModContracts.cs`: edit + batch records
- `src/PaRuX.Server/Orchestration/Gaming/GameSettingBatchPatch.cs`: bounded pure patch composition
- `src/PaRuX.Server/Orchestration/Gaming/TemporaryGameModService.cs`: preview + SHA/pause checks
- `src/PaRuX.Server/Orchestration/Gaming/TemporaryGameModEndpoints.cs`: owner-only route, 16 KiB body
- `src/PaRuX.Server/Orchestration/AgentLibraryTools.cs`: strict structured tool schema/parser, per-argument consent binding, no original content in output
- `src/PaRuX.Server/Orchestration/AgentLibraryToolsBackend.cs`: production service adapter
- `tests/PaRuX.Server.Tests/TemporaryGameModServiceTests.cs`: JSON/INI proposed values, no writes on preview, approve/apply/restore and invalid complete-batch rejection
- `tests/PaRuX.Server.Tests/LibraryAgentToolsTests.cs`: schema/allow list, tampered approval refusal, no config leak, rejected nested objects/values and unpaused process

## Mandatory native validation

On the user's actual Windows 11 build machine, after resolving branch
dependencies and with `dotnet --info` showing the correct .NET 10 SDK:

```powershell
dotnet build PaRuX.sln -c Release -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj -c Release --filter "FullyQualifiedName~TemporaryGameModServiceTests|FullyQualifiedName~LibraryAgentToolsTests" --blame-hang --blame-hang-timeout 4m
```

Then test full Server suite, API owner vs LAN/unauthenticated denial, 8-edit
boundary, 64 KiB UTF-8, duplicate JSON keys anywhere, all-or-nothing failure,
old/new file races, duplicate approvals, expired preview, crash recovery and
restore after PID exit/relaunch. Test the existing owner-only preview dialog
with a **synthetic, permitted offline game** and explicit consent. Validate
that a real game actually exposes and reloads the requested settings; many
do not. Check full before/after diff readability at 13-inch/4K/EN/IT.

No remote game launch, mod installation, PowerShell execution, GitHub Actions,
build, test, merge, tag, packaging, signature or release occurred in this
source-editing environment. Do not infer native pass from static source checks.
