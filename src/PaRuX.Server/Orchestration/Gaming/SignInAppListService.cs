using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Pure parsing/validation of the PaRuX-owned "after sign-in" app list and the read-only importer for the
/// legacy DelayedApps.json schema. Nothing here touches the registry, Startup folders or third-party startup
/// entries (those stay USER_MANAGED). An imported or parsed list is ALWAYS disabled until the owner opts in.
/// </summary>
public static partial class SignInAppListPolicy
{
    public const int MaxApps = 50;
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxNameLength = 80;
    public const int MaxArgsLength = 1024;

    // Package family name (name_13-char publisher id) ! application id.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{1,63}_[a-z0-9]{13}![A-Za-z0-9][A-Za-z0-9.\-_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AumidRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._ \-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessNameRegex();

    private static readonly char[] ShellMetaChars = ['&', '|', ';', '<', '>', '^', '`', '\n', '\r', '\0'];

    public static bool IsValidAumid(string? aumid) => aumid is not null && aumid.Length <= 200 && AumidRegex().IsMatch(aumid);

    public static bool IsValidProcessName(string? name) => name is not null && ProcessNameRegex().IsMatch(name) && !name.EndsWith(' ');

    /// <summary>Process name used for the already-running probe: explicit Process, else the exe file name; null for Appx without one.</summary>
    public static string? ProbeName(SignInAppEntry e)
    {
        string? n = !string.IsNullOrWhiteSpace(e.Process) ? e.Process : e.Kind == SignInAppKind.Exe && e.Path is not null
            ? Path.GetFileNameWithoutExtension(e.Path) : null;
        if (n is null) return null;
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    /// <summary>Entry-level admission. Returns an error text or null.</summary>
    public static string? ValidateEntry(SignInAppEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > MaxNameLength || e.Name.Any(char.IsControl))
            return "The app name must be 1 to 80 characters without control characters.";
        if (e.Args is not null)
        {
            if (e.Args.Length > MaxArgsLength || e.Args.Any(char.IsControl))
                return $"{e.Name}: arguments must be at most 1024 characters without control characters.";
            if (e.Args.IndexOfAny(ShellMetaChars) >= 0)
                return $"{e.Name}: arguments must not contain shell metacharacters.";
        }
        if (e.Process is { Length: > 0 } && !IsValidProcessName(e.Process))
            return $"{e.Name}: the process name is not valid.";

        if (e.Kind == SignInAppKind.Exe)
        {
            if (e.Path is null || ContainsDotDotSegment(e.Path) ||
                !GamingLocalFileSafetyPolicy.IsSafeRegularFile(e.Path, GamingLocalFileSafetyPolicy.ExecutableExtensions))
                return $"{e.Name}: the path must be an existing local .exe (no network path, shortcut, link or relative segment).";
            if (e.Aumid is { Length: > 0 }) return $"{e.Name}: an exe entry must not carry an AUMID.";
            return null;
        }
        if (!IsValidAumid(e.Aumid)) return $"{e.Name}: the AUMID format is not valid.";
        if (!string.IsNullOrEmpty(e.Path)) return $"{e.Name}: an appx entry must not carry a path.";
        if (!string.IsNullOrEmpty(e.Args)) return $"{e.Name}: an appx entry does not take arguments.";
        return null;
    }

    private static bool ContainsDotDotSegment(string path) =>
        path.Split('\\', '/').Any(s => s == "..");

