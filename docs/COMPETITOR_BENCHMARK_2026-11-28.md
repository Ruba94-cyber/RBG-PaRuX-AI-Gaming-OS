# PaRuX vs Locally Uncensored a parità di runtime e modello (2026-11-28)

- **Macchina:** AMD Ryzen 7 8840U + NVIDIA GeForce RTX 5080 (16 GB VRAM), Windows 11.
- **Stesso motore per entrambi:** llama.cpp `b12818` CUDA 13.5 (stesso binario). **Stesso modello:** `Qwen3-4B-Q4_K_M.gguf`.
- **PaRuX:** Core headless (`/api/v1/chat/completions`), profilo di default. **Locally Uncensored CUDA:** `llama-server -ngl 99 -c 16384 -np 1 --jinja`.
- **Protocollo:** 6 prompt *distinti* per carico (mai ripetuti: la speculativa n-gram impara dall'output precedente), Core e raw alternati, `temperature 0`, `enable_thinking false`, `max_tokens 700`, mediane. Nessun uso di UI/schermo.

## 1. Chat e codice (mediane)

| Carico | TTFT PaRuX / LU | tok/s PaRuX / LU | Tempo totale PaRuX / LU |
|---|---:|---:|---:|
| Prosa EN | **47** / 63 ms (-25%) | 227 / 228 | 2,53 / 2,46 s |
| Prosa IT | **48** / 62 ms (-23%) | 230 / 229 | 1,89 / 1,89 s |
| Codice nuovo | **52** / 76 ms (-31%) | 226 / 227 | 3,12 / 3,15 s |
| Modifica di codice (file incollato) | **177** / 257 ms (-31%) | **1.182** / 212 (5,6x) | **0,80** / 3,54 s (-77%) |
| Citazione da documento / RAG | **256** / 434 ms (-41%) | **282** / 202 (+39%) | **2,73** / 3,90 s (-30%) |
| Documento lungo ~4,3k token, 1ª domanda | **360** / 627 ms (-43%) | | |
| Multi-turn, 1º turno | **39** / 69 ms (-44%) | | |
| `bench.py` 1k / 4k TTFT | **118** / 170 ms, **401** / 664 ms | 627 / 217, 333 / 194 | |

Sulla generazione di testo nuovo (prosa, codice da zero) la velocità di decodifica è fissata dagli stessi kernel GPU: a parità di motore e modello è un pareggio (±1%). Il vantaggio di PaRuX viene da ciò che fa sopra il motore:

1. **Decodifica speculativa `ngram-mod` di default** per chat e coding (CUDA, GPU singola, offload completo): output greedy invariato, 5-8x quando la risposta ricopia testo presente nel contesto (modifiche di codice, citazioni, RAG). Sul testo libero costa ~1-2%, perché llama.cpp (anche `b12818`) non permette di spegnerla per singola richiesta: il codice upstream è sotto `#if 0`.
2. **Pipeline Core snella:** niente schemi tool su prompt di semplice scrittura (prima ~900 token in più e chiamate tool spurie da 2-5 s), rispetto di `enable_thinking` / `reasoning_effort` / `top_k` / `min_p` del client, vocabolario del template in cache, probe VRAM non bloccante, profilo e router in parallelo.
3. **Prefill più veloce** (`--ubatch-size 1024`, `--batch-size 2048`): TTFT a freddo inferiore del 23-44%.
4. **Coding agent:** prompt cache attiva (`cache_prompt`, `n_cache_reuse`) e prefisso dell'evidenza stabile tra i turni, così la KV cache viene riusata.
5. **UI Desktop:** rendering markdown incrementale (solo il blocco aperto), flush coalescato a 33-100 ms, nessuna copia del buffer per token: costo lineare invece di O(n²) sulle risposte lunghe.

**Isolamento della cache (scelta di privacy, invariata):** la KV cache è riusata solo all'interno della stessa conversazione e dello stesso progetto. Una domanda senza `conversationId` sullo stesso documento riparte a freddo; il client PaRuX invia sempre il `conversationId`.

### Turni caldi (KV cache già calda, prompt brevi)

Dopo l'ottimizzazione del percorso Core (memo della decisione di routing validata dai file di configurazione, prova di salute dal traffico reale, cache della preferenza di routing), il lavoro del Core prima di inoltrare al motore scende da ~15 a ~7,5 ms. Client a socket grezzo, mediane in ms, Core prima → dopo / LU:

| Carico | PaRuX prima → dopo | LU |
|---|---:|---:|
| Prompt breve | 42 → 20 | 13 |
| Documento lungo, 2ª / 3ª domanda (stessa conversazione) | 30 → 23, 30 → 26 | 16, 16 |
| Multi-turn t1 / t2 / t3 / t4 | 30 → **22**, 38 → **31**, 39 → 33, 42 → **35** | 43, 34, 25, 57 |
| Prosa | 34 → **23** | 25 |

Sui turni caldi con prompt minuscoli resta uno scarto di ~8-10 ms: è il costo del Core stesso (autenticazione, isolamento della cache, controlli del runtime) che LU, senza strato applicativo, non paga. È al di sotto della soglia percepibile e viene recuperato già dal primo prompt non banale.

## 2. Motore aggiornato

`b11871` → `b12818` (CUDA 13.5), stesso modello: `llama-bench` pp512 +8,5%, pp4096 +9,3%, tg128 +4,3%; decodifica +4/+10% sui carichi chat, `ngram-mod` +14/+15% su modifica di codice e citazioni. Tutti i flag emessi da PaRuX sono compatibili e le righe di log lette da `LlamaLoadLogParser` sono presenti.

## 3. Media Studio (ComfyUI v0.44.0, torch 2.14.1+cu130)

Tempi a regime in secondi (mediana di 5 job), primo job tra parentesi. Z-Image 1024² a 8 passi; Wan 2.2 TI2V 5B a 832×480×33. Il riferimento è ComfyUI con le impostazioni di base, come lo usa un concorrente.

| Configurazione | Z-Image nvfp4 | Z-Image bf16 | Wan 5B | SSIM vs base |
|---|---:|---:|---:|---|
| ComfyUI di base | 3,05 (27,8) | 4,91 (16,1) | 18,4 | riferimento |
| **Nuovo default PaRuX** (dynamic VRAM, `--cuda-malloc`, `--preview-method none`) | **2,24 (11,1)** | **4,74 (12,2)** | 18,3 | 1,00 (identico) |
| `--highvram` residente | 2,09 (25,6) | 4,98 (70,6) | 51 (instabile) | 1,00 |
| `--gpu-only` bf16 | | ~965 s/job (non entra in 16 GB) | | |
| `--fast fp16_accumulation` | 2,16 | 3,44 | 18,7 | 0,60-0,93 (non equivalente: resta disattivato) |

- **Guardia del planner:** `--highvram` / `--gpu-only` / `--disable-dynamic-vram` vengono scelti solo se l'intero grafo entra con 2 GiB di margine.
- **Preset video rapido FastWan 2.2 TI2V 5B** (pesi ufficiali FastVideo convertiti con `tools/convert-fastwan-to-comfy.py`): 4 passi, cfg 1, euler, `--reserve-vram 3.0`, 5,4 s contro 18,4 s (3,4x). È opzionale; il default di qualità a 20 passi resta invariato.

## 4. Non verificato

- Nessuna prova dalla UI Desktop (schermo riservato ad altro lavoro): coding agent end-to-end, fluidità del rendering a 1000+ tok/s e test FlaUI non eseguiti.
- Wan alla risoluzione di default 1280×704×49 non misurato.
- La qualità di FastWan è valutata solo a vista.
- Suite `PaRuX.Server.Tests` completa: 16.742 passati, 9 ignorati, 1 fallito (`MediaPrewarmServiceTests.An_attempt_that_outlives_its_budget...`, test di timing instabile e preesistente: fallisce circa 1 volta su 3 anche da solo; file non toccati in questa sessione).
