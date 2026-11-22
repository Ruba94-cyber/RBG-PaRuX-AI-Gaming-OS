using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Playnite;

/// <summary>Pure parsing of the GitHub release metadata of JosefNemec/Playnite.</summary>
internal static class PlayniteReleaseParser
{
    private static readonly Regex InstallerName = new(@"^Playnite\d{3,6}\.exe$", RegexOptions.Compiled);
    private static readonly Regex Sha256Digest = new(@"^sha256:([0-9a-fA-F]{64})$", RegexOptions.Compiled);
    internal static readonly string[] AllowedDownloadHosts =
        { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    public static PlayniteInstallPlanDto Parse(string json, string targetFolder)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
                return Unavailable("The latest Playnite release is a draft.");
            if (root.TryGetProperty("prerelease", out JsonElement pre) && pre.ValueKind == JsonValueKind.True)
                return Unavailable("The latest Playnite release is a pre-release; PaRuX only installs stable releases.");
            string? tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 32 || !Regex.IsMatch(tag, @"^[0-9A-Za-z._-]+$"))
                return Unavailable("The release has no usable version.");
            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                return Unavailable("The release lists no files.");
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                if (name is null || !InstallerName.IsMatch(name)) continue;
                string? url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                if (!IsAllowedUrl(url)) continue;
                long size = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long v) ? v : 0;
                string? digest = asset.TryGetProperty("digest", out JsonElement dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                Match m = digest is null ? Match.Empty : Sha256Digest.Match(digest);
                return new PlayniteInstallPlanDto
                {
                    Available = true, Version = tag, AssetName = name, DownloadUrl = url, SizeBytes = size,
                    Sha256 = m.Success ? m.Groups[1].Value.ToLowerInvariant() : null,
                    TargetFolder = targetFolder,
                    Message = m.Success ? null : "The release does not publish a SHA-256 checksum; the download cannot be verified."
                };
            }
            return Unavailable("The release has no installer for Windows.");
        }
        catch (JsonException) { return Unavailable("The release information could not be read."); }
    }

    internal static bool IsAllowedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps &&
        AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) &&
        (uri.Host != "github.com" || uri.AbsolutePath.StartsWith("/JosefNemec/Playnite/releases/download/", StringComparison.Ordinal));

    private static PlayniteInstallPlanDto Unavailable(string message) => new() { Available = false, Message = message };
}

/// <summary>
/// Optional install of the official Playnite release, per user, after the owner confirmed the version and checksum shown to them.
/// It never touches an existing Playnite folder, never downloads without consent, and verifies the SHA-256 before the installer runs.
/// </summary>
internal sealed class PlayniteInstaller
{
    internal const string ReleaseApi = "https://api.github.com/repos/JosefNemec/Playnite/releases/latest";
    internal const long MaxDownloadBytes = 400L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _targetFolder;
    private readonly string _workFolder;
    private readonly Func<string, string[], CancellationToken, Task<int>> _runInstaller;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _single = new(1, 1);
    private PlayniteInstallStatusDto _status = new();
    private CancellationTokenSource? _cts;

    public PlayniteInstaller(HttpClient http, string? targetFolder = null, string? workFolder = null,
        Func<string, string[], CancellationToken, Task<int>>? runInstaller = null)
    {
        _http = http;
        _targetFolder = targetFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playnite");
        _workFolder = workFolder ?? PaRuXDataRoot.Combine("server", "playnite-download");
        _runInstaller = runInstaller ?? RunInstallerAsync;
    }

    public PlayniteInstallStatusDto Status { get { lock (_gate) return Copy(_status); } }

    private void Set(Action<PlayniteInstallStatusDto> change) { lock (_gate) change(_status); }

