# AI / Gaming handoff audit (2026-11-22)

Scope: "game starts -> AI released" and "game ends -> AI restored" as owned by `WorkloadOrchestrator`
(`src/PaRuX.Server/Orchestration/Gaming/WorkloadOrchestrator.cs`), plus CUDA runtime selection on an NVIDIA RTX GPU.

## What exists (verified in code)

| Requirement | Evidence |
| --- | --- |
| Block new heavy AI on launch | `ReleaseResourcesForGamingAsync` sets `_runtime.SetGamingState(true)` (cancels the gaming token) before anything else; `LocalRuntimeService.EnsureReadyAsync` refuses while `GamingMode` (`LocalRuntimeService.cs:202`); the persisted marker survives restarts (`ReconcileWithPersistedRuntimeState`). |
| Stop PaRuX-owned inference only | `ForceGamingShutdownAsync` -> process manager `GameStart` action writes the marker then `StopAll()` (`LocalAiProcessManager.cs:2610-2621`); processes are resolved from PaRuX PID files, never by name scan (`GetTextProcessId`, `LocalAiProcessManager.cs:700`). Game suspend uses only a process PaRuX itself started (`ValidateOwnedProcess`, identity = PID + start time + image path). |
| Cancel media jobs, speech, evals, GGUF quantizer | `_jobs.CancelAllForGaming()`, `_speech.StopWorkersForGaming()`, `_cancelEvaluations`, `_stopQuantizationForGaming` (confirmed, else entry blocked). |
| Pause downloads / indexing | `ModelDownloadService` and `GameDownloadManager` watch the marker and implement `IGamingResumable`; `KnowledgeIndex` skips the reranker under the marker (`KnowledgeIndex.cs:106`). Resume only when `RestoreAiOnExit` (`RestoreAiResourcesAsync`, resumables loop). |
| Restore on end, reload only on demand | `RestoreAiResourcesAsync(reloadRuntime: userInitiated || RestoreAiOnExit)`; otherwise the runtime stays stopped and loads lazily; `ScheduleEagerRestoreAfterGaming` only for always-on mode (`LocalRuntimeService.cs:134`). |
| Crash / unexpected exit safe | Manual game process exit -> `ExitManualGameSessionAfterProcessExitAsync` (version-checked, `WorkloadOrchestrator.cs:359`); failed entry rolls back launcher/input/AI (`:560-612`); `GamingGuardService` keeps retrying a failed release every tick while the marker is on (`GamingGuardService.cs:104`). |

## Gaps found

1. **FIXED - release was never verified.** `ReleaseResourcesForGamingAsync` returned success as soon as stop calls returned true; nothing measured VRAM or re-checked that the llama-server/ComfyUI processes were gone (the only status text was "GPU telemetry not yet verified"). Now `GamingVramReleaseVerifier` (new) samples used VRAM before the hard-off, then, with a bounded wait (default 8 s, 250 ms poll, 96 MiB settle tolerance), requires (a) no PaRuX-owned inference PID alive and (b) used VRAM settled. Outcomes: `verified` (reports MiB freed), `unverified` (degraded: no telemetry or memory still moving; entry proceeds, status says so), `not_released` (owned process alive: entry fails honestly, rolls back, Guard keeps retrying). Foreign VRAM (the game, a browser) never causes a failure. Surfaced as `GamingStatusDto.AiReleaseVerification` / `AiVramReleasedMib`.
2. **OPEN - Guard retry path is unverified.** `GamingGuardService.AttemptShutdownAsync` (`GamingGuardService.cs:108-150`) marks the shutdown `Succeeded` without the VRAM check; only the orchestrator entry path is verified now.
3. **OPEN - orchestrator does not stop `HardOff` children synchronously.** Avatar / retrieval / vision children are stopped by the Guard tick (`GamingGuardService.cs:84`, <=250 ms later) not by `ReleaseResourcesForGamingAsync`, so "released" in the orchestrator can precede their exit.
4. **OPEN - non-manual (Playnite/Steam-launched) games have no automatic end detection.** Only the manual-game session watcher auto-exits (`ExitManualGameSessionAfterProcessExitAsync`); a launcher-started game that exits leaves state at `GamingLocal` until the user presses Return to AI (safe, but AI stays off). By design (ownership rule), not changed.
5. **OPEN - per-process VRAM on WDDM** is frequently `[N/A]` in nvidia-smi (`NvidiaSmiProbe.ParseComputeAppMemoryMib`), so the verifier uses whole-card used memory plus owned-PID liveness, not per-process attribution.

## CUDA runtime selection (RTX class GPU)

| Item | Status | Evidence |
| --- | --- | --- |
| NVIDIA detection / CUDA preferred over Vulkan | PROVEN in code + earlier measurement | `LocalAiProcessManager.cs:1664` (27 tok/s Vulkan vs 178 tok/s CUDA on RTX 5080, 4B); `RuntimeHardwareProfile.cs:80-97`. |
| Driver / CUDA compatibility | PARTIAL, gap | `RuntimeBackendAdvisor.cs:34-35` hard-codes driver >= 527.41 (CUDA 12) and compute capability >= 5.0, but the installed builds are `cuda13.4` (CUDA 13 needs a much newer driver and drops pre-Turing GPUs). Not tied to the installed build's CUDA major. This machine (driver 621.24, cc 12.0) passes either way; an older driver/GPU could be told "OK". Not fixed in this batch. |
| Layers offloaded reported after a real load | UNPROVEN / gap | `ActualGpuLayers` (`RuntimePerformanceTuner.cs:135`) is the `n_gpu_layers` value llama-bench was *asked* to use, not the "offloaded N/M layers" llama-server logs; no server code parses the load log. At default verbosity the log from my run had no offload line either. |
| VRAM reported after a real load | PARTIAL | Used VRAM is readable via nvidia-smi (`GpuMemoryReleaseWaiter`, `NvidiaSmiProbe`), but nothing records "VRAM used by this model after load" next to the model. |

## Real run on the RTX 5080 (one short run, bounded, process stopped afterwards)

- Runtime: `llama.cpp-b12109-cuda13.4/llama-server.exe` (PaRuX-installed), model `Qwen3-4B-Q4_K_M.gguf` (2.5 GB, in the repo test-data folder), `-ngl 99 -c 4096`, 128-token completion.
- Driver 621.24, RTX 5080 16303 MiB, compute capability 12.0.
- Decode: **109.0 tokens/s** (128 tokens, 9.17 ms/token); prompt eval 55.5 tokens/s on an 8-token prompt (not representative).
- VRAM used: 861 MiB before -> 4156 MiB during (**+3295 MiB**); after killing the server (PID 32452, started by this run) back to **861 MiB** (release confirmed by nvidia-smi).
- Layers offloaded: **NOT CAPTURED** (no offload line at default verbosity; +3.3 GiB resident implies GPU residency but is not a layer count).
- The 109 tok/s is below the earlier 178 tok/s PaRuX figure: this was a bare llama-server with default flags (no PaRuX launch optimizer, flash-attn, batch tuning) while the desktop was in use; do not read it as a regression or a benchmark.

## NOT verified

- The new verifier against real nvidia-smi inside a real Gaming entry (only fake-telemetry unit tests); real orchestrator entry/exit with a game was not run.
- Behavior with ComfyUI media engine resident (no media run done).
- CUDA 13 driver floor on older drivers; layer count after load.
