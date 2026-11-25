# Reference launcher audit (2026-11-22, updated 2026-11-25) - historical, internal

Read-only audit of a small, self-made desktop launcher that coordinated Playnite on one PC. It is kept only as a historical comparison of behaviours. It prescribes NO public feature: nothing of that launcher is copied, and no format, file name, path or device name of it is supported by the product. Nothing was run or modified; examples in tests are synthetic.

Status words follow the repository contract: DONE / PARTIAL / MISSING. "By design" means PaRuX deliberately does not do it (consent, USER_MANAGED, Gaming hard-off).

## Behaviour -> PaRuX (generic equivalents)

| # | Reference behaviour | PaRuX equivalent | Status |
|---|---|---|---|
| 1 | At sign-in: Console only if a graphics card AND a TV are present (bounded waits), otherwise desktop; any error falls back to the quiet desktop | Optional sign-in entry (`--logon`, one HKCU Run value, restored exactly when turned off) applying `LogonStartupPolicy`: chosen TV(s) from the monitor history (bounded probe that confirms absence twice), optional graphics-card name rule, owner fallback, desktop side minimised. Never needs a TV or monitor | DONE (code + pure tests; real sign-in run not verified) |
| 2 | Explicit argument wins; Ctrl = settings, Shift = streaming, Ctrl+Shift = settings | `PlayniteLauncherModeResolver` + `LauncherModifierKeys`; `--logon-force` skips the TV/GPU wait, `--no-start` opens the desktop side | DONE |
| 3 | Start a controller helper, then Playnite Fullscreen (single instance) | "Support apps": programs the owner chose (USER_MANAGED), started with Console, optional stop, bounded wait, per-app timing, optional "replace an older running copy"; Playnite Fullscreen via the existing optional Playnite flow | DONE (fakes tested; not run against real helpers) |
| 4 | Helper download | Catalog of known helpers: download only with a pinned official https URL, version, SHA-256 and size, after explicit consent, into PaRuX's own folder (PARUX_MANAGED, removable). Entries whose source cannot be pinned stay "Locate" only | DONE (fake HTTP; no real download in tests) |
| 5 | Wait for Playnite, then start background apps spaced apart; skip some in Console | After-sign-in apps list (opt-in, `SkipInConsole`, spacing, ready timeout) | DONE |
| 6 | Pad Guide/Assistant/Capture short and long press | `PadActionEngine` tap/hold bindings, `PadLogicalButtons`, owner-configurable hold time | DONE (any pad) |
| 7 | Per-game exclusions from pausing | "Games never paused" list (adds a refusal only; a paused game can always be resumed; anti-cheat refusal unchanged) | DONE |
| 8 | Pad as keyboard/mouse in desktop mode | PaRuX never injects keyboard/mouse input from a pad | MISSING (by design) |
| 9 | Streaming mode: isolate the display, edit the streaming host config, start it, roll back | `StreamingModeService`: consent flags, write-ahead journal, starts only a host PaRuX owns, never edits its config, never isolates displays | PARTIAL (by design) |
| 10 | Gaming mode: power plan, boost, kill background processes | `PowerProfileService` (opt-in, existing plans, journal, restore). PaRuX never kills user processes | DONE for the plan; MISSING (by design) for process killing |
| 11 | Game Bar / capture policy off | `GameBarSuppressionService` (HKCU, journal, restore; consent) | PARTIAL (fakes only on a real Console session) |
| 12 | Startup apps moved/disabled | Not done by PaRuX; third-party startup entries stay USER_MANAGED | MISSING (by design) |
| 13 | Peripheral vendor services started when a USB device is present | Vendor-specific, out of scope | MISSING (out of scope) |
| 14 | Start-up timing log | `StartupTiming.log` in PaRuXData: `date time +elapsed-ms phase`, elapsed from a monotonic stopwatch, trimmed to the newest 500 lines past 256 KB | DONE |
| 15 | Dry run (`--dry-run`) | Settings > Start at sign-in > "Preview what would start": the exact decision and steps, nothing started or changed | DONE |
| 16 | Single instance; second launch forwards its request | `App.xaml.cs` single-instance forwarding | DONE |

## Removed from the product (2026-11-25)

The earlier importers for that launcher's own config files (pad buttons, TV names, delayed-apps list) and their Settings buttons were removed: nobody else has such files, so they were dead code. Adapting PaRuX to one person's existing files is done locally, outside the repository.
