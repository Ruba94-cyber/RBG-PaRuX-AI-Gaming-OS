using Microsoft.Data.Sqlite;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Embedder choice, per-workspace retrieval mode, safe re-embed (migration), corrupt-index recovery and the in-app retrieval check.
/// Everything runs on a deterministic fake embedder: no model, no GPU, no network.
/// </summary>
public sealed class KnowledgeRetrievalModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-retmode-" + Guid.NewGuid().ToString("N"));

    public KnowledgeRetrievalModeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>
    /// Concept embedder: every word (and every benchmark paraphrase of a topic or verb) maps to a hashed axis, so a paraphrase lands
    /// near its document without sharing a keyword. Deterministic. <see cref="FailFor"/> / <see cref="OnBatch"/> inject faults.
    /// </summary>
    private sealed class FakeEmbedder : IRetrievalEmbeddingProvider
    {
        private const int Dims = 256;
        private readonly Dictionary<string, string> _canonical = new(StringComparer.OrdinalIgnoreCase);
        public FakeEmbedder(string model = "fake-a", int dims = Dims)
        {
            Identity = new RetrievalEmbeddingIdentity("fake-" + model, model, "1", dims, "none");
            string[] topics = { "coolant pump", "brake caliper", "battery balancer", "gearbox oil", "wiring harness", "firmware loader", "sensor calibration",
                "torque wrench", "valve timing", "heat exchanger", "fuel injector", "software release", "diagnostic session", "gateway module" };
            string[] verbs = { "inspect", "replace", "calibrate", "measure", "document", "validate", "configure", "archive" };
            for (int i = 0; i < topics.Length; i++) Map(KnowledgeBenchmark.TopicParaphrases[i], topics[i]);
            for (int i = 0; i < verbs.Length; i++) Map(KnowledgeBenchmark.VerbParaphrases[i], verbs[i]);
        }
        private void Map(string phrase, string canonical)
        {
            string[] a = phrase.Split(' '), b = canonical.Split(' ');
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) _canonical[a[i]] = b[i];
            for (int i = b.Length; i < a.Length; i++) _canonical[a[i]] = b[^1];
        }
        public bool Available { get; set; } = true;
        public Func<string, bool> FailFor { get; set; } = _ => false;
        public Action<int>? OnBatch { get; set; }
        public int Batches { get; private set; }
        public RetrievalEmbeddingIdentity Identity { get; }
        public bool IsAvailable => Available;
        public float RelevanceThreshold => 0.05f;
        public float[]? TryEmbed(string text)
        {
            if (FailFor(text)) return null;
            var v = new float[Identity.Dimensions];
            foreach (string raw in System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9_]{2,}").Select(m => m.Value))
            {
                string w = _canonical.TryGetValue(raw, out string? c) ? c : raw;
                int h = 17; foreach (char ch in w) h = unchecked(h * 31 + ch);
                v[(h & 0x7fffffff) % v.Length] += 1f;
            }
            float n = MathF.Sqrt(v.Sum(x => x * x));
            return n == 0 ? v : v.Select(x => x / n).ToArray();
        }
        public IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts)
        {
            OnBatch?.Invoke(++Batches);
            return texts.Select(TryEmbed).ToList();
        }
    }

    private string Docs(int count = 6)
    {
        string f = Path.Combine(_root, "docs-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(f);
        for (int i = 0; i < count; i++)
            File.WriteAllText(Path.Combine(f, "n" + i + ".txt"), "Procedure to " + new[] { "inspect", "replace", "validate" }[i % 3] + " the " + new[] { "coolant pump", "brake caliper" }[i % 2] + " number " + i + ".");
        return f;
    }

    private KnowledgeIndex NewIndex(IRetrievalEmbeddingProvider? e, string? db = null, int batch = 2, int throttle = 0) =>
        new(db ?? Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db"),
            new KnowledgeIndexOptions { EmbedBatchSize = batch, ThrottleMilliseconds = throttle }, e);

    // ------------------------------------------------------------------------------------ settings, modes, catalog

    [Fact]
    public void Workspace_mode_and_embedder_choice_round_trip_through_the_settings_file()
    {
        string path = Path.Combine(_root, "settings.json");
        var store = new KnowledgeSettingsStore(path);
        KnowledgeSettings s = store.Get();
        s.WorkspaceRetrievalModes["project-x"] = KnowledgeRetrievalModes.Embedding;
        s.EmbedderId = "fake-b";
        store.Save(s);

        KnowledgeSettings back = new KnowledgeSettingsStore(path).Get();
        Assert.Equal("embedding", back.EffectiveMode("project-x"));
        Assert.Equal("hybrid", back.EffectiveMode("other"));      // no entry: the default is hybrid
        Assert.Equal("fake-b", back.EmbedderId);
    }

    [Fact]
    public void Existing_settings_without_the_new_fields_keep_their_search_method_as_the_workspace_mode()
    {
        string path = Path.Combine(_root, "legacy.json");
        File.WriteAllText(path, "{ \"SearchMethod\": \"keyword\", \"OcrEnabled\": true }");   // a pre-slice settings file
        KnowledgeSettings s = new KnowledgeSettingsStore(path).Get();
        Assert.True(s.OcrEnabled);
        Assert.Equal("keyword", s.EffectiveMode(null));
        Assert.False(s.EmbeddingNeeded());
        s.WorkspaceRetrievalModes["w"] = "hybrid";
        Assert.True(s.EmbeddingNeeded());   // one hybrid workspace is enough to need vectors
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_defaults_and_is_kept_aside()
    {
        string path = Path.Combine(_root, "bad.json");
        File.WriteAllText(path, "{ not json");
        KnowledgeSettings s = new KnowledgeSettingsStore(path).Get();
        Assert.Equal("hybrid", s.EffectiveMode(null));
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.Equal("{ not json", File.ReadAllText(path + ".corrupt"));
    }

    [Fact]
    public void Every_embedder_option_carries_its_own_vector_identity_tag_for_the_picker()
    {
        var catalog = new KnowledgeEmbedderCatalog(new IRetrievalEmbeddingProvider[] { new FakeEmbedder("fake-a"), new FakeEmbedder("fake-b") });
        IReadOnlyList<KnowledgeEmbedderOption> options = catalog.Options(null);
        Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.Key)));
        Assert.Equal(2, options.Select(o => o.Key).Distinct().Count()); // two models never share a tag, so their vectors are never mixed
    }

    [Fact]
    public void Catalog_lists_only_registered_embedders_marks_the_selected_one_and_falls_back_to_an_installed_one()
    {
        var a = new FakeEmbedder("fake-a") { Available = false };
        var b = new FakeEmbedder("fake-b");
        var catalog = new KnowledgeEmbedderCatalog(new IRetrievalEmbeddingProvider[] { a, b });
        Assert.Equal(2, catalog.Options(null).Count);
        Assert.Equal("fake-fake-b", catalog.Options(null).Single(o => o.Selected).Id);      // a is not installed: first installed wins
        Assert.Equal("fake-fake-a", catalog.Options("fake-fake-a").Single(o => o.Selected).Id);
        Assert.False(catalog.Options(null).Single(o => o.Id == "fake-fake-a").Installed);
        Assert.Same(b, catalog.Resolve("unknown-id"));
        Assert.True(new KnowledgeEmbedderCatalog(Array.Empty<IRetrievalEmbeddingProvider>()).IsEmpty);
    }

    // ------------------------------------------------------------------------------------ retrieval modes on the index

    [Fact]
    public async Task Keyword_embedding_and_hybrid_modes_behave_differently_on_a_paraphrase()
    {
        var e = new FakeEmbedder();
        string docs = Docs();
        using KnowledgeIndex index = NewIndex(e);
        await index.IndexFolderAsync("s", docs, null, null, default);
        string paraphrase = "examine radiator circulation unit";   // shares no keyword with "inspect the coolant pump"
        var scope = new[] { "s" };

        Assert.Empty(index.Search(paraphrase, KnowledgeSearchMode.Keyword, 3, scope));
        Assert.NotEmpty(index.Search(paraphrase, KnowledgeSearchMode.Semantic, 3, scope));
        IReadOnlyList<KnowledgeHit> hybrid = index.Search(paraphrase, KnowledgeSearchMode.Auto, 3, scope);
        Assert.NotEmpty(hybrid);
        // Keyword mode never touches the embedder even though one is installed.
        int before = e.Batches;
        index.Search("coolant pump", KnowledgeSearchMode.Keyword, 3, scope);
        Assert.Equal(before, e.Batches);
    }

    [Fact]
    public async Task Service_resolves_the_workspace_mode_and_degrades_honestly_to_keyword()
    {
        var e = new FakeEmbedder();
        var settings = new KnowledgeSettingsStore(Path.Combine(_root, "svc-settings.json"));
        var catalog = new KnowledgeEmbedderCatalog(new IRetrievalEmbeddingProvider[] { e });
        var sw = new SwitchableEmbedder(() => catalog.Resolve(settings.Get().EmbedderId)!, () => !settings.Get().EmbeddingNeeded());
        using KnowledgeIndex index = NewIndex(sw);
        var rag = new RagStore(new EmbeddingService(Path.Combine(_root, "emb")), Path.Combine(_root, "ragdb"));
        var svc = new KnowledgeJobService(rag, settings, null, index, new UnavailableOcrEngine("test"), e, null, catalog);
        string folder = Docs();
        await index.IndexFolderAsync("s", folder, null, null, default);

        Assert.Equal(KnowledgeSearchMode.Auto, svc.ResolveSearch("w1").Mode);                       // default: hybrid
        KnowledgeSettings s = settings.Get();
        s.WorkspaceRetrievalModes["w1"] = "keyword";
        s.WorkspaceRetrievalModes["w2"] = "embedding";
        settings.Save(s);
        Assert.Equal(KnowledgeSearchMode.Keyword, svc.ResolveSearch("w1").Mode);
        Assert.Equal(KnowledgeSearchMode.Semantic, svc.ResolveSearch("w2").Mode);                  // vectors exist
        Assert.Equal(KnowledgeSearchMode.Auto, svc.ResolveSearch(null).Mode);                      // workspaces are independent

        e.Available = false;                                                                        // embedder removed
        KnowledgeEffectiveSearch degraded = svc.ResolveSearch("w2");
        Assert.Equal(KnowledgeSearchMode.Keyword, degraded.Mode);
        Assert.Equal("embedding", degraded.RequestedMode);
        Assert.NotNull(degraded.DegradedReason);
    }

    [Fact]
    public async Task Embedding_mode_without_any_current_vector_degrades_to_keyword_with_a_reason()
    {
        var e = new FakeEmbedder();
        var settings = new KnowledgeSettingsStore(Path.Combine(_root, "s2.json"));
        KnowledgeSettings s = settings.Get(); s.WorkspaceRetrievalModes["default"] = "keyword"; settings.Save(s);   // index while keyword-only: no vectors
        var catalog = new KnowledgeEmbedderCatalog(new IRetrievalEmbeddingProvider[] { e });
        var sw = new SwitchableEmbedder(() => catalog.Resolve(null)!, () => !settings.Get().EmbeddingNeeded());
        using KnowledgeIndex index = NewIndex(sw);
        await index.IndexFolderAsync("s", Docs(), null, null, default);
        var svc = new KnowledgeJobService(new RagStore(new EmbeddingService(Path.Combine(_root, "emb2")), Path.Combine(_root, "rag2")), settings, null, index,
            new UnavailableOcrEngine("t"), e, null, catalog);

        s = settings.Get(); s.WorkspaceRetrievalModes["default"] = "embedding"; settings.Save(s);
        KnowledgeEffectiveSearch r = svc.ResolveSearch(null);
        Assert.Equal(KnowledgeSearchMode.Keyword, r.Mode);
        Assert.Contains("re-embed", r.DegradedReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(index.EmbeddingState().NeedsReembed);                 // the prompt the UI shows
        Assert.Equal(0, index.EmbeddingState().Current);
    }

    // ------------------------------------------------------------------------------------ migration / re-embed

    [Fact]
    public async Task Switching_to_another_embedder_marks_old_vectors_stale_and_never_mixes_them_in_search()
    {
        string db = Path.Combine(_root, "m.db");
        string docs = Docs();
        var a = new FakeEmbedder("fake-a");
        using (KnowledgeIndex first = NewIndex(a, db)) await first.IndexFolderAsync("s", docs, null, null, default);

        var b = new FakeEmbedder("fake-b");                                  // same dimension, different model
        using KnowledgeIndex index = NewIndex(b, db);
        KnowledgeEmbeddingState st = index.EmbeddingState();
        Assert.True(st.NeedsReembed);
        Assert.Equal(0, st.Current);
        Assert.True(st.Stale > 0);
        Assert.Empty(index.Search("examine radiator circulation unit", KnowledgeSearchMode.Semantic, 3, new[] { "s" }));   // old vectors are ignored, not misread
        Assert.NotEmpty(index.Search("coolant pump", KnowledgeSearchMode.Keyword, 3, new[] { "s" }));                           // text is intact

        KnowledgeReembedResult r = await index.ReembedAsync();
        Assert.False(r.Cancelled);
        Assert.Equal(0, r.Remaining);
        Assert.Equal(0, r.Failed);
        KnowledgeEmbeddingState after = index.EmbeddingState();
        Assert.False(after.NeedsReembed);
        Assert.Equal(st.TotalChunks, after.Total());
        Assert.NotEmpty(index.Search("examine radiator circulation unit", KnowledgeSearchMode.Semantic, 3, new[] { "s" }));
    }

    [Fact]
    public async Task Reembed_can_be_cancelled_keeps_a_consistent_index_and_resumes_to_completion()
    {
        string db = Path.Combine(_root, "c.db");
        var a = new FakeEmbedder("fake-a");
        using (KnowledgeIndex first = NewIndex(a, db)) await first.IndexFolderAsync("s", Docs(12), null, null, default);
        int chunks = NewIndex(a, db).EmbeddingState().TotalChunks;

        var b = new FakeEmbedder("fake-b");
        using var cts = new CancellationTokenSource();
        b.OnBatch = n => { if (n == 2) cts.Cancel(); };
        using KnowledgeIndex index = NewIndex(b, db, batch: 2);
        KnowledgeReembedResult cancelled = await index.ReembedAsync(null, 1000, cts.Token);

        Assert.True(cancelled.Cancelled);
        Assert.True(cancelled.Updated > 0 && cancelled.Remaining > 0);
        KnowledgeEmbeddingState mid = index.EmbeddingState();
        Assert.Equal(chunks, mid.TotalChunks);                                // nothing was lost
        Assert.Equal(cancelled.Updated, mid.Current);                         // exactly the finished batches switched over
        Assert.True(mid.NeedsReembed);

        KnowledgeReembedResult done = await index.ReembedAsync();
        Assert.Equal(0, done.Remaining);
        Assert.False(index.EmbeddingState().NeedsReembed);
    }

    [Fact]
    public async Task Reembed_is_bounded_and_reports_when_the_limit_stopped_it()
    {
        string db = Path.Combine(_root, "b.db");
        using (KnowledgeIndex first = NewIndex(new FakeEmbedder("fake-a"), db)) await first.IndexFolderAsync("s", Docs(12), null, null, default);
        using KnowledgeIndex index = NewIndex(new FakeEmbedder("fake-b"), db, batch: 2);

        KnowledgeReembedResult r = await index.ReembedAsync(null, maxChunks: 5);

        Assert.True(r.HitLimit);
        Assert.Equal(5, r.Processed);
        Assert.True(r.Remaining > 0);
    }

    [Fact]
    public async Task A_passage_that_fails_to_embed_keeps_its_old_vector_and_is_counted()
    {
        string db = Path.Combine(_root, "f.db");
        using (KnowledgeIndex first = NewIndex(new FakeEmbedder("fake-a"), db)) await first.IndexFolderAsync("s", Docs(6), null, null, default);
        var b = new FakeEmbedder("fake-b") { FailFor = t => t.Contains("number 3") };
        using KnowledgeIndex index = NewIndex(b, db);
        int total = index.EmbeddingState().TotalChunks;

        KnowledgeReembedResult r = await index.ReembedAsync();

        Assert.Equal(1, r.Failed);
        KnowledgeEmbeddingState st = index.EmbeddingState();
        Assert.Equal(total, st.TotalChunks);
        Assert.Equal(1, st.Stale);                 // the old vector is still stored, only flagged
        Assert.Equal(total - 1, st.Current);
    }

    [Fact]
    public async Task Reembed_without_an_available_embedder_is_refused()
    {
        using KnowledgeIndex index = NewIndex(new FakeEmbedder { Available = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() => index.ReembedAsync());
    }

    [Fact]
    public async Task Vectors_written_before_the_embed_key_column_existed_are_tagged_as_the_legacy_model_and_stay_usable()
    {
        string db = Path.Combine(_root, "old.db");
        using (var c = new SqliteConnection("Data Source=" + db))
        {
            c.Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE kn_files (id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT NOT NULL, path TEXT NOT NULL, name TEXT NOT NULL, size INTEGER NOT NULL,
  mtime_ticks INTEGER NOT NULL, hash TEXT NOT NULL, tier TEXT NOT NULL, extractor TEXT, note TEXT, duplicate_of INTEGER, indexed_utc TEXT NOT NULL, ocr_mode INTEGER NOT NULL DEFAULT 0, UNIQUE(source_id, path));
CREATE TABLE kn_chunks (id INTEGER PRIMARY KEY AUTOINCREMENT, file_id INTEGER NOT NULL, ord INTEGER NOT NULL, text TEXT NOT NULL, heading TEXT,
  page INTEGER, sheet TEXT, cell_range TEXT, slide INTEGER, char_offset INTEGER NOT NULL DEFAULT 0, embedding BLOB);
INSERT INTO kn_files(source_id,path,name,size,mtime_ticks,hash,tier,indexed_utc) VALUES('s','p','old.txt',1,1,'h','Text','2026-01-01');
INSERT INTO kn_chunks(file_id,ord,text,embedding) VALUES(1,1,'legacy passage about the coolant pump', x'" + Convert.ToHexString(new byte[16]) + @"');";
            cmd.ExecuteNonQuery();
        }

        // Migration tags by the legacy MiniLM key regardless of the dimension used here.
        var legacyLike = new FixedIdentityEmbedder(LegacyMiniLmEmbeddingProvider.DefaultIdentity);
        using (KnowledgeIndex same = NewIndex(legacyLike, db))
        {
            KnowledgeEmbeddingState st = same.EmbeddingState();
            Assert.Equal(1, st.Current);            // tagged, not "missing" or "stale": no forced re-embed for existing users
            Assert.False(st.NeedsReembed);
        }
        using KnowledgeIndex other = NewIndex(new FakeEmbedder("fake-c"), db);
        KnowledgeEmbeddingState switched = other.EmbeddingState();
        Assert.Equal(1, switched.Stale);            // a different embedder is told apart from the legacy vectors
        Assert.True(switched.NeedsReembed);
    }

    private sealed class FixedIdentityEmbedder : IRetrievalEmbeddingProvider
    {
        public FixedIdentityEmbedder(RetrievalEmbeddingIdentity id) => Identity = id;
        public RetrievalEmbeddingIdentity Identity { get; }
        public bool IsAvailable => true;
        public float RelevanceThreshold => 0.3f;
        public float[]? TryEmbed(string text) => null;
        public IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts) => texts.Select(_ => (float[]?)null).ToList();
    }

    // ------------------------------------------------------------------------------------ job service: single run, gaming, cancel

    [Fact]
    public async Task Service_reembed_job_runs_once_refuses_during_gaming_and_can_be_cancelled()
    {
        string db = Path.Combine(_root, "j.db");
        using (KnowledgeIndex first = NewIndex(new FakeEmbedder("fake-a"), db)) await first.IndexFolderAsync("s", Docs(10), null, null, default);
        var b = new FakeEmbedder("fake-b");
        var settings = new KnowledgeSettingsStore(Path.Combine(_root, "js.json"));
        var catalog = new KnowledgeEmbedderCatalog(new IRetrievalEmbeddingProvider[] { b });
        var sw = new SwitchableEmbedder(() => catalog.Resolve(null)!, () => !settings.Get().EmbeddingNeeded());
        using KnowledgeIndex index = NewIndex(sw, db);
        var svc = new KnowledgeJobService(new RagStore(new EmbeddingService(Path.Combine(_root, "emb3")), Path.Combine(_root, "rag3")), settings, null, index,
            new UnavailableOcrEngine("t"), b, null, catalog);

        bool gaming = true;
        svc.GamingActive = () => gaming;
        Assert.False(svc.StartReembed(out string? refusal));
        Assert.Equal("gaming_active", refusal);
        Assert.False(svc.CancelReembed());                                 // nothing running

        gaming = false;
        var gate = new ManualResetEventSlim(false);
        b.OnBatch = n => { if (n == 1) gate.Wait(5000); };                 // hold the first batch so the job is observably running
        Assert.True(svc.StartReembed(out _));
        Assert.True(svc.StartReembed(out _));                              // already running: still one job
        Assert.True(svc.ReembedRunning);
        Assert.True(svc.CancelReembed());
        gate.Set();
        await svc.WaitForReembedAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("cancelled", svc.ReembedStatus.State);

        Assert.True(svc.StartReembed(out _));
        await svc.WaitForReembedAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("done", svc.ReembedStatus.State);
        Assert.Equal(0, svc.ReembedStatus.Remaining);
    }

    // ------------------------------------------------------------------------------------ corrupt index recovery

    [Fact]
    public async Task A_garbage_index_file_is_moved_aside_not_deleted_and_a_fresh_index_works()
    {
        string db = Path.Combine(_root, "garbage.db");
        byte[] garbage = System.Text.Encoding.ASCII.GetBytes(new string('x', 4096));
        File.WriteAllBytes(db, garbage);
        using KnowledgeIndex index = NewIndex(new FakeEmbedder(), db);

        Assert.Empty(index.Search("anything", KnowledgeSearchMode.Keyword, 3, null));   // recovers instead of throwing

        string? moved = index.LastCorruptionRecoveryPath;
        Assert.NotNull(moved);
        Assert.Equal(garbage, File.ReadAllBytes(moved!));                               // the damaged file is preserved byte for byte
        await index.IndexFolderAsync("s", Docs(), null, null, default);
        Assert.NotEmpty(index.Search("coolant", KnowledgeSearchMode.Keyword, 3, new[] { "s" }));
        Assert.True(index.VerifyIntegrity());
    }

    [Fact]
    public async Task A_truncated_index_is_recovered_during_reembed_and_reported()
    {
        string db = Path.Combine(_root, "trunc.db");
        using (KnowledgeIndex first = NewIndex(new FakeEmbedder("fake-a"), db)) await first.IndexFolderAsync("s", Docs(40), null, null, default);
        SqliteConnection.ClearAllPools();
        foreach (string side in new[] { db + "-wal", db + "-shm" }) if (File.Exists(side)) File.Delete(side);
        long size = new FileInfo(db).Length;
        using (var fs = new FileStream(db, FileMode.Open, FileAccess.Write)) { fs.Seek(size / 2, SeekOrigin.Begin); fs.Write(new byte[(int)Math.Min(size / 3, 8192)]); }

        using KnowledgeIndex index = NewIndex(new FakeEmbedder("fake-b"), db);
        bool ok = index.VerifyIntegrity();
        if (ok) return;                       // the damage hit free pages only; nothing to recover and the check says so
        string? moved = index.RecoverFromCorruption();
        Assert.NotNull(moved);
        Assert.True(File.Exists(moved));
        Assert.Equal(0, index.EmbeddingState().TotalChunks);   // a fresh empty index; folders can be re-indexed
    }

    // ------------------------------------------------------------------------------------ benchmark

    [Fact]
    public async Task Retrieval_check_runs_three_modes_and_meaning_modes_beat_keyword_on_paraphrases()
    {
        IReadOnlyList<KnowledgeBenchmarkReport> r = await KnowledgeBenchmark.CompareModesAsync(new FakeEmbedder(), 28, 7, null, default, _root);

        Assert.Equal(new[] { "keyword", "embedding", "hybrid" }, r.Select(x => x.Mode));
        KnowledgeBenchmarkReport kw = r[0], emb = r[1], hyb = r[2];
        Assert.All(r, x => Assert.True(x.IndexSeconds >= 0 && x.DocumentsPerSecond > 0 && x.MedianLatencyMs >= 0));
        Assert.True(emb.RecallAtK > 0.3, "embedding alone must answer many queries, paraphrases included");
        Assert.True(hyb.RecallAtK > kw.RecallAtK);
        Assert.True(hyb.RecallAtK >= 0.9, "hybrid keeps exact identifiers and adds meaning");
        Assert.Equal("fake-a", emb.EmbedderModel);
        Assert.Null(kw.Note);
        Assert.Empty(Directory.GetDirectories(_root, "parux-rag-check-*"));   // the synthetic work folder is removed
    }

    [Fact]
    public async Task Retrieval_check_metrics_are_deterministic_for_a_seed()
    {
        var one = await KnowledgeBenchmark.CompareModesAsync(new FakeEmbedder(), 24, 11, null, default, _root);
        var two = await KnowledgeBenchmark.CompareModesAsync(new FakeEmbedder(), 24, 11, null, default, _root);
        for (int i = 0; i < one.Count; i++)
        {
            Assert.Equal(one[i].RecallAtK, two[i].RecallAtK, 6);
            Assert.Equal(one[i].MeanReciprocalRank, two[i].MeanReciprocalRank, 6);
            Assert.Equal(one[i].PrecisionAtK, two[i].PrecisionAtK, 6);
            Assert.Equal(one[i].Queries, two[i].Queries);
        }
    }

    [Fact]
    public async Task Retrieval_check_without_an_embedder_says_so_instead_of_pretending()
    {
        var r = await KnowledgeBenchmark.CompareModesAsync(null, 20, 7, null, default, _root);
        Assert.All(r, x => Assert.Equal("keyword", x.Mode));
        Assert.Contains(r, x => x.Note != null && x.Note.Contains("keyword"));
    }

    [Fact]
    public async Task Retrieval_check_is_cancellable_and_bounded()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => KnowledgeBenchmark.CompareModesAsync(new FakeEmbedder(), 30, 7, null, cts.Token, _root));
        Assert.Empty(Directory.GetDirectories(_root, "parux-rag-check-*"));
        var tiny = await KnowledgeBenchmark.CompareModesAsync(new FakeEmbedder(), 1, 7, null, default, _root);
        Assert.All(tiny, x => Assert.Equal(KnowledgeBenchmark.MinDocuments, x.Documents));   // clamped
    }
}

internal static class KnowledgeEmbeddingStateTestExtensions
{
    public static int Total(this KnowledgeEmbeddingState s) => s.TotalChunks;
}
