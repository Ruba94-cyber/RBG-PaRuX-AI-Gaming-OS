namespace PaRuX.Intelligence;

/// <summary>
/// Conservative local-GPU batch shape used by the managed Unsloth runner and the
/// user-facing preflight. One pure policy prevents the preview saying "ready"
/// when the worker would immediately refuse KTO's four-sample physical batch.
/// </summary>
public static class TrainingBatchShapePolicy
{
    public const int DefaultEffectiveBatch = 4;

    /// <summary>
    /// The baseline was measured with Unsloth on a 16 GiB RTX 5080 at 1,024
    /// tokens (approx. 0.56 GiB for 1 sample, 0.93 GiB for 4).
    /// This is a bounded estimate, NOT a substitute for live VRAM admission.
    /// </summary>
    public static (int MicroBatch, int GradientAccumulation) Plan(
        int maxSequenceLength, long totalVramBytes)
    {
        if (totalVramBytes <= 0) return (1, DefaultEffectiveBatch);
        const double BaseFootprintBytes = 640d * 1024 * 1024;
        double perSequenceBytes = 128d * 1024 * 1024 * Math.Max(1, maxSequenceLength) / 1024d;
        double usableBytes = totalVramBytes * 0.25 - BaseFootprintBytes;
        int affordable = usableBytes <= 0 ? 1 : (int)Math.Floor(usableBytes / perSequenceBytes);
        int microBatch = Math.Clamp(affordable, 1, DefaultEffectiveBatch);
        while (microBatch > 1 && DefaultEffectiveBatch % microBatch != 0) microBatch--;
        return (microBatch, DefaultEffectiveBatch / microBatch);
    }

    /// <summary>
    /// Computes per-GPU micro-batch, gradient accumulation, and total effective batch across
    /// <paramref name="worldSize"/> data-parallel ranks. With <c>worldSize == 1</c>, matches
    /// <see cref="Plan"/> identically. With <c>worldSize &gt;= 2</c>, reduces gradient accumulation
    /// where multiple GPUs already satisfy the target effective batch and reports the exact
    /// resulting <c>MicroBatch * GradientAccumulation * WorldSize</c>.
    /// </summary>
    public static (int MicroBatch, int GradientAccumulation, int EffectiveBatch) PlanMultiGpu(
        int maxSequenceLength, long perGpuVramBytes, int worldSize)
    {
        int ranks = Math.Max(1, worldSize);
        (int microBatch, int singleGpuAccum) = Plan(maxSequenceLength, perGpuVramBytes);
        if (ranks == 1)
            return (microBatch, singleGpuAccum, microBatch * singleGpuAccum);
        int perStepAcrossRanks = microBatch * ranks;
        int gradAccum = perStepAcrossRanks >= DefaultEffectiveBatch
            ? 1
            : (int)Math.Ceiling((double)DefaultEffectiveBatch / perStepAcrossRanks);
        return (microBatch, gradAccum, perStepAcrossRanks * gradAccum);
    }
}
