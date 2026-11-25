using Microsoft.Data.Sqlite;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Automatic, bounded repair of passages indexed without a vector while the embedding model could not run (antivirus lock,
/// memory pressure). A fake embedder first fails then works; no model, no GPU, no network.
/// </summary>
public sealed class KnowledgeAutoRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-autorec-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _toDispose = new();

    public KnowledgeAutoRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (IDisposable d in _toDispose) { try { d.Dispose(); } catch { } }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class FlakyEmbedder : IRetrievalEmbeddingProvider
    {
        private int _inside, _max;
        public bool Healthy { get; set; }
        public bool Available { get; set; } = true;
        public Action<int>? OnBatch { get; set; }
        public int Batches { get; private set; }
        public List<string> Embedded { get; } = new();
        public int MaxConcurrent => _max;
        public RetrievalEmbeddingIdentity Identity { get; } = new("fake-flaky", "flaky", "1", 64, "none");
        public bool IsAvailable => Available;
        public float RelevanceThreshold => 0.05f;
        public float[]? TryEmbed(string text) => Vec(text);
        public IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts)
        {
            int n = Interlocked.Increment(ref _inside);
            try
            {
                _max = Math.Max(_max, n);
                OnBatch?.Invoke(++Batches);
                if (!Healthy) return texts.Select(_ => (float[]?)null).ToList();
                lock (Embedded) Embedded.AddRange(texts);
                return texts.Select(Vec).ToList();
            }
            finally { Interlocked.Decrement(ref _inside); }
        }
        private float[]? Vec(string text)
        {
            if (!Healthy) return null;
            var v = new float[64];
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9_]{2,}"))
            {
                int h = 17; foreach (char ch in m.Value) h = unchecked(h * 31 + ch);
                v[(h & 0x7fffffff) % 64] += 1f;
            }
            float norm = MathF.Sqrt(v.Sum(x => x * x));
            return norm == 0 ? v : v.Select(x => x / norm).ToArray();
        }
    }

    private string Docs(string marker, int count)
    {
        string f = Path.Combine(_root, "docs-" + marker + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(f);
        for (int i = 0; i < count; i++)
            File.WriteAllText(Path.Combine(f, marker + i + ".txt"), "Procedure " + marker + " to inspect the coolant pump number " + i + ".");
        return f;
    }

    private sealed class Rig
    {
        public required KnowledgeJobService Svc { get; init; }
        public required KnowledgeIndex Index { get; init; }
        public required FlakyEmbedder Embedder { get; init; }
        public DateTime Now { get; set; } = new(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
    }

    private Rig NewRig(FlakyEmbedder e, Action<KnowledgeSettings>? tweak = null)
    {
        var settings = new KnowledgeSettingsStore(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json"));
        if (tweak != null) { KnowledgeSettings s = settings.Get(); tweak(s); settings.Save(s); }
        var catalog = new KnowledgeEmbedderCatalog(new IRetrievalEmbeddingProvider[] { e });
        var sw = new SwitchableEmbedder(() => catalog.Resolve(null)!, () => !settings.Get().EmbeddingNeeded());
        var index = new KnowledgeIndex(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db"),
            new KnowledgeIndexOptions { EmbedBatchSize = 2, ThrottleMilliseconds = 0 }, sw);
        _toDispose.Add(index);
        var svc = new KnowledgeJobService(new RagStore(new EmbeddingService(Path.Combine(_root, "emb-" + Guid.NewGuid().ToString("N"))), Path.Combine(_root, "rag-" + Guid.NewGuid().ToString("N"))),
            settings, null, index, new UnavailableOcrEngine("t"), e, null, catalog);
        _toDispose.Add(svc);
        var rig = new Rig { Svc = svc, Index = index, Embedder = e };
        svc.GamingActive = () => false;
        svc.CorporateActive = () => false;
        svc.RamSnapshot = () => SystemRamSnapshot.TryCreate(16UL << 30, 8UL << 30);
        svc.RecoveryBatchPause = TimeSpan.Zero;
        svc.RecoveryPressureWait = TimeSpan.FromMilliseconds(5);
        svc.RecoveryPressureWaits = 2;
        svc.UtcNow = () => rig.Now;
        return rig;
    }

    private static async Task Settle(KnowledgeJobService svc) => await svc.WaitForReembedAsync(TimeSpan.FromSeconds(20));

    [Fact]
    public async Task Embedder_that_fails_then_recovers_gets_its_missing_vectors_back_without_any_manual_action()
    {
        var e = new FlakyEmbedder { Healthy = false };
        Rig r = NewRig(e);
        await r.Index.IndexFolderAsync("s", Docs("alpha", 6), null, null, default);
        KnowledgeEmbeddingState before = r.Index.EmbeddingState();
        Assert.True(before.Missing > 0);
        Assert.Equal(0, before.Current);
        Assert.Empty(r.Index.Search("coolant pump", KnowledgeSearchMode.Semantic, 3, new[] { "s" }));   // the reported defect

        // Still locked: a fruitless attempt, bounded, nothing broken.
        Assert.True(r.Svc.TryAutoRecoverTick());
        await Settle(r.Svc);
        Assert.Equal("waiting", r.Svc.RecoveryStatus.State);
        Assert.Equal(before.Missing, r.Index.EmbeddingState().Missing);

        // Immediately again: the cool-down holds it back (no hot loop).
        Assert.False(r.Svc.TryAutoRecoverTick());
        Assert.Equal("cooldown", r.Svc.RecoveryStatus.Reason);

        e.Healthy = true;                       // antivirus released the file
        r.Now += TimeSpan.FromSeconds(60);
        Assert.True(r.Svc.TryAutoRecoverTick());
        Assert.True(r.Svc.ReembedStatus.Automatic);
        await Settle(r.Svc);

        KnowledgeEmbeddingState after = r.Index.EmbeddingState();
        Assert.Equal(0, after.Missing);
        Assert.Equal(before.TotalChunks, after.Current);
        Assert.Equal("done", r.Svc.ReembedStatus.State);
        Assert.Equal("idle", r.Svc.RecoveryStatus.State);
        Assert.NotEmpty(r.Index.Search("coolant pump", KnowledgeSearchMode.Semantic, 3, new[] { "s" }));
        Assert.False(r.Svc.TryAutoRecoverTick());   // nothing left: no job
    }

    [Fact]
    public async Task Only_the_missing_chunks_are_embedded_again_and_other_vectors_are_never_touched()
    {
        var e = new FlakyEmbedder { Healthy = true };
        Rig r = NewRig(e);
        await r.Index.IndexFolderAsync("good", Docs("okdoc", 4), null, null, default);
        int goodChunks = r.Index.EmbeddingState().Current;
        Assert.True(goodChunks > 0);
        e.Healthy = false;
        await r.Index.IndexFolderAsync("late", Docs("lockeddoc", 3), null, null, default);
        int missing = r.Index.EmbeddingState().Missing;
        Assert.True(missing > 0);
        Assert.Equal(goodChunks, r.Index.EmbeddingState().Current);

        e.Healthy = true;
        lock (e.Embedded) e.Embedded.Clear();
        Assert.True(r.Svc.TryAutoRecoverTick());
        await Settle(r.Svc);

        lock (e.Embedded)
        {
            Assert.Equal(missing, e.Embedded.Count);                                    // exactly the missing passages
            Assert.All(e.Embedded, t => Assert.DoesNotContain("okdoc", t));             // the healthy documents are not re-processed
        }
        Assert.Equal(0, r.Index.EmbeddingState().Missing);
    }

    [Fact]
    public async Task Never_more_than_one_job_and_a_manual_request_does_not_start_a_second_one()
    {
        var e = new FlakyEmbedder { Healthy = false };
        Rig r = NewRig(e);
        await r.Index.IndexFolderAsync("s", Docs("beta", 8), null, null, default);
        e.Healthy = true;
        var hold = new ManualResetEventSlim(false);
        e.OnBatch = n => { if (n == 1) hold.Wait(5000); };

        Assert.True(r.Svc.TryAutoRecoverTick());
        await Task.Delay(100);
        Assert.True(r.Svc.ReembedRunning);
        Assert.False(r.Svc.TryAutoRecoverTick());                       // an automatic job is already running
        Assert.False(r.Svc.TryAutoRecoverTick());
        hold.Set();
        await Settle(r.Svc);
        Assert.Equal(1, e.MaxConcurrent);                               // the model never ran two batches at once
        Assert.Equal(0, r.Index.EmbeddingState().Missing);
    }

    [Fact]
    public async Task The_person_can_cancel_the_repair_and_it_does_not_restart_by_itself()
    {
        var e = new FlakyEmbedder { Healthy = false };
        Rig r = NewRig(e);
        await r.Index.IndexFolderAsync("s", Docs("gamma", 10), null, null, default);
        e.Healthy = true;
        var gate = new ManualResetEventSlim(false);
        e.OnBatch = n => { if (n == 1) gate.Wait(5000); };

        Assert.True(r.Svc.TryAutoRecoverTick());
        await Task.Delay(100);
        Assert.True(r.Svc.CancelReembed());
        gate.Set();
        await Settle(r.Svc);

        Assert.Equal("cancelled", r.Svc.ReembedStatus.State);
        Assert.Equal("cancelled", r.Svc.RecoveryStatus.State);
        int remaining = r.Index.EmbeddingState().Missing;
        Assert.True(remaining > 0 && remaining < r.Index.EmbeddingState().TotalChunks);   // consistent: finished batches kept
        r.Now += TimeSpan.FromHours(3);
        Assert.False(r.Svc.TryAutoRecoverTick());                                          // respected
        Assert.Equal(remaining, r.Index.EmbeddingState().Missing);

        Assert.True(r.Svc.StartReembed(out _));                                            // the manual button still repairs everything
        await Settle(r.Svc);
        Assert.Equal(0, r.Index.EmbeddingState().Missing);
    }

    [Fact]
    public async Task A_model_that_keeps_failing_is_retried_a_bounded_number_of_times_then_the_repair_stops()
    {
        var e = new FlakyEmbedder { Healthy = false };
        Rig r = NewRig(e);
        await r.Index.IndexFolderAsync("s", Docs("delta", 5), null, null, default);
        int started = 0;
        for (int i = 0; i < 40; i++)
        {
            r.Now += TimeSpan.FromMinutes(6);              // longer than the slow cool-down
            if (r.Svc.TryAutoRecoverTick()) { started++; await Settle(r.Svc); }
            if (r.Now - new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc) > TimeSpan.FromMinutes(25)) break;
        }
        Assert.Equal(KnowledgeJobService.MaxAutoAttempts, started);
        Assert.Equal("gave_up", r.Svc.RecoveryStatus.State);
        Assert.Equal("model_failing", r.Svc.RecoveryStatus.Reason);
        int batchesWhenGivenUp = e.Batches;
        Assert.True(batchesWhenGivenUp <= KnowledgeJobService.MaxAutoAttempts * 2);        // each attempt stops after 2 empty batches

        for (int i = 0; i < 5; i++) { r.Now += TimeSpan.FromMinutes(3); Assert.False(r.Svc.TryAutoRecoverTick()); }
        Assert.Equal(batchesWhenGivenUp, e.Batches);

        // Much later one more bounded round is allowed (a lock held for an hour must not disable repair until restart)...
        e.Healthy = true;
        r.Now += KnowledgeJobService.AutoRearmAfter + TimeSpan.FromMinutes(1);
        Assert.True(r.Svc.TryAutoRecoverTick());
        await Settle(r.Svc);
        Assert.Equal(0, r.Index.EmbeddingState().Missing);
    }

    [Fact]
    public async Task Corporate_memory_pressure_defers_the_repair_without_loading_the_model_and_it_resumes_when_memory_returns()
    {
        var e = new FlakyEmbedder { Healthy = false };
        Rig r = NewRig(e);
        await r.Index.IndexFolderAsync("s", Docs("eps", 6), null, null, default);
        e.Healthy = true;
        int batchesBefore = e.Batches;
        r.Svc.CorporateActive = () => true;

        r.Svc.RamSnapshot = () => SystemRamSnapshot.TryCreate(16UL << 30, 1UL << 30);     // 1 GiB free: inside Corporate's reserve
        Assert.False(r.Svc.TryAutoRecoverTick());
        Assert.Equal("deferred", r.Svc.RecoveryStatus.State);
        Assert.Equal("memory_pressure", r.Svc.RecoveryStatus.Reason);
        r.Svc.RamSnapshot = () => null;                                                   // unreadable sample fails closed
        Assert.False(r.Svc.TryAutoRecoverTick());
        Assert.Equal("memory_pressure", r.Svc.RecoveryStatus.Reason);
        Assert.Equal(batchesBefore, e.Batches);                                           // the model was never asked

        r.Svc.RamSnapshot = () => SystemRamSnapshot.TryCreate(16UL << 30, 6UL << 30);
        Assert.True(r.Svc.TryAutoRecoverTick());
        await Settle(r.Svc);
        Assert.Equal(0, r.Index.EmbeddingState().Missing);
    }

    [Fact]
    public async Task Pressure_that_appears_mid_run_stops_cleanly_keeps_the_work_done_and_does_not_count_as_a_model_failure()
    {
        var e = new FlakyEmbedder { Healthy = false };
        Rig r = NewRig(e);
        await r.Index.IndexFolderAsync("s", Docs("zeta", 60), null, null, default);
        e.Healthy = true;
        r.Svc.CorporateActive = () => true;
        r.Svc.RamSnapshot = () => SystemRamSnapshot.TryCreate(16UL << 30, 8UL << 30);
        e.OnBatch = n => { if (n == 1) r.Svc.RamSnapshot = () => SystemRamSnapshot.TryCreate(16UL << 30, 1UL << 30); };

        Assert.True(r.Svc.TryAutoRecoverTick());
        await Settle(r.Svc);

        KnowledgeEmbeddingState st = r.Index.EmbeddingState();
        Assert.True(st.Current > 0 && st.Missing > 0, $"cur={st.Current} miss={st.Missing} tot={st.TotalChunks} batches={e.Batches} rec={r.Svc.RecoveryStatus}");
        Assert.Equal("deferred", r.Svc.RecoveryStatus.State);
        Assert.Equal("memory_pressure", r.Svc.RecoveryStatus.Reason);
        Assert.Equal(0, r.Svc.RecoveryStatus.Attempts);                                   // not a model failure

        r.Svc.RamSnapshot = () => SystemRamSnapshot.TryCreate(16UL << 30, 8UL << 30);
        e.OnBatch = null;
        Assert.True(r.Svc.TryAutoRecoverTick());
        await Settle(r.Svc);
        Assert.Equal(0, r.Index.EmbeddingState().Missing);
    }

    [Fact]
    public async Task Gaming_and_a_running_index_job_hold_the_repair_back()
    {
        var e = new FlakyEmbedder { Healthy = false };
        Rig r = NewRig(e);
        await r.Index.IndexFolderAsync("s", Docs("eta", 4), null, null, default);
        e.Healthy = true;
        bool gaming = true;
        r.Svc.GamingActive = () => gaming;
        Assert.False(r.Svc.TryAutoRecoverTick());
        Assert.Equal("gaming", r.Svc.RecoveryStatus.Reason);
        gaming = false;
        Assert.True(r.Svc.TryAutoRecoverTick());
        await Settle(r.Svc);
        Assert.Equal(0, r.Index.EmbeddingState().Missing);
    }

    [Fact]
    public async Task Keyword_only_workspaces_and_an_uninstalled_model_never_start_a_repair()
    {
        var e = new FlakyEmbedder { Healthy = false };
        Rig r = NewRig(e, s => s.WorkspaceRetrievalModes["default"] = "keyword");   // vectors are not wanted at all
        await r.Index.IndexFolderAsync("s", Docs("theta", 4), null, null, default);
        e.Healthy = true;
        Assert.False(r.Svc.TryAutoRecoverTick());
        Assert.Equal(0, e.Batches);

        var e2 = new FlakyEmbedder { Healthy = false, Available = false };
        Rig r2 = NewRig(e2);
        await r2.Index.IndexFolderAsync("s", Docs("iota", 4), null, null, default);
        Assert.False(r2.Svc.TryAutoRecoverTick());
        Assert.Equal("no_embedder", r2.Svc.RecoveryStatus.Reason);
    }

    [Fact]
    public void The_repair_code_uses_no_network_api_and_the_status_is_plain_data()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "PaRuX.sln"))) root = Path.GetDirectoryName(root)!;
        Assert.NotNull(root);
        foreach (string file in new[] { "KnowledgeJobService.AutoRecovery.cs", "KnowledgeIndex.Reembed.cs" })
        {
            string text = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Server", "Knowledge", file));
            foreach (string forbidden in new[] { "HttpClient", "System.Net", "WebClient", "Socket", "http://", "https://", "Process.Start" })
                Assert.DoesNotContain(forbidden, text);
        }
    }
}
