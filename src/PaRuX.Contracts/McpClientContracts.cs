namespace PaRuX.Contracts;

public static class McpTransports
{
    public const string Stdio = "stdio";
    public const string Http = "http";
}

public static class McpAuthModes
{
    public const string None = "none";
    public const string Bearer = "bearer";
    public const string OAuth = "oauth";
}

public sealed class McpServerConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Transport { get; set; } = McpTransports.Stdio;
    public string Command { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string AuthMode { get; set; } = McpAuthModes.None;
    public string OAuthClientId { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public List<string> AllowedTools { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// <summary>Per-server connect and tool-call deadline in seconds; 0 keeps the built-in default.</summary>
    public int TimeoutSeconds { get; set; }
}

public sealed class McpServerStatus
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Transport { get; set; } = McpTransports.Stdio;
    public string Command { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string AuthMode { get; set; } = McpAuthModes.None;
    public bool HasCredential { get; set; }
    public bool IsRemote { get; set; }
    public List<string> AllowedTools { get; set; } = new();
    public bool Enabled { get; set; }
    public bool Connected { get; set; }
    public int ToolCount { get; set; }
    public List<string> ToolNames { get; set; } = new();
    public List<string> WithheldToolNames { get; set; } = new();
    public string? PendingAuthorizationUrl { get; set; }
    public string? LastError { get; set; }
    /// <summary>True while the owner has stopped this server for the session: it is not reconnected until started or reloaded.</summary>
    public bool Stopped { get; set; }
    /// <summary>Effective connect/tool-call deadline in seconds (the configured value, or the built-in default).</summary>
    public int TimeoutSeconds { get; set; }
}

public sealed class AddMcpServerRequest
{
    public string Name { get; set; } = string.Empty;
    public string Transport { get; set; } = McpTransports.Stdio;
    public string Command { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string AuthMode { get; set; } = McpAuthModes.None;
    public string? Credential { get; set; }
    public string OAuthClientId { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public List<string> AllowedTools { get; set; } = new();
    /// <summary>
    /// Defaults true for ordinary Settings/API callers. Plugin/import boundaries set false so an
    /// external process/remote tool can never start between persistence and the user's permission
    /// review.
    /// </summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Optional per-server deadline in seconds (5-300); omit or 0 for the default.</summary>
    public int TimeoutSeconds { get; set; }
}

public sealed class SetMcpServerTimeoutRequest
{
    public int TimeoutSeconds { get; set; }
}

public sealed class SetMcpAllowedToolsRequest
{
    public List<string> AllowedTools { get; set; } = new();
}

public sealed class SetMcpServerEnabledRequest
{
    public bool Enabled { get; set; }
}
