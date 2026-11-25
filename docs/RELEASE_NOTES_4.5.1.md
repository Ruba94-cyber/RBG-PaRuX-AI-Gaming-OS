# RBG – PaRuX AI & Gaming OS 4.5.1 release notes (pre-release candidate)

- **Date:** 2026-11-25
- **Channel:** pre-release candidate, built and signed locally. Not published until the owner decides.
- **Replaces 4.5.0:** the 4.5.0 pre-release (Corporate Lite only) is superseded; 4.5.1 is the complete line and includes every 4.5.0 change (see [4.5.0 notes](RELEASE_NOTES_4.5.0.md)).
- **Build commit:** the commit the artifacts were built from (recorded in `SHA256SUMS.txt` and the build log)
- **Version source:** `Directory.Build.props`, `PaRuXProductVersion` = 4.5.1 (Android versionName 4.5.1, versionCode 28)

## What is new since 4.5.0

- **Portable / MSI:** user content (meeting notes, saved web pages) stays inside `PaRuXData` in Portable and Corporate Lite; the MSI bundles the Visual C++ runtime app-local; user skill ids are validated; model ids in errors are listed once.
- **Console:** TV selector with a persistent multi-select list and monitor history; Console mode always starts; TV names from the legacy Console config are imported; PaRuX returns to the foreground when Playnite Fullscreen closes.
- **Start-up:** generic sign-in start orchestrator, support apps, never-pause list, timing log, dry-run.
- **Background downloads need consent:** downloads in the background and online catalog checks are OFF by default. Migration: the old implicit default (on) is not carried over; existing settings read as OFF until you switch it on once. Corporate Lite is always off.
- **Optional MSI features, off by default:** "Controller drivers (ViGEmBus + HidHide)" and "Sunshine (streaming host)". Official, unmodified, hash-pinned vendor files (`packaging/redist-installers.lock.json`); a silent install includes them only with `PARUX_ADD_DRIVERS=1`, `PARUX_ADD_SUNSHINE=1` or `ADDLOCAL=`. Existing driver installs are never touched; uninstalling PaRuX removes only drivers the setup itself installed. Settings > Support apps shows the state and ownership. The MSI is about 52 MB larger with these payloads; `-NoRedist` builds without them. Details and limits: [INSTALL.md](guides/INSTALL.md).
- Everything from 4.5.0 (layered Portable layout, Corporate Lite with automatic small-model fallback, Console mode, Games tools, AI/Chat, Knowledge readers, reliability fixes) is included.

## Signing

MSI, PE files in the packages and ZIP launchers are signed with the self-signed certificate (CN=Paolo Rubagotti); the APK with the owner's Android release key; the update manifest with PaRuX's own RSA key. SmartScreen reputation is NOT established.

## Limitations (NOT RUN or not established)

- No physical test with a real pad, Playnite Fullscreen with a running game, or a training GPU for this build.
- No test on a real corporate PC.
- Win+G is not blocked: the Game Bar block is opt-in and covers the Game Bar settings, not the shortcut.
- Self-signed certificate; no SmartScreen reputation.
- The MSI was not installed on a clean PC: only a silent administrative extraction and content checks were done.
- The optional MSI features (controller drivers, Sunshine) were never installed on a real PC: UAC cannot be driven here, so only the package tables, an administrative extraction, `wix msi validate` and unit tests were checked; the "Optional components" wizard page was not displayed; the vendor silent switches were not exercised. The drivers need UAC and may need a restart; ViGEmBus is retired upstream; Sunshine opens network ports only while it runs.
- Android: pairing with a real Core and a physical device not tested.
- `.pst/.ost` and MF4/MDF are not read; the bundled starter model is weak on multi-part and numeric questions.
- Competitors: no superiority is claimed.

## Upgrade notes

- MSI: in-app Updates or install over the old one. Portable: extract to a new folder and move `PaRuXData` across, keeping it.
- Background downloads: re-enable "Keep runtimes up to date" in Settings if you want it.
- Back up your data first if it holds anything you cannot recreate.
- Files above the GitHub limit are split; join with `join-release-asset.ps1`.

## Links

[Install](guides/INSTALL.md) · [Troubleshooting](guides/TROUBLESHOOTING.md) · [Plan](../PARUX_EXECUTION_PLAN.md) · [Changelog](../CHANGELOG.md)
