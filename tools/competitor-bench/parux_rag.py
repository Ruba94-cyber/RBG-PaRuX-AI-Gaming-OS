"""PaRuX Core RAG run on the synthetic corpus from make_rag_corpus.py: ingest time, retrieval latency/hit rate,
and grounded chat answers (ChatCompletionRequest.documents). Uses the desktop token from a file; never prints it.
Usage: python -I parux_rag.py --endpoint http://127.0.0.1:PORT --token-file f --corpus dir --out out.json [--reps 3]"""
import argparse, http.client, json, os, time, urllib.parse

ap = argparse.ArgumentParser()
ap.add_argument("--endpoint", required=True); ap.add_argument("--token-file", required=True)
ap.add_argument("--corpus", required=True); ap.add_argument("--out", required=True); ap.add_argument("--reps", type=int, default=3)
a = ap.parse_args()
tok = open(a.token_file, encoding="utf8").read().strip()
u = urllib.parse.urlparse(a.endpoint)


def call(method, path, body=None, timeout=600):
    c = http.client.HTTPConnection(u.hostname, u.port, timeout=timeout)
    h = {"Authorization": "Bearer " + tok}
    data = None
    if body is not None:
        data = json.dumps(body); h["Content-Type"] = "application/json"
    t0 = time.perf_counter(); c.request(method, path, data, h); r = c.getresponse(); raw = r.read()
    return r.status, (time.perf_counter() - t0) * 1000, raw


def chat(question):
    body = {"model": "local-default", "stream": True, "temperature": 0, "seed": 7, "max_tokens": 160,
            "messages": [{"role": "user", "content": question}], "documents": {"enabled": True, "sourceIds": []}}
    c = http.client.HTTPConnection(u.hostname, u.port, timeout=300)
    t0 = time.perf_counter()
    c.request("POST", "/api/v1/chat/completions", json.dumps(body), {"Authorization": "Bearer " + tok, "Content-Type": "application/json"})
    r = c.getresponse()
    if r.status != 200: return {"error": r.status, "body": r.read()[:200].decode("utf8", "replace")}
    first = None; text = []
    while True:
        line = r.fp.readline()
        if not line: break
        line = line.strip()
        if not line.startswith(b"data:"): continue
        d = line[5:].strip()
        if d == b"[DONE]": break
        try: j = json.loads(d)
        except Exception: continue
        for ch in j.get("choices", []):
            piece = (ch.get("delta") or {}).get("content")
            if piece:
                if first is None: first = time.perf_counter()
                text.append(piece)
    end = time.perf_counter()
    return {"ttft_ms": None if first is None else (first - t0) * 1000, "total_ms": (end - t0) * 1000, "text": "".join(text)}


qs = json.load(open(os.path.join(a.corpus, "questions.json"), encoding="utf8"))
out = {"corpus_files": len([f for f in os.listdir(a.corpus) if f.endswith(".md")]), "questions": len(qs)}
st, ms, raw = call("POST", "/api/v1/rag/sources", {"folderPath": os.path.abspath(a.corpus), "consentSensitive": True})
out["add_source"] = {"status": st, "ms": ms}
if st != 200:
    out["error"] = raw[:300].decode("utf8", "replace"); json.dump(out, open(a.out, "w"), indent=1); raise SystemExit(0)
sid = json.loads(raw)["id"]
st, ms, raw = call("POST", "/api/v1/rag/sources/%s/reindex" % sid)
out["reindex"] = {"status": st, "ms": ms, "body": raw[:200].decode("utf8", "replace")}
# the Knowledge index builds in the background: wait for it to be idle and time that too
t0 = time.perf_counter(); status = None
while time.perf_counter() - t0 < 300:
    s2, _, r2 = call("GET", "/api/v1/knowledge/sources/%s/status" % sid)
    try: status = json.loads(r2)
    except Exception: status = None
    job = (status or {}).get("job") or {}
    if str(job.get("state", job.get("State", ""))).lower() in ("done", "idle", "complete", "completed", "ready", "indexed", "finished"): break
    if status and not job and status.get("files", 0) >= out["corpus_files"]: break
    time.sleep(0.5)
out["knowledge_index_wait_ms"] = (time.perf_counter() - t0) * 1000; out["knowledge_status"] = status
lat, hits = [], 0
for rep in range(a.reps):
    for q in qs:
        st, ms, raw = call("GET", "/api/v1/knowledge/search?k=5&q=" + urllib.parse.quote(q["q"]))
        ok = st == 200 and q["a"] in raw.decode("utf8", "replace")
        if rep > 0 or True: lat.append(ms)
        if rep == 0 and ok: hits += 1
out["search"] = {"latency_ms": lat, "hit_at_5": hits, "of": len(qs)}
answers = []
for rep in range(a.reps):
    for i, q in enumerate(qs):
        r = chat(q["q"]); r["correct"] = bool(r.get("text")) and q["a"] in r["text"]; r["rep"] = rep; r["q"] = i; r.pop("text", None) if r["correct"] else None
        answers.append(r)
out["chat"] = answers
call("DELETE", "/api/v1/rag/sources/%s" % sid)
json.dump(out, open(a.out, "w"), indent=1)
