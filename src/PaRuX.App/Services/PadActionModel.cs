using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

namespace PaRuX.Services;

/// <summary>The closed list of PaRuX actions a controller control can trigger. Ids are persisted: never rename one.</summary>
public enum PadActionId
{
    None,
    OpenMenu,
    GamingLibrary,
    SwitchWorkspace,
    MiniAgent,
    Dictation,
    Screenshot,
    PauseResumeGame,
    OpenSettings,
    SwitchPlaynite
}

public sealed record PadActionInfo(PadActionId Id, string Key, string En, string It, bool NeedsLocalAi, bool Available)
{
    public string Label(bool italian) => italian ? It : En;
}

public static class PadActionCatalog
{
    public static IReadOnlyList<PadActionInfo> All { get; } = new[]
    {
        new PadActionInfo(PadActionId.None, "none", "None", "Nessuna", false, true),
        new PadActionInfo(PadActionId.OpenMenu, "openMenu", "Open PaRuX menu (keeps the game running)", "Apri il menu PaRuX (il gioco resta in esecuzione)", false, true),
        new PadActionInfo(PadActionId.GamingLibrary, "gamingLibrary", "Open Games tools (Desktop) / library (legacy TV)", "Apri strumenti Giochi (Desktop) / libreria (TV precedente)", false, true),
        new PadActionInfo(PadActionId.SwitchWorkspace, "switchWorkspace", "Switch Gaming / AI", "Passa tra Gaming e AI", false, true),
        new PadActionInfo(PadActionId.MiniAgent, "miniAgent", "Start mini agent (Quick Chat)", "Avvia il mini agente (Quick Chat)", true, true),
        new PadActionInfo(PadActionId.Dictation, "dictation", "Dictation (push-to-talk toggle)", "Dettatura (attiva/disattiva push-to-talk)", true, true),
        new PadActionInfo(PadActionId.Screenshot, "screenshot", "Screenshot of the focused window", "Screenshot della finestra attiva", false, true),
        new PadActionInfo(PadActionId.PauseResumeGame, "pauseResume", "Pause / resume the running game", "Pausa / riprendi il gioco in corso", false, true),
        new PadActionInfo(PadActionId.OpenSettings, "openSettings", "Open Settings > Controller buttons", "Apri Impostazioni > Pulsanti del controller", false, true),
        new PadActionInfo(PadActionId.SwitchPlaynite, "switchPlaynite", "Switch Playnite Console / Desktop", "Passa Playnite tra Console e Desktop", false, true),
    };

    public static PadActionInfo Get(PadActionId id) => All.First(a => a.Id == id);

    public static bool TryParse(string? key, out PadActionId id)
    {
        id = PadActionId.None;
        var hit = All.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));
        if (hit is null) return false;
        id = hit.Id;
        return true;
    }

    public static string KeyOf(PadActionId id) => Get(id).Key;
}

/// <summary>How a binding is triggered: a tap fires on press (or on release when the same control also has a hold binding), a hold after the hold time.</summary>
public enum PadTrigger { Tap, Hold }

/// <summary>
/// Parsing and naming of controller control ids. Ids: "raw:N" (raw HID button index 0..63), "hat:Up|Down|Left|Right" (raw hat),
/// "x:NAME" (Xbox-style name as seen through XInput).
/// </summary>
public static class PadControlId
{
    public const int MaxRawIndex = 63;
    public const string Any = "any";

    public static readonly string[] XboxNames =
    {
        "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "L3", "R3", "Back", "Start", "Guide",
        "DUp", "DDown", "DLeft", "DRight", "LSUp", "LSDown", "LSLeft", "LSRight", "RSUp", "RSDown", "RSLeft", "RSRight"
    };

    private static readonly string[] HatNames = { "Up", "Down", "Left", "Right" };

