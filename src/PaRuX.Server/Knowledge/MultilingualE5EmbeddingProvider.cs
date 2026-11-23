using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PaRuX.Contracts;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Multilingual sentence embeddings for document search (intfloat/multilingual-e5-small, int8 ONNX, 384 dimensions, MIT licence).
/// The previous default (all-MiniLM-L6-v2) was trained mostly on English: an Italian question found the right English passage as
/// its first hit in only 14% of the measured cases, against 57% for this model (and 60% against 90% for Italian over Italian).
///
/// e5 models need a role prefix: "query: " for the question, "passage: " for every indexed text. The retrieval layer already separates
/// the two (TryEmbed for questions, TryEmbedBatch for passages), so the prefix is added here and nothing above changes.
///
/// Behaviour contract shared with the other providers: never throws, returns null when the engine is missing or a call fails
/// (callers then fall back to keyword search), loads on first use and releases its memory after a period without requests.
/// Files live under ai-runtimes/embeddings-e5 and are verified against pinned SHA-256 digests before they are accepted.
/// </summary>
public sealed class MultilingualE5EmbeddingProvider : IRetrievalEmbeddingProvider, IUnloadableModel, IDisposable
{
    public const string ProviderId = "onnx-multilingual-e5-small";
    public const int Dimensions = 384;
    private const int MaxTokens = 512;
    private const int BatchSize = 8;

    public static RetrievalEmbeddingIdentity DefaultIdentity { get; } = new(
        ProviderId, "intfloat/multilingual-e5-small", "xenova-int8-761b726", Dimensions, "e5-query-passage-prefixes");

    private readonly string _root;
    private readonly object _gate = new();
    private readonly IdleModelUnloader _unloader;
    private InferenceSession? _session;
    private UnigramTokenizer? _tokenizer;
    private string[] _inputNames = Array.Empty<string>();
    private bool _loadAttempted;
    private int _inFlight;
    private long _lastUsedTicks = DateTime.UtcNow.Ticks;

    public MultilingualE5EmbeddingProvider() : this(DefaultRoot) { }

    internal MultilingualE5EmbeddingProvider(string runtimeRoot)
    {
        _root = runtimeRoot;
        // A Corporate PC gives the memory back sooner; a desktop keeps the model warm for a few minutes between questions.
        _unloader = new IdleModelUnloader(this, () => CorporateMode.IsActive ? TimeSpan.FromSeconds(120) : TimeSpan.FromMinutes(10));
    }

    private static string DefaultRoot => Path.Combine(PaRuXDataRoot.Path, "ai-runtimes", "embeddings-e5");
    private string ModelPath => Path.Combine(_root, "model.onnx");
    private string TokenizerPath => Path.Combine(_root, "tokenizer.json");
    private string LastErrorPath => Path.Combine(_root, "last-install-error.txt");

