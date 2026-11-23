using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Playnite;

/// <summary>PaRuX's own record about the Aniki ReMake theme (what it installed, what it selected, backups). One small atomic JSON file.</summary>
public sealed class PlayniteThemeStore
{
    private const long MaxFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public sealed class State
    {
        /// <summary>Folder name PaRuX installed (PARUX_MANAGED). Null when PaRuX installed nothing.</summary>
        public string? ManagedFolderName { get; set; }
        public string? ManagedVersion { get; set; }
        /// <summary>Backup folder (under Themes\Fullscreen) of a theme folder PaRuX replaced after consent.</summary>
        public string? BackupFolder { get; set; }
        /// <summary>Playnite data folder the owner located (portable or non-default).</summary>
        public string? DataFolder { get; set; }
        public string? SelectedByPaRuX { get; set; }
        public string? PreviousTheme { get; set; }
        public bool Skipped { get; set; }
    }

    private readonly string _path;
    private readonly object _gate = new();
    private State _state = new();

    public PlayniteThemeStore() : this(PaRuXDataRoot.Combine("server", "playnite-theme.json")) { }

    public PlayniteThemeStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= MaxFileBytes)
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _state = new(); }
    }

    public State Get() { lock (_gate) return Copy(_state); }

    public bool Update(Action<State> change)
    {
        lock (_gate)
        {
            State next = Copy(_state);
            change(next);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(next, JsonOptions));
                File.Move(temp, _path, overwrite: true);
                _state = next;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    private static State Copy(State s) => new()
    {
        ManagedFolderName = s.ManagedFolderName, ManagedVersion = s.ManagedVersion, BackupFolder = s.BackupFolder,
        DataFolder = s.DataFolder, SelectedByPaRuX = s.SelectedByPaRuX, PreviousTheme = s.PreviousTheme, Skipped = s.Skipped
    };
}

/// <summary>
/// Optional "Aniki ReMake" Playnite Fullscreen theme: detect, locate, install after consent (staged, atomic, with backup and rollback),
/// and select it in Playnite through one consented, backed-up, reversible change that is never made while Playnite runs. It never
/// overwrites an existing theme folder without explicit consent, never writes outside Themes\Fullscreen (plus that single config value)
/// and never starts a process.
/// </summary>
internal sealed class PlayniteThemeService
{
    internal const long MaxDownloadBytes = 600L * 1024 * 1024;
    internal const string ReleaseApi = "https://api.github.com/repos/" + PlayniteThemeReleaseParser.Repository + "/releases/latest";
    internal const string LicenseFileName = "LICENSE-Aniki-ReMake-MIT.txt";
    private const string StagingPrefix = ".parux-staging-";
    private const string BackupPrefix = ".parux-backup-";

    private readonly HttpClient _http;
    private readonly IPlayniteEnvironment _env;
    private readonly PlayniteThemeStore _store;
    private readonly Func<string?> _locatedPlaynite;
    private readonly string _workFolder;
    private readonly string _backupFolder;
    private readonly Func<string, long?> _freeSpace;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _single = new(1, 1);
    private PlayniteThemeInstallStatusDto _status = new();
    private CancellationTokenSource? _cts;

    public PlayniteThemeService(HttpClient http, IPlayniteEnvironment env, PlayniteThemeStore store, Func<string?> locatedPlaynite,
        string? workFolder = null, string? backupFolder = null, Func<string, long?>? freeSpace = null)
    {
        _http = http;
        _env = env;
        _store = store;
        _locatedPlaynite = locatedPlaynite;
        _workFolder = workFolder ?? PaRuXDataRoot.Combine("server", "playnite-theme-download");
        _backupFolder = backupFolder ?? PaRuXDataRoot.Combine("server", "playnite-config-backups");
        _freeSpace = freeSpace ?? DefaultFreeSpace;
    }

