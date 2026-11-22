# AI performance: PaRuX vs Locally Uncensored (2026-11-22)

Scope: one PC, one GGUF, one prompt set, repeated runs, honest verdicts. Settled numbers from `COMPETITOR_MEASURED_2026-11-21.md` (UI timing, startup, shutdown, AnythingLLM engine numbers) are reused, not repeated. Harness (committed, no machine data): `tools/competitor-bench/` (`bench.py`, `Run-Engine.ps1`, `Run-Matrix.ps1`, `Run-ParuxCore.ps1`, `parux_rag.py`, `make_rag_corpus.py`, `Probe-IdleCpu.ps1`, summarizers). Raw JSON stayed in a session scratch folder (not committed).

## Machine and method
- Windows 11, Ryzen 7 8840U class CPU, RTX 5080 16 GB (driver 621.24) plus Radeon 780M iGPU. NOT a quiet machine: other agents ran `dotnet test` (testhost) and a Locally Uncensored instance (1.5B model, 2.8 GB VRAM) was resident during the runs, so absolute CPU-bound and idle numbers carry noise; VRAM is reported as a delta over the idle baseline taken just before each launch.
- Model: `Qwen3-4B-Q4_K_M.gguf` (2,497,280,256 bytes), identical file for every row. Prompts: short (about 13-37 tokens), about 1.2k and about 4.6k tokens of synthetic prose; unique nonce per run (no prompt-cache help), temperature 0, 128 new tokens, 1 warm-up excluded plus 5 (engine) or 5-7 (Core) measured runs. Median (min-max). Decode and prompt-processing (pp) come from the server's own `timings`; TTFT is wall time to the first streamed token (reasoning tokens count).
- Engines: PaRuX CUDA llama.cpp b12233 (CUDA 13.5) and b12600; PaRuX Vulkan b12631; Locally Uncensored (LU) 3.1.1 `lu-llama-server` (its own Vulkan build). Each engine was run with its own launch flags and with PaRuX's flags (the exact flags the PaRuX Core builds, captured from the real process command line).
- "Core" rows are the PaRuX Core (`PaRuX.Server`, built from this branch) run headless on an isolated copy of a data root, chat through `/api/v1/chat/completions` (the same route the UI uses). The WinUI process is not included in Core idle numbers.

## 1. Engine level, same GGUF, CUDA/own builds (RTX 5080, ctx and KV as stated)
| Config | ctx / KV | Load ms (3 cold starts) | VRAM delta MiB | Engine RAM MB | Decode tok/s short / 1k / 4k | Prompt-proc tok/s 1k / 4k | TTFT ms short / 1k / 4k |
|---|---|---|---|---|---|---|---|
| PaRuX engine b12233, PaRuX flags (as shipped) | 16k q8_0 | 1407 (1364-3460) | 4069 | 3318 | 207 (201-212) / 196 (195-198) / 179 (176-181) | 11986 / 12041 | 57 / 178 / 570 |
| PaRuX engine b12600, PaRuX flags | 16k q8_0 | 2006 (1582-3858) | 4141 | 3360 | 208 / 179 (108-198, one outlier) / 179 | 11624 / 11962 | 59 / 173 / 527 |
| PaRuX engine b12233, PaRuX flags but KV f16 | 16k f16 | 1336 (1332-1390) | 5146 | 5491 | 219 (217-222) / 212 (209-213) / 186 (185-190) | 12532 / 12636 | 58 / 189 / 665 |
| PaRuX engine, llama.cpp default batch (2048/512) | 16k q8_0 | 1306 | 3993 | 3275 | 214 / 202 / 180 | 10927 / 10870 | 62 / 179 / 571 |
| PaRuX engine b12233, LU's flags | 32k f16 | 1416 (1361-1431) | 7367 | 5462 | 216 / 195 / 179 | 12126 / 11584 | 55 / 183 / 681 |
| LU engine, LU's flags (its default) | 32k f16 | 3561 (2522-3791) | 7526 | 5371 | 215 (212-217) / 199 (197-203) / 173 (156-180) | 9609 / 9873 | 64 / 219 / 790 |
| LU engine, PaRuX flags | 16k q8_0 | 2555 (2481-3587) | 3908 | 3403 | 163 / 160 / 146 | 9595 / 9278 | 53 / 205 / 648 |
| LU engine, LU flags at ctx 16k | 16k f16 | 2458 | 5194 | 5386 | 185 / 171 / 130 | 8895 / 8664 | 70 / 238 / 832 |
| AnythingLLM engine (lemond, CUDA b11425), 2026-11-21, not re-run | 40k f16 | not measured | 8516 (system-wide) | - | 224 / 211 / 188 | 11224 / 10913 | 52 / 228 / 681 |

