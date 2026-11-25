# PaRuX Third-Party Notices

PaRuX's own source code is licensed under the Functional Source License 1.1, MIT Future License (FSL-1.1-MIT; see [LICENSE](LICENSE)) - source-available, with each version converting to MIT two years after release. This operational notice records software **redistributed in PaRuX build artifacts** and distinguishes it from tools and model weights that PaRuX can discover or download/install on explicit user request.

The repository pins concrete dependency versions in its project files. Upstream license files and package metadata remain the authoritative terms for each dependency; this notice is maintained so a GitHub MSI/APK release does not obscure what is actually being redistributed.

## Redistributed in the Windows MSI / self-contained publish

The GitHub MSI includes the self-contained WinUI desktop application, self-contained **PaRuX Server** (`PaRuX.Server` source project), CLI and protected Update Helper. Therefore dependencies of those projects are part of the Windows redistribution boundary.

| Component | License | Used by | Purpose |
|---|---|---|---|
| .NET 10 / ASP.NET Core | MIT and accompanying .NET third-party notices | PaRuX Desktop/Server/CLI/Update Helper | Managed runtime, base libraries and local web host |
| Microsoft.WindowsAppSDK | Microsoft Software License Terms - Windows App SDK (not MIT; text shipped in the NuGet package as `license.txt`, verified 1.8.260804001) | PaRuX Desktop | WinUI 3 application runtime |
| H.NotifyIcon.WinUI | MIT | PaRuX Desktop | System tray |
| Microsoft.Data.Sqlite | MIT | Desktop/Server | Durable local stores |
| DocumentFormat.OpenXml | MIT | Desktop/Server | Office document generation/inspection |
| ExcelDataReader | MIT | Desktop/Server | Spreadsheet import |
| PdfPig | Apache-2.0 | Server | PDF text extraction with page numbers (LKS-1) |
| QRCoder | MIT | Desktop | Pairing QR generation |
| System.Drawing.Common | MIT | Desktop/Server | Windows image/QR support and owner-approved 64x64 game skin PNG conversion |
| ModelContextProtocol | Apache-2.0 | Server | Outbound MCP client capabilities |
| ModelContextProtocol.AspNetCore | Apache-2.0 | Server | PaRuX MCP server/ASP.NET integration |
| Microsoft.ML.OnnxRuntime | MIT | Server | Local ONNX embedding inference |
| Microsoft.CodeAnalysis.CSharp | MIT | Server | Roslyn C# syntax/semantic analysis used by code-aware tooling |
| Microsoft.ML.OnnxRuntimeGenAI.WinML | MIT | Server | ONNX Runtime GenAI Windows ML worker (`PaRuX.WinMlWorker`) |
| Microsoft.Windows.AI.MachineLearning | Microsoft Software License Terms - Windows ML Runtime (proprietary; text shipped in the NuGet package as `license.txt`) | Server | Windows ML execution-provider management. Redistribution is limited to the "Distributable Code" section of that license; this row records the terms and is not a legal clearance. |
| System.Management | MIT | Server | Windows WMI hardware inventory |
| System.Security.Cryptography.ProtectedData | MIT | Server | Windows DPAPI protection of local secrets |
| Microsoft.ML.Tokenizers | MIT | Server | Tokenization for local embedding/retrieval pipeline |
| Parquet.Net | MIT | Server | Dataset Hub Parquet reading/writing |
| LiteDB | MIT | Server | Reads a private copy of a Playnite library as untyped BSON (Playnite importer) |
| SharpCompress | MIT | Server | Safe extraction support for managed runtime archives; retain any notices present in the upstream package/distribution |
| MonoTorrent | MIT | Server | Version 3.0.2. In-process BitTorrent transfer backend for individually authorized magnet and local `.torrent` inputs; no separate qBittorrent process required. Retain the upstream MIT license/copyright notices in redistributions; package dependency closure and Windows release build remain to be verified. |
| llama.cpp `convert_lora_to_gguf.py` + conversion package | MIT (llama.cpp) | Server | Vendored adapter conversion scripts under `llama-cpp-conversion/`; exact upstream text is in `llama-cpp-conversion/LICENSE-llama.cpp` |
| llama.cpp / llama-server baseline archives | MIT | Server | The MSI includes a hash-locked b10549 Vulkan build and b11871 CUDA 13.4 build. The builds remain separate PaRuX-managed runtimes after first-run bootstrap. Copyright (c) 2023-2026 The ggml authors; the MIT license text is shipped as `LICENSE-llama.cpp.txt` beside every bundled `llama-server.exe` and at the package root. |
| Microsoft Visual C++ runtime (`vcruntime140*.dll`, `msvcp140*.dll`) | Microsoft Visual Studio redistributable terms ("Distributable Code") | Portable ZIPs only | Copied unmodified and Microsoft-signed from the build PC's Visual Studio `VC\Redist\MSVC\<version>\x64\Microsoft.VC143.CRT` folder, app-local beside the Core and the bundled CPU llama.cpp engine, so that Standard Portable and Corporate Lite need no installed redistributable. Not part of the MSI. |
| NVIDIA CUDA Runtime and cuBLAS DLLs | NVIDIA CUDA Toolkit EULA, Attachment A | Server | The CUDA baseline includes `cudart64_13.dll`, `cublas64_13.dll` and `cublasLt64_13.dll`; see [NVIDIA_CUDA_NOTICE.txt](NVIDIA_CUDA_NOTICE.txt) for the redistribution boundary and current EULA link. |
| uv (Astral) | MIT OR Apache-2.0 | Server | Hash-locked `uv 0.12.1` is shipped in `runtime-tools/uv` for PaRuX-managed Python environments. |

