namespace PaRuX.Server.Knowledge;

/// <summary>Per-workspace retrieval modes. Keyword = full text only; Embedding = meaning vectors only; Hybrid = both fused (exact identifiers still win).</summary>
public static class KnowledgeRetrievalModes
{
    public const string Keyword = "keyword";
    public const string Embedding = "embedding";
    public const string Hybrid = "hybrid";
    public const string DefaultWorkspace = "default";

    public static readonly string[] All = { Keyword, Embedding, Hybrid };

    public static bool IsValid(string? mode) => mode != null && All.Contains(mode.Trim().ToLowerInvariant());
    public static string Normalize(string? mode) => IsValid(mode) ? mode!.Trim().ToLowerInvariant() : Hybrid;
    public static string WorkspaceKey(string? workspace) => string.IsNullOrWhiteSpace(workspace) ? DefaultWorkspace : workspace.Trim();
}

public sealed record KnowledgeEmbedderOption(string Id, string Label, string Model, int Dimensions, bool Installed, bool Selected, string Key);

/// <summary>The embedders the Core can really use (every registered <see cref="IRetrievalEmbeddingProvider"/>); nothing is listed that cannot run.</summary>
public sealed class KnowledgeEmbedderCatalog
{
    private readonly IReadOnlyList<IRetrievalEmbeddingProvider> _providers;

    public KnowledgeEmbedderCatalog(IEnumerable<IRetrievalEmbeddingProvider> providers) =>
        _providers = (providers ?? Array.Empty<IRetrievalEmbeddingProvider>())
            .GroupBy(p => p.Identity.ProviderId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    public bool IsEmpty => _providers.Count == 0;
    public static string IdOf(IRetrievalEmbeddingProvider p) => p.Identity.ProviderId;

    /// <summary>The chosen provider, else the first installed one, else the first listed; null when none exist.</summary>
    public IRetrievalEmbeddingProvider? Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var chosen = _providers.FirstOrDefault(p => string.Equals(IdOf(p), id, StringComparison.OrdinalIgnoreCase));
            if (chosen != null) return chosen;
        }
        return _providers.FirstOrDefault(p => p.IsAvailable) ?? _providers.FirstOrDefault();
    }

    public IReadOnlyList<KnowledgeEmbedderOption> Options(string? selectedId)
    {
        string? selected = Resolve(selectedId) is { } r ? IdOf(r) : null;
        return _providers.Select(p => new KnowledgeEmbedderOption(IdOf(p), p.Identity.ModelId, p.Identity.ModelId, p.Identity.Dimensions,
            p.IsAvailable, string.Equals(IdOf(p), selected, StringComparison.OrdinalIgnoreCase), p.Identity.StableKey)).ToList();
    }
}

/// <summary>How the index's vectors relate to the embedder in use. Vectors are never discarded to resolve a mismatch.</summary>
public sealed record KnowledgeEmbeddingState(
    bool EmbedderAvailable, string? EmbedderKey, int TotalChunks, int Current, int Stale, int Missing)
{
    /// <summary>True when an embedder is usable and some passages have no vector from it (a different model's vector counts as stale).</summary>
    public bool NeedsReembed => EmbedderAvailable && Stale + Missing > 0;
}

public sealed record KnowledgeReembedProgress(string State, int Processed, int Updated, int Failed, int Total);

public sealed record KnowledgeReembedResult(int Processed, int Updated, int Failed, int Remaining, bool Cancelled, bool HitLimit, bool RecoveredFromCorruption);

/// <summary>What a search will use for a workspace after honest fallbacks.</summary>
public sealed record KnowledgeEffectiveSearch(KnowledgeSearchMode Mode, string EffectiveMode, string RequestedMode, string? DegradedReason);
