<#
.SYNOPSIS
    Real question-and-answer acceptance run of PaRuX Corporate on CAN files (DBC + Vector ASC traces).

.DESCRIPTION
    Extracts the Corporate ZIP into a clean folder, starts it with a scrubbed environment (no developer PATH/DOTNET_*, redirected
    profile), copies the given files into an isolated folder, indexes them through the Core API and asks questions in document mode.
    Every question carries an expected fact taken independently from the files (not from PaRuX). The result file records the answer,
    the sources shown to the user, timings, memory and PASS/FAIL per expectation. Nothing leaves the PC; only copies of the
    supplied files are read.

    The input files are the owner's own data and are NOT part of the repository: pass them with -Files.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string[]]$Files,
    [string]$WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) ("parux-can-qa-" + [Guid]::NewGuid().ToString("N"))),
    [string]$ResultsFile = "",
    [int]$IndexTimeoutMinutes = 30,
    [int]$ChatTimeoutMinutes = 8
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
New-Item -ItemType Directory -Force $WorkRoot | Out-Null
$clean = Join-Path $WorkRoot "pkg"; $data = Join-Path $WorkRoot "files"; $prof = Join-Path $WorkRoot "profile"
New-Item -ItemType Directory -Force $clean, $data, "$prof\Local", "$prof\Roaming", "$prof\Temp" | Out-Null
Expand-Archive -LiteralPath $ZipPath -DestinationPath $clean
$app = Join-Path $clean "PaRuX-Corporate-Lite"
foreach ($f in $Files) { Copy-Item -LiteralPath $f -Destination $data }

# scrubbed environment launch
$psi = New-Object Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $app "PaRuX.exe"; $psi.WorkingDirectory = $app; $psi.UseShellExecute = $false
$psi.EnvironmentVariables.Clear(); $sys = $env:SystemRoot
$vars = @{ SystemRoot = $sys; windir = $sys; SystemDrive = $env:SystemDrive; PATH = "$sys\System32;$sys"; USERNAME = $env:USERNAME
    COMPUTERNAME = $env:COMPUTERNAME; USERPROFILE = $prof; LOCALAPPDATA = "$prof\Local"; APPDATA = "$prof\Roaming"; TEMP = "$prof\Temp"; TMP = "$prof\Temp"
    ProgramFiles = $env:ProgramFiles; ProgramData = $env:ProgramData; NUMBER_OF_PROCESSORS = $env:NUMBER_OF_PROCESSORS
    PARUX_CORPORATE = "1"; PARUX_CORPORATE_MODEL_DOWNLOADS = "0"; PARUX_DATA_ROOT = (Join-Path $app "PaRuXData"); PARUX_LOCALAI_STATE_DIR = (Join-Path $app "PaRuXData") }
foreach ($k in $vars.Keys) { $psi.EnvironmentVariables[$k] = [string]$vars[$k] }
$sw = [Diagnostics.Stopwatch]::StartNew(); $proc = [Diagnostics.Process]::Start($psi)
$boot = Join-Path $vars.PARUX_DATA_ROOT "server\bootstrap.json"
while ($sw.Elapsed.TotalSeconds -lt 180 -and -not (Test-Path $boot)) { if ($proc.HasExited) { throw "App exited early" }; Start-Sleep -Milliseconds 250 }
if (-not (Test-Path $boot)) { throw "Core did not start" }
$startS = [math]::Round($sw.Elapsed.TotalSeconds, 1)
$b = Get-Content $boot -Raw | ConvertFrom-Json; $ep = $b.endpoint.TrimEnd('/'); $h = @{ "X-PaRuX-Token" = $b.token }
function Mem { [math]::Round((Get-Process PaRuX, PaRuX.Server, llama-server -ErrorAction SilentlyContinue | Measure-Object WorkingSet64 -Sum).Sum / 1MB) }
$R = [ordered]@{ zip = (Split-Path $ZipPath -Leaf); zipSha256 = (Get-FileHash $ZipPath).Hash; coreStartSeconds = $startS; ramIdleMb = (Mem); files = @() }
foreach ($f in Get-ChildItem $data) { $R.files += "{0} {1:N1} MB" -f $f.Name, ($f.Length / 1MB) }

