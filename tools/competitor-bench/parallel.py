"""N concurrent streaming chat requests against one OpenAI-compatible endpoint (default 3), same prompt length, unique nonce each.
Reports per-request TTFT/total and aggregate throughput from server-counted tokens (usage.completion_tokens, else timings.predicted_n).
Usage: python -I parallel.py --label X --url http://127.0.0.1:P/v1/chat/completions [--token-file f] [--n 3] [--length 1k] [--rounds 3] --out out.json"""
import argparse, json, os, random, threading, time
import sys; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bench

ap = argparse.ArgumentParser()
ap.add_argument("--label", required=True); ap.add_argument("--url", required=True); ap.add_argument("--token-file")
ap.add_argument("--n", type=int, default=3); ap.add_argument("--length", default="1k"); ap.add_argument("--rounds", type=int, default=3)
ap.add_argument("--max-tokens", type=int, default=128); ap.add_argument("--out", required=True)
a = ap.parse_args()
token = open(a.token_file, encoding="utf8").read().strip() if a.token_file else os.environ.get("BENCH_TOKEN", "")
rounds = []
for rd in range(-1, a.rounds):  # round -1 is a warm-up
    res = [None] * a.n
    def work(i):
        nonce = "%s-par-%d-%d-%d" % (a.label, rd, i, random.randint(1000, 9999))
        res[i] = bench.one(a.url, token, "", bench.PROMPTS[a.length](nonce), a.max_tokens, {})
    th = [threading.Thread(target=work, args=(i,)) for i in range(a.n)]
    t0 = time.perf_counter()
    for t in th: t.start()
    for t in th: t.join()
    wall = time.perf_counter() - t0
    toks = sum((r.get("completion_tokens") or 0) for r in res if "error" not in r)
    out = {"round": rd, "warmup": rd < 0, "wall_s": wall, "completion_tokens": toks, "aggregate_tps": toks / wall if wall else None,
           "ttft_ms": [r.get("ttft_ms") for r in res], "total_ms": [r.get("total_ms") for r in res],
           "errors": [r.get("error") for r in res if "error" in r]}
    rounds.append(out)
    print(a.label, {k: (round(v, 1) if isinstance(v, float) else v) for k, v in out.items() if k != "total_ms"}, flush=True)
json.dump(rounds, open(a.out, "w"), indent=1)
