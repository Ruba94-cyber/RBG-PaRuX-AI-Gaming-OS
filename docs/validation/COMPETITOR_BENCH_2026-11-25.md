# Competitor benchmark on this PC, PaRuX 4.5.1 (2026-11-25)

Scope: PaRuX 4.5.1 as installed (`C:\Program Files\PaRuX`) against Locally Uncensored (LU) 3.1.2 as installed, same GGUF, same prompts, same machine. Nothing here claims general superiority: every verdict names its metric, its level (engine, Core API, app) and its noise. Raw JSON and logs are in `.ai/bench/` (git-ignored); the harness is `tools/competitor-bench/` plus the small drivers described under "Commands".

## Machine and conditions
- Windows 11 Home 10.0.26320, Ryzen 7 8840U (16 logical), 25.8 GB RAM, RTX 5080 16 GB (driver 32.0.16.2124) + Radeon 780M. Claude Desktop and other tools were open; no other inference engine, no game and no `testhost` during the timed runs. The machine is shared, so idle numbers carry about +-0.5 points of noise.
- Model: `Qwen3-4B-Q4_K_M.gguf`, 2,497,280,256 bytes, SHA-256 prefix `7485FE6F11AF`; the PaRuX copy and LU's copy in its models folder are byte-identical (hashes compared). A second file (`Qwen3.5-4B-Q4_K_M.gguf`) was used only for model-switch timing. No model was downloaded.
- PaRuX data root: isolated (`PARUX_DATA_ROOT=.ai/bench/root-a`), fresh (no onboarding, no user data). PaRuX itself copied its bundled CUDA 13.4 b11871 runtime from the installed `runtime-baseline` on first start, so "as shipped" runs the CUDA build on the RTX 5080 (the 2026-11-21 Vulkan-on-iGPU problem does not occur). PaRuX is closed with `PaRuX.exe --exit` (0.65-0.70 s, 0 leftover processes).
- Prompts (`bench.py`): short (about 13-39 tokens), about 1.2k and about 4.6k tokens of synthetic prose; a unique nonce per run (no prompt-cache help), temperature 0, `max_tokens` 128, 1 warm-up excluded + 5 measured, median. A "gen" prompt (400-word essay, 256 tokens) is used for concurrency.
- Token/s: the server's own `timings.predicted_per_second` is the headline. `usage.completion_tokens` (the server's own count) divided by the client's first-to-last-token time is shown as a cross-check ("via usage"). Chunk counts are never used. Through PaRuX the cross-check over-reads on short replies because the Core holds the first 32 characters for the refusal check and releases them together.
- Not installed on this PC, therefore not measured: Ollama, LM Studio, llama.cpp CLI (PATH has entries for Ollama and `.lmstudio`, the folders do not exist), AnythingLLM, Unsloth Studio (`~/.unsloth` exists but is empty; nothing was installed or run).

## 1. Engines, same file (engine level, RTX 5080)
Each row is the engine started alone with the stated flags (`Run-Engine.ps1`, 3 cold loads, then the bench).
| Config | ctx / KV | Load ms (3 cold) | VRAM delta MiB | Decode tok/s short / 1k / 4k (server) | Decode via usage | Prefill tok/s 1k / 4k | TTFT ms short / 1k / 4k |
|---|---|---|---|---|---|---|---|
| PaRuX engine CUDA b11871, PaRuX flags | 16k f16 | 1098, 1147, 1075 | 5143 | 226 / 207 / 185 | 231 / 212 / 189 | 10.1k / 9.9k | 46 / 188 / 656 |
| PaRuX engine, same + `--parallel 4 --kv-unified` | 16k f16 | 1141, 1092, 1141 | 5143 | 227 / 207 / 185 | 232 / 212 / 189 | 10.3k / 10.1k | 38 / 188 / 648 |
| LU default engine (`lu-llama-server`, Vulkan, its own flags) | 32k f16 | 3505, 2365, 2297 | 7526 | 208 / 195 / 172 | 213 / 199 / 175 | 8.5k / 8.6k | 66 / 235 / 793 |
| LU "CUDA" shortcut (external llama.cpp b12621 CUDA, `-c 16384 -np 1`) | 16k f16 | 3650, 2025, 1993 | 5043 | 221 / 203 / 180 | 227 / 209 / 185 | 10.1k / 9.8k | 54 / 203 / 697 |