    /// <summary>Parses the settings/apps schema (case-insensitive keys). The result is never enabled.</summary>
    public static SignInAppListParseResult Parse(string? json)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxFileBytes)
            return new(null, ["The list is empty or larger than 256 KB."], warnings);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return new(null, ["The list is not valid JSON."], warnings); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new(null, ["The list root must be an object."], warnings);

            var settings = new SignInAppListSettings();
            var apps = new List<SignInAppEntry>();
            foreach (JsonProperty top in doc.RootElement.EnumerateObject())
            {
                if (Is(top.Name, "Settings"))
                {
                    if (top.Value.ValueKind != JsonValueKind.Object) { errors.Add("Settings must be an object."); continue; }
                    settings = ParseSettings(top.Value, errors, warnings);
                }
                else if (Is(top.Name, "Apps"))
                {
                    if (top.Value.ValueKind != JsonValueKind.Array) { errors.Add("Apps must be an array."); continue; }
                    if (top.Value.GetArrayLength() > MaxApps) { errors.Add("Too many apps (maximum 50)."); continue; }
                    foreach (JsonElement item in top.Value.EnumerateArray())
                    {
                        SignInAppEntry? entry = ParseEntry(item, errors);
                        if (entry is null) continue;
                        string? problem = ValidateEntry(entry);
                        if (problem is not null) errors.Add(problem); else apps.Add(entry);
                    }
                }
                else warnings.Add($"Ignored unknown field '{Clip(top.Name)}'.");
            }
            if (errors.Count > 0) return new(null, errors, warnings);
            return new(new SignInAppList(false, settings, apps), errors, warnings);
        }
    }

    /// <summary>Opt-in, read-only import of the legacy DelayedApps.json from an explicitly chosen file.</summary>
    public static SignInAppListParseResult ImportLegacyFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path) || ContainsDotDotSegment(path) ||
            !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            return new(null, ["Choose a local .json file."], []);
        try
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return new(null, ["The file does not exist or is a link."], []);
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > MaxFileBytes) return new(null, ["The file is larger than 256 KB."], []);
            using var reader = new StreamReader(fs);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(null, ["The file could not be read."], []);
        }
    }

    private static SignInAppListSettings ParseSettings(JsonElement obj, List<string> errors, List<string> warnings)
    {
        var s = new SignInAppListSettings();
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (Is(p.Name, "LaunchSpacingSeconds")) s = s with { LaunchSpacingSeconds = Int(p, 0, 300, errors) ?? s.LaunchSpacingSeconds };
            else if (Is(p.Name, "DesktopDelaySeconds")) s = s with { DesktopDelaySeconds = Int(p, 0, 600, errors) ?? s.DesktopDelaySeconds };
            else if (Is(p.Name, "ReadyTimeoutSeconds")) s = s with { ReadyTimeoutSeconds = Int(p, 1, 600, errors) ?? s.ReadyTimeoutSeconds };
            else if (Is(p.Name, "ConsoleReadyDelaySeconds")) s = s with { ConsoleReadyDelaySeconds = Int(p, 0, 300, errors) ?? s.ConsoleReadyDelaySeconds };
            else if (Is(p.Name, "ModeWaitSeconds")) s = s with { ModeWaitSeconds = Int(p, 0, 120, errors) ?? s.ModeWaitSeconds };
            else warnings.Add($"Ignored unknown setting '{Clip(p.Name)}'.");
        }
        return s;
    }

    private static int? Int(JsonProperty p, int min, int max, List<string> errors)
    {
        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int v) && v >= min && v <= max) return v;
        errors.Add($"{Clip(p.Name)} must be a whole number from {min} to {max}.");
        return null;
    }

    private static SignInAppEntry? ParseEntry(JsonElement item, List<string> errors)
    {
        if (item.ValueKind != JsonValueKind.Object) { errors.Add("Every app must be an object."); return null; }
        string? name = null, kind = null, path = null, args = null, aumid = null, process = null;
        bool skip = false;
        foreach (JsonProperty p in item.EnumerateObject())
        {
            if (Is(p.Name, "Name")) name = Str(p);
            else if (Is(p.Name, "Kind")) kind = Str(p);
            else if (Is(p.Name, "Path")) path = Str(p);
            else if (Is(p.Name, "Args")) args = Str(p);
            else if (Is(p.Name, "Aumid")) aumid = Str(p);
            else if (Is(p.Name, "Process")) process = Str(p);
            else if (Is(p.Name, "SkipInConsole")) skip = p.Value.ValueKind == JsonValueKind.True;
        }
        SignInAppKind k;
        if (string.Equals(kind, "exe", StringComparison.OrdinalIgnoreCase)) k = SignInAppKind.Exe;
        else if (string.Equals(kind, "appx", StringComparison.OrdinalIgnoreCase)) k = SignInAppKind.Appx;
        else { errors.Add($"{Clip(name ?? "An app")}: Kind must be exe or appx."); return null; }
        return new SignInAppEntry(name ?? "", k, path, args, aumid, process, skip);
    }

    private static string? Str(JsonProperty p) => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
    private static bool Is(string actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    private static string Clip(string s) => s.Length <= 40 ? s : s[..40];
}

