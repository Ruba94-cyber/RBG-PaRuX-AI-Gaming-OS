using System;
using System.Collections.Generic;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

namespace PaRuX.Services
{
    /// <summary>What the guided mapping asks the owner to touch.</summary>
    public enum WizardControl { Button, Axis, StickUp, Trigger, Dpad }

    public enum DetectedKind { None, Button, Axis, Hat }

    /// <summary>One control found by the guided mapping. <see cref="Direction"/> is the sign of the axis movement (+1 / -1);
    /// for a hat it is the hat direction (0 = up ... 7 = up-left); <see cref="Rest"/> is the axis value before the touch.</summary>
    public readonly record struct DetectedControl(DetectedKind Kind, int Index, int Direction = 0, double Rest = 0)
    {
        public bool IsNone => Kind == DetectedKind.None;
    }

    public enum DpadDirection { Up, Down, Left, Right }

    /// <summary>
    /// Pure logic behind the guided mapping wizard, so that any layout (hat or digital-axis D-pad, triggers as axes or buttons,
    /// any VID/PID) can be detected and written into a <see cref="PadMapping"/> without a window or a device. The wizard only
    /// feeds it consecutive raw states; nothing here talks to hardware, games or the virtual pad.
    /// </summary>
    public static class RawMappingWizardModel
    {
        public const double AxisDelta = 0.5;

        public static DetectedControl Detect(WizardControl want, RawGamepadState baseline, RawGamepadState current)
        {
            switch (want)
            {
                case WizardControl.Button:
                    return NewButton(baseline, current);
                case WizardControl.Axis:
                case WizardControl.StickUp:
                    return MovedAxis(baseline, current);
                case WizardControl.Trigger:
                    {
                        // An analogue trigger often also raises a button at the end of its travel: the axis wins, so the pressure is kept.
                        DetectedControl axis = MovedAxis(baseline, current);
                        return axis.IsNone ? NewButton(baseline, current) : axis;
                    }
                case WizardControl.Dpad:
                    {
                        if (current.HatDirection >= 0 && baseline.HatDirection < 0)
                            return new(DetectedKind.Hat, current.HatDirection, current.HatDirection);
                        DetectedControl axis = MovedAxis(baseline, current);
                        return axis.IsNone ? NewButton(baseline, current) : axis;
                    }
            }
            return default;
        }

        private static DetectedControl NewButton(RawGamepadState baseline, RawGamepadState current)
        {
            for (int i = 0; i < current.Buttons.Count; i++)
                if (current.Buttons[i] && !(i < baseline.Buttons.Count && baseline.Buttons[i]))
                    return new(DetectedKind.Button, i);
            return default;
        }

        private static DetectedControl MovedAxis(RawGamepadState baseline, RawGamepadState current)
        {
            int best = -1;
            double bestDelta = AxisDelta;
            for (int i = 0; i < current.Axes.Count; i++)
            {
                if (!double.IsFinite(current.Axes[i])) continue;
                double before = i < baseline.Axes.Count && double.IsFinite(baseline.Axes[i]) ? baseline.Axes[i] : 0;
                double delta = Math.Abs(current.Axes[i] - before);
                if (delta >= bestDelta) { best = i; bestDelta = delta; }
            }
            if (best < 0) return default;
            double rest = best < baseline.Axes.Count && double.IsFinite(baseline.Axes[best]) ? baseline.Axes[best] : 0;
            return new(DetectedKind.Axis, best, current.Axes[best] >= rest ? 1 : -1, rest);
        }

        /// <summary>The stable-reading rule: the same control (kind, index and direction) on consecutive reads.</summary>
        public static bool SameControl(DetectedControl a, DetectedControl b) =>
            a.Kind == b.Kind && a.Index == b.Index && a.Direction == b.Direction;

