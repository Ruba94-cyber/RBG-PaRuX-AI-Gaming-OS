using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Gateway;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Constrained-output guard on the gateway (grammar / JSON schema pass-through contract).</summary>
public sealed class ConstrainedOutputGuardTests
{
    private static string? Check(string json) => ConstrainedOutputGuard.Validate(JsonNode.Parse(json)!.AsObject());

    [Theory]
    [InlineData("{\"messages\":[]}")]
    [InlineData("{\"response_format\":{\"type\":\"text\"}}")]
    [InlineData("{\"response_format\":{\"type\":\"json_object\"}}")]
    [InlineData("{\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"a\",\"schema\":{\"type\":\"object\"}}}}")]
    [InlineData("{\"json_schema\":{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\"}}}}")]
    [InlineData("{\"grammar\":\"root ::= \\\"yes\\\" | \\\"no\\\"\"}")]
    public void Well_formed_constraints_are_accepted_untouched(string body) => Assert.Null(Check(body));

    [Theory]
    [InlineData("{\"response_format\":\"json\"}", "must be an object")]
    [InlineData("{\"response_format\":{\"type\":\"xml\"}}", "must be \"text\"")]
    [InlineData("{\"response_format\":{\"type\":\"json_schema\"}}", "json_schema")]
    [InlineData("{\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"a\"}}}", "schema\" is required")]
    [InlineData("{\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"schema\":\"nope\"}}}", "JSON Schema object")]
    [InlineData("{\"grammar\":42}", "GBNF string")]
    [InlineData("{\"grammar\":\"   \"}", "must not be empty")]
    [InlineData("{\"grammar\":\"just some words\"}", "not GBNF")]
    [InlineData("{\"grammar\":\"root ::= \\\"a\\\"\",\"json_schema\":{\"type\":\"object\"}}", "only one output constraint")]
    [InlineData("{\"response_format\":{\"type\":\"json_object\"},\"grammar\":\"root ::= \\\"a\\\"\"}", "only one output constraint")]
    public void Malformed_or_contradictory_constraints_are_refused_with_a_reason(string body, string fragment)
    {
        string? error = Check(body);
        Assert.NotNull(error);
        Assert.Contains(fragment, error);
    }

    [Fact]
    public void Oversized_grammar_and_deeply_nested_schemas_are_refused()
    {
        string big = "root ::= " + new string('a', ConstrainedOutputGuard.MaxGrammarCharacters);
        Assert.Contains("exceeds", Check(new JsonObject { ["grammar"] = big }.ToJsonString())!);

        JsonNode nested = new JsonObject { ["type"] = "string" };
        for (int i = 0; i < 40; i++) nested = new JsonObject { ["items"] = nested };
        Assert.Contains("nested deeper", Check(new JsonObject { ["json_schema"] = nested }.ToJsonString())!);
    }
}

