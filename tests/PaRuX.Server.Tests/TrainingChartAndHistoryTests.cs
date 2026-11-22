using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TrainingChartAndHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-training-history-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static TrainingJobStatus Job(int samples, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"), Status = "completed", Method = "sft", TotalSteps = samples,
        CreatedUtc = DateTime.UtcNow,
        History = Enumerable.Range(1, samples).Select(i => new TrainingMetricSample
        {
            Step = i, Loss = 2.0 / i, GradientNorm = 0.5, LearningRate = 2e-4, TokensPerSecond = 1000 + i
        }).ToList()
    };

    [Theory]
    [InlineData(10, 240)]
    [InlineData(241, 240)]
    [InlineData(100_000, 240)]
    [InlineData(100_000, 7)]
    public void Downsample_never_exceeds_max_and_keeps_endpoints(int count, int max)
    {
        var series = TrainingChartModel.Build(TrainingChartModel.SeriesKind.Loss,
            Enumerable.Range(0, count).Select(i => ((double)i, (double?)Math.Sin(i))), max);
        Assert.True(series.Points.Count <= Math.Max(2, max));
        Assert.Equal(count, series.RawCount);
        Assert.Equal(0, series.Points[0].Step);
        Assert.Equal(count - 1, series.Points[^1].Step);
        Assert.True(series.Points.Zip(series.Points.Skip(1)).All(p => p.First.Step <= p.Second.Step));
    }

    [Fact]
    public void Downsample_preserves_a_single_spike()
    {
        var data = Enumerable.Range(0, 50_000).Select(i => ((double)i, (double?)(i == 31_337 ? 99.0 : 1.0)));
        var series = TrainingChartModel.Build(TrainingChartModel.SeriesKind.GradientNorm, data, 100);
        Assert.Contains(series.Points, p => p.Value == 99.0);
        Assert.Equal(99.0, series.Max);
    }

    [Fact]
    public void Missing_and_nonfinite_values_are_skipped_not_zeroed()
    {
        var series = TrainingChartModel.Build(TrainingChartModel.SeriesKind.GpuUtilPercent,
            new (double, double?)[] { (1, null), (2, double.NaN), (3, double.PositiveInfinity) });
        Assert.False(series.HasData);
        Assert.Empty(TrainingChartModel.ToPixels(series, 100, 50));
        Assert.Contains("no data", TrainingChartModel.DescribeEn(series, "GPU"));
    }

    [Fact]
    public void Pixels_stay_inside_the_plot_and_flat_series_is_centered()
    {
        var s = TrainingChartModel.Build(TrainingChartModel.SeriesKind.Loss,
            Enumerable.Range(0, 1000).Select(i => ((double)i, (double?)(i % 17))));
        Assert.All(TrainingChartModel.ToPixels(s, 320, 56), p => { Assert.InRange(p.X, 0, 320); Assert.InRange(p.Y, 0, 56); });
        var flat = TrainingChartModel.Build(TrainingChartModel.SeriesKind.Loss, new (double, double?)[] { (0, 1), (1, 1) });
        Assert.All(TrainingChartModel.ToPixels(flat, 100, 40), p => Assert.Equal(20, p.Y));
    }

    [Fact]
    public void Text_alternative_is_localized_and_invariant_culture()
    {
        var s = TrainingChartModel.Build(TrainingChartModel.SeriesKind.Loss, new (double, double?)[] { (1, 2.5), (2, 0.5) });
        Assert.Contains("falling", TrainingChartModel.DescribeEn(s, "Loss"));
        Assert.Contains("in calo", TrainingChartModel.DescribeIt(s, "Perdita"));
        Assert.Contains("2.5", TrainingChartModel.DescribeEn(s, "Loss"));
    }

    [Fact]
    public void Parser_reads_tokens_per_second()
    {
        var s = TrainingMetricsParser.Parse("{'loss': 1.5, 'tokens_per_second': 812.5}");
        Assert.Equal(812.5, s.TokensPerSecond);
        Assert.False(TrainingMetricsParser.Parse("{'loss': 1.5}").TokensPerSecond.HasValue);
    }

    [Fact]
    public void History_round_trips_and_survives_reload()
    {
        TrainingJobStatus job = Job(50);
        TrainingRunHistoryStore.Save(_root, job);
        var loaded = TrainingRunHistoryStore.Load(_root, job.Id);
        Assert.Null(loaded.Error);
        Assert.Equal(50, loaded.Value!.Samples.Count);
        Assert.Equal(1050, loaded.Value.Samples[50 - 1].TokensPerSecond);
    }

    [Fact]
    public void Corrupt_history_is_quarantined_and_reported()
    {
        TrainingJobStatus job = Job(5);
        TrainingRunHistoryStore.Save(_root, job);
        string path = Path.Combine(TrainingRunHistoryStore.HistoryDirectory(_root), job.Id + ".json");
        File.WriteAllText(path, "{ not json");
        var loaded = TrainingRunHistoryStore.Load(_root, job.Id);
        Assert.Null(loaded.Value);
        Assert.NotNull(loaded.Error);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(TrainingRunHistoryStore.HistoryDirectory(_root), "*.corrupt-*"));
        Assert.Null(TrainingRunHistoryStore.Load(_root, job.Id).Error);
    }

    [Fact]
    public void Wrong_schema_is_rejected()
    {
        TrainingJobStatus job = Job(3);
        TrainingRunHistoryStore.Save(_root, job);
        string path = Path.Combine(TrainingRunHistoryStore.HistoryDirectory(_root), job.Id + ".json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\":1", "\"schemaVersion\":99"));
        Assert.NotNull(TrainingRunHistoryStore.Load(_root, job.Id).Error);
    }

    [Fact]
    public void Invalid_job_id_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => TrainingRunHistoryStore.Load(_root, "../evil"));
    }

    [Fact]
    public void Large_run_is_bounded_and_keeps_spikes_and_endpoints()
    {
        TrainingJobStatus job = Job(200_000);
        job.History[123_456].Loss = 500;
        job.History[77_777].GradientNorm = 900;
        TrainingRunHistoryStore.Save(_root, job);
        var loaded = TrainingRunHistoryStore.Load(_root, job.Id).Value!;
        Assert.True(loaded.Samples.Count <= TrainingRunHistoryStore.MaxSamplesPerRun);
        Assert.Equal(200_000, loaded.OriginalSampleCount);
        Assert.Equal(1, loaded.Samples[0].Step);
        Assert.Equal(200_000, loaded.Samples[^1].Step);
        Assert.Contains(loaded.Samples, s => s.Loss == 500);
        Assert.Contains(loaded.Samples, s => s.GradientNorm == 900);
        Assert.True(new FileInfo(Path.Combine(TrainingRunHistoryStore.HistoryDirectory(_root), job.Id + ".json")).Length
            < TrainingRunHistoryStore.MaxFileBytes);
    }

    [Fact]
    public void Retention_keeps_only_the_newest_runs()
    {
        for (int i = 0; i < TrainingRunHistoryStore.MaxRetainedRuns + 5; i++)
            TrainingRunHistoryStore.Save(_root, Job(3));
        Assert.True(Directory.GetFiles(TrainingRunHistoryStore.HistoryDirectory(_root), "*.json").Length
            <= TrainingRunHistoryStore.MaxRetainedRuns);
    }

    [Fact]
    public void Export_csv_has_header_blank_for_missing_and_invariant_numbers()
    {
        string csv = TrainingRunHistoryStore.ToCsv(new[]
        {
            new TrainingMetricSample { Step = 1, Loss = 1.5, LearningRate = 2e-4 },
            new TrainingMetricSample { Step = 2, GpuUtilPercent = 80, VramMib = 4096 }
        });
        string[] lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("step,loss,grad_norm,learning_rate,epoch,tokens_per_second,gpu_util_percent,vram_mib", lines[0]);
        Assert.Equal("1,1.5,,0.0002,,,,", lines[1]);
        Assert.Equal("2,,,,,,80,4096", lines[2]);
    }

    [Fact]
    public void Export_json_contains_job_and_samples()
    {
        TrainingJobStatus job = Job(4);
        TrainingRunHistoryStore.Save(_root, job);
        var run = TrainingRunHistoryStore.Load(_root, job.Id).Value!;
        string json = TrainingRunHistoryStore.ToJson(run);
        Assert.Contains(job.Id, json);
        Assert.Contains("\"samples\"", json);
    }
}
