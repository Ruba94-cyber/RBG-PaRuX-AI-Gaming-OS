using System.Collections.Concurrent;
using PaRuX.Contracts;

namespace PaRuX.Server.Knowledge;

public sealed record KnowledgeJobStatus(string SourceId, string State, IndexProgress? Progress, string? Error, DateTime UpdatedUtc);

/// <summary>Lets the context engine ask the knowledge index without a constructor change (set once at start-up).</summary>
public static class KnowledgeContextProvider
{
    public static KnowledgeJobService? Current { get; set; }

    /// <summary>Position-bearing hits for chat, or null when nothing is indexed/found (the caller falls back to the classic store).</summary>
    public static IReadOnlyList<KnowledgeHit>? TrySearch(string query, int topK, IReadOnlyCollection<string>? sourceIds = null)
    {
        try
        {
            KnowledgeJobService? service = Current;
            // Corporate's authoritative position-bearing index must never
            // silently fall back to an out-of-date classic RAG chunk without
            // reliable page/revision evidence, even when no hit is found.
            if (service == null || !service.HasData)
                return CorporateMode.IsActive ? Array.Empty<KnowledgeHit>() : null;
            IReadOnlyList<KnowledgeHit> hits = service.Index.Search(
                query, KnowledgeSearchMode.Auto, topK, service.ScopedSourceIds(sourceIds));
            return hits.Count == 0 && !CorporateMode.IsActive ? null : hits;
        }
        catch (Exception)
        {
            return CorporateMode.IsActive ? Array.Empty<KnowledgeHit>() : null;
        }
    }
}

