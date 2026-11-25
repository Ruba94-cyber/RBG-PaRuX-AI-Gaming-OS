# Streaming / display gap table (2026-11-22)

Source of the requirement: a reference launcher's Sunshine/Moonlight streaming mode (see `docs/LAUNCHER_AUDIT_2026-11-22.md`).
Status words: DONE / PARTIAL / MISSING / BLOCKED-EXTERNAL. File:line refers to this branch before the new files were added.

| Legacy capability | PaRuX today | Evidence | Status |
|---|---|---|---|
| Detect Sunshine (installed / running) | Probe by process name, known install paths, loopback-only status URL | `Gaming/Providers/TriggerProviders.cs:113-215` | DONE (detection only) |
| Sunshine as trigger for a Gaming profile | Preset `preset-remote-sunshine` | `Gaming/GamingProfileStore.cs:267-276`, `WorkloadOrchestrator.cs:859-862` | DONE |
| Remote input safety (no duplicate virtual pads) | Policy | `Providers/GamingInputSafetyPolicy.cs:22-30` | DONE |
| Start the Sunshine service on demand | `WindowsStreamingHost.StartSunshineAsync` (installed exe, no arguments; consent flag; refused while Gaming) | `Gaming/WindowsStreamingHost.cs`, `StreamingEndpoints.cs` | PARTIAL (code + fakes tested; never run against a real Sunshine) |
| Stop Sunshine afterwards | Only the process PaRuX started (pid + start time must match); also stopped at normal app shutdown | `WindowsStreamingHost.StopOwnedSunshineAsync` | PARTIAL (not persisted across a hard crash; see below) |
| Test/choose a display, apply resolution/refresh | Driver-tested, temporary, confirm-or-auto-revert, one change at a time | `Gaming/GamingDisplayModeService.cs:70-122` | DONE for resolution/refresh |
| Stale-test recovery after crash (resolution/refresh) | Single-record restore at startup | `GamingDisplayModeService.cs:198-237`, `Program.cs:4201` | DONE for that one flow |
| HDR toggle with restore | No documented safe Windows API in this repo; the adapter reads HDR as unavailable and the API rejects it (400) | `WindowsStreamingDisplayController.cs` | MISSING (explicitly not implemented) |
| Multi-change crash journal (several kinds, bounded, corrupt-safe) | `DisplayChangeJournal`, written before each change, recovered at startup | `Gaming/DisplayChangeJournal.cs`, `Program.cs` | DONE (fakes; real display not exercised) |
| Always restore display after streaming ends | `StopAsync`/rollback via the Windows adapter (session-temporary change, exact previous mode restored) | `WindowsStreamingDisplayController.cs` | PARTIAL (resolution/refresh only; not run on a real monitor) |
| Bounded + cancellable streaming start/stop with honest status | Service + API: `GET /api/v1/gaming/streaming`, `POST .../start`, `POST .../stop` (desktop owner only) | `Gaming/StreamingEndpoints.cs` | DONE for the API; no UI |
| Remember paired Moonlight device names | None; needs Sunshine credentials/API | - | MISSING (not implemented; needs a consent + credential design) |
| Virtual display driver install/use | None; PaRuX must not install drivers silently | - | MISSING by design (consent flow still to design) |
| Sunshine config changes | None | - | MISSING by design (the new host seam has no config method) |

## Implemented

- `Gaming/DisplayChangeJournal.cs`: write-ahead, atomic, bounded (32 entries / 64 KB) journal; per-feature owners; newest-first recovery; entries whose monitor is gone are discarded, failed ones are kept; corrupt file is quarantined and reported.
- `Gaming/StreamingModeService.cs`: Idle/Starting/Active/Stopping/Failed state machine; explicit consent flags; step timeout; cancellation; rollback of exactly what the session did; never stops a Sunshine PaRuX did not start; never installs Sunshine or drivers; no Sunshine config access.
- `Gaming/WindowsStreamingDisplayController.cs`: resolution/refresh through the existing `IDisplayModeApi` (ChangeDisplaySettingsEx, session-temporary, driver-tested, topology-checked); the journaled previous value is the full previous mode. Hdr is refused.
- `Gaming/WindowsStreamingHost.cs`: detect installed/running Sunshine; start the installed exe; stop only the owned process. Registered in the Gaming hard-off launch allow-list as GAMING-FEATURE.
- DI in `Program.cs`; `StreamingStartupRecovery` runs `RecoverAfterCrash()` once at startup (non-Testing only), off the startup thread, bounded to 30 s, logging restored/discarded/failed counts; active streaming is stopped on normal shutdown.
- API (`StreamingEndpoints.cs`): desktop owner only (browser sessions denied in the route fixture); `allowStartSunshine` and `allowDisplayChange` must both be present (else 400); a display target without display consent is 400; Hdr/unknown kinds are 400; start is 409 while Gaming is active or a Gaming display change is pending; stop is always allowed.
- Tests: `StreamingModeServiceTests` (21) and `StreamingModeIntegrationTests` (adapter, recovery wiring, API contract, auth).

## Not done / not verified

- HDR: not implemented.
- Nothing was run against real displays or a real Sunshine; all adapter tests use fakes.
- After a hard crash the owned Sunshine handle is not persisted, so a Sunshine PaRuX started is then treated as user-managed and left running (safe, but not cleaned up). Display values are restored at the next start.
- No UI. Paired Moonlight device names, virtual display drivers and Sunshine config remain MISSING by design.
