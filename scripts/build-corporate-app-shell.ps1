<#
.SYNOPSIS
    Stages the CPU-only application shell (launcher + app\ + Core) that scripts/build-corporate-lite.ps1 packages.

.DESCRIPTION
    INTERNAL building block of the Corporate Lite offline portable package; it is not a distribution.
    PaRuX ships exactly two distributions: the MSI (scripts/build-msi.ps1) and Corporate Lite (scripts/build-corporate-lite.ps1).
    The former Standard Portable ZIP is discontinued; its builder and verifier were removed.

    The shell carries no model, no inference runtime, no Vulkan baseline, no data folder and no edition marker: Corporate Lite adds its own
    CPU llama.cpp runtime, models, the parux.corporate marker, launchers and documentation on top of this stage.

    Stage layout (artifacts\corporate-lite\app-shell-stage\PaRuX; the layered layout shared with Corporate Lite):
      PaRuX.exe     small launcher (src\PaRuX.Launcher); starts app\PaRuX.exe, waits, returns its exit code
      app\          the application: PaRuX.exe, PaRuX.Server\, .NET/WinUI runtime, languages
      docs\         licences and third-party notices

    When PARUX_SIGN_PFX is set every first-party PE of the stage is Authenticode-signed. This script does nothing to bypass
    SmartScreen, Defender or IT policy.

.EXAMPLE
    ./scripts/build-corporate-app-shell.ps1                 # normally invoked by build-corporate-lite.ps1
    ./scripts/build-corporate-app-shell.ps1 -SkipPublish    # re-verify an already staged shell of the same commit
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot "portable-layout.ps1")

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) { throw "The PaRuX Corporate Lite shell must be built on Windows." }

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "src\PaRuX.App\PaRuX.App.csproj"
$coreProject = Join-Path $repoRoot "src\PaRuX.Server\PaRuX.Server.csproj"
$shellRoot = Join-Path $repoRoot "artifacts\corporate-lite\app-shell-stage"
$stageDir = Join-Path $shellRoot "PaRuX"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw
    $Version = [string]($projectXml.Project.PropertyGroup | ForEach-Object { $_.PaRuXProductVersion } | Where-Object { $_ } | Select-Object -First 1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be major.minor.patch. Got '$Version'." }

$commitLines = @(& git -C $repoRoot rev-parse HEAD 2>$null)
$gitExit = $LASTEXITCODE
$sourceCommit = if ($gitExit -eq 0 -and $commitLines.Count -gt 0) { ([string]$commitLines[0]).Trim().ToLowerInvariant() } else { '' }
if ($sourceCommit -notmatch '^[0-9a-f]{40}$') { throw "Could not resolve the Git source commit." }
$dirty = @(& git -C $repoRoot status --porcelain=v1 2>$null)
$dirtyNote = if ($dirty.Count -gt 0) { " (working tree has uncommitted changes: this is a local build, not a release artifact)" } else { "" }
Write-Host "Corporate Lite CPU shell $Version from $sourceCommit$dirtyNote"

# The package root ($stageDir) holds only the launcher and folders; the application lives in app\.
$appDir = Join-Path $stageDir "app"
$coreDir = Join-Path $appDir "PaRuX.Server"

if (-not $SkipPublish) {
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
    $fourPart = "$Version.0"
    $common = @("-p:Version=$Version", "-p:FileVersion=$fourPart", "-p:AssemblyVersion=$fourPart", "-p:InformationalVersion=$Version",
        "-p:SourceRevisionId=$sourceCommit", "-p:DebugSymbols=false", "-p:DebugType=None")

    Write-Host "Publishing PaRuX Desktop..."
    & dotnet publish $appProject -c Release -r win-x64 --self-contained true -o $appDir $common
    if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed ($LASTEXITCODE)." }
    Write-Host "Publishing PaRuX Server (Core)..."
    & dotnet publish $coreProject -c Release -r win-x64 --self-contained true -o $coreDir $common
    if ($LASTEXITCODE -ne 0) { throw "Server publish failed ($LASTEXITCODE)." }
    # The ONLY file at the package root: a small launcher that starts app\PaRuX.exe.
    Write-Host "Publishing PaRuX launcher (root PaRuX.exe)..."
    Publish-PaRuXLauncher -RepoRoot $repoRoot -Destination $stageDir -CommonArgs $common
}
if (-not (Test-Path -LiteralPath (Join-Path $appDir "PaRuX.exe")) -or
    -not (Test-Path -LiteralPath (Join-Path $stageDir "PaRuX.exe"))) { throw "Nothing staged at $stageDir (app\PaRuX.exe and the launcher PaRuX.exe). Run without -SkipPublish first." }
# A stale -SkipPublish must not stamp an old compiled Desktop/Core with the CURRENT source commit.
# The marker content is read by build-corporate-lite.ps1 (commit + "cpu-shell").
$provenancePath = Join-Path $appDir ".parux-publish-source"
$expectedProvenance = $sourceCommit + "|cpu-shell"
if ($SkipPublish) {
    if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf) -or
        (Get-Content -LiteralPath $provenancePath -Raw).Trim() -cne $expectedProvenance) {
        throw "-SkipPublish refused: staged source commit/edition differs. Build a fresh publish first."
    }
} else {
    Set-Content -LiteralPath $provenancePath -Value $expectedProvenance -Encoding ASCII
}

