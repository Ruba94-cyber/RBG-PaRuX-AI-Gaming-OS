using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Quick Resume at the top of the Gaming library: with a real, ordinary Win32 application paused through Quick Resume (Character
/// Map, started by the test and identified by its own pid), the "Resume" card is the first thing on the library page, above the
/// search box and the tiles, and pressing it resumes the process and removes the card. Nothing else on the PC is touched.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PausedGameBannerUiTests
{
    private const string GamingTab = "MainWindow_ModeTab_Gaming";

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void A_paused_app_is_offered_for_resume_on_the_Games_tools_page_and_resuming_removes_the_card()
    {
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(0);
        using Process target = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "charmap.exe")) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized })!;   // minimized: it must not pop up on the owner's screen
        try
        {
            UiWait.For(() => { target.Refresh(); return target.MainWindowHandle != IntPtr.Zero; }, "test application window", TimeSpan.FromSeconds(30));
            string name = nameof(A_paused_app_is_offered_for_resume_on_the_Games_tools_page_and_resuming_removes_the_card);
            using var app = new UiTestApplication(name, restartDataRoot: root);
            try
            {
                // Since gaming-removal step 4 the paused-game card and Quick Resume live on the Games tools page, in one expander.
                new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, GamingTab));
                UiWait.ById(app.MainWindow, "GamesToolsPage_QuickResume_Expander").Patterns.ExpandCollapse.Pattern.Expand();
                string resumeId = "GameHome_PausedBanner_Resume_" + target.Id;
                Assert.Null(GamingSectionsPage.TryFind(app.MainWindow, resumeId));   // nothing is paused yet: no card

                // Pause it from the Quick Resume list (the normal way), then the card must appear on its own.
                string pauseId = "QuickResume_Pause_" + target.Id;
                AutomationElement? pause = null;
                UiWait.For(() => (pause = GamingSectionsPage.TryFind(app.MainWindow, pauseId)) != null,
                    "Quick Resume lists the test application", TimeSpan.FromSeconds(60));
                new InstallProgressPage(app).Invoke(pause!);

                UiWait.ById(app.MainWindow, resumeId);   // the card (polls about every 5 s, or right after the action)
                app.CaptureSnapshot("paused-banner-visible");

                new InstallProgressPage(app).Invoke(GamingSectionsPage.TryFind(app.MainWindow, resumeId)!);
                UiWait.For(() => GamingSectionsPage.TryFind(app.MainWindow, resumeId) == null, "resume card gone after resuming", TimeSpan.FromSeconds(30));
                Assert.False(target.HasExited, "the resumed application is still running");
            }
            catch (Exception ex)
            {
                app.CaptureFailure(name, ex);
                throw;
            }
        }
        finally
        {
            try { if (!target.HasExited) target.Kill(true); } catch { }
        }
    }
}
