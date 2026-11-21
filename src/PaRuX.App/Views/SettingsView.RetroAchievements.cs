using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class SettingsView
    {
        private TextBlock? _retroStatus;

        /// <summary>RetroAchievements (emulated games) uses the owner's own user name and Web API key, plus the opt-out for unlock toasts.</summary>
        private StackPanel BuildRetroAchievementsSection()
        {
            StackPanel section = OwnerSection("Integrations", T("RETROACHIEVEMENTS", "RETROACHIEVEMENTS"),
                T("Show your RetroAchievements progress on an emulated game's card. You use your own RetroAchievements user name and Web API key (from your RetroAchievements settings page). The key is stored encrypted and never shown again. Needs the Internet switch on; nothing is requested until you press Show on a game.",
                  "Mostra i tuoi progressi RetroAchievements nella scheda di un gioco emulato. Usi il tuo nome utente e la tua chiave Web API RetroAchievements (dalla pagina impostazioni di RetroAchievements). La chiave è salvata cifrata e non viene più mostrata. Richiede l'interruttore Internet acceso; non viene richiesto nulla finché non premi Mostra su un gioco."),
                out _, out _retroStatus, "Settings_RetroAchievements");
            var card = (StackPanel)((Border)section.Children[1]).Child;
            var user = new TextBox { Header = T("RetroAchievements user name", "Nome utente RetroAchievements") };
            var key = new PasswordBox { Header = T("RetroAchievements Web API key", "Chiave Web API RetroAchievements") };
            AutomationProperties.SetAutomationId(user, "Settings_RetroAchievements_User_TextBox");
            AutomationProperties.SetAutomationId(key, "Settings_RetroAchievements_Key_PasswordBox");
            var save = new Button { Content = T("Save", "Salva") };
            var remove = new Button { Content = T("Remove", "Rimuovi") };
            AutomationProperties.SetAutomationId(save, "Settings_RetroAchievements_Save_Button");
            AutomationProperties.SetAutomationId(remove, "Settings_RetroAchievements_Remove_Button");
            save.Click += async (_, _) =>
            {
                try
                {
                    var status = await OwnerRecordsClient.Current.SaveRetroAchievementsAsync(
                        new RetroAchievementsCredentialsRequest { UserName = user.Text, ApiKey = key.Password }, CancellationToken.None);
                    key.Password = string.Empty;
                    _retroStatus!.Text = T("Saved for ", "Salvato per ") + status.UserName + ".";
                }
                catch (Exception ex) { _retroStatus!.Text = ex.Message; }
            };
            remove.Click += async (_, _) =>
            {
                try { await OwnerRecordsClient.Current.DeleteRetroAchievementsAsync(CancellationToken.None); _retroStatus!.Text = T("Removed.", "Rimossa."); }
                catch (Exception ex) { _retroStatus!.Text = ex.Message; }
            };
            card.Children.Add(user);
            card.Children.Add(key);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(save);
            row.Children.Add(remove);
            card.Children.Add(row);

            var toasts = new ToggleSwitch
            {
                Header = T("Announce unlocked achievements while a game runs", "Annuncia i risultati sbloccati mentre un gioco è in esecuzione"),
                OnContent = T("On", "Acceso"), OffContent = T("Off", "Spento"),
                IsOn = SettingsStore.Current.Data.AchievementToastsEnabled,
            };
            AutomationProperties.SetAutomationId(toasts, "Settings_AchievementToasts_Toggle");
            toasts.Toggled += (_, _) =>
            {
                SettingsStore.Current.Data.AchievementToastsEnabled = toasts.IsOn;
                SettingsStore.Current.Save();
                if (!toasts.IsOn) AchievementToastService.Current.Stop();
            };
            card.Children.Add(toasts);
            card.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
                Text = T("Only while a game you started from PaRuX is running, about once a minute, for Steam and RetroAchievements games with saved credentials. Nothing is polled otherwise.",
                         "Solo mentre un gioco avviato da PaRuX è in esecuzione, circa una volta al minuto, per i giochi Steam e RetroAchievements con credenziali salvate. Altrimenti non viene interrogato nulla."),
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    var status = await OwnerRecordsClient.Current.RetroAchievementsStatusAsync(CancellationToken.None);
                    _dispatcher.TryEnqueue(() => _retroStatus!.Text = status.Configured
                        ? T("Saved for ", "Salvato per ") + status.UserName + "." : T("Not set up.", "Non configurato."));
                }
                catch (Exception) { }
            });
            return section;
        }
    }
}
