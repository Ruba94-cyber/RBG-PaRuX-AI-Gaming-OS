using FlaUI.Core.AutomationElements;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Acceptance for the slim Games tools workspace page (additive step 1). Opening the page and expanding its sections never starts a
/// game, Playnite, a download, an installer or a driver change; the test only checks that the entry points exist and respond.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GamesToolsPageUiTests
{
    private static AutomationElement ById(UiTestApplication app, string id) =>
        UiWait.ById(app.MainWindow, id);

    private static void Press(UiTestApplication app, string id) =>
        new InstallProgressPage(app).Invoke(ById(app, id));

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Games_tools_page_hosts_the_existing_entry_points_without_starting_anything()
    {
        const string name = nameof(Games_tools_page_hosts_the_existing_entry_points_without_starting_anything);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            Press(app, "MainWindow_ModeTab_Gaming");
            Press(app, "GameLibrary_OpenToolsPage_Button");

            Assert.NotNull(ById(app, "Workspace_GamesTools_Page"));
            Assert.NotNull(ById(app, "GamesToolsPage_Heading"));
            Assert.NotNull(ById(app, "GamesToolsPage_Playnite_Status"));
            Assert.NotNull(ById(app, "GamesToolsPage_Handoff_Status"));
            foreach (string action in new[]
            {
                "PlayniteConsole", "PlayniteDesktop", "PlayniteSetup", "PlayniteRefresh",
                "StreamingSettings", "GameBarSettings", "SignInApps", "PadActions",
                "AiProposals", "AiRestore", "AssetStudio", "InstalledMods", "SkinStudio",
                "InstallStage", "HandoffSettings",
            })
            {
                AutomationElement button = ById(app, "GamesToolsPage_" + action + "_Button");
                Assert.False(string.IsNullOrWhiteSpace(button.Name));
            }
            Assert.NotNull(ById(app, "GamesToolsPage_QuickResume_Expander"));
            Assert.NotNull(ById(app, "GamesToolsPage_Downloads_Expander"));

            // The Settings entry points navigate to the existing sections and change nothing.
            Press(app, "GamesToolsPage_StreamingSettings_Button");
            Assert.NotNull(ById(app, "Settings_Playnite_Section"));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
