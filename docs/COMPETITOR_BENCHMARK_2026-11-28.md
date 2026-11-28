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
- Wan alla risoluzione di default 1280×704×49: misurato nella sezione 5.4 (20 passi, senza confronto SSIM).
- La qualità di FastWan è valutata solo a vista.
- Suite `PaRuX.Server.Tests` completa: 16.742 passati, 9 ignorati, 1 fallito (`MediaPrewarmServiceTests.An_attempt_that_outlives_its_budget...`, test di timing instabile e preesistente: fallisce circa 1 volta su 3 anche da solo; file non toccati in questa sessione).

## 5. Sessione perf3: tentativi a parità di motore e modello (2026-11-28)

Stessa macchina, `b12818` CUDA 13.5, `Qwen3-4B-Q4_K_M`, `temperature 0`, prompt distinti, server interlacciati. La GPU è condivisa con un altro lavoro: i valori assoluti oscillano tra sessioni (il TTFT del server grezzo andava da 10 a 17 ms), quindi ogni confronto è interno alla stessa esecuzione. Solo numeri misurati; "scartato" significa peggiore o senza beneficio.

### 5.1 Decodifica su testo libero (tok/s di decodifica dal server, mediana su 8 prompt per carico)

| Tentativo | prosa EN | prosa IT | codice nuovo | Esito |
|---|---:|---:|---:|---|
| `llama-server` ufficiale CUDA 13.5, senza speculativa (riferimento) | 217,5 | 217,1 | 212,9 | |
| Build locale `b12818` + patch, CUDA 12.8: `ngram-mod` attiva sul server, richiesta con `"speculative.disabled": true` | 211,2 | 211,5 | 206,5 | patch funzionante, **scartata come motore** |
| Stessa build, `ngram-mod` attiva sulla richiesta | 208,6 | 210,9 | 204,7 | |
| Stessa build, senza `--spec-type` | 209,8 | 211,4 | 201,8 | |
| Ufficiale con `--spec-type ngram-mod` (default) contro senza, prova 2 | -1,5% | -1,8% | -1,8% | costo reale ma piccolo |
| Idem, prova 3 | -0,4% | -2,0% | -3,6% | |
| `--spec-ngram-mod-n-match 16 --spec-ngram-mod-n-min 16` (prova 2) | -3,5% | -1,6% | -0,6% | scartato (non migliora) |
| `--spec-ngram-mod-n-match 32 --spec-ngram-mod-n-min 64 --spec-ngram-mod-n-max 128` (prova 3) | -1,3% | -1,6% | -0,6% | scartato (non migliora) |

