namespace PaRuX.Server.ChatCode;

/// <summary>Hard limits for one sandboxed run. Every value has a ceiling the caller cannot raise.</summary>
public sealed record ChatCodeLimits
{
    public const int MaxTimeoutSeconds = 60;
    public const int MaxCodeCharacters = 32_000;

    public int TimeoutSeconds { get; init; } = 20;
    /// <summary>Committed-memory cap for the interpreter (Job Object process memory limit).</summary>
    public long MemoryBytes { get; init; } = 512L << 20;
    /// <summary>CPU-time cap; a busy loop is stopped by this even if the wall-clock waiter were stalled.</summary>
    public int CpuSeconds { get; init; } = 30;
    /// <summary>stdout+stderr kept for the model and the user; the run is stopped when the log passes this.</summary>
    public int MaxOutputBytes { get; init; } = 64 * 1024;
    /// <summary>Everything the program may write to its working folder; the run is stopped when it passes this.</summary>
    public long MaxWorkFolderBytes { get; init; } = 48L << 20;
    public int MaxArtifacts { get; init; } = 8;
    public long MaxArtifactBytes { get; init; } = 2L << 20;
    public long MaxTotalArtifactBytes { get; init; } = 4L << 20;
    public long MaxInputBytes { get; init; } = 5L << 20;

    public ChatCodeLimits Clamped() => this with
    {
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, MaxTimeoutSeconds),
        MemoryBytes = Math.Clamp(MemoryBytes, 64L << 20, 1L << 30),
        CpuSeconds = Math.Clamp(CpuSeconds, 1, 120),
        MaxOutputBytes = Math.Clamp(MaxOutputBytes, 1_024, 256 * 1024),
        MaxWorkFolderBytes = Math.Clamp(MaxWorkFolderBytes, 1L << 20, 256L << 20),
        MaxArtifacts = Math.Clamp(MaxArtifacts, 0, 16),
        MaxArtifactBytes = Math.Clamp(MaxArtifactBytes, 1_024, 8L << 20),
        MaxTotalArtifactBytes = Math.Clamp(MaxTotalArtifactBytes, 1_024, 16L << 20),
        MaxInputBytes = Math.Clamp(MaxInputBytes, 0, 32L << 20)
    };
}

/// <summary>A file the USER attached for this run. The model can never name a path; only the host supplies these.</summary>
public sealed record ChatCodeInputFile(string Name, byte[] Content);

public sealed record ChatCodeRequest(
    string Code,
    ChatCodeLimits? Limits = null,
    IReadOnlyList<ChatCodeInputFile>? Inputs = null);

public enum ChatCodeStatus
{
    Completed,
    NonZeroExit,
    TimedOut,
    OutputLimitExceeded,
    Cancelled,
    /// <summary>The request itself was refused (empty/oversized code, bad attachment).</summary>
    Rejected,
    /// <summary>Gaming Mode is active: nothing was started.</summary>
    BlockedGaming,
    /// <summary>No PaRuX-managed Python runtime is installed: nothing was started.</summary>
    BlockedNeedsRuntime,
    /// <summary>The sandbox itself could not be established; the code was NOT run (there is no unsandboxed fallback).</summary>
    SandboxUnavailable
}

public sealed record ChatCodeArtifact(
    string Name,
    string MimeType,
    long Bytes,
    /// <summary>"image", "html", "text" or "data".</summary>
    string Kind,
    /// <summary>Base64 for images/data; UTF-8 text for text and HTML.</summary>
    string Content,
    bool IsBase64);

public sealed record ChatCodeResult(
    ChatCodeStatus Status,
    int? ExitCode,
    string Output,
    bool OutputTruncated,
    IReadOnlyList<ChatCodeArtifact> Artifacts,
    IReadOnlyList<string> SkippedArtifacts,
    string? Message,
    long ElapsedMilliseconds);

/// <summary>What the process launcher needs; the runner has already built the folder and the argument list.</summary>
public sealed record ChatCodeLaunch(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkDirectory,
    /// <summary>Parent of every run folder: the only place the sandbox identity is given write access.</summary>
    string WritableRoot,
    /// <summary>The interpreter's own folder: read-only for the sandbox identity.</summary>
    IReadOnlyList<string> ReadOnlyRoots,
    IReadOnlyDictionary<string, string> Environment,
    ChatCodeLimits Limits);

public sealed record ChatCodeProcessResult(
    int? ExitCode,
    string Output,
    bool OutputTruncated,
    bool TimedOut,
    bool LimitExceeded,
    string? LimitReason,
    /// <summary>Non-null when the sandbox could not be established and nothing ran.</summary>
    string? LaunchError);

public interface IChatCodeProcessLauncher
{
    Task<ChatCodeProcessResult> RunAsync(ChatCodeLaunch launch, CancellationToken ct);
}
