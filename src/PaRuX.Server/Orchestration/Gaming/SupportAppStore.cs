using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

public sealed record SupportAppListState(bool Enabled, IReadOnlyList<SupportAppEntry> Apps, bool Damaged);

/// <summary>
/// The owner's support-app list in one small JSON file under the data root (server/support-apps.json). Off by default. Writes are
/// atomic (temp file, flush, move) with the previous good file kept as .bak; a damaged file reads as "empty and off", is reported
/// honestly and is never overwritten by a read. At most 16 entries, 128 KB, never a link. Nothing here starts a process.
/// </summary>
public sealed class SupportAppStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record StoredFile(int Version, bool Enabled, IReadOnlyList<SupportAppEntry> Apps);

    private readonly string _path;
    private readonly object _gate = new();

    public SupportAppStore() : this(PaRuXDataRoot.Combine("server", "support-apps.json")) { }
    public SupportAppStore(string path) => _path = Path.GetFullPath(path);
    public string BackupPath => _path + ".bak";

    public SupportAppListState Load() { lock (_gate) return ReadLocked(); }

    /// <summary>Replaces the list. A new or changed entry must pass full admission; an unchanged entry whose exe vanished is kept so it can be fixed or removed.</summary>
    public (SupportAppListState? State, string? Error) Save(SupportAppSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            SupportAppListState current = ReadLocked();
            IReadOnlyList<SupportAppEntry> incoming = request.Apps ?? current.Apps;
            if (incoming.Count > SupportAppLimits.MaxApps) return (null, $"Too many support apps (maximum {SupportAppLimits.MaxApps}).");
            var apps = new List<SupportAppEntry>(incoming.Count);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SupportAppEntry raw in incoming)
            {
                if (raw is null) return (null, "Every app must be an object.");
                SupportAppEntry e = SupportAppPolicy.Normalize(raw);
                string? problem = SupportAppPolicy.Validate(e);
                bool unchanged = current.Apps.Any(x => x == e);
                if (problem is not null && !(unchanged && SupportAppPolicy.IsAvailabilityProblem(e))) return (null, problem);
                if (!ids.Add(e.Id)) return (null, $"{e.Name}: duplicate id.");
                apps.Add(e);
            }
            return TryWrite(request.Enabled, apps, current.Damaged);
        }
    }

    /// <summary>Adds one entry (used after a managed download) without touching the rest.</summary>
    public (SupportAppListState? State, string? Error) Add(SupportAppEntry entry)
    {
        lock (_gate)
        {
            SupportAppListState current = ReadLocked();
            if (current.Damaged) return (null, "The saved list is damaged and was not changed.");
            var apps = current.Apps.Where(a => !string.Equals(a.Id, entry.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            apps.Add(entry);
            return Save(new SupportAppSaveRequest(current.Enabled, apps));
        }
    }

    /// <summary>Removes every entry that belongs to a catalog item (after its managed copy was deleted).</summary>
    public SupportAppListState RemoveCatalog(string catalogId)
    {
        lock (_gate)
        {
            SupportAppListState current = ReadLocked();
            if (current.Damaged) return current;
            var apps = current.Apps.Where(a => !string.Equals(a.CatalogId, catalogId, StringComparison.OrdinalIgnoreCase)).ToList();
            return TryWrite(current.Enabled, apps, false).State ?? current;
        }
    }

    private (SupportAppListState? State, string? Error) TryWrite(bool enabled, IReadOnlyList<SupportAppEntry> apps, bool wasDamaged)
    {
        string? temp = null;
        try
        {
            string dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            if (IsLink(_path) || IsLink(BackupPath)) return (null, "The saved list is a link and was not changed.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new StoredFile(1, enabled, apps), Options);
            if (bytes.Length > SupportAppLimits.MaxFileBytes) return (null, "The list is larger than 128 KB.");
            temp = Path.Combine(dir, ".support-apps-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(_path) && !wasDamaged) File.Copy(_path, BackupPath, overwrite: true);
            File.Move(temp, _path, overwrite: true);
            temp = null;
            return (new SupportAppListState(enabled, apps, false), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, "The list could not be saved."); }
        finally
        {
            if (temp is not null) { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    private SupportAppListState ReadLocked()
    {
        if (!File.Exists(_path)) return new(false, [], false);
        SupportAppListState? state = TryRead(_path);
        if (state is not null) return state;
        SupportAppListState? backup = File.Exists(BackupPath) ? TryRead(BackupPath) : null;
        return backup ?? new(false, [], true);
    }

    private static bool IsLink(string path)
    {
        try { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static SupportAppListState? TryRead(string path)
    {
        try
        {
            if (IsLink(path) || new FileInfo(path).Length > SupportAppLimits.MaxFileBytes) return null;
            StoredFile? f = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(path), Options);
            if (f?.Apps is null || f.Apps.Count > SupportAppLimits.MaxApps) return null;
            var apps = new List<SupportAppEntry>();
            foreach (SupportAppEntry e in f.Apps)
            {
                if (e is null) return null;
                SupportAppEntry n = SupportAppPolicy.Normalize(e);
                string? problem = SupportAppPolicy.Validate(n);
                if (problem is not null && !SupportAppPolicy.IsAvailabilityProblem(n)) return null;
                apps.Add(n);
            }
            return new SupportAppListState(f.Enabled, apps, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
}
