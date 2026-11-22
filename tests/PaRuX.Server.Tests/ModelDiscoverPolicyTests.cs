using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDiscoverPolicyTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static DiscoverEntry E(string id, DiscoverTask task = DiscoverTask.Chat, DiscoverFit fit = DiscoverFit.FitsGpu,
        long size = 4 * GiB, bool curated = true, bool installed = false, bool online = false, string repo = "", string quant = "Q4_K_M") =>
        new(id, id, "desc " + id, string.IsNullOrEmpty(repo) ? "org/" + id : repo, task, curated, size, quant, "7B", 32768, fit, null, false, installed, online);

    [Theory]
    [InlineData("coding", null, "standard", DiscoverTask.Coding)]
    [InlineData("general", new[] { "image.generate" }, "standard", DiscoverTask.Image)]
    [InlineData("general", new[] { "video.generate" }, "standard", DiscoverTask.Video)]
    [InlineData("general", new[] { "vision" }, "standard", DiscoverTask.Vision)]
    [InlineData("general", new[] { "chat.reasoning" }, "standard", DiscoverTask.Reasoning)]
    [InlineData("embedding", null, "standard", DiscoverTask.Embeddings)]
    [InlineData("reranker", null, "standard", DiscoverTask.Embeddings)]
    [InlineData("abliterated", null, "standard", DiscoverTask.Uncensored)]
    [InlineData("uncensored-roleplay", null, "standard", DiscoverTask.Uncensored)]
    [InlineData("coding", new[] { "coding" }, "uncensored", DiscoverTask.Uncensored)]
    [InlineData("audio", null, "standard", DiscoverTask.More)]
    [InlineData("", null, null, DiscoverTask.Chat)]
    public void Classify_uses_metadata_with_unfiltered_winning(string category, string[]? caps, string? profile, DiscoverTask expected) =>
        Assert.Equal(expected, ModelDiscoverPolicy.Classify(category, caps, profile));

    [Fact]
    public void Capability_tags_beat_the_category_word() =>
        Assert.Equal(DiscoverTask.Coding, ModelDiscoverPolicy.Classify("vision", new[] { "coding" }, "standard"));

    [Theory]
    [InlineData(ModelHardwareFitTier.ExcellentFullGpu, true, DiscoverFit.RunsFast)]
    [InlineData(ModelHardwareFitTier.FullGpuLikely, true, DiscoverFit.FitsGpu)]
    [InlineData(ModelHardwareFitTier.PartialOffload, true, DiscoverFit.NeedsOffload)]
    [InlineData(ModelHardwareFitTier.PartialOffload, false, DiscoverFit.CpuOnly)]
    [InlineData(ModelHardwareFitTier.NotRecommended, true, DiscoverFit.TooBig)]
    [InlineData(ModelHardwareFitTier.Unknown, true, DiscoverFit.Unknown)]
    public void Fit_badge_follows_the_shared_estimator_tier(ModelHardwareFitTier tier, bool gpu, DiscoverFit expected) =>
        Assert.Equal(expected, ModelDiscoverPolicy.FitFromEstimate(new ModelHardwareFitEstimate { Tier = tier }, gpu));

    [Fact]
    public void Missing_estimate_is_unknown_not_a_guess() =>
        Assert.Equal(DiscoverFit.Unknown, ModelDiscoverPolicy.FitFromEstimate(null, true));

    [Fact]
    public void Real_estimator_on_a_16GB_card_says_small_model_fast_and_huge_model_too_big()
    {
        var hw = new RuntimeManagerHardwareStatus
        {
            GpuDetected = true, VramTotalMib = 16384, RamTotalBytes = 32 * GiB, GpuInventoryAvailable = true,
            GpuAdapters = new List<GpuAdapterStatus> { new() { Index = 0, Name = "Test GPU", DedicatedVideoMemoryBytes = 16 * GiB } }
        };
        Assert.Equal(DiscoverFit.RunsFast,
            ModelDiscoverPolicy.FitFromEstimate(ModelHardwareFitEstimator.Estimate(2 * GiB, hw), true));
        Assert.Equal(DiscoverFit.TooBig,
            ModelDiscoverPolicy.FitFromEstimate(ModelHardwareFitEstimator.Estimate(200 * GiB, hw), true));
    }

    [Fact]
    public void Speed_prefers_the_measured_value_of_the_file()
    {
        var (tps, measured) = ModelDiscoverPolicy.SpeedFor(4 * GiB, DiscoverFit.RunsFast, 150, new[] { new DiscoverBenchmarkPoint(2 * GiB, 300) });
        Assert.Equal(150, tps);
        Assert.True(measured);
    }

    [Fact]
    public void Speed_is_scaled_from_the_nearest_benchmark_for_gpu_resident_models_only()
    {
        var bench = new[] { new DiscoverBenchmarkPoint(2 * GiB, 200), new DiscoverBenchmarkPoint(16 * GiB, 40) };
        var (tps, measured) = ModelDiscoverPolicy.SpeedFor(4 * GiB, DiscoverFit.FitsGpu, null, bench);
        Assert.False(measured);
        Assert.Equal(100, tps);
        Assert.Null(ModelDiscoverPolicy.SpeedFor(4 * GiB, DiscoverFit.NeedsOffload, null, bench).TokensPerSecond);
        Assert.Null(ModelDiscoverPolicy.SpeedFor(4 * GiB, DiscoverFit.RunsFast, null, null).TokensPerSecond);
        Assert.Null(ModelDiscoverPolicy.SpeedFor(0, DiscoverFit.RunsFast, null, bench).TokensPerSecond);
    }

    [Fact]
    public void Speed_text_marks_estimates_and_is_empty_without_a_number()
    {
        Assert.StartsWith("~", ModelDiscoverPolicy.SpeedText(50, false, false));
        Assert.Contains("measured", ModelDiscoverPolicy.SpeedText(50, true, false));
        Assert.Contains("misurati", ModelDiscoverPolicy.SpeedText(50, true, true));
        Assert.Equal(string.Empty, ModelDiscoverPolicy.SpeedText(null, false, false));
    }

    [Fact]
    public void Rank_orders_by_fit_then_curated_then_size_then_title()
    {
        var ranked = ModelDiscoverPolicy.Rank(new[]
        {
            E("toobig", fit: DiscoverFit.TooBig, size: 80 * GiB),
            E("offload", fit: DiscoverFit.NeedsOffload),
            E("fast-small", fit: DiscoverFit.RunsFast, size: 2 * GiB),
            E("fast-big", fit: DiscoverFit.RunsFast, size: 8 * GiB),
            E("fast-online", fit: DiscoverFit.RunsFast, size: 8 * GiB, curated: false),
        }).Select(e => e.Id).ToArray();
        Assert.Equal(new[] { "fast-big", "fast-small", "fast-online", "offload", "toobig" }, ranked);
    }

    [Fact]
    public void Group_follows_task_order_skips_empty_groups_and_dedups_repo_quant()
    {
        var groups = ModelDiscoverPolicy.Group(new[]
        {
            E("img", DiscoverTask.Image), E("chat1"), E("code", DiscoverTask.Coding),
            E("dup-online", curated: false, repo: "org/chat1", quant: "Q4_K_M"),
        });
        Assert.Equal(new[] { DiscoverTask.Chat, DiscoverTask.Coding, DiscoverTask.Image }, groups.Select(g => g.Task).ToArray());
        Assert.Single(groups[0].Entries);
        Assert.True(groups[0].Entries[0].Curated);
        Assert.Single(ModelDiscoverPolicy.Group(new[] { E("a"), E("b", DiscoverTask.Video) }, DiscoverTask.Video));
    }

    [Fact]
    public void Search_matches_every_word_in_title_repo_quant_or_params()
    {
        var items = new[] { E("qwen3-coder", DiscoverTask.Coding), E("llama", quant: "Q8_0") };
        Assert.Single(ModelDiscoverPolicy.Search(items, "qwen coder"));
        Assert.Single(ModelDiscoverPolicy.Search(items, "q8_0"));
        Assert.Equal(2, ModelDiscoverPolicy.Search(items, "  ").Count);
        Assert.Empty(ModelDiscoverPolicy.Search(items, "zzz"));
    }

    [Fact]
    public void Recommendation_is_the_biggest_fast_curated_standard_chat_model_not_installed()
    {
        DiscoverEntry? pick = ModelDiscoverPolicy.RecommendForThisPc(new[]
        {
            E("small", fit: DiscoverFit.RunsFast, size: 2 * GiB),
            E("big", fit: DiscoverFit.RunsFast, size: 9 * GiB),
            E("installed", fit: DiscoverFit.RunsFast, size: 12 * GiB, installed: true),
            E("unfiltered", DiscoverTask.Uncensored, DiscoverFit.RunsFast, size: 13 * GiB),
            E("online", fit: DiscoverFit.RunsFast, size: 14 * GiB, curated: false),
            E("toobig", fit: DiscoverFit.TooBig, size: 90 * GiB),
        });
        Assert.Equal("big", pick!.Id);
    }

    [Fact]
    public void Recommendation_falls_back_to_the_smallest_runnable_and_is_null_when_nothing_runs()
    {
        Assert.Equal("tiny", ModelDiscoverPolicy.RecommendForThisPc(new[]
        {
            E("tiny", fit: DiscoverFit.CpuOnly, size: 1 * GiB), E("mid", fit: DiscoverFit.CpuOnly, size: 3 * GiB)
        })!.Id);
        Assert.Null(ModelDiscoverPolicy.RecommendForThisPc(new[] { E("x", fit: DiscoverFit.TooBig) }));
        Assert.Null(ModelDiscoverPolicy.RecommendForThisPc(null));
    }

    [Fact]
    public void Disk_check_covers_ok_tight_short_quota_and_unknown()
    {
        Assert.Equal(DiskVerdict.Ok, ModelDiscoverPolicy.CheckDisk(4 * GiB, 0, 100 * GiB, null).Verdict);
        Assert.Equal(DiskVerdict.Tight, ModelDiscoverPolicy.CheckDisk(4 * GiB, 0, 5 * GiB, null).Verdict);
        DiscoverDiskCheck short1 = ModelDiscoverPolicy.CheckDisk(10 * GiB, 0, 6 * GiB, null);
        Assert.Equal(DiskVerdict.NotEnoughSpace, short1.Verdict);
        Assert.Equal(4 * GiB, short1.ShortfallBytes);
        Assert.False(short1.CanStart);
        DiscoverDiskCheck quota = ModelDiscoverPolicy.CheckDisk(10 * GiB, 0, 500 * GiB, 3 * GiB);
        Assert.Equal(DiskVerdict.QuotaExceeded, quota.Verdict);
        Assert.Equal(7 * GiB, quota.ShortfallBytes);
        Assert.Equal(DiskVerdict.Unknown, ModelDiscoverPolicy.CheckDisk(10 * GiB, 0, null, null).Verdict);
        Assert.True(ModelDiscoverPolicy.CheckDisk(10 * GiB, 0, null, null).CanStart);
        Assert.Equal(DiskVerdict.Unknown, ModelDiscoverPolicy.CheckDisk(0, 0, 1, 1).Verdict);
    }

    [Fact]
    public void Resume_only_needs_the_remaining_bytes()
    {
        Assert.Equal(DiskVerdict.NotEnoughSpace, ModelDiscoverPolicy.CheckDisk(10 * GiB, 0, 2 * GiB, null).Verdict);
        DiscoverDiskCheck resumed = ModelDiscoverPolicy.CheckDisk(10 * GiB, 9 * GiB, 20 * GiB, null);
        Assert.Equal(DiskVerdict.Ok, resumed.Verdict);
        Assert.Equal(1 * GiB, resumed.RequiredBytes);
    }

    private static ModelDownloadStatus Job(string status, string file = "Qwen3-4B-Q4_K_M.gguf", long done = 1000, long total = 4000,
        string? failure = null, DateTime? updated = null) =>
        new() { Id = Guid.NewGuid().ToString("N"), Status = status, FileName = file, BytesDownloaded = done, TotalBytes = total,
            FailureClass = failure, UpdatedUtc = updated ?? DateTime.UtcNow };

    [Fact]
    public void Job_matching_uses_the_repo_stem_and_ignores_unrelated_files()
    {
        Assert.True(ModelDiscoverPolicy.JobBelongsToRepo("Qwen3-4B-Q4_K_M.gguf", "Qwen/Qwen3-4B-GGUF"));
        Assert.True(ModelDiscoverPolicy.JobBelongsToRepo("qwen3_4b-q8.gguf", "unsloth/Qwen3-4B-GGUF"));
        Assert.False(ModelDiscoverPolicy.JobBelongsToRepo("Llama-3-8B.gguf", "Qwen/Qwen3-4B-GGUF"));
        Assert.False(ModelDiscoverPolicy.JobBelongsToRepo("a.gguf", "x/a-GGUF")); // stem too short to be trusted
    }

    [Fact]
    public void Active_job_wins_then_newest_resumable_job_with_kept_bytes()
    {
        DateTime now = DateTime.UtcNow;
        var old = Job("failed", updated: now.AddMinutes(-10), failure: "transient");
        var recent = Job("paused", updated: now.AddMinutes(-1));
        var empty = Job("cancelled", done: 0, updated: now);
        Assert.Same(recent, ModelDiscoverPolicy.FindJobForFile(new[] { old, recent, empty }, "Qwen/Qwen3-4B-GGUF"));
        var active = Job("downloading", updated: now.AddMinutes(-30));
        Assert.Same(active, ModelDiscoverPolicy.FindJobForFile(new[] { old, recent, active }, "Qwen/Qwen3-4B-GGUF"));
        Assert.Null(ModelDiscoverPolicy.FindJobForFile(new[] { empty }, "Qwen/Qwen3-4B-GGUF"));
    }

    [Fact]
    public void Card_states_are_honest_and_carry_a_plain_fix()
    {
        DiscoverEntry entry = E("m");
        ModelDiscoverPolicy.CardState dl = ModelDiscoverPolicy.DescribeCard(entry, Job("downloading", done: 1000, total: 4000), true, null, false);
        Assert.Equal(ModelDiscoverPolicy.CardAction.Pause, dl.Primary);
        Assert.Contains("25%", dl.Headline);

        ModelDiscoverPolicy.CardState paused = ModelDiscoverPolicy.DescribeCard(entry, Job("paused"), true, null, false);
        Assert.Equal(ModelDiscoverPolicy.CardAction.Resume, paused.Primary);

        ModelDiscoverPolicy.CardState failedDisk = ModelDiscoverPolicy.DescribeCard(entry, Job("failed", failure: "disk"), true, null, false);
        Assert.True(failedDisk.IsProblem);
        Assert.Contains("Free up space", failedDisk.Fix);
        Assert.Equal(ModelDiscoverPolicy.CardAction.Download,
            ModelDiscoverPolicy.DescribeCard(entry, Job("failed", failure: "permanent"), true, null, false).Primary);

        Assert.Equal(ModelDiscoverPolicy.CardAction.Use,
            ModelDiscoverPolicy.DescribeCard(entry with { Installed = true }, null, true, null, false).Primary);
        ModelDiscoverPolicy.CardState offline = ModelDiscoverPolicy.DescribeCard(entry with { Online = true }, null, false, null, false);
        Assert.Equal(ModelDiscoverPolicy.CardAction.GoOnline, offline.Primary);
        Assert.Contains("Internet", offline.Fix);
        Assert.Equal(ModelDiscoverPolicy.CardAction.None,
            ModelDiscoverPolicy.DescribeCard(entry with { Fit = DiscoverFit.TooBig }, null, true, null, false).Primary);
        var noDisk = ModelDiscoverPolicy.CheckDisk(10 * GiB, 0, 1 * GiB, null);
        Assert.Equal(ModelDiscoverPolicy.CardAction.None, ModelDiscoverPolicy.DescribeCard(entry, null, true, noDisk, false).Primary);
        Assert.Equal(ModelDiscoverPolicy.CardAction.Download, ModelDiscoverPolicy.DescribeCard(entry, null, true, null, false).Primary);
    }

    [Fact]
    public void Every_fit_and_task_has_english_and_italian_words()
    {
        foreach (DiscoverFit fit in Enum.GetValues<DiscoverFit>())
        {
            Assert.NotEqual(ModelDiscoverPolicy.FitLabel(fit, false), ModelDiscoverPolicy.FitLabel(fit, true));
            Assert.NotEmpty(ModelDiscoverPolicy.FitGlyph(fit));
        }
        foreach (DiscoverTask task in Enum.GetValues<DiscoverTask>())
            Assert.NotEqual(ModelDiscoverPolicy.TaskTitle(task, false), ModelDiscoverPolicy.TaskTitle(task, true));
        foreach (string cls in new[] { "disk", "auth", "transient", "permanent", "" })
            Assert.NotEqual(ModelDiscoverPolicy.FailureFix(cls, false), ModelDiscoverPolicy.FailureFix(cls, true));
    }

    [Fact]
    public void Facts_line_skips_unknown_parts() =>
        Assert.Equal("7B · Q4_K_M · 4.0 GB · 32K context", ModelDiscoverPolicy.Facts(E("m"), false));
}