Reading: with a CUDA backend PaRuX's engine and LU's CUDA shortcut are within about 3% on decode and prefill and about 2-3x apart on load time (1.1 s vs 2.0-3.7 s). LU's own default Vulkan engine is about 5-7% slower on decode and about 15% slower on prefill, uses 7.5 GB (32k context) and needs 15-20% more time to the first token at 1k/4k. The CUDA shortcut is a manual owner script (`Start-LU-CUDA.ps1`), not LU's default.

## 2. PaRuX through the Core (what a chat really runs) and cold start
Core = `PaRuX.Server` headless on the isolated root (`Run-ParuxCore.ps1`), route `/api/v1/chat/completions`, the route the app uses.
| Metric | PaRuX 4.5.1 installed | With this branch's fixes | Notes |
|---|---|---|---|
| Core start + model ready (3 cold starts) | 14.1 s (first, cold file cache), 6.8 s, 6.75 s | 6.7, 6.6, 6.7 s | includes the explicit model load call |
| TTFT short / 1k / 4k | 141 / 376 / 861 ms | 121 / 308 / 917 ms | engine alone 46 / 188 / 656 ms; difference = Core routing, context pack (about 914 cached tokens) and the refusal hold |
| Decode tok/s short / 1k / 4k (server) | 222 / 204 / 185 | 221 / 202 / 180 | equal to the engine within noise |
| Prefill tok/s 1k / 4k | 11.1k / 10.9k | 10.2k / 9.5k | |
| `usage` frame on streamed replies | short only (1k and 4k: absent) | 5/5 at every length | finding 2 |
| VRAM delta loaded | 5131 MiB | 5131 MiB | adaptive f16 KV chosen (16k ctx) |
| Idle Core (60 s settle, no UI) | Core 2.8% + engine 0.5% of one core; 6.9 GB working set (engine 6.7 GB, mmapped) | 2.4% + 0.6% | finding 3 |
| RAG, 30 synthetic files (31 docs, 152 chunks) | add source 32 ms, reindex 328 ms, index settled 1.6 s; search median 15.5 ms (3.7-36); hit@5 10/10; grounded answers 30/30, TTFT median 328 ms, total 536 ms | reindex 100 ms; search median 11.5 ms; 10/10; 30/30, 330 / 540 ms | LU RAG not measured (limits) |

### App cold start (PaRuX.exe, isolated root, 3 runs)
| Step | PaRuX 4.5.1 | LU 3.1.2 (3 runs) |
|---|---|---|
| Process start to first titled window | 0.58 / 0.58 / 0.89 s | 0.16 / 0.17 / 0.65 s |
| Core answering `/health` | 2.7-3.3 s | not applicable |
| Start to first chat reply (model auto-loaded on the first request, GPU) | 6.3 / 6.5 / 7.1 s (the chat call itself 3.5-3.8 s) | NOT measured (a chat in LU's UI would write into the user's LU data) |
| Close | `--exit` 0.65-0.70 s, 0 leftover | the window hides to tray; the harness killed its own process tree, 0 leftover afterwards |

### Idle (whole tree, 30 s window after a 25-45 s settle)
| App | CPU of one core | Working set | Breakdown |
|---|---|---|---|
| PaRuX 4.5.1, UI open, model resident | 5.05% (4.8-6.0 over 3 runs) | 1.25-1.27 GB | `PaRuX.Server` 3.49%, `PaRuX` UI 0.99%, `llama-server` 0.57% |
| LU 3.1.2, window open | 1.46 / 1.77 / 1.72% | 1.40-1.59 GB | `lu-llama-server` x2 about 0.8-1.0 GB, WebView2 x6 about 0.5 GB |

This is the clearest deficit of PaRuX in this report: about 3x LU's idle CPU. LU's model state at idle was not inspected (two `lu-llama-server` processes were running), so this is app-open vs app-open, not model-for-model.