    public PlayniteThemeInstallStatusDto InstallStatus { get { lock (_gate) return Copy(_status); } }
    private void Set(Action<PlayniteThemeInstallStatusDto> change) { lock (_gate) change(_status); }

    private static long? DefaultFreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- locations ----

    /// <summary>The Playnite data folder: the owner's located one, a portable Playnite's own folder, else %APPDATA%\Playnite.</summary>
    internal string DataFolder()
    {
        string? chosen = _store.Get().DataFolder;
        if (!string.IsNullOrWhiteSpace(chosen)) return chosen;
        PlayniteComponentDto playnite = new PlayniteLocator(_env).Detect(_locatedPlaynite());
        if (playnite is { Detected: true, Folder: not null } && _env.FileExists(Path.Combine(playnite.Folder, "portable.dat"))) return playnite.Folder;
        return Path.Combine(_env.AppData, "Playnite");
    }

    internal static string ThemesFolder(string dataFolder) => Path.Combine(dataFolder, "Themes", "Fullscreen");

    private bool PlayniteRunning() => _env.RunningPlaynite().Count > 0;

    private static bool IsAnikiFolderName(string name) =>
        name.StartsWith("Aniki_ReMake_", StringComparison.OrdinalIgnoreCase) && PlayniteThemeConfig.IsSafeThemeName(name);

