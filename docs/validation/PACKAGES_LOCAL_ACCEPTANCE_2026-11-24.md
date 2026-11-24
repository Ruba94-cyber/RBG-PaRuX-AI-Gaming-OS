# Portable Standard and Corporate Lite (with Vulkan) - local acceptance (2026-11-24)

**Status: VALIDATED LOCALLY on one PC. Not validated on a restricted corporate Windows image, a clean PC, a discrete GPU or an 8 GB host. Unsigned.**

Host: Intel Core i5-10310U, 8 logical cores, 15.7 GB RAM (about 5-7 GB free while other desktop applications ran), Intel UHD Graphics (integrated, shared memory), Windows 11 Pro, standard user.

## Portable Standard (`artifacts/portable/PaRuX-4.4.0-portable-win-x64.zip`, ~2.7 GB)

`tests/portable/verify-standard-portable.ps1` -> **PASS 12/12** on the package built from `main` after the router fixes below: double-click start with no `PARUX_*` variable, bootstrap inside the folder, 4 documents indexed, grounded answer with citation (first token ~50 s on CPU/iGPU), planted instruction ignored, unsupported question declined, clean exit, folder moved and index kept, answer again after the move, nothing written under `%LOCALAPPDATA%\PaRuX`.

Defects found by this acceptance and fixed (all in `main`):
1. A model that is already resident was judged "not compatible" by the fit estimate (it counted memory held by other applications) and every chat got HTTP 503. A resident candidate is now always compatible.
2. The fit estimate judged only the full 32K window; `LaunchPlanner` halves the context before giving up. The estimate now accepts any window of that ladder (down to 4K) unless the user pinned a context. Closest-candidate numbers are now part of the refusal message (it read "needs 5598 MiB, 3576 MiB available").
3. Bounded recovery of idle PaRuX-owned retrieval ONNX sessions when nothing fits (non-Corporate editions; the Corporate block is unchanged).
4. `stage-msi-runtime.ps1` failed with "access denied" on the final `Directory.Move` (real-time scanner handle on a fresh directory of executables); it now retries up to 8 times with growing waits.
5. `-FetchAssets` takes the latest llama.cpp release (b12675), which has no reviewed SHA-256 pin; the package was therefore built from the pinned, previously verified assets (b12283, Qwen3-4B Q4_K_M, multilingual E5 embeddings + reranker). Nothing unpinned was accepted.

## Corporate Lite Offline (`artifacts/corporate-lite/PaRuX-Corporate-Lite-Offline-win-x64.zip`, 2703 MB)

Now also ships the pinned **Vulkan** engine (llama.cpp b10549, 97.2 MB, SHA-256 per file in the package manifest) so a corporate PC needs no installation and no network. `measure-corporate-lite.ps1 -EnforceAcceptance -UnicodeSpacesPath -ReadOnlyDataRoot` -> **PASS** (default configuration, then also with Vulkan active).

| Measure | CPU engine (default on this PC) | Vulkan on the integrated Intel UHD (forced) |
|---|---|---|
| Grounded answers | 4/4 + abstention | 4/4 + abstention |
| Speed | ~6.5 tok/s | ~3.6 tok/s |
| First token | ~22 s | ~61 s (first reply 107 s) |
| Total working set after chat | ~3.9 GB | ~4.2 GB |

Decision taken from this measurement: an **integrated** (shared-memory) GPU keeps the validated CPU engine by default; a **discrete** GPU uses Vulkan with automatic CPU fallback (150 s startup timeout, failure remembered 10 min, memory admission fail-closed against the profile RAM limit); `PARUX_CORPORATE_GPU=force` lets an integrated GPU try Vulkan and `PARUX_CORPORATE_GPU=0` (or `Start PaRuX Corporate Lite (CPU only).cmd`) forces CPU. Confirmed with the process command line that the forced path really launched `llama-server --device Vulkan0 --gpu-layers all`.

## Not verified

- Vulkan on a **discrete** GPU, a broken or missing driver, a VM without GPU: the fallback is covered by unit tests with simulated launches only.
- A real restricted corporate PC (AppLocker/WDAC, EDR, proxy, SmartScreen), clean PC without VC++/.NET, 8 GB RAM, large PDFs, signing, MSI (needs the WiX v7 EULA accepted by the owner: `PARUX_ACCEPT_WIX7_EULA=1`) and APK (no Java/Android SDK on this PC).
- Standard acceptance is sensitive to free RAM: on this PC three runs failed before the fixes and one passed after them.
