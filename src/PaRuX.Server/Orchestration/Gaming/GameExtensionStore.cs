using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Games-side extensions: data-only folders under Documents\PaRuX\Extensions. This store only READS manifests, hashes them and
/// remembers which ones the owner enabled; it never runs, downloads or contacts anything. A folder must hold nothing but the
/// manifest (plus an optional publisher hash and readme/licence text): any other file or folder makes it invalid, so an
/// executable can never ride along. Enabling is bound to the manifest's SHA-256: if the file changes afterwards the extension
/// is switched off until the owner enables the new content. Removal deletes only the known data files and the then-empty folder.
/// </summary>
public sealed class GameExtensionStore
{
    public const int MaxExtensions = 64;
    public const string ManifestName = "extension.json";
    public const string PublisherHashName = "extension.sha256";
    private static readonly string[] AllowedFiles = { ManifestName, PublisherHashName, "README.md", "README.txt", "LICENSE", "LICENSE.txt" };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _statePath;

    private sealed class StateFile { public int Version { get; set; } = 1; public Dictionary<string, string> Enabled { get; set; } = new(StringComparer.Ordinal); }

    public GameExtensionStore(string extensionsRoot, string stateDirectory)
    {
        _root = Path.GetFullPath(extensionsRoot);
        _statePath = Path.Combine(Path.GetFullPath(stateDirectory), "game-extensions.json");
    }

    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PaRuX", "Extensions");

    public string Root => _root;

    /// <summary>Creates the (empty) extensions folder so the owner can drop extensions into it.</summary>
    public string EnsureRoot()
    {
        Directory.CreateDirectory(_root);
        return _root;
    }

