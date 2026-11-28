namespace PaRuX.Server;

/// <summary>One best-effort operating-system RAM sample. Invalid or unavailable samples fail closed.</summary>
internal sealed record SystemRamSnapshot(
    ulong TotalBytes,
    ulong AvailableBytes,
    ulong? CommitAvailableBytes = null,
    ulong? KernelBytes = null)
{
    /// <summary>
    /// <paramref name="commitAvailableBytes"/> is the system-wide commit headroom (CommitLimit - CommitTotal on Windows):
    /// the hard limit for private allocations (KV cache, compute buffers). <paramref name="kernelBytes"/> is paged plus
    /// non-paged kernel memory, which can never be trimmed. Both are null where the platform does not report them
    /// (Linux, or a failed probe), and admission then keeps the classic available-memory behaviour.
    /// </summary>
    public static SystemRamSnapshot? TryCreate(ulong totalBytes, ulong availableBytes,
        ulong? commitAvailableBytes = null, ulong? kernelBytes = null) =>
        totalBytes > 0 && totalBytes <= long.MaxValue && availableBytes <= totalBytes
            ? new SystemRamSnapshot(totalBytes, availableBytes, commitAvailableBytes, kernelBytes)
            : null;

    public bool HasCommitInfo => IsValid && CommitAvailableBytes.HasValue;

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

    // ---- Corporate admission v2: commit headroom + physical working floor (+ the strict profile ceiling) ----

    /// <summary>Share of "used by other applications" memory that Windows can realistically trim (idle working sets, compressed store). Deliberately conservative.</summary>
    internal const double TrimmableFraction = 0.40;
    /// <summary>Physical memory kept for Windows itself when the fit relies on paging other applications out.</summary>
    internal const long PhysicalFloorReserveBytes = 512L * 1024 * 1024;
    internal const double CommitMarginFraction = 0.04;
    internal const long CommitMinMarginBytes = 512L * 1024 * 1024;
    internal const long CommitMaxMarginBytes = 1024L * 1024 * 1024;

    internal enum CorporateAdmissionOutcome
    {
        /// <summary>Fits in what Windows reports free, with the usual reserve.</summary>
        Admit,
        /// <summary>Fits only if Windows pages idle applications out: run, but tell the person the PC is busy.</summary>
        AdmitBusy,
        /// <summary>Commit headroom (CommitLimit - CommitTotal) cannot cover the private allocations. Real out-of-memory risk.</summary>
        RefuseCommit,
        /// <summary>Even after trimming idle applications the working set does not fit in physical memory.</summary>
        RefusePhysical,
        /// <summary>An owner/IT profile ceiling stricter than <see cref="CorporateDynamicFromPercent"/> does not allow it.</summary>
        RefuseCeiling,
    }

    internal sealed record CorporateAdmission(
        CorporateAdmissionOutcome Outcome, double NeededMib, double AvailableMib, double CommitHeadroomMib)
    {
        public bool Allowed => Outcome is CorporateAdmissionOutcome.Admit or CorporateAdmissionOutcome.AdmitBusy;
        public bool Busy => Outcome == CorporateAdmissionOutcome.AdmitBusy;
    }

    internal static long CommitMarginBytes(SystemRamSnapshot snapshot) =>
        (long)Math.Clamp((decimal)snapshot.TotalBytes * (decimal)CommitMarginFraction, CommitMinMarginBytes, CommitMaxMarginBytes);

    /// <summary>
    /// Conservative estimate of what Windows can free by trimming other applications: a fixed share of the memory that is
    /// in use and is neither PaRuX's own resident model nor kernel memory.
    /// </summary>
    internal static long EstimateTrimmableBytes(SystemRamSnapshot snapshot, long reclaimableBytes)
    {
        decimal total = snapshot.TotalBytes;
        decimal kernel = Math.Min(total, snapshot.KernelBytes ?? 0UL);
        decimal reclaimable = Math.Min(Math.Max(0, reclaimableBytes), total);
        decimal others = Math.Max(0, total - snapshot.AvailableBytes - kernel - reclaimable);
        return (long)(others * (decimal)TrimmableFraction);
    }

    /// <summary>
    /// The Corporate CPU admission. A launch needs <paramref name="privateMib"/> of private memory (KV cache, compute
    /// buffers, runtime reserve) that is charged against the commit limit, and <paramref name="mappedMib"/> of
    /// memory-mapped weights that are file-backed (no commit charge, evictable, but read every token so they are part of the
    /// hot working set).
    /// 1. Strict profile (ceiling below <see cref="CorporateDynamicFromPercent"/>): the classic ceiling formula decides, as before.
    /// 2. Commit: private + safety margin must fit in commit headroom (plus the resident PaRuX model being replaced),
    ///    otherwise <see cref="CorporateAdmissionOutcome.RefuseCommit"/>.
    /// 3. Physical: private + mapped within free memory minus the reserve is a plain Admit; within free + estimated
    ///    trimmable memory minus a small floor is AdmitBusy; anything else is RefusePhysical.
    /// A snapshot without commit information (Linux, failed probe) keeps the classic available-memory verdict.
    /// </summary>
    public static CorporateAdmission EvaluateCorporate(
        SystemRamSnapshot? snapshot, double maxSystemRamPercent, long reclaimableBytes,
        double privateMib, double mappedMib)
    {
        double neededMib = Math.Max(0, privateMib) + Math.Max(0, mappedMib);
        if (snapshot?.IsValid != true || !double.IsFinite(maxSystemRamPercent) || !double.IsFinite(neededMib))
            return new CorporateAdmission(CorporateAdmissionOutcome.RefusePhysical, neededMib, 0, 0);

        double comfortableMib = CalculateCorporateAvailableBytes(snapshot, maxSystemRamPercent, reclaimableBytes) / 1048576d;
        if (maxSystemRamPercent < CorporateDynamicFromPercent)
            return new CorporateAdmission(
                neededMib <= comfortableMib ? CorporateAdmissionOutcome.Admit : CorporateAdmissionOutcome.RefuseCeiling,
                neededMib, comfortableMib, 0);

        if (!snapshot.HasCommitInfo)
            return new CorporateAdmission(
                neededMib <= comfortableMib ? CorporateAdmissionOutcome.Admit : CorporateAdmissionOutcome.RefusePhysical,
                neededMib, comfortableMib, 0);

        decimal total = snapshot.TotalBytes;
        decimal reclaimable = Math.Min(Math.Max(0, reclaimableBytes), total);
        double commitHeadroomMib = (double)Math.Max(0m,
            snapshot.CommitAvailableBytes!.Value + reclaimable - CommitMarginBytes(snapshot)) / 1048576d;
        if (Math.Max(0, privateMib) > commitHeadroomMib)
            return new CorporateAdmission(CorporateAdmissionOutcome.RefuseCommit, neededMib,
                commitHeadroomMib + Math.Max(0, mappedMib), commitHeadroomMib);

        if (neededMib <= comfortableMib)
            return new CorporateAdmission(CorporateAdmissionOutcome.Admit, neededMib, comfortableMib, commitHeadroomMib);

        decimal pagedOutFloor = Math.Min(total, snapshot.AvailableBytes + reclaimable
            + EstimateTrimmableBytes(snapshot, reclaimableBytes)) - PhysicalFloorReserveBytes;
        double busyMib = (double)Math.Max(0m, pagedOutFloor) / 1048576d;
        return neededMib <= busyMib
            ? new CorporateAdmission(CorporateAdmissionOutcome.AdmitBusy, neededMib, neededMib, commitHeadroomMib)
            : new CorporateAdmission(CorporateAdmissionOutcome.RefusePhysical, neededMib, busyMib, commitHeadroomMib);
    }

    /// <summary>
    /// Live RAM budget for views that only need one number (status line, context options, AUTO fit): the larger of the
    /// comfortable headroom and what a "busy" admission would allow, limited by commit headroom (all of it treated as
    /// private, which is conservative). Strict profiles and snapshots without commit information return the classic number.
    /// </summary>
    public static long CalculateCorporateAdmissibleBytes(
        SystemRamSnapshot? snapshot, double maxSystemRamPercent, long reclaimableBytes = 0)
    {
        long comfortable = CalculateCorporateAvailableBytes(snapshot, maxSystemRamPercent, reclaimableBytes);
        if (snapshot?.HasCommitInfo != true || !double.IsFinite(maxSystemRamPercent)
            || maxSystemRamPercent < CorporateDynamicFromPercent) return comfortable;
        decimal total = snapshot.TotalBytes;
        decimal reclaimable = Math.Min(Math.Max(0, reclaimableBytes), total);
        decimal busy = Math.Max(0m, Math.Min(total, snapshot.AvailableBytes + reclaimable
            + EstimateTrimmableBytes(snapshot, reclaimableBytes)) - PhysicalFloorReserveBytes);
        // Commit only limits the private part; as a single number, bound the total by commit + weights is unknowable here,
        // so use the physical figure and let EvaluateCorporate apply the commit gate to the private share.
        return (long)Math.Max(comfortable, busy);
    }

    /// <summary>The reserve a Corporate admission keeps free, for messages and tests.</summary>
    internal static long CorporateReserveBytes(SystemRamSnapshot? snapshot) =>
        snapshot?.IsValid != true ? CorporateMinReserveBytes
            : (long)Math.Clamp((decimal)snapshot.TotalBytes * (decimal)CorporateReserveFraction,
                CorporateMinReserveBytes, CorporateMaxReserveBytes);
}
