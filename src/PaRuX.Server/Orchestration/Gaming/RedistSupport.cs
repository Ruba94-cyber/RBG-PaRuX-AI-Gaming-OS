using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>One component of packaging/redist-installers.lock.json, reduced to the fields the policies need.</summary>
public sealed record RedistLockComponent(
    string Id, string Version, string ReleaseTag, string MsiFeature, string InstallType, bool Elevated, IReadOnlyList<string> SilentArgs,
    string AssetName, string AssetUrl, long SizeBytes, string Sha256, string? GithubDigest,
    bool SignatureRequired, string? SignatureSubject, string? SignatureThumbprint,
    string LicenseSpdx, string LicenseTextFile, string? SourceOfferRepository, string? SourceOfferArchive, string? SourceOfferTextFile, string Status);

/// <summary>
/// Pure rules for the vendor payloads the optional MSI features carry. Nothing here downloads, installs or reads the machine: it
/// parses the lock file and decides whether a lock entry, a file's measured facts or a signature result are acceptable. The same
/// rules are mirrored by scripts/stage-msi-redist.ps1 (build time) and scripts/redist/Invoke-PaRuXRedist.ps1 (install time).
/// </summary>
public static class RedistLockPolicy
{
    public static readonly IReadOnlySet<string> AllowedLicenses =
        new HashSet<string>(StringComparer.Ordinal) { "BSD-3-Clause", "MIT", "GPL-3.0-only" };

    public static readonly IReadOnlySet<string> AllowedSilentArgs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/exenoui", "/qn", "/norestart" };

