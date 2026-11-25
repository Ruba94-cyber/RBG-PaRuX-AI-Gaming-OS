# RBG – PaRuX AI & Gaming OS 4.6.0 release notes (official release)

- **Date:** 2026-11-26
- **Channel:** official release (`v4.6.0`), signed and verified.
- **Supersedes:** `v4.5.1` pre-release (and `v4.5.0`).
- **Build commit:** recorded in `SHA256SUMS.txt` and the signed release manifest.
- **Version source:** `Directory.Build.props`, `PaRuXProductVersion` = `4.6.0` (Android `versionName` `4.6.0`, `versionCode` `29`).

## Highlights in 4.6.0

- **Performance (Core & WinUI):**
  - CUDA full-offload launches disable host-RAM prompt cache (`--cache-ram 0`) and enable adaptive pooled parallel slots (`--parallel` + `--kv-unified` + `--kv-unified-per-slot`) without KV oversubscription.
  - Hot-path residency, health and module caching + `K32GetProcessMemoryInfo` working-set reads cut Core TTFT on RTX 5080 (`Qwen3-4B-Q4_K_M`) to **76.4 ms** (short), **164.4 ms** (1k) and **496.0 ms** (4k), and RAG TTFT to **164 ms** (30/30 grounded, hit@5 10/10).
  - Lazy startup pages/OSK, post-paint service deferral, `AppPaths` caching and `PublishReadyToRun` cut WinUI cold window start to **332–341 ms** (Release publish).
  - Adaptive idle polling reduces idle CPU (UI + Core) to ~1.99% of one core.
- **Reliability & Windows sign-in / MSI:**
  - Fixed model-switch hang on `/api/v1/models/library/<file>/load` (6/6 verified) and restored streamed `usage` frames on the tool-enabled path.
  - Sign-in startup uses the current user's scheduled task (`PaRuX at sign-in`, Normal priority, HKCU Run fallback), respects the Desktop window choice (`normal` vs `minimized`), and warns in Settings if Windows did not start PaRuX.
  - Full MSI uninstall (`REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`, never on upgrade) cleans up PaRuX's own scheduled task, HKCU Run entry and empty `Redist` folders without touching third-party components or foreign entries.
- **Native Gaming UI removal complete (steps 2–8):**
  - Removed legacy native Gaming Home/library UI, native Console/TV presenter, Steam/RetroAchievements, Game Extensions, Discover, Wishlist, Smart Collections and dead native-library contracts/metadata services.
  - Collapsed the `AI | Gaming` top bar into the sidebar **`Games`** workspace (`Ctrl+2`).
  - Kept Playnite Fullscreen Console mode, AI↔game handoff/hard-off, downloads/staging, modding/AI Mod Studio/Skin Studio, controller tools (VirtualPad/HidHide/XInput shim), display switching, Sunshine streaming, save backups and local game guide.
- **New AI & Productivity capabilities:**
  - **Mid-run agent steering & durable file rewind:** observable/cancellable steering queue at step boundaries; rewinding chat to message N reverses durable Git patches applied after N (honest refusal on user edits or HEAD changes, with undo).
  - **Text-to-3D & local 3D export:** two-stage local text-to-3D pipeline plus pure-C# GLB → **OBJ**, binary **STL** and 64-byte-aligned **USDZ** export.
  - **Multi-GPU training:** `single` / `device-map-auto` / `data-parallel` (`torchrun`) planning, runner support and wizard UI; aligned v1/v2 training engine install manifest and fixed progressive step history.
  - **Agent skills & Knowledge connectors:** read-only `EXPLAIN` SQL queries with 5 s timeout, deterministic `scatter` SVG charts, YouTube bare-host support and EN+IT errors.
  - **Usability:** file drag-and-drop and clipboard paste on Chat and Knowledge, first-run background-download consent, quick actions (`Ctrl+Shift+F/M/G/A/I`).

## Verification summary

- **Full Server xUnit suite (`tests/PaRuX.Server.Tests`, Release)** and **`tools/release-gate.ps1`** executed on the release commit.
- **Real GPU benchmark (RTX 5080):** measured Core TTFT/prefill/RAG against installed Locally Uncensored.
- **Real GPU training (RTX 5080, Blackwell `sm_120`, `torch 2.10.0+cu128`, `unsloth 2026.9.4`):** QLoRA 4-bit SFT (`Qwen2.5-0.5B`) and 16-bit DoRA SFT (`SmolLM2-135M`) both completed end-to-end with saved safetensors adapters.
- **Android APK:** 53/53 Gradle unit tests, R8-minified Release APK, cold launch and `PairingScreen` verified with 0 crashes on Android 15 emulator.
- **MSI & Corporate Lite:** signed with Authenticode, release manifest RSA key and Android release keystore; verified signatures, manifest envelope, SHA-256 hashes, Corporate Lite acceptance, and local MSI upgrade/uninstall/reinstall cycle.

## Signing

MSI, first-party PE binaries and Corporate Lite launchers are signed with the publisher's self-signed Authenticode certificate; the Android APK with the release keystore; the update manifest with PaRuX's RSA release key. SmartScreen reputation is NOT established.

## Known limitations (honest)

- **Hardware / environments not tested:** real corporate PC (AppLocker/EDR/proxy, 8 GB RAM, no AVX-512), physical multi-GPU (2+ CUDA cards) training run, physical Android phone pairing/camera/mic, physical gamepad press-through with a running game, clean non-development Windows PC.
- **Platforms:** Windows x64 + Android client only (no Linux/macOS desktop builds).
- **Training methods:** real GPU run verified QLoRA 4-bit SFT and 16-bit DoRA SFT; DPO/ORPO/KTO/GRPO/GSPO/full-16bit/embedding-ft remain unit/planner-tested only.
- **3D & external viewers:** OBJ/STL/USDZ exports are verified by binary/format unit tests; external 3D viewer rendering (Blender/Quick Look) was not exercised.
- **Game Bar:** opt-in Game Bar block covers HKCU Game Bar settings and does not block the `Win+G` shortcut.
- **Self-signed Authenticode:** Windows SmartScreen may show an Unknown Publisher warning until a trusted EV/OV code-signing certificate is used.

## Links

[Install](guides/INSTALL.md) · [Troubleshooting](guides/TROUBLESHOOTING.md) · [Plan](../PARUX_EXECUTION_PLAN.md) · [Changelog](../CHANGELOG.md)
