<#
.SYNOPSIS
  Read-only NuGet package/native-library coverage check for THIRD_PARTY_NOTICES.md.
.DESCRIPTION
  Augments (never replaces) tools/validate-license-inventory.ps1 and scripts/generate-sbom.ps1.
  Checks (all read-only; no dotnet invocation, no downloads, no legal clearance claim):
    1. every direct PackageReference of the shipped projects is named in the notices;
    2. every package that contributes runtime/native/resource assets in src/*/obj/project.assets.json
       (transitive closure) is named in the notices. Skipped with a warning when no restore output exists
       (obj/ is Git-ignored) unless -RequireAssets is passed;
    3. every known bundled native library rule (DirectML.dll, msquic.dll, e_sqlite3.dll, llama.cpp, ...) has its
       notice token present in the notices;
    4. with -PublishDir: every unmanaged DLL in that publish/portable folder matches a rule above or is first-party,
       and its notice token is present (a new unclassified native DLL fails the gate);
    5. packaging scripts still ship the llama.cpp license text and the MSIX script does not ship the CUDA notice.
  Use -SelfTest for deterministic synthetic-fixture checks of every rule.
#>
[CmdletBinding()]
param([string]$Root = "", [string]$PublishDir = "", [switch]$RequireAssets, [switch]$SelfTest)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Find-MissingNotices([string[]]$Packages, [string]$NoticeText) {
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($package in ($Packages | Sort-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace($package)) { continue }
        if ($NoticeText.IndexOf($package, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $missing.Add($package)
        }
    }
    return @($missing.ToArray())
}

# Native library rule table: first matching rule classifies an unmanaged DLL; its Token must appear in the notices.
# A null Token marks first-party code. Order matters (specific before generic).
$script:NativeRules = @(
    @{ Pattern = '^(PaRuX|parux_)'; Token = $null },
    @{ Pattern = '^(ggml|llama|mtmd)'; Token = 'llama.cpp' },
    @{ Pattern = '^libomp\.dll$'; Token = 'libomp.dll' },
    @{ Pattern = '^DirectML\.dll$'; Token = 'DirectML.dll' },
    @{ Pattern = '^msquic\.dll$'; Token = 'msquic.dll' },
    @{ Pattern = '^e_sqlite3\.dll$'; Token = 'e_sqlite3.dll' },
    @{ Pattern = '^onnxruntime'; Token = 'Microsoft.ML.OnnxRuntime' },
    @{ Pattern = '^(WebView2Loader|Microsoft\.Web\.WebView2)'; Token = 'Microsoft.Web.WebView2' },
    @{ Pattern = '^(vcruntime|msvcp|concrt|vcomp)'; Token = 'Visual C++ runtime' },
    @{ Pattern = '^(coreclr|clrjit|clrgc|clrgcexp|clretwrc|hostfxr|hostpolicy|mscor|mscordaccore|mscordbi|aspnetcorev2|marshal|createdump|Microsoft\.DiaSymReader\.Native|System\..*\.Native)'; Token = '.NET 10' },
    @{ Pattern = '^(Microsoft\.(UI|Windows|WindowsAppRuntime|DirectManipulation|Graphics|InputStateManager|Internal\.FrameworkUdk)|WindowsApp|CoreMessagingXP|dcompi|dwmcorei|DwmSceneI|DWriteCore|MRM|NPUDetect|PushNotifications|SessionHandleIPC|wuceffectsi|WinUIEdit)'; Token = 'Microsoft.WindowsAppSDK' }
)

function Find-NativeRuleViolations([string[]]$DllNames, [string]$NoticeText) {
    $violations = New-Object System.Collections.Generic.List[string]
    foreach ($name in ($DllNames | Sort-Object -Unique)) {
        $rule = $script:NativeRules | Where-Object { $name -imatch $_.Pattern } | Select-Object -First 1
        if ($null -eq $rule) { $violations.Add("$name (unclassified native DLL: add a rule and a notice row)"); continue }
        if ($null -ne $rule.Token -and $NoticeText.IndexOf([string]$rule.Token, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $violations.Add("$name (notice token '$($rule.Token)' missing)")
        }
    }
    return @($violations.ToArray())
}

function Find-MissingRuleTokens([string]$NoticeText) {
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($rule in $script:NativeRules) {
        if ($null -ne $rule.Token -and $NoticeText.IndexOf([string]$rule.Token, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { $missing.Add([string]$rule.Token) }
    }
    return @($missing.ToArray() | Sort-Object -Unique)
}

# Packages that contribute redistributable assets (runtime/native/resource) to any target of a project.assets.json.
# Build-only packages (analyzers, build tools, ILLink) have none of these and are not redistributed.
function Get-RedistributedAssetPackages([string]$AssetsJson) {
    $doc = $AssetsJson | ConvertFrom-Json
    $names = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($target in $doc.targets.PSObject.Properties) {
        foreach ($lib in $target.Value.PSObject.Properties) {
            $entry = $lib.Value
            if ([string]$entry.type -ne 'package') { continue }
            $hasAssets = $false
            foreach ($section in 'runtime', 'native', 'runtimeTargets', 'resource') {
                if (($entry.PSObject.Properties.Name -contains $section) -and @($entry.$section.PSObject.Properties).Count -gt 0) { $hasAssets = $true }
            }
            if ($hasAssets) { [void]$names.Add(($lib.Name -split '/')[0]) }
        }
    }
    return @($names | Sort-Object)
}

function Test-ManagedPe([string]$Path) {
    $fs = [IO.File]::OpenRead($Path)
    try {
        $br = New-Object IO.BinaryReader($fs)
        if ($fs.Length -lt 0x40 -or $br.ReadUInt16() -ne 0x5A4D) { return $null }
        $fs.Position = 0x3C; $pe = $br.ReadInt32()
        $fs.Position = $pe; if ($br.ReadUInt32() -ne 0x4550) { return $null }
        $fs.Position = $pe + 24; $magic = $br.ReadUInt16()
        $dirs = if ($magic -eq 0x20B) { $pe + 24 + 112 } else { $pe + 24 + 96 }
        $fs.Position = $dirs + 14 * 8
        return ($br.ReadUInt32() -ne 0)
    } finally { $fs.Dispose() }
}

function Get-UnmanagedDllNames([string]$Dir) {
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($file in Get-ChildItem -LiteralPath $Dir -Recurse -File -Filter *.dll) {
        $managed = $null
        try { $managed = Test-ManagedPe $file.FullName } catch { $managed = $null }
        if ($managed -eq $false) { $names.Add($file.Name) }
    }
    return @($names.ToArray())
}

if ($SelfTest) {
    $fixture = "| Component | License | Purpose |" + [Environment]::NewLine +
        "| Example.Library | MIT | synthetic |"
    $missing = @(Find-MissingNotices -Packages @("Example.Library", "Missing.Dependency") -NoticeText $fixture)
    if ($missing.Count -ne 1 -or $missing[0] -ne "Missing.Dependency") {
        throw "Synthetic missing-notice test failed."
    }
    $covered = @(Find-MissingNotices -Packages @("Example.Library") -NoticeText $fixture)
    if ($covered.Count -ne 0) { throw "Synthetic positive coverage test failed." }
    # Transitive packages from a synthetic project.assets.json: runtime-asset packages count, build-only ones do not.
    $assets = '{"targets":{"net10.0":{"Transitive.Lib/1.0.0":{"type":"package","runtime":{"lib/net10.0/T.dll":{}}},"Native.Only/2.0.0":{"type":"package","native":{"x.dll":{}}},"Build.Tool/3.0.0":{"type":"package","build":{"b.props":{}}},"Proj.Ref/1.0.0":{"type":"project","runtime":{"p.dll":{}}}}}}'
    $found = @(Get-RedistributedAssetPackages -AssetsJson $assets)
    if (($found -join ',') -ne 'Native.Only,Transitive.Lib') { throw "Synthetic assets parsing test failed: $($found -join ',')" }
    $gap = @(Find-MissingNotices -Packages $found -NoticeText $fixture)
    if ($gap.Count -ne 2) { throw "Synthetic transitive-gap test failed." }
    # Native DLL rules: unclassified fails, classified-with-token passes, missing token fails, first-party passes.
    $native = "| msquic.dll | MIT |" + [Environment]::NewLine + "| e_sqlite3.dll | public domain |"
    $bad = @(Find-NativeRuleViolations -DllNames @("mystery.dll", "msquic.dll", "DirectML.dll", "PaRuX.Native.dll") -NoticeText $native)
    if ($bad.Count -ne 2 -or ($bad -join ';') -notmatch 'mystery\.dll' -or ($bad -join ';') -notmatch 'DirectML\.dll') { throw "Synthetic native-rule test failed: $($bad -join ';')" }
    if (@(Find-NativeRuleViolations -DllNames @("msquic.dll", "e_sqlite3.dll", "parux_xinput.dll") -NoticeText $native).Count -ne 0) { throw "Synthetic native positive test failed." }
    if ((Find-MissingRuleTokens -NoticeText "").Count -lt 5) { throw "Synthetic rule-token test failed." }
    Write-Host "[notices] Synthetic fixture tests passed; no repository files changed."
    return
}

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
$noticePath = Join-Path $Root "THIRD_PARTY_NOTICES.md"
if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) {
    throw "THIRD_PARTY_NOTICES.md is missing."
}
$notice = Get-Content -LiteralPath $noticePath -Raw
$projects = @(
    "src/PaRuX.App/PaRuX.App.csproj",
    "src/PaRuX.Server/PaRuX.Server.csproj",
    "src/PaRuX.Cli/PaRuX.Cli.csproj",
    "src/PaRuX.WinMlWorker/PaRuX.WinMlWorker.csproj",
    "src/PaRuX.UpdateHelper/PaRuX.UpdateHelper.csproj"
)
$packages = New-Object System.Collections.Generic.List[string]
foreach ($project in $projects) {
    $file = Join-Path $Root $project
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Project missing: $project" }
    [xml]$projectXml = Get-Content -LiteralPath $file -Raw
    foreach ($element in @($projectXml.SelectNodes("//PackageReference[@Include]"))) {
        $package = [string]$element.GetAttribute("Include")
        if (-not [string]::IsNullOrWhiteSpace($package)) { $packages.Add($package) }
    }
}
$missing = @(Find-MissingNotices -Packages $packages.ToArray() -NoticeText $notice)
if ($missing.Count -gt 0) {
    throw "[notices] Missing direct package mentions: $($missing -join ', '). Audit actual licenses and redistribution boundaries before updating notices."
}

# 2) Transitive closure from restore output.
$transitive = New-Object System.Collections.Generic.List[string]
$assetFiles = @()
foreach ($project in $projects) {
    $assetsPath = Join-Path (Join-Path $Root (Split-Path -Parent $project)) "obj/project.assets.json"
    if (Test-Path -LiteralPath $assetsPath -PathType Leaf) { $assetFiles += $assetsPath }
}
if ($assetFiles.Count -eq 0) {
    if ($RequireAssets) { throw "[notices] -RequireAssets: no obj/project.assets.json found. Run dotnet restore first." }
    Write-Host "[notices] WARN: no project.assets.json found (obj/ is Git-ignored); transitive check skipped. Run dotnet restore and re-run, or pass -RequireAssets in release validation."
} else {
    foreach ($assetsPath in $assetFiles) {
        foreach ($package in @(Get-RedistributedAssetPackages -AssetsJson (Get-Content -LiteralPath $assetsPath -Raw))) { $transitive.Add($package) }
    }
    $missingTransitive = @(Find-MissingNotices -Packages $transitive.ToArray() -NoticeText $notice)
    if ($missingTransitive.Count -gt 0) {
        throw "[notices] Missing transitive package mentions (from project.assets.json): $($missingTransitive -join ', '). Read each package's license from its nuspec/LICENSE and add a row."
    }
}

# 3) Native rule tokens.
$missingTokens = @(Find-MissingRuleTokens -NoticeText $notice)
if ($missingTokens.Count -gt 0) { throw "[notices] Missing notice text for bundled native libraries: $($missingTokens -join ', ')." }

# 4) Optional publish/portable folder scan.
$nativeCount = 0
if (-not [string]::IsNullOrWhiteSpace($PublishDir)) {
    if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) { throw "[notices] -PublishDir not found: $PublishDir" }
    $dllNames = @(Get-UnmanagedDllNames -Dir $PublishDir)
    $nativeCount = @($dllNames | Sort-Object -Unique).Count
    $violations = @(Find-NativeRuleViolations -DllNames $dllNames -NoticeText $notice)
    if ($violations.Count -gt 0) { throw "[notices] Native DLL notice gaps in ${PublishDir}: $($violations -join '; ')" }
}

# 5) Packaging scripts keep shipping the llama.cpp license and the MSIX script stays free of the CUDA notice.
foreach ($script in "scripts/stage-msi-runtime.ps1", "scripts/build-corporate-app-shell.ps1", "scripts/build-corporate-lite.ps1", "scripts/build-msi.ps1", "scripts/build-msix.ps1") {
    $scriptPath = Join-Path $Root $script
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw "[notices] Packaging script missing: $script" }
    if ((Get-Content -LiteralPath $scriptPath -Raw).IndexOf("LICENSE-llama.cpp", [System.StringComparison]::Ordinal) -lt 0) {
        throw "[notices] $script no longer ships the llama.cpp MIT license text (LICENSE-llama.cpp)."
    }
}
$msixText = Get-Content -LiteralPath (Join-Path $Root "scripts/build-msix.ps1") -Raw
if ($msixText -match '(?m)^\s*foreach \(\$notice in @\([^\r\n]*NVIDIA_CUDA_NOTICE') {
    throw "[notices] build-msix.ps1 ships NVIDIA_CUDA_NOTICE.txt but the Store package contains no CUDA runtime."
}

Write-Host "[notices] PASS: $($packages.Count) direct PackageReference entries, $(@($transitive | Sort-Object -Unique).Count) transitive redistributed packages ($($assetFiles.Count) assets file(s)), $($script:NativeRules.Count) native rules, $nativeCount native DLLs scanned. This is not license clearance."
Write-Host "[notices] Run tools/validate-license-inventory.ps1 and scripts/generate-sbom.ps1 on Windows as separate gates."
