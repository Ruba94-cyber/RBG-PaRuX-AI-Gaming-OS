# Install RBG – PaRuX AI & Gaming OS

Short name: RBG – PaRuX. Status: **BETA** (v4.1.0 pre-release). Read [Known limits](#known-limits) first.

## Before you start

- Windows 10 or newer, x64. Linux and macOS are not supported.
- Requirements by use case: see [README requirements](../../README.md#system-requirements).
- Pick a package. Release assets vary per release; check the
  [GitHub Releases](https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Gaming-OS/releases) page. If no assets are listed, no public installer exists yet.

| Package | Use it when | Guide |
|---|---|---|
| MSI installer (`PaRuX-<version>-win-x64.msi`) | Normal PC, per-machine install, in-app update | this page |
| Corporate Lite (portable, CPU, offline, AI + documents) | Managed or offline PCs, relocatable folder | [CORPORATE.md](CORPORATE.md), [PORTABLE.md](PORTABLE.md) |
| Android APK | Companion for a paired Windows PC (not standalone) | [Android setup](../PARUX_ANDROID_README.md) |

Large assets: GitHub limits each release file to 2 GiB, so the Corporate Lite ZIP may be published as `.part001`, `.part002`, ...
plus a `.parts.sha256.txt` list. Download every part, then join and verify them with the PowerShell steps in
[PORTABLE.md](PORTABLE.md#download-in-parts-large-corporate-lite-package) before extracting. The MSI is a single file.

## Install with the MSI

1. Download the MSI and `SHA256SUMS.txt` from the same release.
2. Verify the file (PowerShell):
   ```powershell
   Get-FileHash -Algorithm SHA256 .\PaRuX-*.msi
   Get-AuthenticodeSignature .\PaRuX-*.msi
   ```
   Compare the hash with `SHA256SUMS.txt`. The 4.1.0 MSI is signed with a **self-signed** certificate, so on another PC Windows may show
   "Unknown publisher" or a chain-trust failure (see [release record](../RELEASE_CANDIDATE_4.1.0.md)).
3. Run the MSI. The package is per-machine (installs under Program Files, with a Start Menu entry) and asks for administrator approval.
4. Start RBG – PaRuX from the Start Menu.

## Optional components in the MSI: controller drivers and Sunshine

English first, Italian below. **Both are OFF unless you ask for them.** A silent install (`/qn`) never includes them.

**What they are.** The MSI built with the vendor payloads has two extra features, listed on an "Optional components" page after the install folder:

| Feature | Contains (unmodified, official files) | What the setup does |
|---|---|---|
| Controller drivers (ViGEmBus + HidHide) | ViGEmBus 1.22.0 (BSD-3-Clause), HidHide 1.5.230.0 (MIT), signed by Nefarius Software Solutions e.U. | After PaRuX's own setup finishes, an elevated helper checks each file's SHA-256 and Authenticode signature, then runs the vendor installer silently. A driver that is **already on the PC is never touched**. Windows may ask for a restart. |
| Sunshine (streaming host) | Sunshine 2026.914.233613 portable package (GPL-3.0) | Only places the ZIP under `Redist\Sunshine`. PaRuX unpacks it into its own per-user folder when you click **Install** in Settings > Support apps. No Windows service, firewall rule or autostart is created. |

**Switches for a silent or scripted install** (all public properties):

```powershell
msiexec /i PaRuX-4.5.1-win-x64.msi /qn                                   # neither feature (default)
msiexec /i PaRuX-4.5.1-win-x64.msi /qn PARUX_ADD_DRIVERS=1               # drivers only
msiexec /i PaRuX-4.5.1-win-x64.msi /qn PARUX_ADD_SUNSHINE=1              # Sunshine package only
msiexec /i PaRuX-4.5.1-win-x64.msi /qn ADDLOCAL=PaRuXMain,PaRuXControllerDrivers,PaRuXSunshine
```

**Things to know.**

- Needs UAC (the MSI is per-machine); the drivers are kernel drivers and may need a **restart**. Settings > Support apps then says "Restart Windows to finish".
- Ownership: a driver that the setup installed is `PARUX_MANAGED`; one already present is `SYSTEM_DETECTED` (or `USER_MANAGED` if you pointed PaRuX at it) and PaRuX never updates or removes it. Uninstalling PaRuX removes only the drivers the setup itself recorded (full removal only, never on an upgrade, never on "Change"). Remove the others in Windows Settings > Apps.
- Log of the helper: `%ProgramData%\PaRuX\logs\redist-install.log`. A refused file (wrong hash or signature) is logged and skipped; PaRuX's own setup still succeeds.
- **ViGEmBus is no longer maintained** upstream (final release 1.22.0, no security updates).
- **Sunshine opens network ports while it runs** (streaming and its web UI). PaRuX never starts it until you do, and the support-app entry is added with "Start with Console" off.
- The MSI is bigger: about 52 MB of payload (about 45 MB after MSI compression) on top of the normal size. `scripts/build-msi.ps1 -NoRedist` builds the smaller MSI without any of it.
- Vendor files come only from the pins in `packaging/redist-installers.lock.json`; sources and licences are in [THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md), and the Sunshine source offer is installed beside the package.
- **NOT RUN:** no install of either feature was performed on a real PC (UAC cannot be driven here). Only the package tables, an administrative extraction (`msiexec /a`), `wix msi validate` and unit tests were checked. The wizard page was not displayed. The vendors' silent switches (`/exenoui /qn /norestart`, Advanced Installer) come from the installers' own embedded help, not from a vendor web page, and were not exercised.

**Italiano.** Entrambe le funzioni sono **disattivate** se non le richiedi; un'installazione silenziosa (`/qn`) non le include mai.

- *Driver controller (ViGEmBus + HidHide)*: file ufficiali non modificati, firmati da Nefarius Software Solutions e.U. Dopo il setup di PaRuX, un helper con privilegi elevati controlla SHA-256 e firma Authenticode di ogni file e poi lancia l'installer del produttore in modalità silenziosa. **Un driver già presente sul PC non viene mai toccato.** Windows può chiedere un riavvio.
- *Sunshine (host di streaming)*: pacchetto portatile ufficiale (GPL-3.0). Il setup lo copia soltanto in `Redist\Sunshine`; PaRuX lo estrae nella propria cartella per utente quando premi **Installa** in Impostazioni > App di supporto. Non crea servizi di Windows, regole del firewall né avvio automatico. Sunshine apre porte di rete solo mentre è in esecuzione e PaRuX non lo avvia finché non lo fai tu.
- Interruttori: `PARUX_ADD_DRIVERS=1`, `PARUX_ADD_SUNSHINE=1` o `ADDLOCAL=PaRuXMain,PaRuXControllerDrivers,PaRuXSunshine` (vedi gli esempi sopra).
- Serve UAC; i driver sono del kernel e possono richiedere un **riavvio**. ViGEmBus non è più mantenuto dal produttore.
- Proprietà: un driver installato dal setup è `PARUX_MANAGED`; uno già presente è `SYSTEM_DETECTED` (o `USER_MANAGED` se lo hai indicato tu) e PaRuX non lo aggiorna né lo rimuove. La disinstallazione di PaRuX rimuove solo i driver registrati come installati dal setup, e solo con la rimozione completa (non negli aggiornamenti né con "Modifica").
- L'MSI è più grande: circa 52 MB di contenuto (circa 45 MB dopo la compressione). `scripts/build-msi.ps1 -NoRedist` produce l'MSI più piccolo senza questi file.
- **NON PROVATO:** nessuna installazione reale (UAC non è pilotabile qui); verificati solo tabelle del pacchetto, estrazione amministrativa, `wix msi validate` e test unitari; la pagina della procedura guidata non è stata mostrata.

## First launch

Onboarding detects your hardware and what is already installed. Components follow **Detect, then Locate existing / Install with PaRuX / Skip**.
Nothing is downloaded without your confirmation. Choose your purpose (AI, Gaming, or both); you can change it later in Settings.
Then see [model setup in the README](../../README.md#set-up-a-model).

## Where your data lives

- MSI: `%LOCALAPPDATA%\PaRuX` (override with the `PARUX_DATA_ROOT` environment variable, which must be an absolute path).
- Corporate Lite: `PaRuXData` next to the launcher `PaRuX.exe` (the application itself is in `app`).
- Microsoft Store (MSIX) edition: the per-package `LocalState` folder.

Back up before uninstalling or changing machines. See [PRIVACY.md](PRIVACY.md).

## Update

Open **Updates** (shown in the Advanced interface). PaRuX checks the release metadata, a signed envelope and the MSI SHA-256, then offers
**Download & install verified MSI**. Installation is done by an elevated helper and only for a signed, strictly newer release.
Details: [WINDOWS_MSI_RELEASE.md](../WINDOWS_MSI_RELEASE.md). Corporate Lite has no in-app updater: replace the application files and keep `PaRuXData`. The Standard Portable channel is closed (see below).

## Moving from the discontinued Standard Portable

The Standard Portable ZIP is discontinued: it receives no more updates and the in-app updater says so. Nothing is deleted or migrated
automatically. To keep your chats, settings, models and indexes:

1. Close PaRuX completely (tray icon, Exit) and make a copy of the old `PaRuXData` folder (next to the old `PaRuX.exe`) as a backup.
2. Install the MSI as described above and start PaRuX once, then close it from the tray icon.
3. Copy the contents of the old `PaRuXData` into `%LOCALAPPDATA%\PaRuX` (or into the folder set by `PARUX_DATA_ROOT`), merging over the
   fresh files, and start the MSI build.
4. Keep the old Standard Portable folder until you have checked the MSI build; remove it yourself afterwards.

The old copy keeps running and reading its adjacent `PaRuXData` in the meantime; it just never gets a new version. For a portable,
offline use on a company PC use Corporate Lite instead ([CORPORATE.md](CORPORATE.md)).

## Uninstall

Use Windows Settings, Apps. Whether your data folder is kept or removed was not verified for the MSI; back up first.

## Known limits

- MSI install, upgrade from 4.0.1 and uninstall were **NOT RUN** on a clean PC for the 4.1.0 record; only static checks and a signature check were done.
- Another PC cannot verify the self-signed signature without trusting the certificate.
- No clean-machine or VM install test exists yet. See [RELEASE_READINESS.md](../RELEASE_READINESS.md).

Problems? See [TROUBLESHOOTING.md](TROUBLESHOOTING.md) and [FAQ.md](FAQ.md).
