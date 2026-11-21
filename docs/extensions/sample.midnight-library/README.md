# Midnight Library (sample extension)

A games-side extension is a folder of data. Copy this folder to `Documents\PaRuX\Extensions\` (the folder name must equal the
`id`), then open Settings > Integrations > Game extensions and enable it.

Extensions never contain or run code. The folder may hold only `extension.json`, an optional `extension.sha256` (the publisher's
SHA-256 of the manifest), and readme/licence text. PaRuX shows the manifest's SHA-256; enabling is tied to it, so a changed
manifest switches itself off until you enable it again. Lookup links are opened by you in your browser; PaRuX never requests
them. Launch presets only fill the launch-profile form of a game when you press Apply, and the Core validates them on save.

Manifest fields: `schema` (1), `id`, `name`, `version` (1.2.3), `author`, optional `description` and `homepage`, and at least one of
`theme` (`appearance` dark/light, `accent`, optional `background` that keeps text readable), `metadataSources` (https
`urlTemplate` with `{title}`, up to 5) and `launchPresets` (up to 10, each with up to 8 plain arguments). Unknown fields are refused.