    public async Task<PlayniteInstallPlanDto> ResolveAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApi);
            request.Headers.UserAgent.ParseAdd("PaRuX-Playnite-Component/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new PlayniteInstallPlanDto { Message = "GitHub answered " + (int)response.StatusCode + "; try again later." };
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (json.Length > 2_000_000) return new PlayniteInstallPlanDto { Message = "The release information is unexpectedly large." };
            PlayniteInstallPlanDto plan = PlayniteReleaseParser.Parse(json, _targetFolder);
            if (plan.Available && DirectoryHasFiles(_targetFolder))
            {
                plan.Available = false;
                plan.Message = "A folder already exists at the install location. PaRuX never changes an existing Playnite; use Locate existing.";
            }
            return plan;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return new PlayniteInstallPlanDto { Message = "The release information could not be downloaded (no connection?)." };
        }
    }

    private static bool DirectoryHasFiles(string path)
    {
        try { return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Starts the install in the background; returns a refusal reason, or null when it started.</summary>
    public string? Begin(PlayniteInstallRequest request)
    {
        if (request is null || !request.Consent) return "Installing needs your explicit confirmation.";
        if (string.IsNullOrWhiteSpace(request.Version)) return "The version to install is missing.";
        if (!_single.Wait(0)) return "An installation is already running.";
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        Set(s => { s.Stage = "downloading"; s.BytesDownloaded = 0; s.TotalBytes = 0; s.Error = null; s.Version = request.Version; });
        _ = Task.Run(async () =>
        {
            try { await InstallAsync(request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { Set(s => { s.Stage = "cancelled"; s.Error = null; }); }
            catch (Exception ex) { Set(s => { s.Stage = "failed"; s.Error = "The installation failed: " + ex.GetType().Name; }); }
            finally { _single.Release(); }
        }, CancellationToken.None);
        return null;
    }

    public void Cancel() { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }

    internal async Task InstallAsync(PlayniteInstallRequest request, CancellationToken ct)
    {
        PlayniteInstallPlanDto plan = await ResolveAsync(ct).ConfigureAwait(false);
        if (!plan.Available) { Fail(plan.Message ?? "No installable Playnite release."); return; }
        if (!string.Equals(plan.Version, request.Version, StringComparison.Ordinal))
        { Fail("The latest release changed to " + plan.Version + " since you confirmed. Review it again."); return; }
        if (plan.Sha256 is null)
        {
            if (!request.AcceptUnverified) { Fail("The release has no published checksum; confirm the unverified install explicitly."); return; }
        }
        else if (!string.Equals(plan.Sha256, request.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
        { Fail("The checksum differs from the one you confirmed. Review the plan again."); return; }

        Directory.CreateDirectory(_workFolder);
        string file = Path.Combine(_workFolder, plan.AssetName!);
        try
        {
            string? problem = await DownloadAsync(plan, file, ct).ConfigureAwait(false);
            if (problem is not null) { Fail(problem); return; }

            Set(s => s.Stage = "verifying");
            string actual = await Sha256Async(file, ct).ConfigureAwait(false);
            if (plan.Sha256 is not null && !string.Equals(actual, plan.Sha256, StringComparison.OrdinalIgnoreCase))
            { Fail("The downloaded file does not match the published SHA-256. It was deleted and nothing was installed."); return; }

            if (DirectoryHasFiles(_targetFolder)) { Fail("The install folder is no longer empty. Nothing was installed."); return; }
            Set(s => s.Stage = "extracting");
            // Inno Setup switches: silent, per-user, fixed folder, no restart. The installer is the official signed Playnite setup.
            int exit = await _runInstaller(file, new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER", "/DIR=" + _targetFolder }, ct).ConfigureAwait(false);
            if (exit != 0) { Fail("The Playnite installer reported exit code " + exit + "."); return; }
            if (!File.Exists(Path.Combine(_targetFolder, PlayniteLocator.DesktopExeName)))
            { Fail("The installer finished but Playnite was not found in the install folder."); return; }
            Set(s => { s.Stage = "completed"; s.Error = null; });
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Fail(string message) => Set(s => { s.Stage = "failed"; s.Error = message; });

    private async Task<string?> DownloadAsync(PlayniteInstallPlanDto plan, string file, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, plan.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("PaRuX-Playnite-Component/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return "The download failed (HTTP " + (int)response.StatusCode + ").";
        Uri? final = response.RequestMessage?.RequestUri;
        if (final is null || final.Scheme != Uri.UriSchemeHttps ||
            !PlayniteReleaseParser.AllowedDownloadHosts.Contains(final.Host, StringComparer.OrdinalIgnoreCase))
            return "The download was redirected to an untrusted host and was refused.";
        long total = response.Content.Headers.ContentLength ?? plan.SizeBytes;
        if (total > MaxDownloadBytes || total <= 0) return "The download size is not acceptable.";
        Set(s => s.TotalBytes = total);
        await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        byte[] buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            done += read;
            if (done > MaxDownloadBytes) return "The download is larger than expected and was stopped.";
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            long snapshot = done;
            Set(s => s.BytesDownloaded = snapshot);
        }
        return done == total ? null : "The download ended early; nothing was installed.";
    }

    private static async Task<string> Sha256Async(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task<int> RunInstallerAsync(string exe, string[] args, CancellationToken ct)
    {
        var info = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        using Process? process = Process.Start(info);
        if (process is null) return -1;
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static PlayniteInstallStatusDto Copy(PlayniteInstallStatusDto s) => new()
    { Stage = s.Stage, BytesDownloaded = s.BytesDownloaded, TotalBytes = s.TotalBytes, Error = s.Error, Version = s.Version };
}
