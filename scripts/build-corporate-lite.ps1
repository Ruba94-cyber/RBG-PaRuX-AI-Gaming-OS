<#
.SYNOPSIS
    Builds PaRuX Corporate Lite Offline Portable (same product, restricted profile) and verifies it.

.DESCRIPTION
    Output: artifacts\corporate-lite\PaRuX-Corporate-Lite-Offline-win-x64.zip (+ SHA-256 and manifest).
    Contents: the app and Core (from build-portable.ps1's stage or a fresh publish), a CPU llama.cpp runtime, the hash-pinned
    Vulkan llama.cpp runtime (same lock entry 'llama.cpp-vulkan' as the Standard builder; optional GPU offload with automatic
    CPU fallback, switched off with PARUX_CORPORATE_GPU=0), ONE small
    quantised model with a redistribution-friendly licence, the light embedding model, offline configuration
    (`parux.corporate` marker in app\, launchers that set PARUX_CORPORATE=1 and keep all data in .\PaRuXData), licences and a manifest.
    Layout (the ZIP root folder holds ONLY PaRuX.exe plus these folders; the build verifies it and fails otherwise):
        PaRuX.exe          small launcher: starts app\PaRuX.exe, waits, returns its exit code
        app\               the application (PaRuX.exe, PaRuX.Server\, runtime, languages, marker parux.corporate)
        PaRuXData\         all data, beside the launcher (never %LOCALAPPDATA%)
        docs\              README-corporate-lite.txt, MANIFEST.json, licences, notices
        launchers\         Start PaRuX Corporate Lite.cmd, "(CPU only)", "Model Downloads"
    Never included: documents, indexes, history, tokens, anything from the developer PC. Weights are NOT stored in Git: they are
    supplied with -ModelPath / -EmbeddingDir / -RerankerDir / -RuntimeDir (or fetched with -Fetch from the official sources, hashes recorded).

    The package does not bypass SmartScreen/Defender/IT policy, requires no admin rights from the app and starts no cloud inference.
    Normal launch is offline. A separate IT-approved launcher opts into GGUF-only downloads. Runtime installers, media, Gaming
    and automatic updates stay disabled.

.EXAMPLE
    ./scripts/build-corporate-lite.ps1 -ModelPath D:\assets\model.gguf -EmbeddingDir D:\assets\embeddings -RuntimeDir D:\assets\llama-cpu
    ./scripts/build-corporate-lite.ps1 -Fetch -RequireReranker -Smoke
    # Small official Qwen3 0.6B Q8_0 pinned GGUF, intended as a low-memory
    # basic-chat alternative (not equivalent in reasoning quality to the 4B model).
    ./scripts/build-corporate-lite.ps1 -Fetch -LowMemoryStarter -RequireReranker -Smoke
    ./scripts/build-corporate-lite.ps1 -Fetch -NoModel -RequireReranker
    # For a smaller, licence-reviewed GGUF on 8–16 GB machines, supply local assets.
    ./scripts/build-corporate-lite.ps1 -ModelPath D:\assets\light.gguf -ModelOrigin 'publisher/revision' -ModelLicenseId 'Apache-2.0' -EmbeddingDir D:\assets\embeddings -RuntimeDir D:\assets\llama-cpu
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$ModelPath = "",
    [string]$ModelOrigin = "",
    [string]$ModelLicenseId = "",
    [switch]$NoModel,
    [switch]$LowMemoryStarter,
    [string]$EmbeddingDir = "",
    [string]$MultilingualEmbeddingDir = "",
    [string]$MultilingualRerankerDir = "",
    [string]$RerankerDir = "",
    [switch]$RequireReranker,
    [string]$RuntimeDir = "",
    [string]$RuntimeTag = "",
    # Directory holding (or receiving) the locked Vulkan runtime archive. Default: artifacts\cache\runtime, the same cache
    # the Standard builder uses. The archive is verified against packaging\windows-baseline-runtime.lock.json (size + SHA-256).
    [string]$VulkanCacheDir = "",
    [switch]$Fetch,
    [switch]$AllowUnpinnedLlamaCpuRuntime,
    [switch]$SkipPublish,
    [switch]$Smoke,
    [int]$SmokeTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "portable-layout.ps1")
# Windows PowerShell 5.1 renders a progress bar for every Invoke-WebRequest chunk, which cuts multi-GB model downloads to ~1 MB/s.
$ProgressPreference = "SilentlyContinue"
Set-StrictMode -Version Latest
# Reject contradictory package options BEFORE publishing a shell, creating staging
# directories or fetching third-party assets.
if ($NoModel -and -not [string]::IsNullOrWhiteSpace($ModelPath)) {
    throw "-NoModel and -ModelPath are mutually exclusive."
}
if ($LowMemoryStarter -and (-not $Fetch -or $NoModel)) {
    throw "-LowMemoryStarter requires -Fetch and cannot be combined with -NoModel or a custom local GGUF."
}
if ($Fetch -and -not [string]::IsNullOrWhiteSpace($ModelPath)) {
    throw "-Fetch selects the official pinned model. Use -ModelPath without -Fetch for a custom GGUF."
}
if ($Fetch -and (-not [string]::IsNullOrWhiteSpace($ModelOrigin) -or
    -not [string]::IsNullOrWhiteSpace($ModelLicenseId))) {
    throw "Pinned -Fetch has its own model origin and licence. Do not attach custom metadata."
}
$repoRoot = Split-Path -Parent $PSScriptRoot
# Keep experimental low-memory builds separate from the existing standard Corporate
# stage, archive, manifest and downloaded assets. Never overwrite a verified 4B package.
$outRoot = Join-Path $repoRoot $(if ($LowMemoryStarter) { "artifacts\corporate-lite-low-memory" } else { "artifacts\corporate-lite" })
$work = Join-Path $outRoot "stage"
$stage = Join-Path $work "PaRuX-Corporate-Lite"
$assets = Join-Path $outRoot "assets"
New-Item -ItemType Directory -Force $outRoot, $assets | Out-Null

# --- 1. The application (reuse the portable build; it already excludes models, indexes and tokens) --------------------------
# Reuse only the dedicated, CPU-only build shell. This no longer overwrites
# the Standard Portable stage/ZIP when both editions are prepared for release.
$portableStage = Join-Path $repoRoot "artifacts\portable\cpu-shell-stage\PaRuX"
if (-not $SkipPublish) {
    $pv = @{}; if ($Version) { $pv["Version"] = $Version }
    # Corporate Lite has its own CPU engine and must not inherit the Standard portable Vulkan pack.
    & (Join-Path $PSScriptRoot "build-portable.ps1") @pv -WithoutVulkan
    if ($LASTEXITCODE -ne 0) { throw "Portable build failed." }
}
if (-not (Test-Path (Join-Path $portableStage "PaRuX.exe"))) { throw "Portable stage not found. Run without -SkipPublish." }
$sourceCommit = ([string](& git -C $repoRoot rev-parse HEAD)).Trim().ToLowerInvariant()
$sourceMarker = Join-Path $portableStage "app\.parux-publish-source"
if ($sourceCommit -notmatch '^[a-f0-9]{40}$' -or
    -not (Test-Path -LiteralPath $sourceMarker -PathType Leaf) -or
    (Get-Content -LiteralPath $sourceMarker -Raw).Trim() -cne ($sourceCommit + "|cpu-shell")) {
    throw "Corporate Lite CPU shell was compiled from a different commit or edition. Rebuild without -SkipPublish."
}
if (Test-Path (Join-Path $portableStage "app\PaRuX.Server\runtime-baseline")) {
    throw "Corporate Lite requires a CPU-only portable stage. Rebuild without -SkipPublish using -WithoutVulkan."
}
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item -Path (Join-Path $portableStage "*") -Destination $stage -Recurse -Force
# Not part of this edition: CLI and the portable launchers/readme/manifest are replaced below.
Remove-Item (Join-Path $stage "app\Cli") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $stage "launchers") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $stage "docs\README-portable.txt"), (Join-Path $stage "docs\MANIFEST-portable.json") -Force -ErrorAction SilentlyContinue
$appStage = Join-Path $stage "app"
$docsStage = Join-Path $stage "docs"
$launchersStage = Join-Path $stage "launchers"
New-Item -ItemType Directory -Force $docsStage, $launchersStage | Out-Null
if (-not $Version) {
    [xml]$p = Get-Content (Join-Path $repoRoot "Directory.Build.props") -Raw
    $Version = [string]($p.Project.PropertyGroup | ForEach-Object { $_.PaRuXProductVersion } | Where-Object { $_ } | Select-Object -First 1)
}

