using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class FirstModelRecommendationPresenterTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static FirstModelAdvice Advice(string suitability, int rank = 3, string? runtimeState = null, long vram = 16 * Gb, long ram = 32 * Gb)
    {
        var candidate = new FirstModelCandidate
        {
            Model = new RecommendedModel { Id = "m1", Title = "Starter 8B" },
            Suitability = suitability,
            Reason = "raw core reason",
            Rank = rank,
            RuntimeRequirementState = runtimeState
        };
        return new FirstModelAdvice { RecommendedId = "m1", Candidates = new() { candidate }, VramBytes = vram, RamBytes = ram };
    }

    [Fact]
    public void Recommended_candidate_is_preselected_with_a_plain_reason_in_both_languages()
    {
        FirstModelAdvice advice = Advice("FitsOnGpu");
        FirstModelCandidate? pick = FirstModelRecommendationPresenter.PickRecommended(advice);
        Assert.NotNull(pick);
        Assert.Equal("Recommended for your PC: Starter 8B. Fits your 16 GB graphics card, so replies are fast.",
            FirstModelRecommendationPresenter.Headline(pick!, advice, italian: false));
        Assert.Equal("Consigliato per il tuo PC: Starter 8B. Sta nella tua scheda video da 16 GB, quindi risponde in fretta.",
            FirstModelRecommendationPresenter.Headline(pick!, advice, italian: true));
    }

    [Theory]
    [InlineData("TooLarge", 3, null)]
    [InlineData("FitsOnGpu", 0, null)]
    [InlineData("FitsOnGpu", 3, "NeedsRuntimeUpdate")]
    public void Nothing_is_preselected_when_the_pick_cannot_run_here(string suitability, int rank, string? runtimeState)
    {
        Assert.Null(FirstModelRecommendationPresenter.PickRecommended(Advice(suitability, rank, runtimeState)));
    }

    [Fact]
    public void Cpu_only_and_split_reasons_say_replies_are_slower_and_unknown_hardware_falls_back_to_the_core_reason()
    {
        FirstModelAdvice cpu = Advice("CpuOnly", vram: 0);
        Assert.Contains("slower", FirstModelRecommendationPresenter.Reason(cpu.Candidates[0], cpu, italian: false));
        Assert.Contains("più lente", FirstModelRecommendationPresenter.Reason(cpu.Candidates[0], cpu, italian: true));
        FirstModelAdvice unknown = Advice("FitsOnGpu", vram: 0, ram: 0);
        Assert.Equal("raw core reason", FirstModelRecommendationPresenter.Reason(unknown.Candidates[0], unknown, italian: false));
    }
}
