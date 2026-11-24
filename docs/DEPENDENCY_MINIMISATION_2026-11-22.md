# Dependency minimisation (2026-11-22)

Owner requirement: external dependencies reduced to the minimum (ideally Playnite + the Aniki ReMake theme for Gaming only). Whatever can live inside the app does. Everything else is offered as "download now or skip" by the installer (first run) and stays available in Settings.

Status of this document: audit from current source and lock files. Sizes marked `~` come from lock files or are approximations and were not measured on a real download. Nothing here was built, packaged, or run on a GPU.

## 1. Audit

Ownership: PM = PARUX_MANAGED, UM = USER_MANAGED, SD = SYSTEM_DETECTED. "MSI" = installer built by `scripts/build-msi.ps1` (WiX, harvests `runtime-baseline/` staged by `scripts/stage-msi-runtime.ps1`). "Portable" = `scripts/build-portable.ps1` Standard Offline ZIP.

| # | Component | What it is for | Ownership | Size | In MSI | In Portable | Optional? | In-app / drop? |
|---|-----------|----------------|-----------|------|--------|-------------|-----------|----------------|
| 1 | llama.cpp CPU engine | Base offline chat | PM | small (`packaging/llama-cpu-archive.lock.json`) | no (Vulkan covers it) | yes | No (core) | Keep, core |
| 2 | llama.cpp Vulkan (b10549) | Chat on any GPU | PM | ~33 MB | yes (full and lean) | yes | Yes | Keep bundled: smallest GPU engine, makes first chat work offline |
| 3 | llama.cpp CUDA 13.4 (b11871) | Faster chat on NVIDIA | PM | ~152 MB engine | yes (full) | no | Yes | Move to optional download (see ranked plan #1) |
| 4 | CUDA runtime DLLs (cudart/cublas) | Needed by #3 | PM | ~403 MB | yes (full) | no | Yes | Same as #3; largest single payload in the MSI; NVIDIA EULA redistribution (`NVIDIA_CUDA_NOTICE.txt`) goes away with it |
| 5 | uv 0.12.1 | Provisions private Python | PM | ~18 MB | yes (full) | no | Yes | Drop from installer: `ManagedPythonRuntime.EnsureUvAsync` already downloads the pinned archive and verifies its SHA-256 when no staged uv exists |
| 6 | Private Python + wheels (speech/Kokoro, avatar, diffusers, 3D, cutout, LoRA, training) | Voice, images, 3D, training | PM | hundreds of MB to GB per feature (`tools/managed-locks/*.in`) | no | no | Yes, on demand | Already on demand and hash-locked. Never binds to user Python |
| 7 | ComfyUI portable | Image/video generation | PM or UM | ~2 GB (approximate, per variant) | no | no | Yes | Keep as optional. Longer term, `diffusers` engine (#6) replaces the need for ComfyUI in Normal mode |
| 8 | Git | Coding tools | SD / UM | n/a | no | no | Yes | Locate-only. Could be dropped by using built-in git operations only for the coding agent (needs audit, not done) |
| 9 | FFmpeg | Audio/video conversion | SD / UM | n/a | no | no | Yes | Locate-only today. Candidate for a PaRuX-managed verified download (no installer exists yet) |
| 10 | Node.js / npm | Some extensions, browser tooling | SD / UM | n/a | no | no | Yes | Locate-only. Only needed by a few extensions: keep skippable |
| 11 | ONNX Runtime + embedding/reranker models | Document search | PM (NuGet + bundled models) | in app / Portable ZIP | NuGet in app | models in Portable | No (core search) | In-app already |
| 12 | Qwen3-4B Q4_K_M starter model | First offline chat | PM | ~2.5 GB | no | yes | Yes | Portable-only; MSI relies on Model Hub download |
| 13 | Playnite | Gaming library and Console Mode | UM (detected) or PM (PaRuX installs after owner confirms version and SHA-256) | per upstream release | no | no | Yes | **Allowed dependency.** `PlayniteInstaller` already verifies SHA-256, per-user, never touches an existing folder |
| 14 | Aniki ReMake theme | Playnite console look | UM | small | no | no | Yes | **Allowed dependency** |
| 15 | Sunshine | Game streaming host | UM (SD) | n/a | no | no | Yes | Used only if present (`StreamingEndpoints`); never installed by PaRuX. Candidate to drop once Playnite streaming is the only path |
| 16 | XOutput, Nyrna | Controller mapping, process suspend | UM | n/a | no | no | Yes, legacy | Opt-in, legacy defaults. Candidate to drop: first-party `win32native` provider and Console navigation replace them |
| 17 | Microsoft Edge WebView2 / Windows App Runtime / VC++ runtime | App shell | SD / bundled | n/a | app-local | app-local | No | Already self-contained |
| 18 | SearXNG | Private web search | UM | n/a | no | no | Yes | User-operated loopback endpoint only; PaRuX never ships it |
| 19 | MonoTorrent, SharpCompress, PdfPig, Parquet.Net, QRCoder, OpenXml, ExcelDataReader | In-process libraries | PM (NuGet) | in app | in app | in app | No | Reviewed list in `packaging/dependency-boundaries.json`; no runtime install |
| 20 | Corporate Lite CPU pack | Locked-down edition | PM | separate package | n/a | n/a | n/a | Unchanged: no optional installers at all |

Notes:
- The Runtime Manager inventory (`ComponentInventoryService`) only knows rows 2-3, 5-6 (uv/python), 7-10. Playnite has its own flow (`PlayniteInstaller`, `SettingsView.Playnite.cs`). XOutput/Nyrna/Sunshine are detected by Gaming providers, not by the inventory.
- The Microsoft Store edition never downloads programs (`StoreEditionPolicy.AllowsExecutableDownloads`); the Optional components page shows the reason instead of a Download button.

## 2. Ranked plan

1. **Lean MSI (largest win, low risk).** Stage only Vulkan in the MSI. Removes about 555 MB of CUDA payload (rows 3 and 4) plus 18 MB uv from the harvested set, and the NVIDIA redistribution notice from the MSI. CUDA becomes an Optional component, recommended automatically when an NVIDIA GPU is detected. `BundledBaselineRuntimeBootstrap` already handles a Vulkan-only payload. Switch added in this change: `-p:PaRuXMsiRuntimeProfile=lean` (default stays `full`, so nothing changes until the owner decides).
2. **Optional components page (done in this slice).** One list in Settings and in the first-run Components step.
3. **First-run offer for the recommended optional set** (CUDA on NVIDIA machines, a starter model): reuse OnboardingAutoSetup, always with an explicit consent button and a Skip.
4. **FFmpeg as PaRuX-managed download** with pinned URL and SHA-256 (new lock entry), so voice and video do not require the owner to find FFmpeg. Locate-existing stays.
5. **Add Playnite and Aniki ReMake theme as rows** on the same page (state from `PlayniteLocator`, Download now through `PlayniteInstaller`). They are the two accepted external dependencies and should be visible in one place.
6. **Drop or demote Gaming extras**: Sunshine, XOutput, Nyrna stay opt-in and unlisted until Playnite streaming covers the use case; then delete the providers.
7. **Git-free coding agent audit**: decide whether built-in git handling is sufficient; if so remove Git from the list.
8. **Remove for ComfyUI and Python/uv**: needs separate safety work because ComfyUI holds user outputs and checkpoints. Not offered in this slice.

## 3. This slice (implemented)

- `src/PaRuX.Contracts/OptionalComponents.cs`: pure policy. Catalog (name, plain-language purpose, approximate size, EN and IT), state (Installed / Not installed / Detected on this PC), legal buttons, download pre-flight (permission, known free space, archive plus extracted copy plus 256 MB headroom), removal decision.
- Buttons: Download now (PaRuX-managed, not installed, downloads allowed), Locate existing, Skip / Set up later, Remove (PARUX_MANAGED, installed, stopped, reinstallable, strictly inside PaRuX's runtime folder), Forget location (UM only, never deletes files).
- Download itself is unchanged and reuses the Runtime Manager install service: https GitHub release hosts only, SHA-256 verified before extraction, `.partial` resume with Content-Range proof, cancel, background job survives Core restart. New: a Cancel download button in Settings that also cancels the server job, and a disk-space gate before starting.
- `POST /api/v1/runtime/components/{id}/remove-managed` (desktop owner only; browser sessions are refused like the other component routes). Supports `llama-server-cuda` and `llama-server-vulkan` only.
- UI: `OptionalComponentCards` shared by Settings (section "OPTIONAL COMPONENTS") and the first-run Components step. AutomationIds: `Settings_OptionalComponent_{id}_{Row|Title|State|Purpose|Size|Path|Note|DownloadButton|LocateButton|SkipButton|UnskipButton|RemoveButton|ForgetLocationButton}`, `Onboarding_OptionalComponent_{id}_...`, `Settings_Components_CancelDownload_Button`. Existing `Settings_Components_List|Progress|StatusText` kept.
- Nothing is installed silently; detection is read-only; offline shows an honest failure from the existing install path; the page works with no network (it only reads the local inventory).

## 4. Installer design (design only)

Principle: the installer installs PaRuX; optional components are chosen in the app, on first run, with the same page used later in Settings. No MSI custom action downloads anything (a custom action would run elevated, hide failures behind the MSI progress UI and break offline or enterprise installs).

### Bundled-by-default decision rule
A payload is bundled only if all hold: (a) the first chat or search must work offline on a clean PC without it; (b) it is small enough that a failed download would be worse than the size; (c) its licence allows redistribution without extra notice burden. Result:

| Payload | Decision |
|---------|----------|
| App, Core, WinUI/App SDK, ONNX embedding + reranker (Portable) | Bundled |
| llama.cpp Vulkan (~33 MB) | Bundled (MSI and Portable) |
| llama.cpp CPU (Portable and Corporate) | Bundled |
| Starter model | Portable Standard only; MSI offers it from Model Hub on first run |
| llama.cpp CUDA + CUDA runtime (~555 MB) | Optional download; first run proposes it when an NVIDIA GPU is present |
| uv | Not bundled (pinned verified download on demand) |
| ComfyUI, Python feature runtimes, FFmpeg, Git, Node | Optional; never bundled |
| Playnite, Aniki ReMake | Optional; owner confirms version and SHA-256 |

### First run flow (MSI lean and Portable)
1. Components step lists the Optional components page. Each row: state, size, purpose, Download now / Locate existing / Skip.
2. Recommended rows are visually first (CUDA only on NVIDIA). Nothing preselected, nothing starts without a button press.
3. Skipped rows can be done later in Settings > Optional components (same AutomationIds, same code path).
4. Offline PC: rows that need a download show the honest failure from the install path; Locate existing and Skip still work; chat works on the bundled Vulkan or CPU engine.

### Shrinking the installer: what disappears
Lean MSI vs full MSI, from `packaging/windows-baseline-runtime.lock.json`: CUDA engine ~152 MB, CUDA runtime DLLs ~403 MB, uv ~18 MB (about 573 MB uncompressed payload, less after MSI compression), the NVIDIA CUDA redistribution notice, and the CUDA branch of the first-load bootstrap. Portable already lacks all of these. The Vulkan engine stays.

Trade-offs to accept: NVIDIA owners download ~555 MB once instead of getting it with the installer; first CUDA chat is no longer instant offline. Mitigation: first-run proposes it immediately and the download is resumable.

### Script-level change made now
`installer/PaRuX.Setup/PaRuX.Setup.wixproj`: property `PaRuXMsiRuntimeProfile` (`full` default, `lean`). `lean` passes `-ArtifactIds llama.cpp-vulkan` to `stage-msi-runtime.ps1`, which already supports selection and fails closed on unknown ids. Pass `-p:PaRuXMsiRuntimeProfile=lean` to `scripts/build-msi.ps1` builds. A source-contract test pins the default and the lean mapping. No packaging script was run.

### Open decisions for the owner
- Switch the default profile to `lean` when development ends (recommended).
- Whether the first run may auto-propose CUDA download pre-ticked on NVIDIA machines (currently: shown first, not pre-ticked).

## 5. Not verified
- No real download, install, remove, cancel or resume was exercised end to end; no WinUI session was driven; no GPU used.
- Component sizes for ComfyUI and the Python features are approximate.
- Msi `lean` build was not built; only the property wiring and the stage script contract were read.
- First-run step uses the same card builder but the onboarding Cancel for a running download was not added (onboarding keeps its existing busy handling).
