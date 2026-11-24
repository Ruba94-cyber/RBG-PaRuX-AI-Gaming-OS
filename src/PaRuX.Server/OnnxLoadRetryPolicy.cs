namespace PaRuX.Server;

/// <summary>
/// A document-search ONNX model can fail to load for a transient reason on a busy business PC (memory pressure, the antivirus
/// still scanning the file or the native runtime). Keeping the failure forever disabled semantic search until restart, so a
/// load is retried a bounded number of times after a cool-down; a model that keeps failing stays off (keyword search takes over).
/// </summary>
internal static class OnnxLoadRetryPolicy
{
    public const int MaxFailures = 3;
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(45);

    /// <summary>True when a failed load may be attempted again.</summary>
    public static bool MayRetry(int failures, long lastFailureTicksUtc, DateTime nowUtc)
        => failures > 0
           && failures < MaxFailures
           && nowUtc.Ticks - lastFailureTicksUtc >= Cooldown.Ticks;
}
