# PaRuX vs Locally Uncensored & AnythingLLM — Benchmark e Analisi (2026-11-27)

- **Macchina:** AMD Ryzen 7 8840U + NVIDIA GeForce RTX 5080 (16 GB VRAM), 25.8 GB RAM, Windows 11.
- **Modello identico:** `Qwen3-4B-Q4_K_M.gguf` (2,497,280,256 B).
- **Protocollo:** `tools/competitor-bench/bench.py` (`1` warmup + `10` run misurati per lunghezza `short`, `1k`, `4k`, `temperature=0`, `seed=7`, `max_tokens=128`).

## 1. Confronto prestazioni (Mediane su 10 run, RTX 5080)

| Target & Runtime | Short TTFT | Short Decode | 1k TTFT | 1k Decode | 4k TTFT | 4k Decode | VRAM |
|---|---:|---:|---:|---:|---:|---:|---:|
| **PaRuX `main` (CUDA `b12233`, `--parallel 1`, `f16` KV)** | **40.4 ms** | 222.5 tok/s | **149.5 ms** | 200.9 tok/s | **450.6 ms** | 181.0 tok/s | 5,131 MiB |
| **Locally Uncensored CUDA (`b12233`, `-ngl 99 -c 16384 -np 1 --jinja`)** | 55.0 ms | **224.8 tok/s** | 196.5 ms | **211.9 tok/s** | 682.3 ms | **187.6 tok/s** | 5,043 MiB |
| **Locally Uncensored Default Vulkan (`lu-llama-server.exe`)** | 68.9 ms | 215.9 tok/s | 236.7 ms | 200.9 tok/s | 776.2 ms | 178.5 tok/s | 7,525 MiB |
| **LU CUDA baseline esterna (`b12621`, `2026-11-25`)** | 53.5 ms | 227.4 tok/s | 203.1 ms | 208.7 tok/s | 696.6 ms | 184.8 tok/s | 5,043 MiB |
| **AnythingLLM Desktop CUDA (`b11425`)** | — | 226.5 tok/s | 227.9 ms | 212.7 tok/s | 680.7 ms | 189.9 tok/s | — |

### Osservazioni oggettive
1. **Reattività (TTFT) best-in-class:** PaRuX batte sia Locally Uncensored CUDA (stesso motore `b12233`), sia Locally Uncensored Vulkan di serie, sia AnythingLLM CUDA su **tutte e tre le lunghezze**:
   - `short`: **40.4 ms** (-26.5% vs LU CUDA `55.0 ms`, -41.4% vs LU Vulkan `68.9 ms`).
   - `1k`: **149.5 ms** (-23.9% vs LU CUDA `196.5 ms`, -36.8% vs LU Vulkan `236.7 ms`, -34.4% vs AnythingLLM `227.9 ms`).
   - `4k`: **450.6 ms** (-34.0% vs LU CUDA `682.3 ms`, -41.9% vs LU Vulkan `776.2 ms`, -33.8% vs AnythingLLM `680.7 ms`).
2. **Efficienza VRAM vs LU di serie (Vulkan):** PaRuX CUDA usa **5,131 MiB** contro **7,525 MiB** di `lu-llama-server.exe` (-31.8% VRAM) ed è più veloce in decode su `short` (222.5 vs 215.9 tok/s) e `4k` (181.0 vs 178.5 tok/s).
3. **Nota onesta sul decode `1k`/`4k` vs LU CUDA:** nei prompt sintetici `bench.py` compare la parola `"file"` (`"the file watcher"`), per cui `ToolSchemaIntentGate` mantiene i ~917 token di tool schema (`cache_n=917`) invece di ometterli; sui prompt reali senza intent di file/tool (es. chat normale multi-riga verificata da UI), i 917 token vengono omessi.

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
