using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views;
public sealed partial class ManualGameLibraryView
{
    private void BuildGamingStrip()
    {
        _gamingStripText.Text = T("Gaming mode is active. AI is off.", "Modalità Gaming attiva. L'AI è spenta.");
        AutomationProperties.SetAutomationId(_gamingStripText, "GameLibrary_Strip_Status_Text");
        _gamingStripReturn.Content = T("Return to AI", "Torna all'AI");
        _gamingStripReturn.MinHeight = 44;
        _gamingStripReturn.MinWidth = 44;
        _gamingStripReturn.IsEnabled = false;
        AutomationProperties.SetAutomationId(_gamingStripReturn, "GameLibrary_Strip_ReturnToAi_Button");
        AutomationProperties.SetName(_gamingStripReturn, T("Return to AI without closing the game", "Torna all'AI senza chiudere il gioco"));
        // The same handler as the Devices & session button: same confirmation dialog and outcome message.
        _gamingStripReturn.Click += async (_, _) => await ReturnToAiAsync();
        var layout = new Grid { ColumnSpacing = 12 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_gamingStripReturn, 1);
        layout.Children.Add(_gamingStripText);
        layout.Children.Add(_gamingStripReturn);
        _gamingStrip.Child = layout;
        if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out object? brush) && brush is Microsoft.UI.Xaml.Media.Brush fill)
            _gamingStrip.Background = fill;
        AutomationProperties.SetAutomationId(_gamingStrip, "GameLibrary_Strip");
        AutomationProperties.SetName(_gamingStrip, T("Gaming session", "Sessione Gaming"));
    }

    /// <summary>Shows the strip only while Gaming mode is active (same condition as the Return to AI button on Devices &amp; session).</summary>
    private void SyncGamingStrip()
    {
        _gamingStrip.Visibility = _returnToAiAvailable && !GamingOnlySetup ? Visibility.Visible : Visibility.Collapsed;
        _gamingStripReturn.IsEnabled = _returnToAiAvailable && !_busy;
    }

    /// <summary>
    /// A TV-scale presentation of the SAME game library and its existing actions.
    /// Changes only WinUI spacing/type, not stored settings, monitor modes or Windows
    /// input configuration. The desktop layout is restored when Console exits.
    /// </summary>
    public void ApplyConsoleMode(bool enabled)
    {
        _consolePresentation = enabled;
        _root.MaxWidth = enabled ? 1800 : 1500;
        // Console Home is for playing and managing the session. Desktop-only
        // filesystem editing remains available unchanged in normal mode.
        Visibility editorVisibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        _manualAddIntro.Visibility = editorVisibility;
        _name.Visibility = editorVisibility;
        _path.Visibility = editorVisibility;
        _manualAddActions.Visibility = editorVisibility;
        _installerSection.Visibility = editorVisibility;
        _downloads.Visibility = editorVisibility;
        if (enabled) _inspectionCancellation?.Cancel();
        _root.Padding = new Thickness(enabled ? 32 : 24);
        _root.Spacing = enabled ? 24 : 16;
        _heading.FontSize = enabled ? 40 : 28;
        _games.Spacing = enabled ? 16 : 8;
        _discoveredGames.Spacing = enabled ? 16 : 8;

        double bodySize = enabled ? 20 : 14;
        foreach (TextBox box in new[] { _name, _path, _filter })
            box.FontSize = bodySize;
        _gamingStripText.FontSize = bodySize;
        _gamingStripReturn.FontSize = bodySize;
        _gamingStripReturn.MinHeight = enabled ? 56 : 44;
        foreach (Button button in new[] { _add, _browse, _returnToAi, _gamingStatusRefresh, _allStoresRefresh, _displayRefresh, _controllerRefresh, _steamRefresh, _epicRefresh, _gogRefresh, _previous, _next, _detectedPrevious, _detectedNext })
        {
            button.FontSize = bodySize;
            button.MinHeight = enabled ? 56 : 32;
        }
        _status.FontSize = bodySize;
        _storeDiscoveryOfferText.FontSize = bodySize;
        _storeDiscoveryOfferScan.FontSize = bodySize;
        _storeDiscoveryOfferDismiss.FontSize = bodySize;
        _storeDiscoveryOfferScan.MinHeight = enabled ? 56 : 44;
        _storeDiscoveryOfferDismiss.MinHeight = enabled ? 56 : 44;
        _universal?.SetConsolePresentation(enabled);
        if (_sectionBar is not null)
            foreach (SelectorBarItem item in _sectionBar.Items.OfType<SelectorBarItem>())
            { item.FontSize = enabled ? 22 : 14; item.MinHeight = enabled ? 56 : 44; }
        _discoveryStatus.FontSize = bodySize;
        _displayStatus.FontSize = bodySize;
        _controllerStatus.FontSize = bodySize;
        _installerStatus.FontSize = bodySize;
        _inspectInstaller.FontSize = bodySize;
        _inspectInstaller.MinHeight = enabled ? 56 : 32;
        _cancelInspection.FontSize = bodySize;
        _cancelInspection.MinHeight = enabled ? 56 : 32;
        _gamingStatus.FontSize = bodySize;
        RenderGames(); // focusable actions remain identical, only geometry changes
        RenderDiscoveredGames(); // reuse the cached scan; never rescan disks for a UI resize
    }

    /// <summary>Controller-first focus target; reuses the exact same UI actions
    /// as desktop and never installs an input remapper.</summary>
    public void FocusConsoleHome() => _gamingStatusRefresh.Focus(FocusState.Programmatic);

    /// <summary>While a consent dialog or command is active, Console-wide B/Escape
    /// must not tear down the fullscreen presenter beneath that interaction.</summary>
    public bool IsInteractionBusy => _busy;

    /// <summary>Starts one concrete installation of a title through the same confirmation dialogs as the lists below.</summary>
    private async Task LaunchInstallationAsync(GameInstallationDto installation)
    {
        if (installation.Source is GameInstallationSources.Manual or GameInstallationSources.Emulator)
        {
            ManualGameDto? entry = _allGames.FirstOrDefault(g => g.Id == installation.SourceGameId);
            if (entry is null) { await LoadAsync(); entry = _allGames.FirstOrDefault(g => g.Id == installation.SourceGameId); }
            if (entry is null)
            {
                _status.Text = T("That library entry no longer exists.", "Questa voce della libreria non esiste più.");
                return;
            }
            await LaunchAsync(entry);
            return;
        }

        // Store launches only need the store and the game id; the Core re-discovers and re-validates the install.
        var discovered = new DiscoveredGameDto(installation.Source, installation.SourceGameId, installation.Name, string.Empty);
        switch (installation.Source)
        {
            case GameInstallationSources.Steam: await LaunchSteamAsync(discovered); break;
            case GameInstallationSources.Epic: await LaunchEpicAsync(discovered); break;
            case GameInstallationSources.Gog: await LaunchGogAsync(discovered); break;
            case GameInstallationSources.Ea:
            case GameInstallationSources.BattleNet:
            case GameInstallationSources.Ubisoft:
            case GameInstallationSources.Xbox: await LaunchExtraStoreAsync(discovered); break;
        }
    }

    /// <summary>Console shoulder buttons: page the manual list, or the store list when the manual list has no further page.</summary>
    public void PageLists(int delta)
    {
        if (_busy) return;
        if (delta > 0)
        {
            if (_next.IsEnabled) { _page++; RenderGames(); }
            else if (_lastDetectedWindow.HasNext) { _detectedPage++; RenderDiscoveredGames(); }
        }
        else
        {
            if (_detectedPage > 0) { _detectedPage--; RenderDiscoveredGames(); }
            else if (_page > 0) { _page--; RenderGames(); }
        }
    }

    /// <summary>Console right stick: scroll the page without moving focus.</summary>
    public void ScrollBy(double pixels)
    {
        if (Content is ScrollViewer viewer)
            viewer.ChangeView(null, Math.Clamp(viewer.VerticalOffset + pixels, 0, viewer.ScrollableHeight), null, disableAnimation: true);
    }

    /// <summary>Console Menu button: jump to the filter field, where A/Y opens the on-screen keyboard.</summary>
    public void FocusSearch() => _filter.Focus(FocusState.Programmatic);

    /// <summary>Leaving the Games workspace cancels any expensive local hash
    /// before the next workspace resumes interactive AI activity.</summary>
    public void CancelExpensiveWork()
    {
        _inspectionCancellation?.Cancel();
        _universal?.CancelPendingQueries();
    }

    private bool GamingOnlySetup =>
        SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId) == SetupProfile.GamingOnly;

    private void ApplySetupProfilePresentation()
    {
        bool gamingOnly = GamingOnlySetup;
        _returnToAi.Visibility = gamingOnly ? Visibility.Collapsed : Visibility.Visible;
        SyncGamingStrip();
        _sessionHeading.Text = gamingOnly
            ? T("Gaming session", "Sessione Gaming")
            : T("AI / Gaming session", "Sessione AI / Gaming");
        _universal?.ApplySetupProfilePresentation();
    }
}
