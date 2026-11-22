using System.Diagnostics;
using Microsoft.Win32;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers;

namespace PaRuX.Server.Orchestration.Gaming.Playnite;

internal sealed record PlayniteUninstallEntry(string DisplayName, string? InstallLocation, string? DisplayVersion, string? DisplayIcon);

internal sealed record PlayniteRunningProcess(int Pid, string ExePath);

/// <summary>Everything the locator reads from the machine. A fake makes detection unit-testable; the real one is read-only.</summary>
internal interface IPlayniteEnvironment
{
    string LocalAppData { get; }
    string AppData { get; }
    string ProgramFiles { get; }
    IEnumerable<PlayniteUninstallEntry> UninstallEntries();
    bool FileExists(string path);
    string? FileVersion(string path);
    IReadOnlyList<PlayniteRunningProcess> RunningPlaynite();
}

internal sealed class WindowsPlayniteEnvironment : IPlayniteEnvironment
{
    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public IEnumerable<PlayniteUninstallEntry> UninstallEntries()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        (RegistryHive Hive, RegistryView View)[] roots =
        {
            (RegistryHive.CurrentUser, RegistryView.Default),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32)
        };
        foreach ((RegistryHive hive, RegistryView view) in roots)
        {
            var found = new List<PlayniteUninstallEntry>();
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    if (!name.StartsWith("Playnite", StringComparison.OrdinalIgnoreCase)) continue;
                    using RegistryKey? key = uninstall.OpenSubKey(name);
                    string? display = key?.GetValue("DisplayName") as string;
                    if (display is null || !display.StartsWith("Playnite", StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add(new PlayniteUninstallEntry(display, key!.GetValue("InstallLocation") as string,
                        key.GetValue("DisplayVersion") as string, key.GetValue("DisplayIcon") as string));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            foreach (PlayniteUninstallEntry entry in found) yield return entry;
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public string? FileVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { return null; }
    }

    public IReadOnlyList<PlayniteRunningProcess> RunningPlaynite()
    {
        var list = new List<PlayniteRunningProcess>();
        foreach (string name in new[] { PlayniteLocator.DesktopName, PlayniteLocator.FullscreenName })
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        string? path = p.MainModule?.FileName;
                        if (path is not null) list.Add(new PlayniteRunningProcess(p.Id, path));
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                }
            }
        }
        return list;
    }
}

/// <summary>
/// Read-only detection of Playnite (installed, portable, or only seen running) with the Desktop and Fullscreen executables and the version.
/// It never writes, never starts anything and never reads Playnite's library or configuration.
/// </summary>
internal sealed class PlayniteLocator
{
    internal const string DesktopName = "Playnite.DesktopApp";
    internal const string FullscreenName = "Playnite.FullscreenApp";
    internal const string DesktopExeName = DesktopName + ".exe";
    internal const string FullscreenExeName = FullscreenName + ".exe";

    private readonly IPlayniteEnvironment _env;
    public PlayniteLocator(IPlayniteEnvironment? env = null) => _env = env ?? new WindowsPlayniteEnvironment();

    /// <summary>A folder is accepted only if it holds at least one of the two official executables.</summary>
    public PlayniteComponentDto? FromFolder(string? folder, string kind, string source, string? versionHint = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        string full;
        try { full = Path.GetFullPath(folder.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        string desktop = Path.Combine(full, DesktopExeName), fullscreen = Path.Combine(full, FullscreenExeName);
        bool hasDesktop = _env.FileExists(desktop) && CustomExecutableInputProvider.IsSafeHelperPath(desktop);
        bool hasFullscreen = _env.FileExists(fullscreen) && CustomExecutableInputProvider.IsSafeHelperPath(fullscreen);
        if (!hasDesktop && !hasFullscreen) return null;
        return new PlayniteComponentDto
        {
            Detected = true,
            InstallKind = kind,
            Source = source,
            Folder = full,
            DesktopExe = hasDesktop ? desktop : null,
            FullscreenExe = hasFullscreen ? fullscreen : null,
            Version = _env.FileVersion(hasDesktop ? desktop : fullscreen) ?? versionHint,
            Ownership = ComponentOwnership.SYSTEM_DETECTED
        };
    }

    /// <summary>Order: the owner's located folder, the uninstall registry, %LOCALAPPDATA%, %APPDATA%, Program Files, a running process.</summary>
    public PlayniteComponentDto Detect(string? locatedFolder = null)
    {
        PlayniteComponentDto? hit = FromFolder(locatedFolder, "located", "user-located");
        if (hit is not null) { hit.Ownership = ComponentOwnership.USER_MANAGED; return Finish(hit); }

        foreach (PlayniteUninstallEntry entry in _env.UninstallEntries())
        {
            string? folder = entry.InstallLocation;
            if (string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(entry.DisplayIcon))
            {
                try { folder = Path.GetDirectoryName(entry.DisplayIcon.Split(',')[0].Trim('"')); }
                catch (ArgumentException) { folder = null; }
            }
            hit = FromFolder(folder, "installed", "registry", entry.DisplayVersion);
            if (hit is not null) return Finish(hit);
        }

        (string Folder, string Source)[] known =
        {
            (Path.Combine(_env.LocalAppData, "Playnite"), "local-app-data"),
            (Path.Combine(_env.AppData, "Playnite"), "app-data"),
            (Path.Combine(_env.ProgramFiles, "Playnite"), "local-app-data")
        };
        foreach ((string folder, string source) in known)
        {
            hit = FromFolder(folder, "installed", source);
            if (hit is not null) return Finish(hit);
        }

        foreach (PlayniteRunningProcess running in _env.RunningPlaynite())
        {
            hit = FromFolder(Path.GetDirectoryName(running.ExePath), "running", "process");
            if (hit is not null) return Finish(hit);
        }
        return Finish(new PlayniteComponentDto());
    }

    private PlayniteComponentDto Finish(PlayniteComponentDto c)
    {
        IReadOnlyList<PlayniteRunningProcess> running = _env.RunningPlaynite();
        c.Running = c.Folder is not null
            ? running.Any(r => IsInFolder(r.ExePath, c.Folder))
            : running.Count > 0;
        if (c.Detected && c.InstallKind == "installed" && c.Folder is not null &&
            _env.FileExists(Path.Combine(c.Folder, "portable.dat")))
            c.InstallKind = "portable";
        c.Actions = c.Detected
            ? new[] { "launch-desktop", "launch-fullscreen", "locate", "skip" }
            : new[] { "locate", "install", "skip" };
        return c;
    }

    internal static bool IsInFolder(string exePath, string folder)
    {
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
            return string.Equals(dir.TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
