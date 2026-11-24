using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatTokenUsageTests
{
    // Shapes taken from llama-server's OpenAI-compatible stream (final timings chunk and the include_usage frame).
    private const string TimingsFrame =
        "{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"model\":\"m\","
        + "\"timings\":{\"cache_n\":40,\"prompt_n\":10,\"prompt_ms\":50.5,\"prompt_per_second\":198.0,\"predicted_n\":25,\"predicted_ms\":600.0,\"predicted_per_second\":41.6}}";
    private const string UsageFrame =
        "{\"choices\":[],\"model\":\"m\",\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":25,\"total_tokens\":75}}";

    [Fact]
    public void Usage_frame_gives_the_servers_own_counts()
    {
        ChatTokenUsage? u = ChatTokenUsageParser.TryParse(UsageFrame);
        Assert.NotNull(u);
        Assert.Equal(50, u!.PromptTokens);
        Assert.Equal(25, u.CompletionTokens);
        Assert.Equal(75, u.TotalTokens);
        Assert.Null(u.CompletionTokensPerSecond);
    }

    [Fact]
    public void Timings_frame_counts_the_cached_prefix_in_the_prompt_and_reports_speed()
    {
        ChatTokenUsage? u = ChatTokenUsageParser.TryParse(TimingsFrame);
        Assert.NotNull(u);
        Assert.Equal(50, u!.PromptTokens); // 10 evaluated + 40 from the prompt cache
        Assert.Equal(40, u.CachedPromptTokens);
        Assert.Equal(25, u.CompletionTokens);
        Assert.Equal(41.6, u.CompletionTokensPerSecond);
    }

    [Fact]
    public void Merging_the_two_final_frames_keeps_server_counts_and_speed()
    {
        ChatTokenUsage merged = ChatTokenUsageParser.Merge(ChatTokenUsageParser.TryParse(TimingsFrame), ChatTokenUsageParser.TryParse(UsageFrame)!);
        Assert.Equal(50, merged.PromptTokens);
        Assert.Equal(25, merged.CompletionTokens);
        Assert.Equal(41.6, merged.CompletionTokensPerSecond);
        Assert.Equal(40, merged.CachedPromptTokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json \"usage\"")]
    [InlineData("{\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}")]
    [InlineData("{\"usage\":{\"prompt_tokens\":-1,\"completion_tokens\":\"x\"}}")]
    [InlineData("{\"timings\":{\"predicted_per_second\":0}}")]
    [InlineData("[1,2]")]
    public void Frames_without_real_numbers_yield_nothing_never_a_guess(string? data) =>
        Assert.Null(ChatTokenUsageParser.TryParse(data));

    [Fact]
    public void Line_says_in_out_speed_and_cache_in_both_languages_and_is_null_without_data()
    {
        var usage = new ChatTokenUsage { PromptTokens = 1234, CompletionTokens = 56, CompletionTokensPerSecond = 41.64, CachedPromptTokens = 1000 };
        Assert.Equal("1,234 in · 56 out · 41.6 tok/s · 1,000 cached", ChatTokenUsageFormatter.FormatLine(usage, false));
        Assert.Equal("1,234 in ingresso · 56 in uscita · 41.6 tok/s · 1,000 dalla cache", ChatTokenUsageFormatter.FormatLine(usage, true));
        Assert.Null(ChatTokenUsageFormatter.FormatLine(null, false));
        Assert.Null(ChatTokenUsageFormatter.FormatLine(new ChatTokenUsage(), true));
    }

    [Fact]
    public void Report_totals_only_replies_with_data_and_admits_the_rest()
    {
        var replies = new ChatTokenUsage?[]
        {
            new() { PromptTokens = 100, CompletionTokens = 20 }, null, new() { PromptTokens = 300, CompletionTokens = 80 }
        };
        ChatUsageSummary summary = ChatTokenUsageFormatter.Summarize(replies);
        Assert.Equal(3, summary.Replies);
        Assert.Equal(2, summary.RepliesWithUsage);
        Assert.Equal(400, summary.PromptTokens);
        Assert.Equal(100, summary.CompletionTokens);

        string en = ChatTokenUsageFormatter.BuildReport(summary, replies[2], false);
        Assert.Contains("400 tokens in", en, StringComparison.Ordinal);
        Assert.Contains("1 without server data", en, StringComparison.Ordinal);
        Assert.Contains("not estimated", en, StringComparison.Ordinal);
        string it = ChatTokenUsageFormatter.BuildReport(summary, replies[2], true);
        Assert.Contains("senza dati dal server", it, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_with_no_server_data_says_so_instead_of_inventing_zeros()
    {
        ChatUsageSummary none = ChatTokenUsageFormatter.Summarize(new ChatTokenUsage?[] { null, null });
        string text = ChatTokenUsageFormatter.BuildReport(none, null, false);
        Assert.StartsWith("No token usage yet", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0 tokens", text, StringComparison.Ordinal);
    }
}

/// <summary>Pins the real wiring (server asks for usage, client parses and stores it, /usage is local, indicator is Advanced-only).</summary>
public sealed class ChatTokenUsageSourceContractTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void Server_requests_the_usage_frame_only_from_the_managed_local_runtime()
    {
        string source = Read("src", "PaRuX.Server", "ChatProxyService.cs");
        Assert.Contains("[\"stream_options\"] = new JsonObject { [\"include_usage\"] = true }", source, StringComparison.Ordinal);
        int guard = source.IndexOf("if (!_runtime.UsesExternalProvider)\n            streamPayload[\"stream_options\"]", StringComparison.Ordinal);
        int guardCrLf = source.IndexOf("if (!_runtime.UsesExternalProvider)\r\n            streamPayload[\"stream_options\"]", StringComparison.Ordinal);
        Assert.True(guard >= 0 || guardCrLf >= 0, "include_usage must be limited to the managed local runtime");
    }

    [Fact]
    public void Client_parses_usage_session_stores_it_and_slash_usage_never_reaches_the_model()
    {
        Assert.Contains("ChatTokenUsageParser.TryParse(data)", Read("src", "PaRuX.App", "Services", "ChatClient.cs"), StringComparison.Ordinal);
        Assert.Contains("reply.Usage = PaRuX.Intelligence.ChatTokenUsageParser.Merge(reply.Usage, usage)", Read("src", "PaRuX.App", "Services", "ChatSession.cs"), StringComparison.Ordinal);
        string chat = Read("src", "PaRuX.App", "Views", "ChatView.xaml.cs");
        int usageCommand = chat.IndexOf("\"/usage\"", StringComparison.Ordinal);
        int send = chat.IndexOf("ChatSession.Current.SendAsync(", usageCommand, StringComparison.Ordinal);
        Assert.True(usageCommand > 0 && send > usageCommand);
        Assert.Contains("_ = ShowUsageReportAsync();\n                return;", chat.Replace("\r\n", "\n"), StringComparison.Ordinal);
        // Normal mode shows no per-reply line; Advanced does.
        Assert.Contains("_isSimpleMode ? null", chat, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}