/// <summary>Agent loop guard inside the streaming tool loop: the same call is never executed twice in a turn.</summary>
public sealed class AgentLoopGuardTests
{
    private static readonly JsonArray Tools = new(new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = "lookup",
            ["parameters"] = JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}},\"required\":[\"q\"]}")
        }
    });

    private static string Sse(params string[] lines) => string.Join("\n\n", lines) + "\n\n";
    private static string Finish(string reason) => "data: " + new JsonObject
    { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject(), ["finish_reason"] = reason }) }.ToJsonString();
    private static string Content(string text) => "data: " + new JsonObject
    { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text } }) }.ToJsonString();
    private static string Call(string id, string args) => "data: " + new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject
        {
            ["delta"] = new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0, ["id"] = id,
                    ["function"] = new JsonObject { ["name"] = "lookup", ["arguments"] = args }
                })
            }
        })
    }.ToJsonString();

    private sealed class Rig
    {
        public List<string> Frames { get; } = new();
        public List<string> Invocations { get; } = new();
        public List<JsonObject> Requests { get; } = new();
        public Queue<string> Turns { get; } = new();

        public async Task RunAsync()
        {
            var payload = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }) };
            var loop = new StreamingMcpToolLoop(payload, (JsonArray)Tools.DeepClone(),
                open: (body, _) =>
                {
                    Requests.Add((JsonObject)body.DeepClone());
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Turns.Dequeue(), Encoding.UTF8) });
                },
                invokeTool: (n, a, _) => { Invocations.Add(n + a); return Task.FromResult("result-of-" + a); },
                writeFrame: (f, _) => { Frames.Add(f); return Task.CompletedTask; },
                4, 8, 16_000);
            HttpResponseMessage? first = await loop.OpenFirstAsync(default);
            await loop.RunAsync(first!, default);
        }

        public IEnumerable<string> ToolMessages(int request) =>
            Requests[request]["messages"]!.AsArray().Where(m => m!["role"]!.GetValue<string>() == "tool").Select(m => m!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_repeated_identical_call_is_not_executed_again_and_the_model_is_told_why()
    {
        var rig = new Rig();
        rig.Turns.Enqueue(Sse(Call("a", "{\"q\":\"x\"}"), Finish("tool_calls")));
        rig.Turns.Enqueue(Sse(Call("b", "{ \"q\": \"x\" }"), Finish("tool_calls")));   // same call, different whitespace
        rig.Turns.Enqueue(Sse(Content("done"), Finish("stop")));
        await rig.RunAsync();

        Assert.Single(rig.Invocations);
        Assert.Contains(rig.ToolMessages(2), m => m.Contains("already ran in this turn"));
        Assert.Contains(rig.Frames, f => f.Contains("loop-blocked"));
    }

    [Fact]
    public async Task Repeating_again_stops_the_tool_loop_and_tools_are_no_longer_offered()
    {
        var rig = new Rig();
        rig.Turns.Enqueue(Sse(Call("a", "{\"q\":\"x\"}"), Finish("tool_calls")));
        rig.Turns.Enqueue(Sse(Call("b", "{\"q\":\"x\"}"), Finish("tool_calls")));
        rig.Turns.Enqueue(Sse(Call("c", "{\"q\":\"x\"}"), Finish("tool_calls")));
        rig.Turns.Enqueue(Sse(Content("final answer"), Finish("stop")));
        await rig.RunAsync();

        Assert.Single(rig.Invocations);
        Assert.Contains(rig.Frames, f => f.Contains("loop-stopped"));
        Assert.False(rig.Requests[3].ContainsKey("tools"));          // the last request has no tools: the model must answer
        Assert.Contains(rig.ToolMessages(3), m => m.Contains("loop was stopped"));
        Assert.Contains(rig.Frames, f => f.Contains("final answer"));
    }

    [Fact]
    public async Task Different_arguments_are_not_a_loop()
    {
        var rig = new Rig();
        rig.Turns.Enqueue(Sse(Call("a", "{\"q\":\"x\"}"), Finish("tool_calls")));
        rig.Turns.Enqueue(Sse(Call("b", "{\"q\":\"y\"}"), Finish("tool_calls")));
        rig.Turns.Enqueue(Sse(Content("done"), Finish("stop")));
        await rig.RunAsync();
        Assert.Equal(2, rig.Invocations.Count);
    }

    [Fact]
    public async Task Duplicate_argument_keys_are_still_refused_by_the_strict_admission_layer()
    {
        var rig = new Rig();
        rig.Turns.Enqueue(Sse(Call("a", "{\"q\":\"x\",\"q\":\"y\"}"), Finish("tool_calls")));
        rig.Turns.Enqueue(Sse(Content("done"), Finish("stop")));
        await rig.RunAsync();
        Assert.Empty(rig.Invocations);
        Assert.True(rig.Requests.Count == 2, string.Join(" | ", rig.Frames));
        Assert.Contains(rig.ToolMessages(1), m => m.Contains("NOT executed") && m.Contains("Duplicate"));
    }
}

/// <summary>OpenAPI body schemas: described only where a real schema exists.</summary>
public sealed class OpenApiBodySchemaTests
{
    private static JsonObject Doc() => OpenApiDocument.Build(new[]
    {
        new OpenApiRoute("/v1/chat/completions", "POST", false),
        new OpenApiRoute("/api/v1/mcp/servers", "POST", true),
        new OpenApiRoute("/api/v1/mcp/servers/{id}/start", "POST", true),
        new OpenApiRoute("/api/v1/mcp/servers/{id}/timeout", "POST", true),
        new OpenApiRoute("/api/v1/something/else", "POST", false)
    }, "1.0.0");

