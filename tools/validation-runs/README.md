# Validation runs (developer tools)

Scripts used on 2026-11-23 to validate the Corporate packages on a real PC. They contain no data of any real file or company: the documents are generated (`tests/corporate/make-*-samples.py`) and anything private (own files, questions about them) is read from local files that are **not** part of the repository.

| Script | Purpose |
|---|---|
| `start-clean.ps1` | Starts an extracted package with a scrubbed environment (no developer PATH, no DOTNET_*, redirected profile) |
| `idle-test.ps1` | Chat once, then idle for N minutes while sampling the RAM of every PaRuX process (proves the model is released and reloads on demand) |
| `eval_embeddings.py` | Offline retrieval comparison of embedding models (hit@k, MRR) per language pair, plus score calibration |
| `eval_rerankers.py` | Same for cross-encoder rerankers (order quality and sign of the score of the right passage) |
| `calibrate_e5.py` | Score distributions of multilingual-e5-small used to set `RelevanceThreshold` |

Environment: `PARUX_WORK` = a working folder containing `pylib` (pip --target install of onnxruntime, tokenizers, numpy, huggingface_hub), `retrieval-eval/passages.json` (written by the opt-in test `Dump_passages_for_offline_retrieval_evaluation`) and `office-samples/questions.json`. Optional `retrieval-eval/private-questions.json` holds questions about files that must not be published.
