using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Knowledge;

/// <summary>The source ids the person asked PaRuX to keep up to date. Stored beside the knowledge settings.</summary>
public sealed class WatchedFolderStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();

    public WatchedFolderStore(string? path = null) => _path = path ?? PaRuXDataRoot.Combine("knowledge", "watched-folders.json");

    public IReadOnlySet<string> Get()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path), Json)?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>()
                    : new HashSet<string>();
            }
            catch (Exception ex) when (ex is IOException or JsonException) { return new HashSet<string>(); }
        }
    }

    public void Set(string sourceId, bool watched)
    {
        lock (_gate)
        {
            HashSet<string> ids = Get().ToHashSet(StringComparer.Ordinal);
            if (!(watched ? ids.Add(sourceId) : ids.Remove(sourceId))) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(ids.OrderBy(i => i, StringComparer.Ordinal).ToList(), Json));
            File.Move(temp, _path, overwrite: true);
        }
    }
}

/// <summary>
/// Keeps chosen folder sources up to date: after files in a watched folder change and stay quiet for a moment, the source
/// is re-indexed (incrementally: unchanged files are skipped by the index). The person turns it on per folder and can turn it
/// off any time. It never reads outside the folder, never runs while Gaming Mode is active (it retries after the game), and
/// a burst of changes becomes a single index run.
/// </summary>
public sealed class KnowledgeFolderWatcher : IDisposable
{
    private sealed class Entry : IDisposable
    {
        public required FileSystemWatcher Watcher;
        public required Timer Debounce;
        public int Pending;
        public void Dispose() { Watcher.Dispose(); Debounce.Dispose(); }
    }

    private readonly Action<string> _reindex;
    private readonly Func<bool> _gamingActive;
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _gamingRetry;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string _dataRoot;
    private bool _disposed;

    public KnowledgeFolderWatcher(Action<string> reindex, Func<bool>? gamingActive = null, TimeSpan? quiet = null,
        TimeSpan? gamingRetry = null, string? dataRoot = null)
    {
        _reindex = reindex;
        _gamingActive = gamingActive ?? GamingHardOffWatch.MarkerActive;
        _quiet = quiet ?? TimeSpan.FromSeconds(8);
        _gamingRetry = gamingRetry ?? TimeSpan.FromSeconds(30);
        _dataRoot = Path.GetFullPath(dataRoot ?? PaRuXDataRoot.Path).TrimEnd(Path.DirectorySeparatorChar);
    }

    public bool IsWatching(string sourceId) { lock (_gate) return _entries.ContainsKey(sourceId); }

    /// <summary>What a refused <see cref="Watch"/> was refused for, so the caller can ask for consent instead of failing.</summary>
    public SensitiveVerdict LastRefusal { get; private set; } = SensitiveVerdict.Allowed;

    /// <summary>
    /// Starts watching. False when the folder is missing, blocked as sensitive, inside PaRuX's own data, or broad/risky
    /// (a whole drive or profile, application data) without <paramref name="consentBroad"/>.
    /// </summary>
    public bool Watch(string sourceId, string folder, bool consentBroad = false)
    {
        LastRefusal = SensitiveVerdict.Allowed;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
        string full = Path.GetFullPath(folder);
        if (IsInsideDataRoot(full)) { LastRefusal = SensitiveVerdict.Blocked; return false; }
        SensitiveVerdict verdict = SensitivePathPolicy.EvaluateFolder(full).Verdict;
        if (verdict == SensitiveVerdict.Blocked || (verdict == SensitiveVerdict.NeedsConsent && !consentBroad))
        {
            LastRefusal = verdict;
            return false;
        }
        lock (_gate)
        {
            if (_disposed) return false;
            Unwatch(sourceId);
            var watcher = new FileSystemWatcher(full)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            var entry = new Entry { Watcher = watcher, Debounce = new Timer(_ => Fire(sourceId), null, Timeout.Infinite, Timeout.Infinite) };
            void Touch(object? s, FileSystemEventArgs e) => Changed(sourceId, e.FullPath);
            // A folder's own "changed" event only reports that something inside it changed; that inner change has its own event.
            watcher.Changed += (s, e) => { if (!Directory.Exists(e.FullPath)) Changed(sourceId, e.FullPath); };
            watcher.Created += Touch;
            watcher.Deleted += Touch;
            watcher.Renamed += (s, e) => Changed(sourceId, e.FullPath);
            // A full buffer means events were lost: one index run catches everything up.
            watcher.Error += (s, e) => Changed(sourceId, full);
            watcher.EnableRaisingEvents = true;
            _entries[sourceId] = entry;
            return true;
        }
    }

    public void Unwatch(string sourceId)
    {
        lock (_gate)
        {
            if (_entries.Remove(sourceId, out Entry? entry)) entry.Dispose();
        }
    }

    private bool IsInsideDataRoot(string path)
    {
        try
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(full, _dataRoot, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(_dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
    }

    private void Changed(string sourceId, string path)
    {
        // Temporary and hidden files are editor noise; sensitive files are skipped by the index anyway.
        // PaRuX's own files (the index database, logs, saved web pages' manifests) must never retrigger indexing: a watched
        // parent of the data folder would otherwise re-index forever.
        if (IsInsideDataRoot(path)) return;
        string name = Path.GetFileName(path);
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~$", StringComparison.Ordinal)) return;
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(sourceId, out Entry? entry)) return;
            Interlocked.Exchange(ref entry.Pending, 1);
            try { entry.Debounce.Change(_quiet, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }
    }

    private void Fire(string sourceId)
    {
        Entry? entry;
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(sourceId, out entry)) return;
        }
        if (_gamingActive())
        {
            try { entry.Debounce.Change(_gamingRetry, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
            return;
        }
        if (Interlocked.Exchange(ref entry.Pending, 0) == 0) return;
        try { _reindex(sourceId); }
        catch (Exception) { /* the next change retries; a failed start must never kill the timer thread */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (Entry e in _entries.Values) e.Dispose();
            _entries.Clear();
        }
    }
}