    // The only repositories an asset may come from, per component. Anything else (a fork, another owner, a CDN) is refused.
    private static readonly IReadOnlyDictionary<string, string[]> OfficialRepos = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["vigembus"] = ["nefarius/ViGEmBus"],
        ["hidhide"] = ["nefarius/HidHide"],
        ["sunshine"] = ["SunshineStream/Sunshine", "LizardByte/Sunshine"],
    };

    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Thumb = new("^[0-9A-F]{40}$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<RedistLockComponent> Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var list = new List<RedistLockComponent>();
        foreach (JsonElement c in doc.RootElement.GetProperty("components").EnumerateArray())
        {
            JsonElement inst = c.GetProperty("install"), asset = c.GetProperty("asset"), sig = c.GetProperty("signature"), lic = c.GetProperty("license");
            JsonElement offer = c.GetProperty("sourceOffer");
            bool hasOffer = offer.ValueKind == JsonValueKind.Object;
            list.Add(new RedistLockComponent(
                c.GetProperty("id").GetString()!, c.GetProperty("version").GetString()!, c.GetProperty("releaseTag").GetString()!,
                c.GetProperty("msiFeature").GetString()!, inst.GetProperty("type").GetString()!, inst.GetProperty("elevated").GetBoolean(),
                inst.GetProperty("silentArgs").EnumerateArray().Select(a => a.GetString()!).ToArray(),
                asset.GetProperty("name").GetString()!, asset.GetProperty("url").GetString()!, asset.GetProperty("sizeBytes").GetInt64(),
                asset.GetProperty("sha256").GetString()!, asset.GetProperty("githubDigest").ValueKind == JsonValueKind.String ? asset.GetProperty("githubDigest").GetString() : null,
                sig.GetProperty("required").GetBoolean(),
                sig.GetProperty("subject").ValueKind == JsonValueKind.String ? sig.GetProperty("subject").GetString() : null,
                sig.GetProperty("thumbprint").ValueKind == JsonValueKind.String ? sig.GetProperty("thumbprint").GetString() : null,
                lic.GetProperty("spdx").GetString()!, lic.GetProperty("textFile").GetString()!,
                hasOffer ? offer.GetProperty("sourceRepository").GetString() : null,
                hasOffer ? offer.GetProperty("sourceArchive").GetString() : null,
                hasOffer ? offer.GetProperty("textFile").GetString() : null,
                c.GetProperty("status").GetString()!));
        }
        return list;
    }

    /// <summary>Every problem with one lock entry (empty = acceptable). Pins must be exact: no 'latest', no unpinned tag.</summary>
    public static IReadOnlyList<string> Validate(RedistLockComponent c)
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(c.Version) || c.Version.Contains('*')) p.Add("version is not an exact version");
        if (!c.ReleaseTag.StartsWith('v') || c.ReleaseTag[1..] != c.Version) p.Add("releaseTag does not match the exact version");
        if (!Hex64.IsMatch(c.Sha256)) p.Add("sha256 must be 64 lowercase hex characters");
        if (c.SizeBytes <= 0 || c.SizeBytes > 400L * 1024 * 1024) p.Add("sizeBytes out of range");
        if (c.GithubDigest is not null && c.GithubDigest != "sha256:" + c.Sha256) p.Add("the GitHub-published digest differs from the pinned sha256");

        if (!Uri.TryCreate(c.AssetUrl, UriKind.Absolute, out Uri? u) || u.Scheme != Uri.UriSchemeHttps || !u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            p.Add("the asset URL must be https://github.com/...");
        else
        {
            string[] seg = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // /{owner}/{repo}/releases/download/{tag}/{asset}
            bool shape = seg.Length == 6 && seg[2] == "releases" && seg[3] == "download";
            if (!shape) p.Add("the asset URL is not a pinned release-download URL");
            else
            {
                if (!OfficialRepos.TryGetValue(c.Id, out string[]? repos) || !repos.Contains(seg[0] + "/" + seg[1], StringComparer.Ordinal)) p.Add("the asset is not from the official repository");
                if (seg[4] != c.ReleaseTag) p.Add("the asset URL tag differs from releaseTag");
                if (seg[5] != c.AssetName) p.Add("the asset URL file differs from the asset name");
            }
        }
        if (c.AssetName.Contains('/') || c.AssetName.Contains('\\') || c.AssetName.Contains("..")) p.Add("asset name must be a plain file name");

        bool exeInstaller = c.InstallType == "advanced-installer-exe";
        if (c.Elevated && !exeInstaller) p.Add("only a signature-checked vendor installer may run elevated");
        if (exeInstaller)
        {
            if (!c.SignatureRequired) p.Add("an elevated vendor installer must require a valid Authenticode signature");
            if (string.IsNullOrWhiteSpace(c.SignatureSubject) || !c.SignatureSubject.StartsWith("CN=", StringComparison.Ordinal)) p.Add("signature subject pin missing");
            if (c.SignatureThumbprint is null || !Thumb.IsMatch(c.SignatureThumbprint)) p.Add("signature thumbprint pin missing");
            if (!c.AssetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p.Add("an installer must be an .exe");
            if (c.SilentArgs.Count == 0 || c.SilentArgs.Any(a => !AllowedSilentArgs.Contains(a))) p.Add("silent arguments must come from the documented allow-list");
            if (!c.SilentArgs.Contains("/qn") || !c.SilentArgs.Contains("/norestart")) p.Add("silent installs must be /qn and /norestart (no forced reboot)");
        }
        else if (c.SilentArgs.Count != 0) p.Add("a payload that is not executed must have no silent arguments");

        if (!AllowedLicenses.Contains(c.LicenseSpdx)) p.Add("licence is not in the reviewed allow-list");
        if (string.IsNullOrWhiteSpace(c.LicenseTextFile)) p.Add("licence text file missing");
        if (c.LicenseSpdx.StartsWith("GPL", StringComparison.Ordinal))
        {
            if (c.SourceOfferRepository is null || !c.SourceOfferRepository.Contains(c.ReleaseTag, StringComparison.Ordinal)) p.Add("GPL payload needs a source link at the same tag");
            if (c.SourceOfferArchive is null || !c.SourceOfferArchive.Contains(c.ReleaseTag, StringComparison.Ordinal)) p.Add("GPL payload needs a source archive at the same tag");
            if (string.IsNullOrWhiteSpace(c.SourceOfferTextFile)) p.Add("GPL payload needs a written source offer file");
        }
        if (string.IsNullOrWhiteSpace(c.MsiFeature)) p.Add("an optional MSI feature id is required (payloads are never default-installed)");
        return p;
    }

    /// <summary>Size and SHA-256 gate for a file already measured by the caller.</summary>
    public static string? CheckPayload(RedistLockComponent c, long actualSize, string actualSha256Hex)
    {
        if (actualSize != c.SizeBytes) return $"{c.Id}: size {actualSize} differs from the pinned {c.SizeBytes}.";
        if (!string.Equals(actualSha256Hex, c.Sha256, StringComparison.OrdinalIgnoreCase)) return $"{c.Id}: SHA-256 differs from the pinned value.";
        return null;
    }

    /// <summary>Authenticode gate. <paramref name="status"/> is the Windows verdict name ("Valid", "NotSigned", "HashMismatch", ...).</summary>
    public static string? CheckSignature(RedistLockComponent c, string status, string? subject, string? thumbprint)
    {
        if (!c.SignatureRequired) return null;
        if (!string.Equals(status, "Valid", StringComparison.Ordinal)) return $"{c.Id}: Authenticode status is '{status}', not 'Valid'.";
        if (subject is null || !subject.StartsWith(c.SignatureSubject!, StringComparison.Ordinal)) return $"{c.Id}: signer is not the pinned publisher.";
        if (!string.Equals(thumbprint, c.SignatureThumbprint, StringComparison.OrdinalIgnoreCase)) return $"{c.Id}: signing certificate thumbprint is not the pinned one.";
        return null;
    }
}