The MSI build copies this notice, the NVIDIA CUDA notice, the llama.cpp MIT license text (`LICENSE-llama.cpp.txt`), the PaRuX FSL-1.1-MIT license and `OPEN_SOURCE_PROVENANCE.md` beside the installed binaries. The Microsoft Store (MSIX) package carries the same notices except the NVIDIA CUDA notice, because it contains no CUDA runtime. It does **not** bundle model weights, ComfyUI, Unsloth, TRELLIS or other large AI engines listed below.

## Optional vendor payloads in the Windows MSI (off by default)

The MSI built without `-NoRedist` carries two **optional features that are NOT installed by default** (selectable on a setup page, or `ADDLOCAL=` / `PARUX_ADD_DRIVERS=1` / `PARUX_ADD_SUNSHINE=1` for a silent install). The vendor files are redistributed **byte for byte unmodified**; their pins are in [packaging/redist-installers.lock.json](packaging/redist-installers.lock.json) and are checked at build time (size, SHA-256, Authenticode) and again before anything is run. Licence texts and, for GPL, the written source offer ship beside the payload under `Redist\` in the install folder. Checked 2026-11-25; this is a record of terms, not legal clearance.

| Component | Version | License | Asset (official GitHub release) and SHA-256 | MSI feature / how it is used |
|---|---|---|---|---|
| ViGEm Bus Driver (ViGEmBus) | 1.22.0 | BSD-3-Clause (Copyright (c) 2016-2020 Nefarius Software Solutions e.U.) | `ViGEmBus_1.22.0_x64_x86_arm64.exe` from `nefarius/ViGEmBus` tag `v1.22.0`; SHA-256 `89220a7865076b342892f98865f3499fb7c4cfd673159e89d352c360fd014c6a`; Authenticode "Nefarius Software Solutions e.U." | Feature "Controller drivers (ViGEmBus + HidHide)". The vendor installer is run silently, elevated, only if the driver is not already on the PC. Upstream retired and archived the project; 1.22.0 is the final release. |
| HidHide | 1.5.230.0 | MIT (Copyright (c) 2020 Eric Korff de Gidts; (c) 2021-2023 Benjamin Hoglinger-Stelzer) | `HidHide_1.5.230_x64.exe` from `nefarius/HidHide` tag `v1.5.230.0`; SHA-256 `f4bbbcb82e6258641b887c74bc81c4c5f66e4aa811808dfc304347687b7605f6`; Authenticode "Nefarius Software Solutions e.U." | Same feature as above, same rules. |
| Sunshine | 2026.914.233613 | GPL-3.0-only | `Sunshine-Windows-AMD64-lite.zip` (portable package) from `LizardByte/Sunshine`, now served at `SunshineStream/Sunshine`, tag `v2026.914.233613`; SHA-256 `233008e46f4c0e501a586cbfd6c4fd4a4c0d414a0b5fc7f13c070eb92ec3824b`; matches the digest GitHub publishes; the binaries inside are not signed | Feature "Sunshine (streaming host)". The ZIP is only placed; PaRuX unpacks it into its own per-user folder when the owner asks in Settings. No Windows service, firewall rule or autostart is created. Run by PaRuX as a separate process, never linked. |

**GPL-3.0 source offer (Sunshine).** The Corresponding Source of exactly this version is at <https://github.com/SunshineStream/Sunshine/tree/v2026.914.233613> (archive: <https://github.com/SunshineStream/Sunshine/archive/refs/tags/v2026.914.233613.tar.gz>, tag commit `63d35f702ee9e362e43263742981836ec0710384`; build dependencies: <https://github.com/LizardByte/build-deps>). The same offer, with the three-year written-offer wording, is installed as `Redist\Sunshine\SOURCE-OFFER-Sunshine.txt`. The GPL text is `Redist\Sunshine\LICENSE-Sunshine.txt`. The vendor Windows MSI of Sunshine is deliberately not bundled (see the lock's `excluded` entry).

The MSI grows by about 52 MB of payload (about 45 MB after MSI compression, because the ZIP and the signed installers barely compress) when the optional payloads are included; `-NoRedist` builds are unchanged in size.

## Bundled in Standard Portable / Corporate Lite ZIPs — not in the MSI

These artifacts are staged by `scripts/portable-offline-assets.ps1` and `scripts/build-corporate-lite.ps1`. The model licences below were read from the upstream Hugging Face model metadata on 2026-11-11.

| Component | Source (pinned in script) | License | Notes |
|---|---|---|---|
| Qwen3-4B-Q4_K_M.gguf (starter chat model) | `Qwen/Qwen3-4B-GGUF`, sha256 pinned (`scripts/build-corporate-lite.ps1:174`, `scripts/portable-offline-assets.ps1:137`) | Apache-2.0 | Licence tag of `Qwen/Qwen3-4B-GGUF` and of its base model `Qwen/Qwen3-4B` |
| Qwen3-0.6B-Q8_0.gguf (optional low-memory Corporate starter) | `Qwen/Qwen3-0.6B-GGUF`, official Q8_0 file, SHA-256 `9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031` pinned in `scripts/build-corporate-lite.ps1 -Fetch -LowMemoryStarter` | Apache-2.0 | Alternative, not installed alongside the 4B model; lower quality for complex work. Requires Windows low-RAM acceptance before redistribution. |
| Operator-supplied Corporate GGUF (`-ModelPath`) | Explicit local file supplied by the package builder; hash recorded in the resulting manifest | **Unverified unless independently reviewed** | The builder does not infer that a renamed file is Qwen3 or Apache-2.0. Check exact upstream provenance and downstream licence before distribution. |
| all-MiniLM-L6-v2 (ONNX embedding model) | `sentence-transformers/all-MiniLM-L6-v2`, revision `1110a243...`, sha256 pinned (`scripts/build-corporate-lite.ps1:179-181`) | Apache-2.0 | Licence tag of `sentence-transformers/all-MiniLM-L6-v2` |
| ms-marco-MiniLM-L-6-v2 (ONNX reranker, optional) | `Xenova/ms-marco-MiniLM-L-6-v2`, revision `a0914435...`, sha256 pinned (`scripts/build-corporate-lite.ps1:189-191`) | Apache-2.0 (base model) | The `Xenova` ONNX conversion carries no licence tag of its own; its declared base model `cross-encoder/ms-marco-MiniLM-L6-v2` is Apache-2.0 |
| llama.cpp CPU x64 runtime (`llama-server.exe` and DLLs) | Latest `ggml-org/llama.cpp` release asset matching `llama-*-bin-win-cpu-x64.zip`, **not version- or hash-pinned** (`scripts/portable-offline-assets.ps1:108-113`, `scripts/build-corporate-lite.ps1:145-148`) | MIT (llama.cpp upstream; see the MSI table) | The exact build is not recorded, so the notice cannot name a version |

## Agent Platform V2 interoperability — not redistributed

The experimental V2 branch integrates with **already-installed, operator-managed** external programs using command-line or HTTP protocols. None of the following sources or executables is included in PaRuX release artifacts by this work:

| External service/tool | Upstream | License | Integration boundary |
|---|---|---|---|
| Microsoft WinApp CLI | https://github.com/microsoft/winappCli | MIT | Explicit-path, externally installed Windows UIA CLI |
| OpenCode | https://github.com/anomalyco/opencode | MIT | Optional read-only local health/session metadata observer; supervised execution backend NOT implemented |
| SearXNG | https://github.com/searxng/searxng | AGPL-3.0-or-later | User-operated loopback JSON search HTTP endpoint; no bundled server/source |

The October 2026 Agent Platform V2 comparison additionally consulted Jan
(Apache-2.0), OpenHands SDK (MIT), and Odysseus (AGPL-3.0-or-later)
as **reference implementations**, without copying their source. Their
design ideas were adapted into new PaRuX-authored C# code and tests. This
creates no new vendored-source redistribution items. See
`docs/OPEN_SOURCE_AGENT_REVIEW_2026-10.md` and
`OPEN_SOURCE_PROVENANCE.md`.

MIT obligations apply if MIT source is actually copied/distributed, not merely because PaRuX calls another independently installed executable. SearXNG is **AGPL**, not MIT: PaRuX does not incorporate its source, and any future bundling/modification must receive a separate license-compliance review. All current C# adapters and PowerShell acceptance harnesses are PaRuX-authored code, not transcriptions of these upstream implementations.

## Redistributed in the Android APK

| Component/family | License | Purpose |
|---|---|---|
| AndroidX Core / Activity / Lifecycle / Navigation / Security / DataStore / WorkManager | Apache-2.0 | Android application framework, durable pairing/state and local background supervision |
| Jetpack Compose / Material 3 | Apache-2.0 | Android UI |
| CameraX | Apache-2.0 | Camera capture for pairing QR |
| ZXing Core | Apache-2.0 | Fully on-device QR decoding for pairing |
| Media3 | Apache-2.0 | Local media playback |
| Retrofit | Apache-2.0 | Paired-Server HTTP API client |
| OkHttp | Apache-2.0 | Certificate-pinned TLS/network transport; no HTTP logging interceptor is shipped |
| kotlinx-coroutines | Apache-2.0 | Async/concurrency |
| kotlinx-serialization | Apache-2.0 | JSON contracts |
| Coil | Apache-2.0 | Authenticated image loading |
| Kotlin standard library (`kotlin-stdlib`, transitive) | Apache-2.0 (Maven Central POM) | Kotlin runtime used by the APK |
| okio-jvm (transitive of OkHttp) | Apache-2.0 (Maven Central POM) | I/O primitives used by OkHttp |

The Android app does not embed a local LLM or proprietary cloud inference provider. QR decoding is local, and paired application traffic is HTTPS with certificate pinning. Android is a control plane for the user's PaRuX Server, not a cloud-AI client.

## Downloaded/installed on explicit user action — not shipped inside the MSI/APK

PaRuX can either **locate an existing USER_MANAGED installation** or create an isolated **PARUX_MANAGED** copy of supported runtimes. A managed copy is still third-party software under its upstream license; `PARUX_MANAGED` describes lifecycle ownership, not relicensing or authorship.

| Component | Upstream license / terms | Role | PaRuX relationship |
|---|---|---|---|
| llama.cpp / llama-server | MIT | Local LLM serving | Existing installations may be located; the MSI also ships hash-locked baseline Vulkan and CUDA builds. Later managed builds are downloaded on request. |
| NVIDIA CUDA Runtime and cuBLAS DLLs | NVIDIA CUDA Toolkit EULA, Attachment A | CUDA runtime dependencies | The MSI includes only the three DLLs listed above, alongside PaRuX's bundled CUDA llama.cpp application; see `NVIDIA_CUDA_NOTICE.txt`. |
| ComfyUI | GPL-3.0 | Local image/video generation backend | Separate local process reached over HTTP. PaRuX may install/manage a copy on explicit request but does not link its GPL code into PaRuX binaries. ComfyUI remains governed by GPL-3.0. |
| TRELLIS / compatible Windows runtime | Verify the exact selected upstream/fork revision; Microsoft's TRELLIS core is MIT | 3D generation | Separate local runtime, downloaded/cloned on request; not in MSI. |
| Unsloth | Apache-2.0 | LoRA/QLoRA training | Installed into an isolated local Python environment on request. |
| PyTorch / Transformers / PEFT / sentencepiece / protobuf / gguf and conversion/training environment dependencies | Their respective upstream licenses | Adapter conversion/training | Installed into isolated local environments on demand, not committed or embedded in PaRuX MSI. |
| LatentSync (ByteDance) | Apache-2.0 for code; model/data terms must be checked for the exact selected release | Avatar/lip-sync backend | Separate optional local runtime, not in MSI. |
| Duix or other user-selected avatar backend | Provider-specific | Optional avatar backend | Bring-your-own external integration; never required by the public PaRuX distribution. |

For GPL or otherwise differently licensed external engines, PaRuX's lifecycle UI must preserve the upstream identity/license/provenance rather than presenting them as PaRuX-authored components.

## Model weights and datasets

LLM GGUF files, image/video checkpoints, embedding/reranker models, 3D model assets, training base models and user-selected datasets are **not committed to the PaRuX repository and are not shipped inside the MSI/APK** unless a future release explicitly says otherwise.

Every model/dataset can carry terms independent of PaRuX's license. Model Hub/Dataset Hub should expose source/license/provenance where the source provides it. A download being technically compatible with PaRuX does not mean its license permits every downstream use.

## Build/release tooling — not redistributed as PaRuX runtime code

| Tool | Terms | Use |
|---|---|---|
| WiX Toolset v7 | Open Source Maintenance Fee (OSMF) EULA (WiX v7 is not under the older MS-RL used by WiX v3/v4; check the exact terms of the version you install) | Build-time only: authors the Windows MSI. The repository never auto-accepts its EULA; a local builder must explicitly accept before `scripts/build-msi.ps1` runs. WiX tooling is not shipped as part of the installed PaRuX application. |
| Windows SDK `signtool.exe` | Microsoft Windows SDK terms | Optional Authenticode signing of a release MSI; not bundled. |
| `PaRuX.ReleaseTool` | PaRuX FSL-1.1-MIT source | Locally creates the PaRuX-owned detached signed MSI manifest; private release keys are never shipped. |

## Transitive packages and native libraries redistributed in the Windows builds

These ship inside the self-contained Desktop/Server publish as dependencies of the packages above. Versions are the resolved versions in the projects' `obj/project.assets.json` at the time of writing; license facts were read from each package's `.nuspec`, bundled license file or upstream repository (not guessed). `tools/check-direct-third-party-coverage.ps1` now fails when a package resolved in `project.assets.json` or a bundled native DLL is missing from this file.

| Component | License | Used by | Purpose |
|---|---|---|---|
| Mono.Nat 3.0.0 | MIT (Copyright (C) 2006 Alan McGovern, (C) 2007 Ben Motmans, (C) 2013 Nicholas Terry) | Server (via MonoTorrent) | UPnP / NAT-PMP port mapping for the BitTorrent backend (github.com/mono/Mono.Nat) |
| ReusableTasks 4.0.0 | MIT (Copyright (C) 2019 Alan McGovern) | Server (via MonoTorrent) | Allocation-free async primitives (github.com/alanmcgovern/ReusableTasks) |
| K4os.Compression.LZ4 1.3.8 | MIT (Copyright (c) 2017 Milosz Krajewski) | Server (via Parquet.Net) | LZ4 codec. The NuGet package declares only a license URL; the MIT text was read from github.com/MiloszKrajewski/K4os.Compression.LZ4 (`LICENSE`). |
| Snappier 1.3.1 | BSD-3-Clause (Copyright 2011-2020, Google, Inc. and Snappier Authors) | Server (via Parquet.Net) | Snappy codec (github.com/brantburnett/Snappier) |
| ZstdSharp.Port 0.8.8 | MIT (Copyright Oleg Stepanischev) | Server (via Parquet.Net) | Zstandard codec (github.com/oleg-st/ZstdSharp) |
| Google.Protobuf 3.30.2 | BSD-3-Clause (Copyright 2015, Google Inc.) | Server (via Microsoft.ML.Tokenizers) | Protocol Buffers runtime |
| SQLitePCLRaw.core, SQLitePCLRaw.provider.e_sqlite3, SQLitePCLRaw.lib.e_sqlite3 and SQLitePCLRaw.bundle_e_sqlite3 (all 2.1.13) | Apache-2.0 (Copyright 2014-2024 SourceGear, LLC) | Desktop/Server (via Microsoft.Data.Sqlite) | Raw SQLite bindings and the native SQLite build (github.com/ericsink/SQLitePCL.raw). Apache-2.0 requires retaining its notice; the license text is at https://www.apache.org/licenses/LICENSE-2.0. |
| `e_sqlite3.dll` (native SQLite 3.53.3) | Public domain (SQLite, sqlite.org/copyright.html) | Desktop/Server | Native database engine inside `SQLitePCLRaw.lib.e_sqlite3`. The version string was read from the shipped DLL. |
| CommunityToolkit.HighPerformance 8.4.2 | MIT (Copyright (c) .NET Foundation and Contributors) | Server (via Parquet.Net) | High-performance memory helpers (github.com/CommunityToolkit/dotnet); the package ships its own `License.md` and `ThirdPartyNotices.txt`. |
| Microsoft.IO.RecyclableMemoryStream 3.0.1 | MIT (Copyright (c) Microsoft Corporation) | Server (via Parquet.Net) | Pooled memory streams (github.com/microsoft/Microsoft.IO.RecyclableMemoryStream) |
| Microsoft.Extensions.AI.Abstractions 10.8.3 (Server), 9.8.0 (Windows ML worker) | MIT (Copyright (c) Microsoft Corporation) | Server (via ModelContextProtocol.Core) / `PaRuX.WinMlWorker` (via Microsoft.ML.OnnxRuntimeGenAI) | Shared AI abstractions |
| System.Numerics.Tensors 9.0.0 | MIT (Copyright (c) .NET Foundation and Contributors) | Desktop/Server/WinMlWorker (via ONNX Runtime and Windows ML packages) | Tensor primitives; the package ships `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`. |
| System.IO.Packaging 10.0.2 | MIT (Copyright (c) .NET Foundation and Contributors) | Desktop/Server (via DocumentFormat.OpenXml) | Open Packaging Conventions support; the package ships `THIRD-PARTY-NOTICES.TXT`. |
| System.CodeDom 10.0.15 | MIT (Copyright (c) .NET Foundation and Contributors) | Desktop (via System.Management) | CodeDOM support library |
| DocumentFormat.OpenXml.Framework 3.5.1 | MIT (Copyright (c) Microsoft Corporation) | Desktop/Server (via DocumentFormat.OpenXml) | Shared framework assembly of the Open XML SDK |
| Microsoft.Data.Sqlite.Core 10.0.15 | MIT (Copyright (c) Microsoft Corporation) | Desktop/Server (via Microsoft.Data.Sqlite) | ADO.NET provider core |
| Microsoft.CodeAnalysis.Common 5.9.0 | MIT (Copyright (c) Microsoft Corporation) | Desktop/Server (via Microsoft.CodeAnalysis.CSharp) | Roslyn shared compiler assemblies |
| Microsoft.Win32.SystemEvents 10.0.15 | MIT (Copyright (c) .NET Foundation and Contributors) | Desktop/Server (via System.Drawing.Common) | Windows system-event bindings |
| H.GeneratedIcons.System.Drawing 2.1.0 | MIT (declared in the package nuspec; no copyright line) | Desktop (via H.NotifyIcon.WinUI) | Tray icon helper |
| Microsoft.ML.OnnxRuntime.Managed 1.29.0 | MIT (Copyright (c) Microsoft Corporation; `LICENSE.txt` in the package) | Server (via Microsoft.ML.OnnxRuntime) | Managed ONNX Runtime API |
| Microsoft.ML.OnnxRuntimeGenAI.Managed 0.15.2 | MIT (Copyright (c) Microsoft Corporation; `LICENSE` in the package) | `PaRuX.WinMlWorker` (via Microsoft.ML.OnnxRuntimeGenAI.WinML) | Managed ONNX Runtime GenAI API |
| Microsoft.Web.WebView2 1.0.3179.45 | Microsoft WebView2 SDK license (BSD-style: Copyright (C) Microsoft Corporation; redistribution of binary form must reproduce the copyright notice; `LICENSE.txt` in the package) | Desktop (via Microsoft.WindowsAppSDK) | WebView2 loader and interop for in-app web content |
| Microsoft.WindowsAppSDK.AI, Microsoft.WindowsAppSDK.Foundation, Microsoft.WindowsAppSDK.InteractiveExperiences, Microsoft.WindowsAppSDK.ML, Microsoft.WindowsAppSDK.Widgets, Microsoft.WindowsAppSDK.WinUI (and the asset-less Microsoft.WindowsAppSDK.Base, Microsoft.WindowsAppSDK.DWrite, Microsoft.WindowsAppSDK.Runtime) 1.8.x | Microsoft Software License Terms - Windows App SDK (same `license.txt` as the Microsoft.WindowsAppSDK row above) | Desktop (via Microsoft.WindowsAppSDK) | Component packages of the Windows App SDK; versions are pinned by `PaRuX.App.csproj` through the metapackage |
| `DirectML.dll` (DirectML Redistributable Library 1.15.4) | Microsoft Software License Terms - Windows ML Runtime (proprietary; same `license.txt` as the Microsoft.Windows.AI.MachineLearning row above) | Desktop/Server app folder | Copied unmodified and Microsoft-signed from the `runtimes/win-x64/native` folder of Microsoft.Windows.AI.MachineLearning 2.3.42. Redistribution is limited to that license's "Distributable Code" section; this row records the terms and is not a legal clearance. |
| LLVM OpenMP runtime (`libomp.dll`) | Apache-2.0 WITH LLVM-exception (The LLVM Project) | Server/Portable (shipped inside the llama.cpp release archives) | OpenMP runtime used by the llama.cpp CPU backend. The upstream license text `LICENSE-LLVM-OpenMP` ships beside the DLL in every llama.cpp runtime folder. |
| `msquic.dll` (MsQuic 2.4.18) | MIT (Copyright (c) Microsoft Corporation) | Desktop/Server/CLI (shipped by the self-contained .NET 10 runtime) | QUIC transport used by System.Net.Quic. Notice reproduced from the .NET runtime pack `THIRD-PARTY-NOTICES.TXT` (github.com/microsoft/msquic). |

### License texts for the components above

**MIT License** (applies to every component marked MIT in this section and, with the copyright holder named in its row, to llama.cpp, whose verbatim upstream text is in `LICENSE-llama.cpp.txt`):

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

**BSD 3-Clause License** (Snappier, Google.Protobuf; copyright lines as in their rows):

> Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
>
> 1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
> 2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
> 3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Offline/local-AI boundary

PaRuX's **inference and execution design is local-first**, but first-time setup can require Internet access to obtain runtime binaries, model weights, datasets or optional tools the user explicitly selects. After required assets are present, normal local inference/workflows should not require a proprietary cloud AI provider. GitHub/Hugging Face are distribution/discovery sources, not inference backends.

Features whose purpose is explicitly online (for example checking GitHub Releases, searching Hugging Face, downloading an optional model/runtime or connecting to a user-configured remote MCP endpoint) naturally require that network resource. Those paths must fail visibly rather than silently substituting cloud inference.

## Release audit note

Because self-contained .NET and Android builds include transitive dependencies, a public release should still run the package-manager license/vulnerability inventory during local release validation and preserve any upstream NOTICE files that require redistribution. This document is the maintained human-readable summary, not a substitute for the exact upstream license texts.


## Optional virtual-controller components (also offered as an optional MSI feature)

The PaRuX **application** never installs drivers and only talks to them when present. The **setup (MSI)** can install them, only when its optional "Controller drivers (ViGEmBus + HidHide)" feature is ticked or requested on the command line; see "Optional vendor payloads in the Windows MSI" above for versions, hashes and licences. A copy already on the PC is never touched.

| Component | License | How PaRuX uses it |
|---|---|---|
| ViGEmBus (driver) and ViGEmClient (client library) | BSD-3-Clause (driver contract header) / MIT (client) | `ViGEmBusClient.cs` is an original C# implementation of the published IOCTL contract; request layouts and control codes were adapted from the upstream `BusShared.h`, `Common.h` and `ViGEmClient.cpp`. Copyright (c) Nefarius Software Solutions e.U. / Benjamin Hoglinger-Stelzer. Both projects are archived upstream. SOURCE IMPLEMENTED, NOT RUN. |
| HidHide | MIT | Only its command-line tool is executed (`HidHideCLI.exe`, installed by the user); no code copied. NOT RUN against a real installation. |

## PdfPig (document text extraction)

| Component | License | How PaRuX uses it |
|---|---|---|
| PdfPig 0.1.16 (NuGet id `PdfPig`, github.com/UglyToad/PdfPig) | Apache-2.0 | Read-only PDF text extraction with page numbers in `PaRuX.Server/Knowledge/StructuredDocumentExtractor.cs`; no code copied. The look-alike NuGet id `UglyToad.PdfPig` is NOT used. |

## Playnite (emulator definitions) and LiteDB (library import)

| Component | License | How PaRuX uses it |
|---|---|---|
| Playnite emulator definitions (`Emulation/Emulators/*/emulator.yaml`, `Platforms.yaml`; github.com/JosefNemec/Playnite) | MIT (Copyright (c) Josef Nemec) | `tools/generate-playnite-emulator-registry.py` converts the launch arguments, executable names, file extensions and platform names of about 90 emulators into `EmulatorRegistry.Playnite.cs`. Entries are marked `unverified`; hand-tuned PaRuX definitions take precedence. No Playnite code is copied. |
| LiteDB 5.0.21 (NuGet id `LiteDB`, github.com/mbdavid/LiteDB) | MIT | Reads a private temporary COPY of a Playnite library as untyped BSON documents in the Playnite importer. Playnite's own files are never opened for writing. |
| Playnite (the application) | MIT | Optional launcher front-end and migration source; PaRuX does not bundle or redistribute it. |

## Aniki ReMake (design reference)

| Component | License | How PaRuX uses it |
|---|---|---|
| Aniki ReMake (Playnite fullscreen theme by Mike Aniki) | MIT (Copyright (c) 2025 Mike Aniki; as stated by the project owner, not re-read from the upstream repository in this audit) | Design inspiration for the Gaming Home layout only (hero, shelves, focus states). No theme code, XAML, fonts, images or other assets are copied or redistributed; this entry records attribution should any future derivative be added. |
