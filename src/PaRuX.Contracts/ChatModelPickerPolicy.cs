using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Contracts;

/// <summary>Explicit browsing order, not an automatic routing preference.
/// Benchmark measurements are historical observations on this PC, never
/// a guarantee of speed or quality for the next inference.</summary>
public enum ChatModelBrowseSort { Name, SmallestFileFirst, MeasuredSpeedFirst }

/// <summary>
/// Pure policy for the default Chat's model chooser. The quick flyout stays
/// small and the full catalogue stays reachable through a searchable,
/// paged picker. No model is downloaded, loaded or switched by this policy.
/// </summary>
public static class ChatModelPickerPolicy
{
    public const int QuickChoices = 12;
    public const int BrowsePageSize = 24;
    public static bool HasMeasuredSpeed(ManagedModelInfo model) =>
        model.LastBenchmarkTokensPerSecond is double speed && double.IsFinite(speed) && speed > 0;

    public sealed record Page(IReadOnlyList<ManagedModelInfo> Visible, int TotalMatches,
        int PageIndex, int TotalPages);

    public static Page Browse(IEnumerable<ManagedModelInfo>? catalogue, string? query, int requestedPage,
        ChatModelBrowseSort sort = ChatModelBrowseSort.Name)
    {
        string search = (query ?? string.Empty).Trim();
        List<ManagedModelInfo> matching = (catalogue ?? Enumerable.Empty<ManagedModelInfo>())
            .Where(m => m != null && !string.IsNullOrWhiteSpace(m.FileName))
            .GroupBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(m => search.Length == 0 ||
                m.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (m.Alias?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.Quantization?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        static string LabelOf(ManagedModelInfo model) =>
            string.IsNullOrWhiteSpace(model.Alias) ? model.FileName : model.Alias;
        // Missing sizes and absent local benchmarks sort last. Unknown speed
        // must not be presented as zero or as an unmeasured promise.
        IOrderedEnumerable<ManagedModelInfo> ordered = sort switch
        {
            ChatModelBrowseSort.SmallestFileFirst => matching
                .OrderBy(m => m.SizeBytes <= 0 ? 1 : 0)
                .ThenBy(m => m.SizeBytes <= 0 ? long.MaxValue : m.SizeBytes)
                .ThenBy(LabelOf, StringComparer.OrdinalIgnoreCase),
            ChatModelBrowseSort.MeasuredSpeedFirst => matching
                .OrderBy(m => HasMeasuredSpeed(m) ? 0 : 1)
                .ThenByDescending(m => HasMeasuredSpeed(m)
                    ? m.LastBenchmarkTokensPerSecond!.Value : 0)
                .ThenBy(LabelOf, StringComparer.OrdinalIgnoreCase),
            _ => matching.OrderBy(LabelOf, StringComparer.OrdinalIgnoreCase)
        };
        matching = ordered.ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        int pages = Math.Max(1, (matching.Count + BrowsePageSize - 1) / BrowsePageSize);
        int page = Math.Clamp(requestedPage, 0, pages - 1);
        return new Page(matching.Skip(page * BrowsePageSize).Take(BrowsePageSize).ToList(),
            matching.Count, page, pages);
    }

    /// <summary>Quick comparison is allowed only between two distinct,
    /// still-installed local files. Aliases are display-only and must never
    /// be used as a stable model identity.</summary>
    public static bool CanCompare(
        IEnumerable<ManagedModelInfo>? catalogue, string? firstFile, string? secondFile)
    {
        if (string.IsNullOrWhiteSpace(firstFile) || string.IsNullOrWhiteSpace(secondFile)
            || string.Equals(firstFile, secondFile, StringComparison.OrdinalIgnoreCase))
            return false;
        var installed = new HashSet<string>(
            (catalogue ?? Enumerable.Empty<ManagedModelInfo>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.FileName))
                .Select(m => m.FileName), StringComparer.OrdinalIgnoreCase);
        return installed.Contains(firstFile) && installed.Contains(secondFile);
    }

    public static IReadOnlyList<ManagedModelInfo> Quick(
        IEnumerable<ManagedModelInfo>? catalogue, string? currentManualFile)
    {
        var first = Browse(catalogue, null, 0).Visible.Take(QuickChoices).ToList();
        if (string.IsNullOrWhiteSpace(currentManualFile)) return first;
        ManagedModelInfo? selected = (catalogue ?? Enumerable.Empty<ManagedModelInfo>())
            .FirstOrDefault(m => string.Equals(m.FileName, currentManualFile, StringComparison.OrdinalIgnoreCase));
        if (selected == null || first.Any(m =>
            string.Equals(m.FileName, selected.FileName, StringComparison.OrdinalIgnoreCase)))
            return first;
        if (first.Count >= QuickChoices) first.RemoveAt(first.Count - 1);
        first.Insert(0, selected);
        return first;
    }
    public const int MaxFavourites = 8;
    public const int MaxRecents = 5;

    /// <summary>Records a manual choice as most recent: de-duplicated (case-insensitive), newest first, capped.</summary>
    public static List<string> RecordRecent(IEnumerable<string>? recents, string? chosenFile, int max = MaxRecents)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(chosenFile)) result.Add(chosenFile.Trim());
        foreach (string file in recents ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(file)
                && !result.Contains(file.Trim(), StringComparer.OrdinalIgnoreCase))
                result.Add(file.Trim());
        return result.Take(Math.Max(1, max)).ToList();
    }

    /// <summary>Adds or removes a favourite. A full list refuses a new entry instead of silently evicting another.</summary>
    public static (List<string> Favourites, bool Changed) ToggleFavourite(
        IEnumerable<string>? favourites, string? file, int max = MaxFavourites)
    {
        var list = (favourites ?? Enumerable.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();
        if (string.IsNullOrWhiteSpace(file)) return (list, false);
        string key = file.Trim();
        int at = list.FindIndex(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) { list.RemoveAt(at); return (list, true); }
        if (list.Count >= max) return (list, false);
        list.Add(key);
        return (list, true);
    }

    /// <summary>The compact picker: favourites first, then recents, then the rest of the quick list, never more than
    /// <see cref="QuickChoices"/>, only models that are still installed, the pinned one always visible.</summary>
    public sealed record QuickLayoutResult(
        IReadOnlyList<ManagedModelInfo> Favourites,
        IReadOnlyList<ManagedModelInfo> Recent,
        IReadOnlyList<ManagedModelInfo> Others);

    public static QuickLayoutResult QuickLayout(
        IEnumerable<ManagedModelInfo>? catalogue, string? currentManualFile,
        IEnumerable<string>? favourites, IEnumerable<string>? recents)
    {
        List<ManagedModelInfo> installed = Browse(catalogue, null, 0, ChatModelBrowseSort.Name).Visible.ToList();
        List<ManagedModelInfo> all = (catalogue ?? Enumerable.Empty<ManagedModelInfo>())
            .Where(m => m != null && !string.IsNullOrWhiteSpace(m.FileName))
            .GroupBy(m => m.FileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        ManagedModelInfo? Find(string? file) => all.FirstOrDefault(m =>
            string.Equals(m.FileName, file, StringComparison.OrdinalIgnoreCase));

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fav = new List<ManagedModelInfo>();
        foreach (string f in favourites ?? Enumerable.Empty<string>())
            if (Find(f) is { } m && used.Add(m.FileName) && fav.Count < MaxFavourites) fav.Add(m);
        var rec = new List<ManagedModelInfo>();
        foreach (string f in recents ?? Enumerable.Empty<string>())
            if (Find(f) is { } m && used.Add(m.FileName) && rec.Count < MaxRecents) rec.Add(m);
        if (Find(currentManualFile) is { } current && used.Add(current.FileName)) rec.Insert(0, current);

        int room = Math.Max(0, QuickChoices - fav.Count - rec.Count);
        var others = installed.Where(m => !used.Contains(m.FileName)).Take(room).ToList();
        return new QuickLayoutResult(fav, rec, others);
    }
}
