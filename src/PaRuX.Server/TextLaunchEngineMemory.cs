namespace PaRuX.Server;

/// <summary>
/// Remembers, for a bounded time, which rung of the launch ladder produced a healthy text runtime after the primary engine
/// failed, so the next launch of the same model on the same primary runtime starts there instead of re-running every failing
/// CUDA rung first. In memory only (a restart retries the primary), keyed by primary runtime folder + model path, and it
/// expires, so a fixed driver or a newly installed runtime is tried again without any user action. A remembered rung that
/// then fails is forgotten and the full ladder runs.
/// </summary>
internal sealed class TextLaunchEngineMemory
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal sealed record Remembered(TextLaunchLadder.Step Step, TextLaunchLadder.Engine Engine, string Label, DateTime AtUtc);

    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<string, Remembered> _entries = new(StringComparer.OrdinalIgnoreCase);

    internal TextLaunchEngineMemory(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    internal static TextLaunchEngineMemory Shared { get; } = new();

    private static string Key(string primaryRuntimeDirectory, string modelPath) =>
        Path.GetFullPath(primaryRuntimeDirectory).TrimEnd('\\', '/') + "|" + Path.GetFullPath(modelPath);

    /// <summary>Records the rung that worked. A first-rung success (the primary engine is healthy) clears the entry.</summary>
    internal void Record(string primaryRuntimeDirectory, string modelPath, TextLaunchLadder.Rung rung, bool wasFirstPlannedRung)
    {
        string key = Key(primaryRuntimeDirectory, modelPath);
        lock (_gate)
        {
            if (wasFirstPlannedRung) _entries.Remove(key);
            else _entries[key] = new Remembered(rung.Step, rung.Engine, rung.Label, _utcNow());
        }
    }

    internal void Forget(string primaryRuntimeDirectory, string modelPath)
    {
        lock (_gate) _entries.Remove(Key(primaryRuntimeDirectory, modelPath));
    }

    internal Remembered? Recall(string primaryRuntimeDirectory, string modelPath)
    {
        string key = Key(primaryRuntimeDirectory, modelPath);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out Remembered? entry)) return null;
            if (_utcNow() - entry.AtUtc <= Lifetime) return entry;
            _entries.Remove(key);
            return null;
        }
    }

    /// <summary>
    /// The rungs to run: the planned list from the remembered rung onward, or the whole list when nothing is remembered or the
    /// remembered rung is not part of this plan (different arguments or engines installed). Never returns an empty list.
    /// </summary>
    internal static IReadOnlyList<TextLaunchLadder.Rung> StartFrom(IReadOnlyList<TextLaunchLadder.Rung> planned, Remembered? remembered)
    {
        ArgumentNullException.ThrowIfNull(planned);
        if (remembered is null) return planned;
        for (int index = 1; index < planned.Count; index++)
            if (planned[index].Step == remembered.Step && planned[index].Engine == remembered.Engine)
                return planned.Skip(index).ToList();
        return planned;
    }
}