## 3. Model switch and concurrency
| Test | PaRuX 4.5.1 | PaRuX with the fix | LU |
|---|---|---|---|
| Switch the loaded model through `/api/v1/models/library/<file>/load` | from a resident runtime on port 1234: 3 of 3 hung (server up on 1235, Core waiting on 1234, `providerReady=false` for minutes); from the stuck state the next switch worked in 3.9-4.5 s | 6 of 6 worked: 4.6 s (first), then 1.9-2.5 s with a warm file cache | engine restart-to-ready 2.0-3.7 s (an in-app switch was not driven) |
| 3 concurrent 256-token requests, aggregate server-counted tok/s (median of 3 rounds) | 184 tok/s; first token per request 0.27 / 1.6 / 2.9 s (`--parallel 1`, requests queue) | same | LU default Vulkan 114 tok/s (4 slots, slower than one request at 189); LU CUDA shortcut `-np 1` 210 tok/s, worst first token 2.5 s |
| PaRuX engine with `--parallel 4 --kv-unified` (engine level, not a shipped setting) | | 348 tok/s aggregate, worst first token 0.75 s, single-request speed unchanged (213 tok/s) | |

Reading: at `--parallel 1` PaRuX serialises concurrent clients; a 4-slot unified launch gave 1.6x the aggregate throughput and about 3x lower worst wait for 3 clients, with no single-user cost on this engine. It was not adopted: prompt-cache, slot-similarity and reasoning-checkpoint behaviour with 4 slots is untested in the Core, and the launch planner already has a `ParallelSlots` knob and a concurrency optimizer. Owner decision.

## 4. Usability: steps for identical tasks (read from the UI source and LU's public README; LU was not driven)
Counting rule: one click or one keystroke group is 1 step; a native file or folder picker is 2 steps (open + pick). PaRuX numbers come from `ChatView.xaml`, `ChatView.SimpleMode.cs`, `ChatView.Documents.cs` and `OnboardingView.*`; LU from its README (which documents no click path for RAG or settings). "Not verified" means not seen in a running UI by this run. No screenshots were taken.
| Task | PaRuX 4.5.1 | LU 3.1.x | Comment |
|---|---|---|---|
| First chat with a model file already present | start the app, type, Enter (the Core loads the model on the first request; reply after 6.3-7.1 s); "Auto" routing is the default | first-run wizard (skippable), Chat tab, type | similar; PaRuX asks for no model choice |
| Choose a different chat model | 2: open the picker under the composer, pick a model | model manager, then select (not verified) | PaRuX switching hung before the fix |
| Ask about one document | attach button (1) + file picker (2; accepts PDF, DOCX, XLSX, TXT, images, video), type, Enter: 4 | upload in chat (not verified) | the button tooltip said images only: fixed |
| Ask about a folder of documents (RAG) | add the folder once in Knowledge (about 3), then in chat Documents button (1) + switch (1): about 5 once, 2 afterwards | "local index" documented without steps; the embedding model must be installed (the 84 MB file is present here) | PaRuX shows citations and refuses to guess; not compared with LU |
| Create an image | on the empty chat the "Generate an image" tile fills a prompt (1), Send (1); the Create workspace has more control | Create tab, choose a lane, prompt (3+) | image engines download on consent; not run here (no image model in the isolated root) |
| Change generation settings | 1: the sliders button next to the composer | Settings tabs (not verified) | |

Drag and drop of files onto the chat is not implemented in PaRuX (no `AllowDrop` or `DragOver` handler in `src/PaRuX.App`); LU's drop behaviour was not checked. That is a missing convenience, not a measured gap.

