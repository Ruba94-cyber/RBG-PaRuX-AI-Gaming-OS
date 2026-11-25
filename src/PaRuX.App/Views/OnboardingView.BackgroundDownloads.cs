using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>First-run question: allow background runtime downloads and online catalog checks? Safe default OFF; Corporate always OFF.</summary>
    public sealed partial class OnboardingView
    {
        private bool _backgroundDownloadsLoading = true;

        private void ApplyBackgroundDownloadsChoice(bool currentlyOn)
        {
            if (BackgroundDownloadsToggle == null) return;
            BackgroundDownloadsChoiceView v = BackgroundDownloadsChoice.Build(currentlyOn, CorporateMode.IsActive);
            _backgroundDownloadsLoading = true;
            try
            {
                BackgroundDownloadsToggle.IsOn = v.IsOn;
                BackgroundDownloadsToggle.IsEnabled = v.CanChange;
                BackgroundDownloadsExplanationText.Text = T(v.ExplanationEn, v.ExplanationIt);
                BackgroundDownloadsStatusText.Text = T(v.StateEn, v.StateIt);
            }
            finally { _backgroundDownloadsLoading = false; }
        }

        private async Task LoadBackgroundDownloadsChoiceAsync()
        {
            try
            {
                MaintenanceStatusDto status = await AutoMaintenanceClient.Current.GetStatusAsync(CancellationToken.None);
                ApplyBackgroundDownloadsChoice(status.Settings.KeepRuntimesUpdated);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Core not reachable yet: keep the safe default (off) and say nothing was changed.
                Log.Write("Onboarding background-downloads read failed: " + ex.Message);
                ApplyBackgroundDownloadsChoice(false);
            }
        }

        private async void BackgroundDownloadsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_backgroundDownloadsLoading) return;
            bool requested = BackgroundDownloadsChoice.Requested(BackgroundDownloadsToggle.IsOn, CorporateMode.IsActive);
            try
            {
                MaintenanceStatusDto status = await AutoMaintenanceClient.Current.GetStatusAsync(CancellationToken.None);
                MaintenanceSettingsDto saved = await AutoMaintenanceClient.Current.SetSettingsAsync(
                    status.Settings.Enabled, status.Settings.IntervalHours, CancellationToken.None, keepRuntimesUpdated: requested);
                ApplyBackgroundDownloadsChoice(saved.KeepRuntimesUpdated);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not saved: show the truthful state (off) rather than a switch that looks on.
                Log.Write("Onboarding background-downloads save failed: " + ex.Message);
                ApplyBackgroundDownloadsChoice(false);
                BackgroundDownloadsStatusText.Text = T("Could not save this choice, so it stays off: ", "Impossibile salvare la scelta, quindi resta spenta: ") + ex.Message;
            }
        }
    }
}
