using System.IO.Compression;
using System.Security.Cryptography;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

public sealed record SupportAppInstallResult(bool Ok, string Message, string? ExePath = null);

/// <summary>
/// Downloads a catalog support app into PaRuX's own folder (PARUX_MANAGED), only after explicit consent to the exact version, and only
/// when the catalog pins an official https URL, a SHA-256 and a size. The file is hashed before anything is extracted; extraction
/// refuses unsafe paths, links and oversized archives; the result is moved into place only when complete. It never touches a copy the
/// owner keeps elsewhere, never needs administrator rights and never installs a driver or service. The HTTP client is injected, so
/// tests use a fake and download nothing.
/// </summary>
public sealed class SupportAppInstaller
{
    public const int MaxEntries = 2000;
    public const long MaxUnpackedBytes = 600L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _root;
    private readonly SupportAppStore _store;
    private readonly ISupportAppHost _host;
    private readonly Func<bool> _gamingActive;
    private readonly Func<string?, SupportAppCatalogEntry?> _catalog;

    private readonly Func<SupportAppCatalogEntry, string?> _bundled;

    /// <summary>
    /// The unmodified vendor package the PaRuX setup placed beside the app (optional MSI feature), or null. Looked up in the fixed
    /// folder <c>Redist\&lt;catalog id&gt;</c> next to the Server folder, never from a path a client supplies.
    /// </summary>
    public static string? DefaultBundledPackage(SupportAppCatalogEntry entry)
    {
        if (entry.Download is null || entry.RedistId is null) return null;
        string name = Path.GetFileName(new Uri(entry.Download.Url).AbsolutePath);
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Redist", entry.Id, name));
        return File.Exists(path) ? path : null;
    }

    public SupportAppInstaller(HttpClient http, string managedRoot, SupportAppStore store, ISupportAppHost host, Func<bool> gamingActive,
        Func<string?, SupportAppCatalogEntry?>? catalog = null, Func<SupportAppCatalogEntry, string?>? bundledPackage = null)
    {
        _bundled = bundledPackage ?? DefaultBundledPackage;
        _catalog = catalog ?? SupportAppCatalog.Find;
        _http = http; _root = Path.GetFullPath(managedRoot); _store = store; _host = host; _gamingActive = gamingActive;
    }

    public static string? ValidateCatalogDownload(SupportAppDownload d)
    {
        if (!Uri.TryCreate(d.Url, UriKind.Absolute, out Uri? u) || u.Scheme != Uri.UriSchemeHttps) return "The download URL must be https.";
        if (d.Sha256.Length != 64 || !d.Sha256.All(Uri.IsHexDigit)) return "The pinned SHA-256 is not valid.";
        if (d.SizeBytes <= 0 || d.SizeBytes > 400L * 1024 * 1024) return "The pinned size is not valid.";
        if (string.IsNullOrWhiteSpace(d.ExeRelativePath) || Path.IsPathRooted(d.ExeRelativePath) || d.ExeRelativePath.Contains("..")) return "The program path inside the package is not valid.";
        return null;
    }

