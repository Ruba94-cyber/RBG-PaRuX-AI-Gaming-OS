using PaRuX.Contracts;
namespace PaRuX.Server.Knowledge;

/// <summary>
/// What the automatic vector repair is doing, for the status and the Knowledge page. <c>State</c>: idle (nothing is missing),
/// waiting (the model cannot run yet or a cool-down is on), deferred (paused for Gaming, indexing or memory pressure), running,
/// gave_up (the model kept failing: the bounded attempts are spent), cancelled (the person stopped it). <c>Reason</c> is a stable code.
/// </summary>
public sealed record KnowledgeRecoveryStatus(string State, int Missing, int Attempts, int MaxAttempts, string? Reason, DateTime UpdatedUtc);

/// <summary>
/// Automatic, bounded repair of passages that were indexed WITHOUT a vector because the embedding model could not run at that moment
/// (antivirus still scanning the file, memory pressure). Without it those passages stay invisible to meaning search and a re-index
/// skips their unchanged files. Rules: only the missing passages are embedded (never another model's vectors); a single job at a
/// time, shared with the manual re-embed; Gaming, a running index job and (on a Corporate PC) low free memory pause it; the
/// person can cancel it; consecutive fruitless attempts are capped and spaced by <see cref="OnnxLoadRetryPolicy"/>. No network is used.
/// </summary>
public sealed partial class KnowledgeJobService
{
    /// <summary>Consecutive fruitless attempts before the repair stops until something changes.</summary>
    internal const int MaxAutoAttempts = 4;
    /// <summary>After giving up, one more bounded round is allowed this long later, at most <see cref="MaxAutoRearms"/> times per run.</summary>
    internal static readonly TimeSpan AutoRearmAfter = TimeSpan.FromMinutes(30);
    internal const int MaxAutoRearms = 4;
    /// <summary>Free memory, beyond what Corporate keeps for Windows and the person's programs, needed to run the small embedding model.</summary>
    internal const long MinFreeHeadroomBytes = 768L * 1024 * 1024;
    private static readonly TimeSpan RecoveryPollInterval = TimeSpan.FromMinutes(2);

    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    internal Func<bool> CorporateActive { get; set; } = () => CorporateMode.IsActive;
    internal Func<SystemRamSnapshot?> RamSnapshot { get; set; } = ExecutionResourceGuard.TryGetSystemRamSnapshot;
    internal TimeSpan RecoveryPressureWait { get; set; } = TimeSpan.FromSeconds(5);
    internal int RecoveryPressureWaits { get; set; } = 12;
    internal TimeSpan RecoveryBatchPause { get; set; } = TimeSpan.FromMilliseconds(150);

    private readonly object _autoGate = new();
    private Timer? _recoveryTimer;
    private int _tickBusy;
    private int _autoFailures;
    private long _autoLastFailureTicks;
    private int _autoRearms;
    private int _haltedAtMissing = -1;
    private string? _haltReason;
    private DateTime _haltedUtc;
    private string? _lastDeferral;
    private volatile KnowledgeRecoveryStatus _recovery = new("idle", 0, 0, MaxAutoAttempts, null, DateTime.UtcNow);

    public KnowledgeRecoveryStatus RecoveryStatus => _recovery;

    /// <summary>Begins the periodic check (first after 30 s). Idempotent. Production wiring only; tests call <see cref="TryAutoRecoverTick"/>.</summary>
    public void StartAutoRecovery()
    {
        lock (_autoGate) _recoveryTimer ??= new Timer(_ => KickAutoRecovery(), null, TimeSpan.FromSeconds(30), RecoveryPollInterval);
    }

    internal void StopAutoRecovery()
    {
        lock (_autoGate) { _recoveryTimer?.Dispose(); _recoveryTimer = null; }
    }

    private void KickAutoRecovery()
    {
        if (_recoveryTimer == null) return;
        _ = Task.Run(() => { try { TryAutoRecoverTick(); } catch (Exception) { /* a failed check must never surface; the next one retries */ } });
    }

    private void SetRecovery(string state, int missing, string? reason) =>
        _recovery = new KnowledgeRecoveryStatus(state, missing, _autoFailures, MaxAutoAttempts, reason, UtcNow());

