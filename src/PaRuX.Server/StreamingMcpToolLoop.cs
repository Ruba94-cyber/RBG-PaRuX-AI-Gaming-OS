using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>
/// Streams an MCP tool-calling conversation to the client token by token. Every model turn is
/// requested with stream:true; text and reasoning deltas are forwarded the moment they arrive,
/// tool_call deltas are accumulated (never forwarded - the client has no use for half a JSON
/// argument string), and when a turn ends with tool calls they are executed, their results are
/// appended and the next turn is streamed the same way. A turn without tool calls ends the
/// stream. Previously the whole loop ran with stream:false and the finished answer was sent as a
/// single chunk, so the default chat path showed nothing until the last token was generated.
///
/// Trade-off (refusal heuristic): the old tool path returned the buffered answer directly and
/// never consulted <see cref="RefusalHeuristic"/>; only the no-tools path (StreamWithFallback)
/// buffers a bounded prefix for it. This loop keeps that exact contract - streaming a tool turn
/// cannot also hold its text back for a post-hoc refusal retry without re-introducing the very
/// latency this class removes, so no escalation is attempted here and none is silently claimed.
/// </summary>
internal sealed class StreamingMcpToolLoop
{
    internal const int MaxToolRounds = 5;

    /// <summary>Rounds in which a malformed/schema-invalid call is bounced back to the model with the validation error.</summary>
    internal const int MaxRepairRounds = 2;

    /// <summary>A tool that already failed this many times in one turn is not executed again.</summary>
    internal const int MaxExecutionFailuresPerTool = 2;

    /// <summary>Sends one provider request (after any prompt-growth check) and returns the response with headers read.</summary>
    internal delegate Task<HttpResponseMessage> OpenRound(JsonObject payload, CancellationToken cancellationToken);

    private readonly JsonObject _payload;
    private readonly JsonArray _tools;
    private readonly IReadOnlySet<string> _advertisedToolNames;
    private readonly OpenRound _open;
    private readonly Func<string, string, CancellationToken, Task<string>> _invokeTool;
    private readonly Func<string, CancellationToken, Task> _writeFrame;
    private const int MaxPendingCallsPerRound = 64;
    private const int MaxToolNameCharacters = 128;
    private const int MaxSseLineCharacters = 512 * 1024;
    private readonly int _maxCallsPerRound;
    private readonly int _maxCallsPerTurn;
    private int _toolCallsUsed;
    private int _toolResultCharactersRemaining;
    private readonly Dictionary<string, JsonObject?> _schemas;
    private readonly Dictionary<string, int> _executionFailures = new(StringComparer.Ordinal);
    private int _repairRoundsUsed;
    private bool _forceNoTools;

    public StreamingMcpToolLoop(
        JsonObject payload,
        JsonArray tools,
        OpenRound open,
        Func<string, string, CancellationToken, Task<string>> invokeTool,
        Func<string, CancellationToken, Task> writeFrame,
        int maxCallsPerRound,
        int maxCallsPerTurn,
        int maxToolResultCharactersPerTurn)
    {
        _payload = payload;
        _tools = tools;
        _advertisedToolNames = McpToolCallAdmission.AdvertisedNames(tools);
        _open = open;
        _invokeTool = invokeTool;
        _writeFrame = writeFrame;
        _maxCallsPerRound = maxCallsPerRound;
        _maxCallsPerTurn = maxCallsPerTurn;
        _toolResultCharactersRemaining = maxToolResultCharactersPerTurn;
        _schemas = ToolCallRepair.SchemasOf(tools);
        _payload["tools"] = tools;
        _payload["stream"] = true;
    }

    /// <summary>
    /// Opens the first round before the HTTP response has started, so a prompt-growth or transport
    /// failure still maps to a real status code. Returns null when the provider rejected the request
    /// (the caller then falls back to the plain, tool-less stream exactly as the buffered loop did).
    /// </summary>
    public async Task<HttpResponseMessage?> OpenFirstAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await _open(_payload, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        response.Dispose();
        return null;
    }

