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
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            var file = await picker.PickSingleFileAsync();
            if (file != null) _path.Text = file.Path;
        }
        catch (Exception) { _status.Text = T("Could not open the file picker.", "Impossibile aprire la selezione file."); }
        finally { SetBusy(false); }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var result = await GamingOrchestratorClient.Current.AddManualGameAsync(new AddManualGameRequest(_name.Text, _path.Text, _arguments.Text, _workingDirectory.Text,
                (_kind.SelectedItem as ComboBoxItem)?.Tag as string ?? ManualEntryKinds.Game));
            if (!result.Succeeded) { _status.Text = T("Could not add the entry. Check the name, local .exe path, arguments, working folder, or whether it is already listed.", "Impossibile aggiungere la voce. Controlla nome, percorso .exe locale, argomenti, cartella di lavoro o eventuali duplicati."); return; }
            _name.Text = ""; _path.Text = ""; _arguments.Text = ""; _workingDirectory.Text = "";
            await LoadAsync();
            if (_universal is not null) await _universal.RefreshTitlesAfterGameAsync();
        }
        catch (Exception) { _status.Text = T("Could not add the game.", "Impossibile aggiungere il gioco."); }
        finally { SetBusy(false); }
    }

    private async Task RefreshStagedPackagesAsync()
    {
        if (_busy || !_installerInspectionAllowed || _consolePresentation) return;
        SetBusy(true);
        _stagedPackages.Children.Clear();
        _stagedStatus.Text = T("Reading managed package copies…",
            "Lettura delle copie gestite…");
        try
        {
            var result = await GamingOrchestratorClient.Current.ListStagedPackagesAsync();
            if (!result.Succeeded || result.Value is null)
            {
                _stagedStatus.Text = T("The managed package inventory is unavailable.",
                    "Elenco dei pacchetti gestiti non disponibile.");
                return;
            }
            _stagedStatus.Text = result.Value.Count == 0
                ? T("No staged packages.", "Nessun pacchetto preparato.")
                : T($"{result.Value.Count} managed local copies. Remove only those no longer needed.",
                    $"{result.Value.Count} copie locali gestite. Elimina solo quelle che non servono più.");
            foreach (StagedGamePackageDto file in result.Value)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                item.Children.Add(new TextBlock
                {
                    Text = $"{file.FileName} · {file.SizeBytes:N0} B",
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 450
                });
                var remove = new Button
                {
                    Content = T("Remove copy", "Elimina copia"),
                    Tag = file.Id,
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(remove, T(
                    $"Remove managed copy {file.FileName}", $"Elimina copia gestita {file.FileName}"));
                remove.Click += async (_, _) => await RemoveStagedPackageAsync(file);
                item.Children.Add(remove);
                _stagedPackages.Children.Add(item);
            }
        }
        catch (Exception)
        {
            _stagedStatus.Text = T("Could not list managed copies.",
                "Impossibile elencare le copie gestite.");
        }
        finally { SetBusy(false); }
    }

    private async Task RemoveStagedPackageAsync(StagedGamePackageDto file)
    {
        if (_busy || !_installerInspectionAllowed || _consolePresentation) return;
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("Remove managed copy?", "Eliminare la copia gestita?"),
            Content = T(
                $"Remove only the PaRuX-managed copy {file.FileName}? The original installer will not be touched.",
                $"Eliminare soltanto la copia gestita da PaRuX {file.FileName}? Il file originale non verrà modificato."),
            PrimaryButtonText = T("Remove copy", "Elimina copia"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        SetBusy(true);
        bool refresh = false;
        try
        {
            var result = await GamingOrchestratorClient.Current.DeleteStagedPackageAsync(file.Id);
            refresh = result.Succeeded;
            if (!refresh)
                _stagedStatus.Text = T("Could not remove this managed copy.",
                    "Impossibile eliminare questa copia gestita.");
        }
        catch (Exception)
        {
            _stagedStatus.Text = T("Removal failed. Original file preserved.",
                "Eliminazione non riuscita. File originale preservato.");
        }
        finally { SetBusy(false); }
        if (refresh) await RefreshStagedPackagesAsync();
    }

    private void UpdatePackageStageAvailability()
    {
        string digest = _expectedPublisherSha.Text?.Trim() ?? "";
        _stagePackage.IsEnabled = !_busy && !_consolePresentation && _installerInspectionAllowed &&
            !string.IsNullOrEmpty(_inspectedInstallerPath) &&
            digest.Length == 64 && digest.All(Uri.IsHexDigit);
    }

    private async Task StageInspectedPackageAsync()
    {
        if (_busy || !_installerInspectionAllowed || _consolePresentation ||
            string.IsNullOrEmpty(_inspectedInstallerPath)) return;
        string publisherHash = _expectedPublisherSha.Text?.Trim() ?? "";
        if (publisherHash.Length != 64 || !publisherHash.All(Uri.IsHexDigit)) return;

        SetBusy(true);
        CancellationTokenSource? staging = null;
        try
        {
            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = T("Verify and stage a copy?", "Verificare e preparare una copia?"),
                Content = T(
                    "Only proceed if you obtained this checksum independently from the trusted publisher. PaRuX will copy the local package into its private application folder after verifying every byte. It will NOT open, run, install or unpack it.",
                    "Procedi solo se hai ottenuto questo codice SHA-256 in modo indipendente da un produttore attendibile. PaRuX copierà il pacchetto nella propria cartella privata dopo averne verificato ogni byte. NON lo aprirà, eseguirà, installerà né estrarrà."),
                PrimaryButtonText = T("Verify and stage", "Verifica e prepara"),
                CloseButtonText = T("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            staging = new CancellationTokenSource();
            _inspectionCancellation = staging; // shared Gaming/leave-page cancellation policy
            _cancelInspection.IsEnabled = true;
            _installerStatus.Text = T("Verifying and staging a local package…",
                "Verifica e preparazione del pacchetto locale…");
            var result = await GamingOrchestratorClient.Current.StageGamePackageAsync(
                new GamePackageStageRequest(_inspectedInstallerPath, publisherHash), staging.Token);
            if (result.Succeeded && result.Value is not null)
                _installerStatus.Text = T(
                    $"Checked copy ready: {result.Value.FileName}\nSHA-256: {result.Value.Sha256}\nStored locally in PaRuX app data. No software was installed or executed.",
                    $"Copia verificata pronta: {result.Value.FileName}\nSHA-256: {result.Value.Sha256}\nSalvata nei dati locali di PaRuX. Nessun software è stato installato o eseguito.");
            else
                _installerStatus.Text = T(
                    "Verification or staging was refused. Check the publisher's independent SHA-256 and the Gaming status. No completed package was published.",
                    "Verifica o preparazione rifiutata. Controlla lo SHA-256 indipendente del produttore e lo stato Gaming. Nessun pacchetto completo è stato pubblicato.");
        }
        catch (OperationCanceledException) when (staging?.IsCancellationRequested == true)
        {
            _installerStatus.Text = T("Staging cancelled; no completed package was published.",
                "Preparazione annullata; nessun pacchetto completo è stato pubblicato.");
        }
        catch (Exception)
        {
            _installerStatus.Text = T("Package staging could not be completed.",
                "Impossibile completare la preparazione del pacchetto.");
        }
        finally
        {
            _inspectionCancellation = null;
            staging?.Dispose();
            _cancelInspection.IsEnabled = false;
            SetBusy(false);
        }
    }

    private async Task InspectInstallerAsync()
    {
        if (_busy || !_installerInspectionAllowed) return;
        _inspectedInstallerPath = null;
        _expectedPublisherSha.Text = "";
        SetBusy(true);
        CancellationTokenSource? inspection = null;
        try
        {
            var picker = new FileOpenPicker();
            foreach (string ext in new[] { ".exe", ".msi", ".msix", ".zip", ".7z", ".rar", ".iso" })
                picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            var chosen = await picker.PickSingleFileAsync();
            if (chosen is null) return;

            inspection = new CancellationTokenSource();
            _inspectionCancellation = inspection;
            _cancelInspection.IsEnabled = true;
            _installerStatus.Text = T("Computing SHA-256; no file is executed…",
                "Calcolo SHA-256; nessun file viene eseguito…");
            var result = await GamingOrchestratorClient.Current.InspectGameInstallerAsync(
                new GameInstallerInspectionRequest(chosen.Path), inspection.Token);
            if (!result.Succeeded || result.Value is null)
            {
                _installerStatus.Text = T(
                    "Inspection refused or failed. Verify that Gaming is off and the selected file is complete, local and supported.",
                    "Controllo rifiutato o non riuscito. Verifica che il Gaming sia disattivato e che il file sia completo, locale e supportato.");
                return;
            }

            _inspectedInstallerPath = chosen.Path;
            _installerStatus.Text = T(
                $"{result.Value.FileName} · {result.Value.SizeBytes:N0} bytes\nSHA-256: {result.Value.Sha256}\nA matching hash is not a malware or licensing guarantee; check it against the publisher.",
                $"{result.Value.FileName} · {result.Value.SizeBytes:N0} byte\nSHA-256: {result.Value.Sha256}\nL'hash non garantisce sicurezza o licenza: confrontalo con quello del produttore.");
        }
        catch (OperationCanceledException) when (inspection?.IsCancellationRequested == true)
        {
            _installerStatus.Text = T("Inspection cancelled. No file was modified.",
                "Controllo annullato. Nessun file è stato modificato.");
        }
        catch (Exception)
        {
            _installerStatus.Text = T("Local inspection could not be completed.",
                "Impossibile completare il controllo locale.");
        }
        finally
        {
            _inspectionCancellation = null;
            inspection?.Dispose();
            _cancelInspection.IsEnabled = false;
            SetBusy(false);
        }
    }

    private async Task InspectControllersAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _controllerStatus.Text = T("Checking local XInput controllers…", "Verifica dei controller XInput locali…");
        try
        {
            var result = await GamingOrchestratorClient.Current.GetControllersAsync();
            if (!result.Succeeded || result.Value is null)
            {
                _controllerStatus.Text = T("Controller diagnostics are unavailable.", "Diagnostica controller non disponibile.");
                return;
            }
            if (result.Value.Count == 0)
            {
                _controllerStatus.Text = T("No XInput controllers detected. Other input types have not been checked.",
                    "Nessun controller XInput rilevato. Gli altri tipi di input non sono stati verificati.");
                return;
            }
            _controllerStatus.Text = string.Join("   ", result.Value.Select(g =>
                T($"XInput controller {g.Slot + 1}", $"Controller XInput {g.Slot + 1}")));
        }
        catch (Exception)
        {
            _controllerStatus.Text = T("Controller inspection failed.", "Verifica controller non riuscita.");
        }
        finally { SetBusy(false); }
    }

    private async Task InspectDisplaysAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _displayItems.Children.Clear();
        _displayStatus.Text = T("Reading Windows display configuration…", "Lettura configurazione schermi Windows…");
        try
        {
            var result = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
            if (!result.Succeeded || result.Value is not { Supported: true } snapshot)
            {
                _displayStatus.Text = T("Windows display information is unavailable.",
                    "Informazioni sugli schermi Windows non disponibili.");
                return;
            }
            _displayStatus.Text = T(
                $"{snapshot.Displays.Count} connected displays. HDR/VRR capabilities have not been validated.",
                $"{snapshot.Displays.Count} schermi collegati. Le funzionalità HDR/VRR non sono state verificate.");
            foreach (GamingDisplayModeDto display in snapshot.Displays)
            {
                string rate = display.RefreshRateHz > 0 ? $" · {display.RefreshRateHz} Hz" : "";
                string primary = display.Primary ? T(" (Primary)", " (Principale)") : "";
                _displayItems.Children.Add(new TextBlock
                {
                    Text = $"{display.DisplayId}{primary}: {display.Width} × {display.Height}{rate}",
                    FontSize = _consolePresentation ? 20 : 14,
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }
        catch (Exception)
        {
            _displayStatus.Text = T("Could not inspect Windows displays.", "Impossibile controllare gli schermi Windows.");
        }
        finally { SetBusy(false); }
    }
}
