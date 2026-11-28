using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.AgentSkills;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatSettingProposalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-csp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AgentSkillTools Tools(ChatSettingProposalService proposals) => new(
        new ReadOnlySqlQueryService(new AttachedDataSources()),
        new PaRuX.Server.DesktopIntegration.DocumentWriteService(Path.Combine(_root, "docs")),
        null, new ConversationStore(Path.Combine(_root, "db")), proposals);

    [Theory]
    [InlineData("theme", "dark")]
    [InlineData("THEME", "Light")]
    [InlineData("language", "it")]
    [InlineData("interface", "advanced")]
    [InlineData("textProfile", "standard")]
    [InlineData("webSearch", "off")]
    [InlineData("voiceAutoSend", "on")]
    public void Allow_listed_settings_with_allowed_values_validate(string key, string value)
    {
        Assert.True(ChatSettingsAllowList.TryValidate(key, value, corporate: false, out ChatSettingChange? change, out string error), error);
        Assert.Equal(key.ToLowerInvariant(), change!.Key.ToLowerInvariant());
    }

    [Theory]
    [InlineData("pairing", "on")]
    [InlineData("lanAccess", "on")]
    [InlineData("remoteAccess", "on")]
    [InlineData("corporateMode", "off")]
    [InlineData("autoUpdate", "on")]
    [InlineData("gamingHardOff", "off")]
    [InlineData("apiKey", "abc")]
    [InlineData("modelDownloadPath", "C:\\x")]
    [InlineData("backupRestore", "on")]
    [InlineData("", "on")]
    [InlineData("theme", "purple")]
    [InlineData("theme", "")]
    [InlineData("webSearch", "maybe")]
    public void Anything_outside_the_allow_list_or_with_a_bad_value_is_refused(string key, string value)
    {
        Assert.False(ChatSettingsAllowList.TryValidate(key, value, corporate: false, out ChatSettingChange? change, out string error));
        Assert.Null(change);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void The_allow_list_contains_no_security_network_corporate_update_or_gaming_key()
    {
        // The list is closed: exactly these keys, so adding a sensitive one is a deliberate, test-breaking change.
        Assert.Equal(new[] { "interface", "language", "textProfile", "theme", "voiceAutoSend", "webSearch" },
            ChatSettingsAllowList.All.Select(d => d.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Corporate_mode_locks_interface_profile_and_web_search()
    {
        foreach (string key in new[] { "interface", "textProfile", "webSearch" })
        {
            string value = key == "interface" ? "advanced" : key == "textProfile" ? "uncensored" : "on";
            Assert.False(ChatSettingsAllowList.TryValidate(key, value, corporate: true, out _, out string error));
            Assert.Contains("Corporate", error);
        }
        Assert.True(ChatSettingsAllowList.TryValidate("theme", "dark", corporate: true, out _, out _));
    }

    [Fact]
    public async Task The_tool_only_creates_a_pending_proposal_and_never_applies_anything()
    {
        var proposals = new ChatSettingProposalService();
        AgentSkillTools tools = Tools(proposals);

        string result = await tools.CallAsync(AgentSkillTools.ProposeSettingToolName, "{\"setting\":\"theme\",\"value\":\"dark\"}",
            CancellationToken.None, new ChatHistoryScope("conv1", null));

        Assert.DoesNotContain(AgentSkillTools.FailurePrefix, result);
        Assert.Contains("NOTHING has changed yet", result);
        ChatSettingProposalDto pending = Assert.Single(proposals.List("conv1"));
        Assert.Equal("theme", pending.Key);
        Assert.Equal("dark", pending.Value);
        Assert.Empty(proposals.List("conv2"));
    }

    [Fact]
    public async Task The_tool_refuses_forbidden_settings_and_callers_without_an_owner_scope()
    {
        var proposals = new ChatSettingProposalService();
        AgentSkillTools tools = Tools(proposals);
        string refused = await tools.CallAsync(AgentSkillTools.ProposeSettingToolName, "{\"setting\":\"pairing\",\"value\":\"on\"}",
            CancellationToken.None, new ChatHistoryScope("conv1", null));
        Assert.StartsWith(AgentSkillTools.FailurePrefix, refused);
        string noScope = await tools.CallAsync(AgentSkillTools.ProposeSettingToolName, "{\"setting\":\"theme\",\"value\":\"dark\"}", CancellationToken.None, null);
        Assert.StartsWith(AgentSkillTools.FailurePrefix, noScope);
        string noChat = await tools.CallAsync(AgentSkillTools.ProposeSettingToolName, "{\"setting\":\"theme\",\"value\":\"dark\"}",
            CancellationToken.None, new ChatHistoryScope(null, null));
        Assert.StartsWith(AgentSkillTools.FailurePrefix, noChat);
        Assert.Empty(proposals.List("conv1"));
    }

    [Fact]
    public void Proposals_are_replaced_cancelled_and_expire()
    {
        DateTime now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var proposals = new ChatSettingProposalService(() => now);
        var first = proposals.Propose("c", new ChatSettingChange("theme", "dark", "App theme"));
        var second = proposals.Propose("c", new ChatSettingChange("language", "it", "Interface language"));
        Assert.Equal(second.Id, Assert.Single(proposals.List("c")).Id);
        Assert.False(proposals.Delete("c", first.Id));          // stale id
        Assert.False(proposals.Delete("other", second.Id));     // wrong conversation
        Assert.True(proposals.Delete("c", second.Id));
        Assert.Empty(proposals.List("c"));

        proposals.Propose("c", new ChatSettingChange("theme", "dark", "App theme"));
        now = now.AddMinutes(11);
        Assert.Empty(proposals.List("c"));
        Assert.Throws<InvalidOperationException>(() => proposals.Propose("", new ChatSettingChange("theme", "dark", "x")));
    }

    [Theory]
    [InlineData("Please switch to dark mode", true)]
    [InlineData("change the language to Italian", true)]
    [InlineData("turn off web search", true)]
    [InlineData("passa al tema scuro", true)]
    [InlineData("attiva la modalità avanzata", true)]
    [InlineData("What is the capital of France?", false)]
    [InlineData("Explain dark matter", false)]
    public void The_intent_gate_offers_the_setting_tool_only_when_asked_to_change_a_setting(string prompt, bool expected)
    {
        Assert.Equal(expected, ToolSchemaIntentGate.AsksToChangeSettings(prompt));
    }

    [Fact]
    public void Schemas_are_offered_per_intent_and_never_when_the_skill_does_not_allow_them()
    {
        AgentSkillTools tools = Tools(new ChatSettingProposalService());
        JsonArray all = tools.GetToolSchemas(null);
        bool Has(JsonArray a, string n) => a.Any(s => s!["function"]!["name"]!.GetValue<string>() == n);

        Assert.True(Has(all, AgentSkillTools.ProposeSettingToolName));
        Assert.False(Has(ToolSchemaIntentGate.KeepChatSearchSchema(all, "make a chart of sales"), AgentSkillTools.ProposeSettingToolName));
        Assert.True(Has(ToolSchemaIntentGate.KeepChatSearchSchema(all, "switch to dark mode"), AgentSkillTools.ProposeSettingToolName));
        Assert.False(Has(ToolSchemaIntentGate.KeepChatSearchSchema(all, "switch to dark mode"), AgentSkillTools.ChatSearchToolName));
        Assert.False(Has(tools.GetToolSchemas(new[] { "parux_make_chart" }), AgentSkillTools.ProposeSettingToolName));
        Assert.False(ToolSchemaIntentGate.CanOmitToolSchemas("switch to dark mode", "general"));
    }
}
