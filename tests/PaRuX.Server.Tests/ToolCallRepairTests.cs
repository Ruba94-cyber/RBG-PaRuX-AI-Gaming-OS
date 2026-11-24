using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Replay fixtures of broken tool calls (recorded model output shapes, no model needed): truncated JSON, wrong
/// argument types, XML-style Qwen/Hermes calls, unknown tools, plus the bounded repair loop inside
/// <see cref="StreamingMcpToolLoop"/>.
/// </summary>
public sealed class ToolCallRepairTests
{
    private static JsonArray Tools => (JsonArray)ToolsTemplate.DeepClone();
    private static readonly JsonArray ToolsTemplate = new(
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "search_docs",
                ["parameters"] = JsonNode.Parse("""
                {"type":"object","properties":{
                  "query":{"type":"string","minLength":1},
                  "max_results":{"type":"integer","minimum":1,"maximum":20},
                  "deep":{"type":"boolean"},
                  "scope":{"type":"string","enum":["all","notes","files"]}},
                 "required":["query"],"additionalProperties":false}
                """)
            }
        });

    private static Dictionary<string, JsonObject?> Schemas => ToolCallRepair.SchemasOf(Tools);

    [Fact]
    public void Valid_call_is_untouched_and_keeps_the_original_text()
    {
        ToolCallRepair.Outcome o = ToolCallRepair.Inspect("search_docs", "{\"query\": \"x\"}", Schemas);
        Assert.Equal(ToolCallRepair.Status.Valid, o.Status);
        Assert.Equal("{\"query\": \"x\"}", o.ArgumentsJson);
    }

    [Fact]
    public void Wrong_scalar_types_are_coerced_losslessly()
    {
        ToolCallRepair.Outcome o = ToolCallRepair.Inspect("search_docs",
            "{\"query\":\"x\",\"max_results\":\"5\",\"deep\":\"true\",\"scope\":\"Notes\"}", Schemas);
        Assert.Equal(ToolCallRepair.Status.LocallyRepaired, o.Status);
        JsonObject args = JsonNode.Parse(o.ArgumentsJson)!.AsObject();
        Assert.Equal(5, args["max_results"]!.GetValue<int>());
        Assert.True(args["deep"]!.GetValue<bool>());
        Assert.Equal("notes", args["scope"]!.GetValue<string>());
    }

    [Fact]
    public void Truncated_json_missing_only_closers_is_closed_but_a_cut_string_is_not()
    {
        ToolCallRepair.Outcome closed = ToolCallRepair.Inspect("search_docs", "{\"query\":\"cats\",\"max_results\":3", Schemas);
        Assert.Equal(ToolCallRepair.Status.LocallyRepaired, closed.Status);

        ToolCallRepair.Outcome cut = ToolCallRepair.Inspect("search_docs", "{\"query\":\"cats and do", Schemas);
        Assert.Equal(ToolCallRepair.Status.Invalid, cut.Status);
        Assert.Contains("cut off", cut.Error);
    }

    [Fact]
    public void Fenced_trailing_comma_and_double_encoded_arguments_are_repaired()
    {
        Assert.Equal(ToolCallRepair.Status.LocallyRepaired,
            ToolCallRepair.Inspect("search_docs", "```json\n{\"query\":\"a\",}\n```", Schemas).Status);
        Assert.Equal(ToolCallRepair.Status.LocallyRepaired,
            ToolCallRepair.Inspect("search_docs", "\"{\\\"query\\\":\\\"a\\\"}\"", Schemas).Status);
    }

    [Theory]
    [InlineData("{}", "missing required property 'query'")]
    [InlineData("{\"query\":\"a\",\"max_results\":99}", "above the maximum")]
    [InlineData("{\"query\":\"a\",\"max_results\":2.5}", "expected integer")]
    [InlineData("{\"query\":\"a\",\"scope\":\"web\"}", "must be one of")]
    [InlineData("{\"query\":\"a\",\"extra\":1}", "unexpected property")]
    [InlineData("[1,2]", "must be a JSON object")]
    [InlineData("not json at all", "not valid JSON")]
    public void Schema_violations_are_rejected_with_the_reason(string args, string expectedFragment)
    {
        ToolCallRepair.Outcome o = ToolCallRepair.Inspect("search_docs", args, Schemas);
        Assert.Equal(ToolCallRepair.Status.Invalid, o.Status);
        Assert.Contains(expectedFragment, o.Error);
    }

    [Fact]
    public void Unknown_tool_lists_the_real_ones_and_a_case_variant_is_normalised()
    {
        ToolCallRepair.Outcome unknown = ToolCallRepair.Inspect("delete_everything", "{}", Schemas);
        Assert.Equal(ToolCallRepair.Status.Invalid, unknown.Status);
        Assert.Contains("search_docs", unknown.Error);

        ToolCallRepair.Outcome variant = ToolCallRepair.Inspect("functions.Search-Docs", "{\"query\":\"a\"}", Schemas);
        Assert.Equal("search_docs", variant.Name);
        Assert.NotEqual(ToolCallRepair.Status.Invalid, variant.Status);
    }

    [Fact]
    public void Hermes_json_style_and_qwen_xml_style_text_calls_are_parsed()
    {
        var hermes = ToolCallRepair.ParseTextualCalls(
            "<tool_call>\n{\"name\":\"search_docs\",\"arguments\":{\"query\":\"x\"}}\n</tool_call>", out string rest1);
        Assert.Equal("search_docs", Assert.Single(hermes).Name);
        Assert.Null(hermes[0].ParseError);
        Assert.Equal("", rest1);

        var qwen = ToolCallRepair.ParseTextualCalls(
            "Sure.<tool_call><function=search_docs><parameter=query>cats</parameter><parameter=max_results>4</parameter></function></tool_call>",
            out string rest2);
        Assert.Equal("Sure.", rest2);
        ToolCallRepair.Outcome o = ToolCallRepair.Inspect(qwen[0].Name, qwen[0].RawArguments, Schemas);
        Assert.Equal(ToolCallRepair.Status.LocallyRepaired, o.Status);   // "4" -> 4
    }

    [Fact]
    public void Truncated_text_call_is_flagged_not_executed()
    {
        var calls = ToolCallRepair.ParseTextualCalls("<tool_call>{\"name\":\"search_docs\",\"arguments\":{\"query\":\"x", out _);
        Assert.NotNull(Assert.Single(calls).ParseError);
    }

    [Fact]
    public void Prose_that_merely_mentions_markup_is_not_a_text_call()
    {
        Assert.Empty(ToolCallRepair.ParseTextualCalls("Use the tag like this: tool_call in XML.", out _));
        Assert.False(ToolCallRepair.CouldStartTextualCall("Hello"));
        Assert.True(ToolCallRepair.CouldStartTextualCall("<tool"));
    }

    // ----------------------------------------------------------------- loop behaviour

    private sealed class Rig
    {
        public List<string> Frames { get; } = new();
        public List<(string Name, string Args)> Invocations { get; } = new();
        public List<JsonObject> Requests { get; } = new();
        public Queue<string> Turns { get; } = new();
        public Func<string, string, string> Tool = (n, _) => "ok:" + n;

        public async Task RunAsync()
        {
            var payload = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }) };
            var loop = new StreamingMcpToolLoop(
                payload, Tools,
                open: (body, _) =>
                {
                    Requests.Add((JsonObject)body.DeepClone());
                    string sse = Turns.Dequeue();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8) });
                },
                invokeTool: (n, a, _) => { Invocations.Add((n, a)); return Task.FromResult(Tool(n, a)); },
                writeFrame: (f, _) => { Frames.Add(f); return Task.CompletedTask; },
                4, 8, 16_000);
            HttpResponseMessage? first = await loop.OpenFirstAsync(default);
            await loop.RunAsync(first!, default);
        }

        public string Text() => string.Concat(Frames
            .Where(f => f.StartsWith("data: {") && !f.Contains("parux_event"))
            .Select(f => JsonNode.Parse(f[6..])?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>()));
    }

    private static string Sse(params string[] lines) => string.Join("\n\n", lines) + "\n\n";
    private static string Content(string text) => "data: " + new JsonObject
    { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text } }) }.ToJsonString();
    private static string Finish(string reason) => "data: " + new JsonObject
    { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject(), ["finish_reason"] = reason }) }.ToJsonString();
    private static string Call(string id, string name, string args) => "data: " + new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject
        {
            ["delta"] = new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0, ["id"] = id,
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args }
                })
            }
        })
    }.ToJsonString();
    private const string Done = "data: [DONE]";

    [Fact]
    public async Task Invalid_call_is_not_executed_error_is_fed_back_and_the_corrected_retry_runs()
    {
        var rig = new Rig();
        rig.Turns.Enqueue(Sse(Call("c1", "search_docs", "{\"max_results\":3}"), Finish("tool_calls"), Done));
        rig.Turns.Enqueue(Sse(Call("c2", "search_docs", "{\"query\":\"cats\"}"), Finish("tool_calls"), Done));
        rig.Turns.Enqueue(Sse(Content("Found."), Finish("stop"), Done));
        await rig.RunAsync();

        Assert.Equal(("search_docs", "{\"query\":\"cats\"}"), Assert.Single(rig.Invocations));
        JsonArray second = rig.Requests[1]["messages"]!.AsArray();
        string feedback = second[2]!["content"]!.GetValue<string>();
        Assert.Contains("NOT executed", feedback);
        Assert.Contains("missing required property 'query'", feedback);
        Assert.Contains("repair attempt", feedback);
        Assert.Contains(rig.Frames, f => f.Contains("tool_repair") && f.Contains("reprompted"));
        Assert.Equal("Found.", rig.Text());
    }

    [Fact]
    public async Task Repair_is_bounded_to_two_rounds_then_tools_are_withdrawn_and_failure_is_honest()
    {
        var rig = new Rig();
        for (int i = 0; i < 3; i++)
            rig.Turns.Enqueue(Sse(Call("c" + i, "search_docs", "{}"), Finish("tool_calls"), Done));
        rig.Turns.Enqueue(Sse(Content("I could not run the search."), Finish("stop"), Done));
        await rig.RunAsync();

        Assert.Empty(rig.Invocations);
        Assert.Equal(4, rig.Requests.Count);                       // 3 failing turns + the tool-less answer
        Assert.NotNull(rig.Requests[2]["tools"]);
        Assert.Null(rig.Requests[3]["tools"]);
        string last = rig.Requests[3]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
        Assert.Contains("exhausted", last);
        Assert.Contains(rig.Frames, f => f.Contains("tool_repair") && f.Contains("exhausted"));
        Assert.Equal("I could not run the search.", rig.Text());
    }

    [Fact]
    public async Task Locally_fixable_call_runs_with_the_repaired_arguments_and_no_reprompt()
    {
        var rig = new Rig();
        rig.Turns.Enqueue(Sse(Call("c1", "search_docs", "{\"query\":\"cats\",\"max_results\":\"3\"}"), Finish("tool_calls"), Done));
        rig.Turns.Enqueue(Sse(Content("ok"), Finish("stop"), Done));
        await rig.RunAsync();

        (string _, string args) = Assert.Single(rig.Invocations);
        Assert.Equal(3, JsonNode.Parse(args)!["max_results"]!.GetValue<int>());
        Assert.DoesNotContain(rig.Frames, f => f.Contains("tool_repair"));
        Assert.Equal(2, rig.Requests.Count);
    }

    [Fact]
    public async Task Xml_style_call_in_the_text_is_recovered_executed_and_never_shown()
    {
        var rig = new Rig();
        rig.Turns.Enqueue(Sse(
            Content("<tool"), Content("_call>{\"name\":\"search_docs\","), Content("\"arguments\":{\"query\":\"cats\"}}</tool_call>"),
            Finish("stop"), Done));
        rig.Turns.Enqueue(Sse(Content("Answer."), Finish("stop"), Done));
        await rig.RunAsync();

        Assert.Equal("search_docs", Assert.Single(rig.Invocations).Name);
        Assert.Equal("Answer.", rig.Text());
        Assert.DoesNotContain(rig.Frames, f => f.Contains("tool_call>"));
        JsonObject assistant = rig.Requests[1]["messages"]![1]!.AsObject();
        Assert.Equal("search_docs", assistant["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Truncated_xml_call_is_rejected_with_a_reason_instead_of_being_shown_or_run()
    {
        var rig = new Rig();
        rig.Turns.Enqueue(Sse(Content("<tool_call>{\"name\":\"search_docs\",\"arguments\":{\"query\":\"ca"), Finish("length"), Done));
        rig.Turns.Enqueue(Sse(Content("Sorry."), Finish("stop"), Done));
        await rig.RunAsync();

        Assert.Empty(rig.Invocations);
        string feedback = rig.Requests[1]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
        Assert.Contains("NOT executed", feedback);
        Assert.DoesNotContain(rig.Frames, f => f.Contains("tool_call>"));
    }

    [Fact]
    public async Task Ordinary_answer_starting_with_an_angle_bracket_is_streamed_unchanged()
    {
        var rig = new Rig();
        rig.Turns.Enqueue(Sse(Content("<"), Content("div> is an HTML tag."), Finish("stop"), Done));
        await rig.RunAsync();

        Assert.Equal("<div> is an HTML tag.", rig.Text());
        Assert.Empty(rig.Invocations);
    }

    [Fact]
    public async Task A_tool_that_keeps_failing_is_not_executed_a_third_time()
    {
        var rig = new Rig { Tool = (_, _) => "Tool call failed: backend down" };
        for (int i = 0; i < 3; i++)
            rig.Turns.Enqueue(Sse(Call("c" + i, "search_docs", "{\"query\":\"q" + i + "\"}"), Finish("tool_calls"), Done));
        rig.Turns.Enqueue(Sse(Content("It failed."), Finish("stop"), Done));
        await rig.RunAsync();

        Assert.Equal(2, rig.Invocations.Count);
        string third = rig.Requests[3]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
        Assert.Contains("already failed 2 times", third);
    }

    [Fact]
    public async Task Valid_and_invalid_parallel_calls_each_get_a_tool_message_in_order()
    {
        var rig = new Rig();
        rig.Turns.Enqueue(Sse(
            "data: " + new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["delta"] = new JsonObject
                    {
                        ["tool_calls"] = new JsonArray(
                            new JsonObject { ["index"] = 0, ["id"] = "a", ["function"] = new JsonObject { ["name"] = "search_docs", ["arguments"] = "{\"query\":\"ok\"}" } },
                            new JsonObject { ["index"] = 1, ["id"] = "b", ["function"] = new JsonObject { ["name"] = "search_docs", ["arguments"] = "{}" } })
                    }
                })
            }.ToJsonString(),
            Finish("tool_calls"), Done));
        rig.Turns.Enqueue(Sse(Content("done"), Finish("stop"), Done));
        await rig.RunAsync();

        Assert.Single(rig.Invocations);
        JsonArray msgs = rig.Requests[1]["messages"]!.AsArray();
        Assert.Equal("a", msgs[2]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("b", msgs[3]!["tool_call_id"]!.GetValue<string>());
        Assert.Contains("NOT executed", msgs[3]!["content"]!.GetValue<string>());
    }
}
