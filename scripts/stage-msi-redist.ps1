<#
.SYNOPSIS
  Stages the vendor payloads of the optional MSI features (ViGEmBus, HidHide, Sunshine) from packaging/redist-installers.lock.json.
.DESCRIPTION
  Only the pinned release-asset URLs of the lock file are ever fetched (never "latest", never an unpinned tag). Every file is
  size- and SHA-256-checked against the lock (and against the digest GitHub publishes, when the lock records one); vendor installers
  must also carry a valid Authenticode signature from the pinned publisher. Any mismatch deletes the download and fails the build
  with a clear message. Downloads go to artifacts\cache\redist (git-ignored); the staged tree goes to artifacts\redist-stage.
  Nothing is modified: files are copied byte for byte. Use build-msi.ps1 -NoRedist to build an MSI without any of this.

  Output layout (the WiX project harvests these three folders):
    <OutputDir>\Base\      Invoke-PaRuXRedist.ps1 + redist-installers.lock.json   (small; always with the redist build)
    <OutputDir>\Drivers\   ViGEmBus + HidHide installers + their licence texts
    <OutputDir>\Sunshine\  Sunshine portable ZIP + GPL-3.0 text + written source offer
#>
[CmdletBinding()]
param(
    [string]$LockFile = "",
    [string]$CacheDir = "",
    [string]$OutputDir = "",
    [switch]$NoDownload
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($LockFile)) { $LockFile = Join-Path $repoRoot "packaging\redist-installers.lock.json" }
if ([string]::IsNullOrWhiteSpace($CacheDir)) { $CacheDir = Join-Path $repoRoot "artifacts\cache\redist" }
if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = Join-Path $repoRoot "artifacts\redist-stage" }
$LockFile = [System.IO.Path]::GetFullPath($LockFile)
$CacheDir = [System.IO.Path]::GetFullPath($CacheDir)
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

# Shared pure checks (hash/signature/argument rules) live in the helper that also runs at install time.
. (Join-Path $PSScriptRoot "redist\Invoke-PaRuXRedist.ps1")

if (-not (Test-Path -LiteralPath $LockFile -PathType Leaf)) {
    throw "Redist lock file is missing: $LockFile. Build with -NoRedist to produce an MSI without the optional vendor payloads."
}
$lock = Get-Content -LiteralPath $LockFile -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.platform -ne "win-x64") { throw "Unsupported redist lock schema/platform." }
$components = @($lock.components | Where-Object { [string]$_.status -like "included*" })
if ($components.Count -lt 1) { throw "The redist lock has no included component." }

function Assert-LockEntry($c) {
    $id = [string]$c.id
    if ($id -notmatch '^[a-z0-9-]+$') { throw "Lock: invalid id '$id'." }
    if ([string]$c.asset.sha256 -notmatch '^[0-9a-f]{64}$') { throw "Lock: $id has no valid pinned SHA-256." }
    if ([long]$c.asset.sizeBytes -le 0) { throw "Lock: $id has no pinned size." }
    $expectedPrefix = "https://github.com/"
    $url = [string]$c.asset.url
    if (-not $url.StartsWith($expectedPrefix, [System.StringComparison]::Ordinal)) { throw "Lock: $id URL is not a github.com release asset." }
    if ($url -notmatch '/releases/download/' + [regex]::Escape([string]$c.releaseTag) + '/' + [regex]::Escape([string]$c.asset.name) + '$') { throw "Lock: $id URL is not pinned to tag $($c.releaseTag) and file $($c.asset.name)." }
    if ($url -match '/latest/') { throw "Lock: $id points at 'latest'." }
    if ($null -ne $c.asset.githubDigest -and [string]$c.asset.githubDigest -ne ("sha256:" + [string]$c.asset.sha256)) { throw "Lock: $id pinned SHA-256 differs from the GitHub digest." }
    if ([string]$c.install.type -eq "advanced-installer-exe") {
        $e = Test-RedistSilentArgs $c
        if ($e) { throw "Lock: $e" }
        if (-not [bool]$c.signature.required) { throw "Lock: $id is an elevated installer without a required signature." }
    }
    $licence = Join-Path $repoRoot ([string]$c.license.textFile)
    if (-not (Test-Path -LiteralPath $licence -PathType Leaf)) { throw "Lock: $id licence text is missing: $($c.license.textFile)" }
    if (([string]$c.license.spdx).StartsWith("GPL") -and -not (Test-Path -LiteralPath (Join-Path $repoRoot ([string]$c.sourceOffer.textFile)) -PathType Leaf)) {
        throw "Lock: $id is GPL but its written source offer file is missing."
    }
}

