using System.Diagnostics;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>Process operations behind the runner, so the run logic is testable without starting anything.</summary>
public interface ISupportAppHost
{
    IReadOnlyList<SupportAppProcess> Find(string processName);
    /// <summary>Starts the program (no shell). Returns its process id, or null when it could not start.</summary>
    SupportAppProcess? Start(string path, string? args, bool minimized);
    /// <summary>Asks the exact process (id + start time) to close, then ends it if it ignored the request. False when it is not that process any more.</summary>
    bool Stop(SupportAppProcess process);
    DateTime? ExeWriteUtc(string path);
}

/// <summary>Production host: CreateProcess without a shell, and exact (pid + start time) stops only.</summary>
public sealed class SupportAppProcessHost : ISupportAppHost
{
    public IReadOnlyList<SupportAppProcess> Find(string processName)
    {
        var list = new List<SupportAppProcess>();
        foreach (Process p in Process.GetProcessesByName(processName))
        {
            try { list.Add(new SupportAppProcess(p.Id, SafePath(p), SafeStart(p))); }
            finally { p.Dispose(); }
        }
        return list;
    }

    public SupportAppProcess? Start(string path, string? args, bool minimized)
    {
        try
        {
            string full = Path.GetFullPath(path);
            var info = new ProcessStartInfo
            {
                FileName = full,
                WorkingDirectory = Path.GetDirectoryName(full)!,
                Arguments = args ?? string.Empty,
                UseShellExecute = false,
                WindowStyle = minimized ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal,
            };
            using Process? p = Process.Start(info);
            return p is null ? null : new SupportAppProcess(p.Id, full, SafeStart(p));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Stop(SupportAppProcess process)
    {
        try
        {
            using Process p = Process.GetProcessById(process.Pid);
            if (process.StartUtc is DateTime expected && SafeStart(p) is DateTime actual && Math.Abs((actual - expected).TotalSeconds) > 2) return false;
            if (p.HasExited) return true;
            p.CloseMainWindow();
            if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); }
            return p.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return true;   // already gone
        }
    }

    public DateTime? ExeWriteUtc(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? SafePath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
    }

