"""Prompt-cache and reasoning-history check across interleaved conversations (one endpoint, temperature 0).
Turn 1 of conversations A, B, C..., then turn 2 of each in the same order. Turn 2 resends the full history, including the
assistant's reasoning_content (what --reasoning-preserve keeps), so the cached prefix is only reusable when the server kept
that conversation's state (a slot, a host-RAM cache entry or a context checkpoint) intact.
Reports per turn-2 request: TTFT, prompt tokens processed (srv_prompt_n), tokens reused (srv_cache_n), reuse ratio, and
whether the answer equals a control answer computed with cache_prompt=false (same history, same seed): a correct cache or
checkpoint must not change the greedy answer (small numeric drift is reported, not hidden).
Usage: python -I slots_cache.py --label X --url http://127.0.0.1:P/v1/chat/completions [--token-file f] [--conversations 3]
       [--doc-tokens 2000] [--max-tokens 160] [--rounds 2] --out out.json"""
import argparse, json, os, sys, time, http.client, urllib.parse, random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bench

def call(url, token, messages, max_tokens, extra):
    u = urllib.parse.urlparse(url)
    conn = http.client.HTTPConnection(u.hostname, u.port, timeout=300)
    body = {"messages": messages, "stream": True, "temperature": 0, "seed": 7, "max_tokens": max_tokens,
            "stream_options": {"include_usage": True}}
    body.update(extra)
    h = {"Content-Type": "application/json"}
    if token: h["Authorization"] = "Bearer " + token
    t0 = time.perf_counter(); conn.request("POST", u.path, json.dumps(body), h); r = conn.getresponse()
    if r.status != 200: return {"error": "HTTP %d" % r.status, "body": r.read()[:300].decode("utf8", "replace")}
    content, reasoning, timings, t_first = [], [], None, None
    while True:
        line = r.fp.readline()
        if not line: break
        line = line.strip()
        if not line.startswith(b"data:"): continue
        data = line[5:].strip()
        if data == b"[DONE]": break
        try: j = json.loads(data)
        except Exception: continue
        if "timings" in j: timings = j["timings"]
        for c in j.get("choices", []):
            d = c.get("delta", {})
            if d.get("content"): content.append(d["content"]); t_first = t_first or time.perf_counter()
            if d.get("reasoning_content"): reasoning.append(d["reasoning_content"]); t_first = t_first or time.perf_counter()
    res = {"ttft_ms": None if t_first is None else (t_first - t0) * 1000, "content": "".join(content), "reasoning": "".join(reasoning)}
    if timings:
        res["prompt_n"] = timings.get("prompt_n"); res["cache_n"] = timings.get("cache_n")
    return res

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--label", required=True); ap.add_argument("--url", required=True); ap.add_argument("--token-file")
    ap.add_argument("--conversations", type=int, default=3); ap.add_argument("--doc-tokens", type=int, default=2000)
    ap.add_argument("--max-tokens", type=int, default=160); ap.add_argument("--rounds", type=int, default=2)
    ap.add_argument("--control", type=int, default=1, help="1 = also compute the cache_prompt=false control answer")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    token = open(a.token_file, encoding="utf8").read().strip() if a.token_file else os.environ.get("BENCH_TOKEN", "")
    os.environ["BENCH_VARY_BODY"] = "1"  # each conversation gets its own document body, not just its own nonce
    allres = []
    for rd in range(a.rounds):
        convs = []
        for k in range(a.conversations):
            nonce = "%s-r%d-c%d-%d" % (a.label, rd, k, random.randint(1000, 9999))
            convs.append([{"role": "user", "content": bench.make_text(a.doc_tokens, nonce) + "\n\nIn one sentence: what does the scheduler do?"}])
        for k, msgs in enumerate(convs):  # turn 1, all conversations
            r = call(a.url, token, msgs, a.max_tokens, {})
            if "error" in r: print(a.label, "turn1 error", r, flush=True); allres.append({"round": rd, "conv": k, "turn": 1, **r}); continue
            assistant = {"role": "assistant", "content": r["content"]}
            if r["reasoning"]: assistant["reasoning_content"] = r["reasoning"]
            msgs.append(assistant)
            msgs.append({"role": "user", "content": "Now in one sentence: what does the allocator do?"})
            allres.append({"round": rd, "conv": k, "turn": 1, **{x: r.get(x) for x in ("ttft_ms", "prompt_n", "cache_n")}})
        for k, msgs in enumerate(convs):  # turn 2, same order: needs conversation k's state back
            if msgs[-1]["role"] != "user": continue
            r = call(a.url, token, msgs, a.max_tokens, {})
            row = {"round": rd, "conv": k, "turn": 2, **{x: r.get(x) for x in ("ttft_ms", "prompt_n", "cache_n", "error")}}
            total = (r.get("prompt_n") or 0) + (r.get("cache_n") or 0)
            row["reuse_ratio"] = (r.get("cache_n") or 0) / total if total else None
            row["_content"] = r.get("content"); row["_msgs"] = msgs
            allres.append(row)
        # controls only after every turn 2 was measured, so they cannot evict a conversation that is still to be measured
        for row in [x for x in allres if x.get("round") == rd and x.get("turn") == 2]:
            if a.control and row.get("error") is None and row.get("_msgs"):
                ctl = call(a.url, token, row["_msgs"], a.max_tokens, {"cache_prompt": False})
                row["same_answer_as_uncached"] = ctl.get("content") == row["_content"]
                row["same_prefix64_as_uncached"] = (ctl.get("content") or "")[:64] == (row["_content"] or "")[:64]
            row.pop("_msgs", None); row.pop("_content", None)
            print(a.label, {x: (round(v, 3) if isinstance(v, float) else v) for x, v in row.items()}, flush=True)
    json.dump(allres, open(a.out, "w"), indent=1)
