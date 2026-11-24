using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server.ChatCode;

/// <summary>Last few runs, kept so the chat view can fetch the plots/HTML a tool call produced. Bounded in count and bytes.</summary>
public sealed class ChatCodeRunStore
{
    public const int MaxRuns = 8;
    public const long MaxBytes = 24L << 20;
    private readonly object _gate = new();
    private readonly List<(string Id, ChatCodeResult Result, DateTime AtUtc)> _runs = new();

    public string Add(ChatCodeResult result)
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        lock (_gate)
        {
            _runs.Add((id, result, DateTime.UtcNow));
            while (_runs.Count > MaxRuns || (_runs.Count > 1 && _runs.Sum(r => r.Result.Artifacts.Sum(a => a.Content.Length)) > MaxBytes))
                _runs.RemoveAt(0);
        }
        return id;
    }

    public ChatCodeResult? Get(string id)
    {
        lock (_gate) return _runs.FirstOrDefault(r => r.Id == id).Result;
    }

    public IReadOnlyList<(string Id, DateTime AtUtc, ChatCodeStatus Status, int Artifacts)> List()
    {
        lock (_gate) return _runs.Select(r => (r.Id, r.AtUtc, r.Result.Status, r.Result.Artifacts.Count)).ToList();
    }
}

/// <summary>
/// The single model-facing tool, <c>run_python</c>. Consent follows the existing tool-approval model
/// (<see cref="AgentLibraryConsentStore"/>): every run needs the user's explicit, single-use approval of THIS code, given
/// through a desktop-owner endpoint that no tool can reach; a permission mode, including bypass, never stands in for it.
/// The tool is only advertised when the managed runtime is installed and Gaming is off; the runner re-checks both.
/// </summary>
public sealed class ChatCodeTools
{
    public const string RunPythonTool = "run_python";
    public const string Action = "run_python";
    public const int MaxResultCharacters = 6_000;
    public static readonly TimeSpan DefaultApprovalWait = TimeSpan.FromSeconds(120);
    private const string Failed = "Code tool failed: ";

    private readonly ChatCodeRunner _runner;
    private readonly ChatCodeRuntime _runtime;
    private readonly AgentLibraryConsentStore _consent;
    private readonly ChatCodeRunStore _runs;
    private readonly Func<bool> _gamingActive;

    public ChatCodeTools(ChatCodeRunner runner, ChatCodeRuntime runtime, AgentLibraryConsentStore consent, ChatCodeRunStore runs, Func<bool> gamingActive)
    {
        _runner = runner;
        _runtime = runtime;
        _consent = consent;
        _runs = runs;
        _gamingActive = gamingActive;
    }

    public TimeSpan ApprovalWait { get; set; } = DefaultApprovalWait;
    public AgentLibraryConsentStore Consent => _consent;
    public ChatCodeRunStore Runs => _runs;
    public ChatCodeRuntime Runtime => _runtime;
    public ChatCodeRunner Runner => _runner;

    public static bool IsBuiltInTool(string? name) => string.Equals(name, RunPythonTool, StringComparison.Ordinal);

