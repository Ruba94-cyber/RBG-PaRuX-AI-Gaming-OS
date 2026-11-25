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
    /// <summary>The TV-scale presentation belonged to the native Console/TV shell, removed in gaming-removal step 3: always desktop.</summary>
    private static bool _consolePresentation => false;

    /// <summary>Tints the library with the active extension theme's background (only when its appearance matches the app theme).</summary>
    private void ApplyLibraryThemeBackground()
    {
        string? hex = LibraryThemeState.BackgroundHex(ActualTheme == ElementTheme.Dark);
        if (hex is not null && GameExtensionManifestValidator.TryParseHex(hex, out (byte R, byte G, byte B) c))
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, c.R, c.G, c.B));
        else
            ClearValue(BackgroundProperty);
    }

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

    /// <summary>While a consent dialog or command is active, pad navigation (Playnite launcher) must not act beneath it.</summary>
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
