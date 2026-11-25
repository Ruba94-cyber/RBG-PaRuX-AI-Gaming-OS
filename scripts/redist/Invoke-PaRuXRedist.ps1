<#
.SYNOPSIS
  Elevated helper of the PaRuX MSI optional "Controller drivers" feature (ViGEmBus + HidHide).
.DESCRIPTION
  Installed under <INSTALLFOLDER>\Redist and started by deferred, elevated MSI custom actions ONLY when the feature is selected
  (install) or when PaRuX is removed (uninstall). It never touches anything it did not install itself:

    Launch   (synchronous, called by the MSI)  copies this script + the lock file into a SYSTEM/Administrators-only run folder and
             starts the Work step detached, because a vendor installer is itself an MSI and cannot run inside PaRuX's own
             Windows Installer transaction (error 1618).
    Work     waits until Windows Installer is idle, then (Install) for each locked component: skips it when it is already on the PC,
             checks the bundled file's size + SHA-256 + Authenticode against packaging/redist-installers.lock.json, runs the vendor
             installer unmodified with its documented silent switches and records "installed by PaRuX" under HKLM; or (Uninstall)
             removes ONLY components recorded as installed by PaRuX.
    SelfTest pure decision checks with synthetic data; installs nothing, reads no machine state.

  Rollback coupling: the MSI writes HKLM\SOFTWARE\Ruba94-cyber\PaRuX\Features\<id> from the feature's own component, so a rolled
  back or cancelled setup leaves no marker and the Work step does nothing. Dot-sourcing this file only defines the functions.
  Log: %ProgramData%\PaRuX\logs\redist-install.log
#>
[CmdletBinding()]
param(
    [ValidateSet('None', 'Launch', 'Work', 'SelfTest')][string]$Mode = 'None',
    [ValidateSet('Install', 'Uninstall')][string]$Action = 'Install',
    [string]$RedistRoot = '',
    [string]$LockPath = '',
    [string]$Log = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:StateRoot = 'SOFTWARE\Ruba94-cyber\PaRuX'
$script:AllowedArgs = @('/exenoui', '/qn', '/norestart')

# ---------------------------------------------------------------- pure functions (covered by -SelfTest and the xUnit contracts)

function Get-RedistSha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
    return ([System.BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
}

function Test-RedistSilentArgs($Component) {
    # Returns an error text or $null. Only the documented, non-rebooting switches may ever be passed to a vendor installer.
    $list = @($Component.install.silentArgs)
    if ($list.Count -eq 0) { return "$($Component.id): no silent arguments." }
    foreach ($a in $list) { if ($script:AllowedArgs -notcontains ([string]$a).ToLowerInvariant()) { return "$($Component.id): argument '$a' is not in the allow-list." } }
    if (($list -notcontains "/qn") -or ($list -notcontains "/norestart")) { return "$($Component.id): /qn and /norestart are mandatory." }
    return $null
}

function Test-RedistPayload($Component, [long]$ActualSize, [string]$ActualSha256) {
    if ($ActualSize -ne [long]$Component.asset.sizeBytes) { return "$($Component.id): size $ActualSize differs from the pinned $($Component.asset.sizeBytes)." }
    if ($ActualSha256.ToLowerInvariant() -ne ([string]$Component.asset.sha256).ToLowerInvariant()) { return "$($Component.id): SHA-256 differs from the pinned value." }
    return $null
}

function Test-RedistSignature($Component, [string]$Status, [string]$Subject, [string]$Thumbprint) {
    if (-not [bool]$Component.signature.required) { return $null }
    if ($Status -ne 'Valid') { return "$($Component.id): Authenticode status is '$Status', not 'Valid'." }
    if ([string]::IsNullOrEmpty($Subject) -or -not $Subject.StartsWith([string]$Component.signature.subject, [System.StringComparison]::Ordinal)) { return "$($Component.id): signer is not the pinned publisher." }
    if ($Thumbprint.ToUpperInvariant() -ne ([string]$Component.signature.thumbprint).ToUpperInvariant()) { return "$($Component.id): signing certificate is not the pinned one." }
    return $null
}

function Get-RedistInstallDecision([bool]$PayloadExists, [bool]$PresentOnPc, [bool]$InstalledByParux) {
    # Never touch an installation PaRuX did not make: present + not ours -> leave it alone (USER_MANAGED / SYSTEM_DETECTED).
    if (-not $PayloadExists) { return 'SkipNoPayload' }
    if ($InstalledByParux) { return 'SkipAlreadyInstalledByParux' }
    if ($PresentOnPc) { return 'SkipExistingNotOurs' }
    return 'Install'
}

function Get-RedistUninstallDecision([bool]$InstalledByParux, [bool]$ProductStillInstalled) {
    if ($ProductStillInstalled) { return 'SkipProductStillInstalled' }   # cancelled/rolled-back removal
    if (-not $InstalledByParux) { return 'SkipNotOurs' }
    return 'Uninstall'
}

function Convert-RedistExitCode($Component, [int]$Code) {
    if (@($Component.install.exitCodesOk) -contains $Code) { return 'Ok' }
    if (@($Component.install.exitCodesRebootRequired) -contains $Code) { return 'RebootRequired' }
    return 'Failed'
}

function Test-RedistInside([string]$Child, [string]$Root) {
    $c = [System.IO.Path]::GetFullPath($Child)
    $r = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    return $c.StartsWith($r, [System.StringComparison]::OrdinalIgnoreCase)
}

# ---------------------------------------------------------------- machine access (not used by -SelfTest)

function Write-RedistLog([string]$Message) {
    $line = '{0} [{1}] {2}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Action, $Message
    try {
        if ([string]::IsNullOrWhiteSpace($script:LogFile)) { return }
        $dir = Split-Path -Parent $script:LogFile
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8
    } catch { }
}

function Test-RedistElevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object System.Security.Principal.WindowsPrincipal($id)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator) -or $id.IsSystem
}

