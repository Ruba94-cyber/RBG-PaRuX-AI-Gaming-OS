namespace PaRuX.Server;

/// <summary>
/// A document-search ONNX model can fail to load for a transient reason on a busy business PC (memory pressure, the antivirus
/// still scanning the file or the native runtime). Keeping the failure forever disabled semantic search until restart, so a
/// load is retried after a cool-down. After <see cref="MaxFailures"/> quick attempts the retry rate drops to one every
/// <see cref="SlowCooldown"/> (it never stops for good: a lock held by a scanner for several minutes must not disable semantic
/// search until the next restart); meanwhile keyword search takes over.
/// </summary>
internal static class OnnxLoadRetryPolicy
{
    public const int MaxFailures = 3;
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan SlowCooldown = TimeSpan.FromMinutes(5);

    /// <summary>True when a failed load may be attempted again.</summary>
    public static bool MayRetry(int failures, long lastFailureTicksUtc, DateTime nowUtc)
        => failures > 0
           && nowUtc.Ticks - lastFailureTicksUtc >= (failures < MaxFailures ? Cooldown : SlowCooldown).Ticks;
}
