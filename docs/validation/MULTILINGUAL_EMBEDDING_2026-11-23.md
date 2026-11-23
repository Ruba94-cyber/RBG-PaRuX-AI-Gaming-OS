# Multilingual embedding for document search (2026-11-23)

## Why
The default embedding (all-MiniLM-L6-v2) is trained mostly on English. Most real questions here are Italian, over documents in Italian **or English**
(CAN logs, DBC files, specifications). An Italian question finds an English passage only rarely with that model.

## Method (reproducible, no PaRuX code involved)
* Corpus: the synthetic Office documents, three legacy Office files, the owner's DBC and three CAN traces, extracted exactly as the indexer does,
  cut into 900-character passages (10,774 passages; 1,577 evaluated after keeping every correct passage plus a random sample of the rest).
* Questions: 30 of the 34 acceptance questions. Four were dropped because their "correct" set exceeded 30 passages (a generic phrase repeated
  through a whole file), which makes any model look good.
* Correct passage = contains every expected fact of the question. Metric = rank of the first correct passage (hit@1, hit@5, MRR), pure embedding search.
* Tools: ONNX Runtime on this PC (i5-10310U, 8 threads, no GPU), Hugging Face `tokenizers`. Scripts: `retrieval-eval/` (not part of the product).

## Results
| Embedding | size | speed | hit@1 | it->en (13) | it->it (10) |
|---|---|---|---|---|---|
| all-MiniLM-L6-v2 (previous default) | 90 MB | 23 passages/s | 50% | 23% | 70% |
| multilingual-e5-small int8 (new default) | 118 MB | 16 passages/s | 80% | 69% | 90% |
| bge-m3 int8 (reference) | 568 MB | 2 passages/s | 80% | 54% | 100% |

bge-m3 reaches the same overall hit@1 at one eighth of the speed and five times the size: not practical for CPU-only machines.

## Score calibration (multilingual-e5-small int8)
Weakest correct passage 0.495 (p10 0.561, median 0.666); best wrong passage median 0.539 (p90 0.699); typical unrelated passage in the top 100: median 0.428.
`RelevanceThreshold = 0.45` removes noise without losing a correct passage.

## Integration
`MultilingualE5EmbeddingProvider` + `UnigramTokenizer` (no new package). The tokenizer matches the reference implementation on all reference strings
(Italian, English, hex dumps, compatibility characters, CJK). Files are pinned by revision and SHA-256 (`PinnedModelManifest.MultilingualE5SmallInt8`).
The provider is registered before the MiniLM bridge, so it is used when installed and MiniLM remains the fallback.

## Limits
Pure embedding search only: the product also fuses keyword search and a reranker, which can change the absolute numbers. The reranker in the package
(ms-marco MiniLM) is English-only and has not yet been replaced.
