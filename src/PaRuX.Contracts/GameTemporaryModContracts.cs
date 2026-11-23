namespace PaRuX.Contracts;

/// <summary>
/// An AI may propose a temporary plain-text config change, but the owner must
/// inspect the preview before any game file changes. Only local managed portable
/// games paused by PaRuX Quick Resume are eligible in this initial adapter.
/// </summary>
public sealed record GameModProposalRequest(
    string ManualGameId, int ProcessId, DateTime StartedUtc,
    string RelativePath, string ProposedText, bool OfflineSinglePlayerConfirmed);

/// <summary>
/// AI-safe one-setting patch proposal. For INI/CFG, Section is the INI section and
/// Value is plain text. For JSON, Section is an optional dot-separated path of
/// existing nested objects and Value must be a JSON scalar literal, e.g. true,
/// 120 or "high". The original config remains in the owner-only review UI.
/// </summary>
public sealed record GameModSettingPatchRequest(
    string ManualGameId, int ProcessId, DateTime StartedUtc,
    string RelativePath, string Section, string Key, string Value,
    bool OfflineSinglePlayerConfirmed);

/// <summary>
/// One existing INI/CFG key or JSON scalar property in a game-owned config.
/// The AI cannot create keys, replace objects/arrays or supply executable scripts.
/// </summary>
public sealed record GameModSettingEdit(string Section, string Key, string Value);

/// <summary>
/// One atomic gameplay preset: 2-8 edits in the SAME pre-existing config file.
/// A successful preview still requires the owner to compare the full before/after
/// and explicitly approve the separate, reversible apply.
/// </summary>
public sealed record GameModBatchPatchRequest(
    string ManualGameId, int ProcessId, DateTime StartedUtc,
    string RelativePath, IReadOnlyList<GameModSettingEdit> Edits,
    bool OfflineSinglePlayerConfirmed);

public sealed record GameModPreviewDto(
    string PreviewId, string GameName, string RelativePath,
    string CurrentSha256, string OriginalText, string ProposedText,
    DateTime ExpiresUtc, string Advisory);

public sealed record GameModApplyRequest(string PreviewId, bool Consent);
public sealed record GameModRestoreRequest(string ModId, bool Consent);

public sealed record GameTemporaryModDto(
    string Id, string GameName, string RelativePath, DateTime AppliedUtc,
    string OriginalSha256, string ModifiedSha256, string Advisory);
