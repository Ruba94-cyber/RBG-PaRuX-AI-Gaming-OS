using System.Text;
using System.Text.Json.Nodes;
using PaRuX.Services;
using Xunit;

public sealed class CodingAgentPrefixCacheTests
{
    private static string Source(params string[] relative)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(Path.Combine(relative));
    }

    private static string Section(IReadOnlyList<string> log)
    {
        var sb = new StringBuilder("Task: fix\n\n");
        CodingAgentPromptBuilder.AppendEvidenceSection(sb, log.Count, log);
        return sb.ToString();
    }

    [Fact]
    public void Evidence_replay_only_appends_so_the_previous_turn_is_a_prefix_of_the_next()
    {
        var log = new List<string>();
        string previousBody = string.Empty;
        for (int turn = 0; turn < 6; turn++)
        {
            string text = Section(log);
            // Everything up to and including the last evidence entry must be identical to the previous turn's.
            int cut = text.LastIndexOf("\n\n(", StringComparison.Ordinal);
            string body = text[..cut];
            if (turn > 1) Assert.StartsWith(previousBody, body, StringComparison.Ordinal);
            previousBody = body;
            log.Add("observation " + turn + " with some file text");
        }
    }

    [Fact]
    public void Evidence_header_is_constant_and_total_count_is_still_shown()
    {
        string text = Section(new[] { "a", "b", "c" });
        Assert.Contains("Recent execution evidence:\na\n---\nb\n---\nc", text);
        Assert.EndsWith("(3 total observation(s) so far.)", text);
    }

    [Fact]
    public void Structured_direct_requests_ask_for_prompt_cache_and_chunk_reuse_without_overriding_callers()
    {
        var payload = new JsonObject { ["model"] = "parux-coding" };
        StructuredChatCacheHints.Apply(payload);
        Assert.True(payload["cache_prompt"]!.GetValue<bool>());
        Assert.Equal(256, payload["n_cache_reuse"]!.GetValue<int>());

        var explicitOff = new JsonObject { ["cache_prompt"] = false };
        StructuredChatCacheHints.Apply(explicitOff);
        Assert.False(explicitOff["cache_prompt"]!.GetValue<bool>());
    }

    [Fact]
    public void Coding_service_fixes_the_resumed_flag_once_and_scopes_the_cache_to_the_session()
    {
        string service = Source("src", "PaRuX.App", "Services", "CodingAgentService.cs");
        Assert.Contains("bool resumedRun = checkpoint.StepsTaken > 0;", service);
        Assert.DoesNotContain("checkpoint.StepsTaken > 0,", service); // the per-turn flip that rewrote the prompt prefix
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(service, @"cacheScope: resolvedSessionId").Count);
        string client = Source("src", "PaRuX.App", "Services", "StructuredChatClient.cs");
        Assert.Contains("StructuredChatCacheHints.Apply(directPayload);", client);
        Assert.Contains("ConversationId = string.IsNullOrWhiteSpace(cacheScope)", client);
    }
}
