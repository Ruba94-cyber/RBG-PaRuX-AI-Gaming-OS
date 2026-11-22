# Playnite safe-switch validation (2026-11-23)

Status: **SOURCE PATCH / NOT WINDOWS-VERIFIED / DO NOT MERGE YET**.

## Why
`PlayniteLauncher.Switch` formerly closed a PaRuX-owned Playnite before checking if the target mode could actually launch. With Console-only-on-TV enabled, an absent target TV could close a working Desktop instance and then simply restart Desktop. A missing Fullscreen executable or a skipped/undetected Playnite could likewise leave the user without the original session.

## Source changes
- Reject a switch **before** `CloseMainWindow` when Playnite is skipped or no longer detected.
- Resolve target Console mode and connected-display fallback before any close. If it would resolve back to Desktop, leave the existing instance running and explain why.
- Confirm that the target executable exists and passes the existing safe-path check before any close.
- Keep existing rules: only close Playnite started by PaRuX, never force-kill, never switch during a game, preserve user-managed Playnite files and settings.
- Correct Playnite's Program Files source label from `local-app-data` to `program-files`.
- Add five new unit tests: four switch no-close cases and one locator-source case.

## Native Windows validation (required)
Use an isolated clean checkout of the PR tip with `dotnet` SDK 10; do not run against the owner's active games or profiles.

```powershell
dotnet build PaRuX.sln -c Release -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj -c Release --filter "FullyQualifiedName~PlayniteLauncherTests|FullyQualifiedName~PlayniteLocatorTests"
dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj -c Release --blame-hang --blame-hang-timeout 4m
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1
```

Before integration, execute the existing exclusive WinUI/FlaUI suite according to `AGENTS.md`. This remote GitHub session **did not run** these commands; no compiler/runtime/UI result is claimed.

## Real Playnite/console acceptance
1. On Windows, use a Playnite test installation (or explicitly user-authorized existing one); preserve all user-managed files and never touch the owner's ConsoleMode.exe.
2. Start Playnite Desktop through PaRuX. Disconnect the selected Console TV while `ConsoleOnlyWhenTargetConnected` is enabled. Switch must report a refusal; the same Desktop process remains alive and focused/usable.
3. Repeat with Fullscreen executable unavailable and with component skipped/undetected in isolated tests. No close request is allowed; no extra process starts.
4. Restore TV and Fullscreen executable: switch Desktop -> Fullscreen -> Desktop succeeds. Check no duplicate process. While a real game is active, switching is refused.
5. Check controller-only launch/return (Xbox, Stadia, third-party pad as available), real-game session start/stop, PaRuX AI hard-off and VRAM release verification, and startup/return behavior. These are **not** established by unit tests or simulated pads.
6. Re-run UI smoke/regression with IT/EN, TV/DPI and failure messages; measure gaming overhead only with a real reproducible run before claiming performance parity.

## Merge policy
Do not merge this PR, or the six handoff worktree branches, solely on authored tests or static review. The 2026-11-22 handoff also records two older draft PRs #8/#9 as unverified; #9's native Gaming work may be superseded by the Playnite-first direction. Review diffs and verify one branch at a time, with no release/tag/signing until the project-wide acceptance gates pass.
