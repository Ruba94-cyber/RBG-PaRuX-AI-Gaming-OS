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

## AI proof batch (2026-11-22): managed offload report, release text, fallback proofs, CUDA vs Vulkan

Setup (all runs): isolated PaRuX Core (`PaRuX.Server`, Debug self-contained publish of this branch) with its own data root (`.ai/artifacts/aiproof`, git-ignored), RTX 5080 16 GB (driver 621.24, cc 12.0), Radeon 780M iGPU present. Runtime CUDA `llama.cpp-b12109-cuda13.4` (build 12109, commit 13ec8f614); Vulkan `llama.cpp-b10549-vulkan` (build 10549; the only Vulkan build installed, so CUDA and Vulkan are NOT the same llama.cpp build). Models `Qwen3-4B-Q4_K_M.gguf` (2.5 GB, primary) and `Qwen3.5-4B-Q4_K_M.gguf` (switch target). Desktop baseline VRAM 850-920 MiB. Every inference process was stopped afterwards (0 llama-server / Core left, VRAM back to 843-892 MiB).

### 1. TextLoad through a real PaRuX-managed launch: DONE
- PaRuX text launches (`LocalAiProcessManager`, main ladder path and the exact/temporary-model path `BuildArbitraryTextArguments`) now append `-lv 4` through `LlamaLoadLogParser.EnsureLoadLogVerbosity` (unless the list already sets a verbosity). Media assistant and router-mode launches are unchanged.
- Cost: bare llama-server 179.4 tok/s default log vs 178.9 tok/s at `-lv 4` (three launches earlier today); the stderr log did not grow per request (18.7 KB at load, unchanged after a chat turn). Ready-to-health 2.2-2.5 s either way.
- Live `GET /api/v1/runtime/status` after a real PaRuX launch (CUDA, Qwen3-4B, ctx 16384, flash-attn on, q8_0 caches, `--gpu-layers all`): `textLoad = { source: "llama-server load log", offloadedGpuLayers: 37, totalModelLayers: 37, loadedGpuBufferMib: 3786, requestedGpuLayers: "all", vramUsedMib: 5264 }`. After a model switch to Qwen3.5-4B (temporary-model path): 33/33 layers, 3142 MiB device buffers, 4632 MiB card used. Before the second change that path reported `unavailable` (it had no `-lv 4`); the proof run found and fixed it.
- The Vulkan fallback launch reports it too: 37/37, 8334 MiB buffers (see the finding below).

### 2. GPU release verification in status + UI text: DONE (code, unit-tested; live Gaming entry NOT RUN)
- `GamingStatusDto.AiReleaseVerification` ("verified" / "unverified" / "not_released") and `AiVramReleasedMib` already existed from the previous batch; `WorkloadOrchestrator` fills them.
- New pure wording `PaRuX.Contracts.GamingReleaseVerificationText` (English + Italian), appended to the existing "AI Workloads" line of the Gaming status in Settings. The only UI edit is `src/PaRuX.App/Views/SettingsView.xaml.cs` (`RefreshGamingStatusAsync`, the `GamingWorkloadsLabel.Text` assignment). No XAML, Gaming view or packaging file touched. MiB is shown only when measured.
- NOT RUN: a real game start/exit against live nvidia-smi, and a screenshot of the Settings text.

### 3. Fallback behaviour with real runs
| Scenario | Result |
| --- | --- |
| CUDA load failure -> Vulkan, remembered | A CUDA build folder whose `llama-server.exe` is not a valid executable was activated. First launch walked CUDA (launch plan), CUDA (default arguments), CUDA without flash attention, CUDA with reduced GPU offload (all failed: "not a valid application"), then `Vulkan: ready` in 8.7 s, profile "Qwen3-4B-Q4_K_M Standard GPU (Vulkan fallback)", trace visible in `textLaunchTrace`. NEW: `TextLaunchEngineMemory` remembers the working rung per (primary runtime folder, model) for 10 minutes, in memory. After killing the server, the next launch's trace was `remembered working engine: starting at Vulkan ... \|\| Vulkan: ready`: the four failing CUDA rungs were skipped (first reply after the second launch 9.4 s vs 18.4 s for the first launch, which included the failed rungs and Vulkan shader warm-up). A Core restart or expiry retries CUDA. |
| CPU rung live | NOT RUN. The CPU rung uses the Vulkan folder's executable; with both GPU engines broken the ladder ended honestly with the full trace and an HTTP 409/503 (no fake success). Rung order is covered by `TextLaunchLadderTests`. |
| Model switch | Forced route to Qwen3.5-4B while Qwen3-4B was resident: replied in 6.0 s including unload + load; status shows the new model/profile, 33/33 layers, a single llama-server. |
| Context sizing | Launch plan kept `--ctx-size 16384` ("the requested window fits", 13.2 GiB free after reserve), q8_0 caches, `--gpu-layers all`, `--fit on --fit-target 1024`; llama-server logged `n_ctx = 16384` (train 40960); `/api/v1/runtime/context-window` = 16384 observed. A constrained-VRAM shrink was NOT RUN live. |
| Abnormal exit | Killed the owned llama-server (PID 12412) with the model loaded (4982 MiB): 3 s later 0 llama processes and VRAM 916 MiB (no orphan VRAM); the next chat request relaunched a new server (PID 36208) and answered in 2.0 s, one llama-server only, TextLoad 37/37 again. |
| Offline | NOT RUN as true OS-level offline (needs a firewall/adapter change, a system setting I did not modify). Evidence instead: chat and RAG answered with zero new non-loopback connections; Core had 2 keep-alive HTTPS connections open since startup (AWS eu-west-3 and CloudFront; purpose not identified), unchanged by the requests. RAG over a 2-file synthetic folder (2 docs / 2 chunks): the "emergency shutdown code word / station manager" question was answered correctly with a citation (`[kestrel-project.txt, offset 0]`), 3.98 s first turn, 0.25 s second; plain chat 0.67 s. |
| Idle Core | PaRuX.Server with a model resident and no requests, 60 s sample: 0.036% of total CPU (0.34 CPU-s on 16 logical cores), working set 265 MB, private 173 MB, 35 threads. llama-server separately 0.8-1.4 GB working set. |

