using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Playnite;

/// <summary>What the Playnite session signal may ask of the AI side. Implemented over <see cref="WorkloadOrchestrator"/>.</summary>
internal interface IPlayniteYieldTarget
{
    string CurrentState { get; }
    long? ActiveSessionVersion { get; }
    Task<long?> EnterAsync(CancellationToken ct);
    Task<bool> ExitAsync(long version, CancellationToken ct);
    void RegisterOwnedGame(int pid, DateTime startUtc, string imagePath, long version);
    void ClearOwnedGame(long version);
}

internal sealed class OrchestratorYieldTarget : IPlayniteYieldTarget
{
    private readonly WorkloadOrchestrator _orchestrator;
    public OrchestratorYieldTarget(WorkloadOrchestrator orchestrator) => _orchestrator = orchestrator;
    public string CurrentState => _orchestrator.CurrentState;
    public long? ActiveSessionVersion => _orchestrator.ActivePlayniteGameSessionVersion;
    public Task<long?> EnterAsync(CancellationToken ct) => _orchestrator.EnterPlayniteGameSessionAsync(ct);
    public Task<bool> ExitAsync(long version, CancellationToken ct) => _orchestrator.ExitPlayniteGameSessionAsync(version, ct);
    public void RegisterOwnedGame(int pid, DateTime startUtc, string imagePath, long version) =>
        _orchestrator.RegisterOwnedGameProcess(new WorkloadOrchestrator.OwnedGameProcess(pid, startUtc, imagePath, version));
    public void ClearOwnedGame(long version) => _orchestrator.ClearOwnedGameProcess(version);
}

