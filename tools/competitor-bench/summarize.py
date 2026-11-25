import json,sys,statistics as st
def med(v): 
    v=[x for x in v if x is not None]
    return (round(st.median(v),1),round(min(v),1),round(max(v),1)) if v else None
for f in sys.argv[1:]:
    d=json.load(open(f)); print("==",f)
    for ln in ["short","1k","4k"]:
        r=[x for x in d if x["length"]==ln and not x["warmup"] and "error" not in x]
        if not r: continue
        print(ln,"n",len(r),"ttft",med([x["ttft_ms"] for x in r]),"dec_srv",med([x.get("srv_predicted_per_second") for x in r]),"dec_real(usage)",med([x.get("real_decode_tps") for x in r]),"usage_n",sum(1 for x in r if x.get("tokens_source")=="usage"),"pp",med([x.get("srv_prompt_per_second") for x in r]),"prompt_n",r[0].get("srv_prompt_n"),"cache_n",r[0].get("srv_cache_n"))
