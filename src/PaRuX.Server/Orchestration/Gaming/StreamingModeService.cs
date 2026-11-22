namespace PaRuX.Server.Orchestration.Gaming;

internal enum StreamingState { Idle, Starting, Active, Stopping, Failed }
internal enum StreamingStepOutcome { Done, Skipped, Failed, Cancelled, TimedOut }

internal sealed record SunshineProbe(bool Installed, bool Running);
/// <summary>A process PaRuX itself started. Only such a handle may ever be stopped by PaRuX.</summary>
internal sealed record OwnedProcessHandle(int Pid, DateTime StartedUtc);

internal sealed record StreamingDisplayTarget(string DisplayId, DisplayChangeKind Kind, string Value);

/// <summary>Explicit consent flags. Nothing is started or changed unless the matching flag is true.</summary>
internal sealed record StreamingStartRequest(bool AllowStartSunshine, StreamingDisplayTarget? Display, bool AllowDisplayChange);

internal sealed record StreamingStep(string Name, StreamingStepOutcome Outcome, string Detail);

internal sealed record StreamingStatus(StreamingState State, string Message, bool SunshineOwnedByPaRuX,
    bool DisplayRestorePending, IReadOnlyList<StreamingStep> Steps);

/// <summary>Seam over Sunshine. There is deliberately no method that edits Sunshine config, installs it or kills arbitrary processes.</summary>
internal interface IStreamingHost
{
    SunshineProbe ProbeSunshine();
    Task<OwnedProcessHandle> StartSunshineAsync(CancellationToken ct);
    Task<bool> StopOwnedSunshineAsync(OwnedProcessHandle handle, CancellationToken ct);
}

/// <summary>Display seam: read the current value, apply a new one, and (as restorer) put a journaled value back.</summary>
internal interface IStreamingDisplayController : IDisplayChangeRestorer
{
    string? Read(string displayId, DisplayChangeKind kind);
    bool Apply(string displayId, DisplayChangeKind kind, string value);
}

/// <summary>
/// Bounded, cancellable streaming-mode start/stop. Every display change goes through the write-ahead journal; failure or
/// cancellation rolls back exactly what this session did; only a Sunshine process PaRuX started is ever stopped.
/// </summary>
internal sealed class StreamingModeService
{
    internal const string JournalOwner = "streaming-mode";
    private readonly IStreamingHost _host;
    private readonly IStreamingDisplayController _display;
    private readonly DisplayChangeJournal _journal;
    private readonly TimeSpan _stepTimeout;
    private readonly object _gate = new();
    private readonly List<StreamingStep> _steps = new();
    private StreamingState _state = StreamingState.Idle;
    private string _message = "Streaming mode is off.";
    private OwnedProcessHandle? _owned;

    public StreamingModeService(IStreamingHost host, IStreamingDisplayController display, DisplayChangeJournal journal, TimeSpan stepTimeout)
    {
        _host = host; _display = display; _journal = journal; _stepTimeout = stepTimeout;
    }

    public StreamingStatus Status()
    {
        lock (_gate)
            return new(_state, _message, _owned is not null, _journal.Pending(JournalOwner).Count > 0, _steps.ToArray());
    }

    /// <summary>Call at app start: restores display changes a crashed streaming session left behind.</summary>
    public DisplayRecoveryReport RecoverAfterCrash()
    {
        DisplayRecoveryReport report = _journal.RecoverAfterCrash(_display, JournalOwner);
        lock (_gate)
        {
            if (report.Restored + report.Discarded + report.Failed > 0 || report.JournalWasCorrupt)
                _message = report.Clean
                    ? "Display settings left by an interrupted streaming session were restored."
                    : "Some display settings from an interrupted streaming session could not be restored yet; they stay recorded and will be retried.";
        }
        return report;
    }

