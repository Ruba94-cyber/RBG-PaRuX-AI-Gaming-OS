namespace PaRuX.Server.Tunnel;

/// <summary>
/// The one reviewed cloudflared build PaRuX installs. Mirrors <c>packaging/cloudflared.lock.json</c> (a test keeps the two
/// equal). The SHA-256 and byte count are enforced by <see cref="VerifiedDownload"/> before the file is published, so a
/// tampered or substituted asset is never written to the install location.
/// </summary>
internal static class CloudflaredPin
{
    internal const string ComponentId = "cloudflared";
    internal const string Version = "2026.11.2";
    internal const string AssetName = "cloudflared-windows-amd64.exe";
    internal const string Url = "https://github.com/cloudflare/cloudflared/releases/download/" + Version + "/" + AssetName;
    internal const long SizeBytes = 55470800;
    internal const string Sha256 = "555b46c85582461d189cac30d14ad05480d612613801be9f110b8ce08c5b2113";
}
