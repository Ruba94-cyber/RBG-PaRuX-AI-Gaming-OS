# PaRuX controller → Playnite Desktop / Console validation (2026-11-23)

**Status: PARTIAL — source implemented, new unit tests authored, native tests NOT RUN. No main merge / release.**

## Owner direction

- **PaRuX owns controller configuration and buttons in both Desktop and Console.** Playnite is an optional user-managed frontend for game library / game presentation, not a replacement for PaRuX's controller settings.
- PaRuX keeps downloads/install/staging, mod management including AI-generated changes **beyond skins**, safety preview, AI ↔ Gaming resource handoff, streaming and sign-in apps.
- Do not modify the owner's legacy ConsoleMode launcher or Playnite library/config without explicit and scoped consent.

## Added in this branch

- Stable persisted controller action keys `playniteConsole` and `playniteDesktop` in `PadActionCatalog`, independently assignable to a controller's button tap or hold via the existing Settings > Controller buttons UI.
- Controller commands route to `ShowPlayniteLauncher(PlayniteLauncherAction.Console|Desktop, gamepad: true, fromRequest: true)` on PaRuX's UI thread. The existing launcher, authenticated Core routes and optional component setup resolve mode, TV fallback, process ownership and any start refusal. No direct process start, global input injection, gamepad interception or Playnite configuration write was added.
- If Gaming hard-off is active, an explicit new-frontend launch action is denied with EN/IT guidance: it cannot steal game focus. The owner-triggered PaRuX menu remains available.
- Existing action IDs/keys and Stadia defaults remain unchanged. Other controllers can be mapped through existing HID/XInput presets and learning UI, but physical compatibility is not proven merely by these tests.

## Existing game-management capabilities retained (not newly certified)

- Download backend: `GameDownloadManager` and owner-only `GameDownloadEndpoints`; downloaded installers must be inspected and confirmed, staged ZIPs verified. Nothing in this PR adds piracy sources or bypasses store licensing.
- AI mod backend: `TemporaryGameModService` previews and hash-checks offline, PaRuX-managed portable .ini/.cfg/.json/.txt changes; requires an exact paused process and separate owner approval; restore and conflict safeguards. This is **already more than skins** but limited to supported text configs, and not a general binary game patcher.
- Asset mod backend: `GameAssetModService` can generate a reviewed PNG overlay ZIP; deployment depends on a compatible mod loader and separate approval. No unsupported claim that every game accepts such an overlay.
- Continue toward game-specific adapters for approved JSON/INI settings, asset/package formats, scripts where a trusted loader explicitly supports them, and reproducible backup/rollback. **No arbitrary .exe patching, memory injection, anti-cheat bypass or unreviewed file execution.**

## Verification gates to complete on Windows 11 / .NET 10

1. Run `dotnet build PaRuX.sln -c Release -p:Platform=x64` on the **exact branch tip**.
2. `dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj -c Release --filter "FullyQualifiedName~PadActionTests|FullyQualifiedName~PlayniteLauncherSurfaceTests|FullyQualifiedName~PlayniteComponentTests"`.
3. Full Server suite with `--blame-hang --blame-hang-timeout 4m` to expose the known hanging tests; then exclusive FlaUI UI smoke/regression with EN/IT, high contrast and TV DPI.
4. On real physical devices (Xbox XInput, Stadia HID, 8BitDo HID/virtual XInput): bind tap and hold to each new action, verify opening Playnite Fullscreen and Desktop by controller only, no duplicate/hung dialog and no foreground-stealing during a game; test disconnected/reconnected pads. Confirm that the in-game inputs still reach the **game** rather than being swallowed by PaRuX.
5. Playnite not installed/skipped, missing TV / invalid exe / already running: show localized actionable refusal or safe fallback. Test game launch/close + return-to-AI and VRAM release with the actual RTX 5080 configuration.
6. Download + mod E2E uses an owner-approved, legally obtained **synthetic or test game**, a supported loader and clean user data; preview, consent, apply, restart, recovery and conflict handling. No real game folders/saves modified by tests without consent.
7. Review product UX after native Gaming removal: relabel the old `gamingLibrary` action and keep an obvious **Games tools** entry for downloads, mods, backups and install. That removal is a separate verified migration; do not silently break existing bindings.
8. Run `tools/check-public-safety.ps1` locally and independent code review. Measure game input latency and PaRuX overhead before any performance claims.

This remote GitHub editing session wrote source/tests only. It **could not run Windows build, xUnit, FlaUI or physical-pad/game/TV acceptance**. This PR is stacked on draft PR #12 to inherit its Playnite process-safety fixes; it must be rebased/retargeted after #12 is verified and merged.
