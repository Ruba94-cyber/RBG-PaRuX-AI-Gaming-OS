namespace PaRuX.Server;

/// <summary>One best-effort operating-system RAM sample. Invalid or unavailable samples fail closed.</summary>
internal sealed record SystemRamSnapshot(ulong TotalBytes, ulong AvailableBytes)
{
    public static SystemRamSnapshot? TryCreate(ulong totalBytes, ulong availableBytes) =>
        totalBytes > 0 && totalBytes <= long.MaxValue && availableBytes <= totalBytes
            ? new SystemRamSnapshot(totalBytes, availableBytes)
            : null;

    public bool IsValid => TotalBytes > 0 && TotalBytes <= long.MaxValue && AvailableBytes <= TotalBytes;

    public long TotalBytesAsLong => IsValid ? (long)TotalBytes : 0;

    public double? UsedPercent => IsValid
        ? Math.Clamp((TotalBytes - AvailableBytes) * 100d / TotalBytes, 0d, 100d)
        : null;
}

internal interface ISystemRamSnapshotProvider
{
    SystemRamSnapshot? TryGetSnapshot();
}

internal sealed class SystemRamSnapshotProvider : ISystemRamSnapshotProvider
{
    public SystemRamSnapshot? TryGetSnapshot() => ExecutionResourceGuard.TryGetSystemRamSnapshot();
}

/// <summary>Pure, fail-closed RAM admission math shared by model fit and context planning.</summary>
internal static class RuntimeMemoryBudget
{
    /// <summary>
    /// The RAM a model load may take, in bytes, under two limits that must both hold. The profile
    /// ceiling caps the whole system, so memory other applications already hold is taken off that
    /// ceiling first. The load itself can only use what is free plus what the swap reclaims.
    /// <paramref name="reclaimableBytes"/> is the resident model's working set: a swap stops it first,
    /// so it is available to the load, but it is not "used by others". Taking the smaller of the
    /// ceiling and the free memory alone let a busy host admit a model that pushed RAM past the ceiling.
    /// </summary>
    public static long CalculateAvailableSystemRamBytes(
        SystemRamSnapshot? snapshot,
        double maxSystemRamPercent,
        long reclaimableBytes = 0)
    {
        if (snapshot?.IsValid != true || !double.IsFinite(maxSystemRamPercent)) return 0;

        decimal total = Math.Min((decimal)snapshot.TotalBytes, long.MaxValue);
        decimal policyPercent = (decimal)Math.Clamp(maxSystemRamPercent, 0d, 100d);
        decimal policyCap = decimal.Floor(total * policyPercent / 100m);
        decimal reclaimable = Math.Min(Math.Max(0, reclaimableBytes), total);
        decimal usedByOthers = Math.Max(0, total - snapshot.AvailableBytes - reclaimable);
        decimal ceilingHeadroom = Math.Max(0, policyCap - usedByOthers);
        decimal postTransitionAvailable = Math.Min(total, snapshot.AvailableBytes + reclaimable);
        return (long)Math.Min(ceilingHeadroom, postTransitionAvailable);
    }

    /// <summary>Share of installed RAM that Corporate keeps free for Windows and the user's other programs.</summary>
    internal const double CorporateReserveFraction = 0.08;
    internal const long CorporateMinReserveBytes = 1L * 1024 * 1024 * 1024;
    internal const long CorporateMaxReserveBytes = 3L * 512 * 1024 * 1024;
    /// <summary>A profile ceiling below this is an explicit, stricter owner/IT choice and is honored as before.</summary>
    internal const double CorporateDynamicFromPercent = 80;

    /// <summary>
    /// What the bundled CPU model may take on a corporate PC. Teams, Outlook, a browser and security agents routinely hold
    /// 55-70% of a 16 GB machine, and the generic profile ceiling (86% of total RAM) leaves only 3.4 GB there, which a 4B
    /// model needs even at 2K context, so chat was refused with 5.6 GB physically available. Corporate therefore admits by
    /// what Windows reports as available (free plus standby cache), minus a reserve that scales with the machine
    /// (8% of RAM, between 1.0 and 1.5 GiB) for the other applications' spikes. A profile ceiling stricter than 80% is
    /// still honored, and the result is never below the classic formula (a looser ceiling still wins).
    /// </summary>
    public static long CalculateCorporateAvailableBytes(
        SystemRamSnapshot? snapshot,
        double maxSystemRamPercent,
        long reclaimableBytes = 0)
    {
        long classic = CalculateAvailableSystemRamBytes(snapshot, maxSystemRamPercent, reclaimableBytes);
        if (snapshot?.IsValid != true || !double.IsFinite(maxSystemRamPercent)) return classic;
        if (maxSystemRamPercent < CorporateDynamicFromPercent) return classic;

        decimal total = Math.Min((decimal)snapshot.TotalBytes, long.MaxValue);
        decimal reserve = Math.Clamp(total * (decimal)CorporateReserveFraction,
            CorporateMinReserveBytes, CorporateMaxReserveBytes);
        decimal reclaimable = Math.Min(Math.Max(0, reclaimableBytes), total);
        decimal available = Math.Min(total, snapshot.AvailableBytes + reclaimable);
        decimal dynamicHeadroom = Math.Max(0, available - reserve);
        return (long)Math.Max(classic, dynamicHeadroom);
    }

    /// <summary>The reserve a Corporate admission keeps free, for messages and tests.</summary>
    internal static long CorporateReserveBytes(SystemRamSnapshot? snapshot) =>
        snapshot?.IsValid != true ? CorporateMinReserveBytes
            : (long)Math.Clamp((decimal)snapshot.TotalBytes * (decimal)CorporateReserveFraction,
                CorporateMinReserveBytes, CorporateMaxReserveBytes);
}