# --- 2. Optional fetch of official assets (hashes are recorded in the manifest) -----------------------------------------------
function Get-File($url, $dest) {
    if (Test-Path -LiteralPath $dest -PathType Leaf) {
        if ((Get-Item -LiteralPath $dest).Length -le 0) {
            throw "Cached download is empty: $dest. Remove it and rebuild."
        }
        return
    }
    $partial = $dest + "." + [Guid]::NewGuid().ToString("N") + ".partial"
    try {
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing -Headers @{ "User-Agent" = "PaRuX-build" }
        if (-not (Test-Path -LiteralPath $partial -PathType Leaf) -or (Get-Item -LiteralPath $partial).Length -eq 0) {
            throw "Incomplete download: $url"
        }
        Move-Item -LiteralPath $partial -Destination $dest -ErrorAction Stop
    } finally {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    }
}

# The Core already pins these ONNX models to immutable revisions and checks their
# SHA-256 / Git blob digests. Corporate packaging must honor the SAME bytes so a
# fresh offline user never discovers that the bundled RAG engines are incompatible.
# For Git-backed small files the digest is SHA1("blob " + length + NUL + bytes).
function Assert-PinnedAsset {
    param([string]$File, [long]$Size, [string]$DigestKind, [string]$Digest)
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { throw "Required pinned asset missing: $File" }
    $item = Get-Item -LiteralPath $File
    if ($Size -gt 0 -and $item.Length -ne $Size) {
        throw "Pinned asset size mismatch: $File (expected $Size, found $($item.Length))"
    }
    if ($DigestKind -eq "sha256") {
        $actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    } elseif ($DigestKind -eq "git-sha1") {
        if ($Size -gt 1048576) { throw "Refusing to load a large Git blob into memory: $File" }
        $raw = [IO.File]::ReadAllBytes($File)
        $prefix = [Text.Encoding]::ASCII.GetBytes("blob $($raw.Length)" + [char]0)
        $blob = New-Object byte[] ($prefix.Length + $raw.Length)
        [Buffer]::BlockCopy($prefix, 0, $blob, 0, $prefix.Length)
        [Buffer]::BlockCopy($raw, 0, $blob, $prefix.Length, $raw.Length)
        $sha1 = [Security.Cryptography.SHA1]::Create()
        try { $actual = [BitConverter]::ToString($sha1.ComputeHash($blob)).Replace('-', '').ToLowerInvariant() }
        finally { $sha1.Dispose() }
    } else { throw "Unsupported digest kind: $DigestKind" }
    if ($actual -ne $Digest) { throw "Pinned asset digest mismatch: $File (expected $Digest, got $actual)" }
}
function Get-PinnedAsset {
    param([string]$Repo, [string]$Revision, [string]$RemotePath,
          [string]$Dest, [long]$Size, [string]$DigestKind, [string]$Digest)
    if (Test-Path -LiteralPath $Dest -PathType Leaf) {
        try { Assert-PinnedAsset $Dest $Size $DigestKind $Digest; return }
        catch { Write-Warning "Cached managed model asset is invalid; downloading verified replacement: $Dest" }
    }
    $url = "https://huggingface.co/$Repo/resolve/$Revision/$RemotePath"
    $partial = $Dest + "." + [Guid]::NewGuid().ToString("N") + ".partial"
    try {
        Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing -Headers @{ "User-Agent" = "PaRuX-build" }
        Assert-PinnedAsset $partial $Size $DigestKind $Digest
        Move-Item -LiteralPath $partial -Destination $Dest -Force -ErrorAction Stop
    } finally {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    }
}
. (Join-Path $PSScriptRoot "llama-cpu-pin.ps1")
if ($Fetch) {
    # llama.cpp (MIT) CPU build from the official releases.
    # An explicit -RuntimeTag selects that exact reviewed release instead of the newest one (which has no reviewed pin yet).
    if ($RuntimeTag) {
        if ($RuntimeTag -notmatch '^[A-Za-z0-9._-]+$') { throw "Untrusted llama.cpp release tag." }
        $rels = @(Invoke-RestMethod ("https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/" + $RuntimeTag) -Headers @{ "User-Agent" = "PaRuX-build" })
    } else {
        $rels = Invoke-RestMethod "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=20" -Headers @{ "User-Agent" = "PaRuX-build" }
    }
    $rel = $rels | Where-Object { @($_.assets | Where-Object { $_.name -match '^llama-.*-bin-win-cpu-x64\.zip$' }).Count -gt 0 } | Select-Object -First 1
    if (-not $rel) { throw "No official win-cpu-x64 llama.cpp asset found in recent releases." }
    $asset = $rel.assets | Where-Object { $_.name -match '^llama-.*-bin-win-cpu-x64\.zip$' } | Select-Object -First 1
    $RuntimeTag = [string]$rel.tag_name
    if ($RuntimeTag -notmatch '^[A-Za-z0-9._-]+$') { throw "Untrusted llama.cpp release tag." }
    $zip = Join-Path $assets $asset.name
    Get-File $asset.browser_download_url $zip
    # GitHub release API SHA-256, when published, is checked before expanding the ZIP.
    # If absent, keep provenance explicit rather than pretending transport TLS is a signed digest.
    $assetDigestField = $asset.PSObject.Properties["digest"]
    $assetDigest = if ($null -ne $assetDigestField) { [string]$assetDigestField.Value } else { "" }
    # The archive must also match the reviewed pin (packaging/llama-cpu-archive.lock.json), not only the digest
    # delivered by the same GitHub API response as the download. Unpinned tags need an explicit switch.
    $pinnedZipSha = Get-PaRuXLlamaCpuPin -Tag $RuntimeTag -ArchiveName $asset.name -AllowUnpinned:$AllowUnpinnedLlamaCpuRuntime
    if ($pinnedZipSha) {
        Assert-PaRuXLlamaCpuArchivePin -File $zip -PinnedSha256 $pinnedZipSha -UpstreamDigest $assetDigest
    } elseif ($assetDigest -match '^sha256:([a-fA-F0-9]{64})$') {
        $runtimeZipSha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($runtimeZipSha -ne $Matches[1].ToLowerInvariant()) {
            throw "CPU llama.cpp release archive digest does not match the upstream release metadata: $zip"
        }
    } else {
        Write-Warning "llama.cpp release metadata has no SHA-256 digest. Archive origin needs manual release review before redistribution."
    }
    # A cached CPU folder must never be relabelled as a newer release.
    $RuntimeDir = Join-Path $assets ("llama-cpu-" + $RuntimeTag)
    if (-not (Test-Path $RuntimeDir)) { Expand-Archive -LiteralPath $zip -DestinationPath $RuntimeDir }
    # Qwen3-4B Q4_K_M, Apache-2.0 (official Qwen repository): clearly better answers than 1.7B for technical questions,
    # still CPU-usable on 16 GB. Q4_K_M is the quality floor PaRuX uses for technical work.
    if (-not $NoModel) {
        # Qwen publishes both models under Apache-2.0. Content is pinned to
        # official per-file SHA-256, never the mutable name/main ref alone.
        if ($LowMemoryStarter) {
            $ModelPath = Join-Path $assets "Qwen3-0.6B-Q8_0.gguf"
            Get-PinnedAsset "Qwen/Qwen3-0.6B-GGUF" "main" "Qwen3-0.6B-Q8_0.gguf" $ModelPath 0 "sha256" "9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031"
        } else {
            $ModelPath = Join-Path $assets "Qwen3-4B-Q4_K_M.gguf"
            Get-PinnedAsset "Qwen/Qwen3-4B-GGUF" "main" "Qwen3-4B-Q4_K_M.gguf" $ModelPath 0 "sha256" "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5"
        }
    }
    # all-MiniLM-L6-v2 (Apache-2.0): the files the Core's EmbeddingService expects.
    $EmbeddingDir = Join-Path $assets "embeddings"
    New-Item -ItemType Directory -Force $EmbeddingDir | Out-Null
    $embRepo = "sentence-transformers/all-MiniLM-L6-v2"
    $embRev = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41"
    Get-PinnedAsset $embRepo $embRev "onnx/model.onnx" (Join-Path $EmbeddingDir "model.onnx") 90405214 "sha256" "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"
    Get-PinnedAsset $embRepo $embRev "vocab.txt" (Join-Path $EmbeddingDir "vocab.txt") 231508 "git-sha1" "fb140275c155a9c7c5a3b3e0e77a9e839594a938"
    Get-PinnedAsset $embRepo $embRev "tokenizer_config.json" (Join-Path $EmbeddingDir "tokenizer_config.json") 350 "git-sha1" "c79f2b6a0cea6f4b564fed1938984bace9d30ff0"
    Get-PinnedAsset $embRepo $embRev "config.json" (Join-Path $EmbeddingDir "config.json") 612 "git-sha1" "72b987fd805cfa2b58c4c8c952b274a11bfd5a00"
    # multilingual-e5-small (MIT, int8 ONNX): the default document-search embedding. MiniLM above is English-centric; this one finds the
    # right passage for an Italian question over English text (measured 57% vs 14% first hit) and for Italian over Italian (90% vs 60%).
    $MultilingualEmbeddingDir = Join-Path $assets "embeddings-e5"
    New-Item -ItemType Directory -Force $MultilingualEmbeddingDir | Out-Null
    Get-PinnedAsset "Xenova/multilingual-e5-small" "761b726dd34fb83930e26aab4e9ac3899aa1fa78" "onnx/model_int8.onnx" (Join-Path $MultilingualEmbeddingDir "model.onnx") 118054593 "sha256" "4d24e2bc01a447951524466ef533e52944bf48509e6552810bcee1a2711cb02c"
    Get-PinnedAsset "Xenova/multilingual-e5-small" "761b726dd34fb83930e26aab4e9ac3899aa1fa78" "tokenizer.json" (Join-Path $MultilingualEmbeddingDir "tokenizer.json") 17082730 "sha256" "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39"
    # Multilingual cross-encoder (mmarco-mMiniLMv2, Apache-2.0, int8): second-stage ranking when question and evidence differ in language.
    # Matches PinnedModelManifest.MultilingualRerankerInt8; the English reranker below stays as the fallback.
    $MultilingualRerankerDir = Join-Path $assets "reranker-mmarco"
    New-Item -ItemType Directory -Force $MultilingualRerankerDir | Out-Null
    Get-PinnedAsset "cross-encoder/mmarco-mMiniLMv2-L12-H384-v1" "1427fd652930e4ba29e8149678df786c240d8825" "onnx/model_qint8_avx512.onnx" (Join-Path $MultilingualRerankerDir "model.onnx") 118620017 "sha256" "1825907d6c1a9001ff78124780bbde20a614a8c3df3b63409cf3c72c6fe5c8b4"
    Get-PinnedAsset "cross-encoder/mmarco-mMiniLMv2-L12-H384-v1" "1427fd652930e4ba29e8149678df786c240d8825" "tokenizer.json" (Join-Path $MultilingualRerankerDir "tokenizer.json") 17082660 "sha256" "62c24cdc13d4c9952d63718d6c9fa4c287974249e16b7ade6d5a85e7bbb75626"
    # Cross-encoder for optional local second-stage RAG relevance. The exact
    # model/vocab pair below matches PinnedModelManifest.RerankerMsMarcoMiniLm.
    $RerankerDir = Join-Path $assets "reranker"
    New-Item -ItemType Directory -Force $RerankerDir | Out-Null
    $rerRepo = "Xenova/ms-marco-MiniLM-L-6-v2"
    $rerRev = "a09144355adeed5f58c8ed011d209bf8ee5a1fec"
    Get-PinnedAsset $rerRepo $rerRev "onnx/model.onnx" (Join-Path $RerankerDir "model.onnx") 90992115 "sha256" "c623d0bcb99f4622beb413eaef00cfbe5db20df9f1dd982da4b4f26022881870"
    Get-PinnedAsset $rerRepo $rerRev "vocab.txt" (Join-Path $RerankerDir "vocab.txt") 231508 "git-sha1" "fb140275c155a9c7c5a3b3e0e77a9e839594a938"
}
# The remote pinned -Fetch asset is distinguishable from a user-provided replacement.
# Do not claim Qwen/Apache licensing for arbitrary files simply because they are GGUF.
foreach ($v in @($ModelOrigin, $ModelLicenseId)) {
    if ($v.Length -gt 180 -or $v -match '[\x00-\x1F\x7F<>]') {
        throw "Model origin and licence metadata must be short, single-line text without markup."
    }
}
$modelOriginLabel = if ($NoModel) { "none" } elseif ($Fetch -and $LowMemoryStarter) { "Qwen/Qwen3-0.6B-GGUF pinned SHA-256" } elseif ($Fetch) { "Qwen/Qwen3-4B-GGUF pinned SHA-256" } elseif ($ModelOrigin.Trim()) { $ModelOrigin.Trim() } else { "user-supplied (origin not verified)" }
$modelLicenseLabel = if ($NoModel) { "none" } elseif ($Fetch) { "Apache-2.0" } elseif ($ModelLicenseId.Trim()) { $ModelLicenseId.Trim() + " (builder-declared; verify before redistribution)" } else { "NOT VERIFIED - confirm licence before redistribution" }

