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

    /// <param name="Busy">Fits only by letting Windows page idle applications out: run it, with a plain-language warning.</param>
    /// <param name="Refusal">Why it does not fit (null when it fits or the cause is the classic free-memory shortfall).</param>
    internal sealed record Plan(int ContextTokens, double NeededMib, double AvailableMib, bool Fits,
        bool Busy = false, RuntimeMemoryBudget.CorporateAdmissionOutcome? Refusal = null)
    {
        public bool Reduced => Fits && ContextTokens < NormalContextTokens;
    }

    /// <summary>
    /// The bundled CPU engine memory-maps the GGUF (no --no-mmap / --mlock / --load-mode in
    /// <see cref="LocalAiProcessManager.BuildCorporateCpuArguments"/>), so weights are file-backed: they cost no commit
    /// charge and Windows may evict them, but every token reads them, so they stay in the hot working set. Only the rest
    /// (KV cache, compute buffers, runtime reserve) is private and commit-charged.
    /// </summary>
    internal static double MappedWeightsMib(string modelPath) => new FileInfo(modelPath).Length / 1048576d;

    internal static Plan ForModel(string modelPath, SystemRamSnapshot? ram, double maxSystemRamPercent,
        long reclaimableOwnedTextBytes = 0)
    {
        // Do not read a multi-GB model's metadata if Windows RAM could not be
        // measured in the first place. Fail closed with an actionable reason.
        if (ram?.IsValid != true)
            return new Plan(NormalContextTokens, 0, 0, false);
        return Select(ram, maxSystemRamPercent, reclaimableOwnedTextBytes,
            CpuFallbackAdmission.CreateContextEstimator(modelPath), MappedWeightsMib(modelPath));
    }

    /// <summary>Pure deterministic test seam; no Windows APIs or file access.</summary>
    internal static Plan Select(SystemRamSnapshot? ram, double maxSystemRamPercent,
        long reclaimableOwnedTextBytes, Func<int, double> estimateMib, double mappedWeightsMib = 0)
    {
        if (ram?.IsValid != true || !double.IsFinite(maxSystemRamPercent) ||
            maxSystemRamPercent is <= 0 or > 100)
            return new Plan(NormalContextTokens, 0, 0, false);

        double comfortableMib = RuntimeMemoryBudget.CalculateCorporateAvailableBytes(
            ram, maxSystemRamPercent, reclaimableOwnedTextBytes) / 1048576d;
        Plan? firstBusy = null;
        RuntimeMemoryBudget.CorporateAdmission? last = null;
        double lastNeededMib = 0;
        foreach (int contextTokens in CandidateContexts)
        {
            double neededMib = estimateMib(contextTokens);
            if (!double.IsFinite(neededMib) || neededMib <= 0)
                return new Plan(NormalContextTokens, 0, comfortableMib, false);
            lastNeededMib = neededMib;
            double mapped = Math.Clamp(mappedWeightsMib, 0, neededMib);
            RuntimeMemoryBudget.CorporateAdmission verdict = RuntimeMemoryBudget.EvaluateCorporate(
                ram, maxSystemRamPercent, reclaimableOwnedTextBytes, neededMib - mapped, mapped);
            last = verdict;
            if (verdict.Outcome == RuntimeMemoryBudget.CorporateAdmissionOutcome.Admit)
                return new Plan(contextTokens, neededMib, verdict.AvailableMib, true);
            if (verdict.Busy) firstBusy ??= new Plan(contextTokens, neededMib, verdict.AvailableMib, true, Busy: true);
        }
        // A comfortable fit at a smaller context wins over a busy fit at a larger one only when the larger one is not
        // needed for the chat to work; the largest context that fits comfortably is returned above, otherwise the largest
        // busy one (never silently shrinking below what Windows can actually serve).
        if (firstBusy is not null) return firstBusy;
        return new Plan(MinimumUsefulContextTokens, lastNeededMib, last?.AvailableMib ?? comfortableMib, false,
            Refusal: last?.Outcome);
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
        // A busy PC: give the memory back quickly so the other applications are not paged out for long.
        if (plan.Busy) return 120;
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
        if (plan.Refusal == RuntimeMemoryBudget.CorporateAdmissionOutcome.RefuseCommit)
            return CorporateMemoryMessages.CommitExhausted(plan.NeededMib, ram);
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

/// <summary>Plain-language, bilingual (Italian / English) texts for the Corporate memory admission. No raw numbers jargon, always a next action.</summary>
internal static class CorporateMemoryMessages
{
    internal const string BusyEnglish =
        "Your PC is busy; replies may be slower. PaRuX started the model anyway because Windows can make room by moving idle programs out of memory. No program was closed.";
    internal const string BusyItalian =
        "Il PC è molto occupato: le risposte potrebbero essere più lente. PaRuX ha avviato comunque il modello perché Windows può fare spazio spostando i programmi inattivi. Nessun programma è stato chiuso.";

    internal static string Busy => BusyItalian + " / " + BusyEnglish;

    /// <summary>The commit limit (RAM plus page file) is used up: the one case where starting would really fail.</summary>
    internal static string CommitExhausted(double neededMib, SystemRamSnapshot ram)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double freeGiB = (ram.CommitAvailableBytes ?? 0UL) / 1073741824d;
        string need = (neededMib / 1024d).ToString("0.0", inv);
        string free = freeGiB.ToString("0.0", inv);
        return "Not enough free memory to start the local model: Windows has run out of memory it can promise to new programs "
            + "(about " + free + " GB left, the model needs private memory on top of its " + need + " GB file). "
            + "Close a heavy program or some browser tabs, or ask IT to enlarge the Windows page file, then try again. "
            + "PaRuX did not close any program. / "
            + "Memoria insufficiente per avviare il modello locale: Windows non può più garantire memoria ai nuovi programmi "
            + "(restano circa " + free + " GB). Chiudi un programma pesante o alcune schede del browser, oppure chiedi all'IT "
            + "di aumentare il file di paging di Windows, poi riprova. PaRuX non ha chiuso alcun programma.";
    }
}
