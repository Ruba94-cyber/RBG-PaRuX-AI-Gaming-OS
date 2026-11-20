using System;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

/// <summary>
/// Applies a game's controller feel (dead zones, trigger threshold, stick inversion, button remap) to the Xbox report that is
/// about to be sent to the virtual pad, so the setting reaches the game and not only PaRuX's own screens. Pure and hardware-free.
/// An invalid or null profile leaves the report untouched.
/// </summary>
public static class PadFeel
{
    public static Xbox360Report Apply(Xbox360Report report, GamingControllerProfile? profile)
    {
        if (profile is null || profile.Validate() is not null) return report;
        var (lx, ly) = Stick(report.LeftX, report.LeftY, profile.LeftDeadzone, profile.InvertLeftY);
        var (rx, ry) = Stick(report.RightX, report.RightY, profile.RightDeadzone, profile.InvertRightY);
        return new Xbox360Report(
            Remap(report.Buttons, profile.ButtonRemap),
            Trigger(report.LeftTrigger, profile.TriggerThreshold),
            Trigger(report.RightTrigger, profile.TriggerThreshold),
            lx, ly, rx, ry);
    }

    /// <summary>Stable text for comparing two profiles (a game switch only restarts forwarding when the feel really changed).</summary>
    public static string Fingerprint(GamingControllerProfile? profile)
    {
        if (profile is null) return string.Empty;
        string remap = string.Join(",", System.Linq.Enumerable.Select(
            System.Linq.Enumerable.OrderBy(profile.ButtonRemap, kv => kv.Key), kv => kv.Key + ">" + kv.Value));
        return string.Join("|", profile.LeftDeadzone, profile.RightDeadzone, profile.TriggerThreshold,
            profile.InvertLeftY, profile.InvertRightY, remap);
    }

    private static (short X, short Y) Stick(short x, short y, int deadzone, bool invertY)
    {
        double length = Math.Sqrt((double)x * x + (double)y * y);
        short ox = 0, oy = 0;
        if (length > deadzone && length > 0)
        {
            // No circular clamp: a square-gate pad keeps its full-axis diagonals; each axis is clamped below.
            double magnitude = (length - deadzone) / (32767.0 - deadzone);
            ox = (short)Math.Clamp(Math.Round(x / length * magnitude * 32767.0), -32767, 32767);
            oy = (short)Math.Clamp(Math.Round(y / length * magnitude * 32767.0), -32767, 32767);
        }
        if (invertY) oy = (short)-Math.Max(oy, (short)-32767);
        return (ox, oy);
    }

    private static ushort Remap(ushort buttons, System.Collections.Generic.IReadOnlyDictionary<ushort, ushort> remap)
    {
        if (remap.Count == 0) return buttons;
        int result = buttons;
        foreach (ushort source in remap.Keys) result &= ~source;
        foreach (var (source, destination) in remap)
            if ((buttons & source) != 0) result |= destination;
        return (ushort)result;
    }

    private static byte Trigger(byte raw, int threshold) =>
        raw <= threshold ? (byte)0 : (byte)Math.Clamp(Math.Round((raw - threshold) / (255.0 - threshold) * 255.0), 0, 255);
}
