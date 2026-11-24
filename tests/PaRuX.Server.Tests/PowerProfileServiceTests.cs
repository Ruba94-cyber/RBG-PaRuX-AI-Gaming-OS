using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PowerProfileServiceTests : IDisposable
{
    private const string Balanced = PowerProfileService.BalancedPlanId;
    private const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Custom = "11111111-2222-3333-4444-555555555555";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-power-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_dir, "power-journal.json");

    public PowerProfileServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakePower : IPowerPlanApi
    {
        public string Active = Balanced;
        public List<PowerPlanInfo> Plans = new() { new(Balanced, "Balanced"), new(High, "High performance") };
        public bool RejectSet, SetThrows, SilentlyIgnoreSet;
        public readonly List<string> Sets = new();
        public string? GetActive() => Active;
        public IReadOnlyList<PowerPlanInfo> List() => Plans;
        public bool SetActive(string id)
        {
            Sets.Add(id);
            if (SetThrows) throw new InvalidOperationException("boom");
            if (RejectSet) return false;
            if (!SilentlyIgnoreSet) Active = id;
            return true;
        }
    }

    private (PowerProfileService svc, FakePower api) Make() { var api = new FakePower(); return (new PowerProfileService(api, JournalPath), api); }

    [Fact]
    public void Apply_without_consent_changes_nothing()
    {
        var (svc, api) = Make();
        var r = svc.Apply(High, consent: false);
        Assert.Equal(PowerProfileOutcome.Refused, r.Outcome);
        Assert.Empty(api.Sets);
        Assert.False(File.Exists(JournalPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("{8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c}")]
    public void Apply_rejects_a_malformed_plan_id(string? id)
    {
        var (svc, api) = Make();
        Assert.Equal(PowerProfileOutcome.Refused, svc.Apply(id, true).Outcome);
        Assert.Empty(api.Sets);
    }

    [Fact]
    public void Apply_refuses_a_plan_that_does_not_exist_and_never_creates_one()
    {
        var (svc, api) = Make();
        Assert.Equal(PowerProfileOutcome.Refused, svc.Apply(Custom, true).Outcome);
        Assert.Empty(api.Sets);
    }

    [Fact]
    public void Apply_then_restore_puts_the_previous_plan_back_and_clears_the_journal()
    {
        var (svc, api) = Make();
        Assert.Equal(PowerProfileOutcome.Ok, svc.Apply(High, true).Outcome);
        Assert.Equal(High, api.Active);
        Assert.True(File.Exists(JournalPath));
        Assert.True(svc.Status().Applied);

        Assert.Equal(PowerProfileOutcome.Ok, svc.Restore().Outcome);
        Assert.Equal(Balanced, api.Active);
        Assert.False(File.Exists(JournalPath));
        Assert.False(svc.Status().Applied);
    }

    [Fact]
    public void Applying_the_already_active_plan_is_a_no_op_without_a_journal()
    {
        var (svc, api) = Make();
        Assert.Equal(PowerProfileOutcome.Ok, svc.Apply(Balanced, true).Outcome);
        Assert.Empty(api.Sets);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Second_apply_of_the_same_plan_is_idempotent_and_a_different_plan_needs_a_restore_first()
    {
        var (svc, api) = Make();
        api.Plans.Add(new(Custom, "Custom"));
        svc.Apply(High, true);
        int sets = api.Sets.Count;
        Assert.Equal(PowerProfileOutcome.Ok, svc.Apply(High, true).Outcome);
        Assert.Equal(sets, api.Sets.Count);
        Assert.Equal(PowerProfileOutcome.Refused, svc.Apply(Custom, true).Outcome);
        Assert.Equal(High, api.Active);
    }

    [Fact]
    public void Restore_never_overrides_a_plan_the_owner_chose_meanwhile()
    {
        var (svc, api) = Make();
        api.Plans.Add(new(Custom, "Custom"));
        svc.Apply(High, true);
        api.Active = Custom;   // the owner switched plans by hand
        api.Sets.Clear();
        var r = svc.Restore();
        Assert.Equal(PowerProfileOutcome.Ok, r.Outcome);
        Assert.Empty(api.Sets);
        Assert.Equal(Custom, api.Active);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Restore_falls_back_to_balanced_when_the_previous_plan_was_deleted()
    {
        var (svc, api) = Make();
        api.Plans.Add(new(Custom, "Custom"));
        api.Active = Custom;
        svc.Apply(High, true);
        api.Plans.RemoveAll(p => p.Id == Custom);
        Assert.Equal(PowerProfileOutcome.Ok, svc.Restore().Outcome);
        Assert.Equal(Balanced, api.Active);
    }

    [Fact]
    public void A_rejected_change_rolls_back_and_leaves_no_journal()
    {
        var (svc, api) = Make();
        api.RejectSet = true;
        var r = svc.Apply(High, true);
        Assert.Equal(PowerProfileOutcome.Failed, r.Outcome);
        Assert.Equal(Balanced, api.Active);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void A_change_windows_silently_ignores_is_detected_by_the_verification_read()
    {
        var (svc, api) = Make();
        api.SilentlyIgnoreSet = true;
        Assert.Equal(PowerProfileOutcome.Failed, svc.Apply(High, true).Outcome);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void An_api_failure_is_reported_not_thrown()
    {
        var (svc, api) = Make();
        api.SetThrows = true;
        Assert.Equal(PowerProfileOutcome.Failed, svc.Apply(High, true).Outcome);
    }

    [Fact]
    public void Crash_recovery_restores_from_the_journal_with_a_new_service_instance()
    {
        var (svc, api) = Make();
        svc.Apply(High, true);
        var afterCrash = new PowerProfileService(api, JournalPath);   // process restarted: only the file survived
        Assert.Equal(PowerProfileOutcome.Ok, afterCrash.RecoverAfterCrash().Outcome);
        Assert.Equal(Balanced, api.Active);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void A_failed_restore_keeps_the_journal_so_it_is_retried()
    {
        var (svc, api) = Make();
        svc.Apply(High, true);
        api.RejectSet = true;
        Assert.Equal(PowerProfileOutcome.Failed, svc.Restore().Outcome);
        Assert.True(File.Exists(JournalPath));
        api.RejectSet = false;
        Assert.Equal(PowerProfileOutcome.Ok, svc.Restore().Outcome);
        Assert.Equal(Balanced, api.Active);
    }

    [Fact]
    public void A_corrupt_or_oversized_journal_is_set_aside_and_never_trusted()
    {
        var (svc, api) = Make();
        File.WriteAllText(JournalPath, "{ not json");
        var r = svc.Restore();
        Assert.Equal(PowerProfileOutcome.Ok, r.Outcome);
        Assert.Empty(api.Sets);
        Assert.Single(Directory.GetFiles(_dir, "*.corrupt-*"));

        File.WriteAllText(JournalPath, new string('x', (int)PowerProfileService.MaxJournalBytes + 1));
        Assert.Equal(PowerProfileOutcome.Ok, svc.Restore().Outcome);
        Assert.Empty(api.Sets);
    }

    [Fact]
    public void A_journal_with_an_invalid_plan_id_is_rejected()
    {
        var (svc, api) = Make();
        File.WriteAllText(JournalPath, "{\"PreviousPlanId\":\"x\",\"AppliedPlanId\":\"y\",\"AppliedUtc\":\"2026-01-01T00:00:00Z\"}");
        Assert.Equal(PowerProfileOutcome.Ok, svc.Restore().Outcome);
        Assert.Empty(api.Sets);
    }

    [Fact]
    public void Restore_with_nothing_applied_is_a_harmless_no_op()
    {
        var (svc, api) = Make();
        Assert.Equal(PowerProfileOutcome.Ok, svc.Restore().Outcome);
        Assert.Empty(api.Sets);
    }

    [Fact]
    public void Status_lists_the_existing_plans_and_reports_the_active_one()
    {
        var (svc, _) = Make();
        var s = svc.Status();
        Assert.Equal(Balanced, s.ActivePlanId);
        Assert.Equal(2, s.Available.Count);
        Assert.False(s.Applied);
    }

    [Fact]
    public void Dto_mapping_keeps_ids_and_names()
    {
        var (svc, _) = Make();
        var dto = PowerProfileEndpoints.ToDto(svc.Status());
        Assert.Contains(dto.Available, p => p.Id == High && p.Name == "High performance");
    }
}
