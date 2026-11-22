#requires -Version 5.1
<#
Headless run of the Core (PaRuX.Server) of the build under test against an ISOLATED copy of a data root:
 1. N cold Core starts -> time until providerReady after the model load call (Core start + model load through /api/v1/models/library/<file>/load);
 2. one more start: capture the llama-server command line PaRuX really used, VRAM, bench.py through the Core chat API,
    idle CPU/RAM of the Core tree (model loaded, UI process excluded), then the RAG run (parux_rag.py);
 3. stop and verify nothing from that Core/data root is left.
Only processes whose executable lives under -CoreDir or -DataRoot are ever touched.
#>
param(
  [Parameter(Mandatory)][string]$CoreDir,
  [Parameter(Mandatory)][string]$DataRoot,
  [Parameter(Mandatory)][string]$OutDir,
  [Parameter(Mandatory)][string]$Corpus,
  [string]$Label = 'parux-core',
  [string]$ModelFile = 'Qwen3-4B-Q4_K_M.gguf',
  [int]$StartReps = 3, [int]$Runs = 5, [int]$SettleSeconds = 60, [int]$WindowSeconds = 30
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $CoreDir 'PaRuX.Server.exe'
$boot = Join-Path $DataRoot 'server\bootstrap.json'
$env:PARUX_DATA_ROOT = $DataRoot
New-Item -ItemType Directory -Force $OutDir | Out-Null
function Get-Mine { Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and ($_.ExecutablePath.StartsWith($CoreDir, [StringComparison]::OrdinalIgnoreCase) -or $_.ExecutablePath.StartsWith($DataRoot, [StringComparison]::OrdinalIgnoreCase)) } }
function Stop-Mine {
  $t = [Diagnostics.Stopwatch]::StartNew()
  foreach ($p in @(Get-Mine)) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
  while ($t.Elapsed.TotalSeconds -lt 20 -and (Get-Mine)) { Start-Sleep -Milliseconds 200 }
  [pscustomobject]@{ StopSeconds = [math]::Round($t.Elapsed.TotalSeconds, 2); Left = @(Get-Mine).Count }
}
function Hdr($b) { @{ Authorization = "Bearer $($b.token)" } }
function Start-Core {
  Remove-Item $boot -ErrorAction SilentlyContinue
  $sw = [Diagnostics.Stopwatch]::StartNew(); $null = Start-Process $exe -WindowStyle Hidden
  $ready = $null; $b = $null; $warm = $false
  while ($sw.Elapsed.TotalSeconds -lt 180) {
    try {
      $b = Get-Content $boot -Raw | ConvertFrom-Json
      $h = Invoke-RestMethod "$($b.endpoint)/api/v1/health" -Headers (Hdr $b) -TimeoutSec 3
      if (-not $warm) { $warm = $true; Invoke-RestMethod "$($b.endpoint)/api/v1/models/library/$ModelFile/load" -Method Post -Headers (Hdr $b) -ContentType 'application/json' -Body '{"role":"standard"}' -TimeoutSec 180 | Out-Null }
      if ($h.providerReady) { $ready = $sw.ElapsedMilliseconds; break }
    } catch {}
    Start-Sleep -Milliseconds 100
  }
  [pscustomobject]@{ ReadyMs = $ready; Boot = $b }
}
function Procs { @(Get-Mine | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } | Where-Object { $_ }) }
if (Get-Mine) { throw 'A process from this Core/data root is already running; refusing to touch it.' }
$base = [int](& nvidia-smi '--query-gpu=memory.used' '--format=csv,noheader,nounits' | Select-Object -First 1)
$starts = @(); $stops = @()
for ($i = 0; $i -lt $StartReps; $i++) { $s = Start-Core; $starts += $s.ReadyMs; $stops += (Stop-Mine); Start-Sleep 3 }
$s = Start-Core
if (-not $s.ReadyMs) { Stop-Mine | Out-Null; throw 'providerReady never reached' }
$tokFile = Join-Path $OutDir "$Label.tok"; Set-Content $tokFile $s.Boot.token -NoNewline
try {
  $cmd = (Get-CimInstance Win32_Process -Filter "Name='llama-server.exe'" | Where-Object { $_.ExecutablePath.StartsWith($DataRoot, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1).CommandLine
  Set-Content (Join-Path $OutDir "$Label.cmdline.txt") ($cmd -replace '--ui-config-file \S+', '')
  $vram = [int](& nvidia-smi '--query-gpu=memory.used' '--format=csv,noheader,nounits' | Select-Object -First 1)
  python -I "$here\bench.py" --label $Label --url "$($s.Boot.endpoint)/api/v1/chat/completions" --token-file $tokFile --runs $Runs --out "$OutDir\ai-$Label.json" 2>&1 | Out-File "$OutDir\ai-$Label.log" -Encoding utf8
  $vramAfter = [int](& nvidia-smi '--query-gpu=memory.used' '--format=csv,noheader,nounits' | Select-Object -First 1)
  Start-Sleep -Seconds $SettleSeconds
  $p0 = Procs; $per0 = @{}; foreach ($q in $p0) { $per0[$q.Name] = $q.TotalProcessorTime.TotalSeconds }; $c0 = ($p0 | ForEach-Object { $_.TotalProcessorTime.TotalSeconds } | Measure-Object -Sum).Sum; $t0 = Get-Date
  Start-Sleep -Seconds $WindowSeconds
  $p1 = Procs; $c1 = ($p1 | ForEach-Object { $_.TotalProcessorTime.TotalSeconds } | Measure-Object -Sum).Sum; $el = ((Get-Date) - $t0).TotalSeconds
  $idle = [ordered]@{ IdleCpuPctOfOneCore = [math]::Round(($c1 - $c0) / $el * 100, 2); WorkingSetMB = [math]::Round(($p1 | Measure-Object WorkingSet64 -Sum).Sum / 1MB); ProcessCount = $p1.Count
    ByName = @($p1 | Group-Object Name | ForEach-Object { [ordered]@{ Name = $_.Name; Count = $_.Count; CpuPctOfOneCore = [math]::Round(((($_.Group | Measure-Object { $_.TotalProcessorTime.TotalSeconds } -Sum).Sum) - $per0[$_.Name]) / $el * 100, 2); WorkingSetMB = [math]::Round(($_.Group | Measure-Object WorkingSet64 -Sum).Sum / 1MB) } }) }
  python -I "$here\parux_rag.py" --endpoint $s.Boot.endpoint --token-file $tokFile --corpus $Corpus --out "$OutDir\rag-$Label.json" 2>&1 | Out-File "$OutDir\rag-$Label.log" -Encoding utf8
} finally { $stop = Stop-Mine; Remove-Item $tokFile -ErrorAction SilentlyContinue }
[ordered]@{ Label = $Label; CoreDir = $CoreDir; CoreReadyMs = $starts; Stops = $stops; LastStop = $stop; BaselineMiB = $base; VramLoadedMiB = ($vram - $base); VramAfterBenchMiB = ($vramAfter - $base); Idle = $idle; CommandLine = $cmd } |
  ConvertTo-Json -Depth 6 | Set-Content "$OutDir\app-$Label.json"
"$Label ready(ms)=$($starts -join ',') vramMiB=$($vram - $base) idleCpu%=$($idle.IdleCpuPctOfOneCore) ws=$($idle.WorkingSetMB)MB"
