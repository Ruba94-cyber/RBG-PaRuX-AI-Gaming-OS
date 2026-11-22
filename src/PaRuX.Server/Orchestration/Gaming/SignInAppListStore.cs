using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>The saved list plus the facts the trigger needs.</summary>
public sealed record SignInAppListState(SignInAppList List, string? LastRunSessionKey, bool Damaged, IReadOnlyList<string> Warnings);

/// <summary>
/// The PaRuX-owned after-sign-in list in one small JSON file under the data root (server/sign-in-apps.json).
/// Off by default. Writes are atomic (temp file, flush, move) and the previous good file is kept as .bak. A damaged file
/// reads as "empty and disabled" (reported honestly) and is never overwritten by a read. The file is capped at 256 KB and
/// 50 entries and may not be a link. Nothing here touches the registry, Startup folders or third-party startup entries.
/// </summary>
public sealed class SignInAppListStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record StoredFile(int Version, bool Enabled, SignInAppListSettings Settings, IReadOnlyList<SignInAppEntry> Apps, string? LastRunSessionKey);

    private readonly string _path;
    private readonly object _gate = new();

    public SignInAppListStore() : this(PaRuXDataRoot.Combine("server", "sign-in-apps.json")) { }

    public SignInAppListStore(string path) => _path = Path.GetFullPath(path);

    public string BackupPath => _path + ".bak";

    public SignInAppListState Load()
    {
        lock (_gate) return ReadLocked();
    }

    /// <summary>
    /// Replaces the whole list (add, remove and reorder are edits of it). New or changed entries must pass full admission;
    /// an unchanged entry whose exe merely vanished is kept so the owner can fix or remove it.
    /// </summary>
    public (SignInAppListState? State, string? Error) Save(SignInAppListSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            SignInAppListState current = ReadLocked();
            SignInAppListSettings settings = request.Settings ?? current.List.Settings;
            string? settingsError = SignInAppListPolicy.ValidateSettings(settings);
            if (settingsError is not null) return (null, settingsError);

            IReadOnlyList<SignInAppEntry> incoming = request.Apps ?? current.List.Apps;
            if (incoming.Count > SignInAppListPolicy.MaxApps) return (null, "Too many apps (maximum 50).");
            var apps = new List<SignInAppEntry>(incoming.Count);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SignInAppEntry raw in incoming)
            {
                if (raw is null) return (null, "Every app must be an object.");
                SignInAppEntry entry = Normalize(raw);
                string? problem = SignInAppListPolicy.ValidateEntry(entry);
                bool unchanged = current.List.Apps.Any(e => e == entry);
                if (problem is not null && !(unchanged && SignInAppListPolicy.IsAvailabilityProblem(entry)))
                    return (null, problem);
                if (!names.Add(entry.Name)) return (null, $"{entry.Name}: an app with this name is already in the list.");
                apps.Add(entry);
            }
            return TryWrite(new SignInAppList(request.Enabled, settings, apps), current.LastRunSessionKey, current);
        }
    }

    /// <summary>Remembers the sign-in session whose list run was started, so a restart of the shell does not run it twice.</summary>
    public bool MarkRan(string sessionKey)
    {
        lock (_gate)
        {
            SignInAppListState current = ReadLocked();
            if (current.Damaged) return false;
            return TryWrite(current.List, sessionKey, current).State is not null;
        }
    }

    /// <summary>
    /// Applies an already validated import. The result is ALWAYS disabled until the owner opts in again; with
    /// <paramref name="append"/> the existing entries stay and entries with an existing name are not duplicated.
    /// </summary>
    public (SignInAppListState? State, string? Error) ApplyImport(SignInAppList imported, bool append)
    {
        lock (_gate)
        {
            SignInAppListState current = ReadLocked();
            var apps = new List<SignInAppEntry>();
            SignInAppListSettings settings = imported.Settings;
            if (append)
            {
                apps.AddRange(current.List.Apps);
                settings = current.List.Settings;
                foreach (SignInAppEntry e in imported.Apps)
                    if (!apps.Any(x => string.Equals(x.Name, e.Name, StringComparison.OrdinalIgnoreCase))) apps.Add(e);
            }
            else apps.AddRange(imported.Apps);
            if (apps.Count > SignInAppListPolicy.MaxApps) return (null, "Too many apps (maximum 50).");
            return TryWrite(new SignInAppList(false, settings, apps), current.LastRunSessionKey, current);
        }
    }

    private static SignInAppEntry Normalize(SignInAppEntry e) => e with
    {
        Name = (e.Name ?? string.Empty).Trim(),
        Path = NullIfBlank(e.Path),
        Args = NullIfBlank(e.Args),
        Aumid = NullIfBlank(e.Aumid),
        Process = NullIfBlank(e.Process),
    };

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private (SignInAppListState? State, string? Error) TryWrite(SignInAppList list, string? lastKey, SignInAppListState previous)
    {
        string? temp = null;
        try
        {
            string dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            if (IsLink(_path) || IsLink(BackupPath)) return (null, "The saved list is a link and was not changed.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                new StoredFile(1, list.Enabled, list.Settings, list.Apps, lastKey), WriteOptions);
            if (bytes.Length > SignInAppListPolicy.MaxFileBytes) return (null, "The list is larger than 256 KB.");

            temp = Path.Combine(dir, ".sign-in-apps-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            // Keep the previous GOOD file as the backup; a damaged file never replaces a good backup.
            if (File.Exists(_path) && !previous.Damaged) File.Copy(_path, BackupPath, overwrite: true);
            File.Move(temp, _path, overwrite: true);
            temp = null;
            return (new SignInAppListState(list, lastKey, false, []), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "The list could not be saved.");
        }
        finally
        {
            if (temp is not null) { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    private SignInAppListState ReadLocked()
    {
        if (!File.Exists(_path)) return new(SignInAppList.Empty, null, false, []);
        SignInAppListState? state = TryRead(_path);
        if (state is not null) return state;
        SignInAppListState? backup = File.Exists(BackupPath) ? TryRead(BackupPath) : null;
        if (backup is not null)
            return backup with { Warnings = ["The saved list was damaged; the last good backup is shown."] };
        return new(SignInAppList.Empty, null, true, ["The saved list is damaged or unreadable and was not changed."]);
    }

    private static bool IsLink(string path)
    {
        try { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static SignInAppListState? TryRead(string path)
    {
        try
        {
            if (IsLink(path)) return null;
            if (new FileInfo(path).Length > SignInAppListPolicy.MaxFileBytes) return null;
            string json = File.ReadAllText(path);
            SignInAppListParseResult parsed = SignInAppListPolicy.ParseStored(json);
            if (!parsed.Ok || parsed.Value is null) return null;
            bool enabled = false;
            string? lastKey = null;
            using (JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }))
            {
                foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(p.Name, "enabled", StringComparison.OrdinalIgnoreCase)) enabled = p.Value.ValueKind == JsonValueKind.True;
                    else if (string.Equals(p.Name, "lastRunSessionKey", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    {
                        string? k = p.Value.GetString();
                        lastKey = k is { Length: > 0 and <= SignInTriggerPolicy.MaxSessionKeyLength } ? k : null;
                    }
                }
            }
            return new SignInAppListState(parsed.Value with { Enabled = enabled }, lastKey, false, parsed.Warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
