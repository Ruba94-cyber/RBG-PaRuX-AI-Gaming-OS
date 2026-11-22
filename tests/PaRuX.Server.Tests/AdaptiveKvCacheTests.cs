using PaRuX.Contracts;
using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The default KV cache is q8_0. On a CUDA card with ample live free VRAM the launch plan raises it to f16 (measured on an
/// RTX 5080 with Qwen3-4B Q4_K_M: f16 decodes 4-8% faster, +1.1 GB VRAM). Anything tight, user-chosen, measured or
/// non-CUDA keeps q8_0, and the pre-plan baseline the launch ladder falls back to always stays q8_0.
/// </summary>
public sealed class AdaptiveKvCacheTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static GgufModelInfo Qwen3Dense4BQ4() => new()
    {
        FileName = "Qwen3-4B-Q4_K_M.gguf", Architecture = "qwen3", ParsedSuccessfully = true,
        SizeBytes = 2_497_280_256, ParameterCount = 4_020_000_000, ContextLength = 40960,
        BlockCount = 36, EmbeddingLength = 2560, AttentionHeadCount = 32, KeyValueHeadCount = 8, KeyLength = 128, ValueLength = 128
    };

    private static LaunchHardware Card(long free, long total = 16 * GiB, string backend = "text-cuda") => new(
        AcceleratorEligible: true, Backend: backend, SharedMemoryPrimary: false,
        GpuMemoryBytes: free, SystemRamTotalBytes: 32 * GiB, SystemRamAvailableBytes: 24 * GiB,
        PhysicalCores: 8, LogicalCores: 16, GpuDedicatedTotalBytes: total);

    private static LaunchPlan Plan(GgufModelInfo model, LaunchHardware hw, LlamaPerformanceProfileDto baseline, bool adaptive = true) =>
        LaunchPlanner.Plan(new LaunchPlanInput(model, hw, LaunchPlannerFixtures.B10549, baseline, adaptive));

    [Fact]
    public void A_small_model_on_a_mostly_free_16_gb_card_gets_f16_and_the_baseline_stays_q8_0()
    {
        LlamaPerformanceProfileDto baseline = LaunchPlannerFixtures.Baseline(context: 16384);
        LaunchPlan plan = Plan(Qwen3Dense4BQ4(), Card(free: 15 * GiB), baseline);

        Assert.True(plan.Applied);
        Assert.Equal("f16", plan.Profile.CacheTypeK);
        Assert.Equal("f16", plan.Profile.CacheTypeV);
        Assert.Equal(16384, plan.Profile.ContextSize);
        Assert.Equal("q8_0", baseline.CacheTypeK); // the ladder's known-good fallback is untouched
        Assert.Contains(plan.Decisions, d => d.Flag == "--cache-type-k/--cache-type-v" && d.Value == "f16" && d.Reason.Contains("raised to f16"));
        List<string> args = LaunchPlannerFixtures.Arguments(plan, hasCuda: true);
        Assert.Equal("f16", LaunchPlannerFixtures.ValueOf(args, "--cache-type-k"));
        Assert.Equal("f16", LaunchPlannerFixtures.ValueOf(args, "--cache-type-v"));
    }

    [Fact]
    public void The_switch_is_off_unless_the_caller_allows_it()
    {
        LaunchPlan plan = Plan(Qwen3Dense4BQ4(), Card(free: 15 * GiB), LaunchPlannerFixtures.Baseline(context: 16384), adaptive: false);
        Assert.Equal("q8_0", plan.Profile.CacheTypeK);
    }

    [Theory]
    [InlineData(6)]   // another resident (a game, a second model) holds most of the card
    [InlineData(8)]
    public void Tight_free_vram_keeps_q8_0(long freeGiB)
    {
        LaunchPlan plan = Plan(Qwen3Dense4BQ4(), Card(free: freeGiB * GiB), LaunchPlannerFixtures.Baseline(context: 16384));
        Assert.Equal("q8_0", plan.Profile.CacheTypeK);
    }

    [Fact]
    public void A_big_model_keeps_the_existing_quantized_ladder()
    {
        LaunchPlan plan = Plan(LaunchPlannerFixtures.Dense32BQ4(), Card(free: 15 * GiB), LaunchPlannerFixtures.Baseline(context: 32768));
        Assert.NotEqual("f16", plan.Profile.CacheTypeK);
    }

    [Fact]
    public void A_small_card_never_spends_more_than_its_share_even_when_it_is_empty()
    {
        // 8 GiB card, all free: weights + f16 KV + reserve for a 128k window exceed 60% of the card.
        LaunchPlan plan = Plan(Qwen3Dense4BQ4(), Card(free: 8 * GiB, total: 8 * GiB), LaunchPlannerFixtures.Baseline(context: 131072));
        Assert.NotEqual("f16", plan.Profile.CacheTypeK);
    }

    [Fact]
    public void An_explicit_user_context_and_cache_choice_is_never_overridden()
    {
        LlamaPerformanceProfileDto pinned = LaunchPlannerFixtures.Baseline(context: 16384, contextPinned: true, cache: "q8_0");
        LaunchPlan plan = Plan(Qwen3Dense4BQ4(), Card(free: 15 * GiB), pinned);
        Assert.Equal("q8_0", plan.Profile.CacheTypeK);
        Assert.Equal("q8_0", plan.Profile.CacheTypeV);
    }

    [Fact]
    public void A_user_chosen_f16_or_q4_cache_is_left_alone()
    {
        Assert.Equal("q4_0", Plan(Qwen3Dense4BQ4(), Card(free: 15 * GiB), LaunchPlannerFixtures.Baseline(context: 16384, cache: "q4_0")).Profile.CacheTypeK);
        Assert.Equal("f16", Plan(Qwen3Dense4BQ4(), Card(free: 15 * GiB), LaunchPlannerFixtures.Baseline(context: 16384, cache: "f16")).Profile.CacheTypeK);
    }

    [Fact]
    public void Vulkan_and_unknown_total_vram_keep_q8_0()
    {
        Assert.Equal("q8_0", Plan(Qwen3Dense4BQ4(), Card(free: 15 * GiB, backend: "text-vulkan"), LaunchPlannerFixtures.Baseline(context: 16384)).Profile.CacheTypeK);
        Assert.Equal("q8_0", Plan(Qwen3Dense4BQ4(), Card(free: 15 * GiB, total: 0), LaunchPlannerFixtures.Baseline(context: 16384)).Profile.CacheTypeK);
    }

    [Fact]
    public void An_integrated_gpu_with_shared_memory_keeps_q8_0()
    {
        LaunchPlan plan = Plan(Qwen3Dense4BQ4(), LaunchPlannerFixtures.Igpu780M32Gb(), LaunchPlannerFixtures.Baseline(context: 16384));
        Assert.Equal("q8_0", plan.Profile.CacheTypeK);
    }
}