    [Fact]
    public void Chat_completions_documents_the_constrained_output_fields()
    {
        JsonObject post = Doc()["paths"]!["/v1/chat/completions"]!["post"]!.AsObject();
        Assert.True(post["x-parux-body-described"]!.GetValue<bool>());
        JsonObject props = post["requestBody"]!["content"]!["application/json"]!["schema"]!["properties"]!.AsObject();
        Assert.NotNull(props["response_format"]);
        Assert.NotNull(props["grammar"]);
        Assert.NotNull(props["json_schema"]);
        Assert.Contains("llama-server", post["description"]!.GetValue<string>());
    }

    [Fact]
    public void Mcp_bodies_come_from_the_real_dtos_and_use_openapi_3_0_nullability()
    {
        JsonObject paths = Doc()["paths"]!.AsObject();
        JsonObject add = paths["/api/v1/mcp/servers"]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!["properties"]!.AsObject();
        Assert.NotNull(add["name"]);
        Assert.NotNull(add["timeoutSeconds"]);

        JsonObject status = paths["/api/v1/mcp/servers/{id}/start"]!["post"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!.AsObject();
        Assert.NotNull(status["properties"]!["connected"]);
        Assert.NotNull(status["properties"]!["stopped"]);
        Assert.DoesNotContain("\"type\":[", paths.ToJsonString());           // no JSON-Schema 2020-12 type arrays in a 3.0.3 document
        Assert.NotNull(paths["/api/v1/mcp/servers/{id}/timeout"]!["post"]!["requestBody"]);
    }

    [Fact]
    public void A_route_without_a_schema_is_still_declared_undescribed()
    {
        JsonObject other = Doc()["paths"]!["/api/v1/something/else"]!["post"]!.AsObject();
        Assert.Null(other["requestBody"]);
        Assert.Null(other["x-parux-body-described"]);
        Assert.Contains("the body is not described", other["responses"]!["default"]!["description"]!.GetValue<string>());
    }
}

/// <summary>Chat Python approval card rules (pure presenter shared with the WinUI app).</summary>
public sealed class ChatCodeApprovalPresenterTests
{
    private static ChatCodeApprovalDto Item(string id, string action, string state, string summary, int minute = 0) =>
        new(id, action, summary, new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc), state);

    [Fact]
    public void Only_pending_run_python_requests_are_shown_oldest_first_and_bounded()
    {
        var items = new List<ChatCodeApprovalDto>
        {
            Item("late", "run_python", "pending", "h\nprint(2)", 5),
            Item("approved", "run_python", "approved", "h\nprint(0)", 1),
            Item("other", "save_file", "pending", "h\nx", 1),
            Item("early", "run_python", "pending", "h\nprint(1)", 2)
        };
        for (int i = 0; i < 10; i++) items.Add(Item("p" + i, "run_python", "pending", "h\nc", 10 + i));

        IReadOnlyList<ChatCodeApprovalView> shown = ChatCodeApprovalPresenter.Pending(items);
        Assert.Equal(ChatCodeApprovalPresenter.MaxCards, shown.Count);
        Assert.Equal("early", shown[0].Id);
        Assert.Equal("late", shown[1].Id);
        Assert.DoesNotContain(shown, v => v.Id is "approved" or "other");
        Assert.Empty(ChatCodeApprovalPresenter.Pending(null));
    }

    [Fact]
    public void The_exact_code_is_shown_and_a_cut_preview_is_flagged()
    {
        ChatCodeApprovalView plain = ChatCodeApprovalPresenter.ToView(Item("a", "run_python", "pending", "Run Python in the PaRuX sandbox (...):\nprint('hi')\nprint('there')"));
        Assert.Equal("print('hi')\nprint('there')", plain.Code);
        Assert.False(plain.Truncated);

        ChatCodeApprovalView cut = ChatCodeApprovalPresenter.ToView(Item("b", "run_python", "pending", "header\nprint(1)\n... (900 more characters)"));
        Assert.True(cut.Truncated);
    }

