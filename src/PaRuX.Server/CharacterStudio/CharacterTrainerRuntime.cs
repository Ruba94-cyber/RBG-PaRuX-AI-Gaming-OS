using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.CharacterStudio;

/// <summary>Where the trainer lives and who owns it. Never mutated when the user owns it.</summary>
internal sealed record CharacterTrainerLaunch(string Python, string RunScript, string WorkingDirectory, string Ownership);

/// <summary>
/// The image-LoRA trainer component (ostris/ai-toolkit, MIT) with PaRuX's Detect, Locate, Install, Skip ownership:
/// PARUX_MANAGED installs a hash-pinned source archive and a hash-locked Python closure into PaRuX's own folder;
/// USER_MANAGED points at a checkout and Python the user already has and is never written to; Skip records the choice.
/// </summary>
internal sealed class CharacterTrainerRuntime
{
    internal const string Commit = "89680b0e84a6aa0540d44b93025056b128369e54";
    internal const string ArchiveUrl = "https://github.com/ostris/ai-toolkit/archive/" + Commit + ".zip";
    internal const string ArchiveSha256 = "2dd3cec75057ad96da2e7f80b1877b14859106d293f469789b85d9fc7963e33a";
    internal const long ArchiveBytes = 36_079_262;
    internal const string LockSetId = "character-trainer";
    internal const string Managed = "PARUX_MANAGED", UserManaged = "USER_MANAGED", None = "NONE";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private readonly string _root;
    private readonly LocalRuntimeService _runtime;

    public CharacterTrainerRuntime(LocalRuntimeService runtime) : this(runtime, Path.Combine(LocalAiPaths.RuntimeRoot, "character-trainer")) { }
    internal CharacterTrainerRuntime(LocalRuntimeService? runtime, string root) { _runtime = runtime!; _root = root; }

    internal string AppDir => Path.Combine(_root, "app");
    internal string VenvDir => Path.Combine(_root, "venv");
    private string ManifestPath => Path.Combine(_root, "manifest.json");
    private string ChoicePath => Path.Combine(_root, "choice.json");
    internal bool Installing { get; private set; }

    private sealed class Manifest
    {
        public string Commit { get; set; } = string.Empty;
        public string ArchiveSha256 { get; set; } = string.Empty;
        public string Ownership { get; set; } = Managed;
        public string TrainerFolder { get; set; } = string.Empty;
        public string Python { get; set; } = string.Empty;
        public string Probe { get; set; } = string.Empty;
    }

    private sealed class Choice { public bool Skipped { get; set; } }

