using System.Globalization;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Bounded per-run metric history for the Training Lab. One small JSON file per job under
/// <c>history/</c> of the training runtime root, kept after the run ends so past curves can be
/// reopened and re-exported. Bounds: samples per run, number of retained runs and bytes read back,
/// so neither a very long run nor a pile of old runs can grow memory or disk without limit.
/// A corrupt or unreadable file is quarantined (renamed) and reported, never thrown at the caller.
/// </summary>
internal static class TrainingRunHistoryStore
{
    internal const int SchemaVersion = 1;
    internal const int MaxSamplesPerRun = 2_000;
    internal const int MaxRetainedRuns = 50;
    internal const long MaxFileBytes = 8L * 1024 * 1024;
    private const string DirectoryName = "history";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    internal sealed class RunHistory
    {
        public int SchemaVersion { get; set; } = TrainingRunHistoryStore.SchemaVersion;
        public string JobId { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime SavedUtc { get; set; }
        public int TotalSteps { get; set; }
        public int OriginalSampleCount { get; set; }
        public List<TrainingMetricSample> Samples { get; set; } = new();
    }

    internal sealed record LoadResult(RunHistory? Value, string? Error);

    internal static string HistoryDirectory(string runtimeRoot) => Path.Combine(runtimeRoot, DirectoryName);

    /// <summary>Caps a sample list: keeps the first and last sample, the highest-loss and
    /// highest-grad-norm samples (the diagnostic spikes) and an even stride of the rest.</summary>
    internal static List<TrainingMetricSample> Bound(IReadOnlyList<TrainingMetricSample> samples, int max = MaxSamplesPerRun)
    {
        if (max < 4) max = 4;
        if (samples.Count <= max) return samples.ToList();

        var keep = new SortedSet<int> { 0, samples.Count - 1 };
        int maxLoss = -1, maxGrad = -1;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Loss is { } l && double.IsFinite(l) && (maxLoss < 0 || l > samples[maxLoss].Loss!.Value)) maxLoss = i;
            if (samples[i].GradientNorm is { } g && double.IsFinite(g) && (maxGrad < 0 || g > samples[maxGrad].GradientNorm!.Value)) maxGrad = i;
        }
        if (maxLoss >= 0) keep.Add(maxLoss);
        if (maxGrad >= 0) keep.Add(maxGrad);
        int strideSlots = max - keep.Count;
        for (int k = 0; k < strideSlots; k++)
            keep.Add((int)((long)(samples.Count - 1) * k / Math.Max(1, strideSlots - 1)));
        // The stride may collide with pinned indexes; the set dedupes, so the count is <= max.
        return keep.Take(max).Select(i => samples[i]).ToList();
    }

    internal static void Save(string runtimeRoot, TrainingJobStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(status);
        ValidateJobId(status.Id);
        List<TrainingMetricSample> history = status.History ?? new List<TrainingMetricSample>();
        var run = new RunHistory
        {
            JobId = status.Id,
            Method = status.Method,
            Status = status.Status,
            CreatedUtc = status.CreatedUtc,
            SavedUtc = DateTime.UtcNow,
            TotalSteps = status.TotalSteps,
            OriginalSampleCount = history.Count,
            Samples = Bound(history)
        };

        string directory = HistoryDirectory(runtimeRoot);
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, status.Id + ".json");
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(run, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
        Prune(runtimeRoot, MaxRetainedRuns);
    }

    internal static LoadResult Load(string runtimeRoot, string jobId)
    {
        ValidateJobId(jobId);
        string path = Path.Combine(HistoryDirectory(runtimeRoot), jobId + ".json");
        if (!File.Exists(path)) return new LoadResult(null, null);
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
                throw new InvalidDataException("Training history file exceeds the size limit.");
            RunHistory? run = JsonSerializer.Deserialize<RunHistory>(File.ReadAllText(path), JsonOptions);
            if (run == null) throw new InvalidDataException("Training history file is empty.");
            if (run.SchemaVersion != SchemaVersion)
                throw new InvalidDataException("Unsupported training history schema " + run.SchemaVersion + ".");
            if (!string.Equals(run.JobId, jobId, StringComparison.Ordinal))
                throw new InvalidDataException("Training history job id does not match its file name.");
            run.Samples ??= new List<TrainingMetricSample>();
            if (run.Samples.Count > MaxSamplesPerRun) run.Samples = Bound(run.Samples);
            return new LoadResult(run, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            try { File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N")[..8], overwrite: false); } catch { }
            return new LoadResult(null, ex.Message);
        }
    }

    /// <summary>Newest runs win; older history files (and quarantined ones) beyond the cap are deleted.</summary>
    internal static void Prune(string runtimeRoot, int maxRuns)
    {
        string directory = HistoryDirectory(runtimeRoot);
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (FileInfo old in new DirectoryInfo(directory).EnumerateFiles("*.json")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(Math.Max(1, maxRuns)))
                try { old.Delete(); } catch { }
            foreach (FileInfo bad in new DirectoryInfo(directory).EnumerateFiles("*.corrupt-*")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(5))
                try { bad.Delete(); } catch { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static string ToCsv(IEnumerable<TrainingMetricSample> samples)
    {
        var sb = new StringBuilder("step,loss,grad_norm,learning_rate,epoch,tokens_per_second,gpu_util_percent,vram_mib\n");
        foreach (TrainingMetricSample s in samples)
        {
            sb.Append(s.Step.ToString(CultureInfo.InvariantCulture));
            foreach (double? v in new[] { s.Loss, s.GradientNorm, s.LearningRate, s.Epoch, s.TokensPerSecond, s.GpuUtilPercent, s.VramMib })
            {
                sb.Append(',');
                if (v is { } d && double.IsFinite(d)) sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    internal static string ToJson(RunHistory run) => JsonSerializer.Serialize(run, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    private static void ValidateJobId(string? id)
    {
        if (id?.Length != 32 || id.Any(ch => !char.IsAsciiHexDigit(ch)))
            throw new InvalidDataException("Training job id is not a GUID-N identifier.");
    }
}
