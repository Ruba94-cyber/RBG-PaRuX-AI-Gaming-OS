using System.Runtime.Versioning;
using System.Text;

namespace PaRuX.Contracts;

/// <summary>What the Playnite Console Mode launcher is asked to do.</summary>
public enum PlayniteLauncherAction
{
    /// <summary>Nothing was asked: PaRuX starts exactly as its own start preferences say.</summary>
    None,
    /// <summary>Open the launcher surface (Console, Desktop, Streaming, Settings and live status).</summary>
    Open,
    /// <summary>Start Playnite in Fullscreen (Console) mode.</summary>
    Console,
    /// <summary>Start Playnite in Desktop mode.</summary>
    Desktop,
    /// <summary>Start streaming mode (Sunshine plus the display plan).</summary>
    Streaming,
    /// <summary>Open the Console Mode settings.</summary>
    Settings
}

/// <summary>The result of reading a command line: what to do, and whether the large controller layout was asked for.</summary>
public sealed record PlayniteLauncherRequest(PlayniteLauncherAction Action, bool Gamepad)
{
    public static PlayniteLauncherRequest Nothing { get; } = new(PlayniteLauncherAction.None, false);
    public bool HasAction => Action != PlayniteLauncherAction.None;
}

/// <summary>
/// "PaRuX.exe --playnite-console | --playnite-desktop | --streaming | --playnite-settings | --playnite-launcher [--playnite-gamepad]":
/// how a shortcut, a Jump List task, the tray or another PaRuX process asks for a Console Mode action. The arguments carry no path,
/// no command and no value, so a planted argument can only ask PaRuX to show its own launcher.
/// When several are present the safest wins in the owner's order: settings, then streaming, then console, then desktop.
/// </summary>
public static class PlayniteLauncherArguments
{
    public const string ConsoleArgument = "--playnite-console";
    public const string DesktopArgument = "--playnite-desktop";
    public const string StreamingArgument = "--streaming";
    public const string SettingsArgument = "--playnite-settings";
    public const string LauncherArgument = "--playnite-launcher";
    public const string GamepadArgument = "--playnite-gamepad";

    /// <summary>Reads the arguments of a command line whose first element is the executable. Unknown arguments are ignored.</summary>
    public static PlayniteLauncherRequest Parse(IReadOnlyList<string>? commandLine)
    {
        if (commandLine == null || commandLine.Count < 2) return PlayniteLauncherRequest.Nothing;
        bool console = false, desktop = false, streaming = false, settings = false, launcher = false, gamepad = false;
        for (int i = 1; i < commandLine.Count; i++)
        {
            string arg = (commandLine[i] ?? string.Empty).Trim();
            if (Same(arg, ConsoleArgument)) console = true;
            else if (Same(arg, DesktopArgument)) desktop = true;
            else if (Same(arg, StreamingArgument)) streaming = true;
            else if (Same(arg, SettingsArgument)) settings = true;
            else if (Same(arg, LauncherArgument)) launcher = true;
            else if (Same(arg, GamepadArgument)) gamepad = true;
        }
        PlayniteLauncherAction action =
            settings ? PlayniteLauncherAction.Settings
            : streaming ? PlayniteLauncherAction.Streaming
            : console ? PlayniteLauncherAction.Console
            : desktop ? PlayniteLauncherAction.Desktop
            : launcher ? PlayniteLauncherAction.Open
            : PlayniteLauncherAction.None;
        return new PlayniteLauncherRequest(action, gamepad);
    }

