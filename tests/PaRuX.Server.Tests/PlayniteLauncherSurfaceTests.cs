using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PlayniteLauncherSurfaceTests
{
    private static string[] Cmd(params string[] args) => new[] { @"C:\PaRuX\PaRuX.exe" }.Concat(args).ToArray();

    [Theory]
    [InlineData("--playnite-console", PlayniteLauncherAction.Console)]
    [InlineData("--PLAYNITE-DESKTOP", PlayniteLauncherAction.Desktop)]
    [InlineData("--streaming", PlayniteLauncherAction.Streaming)]
    [InlineData("--playnite-settings", PlayniteLauncherAction.Settings)]
    [InlineData("--playnite-launcher", PlayniteLauncherAction.Open)]
    [InlineData("--play", PlayniteLauncherAction.None)]          // the game shortcut argument is not a launcher argument
    [InlineData("--exit", PlayniteLauncherAction.None)]
    [InlineData("--playnite-console-x", PlayniteLauncherAction.None)]
    public void Arguments_are_read_case_insensitively_and_exactly(string arg, PlayniteLauncherAction expected) =>
        Assert.Equal(expected, PlayniteLauncherArguments.Parse(Cmd(arg)).Action);

    [Fact]
    public void The_executable_itself_is_never_an_argument_and_null_is_safe()
    {
        Assert.Equal(PlayniteLauncherAction.None, PlayniteLauncherArguments.Parse(new[] { "--playnite-console" }).Action);
        Assert.Equal(PlayniteLauncherAction.None, PlayniteLauncherArguments.Parse(null).Action);
    }

    [Fact]
    public void Several_arguments_resolve_in_the_owners_order_settings_streaming_console_desktop()
    {
        Assert.Equal(PlayniteLauncherAction.Settings, PlayniteLauncherArguments.Parse(Cmd("--playnite-console", "--streaming", "--playnite-settings")).Action);
        Assert.Equal(PlayniteLauncherAction.Streaming, PlayniteLauncherArguments.Parse(Cmd("--playnite-desktop", "--streaming")).Action);
        Assert.Equal(PlayniteLauncherAction.Console, PlayniteLauncherArguments.Parse(Cmd("--playnite-desktop", "--playnite-console")).Action);
        Assert.True(PlayniteLauncherArguments.Parse(Cmd("--playnite-gamepad", "--playnite-desktop")).Gamepad);
        Assert.False(PlayniteLauncherArguments.Parse(Cmd("--playnite-desktop")).Gamepad);
    }

    [Theory]
    [InlineData(PlayniteLauncherAction.Console)]
    [InlineData(PlayniteLauncherAction.Desktop)]
    [InlineData(PlayniteLauncherAction.Streaming)]
    [InlineData(PlayniteLauncherAction.Settings)]
    [InlineData(PlayniteLauncherAction.Open)]
    public void Arguments_for_an_action_round_trip(PlayniteLauncherAction action) =>
        Assert.Equal(action, PlayniteLauncherArguments.Parse(Cmd(PlayniteLauncherArguments.ArgumentsFor(action))).Action);

    [Theory]
    [InlineData(false, false, PlayniteLauncherAction.None)]
    [InlineData(true, false, PlayniteLauncherAction.Settings)]
    [InlineData(false, true, PlayniteLauncherAction.Streaming)]
    [InlineData(true, true, PlayniteLauncherAction.Settings)]   // a stray Shift never starts streaming when Ctrl is held
    public void Held_modifiers_choose_the_mode_when_no_argument_was_given(bool ctrl, bool shift, PlayniteLauncherAction expected) =>
        Assert.Equal(expected, PlayniteLauncherModeResolver.Resolve(PlayniteLauncherRequest.Nothing, ctrl, shift).Action);

    [Fact]
    public void An_explicit_argument_beats_held_modifiers()
    {
        var request = new PlayniteLauncherRequest(PlayniteLauncherAction.Console, false);
        Assert.Equal(PlayniteLauncherAction.Console, PlayniteLauncherModeResolver.Resolve(request, true, true).Action);
        Assert.Equal(PlayniteLauncherAction.None, PlayniteLauncherModeResolver.Resolve(null, false, false).Action);
    }

    [Fact]
    public void Modifier_sampling_returns_an_early_key_at_once_and_never_sleeps()
    {
        int sleeps = 0;
        var result = PlayniteLauncherModeResolver.Sample((false, true), () => (false, false), _ => sleeps++, () => DateTime.UtcNow,
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20));
        Assert.True(result.Shift);
        Assert.Equal(0, sleeps);
    }

    [Fact]
    public void Modifier_sampling_catches_a_key_that_arrives_inside_the_grace_window_and_is_bounded_otherwise()
    {
        DateTime clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int reads = 0;
        var late = PlayniteLauncherModeResolver.Sample((false, false), () => (false, ++reads >= 4), d => clock += d, () => clock,
            TimeSpan.FromSeconds(1.5), TimeSpan.FromMilliseconds(20));
        Assert.True(late.Shift);

        clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int sleeps = 0;
        var none = PlayniteLauncherModeResolver.Sample((false, false), () => (false, false), d => { clock += d; sleeps++; }, () => clock,
            TimeSpan.FromSeconds(1.5), TimeSpan.FromMilliseconds(20));
        Assert.Equal((false, false), none);
        Assert.InRange(sleeps, 1, 100);   // stops at the deadline, never loops forever
    }

    [Theory]
    [InlineData(0, true, true, 0, ConsoleProbeStep.Present)]
    [InlineData(0, true, false, 1, ConsoleProbeStep.RetryAfterDelay)]   // absent once: confirm again
    [InlineData(1, true, false, 2, ConsoleProbeStep.Absent)]             // absent twice: decided
    [InlineData(0, false, false, 0, ConsoleProbeStep.RetryAfterDelay)]   // Core not answering yet
    [InlineData(6, false, false, 0, ConsoleProbeStep.GiveUp)]            // budget used up: the owner's fallback applies
    public void The_tv_probe_is_bounded_and_confirms_absence(int index, bool readable, bool present, int absentSeen, ConsoleProbeStep expected) =>
        Assert.Equal(expected, ConsoleStartupProbePolicy.Next(index, ConsoleStartupProbePolicy.DefaultMaxProbes, readable, present, absentSeen, ConsoleStartupProbePolicy.DefaultAbsentConfirmations));

    [Fact]
    public void A_giving_up_probe_means_the_fallback_not_console()
    {
        Assert.Equal(ConsoleProbeStep.GiveUp, ConsoleStartupProbePolicy.Next(6, 7, false, false, 0, 2));
        Assert.Equal(ConsoleStartupDecision.Minimize, ConsoleStartupPolicy.Decide(true, "tv-1", tvPresent: false, ConsoleStartupPolicy.FallbackMinimized));
        Assert.Equal(ConsoleStartupDecision.Normal, ConsoleStartupPolicy.Decide(true, "tv-1", tvPresent: false, "normal"));
    }

    // ---- single-instance request forwarding ------------------------------------------------------------------------------
    private static string TempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-plr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void A_request_is_queued_drained_once_and_the_latest_wins()
    {
        string root = TempRoot();
        try
        {
            Assert.True(PlayniteLauncherRequestChannel.TryEnqueue(root, PlayniteLauncherAction.Console));
            Assert.True(PlayniteLauncherRequestChannel.TryEnqueue(root, PlayniteLauncherAction.Settings));
            Assert.False(PlayniteLauncherRequestChannel.TryEnqueue(root, PlayniteLauncherAction.None));
            var drained = PlayniteLauncherRequestChannel.Drain(root);
            Assert.Equal(new[] { PlayniteLauncherAction.Console, PlayniteLauncherAction.Settings }, drained);
            Assert.Equal(PlayniteLauncherAction.Settings, PlayniteLauncherRequestChannel.PickWinner(drained));
            Assert.Empty(PlayniteLauncherRequestChannel.Drain(root));   // taken: never replayed
            Assert.Equal(PlayniteLauncherAction.None, PlayniteLauncherRequestChannel.PickWinner(Array.Empty<PlayniteLauncherAction>()));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Stale_planted_and_oversized_requests_are_dropped()
    {
        string root = TempRoot();
        try
        {
            string dir = PlayniteLauncherRequestChannel.QueueDirectoryFor(root);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "1-a.plr"), "console");
            File.WriteAllText(Path.Combine(dir, "2-b.plr"), "calc.exe");           // not an action name
            File.WriteAllText(Path.Combine(dir, "3-c.plr"), "2");                  // numeric enum value is refused
            File.WriteAllText(Path.Combine(dir, "4-d.plr"), new string('x', 500)); // oversized
            Assert.Empty(PlayniteLauncherRequestChannel.Drain(root, DateTime.UtcNow.AddMinutes(10)));   // all stale
            File.WriteAllText(Path.Combine(dir, "1-a.plr"), "console");
            File.WriteAllText(Path.Combine(dir, "2-b.plr"), "calc.exe");
            File.WriteAllText(Path.Combine(dir, "3-c.plr"), "2");
            Assert.Equal(new[] { PlayniteLauncherAction.Console }, PlayniteLauncherRequestChannel.Drain(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void The_queue_is_bounded()
    {
        string root = TempRoot();
        try
        {
            for (int i = 0; i < PlayniteLauncherRequestChannel.MaxQueuedRequests; i++)
                Assert.True(PlayniteLauncherRequestChannel.TryEnqueue(root, PlayniteLauncherAction.Open));
            Assert.False(PlayniteLauncherRequestChannel.TryEnqueue(root, PlayniteLauncherAction.Open));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void The_launcher_event_never_collides_with_the_game_or_exit_events()
    {
        string root = TempRoot();
        try
        {
            string launcher = PlayniteLauncherRequestChannel.EventNameFor(root);
            Assert.NotEqual(GameLaunchRequestChannel.EventNameFor(root), launcher);
            Assert.NotEqual(AppExitSignal.NameFor(root), launcher);
            Assert.StartsWith("Local\\", launcher);
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- shortcut planning (nothing touches the real desktop) -------------------------------------------------------------
    private const string Exe = @"C:\Apps\PaRuX\PaRuX.exe";
    private const string Folder = @"C:\Fake\Desktop";

    private static ShortcutTarget Target(string exe, string args) => new(exe, args, @"C:\Apps\PaRuX", "", exe, 0);

    [Fact]
    public void A_plan_proposes_one_new_file_per_chosen_action_and_writes_nothing()
    {
        var plan = PlayniteLauncherShortcuts.PlanCreate(Folder, Exe, PlayniteLauncherShortcuts.AllActions, _ => false, _ => null);
        Assert.Equal(4, plan.Count);
        Assert.All(plan, p => Assert.Equal(PlannedShortcutState.Create, p.State));
        Assert.Contains(plan, p => p.Arguments == "--playnite-console" && p.Path == Path.Combine(Folder, "Playnite Console (PaRuX).lnk"));
        Assert.Contains(plan, p => p.Arguments == "--streaming");
        Assert.Single(PlayniteLauncherShortcuts.PlanCreate(Folder, Exe, new[] { PlayniteLauncherAction.Settings, PlayniteLauncherAction.Settings }, _ => false, _ => null));
        Assert.Empty(PlayniteLauncherShortcuts.PlanCreate(Folder, Exe, new[] { PlayniteLauncherAction.None, PlayniteLauncherAction.Open }, _ => false, _ => null));
    }

    [Fact]
    public void An_identical_shortcut_is_left_alone_and_a_different_file_is_never_overwritten()
    {
        string console = Path.Combine(Folder, "Playnite Console (PaRuX).lnk");
        string desktop = Path.Combine(Folder, "Playnite Desktop (PaRuX).lnk");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { console, desktop };
        ShortcutTarget? Read(string p) =>
            p == console ? Target(Exe, "--playnite-console")                   // identical: ours
            : p == desktop ? Target(@"D:\Other\Thing.exe", "")                   // the user's own file with our name
            : null;
        var plan = PlayniteLauncherShortcuts.PlanCreate(Folder, Exe,
            new[] { PlayniteLauncherAction.Console, PlayniteLauncherAction.Desktop }, existing.Contains, Read);
        Assert.Equal(PlannedShortcutState.AlreadyExists, plan[0].State);
        Assert.Equal(console, plan[0].Path);
        Assert.Equal(PlannedShortcutState.Create, plan[1].State);
        Assert.Equal(Path.Combine(Folder, "Playnite Desktop (PaRuX 2).lnk"), plan[1].Path);   // next free name, original untouched
        Assert.DoesNotContain(plan, p => p.State == PlannedShortcutState.Create && p.Path == desktop);
    }

    [Fact]
    public void Removal_proposes_only_shortcuts_that_point_at_this_PaRuX_with_one_of_our_arguments()
    {
        var files = new Dictionary<string, ShortcutTarget?>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(Folder, "a.lnk")] = Target(Exe, "--playnite-desktop"),
            [Path.Combine(Folder, "b.lnk")] = Target(Exe, ""),                          // the user's plain PaRuX shortcut: kept
            [Path.Combine(Folder, "c.lnk")] = Target(Exe, "--play abc"),                // a game shortcut: kept
            [Path.Combine(Folder, "d.lnk")] = Target(@"D:\Other\PaRuX.exe", "--streaming"), // another copy: kept
            [Path.Combine(Folder, "e.lnk")] = null,                                     // unreadable: kept
            [Path.Combine(Folder, "f.lnk")] = Target(Exe, "--streaming"),
        };
        var toRemove = PlayniteLauncherShortcuts.PlanRemove(Folder, Exe, _ => files.Keys, p => files[p]);
        Assert.Equal(new[] { Path.Combine(Folder, "a.lnk"), Path.Combine(Folder, "f.lnk") }, toRemove);
        Assert.Empty(PlayniteLauncherShortcuts.PlanRemove("", Exe, _ => files.Keys, p => files[p]));
    }
}
