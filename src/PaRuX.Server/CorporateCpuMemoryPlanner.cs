namespace PaRuX.Server;

/// <summary>
/// Chooses the largest safe context for the SAME Corporate CPU GGUF, never a
/// different model. A lower context reduces KV memory without tampering with
/// other Windows processes or raising a user's per-model system RAM ceiling.
/// This is a just-in-time launch decision; a concurrent OS memory change can
/// still make native allocation fail, which is reported separately.
/// </summary>
internal static class CorporateCpuMemoryPlanner
{
    internal const int NormalContextTokens = 4096;
    internal const int MinimumUsefulContextTokens = 2048;
    internal static readonly int[] CandidateContexts = [4096, 3072, 2048];

    internal sealed record Plan(int ContextTokens, double NeededMib, double AvailableMib, bool Fits)
    {
        public bool Reduced => Fits && ContextTokens < NormalContextTokens;
    }

    internal static Plan ForModel(string modelPath, SystemRamSnapshot? ram, double maxSystemRamPercent,
        long reclaimableOwnedTextBytes = 0)
    {
        // Do not read a multi-GB model's metadata if Windows RAM could not be
        // measured in the first place. Fail closed with an actionable reason.
        if (ram?.IsValid != true)
            return new Plan(NormalContextTokens, 0, 0, false);
        return Select(ram, maxSystemRamPercent, reclaimableOwnedTextBytes,
            CpuFallbackAdmission.CreateContextEstimator(modelPath));
    }

    /// <summary>Pure deterministic test seam; no Windows APIs or file access.</summary>
    internal static Plan Select(SystemRamSnapshot? ram, double maxSystemRamPercent,
        long reclaimableOwnedTextBytes, Func<int, double> estimateMib)
    {
        if (ram?.IsValid != true || !double.IsFinite(maxSystemRamPercent) ||
            maxSystemRamPercent is <= 0 or > 100)
            return new Plan(NormalContextTokens, 0, 0, false);

        double availableMib = RuntimeMemoryBudget.CalculateCorporateAvailableBytes(
            ram, maxSystemRamPercent, reclaimableOwnedTextBytes) / 1048576d;
        double lastNeededMib = 0;
        foreach (int contextTokens in CandidateContexts)
        {
            double neededMib = estimateMib(contextTokens);
            if (!double.IsFinite(neededMib) || neededMib <= 0)
                return new Plan(NormalContextTokens, 0, availableMib, false);
            lastNeededMib = neededMib;
            if (neededMib <= availableMib)
                return new Plan(contextTokens, neededMib, availableMib, true);
        }
        return new Plan(MinimumUsefulContextTokens, lastNeededMib, availableMib, false);
    }

    /// <summary>
    /// The CPU runtime itself releases its idle model state; shorten that wait
    /// when the *approved* headroom left after the selected model is small.
    /// This does not unload a model while it is answering and does not modify
    /// system apps, the RAM ceiling, or the selected GGUF.
    /// </summary>
    internal static int IdleUnloadSeconds(Plan plan)
    {
        if (!plan.Fits) return LocalAiProcessManager.CorporateIdleUnloadSeconds;
        double remainingMib = Math.Max(0, plan.AvailableMib - plan.NeededMib);
        if (remainingMib < 768) return 120;
        if (remainingMib < 1536) return 180;
        return LocalAiProcessManager.CorporateIdleUnloadSeconds;
    }

    internal static string Shortfall(Plan plan, SystemRamSnapshot? ram, double maxSystemRamPercent)
    {
        if (ram?.IsValid != true || plan.NeededMib <= 0)
            return "Corporate CPU memory availability could not be measured safely, or the model footprint is unknown. "
                + "Retry in Runtime Manager; no other application was stopped.";
        return DescribeShortfall(plan.NeededMib, plan.AvailableMib, ram, maxSystemRamPercent);
    }

    /// <summary>
    /// The refusal in plain numbers: what the model needs, what PaRuX may use right now, what Windows reports free, what
    /// stays reserved for Windows and the user's other programs, and how much more must be freed. Shared by every
    /// Corporate memory refusal so the user always knows what to do.
    /// </summary>
    internal static string DescribeShortfall(double neededMib, double availableMib, SystemRamSnapshot ram, double maxSystemRamPercent)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double windowsFreeGiB = ram.AvailableBytes / 1073741824d;
        double reserveGiB = RuntimeMemoryBudget.CorporateReserveBytes(ram) / 1073741824d;
        double missingGiB = Math.Max(0.1, Math.Ceiling(Math.Max(0, neededMib - availableMib) / 1024d * 10d) / 10d);
        string limitNote = maxSystemRamPercent < RuntimeMemoryBudget.CorporateDynamicFromPercent
            ? " The PaRuX profile also caps total system RAM use at " + maxSystemRamPercent.ToString("0.#", inv) + "%."
            : string.Empty;
        return "Not enough free memory to start the local model. It needs about " + (neededMib / 1024d).ToString("0.0", inv)
            + " GB (at the smallest 2048-token context); PaRuX can use about " + (availableMib / 1024d).ToString("0.0", inv)
            + " GB right now. Windows reports " + windowsFreeGiB.ToString("0.0", inv) + " GB free, and " + reserveGiB.ToString("0.0", inv)
            + " GB is kept free for Windows and your other programs." + limitNote
            + " Free about " + missingGiB.ToString("0.0", inv) + " GB more (for example close a heavy application or browser tabs you do not need) and try again."
            + " PaRuX did not close any program and did not change the model.";
    }
}
