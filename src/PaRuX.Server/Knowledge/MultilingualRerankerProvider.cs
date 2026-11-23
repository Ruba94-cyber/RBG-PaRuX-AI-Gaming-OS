using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PaRuX.Contracts;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Multilingual cross-encoder (mmarco-mMiniLMv2-L12, int8 ONNX) used to re-rank document-search candidates. The previous reranker was trained
/// on English only and demoted the right passage when question and evidence were in different languages (Italian question, English file).
/// Pair layout of XLM-R models: "&lt;s&gt; query &lt;/s&gt;&lt;/s&gt; passage &lt;/s&gt;". Never throws: returns null scores when the engine is
/// missing or a call fails; <see cref="FallbackRerankerProvider"/> then uses the legacy English reranker.
/// </summary>
public sealed class MultilingualRerankerProvider : IRetrievalRerankerProvider, IUnloadableModel, IDisposable
{
    public const string ProviderId = "onnx-multilingual-mmarco-crossencoder";
    private const int MaxTokensPerSide = 256;
    private const int BatchSize = 8;

    public static RetrievalRerankerIdentity DefaultIdentity { get; } = new(ProviderId, "cross-encoder/mmarco-mMiniLMv2-L12-H384-v1", "int8-avx512-1427fd6", "none");

    private readonly string _root;
    private readonly object _gate = new();
    private readonly IdleModelUnloader _unloader;
    private InferenceSession? _session;
    private UnigramTokenizer? _tokenizer;
    private string[] _inputNames = Array.Empty<string>();
    private bool _loadAttempted;
    private int _inFlight;
    private long _lastUsedTicks = DateTime.UtcNow.Ticks;

    public MultilingualRerankerProvider() : this(Path.Combine(PaRuXDataRoot.Path, "ai-runtimes", "rerank-mmarco")) { }

    internal MultilingualRerankerProvider(string runtimeRoot)
    {
        _root = runtimeRoot;
        _unloader = new IdleModelUnloader(this, () => CorporateMode.IsActive ? TimeSpan.FromSeconds(120) : TimeSpan.FromMinutes(10));
    }

    private string ModelPath => Path.Combine(_root, "model.onnx");
    private string TokenizerPath => Path.Combine(_root, "tokenizer.json");
    private string LastErrorPath => Path.Combine(_root, "last-install-error.txt");

