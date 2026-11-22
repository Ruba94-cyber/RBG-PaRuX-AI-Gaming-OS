"""Streaming chat benchmark against an OpenAI-compatible endpoint. Same prompts/params for every target.
Usage: python -I bench.py --label X --url http://127.0.0.1:1234/v1/chat/completions [--token-file f] [--model m] [--runs 5] --out out.json
Token (if any) is read from a file or env BENCH_TOKEN and never printed."""
import argparse, json, os, time, http.client, urllib.parse, random

def make_text(approx_tokens, nonce):
    # deterministic synthetic technical prose; ~1.3 tokens per word on average for this vocabulary
    import zlib
    rnd = random.Random(1234 if not os.environ.get("BENCH_VARY_BODY") else zlib.crc32(nonce.encode()))
    subj = ["the scheduler", "a cache line", "the allocator", "each worker thread", "the build pipeline", "the file watcher",
            "a retry policy", "the telemetry buffer", "the index shard", "a lock-free queue", "the render loop", "the config loader"]
    verb = ["flushes", "reconciles", "defers", "serialises", "revalidates", "compacts", "snapshots", "rebalances", "throttles", "audits"]
    obj = ["pending jobs", "stale entries", "partial writes", "idle connections", "dirty pages", "queued events", "orphaned handles", "batched requests"]
    tail = ["before the next tick", "under memory pressure", "when the budget is exhausted", "after a clean shutdown", "without blocking readers",
            "if the deadline has passed", "once the quorum agrees", "during the warm-up phase"]
    out = [f"[doc {nonce}]"]
    words = 0
    target_words = int(approx_tokens / 1.35)
    while words < target_words:
        s = f"{rnd.choice(subj).capitalize()} {rnd.choice(verb)} {rnd.choice(obj)} {rnd.choice(tail)}; note {rnd.randint(100,999)}."
        out.append(s); words += len(s.split())
    return " ".join(out)

PROMPTS = {
    "short": lambda n: "Explain in two sentences what a mutex is. (ref %s)" % n,
    "1k": lambda n: make_text(1000, n) + "\n\nSummarize the notes above in exactly two sentences.",
    "4k": lambda n: make_text(4000, n) + "\n\nSummarize the notes above in exactly two sentences.",
}

def one(url, token, model, prompt, max_tokens, extra):
    u = urllib.parse.urlparse(url)
    conn = http.client.HTTPConnection(u.hostname, u.port, timeout=300)
    body = {"messages": [{"role": "user", "content": prompt}], "stream": True, "temperature": 0, "seed": 7, "max_tokens": max_tokens}
    if model: body["model"] = model
    body.update(extra)
    h = {"Content-Type": "application/json"}
    if token: h["Authorization"] = "Bearer " + token
    t0 = time.perf_counter()
    conn.request("POST", u.path, json.dumps(body), h)
    r = conn.getresponse()
    if r.status != 200:
        return {"error": f"HTTP {r.status}", "body": r.read()[:200].decode("utf8", "replace")}
    t_first_event = t_first_tok = t_last = None
    chunks = 0; timings = None; usage = None; ptxt = []
    buf = b""
    while True:
        line = r.fp.readline()
        if not line: break
        line = line.strip()
        if not line.startswith(b"data:"): continue
        data = line[5:].strip()
        now = time.perf_counter()
        if data == b"[DONE]": break
        try: j = json.loads(data)
        except Exception: continue
        if t_first_event is None: t_first_event = now
        if "timings" in j: timings = j["timings"]
        if "usage" in j and j["usage"]: usage = j["usage"]
        for c in j.get("choices", []):
            d = c.get("delta", {})
            piece = d.get("content") or d.get("reasoning_content")
            if piece:
                chunks += 1
                if t_first_tok is None: t_first_tok = now
                t_last = now
                if len(ptxt) < 40: ptxt.append(piece)
    t_end = time.perf_counter()
    res = {"ttft_ms": None if t_first_tok is None else (t_first_tok - t0) * 1000,
           "first_event_ms": None if t_first_event is None else (t_first_event - t0) * 1000,
           "total_ms": (t_end - t0) * 1000, "stream_chunks": chunks, "head": "".join(ptxt)[:80]}
    if t_first_tok is not None and t_last is not None and chunks > 1:
        res["client_decode_tps"] = (chunks - 1) / (t_last - t_first_tok)
    if timings:
        for k in ("prompt_n", "cache_n", "prompt_ms", "prompt_per_second", "predicted_n", "predicted_ms", "predicted_per_second"):
            res["srv_" + k] = timings.get(k)
    if usage: res["usage"] = usage
    return res

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--label", required=True); ap.add_argument("--url", required=True)
    ap.add_argument("--token-file"); ap.add_argument("--model", default="")
    ap.add_argument("--runs", type=int, default=5); ap.add_argument("--max-tokens", type=int, default=128)
    ap.add_argument("--lengths", default="short,1k,4k"); ap.add_argument("--extra", default="{}")
    ap.add_argument("--out", required=True); ap.add_argument("--warmup", type=int, default=1)
    a = ap.parse_args()
    token = os.environ.get("BENCH_TOKEN", "")
    if a.token_file: token = open(a.token_file, encoding="utf8").read().strip()
    extra = json.loads(a.extra)
    allres = []
    for ln in a.lengths.split(","):
        for i in range(-a.warmup, a.runs):
            nonce = "%s-%s-%d-%d" % (a.label, ln, i, random.randint(1000, 9999))  # unique prefix => no prompt-cache reuse
            r = one(a.url, token, a.model, PROMPTS[ln](nonce), a.max_tokens, extra)
            r.update({"label": a.label, "length": ln, "run": i, "warmup": i < 0})
            allres.append(r)
            print(a.label, ln, i, {k: (round(v, 1) if isinstance(v, float) else v) for k, v in r.items() if k in ("ttft_ms", "client_decode_tps", "srv_prompt_n", "srv_predicted_per_second", "srv_cache_n", "error")}, flush=True)
    json.dump(allres, open(a.out, "w"), indent=1)
