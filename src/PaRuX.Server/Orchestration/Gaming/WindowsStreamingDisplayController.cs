using System.Globalization;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Streaming display adapter over the existing <see cref="IDisplayModeApi"/> (EnumDisplaySettings / ChangeDisplaySettingsEx).
/// Changes are session-temporary (never written to the monitor's saved defaults), driver-tested first, and limited to
/// monitors in the current topology. The journaled previous value is the complete previous mode, so a restore puts back
/// exactly what was there. HDR is NOT implemented: no documented safe API exists in this repository, so Hdr reads as
/// unavailable and the streaming service refuses to change it.
/// </summary>
internal sealed class WindowsStreamingDisplayController : IStreamingDisplayController
{
    private readonly IDisplayModeApi _api;
    private readonly GamingDisplayTopologyService _topology;

    public WindowsStreamingDisplayController(IDisplayModeApi api, GamingDisplayTopologyService topology)
    {
        _api = api; _topology = topology;
    }

    public string? Read(string displayId, DisplayChangeKind kind)
    {
        if (kind is not (DisplayChangeKind.Resolution or DisplayChangeKind.RefreshRate)) return null;
        try
        {
            if (!InTopology(displayId)) return null;
            DisplayModeEntry? current = _api.Current(displayId);
            return current is null ? null : Encode(current);
        }
        catch (Exception ex) when (IsApiFailure(ex)) { return null; }
    }

    public bool Apply(string displayId, DisplayChangeKind kind, string value)
    {
        if (kind is not (DisplayChangeKind.Resolution or DisplayChangeKind.RefreshRate)) return false;
        try
        {
            if (!InTopology(displayId)) return false;
            DisplayModeEntry? current = _api.Current(displayId);
            if (current is null) return false;
            DisplayModeEntry? target = ResolveTarget(current, _api.Supported(displayId), kind, value);
            if (target is null) return false;
            if (SameMode(target, current)) return true;
            if (!_api.Test(displayId, target)) return false;
            return _api.ApplyTemporary(displayId, target);
        }
        catch (Exception ex) when (IsApiFailure(ex)) { return false; }
    }

    public RestoreOutcome Restore(DisplayJournalEntry entry)
    {
        if (entry.Kind is not (DisplayChangeKind.Resolution or DisplayChangeKind.RefreshRate)) return RestoreOutcome.Failed;
        DisplayModeEntry? previous = Decode(entry.PreviousValue);
        if (previous is null) return RestoreOutcome.Failed;
        try
        {
            GamingDisplaySnapshotDto snapshot = _topology.Snapshot();
            if (!snapshot.Supported || snapshot.Displays.Count == 0) return RestoreOutcome.Failed; // unreadable: keep the restore point
            if (!InTopology(entry.DisplayId)) return RestoreOutcome.NotApplicable;                 // monitor positively gone
            DisplayModeEntry? current = _api.Current(entry.DisplayId);
            if (current is not null && SameMode(current, previous)) return RestoreOutcome.Restored;
            bool listed = _api.Supported(entry.DisplayId).Any(m => m.Width == previous.Width && m.Height == previous.Height
                && m.RefreshRateHz == previous.RefreshRateHz && m.BitsPerPixel == previous.BitsPerPixel);
            return listed && _api.ApplyTemporary(entry.DisplayId, previous) ? RestoreOutcome.Restored : RestoreOutcome.Failed;
        }
        catch (Exception ex) when (IsApiFailure(ex)) { return RestoreOutcome.Failed; }
    }

    /// <summary>Resolution value "WxH" keeps the current refresh rate when listed (else the highest listed); RefreshRate value "N" keeps the resolution.</summary>
    internal static DisplayModeEntry? ResolveTarget(DisplayModeEntry current, IReadOnlyList<DisplayModeEntry> supported, DisplayChangeKind kind, string value)
    {
        int width = current.Width, height = current.Height, hz = current.RefreshRateHz;
        if (kind == DisplayChangeKind.Resolution)
        {
            string[] parts = (value ?? string.Empty).Split('x', 'X');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)) return null;
        }
        else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out hz)) return null;
        if (width <= 0 || height <= 0 || hz < 24) return null;

        IEnumerable<DisplayModeEntry> sameSize = supported.Where(m => m.Width == width && m.Height == height);
        DisplayModeEntry? chosen = kind == DisplayChangeKind.RefreshRate
            ? sameSize.Where(m => m.RefreshRateHz == hz)
                .OrderByDescending(m => m.BitsPerPixel == current.BitsPerPixel).ThenByDescending(m => m.BitsPerPixel).FirstOrDefault()
            : sameSize.Where(m => m.RefreshRateHz >= 24)
                .OrderByDescending(m => m.RefreshRateHz == current.RefreshRateHz).ThenByDescending(m => m.RefreshRateHz)
                .ThenByDescending(m => m.BitsPerPixel == current.BitsPerPixel).ThenByDescending(m => m.BitsPerPixel).FirstOrDefault();
        return chosen is null ? null : chosen with { X = current.X, Y = current.Y };
    }

    internal static string Encode(DisplayModeEntry m) => string.Join(',',
        m.Width.ToString(CultureInfo.InvariantCulture), m.Height.ToString(CultureInfo.InvariantCulture),
        m.RefreshRateHz.ToString(CultureInfo.InvariantCulture), m.BitsPerPixel.ToString(CultureInfo.InvariantCulture),
        m.X.ToString(CultureInfo.InvariantCulture), m.Y.ToString(CultureInfo.InvariantCulture));

    internal static DisplayModeEntry? Decode(string? token)
    {
        string[] p = (token ?? string.Empty).Split(',');
        if (p.Length != 6) return null;
        var v = new int[6];
        for (int i = 0; i < 6; i++)
            if (!int.TryParse(p[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v[i])) return null;
        return v[0] > 0 && v[1] > 0 && v[2] >= 24 && v[3] > 0 ? new DisplayModeEntry(v[0], v[1], v[2], v[3], v[4], v[5]) : null;
    }

    private static bool SameMode(DisplayModeEntry a, DisplayModeEntry b) =>
        a.Width == b.Width && a.Height == b.Height && a.RefreshRateHz == b.RefreshRateHz && a.BitsPerPixel == b.BitsPerPixel;

    private bool InTopology(string displayId)
    {
        GamingDisplaySnapshotDto s = _topology.Snapshot();
        return s.Supported && s.Displays.Any(d => string.Equals(d.DisplayId, displayId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsApiFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException;
}