    /// <summary>Runs the loop to completion. Once called the response has started: failures become a terminal error event.</summary>
    public async Task<bool> RunAsync(HttpResponseMessage first, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = first;
        try
        {
            for (int round = 0; ; round++)
            {
                bool toolsAllowed = round < MaxToolRounds && !_forceNoTools;
                if (response == null)
                {
                    if (!toolsAllowed) { _payload.Remove("tools"); _payload.Remove("tool_choice"); }
                    response = await _open(_payload, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        int status = (int)response.StatusCode;
                        response.Dispose();
                        response = null;
                        await WriteTerminalErrorAsync(
                            "The model provider returned HTTP " + status + ": " + Trim(detail), cancellationToken).ConfigureAwait(false);
                        return false;
                    }
                }

                TurnResult turn;
                using (response)
                {
                    turn = await StreamTurnAsync(response, toolsAllowed, cancellationToken).ConfigureAwait(false);
                }
                response = null;

                if (turn.Failed)
                {
                    await WriteTerminalErrorAsync(turn.FailureMessage!, cancellationToken).ConfigureAwait(false);
                    return false;
                }
                if (turn.Errored) return false; // the provider's own error line was already forwarded, [DONE] sent
                if (turn.ToolCalls.Count == 0 || !toolsAllowed)
                {
                    await _writeFrame("data: [DONE]\n\n", cancellationToken).ConfigureAwait(false);
                    return true;
                }

                await ExecuteToolsAsync(turn, round, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure)
        {
            // Headers are already on the wire: a rethrow would end the stream with a clean-looking 200.
            await WriteTerminalErrorAsync(Trim(failure.Message), cancellationToken).ConfigureAwait(false);
            return false;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task ExecuteToolsAsync(TurnResult turn, int round, CancellationToken cancellationToken)
    {
        // 1. Inspect every call before anything runs: repair what is unambiguous, reject the rest with the reason.
        // Layers: provider IDs made unique, 64 KiB bound, strict JSON admission (exact advertised name, no duplicate keys),
        // then schema validation and bounded local repair.
        var prepared = new List<PreparedCall>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PendingToolCall call in turn.ToolCalls)
        {
            string id = call.Id;
            // A malformed provider can repeat or omit tool IDs. All tool messages in an
            // assistant turn must have unique IDs for reliable result correlation.
            if (id.Length is 0 or > 128 || !usedIds.Add(id))
            {
                int suffix = 0;
                do { id = "call_" + round + "_" + call.Index + "_" + suffix++; }
                while (!usedIds.Add(id));
            }
            string rawArguments = call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString();
            ToolCallRepair.Outcome outcome;
            if (call.ArgumentsTooLarge)
                outcome = new ToolCallRepair.Outcome(ToolCallRepair.Status.Invalid, call.Name, "{}",
                    "The tool arguments exceeded the 64 KiB model-output limit.", Array.Empty<string>());
            else if (call.ParseError != null)
                outcome = new ToolCallRepair.Outcome(ToolCallRepair.Status.Invalid, call.Name, "{}", call.ParseError, Array.Empty<string>());
            else
            {
                outcome = ToolCallRepair.Inspect(call.Name, rawArguments, _schemas);
                if (outcome.Status != ToolCallRepair.Status.Invalid)
                {
                    McpToolCallAdmission.Result admission = McpToolCallAdmission.Inspect(outcome.Name, rawArguments, _advertisedToolNames);
                    if (!admission.Allowed)
                        outcome = new ToolCallRepair.Outcome(ToolCallRepair.Status.Invalid, outcome.Name, "{}", admission.Error, Array.Empty<string>());
                }
            }
            if (outcome.Status != ToolCallRepair.Status.Valid) McpToolUseTelemetry.RecordRepair("detected");
            if (outcome.Status == ToolCallRepair.Status.LocallyRepaired) McpToolUseTelemetry.RecordRepair("fixed-locally");
            prepared.Add(new PreparedCall(id, outcome, outcome.Name.Length == 0 ? call.Name : outcome.Name));
        }

        var callsArray = new JsonArray();
        foreach (PreparedCall p in prepared) callsArray.Add(CallJson(p));

        var assistant = new JsonObject { ["role"] = "assistant", ["content"] = turn.Content.ToString(), ["tool_calls"] = callsArray };
        if (turn.Reasoning.Length > 0) assistant["reasoning_content"] = turn.Reasoning.ToString();
        _payload["messages"]!.AsArray().Add(assistant);

        // 2. Bounded repair budget: the first MaxRepairRounds rounds that contain an invalid call bounce it back;
        // after that the call fails for good and the model is told to answer without tools.
        bool anyInvalid = prepared.Any(p => p.Outcome.Status == ToolCallRepair.Status.Invalid);
        bool repairAvailable = anyInvalid && _repairRoundsUsed < MaxRepairRounds;
        if (anyInvalid)
        {
            if (repairAvailable) _repairRoundsUsed++;
            else _forceNoTools = true;
        }

        var messageById = new Dictionary<string, string>(StringComparer.Ordinal);
        var admittedCalls = new JsonArray();
        int roundAllowance = Math.Min(_maxCallsPerRound, Math.Max(0, _maxCallsPerTurn - _toolCallsUsed));
        int slot = 0;
        var repairEvents = new List<object>();
        foreach (PreparedCall p in prepared)
        {
            if (p.Outcome.Status == ToolCallRepair.Status.Invalid)
            {
                string schemaHint = _schemas.TryGetValue(p.Name, out JsonObject? sch)
                    ? " Expected arguments: " + ToolCallRepair.DescribeSchema(sch) + "." : string.Empty;
                if (repairAvailable)
                {
                    McpToolUseTelemetry.RecordRepair("reprompted");
                    messageById[p.Id] = "Tool call rejected and NOT executed: " + p.Outcome.Error + schemaHint
                        + " Send the call again with corrected arguments (" + (MaxRepairRounds - _repairRoundsUsed)
                        + " repair attempt(s) left), or answer without the tool.";
                    repairEvents.Add(new { id = p.Id, tool = p.Name, status = "reprompted", error = p.Outcome.Error });
                }
                else
                {
                    McpToolUseTelemetry.RecordRepair("exhausted");
                    messageById[p.Id] = "Tool call failed and was NOT executed: " + p.Outcome.Error
                        + " Repair attempts are exhausted. Do not call tools again; tell the user plainly that this tool call could not be completed.";
                    repairEvents.Add(new { id = p.Id, tool = p.Name, status = "exhausted", error = p.Outcome.Error });
                }
                continue;
            }

            if (_executionFailures.TryGetValue(p.Name, out int failed) && failed >= MaxExecutionFailuresPerTool)
            {
                messageById[p.Id] = "Tool call not executed: \"" + p.Name + "\" already failed " + failed
                    + " times in this turn. Do not retry it; tell the user it failed.";
                continue;
            }
            if (slot++ < roundAllowance) admittedCalls.Add(CallJson(p));
            else messageById[p.Id] = "Tool call skipped: the per-turn tool-call limit was reached.";
        }

        string[] names = prepared.Select(p => p.Name).ToArray();
        await _writeFrame("data: " + JsonSerializer.Serialize(new { parux_event = "tool_calls", round, names }) + "\n\n",
            cancellationToken).ConfigureAwait(false);
        foreach (object repairEvent in repairEvents)
            await _writeFrame("data: " + JsonSerializer.Serialize(new { parux_event = "tool_repair", repair = repairEvent }) + "\n\n",
                cancellationToken).ConfigureAwait(false);

        // 3. Execute what is valid.
        IReadOnlyList<McpToolCallBatchExecutor.Result> results = await McpToolCallBatchExecutor.ExecuteAsync(
            admittedCalls, _invokeTool, cancellationToken).ConfigureAwait(false);
        _toolCallsUsed += admittedCalls.Count;

        var nameById = prepared.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        foreach (McpToolCallBatchExecutor.Result result in results)
        {
            string resultText = result.Content;
            if (IsExecutionFailure(resultText) && nameById.TryGetValue(result.Id, out string? failedTool))
            {
                int count = _executionFailures.GetValueOrDefault(failedTool) + 1;
                _executionFailures[failedTool] = count;
                McpToolUseTelemetry.RecordRepair("execution-failure");
                resultText += count >= MaxExecutionFailuresPerTool
                    ? " (This tool has now failed " + count + " times this turn and will not run again; tell the user it failed.)"
                    : " (If this was caused by the arguments, correct them and retry once; otherwise answer without it.)";
            }
            if (_toolResultCharactersRemaining <= 0)
                resultText = "Tool result omitted: the cumulative tool-result budget is exhausted.";
            else
            {
                int limit = Math.Min(8_000, _toolResultCharactersRemaining);
                if (resultText.Length > limit)
                    resultText = resultText[..limit] + "\n[tool result truncated by the Context Engine budget]";
                _toolResultCharactersRemaining = Math.Max(0, _toolResultCharactersRemaining - resultText.Length);
            }
            messageById[result.Id] = resultText;
        }

        // 4. Exactly one tool message per tool_call_id, in the model's original call order, refused siblings included.
        foreach (PreparedCall p in prepared)
        {
            _payload["messages"]!.AsArray().Add(new JsonObject
            {
                ["role"] = "tool", ["tool_call_id"] = p.Id,
                ["content"] = messageById.GetValueOrDefault(p.Id) ?? "Tool call NOT executed: no result was returned."
            });
        }
    }

    private static JsonObject CallJson(PreparedCall p) => new()
    {
        ["id"] = p.Id,
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = p.Name, ["arguments"] = p.Outcome.ArgumentsJson }
    };

    private static bool IsExecutionFailure(string result) =>
        result.StartsWith("Tool call failed:", StringComparison.Ordinal)
        || result.StartsWith("Tool reported an error:", StringComparison.Ordinal);

    private sealed record PreparedCall(string Id, ToolCallRepair.Outcome Outcome, string Name);

    private async Task<TurnResult> StreamTurnAsync(HttpResponseMessage response, bool toolsAllowed, CancellationToken cancellationToken)
    {
        var turn = new TurnResult();
        var calls = new SortedDictionary<int, PendingToolCall>();
        bool sawDone = false, sawFinish = false;
        string? finalFinishReason = null;

        // A model that writes its call as text (<tool_call>... or <function=...>) must not have that raw markup
        // shown as the answer: while the start of the reply could still become such a call, frames are held back.
        var held = new List<string>();
        bool holding = false, holdDecided = false;
        async Task Emit(string frame, CancellationToken ct)
        {
            if (holding) held.Add(frame);
            else await _writeFrame(frame, ct).ConfigureAwait(false);
        }
        async Task ReleaseHeldAsync(CancellationToken ct)
        {
            holding = false;
            foreach (string frame in held) await _writeFrame(frame, ct).ConfigureAwait(false);
            held.Clear();
        }
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var bounded = new BoundedSseLineReader(reader);
            while (await bounded.ReadLineAsync(MaxSseLineCharacters, cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string data = line[5..].Trim();
                if (data.Length == 0) continue;
                if (data == "[DONE]") { sawDone = true; break; }

                JsonNode? chunk;
                try { chunk = JsonNode.Parse(data); }
                catch (JsonException) { await Emit(line + "\n\n", cancellationToken).ConfigureAwait(false); continue; }

                if (chunk is JsonObject obj && obj.ContainsKey("error"))
                {
                    if (holding) await ReleaseHeldAsync(cancellationToken).ConfigureAwait(false);
                    await Emit(line + "\n\n", cancellationToken).ConfigureAwait(false);
                    await Emit("data: [DONE]\n\n", cancellationToken).ConfigureAwait(false);
                    turn.Errored = true;
                    return turn;
                }

                JsonNode? choice = (chunk?["choices"] as JsonArray) is { Count: > 0 } choices ? choices[0] : null;
                JsonObject? delta = choice?["delta"] as JsonObject;
                string? finishReason = StringOf(choice?["finish_reason"]);
                bool hasFinish = finishReason is not null;
                bool hasToolDelta = false;

                if (delta?["tool_calls"] is JsonArray toolDeltas)
                {
                    hasToolDelta = true;
                    foreach (JsonNode? tdNode in toolDeltas)
                    {
                        if (tdNode is not JsonObject td) continue;
                        if (td["index"] is not JsonValue iv || !iv.TryGetValue(out int idx) || idx < 0)
                        {
                            turn.Failed = true;
                            turn.FailureMessage = "The model emitted a tool call without a valid index; no tools were executed.";
                            return turn;
                        }
                        if (!calls.TryGetValue(idx, out PendingToolCall? pending))
                        {
                            if (calls.Count >= MaxPendingCallsPerRound)
                            {
                                turn.Failed = true;
                                turn.FailureMessage = "The model emitted too many tool calls in one turn; no tools were executed.";
                                return turn;
                            }
                            calls[idx] = pending = new PendingToolCall { Index = idx };
                        }
                        if (td["id"] is JsonValue idv && idv.TryGetValue(out string? callId) && !string.IsNullOrEmpty(callId))
                            pending.Id = callId;
                        if (td["function"] is JsonObject fn)
                        {
                            if (fn["name"] is JsonValue nv && nv.TryGetValue(out string? nm) && !string.IsNullOrEmpty(nm))
                            {
                                if (nm.Length > MaxToolNameCharacters - pending.Name.Length)
                                {
                                    turn.Failed = true;
                                    turn.FailureMessage = "The model emitted an oversized tool name; no tools were executed.";
                                    return turn;
                                }
                                pending.Name += nm;
                            }
                            if (fn["arguments"] is JsonValue av && av.TryGetValue(out string? args) && args is not null &&
                                !pending.ArgumentsTooLarge)
                            {
                                // Bound memory while receiving SSE chunks, before the JSON preflight.
                                // Do not retain partial executable arguments if the bound is exceeded.
                                if (args.Length > McpToolCallAdmission.MaxArgumentsCharacters - pending.Arguments.Length)
                                {
                                    pending.Arguments.Clear();
                                    pending.ArgumentsTooLarge = true;
                                }
                                else pending.Arguments.Append(args);
                            }
                        }
                    }
                }

                string? content = StringOf(delta?["content"]);
                string? reasoning = StringOf(delta?["reasoning_content"]);
                if (!string.IsNullOrEmpty(content)) turn.Content.Append(content);
                if (!string.IsNullOrEmpty(reasoning)) turn.Reasoning.Append(reasoning);
                if (hasFinish)
                {
                    sawFinish = true;
                    finalFinishReason = finishReason;
                }

                if (toolsAllowed && calls.Count == 0 && !holdDecided && !string.IsNullOrEmpty(content))
                {
                    string lead = turn.Content.ToString().TrimStart();
                    if (lead.Length > 0)
                    {
                        if (ToolCallRepair.StartsTextualCall(lead)) { holding = true; holdDecided = true; }
                        else if (ToolCallRepair.CouldStartTextualCall(lead)) holding = true;
                        else { if (holding) await ReleaseHeldAsync(cancellationToken).ConfigureAwait(false); holdDecided = true; }
                    }
                }

                bool turnHasTools = calls.Count > 0;
                if (hasToolDelta && string.IsNullOrEmpty(content) && string.IsNullOrEmpty(reasoning) && !hasFinish)
                    continue; // pure tool_call delta: accumulated, never shown
                if (hasFinish && turnHasTools && toolsAllowed)
                    continue; // finish_reason=tool_calls ends the TURN, not the client's stream
                if (hasToolDelta)
                {
                    // Mixed chunk: forward only the text half.
                    delta!.Remove("tool_calls");
                    await Emit("data: " + chunk!.ToJsonString() + "\n\n", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                await Emit(line + "\n\n", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (SseLineTooLongException)
        {
            turn.Failed = true;
            turn.FailureMessage = "The model emitted an oversized SSE message; no tools were executed.";
            return turn;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
        {
            turn.Failed = true;
            turn.FailureMessage = "The model provider stream was interrupted: " + Trim(ex.Message);
            return turn;
        }

        if (!sawDone && !sawFinish)
        {
            turn.Failed = true;
            turn.FailureMessage = "The model provider closed the stream before the answer was complete.";
            return turn;
        }

        // A bare [DONE] or a text/length finish marker does not commit pending
        // side-effecting calls. Only the provider's explicit "tool_calls"
        // finish_reason authorizes this turn's tool batch.
        if (toolsAllowed && calls.Count > 0 && finalFinishReason != "tool_calls")
        {
            turn.Failed = true;
            turn.FailureMessage = "The provider ended a tool turn without a tool_calls finish marker; no tools were executed.";
            return turn;
        }
        if (toolsAllowed && calls.Count == 0 && finalFinishReason == "tool_calls")
        {
            turn.Failed = true;
            turn.FailureMessage = "The provider declared tool_calls but sent no tool calls; no tools were executed.";
            return turn;
        }
        if (toolsAllowed && calls.Count == 0 && turn.Content.Length > 0)
        {
            List<ToolCallRepair.TextualCall> textual = ToolCallRepair.ParseTextualCalls(turn.Content.ToString(), out string remaining);
            if (textual.Count > 0)
            {
                // Recovered: the markup is not shown, the calls go through the same inspect/execute path.
                held.Clear();
                holding = false;
                turn.Content.Clear().Append(remaining);
                int i = 0;
                foreach (ToolCallRepair.TextualCall t in textual)
                {
                    var pending = new PendingToolCall { Index = i++, Name = t.Name, ParseError = t.ParseError };
                    pending.Arguments.Append(t.RawArguments);
                    turn.ToolCalls.Add(pending);
                    McpToolUseTelemetry.RecordRepair("textual-recovered");
                }
                return turn;
            }
        }
        if (holding) await ReleaseHeldAsync(cancellationToken).ConfigureAwait(false);

        if (toolsAllowed) foreach (PendingToolCall call in calls.Values) turn.ToolCalls.Add(call);
        return turn;
    }

    private async Task WriteTerminalErrorAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await _writeFrame("data: " + JsonSerializer.Serialize(new { error = new { message, type = "provider_error" } }) + "\n\n",
                cancellationToken).ConfigureAwait(false);
            await _writeFrame("data: [DONE]\n\n", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* connection already gone */ }
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static string Trim(string text) => text.Length <= 400 ? text : text[..400] + "...";

    /// <summary>
    /// Read SSE text with a strict in-flight memory bound. StreamReader.ReadLineAsync
    /// allocates the entire line before the caller can check its length. A corrupted
    /// local provider could therefore send a multi-megabyte token/tool-argument line.
    /// This reader checks each chunk BEFORE appending and recognizes LF, CRLF and CR.
    /// </summary>
    private sealed class BoundedSseLineReader
    {
        private readonly TextReader _reader;
        private readonly char[] _buffer = new char[4 * 1024];
        private int _position;
        private int _available;
        private bool _skipFollowingLf;

        public BoundedSseLineReader(TextReader reader) => _reader = reader;

        public async ValueTask<string?> ReadLineAsync(int maximumCharacters, CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_position >= _available)
                {
                    _available = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _position = 0;
                    if (_available == 0) return line.Length == 0 ? null : line.ToString();
                }

                if (_skipFollowingLf)
                {
                    _skipFollowingLf = false;
                    if (_buffer[_position] == '\n') { _position++; continue; }
                }

                int end = _position;
                while (end < _available && _buffer[end] != '\n' && _buffer[end] != '\r') end++;
                int length = end - _position;
                if (length > maximumCharacters - line.Length) throw new SseLineTooLongException();
                line.Append(_buffer, _position, length);
                _position = end;
                if (_position < _available)
                {
                    _skipFollowingLf = _buffer[_position++] == '\r';
                    return line.ToString();
                }
            }
        }
    }

    private sealed class SseLineTooLongException : Exception { }

    private sealed class PendingToolCall
    {
        public int Index;
        public string Id = string.Empty;
        public string Name = string.Empty;
        public StringBuilder Arguments { get; } = new();
        public bool ArgumentsTooLarge;
        /// <summary>Set for a text-form call that could not be parsed; the call is rejected, not run.</summary>
        public string? ParseError;
    }

    private sealed class TurnResult
    {
        public StringBuilder Content { get; } = new();
        public StringBuilder Reasoning { get; } = new();
        public List<PendingToolCall> ToolCalls { get; } = new();
        public bool Failed;
        public bool Errored;
        public string? FailureMessage;
    }
}
