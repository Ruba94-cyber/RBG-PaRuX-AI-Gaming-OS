<#
.SYNOPSIS
    Shared layout rules of the layered portable packages (Standard Portable, Corporate Lite). Dot-source it.

.DESCRIPTION
    Every portable ZIP has ONE top-level folder (PaRuX\ or PaRuX-Corporate-Lite\) and, inside it:

        PaRuX.exe        small launcher: the only file at the package root
        app\             the real application (app\PaRuX.exe, app\PaRuX.Server\, runtimes, languages, markers)
        PaRuXData\       all durable data (models, settings, history, indexes), beside the launcher
        docs\            README, licences, third-party notices, manifest
        launchers\       optional .cmd entry points (Corporate: offline / CPU only / model downloads)

    The markers parux.portable / parux.corporate live in app\ (no loose file at the root); the data
    root detection (PaRuXDataRoot) finds PaRuXData beside app\. MSI and MSIX keep their own layout and
    never use this file.
#>
Set-StrictMode -Version Latest

$script:PaRuXLauncherFileName = "PaRuX.exe"
$script:PaRuXPackageRootDirectories = @("app", "PaRuXData", "docs", "launchers")

<#
    Returns the problems of a package listing (entry names relative to the package root, '/' separators).
    Empty result = the layout is valid: the root holds PaRuX.exe plus only the allowed folders, and the
    real application exists in app\.
#>
function Get-PaRuXPackageLayoutViolations {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]]$Names)

    $problems = New-Object System.Collections.Generic.List[string]
    $hasLauncher = $false
    $hasApp = $false
    foreach ($name in $Names) {
        if ([string]::IsNullOrWhiteSpace($name)) { $problems.Add("empty entry name"); continue }
        $slash = $name.IndexOf('/')
        if ($slash -lt 0) {
            if ($name -ceq $script:PaRuXLauncherFileName) { $hasLauncher = $true }
            else { $problems.Add("unexpected file at the package root: $name") }
            continue
        }
        $top = $name.Substring(0, $slash)
        if ($script:PaRuXPackageRootDirectories -cnotcontains $top) {
            $problems.Add("unexpected folder at the package root: $top/")
            continue
        }
        if ($name -ceq "app/PaRuX.exe") { $hasApp = $true }
    }
    if (-not $hasLauncher) { $problems.Add("the launcher PaRuX.exe is missing from the package root") }
    if (-not $hasApp) { $problems.Add("the application app/PaRuX.exe is missing") }
    $problems.ToArray() | Select-Object -Unique
}

function Assert-PaRuXPackageLayout {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]]$Names, [string]$Label = "Portable package")
    $problems = @(Get-PaRuXPackageLayoutViolations -Names $Names)
    if ($problems.Count -gt 0) {
        throw "$Label violates the layered layout (PaRuX.exe + app/, PaRuXData/, docs/, launchers/ only): " + ($problems -join "; ")
    }
}

<#
    Publishes src\PaRuX.Launcher (self-contained, single file, trimmed, win-x64) and copies ONLY PaRuX.exe to
    $Destination. Nothing else (no pdb, no json, no dll) may reach the package root.
#>
function Publish-PaRuXLauncher {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string]$RepoRoot,
        [Parameter(Mandatory = $true)] [string]$Destination,
        [string[]]$CommonArgs = @()
    )
    $project = Join-Path $RepoRoot "src\PaRuX.Launcher\PaRuX.Launcher.csproj"
    $out = Join-Path $RepoRoot "artifacts\portable\launcher-publish"
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $out $CommonArgs
    if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed ($LASTEXITCODE)." }
    $built = Join-Path $out $script:PaRuXLauncherFileName
    if (-not (Test-Path -LiteralPath $built -PathType Leaf) -or (Get-Item -LiteralPath $built).Length -lt 100000) {
        throw "The published launcher is missing or implausibly small: $built"
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -LiteralPath $built -Destination (Join-Path $Destination $script:PaRuXLauncherFileName) -Force
}

<#
    Processes of an extracted package, by path prefix: the launcher (root\PaRuX.exe), the application
    (root\app\PaRuX.exe) and the Core (root\app\PaRuX.Server\PaRuX.Server.exe) all start with the package root.
    Smoke tests use it to clean up only their own tree.
#>
function Get-PaRuXPackageProcessesUnder {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] [string]$Root)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    @(Get-Process -Name PaRuX, PaRuX.Server -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
}
