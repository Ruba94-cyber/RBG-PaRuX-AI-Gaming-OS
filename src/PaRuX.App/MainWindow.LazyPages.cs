using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;
using PaRuX.Views;

namespace PaRuX
{
    /// <summary>
    /// The ten advanced workspace pages (Create, Projects, Document Search, Model Intelligence, Lifecycle Lab, Workflows,
    /// Tools, Observability, Updates, Runtime) build their whole UI in code, which costs time on the first-paint path.
    /// They are therefore NOT declared in MainWindow.xaml: each one is constructed in code the first time it is opened
    /// and added to <c>WorkspacePagesHost</c>. (An earlier attempt used x:Load="False" and <c>FindName</c>, which returns
    /// null for the element in this window and left a blank page; constructing the control directly cannot fail that way.)
    /// A page that cannot be created is logged in full and replaced by a visible error panel with a Retry button.
    /// </summary>
    public sealed partial class MainWindow
    {
        private sealed class LazyWorkspacePage
        {
            public LazyWorkspacePage(string automationId, string nameEn, string nameIt, Func<FrameworkElement> factory)
            {
                AutomationId = automationId;
                NameEn = nameEn;
                NameIt = nameIt;
                Factory = factory;
            }

            public string AutomationId { get; }
            public string NameEn { get; }
            public string NameIt { get; }
            public Func<FrameworkElement> Factory { get; }
            public FrameworkElement? Instance { get; set; }
        }

        private readonly Dictionary<StudioWorkspace, LazyWorkspacePage> _lazyPages = new()
        {
            [StudioWorkspace.Create] = new("Workspace_Create_Page", "Create", "Crea", () => new CreateWorkspaceView()),
            [StudioWorkspace.Projects] = new("Workspace_Projects_Page", "Projects", "Progetti", () => new ProjectsWorkspaceView()),
            [StudioWorkspace.DocumentSearch] = new("Workspace_DocumentSearch_Page", "Document search", "Ricerca documenti", () => new DocumentSearchView()),
            [StudioWorkspace.ModelRouting] = new("Workspace_ModelRouting_Page", "Model Intelligence", "Model Intelligence", () => new ModelRoutingPanel()),
            [StudioWorkspace.Lifecycle] = new("Workspace_Lifecycle_Page", "Lifecycle Lab", "Lifecycle Lab", () => new LifecycleLabView()),
            [StudioWorkspace.Workflows] = new("Workspace_Workflows_Page", "Workflows", "Workflow", () => new WorkflowWorkspaceView()),
            [StudioWorkspace.Tools] = new("Workspace_Tools_Page", "Tools and automations", "Strumenti e automazioni", () => new ToolsAutomationView()),
            [StudioWorkspace.Observability] = new("Workspace_Observability_Page", "Observability", "Osservabilità", () => new ObservabilityView()),
            [StudioWorkspace.Updates] = new("Workspace_Updates_Page", "Updates", "Aggiornamenti", () => new UpdateManagerView()),
            [StudioWorkspace.Runtime] = new("Workspace_Runtime_Page", "Runtime", "Runtime", () => new RuntimeManagerView()),
        };

        private Grid? _lazyPageErrorPanel;
        private TextBlock? _lazyPageErrorText;
        private StudioWorkspace _lazyPageErrorWorkspace;

        private static bool IsLazyWorkspace(StudioWorkspace workspace) => workspace switch
        {
            StudioWorkspace.Create or StudioWorkspace.Projects or StudioWorkspace.DocumentSearch or StudioWorkspace.ModelRouting
                or StudioWorkspace.Lifecycle or StudioWorkspace.Workflows or StudioWorkspace.Tools or StudioWorkspace.Observability
                or StudioWorkspace.Updates or StudioWorkspace.Runtime => true,
            _ => false,
        };

        /// <summary>The page if it has already been created, otherwise null. Never creates anything.</summary>
        private FrameworkElement? PeekPage(StudioWorkspace workspace) =>
            _lazyPages.TryGetValue(workspace, out LazyWorkspacePage? lazy) ? lazy.Instance : null;

        private bool IsLazyPageVisible(StudioWorkspace workspace) => PeekPage(workspace)?.Visibility == Visibility.Visible;