$requiredInputs = @(@("EmbeddingDir", $EmbeddingDir), @("RuntimeDir", $RuntimeDir))
if (-not $NoModel) { $requiredInputs = @(,@("ModelPath", $ModelPath)) + $requiredInputs }
foreach ($pair in $requiredInputs) {
    if ([string]::IsNullOrWhiteSpace($pair[1]) -or -not (Test-Path $pair[1])) { throw "$($pair[0]) is required (or use -Fetch). Not found: '$($pair[1])'" }
}
# Avoid creating a "standalone" archive that silently drops from semantic RAG to lexical-only
# because the model directory existed but the actual files were missing.
foreach ($required in "model.onnx", "vocab.txt", "tokenizer_config.json", "config.json") {
    $file = Join-Path $EmbeddingDir $required
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Corporate Lite requires a complete local embedding engine: $file"
    }
}
# Even if the build uses offline pre-fetched assets, the shipped ONNX weights and
# tokenizer must match the Core's exact pinned baseline. No unreviewed substitutes.
Assert-PinnedAsset (Join-Path $EmbeddingDir "model.onnx") 90405214 "sha256" "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"
Assert-PinnedAsset (Join-Path $EmbeddingDir "vocab.txt") 231508 "git-sha1" "fb140275c155a9c7c5a3b3e0e77a9e839594a938"
Assert-PinnedAsset (Join-Path $EmbeddingDir "tokenizer_config.json") 350 "git-sha1" "c79f2b6a0cea6f4b564fed1938984bace9d30ff0"
Assert-PinnedAsset (Join-Path $EmbeddingDir "config.json") 612 "git-sha1" "72b987fd805cfa2b58c4c8c952b274a11bfd5a00"
if (-not [string]::IsNullOrWhiteSpace($MultilingualEmbeddingDir)) {
    Assert-PinnedAsset (Join-Path $MultilingualEmbeddingDir "model.onnx") 118054593 "sha256" "4d24e2bc01a447951524466ef533e52944bf48509e6552810bcee1a2711cb02c"
    Assert-PinnedAsset (Join-Path $MultilingualEmbeddingDir "tokenizer.json") 17082730 "sha256" "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39"
}
if (-not [string]::IsNullOrWhiteSpace($MultilingualRerankerDir)) {
    Assert-PinnedAsset (Join-Path $MultilingualRerankerDir "model.onnx") 118620017 "sha256" "1825907d6c1a9001ff78124780bbde20a614a8c3df3b63409cf3c72c6fe5c8b4"
    Assert-PinnedAsset (Join-Path $MultilingualRerankerDir "tokenizer.json") 17082660 "sha256" "62c24cdc13d4c9952d63718d6c9fa4c287974249e16b7ade6d5a85e7bbb75626"
}
if ($RequireReranker -and [string]::IsNullOrWhiteSpace($RerankerDir)) {
    throw "Complete local RAG requires -RerankerDir or -Fetch. No cloud or later runtime download is permitted."
}
if (-not [string]::IsNullOrWhiteSpace($RerankerDir)) {
    foreach ($required in "model.onnx", "vocab.txt") {
        $file = Join-Path $RerankerDir $required
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
            throw "Incomplete local reranker engine: $file"
        }
    }
    Assert-PinnedAsset (Join-Path $RerankerDir "model.onnx") 90992115 "sha256" "c623d0bcb99f4622beb413eaef00cfbe5db20df9f1dd982da4b4f26022881870"
    Assert-PinnedAsset (Join-Path $RerankerDir "vocab.txt") 231508 "git-sha1" "fb140275c155a9c7c5a3b3e0e77a9e839594a938"
}
if ($ModelPath -match '(?i)(personal|company|private)') { throw "Refusing a model path that looks private." }
if ($RuntimeTag -and $RuntimeTag -notmatch '^[A-Za-z0-9._-]+$') { throw "RuntimeTag contains unsafe characters." }
# Official CPU ZIPs sometimes contain a wrapper folder. Stage the native exe's actual parent.
$native = Join-Path $RuntimeDir "llama-server.exe"
if (-not (Test-Path $native)) {
    $nested = @(Get-ChildItem $RuntimeDir -Filter "llama-server.exe" -Recurse -File)
    if ($nested.Count -ne 1) { throw "CPU runtime must contain exactly one llama-server.exe. Got $($nested.Count)." }
    $RuntimeDir = $nested[0].DirectoryName
    $native = Join-Path $RuntimeDir "llama-server.exe"
}
if (-not (Test-Path $native)) { throw "CPU runtime executable missing." }
if ((Get-Item $native).Length -lt 4096) { throw "CPU runtime exe is unexpectedly small." }