    public RetrievalEmbeddingIdentity Identity => DefaultIdentity;
    public bool IsAvailable => File.Exists(ModelPath) && File.Exists(TokenizerPath);
    public bool IsLoaded => _session != null;
    public DateTime LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);

    /// <summary>Cosine floor below which a hit is noise. Calibrated on 30 unambiguous questions over the evaluation corpus (int8 model):
    /// the weakest correct passage scored 0.495 (10th percentile 0.561, median 0.666), while the typical unrelated passage among the top 100
    /// scored 0.43 (median). 0.45 removes the noise without losing a correct passage; ranking and the reranker do the rest.</summary>
    public float RelevanceThreshold => 0.45f;

    /// <summary>Status for the UI and the package smoke test. With <paramref name="probe"/> one real embedding is computed first, so a model that is present but cannot run is reported.</summary>
    public object GetStatus(bool probe = false)
    {
        int? probeDimensions = probe ? TryEmbed("probe")?.Length : null;
        return new { installed = IsAvailable, loaded = IsLoaded, dimensions = Dimensions, model = Identity.ModelId, probeDimensions, lastError = SafeRead(LastErrorPath) };
    }

    public float[]? TryEmbed(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Embed(new[] { "query: " + text })[0];
    }

    public IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts)
    {
        var result = new List<float[]?>(texts.Count);
        for (int s = 0; s < texts.Count; s += BatchSize)
        {
            var slice = new List<string>(BatchSize);
            for (int i = s; i < Math.Min(texts.Count, s + BatchSize); i++) slice.Add("passage: " + (texts[i] ?? string.Empty));
            result.AddRange(Embed(slice));
        }
        return result;
    }

    /// <summary>Downloads the pinned files (refused when strict offline / Corporate mode forbids the network).</summary>
    public async Task<string> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        try
        {
            await new PinnedModelDownloader().DownloadAsync(PinnedModelManifest.MultilingualE5SmallInt8, _root, progress, ct).ConfigureAwait(false);
            lock (_gate) { ReleaseLocked(); _loadAttempted = false; }
            try { File.Delete(LastErrorPath); } catch (IOException) { }
            return "Multilingual embedding engine installed at " + _root;
        }
        catch (Exception ex)
        {
            try { await File.WriteAllTextAsync(LastErrorPath, ex.Message, ct).ConfigureAwait(false); } catch (IOException) { }
            throw;
        }
    }

    public bool UnloadIfIdle(TimeSpan idle)
    {
        lock (_gate)
        {
            if (_session == null || Volatile.Read(ref _inFlight) > 0 || DateTime.UtcNow - LastUsedUtc < idle) return false;
            ReleaseLocked();
            _loadAttempted = false;
            return true;
        }
    }

    private float[]?[] Embed(IReadOnlyList<string> texts)
    {
        var output = new float[]?[texts.Count];
        Interlocked.Increment(ref _inFlight);
        try
        {
            if (!TryEnsureLoaded()) return output;
            Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
            var ids = texts.Select(t => _tokenizer!.Encode(t, MaxTokens)).ToArray();
            int batch = ids.Length, width = ids.Max(a => a.Length);
            var inputIds = new DenseTensor<long>(new[] { batch, width });
            var mask = new DenseTensor<long>(new[] { batch, width });
            var types = new DenseTensor<long>(new[] { batch, width });
            for (int b = 0; b < batch; b++)
                for (int t = 0; t < width; t++)
                {
                    bool real = t < ids[b].Length;
                    inputIds[b, t] = real ? ids[b][t] : _tokenizer!.PadId;
                    mask[b, t] = real ? 1 : 0;
                }
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask)
            };
            if (_inputNames.Contains("token_type_ids")) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> run = _session!.Run(inputs);
            Tensor<float> hidden = run.First().AsTensor<float>();
            for (int b = 0; b < batch; b++)
            {
                var pooled = new float[Dimensions]; int real = ids[b].Length;
                for (int t = 0; t < real; t++)
                    for (int d = 0; d < Dimensions; d++) pooled[d] += hidden[b, t, d];
                double sum = 0;
                for (int d = 0; d < Dimensions; d++) { pooled[d] /= real; sum += (double)pooled[d] * pooled[d]; }
                float norm = (float)Math.Sqrt(sum);
                if (norm > 1e-12f) for (int d = 0; d < Dimensions; d++) pooled[d] /= norm;
                output[b] = pooled;
            }
            return output;
        }
        catch (Exception)
        {
            return new float[]?[texts.Count];       // keyword search takes over; never an error for the caller
        }
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
            if (_loadAttempted) return false;            // a damaged file is not retried on every request
            _loadAttempted = true;
            try
            {
                _tokenizer = UnigramTokenizer.Load(TokenizerPath);
                var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
                int threads = Math.Max(1, Environment.ProcessorCount / 2);
                options.IntraOpNumThreads = threads; options.InterOpNumThreads = 1;
                _session = new InferenceSession(ModelPath, options);
                _inputNames = _session.InputMetadata.Keys.ToArray();
                return true;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(LastErrorPath, "Failed to load the multilingual embedding engine: " + ex.Message); } catch (IOException) { }
                ReleaseLocked();
                return false;
            }
        }
    }

    private void ReleaseLocked() { _session?.Dispose(); _session = null; _tokenizer = null; }

    private static string? SafeRead(string path) { try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch (IOException) { return null; } }

    public void Dispose() { _unloader.Dispose(); lock (_gate) ReleaseLocked(); }
}
