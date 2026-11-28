namespace PaRuX.Contracts;

/// <summary>Lifecycle of the optional Internet tunnel (Settings, Remote access). Off by default.</summary>
public enum RemoteTunnelState { Off, Starting, Online, Error }

/// <summary>
/// Stable machine-readable reason codes. The desktop maps them to localized text; <see cref="RemoteTunnelStatus.Message"/>
/// carries the English fallback. Never contains a token or a raw tool log line.
/// </summary>
public static class RemoteTunnelReasons
{
    public const string None = "none";
    public const string ConsentRequired = "consent_required";
    public const string CorporateBlocked = "corporate_blocked";
    public const string GamingActive = "gaming_active";
    public const string ComponentMissing = "component_missing";
    public const string ComponentSkipped = "component_skipped";
    public const string InstallFailed = "install_failed";
    public const string ListenerUnavailable = "listener_unavailable";
    public const string StartFailed = "start_failed";
    public const string UrlTimeout = "url_timeout";
    public const string ProcessExited = "process_exited";
    public const string NetworkUnreachable = "network_unreachable";
    public const string TokenRejected = "token_rejected";
    public const string NamedUrlMissing = "named_url_missing";
}

/// <summary>What the person can do next. Drives the single call-to-action next to the status.</summary>
public static class RemoteTunnelActions
{
    public const string None = "none";
    public const string Consent = "consent";
    public const string InstallComponent = "install_component";
    public const string Retry = "retry";
    public const string CheckNetwork = "check_network";
    public const string CheckToken = "check_token";
    public const string EndGaming = "end_gaming";
}

public sealed class RemoteTunnelPendingPairing
{
    public string Id { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public DateTime RequestedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
}

public sealed class RemoteTunnelStatus
{
    public RemoteTunnelState State { get; set; }
    /// <summary>The user's choice. True can coexist with State=Off while Gaming Mode holds the tunnel down.</summary>
    public bool Enabled { get; set; }
    public bool ConsentGiven { get; set; }
    public bool Allowed { get; set; } = true;
    public string Mode { get; set; } = "quick";
    public string PublicUrl { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = RemoteTunnelReasons.None;
    public string Message { get; set; } = string.Empty;
    public string NextAction { get; set; } = RemoteTunnelActions.None;
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; }
    /// <summary>Loopback port a named tunnel's public hostname must forward to (http://127.0.0.1:port).</summary>
    public int ListenerPort { get; set; }

    public bool ComponentDetected { get; set; }
    /// <summary>PARUX_MANAGED, USER_MANAGED or empty when nothing is detected.</summary>
    public string ComponentOwnership { get; set; } = string.Empty;
    public string ComponentVersion { get; set; } = string.Empty;
    public string ComponentPath { get; set; } = string.Empty;
    public string PinnedVersion { get; set; } = string.Empty;
    public bool ComponentSkipped { get; set; }

    public bool HasNamedToken { get; set; }
    public string NamedPublicUrl { get; set; } = string.Empty;
    public List<RemoteTunnelPendingPairing> PendingPairings { get; set; } = new();
}

public sealed class RemoteTunnelConfigRequest
{
    /// <summary>Null leaves the switch alone.</summary>
    public bool? Enabled { get; set; }
    /// <summary>True records the explicit consent to expose the paired-device API through Cloudflare.</summary>
    public bool? Consent { get; set; }
    /// <summary>Null leaves a stored token alone; empty removes it; a value replaces it (stored with DPAPI).</summary>
    public string? NamedToken { get; set; }
    /// <summary>The https hostname the named tunnel serves, needed because Cloudflare does not print it.</summary>
    public string? NamedPublicUrl { get; set; }
}

public sealed class RemoteTunnelLocateRequest
{
    public string Path { get; set; } = string.Empty;
}

/// <summary>Returned (202) when a NEW device pairs through the tunnel: the PC owner must approve first.</summary>
public sealed class PairingApprovalPending
{
    public string RequestId { get; set; } = string.Empty;
    public string PollSecret { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 2;
    public DateTime ExpiresUtc { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class PairingPollRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string PollSecret { get; set; } = string.Empty;
}
