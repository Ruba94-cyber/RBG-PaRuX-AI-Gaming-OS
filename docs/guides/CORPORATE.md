# Corporate Edition (Corporate Lite)

Corporate Lite is a restricted, offline edition of RBG – PaRuX for local chat and document search.
**Status: PARTIAL.** It is not enterprise-certified.

## What it is

- AI chat and **Document search** only. The sidebar shows Chat, Document search, Settings and Help. Gaming, Create, cloud
  and internet features are not available.
- Packaged as a ZIP with a `parux.corporate` marker. All data stays in `PaRuXData` next to `PaRuX.exe`, never in the user
  profile; if that folder cannot be created or written, the application refuses to start instead of falling back to
  `%LOCALAPPDATA%`. No installation and no administrator rights are needed by the application.

## Folder layout

The ZIP has one top-level folder (`PaRuX-Corporate-Lite`). Inside it there is **one** file, `PaRuX.exe`, and four folders:

```text
PaRuX-Corporate-Lite\
  PaRuX.exe                                  small launcher, the only visible file
  app\                                       the application
    PaRuX.exe                                the real program (started by the launcher)
    PaRuX.Server\                            local Core
    parux.corporate                          Corporate marker (no loose marker file at the root)
    ... .NET / WinUI runtime, language folders, Assets
  PaRuXData\                                 all data: settings.json, models, runtimes, indexes, history
  docs\                                      README-corporate-lite.txt, MANIFEST.json, LICENSE.txt, notices
  launchers\
    Start PaRuX Corporate Lite.cmd           offline start
    Start PaRuX Corporate Lite (CPU only).cmd
    Start PaRuX Corporate Model Downloads.cmd
```

- Double-click `PaRuX.exe` (offline Corporate behaviour comes from the marker) or use a `.cmd` in `launchers\`. The
  `.cmd` files find the package root as `..` and use `..\PaRuXData`, so they work from any location.
- The launcher starts `app\PaRuX.exe` with the same arguments, waits for it and returns its exit code, so `PaRuX.exe --exit`
  still stops the running instance. It never needs administrator rights and uses no network. If the launcher is killed the
  application is stopped with it (Windows Job Object); if `app\PaRuX.exe` is missing it shows a message and exits with a
  non-zero code.
- The build verifies the finished ZIP and fails when the root holds anything other than `PaRuX.exe` and the folders
  `app`, `PaRuXData`, `docs`, `launchers`.
- Older, already extracted flat packages (all files beside `PaRuX.exe`) keep working; the MSI and MSIX editions are unchanged.
- Windows shows two processes named `PaRuX.exe` while the program runs: the launcher (a few MB) and the application in `app\`.
  A shortcut or taskbar pin created from the root `PaRuX.exe` opens the launcher, which is fine; the application window
  belongs to `app\PaRuX.exe`.
- Inference runs locally through the bundled llama.cpp runtimes. The validated **CPU** engine is the default and the
  fallback. The package also carries the hash-pinned **Vulkan** engine (llama.cpp b10549, the same lock entry as the
  Standard Portable): when a usable GPU adapter exists, PaRuX tries it first and, if it does not become healthy in time
  or fails (no driver, VM without GPU, allocation error, implausible output), it falls back to the CPU engine on its own
  and records why (Runtime Manager launch trace). Nothing is downloaded or installed and no other process is stopped.
  GPU memory shared with system RAM counts against the same profile RAM limit; the Vulkan start is skipped when it
  would not fit. Network features are off; the model-download and runtime-installer endpoints answer 403.
- **Default backend:** a discrete GPU uses Vulkan; an integrated (shared-memory) GPU stays on the CPU engine, because on the Intel UHD reference PC Vulkan measured 3.6 tok/s against 6.5 tok/s on the CPU (first reply 107 s versus 22 s). `PARUX_CORPORATE_GPU=force` lets an integrated GPU try Vulkan anyway.
- **IT switch:** `PARUX_CORPORATE_GPU=0` (also `false`, `off`, `no`, `cpu`) forces CPU only. The package includes
  `launchers\Start PaRuX Corporate Lite (CPU only).cmd`, which sets it.

## Packages

| Variant | Contains | Note |
|---|---|---|
| Corporate Lite with model | CPU and Vulkan runtimes, Qwen3-4B Q4_K_M, ONNX embedding and reranker | Complete offline chat. The 4.2.0 test package was 2,668 MB (CPU only); the Vulkan runtime adds its unpacked size, printed in `docs\README-corporate-lite.txt` and `docs\MANIFEST.json`. |
| NoModel | CPU and Vulkan runtimes, no chat model | Not a complete chat setup on its own. |

The 4.1.0 release record lists the NoModel ZIP only (280,483,402 bytes). Review runtime and model licences before distribution.

## Use

1. Extract the ZIP and run `PaRuX.exe` or `launchers\Start PaRuX Corporate Lite.cmd` (offline start).
2. Import a GGUF model in **Model Hub** (it is copied into `PaRuXData/models`) if the package has none.
3. Open **Document search**, use **Add folder...**, wait for indexing, then ask questions in Chat. Answers cite sources.
4. Only with IT approval: `launchers\Start PaRuX Corporate Model Downloads.cmd` enables GGUF downloads. Inference stays local.

## What was measured

Reference PC: Ryzen 7 8840U, CPU engine (see [PARUX_EXECUTION_PLAN.md](../../PARUX_EXECUTION_PLAN.md) and
[QA_EVIDENCE.md](../QA_EVIDENCE.md)):

- Strict gate on a synthetic corpus of 319 files: grounded answers 4/4, abstention OK, first token about 13.5 s, about 13 tokens/s.
- Core plus llama-server peak about 3.7 GB; the run used about **7 GB** of RAM in total on that PC.
- 30,000-page synthetic PDF indexed to completion (about 41 minutes); 0 non-loopback connections during the gate.

## Not established

- A clean non-developer PC with only 8 GB RAM, firewall-blocked network, OCR on scanned PDFs and disk-full behaviour: **NOT RUN**.
- Roles, per-user policies and an audit log **do not exist**.
- LAN access and the browser passphrase portal were tested by unit tests only.
- NTFS ACL support and IT approval may be required; FAT/exFAT is not certified.

Related: [PORTABLE.md](PORTABLE.md), [PRIVACY.md](PRIVACY.md), [SECURITY_REVIEW.md](../SECURITY_REVIEW.md),
[CORPORATE_TECHNICAL_RAG_ACCEPTANCE.md](../CORPORATE_TECHNICAL_RAG_ACCEPTANCE.md).
