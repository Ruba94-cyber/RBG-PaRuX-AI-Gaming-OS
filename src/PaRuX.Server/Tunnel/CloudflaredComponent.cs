using System.Security.Cryptography;
using PaRuX.Contracts;

namespace PaRuX.Server.Tunnel;

internal sealed record CloudflaredDetection(
    bool Detected, ComponentOwnership? Ownership, string? Path, string? Version, bool Skipped);

/// <summary>
/// cloudflared as a PaRuX component: Detect, Locate existing, Install with PaRuX, or Skip.
/// PARUX_MANAGED lives under the PaRuX data root and is hash-verified against the pin before every use.
/// USER_MANAGED is a path the person chose; PaRuX only runs it and never installs over, updates or deletes it.
/// </summary>
internal class CloudflaredComponent
{
    private readonly string _managedRoot;
    private readonly Func<bool> _corporate;
    private readonly Func<string?> _locatedPath;
    private readonly Func<bool> _skipped;
    private readonly Func<string, string, long, string, CancellationToken, Task> _download;

    internal CloudflaredComponent()
        : this(Path.Combine(LocalAiPaths.Root, "components", CloudflaredPin.ComponentId),
            () => PaRuX.Contracts.CorporateMode.IsActive,
            () => LocalAiPaths.GetComponentLocation(CloudflaredPin.ComponentId),
            () => LocalAiPaths.IsComponentSkipped(CloudflaredPin.ComponentId),
            DownloadWithPin) { }

    internal CloudflaredComponent(string managedRoot, Func<bool> corporate, Func<string?> locatedPath, Func<bool> skipped,
        Func<string, string, long, string, CancellationToken, Task> download)
    {
        _managedRoot = managedRoot;
        _corporate = corporate;
        _locatedPath = locatedPath;
        _skipped = skipped;
        _download = download;
    }

    internal string ManagedPath => System.IO.Path.Combine(_managedRoot, CloudflaredPin.Version, "cloudflared.exe");

    internal virtual CloudflaredDetection Detect()
    {
        bool skipped = _skipped();
        string? located = _locatedPath();
        if (!string.IsNullOrWhiteSpace(located) && File.Exists(located))
            return new CloudflaredDetection(true, ComponentOwnership.USER_MANAGED, located, null, skipped);
        if (File.Exists(ManagedPath))
            return new CloudflaredDetection(true, ComponentOwnership.PARUX_MANAGED, ManagedPath, CloudflaredPin.Version, skipped);
        return new CloudflaredDetection(false, null, null, null, skipped);
    }

    /// <summary>
    /// The executable to launch, or null. A PARUX_MANAGED file is re-hashed against the pin first; a mismatch (corruption or
    /// replacement) is refused rather than run. A USER_MANAGED file is the person's own software and is used as chosen.
    /// </summary>
    internal virtual string? ResolveForLaunch(out string failure)
    {
        failure = string.Empty;
        CloudflaredDetection found = Detect();
        if (!found.Detected || found.Path is null) return null;
        if (found.Ownership == ComponentOwnership.PARUX_MANAGED && !VerifyManaged(found.Path))
        {
            failure = "The PaRuX-managed cloudflared file does not match its pinned SHA-256. Install it again.";
            return null;
        }
        return found.Path;
    }

    internal static bool VerifyManaged(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Length != CloudflaredPin.SizeBytes) return false;
            string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(CloudflaredPin.Sha256));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Install with PaRuX. Refused in Corporate mode. Writes only inside the PaRuX data root.</summary>
    internal async Task InstallAsync(CancellationToken ct)
    {
        if (_corporate()) throw new InvalidOperationException("The Internet tunnel is not available in PaRuX Corporate Lite.");
        if (File.Exists(ManagedPath) && VerifyManaged(ManagedPath)) { LocalAiPaths.ClearComponentSkipped(CloudflaredPin.ComponentId); return; }
        await _download(CloudflaredPin.Url, ManagedPath, CloudflaredPin.SizeBytes, CloudflaredPin.Sha256, ct).ConfigureAwait(false);
        try { LocalAiPaths.ClearComponentSkipped(CloudflaredPin.ComponentId); } catch (Exception ex) when (ex is IOException or InvalidDataException) { }
    }

    /// <summary>Locate existing: bind to a cloudflared the person already has. Nothing is copied, changed or deleted.</summary>
    internal static void Locate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new InvalidDataException("The selected file does not exist.");
        if (!System.IO.Path.GetFileName(path).StartsWith("cloudflared", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select the cloudflared executable (cloudflared.exe).");
        LocalAiPaths.SetComponentLocation(CloudflaredPin.ComponentId, path);
        LocalAiPaths.ClearComponentSkipped(CloudflaredPin.ComponentId);
    }

    internal static void Skip() => LocalAiPaths.SetComponentSkipped(CloudflaredPin.ComponentId);

    private static async Task DownloadWithPin(string url, string destination, long size, string sha256, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PaRuX-component-installer");
        await VerifiedDownload.DownloadAsync(http, url, destination, sha256, size, ct).ConfigureAwait(false);
    }
}
