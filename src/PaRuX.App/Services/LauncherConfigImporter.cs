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
    IReadOnlyList<string> NotImported,
    int? HoldMs = null);

/// <summary>
/// Opt-in, read-only migration of the legacy <c>console-config.json</c> written by the owner's small ConsoleMode launcher.
/// Only fields with a native PaRuX equivalent become bindings; every other key is listed in <see cref="LauncherImportResult.NotImported"/>
/// so nothing is silently dropped. The source file is never modified. Bounded input, never throws.
/// </summary>
public static class LauncherConfigImporter
{
    public const int MaxBytes = 256 * 1024;

    // Legacy launcher buttons are Stadia-named but are logical buttons: each resolves per pad through PadLogicalButtons
    // (Xbox/XInput Guide, preset raw indexes, Stadia measured indexes). Pads without a sourced index are left to the mapping wizard.
    private static readonly Dictionary<string, (PadLogicalButton Button, PadTrigger Trigger)> Buttons = new(StringComparer.Ordinal)
    {
        ["assistant"] = (PadLogicalButton.Assistant, PadTrigger.Tap),
        ["capture"] = (PadLogicalButton.Capture, PadTrigger.Tap),
        ["stadia_short"] = (PadLogicalButton.Guide, PadTrigger.Tap),
        ["stadia_long"] = (PadLogicalButton.Guide, PadTrigger.Hold),
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
        int? holdMs = null;

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
                    case "longPressMs":
                        // The legacy long-press time maps to the one PaRuX hold time (every pad, every hold binding).
                        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int ms)
                            && ms is >= PadActionSettings.MinHoldMs and <= PadActionSettings.MaxHoldMs)
                        {
                            holdMs = ms;
                            imported.Add($"longPressMs: {ms} ms becomes the hold time of every controller binding.");
                        }
                        else skipped.Add($"longPressMs: needs a whole number from {PadActionSettings.MinHoldMs} to {PadActionSettings.MaxHoldMs}; the default {PadActionSettings.HoldMs} ms is kept.");
                        break;
                    case "noFreezeGames":
                        skipped.Add("noFreezeGames: PaRuX only pauses a game it launched itself, and never a game on its anti-cheat list; there is no per-game exclusion list.");
                        break;
                    case "streaming":
                        skipped.Add("streaming: PaRuX streaming mode starts Sunshine and changes resolution/refresh only after explicit consent and never edits Sunshine's own configuration; set it up in Gaming > Streaming.");
                        break;
                    case "desktopMode":
                        skipped.Add("desktopMode: PaRuX never turns a pad into keyboard or mouse input; Console navigation is configured in Settings > Controller buttons > TV mode.");
                        break;
                    case "version":
                        break;
                    default:
                        skipped.Add($"{prop.Name}: no PaRuX equivalent (kept in the original file).");
                        break;
                }
            }
            return new LauncherImportResult(true, PadActionSettings.Normalize(bindings), tv, imported, skipped, holdMs);
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
            if (b.Name == "capture_assistant") { skipped.Add("buttons.capture_assistant: two-button chords are not supported; bind each button on its own."); continue; }
            if (!Buttons.TryGetValue(b.Name, out var slot)) { skipped.Add($"buttons.{b.Name}: unknown button."); continue; }
            if (action is null || !Actions.TryGetValue(action, out var id))
            {
                string why = action switch
                {
                    "desktop_mode" => "PaRuX does not turn a pad into keyboard/mouse; use 'Switch Playnite Console / Desktop' if you meant Playnite's mode",
                    "playnite" => "use 'Open Playnite in Console' for the same effect",
                    "hotkey" or "run" or "osk" or "close_window_confirm" => "PaRuX never sends keystrokes or runs programs from a pad button",
                    _ => "no PaRuX equivalent"
                };
                skipped.Add($"buttons.{b.Name}: action '{action}' not imported ({why}).");
                continue;
            }
            if (id == PadActionId.None) continue;
            var rows = PadLogicalButtons.Expand(slot.Button, id, slot.Trigger);
            if (rows.Count == 0) { skipped.Add($"buttons.{b.Name}: no known pad mapping for this button; assign it in the mapping wizard."); continue; }
            bindings.AddRange(rows);
            string pads = string.Join(", ", rows.Select(r => r.Device == PadControlId.Any ? "any Xbox/XInput pad" : r.Device));
            imported.Add($"buttons.{b.Name}: {action} -> {PadActionCatalog.KeyOf(id)} ({slot.Trigger.ToString().ToLowerInvariant()}) on {pads}.");
        }
    }

    /// <summary>Adds the imported rows to the current table. A row for the same device, control and trigger is replaced; everything else is kept.
    /// Pure: the caller persists the result only after the owner confirmed.</summary>
    public static List<PadButtonBinding> Merge(IEnumerable<PadButtonBinding> current, IEnumerable<PadButtonBinding> imported)
    {
        var incoming = PadActionSettings.Normalize(imported);
        var keys = incoming.Select(b => (b.Device, b.Control, b.Trigger)).ToHashSet();
        var merged = PadActionSettings.Normalize(current).Where(b => !keys.Contains((b.Device, b.Control, b.Trigger))).ToList();
        merged.AddRange(incoming);
        return PadActionSettings.Normalize(merged);
    }
}