    public async Task<SupportAppInstallResult> InstallAsync(SupportAppInstallRequest request, CancellationToken ct)
    {
        if (_gamingActive()) return new(false, "Leave Gaming before downloading a support app.");
        SupportAppCatalogEntry? entry = _catalog(request?.CatalogId);
        if (entry is null) return new(false, "Unknown support app.");
        SupportAppDownload? d = entry.Download;
        if (d is null) return new(false, entry.NoDownloadReason?.En ?? "PaRuX does not download this app. Locate your own copy.");
        if (request is null || !request.Confirm || !string.Equals(request.Version, d.Version, StringComparison.Ordinal))
            return new(false, "Download needs your explicit consent to this exact version.");
        string? bad = ValidateCatalogDownload(d);
        if (bad is not null) return new(false, bad);

        string target = Path.Combine(_root, entry.Id, d.Version);
        string exe = Path.GetFullPath(Path.Combine(target, d.ExeRelativePath));   // "Sunshine/sunshine.exe" -> backslashes
        if (File.Exists(exe)) return Register(entry, d, exe, "Already downloaded.");

        string staging = Path.Combine(_root, ".staging-" + Guid.NewGuid().ToString("N"));
        string archive = staging + ".zip";
        try
        {
            Directory.CreateDirectory(_root);
            string? local = _bundled(entry);
            if (local is not null)
            {
                // Offline path: the package the PaRuX setup carried. Same pinned size and SHA-256 gate as a download; no network.
                if (new FileInfo(local).Length != d.SizeBytes) return new(false, "The package shipped with PaRuX does not match the pinned size; nothing was installed.");
                File.Copy(local, archive, overwrite: false);
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                using HttpResponseMessage response = await _http.GetAsync(d.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return new(false, "The download server answered " + (int)response.StatusCode + ".");
                if (response.Content.Headers.ContentLength is long len && len > d.SizeBytes) return new(false, "The file is larger than the pinned size; nothing was kept.");

                using (var fs = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (Stream src = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    long total = 0; int n;
                    while ((n = await src.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > d.SizeBytes) return new(false, "The file is larger than the pinned size; nothing was kept.");
                        await fs.WriteAsync(buffer.AsMemory(0, n), timeout.Token).ConfigureAwait(false);
                    }
                    if (total != d.SizeBytes) return new(false, "The downloaded size does not match the pinned size; nothing was kept.");
                }
            }

            string actual;
            using (var fs = File.OpenRead(archive)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
            if (!string.Equals(actual, d.Sha256, StringComparison.OrdinalIgnoreCase)) return new(false, "The SHA-256 of the download does not match the pinned value; nothing was kept.");

            string? extractError = Extract(archive, staging);
            if (extractError is not null) return new(false, extractError);
            string stagedExe = Path.Combine(staging, d.ExeRelativePath);
            if (!File.Exists(stagedExe)) return new(false, "The package does not contain the expected program; nothing was kept.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);
            return Register(entry, d, exe, "Downloaded and verified.");
        }
        catch (OperationCanceledException) { return new(false, "The download was cancelled or took too long; nothing was kept."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(false, "The download failed: " + ex.GetType().Name + "; nothing was kept.");
        }
        finally
        {
            TryDelete(archive);
            TryDeleteDir(staging);
        }
    }

    private SupportAppInstallResult Register(SupportAppCatalogEntry entry, SupportAppDownload d, string exe, string message)
    {
        // Added OFF for "start with Console": the owner turns it on after looking at it.
        var e = new SupportAppEntry(entry.Id, entry.Name, exe, null, null, Enabled: true, StartWithConsole: false, StopWithConsole: false,
            Minimized: true, TimeoutSeconds: SupportAppLimits.DefaultTimeoutSeconds, ReplaceIfStale: false, CatalogId: entry.Id, Version: d.Version);
        var (state, error) = _store.Add(SupportAppPolicy.Normalize(e));
        return state is null ? new(false, error ?? "The app was downloaded but could not be added to the list.", exe) : new(true, message, exe);
    }

    /// <summary>Removes the PaRuX-managed copy of one catalog app: a strict child of PaRuX's own folder, never a link, never a running program.</summary>
    public SupportAppInstallResult Remove(string catalogId)
    {
        SupportAppCatalogEntry? entry = _catalog(catalogId);
        if (entry is null) return new(false, "Unknown support app.");
        string folder = Path.Combine(_root, entry.Id);
        if (!SupportAppPolicy.IsInside(Path.Combine(folder, "x"), _root)) return new(false, "The folder is not inside PaRuX's own folder.");
        if (!Directory.Exists(folder)) return new(false, "This app was not downloaded by PaRuX.");
        if ((new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0) return new(false, "The folder is a link, so PaRuX will not delete it.");
        string? probe = entry.Download is null ? null : Path.GetFileNameWithoutExtension(entry.Download.ExeRelativePath);
        if (probe is not null && _host.Find(probe).Any(p => SupportAppPolicy.IsInside(p.Path, folder)))
            return new(false, "Close the app first, then remove it.");
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(false, "Could not remove the files (in use or protected)."); }
        _store.RemoveCatalog(entry.Id);
        return new(true, "Removed.");
    }

    /// <summary>Zip extraction with the usual guards: no absolute or parent paths, no links, bounded count and size.</summary>
    internal static string? Extract(string archivePath, string destination)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destination);
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count > MaxEntries) return "The package has too many files; nothing was kept.";
        long total = 0;
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            total += e.Length;
            if (total > MaxUnpackedBytes) return "The package is too large once unpacked; nothing was kept.";
            string full = Path.GetFullPath(Path.Combine(destination, e.FullName));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "The package contains an unsafe path; nothing was kept.";
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(full); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            e.ExtractToFile(full, overwrite: false);
        }
        return null;
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
