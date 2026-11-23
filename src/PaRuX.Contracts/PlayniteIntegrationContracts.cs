namespace PaRuX.Contracts;

// ---- Playnite integration (optional USER_MANAGED / SYSTEM_DETECTED component). PaRuX never edits Playnite's files, config, themes or library. ----

public static class PlayniteModes
{
    public const string Desktop = "desktop";
    public const string Console = "console";
    public static bool IsValid(string? mode) => mode is Desktop or Console;
}

/// <summary>What was found on this PC. Read-only evidence; "Source" says where it was found.</summary>
public sealed class PlayniteComponentDto
{
    public string Id { get; set; } = "playnite";
    public ComponentOwnership Ownership { get; set; } = ComponentOwnership.SYSTEM_DETECTED;
    public bool Detected { get; set; }
    /// <summary>"installed" | "portable" | "running" | "located" | "none"</summary>
    public string InstallKind { get; set; } = "none";
    /// <summary>"registry" | "local-app-data" | "app-data" | "program-files" | "process" | "user-located" | "none"</summary>
    public string Source { get; set; } = "none";
    public string? Folder { get; set; }
    public string? DesktopExe { get; set; }
    public string? FullscreenExe { get; set; }
    public string? Version { get; set; }
    public bool Running { get; set; }
    /// <summary>True when the owner chose "Skip": nothing is probed in the background and no AI yield happens.</summary>
    public bool Skipped { get; set; }
    public string[] Actions { get; set; } = System.Array.Empty<string>();
}

public sealed class PlayniteSettingsDto
{
    /// <summary>Stop PaRuX-owned inference while a Playnite game runs and give AI back afterwards. On by default.</summary>
    public bool AiYieldEnabled { get; set; } = true;
    /// <summary>"desktop" | "console" (Playnite Fullscreen).</summary>
    public string StartMode { get; set; } = PlayniteModes.Desktop;
    public string? TargetDisplayId { get; set; }
    /// <summary>Console mode only when the chosen display is connected; otherwise the fallback below applies.</summary>
    public bool ConsoleOnlyWhenTargetConnected { get; set; }
    /// <summary>"normal" | "minimized" (ConsoleStartupPolicy fallbacks) when the chosen display is missing.</summary>
    public string FallbackWithoutDisplay { get; set; } = ConsoleStartupPolicy.FallbackNormal;
    public bool ReturnToPaRuXOnClose { get; set; } = true;
    /// <summary>Start Playnite (in StartMode) when PaRuX itself starts. Off by default.</summary>
    public bool LaunchAtStartup { get; set; }
    /// <summary>When PaRuX itself starts in Console mode, start Playnite Fullscreen (with the theme the owner selected in Playnite). On by default; needs Playnite found and not skipped.</summary>
    public bool ConsoleStartsPlaynite { get; set; } = true;
    public bool Skipped { get; set; }
}

public sealed class PlayniteSessionDto
{
    /// <summary>"idle" | "game-detected" | "yielding" | "yielded" | "restoring" | "user-override" | "failed" | "disabled"</summary>
    public string State { get; set; } = "idle";
    public bool PlayniteRunning { get; set; }
    public bool PaRuXLaunchedPlaynite { get; set; }
    public string? LaunchedMode { get; set; }
    public int TrackedProcessCount { get; set; }
    public string? GameProcessName { get; set; }
    public DateTime? SessionStartedUtc { get; set; }
    public DateTime? LastEndedUtc { get; set; }
    public string? LastError { get; set; }
    public int YieldAttempts { get; set; }
    /// <summary>Increments each time a Playnite process PaRuX started has closed; the app returns to PaRuX on a change.</summary>
    public long PaRuXLaunchedClosedCounter { get; set; }
}

/// <summary>
/// Pure return-to-AI policy. The caller captures the close counter before opening
/// Playnite, so even a process that closes before the first UI poll is observed.
/// Never bring PaRuX forward while another PaRuX-started Playnite is still alive.
/// </summary>
public static class PlayniteReturnWatchPolicy
{
    public static bool ShouldBringForward(long? beforeLaunchCounter, PlayniteStatusDto? current, bool forceReturn = false) =>
        beforeLaunchCounter.HasValue &&
        current is not null &&
        (current.Settings.ReturnToPaRuXOnClose || forceReturn) &&
        !current.Session.PaRuXLaunchedPlaynite &&
        current.Session.TrackedProcessCount == 0 &&
        current.Session.PaRuXLaunchedClosedCounter > beforeLaunchCounter.Value;

