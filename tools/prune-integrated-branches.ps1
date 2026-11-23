# Prune ONLY known superseded PaRuX remote branches after the November mainline squash.
# Default is audit/dry-run. -Apply requires an authenticated Git origin with push permission.
# Never deletes main, active WIP worktree-agent-* branches, or remote/* feature checkpoints.
[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$integrationCommit = '2e2d630ff1e52f8cb7281f215a7d45e47b2de750'
$candidates = @(
    'integration/20261123-b'
    'fix/corporate-cpu-memory-diagnostics-20261116'
    'feat/gaming-owned-post-launch-actions-20261116'
    'fix/playnite-safe-switch-preflight-20261123'
    'feat/pad-playnite-actions-20261123'
    'feat/json-config-ai-mod-previews-20261123'
    'feat/playnite-first-games-tools-20261123'
    'feat/ai-gameplay-batch-mods-20261123'
    'fix/blf-bounded-alignment-20261123'
    'fix/integration-routing-regressions-20261123'
    'fix/sandbox-required-acl-diagnostics-20261123'
    'codex/gaming-library-20261119'
)
function Check-Exit([string]$message) {
    if ($LASTEXITCODE -ne 0) { throw "$message (git exit $LASTEXITCODE)" }
}
Push-Location $root
try {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw 'Git is required to audit/clean branches.'
    }
    $origin = (& git remote get-url origin).Trim()
    Check-Exit 'Cannot read origin'
    if ($origin -notmatch '(^|[/:])github\.com[:/]Ruba94-cyber/RBG-PaRuX-AI-Gaming-OS(\.git)?$') {
        throw "Refusing to act on an unexpected origin: $origin"
    }
    & git fetch origin main --quiet
    Check-Exit 'Cannot fetch main'
    & git merge-base --is-ancestor $integrationCommit 'refs/remotes/origin/main'
    Check-Exit 'Main does not contain the verified source-consolidation commit; aborting'

    Write-Host "[PaRuX cleanup] Mode: $(if ($Apply) { 'DELETE integrated branches' } else { 'DRY RUN' })"
    Write-Host '[PaRuX cleanup] WIP worktree-agent-*, remote/* and all other unlisted refs are preserved.'
    foreach ($branch in $candidates) {
        if ($branch -match '^(main|worktree-agent-|remote/)') {
            throw "Unsafe entry in branch whitelist: $branch"
        }
        $remoteRef = 'refs/heads/' + $branch
        $found = @(& git ls-remote --heads origin $remoteRef)
        Check-Exit "Cannot inspect $branch"
        if ($found.Count -eq 0) {
            Write-Host "[PaRuX cleanup] Already absent: $branch"
            continue
        }
        if (-not $Apply) {
            Write-Host "[PaRuX cleanup] Would delete: $branch"
            continue
        }
        & git push origin --delete $branch
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Git rejected branch deletion: $branch. Other branches will still be checked."
        } else {
            Write-Host "[PaRuX cleanup] Deleted: $branch" -ForegroundColor Green
        }
    }
    if (-not $Apply) {
        Write-Host '[PaRuX cleanup] No refs were changed. Rerun with -Apply only after reviewing the list.'
    }
}
finally {
    Pop-Location
}
