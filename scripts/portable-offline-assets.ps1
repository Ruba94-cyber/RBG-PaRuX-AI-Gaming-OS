<#
 Shared build-machine helpers for Corporate Lite and the MSI: pinned-asset verification and the app-local
 Visual C++ runtime copy. This script defines functions only: no network, writes or model installs occur when it is dot-sourced.
 Native dependencies are shipped INSIDE the package, never installed on the target.
#>
. (Join-Path $PSScriptRoot "llama-cpu-pin.ps1")
$ProgressPreference = "SilentlyContinue"   # see build-corporate-lite.ps1: the 5.1 progress bar makes large downloads ~10x slower

function Assert-PaRuXPinnedAsset {
    param([string]$File, [long]$Size, [string]$HashKind, [string]$Digest)
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { throw "Missing offline asset: $File" }
    $info = Get-Item -LiteralPath $File
    if ($Size -gt 0 -and $info.Length -ne $Size) { throw "Asset size mismatch: $File" }
    if ($HashKind -eq "sha256") {
        $actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    } elseif ($HashKind -eq "git-sha1") {
        if ($info.Length -gt 1048576) { throw "Unreasonably large Git blob: $File" }
        $bytes = [IO.File]::ReadAllBytes($File)
        $header = [Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)" + [char]0)
        $blob = New-Object byte[] ($header.Length + $bytes.Length)
        [Buffer]::BlockCopy($header, 0, $blob, 0, $header.Length)
        [Buffer]::BlockCopy($bytes, 0, $blob, $header.Length, $bytes.Length)
        $hash = [Security.Cryptography.SHA1]::Create()
        try { $actual = [BitConverter]::ToString($hash.ComputeHash($blob)).Replace("-", "").ToLowerInvariant() }
        finally { $hash.Dispose() }
    } else { throw "Unsupported asset digest type: $HashKind" }
    if ($actual -ne $Digest.ToLowerInvariant()) { throw "Asset hash mismatch: $File" }
}

# The official llama.cpp and ONNX Runtime binaries import the Microsoft Visual C++ runtime.
# A clean Windows PC is not guaranteed to have it, so the portable editions ship it app-local
# (the deployment Microsoft documents for the redistributable DLLs) from the build PC's licensed
# Visual Studio Redist folder. Every copied file must carry a valid Microsoft signature.
function Copy-PaRuXVcRuntime {
    param([string[]]$Destinations)
    $names = @("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll",
               "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll")
    $candidates = @()
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $vs = Join-Path $root "Microsoft Visual Studio"
        if (-not (Test-Path -LiteralPath $vs -PathType Container)) { continue }
        $candidates += @(Get-ChildItem -LiteralPath $vs -Directory -Recurse -Filter "Microsoft.VC14*.CRT" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\Redist\\MSVC\\[0-9.]+\\x64\\Microsoft\.VC14\d\.CRT$' })
    }
    # Build PCs without the Visual Studio C++ workload (Build Tools only, no admin rights) can point to a folder holding the x64 DLLs
    # taken from the official signed vc_redist.x64.exe. The same Microsoft-signature check below still applies to every file.
    $overrideDir = $env:PARUX_VC_REDIST_X64_DIR
    if (-not [string]::IsNullOrWhiteSpace($overrideDir) -and (Test-Path -LiteralPath $overrideDir -PathType Container)) {
        $candidates = @(Get-Item -LiteralPath $overrideDir)
    }
    $source = $candidates | Sort-Object { [version]((Get-Item (Join-Path $_.FullName "vcruntime140.dll")).VersionInfo.FileVersion -replace '[^0-9.].*$', '') } -Descending |
        Select-Object -First 1
    if ($null -eq $source) {
        throw "Visual C++ x64 redistributable DLLs not found on the build PC (Visual Studio 'VC\Redist\MSVC\<version>\x64\Microsoft.VC14x.CRT'). The portable editions must ship them: without them llama.cpp and ONNX Runtime do not start on a clean PC."
    }
    foreach ($name in $names) {
        $file = Join-Path $source.FullName $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Visual C++ runtime file missing in the Redist folder: $name" }
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
            throw "Visual C++ runtime file is not validly signed by Microsoft: $file"
        }
    }
    foreach ($destination in $Destinations) {
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $source.FullName $name) -Destination $destination -Force }
    }
    $version = (Get-Item (Join-Path $source.FullName "vcruntime140.dll")).VersionInfo.FileVersion
    Write-Host "Visual C++ runtime $version bundled app-local in $($Destinations.Count) folder(s)."
    return [string]$version
}