    private static DateTime? SafeStart(Process p)
    {
        try { return p.StartTime.ToUniversalTime(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
    }
}

/// <summary>
/// Starts the owner's support apps when Console begins and stops the ones marked "stop with Console" when it ends. Off unless the
/// list is on; refuses while Gaming is active (checked at start, before each launch and every second of a wait); an app already
/// running is left alone (unless it asked to replace a stale copy); only a process PaRuX started this session is ever stopped, by
/// exact pid + start time. Bounded wait per app, cancellable, every outcome observable and logged without paths.
/// </summary>
public sealed class SupportAppRunner
{
    private readonly SupportAppStore _store;
    private readonly ISupportAppHost _host;
    private readonly Func<bool> _gamingActive;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<long> _clockMs;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, SupportAppProcess> _started = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SupportAppResult> _last = [];
    private readonly SemaphoreSlim _one = new(1, 1);

    public SupportAppRunner(SupportAppStore store, ISupportAppHost host, Func<bool> gamingActive,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<long>? clockMs = null, Action<string>? log = null)
    {
        _store = store; _host = host; _gamingActive = gamingActive;
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        long origin = Stopwatch.GetTimestamp();
        _clockMs = clockMs ?? (() => (long)Stopwatch.GetElapsedTime(origin).TotalMilliseconds);
        _log = log ?? (_ => { });
    }

    public IReadOnlyList<SupportAppResult> LastRun { get { lock (_gate) return _last; } }

    public bool IsRunningNow(SupportAppEntry e)
    {
        string? name = SupportAppPolicy.ProbeName(e);
        return name is not null && _host.Find(name).Count > 0;
    }

    public async Task<IReadOnlyList<SupportAppResult>> StartAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SupportAppListState state = _store.Load();
            var results = new List<SupportAppResult>();
            if (!state.Enabled || state.Damaged) return Publish(results);
            foreach (SupportAppEntry e in state.Apps.Where(a => a.StartWithConsole))
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await StartOneAsync(e, ct).ConfigureAwait(false));
            }
            return Publish(results);
        }
        finally { _one.Release(); }
    }

    private async Task<SupportAppResult> StartOneAsync(SupportAppEntry e, CancellationToken ct)
    {
        long t0 = _clockMs();
        SupportAppResult Done(SupportAppOutcome o, string why)
        {
            var r = new SupportAppResult(e.Id, e.Name, o, why, _clockMs() - t0);
            _log($"support app {e.Id}: {o} ({why}) in {r.ElapsedMs} ms");
            return r;
        }

        string? problem = SupportAppPolicy.Validate(e);
        string? probe = SupportAppPolicy.ProbeName(e);
        IReadOnlyList<SupportAppProcess> running = probe is null ? [] : _host.Find(probe);
        SupportAppStartDecision decision = SupportAppPolicy.DecideStart(
            e, _gamingActive(), problem, SupportAppPolicy.IsAvailabilityProblem(e), running,
            problem is null ? _host.ExeWriteUtc(e.Path) : null);

        switch (decision)
        {
            case SupportAppStartDecision.SkippedGamingActive: return Done(SupportAppOutcome.SkippedGamingActive, "Gaming is on");
            case SupportAppStartDecision.SkippedDisabled: return Done(SupportAppOutcome.SkippedDisabled, "turned off");
            case SupportAppStartDecision.SkippedMissing: return Done(SupportAppOutcome.SkippedMissing, "program not found");
            case SupportAppStartDecision.SkippedInvalid: return Done(SupportAppOutcome.SkippedInvalid, "entry not valid");
            case SupportAppStartDecision.AlreadyRunning: return Done(SupportAppOutcome.AlreadyRunning, "already running");
        }

        bool replaced = false;
        if (decision == SupportAppStartDecision.ReplaceStale)
        {
            foreach (SupportAppProcess p in running) _host.Stop(p);
            replaced = true;
        }

        if (_gamingActive()) return Done(SupportAppOutcome.SkippedGamingActive, "Gaming is on");
        SupportAppProcess? started = _host.Start(e.Path, e.Args, e.Minimized);
        if (started is null) return Done(SupportAppOutcome.Failed, "could not start");
        lock (_gate) _started[e.Id] = started;

        // Bounded wait for the program to show up as a process (some helpers hand over to another process).
        int seconds = Math.Clamp(e.TimeoutSeconds, SupportAppLimits.MinTimeoutSeconds, SupportAppLimits.MaxTimeoutSeconds);
        for (int waited = 0; waited < seconds; waited++)
        {
            if (probe is null || _host.Find(probe).Count > 0) return Done(replaced ? SupportAppOutcome.ReplacedStale : SupportAppOutcome.Started, replaced ? "replaced an older copy" : "started");
            if (_gamingActive()) return Done(SupportAppOutcome.SkippedGamingActive, "Gaming started while waiting");
            await _delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        return probe is null || _host.Find(probe).Count > 0
            ? Done(replaced ? SupportAppOutcome.ReplacedStale : SupportAppOutcome.Started, "started")
            : Done(SupportAppOutcome.TimedOut, "did not appear in time");
    }

    /// <summary>Stops, by exact process identity, the apps that are marked "stop with Console" AND were started by this runner.</summary>
    public async Task<IReadOnlyList<SupportAppResult>> StopAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SupportAppListState state = _store.Load();
            var results = new List<SupportAppResult>();
            foreach (SupportAppEntry e in state.Apps.Where(a => a.StopWithConsole))
            {
                SupportAppProcess? p;
                lock (_gate) { _started.TryGetValue(e.Id, out p); _started.Remove(e.Id); }
                if (p is null) continue;   // PaRuX did not start it: never stopped
                long t0 = _clockMs();
                bool ok = _host.Stop(p);
                var r = new SupportAppResult(e.Id, e.Name, ok ? SupportAppOutcome.Stopped : SupportAppOutcome.NotStopped, ok ? "stopped" : "not the same process any more", _clockMs() - t0);
                _log($"support app {e.Id}: {r.Outcome} in {r.ElapsedMs} ms");
                results.Add(r);
            }
            return Publish(results);
        }
        finally { _one.Release(); }
    }

    private IReadOnlyList<SupportAppResult> Publish(List<SupportAppResult> results)
    {
        lock (_gate) _last = results;
        return results;
    }
}
