using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConsoleModeReadinessTests
{
    private static PlayniteComponentDto Installed() => new()
    {
        Detected = true,
        FullscreenExe = @"C:\Program Files\Playnite\Playnite.FullscreenApp.exe",
        Version = "10.0"
    };

    private static string T(string en, string it) => en;

    [Fact]
    public void Console_start_uses_optional_Playnite_and_never_assumes_it_is_installed()
    {
        var options = new PlayniteSettingsDto { ConsoleStartsPlaynite = true };
        var installed = Installed();

        Assert.True(ConsoleModeReadiness.ShouldStartWithConsole(options, installed));
        Assert.True(ConsoleModeReadiness.CanTest(installed));

        options.ConsoleStartsPlaynite = false;
        Assert.False(ConsoleModeReadiness.ShouldStartWithConsole(options, installed));
        options.ConsoleStartsPlaynite = true;
        options.Skipped = true;
        Assert.False(ConsoleModeReadiness.ShouldStartWithConsole(options, installed));
        options.Skipped = false;
        installed.Skipped = true;
        Assert.False(ConsoleModeReadiness.ShouldStartWithConsole(options, installed));
        Assert.False(ConsoleModeReadiness.CanTest(installed));
        installed.Skipped = false;
        installed.FullscreenExe = null;
        Assert.False(ConsoleModeReadiness.ShouldStartWithConsole(options, installed));
        Assert.False(ConsoleModeReadiness.CanTest(installed));
        installed.Detected = false;
        Assert.False(ConsoleModeReadiness.ShouldStartWithConsole(options, installed));
    }

    [Fact]
    public void Console_first_handoff_never_accepts_a_desktop_fallback_or_an_unconfirmed_launch()
    {
        Assert.True(ConsoleModeReadiness.AcceptedConsoleHandoff(new PlayniteLaunchResultDto
        {
            Ok = true, Outcome = "started", Mode = PlayniteModes.Console
        }));
        Assert.True(ConsoleModeReadiness.AcceptedConsoleHandoff(new PlayniteLaunchResultDto
        {
            Ok = true, Outcome = "already-running", Mode = PlayniteModes.Console
        }));

        Assert.False(ConsoleModeReadiness.AcceptedConsoleHandoff(null));
        Assert.False(ConsoleModeReadiness.AcceptedConsoleHandoff(new PlayniteLaunchResultDto
        {
            Ok = true, Outcome = "started", Mode = PlayniteModes.Desktop
        }));
        Assert.False(ConsoleModeReadiness.AcceptedConsoleHandoff(new PlayniteLaunchResultDto
        {
            Ok = false, Outcome = "already-running", Mode = PlayniteModes.Console
        }));
        Assert.False(ConsoleModeReadiness.AcceptedConsoleHandoff(new PlayniteLaunchResultDto
        {
            Ok = true, Outcome = "refused", Mode = PlayniteModes.Console
        }));
        Assert.False(ConsoleModeReadiness.AcceptedConsoleHandoff(new PlayniteLaunchResultDto
        {
            Ok = true, Outcome = "started", Mode = null
        }));
    }

    [Fact]
    public void Playnite_desktop_fallback_is_not_mistaken_for_fullscreen_or_unavailable()
    {
        Assert.Equal(ConsolePlayniteStartResult.Unavailable, ConsoleModeReadiness.ClassifyStart(null));
        Assert.Equal(ConsolePlayniteStartResult.Fullscreen, ConsoleModeReadiness.ClassifyStart(new()
        { Ok = true, Outcome = "started", Mode = PlayniteModes.Console }));
        Assert.Equal(ConsolePlayniteStartResult.DesktopFallback, ConsoleModeReadiness.ClassifyStart(new()
        { Ok = true, Outcome = "started", Mode = PlayniteModes.Desktop }));
        Assert.Equal(ConsolePlayniteStartResult.DesktopFallback, ConsoleModeReadiness.ClassifyStart(new()
        { Ok = true, Outcome = "already-running", Mode = PlayniteModes.Desktop }));
        Assert.Equal(ConsolePlayniteStartResult.Unavailable, ConsoleModeReadiness.ClassifyStart(new()
        { Ok = false, Outcome = "refused", Mode = PlayniteModes.Desktop }));
    }

    [Fact]
    public void Readiness_shows_missing_theme_and_missing_pad_without_claiming_console_is_ready()
    {
        var rows = ConsoleModeReadiness.Build(Installed(), null, false, System.Array.Empty<string>(), T);

        Assert.Contains(rows, r => r.Id == "Playnite" && r.State == ConsoleReadinessState.Ok);
        Assert.Contains(rows, r => r.Id == "ThemeInstalled" && r.State == ConsoleReadinessState.Attention);
        Assert.Contains(rows, r => r.Id == "ThemeSelected" && r.State == ConsoleReadinessState.Attention);
        Assert.Contains(rows, r => r.Id == "AnikiHelper" && r.State == ConsoleReadinessState.Attention);
        Assert.Contains(rows, r => r.Id == "Display" && r.State == ConsoleReadinessState.Attention);
        Assert.Contains(rows, r => r.Id == "Controller" && r.State == ConsoleReadinessState.Attention);
    }

    [Fact]
    public void Readiness_reports_selected_Aniki_and_generic_controller_without_brand_restrictions()
    {
        var theme = new PlayniteThemeComponentDto
        {
            Detected = true,
            Selected = true,
            Version = "3.6.0",
            ConfigReadable = true,
            PlayniteDataFolderFound = true,
            AnikiHelperFoundOnDisk = true,
            AnikiHelperVersion = "1.3.6"
        };
        var rows = ConsoleModeReadiness.Build(Installed(), theme, true, new[] { "Wireless Controller" }, T);

        Assert.Contains(rows, r => r.Id == "ThemeInstalled" && r.State == ConsoleReadinessState.Ok);
        Assert.Contains(rows, r => r.Id == "ThemeSelected" && r.State == ConsoleReadinessState.Ok);
        Assert.Contains(rows, r => r.Id == "AnikiHelper" && r.State == ConsoleReadinessState.Attention && r.Detail.Contains("manifest found"));
        Assert.Contains(rows, r => r.Id == "Controller" && r.State == ConsoleReadinessState.Ok && r.Detail.Contains("Wireless Controller"));
    }

    [Fact]
    public void An_explicit_Console_request_ignores_the_TV_start_switch_but_never_Playnite_that_cannot_start()
    {
        var options = new PlayniteSettingsDto { ConsoleStartsPlaynite = false };
        Assert.False(ConsoleModeReadiness.ShouldStartWithConsole(options, Installed()));
        Assert.True(ConsoleModeReadiness.ShouldStartWithConsole(options, Installed(), explicitRequest: true));

        options.Skipped = true;
        Assert.False(ConsoleModeReadiness.ShouldStartWithConsole(options, Installed(), explicitRequest: true));
        options.Skipped = false;
        var noFullscreen = Installed();
        noFullscreen.FullscreenExe = null;
        Assert.False(ConsoleModeReadiness.ShouldStartWithConsole(options, noFullscreen, explicitRequest: true));
        Assert.False(ConsoleModeReadiness.ShouldStartWithConsole(options, new PlayniteComponentDto(), explicitRequest: true));
    }

    [Theory]
    [InlineData(ConsolePlayniteStartResult.Fullscreen, true, ConsoleStartFollowUp.MinimizePaRuX)]
    [InlineData(ConsolePlayniteStartResult.Fullscreen, false, ConsoleStartFollowUp.MinimizePaRuX)]
    [InlineData(ConsolePlayniteStartResult.DesktopFallback, true, ConsoleStartFollowUp.StayOpen)]
    [InlineData(ConsolePlayniteStartResult.DesktopFallback, false, ConsoleStartFollowUp.StayOpen)]
    [InlineData(ConsolePlayniteStartResult.Unavailable, true, ConsoleStartFollowUp.StayOpen)]
    [InlineData(ConsolePlayniteStartResult.Unavailable, false, ConsoleStartFollowUp.LegacyConsoleFallback)]
    public void Only_a_confirmed_Fullscreen_hides_PaRuX_and_always_Console_never_opens_a_native_TV_takeover(
        ConsolePlayniteStartResult result, bool alwaysConsole, ConsoleStartFollowUp expected) =>
        Assert.Equal(expected, ConsoleModeReadiness.FollowUp(result, alwaysConsole));

    [Fact]
    public void Theme_advice_is_honest_and_silent_when_nothing_is_wrong_or_the_owner_skipped_it()
    {
        Assert.Null(ConsoleModeReadiness.ThemeAdvice(null, T));
        Assert.Null(ConsoleModeReadiness.ThemeAdvice(new PlayniteThemeComponentDto { Skipped = true }, T));
        Assert.Null(ConsoleModeReadiness.ThemeAdvice(new PlayniteThemeComponentDto { Detected = true, Selected = true, ConfigReadable = true }, T));
        Assert.Null(ConsoleModeReadiness.ThemeAdvice(new PlayniteThemeComponentDto { Detected = true, ConfigReadable = false }, T));
        Assert.Contains("not installed", ConsoleModeReadiness.ThemeAdvice(new PlayniteThemeComponentDto(), T));
        string? other = ConsoleModeReadiness.ThemeAdvice(
            new PlayniteThemeComponentDto { Detected = true, ConfigReadable = true, SelectedTheme = "Default" }, T);
        Assert.Contains("Default", other);
        Assert.Contains("Select it", other);
    }

    [Fact]
    public void Always_Console_start_is_off_by_default_and_needs_no_TV_choice()
    {
        // The option lives in AppSettings (App project); the pure decision above never reads a display.
        Assert.False(new PlayniteSettingsDto().LaunchAtStartup);
        Assert.True(ConsoleModeReadiness.ShouldStartWithConsole(new PlayniteSettingsDto { ConsoleStartsPlaynite = false }, Installed(), explicitRequest: true));
    }
}
