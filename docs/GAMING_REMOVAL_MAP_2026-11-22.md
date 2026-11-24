# Gaming / TV removal map (2026-11-22)

Status: ANALYSIS ONLY. Nothing was deleted or changed in `src/` or `tests/`. Source of truth: working code at branch `worktree-agent-ae8fcea9d87ad7571` (merged with `main`), grep and symbol references, not older plans.

## 1. Owner decision this map implements

Playnite (+ Aniki ReMake theme) becomes an optional dependency. The native TV/Console shell and the native Gaming library UI are removed. These stay intact and working:

- (a) game downloads, installs, staging, installer inspection, store providers as used for installs;
- (b) game modding and mod management, including AI-driven modding (GameMod*, GameAssetMod*, drop-in, mod process guard, temporary mods, skin studio) and their UI entry points;
- (c) the AI/game resource handoff (WorkloadOrchestrator, VRAM release verification, Playnite session tracker), pad actions and logical buttons, streaming mode, sign-in apps, the Playnite component and launcher surface;
- (d) auth, pairing, LAN security, backups, user data and settings migration.

## 2. Result in numbers

Method: every file under `src/` and `tests/` whose name or content is Gaming/TV/Console/Playnite/pad/streaming/mod/store/library related was classified (rules in appendix A, per-file table in appendix B). Android and CLI have only a `gamingMode` DTO flag and help text and need no change. Shared files that only mention gaming are listed separately in section 6 as EDIT (they are not counted below).

| Class | src files | tests files | Meaning |
|---|---:|---:|---|
| KEEP | 151 | 99 | stays as is (backend, handoff, Playnite, pad, streaming, sign-in, downloads/installs, mods, saves) |
| MOVE | 14 | 6 | stays, but its UI must be re-hosted on the new Games tools page |
| REMOVE | 74 | 55 | deleted in the staged plan |
| DECIDE | 37 | 23 | needs an owner decision (virtual pad / HidHide / XInput shim / raw pad / display modes / local game guide) |
| EDIT (tests only) | - | 6 | tests that scan Views source or only touch the Gaming tab: retarget, do not just delete |
| Total classified | 276 | 189 | 465 files, about 88.6k lines (KEEP about 45.8k, REMOVE about 27.1k, DECIDE about 10.3k, MOVE about 4.7k, EDIT 0.8k) |

Plus about 35 shared files that must be edited (section 6).

## 3. Proposed navigation: Gaming workspace becomes a slim "Games" workspace

Today: top-level mode tab AI | Gaming (Ctrl+1/Ctrl+2, setup profile Both / AiOnly / GamingOnly), `__games__` workspace = `ManualGameLibraryView` (hosts `UniversalLibraryPanel` > `GamingHomeView` > `GameDetailPanel`, tiles, shelves, Console/TV button).

Proposal (one nav item `Navigation_GameLibrary_Item`, tag `__games__`, kept so every FlaUI helper and deep link keeps working), new view `GamesToolsView`, Normal vs Advanced disclosure:

1. Play: Playnite card (status from `PlayniteLauncherSurface`/`PlayniteClient`, Console / Desktop / Streaming / Settings, "Install with PaRuX / Locate existing / Skip" setup flow already in `PlayniteSetupFlow`), plus `PausedGameBanner` and the Quick Resume list (resume and AI-handoff state).
2. AI Mod Studio (Normal): AI Game Asset Mods, Installed AI mods, Temporary mods (restore, pending AI proposals), Minecraft Skin Studio. Same dialogs, same AutomationIds.
3. Downloads and installs (Normal): `GameDownloadsPanel`, installer inspect and stage block, store edition panel, staged packages.
4. Library import status (Advanced): `PlayniteImportPanel`, store discovery refresh (Steam/Epic/GOG), first-run scan offer, health summary. No tiles, no detail panel.
5. Save backups (Advanced): `GameSaveBackupPanel` (user data safety must stay reachable).
6. Devices (Advanced, only if the DECIDE bucket keeps it): `GamingDevicesPanel`.

Shell changes: remove the `ConsoleModeButton` and the Console/TV tray flow; keep the tray Playnite item. Keep the AI|Gaming tab in the first iterations (step 2), then collapse it (step 6): "Games" becomes an ordinary workspace in every profile that has Games on, Ctrl+2 still opens it, `GamingOnly` profile starts on it. `LastModeTab` stays readable and is ignored.

## 4. Group table (KEEP / MOVE / REMOVE / DECIDE) with dependency edges and tests

Counts are files (src + tests together). "Edges" are found with grep on type names (outside the file itself).

