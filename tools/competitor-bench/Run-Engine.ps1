#requires -Version 5.1
<#
Engine-level A/B: starts one llama-server build with one flag set on a read-only GGUF, measures
model-load time (N cold starts, health=ok), then runs bench.py (short/1k/4k, 1 warm-up excluded)
and records engine VRAM (nvidia-smi delta over the idle baseline) and engine working set.
Always stops the engine it started. Usage:
  Run-Engine.ps1 -Label x -Server <llama-server.exe> -Model <gguf> -ArgLine "<flags>" -OutDir <dir>
#>
param(
  [Parameter(Mandatory)][string]$Label,
  [Parameter(Mandatory)][string]$Server,
  [Parameter(Mandatory)][string]$Model,
  [Parameter(Mandatory)][string]$ArgLine,
  [Parameter(Mandatory)][string]$OutDir,
  [int]$Port = 18002, [int]$Runs = 5, [int]$LoadReps = 3
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
function Start-Engine {
  $psi = [Diagnostics.ProcessStartInfo]::new($Server)
  $psi.Arguments = "--model `"$Model`" --host 127.0.0.1 --port $Port $ArgLine"
  $psi.UseShellExecute = $false; $psi.RedirectStandardError = $true; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
  $sw = [Diagnostics.Stopwatch]::StartNew(); $p = [Diagnostics.Process]::Start($psi)
  $null = $p.StandardError.ReadToEndAsync(); $null = $p.StandardOutput.ReadToEndAsync()
  $ready = $null
  while ($sw.Elapsed.TotalSeconds -lt 120) {
    try { $h = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2; if ($h.status -eq 'ok') { $ready = $sw.ElapsedMilliseconds; break } } catch {}
    Start-Sleep -Milliseconds 50
  }
  [pscustomobject]@{ P = $p; Ready = $ready }
}
function Used-Mib { [int](& nvidia-smi '--query-gpu=memory.used' '--format=csv,noheader,nounits' | Select-Object -First 1) }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$base = Used-Mib
$loads = @()
$e = $null
try {
  for ($i = 0; $i -lt $LoadReps; $i++) {
    $e = Start-Engine; $loads += $e.Ready
    Stop-Process -Id $e.P.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 3
  }
  $e = Start-Engine
  if ($e.Ready) {
    python -I "$here\bench.py" --label $Label --url "http://127.0.0.1:$Port/v1/chat/completions" --runs $Runs --out "$OutDir\ai-$Label.json" 2>&1 | Out-File "$OutDir\ai-$Label.log" -Encoding utf8
    # 3 concurrent generation-heavy requests vs 1 (server-counted tokens): shows whether the engine batches slots
    python -I "$here\parallel.py" --label "$Label-par3" --n 3 --length gen --max-tokens 256 --url "http://127.0.0.1:$Port/v1/chat/completions" --out "$OutDir\par3-$Label.json" 2>&1 | Out-File "$OutDir\par3-$Label.log" -Encoding utf8
    python -I "$here\parallel.py" --label "$Label-par1" --n 1 --length gen --max-tokens 256 --url "http://127.0.0.1:$Port/v1/chat/completions" --out "$OutDir\par1-$Label.json" 2>&1 | Out-File "$OutDir\par1-$Label.log" -Encoding utf8
    $used = Used-Mib
    $ws = [math]::Round((Get-Process -Id $e.P.Id).WorkingSet64 / 1MB)
    [ordered]@{ Label = $Label; Server = $Server; Args = $ArgLine; LoadMs = $loads; BaselineMiB = $base; UsedMiB = $used; EngineVramMiB = ($used - $base); EngineWsMB = $ws } |
      ConvertTo-Json | Set-Content "$OutDir\ai-$Label.meta.json"
    "$Label load(ms)=$($loads -join ',') vramMiB=$($used - $base)"
  } else { "$Label FAILED to start" }
} finally {
  if ($e -and $e.P -and -not $e.P.HasExited) { Stop-Process -Id $e.P.Id -Force -ErrorAction SilentlyContinue }
  Start-Sleep 2
}
