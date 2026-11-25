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

    // The desktop side follows the owner's "without TV" choice for EVERY start, sign-in included: "normal" (the default) opens the
    // window in front, "minimized" keeps it on the taskbar. A sign-in start used to force minimised regardless of the setting, which
    // made an owner who chose "normal" see nothing at sign-in. Anything that is not exactly "minimized" means a normal window.
    private static LogonWindow Window(LogonStartupSettings s, bool logon, bool desktop) =>
        desktop && string.Equals(s.WithoutTvFallback, ConsoleStartupPolicy.FallbackMinimized, StringComparison.Ordinal)
            ? LogonWindow.Minimized
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
            steps.Add(new("minimize", new("Keep the PaRuX window minimised (your choice: no window in front).", "Tiene ridotta a icona la finestra di PaRuX (tua scelta: nessuna finestra in primo piano).")));
        else if (mode == LogonMode.Desktop)
            steps.Add(new("show-window", new("Open the PaRuX window normally and bring it to the front (your choice).", "Apre normalmente la finestra di PaRuX e la porta in primo piano (tua scelta).")));
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

/// <summary>The fallback sign-in entry is one HKCU Run value. This seam is the registry; tests use a dictionary and never touch Windows.</summary>
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

/// <summary>What Windows Task Scheduler reports for one task of the current user (read-only snapshot).</summary>
public sealed record LogonTaskInfo(string? Command, string? Arguments, bool Enabled, DateTimeOffset? LastRunUtc, int? LastResult, string Xml);

/// <summary>The outcome of a Task Scheduler call. <see cref="AccessDenied"/> means Windows refused it for this user (administrator rights needed).</summary>
public sealed record LogonTaskCallResult(bool Ok, bool AccessDenied, string? Error)
{
    public static LogonTaskCallResult Success { get; } = new(true, false, null);
    public static LogonTaskCallResult Failed(string error, bool accessDenied = false) => new(false, accessDenied, error);
}

/// <summary>
/// The primary sign-in entry: one scheduled task in the root folder for the CURRENT user (logon trigger for that user, interactive token,
/// least privilege). The calls go to the Task Scheduler service, so they are never redirected by another app's private registry or file
/// view (the reason a Run value written from inside such an app is never seen by Windows). Tests use <see cref="FakeLogonTaskStore"/>.
/// </summary>
public interface ILogonTaskStore
{
    LogonTaskInfo? Get(string name);
    LogonTaskCallResult Register(string name, string xml);
    LogonTaskCallResult Delete(string name);
}

public sealed class FakeLogonTaskStore : ILogonTaskStore
{
    public Dictionary<string, LogonTaskInfo> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When set, Register fails with this result (simulates a policy or an access-denied Task Scheduler).</summary>
    public LogonTaskCallResult? FailRegister { get; set; }
    public int RegisterCalls { get; private set; }

    public LogonTaskInfo? Get(string name) => Tasks.TryGetValue(name, out LogonTaskInfo? t) ? t : null;

    public LogonTaskCallResult Register(string name, string xml)
    {
        RegisterCalls++;
        if (FailRegister is { } fail) return fail;
        System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XElement? exec = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Exec");
        string? command = exec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value;
        string? args = exec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value;
        bool enabled = !string.Equals(doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Settings")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase);
        Tasks[name] = new LogonTaskInfo(command, args, enabled, null, null, xml);
        return LogonTaskCallResult.Success;
    }

    public LogonTaskCallResult Delete(string name) { Tasks.Remove(name); return LogonTaskCallResult.Success; }
}

public enum LogonRegistrationState { NotRegistered, RegisteredPlain, RegisteredForLogon, Foreign }

/// <summary>Which Windows mechanism starts PaRuX at sign-in.</summary>
public enum LogonMechanism { None, ScheduledTask, RunKey, ScheduledTaskAndRunKey }

/// <summary>Everything Settings shows about the sign-in entry.</summary>
public sealed record LogonRegistrationStatus(
    LogonRegistrationState State,
    LogonMechanism Mechanism,
    bool ForeignTask,
    bool ForeignRunValue,
    bool TaskEnabled,
    DateTimeOffset? TaskLastRunUtc,
    int? TaskLastResult);

/// <summary>The result of turning the sign-in start on.</summary>
public sealed record LogonEnableResult(bool Ok, LogonMechanism Mechanism, string? Error, string? Note, bool NeedsAdministrator)
{
    public static LogonEnableResult Fail(string error, bool needsAdmin = false) => new(false, LogonMechanism.None, error, null, needsAdmin);
}

/// <summary>
/// Reversible sign-in registration for the CURRENT Windows user only. Preferred: the scheduled task <see cref="TaskName"/> (logon trigger
/// for this user, least privilege, no administrator rights on a normal Windows). When Task Scheduler refuses or is unavailable, the HKCU Run
/// value is used instead. What was there before (a PaRuX Run value, a foreign Run value, a foreign task with the same name) is recorded in a
/// small journal and put back exactly by <see cref="Restore()"/>. A value or task that is not PaRuX's own is never overwritten silently.
/// </summary>
public sealed class LogonRegistration
{
    public const string ValueName = "PaRuX";
    public const string TaskName = "PaRuX at sign-in";
    /// <summary>Normal process priority for the task (Task Scheduler's default 7 is "below normal", which also slows Core started as a child).</summary>
    public const int TaskPriority = 4;

