using FlaUI.Core.AutomationElements;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Non-destructive transition acceptance: the Playnite-first Games tools panel
/// links to the EXISTING management surfaces and leaves the old Library entry
/// intact. Nothing in this test launches a game or Playnite, downloads a file,
/// installs a mod or writes into the owner's folders.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GamesToolsHomeUiTests
{
    private static AutomationElement ById(UiTestApplication app, string id) =>
        UiWait.ById(app.MainWindow, id);

    private static void Press(UiTestApplication app, string id) =>
        new InstallProgressPage(app).Invoke(ById(app, id));

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Games_tools_exposes_Playnite_AI_mods_and_legacy_navigation_without_starting_anything()
    {
        const string name = nameof(Games_tools_exposes_Playnite_AI_mods_and_legacy_navigation_without_starting_anything);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            Press(app, "MainWindow_ModeTab_Gaming");

            // Existing library remains the default during the staged migration:
            // shortcut handlers and pre-existing game UI tests still work.
            Assert.NotNull(ById(app, "GameLibrary_Section_Library"));
            Assert.NotNull(ById(app, "GameLibrary_OpenTools_Button"));
            Press(app, "GameLibrary_OpenTools_Button");

            Assert.NotNull(ById(app, "GamesTools_Home"));
            Assert.NotNull(ById(app, "GamesTools_Playnite_Status"));
            foreach (string action in new[]
            {
                "PlayniteConsole", "PlayniteDesktop", "PlayniteSetup", "PlayniteRefresh",
                "AiProposals", "AiRestore", "AssetStudio", "InstalledMods",
                "Downloads", "Install", "Backups", "Controller",
            })
            {
                AutomationElement button = ById(app, "GamesTools_" + action + "_Button");
                Assert.False(string.IsNullOrWhiteSpace(button.Name));
            }
            Assert.NotNull(ById(app, "GamesTools_QuickResume_Expander"));

            // Direct navigation reuses the same controls and AutomationIds.
            Press(app, "GamesTools_Downloads_Button");
            Assert.NotNull(ById(app, "GameDownloads_Panel"));
            GamingSectionsPage.SelectSection(app.MainWindow, "Tools");
            Press(app, "GamesTools_Install_Button");
            Assert.NotNull(ById(app, "GameLibrary_Installer_Inspect_Button"));
            GamingSectionsPage.SelectSection(app.MainWindow, "Tools");
            Press(app, "GamesTools_Backups_Button");
            Assert.NotNull(ById(app, "GameSaves_Panel"));

            // The original native library is still reachable for migration and
            // old deep links; this test never assumes its eventual removal.
            GamingSectionsPage.SelectSection(app.MainWindow, "Library");
            Assert.NotNull(ById(app, "GameHome_Sort"));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
