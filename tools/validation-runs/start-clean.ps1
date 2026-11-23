param(
  [Parameter(Mandatory)][string]$AppDir,
  [string]$ProfileDir = (Join-Path $env:TEMP "parux-clean-profile"),
  [int]$TimeoutSeconds = 180
)
# Starts the packaged app with a scrubbed environment: no DOTNET_*, no developer PATH, empty user profile folders.
New-Item -ItemType Directory -Force "$ProfileDir\Local", "$ProfileDir\Roaming", "$ProfileDir\Temp" | Out-Null
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $AppDir "PaRuX.exe"
$psi.WorkingDirectory = $AppDir
$psi.UseShellExecute = $false
$psi.EnvironmentVariables.Clear()
$sys = $env:SystemRoot
$vars = @{
  SystemRoot = $sys; windir = $sys; SystemDrive = $env:SystemDrive; ComSpec = "$sys\System32\cmd.exe"
  PATH = "$sys\System32;$sys"; PATHEXT = ".COM;.EXE;.BAT;.CMD"
  USERNAME = $env:USERNAME; COMPUTERNAME = $env:COMPUTERNAME; USERPROFILE = $ProfileDir
  LOCALAPPDATA = "$ProfileDir\Local"; APPDATA = "$ProfileDir\Roaming"; TEMP = "$ProfileDir\Temp"; TMP = "$ProfileDir\Temp"
  ProgramFiles = $env:ProgramFiles; "ProgramFiles(x86)" = ${env:ProgramFiles(x86)}; ProgramData = $env:ProgramData
  NUMBER_OF_PROCESSORS = $env:NUMBER_OF_PROCESSORS; PROCESSOR_ARCHITECTURE = $env:PROCESSOR_ARCHITECTURE
  PARUX_CORPORATE = "1"; PARUX_CORPORATE_MODEL_DOWNLOADS = "0"
  PARUX_DATA_ROOT = (Join-Path $AppDir "PaRuXData"); PARUX_LOCALAI_STATE_DIR = (Join-Path $AppDir "PaRuXData")
}
foreach ($k in $vars.Keys) { if ($null -ne $vars[$k]) { $psi.EnvironmentVariables[$k] = [string]$vars[$k] } }
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = [Diagnostics.Process]::Start($psi)
$boot = Join-Path $vars.PARUX_DATA_ROOT "server\bootstrap.json"
while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds -and -not (Test-Path $boot)) { if ($p.HasExited) { Write-Output "EXITED early code=$($p.ExitCode)"; exit 2 }; Start-Sleep -Milliseconds 250 }
if (-not (Test-Path $boot)) { Write-Output "NO bootstrap after $TimeoutSeconds s"; exit 3 }
Write-Output ("bootstrap after {0:N1} s; app pid {1}" -f $sw.Elapsed.TotalSeconds, $p.Id)
