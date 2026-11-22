using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Settings > Gaming & AI Sharing > "After sign-in apps": the PaRuX-owned list of apps PaRuX starts once after Windows sign-in.
    /// Off until the owner turns it on. PaRuX only ever starts the apps listed here; it never edits Windows Run keys, the Startup
    /// folder or any third-party startup entry. Core owns the list and every decision; this panel shows its answers.
    /// </summary>
    public sealed partial class SettingsView
    {
        private const string SignInIdPrefix = "Settings_SignInApps";
        private StackPanel? _signInList;
        private TextBlock? _signInStatus;
        private TextBlock? _signInRunText;
        private ToggleSwitch? _signInEnabled;
        private NumberBox? _signInSpacing, _signInDesktopDelay, _signInReadyTimeout, _signInConsoleDelay, _signInModeWait;
        private TextBox? _signInName, _signInPath, _signInArgs, _signInAumid, _signInProcess;
        private ComboBox? _signInKind;
        private CheckBox? _signInSkipConsole;
        private SignInAppListStatus? _signInState;
        private bool _signInLoading;

        private StackPanel BuildSignInAppListSection()
        {
            StackPanel section = OwnerSection("Gaming & AI Sharing", T("AFTER SIGN-IN APPS", "APP DOPO L'ACCESSO"),
                T("Apps PaRuX starts for you once after you sign in to Windows, in this order, spaced apart. Off until you turn it on. PaRuX only starts the apps listed here: it never changes Windows startup entries (Run keys, Startup folder) or other programs' startup settings. Nothing starts while Gaming is on; apps marked \"Skip in Console / TV\" are skipped while Console / TV mode is active.",
                  "App che PaRuX avvia per te una sola volta dopo l'accesso a Windows, in quest'ordine e a intervalli. Disattivato finché non lo attivi. PaRuX avvia solo le app elencate qui: non modifica mai le voci di avvio di Windows (chiavi Run, cartella Esecuzione automatica) né le impostazioni di avvio di altri programmi. Nulla parte mentre il Gaming è attivo; le app con \"Salta in Console / TV\" vengono saltate mentre la modalità Console / TV è attiva."),
                out _signInList, out _signInStatus, SignInIdPrefix,
                (T("Refresh", "Aggiorna"), () => _ = RefreshSignInAppsAsync()),
                (T("Run now", "Avvia ora"), () => _ = RunSignInAppsNowAsync()),
                (T("Stop", "Interrompi"), () => _ = CancelSignInAppsAsync()),
                (T("Import from legacy DelayedApps.json", "Importa da DelayedApps.json del vecchio launcher"), () => _ = ImportSignInAppsAsync()));
            AutomationProperties.SetAutomationId(section, SignInIdPrefix + "_Section");
            var card = (StackPanel)((Border)section.Children[1]).Child;

            // Buttons were created by OwnerSection; give them stable automation ids in the order they were added.
            if (card.Children.LastOrDefault() is StackPanel buttons)
            {
                string[] ids = { "Refresh", "RunNow", "Stop", "Import" };
                for (int i = 0; i < buttons.Children.Count && i < ids.Length; i++)
                    AutomationProperties.SetAutomationId(buttons.Children[i], SignInIdPrefix + "_" + ids[i] + "_Button");
            }

            _signInEnabled = new ToggleSwitch
            {
                Header = T("Start these apps after I sign in to Windows", "Avvia queste app dopo il mio accesso a Windows"),
                OnContent = T("On", "Acceso"), OffContent = T("Off", "Spento"),
            };
            AutomationProperties.SetAutomationId(_signInEnabled, SignInIdPrefix + "_Enabled_Toggle");
            _signInEnabled.Toggled += async (_, _) => { if (!_signInLoading) await SaveSignInAppsAsync(enabledOverride: _signInEnabled.IsOn); };
            card.Children.Insert(1, _signInEnabled);

            _signInRunText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
            AutomationProperties.SetAutomationId(_signInRunText, SignInIdPrefix + "_Run_Text");
            card.Children.Add(_signInRunText);

            card.Children.Add(new TextBlock
            {
                Text = T("Add an app", "Aggiungi un'app"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0),
            });
            _signInName = Field(T("Name", "Nome"), "Name");
            _signInKind = new ComboBox { Header = T("Type", "Tipo"), MinWidth = 260 };
            _signInKind.Items.Add(new ComboBoxItem { Content = T("Program (.exe)", "Programma (.exe)"), Tag = "exe" });
            _signInKind.Items.Add(new ComboBoxItem { Content = T("Microsoft Store app (AUMID)", "App di Microsoft Store (AUMID)"), Tag = "appx" });
            _signInKind.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(_signInKind, SignInIdPrefix + "_Kind_Combo");
            _signInPath = Field(T("Program path (.exe)", "Percorso del programma (.exe)"), "Path");
            var browse = new Button { Content = T("Browse...", "Sfoglia...") };
            AutomationProperties.SetAutomationId(browse, SignInIdPrefix + "_Browse_Button");
            browse.Click += async (_, _) => await BrowseSignInExeAsync();
            _signInArgs = Field(T("Arguments (optional)", "Argomenti (facoltativi)"), "Args");
            _signInAumid = Field(T("Store app id (AUMID), for example Publisher.App_8wekyb3d8bbwe!App", "ID app dello Store (AUMID), ad esempio Publisher.App_8wekyb3d8bbwe!App"), "Aumid");
            _signInProcess = Field(T("Already-running check: process name (optional)", "Controllo già in esecuzione: nome processo (facoltativo)"), "Process");
            _signInSkipConsole = new CheckBox { Content = T("Skip in Console / TV", "Salta in Console / TV") };
            AutomationProperties.SetAutomationId(_signInSkipConsole, SignInIdPrefix + "_SkipConsole_Check");
            var add = new Button { Content = T("Add to list", "Aggiungi all'elenco") };
            AutomationProperties.SetAutomationId(add, SignInIdPrefix + "_Add_Button");
            add.Click += async (_, _) => await AddSignInAppAsync();
            card.Children.Add(_signInName);
            card.Children.Add(_signInKind);
            card.Children.Add(_signInPath);
            card.Children.Add(browse);
            card.Children.Add(_signInArgs);
            card.Children.Add(_signInAumid);
            card.Children.Add(_signInProcess);
            card.Children.Add(_signInSkipConsole);
            card.Children.Add(add);

            card.Children.Add(new TextBlock
            {
                Text = T("Timing (seconds)", "Tempi (secondi)"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0),
            });
            _signInSpacing = Num(T("Between apps", "Tra un'app e l'altra"), 0, 300, "Spacing");
            _signInDesktopDelay = Num(T("Wait before the first app (desktop)", "Attesa prima della prima app (desktop)"), 0, 600, "DesktopDelay");
            _signInConsoleDelay = Num(T("Wait before the first app (Console / TV)", "Attesa prima della prima app (Console / TV)"), 0, 300, "ConsoleDelay");
            _signInReadyTimeout = Num(T("Wait for an app to appear (max)", "Attesa che un'app compaia (max)"), 1, 600, "ReadyTimeout");
            _signInModeWait = Num(T("Wait for the start mode to settle (max)", "Attesa che la modalità di avvio si stabilizzi (max)"), 0, 120, "ModeWait");
            var saveTiming = new Button { Content = T("Save timing", "Salva i tempi") };
            AutomationProperties.SetAutomationId(saveTiming, SignInIdPrefix + "_SaveTiming_Button");
            saveTiming.Click += async (_, _) => await SaveSignInAppsAsync();
            foreach (NumberBox n in new[] { _signInSpacing, _signInDesktopDelay, _signInConsoleDelay, _signInReadyTimeout, _signInModeWait })
                card.Children.Add(n);
            card.Children.Add(saveTiming);

            _ = RefreshSignInAppsAsync();
            return section;
        }

        private static TextBox Field(string header, string id)
        {
            var box = new TextBox { Header = header };
            AutomationProperties.SetAutomationId(box, SignInIdPrefix + "_" + id + "_TextBox");
            return box;
        }

        private static NumberBox Num(string header, double min, double max, string id)
        {
            var box = new NumberBox { Header = header, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 260 };
            AutomationProperties.SetAutomationId(box, SignInIdPrefix + "_" + id + "_NumberBox");
            return box;
        }

        private async Task RefreshSignInAppsAsync()
        {
            if (_signInList == null || _signInStatus == null) return;
            try
            {
                SignInAppListStatus status = await SignInAppListClient.Current.GetAsync(CancellationToken.None);
                ApplySignInStatus(status);
                if (status.StoreDamaged)
                    _signInStatus.Text = T("The saved list is damaged and was not changed. Saving a new list replaces it.", "L'elenco salvato è danneggiato e non è stato modificato. Salvando un nuovo elenco lo sostituisci.");
            }
            catch (Exception ex) { _signInStatus.Text = T("Could not read the list: ", "Impossibile leggere l'elenco: ") + ex.Message; }
        }

        private void ApplySignInStatus(SignInAppListStatus status)
        {
            _signInState = status;
            _signInLoading = true;
            try
            {
                _signInEnabled!.IsOn = status.Enabled;
                _signInSpacing!.Value = status.Settings.LaunchSpacingSeconds;
                _signInDesktopDelay!.Value = status.Settings.DesktopDelaySeconds;
                _signInConsoleDelay!.Value = status.Settings.ConsoleReadyDelaySeconds;
                _signInReadyTimeout!.Value = status.Settings.ReadyTimeoutSeconds;
                _signInModeWait!.Value = status.Settings.ModeWaitSeconds;
            }
            finally { _signInLoading = false; }

            _signInList!.Children.Clear();
            if (status.Apps.Count == 0)
                _signInList.Children.Add(Line(T("No apps in the list.", "Nessuna app nell'elenco."), 0.7));
            for (int i = 0; i < status.Apps.Count; i++) _signInList.Children.Add(BuildSignInRow(status.Apps, i));
            _signInStatus!.Text = string.Empty;
            _signInRunText!.Text = DescribeSignInRun(status.Run);
        }

        private UIElement BuildSignInRow(IReadOnlyList<SignInAppEntryStatus> apps, int index)
        {
            SignInAppEntryStatus item = apps[index];
            SignInAppEntry e = item.Entry;
            var row = new StackPanel { Spacing = 4 };
            AutomationProperties.SetAutomationId(row, SignInIdPrefix + "_Row_" + index);
            string target = e.Kind == SignInAppKind.Exe ? (e.Path ?? "") + (string.IsNullOrEmpty(e.Args) ? "" : " " + e.Args) : e.Aumid ?? "";
            row.Children.Add(Line((index + 1) + ". " + e.Name + " · " + (e.Kind == SignInAppKind.Exe ? T("Program", "Programma") : T("Store app", "App dello Store"))));
            row.Children.Add(Line(target, 0.65));
            if (item.Problem is not null)
                row.Children.Add(Line(T("Cannot start: ", "Non può partire: ") + item.Problem, 0.9));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var skip = new CheckBox { Content = T("Skip in Console / TV", "Salta in Console / TV"), IsChecked = e.SkipInConsole };
            AutomationProperties.SetAutomationId(skip, SignInIdPrefix + "_Skip_" + index);
            skip.Click += async (_, _) => await MutateSignInAppsAsync(list => list[index] = list[index] with { SkipInConsole = skip.IsChecked == true });
            var up = new Button { Content = T("Move up", "Su"), IsEnabled = index > 0 };
            var down = new Button { Content = T("Move down", "Giù"), IsEnabled = index < apps.Count - 1 };
            var remove = new Button { Content = T("Remove", "Rimuovi") };
            AutomationProperties.SetAutomationId(up, SignInIdPrefix + "_Up_" + index);
            AutomationProperties.SetAutomationId(down, SignInIdPrefix + "_Down_" + index);
            AutomationProperties.SetAutomationId(remove, SignInIdPrefix + "_Remove_" + index);
            up.Click += async (_, _) => await MutateSignInAppsAsync(list => { (list[index - 1], list[index]) = (list[index], list[index - 1]); });
            down.Click += async (_, _) => await MutateSignInAppsAsync(list => { (list[index + 1], list[index]) = (list[index], list[index + 1]); });
            remove.Click += async (_, _) => await MutateSignInAppsAsync(list => list.RemoveAt(index));
            buttons.Children.Add(skip);
            buttons.Children.Add(up);
            buttons.Children.Add(down);
            buttons.Children.Add(remove);
            row.Children.Add(buttons);
            return row;
        }

        private async Task MutateSignInAppsAsync(Action<List<SignInAppEntry>> change)
        {
            if (_signInState == null) return;
            List<SignInAppEntry> list = _signInState.Apps.Select(a => a.Entry).ToList();
            change(list);
            await SaveSignInAppsAsync(apps: list);
        }

        private async Task SaveSignInAppsAsync(bool? enabledOverride = null, List<SignInAppEntry>? apps = null)
        {
            if (_signInState == null || _signInStatus == null) return;
            try
            {
                var settings = new SignInAppListSettings(
                    LaunchSpacingSeconds: (int)Math.Round(double.IsNaN(_signInSpacing!.Value) ? 3 : _signInSpacing.Value),
                    DesktopDelaySeconds: (int)Math.Round(double.IsNaN(_signInDesktopDelay!.Value) ? 10 : _signInDesktopDelay.Value),
                    ReadyTimeoutSeconds: (int)Math.Round(double.IsNaN(_signInReadyTimeout!.Value) ? 60 : _signInReadyTimeout.Value),
                    ConsoleReadyDelaySeconds: (int)Math.Round(double.IsNaN(_signInConsoleDelay!.Value) ? 5 : _signInConsoleDelay.Value),
                    ModeWaitSeconds: (int)Math.Round(double.IsNaN(_signInModeWait!.Value) ? 15 : _signInModeWait.Value));
                SignInAppListStatus saved = await SignInAppListClient.Current.SaveAsync(new SignInAppListSaveRequest(
                    enabledOverride ?? _signInState.Enabled, settings, apps ?? _signInState.Apps.Select(a => a.Entry).ToList()), CancellationToken.None);
                ApplySignInStatus(saved);
                _signInStatus.Text = T("Saved.", "Salvato.");
            }
            catch (Exception ex)
            {
                _signInStatus.Text = T("Could not save: ", "Impossibile salvare: ") + ex.Message;
                await RefreshSignInAppsAsync();   // show the real saved state again (the toggle/list never lies)
                _signInStatus.Text = T("Could not save: ", "Impossibile salvare: ") + ex.Message;
            }
        }

        private async Task AddSignInAppAsync()
        {
            if (_signInState == null || _signInStatus == null) return;
            bool appx = (_signInKind!.SelectedItem as ComboBoxItem)?.Tag as string == "appx";
            var entry = new SignInAppEntry(
                _signInName!.Text.Trim(),
                appx ? SignInAppKind.Appx : SignInAppKind.Exe,
                appx ? null : _signInPath!.Text.Trim(),
                appx ? null : _signInArgs!.Text.Trim(),
                appx ? _signInAumid!.Text.Trim() : null,
                _signInProcess!.Text.Trim(),
                _signInSkipConsole!.IsChecked == true);
            List<SignInAppEntry> list = _signInState.Apps.Select(a => a.Entry).ToList();
            list.Add(entry);
            await SaveSignInAppsAsync(apps: list);
            if (_signInStatus.Text == T("Saved.", "Salvato."))
            {
                _signInName.Text = _signInPath!.Text = _signInArgs!.Text = _signInAumid!.Text = _signInProcess!.Text = string.Empty;
                _signInSkipConsole.IsChecked = false;
            }
        }

        private async Task BrowseSignInExeAsync()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null) return;
                _signInPath!.Text = file.Path;
                if (string.IsNullOrWhiteSpace(_signInName!.Text)) _signInName.Text = System.IO.Path.GetFileNameWithoutExtension(file.Path);
            }
            catch (Exception ex) { if (_signInStatus != null) _signInStatus.Text = ex.Message; }
        }

        private async Task RunSignInAppsNowAsync()
        {
            if (_signInStatus == null) return;
            try
            {
                SignInTriggerResponse r = await SignInAppListClient.Current.RunNowAsync(CancellationToken.None);
                _signInStatus.Text = DescribeSignInDecision(r.Decision);
                _signInRunText!.Text = DescribeSignInRun(r.Run);
                if (r.Decision == SignInTriggerDecision.Started) _ = PollSignInRunAsync();
            }
            catch (Exception ex) { _signInStatus.Text = ex.Message; }
        }

        private async Task PollSignInRunAsync()
        {
            for (int i = 0; i < 450 && _signInRunText != null; i++)
            {
                await Task.Delay(2000);
                try
                {
                    SignInAppListStatus s = await SignInAppListClient.Current.GetAsync(CancellationToken.None);
                    _signInRunText.Text = DescribeSignInRun(s.Run);
                    if (s.Run.State is not (SignInRunState.Waiting or SignInRunState.Running)) return;
                }
                catch (Exception) { return; }
            }
        }

        private async Task CancelSignInAppsAsync()
        {
            if (_signInStatus == null) return;
            try
            {
                SignInRunSnapshot snap = await SignInAppListClient.Current.CancelAsync(CancellationToken.None);
                _signInRunText!.Text = DescribeSignInRun(snap);
            }
            catch (Exception ex) { _signInStatus.Text = ex.Message; }
        }

        private async Task ImportSignInAppsAsync()
        {
            if (_signInStatus == null) return;
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".json");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null) return;

                SignInAppImportPreview preview = await SignInAppListClient.Current.ImportPreviewAsync(file.Path, CancellationToken.None);
                if (!preview.Ok)
                {
                    _signInStatus.Text = T("Cannot import: ", "Impossibile importare: ") + string.Join(" ", preview.Errors);
                    return;
                }

                var text = new StringBuilder();
                foreach (SignInAppEntry e in preview.Apps)
                    text.AppendLine("• " + e.Name + " (" + (e.Kind == SignInAppKind.Exe ? e.Path : e.Aumid) + ")" + (e.SkipInConsole ? "  [" + T("skip in Console / TV", "salta in Console / TV") + "]" : ""));
                if (preview.Apps.Count == 0) text.AppendLine(T("The file contains no apps.", "Il file non contiene app."));
                foreach (string w in preview.Warnings) text.AppendLine("! " + w);
                var content = new StackPanel { Spacing = 8 };
                content.Children.Add(new TextBlock { Text = text.ToString(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                content.Children.Add(new TextBlock
                {
                    Text = T("The imported list is saved turned OFF; turn it on yourself when you are ready. The file you picked is only read, never changed. Apps already running are skipped when the list runs.",
                             "L'elenco importato viene salvato SPENTO; attivalo tu quando vuoi. Il file scelto viene solo letto, mai modificato. Le app già in esecuzione vengono saltate quando l'elenco parte."),
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
                });
                var append = new CheckBox { Content = T("Add to my current list instead of replacing it", "Aggiungi all'elenco attuale invece di sostituirlo") };
                AutomationProperties.SetAutomationId(append, SignInIdPrefix + "_Import_Append_Check");
                content.Children.Add(append);
                AutomationProperties.SetAutomationId(content, SignInIdPrefix + "_Import_Preview");

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = T("Import after-sign-in apps", "Importa le app dopo l'accesso"),
                    Content = new ScrollViewer { Content = content, MaxHeight = 420 },
                    PrimaryButtonText = T("Import", "Importa"),
                    CloseButtonText = T("Cancel", "Annulla"),
                    DefaultButton = ContentDialogButton.Close,
                    IsPrimaryButtonEnabled = preview.Apps.Count > 0,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                SignInAppListStatus saved = await SignInAppListClient.Current.ImportConfirmAsync(file.Path, append.IsChecked == true, CancellationToken.None);
                ApplySignInStatus(saved);
                _signInStatus.Text = T("Imported. The list is off until you turn it on.", "Importato. L'elenco resta spento finché non lo attivi.");
            }
            catch (Exception ex) { _signInStatus.Text = T("Import failed: ", "Importazione non riuscita: ") + ex.Message; }
        }

        private static string DescribeSignInDecision(SignInTriggerDecision d) => d switch
        {
            SignInTriggerDecision.Started => T("Started.", "Avviato."),
            SignInTriggerDecision.Disabled => T("Turn the list on first.", "Attiva prima l'elenco."),
            SignInTriggerDecision.EmptyList => T("The list is empty.", "L'elenco è vuoto."),
            SignInTriggerDecision.GamingActive => T("Not started: Gaming is on.", "Non avviato: il Gaming è attivo."),
            SignInTriggerDecision.AlreadyRunning => T("The list is already running.", "L'elenco è già in esecuzione."),
            _ => d.ToString(),
        };

        private static string DescribeSignInRun(SignInRunSnapshot run)
        {
            string state = run.State switch
            {
                SignInRunState.Idle => T("Not run in this session.", "Non eseguito in questa sessione."),
                SignInRunState.Waiting => T("Waiting before the first app...", "In attesa della prima app..."),
                SignInRunState.Running => T("Running: ", "In corso: ") + Math.Min(run.CurrentIndex + 1, run.Total) + "/" + run.Total,
                SignInRunState.Completed => T("Finished.", "Terminato."),
                SignInRunState.Cancelled => T("Stopped.", "Interrotto."),
                SignInRunState.SkippedDisabled => T("Off.", "Spento."),
                SignInRunState.SkippedGamingActive => T("Stopped because Gaming is on.", "Interrotto perché il Gaming è attivo."),
                _ => run.State.ToString(),
            };
            if (run.Results.Count == 0) return state;
            return state + Environment.NewLine + string.Join(Environment.NewLine, run.Results.Select(r => r.Name + ": " + DescribeSignInOutcome(r.Outcome)));
        }

        private static string DescribeSignInOutcome(SignInAppOutcome o) => o switch
        {
            SignInAppOutcome.Launched => T("started", "avviata"),
            SignInAppOutcome.SkippedAlreadyRunning => T("already running", "già in esecuzione"),
            SignInAppOutcome.SkippedConsoleMode => T("skipped (Console / TV)", "saltata (Console / TV)"),
            SignInAppOutcome.SkippedGamingActive => T("skipped (Gaming)", "saltata (Gaming)"),
            SignInAppOutcome.SkippedInvalid => T("skipped (cannot start)", "saltata (non può partire)"),
            SignInAppOutcome.Failed => T("failed", "non riuscita"),
            SignInAppOutcome.Cancelled => T("cancelled", "annullata"),
            _ => o.ToString(),
        };
    }
}
