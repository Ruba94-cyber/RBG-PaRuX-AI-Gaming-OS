using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX;

/// <summary>
/// Console/TV start preference. Opt-in (Settings > Gaming > Console / TV): when the chosen TV is connected at launch, Console mode
/// (Playnite Fullscreen with the owner's theme) is started; otherwise PaRuX starts as the owner chose (normal AI window, or minimized).
/// Since gaming-removal step 3 there is no native PaRuX fullscreen fallback: if Playnite cannot start, PaRuX stays in its normal
/// window. It never changes Windows display settings and never uses another screen than the one the owner marked as TV.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan ConsoleStartupProbeInterval = TimeSpan.FromSeconds(2);
    private const int ConsoleStartupMaxProbes = 7;      // Core may still be starting, or Windows may still be enumerating the TV
    private const int ConsoleStartupAbsentConfirmations = 2;

    internal async Task ApplyConsoleStartupPreferenceAsync()
    {
        try
        {
            var data = SettingsStore.Current.Data;
            if (data.StartInConsoleMode && await ApplyAlwaysConsoleStartupAsync()) return;   // simple option: no TV, any pad
            if (!data.ConsoleStartOnTvWhenPresent) return;  // default: the app starts exactly as before
            ConsoleTvSimulation.Mode simulation = ConsoleTvSimulation.FromEnvironment();   // Debug/test seam only; Off in Release
            await MigrateLegacyTvChoiceAsync();
            // Any selected TV (from the persistent list) that is connected counts; no TV selected means nothing to wait for.
            ConsoleStartupTvProbe.Result probe = await ConsoleStartupTvProbe.RunAsync(
                GamingCoreTopologyProvider.Provider, () => SettingsStore.Current.Data.ConsoleTvs, simulation,
                (delay, ct) => Task.Delay(delay, ct), () => App.IsShuttingDown, ConsoleStartupMaxProbes,
                ConsoleStartupAbsentConfirmations, ConsoleStartupProbeInterval);
            if (App.IsShuttingDown) return;
            RefreshTvSettingsFromSnapshot(probe.Snapshot);
            string targetId = probe.DisplayId ?? string.Empty;
            bool tvPresent = probe.TvPresent;

            switch (ConsoleStartupPolicy.Decide(data.ConsoleStartOnTvWhenPresent, targetId, tvPresent, data.StartupWithoutTv))
            {
                case ConsoleStartupDecision.EnterConsole:
                    if (_consoleRoutingInProgress || GamingManager.Current.IsActive
                        || LocalAiController.Current.IsGameActive())
                        return;
                    _consoleRoutingInProgress = true;
                    try
                    {
                        // Preferred Console UI is Playnite Fullscreen + the owner's Aniki theme:
                        // avoid creating the heavy legacy Gaming Home just to cover it immediately.
                        ConsolePlayniteStartResult handoff = await TryStartPlayniteConsoleAsync();
                        ConsoleStartFollowUp follow = ConsoleModeReadiness.FollowUp(handoff, alwaysConsole: false);
                        if (follow == ConsoleStartFollowUp.MinimizePaRuX)
                        {
                            MinimizeForPlayniteConsole();
                            Log.Write("Console startup: Playnite Fullscreen accepted; PaRuX remains available for AI.");
                            break;
                        }
                        if (follow == ConsoleStartFollowUp.StayOpen)
                        {
                            Log.Write("Console startup: Playnite started Desktop due to display fallback; no native Console takeover.");
                            break;
                        }

                        // Playnite was NOT started at all: the native Console/TV shell no longer exists, so PaRuX stays in its normal window.
                        Log.Write("Console startup: Playnite Fullscreen was not started (" + handoff + "); PaRuX stays in its normal window.");
                    }
                    finally { _consoleRoutingInProgress = false; }
                    break;
                case ConsoleStartupDecision.Minimize:
                    if (AppWindow.Presenter is OverlappedPresenter overlapped)
                    {
                        overlapped.Minimize();
                        Log.Write("Console/TV start preference: TV not connected, started minimized.");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            // A failed preference must never stop the app from being usable: it simply stays in the normal window.
            Log.Write("Console/TV start preference failed: " + ex.GetType().Name + ": " + ex.Message);
            _consoleRoutingInProgress = false;
        }
    }

    /// <summary>Refreshes the TV history and the older single-target fields from a snapshot Core just returned; saves only when something changed.</summary>
    internal static void RefreshTvSettingsFromSnapshot(GamingDisplaySnapshotDto? snapshot)
    {
        if (snapshot is null) return;
        if (ConsoleTvSettingsSync.Apply(SettingsStore.Current.Data, snapshot, ConsoleTvSimulation.Mode.Off, DateTime.UtcNow, out _))
            SettingsStore.Current.Save();
    }

    /// <summary>
    /// One-time: an owner who picked a single TV with an older PaRuX has no entry in the new list yet. Wait (bounded) for the first readable
    /// display list and turn that choice into a selected entry, so the start preference keeps working after the update.
    /// </summary>
    private async Task MigrateLegacyTvChoiceAsync()
    {
        var data = SettingsStore.Current.Data;
        if (data.ConsoleTvsMigrated || string.IsNullOrWhiteSpace(data.ConsoleTargetDisplayId)) return;
        for (int probe = 0; probe < ConsoleStartupMaxProbes && !App.IsShuttingDown; probe++)
        {
            GamingDisplaySnapshotDto? snapshot = await GamingCoreTopologyProvider.Provider.ReadAsync(default);
            if (snapshot is { Supported: true }) { RefreshTvSettingsFromSnapshot(snapshot); return; }
            await Task.Delay(ConsoleStartupProbeInterval);
        }
    }
}
