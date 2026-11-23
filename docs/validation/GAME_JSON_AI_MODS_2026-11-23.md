# Safe AI-driven JSON game configuration mods — 2026-11-23

Status: **PARTIAL / SOURCE ONLY / NOT NATIVE-TESTED / NOT RELEASE READY**.

## User story

PaRuX must support AI-assisted game mods **beyond skins**. This focused slice adds one more conservative adapter for *existing scalar JSON configuration properties*, alongside existing INI/CFG one-key editing, offline managed text changes and PNG texture ZIP export. The Playnite game library remains the optional frontend; PaRuX owns mod proposals, approvals, integrity checks and rollback.

## Preview request (existing API and AI tool)

- Owner-only `POST /api/v1/gaming/mods/temporary/patch-preview` and AI tool `game_mod_patch` already exist. `GameModSettingPatchRequest` now also accepts `.json`.
- Example request *fields*: `relative_path=config/options.json`, `section=graphics`, `key=fps`, `value=120`. The `value` is a **string containing a valid JSON literal**, not raw text added to a JSON document. To replace a string property use a JSON-quoted literal; a bare word is rejected.
- `section` is empty for root properties or up to four existing nested object names separated by dots. Arrays and object-valued properties cannot be replaced.
- Existing property must be unique; the JSON file must have no duplicate names anywhere, so reserialization cannot silently collapse unrelated data. A scalar may only change to the same JSON type (or to/from null).
- Every proposal is a preview only. The current game file is read from a safe, PaRuX-managed directory **only after** exact offline, confirmed paused-game identity checks. No file content goes to the model. Preview Core rehashes the file; apply is separately authorized through desktop-owner UI with a short-lived single-use preview, backup and SHA-checked restore.
- JSON serialization may reformat whitespace. The owner must inspect **the full before/after**, not assume a surgical byte edit. No game-specific reload is guaranteed.
- No unverified Playnite/Steam/Epic game folder is modified, no executable/DRM/anti-cheat memory patch, game process injection, script execution or arbitrary binary modding.

## Files

- `src/PaRuX.Server/Orchestration/Gaming/GameJsonSettingPatch.cs`: pure, bounded patch.
- `src/PaRuX.Server/Orchestration/Gaming/TemporaryGameModService.cs`: existing service routes INI/CFG/JSON through the same preview/approval/restore sequence.
- `src/PaRuX.Server/Orchestration/AgentLibraryTools.cs`: same `game_mod_patch` model tool, consent binding and permission gate; schema mentions JSON and denial remains safe.
- `src/PaRuX.Contracts/GameTemporaryModContracts.cs`: shared request semantics.
- `tests/PaRuX.Server.Tests/TemporaryGameModServiceTests.cs`: new synthetic E2E-style preview/apply/restore and negative unit tests.

## Validation on native Windows / .NET 10 (not run here)

1. `dotnet build PaRuX.sln -c Release -p:Platform=x64`.
2. `dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj -c Release --filter "FullyQualifiedName~TemporaryGameModServiceTests|FullyQualifiedName~AgentLibraryToolsTests"` and then full Server xUnit with `--blame-hang --blame-hang-timeout 4m`.
3. Test malformed JSON, duplicate property names anywhere, nested paths, missing path, type changes, arrays, objects, non-ASCII controls, trailing whitespace and near-64KiB JSON. Include file-change-before-apply, skip approval, lost pause, wrong PID, game exit and rollback conflicts.
4. Use only a synthetic PaRuX-managed test game and isolated data root; verify AI preview confirmation and owner-only UI, cancellation, 5-minute expiry, changed-file SHA rejection, backup and restoration on process exit. No real games/saves or user-controlled configs should be modified by test automation.
5. Run exclusive FlaUI: controller navigation in Games tools/AI mod review, EN/IT labels, 13-inch layout and Fullscreen Playnite return flow.
6. Source/security/public-safety check via `tools/check-public-safety.ps1` and independent code review. Hardware and real supported game-loader acceptance must be recorded separately. Only merge after gates pass.

Still outside this slice: game-specific mod formats/packages and install adapters, AI-generated scripts under trusted mod loaders, dependency-aware mod manager, compatibility metadata, mod conflict detection, save integration, Playnite metadata synchronization, verified staging of officially obtained game downloads, and true hardware/console performance.

**No Windows/.NET SDK, xUnit or FlaUI was executed in this remote GitHub editing session. No merge into main, tag, signing or release.**
