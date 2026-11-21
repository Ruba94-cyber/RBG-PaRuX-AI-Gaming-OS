# Measured AI comparison on one PC (2026-11-21)

Scope: PaRuX 4.4.0 against Locally Uncensored 3.1.1 (LU) and AnythingLLM Desktop 1.20.0 on one machine and one model file. Raw CSV/JSON/logs and the scripts are under `.ai/artifacts/competitors/results-20261121/` (git-ignored). Nothing here is a claim of superiority; the headline is a PaRuX deficit (see "Where PaRuX loses").

## Machine and conditions
- Windows 11 Home 10.0.26320, Ryzen 7 8840U class CPU (16 logical), 25.8 GB RAM, RTX 5080 16 GB (driver 621.24) + Radeon 780M iGPU (shared memory). Nobody else used the GPU during runs; the desktop was shared with the operator's Claude app.
- Model: `Qwen3-4B-Q4_K_M.gguf`, 2,497,280,256 bytes, SHA-256 `7485FE6F...4FDF5`. PaRuX (both configs) and LU load this exact file. AnythingLLM's engine cannot load a local path (its Lemonade registry treats the path as a Hugging Face repo), so it uses the `unsloth/Qwen3-4B-GGUF` Q4_K_M file (2,497,281,312 bytes, same quant, 1 KB different header); its default `Qwen3-4B-GGUF` entry is Q4_0 and was NOT used.
- Prompts (`bench.py`): short (about 20 tokens), about 1.2k tokens, about 4.6k tokens of synthetic prose plus "summarise in two sentences"; each run has a unique nonce prefix, temperature 0 (API runs), `max_tokens` 128, 1 warm-up + 5 measured runs per length. Median (min-max) reported.
- TTFT = time from request send (HTTP) or Send click (UI) to the first streamed token of any kind. Decode tok/s = streamed chunks per second after the first token (API) or app-reported output tokens over wall time (LU UI).

### Backends (fairness)
| App | Engine | Build | Device actually used | Ctx / KV | Thinking |
|---|---|---|---|---|---|
| PaRuX portable, as shipped | llama.cpp Vulkan (bundled `b10549`, later auto-updated to `b12599`) | b10549 / b12599 | `--device Vulkan0` = Radeon 780M iGPU (5080 idle, 0 MiB) | 16384, q8_0 | low/off via PaRuX routing |
| PaRuX configured (CUDA runtime already installed in the data root) | llama.cpp CUDA 13.5 | b12233 | RTX 5080 (4.0 GB) | 16384, q8_0 | low/off |
| LU 3.1.1 | its own `lu-llama-server` (Vulkan build, `0.8.0-dev build 1`, commit 8642541) | custom | RTX 5080 (7.1-7.8 GB) | 32768, f16 | UI Think switched OFF |
| AnythingLLM 1.20.0 | Lemonade `lemond 2026.40.0` driving llama.cpp CUDA 13.4 | b11425 | RTX 5080 (8.5 GB) | 40960, f16 | default (reasoning tokens counted) |
Contexts differ per app because each app chooses its own default; they were not forced equal, so VRAM rows are "as shipped", not like for like. LU UI numbers include DOM rendering; PaRuX and AnythingLLM-engine numbers are HTTP-level (the Core API `/api/v1/chat/completions` that the PaRuX UI also uses; AnythingLLM through lemond `/api/v1/chat/completions`). UI-level timing was NOT measured for PaRuX or AnythingLLM chat.

## 1. Cold start to titled window, graceful shutdown (5 runs each)
| App | First titled window (median, min-max) | Main window | Shutdown |
|---|---|---|---|
| PaRuX, fresh portable profile (onboarding not done) | 1.06 s (1.05-1.09) | same window | 0.86 s median (0.73-0.87; one 5.1 s outlier), `PaRuX.exe --exit`, 0 leftover processes |
| PaRuX, configured data root (onboarding done) | 1.38 s (1.26-1.40) | same | 0.73 s (0.71-0.77), 0 leftover |
| LU | 0.17 s (0.15-0.19) first (small) window | 0.82 s (0.80-0.89) | closing the window only hides to tray; no CLI exit; had to Stop-Process (8 s timeout shown is the wait, not a graceful exit) |
| AnythingLLM (first-run onboarding screen) | 2.99 s (2.89-3.64) | same | close is intercepted ("hidden to tray"); processes stopped with Stop-Process |
LU and AnythingLLM load/start their model engine in the background after the window; PaRuX does too (provider ready after 6.7-11.5 s, measured through `/api/v1/health`).

