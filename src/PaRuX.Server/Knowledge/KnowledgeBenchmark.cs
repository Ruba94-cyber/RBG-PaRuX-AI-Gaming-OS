using System.Diagnostics;
using System.Text;

namespace PaRuX.Server.Knowledge;

public sealed record KnowledgeBenchmarkOptions(int Documents = 120, int TopK = 5, int Seed = 7, KnowledgeResourcePlan? Plan = null,
    IRetrievalEmbeddingProvider? Embedder = null, string Mode = KnowledgeRetrievalModes.Keyword, bool Paraphrases = false);

/// <summary>Aggregate numbers only: no document text, no paths.</summary>
public sealed record KnowledgeBenchmarkReport(
    int Documents, int Queries, int TopK, double PrecisionAtK, double RecallAtK, double MeanReciprocalRank, double NdcgAtK,
    double MedianLatencyMs, double P95LatencyMs, double IndexSeconds, double DocumentsPerSecond, string Profile,
    int NegativeQueries, int NegativeQueriesAnsweredWithNoEvidence,
    string Mode = KnowledgeRetrievalModes.Keyword, string? EmbedderModel = null, string? Note = null);

/// <summary>
/// Reproducible retrieval benchmark on a synthetic corpus generated here (never real documents). Queries are of four
/// kinds: exact identifiers, topical words, paraphrases that share no keyword with the document (only a meaning-based
/// retriever can answer these), and questions the corpus cannot answer (these must yield "not enough information").
/// Metrics come from <see cref="RetrievalEvaluationService"/> plus precision@k.
/// </summary>
public static class KnowledgeBenchmark
{
    public const int MinDocuments = 20, MaxDocuments = 300;

    private static readonly string[] Topics =
    {
        "coolant pump", "brake caliper", "battery balancer", "gearbox oil", "wiring harness", "firmware loader", "sensor calibration",
        "torque wrench", "valve timing", "heat exchanger", "fuel injector", "software release", "diagnostic session", "gateway module"
    };
    /// <summary>Same order as the topics: wording that shares no word with the topic, used by the paraphrase queries.</summary>
    public static readonly string[] TopicParaphrases =
    {
        "radiator circulation unit", "stopping clamp assembly", "cell equaliser board", "transmission lubricant", "cable bundle loom", "bootstrap updater tool", "gauge adjustment routine",
        "bolt tightening spanner", "camshaft sequencing", "thermal radiator core", "nozzle dosing part", "build deployment", "troubleshooting sitting", "bridge node"
    };
    private static readonly string[] Verbs = { "inspect", "replace", "calibrate", "measure", "document", "validate", "configure", "archive" };
    /// <summary>Same order as the verbs.</summary>
    public static readonly string[] VerbParaphrases = { "examine", "swap", "tune", "gauge", "record", "verify", "set up", "store" };

    public sealed record SyntheticCorpus(string Folder, IReadOnlyList<RetrievalEvalCase> Cases, IReadOnlyList<string> UnanswerableQueries);

    /// <summary>Writes <paramref name="documents"/> small text/markdown/csv files with unique codes; deterministic for a seed.</summary>
    public static SyntheticCorpus GenerateCorpus(string folder, int documents, int seed, bool includeParaphrases = false)
    {
        Directory.CreateDirectory(folder);
        var rng = new Random(seed);
        var cases = new List<RetrievalEvalCase>();
        var byTopicVerb = new Dictionary<(int, int), List<string>>();
        var paraphraseSeeds = new List<(int i, int topic, int verb)>();
        for (int i = 0; i < documents; i++)
        {
            int topicIndex = i % Topics.Length;
            int verbIndex = (i / Topics.Length + i) % Verbs.Length;
            string topic = Topics[topicIndex];
            string verb = Verbs[verbIndex];
            string code = "ZQ" + (1000 + i) + "_" + Convert.ToHexString(BitConverter.GetBytes(rng.Next())).Substring(0, 4);
            string unique = "marker" + (char)('a' + i % 26) + (char)('a' + i / 26 % 26) + (char)('a' + i / 676 % 26);
            string name = "doc-" + i.ToString("D4");
            var sb = new StringBuilder();
            sb.Append("# Procedure ").Append(code).Append('\n');
            sb.Append("This synthetic note explains how to ").Append(verb).Append(" the ").Append(topic).Append(" in the test workshop.\n");
            sb.Append("Reference identifier ").Append(code).Append(" and keyword ").Append(unique).Append(" belong only to this note.\n");
            for (int k = 0; k < 6; k++) sb.Append("Filler sentence ").Append(k).Append(" about generic workshop planning and scheduling.\n");
            string ext = (i % 3) switch { 0 => ".md", 1 => ".txt", _ => ".csv" };
            string text = ext == ".csv" ? "id,topic,code\n" + i + "," + topic + "," + code + "\n" + sb : sb.ToString();
            File.WriteAllText(Path.Combine(folder, name + ext), text);
            cases.Add(new RetrievalEvalCase("exact-" + i, "where is " + code + " described", new Dictionary<string, int> { [name + ext] = 3 }));
            if (i % 2 == 0)
                cases.Add(new RetrievalEvalCase("word-" + i, unique + " " + verb + " " + topic, new Dictionary<string, int> { [name + ext] = 3 }));
            if (!byTopicVerb.TryGetValue((topicIndex, verbIndex), out var list)) byTopicVerb[(topicIndex, verbIndex)] = list = new List<string>();
            list.Add(name + ext);
            if (i % 3 == 0) paraphraseSeeds.Add((i, topicIndex, verbIndex));
        }
        // Every document that matches the same topic and verb is a correct answer to the paraphrase.
        if (includeParaphrases) foreach ((int i, int t, int v) in paraphraseSeeds)
            cases.Add(new RetrievalEvalCase("para-" + i, "how do I " + VerbParaphrases[v] + " the " + TopicParaphrases[t],
                byTopicVerb[(t, v)].ToDictionary(n => n, _ => 3)));
        var unanswerable = new List<string>
        {
            "what is the torque of the quantum flux capacitor", "who won the regional chess championship in 1987",
            "describe the migration of arctic terns", "list the ingredients of the saffron risotto recipe"
        };
        return new SyntheticCorpus(folder, cases, unanswerable);
    }

