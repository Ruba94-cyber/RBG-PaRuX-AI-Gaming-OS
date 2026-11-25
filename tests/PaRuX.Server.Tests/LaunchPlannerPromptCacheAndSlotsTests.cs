using PaRuX.Contracts;
using PaRuX.Intelligence;
using Xunit;
using static PaRuX.Server.Tests.LaunchPlannerFixtures;

namespace PaRuX.Server.Tests;

/// <summary>
/// Host-RAM prompt cache (--cache-ram) and adaptive pooled parallel slots in the launch planner. Pure planning over fake
/// hardware and fake GGUF headers: what is pinned here is which flags a launch gets and why, never a speed. The measured
/// evidence behind both decisions (engine and Core level, prompt reuse across slots, unchanged greedy answers with
/// reasoning history, no KV oversubscription) is in docs/validation/COMPETITOR_BENCH_2026-11-25.md and
/// tools/competitor-bench/slots_cache.py.
/// </summary>
public sealed class LaunchPlannerPromptCacheAndSlotsTests
{
    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>The build the bench PC ships (b11871): its --help lists --cache-ram, --kv-unified and --kv-unified-per-slot.</summary>
    private static readonly LlamaServerCapabilities B11871 = LlamaServerCapabilities.FromHelpText("""
        -ncmoe, --n-cpu-moe N
        -ot,   --override-tensor <tensor name pattern>=<buffer type>,...
        -lm,   --load-mode MODE
        --kv-unified-per-slot N                 context limit per parallel slot (default: unset, behavior unchanged).
        -cram, --cache-ram N                    set the maximum cache size in MiB (default: 8192, 0 for decision
        -kvu,  --kv-unified, -no-kvu, --no-kv-unified
        """);

    /// <summary>RTX 5080 16 GB as LaunchHardware.From reports it: live free VRAM plus the dedicated total.</summary>
    private static LaunchHardware Rtx5080(long freeBytes = 15 * GiB + GiB / 2) => new(
        AcceleratorEligible: true, Backend: "text-cuda", SharedMemoryPrimary: false,
        GpuMemoryBytes: freeBytes, SystemRamTotalBytes: 26 * GiB, SystemRamAvailableBytes: 12 * GiB,
        PhysicalCores: 8, LogicalCores: 16, GpuDedicatedTotalBytes: 16 * GiB);

    /// <summary>Qwen3-4B Q4_K_M geometry (36 blocks, 8 KV heads x 128): the model of the bench.</summary>
    private static GgufModelInfo Qwen3Dense4B() => new()
    {
        FileName = "Qwen3-4B-Q4_K_M.gguf", Architecture = "qwen3", ParsedSuccessfully = true,
        SizeBytes = 2_497_280_256, ParameterCount = 4_020_000_000, ContextLength = 40960,
        BlockCount = 36, EmbeddingLength = 2560, AttentionHeadCount = 32, KeyValueHeadCount = 8, KeyLength = 128, ValueLength = 128
    };

    private static LlamaPerformanceProfileDto ChatBaseline(int slots = 2, string cache = "f16")
    {
        LlamaPerformanceProfileDto baseline = Baseline(context: 16384, cache: cache);
        baseline.ParallelSlots = slots; // RecommendParallelSlots: 2 for chat on a large card, 1 for coding/long-context
        return baseline;
    }

    // ---------------------------------------------------------------- --cache-ram

    [Fact]
    public void A_fully_offloaded_CUDA_launch_turns_the_host_prompt_cache_off_and_says_why()
    {
        LaunchPlan plan = Plan(Dense8BQ4(), Rtx5080Egpu32Gb(), B11871);

        Assert.Equal(0, plan.Profile.HostPromptCacheMib);
        List<string> args = Arguments(plan, hasCuda: true);
        Assert.Equal("0", ValueOf(args, "--cache-ram"));
        LaunchPlanDecision decision = Assert.Single(plan.Decisions, d => d.Flag == "--cache-ram");
        Assert.Contains("CUDA GPU", decision.Reason);
    }

