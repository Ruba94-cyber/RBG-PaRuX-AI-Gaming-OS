using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Settings > Agents > "Code execution in chat". The managed Python runtime is opt-in and PARUX_MANAGED: nothing is
    /// installed until the owner presses Install, a Python the user installed is never touched or used, and Skip keeps
    /// the tool permanently off. State always comes from the Core; a failed call is shown as such.
    /// </summary>
    public sealed partial class SettingsView
    {
        private bool _chatCodeSettingsAdded;

        private void AddChatCodeSettingsSection()
        {
            if (_chatCodeSettingsAdded || SettingsContentPanel == null) return;
            _chatCodeSettingsAdded = true;

            var section = new StackPanel
            {
                Tag = "Agents",
                Spacing = 8,
                Visibility = string.Equals(_selectedSettingsCategory, "Agents", StringComparison.Ordinal)
                    ? Visibility.Visible : Visibility.Collapsed
            };
            section.Children.Add(new TextBlock
            {
                Text = T("CODE EXECUTION IN CHAT", "ESECUZIONE DI CODICE NELLA CHAT"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 0, 0)
            });

            var card = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? stroke) && stroke is Brush brush)
                card.BorderBrush = brush;

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = T("Lets the model run short Python programs in an isolated sandbox (no network, no access to your files, limited time and memory). Every run asks for your approval in the chat. It uses a Python runtime installed and owned by PaRuX; a Python you installed yourself is never modified or used.",
                         "Permette al modello di eseguire brevi programmi Python in una sandbox isolata (nessuna rete, nessun accesso ai tuoi file, tempo e memoria limitati). Ogni esecuzione chiede la tua approvazione nella chat. Usa un runtime Python installato e gestito da PaRuX; un Python installato da te non viene mai modificato né usato."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });
            var state = new TextBlock { TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(state, "Settings_ChatCode_State");
            content.Children.Add(state);

            var enabled = new ToggleSwitch
            {
                Header = T("Allow the model to run Python (still needs your approval each time)", "Consenti al modello di eseguire Python (serve comunque la tua approvazione ogni volta)"),
                OffContent = T("Off", "No"), OnContent = T("On", "Sì")
            };
            var install = new Button { Content = T("Install with PaRuX", "Installa con PaRuX") };
            var cancel = new Button { Content = T("Cancel install", "Annulla installazione"), Visibility = Visibility.Collapsed };
            var skip = new Button { Content = T("Skip", "Salta") };
            AutomationProperties.SetAutomationId(install, "Settings_ChatCode_Install_Button");
            AutomationProperties.SetAutomationId(skip, "Settings_ChatCode_Skip_Button");
            content.Children.Add(enabled);
            content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { install, cancel, skip } });

            bool loading = false;
            DispatcherTimerLike? poll = null;

            async Task Apply(Func<CancellationToken, Task<ChatCodeRuntimeDto>> call)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    Show(await call(cts.Token));
                }
                catch (Exception ex) { state.Text = T("Request failed: ", "Richiesta non riuscita: ") + ex.Message; }
            }

            void Show(ChatCodeRuntimeDto s)
            {
                loading = true;
                state.Text = ChatCodeApprovalPresenter.RuntimeStateText(s.State, s.Stage, s.Error, UiText.IsItalian);
                enabled.IsOn = s.Enabled;
                enabled.IsEnabled = s.State == "ready";
                install.IsEnabled = s.State is "not-installed" or "failed";
                install.Visibility = s.State == "ready" || s.State == "skipped" ? Visibility.Collapsed : Visibility.Visible;
                cancel.Visibility = s.State == "installing" ? Visibility.Visible : Visibility.Collapsed;
                skip.Content = s.Skipped ? T("Un-skip", "Non saltare") : T("Skip", "Salta");
                skip.IsEnabled = s.State != "installing";
                loading = false;
                if (s.State == "installing") poll ??= new DispatcherTimerLike(async () => await Apply(ct => ChatCodeClient.Current.GetStatusAsync(ct)));
                else { poll?.Stop(); poll = null; }
            }

            install.Click += async (_, _) => await Apply(ct => ChatCodeClient.Current.PostRuntimeAsync("install", ct));
            cancel.Click += async (_, _) => await Apply(ct => ChatCodeClient.Current.PostRuntimeAsync("cancel", ct));
            skip.Click += async (_, _) =>
            {
                bool skipped = string.Equals(skip.Content as string, T("Un-skip", "Non saltare"), StringComparison.Ordinal);
                await Apply(ct => ChatCodeClient.Current.PostRuntimeAsync(skipped ? "unskip" : "skip", ct));
            };
            enabled.Toggled += async (_, _) =>
            {
                if (loading) return;
                await Apply(ct => ChatCodeClient.Current.SetEnabledAsync(enabled.IsOn, ct));
            };

            state.Text = T("Checking...", "Verifica in corso...");
            _ = Apply(ct => ChatCodeClient.Current.GetStatusAsync(ct));

            card.Child = content;
            section.Children.Add(card);
            SettingsContentPanel.Children.Add(section);
        }

        /// <summary>Tiny repeating poll used only while an install runs; stops itself when the state leaves "installing".</summary>
        private sealed class DispatcherTimerLike
        {
            private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

            public DispatcherTimerLike(Func<Task> tick)
            {
                _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.CreateTimer();
                if (_timer == null) return;
                _timer.Interval = TimeSpan.FromSeconds(2);
                _timer.IsRepeating = true;
                _timer.Tick += async (_, _) => await tick();
                _timer.Start();
            }

            public void Stop() => _timer?.Stop();
        }
    }
}
