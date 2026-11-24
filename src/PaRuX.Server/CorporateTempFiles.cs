using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Scratch space for extracted attachments and backup staging. The standard editions keep using the OS temp folder.
/// Corporate is portable and offline: decrypted or extracted document content must not be left in the host's
/// shared %TEMP%, so it goes to a <c>tmp</c> folder under the PaRuXData root, which is swept when the process
/// first uses it (a crash or kill can leave residue that no <c>finally</c> removed).
/// </summary>
internal static class CorporateTempFiles
{
    internal const string FolderName = "tmp";

    private static readonly object Gate = new();
    private static string? _swept;

    /// <summary>The scratch root: PaRuXData\tmp in Corporate, the OS temp folder otherwise.</summary>
    internal static string Root
    {
        get
        {
            if (!CorporateMode.IsActive) return Path.GetTempPath();
            string root = ResolveRoot(true, PaRuXDataRoot.Path, Path.GetTempPath());
            lock (Gate)
            {
                // Sweep once per process and root, before the first caller creates anything in it.
                if (!string.Equals(_swept, root, StringComparison.OrdinalIgnoreCase))
                {
                    SweepResidue(root);
                    _swept = root;
                }
            }
            return root;
        }
    }

    /// <summary>Pure rule, testable without the process environment.</summary>
    internal static string ResolveRoot(bool corporate, string dataRoot, string systemTemp) =>
        corporate ? Path.Combine(dataRoot, FolderName) : systemTemp;

    /// <summary>Creates the folder and removes whatever an earlier run left in it. Best effort per entry.</summary>
    internal static void SweepResidue(string root)
    {
        try { Directory.CreateDirectory(root); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
                try { Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (string file in Directory.EnumerateFiles(root))
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Test seam: forget that the root was swept.</summary>
    internal static void ResetForTests() { lock (Gate) { _swept = null; } }
}