    /// <summary>The command-line arguments a shortcut passes for one action. Nothing for None.</summary>
    public static string ArgumentsFor(PlayniteLauncherAction action) => action switch
    {
        PlayniteLauncherAction.Console => ConsoleArgument,
        PlayniteLauncherAction.Desktop => DesktopArgument,
        PlayniteLauncherAction.Streaming => StreamingArgument,
        PlayniteLauncherAction.Settings => SettingsArgument,
        PlayniteLauncherAction.Open => LauncherArgument,
        _ => string.Empty
    };

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The decided start action and the plain reason (for the log and the launcher's message line).</summary>
public sealed record PlayniteLauncherResolution(PlayniteLauncherAction Action, string Reason);

/// <summary>
/// Startup-mode resolution: an explicit argument always wins; otherwise a held
/// Ctrl opens the settings and a held Shift starts streaming (Ctrl+Shift = settings, so a stray Shift never starts streaming when
/// the owner meant the settings); with nothing held and nothing asked, PaRuX does what its own start preferences say.
/// </summary>
public static class PlayniteLauncherModeResolver
{
    public static PlayniteLauncherResolution Resolve(PlayniteLauncherRequest? explicitRequest, bool ctrlHeld, bool shiftHeld)
    {
        if (explicitRequest is { HasAction: true } r) return new(r.Action, "argument " + PlayniteLauncherArguments.ArgumentsFor(r.Action));
        if (ctrlHeld) return new(PlayniteLauncherAction.Settings, shiftHeld ? "Ctrl and Shift held: settings win" : "Ctrl held");
        if (shiftHeld) return new(PlayniteLauncherAction.Streaming, "Shift held");
        return new(PlayniteLauncherAction.None, "no argument and no modifier");
    }

    /// <summary>
    /// Samples the modifier keys at the start of the process and then for at most <paramref name="grace"/>, because a key pressed
    /// together with a click can reach the window a moment after the process began. A modifier already seen at the first sample is
    /// returned at once. Every dependency is injected, so the wait is bounded and testable.
    /// </summary>
    public static (bool Ctrl, bool Shift) Sample(
        (bool Ctrl, bool Shift) early, Func<(bool Ctrl, bool Shift)> read, Action<TimeSpan> sleep, Func<DateTime> utcNow,
        TimeSpan grace, TimeSpan step)
    {
        if (early.Ctrl || early.Shift) return early;
        DateTime deadline = utcNow() + (grace < TimeSpan.Zero ? TimeSpan.Zero : grace);
        TimeSpan pause = step <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(20) : step;
        while (true)
        {
            (bool Ctrl, bool Shift) now = read();
            if (now.Ctrl || now.Shift) return now;
            if (utcNow() >= deadline) return (false, false);
            sleep(pause);
        }
    }
}

/// <summary>What one probe of the connected displays means for the TV start preference.</summary>
public enum ConsoleProbeStep
{
    /// <summary>The chosen TV is connected: stop probing.</summary>
    Present,
    /// <summary>The displays were read twice without the TV: stop probing, the TV is not there.</summary>
    Absent,
    /// <summary>Not decided yet: wait one interval and probe again.</summary>
    RetryAfterDelay,
    /// <summary>The probe budget is used up: treat the TV as not connected (the owner's fallback applies).</summary>
    GiveUp
}

/// <summary>
/// Bounded wait for the TV at start (Core may still be starting; Windows may still be enumerating a TV that just woke). Pure, so the
/// time limit and the "confirm absence once more" rule are tested without a display.
/// </summary>
public static class ConsoleStartupProbePolicy
{
    public const int DefaultMaxProbes = 7;
    public const int DefaultAbsentConfirmations = 2;

    /// <param name="probeIndex">Zero-based number of the probe that just finished.</param>
    /// <param name="topologyReadable">Core answered with the display list.</param>
    /// <param name="tvPresent">The chosen TV is in that list.</param>
    /// <param name="absentSeen">Readable-but-absent answers so far, including this one (the caller counts).</param>
    public static ConsoleProbeStep Next(int probeIndex, int maxProbes, bool topologyReadable, bool tvPresent, int absentSeen, int absentConfirmations)
    {
        if (topologyReadable)
        {
            if (tvPresent) return ConsoleProbeStep.Present;
            if (absentSeen >= Math.Max(1, absentConfirmations)) return ConsoleProbeStep.Absent;
        }
        return probeIndex + 1 >= Math.Max(1, maxProbes) ? ConsoleProbeStep.GiveUp : ConsoleProbeStep.RetryAfterDelay;
    }
}

/// <summary>
/// Single-instance request forwarding between PaRuX processes: a second process writes the action into a small queue folder in the
/// data root and wakes the running one with a session-local event (the same pattern as <see cref="GameLaunchRequestChannel"/>). The
/// request is only an action name from a fixed list, never a path or a command; stale requests are dropped and the latest one wins.
/// </summary>
public static class PlayniteLauncherRequestChannel
{
    private const string Extension = ".plr";
    public const int MaxQueuedRequests = 8;
    public static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(2);

    public static string QueueDirectoryFor(string dataRoot) => Path.Combine(dataRoot, "launcher-requests");

    public static string EventNameFor(string dataRoot) => GameLaunchRequestChannel.EventNameFor(dataRoot).Replace(".Play.", ".Launcher.", StringComparison.Ordinal);

    public static bool IsRequestable(PlayniteLauncherAction action) => action != PlayniteLauncherAction.None;

    public static bool TryEnqueue(string dataRoot, PlayniteLauncherAction action)
    {
        if (!IsRequestable(action)) return false;
        try
        {
            string directory = QueueDirectoryFor(dataRoot);
            Directory.CreateDirectory(directory);
            if (Directory.EnumerateFiles(directory, "*" + Extension).Take(MaxQueuedRequests).Count() >= MaxQueuedRequests) return false;
            string finalPath = Path.Combine(directory, DateTime.UtcNow.Ticks.ToString("D19") + "-" + Guid.NewGuid().ToString("N") + Extension);
            string temp = finalPath + ".tmp";
            File.WriteAllText(temp, ArgumentsFor(action), Encoding.UTF8);
            File.Move(temp, finalPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    [SupportedOSPlatform("windows")]
    public static bool TrySubmit(string dataRoot, PlayniteLauncherAction action, out bool runningInstanceNotified)
    {
        runningInstanceNotified = false;
        if (!TryEnqueue(dataRoot, action)) return false;
        runningInstanceNotified = AppExitSignal.TrySignal(EventNameFor(dataRoot));
        return true;
    }

    [SupportedOSPlatform("windows")]
    public static IDisposable Listen(string dataRoot, Action onRequest) => GameLaunchRequestChannel.ListenOn(EventNameFor(dataRoot), onRequest);

    /// <summary>Takes the queued requests (oldest first), deleting each file. Invalid, oversized and stale ones are dropped.</summary>
    public static IReadOnlyList<PlayniteLauncherAction> Drain(string dataRoot, DateTime? nowUtc = null)
    {
        var actions = new List<PlayniteLauncherAction>();
        string directory = QueueDirectoryFor(dataRoot);
        if (!Directory.Exists(directory)) return actions;
        DateTime now = nowUtc ?? DateTime.UtcNow;
        string[] files;
        try { files = Directory.EnumerateFiles(directory, "*" + Extension).OrderBy(f => f, StringComparer.Ordinal).Take(MaxQueuedRequests).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return actions; }
        foreach (string file in files)
        {
            try
            {
                var info = new FileInfo(file);
                PlayniteLauncherAction action = PlayniteLauncherAction.None;
                if (now - info.LastWriteTimeUtc <= MaxRequestAge && info.Length <= 64)
                    action = TryParse(File.ReadAllText(file, Encoding.UTF8));
                File.Delete(file);
                if (action != PlayniteLauncherAction.None) actions.Add(action);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return actions;
    }

    /// <summary>Latest request wins (a Settings request is not overridden by an earlier Console one).</summary>
    public static PlayniteLauncherAction PickWinner(IReadOnlyList<PlayniteLauncherAction> drained) =>
        drained.Count == 0 ? PlayniteLauncherAction.None : drained[^1];

    private static string ArgumentsFor(PlayniteLauncherAction action) => action.ToString().ToLowerInvariant();

    private static PlayniteLauncherAction TryParse(string text) =>
        Enum.TryParse(text.Trim(), ignoreCase: true, out PlayniteLauncherAction a) && IsRequestable(a) && Enum.IsDefined(a)
            && text.Trim().All(char.IsLetter) ? a : PlayniteLauncherAction.None;
}

public enum PlannedShortcutState
{
    /// <summary>Will be written as a new file.</summary>
    Create,
    /// <summary>An identical PaRuX shortcut is already there: nothing is written.</summary>
    AlreadyExists
}

/// <summary>One shortcut that PaRuX proposes. Nothing is written until the owner confirms the list.</summary>
public sealed record PlannedShortcut(PlayniteLauncherAction Action, string Path, string Arguments, string Description, PlannedShortcutState State);

/// <summary>
/// Plans the optional desktop shortcuts (and Jump List tasks) for the launcher actions. The plan only reads: the file-system and
/// shortcut access are injected, so tests never touch the real desktop. A file that exists and is not the identical PaRuX shortcut is
/// never overwritten (the next free name is proposed); removal only proposes shortcuts that point at this PaRuX with exactly one of
/// these arguments, so a user's own shortcut is never in the list.
/// </summary>
public static class PlayniteLauncherShortcuts
{
    public const int MaxCandidates = 10;

    /// <summary>The four launcher actions a shortcut can start, with the plain names used for the files and the Jump List.</summary>
    public static IReadOnlyList<(PlayniteLauncherAction Action, string Name, string Description)> Specs { get; } = new[]
    {
        (PlayniteLauncherAction.Console, "Playnite Console (PaRuX)", "Open Playnite in Console (Fullscreen) mode with PaRuX"),
        (PlayniteLauncherAction.Desktop, "Playnite Desktop (PaRuX)", "Open Playnite in Desktop mode with PaRuX"),
        (PlayniteLauncherAction.Streaming, "Streaming mode (PaRuX)", "Start streaming mode with PaRuX"),
        (PlayniteLauncherAction.Settings, "Console Mode settings (PaRuX)", "Open the Playnite Console Mode settings in PaRuX")
    };

    public static PlayniteLauncherAction[] AllActions { get; } = Specs.Select(s => s.Action).ToArray();

    public static IReadOnlyList<PlannedShortcut> PlanCreate(
        string folder, string executablePath, IEnumerable<PlayniteLauncherAction> wanted,
        Func<string, bool> pathExists, Func<string, ShortcutTarget?> readShortcut)
    {
        var plan = new List<PlannedShortcut>();
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(executablePath)) return plan;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlayniteLauncherAction action in wanted.Distinct())
        {
            var spec = Specs.FirstOrDefault(s => s.Action == action);
            if (spec.Name is null) continue;
            string arguments = PlayniteLauncherArguments.ArgumentsFor(action);
            for (int attempt = 0; attempt < MaxCandidates; attempt++)
            {
                string file = attempt == 0 ? spec.Name : spec.Name[..^1] + " " + (attempt + 1) + ")";
                string path = Path.Combine(folder, file + ".lnk");
                if (used.Contains(path)) continue;
                if (pathExists(path))
                {
                    ShortcutTarget? existing = readShortcut(path);
                    if (existing != null && IsOurs(existing, executablePath, arguments))
                    {
                        used.Add(path);
                        plan.Add(new PlannedShortcut(action, path, arguments, spec.Description, PlannedShortcutState.AlreadyExists));
                        break;
                    }
                    continue;   // someone else's or edited file: keep it, try the next name
                }
                used.Add(path);
                plan.Add(new PlannedShortcut(action, path, arguments, spec.Description, PlannedShortcutState.Create));
                break;
            }
        }
        return plan;
    }

    /// <summary>The shortcut files in <paramref name="folder"/> that PaRuX created (same executable, exactly one of its arguments).</summary>
    public static IReadOnlyList<string> PlanRemove(
        string folder, string executablePath, Func<string, IEnumerable<string>> listShortcuts, Func<string, ShortcutTarget?> readShortcut)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(executablePath)) return result;
        foreach (string path in listShortcuts(folder))
        {
            ShortcutTarget? target = readShortcut(path);
            if (target == null) continue;
            if (AllActions.Any(a => IsOurs(target, executablePath, PlayniteLauncherArguments.ArgumentsFor(a)))) result.Add(path);
        }
        return result;
    }

    private static bool IsOurs(ShortcutTarget target, string executablePath, string arguments) =>
        string.Equals(target.TargetPath, executablePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(target.Arguments.Trim(), arguments, StringComparison.Ordinal);
}