Findings:
- Same-flag comparison: the PaRuX CUDA build prefills 25% faster than LU's Vulkan build (12.0k vs 9.6-9.9k tok/s) and loads 2-2.5x faster (1.4 s vs 2.5-3.6 s); decode is equal within noise at ctx 16k/32k for short and 1k prompts and 3% ahead at 4k.
- LU's engine is clearly slower with PaRuX's flags (163 vs 215 tok/s): q8_0 KV on a Vulkan build costs it about 25%, which is why LU keeps f16. The PaRuX CUDA build does not pay that price (207 vs 219).
- AnythingLLM's engine decodes 5-8% faster than PaRuX's default (224/211/188 vs 207/196/179). The cause is isolated: it is f16 KV. The PaRuX engine with f16 KV gives 219/212/186, i.e. equal to AnythingLLM.
- Build b12600 vs b12233: no measurable difference.

## 2. PaRuX Core (what a user's chat actually runs), CUDA runtime configured vs Vulkan only
Launch flags captured from the process (CUDA state): `--ctx-size 16384 --batch-size 2048 --ubatch-size 1024 --cache-type-k/v q8_0 --flash-attn on --gpu-layers all --fit on --fit-target 1024 --cont-batching --parallel 1 --load-mode none --poll 45 --reasoning-effort medium --reasoning-budget 768`. Vulkan-only state: b12631, `--device Vulkan1` (the RTX 5080, chosen by the device selector; before 2026-11-21 it was the iGPU `Vulkan0`), ctx 32768, batch 512 / ubatch 128, `--threads 12 --threads-batch 16 --cpu-strict 1`.
| Core state | Cold Core start + model ready (s) | VRAM delta MiB | Decode short / 1k / 4k | Prompt-proc 1k / 4k | TTFT ms short / 1k / 4k | Idle CPU / RAM (Core + engine, 60 s settle) |
|---|---|---|---|---|---|---|
| CUDA runtime configured | 6.6-6.8 (3 starts) | 4058 | 202 (201-209) / 190 (188-193) / 176 (174-178) | 11396 / 11518 | 211 / 321 / 730 | 1.1% / 4.3 GB (engine 0.1%, Core 1.0%) |
| Vulkan only, before this change | 8.8-9.8 | 4884 | 178 (163-186) / 164 (149-166) / 157 (151-160) | 7332 / 7064 | 190 / 401 / 1010 | 0.9% / 6.7 GB |
| Vulkan only, after this change (batch 2048/1024 on a discrete card) | 13.3 (one cold start) | 5074 | 151 / 161 / 157 | 10262 / 10106 | 221 / 412 / 880 | see note |
Notes:
- Idle after a chat burst can include Core-started background inference: two of five Vulkan runs showed the engine at 28% and 100% of a core for the first 20-60 s after the bench (the same flags run raw, with a bench and then idle, decayed to under 1% in 20 s; the raw probes show 0.5-1.6% idle for every flag variant). Settled idle is 0.8-1.1% of one core. The WinUI process adds about 460 MB (2026-11-21 measurement).
- Core vs raw engine at TTFT: short 190-210 ms vs 54-57 ms. Instrumented timeline (temporary, not committed): model routing 34-47 ms (candidate evidence: runtime status 10-15 ms, hardware fit per model 8-10 ms, ranking the rest), context pack 7 ms, then the refusal-fallback hold (`ChatProxyService.StreamWithFallbackAsync`) which buffers the first content tokens for up to 100 ms (or 240 characters) to detect a refusal before showing text; reasoning tokens bypass it. A request flagged `preferLowLatency` (what the UI sends for trivial turns) skips the context pack, tools and the hold and measured 75-140 ms.
- RAG (below) and decode through the Core agree with the raw engine within noise.

## 3. RAG, synthetic corpus (30 markdown files, 136 KB, one unique fact per file; 10 questions x 3 reps)
| Product | Ingest | Retrieval | Answers |
|---|---|---|---|
| PaRuX Core (embeddings bundled in the portable data root) | 2.4-2.7 s synchronous reindex (31 documents, 152 chunks), index fully settled after 4.6-4.7 s | median 15 ms (4-48 ms), hit@5 10/10 | 30/30 correct. Lookup-style questions are answered deterministically from the cited passage with no model call (16 ms); a model-grounded question was NOT separately timed |
| Locally Uncensored | NOT run: needs an 84 MB embedding model that is not installed and UI driving | - | 2026-11-21: 5/5 with files attached inline, no RAG |
| AnythingLLM | NOT run: not installed any more (installer kept); needs UI onboarding and an API key | - | 2026-11-21: 5/5 with files as context, no multi-chunk ingest timing |
| Unsloth Studio | NOT run (see below) | - | - |
Only PaRuX has a RAG ingest/query measurement; there is no competitor number to compare, and none is claimed.

