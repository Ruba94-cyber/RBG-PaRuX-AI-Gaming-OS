using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// The ten advanced workspace pages (Create, Projects, Document Search, Model Intelligence, Lifecycle Lab, Workflows, Tools,
/// Observability, Updates, Runtime) are created in code the first time they are opened. This opens every one of them in the
/// real app and asserts that (1) none exists before it is opened, (2) opening creates it with its root AutomationId, (3) its
/// landmark control is visible, (4) a second visit reuses the same page, and (5) the app log has no creation/opening failure
/// or exception. It navigates only through the UIA SelectionItem/Toggle patterns (no raw mouse or keyboard input), and the
/// window is captured with PrintWindow, so it is safe to run while another window holds the foreground.
/// Document Search exists only in the corporate edition, so it is covered by the corporate run.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class LazyWorkspacePagesUiTests
{
    private sealed record LazyPageCase(string Workspace, string NavId, string RootId, WorkspaceDescriptor? Landmark, string? LandmarkId = null, ControlType LandmarkType = ControlType.Edit);

    private static readonly LazyPageCase[] StandardPages =
    {
        new("Create", "Navigation_Create_Item", "Workspace_Create_Page", WorkspaceNavigationPage.CreateDescriptor),
        new("Projects", "Navigation_Projects_Item", "Workspace_Projects_Page", WorkspaceNavigationPage.ProjectsDescriptor),
        new("ModelRouting", "Navigation_ModelRouting_Item", "Workspace_ModelRouting_Page", WorkspaceNavigationPage.ModelRoutingDescriptor),
        new("Lifecycle", "Navigation_Lifecycle_Item", "Workspace_Lifecycle_Page", WorkspaceNavigationPage.LifecycleDescriptor),
        new("Workflows", "Navigation_Workflows_Item", "Workspace_Workflows_Page", WorkspaceNavigationPage.WorkflowsDescriptor),
        new("Tools", "Navigation_Tools_Item", "Workspace_Tools_Page", WorkspaceNavigationPage.ToolsDescriptor),
        new("Observability", "Navigation_Observability_Item", "Workspace_Observability_Page", WorkspaceNavigationPage.ObservabilityDescriptor),
        new("Updates", "Navigation_Updates_Item", "Workspace_Updates_Page", WorkspaceNavigationPage.UpdatesDescriptor),
        new("Runtime", "Navigation_Runtime_Item", "Workspace_Runtime_Page", WorkspaceNavigationPage.RuntimeDescriptor),
    };

    private static readonly LazyPageCase[] CorporatePages =
    {
        new("DocumentSearch", "Navigation_DocumentSearch_Item", "Workspace_DocumentSearch_Page", null, "DocSearch_Query_TextBox", ControlType.Edit),
    };

    [Theory, Trait("Category", "Regression"), Trait("Category", "LazyPages"), Trait("Category", "FlaUI")]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_lazy_workspace_page_is_created_on_first_open_and_the_app_log_stays_clean(bool corporate)
    {
        string name = "LazyPages_" + (corporate ? "corporate" : "standard");
        var env = corporate ? new Dictionary<string, string> { ["PARUX_CORPORATE"] = "1" } : null;
        using var app = new UiTestApplication(name, extraEnvironment: env);
        try
        {
            app.ResizeMainWindow(1400, 900);
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            UiWait.ById(app.MainWindow, "Chat_Prompt_TextBox", ControlType.Edit);
            if (!corporate) nav.EnableAdvanced();

            LazyPageCase[] pages = corporate ? CorporatePages : StandardPages;

            // (1) Nothing is created before it is opened.
            foreach (LazyPageCase p in pages)
                Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(p.RootId)));

            foreach (LazyPageCase p in pages)
            {
                SelectNavItem(app, p.NavId);

                // (2) Opening creates the page and exposes its root AutomationId.
                UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(p.RootId)) != null,
                    p.Workspace + " page root (" + p.RootId + ") created", TimeSpan.FromSeconds(20));

                // (3) Its landmark content is visible (not a blank page) and no error state is showing.
                if (p.Landmark != null) nav.WaitForWorkspace(p.Landmark, TimeSpan.FromSeconds(20));
                else UiWait.ById(app.MainWindow, p.LandmarkId!, p.LandmarkType);
                Assert.Null(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Workspace_LoadError_Panel")));
                VerifyCapture.Shot(app, name + "-" + p.Workspace);
            }

            // (4) A second visit reuses the same page: still exactly one root, no creation logged twice.
            LazyPageCase again = pages[0];
            SelectNavItem(app, corporate ? "Navigation_Chat_Item" : pages[^1].NavId);
            SelectNavItem(app, again.NavId);
            UiWait.For(() => app.MainWindow.FindAllDescendants(cf => cf.ByAutomationId(again.RootId)).Length == 1,
                "exactly one " + again.Workspace + " page after a second visit");

            // (5) The log proves every page was created once and nothing failed or threw.
            string log = File.Exists(app.LogPath) ? File.ReadAllText(app.LogPath) : "";
            foreach (LazyPageCase p in pages)
            {
                string created = "Workspace page created on first open: " + p.Workspace;
                Assert.Equal(1, CountOccurrences(log, created));
            }
            string[] bad = log.Split('\n').Where(l =>
                l.Contains("could not be created", StringComparison.Ordinal)
                || l.Contains("failed while opening", StringComparison.Ordinal)
                || l.Contains("failed while hiding", StringComparison.Ordinal)
                || l.Contains("FATAL", StringComparison.Ordinal)
                || l.Contains("xception", StringComparison.Ordinal)).ToArray();
            Assert.True(bad.Length == 0, "Suspect app log lines:\n" + string.Join("\n", bad));
        }
        catch (Exception ex)
        {
            try { VerifyCapture.Shot(app, name + "-FAILURE"); } catch { /* the capture must not hide the real failure */ }
            app.CaptureFailure(name, ex);
            throw;
        }
    }

    /// <summary>Selects a navigation item through the UIA SelectionItem pattern only; never falls back to raw input.</summary>
    private static void SelectNavItem(UiTestApplication app, string navId)
    {
        AutomationElement item = UiWait.ById(app.MainWindow, navId, ControlType.ListItem);
        try { item.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch { /* not exposed in a compact pane */ }
        var selection = item.Patterns.SelectionItem.PatternOrDefault
            ?? throw new InvalidOperationException(navId + " does not expose the UIA SelectionItem pattern.");
        selection.Select();
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