    [Fact]
    public void Failure_and_state_texts_are_honest_in_both_languages()
    {
        string en = ChatCodeApprovalPresenter.DecisionFailed(true, "HTTP 404", false);
        string it = ChatCodeApprovalPresenter.DecisionFailed(true, "HTTP 404", true);
        Assert.Contains("not run", en);
        Assert.Contains("non è stato eseguito", it);
        Assert.NotEqual(ChatCodeApprovalPresenter.Title(false), ChatCodeApprovalPresenter.Title(true));
        Assert.Contains("failed", ChatCodeApprovalPresenter.RuntimeStateText("failed", null, "disk full", false));
        Assert.Contains("non riuscita", ChatCodeApprovalPresenter.RuntimeStateText("failed", null, "disco pieno", true));
        Assert.Contains("not installed", ChatCodeApprovalPresenter.RuntimeStateText("not-installed", null, null, false));
    }
}

/// <summary>MCP start/stop/reload/timeout lifecycle: real connection attempts, honest states.</summary>
public sealed class McpServerLifecycleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-mcp-life-" + Guid.NewGuid().ToString("N"));

    public McpServerLifecycleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private McpClientRegistry NewRegistry() => new(_dir);

    private static AddMcpServerRequest Missing(string name) => new()
    {
        Name = name, Command = "parux-test-no-such-command-" + Guid.NewGuid().ToString("N"), Args = ""
    };

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(301)]
    public void An_out_of_range_timeout_is_refused(int seconds) =>
        Assert.Throws<InvalidDataException>(() => McpClientRegistry.ValidateTimeout(seconds));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(300)]
    public void Zero_and_the_bounds_are_accepted(int seconds) => Assert.Equal(seconds, McpClientRegistry.ValidateTimeout(seconds));

    [Fact]
    public async Task Timeout_is_stored_per_server_survives_reload_of_the_store_and_is_reported()
    {
        McpClientRegistry registry = NewRegistry();
        McpServerConfig a = registry.AddServer(Missing("a"));
        McpServerConfig b = registry.AddServer(Missing("b"));
        await registry.SetServerTimeoutAsync(a.Id, 45, default);

        McpClientRegistry again = NewRegistry();
        Assert.Equal(45, again.GetConfigs().Single(c => c.Id == a.Id).TimeoutSeconds);
        Assert.Equal(0, again.GetConfigs().Single(c => c.Id == b.Id).TimeoutSeconds);
        McpServerStatus status = (await again.GetStatusesAsync(default)).Single(s => s.Id == a.Id);
        Assert.Equal(45, status.TimeoutSeconds);
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.SetServerTimeoutAsync(a.Id, 2, default));
        await Assert.ThrowsAsync<FileNotFoundException>(() => registry.SetServerTimeoutAsync("nope", 30, default));
    }

    [Fact]
    public async Task Start_reports_a_failed_launch_honestly_instead_of_claiming_it_started()
    {
        McpClientRegistry registry = NewRegistry();
        McpServerConfig server = registry.AddServer(Missing("broken"));
        McpServerStatus status = await registry.StartServerAsync(server.Id, default);
        Assert.False(status.Connected);
        Assert.False(string.IsNullOrWhiteSpace(status.LastError));
        Assert.False(status.Stopped);
    }

    [Fact]
    public async Task Stop_keeps_the_server_disconnected_without_probing_it_until_started_or_reloaded()
    {
        McpClientRegistry registry = NewRegistry();
        McpServerConfig server = registry.AddServer(Missing("held"));
        McpServerStatus stopped = await registry.StopServerAsync(server.Id, default);
        Assert.True(stopped.Stopped);
        Assert.False(stopped.Connected);
        Assert.True(registry.GetConfigs().Single().Enabled);              // configuration untouched

        McpServerStatus listed = (await registry.GetStatusesAsync(default)).Single();
        Assert.True(listed.Stopped);
        Assert.Contains("Stopped by the owner", listed.LastError);        // not a connection error: no launch was attempted

        McpServerStatus started = await registry.StartServerAsync(server.Id, default);
        Assert.False(started.Stopped);
        Assert.NotNull(started.LastError);
        Assert.DoesNotContain("Stopped by the owner", started.LastError);

        await registry.StopServerAsync(server.Id, default);
        McpServerStatus reloaded = await registry.ReloadServerAsync(server.Id, default);
        Assert.False(reloaded.Stopped);
    }

    [Fact]
    public async Task A_disabled_server_cannot_be_started_and_an_unknown_id_is_not_found()
    {
        McpClientRegistry registry = NewRegistry();
        McpServerConfig server = registry.AddServer(Missing("off"));
        await registry.SetEnabledAsync(server.Id, false, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.StartServerAsync(server.Id, default));
        await Assert.ThrowsAsync<FileNotFoundException>(() => registry.StartServerAsync("nope", default));
        await Assert.ThrowsAsync<FileNotFoundException>(() => registry.StopServerAsync("nope", default));
        await Assert.ThrowsAsync<FileNotFoundException>(() => registry.ReloadServerAsync("nope", default));
        await Assert.ThrowsAsync<FileNotFoundException>(() => registry.GetStatusAsync("nope", default));
    }

    [Fact]
    public async Task A_server_that_never_answers_is_cut_off_at_its_own_deadline()
    {
        // powershell stays alive and never speaks MCP: only the per-server deadline can end the attempt.
        McpClientRegistry registry = NewRegistry();
        McpServerConfig server = registry.AddServer(new AddMcpServerRequest
        {
            Name = "silent", Command = "powershell", Args = "-NoProfile -Command Start-Sleep -Seconds 40", TimeoutSeconds = 5
        });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        McpServerStatus status = await registry.StartServerAsync(server.Id, default);
        clock.Stop();
        Assert.False(status.Connected);
        Assert.Contains("within 5 seconds", status.LastError);
        Assert.InRange(clock.Elapsed.TotalSeconds, 4, 20);
        Assert.Equal(5, status.TimeoutSeconds);
    }
}

