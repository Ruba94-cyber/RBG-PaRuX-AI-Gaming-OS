using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

namespace PaRuX.Services;

/// <summary>Buttons that exist on several pad families under different physical names (Xbox Guide, PlayStation PS, Switch Home, Stadia button).</summary>
public enum PadLogicalButton
{
    /// <summary>The central "home" button: Xbox Guide, PS button, Switch Home, Stadia button.</summary>
    Guide,
    /// <summary>The extra assistant button. Only the Stadia pad has one with a measured index; other pads are left to the mapping wizard.</summary>
    Assistant,
    /// <summary>The capture/share button. Only the Stadia pad has one with a measured index; other pads are left to the mapping wizard.</summary>
    Capture
}

/// <summary>One concrete control a logical button resolves to for a device key ("any" or "VID:PID").</summary>
public sealed record PadLogicalTarget(string Device, string Control, bool Measured, string Source);

/// <summary>
/// Resolves logical buttons to concrete control ids per pad using data the repository already holds: XInput names for Xbox-family pads
/// (a single "any" row, since "x:" ids only come from XInput), and the raw indexes of <see cref="PadMappingPresets"/> for raw HID pads.
/// Nothing is invented: a pad without a sourced index resolves to nothing and stays with the guided mapping wizard.
/// </summary>
public static class PadLogicalButtons
{
    private const ushort StadiaVid = 0x18D1, StadiaPid = 0x9400;

    // Stadia indexes measured on a real pad (see PadActionSettings.DefaultBindings and the "stadia" preset note).
    private static readonly Dictionary<PadLogicalButton, int> StadiaMeasured = new()
    {
        [PadLogicalButton.Guide] = 8,
        [PadLogicalButton.Assistant] = 11,
        [PadLogicalButton.Capture] = 12,
    };

    /// <summary>Every known concrete control for a logical button (XInput first, then raw pads from the preset table).</summary>
    public static IReadOnlyList<PadLogicalTarget> Resolve(PadLogicalButton button)
    {
        var list = new List<PadLogicalTarget>();
        if (button == PadLogicalButton.Guide)
        {
            list.Add(new PadLogicalTarget(PadControlId.Any, PadControlId.Xbox("Guide"), true, "Xbox / XInput Guide"));
            foreach (PadMappingPreset p in PadMappingPresets.All)
            {
                if (PadMappingPresets.IsXInputFamily(p.VendorId, p.ProductId)) continue;   // read through XInput: covered by the "any" row
                foreach (var kv in p.Mapping.Buttons)
                {
                    if (kv.Value != Xbox360Report.Guide) continue;
                    bool measured = p.VendorId == StadiaVid && p.ProductId == StadiaPid;
                    list.Add(new PadLogicalTarget(PadControlId.DeviceKey(p.VendorId, p.ProductId), PadControlId.Raw(kv.Key), measured, p.Name));
                    break;
                }
            }
        }
        else if (StadiaMeasured.TryGetValue(button, out int index))
        {
            list.Add(new PadLogicalTarget(PadControlId.DeviceKey(StadiaVid, StadiaPid), PadControlId.Raw(index), true, "Google Stadia controller"));
        }
        return list;
    }

    /// <summary>The control a logical button maps to on one pad, or null when no sourced mapping exists (use the mapping wizard).</summary>
    public static string? ControlFor(ushort vendorId, ushort productId, PadLogicalButton button)
    {
        if (PadMappingPresets.IsXInputFamily(vendorId, productId))
            return button == PadLogicalButton.Guide ? PadControlId.Xbox("Guide") : null;
        string key = PadControlId.DeviceKey(vendorId, productId);
        return Resolve(button).FirstOrDefault(t => t.Device == key)?.Control;
    }

    /// <summary>Bindings for a logical button on every pad that has a sourced mapping. Empty when nothing is known.</summary>
    public static IReadOnlyList<PadButtonBinding> Expand(PadLogicalButton button, PadActionId action, PadTrigger trigger = PadTrigger.Tap) =>
        action == PadActionId.None
            ? Array.Empty<PadButtonBinding>()
            : Resolve(button).Select(t => PadButtonBinding.Make(t.Device, t.Control, action, trigger)).ToList();
}