## 2. First token and decode speed (API/engine level unless noted)
| Setup | Short TTFT | 1k TTFT | 4k TTFT | Decode tok/s (short / 1k / 4k) |
|---|---|---|---|---|
| PaRuX as shipped, b10549, 780M iGPU | 0.32 s | 3.9 s | 18.8 s | 29.0 / 28.3 / 22.9 |
| PaRuX as shipped, b12599, 780M iGPU | 0.34 s | 3.0 s | 12.5 s | 30.6 / 28.7 / 23.5 |
| PaRuX configured, CUDA b12233, 5080 | 0.087 s | 0.28 s | 0.66 s | 193 / 186 / 178 |
| AnythingLLM lemond CUDA b11425, 5080 | 0.052 s | 0.23 s | 0.68 s | 227 / 213 / 190 |
| LU UI (DOM timing), 5080 | 0.15 s | 0.30 s | 1.16 s | 162 / 157 / 137 (UI-derived, lower than engine) |
| Reference: PaRuX engine b12599 Vulkan1 (5080), PaRuX flags | 0.054 s | 0.16 s | 0.59 s | 194 / 181 / 163 |
| Reference: PaRuX engine b12599 Vulkan0 (780M) | 0.25 s | 3.0 s | 12.2 s | 32 / 28 / 23.5 |
| Reference: CUDA b12233 (PaRuX flags) | 0.054 s | 0.17 s | 0.57 s | 214 / 192 / 181 |
| Reference: LU engine with LU flags | 0.060 s | 0.22 s | 0.78 s | 210 / 196 / 173 |
Notes: PaRuX adds about 920 tokens of instructions/context to every request (`prompt_n` 16 + `cache_n` 929 on a 12-token question); it is cached after the first request (first request on a cold server: 3.4 s). The 780M vs 5080 gap is about 6.5x decode and 10-20x prefill. LU's 4k TTFT includes UI work; its in-app "in" token counter reports far fewer tokens than sent (cache reuse), so LU's UI TTFT may include partial prompt reuse; a 20k-character probe with a secret in the middle was answered correctly.

## 3. Idle cost (60 s settle, 30 s window; share of one core, working set of the whole tree)
| App | No model | Model loaded |
|---|---|---|
| PaRuX (portable, iGPU model) | 1.3% / 458 MB (3 procs) | 1.8% / 5.3 GB (llama-server 4.6 GB mmapped) |
| PaRuX configured (CUDA) | not measured | 3.5% / 4.5 GB (one sample; the other config gave 1.8%, so idle CPU is noisy 1.3-3.5%) |
| LU | 0.21% / 632 MB (engine process killed; no UI unload exists) | 1.7% / 8.0 GB (llama-server 7.4 GB) |
| AnythingLLM | 2.6% / 808 MB | 0.8% / 3.7 GB |

## 4. VRAM with the model loaded (nvidia-smi, system-wide while nothing else used the GPU)
PaRuX as shipped: 0 MiB on the 5080 (iGPU dedicated 3.4 GB + 0.8 GB shared). PaRuX configured: 4,070 MiB. LU: 7,116-7,821 MiB. AnythingLLM: 8,516 MiB. Differences are mostly context size and KV type (see backends table).

## 5. Document Q&A (3 synthetic markdown files, 5 factual questions, same Qwen3-4B Q4_K_M)
| App | Correct | Median time to first token / to done | Path | Citations |
|---|---|---|---|---|
| AnythingLLM (UI via CDP) | 5/5 | 1.27 s / 1.60 s | files attached as workspace context ("Added as context", no chunking for tiny files) | shows "Sources" |
| LU (UI via CDP) | 5/5 | 0.14 s / 0.25 s | files attached inline to the chat; the RAG feature needs an 84 MB embedding model that was not installed | none shown |
| PaRuX | NOT MEASURED | | the Core RAG routes are desktop-owner gated (`RequireDesktopOwner`, 401/403 for a bootstrap token) and I did not script the WinUI folder picker | |
Indexing time and a real multi-chunk RAG run were not measured for any app. No tool/agent-style task comparison was run.

