namespace PaRuX.Contracts;

/// <summary>
/// The one-line wording of the post-release GPU check ("verified" / "unverified" / "not_released" plus MiB freed) shown
/// next to the AI workload line of the Gaming status. Pure so both languages are testable; empty when no check ran.
/// A MiB figure is only shown when it was measured, never a guess.
/// </summary>
public static class GamingReleaseVerificationText
{
    public static string English(GamingStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return Normalize(status.AiReleaseVerification) switch
        {
            "verified" => status.AiVramReleasedMib is long mib
                ? $"GPU release verified ({mib} MiB freed)"
                : "GPU release verified",
            "unverified" => "GPU release unverified (no reliable GPU telemetry)",
            "not_released" => "GPU NOT released (a PaRuX AI process is still running)",
            _ => string.Empty,
        };
    }

    public static string Italian(GamingStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return Normalize(status.AiReleaseVerification) switch
        {
            "verified" => status.AiVramReleasedMib is long mib
                ? $"rilascio GPU verificato ({mib} MiB liberati)"
                : "rilascio GPU verificato",
            "unverified" => "rilascio GPU non verificato (telemetria GPU non affidabile)",
            "not_released" => "GPU NON rilasciata (un processo IA di PaRuX è ancora attivo)",
            _ => string.Empty,
        };
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
