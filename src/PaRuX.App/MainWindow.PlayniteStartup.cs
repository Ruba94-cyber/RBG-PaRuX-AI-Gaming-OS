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
            if (hasPlayRequest || App.IsShuttingDown || _consoleModeActive || _playniteConsoleHandoffActive) return;
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

    /// <summary>
    /// Console-first startup: open the already installed Playnite Fullscreen directly.
    /// No native PaRuX Gaming Home is constructed on success; no Playnite theme or
    /// controller files are altered. On failure the caller retains its safe legacy fallback.
    /// </summary>
    internal async Task<bool> TryStartPlayniteConsoleAsync()
    {
        try
        {
            if (App.IsShuttingDown || _consoleModeActive || _playniteConsoleHandoffActive)
                return false;
            var before = await PlayniteClient.Current.GetStatusAsync();
            if (!before.Succeeded || before.Value is not { } status) return false;
            if (!ConsoleModeReadiness.ShouldStartWithConsole(status.Settings, status.Component)) return false;

            var result = await PlayniteClient.Current.LaunchAsync(PlayniteModes.Console, PlayniteDisplayId());
            Log.Write("Playnite-first Console handoff: " + (result.Value?.Outcome ?? result.Error ?? "no answer"));
            if (!result.Succeeded || !ConsoleModeReadiness.AcceptedConsoleHandoff(result.Value))
                return false;

            _playniteConsoleHandoffActive = true;
            if (result.Value!.Outcome == "started" || status.Session.PaRuXLaunchedPlaynite)
                BeginPlayniteReturnWatch(status.Session.PaRuXLaunchedClosedCounter);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Playnite-first Console handoff failed: " + ex.GetType().Name);
            return false;
        }
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
    internal void BeginPlayniteReturnWatch(long? beforeLaunchCounter = null, bool forceReturn = false)
    {
        if (Interlocked.Exchange(ref _playniteWatchRunning, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                long? baseline = beforeLaunchCounter;
                int idle = 0;
                while (!App.IsShuttingDown && idle < 40)   // at most ~2 minutes without a PaRuX-started Playnite
                {
                    var status = await PlayniteClient.Current.GetStatusAsync();
                    if (status.Value is { } s)
                    {
                        long counter = s.Session.PaRuXLaunchedClosedCounter;
                        baseline ??= counter;
                        if (PlayniteReturnWatchPolicy.ShouldBringForward(baseline, s, forceReturn))
                        {
                            _playniteConsoleHandoffActive = false;
                            _dispatcher.TryEnqueue(PadBringForward);
                            return;
                        }
                        // Playnite can close while a launched game remains running.
                        // Keep the pending close visible, and keep watching the game,
                        // instead of stealing focus or timing out after two minutes.
                        if (!PlayniteReturnWatchPolicy.KeepPendingClose(baseline, s, forceReturn))
                            baseline = counter;
                        idle = s.Session.PaRuXLaunchedPlaynite || s.Session.TrackedProcessCount > 0 ? 0 : idle + 1;
                    }
                    else idle++;
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }
            catch (Exception ex) { Log.Write("Playnite return watch stopped: " + ex.GetType().Name); }
            finally { Interlocked.Exchange(ref _playniteWatchRunning, 0); }
        });
    }
}
