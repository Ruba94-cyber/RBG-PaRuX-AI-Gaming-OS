using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

/// <summary>How a physical pad reaches Windows games.</summary>
public enum RawPadRoute
{
    /// <summary>Xbox-family pad: Windows games read it directly through XInput; no forwarding is needed.</summary>
    XInputDirect,
    /// <summary>Raw HID pad: games do not understand it until PaRuX forwards it to a virtual Xbox 360 pad (ViGEm) and hides the physical one (HidHide).</summary>
    NeedsForwarding
}

/// <summary>
/// A built-in starting mapping for a known pad layout, keyed by USB/Bluetooth vendor and product id. Indexes are Windows
/// RawGameController indexes: buttons in HID usage order (0-based), axes in HID axis order (X, Y, Z, Rx, Ry, Rz...), and
/// switch 0 as the hat. Every preset is a proposal: <see cref="Verified"/> stays false until the owner has confirmed the
/// numbers against the real pad (Live view, Read once or the guided mapping wizard), so the UI always asks for confirmation.
/// </summary>
public sealed record PadMappingPreset(
    string Id, string Name, ushort VendorId, ushort ProductId, string Layout, PadMapping Mapping, bool Verified)
{
    /// <summary>Why the preset is not verified, shown next to the proposal.</summary>
    public string Note { get; init; } = "Unverified - confirm in the mapping wizard.";
}

/// <summary>Built-in preset table and the XInput-versus-raw classification used by the Devices panel.</summary>
public static class PadMappingPresets
{
    private const ushort Microsoft = 0x045E;
    private static readonly HashSet<ushort> XboxProducts = new() { 0x028E, 0x028F, 0x02D1, 0x02DD, 0x02E0, 0x02EA, 0x02FD, 0x0B12, 0x0B13 };

    /// <summary>Xbox 360/One/Series pads (including Xbox One over Bluetooth) are read by games through XInput.</summary>
    public static bool IsXInputFamily(ushort vendorId, ushort productId) => vendorId == Microsoft && XboxProducts.Contains(productId);

    public static RawPadRoute Classify(ushort vendorId, ushort productId) =>
        IsXInputFamily(vendorId, productId) ? RawPadRoute.XInputDirect : RawPadRoute.NeedsForwarding;

    public static IReadOnlyList<PadMappingPreset> All { get; } = Build();

    public static PadMappingPreset? Find(ushort vendorId, ushort productId) =>
        All.FirstOrDefault(p => p.VendorId == vendorId && p.ProductId == productId);

    private static PadMapping Map(
        (int Index, ushort Bit)[] buttons, int lx, int ly, int rx, int ry, int? lt, int? rt,
        int? ltButton = null, int? rtButton = null, bool hat = true) => new()
    {
        Buttons = buttons.ToDictionary(b => b.Index, b => b.Bit),
        LeftXAxis = lx, LeftYAxis = ly, RightXAxis = rx, RightYAxis = ry,
        LeftTriggerAxis = lt, RightTriggerAxis = rt,
        LeftTriggerButton = ltButton, RightTriggerButton = rtButton,
        TriggerAxisIsSigned = true,                 // RawGameController axes are normalised to -1..1, rest = -1
        InvertLeftY = true, InvertRightY = true,    // HID Y grows downwards
        HatIsDpad = hat, HatTarget = PadHatTarget.Dpad,
        StickDeadzone = 0.1
    };

