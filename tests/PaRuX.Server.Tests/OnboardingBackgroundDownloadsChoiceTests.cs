using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OnboardingBackgroundDownloadsChoiceTests
{
    [Fact]
    public void The_safe_default_is_off()
    {
        Assert.False(BackgroundDownloadsChoice.DefaultOn);
        BackgroundDownloadsChoiceView v = BackgroundDownloadsChoice.Build(BackgroundDownloadsChoice.DefaultOn, corporate: false);
        Assert.False(v.IsOn);
        Assert.True(v.CanChange);
        Assert.Contains("Off", v.StateEn);
        Assert.Contains("Spento", v.StateIt);
    }

    [Fact]
    public void Corporate_is_always_off_and_cannot_be_changed_whatever_was_stored()
    {
        BackgroundDownloadsChoiceView v = BackgroundDownloadsChoice.Build(currentlyOn: true, corporate: true);
        Assert.False(v.IsOn);
        Assert.False(v.CanChange);
        Assert.Contains("Corporate", v.ExplanationEn);
        Assert.Contains("Corporate", v.ExplanationIt);
        Assert.False(BackgroundDownloadsChoice.Requested(true, corporate: true));
        Assert.True(BackgroundDownloadsChoice.Requested(true, corporate: false));
        Assert.False(BackgroundDownloadsChoice.Requested(false, corporate: false));
    }

    [Fact]
    public void The_explanation_is_complete_and_bilingual()
    {
        BackgroundDownloadsChoiceView v = BackgroundDownloadsChoice.Build(true, false);
        Assert.True(v.IsOn);
        Assert.NotEqual(v.ExplanationEn, v.ExplanationIt);
        foreach (string needle in new[] { "online catalogs", "download", "never downloads models", "Gaming", "Settings" }) Assert.Contains(needle, v.ExplanationEn);
        foreach (string needle in new[] { "cataloghi online", "scaricare", "mai modelli", "Gaming", "Impostazioni" }) Assert.Contains(needle, v.ExplanationIt);
        Assert.Contains("Off by default", v.ExplanationEn);
        Assert.Contains("Spento per impostazione predefinita", v.ExplanationIt);
    }

    private static string Read(params string[] relative)
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir)!;
        return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(relative).ToArray()));
    }

    [Fact]
    public void The_first_run_has_the_question_visible_outside_the_collapsed_manual_expander_and_off_by_default()
    {
        string xaml = Read("src", "PaRuX.App", "Views", "OnboardingView.xaml");
        int section = xaml.IndexOf("x:Name=\"BackgroundDownloadsSection\"", StringComparison.Ordinal);
        int manual = xaml.IndexOf("x:Name=\"ManualSetupExpander\"", StringComparison.Ordinal);
        Assert.True(section > 0 && manual > section, "The question must come before (outside) the collapsed manual-setup expander.");
        Match toggle = Regex.Match(xaml, "<ToggleSwitch x:Name=\"BackgroundDownloadsToggle\"[^>]*>", RegexOptions.Singleline);
        Assert.True(toggle.Success);
        Assert.Contains("IsOn=\"False\"", toggle.Value);
        Assert.Contains("AutomationProperties.AutomationId=\"Onboarding_BackgroundDownloads_Toggle\"", toggle.Value);
        Assert.Contains("loc:Loc.ItName=", toggle.Value);
        Assert.Contains("loc:Loc.ItText=", xaml[section..manual]);
        // The Detect -> Locate / Install / Skip flow text stays.
        Assert.Contains("Detect → Locate existing / Install with PaRuX / Skip", xaml);
    }

    [Fact]
    public void The_first_run_switch_saves_through_the_server_setting_and_shows_the_truth_when_saving_fails()
    {
        string code = Read("src", "PaRuX.App", "Views", "OnboardingView.BackgroundDownloads.cs");
        Assert.Contains("SetSettingsAsync", code);
        Assert.Contains("keepRuntimesUpdated: requested", code);
        Assert.Contains("BackgroundDownloadsChoice.Requested", code);
        Assert.Contains("CorporateMode.IsActive", code);
        Assert.Contains("ApplyBackgroundDownloadsChoice(false)", code);   // a failed save never leaves a switch that looks ON
        Assert.Contains("ApplyBackgroundDownloadsChoice(BackgroundDownloadsChoice.DefaultOn)", Read("src", "PaRuX.App", "Views", "OnboardingView.xaml.cs"));
    }

    [Fact]
    public void Settings_keeps_the_same_choice_with_the_same_wording_and_Corporate_lock()
    {
        string cs = Read("src", "PaRuX.App", "Views", "SettingsView.AutoCheck.cs");
        Assert.Contains("BackgroundDownloadsChoice.Build", cs);
        Assert.Contains("BackgroundDownloadsChoice.Requested", cs);
        Assert.Contains("IsEnabled = choice.CanChange", cs);
        string xaml = Read("src", "PaRuX.App", "Views", "SettingsView.xaml");
        Assert.Contains("Settings_AutoCheck_RuntimeUpdate_Explanation_Text", xaml);
        Assert.Matches("<ToggleSwitch x:Name=\"Settings_AutoCheck_RuntimeUpdate_Toggle\"[^>]*IsOn=\"False\"", xaml.Replace("\r\n", " ").Replace("\n", " "));
    }

    [Fact]
    public void The_server_still_ignores_consent_it_did_not_record_and_forces_Corporate_off()
    {
        string model = Read("src", "PaRuX.Server", "AutoMaintenanceModels.cs");
        Assert.Contains("KeepRuntimesUpdated && RuntimeUpdateConsentUtc is not null && !PaRuX.Contracts.CorporateMode.IsActive", model);
        Assert.False(new PaRuX.Server.AutoMaintenanceSettings().KeepRuntimesUpdated);
    }
}
