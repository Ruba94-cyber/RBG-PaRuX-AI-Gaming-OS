# Corporate Lite Offline - local acceptance (2026-11-24)

**Status: VALIDATED LOCALLY (synthetic corpus). NOT validated on a restricted corporate Windows PC.**

- Source: `main` at `eb66df6e` plus the harness fix in the commit that adds this file.
- Package: `PaRuX-Corporate-Lite-Offline-win-x64.zip`, 2669 MB, built locally from pinned assets (llama.cpp b12283 CPU, Qwen3-4B Q4_K_M, multilingual E5 embeddings and reranker). SHA-256 `978e1c90a28963f8525cb7fa32cfdcfcc71de6bb55acb53edcb4c8b3551f96bd`. Unsigned.
- Host: Intel Core i5-10310U, 8 logical cores, 15.7 GB RAM, Windows 11 Pro, standard (non-elevated) user. This is a developer PC, not a locked-down corporate image.
- Command: `tests/corporate/measure-corporate-lite.ps1 -EnforceAcceptance -UnicodeSpacesPath -ReadOnlyDataRoot` -> `Corporate offline synthetic-corpus acceptance PASS` (third run; evidence in `results/corporate-main-20261124/`, kept local).

## Evidence (all measured, one run)

| Check | Result |
|---|---|
| Extract / cold start to Core | 42.9 s / 7.1 s |
| Idle after start (no model loaded) | total working set 572 MB (App 190, Core 382) |
| Index 319 files (300 synthetic + edge cases) | done, 1938 chunks, 307 indexed, 3 metadata-only, 9 failed on purpose (corrupt/encrypted) |
| Search | exact-id p95 13 ms; semantic p95 242 ms; hybrid p95 ~1.3 s; 0 errors |
| Grounded chat | 4/4 with fact and citation; abstention on unsupported question; first token 15-22 s, ~6.5 tok/s (CPU) |
| Memory after chat | llama-server 3.3 GB, Core 450 MB, App 190 MB, total ~3.9 GB; CPU ~0% idle |
| ONNX embedding + reranker | real embed/rerank call, then status loaded |
| VC++ runtime | `vcruntime140` / `msvcp140` loaded from the extracted package folder |
| Network | no listener on a non-loopback address; downloads refused (403) |
| Writes outside portable folder | none (file and HKCU snapshot before/after) |
| Clean exit (`PaRuX.exe --exit`) | no process left |
| Hard kill of `PaRuX.exe` | no package process left within 5 s; same data root restarts and the Core answers (stale `bootstrap.json` handled) |
| Path with spaces, accents and CJK | starts, Core answers |
| Non-writable `PaRuXData` | no Core started, message names the data root, fallback log written to `%TEMP%`, no leftover process |

## Defects found and fixed during this validation

1. `PARUX_EXECUTION_PLAN.md` had been truncated from 1 MB to 791 bytes by commit `26b657ff` (broke `ActiveReleaseStatusContractTests` and `tools/release-gate.ps1`). Restored in `be5705a8`.
2. Corporate warm-up loaded the 2.9 GB model at every start; now deferred to the first chat request.
3. No Core singleton (a slow start could spawn a second Core on the same data); named mutex per data root added. App mutex now per data root in Release.
4. Non-writable data root exited silently; it now shows a message and writes a fallback log.
5. Missing `PaRuXData` next to the Corporate marker fell back to `%LOCALAPPDATA%`; it is now created beside the marker or the start is refused.
6. PID/profile record write failures left an untracked `llama-server`; the process is now stopped and the error reported.
7. Extracted attachments and backups used `%TEMP%`; Corporate now uses `PaRuXData\tmp`.
8. `ValidatedRemoteConnector` could be reached in Corporate (cloud fallback test, external provider probe) and bypassed the dead-proxy guard; non-loopback is now refused when Corporate is active. A source test blocks new `UseProxy = false` outside an allowlist.
9. Harness: ONNX status check was non-loading by design (now probes first), hard-kill stage did not exist, read-only stage criteria, stale bootstrap on restart.

## Not verified (do not claim)

- A real restricted corporate PC (AppLocker/WDAC, EDR, proxy, SmartScreen/Mark-of-the-Web, no VC++/.NET installed). A VM or Windows Sandbox run is still required.
- 8 GB RAM and throttled memory profiles; this host has 15.7 GB.
- Large documents (30,000-page PDF gate), OCR, disk-full, sleep/hibernate, two Windows sessions.
- Package signing. Open item: `ValidatedRemoteConnector` is the only audited egress path; a firewall-level block was not tested.
