using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaRuX.Server;

/// <summary>
/// Working set of one process without the whole-system snapshot. On Windows <see cref="Process.WorkingSet64"/>
/// enumerates every process on the machine (NtQuerySystemInformation) to read one number; the router's fit estimate
/// did that on every routed chat request (dotnet-trace, 2026-11-25: about 7 ms per call on the bench PC, more on a busy
/// one). <c>K32GetProcessMemoryInfo</c> reads the same counter from the process handle. Any failure falls back to the
/// framework property, so the value is never invented.
/// </summary>
internal static class ProcessWorkingSet
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);

    internal static long Read(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (K32GetProcessMemoryInfo(process.Handle, out ProcessMemoryCounters counters,
                        (uint)Marshal.SizeOf<ProcessMemoryCounters>()))
                    return (long)Math.Min((ulong)long.MaxValue, counters.WorkingSetSize);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                or NotSupportedException or EntryPointNotFoundException or DllNotFoundException) { }
        }
        process.Refresh();
        return process.WorkingSet64;
    }
}
