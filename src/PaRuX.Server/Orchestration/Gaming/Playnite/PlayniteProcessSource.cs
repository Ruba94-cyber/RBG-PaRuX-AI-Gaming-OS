using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaRuX.Server.Orchestration.Gaming.Playnite;

/// <summary>Read-only process snapshot for the session tracker. No handle with terminate/suspend rights is ever opened.</summary>
internal interface IProcessSnapshotSource
{
    IReadOnlyList<ProcEntry> Capture();
    ProcDetail? Identify(int pid);
}

internal sealed class WindowsProcessSnapshotSource : IProcessSnapshotSource
{
    private const uint Th32csSnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    public IReadOnlyList<ProcEntry> Capture()
    {
        var table = new List<ProcEntry>();
        if (!OperatingSystem.IsWindows()) return table;
        IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return table;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>(), szExeFile = string.Empty };
            if (!Process32FirstW(snap, ref entry)) return table;
            do { table.Add(new ProcEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile)); }
            while (Process32NextW(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return table;
    }

    public ProcDetail? Identify(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            if (p.HasExited) return null;
            string? path = null;
            try { path = p.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            return new ProcDetail(p.StartTime.ToUniversalTime(), path, p.MainWindowHandle != IntPtr.Zero);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { return null; }
    }
}
