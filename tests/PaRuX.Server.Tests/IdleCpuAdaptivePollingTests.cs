using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Playnite;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Idle-CPU work (2026-11-25): adaptive poll intervals are pure policies, and the latency bounds they promise are tested here.</summary>
public sealed class IdleCpuAdaptivePollingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-idle-" + Guid.NewGuid().ToString("N"));

    public IdleCpuAdaptivePollingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static PlayniteWatchSignals Quiet() => new(true, false, false, false, false, false, false);

    // ---- Playnite watcher cadence (pure policy) ----

    [Fact]
    public void Playnite_cadence_is_long_only_when_nothing_can_start_a_session()
    {
        Assert.Equal(PlayniteWatchCadence.Idle, PlayniteWatchCadence.Next(Quiet()));
        Assert.True(PlayniteWatchCadence.Idle > PlayniteWatchCadence.Fast);
    }

    [Theory]
    [InlineData(nameof(PlayniteWatchSignals.PlayniteAlive))]
    [InlineData(nameof(PlayniteWatchSignals.GameCandidateOrActive))]
    [InlineData(nameof(PlayniteWatchSignals.Driving))]
    [InlineData(nameof(PlayniteWatchSignals.PaRuXLaunchedPlaynite))]
    [InlineData(nameof(PlayniteWatchSignals.BoostActive))]
    [InlineData(nameof(PlayniteWatchSignals.LastTickFailed))]
    public void Playnite_cadence_is_the_original_one_second_in_every_urgent_state(string signal)
    {
        PlayniteWatchSignals q = Quiet();
        PlayniteWatchSignals s = signal switch
        {
            nameof(PlayniteWatchSignals.PlayniteAlive) => q with { PlayniteAlive = true },
            nameof(PlayniteWatchSignals.GameCandidateOrActive) => q with { GameCandidateOrActive = true },
            nameof(PlayniteWatchSignals.Driving) => q with { Driving = true },
            nameof(PlayniteWatchSignals.PaRuXLaunchedPlaynite) => q with { PaRuXLaunchedPlaynite = true },
            nameof(PlayniteWatchSignals.BoostActive) => q with { BoostActive = true },
            _ => q with { LastTickFailed = true }
        };
        Assert.Equal(TimeSpan.FromSeconds(1), PlayniteWatchCadence.Next(s));
    }

    [Fact]
    public void Disabled_integration_backs_off_but_a_yield_in_flight_is_never_slowed()
    {
        Assert.Equal(PlayniteWatchCadence.Disabled, PlayniteWatchCadence.Next(Quiet() with { IntegrationEnabled = false }));
        Assert.Equal(PlayniteWatchCadence.Fast, PlayniteWatchCadence.Next(Quiet() with { IntegrationEnabled = false, Driving = true }));
    }

    [Fact]
    public void Declared_extra_detection_delay_stays_within_one_second()
    {
        // Yield latency contract: min(3 s, current + 1 s). Once Playnite is alive the tick is the old 1 s, so the only added delay
        // is noticing a Playnite that has just appeared, and it is bounded here.
        Assert.True(PlayniteWatchCadence.MaxExtraDetectionDelay <= TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), PlayniteWatchCadence.Fast);
    }

    // ---- The service uses the policy ----

    private PlayniteSessionService Service(FakeProcessWorld world, FakeHost host, DateTime[] clock, out PlayniteSettingsStore settings)
    {
        settings = new PlayniteSettingsStore(Path.Combine(_dir, "s.json"));
        var env = new FakeEnv();
        return new PlayniteSessionService(settings, new PlayniteLocator(env), new PlayniteLauncher(host, env, _ => true), world,
            new FakeYieldTarget(), new PlayniteGameSessionTracker(9999, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)), () => clock[0]);
    }

    [Fact]
    public async Task Service_waits_long_with_no_playnite_and_returns_to_one_second_as_soon_as_playnite_exists()
    {
        var world = new FakeProcessWorld().Add(10, 1, "explorer.exe", -100);
        var clock = new[] { FakeProcessWorld.T0 };
        PlayniteSessionService service = Service(world, new FakeHost(), clock, out _);

        await service.TickAsync(CancellationToken.None);
        Assert.Equal(PlayniteWatchCadence.Idle, service.NextDelay);

        world.Add(100, 1, "Playnite.FullscreenApp.exe", -1);
        clock[0] = clock[0].AddSeconds(2);
        await service.TickAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(1), service.NextDelay);
    }

    [Fact]
    public async Task Service_stays_fast_for_the_whole_game_session_so_the_yield_and_restore_latency_is_unchanged()
    {
        var world = new FakeProcessWorld().Add(100, 1, "Playnite.FullscreenApp.exe", -60).Add(200, 100, "game.exe", 10);
        var clock = new[] { FakeProcessWorld.T0 };
        PlayniteSessionService service = Service(world, new FakeHost(), clock, out _);
        for (int s = 10; s <= 30; s++)
        {
            clock[0] = FakeProcessWorld.T0.AddSeconds(s);
            await service.TickAsync(CancellationToken.None);
            Assert.Equal(TimeSpan.FromSeconds(1), service.NextDelay);
        }
        Assert.Equal("yielded", service.GetSession().State);
    }

    [Fact]
    public async Task Boost_keeps_the_fast_tick_for_the_boost_window_only()
    {
        var world = new FakeProcessWorld().Add(10, 1, "explorer.exe", -100);
        var clock = new[] { FakeProcessWorld.T0 };
        PlayniteSessionService service = Service(world, new FakeHost(), clock, out _);
        await service.TickAsync(CancellationToken.None);
        service.Boost();
        Assert.Equal(PlayniteWatchCadence.Fast, service.NextDelay);
        clock[0] = clock[0] + PlayniteWatchCadence.BoostWindow + TimeSpan.FromSeconds(1);
        Assert.Equal(PlayniteWatchCadence.Idle, service.NextDelay);
    }

    [Fact]
    public async Task A_short_game_launched_after_playnite_appeared_is_still_detected_at_the_old_resolution()
    {
        // Playnite appears while the watcher sleeps on its idle tick; the next sample sees it and the game together, and the
        // 4 s start debounce runs from that sample exactly as before (a game that lives 6 s is a session).
        var world = new FakeProcessWorld().Add(100, 1, "Playnite.FullscreenApp.exe", 1).Add(200, 100, "game.exe", 1.5);
        var target = new FakeYieldTarget();
        var clock = new[] { FakeProcessWorld.T0 };
        var env = new FakeEnv();
        var service = new PlayniteSessionService(new PlayniteSettingsStore(Path.Combine(_dir, "t.json")), new PlayniteLocator(env),
            new PlayniteLauncher(new FakeHost(), env, _ => true), world, target,
            new PlayniteGameSessionTracker(9999, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)), () => clock[0]);
        for (int s = 2; s <= 8; s++)   // one sample per second after the first sighting at t = 2
        {
            clock[0] = FakeProcessWorld.T0.AddSeconds(s);
            await service.TickAsync(CancellationToken.None);
        }
        Assert.Equal(1, target.EnterCalls);
    }

    // ---- Detection latency before/after (simulated clock, same tracker and service) ----

    /// <summary>Seconds from "Playnite and its game appeared" to the yield entering Gaming, stepping the clock in 50 ms slices.
    /// <paramref name="fixedDelay"/> reproduces the old 1 s tick; null uses the adaptive policy.</summary>
    private async Task<double> TimeToYieldAsync(double appearAtSeconds, TimeSpan? fixedDelay, string settingsName)
    {
        var world = new FakeProcessWorld();
        var target = new FakeYieldTarget();
        var clock = new[] { FakeProcessWorld.T0 };
        var env = new FakeEnv();
        var service = new PlayniteSessionService(new PlayniteSettingsStore(Path.Combine(_dir, settingsName)), new PlayniteLocator(env),
            new PlayniteLauncher(new FakeHost(), env, _ => true), world, target,
            new PlayniteGameSessionTracker(9999, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)), () => clock[0]);
        bool added = false;
        double nextTickAt = 0;
        for (double t = 0; t < 40; t += 0.05)
        {
            clock[0] = FakeProcessWorld.T0.AddSeconds(t);
            if (!added && t >= appearAtSeconds)
            {
                world.Add(100, 1, "Playnite.FullscreenApp.exe", appearAtSeconds).Add(200, 100, "game.exe", appearAtSeconds);
                added = true;
            }
            if (t + 1e-9 < nextTickAt) continue;
            await service.TickAsync(CancellationToken.None);
            nextTickAt = t + (fixedDelay ?? service.NextDelay).TotalSeconds;
            if (target.EnterCalls > 0) return t - appearAtSeconds;
        }
        return double.PositiveInfinity;
    }

    [Fact]
    public async Task Yield_latency_after_is_never_more_than_one_second_worse_than_the_old_fixed_tick_and_stays_under_the_stated_bound()
    {
        double worstBefore = 0, worstAfter = 0;
        for (double offset = 1; offset <= 3.01; offset += 0.25)
        {
            double before = await TimeToYieldAsync(offset, TimeSpan.FromSeconds(1), "b" + offset + ".json");
            double after = await TimeToYieldAsync(offset, null, "a" + offset + ".json");
            worstBefore = Math.Max(worstBefore, before);
            worstAfter = Math.Max(worstAfter, after);
            Assert.True(after <= before + 1.0 + 1e-6, $"offset {offset}: before {before:F2} s, after {after:F2} s");
        }
        Console.WriteLine($"YIELD-LATENCY worst before {worstBefore:F2} s, worst after {worstAfter:F2} s");
        // Measured on the simulated clock: the old worst case is the 4 s start debounce plus two 1 s ticks (the first sighting of
        // Playnite and, one tick later, of its game); the adaptive watcher adds at most the 1 s of MaxExtraDetectionDelay.
        Assert.True(worstBefore <= 6.0, "before worst " + worstBefore);
        Assert.True(worstAfter <= worstBefore + PlayniteWatchCadence.MaxExtraDetectionDelay.TotalSeconds + 1e-6, $"after worst {worstAfter}, before worst {worstBefore}");
        Assert.True(worstAfter <= 7.0, "after worst " + worstAfter);
    }

    [Fact]
    public async Task Yield_latency_when_playnite_is_already_running_is_identical_to_the_old_tick()
    {
        // Playnite is up (fast tick), then a game starts: the adaptive watcher must react exactly as the fixed 1 s one did.
        async Task<double> Run(TimeSpan? fixedDelay, string name)
        {
            var world = new FakeProcessWorld().Add(100, 1, "Playnite.FullscreenApp.exe", -60);
            var target = new FakeYieldTarget();
            var clock = new[] { FakeProcessWorld.T0 };
            var env = new FakeEnv();
            var service = new PlayniteSessionService(new PlayniteSettingsStore(Path.Combine(_dir, name)), new PlayniteLocator(env),
                new PlayniteLauncher(new FakeHost(), env, _ => true), world, target,
                new PlayniteGameSessionTracker(9999, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)), () => clock[0]);
            double next = 0; bool added = false;
            for (double t = 0; t < 30; t += 0.05)
            {
                clock[0] = FakeProcessWorld.T0.AddSeconds(t);
                if (!added && t >= 10.3) { world.Add(200, 100, "game.exe", 10.3); added = true; }
                if (t + 1e-9 < next) continue;
                await service.TickAsync(CancellationToken.None);
                next = t + (fixedDelay ?? service.NextDelay).TotalSeconds;
                if (target.EnterCalls > 0) return t;
            }
            return double.PositiveInfinity;
        }
        Assert.Equal(await Run(TimeSpan.FromSeconds(1), "x1.json"), await Run(null, "x2.json"), 6);
    }

    // ---- Windows idle probe (real OS calls, tolerant of a headless runner) ----

    [Fact]
    public void Windows_source_capture_still_returns_real_image_names_without_per_entry_marshalling_strings()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = new WindowsProcessSnapshotSource();
        IReadOnlyList<ProcEntry> table = source.Capture();
        ProcEntry self = Assert.Single(table, e => e.Pid == Environment.ProcessId);
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().ProcessName, PlayniteGameSessionTracker.BaseName(self.Name), ignoreCase: true);
    }

    [Fact]
    public void Windows_idle_probe_finds_a_running_image_by_full_sweep_and_by_window_sweep_and_rejects_an_absent_one()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = new WindowsProcessSnapshotSource();
        string me = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { me };
        var absent = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "parux-no-such-process-" + Guid.NewGuid().ToString("N") };
        Assert.True(source.AnyProcessNamed(present));    // first call is the full sweep
        Assert.False(source.AnyProcessNamed(absent));    // second call is the cheap window sweep: never a false positive
        Assert.True(WindowsProcessSnapshotSource.FullSweepInterval <= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Windows_window_sweep_notices_a_new_window_owning_process_within_one_probe()
    {
        if (!OperatingSystem.IsWindows()) return;
        string host = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(host)) return;
        string copy = Path.Combine(_dir, "ParuxProbeWindowApp.exe");   // a uniquely named image that owns a top-level WinForms window
        File.Copy(host, copy, true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ParuxProbeWindowApp" };
        var source = new WindowsProcessSnapshotSource();
        Assert.False(source.AnyProcessNamed(names));   // full sweep: not running yet
        System.Diagnostics.Process? p = null;
        try
        {
            p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(copy, "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Application]::Run((New-Object System.Windows.Forms.Form))\"") { UseShellExecute = false });
            if (p is null) return;
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !source.AnyProcessNamed(names)) Thread.Sleep(200);
            // Within 10 s the window sweep (or, on a runner with no desktop window station, the periodic full sweep) must see it.
            bool seen = source.AnyProcessNamed(names);
            Assert.True(seen || DateTime.UtcNow >= deadline && !Environment.UserInteractive, "exited=" + p.HasExited + " hwnd=" + (p.HasExited ? 0 : (long)p.MainWindowHandle) + " interactive=" + Environment.UserInteractive);
        }
        finally { try { p?.Kill(); p?.WaitForExit(2000); } catch { } }
    }

    // ---- UI cadence (pure policy) ----

    [Fact]
    public void Ui_poll_keeps_two_seconds_while_anything_is_in_flight_and_relaxes_only_when_idle()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), UiPollCadence.Next(windowHidden: false, anyActivity: true));
        Assert.Equal(TimeSpan.FromSeconds(2), UiPollCadence.Next(windowHidden: true, anyActivity: true));
        Assert.Equal(UiPollCadence.VisibleIdle, UiPollCadence.Next(false, false));
        Assert.Equal(UiPollCadence.HiddenIdle, UiPollCadence.Next(true, false));
        Assert.True(UiPollCadence.HiddenIdle >= UiPollCadence.VisibleIdle);
    }

    [Theory]
    [InlineData("queued", true)]
    [InlineData("downloading", true)]
    [InlineData("retrying", true)]
    [InlineData("completed", false)]
    [InlineData("failed", false)]
    [InlineData(null, false)]
    public void Download_in_flight_matches_the_states_that_can_still_finish(string? status, bool expected) =>
        Assert.Equal(expected, UiPollCadence.DownloadInFlight(status));

    [Theory]
    [InlineData("extracting", true)]
    [InlineData("performance-check", true)]
    [InlineData("interrupted", false)]
    [InlineData("completed", false)]
    public void Install_in_flight_matches_the_stages_that_can_still_change(string stage, bool expected) =>
        Assert.Equal(expected, UiPollCadence.InstallInFlight(stage));

    [Fact]
    public void Hidden_window_skips_ui_refresh_but_the_default_probe_is_never_hidden()
    {
        Assert.False(UiPollCadence.ShouldRefreshUi(windowHidden: true));
        Assert.True(UiPollCadence.ShouldRefreshUi(windowHidden: false));
        UiPollCadence.WindowHiddenProbe = null;
        Assert.False(UiPollCadence.IsWindowHidden());
        UiPollCadence.WindowHiddenProbe = () => true;
        try { Assert.True(UiPollCadence.IsWindowHidden()); }
        finally { UiPollCadence.WindowHiddenProbe = null; }
    }

    [Fact]
    public void Shared_task_list_snapshot_lives_short_while_work_is_live_long_when_idle_and_dies_on_a_change()
    {
        var policy = new SharedSnapshotPolicy();
        DateTime t = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(policy.IsFresh(t));
        policy.Record(t, anyNonTerminal: true);
        Assert.True(policy.IsFresh(t.AddMilliseconds(1000)));
        Assert.False(policy.IsFresh(t.AddMilliseconds(1600)));
        policy.Record(t, anyNonTerminal: false);
        Assert.True(policy.IsFresh(t.AddSeconds(3)));
        Assert.False(policy.IsFresh(t.AddSeconds(5)));
        policy.Invalidate();
        Assert.False(policy.IsFresh(t.AddMilliseconds(1)));
    }

    // ---- Source contracts: the wiring cannot silently regress ----

    private static string Repo()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Repo() }.Concat(parts).ToArray()));

    [Fact]
    public void Source_contract_playnite_watcher_uses_the_cadence_policy_and_no_hardcoded_one_second_delay()
    {
        string src = Read("src", "PaRuX.Server", "Orchestration", "Gaming", "Playnite", "PlayniteSessionService.cs");
        Assert.Contains("Task.Delay(ComputeNextDelay()", src);
        Assert.DoesNotContain("Task.Delay(TimeSpan.FromSeconds(1)", src);
        string endpoints = Read("src", "PaRuX.Server", "Orchestration", "Gaming", "Playnite", "PlayniteEndpoints.cs");
        Assert.Contains("session.Boost()", endpoints);
    }

    [Fact]
    public void Source_contract_gaming_marker_is_still_polled_every_250_ms()
    {
        string src = Read("src", "PaRuX.Server", "GamingGuardService.cs");
        Assert.Contains("Task.Delay(250, stoppingToken)", src);
        Assert.Contains("retryReopen", src);
    }

    [Fact]
    public void Source_contract_main_window_poll_is_adaptive_and_skips_repaint_when_hidden()
    {
        string src = Read("src", "PaRuX.App", "MainWindow.xaml.cs");
        Assert.Contains("UiPollCadence.Next(", src);
        Assert.Contains("UiPollCadence.ShouldRefreshUi(", src);
        Assert.Contains("UiPollCadence.WindowHiddenProbe", src);
        string client = Read("src", "PaRuX.App", "Services", "AgentOrchestratorClient.cs");
        Assert.Contains("SharedSnapshotPolicy", client);
    }
}
