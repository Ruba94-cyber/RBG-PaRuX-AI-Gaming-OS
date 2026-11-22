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
    private void BuildStoreDiscoveryOffer(StackPanel root)
    {
        _storeDiscoveryOfferText.Text = T(
            "Find installed Steam, Epic and GOG games with one local read-only scan. Nothing is installed, launched or uploaded. Or skip and scan later.",
            "Trova i giochi Steam, Epic e GOG installati con una scansione locale di sola lettura. Nessuna installazione, avvio o invio di dati. Oppure salta e rileva in seguito.");
        AutomationProperties.SetAutomationId(_storeDiscoveryOffer, "GameLibrary_FirstRunScan_Offer");
        AutomationProperties.SetAutomationId(_storeDiscoveryOfferText, "GameLibrary_FirstRunScan_Description");
        AutomationProperties.SetLiveSetting(_storeDiscoveryOfferText, AutomationLiveSetting.Polite);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(_storeDiscoveryOfferText);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _storeDiscoveryOfferScan.Content = T("Find my installed games", "Trova i giochi installati");
        AutomationProperties.SetAutomationId(_storeDiscoveryOfferScan, "GameLibrary_FirstRunScan_Start");
        _storeDiscoveryOfferScan.Click += async (_, _) => await RefreshAllStoresAsync();
        _storeDiscoveryOfferDismiss.Content = T("Not now", "Non ora");
        AutomationProperties.SetAutomationId(_storeDiscoveryOfferDismiss, "GameLibrary_FirstRunScan_Dismiss");
        _storeDiscoveryOfferDismiss.Click += (_, _) => DismissStoreDiscoveryOffer();
        actions.Children.Add(_storeDiscoveryOfferScan);
        actions.Children.Add(_storeDiscoveryOfferDismiss);
        content.Children.Add(actions);
        _storeDiscoveryOffer.Child = content;
        root.Children.Add(_storeDiscoveryOffer);
        UpdateStoreDiscoveryOffer();
    }

    private void UpdateStoreDiscoveryOffer()
    {
        _storeDiscoveryOffer.Visibility =
            !SettingsStore.Current.Data.GameStoreDiscoveryPromptDismissed && !CorporateMode.IsActive
                && !GamingManager.Current.IsActive && !_lastConfirmedGamingActive
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DismissStoreDiscoveryOffer()
    {
        SettingsStore.Current.Data.GameStoreDiscoveryPromptDismissed = true;
        SettingsStore.Current.Save();
        UpdateStoreDiscoveryOffer();
    }

    private Task RefreshSteamAsync() => RefreshDetectedAsync(
        "Steam", () => GamingOrchestratorClient.Current.DiscoverSteamGamesAsync());

    private Task RefreshEpicAsync() => RefreshDetectedAsync(
        "Epic", () => GamingOrchestratorClient.Current.DiscoverEpicGamesAsync());

    private Task RefreshGogAsync() => RefreshDetectedAsync(
        "GOG", () => GamingOrchestratorClient.Current.DiscoverGogGamesAsync());

    private bool CanDiscoverStores()
    {
        if (!GamingManager.Current.IsActive && !_lastConfirmedGamingActive) return true;
        _discoveryStatus.Text = T(
            "Store detection is paused during Gaming to protect game performance. Return to AI and try again.",
            "Rilevamento store sospeso durante il Gaming per proteggere le prestazioni. Torna all'AI e riprova.");
        return false;
    }

    private static async Task<GamingCallResult<List<DiscoveredGameDto>>> TryDiscoverStoreAsync(
        string provider, Func<Task<GamingCallResult<List<DiscoveredGameDto>>>> discover)
    {
        try { return await discover(); }
        catch (Exception ex)
        {
            // One failed provider must not discard titles returned by other providers.
            Log.Write("Gaming store discovery " + provider + ": " + ex.GetType().Name);
            return GamingCallResult<List<DiscoveredGameDto>>.Fail(provider + " discovery unavailable.");
        }
    }

    private async Task RefreshAllStoresAsync()
    {
        if (_busy || !CanDiscoverStores()) return;
        SetBusy(true);
        _lastDetectedGames.Clear();
        _detectedPage = 0;
        RenderDiscoveredGames();
        _discoveryStatus.Text = T("Detecting Steam, Epic and GOG installations…",
                                  "Rilevamento installazioni Steam, Epic e GOG…");
        try
        {
            // Each store remains independent. A broken provider cannot erase a valid
            // catalog result from another store. No background scan is started at boot.
            var steam = TryDiscoverStoreAsync("Steam", () => GamingOrchestratorClient.Current.DiscoverSteamGamesAsync());
            var epic = TryDiscoverStoreAsync("Epic", () => GamingOrchestratorClient.Current.DiscoverEpicGamesAsync());
            var gog = TryDiscoverStoreAsync("GOG", () => GamingOrchestratorClient.Current.DiscoverGogGamesAsync());
            await Task.WhenAll(steam, epic, gog);

            var results = new[]
            {
                (Name: "Steam", Result: await steam),
                (Name: "Epic", Result: await epic),
                (Name: "GOG", Result: await gog)
            };
            string[] failed = results.Where(x => !x.Result.Succeeded || x.Result.Value == null)
                .Select(x => x.Name).ToArray();
            _lastDetectedGames = results
                .Where(x => x.Result.Succeeded && x.Result.Value != null)
                .SelectMany(x => x.Result.Value!)
                .GroupBy(g => g.ProviderId + ":" + g.ProviderGameId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _discoveryStatus.Text = T(
                $"Found {_lastDetectedGames.Count} installed games across stores. " +
                (failed.Length > 0 ? "Unavailable: " + string.Join(", ", failed) : "All providers responded."),
                $"Rilevati {_lastDetectedGames.Count} giochi installati tra gli store. " +
                (failed.Length > 0 ? "Non disponibili: " + string.Join(", ", failed) : "Tutti i provider hanno risposto."));
            RenderDiscoveredGames();
            // Dismiss only after at least one real provider responded. A full outage
            // keeps the banner available to retry; never claim success on a failure.
            if (results.Any(x => x.Result.Succeeded && x.Result.Value != null))
                DismissStoreDiscoveryOffer();
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T("Store discovery failed. Nothing was changed.",
                "Rilevamento degli store non riuscito. Nessun dato è stato modificato.");
        }
        finally { SetBusy(false); }
    }

    private async Task RefreshDetectedAsync(
        string provider,
        Func<Task<GamingCallResult<List<DiscoveredGameDto>>>> fetch)
    {
        if (_busy || !CanDiscoverStores()) return;
        SetBusy(true);
        _lastDetectedGames.Clear();
        _discoveredGames.Children.Clear();
        _discoveryStatus.Text = T($"Detecting installed {provider} games…", $"Rilevamento giochi {provider} installati…");
        try
        {
            GamingCallResult<List<DiscoveredGameDto>> result = await fetch();
            if (!result.Succeeded || result.Value is null)
            {
                _discoveryStatus.Text = T($"{provider} discovery unavailable. No files were changed.", $"Rilevamento {provider} non disponibile. Nessun file è stato modificato.");
                return;
            }
            _lastDetectedGames = result.Value.ToList();
            _detectedPage = 0;
            _discoveryStatus.Text = result.Value.Count == 0
                ? T($"No installed {provider} games found.", $"Nessun gioco {provider} installato rilevato.")
                : T($"Found {result.Value.Count} installed {provider} games. Use pagination to view them all.",
                    $"Rilevati {result.Value.Count} giochi {provider} installati. Usa le pagine per consultarli tutti.");
            RenderDiscoveredGames();
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T($"{provider} discovery failed. No files were changed.", $"Rilevamento {provider} non riuscito. Nessun file è stato modificato.");
        }
        finally { SetBusy(false); }
    }

    private static string InstallStateLabel(string state) => state switch
    {
        GameInstallStates.Installed => T("installed", "installato"),
        GameInstallStates.Updating => T("updating", "in aggiornamento"),
        GameInstallStates.Incomplete => T("incomplete", "incompleto"),
        _ => T("state not reported", "stato non riportato")
    };
}
