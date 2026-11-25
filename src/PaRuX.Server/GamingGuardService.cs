using PaRuX.Server.Orchestration.Gaming;

namespace PaRuX.Server;

/// <summary>
/// Keeps the server alive while making every AI workload disposable. The marker
/// is owned by the machine's gaming integration; PaRuX only reacts to it.
/// </summary>
public sealed class GamingGuardService : BackgroundService
{
    /// <summary>The wait between shutdown attempts while a hard-off is pending. The same policy drives the retry loop and the runtime status.</summary>
    internal static readonly TimeSpan ReleaseRetryInterval = GamingShutdownRetry.RetryInterval;

    /// <summary>How often the safety-net reopen retry runs while Gaming is off (the marker itself is still polled every 250 ms).</summary>
    internal static readonly TimeSpan ReopenRetryInterval = TimeSpan.FromSeconds(2);

    private readonly LocalRuntimeService _runtime;
    private readonly MediaJobService _jobs;
    private readonly SpeechService _speech;
    private readonly GamingShutdownRetry _shutdown;
    private readonly Func<bool> _gamingMode;
    private readonly Func<CancellationToken, Task<bool>> _shutdownAttempt;
    private bool? _lastState;
    private Func<CancellationToken, Task<int?>>? _gpuUsedMibSampler;
    private Func<IReadOnlyList<int>>? _ownedInferenceProbe;

    /// <summary>Upper bound of the post-release GPU check on the retry path; tests shorten it.</summary>
    internal TimeSpan VramVerifyMaxWait { get; set; } = GamingVramReleaseVerifier.DefaultMaxWait;
    internal TimeSpan VramVerifyInterval { get; set; } = GamingVramReleaseVerifier.DefaultInterval;

    /// <summary>verified / unverified / not_released from the latest retry-path release check; null when none ran.</summary>
    internal string? LastReleaseVerification => _shutdown.LastVerification;

    /// <summary>MiB freed per the latest retry-path release check; null when unknown.</summary>
    internal long? LastVramReleasedMib => _shutdown.LastVramReleasedMib;

    /// <summary>Enables the same <see cref="GamingVramReleaseVerifier"/> check the orchestrator entry path runs (production wiring and tests).</summary>
    internal void UseReleaseVerification(Func<CancellationToken, Task<int?>> gpuUsedMibSampler, Func<IReadOnlyList<int>> ownedInferenceProbe)
    {
        _gpuUsedMibSampler = gpuUsedMibSampler;
        _ownedInferenceProbe = ownedInferenceProbe;
    }

    /// <summary>True while Gaming Mode is on and at least one local AI runtime has not yet confirmed stopped.</summary>
    public bool ShutdownPending => _shutdown.Pending;

    /// <summary>Why the latest Gaming Mode shutdown attempt did not finish; null once every runtime has stopped.</summary>
    public string? LastShutdownFailure => _shutdown.LastFailure;

    /// <summary>Last release failure while a hard-off is still pending; null once released.</summary>
    internal string? LastReleaseFailure => _shutdown.LastFailure;

    /// <summary>Test seam: replaces the real hard-off release so retry behavior is deterministic. A failed release is reported by throwing.</summary>
    internal Func<CancellationToken, Task>? ReleaseOverrideForTests { get; set; }

    /// <summary>Compatibility constructor for focused tests/older composition roots.</summary>
    public GamingGuardService(LocalRuntimeService runtime, MediaJobService jobs)
        : this(runtime, jobs, new SpeechService(runtime))
    {
    }

    /// <summary>Production DI constructor; the registered SpeechService singleton is stopped with
    /// every other AI workload when Gaming Mode becomes active.</summary>
    public GamingGuardService(LocalRuntimeService runtime, MediaJobService jobs, SpeechService speech)
        : this(runtime, jobs, speech, () => runtime.GamingMode, runtime.ForceGamingShutdownAsync)
    {
        // Production: the retry path measures the release exactly like the orchestrator entry path.
        UseReleaseVerification(GpuMemoryReleaseWaiter.SampleUsedMibAsync, runtime.OwnedInferenceProcessIds);
    }