/// <summary>What the machine says about one bundled-capable component (probe result) and what PaRuX's own setup recorded.</summary>
public sealed record RedistProbeResult(bool Present, string? Path, string? Version);
public sealed record RedistSetupRecord(bool InstalledByPaRuX, string? Version, bool RebootRequired);

public interface IRedistProbe
{
    RedistProbeResult Probe(string redistId);
    RedistSetupRecord? ReadSetupRecord(string redistId);
    bool BundledPayloadAvailable(string redistId);
}

public static class RedistStatusPolicy
{
    /// <summary>
    /// Ownership and state. A copy recorded by PaRuX's setup is PARUX_MANAGED; one that was already there (or the owner chose) is never
    /// claimed: SYSTEM_DETECTED when found by looking, USER_MANAGED when the owner pointed PaRuX at it.
    /// </summary>
    public static RedistStatusRow Decide(string id, string name, RedistProbeResult probe, RedistSetupRecord? setup, bool ownerPickedPath, bool managedByPaRuXFolder, bool bundledAvailable)
    {
        bool byParux = setup is { InstalledByPaRuX: true } || managedByPaRuXFolder;
        if (byParux && probe.Present)
            return new(id, name, RedistState.Installed, ComponentOwnership.PARUX_MANAGED, probe.Version ?? setup?.Version, probe.Path, false, bundledAvailable,
                "Installed by PaRuX; PaRuX may remove it.", "Installato da PaRuX; PaRuX può rimuoverlo.");
        if (byParux && setup is { RebootRequired: true })
            return new(id, name, RedistState.RestartRequired, ComponentOwnership.PARUX_MANAGED, setup.Version, null, true, bundledAvailable,
                "Installed by the PaRuX setup. Restart Windows to finish loading the driver.", "Installato dal setup di PaRuX. Riavvia Windows per completare il caricamento del driver.");
        if (byParux)
            return new(id, name, RedistState.MissingAfterInstall, ComponentOwnership.PARUX_MANAGED, setup?.Version, null, false, bundledAvailable,
                "The PaRuX setup installed it but it is no longer present. Reinstall the feature from Windows Settings > Apps.", "Il setup di PaRuX l'aveva installato ma non è più presente. Reinstalla la funzione da Impostazioni di Windows > App.");
        if (probe.Present)
            return new(id, name, RedistState.Installed, ownerPickedPath ? ComponentOwnership.USER_MANAGED : ComponentOwnership.SYSTEM_DETECTED, probe.Version, probe.Path, false, bundledAvailable,
                "Already on this PC. PaRuX uses it and never changes, updates or removes it.", "Già presente su questo PC. PaRuX lo usa e non lo modifica, aggiorna né rimuove mai.");
        return new(id, name, RedistState.NotInstalled, null, null, null, false, bundledAvailable,
            bundledAvailable ? "Not installed. The package is bundled with this setup; install it from here (no administrator rights)." : "Not installed. Optional: add it with the PaRuX setup (Change) or install it yourself.",
            bundledAvailable ? "Non installato. Il pacchetto è incluso in questo setup; installalo da qui (senza diritti di amministratore)." : "Non installato. Facoltativo: aggiungilo con il setup di PaRuX (Modifica) o installalo tu.");
    }
}