/// <summary>Abstraction over process probing/launching so the run logic is testable without starting anything.</summary>
public interface ISignInAppLauncher
{
    bool IsProcessRunning(string processName);
    /// <summary>Starts the entry. Returns null on success, otherwise a short honest failure reason.</summary>
    Task<string?> LaunchAsync(SignInAppEntry entry, CancellationToken ct);
}

/// <summary>Production launcher: exe via CreateProcess (no shell), appx via the AppsFolder shell target with a validated AUMID.</summary>
public sealed class SignInAppProcessLauncher : ISignInAppLauncher
{
    public bool IsProcessRunning(string processName)
    {
        Process[] found = Process.GetProcessesByName(processName);
        try { return found.Length > 0; }
        finally { foreach (Process p in found) p.Dispose(); }
    }

    public Task<string?> LaunchAsync(SignInAppEntry entry, CancellationToken ct)
    {
        try
        {
            ProcessStartInfo info;
            if (entry.Kind == SignInAppKind.Exe)
            {
                string full = Path.GetFullPath(entry.Path!);
                info = new ProcessStartInfo
                {
                    FileName = full,
                    WorkingDirectory = Path.GetDirectoryName(full)!,
                    Arguments = entry.Args ?? "",
                    UseShellExecute = false,
                };
            }
            else
            {
                info = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    Arguments = "shell:AppsFolder\\" + entry.Aumid,
                    UseShellExecute = false,
                };
            }
            using Process? p = Process.Start(info);
            return Task.FromResult<string?>(p is null ? "not-started" : null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or
            IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<string?>("start-failed");
        }
    }
}

/// <summary>
/// Runs a validated <see cref="SignInAppList"/> once (the caller decides when: after sign-in). Off unless the list is
/// enabled; never runs or keeps running while Gaming is active (hard-off, checked every second and before every launch);
/// skips SkipInConsole apps while Console/TV mode is active; skips apps whose process is already running; bounded
/// retries (3 attempts, only on launch failure); cancellable; observable via <see cref="Snapshot"/>.
/// </summary>
public sealed class SignInAppListService
{
    public const int MaxAttempts = 3;
    private readonly ISignInAppLauncher _launcher;
    private readonly Func<bool> _gamingActive;
    private readonly Func<bool> _consoleActive;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private SignInRunSnapshot _snapshot = new(SignInRunState.Idle, 0, 0, []);
    private int _running;

