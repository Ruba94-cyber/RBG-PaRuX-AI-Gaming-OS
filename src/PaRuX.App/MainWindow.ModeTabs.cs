using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PaRuX.Services;
using Windows.System;

namespace PaRuX;

/// <summary>
/// Profile-driven sidebar visibility and Ctrl+1 (Chat) / Ctrl+2 (Games) shortcuts.
/// Gaming-removal step 6 collapsed the old AI | Gaming top tab bar: Games is now an ordinary
/// sidebar workspace whenever the setup profile enables Gaming (Both or GamingOnly), while
/// SettingsStore.Current.Data.LastModeTab remains readable in settings.json for compatibility
/// and is ignored.
/// </summary>
public sealed partial class MainWindow
{
    internal const string ModeKeyAi = "AI";
    internal const string ModeKeyGaming = "Gaming";

    private string _modeTab = ModeKeyAi;
    private bool _modeTabInitialized;

    private bool IsGamingModeTab => !PaRuX.Contracts.CorporateMode.IsActive
        && SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId) == SetupProfile.GamingOnly;

    private static string ModeTabText(string en, string it) => ConsoleText(en, it);

    private void InitializeModeTabs()
    {
        ApplyModeTabLocalization();

        AddModeTabAccelerator(VirtualKey.Number1, ModeKeyAi);
        AddModeTabAccelerator(VirtualKey.Number2, ModeKeyGaming);

        ApplyModeTabVisibility();
    }

    private void AddModeTabAccelerator(VirtualKey key, string tab)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            if (App.IsShuttingDown || NavView.IsPaneVisible == false) return;
            if (tab == ModeKeyGaming)
            {
                if (!CurrentSetupPlan.ShowGamingWorkspace) return;
                TryNavigateWorkspace(WorkspaceNavigationOrder.GamesWorkspaceTag);
                GameLibraryNavItem.Focus(FocusState.Keyboard);
            }
            else
            {
                if (!CurrentSetupPlan.ShowAiWorkspaces) return;
                ShowChat();
                ChatNavItem.Focus(FocusState.Keyboard);
            }
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void ApplyModeTabLocalization()
    {
        ToolTipService.SetToolTip(GameLibraryNavItem, ModeTabText("Games (Ctrl+2)", "Giochi (Ctrl+2)"));
    }

    /// <summary>Called once from NavView_Loaded, after the existing startup page was chosen.</summary>
    private void RestoreModeTabFromSettings()
    {
        if (_modeTabInitialized) return;
        _modeTabInitialized = true;
        SetupProfilePlan setup = CurrentSetupPlan;
        // GamingOnly starts on Games; Both always starts on Chat (obsolete LastModeTab is ignored).
        if (setup.StartInGamingTab && SettingsStore.Current.Data.OnboardingCompleted
            && NavView.IsPaneVisible && ChatPageIfCreated is { Visibility: Visibility.Visible })
        {
            SelectModeTab(ModeKeyGaming, navigate: true);
        }
    }

    private static SetupProfilePlan CurrentSetupPlan =>
        SetupProfilePolicy.Plan(SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId));

    /// <summary>
    /// Applies the setup profile (AI only / Gaming only / Both) to the shell. Nothing is deleted
    /// or uninstalled, and the choice can be changed again in Settings. Safe to call repeatedly.
    /// </summary>
    internal void ApplySetupProfile()
    {
        SetupProfilePlan setup = CurrentSetupPlan;
        if (setup.StartInGamingTab)
        {
            bool settledShell = SettingsStore.Current.Data.OnboardingCompleted && OnboardingPageIfCreated is not { Visibility: Visibility.Visible };
            if (!string.Equals(_modeTab, ModeKeyGaming, StringComparison.Ordinal))
                SelectModeTab(ModeKeyGaming, navigate: settledShell);
        }
        else if (!setup.ShowGamingWorkspace && string.Equals(_modeTab, ModeKeyGaming, StringComparison.Ordinal))
        {
            bool settledShell = SettingsStore.Current.Data.OnboardingCompleted && OnboardingPageIfCreated is not { Visibility: Visibility.Visible };
            SelectModeTab(ModeKeyAi, navigate: settledShell);
        }
        ApplyModeTabVisibility();
        EnsureWorkspaceFitsSetupProfile();
        // The bubble is another AI navigation surface. Gaming-only must hide it
        // immediately rather than waiting for the next game/watchdog event.
        App.BubbleWin?.ApplyVisibilityMode();
    }

    /// <summary>Selects the AI or Games home workspace when permitted by the active setup profile.</summary>
    private void SelectModeTab(string tab, bool navigate)
    {
        tab = string.Equals(tab, ModeKeyGaming, StringComparison.Ordinal) ? ModeKeyGaming : ModeKeyAi;
        SetupProfilePlan setup = CurrentSetupPlan;
        if (tab == ModeKeyGaming && !setup.ShowGamingWorkspace) tab = ModeKeyAi;
        if (tab == ModeKeyAi && !setup.ShowAiWorkspaces) tab = ModeKeyGaming;
        _modeTab = tab;
        if (!navigate) return;

        if (tab == ModeKeyGaming) TryNavigateWorkspace(WorkspaceNavigationOrder.GamesWorkspaceTag);
        else ShowChat();
    }

    /// <summary>Tracks whether the current workspace is Games without navigating or mutating settings.</summary>
    private void SyncModeTabToWorkspace(bool gaming) =>
        _modeTab = gaming ? ModeKeyGaming : ModeKeyAi;

    /// <summary>
    /// Applies visibility only. Collapsed items are neither rendered nor focusable, but still exist
    /// in the NavigationView (same x:Name, Tag and AutomationId).
    /// </summary>
    private void ApplyModeTabVisibility()
    {
        bool corporate = PaRuX.Contracts.CorporateMode.IsActive;
        SetupProfile selected = SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId);
        bool gaming = !corporate && selected == SetupProfile.GamingOnly;
        bool ai = !gaming;
        bool studio = StudioModeToggle.IsOn;
        var visibleIds = WorkspaceNavigationOrder
            .GetOrderedAutomationIds(studio, gaming, selected, corporate)
            .ToHashSet(StringComparer.Ordinal);
        foreach (NavigationViewItem item in WorkspaceNavigationItems())
        {
            string id = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(item);
            item.Visibility = visibleIds.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
        }

        // Corporate is intentionally Chat + documents + Settings/Help. Model Hub stays
        // reachable from Chat model selection/Settings, but not as sidebar clutter.
        WorkspaceSectionHeader.Content = corporate
            ? ModeTabText("Assistant", "Assistente")
            : gaming ? ModeTabText("Games", "Giochi")
                : ModeTabText("Workspaces", "Aree di lavoro");
        StudioModeToggle.Visibility = !corporate && !gaming
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateContentStudioModeControlsVisibility(NavView.IsPaneOpen);
        PlayniteConsoleButton.Visibility = !corporate && CurrentSetupPlan.ShowGamingWorkspace
            ? Visibility.Visible : Visibility.Collapsed;

        // Only AI profiles have conversations. Gaming-only must not expose a chat
        // search box, new-chat action, history, nor a shortcut to those controls.
        SearchBox.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        if (NewChatButton.Parent is FrameworkElement chatButtons)
            chatButtons.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;

        var workspaceItems = WorkspaceNavigationItems();
        foreach (object menuItem in NavView.MenuItems)
        {
            if (ReferenceEquals(menuItem, WorkspaceSectionHeader)) continue;
            if (menuItem is NavigationViewItemBase element
                && !workspaceItems.Any(w => ReferenceEquals(w, menuItem)))
                element.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
