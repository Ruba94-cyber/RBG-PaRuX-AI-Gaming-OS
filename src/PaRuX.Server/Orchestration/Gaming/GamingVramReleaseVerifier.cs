using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>What the post-release check could actually prove about the AI hard-off.</summary>
internal enum GamingVramReleaseOutcome
{
    /// <summary>Every PaRuX-owned inference process is gone and used VRAM stopped moving.</summary>
    Verified,
    /// <summary>PaRuX-owned processes are gone, but VRAM could not be measured or had not settled in time (degraded, honest).</summary>
    Unverified,
    /// <summary>A PaRuX-owned inference process is still alive after the bounded wait: the release failed.</summary>
    NotReleased
}

internal sealed record GamingVramReleaseReport(
    GamingVramReleaseOutcome Outcome,
    int? BeforeUsedMib,
    int? AfterUsedMib,
    IReadOnlyList<int> OwnedProcessesStillAlive,
    TimeSpan Waited,
    string Message)
{
    /// <summary>MiB freed on the card between the baseline and the final sample; null when either is unknown.</summary>
    public long? ReleasedMib => BeforeUsedMib is { } b && AfterUsedMib is { } a ? Math.Max(0, (long)b - a) : null;
}

/// <summary>
/// Verifies, with a bounded wait, that a Gaming hard-off really released the GPU instead of trusting that a stop was
/// sent. Only PaRuX-owned processes are judged (a foreign process, including the game, may legitimately hold VRAM, so
/// total VRAM alone never fails the release). Pure and injectable: no hardware is needed to test it.
/// </summary>
internal static class GamingVramReleaseVerifier
{
    internal static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);
    internal const int DefaultToleranceMib = 96;

    internal static async Task<GamingVramReleaseReport> VerifyAsync(
        int? beforeUsedMib,
        Func<CancellationToken, Task<int?>> sampleUsedMib,
        Func<IReadOnlyList<int>> ownedProcessesAlive,
        CancellationToken ct,
        TimeSpan? maxWait = null,
        TimeSpan? interval = null,
        int toleranceMib = DefaultToleranceMib)
    {
        TimeSpan limit = maxWait ?? DefaultMaxWait;
        TimeSpan step = interval ?? DefaultInterval;
        var watch = Stopwatch.StartNew();

        int? previous = null;
        int? last = null;
        bool settled = false;
        IReadOnlyList<int> alive = ownedProcessesAlive();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            alive = ownedProcessesAlive();
            int? current = await SafeSampleAsync(sampleUsedMib, ct).ConfigureAwait(false);
            if (current is not null)
            {
                settled = previous is not null && Math.Abs(current.Value - previous.Value) <= toleranceMib;
                previous = current;
                last = current;
            }
            else
            {
                settled = false;
                last = null;
            }
            bool memoryOk = current is null || settled;
            if (alive.Count == 0 && memoryOk) break;
            if (watch.Elapsed >= limit) break;
            await Task.Delay(step, ct).ConfigureAwait(false);
        }

        if (alive.Count > 0)
            return new GamingVramReleaseReport(GamingVramReleaseOutcome.NotReleased, beforeUsedMib, last, alive, watch.Elapsed,
                $"{alive.Count} PaRuX-owned inference process(es) were still running after {watch.Elapsed.TotalSeconds:0.#}s; the GPU was not released.");
        if (last is null)
            return new GamingVramReleaseReport(GamingVramReleaseOutcome.Unverified, beforeUsedMib, null, alive, watch.Elapsed,
                "PaRuX-owned inference processes exited, but GPU memory telemetry is unavailable so the release could not be measured.");
        if (!settled)
            return new GamingVramReleaseReport(GamingVramReleaseOutcome.Unverified, beforeUsedMib, last, alive, watch.Elapsed,
                "PaRuX-owned inference processes exited, but GPU memory was still changing when the bounded wait ended.");
        long? freed = beforeUsedMib is { } b ? Math.Max(0, (long)b - last.Value) : null;
        return new GamingVramReleaseReport(GamingVramReleaseOutcome.Verified, beforeUsedMib, last, alive, watch.Elapsed,
            freed is { } f
                ? $"PaRuX-owned inference exited and GPU memory settled; about {f} MiB was released."
                : "PaRuX-owned inference exited and GPU memory settled.");
    }

    private static async Task<int?> SafeSampleAsync(Func<CancellationToken, Task<int?>> sample, CancellationToken ct)
    {
        try { return await sample(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
