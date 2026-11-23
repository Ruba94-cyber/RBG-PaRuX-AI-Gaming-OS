using System.Text.Json.Nodes;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class McpToolCallAdmissionTests
{
    private static readonly JsonArray AnnouncedTools = new(
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = "search_docs" }
        });

    private static IReadOnlySet<string> Names() => McpToolCallAdmission.AdvertisedNames(AnnouncedTools);

    [Fact]
    public void Exact_advertised_tool_with_valid_json_preserves_original_arguments()
    {
        var r = McpToolCallAdmission.Inspect("search_docs", "{ \"query\": \"text\" }", Names());
        Assert.True(r.Allowed);
        Assert.Equal("{ \"query\": \"text\" }", r.ArgumentsJson);
    }

    [Fact]
    public void Innocent_json_code_fence_and_trailing_comma_are_normalized_without_guessing()
    {
        var r = McpToolCallAdmission.Inspect("search_docs", "```json\n{\"query\":\"cats\",}\n```", Names());
        Assert.True(r.Allowed);
        Assert.Equal("{\"query\":\"cats\"}", r.ArgumentsJson);
    }

    [Theory]
    [InlineData("SEARCH_DOCS", "{}")]
    [InlineData("functions.search_docs", "{}")]
    [InlineData("nonexistent", "{}")]
    [InlineData("search_docs", "[1,2]")]
    [InlineData("search_docs", "{\"q\":")]
    [InlineData("search_docs", "{\"q\":\"truncated}")]
    [InlineData("search_docs", "{\"a\":1,\"a\":2}")]
    [InlineData("search_docs", "```python\n{}\n```")]
    public void Unknown_or_ambiguous_calls_fail_closed(string name, string args)
    {
        Assert.False(McpToolCallAdmission.Inspect(name, args, Names()).Allowed);
    }

    [Fact]
    public void Oversized_tool_arguments_are_never_executed()
    {
        string oversized = "{\"prompt\":\"" + new string('a', McpToolCallAdmission.MaxArgumentsCharacters) + "\"}";
        var r = McpToolCallAdmission.Inspect("search_docs", oversized, Names());
        Assert.False(r.Allowed);
        Assert.Contains("size", r.Error);
    }

    [Fact]
    public void Missing_arguments_are_accepted_as_empty_object_for_parameterless_tools()
    {
        var r = McpToolCallAdmission.Inspect("search_docs", "", Names());
        Assert.True(r.Allowed);
        Assert.Equal("{}", r.ArgumentsJson);
    }
}
