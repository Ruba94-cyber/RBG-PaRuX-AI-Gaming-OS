using System.Text;
using System.Text.Json;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// SentencePiece-Unigram tokenizer read from a Hugging Face "tokenizer.json" (the format shipped with XLM-RoBERTa derived models such as
/// multilingual-e5). It needs no native library and no extra package: the vocabulary is a list of (piece, log-probability) pairs whose
/// position is the model's token id, and the best segmentation of each word is found with the Viterbi algorithm.
///
/// Pipeline (identical in shape to the published tokenizer): NFKC normalisation, whitespace folded to single spaces, every word gets the
/// word-start marker "▁", each word is segmented on its own, then the ids are wrapped as "&lt;s&gt; ... &lt;/s&gt;". Verified against the reference
/// implementation on a set of Italian / English / mixed strings (see UnigramTokenizerTests).
/// </summary>
internal sealed class UnigramTokenizer
{
    private const char WordStart = '▁';
    private readonly string[] _pieces;
    private readonly double[] _scores;
    private readonly Dictionary<string, int> _index;
    private readonly int _unk, _bos, _eos;
    private readonly int _maxPieceCodePoints;
    private readonly double _unkScore;

    public int BosId => _bos;
    public int EosId => _eos;
    public int PadId { get; }
    public int VocabularySize => _pieces.Length;

    private UnigramTokenizer(string[] pieces, double[] scores, int unk, int bos, int eos, int pad)
    {
        _pieces = pieces; _scores = scores; _unk = unk; _bos = bos; _eos = eos; PadId = pad;
        _index = new Dictionary<string, int>(pieces.Length, StringComparer.Ordinal);
        int max = 1; double min = 0;
        for (int i = 0; i < pieces.Length; i++)
        {
            if (i == unk || i == bos || i == eos || i == pad) continue;       // special tokens are never produced by segmentation
            _index.TryAdd(pieces[i], i);
            int cps = CodePointCount(pieces[i]); if (cps > max) max = cps;
            if (scores[i] < min) min = scores[i];
        }
        _maxPieceCodePoints = Math.Min(max, 40);
        _unkScore = min - 10;                                                  // same rule as the reference implementation
    }

    public static UnigramTokenizer Load(string tokenizerJsonPath)
    {
        using FileStream fs = File.OpenRead(tokenizerJsonPath);
        using JsonDocument doc = JsonDocument.Parse(fs);
        JsonElement model = doc.RootElement.GetProperty("model");
        if (!string.Equals(model.GetProperty("type").GetString(), "Unigram", StringComparison.Ordinal))
            throw new InvalidDataException("tokenizer.json is not a Unigram tokenizer.");
        JsonElement vocab = model.GetProperty("vocab");
        int n = vocab.GetArrayLength();
        if (n < 1000 || n > 2_000_000) throw new InvalidDataException("Unexpected vocabulary size.");
        var pieces = new string[n]; var scores = new double[n];
        int i = 0;
        foreach (JsonElement e in vocab.EnumerateArray()) { pieces[i] = e[0].GetString() ?? string.Empty; scores[i] = e[1].GetDouble(); i++; }
        int unk = model.TryGetProperty("unk_id", out JsonElement u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 3;
        int bos = Array.IndexOf(pieces, "<s>"), eos = Array.IndexOf(pieces, "</s>"), pad = Array.IndexOf(pieces, "<pad>");
        if (bos < 0 || eos < 0 || pad < 0) throw new InvalidDataException("Special tokens are missing from the vocabulary.");
        return new UnigramTokenizer(pieces, scores, unk, bos, eos, pad);
    }

    /// <summary>Token ids for <paramref name="text"/> including the start and end markers; never longer than <paramref name="maxTokens"/>.</summary>
    public int[] Encode(string text, int maxTokens)
    {
        if (maxTokens < 2) maxTokens = 2;
        var ids = new List<int>(64) { _bos };
        string normalised = Normalise(text ?? string.Empty);
        if (normalised.Length > 0)
        {
            foreach (string word in normalised.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                SegmentWord(WordStart + word, ids);
                if (ids.Count >= maxTokens - 1) break;
            }
            // The reference keeps one trailing space as a lone word marker (passages cut at a fixed length often end with one).
            if (normalised.EndsWith(' ') && ids.Count < maxTokens - 1 && _index.TryGetValue(WordStart.ToString(), out int marker)) ids.Add(marker);
        }
        if (ids.Count > maxTokens - 1) ids.RemoveRange(maxTokens - 1, ids.Count - (maxTokens - 1));
        ids.Add(_eos);
        return ids.ToArray();
    }

    /// <summary>NFKC, every kind of white space becomes a single space, control characters are dropped, leading space is removed (one trailing space is kept).</summary>
    internal static string Normalise(string text)
    {
        string s;
        try { s = text.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { s = text; }
        var sb = new StringBuilder(s.Length);
        bool pendingSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c) || c == '​') { pendingSpace = sb.Length > 0; continue; }
            if (char.IsControl(c)) continue;
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        if (pendingSpace) sb.Append(' ');
        return sb.ToString();
    }

    private void SegmentWord(string word, List<int> output)
    {
        // boundaries of the Unicode code points inside the word (a surrogate pair is one code point)
        var starts = new List<int>(word.Length + 1);
        for (int p = 0; p < word.Length; p++) { starts.Add(p); if (char.IsHighSurrogate(word[p]) && p + 1 < word.Length && char.IsLowSurrogate(word[p + 1])) p++; }
        int m = starts.Count; starts.Add(word.Length);
        var best = new double[m + 1]; var prev = new int[m + 1]; var piece = new int[m + 1];
        Array.Fill(best, double.NegativeInfinity); best[0] = 0;
        for (int i = 0; i < m; i++)
        {
            if (double.IsNegativeInfinity(best[i])) continue;
            bool single = false;
            int maxLen = Math.Min(_maxPieceCodePoints, m - i);
            for (int len = 1; len <= maxLen; len++)
            {
                string sub = word.Substring(starts[i], starts[i + len] - starts[i]);
                if (!_index.TryGetValue(sub, out int id)) continue;
                if (len == 1) single = true;
                double cand = best[i] + _scores[id];
                if (cand > best[i + len]) { best[i + len] = cand; prev[i + len] = i; piece[i + len] = id; }
            }
            if (!single)
            {
                double cand = best[i] + _unkScore;
                if (cand > best[i + 1]) { best[i + 1] = cand; prev[i + 1] = i; piece[i + 1] = _unk; }
            }
        }
        var rev = new List<int>();
        for (int pos = m; pos > 0; pos = prev[pos]) rev.Add(piece[pos]);
        for (int k = rev.Count - 1; k >= 0; k--) output.Add(rev[k]);
    }

    private static int CodePointCount(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++) { n++; if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++; }
        return n;
    }
}
