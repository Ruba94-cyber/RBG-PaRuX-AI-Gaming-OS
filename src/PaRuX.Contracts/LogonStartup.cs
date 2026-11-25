using System.Text;
using System.Text.Json;

namespace PaRuX.Contracts;

/// <summary>What the command line of a Windows-sign-in start asked for.</summary>
public sealed record LogonStartupRequest(bool Logon, bool Force, bool NoStart)
{
    public static LogonStartupRequest None { get; } = new(false, false, false);
}

/// <summary>
/// "PaRuX.exe --logon [--logon-force] [--no-start]": the form of the optional sign-in entry PaRuX registers for the current Windows user.
/// <c>--logon-force</c> skips the TV/GPU wait and opens Console; <c>--no-start</c> opens the desktop side even when Console is configured.
/// The arguments carry no path and no command, so a planted argument can only change which of PaRuX's own start modes is used.
/// </summary>
public static class LogonStartupArguments
{
    public const string LogonArgument = "--logon";
    public const string ForceArgument = "--logon-force";
    public const string NoStartArgument = "--no-start";

    public static LogonStartupRequest Parse(IReadOnlyList<string>? commandLine)
    {
        if (commandLine is null || commandLine.Count < 2) return LogonStartupRequest.None;
        bool logon = false, force = false, noStart = false;
        for (int i = 1; i < commandLine.Count; i++)
        {
            string a = (commandLine[i] ?? string.Empty).Trim();
            if (Same(a, LogonArgument)) logon = true;
            else if (Same(a, ForceArgument)) force = true;
            else if (Same(a, NoStartArgument)) noStart = true;
        }
        // --logon-force and --no-start only mean something for a sign-in start; alone they are ignored.
        return logon ? new LogonStartupRequest(true, force && !noStart, noStart) : LogonStartupRequest.None;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

public enum LogonMode { Desktop, Console, Settings, Streaming, Launcher }
public enum LogonWindow { Normal, Minimized }

/// <summary>A bilingual line: every user-facing string of the start plan exists in English and Italian.</summary>
public sealed record LogonText(string En, string It)
{
    public string For(bool italian) => italian ? It : En;
}

/// <summary>The owner's saved start choices that matter to the decision. Everything is opt-in and off by default.</summary>
public sealed record LogonStartupSettings(
    bool StartInConsoleMode,
    bool StartOnTvWhenPresent,
    string? WithoutTvFallback,
    string? GpuNamePattern,
    int SupportAppsToStart,
    bool PowerProfileEnabled,
    bool BlockGameBar,
    bool SignInListEnabled);

/// <summary>What was found at start: the chosen TV(s), the optional GPU rule and whether a game is running.</summary>
public sealed record LogonProbe(bool TvSelected, bool TvPresent, bool? GpuPresent, bool GamingActive);

public sealed record LogonStep(string Code, LogonText Text);

/// <summary>The decision and the exact list of things PaRuX will do (or, in a dry run, would do).</summary>
public sealed record LogonPlan(LogonMode Mode, LogonText Reason, LogonWindow Window, IReadOnlyList<LogonStep> Steps)
{
    public string Describe(bool italian)
    {
        var sb = new StringBuilder();
        sb.AppendLine((italian ? "Modalità: " : "Mode: ") + Mode + (Window == LogonWindow.Minimized ? (italian ? " (finestra ridotta a icona)" : " (window minimised)") : string.Empty));
        sb.AppendLine((italian ? "Motivo: " : "Reason: ") + Reason.For(italian));
        foreach (LogonStep s in Steps) sb.AppendLine("- " + s.Text.For(italian));
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// The pure sign-in decision. An explicit argument or modifier wins; otherwise Console starts only when the owner asked for it AND
/// (for the TV preference) a chosen TV is connected AND (when set) the GPU rule matches. Anything uncertain falls back to the
/// owner's fallback (normal or minimised). A running game keeps everything off (Gaming hard-off). Nothing here touches Windows.
/// </summary>
public static class LogonStartupPolicy
{
    public static LogonPlan Decide(LogonStartupRequest request, PlayniteLauncherResolution launcher, LogonStartupSettings s, LogonProbe probe)
    {
        bool logon = request.Logon;
        if (probe.GamingActive)
            return Plan(LogonMode.Desktop, Window(s, logon, desktop: true), s, console: false,
                new("A game is running: nothing is started.", "Un gioco è in esecuzione: non viene avviato nulla."));

        switch (launcher.Action)
        {
            case PlayniteLauncherAction.Settings:
                return Plan(LogonMode.Settings, LogonWindow.Normal, s, false, new($"Asked explicitly ({launcher.Reason}).", $"Richiesto esplicitamente ({launcher.Reason})."), launcherStep: true);
            case PlayniteLauncherAction.Streaming:
                return Plan(LogonMode.Streaming, LogonWindow.Normal, s, false, new($"Asked explicitly ({launcher.Reason}).", $"Richiesto esplicitamente ({launcher.Reason})."), launcherStep: true);
            case PlayniteLauncherAction.Open:
                return Plan(LogonMode.Launcher, LogonWindow.Normal, s, false, new($"Asked explicitly ({launcher.Reason}).", $"Richiesto esplicitamente ({launcher.Reason})."), launcherStep: true);
            case PlayniteLauncherAction.Console:
                return Plan(LogonMode.Console, LogonWindow.Minimized, s, true, new($"Asked explicitly ({launcher.Reason}).", $"Richiesto esplicitamente ({launcher.Reason})."));
            case PlayniteLauncherAction.Desktop:
                return Plan(LogonMode.Desktop, Window(s, logon, true), s, false, new($"Asked explicitly ({launcher.Reason}).", $"Richiesto esplicitamente ({launcher.Reason})."));
        }

        if (request.NoStart)
            return Plan(LogonMode.Desktop, Window(s, logon, true), s, false, new("Started with --no-start: Console is not opened.", "Avviato con --no-start: la Console non viene aperta."));
        if (request.Force)
            return Plan(LogonMode.Console, LogonWindow.Minimized, s, true, new("Forced: the TV and GPU checks are skipped.", "Forzato: i controlli TV e GPU sono saltati."));

        bool wantsConsole = s.StartInConsoleMode || (s.StartOnTvWhenPresent && probe.TvSelected);
        if (!wantsConsole)
            return Plan(LogonMode.Desktop, Window(s, logon, true), s, false, new("Console start is not turned on.", "L'avvio in Console non è attivo."));

        bool gpuRule = !string.IsNullOrWhiteSpace(s.GpuNamePattern);
        if (gpuRule && probe.GpuPresent != true)
            return Plan(LogonMode.Desktop, Window(s, logon, true), s, false, new("The required graphics card was not found.", "La scheda grafica richiesta non è stata trovata."));

        if (!s.StartInConsoleMode && !probe.TvPresent)
            return Plan(LogonMode.Desktop, Window(s, logon, true), s, false, new("The chosen TV is not connected.", "La TV scelta non è collegata."));

        return Plan(LogonMode.Console, LogonWindow.Minimized, s, true,
            s.StartInConsoleMode
                ? new("Console start is turned on.", "L'avvio in Console è attivo.")
                : new("The chosen TV is connected.", "La TV scelta è collegata."));
    }

    // A sign-in start never grabs the screen on the desktop side: it is minimised unless the owner asked for a normal window.
    private static LogonWindow Window(LogonStartupSettings s, bool logon, bool desktop) =>
        !desktop ? LogonWindow.Normal
        : string.Equals(s.WithoutTvFallback, ConsoleStartupPolicy.FallbackMinimized, StringComparison.Ordinal) || logon ? LogonWindow.Minimized
        : LogonWindow.Normal;

    private static LogonPlan Plan(LogonMode mode, LogonWindow window, LogonStartupSettings s, bool console, LogonText reason, bool launcherStep = false)
    {
        var steps = new List<LogonStep>();
        if (console)
        {
            if (s.SupportAppsToStart > 0)
                steps.Add(new("support-apps", new($"Start {s.SupportAppsToStart} support app(s) you configured (optional, never installed or changed by PaRuX).",
                    $"Avvia {s.SupportAppsToStart} app di supporto configurate da te (facoltative, mai installate o modificate da PaRuX).")));
            steps.Add(new("playnite", new("Open Playnite Fullscreen when it is available; otherwise Console opens inside PaRuX.",
                "Apre Playnite Fullscreen se disponibile; altrimenti la Console si apre dentro PaRuX.")));
            if (s.PowerProfileEnabled)
                steps.Add(new("power-profile", new("Switch to the Windows power plan you chose (restored when Console ends).",
                    "Passa al piano energetico di Windows che hai scelto (ripristinato alla fine della Console).")));
            if (s.BlockGameBar)
                steps.Add(new("game-bar", new("Block the Xbox Game Bar while Console is in use (restored afterwards).",
                    "Blocca la Xbox Game Bar mentre la Console è in uso (ripristinata dopo).")));
        }
        if (launcherStep)
            steps.Add(new("launcher", new("Open PaRuX's launcher page for the requested action.", "Apre la pagina del launcher di PaRuX per l'azione richiesta.")));
        if (window == LogonWindow.Minimized && !console)
            steps.Add(new("minimize", new("Keep the PaRuX window minimised (no window in front).", "Tiene ridotta a icona la finestra di PaRuX (nessuna finestra in primo piano).")));
        if (s.SignInListEnabled && mode != LogonMode.Settings)
            steps.Add(new("sign-in-list", new("Run your after-sign-in app list (apps marked Skip in Console are skipped in Console).",
                "Esegue l'elenco di app dopo l'accesso (le app con Salta in Console sono saltate in Console).")));
        steps.Add(new("timing-log", new("Write the start timings to StartupTiming.log in PaRuXData.", "Scrive i tempi di avvio in StartupTiming.log dentro PaRuXData.")));
        return new LogonPlan(mode, reason, window, steps);
    }
}

/// <summary>Optional GPU requirement: a name fragment (case-insensitive, <c>*</c> wildcards allowed) matched against the present display adapters.</summary>
public static class LogonGpuRule
{
    public const int MaxPatternLength = 80;

    public static string? Normalize(string? pattern)
    {
        string p = new string((pattern ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return p.Length == 0 ? null : p.Length <= MaxPatternLength ? p : p[..MaxPatternLength];
    }

    public static bool Matches(string? pattern, IEnumerable<string?>? adapterNames)
    {
        string? p = Normalize(pattern);
        if (p is null) return true;
        string[] parts = p.Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string? name in adapterNames ?? Array.Empty<string?>())
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            int at = 0; bool all = true;
            foreach (string part in parts)
            {
                int i = name.IndexOf(part, at, StringComparison.OrdinalIgnoreCase);
                if (i < 0) { all = false; break; }
                at = i + part.Length;
            }
            if (all) return true;
        }
        return false;
    }
}

/// <summary>
/// Start timings with an explicit measurement: every line is "date time +elapsed-ms phase", where elapsed is read from a monotonic
/// stopwatch started at the beginning of the start sequence. The sink is injected (a file in PaRuXData in the app, a list in tests).
/// </summary>
public sealed class StartupTimingRecorder
{
    private readonly Func<long> _elapsedMs;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _append;

    public StartupTimingRecorder(Func<long> elapsedMs, Func<DateTime> now, Action<string> append)
    {
        _elapsedMs = elapsedMs; _now = now; _append = append;
    }

    public static string Format(DateTime local, long elapsedMs, string phase) =>
        local.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " +" + Math.Max(0, elapsedMs) + "ms " + Clean(phase);

    public void Mark(string phase)
    {
        try { _append(Format(_now(), _elapsedMs(), phase)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* timing is diagnostic only */ }
    }

    private static string Clean(string s) => new string((s ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
}

/// <summary>The timing file: appended line by line, trimmed to the newest 500 lines once it passes 256 KB. Never throws.</summary>
public static class StartupTimingFile
{
    public const long MaxBytes = 256 * 1024;
    public const int KeepLines = 500;

    public static void Append(string path, string line)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            {
                string[] tail = File.ReadAllLines(path).TakeLast(KeepLines).ToArray();
                File.WriteAllLines(path, tail);
            }
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>The sign-in entry is one HKCU Run value. This seam is the registry; tests use a dictionary and never touch Windows.</summary>
public interface IRunKeyStore
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}

public sealed class FakeRunKeyStore : IRunKeyStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(string name) => Values.TryGetValue(name, out string? v) ? v : null;
    public void Set(string name, string value) => Values[name] = value;
    public void Delete(string name) => Values.Remove(name);
}

public enum LogonRegistrationState { NotRegistered, RegisteredPlain, RegisteredForLogon, Foreign }

/// <summary>
/// Reversible sign-in registration for the CURRENT user only (HKCU Run, no admin, no task). Enabling replaces PaRuX's own value
/// with the same executable plus <c>--logon</c> and records the previous value in a small journal; disabling puts it back exactly
/// (or removes it when there was none). A value that is not PaRuX's own command is never overwritten silently.
/// </summary>
public sealed class LogonRegistration
{
    public const string ValueName = "PaRuX";
    private readonly IRunKeyStore _store;
    private readonly string _journalPath;
    private sealed record Journal(int Version, bool HadValue, string? Previous);

    public LogonRegistration(IRunKeyStore store, string journalPath) { _store = store; _journalPath = journalPath; }

    public static string CommandFor(string exePath, bool logon) =>
        "\"" + exePath + "\"" + (logon ? " " + LogonStartupArguments.LogonArgument : string.Empty);

    public LogonRegistrationState Status(string exePath)
    {
        string? v = _store.Get(ValueName);
        if (string.IsNullOrWhiteSpace(v)) return LogonRegistrationState.NotRegistered;
        if (string.Equals(v, CommandFor(exePath, true), StringComparison.OrdinalIgnoreCase)) return LogonRegistrationState.RegisteredForLogon;
        if (string.Equals(v, CommandFor(exePath, false), StringComparison.OrdinalIgnoreCase)) return LogonRegistrationState.RegisteredPlain;
        return LogonRegistrationState.Foreign;
    }

    /// <summary>Returns null on success, otherwise a short reason. <paramref name="replaceForeign"/> must come from an explicit confirmation.</summary>
    public string? Enable(string exePath, bool replaceForeign = false)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath) || exePath.Any(char.IsControl) || exePath.Contains('"'))
            return "The PaRuX executable path is not valid.";
        LogonRegistrationState state = Status(exePath);
        if (state == LogonRegistrationState.RegisteredForLogon) return null;
        if (state == LogonRegistrationState.Foreign && !replaceForeign)
            return "A different command is already registered under PaRuX's startup name; it was not changed.";
        string? previous = _store.Get(ValueName);
        try
        {
            if (!File.Exists(_journalPath) || state != LogonRegistrationState.RegisteredForLogon)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
                File.WriteAllText(_journalPath, JsonSerializer.Serialize(new Journal(1, previous is not null, previous)));
            }
            _store.Set(ValueName, CommandFor(exePath, true));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "The sign-in entry could not be saved."; }
    }

    /// <summary>Restores the value from before <see cref="Enable"/> (or removes it). Returns null on success.</summary>
    public string? Restore()
    {
        try
        {
            Journal? j = File.Exists(_journalPath) ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(_journalPath)) : null;
            if (j is { HadValue: true, Previous: { Length: > 0 } prev }) _store.Set(ValueName, prev);
            else _store.Delete(ValueName);
            if (File.Exists(_journalPath)) File.Delete(_journalPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return "The sign-in entry could not be restored."; }
    }
}
