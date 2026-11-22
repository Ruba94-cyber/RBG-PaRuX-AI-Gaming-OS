using System.Diagnostics;
using System.Runtime.InteropServices;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers;

namespace PaRuX.Server.Orchestration.Gaming.Playnite;

/// <summary>A Playnite process PaRuX itself started (the only kind PaRuX may ask to close).</summary>
internal interface IOwnedPlayniteProcess : IDisposable
{
    int Id { get; }
    DateTime StartUtc { get; }
    bool HasExited { get; }
    bool CloseMainWindow();
    bool WaitForExit(int milliseconds);
    event Action? Exited;
}

internal interface IPlayniteProcessHost
{
    IOwnedPlayniteProcess? Start(string exePath, string workingDirectory, bool minimized);
    /// <summary>Brings the window of an already running Playnite to the front. Returns false when Windows kept the focus elsewhere.</summary>
    bool Focus(int pid);
}

internal sealed class WindowsPlayniteProcessHost : IPlayniteProcessHost
{
    private sealed class Owned : IOwnedPlayniteProcess
    {
        private readonly Process _process;
        public Owned(Process process)
        {
            _process = process;
            StartUtc = process.StartTime.ToUniversalTime();
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => Exited?.Invoke();
        }
        public int Id => _process.Id;
        public DateTime StartUtc { get; }
        public bool HasExited { get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } } }
        public bool CloseMainWindow() { try { return _process.CloseMainWindow(); } catch (InvalidOperationException) { return false; } }
        public bool WaitForExit(int milliseconds) { try { return _process.WaitForExit(milliseconds); } catch (InvalidOperationException) { return true; } }
        public event Action? Exited;
        public void Dispose() => _process.Dispose();
    }

    public IOwnedPlayniteProcess? Start(string exePath, string workingDirectory, bool minimized)
    {
        var info = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            WindowStyle = minimized ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
        };
        Process? started = Process.Start(info);
        return started is null ? null : new Owned(started);
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    public bool Focus(int pid)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using Process p = Process.GetProcessById(pid);
            IntPtr hwnd = p.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return false;
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
            SetForegroundWindow(hwnd);
            return GetForegroundWindow() == hwnd;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}

/// <summary>
/// Starts Playnite Desktop or Fullscreen: validated executable, its own folder as working directory, one instance, and an optional
/// chosen display (only when that display is connected). It never edits Playnite, never starts a second copy, and closes only a
/// Playnite process it started itself, by asking the window to close (never by terminating).
/// </summary>
internal sealed class PlayniteLauncher
{
    private readonly IPlayniteProcessHost _host;
    private readonly IPlayniteEnvironment _env;
    private readonly Func<string?, bool> _displayConnected;
    private readonly object _gate = new();
    private IOwnedPlayniteProcess? _owned;
    private string? _ownedMode;
    private long _closedCounter;

    public PlayniteLauncher(IPlayniteProcessHost host, IPlayniteEnvironment env, Func<string?, bool> displayConnected)
    {
        _host = host;
        _env = env;
        _displayConnected = displayConnected;
    }

    public bool PaRuXLaunched { get { lock (_gate) return _owned is { HasExited: false }; } }
    public string? LaunchedMode { get { lock (_gate) return _owned is { HasExited: false } ? _ownedMode : null; } }
    public long ClosedCounter => Interlocked.Read(ref _closedCounter);
    public int? OwnedPid { get { lock (_gate) return _owned is { HasExited: false } ? _owned.Id : null; } }
    public (int Pid, DateTime StartUtc)? OwnedIdentity { get { lock (_gate) return _owned is { HasExited: false } o ? (o.Id, o.StartUtc) : null; } }
    public string? LastExePath { get; private set; }

