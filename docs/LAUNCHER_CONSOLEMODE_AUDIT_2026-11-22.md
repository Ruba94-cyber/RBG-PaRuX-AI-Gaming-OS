# Owner launcher audit: `ConsoleMode.exe` (2026-11-22)

Read-only audit of the small desktop launcher the owner built to coordinate Playnite. Nothing was run, modified or deleted; the original EXE, scripts and config were only read.

## Identification

| Item | Value |
|---|---|
| Desktop shortcut | `Console Mode.lnk` -> `%USERPROFILE%\Documents\Login Script\ConsoleMode.exe` (no args, working dir = that folder) |
| Other shortcut | `Locally Uncensored (CUDA).lnk` -> hidden PowerShell `Start-LU-CUDA.ps1` (out of scope here) |
| Kind | PyInstaller one-file bundle of Python 3.13 + tkinter, ~11.6 MB, unsigned, no version metadata |
| SHA-256 (2026-11-22 09:20) | `F7001C8BCE8B94E5F5B0205F8F5F38D885E09599BB276C8271440705E82F326A` |
| Source | Present: `console-mode-src\console_mode.py` (~3,000 lines) plus `ConsoleMode.spec`; a sibling `StadiaConsoleShortcuts.exe` handles the pad's Stadia-button chords |
| Config / logs | `console-config.json`, `DelayedApps.json`, `GamingMode.state.json`, `ConsoleMode.log`, `StartupTiming.log` |
| Companion scripts | `Start-Playnite-eGPU.ps1` (entry, eGPU + TV detection), `Start/Stop-GamingMode.ps1`, `Start/Stop-StreamingMode.ps1`, `Streaming-Common.ps1`, `Display-Tool.ps1`, `Start-DelayedApps.ps1`, `Move-StartupApps.ps1`, `Apply-PlayniteTuning.ps1`, `Disable-GameBarPolicy.ps1` |

## Behaviours found and PaRuX status

| Launcher behaviour | PaRuX status (verify in code before closing) |
|---|---|
| Choose Console vs Desktop at logon (eGPU present, named TV connected, `--settings/--streaming` args, Shift held) | Settings > Gaming > Console / TV start-in-TV-mode exists; TV matching by monitor code; see plan checkpoint 2026-11-21 |
| Start Playnite Fullscreen on the eGPU/TV | Not needed: PaRuX Console presenter replaces Playnite Fullscreen |
| Pad Stadia button: short = freeze Playnite/game (Nyrna), long = desktop mode, assistant = voice assistant, capture = overlay (`alt+r`) | Freeze/suspend policy exists (`GameAutoSuspendPolicy`); configurable pad button actions exist; **overlay hotkey and assistant-voice hand-off to PaRuX chat: verify/MISSING** |
| Streaming mode (Sunshine service, display test, stale-test recovery, paired-device names) | Sunshine presence handled in 6 files; **full start/stop streaming-mode orchestration with display restore: PARTIAL, verify** |
| Delayed logon apps (`DelayedApps.json`: launch spacing, skip in console, appx by AUMID) | **MISSING in PaRuX** (no source file matches) |
| Game Bar / Game DVR policy off | **MISSING** by design: it is a Windows setting change and needs explicit owner consent plus a restore path |
| Startup apps move/disable with registry backups | **MISSING**; same consent/rollback constraint as above |
| Gaming Mode (power plan save/restore, state file) | Workload Orchestrator + display/power profiles exist; compare restore-after-crash semantics |

## Migration plan (not implemented yet)

1. One-time, opt-in importer for `console-config.json` (`tvNames`, `longPressMs`, `buttons.*`, `noFreezeGames`) mapped onto the existing PaRuX TV and pad-action settings. Keys without a native equivalent are reported, not silently dropped.
2. `DelayedApps.json` -> a native "After sign-in" app list with spacing, skip-in-Console and AUMID support, user-managed (never mutate third-party startup entries without consent and a backup).
3. After the migration is verified on the real PC, PaRuX no longer requires `ConsoleMode.exe`; the original files stay untouched.

## Caveats

- Only the source and logs were read; behaviours were not executed.
- The EXE is the owner's own build, but unsigned: do not redistribute it or embed it in PaRuX packages. Re-implement natively.
