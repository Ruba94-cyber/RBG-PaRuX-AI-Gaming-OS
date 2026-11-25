namespace PaRuX.Server.Orchestration.Gaming.Playnite;

/// <summary>What the Playnite watcher knows when it decides how long to wait before the next process snapshot.</summary>
internal readonly record struct PlayniteWatchSignals(
    bool IntegrationEnabled,
    bool PlayniteAlive,
    bool GameCandidateOrActive,
    bool Driving,
    bool PaRuXLaunchedPlaynite,
    bool BoostActive,
    bool LastTickFailed);

/// <summary>
/// Pure policy for the Playnite session watcher tick. A process snapshot costs CPU (it was about 1.1% of a core at a fixed 1 s tick
/// while nothing was happening), so the watcher waits <see cref="Idle"/> only while NOTHING can start a game session: the integration
/// is on, no Playnite process is alive, nothing is tracked, no yield/restore is in flight, PaRuX did not just launch Playnite and no
/// caller asked for urgency. In every other state the cadence is the original <see cref="Fast"/> 1 s, so yielding the GPU is exactly as
/// quick as before once Playnite exists (a store-launcher game is always a child of a running Playnite, never the other way round).
/// The only added delay is noticing a Playnite that has just appeared: at most <see cref="MaxExtraDetectionDelay"/>.
/// </summary>
internal static class PlayniteWatchCadence
{
    /// <summary>The original tick: used whenever a session could start or is in progress.</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromSeconds(1);

    /// <summary>Tick while no Playnite process exists and nothing is in flight.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);

    /// <summary>Tick while the integration is switched off or skipped (nothing to watch; a settings change is noticed within this).</summary>
    public static readonly TimeSpan Disabled = TimeSpan.FromSeconds(5);

    /// <summary>The stated worst-case extra delay before a freshly started Playnite is noticed compared with the 1 s tick.</summary>
    public static readonly TimeSpan MaxExtraDetectionDelay = Idle - Fast;

    /// <summary>How long a caller-requested boost keeps the fast tick (PaRuX launching Playnite, a live status read from the UI).</summary>
    public static readonly TimeSpan BoostWindow = TimeSpan.FromSeconds(60);

    public static TimeSpan Next(PlayniteWatchSignals s)
    {
        if (s.LastTickFailed) return Fast;
        if (!s.IntegrationEnabled) return s.Driving ? Fast : Disabled;
        if (s.PlayniteAlive || s.GameCandidateOrActive || s.Driving || s.PaRuXLaunchedPlaynite || s.BoostActive) return Fast;
        return Idle;
    }
}
