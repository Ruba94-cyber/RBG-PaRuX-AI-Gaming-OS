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

namespace PaRuX.Views
{
    public sealed partial class SettingsView
    {
        private StackPanel? _extensionsList;
        private TextBlock? _extensionsStatus;
        private string? _extensionPendingRemove;

        /// <summary>
        /// Games-side extensions: data-only folders under Documents\PaRuX\Extensions (a library theme, lookup links, launch presets).
        /// Nothing runs and nothing is requested; each extension shows its SHA-256 and can be enabled, disabled or removed.
        /// </summary>
        private StackPanel BuildGameExtensionsSection()
        {
            StackPanel section = OwnerSection("Integrations", T("GAME EXTENSIONS", "ESTENSIONI GIOCHI"),
                T("Extensions are data files, never programs: a library theme, lookup links you open yourself and launch presets you apply yourself. Put an extension folder in Documents\\PaRuX\\Extensions, check its SHA-256, then enable it. If its file changes later it switches itself off until you enable it again.",
                  "Le estensioni sono file di dati, mai programmi: un tema della libreria, link di ricerca che apri tu e preset di avvio che applichi tu. Metti la cartella di un'estensione in Documents\\PaRuX\\Extensions, controlla il suo SHA-256 e poi abilitala. Se il suo file cambia in seguito si disattiva da sola finché non la riabiliti."),
                out _extensionsList, out _extensionsStatus, "Settings_GameExtensions",
                (T("Open extensions folder", "Apri la cartella delle estensioni"), () => OpenExtensionsFolder()),
                (T("Refresh", "Aggiorna"), () => _ = RefreshExtensionsAsync()));
            _ = RefreshExtensionsAsync();
            return section;
        }

        private void OpenExtensionsFolder()
        {
            try
            {
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PaRuX", "Extensions");
                System.IO.Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception ex) { _extensionsStatus!.Text = T("Could not open the folder: ", "Impossibile aprire la cartella: ") + ex.Message; }
        }

        private async Task RefreshExtensionsAsync()
        {
            if (_extensionsList is null || _extensionsStatus is null) return;
            GamingCallResult<List<GameExtensionInfo>> result = await GamingOrchestratorClient.Current.ListExtensionsAsync(CancellationToken.None);
            _dispatcher.TryEnqueue(() => ShowExtensions(result));
        }

        private void ShowExtensions(GamingCallResult<List<GameExtensionInfo>> result)
        {
            _extensionsList!.Children.Clear();
            if (!result.Succeeded || result.Value is null)
            {
                _extensionsStatus!.Text = T("Extensions could not be read: ", "Impossibile leggere le estensioni: ") + result.Error;
                return;
            }
            _extensionsStatus!.Text = result.Value.Count == 0
                ? T("No extensions installed. A sample is in the PaRuX repository under docs\\extensions.", "Nessuna estensione installata. Un esempio è nel repository di PaRuX in docs\\extensions.")
                : result.Value.Count + T(" extension(s) found.", " estensione/i trovata/e.");
            foreach (GameExtensionInfo ext in result.Value) _extensionsList.Children.Add(BuildExtensionRow(ext));
        }

