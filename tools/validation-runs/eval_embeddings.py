import sys, os, json, time, re, random
sys.path.insert(0, os.path.join(os.environ.get("PARUX_WORK", "."), "pylib"))
import numpy as np, onnxruntime as ort
from tokenizers import Tokenizer
from huggingface_hub import hf_hub_download
try:
    import ctypes; ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)   # BELOW_NORMAL
except Exception:
    pass

P = os.environ.get("PARUX_WORK", ".")
passages = json.load(open(os.path.join(P, "retrieval-eval", "passages.json"), encoding="utf-8"))
questions = json.load(open(os.path.join(P, "office-samples", "questions.json"), encoding="utf-8"))
can_q = []
_pq = os.path.join(P, "retrieval-eval", "private-questions.json")   # optional, local only: [[question, [expected tokens]], ...]
if os.path.exists(_pq):
    can_q = [tuple(x) for x in json.load(open(_pq, encoding="utf-8"))]
Q = [(q, a, "it->en") for q, a in can_q]
n_office = len(questions) - 10   # last 10 are the multilingual ones
cross_tags = ["en->de", "de->de", "it->de", "fr->fr", "en->fr", "es->es", "it->es", "en->en", "de->en", "it->fr"]
for i, x in enumerate(questions):
    if i < n_office:
        tag = "it->en" if any(k in x["q"] for k in ("ppt", "test message", "PumpCheck", "TestMacro", "SimpleMacro")) else "it->it"
    else:
        tag = cross_tags[i - n_office]
    Q.append((x["q"], x["all"], tag))

def gold_for(tokens):
    toks = [t for t in tokens if len(t) >= 3]
    hit = [i for i, p in enumerate(passages) if all(t.lower() in p["text"].lower() for t in toks)]
    if not hit:
        longest = max(toks, key=len)
        hit = [i for i, p in enumerate(passages) if longest.lower() in p["text"].lower()]
    return set(hit)

golds = [gold_for(a) for _, a, _ in Q]
# a question whose "right passages" are hundreds (a generic phrase repeated across a whole file) cannot discriminate between models
ok = [i for i, g in enumerate(golds) if 0 < len(g) <= 30]
print("questions kept (<=30 gold passages):", len(ok), "of", len(Q), "| dropped as ambiguous:", [Q[i][0][:40] for i in range(len(Q)) if i not in ok])
Q = [Q[i] for i in ok]; golds = [golds[i] for i in ok]
keep = set().union(*golds)
rnd = random.Random(7)
others = [i for i in range(len(passages)) if i not in keep]
rnd.shuffle(others)
sel = sorted(keep | set(others[:1500]))
sub = [passages[i] for i in sel]
remap = {old: new for new, old in enumerate(sel)}
golds = [{remap[g] for g in gs} for gs in golds]
print(f"passages {len(passages)} -> evaluated {len(sub)}; questions {len(Q)}; questions without gold: {sum(1 for g in golds if not g)}")

MODELS = {
    "minilm-l6 (attuale)": dict(repo="sentence-transformers/all-MiniLM-L6-v2", onnx="onnx/model.onnx", pool="mean", qp="", pp=""),
    "multilingual-e5-small int8": dict(repo="Xenova/multilingual-e5-small", onnx="onnx/model_int8.onnx", pool="mean", qp="query: ", pp="passage: "),
    "paraphrase-multilingual-MiniLM-L12": dict(repo="sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2", onnx="onnx/model.onnx", pool="mean", qp="", pp=""),
    "bge-m3 int8": dict(repo="Xenova/bge-m3", onnx="onnx/model_int8.onnx", pool="cls", qp="", pp=""),
}
only = sys.argv[1:] or list(MODELS)

def embed(sess, tok, texts, pool, bs=16):
    names = [i.name for i in sess.get_inputs()]
    out = []
    for s in range(0, len(texts), bs):
        enc = tok.encode_batch(texts[s:s + bs])
        ids = np.array([e.ids for e in enc], dtype=np.int64); mask = np.array([e.attention_mask for e in enc], dtype=np.int64)
        feed = {"input_ids": ids, "attention_mask": mask}
        if "token_type_ids" in names: feed["token_type_ids"] = np.zeros_like(ids)
        h = sess.run(None, {k: v for k, v in feed.items() if k in names})[0]
        v = h[:, 0, :] if pool == "cls" else (h * mask[..., None]).sum(1) / np.maximum(mask.sum(1, keepdims=True), 1)
        v = v / np.maximum(np.linalg.norm(v, axis=1, keepdims=True), 1e-9)
        out.append(v)
    return np.vstack(out)

results = {}
for name in only:
    m = MODELS[name]
    t0 = time.time()
    path = hf_hub_download(m["repo"], m["onnx"]); tokp = hf_hub_download(m["repo"], "tokenizer.json")
    tok = Tokenizer.from_file(tokp); tok.enable_truncation(max_length=256); tok.enable_padding()
    so = ort.SessionOptions(); so.intra_op_num_threads = 4
    sess = ort.InferenceSession(path, so, providers=["CPUExecutionProvider"])
    t1 = time.time()
    P_emb = embed(sess, tok, [m["pp"] + p["text"] for p in sub], m["pool"])
    t2 = time.time()
    Q_emb = embed(sess, tok, [m["qp"] + q for q, _, _ in Q], m["pool"])
    sims = Q_emb @ P_emb.T
    ranks = []
    for qi, gs in enumerate(golds):
        order = np.argsort(-sims[qi])
        pos = next((r + 1 for r, idx in enumerate(order) if idx in gs), 9999) if gs else 9999
        ranks.append(pos)
    results[name] = ranks
    gs, nt, na = [], [], []
    for qi, g in enumerate(golds):
        srow = sims[qi]; order = np.argsort(-srow); gl = list(g)
        gs.append(float(srow[gl].max())); non = [i for i in order[:300] if i not in g]
        nt.append(float(srow[non[0]])); na.extend(float(x) for x in srow[non[:100]])
    print("   scores: best-gold min %.3f p10 %.3f median %.3f | best-non-gold median %.3f p90 %.3f max %.3f | non-gold top100 median %.3f p95 %.3f" % (min(gs), np.percentile(gs, 10), np.median(gs), np.median(nt), np.percentile(nt, 90), max(nt), np.median(na), np.percentile(na, 95)))
    groups = {}
    for (q, a, tag), r in zip(Q, ranks):
        grp = "mono" if tag.split("->")[0] == tag.split("->")[1] else "cross"
        groups.setdefault(grp, []).append(r); groups.setdefault("all", []).append(r)
    def stat(rs): return "hit@1 %.0f%%  hit@5 %.0f%%  hit@10 %.0f%%  MRR %.2f  (n=%d)" % (100 * np.mean([r <= 1 for r in rs]), 100 * np.mean([r <= 5 for r in rs]), 100 * np.mean([r <= 10 for r in rs]), np.mean([1 / r if r < 9999 else 0 for r in rs]), len(rs))
    print(f"\n== {name}  | load {t1 - t0:.0f}s, encode {len(sub)} passages {t2 - t1:.0f}s ({len(sub) / max(t2 - t1, 1e-9):.0f}/s), model {os.path.getsize(path) / 1e6:.0f} MB")
    for g in ("all", "mono", "cross"): print(f"   {g:5s} {stat(groups[g])}")
    for tag in sorted({t for _, _, t in Q}):
        rs = [r for (q, a, t), r in zip(Q, ranks) if t == tag]; print(f"      {tag:6s} {stat(rs)}")
json.dump({k: v for k, v in results.items()}, open(os.path.join(P, "retrieval-eval", "ranks.json"), "w"))
