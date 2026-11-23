# Integration report — 2026-11-23 (branch `integration/20261123-b`)

Ogni numero qui sotto è **misurato** salvo dove scritto "non verificato". Nessun dato dei file aziendali del proprietario è incluso.

## A. Stato generale
- Integrate in ordine le PR #12–#16, poi #8 e #9, nel branch di integrazione (nessun merge su `main`).
- Fix CS0029 in `ManualGameLibraryView.cs` (parametro lambda `_` → `(sender, args)`); gli errori XAML WMC0010/WMC0001 erano a cascata, nessun attributo `Loc.*` rimosso.
- Fix CS0136 in `GameLaunchProfile.cs` (difetto già presente in PR #9).
- Conflitti risolti in `KnowledgeService.cs` / `SmartRouterService.cs` (PR #8).

## B. Build prodotte (locali, in `wt-int/artifacts`, NON in git)
| Pacchetto | Dimensione | SHA-256 |
|---|---|---|
| Corporate Lite LowMemory (Qwen3-0.6B) | 1.062.876.162 B | `e2b68e59c1062a5c63929dcabd08eb54168e8971fcdeeccbe9286bfc2561b2d8` |
| Corporate Lite 4B (Qwen3-4B) | 2.798.373.676 B | `a84254984e19acbe994f3c6d3c20d23f094e77a86fc46e2cf8b0b06cb5f44923` |
- Smoke di build OK (prova reale di caricamento ONNX con embedding/rerank).
- LowMemory: avvio core 5,6 s, RAM idle 1267 MB, indicizzazione di 18 file di test: 10.947 chunk, 0 falliti, picco 2314 MB, 1009 s.
- **Non prodotti**: Portable, Desktop Installer, MSI (richiede consenso EULA WiX v7), APK. **Non firmati** (nessun certificato autorizzato).

## C. Test
- Suite xUnit filtrate sui moduli toccati verdi; tokenizer Unigram C# = riferimento HF con 0 differenze su set IT/EN/misto.
- Accettazione su documenti reali del proprietario (34 domande IT su evidenza EN): 4B ≈ 19/24, 0.6B ≈ 15–17/24 sulle domande numeriche/multi-parte. File e risposte restano **solo locali**.
- Non eseguiti: FlaUI/UI automation, benchmark completi, i 10 scenari Corporate in forma automatizzata completa (parzialmente coperti: offline, no GPU, RAM limitata, non-admin, scarico memoria idle verificato).

## D. Corporate
- Portable/offline, nessuna telemetria, modelli caricati a richiesta, scarico ONNX/llama a idle (llama `--sleep-idle-seconds 300`, ONNX 120 s).
- Nuove capacità di indicizzazione: CAN ASC/BLF (J1939 PGN/SA, DM1), DBC, OLE (.doc/.ppt/.msg), macro VBA, e-mail con allegati, lettore universale di script (qualsiasi linguaggio).
- Embedding multilingue `multilingual-e5-small` int8 (soglia calibrata 0,45), file pinnati con revisione e SHA-256.
- Confronto con AnythingLLM: **non eseguito** (solo pianificato).

## E. Gaming
Integrato tramite le PR sopra (Playnite, pad, mod, ecc.); nessuna validazione fisica aggiuntiva in questa sessione.

## F. Prestazioni
Solo i numeri della sezione B. Nessun benchmark comparativo.

## G. Sicurezza
- Nessun segreto/certificato nel repo; dati corporate/CAN/SPN rimossi dagli script (domande reali spostate in file locale fuori repo).
- File `.enc` non toccati. Nessun fork/pubblicazione con altro account.

## H. GitHub
- Push **bloccato**: account attivo `teamdtsiveco-code` con permesso READ (403). Nessun tag/release. Branch e bundle locale pronti.

## I. Limiti / prossimi passi
1. Reranker multilingue (mmarco-mMiniLMv2 int8): INTEGRATO a fine sessione (provider con fallback sull'inglese, file pinnati con SHA-256, endpoint /api/v1/reranker/multilingual/engine, script di build, 2 test verdi con modello reale). NON ancora misurato end-to-end: serve ricostruire un pacchetto e rieseguire le 34 domande di accettazione.
2. Scelta modelli finale (candidati: Qwen3-4B-Instruct-2507, Qwen3.5-4B, Gemma-4-E4B; piccoli: Qwen3.5-2B, Qwen3-1.7B) non completata; pacchetti attuali usano 4B e 0.6B.
3. Ricostruire i pacchetti finali con e5 e misurare; build Portable/Installer, hash, firma.
4. Confronto AnythingLLM; test UI; verifica primo avvio automatico sul pacchetto.
5. Non supportati: .pst/.ost/MF4, macro in .ppt legacy.
6. Merge su `main` solo dopo verifica e approvazione.
