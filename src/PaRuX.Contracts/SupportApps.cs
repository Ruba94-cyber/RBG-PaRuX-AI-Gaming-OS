using System.Text.Json.Serialization;

namespace PaRuX.Contracts;

/// <summary>
/// A helper program the owner wants running while Console is in use (for example a controller helper). PaRuX only STARTS the exe the
/// owner picked (or one it downloaded into its own folder after explicit consent); it never installs, edits or updates the owner's
/// own copy. Off by default.
/// </summary>
public sealed record SupportAppEntry(
    string Id,
    string Name,
    string Path,
    string? Args = null,
    string? Process = null,
    bool Enabled = true,
    bool StartWithConsole = true,
    bool StopWithConsole = false,
    bool Minimized = true,
    int TimeoutSeconds = SupportAppLimits.DefaultTimeoutSeconds,
    bool ReplaceIfStale = false,
    string? CatalogId = null,
    string? Version = null);

public static class SupportAppLimits
{
    public const int MaxApps = 16;
    public const int DefaultTimeoutSeconds = 15;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 120;
    public const int MaxFileBytes = 128 * 1024;
}

public sealed record SupportAppSaveRequest(bool Enabled, IReadOnlyList<SupportAppEntry>? Apps);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupportAppOutcome
{
    Started, AlreadyRunning, ReplacedStale, SkippedDisabled, SkippedMissing, SkippedInvalid, SkippedGamingActive, Failed, TimedOut, Stopped, NotStopped,
}

public sealed record SupportAppResult(string Id, string Name, SupportAppOutcome Outcome, string Reason, long ElapsedMs);

public sealed record SupportAppRow(SupportAppEntry Entry, ComponentOwnership Ownership, string? Problem, bool Running);

public sealed record SupportAppStatus(
    bool Enabled,
    IReadOnlyList<SupportAppRow> Apps,
    IReadOnlyList<SupportAppResult> LastRun,
    bool Damaged,
    int MaxApps,
    string ManagedFolder);

public sealed record SupportAppInstallRequest(string CatalogId, string? Version, bool Confirm);
public sealed record SupportAppRemoveRequest(string CatalogId);
public sealed record SupportAppActionResult(bool Ok, string Message, SupportAppStatus Status);

/// <summary>The official source of a downloadable support app: fixed https URL, exact version, pinned SHA-256 and size.</summary>
public sealed record SupportAppDownload(string Url, string Version, string Sha256, long SizeBytes, string ExeRelativePath, string SourcePage);

public sealed record SupportAppCatalogEntry(
    string Id,
    string Name,
    LogonText Purpose,
    string License,
    string ProjectUrl,
    SupportAppDownload? Download,
    LogonText? NoDownloadReason);

/// <summary>
/// Known helper programs. An entry has a download ONLY when its official source, version, SHA-256 and size could be confirmed; every
/// other entry is "Locate only": PaRuX shows it, never downloads it. Drivers and services that need administrator rights are not
/// listed as downloads at all (PaRuX never installs a driver).
/// </summary>
public static class SupportAppCatalog
{
    public static IReadOnlyList<SupportAppCatalogEntry> All { get; } = new SupportAppCatalogEntry[]
    {
        new("nyrna", "Nyrna",
            new("Suspends and resumes a running game from a hotkey or tray (portable build, no installer).", "Sospende e riprende un gioco in esecuzione da scorciatoia o tray (versione portatile, senza installer)."),
            "GPL-3.0", "https://github.com/Merrit/nyrna",
            new("https://github.com/Merrit/nyrna/releases/download/v2.27.2/Nyrna-Windows-Portable.zip", "2.27.2",
                "a3e609f3b80ab5b2d079f51f59fe6bb113a2003bafff3b32439a352000f9c7bc", 13972224, "nyrna.exe",
                "https://github.com/Merrit/nyrna/releases/tag/v2.27.2"),
            null),
        new("xoutput", "XOutput",
            new("Presents a controller as an Xbox pad to games that only know XInput.", "Presenta un controller come pad Xbox ai giochi che conoscono solo XInput."),
            "MIT", "https://github.com/csutorasa/XOutput", null,
            new("No publisher hash is published for the official release and it needs a separate driver, so PaRuX does not download it. Locate your own copy.",
                "Per la release ufficiale non è pubblicato alcun hash e serve un driver a parte, quindi PaRuX non lo scarica. Indica la tua copia.")),
        new("sunshine", "Sunshine",
            new("Streams the screen to a Moonlight client.", "Trasmette lo schermo a un client Moonlight."),
            "GPL-3.0", "https://github.com/LizardByte/Sunshine", null,
            new("It installs a Windows service with administrator rights, so PaRuX never installs it. Locate your own copy.",
                "Installa un servizio di Windows con diritti di amministratore, quindi PaRuX non lo installa mai. Indica la tua copia.")),
    };

    public static SupportAppCatalogEntry? Find(string? id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
}