## Where PaRuX loses
1. Out-of-the-box GPU choice (the large one). The portable build without the CUDA runtime launches `--device Vulkan0`, which is the Radeon 780M on this dGPU+iGPU laptop: about 29 tok/s and 12-19 s for a 4k prompt instead of about 190 tok/s and 0.6 s on the 5080. LU and AnythingLLM both put the model on the 5080 by default. Cause: `src/PaRuX.Server/RuntimeLaunchOptimizer.cs` lines 478-479 add a hard-coded `--device Vulkan0` for any non-CUDA runtime when the profile has no device, and `src/PaRuX.Server/LocalAiProcessManager.cs` line 1690 does the same for the assistant runtime. With a CUDA runtime in the data root PaRuX is competitive (table 2). A device-ranking step (prefer the adapter with the most dedicated VRAM, ignore shared-memory iGPUs) is the missing piece.
2. About 920 extra prompt tokens per request (skills/context pack): `ChatProxyService.ApplyContextPack` and the skill-instruction injection (lines about 1341-1411 of `src/PaRuX.Server/ChatProxyService.cs`). It costs 3.4 s on the first request on a slow GPU and some ms afterwards (short TTFT 87 ms vs 54 ms raw engine) and consumes context.
3. Startup: 1.06-1.38 s to the main window vs LU 0.8 s (LU shows a first window at 0.17 s) - PaRuX wins only against AnythingLLM (3 s).
4. Idle CPU with and without a model (1.3-3.5%) is above LU's 0.2% without a model; likely UI/Core polling (not profiled this run).
5. Decode on the 5080 with PaRuX's own flags (q8_0 KV, flash-attn, `--poll 45`) is 162-194 tok/s vs 213-227 for AnythingLLM's engine and CUDA b12233; the q8 KV/ctx 16k settings and the Vulkan build account for part of it. Not isolated.
Where PaRuX is not behind: graceful shutdown (0.7 s, no leftover processes, LU/AnythingLLM only hide to tray), RAM without a model (458 MB vs 632/808 MB), VRAM (4.0 GB vs 7.8/8.5 GB, but with a smaller context), configured TTFT at 1k/4k (0.28/0.66 s vs LU UI 0.30/1.16 s, different measurement level).

## Blocked / not measured
- Unsloth Studio: BLOCKED-EXTERNAL. The official `install.ps1` (https://unsloth.ai/install.ps1, SHA-256 `90569ABD...552F1`, saved under `.ai/artifacts/competitors/unsloth-install/`, not executed) delegates to a setup that installs Git, CMake, Visual Studio Build Tools and CUDA through winget; that is machine-level, multi-GB and may need elevation, which exceeds the "no system changes / no elevated installs" rule. No startup, speed, RAG or fine-tune smoke numbers exist for it. (A `UNSLOTH_NO_TORCH` inference-only install in an isolated folder might avoid it but was not verified.)
- PaRuX document Q&A / indexing time; LU RAG with the embedding model; AnythingLLM multi-chunk RAG and indexing time; tool/agent task; UI-level TTFT for PaRuX and AnythingLLM; Playnite vs PaRuX on a 2,000-title library (not started: AI comparison and installs consumed the session).
- PaRuX and LU startup were measured on different profile states (PaRuX fresh and configured; LU after setup; AnythingLLM before onboarding). AnythingLLM idle "no model" ran with the onboarding screen.
- LU/AnythingLLM "leftover process" counts in the raw JSON are a race (read immediately after a forced stop) and should be ignored.
- GPU load/power and iGPU memory were not logged beyond the per-process GPU memory counters in the idle JSON.

## Licences, terms and installs
- AnythingLLM Desktop 1.20.0: official signed installer (`https://cdn.anythingllm.com/latest/AnythingLLMDesktop.exe`, Mintplex Labs Inc signature valid, SHA-256 `63DAD00F...25F55`), silent per-user install (`/S`), no licence page was shown; the app then downloaded its Lemonade engine, llama.cpp and ffmpeg from `cdn.anythingllm.com`. Its first-run onboarding was completed with the local engine and an empty optional e-mail.
- LU 3.1.1: already installed; only its first-run wizard (Get Started, Skip x3) was clicked; no licence text.
- Model files were not downloaded for LU/PaRuX (copied the existing GGUF into LU's models folder); AnythingLLM pulled `unsloth/Qwen3-4B-GGUF` (Q4_0, then Q4_K_M) from Hugging Face via its own engine.
- No accounts, payments or credentials were entered. LU and AnythingLLM were left installed.

