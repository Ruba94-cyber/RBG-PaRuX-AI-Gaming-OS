# Streaming / display gap table (2026-11-22)

Source of the requirement: the legacy launcher's Sunshine/Moonlight streaming mode (see `docs/LAUNCHER_CONSOLEMODE_AUDIT_2026-11-22.md` on main).
Status words: DONE / PARTIAL / MISSING / BLOCKED-EXTERNAL. File:line refers to this branch before the new files were added.

| Legacy capability | PaRuX today | Evidence | Status |
|---|---|---|---|
| Detect Sunshine (installed / running) | Probe by process name, known install paths, loopback-only status URL | `Gaming/Providers/TriggerProviders.cs:113-215` | DONE (detection only) |
| Sunshine as trigger for a Gaming profile | Preset `preset-remote-sunshine` | `Gaming/GamingProfileStore.cs:267-276`, `WorkloadOrchestrator.cs:859-862` | DONE |
| Remote input safety (no duplicate virtual pads) | Policy | `Providers/GamingInputSafetyPolicy.cs:22-30` | DONE |
| Start the Sunshine service on demand | None (only detection; `sunshine` is on a protected-process list, `WorkloadOrchestrator.cs:947`) | grep Sunshine in `src` | MISSING -> new `StreamingModeService` (consent-gated, owned process only) |
| Stop Sunshine afterwards | None | - | MISSING -> new (only a process PaRuX started) |
| Test/choose a display, apply resolution/refresh | Driver-tested, temporary, confirm-or-auto-revert, one change at a time | `Gaming/GamingDisplayModeService.cs:70-122` | DONE for resolution/refresh |
| Stale-test recovery after crash (resolution/refresh) | Single-record restore at startup | `GamingDisplayModeService.cs:198-237`, `Program.cs:4201` | DONE for that one flow |
| HDR toggle with restore | No HDR change or restore anywhere (class doc: "HDR, VRR and colour settings are not touched", `GamingDisplayModeService.cs:11`) | - | MISSING (journal supports `Hdr` kind; a Windows controller is not implemented) |
| Multi-change crash journal (several kinds, bounded, corrupt-safe) | Only one pending record, mode only | `GamingDisplayModeService.cs:17,25` | MISSING -> new `DisplayChangeJournal` |
| Always restore display after streaming ends | Not applicable (no streaming flow) | - | MISSING -> new `StreamingModeService.StopAsync`/rollback |
| Bounded + cancellable streaming start/stop with honest status | None | - | MISSING -> new (per-step timeout, cancellation, step list) |
| Remember paired Moonlight device names | None; needs Sunshine credentials/API | - | MISSING (not implemented; needs a consent + credential design) |
| Virtual display driver install/use | None; PaRuX must not install drivers silently | - | MISSING by design (consent flow still to design) |
| Sunshine config changes | None | - | MISSING by design (the new host seam has no config method) |

## Implemented in this slice (pure, injectable, tested)

- `Gaming/DisplayChangeJournal.cs`: write-ahead, atomic, bounded (32 entries / 64 KB) journal; entries owned per feature; newest-first recovery; entries whose monitor is gone are discarded, failed ones are kept for retry; corrupt file is quarantined and reported.
- `Gaming/StreamingModeService.cs`: Idle/Starting/Active/Stopping/Failed state machine; explicit consent flags; step timeout; cancellation; rollback of exactly what the session did; never stops a Sunshine PaRuX did not start; never installs Sunshine or drivers; no Sunshine config access.
- `tests/PaRuX.Server.Tests/StreamingModeServiceTests.cs`: 21 cases.

## Not done / not verified

- No Windows implementation of `IStreamingHost` / `IStreamingDisplayController` (real Sunshine start/stop, HDR via DisplayConfig); no DI registration, API endpoint or UI; `RecoverAfterCrash` is not yet called at startup.
- After a hard crash the "owned" Sunshine handle is not persisted, so a Sunshine PaRuX started is then treated as user-managed and left running (safe, but not cleaned up).
- Nothing was run against real displays or a real Sunshine.
