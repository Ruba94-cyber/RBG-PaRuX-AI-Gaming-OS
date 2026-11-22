namespace PaRuX.Contracts;

/// <summary>Which button of an open dialog a gamepad should land on.</summary>
public enum ConsoleDialogButton { None, Primary, Secondary, Close }

/// <summary>Pure rules for operating dialogs and sliders from a gamepad in Console/TV mode (no WinUI, deterministic tests).</summary>
public static class ConsolePadOverlayPolicy
{
    /// <summary>
    /// Chooses the button to focus when a dialog is open and the pad focus is not inside it. The dialog's own default wins when that
    /// button exists; otherwise the SAFE button is preferred (Close/Cancel, then Primary, then Secondary), so a destructive
    /// confirmation that did not state a default is never one accidental A press away from confirming.
    /// </summary>
    public static ConsoleDialogButton PickDialogButton(ConsoleDialogButton declaredDefault, bool hasPrimary, bool hasSecondary, bool hasClose)
    {
        bool Has(ConsoleDialogButton b) => b switch
        {
            ConsoleDialogButton.Primary => hasPrimary,
            ConsoleDialogButton.Secondary => hasSecondary,
            ConsoleDialogButton.Close => hasClose,
            _ => false
        };
        if (Has(declaredDefault)) return declaredDefault;
        if (hasClose) return ConsoleDialogButton.Close;
        if (hasPrimary) return ConsoleDialogButton.Primary;
        if (hasSecondary) return ConsoleDialogButton.Secondary;
        return ConsoleDialogButton.None;
    }

    /// <summary>
    /// Next value of a slider for one D-pad step (direction +1 right, -1 left), clamped to the range and snapped to the step grid.
    /// A zero or negative step falls back to one hundredth of the range, so a pad can always cross the whole slider in about 100 presses.
    /// </summary>
    public static double NextSliderValue(double value, double minimum, double maximum, double step, int direction)
    {
        if (maximum < minimum) (minimum, maximum) = (maximum, minimum);
        double range = maximum - minimum;
        if (range <= 0) return minimum;
        if (step <= 0 || double.IsNaN(step)) step = range / 100.0;
        double next = value + (direction >= 0 ? step : -step);
        double snapped = minimum + Math.Round((next - minimum) / step) * step;
        return Math.Clamp(snapped, minimum, maximum);
    }
}
