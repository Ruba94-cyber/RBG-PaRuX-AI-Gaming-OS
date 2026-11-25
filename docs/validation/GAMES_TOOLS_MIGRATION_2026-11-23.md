# Playnite-first Games tools: staged migration — 2026-11-23

**STATUS: SOURCE IMPLEMENTED / TESTS AUTHORED / NO WINDOWS BUILD, FLAUI OR PHYSICAL GAMEPAD RUN. NOT READY FOR MAIN/RELEASE.**

## Product intent and preserved ownership

PaRuX is the unified AI + Gaming application. Playnite, when installed/selected, provides the game library and its Desktop/Fullscreen presentation; PaRuX continues to own controller actions/config, AI/Gaming resource handoff, game downloads/inspection/staging, legally obtained game installer/portable flows, AI-assisted mods **beyond skins**, backup/restore, Quick Resume, streaming and sign-in apps.

This is **step 1** of `docs/GAMING_REMOVAL_MAP_2026-11-22.md`: a navigable tools hub without deleting native library/controller/Console code or user settings. Existing `GameLibrary_Section_Library` remains the default until a verified migration, so shortcut deep links and preexisting FlaUI test navigation remain valid. A prominent `GameLibrary_OpenTools_Button` enters `GameLibrary_Section_Tools`.

## Implemented

- `src/PaRuX.App/Views/GamesToolsHome.cs`: simple full-width, wrap-safe, controller-focusable actions with EN/IT text and stable `GamesTools_*` automation identifiers. The Playnite card opens the existing optional Console, Desktop and setup launcher surface; status is fetched **only on explicit Tools entry or Refresh**, not while an invisible page is built.
- Direct tools actions reuse `TemporaryGameModsDialog.ShowPendingAiProposalsAsync`, `ShowRestoreListAsync`, `GameAssetModStudioDialog.ShowAsync` and `GameModDropInDialog.ShowInstalledAsync`: no replacement of the owner approval, hash check, backup, restore or mod-loader compatibility gates. AI mods are not restricted to skins: existing text config proposals are retained; separate JSON one-property enhancements are in draft PR #14.
- Download, installer/import, save backup/status and controller settings buttons navigate to the **existing** panels and Settings UI. The backend game download manager continues to govern checksum, source, staging, edition restrictions and AI/Gaming download pause. No URL, account, game file, launcher or process is touched by merely viewing the new hub.
- Quick Resume panel instantiated **only when its expander is opened**, not on app startup.
- New Playnite opens are refused in the UI if the status is unavailable or shows a tracked game. The desktop-owner Core `/api/v1/gaming/playnite/launch` endpoint also refuses while `PlayniteSessionService.GameRunning` is true; source guard is factored into `PlayniteForegroundAdmission` and receives xUnit tests. This complements but does not replace PR #12 (race-safe single-instance launch, safe switch and return-to-AI).
- Legacy native Console mode selects Library only on the transition to preserve its still-live tile-focused pad navigation. Do not remove the shell before implementing the Playnite replacement and native controller acceptance.
- The existing persisted controller action `gamingLibrary` (and Desktop SwitchWorkspace → Games) now opens the Tools section and focuses the first Playnite Console command; the old action key and older Console path are preserved. The controller settings link reuses the same explicit settings navigation without synthesizing a second pad event.
- The Desktop pad route selects Tools **before** `ManualGameLibraryView.OnShown`, which skips the obsolete native tile/catalogue refresh for this path. Manual/legacy Library and native deep-link workflows retain their normal refresh behavior.
- `PlayniteLauncherForegroundPolicy` is now shared by the Games hub and `MainWindow.ShowPlayniteLauncherAsync`. Direct shortcut, second-instance and controller Console/Desktop requests check the native gaming marker and Playnite session *before* `PadBringForward`; unknown/transitional session states refuse instead of taking focus. Owner-requested setup/settings are still allowed. Core additionally checks both `PlayniteSessionService.GameRunning` and `LocalRuntimeService.GamingMode`.
- The Playnite modal is now **awaited** in the Games hub: the launching button remains busy while the modal is open, and Playnite status refreshes after returning from Locate / Install / Skip. These are interaction-source changes only, not test evidence.