| Id | Area | Class | Files | Key files | Edges (who uses it) | Tests that go with it |
|---|---|---|---:|---|---|---|
| A1 | Downloads, torrent, portable install, package staging, installer inspection, store discovery (Steam, Epic, GOG, extra stores), launch-id policy | KEEP | 38 | `Gaming/Downloads/*`, `GamePackageStageService`, `GameInstallerInspectionService`, `GameFolderInspector`, `Steam/Epic/Gog*`, `ExtraStoreDiscovery`, `GameStoreLaunchIdPolicy`, `Contracts/Game{Download,PackageStage,InstallerInspection}Contracts` | `Program.cs` registration, `ManualGameLibrary` (hub), `AgentLibraryToolsBackend`, UI `GameDownloadsPanel` and `ManualGameLibraryView.Installer/Stores` | `GameDownloadManagerTests`, `GameDownloadTorrentTests`, `GamePackageStageTests`, `GameInstallerInspectionTests`, `PortableGameInstallServiceTests`, `Steam/Epic/Gog*Tests`, `GamingDownloadPauseTests` |
| A2 | Downloads/installs UI | MOVE | 4 | `GameDownloadsPanel`, `ManualGameLibraryView.Installer`, `ManualGameLibraryView.Stores`, FlaUI `RealDownloadsUiTests`, `GamingDownloadsAndDevicesUiTests` | hosted only by `ManualGameLibraryView` | FlaUI tests above (IDs `GameDownloads_*`, `GameLibrary_Installer_*`, `GameLibrary_*_Refresh_Button`, `GameLibrary_FirstRunScan_*`) |
| B1 | Mod backends: asset mods, drop-in, process guard, temporary mods + recovery, skin studio, UV restyler, PNG guard, ini patch | KEEP | 22 | `GameAssetMod{Service,Endpoints}`, `GameModDropInService`, `GameModProcessGuard`, `TemporaryGameMod{Service,Endpoints,RecoveryHostedService}`, `GameSkinStudio*`, `MinecraftSkinUvRestyler`, `PngAssetGuard`, `GameIniSettingPatch`, matching Contracts | `Program.cs`; `AgentLibraryTools(+Backend)` (AI-driven flows call `TemporaryGameModService` and `GameTitleStore`); `ManualGameLibrary`, `QuickResumeService` | `GameAssetModServiceTests`, `GameModDropInServiceTests`, `GameModDropInErrorMappingTests`, `GameModProcessGuardTests`, `TemporaryGameModServiceTests`, `GameSkinStudioServiceTests`, `MinecraftSkinUvRestylerTests`, `AgentDiscoverQuickResumeToolsTests`, LAN auth rows in `BrowserOwnerAndLanHardeningTests` |
| B2 | Mod dialogs and QuickResume panel (mod buttons) and paused-game banner | MOVE | 8 (+2 tests) | `GameAssetModStudioDialog`, `GameModDropInDialog`, `TemporaryGameModsDialog`, `MinecraftSkinStudioDialog`, `QuickResumePanel`, `PausedGameBanner` | today reached only through `GamingHomeView` (`GameHome_AssetStudio_Open`, `GameHome_InstalledMods_Open`, quick-resume row) and `QuickResumePanel`; `PausedGameBanner` is hosted only by `GamingHomeView`. This is the main re-homing job | `QuickResumeAndMetadataUiTests`, `PausedGameBannerUiTests`, `AdvancedGameManagementUiTests` (partly) |
| C1 | AI/game handoff: WorkloadOrchestrator, VRAM release verification, session record, profile store, hard-off, guard, child scope, shutdown retry, QuickResume, suspend policies, owned-game guards, manual game library/session/launch, play time tracker, App-side `GamingManager`/`GamingOrchestratorClient` | KEEP | 91 | `WorkloadOrchestrator`, `GamingVramReleaseVerifier`, `Server/Gaming{HardOff,GuardService,ChildScope,ShutdownRetry,TextMemory}`, `QuickResume*`, `OwnedGame*`, `ManualGame*`, `Contracts/{GamingReleaseVerificationText,VerifiedGameReturnSignal,QuickResumeContracts,ManualGame*}` | `McpClientRegistry` (orchestrator), `KnowledgeWebWatchEndpoints`, `ManagedMediaV2Endpoints`, `RuntimeHardwareEndpointExtensions`, `TrainingRuntimeV2Endpoints` (GamingHardOff), speech/retrieval/discovery hard-off | all `Gaming*HardOff*`, `Gaming*Transition*`, `WorkloadOrchestratorTests`, `GamingVram*`, `QuickResume*Tests`, `OwnedGame*Tests`, `ManualGame*Tests`, `GamingHardOffProcessLaunchInventoryTests` (+ the two `GamingHardOff*AllowList.txt`) |
| C2 | Playnite component, locator, launcher, session tracker, settings store, endpoints, launcher surface, setup flow, shortcut service, tray, startup arguments | KEEP | 22 | `Gaming/Playnite/*`, `Services/Playnite{Client,SetupFlow,LauncherShortcutService}`, `PlayniteLauncherSurface`, `MainWindow.Playnite{Launcher,Startup}`, `SettingsView.Playnite`, `Contracts/Playnite{Integration,Launcher}Contracts` | `MainWindow.xaml` tray item, `ConsoleDisplayPreferences` (TV choice), `PadActionPoller` for launcher pad keys | `PlayniteComponentTests`, `PlayniteLauncherSurfaceTests`, `PlayniteSessionServiceTests`, `PlayniteGameSessionTrackerTests`, `PlayniteEmulatorDefinitionsTests` |
| C3 | Pad actions, logical buttons, launcher config importer, modifier keys, Settings pad buttons | KEEP | 12 | `PadAction{Engine,Listener,Model,Policy,Poller}`, `PadLogicalButtons`, `LauncherConfigImporter`, `LauncherModifierKeys`, `MainWindow.PadActions`, `SettingsView.PadButtons` | linked into the test project by `<Compile Include>` (see risks) | `PadActionTests`, `LauncherConfigImporterTests` |
| C4 | Streaming mode, sign-in apps, TV display choice, window placement | KEEP | 17 | `StreamingModeService`, `StreamingEndpoints`, `WindowsStreaming*`, `SignInApp*` (Server + App + `MainWindow.SignInApps` + `SettingsView.SignInApps`), `ConsoleDisplayPreferences`, `SettingsView.ConsoleDisplays`, `OwnedGameWindowPlacementService` | `ConsoleDisplaySelectionPolicy` is used by `OwnedGameWindowPlacementService` and the Playnite launcher: it must NOT go with the Console shell | `StreamingModeServiceTests`, `StreamingModeIntegrationTests`, `SignInAppList*Tests`, `OwnedGameWindowPlacementPolicyTests` |
| C5 | Hub dependencies of the kept code: title store/catalog/read cache/endpoints, launch profile and hooks backend, emulator registry/discovery/command builder, external library store, local game chat policy | KEEP (trim UI-only endpoints at step 8) | 32 | `GameTitleStore`, `GameTitleCatalog`, `GameLaunchProfile`, `GameLaunchHooks`, `Emulators/*`, `ExternalGameLibraryStore`, `LocalGameChatPolicy` | `ManualGameSessionService`, `LauncherPlayTimeTracker`, `PlayniteImportEndpoints`, `AgentLibraryTools(+Backend)`, `ManualGameLibrary`, `GameTitleCatalog`, `PlayniteLibraryReader` all depend on them. They look removable but are not | `GameTitleCatalogTests`, `GameTitleLibraryFeatureTests`, `GameLaunchProfileTests`, `GameLaunchHookTests`, `Emulator*Tests`, `ExternalGameLibraryStoreTests`, `LocalGameChatPolicyTests` |
| D1 | Save backups, restore, sync, save location hints (user data) | KEEP | 15 | `GameSaveBackup*`, `GameSaveRestore`, `GameSaveLocationHints` | `Program.cs`, `GameSaveHookPresenter` | `GameSaveBackup*Tests`, `GameSaveRestoreTests`, `GameSaveSyncConflictTests`, `GameSaveLocationHintsTests` |
| D2 | Save backup panel | MOVE | 2 | `GameSaveBackupPanel`, FlaUI `SaveBackupSyncUiTests` | hosted by `GameDetailPanel` today | `SaveBackupSyncUiTests` |
| D3 | Playnite library import (reader, endpoints, contracts, panel) | MOVE | 6 | `PlayniteLibraryReader`, `PlayniteImportEndpoints`, `PlayniteImportContracts`, `PlayniteImportPanel`, FlaUI `PlayniteImportUiTests` | panel hosted by `ManualGameLibraryView`; backend depends on `EmulatorRegistry` and `GameTitleStore` | `PlayniteLibraryReaderTests`, `PlayniteImportUiTests` |
| E | Virtual pad (ViGEm), HidHide, XInput shim installer, raw pad mapping/wizard/live view, controller profiles, display modes/topology/journal, `GamingDevicesPanel`, local game guide (+ `LocalGameGuide*`) | DECIDE | 37 src + 23 tests | `Providers/VirtualPad/*`, `XInputShimInstaller`, `GamingDevicesPanel`, `ControllerMappingWizard`, `ControllerLive*`, `Gaming{Display*}`, `LocalGameGuide*` | App: `VirtualPadActivationController`, `GamingDevicesPanel`; Server: `HidHideManager` (Program.cs), `WindowsRawGamepadSource` | `VirtualPad*Tests`, `XInputShim*Tests`, `RawPad*Tests`, `PadFeelTests`, `PadMappingPresetsTests`, `GamingDisplay*Tests`, `LocalGameGuide*Tests`, `GamingDevicesHidingOptionalContractTests` |
| F | Console/TV shell: `MainWindow.Console{Mode,Pad,Startup}`, `OnScreenKeyboard{Overlay,Model}` + `MainWindow.OnScreenKeyboard`, `ConsoleRawPadNavigator`, `Console{Navigation,PadOverlay,Startup}Policy`, `ConsoleTextEntry`, `ManualGameLibraryView.Console` | REMOVE | 24 | see appendix B | `MainWindow.xaml` (`ConsoleModeButton`, `RootGrid_ConsoleKeyDown`), `MainWindow.ModeTabs`, `Workspaces`, `Accessibility`, `Responsive`, `LazyPages`, `ManualGameLibraryView*`; tests `StartupLazyPageContractTests` read `MainWindow.ConsoleMode.cs` | `Console*PolicyTests`, `ConsoleTextEntryTests`, `OnScreenKeyboardModelTests`; FlaUI `ConsoleModeUiTests`, `ConsoleStartupUiTests`, `ConsolePadOperationUiTests`, `ConsoleAndLanguageUiTests`, `RealGameConsolePadTests`, `OnScreenKeyboardUiTests`, `ConsolePadSupport` |
| G | Library UI and extras: `GamingHomeView` (2.5k lines), `GameDetailPanel`, tile grid, shelf feed, hero card, grouped library, side filters, wishlist dialog, smart collections, shortcuts section, launch profile/hooks/controller profile sections, `UniversalLibraryPanel`, `ManualGameLibraryView` core, `GamingLibraryClient` (partly), achievements (Steam + RetroAchievements services, toasts, settings), SteamGridDB, metadata/IGDB, library theme and game extensions, discover (recommender, Steam catalog), wishlist store, smart collection store, library contracts | REMOVE | 105 (62 src, 43 tests) | see appendix B | `UniversalLibraryPanel` hosts `GamingHomeView`; `GamingHomeView` hosts `GameDetailPanel`, `QuickResumePanel`, `PausedGameBanner`; `WebhookRuntime` references the achievement services; `Program.cs` registers all stores | `Game*Library*`, `GameTile*`, `GameShelf*`, `GameHero*`, `GameFilter*`, `GameSmartCollection*`, `LocalWishlist*`, `GameDiscover*`, `RetroAchievementsTests`, `SteamAchievementsServiceTests`, `SteamGridDbGalleryTests`, `GameExtensionTests`, `GameArtStoreTests`, `GameMetadataParsingTests`, FlaUI `Gaming*UiTests` (13 files), `ManualGameLibraryUiTests`, `AdvancedGameManagementUiTests`, `RealDevicesAndGamesUiTests`, `RealGameLaunchUiTests`, `LocalOnlyAndGamingHonestyCoverageTests` |

