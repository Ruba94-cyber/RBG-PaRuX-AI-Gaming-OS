namespace PaRuX.Contracts;

/// <summary>
/// Resolves the state directory used to coordinate PaRuX with its LocalAI runtimes.
/// </summary>
public static class PaRuXLocalAiStateRoot
{
    private const string OverrideEnvironmentVariable = "PARUX_LOCALAI_STATE_DIR";

    /// <summary>
    /// %LOCALAPPDATA%\LocalAI by default. A process-local absolute override is supported for
    /// isolated integration/UI-test child processes; normal launches never set it.
    /// </summary>
    public static string Path
    {
        get
        {
            // Polled by the Gaming guard several times per second; the portable-marker probe is a handful of file-system
            // calls, so the answer is reused for two seconds per (base directory, override) pair.
            string? overrideRoot = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
            string baseDirectory = AppContext.BaseDirectory;
            long now = Environment.TickCount64;
            Cached? c = _cached;
            if (c != null && now - c.At < 2000 && c.BaseDirectory == baseDirectory && c.Override == overrideRoot) return c.Value;
            string value = Resolve(baseDirectory, overrideRoot, PackageIdentity.FamilyName);
            _cached = new Cached(now, baseDirectory, overrideRoot, value);
            return value;
        }
    }

    private sealed record Cached(long At, string BaseDirectory, string? Override, string Value);
    private static volatile Cached? _cached;

    /// <summary>Portable marker wins over inherited process overrides; all
    /// coordination files stay inside the same movable PaRuXData directory.</summary>
    internal static string Resolve(string baseDirectory, string? overrideRoot, string? packageFamilyName = null)
    {
        string? portableData = PaRuXDataRoot.FindPortableDataRoot(baseDirectory);
        if (portableData != null) return portableData;
        if (!string.IsNullOrWhiteSpace(overrideRoot) && System.IO.Path.IsPathFullyQualified(overrideRoot))
            return System.IO.Path.GetFullPath(overrideRoot);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (PackageIdentity.IsValidFamilyName(packageFamilyName))
            return PackageIdentity.PackagedLocalAiStateRoot(localAppData, packageFamilyName!);
        return System.IO.Path.Combine(localAppData, "LocalAI");
    }

}
