# Release readiness: local distribution path, 2026-11-22

Scope: prove that the packaging scripts produce correct, verifiable artifacts from current `main`
(commit `8880b0dd`, product version 4.4.0 in `Directory.Build.props`). Everything was built locally into
`artifacts/` of an agent worktree. Nothing was published, tagged, uploaded or submitted. No owner install or
owner data was touched. No desktop UI was driven and no GPU was used.

Note on version: the task text said target 4.1.0, but the repository, the existing local artifacts and the
last published pre-release are already beyond that. The scripts build the version in `Directory.Build.props`
(4.4.0). No version was changed.

Signing: the owner's self-signed local Authenticode certificate was used through the scripts' own
`PARUX_SIGN_PFX` path (password read from the owner's local file into the process environment only; a scan of
all logs in `artifacts/readiness/` found no password). The signature status is `Valid` on this PC only
because that self-signed certificate is trusted in this user's store; on a clean PC it will show as
untrusted / Unknown publisher (documented limit).

## Result table

| # | Criterion | Result | Evidence (under `artifacts/readiness/` unless noted) |
|---|---|---|---|
| 1 | Release build of `PaRuX.sln` (Desktop, Server, Contracts, Intelligence, tests, tools) | PASS | `build-sln-release.log`: 0 errors, 515 warnings (CA1416 platform analyzer, CS86xx nullable, CS0649, CS8848, CS0252, MCP9007 obsolete MCP OAuth delegate). Fresh worktree, so effectively a clean build, 82 s. |
| 2 | Server Release publish (self-contained win-x64) | PASS | Done inside the portable and MSI builds (`portable-build.log`, `msi-build.log`). |
| 3 | Portable ZIP build + built-in verification | PASS | `artifacts/portable/PaRuX-4.4.0-portable-win-x64.zip`, 2702.8 MB, 1277 entries, SHA-256 `cd8ea235...bd4d` (matches the `.sha256.txt` and an independent `Get-FileHash`). |
| 4 | Portable content vs `MANIFEST-portable.json` | PASS | 1276 manifest files: 0 missing, 0 size mismatches; random 120-file SHA-256 sample: 0 mismatches. Required notices present (LICENSE.txt, NVIDIA_CUDA_NOTICE.txt, THIRD_PARTY_NOTICES.md, OPEN_SOURCE_PROVENANCE.md, README-portable.txt). No tokens, keys, DBs or user data (`portable-inspect.txt`; name matches were all library/tokenizer false positives). Starter Qwen3-4B Q4_K_M, ONNX embedding + reranker, CPU b12283 and Vulkan b10549 llama.cpp included. |
| 5 | Portable Authenticode | PASS (local trust only) | 8 first-party PEs signed by the owner cert with DigiCert timestamp (`PaRuX.exe`, `PaRuX.dll`, Contracts, Intelligence, Server exe/dll). Of 863 PEs, 146 are unsigned, all third-party: llama.cpp binaries (103), SQLite/PdfPig/MonoTorrent/LiteDB/Parquet/ModelContextProtocol/ZstdSharp etc. managed libs and native `e_sqlite3`. The ZIP container itself is not signed (by design). |
| 6 | MSI build | PASS | `artifacts/installer/PaRuX-4.4.0-win-x64.msi`, 838,101,973 bytes, SHA-256 `842c433c...cac00` (matches `.sha256.txt`). Built with `build-msi.ps1` (not `-Release`; WiX 7 EULA already accepted by the owner for this project). |
| 7 | MSI Authenticode | PASS (local trust only) | `signtool verify` "Successfully verified"; `Get-AuthenticodeSignature` = Valid. Desktop, Server, `Cli\parux.exe`, `UpdateHelper\PaRuX.UpdateHelper.exe` all Valid. |
| 8 | MSI table review | PASS | `msi-props.txt`: per-machine (ALLUSERS=1), ProductVersion 4.4.0, UpgradeCode `F69DACAC-...` identical to the previous 4.4.0 MSI in the main checkout, new ProductCode, MajorUpgrade via `RemoveExistingProducts` (seq 1401), downgrade blocked ("A newer version of PaRuX is already installed"), requires 64-bit Windows 10+, PATH entry for `Cli`, Start Menu shortcut, 1648 files. |
| 9 | MSI payload, admin-free extraction | PASS | `msiexec /a` into `msi-admin-extract/` (no system change, exit 0, 17 s): 1649 files; correct layout; no `parux.portable`, no `PaRuXData`, no models; Vulkan b10549 + CUDA 13.4 baseline runtimes; all four first-party exes signed. |
| 10 | MSI install / upgrade / rollback / uninstall | NOT RUN | This PC is Windows 11 Home (`WindowsEditionId = Core`): no Windows Sandbox, no Hyper-V VM available. `scripts/test-msi.ps1` is per-machine and needs an elevated shell, so running it here would modify the owner's Program Files and registry. Needs a Pro/Enterprise host, VM or clean machine. |
| 11 | Portable relocation smoke (`-Smoke`) and first run | NOT RUN | `-Smoke` and starting `PaRuX.exe`/Core load ONNX and the Vulkan loader (GPU) and open a window; both were excluded by the task. Static inventory above is the only evidence. |
| 12 | MSIX build (Store edition) | PASS (unsigned by design) | `msix-out/PaRuX_4.4.0.0_x64.msix`, 206.9 MB, SHA-256 `97786a99...1549`. 1218 entries; manifest + blockmap present; `runFullTrust`, internet/LAN capabilities; no UpdateHelper, no CLI, no portable/corporate marker, no models or PaRuXData. Identity read from the Git-ignored local file; not reproduced here. Partner Center re-signs; `-SignTestCopy` not used because it writes a throwaway certificate into the owner's cert store. |
| 13 | MSIX install / Store submission / WACK | NOT RUN | Needs a trusted test certificate (system security change) and the owner's Partner Center upload. |
| 14 | Corporate Lite build | PASS | `artifacts/corporate-lite/PaRuX-Corporate-Lite-Offline-win-x64.zip`, 2668.5 MB, 1223 entries, SHA-256 `c86860be...c582` (matches `.sha256.txt`); builder's own check: complete per-file SHA-256 inventory, model + reranker included. Contains `parux.corporate`, "Start PaRuX Corporate Lite.cmd" and "Start PaRuX Corporate Model Downloads.cmd", Qwen3-4B Q4_K_M, ONNX embedding/reranker. Built with `-SkipPublish` on the CPU-shell stage (8 first-party PEs signed in that stage, 167.2 MB internal CPU-shell zip also produced). I did not extract it and re-verify signatures. |
| 15 | Corporate behaviour on a locked-down machine (no admin, offline, IT policy) | NOT RUN | Needs a real managed endpoint. The bundled model choice (Qwen3-4B, Apache-2.0) was confirmed by the owner per the project memory. |
| 16 | Android: JVM unit tests, lint, debug APK, unsigned release APK (`gradlew clean test lint assembleDebug assembleRelease`, same as the Android step of `tools/release-gate.ps1`) | PASS | `android-gate.log`: BUILD SUCCESSFUL, 106 unit tests, 0 failures, 0 skipped, lint 0 errors / 29 warnings, `app-release-unsigned.apk` 4,239,934 bytes SHA-256 `fca054bd...e1e5`, versionName 4.4.0 / versionCode 26. |
| 17 | Android signed APK (`publish-android-apk.ps1 -LocalOnly`) | BLOCKED (owner) | Keystore file exists in `Documents\LocalAI`, but no keystore password file exists on this PC and the script requires a clean tree plus the owner's passwords. Not guessed. |
| 18 | Android on emulator/device | NOT RUN | No device; emulator not started (GPU/desktop excluded). |
| 19 | Release-asset verifier self-test (`tools/test-release-asset-verifier.ps1`) | PASS | `release-asset-verifier-selftest.log`: 28 synthetic scenarios pass. Signature boundaries are stubbed there. `scripts/verify-release-assets.ps1` itself was NOT RUN: it needs a real release folder, the release private key, tag and the Android APK. |
| 20 | Signed update envelope (`PARUX_RELEASE_PRIVATE_KEY`, `build-msi.ps1 -Release`) | NOT RUN | A `-Release` build needs the release private key and writes the update manifest. Out of scope for a no-publish run. |
| 21 | SBOM | PASS | `sbom-windows.json` (CycloneDX): 66 components, licences resolved 83, unknown 0 by the script (but see gap list: K4os.Compression.LZ4 and Microsoft.Windows.SDK.BuildTools show no licence id). |
| 22 | License inventory + direct dependency coverage | PASS (not legal clearance) | `tools/validate-license-inventory.ps1`: 4 lock artifacts, 27 notice rows consistent. `tools/check-direct-third-party-coverage.ps1`: all 25 direct PackageReference entries mentioned. |
| 23 | Playnite / Aniki-derived assets copied? | PASS with note | No file named or containing Playnite/Aniki code, themes, fonts or images is in the portable or MSI payload, and `src/PaRuX.App/Assets` holds only the PaRuX PRX Orbit icons. "Aniki" appears only in `docs/GAMING_FRONTEND_UX_REVIEW_2026-11-22.md` as design inspiration (no assets). The one real derivative is the emulator registry generated from Playnite's `emulator.yaml` / `Platforms.yaml` (MIT, Josef Nemec): attributed in `THIRD_PARTY_NOTICES.md` (section "Playnite (emulator definitions) and LiteDB"). LiteDB reads a private copy of a Playnite library; Playnite itself is not bundled. A byte-level provenance diff against Playnite's repository was NOT RUN (no network comparison). |
| 24 | Bundled runtimes licences | PASS with gaps | llama.cpp (MIT), Qwen3-4B (Apache-2.0), MiniLM / ms-marco ONNX (Apache-2.0), VC++ 14.44 runtime, .NET 10.0.10 self-contained, Windows App SDK 1.8 are all named in the notices. CUDA 13.4 runtime ships only in the MSI baseline, under the NVIDIA CUDA EULA with `NVIDIA_CUDA_NOTICE.txt`. See gaps below. |
| 25 | `tools/check-public-safety.ps1` | PASS | "public-safety: OK (Working)", run before and after the report was written. |
| 26 | Clean-machine trust of the self-signed certificate / SmartScreen | NOT RUN (expected to warn) | Needs a clean PC; the owner must decide between trusting the `.cer` or buying a CA certificate (budget 0 means self-signed stays a documented limit). |

