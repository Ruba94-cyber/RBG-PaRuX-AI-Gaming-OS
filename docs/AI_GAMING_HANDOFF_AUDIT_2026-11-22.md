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
2. **FIXED (see follow-up) - Guard retry path is unverified.** `GamingGuardService.AttemptShutdownAsync` (`GamingGuardService.cs:108-150`) marks the shutdown `Succeeded` without the VRAM check; only the orchestrator entry path is verified now.
3. **FIXED (see follow-up) - orchestrator did not stop `HardOff` children synchronously.** Avatar / retrieval / vision children are stopped by the Guard tick (`GamingGuardService.cs:84`, <=250 ms later) not by `ReleaseResourcesForGamingAsync`, so "released" in the orchestrator can precede their exit.
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

## Follow-up batch (2026-11-22, same day): gaps 1-3 and CUDA/offload items closed in code

| Gap | Status | Evidence |
| --- | --- | --- |
| 2 (Guard retry unverified) | FIXED | `GamingGuardService.AttemptShutdownAsync` now samples used VRAM before the attempt and, after a "released" result, runs the same `GamingVramReleaseVerifier` (production: `GpuMemoryReleaseWaiter` + `LocalRuntimeService.OwnedInferenceProcessIds`). `verified` / `unverified` complete the shutdown (unverified is recorded, not hidden); `not_released` keeps it pending and retries. State is on `GamingShutdownRetry.LastVerification` / `LastVramReleasedMib`. Tests: `GamingReleaseHandoffGapTests` (3 Guard tests). |
| 3 (HardOff children) | FIXED | `ReleaseResourcesForGamingAsync` calls `_runtime.HardOff.StopAll()` itself and refuses entry (Guard keeps retrying) if a child does not confirm exit. Tests: 2 orchestrator tests in the same file. |
| CUDA driver floor | FIXED in `RuntimeBackendAdvisor` | Floors by CUDA major of the installed build (its toolkit: runtime sidecar/active toolkit, else the folder name) or of the build PaRuX would install (same rule as `RuntimeBuildCatalog.PreferredCudaMajor`): CUDA 12 = driver 527.41 / cc 5.0 (unchanged); CUDA 13.x = driver 580 / cc 7.5; Blackwell (cc >= 10, incl. 12.0) = driver 570.65 on any build. Source: NVIDIA CUDA Toolkit release notes (driver branch R580 for 13.0; one minor-version-compatibility minimum of ">= 580" for 13.x, which covers the bundled 13.4 build whose own branch is R615; CUDA 12.8 GA lists Windows >= 570.65). The notes list no per-OS number for 13.x, so 580 is the documented branch floor, not a Windows-specific build number. cc 7.5 comes from CUDA 13 dropping pre-Turing support (the page I read states this explicitly only for cuFFT; treat the cc floor as less firmly sourced than the driver floor). Tests: `RuntimeBackendAdvisorCudaFloorTests` (13). |
| Layers offloaded after load | FIXED (needs `-lv 4`) | `LlamaLoadLogParser` reads llama-server's own log: `offloaded N/M layers`, and the sum of non-CPU/non-`_Host` model+KV+compute buffers of the last load pass (recent builds run a zero-size fitting pass first; both are handled). Surfaced as `RuntimeManagerStatus.TextLoad` (`OffloadedGpuLayers`, `TotalModelLayers`, `LoadedGpuBufferMib`, `VramUsedMib`) and `RequestedGpuLayers` kept separate and labelled as a request. llama-bench `ActualGpuLayers` is documented as requested, with an honest `RequestedGpuLayers` alias. At default verbosity (`-lv 3`) b12109 logs none of these lines (17 log lines), so status says `unavailable` with a note; PaRuX launch arguments were NOT changed to raise verbosity. Tests: `LlamaLoadLogParserTests` (9). |

### Ollama vs llama.cpp comparison harness: NOT RUN

Ollama is not installed on this PC (`ollama` not on PATH; the PATH entry `%LOCALAPPDATA%\Programs\Ollama` does not exist). No harness skeleton was written; add it when Ollama is present, comparing the same GGUF, `-ngl`/context and 128-token greedy completion.

### Real run, RTX 5080, 2026-11-22 (three short launches, each stopped afterwards)

- Runtime `llama.cpp-b12109-cuda13.4` (PaRuX-installed), model `Qwen3-4B-Q4_K_M.gguf` (2.5 GB, already on disk), `-ngl 99 -c 4096`, port 18434, one 128-token greedy completion per launch (prompt of 10 tokens, `cache_prompt` off). Driver 621.24.
- Decode: 179.4 / 179.3 / 178.9 tokens/s (default log, `-v`, `-lv 4`). This matches the earlier PaRuX 178 tok/s figure, so the 109 tok/s in the first run above was a cold/busy-desktop artifact, not the engine.
- Time to first token (non-streaming; approximated by prompt-eval time of the 10-token prompt, which is the time until the first token can start): 64 ms / 38 ms / 86 ms. Server ready (load + health) 2.8-4.1 s.
- Layers offloaded (from the `-v` / `-lv 4` log): **37/37** (the log counts the 36 transformer layers plus the output layer: "offloaded 37/37 layers to GPU"). Device buffers from the real pass: model 2375.91 MiB + KV 576.00 MiB + compute 81.08 MiB = 3033 MiB (CPU_Mapped 304.28 MiB and CUDA_Host 24.08 MiB are host RAM). Default-verbosity run: not observable (17 log lines, no offload line).
- VRAM used (nvidia-smi, whole card): 891 MiB before -> 4190 MiB after load (+3299 MiB; CUDA context accounts for the ~270 MiB beyond the logged buffers) -> 4172 MiB after generation -> **867 MiB after the server was stopped** (stopped by the run script; no inference process left; second and third launches showed the same 866/864 MiB return).

### NOT verified (this batch)

- `TextLoad` through a real PaRuX-managed launch and the live `/api/v1/runtime/status` endpoint (parser and contract wiring are unit-tested; the real run above used bare llama-server). In a PaRuX launch the report will say `unavailable` until the runtime is started with `-lv 4`.
- New Guard-retry verification against real nvidia-smi inside a real Gaming entry; real game start/exit; ComfyUI resident.
- CUDA 13 floors on an actual older driver or pre-Turing GPU (rule tests only); the cc 7.5 floor's source (see above).
