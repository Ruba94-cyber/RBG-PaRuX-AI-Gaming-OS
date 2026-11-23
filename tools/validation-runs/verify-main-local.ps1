# Local-only integrated PaRuX verification. No GitHub Actions, release, signing, or user-data cleanup.
# Example: powershell -ExecutionPolicy Bypass -File tools/validation-runs/verify-main-local.ps1 -FullSuite -SandboxSmoke
[CmdletBinding()]
param(
    [switch]$FullSuite,
    [switch]$SandboxSmoke,
    [switch]$UiSmoke,
    [string]$ResultsDirectory = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'PaRuX native acceptance requires Windows x64. This script does not simulate a Windows run.'
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'PaRuX acceptance requires a 64-bit Windows host.'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workflowRoot = Join-Path $root '.github\workflows'
if (Test-Path $workflowRoot) {
    $workflows = @(Get-ChildItem $workflowRoot -File -Recurse -ErrorAction SilentlyContinue)
    if ($workflows.Count -gt 0) { throw 'GitHub Actions workflow files are prohibited by PaRuX policy.' }
}

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $root 'artifacts\local-validation'
}
$ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$dotnet = $dotnetCommand.Source
$serverTests = Join-Path $root 'tests\PaRuX.Server.Tests\PaRuX.Server.Tests.csproj'
$appProject = Join-Path $root 'src\PaRuX.App\PaRuX.App.csproj'
$uiTests = Join-Path $root 'tests\PaRuX.UITests\PaRuX.UITests.csproj'

function Invoke-Checked([string]$Name, [string[]]$Arguments) {
    Write-Host "[PaRuX local validation] $Name" -ForegroundColor Cyan
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed (exit code $LASTEXITCODE). Keep logs in $ResultsDirectory for diagnosis."
    }
}

Push-Location $root
try {
    Invoke-Checked 'Check pinned .NET SDK' @('--version')
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $head = (& git rev-parse --short HEAD).Trim()
        Write-Host "[PaRuX local validation] Source commit: $head"
    }
    Invoke-Checked 'Restore server tests' @('restore', $serverTests, '-p:Platform=x64', '--nologo')
    Invoke-Checked 'Build server test graph (Release x64)' @('build', $serverTests, '-c', 'Release', '-p:Platform=x64', '--no-restore', '--nologo')
    Invoke-Checked 'Build WinUI desktop (Release x64)' @('build', $appProject, '-c', 'Release', '-p:Platform=x64', '--nologo')

    $filter = 'FullyQualifiedName~CanTraceExtractorTests|FullyQualifiedName~MediaCapabilityRoutingTests|FullyQualifiedName~ModelDownloadServiceTests|FullyQualifiedName~GameModDropInServiceTests|FullyQualifiedName~WebSessionRouteDecisionTests|FullyQualifiedName~PlayniteComponentTests|FullyQualifiedName~ConsoleModeReadinessTests|FullyQualifiedName~PlayniteTheme|FullyQualifiedName~TemporaryGameModServiceTests|FullyQualifiedName~MultilingualEmbeddingTests|FullyQualifiedName~CorporateCpuMemoryPlannerTests'
    Invoke-Checked 'Focused integration regressions' @(
        'test', $serverTests, '-c', 'Release', '-p:Platform=x64', '--no-build', '--no-restore',
        '--filter', $filter, '--blame-hang', '--blame-hang-timeout', '4m',
        '--logger', 'trx;LogFileName=integration-focused.trx', '--results-directory', $ResultsDirectory
    )

    if ($SandboxSmoke) {
        $sandboxFilter = 'FullyQualifiedName~WindowsCodingSandboxAclTests.RunAsync_InDirectory_CanRunEchoInsideAppContainer|FullyQualifiedName~WindowsCodingSandboxIntegrationTests.Real_no_network_appcontainer_executes_in_workspace_and_scrubs_parent_secret|FullyQualifiedName~WindowsCodingSandboxIntegrationTests.Npm_test_runs_the_package_script_inside_the_offline_sandbox'
        Invoke-Checked 'Real Windows AppContainer smoke (security tests are not skipped)' @(
            'test', $serverTests, '-c', 'Release', '-p:Platform=x64', '--no-build', '--no-restore',
            '--filter', $sandboxFilter, '--blame-hang', '--blame-hang-timeout', '4m',
            '--logger', 'trx;LogFileName=appcontainer-smoke.trx', '--results-directory', $ResultsDirectory
        )
    }

    if ($FullSuite) {
        Invoke-Checked 'Full server xUnit suite' @(
            'test', $serverTests, '-c', 'Release', '-p:Platform=x64', '--no-build', '--no-restore',
            '--blame-hang', '--blame-hang-timeout', '4m',
            '--logger', 'trx;LogFileName=server-full.trx', '--results-directory', $ResultsDirectory
        )
    }

    if ($UiSmoke) {
        if (-not (Test-Path $uiTests)) { throw 'WinUI UI test project is missing.' }
        Write-Host '[PaRuX local validation] FlaUI tests require an interactive Windows desktop and may launch the application.'
        Invoke-Checked 'Restore WinUI UI tests' @('restore', $uiTests, '-p:Platform=x64', '--nologo')
        Invoke-Checked 'WinUI UI smoke tests (interactive desktop only)' @(
            'test', $uiTests, '-c', 'Release', '-p:Platform=x64', '--no-restore',
            '--logger', 'trx;LogFileName=ui-smoke.trx', '--results-directory', $ResultsDirectory
        )
    }

    Write-Host "[PaRuX local validation] Requested builds/tests completed successfully. Results: $ResultsDirectory" -ForegroundColor Green
    Write-Host '[PaRuX local validation] NOT COVERED: physical gamepad, actual Playnite/game processes, HDR/eGPU and package signing.'
}
finally {
    Pop-Location
}
