using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AdvancedWorkspaceGuidanceTests
{
    private static void AssertBothLanguages(Bilingual text, string what)
    {
        Assert.False(string.IsNullOrWhiteSpace(text.En), what + " has no English text");
        Assert.False(string.IsNullOrWhiteSpace(text.It), what + " has no Italian text");
    }

    [Fact]
    public void Every_control_has_an_english_and_an_italian_label_and_tooltip()
    {
        Assert.NotEmpty(ControlHelpCatalog.All);
        foreach (ControlHelp help in ControlHelpCatalog.All)
        {
            AssertBothLanguages(help.Label, help.Id + " label");
            AssertBothLanguages(help.Tooltip, help.Id + " tooltip");
            Assert.NotEqual(help.Tooltip.En, help.Tooltip.It);
        }
    }

    [Fact]
    public void Every_declared_control_id_is_registered_exactly_once()
    {
        var declared = typeof(ControlHelpIds).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(declared.Count, ControlHelpCatalog.All.Select(h => h.Id).Distinct().Count());
        foreach (string id in declared) Assert.NotNull(ControlHelpCatalog.Find(id));
    }

    [Fact]
    public void The_headline_technical_controls_say_what_they_do()
    {
        Assert.Contains("more detail but slower", ControlHelpCatalog.Tooltip(ControlHelpIds.MediaSteps, italian: false));
        Assert.Contains("più dettagli", ControlHelpCatalog.Tooltip(ControlHelpIds.MediaSteps, italian: true));
        foreach (string id in new[] { ControlHelpIds.MediaCfg, ControlHelpIds.MediaSampler, ControlHelpIds.MediaSeed, ControlHelpIds.RuntimeQuant,
                     ControlHelpIds.RuntimeContextSize, ControlHelpIds.ChatTemperature, ControlHelpIds.ChatTopP, ControlHelpIds.TrainRank })
            Assert.True(ControlHelpCatalog.Tooltip(id, false).Length > 40, id);
    }

    [Fact]
    public void Every_workspace_guide_has_what_it_does_an_example_an_empty_state_and_one_first_action_in_both_languages()
    {
        var ids = typeof(WorkspaceGuideIds).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(ids.Count, WorkspaceGuideCatalog.All.Count);
        foreach (string id in ids)
        {
            WorkspaceGuide g = WorkspaceGuideCatalog.Get(id);
            AssertBothLanguages(g.WhatItDoes, id + " what");
            AssertBothLanguages(g.Example, id + " example");
            AssertBothLanguages(g.EmptyState, id + " empty");
            AssertBothLanguages(g.FirstAction, id + " action");
            Assert.True(g.FirstAction.En.Length < 40, id + " first action should be a short button label");
        }
    }

    [Fact]
    public void Template_catalog_offers_the_requested_starting_points_in_both_languages()
    {
        foreach (WorkspaceTemplate t in WorkspaceTemplateCatalog.All)
        {
            AssertBothLanguages(t.Title, t.Id + " title");
            AssertBothLanguages(t.Description, t.Id + " description");
            AssertBothLanguages(t.Prompt, t.Id + " prompt");
        }
        Assert.Equal(WorkspaceTemplateCatalog.All.Count, WorkspaceTemplateCatalog.All.Select(t => t.Id).Distinct().Count());
        Assert.Contains(WorkspaceTemplateCatalog.For(TemplateWorkspaces.MediaImage), t => t.Title.En == "Portrait");
        Assert.Contains(WorkspaceTemplateCatalog.For(TemplateWorkspaces.MediaImage), t => t.Title.En == "Product photo");
        Assert.Contains(WorkspaceTemplateCatalog.For(TemplateWorkspaces.MediaVideo), t => t.Title.En == "Short video from an image");
        Assert.Contains(WorkspaceTemplateCatalog.For(TemplateWorkspaces.Coding), t => t.Title.En == "Fix a bug");
        Assert.Contains(WorkspaceTemplateCatalog.For(TemplateWorkspaces.Coding), t => t.Title.En == "Explain this repo");
        Assert.Contains(WorkspaceTemplateCatalog.For(TemplateWorkspaces.Automations), t => t.Title.En == "Summarize new files in a folder every morning");
        Assert.NotEmpty(WorkspaceTemplateCatalog.For(TemplateWorkspaces.Knowledge));
        Assert.NotEmpty(WorkspaceTemplateCatalog.For(TemplateWorkspaces.GroupChat));
        Assert.NotEmpty(WorkspaceTemplateCatalog.For(TemplateWorkspaces.Compare));
    }

    [Fact]
    public void Media_templates_carry_sizes_the_size_boxes_accept()
    {
        foreach (WorkspaceTemplate t in WorkspaceTemplateCatalog.For(TemplateWorkspaces.MediaImage).Concat(WorkspaceTemplateCatalog.For(TemplateWorkspaces.MediaVideo)))
        {
            Assert.True(int.Parse(t.Field("width")) % 8 == 0, t.Id);
            Assert.True(int.Parse(t.Field("height")) % 8 == 0, t.Id);
        }
        Assert.Equal("true", WorkspaceTemplateCatalog.Get("media.video.fromImage").Field("needsStartImage"));
    }

    [Fact]
    public void Automation_template_substitutes_the_folder_into_valid_json_even_with_backslashes()
    {
        WorkspaceTemplate t = WorkspaceTemplateCatalog.Get("automation.summarizeEachNewFile");
        AutomationDraft draft = AutomationTemplateBuilder.Build(t, @"D:\Data\My Docs", italian: false);
        using JsonDocument doc = JsonDocument.Parse(draft.TriggerJson);
        Assert.Equal(@"D:\Data\My Docs", doc.RootElement.GetProperty("folderPath").GetString());
        Assert.Equal("file_created", draft.Trigger);
        Assert.Contains("{fileName}", draft.Goal);

        AutomationDraft daily = AutomationTemplateBuilder.Build(WorkspaceTemplateCatalog.Get("automation.dailyFolderSummary"), @"D:\Inbox", italian: true);
        Assert.Contains(@"D:\Inbox", daily.Goal);
        Assert.Equal(86400, JsonDocument.Parse(daily.TriggerJson).RootElement.GetProperty("intervalSeconds").GetInt32());
        Assert.Throws<ArgumentException>(() => AutomationTemplateBuilder.Build(t, "  ", italian: false));
    }

    [Fact]
    public void Automation_template_triggers_are_known_to_the_engine_and_goals_only_use_placeholders_it_fills()
    {
        foreach (WorkspaceTemplate t in WorkspaceTemplateCatalog.For(TemplateWorkspaces.Automations))
        {
            string trigger = t.Field("trigger");
            Assert.Contains(trigger, PaRuX.Contracts.AutomationTriggerType.All);
            var allowed = trigger switch
            {
                "file_created" => new[] { "fileName", "folderPath" },
                "git_commit" => new[] { "commitSha", "commitMessage", "repositoryPath" },
                _ => Array.Empty<string>()
            };
            foreach (var m in System.Text.RegularExpressions.Regex.Matches(t.Prompt.En + t.Prompt.It, @"\{([A-Za-z]+)\}").Cast<System.Text.RegularExpressions.Match>())
                Assert.Contains(m.Groups[1].Value, allowed);
        }
    }

    [Theory]
    [InlineData("ComfyUI is not running on this computer.", WorkspaceErrorKind.EngineNotRunning)]
    [InlineData("CUDA out of memory. Tried to allocate 2.00 GiB", WorkspaceErrorKind.OutOfMemory)]
    [InlineData("No model installed for image generation.", WorkspaceErrorKind.NoModelInstalled)]
    [InlineData("The model is not loaded", WorkspaceErrorKind.ModelNotLoaded)]
    [InlineData("'npx' is not recognized as an internal or external command", WorkspaceErrorKind.McpCommandMissing)]
    [InlineData("Could not find a part of the path 'D:\\gone\\x'.", WorkspaceErrorKind.FolderMissing)]
    [InlineData("'x' is an invalid start of a value. JsonException", WorkspaceErrorKind.InvalidJson)]
    [InlineData("The request timed out.", WorkspaceErrorKind.TimedOut)]
    [InlineData("No connection could be made because the target machine actively refused it", WorkspaceErrorKind.CoreUnreachable)]
    [InlineData("Microphone not found", WorkspaceErrorKind.MicrophoneUnavailable)]
    [InlineData("Missing node type: ImpactWildcard", WorkspaceErrorKind.WorkflowNodeMissing)]
    [InlineData("Refused: Gaming Mode is active", WorkspaceErrorKind.GamingModeActive)]
    public void Workspace_failures_become_a_cause_plus_a_next_action_in_both_languages(string raw, WorkspaceErrorKind expected)
    {
        Assert.Equal(expected, WorkspaceErrorExplainer.Classify(raw));
        FriendlyError en = WorkspaceErrorExplainer.Explain(raw, false)!;
        FriendlyError it = WorkspaceErrorExplainer.Explain(raw, true)!;
        Assert.NotEqual(en.Cause, it.Cause);
        Assert.False(string.IsNullOrWhiteSpace(en.NextAction));
        Assert.False(string.IsNullOrWhiteSpace(it.NextAction));
        Assert.DoesNotContain("Exception", en.Line);
    }

    [Fact]
    public void Setup_errors_still_win_and_unknown_or_validation_messages_are_left_exactly_as_they_were()
    {
        Assert.Equal(SetupErrorExplainer.Explain("No such host is known.", false)!.Line, WorkspaceErrorExplainer.Explain("No such host is known.", false)!.Line);
        Assert.Equal("Generation failed: An image prompt is required.", WorkspaceErrorExplainer.StatusLine("Generation failed: ", "An image prompt is required.", false));
        Assert.Null(WorkspaceErrorExplainer.Explain(null, false));
        Assert.StartsWith("Generation failed: Your graphics card ran out of memory.", WorkspaceErrorExplainer.StatusLine("Generation failed: ", "CUDA out of memory", false));
    }

    [Fact]
    public void Long_job_status_shows_stage_percent_and_remaining_time_in_both_languages()
    {
        Assert.Equal("Rendering · 40% · about 2 min left", LongJobProgressPresenter.Line("Rendering", 40, 100, TimeSpan.FromSeconds(60), false));
        Assert.Equal("Rendering · 40% · mancano circa 2 min", LongJobProgressPresenter.Line("Rendering", 40, 100, TimeSpan.FromSeconds(60), true));
        // No server estimate: derived from progress once there is enough to go on, otherwise honest elapsed time.
        Assert.Equal(60, LongJobProgressPresenter.EtaSeconds(50, null, TimeSpan.FromSeconds(60)));
        Assert.Null(LongJobProgressPresenter.EtaSeconds(2, null, TimeSpan.FromSeconds(60)));
        Assert.Equal("Queued · working for 1:05", LongJobProgressPresenter.Line("Queued", 0, null, TimeSpan.FromSeconds(65), false));
        Assert.Contains("almost done", LongJobProgressPresenter.Line("Saving", 99, 3, TimeSpan.FromSeconds(300), false));
    }
}
