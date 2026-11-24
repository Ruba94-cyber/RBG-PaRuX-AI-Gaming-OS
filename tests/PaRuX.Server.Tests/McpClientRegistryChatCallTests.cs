using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class McpClientRegistryChatCallTests
{
    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"a\":1")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public void Invalid_or_non_object_arguments_are_rejected(string json)
    {
        Assert.False(McpClientRegistry.TryParseToolArguments(json, out var arguments));
        Assert.Empty(arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void Empty_input_or_empty_object_means_no_arguments(string? json)
    {
        Assert.True(McpClientRegistry.TryParseToolArguments(json, out var arguments));
        Assert.Empty(arguments);
    }

    [Fact]
    public void Valid_object_is_parsed_with_ordinal_keys()
    {
        Assert.True(McpClientRegistry.TryParseToolArguments("{\"path\":\"a\",\"Path\":\"b\"}", out var arguments));
        Assert.Equal(2, arguments.Count);
    }

    [Fact]
    public void Result_text_is_capped_with_the_coding_agent_limit()
    {
        string capped = McpClientRegistry.CapToolResultText(new string('x', 20_000));
        Assert.EndsWith("[external tool result truncated]", capped, StringComparison.Ordinal);
        Assert.Equal(8_000 + "\n[external tool result truncated]".Length, capped.Length);
        Assert.Equal("short", McpClientRegistry.CapToolResultText("short"));
    }

    [Fact]
    public async Task Chat_call_with_invalid_json_is_rejected_before_any_server_is_tried()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-mcpchat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var registry = new McpClientRegistry(dir);
            ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                registry.CallToolAsync("some_external_tool", "{broken", CancellationToken.None));
            Assert.Contains("not run", ex.Message, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
