# Owner launcher audit: `ConsoleMode.exe` (2026-11-22, re-verified against the source on 2026-11-24)

Read-only audit of the small desktop launcher the owner built to coordinate Playnite. Nothing was run, modified or deleted; the original EXE, scripts and config were only read. No file of the original is copied into this repository; the examples in tests are synthetic.

## Identification

| Item | Value |
|---|---|
| Kind | PyInstaller one-file bundle (Python 3 + tkinter), unsigned, owner's own build. Do not redistribute or embed it; PaRuX re-implements natively. |
| Entry | `console_entry.py` dispatches `--boot / --supervise / --delayed / --gaming-stop` (no UI, `boot_manager.py`) or the tkinter status/settings window (`console_mode.py`, ~3,000 lines). |
| Logon tasks | `--boot` (elevated, replaces the old Start-Playnite-eGPU script), `--boot --force` (on demand), `--delayed` (not elevated). |
| Not auditable here | `StadiaConsoleShortcuts.exe` (the pad-button bridge) has no source in the folder. Its behaviour is known only from `console-config.json` (schema) and the action labels in `console_mode.py`. |

## Real behaviours (source-verified) and PaRuX status

Status words follow the repository contract: DONE / PARTIAL / MISSING. "By design" means PaRuX deliberately does not do it (consent, USER_MANAGED, Gaming hard-off); it is not a gap to close silently.