Note: ids F and G together are 74 src and 55 test REMOVE files (appendix B has every row). Group file counts include both src and tests.

## 5. UI AutomationIds and FlaUI tests affected

AutomationIds that must keep their exact string (moved controls; existing tests stay green after the move, rename only in a later, explicit step):

- `Navigation_GameLibrary_Item` (13 uses in UITests), `MainWindow_ModeTab_Gaming` (33 uses): keep until step 6 collapses the tab.
- Moved with the mod/handoff UI: `GameHome_AssetStudio_Open`, `GameHome_InstalledMods_Open`, `GameAssetStudio_*`, `QuickResume_*` (list, pause, resume, recovery, `QuickResume_Mods_*`, `QuickResume_Mods_SkinStudioButton`), `SkinStudio_*`.
- Moved with downloads/installs: `GameDownloads_*`, `GameLibrary_Installer_*`, `GameLibrary_{AllStores,Steam,Epic,Gog}_Refresh_Button`, `GameLibrary_Steam_Status_Text`, `GameLibrary_FirstRunScan_*`, `GameLibrary_Store_*`.
- Moved with import and saves: `GameImport_*` (if kept), Playnite import panel ids, save backup ids.
- Stay with Playnite: ids built in `PlayniteLauncherSurface` and `SettingsView.Playnite`.
- Disappear with their views (tests deleted): all `GameHome_*`, `GameDetail_*`, `GameTitle_*`, `GameQuick_*`, `GameLibrary_Strip*`, `GameLibrary_Gaming_*`, `GameLibrary_ReturnToAi_Button`, `GameLibrary_Sections`, `Emulators_*`, `GameDevices_*` (only if DECIDE removes), `MainWindow_ConsoleMode_Button` (10 uses).

FlaUI test files (UITests): 28 files reference removed IDs. Disposition:
- Delete (REMOVE): the 13 `Gaming*UiTests`, `ManualGameLibraryUiTests`, `AdvancedGameManagementUiTests`, `RealDevicesAndGamesUiTests`, `RealGameLaunchUiTests`, `LocalOnlyAndGamingHonestyCoverageTests` (+ `GamingSectionsPage`, `LocalOnlyAndGamingHonestyPage`), the 5 `Console*`/`OnScreenKeyboard` tests, `RealGameConsolePadTests`, infrastructure `ConsolePadSupport`, `SyntheticGameLibrary` (after the last user is gone).
- Keep and re-point (MOVE): `QuickResumeAndMetadataUiTests`, `PausedGameBannerUiTests`, `GamingDownloadsAndDevicesUiTests`, `RealDownloadsUiTests`, `PlayniteImportUiTests`, `SaveBackupSyncUiTests` (they stay valid because IDs are preserved; only their navigation preamble changes if the host page changes).
- Edit (EDIT): `ModeTabsUiTests`, `SetupProfileRuntimeUiTests`, `SimulatedExternalsUiTests`, plus `ResizeStabilityUiTests`, `CreateModeTabsAndSetupTests`, `OnboardingView*` tests when the tab or profile changes.

## 6. Shared files that must be edited (not counted as KEEP or REMOVE)

`MainWindow.xaml` (Console button, key handler, tray item stays, window title `AI & Gaming OS` is branding, leave), `MainWindow.xaml.cs`, `MainWindow.Workspaces.cs`, `MainWindow.LazyPages.cs`, `MainWindow.ModeTabs.cs`, `MainWindow.Responsive.cs`, `MainWindow.Accessibility.cs`, `MainWindow.Tour.cs`, `MainWindow.CommandPalette.cs`, `App.xaml.cs` (startup Console resolution), `Models/AppSettings.cs` (obsolete fields below), `Services/WorkspaceNavigationOrder`, `SetupProfilePolicy`, `SetupProfileTransition`, `SetupProfileRuntime`, `SettingsCategoryPolicy`, `SettingsSectionVisibilityPolicy`, `InAppHelpCatalog`, `DiscoveryPolicyPresenter`, `ComponentLifecyclePresenter`, `RuntimeRecoveryGuidance`, `SettingsView.xaml(.cs)`, `SettingsView.{Extensions,DiscoveryPolicy,SetupProfile,Knowledge,OwnerRecords,ProfileSections}`, `OnboardingView.{SetupProfile,xaml,FirstModel}`, `ChatView.xaml(.cs)` (gaming handoff banner, keep), `Server/Program.cs` (registrations and endpoint maps), `Server/WebhookRuntime.cs` (achievement events), `Server/Orchestration/AgentLibraryTools*.cs` (only if a removed store is referenced), `tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj` (linked sources), `BrowserOwnerAndLanHardeningTests` (route table rows of removed routes only).

## 7. Settings and data that must be migrated (or deliberately left)

Rule: never delete user data silently; unknown or obsolete JSON members must still load (all settings readers here are version tolerant). Prefer "leave in place, mark obsolete, add an opt-in cleanup".

`AppSettings` (settings.json) fields that become dead after removal; keep the properties (marked `[Obsolete]`, still serialised) for at least one release so downgrade and rollback do not lose them:
`GameLibrarySort/Layout/Group/CoverSize/CompactTop/Source/Platform/Installed/FavoritesOnly/Tag/ShowHidden/Presets`, `GameMetadataEnabled`, `GameMetadataPromptDismissed`, `GameStoreDiscoveryPromptDismissed` (kept: store discovery stays), `ActiveLibraryThemeExtensionId`, `AchievementToastsEnabled`, `ConsoleStartOnTvWhenPresent`, `ConsoleStartupFallback`-style fields, `RouteVerifiedWindowedGamesToTv`, `AutoShowConsoleAfterVerifiedGameExit`, `LastModeTab`. Kept and live: `PadActions` (including the Tv override map: the Settings pad screen should hide the TV rows but keep storing them), `ConsoleDisplays`, `ConsoleTargetDisplayId` (Playnite launcher TV), `SetupProfileId`.

Server data under the data root (leave files; no deletion code in this effort):
- KEEP stores: `manual-games.json`, `game-titles.json`/`external-game-titles.json`, `gaming-profiles.json`, `gaming-session.json`, `paused.json`, `game-downloads.json`, `game-launch-profiles.json`, `game-hooks.json`, `playnite-integration.json`, `sign-in-apps.json`, `server/game-asset-mods`, `server/game-mod-install-receipts`, `server/game-package-staging`, `server/game-save-backups`, `game-save-sync*.json`, `display-revert.json`, plus temporary-mod recovery state.
- ORPHANS after removal (kept on disk): `game-smart-collections.json`, `game-wishlist.json`, `game-discover.json` + `cache/game-discover`, `game-extensions.json` and `Documents\PaRuX\Extensions`, `game-covers`, `game-info`, `retroachievements-credentials.json`, `steam-credentials.json` (secret-protected: do not delete silently, offer a one-click "remove saved credentials" in Settings), SteamGridDB key store, `game-controller-profiles.json`.
- DECIDE bucket data: `hidhide-journal.json`, `xinput-shim-installs.json`, `gaming/raw-pad-mappings`, `gaming/raw-pad-game-profiles`, `controller-profile.json`. If the shim installer is removed, installed XInput shims inside user game folders must first be uninstalled or the user told (it mutates third-party game directories).

## 8. Staged removal plan (small verified steps)

Test command convention for every step: build App + Server + tests, then the targeted filters named below, then (at the end of the step) the FlaUI subset named below on the Debug app. No GPU, no model load needed for any step. Each step is one commit on a branch; rollback = `git revert` of that commit (no step deletes user data, so rollback never needs data repair).