/// <summary>
/// Runs index jobs for the folders the person registered in the existing RAG source list (no second folder list). One job per
/// source, pausable and cancellable, at below-normal priority with an optional throttle so small PCs stay responsive.
/// Reads documents only; the index lives in the user's data root.
/// </summary>
public sealed partial class KnowledgeJobService : IDisposable
{
    private readonly RagStore _sources;
    private readonly KnowledgeSettingsStore _settings;
    private readonly EmbeddingService? _embeddings;
    private readonly IOcrEngine _ocr;
    private readonly IdleModelUnloader? _unloader;
    private readonly RerankerService? _reranker;
    private readonly IdleModelUnloader? _rerankerUnloader;
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, IndexControl Control, Task Task)> _jobs = new();
    private readonly ConcurrentDictionary<string, KnowledgeJobStatus> _status = new();

    public KnowledgeIndex Index { get; }
    public IRetrievalEmbeddingProvider? Embedder { get; }
    public IRetrievalRerankerProvider? Reranker { get; }
    public KnowledgeSettingsStore Settings => _settings;
    public KnowledgeEmbedderCatalog Embedders { get; }

    public KnowledgeJobService(RagStore sources, KnowledgeSettingsStore? settings = null, EmbeddingService? embeddings = null,
        KnowledgeIndex? index = null, IOcrEngine? ocr = null, IRetrievalEmbeddingProvider? embedder = null,
        IRetrievalRerankerProvider? reranker = null, KnowledgeEmbedderCatalog? embedderCatalog = null,
        RerankerService? rerankerService = null)
    {
        _sources = sources;
        _settings = settings ?? KnowledgeSettingsStore.Shared;
        _embeddings = embeddings;
        _ocr = ocr ?? new WindowsOcrEngine();
        // The person's search-method choice can switch the embedder off without uninstalling it.
        Embedders = embedderCatalog ?? new KnowledgeEmbedderCatalog(embedder == null ? Array.Empty<IRetrievalEmbeddingProvider>() : new[] { embedder });
        // Off only when every workspace is keyword-only; the embedder choice is resolved on each call.
        Embedder = Embedders.IsEmpty ? null : new SwitchableEmbedder(() => Embedders.Resolve(_settings.Get().EmbedderId)!, () => !_settings.Get().EmbeddingNeeded());
        Reranker = reranker;
        Index = index ?? CreateIndex(PaRuXDataRoot.Combine("knowledge", "knowledge.db"), Embedder, BuildOptions(), reranker);
        if (embeddings != null)
            _unloader = new IdleModelUnloader(embeddings, () => CurrentPlan().IdleUnload);
        _reranker = rerankerService;
        if (_reranker != null)
            _rerankerUnloader = new IdleModelUnloader(_reranker, () => CurrentPlan().IdleUnload);
    }

    /// <summary>
    /// The only opportunistic low-memory reclaim action available to Corporate:
    /// dispose OUR idle ONNX retrieval sessions, not documents, indexes or system
    /// processes. Do not interfere with active indexing or in-flight ONNX calls.
    /// A later query transparently reloads exactly the same installed models.
    /// </summary>
    internal static bool MayReclaimIdleRetrieval(bool corporate, bool indexingBusy) =>
        corporate && !indexingBusy;

    public int ReleaseIdleRetrievalModelsForMemoryPressure()
    {
        if (!MayReclaimIdleRetrieval(CorporateMode.IsActive,
                _jobs.Any(job => !job.Value.Task.IsCompleted)))
            return 0;
        int released = 0;
        if (_embeddings?.UnloadIfIdle(TimeSpan.Zero) == true) released++;
        if (_reranker?.UnloadIfIdle(TimeSpan.Zero) == true) released++;
        return released;
    }

    public void Dispose()
    {
        StopAutoRecovery();
        _unloader?.Dispose();
        _rerankerUnloader?.Dispose();
    }

    /// <summary>The resource plan the current settings and this PC resolve to.</summary>
    public KnowledgeResourcePlan CurrentPlan() =>
        KnowledgeResourcePlan.Resolve(_settings.Get(), KnowledgeResourcePlan.DetectTotalRam(), KnowledgeResourcePlan.DetectDedicatedGpu(), Environment.ProcessorCount);

    /// <summary>Index options for the current settings: profile limits, thread cap and OCR only when the person opted in.</summary>
    public KnowledgeIndexOptions BuildOptions()
    {
        KnowledgeSettings settings = _settings.Get();
        KnowledgeResourcePlan plan = CurrentPlan();
        if (_embeddings != null) _embeddings.ThreadLimit = plan.EmbeddingThreads;
        return new KnowledgeIndexOptions
        {
            Plan = plan,
            EmbedBatchSize = plan.EmbedBatchSize,
            Limits = ExtractionLimits.Default with { Ocr = settings.OcrEnabled ? _ocr : null, OcrLanguage = settings.OcrLanguage }
        };
    }

    /// <summary>The default index. The embedder is what makes Semantic/Hybrid search work: without it the index is lexical-only
    /// (found in the Corporate Lite measurement run: 0 semantic hits for 50 queries).</summary>
    public static KnowledgeIndex CreateIndex(string dbPath, IRetrievalEmbeddingProvider? embedder, KnowledgeIndexOptions? options = null,
        IRetrievalRerankerProvider? reranker = null) =>
        new(dbPath, options ?? new KnowledgeIndexOptions { ThrottleMilliseconds = 5 }, embedder, reranker);

    public bool HasData
    {
        get
        {
            try
            {
                // One scoped, indexed SQLite EXISTS instead of two COUNT queries
                // and a new connection for each folder in a large company corpus.
                return Index.HasAnyIndexedChunks(_sources.ListSources().Select(s => s.Id).ToArray());
            }
            catch (Exception) { return false; }
        }
    }

    public IReadOnlyCollection<string> EnabledSourceIds() => _sources.ListSources().Where(s => s.Enabled).Select(s => s.Id).ToList();

    /// <summary>The chosen sources that exist and are enabled; every enabled source when none are chosen.</summary>
    public IReadOnlyCollection<string> ScopedSourceIds(IReadOnlyCollection<string>? chosen)
    {
        IReadOnlyCollection<string> enabled = EnabledSourceIds();
        if (chosen == null || chosen.Count == 0) return enabled;
        // A large explicit scope may hold thousands of IDs. A HashSet
        // avoids a quadratic linear-list membership scan on every Chat turn.
        var selected = new HashSet<string>(chosen, StringComparer.Ordinal);
        return enabled.Where(selected.Contains).ToList();
    }

    public bool Start(string sourceId)
    {
        RagSourceDto? source = _sources.ListSources().Find(s => s.Id == sourceId);
        if (source == null) return false;
        if (SensitivePathPolicy.EvaluateFolder(source.FolderPath).Verdict == SensitiveVerdict.Blocked)
        {
            _status[sourceId] = new KnowledgeJobStatus(sourceId, "failed", null, "blocked_sensitive_path", DateTime.UtcNow);
            return false;   // a folder added before the policy existed is still refused
        }
        if (_jobs.TryGetValue(sourceId, out var running) && !running.Task.IsCompleted) return true;
        Index.UpdateOptions(BuildOptions());
        var cts = new CancellationTokenSource();
        var control = new IndexControl();
        var progress = new Progress<IndexProgress>(p => _status[sourceId] = new KnowledgeJobStatus(sourceId, p.State, p, null, DateTime.UtcNow));
        Task task = Task.Run(async () =>
        {
            try
            {
                IndexProgress final = await Index.IndexFolderAsync(sourceId, source.FolderPath, progress, control, cts.Token).ConfigureAwait(false);
                _status[sourceId] = new KnowledgeJobStatus(sourceId, "done", final, null, DateTime.UtcNow);
                KickAutoRecovery();   // documents indexed while the model could not run are repaired once it can
            }
            catch (OperationCanceledException) { _status[sourceId] = new KnowledgeJobStatus(sourceId, "cancelled", null, null, DateTime.UtcNow); }
            catch (Exception ex) { _status[sourceId] = new KnowledgeJobStatus(sourceId, "failed", null, ex.GetType().Name, DateTime.UtcNow); }
        });
        _jobs[sourceId] = (cts, control, task);
        _status[sourceId] = new KnowledgeJobStatus(sourceId, "running", null, null, DateTime.UtcNow);
        return true;
    }

    // ---------------------------------------------------------------------------------------- retrieval mode and re-embed

    /// <summary>The search a workspace gets right now: its chosen mode, degraded honestly to keyword when no usable vectors exist.</summary>
    public KnowledgeEffectiveSearch ResolveSearch(string? workspace)
    {
        string requested = _settings.Get().EffectiveMode(workspace);
        if (requested == KnowledgeRetrievalModes.Keyword) return new(KnowledgeSearchMode.Keyword, requested, requested, null);
        if (Embedder is not { IsAvailable: true })
            return new(KnowledgeSearchMode.Keyword, KnowledgeRetrievalModes.Keyword, requested, "The search model is not installed or is switched off, so keyword search is used.");
        if (requested == KnowledgeRetrievalModes.Embedding)
            return Index.HasCurrentVectors()
                ? new(KnowledgeSearchMode.Semantic, requested, requested, null)
                : new(KnowledgeSearchMode.Keyword, KnowledgeRetrievalModes.Keyword, requested, "No passage has a vector from the chosen search model yet; re-embed the index. Keyword search is used meanwhile.");
        return new(KnowledgeSearchMode.Auto, requested, requested, null);
    }

    private readonly object _reembedGate = new();
    private CancellationTokenSource? _reembedCts;
    private Task? _reembedTask;
    private volatile KnowledgeReembedStatus _reembed = new("idle", 0, 0, 0, 0, 0, null, false, DateTime.UtcNow);

    public KnowledgeReembedStatus ReembedStatus => _reembed;
    public bool ReembedRunning { get { lock (_reembedGate) return _reembedTask is { IsCompleted: false }; } }

    /// <summary>Starts the bounded background re-embed. False with a reason when it cannot start; true when started or already running.</summary>
    public bool StartReembed(out string? refusal) => StartReembedCore(false, out refusal);

    private bool StartReembedCore(bool automatic, out string? refusal)
    {
        refusal = null;
        lock (_reembedGate)
        {
            Task? predecessor = null;
            if (_reembedTask is { IsCompleted: false } running)
            {
                // A second automatic request, or any request while the person's own run is going, is the same single job.
                if (automatic || !_reembedAutomatic) return true;
                // The person asked for the full re-embed while the automatic repair runs: the repair yields (it is cancelled
                // between batches, nothing is lost) and the full run follows it, so there is still never more than one job.
                _reembedCts?.Cancel();
                predecessor = running;
            }
            if (GamingActive()) { refusal = "gaming_active"; return false; }
            if (Embedder is not { IsAvailable: true }) { refusal = "no_embedder"; return false; }
            Index.UpdateOptions(BuildOptions());
            var cts = new CancellationTokenSource();
            _reembedCts = cts;
            _reembedAutomatic = automatic;
            _reembed = new KnowledgeReembedStatus("running", 0, 0, 0, 0, 0, null, false, DateTime.UtcNow, automatic);
            var progress = new Progress<KnowledgeReembedProgress>(p =>
                _reembed = new KnowledgeReembedStatus("running", p.Processed, p.Updated, p.Failed, p.Total, 0, null, false, DateTime.UtcNow, automatic));
            _reembedTask = Task.Run(async () =>
            {
                if (predecessor != null) { try { await predecessor.ConfigureAwait(false); } catch (Exception) { } }
                try
                {
                    KnowledgeReembedResult r;
                    bool deferred = false;
                    if (automatic)
                    {
                        r = await Index.ReembedAsync(progress, KnowledgeIndex.DefaultReembedLimit, cts.Token, missingOnly: true,
                            stopAfterEmptyBatches: 2, beforeBatch: async ct =>
                            {
                                bool go = await RecoveryGateAsync(ct).ConfigureAwait(false);
                                if (!go) deferred = true;
                                return go;
                            }).ConfigureAwait(false);
                    }
                    else r = await Index.ReembedAsync(progress, KnowledgeIndex.DefaultReembedLimit, cts.Token).ConfigureAwait(false);
                    string state = r.Cancelled ? "cancelled" : r.RecoveredFromCorruption ? "recovered" : r.Remaining > 0 ? "partial" : "done";
                    _reembed = new KnowledgeReembedStatus(state, r.Processed, r.Updated, r.Failed, r.Processed, r.Remaining, null, r.RecoveredFromCorruption, DateTime.UtcNow, automatic);
                    if (automatic) OnAutoRecoveryFinished(r, deferred, cts.IsCancellationRequested);
                    else OnManualReembedFinished(r);
                }
                catch (Exception ex)
                {
                    _reembed = new KnowledgeReembedStatus("failed", 0, 0, 0, 0, 0, ex.GetType().Name, false, DateTime.UtcNow, automatic);
                    if (automatic) OnAutoRecoveryFailed();
                }
            });
            return true;
        }
    }

    private bool _reembedAutomatic;

    /// <summary>The Gaming hard-off signal (the persisted marker); replaceable so tests need not touch the real marker file.</summary>
    internal Func<bool> GamingActive { get; set; } = GamingHardOffWatch.MarkerActive;

    private int _benchmarkRunning;
    public bool TryBeginBenchmark() => Interlocked.CompareExchange(ref _benchmarkRunning, 1, 0) == 0;
    public void EndBenchmark() => Interlocked.Exchange(ref _benchmarkRunning, 0);

    public bool CancelReembed()
    {
        lock (_reembedGate)
        {
            if (_reembedTask is not { IsCompleted: false }) return false;
            if (_reembedAutomatic) NoteAutoRecoveryCancelledByPerson();   // a person's Cancel is respected: no automatic restart
            _reembedCts?.Cancel();
            return true;
        }
    }

    /// <summary>Test/shutdown helper: waits for the re-embed job to finish.</summary>
    public async Task WaitForReembedAsync(TimeSpan timeout)
    {
        Task? t; lock (_reembedGate) t = _reembedTask;
        if (t != null) await Task.WhenAny(t, Task.Delay(timeout)).ConfigureAwait(false);
    }

    public KnowledgeJobStatus? Status(string sourceId) => _status.TryGetValue(sourceId, out var s) ? s : null;
    public OcrAvailability OcrAvailability() => _ocr.CheckAvailability();

    public bool Pause(string id) { if (!_jobs.TryGetValue(id, out var j)) return false; j.Control.Pause(); return true; }
    public bool Resume(string id) { if (!_jobs.TryGetValue(id, out var j)) return false; j.Control.Resume(); return true; }
    public bool Cancel(string id) { if (!_jobs.TryGetValue(id, out var j)) return false; j.Cts.Cancel(); return true; }

    /// <summary>Stops the folder's job (waiting briefly so it cannot write after the delete), then securely removes its index data.</summary>
    public SecureDeletionResult Remove(string sourceId)
    {
        StopAndWait(sourceId);
        SecureDeletionResult result = Index.RemoveSource(sourceId);
        _status.TryRemove(sourceId, out _);
        return result;
    }

    /// <summary>"Delete everything": every folder's job is stopped, every source is removed from the folder list and the index
    /// database (rows, FTS, trigram, vectors, WAL/SHM) is zeroed and deleted, as is the classic RAG store's data.</summary>
    public SecureDeletionResult DeleteEverything()
    {
        foreach (string id in _jobs.Keys.ToList()) StopAndWait(id);
        SecureDeletionResult result = Index.DeleteEverything();
        _sources.RemoveAllSources();
        _status.Clear();
        return result;
    }

    private void StopAndWait(string sourceId)
    {
        Cancel(sourceId);
        if (_jobs.TryRemove(sourceId, out var job))
        {
            try { job.Task.Wait(TimeSpan.FromSeconds(10)); } catch (Exception) { /* cancelled or failed: either way it has stopped */ }
        }
    }
}

