using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// What PaRuX tells the user when the Corporate default model was refused by the memory plan and a smaller bundled model
/// answers instead. Bilingual on purpose: the Core has no UI language, and the same sentence reaches the Runtime Manager and
/// the chat route event.
/// </summary>
internal sealed record CorporateFallbackNotice(string DefaultModelPath, string UsedModelPath, string English, string Italian)
{
    public string DefaultName => CorporateSmallModelFallback.DisplayName(DefaultModelPath);
    public string UsedName => CorporateSmallModelFallback.DisplayName(UsedModelPath);
    /// <summary>One line carrying both languages, for places that show a single string.</summary>
    public string Combined => Italian + " / " + English;
}

/// <summary>
/// Automatic fallback of the Corporate default model (Qwen3-4B) to a smaller GGUF that ships in the same package.
/// Rules, all enforced here or by the callers' wiring:
///  - only when no model was explicitly requested (a named model, a saved Chat assignment, a manual lock is never replaced);
///  - only models listed in the package manifest (docs/MANIFEST.json) that are installed, intact (SHA-256 when the manifest
///    pins one), strictly smaller than the default, and admitted by the SAME memory planner as any other launch;
///  - at most <see cref="MaxAttempts"/> candidates, largest first, no loop, cancellation honoured;
///  - the saved settings are never touched; the choice is remembered until free RAM grows significantly, so a running
///    fallback is not stopped and reloaded on every request.
/// </summary>
internal static class CorporateSmallModelFallback
{
    internal const int MaxAttempts = 3;
    /// <summary>Free RAM must grow by this much (MiB) before a remembered fallback is re-evaluated against the default model.</summary>
    internal const double SignificantGrowthMib = 1024;

    internal sealed record AdmittedModel(string FileName, string? Sha256);

