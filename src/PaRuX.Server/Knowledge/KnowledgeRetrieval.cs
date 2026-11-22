namespace PaRuX.Server.Knowledge;

public static class KnowledgeSearchMethods
{
    /// <summary>Hybrid (keyword + meaning) whenever an embedder is installed; keyword only otherwise. The default.</summary>
    public const string Auto = "auto";
    /// <summary>Keyword (BM25-style full text) only; the embedder is never used for indexing or search.</summary>
    public const string Keyword = "keyword";

    public static bool IsValid(string? method) => method is Auto or Keyword;
    public static string Normalize(string? method) => string.Equals(method, Keyword, StringComparison.OrdinalIgnoreCase) ? Keyword : Auto;
}

/// <summary>
/// The embedder as the Local Knowledge index sees it: the person's choice of search method can switch it off without
/// changing which embedder is installed. When off it reports "not available", so indexing stores no vectors and Auto
/// search falls back to keyword search; nothing is loaded or downloaded.
/// </summary>
public sealed class SwitchableEmbedder : IRetrievalEmbeddingProvider
{
    private readonly Func<IRetrievalEmbeddingProvider> _resolve;
    private readonly Func<bool> _keywordOnly;

    public SwitchableEmbedder(IRetrievalEmbeddingProvider inner, Func<bool> keywordOnly)
        : this(inner == null ? throw new ArgumentNullException(nameof(inner)) : () => inner, keywordOnly) { }

    /// <summary>The inner embedder is resolved on every call, so the person's embedder choice takes effect without a restart.</summary>
    public SwitchableEmbedder(Func<IRetrievalEmbeddingProvider> resolve, Func<bool> keywordOnly)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _keywordOnly = keywordOnly ?? throw new ArgumentNullException(nameof(keywordOnly));
    }

    public IRetrievalEmbeddingProvider Inner => _resolve();
    public RetrievalEmbeddingIdentity Identity => _resolve().Identity;
    public bool IsAvailable => !_keywordOnly() && _resolve().IsAvailable;
    public float RelevanceThreshold => _resolve().RelevanceThreshold;
    public float[]? TryEmbed(string text) => _keywordOnly() ? null : _resolve().TryEmbed(text);
    public IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts) =>
        _keywordOnly() ? texts.Select(_ => (float[]?)null).ToList() : _resolve().TryEmbedBatch(texts);
}

public sealed record KnowledgeSearchChoice(string Id, string Label, string Description, bool Selected);

public sealed record KnowledgeRetrievalStatus(
    string Method,
    string Mode,
    string Reason,
    bool EmbedderInstalled,
    string? EmbedderModel,
    int? EmbedderDimensions,
    bool CanInstallEmbedder,
    bool OfflineBlocked,
    bool RerankerInstalled,
    IReadOnlyList<KnowledgeSearchChoice> Choices,
    string Note);

public static class KnowledgeRetrieval
{
    /// <summary>What search will actually do right now, and why, in words a person can act on.</summary>
    public static KnowledgeRetrievalStatus Describe(
        string? method, IRetrievalEmbeddingProvider? embedder, bool rerankerInstalled, bool offlineBlocked)
    {
        string chosen = KnowledgeSearchMethods.Normalize(method);
        IRetrievalEmbeddingProvider? raw = embedder is SwitchableEmbedder s ? s.Inner : embedder;
        bool installed = raw is { IsAvailable: true };
        bool hybrid = chosen == KnowledgeSearchMethods.Auto && installed;

        string reason = hybrid
            ? "Hybrid search: keyword matching plus meaning-based matching with " + raw!.Identity.ModelId + ". An exact code or identifier still wins."
            : chosen == KnowledgeSearchMethods.Keyword
                ? "Keyword search only, as you chose. Meaning-based matching is off, so a question must share words with the passage."
                : offlineBlocked
                    ? "Keyword search only: the search model is not installed and strict offline mode blocks installing it."
                    : "Keyword search only: the search model is not installed yet. Install it (a hash-checked download) to add meaning-based matching.";

        return new KnowledgeRetrievalStatus(
            chosen, hybrid ? "hybrid" : "keyword", reason, installed,
            raw?.Identity.ModelId, raw?.Identity.Dimensions,
            CanInstallEmbedder: !installed && !offlineBlocked, offlineBlocked, rerankerInstalled,
            new[]
            {
                new KnowledgeSearchChoice(KnowledgeSearchMethods.Auto, "Automatic",
                    "Keyword plus meaning-based matching when the search model is installed; keyword only until then.", chosen == KnowledgeSearchMethods.Auto),
                new KnowledgeSearchChoice(KnowledgeSearchMethods.Keyword, "Keyword only",
                    "Never uses the search model. Fastest to index and fully predictable.", chosen == KnowledgeSearchMethods.Keyword)
            },
            "Passages indexed while Keyword only was on have no meaning vectors; re-index a folder to add them after switching back.");
    }
}