    /// <summary>
    /// One check: starts the background repair when passages lack a vector, the embedder is installed and nothing forbids it now.
    /// Cheap when there is nothing to do. Returns true only when it started a job.
    /// </summary>
    internal bool TryAutoRecoverTick()
    {
        if (Interlocked.CompareExchange(ref _tickBusy, 1, 0) != 0) return false;
        try
        {
            if (ReembedRunning) return false;   // one job at a time (a manual re-embed, or the repair itself)
            if (Embedder is not { IsAvailable: true })
            {
                SetRecovery("waiting", _recovery.Missing, "no_embedder");
                return false;
            }
            if (_jobs.Any(j => !j.Value.Task.IsCompleted)) { SetRecovery("deferred", _recovery.Missing, "indexing"); return false; }

            int missing;
            try { missing = Index.CountMissingVectors(); }
            catch (Exception) { return false; }
            DateTime now = UtcNow();
            lock (_autoGate)
            {
                if (missing == 0)
                {
                    _autoFailures = 0; _haltedAtMissing = -1; _haltReason = null; _autoRearms = 0;
                    SetRecovery("idle", 0, null);
                    return false;
                }
                if (_haltedAtMissing >= 0)
                {
                    bool changed = missing != _haltedAtMissing;
                    bool rearm = _haltReason == "model_failing" && _autoRearms < MaxAutoRearms && now - _haltedUtc >= AutoRearmAfter;
                    if (!changed && !rearm) { SetRecovery(_haltReason == "cancelled" ? "cancelled" : "gave_up", missing, _haltReason); return false; }
                    if (rearm && !changed) _autoRearms++;
                    _haltedAtMissing = -1; _haltReason = null; _autoFailures = 0;
                }
                if (_autoFailures > 0 && !OnnxLoadRetryPolicy.MayRetry(_autoFailures, _autoLastFailureTicks, now))
                {
                    SetRecovery("waiting", missing, "cooldown");
                    return false;
                }
            }
            string? why = RecoveryDeferral();
            if (why != null) { SetRecovery("deferred", missing, why); return false; }

            SetRecovery("running", missing, null);
            if (!StartReembedCore(true, out string? refusal))
            {
                SetRecovery("deferred", missing, refusal);
                return false;
            }
            return true;
        }
        finally { Interlocked.Exchange(ref _tickBusy, 0); }
    }

    /// <summary>Why the repair must not run right now, or null when it may.</summary>
    private string? RecoveryDeferral()
    {
        if (GamingActive()) return "gaming";
        if (_jobs.Any(j => !j.Value.Task.IsCompleted)) return "indexing";
        SystemRamSnapshot? ram = RamSnapshot();
        if (CorporateActive())
        {
            // Fail closed: an unreadable memory sample on a Corporate PC is treated as pressure.
            if (ram?.IsValid != true) return "memory_pressure";
            long free = (long)ram.AvailableBytes - RuntimeMemoryBudget.CorporateReserveBytes(ram);
            if (free < MinFreeHeadroomBytes) return "memory_pressure";
        }
        else if (ram?.IsValid == true && ram.AvailableBytes < 512UL * 1024 * 1024) return "memory_pressure";
        return null;
    }

    /// <summary>Awaited before each batch of the repair: waits a bounded time while something forbids it, then stops cleanly.</summary>
    private async Task<bool> RecoveryGateAsync(CancellationToken ct)
    {
        for (int i = 0; ; i++)
        {
            string? why = RecoveryDeferral();
            if (why == null)
            {
                if (RecoveryBatchPause > TimeSpan.Zero) await Task.Delay(RecoveryBatchPause, ct).ConfigureAwait(false);   // low priority: yield between batches
                return true;
            }
            _lastDeferral = why;
            SetRecovery("deferred", _recovery.Missing, why);
            if (i >= RecoveryPressureWaits) return false;
            await Task.Delay(RecoveryPressureWait, ct).ConfigureAwait(false);
        }
    }

    private void OnAutoRecoveryFinished(KnowledgeReembedResult r, bool deferred, bool cancelRequested)
    {
        lock (_autoGate)
        {
            if (r.Cancelled || cancelRequested)
            {
                if (_haltReason == "cancelled") { _haltedAtMissing = r.Remaining; SetRecovery("cancelled", r.Remaining, "cancelled"); }
                else SetRecovery("waiting", r.Remaining, "superseded");
                return;
            }
            if (deferred) { SetRecovery("deferred", r.Remaining, _lastDeferral); return; }   // not a model failure: no attempt spent
            if (r.Remaining == 0)
            {
                _autoFailures = 0; _autoRearms = 0;
                SetRecovery("idle", 0, null);
                return;
            }
            if (r.Updated > 0) _autoFailures = 0;   // progress: keep going on the next check
            else
            {
                _autoFailures++;
                _autoLastFailureTicks = UtcNow().Ticks;
                if (_autoFailures >= MaxAutoAttempts)
                {
                    _haltedAtMissing = r.Remaining; _haltReason = "model_failing"; _haltedUtc = UtcNow();
                    SetRecovery("gave_up", r.Remaining, "model_failing");
                    return;
                }
            }
            SetRecovery("waiting", r.Remaining, r.Updated > 0 ? "partial" : "model_failing");
        }
    }

    private void OnAutoRecoveryFailed()
    {
        lock (_autoGate)
        {
            _autoFailures++;
            _autoLastFailureTicks = UtcNow().Ticks;
            if (_autoFailures >= MaxAutoAttempts) { _haltedAtMissing = _recovery.Missing; _haltReason = "model_failing"; _haltedUtc = UtcNow(); SetRecovery("gave_up", _recovery.Missing, "model_failing"); }
            else SetRecovery("waiting", _recovery.Missing, "model_failing");
        }
    }

    private void OnManualReembedFinished(KnowledgeReembedResult r)
    {
        if (r.Cancelled) return;
        lock (_autoGate)
        {
            _autoFailures = 0; _haltedAtMissing = -1; _haltReason = null; _autoRearms = 0;
            SetRecovery(r.Remaining == 0 ? "idle" : "waiting", r.Remaining, null);
        }
    }

    private void NoteAutoRecoveryCancelledByPerson()
    {
        lock (_autoGate)
        {
            _haltedAtMissing = _recovery.Missing; _haltReason = "cancelled"; _haltedUtc = UtcNow();
        }
    }
}