        private UIElement BuildExtensionRow(GameExtensionInfo ext)
        {
            var box = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 8) };
            AutomationProperties.SetAutomationId(box, "Settings_GameExtension_" + ext.FolderName);
            box.Children.Add(new TextBlock
            {
                Text = (ext.Valid ? ext.Name + " " + ext.Version + " - " + ext.Author : ext.FolderName),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
            if (ext.Description.Length > 0) box.Children.Add(new TextBlock { Text = ext.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            if (ext.Valid)
            {
                var parts = new List<string>();
                if (ext.Theme is not null) parts.Add(T("library theme", "tema della libreria"));
                if (ext.MetadataSources.Count > 0) parts.Add(ext.MetadataSources.Count + T(" lookup link(s)", " link di ricerca"));
                if (ext.LaunchPresets.Count > 0) parts.Add(ext.LaunchPresets.Count + T(" launch preset(s)", " preset di avvio"));
                box.Children.Add(new TextBlock { Text = T("Adds: ", "Aggiunge: ") + string.Join(", ", parts), Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
            }
            if (ext.Sha256.Length > 0)
            {
                var hash = new TextBox { Header = "SHA-256", Text = ext.Sha256, IsReadOnly = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
                AutomationProperties.SetAutomationId(hash, "Settings_GameExtension_Hash_" + ext.FolderName);
                box.Children.Add(hash);
                box.Children.Add(new TextBlock
                {
                    Opacity = 0.8, TextWrapping = TextWrapping.Wrap,
                    Text = ext.PublisherHash switch
                    {
                        "match" => T("The publisher hash file matches this manifest. That shows the file is what the publisher listed; it does not make it trusted.",
                                     "Il file hash del publisher coincide con questo manifest. Indica che il file è quello elencato dal publisher; non lo rende affidabile."),
                        "mismatch" => T("The publisher hash file does NOT match this manifest.", "Il file hash del publisher NON coincide con questo manifest."),
                        _ => T("No publisher hash file: nothing to compare with. Check the hash yourself if you got this from someone.",
                               "Nessun file hash del publisher: niente da confrontare. Controlla tu l'hash se l'hai ricevuta da qualcuno."),
                    },
                });
            }
            foreach (string issue in ext.Issues)
                box.Children.Add(new TextBlock { Text = "! " + issue, TextWrapping = TextWrapping.Wrap, Foreground = Application.Current.Resources.TryGetValue("PaRuXDangerTextBrush", out object? danger) && danger is Microsoft.UI.Xaml.Media.Brush b ? b : null });
            if (ext.ChangedSinceEnabled)
                box.Children.Add(new TextBlock { Text = T("This extension changed after you enabled it, so it is off. Review its hash and enable it again.",
                                                           "Questa estensione è cambiata dopo che l'hai abilitata, quindi è disattivata. Controlla l'hash e abilitala di nuovo."), TextWrapping = TextWrapping.Wrap });

            var enable = new ToggleSwitch
            {
                Header = T("Enabled", "Abilitata"), OnContent = T("On", "Acceso"), OffContent = T("Off", "Spento"),
                IsOn = ext.Enabled, IsEnabled = ext.Valid,
            };
            AutomationProperties.SetAutomationId(enable, "Settings_GameExtension_Enabled_" + ext.FolderName);
            enable.Toggled += async (_, _) =>
            {
                GamingCallResult<List<GameExtensionInfo>> r = await GamingOrchestratorClient.Current.SetExtensionEnabledAsync(ext.Id, enable.IsOn, CancellationToken.None);
                if (!enable.IsOn && SettingsStore.Current.Data.ActiveLibraryThemeExtensionId == ext.Id) UseLibraryTheme(null);
                _dispatcher.TryEnqueue(() => { ShowExtensions(r); if (!r.Succeeded) _extensionsStatus!.Text = r.Error ?? string.Empty; });
            };
            box.Children.Add(enable);

            if (ext.Valid && ext.Enabled && ext.Theme is not null)
            {
                bool active = SettingsStore.Current.Data.ActiveLibraryThemeExtensionId == ext.Id;
                var use = new ToggleSwitch
                {
                    Header = T("Use as library theme", "Usa come tema della libreria"), OnContent = T("Applied", "Applicato"), OffContent = T("Not applied", "Non applicato"),
                    IsOn = active,
                };
                AutomationProperties.SetAutomationId(use, "Settings_GameExtension_UseTheme_" + ext.FolderName);
                use.Toggled += (_, _) =>
                {
                    UseLibraryTheme(use.IsOn ? ext : null);
                    _ = RefreshExtensionsAsync();
                };
                box.Children.Add(use);
            }

            // Removal asks twice: the first press arms it, the second deletes the extension's own data files and folder.
            bool armed = _extensionPendingRemove == ext.FolderName;
            var remove = new Button { Content = armed ? T("Press again to remove", "Premi di nuovo per rimuovere") : T("Remove", "Rimuovi") };
            AutomationProperties.SetAutomationId(remove, "Settings_GameExtension_Remove_" + ext.FolderName);
            remove.Click += async (_, _) =>
            {
                if (_extensionPendingRemove != ext.FolderName) { _extensionPendingRemove = ext.FolderName; await RefreshExtensionsAsync(); return; }
                _extensionPendingRemove = null;
                if (SettingsStore.Current.Data.ActiveLibraryThemeExtensionId == ext.Id) UseLibraryTheme(null);
                GamingCallResult<List<GameExtensionInfo>> r = await GamingOrchestratorClient.Current.RemoveExtensionAsync(ext.FolderName, CancellationToken.None);
                if (r.Succeeded) _dispatcher.TryEnqueue(() => ShowExtensions(r));
                else _dispatcher.TryEnqueue(() => _extensionsStatus!.Text = T("Not removed: ", "Non rimossa: ") + r.Error);
            };
            box.Children.Add(remove);
            return box;
        }

        /// <summary>Applies (or clears, with null) a library theme through the accent system; the saved accent choice is never overwritten.</summary>
        private void UseLibraryTheme(GameExtensionInfo? ext)
        {
            SettingsStore.Current.Data.ActiveLibraryThemeExtensionId = ext?.Id ?? string.Empty;
            SettingsStore.Current.Save();
            LibraryThemeState.Set(ext?.Theme);
        }
    }
}
