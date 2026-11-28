# Converts the official FastVideo/FastWan2.2-TI2V-5B-FullAttn-Diffusers transformer (diffusers layout, Hugging Face)
# to the native Wan key layout ComfyUI UNETLoader reads (name it Wan2_2-TI2V-5B-FastWan_bf16.safetensors).
# usage: python convert-fastwan-to-comfy.py <diffusers_transformer.safetensors> <native wan2.2_ti2v_5B_fp16.safetensors (key/shape reference)> <out.safetensors>
# Needs torch + safetensors (the ComfyUI python has both). Exits non-zero unless every key and shape matches the reference.
"""Convert FastVideo diffusers WanTransformer3DModel safetensors -> native Wan key layout (what ComfyUI UNETLoader expects).
Validates key set + shapes against the native wan2.2_ti2v_5B_fp16 header. Run with a python that has torch+safetensors."""
import re, sys, json, struct
from safetensors import safe_open
from safetensors.torch import save_file

src, ref, dst = sys.argv[1:4]

def header(p):
    with open(p, "rb") as f:
        n = struct.unpack("<Q", f.read(8))[0]
        h = json.loads(f.read(n))
    h.pop("__metadata__", None)
    return h

RULES = [
    (r"^condition_embedder\.text_embedder\.linear_1\.", "text_embedding.0."),
    (r"^condition_embedder\.text_embedder\.linear_2\.", "text_embedding.2."),
    (r"^condition_embedder\.time_embedder\.linear_1\.", "time_embedding.0."),
    (r"^condition_embedder\.time_embedder\.linear_2\.", "time_embedding.2."),
    (r"^condition_embedder\.time_proj\.", "time_projection.1."),
    (r"^proj_out\.", "head.head."),
    (r"^scale_shift_table$", "head.modulation"),
    (r"^blocks\.(\d+)\.attn1\.to_q\.", r"blocks.\1.self_attn.q."),
    (r"^blocks\.(\d+)\.attn1\.to_k\.", r"blocks.\1.self_attn.k."),
    (r"^blocks\.(\d+)\.attn1\.to_v\.", r"blocks.\1.self_attn.v."),
    (r"^blocks\.(\d+)\.attn1\.to_out\.0\.", r"blocks.\1.self_attn.o."),
    (r"^blocks\.(\d+)\.attn1\.norm_q\.", r"blocks.\1.self_attn.norm_q."),
    (r"^blocks\.(\d+)\.attn1\.norm_k\.", r"blocks.\1.self_attn.norm_k."),
    (r"^blocks\.(\d+)\.attn2\.to_q\.", r"blocks.\1.cross_attn.q."),
    (r"^blocks\.(\d+)\.attn2\.to_k\.", r"blocks.\1.cross_attn.k."),
    (r"^blocks\.(\d+)\.attn2\.to_v\.", r"blocks.\1.cross_attn.v."),
    (r"^blocks\.(\d+)\.attn2\.to_out\.0\.", r"blocks.\1.cross_attn.o."),
    (r"^blocks\.(\d+)\.attn2\.norm_q\.", r"blocks.\1.cross_attn.norm_q."),
    (r"^blocks\.(\d+)\.attn2\.norm_k\.", r"blocks.\1.cross_attn.norm_k."),
    (r"^blocks\.(\d+)\.norm2\.", r"blocks.\1.norm3."),
    (r"^blocks\.(\d+)\.ffn\.net\.0\.proj\.", r"blocks.\1.ffn.0."),
    (r"^blocks\.(\d+)\.ffn\.net\.2\.", r"blocks.\1.ffn.2."),
    (r"^blocks\.(\d+)\.scale_shift_table$", r"blocks.\1.modulation"),
]

def conv(k):
    for pat, rep in RULES:
        if re.match(pat, k): return re.sub(pat, rep, k)
    return k

out = {}
with safe_open(src, "pt") as f:
    for k in f.keys():
        t = f.get_tensor(k)
        out[conv(k)] = t.contiguous()
rh = header(ref)
missing = sorted(set(rh) - set(out)); extra = sorted(set(out) - set(rh))
print("native keys", len(rh), "converted", len(out), "missing", missing[:10], "extra", extra[:10])
bad = [(k, list(out[k].shape), rh[k]["shape"]) for k in out if k in rh and list(out[k].shape) != rh[k]["shape"]]
print("shape mismatches", bad[:10])
if missing or extra or bad: sys.exit(2)
dt = {str(v.dtype) for v in out.values()}; print("dtypes", dt)
save_file(out, dst)
print("saved", dst)
