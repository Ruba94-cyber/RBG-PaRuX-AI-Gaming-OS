namespace PaRuX.Contracts;

/// <summary>
/// "Prompt to mod": the owner describes a change in natural language and picks the
/// existing game files the local model may touch. The model proposes a structured change
/// set; nothing is written until the owner approves each file separately.
/// <paramref name="ProcessId"/> / <paramref name="StartedUtc"/> are only needed when the game
/// is running and paused by Quick Resume (PaRuX-managed portable games only); 0 means the game is closed.
/// </summary>
public sealed record GameModPromptRequest(
    string ManualGameId, string Prompt, IReadOnlyList<string> RelativePaths,
    string? ModFolder, int ProcessId, DateTime StartedUtc, bool OfflineSinglePlayerConfirmed);

/// <summary>One side-by-side diff row. Kind: same, changed, removed, added or gap (collapsed unchanged lines).</summary>
public sealed record GameModDiffRowDto(string Kind, int OldLine, string OldText, int NewLine, string NewText);

/// <summary>One file of a change set. Kind: text or image.</summary>
public sealed record GameModFileChangeDto(
    string RelativePath, string Kind, string Format, bool IsNewFile,
    string BeforeSha256, string AfterSha256, long BeforeBytes, long AfterBytes, int ChangedLines,
    IReadOnlyList<GameModDiffRowDto> Rows, string? BeforePngBase64, string? AfterPngBase64,
    IReadOnlyList<string> Warnings);

public sealed record GameModChangeSetDto(
    string ChangeSetId, string GameName, string Prompt, string Summary,
    IReadOnlyList<GameModFileChangeDto> Files, DateTime ExpiresUtc, string Advisory);

/// <summary>Approval is per file: only the listed paths are written; the rest of the set is discarded.</summary>
public sealed record GameModChangeSetApplyRequest(
    string ChangeSetId, IReadOnlyList<string> ApprovedPaths, bool Consent,
    int ProcessId = 0, DateTime StartedUtc = default);

public sealed record GameModBackupFileDto(string RelativePath, bool WasNewFile);

public sealed record GameModBackupSetDto(
    string Id, string GameName, DateTime AppliedUtc,
    IReadOnlyList<GameModBackupFileDto> Files, string Advisory);

public sealed record GameModChangeSetRestoreRequest(
    string BackupId, bool Consent, int ProcessId = 0, DateTime StartedUtc = default);

public sealed record GameModRestoreResultDto(
    IReadOnlyList<string> Restored, IReadOnlyList<string> Conflicts, bool Complete);

/// <summary>An existing editable file under the game's own folder that a change set may target.</summary>
public sealed record GameModCandidateDto(string RelativePath, string Kind, string Format, long Bytes);