    internal static string DisplayName(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileNameWithoutExtension(path);

    // ---- manifest -----------------------------------------------------------------------------------------------------

    /// <summary>docs/MANIFEST.json of the extracted package: next to PaRuXData, else a few levels above the application.</summary>
    internal static string? LocateManifest(string? dataRoot = null, string? baseDirectory = null)
    {
        try
        {
            string data = dataRoot ?? PaRuXDataRoot.Path;
            string? parent = string.IsNullOrWhiteSpace(data) ? null : Path.GetDirectoryName(Path.GetFullPath(data));
            if (parent != null)
            {
                string candidate = Path.Combine(parent, "docs", "MANIFEST.json");
                if (File.Exists(candidate)) return candidate;
            }
            string? dir = baseDirectory ?? AppContext.BaseDirectory;
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "docs", "MANIFEST.json");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    /// <summary>The small models the package declares: <c>alternateModel</c>(+Sha256) and an optional <c>alternateModels</c> array.</summary>
    internal static IReadOnlyList<AdmittedModel> ReadManifestModels(string? manifestPath)
    {
        var result = new List<AdmittedModel>();
        if (string.IsNullOrWhiteSpace(manifestPath)) return result;
        try
        {
            FileInfo info = new(manifestPath);
            if (!info.Exists || info.Length > 8L * 1024 * 1024) return result;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath).TrimStart('﻿'));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;

            static string? Str(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
            void Add(string? name, string? sha)
            {
                if (name is null || !PaRuXPathSafety.IsSimpleFileName(name)
                    || !name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("projector", StringComparison.OrdinalIgnoreCase)) return;
                if (result.Any(m => string.Equals(m.FileName, name, StringComparison.OrdinalIgnoreCase))) return;
                result.Add(new AdmittedModel(name, string.IsNullOrWhiteSpace(sha) ? null : sha.Trim()));
            }

            bool single = root.TryGetProperty("alternateModelIncluded", out JsonElement inc) && inc.ValueKind == JsonValueKind.True;
            if (single) Add(Str(root, "alternateModel"), Str(root, "alternateModelSha256"));
            if (root.TryGetProperty("alternateModels", out JsonElement many) && many.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in many.EnumerateArray().Take(8)) Add(Str(item, "name"), Str(item, "sha256"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        return result;
    }

    // ---- candidates ---------------------------------------------------------------------------------------------------

    /// <summary>Installed manifest models strictly smaller than the default, largest (best quality) first. No planner here.</summary>
    internal static IReadOnlyList<string> AdmittedAlternates(
        string defaultModelPath, string modelsDirectory, IReadOnlyList<AdmittedModel> manifest,
        Func<string, bool> isUsable, Func<string, string?, bool> verifyIntegrity)
    {
        var found = new List<(string Path, long Size)>();
        try
        {
            if (!File.Exists(defaultModelPath)) return Array.Empty<string>();
            long defaultSize = new FileInfo(defaultModelPath).Length;
            string defaultFull = Path.GetFullPath(defaultModelPath);
            foreach (AdmittedModel m in manifest)
            {
                string path = Path.GetFullPath(Path.Combine(modelsDirectory, m.FileName));
                if (string.Equals(path, defaultFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                long size = new FileInfo(path).Length;
                if (size >= defaultSize) continue;
                if (!isUsable(path) || !verifyIntegrity(path, m.Sha256)) continue;
                found.Add((path, size));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Array.Empty<string>();
        }
        return found.OrderByDescending(f => f.Size).Select(f => f.Path).ToList();
    }

    private static readonly object HashGate = new();
    private static readonly Dictionary<string, (long Length, DateTime Stamp, string Sha)> HashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SHA-256 of an installed alternate against the manifest pin (cached per file length and write time). No pin = accepted by name.</summary>
    internal static bool VerifyAgainstManifest(string path, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return true;
        try
        {
            FileInfo info = new(path);
            lock (HashGate)
                if (HashCache.TryGetValue(path, out var c) && c.Length == info.Length && c.Stamp == info.LastWriteTimeUtc)
                    return string.Equals(c.Sha, expectedSha256, StringComparison.OrdinalIgnoreCase);
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
            string sha = Convert.ToHexString(SHA256.HashData(fs));
            lock (HashGate) HashCache[path] = (info.Length, info.LastWriteTimeUtc, sha);
            return string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The production candidate list for this package; empty when there is no manifest or nothing qualifies.</summary>
    internal static IReadOnlyList<string> ResolveForCurrentPackage(string defaultModelPath, Func<string, bool> isUsable) =>
        AdmittedAlternates(defaultModelPath, Path.GetDirectoryName(Path.GetFullPath(defaultModelPath)) ?? string.Empty,
            ReadManifestModels(LocateManifest()), isUsable, VerifyAgainstManifest);

    /// <summary>True for the package-pinned starter when the user has not assigned another Chat model.</summary>
    internal static bool IsUnassignedPackageDefault(string modelPath)
    {
        try
        {
            if (!CorporateMode.IsActive) return false;
            if (!string.IsNullOrWhiteSpace(LocalAiPaths.GetRoleOverride("standard"))) return false;
            string? bundled = LocalAiPaths.FindBundledCorporateModel();
            return bundled != null && string.Equals(Path.GetFullPath(bundled), Path.GetFullPath(modelPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>A named model, a saved Chat assignment or a manual lock is an explicit request and is never replaced.</summary>
    internal static bool Eligible(bool callerNamedNoModel, bool corporate, bool isUnassignedPackageDefault) =>
        callerNamedNoModel && corporate && isUnassignedPackageDefault;

    // ---- choice -------------------------------------------------------------------------------------------------------

    internal sealed record Selection(string? ModelPath, IReadOnlyList<string> Tried);

    /// <summary>First candidate the planner admits, trying at most <paramref name="maxAttempts"/>; never loops, never loads anything.</summary>
    internal static Selection Choose(IReadOnlyList<string> alternates, Func<string, bool> admits, CancellationToken ct, int maxAttempts = MaxAttempts)
    {
        var tried = new List<string>();
        foreach (string alt in alternates.Take(Math.Max(0, maxAttempts)))
        {
            ct.ThrowIfCancellationRequested();
            tried.Add(alt);
            bool ok;
            try { ok = admits(alt); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or ArgumentException or NotSupportedException) { ok = false; }
            if (ok) return new Selection(alt, tried);
        }
        return new Selection(null, tried);
    }

    /// <summary>Same bounded choice for callers whose admission check is asynchronous (the Runtime Manager preflight).</summary>
    internal static async Task<Selection> ChooseAsync(IReadOnlyList<string> alternates, Func<string, Task<bool>> admits,
        CancellationToken ct, int maxAttempts = MaxAttempts)
    {
        var tried = new List<string>();
        foreach (string alt in alternates.Take(Math.Max(0, maxAttempts)))
        {
            ct.ThrowIfCancellationRequested();
            tried.Add(alt);
            bool ok;
            try { ok = await admits(alt).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or ArgumentException or NotSupportedException) { ok = false; }
            if (ok) return new Selection(alt, tried);
        }
        return new Selection(null, tried);
    }

    internal static CorporateFallbackNotice BuildNotice(string defaultModelPath, string usedModelPath, double neededMib, double availableMib)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string d = DisplayName(defaultModelPath), u = DisplayName(usedModelPath);
        string numbers = neededMib > 0
            ? " (" + (neededMib / 1024d).ToString("0.0", inv) + " GB / " + (availableMib / 1024d).ToString("0.0", inv) + " GB)"
            : string.Empty;
        return new CorporateFallbackNotice(defaultModelPath, usedModelPath,
            "Not enough memory for " + d + ": using " + u + "." + numbers + " Your saved settings are unchanged; PaRuX will use " + d + " again when more memory is free.",
            "Memoria insufficiente per " + d + ": uso " + u + "." + numbers + " Le tue impostazioni salvate non sono cambiate; PaRuX userà di nuovo " + d + " quando ci sarà più memoria libera.");
    }

    /// <summary>Refusal that still guides the user when not even a small model fits (same numbers as the default's refusal).</summary>
    internal static string AppendNoSmallModelFits(string defaultRefusal, IReadOnlyList<string> tried) =>
        tried.Count == 0
            ? defaultRefusal
            : defaultRefusal + " The smaller bundled model(s) were checked as well ("
                + string.Join(", ", tried.Select(DisplayName)) + ") and none fits either. Close heavy applications to free memory, then try again.";

    // ---- published state (what Runtime Manager and chat read) --------------------------------------------------------

    private static readonly object StateGate = new();
    private static CorporateFallbackNotice? _published;

    internal static void Publish(CorporateFallbackNotice notice) { lock (StateGate) _published = notice; }
    internal static void ClearPublished() { lock (StateGate) _published = null; }

    /// <summary>The notice, only while <paramref name="activeModelPath"/> is the fallback model it describes.</summary>
    internal static CorporateFallbackNotice? CurrentFor(string? activeModelPath)
    {
        if (string.IsNullOrWhiteSpace(activeModelPath)) return null;
        lock (StateGate)
        {
            if (_published is null) return null;
            try
            {
                return string.Equals(Path.GetFullPath(_published.UsedModelPath), Path.GetFullPath(activeModelPath), StringComparison.OrdinalIgnoreCase)
                    ? _published : null;
            }
            catch (ArgumentException) { return null; }
        }
    }

    // ---- remembered choice ------------------------------------------------------------------------------------------

    internal sealed class Memory
    {
        internal sealed record Entry(string DefaultModelPath, string FallbackModelPath, double AvailableMib);
        private readonly object _gate = new();
        private Entry? _entry;

        internal static Memory Shared { get; } = new();

        public void Remember(string defaultModelPath, string fallbackModelPath, double availableMib)
        {
            lock (_gate) _entry = new Entry(defaultModelPath, fallbackModelPath, availableMib);
        }

        public Entry? Get(string defaultModelPath)
        {
            lock (_gate)
                return _entry is { } e && string.Equals(e.DefaultModelPath, defaultModelPath, StringComparison.OrdinalIgnoreCase) ? e : null;
        }

        public void Forget() { lock (_gate) _entry = null; }

        /// <summary>True when free RAM grew enough since the decision to look at the default model again.</summary>
        public static bool GrewSignificantly(Entry entry, double effectiveAvailableMib) =>
            double.IsFinite(effectiveAvailableMib) && effectiveAvailableMib >= entry.AvailableMib + SignificantGrowthMib;

        /// <summary>Keep the running fallback without a reload (no growth, or the default still does not fit); the baseline moves up with the growth.</summary>
        public bool ShouldKeepFallback(Entry entry, double effectiveAvailableMib, bool defaultFitsNow)
        {
            if (!GrewSignificantly(entry, effectiveAvailableMib)) return true;
            if (defaultFitsNow) return false;
            lock (_gate)
                if (_entry is { } cur && ReferenceEquals(cur, entry)) _entry = entry with { AvailableMib = effectiveAvailableMib };
            return true;
        }
    }
}
