using Microsoft.Data.Sqlite;

namespace PaRuX.Server.Knowledge;

/// <summary>
/// Vector bookkeeping and the bounded, cancellable re-embed used when the person changes the embedder. A passage's vector is
/// tagged with the embedder that made it (<c>embed_key</c>); search only uses vectors from the embedder in use, so two models of
/// the same dimension are never mixed. Re-embedding replaces a vector only after a valid new one exists; a failed or cancelled run
/// leaves the old vector and every passage's text untouched.
/// </summary>
public sealed partial class KnowledgeIndex
{
    public const int DefaultReembedLimit = 250_000;

    // The "File: name" passage never carries a vector, so it is not counted as missing.
    private const string EmbeddablePassage = "COALESCE(heading,'')<>'file name'";

    /// <summary>Counts passages whose vector matches the embedder in use (Current), came from another one (Stale) or is absent (Missing).</summary>
    public KnowledgeEmbeddingState EmbeddingState()
    {
        EnsureSchema();
        bool available = _embedder is { IsAvailable: true };
        string? key = available ? _embedder!.Identity.StableKey : null;
        lock (_gate)
        {
            using SqliteConnection c = Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = @"SELECT COUNT(*),
COALESCE(SUM(embedding IS NOT NULL AND embed_key=$k),0),
COALESCE(SUM(embedding IS NOT NULL AND (embed_key IS NULL OR embed_key<>$k)),0),
COALESCE(SUM(embedding IS NULL),0) FROM kn_chunks WHERE " + EmbeddablePassage + ";";
            cmd.Parameters.AddWithValue("$k", (object?)key ?? "");
            using SqliteDataReader r = cmd.ExecuteReader();
            r.Read();
            return new KnowledgeEmbeddingState(available, key, r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
        }
    }

    /// <summary>Passages that have no vector at all (e.g. indexed while the model could not load). Cheap; 0 when nothing is missing.</summary>
    internal int CountMissingVectors()
    {
        EnsureSchema();
        lock (_gate)
        {
            using SqliteConnection c = Open();
            return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM kn_chunks WHERE " + EmbeddablePassage + " AND embedding IS NULL;"));
        }
    }

    /// <summary>True when at least one passage has a vector from the embedder in use (so embedding-only search can answer).</summary>
    public bool HasCurrentVectors()
    {
        if (_embedder is not { IsAvailable: true }) return false;
        EnsureSchema();
        lock (_gate)
        {
            using SqliteConnection c = Open();
            return Scalar(c, "SELECT 1 FROM kn_chunks WHERE embedding IS NOT NULL AND embed_key=$k LIMIT 1;", ("$k", _embedder.Identity.StableKey)) != null;
        }
    }

    /// <summary>
    /// Embeds every passage that lacks a vector from the embedder in use, in batches. Bounded by <paramref name="maxChunks"/>,
    /// cancellable between batches (the work done so far is kept and consistent), paced by the plan's throttle. Throws
    /// <see cref="InvalidOperationException"/> when no embedder is usable. A damaged index file is moved aside (see
    /// <see cref="RecoverFromCorruption"/>) and reported through <see cref="KnowledgeReembedResult.RecoveredFromCorruption"/>.
    /// </summary>
    public Task<KnowledgeReembedResult> ReembedAsync(IProgress<KnowledgeReembedProgress>? progress = null,
        int maxChunks = DefaultReembedLimit, CancellationToken ct = default) =>
        ReembedAsync(progress, maxChunks, ct, missingOnly: false, stopAfterEmptyBatches: 0, beforeBatch: null);

    /// <summary>
    /// The incremental form used by the automatic recovery: with <paramref name="missingOnly"/> only passages that have NO vector
    /// are re-processed (a vector from another embedder is left alone: replacing those is the person's explicit choice).
    /// <paramref name="stopAfterEmptyBatches"/> (when above 0) ends the run after that many batches in a row in which no passage
    /// got a vector, i.e. the model still cannot run, instead of grinding through the whole index. <paramref name="beforeBatch"/> is
    /// awaited before each batch; returning false stops the run cleanly (memory pressure, Gaming, indexing busy): nothing is lost.
    /// </summary>
    internal async Task<KnowledgeReembedResult> ReembedAsync(IProgress<KnowledgeReembedProgress>? progress, int maxChunks,
        CancellationToken ct, bool missingOnly, int stopAfterEmptyBatches, Func<CancellationToken, Task<bool>>? beforeBatch)
    {
        if (_embedder is not { IsAvailable: true } embedder) throw new InvalidOperationException("No search model is available to re-embed with.");
        int processed = 0, updated = 0, failed = 0, total = 0;
        try
        {
            EnsureSchema();
            string key = embedder.Identity.StableKey;
            int dims = embedder.Identity.Dimensions;
            total = Math.Min(maxChunks, CountTargets(key, missingOnly));
            progress?.Report(new KnowledgeReembedProgress("running", 0, 0, 0, total));
            long last = 0;
            int batchSize = Math.Max(1, _options.EmbedBatchSize);
            int emptyInARow = 0;
            while (processed < maxChunks)
            {
                ct.ThrowIfCancellationRequested();
                if (beforeBatch != null && !await beforeBatch(ct).ConfigureAwait(false)) break;
                ct.ThrowIfCancellationRequested();
                var batch = new List<(long id, string text)>();
                lock (_gate)
                {
                    using SqliteConnection c = Open();
                    using SqliteCommand cmd = c.CreateCommand();
                    cmd.CommandText = "SELECT id,text FROM kn_chunks WHERE id>$l AND " + EmbeddablePassage
                        + " AND " + (missingOnly ? "embedding IS NULL" : "(embedding IS NULL OR embed_key IS NULL OR embed_key<>$k)") + " ORDER BY id LIMIT $n;";
                    cmd.Parameters.AddWithValue("$l", last); cmd.Parameters.AddWithValue("$k", key);
                    cmd.Parameters.AddWithValue("$n", Math.Min(batchSize, maxChunks - processed));
                    using SqliteDataReader r = cmd.ExecuteReader();
                    while (r.Read()) batch.Add((r.GetInt64(0), r.GetString(1)));
                }
                if (batch.Count == 0) break;
                last = batch[^1].id;

                IReadOnlyList<float[]?> vectors;
                try { vectors = embedder.TryEmbedBatch(batch.Select(b => b.text).ToList()); }
                catch (Exception) { vectors = new float[]?[batch.Count]; }
                if (vectors.Count != batch.Count) vectors = new float[]?[batch.Count];

                int updatedBefore = updated;
                lock (_gate)
                {
                    using SqliteConnection c = Open();
                    using SqliteTransaction tx = c.BeginTransaction();
                    for (int i = 0; i < batch.Count; i++)
                    {
                        float[]? v = vectors[i];
                        if (v == null || v.Length != dims || !v.All(float.IsFinite)) { failed++; continue; }
                        using SqliteCommand up = c.CreateCommand();
                        up.Transaction = tx;
                        up.CommandText = "UPDATE kn_chunks SET embedding=$v, embed_key=$k WHERE id=$id;";
                        up.Parameters.AddWithValue("$v", ToBlob(v)); up.Parameters.AddWithValue("$k", key); up.Parameters.AddWithValue("$id", batch[i].id);
                        updated += up.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                processed += batch.Count;
                emptyInARow = updated == updatedBefore ? emptyInARow + 1 : 0;
                progress?.Report(new KnowledgeReembedProgress("running", processed, updated, failed, total));
                if (_options.ThrottleMilliseconds > 0) await Task.Delay(_options.ThrottleMilliseconds, ct).ConfigureAwait(false);
                else await Task.Yield();
                if (stopAfterEmptyBatches > 0 && emptyInARow >= stopAfterEmptyBatches) break;
            }
            int remaining = CountTargets(key, missingOnly);
            return new KnowledgeReembedResult(processed, updated, failed, remaining, false, processed >= maxChunks && remaining > 0, false);
        }
        catch (OperationCanceledException)
        {
            int remaining = 0;
            try { remaining = CountTargets(embedder.Identity.StableKey, missingOnly); } catch (Exception) { }
            return new KnowledgeReembedResult(processed, updated, failed, remaining, true, false, false);
        }
        catch (SqliteException ex) when (IsCorrupt(ex))
        {
            RecoverFromCorruption();
            return new KnowledgeReembedResult(processed, updated, failed, 0, false, false, true);
        }
    }

    private int CountTargets(string key, bool missingOnly = false)
    {
        lock (_gate)
        {
            using SqliteConnection c = Open();
            return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM kn_chunks WHERE " + EmbeddablePassage
                + " AND " + (missingOnly ? "embedding IS NULL" : "(embedding IS NULL OR embed_key IS NULL OR embed_key<>$k)") + ";", ("$k", key)));
        }
    }
}
