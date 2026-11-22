namespace PaRuX.Contracts;

// ---- Optional components page: pure presentation/decision policy (no I/O, no network, no UI types). ----
// One list shared by Settings and the first-run setup step. It only decides what to SHOW and which buttons are legal;
// the actual download (https only, SHA-256, cancel, resume, disk check) stays in the Runtime Manager install service.

/// <summary>What the owner sees for an optional component.</summary>
public enum OptionalComponentState
{
    /// <summary>Installed by PaRuX, or located by the owner and working.</summary>
    Installed,
    /// <summary>Nothing found. May be skipped; may be downloaded later.</summary>
    NotInstalled,
    /// <summary>Found on this PC (PATH or a known folder) but PaRuX did not install it and does not own it.</summary>
    DetectedOnThisPc
}

public enum OptionalComponentButton
{
    /// <summary>PaRuX downloads and installs its own copy (PARUX_MANAGED only).</summary>
    DownloadNow,
    /// <summary>Point PaRuX at a copy the owner already has.</summary>
    LocateExisting,
    /// <summary>Decide later; nothing is installed.</summary>
    Skip,
    /// <summary>Undo a previous Skip.</summary>
    UndoSkip,
    /// <summary>Delete the copy PaRuX installed. Only ever offered for PARUX_MANAGED components.</summary>
    Remove,
    /// <summary>Make PaRuX forget a location the owner chose. Never deletes anything.</summary>
    ForgetLocation
}

public sealed record OptionalComponentCatalogEntry(
    string Id,
    string NameEn,
    string NameIt,
    string PurposeEn,
    string PurposeIt,
    /// <summary>Approximate download size. 0 means "shown before the download starts".</summary>
    long ApproxDownloadBytes,
    /// <summary>True when PaRuX has its own verified installer for this component.</summary>
    bool PaRuXCanDownload,
    /// <summary>True when the owner can point PaRuX at an existing copy.</summary>
    bool CanLocate,
    /// <summary>True when PaRuX may delete its own copy (reinstallable, holds no user data).</summary>
    bool RemovableWhenManaged,
    /// <summary>True when the component is provisioned silently-on-demand by a feature and has no skip/download choice.</summary>
    bool OnDemandOnly);

public sealed class OptionalComponentRow
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public OptionalComponentState State { get; init; }
    public string StateLabel { get; init; } = string.Empty;
    public bool Skipped { get; init; }
    public ComponentOwnership Ownership { get; init; }
    public string OwnershipLabel { get; init; } = string.Empty;
    public long ApproxDownloadBytes { get; init; }
    public string SizeLabel { get; init; } = string.Empty;
    public string? Path { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<OptionalComponentButton> Buttons { get; init; } = Array.Empty<OptionalComponentButton>();
    /// <summary>Why a Download button is missing (Store edition, offline-only edition) or other honest note; null when none.</summary>
    public string? Note { get; init; }
}

public readonly record struct OptionalComponentsContext(bool ExecutableDownloadsAllowed, bool Italian);

