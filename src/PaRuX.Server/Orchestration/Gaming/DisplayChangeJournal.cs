using System.Text.Json;

namespace PaRuX.Server.Orchestration.Gaming;

internal enum DisplayChangeKind { Resolution, RefreshRate, Hdr }

/// <summary>One display change PaRuX applied and must be able to undo. PreviousValue is an opaque, kind-specific token.</summary>
internal sealed record DisplayJournalEntry(string Id, DisplayChangeKind Kind, string DisplayId,
    string PreviousValue, string AppliedValue, DateTime CreatedUtc, string Owner);

internal enum RestoreOutcome { Restored, NotApplicable, Failed }

/// <summary>Puts one journaled display value back. NotApplicable means the target no longer exists (monitor gone).</summary>
internal interface IDisplayChangeRestorer
{
    RestoreOutcome Restore(DisplayJournalEntry entry);
}

internal sealed record DisplayRecoveryReport(int Restored, int Discarded, int Failed, bool JournalWasCorrupt)
{
    public static readonly DisplayRecoveryReport Empty = new(0, 0, 0, false);
    public bool Clean => Failed == 0 && !JournalWasCorrupt;
}

/// <summary>
/// Persisted write-ahead journal of display changes. An entry is written BEFORE a change is applied and removed only after
/// it was restored (or confirmed unneeded), so a crash at any point leaves a restore point for the next start.
/// Bounded in count and size; a corrupt file is quarantined, never silently trusted or silently dropped.
/// </summary>
internal sealed class DisplayChangeJournal
{
    internal const int MaxEntries = 32;
    internal const long MaxFileBytes = 64 * 1024;
    private readonly string _path;
    private readonly object _gate = new();

    public DisplayChangeJournal(string path) => _path = path;

    /// <summary>Persists the restore point. Throws when it cannot be saved: the caller must then not change the display.</summary>
    public void Begin(DisplayJournalEntry entry)
    {
        lock (_gate)
        {
            List<DisplayJournalEntry> list = Load(out _);
            if (list.Count >= MaxEntries)
                throw new InvalidOperationException("Too many unrestored display changes are recorded; restore them first.");
            list.Add(entry);
            Save(list);
        }
    }

    public void Complete(string id)
    {
        lock (_gate)
        {
            List<DisplayJournalEntry> list = Load(out _);
            if (list.RemoveAll(e => e.Id == id) > 0) TrySave(list);
        }
    }

    public IReadOnlyList<DisplayJournalEntry> Pending(string? owner = null)
    {
        lock (_gate)
            return Load(out _).Where(e => owner is null || e.Owner == owner).ToArray();
    }

    /// <summary>Startup recovery: restores newest-first. Failed entries stay on disk for the next attempt.</summary>
    public DisplayRecoveryReport RecoverAfterCrash(IDisplayChangeRestorer restorer, string? owner = null)
    {
        lock (_gate)
        {
            List<DisplayJournalEntry> list = Load(out bool corrupt);
            int restored = 0, discarded = 0, failed = 0;
            foreach (DisplayJournalEntry e in list.Where(e => owner is null || e.Owner == owner).Reverse().ToArray())
            {
                RestoreOutcome outcome;
                try { outcome = restorer.Restore(e); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                    or System.ComponentModel.Win32Exception) { outcome = RestoreOutcome.Failed; }
                switch (outcome)
                {
                    case RestoreOutcome.Restored: restored++; list.Remove(e); break;
                    case RestoreOutcome.NotApplicable: discarded++; list.Remove(e); break;
                    default: failed++; break;
                }
            }
            TrySave(list);
            return new DisplayRecoveryReport(restored, discarded, failed, corrupt);
        }
    }

    private List<DisplayJournalEntry> Load(out bool corrupt)
    {
        corrupt = false;
        try
        {
            if (!File.Exists(_path)) return new();
            if (new FileInfo(_path).Length > MaxFileBytes) throw new JsonException("journal too large");
            var list = JsonSerializer.Deserialize<List<DisplayJournalEntry>>(File.ReadAllText(_path)) ?? throw new JsonException("null");
            if (list.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.DisplayId))) throw new JsonException("bad entry");
            return list;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            corrupt = true;
            try { File.Move(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), overwrite: true); } catch { }
            return new();
        }
    }

    private void Save(List<DisplayJournalEntry> list)
    {
        try
        {
            if (list.Count == 0) { File.Delete(_path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The display restore point could not be saved; nothing was changed.");
        }
    }

    private void TrySave(List<DisplayJournalEntry> list)
    {
        try { Save(list); } catch (InvalidOperationException) { }
    }
}