    [Fact]
    public void Partial_offload_shared_memory_and_CPU_launches_keep_the_engine_default_cache()
    {
        foreach ((GgufModelInfo model, LaunchHardware hw, bool cuda) in new[]
                 {
                     (Dense32BQ4(), Rtx5080Egpu32Gb(), true), // layers on the CPU: slow prefill, the cache pays
                     (Dense8BQ4(), Igpu780M32Gb(), false),
                     (Dense8BQ4(), CpuOnly16Gb(), false)
                 })
        {
            LaunchPlan plan = Plan(model, hw, B11871);
            Assert.Equal(-1, plan.Profile.HostPromptCacheMib);
            Assert.DoesNotContain("--cache-ram", Arguments(plan, cuda));
        }
    }

    [Fact]
    public void A_runtime_that_does_not_list_the_flag_never_gets_it()
    {
        var noCacheFlag = new LlamaServerCapabilities(NCpuMoe: true, OverrideTensor: true, LoadMode: true, Source: "probed-help");

        LaunchPlan plan = Plan(Dense8BQ4(), Rtx5080Egpu32Gb(), noCacheFlag);

        Assert.Equal(-1, plan.Profile.HostPromptCacheMib);
        Assert.DoesNotContain("--cache-ram", Arguments(plan, hasCuda: true));
        Assert.DoesNotContain(plan.Decisions, d => d.Flag == "--cache-ram");
    }

    [Fact]
    public void Capabilities_report_the_cache_and_unified_kv_flags_only_when_the_help_lists_them()
    {
        Assert.True(B11871 is { CacheRam: true, KvUnified: true, KvUnifiedPerSlot: true });
        LlamaServerCapabilities perSlotOnly = LlamaServerCapabilities.FromHelpText("--kv-unified-per-slot N\n");
        Assert.False(perSlotOnly.KvUnified); // "--kv-unified-per-slot" must not be read as "--kv-unified"
        Assert.True(perSlotOnly.KvUnifiedPerSlot);
        LlamaServerCapabilities b10549 = LlamaServerCapabilities.FromBuild(10549);
        Assert.True(b10549 is { CacheRam: true, KvUnified: true, KvUnifiedPerSlot: false });
        Assert.False(LlamaServerCapabilities.Unverified.CacheRam);
    }

    // ---------------------------------------------------------------- pooled parallel slots

    [Fact]
    public void A_chat_launch_with_ample_free_VRAM_gets_two_slots_sharing_a_pool_of_two_full_windows()
    {
        LaunchPlan plan = Plan(Qwen3Dense4B(), Rtx5080(), B11871, ChatBaseline());

        Assert.Equal(2, plan.Profile.ParallelSlots);
        Assert.Equal(16384, plan.Profile.UnifiedKvPerSlotTokens);
        List<string> args = Arguments(plan, hasCuda: true);
        Assert.Equal("32768", ValueOf(args, "--ctx-size"));
        Assert.Equal("2", ValueOf(args, "--parallel"));
        Assert.Contains("--kv-unified", args);
        Assert.Equal("16384", ValueOf(args, "--kv-unified-per-slot"));
        // Each request keeps its whole window: the pool is exactly slots x window, so it cannot be oversubscribed.
        (int total, int slots) = RuntimeLaunchOptimizer.ResolveLaunchContextAndSlots(plan.Profile);
        Assert.Equal(total, slots * plan.Profile.UnifiedKvPerSlotTokens);
        Assert.Contains(plan.Decisions, d => d.Flag == "--parallel" && d.Value == "2" && d.Reason.Contains("whole window"));
    }

