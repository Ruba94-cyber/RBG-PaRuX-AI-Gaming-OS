using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real-PC finding (2026-11-24): on a 16 GB corporate PC with Teams, Outlook and a browser at 65-70% memory use the local
/// model was refused with "not enough RAM" although 5 GB were physically available, and the refusal named no numbers.
/// Corporate now admits by what Windows reports available minus a reserve that scales with the machine.
/// </summary>
public sealed class CorporateDynamicMemoryTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    // Roughly a Qwen3-4B Q4_K_M on the bundled CPU engine: weights + KV + runtime reserve.
    private static double Estimate(int context) => context switch
    {
        4096 => 3300,
        3072 => 3000,
        2048 => 1950,
        _ => throw new ArgumentOutOfRangeException(nameof(context))
    };

    [Theory]
    [InlineData(5.6, 4096)]   // 16 GB PC at 65% use (the reported case)
    [InlineData(4.8, 4096)]   // Teams spike: 70% use
    [InlineData(4.3, 3072)]   // 73% use: smaller window, still answers
    [InlineData(3.3, 2048)]   // 79% use: minimum useful window
    public void A_16_gb_pc_with_the_usual_corporate_applications_still_starts_the_model(double availableGiB, int expectedContext)
    {
        var ram = new SystemRamSnapshot(16 * GiB, (ulong)(availableGiB * GiB));
        var plan = CorporateCpuMemoryPlanner.Select(ram, 86, 0, Estimate);
        Assert.True(plan.Fits, "needs " + plan.NeededMib + " MiB, available " + plan.AvailableMib + " MiB");
        Assert.Equal(expectedContext, plan.ContextTokens);
    }

    [Fact]
    public void The_reserve_for_windows_and_the_other_programs_scales_with_the_machine_between_one_and_one_and_a_half_gib()
    {
        Assert.Equal(1L * 1024 * 1024 * 1024, RuntimeMemoryBudget.CorporateReserveBytes(new SystemRamSnapshot(8 * GiB, 3 * GiB)));
        Assert.Equal((long)(16 * GiB * 0.08), RuntimeMemoryBudget.CorporateReserveBytes(new SystemRamSnapshot(16 * GiB, 5 * GiB)));
        Assert.Equal(3L * 512 * 1024 * 1024, RuntimeMemoryBudget.CorporateReserveBytes(new SystemRamSnapshot(64 * GiB, 20 * GiB)));
    }

    [Fact]
    public void The_model_is_still_refused_when_the_pc_is_really_out_of_memory_and_the_message_gives_the_numbers()
    {
        var ram = new SystemRamSnapshot(16 * GiB, (ulong)(2.4 * GiB)); // 85% in use
        var plan = CorporateCpuMemoryPlanner.Select(ram, 86, 0, Estimate);
        Assert.False(plan.Fits);

        string message = CorporateCpuMemoryPlanner.Shortfall(plan, ram, 86);
        Assert.Contains("needs about", message);
        Assert.Contains("PaRuX can use about", message);
        Assert.Contains("Windows reports 2.4 GB free", message);
        Assert.Contains("kept free for Windows and your other programs", message);
        Assert.Matches(@"Free about \d+\.\d GB more", message);
        Assert.Contains("did not close any program", message);
    }

    [Fact]
    public void An_explicitly_stricter_profile_ceiling_is_still_honored()
    {
        var ram = new SystemRamSnapshot(16 * GiB, (ulong)(5.6 * GiB));          // 65% in use
        var plan = CorporateCpuMemoryPlanner.Select(ram, 70, 0, Estimate);       // IT chose a 70% ceiling
        Assert.False(plan.Fits);
        Assert.Contains("caps total system RAM use at 70%", CorporateCpuMemoryPlanner.Shortfall(plan, ram, 70));
    }

    [Fact]
    public void The_dynamic_rule_never_admits_more_than_windows_reports_available_plus_the_owned_model()
    {
        var ram = new SystemRamSnapshot(16 * GiB, 2 * GiB);
        long withReclaim = RuntimeMemoryBudget.CalculateCorporateAvailableBytes(ram, 100, (long)(1 * GiB));
        Assert.True(withReclaim <= (long)(3 * GiB));
        Assert.True(withReclaim >= 0);
        Assert.Equal(0, RuntimeMemoryBudget.CalculateCorporateAvailableBytes(null, 86));
    }
}