    /// <summary>Decides the mode to start: an explicit request wins; the owner's "only when the TV is connected" rule uses ConsoleStartupPolicy.</summary>
    internal (string Mode, bool Minimized, string? Note) ResolveMode(PlayniteSettingsDto settings, string? requestedMode, string? requestedDisplay)
    {
        string mode = PlayniteModes.IsValid(requestedMode) ? requestedMode! : settings.StartMode;
        if (mode != PlayniteModes.Console) return (PlayniteModes.Desktop, false, null);
        string? display = string.IsNullOrWhiteSpace(requestedDisplay) ? settings.TargetDisplayId : requestedDisplay;
        if (!settings.ConsoleOnlyWhenTargetConnected) return (PlayniteModes.Console, false, null);
        ConsoleStartupDecision decision = ConsoleStartupPolicy.Decide(true, display, _displayConnected(display), settings.FallbackWithoutDisplay);
        return decision switch
        {
            ConsoleStartupDecision.EnterConsole => (PlayniteModes.Console, false, null),
            ConsoleStartupDecision.Minimize => (PlayniteModes.Desktop, true, "The chosen display is not connected: Playnite Desktop was started minimized."),
            _ => (PlayniteModes.Desktop, false, "The chosen display is not connected: Playnite Desktop was started instead of Fullscreen.")
        };
    }

    public PlayniteLaunchResultDto Launch(PlayniteComponentDto component, PlayniteSettingsDto settings, string? requestedMode, string? requestedDisplay)
    {
        if (settings.Skipped) return Refused("Playnite is skipped. Choose it again in Settings before starting it.");
        if (!component.Detected || component.Folder is null) return Refused("Playnite was not found. Locate it or install it first.");
        (string mode, bool minimized, string? note) = ResolveMode(settings, requestedMode, requestedDisplay);
        string? exe = mode == PlayniteModes.Console ? component.FullscreenExe : component.DesktopExe;
        if (exe is null) return Refused(mode == PlayniteModes.Console
            ? "This Playnite folder has no Fullscreen executable." : "This Playnite folder has no Desktop executable.");
        if (!CustomExecutableInputProvider.IsSafeHelperPath(exe)) return Refused("The Playnite executable is missing, linked or unsafe to start.");

        lock (_gate)
        {
            // One instance: any Playnite already running is brought forward, never duplicated.
            PlayniteRunningProcess? running = _env.RunningPlaynite()
                .FirstOrDefault(r => PlayniteLocator.IsInFolder(r.ExePath, component.Folder));
            if (running is not null)
            {
                bool sameMode = string.Equals(Path.GetFileName(running.ExePath), Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase);
                bool focused = _host.Focus(running.Pid);
                return new PlayniteLaunchResultDto
                {
                    Ok = sameMode,
                    Outcome = "already-running",
                    Mode = sameMode ? mode : (mode == PlayniteModes.Console ? PlayniteModes.Desktop : PlayniteModes.Console),
                    Message = sameMode
                        ? (focused ? "Playnite is already running; it was brought to the front." : "Playnite is already running; Windows kept the focus elsewhere.")
                        : "Playnite is already running in the other mode. Use Switch mode (works when PaRuX started it) or close it first."
                };
            }
            try
            {
                IOwnedPlayniteProcess? started = _host.Start(exe, component.Folder, minimized);
                if (started is null) return Refused("Windows did not start Playnite.");
                _owned?.Dispose();
                _owned = started;
                _ownedMode = mode;
                LastExePath = exe;
                started.Exited += () => Interlocked.Increment(ref _closedCounter);
                if (started.HasExited) Interlocked.Increment(ref _closedCounter);
                return new PlayniteLaunchResultDto { Ok = true, Outcome = "started", Mode = mode, Message = note };
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return Refused("Playnite could not be started: " + ex.GetType().Name);
            }
        }
    }

    /// <summary>Console &lt;-&gt; Desktop. Only possible for a Playnite that PaRuX started and only while no game is running.</summary>
    public PlayniteLaunchResultDto Switch(PlayniteComponentDto component, PlayniteSettingsDto settings, bool gameRunning, string? displayId)
    {
        string? current;
        IOwnedPlayniteProcess? owned;
        lock (_gate) { owned = _owned is { HasExited: false } ? _owned : null; current = owned is null ? null : _ownedMode; }
        if (owned is null)
            return Refused("Playnite was not started by PaRuX, so PaRuX will not close it. Close it yourself, then start the other mode here.");
        if (gameRunning) return Refused("A game is running. Close the game first.");
        string target = current == PlayniteModes.Console ? PlayniteModes.Desktop : PlayniteModes.Console;
        owned.CloseMainWindow();
        if (!owned.WaitForExit(10_000))
            return Refused("Playnite did not close in time (it may be asking to save). PaRuX never force-closes it; close it yourself.");
        return Launch(component, settings, target, displayId);
    }

    private static PlayniteLaunchResultDto Refused(string message) => new() { Ok = false, Outcome = "refused", Message = message };
}
