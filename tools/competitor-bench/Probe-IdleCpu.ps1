#requires -Version 5.1
<# Idle CPU of one llama-server launch: start, one short chat request, settle, then CPU seconds over a window.
   Usage: Probe-IdleCpu.ps1 -Server <exe> -Model <gguf> -ArgLine "<flags>" [-Label x]. Always stops what it starts. #>
param([Parameter(Mandatory)][string]$Server, [Parameter(Mandatory)][string]$Model, [Parameter(Mandatory)][string]$ArgLine,
  [string]$Label = 'idle', [int]$Port = 18003, [int]$SettleSeconds = 20, [int]$WindowSeconds = 20)
$psi = [Diagnostics.ProcessStartInfo]::new($Server)
$psi.Arguments = "--model `"$Model`" --host 127.0.0.1 --port $Port $ArgLine"
$psi.UseShellExecute = $false; $psi.RedirectStandardError = $true; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
$p = [Diagnostics.Process]::Start($psi); $null = $p.StandardError.ReadToEndAsync(); $null = $p.StandardOutput.ReadToEndAsync()
try {
  $ok = $false
  for ($i = 0; $i -lt 400 -and -not $ok; $i++) { try { $ok = (Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2).status -eq 'ok' } catch { Start-Sleep -Milliseconds 100 } }
  Invoke-RestMethod "http://127.0.0.1:$Port/v1/chat/completions" -Method Post -ContentType 'application/json' -Body '{"messages":[{"role":"user","content":"Say hi."}],"max_tokens":16}' | Out-Null
  Start-Sleep -Seconds $SettleSeconds
  $c0 = (Get-Process -Id $p.Id).TotalProcessorTime.TotalSeconds; Start-Sleep -Seconds $WindowSeconds
  $c1 = (Get-Process -Id $p.Id).TotalProcessorTime.TotalSeconds
  "{0}: idle CPU {1:N2}% of one core ({2:N2}s over {3}s)" -f $Label, (($c1 - $c0) / $WindowSeconds * 100), ($c1 - $c0), $WindowSeconds
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 2 }
