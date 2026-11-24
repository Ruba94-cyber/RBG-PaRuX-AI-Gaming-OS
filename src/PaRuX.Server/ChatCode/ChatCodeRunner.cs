using System.Diagnostics;
using System.Text;

namespace PaRuX.Server.ChatCode;

/// <summary>
/// Runs model- or user-written Python in a throw-away folder and collects what it produced. This class owns the
/// policy (limits, folder layout, attachments, artifact filtering, cleanup, honest status); the operating-system
/// enforcement (AppContainer identity without network capability, Job Object limits, process-tree kill) lives behind
/// <see cref="IChatCodeProcessLauncher"/>. There is deliberately no unsandboxed fallback: when the launcher cannot
/// establish the sandbox the code is not run.
/// </summary>
public sealed class ChatCodeRunner
{
    internal const string ScriptName = "main.py";
    internal const string OutputFolder = "out";
    internal const string InputFolder = "in";

    private static readonly Dictionary<string, (string Mime, string Kind, bool Base64)> ArtifactTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = ("image/png", "image", true),
        [".jpg"] = ("image/jpeg", "image", true),
        [".jpeg"] = ("image/jpeg", "image", true),
        [".gif"] = ("image/gif", "image", true),
        [".webp"] = ("image/webp", "image", true),
        [".svg"] = ("image/svg+xml", "image", true),   // shown through <img> only: scripts in an SVG never run there
        [".html"] = ("text/html", "html", false),
        [".htm"] = ("text/html", "html", false),
        [".csv"] = ("text/csv", "text", false),
        [".txt"] = ("text/plain", "text", false),
        [".md"] = ("text/markdown", "text", false),
        [".json"] = ("application/json", "text", false),
        [".log"] = ("text/plain", "text", false)
    };

    private readonly IChatCodeProcessLauncher _launcher;
    private readonly Func<string?> _interpreter;
    private readonly Func<bool> _gamingActive;
    private readonly string _runsRoot;

    public ChatCodeRunner(IChatCodeProcessLauncher launcher, Func<string?> interpreter, Func<bool> gamingActive, string runsRoot)
    {
        _launcher = launcher;
        _interpreter = interpreter;
        _gamingActive = gamingActive;
        _runsRoot = Path.GetFullPath(runsRoot);
    }

    public string RunsRoot => _runsRoot;

    public async Task<ChatCodeResult> RunAsync(ChatCodeRequest request, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        ChatCodeResult Done(ChatCodeStatus status, string message, int? exit = null, string output = "", bool truncated = false,
            IReadOnlyList<ChatCodeArtifact>? artifacts = null, IReadOnlyList<string>? skipped = null) =>
            new(status, exit, output, truncated, artifacts ?? Array.Empty<ChatCodeArtifact>(), skipped ?? Array.Empty<string>(),
                message, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        ArgumentNullException.ThrowIfNull(request);
        string code = request.Code ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code)) return Done(ChatCodeStatus.Rejected, "No code was provided.");
        if (code.Length > ChatCodeLimits.MaxCodeCharacters)
            return Done(ChatCodeStatus.Rejected, "The code is longer than " + ChatCodeLimits.MaxCodeCharacters + " characters.");
        if (code.Contains('\0')) return Done(ChatCodeStatus.Rejected, "The code contains a NUL character.");

        ChatCodeLimits limits = (request.Limits ?? new ChatCodeLimits()).Clamped();
        if (_gamingActive())
            return Done(ChatCodeStatus.BlockedGaming, "Gaming Mode is active, so code execution is unavailable.");

        string? python = _interpreter();
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
            return Done(ChatCodeStatus.BlockedNeedsRuntime,
                "No PaRuX-managed Python runtime is installed. The user can install it from Settings > Code execution; PaRuX never installs it silently.");

        string? inputError = ValidateInputs(request.Inputs, limits);
        if (inputError != null) return Done(ChatCodeStatus.Rejected, inputError);

        string runDir = Path.Combine(_runsRoot, "run-" + Guid.NewGuid().ToString("N")[..16]);
        string work = Path.Combine(runDir, "work");
        string outDir = Path.Combine(work, OutputFolder);
        try
        {
            Directory.CreateDirectory(_runsRoot);
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(Path.Combine(work, "tmp"));
            if (request.Inputs is { Count: > 0 })
            {
                string inDir = Path.Combine(work, InputFolder);
                Directory.CreateDirectory(inDir);
                foreach (ChatCodeInputFile input in request.Inputs)
                    await File.WriteAllBytesAsync(Path.Combine(inDir, input.Name), input.Content, ct).ConfigureAwait(false);
            }
            await File.WriteAllTextAsync(Path.Combine(work, ScriptName), code, new UTF8Encoding(false), ct).ConfigureAwait(false);

            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PYTHONIOENCODING"] = "utf-8",
                ["PYTHONUTF8"] = "1",
                ["PYTHONDONTWRITEBYTECODE"] = "1",
                ["TEMP"] = Path.Combine(work, "tmp"),
                ["TMP"] = Path.Combine(work, "tmp"),
                ["HOME"] = work,
                ["USERPROFILE"] = work,
                ["PARUX_OUT"] = OutputFolder,
                ["PARUX_IN"] = InputFolder,
                ["MPLBACKEND"] = "Agg",
                ["MPLCONFIGDIR"] = Path.Combine(work, "tmp"),
                ["NO_PROXY"] = "*"
            };

            // -I: isolated mode (no user site, no PYTHON* variables, no cwd on sys.path surprises); -B: no .pyc writes.
            var launch = new ChatCodeLaunch(
                python, new[] { "-I", "-B", ScriptName }, work, _runsRoot,
                new[] { Path.GetDirectoryName(python)! }, env, limits);

            ChatCodeProcessResult result = await _launcher.RunAsync(launch, ct).ConfigureAwait(false);

            if (result.LaunchError != null)
                return Done(ChatCodeStatus.SandboxUnavailable,
                    "The sandbox could not be established, so the code was NOT run: " + result.LaunchError);

            var (artifacts, skipped) = CollectArtifacts(outDir, limits);
            ChatCodeStatus status = result.TimedOut ? ChatCodeStatus.TimedOut
                : result.LimitExceeded ? ChatCodeStatus.OutputLimitExceeded
                : result.ExitCode == 0 ? ChatCodeStatus.Completed : ChatCodeStatus.NonZeroExit;
            string message = status switch
            {
                ChatCodeStatus.TimedOut => "The program ran for more than " + limits.TimeoutSeconds + " s and its whole process tree was stopped.",
                ChatCodeStatus.OutputLimitExceeded => "The program was stopped: " + (result.LimitReason ?? "a size limit was exceeded") + ".",
                ChatCodeStatus.NonZeroExit => "The program exited with code " + result.ExitCode + ".",
                _ => "Completed."
            };
            return Done(status, message, result.ExitCode, result.Output, result.OutputTruncated, artifacts, skipped);
        }
        catch (OperationCanceledException)
        {
            // Includes a Gaming start cancelling the launcher's token: the launcher has already killed the tree.
            if (_gamingActive()) return Done(ChatCodeStatus.BlockedGaming, "Gaming Mode started, so the program was stopped.");
            if (ct.IsCancellationRequested) return Done(ChatCodeStatus.Cancelled, "The run was cancelled and the program was stopped.");
            throw;
        }
        catch (UnavailableDuringGamingException ex)
        {
            return Done(ChatCodeStatus.BlockedGaming, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Done(ChatCodeStatus.SandboxUnavailable, "The run folder could not be prepared, so the code was NOT run: " + ex.Message);
        }
        finally
        {
            TryDelete(runDir);
        }
    }

    internal static string? ValidateInputs(IReadOnlyList<ChatCodeInputFile>? inputs, ChatCodeLimits limits)
    {
        if (inputs == null || inputs.Count == 0) return null;
        if (inputs.Count > 16) return "At most 16 attached files can be used.";
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChatCodeInputFile input in inputs)
        {
            string name = input.Name ?? string.Empty;
            if (name.Length is 0 or > 100 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.StartsWith('.') || name.EndsWith('.') || name.EndsWith(' ') || IsReservedDeviceName(name))
                return "Attached file name \"" + (name.Length > 40 ? name[..40] : name) + "\" is not allowed.";
            if (!seen.Add(name)) return "Two attached files are named \"" + name + "\".";
            total += input.Content?.LongLength ?? 0;
        }
        return total > limits.MaxInputBytes ? "The attached files are larger than " + (limits.MaxInputBytes >> 20) + " MiB." : null;
    }

    private static bool IsReservedDeviceName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$"
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]));
    }

    internal static (List<ChatCodeArtifact> Artifacts, List<string> Skipped) CollectArtifacts(string outDir, ChatCodeLimits limits)
    {
        var artifacts = new List<ChatCodeArtifact>();
        var skipped = new List<string>();
        if (!Directory.Exists(outDir)) return (artifacts, skipped);

        string outFull = Path.GetFullPath(outDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long total = 0;
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(outDir).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(200).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (artifacts, skipped); }

        foreach (FileSystemInfo entry in entries)
        {
            string label = entry.Name.Length > 80 ? entry.Name[..80] : entry.Name;
            try
            {
                if (entry is not FileInfo file) { skipped.Add(label + " (folders are not collected)"); continue; }
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) { skipped.Add(label + " (link refused)"); continue; }
                string full = Path.GetFullPath(file.FullName);
                if (!full.StartsWith(outFull, StringComparison.OrdinalIgnoreCase)) { skipped.Add(label + " (outside the output folder)"); continue; }
                if (!ArtifactTypes.TryGetValue(file.Extension, out var type)) { skipped.Add(label + " (file type not shown)"); continue; }
                if (artifacts.Count >= limits.MaxArtifacts) { skipped.Add(label + " (more than " + limits.MaxArtifacts + " files)"); continue; }
                if (file.Length > limits.MaxArtifactBytes) { skipped.Add(label + " (larger than " + (limits.MaxArtifactBytes >> 10) + " KiB)"); continue; }
                if (total + file.Length > limits.MaxTotalArtifactBytes) { skipped.Add(label + " (total size limit)"); continue; }

                byte[] bytes = File.ReadAllBytes(full);
                total += bytes.Length;
                string content = type.Base64 ? Convert.ToBase64String(bytes) : new UTF8Encoding(false, false).GetString(bytes);
                if (type.Kind == "html") content = ChatCodePreview.Wrap(content);
                artifacts.Add(new ChatCodeArtifact(label, type.Mime, bytes.Length, type.Kind, content, type.Base64));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(label + " (unreadable)");
            }
        }
        return (artifacts, skipped);
    }

    private static void TryDelete(string runDir)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { if (Directory.Exists(runDir)) Directory.Delete(runDir, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(40 * (attempt + 1)); }
        }
    }
}
