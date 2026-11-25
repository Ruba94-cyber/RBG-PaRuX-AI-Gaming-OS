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
    /// <summary>A store client was asked to start a game: switch an owner-armed virtual pad to that game's controller settings.</summary>
    /// <summary>Same rule as the Core's title catalogue (GameTitleCatalog.KeyFor), which the per-game controller settings are keyed by.</summary>
    private static string TitleKeyFor(string? name, string installationId)
    {
        string key = GameLibraryQuery.NormalizeName(name);
        return key.Length >= 2 ? key : "id:" + installationId;
    }

    private static void NotifyStoreGameStarted(DiscoveredGameDto game)
    {
        string installationId = game.ProviderId.ToLowerInvariant() + ":" + game.ProviderGameId;
        _ = VirtualPadActivationController.Current.NotifyGameStartedAsync(installationId,
            TitleKeyFor(game.Name, installationId));
    }

    private async Task LaunchSteamAsync(DiscoveredGameDto game)
    {
        if (_busy || !string.Equals(game.ProviderId, "steam", StringComparison.OrdinalIgnoreCase)) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Launch using Steam?", "Avviare tramite Steam?"),
                Content = T(
                    "PaRuX will prepare Gaming Mode, then ask the installed official Steam client to launch this game. Steam owns the actual game process, so automatic Return to AI is not available. Return manually when finished.",
                    "PaRuX preparerà la modalità Gaming e chiederà al client Steam ufficiale installato di avviare il gioco. Steam gestisce il processo effettivo: il ritorno automatico all'AI non è disponibile. Torna manualmente al termine."),
                PrimaryButtonText = T("Request Steam launch", "Richiedi avvio a Steam"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.RequestSteamGameLaunchAsync(game.ProviderGameId);
            await UpdateGamingStatusAsync();
            if (result.Succeeded && result.Value is { Launched: true }) NotifyStoreGameStarted(game);
            _discoveryStatus.Text = result.Succeeded && result.Value is { Launched: true }
                ? T("Launch request sent to Steam. Return to AI manually after playing.",
                    "Richiesta di avvio inviata a Steam. Torna manualmente all'AI dopo aver giocato.")
                : T("Steam launch was not accepted. Check the installed client and Gaming state.",
                    "Avvio tramite Steam non accettato. Controlla il client installato e lo stato Gaming.");
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T("Could not request Steam launch.", "Impossibile richiedere l'avvio a Steam.");
        }
        finally { SetBusy(false); }
    }

    private async Task LaunchEpicAsync(DiscoveredGameDto game)
    {
        if (_busy || !string.Equals(game.ProviderId, "epic", StringComparison.OrdinalIgnoreCase)) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Launch using Epic Games Launcher?", "Avviare tramite Epic Games Launcher?"),
                Content = T(
                    "PaRuX will prepare Gaming Mode and ask Windows to open Epic's registered game-launch protocol. The Windows protocol handler is user-configurable; verify that it opens the real Epic client. PaRuX cannot verify the game process or account ownership. Return to AI manually when finished.",
                    "PaRuX preparerà la modalità Gaming e chiederà a Windows di aprire il protocollo di avvio registrato da Epic. Il gestore di protocollo Windows è configurabile dall'utente: verifica che apra il vero client Epic. PaRuX non può verificare il processo del gioco né la proprietà sull'account. Torna manualmente all'AI al termine."),
                PrimaryButtonText = T("Request Epic launch", "Richiedi avvio a Epic"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.RequestEpicGameLaunchAsync(game.ProviderGameId);
            await UpdateGamingStatusAsync();
            if (result.Succeeded && result.Value is { Launched: true }) NotifyStoreGameStarted(game);
            _discoveryStatus.Text = result.Succeeded && result.Value is { Launched: true }
                ? T("Epic protocol request dispatched. Game start is not verified; return to AI manually.",
                    "Richiesta al protocollo Epic inviata. L'avvio del gioco non è verificato: torna manualmente all'AI.")
                : T("Epic request was not dispatched. Check the installed client, Windows protocol registration and Gaming state.",
                    "Richiesta Epic non inviata. Verifica client installato, registrazione del protocollo Windows e stato Gaming.");
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T("Could not request Epic launch.", "Impossibile richiedere l'avvio tramite Epic.");
        }
        finally { SetBusy(false); }
    }

    /// <summary>EA app, Battle.net, Ubisoft Connect and Xbox: confirmed handoff through the client's own URI protocol.</summary>
    private async Task LaunchExtraStoreAsync(DiscoveredGameDto game)
    {
        if (_busy || !GameStoreLaunchIdPolicy.SupportsOfficialLaunch(game.ProviderId, game.ProviderGameId)) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Launch using the store client?", "Avviare tramite il client dello store?"),
                Content = T(
                    "PaRuX will prepare Gaming Mode and ask Windows to open the store client's own registered launch protocol for this installed game. PaRuX does not sign in, read credentials or verify ownership; it only observes the game to count play time. Return to AI manually when finished.",
                    "PaRuX preparerà la modalità Gaming e chiederà a Windows di aprire il protocollo di avvio registrato dal client dello store per questo gioco installato. PaRuX non accede, non legge credenziali e non verifica la proprietà; osserva il gioco solo per contare il tempo di gioco. Torna manualmente all'AI al termine."),
                PrimaryButtonText = T("Request launch", "Richiedi avvio"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.RequestExtraStoreGameLaunchAsync(game.ProviderId, game.ProviderGameId);
            await UpdateGamingStatusAsync();
            if (result.Succeeded && result.Value is { Launched: true }) NotifyStoreGameStarted(game);
            _discoveryStatus.Text = result.Succeeded && result.Value is { Launched: true }
                ? T("Launch request sent through the store client. Return to AI manually after playing.",
                    "Richiesta di avvio inviata tramite il client dello store. Torna manualmente all'AI dopo aver giocato.")
                : T("Launch was not accepted. Check the installed client, its Windows protocol registration and Gaming state.",
                    "Avvio non accettato. Controlla il client installato, la registrazione del protocollo Windows e lo stato Gaming.");
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T("Could not request the launch.", "Impossibile richiedere l'avvio.");
        }
        finally { SetBusy(false); }
    }

    private async Task LaunchGogAsync(DiscoveredGameDto game)
    {
        if (_busy || !string.Equals(game.ProviderId, "gog", StringComparison.OrdinalIgnoreCase)) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Launch using GOG GALAXY?", "Avviare tramite GOG GALAXY?"),
                Content = T(
                    "PaRuX will prepare Gaming Mode, then ask the installed GOG GALAXY client to launch the selected game. GALAXY owns the actual game process. Automatic Return to AI is not available; return manually when finished.",
                    "PaRuX preparerà la modalità Gaming e chiederà al client GOG GALAXY installato di avviare il gioco selezionato. GALAXY gestisce il processo effettivo. Il ritorno automatico all'AI non è disponibile: torna manualmente al termine."),
                PrimaryButtonText = T("Request GOG launch", "Richiedi avvio a GOG"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.RequestGogGameLaunchAsync(game.ProviderGameId);
            await UpdateGamingStatusAsync();
            if (result.Succeeded && result.Value is { Launched: true }) NotifyStoreGameStarted(game);
            _discoveryStatus.Text = result.Succeeded && result.Value is { Launched: true }
                ? T("Launch request sent to GOG GALAXY. Return to AI manually after playing.",
                    "Richiesta di avvio inviata a GOG GALAXY. Torna manualmente all'AI dopo aver giocato.")
                : T("GOG launch was not accepted. Check the installed client and Gaming state.",
                    "Avvio tramite GOG non accettato. Controlla il client installato e lo stato Gaming.");
        }
        catch (Exception)
        {
            _discoveryStatus.Text = T("Could not request GOG launch.", "Impossibile richiedere l'avvio tramite GOG.");
        }
        finally { SetBusy(false); }
    }

    private async Task LaunchAsync(ManualGameDto game)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            bool isApplication = string.Equals(game.Kind, ManualEntryKinds.Application, StringComparison.OrdinalIgnoreCase);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = isApplication ? T("Start selected application?", "Avviare l'applicazione selezionata?")
                                      : T("Launch selected game?", "Avviare il gioco selezionato?"),
                Content = isApplication
                    ? T("PaRuX will start only this saved program with its saved arguments. It does not change AI or Gaming state and does not monitor or close the program.",
                        "PaRuX avvierà solo questo programma salvato con i suoi argomenti. Non modifica lo stato AI o Gaming e non monitora né chiude il programma.")
                    : T(
                    "PaRuX will pause local AI resources and start only this saved executable. It will not install software, suspend the game, or forcibly close it. Automatic return is enabled only if Windows confirms the owned game process.",
                    "PaRuX sospenderà le risorse AI locali e avvierà solo questo eseguibile salvato. Non installerà software, non sospenderà il gioco né lo chiuderà forzatamente. Il ritorno automatico è disponibile solo quando Windows conferma il processo del gioco avviato."),
                PrimaryButtonText = T("Launch", "Avvia"),
                CloseButtonText = T("Cancel", "Annulla"),
                // Desktop keeps Cancel as the default (an accidental Enter never starts a game). In Console/TV mode the owner has just
                // pressed Play with a controller and A must confirm: the default is Launch there (B still cancels).
                DefaultButton = _consolePresentation ? ContentDialogButton.Primary : ContentDialogButton.Close
            };
            // Make the optional TV window routing visible BEFORE permission
            // to launch. A chosen TV does not promise fullscreen routing.
            var displaySettings = SettingsStore.Current.Data;
            // Routing is opt-in through choosing a TV; with none chosen the game launches normally.
            bool routeToTv = !isApplication && displaySettings.RouteVerifiedWindowedGamesToTv &&
                !string.IsNullOrWhiteSpace(displaySettings.ConsoleTargetDisplayId);
            string? gameDisplayId = null;
            if (routeToTv)
            {
                var snapshot = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
                if (!snapshot.Succeeded ||
                    !GameTvLaunchAdmissionPolicy.TryResolveTarget(true, snapshot.Value,
                        displaySettings.ConsoleDisplays, displaySettings.ConsoleTargetDisplayId,
                        out gameDisplayId))
                {
                    _status.Text = T(
                        "TV routing is enabled but no connected, user-classified TV is selected. No game started. Check Gaming display settings.",
                        "Instradamento TV attivo, ma nessuna TV collegata e classificata è selezionata. Nessun gioco avviato. Controlla le impostazioni Gaming.");
                    return;
                }
            }
            if (!string.IsNullOrWhiteSpace(gameDisplayId))
                dialog.Content = (dialog.Content?.ToString() ?? string.Empty) + "\n\n" +
                    T("PaRuX will attempt to place one verified windowed game window on your selected TV. Exclusive fullscreen, child processes and external store launchers are not moved.",
                      "PaRuX proverà a posizionare una finestra verificata del gioco sulla TV selezionata. Fullscreen esclusivo, processi secondari e launcher esterni non vengono spostati.");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (routeToTv)
            {
                // Roles/topology may change while the confirmation is shown.
                // Never silently launch onto a different monitor.
                var latest = SettingsStore.Current.Data;
                var snapshot = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
                if (!latest.RouteVerifiedWindowedGamesToTv ||
                    !string.Equals(latest.ConsoleTargetDisplayId, gameDisplayId,
                        StringComparison.OrdinalIgnoreCase) ||
                    !snapshot.Succeeded ||
                    !GameTvLaunchAdmissionPolicy.TryResolveTarget(true, snapshot.Value,
                        latest.ConsoleDisplays, gameDisplayId, out _))
                {
                    _status.Text = T(
                        "The selected TV changed or disconnected before launch. No game was started; select it again in Gaming settings.",
                        "La TV selezionata è cambiata o scollegata prima dell'avvio. Nessun gioco avviato; riselezionala nelle impostazioni Gaming.");
                    return;
                }
            }

            var result = await GamingOrchestratorClient.Current.LaunchManualGameAsync(
                game.Id, displayId: gameDisplayId);
            if (!result.Succeeded || result.Value is not { Launched: true } launch)
            {
                _status.Text = !string.IsNullOrWhiteSpace(gameDisplayId) &&
                    (result.Error?.Contains("selected game TV is unavailable",
                        StringComparison.OrdinalIgnoreCase) == true)
                    ? T("The selected TV is unavailable or its Windows display ID changed. The game was not started. Open Gaming settings and select the TV again.",
                        "La TV selezionata non è disponibile o il suo identificativo Windows è cambiato. Il gioco non è stato avviato. Apri le impostazioni Gaming e seleziona nuovamente la TV.")
                    : T("Could not launch the game. Check Gaming state and the executable.",
                        "Impossibile avviare il gioco. Controlla lo stato Gaming e l'eseguibile.");
                return;
            }
            await UpdateGamingStatusAsync();
            // Switches an owner-armed virtual pad to this game's profile; never starts forwarding by itself.
            string manualSource = string.IsNullOrEmpty(game.EmulatorId) ? GameInstallationSources.Manual : GameInstallationSources.Emulator;
            _ = VirtualPadActivationController.Current.NotifyGameStartedAsync(game.Id,
                TitleKeyFor(game.Name, manualSource + ":" + game.Id));
            if (isApplication)
            {
                _status.Text = T("Application started. PaRuX does not monitor it and AI state is unchanged.",
                    "Applicazione avviata. PaRuX non la monitora e lo stato AI resta invariato.");
                return;
            }
            _status.Text = launch.StartState == GameStartStates.Verified
                ? T("Game process verified. Automatic return to AI is monitoring it.",
                    "Processo del gioco verificato. Il ritorno automatico all'AI lo sta monitorando.")
                : T("Game process started but its identity is not verified. Return to AI manually when finished.",
                    "Processo del gioco avviato ma identità non verificata. Torna manualmente all'AI al termine.");

            // Report what Windows ACTUALLY allowed, not a generic promise that
            // every emulator/exclusive fullscreen frame moved successfully.
            if (!string.IsNullOrEmpty(gameDisplayId))
                _status.Text += "\n" + (launch.WindowPlacement switch
                {
                    GameWindowPlacementStates.Placed => T("Window moved onto the selected TV without changing focus or display settings.",
                        "Finestra spostata sulla TV scelta senza cambiare focus o impostazioni degli schermi."),
                    GameWindowPlacementStates.AlreadyOnTv => T("The game window was already positioned on the TV.",
                        "La finestra del gioco era già posizionata sulla TV."),
                    GameWindowPlacementStates.UnsupportedWindow => T(
                        "TV window placement not confirmed (fullscreen, multiple windows or incompatible window). The game is still running; position it in its own settings.",
                        "Spostamento sulla TV non confermato (fullscreen, più finestre o finestra incompatibile). Il gioco resta avviato; imposta lo schermo nelle sue opzioni."),
                    _ => T("TV placement could not be confirmed. The game remains running; use its own display settings.",
                        "Posizionamento sulla TV non confermato. Il gioco resta avviato; usa le sue impostazioni schermo.")
                });
            if (!string.IsNullOrEmpty(launch.HdrReadiness))
                _status.Text += "\n" + (launch.HdrReadiness switch
                {
                    "hdr-ready" => T(
                        "Windows HDR was verified active on the selected display before launch; game HDR rendering is not guaranteed.",
                        "HDR Windows verificato attivo sullo schermo scelto prima dell'avvio; il rendering HDR del gioco non è garantito."),
                    "hdr-preference-not-ready" => T(
                        "HDR was preferred but not confirmed. The game started using unchanged Windows color settings.",
                        "HDR preferito ma non confermato. Il gioco è stato avviato senza modificare le impostazioni colore di Windows."),
                    _ => T("HDR readiness not confirmed.", "Verifica HDR non confermata.")
                });
            if (launch.EmulatorDisplay == GameEmulatorDisplayStates.NativeFullscreenArgument)
                _status.Text += "\n" + T(
                    "The emulator received its native fullscreen launch option. The actual display mode is not confirmed; an extra Alt+Enter toggle was avoided.",
                    "L'emulatore ha ricevuto la propria opzione di avvio a schermo intero. Il risultato non è confermato; è stato evitato un secondo Alt+Invio.");
            if (!string.IsNullOrEmpty(launch.PostLaunch))
                _status.Text += "\n" + (launch.PostLaunch switch
                {
                    GamePostLaunchStates.ForegroundConfirmed => T(
                        "The owned game window was confirmed in the foreground.",
                        "La finestra verificata del gioco è stata portata in primo piano."),
                    GamePostLaunchStates.FullscreenRequested => T(
                        "Alt+Enter was posted to the verified game window; fullscreen is not guaranteed.",
                        "Alt+Invio è stato inviato alla finestra verificata del gioco; lo schermo intero non è garantito."),
                    GamePostLaunchStates.ClickPosted => T(
                        "A center click was posted to the verified foreground game window; the game's response is unknown.",
                        "Click centrale inviato alla finestra verificata in primo piano; la risposta del gioco non è verificabile."),
                    GamePostLaunchStates.FullscreenAndClickPosted => T(
                        "Alt+Enter and a center click were posted to the verified game window; the game may ignore them.",
                        "Alt+Invio e click centrale inviati alla finestra verificata; il gioco potrebbe ignorarli."),
                    GamePostLaunchStates.ForegroundDenied => T(
                        "Windows did not grant game foreground focus; no keyboard or mouse messages were sent.",
                        "Windows non ha concesso il primo piano al gioco; nessun tasto o click è stato inviato."),
                    _ => T(
                        "The optional post-launch actions were not confirmed. The game is still running; use its own display or input settings.",
                        "Le azioni facoltative dopo l'avvio non sono state confermate. Il gioco è in esecuzione; usa le sue impostazioni di schermo o input.")
                });
        }
        catch (Exception)
        {
            _status.Text = T("Could not launch the game.", "Impossibile avviare il gioco.");
        }
        finally { SetBusy(false); }
    }

    private async Task RemoveAsync(ManualGameDto game)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = T("Remove game?", "Rimuovere il gioco?"), Content = T("This removes only the entry; game files stay on disk.", "Viene rimossa solo la voce; i file del gioco restano sul disco."), PrimaryButtonText = T("Remove", "Rimuovi"), CloseButtonText = T("Cancel", "Annulla"), DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.RemoveManualGameAsync(game.Id);
            if (!result.Succeeded) { _status.Text = T("Could not remove the entry.", "Impossibile rimuovere la voce."); return; }
            await LoadAsync();
            if (_universal is not null) await _universal.RefreshTitlesAfterGameAsync();
        }
        catch (Exception) { _status.Text = T("Could not remove the entry.", "Impossibile rimuovere la voce."); }
        finally { SetBusy(false); }
    }

    private Control? _focusBeforeBusy;

    /// <summary>Remembers the focused control when an interaction starts and puts focus back when it
    /// ends (game launch request, dialogs, rescans), so controller users never land on nothing.
    /// Rendering may replace the control meanwhile; then the Console home target is used.</summary>
    private void TrackFocusAcrossBusy(bool value)
    {
        if (value == _busy) return;
        if (value)
        {
            _focusBeforeBusy = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
            return;
        }
        // Focus restoration was only needed by the removed TV presentation; desktop keeps WinUI's own focus handling.
        _focusBeforeBusy = null;
    }

    private void SetBusy(bool value) { TrackFocusAcrossBusy(value); _busy = value; _add.IsEnabled = !value; _browse.IsEnabled = !value; _steamRefresh.IsEnabled = !value; _epicRefresh.IsEnabled = !value; _gogRefresh.IsEnabled = !value; _allStoresRefresh.IsEnabled = !value; _displayRefresh.IsEnabled = !value; _controllerRefresh.IsEnabled = !value; _inspectInstaller.IsEnabled = !value && _installerInspectionAllowed; _refreshStaged.IsEnabled = !value && _installerInspectionAllowed; UpdatePackageStageAvailability(); _cancelInspection.IsEnabled = value && _inspectionCancellation != null; _gamingStatusRefresh.IsEnabled = !value; _returnToAi.IsEnabled = !value && _returnToAiAvailable; SyncGamingStrip(); _games.IsHitTestVisible = !value; _discoveredGames.IsHitTestVisible = !value; _detectedPrevious.IsEnabled = !value && _lastDetectedWindow.HasPrevious; _detectedNext.IsEnabled = !value && _lastDetectedWindow.HasNext; _storeDiscoveryOfferScan.IsEnabled = !value; _storeDiscoveryOfferDismiss.IsEnabled = !value; }
}
