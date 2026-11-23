using System.Text.Json;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The multilingual embedding provider and its Unigram tokenizer. Algorithm tests use a tiny invented vocabulary; the comparison with the
/// reference tokenizer and the real-model checks need files that are not in the repository (downloaded model, golden ids produced by the
/// Hugging Face "tokenizers" library) and are skipped when those are absent.
/// </summary>
public sealed class MultilingualEmbeddingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-e5-" + Guid.NewGuid().ToString("N"));
    public MultilingualEmbeddingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>ids: 0 &lt;s&gt;, 1 &lt;pad&gt;, 2 &lt;/s&gt;, 3 &lt;unk&gt;, then pieces. The vocabulary is padded to the minimum size the loader accepts.</summary>
    private string TinyTokenizer(params (string Piece, double Score)[] pieces)
    {
        var vocab = new List<object[]> { new object[] { "<s>", 0.0 }, new object[] { "<pad>", 0.0 }, new object[] { "</s>", 0.0 }, new object[] { "<unk>", 0.0 } };
        foreach (var (p, s) in pieces) vocab.Add(new object[] { p, s });
        for (int i = vocab.Count; i < 1100; i++) vocab.Add(new object[] { "zz" + i, -20.0 });
        string path = Path.Combine(_dir, "tokenizer-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { model = new { type = "Unigram", unk_id = 3, vocab } }));
        return path;
    }

    [Fact]
    public void The_best_scoring_segmentation_wins_and_each_word_starts_with_the_word_marker()
    {
        string path = TinyTokenizer(("\u2581ab", -2.0), ("\u2581a", -3.0), ("b", -3.0), ("\u2581abc", -9.0), ("c", -1.0), ("\u2581", -4.0));
        UnigramTokenizer t = UnigramTokenizer.Load(path);
        int[] ids = t.Encode("abc", 64);
        // candidates for the word: [▁ab, c] = -3.0, [▁a, b, c] = -7.0, [▁abc] = -9.0  -> the first one
        Assert.Equal(new[] { 0, 4, 8, 2 }, ids);
        Assert.Equal(0, ids[0]); Assert.Equal(2, ids[^1]);
    }

    [Fact]
    public void Unknown_characters_become_the_unknown_token_and_do_not_stop_the_word()
    {
        UnigramTokenizer t = UnigramTokenizer.Load(TinyTokenizer(("\u2581a", -3.0), ("b", -3.0)));
        int[] ids = t.Encode("aXb", 64);
        Assert.Equal(new[] { 0, 4, 3, 5, 2 }, ids);       // ▁a, <unk> for X, b
    }

    [Fact]
    public void White_space_is_folded_and_compatibility_characters_are_normalised()
    {
        UnigramTokenizer t = UnigramTokenizer.Load(TinyTokenizer(("\u2581a", -3.0), ("\u2581b", -3.0), ("\u2581ab", -9.0)));
        Assert.Equal(t.Encode("a b", 64), t.Encode("  a \t\r\n  b  ", 64));
        Assert.Equal(t.Encode("a", 64), t.Encode("\uFF41", 64));          // full-width "a" is NFKC-equivalent to "a"
        Assert.Equal(new[] { 0, 2 }, t.Encode("", 64));
        Assert.Equal(new[] { 0, 2 }, t.Encode("   ", 64));
    }

    [Fact]
    public void Long_input_is_truncated_but_always_closed_with_the_end_marker()
    {
        UnigramTokenizer t = UnigramTokenizer.Load(TinyTokenizer(("\u2581a", -3.0)));
        int[] ids = t.Encode(string.Join(' ', Enumerable.Repeat("a", 500)), 16);
        Assert.Equal(16, ids.Length); Assert.Equal(0, ids[0]); Assert.Equal(2, ids[^1]);
    }

    [Fact]
    public void A_file_that_is_not_a_unigram_tokenizer_is_rejected_with_a_clear_error()
    {
        string path = Path.Combine(_dir, "bad.json"); File.WriteAllText(path, "{\"model\":{\"type\":\"BPE\"}}");
        Assert.Throws<InvalidDataException>(() => UnigramTokenizer.Load(path));
    }

    [Fact]
    public void The_provider_without_its_model_never_throws_and_reports_that_it_is_not_available()
    {
        using var provider = new MultilingualE5EmbeddingProvider(Path.Combine(_dir, "empty"));
        Assert.False(provider.IsAvailable);
        Assert.Null(provider.TryEmbed("domanda"));
        IReadOnlyList<float[]?> batch = provider.TryEmbedBatch(new[] { "uno", "due", "tre" });
        Assert.Equal(3, batch.Count); Assert.All(batch, v => Assert.Null(v));
        Assert.Equal("onnx-multilingual-e5-small", provider.Identity.ProviderId);
        Assert.Equal(384, provider.Identity.Dimensions);
    }

    [Fact]
    public void A_damaged_model_file_is_reported_not_thrown_and_is_not_retried_on_every_call()
    {
        string root = Path.Combine(_dir, "damaged"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "model.onnx"), "not an onnx model"); File.WriteAllText(Path.Combine(root, "tokenizer.json"), "{}");
        using var provider = new MultilingualE5EmbeddingProvider(root);
        Assert.True(provider.IsAvailable);
        Assert.Null(provider.TryEmbed("x")); Assert.Null(provider.TryEmbed("y"));
        Assert.False(provider.IsLoaded);
        Assert.Contains("Failed to load", File.ReadAllText(Path.Combine(root, "last-install-error.txt")));
    }

    [Fact]
    public void The_pinned_manifest_names_an_immutable_revision_and_a_digest_for_every_file()
    {
        PinnedModelManifest m = PinnedModelManifest.MultilingualE5SmallInt8;
        Assert.Matches("^[0-9a-f]{40}$", m.Revision);
        Assert.All(m.Files, f => { Assert.NotNull(f.Sha256); Assert.Matches("^[0-9a-f]{64}$", f.Sha256!); Assert.True(f.Size > 0); });
        Assert.Contains(m.Files, f => f.LocalName == "model.onnx"); Assert.Contains(m.Files, f => f.LocalName == "tokenizer.json");
        Assert.StartsWith("https://huggingface.co/Xenova/multilingual-e5-small/resolve/" + m.Revision + "/", m.UrlFor(m.Files[0]));
    }

    // ---- real files (skipped when absent) -------------------------------------------------------------------------------------
    private static string? RealTokenizerJson()
    {
        string f = Path.Combine(Environment.GetEnvironmentVariable("PARUX_E5_DIR") ?? @"C:\Users\DTS\Desktop\Parux\models\e5", "tokenizer.json");
        return File.Exists(f) && new FileInfo(f).Length > 1_000_000 ? f : null;
    }

    [Fact]
    public void The_tokenizer_matches_the_reference_implementation_on_italian_and_english_text()
    {
        string? tok = RealTokenizerJson();
        string golden = Environment.GetEnvironmentVariable("PARUX_TOKENIZER_GOLDEN") ?? @"C:\Users\DTS\Desktop\Parux\retrieval-eval\tokenizer-golden.json";
        if (tok == null || !File.Exists(golden)) return;
        UnigramTokenizer t = UnigramTokenizer.Load(tok);
        var cases = JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(golden))!;
        var mismatches = new List<string>();
        foreach (GoldenCase c in cases)
        {
            int[] mine = t.Encode(c.text, 100000);
            if (!mine.SequenceEqual(c.ids)) mismatches.Add($"[{c.text[..Math.Min(60, c.text.Length)].Replace("\n", " ")}] mine={string.Join(',', mine.Take(12))} ref={string.Join(',', c.ids.Take(12))}");
        }
        string outDir = Environment.GetEnvironmentVariable("PARUX_CAN_PROBE_OUT") ?? "";
        if (outDir.Length > 0) { Directory.CreateDirectory(outDir); File.WriteAllText(Path.Combine(outDir, "tokenizer-mismatches.txt"), string.Join("\n", mismatches)); }
        // Italian, English, technical strings, hex dumps, compatibility characters and CJK all match the reference exactly.
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {cases.Count} differ: " + string.Join(" | ", mismatches.Take(3)));
    }

    private sealed record GoldenCase(string text, int[] ids);

    [Fact]
    public void The_multilingual_reranker_without_its_model_never_throws_and_the_fallback_uses_the_legacy_one()
    {
        using var provider = new MultilingualRerankerProvider(Path.Combine(_dir, "no-rerank"));
        Assert.False(provider.IsAvailable);
        Assert.All(provider.TryScoreBatch("q", new[] { "a", "b" }), s => Assert.Null(s));
        Assert.Matches("^[0-9a-f]{64}$", PinnedModelManifest.MultilingualRerankerInt8.Files[0].Sha256!);
        Assert.Equal(40, PinnedModelManifest.MultilingualRerankerInt8.Revision.Length);
    }

    [Fact]
    public void The_real_multilingual_reranker_prefers_the_passage_that_answers_an_italian_question_over_english_evidence()
    {
        string root = Environment.GetEnvironmentVariable("PARUX_RERANK_DIR") ?? @"C:\Users\DTS\Desktop\Parux\models\rerank";
        if (!File.Exists(Path.Combine(root, "model.onnx")) || !File.Exists(Path.Combine(root, "tokenizer.json"))) return;
        using var provider = new MultilingualRerankerProvider(root);
        var scores = provider.TryScoreBatch("Qual e' il periodo di invio del messaggio?", new[]
        {
            "Cycle time (transmission period): 100 ms for this message.",
            "The coolant temperature sensor reports a plausible value.",
            "Penalty of 2.5 percent for each week of delay."
        });
        Assert.All(scores, s => Assert.NotNull(s));
        Assert.True(scores[0] > scores[1] && scores[0] > scores[2]);
    }

    [Fact]
    public void The_real_model_places_an_italian_question_closer_to_its_english_passage_than_to_unrelated_text()
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("PARUX_E5_DIR") ?? @"C:\Users\DTS\Desktop\Parux\models\e5", "");
        if (!File.Exists(Path.Combine(root, "model.onnx")) || !File.Exists(Path.Combine(root, "tokenizer.json"))) return;
        using var p = new MultilingualE5EmbeddingProvider(root);
        float[]? q = p.TryEmbed("Chi e' il mittente del messaggio?");
        var docs = p.TryEmbedBatch(new[] { "From: Travis Ferguson. The sender of this e-mail message is Travis.", "The coolant temperature sensor circuit reports a plausible value.", "Penalty of 2.5 percent for each week of delay." });
        Assert.NotNull(q); Assert.All(docs, d => Assert.NotNull(d));
        Assert.Equal(384, q!.Length);
        Assert.InRange(q.Sum(x => x * x), 0.99f, 1.01f);                     // L2-normalised
        float Dot(float[] a, float[] b) => a.Zip(b, (x, y) => x * y).Sum();
        Assert.True(Dot(q, docs[0]!) > Dot(q, docs[1]!) && Dot(q, docs[0]!) > Dot(q, docs[2]!));
        Assert.True(p.IsLoaded); Assert.True(p.UnloadIfIdle(TimeSpan.Zero)); Assert.False(p.IsLoaded);   // gives the memory back
    }
}