## 4. Verdicts per metric
| Metric | Verdict |
|---|---|
| Decode tok/s, CUDA, own defaults | vs LU: equal within noise (207 vs 215 short, 179 vs 173 at 4k). vs AnythingLLM engine: SLOWER by 5-8%; cause f16 KV, reproduced (219/212/186 with f16). f16 not adopted, see below |
| Prompt processing tok/s | FASTER than LU (+25%); equal/faster than AnythingLLM (12.0k vs 11.2k, previous session) |
| TTFT, engine level | equal within noise (short 57 vs 52-64 ms); long prompts faster than LU (570 vs 790 ms at 4k) |
| TTFT through the Core (default API path) | SLOWER than a raw engine by about 140 ms (short) because of routing 35-45 ms plus the refusal hold; UI trivial turns 75-140 ms. NOT fixed |
| Model load | FASTER than LU (1.4 s vs 2.5-3.6 s). AnythingLLM not measured |
| VRAM used | LOWER than LU at the products' own defaults (4.07 GB vs 7.5 GB) and at equal ctx 16k (4.07 vs 5.19 GB); the gap is mostly KV type |
| Idle RAM/CPU | equal within noise (Core + engine 0.8-1.1%, 4.3 GB with the model; LU 1.7%/8.0 GB and AnythingLLM 0.8%/3.7 GB from 2026-11-21) |
| Vulkan-only default (no CUDA runtime configured) | now on the right GPU (RTX 5080) but 20% slower decode and 35-40% slower prefill than CUDA; with this change prefill is +45% |
| RAG ingest/query | no competitor measured; PaRuX 2.5 s for 30 files, 15 ms retrieval, 10/10 |

## 5. Root causes, changes and rejected changes
1. FIXED, tested: Vulkan on a discrete GPU launched with batch 512 / ubatch 128, the integrated-GPU values. Engine A/B on the RTX 5080 through Vulkan: 512/128 prefilled at 6.8-7.0k tok/s and 4k TTFT 814 ms; 2048/1024 gave 10.5k tok/s and 569 ms (+55% / -30%); 2048/512 was in between; decode unchanged. `VulkanDeviceSelector.DeviceChoice` now carries `Discrete` (not integrated, at least 4 GiB dedicated) and `RuntimeLaunchOptimizer` uses `LocalAiProcessManager.DefaultBatchSizes(true, ...)` for it; an integrated GPU, an unknown listing and an explicit user device keep the small values. Test: `VulkanDeviceChoiceTests.A_discrete_vulkan_card_prefills_with_the_large_batches_...`. Through the Core the prefill gain reproduced (7.3k to 10.2k), the TTFT gain at 4k is 1010 to 880 ms; 1k and short TTFT did not move beyond noise because the Core path dominates there.
2. REJECTED with evidence: promoting the default KV cache to f16 when VRAM is ample (prototype built and tested, then removed). Engine and Core runs agree: decode +4-8% and prefill +4%, but VRAM +1.1 GB, engine RAM +2.2 GB (3.3 to 5.5 GB), and TTFT at 4k +100-150 ms (665 vs 570 ms raw; 900 vs 745 ms through the Core, repeated in an A/B/A/B interleave). It is a trade, not a clear win, so the default stays q8_0. The gap to AnythingLLM at decode is therefore a documented, deliberate memory trade-off.
3. NOT FIXED (cause known): Core pre-engine overhead and the 100 ms refusal hold on the default API path. Candidate ideas, each with a behaviour risk that needs an owner decision: cache the per-role candidate evidence for a few seconds keyed by library stamp and loaded model (saves 20-25 ms), skip evidence collection when exactly one candidate is the healthy resident model, and release the refusal hold earlier once a fixed number of tokens has streamed (the hold exists so an auto-uncensored retry can replace a refusal before it is shown).
4. Risk found, not fixed: a data root that has a CUDA runtime installed but only `text-vulkan` in `LocalAI/config/runtime-build-config.json` launches the Vulkan build (the 4.4.0 portable copy used for the earlier session did exactly this after an automatic Vulkan runtime update). With `text-cuda` set the same root runs CUDA. Whether the real setup flow always writes the CUDA tag was not verified here.

## 6. Not run / limits
- Unsloth Studio: not run. The 2026-11-21 finding stands: its installer needs machine-level Git, CMake, Visual Studio Build Tools and CUDA through winget, which is beyond "no elevated installs"; no non-elevated path was verified.
- AnythingLLM Desktop: not reinstalled this session (it had been removed); its engine numbers are the 2026-11-21 ones, UI/RAG not repeated. Locally Uncensored: engine only; no UI TTFT or RAG repeat, no embedding model installed.
- UI-level timings for PaRuX were not measured; Core idle excludes the WinUI process.
- Contention: other agents' tests and a resident LU instance ran concurrently; spread is wide on a few rows (see min-max) and the TTFT numbers through the Core have roughly +-50 ms of noise.
- Single model (4B Q4_K_M) and single GPU; larger models, MoE offload, speculative decoding and concurrent-slot throughput were not compared.