    private readonly IRunKeyStore _store;
    private readonly ILogonTaskStore? _tasks;
    private readonly string _journalPath;
    private readonly string? _userId;
    private readonly string? _userSid;

    private sealed record Journal(int Version, bool HadValue, string? Previous, bool HadTask = false, string? PreviousTaskXml = null);

    /// <summary>Run-value only (no Task Scheduler).</summary>
    public LogonRegistration(IRunKeyStore store, string journalPath) : this(store, null, journalPath, null, null) { }

    /// <param name="userId">DOMAIN\user of the current user (logon trigger).</param>
    /// <param name="userSid">SID of the current user (task principal).</param>
    public LogonRegistration(IRunKeyStore store, ILogonTaskStore? tasks, string journalPath, string? userId, string? userSid)
    {
        _store = store; _tasks = tasks; _journalPath = journalPath; _userId = userId; _userSid = userSid;
    }

    public static string CommandFor(string exePath, bool logon) =>
        "\"" + exePath + "\"" + (logon ? " " + LogonStartupArguments.LogonArgument : string.Empty);

    /// <summary>True when the task starts this executable with exactly <c>--logon</c>.</summary>
    public static bool IsOwnLogonTask(LogonTaskInfo? task, string exePath) =>
        task is not null
        && string.Equals((task.Command ?? string.Empty).Trim().Trim('"'), exePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals((task.Arguments ?? string.Empty).Trim(), LogonStartupArguments.LogonArgument, StringComparison.OrdinalIgnoreCase);

    private static bool IsOwnRunValue(string? v, string exePath) =>
        string.Equals(v, CommandFor(exePath, true), StringComparison.OrdinalIgnoreCase)
        || string.Equals(v, CommandFor(exePath, false), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The task definition: logon trigger for this user only, interactive token, least privilege, normal priority, no time limit,
    /// one instance, never stopped on battery. All values are XML-escaped.
    /// </summary>
    public static string BuildTaskXml(string exePath, string userId, string userSid)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s) ?? string.Empty;
        string dir = Path.GetDirectoryName(exePath) ?? string.Empty;
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n"
            + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n"
            + "  <RegistrationInfo><Description>Starts PaRuX when you sign in to Windows (Console or Desktop, as chosen in PaRuX Settings). Turn it off in PaRuX Settings.</Description></RegistrationInfo>\n"
            + "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + X(userId) + "</UserId></LogonTrigger></Triggers>\n"
            + "  <Principals><Principal id=\"Author\"><UserId>" + X(userSid) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n"
            + "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
            + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>"
            + "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"
            + "<Priority>" + TaskPriority + "</Priority></Settings>\n"
            + "  <Actions Context=\"Author\"><Exec><Command>" + X(exePath) + "</Command><Arguments>" + LogonStartupArguments.LogonArgument
            + "</Arguments><WorkingDirectory>" + X(dir) + "</WorkingDirectory></Exec></Actions>\n"
            + "</Task>\n";
    }

    public LogonRegistrationState Status(string exePath) => Describe(exePath).State;