public static class OptionalComponentPolicy
{
    /// <summary>Optional components, in display order. Core engines (bundled CPU engine, embeddings) are not optional and are not listed.</summary>
    public static readonly IReadOnlyList<OptionalComponentCatalogEntry> Catalog = new[]
    {
        new OptionalComponentCatalogEntry("llama-server-vulkan",
            "Graphics engine (Vulkan)", "Motore grafico (Vulkan)",
            "Runs chat models on any modern graphics card. The built-in processor engine works without it, only slower.",
            "Esegue i modelli di chat su qualsiasi scheda grafica recente. Il motore integrato su processore funziona anche senza, solo più lento.",
            34_936_498, PaRuXCanDownload: true, CanLocate: false, RemovableWhenManaged: true, OnDemandOnly: false),
        new OptionalComponentCatalogEntry("llama-server-cuda",
            "NVIDIA engine (CUDA)", "Motore NVIDIA (CUDA)",
            "Faster chat on NVIDIA graphics cards. Only useful if this PC has one.",
            "Chat più veloce sulle schede grafiche NVIDIA. Serve solo se questo PC ne ha una.",
            583_596_026, PaRuXCanDownload: true, CanLocate: false, RemovableWhenManaged: true, OnDemandOnly: false),
        new OptionalComponentCatalogEntry("comfyui",
            "ComfyUI (images and video)", "ComfyUI (immagini e video)",
            "Lets Create make images and video locally. Skip it if you only want to chat.",
            "Permette a Crea di generare immagini e video in locale. Salta se vuoi solo conversare.",
            2_000_000_000, PaRuXCanDownload: true, CanLocate: true, RemovableWhenManaged: false, OnDemandOnly: false),
        new OptionalComponentCatalogEntry("ffmpeg",
            "FFmpeg", "FFmpeg",
            "Converts audio and video for voice input and video tools.",
            "Converte audio e video per l'ingresso vocale e gli strumenti video.",
            0, PaRuXCanDownload: false, CanLocate: true, RemovableWhenManaged: false, OnDemandOnly: false),
        new OptionalComponentCatalogEntry("git",
            "Git", "Git",
            "Lets the coding tools work with your repositories.",
            "Permette agli strumenti di programmazione di lavorare con i tuoi repository.",
            0, PaRuXCanDownload: false, CanLocate: true, RemovableWhenManaged: false, OnDemandOnly: false),
        new OptionalComponentCatalogEntry("node",
            "Node.js", "Node.js",
            "Needed only by some extensions and browser tools.",
            "Serve solo ad alcune estensioni e strumenti del browser.",
            0, PaRuXCanDownload: false, CanLocate: true, RemovableWhenManaged: false, OnDemandOnly: false),
        new OptionalComponentCatalogEntry("npm",
            "npm", "npm",
            "Package manager used together with Node.js.",
            "Gestore di pacchetti usato insieme a Node.js.",
            0, PaRuXCanDownload: false, CanLocate: true, RemovableWhenManaged: false, OnDemandOnly: false),
        new OptionalComponentCatalogEntry("uv",
            "Python tools (uv)", "Strumenti Python (uv)",
            "Prepared automatically the first time a voice, image or training feature needs Python. PaRuX never uses your own Python.",
            "Preparato automaticamente la prima volta che una funzione vocale, di immagini o di addestramento richiede Python. PaRuX non usa mai il tuo Python.",
            18_874_368, PaRuXCanDownload: false, CanLocate: false, RemovableWhenManaged: false, OnDemandOnly: true),
        new OptionalComponentCatalogEntry("python",
            "Python runtime", "Runtime Python",
            "Private Python used only by PaRuX features that need it. Installed on demand.",
            "Python privato usato solo dalle funzioni di PaRuX che lo richiedono. Installato su richiesta.",
            0, PaRuXCanDownload: false, CanLocate: false, RemovableWhenManaged: false, OnDemandOnly: true)
    };

    private static string Pick(bool italian, string en, string it) => italian ? it : en;