## Tests authored

- `tests/PaRuX.UITests/Tests/GamesToolsHomeUiTests.cs`: isolated data root, navigates Games → Tools via shortcut, verifies buttons/AutomationIds, opens preexisting Downloads / Add inspector / Devices save backup via direct links and confirms the old Library still exists. No launches, downloads or mod writes.
- `tests/PaRuX.Server.Tests/PlayniteComponentTests.cs`: Core refusal during a tracked or native game; strict foreground preflight for idle/disabled, counted processes, unknown, failed and transitional states, and setup/settings exemption.
- `tests/PaRuX.Server.Tests/PadActionTests.cs`: persisted `gamingLibrary` key and bilingual controller action label survive the migration.

## Native acceptance required before merge

1. On Windows 11 / .NET 10, build **the exact branch**: `dotnet build PaRuX.sln -c Release -p:Platform=x64`.
2. Run focused `dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj -c Release --filter "FullyQualifiedName~PlayniteForegroundAdmissionTests|FullyQualifiedName~PlayniteLauncherTests|FullyQualifiedName~PlayniteGameSessionTrackerTests"`.
3. Run full Server test suite `--blame-hang --blame-hang-timeout 4m`, investigate known hang, then **exclusive** FlaUI: `dotnet test tests/PaRuX.UITests/PaRuX.UITests.csproj -c Release --filter "FullyQualifiedName~GamesToolsHomeUiTests|FullyQualifiedName~GamingSectionsAndEmulatorUiTests|FullyQualifiedName~PlayniteImportUiTests|FullyQualifiedName~QuickResumeAndMetadataUiTests|FullyQualifiedName~SaveBackupSyncUiTests"`. Fix any preexisting assumptions about page height/scrolling; do not force tests green by deleting assertions.
4. On a real PC with physical HID/XInput pad and TV: Tools → Playnite Console/Fullscreen, other modes, no-installed setup, back to PaRuX, controller-only focus and refusal while a tracked/native game runs. Verify **no PaRuX window activation before refusal**, including direct Desktop/Console shortcuts and second-instance requests. Verify the `gamingLibrary` pad action focuses the Tools Console button in Desktop while old Console still uses Library. Test stale status, races, repeated clicks, modals after setup changes and in-game input passthrough; measure actual GPU handoff/VRAM/fps.
5. Exercise every Tools mod dialog under synthetic, owner-approved offline game: no silent writes, preview and single-use owner consent, installer staging/hash and backups, restore/conflicts, unsupported loaders. Playnite and old ConsoleMode files are read-only unless the owner explicitly authorizes a scoped action.
6. Inspect Normal/Advanced, English/Italian, dark/light/high-contrast, small 13-inch/900 DIP and 4K TV layout, scrolling, keyboard/gamepad UIA focus, modals/back-button accessibility and no hidden process scanning. Ensure the quick panel is not constructed before expansion.
7. Independently review controller/launch admission, run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1` on the local PC, reconcile `PARUX_EXECUTION_PLAN.md`.

## Migration dependencies and unresolved work

- **PR #12** must first pass real Windows tests: Playnite single-process launch, safe switch and return tracking.
- **PR #13** separately adds PaRuX controller bindings for Console and Desktop. This PR must not claim those gamepad shortcuts are currently enabled on main.
- **PR #14** separately adds reversible JSON one-key AI mod previews. This hub reuses the existing mod review UI; JSON must pass native build and tests before inclusion.
- **Later verified PRs**: promote Tools to default Games entry, move (not copy) Quick Resume, backup and install panels to the tools hierarchy, remove tile-library/Console shell in stages, preserve the routing IDs and persisted settings migration, re-point old UI tests and remove only truly unreachable code. Controller driver/profile support and real-game Playnite handoff still need acceptance. Never blindly delete files or alter any other launcher app.

This session used the GitHub connector to edit branches directly, **not** a Windows runtime. Nothing was merged into main, packaged, signed, published or verified on the owner's hardware.

**Limitation:** the UI and Core perform admission checks when a command arrives; no polling-based check can guarantee that an external game never starts between status sampling and foreground activation. The server checks again immediately before Playnite process launch, but actual foreground and process race behavior must be validated on a real Windows session.
