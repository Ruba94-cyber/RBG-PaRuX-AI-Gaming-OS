import sys, os, json, time, io
sys.path.insert(0, os.path.join(os.environ.get("PARUX_WORK", "."), "pylib"))
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
import numpy as np, onnxruntime as ort
from tokenizers import Tokenizer, BertWordPieceTokenizer
from huggingface_hub import hf_hub_download
try:
    import ctypes; ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)
except Exception:
    pass
P = os.environ.get("PARUX_WORK", ".")
# ---- reuse the question/passage/gold set of the embedding evaluation (first part of that script)
src = open(os.path.join(P, "retrieval-eval", "eval_embeddings.py"), encoding="utf-8").read().split("MODELS = {")[0]
exec(src)
# ---- first stage: multilingual-e5-small (the new default) -> top-K candidates per question
tokp = hf_hub_download("Xenova/multilingual-e5-small", "tokenizer.json"); tk = Tokenizer.from_file(tokp); tk.enable_truncation(max_length=256); tk.enable_padding()
so = ort.SessionOptions(); so.intra_op_num_threads = 4
es = ort.InferenceSession(hf_hub_download("Xenova/multilingual-e5-small", "onnx/model_int8.onnx"), so, providers=["CPUExecutionProvider"])
def emb(texts, bs=16):
    out = []
    for s in range(0, len(texts), bs):
        enc = tk.encode_batch(texts[s:s + bs]); ids = np.array([e.ids for e in enc], dtype=np.int64); mask = np.array([e.attention_mask for e in enc], dtype=np.int64)
        h = es.run(None, {"input_ids": ids, "attention_mask": mask, "token_type_ids": np.zeros_like(ids)})[0]
        v = (h * mask[..., None]).sum(1) / np.maximum(mask.sum(1, keepdims=True), 1); out.append(v / np.linalg.norm(v, axis=1, keepdims=True))
    return np.vstack(out)
cache = os.path.join(P, "retrieval-eval", "e5_passages.npy")
Pm = np.load(cache) if os.path.exists(cache) and np.load(cache).shape[0] == len(sub) else emb(["passage: " + p["text"] for p in sub])
np.save(cache, Pm)
Qm = emb(["query: " + q for q, _, _ in Q]); sims = Qm @ Pm.T
K = 30
cands = [list(np.argsort(-sims[qi])[:K]) for qi in range(len(Q))]
first_stage = [next((r + 1 for r, i in enumerate(c) if i in golds[qi]), 9999) for qi, c in enumerate(cands)]
print("first stage e5 (top-%d candidates): hit@1 %.0f%%  recall@%d %.0f%%" % (K, 100 * np.mean([r <= 1 for r in first_stage]), K, 100 * np.mean([r <= K for r in first_stage])))

def ce_score(sess, tok, pairs, bs=8, bert=False):
    names = [i.name for i in sess.get_inputs()]; out = []
    for s in range(0, len(pairs), bs):
        enc = tok.encode_batch(pairs[s:s + bs])
        ids = np.array([e.ids for e in enc], dtype=np.int64); mask = np.array([e.attention_mask for e in enc], dtype=np.int64)
        feed = {"input_ids": ids, "attention_mask": mask}
        if "token_type_ids" in names: feed["token_type_ids"] = np.array([e.type_ids for e in enc], dtype=np.int64)
        lo = sess.run(None, {k: v for k, v in feed.items() if k in names})[0]
        out.extend(float(x) for x in (lo[:, 0] if lo.ndim == 2 else lo.reshape(len(enc))))
    return out

RER = {
    "ms-marco-MiniLM-L6 (attuale, inglese)": dict(kind="bert"),
    "mmarco-mMiniLMv2-L12 (multilingue)": dict(repo="cross-encoder/mmarco-mMiniLMv2-L12-H384-v1", onnx="onnx/model.onnx"),
}
RER["mmarco int8 (avx512 export)"] = dict(repo="cross-encoder/mmarco-mMiniLMv2-L12-H384-v1", onnx="onnx/model_qint8_avx512.onnx")
only = sys.argv[1:] or list(RER)
for name, r in RER.items():
    if name not in only: continue
    t0 = time.time()
    if r.get("kind") == "bert":
        base = os.path.join(P, "wt-int", "artifacts", "corporate-lite", "assets", "reranker")
        tok = BertWordPieceTokenizer(os.path.join(base, "vocab.txt"), lowercase=True); tok.enable_truncation(max_length=256); tok.enable_padding()
        sess = ort.InferenceSession(os.path.join(base, "model.onnx"), so, providers=["CPUExecutionProvider"])
    else:
        tok = Tokenizer.from_file(hf_hub_download(r["repo"], "tokenizer.json")); tok.enable_truncation(max_length=256); tok.enable_padding()
        sess = ort.InferenceSession(hf_hub_download(r["repo"], r["onnx"]), so, providers=["CPUExecutionProvider"])
    ranks, gold_pos, best_gold, best_non = [], 0, [], []
    for qi in range(len(Q)):
        pairs = [(Q[qi][0], sub[i]["text"]) for i in cands[qi]]
        sc = np.array(ce_score(sess, tok, pairs))
        order = np.argsort(-sc); ranked = [cands[qi][j] for j in order]
        ranks.append(next((k + 1 for k, i in enumerate(ranked) if i in golds[qi]), 9999))
        g = [sc[j] for j, i in enumerate(cands[qi]) if i in golds[qi]]; n = [sc[j] for j, i in enumerate(cands[qi]) if i not in golds[qi]]
        if g: best_gold.append(max(g)); gold_pos += max(g) > 0
        if n: best_non.append(max(n))
    grp = {}
    for (q, a, tag), rk in zip(Q, ranks):
        g = "mono" if tag.split("->")[0] == tag.split("->")[1] else "cross"; grp.setdefault(g, []).append(rk); grp.setdefault("all", []).append(rk)
    print("\n== %s | %.0fs for %d pairs" % (name, time.time() - t0, len(Q) * K))
    for g in ("all", "mono", "cross"):
        rs = grp[g]; print("   %-5s hit@1 %.0f%%  hit@3 %.0f%%  MRR %.2f  (n=%d)" % (g, 100 * np.mean([x <= 1 for x in rs]), 100 * np.mean([x <= 3 for x in rs]), np.mean([1 / x if x < 9999 else 0 for x in rs]), len(rs)))
    it_en = [rk for (q, a, tag), rk in zip(Q, ranks) if tag == "it->en"]
    print("   it->en hit@1 %.0f%% (n=%d)" % (100 * np.mean([x <= 1 for x in it_en]), len(it_en)))
    print("   best correct passage scored > 0 in %d of %d questions; scores: best-correct median %.2f, best-wrong median %.2f" % (gold_pos, len(best_gold), np.median(best_gold), np.median(best_non)))
