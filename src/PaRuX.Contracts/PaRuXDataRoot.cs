namespace PaRuX.Contracts;

/// <summary>
/// The one place PaRuX's per-user data directory is defined.
///
/// Everything durable lives under it: conversations, memory, the RAG index, settings, paired-device
/// credentials, downloaded models and installed runtimes. Resolving it in one place is what keeps a
/// component from inventing its own location and stranding the user's data somewhere nothing else
/// reads.
/// </summary>
public static class PaRuXDataRoot
{
    private const string FolderName = "PaRuX";
    /// <summary>The standard Portable edition carries its data beside PaRuX.exe.</summary>
    public const string PortableMarkerFileName = "parux.portable";

    private static readonly object Gate = new();
    private static string? _resolved;

    /// <summary>
    /// %LOCALAPPDATA%\PaRuX by default. A process-local absolute override is supported for
    /// isolated integration/UI-test child processes; normal launches never set it.
    /// </summary>
    public static string Path
    {
        get
        {
            if (_resolved != null) return _resolved;
            lock (Gate)
            {
                _resolved ??= DefaultLocation();
                return _resolved;
            }
        }
    }

    public static string Combine(params string[] parts)
    {
        string[] all = new string[parts.Length + 1];
        all[0] = Path;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return System.IO.Path.Combine(all);
    }

    /// <summary>Test seam: forces the next <see cref="Path"/> access to resolve again.</summary>
    internal static void ResetForTests() { lock (Gate) { _resolved = null; } }

    /// <summary>Test seam: points the whole process at an isolated directory. The test suite sets
    /// this before anything else runs, because several suites exercise the real stores and would
    /// otherwise create and mutate files inside the developer&apos;s own installation.</summary>
    internal static void UseForTests(string directory)
    {
        lock (Gate) { _resolved = directory; }
    }