    /// <summary>The Gaming Mode state and the shutdown attempt are injected, so the retry loop can be driven without a live
    /// runtime. The retry state is the runtime's, so the runtime status reports the same pending shutdown.</summary>
    internal GamingGuardService(LocalRuntimeService runtime, MediaJobService jobs, SpeechService speech,
        Func<bool> gamingMode, Func<CancellationToken, Task<bool>> shutdownAttempt)
    {
        _runtime = runtime;
        _jobs = jobs;
        _speech = speech;
        _gamingMode = gamingMode;
        _shutdownAttempt = shutdownAttempt;
        _shutdown = runtime.GamingShutdown;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime lastReopenRetryUtc = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime now = DateTime.UtcNow;
            // The marker is still read every 250 ms (a game must stop AI promptly), but the "retry a failed reopen" sweep over every
            // participant is only a safety net while Gaming is off, so it runs every ReopenRetryInterval instead of 4 times a second.
            bool retryReopen = now - lastReopenRetryUtc >= ReopenRetryInterval;
            if (retryReopen) lastReopenRetryUtc = now;
            await TickAsync(now, stoppingToken, retryReopen).ConfigureAwait(false);
            await Task.Delay(250, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One reconciliation step. A hard-off that fails to release (for example a resident that did not stop) is retried
    /// at the fixed interval while Gaming Mode stays active instead of being forgotten after the first attempt, and a
    /// failure never escapes: an unhandled exception here would stop the host. Nothing here ever starts or reloads AI
    /// when the marker disappears.
    /// </summary>
    internal async Task TickAsync(DateTime nowUtc, CancellationToken ct, bool retryReopen = true)
    {
        bool active = _gamingMode();
        if (_lastState != active)
        {
            _lastState = active;
            _runtime.SetGamingState(active);
            if (active)
            {
                _jobs.CancelAllForGaming();
                _speech.StopWorkersForGaming();
                _runtime.HardOff.StopAll();
                _shutdown.Begin(nowUtc);
            }
            else
            {
                _shutdown.Cleared();
                // Reopen only after the runtime Gaming marker is actually cleared.
                // The speech generation stays invalidated for all old requests.
                _speech.AllowWorkersAfterGaming();
                _runtime.HardOff.ReopenAll();
            }
        }
        else if (!active && retryReopen)
        {
            // A reopen that failed once (marker flap, child not yet exited, disposed process) must not leave AI denied
            // until restart: retry on every tick while Gaming is off. Both calls are idempotent and cheap when open.
            _speech.AllowWorkersAfterGaming();
            _runtime.HardOff.ReopenAll();
        }

        if (active && _shutdown.IsDue(nowUtc))
            await AttemptShutdownAsync(nowUtc, ct).ConfigureAwait(false);
    }

    private async Task AttemptShutdownAsync(DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            // Repeat ownership verification on each retry. A failed speech child exit
            // must not be reported as complete just because the text runtime stopped.
            if (!_speech.StopWorkersForGaming())
            {
                _shutdown.Failed("A managed speech child did not confirm exit; retrying Gaming hard-off.", nowUtc);
                return;
            }
            if (!_runtime.HardOff.StopAll())
            {
                _shutdown.Failed("A managed retrieval/avatar/vision child did not confirm exit; retrying Gaming hard-off.", nowUtc);
                return;
            }
            int? usedBeforeMib = null;
            if (_gpuUsedMibSampler is { } baseline)
            {
                try { usedBeforeMib = await baseline(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* telemetry is optional */ }
            }
            bool released;
            if (ReleaseOverrideForTests is { } release)
            {
                await release(ct).ConfigureAwait(false);
                released = true;
            }
            else
            {
                released = await _shutdownAttempt(ct).ConfigureAwait(false);
            }

            if (released)
            {
                if (_gpuUsedMibSampler is { } sampler && _ownedInferenceProbe is { } probe)
                {
                    GamingVramReleaseReport report = await GamingVramReleaseVerifier.VerifyAsync(
                        usedBeforeMib, sampler, probe, ct, VramVerifyMaxWait, VramVerifyInterval).ConfigureAwait(false);
                    _shutdown.RecordVerification(report.Outcome switch
                    {
                        GamingVramReleaseOutcome.Verified => "verified",
                        GamingVramReleaseOutcome.Unverified => "unverified",
                        _ => "not_released"
                    }, report.ReleasedMib);
                    if (report.Outcome == GamingVramReleaseOutcome.NotReleased)
                    {
                        _shutdown.Failed(report.Message + " Retrying.", nowUtc);
                        return;
                    }
                }
                _shutdown.Succeeded();
                return;
            }
            _shutdown.Failed("A local AI process did not stop and is still holding resources; retrying.", nowUtc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _shutdown.Failed("Gaming shutdown failed: " + ex.Message, nowUtc);
        }
    }
}
