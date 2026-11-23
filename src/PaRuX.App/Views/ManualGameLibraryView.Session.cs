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
    /// <summary>
    /// Starts the game a desktop or Steam shortcut named, through the normal Play path and its confirmation.
    /// The workspace must already be shown. A reason is put in the status line when nothing started.
    /// </summary>
    public async Task PlayRequestedByShortcutAsync(string titleKey)
    {
        if (_universal is null) return;
        try
        {
            string? problem = await _universal.PlayByKeyAsync(titleKey);
            if (problem is not null) _status.Text = problem;
        }
        catch (Exception ex)
        {
            Log.Write("PlayRequestedByShortcutAsync failed: " + ex.Message);
            _status.Text = T("The shortcut could not start the game: ", "Il collegamento non ha potuto avviare il gioco: ") + ex.Message;
        }
    }

    public async void OnShown()
    {
        ApplySetupProfilePresentation();
        UpdateStoreDiscoveryOffer();
        if ((_sectionBar?.SelectedItem as Microsoft.UI.Xaml.Controls.SelectorBarItem)?.Tag as string == "Tools")
        {
            // Desktop controller entry targets Playnite tools, not the obsolete
            // native tile catalogue. Skip game/store scans and list hydration
            // unless the owner explicitly returns to the Library section.
            await UpdateGamingStatusAsync();
            return;
        }
        if (_universal is not null)
            await _universal.RefreshVisibleTitlesAsync();
        await RefreshAsync();
        await UpdateGamingStatusAsync();
        UpdateStoreDiscoveryOffer();
        // The initial catalog load briefly disables buttons; restore a useful
        // gamepad focus target once the real server status has been fetched.
        if (_consolePresentation && !_busy)
            FocusConsoleHome();
    }

    /// <summary>Returns null when status cannot be confirmed. A failed read never
    /// counts as a game exit or an unlocked AI runtime.</summary>
    private async Task<GamingStatusDto?> UpdateGamingStatusAsync()
    {
        GamingStatusDto? observed = null;
        try
        {
            GamingStatusDto? status = await GamingOrchestratorClient.Current.GetStatusAsync();
            observed = status;
            if (status is null)
            {
                _returnToAiAvailable = false;
                _installerInspectionAllowed = false;
                _gamingStatus.Text = T("Gaming status unavailable. Check the local Core connection.",
                    "Stato Gaming non disponibile. Controlla il collegamento al Core locale.");
            }
            else
            {
                _returnToAiAvailable = status.IsGamingActive || status.State != "AiReady";
                _lastConfirmedGamingActive = _returnToAiAvailable;
                _installerInspectionAllowed = !_returnToAiAvailable;
                _gamingStatus.Text = status.State == "GamingLocal" && status.IsGamingActive &&
                    status.ManualReturnRequiredReason is { Length: > 0 } manualReason
                    ? manualReason switch
                    {
                        "bootstrap-exited-early" => T(
                            "The launched process ended too quickly to confirm the game ended. Gaming remains active; return to AI manually when finished.",
                            "Il processo avviato si è chiuso troppo presto per confermare la fine del gioco. Gaming rimane attivo; torna all'AI manualmente quando hai finito."),
                        "child-still-active" => T(
                            "A child process is still running after the game launcher closed. Gaming remains active; use Return to AI after playing.",
                            "Dopo la chiusura del launcher è ancora attivo un processo figlio. Gaming rimane attivo; usa Torna all'AI dopo aver giocato."),
                        _ => T(
                            "PaRuX could not verify that every game process exited. Gaming stays active; use Return to AI manually.",
                            "PaRuX non può confermare che tutti i processi del gioco siano terminati. Gaming rimane attivo; usa Torna all'AI manualmente.")
                    }
                    : status.State == "AiReady" && status.IsGamingActive
                    ? T("Gaming resource lock is still active after Core restart. Select Return to AI to recover without closing the game.",
                        "Il blocco delle risorse Gaming è ancora attivo dopo il riavvio del Core. Seleziona Torna all'AI per recuperare senza chiudere il gioco.")
                    : status.State == "AiReady"
                        ? T("AI ready · No Gaming session active.", "AI pronta · Nessuna sessione Gaming attiva.")
                        : T($"Gaming state: {GamingStateLabel(status.State)}. Library search uses the last cached catalogue; Return to AI leaves the game running.",
                            $"Stato Gaming: {GamingStateLabel(status.State)}. La ricerca usa il catalogo in cache; tornando all'AI il gioco rimane aperto.");
            }
        }
        catch (Exception)
        {
            _returnToAiAvailable = false;
            _installerInspectionAllowed = false;
            _gamingStatus.Text = T("Gaming status unavailable.", "Stato Gaming non disponibile.");
        }
        _returnToAi.IsEnabled = _returnToAiAvailable && !_busy;
        SyncGamingStrip();
        _inspectInstaller.IsEnabled = _installerInspectionAllowed && !_busy;
        _refreshStaged.IsEnabled = _installerInspectionAllowed && !_busy;
        UpdatePackageStageAvailability();
        return observed;
    }

    /// <summary>Refresh only when the user brings the Console window back into
    /// the foreground. Do not steal focus from a running emulator or poll in the
    /// background. An unconfirmed Core response never implies a game ended.</summary>
    public async Task OnConsoleForegroundActivatedAsync()
    {
        if (!_consolePresentation || _busy || _consoleForegroundRefreshInFlight) return;
        _consoleForegroundRefreshInFlight = true;
        try
        {
            bool previouslyGaming = _lastConfirmedGamingActive;
            GamingStatusDto? state = await UpdateGamingStatusAsync();
            if (GameLibraryForegroundReturnPolicy.CanRefreshTitles(previouslyGaming, state))
            {
                if (_universal is not null)
                    await _universal.RefreshTitlesAfterGameAsync();
                _gamingStatus.Text = GameLibraryForegroundReturnPolicy.HasConfirmedOwnedExit(state)
                    ? T("The verified game process exited. PaRuX returned resources to AI; Console Home is ready.",
                        "Il processo del gioco verificato è terminato. PaRuX ha restituito le risorse all'AI; la Home Console è pronta.")
                    : T("AI resources available again. PaRuX did not terminate the game or launcher.",
                        "Risorse AI nuovamente disponibili. PaRuX non ha terminato il gioco o il launcher.");
            }
        }
        catch (Exception)
        {
            _gamingStatus.Text = T("Could not refresh Gaming status after returning to PaRuX.",
                "Impossibile aggiornare lo stato Gaming al ritorno in PaRuX.");
        }
        finally { _consoleForegroundRefreshInFlight = false; }
    }

    private async Task RefreshGamingStatusButtonAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try { await UpdateGamingStatusAsync(); }
        finally { SetBusy(false); }
    }

    private async Task ReturnToAiAsync()
    {
        if (GamingOnlySetup || _busy || !_returnToAiAvailable) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Return to AI?", "Tornare all'AI?"),
                Content = T(
                    "PaRuX will try to restore AI resources but will not terminate, suspend or close your game or Steam. If the game is still running, it may compete with AI for GPU resources.",
                    "PaRuX tenterà di ripristinare le risorse AI senza terminare, sospendere o chiudere il gioco o Steam. Se il gioco è ancora aperto, potrebbe competere con l'AI per le risorse GPU."),
                PrimaryButtonText = T("Return to AI", "Torna all'AI"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            GamingCallResult<GamingStatusDto> result = await GamingOrchestratorClient.Current.ExitGamingAsync();
            GamingStatusDto? state = await UpdateGamingStatusAsync();
            // HTTP success alone is not a confirmed state transition. In particular,
            // a failed status read must never display "AI ready" after a gaming exit.
            if (state is { IsGamingActive: false } && !result.Succeeded)
                // Gaming Mode is over, but nothing could be restored (typically: no chat engine/model installed yet).
                _gamingStatus.Text = T("Gaming Mode ended. No AI model could be restored (none is ready yet): install or start one in Model Hub.",
                    "Modalità Gaming terminata. Nessun modello IA da ripristinare (nessuno è pronto): installalo o avvialo da Model Hub.");
            else if (!result.Succeeded || !GameLibraryForegroundReturnPolicy.CanRefreshTitles(true, state))
                _gamingStatus.Text = T("Return to AI could not be confirmed. Inspect Gaming status and retry.",
                    "Ritorno all'AI non confermato. Controlla lo stato Gaming e riprova.");
            else
            {
                if (_universal is not null)
                    await _universal.RefreshTitlesAfterGameAsync();
                _gamingStatus.Text = T("AI resources ready. PaRuX did not close the game.",
                    "Risorse AI pronte. PaRuX non ha chiuso il gioco.");
            }
        }
        catch (Exception)
        {
            _gamingStatus.Text = T("Return to AI failed. The game was not closed.",
                "Ritorno all'AI non riuscito. Il gioco non è stato chiuso.");
        }
        finally { SetBusy(false); }
    }
}