/// <summary>
/// Watches the Playnite process tree and turns the game-session signal into the existing Gaming hard-off: stop PaRuX-owned inference
/// (VRAM release is verified inside the orchestrator), pause downloads/indexing, and on exit restore AI availability without reloading
/// models. It starts and stops nothing but its own orchestrator transition, so it can never terminate or suspend a process PaRuX does not own.
/// Entry and exit are bounded (3 attempts with back-off), cancellable, and a failure is reported as a failure, never as success.
/// </summary>
internal sealed class PlayniteSessionService : BackgroundService
{
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan[] Backoff = { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DetectCache = TimeSpan.FromSeconds(20);

    private readonly PlayniteSettingsStore _settings;
    private readonly PlayniteLocator _locator;
    private readonly PlayniteLauncher _launcher;
    private readonly IProcessSnapshotSource _source;
    private readonly IPlayniteYieldTarget _target;
    private readonly PlayniteGameSessionTracker _tracker;
    private readonly Func<DateTime> _clock;
    private readonly ILogger<PlayniteSessionService>? _logger;
    private readonly object _gate = new();

    private PlayniteComponentDto? _component;
    private DateTime _componentAt = DateTime.MinValue;
    private string _state = "idle";
    private long? _drivenVersion;
    private bool _pendingYield;
    private int _attempts;
    private DateTime _nextAttempt;
    private int _exitAttempts;
    private bool _pendingExit;
    private DateTime _nextExitAttempt;
    private string? _lastError;
    private DateTime? _sessionStartedUtc, _lastEndedUtc;
    private PlayniteTrackerResult? _last;
    private DateTime _boostUntil = DateTime.MinValue;
    private bool _lastTickFailed;

    /// <summary>How long the watcher waits before its next snapshot (see <see cref="PlayniteWatchCadence"/>).</summary>
    internal TimeSpan NextDelay => ComputeNextDelay();

    /// <summary>Asks for the fast tick for <see cref="PlayniteWatchCadence.BoostWindow"/>: PaRuX is launching Playnite, or the UI is
    /// actively watching the session. Never slows anything down; the idle tick resumes by itself afterwards.</summary>
    internal void Boost()
    {
        lock (_gate) _boostUntil = _clock() + PlayniteWatchCadence.BoostWindow;
    }

    public PlayniteSessionService(PlayniteSettingsStore settings, PlayniteLocator locator, PlayniteLauncher launcher,
        IProcessSnapshotSource source, IPlayniteYieldTarget target, PlayniteGameSessionTracker? tracker = null,
        Func<DateTime>? clock = null, ILogger<PlayniteSessionService>? logger = null)
    {
        _settings = settings;
        _locator = locator;
        _launcher = launcher;
        _source = source;
        _target = target;
        _tracker = tracker ?? new PlayniteGameSessionTracker(Environment.ProcessId);
        _clock = clock ?? (() => DateTime.UtcNow);
        _logger = logger;
    }

    public PlayniteComponentDto Component(bool refresh = false)
    {
        PlayniteSettingsDto s = _settings.Get();
        lock (_gate)
        {
            if (refresh || _component is null || _clock() - _componentAt > DetectCache)
            {
                _component = _locator.Detect(_settings.LocatedFolder);
                _componentAt = _clock();
            }
            _component.Skipped = s.Skipped;
            if (s.Skipped) _component.Actions = new[] { "unskip" };
            return _component;
        }
    }

    public PlayniteSessionDto GetSession()
    {
        lock (_gate)
        {
            return new PlayniteSessionDto
            {
                State = _state,
                PlayniteRunning = _last?.PlayniteAlive ?? false,
                PaRuXLaunchedPlaynite = _launcher.PaRuXLaunched,
                LaunchedMode = _launcher.LaunchedMode,
                TrackedProcessCount = _last?.Game.Count ?? 0,
                GameProcessName = _last?.Primary?.Name,
                SessionStartedUtc = _sessionStartedUtc,
                LastEndedUtc = _lastEndedUtc,
                LastError = _lastError,
                YieldAttempts = _attempts,
                PaRuXLaunchedClosedCounter = _launcher.ClosedCounter
            };
        }
    }

    /// <summary>True while a detected game is running (used to refuse a mode switch).</summary>
    public bool GameRunning { get { lock (_gate) return _last?.GameActive ?? false; } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken).ConfigureAwait(false); lock (_gate) _lastTickFailed = false; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // The watcher must survive any single failure; it reports it instead of dying silently.
                lock (_gate) { _lastError = "The Playnite watcher failed once: " + ex.GetType().Name; _lastTickFailed = true; }
                _logger?.LogWarning("Playnite watcher tick failed: {Type}", ex.GetType().Name);
            }
            try { await Task.Delay(ComputeNextDelay(), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private TimeSpan ComputeNextDelay()
    {
        PlayniteSettingsDto settings = _settings.Get();
        lock (_gate)
        {
            bool driving = _drivenVersion.HasValue || _pendingYield || _pendingExit;
            var signals = new PlayniteWatchSignals(
                IntegrationEnabled: !settings.Skipped && settings.AiYieldEnabled,
                PlayniteAlive: _last?.PlayniteAlive ?? false,
                GameCandidateOrActive: (_last?.GameActive ?? false) || (_last?.Game.Count ?? 0) > 0,
                Driving: driving,
                PaRuXLaunchedPlaynite: _launcher.PaRuXLaunched,
                BoostActive: _clock() < _boostUntil,
                LastTickFailed: _lastTickFailed);
            return PlayniteWatchCadence.Next(signals);
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        PlayniteSettingsDto settings = _settings.Get();
        bool driving;
        lock (_gate) driving = _drivenVersion.HasValue || _pendingYield || _pendingExit;
        if (settings.Skipped || !settings.AiYieldEnabled)
        {
            if (!driving)
            {
                if (_tracker.GameActive || _last is not null) _tracker.Reset();
                lock (_gate) { _last = null; _state = "disabled"; }
                return;
            }
        }
        else
        {
            lock (_gate) if (_state == "disabled") _state = "idle";
        }

        DateTime now = _clock();
        PlayniteComponentDto component = Component();
        PlayniteTrackerResult result;
        bool nothingTracked;
        lock (_gate) nothingTracked = !driving && !(_last?.GameActive ?? false) && (_last?.Game.Count ?? 0) == 0 && !_tracker.GameActive;
        if (nothingTracked && !_source.AnyProcessNamed(PlayniteGameSessionTracker.PlayniteNames))
        {
            // No Playnite process exists and nothing is tracked, so a full sample could only return "nothing": skip the process table.
            // Reset drops any per-process cache so no stale id (Windows reuses them) survives into the next real sample.
            _tracker.Reset();
            result = new PlayniteTrackerResult(PlayniteTrackerEvent.None, false, false, Array.Empty<TrackedProcess>(), null);
        }
        else result = _tracker.Sample(_source.Capture(), now, _source.Identify, component.Folder);
        lock (_gate) _last = result;

        switch (result.Event)
        {
            case PlayniteTrackerEvent.Start:
                OnGameStarted(result, now);
                break;
            case PlayniteTrackerEvent.End:
                OnGameEnded(now);
                break;
        }

        await DriveAsync(result, now, ct).ConfigureAwait(false);
    }

    private void OnGameStarted(PlayniteTrackerResult result, DateTime now)
    {
        lock (_gate)
        {
            _sessionStartedUtc = now;
            _lastError = null;
            _attempts = 0;
            _exitAttempts = 0;
            _pendingExit = false;
            if (_target.CurrentState != "AiReady")
            {
                // Gaming is already on (another session, or a hard-off still held): AI is already off, there is nothing to drive.
                _state = "yielded";
                _drivenVersion = null;
                _pendingYield = false;
                return;
            }
            _state = "game-detected";
            _pendingYield = true;
            _nextAttempt = now;
        }
    }

    private void OnGameEnded(DateTime now)
    {
        lock (_gate)
        {
            _lastEndedUtc = now;
            _pendingYield = false;
            if (_drivenVersion.HasValue)
            {
                _pendingExit = true;
                _exitAttempts = 0;
                _nextExitAttempt = now;
                _state = "restoring";
            }
            else
            {
                _state = "idle";
            }
        }
    }

    private async Task DriveAsync(PlayniteTrackerResult result, DateTime now, CancellationToken ct)
    {
        bool tryEnter, tryExit;
        long? version;
        lock (_gate)
        {
            version = _drivenVersion;
            // The owner pressed "Return to AI" during the game: respect it for the rest of this game.
            if (version.HasValue && !_pendingExit && _target.ActiveSessionVersion != version)
            {
                _target.ClearOwnedGame(version.Value);
                _drivenVersion = null;
                _state = "user-override";
                version = null;
            }
            tryEnter = _pendingYield && now >= _nextAttempt;
            tryExit = _pendingExit && now >= _nextExitAttempt;
        }

        if (tryEnter) await TryEnterAsync(result, now, ct).ConfigureAwait(false);
        if (tryExit && version is { } v) await TryExitAsync(v, now, ct).ConfigureAwait(false);
    }

    private async Task TryEnterAsync(PlayniteTrackerResult result, DateTime now, CancellationToken ct)
    {
        lock (_gate) { _attempts++; _state = "yielding"; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AttemptTimeout);
        long? version = null;
        string? failure = null;
        try { version = await _target.EnterAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = "Entering Gaming timed out."; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { failure = "Entering Gaming failed: " + ex.GetType().Name; }

        lock (_gate)
        {
            if (version.HasValue)
            {
                _drivenVersion = version;
                _pendingYield = false;
                _state = "yielded";
                _lastError = null;
                // Pause/resume is offered only for a game inside a Playnite tree that PaRuX itself started.
                if (_launcher.PaRuXLaunched && result.Primary is { Path: not null } primary)
                    _target.RegisterOwnedGame(primary.Pid, primary.StartUtc, primary.Path, version.Value);
                return;
            }
            failure ??= "Gaming resources could not be released; AI was left as it was.";
            _lastError = failure;
            if (_attempts >= MaxAttempts)
            {
                _pendingYield = false;
                _state = "failed";
            }
            else
            {
                _state = "game-detected";
                _nextAttempt = now + Backoff[Math.Min(_attempts, Backoff.Length - 1)];
            }
        }
    }

    private async Task TryExitAsync(long version, DateTime now, CancellationToken ct)
    {
        lock (_gate) _exitAttempts++;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AttemptTimeout);
        bool ok = false;
        string? failure = null;
        try { ok = await _target.ExitAsync(version, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = "Restoring AI timed out."; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { failure = "Restoring AI failed: " + ex.GetType().Name; }

        lock (_gate)
        {
            // The version moved on (the owner returned to AI by hand, or a newer session began): nothing is left to restore.
            if (!ok && _target.ActiveSessionVersion != version && _target.CurrentState == "AiReady") ok = true;
            if (ok)
            {
                _target.ClearOwnedGame(version);
                _drivenVersion = null;
                _pendingExit = false;
                _state = "idle";
                _lastError = null;
                return;
            }
            _lastError = failure ?? "AI could not be restored automatically. Use Return to AI.";
            if (_exitAttempts >= MaxAttempts)
            {
                _pendingExit = false;
                _drivenVersion = null;
                _state = "failed";
            }
            else _nextExitAttempt = now + Backoff[Math.Min(_exitAttempts, Backoff.Length - 1)];
        }
    }
}