### 4. llama.cpp CUDA vs Vulkan, same model/quant/prompt (bare llama-server, 3 timed repetitions after 1 warm-up)
Qwen3-4B-Q4_K_M, `-ngl 99`, one slot, 124-token prompt, 128-token greedy completion (`/completion`, top_k 1, `cache_prompt` off), `-lv 4`, device CUDA0 / Vulkan1 (the RTX 5080; Vulkan0 is the iGPU). TTFT = request to first streamed token.

| Case | Decode tok/s (3 reps) | TTFT ms (3 reps) | Prompt tok/s | VRAM loaded |
| --- | --- | --- | --- | --- |
| CUDA b12109, ctx 4096, default flags | 210.3 / 214.3 / 213.2 (mean 212.6) | 35 / 22 / 22 | ~6500 | 4149 MiB |
| Vulkan b10549, ctx 4096, default flags | 209.7 / 213.0 / 210.9 (mean 211.2) | 51 / 30 / 31 | ~4500 | 3915 MiB |
| CUDA, PaRuX args (ctx 16384, flash-attn on, q8_0/q8_0, b2048/ub1024) | 201.8 / 203.0 / 202.9 (mean 202.6) | 35 / 25 / 24 | ~5650 | 4917 MiB |
| Vulkan, same args (flash-attn on) | 172.5 / 176.9 / 168.1 (mean 172.5) | 42 / 31 / 30 | ~4400 | 4655 MiB |
| Vulkan, PaRuX Vulkan-fallback args (flash-attn OFF, K q8_0, V f16, ctx 16384) | 37.9 / 38.4 / 40.2 (mean 38.8) | 3304 / 135 / 137 | ~950 | 9214 MiB |

- Finding (not changed, recommended): the CUDA->Vulkan rung (`TextLaunchLadder.Step.VulkanEngine`) turns flash attention off, which on Vulkan costs about 4.4x decode (38.8 vs 172.5 tok/s), ~4.6x prompt speed and doubles VRAM for the same model on the same GPU. The "27 tok/s Vulkan vs 178 tok/s CUDA" comment in `LocalAiProcessManager` is therefore mostly an argument choice, not the Vulkan backend (bare Vulkan matches CUDA at default flags). Suggested change: try Vulkan with the planned flash attention first (the existing `VulkanKeepFlash` step) and keep the flash-off Vulkan rung as the retry; that needs the 6-rung cap and four ladder test files adjusted, so it is left for a dedicated change.
- Caveat: Vulkan is build 10549, CUDA is build 12109; differences of a few percent cannot be attributed to the backend alone. The first Vulkan run also pays shader warm-up (the 3304 ms TTFT).
- Ollama and the LM Studio CLI: NOT RUN (`ollama`, `lms` not on PATH).

### Tests added
`GamingReleaseVerificationTextTests` (verified / unverified / not_released / none, EN+IT, no invented MiB; `-lv 4` added once and an existing verbosity kept) and `TextLaunchEngineMemoryTests` (record/recall, per-model and per-runtime keys, 10 minute expiry, primary success clears, forget, remembered rung absent from the plan).

### NOT verified (this batch)
Real Gaming entry/exit against live nvidia-smi and the Settings text on screen; CPU rung live; constrained-VRAM context shrink live; true OS-level offline; the purpose of Core's 2 background HTTPS connections; Ollama.