    public LogonRegistrationStatus Describe(string exePath)
    {
        string? v = _store.Get(ValueName);
        LogonTaskInfo? task = _tasks?.Get(TaskName);
        bool taskOwn = IsOwnLogonTask(task, exePath);
        bool taskActive = taskOwn && task!.Enabled;
        bool runLogon = string.Equals(v, CommandFor(exePath, true), StringComparison.OrdinalIgnoreCase);
        bool runPlain = string.Equals(v, CommandFor(exePath, false), StringComparison.OrdinalIgnoreCase);
        bool runForeign = !string.IsNullOrWhiteSpace(v) && !runLogon && !runPlain;
        LogonMechanism mech = taskActive && runLogon ? LogonMechanism.ScheduledTaskAndRunKey
            : taskActive ? LogonMechanism.ScheduledTask
            : runLogon ? LogonMechanism.RunKey
            : LogonMechanism.None;
        LogonRegistrationState state = mech != LogonMechanism.None ? LogonRegistrationState.RegisteredForLogon
            : runPlain ? LogonRegistrationState.RegisteredPlain
            : runForeign || (task is not null && !taskOwn) ? LogonRegistrationState.Foreign
            : LogonRegistrationState.NotRegistered;
        return new LogonRegistrationStatus(state, mech, task is not null && !taskOwn, runForeign, taskActive,
            taskOwn ? task!.LastRunUtc : null, taskOwn ? task!.LastResult : null);
    }

    /// <summary>Returns null on success, otherwise a short reason. <paramref name="replaceForeign"/> must come from an explicit confirmation.</summary>
    public string? Enable(string exePath, bool replaceForeign = false)
    {
        LogonEnableResult r = EnableDetailed(exePath, replaceForeign);
        return r.Ok ? null : r.Error;
    }