function Open-RedistHklm {
    return [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
}

function Test-RedistMarker([string]$SubKey) {
    $hklm = Open-RedistHklm
    try { $k = $hklm.OpenSubKey($script:StateRoot + '\' + $SubKey); if ($null -eq $k) { return $false } else { $k.Dispose(); return $true } } finally { $hklm.Dispose() }
}

function Test-RedistFeatureSelected([string]$FeatureKey) {
    $hklm = Open-RedistHklm
    try {
        $k = $hklm.OpenSubKey($script:StateRoot + '\Features')
        if ($null -eq $k) { return $false }
        try { return ($k.GetValue($FeatureKey) -eq 1) } finally { $k.Dispose() }
    } finally { $hklm.Dispose() }
}

function Get-RedistState([string]$Id) {
    $hklm = Open-RedistHklm
    try {
        $k = $hklm.OpenSubKey($script:StateRoot + '\Redist\' + $Id)
        if ($null -eq $k) { return $null }
        try { return [pscustomobject]@{ InstalledByParux = ($k.GetValue('InstalledByPaRuX') -eq 1); Version = [string]$k.GetValue('Version') } } finally { $k.Dispose() }
    } finally { $hklm.Dispose() }
}

function Set-RedistState($Component, [bool]$Reboot, [string]$Sha256) {
    $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $k = $hklm.CreateSubKey($script:StateRoot + '\Redist\' + $Component.id)
        try {
            $k.SetValue('InstalledByPaRuX', 1, 'DWord')
            $k.SetValue('Version', [string]$Component.version, 'String')
            $k.SetValue('Sha256', $Sha256, 'String')
            $k.SetValue('RebootRequired', $(if ($Reboot) { 1 } else { 0 }), 'DWord')
            $k.SetValue('InstalledUtc', (Get-Date).ToUniversalTime().ToString('o'), 'String')
        } finally { $k.Dispose() }
    } finally { $hklm.Dispose() }
}

function Remove-RedistState([string]$Id) {
    $hklm = Open-RedistHklm
    try { $hklm.DeleteSubKeyTree($script:StateRoot + '\Redist\' + $Id, $false) } finally { $hklm.Dispose() }
}

function Test-RedistPresent($Component) {
    foreach ($p in @($Component.install.presenceProbes)) {
        if ($p.kind -eq 'service') {
            if (Test-Path -LiteralPath ('Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\' + $p.name)) { return $true }
        } elseif ($p.kind -eq 'file') {
            if (Test-Path -LiteralPath ([Environment]::ExpandEnvironmentVariables([string]$p.path))) { return $true }
        }
    }
    return ($null -ne (Get-RedistArpEntry $Component))
}

function Get-RedistArpEntry($Component) {
    if ([string]::IsNullOrWhiteSpace([string]$Component.install.arpDisplayName)) { return $null }
    foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($sub in Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue) {
            $p = Get-ItemProperty -LiteralPath $sub.PSPath -ErrorAction SilentlyContinue
            if ($null -eq $p -or -not ($p.PSObject.Properties.Name -contains 'DisplayName')) { continue }
            $publisher = if ($p.PSObject.Properties.Name -contains 'Publisher') { [string]$p.Publisher } else { '' }
            if ($p.DisplayName -eq $Component.install.arpDisplayName -and $publisher -eq $Component.install.arpPublisher) {
                $uninstall = if ($p.PSObject.Properties.Name -contains 'UninstallString') { [string]$p.UninstallString } else { '' }
                $m = [regex]::Match($uninstall, '(?i)msiexec(\.exe)?"?\s+/[ix]\s*(\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\})')
                return [pscustomobject]@{ ProductCode = $(if ($m.Success) { $m.Groups[2].Value } else { $null }); DisplayVersion = [string]$p.DisplayVersion }
            }
        }
    }
    return $null
}

function Wait-RedistMsiIdle([int]$TimeoutSeconds = 900) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $quiet = 0
    while ((Get-Date) -lt $deadline) {
        $m = $null
        $busy = $false
        if ([System.Threading.Mutex]::TryOpenExisting('Global\_MSIExecute', [ref]$m)) {
            try { if ($m.WaitOne(0)) { $m.ReleaseMutex() } else { $busy = $true } }
            catch [System.Threading.AbandonedMutexException] { }
            finally { $m.Dispose() }
        }
        if ($busy) { $quiet = 0 } else { $quiet++ }
        if ($quiet -ge 3) { return $true }   # idle for 3 consecutive polls
        Start-Sleep -Seconds 2
    }
    return $false
}

function Invoke-RedistInstall($Lock, [string]$Root, [string]$RunDir) {
    foreach ($c in @($Lock.components | Where-Object { $_.install.type -eq 'advanced-installer-exe' })) {
        $feature = [string]$c.msiFeature
        if (-not (Test-RedistFeatureSelected $feature)) { Write-RedistLog "$($c.id): feature '$feature' is not selected (or the setup was rolled back); nothing to do."; continue }
        $payload = Join-Path (Join-Path $Root ([string]$c.payloadFolder)) ([string]$c.asset.name)
        $state = Get-RedistState $c.id
        $decision = Get-RedistInstallDecision -PayloadExists ((Test-Path -LiteralPath $payload -PathType Leaf) -and (Test-RedistInside $payload $Root)) `
            -PresentOnPc (Test-RedistPresent $c) -InstalledByParux ($null -ne $state -and $state.InstalledByParux)
        if ($decision -ne 'Install') { Write-RedistLog "$($c.id): $decision - not installing and not touching what is there."; continue }
        $argError = Test-RedistSilentArgs $c
        if ($argError) { Write-RedistLog "REFUSED $argError"; continue }

        # Copy into the protected run folder first, then verify the COPY that will be executed.
        $copy = Join-Path $RunDir ([string]$c.asset.name)
        Copy-Item -LiteralPath $payload -Destination $copy -Force
        $sha = Get-RedistSha256 $copy
        $bad = Test-RedistPayload $c (Get-Item -LiteralPath $copy).Length $sha
        if ($bad) { Write-RedistLog "REFUSED $bad"; continue }
        $sig = Get-AuthenticodeSignature -LiteralPath $copy
        $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '' }
        $thumb = if ($sig.SignerCertificate) { $sig.SignerCertificate.Thumbprint } else { '' }
        $bad = Test-RedistSignature $c ([string]$sig.Status) $subject $thumb
        if ($bad) { Write-RedistLog "REFUSED $bad"; continue }

        Write-RedistLog "$($c.id) $($c.version): checks passed (SHA-256 $sha, signer $subject); running the vendor installer silently."
        $proc = Start-Process -FilePath $copy -ArgumentList @($c.install.silentArgs) -PassThru -WindowStyle Hidden
        if (-not $proc.WaitForExit(20 * 60 * 1000)) { Write-RedistLog "$($c.id): the vendor installer is still running after 20 minutes; leaving it alone, nothing recorded."; continue }
        $outcome = Convert-RedistExitCode $c $proc.ExitCode
        Write-RedistLog "$($c.id): installer exit code $($proc.ExitCode) -> $outcome."
        if ($outcome -eq 'Failed') { continue }
        $reboot = ($outcome -eq 'RebootRequired')
        if (-not $reboot -and -not (Test-RedistPresent $c)) { Write-RedistLog "$($c.id): reported success but is not detectable; not recording ownership."; continue }
        Set-RedistState $c $reboot $sha
        Write-RedistLog "$($c.id): recorded as installed by PaRuX$(if ($reboot) { ' (restart required)' })."
    }
}

function Invoke-RedistUninstall($Lock) {
    $hklm = Open-RedistHklm
    try { $k = $hklm.OpenSubKey($script:StateRoot); $productStillInstalled = ($null -ne $k -and $null -ne $k.GetValue('CliPathRegistered')); if ($k) { $k.Dispose() } } finally { $hklm.Dispose() }
    foreach ($c in @($Lock.components | Where-Object { $_.install.type -eq 'advanced-installer-exe' })) {
        $state = Get-RedistState $c.id
        $decision = Get-RedistUninstallDecision -InstalledByParux ($null -ne $state -and $state.InstalledByParux) -ProductStillInstalled $productStillInstalled
        if ($decision -ne 'Uninstall') { Write-RedistLog "$($c.id): $decision - nothing removed."; continue }
        $arp = Get-RedistArpEntry $c
        if ($null -eq $arp -or [string]::IsNullOrEmpty($arp.ProductCode)) {
            Write-RedistLog "$($c.id): no matching Windows Installer entry; it was already removed. Clearing the PaRuX record."
            Remove-RedistState $c.id; continue
        }
        Write-RedistLog "$($c.id): removing the copy PaRuX installed ($($arp.ProductCode))."
        $proc = Start-Process -FilePath (Join-Path ([Environment]::SystemDirectory) 'msiexec.exe') -ArgumentList @('/x', $arp.ProductCode, '/qn', '/norestart') -PassThru -WindowStyle Hidden
        if (-not $proc.WaitForExit(20 * 60 * 1000)) { Write-RedistLog "$($c.id): uninstall still running after 20 minutes; record kept."; continue }
        $outcome = Convert-RedistExitCode $c $proc.ExitCode
        Write-RedistLog "$($c.id): uninstall exit code $($proc.ExitCode) -> $outcome."
        if ($outcome -ne 'Failed') { Remove-RedistState $c.id }
    }
}

# After a full removal the Redist folder can survive as an empty shell (it is touched at run time, not owned by a component).
# Removes ONLY directories that contain no file at all, deepest first, and never anything outside RedistRoot.
function Remove-RedistEmptyFolders([string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root) -or -not (Test-Path -LiteralPath $Root -PathType Container)) { return }
    if ((Split-Path -Leaf $Root) -ne 'Redist') { Write-RedistLog 'Not a Redist folder; left untouched.'; return }
    $dirs = @(Get-ChildItem -LiteralPath $Root -Directory -Recurse -Force -ErrorAction SilentlyContinue | Sort-Object { $_.FullName.Length } -Descending) + @(Get-Item -LiteralPath $Root)
    foreach ($d in $dirs) {
        if (@(Get-ChildItem -LiteralPath $d.FullName -Force -ErrorAction SilentlyContinue).Count -eq 0) {
            try { Remove-Item -LiteralPath $d.FullName -Force -ErrorAction Stop; Write-RedistLog "Removed the empty folder $($d.FullName)." } catch { Write-RedistLog "Could not remove $($d.FullName): $($_.Exception.Message)" }
        }
    }
}

function New-RedistRunDir {
    $base = Join-Path ([Environment]::SystemDirectory) 'config\systemprofile\PaRuX-redist-run'
    $dir = Join-Path $base ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $id = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $dir -AclObject $acl
    return $dir
}

# ---------------------------------------------------------------- self test

function Invoke-RedistSelfTest {
    $fail = New-Object System.Collections.Generic.List[string]
    function Assert-Eq($Name, $Actual, $Expected) { if ($Actual -ne $Expected) { $fail.Add("$Name : expected '$Expected' got '$Actual'") } }
    $c = [pscustomobject]@{
        id = 'fake'; version = '1.0.0'
        asset = [pscustomobject]@{ sizeBytes = 10; sha256 = ('ab' * 32) }
        signature = [pscustomobject]@{ required = $true; subject = 'CN=Vendor'; thumbprint = ('CD' * 20) }
        install = [pscustomobject]@{ silentArgs = @('/exenoui', '/qn', '/norestart'); exitCodesOk = @(0); exitCodesRebootRequired = @(3010, 1641) }
    }
    Assert-Eq 'payload ok' (Test-RedistPayload $c 10 ('AB' * 32)) $null
    Assert-Eq 'payload size' ([bool](Test-RedistPayload $c 11 ('ab' * 32))) $true
    Assert-Eq 'payload hash' ([bool](Test-RedistPayload $c 10 ('00' * 32))) $true
    Assert-Eq 'sig ok' (Test-RedistSignature $c 'Valid' 'CN=Vendor, O=X' ('cd' * 20)) $null
    Assert-Eq 'sig unsigned' ([bool](Test-RedistSignature $c 'NotSigned' '' '')) $true
    Assert-Eq 'sig hashmismatch' ([bool](Test-RedistSignature $c 'HashMismatch' 'CN=Vendor' ('CD' * 20))) $true
    Assert-Eq 'sig other publisher' ([bool](Test-RedistSignature $c 'Valid' 'CN=Evil' ('CD' * 20))) $true
    Assert-Eq 'sig other thumbprint' ([bool](Test-RedistSignature $c 'Valid' 'CN=Vendor' ('EE' * 20))) $true
    Assert-Eq 'args ok' (Test-RedistSilentArgs $c) $null
    $bad = [pscustomobject]@{ id = 'bad'; install = [pscustomobject]@{ silentArgs = @('/qn', '/norestart', '/forcerestart') } }
    Assert-Eq 'args forbidden' ([bool](Test-RedistSilentArgs $bad)) $true
    $noRestart = [pscustomobject]@{ id = 'nr'; install = [pscustomobject]@{ silentArgs = @('/qn') } }
    Assert-Eq 'args need norestart' ([bool](Test-RedistSilentArgs $noRestart)) $true
    Assert-Eq 'decision fresh' (Get-RedistInstallDecision $true $false $false) 'Install'
    Assert-Eq 'decision user copy' (Get-RedistInstallDecision $true $true $false) 'SkipExistingNotOurs'
    Assert-Eq 'decision ours' (Get-RedistInstallDecision $true $true $true) 'SkipAlreadyInstalledByParux'
    Assert-Eq 'decision no payload' (Get-RedistInstallDecision $false $false $false) 'SkipNoPayload'
    Assert-Eq 'uninstall ours' (Get-RedistUninstallDecision $true $false) 'Uninstall'
    Assert-Eq 'uninstall not ours' (Get-RedistUninstallDecision $false $false) 'SkipNotOurs'
    Assert-Eq 'uninstall rolled back' (Get-RedistUninstallDecision $true $true) 'SkipProductStillInstalled'
    Assert-Eq 'exit ok' (Convert-RedistExitCode $c 0) 'Ok'
    Assert-Eq 'exit reboot' (Convert-RedistExitCode $c 3010) 'RebootRequired'
    Assert-Eq 'exit fail' (Convert-RedistExitCode $c 1603) 'Failed'
    Assert-Eq 'inside ok' (Test-RedistInside 'C:\a\b\c.exe' 'C:\a\b') $true
    Assert-Eq 'inside escape' (Test-RedistInside 'C:\a\b\..\x.exe' 'C:\a\b') $false
    if ($fail.Count -gt 0) { $fail | ForEach-Object { Write-Output "FAIL $_" }; exit 1 }
    Write-Output 'REDIST_SELFTEST_OK'
}

# ---------------------------------------------------------------- entry points

if ($MyInvocation.InvocationName -eq '.' -or $Mode -eq 'None') { return }

if ($Mode -eq 'SelfTest') { Invoke-RedistSelfTest; exit 0 }

$script:LogFile = if ([string]::IsNullOrWhiteSpace($Log)) { Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'PaRuX\logs\redist-install.log' } else { $Log }
try {
    if (-not (Test-RedistElevated)) { Write-RedistLog 'Not elevated; refusing to run.'; exit 0 }
    if ([string]::IsNullOrWhiteSpace($RedistRoot)) { Write-RedistLog 'No Redist folder was given.'; exit 0 }
    $RedistRoot = [System.IO.Path]::GetFullPath($RedistRoot)

    if ($Mode -eq 'Launch') {
        $lockSource = Join-Path $RedistRoot 'redist-installers.lock.json'
        if (-not (Test-Path -LiteralPath $lockSource -PathType Leaf)) { Write-RedistLog 'The lock file is missing; nothing scheduled.'; exit 0 }
        $run = New-RedistRunDir
        Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $run 'Invoke-PaRuXRedist.ps1') -Force
        Copy-Item -LiteralPath $lockSource -Destination (Join-Path $run 'redist-installers.lock.json') -Force
        $ps = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
        $argLine = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Mode Work -Action {1} -RedistRoot "{2}" -LockPath "{3}" -Log "{4}"' -f `
            (Join-Path $run 'Invoke-PaRuXRedist.ps1'), $Action, $RedistRoot, (Join-Path $run 'redist-installers.lock.json'), $script:LogFile
        Start-Process -FilePath $ps -ArgumentList $argLine -WindowStyle Hidden | Out-Null
        Write-RedistLog "Scheduled the $Action step to run after the PaRuX setup finishes (run folder $run)."
        exit 0
    }

    # Work
    $lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
    if ($lock.schemaVersion -ne 1) { Write-RedistLog 'Unsupported lock schema.'; exit 0 }
    $runDir = Split-Path -Parent $LockPath
    if (-not (Wait-RedistMsiIdle)) { Write-RedistLog 'Windows Installer stayed busy for 15 minutes; giving up without changes.'; exit 0 }
    Start-Sleep -Seconds 2
    if ($Action -eq 'Install') { Invoke-RedistInstall $lock $RedistRoot $runDir } else { Invoke-RedistUninstall $lock; Remove-RedistEmptyFolders $RedistRoot }
    Write-RedistLog 'Done.'
} catch {
    Write-RedistLog ('Unexpected error: ' + $_.Exception.Message)
}
exit 0
