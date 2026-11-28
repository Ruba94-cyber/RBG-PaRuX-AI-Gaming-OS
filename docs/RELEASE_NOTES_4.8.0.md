# RBG – PaRuX AI & Gaming OS 4.8.0 release notes (official release)

- **Date:** 2026-11-28
- **Channel:** official release (`v4.8.0`), signed and verified.
- **Supersedes:** `v4.7.0`.
- **Build commit:** recorded in `SHA256SUMS.txt` and the signed release manifest.
- **Version source:** `Directory.Build.props`, `PaRuXProductVersion` = `4.8.0` (Android `versionName` `4.8.0`, `versionCode` `31`).

## Highlights in 4.8.0

- **Faster than raw llama.cpp at the same engine and model** (RTX 5080, `Qwen3-4B-Q4_K_M`, both on llama.cpp `b12818` CUDA 13.5):
  - **Cold and long prompts:** time to first token is 23–44% lower.
  - **Code edits:** decoding is 5.6x faster and the whole edit takes 77% less time, thanks to default n-gram speculative decoding.
  - **RAG:** answers come 39% faster.
  - **Warm turns:** router and health checks are cached.
  - **Streaming:** desktop Markdown rendering is incremental.
  - **Engine:** the bundled engine moves from `b11871` to `b12818` (CUDA 13.5).
  - Details are in [COMPETITOR_BENCHMARK_2026-11-28.md](COMPETITOR_BENCHMARK_2026-11-28.md).
- **Media:**
  - **Defaults:** new launch settings (dynamic VRAM, `--cuda-malloc`, no previews) make Z-Image 2.24 s/image instead of 3.05 s, with identical output.
  - **FastWan:** a new optional 4-step FastWan video preset, 3.4x faster.
- **Character Studio:**
  - **Wizard:** a guided Character → Pictures → Training → Done flow creates a consistent-look character.
  - **Training:** the character LoRA trains locally with a pinned, hash-locked trainer, using Quick, Balanced or Best presets picked by VRAM.
  - **Control:** training shows progress and an ETA, and can be cancelled and resumed.
  - **Binding:** the LoRA is bound to the character automatically.
- **Remote access from anywhere (opt-in):**
  - **Tunnel:** a Cloudflare quick tunnel (pinned `cloudflared`) exposes only the paired-device API.
  - **Pairing:** new devices need your approval on the PC, through an in-app bar or a Windows notification. Pairing attempts are rate-limited.
  - **Restrictions:** the tunnel never runs in Corporate mode and is switched off by Gaming Mode.
  - **Phone:** the app prefers home Wi-Fi, falls back to the tunnel, and refreshes the address by itself.
- **Android app:**
  - **Runtime and documents:** load or unload the model, upload documents to the PC knowledge base with indexing progress.
  - **Chats:** incognito chats, rename and delete conversations, copy or regenerate answers.
  - **Media:** advanced options for size, quality, seed and video length.
  - **Language:** English with full Italian translation.
- **Feature parity with other local AI apps:**
  - **Search:** keyless Marginalia web search, plus "search my earlier chats" (project-scoped, never incognito).
  - **Settings from chat:** change settings from chat, always behind an Apply/Cancel card.
  - **Appearance:** a background image for each chat.
- **Easier to use:**
  - **First run:** the model that fits your PC is preselected, with the reason. Setup errors are explained in plain words with the next step. The Simple/Advanced switch and other shortcuts stay hidden until first-run setup is finished.
  - **Model Hub:** plain badges ("Fits your PC", "Fast", "Best quality").
  - **Advanced workspaces:** each has a "What this does" header, ready-made templates, and plain-language help on 41 technical controls.
  - **Language:** English by default everywhere, with Italian support.
- **Corporate Lite:**
  - **Memory admission:** models are admitted by what Windows can actually commit, instead of instantaneous free RAM. A busy PC (around 80% RAM used) now runs chat with a "your PC is busy, replies may be slower" note instead of refusing.
  - **Indexing:** the embedding server is no longer restarted while a request is in flight. Indexing retries with a friendly message instead of a "server unreachable" error.
  - **Disk space:** free space on redirected or UNC folders is now detected.

## Known limits

- No visual pass on the desktop UI was done for this batch: automated tests and builds only.
- No physical Android phone was used; the Android app was verified by unit tests and build only.
- The corporate memory admission was verified with tests, not on a managed corporate PC.