    [Fact]
    public void The_slot_count_steps_down_until_the_pool_fits_and_falls_back_to_one_slot()
    {
        // 4 wanted with q8_0: four 16k windows of a 4B fit a free 16 GB card.
        LaunchPlan four = Plan(Qwen3Dense4B(), Rtx5080(), B11871, ChatBaseline(slots: 4, cache: "q8_0"));
        Assert.Equal(4, four.Profile.ParallelSlots);

        // Only about 6.5 GB free (a game or another model holds the rest): no second window fits with 2 GB headroom.
        LaunchPlan tight = Plan(Qwen3Dense4B(), Rtx5080(freeBytes: 6 * GiB + GiB / 2), B11871, ChatBaseline());
        Assert.Equal(0, tight.Profile.UnifiedKvPerSlotTokens);
        Assert.DoesNotContain("--kv-unified", Arguments(tight, hasCuda: true));
        Assert.Equal("1", ValueOf(Arguments(tight, hasCuda: true), "--parallel"));
        Assert.Contains(tight.Decisions, d => d.Flag == "--parallel" && d.Value == "1" && d.Reason.Contains("does not fit"));
    }

    [Fact]
    public void Coding_and_long_context_single_slot_launches_are_left_alone()
    {
        LaunchPlan plan = Plan(Qwen3Dense4B(), Rtx5080(), B11871, ChatBaseline(slots: 1));

        Assert.Equal(1, plan.Profile.ParallelSlots);
        Assert.Equal(0, plan.Profile.UnifiedKvPerSlotTokens);
        Assert.DoesNotContain("--kv-unified", Arguments(plan, hasCuda: true));
    }

    [Fact]
    public void Pooled_slots_need_the_per_slot_cap_a_dedicated_CUDA_card_and_a_fully_offloaded_model()
    {
        // b10549 lists --kv-unified but not --kv-unified-per-slot: an uncapped shared pool can be oversubscribed (HTTP 500).
        Assert.Equal(0, Plan(Qwen3Dense4B(), Rtx5080(), B10549, ChatBaseline()).Profile.UnifiedKvPerSlotTokens);
        // Shared-memory iGPU and CPU-only never get extra slots.
        Assert.Equal(0, Plan(Qwen3Dense4B(), Igpu780M32Gb(), B11871, ChatBaseline()).Profile.UnifiedKvPerSlotTokens);
        Assert.Equal(0, Plan(Qwen3Dense4B(), CpuOnly16Gb(), B11871, ChatBaseline()).Profile.UnifiedKvPerSlotTokens);
        // A model that spills layers to the CPU keeps one slot.
        LaunchPlan spilled = Plan(Dense32BQ4(), Rtx5080(), B11871, ChatBaseline(cache: "q8_0"));
        Assert.Equal(0, spilled.Profile.UnifiedKvPerSlotTokens);
    }

    [Fact]
    public void A_pooled_launch_has_a_valid_launch_descriptor_and_classic_launches_are_unchanged()
    {
        LaunchPlan plan = Plan(Qwen3Dense4B(), Rtx5080(), B11871, ChatBaseline());
        var args = new List<string> { "--model", "C:\\m\\Qwen3-4B-Q4_K_M.gguf", "--alias", "local-coder", "--host", "127.0.0.1", "--port", "1234" };
        args.AddRange(Arguments(plan, hasCuda: true));

        TextLaunchDescriptorResult described = TextLaunchDescriptor.Create("text-cuda", args, new Dictionary<string, string>());
        Assert.Null(described.Reason);

        LlamaPerformanceProfileDto classic = ChatBaseline(slots: 1);
        Assert.Equal(RuntimeLaunchOptimizer.ResolveContextAndSlots(classic.ContextSize, classic.ParallelSlots, classic.ContextIsPerRequest),
            RuntimeLaunchOptimizer.ResolveLaunchContextAndSlots(classic));
    }

    [Fact]
    public void Launch_only_knobs_survive_the_profile_copy()
    {
        LlamaPerformanceProfileDto source = ChatBaseline();
        source.HostPromptCacheMib = 0;
        source.UnifiedKvPerSlotTokens = 16384;

        LlamaPerformanceProfileDto copy = RuntimePerformanceProfileCopy.Copy(source);

        Assert.Equal(0, copy.HostPromptCacheMib);
        Assert.Equal(16384, copy.UnifiedKvPerSlotTokens);
    }
}