    public LogonEnableResult EnableDetailed(string exePath, bool replaceForeign = false)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath) || exePath.Any(char.IsControl) || exePath.Contains('"'))
            return LogonEnableResult.Fail("The PaRuX executable path is not valid.");
        LogonRegistrationStatus now = Describe(exePath);
        string? previousRun = _store.Get(ValueName);
        LogonTaskInfo? previousTask = _tasks?.Get(TaskName);
        bool useTask = _tasks is not null && !string.IsNullOrWhiteSpace(_userId) && !string.IsNullOrWhiteSpace(_userSid);

        // Already done: our task is active and no PaRuX Run value would start a second copy (or, without Task Scheduler, the Run value).
        if (now.Mechanism == LogonMechanism.ScheduledTask) return new(true, LogonMechanism.ScheduledTask, null, null, false);
        if (now.Mechanism == LogonMechanism.RunKey && !useTask) return new(true, LogonMechanism.RunKey, null, null, false);

        const string ForeignRun = "A different command is already registered under PaRuX's startup name; it was not changed.";
        if (now.ForeignRunValue && !replaceForeign && !useTask) return LogonEnableResult.Fail(ForeignRun);
        if (now.ForeignTask && !replaceForeign)
            return LogonEnableResult.Fail("A different scheduled task named \"" + TaskName + "\" already exists; it was not changed.");

        try
        {
            if (!File.Exists(_journalPath))
            {
                // A PaRuX --logon entry is our own: it is not "previous state" to bring back (turning off must stop the sign-in start).
                bool runIsOwnLogon = string.Equals(previousRun, CommandFor(exePath, true), StringComparison.OrdinalIgnoreCase);
                bool taskIsOwn = IsOwnLogonTask(previousTask, exePath);
                Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
                File.WriteAllText(_journalPath, JsonSerializer.Serialize(new Journal(2,
                    previousRun is not null && !runIsOwnLogon, runIsOwnLogon ? null : previousRun,
                    previousTask is not null && !taskIsOwn, taskIsOwn ? null : previousTask?.Xml)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return LogonEnableResult.Fail("The sign-in entry could not be saved."); }

        string? taskNote = null;
        bool needsAdmin = false;
        if (useTask)
        {
            LogonTaskCallResult r = _tasks!.Register(TaskName, BuildTaskXml(exePath, _userId!, _userSid!));
            if (r.Ok && IsOwnLogonTask(_tasks.Get(TaskName), exePath))
            {
                try
                {
                    // One mechanism only: PaRuX's own Run value would start a second copy (the journal brings a plain one back on Restore).
                    if (IsOwnRunValue(_store.Get(ValueName), exePath)) _store.Delete(ValueName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                return new(true, LogonMechanism.ScheduledTask, null, null, false);
            }
            needsAdmin = r.AccessDenied;
            taskNote = r.AccessDenied
                ? "Windows did not allow a sign-in task for your user without administrator rights; the Run entry is used instead."
                : "The sign-in task could not be created (" + (r.Error ?? "unknown error") + "); the Run entry is used instead.";
        }

        // Fallback: the HKCU Run value (only where a foreign value is not in the way).
        if (now.ForeignRunValue && !replaceForeign) return new(false, LogonMechanism.None, ForeignRun + " " + taskNote, taskNote, needsAdmin);
        try
        {
            _store.Set(ValueName, CommandFor(exePath, true));
            return new(true, LogonMechanism.RunKey, null, taskNote, needsAdmin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(false, LogonMechanism.None, "The sign-in entry could not be saved.", taskNote, needsAdmin);
        }
    }

    /// <summary>Restores what was there before <see cref="Enable"/> (task and Run value). Returns null on success.</summary>
    public string? Restore() => Restore(null);

    /// <param name="exePath">The running PaRuX; when given, only a task or Run value that starts exactly this program is removed without a journal.</param>
    public string? Restore(string? exePath)
    {
        try
        {
            Journal? j = File.Exists(_journalPath) ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(_journalPath)) : null;
            if (_tasks is not null)
            {
                LogonTaskInfo? current = _tasks.Get(TaskName);
                if (j is { HadTask: true, PreviousTaskXml: { Length: > 0 } xml })
                {
                    LogonTaskCallResult r = _tasks.Register(TaskName, xml);
                    if (!r.Ok) return "The previous scheduled task could not be put back" + (r.AccessDenied ? " (administrator rights needed)." : ".");
                }
                else if (current is not null && (exePath is null ? IsLogonTaskForAnyPaRuX(current) : IsOwnLogonTask(current, exePath)))
                {
                    LogonTaskCallResult r = _tasks.Delete(TaskName);
                    if (!r.Ok) return "The sign-in task could not be removed" + (r.AccessDenied ? " (administrator rights needed)." : ".");
                }
            }
            if (j is { HadValue: true, Previous: { Length: > 0 } prev }) _store.Set(ValueName, prev);
            else if (j is not null || exePath is null || IsOwnRunValue(_store.Get(ValueName), exePath)) _store.Delete(ValueName);
            if (File.Exists(_journalPath)) File.Delete(_journalPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException) { return "The sign-in entry could not be restored."; }
    }

    // Without the executable path only a task that runs some PaRuX.exe with exactly --logon counts as ours.
    private static bool IsLogonTaskForAnyPaRuX(LogonTaskInfo t) =>
        string.Equals(Path.GetFileName((t.Command ?? string.Empty).Trim().Trim('"')), "PaRuX.exe", StringComparison.OrdinalIgnoreCase)
        && string.Equals((t.Arguments ?? string.Empty).Trim(), LogonStartupArguments.LogonArgument, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Result of the "did PaRuX start at the last sign-in?" check.</summary>
public enum LogonStartHealth { Unknown, Started, NotStartedAtLastSignIn, NotRegistered }

/// <summary>
/// Pure check behind the Settings warning. PaRuX records the process start time of every <c>--logon</c> start; when the sign-in start is
/// registered, the current Windows sign-in is known and older than <see cref="Grace"/>, and no <c>--logon</c> start happened since that
/// sign-in, Windows did not start PaRuX at the last sign-in.
/// </summary>
public static class LogonStartHealthPolicy
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(3);
    /// <summary>Clock and process-start rounding tolerance.</summary>
    public static readonly TimeSpan Skew = TimeSpan.FromSeconds(5);

    public static LogonStartHealth Evaluate(bool registered, DateTimeOffset? signInUtc, DateTimeOffset? lastLogonStartUtc, DateTimeOffset nowUtc)
    {
        if (!registered) return LogonStartHealth.NotRegistered;
        if (signInUtc is null) return LogonStartHealth.Unknown;
        if (lastLogonStartUtc is { } s && s >= signInUtc.Value - Skew) return LogonStartHealth.Started;
        if (nowUtc - signInUtc.Value < Grace) return LogonStartHealth.Unknown;   // Windows may still be starting it
        return LogonStartHealth.NotStartedAtLastSignIn;
    }
}

/// <summary>The small marker a <c>--logon</c> start writes (UTC process start). Never throws.</summary>
public static class LogonStartMarker
{
    private sealed record Marker(int Version, DateTimeOffset StartedUtc, string? Via);

    public static void Write(string path, DateTimeOffset startedUtc, string? via)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Marker(1, startedUtc, via)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static DateTimeOffset? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Marker>(File.ReadAllText(path))?.StartedUtc : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