public sealed record KnowledgeReembedStatus(string State, int Processed, int Updated, int Failed, int Total, int Remaining, string? Error,
    bool RecoveredFromCorruption, DateTime UpdatedUtc, bool Automatic = false);

public sealed class KnowledgeRetrievalUpdate
{
    public string? Workspace { get; set; }
    public string? Mode { get; set; }
    public string? EmbedderId { get; set; }
}

public sealed class KnowledgeBenchmarkRequest
{
    public int? Documents { get; set; }
    public int? Seed { get; set; }
}

public sealed class KnowledgeSettingsUpdate
{
    public bool? OcrEnabled { get; set; }
    public string? OcrLanguage { get; set; }
    public bool? StrictOffline { get; set; }
    public string? SearchMethod { get; set; }
    public string? Profile { get; set; }
}

public static class KnowledgeEndpoints
{
    private static object DescribePlan(KnowledgeJobService svc, string? workspace)
    {
        KnowledgeSettings s = svc.Settings.Get();
        KnowledgeEffectiveSearch effective = svc.ResolveSearch(workspace);
        KnowledgeEmbeddingState state = svc.Index.EmbeddingState();
        return new
        {
            workspace = KnowledgeRetrievalModes.WorkspaceKey(workspace),
            modes = KnowledgeRetrievalModes.All,
            requestedMode = effective.RequestedMode, effectiveMode = effective.EffectiveMode, degradedReason = effective.DegradedReason,
            embedders = svc.Embedders.Options(s.EmbedderId),
            rerankerInstalled = svc.Reranker is { IsAvailable: true },
            embedding = new { state.EmbedderAvailable, state.TotalChunks, state.Current, state.Stale, state.Missing, needsReembed = state.NeedsReembed },
            reembed = svc.ReembedStatus,
            recovery = svc.RecoveryStatus,
            offlineBlocked = OfflinePolicy.IsBlocked(svc.Settings)
        };
    }

