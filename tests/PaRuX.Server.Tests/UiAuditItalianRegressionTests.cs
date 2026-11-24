using System.Globalization;
using System.Text.RegularExpressions;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Regression guards for the Italian gaps a real-window audit found (Create tabs and setup card, Model Hub hardware line,
/// suggestion/update/token status lines, Observability, Lifecycle Lab and Tools section captions). The English wording of the same
/// strings stays pinned by the existing presenter tests.
/// </summary>
public sealed class UiAuditItalianRegressionTests
{
    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("PaRuX.sln not found.");
    }

    private static string App(string relative) => File.ReadAllText(Path.Combine(Root(), "src", "PaRuX.App", relative));

    private static T WithCulture<T>(string culture, Func<T> action)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try { return action(); }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void Media_setup_card_speaks_Italian_without_English_chrome()
    {
        var status = new MediaSetupStatus
        {
            State = "idle", Ready = false,
            Steps = new List<MediaSetupStepDto>
            {
                new() { Id = "e", Title = "Image engine (ComfyUI)", State = "pending", SizeBytes = 2_000_000_000L, License = "GPL-3.0" }
            }
        };
        MediaSetupCardModel card = MediaSetupPresenter.Present(status, "image generation", italian: true);
        Assert.Equal("Configura: generazione di immagini", card.ButtonLabel);
        Assert.Contains("PaRuX scaricherà e installerà", card.Detail);
        Assert.Contains("Licenze:", card.Detail);
        Assert.DoesNotContain("Set up", card.Headline + card.ButtonLabel);
        Assert.DoesNotContain("will download", card.Detail);
        // The English default is unchanged.
        Assert.Equal("Set up image generation", MediaSetupPresenter.Present(status, "image generation").ButtonLabel);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("blocked")]
    [InlineData("waiting")]
    public void Media_setup_card_states_have_Italian_buttons(string state)
    {
        var status = new MediaSetupStatus { State = state, Ready = false, Message = "m" };
        MediaSetupCardModel card = MediaSetupPresenter.Present(status, "video generation", italian: true);
        Assert.DoesNotMatch(new Regex(@"^(Set up|Cancel setup|Try again|Resume setup)", RegexOptions.None, TimeSpan.FromSeconds(1)), card.ButtonLabel);
        Assert.DoesNotContain("setup", card.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_tabs_keep_English_ids_and_localize_only_the_visible_label()
    {
        string source = App(Path.Combine("Views", "CreateWorkspaceView.Tabs.cs"));
        Assert.Contains("Text = ModeLabel(name), Tag = name", source);
        Assert.Contains("\"Create_Mode_\" + name", source);
        // Selection is keyed by the English Tag, never by the localized text.
        Assert.Contains("SelectedItem as SelectorBarItem)?.Tag as string", source);
        foreach (string mode in new[] { "Image", "Music", "Edit", "Speech", "Workflows", "Gallery" })
            Assert.Contains("\"" + mode + "\" =>", source);
        string view = App(Path.Combine("Views", "CreateWorkspaceView.cs"));
        foreach (Match m in Regex.Matches(view, "Section\\(\"([^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(2)))
            Assert.Contains("\"" + m.Groups[1].Value + "\" =>", view);
    }

    [Theory]
    [InlineData("Views/LifecycleLabView.cs", "Section")]
    [InlineData("Views/LifecycleLabView.cs", "Label")]
    [InlineData("Views/ToolsAutomationView.cs", "Section")]
    public void Section_captions_have_an_Italian_mapping(string file, string call)
    {
        string source = App(file.Replace('/', Path.DirectorySeparatorChar));
        var captions = Regex.Matches(source, call + "\\(\"([A-Z0-9][A-Z0-9 /·&:'→-]+)\"", RegexOptions.None, TimeSpan.FromSeconds(2))
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(captions);
        foreach (string caption in captions)
            Assert.True(source.Contains("\"" + caption + "\" =>", StringComparison.Ordinal), file + " has no Italian mapping for: " + caption);
    }

    [Fact]
    public void Observability_metric_labels_cover_every_Core_metric_id()
    {
        string server = File.ReadAllText(Path.Combine(Root(), "src", "PaRuX.Server", "ObservabilityService.cs"));
        string view = App(Path.Combine("Views", "ObservabilityView.cs"));
        foreach (Match m in Regex.Matches(server, "Id = \"([a-z_]+)\", Label = \"", RegexOptions.None, TimeSpan.FromSeconds(2)))
            Assert.Contains("\"" + m.Groups[1].Value + "\" =>", view);
        foreach (string heading in new[] { "CURRENT CORE SNAPSHOT", "MODEL BENCHMARK LEADERBOARD", "ACTIVITY TIMELINE" })
            Assert.Contains("\"" + heading + "\" => T(", view);
    }

    [Fact]
    public void Status_lines_follow_the_UI_language()
    {
        Assert.Equal("Hardware profile not available yet.", WithCulture("en-US", () => ModelSuggestionPresenter.FormatHardware(null)));
        Assert.Equal("Profilo hardware non ancora disponibile.", WithCulture("it-IT", () => ModelSuggestionPresenter.FormatHardware(null)));
        string token = WithCulture("it-IT", () => ModelDownloadSettingsPolicy.DescribeCredentialStatus(new ModelDownloadCredentialStatus { TokenConfigured = false }));
        Assert.StartsWith("Nessun token", token);
        Assert.Equal("Illimitato", WithCulture("it-IT", () => ModelDownloadSettingsPolicy.DescribeBandwidthLimit(0)));
    }

    [Fact]
    public void Model_Hub_hardware_line_has_no_English_fragments_in_the_localized_branches()
    {
        string source = App(Path.Combine("Views", "ModelHubView.cs"));
        Assert.DoesNotContain("+ \" dedicated (\"", source);
        Assert.DoesNotContain("+ \" shared system-memory limit (\"", source);
        Assert.Contains("rilevata, ma nessun backend di testo PaRuX compatibile", source);
    }

    [Fact]
    public void Settings_selection_tints_follow_the_PRX_Orbit_brand_not_a_fixed_purple()
    {
        foreach (string file in new[] { "SettingsView.xaml.cs", "SettingsView.PadButtons.cs" })
        {
            string source = App(Path.Combine("Views", file));
            Assert.DoesNotContain("124, 108, 240", source);
        }
        Assert.Contains("BrandTint(kvp.Value, 48)", App(Path.Combine("Views", "SettingsView.xaml.cs")));
    }
}
