using System.Diagnostics;
using Microsoft.Win32;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Read-only machine probe for the three bundled-capable components. It never installs, starts, stops or edits anything. The
/// "installed by PaRuX" record lives under HKLM (admin-writable only) and is written by the setup's elevated helper.
/// </summary>
public sealed class WindowsRedistProbe : IRedistProbe
{
    public const string StateKey = @"SOFTWARE\Ruba94-cyber\PaRuX\Redist";

    public RedistProbeResult Probe(string redistId)
    {
        if (!OperatingSystem.IsWindows()) return new(false, null, null);
        try
        {
            switch (redistId)
            {
                case "vigembus":
                {
                    string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "ViGEmBus.sys");
                    bool present = ServiceExists("ViGEmBus") || File.Exists(sys);
                    return new(present, present && File.Exists(sys) ? sys : null, File.Exists(sys) ? FileVersion(sys) : null);
                }
                case "hidhide":
                {
                    string cli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions", "HidHide", "x64", "HidHideCLI.exe");
                    bool present = ServiceExists("HidHide") || File.Exists(cli);
                    return new(present, File.Exists(cli) ? cli : null, File.Exists(cli) ? FileVersion(cli) : null);
                }
                case "sunshine":
                {
                    string? exe = SunshineSessionTriggerProvider.ExecutablePath;
                    return exe is null ? new(false, null, null) : new(true, exe, FileVersion(exe));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return new(false, null, null);
    }

    public RedistSetupRecord? ReadSetupRecord(string redistId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = hklm.OpenSubKey(StateKey + "\\" + redistId);
            if (key is null) return null;
            return new RedistSetupRecord(
                key.GetValue("InstalledByPaRuX") is int i && i == 1,
                key.GetValue("Version") as string,
                key.GetValue("RebootRequired") is int r && r == 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    public bool BundledPayloadAvailable(string redistId)
    {
        SupportAppCatalogEntry? entry = SupportAppCatalog.Find(redistId);
        return entry is not null && SupportAppInstaller.DefaultBundledPackage(entry) is not null;
    }

    private static bool ServiceExists(string name)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return key is not null;
    }

    private static string? FileVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileVersion; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

public static class RedistDetector
{
    public static IReadOnlyList<RedistStatusRow> BuildRows(IRedistProbe probe, SupportAppListState list)
    {
        string root = SupportAppPolicy.ManagedRoot;
        var rows = new List<RedistStatusRow>();
        foreach (SupportAppCatalogEntry c in SupportAppCatalog.All.Where(e => e.RedistId is not null))
        {
            RedistProbeResult p = probe.Probe(c.RedistId!);
            RedistSetupRecord? setup = probe.ReadSetupRecord(c.RedistId!);
            bool managedFolder = p.Present && SupportAppPolicy.IsInside(p.Path, root);
            bool picked = p.Present && !managedFolder && list.Apps.Any(a => SupportAppPolicy.PathsMatch(a.Path, p.Path));
            rows.Add(RedistStatusPolicy.Decide(c.Id, c.Name, p, c.Kind == SupportAppKind.Driver ? setup : null, picked, managedFolder, probe.BundledPayloadAvailable(c.RedistId!)));
        }
        return rows;
    }
}
