using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExplicitReasoningAndSamplingTests
{
    private static ChatCompletionRequest Parse(string json) =>
        JsonSerializer.Deserialize<ChatCompletionRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void Explicit_enable_thinking_false_beats_a_hard_coding_prompt()
    {
        var request = Parse("""{"messages":[{"role":"user","content":"Refactor and debug this repository, implement tests"}],"chat_template_kwargs":{"enable_thinking":false}}""");
        var decision = AdaptiveReasoningPolicy.Decide(request);
        Assert.Equal(0, decision.Budget);
        Assert.True(decision.Explicit);
    }

    [Fact]
    public void Explicit_enable_thinking_true_beats_a_trivial_prompt()
    {
        var request = Parse("""{"messages":[{"role":"user","content":"hi"}],"chat_template_kwargs":{"enable_thinking":true}}""");
        Assert.True(AdaptiveReasoningPolicy.Decide(request).Budget > 0);
    }

    [Fact]
    public void Top_level_reasoning_effort_none_disables_thinking()
    {
        var request = Parse("""{"messages":[{"role":"user","content":"debug the compiler"}],"reasoning_effort":"none"}""");
        Assert.Equal(0, AdaptiveReasoningPolicy.Decide(request).Budget);
    }

    [Fact]
    public void Top_k_and_min_p_are_forwarded_and_validated()
    {
        var request = Parse("""{"messages":[{"role":"user","content":"x"}],"top_k":1,"min_p":0.05}""");
        Assert.Null(ChatSamplingOptions.Validate(request));
        var payload = ChatProxyService.BuildProviderPayload(request);
        Assert.Equal(1, (int)payload["top_k"]!);
        Assert.Equal(0.05, (double)payload["min_p"]!);
        Assert.NotNull(ChatSamplingOptions.Validate(Parse("""{"messages":[],"min_p":3}""")));
    }

    [Theory]
    [InlineData("Write a detailed 400-word explanation of how a hash table works, with an example.")]
    [InlineData("Scrivi una spiegazione dettagliata di 300 parole in italiano su come funziona un B-tree.")]
    public void Plain_authoring_prompts_do_not_carry_tool_schemas(string query) =>
        Assert.True(ToolSchemaIntentGate.CanOmitToolSchemas(query, "general"));
}
