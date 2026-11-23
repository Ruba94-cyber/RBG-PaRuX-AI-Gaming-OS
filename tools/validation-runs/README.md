# Validation runs (developer tools)

Scripts used on 2026-11-23 to validate the Corporate packages on a real PC. They contain no data of any real file or company: the documents are generated (`tests/corporate/make-*-samples.py`) and anything private (own files, questions about them) is read from local files that are **not** part of the repository.

| Script | Purpose |
|---|---|
| `verify-main-local.ps1` | Windows-only, manual Release x64 WinUI/server build and focused regressions; optional full xUnit, real AppContainer smoke and interactive UI tests; saves local TRX evidence, never creates releases or runs GitHub Actions |
| `start-clean.ps1` | Starts an extracted package with a scrubbed environment (no developer PATH, no DOTNET_*, redirected profile) |
| `idle-test.ps1` | Chat once, then idle for N minutes while sampling the RAM of every PaRuX process (proves the model is released and reloads on demand) |
| `eval_embeddings.py` | Offline retrieval comparison of embedding models (hit@k, MRR) per language pair, plus score calibration |
| `eval_rerankers.py` | Same for cross-encoder rerankers (order quality and sign of the score of the right passage) |
| `calibrate_e5.py` | Score distributions of multilingual-e5-small used to set `RelevanceThreshold` |

Environment: `PARUX_WORK` = a working folder containing `pylib` (pip --target install of onnxruntime, tokenizers, numpy, huggingface_hub), `retrieval-eval/passages.json` (written by the opt-in test `Dump_passages_for_offline_retrieval_evaluation`) and `office-samples/questions.json`. Optional `retrieval-eval/private-questions.json` holds questions about files that must not be published.

## Manual integration acceptance (main)

From a trusted Windows x64 development machine with the .NET 10 SDK pinned in `global.json` and Windows/WinUI build prerequisites installed, run:

```powershell
powershell -ExecutionPolicy Bypass -File tools/validation-runs/verify-main-local.ps1 -FullSuite -SandboxSmoke
```

To include FlaUI UI automation, add `-UiSmoke` from a logged-in interactive desktop session. The default run compiles the actual Server test graph and WinUI desktop, then runs a focused regression subset. Test reports are saved under the Git-ignored `artifacts/local-validation/`; never commit, publish or upload customer logs. A failing native AppContainer test remains a failure rather than falling back to an unrestricted process. Real controller, actual Playnite game process, HDR/eGPU performance, clean Corporate PC and release packaging need separate physical acceptance. **Source consolidation on main is not a release certification.** GitHub Actions remain disabled.