## Attribution and notice gaps to fix before a public release

`THIRD_PARTY_NOTICES.md` and `OPEN_SOURCE_PROVENANCE.md` contain no entry for these shipped components
(checked against the SBOM and the file inventory of the portable ZIP):

- Mono.Nat 3.0.0 and ReusableTasks 4.0.0 (MonoTorrent family, upstream MIT notices should accompany them).
- K4os.Compression.LZ4 1.3.8 (SBOM carries no licence id; upstream is MIT, needs verification), Snappier 1.3.1 (BSD-3-Clause), ZstdSharp.Port 0.8.8 (MIT).
- SQLitePCLRaw core/provider/bundle/lib.e_sqlite3 2.1.13 (Apache-2.0) and the native `e_sqlite3.dll`
  (SQLite is public domain): only `Microsoft.Data.Sqlite` is listed.
- CommunityToolkit.HighPerformance 8.4.2, Microsoft.IO.RecyclableMemoryStream 3.0.1, Microsoft.Extensions.AI.Abstractions,
  System.Numerics.Tensors, System.IO.Packaging, System.CodeDom (MIT, Microsoft/.NET Foundation).
- `DirectML.dll` (Microsoft redistributable) in the app folder.
- Google.Protobuf is listed only generically; `msquic.dll` (shipped by the .NET runtime) is not named.
- No licence text file is shipped for llama.cpp inside `runtime-baseline/*` (only `LICENSE-LLVM-OpenMP`); the MIT
  copyright/permission notice should be included next to the binaries or in the notices file.