    public static string FormatSize(long bytes, bool italian)
    {
        if (bytes <= 0) return Pick(italian, "Size shown before download", "Dimensione mostrata prima del download");
        double mib = bytes / 1048576.0;
        string value = mib >= 1024
            ? (mib / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
            : Math.Max(1, Math.Round(mib)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        return Pick(italian, "About ", "Circa ") + value;
    }

    public static string FormatOwnership(ComponentOwnership ownership, bool italian) => ownership switch
    {
        ComponentOwnership.PARUX_MANAGED => Pick(italian, "Installed by PaRuX", "Installato da PaRuX"),
        ComponentOwnership.USER_MANAGED => Pick(italian, "Chosen by you", "Scelto da te"),
        ComponentOwnership.SYSTEM_DETECTED => Pick(italian, "Found on this PC", "Trovato su questo PC"),
        _ => Pick(italian, "Unknown", "Sconosciuto")
    };

    public static string FormatState(OptionalComponentState state, bool skipped, bool italian) => state switch
    {
        OptionalComponentState.Installed => Pick(italian, "Installed", "Installato"),
        OptionalComponentState.DetectedOnThisPc => Pick(italian, "Detected on this PC", "Rilevato su questo PC"),
        _ => skipped ? Pick(italian, "Not installed (skipped)", "Non installato (saltato)") : Pick(italian, "Not installed", "Non installato")
    };

    public static string ButtonLabel(OptionalComponentButton button, bool italian) => button switch
    {
        OptionalComponentButton.DownloadNow => Pick(italian, "Download now", "Scarica ora"),
        OptionalComponentButton.LocateExisting => Pick(italian, "Locate existing…", "Indica quello esistente…"),
        OptionalComponentButton.Skip => Pick(italian, "Skip", "Salta"),
        OptionalComponentButton.UndoSkip => Pick(italian, "Set up later", "Configura dopo"),
        OptionalComponentButton.Remove => Pick(italian, "Remove", "Rimuovi"),
        OptionalComponentButton.ForgetLocation => Pick(italian, "Forget location", "Dimentica la posizione"),
        _ => button.ToString()
    };

    /// <summary>Stable suffix for AutomationIds: Settings_OptionalComponent_{id}_{suffix}.</summary>
    public static string AutomationSuffix(OptionalComponentButton button) => button switch
    {
        OptionalComponentButton.DownloadNow => "DownloadButton",
        OptionalComponentButton.LocateExisting => "LocateButton",
        OptionalComponentButton.Skip => "SkipButton",
        OptionalComponentButton.UndoSkip => "UnskipButton",
        OptionalComponentButton.Remove => "RemoveButton",
        OptionalComponentButton.ForgetLocation => "ForgetLocationButton",
        _ => button + "Button"
    };

    public static OptionalComponentState ResolveState(ComponentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.Detected) return OptionalComponentState.NotInstalled;
        return status.Ownership == ComponentOwnership.SYSTEM_DETECTED
            ? OptionalComponentState.DetectedOnThisPc
            : OptionalComponentState.Installed;
    }

    /// <summary>The legal buttons for one component. Nothing here installs, deletes or changes anything; it only gates what is offered.</summary>
    public static IReadOnlyList<OptionalComponentButton> ButtonsFor(
        OptionalComponentCatalogEntry entry, ComponentStatus status, OptionalComponentsContext context)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(status);
        var buttons = new List<OptionalComponentButton>();
        if (entry.OnDemandOnly) return buttons;

        string[] actions = status.Actions ?? Array.Empty<string>();
        bool has(string a) => actions.Contains(a, StringComparer.OrdinalIgnoreCase);
        OptionalComponentState state = ResolveState(status);

        // Download: PaRuX's own copy only, never for something the owner or the system already provides.
        if (entry.PaRuXCanDownload && context.ExecutableDownloadsAllowed && has("install")
            && status.Ownership == ComponentOwnership.PARUX_MANAGED && state == OptionalComponentState.NotInstalled)
            buttons.Add(OptionalComponentButton.DownloadNow);

        if (entry.CanLocate && (has("locate") || has("relocate")))
            buttons.Add(OptionalComponentButton.LocateExisting);

        if (state == OptionalComponentState.NotInstalled && status.Ownership != ComponentOwnership.USER_MANAGED)
        {
            if (status.Skipped || has("unskip")) buttons.Add(OptionalComponentButton.UndoSkip);
            else if (has("skip")) buttons.Add(OptionalComponentButton.Skip);
        }

        // Remove deletes files, so it exists only for a PaRuX-managed copy that PaRuX may reinstall.
        if (entry.RemovableWhenManaged && status.Ownership == ComponentOwnership.PARUX_MANAGED && status.Detected)
            buttons.Add(OptionalComponentButton.Remove);

        // A location the owner chose can be forgotten; this never touches the files.
        if (status.Ownership == ComponentOwnership.USER_MANAGED && has("remove"))
            buttons.Add(OptionalComponentButton.ForgetLocation);

        return buttons;
    }

