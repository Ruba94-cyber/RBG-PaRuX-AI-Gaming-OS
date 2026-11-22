using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Pure decision for the once-per-Windows-sign-in trigger. The shell reports the sign-in session (start of its explorer.exe)
/// and its own start time; the list starts only when the owner enabled it, Gaming is off, the shell really started right
/// after that sign-in (not hours later by hand) and this sign-in session has not run the list yet.
/// </summary>
public static class SignInTriggerPolicy
{
    public const int MaxSessionKeyLength = 64;
    /// <summary>The shell must start within this long after the sign-in session started.</summary>
    public static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(10);
    /// <summary>Clock jitter allowance when the shell starts "before" the session timestamp.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(2);

    public static SignInTriggerDecision Decide(
        bool enabled, int appCount, bool gamingActive, bool alreadyRunning, string? lastRunSessionKey, SignInTriggerRequest request)
    {
        if (!enabled) return SignInTriggerDecision.Disabled;
        if (appCount == 0) return SignInTriggerDecision.EmptyList;
        if (gamingActive) return SignInTriggerDecision.GamingActive;
        if (string.IsNullOrWhiteSpace(request.SessionKey) || request.SessionKey.Length > MaxSessionKeyLength ||
            request.SessionStartUtc is null || request.ShellStartUtc is null)
            return SignInTriggerDecision.SignInUnknown;
        if (string.Equals(request.SessionKey, lastRunSessionKey, StringComparison.Ordinal))
            return SignInTriggerDecision.AlreadyRanThisSignIn;
        TimeSpan sinceSignIn = request.ShellStartUtc.Value - request.SessionStartUtc.Value;
        if (sinceSignIn < -ClockTolerance || sinceSignIn > SignInWindow) return SignInTriggerDecision.NotAtSignIn;
        if (alreadyRunning) return SignInTriggerDecision.AlreadyRunning;
        return SignInTriggerDecision.Started;
    }
}

/// <summary>
/// Owns the one background run of the after-sign-in list: persisted list in, <see cref="SignInAppListService"/> run out.
/// The run is started at most once per sign-in session by <see cref="Trigger"/> (the shell calls it once after it started),
/// or on demand by <see cref="RunNow"/>. Gaming hard-off is read live from the runtime; the Console/TV state is whatever the
/// shell last reported through <see cref="ConsoleActive"/>. Disposing cancels a run in progress.
/// </summary>
public sealed class SignInAppListRunner : IDisposable
{
    private readonly SignInAppListStore _store;
    private readonly SignInAppListService _service;
    private readonly Func<bool> _gamingActive;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task _current = Task.CompletedTask;
    private volatile bool _consoleActive;

    public SignInAppListRunner(SignInAppListStore store, ISignInAppLauncher launcher, Func<bool> gamingActive,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _store = store;
        _gamingActive = gamingActive;
        _service = new SignInAppListService(launcher, gamingActive, () => _consoleActive, delay);
    }

    /// <summary>The shell's real Console/TV state, as last reported.</summary>
    public bool ConsoleActive { get => _consoleActive; set => _consoleActive = value; }

    public SignInRunSnapshot Snapshot() => _service.Snapshot();

    /// <summary>The background run (completed when idle); tests await it.</summary>
    public Task CurrentRun { get { lock (_gate) return _current; } }

    public SignInTriggerResponse Trigger(SignInTriggerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _consoleActive = request.ConsoleActive;
            SignInAppListState state = _store.Load();
            SignInTriggerDecision decision = SignInTriggerPolicy.Decide(
                state.List.Enabled && !state.Damaged, state.List.Apps.Count, _gamingActive(), !_current.IsCompleted,
                state.LastRunSessionKey, request);
            if (decision != SignInTriggerDecision.Started) return new(decision, _service.Snapshot());
            // Consume the sign-in first: a crash mid-run must not relaunch the whole list at the next shell start.
            if (!_store.MarkRan(request.SessionKey!)) return new(SignInTriggerDecision.Disabled, _service.Snapshot());
            StartLocked(state.List);
            return new(SignInTriggerDecision.Started, _service.Snapshot());
        }
    }

    /// <summary>Owner-initiated run of the saved list (ignores the sign-in session, still opt-in, Gaming hard-off and cancellable).</summary>
    public SignInTriggerResponse RunNow()
    {
        lock (_gate)
        {
            SignInAppListState state = _store.Load();
            if (!state.List.Enabled || state.Damaged) return new(SignInTriggerDecision.Disabled, _service.Snapshot());
            if (state.List.Apps.Count == 0) return new(SignInTriggerDecision.EmptyList, _service.Snapshot());
            if (_gamingActive()) return new(SignInTriggerDecision.GamingActive, _service.Snapshot());
            if (!_current.IsCompleted) return new(SignInTriggerDecision.AlreadyRunning, _service.Snapshot());
            StartLocked(state.List);
            return new(SignInTriggerDecision.Started, _service.Snapshot());
        }
    }

    public void Cancel()
    {
        lock (_gate) _cts?.Cancel();
    }

    private void StartLocked(SignInAppList list)
    {
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;
        // Task.Run: the one-second slices and launches must never run on a request thread.
        _current = Task.Run(async () =>
        {
            try { await _service.RunAsync(list, cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* the service reports outcomes; never crash the Core */ }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