| Step | What | Files | Tests that must stay green | Rollback |
|---|---|---|---|---|
| 0 | Baseline, no code change: record the current pass/fail list for the filters below, take a copy of a real data root (read-only copy used later for the migration smoke), run `tools/check-public-safety.ps1`. | none | everything in the filter list below (baseline) | n/a |
| 1 | ADD `GamesToolsView` (new, additive, behind a hidden setting `GamesSlimWorkspace`, default off) hosting existing controls unchanged with the same AutomationIds: Playnite card, mod buttons, QuickResume, paused banner, downloads/installs, import status, save backups. Extract the mod/quick-resume button row out of `GamingHomeView` into a reusable `GameModEntryPoints` control used by both pages (this is the real "MOVE"). | new `Views/GamesToolsView.cs`, `Views/GameModEntryPoints.cs`; small edits in `GamingHomeView`, `MainWindow.LazyPages` | all current Gaming FlaUI tests unchanged; new `GamesToolsUiTests` (mods open, downloads panel visible, Playnite card state, import status); `WorkspaceNavigationLayoutContractTests`, `StartupLazyPageContractTests` | revert one commit; nothing else depends on the new view |
| 2 | Flip default: `__games__` opens `GamesToolsView`; `GamingHomeView` becomes unreachable (still compiled). Library-driving FlaUI tests are tagged to skip with a clear reason. | `MainWindow.LazyPages/Workspaces`, `WorkspaceNavigationOrder` | `QuickResume*`, `PausedGameBannerUiTests`, `GamingDownloadsAndDevicesUiTests`, `PlayniteImportUiTests`, `ModeTabsUiTests`, `SetupProfileRuntimeUiTests` | flip the default back (one line) or revert |
| 3 (first REMOVE) | Remove the Console/TV shell: 24 files (F). Edit `MainWindow.xaml` (button, key handler), `ModeTabs` (button visibility), `Responsive`, `Accessibility`, startup resolution in `App.xaml.cs`. Keep `PadActions` and Tv override storage, `ConsoleDisplayPreferences`, Playnite tray item and launcher. Mark the 4 Console settings fields obsolete. | F list in appendix B + the edit list | `PadActionTests`, `LauncherConfigImporterTests`, `Playnite*Tests`, `StreamingModeService*Tests`, `OwnedGameWindowPlacement*Tests`, `StartupLazyPageContractTests` (re-pointed from `MainWindow.ConsoleMode.cs`), `WorkspaceNavigationLayoutContractTests`; FlaUI: `ModeTabsUiTests`, `PlayniteImportUiTests`, `QuickResume*` | revert; settings fields are only marked obsolete, so no data change |
| 4 | Remove the library views and their App services: `GamingHomeView`, `GameDetailPanel`, tile/shelf/hero/grouped/side-filter/wishlist/smart-collection/section views, `UniversalLibraryPanel` and `ManualGameLibraryView` core (keep `.Installer/.Stores` as MOVE parts of the new page), SteamGridDB, metadata/IGDB, library theme, `GamingLibraryClient` trimmed to the mods/downloads/installers/stores/saves/quick-resume methods. Remove matching `<Compile Include>` lines from the test csproj in the SAME commit. Retire or retarget the 3 source-scanning contract tests. | G (App part), csproj | `GameDownload*`, mods tests, `QuickResume*`, `ManualGame*`, `GameSave*`, `Playnite*`, `WorkloadOrchestratorTests`, `Gaming*HardOff*`; FlaUI: new `GamesToolsUiTests`, `QuickResume*`, `GamingDownloadsAndDevicesUiTests` | revert (largest diff: about 14k lines; keep the commit self-contained and compiling) |
| 5 | Remove achievements (Steam + RetroAchievements), game extensions, Discover, wishlist, smart collections on the server side: services, endpoints, Contracts, `WebhookRuntime` events, `Program.cs` registrations, `MainWindow.AchievementToasts/GameExtensions`, `SettingsView.RetroAchievements/Extensions`. Drop only those routes from `BrowserOwnerAndLanHardeningTests` and from any OpenAPI/endpoint inventory test; keep rows of kept routes. Add the optional "remove saved credentials" action. | G (Server part) | `BrowserOwnerAndLanHardeningTests`, `OpenApiDocumentTests`, `ProductV2EndpointsCoverageTests`, `RemotePairingEndpointTests`, `GamingHardOffProcessLaunchInventoryTests` (+ allow-lists), webhook tests | revert; data files untouched |
| 6 | Settings, profile and nav cleanup: collapse the AI/Gaming tab into a normal Games nav item, update setup profile text, onboarding, tour, command palette, help catalog and the "Gaming & AI Sharing" settings page (handoff wording only). Mark `LastModeTab` obsolete. | shared edit list | `SetupProfile*Tests`, `WorkspaceNavigationLayoutContractTests`, `AdaptiveWorkspaceUiContractTests`, `ProgressiveDisclosureUiContractTests`, `UiLocalizationHookTests`, `CodeBuiltUiLocalizationTests`; FlaUI `ModeTabs*`, `SetupProfile*`, `CreateModeTabsAndSetupTests` | revert |
| 7 | Resolve the DECIDE bucket after the owner answers section 9. If "remove": uninstall XInput shims first, then remove virtual pad/HidHide/raw pad/display-mode code and `GamingDevicesPanel`. If "keep": move `GamingDevicesPanel` into the Advanced section of the Games page. | E list | `PadActionTests` (must stay), `Streaming*Tests`, `GamingDevicesHidingOptionalContractTests` (retire or retarget) | revert; shims are the only external side effect, so uninstall code lands before removal code |
| 8 | Trim UI-only endpoints left behind in the kept services (titles list/query, library home, hooks/profile editors) once nothing in App or AI tools calls them; verify with grep and `AgentLibraryTools` tests first. | `GameTitleEndpoints`, `GamingEndpoints`, `GameLaunchProfileEndpoints` | `AgentDiscoverQuickResumeToolsTests`, `GameTitleLibraryFeatureTests`, LAN hardening | revert |
| 9 | Reconcile docs: `PARUX_EXECUTION_PLAN.md`, README feature list, in-app help, this map marked DONE per step. Run full Server suite, FlaUI regression, `tools/check-public-safety.ps1`; owner smoke on a real data copy (upgrade path from step 0 copy). | docs only | full suites | revert docs |

First removal step: step 3 (Console/TV shell, 24 files). Prerequisite and zero-deletion first commit: step 1 (the additive Games page that already hosts the mod, handoff, download and install entry points), because once the library views go, the mod buttons would otherwise have no home.

Baseline filter list for step 0: `Gaming|Game|QuickResume|Playnite|Pad|Streaming|SignInApp|WorkloadOrchestrator|VirtualPad|Console|Emulator|Steam|Epic|Gog|Launcher|Hardening|OpenApi|StartupLazyPage|WorkspaceNavigation|SetupProfile` on `PaRuX.Server.Tests`, and the FlaUI classes named in section 5.

## 9. Open owner decisions (they block only step 7)

1. Virtual pad (ViGEm), HidHide guided setup, XInput shim installer, raw pad mapping wizard and live view: keep as Advanced "Controller tools" on the Games page, or remove because Playnite/Steam Input cover it? (37 src + 23 test files, about 10k lines.) The shim installer writes into third-party game folders, which makes removal the riskiest item.
2. Display mode switching and topology (`GamingDisplay*`, journal): streaming mode uses its own display controller; confirm these are not needed outside the Console shell.
3. Local game guide (AI answers from user-supplied guides): keep (it is an AI feature) and re-home to the Games page or Chat.
4. Emulator registry/discovery: kept as a backend dependency of manual games and Playnite import; confirm no UI is wanted.
5. Tv override rows in the Pad settings: hide only (proposed) or delete the storage.

### Owner decisions recorded 2026-11-24

1. Controller tools (virtual pad, HidHide, XInput shim installer, mapping wizard, live view): **keep** as Advanced "Controller tools" on the Games page.
2. Display mode switching and topology (`GamingDisplay*`, journal): **keep**.
3. Local game guide: **keep**, re-homed on the slim Games page.
4. Emulator registry/discovery: **backend only**, no UI.
5. TV override rows in Pad settings: **hide only**, keep the stored data.

Consequence: step 7 (deleting the controller and display stacks) is cancelled; only the native Console/TV shell and library views are removed (steps 2-6, 8-9), after step 1 gives the mod/handoff/download/install entry points a home.

## 10. Risks

