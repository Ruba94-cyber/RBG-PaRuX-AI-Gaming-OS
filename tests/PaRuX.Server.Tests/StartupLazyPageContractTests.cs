using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Source contracts for the first-paint startup work. The ten advanced workspace pages are created in code on first
/// open (never x:Load/FindName: FindName returned null for an x:Load element in this window, leaving a blank page).
/// The real behaviour is proven by tests/PaRuX.UITests LazyWorkspacePagesUiTests; timing by tools/measure-app-startup.ps1.</summary>
public sealed class StartupLazyPageContractTests
{
    private static readonly string[] LazyViews =
    {
        "CreateWorkspaceView", "ProjectsWorkspaceView", "DocumentSearchView", "ModelRoutingPanel", "LifecycleLabView",
        "WorkflowWorkspaceView", "ToolsAutomationView", "ObservabilityView", "UpdateManagerView", "RuntimeManagerView",
    };

    [Fact]
    public void Lazy_pages_are_not_declared_in_xaml_and_never_use_x_Load_or_FindName()
    {
        string xaml = Read("src", "PaRuX.App", "MainWindow.xaml");
        Assert.DoesNotContain("x:Load", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WorkspacePagesHost\"", xaml, StringComparison.Ordinal);
        foreach (string view in LazyViews)
            Assert.DoesNotContain("views:" + view, xaml, StringComparison.Ordinal);

        foreach (string file in Directory.GetFiles(Path.Combine(Root(), "src", "PaRuX.App"), "MainWindow*.cs"))
            Assert.DoesNotContain("FindName(", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_lazy_page_is_registered_with_a_factory_and_a_stable_root_AutomationId()
    {
        string lazy = Read("src", "PaRuX.App", "MainWindow.LazyPages.cs");
        foreach (string view in LazyViews)
            Assert.Contains("new " + view + "()", lazy, StringComparison.Ordinal);
        Assert.Equal(10, Regex.Matches(lazy, "\\[StudioWorkspace\\.\\w+\\] = new\\(\"Workspace_\\w+_Page\"").Count);
        Assert.Contains("AutomationProperties.SetAutomationId(page, lazy.AutomationId)", lazy, StringComparison.Ordinal);
    }

    [Fact]
    public void Lazy_page_failures_are_logged_and_shown_never_swallowed_into_a_blank_page()
    {
        string lazy = Read("src", "PaRuX.App", "MainWindow.LazyPages.cs");
        Assert.Contains("could not be created", lazy, StringComparison.Ordinal);
        Assert.Contains("failed while opening", lazy, StringComparison.Ordinal);
        Assert.Contains("Log.Write(", lazy, StringComparison.Ordinal);
        Assert.Contains("Workspace_LoadError_Panel", lazy, StringComparison.Ordinal);
        Assert.Contains("Workspace_LoadError_Retry_Button", lazy, StringComparison.Ordinal);
        Assert.Contains("ShowLazyPageError(workspace)", lazy, StringComparison.Ordinal);
        // Localized: no mixed-language error state.
        Assert.Contains("UiText.Pick(", lazy, StringComparison.Ordinal);
    }

    [Fact]
    public void Lazy_page_fields_no_longer_exist_so_nothing_can_touch_a_page_that_was_not_created()
    {
        string[] fields = { "CreatePage", "ProjectsPage", "DocumentSearchPage", "ModelRoutingPage", "LifecycleLabPage",
            "WorkflowPage", "ToolsAutomationPage", "ObservabilityPage", "UpdateManagerPage", "RuntimeManagerPage" };
        foreach (string file in Directory.GetFiles(Path.Combine(Root(), "src", "PaRuX.App"), "MainWindow*.cs"))
        {
            string text = File.ReadAllText(file);
            foreach (string field in fields)
                Assert.False(Regex.IsMatch(text, "\\b" + field + "\\b"), Path.GetFileName(file) + " still references " + field);
        }
    }

    [Fact]
    public void Startup_defers_the_bubble_window_agent_workers_and_warmup_until_after_the_main_window_is_shown()
    {
        string app = Read("src", "PaRuX.App", "App.xaml.cs");
        int activate = app.IndexOf("MainWin.Activate();", StringComparison.Ordinal);
        int deferredDef = app.IndexOf("private static void FinishDeferredStartup", StringComparison.Ordinal);
        int bubble = app.IndexOf("new BubbleWindow()", StringComparison.Ordinal);
        int worker = app.IndexOf("AgentCodingWorkerService.Current.Start()", StringComparison.Ordinal);
        int warm = app.IndexOf("_ = WarmLocalAiAsync();", StringComparison.Ordinal);
        Assert.True(activate > 0 && deferredDef > activate && bubble > deferredDef && worker > deferredDef && warm > deferredDef);
        Assert.Contains("DispatcherQueuePriority.Low, FinishDeferredStartup", app, StringComparison.Ordinal);
        // The deferred work honours a shutdown or a setup-profile switch that happened in between.
        Assert.Contains("if (IsShuttingDown) return;", app, StringComparison.Ordinal);
        Assert.Contains("SetupProfileRuntime.Running.AgentWorkers", app, StringComparison.Ordinal);
        // Gaming hard-off safety path and the --play hand-off still run before the window exists / are untouched.
        int window = app.IndexOf("new MainWindow()", StringComparison.Ordinal);
        Assert.True(app.IndexOf("GamingManager.Current.Start()", StringComparison.Ordinal) < window);
        Assert.True(app.IndexOf("LocalAiController.Current.StartWatching()", StringComparison.Ordinal) < window);
        Assert.Contains("GameLaunchRequestChannel.TrySubmit", app, StringComparison.Ordinal);
        Assert.Contains("ProcessGameShortcutRequests", app, StringComparison.Ordinal);
    }

    private static string Root()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PARUX_EXECUTION_PLAN.md"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()));
}