    public SignInAppListService(ISignInAppLauncher launcher, Func<bool> gamingActive, Func<bool> consoleActive,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _launcher = launcher;
        _gamingActive = gamingActive;
        _consoleActive = consoleActive;
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    public SignInRunSnapshot Snapshot() { lock (_gate) return _snapshot; }

    private sealed class GamingStartedException : Exception { }

    public async Task<SignInRunSnapshot> RunAsync(SignInAppList list, CancellationToken ct)
    {
        if (!list.Enabled) return Set(SignInRunState.SkippedDisabled, 0, 0, []);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return Snapshot(); // one run at a time
        var results = new List<SignInAppResult>();
        int i = 0;
        try
        {
            if (_gamingActive()) return Set(SignInRunState.SkippedGamingActive, 0, list.Apps.Count, []);
            Set(SignInRunState.Waiting, 0, list.Apps.Count, results);
            bool console = _consoleActive();
            int lead = console ? list.Settings.ConsoleReadyDelaySeconds : list.Settings.DesktopDelaySeconds;
            await WaitAsync(lead, ct).ConfigureAwait(false);

            for (; i < list.Apps.Count; i++)
            {
                SignInAppEntry app = list.Apps[i];
                Set(SignInRunState.Running, i, list.Apps.Count, results);
                if (_gamingActive()) throw new GamingStartedException();
                SignInAppResult r = await RunOneAsync(app, list.Settings, ct).ConfigureAwait(false);
                results.Add(r);
                Set(SignInRunState.Running, i, list.Apps.Count, results);
                if (r.Outcome == SignInAppOutcome.Launched && i < list.Apps.Count - 1)
                    await WaitAsync(list.Settings.LaunchSpacingSeconds, ct).ConfigureAwait(false);
            }
            return Set(SignInRunState.Completed, list.Apps.Count, list.Apps.Count, results);
        }
        catch (GamingStartedException)
        {
            for (; i < list.Apps.Count; i++)
                if (results.Count <= i) results.Add(new(list.Apps[i].Name, SignInAppOutcome.SkippedGamingActive, 0, "gaming-active"));
            return Set(SignInRunState.SkippedGamingActive, list.Apps.Count, list.Apps.Count, results);
        }
        catch (OperationCanceledException)
        {
            for (; i < list.Apps.Count; i++)
                if (results.Count <= i) results.Add(new(list.Apps[i].Name, SignInAppOutcome.Cancelled, 0, "cancelled"));
            return Set(SignInRunState.Cancelled, i, list.Apps.Count, results);
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    private async Task<SignInAppResult> RunOneAsync(SignInAppEntry app, SignInAppListSettings s, CancellationToken ct)
    {
        string? problem = SignInAppListPolicy.ValidateEntry(app); // revalidate: the file may have become a link since import
        if (problem is not null) return new(app.Name, SignInAppOutcome.SkippedInvalid, 0, "invalid-entry");
        if (app.SkipInConsole && _consoleActive()) return new(app.Name, SignInAppOutcome.SkippedConsoleMode, 0, "console-mode");
        string? probe = SignInAppListPolicy.ProbeName(app);
        if (probe is not null && SafeRunning(probe)) return new(app.Name, SignInAppOutcome.SkippedAlreadyRunning, 0, "already-running");

        string reason = "failed";
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (_gamingActive()) throw new GamingStartedException();
            string? fail = await _launcher.LaunchAsync(app, ct).ConfigureAwait(false);
            if (fail is null)
            {
                if (probe is null) return new(app.Name, SignInAppOutcome.Launched, attempt, "started");
                for (int t = 0; t < s.ReadyTimeoutSeconds; t++)
                {
                    if (SafeRunning(probe)) return new(app.Name, SignInAppOutcome.Launched, attempt, "running");
                    await WaitAsync(1, ct).ConfigureAwait(false);
                }
                // Started but never observed: report honestly and do NOT relaunch (could duplicate a slow app).
                return new(app.Name, SignInAppOutcome.Launched, attempt, "process-not-observed");
            }
            reason = fail;
            if (attempt < MaxAttempts) await WaitAsync(1, ct).ConfigureAwait(false);
        }
        return new(app.Name, SignInAppOutcome.Failed, MaxAttempts, reason);
    }

    private bool SafeRunning(string probe)
    {
        try { return _launcher.IsProcessRunning(probe); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Waits in one-second slices so cancellation and Gaming hard-off are honoured promptly.</summary>
    private async Task WaitAsync(int seconds, CancellationToken ct)
    {
        for (int n = 0; n < seconds; n++)
        {
            ct.ThrowIfCancellationRequested();
            if (_gamingActive()) throw new GamingStartedException();
            await _delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
    }

    private SignInRunSnapshot Set(SignInRunState state, int index, int total, IReadOnlyList<SignInAppResult> results)
    {
        var snap = new SignInRunSnapshot(state, index, total, results.ToArray());
        lock (_gate) _snapshot = snap;
        return snap;
    }
}
