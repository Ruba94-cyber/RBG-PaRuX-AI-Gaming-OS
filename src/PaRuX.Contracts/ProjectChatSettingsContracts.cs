namespace PaRuX.Contracts;

/// <summary>An indexed document the owner pinned into a workspace's prompt. Identified by name inside a knowledge source, never by path.</summary>
public sealed class PinnedDocumentRef
{
    public string Name { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
}

/// <summary>Per-workspace (Project) chat settings: a system prompt template with safe variables and pinned documents under a token budget.</summary>
public sealed class ProjectChatSettingsDto
{
    public string ProjectId { get; set; } = string.Empty;
    /// <summary>Template as typed by the owner, e.g. "Today is {date}. You help with {workspace}."</summary>
    public string SystemPromptTemplate { get; set; } = string.Empty;
    public List<PinnedDocumentRef> PinnedDocuments { get; set; } = new();
    public int PinBudgetTokens { get; set; } = 2_000;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class PinnedDocumentStatusDto
{
    public string Name { get; set; } = string.Empty;
    public bool Included { get; set; }
    public bool Truncated { get; set; }
    public int Tokens { get; set; }
    /// <summary>null, "truncated", "over_budget", "not_found" or "empty".</summary>
    public string? Problem { get; set; }
}

/// <summary>What the settings resolve to right now, so the UI can preview them and warn honestly.</summary>
public sealed class ProjectChatSettingsPreviewDto
{
    public string ExpandedSystemPrompt { get; set; } = string.Empty;
    public List<string> UnknownVariables { get; set; } = new();
    public List<PinnedDocumentStatusDto> Pins { get; set; } = new();
    public int PinBudgetTokens { get; set; }
    public int PinUsedTokens { get; set; }
    public bool PinExceedsBudget { get; set; }
    public bool PinTokensEstimated { get; set; }
    /// <summary>English warning text for the pin plan, or null. The desktop localises from the structured fields.</summary>
    public string? PinWarning { get; set; }
}

public sealed class ProjectChatSettingsResponse
{
    public ProjectChatSettingsDto Settings { get; set; } = new();
    public ProjectChatSettingsPreviewDto Preview { get; set; } = new();
}

public sealed class PinnableDocumentDto
{
    public string Name { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}