    private static (string Name, string Version)? ReadYaml(string folder)
    {
        try
        {
            string file = Path.Combine(folder, "theme.yaml");
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 64 * 1024) return null;
            var meta = PlayniteThemeArchive.ParseThemeYaml(File.ReadAllText(file));
            if (meta is null || !string.Equals(meta.Value.Mode, "Fullscreen", StringComparison.OrdinalIgnoreCase)) return null;
            return (meta.Value.Name, meta.Value.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? FindLicense(string folder)
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(folder))
            {
                string n = Path.GetFileName(f);
                if (n.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || n.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase)) return n;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    // ---- detection (read-only) ----

    public PlayniteThemeComponentDto Detect()
    {
        PlayniteThemeStore.State st = _store.Get();
        string data = DataFolder();
        var c = new PlayniteThemeComponentDto
        {
            PlayniteDataFolder = data, PlayniteDataFolderFound = Directory.Exists(data), Skipped = st.Skipped,
            PlayniteRunning = PlayniteRunning()
        };
        (bool readable, string? selected) = PlayniteThemeConfig.Read(PlayniteThemeConfig.FilePath(data));
        c.ConfigReadable = readable;
        c.SelectedTheme = selected;
        string themes = ThemesFolder(data);
        string? folder = null;
        try
        {
            if (Directory.Exists(themes))
            {
                var candidates = Directory.EnumerateDirectories(themes)
                    .Where(d => IsAnikiFolderName(Path.GetFileName(d)) && ReadYaml(d) is not null).ToList();
                folder = candidates.FirstOrDefault(d => string.Equals(Path.GetFileName(d), st.ManagedFolderName, StringComparison.OrdinalIgnoreCase))
                    ?? candidates.FirstOrDefault();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (folder is not null)
        {
            string name = Path.GetFileName(folder);
            c.Detected = true;
            c.ThemeFolder = folder;
            c.ThemeFolderName = name;
            c.Version = ReadYaml(folder)?.Version;
            c.Ownership = string.Equals(name, st.ManagedFolderName, StringComparison.OrdinalIgnoreCase) ? ComponentOwnership.PARUX_MANAGED : ComponentOwnership.USER_MANAGED;
            c.LicenseFile = FindLicense(folder);
            c.Selected = string.Equals(selected, name, StringComparison.OrdinalIgnoreCase);
        }
        c.PreviousTheme = st.PreviousTheme;
        c.CanRestoreSelection = st.SelectedByPaRuX is not null && st.PreviousTheme is not null &&
            string.Equals(selected, st.SelectedByPaRuX, StringComparison.OrdinalIgnoreCase);
        c.CanRollback = c.Ownership == ComponentOwnership.PARUX_MANAGED && c.Detected && !c.Selected;

        var actions = new List<string>();
        if (!c.Detected) { actions.Add("install"); actions.Add("locate"); actions.Add("skip"); }
        else
        {
            if (!c.Selected) actions.Add("select");
            if (c.CanRestoreSelection) actions.Add("restore-selection");
            if (c.CanRollback) actions.Add("rollback");
            actions.Add("locate");
            actions.Add("skip");
        }
        c.Actions = actions.ToArray();
        c.Message = !c.PlayniteDataFolderFound
            ? "Playnite has not created its data folder yet. Start Playnite once, then check again."
            : c.Detected ? null : "The Aniki ReMake theme is not installed in this Playnite.";
        return c;
    }

    public bool SetSkipped(bool skipped) => _store.Update(s => s.Skipped = skipped);

    /// <summary>"Locate existing": the owner points at the theme folder or at a Playnite data folder; PaRuX only remembers it (USER_MANAGED).</summary>
    public (PlayniteThemeComponentDto? Component, string? Error) Locate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return (null, "Choose the Aniki ReMake theme folder or the Playnite data folder.");
        string full;
        try { full = Path.GetFullPath(input.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return (null, "That path is not valid."); }
        if (!Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal)) return (null, "Use a folder on this PC.");
        string? data = null;
        string leaf = Path.GetFileName(full.TrimEnd('\\', '/'));
        if (IsAnikiFolderName(leaf) && Directory.Exists(full) && ReadYaml(full) is not null)
        {
            DirectoryInfo? parent = new DirectoryInfo(full).Parent;   // ...\Themes\Fullscreen
            if (parent is { Name: "Fullscreen", Parent: { Name: "Themes", Parent: not null } themes }) data = themes.Parent!.FullName;
        }
        else if (Directory.Exists(ThemesFolder(full))) data = full;
        if (data is null) return (null, "No Aniki ReMake theme folder or Playnite data folder with Themes\\Fullscreen was found there.");
        if (!_store.Update(s => { s.DataFolder = data; s.Skipped = false; })) return (null, "The location could not be saved.");
        return (Detect(), null);
    }

    // ---- release plan ----

    public async Task<PlayniteThemePlanDto> ResolveAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApi);
            request.Headers.UserAgent.ParseAdd("PaRuX-Playnite-Theme/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new PlayniteThemePlanDto { Message = "GitHub answered " + (int)response.StatusCode + "; try again later." };
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (json.Length > 2_000_000) return new PlayniteThemePlanDto { Message = "The release information is unexpectedly large." };
            PlayniteThemePlanDto plan = PlayniteThemeReleaseParser.Parse(json);
            if (!plan.Available) return plan;
            if (plan.SizeBytes > MaxDownloadBytes) { plan.Available = false; plan.Message = "The release is larger than PaRuX allows."; return plan; }
            string data = DataFolder();
            plan.PlayniteRunning = PlayniteRunning();
            plan.TargetFolder = ThemesFolder(data);
            string existing = Path.Combine(plan.TargetFolder, plan.ThemeFolderName!);
            plan.ExistingFolder = Directory.Exists(existing);
            if (plan.ExistingFolder) plan.ExistingVersion = ReadYaml(existing)?.Version;
            if (!Directory.Exists(data))
            { plan.Available = false; plan.Message = "Playnite has not created its data folder yet. Start Playnite once, then try again."; }
            else if (plan.PlayniteRunning)
            { plan.Message = "Playnite is running. Close it before installing (PaRuX never changes themes while Playnite runs)."; }
            else if (plan.ExistingFolder)
            { plan.Message = "This theme folder already exists. It is only replaced if you confirm, and it is kept as a backup you can roll back to."; }
            return plan;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return new PlayniteThemePlanDto { Message = "The release information could not be downloaded (no connection?)." };
        }
    }