    public JsonArray GetToolSchemas(IReadOnlyCollection<string>? allowedTools = null)
    {
        var schemas = new JsonArray();
        if (!ContextEngineService.ToolNameAllowed(RunPythonTool, allowedTools) || !_runtime.ToolAvailable) return schemas;
        schemas.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = RunPythonTool,
                ["description"] = "Run a short Python 3 program in PaRuX's isolated sandbox and get its printed output. The user must approve every run. "
                    + "The sandbox has NO network, NO access to the user's files, a few seconds of time, limited memory and no ability to start other programs; only the Python standard library is installed. "
                    + "To show the user a result, write files into the folder named by os.environ['PARUX_OUT'] (default 'out'): .png/.svg images, .html pages (self-contained, no external resources), .csv/.txt/.json. Print results you need to read.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["code"] = new JsonObject { ["type"] = "string", ["description"] = "Complete Python 3 source.", ["minLength"] = 1, ["maxLength"] = ChatCodeLimits.MaxCodeCharacters },
                        ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = ChatCodeLimits.MaxTimeoutSeconds, ["description"] = "Wall-clock limit, default 20." }
                    },
                    ["required"] = new JsonArray("code")
                }
            }
        });
        return schemas;
    }

    public async Task<string> CallAsync(string toolName, string argumentsJson, CancellationToken ct, IReadOnlyCollection<string>? allowedTools = null)
    {
        try
        {
            if (!IsBuiltInTool(toolName)) return Failed + "unknown tool.";
            if (!ContextEngineService.ToolNameAllowed(toolName, allowedTools)) return Failed + "the selected skill does not allow this tool.";
            if (_gamingActive()) return Failed + "Gaming Mode is active, so code execution is unavailable.";

            JsonObject args;
            try { args = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { return Failed + "the arguments are not valid JSON."; }
            string code = args["code"] is JsonValue cv && cv.TryGetValue(out string? c) ? c ?? string.Empty : string.Empty;
            int timeout = args["timeout_seconds"] is JsonValue tv && tv.TryGetValue(out int t) ? t : 20;
            if (string.IsNullOrWhiteSpace(code)) return Failed + "no code was provided.";
            if (code.Length > ChatCodeLimits.MaxCodeCharacters) return Failed + "the code is longer than " + ChatCodeLimits.MaxCodeCharacters + " characters.";
            if (timeout is < 1 or > ChatCodeLimits.MaxTimeoutSeconds) return Failed + "timeout_seconds must be 1.." + ChatCodeLimits.MaxTimeoutSeconds + ".";
            if (_runtime.UsableInterpreter() == null)
                return Failed + "BLOCKED: no PaRuX-managed Python runtime is installed (or code execution is switched off). Tell the user they can install it in Settings > Code execution; do not retry.";

            string binding = AgentLibraryConsentStore.Bind(Action, code, timeout.ToString());
            string? id = _consent.Request(Action, binding, Summary(code, timeout));
            if (id == null) return Failed + "too many code approvals are already waiting; ask the user to answer them first.";

            string? decision = await WaitForDecisionAsync(id, ct).ConfigureAwait(false);
            if (decision != "approved")
                return decision == "denied" ? Failed + "the user did not approve running this code. Do not run it; ask what they want instead."
                    : Failed + "the user did not answer the approval request in time. Nothing was run.";
            if (!_consent.TryConsume(id, Action, binding)) return Failed + "the approval is no longer valid. Nothing was run.";

            ChatCodeResult result = await _runner.RunAsync(
                new ChatCodeRequest(code, new ChatCodeLimits { TimeoutSeconds = timeout }), ct).ConfigureAwait(false);
            string runId = _runs.Add(result);
            return Format(result, runId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed + Bound(ex.Message, 300);
        }
    }

    internal static string Summary(string code, int timeout)
    {
        string preview = code.Length <= 1_800 ? code : code[..1_800] + "\n... (" + (code.Length - 1_800) + " more characters)";
        return "Run Python in the PaRuX sandbox (no network, no access to your files, up to " + timeout + " s):\n" + preview;
    }

    private async Task<string?> WaitForDecisionAsync(string id, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + ApprovalWait;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            AgentLibraryConsentItem? item = _consent.List().FirstOrDefault(i => i.Id == id);
            if (item == null) return "denied";            // denied/consumed/expired entries are pruned
            if (item.State == "approved") return "approved";
            if (item.State == "denied") return "denied";
            if (DateTime.UtcNow >= deadline) return null;
            if (_gamingActive()) return null;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    internal static string Format(ChatCodeResult r, string runId)
    {
        var sb = new StringBuilder();
        sb.Append("status: ").Append(r.Status).Append(r.ExitCode.HasValue ? " (exit code " + r.ExitCode + ")" : "").Append("; ").Append(r.ElapsedMilliseconds).Append(" ms; run_id ").Append(runId).Append('\n');
        if (!string.IsNullOrEmpty(r.Message) && r.Status != ChatCodeStatus.Completed) sb.Append(r.Message).Append('\n');
        if (r.Status is ChatCodeStatus.BlockedGaming or ChatCodeStatus.BlockedNeedsRuntime or ChatCodeStatus.SandboxUnavailable or ChatCodeStatus.Rejected)
            return Failed + sb.ToString().Trim() + " Nothing was run.";
        string output = r.Output.Length <= MaxResultCharacters ? r.Output : r.Output[..MaxResultCharacters] + "\n[output truncated]";
        sb.Append("output:\n").Append(output.Length == 0 ? "(no output)" : output).Append('\n');
        if (r.Artifacts.Count > 0)
            sb.Append("files shown to the user: ").Append(string.Join(", ", r.Artifacts.Select(a => a.Name + " (" + a.Kind + ", " + a.Bytes + " bytes)"))).Append('\n');
        if (r.SkippedArtifacts.Count > 0)
            sb.Append("files not shown: ").Append(string.Join("; ", r.SkippedArtifacts.Take(6))).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static string Bound(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