    public static string Raw(int index) => "raw:" + index.ToString(CultureInfo.InvariantCulture);
    public static string Xbox(string name) => "x:" + name;
    public static string Hat(string name) => "hat:" + name;

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 16) return false;
        if (id.StartsWith("raw:", StringComparison.Ordinal))
            return int.TryParse(id.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 0 and <= MaxRawIndex
                && id == Raw(n);   // canonical form only ("raw:07" is rejected so a row can never be duplicated by spelling)
        if (id.StartsWith("x:", StringComparison.Ordinal)) return XboxNames.Contains(id[2..], StringComparer.Ordinal);
        if (id.StartsWith("hat:", StringComparison.Ordinal)) return HatNames.Contains(id[4..], StringComparer.Ordinal);
        return false;
    }

    public static bool TryRawIndex(string id, out int index)
    {
        index = -1;
        return id.StartsWith("raw:", StringComparison.Ordinal) && IsValid(id)
            && int.TryParse(id.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    /// <summary>"18D1:9400" (upper-case hex) for a pad.</summary>
    public static string DeviceKey(ushort vendorId, ushort productId) => $"{vendorId:X4}:{productId:X4}";

    public static bool IsValidDeviceKey(string? key)
    {
        if (string.Equals(key, Any, StringComparison.Ordinal)) return true;
        if (key is null || key.Length != 9 || key[4] != ':') return false;
        for (int i = 0; i < 9; i++)
        {
            if (i == 4) continue;
            char c = key[i];
            if (!(c is >= '0' and <= '9' || c is >= 'A' and <= 'F')) return false;
        }
        return true;
    }
}

/// <summary>One persisted rule: on this pad (or any pad), this control, tapped or held, runs this PaRuX action.</summary>
public sealed record PadButtonBinding(string Device, string Control, string Action, string Trigger = "tap")
{
    public PadActionId ActionId => PadActionCatalog.TryParse(Action, out var id) ? id : PadActionId.None;
    public PadTrigger TriggerKind => string.Equals(Trigger, "hold", StringComparison.Ordinal) ? PadTrigger.Hold : PadTrigger.Tap;

    /// <summary>True when every field is well formed; an invalid binding is dropped on load.</summary>
    public bool IsValid() =>
        PadControlId.IsValidDeviceKey(Device) && PadControlId.IsValid(Control)
        && PadActionCatalog.TryParse(Action, out _) && (Trigger is "tap" or "hold");

    public static PadButtonBinding Make(string device, string control, PadActionId action, PadTrigger trigger = PadTrigger.Tap) =>
        new(device, control, PadActionCatalog.KeyOf(action), trigger == PadTrigger.Hold ? "hold" : "tap");
}

/// <summary>A flagged problem in the binding table (shown in the Settings UI, never silently dropped).</summary>
public sealed record PadBindingConflict(string Device, string Control, string Trigger, string Message);

/// <summary>Everything about controller buttons that is persisted in <c>AppSettings.PadActions</c>.</summary>
[JsonConverter(typeof(PadActionSettingsJsonConverter))]
public sealed class PadActionSettings
{
    public const int CurrentVersion = 1;
    public const int MaxBindings = 128;
    public const int MaxTvOverrides = 48;
    public const long HoldMs = 600;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>"Use controller buttons for PaRuX actions". Default on (only effective with at least one binding).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Owner-edited table. Null means "never edited": the built-in defaults apply. An empty list means "owner cleared everything".</summary>
    public List<PadButtonBinding>? Bindings { get; set; }

    /// <summary>Console/TV navigation overrides on top of the built-in defaults: control id to <see cref="TvAction"/> key. Null = defaults.</summary>
    public Dictionary<string, string>? Tv { get; set; }

    /// <summary>Built-in defaults: only a Google Stadia pad (VID 18D1 PID 9400) has any; every other pad is unbound.
    /// Raw indexes measured on a real Stadia pad: Guide 8, Assistant 11, Capture 12.</summary>
    public static IReadOnlyList<PadButtonBinding> DefaultBindings { get; } = new[]
    {
        PadButtonBinding.Make("18D1:9400", PadControlId.Raw(8), PadActionId.OpenMenu),
        PadButtonBinding.Make("18D1:9400", PadControlId.Raw(11), PadActionId.MiniAgent),
        PadButtonBinding.Make("18D1:9400", PadControlId.Raw(12), PadActionId.Screenshot),
    };

    public IReadOnlyList<PadButtonBinding> EffectiveBindings =>
        Enabled ? Normalize(Bindings ?? DefaultBindings.ToList()) : new List<PadButtonBinding>();

    public ConsoleNavigationBindings EffectiveTv => ConsoleNavigationBindings.FromStored(Tv);

    /// <summary>Drops invalid and duplicate rows and enforces the size bounds. Never throws.</summary>
    public static List<PadButtonBinding> Normalize(IEnumerable<PadButtonBinding?>? raw, List<PadBindingConflict>? conflicts = null)
    {
        var result = new List<PadButtonBinding>();
        if (raw is null) return result;
        var seen = new HashSet<(string, string, string)>();
        foreach (PadButtonBinding? b in raw)
        {
            if (b is null || !b.IsValid()) continue;
            if (b.ActionId == PadActionId.None) continue;   // "None" is the absence of a row
            if (!seen.Add((b.Device, b.Control, b.Trigger)))
            {
                conflicts?.Add(new PadBindingConflict(b.Device, b.Control, b.Trigger, "Duplicate row: only the first assignment is used."));
                continue;
            }
            if (result.Count >= MaxBindings) break;
            result.Add(b);
        }
        return result;
    }

    /// <summary>Conflicts the owner should see: duplicate rows, and a pad-specific row that overrides an "any pad" row.</summary>
    public static List<PadBindingConflict> FindConflicts(IEnumerable<PadButtonBinding?>? raw)
    {
        var conflicts = new List<PadBindingConflict>();
        var rows = Normalize(raw, conflicts);
        foreach (var any in rows.Where(r => r.Device == PadControlId.Any))
            if (rows.Any(r => r.Device != PadControlId.Any && r.Control == any.Control && r.Trigger == any.Trigger))
                conflicts.Add(new PadBindingConflict(any.Device, any.Control, any.Trigger, "A pad-specific assignment overrides the 'any pad' one for this control."));
        return conflicts;
    }

    public PadActionSettings Clone() => new()
    {
        Version = Version, Enabled = Enabled,
        Bindings = Bindings?.ToList(),
        Tv = Tv is null ? null : new Dictionary<string, string>(Tv)
    };
}

internal sealed class PadActionSettingsJsonConverter : JsonConverter<PadActionSettings>
{
    public override bool HandleNull => true;

    public override PadActionSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Version tolerant and never throws: anything unexpected falls back to the safe defaults instead of failing the whole settings file.
        var result = new PadActionSettings();
        try
        {
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;
            foreach (JsonProperty p in root.EnumerateObject())
            {
                switch (p.Name.ToLowerInvariant())
                {
                    case "enabled":
                        if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) result.Enabled = p.Value.GetBoolean();
                        break;
                    case "bindings":
                        if (p.Value.ValueKind == JsonValueKind.Array) result.Bindings = ReadBindings(p.Value);
                        break;
                    case "tv":
                        if (p.Value.ValueKind == JsonValueKind.Object) result.Tv = ReadTv(p.Value);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new PadActionSettings();
        }
        return result;
    }

    private static List<PadButtonBinding> ReadBindings(JsonElement array)
    {
        var rows = new List<PadButtonBinding?>();
        int seen = 0;
        foreach (JsonElement e in array.EnumerateArray())
        {
            if (++seen > PadActionSettings.MaxBindings * 2) break;   // bounded work even for a hostile file
            if (e.ValueKind != JsonValueKind.Object) continue;
            rows.Add(new PadButtonBinding(Str(e, "device"), Str(e, "control"), Str(e, "action"), Str(e, "trigger", "tap")));
        }
        return PadActionSettings.Normalize(rows);
    }

    private static Dictionary<string, string> ReadTv(JsonElement obj)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (map.Count >= PadActionSettings.MaxTvOverrides) break;
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            if (!ConsoleNavigationBindings.IsKnownControl(p.Name) || !TvActionCatalog.TryParse(p.Value.GetString(), out _)) continue;
            map[p.Name] = p.Value.GetString()!;
        }
        return map;
    }

    private static string Str(JsonElement e, string name, string fallback = "")
    {
        foreach (JsonProperty p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
            {
                string s = p.Value.GetString() ?? fallback;
                return s.Length > 40 ? string.Empty : s;
            }
        return fallback;
    }

    public override void Write(Utf8JsonWriter writer, PadActionSettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", PadActionSettings.CurrentVersion);
        writer.WriteBoolean("enabled", value.Enabled);
        if (value.Bindings is not null)
        {
            writer.WriteStartArray("bindings");
            foreach (PadButtonBinding b in PadActionSettings.Normalize(value.Bindings))
            {
                writer.WriteStartObject();
                writer.WriteString("device", b.Device);
                writer.WriteString("control", b.Control);
                writer.WriteString("action", b.Action);
                writer.WriteString("trigger", b.Trigger);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (value.Tv is not null)
        {
            writer.WriteStartObject("tv");
            foreach (var (control, action) in value.Tv.Take(PadActionSettings.MaxTvOverrides))
                if (ConsoleNavigationBindings.IsKnownControl(control) && TvActionCatalog.TryParse(action, out _))
                    writer.WriteString(control, action);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}

/// <summary>Console/TV navigation actions that can be assigned to a control. Persisted by key.</summary>
public enum TvAction
{
    None, Accept, Back, Up, Down, Left, Right, PagePrevious, PageNext, ScrollUp, ScrollDown, Search, SwitchWorkspace, OpenMenu
}

public sealed record TvActionInfo(TvAction Id, string Key, string En, string It);

public static class TvActionCatalog
{
    public static IReadOnlyList<TvActionInfo> All { get; } = new[]
    {
        new TvActionInfo(TvAction.None, "none", "Nothing", "Niente"),
        new TvActionInfo(TvAction.Accept, "accept", "Accept", "Conferma"),
        new TvActionInfo(TvAction.Back, "back", "Back / Cancel", "Indietro / Annulla"),
        new TvActionInfo(TvAction.Up, "up", "Move up", "Su"),
        new TvActionInfo(TvAction.Down, "down", "Move down", "Giù"),
        new TvActionInfo(TvAction.Left, "left", "Move left", "Sinistra"),
        new TvActionInfo(TvAction.Right, "right", "Move right", "Destra"),
        new TvActionInfo(TvAction.PagePrevious, "pagePrev", "Previous page", "Pagina precedente"),
        new TvActionInfo(TvAction.PageNext, "pageNext", "Next page", "Pagina successiva"),
        new TvActionInfo(TvAction.ScrollUp, "scrollUp", "Scroll up", "Scorri su"),
        new TvActionInfo(TvAction.ScrollDown, "scrollDown", "Scroll down", "Scorri giù"),
        new TvActionInfo(TvAction.Search, "search", "Search", "Cerca"),
        new TvActionInfo(TvAction.SwitchWorkspace, "switch", "Switch Gaming / AI", "Passa tra Gaming e AI"),
        new TvActionInfo(TvAction.OpenMenu, "menu", "Open PaRuX menu", "Apri il menu PaRuX"),
    };

    public static TvActionInfo Get(TvAction id) => All.First(a => a.Id == id);

    public static bool TryParse(string? key, out TvAction id)
    {
        id = TvAction.None;
        var hit = All.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));
        if (hit is null) return false;
        id = hit.Id;
        return true;
    }
}

/// <summary>
/// Console/TV navigation bindings: control id ("x:A", "x:DUp", "x:RSDown"...) to <see cref="TvAction"/>. Pure and persistable:
/// the default table is exactly today's navigation (A accept, B back, View switches Gaming/AI, Menu search, shoulders page,
/// right stick scrolls, D-pad and left stick move). Only differences from the defaults are stored, so the persisted size is bounded
/// and "Restore defaults" is simply "store nothing".
/// </summary>
public sealed class ConsoleNavigationBindings
{
    public static readonly string[] Controls =
    {
        "x:A", "x:B", "x:X", "x:Y", "x:LB", "x:RB", "x:LT", "x:RT", "x:L3", "x:R3", "x:Back", "x:Start", "x:Guide",
        "x:DUp", "x:DDown", "x:DLeft", "x:DRight", "x:LSUp", "x:LSDown", "x:LSLeft", "x:LSRight", "x:RSUp", "x:RSDown", "x:RSLeft", "x:RSRight"
    };

    public static bool IsKnownControl(string? id) => id is not null && Array.IndexOf(Controls, id) >= 0;

    private static readonly Dictionary<string, TvAction> DefaultMap = new(StringComparer.Ordinal)
    {
        ["x:A"] = TvAction.Accept, ["x:B"] = TvAction.Back, ["x:Back"] = TvAction.SwitchWorkspace, ["x:Start"] = TvAction.Search,
        ["x:LB"] = TvAction.PagePrevious, ["x:RB"] = TvAction.PageNext,
        ["x:DUp"] = TvAction.Up, ["x:DDown"] = TvAction.Down, ["x:DLeft"] = TvAction.Left, ["x:DRight"] = TvAction.Right,
        ["x:LSUp"] = TvAction.Up, ["x:LSDown"] = TvAction.Down, ["x:LSLeft"] = TvAction.Left, ["x:LSRight"] = TvAction.Right,
        ["x:RSUp"] = TvAction.ScrollUp, ["x:RSDown"] = TvAction.ScrollDown,
    };

    private readonly Dictionary<string, TvAction> _map;

    private ConsoleNavigationBindings(Dictionary<string, TvAction> map) => _map = map;

    public static ConsoleNavigationBindings Defaults() => new(new Dictionary<string, TvAction>(DefaultMap, StringComparer.Ordinal));

    /// <summary>Defaults plus the validated overrides; unknown controls/actions and oversize input are ignored. Never throws.</summary>
    public static ConsoleNavigationBindings FromStored(IReadOnlyDictionary<string, string>? stored)
    {
        var map = new Dictionary<string, TvAction>(DefaultMap, StringComparer.Ordinal);
        if (stored is null) return new ConsoleNavigationBindings(map);
        int n = 0;
        foreach (var (control, action) in stored)
        {
            if (++n > PadActionSettings.MaxTvOverrides) break;
            if (!IsKnownControl(control) || !TvActionCatalog.TryParse(action, out TvAction id)) continue;
            if (id == TvAction.None) map.Remove(control); else map[control] = id;
        }
        return new ConsoleNavigationBindings(map);
    }

    public TvAction Resolve(string control) => _map.TryGetValue(control, out var a) ? a : TvAction.None;

    public ConsoleNavigationBindings With(string control, TvAction action)
    {
        var copy = new Dictionary<string, TvAction>(_map, StringComparer.Ordinal);
        if (IsKnownControl(control)) { if (action == TvAction.None) copy.Remove(control); else copy[control] = action; }
        return new ConsoleNavigationBindings(copy);
    }

    /// <summary>The overrides to persist (differences from the defaults only); null when this equals the defaults.</summary>
    public Dictionary<string, string>? ToStored()
    {
        var diff = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string c in Controls)
        {
            TvAction mine = Resolve(c);
            DefaultMap.TryGetValue(c, out TvAction def);
            if (mine != def) diff[c] = TvActionCatalog.Get(mine).Key;
        }
        return diff.Count == 0 ? null : diff;
    }

    /// <summary>Controls assigned to an action that only makes sense on one control at a time are not a conflict; this lists actions with no control at all
    /// (the owner could lock themselves out of Accept or Back) so the UI can warn.</summary>
    public IReadOnlyList<TvAction> UnreachableEssentials()
    {
        var present = new HashSet<TvAction>(_map.Values);
        return new[] { TvAction.Accept, TvAction.Back, TvAction.Up, TvAction.Down, TvAction.Left, TvAction.Right }
            .Where(a => !present.Contains(a)).ToList();
    }

    /// <summary>Maps a TV action to the navigator's vocabulary. Accept is reported through the flag; OpenMenu has no navigator action
    /// (the host handles it) and returns (None, false).</summary>
    public static (ConsoleNavigationAction Action, bool Accept) ToNavigation(TvAction action) => action switch
    {
        TvAction.Accept => (ConsoleNavigationAction.None, true),
        TvAction.Back => (ConsoleNavigationAction.Exit, false),
        TvAction.Up => (ConsoleNavigationAction.Up, false),
        TvAction.Down => (ConsoleNavigationAction.Down, false),
        TvAction.Left => (ConsoleNavigationAction.Left, false),
        TvAction.Right => (ConsoleNavigationAction.Right, false),
        TvAction.PagePrevious => (ConsoleNavigationAction.PreviousPage, false),
        TvAction.PageNext => (ConsoleNavigationAction.NextPage, false),
        TvAction.ScrollUp => (ConsoleNavigationAction.ScrollUp, false),
        TvAction.ScrollDown => (ConsoleNavigationAction.ScrollDown, false),
        TvAction.Search => (ConsoleNavigationAction.FocusSearch, false),
        TvAction.SwitchWorkspace => (ConsoleNavigationAction.SwitchWorkspace, false),
        _ => (ConsoleNavigationAction.None, false)
    };
}

/// <summary>A row of the Settings table: one control of a pad.</summary>
public sealed record PadControlInfo(string Id, string En, string It, int? RawIndex);

/// <summary>Which controls to list for a pad, with human labels (Xbox-style for mapped buttons, raw for extras).</summary>
public static class PadControlCatalog
{
    private static readonly Dictionary<string, (string En, string It)> XboxLabels = new()
    {
        ["A"] = ("A", "A"), ["B"] = ("B", "B"), ["X"] = ("X", "X"), ["Y"] = ("Y", "Y"), ["LB"] = ("LB", "LB"), ["RB"] = ("RB", "RB"),
        ["LT"] = ("LT (left trigger)", "LT (grilletto sinistro)"), ["RT"] = ("RT (right trigger)", "RT (grilletto destro)"),
        ["L3"] = ("L3 (left stick click)", "L3 (click stick sinistro)"), ["R3"] = ("R3 (right stick click)", "R3 (click stick destro)"),
        ["Back"] = ("Back / View", "Indietro / View"), ["Start"] = ("Start / Menu", "Start / Menu"), ["Guide"] = ("Guide", "Guide"),
        ["DUp"] = ("D-pad up", "Croce su"), ["DDown"] = ("D-pad down", "Croce giù"), ["DLeft"] = ("D-pad left", "Croce sinistra"), ["DRight"] = ("D-pad right", "Croce destra"),
        ["LSUp"] = ("Left stick up", "Stick sinistro su"), ["LSDown"] = ("Left stick down", "Stick sinistro giù"),
        ["LSLeft"] = ("Left stick left", "Stick sinistro sinistra"), ["LSRight"] = ("Left stick right", "Stick sinistro destra"),
        ["RSUp"] = ("Right stick up", "Stick destro su"), ["RSDown"] = ("Right stick down", "Stick destro giù"),
        ["RSLeft"] = ("Right stick left", "Stick destro sinistra"), ["RSRight"] = ("Right stick right", "Stick destro destra"),
    };

    /// <summary>Raw layout measured on a real Google Stadia pad (BLE, 15 buttons): index to Xbox-style name or extra control.</summary>
    private static readonly (int Index, string En, string It)[] StadiaRaw =
    {
        (0, "A", "A"), (1, "B", "B"), (2, "X", "X"), (3, "Y", "Y"), (4, "LB", "LB"), (5, "RB", "RB"),
        (6, "Select (Back)", "Select (Indietro)"), (7, "Start", "Start"), (8, "Stadia (Guide)", "Stadia (Guide)"),
        (9, "L3", "L3"), (10, "R3", "R3"), (11, "Assistant", "Assistant"), (12, "Capture", "Cattura"),
        (13, "RT (digital)", "RT (digitale)"), (14, "LT (digital)", "LT (digitale)"),
    };

    public static string XboxLabel(string name, bool italian) => XboxLabels.TryGetValue(name, out var l) ? (italian ? l.It : l.En) : name;

    /// <summary>Every Xbox-style control: for XInput pads (Xbox, DS4Windows/Steam Input output, virtual pads) and for "any pad".</summary>
    public static IReadOnlyList<PadControlInfo> ForXInput() =>
        PadControlId.XboxNames.Select(n => new PadControlInfo(PadControlId.Xbox(n), XboxLabels[n].En, XboxLabels[n].It, null)).ToList();

    /// <summary>Every control of a raw HID pad: all its buttons (labelled from the Stadia table, or from the preset mapping, else "Button N") and the hat.</summary>
    public static IReadOnlyList<PadControlInfo> ForRaw(ushort vendorId, ushort productId, int buttonCount, bool hasHat)
    {
        var rows = new List<PadControlInfo>();
        int count = Math.Clamp(buttonCount, 0, PadControlId.MaxRawIndex + 1);
        PadMappingPreset? preset = PadMappingPresets.Find(vendorId, productId);
        bool stadia = vendorId == 0x18D1 && productId == 0x9400;
        for (int i = 0; i < count; i++)
        {
            string en = $"Button {i}", it = $"Pulsante {i}";
            if (stadia && StadiaRaw.FirstOrDefault(s => s.Index == i) is { En: not null } s) { en = s.En; it = s.It; }
            else if (!stadia && preset is not null && preset.Mapping.Buttons.TryGetValue(i, out ushort bit) && BitName(bit) is { } name)
            { en = $"{name} (button {i})"; it = $"{name} (pulsante {i})"; }
            rows.Add(new PadControlInfo(PadControlId.Raw(i), en, it, i));
        }
        if (hasHat)
            foreach (var (n, en, it) in new[] { ("Up", "D-pad up", "Croce su"), ("Down", "D-pad down", "Croce giù"), ("Left", "D-pad left", "Croce sinistra"), ("Right", "D-pad right", "Croce destra") })
                rows.Add(new PadControlInfo(PadControlId.Hat(n), en, it, null));
        return rows;
    }

    private static string? BitName(ushort bit) => bit switch
    {
        Xbox360Report.A => "A", Xbox360Report.B => "B", Xbox360Report.X => "X", Xbox360Report.Y => "Y",
        Xbox360Report.LeftShoulder => "LB", Xbox360Report.RightShoulder => "RB", Xbox360Report.Back => "Back", Xbox360Report.Start => "Start",
        Xbox360Report.LeftThumb => "L3", Xbox360Report.RightThumb => "R3", Xbox360Report.Guide => "Guide", _ => null
    };
}

/// <summary>
/// The "Passes to the game as" column: edits the EXISTING <see cref="PadMapping"/> of a raw pad (the same data the mapping wizard and
/// <see cref="RawPadMappingStore"/> use; there is no parallel store). Pure, so it is unit-tested without hardware.
/// </summary>
public static class PadPassThroughEditor
{
    public static readonly string[] Targets =
    {
        "none", "A", "B", "X", "Y", "LB", "RB", "L3", "R3", "Back", "Start", "Guide", "LT", "RT", "DUp", "DDown", "DLeft", "DRight"
    };

    private static ushort Bit(string target) => target switch
    {
        "A" => Xbox360Report.A, "B" => Xbox360Report.B, "X" => Xbox360Report.X, "Y" => Xbox360Report.Y,
        "LB" => Xbox360Report.LeftShoulder, "RB" => Xbox360Report.RightShoulder, "L3" => Xbox360Report.LeftThumb, "R3" => Xbox360Report.RightThumb,
        "Back" => Xbox360Report.Back, "Start" => Xbox360Report.Start, "Guide" => Xbox360Report.Guide,
        "DUp" => Xbox360Report.DpadUp, "DDown" => Xbox360Report.DpadDown, "DLeft" => Xbox360Report.DpadLeft, "DRight" => Xbox360Report.DpadRight,
        _ => 0
    };

    private static string? NameOf(ushort bit)
    {
        foreach (string t in Targets) if (t != "none" && t != "LT" && t != "RT" && Bit(t) == bit) return t;
        return null;
    }

    /// <summary>The virtual Xbox control that raw button <paramref name="index"/> feeds today ("none" when it feeds nothing).</summary>
    public static string TargetOf(PadMapping mapping, int index)
    {
        if (mapping.Buttons.TryGetValue(index, out ushort bit) && NameOf(bit) is { } n) return n;
        if (mapping.LeftTriggerButton == index) return "LT";
        if (mapping.RightTriggerButton == index) return "RT";
        return "none";
    }

    /// <summary>Returns a copy of the mapping where raw button <paramref name="index"/> feeds <paramref name="target"/> ("none" swallows it).
    /// Any previous assignment of that button is removed. Unknown targets or indexes leave the mapping unchanged.</summary>
    public static PadMapping SetTarget(PadMapping mapping, int index, string target)
    {
        PadMapping copy = mapping.Clone();
        if (index is < 0 or > PadControlId.MaxRawIndex || Array.IndexOf(Targets, target) < 0) return copy;
        copy.Buttons.Remove(index);
        if (copy.LeftTriggerButton == index) copy.LeftTriggerButton = null;
        if (copy.RightTriggerButton == index) copy.RightTriggerButton = null;
        switch (target)
        {
            case "none": break;
            case "LT": copy.LeftTriggerButton = index; break;
            case "RT": copy.RightTriggerButton = index; break;
            default: copy.Buttons[index] = Bit(target); break;
        }
        return copy;
    }

    /// <summary>Virtual controls fed by more than one physical button (flagged in the UI; allowed, since two buttons may share a role on purpose).</summary>
    public static IReadOnlyList<string> Conflicts(PadMapping mapping, int buttonCount)
    {
        var byTarget = new Dictionary<string, int>();
        for (int i = 0; i < Math.Min(buttonCount, PadControlId.MaxRawIndex + 1); i++)
        {
            string t = TargetOf(mapping, i);
            if (t == "none") continue;
            byTarget[t] = byTarget.GetValueOrDefault(t) + 1;
        }
        return byTarget.Where(kv => kv.Value > 1).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }
}