        /// <summary>Returns the page, creating it on first use. Returns null (after logging) when creation fails.</summary>
        private FrameworkElement? EnsurePage(StudioWorkspace workspace)
        {
            if (!_lazyPages.TryGetValue(workspace, out LazyWorkspacePage? lazy)) return null;
            if (lazy.Instance != null) return lazy.Instance;
            try
            {
                FrameworkElement page = lazy.Factory();
                page.Visibility = Visibility.Collapsed;
                AutomationProperties.SetAutomationId(page, lazy.AutomationId);
                // A UserControl with only an id is dropped from the UIA tree; a name plus the Content view exposes the page root.
                AutomationProperties.SetName(page, UiText.Pick(lazy.NameEn, lazy.NameIt));
                AutomationProperties.SetAccessibilityView(page, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content);
                WorkspacePagesHost.Children.Insert(0, page);
                lazy.Instance = page;
                Log.Write("Workspace page created on first open: " + workspace);
                return page;
            }
            catch (Exception ex)
            {
                Log.Write("Workspace page " + workspace + " could not be created: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex);
                return null;
            }
        }

        /// <summary>Makes the workspace's page visible, or shows the visible error state when it cannot be created.</summary>
        private void ShowLazyPage(StudioWorkspace workspace)
        {
            FrameworkElement? page = EnsurePage(workspace);
            if (page != null)
            {
                page.Visibility = Visibility.Visible;
                return;
            }
            ShowLazyPageError(workspace);
        }

        /// <summary>Calls the page's OnShown refresh. A synchronous failure is logged and shown, never swallowed.</summary>
        private void NotifyLazyPageShown(StudioWorkspace workspace)
        {
            FrameworkElement? page = PeekPage(workspace);
            if (page == null) return;
            try
            {
                switch (page)
                {
                    case CreateWorkspaceView v: v.OnShown(); break;
                    case ProjectsWorkspaceView v: v.OnShown(); break;
                    case DocumentSearchView v: v.OnShown(); break;
                    case ModelRoutingPanel v: v.OnShown(); break;
                    case LifecycleLabView v: v.OnShown(); break;
                    case WorkflowWorkspaceView v: v.OnShown(); break;
                    case ToolsAutomationView v: v.OnShown(); break;
                    case ObservabilityView v: v.OnShown(); break;
                    case UpdateManagerView v: v.OnShown(); break;
                    case RuntimeManagerView v: v.OnShown(); break;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Workspace page " + workspace + " failed while opening: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex);
                page.Visibility = Visibility.Collapsed;
                ShowLazyPageError(workspace);
            }
        }

        /// <summary>Hides every created lazy page (and the error panel); calls OnHidden where a page has it.</summary>
        private void HideLazyPages()
        {
            foreach (KeyValuePair<StudioWorkspace, LazyWorkspacePage> entry in _lazyPages)
            {
                FrameworkElement? page = entry.Value.Instance;
                if (page == null) continue;
                try
                {
                    if (page is DocumentSearchView docs && docs.Visibility == Visibility.Visible) docs.OnHidden();
                }
                catch (Exception ex)
                {
                    Log.Write("Workspace page " + entry.Key + " failed while hiding: " + ex.GetType().Name + ": " + ex.Message);
                }
                page.Visibility = Visibility.Collapsed;
            }
            if (_lazyPageErrorPanel != null) _lazyPageErrorPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowLazyPageError(StudioWorkspace workspace)
        {
            _lazyPageErrorWorkspace = workspace;
            if (_lazyPageErrorPanel == null)
            {
                _lazyPageErrorText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
                AutomationProperties.SetAutomationId(_lazyPageErrorText, "Workspace_LoadError_Text");
                var retry = new Button { Content = UiText.Pick("Try again", "Riprova"), Margin = new Thickness(0, 12, 0, 0) };
                AutomationProperties.SetAutomationId(retry, "Workspace_LoadError_Retry_Button");
                retry.Click += (_, _) =>
                {
                    StudioWorkspace target = _lazyPageErrorWorkspace;
                    _lazyPageErrorPanel!.Visibility = Visibility.Collapsed;
                    ShowStudioWorkspace(target);
                    NotifyLazyPageShown(target);
                };
                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
                stack.Children.Add(_lazyPageErrorText);
                stack.Children.Add(retry);
                _lazyPageErrorPanel = new Grid { Visibility = Visibility.Collapsed };
                AutomationProperties.SetAutomationId(_lazyPageErrorPanel, "Workspace_LoadError_Panel");
                _lazyPageErrorPanel.Children.Add(stack);
                WorkspacePagesHost.Children.Add(_lazyPageErrorPanel);
            }
            _lazyPageErrorText!.Text = UiText.Pick(
                "This workspace could not be opened (" + workspace + "). The details are in the PaRuX log. Nothing was changed.",
                "Questa area di lavoro non si è potuta aprire (" + workspace + "). I dettagli sono nel log di PaRuX. Non è stato modificato nulla.");
            _lazyPageErrorPanel.Visibility = Visibility.Visible;
        }
    }
}
