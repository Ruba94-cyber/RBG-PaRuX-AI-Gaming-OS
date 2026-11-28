using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatHotPathPrechecksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-hotpath-" + Guid.NewGuid().ToString("N"));

    public ChatHotPathPrechecksTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ManagedModelVariantIdentity Variant(string id) =>
        new("qwen", "rev", id, ManagedModelFormat.Gguf, "Q4", id + ".gguf");

    private static RuntimeEngineBenchmarkMeasurement Measurement() =>
        new(PromptTokensPerSecond: 500, DecodeTokensPerSecond: 45, TimeToFirstTokenMilliseconds: 600,
            SustainedDecodeTokensPerSecond: 45, PeakVramMib: 16000, MaxGpuTemperatureC: 70, ErrorRate: 0,
            LoadMilliseconds: 1000, AverageGpuPowerWatts: 150);

    private static void SaveRecord(RuntimeEngineRaceEvidenceStore store, bool promote)
    {
        var request = new RuntimeEngineRaceRequest(
            "baseline", Variant("a"), "candidate", Variant("b"), "coding",
            SameWeightsAndQualityComparable: true, BaselineQualityScore: 95, CandidateQualityScore: 95,
            BaselineEngineVersion: "build-a", CandidateEngineVersion: "build-b");
        var result = new RuntimeEngineRaceResult
        {
            Status = "completed",
            BaselineEngineId = "baseline",
            CandidateEngineId = "candidate",
            BaselineEngineVersion = "build-a",
            CandidateEngineVersion = "build-b",
            BaselineMeasurement = Measurement(),
            CandidateMeasurement = Measurement(),
            Promotion = new RuntimePromotionDecision(promote, 1, 1.2, 20, promote ? "winner" : "no")
        };
        store.Save(request, result, "gpu-key");
    }

    [Fact]
    public void Missing_evidence_file_has_no_promoted_winner()
    {
        var store = new RuntimeEngineRaceEvidenceStore(Path.Combine(_root, "none.json"));
        Assert.False(store.MayHavePromotedWinner());
    }

    [Fact]
    public void Only_a_promoted_record_makes_a_winner_possible_and_fresh_writes_are_seen_immediately()
    {
        var store = new RuntimeEngineRaceEvidenceStore(Path.Combine(_root, "evidence.json"));
        SaveRecord(store, promote: false);
        Assert.False(store.MayHavePromotedWinner());
        // Written within the last second: the cache must not hide the new promoted record.
        SaveRecord(store, promote: true);
        Assert.True(store.MayHavePromotedWinner());
        Assert.NotNull(store.FindLatestWinner("qwen", "rev", "coding", "gpu-key"));
    }

    [Fact]
    public void Cached_answer_survives_when_file_is_old_and_unchanged()
    {
        string path = Path.Combine(_root, "old.json");
        var store = new RuntimeEngineRaceEvidenceStore(path);
        SaveRecord(store, promote: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        Assert.True(store.MayHavePromotedWinner());
        Assert.True(store.MayHavePromotedWinner());
    }

    [Fact]
    public void Corrupt_evidence_file_means_no_winner_like_the_full_lookup()
    {
        string path = Path.Combine(_root, "bad.json");
        File.WriteAllText(path, "{not json");
        var store = new RuntimeEngineRaceEvidenceStore(path);
        Assert.False(store.MayHavePromotedWinner());
        Assert.Null(store.FindLatestWinner("qwen", "rev", "coding", "gpu-key"));
    }
}