- Data loss / orphan settings: handled by "mark obsolete, never delete" (section 7). The only secrets at risk are `steam-credentials.json` and `retroachievements-credentials.json`: do not delete on upgrade.
- Source-scanning contract tests that read Views: `GameStoreFirstRunSourceContractTests` (globs `ManualGameLibraryView*.cs`), `GamingShelfAndHeroSourceContractTests` and `GrapheneGamingDesignRegressionTests` (read `GamingHomeView.cs`), `StartupLazyPageContractTests` (reads `MainWindow.ConsoleMode.cs`, `LazyPages`, `Workspaces`, `xaml`), `WorkspaceNavigationLayoutContractTests`, `GamingDevicesHidingOptionalContractTests` (reads `GamingDevicesPanel.cs`), `AdaptiveWorkspaceUiContractTests`, `ProfileSurfaceUiContractTests`, `ProgressiveDisclosureUiContractTests`, `AccessibilityTests`, `IconOnlyButtonAccessibilityTests`, `UiLocalizationHookTests`, `CodeBuiltUiLocalizationTests`. Deleting a file they open makes them throw, not fail cleanly: update them in the same commit, and re-run localization/accessibility scanners because they iterate every View file.
- Linked source files: `PaRuX.Server.Tests.csproj` compiles App files directly (`PadAction*`, `PadLogicalButtons`, `LauncherConfigImporter`, `ControllerLiveModel`, `RawMappingWizardModel`, `RawPadEdgeTranslator`, `SteamGridDbGallery`, `GameArtStore`, `SteamAchievementsText`, `AchievementSessionWatcher`, `LibraryThemeState`, `VoiceGamingTransition`) and `tools/PaRuX.VirtualPadSim/PadScript.cs`. Remove the include in the same commit as the file or the whole test project stops compiling.
- Hub dependencies that look removable but are not: `GameTitleStore`, `EmulatorRegistry`, `GameLaunchProfile`, `ExternalGameLibraryStore`, `LocalGameChatPolicy` (see C5). `ConsoleDisplaySelectionPolicy` lives in the Console-named contract file but serves Playnite and window placement.
- Inventories and snapshots: `GamingHardOffProcessLaunchInventoryTests` with two allow-list text files (removing a launch site requires editing the list), LAN/auth route tables in `BrowserOwnerAndLanHardeningTests`, OpenAPI/endpoint coverage tests.
- Security invariants to re-verify after each server-side step: Gaming hard-off still blocks speech/retrieval/discovery/web watch/media when a game runs; kept mod endpoints stay owner-authenticated and LAN-gated; path safety (`GamingLocalFileSafetyPolicy`, `PngAssetGuard`, `GameModProcessGuard`) untouched.
- UI regressions only visible in the real app: re-hosted dialogs need `XamlRoot` from the new page (all dialogs take `XamlRoot` as a parameter, low risk); narrow-width layout of the new page; keyboard/pad focus order (PadActions still drives it).
- Parallel writers: this map is a snapshot; step 0 must re-run the file classification (appendix A) before step 1.
- Not verified here: no build, no test run and no desktop UI was used for this document; all numbers come from file listing, `wc` and grep.

## 11. Estimate

| Step | Effort |
|---|---|
| 0 baseline | 0.5 day |
| 1 additive Games page + mod entry extraction | 2 days |
| 2 flip default | 0.5 day |
| 3 Console/TV shell removal | 1.5 days |
| 4 library views + App services | 2.5 days |
| 5 server extras (achievements, discover, wishlist, extensions, collections) | 1.5 days |
| 6 settings/profile/nav cleanup | 1.5 days |
| 7 DECIDE bucket (if removal chosen) | 2 days (0.5 if kept and re-homed) |
| 8 endpoint trim | 1 day |
| 9 docs, full suites, real-data smoke | 1 day |
| Total | about 13 to 14 working days sequential, about 8 to 9 with steps 5 and 7 in parallel worktrees |

Full Server suite is about 14.8k tests (about 41 minutes), so run it once at the end of steps 4, 6 and 9, and use filters in between.

## Appendix A. Classification rules

First matching rule wins (regexes on repository paths over `git ls-files src tests`). Shared Gaming words in model-hub, streaming-reader or Ollama files (for example `ModelDownload*`, `ExplicitApiStreamingReader`) were deliberately excluded. Rule order: EDIT (tests that only touch the tab or scan Views) > hub dependencies (KEEP) > mod dialogs/QuickResume/paused banner (MOVE) > mods backend (KEEP) > downloads UI (MOVE) > downloads/installs/stores (KEEP) > Playnite import (MOVE) > Playnite (KEEP) > save backups (KEEP, panel MOVE) > pad/streaming/sign-in (KEEP) > virtual pad/HidHide/displays/guide (DECIDE) > Console/TV (REMOVE) > handoff/hard-off/manual launch (KEEP) > library UI and extras (REMOVE).

## Appendix B. Per-file table

Columns: class, path, group id (A downloads/installs, B mods, C handoff/Playnite/pad/streaming/sign-in, D saves/import, E decide bucket, F console shell, G library UI/extras, H edit-only tests). Sorted REMOVE, MOVE, DECIDE, EDIT, KEEP.

