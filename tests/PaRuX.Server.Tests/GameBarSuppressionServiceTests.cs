using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameBarSuppressionServiceTests : IDisposable
{
    private static readonly GameBarValueId Nexus = GameBarSuppressionPolicy.Targets[0];
    private static readonly GameBarValueId Startup = GameBarSuppressionPolicy.Targets[1];
    private static readonly GameBarValueId Dvr = GameBarSuppressionPolicy.Targets[2];
    private static readonly GameBarValueId Capture = GameBarSuppressionPolicy.Targets[3];
    private static readonly GameBarValueId GameMode = new(GameBarSuppressionPolicy.GameBarKey, "AutoGameModeEnabled");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-gamebar-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_dir, "gamebar-journal.json");

    public GameBarSuppressionServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeRegistry : IGameBarRegistry
    {
        public readonly Dictionary<GameBarValueId, GameBarRegState> Values = new();
        public readonly List<string> Calls = new();
        public bool RejectWrites;

        public GameBarRegState Read(GameBarValueId id) => Values.TryGetValue(id, out var s) ? s : GameBarRegState.Absent;

        public void WriteDword(GameBarValueId id, int value)
        {
            Calls.Add("set " + id.Name + "=" + value);
            if (RejectWrites) throw new UnauthorizedAccessException();
            Values[id] = new GameBarRegState(true, value);
        }

        public void Delete(GameBarValueId id) { Calls.Add("del " + id.Name); Values.Remove(id); }
    }

    private (GameBarSuppressionService svc, FakeRegistry reg) Make()
    {
        var reg = new FakeRegistry();
        reg.Values[Nexus] = new(true, 1);
        reg.Values[Dvr] = new(true, 1);
        reg.Values[Capture] = new(true, 1);
        // ShowStartupPanel is absent, AutoGameModeEnabled must never be touched.
        reg.Values[GameMode] = new(true, 1);
        return (new GameBarSuppressionService(reg, JournalPath), reg);
    }

    // ---- pure policy ----

    [Fact]
    public void Plan_writes_only_absent_or_nonzero_dword_values_and_records_the_exact_prior()
    {
        var state = new Dictionary<GameBarValueId, GameBarRegState>
        {
            [Nexus] = new(true, 1), [Startup] = GameBarRegState.Absent, [Dvr] = new(true, 0), [Capture] = new(true, null),
        };
        var plan = GameBarSuppressionPolicy.PlanApply(id => state[id], Array.Empty<GameBarJournalValue>());
        Assert.Equal(GameBarPlanKind.Write, plan[0].Kind); Assert.Equal(1, plan[0].Prior);
        Assert.Equal(GameBarPlanKind.Write, plan[1].Kind); Assert.Null(plan[1].Prior);
        Assert.Equal(GameBarPlanKind.SkipAlreadyOff, plan[2].Kind);
        Assert.Equal(GameBarPlanKind.SkipOtherType, plan[3].Kind);
    }

    [Fact]
    public void Plan_never_targets_game_mode_or_hklm_values()
    {
        Assert.False(GameBarSuppressionPolicy.IsTarget(GameMode));
        Assert.False(GameBarSuppressionPolicy.IsTarget(new GameBarValueId(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR")));
        Assert.Equal(4, GameBarSuppressionPolicy.Targets.Count);
    }

    [Fact]
    public void Restore_plan_leaves_values_that_no_longer_read_zero()
    {
        var vals = new[] { new GameBarJournalValue(Nexus.SubKey, Nexus.Name, 1), new GameBarJournalValue(Dvr.SubKey, Dvr.Name, 1), new GameBarJournalValue(Capture.SubKey, Capture.Name, 1) };
        var state = new Dictionary<GameBarValueId, GameBarRegState> { [Nexus] = new(true, 0), [Dvr] = new(true, 1), [Capture] = GameBarRegState.Absent };
        var plan = GameBarSuppressionPolicy.PlanRestore(vals, id => state[id]);
        Assert.Equal(GameBarRestoreKind.Restore, plan[0].Kind);
        Assert.Equal(GameBarRestoreKind.LeaveUserChange, plan[1].Kind);
        Assert.Equal(GameBarRestoreKind.AlreadyGone, plan[2].Kind);
    }

    // ---- service + adapter ----

    [Fact]
    public void Apply_without_consent_changes_nothing()
    {
        var (svc, reg) = Make();
        Assert.Equal(GameBarOutcome.Refused, svc.Apply(false, false).Outcome);
        Assert.Empty(reg.Calls);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Apply_sets_only_the_three_needed_values_to_zero_and_leaves_game_mode_alone()
    {
        var (svc, reg) = Make();
        Assert.Equal(GameBarOutcome.Ok, svc.Apply(true, false).Outcome);
        Assert.Equal(0, reg.Values[Nexus].Dword);
        Assert.Equal(0, reg.Values[Startup].Dword);
        Assert.Equal(0, reg.Values[Dvr].Dword);
        Assert.Equal(0, reg.Values[Capture].Dword);
        Assert.Equal(1, reg.Values[GameMode].Dword);
        Assert.DoesNotContain(reg.Calls, c => c.Contains("AutoGameMode"));
        Assert.True(File.Exists(JournalPath));
        Assert.True(svc.Status().Applied);
    }

    [Fact]
    public void Restore_puts_back_exact_prior_values_including_absence()
    {
        var (svc, reg) = Make();
        svc.Apply(true, false);
        Assert.Equal(GameBarOutcome.Ok, svc.Restore().Outcome);
        Assert.Equal(1, reg.Values[Nexus].Dword);
        Assert.False(reg.Values.ContainsKey(Startup));   // was absent, absent again
        Assert.Equal(1, reg.Values[Dvr].Dword);
        Assert.Equal(1, reg.Values[Capture].Dword);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Values_already_off_are_not_journaled_and_not_changed_by_restore()
    {
        var (svc, reg) = Make();
        reg.Values[Nexus] = new(true, 0);
        svc.Apply(true, false);
        Assert.DoesNotContain(reg.Calls, c => c == "set UseNexusForGameBarEnabled=0");
        svc.Restore();
        Assert.Equal(0, reg.Values[Nexus].Dword);   // the owner had it off before, it stays off
    }

    [Fact]
    public void Nothing_to_change_creates_no_journal()
    {
        var (svc, reg) = Make();
        foreach (var t in GameBarSuppressionPolicy.Targets) reg.Values[t] = new(true, 0);
        Assert.Equal(GameBarOutcome.Ok, svc.Apply(true, false).Outcome);
        Assert.Empty(reg.Calls);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Value_changed_by_the_owner_meanwhile_is_left_alone_on_restore()
    {
        var (svc, reg) = Make();
        svc.Apply(true, false);
        reg.Values[Nexus] = new(true, 1);   // the owner re-enabled the controller button in Windows Settings
        reg.Values.Remove(Dvr);              // and something else removed another value
        var r = svc.Restore();
        Assert.Equal(GameBarOutcome.Ok, r.Outcome);
        Assert.Equal(1, reg.Values[Nexus].Dword);
        Assert.False(reg.Values.ContainsKey(Dvr));
        Assert.Equal(1, reg.Values[Capture].Dword);   // untouched ones are still restored
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Crash_recovery_with_a_new_service_instance_restores_from_the_journal()
    {
        var (svc, reg) = Make();
        svc.Apply(true, false);
        var afterCrash = new GameBarSuppressionService(reg, JournalPath);   // new process, same journal file
        Assert.True(afterCrash.Status().Applied);
        Assert.Equal(GameBarOutcome.Ok, afterCrash.RecoverAfterCrash().Outcome);
        Assert.Equal(1, reg.Values[Nexus].Dword);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Crash_recovery_does_not_override_a_value_the_owner_changed_after_the_crash()
    {
        var (svc, reg) = Make();
        svc.Apply(true, false);
        reg.Values[Capture] = new(true, 1);
        new GameBarSuppressionService(reg, JournalPath).RecoverAfterCrash();
        Assert.Equal(1, reg.Values[Capture].Dword);
        Assert.DoesNotContain(reg.Calls.Skip(4), c => c == "set AppCaptureEnabled=1");
    }

    [Fact]
    public void Permanent_block_survives_automatic_restore_and_is_released_only_explicitly()
    {
        var (svc, reg) = Make();
        svc.Apply(true, permanent: true);
        Assert.True(svc.Status().Permanent);
        svc.Restore();
        new GameBarSuppressionService(reg, JournalPath).RecoverAfterCrash();
        Assert.Equal(0, reg.Values[Nexus].Dword);
        Assert.True(File.Exists(JournalPath));
        Assert.Equal(GameBarOutcome.Ok, svc.Restore(includePermanent: true).Outcome);
        Assert.Equal(1, reg.Values[Nexus].Dword);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Second_apply_is_idempotent_and_can_upgrade_to_permanent()
    {
        var (svc, reg) = Make();
        svc.Apply(true, false);
        int calls = reg.Calls.Count;
        Assert.Equal(GameBarOutcome.Ok, svc.Apply(true, true).Outcome);
        Assert.Equal(calls, reg.Calls.Count);
        Assert.True(svc.Status().Permanent);
    }

    [Fact]
    public void Non_dword_value_is_never_touched()
    {
        var (svc, reg) = Make();
        reg.Values[Startup] = new(true, null);
        svc.Apply(true, false);
        Assert.DoesNotContain(reg.Calls, c => c.Contains("ShowStartupPanel"));
        svc.Restore();
        Assert.DoesNotContain(reg.Calls, c => c.Contains("ShowStartupPanel"));
    }

    [Fact]
    public void Rejected_write_rolls_back_and_leaves_no_restore_point()
    {
        var (svc, reg) = Make();
        reg.RejectWrites = true;
        var r = svc.Apply(true, false);
        Assert.Equal(GameBarOutcome.Failed, r.Outcome);
        Assert.Equal(1, reg.Values[Nexus].Dword);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Corrupt_or_foreign_journal_is_set_aside_and_never_writes_outside_the_whitelist()
    {
        var (svc, reg) = Make();
        File.WriteAllText(JournalPath, "{\"Permanent\":false,\"AppliedUtc\":\"2026-01-01T00:00:00Z\",\"Values\":[{\"SubKey\":\"SOFTWARE\\\\Evil\",\"Name\":\"X\",\"Prior\":5}]}");
        Assert.Equal(GameBarOutcome.Ok, svc.Restore().Outcome);
        Assert.Empty(reg.Calls);
        Assert.False(File.Exists(JournalPath));
        Assert.NotEmpty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public void Adapter_rejects_values_outside_the_whitelist()
    {
        var adapter = new WindowsGameBarRegistry();
        Assert.Throws<InvalidOperationException>(() => adapter.WriteDword(GameMode, 0));
        Assert.Throws<InvalidOperationException>(() => adapter.Delete(new GameBarValueId(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR")));
    }
}