# --- 3. Contained, pre-seeded data root + offline configuration -------------------------------------------------------------
$data = Join-Path $stage "PaRuXData"
$tag = if ($RuntimeTag) { $RuntimeTag } else { "bundled" }
$runtimeDest = Join-Path $data "LocalAI\runtime\llama.cpp-$tag-cpu"
New-Item -ItemType Directory -Force $runtimeDest, (Join-Path $data "models"), (Join-Path $data "ai-runtimes\embeddings") | Out-Null
Copy-Item (Join-Path $RuntimeDir "*") $runtimeDest -Recurse -Force
if (-not $NoModel) { Copy-Item $ModelPath (Join-Path $data "models") -Force }
# Extra models must not silently change the starter fallback.
if (-not $NoModel) { Set-Content (Join-Path (Join-Path $data "models") ".corporate-default-model") ([IO.Path]::GetFileName($ModelPath)) -Encoding UTF8 }
if (-not (Test-Path (Join-Path $runtimeDest "llama-server.exe"))) { throw "Bundled CPU runtime was not staged." }
# llama.cpp is MIT: ship its license text beside the engine.
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) "llama-cpp-conversion\LICENSE-llama.cpp") -Destination (Join-Path $runtimeDest "LICENSE-llama.cpp.txt") -Force
# --- 3b. The pinned Vulkan runtime (same lock entry, archive hash and staging code as the Standard Portable) --------------------------
# No download at run time and no unpinned asset: stage-msi-runtime.ps1 verifies the archive size and SHA-256 from the lock, expands it
# into a throw-away publish folder and writes a per-file SHA-256 inventory; the verified payload is then copied into the data root
# where LocalAiPaths.RuntimeVulkanPath looks for it. The CPU runtime above stays the validated default and the automatic fallback.
$vulkanLockPath = Join-Path $repoRoot "packaging\windows-baseline-runtime.lock.json"
$vulkanLock = Get-Content -LiteralPath $vulkanLockPath -Raw | ConvertFrom-Json
$vulkanArtifact = @($vulkanLock.artifacts | Where-Object { [string]$_.id -ceq "llama.cpp-vulkan" })
if ($vulkanArtifact.Count -ne 1) { throw "The runtime lock must contain exactly one llama.cpp-vulkan entry." }
$vulkanArtifact = $vulkanArtifact[0]
$vulkanFolder = [IO.Path]::GetFileName(([string]$vulkanArtifact.destination).TrimEnd('/', '\'))
if ($vulkanFolder -cne "llama.cpp-$($vulkanArtifact.version)-vulkan" -or $vulkanFolder -cne "llama.cpp-b10549-vulkan") {
    throw "Unexpected Vulkan runtime folder '$vulkanFolder'. The Core resolves llama.cpp-b10549-vulkan (LocalAiPaths.DefaultVulkanBuildTag)."
}
$vulkanTemp = Join-Path $outRoot "vulkan-stage"
if (Test-Path -LiteralPath $vulkanTemp) { Remove-Item -LiteralPath $vulkanTemp -Recurse -Force }
New-Item -ItemType Directory -Force $vulkanTemp | Out-Null
$vulkanStageArgs = @{ PublishDir = $vulkanTemp; ArtifactIds = @("llama.cpp-vulkan") }
if (-not [string]::IsNullOrWhiteSpace($VulkanCacheDir)) { $vulkanStageArgs["CacheDir"] = $VulkanCacheDir }
& (Join-Path $PSScriptRoot "stage-msi-runtime.ps1") @vulkanStageArgs
if (-not $?) { throw "Locked Vulkan runtime staging failed." }
$vulkanSource = Join-Path $vulkanTemp "PaRuX.Server\runtime-baseline\$vulkanFolder"
$vulkanDest = Join-Path $data "LocalAI\runtime\$vulkanFolder"
if (-not (Test-Path -LiteralPath (Join-Path $vulkanSource "llama-server.exe") -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $vulkanSource "ggml-vulkan.dll") -PathType Leaf)) {
    throw "The locked Vulkan runtime is missing llama-server.exe or ggml-vulkan.dll."
}
New-Item -ItemType Directory -Force $vulkanDest | Out-Null
Copy-Item (Join-Path $vulkanSource "*") $vulkanDest -Recurse -Force
# Re-verify every copied file against the inventory the locked staging wrote.
$vulkanInventory = Get-Content -LiteralPath (Join-Path $vulkanTemp "PaRuX.Server\runtime-baseline\staging-manifest.json") -Raw | ConvertFrom-Json
$vulkanPrefix = "runtime-baseline/$vulkanFolder/"
$vulkanEntries = @($vulkanInventory.files | Where-Object { ([string]$_.path).StartsWith($vulkanPrefix, [StringComparison]::Ordinal) })
if ($vulkanEntries.Count -lt 2) { throw "Vulkan staging inventory is incomplete." }
foreach ($entry in $vulkanEntries) {
    $copied = Join-Path $vulkanDest (([string]$entry.path).Substring($vulkanPrefix.Length).Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $copied -PathType Leaf) -or
        (Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$entry.sha256).ToLowerInvariant()) {
        throw "Vulkan runtime file differs from the locked inventory: $($entry.path)"
    }
}
# Truthful, auditable provenance beside the engine (IT can compare it with the published lock).
[ordered]@{ schemaVersion = 1; platform = "win-x64"; artifacts = @($vulkanArtifact) } | ConvertTo-Json -Depth 6 |
    Set-Content (Join-Path $vulkanDest "parux-runtime-lock.json") -Encoding UTF8
