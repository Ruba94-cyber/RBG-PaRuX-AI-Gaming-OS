using PaRuX.Server.Orchestration.Gaming.Playnite;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Fake process world: a mutable table of processes the tracker observes. Nothing here can be killed or suspended.</summary>
internal sealed class FakeProcessWorld : IProcessSnapshotSource
{
    public static readonly DateTime T0 = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly Dictionary<int, (ProcEntry Entry, ProcDetail Detail)> _procs = new();
    public int IdentifyCalls;

    public FakeProcessWorld Add(int pid, int parent, string name, double startSeconds = 0, bool window = true, string? path = null)
    {
        _procs[pid] = (new ProcEntry(pid, parent, name),
            new ProcDetail(T0.AddSeconds(startSeconds), path ?? @"C:\Games\" + name, window));
        return this;
    }

    public FakeProcessWorld Remove(params int[] pids) { foreach (int p in pids) _procs.Remove(p); return this; }
    public IReadOnlyList<ProcEntry> Capture() => _procs.Values.Select(v => v.Entry).ToList();
    public ProcDetail? Identify(int pid) { IdentifyCalls++; return _procs.TryGetValue(pid, out var v) ? v.Detail : null; }
}

public sealed class PlayniteGameSessionTrackerTests
{
    private const int Self = 9999;
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(4), Grace = TimeSpan.FromSeconds(8);

    private static PlayniteGameSessionTracker Tracker() => new(Self, Debounce, Grace);

    private static PlayniteTrackerResult At(PlayniteGameSessionTracker t, FakeProcessWorld w, double seconds, string? folder = null) =>
        t.Sample(w.Capture(), FakeProcessWorld.T0.AddSeconds(seconds), w.Identify, folder);

    private static FakeProcessWorld WithPlaynite() =>
        new FakeProcessWorld().Add(100, 1, "Playnite.FullscreenApp.exe", -60, path: @"C:\P\Playnite.FullscreenApp.exe");

    /// <summary>Runs samples each second until the event shows up or the time runs out.</summary>
    private static (PlayniteTrackerEvent Event, double At) Run(PlayniteGameSessionTracker t, FakeProcessWorld w, double from, double to)
    {
        for (double s = from; s <= to; s++)
        {
            var r = At(t, w, s);
            if (r.Event != PlayniteTrackerEvent.None) return (r.Event, s);
        }
        return (PlayniteTrackerEvent.None, -1);
    }

    [Fact]
    public void Game_started_by_playnite_starts_a_session_only_after_the_debounce()
    {
        var w = WithPlaynite().Add(200, 100, "game.exe", 10);
        var t = Tracker();
        Assert.Equal(PlayniteTrackerEvent.None, At(t, w, 10).Event);
        Assert.Equal(PlayniteTrackerEvent.None, At(t, w, 13).Event);
        var r = At(t, w, 14);
        Assert.Equal(PlayniteTrackerEvent.Start, r.Event);
        Assert.True(r.GameActive);
        Assert.Equal("game", r.Primary!.Name);
    }

    [Fact]
    public void Rapid_start_and_stop_never_starts_a_session()
    {
        var w = WithPlaynite().Add(200, 100, "stub.exe", 10);
        var t = Tracker();
        At(t, w, 10); At(t, w, 12);
        w.Remove(200);
        Assert.Equal((PlayniteTrackerEvent.None, -1), Run(t, w, 13, 60));
        Assert.False(t.GameActive);
    }

    [Fact]
    public void Repeated_rapid_start_stop_cycles_never_start_a_session()
    {
        var w = WithPlaynite();
        var t = Tracker();
        for (int i = 0; i < 5; i++)
        {
            w.Add(200 + i, 100, "stub" + i + ".exe", i * 10);
            At(t, w, i * 10 + 1);
            w.Remove(200 + i);
            At(t, w, i * 10 + 3);
        }
        Assert.False(t.GameActive);
    }

    [Fact]
    public void Nested_launchers_are_followed_to_the_real_game()
    {
        var w = WithPlaynite().Add(201, 100, "cmd.exe", 10).Add(202, 201, "launcher.exe", 11).Add(203, 202, "game.exe", 12);
        var t = Tracker();
        var (ev, _) = Run(t, w, 12, 30);
        Assert.Equal(PlayniteTrackerEvent.Start, ev);
        Assert.Equal(3, At(t, w, 31).Game.Count);
    }

    [Fact]
    public void Table_order_does_not_matter_for_deep_trees()
    {
        var w = new FakeProcessWorld().Add(203, 202, "game.exe", 12).Add(202, 201, "launcher.exe", 11)
            .Add(201, 100, "cmd.exe", 10).Add(100, 1, "Playnite.DesktopApp.exe", -5);
        var t = Tracker();
        Assert.Equal(PlayniteTrackerEvent.Start, Run(t, w, 12, 30).Event);
    }

    [Fact]
    public void Child_exiting_first_keeps_the_session_while_the_parent_lives()
    {
        var w = WithPlaynite().Add(201, 100, "launcher.exe", 10).Add(202, 201, "game.exe", 11);
        var t = Tracker();
        Run(t, w, 11, 30);
        w.Remove(202);   // game child exits, launcher still alive
        Assert.Equal(PlayniteTrackerEvent.None, Run(t, w, 31, 60).Event);
        Assert.True(t.GameActive);
    }

    [Fact]
    public void Parent_exiting_first_keeps_the_session_while_the_child_lives()
    {
        var w = WithPlaynite().Add(201, 100, "launcher.exe", 10).Add(202, 201, "game.exe", 11);
        var t = Tracker();
        Run(t, w, 11, 30);
        w.Remove(201);   // launcher exits, orphaned game continues
        Assert.Equal(PlayniteTrackerEvent.None, Run(t, w, 31, 90).Event);
        Assert.True(t.GameActive);
    }

    [Fact]
    public void Session_ends_only_after_the_grace_when_everything_exited()
    {
        var w = WithPlaynite().Add(200, 100, "game.exe", 10);
        var t = Tracker();
        Run(t, w, 10, 30);
        w.Remove(200);
        Assert.Equal(PlayniteTrackerEvent.None, At(t, w, 40).Event);
        Assert.Equal(PlayniteTrackerEvent.None, At(t, w, 47).Event);
        Assert.Equal(PlayniteTrackerEvent.End, At(t, w, 48).Event);
        Assert.False(t.GameActive);
    }

    [Fact]
    public void Crash_with_error_reporter_still_ends_the_session()
    {
        var w = WithPlaynite().Add(200, 100, "game.exe", 10);
        var t = Tracker();
        Run(t, w, 10, 30);
        w.Remove(200).Add(300, 1, "WerFault.exe", 40).Add(301, 100, "WerFault.exe", 40);
        Assert.Equal(PlayniteTrackerEvent.End, Run(t, w, 40, 70).Event);
    }

    [Fact]
    public void Crash_handler_child_does_not_hold_the_session_open()
    {
        var w = WithPlaynite().Add(200, 100, "game.exe", 10).Add(210, 200, "UnityCrashHandler64.exe", 11);
        var t = Tracker();
        Run(t, w, 11, 30);
        w.Remove(200);
        Assert.Equal(PlayniteTrackerEvent.End, Run(t, w, 31, 60).Event);
    }

    [Fact]
    public void Closing_playnite_while_the_game_runs_does_not_end_the_session()
    {
        var w = WithPlaynite().Add(200, 100, "game.exe", 10);
        var t = Tracker();
        Run(t, w, 10, 30);
        w.Remove(100);   // Playnite closed, game orphaned
        Assert.Equal(PlayniteTrackerEvent.None, Run(t, w, 31, 120).Event);
        var r = At(t, w, 121);
        Assert.True(r.GameActive);
        Assert.False(r.PlayniteAlive);
        w.Remove(200);
        Assert.Equal(PlayniteTrackerEvent.End, Run(t, w, 122, 150).Event);
    }

    [Fact]
    public void Relaunch_within_the_grace_is_the_same_session()
    {
        var w = WithPlaynite().Add(200, 100, "launcher-stub.exe", 10);
        var t = Tracker();
        Run(t, w, 10, 30);
        w.Remove(200);
        At(t, w, 33);
        w.Add(201, 100, "game.exe", 34);
        Assert.Equal(PlayniteTrackerEvent.None, Run(t, w, 34, 80).Event);
        Assert.True(t.GameActive);
    }

    [Fact]
    public void Relaunch_after_the_end_is_a_new_session()
    {
        var w = WithPlaynite().Add(200, 100, "game.exe", 10);
        var t = Tracker();
        Assert.Equal(PlayniteTrackerEvent.Start, Run(t, w, 10, 30).Event);
        w.Remove(200);
        Assert.Equal(PlayniteTrackerEvent.End, Run(t, w, 31, 60).Event);
        w.Add(300, 100, "other-game.exe", 70);
        Assert.Equal(PlayniteTrackerEvent.Start, Run(t, w, 70, 100).Event);
    }

    [Fact]
    public void Foreign_processes_are_never_tracked()
    {
        var w = WithPlaynite().Add(500, 1, "chrome.exe", 5).Add(501, 500, "chrome-gpu.exe", 5).Add(502, 4, "other.exe", 5);
        var t = Tracker();
        var r = At(t, w, 20);
        Assert.Empty(r.Game);
        Assert.Equal((PlayniteTrackerEvent.None, -1), Run(t, w, 21, 80));
    }

    [Fact]
    public void Without_playnite_nothing_is_tracked_and_nothing_is_identified()
    {
        var w = new FakeProcessWorld().Add(500, 1, "game.exe", 5).Add(501, 500, "child.exe", 6);
        var t = Tracker();
        Assert.Empty(At(t, w, 20).Game);
        Assert.Equal(0, w.IdentifyCalls);
    }

    [Fact]
    public void A_reused_parent_id_is_not_a_parent()
    {
        // The child started long before this Playnite: its recorded parent id was reused.
        var w = WithPlaynite().Add(200, 100, "old.exe", -3600);
        Assert.Empty(At(Tracker(), w, 20).Game);
    }

    [Fact]
    public void A_tracked_id_reused_by_another_program_is_dropped()
    {
        var w = WithPlaynite().Add(200, 100, "game.exe", 10);
        var t = Tracker();
        Run(t, w, 10, 30);
        w.Remove(200).Add(200, 1, "unrelated.exe", 40);
        var r = At(t, w, 41);
        Assert.DoesNotContain(r.Game, g => g.Pid == 200);
    }

    [Theory]
    [InlineData("CefSharp.BrowserSubprocess.exe")]
    [InlineData("Playnite.Common.exe")]
    [InlineData("conhost.exe")]
    [InlineData("crashpad_handler.exe")]
    [InlineData("vc_redist.x64.exe")]
    [InlineData("MyGameUpdater.exe")]
    public void Helper_processes_are_never_the_game(string name)
    {
        var w = WithPlaynite().Add(200, 100, name, 10);
        var t = Tracker();
        Assert.Empty(At(t, w, 20).Game);
        Assert.Equal((PlayniteTrackerEvent.None, -1), Run(t, w, 21, 60));
    }

    [Fact]
    public void Playnite_switching_its_own_mode_is_not_a_game()
    {
        var w = WithPlaynite().Add(200, 100, "Playnite.DesktopApp.exe", 10, path: @"C:\P\Playnite.DesktopApp.exe");
        Assert.Empty(At(Tracker(), w, 20).Game);
    }

    [Theory]
    [InlineData("svchost.exe")]
    [InlineData("explorer.exe")]
    [InlineData("PaRuX.Server.exe")]
    [InlineData("PaRuXHelper.exe")]
    public void Windows_security_and_paRuX_processes_are_never_tracked(string name)
    {
        var w = WithPlaynite().Add(200, 100, name, 10);
        Assert.Empty(At(Tracker(), w, 20).Game);
    }

    [Fact]
    public void Own_process_id_is_never_tracked()
    {
        var w = WithPlaynite().Add(Self, 100, "game.exe", 10);
        Assert.Empty(At(Tracker(), w, 20).Game);
    }

    [Fact]
    public void Store_launcher_child_with_a_window_is_adopted_while_playnite_runs()
    {
        var w = WithPlaynite().Add(300, 1, "steam.exe", -100).Add(301, 300, "bigGame.exe", 10, window: true);
        var t = Tracker();
        Assert.Equal(PlayniteTrackerEvent.Start, Run(t, w, 10, 30).Event);
    }

    [Fact]
    public void Store_launcher_helper_children_are_not_games()
    {
        var w = WithPlaynite().Add(300, 1, "steam.exe", -100).Add(301, 300, "steamwebhelper.exe", 10).Add(302, 300, "gameoverlayui.exe", 10);
        Assert.Empty(At(Tracker(), w, 20).Game);
    }

    [Fact]
    public void Windowless_store_launcher_child_is_not_adopted()
    {
        var w = WithPlaynite().Add(300, 1, "steam.exe", -100).Add(301, 300, "daemon.exe", 10, window: false);
        Assert.Empty(At(Tracker(), w, 30).Game);
    }

    [Fact]
    public void Store_launcher_child_is_ignored_when_playnite_is_not_running()
    {
        var w = new FakeProcessWorld().Add(300, 1, "steam.exe", -100).Add(301, 300, "bigGame.exe", 10);
        Assert.Empty(At(Tracker(), w, 30).Game);
    }

    [Fact]
    public void Store_launcher_child_older_than_the_playnite_session_is_not_adopted()
    {
        var w = WithPlaynite().Add(300, 1, "steam.exe", -300).Add(301, 300, "alreadyRunning.exe", -200, window: true);
        Assert.Empty(At(Tracker(), w, 30).Game);
    }

    [Fact]
    public void Playnite_from_another_folder_is_not_the_located_installation()
    {
        var w = WithPlaynite().Add(200, 100, "game.exe", 10);
        var t = Tracker();
        Assert.Empty(At(t, w, 20, folder: @"D:\OtherPlaynite").Game);
    }

    [Fact]
    public void Tracked_set_is_capped()
    {
        var w = WithPlaynite();
        for (int i = 0; i < 400; i++) w.Add(1000 + i, 100, "child" + i + ".exe", 10);
        Assert.True(At(Tracker(), w, 20).Game.Count <= PlayniteGameSessionTracker.MaxTracked);
    }

    [Fact]
    public void Reset_forgets_the_session()
    {
        var w = WithPlaynite().Add(200, 100, "game.exe", 10);
        var t = Tracker();
        Run(t, w, 10, 30);
        Assert.True(t.GameActive);
        t.Reset();
        Assert.False(t.GameActive);
    }

    [Fact]
    public void Tracker_has_no_way_to_act_on_processes()
    {
        // The tracker's only inputs are a snapshot and an identity lookup; its API exposes no terminate/suspend/start member.
        var members = typeof(PlayniteGameSessionTracker).GetMethods().Select(m => m.Name).ToArray();
        Assert.DoesNotContain(members, n => n.Contains("Kill", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Suspend", StringComparison.OrdinalIgnoreCase) || n.Contains("Terminate", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Start", StringComparison.OrdinalIgnoreCase));
    }
}