    public static void MapKnowledgeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/knowledge/search", (string q, string? mode, int? k, string? workspace, KnowledgeJobService svc, WebKnowledgeSources web) =>
        {
            var m = Enum.TryParse(mode, true, out KnowledgeSearchMode parsed) ? parsed : KnowledgeSearchMode.Auto;
            // An explicit mode is honoured; "auto" (the default) follows the workspace's chosen retrieval mode.
            if (string.IsNullOrWhiteSpace(mode) || m == KnowledgeSearchMode.Auto) m = svc.ResolveSearch(workspace).Mode;
            IReadOnlyList<KnowledgeHit> hits = svc.Index.Search(q ?? string.Empty, m, Math.Clamp(k ?? 10, 1, 50), svc.EnabledSourceIds());
            return Results.Ok(hits.Select(h => new
            {
                h.FileName, h.FilePath, citation = h.Citation, text = h.Text, match = h.Match, score = h.Score, sourceUrl = web.TryGetUrl(h.FilePath),
                page = h.Position.Page, sheet = h.Position.Sheet, cellRange = h.Position.CellRange, slide = h.Position.Slide, heading = h.Position.Heading
            }));
        });
        app.MapPost("/api/v1/knowledge/sources/{id}/index", (string id, KnowledgeJobService svc) =>
        {
            if (svc.Start(id)) return Results.Accepted();
            return svc.Status(id)?.Error == "blocked_sensitive_path"
                ? Results.Json(new { error = "This folder holds credentials, keys or private application data and is not indexed.", code = "sensitive_blocked" }, statusCode: 403)
                : Results.NotFound(new { error = "RAG source not found" });
        }).RequireDesktopOwner();
        app.MapGet("/api/v1/knowledge/settings", (bool? probe, KnowledgeJobService svc) =>
        {
            KnowledgeSettings s = svc.Settings.Get();
            KnowledgeResourcePlan plan = svc.CurrentPlan();
            long ram = KnowledgeResourcePlan.DetectTotalRam();
            bool gpu = KnowledgeResourcePlan.DetectDedicatedGpu();
            OcrAvailability? ocr = null;
            if (probe == true)
            {
                try { ocr = svc.OcrAvailability(); }
                catch (Exception ex) when (WindowsOcrEngine.IsGamingRefusal(ex))
                {
                    // Honest state: the probe was not run because Gaming Mode is active (never cached, retry after Gaming).
                    ocr = OcrAvailability.No("Gaming Mode is active, so the OCR check is unavailable until Gaming ends.");
                }
            }
            return Results.Ok(new
            {
                ocrEnabled = s.OcrEnabled, ocrLanguage = s.OcrLanguage, strictOffline = s.StrictOffline || CorporateMode.IsActive,
                corporateOffline = CorporateMode.IsActive,
                profile = (s.Profile ?? plan.Profile).ToString(), profileIsAutomatic = s.Profile == null,
                recommendedProfile = KnowledgeResourcePlan.Recommend(ram, gpu).ToString(),
                embeddingThreads = plan.EmbeddingThreads, dutyCycle = plan.DutyCycle, memoryBudgetMb = plan.ManagedMemoryBudgetBytes >> 20,
                idleUnloadSeconds = (int)plan.IdleUnload.TotalSeconds, totalRamMb = ram >> 20, dedicatedGpu = gpu,
                ocr = ocr == null ? null : new { available = ocr.Available, reason = ocr.Reason, languages = ocr.Languages }
            });
        });
        app.MapPut("/api/v1/knowledge/settings", (KnowledgeSettingsUpdate update, KnowledgeJobService svc) =>
        {
            KnowledgeSettings s = svc.Settings.Get();
            if (update.OcrEnabled.HasValue) s.OcrEnabled = update.OcrEnabled.Value;
            if (update.OcrLanguage != null) s.OcrLanguage = update.OcrLanguage.Length == 0 ? null : update.OcrLanguage;
            if (update.StrictOffline.HasValue) s.StrictOffline = update.StrictOffline.Value;
            if (update.SearchMethod != null)
            {
                if (!KnowledgeSearchMethods.IsValid(update.SearchMethod)) return Results.BadRequest(new { error = "searchMethod must be auto or keyword." });
                s.SearchMethod = update.SearchMethod;
                // Keep the default workspace's per-workspace choice in step with the legacy toggle.
                s.WorkspaceRetrievalModes[KnowledgeRetrievalModes.DefaultWorkspace] =
                    KnowledgeSearchMethods.Normalize(update.SearchMethod) == KnowledgeSearchMethods.Keyword ? KnowledgeRetrievalModes.Keyword : KnowledgeRetrievalModes.Hybrid;
            }
            if (update.Profile != null)
            {
                if (update.Profile.Equals("auto", StringComparison.OrdinalIgnoreCase)) s.Profile = null;
                else if (Enum.TryParse(update.Profile, true, out KnowledgeProfile parsed)) s.Profile = parsed;
                else return Results.BadRequest(new { error = "profile must be auto, Eco, Balanced or Performance." });
            }
            svc.Settings.Save(s);
            svc.Index.UpdateOptions(svc.BuildOptions());
            return Results.NoContent();
        }).RequireDesktopOwner();
        // What search does right now (hybrid or keyword) and why, plus the method choices.
        app.MapGet("/api/v1/knowledge/retrieval", (KnowledgeJobService svc) =>
            Results.Ok(KnowledgeRetrieval.Describe(svc.Settings.Get().SearchMethod, svc.Embedder,
                svc.Reranker is { IsAvailable: true }, OfflinePolicy.IsBlocked(svc.Settings))));
        // Embedder picker + per-workspace retrieval mode + embedding state, in one truthful read.
        app.MapGet("/api/v1/knowledge/retrieval/plan", (string? workspace, KnowledgeJobService svc) => Results.Ok(DescribePlan(svc, workspace)));
        app.MapPut("/api/v1/knowledge/retrieval", (KnowledgeRetrievalUpdate update, KnowledgeJobService svc) =>
        {
            if (svc.ReembedRunning) return Results.Json(new { error = "A re-embed is running; cancel it or wait before changing the search model.", code = "reembed_running" }, statusCode: 409);
            KnowledgeSettings s = svc.Settings.Get();
            if (update.Mode != null)
            {
                if (!KnowledgeRetrievalModes.IsValid(update.Mode)) return Results.BadRequest(new { error = "mode must be keyword, embedding or hybrid." });
                string ws = KnowledgeRetrievalModes.WorkspaceKey(update.Workspace);
                s.WorkspaceRetrievalModes[ws] = KnowledgeRetrievalModes.Normalize(update.Mode);
                if (ws == KnowledgeRetrievalModes.DefaultWorkspace)
                    s.SearchMethod = s.WorkspaceRetrievalModes[ws] == KnowledgeRetrievalModes.Keyword ? KnowledgeSearchMethods.Keyword : KnowledgeSearchMethods.Auto;
            }
            if (update.EmbedderId != null)
            {
                KnowledgeEmbedderOption? option = svc.Embedders.Options(null).FirstOrDefault(o => string.Equals(o.Id, update.EmbedderId, StringComparison.OrdinalIgnoreCase));
                if (option == null) return Results.BadRequest(new { error = "Unknown search model." });
                if (!option.Installed) return Results.Json(new { error = "That search model is not installed.", code = "embedder_not_installed" }, statusCode: 409);
                s.EmbedderId = option.Id;
            }
            svc.Settings.Save(s);
            return Results.Ok(DescribePlan(svc, update.Workspace));
        }).RequireDesktopOwner();
        app.MapGet("/api/v1/knowledge/reembed", (KnowledgeJobService svc) => Results.Ok(svc.ReembedStatus));
        app.MapPost("/api/v1/knowledge/reembed", (KnowledgeJobService svc) =>
        {
            if (svc.StartReembed(out string? refusal)) return Results.Accepted();
            return Results.Json(new { error = refusal == "gaming_active" ? "Gaming Mode is active; re-embedding waits until it ends." : "No search model is installed to re-embed with.", code = refusal }, statusCode: 409);
        }).RequireDesktopOwner();
        app.MapPost("/api/v1/knowledge/reembed/cancel", (KnowledgeJobService svc) => svc.CancelReembed() ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();
        // "Check retrieval quality": synthetic labelled corpus, all three modes, never real documents.
        app.MapPost("/api/v1/knowledge/benchmark", async (KnowledgeBenchmarkRequest? request, KnowledgeJobService svc, HttpContext http) =>
        {
            if (svc.GamingActive()) return Results.Json(new { error = "Gaming Mode is active; the retrieval check is unavailable until it ends.", code = "gaming_active" }, statusCode: 409);
            if (!svc.TryBeginBenchmark()) return Results.Json(new { error = "A retrieval check is already running.", code = "benchmark_running" }, statusCode: 409);
            try
            {
                var reports = await KnowledgeBenchmark.CompareModesAsync(svc.Embedder, request?.Documents ?? 60, request?.Seed ?? 7, svc.BuildOptions().Plan, http.RequestAborted);
                return Results.Ok(reports);
            }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            finally { svc.EndBenchmark(); }
        }).RequireDesktopOwner();
        app.MapPost("/api/v1/knowledge/sources/{id}/pause", (string id, KnowledgeJobService svc) => svc.Pause(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();
        app.MapPost("/api/v1/knowledge/sources/{id}/resume", (string id, KnowledgeJobService svc) => svc.Resume(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();
        app.MapPost("/api/v1/knowledge/sources/{id}/cancel", (string id, KnowledgeJobService svc) => svc.Cancel(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();
        app.MapGet("/api/v1/knowledge/sources/{id}/status", (string id, KnowledgeJobService svc) =>
        {
            var (files, chunks) = svc.Index.Stats(id);
            return Results.Ok(new { job = svc.Status(id), files, chunks, problems = svc.Index.Problems(id) });
        });
        app.MapDelete("/api/v1/knowledge/sources/{id}", (string id, KnowledgeJobService svc) => { svc.Remove(id); return Results.NoContent(); }).RequireDesktopOwner();
        // Secure "delete everything": index database, FTS/trigram/vector entries, WAL/SHM and the folder list. Documents are never touched.
        app.MapPost("/api/v1/knowledge/delete-everything", (KnowledgeJobService svc) =>
        {
            SecureDeletionResult r = svc.DeleteEverything();
            return Results.Ok(new { files = r.FilesRemoved, chunks = r.ChunksRemoved, vacuumed = r.Vacuumed, databaseFilesRemoved = r.FilesRemovedOnDisk });
        }).RequireDesktopOwner();
    }
}
