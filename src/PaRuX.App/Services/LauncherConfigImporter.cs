using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PaRuX.Services;

/// <summary>Outcome of reading an owner-built launcher config. Pure data: nothing is applied until the owner confirms.</summary>
public sealed record LauncherImportResult(
    bool Parsed,
    IReadOnlyList<PadButtonBinding> Bindings,
    IReadOnlyList<string> TvNames,
    IReadOnlyList<string> Imported,
    IReadOnlyList<string> NotImported);

/// <summary>
/// Opt-in, read-only migration of the legacy <c>console-config.json</c> written by the owner's small ConsoleMode launcher.
/// Only fields with a native PaRuX equivalent become bindings; every other key is listed in <see cref="LauncherImportResult.NotImported"/>
/// so nothing is silently dropped. The source file is never modified. Bounded input, never throws.
/// </summary>
public static class LauncherConfigImporter
{
    public const int MaxBytes = 256 * 1024;
    private const string StadiaDevice = "18D1:9400";

    // Stadia raw indexes measured on a real pad (see PadActionSettings.DefaultBindings).
    private static readonly Dictionary<string, (string Control, PadTrigger Trigger)> Buttons = new(StringComparer.Ordinal)
    {
        ["assistant"] = (PadControlId.Raw(11), PadTrigger.Tap),
        ["capture"] = (PadControlId.Raw(12), PadTrigger.Tap),
        ["stadia_short"] = (PadControlId.Raw(8), PadTrigger.Tap),
        ["stadia_long"] = (PadControlId.Raw(8), PadTrigger.Hold),
    };

    private static readonly Dictionary<string, PadActionId> Actions = new(StringComparer.Ordinal)
    {
        ["chatgpt_voice"] = PadActionId.MiniAgent,   // the voice assistant becomes PaRuX's local mini agent
        ["overlay"] = PadActionId.OpenMenu,
        ["freeze_playnite"] = PadActionId.PauseResumeGame,
        ["none"] = PadActionId.None,
    };

    public static LauncherImportResult Parse(string? json)
    {
        var imported = new List<string>();
        var skipped = new List<string>();
        var bindings = new List<PadButtonBinding>();
        var tv = new List<string>();

        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes)
            return new LauncherImportResult(false, bindings, tv, imported, new[] { "File is empty or larger than the allowed size." });

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new LauncherImportResult(false, bindings, tv, imported, new[] { "Top level is not a JSON object." });

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "buttons":
                        ReadButtons(prop.Value, bindings, imported, skipped);
                        break;
                    case "tvNames":
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var n in prop.Value.EnumerateArray())
                                if (n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()) && tv.Count < 16)
                                    tv.Add(n.GetString()!.Trim());
                        }
                        if (tv.Count > 0) imported.Add($"tvNames: {tv.Count} name(s) offered as TV hints; pick the TV in Settings > Gaming > Console / TV.");
                        break;
                    case "version":
                        break;
                    default:
                        skipped.Add($"{prop.Name}: no PaRuX equivalent (kept in the original file).");
                        break;
                }
            }
            return new LauncherImportResult(true, PadActionSettings.Normalize(bindings), tv, imported, skipped);
        }
        catch (JsonException ex)
        {
            return new LauncherImportResult(false, bindings, tv, imported, new[] { "Invalid JSON: " + ex.Message });
        }
    }

    private static void ReadButtons(JsonElement buttons, List<PadButtonBinding> bindings, List<string> imported, List<string> skipped)
    {
        if (buttons.ValueKind != JsonValueKind.Object) { skipped.Add("buttons: not an object."); return; }
        foreach (var b in buttons.EnumerateObject())
        {
            string? action = b.Value.ValueKind == JsonValueKind.Object && b.Value.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() : null;
            if (!Buttons.TryGetValue(b.Name, out var slot)) { skipped.Add($"buttons.{b.Name}: unknown button."); continue; }
            if (action is null || !Actions.TryGetValue(action, out var id))
            {
                skipped.Add($"buttons.{b.Name}: action '{action}' has no PaRuX equivalent.");
                continue;
            }
            if (id == PadActionId.None) continue;
            bindings.Add(PadButtonBinding.Make(StadiaDevice, slot.Control, id, slot.Trigger));
            imported.Add($"buttons.{b.Name}: {action} -> {PadActionCatalog.KeyOf(id)} ({slot.Trigger}).");
        }
    }
}
