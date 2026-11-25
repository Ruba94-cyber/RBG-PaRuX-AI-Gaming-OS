using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentDiscoverQuickResumeToolsTests
{
    private static readonly DateTime EdenStart = new(2026, 11, 4, 8, 30, 0, DateTimeKind.Utc);

    private class FakeBackend : IAgentLibraryBackend
    {
        public bool GamingActive { get; set; }
        public List<string> Calls = new();
        public List<QuickResumeAppDto> Apps = new()
        {
            new(4242, "eden", @"Eden - C:\Users\paolo\Games\mk8.nsp", EdenStart, false, true, null),
            new(4300, "notepad", "Notes", EdenStart.AddHours(1), true, true, null),
            new(900, "explorer", "Files", EdenStart, false, false, "Windows and security components cannot be paused."),
        };

        public IReadOnlyList<GameTitleDto> Titles() => Array.Empty<GameTitleDto>();
        public bool SetFavorite(string k, bool f) => true;
        public bool SetTags(string k, IReadOnlyList<string> t) => true;
        public bool SetCollections(string k, IReadOnlyList<string> c) => true;
        public Task<ManualGameLaunchResult> LaunchAsync(GameInstallationDto i, CancellationToken ct) => throw new NotSupportedException();
        public bool RemoveManualEntry(string id) => true;
        public string EnqueueDownload(string url, string? f, string? s) => "dl";

        public Task<QuickResumeStatusDto> QuickResumeListAsync(CancellationToken ct) => Task.FromResult(new QuickResumeStatusDto(Apps));
        public Task<QuickResumeActionResult> QuickResumeSuspendAsync(QuickResumeActionRequest r, CancellationToken ct)
        { Calls.Add($"pause:{r.ProcessId}"); return Task.FromResult(new QuickResumeActionResult(true, "eden is paused.")); }
        public Task<QuickResumeActionResult> QuickResumeResumeAsync(QuickResumeActionRequest r, CancellationToken ct)
        { Calls.Add($"resume:{r.ProcessId}"); return Task.FromResult(new QuickResumeActionResult(true, "notepad is running again.")); }
    }

    private static (AgentLibraryTools Tools, FakeBackend Backend, AgentLibraryConsentStore Consent) Make()
    {
        var backend = new FakeBackend();
        var consent = new AgentLibraryConsentStore();
        return (new AgentLibraryTools(backend, consent), backend, consent);
    }

    private static Task<string> Call(AgentLibraryTools t, string tool, string json, bool bypass = false) =>
        t.CallAsync(tool, json, CancellationToken.None, null, bypass);

    private static string IdFrom(string text)
    {
        int at = text.IndexOf("confirmation_id \"", StringComparison.Ordinal) + "confirmation_id \"".Length;
        return text[at..text.IndexOf('"', at)];
    }

    private static string Started(DateTime d) => d.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
    private static string PauseJson() => "{\"process_id\":4242,\"started_utc\":\"" + Started(EdenStart) + "\"}";
    private static string ResumeJson() => "{\"process_id\":4300,\"started_utc\":\"" + Started(EdenStart.AddHours(1)) + "\"}";
    private static string WithId(string json, string id) => json.TrimEnd('}') + ",\"confirmation_id\":\"" + id + "\"}";

    [Fact]
    public void New_tools_are_registered_with_closed_bounded_schemas_and_correct_permission_classes()
    {
        var (tools, _, _) = Make();
        var byName = tools.GetToolSchemas().ToDictionary(s => s!["function"]!["name"]!.GetValue<string>(), s => s!["function"]!["parameters"]!);
        foreach (string n in new[] { "quick_resume_list", "quick_resume_pause", "quick_resume_resume" })
        {
            Assert.True(byName.ContainsKey(n));
            Assert.True(AgentLibraryTools.IsBuiltInTool(n));
            Assert.False(byName[n]["additionalProperties"]!.GetValue<bool>());
        }
        Assert.Equal(1, byName["quick_resume_pause"]["properties"]!["process_id"]!["minimum"]!.GetValue<int>());
        Assert.Contains("process_id", byName["quick_resume_resume"]["required"]!.AsArray().Select(x => x!.GetValue<string>()));

        Assert.False(AgentLibraryTools.RequiresConfirmation("quick_resume_list"));
        Assert.All(new[] { "quick_resume_pause", "quick_resume_resume" }, n => Assert.True(AgentLibraryTools.RequiresConfirmation(n)));
    }

    [Fact]
    public async Task Quick_resume_list_shows_identity_and_state_without_absolute_paths()
    {
        var (tools, _, _) = Make();
        string r = await Call(tools, "quick_resume_list", "{}");
        Assert.Contains("eden process_id 4242 started_utc " + Started(EdenStart), r);
        Assert.Contains("PAUSED", r);
        Assert.Contains("cannot be paused", r);
        Assert.DoesNotContain(@"C:\", r);
        Assert.Contains("[path]", r);
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == "quick_resume_list" && e.Outcome == "success");
    }

    [Theory]
    [InlineData("quick_resume_pause")]
    [InlineData("quick_resume_resume")]
    public async Task Pause_and_resume_need_a_single_use_confirmation_even_in_bypass_mode(string tool)
    {
        var (tools, b, consent) = Make();
        string json = tool == "quick_resume_pause" ? PauseJson() : ResumeJson();

        string first = await Call(tools, tool, json, bypass: true);
        Assert.Contains("Confirmation required", first);
        Assert.Empty(b.Calls);
        Assert.Contains("Confirmation required", await Call(tools, tool, WithId(json, "deadbeef"), bypass: true));
        Assert.Empty(b.Calls);

        string id = IdFrom(first);
        Assert.Contains("Confirmation required", await Call(tools, tool, WithId(json, id)));  // pending, not yet approved
        Assert.Empty(b.Calls);
        Assert.True(consent.Approve(id));
        string done = await Call(tools, tool, WithId(json, id));
        Assert.StartsWith("OK:", done);
        Assert.Single(b.Calls);
        // Spent: replaying the same id does nothing.
        Assert.Contains("Confirmation required", await Call(tools, tool, WithId(json, id)));
        Assert.Single(b.Calls);
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == tool && e.Outcome == "confirmation-required");
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == tool && e.Outcome == "success");
    }

    [Fact]
    public async Task A_confirmation_for_one_process_cannot_pause_another()
    {
        var (tools, b, consent) = Make();
        string id = IdFrom(await Call(tools, "quick_resume_pause", PauseJson()));
        consent.Approve(id);
        b.Apps.Add(new QuickResumeAppDto(5000, "other", "Other", EdenStart, false, true, null));
        string other = "{\"process_id\":5000,\"started_utc\":\"" + Started(EdenStart) + "\",\"confirmation_id\":\"" + id + "\"}";
        Assert.Contains("Confirmation required", await Call(tools, "quick_resume_pause", other));
        Assert.Empty(b.Calls);
    }

    [Fact]
    public async Task Protected_or_unpausable_processes_are_refused_before_any_confirmation_is_requested()
    {
        var (tools, b, consent) = Make();
        string r = await Call(tools, "quick_resume_pause", "{\"process_id\":900,\"started_utc\":\"" + Started(EdenStart) + "\"}");
        Assert.StartsWith("Library tool failed:", r);
        Assert.Contains("cannot be paused", r);
        // Even if the list wrongly said CanSuspend, the policy still refuses a protected name.
        b.Apps.Add(new QuickResumeAppDto(901, "lsass", "x", EdenStart, false, true, null));
        string p = await Call(tools, "quick_resume_pause", "{\"process_id\":901,\"started_utc\":\"" + Started(EdenStart) + "\"}");
        Assert.StartsWith("Library tool failed:", p);
        Assert.Empty(consent.List());
        Assert.Empty(b.Calls);
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == "quick_resume_pause" && e.Outcome == "refused" && e.Subject == "explorer");
    }

    [Fact]
    public async Task Stale_identity_unknown_process_and_resume_of_an_unpaused_app_are_refused()
    {
        var (tools, b, consent) = Make();
        // Same pid, different start time: the pid was reused.
        string stale = await Call(tools, "quick_resume_pause", "{\"process_id\":4242,\"started_utc\":\"" + Started(EdenStart.AddMinutes(10)) + "\"}");
        Assert.StartsWith("Library tool failed:", stale);
        Assert.Contains("quick_resume_list", stale);
        // Resume of something PaRuX did not pause.
        Assert.Contains("did not pause", await Call(tools, "quick_resume_resume", PauseJson()));
        Assert.StartsWith("Library tool failed:", await Call(tools, "quick_resume_pause", """{"process_id":4242,"started_utc":"yesterday"}"""));
        Assert.Empty(consent.List());
        Assert.Empty(b.Calls);
    }

    [Theory]
    [InlineData("""{"process_id":0,"started_utc":"2026-11-04T08:30:00Z"}""")]
    [InlineData("""{"process_id":"4242","started_utc":"2026-11-04T08:30:00Z"}""")]
    [InlineData("""{"started_utc":"2026-11-04T08:30:00Z"}""")]
    [InlineData("""{"process_id":4242}""")]
    [InlineData("""{"process_id":4242,"started_utc":"2026-11-04T08:30:00Z","path":"C:\\x"}""")]
    public async Task Pause_validates_its_arguments(string json)
    {
        var (tools, b, consent) = Make();
        Assert.StartsWith("Library tool failed:", await Call(tools, "quick_resume_pause", json));
        Assert.Empty(consent.List());
        Assert.Empty(b.Calls);
    }

    [Theory]
    [InlineData("quick_resume_pause")]
    [InlineData("quick_resume_resume")]
    public async Task Pause_and_resume_are_refused_under_gaming_hard_off(string tool)
    {
        var (tools, b, consent) = Make();
        b.GamingActive = true;
        string r = await Call(tools, tool, tool == "quick_resume_pause" ? PauseJson() : ResumeJson());
        Assert.StartsWith("Library tool failed:", r);
        Assert.Contains("Gaming Mode", r);
        Assert.Empty(consent.List());
        Assert.Empty(b.Calls);
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == tool && e.Outcome == "gaming-hard-off");
    }

    [Fact]
    public async Task Audit_for_the_new_tools_holds_names_and_outcomes_only()
    {
        var (tools, _, _) = Make();
        await Call(tools, "quick_resume_list", "{}");
        await Call(tools, "quick_resume_pause", PauseJson());
        var audit = tools.Audit.Snapshot();
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, e => e.Tool == "quick_resume_pause" && e.Subject == "eden");
        Assert.All(audit, e => { Assert.DoesNotContain(@":\", e.Subject ?? string.Empty); Assert.DoesNotContain("4242", e.Subject ?? string.Empty); });
    }

    [Fact]
    public async Task Skill_allow_list_blocks_the_new_tools()
    {
        var (tools, b, _) = Make();
        var allowed = new[] { "library_search" };
        Assert.Empty(tools.GetToolSchemas(allowed).Where(s => s!["function"]!["name"]!.GetValue<string>().StartsWith("quick_", StringComparison.Ordinal)));
        string r = await tools.CallAsync("quick_resume_pause", PauseJson(), CancellationToken.None, allowed);
        Assert.StartsWith("Library tool failed:", r);
        Assert.Empty(b.Calls);
    }
}