    /// <summary>The location computed without touching the disk, so the naming rule can be
    /// asserted directly.</summary>
    internal static string DefaultLocation() => ResolveDefaultLocation(
        AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PARUX_DATA_ROOT"), PackageIdentity.FamilyName);

    /// <summary>Portable marker wins over inherited/machine-wide environment
    /// overrides, which must not silently redirect a USB edition into some
    /// unrelated host user's profile. Pure with respect to process environment.</summary>
    internal static string ResolveDefaultLocation(string baseDirectory, string? overrideRoot, string? packageFamilyName = null)
    {
        string? portableRoot = FindPortableDataRoot(baseDirectory);
        if (portableRoot != null) return portableRoot;

        if (!string.IsNullOrWhiteSpace(overrideRoot) && System.IO.Path.IsPathFullyQualified(overrideRoot))
            return System.IO.Path.GetFullPath(overrideRoot);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Microsoft Store (MSIX) edition: use the real per-package folder so Desktop, Core and child
        // processes share one location and nothing lands in an unpackaged install's %LOCALAPPDATA%\PaRuX.
        if (PackageIdentity.IsValidFamilyName(packageFamilyName))
            return PackageIdentity.PackagedDataRoot(localAppData, packageFamilyName!);

        return System.IO.Path.Combine(localAppData, FolderName);
    }

    /// <summary>Folder name of the application payload inside the layered portable packages
    /// (PaRuX.exe launcher and PaRuXData sit one level above it).</summary>
    public const string PackageAppFolderName = "app";
    private const string DataFolderName = "PaRuXData";
    /// <summary>The Desktop runs from app\, the Core from app\PaRuX.Server\: the package root is
    /// at most this many levels above the running program.</summary>
    internal const int MaxPackageDepth = 3;

    /// <summary>The program folder followed by up to <see cref="MaxPackageDepth"/> ancestors, nearest first.</summary>
    internal static IEnumerable<string> PackageSearchDirectories(string baseDirectory)
    {
        string? current = System.IO.Path.GetFullPath(baseDirectory);
        for (int level = 0; level <= MaxPackageDepth && !string.IsNullOrEmpty(current); level++)
        {
            yield return current;
            string trimmed = current.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            current = System.IO.Path.GetDirectoryName(trimmed);
        }
    }

    /// <summary>Where the portable data folder sits relative to a directory that carries a marker.
    /// Legacy ZIPs and the MSI keep PaRuXData beside the marker; the layered package keeps the
    /// marker in <c>app\</c> and PaRuXData beside <c>app\</c> (the package root).</summary>
    private static IEnumerable<string> DataFolderCandidates(string markerDirectory)
    {
        yield return System.IO.Path.Combine(markerDirectory, DataFolderName);
        string trimmed = markerDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (string.Equals(System.IO.Path.GetFileName(trimmed), PackageAppFolderName, StringComparison.OrdinalIgnoreCase) &&
            System.IO.Path.GetDirectoryName(trimmed) is { Length: > 0 } packageRoot)
            yield return System.IO.Path.Combine(packageRoot, DataFolderName);
    }

    /// <summary>The program that startup entries, shortcuts and Jump Lists must start. In the layered portable
    /// packages the running application is <c>app\PaRuX.exe</c> but the visible entry point is the launcher at the
    /// package root (<c>PaRuX.exe</c>, beside <c>app\</c>): pointing at it keeps the Job Object, the working
    /// directory and the root layout intact. Anywhere else (MSI, MSIX, development) it is the running program itself.</summary>
    public static string? LaunchTargetFor(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return processPath;
        try
        {
            string full = System.IO.Path.GetFullPath(processPath);
            string? appDirectory = System.IO.Path.GetDirectoryName(full);
            if (appDirectory == null ||
                !string.Equals(System.IO.Path.GetFileName(appDirectory), PackageAppFolderName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(System.IO.Path.GetFileName(full), "PaRuX.exe", StringComparison.OrdinalIgnoreCase))
                return processPath;
            bool layered = System.IO.File.Exists(System.IO.Path.Combine(appDirectory, PortableMarkerFileName)) ||
                           System.IO.File.Exists(System.IO.Path.Combine(appDirectory, CorporateMode.MarkerFileName));
            string? packageRoot = System.IO.Path.GetDirectoryName(appDirectory);
            if (!layered || packageRoot == null) return processPath;
            string launcher = System.IO.Path.Combine(packageRoot, "PaRuX.exe");
            return System.IO.File.Exists(launcher) ? launcher : processPath;
        }
        catch (Exception) { return processPath; }
    }

    /// <summary>Pure directory boundary for tests and normal executable/server startup.
    /// The marker must sit adjacent to a real PaRuXData directory (or, in the layered package,
    /// in <c>app\</c> with PaRuXData beside <c>app\</c>). Environment-only
    /// corporate UI tests do not accidentally inherit a sibling directory.</summary>
    /// <summary>Recognizes a physically extracted Standard/Corporate ZIP, from
    /// the Desktop exe folder or the nested PaRuX.Server Core directory, searching up to three
    /// levels (Core in app\PaRuX.Server\ reaches the package root). A marker
    /// alone is insufficient: PaRuXData must also exist (Corporate creates it, see below).</summary>
    public static string? FindPortableDataRoot(string baseDirectory)
    {
        try
        {
            foreach (string root in PackageSearchDirectories(baseDirectory))
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(root, CorporateMode.MarkerFileName)) &&
                    !DataFolderCandidates(root).Any(System.IO.Directory.Exists))
                {
                    // Corporate invariant: data never leaves the package folder. A fresh ZIP has no
                    // PaRuXData yet; create it where the marker says it belongs, and refuse to start
                    // (no %LOCALAPPDATA% fallback) if that is impossible.
                    string corporateData = DataFolderCandidates(root).Last();
                    try { System.IO.Directory.CreateDirectory(corporateData); }
                    catch (Exception ex)
                    {
                        throw new CorporateDataRootUnavailableException(corporateData, ex);
                    }
                }
                if (System.IO.File.Exists(System.IO.Path.Combine(root, PortableMarkerFileName)) ||
                    System.IO.File.Exists(System.IO.Path.Combine(root, CorporateMode.MarkerFileName)))
                {
                    foreach (string data in DataFolderCandidates(root))
                        if (System.IO.Directory.Exists(data)) return data;
                }
            }
        }
        catch (CorporateDataRootUnavailableException) { throw; }
        catch (Exception) { /* standard MSI/profile fallback */ }
        return null;
    }

    internal static string? FindCorporatePortableDataRoot(string baseDirectory)
    {
        try
        {
            foreach (string root in PackageSearchDirectories(baseDirectory))
            {
                if (!System.IO.File.Exists(System.IO.Path.Combine(root, CorporateMode.MarkerFileName))) continue;
                foreach (string data in DataFolderCandidates(root))
                    if (System.IO.Directory.Exists(data)) return data;
            }
        }
        catch (Exception) { /* ordinary local profile fallback */ }
        return null;
    }
}

/// <summary>Corporate package whose own PaRuXData folder cannot be created: startup is refused rather
/// than silently storing data in the user profile.</summary>
public sealed class CorporateDataRootUnavailableException(string folder, Exception inner)
    : IOException("The Corporate data folder \"" + folder + "\" cannot be created: " + inner.Message
        + " Corporate data must stay beside PaRuX; move the folder to a location you can write to.", inner)
{
    public string Folder { get; } = folder;
}
