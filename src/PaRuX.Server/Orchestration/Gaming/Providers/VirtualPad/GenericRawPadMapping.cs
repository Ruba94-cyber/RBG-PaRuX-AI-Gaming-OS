using System;

namespace PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

/// <summary>
/// Conservative last-resort mapping for a raw HID pad that has neither a saved mapping nor a preset, used only for Console/TV
/// navigation. It assumes the common HID/DirectInput gamepad layout (Windows RawGameController indexes): hat switch 0 = D-pad,
/// axes 0/1 = left stick, buttons 0 = A, 1 = B, 4/5 = LB/RB, 6 = Back, 7 = Start (face buttons X/Y at 2/3, stick clicks at 8/9).
/// The right stick and analog triggers are deliberately NOT mapped: their axis numbers differ between layouts (axis 3 is the
/// right stick on one pad and L2 on another), and a wrongly mapped resting trigger would scroll or switch workspace forever.
/// The result is always UNVERIFIED for the specific model; it never replaces a saved mapping or a preset.
/// </summary>
public static class GenericRawPadMapping
{
    private const int MinButtons = 4;
    private const int MinAxesWithoutHat = 2;

    /// <summary>
    /// Builds the fallback from the counts a device reports. Refuses (returns false, <paramref name="description"/> = reason) a
    /// device that does not look like a gamepad: it needs at least 4 buttons AND (one hat switch OR two axes), so mice,
    /// keyboards, media keys and button-less flight sticks are never polled.
    /// </summary>
    public static bool TryBuild(int buttonCount, int axisCount, int switchCount, out PadMapping? mapping, out string description)
    {
        mapping = null;
        if (buttonCount < MinButtons)
        {
            description = $"not a gamepad ({buttonCount} buttons; at least {MinButtons} needed)";
            return false;
        }
        if (switchCount < 1 && axisCount < MinAxesWithoutHat)
        {
            description = $"not a gamepad (no hat switch and {axisCount} axes; a hat or {MinAxesWithoutHat} axes needed)";
            return false;
        }

        var map = new PadMapping
        {
            TriggerAxisIsSigned = true,                 // RawGameController axes are normalised to -1..1; no trigger axis is mapped anyway
            InvertLeftY = true,                         // HID Y grows downwards
            StickDeadzone = 0.15,                       // slightly wider than presets: unknown hardware drifts more
            HatIsDpad = switchCount >= 1,
            HatTarget = PadHatTarget.Dpad
        };
        if (axisCount >= 2) { map.LeftXAxis = 0; map.LeftYAxis = 1; }

        void Add(int index, ushort bit) { if (index < buttonCount) map.Buttons[index] = bit; }
        Add(0, Xbox360Report.A);
        Add(1, Xbox360Report.B);
        Add(2, Xbox360Report.X);
        Add(3, Xbox360Report.Y);
        Add(4, Xbox360Report.LeftShoulder);
        Add(5, Xbox360Report.RightShoulder);
        Add(6, Xbox360Report.Back);
        Add(7, Xbox360Report.Start);
        Add(8, Xbox360Report.LeftThumb);
        Add(9, Xbox360Report.RightThumb);

        if (map.Validate() is not null)
        {
            description = "generic mapping failed validation";
            return false;
        }
        mapping = map;
        string nav = (switchCount >= 1 ? "hat D-pad" : "no hat") + (axisCount >= 2 ? ", left stick" : "");
        description = $"generic HID layout ({nav}, {map.Buttons.Count} buttons mapped)";
        return true;
    }
}
