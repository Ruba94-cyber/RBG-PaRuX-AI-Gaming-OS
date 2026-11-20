using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace PaRuX.Contracts;

public enum GameShortcutOutcome
{
    Created,
    /// <summary>An identical PaRuX shortcut is already there; nothing was written.</summary>
    AlreadyExists,
    InvalidRequest,
    FolderUnavailable,
    Failed
}

public sealed record GameShortcutResult(GameShortcutOutcome Outcome, string? Path, string Message);

/// <summary>What a Windows shortcut (.lnk) points at.</summary>
public sealed record ShortcutTarget(string TargetPath, string Arguments, string WorkingDirectory, string Description, string IconPath, int IconIndex);

/// <summary>
/// Desktop shortcuts for library games. A shortcut starts <c>PaRuX.exe --play &lt;titleKey&gt;</c>, so the game still
/// starts through PaRuX (its confirmation, Gaming Mode preparation and play-time tracking), not around it.
///
/// A file that is already there is never overwritten: an identical PaRuX shortcut is left alone, and anything else
/// (a store-created shortcut, one the user edited) makes PaRuX pick the next free name instead.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GameShortcuts
{
    private const int MaxNameLength = 80;
    private const int MaxCandidates = 20;
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>A file-name-safe version of the game name; never empty.</summary>
    public static string SafeFileName(string? gameName)
    {
        var builder = new StringBuilder();
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in gameName ?? string.Empty)
        {
            if (char.IsControl(c) || Array.IndexOf(invalid, c) >= 0) builder.Append(' ');
            else builder.Append(c);
        }
        string name = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', ' ');
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd('.', ' ');
        if (name.Length == 0) name = "Game";
        if (ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) name += " (PaRuX)";
        return name;
    }

    /// <summary>
    /// Creates the shortcut in <paramref name="folder"/>. <paramref name="executablePath"/> is the PaRuX executable the
    /// shortcut starts; <paramref name="iconPath"/> is optional (the executable's own icon is used when it is empty or
    /// missing).
    /// </summary>
    public static GameShortcutResult CreateInFolder(
        string folder, string gameName, string titleKey, string executablePath, string? iconPath = null)
    {
        if (!GameLaunchRequestChannel.IsValidTitleKey(titleKey))
            return new GameShortcutResult(GameShortcutOutcome.InvalidRequest, null, "That game cannot be addressed by a shortcut.");
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
            return new GameShortcutResult(GameShortcutOutcome.InvalidRequest, null, "PaRuX could not find its own program file to point the shortcut at.");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new GameShortcutResult(GameShortcutOutcome.FolderUnavailable, null, "The shortcut folder does not exist.");

        string exe = Path.GetFullPath(executablePath);
        string icon = !string.IsNullOrWhiteSpace(iconPath) && Path.IsPathFullyQualified(iconPath) && File.Exists(iconPath) ? Path.GetFullPath(iconPath) : exe;
        var wanted = new ShortcutTarget(
            exe,
            GameLaunchRequestChannel.ArgumentsFor(titleKey),
            Path.GetDirectoryName(exe) ?? string.Empty,
            "Start " + Truncate(gameName, 120) + " with PaRuX",
            icon,
            0);

        string baseName = SafeFileName(gameName);
        for (int attempt = 0; attempt < MaxCandidates; attempt++)
        {
            string candidateName = attempt == 0 ? baseName
                : attempt == 1 ? baseName + " (PaRuX)"
                : baseName + " (PaRuX " + attempt + ")";
            string path = Path.Combine(folder, candidateName + ".lnk");
            try
            {
                if (File.Exists(path))
                {
                    ShortcutTarget? existing = WindowsShortcutFile.TryRead(path);
                    if (existing != null && SameCommand(existing, wanted))
                        return new GameShortcutResult(GameShortcutOutcome.AlreadyExists, path, "The shortcut already exists.");
                    continue; // someone else's or edited file: keep it, try the next name
                }
                if (Directory.Exists(path)) continue;
                WindowsShortcutFile.Create(path, wanted);
                return new GameShortcutResult(GameShortcutOutcome.Created, path, "Shortcut created.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            {
                return new GameShortcutResult(GameShortcutOutcome.Failed, null, "The shortcut could not be created: " + ex.Message);
            }
        }
        return new GameShortcutResult(GameShortcutOutcome.Failed, null, "Too many shortcuts with this name already exist in that folder.");
    }

    /// <summary>Creates the shortcut on the user's desktop.</summary>
    public static GameShortcutResult CreateOnDesktop(string gameName, string titleKey, string executablePath, string? iconPath = null) =>
        CreateInFolder(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), gameName, titleKey, executablePath, iconPath);

    private static bool SameCommand(ShortcutTarget a, ShortcutTarget b) =>
        string.Equals(a.TargetPath, b.TargetPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Arguments.Trim(), b.Arguments.Trim(), StringComparison.Ordinal);

    private static string Truncate(string? value, int max)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max];
    }
}

/// <summary>Minimal reader/writer for Windows shell links through the shell's own IShellLink.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsShortcutFile
{
    /// <summary>Writes a new shortcut. Throws <see cref="IOException"/> when the file already exists.</summary>
    public static void Create(string path, ShortcutTarget target)
    {
        if (File.Exists(path)) throw new IOException("The shortcut file already exists.");
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target.TargetPath);
            link.SetArguments(target.Arguments);
            link.SetWorkingDirectory(target.WorkingDirectory);
            link.SetDescription(target.Description);
            link.SetIconLocation(target.IconPath, target.IconIndex);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    /// <summary>Reads a shortcut, or null when the file is not a readable shell link.</summary>
    public static ShortcutTarget? TryRead(string path)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(1024);
            var arguments = new StringBuilder(2048);
            var directory = new StringBuilder(1024);
            var description = new StringBuilder(1024);
            var icon = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            link.GetArguments(arguments, arguments.Capacity);
            link.GetWorkingDirectory(directory, directory.Capacity);
            link.GetDescription(description, description.Capacity);
            link.GetIconLocation(icon, icon.Capacity, out int iconIndex);
            return new ShortcutTarget(target.ToString(), arguments.ToString(), directory.ToString(), description.ToString(), icon.ToString(), iconIndex);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
        finally { if (link != null) Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
