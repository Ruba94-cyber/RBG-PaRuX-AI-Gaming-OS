# Corporate Lite - automatic small-model fallback (2026-11-25)

**Status: IMPLEMENTED; proven in unit tests and on a real Core + bundled CPU llama-server under a test-owned RAM limit (this PC). NOT re-run through a rebuilt package and `measure-corporate-lite.ps1` end to end. NOT proven on a restricted corporate PC.**

## Behaviour

- Applies only in Corporate, only when the package default (Qwen3-4B) is refused by the memory plan and nobody asked for a model: `StartTextAsync` with no model named (Runtime Manager Ensure/Auto/On, warm-up) and `EnsureRoleLoadedAsync("standard")` for an unassigned role. A named model, a saved Chat assignment, a persisted role load or a manual lock is never replaced; it keeps the honest refusal.
- Candidates are the GGUF files listed in the package `docs/MANIFEST.json` (`alternateModel`, optional `alternateModels`), installed, with a valid header and the pinned SHA-256, strictly smaller than the default, largest first, at most 3 attempts, each through the same `CorporateCpuMemoryPlanner` admission as any launch. Nothing a planner refuses is started; if nothing fits the refusal states the default's numbers and that the smaller models were checked too.
- Saved settings (roles, routing preference, profiles) are not written. The choice is remembered in memory and the running small model is reused (no stop/reload per request) until free RAM grows by 1 GiB and the default would then fit.
- Observable: runtime status `corporateMemoryFallbackNotice` / `corporateMemoryFallbackNoticeIt`, a Runtime Manager diagnostics info issue, the Runtime action message, the chat `model_routed.fallbackReason`, and the launch trace. Text: `Memoria insufficiente per <default>: uso <small>.` / `Not enough memory for <default>: using <small>.`
- No network, no download, no new route.

## Real evidence (2026-11-25, this PC: Ryzen 7 8840U, 25.8 GB RAM)

Fresh Core built from this source (`src/PaRuX.Server/bin/Release/net10.0`), isolated data root, hard-linked copies of the package's Qwen3-4B and Qwen3-0.6B, the package's CPU llama.cpp b12283, `PARUX_CORPORATE=1`. A helper process owned by the test held RAM until 3.3 GiB were available (the harness technique; no Windows setting changed; helper stopped afterwards).

| Check | Result |
|---|---|
| `POST /api/v1/runtime/actions {Ensure}`, nothing loaded | 200, started Qwen3-0.6B-Q8_0 with the IT+EN notice (needs 3.4 GB, PaRuX can use 1.8 GB) |
| Status after | active model Qwen3-0.6B-Q8_0.gguf, notice EN+IT present |
| Second Ensure | "Already running", same llama-server PID (no reload) |
| Chat through the router (second fresh Core, chat first) | 200, `selectedModel` Qwen3-0.6B-Q8_0, answer "ready", `fallbackReason` carries the IT+EN notice; second chat same PID |
| Explicit load of the 4B at 3.3 GiB free | 400 honest refusal (needs 3.4 GB, 1.8 GB available); running 0.6B and its PID untouched |
| Saved settings | no role override file, no routing preference written; only the runtime pid identity file appeared |

## Not verified

- A package rebuilt with this code and the full `measure-corporate-lite.ps1` low-memory stage (extended to also check the notice and PID reuse; not executed).
- The Runtime Manager and chat UI rendering of the notice (status/diagnostics/route event data verified, WinUI not driven).
- Vulkan engine path with the fallback model, a 1.7B model (none shipped), 8 GB machines, corporate EDR/AppLocker.
