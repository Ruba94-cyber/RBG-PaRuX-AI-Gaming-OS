using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaRuX.Server.Orchestration.Gaming.Playnite;

/// <summary>Read-only process snapshot for the session tracker. No handle with terminate/suspend rights is ever opened.</summary>
internal interface IProcessSnapshotSource
{
    IReadOnlyList<ProcEntry> Capture();
    ProcDetail? Identify(int pid);

    /// <summary>True when any process image (with or without ".exe") is one of <paramref name="baseNames"/>. The idle watcher only needs
    /// this until a Playnite process exists; the default goes through <see cref="Capture"/>, the Windows source does it without
    /// building a table.</summary>
    bool AnyProcessNamed(HashSet<string> baseNames) =>
        Capture().Any(e => baseNames.Contains(PlayniteGameSessionTracker.BaseName(e.Name)));
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

    // ---- Idle probe: "is any Playnite process alive?" without a process table ----
    // A full Toolhelp snapshot costs real CPU (it copies every process and thread). While nothing is going on the watcher only needs
    // to notice that Playnite appeared, and Playnite (Desktop and Fullscreen) always owns a top-level window, visible or in the tray.
    // So the cheap sweep enumerates top-level windows, maps each distinct window-owning process id to its image name ONCE (cached),
    // and a full allocation-free snapshot still runs every FullSweepInterval as a safety net for a Playnite without a window.
    internal static readonly TimeSpan FullSweepInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NameCacheLifetime = TimeSpan.FromSeconds(30);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] buffer, ref uint size);

    private readonly object _probeGate = new();
    private readonly HashSet<uint> _windowPids = new();
    private readonly Dictionary<uint, (string BaseName, long AtMs)> _nameCache = new();
    private readonly EnumWindowsProc _collect;
    private long _lastFullSweepMs = long.MinValue;
    private readonly char[] _pathBuffer = new char[1024];

    public WindowsProcessSnapshotSource() => _collect = (hWnd, _) =>
    {
        if (GetWindowThreadProcessId(hWnd, out uint pid) != 0 && pid != 0) _windowPids.Add(pid);
        return true;
    };

    public bool AnyProcessNamed(HashSet<string> baseNames)
    {
        if (!OperatingSystem.IsWindows()) return false;
        lock (_probeGate)
        {
            long now = Environment.TickCount64;
            if (_lastFullSweepMs == long.MinValue || now - _lastFullSweepMs >= (long)FullSweepInterval.TotalMilliseconds)
            {
                _lastFullSweepMs = now;
                return FullSweep(baseNames);
            }
            return WindowSweep(baseNames, now);
        }
    }

    private bool WindowSweep(HashSet<string> baseNames, long now)
    {
        _windowPids.Clear();
        if (!EnumWindows(_collect, IntPtr.Zero)) return true;   // cannot tell: let the caller do a full sample
        foreach (uint stale in _nameCache.Where(kv => now - kv.Value.AtMs > (long)NameCacheLifetime.TotalMilliseconds).Select(kv => kv.Key).ToList())
            _nameCache.Remove(stale);
        foreach (uint pid in _windowPids)
        {
            if (!_nameCache.TryGetValue(pid, out var cached))
            {
                cached = (ImageBaseName(pid), now);
                _nameCache[pid] = cached;
            }
            if (cached.BaseName.Length > 0 && baseNames.Contains(cached.BaseName)) return true;
        }
        return false;
    }

    /// <summary>Image name without folder and extension; empty when the process cannot be queried (then it is not a user-mode Playnite).</summary>
    private string ImageBaseName(uint pid)
    {
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return string.Empty;
        try
        {
            uint size = (uint)_pathBuffer.Length;
            if (!QueryFullProcessImageNameW(handle, 0, _pathBuffer, ref size) || size == 0) return string.Empty;
            return Path.GetFileNameWithoutExtension(new string(_pathBuffer, 0, (int)size));
        }
        finally { CloseHandle(handle); }
    }

    private static bool FullSweep(HashSet<string> baseNames)
    {
        IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return true;   // cannot tell: let the caller do a full sample
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>(), szExeFile = string.Empty };
            if (!Process32FirstW(snap, ref entry)) return true;
            do { if (baseNames.Contains(PlayniteGameSessionTracker.BaseName(entry.szExeFile))) return true; }
            while (Process32NextW(snap, ref entry));
            return false;
        }
        finally { CloseHandle(snap); }
    }

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
