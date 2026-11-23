param([Parameter(Mandatory)][string]$ZipPath, [string]$WorkRoot = (Join-Path $env:TEMP "parux-idle-run"), [int]$IdleMinutes = 8)
$ErrorActionPreference = "Stop"
Get-Process | Where-Object { $_.Name -match '^PaRuX|llama-server' } | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
if (Test-Path $WorkRoot) { Remove-Item $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force "$WorkRoot\profile\Local", "$WorkRoot\profile\Roaming", "$WorkRoot\profile\Temp" | Out-Null
Expand-Archive -LiteralPath $ZipPath -DestinationPath "$WorkRoot\pkg"
$app = Join-Path "$WorkRoot\pkg" "PaRuX-Corporate-Lite"; $prof = "$WorkRoot\profile"; $sys = $env:SystemRoot
$psi = New-Object Diagnostics.ProcessStartInfo; $psi.FileName = Join-Path $app "PaRuX.exe"; $psi.WorkingDirectory = $app; $psi.UseShellExecute = $false
$psi.EnvironmentVariables.Clear()
$vars = @{ SystemRoot = $sys; windir = $sys; SystemDrive = $env:SystemDrive; PATH = "$sys\System32;$sys"; USERNAME = $env:USERNAME; COMPUTERNAME = $env:COMPUTERNAME; USERPROFILE = $prof
    LOCALAPPDATA = "$prof\Local"; APPDATA = "$prof\Roaming"; TEMP = "$prof\Temp"; TMP = "$prof\Temp"; ProgramFiles = $env:ProgramFiles; ProgramData = $env:ProgramData
    NUMBER_OF_PROCESSORS = $env:NUMBER_OF_PROCESSORS; PARUX_CORPORATE = "1"; PARUX_CORPORATE_MODEL_DOWNLOADS = "0"; PARUX_DATA_ROOT = "$app\PaRuXData"; PARUX_LOCALAI_STATE_DIR = "$app\PaRuXData" }
foreach ($k in $vars.Keys) { $psi.EnvironmentVariables[$k] = [string]$vars[$k] }
$null = [Diagnostics.Process]::Start($psi)
$boot = "$app\PaRuXData\server\bootstrap.json"
for ($i = 0; $i -lt 180 -and -not (Test-Path $boot); $i++) { Start-Sleep 1 }
$b = Get-Content $boot -Raw | ConvertFrom-Json; $ep = $b.endpoint.TrimEnd('/'); $h = @{ "X-PaRuX-Token" = $b.token }
function Mem { $r = [ordered]@{}; foreach ($n in 'llama-server', 'PaRuX.Server', 'PaRuX') { $p = Get-Process $n -ErrorAction SilentlyContinue; $r[$n] = if ($p) { [int](($p | Measure-Object WorkingSet64 -Sum).Sum / 1MB) } else { 0 } }; $r }
function Show($label) { $m = Mem; "{0,-34} llama-server {1,5} MB | Core {2,4} MB | UI {3,4} MB | totale {4,5} MB | {5:HH:mm:ss}" -f $label, $m['llama-server'], $m['PaRuX.Server'], $m['PaRuX'], ($m.Values | Measure-Object -Sum).Sum, (Get-Date) }
Show "dopo avvio (nessuna richiesta)"
# one plain chat turn (no documents) so the text engine has to load
$body = @{ model = "local-default"; stream = $false; max_tokens = 20; messages = @(@{ role = "user"; content = "Say hello in one short sentence." }) } | ConvertTo-Json -Depth 5 -Compress
$sw = [Diagnostics.Stopwatch]::StartNew()
try { $r = Invoke-RestMethod "$ep/api/v1/chat/completions" -Method Post -Headers $h -ContentType "application/json" -Body $body -TimeoutSec 300; "risposta in {0:N1}s: {1}" -f $sw.Elapsed.TotalSeconds, ($r.choices[0].message.content -replace '\s+', ' ') } catch { "ERRORE chat: " + $_.Exception.Message }
Show "subito dopo la chat"
$end = (Get-Date).AddMinutes($IdleMinutes); $n = 0
while ((Get-Date) -lt $end) { Start-Sleep 60; $n++; Show ("inattivo da $n min") }
foreach ($u in "/api/v1/embeddings/engine", "/api/v1/reranker/engine") { try { "$u -> " + (Invoke-RestMethod "$ep$u" -Headers $h | ConvertTo-Json -Compress) } catch {} }
# the engine must come back on demand after being released
$sw.Restart()
try { $r = Invoke-RestMethod "$ep/api/v1/chat/completions" -Method Post -Headers $h -ContentType "application/json" -Body $body -TimeoutSec 300; "dopo il riposo, nuova chat in {0:N1}s: {1}" -f $sw.Elapsed.TotalSeconds, ($r.choices[0].message.content -replace '\s+', ' ') } catch { "ERRORE chat dopo riposo: " + $_.Exception.Message }
Show "dopo la nuova chat"
& "$app\PaRuX.exe" --exit 2>$null; Start-Sleep 8
"processi rimasti: " + ((Get-Process PaRuX, PaRuX.Server, llama-server -ErrorAction SilentlyContinue | ForEach-Object Name) -join ",")