$vulkanSizeBytes = (Get-ChildItem -LiteralPath $vulkanDest -Recurse -File | Measure-Object Length -Sum).Sum
$vulkanSizeMb = [math]::Round($vulkanSizeBytes / 1MB, 1)
$vulkanNativeSha = (Get-FileHash (Join-Path $vulkanDest "llama-server.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
Remove-Item -LiteralPath $vulkanTemp -Recurse -Force -ErrorAction SilentlyContinue
# No prerequisites on the target PC: llama.cpp and ONNX Runtime import the Visual C++ runtime,
# so it travels app-local next to every native engine and next to the Core.
. (Join-Path $PSScriptRoot "portable-offline-assets.ps1")
$vcRuntimeVersion = Copy-PaRuXVcRuntime -Destinations @($runtimeDest, $vulkanDest, (Join-Path $appStage "PaRuX.Server"), $appStage)
foreach ($vcDir in @($runtimeDest, $vulkanDest, (Join-Path $appStage "PaRuX.Server"))) {
    foreach ($vcFile in "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll") {
        if (-not (Test-Path -LiteralPath (Join-Path $vcDir $vcFile) -PathType Leaf)) { throw "Visual C++ runtime not staged: $vcDir\$vcFile" }
    }
}
Copy-Item (Join-Path $EmbeddingDir "*") (Join-Path $data "ai-runtimes\embeddings") -Recurse -Force
if (-not [string]::IsNullOrWhiteSpace($MultilingualEmbeddingDir)) {
    $e5Dest = Join-Path $data "ai-runtimes\embeddings-e5"
    New-Item -ItemType Directory -Force $e5Dest | Out-Null
    Copy-Item (Join-Path $MultilingualEmbeddingDir "*") $e5Dest -Recurse -Force
}
if (-not [string]::IsNullOrWhiteSpace($MultilingualRerankerDir)) {
    $mmDest = Join-Path $data "ai-runtimes\rerank-mmarco"
    New-Item -ItemType Directory -Force $mmDest | Out-Null
    Copy-Item (Join-Path $MultilingualRerankerDir "*") $mmDest -Recurse -Force
}
if (-not [string]::IsNullOrWhiteSpace($RerankerDir)) {
    $rerankerDest = Join-Path $data "ai-runtimes\reranker"
    New-Item -ItemType Directory -Force $rerankerDest | Out-Null
    Copy-Item (Join-Path $RerankerDir "*") $rerankerDest -Recurse -Force
}
# The marker lives in app\ (no loose file at the package root); PaRuXDataRoot finds PaRuXData beside app\.
Set-Content (Join-Path $appStage "parux.corporate") "PaRuX Corporate Lite Offline: AI + documents only, no cloud." -Encoding ASCII
$settings = @{ OnboardingCompleted = $true; SetupProfileId = "ai"; StudioModeEnabled = $false; DefaultProfile = "AUTO" } | ConvertTo-Json
Set-Content (Join-Path $data "settings.json") $settings -Encoding UTF8
@'
@echo off
rem Corporate offline default: no external HTTP, data in the PaRuXData folder of the package (one level above "launchers").
setlocal
for %%I in ("%~dp0..") do set "PARUX_PACKAGE=%%~fI"
set "PARUX_CORPORATE=1"
set "PARUX_CORPORATE_MODEL_DOWNLOADS=0"
set "PARUX_DATA_ROOT=%PARUX_PACKAGE%\PaRuXData"
set "PARUX_LOCALAI_STATE_DIR=%PARUX_PACKAGE%\PaRuXData"
start "" "%PARUX_PACKAGE%\PaRuX.exe"
'@ | Set-Content (Join-Path $launchersStage "Start PaRuX Corporate Lite.cmd") -Encoding ASCII
@'
@echo off
rem Use only with IT approval. Allows GGUF model transfers, not cloud inference.
setlocal
for %%I in ("%~dp0..") do set "PARUX_PACKAGE=%%~fI"
set "PARUX_CORPORATE=1"
set "PARUX_CORPORATE_MODEL_DOWNLOADS=1"
set "PARUX_DATA_ROOT=%PARUX_PACKAGE%\PaRuXData"
set "PARUX_LOCALAI_STATE_DIR=%PARUX_PACKAGE%\PaRuXData"
start "" "%PARUX_PACKAGE%\PaRuX.exe"
'@ | Set-Content (Join-Path $launchersStage "Start PaRuX Corporate Model Downloads.cmd") -Encoding ASCII
@'
@echo off
rem IT option: force the CPU engine only. The bundled Vulkan (GPU) engine is never started.
setlocal
for %%I in ("%~dp0..") do set "PARUX_PACKAGE=%%~fI"
set "PARUX_CORPORATE=1"
set "PARUX_CORPORATE_MODEL_DOWNLOADS=0"
set "PARUX_CORPORATE_GPU=0"
set "PARUX_DATA_ROOT=%PARUX_PACKAGE%\PaRuXData"
set "PARUX_LOCALAI_STATE_DIR=%PARUX_PACKAGE%\PaRuXData"
start "" "%PARUX_PACKAGE%\PaRuX.exe"
'@ | Set-Content (Join-Path $launchersStage "Start PaRuX Corporate Lite (CPU only).cmd") -Encoding ASCII
$modelGuide = if ($NoModel) { "This NoModel edition has NO chat model: import a compatible GGUF before expecting local chat answers." } else { "Local GGUF included: $([IO.Path]::GetFileName($ModelPath)). The model is not guaranteed to fit every 8-16 GB PC; verify memory headroom before running." }
$modelLicenceGuide = "Bundled model origin: $modelOriginLabel. Licence: $modelLicenseLabel. The package checksum proves included bytes, not model origin or redistribution rights."
if ($LowMemoryStarter) {
    $modelLicenceGuide += " LOW-MEMORY STARTER: 0.6B is primarily for basic chat; it is not a quality replacement for the 4B model, especially for complex corporate RAG or reasoning."
}
$ragGuide = if ([string]::IsNullOrWhiteSpace($RerankerDir)) { "Local embeddings included; advanced second-stage reranking is NOT included in this reduced package." } else { "Local embeddings and cross-encoder reranking included for document retrieval." }
@"
PaRuX Corporate Lite Offline $Version
Start with PaRuX.exe or "launchers\Start PaRuX Corporate Lite.cmd". No installation and no administrator rights are needed by the application.
Folder layout: PaRuX.exe (launcher, the only file at the root), app\ (the application), PaRuXData\ (all data, never moved to the user profile),
docs\ (this file, manifest, licences), launchers\ (the .cmd entry points; they set the Corporate options and use ..\PaRuXData).
CPU runtime, Vulkan (GPU) runtime and embedding model are included. No runtime setup or Internet needed.
GPU: if a usable discrete graphics adapter is found, the bundled Vulkan engine ($vulkanSizeMb MB, llama.cpp $($vulkanArtifact.version), SHA-256 pinned in
PaRuXData/LocalAI/runtime/$vulkanFolder/parux-runtime-lock.json) is tried first. Integrated-GPU shared memory counts against the same RAM limit; an integrated GPU stays on the CPU engine by default (measured faster) unless PARUX_CORPORATE_GPU=force is set.
If Vulkan does not start in time or fails (no driver, VM, allocation error) PaRuX falls back automatically to the CPU engine. Nothing is
installed or downloaded. IT can force CPU only with "Start PaRuX Corporate Lite (CPU only).cmd" or the variable PARUX_CORPORATE_GPU=0.
$modelGuide
$modelLicenceGuide
$ragGuide
Offline: "launchers\Start PaRuX Corporate Lite.cmd". Import further GGUF files in Model Hub (files are copied into PaRuXData/models).
For IT-approved GGUF downloads only: "launchers\Start PaRuX Corporate Model Downloads.cmd". Model inference remains entirely local;
Gaming, media, online runtime installers, cloud inference and automatic updates stay disabled.
IT network rules still apply; downloads require network permission. Documents remain on this PC.
Licences: see docs\LICENSE.txt and docs\THIRD_PARTY_NOTICES.md. Bundled GGUF licence is stated above and must be verified independently; embeddings: multilingual-e5-small (MIT, default for Italian/English document search) and all-MiniLM-L6-v2 (Apache-2.0, fallback);
llama.cpp: MIT; optional cross-encoder reranker: Xenova/ms-marco-MiniLM-L-6-v2, review source licence before redistribution).

Struttura: PaRuX.exe (avviatore, unico file alla radice), app\ (l'applicazione), PaRuXData\ (tutti i dati), docs\ (documenti, manifest, licenze), launchers\ (avvii .cmd).
Avvio offline con motori CPU e Vulkan (GPU, $vulkanSizeMb MB) gia' inclusi: PaRuX.exe oppure "launchers\Start PaRuX Corporate Lite.cmd". Con una GPU integrata resta il motore CPU (piu' veloce, PARUX_CORPORATE_GPU=force per provare la GPU). Se la GPU non e' utilizzabile
o Vulkan non parte, PaRuX torna da solo al motore CPU. Solo CPU (IT): "Start PaRuX Corporate Lite (CPU only).cmd" oppure PARUX_CORPORATE_GPU=0.
Importa nuovi GGUF e scegli il modello da Chat. Solo con consenso IT usa "Start PaRuX Corporate Model Downloads.cmd".
"@ | Set-Content (Join-Path $docsStage "README-corporate-lite.txt") -Encoding UTF8

# --- 4. Manifest with hashes -----------------------------------------------------------------------------------------------
$files = Get-ChildItem $stage -Recurse -File | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifest = [ordered]@{
    product = "PaRuX Corporate Lite Offline"; version = $Version; generatedUtc = [DateTime]::UtcNow.ToString("o")
    modelIncluded = (-not $NoModel)
    model = $(if ($NoModel) { $null } else { [IO.Path]::GetFileName($ModelPath) }); modelSha256 = $(if ($NoModel) { $null } else { (Get-FileHash $ModelPath -Algorithm SHA256).Hash.ToLowerInvariant() })
    modelOrigin = $modelOriginLabel; modelLicense = $modelLicenseLabel; modelSourceVerifiedByBuilder = [bool]($Fetch -and -not $NoModel)
    starterProfile = $(if ($NoModel) { "none" } elseif ($LowMemoryStarter) { "low-memory-0.6b" } else { "standard-or-user-supplied" })
    runtimeTag = $tag; runtimeIncluded = $true; embeddingIncluded = $true; multilingualEmbeddingIncluded = (-not [string]::IsNullOrWhiteSpace($MultilingualEmbeddingDir)); multilingualRerankerIncluded = (-not [string]::IsNullOrWhiteSpace($MultilingualRerankerDir)); rerankerIncluded = (-not [string]::IsNullOrWhiteSpace($RerankerDir)); modelImportAllowed = $true; modelDownloadsRequireOptIn = $true
    nativeRuntimeSha256 = (Get-FileHash (Join-Path $runtimeDest "llama-server.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
    vulkanRuntimeIncluded = $true; vulkanRuntimeTag = [string]$vulkanArtifact.version; vulkanRuntimeFolder = $vulkanFolder
    vulkanArchiveName = [string]$vulkanArtifact.archiveName; vulkanArchiveSha256 = ([string]$vulkanArtifact.sha256).ToLowerInvariant()
    vulkanArchiveSizeBytes = [int64]$vulkanArtifact.sizeBytes; vulkanRuntimeSizeBytes = [int64]$vulkanSizeBytes
    vulkanRuntimeFileCount = @(Get-ChildItem -LiteralPath $vulkanDest -Recurse -File).Count
    vulkanNativeRuntimeSha256 = $vulkanNativeSha
    corporateGpuDisableVariable = "PARUX_CORPORATE_GPU"
    fileCount = $files.Count; files = $files
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $docsStage "MANIFEST.json") -Encoding UTF8

# --- 5. ZIP ('/' separators, one top-level folder) -----------------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zipName = if ($LowMemoryStarter) { "PaRuX-Corporate-Lite-LowMemory-Offline-win-x64.zip" } else { "PaRuX-Corporate-Lite-Offline-win-x64.zip" }
$zipPath = Join-Path $outRoot $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$fs = [IO.File]::Open($zipPath, [IO.FileMode]::Create, [IO.FileAccess]::Write)
try {
    $zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $root = (Resolve-Path $stage).Path.TrimEnd('\')
        foreach ($f in Get-ChildItem $root -Recurse -File) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, "PaRuX-Corporate-Lite/" + $f.FullName.Substring($root.Length + 1).Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $zip.Dispose() }
} finally { $fs.Dispose() }
$sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content (Join-Path $outRoot ([IO.Path]::GetFileNameWithoutExtension($zipName) + ".sha256.txt")) "$sha  $zipName" -Encoding ASCII
Write-Host "ZIP: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB) SHA-256 $sha"

# --- 6. Verify the finished ZIP ------------------------------------------------------------------------------------------
$z = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($z.Entries | ForEach-Object { $_.FullName })
    if (@($names | Where-Object { $_.Contains('\') }).Count -gt 0) { throw "ZIP has backslash entries." }
    $rel = @($names | ForEach-Object { $_.Substring("PaRuX-Corporate-Lite/".Length) })
    # Root = ONLY PaRuX.exe + app/, PaRuXData/, docs/, launchers/ (the build fails on anything else).
    Assert-PaRuXPackageLayout -Names $rel -Label "Corporate Lite ZIP"
    foreach ($req in "PaRuX.exe", "app/PaRuX.exe", "app/PaRuX.Server/PaRuX.Server.exe", "app/parux.corporate", "launchers/Start PaRuX Corporate Lite.cmd", "docs/MANIFEST.json", "docs/LICENSE.txt",
        "PaRuXData/settings.json", "launchers/Start PaRuX Corporate Model Downloads.cmd", "launchers/Start PaRuX Corporate Lite (CPU only).cmd", "docs/README-corporate-lite.txt") { if ($rel -notcontains $req) { throw "Missing $req" } }
    if (@($rel | Where-Object { $_ -match '(?i)^PaRuXData/(parux\.db|history|attachments|server/)' -or $_ -match '(?i)(^|/)(rag\.db|knowledge\.db|bootstrap\.json)$' }).Count -gt 0) { throw "ZIP contains user data/indexes/tokens." }
    $expectedModels = if ($NoModel) { 0 } else { 1 }
    if (@($rel | Where-Object { $_ -match '(?i)^PaRuXData/models/.+\.gguf$' }).Count -ne $expectedModels) { throw "Expected $expectedModels bundled model(s)." }
    if (-not $NoModel -and $rel -notcontains "PaRuXData/models/.corporate-default-model") { throw "Bundled default-model marker missing." }
    foreach ($needed in "model.onnx", "vocab.txt", "tokenizer_config.json", "config.json") {
        if ($rel -notcontains "PaRuXData/ai-runtimes/embeddings/$needed") {
            throw "Incomplete standalone embedding engine: $needed"
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($RerankerDir)) {
        foreach ($needed in "model.onnx", "vocab.txt") {
            if ($rel -notcontains "PaRuXData/ai-runtimes/reranker/$needed") {
                throw "Incomplete standalone reranker engine: $needed"
            }
        }
    }
    if (@($rel | Where-Object { $_ -match '(?i)^PaRuXData/LocalAI/runtime/llama\.cpp-[^/]+-cpu/llama-server\.exe$' }).Count -ne 1) {
        throw "Bundled CPU runtime executable missing from ZIP."
    }
    # The pinned Vulkan runtime must be in the finished archive, complete and matching the repo lock.
    $vulkanRel = "PaRuXData/LocalAI/runtime/$vulkanFolder"
    foreach ($needed in "llama-server.exe", "ggml-vulkan.dll", "LICENSE-llama.cpp.txt", "parux-runtime-lock.json") {
        if ($rel -notcontains "$vulkanRel/$needed") { throw "Bundled Vulkan runtime incomplete in ZIP: $needed" }
    }
    if (@($rel | Where-Object { $_ -match '(?i)^PaRuXData/LocalAI/runtime/llama\.cpp-[^/]+-vulkan/llama-server\.exe$' }).Count -ne 1) {
        throw "Expected exactly one bundled Vulkan llama-server.exe."
    }
    if (-not $manifest.vulkanRuntimeIncluded -or $manifest.vulkanRuntimeTag -cne [string]$vulkanArtifact.version) { throw "MANIFEST.json does not describe the Vulkan runtime." }
    $lockEntry = $z.GetEntry("PaRuX-Corporate-Lite/$vulkanRel/parux-runtime-lock.json")
    $lockReader = [IO.StreamReader]::new($lockEntry.Open())
    try { $shippedLock = $lockReader.ReadToEnd() | ConvertFrom-Json } finally { $lockReader.Dispose() }
    if (@($shippedLock.artifacts).Count -ne 1 -or ([string]@($shippedLock.artifacts)[0].sha256).ToLowerInvariant() -ne ([string]$vulkanArtifact.sha256).ToLowerInvariant()) {
        throw "Shipped Vulkan lock does not match packaging/windows-baseline-runtime.lock.json."
    }
    $cpuOnlyEntry = $z.GetEntry("PaRuX-Corporate-Lite/launchers/Start PaRuX Corporate Lite (CPU only).cmd")
    $cpuOnlyReader = [IO.StreamReader]::new($cpuOnlyEntry.Open())
    try { $cpuOnlyText = $cpuOnlyReader.ReadToEnd() } finally { $cpuOnlyReader.Dispose() }
    if ($cpuOnlyText -notmatch 'PARUX_CORPORATE_GPU=0') { throw "CPU-only launcher does not set PARUX_CORPORATE_GPU=0." }
    $offlineEntry = $z.GetEntry("PaRuX-Corporate-Lite/launchers/Start PaRuX Corporate Lite.cmd")
    $onlineEntry = $z.GetEntry("PaRuX-Corporate-Lite/launchers/Start PaRuX Corporate Model Downloads.cmd")
    if ($null -eq $offlineEntry -or $null -eq $onlineEntry) { throw "Missing Corporate launchers." }
    $reader = [IO.StreamReader]::new($offlineEntry.Open())
    try { $offlineText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $reader = [IO.StreamReader]::new($onlineEntry.Open())
    try { $onlineText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($offlineText -match 'PARUX_CORPORATE_GPU=0') { throw "The default launcher must not disable the GPU path." }
    # The .cmd files sit in launchers\: they must resolve data one level up, never beside themselves.
    foreach ($cmdText in @($cpuOnlyText, $offlineText, $onlineText)) {
        if ($cmdText -notmatch [regex]::Escape('%~dp0..') -or $cmdText -notmatch [regex]::Escape('%PARUX_PACKAGE%\PaRuXData')) {
            throw "A Corporate launcher does not resolve PaRuXData from the package root."
        }
    }
    if ($offlineText -notmatch 'PARUX_CORPORATE_MODEL_DOWNLOADS=0' -or $onlineText -notmatch 'PARUX_CORPORATE_MODEL_DOWNLOADS=1') {
        throw "Corporate model-download consent was not staged correctly."
    }
    # Verify native CPU engine and starter model bytes from the FINISHED archive, not just the stage.
    $nativePath = @($rel | Where-Object { $_ -match '(?i)^PaRuXData/LocalAI/runtime/llama\.cpp-[^/]+-cpu/llama-server\.exe$' })[0]
    $modelPathInZip = if ($NoModel) { $null } else { @($rel | Where-Object { $_ -match '(?i)^PaRuXData/models/.+\.gguf$' })[0] }
    $shaTool = [Security.Cryptography.SHA256]::Create()
    try {
        $checks = @(@{ entry = $nativePath; expected = $manifest.nativeRuntimeSha256; title = "CPU runtime" },
            @{ entry = "$vulkanRel/llama-server.exe"; expected = $manifest.vulkanNativeRuntimeSha256; title = "Vulkan runtime" })
        if (-not $NoModel) { $checks += @{ entry = $modelPathInZip; expected = $manifest.modelSha256; title = "Starter GGUF" } }
        foreach ($check in $checks) {
            $zipEntry = $z.GetEntry("PaRuX-Corporate-Lite/" + $check.entry)
            if ($null -eq $zipEntry) { throw "Missing $($check.title) archive entry." }
            $stream = $zipEntry.Open()
            try { $actual = [BitConverter]::ToString($shaTool.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
            finally { $stream.Dispose() }
            if ($actual -ne $check.expected) { throw "$($check.title) SHA-256 mismatch in finished ZIP." }
        }
    } finally { $shaTool.Dispose() }
    # Verify EVERY staged file from the finished archive, including native DLLs, Core, tokenizer
    # and ONNX assets. The old two-file verification could pass with missing/corrupt embedding DLLs.
    if ($names.Count -ne ($manifest.fileCount + 1)) { throw "Archive inventory count differs from MANIFEST.json." }
    foreach ($item in $files) {
        $entry = $z.GetEntry("PaRuX-Corporate-Lite/" + $item.path)
        if ($null -eq $entry -or $entry.Length -ne $item.size) {
            throw "Archive missing or truncated: $($item.path)"
        }
        $stream = $entry.Open()
        try {
            $shaTool = [Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($shaTool.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
            finally { $shaTool.Dispose() }
        } finally { $stream.Dispose() }
        if ($actual -ne $item.sha256) { throw "Corporate archive SHA-256 mismatch: $($item.path)" }
    }
    Write-Host "ZIP check OK: $($names.Count) entries, complete file SHA-256 inventory; vulkanRuntime=$($manifest.vulkanRuntimeTag) ($vulkanSizeMb MB); modelIncluded=$(-not $NoModel); rerankerIncluded=$(-not [string]::IsNullOrWhiteSpace($RerankerDir))."
} finally { $z.Dispose() }

# --- 7. Smoke: extracted package starts offline, Core is up, downloads are refused, document search answers -------------------
if ($Smoke) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("corp-smoke-" + [Guid]::NewGuid().ToString("N"))
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $tmp)
    $app = Join-Path $tmp "PaRuX-Corporate-Lite"
    $env:PARUX_CORPORATE = "1"; $env:PARUX_CORPORATE_MODEL_DOWNLOADS = "0"
    $env:PARUX_DATA_ROOT = Join-Path $app "PaRuXData"; $env:PARUX_LOCALAI_STATE_DIR = $env:PARUX_DATA_ROOT
    # A staged file is not enough: exercise the executable's Windows native DLL loader.
    $nativeExe = @(Get-ChildItem (Join-Path $app "PaRuXData/LocalAI/runtime") -Filter "llama-server.exe" -Recurse -File | Where-Object { $_.DirectoryName -match '-cpu$' })
    if ($nativeExe.Count -ne 1) { throw "Offline smoke: expected one bundled CPU llama-server.exe." }
    $stdout = Join-Path $tmp "cpu-help-out.txt"; $stderr = Join-Path $tmp "cpu-help-err.txt"
    $nativeProc = Start-Process $nativeExe[0].FullName -WorkingDirectory $nativeExe[0].DirectoryName -ArgumentList "--help" -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    # Windows PowerShell 5.1 drops ExitCode of a -PassThru process unless its handle is cached first.
    $null = $nativeProc.Handle
    if (-not $nativeProc.WaitForExit(15000)) {
        $nativeProc.Kill()
        throw "Offline smoke: native CPU loader timed out."
    }
    if ($nativeProc.ExitCode -ne 0) { throw "Offline smoke: native CPU loader failed (exit $($nativeProc.ExitCode))." }
    # The bundled Vulkan engine must at least load. A host without a Vulkan loader/ICD is only a warning: the app then runs on the CPU engine.
    $vulkanSmokeExe = Join-Path $app "PaRuXData/LocalAI/runtime/$vulkanFolder/llama-server.exe"
    $vOut = Join-Path $tmp "vulkan-help-out.txt"; $vErr = Join-Path $tmp "vulkan-help-err.txt"
    $vProc = Start-Process $vulkanSmokeExe -WorkingDirectory (Split-Path -Parent $vulkanSmokeExe) -ArgumentList "--help" -PassThru -NoNewWindow -RedirectStandardOutput $vOut -RedirectStandardError $vErr
    $null = $vProc.Handle
    if (-not $vProc.WaitForExit(15000)) { $vProc.Kill(); throw "Offline smoke: native Vulkan loader timed out." }
    if ($vProc.ExitCode -ne 0) { Write-Warning "Vulkan native loader unavailable on this host (exit $($vProc.ExitCode)); Corporate would use the CPU engine here." }
    # The process started here is the root launcher; the application is its child from app\.
    $proc = Start-Process (Join-Path $app "PaRuX.exe") -PassThru
    try {
        $boot = Join-Path $env:PARUX_DATA_ROOT "server\bootstrap.json"
        $deadline = (Get-Date).AddSeconds($SmokeTimeoutSeconds)
        while ((Get-Date) -lt $deadline -and -not (Test-Path $boot)) { if ($proc.HasExited) { throw "App exited early." }; Start-Sleep 2 }
        if (-not (Test-Path $boot)) { throw "Core did not start." }
        $appExePath = [IO.Path]::GetFullPath((Join-Path $app "app\PaRuX.exe"))
        if (@(Get-Process -Name PaRuX -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExePath }).Count -lt 1) {
            throw "The launcher did not start app\PaRuX.exe."
        }
        $b = Get-Content $boot -Raw | ConvertFrom-Json
        $h = @{ "X-PaRuX-Token" = $b.token }
        $r = try { Invoke-WebRequest -Uri ($b.endpoint.TrimEnd('/') + "/api/v1/models/downloads") -Method Post -Headers $h -ContentType "application/json" -Body "{}" -UseBasicParsing } catch { $_.Exception.Response }
        if ([int]$r.StatusCode -ne 403) { throw "Download endpoint was not refused (status $([int]$r.StatusCode))." }
        $s = Invoke-WebRequest -Uri ($b.endpoint.TrimEnd('/') + "/api/v1/knowledge/search?q=test") -Headers $h -UseBasicParsing
        if ($s.StatusCode -ne 200) { throw "Knowledge search not available." }
        # Actual ONNX load: basic keyword search can return 200 without the embedding model.
        # Corporate status is observational by design (an idle ONNX session is not re-created by a status check, so no RAM is
        # reserved for unused models). Prove the real native load by performing a real embedding first, then read the status.
        $probe = Invoke-RestMethod -Uri ($b.endpoint.TrimEnd('/') + "/v1/embeddings") -Method Post -Headers $h -ContentType "application/json" -Body '{"input":"smoke test"}' -TimeoutSec 120
        if (-not $probe.data -or @($probe.data[0].embedding).Count -lt 1) { throw "Offline smoke: bundled embedding ONNX engine returned no vector." }
        $emb = Invoke-RestMethod -Uri ($b.endpoint.TrimEnd('/') + "/api/v1/embeddings/engine") -Headers $h -TimeoutSec 120
        if (-not $emb.Installed -or -not $emb.Loaded) {
            throw "Offline smoke: bundled embedding ONNX engine failed real native load."
        }
        if (-not [string]::IsNullOrWhiteSpace($MultilingualEmbeddingDir)) {
            # probe=1 computes one real embedding first, so a model that is present but cannot run is caught here
            $ml = Invoke-RestMethod -Uri ($b.endpoint.TrimEnd('/') + "/api/v1/embeddings/multilingual/engine?probe=1") -Headers $h -TimeoutSec 180
            if (-not $ml.installed -or -not $ml.loaded -or [int]$ml.probeDimensions -ne 384) {
                throw "Offline smoke: bundled multilingual embedding engine failed real native load."
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($MultilingualRerankerDir)) {
            $mr = Invoke-RestMethod -Uri ($b.endpoint.TrimEnd('/') + "/api/v1/reranker/multilingual/engine?probe=1") -Headers $h -TimeoutSec 180
            if (-not $mr.installed -or -not $mr.loaded -or $null -eq $mr.probeScore) {
                throw "Offline smoke: bundled multilingual reranker failed real native load."
            }
        }
        if ($RequireReranker) {
            $rr =Invoke-RestMethod -Uri ($b.endpoint.TrimEnd('/') + "/api/v1/reranker/rerank") -Method Post -Headers $h -ContentType "application/json" -Body '{"query":"engine temperature","documents":["coolant temperature sensor","radio volume"],"topK":2}' -TimeoutSec 120
            if (@($rr).Count -lt 1) { throw "Offline smoke: bundled reranker ONNX engine returned no ranking." }
            $rer = Invoke-RestMethod -Uri ($b.endpoint.TrimEnd('/') + "/api/v1/reranker/engine") -Headers $h -TimeoutSec 120
            if (-not $rer.Installed -or -not $rer.Loaded) {
                throw "Offline smoke: bundled reranker ONNX engine failed real native load."
            }
        }
        Write-Host "Smoke OK: offline start, downloads refused (403), embedding ONNX loaded$(if ($RequireReranker) { ', reranker ONNX loaded' }). This does NOT verify document indexing, source citations or chat inference; run tests/corporate/measure-corporate-lite.ps1 -EnforceAcceptance with a bundled model."
    } finally {
        Get-PaRuXPackageProcessesUnder -Root $tmp | Stop-Process -Force -ErrorAction SilentlyContinue
        Remove-Item Env:PARUX_CORPORATE, Env:PARUX_CORPORATE_MODEL_DOWNLOADS, Env:PARUX_DATA_ROOT, Env:PARUX_LOCALAI_STATE_DIR -ErrorAction SilentlyContinue
        Start-Sleep 2; Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host "Done."
