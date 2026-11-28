using System.Diagnostics;

namespace PaRuX.Server.Tunnel;

internal interface ITunnelProcess : IDisposable
{
    /// <summary>Completes with the exit code when the process ends for any reason.</summary>
    Task<int> Exited { get; }
    void Kill();
}

internal interface ITunnelProcessLauncher
{
    ITunnelProcess Start(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, Action<string> onLine);
}

/// <summary>Starts cloudflared bound to Core's kill-on-close job, so a crashed Core cannot leave a public tunnel behind.</summary>
internal sealed class TunnelProcessLauncher : ITunnelProcessLauncher
{
    public ITunnelProcess Start(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, Action<string> onLine)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        // The named-tunnel token travels in the environment: a command line is readable by every process on the machine.
        foreach (KeyValuePair<string, string> pair in environment) info.Environment[pair.Key] = pair.Value;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var wrapper = new RealProcess(process, onLine);
        process.Start();
        // Bound to the kill-on-close job before any output is read; if the job is unavailable the process is not trusted to
        // outlive nothing, so it is stopped rather than left unsupervised.
        if (!ChildProcessJob.Assign(process))
        {
            wrapper.Kill();
            wrapper.Dispose();
            throw new InvalidOperationException("PaRuX could not bind the tunnel process to its own lifetime, so it was not started.");
        }
        wrapper.BeginRead();
        return wrapper;
    }

    private sealed class RealProcess : ITunnelProcess
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RealProcess(Process process, Action<string> onLine)
        {
            _process = process;
            process.Exited += (_, _) =>
            {
                try { _exited.TrySetResult(process.ExitCode); } catch { _exited.TrySetResult(-1); }
            };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        }

        internal void BeginRead()
        {
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public Task<int> Exited => _exited.Task;

        public void Kill()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            Kill();
            try { _process.Dispose(); } catch { }
        }
    }
}