| Class | Path | Group |
|---|---|---|
| REMOVE | `src/PaRuX.App/MainWindow.AchievementToasts.cs` | G |
| REMOVE | `src/PaRuX.App/MainWindow.ConsoleMode.cs` | F |
| REMOVE | `src/PaRuX.App/MainWindow.ConsolePad.cs` | F |
| REMOVE | `src/PaRuX.App/MainWindow.ConsoleStartup.cs` | F |
| REMOVE | `src/PaRuX.App/MainWindow.GameExtensions.cs` | G |
| REMOVE | `src/PaRuX.App/MainWindow.OnScreenKeyboard.cs` | F |
| REMOVE | `src/PaRuX.App/Services/AchievementSessionWatcher.cs` | G |
| REMOVE | `src/PaRuX.App/Services/AchievementToastService.cs` | G |
| REMOVE | `src/PaRuX.App/Services/ConsoleRawPadNavigator.cs` | F |
| REMOVE | `src/PaRuX.App/Services/GameArtStore.cs` | G |
| REMOVE | `src/PaRuX.App/Services/GameMetadataService.cs` | G |
| REMOVE | `src/PaRuX.App/Services/GamingLibraryClient.cs` | G |
| REMOVE | `src/PaRuX.App/Services/LibraryThemeState.cs` | G |
| REMOVE | `src/PaRuX.App/Services/SteamAchievementsText.cs` | G |
| REMOVE | `src/PaRuX.App/Services/SteamGridDbArtworkPicker.cs` | G |
| REMOVE | `src/PaRuX.App/Services/SteamGridDbGallery.cs` | G |
| REMOVE | `src/PaRuX.App/Services/SteamGridDbKeyStore.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GameControllerProfileSection.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GameDetailPanel.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GameHeroCard.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GameHooksSection.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GameLaunchProfileSection.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GameMotion.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GameShelfFeed.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GameShortcutsSection.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GameSideFilterPanel.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GameTileGrid.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GamingHomeView.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GamingWishlistDialog.cs` | G |
| REMOVE | `src/PaRuX.App/Views/GroupedGameLibrary.cs` | G |
| REMOVE | `src/PaRuX.App/Views/IGameTileSurface.cs` | G |
| REMOVE | `src/PaRuX.App/Views/ManualGameLibraryView.Console.cs` | F |
| REMOVE | `src/PaRuX.App/Views/OnScreenKeyboardOverlay.cs` | F |
| REMOVE | `src/PaRuX.App/Views/SettingsView.RetroAchievements.cs` | G |
| REMOVE | `src/PaRuX.App/Views/SteamGridDbArtworkDialog.cs` | G |
| REMOVE | `src/PaRuX.Contracts/ConsoleNavigationPolicy.cs` | F |
| REMOVE | `src/PaRuX.Contracts/ConsolePadOverlayPolicy.cs` | F |
| REMOVE | `src/PaRuX.Contracts/ConsoleStartupPolicy.cs` | F |
| REMOVE | `src/PaRuX.Contracts/ConsoleTextEntry.cs` | F |
| REMOVE | `src/PaRuX.Contracts/GameDiscoverContracts.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameDiscoveryContracts.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameExtensionManifest.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameFilterPresets.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameHeroSyncPolicy.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameLaunchRequestChannel.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameLibraryHealthPolicy.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameLibraryHome.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameLibraryPageWindow.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameLibraryQuery.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameLibraryTools.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameLibraryViewModel.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameMetadataContracts.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameMetadataIgdb.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameShelfFeedModel.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameShortcuts.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameSmartCollections.cs` | G |
| REMOVE | `src/PaRuX.Contracts/GameTileListModel.cs` | G |
| REMOVE | `src/PaRuX.Contracts/LocalWishlistContracts.cs` | G |
| REMOVE | `src/PaRuX.Contracts/LocalWishlistManualBatchPolicy.cs` | G |
| REMOVE | `src/PaRuX.Contracts/OnScreenKeyboardModel.cs` | F |
| REMOVE | `src/PaRuX.Contracts/RetroAchievementsContracts.cs` | G |
| REMOVE | `src/PaRuX.Contracts/SteamAchievementContracts.cs` | G |
| REMOVE | `src/PaRuX.Server/Orchestration/Gaming/Discover/GameDiscoverEndpoints.cs` | G |
| REMOVE | `src/PaRuX.Server/Orchestration/Gaming/Discover/GameDiscoverRecommender.cs` | G |
| REMOVE | `src/PaRuX.Server/Orchestration/Gaming/Discover/GameDiscoverService.cs` | G |
| REMOVE | `src/PaRuX.Server/Orchestration/Gaming/Discover/GameDiscoverSettings.cs` | G |
| REMOVE | `src/PaRuX.Server/Orchestration/Gaming/Discover/IGameCatalogProvider.cs` | G |
| REMOVE | `src/PaRuX.Server/Orchestration/Gaming/Discover/SteamStoreCatalogProvider.cs` | G |
| REMOVE | `src/PaRuX.Server/Orchestration/Gaming/GameExtensionStore.cs` | G |
| REMOVE | `src/PaRuX.Server/Orchestration/Gaming/GameSmartCollectionStore.cs` | G |
| REMOVE | `src/PaRuX.Server/Orchestration/Gaming/LocalWishlistPolicy.cs` | G |
| REMOVE | `src/PaRuX.Server/Orchestration/Gaming/LocalWishlistStore.cs` | G |
| REMOVE | `src/PaRuX.Server/RetroAchievementsService.cs` | G |
| REMOVE | `src/PaRuX.Server/SteamAchievementsService.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/ConsoleNavigationPolicyTests.cs` | F |
| REMOVE | `tests/PaRuX.Server.Tests/ConsolePadOverlayPolicyTests.cs` | F |
| REMOVE | `tests/PaRuX.Server.Tests/ConsoleStartupPolicyTests.cs` | F |
| REMOVE | `tests/PaRuX.Server.Tests/ConsoleTextEntryTests.cs` | F |
| REMOVE | `tests/PaRuX.Server.Tests/GameArtStoreTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameDiscoverProviderTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameDiscoverRecommenderTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameExtensionTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameFilterPresetTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameHeroSyncPolicyTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameLaunchHookTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameLaunchProfileTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameLibraryGroupingTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameLibraryHealthPolicyTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameLibraryHomeTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameLibraryPageWindowTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameLibraryQueryTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameLibraryToolsTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameMetadataParsingTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameShelfFeedModelTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameShortcutTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameSmartCollectionTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/GameTileListModelTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/LocalWishlistManualBatchPolicyTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/LocalWishlistPolicyTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/LocalWishlistStoreTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/OnScreenKeyboardModelTests.cs` | F |
| REMOVE | `tests/PaRuX.Server.Tests/RetroAchievementsTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/SteamAchievementsServiceTests.cs` | G |
| REMOVE | `tests/PaRuX.Server.Tests/SteamGridDbGalleryTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Infrastructure/ConsolePadSupport.cs` | F |
| REMOVE | `tests/PaRuX.UITests/Infrastructure/SyntheticGameLibrary.cs` | G |
| REMOVE | `tests/PaRuX.UITests/PageObjects/GamingSectionsPage.cs` | G |
| REMOVE | `tests/PaRuX.UITests/PageObjects/LocalOnlyAndGamingHonestyPage.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/AdvancedGameManagementUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/ConsoleAndLanguageUiTests.cs` | F |
| REMOVE | `tests/PaRuX.UITests/Tests/ConsoleModeUiTests.cs` | F |
| REMOVE | `tests/PaRuX.UITests/Tests/ConsolePadOperationUiTests.cs` | F |
| REMOVE | `tests/PaRuX.UITests/Tests/ConsoleStartupUiTests.cs` | F |
| REMOVE | `tests/PaRuX.UITests/Tests/GamingCompactTopUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/GamingDetailNarrowUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/GamingHomeCompetitiveUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/GamingHomeModesUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/GamingHomeUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/GamingLibraryCollectionsUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/GamingLibraryGroupingUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/GamingLibraryToolsUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/GamingLibraryVirtualizationUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/GamingShelfFeedUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/GamingVisualAuditUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/LocalOnlyAndGamingHonestyCoverageTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/OnScreenKeyboardUiTests.cs` | F |
| REMOVE | `tests/PaRuX.UITests/Tests/RealDevicesAndGamesUiTests.cs` | G |
| REMOVE | `tests/PaRuX.UITests/Tests/RealGameConsolePadTests.cs` | F |
| REMOVE | `tests/PaRuX.UITests/Tests/RealGameLaunchUiTests.cs` | G |
| MOVE | `src/PaRuX.App/Views/GameAssetModStudioDialog.cs` | B |
| MOVE | `src/PaRuX.App/Views/GameDownloadsPanel.cs` | A |
| MOVE | `src/PaRuX.App/Views/GameModDropInDialog.cs` | B |
| MOVE | `src/PaRuX.App/Views/GameSaveBackupPanel.cs` | D |
| MOVE | `src/PaRuX.App/Views/ManualGameLibraryView.Installer.cs` | A |
| MOVE | `src/PaRuX.App/Views/ManualGameLibraryView.Stores.cs` | A |
| MOVE | `src/PaRuX.App/Views/MinecraftSkinStudioDialog.cs` | B |
| MOVE | `src/PaRuX.App/Views/PausedGameBanner.cs` | B |
| MOVE | `src/PaRuX.App/Views/PlayniteImportPanel.cs` | D |
| MOVE | `src/PaRuX.App/Views/QuickResumePanel.cs` | B |
| MOVE | `src/PaRuX.App/Views/TemporaryGameModsDialog.cs` | B |
| MOVE | `src/PaRuX.Contracts/PlayniteImportContracts.cs` | D |
| MOVE | `src/PaRuX.Server/Orchestration/Gaming/PlayniteImportEndpoints.cs` | D |
| MOVE | `src/PaRuX.Server/Orchestration/Gaming/PlayniteLibraryReader.cs` | D |
| MOVE | `tests/PaRuX.Server.Tests/PlayniteLibraryReaderTests.cs` | D |
| MOVE | `tests/PaRuX.UITests/Tests/GamingDownloadsAndDevicesUiTests.cs` | A |
| MOVE | `tests/PaRuX.UITests/Tests/PausedGameBannerUiTests.cs` | B |
| MOVE | `tests/PaRuX.UITests/Tests/PlayniteImportUiTests.cs` | D |
| MOVE | `tests/PaRuX.UITests/Tests/QuickResumeAndMetadataUiTests.cs` | B |
| MOVE | `tests/PaRuX.UITests/Tests/SaveBackupSyncUiTests.cs` | D |
| DECIDE | `src/PaRuX.App/Services/RawPadEdgeTranslator.cs` | E |
| DECIDE | `src/PaRuX.App/Services/VirtualPadActivationController.cs` | E |
| DECIDE | `src/PaRuX.App/Services/WindowsRawGamepadSource.cs` | E |
| DECIDE | `src/PaRuX.App/Services/XInputShimBridge.cs` | E |
| DECIDE | `src/PaRuX.App/Views/GamingDevicesPanel.cs` | E |
| DECIDE | `src/PaRuX.Contracts/GamingControllerContracts.cs` | E |
| DECIDE | `src/PaRuX.Contracts/GamingControllerProfile.cs` | E |
| DECIDE | `src/PaRuX.Contracts/GamingDisplayChangeContracts.cs` | E |
| DECIDE | `src/PaRuX.Contracts/GamingDisplayContracts.cs` | E |
| DECIDE | `src/PaRuX.Contracts/HidHideHealthDto.cs` | E |
| DECIDE | `src/PaRuX.Contracts/LocalGameGuideContracts.cs` | E |
| DECIDE | `src/PaRuX.Contracts/LocalGameGuideSourcePicker.cs` | E |
| DECIDE | `src/PaRuX.Contracts/VirtualPadIsolationDto.cs` | E |
| DECIDE | `src/PaRuX.Contracts/XInputShimContracts.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/ControllerProfileStore.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/DisplayChangeJournal.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/DisplayModeApi.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/GameControllerProfileStore.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/GamingDisplayModeService.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/GamingDisplayTopologyService.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/ControllerProfileMath.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/InputProviders.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/NativeXInputProbe.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualControllerProbe.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualPad/GamePadProfileStore.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualPad/GenericRawPadMapping.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualPad/HidHideManager.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualPad/PadBinding.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualPad/PadFeel.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualPad/PadMappingPresets.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualPad/RawPadMappingStore.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualPad/SharedMemoryXInput.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualPad/ViGEmBusClient.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualPad/VirtualPadForwardingService.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualPad/VirtualPadForwardingSession.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/Providers/VirtualPad/Xbox360Report.cs` | E |
| DECIDE | `src/PaRuX.Server/Orchestration/Gaming/XInputShimInstaller.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/ControllerEmulatedScenarioTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/DisplayTopologySimulationTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/GameControllerProfileStoreTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/GamingDevicesHidingOptionalContractTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/GamingDisplayModeServiceTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/GamingDisplayTopologyTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/GenericRawPadMappingTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/LocalGameGuideEndpointTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/LocalGameGuideProcessGateTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/LocalGameGuideSourcePickerTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/NativeXInputProbeTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/PadFeelTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/PadMappingPresetsTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/RawPadEdgeTranslatorTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/RawPadLayoutMappingTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/RawPadMappingStoreTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/VirtualPadActivationTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/VirtualPadForwardingSessionTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/VirtualPadSimScriptTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/VirtualPadTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/VirtualPadUnhiddenForwardingTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/XInputShimInstallerTests.cs` | E |
| DECIDE | `tests/PaRuX.Server.Tests/XInputShimTests.cs` | E |
| EDIT | `tests/PaRuX.Server.Tests/GameStoreFirstRunSourceContractTests.cs` | H |
| EDIT | `tests/PaRuX.Server.Tests/GamingShelfAndHeroSourceContractTests.cs` | H |
| EDIT | `tests/PaRuX.Server.Tests/GrapheneGamingDesignRegressionTests.cs` | H |
| EDIT | `tests/PaRuX.UITests/Tests/ModeTabsUiTests.cs` | H |
| EDIT | `tests/PaRuX.UITests/Tests/SetupProfileRuntimeUiTests.cs` | H |
| EDIT | `tests/PaRuX.UITests/Tests/SimulatedExternalsUiTests.cs` | H |
| KEEP | `src/PaRuX.App/MainWindow.PadActions.cs` | C |
| KEEP | `src/PaRuX.App/MainWindow.PlayniteLauncher.cs` | C |
| KEEP | `src/PaRuX.App/MainWindow.PlayniteStartup.cs` | C |
| KEEP | `src/PaRuX.App/MainWindow.SignInApps.cs` | C |
| KEEP | `src/PaRuX.App/Services/GamingCallResult.cs` | C |
| KEEP | `src/PaRuX.App/Services/GamingManager.cs` | C |
| KEEP | `src/PaRuX.App/Services/GamingOrchestratorClient.cs` | C |
| KEEP | `src/PaRuX.App/Services/GamingProfileEdit.cs` | C |
| KEEP | `src/PaRuX.App/Services/LauncherConfigImporter.cs` | C |
| KEEP | `src/PaRuX.App/Services/LauncherModifierKeys.cs` | C |
| KEEP | `src/PaRuX.App/Services/PadActionEngine.cs` | C |
| KEEP | `src/PaRuX.App/Services/PadActionListener.cs` | C |
| KEEP | `src/PaRuX.App/Services/PadActionModel.cs` | C |
| KEEP | `src/PaRuX.App/Services/PadActionPolicy.cs` | C |
| KEEP | `src/PaRuX.App/Services/PadActionPoller.cs` | C |
| KEEP | `src/PaRuX.App/Services/PadLogicalButtons.cs` | C |
| KEEP | `src/PaRuX.App/Services/PlayniteClient.cs` | C |
| KEEP | `src/PaRuX.App/Services/PlayniteLauncherShortcutService.cs` | C |
| KEEP | `src/PaRuX.App/Services/PlayniteSetupFlow.cs` | C |
| KEEP | `src/PaRuX.App/Services/SignInAppListClient.cs` | C |
| KEEP | `src/PaRuX.App/Services/VoiceGamingTransition.cs` | C |
| KEEP | `src/PaRuX.App/Views/ManualGameLibraryView.Cards.cs` | C |
| KEEP | `src/PaRuX.App/Views/ManualGameLibraryView.Launch.cs` | C |
| KEEP | `src/PaRuX.App/Views/ManualGameLibraryView.Session.cs` | C |
| KEEP | `src/PaRuX.App/Views/ManualGameLibraryView.cs` | C |
| KEEP | `src/PaRuX.App/Views/PlayniteLauncherSurface.cs` | C |
| KEEP | `src/PaRuX.App/Views/SettingsView.ConsoleDisplays.cs` | C |
| KEEP | `src/PaRuX.App/Views/SettingsView.PadButtons.cs` | C |
| KEEP | `src/PaRuX.App/Views/SettingsView.Playnite.cs` | C |
| KEEP | `src/PaRuX.App/Views/SettingsView.SignInApps.cs` | C |
| KEEP | `src/PaRuX.Contracts/ConsoleDisplayPreferences.cs` | C |
| KEEP | `src/PaRuX.Contracts/GameAssetModContracts.cs` | B |
| KEEP | `src/PaRuX.Contracts/GameDownloadContracts.cs` | A |
| KEEP | `src/PaRuX.Contracts/GameInstallerInspectionContracts.cs` | A |
| KEEP | `src/PaRuX.Contracts/GameLaunchHookContracts.cs` | C |
| KEEP | `src/PaRuX.Contracts/GameLaunchProfileContracts.cs` | C |
| KEEP | `src/PaRuX.Contracts/GameManagementContracts.cs` | C |
| KEEP | `src/PaRuX.Contracts/GameModDropInContracts.cs` | B |
| KEEP | `src/PaRuX.Contracts/GamePackageStageContracts.cs` | A |
| KEEP | `src/PaRuX.Contracts/GameSaveBackupContracts.cs` | D |
| KEEP | `src/PaRuX.Contracts/GameSaveHookPresenter.cs` | D |
| KEEP | `src/PaRuX.Contracts/GameSkinStudioContracts.cs` | B |
| KEEP | `src/PaRuX.Contracts/GameStoreLaunchIdPolicy.cs` | A |
| KEEP | `src/PaRuX.Contracts/GameTemporaryModContracts.cs` | C |
| KEEP | `src/PaRuX.Contracts/GameTitleContracts.cs` | C |
| KEEP | `src/PaRuX.Contracts/GamingReleaseVerificationText.cs` | C |
| KEEP | `src/PaRuX.Contracts/ManualGameContracts.cs` | C |
| KEEP | `src/PaRuX.Contracts/ManualGameLaunchContracts.cs` | C |
| KEEP | `src/PaRuX.Contracts/PlayniteIntegrationContracts.cs` | C |
| KEEP | `src/PaRuX.Contracts/PlayniteLauncherContracts.cs` | C |
| KEEP | `src/PaRuX.Contracts/QuickResumeContracts.cs` | C |
| KEEP | `src/PaRuX.Contracts/SignInAppListApiContracts.cs` | C |
| KEEP | `src/PaRuX.Contracts/SignInAppListContracts.cs` | C |
| KEEP | `src/PaRuX.Contracts/VerifiedGameReturnSignal.cs` | C |
| KEEP | `src/PaRuX.Server/GamingChildScope.cs` | C |
| KEEP | `src/PaRuX.Server/GamingGuardService.cs` | C |
| KEEP | `src/PaRuX.Server/GamingHardOff.cs` | C |
| KEEP | `src/PaRuX.Server/GamingHardOffWatch.cs` | C |
| KEEP | `src/PaRuX.Server/GamingShutdownRetry.cs` | C |
| KEEP | `src/PaRuX.Server/GamingTextMemory.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Downloads/EmbeddedTorrentTransport.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Downloads/ExternalTorrentClientTransport.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Downloads/GameDownloadEndpoints.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Downloads/GameDownloadManager.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Downloads/GameDownloadSourceValidator.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Downloads/GameDownloadTransport.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Downloads/HttpGameDownloadTransport.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Downloads/NativeTorrentRecoveryStore.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Downloads/PortableGameInstallService.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Emulators/EmulatorCommandBuilder.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Emulators/EmulatorDiscovery.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Emulators/EmulatorEndpoints.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Emulators/EmulatorRecipeValidator.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Emulators/EmulatorRegistry.Playnite.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Emulators/EmulatorRegistry.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Emulators/SwitchTitleFiles.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/EpicGameDiscoveryProvider.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/EpicGameLaunchProtocol.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/ExternalGameLibraryStore.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/ExternalLibraryPricePolicies.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/ExtraStoreDiscovery.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameAssetModEndpoints.cs` | B |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameAssetModService.cs` | B |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameFolderInspector.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameIniSettingPatch.cs` | B |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameInstallerInspectionService.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameLaunchHooks.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameLaunchProfile.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameLaunchProfileEndpoints.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameModDropInService.cs` | B |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameModProcessGuard.cs` | B |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GamePackageStageService.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameSaveBackupConflicts.cs` | D |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameSaveBackupEndpoints.cs` | D |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameSaveBackupService.cs` | D |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameSaveBackupSync.cs` | D |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameSaveLocationHints.cs` | D |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameSaveRestore.cs` | D |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameSkinStudioEndpoints.cs` | B |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameSkinStudioService.cs` | B |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameSuspendControllerPolicies.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameSuspendDebouncePolicy.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameTitleCatalog.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameTitleEndpoints.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameTitleReadCache.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GameTitleStore.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GamingEndpoints.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GamingLocalFileSafetyPolicy.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GamingProfileStore.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GamingSessionRecord.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GamingVramReleaseVerifier.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GogGalaxyLocator.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/GogGameDiscoveryProvider.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/LauncherPlayTimeTracker.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/LocalGameChatPolicy.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/ManualGameLibrary.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/ManualGameSessionService.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/MinecraftSkinUvRestyler.cs` | B |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/OwnedGameChildExitGuard.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/OwnedGameWindowPlacementService.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Playnite/PlayniteEndpoints.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Playnite/PlayniteGameSessionTracker.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Playnite/PlayniteInstaller.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Playnite/PlayniteLauncher.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Playnite/PlayniteLocator.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Playnite/PlayniteProcessSource.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Playnite/PlayniteSessionService.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Playnite/PlayniteSettingsStore.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/PngAssetGuard.cs` | B |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Providers/GamingInputSafetyPolicy.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Providers/IGamingProviders.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Providers/LauncherProviders.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Providers/SuspendProviders.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/Providers/TriggerProviders.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/QuickResumeProcessTree.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/QuickResumeService.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/SignInAppListEndpoints.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/SignInAppListRunner.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/SignInAppListService.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/SignInAppListStore.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/SteamGameDiscoveryProvider.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/SteamInstallLocator.cs` | A |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/StreamingEndpoints.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/StreamingModeService.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/TemporaryGameModEndpoints.cs` | B |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/TemporaryGameModRecoveryHostedService.cs` | B |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/TemporaryGameModService.cs` | B |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/WindowsStreamingDisplayController.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/WindowsStreamingHost.cs` | C |
| KEEP | `src/PaRuX.Server/Orchestration/Gaming/WorkloadOrchestrator.cs` | C |
| KEEP | `src/PaRuX.Server/SpeechUnavailableDuringGamingException.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/EmulatorDefinitionExhaustiveTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/EmulatorDiscoverySinglePassTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/EmulatorEden2Tests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/EmulatorManagerTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/EmulatorRegistryAdditionsTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/EpicGameDiscoveryTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/EpicGameLaunchProtocolTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/ExternalGameLibraryStoreTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/ExtraStoreDiscoveryTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/FirstPartyGamingDefaultsTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GameAssetModServiceTests.cs` | B |
| KEEP | `tests/PaRuX.Server.Tests/GameCompletionStatusTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GameDownloadManagerTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/GameDownloadTorrentTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/GameFolderInspectorTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/GameInstallerInspectionTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/GameModDropInErrorMappingTests.cs` | B |
| KEEP | `tests/PaRuX.Server.Tests/GameModDropInServiceTests.cs` | B |
| KEEP | `tests/PaRuX.Server.Tests/GameModProcessGuardTests.cs` | B |
| KEEP | `tests/PaRuX.Server.Tests/GamePackageStageTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/GameProcessSelectionTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GameSaveBackupServiceTests.cs` | D |
| KEEP | `tests/PaRuX.Server.Tests/GameSaveBackupSyncTests.cs` | D |
| KEEP | `tests/PaRuX.Server.Tests/GameSaveHookPresenterAndIgdbTests.cs` | D |
| KEEP | `tests/PaRuX.Server.Tests/GameSaveLocationHintsTests.cs` | D |
| KEEP | `tests/PaRuX.Server.Tests/GameSaveRestoreTests.cs` | D |
| KEEP | `tests/PaRuX.Server.Tests/GameSaveSyncConflictTests.cs` | D |
| KEEP | `tests/PaRuX.Server.Tests/GameSkinStudioServiceTests.cs` | B |
| KEEP | `tests/PaRuX.Server.Tests/GameStoreLaunchIdPolicyTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/GameSuspendControllerPoliciesTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GameSuspendDebouncePolicyTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GameTitleCatalogTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GameTitleLaunchPolicyTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GameTitleLibraryFeatureTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingActionTokenTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingCallResultTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingChildScopeHardOffTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingDiscoveryHardOffTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingDownloadPauseTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingGuardShutdownRetryTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingHardOffAppProcessLaunchAllowList.txt` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingHardOffProcessLaunchAllowList.txt` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingHardOffProcessLaunchInventoryTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingHardOffTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingInputSafetyPolicyTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingLauncherSafetyTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingLocalFileSafetyPolicyTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingMidRunStopTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingNoContentTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingProfileEditTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingProfilePersistenceTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingProviderInjectionTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingReleaseHandoffGapTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingReleaseVerificationTextTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingReopenRecoveryTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingSessionRecordTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingShutdownRetryTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingStoreLaunchAdmissionTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingTransitionBenchmarkTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingTransitionConcurrencyTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingTransitionIntegrationTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingVramMeasurementTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GamingVramReleaseVerificationTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/GogGalaxyLocatorTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/GogGameDiscoveryTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/LauncherConfigImporterTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/LauncherPlayTimeTrackerTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/LocalGameChatPolicyTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/ManualGameElevationRegressionTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/ManualGameLaunchTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/ManualGameLibraryTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/MinecraftSkinUvRestylerTests.cs` | B |
| KEEP | `tests/PaRuX.Server.Tests/OwnedGameChildExitGuardTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/OwnedGameProcessTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/OwnedGameWindowPlacementPolicyTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/PadActionTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/PlayniteComponentTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/PlayniteEmulatorDefinitionsTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/PlayniteGameSessionTrackerTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/PlayniteLauncherSurfaceTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/PlayniteRealLibraryProbe.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/PlayniteSessionServiceTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/PortableGameInstallServiceTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/QuickResumeRecoveryTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/QuickResumeTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/RealGamesFolderProbe.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/SignInAppListPersistenceTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/SignInAppListTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/SpeechGamingHardOffTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/SteamGameDiscoveryTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/SteamInstallLocatorTests.cs` | A |
| KEEP | `tests/PaRuX.Server.Tests/StreamingModeIntegrationTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/StreamingModeServiceTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/TemporaryGameModServiceTests.cs` | B |
| KEEP | `tests/PaRuX.Server.Tests/VerifiedGameReturnSignalTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/VoiceGamingTransitionTests.cs` | C |
| KEEP | `tests/PaRuX.Server.Tests/WorkloadOrchestratorTests.cs` | C |
| KEEP | `tests/PaRuX.UITests/Tests/GamingSectionsAndEmulatorUiTests.cs` | C |
| KEEP | `tests/PaRuX.UITests/Tests/ManualGameLibraryUiTests.cs` | C |
