using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services;

/// <summary>Task group of the Model Hub "Discover" tab. Order is the display order.</summary>
public enum DiscoverTask { Chat, Coding, Reasoning, Vision, Uncensored, Image, Video, Embeddings, More }

/// <summary>How a model runs on THIS PC. Computed from the real detected hardware (see
/// <see cref="ModelDiscoverPolicy.FitFromEstimate"/>), never from a vendor claim. Order is best to worst for ranking.</summary>
public enum DiscoverFit { RunsFast, FitsGpu, NeedsOffload, CpuOnly, Unknown, TooBig }

/// <summary>Size check before a download starts.</summary>
public enum DiskVerdict { Ok, Tight, QuotaExceeded, NotEnoughSpace, Unknown }

public sealed record DiscoverDiskCheck(DiskVerdict Verdict, long RequiredBytes, long FreeBytes, long ShortfallBytes)
{
    public bool CanStart => Verdict is DiskVerdict.Ok or DiskVerdict.Tight or DiskVerdict.Unknown;
}

/// <summary>Source-agnostic card input: a curated recommendation, a media checkpoint or an online search result.</summary>
public sealed record DiscoverEntry(
    string Id,
    string Title,
    string Description,
    string RepoId,
    DiscoverTask Task,
    bool Curated,
    long SizeBytes,
    string Quant,
    string Params,
    long ContextLength,
    DiscoverFit Fit,
    double? SpeedTokensPerSecond,
    bool SpeedMeasured,
    bool Installed,
    bool Online);

public sealed record DiscoverGroup(DiscoverTask Task, IReadOnlyList<DiscoverEntry> Entries);

/// <summary>A previously benchmarked local model, used to scale a speed estimate for a model that was never run here.</summary>
public sealed record DiscoverBenchmarkPoint(long SizeBytes, double TokensPerSecond);