    public async Task<StreamingStatus> StartAsync(StreamingStartRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_state is StreamingState.Starting or StreamingState.Active or StreamingState.Stopping)
                throw new InvalidOperationException("Streaming mode is already " + _state.ToString().ToLowerInvariant() + ".");
            if (_journal.Pending(JournalOwner).Count > 0)
                throw new InvalidOperationException("Display settings from a previous session are still waiting to be restored.");
            _state = StreamingState.Starting; _steps.Clear(); _owned = null; _message = "Starting streaming mode.";
        }
        try
        {
            // 1. Sunshine: never installed, never configured, never adopted for stopping.
            SunshineProbe probe = _host.ProbeSunshine();
            if (!probe.Installed)
                return await FailAsync("Sunshine is not installed. PaRuX does not install it; install it yourself, then retry.", "sunshine", StreamingStepOutcome.Failed);
            if (probe.Running)
                AddStep("sunshine", StreamingStepOutcome.Skipped, "Sunshine was already running; PaRuX will leave it running.");
            else if (!request.AllowStartSunshine)
                return await FailAsync("Sunshine is not running and starting it needs your consent.", "sunshine", StreamingStepOutcome.Skipped);
            else
            {
                OwnedProcessHandle h = await _host.StartSunshineAsync(ct).WaitAsync(_stepTimeout, ct).ConfigureAwait(false);
                lock (_gate) _owned = h;
                AddStep("sunshine", StreamingStepOutcome.Done, "Started Sunshine (PaRuX owns this process).");
            }

            // 2. Display: only with consent, journaled before the change.
            if (request.Display is null)
                AddStep("display", StreamingStepOutcome.Skipped, "No display change requested.");
            else if (!request.AllowDisplayChange)
                AddStep("display", StreamingStepOutcome.Skipped, "Display change not applied: no consent given.");
            else
            {
                StreamingDisplayTarget t = request.Display;
                string? previous = _display.Read(t.DisplayId, t.Kind);
                if (previous is null)
                    return await FailAsync("The current display value could not be read, so it cannot be restored; nothing was changed.", "display", StreamingStepOutcome.Failed);
                string id = Guid.NewGuid().ToString("N");
                _journal.Begin(new DisplayJournalEntry(id, t.Kind, t.DisplayId, previous, t.Value, DateTime.UtcNow, JournalOwner));
                bool ok = await Task.Run(() => _display.Apply(t.DisplayId, t.Kind, t.Value), CancellationToken.None)
                    .WaitAsync(_stepTimeout, ct).ConfigureAwait(false);
                if (!ok)
                {
                    _journal.Complete(id);
                    return await FailAsync("Windows rejected the display change; nothing was changed.", "display", StreamingStepOutcome.Failed);
                }
                AddStep("display", StreamingStepOutcome.Done, "Display changed; it will be restored when streaming mode stops.");
            }
            lock (_gate) { _state = StreamingState.Active; _message = "Streaming mode is active."; }
        }
        catch (OperationCanceledException) { return await FailAsync("Streaming start was cancelled; changes were rolled back.", "start", StreamingStepOutcome.Cancelled); }
        catch (TimeoutException) { return await FailAsync("A streaming step timed out; changes were rolled back.", "start", StreamingStepOutcome.TimedOut); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception) { return await FailAsync("Streaming start failed: " + ex.Message, "start", StreamingStepOutcome.Failed); }
        return Status();
    }

    /// <summary>Stops streaming mode and restores what this session changed. Restore is bounded and ignores caller cancellation.</summary>
    public async Task<StreamingStatus> StopAsync()
    {
        lock (_gate)
        {
            if (_state == StreamingState.Stopping) throw new InvalidOperationException("Streaming mode is already stopping.");
            _state = StreamingState.Stopping; _message = "Stopping streaming mode.";
        }
        bool clean = await RollbackAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _state = clean ? StreamingState.Idle : StreamingState.Failed;
            _message = clean ? "Streaming mode is off and display settings are restored."
                : "Streaming mode stopped, but some settings could not be restored yet; they stay recorded and are retried at the next start.";
        }
        return Status();
    }

    private async Task<StreamingStatus> FailAsync(string message, string step, StreamingStepOutcome outcome)
    {
        AddStep(step, outcome, message);
        bool clean = await RollbackAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _state = StreamingState.Failed;
            _message = clean ? message : message + " Rollback was incomplete; restore points stay recorded.";
        }
        return Status();
    }

    /// <summary>Undoes this session: restore journaled display values, stop an owned Sunshine. Bounded; never uses the caller's token.</summary>
    private async Task<bool> RollbackAsync()
    {
        bool clean = true;
        foreach (DisplayJournalEntry e in _journal.Pending(JournalOwner).Reverse())
        {
            RestoreOutcome r;
            try { r = await Task.Run(() => _display.Restore(e)).WaitAsync(_stepTimeout).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException) { r = RestoreOutcome.Failed; }
            if (r == RestoreOutcome.Failed) { clean = false; AddStep("restore", StreamingStepOutcome.Failed, "Could not restore " + e.Kind + " on " + e.DisplayId + "."); }
            else { _journal.Complete(e.Id); AddStep("restore", StreamingStepOutcome.Done, "Restored " + e.Kind + " on " + e.DisplayId + "."); }
        }
        OwnedProcessHandle? owned;
        lock (_gate) owned = _owned;
        if (owned is not null)
        {
            bool stopped;
            try { stopped = await _host.StopOwnedSunshineAsync(owned, CancellationToken.None).WaitAsync(_stepTimeout).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { stopped = false; }
            if (stopped) { lock (_gate) _owned = null; AddStep("sunshine-stop", StreamingStepOutcome.Done, "Stopped the Sunshine process PaRuX started."); }
            else { clean = false; AddStep("sunshine-stop", StreamingStepOutcome.Failed, "Could not stop the Sunshine process PaRuX started."); }
        }
        return clean;
    }

    private void AddStep(string name, StreamingStepOutcome outcome, string detail)
    {
        lock (_gate) _steps.Add(new StreamingStep(name, outcome, detail));
    }
}