try {
    $add = Invoke-RestMethod "$ep/api/v1/rag/sources" -Method Post -Headers $h -ContentType "application/json" -Body (@{ folderPath = $data } | ConvertTo-Json)
    $id = if ($add.id) { $add.id } else { $add.Id }
    $sw.Restart(); $peak = 0
    $null = Invoke-RestMethod "$ep/api/v1/knowledge/sources/$id/index" -Method Post -Headers $h
    do { Start-Sleep -Milliseconds 700; $s = Invoke-RestMethod "$ep/api/v1/knowledge/sources/$id/status" -Headers $h; $m = Mem; if ($m -gt $peak) { $peak = $m } }
    while ($s.job.state -notin 'done', 'failed', 'cancelled' -and $sw.Elapsed.TotalMinutes -lt $IndexTimeoutMinutes)
    $R.index = [ordered]@{ seconds = [math]::Round($sw.Elapsed.TotalSeconds); state = $s.job.state; files = $s.files; chunks = $s.chunks; failed = $s.job.progress.failed; peakRamMb = $peak; problems = @($s.problems | ForEach-Object { "$($_.relativePath): $($_.reason)" }) }

    $questions = @(
        @{ q = "Nel file DTC DMS Can Recording.asc, quali codici DM1 (SPN e FMI) risultano attivi e da quali indirizzi sorgente?"; all = @("520662", "497662", "521454") }
        @{ q = "Quanti frame ha l'ID 0x0CF00400x in CAN-20260730-123749.asc e a che riga compare per la prima volta?"; all = @("2344", "15") }
        @{ q = "A che riga compare per la prima volta l'ID 0x0CF00400x in DTC DMS Can Recording.asc?"; all = @("16") }
        @{ q = "Che segnali contiene il messaggio ETC7 nel DBC?"; all = @("TransmEngCrkEna", "TransmMode1Ind", "TransmMode5Ind") }
        @{ q = "Qual e' il cycle time del messaggio ETC7?"; all = @("100") }
        @{ q = "Quanti frame e quanti ID distinti ci sono in CAN-20260807-092922.asc?"; all = @("30875", "221") }
        @{ q = "Quanti frame e quanti ID distinti ci sono in DTC DMS Can Recording.asc?"; all = @("281491", "266") }
        @{ q = "Quale e' la durata della registrazione CAN-20260730-123749.asc?"; all = @("23.4") }
    )
    function Ask([string]$q) {
        $req = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "$ep/api/v1/chat/completions"); $req.Headers.Add("X-PaRuX-Token", $b.token)
        $body = @{ model = "local-default"; stream = $true; max_tokens = 260; messages = @(@{ role = "user"; content = $q }); documents = @{ enabled = $true; sourceIds = @($id) } } | ConvertTo-Json -Depth 6 -Compress
        $req.Content = [Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, "application/json")
        $c = [Net.Http.HttpClient]::new(); $c.Timeout = [TimeSpan]::FromMinutes($ChatTimeoutMinutes)
        $sw2 = [Diagnostics.Stopwatch]::StartNew(); $ttft = $null; $txt = New-Object Text.StringBuilder; $files = @(); $status = 0
        try {
            $r = $c.SendAsync($req, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult(); $status = [int]$r.StatusCode
            if (-not $r.IsSuccessStatusCode) { [void]$txt.Append($r.Content.ReadAsStringAsync().GetAwaiter().GetResult()) }
            else {
                $sr = [IO.StreamReader]::new($r.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                while ($null -ne ($l = $sr.ReadLine())) {
                    if (-not $l.StartsWith("data:")) { continue }; $d = $l.Substring(5).Trim(); if ($d -eq "[DONE]") { break }
                    try { $j = $d | ConvertFrom-Json } catch { continue }
                    if ($j.PSObject.Properties["parux_event"] -and $j.parux_event -eq "rag_sources") { $files += @($j.files) }
                    $t = $null; try { $t = $j.choices[0].delta.content } catch {}
                    if ($t) { if ($null -eq $ttft) { $ttft = $sw2.Elapsed.TotalSeconds }; [void]$txt.Append($t) }
                }
            }
        } catch { [void]$txt.Append("ERR " + $_.Exception.Message) } finally { $c.Dispose() }
        [pscustomobject]@{ http = $status; ttftS = $(if ($ttft) { [math]::Round($ttft, 1) } else { $null }); totalS = [math]::Round($sw2.Elapsed.TotalSeconds, 1); ramMb = (Mem); sources = @($files | Select-Object -Unique); answer = $txt.ToString().Trim() }
    }
    $R.questions = @()
    foreach ($item in $questions) {
        $a = Ask $item.q
        $missing = @($item.all | Where-Object { $a.answer -notmatch ("(?<![0-9])" + [regex]::Escape($_) + $(if ($_ -like "*.*") { "" } else { "(?![0-9])" })) })
        $R.questions += [ordered]@{ question = $item.q; expected = $item.all; pass = ($a.http -eq 200 -and $missing.Count -eq 0); missing = $missing; http = $a.http; ttftS = $a.ttftS; totalS = $a.totalS; ramMb = $a.ramMb; sources = $a.sources; answer = $a.answer }
    }
    $R.passed = @($R.questions | Where-Object pass).Count; $R.total = $R.questions.Count
} finally {
    Get-Process PaRuX, PaRuX.Server, llama-server -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($WorkRoot, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue
}
if ($ResultsFile) { $R | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResultsFile -Encoding UTF8 }
$R