public sealed class McpLifecycleRouteTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public McpLifecycleRouteTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private HttpClient Owner()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    [Theory]
    [InlineData("GET", "/api/v1/mcp/servers/parux-none")]
    [InlineData("POST", "/api/v1/mcp/servers/parux-none/start")]
    [InlineData("POST", "/api/v1/mcp/servers/parux-none/stop")]
    [InlineData("POST", "/api/v1/mcp/servers/parux-none/reload")]
    public async Task Lifecycle_routes_require_a_token_and_report_an_unknown_server_as_404(string method, string path)
    {
        using HttpClient anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path))).StatusCode);
        using HttpClient owner = Owner();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.SendAsync(new HttpRequestMessage(new HttpMethod(method), path))).StatusCode);
    }

    [Fact]
    public async Task Gateway_refuses_a_bad_constraint_with_400_before_any_provider_is_touched()
    {
        LocalApiServerService localApi = _factory.Services.GetRequiredService<LocalApiServerService>();
        LocalApiCompletionResult result = await localApi.CompleteAsync(
            "{\"model\":\"parux-main\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"grammar\":\"nonsense\"}", CancellationToken.None);
        Assert.Equal(400, result.StatusCode);
        Assert.Contains("GBNF", result.Body);
    }

    [Fact]
    public async Task Timeout_route_validates_the_range_and_the_server()
    {
        using HttpClient owner = Owner();
        HttpResponseMessage unknown = await owner.PostAsJsonAsync("/api/v1/mcp/servers/parux-none/timeout", new SetMcpServerTimeoutRequest { TimeoutSeconds = 30 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        HttpResponseMessage bad = await owner.PostAsJsonAsync("/api/v1/mcp/servers/parux-none/timeout", new SetMcpServerTimeoutRequest { TimeoutSeconds = 9999 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task The_served_openapi_document_describes_the_new_lifecycle_and_gateway_bodies()
    {
        using HttpClient owner = Owner();
        JsonObject doc = JsonNode.Parse(await owner.GetStringAsync("/openapi.json"))!.AsObject();
        JsonObject paths = doc["paths"]!.AsObject();
        Assert.True(paths["/api/v1/mcp/servers/{id}/start"]!["post"]!["x-parux-owner-only"]!.GetValue<bool>());
        Assert.NotNull(paths["/api/v1/mcp/servers/{id}/timeout"]!["post"]!["requestBody"]);
        Assert.NotNull(paths["/v1/chat/completions"]!["post"]!["requestBody"]);
    }
}
