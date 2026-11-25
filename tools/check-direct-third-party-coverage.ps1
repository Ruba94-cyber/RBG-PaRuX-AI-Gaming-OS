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

function Find-RedistNoticeGaps([string]$LockJson, [string]$NoticeText, [string]$Root) {
    $gaps = New-Object System.Collections.Generic.List[string]
    $lock = $LockJson | ConvertFrom-Json
    foreach ($c in @($lock.components | Where-Object { [string]$_.status -like "included*" })) {
        $id = [string]$c.id
        foreach ($token in @([string]$c.displayName, [string]$c.version, [string]$c.license.spdx, [string]$c.asset.sha256, [string]$c.asset.name)) {
            if ([string]::IsNullOrWhiteSpace($token) -or $NoticeText.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                if ($token -eq [string]$c.displayName -and $NoticeText.IndexOf(([string]$c.displayName -split ' ')[0], [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { continue }
                $gaps.Add("$id notice is missing '$token'")
            }
        }
        if ([string]::IsNullOrWhiteSpace([string]$c.license.textFile) -or -not (Test-Path -LiteralPath (Join-Path $Root ([string]$c.license.textFile)) -PathType Leaf)) { $gaps.Add("$id licence text file is missing") }
        if (([string]$c.license.spdx).StartsWith("GPL")) {
            if ($null -eq $c.sourceOffer) { $gaps.Add("$id is GPL but has no source offer") }
            else {
                foreach ($u in @([string]$c.sourceOffer.sourceRepository, [string]$c.sourceOffer.sourceArchive)) {
                    if ($u -notlike "*$([string]$c.releaseTag)*") { $gaps.Add("$id source offer link is not at the shipped tag: $u") }
                    elseif ($NoticeText.IndexOf($u, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { $gaps.Add("$id source link is not in the notices: $u") }
                }
                if (-not (Test-Path -LiteralPath (Join-Path $Root ([string]$c.sourceOffer.textFile)) -PathType Leaf)) { $gaps.Add("$id written source offer file is missing") }
            }
        }
    }
    return @($gaps.ToArray())
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
    # Optional MSI payload gate: a GPL component without a same-tag source link, or with a missing hash/version in the notices, fails.
    $tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("redist-notice-selftest-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tmpRoot | Out-Null
    try {
        Set-Content -LiteralPath (Join-Path $tmpRoot "L.txt") -Value "licence" -Encoding ascii
        Set-Content -LiteralPath (Join-Path $tmpRoot "S.txt") -Value "offer" -Encoding ascii
        $sample = '{"components":[{"id":"x","displayName":"Foo Tool","version":"1.2.3","releaseTag":"v1.2.3","status":"included","asset":{"name":"foo.zip","sha256":"' + ("a" * 64) + '"},"license":{"spdx":"GPL-3.0-only","textFile":"L.txt"},"sourceOffer":{"sourceRepository":"https://example.invalid/src/v1.2.3","sourceArchive":"https://example.invalid/a/v1.2.3.tgz","textFile":"S.txt"}}]}'
        $good = "Foo Tool 1.2.3 GPL-3.0-only foo.zip " + ("a" * 64) + " https://example.invalid/src/v1.2.3 https://example.invalid/a/v1.2.3.tgz"
        if (@(Find-RedistNoticeGaps -LockJson $sample -NoticeText $good -Root $tmpRoot).Count -ne 0) { throw "Synthetic redist positive test failed." }
        if (@(Find-RedistNoticeGaps -LockJson $sample -NoticeText "Foo Tool 1.2.3 GPL-3.0-only foo.zip" -Root $tmpRoot).Count -lt 3) { throw "Synthetic redist missing-hash/source test failed." }
        $wrongTag = $sample.Replace("src/v1.2.3", "src/v0.0.1")
        if (@(Find-RedistNoticeGaps -LockJson $wrongTag -NoticeText $good -Root $tmpRoot).Count -lt 1) { throw "Synthetic redist wrong-tag test failed." }
    } finally { Remove-Item -LiteralPath $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue }
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

# 6) Optional vendor payloads of the MSI (packaging/redist-installers.lock.json): every included component needs its name, version,
#    SPDX licence and pinned SHA-256 in the notices, its licence text file in the repo, and (GPL) a same-tag source offer.
$redistLock = Join-Path $Root "packaging/redist-installers.lock.json"
$redistCount = 0
if (Test-Path -LiteralPath $redistLock -PathType Leaf) {
    $redistGaps = @(Find-RedistNoticeGaps -LockJson (Get-Content -LiteralPath $redistLock -Raw) -NoticeText $notice -Root $Root)
    if ($redistGaps.Count -gt 0) { throw "[notices] Optional MSI payload gaps: $($redistGaps -join '; ')" }
    $redistCount = @((Get-Content -LiteralPath $redistLock -Raw | ConvertFrom-Json).components).Count
}

Write-Host "[notices] PASS: $redistCount optional MSI payloads, $($packages.Count) direct PackageReference entries, $(@($transitive | Sort-Object -Unique).Count) transitive redistributed packages ($($assetFiles.Count) assets file(s)), $($script:NativeRules.Count) native rules, $nativeCount native DLLs scanned. This is not license clearance."
Write-Host "[notices] Run tools/validate-license-inventory.ps1 and scripts/generate-sbom.ps1 on Windows as separate gates."