    /// <summary>Builds the rows of the Optional components page from the Runtime Manager inventory. Catalog order; unknown ids are not shown.</summary>
    public static IReadOnlyList<OptionalComponentRow> BuildRows(IReadOnlyList<ComponentStatus>? inventory, OptionalComponentsContext context)
    {
        var rows = new List<OptionalComponentRow>();
        if (inventory == null) return rows;
        bool it = context.Italian;
        foreach (OptionalComponentCatalogEntry entry in Catalog)
        {
            ComponentStatus? status = inventory.FirstOrDefault(c => string.Equals(c.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (status == null) continue;
            OptionalComponentState state = ResolveState(status);
            string? note = null;
            if (entry.PaRuXCanDownload && !context.ExecutableDownloadsAllowed && state == OptionalComponentState.NotInstalled)
                note = StoreEditionPolicy.ExecutableDownloadsReason;
            else if (entry.OnDemandOnly && state == OptionalComponentState.NotInstalled)
                note = Pick(it, "Nothing to do: it is prepared when a feature needs it.", "Nulla da fare: viene preparato quando una funzione lo richiede.");
            else if (state == OptionalComponentState.NotInstalled && entry.PaRuXCanDownload)
                note = Pick(it, "Needs an internet connection and is checked against a published SHA-256 before use. Nothing is downloaded until you press the button.",
                    "Richiede una connessione internet e viene verificato con uno SHA-256 pubblicato prima dell'uso. Non scarica nulla finché non premi il pulsante.");
            rows.Add(new OptionalComponentRow
            {
                Id = entry.Id,
                Name = it ? entry.NameIt : entry.NameEn,
                Purpose = it ? entry.PurposeIt : entry.PurposeEn,
                State = state,
                StateLabel = FormatState(state, status.Skipped, it),
                Skipped = status.Skipped && state == OptionalComponentState.NotInstalled,
                Ownership = status.Ownership,
                OwnershipLabel = FormatOwnership(status.Ownership, it),
                ApproxDownloadBytes = entry.ApproxDownloadBytes,
                SizeLabel = FormatSize(entry.ApproxDownloadBytes, it),
                Path = status.Path,
                Version = status.Version,
                Buttons = ButtonsFor(entry, status, context),
                Note = note
            });
        }
        return rows;
    }
}

/// <summary>Pre-flight for a "Download now" press. The install service re-checks everything; this only stops an obviously doomed start.</summary>
public static class OptionalComponentDownloadGate
{
    public const long HeadroomBytes = 256L * 1024 * 1024;

    public readonly record struct Decision(bool Allowed, string ReasonEn, string ReasonIt);

    /// <param name="downloadBytes">Size of the archive, or 0 when unknown.</param>
    /// <param name="freeBytes">Free space on the destination volume, or a negative number when it could not be read.</param>
    public static Decision Evaluate(long downloadBytes, long freeBytes, bool executableDownloadsAllowed)
    {
        if (!executableDownloadsAllowed)
            return new Decision(false, StoreEditionPolicy.ExecutableDownloadsReason, StoreEditionPolicy.ExecutableDownloadsReason);
        if (freeBytes < 0)
            return new Decision(false, "Free space could not be verified. Choose a writable location, then retry.",
                "Impossibile verificare lo spazio libero. Scegli una posizione scrivibile e riprova.");
        // Archive plus its extracted copy, plus room to keep the disk usable.
        long required = checked(Math.Max(0, downloadBytes) * 2 + HeadroomBytes);
        if (freeBytes < required)
            return new Decision(false,
                "Not enough free disk space: " + FormatMb(required) + " needed, " + FormatMb(freeBytes) + " available.",
                "Spazio su disco insufficiente: servono " + FormatMb(required) + ", disponibili " + FormatMb(freeBytes) + ".");
        return new Decision(true, string.Empty, string.Empty);
    }

    private static string FormatMb(long bytes) => (bytes / 1048576).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MB";
}

/// <summary>Decides whether PaRuX may delete its own installed copy of a component.</summary>
public static class ManagedComponentRemovalPolicy
{
    public readonly record struct Decision(bool Allowed, string Reason);

    public static Decision Evaluate(string componentId, ComponentOwnership ownership, bool installed, bool running,
        string? installFolder, string? managedRoot)
    {
        OptionalComponentCatalogEntry? entry = OptionalComponentPolicy.Catalog
            .FirstOrDefault(c => string.Equals(c.Id, componentId, StringComparison.OrdinalIgnoreCase));
        if (entry == null || !entry.RemovableWhenManaged)
            return new Decision(false, "PaRuX does not remove this component.");
        if (ownership != ComponentOwnership.PARUX_MANAGED)
            return new Decision(false, "Only components installed by PaRuX can be removed. Your own software is never changed.");
        if (!installed)
            return new Decision(false, "This component is not installed.");
        if (running)
            return new Decision(false, "Stop the engine first, then remove it.");
        if (string.IsNullOrWhiteSpace(installFolder) || string.IsNullOrWhiteSpace(managedRoot))
            return new Decision(false, "The install folder could not be determined.");
        string folder, root;
        try
        {
            folder = Path.GetFullPath(installFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            root = Path.GetFullPath(managedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new Decision(false, "The install folder path is not valid.");
        }
        // Must be a strict child of PaRuX's own runtime folder; a bundled copy beside the app is never deleted here.
        if (!folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new Decision(false, "This copy is bundled with the app, not downloaded, so it cannot be removed here.");
        return new Decision(true, string.Empty);
    }
}
