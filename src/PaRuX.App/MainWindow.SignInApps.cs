using System;
using System.Diagnostics;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX;

/// <summary>
/// After-sign-in app list trigger. Once, after the shell finished choosing Desktop vs Console/TV at start, the shell tells Core
/// (a) which Windows sign-in session this is, (b) when this process started and (c) the real Console/TV state. Core decides:
/// the list must be enabled by the owner, Gaming must be off, the shell must have started right after the sign-in, and this
/// sign-in must not have run the list before. While a run is in progress the shell keeps Core informed about Console/TV changes.
/// Nothing here touches Windows startup entries.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan SignInStatePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SignInStatePollMax = TimeSpan.FromMinutes(15);

    /// <summary>Start sequence: the Console/TV start preference first (unless a game shortcut started us), then the sign-in list.</summary>
    internal async Task RunStartupSequenceAsync(bool hasPlayRequest)
    {
        if (!hasPlayRequest) await ApplyConsoleStartupPreferenceAsync();
        await RunSignInAppListAfterStartupAsync();
    }

    internal async Task RunSignInAppListAfterStartupAsync()
    {
        try
        {
            if (App.IsShuttingDown) return;
            SignInAppListStatus status = await SignInAppListClient.Current.GetAsync(System.Threading.CancellationToken.None);
            if (!status.Enabled || status.Apps.Count == 0) return;   // off by default: nothing else happens

            // Let an in-flight Console/TV decision settle (bounded by the owner's ModeWaitSeconds), so SkipInConsole sees the real mode.
            DateTime until = DateTime.UtcNow.AddSeconds(Math.Max(0, status.Settings.ModeWaitSeconds));
            while (_consoleRoutingInProgress && DateTime.UtcNow < until && !App.IsShuttingDown)
                await Task.Delay(250);
            if (App.IsShuttingDown) return;

            bool console = _consoleModeActive;
            SignInSessionInfo session = SignInSessionProbe.Probe();
            DateTimeOffset? shellStart = null;
            try { using Process me = Process.GetCurrentProcess(); shellStart = new DateTimeOffset(me.StartTime.ToUniversalTime(), TimeSpan.Zero); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }

            SignInTriggerResponse response = await SignInAppListClient.Current.TriggerAsync(
                new SignInTriggerRequest(session.Key, session.StartUtc, shellStart, console), System.Threading.CancellationToken.None);
            Log.Write("After-sign-in app list: " + response.Decision + ".");
            if (response.Decision != SignInTriggerDecision.Started) return;

            // Keep Core's Console/TV signal live while the run is in progress.
            bool last = console;
            DateTime stop = DateTime.UtcNow + SignInStatePollMax;
            while (!App.IsShuttingDown && DateTime.UtcNow < stop)
            {
                await Task.Delay(SignInStatePollInterval);
                bool now = _consoleModeActive;
                if (now != last)
                {
                    await SignInAppListClient.Current.ReportShellStateAsync(now, System.Threading.CancellationToken.None);
                    last = now;
                }
                SignInAppListStatus s = await SignInAppListClient.Current.GetAsync(System.Threading.CancellationToken.None);
                if (s.Run.State is not (SignInRunState.Waiting or SignInRunState.Running)) break;
            }
        }
        catch (Exception ex)
        {
            // A failed optional list never stops the app: it is only logged (no paths, no arguments).
            Log.Write("After-sign-in app list failed: " + ex.GetType().Name);
        }
    }
}

internal readonly record struct SignInSessionInfo(string? Key, DateTimeOffset? StartUtc);

/// <summary>Identifies the current Windows sign-in session by the start time of its explorer.exe (read-only process query).</summary>
internal static class SignInSessionProbe
{
    public static SignInSessionInfo Probe()
    {
        try
        {
            int sessionId;
            using (Process me = Process.GetCurrentProcess()) sessionId = me.SessionId;
            DateTime? earliest = null;
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if (p.SessionId != sessionId) continue;
                    DateTime start = p.StartTime.ToUniversalTime();
                    if (earliest is null || start < earliest) earliest = start;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                finally { p.Dispose(); }
            }
            if (earliest is null) return new(null, null);
            var utc = new DateTimeOffset(earliest.Value, TimeSpan.Zero);
            return new("explorer-" + utc.ToUnixTimeSeconds().ToString("x"), utc);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return new(null, null);
        }
    }
}
