#requires -Version 7.0
<#
  Repeatable idle-CPU harness for PaRuX (UI) + PaRuX.Server (Core): CPU TIME deltas, never wall-clock.
  For each repetition: start PaRuX.exe with an isolated PARUX_DATA_ROOT, settle, then sum TotalProcessorTime deltas of the
  PaRuX and PaRuX.Server processes started by this run over -WindowSeconds, then close with PaRuX.exe --exit.
  Machine load is reported next to every number (system-wide busy% over the same window, plus the number of
  testhost/dotnet/msbuild processes seen at the start and the end) so a noisy run can be recognised, not hidden.
  The result is "percent of ONE core" = cpuSeconds / windowSeconds * 100 (wall-clock only converts seconds to a rate).
  Usage: Measure-AppIdleCpu.ps1 -AppDir <folder with PaRuX.exe> -ServerExe <PaRuX.Server.exe> -DataRoot <dir under .ai/> [-Label x] [-Repeats 3] [-WindowSeconds 120]
  Nothing outside -DataRoot is written (plus -OutJson when given). It never touches user data or artifacts/.
#>
param(
  [Parameter(Mandatory)][string]$AppDir,
  [Parameter(Mandatory)][string]$DataRoot,
  [string]$Label = 'run',
  [int]$Repeats = 3,
  [int]$WindowSeconds = 120,
  [int]$SettleSeconds = 45,
  [string]$ServerExe,
  [string]$OutJson
)
$ErrorActionPreference = 'Stop'
Add-Type -Namespace Native -Name Sys -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
'@
function Get-Busy { $i = 0L; $k = 0L; $u = 0L; [void][Native.Sys]::GetSystemTimes([ref]$i, [ref]$k, [ref]$u); [pscustomobject]@{ Idle = $i; Total = $k + $u } }
function Get-NoisyCount { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -in 'testhost', 'msbuild', 'vbcscompiler', 'MSBuild' }).Count }

$exe = Join-Path $AppDir 'PaRuX.exe'
if (-not (Test-Path $exe)) { throw "PaRuX.exe not found in $AppDir" }
$root = [IO.Path]::GetFullPath($DataRoot)
$existing = @(Get-Process -Name 'PaRuX', 'PaRuX.Server' -ErrorAction SilentlyContinue | ForEach-Object Id)
if ($existing.Count -gt 0) { Write-Warning "PaRuX processes already running (ids $($existing -join ',')); they are excluded from the measurement." }

$runs = @()
for ($r = 1; $r -le $Repeats; $r++) {
  $rootRun = Join-Path $root ("run$r")
  if (Test-Path $rootRun) { Remove-Item $rootRun -Recurse -Force }
  New-Item -ItemType Directory -Force $rootRun | Out-Null
  $psi = [Diagnostics.ProcessStartInfo]::new($exe)
  $psi.WorkingDirectory = $AppDir; $psi.UseShellExecute = $false
  $psi.Environment['PARUX_DATA_ROOT'] = $rootRun
  if ($ServerExe) { $psi.Environment['PARUX_SERVER_PATH'] = [IO.Path]::GetFullPath($ServerExe) }
  $app = [Diagnostics.Process]::Start($psi)
  try {
    Start-Sleep -Seconds $SettleSeconds
    $mine = @(Get-Process -Name 'PaRuX', 'PaRuX.Server' -ErrorAction SilentlyContinue | Where-Object { $_.Id -notin $existing })
    $noisyStart = Get-NoisyCount
    $b0 = Get-Busy
    $c0 = @{}; foreach ($p in $mine) { try { $c0[$p.Id] = $p.TotalProcessorTime.TotalSeconds } catch { } }
    Start-Sleep -Seconds $WindowSeconds
    $b1 = Get-Busy
    $per = [ordered]@{}; $sum = 0.0
    foreach ($p in $mine) {
      try { $p.Refresh(); $d = $p.TotalProcessorTime.TotalSeconds - $c0[$p.Id]; $per[$p.ProcessName] = [math]::Round(($per[$p.ProcessName] ?? 0) + $d, 3); $sum += $d } catch { }
    }
    $busy = 100.0 * (1 - ($b1.Idle - $b0.Idle) / [double]($b1.Total - $b0.Total))
    $runs += [pscustomobject]@{
      Run = $r; CpuSeconds = [math]::Round($sum, 3); PercentOfOneCore = [math]::Round($sum / $WindowSeconds * 100, 2)
      PerProcessSeconds = $per; Processes = ($mine | ForEach-Object ProcessName) -join ','
      MachineBusyPercent = [math]::Round($busy, 1); NoisyProcsStart = $noisyStart; NoisyProcsEnd = (Get-NoisyCount)
    }
    '{0} run {1}: {2:N2}% of one core ({3:N2} s CPU over {4} s) {5} | machine busy {6:N1}%, noisy procs {7}->{8}' -f $Label, $r, $runs[-1].PercentOfOneCore, $sum, $WindowSeconds, ($per | ConvertTo-Json -Compress), $busy, $noisyStart, $runs[-1].NoisyProcsEnd
  } finally {
    try { $psi2 = [Diagnostics.ProcessStartInfo]::new($exe, '--exit'); $psi2.UseShellExecute = $false; $psi2.Environment['PARUX_DATA_ROOT'] = $rootRun; if ($ServerExe) { $psi2.Environment['PARUX_SERVER_PATH'] = [IO.Path]::GetFullPath($ServerExe) }; [Diagnostics.Process]::Start($psi2).WaitForExit(15000) | Out-Null } catch { }
    Start-Sleep -Seconds 2
    foreach ($p in @(Get-Process -Name 'PaRuX', 'PaRuX.Server' -ErrorAction SilentlyContinue | Where-Object { $_.Id -notin $existing })) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  }
}
$pcts = $runs.PercentOfOneCore | Sort-Object
$summary = [pscustomobject]@{ Label = $Label; WindowSeconds = $WindowSeconds; Repeats = $Repeats; MedianPercent = $pcts[[int][math]::Floor(($pcts.Count - 1) / 2)]; MinPercent = $pcts[0]; MaxPercent = $pcts[-1]; Runs = $runs }
"{0}: median {1}% (min {2}, max {3}) of one core" -f $Label, $summary.MedianPercent, $summary.MinPercent, $summary.MaxPercent
if ($OutJson) { New-Item -ItemType Directory -Force (Split-Path -Parent ([IO.Path]::GetFullPath($OutJson))) | Out-Null; $summary | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 $OutJson }
