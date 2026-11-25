using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using PaRuX.Views;

namespace PaRuX
{
    /// <summary>
    /// First-class workspace navigation and recent conversations share the NavigationView's native
    /// MenuItems scroll surface, so neither section can paint over or make the other unreachable.
    /// </summary>
    public sealed partial class MainWindow
    {
        private bool _synchronizingStudioModeToggles;

        private bool _applyingNavigationMode;
        private bool _mediaEditSourceBridgeSubscribed;

        private enum StudioWorkspace
        {
            Chat,
            Create,
            Projects,
            ModelHub,
            ModelRouting,
            Lifecycle,
            Workflows,
            Tools,
            Observability,
            Updates,
            Runtime,
            Games,
            DocumentSearch,
            GamesTools
        }

        private void NavView_Loaded(object sender, RoutedEventArgs e)
        {
            GameLibraryNavItem.Content = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "Giochi" : "Games";
            _applyingNavigationMode = true;
            try
            {
                bool studioModeEnabled = SettingsStore.Current.Data.StudioModeEnabled;
                StudioModeToggle.IsOn = studioModeEnabled;
                ContentStudioModeToggle.IsOn = studioModeEnabled;
            }
            finally
            {
                _applyingNavigationMode = false;
            }
            UpdateContentStudioModeControlsVisibility(NavView.IsPaneOpen);
            ApplyNavigationMode(navigateHomeIfHidden: false);
            RestoreModeTabFromSettings();
            ApplySetupProfile();

            if (!_mediaEditSourceBridgeSubscribed)
            {
                _mediaEditSourceBridgeSubscribed = true;
                MediaEditSourceBridge.Current.EditSourceRequested += (_, _) =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (EnsurePage(StudioWorkspace.Create) is CreateWorkspaceView createView)
                            MediaEditSourceBridge.TryApplyToCreateView(createView);
                        ShowStudioWorkspace(StudioWorkspace.Create);
                    });
                };
            }
        }

        /// <summary>Opens the Model Hub (on its Start tab) from anywhere, e.g. the chat model picker's "Get more models".</summary>
        public void ShowModelHub()
        {
            ShowStudioWorkspace(StudioWorkspace.ModelHub);
            ModelHubPage.OnShown();
        }

        /// <summary>Opens the Model Hub straight on the Discover tab (used by the Chat model picker).</summary>
        public void ShowModelHubDiscover()
        {
            ShowModelHub();
            ModelHubPage.ShowDiscoverTab();
        }

        private void StudioModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_applyingNavigationMode || _synchronizingStudioModeToggles) return;

            bool studioModeEnabled = sender is ToggleSwitch source
                ? source.IsOn
                : StudioModeToggle.IsOn;
            _synchronizingStudioModeToggles = true;
            try
            {
                StudioModeToggle.IsOn = studioModeEnabled;
                ContentStudioModeToggle.IsOn = studioModeEnabled;
            }
            finally
            {
                _synchronizingStudioModeToggles = false;
            }

            SettingsStore.Current.Data.StudioModeEnabled = studioModeEnabled;
            SettingsStore.Current.Save();
            ApplyNavigationMode(navigateHomeIfHidden: true);
        }

        private const string AdvancedWorkspaceList =
            "Model routing, Runtime, Lifecycle Lab, Workflows, Tools, Observability and Updates";

        private void ApplyNavigationMode(bool navigateHomeIfHidden)
        {
            bool studio = StudioModeToggle.IsOn;
            Visibility advanced = studio ? Visibility.Visible : Visibility.Collapsed;

            ModelRoutingNavItem.Visibility = advanced;
            LifecycleNavItem.Visibility = advanced;
            WorkflowNavItem.Visibility = advanced;
            ToolsNavItem.Visibility = advanced;
            ObservabilityNavItem.Visibility = advanced;
            UpdatesNavItem.Visibility = advanced;
            RuntimeNavItem.Visibility = advanced;
            ChatPage.ApplyInterfaceMode(studio);
            ModelHubPageIfCreated?.ApplyInterfaceMode(studio);
            ApplyModeTabVisibility();

            if (!studio && navigateHomeIfHidden && !IsGamingModeTab)
            {
                ShowChat();
                RefreshHistory();
            }
        }

        private bool IsAdvancedWorkspaceVisible() =>
            IsLazyPageVisible(StudioWorkspace.ModelRouting)
            || IsLazyPageVisible(StudioWorkspace.Lifecycle)
            || IsLazyPageVisible(StudioWorkspace.Workflows)
            || IsLazyPageVisible(StudioWorkspace.Tools)
            || IsLazyPageVisible(StudioWorkspace.Observability)
            || IsLazyPageVisible(StudioWorkspace.Updates)
            || IsLazyPageVisible(StudioWorkspace.Runtime);

        private void HideAllWorkspacePages()
        {
            if (ModelHubPageIfCreated is { Visibility: Visibility.Visible } visibleHub)
                visibleHub.OnHidden();
            if (ModelHubPageIfCreated is { } hub) hub.Visibility = Visibility.Collapsed;
            HideLazyPages();
            GameLibraryPageIfCreated?.CancelExpensiveWork();
            if (GameLibraryPageIfCreated is { } games) games.Visibility = Visibility.Collapsed;
        }

        private bool _onboardingHandoffActive;

        private void OnFirstModelRequested(string repoId)
        {
            // The recommended first-model path hands off to Model Hub and MUST return to onboarding;
            // OnboardingCompleted is NOT marked true until the user finishes setup with working chat evidence.
            _onboardingHandoffActive = true;
            if (OnboardingReturnBanner != null) OnboardingReturnBanner.IsOpen = true;
            NavView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;

            ShowStudioWorkspace(StudioWorkspace.ModelHub);
            if (string.IsNullOrWhiteSpace(repoId))
            {
                if (CorporateMode.IsActive) ModelHubPage.OnShown();
                else ModelHubPage.OnShownBrowsingChatModels();
            }
            else
                ModelHubPage.OnShownSearching(repoId, OnboardingPage.SuggestedQuantHintForHandoff);
            GuidedTourDecision tourDecision = GuidedTourTriggerPolicy.EvaluateHandoff(
                SettingsStore.Current.Data.GuidedTourCompleted,
                "ModelHub");
            if (tourDecision.ShouldStart)
            {
                _dispatcher.TryEnqueue(MaybeStartGuidedTour);
            }
        }

        private void ReturnToOnboardingButton_Click(object sender, RoutedEventArgs e)
        {
            ShowOnboarding();
        }

        private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            if (_onboardingHandoffActive)
            {
                ShowOnboarding();
            }
        }

        /// <summary>Opens the Runtime Manager workspace (engine installs), whichever interface mode is active.</summary>
        public void ShowRuntimeManager() => TryNavigateWorkspace("__runtime__");

        /// <summary>Opens the slim Games tools page (same setup-profile gate as Games).</summary>
        internal void ShowGamesTools() => TryNavigateWorkspace(WorkspaceNavigationOrder.GamesToolsWorkspaceTag);

        /// <summary>
        /// The Games home: since step 2 of the gaming-removal map it is the slim Games tools page (Playnite, AI mods, downloads,
        /// handoff, guide, controller tools). The native library (Gaming Home, shelves, detail) is no longer a destination.
        /// Callers must already have checked the setup-profile gate (ShowStudioWorkspace re-checks it).
        /// </summary>
        private void ShowGamesHome()
        {
            ShowStudioWorkspace(StudioWorkspace.GamesTools);
            NotifyLazyPageShown(StudioWorkspace.GamesTools);
        }

        /// <summary>From Games tools: opens one existing section of the Games library page (Downloads, Add, Devices...). Navigation only.</summary>
        internal void ShowGamesLibrarySection(string section)
        {
            if (!IsWorkspaceAllowedForSetup(WorkspaceNavigationOrder.GamesWorkspaceTag)) return;
            ShowStudioWorkspace(StudioWorkspace.Games);
            GameLibraryPage.OnShown();
            GameLibraryPage.OpenSection(section);
        }

        /// <summary>From Games tools: opens Settings > Gaming and AI Sharing on one existing section (see SettingsView.GamingSection*).</summary>
        internal void ShowGamingAiSharingSettings(string? sectionAutomationId)
        {
            ShowSettings();
            SettingsPage.ShowGamingAiSharingSection(sectionAutomationId);
        }

        /// <summary>
        /// "PaRuX.exe --play key" (a desktop or Steam shortcut): bring PaRuX forward, open Games and run the normal Play
        /// path for that title. The usual confirmation still comes first; nothing starts silently.
        /// </summary>
        internal void OpenGameFromShortcut(string titleKey)
        {
            bool setupOpen = OnboardingPage.Visibility == Visibility.Visible;
            if (setupOpen || !IsWorkspaceAllowedForSetup("__games__"))
            {
                // Never dropped silently: the person clicked a shortcut and is told why nothing started.
                Log.Write("Game shortcut not started: first-run setup is open or the Games workspace is off for this setup profile.");
                App.ShowMainWindow();
                _ = ShowShortcutNoticeAsync(setupOpen
                    ? PaRuX.Services.UiText.Pick(
                        "PaRuX is still being set up, so the game was not started. Finish the setup, then click the shortcut again.",
                        "PaRuX è ancora in fase di configurazione, quindi il gioco non è stato avviato. Termina la configurazione, poi fai di nuovo clic sul collegamento.")
                    : PaRuX.Services.UiText.Pick(
                        "The Games area is turned off in your setup profile, so the game was not started. Change the profile in Settings to use game shortcuts.",
                        "L'area Giochi è disattivata nel tuo profilo di configurazione, quindi il gioco non è stato avviato. Cambia profilo nelle Impostazioni per usare i collegamenti ai giochi."));
                return;
            }
            App.ShowMainWindow();
            ShowStudioWorkspace(StudioWorkspace.Games);
            GameLibraryPage.OnShown();
            _ = GameLibraryPage.PlayRequestedByShortcutAsync(titleKey);
        }

        private bool _shortcutNoticeOpen;

        private async System.Threading.Tasks.Task ShowShortcutNoticeAsync(string message)
        {
            if (_shortcutNoticeOpen || Content?.XamlRoot is null) return;
            _shortcutNoticeOpen = true;
            try
            {
                await new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = PaRuX.Services.UiText.Pick("Game shortcut", "Collegamento al gioco"),
                    Content = new Microsoft.UI.Xaml.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "OK"
                }.ShowAsync();
            }
            catch (Exception ex) { Log.Write("Shortcut notice failed: " + ex.Message); }
            finally { _shortcutNoticeOpen = false; }
        }

        private bool TryNavigateWorkspace(string tag)
        {
            // A link, keyboard action or UIA invoke must obey the SAME profile as the
            // sidebar. Visibility is not a sufficient access boundary: a hidden item
            // or old deep link could otherwise open a different product surface.
            // Only "__name__" tags are workspaces. Anything else is a saved conversation id: the profile gate
            // must not swallow it (it used to, so clicking a saved chat silently did nothing).
            if (!tag.StartsWith("__", StringComparison.Ordinal)) return false;
            if (!IsWorkspaceAllowedForSetup(tag)) return true;
            switch (tag)
            {
                // "__games__" (sidebar, Ctrl+2, mode tab, old deep links) and "__gamestools__" open the same slim Games tools page.
                case WorkspaceNavigationOrder.GamesWorkspaceTag:
                case WorkspaceNavigationOrder.GamesToolsWorkspaceTag:
                    ShowGamesHome();
                    return true;
                case "__chat__":
                    ShowChat();
                    return true;
                case "__create__":
                    ShowStudioWorkspace(StudioWorkspace.Create);
                    NotifyLazyPageShown(StudioWorkspace.Create);
                    return true;
                case "__documents__":
                    ShowStudioWorkspace(StudioWorkspace.DocumentSearch);
                    NotifyLazyPageShown(StudioWorkspace.DocumentSearch);
                    return true;
                case "__projects__":
                    ShowStudioWorkspace(StudioWorkspace.Projects);
                    NotifyLazyPageShown(StudioWorkspace.Projects);
                    return true;
                case "__modelhub__":
                    ShowStudioWorkspace(StudioWorkspace.ModelHub);
                    ModelHubPage.OnShown();
                    return true;
                case "__modelrouting__":
                    ShowStudioWorkspace(StudioWorkspace.ModelRouting);
                    NotifyLazyPageShown(StudioWorkspace.ModelRouting);
                    return true;
                case "__lifecycle__":
                    ShowStudioWorkspace(StudioWorkspace.Lifecycle);
                    NotifyLazyPageShown(StudioWorkspace.Lifecycle);
                    return true;
                case "__workflows__":
                    ShowStudioWorkspace(StudioWorkspace.Workflows);
                    NotifyLazyPageShown(StudioWorkspace.Workflows);
                    return true;
                case "__tools__":
                    ShowStudioWorkspace(StudioWorkspace.Tools);
                    NotifyLazyPageShown(StudioWorkspace.Tools);
                    return true;
                case "__observability__":
                    ShowStudioWorkspace(StudioWorkspace.Observability);
                    NotifyLazyPageShown(StudioWorkspace.Observability);
                    return true;
                case "__updates__":
                    ShowStudioWorkspace(StudioWorkspace.Updates);
                    NotifyLazyPageShown(StudioWorkspace.Updates);
                    return true;
                case "__runtime__":
                    ShowStudioWorkspace(StudioWorkspace.Runtime);
                    NotifyLazyPageShown(StudioWorkspace.Runtime);
                    return true;
                case "__help__":
                    _ = ShowInAppHelpDialogAsync();
                    return true;
                case "__settings__":
                    ShowSettings();
                    return true;
                default:
                    return false;
            }
        }

        private void ShowStudioWorkspace(StudioWorkspace workspace)
        {
            if (!IsWorkspaceAllowedForSetup(NavigationItemFor(workspace).Tag as string))
                return;
            if (workspace == StudioWorkspace.Chat)
            {
                ShowChat();
                return;
            }

            SyncModeTabToWorkspace(gaming: workspace is StudioWorkspace.Games or StudioWorkspace.GamesTools);
            SetNavPaneVisible(true);
            ChatPage.Visibility = Visibility.Collapsed;
            if (SettingsPageIfCreated is { } settingsPage) settingsPage.Visibility = Visibility.Collapsed;
            OnboardingPage.Visibility = Visibility.Collapsed;
            HideAllWorkspacePages();
            SelectWorkspaceNavigationItem(NavigationItemFor(workspace));

            switch (workspace)
            {
                case StudioWorkspace.Create: ShowLazyPage(StudioWorkspace.Create); break;
                case StudioWorkspace.Projects: ShowLazyPage(StudioWorkspace.Projects); break;
                case StudioWorkspace.ModelHub: ModelHubPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.ModelRouting: ShowLazyPage(StudioWorkspace.ModelRouting); break;
                case StudioWorkspace.Lifecycle: ShowLazyPage(StudioWorkspace.Lifecycle); break;
                case StudioWorkspace.Workflows: ShowLazyPage(StudioWorkspace.Workflows); break;
                case StudioWorkspace.Tools: ShowLazyPage(StudioWorkspace.Tools); break;
                case StudioWorkspace.Observability: ShowLazyPage(StudioWorkspace.Observability); break;
                case StudioWorkspace.Updates: ShowLazyPage(StudioWorkspace.Updates); break;
                case StudioWorkspace.Runtime: ShowLazyPage(StudioWorkspace.Runtime); break;
                case StudioWorkspace.Games: GameLibraryPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.DocumentSearch: ShowLazyPage(StudioWorkspace.DocumentSearch); break;
                case StudioWorkspace.GamesTools: ShowLazyPage(StudioWorkspace.GamesTools); break;
            }
        }

        private bool IsWorkspaceAllowedForSetup(string? tag) =>
            WorkspaceNavigationOrder.CanOpenWorkspace(tag,
                SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId),
                CorporateMode.IsActive);

        /// <summary>If the user switches from Both to AI-only or Gaming-only while on
        /// an incompatible page, leave that page immediately. Also repairs the active
        /// page after restart or restored deep links. Never switches away from first-run setup.</summary>
        private void EnsureWorkspaceFitsSetupProfile()
        {
            if (OnboardingPage.Visibility == Visibility.Visible) return;
            string? activeTag = ActiveWorkspaceTag();
            if (activeTag == null || IsWorkspaceAllowedForSetup(activeTag)) return;

            if (CurrentSetupPlan.StartInGamingTab)
            {
                ShowGamesHome();
            }
            else
            {
                ShowChat();
            }
        }

        private string? ActiveWorkspaceTag()
        {
            if (GameLibraryPageIfCreated is { Visibility: Visibility.Visible }) return "__games__";
            if (IsLazyPageVisible(StudioWorkspace.GamesTools)) return WorkspaceNavigationOrder.GamesToolsWorkspaceTag;
            if (IsLazyPageVisible(StudioWorkspace.Create)) return "__create__";
            if (IsLazyPageVisible(StudioWorkspace.Projects)) return "__projects__";
            if (ModelHubPageIfCreated is { Visibility: Visibility.Visible }) return "__modelhub__";
            if (IsLazyPageVisible(StudioWorkspace.DocumentSearch)) return "__documents__";
            if (IsLazyPageVisible(StudioWorkspace.ModelRouting)) return "__modelrouting__";
            if (IsLazyPageVisible(StudioWorkspace.Lifecycle)) return "__lifecycle__";
            if (IsLazyPageVisible(StudioWorkspace.Workflows)) return "__workflows__";
            if (IsLazyPageVisible(StudioWorkspace.Tools)) return "__tools__";
            if (IsLazyPageVisible(StudioWorkspace.Observability)) return "__observability__";
            if (IsLazyPageVisible(StudioWorkspace.Updates)) return "__updates__";
            if (IsLazyPageVisible(StudioWorkspace.Runtime)) return "__runtime__";
            if (ChatPage.Visibility == Visibility.Visible) return "__chat__";
            if (SettingsPageIfCreated is { Visibility: Visibility.Visible }) return "__settings__";
            return null;
        }

        private void SelectWorkspaceNavigationItem(NavigationViewItem? selected)
        {
            // Corporate Model Hub can open from Chat's model picker while absent
            // from the compact sidebar. Never focus a collapsed navigation item.
            if (selected != null && selected.Visibility != Visibility.Visible)
                selected = ChatNavItem.Visibility == Visibility.Visible ? ChatNavItem : null;
            NavView.SelectedItem = selected;
            selected?.Focus(FocusState.Programmatic);
        }

        private NavigationViewItem[] WorkspaceNavigationItems() => new[]
        {
            ChatNavItem, CreateNavItem, ProjectsNavItem, GameLibraryNavItem, DocumentSearchNavItem, ModelHubNavItem, ModelRoutingNavItem,
            LifecycleNavItem, WorkflowNavItem, ToolsNavItem, ObservabilityNavItem,
            UpdatesNavItem, RuntimeNavItem, SettingsNavItem, HelpNavItem
        };

        private NavigationViewItem NavigationItemFor(StudioWorkspace workspace) => workspace switch
        {
            StudioWorkspace.Chat => ChatNavItem,
            StudioWorkspace.Games or StudioWorkspace.GamesTools => GameLibraryNavItem,
            StudioWorkspace.DocumentSearch => DocumentSearchNavItem,
            StudioWorkspace.Create => CreateNavItem,
            StudioWorkspace.Projects => ProjectsNavItem,
            StudioWorkspace.ModelHub => ModelHubNavItem,
            StudioWorkspace.ModelRouting => ModelRoutingNavItem,
            StudioWorkspace.Lifecycle => LifecycleNavItem,
            StudioWorkspace.Workflows => WorkflowNavItem,
            StudioWorkspace.Tools => ToolsNavItem,
            StudioWorkspace.Observability => ObservabilityNavItem,
            StudioWorkspace.Updates => UpdatesNavItem,
            StudioWorkspace.Runtime => RuntimeNavItem,
            _ => CreateNavItem
        };
    }
}
