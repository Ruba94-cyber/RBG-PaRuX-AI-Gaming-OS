using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX;

/// <summary>
/// Console/TV start preference. Opt-in (Settings > Gaming > Console / TV): when the chosen TV is connected at launch the app opens
/// directly in Console/TV mode on it; otherwise it starts as the owner chose (normal AI window, or minimized). It reuses the existing
/// display selection policy and <see cref="EnterConsoleModeAsync"/>; it never changes Windows display settings and never uses another
/// screen than the one the owner marked as TV.
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
            if (!data.ConsoleStartOnTvWhenPresent) return;   // default: the app starts exactly as before
            string targetId = data.ConsoleTargetDisplayId ?? string.Empty;
            bool tvPresent = false;
            if (!string.IsNullOrWhiteSpace(targetId))
            {
                int absentSeen = 0;
                for (int probe = 0; probe < ConsoleStartupMaxProbes && !App.IsShuttingDown; probe++)
                {
                    var topology = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
                    bool readable = topology.Succeeded;
                    bool present = readable && ConsoleDisplaySelectionPolicy.SelectTarget(
                        topology.Value, SettingsStore.Current.Data.ConsoleDisplays, targetId) is not null;
                    if (readable && !present) absentSeen++;
                    // Pure, tested decision: present stops, a TV absent twice stops (a TV that just woke can take a moment), otherwise wait and retry within the budget.
                    ConsoleProbeStep step = ConsoleStartupProbePolicy.Next(probe, ConsoleStartupMaxProbes, readable, present, absentSeen, ConsoleStartupAbsentConfirmations);
                    tvPresent = step == ConsoleProbeStep.Present;
                    if (step is ConsoleProbeStep.Present or ConsoleProbeStep.Absent or ConsoleProbeStep.GiveUp) break;
                    await Task.Delay(ConsoleStartupProbeInterval);
                }
            }
            if (App.IsShuttingDown) return;

            switch (ConsoleStartupPolicy.Decide(data.ConsoleStartOnTvWhenPresent, targetId, tvPresent, data.StartupWithoutTv))
            {
                case ConsoleStartupDecision.EnterConsole:
                    if (_consoleModeActive || _consoleRoutingInProgress || GamingManager.Current.IsActive
                        || LocalAiController.Current.IsGameActive())
                        return;
                    _consoleRoutingInProgress = true;
                    try
                    {
                        // Preferred Console UI is Playnite Fullscreen + the owner's Aniki theme:
                        // avoid creating the heavy legacy Gaming Home just to cover it immediately.
                        if (await TryStartPlayniteConsoleAsync())
                        {
                            if (AppWindow.Presenter is OverlappedPresenter overlappedConsole)
                                overlappedConsole.Minimize();
                            Log.Write("Console startup: Playnite Fullscreen accepted; PaRuX remains available for AI.");
                            break;
                        }

                        // Fallback only when Playnite is absent, skipped, refused, or not actually Fullscreen.
                        await EnterConsoleModeAsync();
                        Log.Write("Console startup: Playnite unavailable; opened the legacy Console fallback.");
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

    /// <summary>
    /// Console/TV workspace switch from the controller: Gaming library to the AI chat and back, without leaving fullscreen.
    /// Returns true when handled.
    /// </summary>
    private bool SwitchConsoleWorkspace()
    {
        if (!_consoleModeActive || _consoleRoutingInProgress) return false;
        if (GameLibraryPageIfCreated?.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
        {
            ShowChat();
            return true;
        }
        ShowStudioWorkspace(StudioWorkspace.Games);
        GameLibraryPage.ApplyConsoleMode(true);
        GameLibraryPage.OnShown();
        GameLibraryPage.FocusConsoleHome();
        return true;
    }

    /// <summary>True while the Gaming library is the visible workspace of Console/TV mode.</summary>
    private bool ConsoleOnGamingWorkspace() => GameLibraryPageIfCreated?.Visibility == Microsoft.UI.Xaml.Visibility.Visible;
}
