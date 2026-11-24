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
                bool toolsAllowed = round < MaxToolRounds;
                if (response == null)
                {
                    if (!toolsAllowed) _payload.Remove("tools");
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
        var callsArray = new JsonArray();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var oversizedIds = new HashSet<string>(StringComparer.Ordinal);
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
            string arguments = call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString();
            if (call.ArgumentsTooLarge) oversizedIds.Add(id);
            callsArray.Add(new JsonObject
            {
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = arguments }
            });
        }

        // Verify the exact tool name and JSON syntax before any user-facing or
        // external tool invocation. Incomplete and unadvertised calls are NOT run.
        int roundAllowance = Math.Min(_maxCallsPerRound, Math.Max(0, _maxCallsPerTurn - _toolCallsUsed));
        var admittedCalls = new JsonArray();
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < callsArray.Count; i++)
        {
            JsonObject call = callsArray[i]!.AsObject();
            string id = call["id"]!.GetValue<string>();
            if (i >= roundAllowance)
            {
                refused[id] = "Tool call skipped: the per-turn tool-call limit was reached.";
                call["function"]!["arguments"] = "{}"; // tool-call transcript must stay valid JSON
                continue;
            }
            if (oversizedIds.Contains(id))
            {
                refused[id] = "Tool call NOT executed: arguments exceeded the 64 KiB model-output limit.";
                call["function"]!["arguments"] = "{}";
                continue;
            }
            JsonObject fn = call["function"]!.AsObject();
            McpToolCallAdmission.Result admission = McpToolCallAdmission.Inspect(
                fn["name"]!.GetValue<string>(), fn["arguments"]!.GetValue<string>(), _advertisedToolNames);
            if (!admission.Allowed)
            {
                refused[id] = "Tool call NOT executed: " + admission.Error +
                    " Retry with an advertised tool name and complete JSON arguments.";
                // Preserve the explanation in the tool message, not invalid/oversized
                // arguments in the following provider request. This avoids a second
                // malformed-tool-request failure before the model can correct itself.
                fn["arguments"] = "{}";
                continue;
            }
            fn["arguments"] = admission.ArgumentsJson;
            admittedCalls.Add(call.DeepClone());
        }

        var assistant = new JsonObject { ["role"] = "assistant", ["content"] = turn.Content.ToString(), ["tool_calls"] = callsArray };
        if (turn.Reasoning.Length > 0) assistant["reasoning_content"] = turn.Reasoning.ToString();
        _payload["messages"]!.AsArray().Add(assistant);

        var names = callsArray.Select(c => c!["function"]!["name"]!.GetValue<string>()).ToArray();
        await _writeFrame("data: " + JsonSerializer.Serialize(new { parux_event = "tool_calls", round, names }) + "\n\n",
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<McpToolCallBatchExecutor.Result> results = await McpToolCallBatchExecutor.ExecuteAsync(
            admittedCalls, _invokeTool, cancellationToken).ConfigureAwait(false);
        _toolCallsUsed += admittedCalls.Count;
        var byId = results.ToDictionary(result => result.Id, StringComparer.Ordinal);

        // Write tool messages in the MODEL'S original order, including refused
        // siblings. Each ID receives exactly one result, so providers cannot
        // misattribute a successful tool reply to an invalid neighbour.
        foreach (JsonNode? node in callsArray)
        {
            string id = node!["id"]!.GetValue<string>();
            string resultText;
            if (refused.TryGetValue(id, out string? reason))
                resultText = reason;
            else if (byId.TryGetValue(id, out McpToolCallBatchExecutor.Result? result))
            {
                resultText = result.Content;
                if (_toolResultCharactersRemaining <= 0)
                    resultText = "Tool result omitted: the cumulative tool-result budget is exhausted.";
                else
                {
                    int limit = Math.Min(8_000, _toolResultCharactersRemaining);
                    if (resultText.Length > limit)
                        resultText = resultText[..limit] + "\n[tool result truncated by the Context Engine budget]";
                    _toolResultCharactersRemaining = Math.Max(0, _toolResultCharactersRemaining - resultText.Length);
                }
            }
            else
                resultText = "Tool call NOT executed: no result was returned.";
            _payload["messages"]!.AsArray().Add(new JsonObject
            {
                ["role"] = "tool", ["tool_call_id"] = id, ["content"] = resultText
            });
        }
    }

    private async Task<TurnResult> StreamTurnAsync(HttpResponseMessage response, bool toolsAllowed, CancellationToken cancellationToken)
    {
        var turn = new TurnResult();
        var calls = new SortedDictionary<int, PendingToolCall>();
        bool sawDone = false, sawFinish = false;
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Length > MaxSseLineCharacters)
                {
                    turn.Failed = true;
                    turn.FailureMessage = "The model emitted an oversized SSE message; no tools were executed.";
                    return turn;
                }
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string data = line[5..].Trim();
                if (data.Length == 0) continue;
                if (data == "[DONE]") { sawDone = true; break; }

                JsonNode? chunk;
                try { chunk = JsonNode.Parse(data); }
                catch (JsonException) { await _writeFrame(line + "\n\n", cancellationToken).ConfigureAwait(false); continue; }

                if (chunk is JsonObject obj && obj.ContainsKey("error"))
                {
                    await _writeFrame(line + "\n\n", cancellationToken).ConfigureAwait(false);
                    await _writeFrame("data: [DONE]\n\n", cancellationToken).ConfigureAwait(false);
                    turn.Errored = true;
                    return turn;
                }

                JsonNode? choice = (chunk?["choices"] as JsonArray) is { Count: > 0 } choices ? choices[0] : null;
                JsonObject? delta = choice?["delta"] as JsonObject;
                bool hasFinish = choice?["finish_reason"] is JsonValue fr && fr.GetValueKind() == JsonValueKind.String;
                bool hasToolDelta = false;

                if (delta?["tool_calls"] is JsonArray toolDeltas)
                {
                    hasToolDelta = true;
                    foreach (JsonNode? tdNode in toolDeltas)
                    {
                        if (tdNode is not JsonObject td) continue;
                        int idx = td["index"] is JsonValue iv && iv.TryGetValue(out int parsedIndex) ? parsedIndex : 0;
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
                if (hasFinish) sawFinish = true;

                bool turnHasTools = calls.Count > 0;
                if (hasToolDelta && string.IsNullOrEmpty(content) && string.IsNullOrEmpty(reasoning) && !hasFinish)
                    continue; // pure tool_call delta: accumulated, never shown
                if (hasFinish && turnHasTools && toolsAllowed)
                    continue; // finish_reason=tool_calls ends the TURN, not the client's stream
                if (hasToolDelta)
                {
                    // Mixed chunk: forward only the text half.
                    delta!.Remove("tool_calls");
                    await _writeFrame("data: " + chunk!.ToJsonString() + "\n\n", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                await _writeFrame(line + "\n\n", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
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

    private sealed class PendingToolCall
    {
        public int Index;
        public string Id = string.Empty;
        public string Name = string.Empty;
        public StringBuilder Arguments { get; } = new();
        public bool ArgumentsTooLarge;
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