    // ---- install ----

    public string? Begin(PlayniteThemeInstallRequest request)
    {
        if (request is null || !request.Consent) return "Installing needs your explicit confirmation.";
        if (string.IsNullOrWhiteSpace(request.Version)) return "The version to install is missing.";
        if (!_single.Wait(0)) return "An installation is already running.";
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        Set(s => { s.Stage = "downloading"; s.BytesDownloaded = 0; s.TotalBytes = 0; s.Error = null; s.RolledBack = false; s.Version = request.Version; });
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

    private void Fail(string message, bool rolledBack = false) => Set(s => { s.Stage = "failed"; s.Error = message; s.RolledBack = rolledBack; });

    internal async Task InstallAsync(PlayniteThemeInstallRequest request, CancellationToken ct)
    {
        if (PlayniteRunning()) { Fail("Playnite is running. Close it first; PaRuX never changes themes while Playnite runs."); return; }
        PlayniteThemePlanDto plan = await ResolveAsync(ct).ConfigureAwait(false);
        if (!plan.Available) { Fail(plan.Message ?? "No installable Aniki ReMake release."); return; }
        if (!string.Equals(plan.Version, request.Version, StringComparison.Ordinal))
        { Fail("The latest release changed to " + plan.Version + " since you confirmed. Review it again."); return; }
        if (plan.Sha256 is null)
        {
            if (!request.AcceptUnverified) { Fail("The release has no published checksum; confirm the unverified install explicitly."); return; }
        }
        else if (!string.Equals(plan.Sha256, request.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
        { Fail("The checksum differs from the one you confirmed. Review the plan again."); return; }
        if (plan.ExistingFolder && !request.ReplaceExisting)
        { Fail("A folder named " + plan.ThemeFolderName + " already exists. PaRuX does not overwrite it unless you explicitly choose to replace it."); return; }

        string themes = plan.TargetFolder!;
        long? free = _freeSpace(themes);
        if (free is { } f && f < plan.SizeBytes * 3 + 64L * 1024 * 1024)
        { Fail("There is not enough free disk space for the theme (about " + (plan.SizeBytes * 3 / 1048576) + " MB needed)."); return; }

        Directory.CreateDirectory(_workFolder);
        string file = Path.Combine(_workFolder, plan.AssetName!);
        string staging = Path.Combine(themes, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            string? problem = await DownloadAsync(plan, file, ct).ConfigureAwait(false);
            if (problem is not null) { Fail(problem); return; }

            Set(s => s.Stage = "verifying");
            string actual = await Sha256Async(file, ct).ConfigureAwait(false);
            if (plan.Sha256 is not null && !string.Equals(actual, plan.Sha256, StringComparison.OrdinalIgnoreCase))
            { Fail("The downloaded file does not match the published SHA-256. It was deleted and nothing was installed."); return; }

            Set(s => s.Stage = "staging");
            Directory.CreateDirectory(themes);
            SweepStaleStaging(themes);
            var (info, error) = await Task.Run(() => PlayniteThemeArchive.Extract(file, staging, ct), ct).ConfigureAwait(false);
            if (info is null) { Fail(error ?? "The theme package could not be unpacked."); return; }
            if (!string.Equals(info.Id, plan.ThemeFolderName, StringComparison.OrdinalIgnoreCase))
            { Fail("The package theme id does not match the release asset; nothing was installed."); return; }

            string? licenseProblem = await EnsureLicenseAsync(staging, info, plan.Version!, ct).ConfigureAwait(false);
            if (licenseProblem is not null) { Fail(licenseProblem); return; }

            ct.ThrowIfCancellationRequested();
            if (PlayniteRunning()) { Fail("Playnite was started during the install; nothing was changed. Close it and try again."); return; }
            Set(s => s.Stage = "installing");
            InstallStaged(staging, themes, info, plan, request.ReplaceExisting);
        }
        finally
        {
            TryDelete(staging);
            try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Atomic swap by rename (staging and target share the volume). Any failure puts the previous folder back.</summary>
    private void InstallStaged(string staging, string themes, ThemeArchiveInfo info, PlayniteThemePlanDto plan, bool replace)
    {
        string target = Path.Combine(themes, info.Id);
        string? backupRoot = null;
        string? backedUp = null;
        bool stagedIn = false;
        try
        {
            if (Directory.Exists(target))
            {
                if (!replace) { Fail("The theme folder appeared during the install; nothing was changed."); return; }
                backupRoot = Path.Combine(themes, BackupPrefix + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(backupRoot);
                string destination = Path.Combine(backupRoot, info.Id);
                Directory.Move(target, destination);
                backedUp = destination;   // set only once the original really moved
            }
            Directory.Move(staging, target);
            stagedIn = true;
            if (!File.Exists(Path.Combine(target, "theme.yaml"))) throw new IOException("The installed theme is incomplete.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bool restored = false;
            try
            {
                // Only the copy PaRuX itself moved in may be removed; an original that never moved is never touched.
                if (stagedIn && Directory.Exists(target)) Directory.Delete(target, true);
                if (backedUp is not null && Directory.Exists(backedUp) && !Directory.Exists(target)) { Directory.Move(backedUp, target); restored = true; }
                if (backupRoot is not null && (backedUp is null || restored)) TryDelete(backupRoot);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            Fail("The theme could not be installed (" + ex.GetType().Name + ")." +
                (backedUp is null ? string.Empty : restored ? " Your previous folder was put back." : " Your previous folder is kept in " + backupRoot + "."), restored);
            return;
        }

        string? oldBackup = _store.Get().BackupFolder;
        if (!_store.Update(s =>
        {
            s.ManagedFolderName = info.Id;
            s.ManagedVersion = info.Version;
            if (backupRoot is not null) s.BackupFolder = backupRoot;
        }))
        {
            // Never report success or discard an older backup if ownership/provenance could not be saved.
            // The staged theme remains on disk for owner recovery; it is not silently considered managed.
            Fail("The theme files were installed, but PaRuX could not record ownership. Do not remove the existing backup; inspect the theme in Playnite before retrying.");
            return;
        }
        // Only the newest backup is kept; an older one that PaRuX created itself is removed.
        if (backupRoot is not null && oldBackup is not null && IsPaRuXBackup(oldBackup, themes)) TryDelete(oldBackup);
        Set(s => { s.Stage = "completed"; s.Error = null; });
    }

    private static bool IsPaRuXBackup(string path, string themes)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return Path.GetFileName(full).StartsWith(BackupPrefix, StringComparison.Ordinal) &&
                string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(themes).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static void SweepStaleStaging(string themes)
    {
        try
        {
            foreach (string d in Directory.EnumerateDirectories(themes, StagingPrefix + "*")) TryDelete(d);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The MIT license text must stay with the theme: the archive's own, else the repository's LICENSE at the release tag.</summary>
    private async Task<string?> EnsureLicenseAsync(string staging, ThemeArchiveInfo info, string tag, CancellationToken ct)
    {
        if (info.LicenseFile is not null) return null;
        string text;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://raw.githubusercontent.com/" + PlayniteThemeReleaseParser.Repository + "/" + Uri.EscapeDataString(tag) + "/LICENSE");
            request.Headers.UserAgent.ParseAdd("PaRuX-Playnite-Theme/1.0");
            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || !PlayniteThemeReleaseParser.IsAllowedFinalHost(response.RequestMessage?.RequestUri, license: true))
                return "The MIT license text of the theme could not be obtained, so nothing was installed.";
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return "The MIT license text of the theme could not be downloaded, so nothing was installed.";
        }
        if (text.Length is < 200 or > 20_000 || text.IndexOf("MIT License", StringComparison.OrdinalIgnoreCase) < 0 ||
            text.IndexOf("Permission is hereby granted", StringComparison.OrdinalIgnoreCase) < 0)
            return "The theme license text is not the MIT license PaRuX expected, so nothing was installed.";
        string notice = text.TrimEnd() + Environment.NewLine + Environment.NewLine +
            "Aniki ReMake " + info.Version + " - " + PlayniteThemeReleaseParser.SourceUrl + Environment.NewLine +
            "Installed by PaRuX from the official release; this notice is kept with the theme." + Environment.NewLine;
        await File.WriteAllTextAsync(Path.Combine(staging, LicenseFileName), notice, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> DownloadAsync(PlayniteThemePlanDto plan, string file, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, plan.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("PaRuX-Playnite-Theme/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return "The download failed (HTTP " + (int)response.StatusCode + ").";
        if (!PlayniteThemeReleaseParser.IsAllowedFinalHost(response.RequestMessage?.RequestUri, license: false))
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

    // ---- rollback / removal of what PaRuX installed ----

    /// <summary>Restores the folder PaRuX replaced, or removes a theme PaRuX installed. Only PARUX_MANAGED, only with consent, never while Playnite runs.</summary>
    public PlayniteThemeActionResultDto Rollback(bool consent)
    {
        if (!consent) return Refused("This needs your explicit confirmation.");
        if (_single.CurrentCount == 0) return Refused("An installation is running.");
        if (PlayniteRunning()) return Refused("Playnite is running. Close it first; PaRuX never changes themes while Playnite runs.");
        PlayniteThemeComponentDto c = Detect();
        if (!c.Detected || c.Ownership != ComponentOwnership.PARUX_MANAGED || c.ThemeFolder is null || c.ThemeFolderName is null)
            return Refused("PaRuX did not install this theme, so PaRuX will not change it.");
        if (c.Selected) return Refused("The theme is selected in Playnite. Restore the previous theme first.");
        PlayniteThemeStore.State st = _store.Get();
        string themes = ThemesFolder(DataFolder());
        string target = c.ThemeFolder;
        string removed = Path.Combine(themes, BackupPrefix + "removed-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string? backed = st.BackupFolder is not null && IsPaRuXBackup(st.BackupFolder, themes) ? Path.Combine(st.BackupFolder, c.ThemeFolderName) : null;
            Directory.CreateDirectory(removed);
            Directory.Move(target, Path.Combine(removed, c.ThemeFolderName));
            if (backed is not null && Directory.Exists(backed))
            {
                try { Directory.Move(backed, target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Directory.Move(Path.Combine(removed, c.ThemeFolderName), target);   // put the PaRuX copy back
                    TryDelete(removed);
                    return Refused("The previous folder could not be restored (" + ex.GetType().Name + "); nothing was changed.");
                }
                TryDelete(st.BackupFolder!);
                _store.Update(s => { s.ManagedFolderName = null; s.ManagedVersion = null; s.BackupFolder = null; });
                TryDelete(removed);
                return new PlayniteThemeActionResultDto { Ok = true, Outcome = "rolled-back", Message = "Your previous theme folder was put back." };
            }
            _store.Update(s => { s.ManagedFolderName = null; s.ManagedVersion = null; s.BackupFolder = null; });
            TryDelete(removed);
            return new PlayniteThemeActionResultDto { Ok = true, Outcome = "removed", Message = "The theme PaRuX installed was removed." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refused("The theme could not be rolled back (" + ex.GetType().Name + ").");
        }
    }

    // ---- selecting the theme in Playnite (single consented, backed-up, reversible config change) ----

    public PlayniteThemeActionResultDto Select(bool consent)
    {
        if (!consent) return Refused("Changing Playnite's selected theme needs your explicit confirmation.");
        PlayniteThemeComponentDto c = Detect();
        if (!c.Detected || c.ThemeFolderName is null) return Refused("The Aniki ReMake theme is not installed in Playnite.");
        if (c.PlayniteRunning)
            return Refused("Playnite is running, so PaRuX will not edit its configuration. Close Playnite (Desktop and Fullscreen), or choose the Aniki ReMake theme yourself in Playnite Fullscreen > Settings > Theme.");
        if (!c.ConfigReadable) return Refused("Playnite's Fullscreen configuration has no readable Theme setting. Start Playnite Fullscreen once, or choose the theme in Playnite yourself.");
        if (c.Selected) return new PlayniteThemeActionResultDto { Ok = true, Outcome = "already-selected", Message = "Aniki ReMake is already the selected Fullscreen theme." };
        return ChangeTheme(c.ThemeFolderName, "selected", previous => _store.Update(s => { s.SelectedByPaRuX = c.ThemeFolderName; s.PreviousTheme = previous; }));
    }

    /// <summary>Puts the previously selected theme back, but only if PaRuX made the change and nothing has changed it since.</summary>
    public PlayniteThemeActionResultDto RestoreSelection(bool consent)
    {
        if (!consent) return Refused("Changing Playnite's selected theme needs your explicit confirmation.");
        PlayniteThemeStore.State st = _store.Get();
        PlayniteThemeComponentDto c = Detect();
        if (PlayniteRunning())
            return Refused("Playnite is running, so PaRuX will not edit its configuration. Close it first, or change the theme in Playnite yourself.");
        if (st.SelectedByPaRuX is null || st.PreviousTheme is null || !c.CanRestoreSelection)
            return Refused("PaRuX did not change the theme, or it was changed afterwards, so PaRuX leaves it as it is.");
        return ChangeTheme(st.PreviousTheme, "restored", _ => _store.Update(s => { s.SelectedByPaRuX = null; s.PreviousTheme = null; }));
    }

    private PlayniteThemeActionResultDto ChangeTheme(string newTheme, string outcome, Action<string?> record)
    {
        string config = PlayniteThemeConfig.FilePath(DataFolder());
        try
        {
            byte[] original = File.ReadAllBytes(config);
            if (original.Length > PlayniteThemeConfig.MaxBytes) return Refused("Playnite's configuration file is unexpectedly large; nothing was changed.");
            string? previous = PlayniteThemeConfig.Read(config).Theme;
            byte[]? changed = PlayniteThemeConfig.WithTheme(original, newTheme);
            if (changed is null) return Refused("The configuration could not be edited safely; nothing was changed.");

            Directory.CreateDirectory(_backupFolder);
            string backup = Path.Combine(_backupFolder, "fullscreenConfig." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json");
            File.WriteAllBytes(backup, original);
            string temp = config + ".parux-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, changed);
                File.Move(temp, config, overwrite: true);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

            string? after = PlayniteThemeConfig.Read(config).Theme;
            if (!string.Equals(after, newTheme, StringComparison.Ordinal))
            {
                File.WriteAllBytes(config, original);   // verification failed: put the original bytes back
                return Refused("The change could not be verified, so the original configuration was put back.");
            }
            record(previous);
            return new PlayniteThemeActionResultDto
            {
                Ok = true, Outcome = outcome, BackupFile = backup,
                Message = outcome == "selected"
                    ? "Aniki ReMake is now the Playnite Fullscreen theme. A backup of the configuration was saved and you can restore the previous theme."
                    : "The previous Playnite Fullscreen theme was restored."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refused("Playnite's configuration could not be changed (" + ex.GetType().Name + "); nothing was changed.");
        }
    }

    private static PlayniteThemeActionResultDto Refused(string message) => new() { Ok = false, Outcome = "refused", Message = message };

    private static PlayniteThemeInstallStatusDto Copy(PlayniteThemeInstallStatusDto s) => new()
    { Stage = s.Stage, BytesDownloaded = s.BytesDownloaded, TotalBytes = s.TotalBytes, Error = s.Error, Version = s.Version, RolledBack = s.RolledBack };
}
