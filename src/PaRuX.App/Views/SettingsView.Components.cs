using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// Settings › Runtime Components & Lifecycle: inspect component health (installed, version, path,
    /// and ownership: PaRuX-managed, user-managed or system-detected) and run Detect, Locate, Install,
    /// or Skip at any time.
    /// </summary>
    public sealed partial class SettingsView
    {
        private bool _componentsSettingsBusy;
        private CancellationTokenSource? _componentsSettingsCts;

        private async void RefreshComponentsSettings_Click(object sender, RoutedEventArgs e)
        {
            await RefreshRuntimeComponentsAsync();
        }

        public async Task RefreshRuntimeComponentsAsync()
        {
            if (_componentsSettingsBusy || ComponentsSettingsList == null) return;
            SetComponentsSettingsBusy(true, "Detecting installed components…");
            try
            {
                List<ComponentStatus> components = await RuntimeManagerClient.Current.GetComponentsAsync(CancellationToken.None);
                RenderComponentsSettings(components);
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = components.Count + T(" component(s) checked. Detection never installs or changes software.", " componenti controllati. Il rilevamento non installa né modifica mai il software.");
                }
            }
            catch (Exception ex)
            {
                Log.Write("Settings RefreshRuntimeComponents failed: " + ex.Message);
                if (ComponentsSettingsList != null) ComponentsSettingsList.Children.Clear();
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = T("Component discovery failed: ", "Rilevamento dei componenti non riuscito: ") + ex.Message;
                }
            }
            finally
            {
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
            }
        }

        private void SetComponentsSettingsBusy(bool busy, string message)
        {
            _componentsSettingsBusy = busy;
            if (ComponentsSettingsProgress != null)
            {
                ComponentsSettingsProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            }
            if (ComponentsSettingsStatusText != null && !string.IsNullOrWhiteSpace(message))
            {
                ComponentsSettingsStatusText.Text = message;
            }
            if (RefreshComponentsSettingsButton != null)
            {
                RefreshComponentsSettingsButton.IsEnabled = !busy;
            }
        }

        private IReadOnlyList<ComponentStatus> _componentsSettingsInventory = Array.Empty<ComponentStatus>();
        private string? _componentsSettingsInstallId;

        private static bool ComponentsSettingsItalian => string.Equals(T("en", "it"), "it", StringComparison.Ordinal);

        private void RenderComponentsSettings(IReadOnlyList<ComponentStatus> components)
        {
            if (ComponentsSettingsList == null) return;
            _componentsSettingsInventory = components;
            bool italian = ComponentsSettingsItalian;
            IReadOnlyList<OptionalComponentRow> rows = OptionalComponentPolicy.BuildRows(
                components, new OptionalComponentsContext(StoreEditionPolicy.AllowsExecutableDownloads, italian));
            OptionalComponentCards.Render(ComponentsSettingsList, rows, "Settings_OptionalComponent_", italian, _componentsSettingsBusy,
                OnOptionalComponentButtonAsync, OnOptionalComponentError);
        }

        private void OnOptionalComponentError(Exception ex)
        {
            Log.Write("Settings optional component action failed: " + ex.Message);
            SetComponentsSettingsBusy(false, ex.Message);
        }

        private async Task OnOptionalComponentButtonAsync(OptionalComponentRow row, OptionalComponentButton button)
        {
            if (_componentsSettingsBusy) return;
            ComponentStatus? component = _componentsSettingsInventory.FirstOrDefault(c => string.Equals(c.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            if (component == null) return;
            switch (button)
            {
                case OptionalComponentButton.DownloadNow: await InstallComponentSettingsAsync(component); break;
                case OptionalComponentButton.LocateExisting: await LocateComponentSettingsAsync(component); break;
                case OptionalComponentButton.ForgetLocation: await RemoveLocationSettingsAsync(component); break;
                case OptionalComponentButton.Skip: await SkipComponentSettingsAsync(component); break;
                case OptionalComponentButton.UndoSkip: await UnskipComponentSettingsAsync(component); break;
                case OptionalComponentButton.Remove: await RemoveManagedComponentSettingsAsync(component, row); break;
            }
        }

        private async void CancelComponentsSettingsDownload_Click(object sender, RoutedEventArgs e)
        {
            _componentsSettingsCts?.Cancel();
            string? id = _componentsSettingsInstallId;
            if (id == null) return;
            try { await RuntimeManagerClient.Current.CancelInstallAsync(id, CancellationToken.None); }
            catch (Exception ex) { Log.Write("Settings cancel install failed: " + ex.Message); }
        }

        private async Task RemoveManagedComponentSettingsAsync(ComponentStatus component, OptionalComponentRow row)
        {
            bool italian = ComponentsSettingsItalian;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = (italian ? "Rimuovere " : "Remove ") + row.Name + "?",
                Content = italian
                    ? "PaRuX eliminerà la propria copia scaricata. Puoi scaricarla di nuovo in qualsiasi momento. Il tuo software e i tuoi dati non vengono toccati."
                    : "PaRuX will delete its own downloaded copy. You can download it again at any time. Your own software and data are not touched.",
                PrimaryButtonText = OptionalComponentPolicy.ButtonLabel(OptionalComponentButton.Remove, italian),
                CloseButtonText = italian ? "Annulla" : "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            SetComponentsSettingsBusy(true, T("Removing ", "Rimozione di ") + row.Name + "…");
            try
            {
                await RuntimeManagerClient.Current.RemoveManagedAsync(component.Id, CancellationToken.None);
                if (ComponentsSettingsStatusText != null)
                    ComponentsSettingsStatusText.Text = row.Name + T(" removed.", " rimosso.");
            }
            finally
            {
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
                await RefreshRuntimeComponentsAsync();
            }
        }

        private async Task InstallComponentSettingsAsync(ComponentStatus component)
        {
            string? backend = ComponentLifecyclePresenter.BackendForComponent(component.Id);
            if (backend == null)
            {
                throw new InvalidOperationException("This component has no PaRuX-managed installer.");
            }

            SetComponentsSettingsBusy(true, "Checking available builds for " + component.DisplayName + "…");
            _componentsSettingsCts?.Cancel();
            _componentsSettingsCts?.Dispose();
            _componentsSettingsCts = new CancellationTokenSource();
            CancellationToken token = _componentsSettingsCts.Token;

            try
            {
                List<RuntimeBuildOption> available = await RuntimeManagerClient.Current.GetAvailableBuildsAsync(backend, token);
                RuntimeBuildOption? selected = ComponentLifecyclePresenter.SelectRecommendedBuild(available);
                if (selected == null)
                {
                    throw new InvalidOperationException("No compatible Windows build is currently published for " + component.DisplayName + ".");
                }

                long free;
                try { free = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(PaRuXDataRoot.Path))!).AvailableFreeSpace; }
                catch (Exception) { free = -1; }
                OptionalComponentDownloadGate.Decision gate = OptionalComponentDownloadGate.Evaluate(
                    selected.AssetSizeBytes, free, StoreEditionPolicy.AllowsExecutableDownloads);
                if (!gate.Allowed) throw new InvalidOperationException(ComponentsSettingsItalian ? gate.ReasonIt : gate.ReasonEn);

                if (ComponentsSettingsCancelButton != null) ComponentsSettingsCancelButton.Visibility = Visibility.Visible;
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = T("Installing ", "Installazione di ") + component.DisplayName + " " + selected.Tag
                        + (string.IsNullOrWhiteSpace(selected.Variant) ? string.Empty : " (" + selected.Variant + ")") + "…";
                }

                RuntimeInstallStatus started = await RuntimeManagerClient.Current.StartInstallAsync(
                    backend, selected.Tag, selected.Variant, token);

                _componentsSettingsInstallId = started.Id;
                RuntimeInstallStatus final = await RuntimeManagerClient.Current.WaitForInstallAsync(started.Id, progress =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (ComponentsSettingsStatusText != null)
                        {
                            ComponentsSettingsStatusText.Text = ComponentLifecyclePresenter.FormatInstallProgress(
                                component.DisplayName, progress.Stage, progress.BytesDownloaded, progress.TotalBytes);
                        }
                    });
                }, token);

                if (final.Stage != "completed")
                {
                    throw new InvalidOperationException(final.Error ?? ("Install ended with state " + final.Stage + "."));
                }

                try
                {
                    await RuntimeManagerClient.Current.UnskipAsync(component.Id, CancellationToken.None);
                }
                catch
                {
                    // Stale skip state is cosmetic and discovery hides it while installed
                }

                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = ComponentLifecyclePresenter.FormatInstallCompletion(component.DisplayName, final);
                }
            }
            catch (OperationCanceledException)
            {
                if (ComponentsSettingsStatusText != null)
                    ComponentsSettingsStatusText.Text = component.DisplayName + T(" download cancelled. Nothing was installed.", " download annullato. Non è stato installato nulla.");
            }
            finally
            {
                _componentsSettingsInstallId = null;
                if (ComponentsSettingsCancelButton != null) ComponentsSettingsCancelButton.Visibility = Visibility.Collapsed;
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
                await RefreshRuntimeComponentsAsync();
            }
        }

        private async Task LocateComponentSettingsAsync(ComponentStatus component)
        {
            string path;
            if (component.Id == "comfyui")
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                path = folder.Path;
            }
            else
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;
                path = file.Path;
            }

            SetComponentsSettingsBusy(true, "Registering existing " + component.DisplayName + "…");
            try
            {
                await RuntimeManagerClient.Current.LocateAsync(component.Id, path, CancellationToken.None);
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = component.DisplayName + T(" location registered.", " posizione registrata.");
                }
            }
            finally
            {
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
                await RefreshRuntimeComponentsAsync();
            }
        }

        private async Task RemoveLocationSettingsAsync(ComponentStatus component)
        {
            SetComponentsSettingsBusy(true, "Forgetting location for " + component.DisplayName + "…");
            try
            {
                await RuntimeManagerClient.Current.RemoveLocationAsync(component.Id, CancellationToken.None);
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = component.DisplayName + T(" location forgotten.", " posizione dimenticata.");
                }
            }
            finally
            {
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
                await RefreshRuntimeComponentsAsync();
            }
        }

        private async Task SkipComponentSettingsAsync(ComponentStatus component)
        {
            SetComponentsSettingsBusy(true, "Skipping " + component.DisplayName + "…");
            try
            {
                await RuntimeManagerClient.Current.SkipAsync(component.Id, CancellationToken.None);
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = component.DisplayName + T(" skipped for now.", " saltato per ora.");
                }
            }
            finally
            {
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
                await RefreshRuntimeComponentsAsync();
            }
        }

        private async Task UnskipComponentSettingsAsync(ComponentStatus component)
        {
            SetComponentsSettingsBusy(true, "Re-enabling setup for " + component.DisplayName + "…");
            try
            {
                await RuntimeManagerClient.Current.UnskipAsync(component.Id, CancellationToken.None);
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = component.DisplayName + T(" setup re-enabled.", " configurazione riabilitata.");
                }
            }
            finally
            {
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
                await RefreshRuntimeComponentsAsync();
            }
        }
    }
}
