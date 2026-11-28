using PaRuX.Server;
using Xunit;
using static PaRuX.Server.RuntimeMemoryBudget;

namespace PaRuX.Server.Tests;

/// <summary>
/// Corporate admission v2: commit headroom for the private part, a physical working floor that counts trimmable memory,
/// and the strict profile ceiling. Pure math, no Windows APIs.
/// </summary>
public sealed class CorporateCommitAdmissionTests
{
    private const ulong Gib = 1024UL * 1024 * 1024;

    // The owner report: 16 GB PC, ~80% RAM in use, large page file.
    private static SystemRamSnapshot Busy16Gb(ulong commitAvail = 20 * Gib) =>
        SystemRamSnapshot.TryCreate(16 * Gib, (ulong)(3.2 * Gib), commitAvail, (ulong)(1.2 * Gib))!;

    // A 4B Q4 model: 2.5 GB memory-mapped weights, about 0.9 GB private (KV, compute buffers, reserve).
    private const double PrivateMib = 0.9 * 1024, MappedMib = 2.5 * 1024;

    [Fact]
    public void Pc80PercentUsed_WithLargeCommit_IsAdmittedWithBusyWarning()
    {
        CorporateAdmission verdict = EvaluateCorporate(Busy16Gb(), 86, 0, PrivateMib, MappedMib);
        Assert.Equal(CorporateAdmissionOutcome.AdmitBusy, verdict.Outcome);
        Assert.True(verdict.Allowed);
        Assert.True(verdict.Busy);
        // The old math refused exactly this machine (about 1.9 GB admitted for 3.4 GB needed).
        Assert.True(CalculateCorporateAvailableBytes(Busy16Gb(), 86) / 1048576d < PrivateMib + MappedMib);
    }

    [Fact]
    public void PlannerKeepsTheFullContext_AndFlagsBusy()
    {
        CorporateCpuMemoryPlanner.Plan plan = CorporateCpuMemoryPlanner.Select(
            Busy16Gb(), 86, 0, ctx => MappedMib + PrivateMib * ctx / 4096d, MappedMib);
        Assert.True(plan.Fits);
        Assert.True(plan.Busy);
        Assert.Equal(4096, plan.ContextTokens);
        Assert.Equal(120, CorporateCpuMemoryPlanner.IdleUnloadSeconds(plan));
        Assert.Contains("Your PC is busy; replies may be slower", CorporateMemoryMessages.Busy);
        Assert.Contains("Il PC è molto occupato", CorporateMemoryMessages.Busy);
    }

    [Fact]
    public void PlentyOfFreeMemory_IsAdmittedWithoutWarning()
    {
        SystemRamSnapshot ram = SystemRamSnapshot.TryCreate(16 * Gib, 10 * Gib, 20 * Gib, Gib)!;
        CorporateAdmission verdict = EvaluateCorporate(ram, 86, 0, PrivateMib, MappedMib);
        Assert.Equal(CorporateAdmissionOutcome.Admit, verdict.Outcome);
        Assert.False(verdict.Busy);
    }

    [Fact]
    public void CommitExhausted_IsRefused_EvenWithFreePhysicalMemory()
    {
        SystemRamSnapshot ram = SystemRamSnapshot.TryCreate(16 * Gib, 10 * Gib, (ulong)(0.6 * Gib), Gib)!;
        CorporateAdmission verdict = EvaluateCorporate(ram, 86, 0, PrivateMib, MappedMib);
        Assert.Equal(CorporateAdmissionOutcome.RefuseCommit, verdict.Outcome);
        CorporateCpuMemoryPlanner.Plan plan = CorporateCpuMemoryPlanner.Select(
            ram, 86, 0, ctx => MappedMib + PrivateMib * ctx / 4096d, MappedMib);
        Assert.False(plan.Fits);
        string message = CorporateCpuMemoryPlanner.Shortfall(plan, ram, 86);
        Assert.Contains("Not enough free memory", message);          // English
        Assert.Contains("Memoria insufficiente", message);           // Italian
        Assert.Contains("PaRuX did not close any program", message); // next action stays honest
        Assert.Contains("Close a heavy program", message);
    }

    [Fact]
    public void MemoryMappedWeights_DoNotConsumeCommit()
    {
        // 1.6 GB commit headroom: below the 3.4 GB file+private total, but above the 0.9 GB private part
        // plus the safety margin, so the mapped weights must not be charged against commit.
        SystemRamSnapshot ram = SystemRamSnapshot.TryCreate(16 * Gib, (ulong)(3.2 * Gib), (ulong)(1.6 * Gib), Gib)!;
        Assert.True(EvaluateCorporate(ram, 86, 0, PrivateMib, MappedMib).Allowed);
        // The same total counted as private (for example --no-mmap) would be refused.
        Assert.Equal(CorporateAdmissionOutcome.RefuseCommit,
            EvaluateCorporate(ram, 86, 0, PrivateMib + MappedMib, 0).Outcome);
    }