## 5. Findings and what was done
| # | Finding | Evidence | Action | New build needed |
|---|---|---|---|---|
| 1 | A model switch hangs when the previous runtime is the Core's own: the port moves to 1235 | 3/3 hangs, port table and `runtime/status` captured; fixed build 6/6 OK | fixed (`LocalAiProcessManager.IsHeldByResidentTextRuntime`), source-contract test | yes (installer) |
| 2 | Streamed `usage` frame missing on the tool-enabled path | absent at 1k/4k before, 5/5 present after | fixed in `ChatProxyService.CreateMcpToolLoopAsync`, test | yes |
| 3 | Idle CPU: Core 2.4-3.5% (UI 1%) vs LU about 1.5% for the whole app | `dotnet-trace` of the idle Core: Playnite session watcher `WindowsProcessSnapshotSource.Capture` about 1.1% (1 s tick), Gaming guard `ReopenAfterGaming` plus portable-root probes about 0.3%, HTTP polling about 0.15% | partly fixed (probe cached for 2 s, about 0.4 points); NOT fixed: the Playnite watcher (a slower tick delays freeing the GPU when a store-launcher game starts; owner decision) | yes |
| 4 | The Core adds about 75-260 ms at TTFT (4k: 861-917 vs 656 ms raw) | section 2 | not changed (trade-offs documented 2026-11-22) | |
| 5 | `--parallel 1` queues concurrent clients | section 3 | measured only; owner decision | |
| 6 | Attach button tooltip said images only | `ChatView.xaml` | tooltip fixed (automation name kept for the UI tests) | yes |

Tests run: `TextRuntimePort*`, `ChatTokenUsage*`, `LocalAiStateRoot*`, `IconOnlyButton*`, portable-root tests (50/50), a wider filter over chat proxy, MCP tool loop, localisation and process manager (279/279); `tools/audit-it-en-literal-pairs.ps1` OK. The full suite and the UI tests were not run.

## 6. Competitors not installed (documented claims only, NOT measured here)
- AnythingLLM and Unsloth Studio: the values come from earlier repo documents, not from this run. `docs/COMPETITOR_MEASURED_2026-11-21.md` (AnythingLLM Desktop 1.20.0, installed then, since removed): engine decode 227 / 213 / 190 tok/s and TTFT 52 ms short, 0.68 s at 4k on the RTX 5080 (f16, ctx 40k, 8.5 GB VRAM), cold start to window 2.99 s, idle 2.6% and 808 MB without a model. Unsloth Studio: install blocked (machine-level toolchain), so everything in `docs/AI_COMPETITIVE_GAPS_2026-11-22.md` about it is a vendor statement; no speed was measured.
- Ollama, LM Studio, llama.cpp CLI: absent.

## 7. Limits (honest)
- One model (4B Q4_K_M), one GPU, one machine, 5 measured runs per cell, on a shared machine. Differences under about 5% mean "no difference shown".
- LU was measured at engine level, window start and idle only. LU chat, RAG and UI latency were NOT measured: driving them would create conversations and indexes in the user's LU data. The LU step counts come from its README, not from clicking.
- The PaRuX first reply was measured over the Core API after the window appeared; UI render time was not measured.
- The "fixed" column comes from a Release build of this branch's `PaRuX.Server` run headless; the installer and artifacts were not rebuilt, so items marked "yes" reach users only with a new build.
- Image generation, voice and training were not benchmarked.
- Idle numbers include a 25-60 s settle; Core calibration jobs can add noise.

## Commands (paths are parameters)
```
python -I tools/competitor-bench/make_rag_corpus.py --out .ai/bench/corpus --files 30 --questions 10
pwsh tools/competitor-bench/Run-ParuxCore.ps1 -CoreDir "C:\Program Files\PaRuX\PaRuX.Server" -DataRoot .ai\bench\root-a -OutDir .ai\bench\out -Corpus .ai\bench\corpus -Label parux451-core
pwsh tools/competitor-bench/Run-Engine.ps1 -Label lu-default-vulkan -Server "C:\Program Files\Locally Uncensored\lu-llama-server.exe" -Model <LU models>\Qwen3-4B-Q4_K_M.gguf -ArgLine "--ctx-size 32768 -ngl 999 --load-mode none" -OutDir .ai\bench\out
python -I tools/competitor-bench/parallel.py --label x --n 3 --length gen --max-tokens 256 --url http://127.0.0.1:PORT/v1/chat/completions --out out.json
python -I tools/competitor-bench/summarize.py ai-<label>.json
```
App start, idle and exit were scripted with `Start-Process`, `Process.TotalProcessorTime` deltas and `PaRuX.exe --exit` under `PARUX_DATA_ROOT`; the idle trace used `dotnet-trace collect --profile dotnet-sampled-thread-time`.
