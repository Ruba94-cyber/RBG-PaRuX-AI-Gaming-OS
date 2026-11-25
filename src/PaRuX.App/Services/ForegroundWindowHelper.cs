using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// The documented way to bring a window forward when Windows' foreground lock would otherwise only flash the taskbar: restore or
    /// show it, grant foreground rights (AllowSetForegroundWindow), join the input queue of the current foreground window for the
    /// duration of SetForegroundWindow, then raise it. It never minimizes, moves or closes any other window, and callers must decide
    /// first whether taking the focus is allowed (never over a running game).
    /// </summary>
    internal static class ForegroundWindowHelper
    {
        private const int SW_SHOW = 5, SW_RESTORE = 9;
        private const uint ASFW_ANY = 0xFFFFFFFF;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint dwProcessId);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        /// <summary>True when the window is the foreground window afterwards.</summary>
        public static bool BringToForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
            try
            {
                ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);
                if (GetForegroundWindow() == hwnd) return true;
                try { AllowSetForegroundWindow(ASFW_ANY); } catch { /* best effort */ }
                IntPtr fg = GetForegroundWindow();
                uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
                uint me = GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
                try
                {
                    SetForegroundWindow(hwnd);
                    BringWindowToTop(hwnd);
                }
                finally { if (attached) AttachThreadInput(me, fgThread, false); }
                return GetForegroundWindow() == hwnd;
            }
            catch (Exception ex) { Log.Write("Foreground request failed: " + ex.GetType().Name); return false; }
        }

        /// <summary>
        /// Waits (bounded) for the main window of a process with this name and raises it. <paramref name="mayTakeFocus"/> is checked
        /// before every attempt, so a game that starts meanwhile is never disturbed.
        /// </summary>
        public static async Task<bool> BringProcessWindowForwardAsync(string processName, Func<bool> mayTakeFocus, TimeSpan timeout, CancellationToken ct = default)
        {
            DateTime end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
            {
                if (!mayTakeFocus()) return false;
                foreach (Process p in Process.GetProcessesByName(processName))
                {
                    using (p)
                    {
                        try
                        {
                            p.Refresh();
                            IntPtr hwnd = p.MainWindowHandle;
                            if (hwnd == IntPtr.Zero) continue;
                            try { AllowSetForegroundWindow((uint)p.Id); } catch { }
                            if (BringToForeground(hwnd)) return true;
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    }
                }
                try { await Task.Delay(400, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return false; }
            }
            return false;
        }
    }
}