    private static KnowledgeSearchMode ToSearchMode(string mode) => mode switch
    {
        KnowledgeRetrievalModes.Embedding => KnowledgeSearchMode.Semantic,
        KnowledgeRetrievalModes.Hybrid => KnowledgeSearchMode.Auto,
        _ => KnowledgeSearchMode.Keyword
    };

    public static async Task<KnowledgeBenchmarkReport> RunAsync(string workFolder, KnowledgeBenchmarkOptions? options = null, CancellationToken ct = default)
    {
        options ??= new KnowledgeBenchmarkOptions();
        string mode = KnowledgeRetrievalModes.Normalize(options.Mode);
        string? note = null;
        IRetrievalEmbeddingProvider? embedder = options.Embedder is { IsAvailable: true } ? options.Embedder : null;
        if (mode != KnowledgeRetrievalModes.Keyword && embedder == null)
        {
            note = "No search model is available: this row ran as keyword search.";
            mode = KnowledgeRetrievalModes.Keyword;
        }
        string docs = Path.Combine(workFolder, "corpus");
        SyntheticCorpus corpus = GenerateCorpus(docs, options.Documents, options.Seed, options.Paraphrases);
        var index = new KnowledgeIndex(Path.Combine(workFolder, "bench-" + mode + ".db"),
            new KnowledgeIndexOptions { EmbedChunks = embedder != null, Plan = options.Plan, EmbedBatchSize = options.Plan?.EmbedBatchSize ?? 16 }, embedder);
        Stopwatch sw = Stopwatch.StartNew();
        await index.IndexFolderAsync("bench", docs, null, null, ct).ConfigureAwait(false);
        sw.Stop();
        KnowledgeSearchMode searchMode = ToSearchMode(mode);

        IReadOnlyList<string> Retrieve(string query, int k)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<KnowledgeHit> hits = index.Search(query, searchMode, k * 3, new[] { "bench" }, ct);
            return hits.Select(h => h.FileName).Distinct().Take(k).ToList();
        }
        RetrievalEvalMetrics metrics = new RetrievalEvaluationService().Evaluate(
            "knowledge-" + mode, corpus.Cases, Retrieve, options.TopK, GC.GetTotalMemory(false));

        double precisionSum = 0;
        foreach (RetrievalEvalCase c in corpus.Cases)
        {
            IReadOnlyList<string> top = Retrieve(c.Query, options.TopK);
            precisionSum += top.Count == 0 ? 0 : (double)top.Count(d => c.RelevanceByDocumentId.ContainsKey(d)) / top.Count;
        }

        int negativeOk = 0;
        foreach (string q in corpus.UnanswerableQueries)
        {
            IReadOnlyList<KnowledgeHit> hits = index.Search(q, searchMode, 6, new[] { "bench" }, ct);
            if (DocumentChatPlanner.Plan(q, hits).Kind == DocumentChatKind.NotEnoughInformation) negativeOk++;
        }
        return new KnowledgeBenchmarkReport(options.Documents, corpus.Cases.Count, options.TopK, precisionSum / corpus.Cases.Count,
            metrics.RecallAtK, metrics.MeanReciprocalRank, metrics.NdcgAtK, metrics.MedianLatencyMilliseconds, metrics.P95LatencyMilliseconds,
            sw.Elapsed.TotalSeconds, options.Documents / Math.Max(0.001, sw.Elapsed.TotalSeconds), options.Plan?.Profile.ToString() ?? "unthrottled",
            corpus.UnanswerableQueries.Count, negativeOk, mode, embedder?.Identity.ModelId, note);
    }

    /// <summary>
    /// Runs keyword, embedding and hybrid on the same seeded corpus in a private temp folder that is always removed. Document
    /// count is clamped to <see cref="MinDocuments"/>..<see cref="MaxDocuments"/> so the check stays bounded. Cancellable.
    /// </summary>
    public static async Task<IReadOnlyList<KnowledgeBenchmarkReport>> CompareModesAsync(IRetrievalEmbeddingProvider? embedder, int documents = 60,
        int seed = 7, KnowledgeResourcePlan? plan = null, CancellationToken ct = default, string? workRoot = null)
    {
        string work = Path.Combine(workRoot ?? Path.GetTempPath(), "parux-rag-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var reports = new List<KnowledgeBenchmarkReport>();
            foreach (string mode in KnowledgeRetrievalModes.All)
            {
                ct.ThrowIfCancellationRequested();
                string folder = Path.Combine(work, mode);
                reports.Add(await RunAsync(folder, new KnowledgeBenchmarkOptions(Math.Clamp(documents, MinDocuments, MaxDocuments), 5, seed, plan, embedder, mode, Paraphrases: true), ct).ConfigureAwait(false));
            }
            return reports;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, true); } catch (Exception) { /* a temp folder left behind holds only synthetic text */ }
        }
    }
}