    [Fact]
    public void ResidentPaRuXModel_CountsAsCommitHeadroomWhenItIsReplaced()
    {
        SystemRamSnapshot ram = SystemRamSnapshot.TryCreate(16 * Gib, 6 * Gib, (ulong)(0.7 * Gib), Gib)!;
        Assert.Equal(CorporateAdmissionOutcome.RefuseCommit, EvaluateCorporate(ram, 86, 0, PrivateMib, MappedMib).Outcome);
        Assert.True(EvaluateCorporate(ram, 86, 3L * (long)Gib, PrivateMib, MappedMib).Allowed);
    }

    [Fact]
    public void StrictProfileCeiling_IsStillHonored()
    {
        // A 70% ceiling on a machine already at 80%: the owner/IT limit wins over the dynamic path, even with huge commit.
        CorporateAdmission verdict = EvaluateCorporate(Busy16Gb(), 70, 0, PrivateMib, MappedMib);
        Assert.Equal(CorporateAdmissionOutcome.RefuseCeiling, verdict.Outcome);
        Assert.Equal(CalculateAvailableSystemRamBytes(Busy16Gb(), 70), CalculateCorporateAdmissibleBytes(Busy16Gb(), 70));
        // And it still admits when it genuinely fits under the ceiling.
        SystemRamSnapshot idle = SystemRamSnapshot.TryCreate(16 * Gib, 12 * Gib, 20 * Gib, Gib)!;
        Assert.Equal(CorporateAdmissionOutcome.Admit, EvaluateCorporate(idle, 70, 0, PrivateMib, MappedMib).Outcome);
    }

    [Fact]
    public void MachineThatCannotHoldTheWorkingSet_IsRefusedPhysically()
    {
        // 8 GB PC at 95% used: even trimming the conservative share of other programs cannot hold 3.4 GB.
        SystemRamSnapshot ram = SystemRamSnapshot.TryCreate(8 * Gib, (ulong)(0.4 * Gib), 20 * Gib, Gib)!;
        Assert.Equal(CorporateAdmissionOutcome.RefusePhysical, EvaluateCorporate(ram, 86, 0, PrivateMib, MappedMib).Outcome);
    }

    [Fact]
    public void SnapshotWithoutCommitInfo_KeepsTheClassicVerdict()
    {
        // Linux /proc/meminfo (MemAvailable already includes reclaimable cache) and a failed Windows probe.
        SystemRamSnapshot linux = SystemRamSnapshot.TryCreate(16 * Gib, (ulong)(3.2 * Gib))!;
        Assert.False(linux.HasCommitInfo);
        Assert.Equal(CalculateCorporateAvailableBytes(linux, 86), CalculateCorporateAdmissibleBytes(linux, 86));
        Assert.Equal(CorporateAdmissionOutcome.RefusePhysical, EvaluateCorporate(linux, 86, 0, PrivateMib, MappedMib).Outcome);
        SystemRamSnapshot roomy = SystemRamSnapshot.TryCreate(16 * Gib, 8 * Gib)!;
        Assert.Equal(CorporateAdmissionOutcome.Admit, EvaluateCorporate(roomy, 86, 0, PrivateMib, MappedMib).Outcome);
    }

    [Fact]
    public void UnmeasurableMemory_FailsClosed()
    {
        Assert.False(EvaluateCorporate(null, 86, 0, 1, 1).Allowed);
        Assert.False(EvaluateCorporate(Busy16Gb(), double.NaN, 0, 1, 1).Allowed);
    }

    [Fact]
    public void AdmissibleBytes_ExceedsTheComfortableFigure_OnABusyPc()
    {
        long comfortable = CalculateCorporateAvailableBytes(Busy16Gb(), 86);
        long admissible = CalculateCorporateAdmissibleBytes(Busy16Gb(), 86);
        Assert.True(admissible > comfortable);
        Assert.True(admissible <= 16L * (long)Gib);
    }

    [Fact]
    public void CpuEngineStillMemoryMapsTheWeights()
    {
        // The admission treats the GGUF as file-backed; if the launch ever pins or copies it, this must be revisited.
        List<string> args = LocalAiProcessManager.BuildCorporateCpuArguments("m.gguf", 1234, 8);
        Assert.DoesNotContain("--no-mmap", args);
        Assert.DoesNotContain("--mlock", args);
        Assert.DoesNotContain("--load-mode", args);
        Assert.Contains("--no-repack", args);
    }

    [Fact]
    public void WindowsSnapshot_CarriesCommitHeadroom()
    {
        if (!OperatingSystem.IsWindows()) return;
        SystemRamSnapshot? live = ExecutionResourceGuard.TryGetSystemRamSnapshot();
        Assert.NotNull(live);
        Assert.True(live!.HasCommitInfo);
        Assert.True(live.CommitAvailableBytes > 0);
    }
}