function Get-VerifiedAsset($c) {
    $path = Join-Path $CacheDir ([string]$c.asset.name)
    $fail = $null
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $fail = Test-RedistPayload $c (Get-Item -LiteralPath $path).Length (Get-RedistSha256 $path)
        if ($fail) { Write-Host "Cached $($c.id) is not the pinned file ($fail); discarding it."; Remove-Item -LiteralPath $path -Force }
    } else { $fail = "missing" }
    if ($fail) {
        if ($NoDownload) { throw "Redist payload $($c.id) $($c.version) is not in the cache ($path) and -NoDownload was given. Download it from $($c.asset.url) or build with -NoRedist." }
        $partial = "$path.partial"
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
        Write-Host "Downloading pinned $($c.id) $($c.version) ($([math]::Round([long]$c.asset.sizeBytes / 1MB, 1)) MB) from $($c.asset.url)"
        try { Invoke-WebRequest -Uri ([string]$c.asset.url) -OutFile $partial -UseBasicParsing }
        catch { Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue; throw "Could not download the pinned $($c.id) asset: $($_.Exception.Message). Check the network, or build with -NoRedist." }
        $bad = Test-RedistPayload $c (Get-Item -LiteralPath $partial).Length (Get-RedistSha256 $partial)
        if ($bad) { Remove-Item -LiteralPath $partial -Force; throw "Verification failed, download discarded: $bad" }
        Move-Item -LiteralPath $partial -Destination $path -Force
    }
    if ([bool]$c.signature.required) {
        $sig = Get-AuthenticodeSignature -LiteralPath $path
        $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { "" }
        $thumb = if ($sig.SignerCertificate) { $sig.SignerCertificate.Thumbprint } else { "" }
        $bad = Test-RedistSignature $c ([string]$sig.Status) $subject $thumb
        if ($bad) { throw "Authenticode check failed: $bad" }
    }
    return $path
}

if (Test-Path -LiteralPath $OutputDir) { Remove-Item -LiteralPath $OutputDir -Recurse -Force }
$base = Join-Path $OutputDir "Base"
New-Item -ItemType Directory -Force -Path $base | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "redist\Invoke-PaRuXRedist.ps1") -Destination $base -Force
Copy-Item -LiteralPath $LockFile -Destination (Join-Path $base "redist-installers.lock.json") -Force

$manifest = @()
foreach ($c in $components) {
    Assert-LockEntry $c
    $source = Get-VerifiedAsset $c
    $folder = Join-Path $OutputDir ([string]$c.payloadFolder)
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $folder ([string]$c.asset.name)) -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot ([string]$c.license.textFile)) -Destination $folder -Force
    if ($null -ne $c.sourceOffer) { Copy-Item -LiteralPath (Join-Path $repoRoot ([string]$c.sourceOffer.textFile)) -Destination $folder -Force }
    $manifest += [pscustomobject]@{ id = $c.id; version = $c.version; asset = $c.asset.name; sizeBytes = [long]$c.asset.sizeBytes; sha256 = $c.asset.sha256; license = $c.license.spdx }
    Write-Host "Staged $($c.id) $($c.version): $($c.asset.name), SHA-256 verified$(if ([bool]$c.signature.required) { ', Authenticode verified' })."
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir "staged-manifest.json") -Encoding UTF8
$total = ($manifest | Measure-Object -Property sizeBytes -Sum).Sum
Write-Host "Redist payload staged: $($manifest.Count) component(s), $([math]::Round($total / 1MB, 1)) MB added to the MSI (before MSI compression)."