| # | Original behaviour (verified in source) | PaRuX equivalent | Status |
|---|---|---|---|
| 1 | Logon: console only if the named eGPU AND a configured TV are active (waits 12 s / 15 s for each), otherwise desktop; on any error falls back to the quiet desktop profile | Start-in-TV when the chosen TV is connected, bounded probe that confirms absence twice (`ConsoleProbeStep`, `MainWindow.ConsoleStartup.cs`), owner-chosen fallback (normal / minimized). The eGPU is not a separate condition: a TV driven by it is simply connected or not. Any TV is chosen from the display list (no hard-coded names) | DONE (generalised) |
| 2 | Start mode override: `--console/--settings/--streaming` arguments win; otherwise Ctrl = settings, Shift = streaming, Ctrl+Shift = settings; keys sampled at process start plus a 1.5 s grace | `PlayniteLauncherModeResolver` + `LauncherModifierKeys.Sample` (same rules, injected clock, tested) | DONE |
| 3 | Start Playnite Fullscreen (above-normal priority at logon, single instance), start XOutput | Optional Playnite: `MainWindow.PlayniteStartup.cs` + `PlayniteLauncher.cs` (Fullscreen, single instance, display-aware, Desktop fallback, return watch). XOutput is not needed: PaRuX reads pads itself | DONE (priority boost and XOutput by design not replicated) |
| 4 | Wait for Playnite readiness, then start background apps spaced apart; skip some in console mode; AUMID apps; process-name "already running" probe | `SignInAppListService/Runner/Store` (opt-in, disabled until the owner enables it, validated paths/AUMIDs, `SkipInConsole`, spacing, ready timeout) and the importer of the legacy `DelayedApps.json` (`SignInAppListPolicy.ImportLegacyFile`). The 2026-11-22 version of this table called it MISSING: that was stale | DONE |
| 5 | Pad Stadia button: short = freeze/resume game, long (`longPressMs`) = other action, Assistant, Capture buttons | `PadActionEngine` tap/hold bindings per pad, `PadLogicalButtons` (Guide/Assistant/Capture resolved per pad family), `PauseResumeGame` (only a game PaRuX launched, never on the anti-cheat list), mapping wizard for any other pad. **New:** the hold time is now owner-configurable (300-3000 ms, was fixed 600 ms) | DONE (any pad) |
| 6 | `capture_assistant` chord (two buttons together) | Not supported; reported by the importer with the reason | MISSING (by design for now) |
| 7 | `noFreezeGames` per-game exclusions, `freezeMinSecondsBeforeAutoResume` | PaRuX pauses only games it launched and refuses on its anti-cheat list; no per-game list. Reported by the importer | PARTIAL |
| 8 | "Desktop mode" of the bridge: the pad becomes keyboard/mouse shortcuts (`desktopMode` map, Win/Alt+Tab/...), overlay hotkey `alt+r`, hotkey/run/OSK actions | PaRuX never injects keyboard/mouse input from a pad and never sends keystrokes to a game; Console navigation (TV mode table) and the on-screen keyboard are native. Reported by the importer | MISSING (by design) |
| 9 | Voice assistant (ChatGPT desktop via UI automation) | `MiniAgent` / `Dictation` pad actions: local mini agent and local push-to-talk (no cloud assistant) | DONE (local equivalent) |
| 10 | Importer for the legacy config | `LauncherConfigImporter`: preview, nothing applied until confirmed, source never modified. **New:** `longPressMs` is imported as the hold time; every unsupported key (`noFreezeGames`, `streaming`, `desktopMode`, chord, hotkey/run/OSK actions) is listed with its reason | DONE |
| 11 | Streaming mode: isolate the dummy display as the only active one at a set resolution, edit `sunshine.conf` for low latency (with backup), start the Sunshine service, verify, roll back everything on failure; stale display-test recovery | `StreamingModeService`: consent flags, resolution/refresh change through a write-ahead journal, starts only a Sunshine PaRuX owns, rollback and crash recovery at startup/shutdown. It never edits Sunshine's config and never isolates displays | PARTIAL (by design: Sunshine config is USER_MANAGED; display isolation is not implemented) |
| 12 | Gaming Mode: save the active power plan, activate a gaming plan + aggressive boost, state file, restore at Playnite exit, supervisor restarts the watch when Playnite relaunches, `--gaming-stop` | **New:** `PowerProfileService` (powrprof, no process launch): opt-in, existing plans only, explicit consent per apply, write-ahead journal, restore when Console ends / at shutdown / after a crash at next start, never overrides a plan the owner chose meanwhile, falls back to Balanced if the old plan is gone. Wired to Console/TV enter-exit and the PaRuX-started Playnite Fullscreen lifetime. Boost-mode tuning is not touched | DONE in code; real-PC effect NOT verified (see report) |
| 13 | Gaming Mode also kills OneDrive, Edge, Teams, Radeon software, Game Bar processes and restarts OneDrive afterwards | PaRuX never kills user processes (Gaming hard-off only stops PaRuX's own AI children) | MISSING (by design) |
| 14 | Game Bar / Game DVR policy off (HKLM policy), `GameBar` registry values | Windows setting change; not done. An opt-in with backup/restore is possible but is not implemented | MISSING (consent + restore path required) |
| 15 | Startup apps moved/disabled (`Move-StartupApps`, `Restore-StartupApps`, registry exports, manifest) | Not done; third-party startup entries stay USER_MANAGED. The PaRuX "after sign-in" list (row 4) is the supported alternative | MISSING (consent + backup required) |
| 16 | Peripheral vendor services started only when a USB device is present | Vendor-specific to the owner's PC | MISSING (out of scope) |
| 17 | Playnite tuning script (update checks, extension disable, Stadia HID disable, Aniki splash timing) with backups | PaRuX never edits Playnite's files except the explicit Aniki ReMake selection with backup/rollback (`/theme/select`, `/theme/rollback`) | PARTIAL (by design) |
| 18 | Logging: `ConsoleMode.log`, `StartupTiming.log`, `StreamingMode.log`, state files | PaRuX log + per-feature status/journals; no separate timing log | PARTIAL |
| 19 | Single instance; second launch forwards its request (`ConsoleMode.request`) | PaRuX single-instance forwarding of launcher arguments (`App.xaml.cs`) | DONE |

## What changed in this pass

- Configurable hold time for pad bindings (`PadActionSettings.HoldMsOverride`, Settings > Controller buttons), imported from `longPressMs`.
- Importer reports every unsupported legacy key with a reason.
- Opt-in Console power plan with a crash-safe restore point (`PowerProfileService`, routes `/api/v1/gaming/power-profile*`, Settings > Gaming).

## Caveats

- Only the source and logs were read; behaviours were not executed.
- No real Playnite / pad / Console-TV run was done for the new pieces; they are covered by pure unit tests with fakes.
