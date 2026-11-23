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
        Assert.Contains(rows, r => r.Id == "AnikiHelper" && r.State == ConsoleReadinessState.Ok);
        Assert.Contains(rows, r => r.Id == "Controller" && r.State == ConsoleReadinessState.Ok && r.Detail.Contains("Wireless Controller"));
    }
}
