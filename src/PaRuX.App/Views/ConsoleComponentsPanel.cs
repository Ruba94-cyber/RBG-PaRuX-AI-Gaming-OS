using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Optional Console-mode components in the Runtime Manager pattern: Detect, then Locate existing / Install with PaRuX / Skip, each
    /// with its ownership. Used by first-run setup (Playnite and the Aniki ReMake theme) and by Settings > Console mode (theme; Playnite has
    /// its own section there), so a skipped component can be installed later from Settings. Every change waits for an explicit confirm
    /// press that shows exactly what will happen first; Core decides and refuses honestly (for example while Playnite is running).
    /// </summary>
    public sealed class ConsoleComponentsPanel
    {
        private static string T(string en, string it) => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

        private readonly string _id;
        private readonly bool _includePlaynite;
        private readonly StackPanel _rows = new() { Spacing = 4 };
        private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        private readonly StackPanel _locateRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        private readonly TextBox _path = new() { MinWidth = 320 };
        private readonly Button _confirm = new() { Visibility = Visibility.Collapsed };
        private readonly Button _cancel = new() { Visibility = Visibility.Collapsed };
        private Func<Task>? _pending;
        private bool _busy;
        private PlayniteComponentDto? _playnite;
        private PlayniteThemeComponentDto? _theme;
        private PlayniteThemePlanDto? _plan;
        private PlayniteInstallPlanDto? _playnitePlan;

        public StackPanel Root { get; } = new() { Spacing = 8 };

        /// <summary>Raised after a component state may have changed (the caller refreshes its own rows).</summary>
        public event Action? StateChanged;

        public ConsoleComponentsPanel(string automationIdPrefix, bool includePlaynite)
        {
            _id = automationIdPrefix;
            _includePlaynite = includePlaynite;
            Root.Children.Add(new TextBlock
            {
                Text = T("Optional Console mode components", "Componenti facoltativi per la modalità Console"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            Root.Children.Add(new TextBlock
            {
                Text = T("Each one is optional: find the copy you already have, let PaRuX install the official release after you confirm, or skip it and decide later in Settings. PaRuX only manages what it installed itself.",
                         "Ognuno è facoltativo: indica la copia che hai già, lascia che PaRuX installi la versione ufficiale dopo la tua conferma, oppure salta e decidi dopo in Impostazioni. PaRuX gestisce solo ciò che ha installato lui."),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.85
            });
            Root.Children.Add(new TextBlock
            {
                Text = T("Aniki ReMake also requires Aniki Helper. After installing the theme, open Playnite Desktop > Add-ons (F9) > Browse > Generic, install or update Aniki Helper, and restart Playnite. PaRuX never silently installs extensions.",
                         "Aniki ReMake richiede anche Aniki Helper. Dopo il tema, apri Playnite Desktop > Add-ons (F9) > Browse > Generic, installa o aggiorna Aniki Helper e riavvia Playnite. PaRuX non installa estensioni senza consenso."),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.85
            });
            Root.Children.Add(_rows);

            _path.PlaceholderText = T(@"Folder, e.g. D:\Apps\Playnite", @"Cartella, es. D:\Apps\Playnite");
            Tag(_path, "LocatePath_TextBox");
            var locate = Button(T("Use this folder", "Usa questa cartella"), "Locate_Button", () => _ = LocateAsync());
            _locateRow.Children.Add(_path);
            _locateRow.Children.Add(locate);
            Root.Children.Add(_locateRow);

            if (_includePlaynite)
            {
                _buttons.Children.Add(Button(T("Install Playnite (official release)", "Installa Playnite (versione ufficiale)"), "InstallPlaynite_Button", () => _ = PlaynitePlanAsync()));
                _buttons.Children.Add(Button(T("Skip Playnite", "Salta Playnite"), "SkipPlaynite_Button", () => _ = TogglePlayniteSkipAsync()));
            }
            _buttons.Children.Add(Button(T("Install Aniki ReMake theme (official release)", "Installa il tema Aniki ReMake (versione ufficiale)"), "InstallTheme_Button", () => _ = ThemePlanAsync()));
            _buttons.Children.Add(Button(T("Use Aniki ReMake in Playnite", "Usa Aniki ReMake in Playnite"), "SelectTheme_Button",
                () => Ask(PlayniteThemeSetupFlow.SelectConsentText(), T("Confirm and change the theme", "Conferma e cambia il tema"), SelectAsync)));
            _buttons.Children.Add(Button(T("Restore the previous theme", "Ripristina il tema precedente"), "RestoreTheme_Button",
                () => Ask(PlayniteThemeSetupFlow.RestoreConsentText(), T("Confirm and restore", "Conferma e ripristina"), RestoreAsync)));
            _buttons.Children.Add(Button(T("Roll back the theme install", "Annulla l'installazione del tema"), "RollbackTheme_Button",
                () => Ask(PlayniteThemeSetupFlow.RollbackConsentText(false), T("Confirm and roll back", "Conferma e annulla"), RollbackAsync)));
            _buttons.Children.Add(Button(T("Skip the theme", "Salta il tema"), "SkipTheme_Button", () => _ = ToggleThemeSkipAsync()));
            _buttons.Children.Add(Button(T("Refresh", "Aggiorna"), "Refresh_Button", () => _ = RefreshAsync()));
            Root.Children.Add(new ScrollViewer { Content = _buttons, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

            _confirm.Click += async (_, _) => await RunPendingAsync();
            Tag(_confirm, "Confirm_Button");
            _cancel.Content = T("Cancel", "Annulla");
            Tag(_cancel, "Cancel_Button");
            _cancel.Click += async (_, _) => await CancelAsync();
            var confirmRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            confirmRow.Children.Add(_confirm);
            confirmRow.Children.Add(_cancel);
            Root.Children.Add(confirmRow);
            Tag(_status, "Status_Text");
            Root.Children.Add(_status);
            AutomationProperties.SetAutomationId(Root, _id + "_Panel");
        }

        private void Tag(DependencyObject o, string name) => AutomationProperties.SetAutomationId(o, _id + "_" + name);

        private Button Button(string label, string name, Action click)
        {
            var b = new Button { Content = label };
            Tag(b, name);
            b.Click += (_, _) => click();
            return b;
        }

        private Button? Find(string name) => _buttons.Children.OfType<Button>()
            .FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == _id + "_" + name);

        private void SetStatus(string text) => _status.Text = text;

        private void Ask(string text, string confirmLabel, Func<Task> action)
        {
            _pending = action;
            _confirm.Content = confirmLabel;
            _confirm.Visibility = Visibility.Visible;
            _cancel.Visibility = Visibility.Visible;
            SetStatus(text);
        }

        private void ClearAsk()
        {
            _pending = null;
            _confirm.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
        }

        private async Task RunPendingAsync()
        {
            Func<Task>? action = _pending;
            ClearAsk();
            if (action is null || _busy) return;
            _busy = true;
            try { await action(); }
            catch (Exception ex) { SetStatus(T("Not completed: ", "Non completato: ") + ex.Message); }
            finally { _busy = false; }
            await RefreshAsync();
            StateChanged?.Invoke();
        }

        private async Task CancelAsync()
        {
            ClearAsk();
            if (_busy) { await PlayniteClient.Current.CancelThemeInstallAsync(); SetStatus(T("Cancelling...", "Annullamento...")); }
            else SetStatus(T("Nothing was changed.", "Non è stato cambiato nulla."));
        }

        public async Task RefreshAsync()
        {
            try
            {
                var theme = await PlayniteClient.Current.GetThemeStatusAsync();
                _theme = theme.Value?.Component;
                if (_includePlaynite)
                {
                    var pl = await PlayniteClient.Current.GetStatusAsync();
                    _playnite = pl.Value?.Component;
                }
                if (!theme.Succeeded) SetStatus(T("PaRuX could not read the theme state: ", "PaRuX non ha potuto leggere lo stato del tema: ") + theme.Error);
                Render();
            }
            catch (Exception ex) { SetStatus(T("PaRuX could not read the component state: ", "PaRuX non ha potuto leggere lo stato dei componenti: ") + ex.Message); }
        }

        private void Row(string name, string text)
        {
            var line = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            Tag(line, "Row_" + name);
            _rows.Children.Add(line);
        }

        private void Render()
        {
            _rows.Children.Clear();
            if (_includePlaynite)
            {
                PlayniteComponentDto? p = _playnite;
                string state = p is null ? T("not checked", "non controllato") : p.Skipped ? T("skipped by you", "saltato da te")
                    : !p.Detected ? T("not found", "non trovato") : (p.Version ?? p.InstallKind) + " (" + p.Source + ") - " + p.Ownership;
                Row("Playnite", T("Playnite: ", "Playnite: ") + state);
            }
            PlayniteThemeComponentDto? t = _theme;
            string theme = t is null ? T("not checked", "non controllato") : t.Skipped ? T("skipped by you", "saltato da te")
                : !t.Detected ? T("not installed", "non installato")
                : (t.Version is { Length: > 0 } v ? v : T("version unknown", "versione sconosciuta")) + " - " + t.Ownership +
                  (t.Selected ? T(", selected in Playnite", ", selezionato in Playnite") : T(", not selected", ", non selezionato")) +
                  (t.LicenseFile is null ? string.Empty : T(", license kept", ", licenza conservata"));
            Row("Theme", T("Aniki ReMake theme: ", "Tema Aniki ReMake: ") + theme);
            string helper = t is null ? T("not checked", "non controllato") :
                t.Skipped ? T("theme skipped", "tema saltato") :
                t.AnikiHelperFoundOnDisk
                    ? (t.AnikiHelperVersion ?? T("version unknown", "versione sconosciuta")) +
                        T(" (manifest found; verify in Playnite)", " (manifest trovato; verifica in Playnite)")
                    : T("required: install/update from Playnite Desktop > Add-ons (F9) > Browse > Generic",
                        "richiesto: installa/aggiorna da Playnite Desktop > Add-ons (F9) > Browse > Generic");
            Row("AnikiHelper", T("Aniki Helper: ", "Aniki Helper: ") + helper);
            if (t?.Message is { Length: > 0 } m && !t.Detected) Row("ThemeNote", m);

            string[] actions = t?.Actions ?? Array.Empty<string>();
            bool Has(string a) => actions.Contains(a);
            Show("InstallTheme_Button", t is not null && Has("install"));
            Show("SelectTheme_Button", t is not null && Has("select"));
            Show("RestoreTheme_Button", t is not null && Has("restore-selection"));
            Show("RollbackTheme_Button", t is not null && Has("rollback"));
            if (Find("SkipTheme_Button") is { } skip) skip.Content = t?.Skipped == true ? T("Use the theme again", "Usa di nuovo il tema") : T("Skip the theme", "Salta il tema");
            if (Find("SkipPlaynite_Button") is { } skipP) skipP.Content = _playnite?.Skipped == true ? T("Use Playnite again", "Usa di nuovo Playnite") : T("Skip Playnite", "Salta Playnite");
            Show("InstallPlaynite_Button", _includePlaynite && _playnite is { Detected: false });
        }

        private void Show(string name, bool visible)
        {
            if (Find(name) is { } b) b.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- actions ----

        private async Task LocateAsync()
        {
            string path = _path.Text?.Trim() ?? string.Empty;
            if (path.Length == 0) { SetStatus(T("Type or paste the folder first.", "Scrivi o incolla prima la cartella.")); return; }
            // The folder decides what it is: a theme folder or Playnite data folder is the theme; otherwise it is tried as Playnite itself.
            var theme = await PlayniteClient.Current.LocateThemeAsync(path);
            if (theme.Succeeded) { SetStatus(T("Your Aniki ReMake was found. PaRuX will only read it.", "Il tuo Aniki ReMake è stato trovato. PaRuX lo leggerà soltanto.")); }
            else if (_includePlaynite)
            {
                var pl = await PlayniteClient.Current.LocateAsync(path);
                SetStatus(pl.Succeeded ? T("Your Playnite was found. PaRuX will only read and start it.", "Il tuo Playnite è stato trovato. PaRuX lo leggerà e avvierà soltanto.") : pl.Error ?? theme.Error ?? string.Empty);
            }
            else SetStatus(theme.Error ?? string.Empty);
            await RefreshAsync();
            StateChanged?.Invoke();
        }

        private async Task ThemePlanAsync()
        {
            SetStatus(T("Checking the latest official Aniki ReMake release...", "Controllo l'ultima versione ufficiale di Aniki ReMake..."));
            (PlayniteThemePlanDto? plan, string text) = PlayniteThemeSetupFlow.Describe(await PlayniteClient.Current.GetThemePlanAsync());
            _plan = plan;
            if (plan is null) { SetStatus(text); return; }
            Ask(text, plan.ExistingFolder ? T("Confirm: replace and keep a backup", "Conferma: sostituisci e conserva un backup") : T("Confirm and install", "Conferma e installa"), InstallThemeAsync);
        }

        private async Task InstallThemeAsync()
        {
            if (_plan is not { Available: true } plan) return;
            (string text, bool _) = await PlayniteThemeSetupFlow.InstallAsync(plan, SetStatus);
            SetStatus(text);
        }

        private async Task SelectAsync() => Report(await PlayniteClient.Current.SelectThemeAsync());
        private async Task RestoreAsync() => Report(await PlayniteClient.Current.RestoreThemeSelectionAsync());
        private async Task RollbackAsync() => Report(await PlayniteClient.Current.RollbackThemeAsync());

        private void Report(GamingCallResult<PlayniteThemeActionResultDto> result) =>
            SetStatus(result.Error ?? result.Value?.Message ?? T("Done.", "Fatto."));

        private async Task ToggleThemeSkipAsync()
        {
            var r = await PlayniteClient.Current.SkipThemeAsync(_theme?.Skipped != true);
            if (!r.Succeeded) SetStatus(r.Error ?? string.Empty);
            await RefreshAsync();
            StateChanged?.Invoke();
        }

        private async Task TogglePlayniteSkipAsync()
        {
            var r = await PlayniteClient.Current.SkipAsync(_playnite?.Skipped != true);
            if (!r.Succeeded) SetStatus(r.Error ?? string.Empty);
            await RefreshAsync();
            StateChanged?.Invoke();
        }

        private async Task PlaynitePlanAsync()
        {
            SetStatus(T("Checking the latest official Playnite release...", "Controllo l'ultima versione ufficiale di Playnite..."));
            (PlayniteInstallPlanDto? plan, string text) = PlayniteSetupFlow.Describe(await PlayniteClient.Current.GetInstallPlanAsync());
            _playnitePlan = plan;
            if (plan is null) { SetStatus(text); return; }
            Ask(text, T("Confirm and install per user", "Conferma e installa per l'utente"), async () =>
            {
                if (_playnitePlan is { Available: true } p) SetStatus(await PlayniteSetupFlow.InstallAsync(p, SetStatus));
            });
        }
    }
}
