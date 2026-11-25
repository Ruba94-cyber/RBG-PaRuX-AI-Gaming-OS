namespace PaRuX.Contracts;

/// <summary>What the return watch should do after one status sample.</summary>
public enum PlayniteReturnDecision
{
    /// <summary>Nothing to act on yet (Playnite still open, no answer, or not started by PaRuX).</summary>
    KeepWatching,
    /// <summary>Playnite Fullscreen already closed but a game it started is still running: do not touch the focus.</summary>
    WaitForGame,
    /// <summary>The PaRuX-started Playnite and every tracked game have ended and the owner wants PaRuX back: bring it forward.</summary>
    ReturnToPaRuX,
    /// <summary>Playnite ended but PaRuX must not come forward (preference off, or Playnite was started by the user): only clean up.</summary>
    SettledNoReturn
}

public enum PlayniteReturnWatchOutcome { Returned, SettledNoReturn, TimedOut, Cancelled }

/// <summary>Pure, single decision of the Playnite return watch. No I/O, no clock: fully unit-testable.</summary>
public static class PlayniteConsoleReturnPolicy
{
    /// <param name="baseline">Closed counter captured before the launch.</param>
    /// <param name="externalOwned">Playnite was already running and was not started by PaRuX (never resume for it).</param>
    /// <param name="observedExternalRunning">An external Playnite was seen running at least once.</param>
    /// <param name="forceReturn">An explicit Console handoff always comes back, independent of the optional preference.</param>
    public static PlayniteReturnDecision Decide(long? baseline, PlayniteStatusDto? status, bool forceReturn,
        bool externalOwned = false, bool observedExternalRunning = false)
    {
        if (status is null) return PlayniteReturnDecision.KeepWatching;
        if (externalOwned)
            return PlayniteReturnWatchPolicy.ExternalCloseSettled(status, observedExternalRunning)
                ? PlayniteReturnDecision.SettledNoReturn
                : PlayniteReturnDecision.KeepWatching;
        if (PlayniteReturnWatchPolicy.OwnedCloseSettled(baseline, status))
            return PlayniteReturnWatchPolicy.ShouldBringForward(baseline, status, forceReturn)
                ? PlayniteReturnDecision.ReturnToPaRuX
                : PlayniteReturnDecision.SettledNoReturn;
        return PlayniteReturnWatchPolicy.KeepPendingClose(baseline, status, forceReturn)
            ? PlayniteReturnDecision.WaitForGame
            : PlayniteReturnDecision.KeepWatching;
    }
}

/// <summary>
/// The bounded, single-loop Playnite return watch, with its status source, delay and clock injected so tests can drive it with a fake
/// watcher. It never returns more than once, never loops after a decision, and ends by itself when nothing of Playnite is alive for
/// <c>maxIdle</c> consecutive samples.
/// </summary>
public static class PlayniteReturnWatcher
{
    public static async Task<PlayniteReturnWatchOutcome> RunAsync(
        Func<CancellationToken, Task<PlayniteStatusDto?>> poll,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<bool> shuttingDown,
        Action<string> log,
        long? baseline,
        bool forceReturn,
        bool externalOwned,
        bool externalInitiallyRunning,
        string returnLogLine,
        TimeSpan interval,
        int maxIdle = 40,
        CancellationToken ct = default)
    {
        bool observedExternalRunning = externalInitiallyRunning;
        int idle = 0;
        bool waitingLogged = false;
        while (!shuttingDown() && !ct.IsCancellationRequested && idle < maxIdle)
        {
            PlayniteStatusDto? status = await poll(ct).ConfigureAwait(false);
            if (status is not null)
            {
                baseline ??= status.Session.PaRuXLaunchedClosedCounter;
                if (externalOwned) observedExternalRunning |= status.Session.PlayniteRunning || status.Component.Running;
                switch (PlayniteConsoleReturnPolicy.Decide(baseline, status, forceReturn, externalOwned, observedExternalRunning))
                {
                    case PlayniteReturnDecision.ReturnToPaRuX:
                        log(returnLogLine);
                        return PlayniteReturnWatchOutcome.Returned;
                    case PlayniteReturnDecision.SettledNoReturn:
                        log(externalOwned
                            ? "Playnite closed (started by the user): PaRuX stays where it is."
                            : "Playnite closed: PaRuX stays where it is (return preference is off).");
                        return PlayniteReturnWatchOutcome.SettledNoReturn;
                    case PlayniteReturnDecision.WaitForGame:
                        if (!waitingLogged) { log("Playnite closed while a game is still running: waiting for the game before returning to PaRuX."); waitingLogged = true; }
                        break;
                    default:
                        // A mode switch closes one owned Playnite and starts another: follow the new counter only while the replacement is
                        // alive. Never advance it merely because the counter moved: the close may still be settling (a stale process
                        // snapshot) and swallowing that bump is exactly what silently cancelled the return.
                        if (!externalOwned && status.Session.PaRuXLaunchedPlaynite)
                            baseline = status.Session.PaRuXLaunchedClosedCounter;
                        break;
                }
                idle = PlayniteReturnWatchPolicy.HasActiveFrontendOrGames(status) ? 0 : idle + 1;
            }
            else idle++;
            try { await delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return PlayniteReturnWatchOutcome.Cancelled; }
        }
        return ct.IsCancellationRequested ? PlayniteReturnWatchOutcome.Cancelled : PlayniteReturnWatchOutcome.TimedOut;
    }
}
