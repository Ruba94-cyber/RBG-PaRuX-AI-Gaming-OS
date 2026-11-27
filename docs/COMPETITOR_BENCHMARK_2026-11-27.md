# PaRuX vs Locally Uncensored & AnythingLLM — Benchmark e Analisi (2026-11-27)

- **Macchina:** AMD Ryzen 7 8840U + NVIDIA GeForce RTX 5080 (16 GB VRAM), 25.8 GB RAM, Windows 11.
- **Modello identico:** `Qwen3-4B-Q4_K_M.gguf` (2,497,280,256 B).
- **Protocollo:** `tools/competitor-bench/bench.py` (`1` warmup + `10` run misurati per lunghezza `short`, `1k`, `4k`, `temperature=0`, `seed=7`, `max_tokens=128`).

## 1. Confronto prestazioni (Mediane su 10 run, RTX 5080)

| Target & Runtime | Short TTFT | Short Decode | 1k TTFT | 1k Decode | 4k TTFT | 4k Decode | VRAM |
|---|---:|---:|---:|---:|---:|---:|---:|
| **PaRuX `v4.7.0` (CUDA `b12233`, `--parallel 1`, `f16` KV, `--poll 100`)** | **31.3 ms** | 218.7 tok/s (srv 214.4) | **124.5 ms** | 207.9 tok/s (srv 204.8) | **404.9 ms** | 184.5 tok/s (srv 182.0) | 5,131 MiB |
| **Locally Uncensored CUDA (`b12233`, `-ngl 99 -c 16384 -np 1 --jinja`)** | 55.0 ms | **224.8 tok/s** (srv 218.6) | 196.5 ms | **211.9 tok/s** (srv 205.8) | 682.3 ms | **187.6 tok/s** (srv 182.7) | 5,043 MiB |
| **Locally Uncensored Default Vulkan (`lu-llama-server.exe`)** | 68.9 ms | 215.9 tok/s | 236.7 ms | 200.9 tok/s | 776.2 ms | 178.5 tok/s | 7,525 MiB |
| **LU CUDA baseline esterna (`b12621`, `2026-11-25`)** | 53.5 ms | 227.4 tok/s | 203.1 ms | 208.7 tok/s | 696.6 ms | 184.8 tok/s | 5,043 MiB |
| **AnythingLLM Desktop CUDA (`b11425`)** | — | 226.5 tok/s | 227.9 ms | 212.7 tok/s | 680.7 ms | 189.9 tok/s | — |

### Osservazioni oggettive (`v4.7.0`)
1. **Reattività (TTFT) e Prefill (`pp`) best-in-class:** PaRuX `v4.7.0` batte sia Locally Uncensored CUDA (stesso motore `b12233`), sia Locally Uncensored Vulkan di serie, sia AnythingLLM CUDA su **tutte e tre le lunghezze**:
   - `short`: TTFT **31.3 ms** (-43.1% vs LU CUDA `55.0 ms`, -54.6% vs LU Vulkan `68.9 ms`), prefill **3,126.1 tok/s** vs `945.5 tok/s` (+230.6%).
   - `1k`: TTFT **124.5 ms** (-36.6% vs LU CUDA `196.5 ms`, -47.4% vs LU Vulkan `236.7 ms`, -45.4% vs AnythingLLM `227.9 ms`), prefill **13,045.1 tok/s** vs `11,841.7 tok/s` (+10.2%).
   - `4k`: TTFT **404.9 ms** (-40.7% vs LU CUDA `682.3 ms`, -47.8% vs LU Vulkan `776.2 ms`, -40.5% vs AnythingLLM `680.7 ms`), prefill **12,473.3 tok/s** vs `11,579.0 tok/s` (+7.7%).
2. **Decode allineato & tempo di completamento totale inferiore:** grazie al fix di `ToolSchemaIntentGate` (niente più +917 token di tool schema sui prompt `1k`/`4k` con nomi generici nel corpo incollato: `prompt_n` `1,213`/`4,641` vs LU `1,193`/`4,637`) e a `--poll 100` con KV `f16`, la velocità pura del motore (`dec_srv`) è entro lo **0.4%–1.9%** di LU CUDA (`214.4 / 204.8 / 182.0` vs `218.6 / 205.8 / 182.7 tok/s`) e il tempo totale dall'invio al completamento di 128 token è **inferiore su PaRuX su tutti i tagli** (su 4k: `1.099 s` vs `1.364 s`, **-265 ms**).
3. **Efficienza VRAM vs LU di serie (Vulkan):** PaRuX CUDA usa **5,131 MiB** contro **7,525 MiB** di `lu-llama-server.exe` (-31.8% VRAM) ed è più veloce su tutte le metriche TTFT e decode (`218.7 / 207.9 / 184.5` vs `215.9 / 200.9 / 178.5 tok/s`).

## 2. RAG & Conoscenza locale (PaRuX Core CUDA)
- **Corpo sintetico (`parux_rag.py`, 31 file / 210 chunk):** indicizzazione completata in **1.57 s**, **`hit@5 = 10/10`**, **`30/30` risposte corrette e citate**, TTFT ~95–101 ms.
- **Corporate Lite Offline (`measure-corporate-lite.ps1 -EnforceAcceptance`):** `319` file / `1,938` chunk in **87.5 s**, ricerca `exact-id` p50 `6.5 ms`, `semantic` p50 `126.8 ms`, `hybrid` p50 `723.8 ms` (`0` errori), Chat grounded `4/4` + astensione, fallback low-memory a `Qwen3-0.6B-Q8_0` e hard-kill recovery verdi.

## 3. Confronto funzionalità: PaRuX vs Locally Uncensored 3.1.x & AnythingLLM 1.20.0

| Funzionalità | PaRuX (`main`) | Locally Uncensored 3.1.x | AnythingLLM 1.20.0 |
|---|---|---|---|
| **Chat locale multi-turn + prompt cache + HTML Canvas** | **DONE** (verificato da UI reale, `192.9–204.0 tok/s`) | Sì | Parziale (no Canvas locale equivalente) |
| **Hybrid RAG + Reranker multilingue (`E5` + `mmarco`) + Benchmark interno** | **DONE** (`hit@5 10/10`, `30/30`, IT+EN da UI reale) | Base (embedder opzionale, no benchmark) | Sì (no benchmark deterministico in-app) |
| **Modalità Corporate Offline certificata (zero egress, fallback RAM, log puliti)** | **DONE** (`measure-corporate-lite.ps1` PASS + UI IT/EN reale) | No | No |
| **Training Lab GPU (SFT/QLoRA/DoRA/DPO/ORPO/KTO/GRPO/GSPO/16-bit/Embedding + GGUF/imatrix)** | **DONE** (verificato su RTX 5080) | No | No |
| **3D Studio (Text-to-3D + Image-to-3D + export OBJ/STL/USDZ)** | **DONE** | Sì | No |
| **Media Studio (ComfyUI import arbitrari, LoRA stack, inpaint, Image/Video)** | **DONE** (UI/import); **PARTIAL** (warm `/free` eviction solo unit test in questo giro) | Sì (immagini/video) | No |
| **Coding Agent con worktree, sandbox Python, undo patch, Steer/Rewind** | **DONE** | Parziale (coding) | No |
| **Gaming Hard-Off + Playnite Console Mode + Controller tools** | **DONE** (unico con hard-off VRAM verificato per giochi) | No | No |
| **App Android companion con pairing QR** | **DONE** (`BUILD SUCCESSFUL` + unit test verdi) | Sì | No |
| **Smart Router multi-ruolo + fallback Uncensored adattivo** | **DONE** | Manuale | Manuale |
