using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PlayniteConsoleReturnTests
{
    private const string Line = "Playnite-first Console handoff: Playnite closed, returning to PaRuX";

    private static PlayniteStatusDto Status(long counter, bool ownedAlive = false, bool running = false,
        bool componentRunning = false, int games = 0, bool returnPref = true) => new()
    {
        Settings = new PlayniteSettingsDto { ReturnToPaRuXOnClose = returnPref },
        Component = new PlayniteComponentDto { Running = componentRunning },
        Session = new PlayniteSessionDto
        {
            PaRuXLaunchedPlaynite = ownedAlive, PlayniteRunning = running,
            TrackedProcessCount = games, PaRuXLaunchedClosedCounter = counter
        }
    };

    // ---- pure policy -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Owned_playnite_closed_and_nothing_else_running_returns()
    {
        Assert.Equal(PlayniteReturnDecision.ReturnToPaRuX, PlayniteConsoleReturnPolicy.Decide(5, Status(6), forceReturn: false));
    }

    [Fact]
    public void Explicit_console_handoff_returns_even_when_the_optional_preference_is_off()
    {
        var s = Status(6, returnPref: false);
        Assert.Equal(PlayniteReturnDecision.SettledNoReturn, PlayniteConsoleReturnPolicy.Decide(5, s, forceReturn: false));
        Assert.Equal(PlayniteReturnDecision.ReturnToPaRuX, PlayniteConsoleReturnPolicy.Decide(5, s, forceReturn: true));
    }

    [Fact]
    public void A_running_game_started_through_playnite_blocks_the_return()
    {
        Assert.Equal(PlayniteReturnDecision.WaitForGame, PlayniteConsoleReturnPolicy.Decide(5, Status(6, games: 1), forceReturn: true));
        Assert.Equal(PlayniteReturnDecision.ReturnToPaRuX, PlayniteConsoleReturnPolicy.Decide(5, Status(6, games: 0), forceReturn: true));
    }

    [Fact]
    public void A_playnite_started_by_the_user_never_returns()
    {
        var closed = Status(0);
        Assert.Equal(PlayniteReturnDecision.KeepWatching,
            PlayniteConsoleReturnPolicy.Decide(0, Status(0, running: true), true, externalOwned: true, observedExternalRunning: true));
        Assert.Equal(PlayniteReturnDecision.SettledNoReturn,
            PlayniteConsoleReturnPolicy.Decide(0, closed, true, externalOwned: true, observedExternalRunning: true));
        // Never observed running: nothing settles, nothing returns.
        Assert.Equal(PlayniteReturnDecision.KeepWatching,
            PlayniteConsoleReturnPolicy.Decide(0, closed, true, externalOwned: true, observedExternalRunning: false));
    }

    [Fact]
    public void A_still_alive_playnite_or_unknown_status_keeps_watching()
    {
        Assert.Equal(PlayniteReturnDecision.KeepWatching, PlayniteConsoleReturnPolicy.Decide(5, null, true));
        Assert.Equal(PlayniteReturnDecision.KeepWatching, PlayniteConsoleReturnPolicy.Decide(5, Status(5, ownedAlive: true), true));
        Assert.Equal(PlayniteReturnDecision.KeepWatching, PlayniteConsoleReturnPolicy.Decide(5, Status(6, componentRunning: true), true));
    }

    // ---- watcher with a fake status source ---------------------------------------------------------------------------------

    private sealed class Script
    {
        private readonly Queue<PlayniteStatusDto?> _steps;
        public int Polls; public int Delays; public readonly List<string> Log = new();
        public Script(params PlayniteStatusDto?[] steps) => _steps = new Queue<PlayniteStatusDto?>(steps);
        public Task<PlayniteStatusDto?> Poll(CancellationToken _)
        {
            Polls++;
            // The last sample repeats forever: a stable end state, not a crash.
            return Task.FromResult(_steps.Count > 1 ? _steps.Dequeue() : _steps.Peek());
        }
        public Task Delay(TimeSpan _, CancellationToken __) { Delays++; return Task.CompletedTask; }
    }

    private static Task<PlayniteReturnWatchOutcome> Run(Script s, long? baseline, bool force = true, bool external = false,
        bool externalRunning = false, int maxIdle = 40, Func<bool>? shuttingDown = null) =>
        PlayniteReturnWatcher.RunAsync(s.Poll, s.Delay, shuttingDown ?? (() => false), s.Log.Add, baseline, force, external,
            externalRunning, Line, TimeSpan.FromSeconds(3), maxIdle);

    [Fact]
    public async Task Closing_fullscreen_returns_once_and_writes_the_return_line()
    {
        var s = new Script(Status(5, ownedAlive: true), Status(5, ownedAlive: true), Status(6));
        Assert.Equal(PlayniteReturnWatchOutcome.Returned, await Run(s, 5));
        Assert.Equal(new[] { Line }, s.Log);
        Assert.Equal(3, s.Polls);   // returned on the first settled sample, no loop afterwards
    }

    [Fact]
    public async Task A_stale_process_snapshot_right_after_the_close_does_not_swallow_the_return()
    {
        // Counter already bumped while the snapshot still shows Playnite: the old watch advanced its baseline here and never returned.
        var s = new Script(Status(5, ownedAlive: true), Status(6, running: true), Status(6, componentRunning: true), Status(6));
        Assert.Equal(PlayniteReturnWatchOutcome.Returned, await Run(s, 5));
        Assert.Contains(Line, s.Log);
    }

    [Fact]
    public async Task A_mode_switch_follows_the_replacement_and_returns_only_when_that_one_closes()
    {
        var s = new Script(Status(5, ownedAlive: true), Status(6, ownedAlive: true), Status(6, ownedAlive: true), Status(7));
        Assert.Equal(PlayniteReturnWatchOutcome.Returned, await Run(s, 5));
        Assert.Equal(4, s.Polls);
    }

    [Fact]
    public async Task The_return_waits_for_a_game_that_outlives_playnite()
    {
        var s = new Script(Status(5, ownedAlive: true), Status(6, games: 1), Status(6, games: 1), Status(6));
        Assert.Equal(PlayniteReturnWatchOutcome.Returned, await Run(s, 5));
        Assert.Equal(4, s.Polls);
        Assert.Single(s.Log, l => l.Contains("waiting for the game"));
        Assert.Equal(Line, s.Log[^1]);
    }

    [Fact]
    public async Task A_user_started_playnite_closing_never_returns_and_logs_no_return_line()
    {
        var s = new Script(Status(0, running: true), Status(0));
        Assert.Equal(PlayniteReturnWatchOutcome.SettledNoReturn, await Run(s, 0, external: true, externalRunning: true));
        Assert.DoesNotContain(Line, s.Log);
    }

    [Fact]
    public async Task The_watch_ends_by_itself_and_never_loops_forever()
    {
        var s = new Script(Status(5));   // never started anything alive and nothing closed after the baseline
        Assert.Equal(PlayniteReturnWatchOutcome.TimedOut, await Run(s, 5, maxIdle: 4));
        Assert.Equal(4, s.Polls);

        var shut = new Script(Status(5, ownedAlive: true));
        int calls = 0;
        Assert.Equal(PlayniteReturnWatchOutcome.TimedOut, await Run(shut, 5, shuttingDown: () => calls++ >= 2));
        Assert.Equal(2, shut.Polls);
    }

    [Fact]
    public async Task A_failing_status_source_counts_as_idle_instead_of_returning()
    {
        var s = new Script((PlayniteStatusDto?)null);
        Assert.Equal(PlayniteReturnWatchOutcome.TimedOut, await Run(s, 5, maxIdle: 3));
        Assert.Empty(s.Log);
    }

    // ---- source contract ---------------------------------------------------------------------------------------------------

    private static string Src(params string[] parts)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Path.Combine(new[] { dir ?? throw new InvalidOperationException("repo root not found") }.Concat(parts).ToArray()));
    }

    [Fact]
    public void The_app_wires_the_return_raise_and_log_line_through_the_tested_seams()
    {
        string src = Src("src", "PaRuX.App", "MainWindow.PlayniteStartup.cs");
        Assert.Contains("Playnite-first Console handoff: Playnite closed, returning to PaRuX", src);
        Assert.Contains("PlayniteReturnWatcher.RunAsync", src);
        Assert.Contains("GetLiveStatusAsync", src);
        Assert.Contains("ReturnToPaRuXAfterPlaynite", src);
        Assert.Contains("RaisePlayniteFullscreen();", src);
        Assert.Contains("GamingManager.Current.IsActive", src);   // never raise or return over a running game
        Assert.Contains("PadBringForward();", src);
    }

    [Fact]
    public void The_foreground_helper_uses_the_documented_calls_and_never_minimizes()
    {
        string src = Src("src", "PaRuX.App", "Services", "ForegroundWindowHelper.cs");
        Assert.Contains("AllowSetForegroundWindow", src);
        Assert.Contains("SetForegroundWindow", src);
        Assert.Contains("ShowWindow", src);
        Assert.DoesNotContain("SW_MINIMIZE", src);
        Assert.DoesNotContain("SW_HIDE", src);
        string pad = Src("src", "PaRuX.App", "MainWindow.PadActions.cs");
        Assert.Contains("ForegroundWindowHelper.BringToForeground", pad);
    }

    [Fact]
    public void The_status_endpoint_offers_a_live_refresh_for_the_watch()
    {
        string ep = Src("src", "PaRuX.Server", "Orchestration", "Gaming", "Playnite", "PlayniteEndpoints.cs");
        Assert.Contains("bool live = false", ep);
        Assert.Contains("status?live=true", Src("src", "PaRuX.App", "Services", "PlayniteClient.cs"));
    }
}
