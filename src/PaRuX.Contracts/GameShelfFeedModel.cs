using System.Text;

namespace PaRuX.Contracts;

public enum GameShelfKind { Continue, Favorites, RecentlyAdded, Collection }

/// <summary>
/// One horizontal shelf of the console-style Games feed. <see cref="Titles"/> are references into the caller's list (no copies of
/// title data). <see cref="Id"/> is stable and safe for automation ids; <see cref="Name"/> is the collection name for
/// <see cref="GameShelfKind.Collection"/> and empty for the built-in shelves (the UI localizes those).
/// </summary>
public sealed record GameShelf(string Id, GameShelfKind Kind, string Name, IReadOnlyList<GameTitleDto> Titles);

/// <summary>
/// Pure rules of the shelf feed: which shelves exist for a title list, what each holds, and how D-pad / keyboard movement crosses
/// shelves. Within one shelf a title appears at most once; the same title may appear on several shelves (a favorite that was
/// played recently is on both), exactly like console launchers. Only the shelf contents are lists of references, so a 5,000-title
/// library costs a few passes over the list and never copies a title. Rendering is virtualized per shelf by the UI.
/// </summary>
public static class GameShelfFeedModel
{
    public const int ContinueLimit = 20;
    public const int RecentlyAddedLimit = 40;
    public const int MaxCollectionShelves = 8;

    /// <summary>
    /// Builds the shelves for <paramref name="titles"/> (already filtered and sorted by the host): Continue (played, most recent first),
    /// Favorites (list order), Recently added (newest first) and up to <see cref="MaxCollectionShelves"/> collections (largest first,
    /// then by name; titles in list order). Empty shelves are omitted. Hidden titles are left out unless <paramref name="includeHidden"/>.
    /// </summary>
    public static IReadOnlyList<GameShelf> Build(IReadOnlyList<GameTitleDto> titles, bool includeHidden = false)
    {
        var shelves = new List<GameShelf>();
        var visible = new List<GameTitleDto>(titles.Count);
        foreach (GameTitleDto t in titles) if (includeHidden || !t.Hidden) visible.Add(t);

        List<GameTitleDto> played = visible
            .Where(t => t.LastPlayedUtc.HasValue || t.PlaySeconds > 0)
            .OrderByDescending(t => t.LastPlayedUtc.HasValue).ThenByDescending(t => t.LastPlayedUtc).ThenByDescending(t => t.PlaySeconds)
            .Take(ContinueLimit).ToList();
        if (played.Count > 0) shelves.Add(new GameShelf("continue", GameShelfKind.Continue, string.Empty, played));

        List<GameTitleDto> favorites = visible.Where(t => t.Favorite).ToList();
        if (favorites.Count > 0) shelves.Add(new GameShelf("favorites", GameShelfKind.Favorites, string.Empty, favorites));

        List<GameTitleDto> added = visible.Where(t => t.AddedUtc.HasValue)
            .OrderByDescending(t => t.AddedUtc).Take(RecentlyAddedLimit).ToList();
        if (added.Count > 0) shelves.Add(new GameShelf("recent", GameShelfKind.RecentlyAdded, string.Empty, added));

        var byCollection = new Dictionary<string, (string Name, List<GameTitleDto> Titles)>(StringComparer.OrdinalIgnoreCase);
        foreach (GameTitleDto t in visible)
        {
            foreach (string raw in t.Collections)
            {
                string name = raw?.Trim() ?? string.Empty;
                if (name.Length == 0) continue;
                if (!byCollection.TryGetValue(name, out var entry)) byCollection[name] = entry = (name, new List<GameTitleDto>());
                // A title listing the same collection twice still sits on that shelf once.
                if (entry.Titles.Count == 0 || !ReferenceEquals(entry.Titles[^1], t)) entry.Titles.Add(t);
            }
        }
        var usedIds = new HashSet<string>(shelves.Select(s => s.Id), StringComparer.Ordinal);
        foreach (var entry in byCollection.Values
                     .OrderByDescending(e => e.Titles.Count).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(MaxCollectionShelves))
        {
            string id = UniqueId("collection_" + Slug(entry.Name), usedIds);
            shelves.Add(new GameShelf(id, GameShelfKind.Collection, entry.Name, entry.Titles));
        }
        return shelves;
    }

    /// <summary>Same shelves with the same titles in the same order (a refresh that needs no rebuild of the feed).</summary>
    public static bool SameMembership(IReadOnlyList<GameShelf> a, IReadOnlyList<GameShelf> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || a[i].Titles.Count != b[i].Titles.Count) return false;
            for (int j = 0; j < a[i].Titles.Count; j++)
                if (!string.Equals(a[i].Titles[j].TitleKey, b[i].Titles[j].TitleKey, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        return sb.ToString();
    }

    private static string UniqueId(string id, HashSet<string> used)
    {
        string candidate = id;
        for (int n = 2; !used.Add(candidate); n++) candidate = id + "_" + n;
        return candidate;
    }

    /// <summary>
    /// Next focused tile as (shelf, index) for keyboard / D-pad movement. Left/Right move inside a shelf and stop at its ends;
    /// Up/Down enter the neighbouring non-empty shelf at the same column, clamped to its last tile, and stop at the first and last
    /// shelf (so the page can take focus further, towards the toolbar or the footer); Home/End jump to the first/last tile of the
    /// shelf; the page keys (bumpers) move <paramref name="pageSize"/> tiles. The position is returned unchanged when there is nowhere
    /// to go and (-1, -1) when no shelf has tiles.
    /// </summary>
    public static (int Shelf, int Index) Move(IReadOnlyList<int> sizes, int shelf, int index, GameLibraryHome.Direction direction, int pageSize = 5)
    {
        if (sizes.Count == 0 || sizes.All(s => s <= 0)) return (-1, -1);
        shelf = Math.Clamp(shelf, 0, sizes.Count - 1);
        if (sizes[shelf] <= 0) return (-1, -1);
        index = Math.Clamp(index, 0, sizes[shelf] - 1);
        pageSize = Math.Max(1, pageSize);
        int Neighbour(int step)
        {
            for (int s = shelf + step; s >= 0 && s < sizes.Count; s += step) if (sizes[s] > 0) return s;
            return -1;
        }
        switch (direction)
        {
            case GameLibraryHome.Direction.Left: return (shelf, Math.Max(0, index - 1));
            case GameLibraryHome.Direction.Right: return (shelf, Math.Min(sizes[shelf] - 1, index + 1));
            case GameLibraryHome.Direction.PageUp: return (shelf, Math.Max(0, index - pageSize));
            case GameLibraryHome.Direction.PageDown: return (shelf, Math.Min(sizes[shelf] - 1, index + pageSize));
            case GameLibraryHome.Direction.First: return (shelf, 0);
            case GameLibraryHome.Direction.Last: return (shelf, sizes[shelf] - 1);
            case GameLibraryHome.Direction.Up:
            {
                int above = Neighbour(-1);
                return above < 0 ? (shelf, index) : (above, Math.Min(index, sizes[above] - 1));
            }
            case GameLibraryHome.Direction.Down:
            {
                int below = Neighbour(1);
                return below < 0 ? (shelf, index) : (below, Math.Min(index, sizes[below] - 1));
            }
            default: return (shelf, index);
        }
    }
}
