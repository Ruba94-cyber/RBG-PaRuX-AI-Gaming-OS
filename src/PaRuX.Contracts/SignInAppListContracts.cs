namespace PaRuX.Contracts;

/// <summary>Kind of a PaRuX-owned after-sign-in list entry.</summary>
public enum SignInAppKind { Exe, Appx }

/// <summary>Timing settings of the after-sign-in list. All values are clamped to the documented bounds.</summary>
public sealed record SignInAppListSettings(
    int LaunchSpacingSeconds = 3,
    int DesktopDelaySeconds = 10,
    int ReadyTimeoutSeconds = 60,
    int ConsoleReadyDelaySeconds = 5,
    int ModeWaitSeconds = 15);

/// <summary>One entry. Path is required for Exe, Aumid for Appx; Process is an optional already-running probe name.</summary>
public sealed record SignInAppEntry(
    string Name,
    SignInAppKind Kind,
    string? Path = null,
    string? Args = null,
    string? Aumid = null,
    string? Process = null,
    bool SkipInConsole = false);

/// <summary>A validated list. Off by default; PaRuX only ever launches this list and never edits third-party startup entries.</summary>
public sealed record SignInAppList(
    bool Enabled,
    SignInAppListSettings Settings,
    IReadOnlyList<SignInAppEntry> Apps)
{
    public static SignInAppList Empty { get; } = new(false, new SignInAppListSettings(), []);
}

public enum SignInAppOutcome
{
    Launched, SkippedAlreadyRunning, SkippedConsoleMode, SkippedGamingActive,
    SkippedInvalid, Failed, Cancelled,
}

public sealed record SignInAppResult(string Name, SignInAppOutcome Outcome, int Attempts, string Reason);

public enum SignInRunState { Idle, Waiting, Running, Completed, Cancelled, SkippedDisabled, SkippedGamingActive }

/// <summary>Observable snapshot of a run: state, the index being processed, and per-app results so far.</summary>
public sealed record SignInRunSnapshot(
    SignInRunState State,
    int CurrentIndex,
    int Total,
    IReadOnlyList<SignInAppResult> Results);

/// <summary>Validation / import outcome. Value is only set when there are no errors.</summary>
public sealed record SignInAppListParseResult(SignInAppList? Value, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool Ok => Value is not null && Errors.Count == 0;
}
