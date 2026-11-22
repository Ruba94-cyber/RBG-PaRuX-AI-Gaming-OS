"""Summarise a parux_rag.py result: python -I summarize_rag.py rag-*.json"""
import json, statistics as st, sys

for f in sys.argv[1:]:
    d = json.load(open(f, encoding="utf8"))
    print("==", f)
    if "error" in d:
        print("ERROR", d["error"]); continue
    print("files", d["corpus_files"], "add_source ms", round(d["add_source"]["ms"]), "reindex ms", round(d["reindex"]["ms"]), d["reindex"]["body"][:90],
          "knowledge wait ms", round(d["knowledge_index_wait_ms"]))
    lat = d["search"]["latency_ms"]
    print("search median ms", round(st.median(lat), 1), "min", round(min(lat), 1), "max", round(max(lat), 1), "hit@5", d["search"]["hit_at_5"], "/", d["search"]["of"])
    c = d["chat"]
    tt = [x["ttft_ms"] for x in c if x.get("ttft_ms")]; tot = [x["total_ms"] for x in c if x.get("total_ms")]
    print("chat correct", sum(1 for x in c if x.get("correct")), "/", len(c), "ttft median ms", round(st.median(tt)) if tt else None, "total median ms", round(st.median(tot)) if tot else None)
