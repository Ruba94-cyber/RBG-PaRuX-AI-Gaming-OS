using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX;

/// <summary>
/// Sign-in start orchestrator. When Windows starts PaRuX through the optional sign-in entry (<c>--logon</c>), it applies ONE decision
/// (<see cref="LogonStartupPolicy"/>): Console or Desktop, from the owner's saved choices, the chosen TV(s), an optional graphics card
/// rule and Ctrl/Shift/arguments. Console starts the helpers the owner configured (through the existing Console hooks), Playnite
/// Fullscreen when available, the optional power plan and Game Bar block; the Desktop side follows the owner's "without TV" choice
/// (normal window in front by default, minimised only when chosen).
/// Every phase is timed into StartupTiming.log. The same decision is available as a dry run that starts nothing. It never needs a
/// TV or a monitor: with nothing connected the Desktop side simply starts.
/// </summary>
public sealed partial class MainWindow
{
    private static string LogonTimingPath => PaRuXDataRoot.Combine("logs", "StartupTiming.log");

    private static StartupTimingRecorder NewTimingRecorder()
    {
        long origin = Stopwatch.GetTimestamp();
        return new StartupTimingRecorder(
            () => (long)Stopwatch.GetElapsedTime(origin).TotalMilliseconds, () => DateTime.Now,
            line => StartupTimingFile.Append(LogonTimingPath, line));
    }

    /// <summary>Reads everything the decision needs. Read-only: it starts nothing and changes no setting.</summary>
    internal async Task<(LogonStartupSettings Settings, LogonProbe Probe)> GatherLogonInputsAsync(LogonStartupRequest request, PlayniteLauncherResolution resolution, bool quick)
    {
        var data = SettingsStore.Current.Data;
        int helpers = 0;
        try
        {
            SupportAppStatus st = await SupportAppClient.Current.GetAsync(CancellationToken.None);
            helpers = st.Enabled ? st.Apps.Count(a => a.Entry.Enabled && a.Entry.StartWithConsole) : 0;
        }
        catch (Exception ex) { Log.Write("Sign-in start: support apps not readable (" + ex.GetType().Name + ")."); }
        bool signIn = false;
        try { SignInAppListStatus s = await SignInAppListClient.Current.GetAsync(CancellationToken.None); signIn = s.Enabled && s.Apps.Count > 0; }
        catch (Exception ex) { Log.Write("Sign-in start: app list not readable (" + ex.GetType().Name + ")."); }

        string? gpuRule = LogonGpuRule.Normalize(data.LogonGpuNamePattern);
        var settings = new LogonStartupSettings(
            data.StartInConsoleMode, data.ConsoleStartOnTvWhenPresent, data.StartupWithoutTv, gpuRule, helpers,
            data.ConsolePowerProfileEnabled && !string.IsNullOrWhiteSpace(data.ConsolePowerPlanId),
            data.BlockXboxGameBar && data.GameBarBlockConsentAsked, signIn);

        bool gaming = GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive();
        ConsoleTvSimulation.Mode simulation = ConsoleTvSimulation.FromEnvironment();
        bool tvSelected = ConsoleTvRegistry.HasSelection(data.ConsoleTvs, simulation);
        bool tvPresent = false;
        bool explicitOrForced = resolution.Action != PlayniteLauncherAction.None || request.Force || request.NoStart;
        bool needTv = !gaming && !explicitOrForced && data.ConsoleStartOnTvWhenPresent && !data.StartInConsoleMode && tvSelected;
        if (needTv)
        {
            // Read-only wait for the TV: Core may still be starting and Windows may still be enumerating a TV that just woke.
            ConsoleStartupTvProbe.Result probe = await ConsoleStartupTvProbe.RunAsync(
                GamingCoreTopologyProvider.Provider, () => SettingsStore.Current.Data.ConsoleTvs, simulation,
                (delay, ct) => Task.Delay(delay, ct), () => App.IsShuttingDown, quick ? 2 : ConsoleStartupMaxProbes,
                ConsoleStartupAbsentConfirmations, quick ? TimeSpan.FromSeconds(1) : ConsoleStartupProbeInterval);
            tvPresent = probe.TvPresent;
            if (!quick) RefreshTvSettingsFromSnapshot(probe.Snapshot);
        }
        bool? gpuPresent = null;
        if (gpuRule is not null && !gaming && !explicitOrForced) gpuPresent = LogonGpuRule.Matches(gpuRule, PresentDisplayAdapters.Read());
        return (settings, new LogonProbe(tvSelected, tvPresent, gpuPresent, gaming));
    }

    /// <summary>Dry run: the exact decision and steps a sign-in start would take now, without starting or changing anything. Text in the UI language.</summary>
    internal async Task<string> DescribeLogonDryRunAsync()
    {
        var request = new LogonStartupRequest(true, false, false);
        PlayniteLauncherResolution resolution = PlayniteLauncherModeResolver.Resolve(PlayniteLauncherRequest.Nothing, false, false);
        var (settings, probe) = await GatherLogonInputsAsync(request, resolution, quick: true);
        LogonPlan plan = LogonStartupPolicy.Decide(request, resolution, settings, probe);
        bool it = UiText.IsItalian;
        return plan.Describe(it) + Environment.NewLine + (it ? "Prova a secco: non è stato avviato né modificato nulla." : "Dry run: nothing was started or changed.");
    }