/// <summary>
/// Pure policy of the Model Hub "Discover" tab: task grouping from model metadata, fit/speed badges, ranking, the
/// "Recommended for your PC" pick, the disk-space gate and the plain-language wording. It does no I/O and starts nothing;
/// downloads still go through the existing consent, licence-review and verified-transfer path.
/// </summary>
public static class ModelDiscoverPolicy
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MinTightMarginBytes = 2 * GiB;

    // ---- Task grouping -------------------------------------------------------------------------------------------

    public static IReadOnlyList<DiscoverTask> TaskOrder { get; } = new[]
    {
        DiscoverTask.Chat, DiscoverTask.Coding, DiscoverTask.Reasoning, DiscoverTask.Vision,
        DiscoverTask.Uncensored, DiscoverTask.Image, DiscoverTask.Video, DiscoverTask.Embeddings, DiscoverTask.More
    };

    /// <summary>Maps model metadata to one task group. Explicit capability tags win over the coarse category word, and the
    /// content-profile axis ("uncensored", or an abliterated/uncensored classifier category) wins over the task because
    /// those models are shown in their own, clearly labelled group.</summary>
    public static DiscoverTask Classify(string? category, IEnumerable<string>? capabilityIds, string? contentProfileId)
    {
        string cat = (category ?? string.Empty).Trim().ToLowerInvariant();
        if (string.Equals(contentProfileId, ModelContentProfileIds.Uncensored, StringComparison.OrdinalIgnoreCase)
            || cat is "uncensored-roleplay" or "abliterated" or "uncensored")
            return DiscoverTask.Uncensored;

        var caps = new HashSet<string>(capabilityIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (caps.Contains(ModelCapabilityIds.ImageGenerate) || caps.Contains(ModelCapabilityIds.ImageEdit)) return DiscoverTask.Image;
        if (caps.Contains(ModelCapabilityIds.VideoGenerate)) return DiscoverTask.Video;
        if (caps.Contains(ModelCapabilityIds.Coding)) return DiscoverTask.Coding;
        if (caps.Contains(ModelCapabilityIds.Vision)) return DiscoverTask.Vision;
        if (caps.Contains(ModelCapabilityIds.Reasoning)) return DiscoverTask.Reasoning;
        if (caps.Contains(ModelCapabilityIds.GeneralChat)) return DiscoverTask.Chat;

        return cat switch
        {
            "coding" => DiscoverTask.Coding,
            "vision" => DiscoverTask.Vision,
            "reasoning" => DiscoverTask.Reasoning,
            "embedding" or "reranker" => DiscoverTask.Embeddings,
            "image" => DiscoverTask.Image,
            "video" => DiscoverTask.Video,
            "audio" => DiscoverTask.More,
            _ => DiscoverTask.Chat
        };
    }

    public static string TaskTitle(DiscoverTask task, bool italian) => task switch
    {
        DiscoverTask.Chat => italian ? "Chat e scrittura" : "Chat & writing",
        DiscoverTask.Coding => italian ? "Programmazione" : "Coding",
        DiscoverTask.Reasoning => italian ? "Ragionamento" : "Reasoning",
        DiscoverTask.Vision => italian ? "Visione (immagini in ingresso)" : "Vision (understands pictures)",
        DiscoverTask.Uncensored => italian ? "Senza filtri" : "Unfiltered",
        DiscoverTask.Image => italian ? "Creare immagini" : "Image creation",
        DiscoverTask.Video => italian ? "Creare video" : "Video creation",
        DiscoverTask.Embeddings => italian ? "Documenti e ricerca (embedding)" : "Documents & search (embeddings)",
        _ => italian ? "Altro" : "More"
    };

    public static string TaskHint(DiscoverTask task, bool italian) => task switch
    {
        DiscoverTask.Uncensored => italian
            ? "Modelli che rispondono senza le solite limitazioni. Li scegli tu e te ne assumi la responsabilità."
            : "Models that answer without the usual safeguards. Your choice, your responsibility.",
        DiscoverTask.Embeddings => italian
            ? "Servono per cercare nei tuoi documenti, non per chiacchierare."
            : "These power search over your documents; they do not chat.",
        _ => string.Empty
    };

    public static string TaskAutomationId(DiscoverTask task) => "ModelHub_Discover_Filter_" + task;

    // ---- Fit and speed -------------------------------------------------------------------------------------------

    /// <summary>One-to-one view of the shared hardware estimator's tier, so Discover cannot disagree with the rest of
    /// the Hub. A full-GPU tier with comfortable headroom is "runs fast".</summary>
    public static DiscoverFit FitFromEstimate(ModelHardwareFitEstimate? estimate, bool gpuUsable)
    {
        if (estimate == null) return DiscoverFit.Unknown;
        return estimate.Tier switch
        {
            ModelHardwareFitTier.ExcellentFullGpu => DiscoverFit.RunsFast,
            ModelHardwareFitTier.FullGpuLikely => DiscoverFit.FitsGpu,
            ModelHardwareFitTier.PartialOffload or ModelHardwareFitTier.MultiGpuSplitPossible =>
                gpuUsable ? DiscoverFit.NeedsOffload : DiscoverFit.CpuOnly,
            ModelHardwareFitTier.NotRecommended => DiscoverFit.TooBig,
            _ => DiscoverFit.Unknown
        };
    }

    public static string FitLabel(DiscoverFit fit, bool italian) => fit switch
    {
        DiscoverFit.RunsFast => italian ? "Veloce sulla tua GPU" : "Runs fast on your GPU",
        DiscoverFit.FitsGpu => italian ? "Entra nella tua GPU" : "Fits your GPU",
        DiscoverFit.NeedsOffload => italian ? "Serve la RAM (più lento)" : "Needs offload (slower)",
        DiscoverFit.CpuOnly => italian ? "Solo CPU (lento)" : "CPU only (slow)",
        DiscoverFit.TooBig => italian ? "Troppo grande per questo PC" : "Too big for this PC",
        _ => italian ? "Compatibilità da verificare" : "Fit not checked"
    };

    /// <summary>A symbol next to the word so the badge never relies on colour alone.</summary>
    public static string FitGlyph(DiscoverFit fit) => fit switch
    {
        DiscoverFit.RunsFast => "✔",
        DiscoverFit.FitsGpu => "✔",
        DiscoverFit.NeedsOffload => "⚠",
        DiscoverFit.CpuOnly => "⚠",
        DiscoverFit.TooBig => "✖",
        _ => "?"
    };

    public static bool IsRunnable(DiscoverFit fit) => fit is DiscoverFit.RunsFast or DiscoverFit.FitsGpu
        or DiscoverFit.NeedsOffload or DiscoverFit.CpuOnly;

    /// <summary>Measured speed of this very file when it was benchmarked here; otherwise a decode-speed estimate scaled
    /// from the nearest benchmarked model, but only when the model sits fully in GPU memory (decode is bandwidth bound,
    /// so tokens/s scale about inversely with weight size). Offloaded or CPU runs get no number rather than a guess.</summary>
    public static (double? TokensPerSecond, bool Measured) SpeedFor(
        long sizeBytes, DiscoverFit fit, double? measuredForThisFile, IEnumerable<DiscoverBenchmarkPoint>? benchmarks)
    {
        if (measuredForThisFile is double m && double.IsFinite(m) && m > 0) return (m, true);
        if (sizeBytes <= 0 || fit is not (DiscoverFit.RunsFast or DiscoverFit.FitsGpu)) return (null, false);
        DiscoverBenchmarkPoint? nearest = (benchmarks ?? Enumerable.Empty<DiscoverBenchmarkPoint>())
            .Where(b => b != null && b.SizeBytes > 0 && double.IsFinite(b.TokensPerSecond) && b.TokensPerSecond > 0)
            .OrderBy(b => Math.Abs(Math.Log((double)b.SizeBytes / sizeBytes)))
            .FirstOrDefault();
        if (nearest == null) return (null, false);
        double estimate = nearest.TokensPerSecond * nearest.SizeBytes / sizeBytes;
        return (Math.Round(estimate, 1), false);
    }

    public static string SpeedText(double? tokensPerSecond, bool measured, bool italian)
    {
        if (tokensPerSecond is not double v || v <= 0) return string.Empty;
        string number = v >= 100 ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
            : v.ToString("0.0", CultureInfo.InvariantCulture);
        return measured
            ? number + (italian ? " token/s misurati su questo PC" : " tokens/s measured on this PC")
            : "~" + number + (italian ? " token/s stimati" : " tokens/s estimated");
    }

    // ---- Ranking, grouping, search ------------------------------------------------------------------------------

    /// <summary>Best fit first; curated before unvetted online results; within the same fit the larger (usually more
    /// capable) model first; title as the stable tie-break. Models that are too big sort last.</summary>
    public static IReadOnlyList<DiscoverEntry> Rank(IEnumerable<DiscoverEntry>? entries) =>
        (entries ?? Enumerable.Empty<DiscoverEntry>())
            .Where(e => e != null)
            .OrderBy(e => (int)e.Fit)
            .ThenBy(e => e.Curated ? 0 : 1)
            .ThenByDescending(e => e.SizeBytes)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>Keeps entries matching the query (title, repository, quant, parameter count, description); no query keeps all.</summary>
    public static IReadOnlyList<DiscoverEntry> Search(IEnumerable<DiscoverEntry>? entries, string? query)
    {
        string q = (query ?? string.Empty).Trim();
        IEnumerable<DiscoverEntry> all = (entries ?? Enumerable.Empty<DiscoverEntry>()).Where(e => e != null);
        if (q.Length == 0) return all.ToList();
        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return all.Where(e => words.All(w =>
            e.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
            || e.RepoId.Contains(w, StringComparison.OrdinalIgnoreCase)
            || e.Quant.Contains(w, StringComparison.OrdinalIgnoreCase)
            || e.Params.Contains(w, StringComparison.OrdinalIgnoreCase)
            || e.Description.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Groups ranked entries by task in <see cref="TaskOrder"/>; empty groups are omitted. A repeated repository+quant
    /// (curated and online copy of the same model) keeps the curated, then the first.</summary>
    public static IReadOnlyList<DiscoverGroup> Group(IEnumerable<DiscoverEntry>? entries, DiscoverTask? only = null)
    {
        IReadOnlyList<DiscoverEntry> ranked = Rank(entries)
            .GroupBy(e => (e.RepoId + "|" + e.Quant).ToLowerInvariant())
            .Select(g => g.OrderBy(e => e.Curated ? 0 : 1).First())
            .ToList();
        ranked = Rank(ranked);
        var groups = new List<DiscoverGroup>();
        foreach (DiscoverTask task in TaskOrder)
        {
            if (only is DiscoverTask filter && filter != task) continue;
            List<DiscoverEntry> inGroup = ranked.Where(e => e.Task == task).ToList();
            if (inGroup.Count > 0) groups.Add(new DiscoverGroup(task, inGroup));
        }
        return groups;
    }

    /// <summary>"Recommended for your PC": the largest curated, standard (not unfiltered) chat model that is not installed
    /// and that runs on the GPU. When nothing fits the GPU, the smallest model that can still run. Null when nothing can run.</summary>
    public static DiscoverEntry? RecommendForThisPc(IEnumerable<DiscoverEntry>? entries)
    {
        List<DiscoverEntry> chat = (entries ?? Enumerable.Empty<DiscoverEntry>())
            .Where(e => e != null && e.Curated && !e.Installed && e.Task == DiscoverTask.Chat && IsRunnable(e.Fit))
            .ToList();
        DiscoverEntry? onGpu = chat.Where(e => e.Fit is DiscoverFit.RunsFast or DiscoverFit.FitsGpu)
            .OrderBy(e => (int)e.Fit).ThenByDescending(e => e.SizeBytes)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        // A model that merely "fits" but is much larger than a fast one is still the better answer; prefer size within the
        // GPU-resident tiers, but never recommend a model with no headroom over a fast one of similar size.
        if (onGpu != null)
        {
            DiscoverEntry? fastest = chat.Where(e => e.Fit == DiscoverFit.RunsFast)
                .OrderByDescending(e => e.SizeBytes).FirstOrDefault();
            return fastest ?? onGpu;
        }
        return chat.OrderBy(e => (int)e.Fit).ThenBy(e => e.SizeBytes <= 0 ? long.MaxValue : e.SizeBytes)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    // ---- Disk -----------------------------------------------------------------------------------------------------

    /// <summary>Gate before a download. <paramref name="alreadyDownloadedBytes"/> is the preserved partial file of a resume,
    /// so resuming a 90% finished download is not refused for lack of the whole size. Unknown free space never blocks:
    /// the transfer service performs the authoritative check and reports "disk" failures itself.</summary>
    public static DiscoverDiskCheck CheckDisk(long totalBytes, long alreadyDownloadedBytes, long? destinationFreeBytes, long? quotaHeadroomBytes)
    {
        long remaining = Math.Max(0, totalBytes - Math.Max(0, alreadyDownloadedBytes));
        if (totalBytes <= 0) return new DiscoverDiskCheck(DiskVerdict.Unknown, 0, destinationFreeBytes ?? 0, 0);
        if (quotaHeadroomBytes is long quota && quota >= 0 && remaining > quota)
            return new DiscoverDiskCheck(DiskVerdict.QuotaExceeded, remaining, destinationFreeBytes ?? 0, remaining - quota);
        if (destinationFreeBytes is not long free) return new DiscoverDiskCheck(DiskVerdict.Unknown, remaining, 0, 0);
        if (remaining > free) return new DiscoverDiskCheck(DiskVerdict.NotEnoughSpace, remaining, free, remaining - free);
        long margin = Math.Max(MinTightMarginBytes, remaining / 10);
        return new DiscoverDiskCheck(free - remaining < margin ? DiskVerdict.Tight : DiskVerdict.Ok, remaining, free, 0);
    }

    public static string DiskText(DiscoverDiskCheck check, bool italian) => check.Verdict switch
    {
        DiskVerdict.NotEnoughSpace => italian
            ? $"Spazio insufficiente: mancano {FormatSize(check.ShortfallBytes)}. Libera spazio sul disco dei modelli e riprova."
            : $"Not enough disk space: {FormatSize(check.ShortfallBytes)} short. Free up space on the models drive and try again.",
        DiskVerdict.QuotaExceeded => italian
            ? $"Supera il limite di spazio per l'IA di PaRuX di {FormatSize(check.ShortfallBytes)}. Elimina un modello che non usi o alza il limite nelle impostazioni."
            : $"This is {FormatSize(check.ShortfallBytes)} over PaRuX's AI storage limit. Remove a model you do not use or raise the limit in Settings.",
        DiskVerdict.Tight => italian
            ? "Lo spazio basta, ma resterà poco disco libero."
            : "There is enough space, but the drive will be nearly full afterwards.",
        _ => string.Empty
    };

    // ---- Downloads: resume and matching -------------------------------------------------------------------------

    /// <summary>The existing job a card should resume or follow. An active job wins; otherwise the newest paused, failed
    /// or cancelled job that kept bytes on disk (resuming never restarts from zero).</summary>
    public static ModelDownloadStatus? FindJobForFile(IEnumerable<ModelDownloadStatus>? jobs, string? repoId)
    {
        if (jobs == null || string.IsNullOrWhiteSpace(repoId)) return null;
        List<ModelDownloadStatus> matching = jobs.Where(j => j != null && JobBelongsToRepo(j.FileName, repoId)).ToList();
        ModelDownloadStatus? active = matching.Where(j => ModelDownloadStatusPolicy.IsActive(j.Status))
            .OrderByDescending(j => j.UpdatedUtc).FirstOrDefault();
        if (active != null) return active;
        return matching.Where(j => j.Status is "paused" or "failed" or "cancelled" && j.BytesDownloaded > 0)
            .OrderByDescending(j => j.UpdatedUtc).FirstOrDefault();
    }

    /// <summary>Repository "Qwen/Qwen3-4B-GGUF" owns files that start with "Qwen3-4B" (case and separator insensitive).</summary>
    public static bool JobBelongsToRepo(string? fileName, string? repoId)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(repoId)) return false;
        string repoName = repoId!.Split('/').Last();
        foreach (string suffix in new[] { "-GGUF", "_GGUF", "-gguf" })
            if (repoName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                repoName = repoName[..^suffix.Length];
        string stem = Normalize(repoName);
        return stem.Length >= 3 && Normalize(fileName!).StartsWith(stem, StringComparison.Ordinal);
    }

    private static string Normalize(string value) =>
        new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public enum CardAction { Download, Resume, Pause, Use, Retry, GoOnline, None }

    public sealed record CardState(string Headline, string Fix, CardAction Primary, double? Progress, bool IsProblem);

    /// <summary>The honest state of one card, with the plain-language fix when something is wrong. Order of precedence:
    /// downloading, paused/failed job, installed, offline, then ready to download.</summary>
    public static CardState DescribeCard(
        DiscoverEntry entry, ModelDownloadStatus? job, bool online, DiscoverDiskCheck? disk, bool italian)
    {
        if (job != null && ModelDownloadStatusPolicy.IsActive(job.Status))
        {
            double? p = job.TotalBytes > 0 ? Math.Clamp((double)job.BytesDownloaded / job.TotalBytes, 0, 1) : null;
            string percent = p.HasValue ? " " + Math.Round(p.Value * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "…";
            string head = job.Status == "retrying"
                ? (italian ? "Nuovo tentativo" : "Retrying") + percent
                : job.Status == "queued" ? (italian ? "In coda" : "Queued")
                : (italian ? "Download in corso" : "Downloading") + percent;
            return new CardState(head, string.Empty, CardAction.Pause, p, false);
        }
        if (job != null && job.Status == "paused")
            return new CardState(italian ? "In pausa" : "Paused",
                italian ? "Il file parziale è conservato: riprendi quando vuoi." : "The partial file is kept: resume whenever you like.",
                CardAction.Resume, ProgressOf(job), false);
        if (job != null && job.Status is "failed" or "cancelled")
        {
            bool cancelled = job.Status == "cancelled";
            string fix = cancelled
                ? (italian ? "Annullato. Riprendi per continuare dal punto raggiunto." : "Cancelled. Resume to continue from where it stopped.")
                : FailureFix(job.FailureClass, italian);
            bool canResume = ModelDownloadRowPresentation.CanResume(job);
            return new CardState(cancelled ? (italian ? "Annullato" : "Cancelled") : (italian ? "Download non riuscito" : "Download failed"),
                fix, canResume ? CardAction.Resume : CardAction.Download, ProgressOf(job), !cancelled);
        }
        if (entry.Installed)
            return new CardState(italian ? "Installato" : "Installed", string.Empty, CardAction.Use, null, false);
        if (entry.Online && !online)
            return new CardState(italian ? "Serve Internet" : "Needs the internet",
                italian ? "Attiva l'interruttore Internet per cercare e scaricare da Hugging Face." : "Turn on the Internet switch to search and download from Hugging Face.",
                CardAction.GoOnline, null, false);
        if (entry.Fit == DiscoverFit.TooBig)
            return new CardState(italian ? "Troppo grande" : "Too big",
                italian ? "Non entra in questo PC. Scegli una versione più piccola." : "It does not fit this PC. Pick a smaller one.",
                CardAction.None, null, true);
        if (disk is { CanStart: false })
            return new CardState(italian ? "Spazio insufficiente" : "Not enough space", DiskText(disk, italian), CardAction.None, null, true);
        return new CardState(italian ? "Pronto da scaricare" : "Ready to download", string.Empty, CardAction.Download, null, false);
    }

    private static double? ProgressOf(ModelDownloadStatus job) =>
        job.TotalBytes > 0 ? Math.Clamp((double)job.BytesDownloaded / job.TotalBytes, 0, 1) : null;

    /// <summary>One sentence for a failed transfer that names what to do, by failure class.</summary>
    public static string FailureFix(string? failureClass, bool italian) => (failureClass ?? string.Empty).ToLowerInvariant() switch
    {
        "disk" => italian ? "Disco pieno. Libera spazio e tocca Riprendi." : "The drive is full. Free up space, then tap Resume.",
        "auth" => italian ? "Questo modello richiede un accesso Hugging Face. Salva un token nelle impostazioni e riprova." : "This model needs a Hugging Face sign-in. Save an access token in Settings, then retry.",
        "transient" => italian ? "Connessione interrotta. Controlla la rete e tocca Riprendi: il file parziale non va perso." : "The connection dropped. Check your network and tap Resume; the partial file is safe.",
        "permanent" => italian ? "Questo file non è più disponibile da quella fonte. Scegli un'altra versione." : "That file is no longer available from the source. Pick another version.",
        _ => italian ? "Non è riuscito. Tocca Riprendi; se si ripete, scegli un'altra versione." : "It did not finish. Tap Resume; if it keeps failing, pick another version."
    };

    // ---- Wording --------------------------------------------------------------------------------------------------

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        return bytes >= GiB
            ? (bytes / (double)GiB).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : (bytes / (1024.0 * 1024)).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    public static string FormatContext(long tokens)
    {
        if (tokens <= 0) return string.Empty;
        return tokens >= 1000
            ? (tokens / 1024).ToString(CultureInfo.InvariantCulture) + "K"
            : tokens.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"4B · Q4_K_M · 2.5 GB · 32K context", skipping what is unknown.</summary>
    public static string Facts(DiscoverEntry e, bool italian)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.Params)) parts.Add(e.Params);
        if (!string.IsNullOrWhiteSpace(e.Quant) && !string.Equals(e.Quant, "unknown", StringComparison.OrdinalIgnoreCase)) parts.Add(e.Quant);
        string size = FormatSize(e.SizeBytes);
        if (size.Length > 0) parts.Add(size);
        string ctx = FormatContext(e.ContextLength);
        if (ctx.Length > 0) parts.Add(ctx + (italian ? " di contesto" : " context"));
        return string.Join(" · ", parts);
    }
}
