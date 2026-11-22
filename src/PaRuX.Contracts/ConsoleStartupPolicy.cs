namespace PaRuX.Contracts;

/// <summary>What a launch does with the Console/TV start preference.</summary>
public enum ConsoleStartupDecision
{
    /// <summary>Start exactly as before (the AI window in desktop mode).</summary>
    Normal,
    /// <summary>Open directly in Console/TV mode on the chosen TV (the Gaming library).</summary>
    EnterConsole,
    /// <summary>Start minimized.</summary>
    Minimize
}

/// <summary>
/// Pure decision for the opt-in "start in TV mode when the TV is on" preference. The preference is off by default; when it is on, a
/// TV that is chosen AND connected opens Console/TV mode, and otherwise the owner's fallback applies ("normal" or "minimized").
/// A missing target, an unknown fallback value or any doubt resolves to the normal window, never to a hidden app.
/// </summary>
public static class ConsoleStartupPolicy
{
    public const string FallbackNormal = "normal";
    public const string FallbackMinimized = "minimized";

    public static ConsoleStartupDecision Decide(bool startOnTvWhenPresent, string? targetDisplayId, bool tvPresent, string? withoutTvFallback)
    {
        if (!startOnTvWhenPresent) return ConsoleStartupDecision.Normal;
        if (!string.IsNullOrWhiteSpace(targetDisplayId) && tvPresent) return ConsoleStartupDecision.EnterConsole;
        return string.Equals(withoutTvFallback, FallbackMinimized, System.StringComparison.Ordinal)
            ? ConsoleStartupDecision.Minimize
            : ConsoleStartupDecision.Normal;
    }
}