    public RetrievalRerankerIdentity Identity => DefaultIdentity;
    public bool IsAvailable => File.Exists(ModelPath) && File.Exists(TokenizerPath);
    public bool IsLoaded => _session != null;
    public DateTime LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);

    public object GetStatus(bool probe = false)
    {
        float? score = probe ? TryScoreBatch("probe", new[] { "probe" })[0] : null;
        return new { installed = IsAvailable, loaded = IsLoaded, model = Identity.ModelId, probeScore = score, lastError = SafeRead(LastErrorPath) };
    }

    public async Task<string> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        try
        {
            await new PinnedModelDownloader().DownloadAsync(PinnedModelManifest.MultilingualRerankerInt8, _root, progress, ct).ConfigureAwait(false);
            lock (_gate) { ReleaseLocked(); _loadAttempted = false; }
            try { File.Delete(LastErrorPath); } catch (IOException) { }
            return "Multilingual reranker installed at " + _root;
        }
        catch (Exception ex)
        {
            try { await File.WriteAllTextAsync(LastErrorPath, ex.Message, ct).ConfigureAwait(false); } catch (IOException) { }
            throw;
        }
    }

    public IReadOnlyList<float?> TryScoreBatch(string query, IReadOnlyList<string> documents)
    {
        var result = new List<float?>(documents.Count);
        for (int s = 0; s < documents.Count; s += BatchSize)
        {
            var slice = new List<string>();
            for (int i = s; i < Math.Min(documents.Count, s + BatchSize); i++) slice.Add(documents[i] ?? string.Empty);
            result.AddRange(Score(query, slice));
        }
        return result;
    }

    private float?[] Score(string query, IReadOnlyList<string> docs)
    {
        var output = new float?[docs.Count];
        if (string.IsNullOrWhiteSpace(query)) return output;
        Interlocked.Increment(ref _inFlight);
        try
        {
            if (!TryEnsureLoaded()) return output;
            Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
            int[] q = _tokenizer!.Encode(query, MaxTokensPerSide);                    // <s> q </s>
            var pairs = new int[docs.Count][];
            for (int b = 0; b < docs.Count; b++)
            {
                int[] d = _tokenizer.Encode(docs[b], MaxTokensPerSide);              // <s> d </s>
                var ids = new int[q.Length + d.Length];                              // <s> q </s> </s> d </s>
                Array.Copy(q, ids, q.Length);
                ids[q.Length] = _tokenizer.EosId;
                Array.Copy(d, 1, ids, q.Length + 1, d.Length - 1);
                pairs[b] = ids;
            }
            int width = pairs.Max(a => a.Length);
            var inputIds = new DenseTensor<long>(new[] { pairs.Length, width });
            var mask = new DenseTensor<long>(new[] { pairs.Length, width });
            for (int b = 0; b < pairs.Length; b++)
                for (int t = 0; t < width; t++)
                {
                    bool real = t < pairs[b].Length;
                    inputIds[b, t] = real ? pairs[b][t] : _tokenizer.PadId;
                    mask[b, t] = real ? 1 : 0;
                }
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", inputIds), NamedOnnxValue.CreateFromTensor("attention_mask", mask) };
            if (_inputNames.Contains("token_type_ids")) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new[] { pairs.Length, width })));
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> run = _session!.Run(inputs);
            float[] logits = run.First().AsTensor<float>().ToArray();
            if (logits.Length != docs.Count) return output;
            for (int b = 0; b < logits.Length; b++) output[b] = float.IsFinite(logits[b]) ? logits[b] : null;
            return output;
        }
        catch (Exception) { return new float?[docs.Count]; }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
        }
    }

    private bool TryEnsureLoaded()
    {
        if (_session != null && _tokenizer != null) return true;
        if (!IsAvailable) return false;
        lock (_gate)
        {
            if (_session != null && _tokenizer != null) return true;
            if (_loadAttempted) return false;
            _loadAttempted = true;
            try
            {
                _tokenizer = UnigramTokenizer.Load(TokenizerPath);
                var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2), InterOpNumThreads = 1 };
                _session = new InferenceSession(ModelPath, options);
                _inputNames = _session.InputMetadata.Keys.ToArray();
                return true;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(LastErrorPath, "Failed to load the multilingual reranker: " + ex.Message); } catch (IOException) { }
                ReleaseLocked();
                return false;
            }
        }
    }

    public bool UnloadIfIdle(TimeSpan idle)
    {
        lock (_gate)
        {
            if (_session == null || Volatile.Read(ref _inFlight) > 0 || DateTime.UtcNow - LastUsedUtc < idle) return false;
            ReleaseLocked(); _loadAttempted = false; return true;
        }
    }

    private void ReleaseLocked() { _session?.Dispose(); _session = null; _tokenizer = null; }
    private static string? SafeRead(string path) { try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch (IOException) { return null; } }
    public void Dispose() { _unloader.Dispose(); lock (_gate) ReleaseLocked(); }
}

/// <summary>Uses the multilingual reranker when installed and able to score every candidate, otherwise the legacy English one.</summary>
public sealed class FallbackRerankerProvider : IRetrievalRerankerProvider
{
    private readonly MultilingualRerankerProvider _primary;
    private readonly IRetrievalRerankerProvider _fallback;
    public FallbackRerankerProvider(MultilingualRerankerProvider primary, IRetrievalRerankerProvider fallback) { _primary = primary; _fallback = fallback; }
    public RetrievalRerankerIdentity Identity => _primary.IsAvailable ? _primary.Identity : _fallback.Identity;
    public bool IsAvailable => _primary.IsAvailable || _fallback.IsAvailable;
    public IReadOnlyList<float?> TryScoreBatch(string query, IReadOnlyList<string> documents)
    {
        if (_primary.IsAvailable)
        {
            IReadOnlyList<float?> scores = _primary.TryScoreBatch(query, documents);
            if (scores.Count == documents.Count && scores.All(s => s.HasValue)) return scores;
        }
        return _fallback.TryScoreBatch(query, documents);
    }
}
