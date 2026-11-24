using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.ChatCode;

public sealed class ChatCodeSettings
{
    /// <summary>The owner can switch the tool off without removing the runtime.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>"Skip" in Detect -> Locate / Install / Skip: the tool is never offered and nothing is installed.</summary>
    public bool Skipped { get; set; }
}

public sealed record ChatCodeDetectedPython(string Path, string Ownership, bool Usable, string Reason);

public sealed record ChatCodeRuntimeStatus(
    /// <summary>ready | not-installed | installing | failed | skipped</summary>
    string State,
    /// <summary>PARUX_MANAGED when PaRuX installed the interpreter; NONE otherwise.</summary>
    string Ownership,
    string? InterpreterPath,
    string PythonVersion,
    string Stage,
    string? Error,
    IReadOnlyList<ChatCodeDetectedPython> Detected,
    ChatCodeSettings Settings);

/// <summary>
/// The Python runtime used by chat code execution, as a Runtime Manager style component: Detect -> Install with PaRuX /
/// Skip. Only an interpreter PaRuX itself installed under its managed toolchain is ever executed (PARUX_MANAGED): the
/// sandbox identity needs read access to the interpreter folder, and PaRuX never changes permissions of software the user
/// manages (USER_MANAGED) or the system installed (SYSTEM_DETECTED) - those are listed so the user knows why they are not used.
/// Installing is an explicit user action, bounded, cancellable, refused while Gaming, and never happens as a side effect.
/// </summary>
public sealed class ChatCodeRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly Func<string?> _probe;
    private readonly Func<CancellationToken, Task<string>> _installer;
    private readonly Func<bool> _gamingActive;
    private readonly Func<IReadOnlyList<ChatCodeDetectedPython>> _detector;
    private ChatCodeSettings _settings = new();
    private CancellationTokenSource? _install;
    private string _stage = string.Empty;
    private string? _error;

    public ChatCodeRuntime(string settingsPath, Func<string?> probe, Func<CancellationToken, Task<string>> installer,
        Func<bool> gamingActive, Func<IReadOnlyList<ChatCodeDetectedPython>>? detector = null)
    {
        _settingsPath = settingsPath;
        _probe = probe;
        _installer = installer;
        _gamingActive = gamingActive;
        _detector = detector ?? DetectOtherPythons;
        try
        {
            if (File.Exists(_settingsPath) && new FileInfo(_settingsPath).Length <= 16 * 1024)
                _settings = JsonSerializer.Deserialize<ChatCodeSettings>(File.ReadAllText(_settingsPath), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _settings = new(); }
    }

    public static ChatCodeRuntime CreateDefault(Func<bool> gamingActive) => new(
        PaRuXDataRoot.Combine("server", "chat-code.json"),
        () => ManagedPythonRuntime.TryGetInstalledPython(ManagedPythonRuntime.DefaultPythonVersion),
        ct => ManagedPythonRuntime.EnsurePythonAsync(ManagedPythonRuntime.DefaultPythonVersion, ct),
        gamingActive);

    public ChatCodeSettings Settings { get { lock (_gate) return new ChatCodeSettings { Enabled = _settings.Enabled, Skipped = _settings.Skipped }; } }

    /// <summary>The interpreter to execute, or null (not installed, skipped, or switched off).</summary>
    public string? UsableInterpreter()
    {
        lock (_gate) { if (_settings.Skipped || !_settings.Enabled) return null; }
        string? path = _probe();
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    /// <summary>True when the model should be offered the tool at all.</summary>
    public bool ToolAvailable => !_gamingActive() && UsableInterpreter() != null;

    public ChatCodeRuntimeStatus GetStatus()
    {
        string? path = _probe();
        bool present = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        lock (_gate)
        {
            string state = _install != null ? "installing"
                : _settings.Skipped ? "skipped"
                : present ? "ready"
                : _error != null ? "failed" : "not-installed";
            return new ChatCodeRuntimeStatus(state, present ? "PARUX_MANAGED" : "NONE", present ? path : null,
                ManagedPythonRuntime.DefaultPythonVersion, _stage, _error, present ? Array.Empty<ChatCodeDetectedPython>() : _detector(),
                new ChatCodeSettings { Enabled = _settings.Enabled, Skipped = _settings.Skipped });
        }
    }

    public bool SetEnabled(bool enabled) { lock (_gate) { _settings.Enabled = enabled; return Persist(); } }

    public bool SetSkipped(bool skipped) { lock (_gate) { _settings.Skipped = skipped; return Persist(); } }

    /// <summary>Starts the managed install. Returns a refusal text, or null when it started.</summary>
    public string? BeginInstall()
    {
        if (_gamingActive()) return "Gaming Mode is active, so the Python runtime cannot be installed now.";
        lock (_gate)
        {
            if (_install != null) return "The Python runtime is already being installed.";
            if (_settings.Skipped) return "Code execution was skipped. Un-skip it first.";
            string? existing = _probe();
            if (!string.IsNullOrWhiteSpace(existing) && File.Exists(existing)) return "The PaRuX-managed Python runtime is already installed.";
            _error = null;
            _stage = "Installing the PaRuX-managed Python runtime...";
            _install = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        }
        CancellationTokenSource cts;
        lock (_gate) cts = _install!;
        _ = Task.Run(async () =>
        {
            try
            {
                await _installer(cts.Token).ConfigureAwait(false);
                lock (_gate) { _stage = "Installed."; _error = null; }
            }
            catch (OperationCanceledException) { lock (_gate) { _stage = "Cancelled."; _error = null; } }
            catch (Exception ex) { lock (_gate) { _stage = "Failed."; _error = Bound(ex.Message); } }
            finally { lock (_gate) { _install = null; } cts.Dispose(); }
        });
        return null;
    }

    public void CancelInstall()
    {
        lock (_gate) { try { _install?.Cancel(); } catch (ObjectDisposedException) { } }
    }

    private bool Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            string temp = _settingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_settings, JsonOptions));
            File.Move(temp, _settingsPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string Bound(string text) => text.Length <= 400 ? text : text[..400] + "...";

    internal static IReadOnlyList<ChatCodeDetectedPython> DetectOtherPythons()
    {
        var found = new List<ChatCodeDetectedPython>();
        const string reason = "Not used: the sandbox cannot run it without changing the permissions of software PaRuX does not manage.";
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach ((string root, string ownership) in new[]
            {
                (Path.Combine(local, "Programs", "Python"), "USER_MANAGED"),
                (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)), "SYSTEM_DETECTED")
            })
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.EnumerateDirectories(root, "Python3*").Take(6))
                {
                    string exe = Path.Combine(dir, "python.exe");
                    if (File.Exists(exe)) found.Add(new ChatCodeDetectedPython(exe, ownership, false, reason));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return found;
    }
}
