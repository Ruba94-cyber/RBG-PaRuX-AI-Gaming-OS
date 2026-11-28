using System.Diagnostics;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Cheap change detector for the persisted inputs of an AUTO routing decision (role assignments, candidate pools,
/// capability mode overrides, the managed model folders). A file or folder is summarised as one number built from its
/// existence, length and last-write time, so a changed stamp means "re-run the full router".
/// </summary>
internal static class RoutingStamp
{
    internal static long OfFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? HashCode.Combine(1, info.Length, info.LastWriteTimeUtc.Ticks) : 0;
        }
        catch { return -1; }
    }

    /// <summary>A folder's own last-write time moves when a direct child is added, removed or renamed.</summary>
    internal static long OfDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists ? HashCode.Combine(2, info.LastWriteTimeUtc.Ticks) : 0;
        }
        catch { return -1; }
    }

    internal static long Combine(params long[] parts)
    {
        var hash = new HashCode();
        foreach (long part in parts) hash.Add(part);
        return hash.ToHashCode();
    }
}

/// <summary>What makes two AUTO routing requests interchangeable: the same mode, role, profile and the same prompt features
/// the ranker actually reads (task-intelligence demand and the trivial-turn flag), never the prompt text itself.</summary>
internal sealed record RouteMemoKey(
    string Mode, string RequestedRole, string NormalizedRole, string AutoProfile,
    double Demand, bool Trivial, bool ManagedLibraryExpanded)
{
    /// <summary>The key for a pool with at most one candidate: nothing is ranked, so the prompt features cannot change the outcome.</summary>
    internal RouteMemoKey WithoutPromptFeatures() => this with { Demand = 0, Trivial = false };
}

/// <summary>One remembered AUTO selection with the evidence it was made from.</summary>
internal sealed class RouteMemoEntry
{
    public required RouteMemoKey Key { get; init; }
    public required long Stamp { get; init; }
    public required long CreatedTicks { get; init; }
    public required string SelectedPath { get; init; }
    public required string Role { get; init; }
    public required IReadOnlyList<CandidateEvidenceDto> Candidates { get; init; }
    public required CandidateEvidenceDto Selected { get; init; }
    public string? EscalationNote { get; init; }
}

/// <summary>
/// Remembers the last AUTO routing selection per request-feature key so a warm chat turn skips candidate discovery,
/// hardware-fit estimates and ranking. An entry is used only while all of these hold: the persisted-input stamp is
/// unchanged, it is younger than <see cref="Ttl"/> (benchmarks, capability profiles and free-memory drift are not stamped),
/// and the caller confirms the live resident still is the remembered model. Any failure to match simply means a normal
/// full route; the memo never produces a decision the router would not have produced for the same inputs.
/// </summary>
internal sealed class RouteDecisionMemo
{
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(10);
    private const int MaximumEntries = 16;

    private readonly object _gate = new();
    private readonly Dictionary<RouteMemoKey, RouteMemoEntry> _entries = new();
    private readonly Func<long> _nowTicks;

    internal RouteDecisionMemo(Func<long>? nowTicks = null, TimeSpan? ttl = null)
    {
        _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
        Ttl = ttl ?? DefaultTtl;
    }

    internal TimeSpan Ttl { get; }
    internal int Hits { get; private set; }
    internal int Misses { get; private set; }
    internal int Count { get { lock (_gate) return _entries.Count; } }

    internal bool TryGet(RouteMemoKey key, long stamp, out RouteMemoEntry? entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out RouteMemoEntry? found)
                && found.Stamp == stamp
                && Stopwatch.GetElapsedTime(found.CreatedTicks, _nowTicks()) < Ttl)
            {
                Hits++;
                entry = found;
                return true;
            }
            if (found is not null) _entries.Remove(key);
            Misses++;
            entry = null;
            return false;
        }
    }

    internal void Store(RouteMemoEntry entry)
    {
        lock (_gate)
        {
            if (_entries.Count >= MaximumEntries && !_entries.ContainsKey(entry.Key))
            {
                RouteMemoKey oldest = _entries.OrderBy(pair => pair.Value.CreatedTicks).First().Key;
                _entries.Remove(oldest);
            }
            _entries[entry.Key] = entry;
        }
    }

    internal long NowTicks => _nowTicks();

    /// <summary>Forgets everything: a model, role or profile change, or any routing failure.</summary>
    internal void Invalidate()
    {
        lock (_gate) _entries.Clear();
    }
}
