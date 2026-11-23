namespace PaRuX.Contracts;

// ---- Optional "Aniki ReMake" Playnite Fullscreen theme component (Detect -> Locate existing / Install with PaRuX / Skip). ----
// PARUX_MANAGED only for a theme folder PaRuX itself installed after consent; every other copy is USER_MANAGED and is never modified.
// PaRuX never touches Playnite configuration except the single, consented, backed-up, reversible "Theme" selection in fullscreenConfig.json,
// and never while Playnite is running.

public sealed class PlayniteThemeComponentDto
{
    public string Id { get; set; } = "playnite-aniki-remake";
    public ComponentOwnership Ownership { get; set; } = ComponentOwnership.USER_MANAGED;
    public bool Detected { get; set; }
    /// <summary>True when the Playnite data folder (where Themes\Fullscreen lives) exists, so a theme can be installed or found.</summary>
    public bool PlayniteDataFolderFound { get; set; }
    public string? PlayniteDataFolder { get; set; }
    public string? ThemeFolderName { get; set; }
    public string? ThemeFolder { get; set; }
    public string? Version { get; set; }
    /// <summary>The theme currently set in Playnite's fullscreenConfig.json (read only), or null when unreadable.</summary>
    public string? SelectedTheme { get; set; }
    public bool ConfigReadable { get; set; }
    /// <summary>True when the selected Playnite Fullscreen theme is this Aniki ReMake folder.</summary>
    public bool Selected { get; set; }
    /// <summary>Read-only evidence of the Aniki Helper extension manifest in this Playnite profile. This does NOT prove it loaded successfully.</summary>
    public bool AnikiHelperFoundOnDisk { get; set; }
    public string? AnikiHelperVersion { get; set; }
    /// <summary>True when any Playnite process is running (the configuration is then never edited).</summary>
    public bool PlayniteRunning { get; set; }
    /// <summary>The MIT license file kept next to the theme (file name), or null.</summary>
    public string? LicenseFile { get; set; }
    /// <summary>True when PaRuX can undo its own theme selection (it changed it earlier and nothing changed it since).</summary>
    public bool CanRestoreSelection { get; set; }
    public string? PreviousTheme { get; set; }
    /// <summary>True when PaRuX holds a backup of a theme folder it replaced, or can remove a theme it installed.</summary>
    public bool CanRollback { get; set; }
    public bool Skipped { get; set; }
    public string? Message { get; set; }
    public string[] Actions { get; set; } = System.Array.Empty<string>();
}

/// <summary>The official Mike-Aniki/Aniki-ReMake release resolved for the owner to confirm. Resolving downloads nothing.</summary>
public sealed class PlayniteThemePlanDto
{
    public bool Available { get; set; }
    public string? Version { get; set; }
    public string? AssetName { get; set; }
    public string? DownloadUrl { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>Always the official repository, shown to the owner before anything is downloaded.</summary>
    public string SourceUrl { get; set; } = "https://github.com/Mike-Aniki/Aniki-ReMake";
    public string License { get; set; } = "MIT";
    public string? ThemeFolderName { get; set; }
    public string? TargetFolder { get; set; }
    /// <summary>True when a folder with this name already exists: installing then needs ReplaceExisting and keeps a backup.</summary>
    public bool ExistingFolder { get; set; }
    public string? ExistingVersion { get; set; }
    public bool PlayniteRunning { get; set; }
    public string? Message { get; set; }
}

public sealed class PlayniteThemeInstallRequest
{
    public bool Consent { get; set; }
    public string Version { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
    public bool AcceptUnverified { get; set; }
    /// <summary>Explicit consent to replace an existing folder of the same name (it is moved to a backup, never deleted first).</summary>
    public bool ReplaceExisting { get; set; }
}

public sealed class PlayniteThemeInstallStatusDto
{
    /// <summary>"idle" | "downloading" | "verifying" | "staging" | "installing" | "completed" | "failed" | "cancelled"</summary>
    public string Stage { get; set; } = "idle";
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public string? Error { get; set; }
    public string? Version { get; set; }
    public bool RolledBack { get; set; }
}

public sealed class PlayniteThemeLocateRequest { public string Path { get; set; } = string.Empty; }

public sealed class PlayniteThemeConsentRequest
{
    public bool Consent { get; set; }
}

public sealed class PlayniteThemeActionResultDto
{
    public bool Ok { get; set; }
    /// <summary>"selected" | "already-selected" | "restored" | "rolled-back" | "removed" | "refused"</summary>
    public string Outcome { get; set; } = "refused";
    public string? Message { get; set; }
    public string? BackupFile { get; set; }
}

public sealed class PlayniteThemeStatusDto
{
    public PlayniteThemeComponentDto Component { get; set; } = new();
    public PlayniteThemeInstallStatusDto Install { get; set; } = new();
}
