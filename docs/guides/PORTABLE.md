# Portable use (Corporate Lite)

PaRuX has **two distributions**: the **MSI** (normal Windows installation) and **Corporate Lite** (portable, offline, for company PCs).
The former *Standard Portable* ZIP (`PaRuX-<version>-portable-win-x64.zip`) is **discontinued**: it is no longer built, published or
updated. If you still have one, see [Moving from the discontinued Standard Portable](INSTALL.md#moving-from-the-discontinued-standard-portable).

This page describes the layout shared by the portable Corporate Lite package. Setup and policy: [CORPORATE.md](CORPORATE.md).

## Use it

1. Extract the Corporate Lite ZIP to a writable **NTFS** folder (any drive). Removable FAT/exFAT drives are not certified.
2. Double-click `PaRuX.exe` or one of the `.cmd` files in `launchers\` (offline / CPU only / model downloads).
3. A `parux.corporate` marker in `app\`, plus the `PaRuXData` folder next to `PaRuX.exe`, keeps all data in the package folder.
   Chats, settings, models and indexes stay in `PaRuXData`, not in your Windows profile.

### Folder layout

The ZIP root folder holds **one** file, `PaRuX.exe`, and four folders:

```text
PaRuX-Corporate-Lite\
  PaRuX.exe                       small launcher, the only visible file (starts app\PaRuX.exe, waits, returns its exit code)
  app\                            the application: PaRuX.exe, PaRuX.Server\, .NET/WinUI runtime, languages, parux.corporate
  PaRuXData\                      all your data (settings, models, history, indexes)
  docs\                           README-corporate-lite.txt, MANIFEST.json, licences, third-party notices
  launchers\                      Start PaRuX Corporate Lite.cmd and the CPU-only / model-download variants
```

The build verifies this and fails if anything else is at the root. `PaRuX.exe --exit` goes through the launcher to the running
application. Killing the launcher also stops the application. You can move the extracted folder to another path or drive.

## Download in parts (large Corporate Lite package)

GitHub allows at most 2 GiB per release file. A Corporate Lite package that bundles a model may be larger, so a release can publish
it as numbered parts instead of one file:

- `PaRuX-Corporate-Lite-Offline-win-x64.zip.part001`, `.part002`, ... (each at most 1.9 GiB)
- `PaRuX-Corporate-Lite-Offline-win-x64.zip.parts.sha256.txt` (SHA-256 of every part and of the joined ZIP)

Put **all** parts and the `.parts.sha256.txt` file in one folder, then join them with plain Windows PowerShell (no extra software).
Run from that folder (adjust `$name` to the file you downloaded):

```powershell
$name  = "PaRuX-Corporate-Lite-Offline-win-x64.zip"
$lines = Get-Content "$name.parts.sha256.txt"
# 1. every part must match the published hash (also catches a missing or truncated download)
foreach ($l in $lines) {
    $hash, $file = $l -split '  ', 2
    if ($file -ne $name -and (Get-FileHash $file -Algorithm SHA256).Hash -ne $hash) { throw "Hash mismatch: $file" }
}
# 2. concatenate the parts in order
$out = [IO.File]::Create("$PWD\$name")
Get-ChildItem "$name.part*" | Where-Object Name -match '\.part\d{3}$' | Sort-Object Name | ForEach-Object {
    $in = [IO.File]::OpenRead($_.FullName); $in.CopyTo($out); $in.Dispose()
}
$out.Dispose()
# 3. the joined ZIP must match the last line of the list
$want = ($lines | Where-Object { $_ -like "*  $name" }) -split '  ' | Select-Object -First 1
if ((Get-FileHash $name -Algorithm SHA256).Hash -ne $want) { throw "Joined ZIP hash mismatch - download the parts again." }
"Joined and verified: $name"
```

The repository also provides `tools/join-release-asset.ps1` (same steps with better error messages):
`powershell -NoProfile -ExecutionPolicy Bypass -File .\join-release-asset.ps1 -FirstPart .\<name>.zip.part001`.
`copy /b <name>.zip.part001+<name>.zip.part002 <name>.zip` in `cmd` also joins two parts, but it does not verify anything, so
compare `Get-FileHash` with the list yourself. The part files can be deleted after extraction.
This is a transport workaround for the host's size limit, not a different product.

## Not included

Windows drivers, game stores, games, and optional image/video/training engines. Corporate Lite has no in-app updater: IT replaces the
application files (`PaRuX.exe`, `app\`, `docs\`, `launchers\`) and keeps `PaRuXData`.

## Limits

- Closing the window does not quit the app: PaRuX hides to the system tray so background work and the local Core keep running.
  Quit from the tray icon (Exit). This is by design, not a hang.
- The ZIP container cannot carry a signature. Builds made with a signing certificate Authenticode-sign the first-party executables
  and DLLs inside it; Windows can still show "Unknown publisher", and SmartScreen and antivirus rules still apply (no bypass).

For problems see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).