    /// <summary>
    /// Clear an owned Console handoff after the exact Playnite launch has closed and
    /// every tracked game has exited. This deliberately does NOT depend on the
    /// optional "bring PaRuX to front" preference.
    /// </summary>
    public static bool OwnedCloseSettled(long? beforeLaunchCounter, PlayniteStatusDto? current) =>
        beforeLaunchCounter.HasValue &&
        current is not null &&
        !current.Session.PaRuXLaunchedPlaynite &&
        current.Session.TrackedProcessCount == 0 &&
        current.Session.PaRuXLaunchedClosedCounter > beforeLaunchCounter.Value;

    /// <summary>
    /// When Playnite was already running and is not PaRuX-owned, clear only the
    /// Console handoff after an observed process exit. Never foreground PaRuX.
    /// </summary>
    public static bool ExternalCloseSettled(PlayniteStatusDto? current, bool observedRunning) =>
        observedRunning && current is not null &&
        !current.Session.PlayniteRunning &&
        current.Session.TrackedProcessCount == 0;

    /// <summary>
    /// A game can outlive Playnite itself. Do not steal focus while it is running;
    /// keep the old counter until its tracked process exits, then return to PaRuX.
    /// </summary>
    public static bool KeepPendingClose(long? beforeLaunchCounter, PlayniteStatusDto? current, bool forceReturn = false) =>
        beforeLaunchCounter.HasValue &&
        current is not null &&
        (current.Settings.ReturnToPaRuXOnClose || forceReturn) &&
        !current.Session.PaRuXLaunchedPlaynite &&
        current.Session.TrackedProcessCount > 0 &&
        current.Session.PaRuXLaunchedClosedCounter > beforeLaunchCounter.Value;
}

public sealed class PlayniteStatusDto
{
    public PlayniteComponentDto Component { get; set; } = new();
    public PlayniteSettingsDto Settings { get; set; } = new();
    public PlayniteSessionDto Session { get; set; } = new();
}

/// <summary>
/// UI admission policy for NEW Playnite Desktop/Fullscreen requests, before
/// any window activation. Gamepad, shortcut and Games Tools entry points
/// must use the same predicate. The Core independently rechecks game activity
/// when the launch request actually arrives.
/// </summary>
public static class PlayniteLauncherForegroundPolicy
{
    public static bool RequiresGamePreflight(PlayniteLauncherAction action) =>
        action is PlayniteLauncherAction.Console or PlayniteLauncherAction.Desktop;

    public static bool MayOpen(PlayniteLauncherAction action, bool nativeGameActive, PlayniteStatusDto? status)
    {
        if (!RequiresGamePreflight(action)) return true; // explicit settings/setup can be opened by the owner
        if (nativeGameActive || status?.Session is not { } session) return false;
        // A nonzero process count and all transitional/failure states fail closed.
        // An unrecognized new session state must not automatically steal focus.
        return session.TrackedProcessCount == 0 &&
            (session.State is "idle" or "disabled");
    }
}

public sealed class PlayniteLocateRequest { public string Path { get; set; } = string.Empty; }

public sealed class PlayniteLaunchRequest
{
    /// <summary>"desktop" | "console"; null = the saved start mode.</summary>
    public string? Mode { get; set; }
    public string? DisplayId { get; set; }
}

public sealed class PlayniteLaunchResultDto
{
    public bool Ok { get; set; }
    /// <summary>"started" | "already-running" | "refused"</summary>
    public string Outcome { get; set; } = "refused";
    public string? Mode { get; set; }
    public string? Message { get; set; }
}

/// <summary>Official release of JosefNemec/Playnite resolved for the owner to confirm. Nothing is downloaded by resolving it.</summary>
public sealed class PlayniteInstallPlanDto
{
    public bool Available { get; set; }
    public string? Version { get; set; }
    public string? AssetName { get; set; }
    public string? DownloadUrl { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>SHA-256 from the release metadata; null when the release provides none.</summary>
    public string? Sha256 { get; set; }
    public string? TargetFolder { get; set; }
    public string? Message { get; set; }
}

public sealed class PlayniteInstallRequest
{
    public bool Consent { get; set; }
    public string Version { get; set; } = string.Empty;
    /// <summary>The checksum the owner saw in the plan; the download must match it.</summary>
    public string? Sha256 { get; set; }
    /// <summary>Required only when the release provides no checksum: the owner accepts an unverified download.</summary>
    public bool AcceptUnverified { get; set; }
}

public sealed class PlayniteInstallStatusDto
{
    /// <summary>"idle" | "downloading" | "verifying" | "extracting" | "completed" | "failed" | "cancelled"</summary>
    public string Stage { get; set; } = "idle";
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public string? Error { get; set; }
    public string? Version { get; set; }
}
