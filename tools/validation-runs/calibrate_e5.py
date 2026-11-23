import sys, os, json, random, io
sys.path.insert(0, os.path.join(os.environ.get("PARUX_WORK", "."), "pylib"))
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
import numpy as np, onnxruntime as ort
from tokenizers import Tokenizer
from huggingface_hub import hf_hub_download
try:
    import ctypes; ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)
except Exception:
    pass
P = os.environ.get("PARUX_WORK", ".")
exec(open(os.path.join(P, "retrieval-eval", "eval_embeddings.py"), encoding="utf-8").read().split("MODELS = {")[0].replace("sel = sorted(keep | set(others[:3200]))", "sel = sorted(keep | set(others[:900]))"))
tokp = hf_hub_download("Xenova/multilingual-e5-small", "tokenizer.json"); tok = Tokenizer.from_file(tokp)
# ---- golden tokenizer cases (written next to the tests, no corporate content: strings are invented or taken from the synthetic corpus)
cases = ["Qual e' il cycle time del messaggio ETC7?", "What is the tightening torque for the M10 mounting bolts?", "Wartungsanleitung Hydraulikpumpe HP-30 – Überdruck",
         "0x18FE4A03x Rx d 8 FF DF 7F FF FD FF FF FF", "Contratto di fornitura n. 2026/118, Art. 7 - Penali: 2,5% per ogni settimana", "perché è già così: città, università, età",
         "  leading   and   trailing   spaces  ", "line one\nline two\ttabbed", "ﬁne ﬂow ① ½ ㎏ Ａ１", "日本語のテキスト and mixed 中文", "email: nome.cognome@example.test; url https://example.test/a?b=c", "", "a", "SPN 123456 FMI 2 (occurrences 3) | lamps: protect=off"]
cases += [p["text"][:300] for p in sub[:40:3]]
gold = [{"text": c, "ids": tok.encode(c).ids} for c in cases]
json.dump(gold, open(os.path.join(P, "retrieval-eval", "tokenizer-golden.json"), "w", encoding="utf-8"), ensure_ascii=False)
print("golden cases:", len(gold))
# ---- score calibration
so = ort.SessionOptions(); so.intra_op_num_threads = 4
sess = ort.InferenceSession(hf_hub_download("Xenova/multilingual-e5-small", "onnx/model_int8.onnx"), so, providers=["CPUExecutionProvider"])
tok.enable_truncation(max_length=256); tok.enable_padding()
def embed(texts, bs=16):
    out = []
    for s in range(0, len(texts), bs):
        enc = tok.encode_batch(texts[s:s + bs]); ids = np.array([e.ids for e in enc], dtype=np.int64); mask = np.array([e.attention_mask for e in enc], dtype=np.int64)
        h = sess.run(None, {"input_ids": ids, "attention_mask": mask, "token_type_ids": np.zeros_like(ids)})[0]
        v = (h * mask[..., None]).sum(1) / np.maximum(mask.sum(1, keepdims=True), 1); out.append(v / np.linalg.norm(v, axis=1, keepdims=True))
    return np.vstack(out)
Pm = embed(["passage: " + p["text"] for p in sub]); Qm = embed(["query: " + q for q, _, _ in Q])
sims = Qm @ Pm.T
gs, ng_top, ng_all = [], [], []
for qi, g in enumerate(golds):
    s = sims[qi]; order = np.argsort(-s)
    gs.append(max(s[list(g)])); nong = [i for i in order if i not in g]
    ng_top.append(s[nong[0]]); ng_all.extend(s[nong[:200]])
print("best gold passage score:   min %.3f  p10 %.3f  median %.3f" % (min(gs), np.percentile(gs, 10), np.median(gs)))
print("best NON-gold score:       median %.3f  p90 %.3f  max %.3f" % (np.median(ng_top), np.percentile(ng_top, 90), max(ng_top)))
print("typical non-gold (top200): median %.3f  p95 %.3f" % (np.median(ng_all), np.percentile(ng_all, 95)))
allq = sims.flatten(); print("all pairs: p50 %.3f p90 %.3f p99 %.3f" % tuple(np.percentile(allq, [50, 90, 99])))
