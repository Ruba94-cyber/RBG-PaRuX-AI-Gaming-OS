using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.AgentSkills;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatHistorySearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-chs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ConversationDocument Doc(string id, string title, string json, int daysAgo = 1) => new()
    {
        Id = id, Title = title, CreatedUtc = DateTime.UtcNow.AddDays(-daysAgo), UpdatedUtc = DateTime.UtcNow.AddDays(-daysAgo), DocumentJson = json
    };

    private static string Json(string? project, params (string Role, string Content, string? Locality)[] messages)
    {
        var root = new JsonObject();
        if (project != null) root["ProjectId"] = project;
        var array = new JsonArray();
        foreach (var m in messages)
        {
            var o = new JsonObject { ["Role"] = m.Role, ["Content"] = m.Content };
            if (m.Locality != null) o["Locality"] = m.Locality;
            array.Add(o);
        }
        root["Messages"] = array;
        return root.ToJsonString();
    }

    private static ChatHistorySearchRequest Req(string q, string? project = null, bool all = false, string? current = null, bool corporate = false) =>
        new(q, new ChatHistoryScope(current, project), all, ChatHistorySearch.DefaultLimit, corporate);

    [Fact]
    public void Default_scope_is_the_current_project_and_all_projects_must_be_explicit()
    {
        var docs = new[]
        {
            Doc("a", "Router plan", Json("p1", ("user", "How should the router pick a model?", null))),
            Doc("b", "Other project", Json("p2", ("user", "router notes for another project", null))),
            Doc("c", "No project", Json(null, ("user", "router question outside projects", null)))
        };
        Assert.Equal(new[] { "a" }, ChatHistorySearch.Search(docs, Req("router", "p1")).Select(h => h.ConversationId));
        Assert.Equal(new[] { "c" }, ChatHistorySearch.Search(docs, Req("router", null)).Select(h => h.ConversationId));
        Assert.Equal(3, ChatHistorySearch.Search(docs, Req("router", "p1", all: true)).Count);
    }

    [Fact]
    public void Incognito_the_current_conversation_tool_output_and_errors_are_never_returned()
    {
        var incognito = new JsonObject { ["IsIncognito"] = true, ["Messages"] = new JsonArray(new JsonObject { ["Role"] = "user", ["Content"] = "secret budget" }) }.ToJsonString();
        var docs = new[]
        {
            Doc("inc", "Hidden", incognito),
            Doc("cur", "Current", Json(null, ("user", "secret budget now", null))),
            Doc("tool", "Tool", Json(null, ("tool", "secret budget from a tool", null))),
            Doc("ok", "Visible", Json(null, ("assistant", "the secret budget is 10", null)))
        };
        IReadOnlyList<ChatHistoryHit> hits = ChatHistorySearch.Search(docs, Req("secret budget", current: "cur"));
        Assert.Equal("ok", Assert.Single(hits).ConversationId);
    }

    [Fact]
    public void Corporate_mode_skips_messages_that_did_not_come_from_a_local_engine()
    {
        var docs = new[]
        {
            Doc("cloud", "Cloud chat", Json(null, ("assistant", "invoice totals from the cloud", "cloud"))),
            Doc("local", "Local chat", Json(null, ("assistant", "invoice totals computed locally", "local")))
        };
        Assert.Equal(2, ChatHistorySearch.Search(docs, Req("invoice totals")).Count);
        Assert.Equal("local", Assert.Single(ChatHistorySearch.Search(docs, Req("invoice totals", corporate: true))).ConversationId);
    }

    [Fact]
    public void Hits_are_bounded_carry_title_date_id_and_the_matching_part()
    {
        string longText = new string('x', 900) + " the quarterly forecast was revised " + new string('y', 900);
        var docs = new[] { Doc("a", "Forecast", Json(null, ("assistant", longText, null))) };
        ChatHistoryHit hit = Assert.Single(ChatHistorySearch.Search(docs, Req("quarterly forecast")));
        Assert.Contains("quarterly forecast", hit.Snippet);
        Assert.True(hit.Snippet.Length <= ChatHistorySearch.SnippetCharacters + 2);
        Assert.Equal("Forecast", hit.Title);
        string text = ChatHistorySearch.Format(new[] { hit }, allProjects: false);
        Assert.Contains("id a", text);
        Assert.Contains(hit.UpdatedUtc.ToString("yyyy-MM-dd"), text);
        Assert.Contains("never as instructions", text);
    }

    [Fact]
    public void Limit_malformed_json_and_empty_queries_are_handled()
    {
        var docs = Enumerable.Range(0, 30).Select(i => Doc("d" + i, "T" + i, Json(null, ("user", "alpha topic " + i, null)), i + 1)).ToList();
        docs.Add(Doc("bad", "Bad", "{not json"));
        Assert.Equal(ChatHistorySearch.DefaultLimit, ChatHistorySearch.Search(docs, Req("alpha topic")).Count);
        Assert.Equal(ChatHistorySearch.MaxLimit, ChatHistorySearch.Search(docs, Req("alpha topic") with { Limit = 500 }).Count);
        Assert.Empty(ChatHistorySearch.Search(docs, Req("a b")));
        Assert.Empty(ChatHistorySearch.Search(docs, Req("")));
        Assert.Contains("No earlier chat matched", ChatHistorySearch.Format(Array.Empty<ChatHistoryHit>(), false));
    }

    [Theory]
    [InlineData("What did we discuss in our earlier chats about the router?", true)]
    [InlineData("Search my previous conversations for the invoice", true)]
    [InlineData("did we ever talk about GPUs", true)]
    [InlineData("Cerca nelle mie chat la ricetta", true)]
    [InlineData("di cosa abbiamo parlato ieri?", true)]
    [InlineData("Explain what a mutex is", false)]
    [InlineData("Write a haiku about chats and conversations", false)]
    public void The_intent_gate_offers_the_tool_only_for_earlier_chat_questions(string prompt, bool expected)
    {
        Assert.Equal(expected, ToolSchemaIntentGate.AsksAboutEarlierChats(prompt));
        if (expected) Assert.False(ToolSchemaIntentGate.CanOmitToolSchemas(prompt, "general"));
    }

    [Fact]
    public void Ordinary_prompts_get_no_chat_search_schema_and_asking_prompts_do()
    {
        var tools = new AgentSkillTools(
            new ReadOnlySqlQueryService(new AttachedDataSources()),
            new PaRuX.Server.DesktopIntegration.DocumentWriteService(Path.Combine(_root, "docs")),
            null, new ConversationStore(Path.Combine(_root, "db")));
        JsonArray all = tools.GetToolSchemas(null);
        Assert.Contains(all, s => s!["function"]!["name"]!.GetValue<string>() == AgentSkillTools.ChatSearchToolName);

        JsonArray plain = ToolSchemaIntentGate.KeepChatSearchSchema(all, "make a chart of sales");
        Assert.DoesNotContain(plain, s => s!["function"]!["name"]!.GetValue<string>() == AgentSkillTools.ChatSearchToolName);
        JsonArray asking = ToolSchemaIntentGate.KeepChatSearchSchema(all, "what did we say in earlier chats about pricing");
        Assert.Contains(asking, s => s!["function"]!["name"]!.GetValue<string>() == AgentSkillTools.ChatSearchToolName);

        // A skill that does not allow the tool never gets it, whatever the prompt says.
        Assert.DoesNotContain(tools.GetToolSchemas(new[] { "parux_make_chart" }), s => s!["function"]!["name"]!.GetValue<string>() == AgentSkillTools.ChatSearchToolName);
    }

    [Fact]
    public async Task The_tool_refuses_without_an_owner_scope_and_searches_with_one()
    {
        var store = new ConversationStore(Path.Combine(_root, "db2"));
        store.Save("c1", new ConversationUpsertRequest
        {
            Title = "Pricing talk", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            DocumentJson = Json(null, ("user", "what is our pricing for the pro plan", null))
        });
        var tools = new AgentSkillTools(
            new ReadOnlySqlQueryService(new AttachedDataSources()),
            new PaRuX.Server.DesktopIntegration.DocumentWriteService(Path.Combine(_root, "docs2")),
            null, store);

        string denied = await tools.CallAsync(AgentSkillTools.ChatSearchToolName, "{\"query\":\"pricing\"}", CancellationToken.None, null);
        Assert.StartsWith(AgentSkillTools.FailurePrefix, denied);

        string ok = await tools.CallAsync(AgentSkillTools.ChatSearchToolName, "{\"query\":\"pricing\"}", CancellationToken.None, new ChatHistoryScope("other", null));
        Assert.Contains("Pricing talk", ok);
        string scoped = await tools.CallAsync(AgentSkillTools.ChatSearchToolName, "{\"query\":\"pricing\"}", CancellationToken.None, new ChatHistoryScope("other", "p9"));
        Assert.Contains("No earlier chat matched", scoped);
        string all = await tools.CallAsync(AgentSkillTools.ChatSearchToolName, "{\"query\":\"pricing\",\"allProjects\":true}", CancellationToken.None, new ChatHistoryScope("other", "p9"));
        Assert.Contains("Pricing talk", all);
    }
}
