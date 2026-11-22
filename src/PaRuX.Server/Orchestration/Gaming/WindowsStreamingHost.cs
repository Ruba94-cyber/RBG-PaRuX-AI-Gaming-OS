using System.Diagnostics;
using PaRuX.Server.Orchestration.Gaming.Providers;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Sunshine seam for Windows. Detects an installed / running Sunshine (read-only), starts the installed executable with no
/// arguments, and stops ONLY the process this instance started (pid + start-time must still match). It never installs
/// Sunshine, never touches its configuration, and never stops a Sunshine it did not start.
/// </summary>
internal sealed class WindowsStreamingHost : IStreamingHost
{
    internal static readonly TimeSpan StartSettle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);
    private readonly Func<string?> _findExecutable;
    private readonly Func<bool> _isRunning;
    private readonly object _gate = new();
    private Process? _owned;

    public WindowsStreamingHost() : this(() => SunshineSessionTriggerProvider.ExecutablePath, ScanRunning) { }

    internal WindowsStreamingHost(Func<string?> findExecutable, Func<bool> isRunning)
    {
        _findExecutable = findExecutable; _isRunning = isRunning;
    }

    public SunshineProbe ProbeSunshine()
    {
        bool running = _isRunning();
        return new SunshineProbe(running || _findExecutable() is not null, running);
    }

    // Gaming hard-off: the only caller is the streaming start route, which refuses while Gaming is active (_gamingActive
    // gate in StreamingEndpoints). Sunshine is a streaming host, not an AI child.
    public async Task<OwnedProcessHandle> StartSunshineAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Starting Sunshine requires a Windows desktop.");
        string exe = _findExecutable() ?? throw new InvalidOperationException("The Sunshine executable was not found.");
        lock (_gate)
        {
            if (_owned is { HasExited: false }) throw new InvalidOperationException("PaRuX already started a Sunshine process.");
        }
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty
        };
        Process process = Process.Start(info) ?? throw new InvalidOperationException("Windows did not start Sunshine.");
        try
        {
            await Task.Delay(StartSettle, ct).ConfigureAwait(false);
            if (process.HasExited) throw new InvalidOperationException("Sunshine exited right after starting (code " + process.ExitCode + ").");
            var handle = new OwnedProcessHandle(process.Id, process.StartTime.ToUniversalTime());
            lock (_gate) _owned = process;
            return handle;
        }
        catch
        {
            // Only our own just-started process is cleaned up here.
            try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
            throw;
        }
    }

    public async Task<bool> StopOwnedSunshineAsync(OwnedProcessHandle handle, CancellationToken ct)
    {
        Process? process;
        lock (_gate) process = _owned;
        if (process is null) return false; // not ours (e.g. handle from a previous run): never touch it
        try
        {
            if (process.Id != handle.Pid || Math.Abs((process.StartTime.ToUniversalTime() - handle.StartedUtc).TotalSeconds) > 2) return false;
            if (!process.HasExited)
            {
                process.Kill(true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ExitWait);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            lock (_gate) { if (ReferenceEquals(_owned, process)) _owned = null; }
            process.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return false;
        }
    }

    private static bool ScanRunning()
    {
        try
        {
            Process[] found = Process.GetProcessesByName("sunshine");
            bool any = found.Length > 0;
            foreach (Process p in found) p.Dispose();
            return any;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { return false; }
    }
}