    private static List<PadMappingPreset> Build()
    {
        const ushort A = Xbox360Report.A, B = Xbox360Report.B, X = Xbox360Report.X, Y = Xbox360Report.Y;
        const ushort LB = Xbox360Report.LeftShoulder, RB = Xbox360Report.RightShoulder;
        const ushort Back = Xbox360Report.Back, Start = Xbox360Report.Start, L3 = Xbox360Report.LeftThumb, R3 = Xbox360Report.RightThumb;
        const ushort Guide = Xbox360Report.Guide;

        // PlayStation layout on Windows HID: Square 0, Cross 1, Circle 2, Triangle 3, L1 4, R1 5, L2 6, R2 7, Share/Create 8,
        // Options 9, L3 10, R3 11, PS 12. Axes: LX 0, LY 1, RX 2, L2 3, R2 4, RY 5.
        PadMapping Ps() => Map(new[]
        {
            (1, A), (2, B), (0, X), (3, Y), (4, LB), (5, RB), (8, Back), (9, Start), (10, L3), (11, R3), (12, Guide)
        }, lx: 0, ly: 1, rx: 2, ry: 5, lt: 3, rt: 4);

        var list = new List<PadMappingPreset>
        {
            // 15 buttons, 6 axes, 1 hat as observed on this PC for the BLE HID pad; layout follows the community SDL mapping.
            new("stadia", "Google Stadia controller (USB or Bluetooth LE)", 0x18D1, 0x9400, "15 buttons, 6 axes, 1 hat",
                Map(new[]
                {
                    (0, A), (1, B), (3, X), (4, Y), (6, LB), (7, RB), (10, Back), (11, Start), (12, Guide), (13, L3), (14, R3)
                }, lx: 0, ly: 1, rx: 2, ry: 3, lt: 5, rt: 4), false)
            {
                Note = "Unverified - derived from the community layout; the raw indexes of a Stadia pad could not be proven without pressing its buttons. Confirm in the mapping wizard."
            },
            // Observed raw layout: 15 buttons, 6 axes, 1 hat (generic D-input mode, not XInput mode).
            new("8bitdo-sn30pro", "8BitDo SN30 Pro (generic Bluetooth HID mode)", 0x2DC8, 0x2101, "15 buttons, 6 axes, 1 hat",
                Map(new[]
                {
                    (0, A), (1, B), (3, X), (4, Y), (6, LB), (7, RB), (10, Back), (11, Start), (12, Guide), (13, L3), (14, R3)
                }, lx: 0, ly: 1, rx: 2, ry: 3, lt: 5, rt: 4), false),
            new("ds4-v1", "Sony DualShock 4 (first generation)", 0x054C, 0x05C4, "14 buttons, 6 axes, 1 hat", Ps(), false),
            new("ds4-v2", "Sony DualShock 4 (second generation)", 0x054C, 0x09CC, "14 buttons, 6 axes, 1 hat", Ps(), false),
            new("dualsense", "Sony DualSense", 0x054C, 0x0CE6, "15 buttons, 6 axes, 1 hat", Ps(), false),
            // Windows exposes the Switch Pro as generic HID: B 0, A 1, Y 2, X 3, L 4, R 5, ZL 6, ZR 7, Minus 8, Plus 9, LS 10, RS 11, Home 12.
            // Positional (south = Xbox A) rather than by printed label, like SDL.
            new("switch-pro", "Nintendo Switch Pro controller", 0x057E, 0x2009, "14 buttons, 4 axes, 1 hat",
                Map(new[]
                {
                    (0, A), (1, B), (2, X), (3, Y), (4, LB), (5, RB), (8, Back), (9, Start), (10, L3), (11, R3), (12, Guide)
                }, lx: 0, ly: 1, rx: 2, ry: 3, lt: null, rt: null, ltButton: 6, rtButton: 7), false),
            // Windows normally serves this one through XInput; the raw layout is only a fallback for forwarding.
            new("xbox-one-bt", "Xbox One controller (Bluetooth)", 0x045E, 0x02FD, "16 buttons, 6 axes, 1 hat",
                Map(new[]
                {
                    (0, A), (1, B), (2, X), (3, Y), (4, LB), (5, RB), (6, Back), (7, Start), (8, L3), (9, R3)
                }, lx: 0, ly: 1, rx: 2, ry: 3, lt: 4, rt: 5), false)
            {
                Note = "Unverified - Windows reads this pad directly through XInput, so forwarding is normally unnecessary."
            },
        };
        return list;
    }
}
