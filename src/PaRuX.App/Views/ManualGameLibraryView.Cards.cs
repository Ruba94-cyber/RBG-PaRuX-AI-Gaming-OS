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
    private void RenderDiscoveredGames()
    {
        _discoveredGames.Children.Clear();
        var matches = GameLibraryQuery.Filter(_lastDetectedGames, _filter.Text,
            (_storeFilter.SelectedItem as ComboBoxItem)?.Tag as string,
            (_stateFilter.SelectedItem as ComboBoxItem)?.Tag as string).ToList();
        GameLibraryPageWindow window = GameLibraryPageWindow.For(matches.Count, _detectedPage);
        _detectedPage = window.Page;
        _lastDetectedWindow = window;
        _detectedPrevious.IsEnabled = !_busy && window.HasPrevious;
        _detectedNext.IsEnabled = !_busy && window.HasNext;
        _detectedRange.Text = window.Count == 0
            ? T("No matching store games.", "Nessun gioco store corrispondente.")
            : T($"Showing store games {window.FirstVisible}–{window.LastVisible} of {window.Total}.",
                $"Mostrati giochi store {window.FirstVisible}–{window.LastVisible} di {window.Total}.");
        foreach (DiscoveredGameDto game in matches.Skip(window.Offset).Take(window.Count))
        {
            var item = new StackPanel
            {
                Spacing = _consolePresentation ? 12 : 2,
                Padding = new Thickness(_consolePresentation ? 20 : 12)
            };
            item.Children.Add(new TextBlock
            {
                Text = game.Name,
                FontSize = _consolePresentation ? 24 : 16,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            item.Children.Add(new TextBlock
            {
                Text = game.ProviderId.ToUpperInvariant() + " #" + game.ProviderGameId + " · " + InstallStateLabel(game.InstallState),
                FontSize = _consolePresentation ? 18 : 14,
                TextWrapping = TextWrapping.Wrap
            });
            var otherStores = GameLibraryQuery.OtherStores(game, _lastDetectedGames);
            if (otherStores.Count > 0)
                item.Children.Add(new TextBlock
                {
                    Text = T("Also found in: ", "Presente anche in: ") + string.Join(", ", otherStores),
                    FontSize = _consolePresentation ? 18 : 14,
                    TextWrapping = TextWrapping.Wrap
                });
            bool canUseStoreHandoff = GameStoreLaunchIdPolicy.SupportsOfficialLaunch(
                game.ProviderId, game.ProviderGameId);
            if (!canUseStoreHandoff)
            {
                item.Children.Add(new TextBlock
                {
                    Text = T("This store entry has no supported launch identifier.",
                        "Questa voce store non ha un identificativo di avvio supportato."),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = _consolePresentation ? 18 : 14
                });
            }
            if (canUseStoreHandoff && string.Equals(game.ProviderId, "steam", StringComparison.OrdinalIgnoreCase))
            {
                var request = new Button
                {
                    Content = T("Play via Steam", "Gioca tramite Steam"),
                    FontSize = _consolePresentation ? 20 : 14,
                    MinHeight = _consolePresentation ? 56 : 32,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                AutomationProperties.SetAutomationId(request, "GameLibrary_Steam_Launch_" + game.ProviderGameId);
                AutomationProperties.SetName(request,
                    T("Request Steam to launch ", "Chiedi a Steam di avviare ") + game.Name);
                request.Click += async (_, _) => await LaunchSteamAsync(game);
                item.Children.Add(request);
            }
            if (canUseStoreHandoff && string.Equals(game.ProviderId, "epic", StringComparison.OrdinalIgnoreCase))
            {
                var request = new Button
                {
                    Content = T("Play via Epic Games Launcher", "Gioca tramite Epic Games Launcher"),
                    FontSize = _consolePresentation ? 20 : 14,
                    MinHeight = _consolePresentation ? 56 : 32,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                AutomationProperties.SetAutomationId(request, "GameLibrary_Epic_Launch_" + game.ProviderGameId);
                AutomationProperties.SetName(request,
                    T("Request Epic Games Launcher to start ", "Chiedi a Epic Games Launcher di avviare ") + game.Name);
                request.Click += async (_, _) => await LaunchEpicAsync(game);
                item.Children.Add(request);
            }
            if (canUseStoreHandoff && string.Equals(game.ProviderId, "gog", StringComparison.OrdinalIgnoreCase))
            {
                var request = new Button
                {
                    Content = T("Play via GOG GALAXY", "Gioca tramite GOG GALAXY"),
                    FontSize = _consolePresentation ? 20 : 14,
                    MinHeight = _consolePresentation ? 56 : 32,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                AutomationProperties.SetAutomationId(request, "GameLibrary_Gog_Launch_" + game.ProviderGameId);
                AutomationProperties.SetName(request,
                    T("Request GOG GALAXY to launch ", "Chiedi a GOG GALAXY di avviare ") + game.Name);
                request.Click += async (_, _) => await LaunchGogAsync(game);
                item.Children.Add(request);
            }
            _discoveredGames.Children.Add(item);
        }
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try { await LoadAsync(); }
        catch (Exception) { _status.Text = T("Could not load the library.", "Impossibile caricare la libreria."); }
        finally { SetBusy(false); }
    }

    private async Task LoadAsync()
    {
        _status.Text = T("Loading…", "Caricamento…");
        var result = await GamingOrchestratorClient.Current.ListManualGamesAsync();
        if (!result.Succeeded || result.Value == null) { _status.Text = T("Could not load the library. Try opening it again.", "Impossibile caricare la libreria. Prova a riaprirla."); return; }
        _allGames = result.Value;
        _page = 0;
        RenderGames();
    }

    private void RenderGames()
    {
        _games.Children.Clear();
        var matches = _allGames.Where(game => game.Name.Contains(_filter.Text ?? "", StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (_page * 100 >= matches.Count) _page = Math.Max(0, (matches.Count - 1) / 100);
        int first = matches.Count == 0 ? 0 : _page * 100 + 1;
        int last = Math.Min(matches.Count, (_page + 1) * 100);
        _status.Text = matches.Count == 0 ? T("No matching games.", "Nessun gioco corrispondente.") :
            T($"Showing {first}–{last} of {matches.Count}.", $"Mostrati {first}–{last} di {matches.Count}.");
        _previous.IsEnabled = _page > 0; _next.IsEnabled = last < matches.Count;
        foreach (ManualGameDto game in matches.Skip(_page * 100).Take(100))
        {
            var card = new StackPanel { Spacing = _consolePresentation ? 12 : 4, Padding = new Thickness(_consolePresentation ? 20 : 12) };
            card.Children.Add(new TextBlock { Text = game.Name, FontSize = _consolePresentation ? 24 : 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(new TextBlock { Text = game.ExecutablePath, FontSize = _consolePresentation ? 16 : 14, TextWrapping = TextWrapping.Wrap });
            bool isApplicationEntry = string.Equals(game.Kind, ManualEntryKinds.Application, StringComparison.OrdinalIgnoreCase);
            string details = (isApplicationEntry ? T("Application", "Applicazione") : T("Game", "Gioco")) +
                (string.IsNullOrEmpty(game.Arguments) ? "" : " · " + T("with arguments", "con argomenti"));
            card.Children.Add(new TextBlock { Text = details, FontSize = _consolePresentation ? 16 : 13, Opacity = 0.8 });
            var launch = new Button
            {
                Content = isApplicationEntry ? T("Start", "Avvia") : T("Play", "Gioca"),
                HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = _consolePresentation ? 20 : 14,
                MinHeight = _consolePresentation ? 56 : 32
            };
            AutomationProperties.SetName(launch, T("Launch ", "Avvia ") + game.Name);
            AutomationProperties.SetAutomationId(launch, "GameLibrary_Launch_" + game.Id);
            launch.Click += async (_, _) => await LaunchAsync(game);
            card.Children.Add(launch);
            var remove = new Button { Content = T("Remove from list", "Rimuovi dalla lista"), HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = _consolePresentation ? 20 : 14, MinHeight = _consolePresentation ? 56 : 32 };
            AutomationProperties.SetName(remove, T("Remove ", "Rimuovi ") + game.Name);
            AutomationProperties.SetAutomationId(remove, "GameLibrary_Remove_" + game.Id);
            remove.Click += async (_, _) => await RemoveAsync(game);
            card.Children.Add(remove); _games.Children.Add(card);
        }
    }
}
