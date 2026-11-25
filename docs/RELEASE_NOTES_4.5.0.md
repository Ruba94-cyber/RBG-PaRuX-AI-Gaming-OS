# RBG – PaRuX AI & Gaming OS 4.5.0 release notes (pre-release candidate)

- **Date:** 2026-11-25
- **Channel:** pre-release candidate, built and signed locally. Not published until the owner decides; 4.4.0 remains the latest published pre-release.
- **Build commit:** the commit the artifacts were built from (recorded in `SHA256SUMS.txt` and the build log)
- **Version source:** `Directory.Build.props`, `PaRuXProductVersion` = 4.5.0 (Android versionName 4.5.0, versionCode 27)

## What is new since 4.4.0

- **Portable layout:** `PaRuX.exe` launcher at the root, application in `app\`, data in `PaRuXData\`. Applies to Standard Portable and Corporate Lite.
- **Corporate Lite:** CPU by default, bundled Vulkan on discrete GPUs, RAM admission with clear refusals, offline by construction, per-data-root single instance. The automatic small-model fallback is not active yet.
- **Console mode:** Start in Console mode without a TV; Playnite Fullscreen handoff with reversible theme changes; configurable pad hold time; opt-in Console power plan; opt-in Xbox Game Bar block (per user, with consent, journaled restore).
- **Games tools page** and per-game launch focus / post-launch actions.
- **AI / Chat:** self-healing tool calls, managed Python in chat, constrained output, loop guard, fail-closed MCP admission, Canvas preview, token usage, prompt variables, document pins, group chat, Paperless-ngx, Brave web search with sources, meeting assistant, dictation, Agent Flows, training parity.
- **Knowledge:** CAN traces (`.asc`, `.blf`), `.msg`, `.doc`, `.ppt`.
- **Corporate reliability:** ONNX retrieval engines recover on their own after a transient load failure; RAM admission no longer blocks the small alternative model.
- **Knowledge:** passages indexed without a vector are repaired automatically (bounded); local file protection hardened.
- **Sandbox:** non-canonical folder ACLs are tolerated, system folders are never restricted.
- **Evaluation:** runtime-dependency hash cache ignores files changed in the last 3 seconds.
- **UI:** Italian localisation gaps, Settings/Help always reachable, Projects page alignment.

## Signing

MSI, PE files in the packages and ZIP launchers are signed with the self-signed certificate (CN=Paolo Rubagotti); the APK with the owner's Android release key; the update manifest with PaRuX's own RSA key. SmartScreen reputation is NOT established.

## Limitations (NOT RUN or not established)

- No physical test with a real pad, Playnite Fullscreen or a training GPU for this build.
- No test on a real corporate PC.
- Win+G is not blocked: the Game Bar block is opt-in and covers the Game Bar settings, not the shortcut.
- Self-signed certificate; no SmartScreen reputation.
- Clean-machine MSI install/upgrade/uninstall: only a silent administrative extraction was checked, unless the owner adds evidence.
- Android: pairing with a real Core and a physical device not tested.
- `.pst/.ost` and MF4/MDF are not read; the bundled starter model is weak on multi-part and numeric questions.
- Competitors: no superiority is claimed.

## Upgrade notes

- MSI: in-app Updates or install over the old one. Portable: replace the files and keep `PaRuXData`. The layered portable layout differs from older flat packages: extract to a new folder and move `PaRuXData` across.
- Back up your data first if it holds anything you cannot recreate.
- Files above the GitHub limit are split; join with `join-release-asset.ps1`.

## Links

[Install](guides/INSTALL.md) · [Troubleshooting](guides/TROUBLESHOOTING.md) · [Plan](../PARUX_EXECUTION_PLAN.md) · [Changelog](../CHANGELOG.md)
