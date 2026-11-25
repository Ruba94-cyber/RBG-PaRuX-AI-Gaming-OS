namespace PaRuX.Contracts;

/// <summary>Read-only current Windows display configuration, without EDID serials or HDR claims.</summary>
public sealed record GamingDisplayModeDto(
    string DisplayId,
    bool Primary,
    int X,
    int Y,
    int Width,
    int Height,
    int RefreshRateHz)
{
    /// <summary>Optional, Windows-reported friendly device description only.
    /// No EDID serial, hardware instance ID or registry key is transmitted.</summary>
    public string? FriendlyName { get; init; }

    /// <summary>EDID manufacturer + product code (for example "ABC1234"), shared by identical models. Not a serial number.</summary>
    public string? MonitorCode { get; init; }

    /// <summary>Stable monitor identifier: monitor code plus a one-way hash of the EDID serial. Survives port and arrangement changes;
    /// the raw serial is never read out of Core.</summary>
    public string? StableId { get; init; }

    /// <summary>
    /// Only true HDR (not WCG) counts as HDR enabled. Values are null when
    /// Windows/driver do not provide a reliable distinction on this display.
    /// Read-only: enumerating displays NEVER changes Windows color settings.
    /// </summary>
    public bool? HdrSupported { get; init; }
    public bool? HdrEnabled { get; init; }
    /// <summary>hdr | wcg | sdr | advanced-color-unknown | unknown</summary>
    public string ColorMode { get; init; } = "unknown";
}

public sealed record GamingDisplaySnapshotDto(
    bool Supported,
    string Message,
    IReadOnlyList<GamingDisplayModeDto> Displays);