        /// <summary>Stick Y: pushing the stick UP on a normal HID pad lowers the value, which is what "inverted" means in the mapping.</summary>
        public static bool StickUpNeedsInvert(DetectedControl up) => up.Direction < 0;

        /// <summary>Writes a detected trigger. An axis clears the button alternative and reads its rest value to pick the range
        /// (about -1 = signed, about 0 = unsigned); a button clears the axis.</summary>
        public static void ApplyTrigger(PadMapping mapping, bool left, DetectedControl found)
        {
            if (found.Kind == DetectedKind.Axis)
            {
                if (left) { mapping.LeftTriggerAxis = found.Index; mapping.LeftTriggerButton = null; }
                else { mapping.RightTriggerAxis = found.Index; mapping.RightTriggerButton = null; }
                mapping.TriggerAxisIsSigned = found.Rest < -0.5;
            }
            else if (found.Kind == DetectedKind.Button)
            {
                if (left) { mapping.LeftTriggerButton = found.Index; mapping.LeftTriggerAxis = null; }
                else { mapping.RightTriggerButton = found.Index; mapping.RightTriggerAxis = null; }
            }
        }

        public static ushort DpadBit(DpadDirection d) => d switch
        {
            DpadDirection.Up => Xbox360Report.DpadUp,
            DpadDirection.Down => Xbox360Report.DpadDown,
            DpadDirection.Left => Xbox360Report.DpadLeft,
            _ => Xbox360Report.DpadRight
        };

        /// <summary>Writes one detected D-pad direction. Returns true when the whole D-pad is now known (a hat covers all four
        /// directions at once), so the wizard can skip the remaining directions.</summary>
        public static bool ApplyDpad(PadMapping mapping, DpadDirection direction, DetectedControl found)
        {
            switch (found.Kind)
            {
                case DetectedKind.Hat:
                    mapping.HatIsDpad = true;
                    mapping.HatTarget = PadHatTarget.Dpad;
                    mapping.DpadXAxis = null;
                    mapping.DpadYAxis = null;
                    RemoveDpadButtons(mapping);
                    return true;
                case DetectedKind.Axis:
                    {
                        mapping.HatIsDpad = false;
                        bool vertical = direction is DpadDirection.Up or DpadDirection.Down;
                        // Positive means Right / Down by definition, so a positive reading for Up or Left is an inversion.
                        bool positiveIsOpposite = direction is DpadDirection.Up or DpadDirection.Left;
                        bool invert = positiveIsOpposite ? found.Direction > 0 : found.Direction < 0;
                        if (vertical) { mapping.DpadYAxis = found.Index; mapping.InvertDpadY = invert; }
                        else { mapping.DpadXAxis = found.Index; mapping.InvertDpadX = invert; }
                        return false;
                    }
                case DetectedKind.Button:
                    mapping.HatIsDpad = false;
                    mapping.Buttons[found.Index] = DpadBit(direction);
                    return false;
            }
            return false;
        }

        private static void RemoveDpadButtons(PadMapping mapping)
        {
            const ushort all = Xbox360Report.DpadUp | Xbox360Report.DpadDown | Xbox360Report.DpadLeft | Xbox360Report.DpadRight;
            var remove = new List<int>();
            foreach (var (index, bit) in mapping.Buttons)
                if ((bit & all) != 0) remove.Add(index);
            foreach (int i in remove) mapping.Buttons.Remove(i);
        }

        /// <summary>The starting point for a pad without a preset: the generic HID layout built from the counts the device reports.</summary>
        public static PadMappingPreset? GenericStart(string name, ushort vendorId, ushort productId, int buttons, int axes, int hats)
        {
            if (!GenericRawPadMapping.TryBuild(buttons, axes, hats, out PadMapping? mapping, out string description) || mapping is null)
                return null;
            return new PadMappingPreset("generic", name, vendorId, productId, description, mapping, false)
            {
                Note = "Unverified generic layout - confirm every control in the guided mapping."
            };
        }
    }
}