    /// <summary>The real sign-in start. Returns true when it handled the start (the caller then only runs the after-sign-in list).</summary>
    internal async Task<bool> RunLogonStartupAsync(LogonStartupRequest request, PlayniteLauncherResolution resolution)
    {
        StartupTimingRecorder timing = NewTimingRecorder();
        try
        {
            timing.Mark("start sequence begins");
            RecordLogonStart();
            var (settings, probe) = await GatherLogonInputsAsync(request, resolution, quick: false);
            timing.Mark("inputs read (TV " + (probe.TvPresent ? "present" : probe.TvSelected ? "absent" : "not chosen") + ", GPU rule " + (probe.GpuPresent?.ToString() ?? "off") + ")");
            if (App.IsShuttingDown) return true;

            LogonPlan plan = LogonStartupPolicy.Decide(request, resolution, settings, probe);
            Log.Write("Sign-in start decision: " + plan.Mode + " (" + plan.Reason.En + ").");
            timing.Mark("decision: " + plan.Mode);

            switch (plan.Mode)
            {
                case LogonMode.Settings: ShowPlayniteLauncher(PlayniteLauncherAction.Settings, App.StartupLauncherRequest.Gamepad, fromRequest: true); break;
                case LogonMode.Streaming: ShowPlayniteLauncher(PlayniteLauncherAction.Streaming, App.StartupLauncherRequest.Gamepad, fromRequest: true); break;
                case LogonMode.Launcher: ShowPlayniteLauncher(PlayniteLauncherAction.Open, App.StartupLauncherRequest.Gamepad, fromRequest: true); break;
                case LogonMode.Console: await StartLogonConsoleAsync(settings.StartInConsoleMode || request.Force || resolution.Action == PlayniteLauncherAction.Console); break;
                default:
                    // The owner's "without TV" choice: minimised only when chosen; otherwise the window opens normally and comes to the front.
                    if (plan.Window == LogonWindow.Minimized)
                    {
                        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlapped) overlapped.Minimize();
                    }
                    else
                    {
                        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } shown)
                            shown.Restore();
                        App.ShowMainWindow();
                    }
                    break;
            }
            timing.Mark("start action requested: " + plan.Mode);
            await RunSignInAppListAfterStartupAsync();
            timing.Mark("after-sign-in list handled");
        }
        catch (Exception ex)
        {
            // A failed sign-in start never leaves the app unusable: it stays in its normal window.
            Log.Write("Sign-in start failed: " + ex.GetType().Name + ": " + ex.Message);
            timing.Mark("failed: " + ex.GetType().Name);
            _consoleRoutingInProgress = false;
        }
        return true;
    }

    /// <summary>Records this sign-in start (process start time) for the Settings check "did PaRuX start at the last sign-in?".</summary>
    private static void RecordLogonStart()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try { using Process me = Process.GetCurrentProcess(); started = new DateTimeOffset(me.StartTime.ToUniversalTime(), TimeSpan.Zero); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        string via = "CommandLine";
        try
        {
            string? exe = StartupService.LaunchExecutable;
            if (exe != null)
            {
                LogonMechanism mechanism = RegistryRunKeyStore.CreateRegistration().Describe(exe).Mechanism;
                if (mechanism != LogonMechanism.None) via = mechanism.ToString();
            }
        }
        catch { }
        LogonStartMarker.Write(RegistryRunKeyStore.LogonStartMarkerPath, started, via);
    }

    private async Task StartLogonConsoleAsync(bool playniteOnly)
    {
        if (_consoleRoutingInProgress || GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive()) return;
        _consoleRoutingInProgress = true;
        try
        {
            ConsolePlayniteStartResult handoff = await TryStartPlayniteConsoleAsync(explicitRequest: playniteOnly);
            ConsoleStartFollowUp follow = ConsoleModeReadiness.FollowUp(handoff, alwaysConsole: playniteOnly);
            if (follow == ConsoleStartFollowUp.MinimizePaRuX) { MinimizeForPlayniteConsole(); return; }
            if (follow == ConsoleStartFollowUp.StayOpen) return;
            // Playnite was not started: PaRuX simply stays in its normal window (the native Console/TV shell was removed).
            Log.Write("Sign-in Console start: Playnite Fullscreen was not started (" + handoff + "); PaRuX stays in its normal window.");
        }
        finally { _consoleRoutingInProgress = false; }
    }

    /// <summary>Starts the owner's support apps (off unless the list is on). Called with the existing Console power/Game Bar hooks; never blocks the UI.</summary>
    internal async Task StartConsoleSupportAppsAsync()
    {
        try
        {
            if (App.IsShuttingDown) return;
            SupportAppStatus status = await SupportAppClient.Current.GetAsync(CancellationToken.None);
            if (!status.Enabled || status.Apps.Count == 0) return;
            StartupTimingRecorder timing = NewTimingRecorder();
            timing.Mark("support apps: start requested");
            SupportAppStatus after = await SupportAppClient.Current.StartAsync(CancellationToken.None);
            timing.Mark("support apps: " + string.Join(", ", after.LastRun.Select(r => r.Id + "=" + r.Outcome + "/" + r.ElapsedMs + "ms")));
        }
        catch (Exception ex) { Log.Write("Support apps start failed: " + ex.GetType().Name); }
    }

    internal async Task StopConsoleSupportAppsAsync()
    {
        try { await SupportAppClient.Current.StopAsync(CancellationToken.None); }
        catch (Exception ex) { Log.Write("Support apps stop failed: " + ex.GetType().Name); }
    }
}