    public IReadOnlyList<GameExtensionInfo> List()
    {
        lock (_gate)
        {
            var result = new List<GameExtensionInfo>();
            if (!Directory.Exists(_root)) return result;
            try { if (new DirectoryInfo(_root).Attributes.HasFlag(FileAttributes.ReparsePoint)) return result; }   // a linked root could point anywhere
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }
            Dictionary<string, string> enabled = ReadState().Enabled;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Take(MaxExtensions).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }
            foreach (string dir in dirs) result.Add(Inspect(dir, enabled));
            return result;
        }
    }

    /// <returns>Null on success, otherwise the reason.</returns>
    public string? SetEnabled(string id, bool enable)
    {
        if (!GameExtensionManifestValidator.IsValidId(id)) return "Not a valid extension id.";
        lock (_gate)
        {
            if (RootIsLink()) return "The extensions folder is a link, which is not allowed.";
            string dir = Path.Combine(_root, id);
            if (!Directory.Exists(dir)) return "That extension is not installed.";
            StateFile state = ReadState();
            if (enable)
            {
                GameExtensionInfo info = Inspect(dir, new Dictionary<string, string>());
                if (!info.Valid) return "This extension has problems and cannot be enabled: " + (info.Issues.Count > 0 ? info.Issues[0] : "invalid manifest.");
                state.Enabled[id] = info.Sha256;
            }
            else state.Enabled.Remove(id);
            return WriteState(state) ? null : "The choice could not be saved.";
        }
    }

    /// <returns>Null on success, otherwise the reason.</returns>
    public string? Remove(string id)
    {
        if (!GameExtensionManifestValidator.IsValidId(id)) return "Not a valid extension id.";
        lock (_gate)
        {
            string dir = Path.GetFullPath(Path.Combine(_root, id));
            if (!string.Equals(Path.GetDirectoryName(dir), _root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return "Not a valid extension id.";
            if (!Directory.Exists(dir)) return "That extension is not installed.";
            if (RootIsLink()) return "The extensions folder is a link, which is not allowed.";
            try
            {
                if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) return "This folder is a link; remove it yourself in File Explorer.";
                var unexpected = new List<string>();
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    string name = Path.GetFileName(entry);
                    bool allowed = File.Exists(entry) && AllowedFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
                        && !File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint);
                    if (!allowed) unexpected.Add(name);
                }
                if (unexpected.Count > 0)
                    return "The folder holds files that are not extension data (" + string.Join(", ", unexpected.Take(3)) + "). Remove the folder yourself in File Explorer.";
                foreach (string file in Directory.EnumerateFiles(dir)) File.Delete(file);
                Directory.Delete(dir, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "The extension could not be removed: " + ex.Message; }
            StateFile state = ReadState();
            if (state.Enabled.Remove(id)) WriteState(state);
            return null;
        }
    }

    private bool RootIsLink()
    {
        try { return Directory.Exists(_root) && new DirectoryInfo(_root).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private GameExtensionInfo Inspect(string dir, Dictionary<string, string> enabled)
    {
        string folder = Path.GetFileName(dir);
        var info = new GameExtensionInfo { Id = folder, FolderName = folder, Name = folder };
        var issues = new List<string>();
        try
        {
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) { issues.Add("The folder is a link, which is not allowed."); return Finish(info, issues, null, enabled); }

            foreach (string entry in Directory.EnumerateFileSystemEntries(dir).Take(50))
            {
                string name = Path.GetFileName(entry);
                if (Directory.Exists(entry)) { issues.Add("Unexpected folder \"" + name + "\": extensions are data only."); continue; }
                if (!AllowedFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) { issues.Add("Unexpected file \"" + name + "\": extensions are data only, no programs or scripts."); continue; }
                var fi = new FileInfo(entry);
                if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) issues.Add("\"" + name + "\" is a link, which is not allowed.");
                else if (fi.Length > GameExtensionManifestValidator.MaxManifestBytes) issues.Add("\"" + name + "\" is larger than " + GameExtensionManifestValidator.MaxManifestBytes / 1024 + " KB.");
            }

            string manifestPath = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifestPath)) { issues.Add(ManifestName + " is missing."); return Finish(info, issues, null, enabled); }
            var manifestInfo = new FileInfo(manifestPath);
            if (manifestInfo.Length > GameExtensionManifestValidator.MaxManifestBytes) return Finish(info, issues, null, enabled);   // already reported as oversize
            if (manifestInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) return Finish(info, issues, null, enabled);

            byte[] bytes = File.ReadAllBytes(manifestPath);
            info.Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            GameExtensionValidationResult validation = GameExtensionManifestValidator.Validate(bytes);
            foreach (GameExtensionIssue i in validation.Issues) issues.Add(i.Path + ": " + i.Message);
            info.PublisherHash = CheckPublisherHash(dir, info.Sha256, issues);
            if (validation.Manifest is { } m)
            {
                if (!string.Equals(m.Id, folder, StringComparison.Ordinal)) issues.Add("id: the folder name must equal the extension id (" + m.Id + ").");
                else return Finish(info, issues, m, enabled);
            }
            return Finish(info, issues, null, enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            issues.Add("The folder could not be read.");
            return Finish(info, issues, null, enabled);
        }
    }

    private static string CheckPublisherHash(string dir, string actual, List<string> issues)
    {
        string path = Path.Combine(dir, PublisherHashName);
        if (!File.Exists(path)) return "none";
        string first;
        try { first = (File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "none"; }
        if (first.Length == 64 && string.Equals(first, actual, StringComparison.OrdinalIgnoreCase)) return "match";
        issues.Add("The publisher hash file does not match this manifest (it was changed or the hash is malformed).");
        return "mismatch";
    }

    private static GameExtensionInfo Finish(GameExtensionInfo info, List<string> issues, GameExtensionManifest? m, Dictionary<string, string> enabled)
    {
        info.Issues = issues.Take(12).ToList();
        if (m is not null && issues.Count == 0)
        {
            info.Valid = true;
            info.Id = m.Id; info.Name = m.Name; info.Version = m.Version; info.Author = m.Author;
            info.Description = m.Description; info.Homepage = m.Homepage;
            info.Theme = m.Theme; info.MetadataSources = m.MetadataSources; info.LaunchPresets = m.LaunchPresets;
            if (enabled.TryGetValue(m.Id, out string? sha))
            {
                if (string.Equals(sha, info.Sha256, StringComparison.Ordinal)) info.Enabled = true;
                else info.ChangedSinceEnabled = true;
            }
        }
        return info;
    }

    private StateFile ReadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return new StateFile();
            return JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_statePath), Json) ?? new StateFile();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new StateFile(); }
    }

    private bool WriteState(StateFile state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            string temp = _statePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
            File.Move(temp, _statePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>Process-wide store used by the HTTP routes (default root: Documents\PaRuX\Extensions).</summary>
internal static class GameExtensionRuntime
{
    private static readonly Lazy<GameExtensionStore> Instance = new(() => new GameExtensionStore(GameExtensionStore.DefaultRoot(), ServerState.StateDirectory));
    public static GameExtensionStore Store => Instance.Value;
}

public static class GameExtensionEndpoints
{
    public static void MapGameExtensionEndpoints(this Microsoft.AspNetCore.Builder.WebApplication app)
    {
        app.MapGet("/api/v1/gaming/extensions", () => Microsoft.AspNetCore.Http.Results.Ok(GameExtensionRuntime.Store.List())).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/extensions/{id}/enabled", (string id, GameExtensionEnabledRequest request) =>
        {
            if (request is null) return Microsoft.AspNetCore.Http.Results.BadRequest();
            string? error = GameExtensionRuntime.Store.SetEnabled(id, request.Enabled);
            if (error is not null) return Microsoft.AspNetCore.Http.Results.BadRequest(new { error });
            AuditLogService.TryRecord("gaming.extension", (request.Enabled ? "enabled " : "disabled ") + id);
            return Microsoft.AspNetCore.Http.Results.Ok(GameExtensionRuntime.Store.List());
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/extensions/{id}", (string id) =>
        {
            string? error = GameExtensionRuntime.Store.Remove(id);
            if (error is not null) return Microsoft.AspNetCore.Http.Results.BadRequest(new { error });
            AuditLogService.TryRecord("gaming.extension", "removed " + id);
            return Microsoft.AspNetCore.Http.Results.Ok(GameExtensionRuntime.Store.List());
        }).RequireDesktopOwner();
    }
}