- The MSIX package carries `NVIDIA_CUDA_NOTICE.txt` although it contains no CUDA runtime (harmless, but inaccurate).

Existing gate `check-direct-third-party-coverage.ps1` passes because it only checks direct references, so these
transitive/native items are not caught by any current gate.

## Honest gaps and what needs the owner

1. MSI install/upgrade/rollback/uninstall has never been exercised on this Windows Home PC. Needs Windows Sandbox
   (Pro/Enterprise), a Hyper-V VM, or a clean machine, and `scripts/test-msi.ps1` from an elevated shell.
2. Portable first-run and Corporate first-run on a clean machine were not executed (no UI, no GPU in this run).
3. Authenticode uses the owner's self-signed certificate: the signatures verify here but will warn elsewhere.
   A CA-issued certificate is an owner purchase/decision (budget 0).
4. Signed Android APK: the keystore password is not available to automation. Owner step.
5. Signed update manifest and `build-msi.ps1 -Release`: needs the release private key and password. Owner step.
6. Microsoft Store: MSIX is built and structurally valid but untested by WACK or sideload; upload to Partner Center is the owner's step.
7. The 4.1.0 target in the task text does not match the repository (4.4.0). The owner should confirm which version
   the release is meant to carry before any tag. No tag, release or Store action was taken.
8. Warnings: 515 compiler warnings, mostly CA1416 and nullable. Not release blockers, but not zero.

## Re-run

Build outputs are in the worktree `artifacts/` (Git-ignored): portable, installer, corporate-lite, readiness.
Commands used: `dotnet build PaRuX.sln -c Release`; `scripts/build-portable.ps1`; `scripts/build-msi.ps1`;
`scripts/build-corporate-lite.ps1 -ModelPath ... -EmbeddingDir ... -RerankerDir ... -RuntimeDir ... -RuntimeTag b12283 -RequireReranker -SkipPublish`;
`scripts/build-msix.ps1 -IdentityFile <local> -RuntimeCacheDir ... -OutputDir ...`; `gradlew clean test lint assembleDebug assembleRelease`;
`tools/test-release-asset-verifier.ps1`; `scripts/generate-sbom.ps1 -Scope windows`; `tools/validate-license-inventory.ps1`;
`tools/check-direct-third-party-coverage.ps1`; `tools/check-public-safety.ps1`.
