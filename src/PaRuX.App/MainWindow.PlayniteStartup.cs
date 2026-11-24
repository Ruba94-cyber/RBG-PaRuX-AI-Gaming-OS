using System;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX;

/// <summary>
/// Playnite Console Mode glue: optional Playnite start with PaRuX, the pad "switch Console/Desktop" action and returning to PaRuX when a
/// Playnite that PaRuX started closes. Core owns every decision (display connected? fallback? single instance?); this partial only asks
/// and then brings PaRuX forward. Nothing here touches Playnite's files or any process PaRuX did not start.
/// </summary>
public sealed partial class MainWindow
{
    private int _playniteWatchRunning;

    internal static string? PlayniteDisplayId()
    {
        string id = SettingsStore.Current.Data.ConsoleTargetDisplayId ?? string.Empty;
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>Called once after the Console/TV start decision. Does nothing unless the owner turned "Start Playnite with PaRuX" on.</summary>
    internal async Task ApplyPlayniteStartupAsync(bool hasPlayRequest)
    {
        try
        {
            if (hasPlayRequest || App.IsShuttingDown || _consoleModeActive ||
                _playniteConsoleHandoffActive || _playniteDesktopFallbackAtStartup) return;
            var status = await PlayniteClient.Current.GetStatusAsync();
            if (!status.Succeeded || status.Value is not { } s) return;
            if (!s.Settings.LaunchAtStartup || s.Settings.Skipped || !s.Component.Detected) return;
            var result = await PlayniteClient.Current.LaunchAsync(null, PlayniteDisplayId());
            Log.Write("Playnite startup: " + (result.Value?.Outcome ?? result.Error ?? "no answer"));
            if (result.Succeeded && result.Value is { Ok: true } &&
                (result.Value.Outcome == "started" || s.Session.PaRuXLaunchedPlaynite))
                BeginPlayniteReturnWatch(s.Session.PaRuXLaunchedClosedCounter);
        }
        catch (Exception ex) { Log.Write("Playnite startup failed: " + ex.GetType().Name); }
    }

    // The TV-facing game library belongs to Playnite, not to PaRuX's legacy WinUI
    // fullscreen presenter. This flag also prevents the independent startup hook from
    // opening a second Playnite instance or re-selecting the Desktop mode.
    private volatile bool _playniteConsoleHandoffActive;
    private volatile bool _playniteDesktopFallbackAtStartup;

    /// <summary>
    /// Console-first startup: open the already installed Playnite Fullscreen directly.
    /// No native PaRuX Gaming Home is constructed on success; no Playnite theme or
    /// controller files are altered. On failure the caller retains its safe legacy fallback.
    /// </summary>
    internal async Task<ConsolePlayniteStartResult> TryStartPlayniteConsoleAsync(bool explicitRequest = false)
    {
        try
        {
            if (App.IsShuttingDown || _consoleModeActive || _playniteConsoleHandoffActive)
                return ConsolePlayniteStartResult.Unavailable;
            var before = await PlayniteClient.Current.GetStatusAsync();
            if (!before.Succeeded || before.Value is not { } status) return ConsolePlayniteStartResult.Unavailable;
            if (!ConsoleModeReadiness.ShouldStartWithConsole(status.Settings, status.Component, explicitRequest)) return ConsolePlayniteStartResult.Unavailable;

            var result = await PlayniteClient.Current.LaunchAsync(PlayniteModes.Console, PlayniteDisplayId());
            Log.Write("Playnite-first Console handoff: " + (result.Value?.Outcome ?? result.Error ?? "no answer"));
            ConsolePlayniteStartResult outcome = result.Succeeded
                ? ConsoleModeReadiness.ClassifyStart(result.Value)
                : ConsolePlayniteStartResult.Unavailable;
            if (outcome == ConsolePlayniteStartResult.DesktopFallback)
            {
                // The TV can disconnect between the shell probe and Core's final
                // launch check. Playnite already started Desktop: do not start it
                // again or overlay another fullscreen UI on the wrong monitor.
                _playniteDesktopFallbackAtStartup = true;
                return outcome;
            }
            if (outcome != ConsolePlayniteStartResult.Fullscreen)
                return ConsolePlayniteStartResult.Unavailable;

            _playniteConsoleHandoffActive = true;
            if (result.Value!.Outcome == "started" || status.Session.PaRuXLaunchedPlaynite)
                BeginPlayniteReturnWatch(status.Session.PaRuXLaunchedClosedCounter);
            else
                // An existing user-owned Fullscreen cannot increment our owned-close
                // counter. Observe its exit for state cleanup, never to steal focus.
                BeginPlayniteReturnWatch(watchExternalClose: true,
                    externalInitiallyRunning: status.Session.PlayniteRunning || status.Component.Running);
            return ConsolePlayniteStartResult.Fullscreen;
        }
        catch (Exception ex)
        {
            Log.Write("Playnite-first Console handoff failed: " + ex.GetType().Name);
            return ConsolePlayniteStartResult.Unavailable;
        }
    }

    /// <summary>Hides PaRuX behind Playnite Fullscreen. The AI keeps running; Playnite closing (or a pad action) brings PaRuX back.</summary>
    internal void MinimizeForPlayniteConsole()
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Minimize();
    }