    private Manifest? Read()
    {
        try { return File.Exists(ManifestPath) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private void Write(Manifest m)
    {
        Directory.CreateDirectory(_root);
        string tmp = ManifestPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(m));
        File.Move(tmp, ManifestPath, true);
    }

    /// <summary>Detect: what is installed or located right now, from disk only (no process is started).</summary>
    internal CharacterTrainerStatus GetStatus()
    {
        var status = new CharacterTrainerStatus { Installing = Installing };
        Manifest? m = Read();
        if (m == null)
        {
            bool skipped = false;
            try { skipped = File.Exists(ChoicePath) && JsonSerializer.Deserialize<Choice>(File.ReadAllText(ChoicePath))?.Skipped == true; } catch (Exception ex) when (ex is IOException or JsonException) { }
            status.State = skipped ? "skipped" : "not_installed";
            status.Ownership = None;
            status.Detail = skipped
                ? "You chose to skip the training engine. Pictures can still be made; training needs it."
                : "The training engine is not set up yet. It is a one-time download of a few GB.";
            return status;
        }
        status.Ownership = m.Ownership;
        status.Version = m.Ownership == Managed ? m.Commit[..12] : null;
        CharacterTrainerLaunch? launch = Resolve(m);
        status.State = launch != null && (m.Ownership == UserManaged || m.Probe.Length > 0) ? "ready" : "broken";
        status.Detail = status.State == "ready"
            ? (m.Ownership == Managed ? "Training engine ready (installed by PaRuX)." : "Using your own training engine; PaRuX will not change it.")
            : "The training engine files are missing or damaged. Repair it to continue.";
        return status;
    }

    private CharacterTrainerLaunch? Resolve(Manifest m)
    {
        if (m.Ownership == UserManaged)
        {
            string run = Path.Combine(m.TrainerFolder, "run.py");
            return File.Exists(run) && File.Exists(m.Python) ? new CharacterTrainerLaunch(m.Python, run, m.TrainerFolder, UserManaged) : null;
        }
        string dir = Path.Combine(AppDir, "ai-toolkit-" + m.Commit);
        string runPy = Path.Combine(dir, "run.py");
        string py = ManagedPythonRuntime.VenvPython(VenvDir);
        return File.Exists(runPy) && File.Exists(py) ? new CharacterTrainerLaunch(py, runPy, dir, Managed) : null;
    }

    internal CharacterTrainerLaunch? TryGetLaunch()
    {
        Manifest? m = Read();
        return m == null ? null : Resolve(m);
    }

    /// <summary>Locate: adopt an existing checkout and Python. Read-only for PaRuX.</summary>
    internal CharacterTrainerStatus Locate(string trainerFolder, string pythonExe)
    {
        string folder = Path.GetFullPath(trainerFolder ?? string.Empty);
        string py = Path.GetFullPath(pythonExe ?? string.Empty);
        if (!File.Exists(Path.Combine(folder, "run.py")) || !Directory.Exists(Path.Combine(folder, "toolkit")))
            throw new InvalidDataException("That folder does not look like a training engine (run.py is missing).");
        if (!File.Exists(py) || !string.Equals(Path.GetFileName(py), "python.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose the python.exe of the environment that runs that training engine.");
        Write(new Manifest { Ownership = UserManaged, TrainerFolder = folder, Python = py, Commit = "user" , Probe = "user"});
        return GetStatus();
    }

    internal CharacterTrainerStatus Skip()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(ChoicePath, JsonSerializer.Serialize(new Choice { Skipped = true }));
        return GetStatus();
    }

    /// <summary>Install with PaRuX. Pinned archive plus hash-locked packages only; refused during Gaming.</summary>
    internal async Task<CharacterTrainerStatus> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (_runtime?.GamingMode == true) throw new UnavailableDuringGamingException("Gaming Mode is active, so the training engine cannot be installed right now.");
        Manifest? existing = Read();
        if (existing is { Ownership: UserManaged })
            throw new InvalidOperationException("You are using your own training engine; PaRuX will not install over it. Remove that choice first.");
        await InstallGate.WaitAsync(ct).ConfigureAwait(false);
        Installing = true;
        try
        {
            Directory.CreateDirectory(_root);
            progress?.Report("Downloading the training engine source...");
            string zip = Path.Combine(_root, "source.zip");
            await VerifiedDownload.DownloadAsync(Http, ArchiveUrl, zip, ArchiveSha256, ArchiveBytes, ct).ConfigureAwait(false);
            string staging = Path.Combine(_root, "app.staging");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            await SafeArchiveExtractor.ExtractAsync(zip, staging, ct, ownedRoot: _root).ConfigureAwait(false);
            if (Directory.Exists(AppDir)) Directory.Delete(AppDir, true);
            Directory.Move(staging, AppDir);
            File.Delete(zip);

            progress?.Report("Preparing PaRuX-managed Python...");
            await ManagedPythonRuntime.EnsureVenvAsync(VenvDir, ManagedPythonRuntime.DefaultPythonVersion, ct).ConfigureAwait(false);

            progress?.Report("Installing the pinned GPU libraries (large download)...");
            TorchInstallChannelPlan plan = TorchInstallChannelPolicy.Select(await TorchInstallHardwareDetector.DetectAsync(ct).ConfigureAwait(false));
            if (plan.Channel != TorchInstallChannel.Cuda128)
                throw new TrainingRuntimeRefusalException("hardware_unsupported", "Training a character needs an NVIDIA graphics card. " + plan.Reason);
            await TrainingRuntimeLock.InstallAsync(TrainingRuntimeLock.TorchLockPath(plan.Channel),
                args => ManagedPythonRuntime.PipInstallAsync(VenvDir, args, ct, TimeSpan.FromMinutes(40)),
                TrainingRuntimeLock.TorchIndexUrl(plan.Channel)).ConfigureAwait(false);

            progress?.Report("Installing the training packages...");
            await ManagedPythonRuntime.PipInstallAsync(VenvDir, ManagedRequirementLocks.ArgumentsFor(LockSetId), ct, TimeSpan.FromMinutes(40)).ConfigureAwait(false);

            progress?.Report("Checking the engine...");
            string probe = await ProbeAsync(ManagedPythonRuntime.VenvPython(VenvDir), Path.Combine(AppDir, "ai-toolkit-" + Commit), ct).ConfigureAwait(false);
            Write(new Manifest { Commit = Commit, ArchiveSha256 = ArchiveSha256, Ownership = Managed, Probe = probe });
            if (File.Exists(ChoicePath)) File.Delete(ChoicePath);
            return GetStatus();
        }
        finally { Installing = false; InstallGate.Release(); }
    }

    internal static async Task<string> ProbeAsync(string python, string workingDir, CancellationToken ct)
    {
        const string code = "import torch,diffusers;from diffusers import ZImagePipeline;print('ok',torch.__version__,torch.cuda.is_available(),diffusers.__version__)";
        var psi = new ProcessStartInfo(python) { WorkingDirectory = workingDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-I"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(code);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("The Python probe did not start.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(6));
        string output = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
        string error = await p.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
        await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        if (p.ExitCode != 0 || !output.StartsWith("ok", StringComparison.Ordinal))
            throw new InvalidOperationException("The training engine did not pass its self-check: " + ManagedPythonRuntime.Trim(error));
        if (!output.Contains("True", StringComparison.Ordinal))
            throw new TrainingRuntimeRefusalException("hardware_unsupported", "The GPU libraries installed but no usable NVIDIA GPU was found.");
        return output.Trim();
    }
}