$docsDir = Join-Path $stageDir "docs"
New-Item -ItemType Directory -Force -Path $docsDir | Out-Null
foreach ($notice in @("LICENSE:LICENSE.txt", "NVIDIA_CUDA_NOTICE.txt:NVIDIA_CUDA_NOTICE.txt", "THIRD_PARTY_NOTICES.md:THIRD_PARTY_NOTICES.md", "OPEN_SOURCE_PROVENANCE.md:OPEN_SOURCE_PROVENANCE.md")) {
    $src, $dst = $notice.Split(':')
    if (Test-Path -LiteralPath (Join-Path $repoRoot $src)) { Copy-Item -LiteralPath (Join-Path $repoRoot $src) -Destination (Join-Path $docsDir $dst) -Force }
}
# llama.cpp is MIT: its copyright/permission text travels with the package docs.
Copy-Item -LiteralPath (Join-Path $repoRoot "llama-cpp-conversion\LICENSE-llama.cpp") -Destination (Join-Path $docsDir "LICENSE-llama.cpp.txt") -Force

# Authenticode: every first-party PE (PaRuX*.exe/.dll, parux.exe) is signed so Corporate Lite's file manifest describes the signed
# bytes. Third-party/runtime binaries are not touched. Without PARUX_SIGN_PFX the stage is unsigned (local/test builds).
if (-not [string]::IsNullOrWhiteSpace($env:PARUX_SIGN_PFX)) {
    & (Join-Path $PSScriptRoot "sign-first-party.ps1") -Root $stageDir
    if (-not $?) { throw "First-party Authenticode signing of the Corporate Lite shell failed." }
} else {
    Write-Warning "PARUX_SIGN_PFX is not set: the shell's first-party binaries are UNSIGNED."
}

# Verify the stage: only the launcher plus app/ and docs/, nothing edition-specific.
$stageFull = (Resolve-Path -LiteralPath $stageDir).Path.TrimEnd('\')
$names = @(Get-ChildItem -LiteralPath $stageFull -Recurse -File | ForEach-Object { $_.FullName.Substring($stageFull.Length + 1).Replace('\', '/') })
Assert-PaRuXPackageLayout -Names $names -Label "Corporate Lite shell"
$required = @("PaRuX.exe", "app/PaRuX.exe", "app/PaRuX.pri", "app/PaRuX.Server/PaRuX.Server.exe", "app/PaRuX.Server/PaRuX.Server.dll",
    "docs/LICENSE.txt", "app/hostfxr.dll", "app/hostpolicy.dll", "app/coreclr.dll",
    "app/PaRuX.Server/hostfxr.dll", "app/PaRuX.Server/hostpolicy.dll", "app/PaRuX.Server/coreclr.dll")
foreach ($file in $required) { if ($names -notcontains $file) { throw "Corporate Lite shell is missing required file: $file" } }
if (@($names | Where-Object { $_ -like "app/PaRuX.Server/runtime-baseline/*" -or $_ -like "app/Cli/*" -or $_ -like "PaRuXData/*" -or $_ -eq "app/parux.portable" }).Count -gt 0) {
    throw "The CPU-only shell unexpectedly contains a runtime baseline, the CLI, a data folder or an edition marker."
}
Write-Host "Shell check OK: $($names.Count) files, stage $stageDir"
Write-Host "Done."