- **Patch** (`tools/llama-patches/b12818-per-request-speculative-disable.patch`, 3 file, 34 righe): aggiunge il campo di richiesta `speculative.disabled` (chiave piatta con il punto; la forma annidata viene ignorata) e porta a 0 la lunghezza massima di bozza per quella richiesta. Con la bozza spenta il costo della speculativa sparisce (211,2 contro 209,8 della stessa build senza speculativa): il guadagno massimo rispetto alla speculativa attiva è lo 0,3-1,2% sul testo libero.
- **Perché non diventa il motore incluso:** (1) la build disponibile qui usa CUDA 12.8 (l'unico toolkit installato) ed è il 2,6-5,2% più lenta del binario ufficiale CUDA 13.5, quindi nonostante la patch resta sotto il riferimento; servirebbero un toolkit 13.5 (circa 3 GB) e un'altra build di circa 30 minuti per, al massimo, pareggiare; (2) il motore incluso deve essere un artefatto con hash fissato nel lock e scaricabile dalla stessa origine delle release ufficiali: un binario costruito qui non ha un'origine da fissare; (3) il tetto del beneficio è circa l'1%. Resta una patch documentata, non spedita.
- Build locale: cmake + Visual Studio 2022 + CUDA 12.8 (`-DGGML_CUDA=ON -DCMAKE_CUDA_ARCHITECTURES=120`). `nvml.h` non è nel toolkit installato ed è stato preso dal pacchetto ufficiale NVIDIA `cuda_nvml_dev` 12.8.90 (SHA-256 verificato contro il manifest NVIDIA). Gli output greedy della build senza speculativa sono identici a quelli ufficiali (24/24). Con `--spec-type` attivo sul server, anche con la bozza spenta per richiesta, gli output su testo libero divergono da quelli del server senza speculativa (0/24 identici), come già visto con il server ufficiale: è la numerica del percorso di verifica, non un errore.
- `--spec-draft-p-min` non è applicabile: è un parametro dei modelli bozza, il percorso `ngram-mod` (`draft_one`) legge solo `n_min`, `n_max` e `n_match`. Il costo di tokenizzazione/campionamento del Core non è stato isolato.

### 5.2 TTFT a caldo, prompt minuscolo (Core con `PARUX_CHAT_PHASE_TRACE=1`)

Client a socket grezzo, mediane su 30-80 turni alternati. Percorso a bassa latenza ("hi there N"): Core 18,5-19,3 ms contro 10,4-10,7 ms del server grezzo (+8-9 ms, coerente con la sezione 1). Il lavoro prima di inoltrare al motore è circa 8 ms ripartiti in 14 fasi da 0,2 a 1,4 ms, nessuna dominante (ms dall'inizio della richiesta): pipeline e modalità 0,3, runtime pronto 1,0, scorciatoie/skill 1,6, preferenza di instradamento 2,0, memo di instradamento 2,3-3,8 (di cui circa 0,6 ricerca del PID del runtime, 0,4 lettura di due file piccoli, 0,3 salute), finestra di contesto 4,0, retrieval 4,8-7,2, preparazione motore 5,6, costruzione del payload 6,7-9,0, risposta HTTP del motore 8,1-10,4.

- **Tenuto:** cache validata da `stat` per i file del ruolo e del modello attivi (`StampedTextFileCache`, usata dal controllo di residenza a ogni turno): 88 us contro 24 us a lettura (due letture per turno), con guardia contro scritture ravvicinate (250 ms), così uno swap di modello non viene mai perso. Guadagno reale ma di circa 0,1-0,4 ms, non risolvibile nel rumore del TTFT end-to-end: non lo conto come riduzione dello scarto.
- **Non fatto per rischio/beneficio:** cache a TTL della ricerca del PID (0,6 ms, tocca la prova di identità del processo); retrieval saltato sui prompt banali (cambia il comportamento della memoria). `SocketsHttpHandler` è già a `NoDelay` con connessioni in pool; non c'è `Expect: 100-continue` (corpo di lunghezza nota); il middleware è confronto di stringhe.
- **Pista nuova, non implementata:** sui turni che mantengono gli schemi degli strumenti (prompt con intento di strumento, per esempio "di' la parola 3 in francese") il Core conta due volte il prompt reso, con due giri HTTP in serie verso il motore (adattamento della finestra circa 5,5 ms e conteggio iniziale del ciclo strumenti circa 3-4 ms): Core 34-36 ms contro 11,5 ms grezzo. Riusare il primo conteggio esatto per il primo giro farebbe risparmiare 3-4 ms su quei turni; richiede di provare che i due payload producono lo stesso conteggio.

### 5.3 Prefill e contesto lungo (`llama-bench`, `-fa 1`, Qwen3-4B Q4_K_M)

| Prova | Risultato | Esito |
|---|---|---|
| `--ubatch-size` 256 / 512 / 1024 / 2048 (batch 2048), pp4096 | 10.582 / 12.468 / **13.319** / 13.110 tok/s; pp12000: 8.824 / 10.130 / **10.695** / 10.689 | 1024 resta il valore giusto |
| Cache KV `q8_0` K+V contro `f16` | pp4096 12.830 contro 13.458 (-4,7%), tg128 232,9 contro 245,2 (-5,0%); a profondità 8000: pp 8.399 contro 8.789 (-4,4%), tg 178,0 contro 182,3 (-2,4%) | scartato: più lento, risparmia solo VRAM (qualità non misurata perché già non conviene) |
| `q8_0` solo sulla K | pp4096 13.008 (-3,3%), tg128 234,8 (-4,2%), tg a profondità 8000 164,9 (-9,5%) | scartato |
| `--load-mode` auto / none / mmap / dio / mlock, avvio fino a pronto (ms, 2 prove) | 2332, 2170 / **1471, 1545** / 2157, 1864 / 1746, 1449 / 1539, 1493 | `none` (già usato dal Core) è tra i migliori; `--no-mmap` non esiste più in b12818; nessun effetto a regime con offload completo |
| Server grezzo `--poll 100 --prio 2` contro predefinito, TTFT a caldo (80 turni alternati) | 16,88 contro 16,66 ms | nessun effetto, scartato |
| `-b 2048 -ub 1024` contro predefinito, TTFT a caldo | 16,86 contro 16,84 ms | nessun costo sui prompt minuscoli |

Flash-attention è già attiva (`--flash-attn on`). L'affinità dei thread non è stata provata: la decodifica è su GPU.

### 5.4 Media (ComfyUI 0.44.0, torch 2.14.1+cu130, RTX 5080, nuovo default PaRuX)

| Prova | Risultato | Esito |
|---|---|---|
| **Wan 2.2 TI2V 5B alla risoluzione di default 1280x704x49, 20 passi** | **71,7 s a regime** (71,67 e 71,64), 95,5 s il primo job, VRAM di picco 15.294 MiB | misura nuova (prima mancante) |
| Z-Image nvfp4 1024x1024, 8 passi, base di oggi | 2,20 s a regime (2,18-2,22), primo job 25,5 s con modello non in cache disco | riferimento |
| Z-Image bf16, base di oggi | 4,81 s a regime, primo job 14,5 s | riferimento |
| `torch.compile` (inductor, triton-windows 3.9) su Z-Image bf16 | 6,35 s a regime (+32%), primo job 83,8 s, SSIM contro la base 0,854 (seed 1000) e 0,988 (seed 1001) | scartato: più lento e sotto la soglia 0,98 |
| `torch.compile` su Z-Image nvfp4 | fallisce nei kernel di quantizzazione di ComfyUI (`Expect size to be a plain tuple of ints`) | scartato |
| SageAttention 1.0.6 (triton) su Z-Image nvfp4 | 57-62 s per job (circa 26 volte più lento); immagine identica alla base (SSIM 1,00: il percorso sage non incide su questo modello) | scartato |

Il nodo `TorchCompileModel` di ComfyUI disattiva la VRAM dinamica (`disable_dynamic`), il che spiega perché il bf16 (non entra in 16 GB) peggiora. Il precaricamento dei modelli in idle per il tempo di avvio a freddo non è stato toccato: resta quello già esistente (`MediaPrewarmService`).

### 5.5 Non verificato in questa sessione

- Nessuna prova del motore patchato con CUDA 13.5 (toolkit non installato): il tetto di circa l'1% è quello misurato con CUDA 12.8.
- Il guadagno di `StampedTextFileCache` non è misurabile end-to-end (rumore del TTFT di 1-2 ms); misurato solo sulla singola lettura.
- Qualità del KV `q8_0` non misurata (scartato per velocità).
- Wan 1280x704x49 senza confronto SSIM e senza il preset FastWan (i pesi convertiti non sono più sul disco).
