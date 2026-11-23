using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Playnite;

/// <summary>PaRuX's own Playnite preferences in one small atomic JSON file. Playnite's files are never touched.</summary>
public sealed class PlayniteSettingsStore
{
    private const long MaxFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private sealed class Persisted
    {
        public PlayniteSettingsDto Settings { get; set; } = new();
        /// <summary>Folder the owner picked with "Locate existing"; USER_MANAGED, never modified.</summary>
        public string? LocatedFolder { get; set; }
    }

    private readonly string _path;
    private readonly object _gate = new();
    private Persisted _state = new();

    public PlayniteSettingsStore() : this(PaRuXDataRoot.Combine("server", "playnite-integration.json")) { }

    public PlayniteSettingsStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= MaxFileBytes)
                _state = Sanitize(JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path), JsonOptions) ?? new());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _state = new(); }
    }

    private static Persisted Sanitize(Persisted p)
    {
        p.Settings ??= new();
        if (!PlayniteModes.IsValid(p.Settings.StartMode)) p.Settings.StartMode = PlayniteModes.Desktop;
        if (p.Settings.FallbackWithoutDisplay is not (ConsoleStartupPolicy.FallbackNormal or ConsoleStartupPolicy.FallbackMinimized))
            p.Settings.FallbackWithoutDisplay = ConsoleStartupPolicy.FallbackNormal;
        if (p.Settings.TargetDisplayId is { Length: > 256 }) p.Settings.TargetDisplayId = null;
        return p;
    }

    public PlayniteSettingsDto Get() { lock (_gate) return Copy(_state.Settings); }
    public string? LocatedFolder { get { lock (_gate) return _state.LocatedFolder; } }

    public (PlayniteSettingsDto? Saved, string? Error) Save(PlayniteSettingsDto input)
    {
        if (input is null) return (null, "No settings were sent.");
        if (!PlayniteModes.IsValid(input.StartMode)) return (null, "The start mode must be 'desktop' or 'console'.");
        if (input.FallbackWithoutDisplay is not (ConsoleStartupPolicy.FallbackNormal or ConsoleStartupPolicy.FallbackMinimized))
            return (null, "The fallback must be 'normal' or 'minimized'.");
        if (input.TargetDisplayId is { Length: > 256 }) return (null, "The display id is too long.");
        lock (_gate)
        {
            var next = new Persisted { Settings = Copy(input), LocatedFolder = _state.LocatedFolder };
            return Persist(next) ? (Copy(next.Settings), null) : (null, "The Playnite settings could not be saved.");
        }
    }

    public bool SetLocatedFolder(string? folder)
    {
        lock (_gate) return Persist(new Persisted { Settings = Copy(_state.Settings), LocatedFolder = folder });
    }

    public bool SetSkipped(bool skipped)
    {
        lock (_gate)
        {
            PlayniteSettingsDto s = Copy(_state.Settings);
            s.Skipped = skipped;
            return Persist(new Persisted { Settings = s, LocatedFolder = _state.LocatedFolder });
        }
    }

    private bool Persist(Persisted next)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, JsonOptions));
            File.Move(temp, _path, overwrite: true);
            _state = next;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static PlayniteSettingsDto Copy(PlayniteSettingsDto s) => new()
    {
        AiYieldEnabled = s.AiYieldEnabled, StartMode = s.StartMode, TargetDisplayId = s.TargetDisplayId,
        ConsoleOnlyWhenTargetConnected = s.ConsoleOnlyWhenTargetConnected, FallbackWithoutDisplay = s.FallbackWithoutDisplay,
        ReturnToPaRuXOnClose = s.ReturnToPaRuXOnClose, LaunchAtStartup = s.LaunchAtStartup, ConsoleStartsPlaynite = s.ConsoleStartsPlaynite, Skipped = s.Skipped
    };
}