    /// <summary>
    /// "Start PaRuX in Console mode" (Settings): no TV needed, any controller. Starts Playnite Fullscreen and minimizes PaRuX; if Playnite
    /// is missing, skipped, refuses or a game is active, PaRuX just opens normally (never a native TV takeover). True when this handled the start.
    /// </summary>
    internal async Task<bool> ApplyAlwaysConsoleStartupAsync()
    {
        if (!SettingsStore.Current.Data.StartInConsoleMode) return false;
        if (GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive()) return true;   // a game is running: never take the screen
        ConsolePlayniteStartResult result = await TryStartPlayniteConsoleAsync(explicitRequest: true);
        switch (ConsoleModeReadiness.FollowUp(result, alwaysConsole: true))
        {
            case ConsoleStartFollowUp.MinimizePaRuX:
                MinimizeForPlayniteConsole();
                Log.Write("Console mode start: Playnite Fullscreen accepted; PaRuX minimized, AI stays available.");
                break;
            default:
                Log.Write("Console mode start: Playnite Fullscreen was not started (" + result + "); PaRuX stays open.");
                break;
        }
        return true;
    }

    /// <summary>
    /// An explicit Console request (--playnite-console, pad action, second launch): start Playnite Fullscreen straight away, like the owner's
    /// Console Mode shortcut. True when Fullscreen is now up (PaRuX minimized); false means the caller should show the launcher so the
    /// reason or the Detect / Locate / Install / Skip setup is visible.
    /// </summary>
    internal async Task<bool> TryDirectPlayniteConsoleAsync()
    {
        ConsolePlayniteStartResult result = await TryStartPlayniteConsoleAsync(explicitRequest: true);
        if (ConsoleModeReadiness.FollowUp(result, alwaysConsole: true) != ConsoleStartFollowUp.MinimizePaRuX)
            return result == ConsolePlayniteStartResult.DesktopFallback;   // Desktop was started: nothing more to show
        MinimizeForPlayniteConsole();
        return true;
    }

    /// <summary>Pad action: Console &lt;-&gt; Desktop for a Playnite PaRuX started. A refusal is shown as a short toast.</summary>
    private async Task PadSwitchPlayniteAsync()
    {
        var before = await PlayniteClient.Current.GetStatusAsync();
        var result = await PlayniteClient.Current.SwitchAsync(PlayniteDisplayId());
        if (result.Error is { } error) { PadToast(error); return; }
        string mode = result.Value?.Mode == PlayniteModes.Console ? "Playnite Console" : "Playnite Desktop";
        PadToast(result.Value?.Ok == true ? mode : (result.Value?.Message ?? PadT("Playnite was not switched.", "Playnite non è stato cambiato.")));
        if (result.Succeeded && result.Value is { Ok: true })
            BeginPlayniteReturnWatch(before.Value?.Session.PaRuXLaunchedClosedCounter);
    }

    /// <summary>Watches (bounded, one loop) until the Playnite that PaRuX started closes, then returns to PaRuX if the owner wants that.</summary>
    internal void BeginPlayniteReturnWatch(long? beforeLaunchCounter = null, bool forceReturn = false,
        bool watchExternalClose = false, bool externalInitiallyRunning = false)
    {
        if (Interlocked.Exchange(ref _playniteWatchRunning, 1) == 1) return;
        bool consoleHandoffWatcher = _playniteConsoleHandoffActive;
        _ = Task.Run(async () =>
        {
            try
            {
                long? baseline = beforeLaunchCounter;
                bool observedExternalRunning = externalInitiallyRunning;
                int idle = 0;
                while (!App.IsShuttingDown && idle < 40)   // at most ~2 minutes without a PaRuX-started Playnite
                {
                    var status = await PlayniteClient.Current.GetStatusAsync();
                    if (status.Value is { } s)
                    {
                        long counter = s.Session.PaRuXLaunchedClosedCounter;
                        baseline ??= counter;
                        if (watchExternalClose)
                        {
                            observedExternalRunning |= s.Session.PlayniteRunning || s.Component.Running;
                            if (PlayniteReturnWatchPolicy.ExternalCloseSettled(s, observedExternalRunning))
                            {
                                // The user started this instance: never bring PaRuX forward.
                                _playniteConsoleHandoffActive = false;
                                return;
                            }
                        }
                        if (!watchExternalClose && PlayniteReturnWatchPolicy.OwnedCloseSettled(baseline, s))
                        {
                            _playniteConsoleHandoffActive = false;
                            if (PlayniteReturnWatchPolicy.ShouldBringForward(baseline, s, forceReturn))
                                _dispatcher.TryEnqueue(PadBringForward);
                            return;
                        }
                        // Playnite can close while a launched game remains running.
                        // Keep the pending close visible, and keep watching the game,
                        // instead of stealing focus or timing out after two minutes.
                        if (!PlayniteReturnWatchPolicy.KeepPendingClose(baseline, s, forceReturn))
                            baseline = counter;
                        // Keep observing user-owned Playnite too. It must not silently
                        // time out after two minutes while the player's library is open.
                        idle = PlayniteReturnWatchPolicy.HasActiveFrontendOrGames(s) ? 0 : idle + 1;
                    }
                    else idle++;
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }
            catch (Exception ex) { Log.Write("Playnite return watch stopped: " + ex.GetType().Name); }
            finally
            {
                // Avoid a permanently stuck Console state after a provider failure,
                // a cancelled watch or the bounded idle timeout; never auto-focus here.
                if (consoleHandoffWatcher) _playniteConsoleHandoffActive = false;
                Interlocked.Exchange(ref _playniteWatchRunning, 0);
            }
        });
    }
}
