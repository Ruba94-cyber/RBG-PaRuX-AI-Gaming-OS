using System;
using System.Collections.Generic;
using System.IO;
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
    /// Settings > Gaming & AI Sharing > "Start at sign-in", "Support apps" and "Games the pause button never pauses".
    /// Everything is opt-in and reversible: the sign-in entry is one value for the current Windows user (put back exactly when turned
    /// off), support apps are programs YOU chose (never installed or changed by PaRuX, except a catalog download you confirmed, which
    /// lives in PaRuX's own folder and can be removed), and nothing here ever needs a TV or a monitor to be connected.
    /// </summary>
    public sealed partial class SettingsView
    {
        private const string LogonIdPrefix = "Settings_LogonStart";
        private const string SupportIdPrefix = "Settings_SupportApps";
        private ToggleSwitch? _logonToggle;
        private ComboBox? _logonWindow;
        private TextBox? _logonGpu;
        private TextBlock? _logonStatus, _logonPlanText;
        private StackPanel? _logonList;
        private bool _logonLoading;

        private StackPanel BuildLogonStartupSection()
        {
            StackPanel section = OwnerSection("Gaming & AI Sharing", T("START AT SIGN-IN", "AVVIO ALL'ACCESSO"),
                T("PaRuX can start when you sign in to Windows and apply your choice: Console (Playnite Fullscreen, your support apps, optional power plan) or the desktop side, as a normal window in front or minimised (your choice below). It uses ONE scheduled task for your Windows user only (\"" + LogonRegistration.TaskName + "\", no administrator rights); only if Windows refuses the task does it use the Run entry of your user instead. Whatever was there before is put back exactly when you turn it off. It never needs a TV or monitor: with nothing connected the desktop side starts. Ctrl at start opens the settings, Shift opens streaming.",
                  "PaRuX può avviarsi quando accedi a Windows e applicare la tua scelta: Console (Playnite Fullscreen, le tue app di supporto, piano energetico facoltativo) oppure il lato desktop, come finestra normale in primo piano o ridotta a icona (scegli qui sotto). Usa UNA sola attività pianificata per il tuo utente Windows (\"" + LogonRegistration.TaskName + "\", nessun diritto di amministratore); solo se Windows rifiuta l'attività usa invece la voce Run del tuo utente. Quando la spegni, ciò che c'era prima torna esattamente com'era. Non richiede mai una TV o un monitor: senza nulla di collegato parte il lato desktop. Ctrl all'avvio apre le impostazioni, Shift apre lo streaming."),
                out _logonList, out _logonStatus, LogonIdPrefix,
                (T("Preview what would start (dry run)", "Anteprima di cosa partirebbe (prova a secco)"), () => _ = LogonDryRunAsync()),
                (T("Show the last start timings", "Mostra gli ultimi tempi di avvio"), () => ShowStartupTimings()));
            AutomationProperties.SetAutomationId(section, LogonIdPrefix + "_Section");
            var card = (StackPanel)((Border)section.Children[1]).Child;

            _logonToggle = new ToggleSwitch
            {
                Header = T("Start PaRuX when I sign in to Windows and apply my Console / Desktop choice", "Avvia PaRuX quando accedo a Windows e applica la mia scelta Console / Desktop"),
                OnContent = T("On", "Acceso"), OffContent = T("Off", "Spento"),
            };
            AutomationProperties.SetAutomationId(_logonToggle, LogonIdPrefix + "_Enabled_Toggle");
            _logonToggle.Toggled += async (_, _) => { if (!_logonLoading) await ToggleLogonAsync(_logonToggle.IsOn); };
            card.Children.Insert(1, _logonToggle);

            // The same setting as Console / TV > "When no chosen TV is connected": it decides the desktop-side window for every start,
            // sign-in included (it used to be ignored at sign-in, which always minimised).
            _logonWindow = new ComboBox
            {
                Header = T("At sign-in, when Console does not start, the PaRuX window", "All'accesso, quando la Console non parte, la finestra di PaRuX"),
                MinWidth = 320,
            };
            _logonWindow.Items.Add(new ComboBoxItem { Content = T("Opens normally, in front (recommended)", "Si apre normalmente, in primo piano (consigliato)"), Tag = ConsoleStartupPolicy.FallbackNormal });
            _logonWindow.Items.Add(new ComboBoxItem { Content = T("Starts minimised on the taskbar", "Parte ridotta a icona sulla barra delle applicazioni"), Tag = ConsoleStartupPolicy.FallbackMinimized });
            AutomationProperties.SetAutomationId(_logonWindow, LogonIdPrefix + "_Window_Combo");
            _logonWindow.SelectedIndex = string.Equals(SettingsStore.Current.Data.StartupWithoutTv, ConsoleStartupPolicy.FallbackMinimized, StringComparison.Ordinal) ? 1 : 0;
            _logonWindow.SelectionChanged += (_, _) =>
            {
                if (_logonLoading) return;
                string chosen = (_logonWindow.SelectedItem as ComboBoxItem)?.Tag as string ?? ConsoleStartupPolicy.FallbackNormal;
                SettingsStore.Current.Data.StartupWithoutTv = chosen == ConsoleStartupPolicy.FallbackMinimized ? ConsoleStartupPolicy.FallbackMinimized : ConsoleStartupPolicy.FallbackNormal;
                SettingsStore.Current.Save();
                _logonStatus!.Text = T("Saved.", "Salvato.");
            };
            card.Children.Insert(2, _logonWindow);

            _logonGpu = new TextBox
            {
                Header = T("Optional: Console opens only when this graphics card is present (name or part of it, * allowed). Empty = no rule.",
                           "Facoltativo: la Console si apre solo se questa scheda grafica è presente (nome o parte, * ammesso). Vuoto = nessuna regola."),
                MaxLength = LogonGpuRule.MaxPatternLength, Text = SettingsStore.Current.Data.LogonGpuNamePattern ?? string.Empty,
            };
            AutomationProperties.SetAutomationId(_logonGpu, LogonIdPrefix + "_Gpu_TextBox");
            var saveGpu = new Button { Content = T("Save the graphics card rule", "Salva la regola della scheda grafica") };
            AutomationProperties.SetAutomationId(saveGpu, LogonIdPrefix + "_SaveGpu_Button");
            saveGpu.Click += (_, _) =>
            {
                SettingsStore.Current.Data.LogonGpuNamePattern = LogonGpuRule.Normalize(_logonGpu.Text) ?? string.Empty;
                SettingsStore.Current.Save();
                _logonStatus!.Text = T("Saved.", "Salvato.");
            };
            card.Children.Add(_logonGpu);
            card.Children.Add(saveGpu);
            _logonPlanText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            AutomationProperties.SetAutomationId(_logonPlanText, LogonIdPrefix + "_Plan_Text");
            card.Children.Add(_logonPlanText);
            _ = RefreshLogonStateAsync();
            return section;
        }

        /// <summary>Reads the real state (Task Scheduler and the Run value, off the UI thread) and shows how PaRuX is registered, when the
        /// task last ran and whether Windows started PaRuX at the last sign-in.</summary>
        private async Task RefreshLogonStateAsync(string? prefix = null)
        {
            if (_logonToggle == null) return;
            string? exe = StartupService.LaunchExecutable;
            LogonRegistrationStatus status;
            DateTimeOffset? lastStart, signIn;
            try
            {
                (status, lastStart, signIn) = await Task.Run(() =>
                {
                    LogonRegistrationStatus s = exe is null
                        ? new LogonRegistrationStatus(LogonRegistrationState.NotRegistered, LogonMechanism.None, false, false, false, null, null)
                        : RegistryRunKeyStore.CreateRegistration().Describe(exe);
                    return (s, LogonStartMarker.Read(RegistryRunKeyStore.LogonStartMarkerPath), SignInSessionProbe.Probe().StartUtc);
                });
            }
            catch (Exception ex) { _logonStatus!.Text = (prefix ?? string.Empty) + ex.Message; return; }

            _logonLoading = true;
            try { _logonToggle.IsOn = status.State == LogonRegistrationState.RegisteredForLogon; }
            finally { _logonLoading = false; }
            _logonStatus!.Text = (prefix ?? string.Empty) + DescribeLogonStatus(status, lastStart, signIn);
        }

        private string DescribeLogonStatus(LogonRegistrationStatus s, DateTimeOffset? lastStart, DateTimeOffset? signIn)
        {
            static string When(DateTimeOffset? t) => t is { } v ? v.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) : "-";
            var sb = new StringBuilder();
            switch (s.Mechanism)
            {
                case LogonMechanism.ScheduledTask:
                case LogonMechanism.ScheduledTaskAndRunKey:
                    sb.Append(T("Registered with the Windows scheduled task \"" + LogonRegistration.TaskName + "\".", "Registrato con l'attività pianificata di Windows \"" + LogonRegistration.TaskName + "\"."));
                    sb.Append(' ').Append(s.TaskLastRunUtc is null
                        ? T("It has not run yet.", "Non è ancora stata eseguita.")
                        : T("Last run: ", "Ultima esecuzione: ") + When(s.TaskLastRunUtc) + (s.TaskLastResult is int r && r != 0 ? T(" (result 0x", " (risultato 0x") + r.ToString("X8") + ")" : string.Empty) + ".");
                    break;
                case LogonMechanism.RunKey:
                    sb.Append(T("Registered with the Run entry of your user (fallback: Windows did not accept the scheduled task). Turn it off and on again to retry the task.",
                                "Registrato con la voce Run del tuo utente (riserva: Windows non ha accettato l'attività pianificata). Spegni e riaccendi per riprovare con l'attività."));
                    break;
                default:
                    sb.Append(s.State switch
                    {
                        LogonRegistrationState.RegisteredPlain => T("PaRuX starts with Windows without applying a Console / Desktop choice. Turn this on to apply it.", "PaRuX si avvia con Windows senza applicare una scelta Console / Desktop. Attivalo per applicarla."),
                        LogonRegistrationState.Foreign => T("Another command or task is registered under PaRuX's startup name; it is left untouched.", "Un altro comando o attività è registrato con il nome di avvio di PaRuX; non viene toccato."),
                        _ => T("Not registered: PaRuX does not start at sign-in.", "Non registrato: PaRuX non parte all'accesso."),
                    });
                    break;
            }
            if (lastStart is not null) sb.Append(' ').Append(T("Last sign-in start of PaRuX: ", "Ultimo avvio di PaRuX all'accesso: ") + When(lastStart) + ".");
            if (LogonStartHealthPolicy.Evaluate(s.State == LogonRegistrationState.RegisteredForLogon, signIn, lastStart, DateTimeOffset.UtcNow) == LogonStartHealth.NotStartedAtLastSignIn)
                sb.Append(Environment.NewLine).Append(T("Warning: at your last sign-in (" + When(signIn) + ") Windows did not start PaRuX. Turn this off and on again; if it keeps happening, check Task Scheduler > \"" + LogonRegistration.TaskName + "\".",
                    "Attenzione: al tuo ultimo accesso (" + When(signIn) + ") Windows non ha avviato PaRuX. Spegni e riaccendi questa opzione; se succede ancora, controlla Utilità di pianificazione > \"" + LogonRegistration.TaskName + "\"."));
            return sb.ToString();
        }

        private async Task ToggleLogonAsync(bool on)
        {
            try
            {
                string? exe = StartupService.LaunchExecutable;
                if (on)
                {
                    if (exe is null) { await RefreshLogonStateAsync(T("PaRuX could not find its own program file. ", "PaRuX non trova il proprio file di programma. ")); return; }
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = T("Start PaRuX at sign-in?", "Avviare PaRuX all'accesso?"),
                        Content = new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = T("PaRuX will create one scheduled task for your Windows user only (\"" + LogonRegistration.TaskName + "\": at your sign-in, normal rights, no administrator). If Windows does not allow it, the Run entry of your user is used instead. When you turn it off, whatever was there before is put back exactly. Nothing else in Windows changes.",
                                     "PaRuX creerà una sola attività pianificata solo per il tuo utente Windows (\"" + LogonRegistration.TaskName + "\": al tuo accesso, diritti normali, nessun amministratore). Se Windows non la consente, viene usata invece la voce Run del tuo utente. Quando la spegni, ciò che c'era prima torna esattamente com'era. Nient'altro in Windows cambia."),
                        },
                        PrimaryButtonText = T("Turn on", "Attiva"), CloseButtonText = T("Cancel", "Annulla"), DefaultButton = ContentDialogButton.Close,
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) { await RefreshLogonStateAsync(); return; }
                    LogonEnableResult result = await Task.Run(() => RegistryRunKeyStore.CreateRegistration().EnableDetailed(exe));
                    if (!result.Ok)
                    {
                        string admin = result.NeedsAdministrator ? T(" Windows needs administrator rights for this on this PC: start PaRuX once with \"Run as administrator\" and turn this on again.", " Su questo PC Windows richiede i diritti di amministratore: avvia PaRuX una volta con \"Esegui come amministratore\" e riattiva questa opzione.") : string.Empty;
                        await RefreshLogonStateAsync(T("Not changed: ", "Non modificato: ") + result.Error + admin + " ");
                        return;
                    }
                    Log.Write("Sign-in start registered with " + result.Mechanism + (result.Note is null ? "." : " (" + result.Note + ")."));
                    SettingsStore.Current.Data.StartWithWindows = true;
                    SettingsStore.Current.Save();
                    await RefreshLogonStateAsync(result.Note is null ? null : result.Note + " ");
                }
                else
                {
                    string? error = await Task.Run(() => RegistryRunKeyStore.CreateRegistration().Restore(exe));
                    if (error is not null) { await RefreshLogonStateAsync(T("Not changed: ", "Non modificato: ") + error + " "); return; }
                    Log.Write("Sign-in start turned off (previous entries restored).");
                    SettingsStore.Current.Data.StartWithWindows = StartupService.IsEnabled();
                    SettingsStore.Current.Save();
                    await RefreshLogonStateAsync();
                }
            }
            catch (Exception ex) { await RefreshLogonStateAsync(ex.Message + " "); }
        }

        private async Task LogonDryRunAsync()
        {
            try
            {
                _logonPlanText!.Text = T("Checking (nothing is started)...", "Controllo in corso (non parte nulla)...");
                _logonPlanText.Text = await App.MainWin!.DescribeLogonDryRunAsync();
            }
            catch (Exception ex) { _logonPlanText!.Text = ex.Message; }
        }

        private void ShowStartupTimings()
        {
            try
            {
                string path = PaRuXDataRoot.Combine("logs", "StartupTiming.log");
                _logonPlanText!.Text = File.Exists(path)
                    ? string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(14))
                    : T("No start timings yet. They are written when PaRuX starts at sign-in or starts the support apps.", "Ancora nessun tempo di avvio. Vengono scritti quando PaRuX parte all'accesso o avvia le app di supporto.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logonPlanText!.Text = ex.Message; }
        }

        // ---- support apps ------------------------------------------------------------------------------------------------------

        private StackPanel? _supportList, _supportCatalog;
        private TextBlock? _supportStatus;
        private ToggleSwitch? _supportEnabled;
        private SupportAppStatus? _supportState;
        private bool _supportLoading;
        private List<RedistStatusRow> _redistRows = new();

        private StackPanel BuildSupportAppsSection()
        {
            StackPanel section = OwnerSection("Gaming & AI Sharing", T("SUPPORT APPS", "APP DI SUPPORTO"),
                T("Helper programs you want running while Console is in use (for example a controller helper). PaRuX only STARTS the program you pick and can stop the ones you mark; it never installs, edits or updates your own copy. A catalog entry can be downloaded into PaRuX's own folder only if its official source, version and SHA-256 are pinned, only after you confirm, and it can be removed again. Off until you turn it on; nothing starts while Gaming is on.",
                  "Programmi di aiuto che vuoi attivi mentre la Console è in uso (per esempio un helper per il controller). PaRuX AVVIA solo il programma che scegli e può chiudere quelli che indichi; non installa, modifica né aggiorna mai la tua copia. Una voce del catalogo può essere scaricata nella cartella di PaRuX solo se fonte ufficiale, versione e SHA-256 sono fissati, solo dopo la tua conferma, e può essere rimossa. Spento finché non lo attivi; nulla parte mentre il Gaming è attivo."),
                out _supportList, out _supportStatus, SupportIdPrefix,
                (T("Refresh", "Aggiorna"), () => _ = RefreshSupportAppsAsync()),
                (T("Add a program...", "Aggiungi un programma..."), () => _ = AddSupportAppAsync(null)),
                (T("Start now", "Avvia ora"), () => _ = SupportActionAsync(c => SupportAppClient.Current.StartAsync(c))),
                (T("Stop (only what PaRuX started)", "Ferma (solo ciò che ha avviato PaRuX)"), () => _ = SupportActionAsync(c => SupportAppClient.Current.StopAsync(c))));
            AutomationProperties.SetAutomationId(section, SupportIdPrefix + "_Section");
            var card = (StackPanel)((Border)section.Children[1]).Child;
            _supportEnabled = new ToggleSwitch { Header = T("Start these programs with Console", "Avvia questi programmi con la Console"), OnContent = T("On", "Acceso"), OffContent = T("Off", "Spento") };
            AutomationProperties.SetAutomationId(_supportEnabled, SupportIdPrefix + "_Enabled_Toggle");
            _supportEnabled.Toggled += async (_, _) => { if (!_supportLoading) await SaveSupportAppsAsync(_supportEnabled.IsOn, null); };
            card.Children.Insert(1, _supportEnabled);
            card.Children.Add(new TextBlock { Text = T("Known helpers", "Helper noti"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            _supportCatalog = new StackPanel { Spacing = 6 };
            AutomationProperties.SetAutomationId(_supportCatalog, SupportIdPrefix + "_Catalog_List");
            card.Children.Add(_supportCatalog);
            _ = RefreshSupportAppsAsync();
            return section;
        }

        private async Task RefreshSupportAppsAsync()
        {
            if (_supportList == null) return;
            try
            {
                ApplySupportStatus(await SupportAppClient.Current.GetAsync(CancellationToken.None));
                List<SupportAppCatalogEntry> catalog = await SupportAppClient.Current.CatalogAsync(CancellationToken.None);
                _supportCatalog!.Children.Clear();
                try { _redistRows = await SupportAppClient.Current.RedistAsync(CancellationToken.None); }
                catch (Exception) { _redistRows = new List<RedistStatusRow>(); }   // the status line is informative only
                foreach (SupportAppCatalogEntry c in catalog) _supportCatalog.Children.Add(BuildCatalogRow(c));
            }
            catch (Exception ex) { _supportStatus!.Text = T("Could not read the list: ", "Impossibile leggere l'elenco: ") + ex.Message; }
        }

        private void ApplySupportStatus(SupportAppStatus status)
        {
            _supportState = status;
            _supportLoading = true;
            try { _supportEnabled!.IsOn = status.Enabled; } finally { _supportLoading = false; }
            _supportList!.Children.Clear();
            if (status.Apps.Count == 0) _supportList.Children.Add(Line(T("No support apps.", "Nessuna app di supporto."), 0.7));
            foreach (SupportAppRow row in status.Apps) _supportList.Children.Add(BuildSupportRow(row));
            _supportStatus!.Text = status.Damaged
                ? T("The saved list is damaged and was not changed. Saving a new list replaces it.", "L'elenco salvato è danneggiato e non è stato modificato. Salvando un nuovo elenco lo sostituisci.")
                : string.Join("  ", status.LastRun.Select(r => r.Name + ": " + r.Outcome));
        }

        private static string StopWithConsoleHelp => T(
            "\"Stop when Console ends\" closes only apps PaRuX started itself for this Console session, when you leave Console mode. A copy that was already running before is never stopped, and nothing is stopped if you turn the option off first.",
            "\"Ferma a fine Console\" chiude solo le app avviate da PaRuX per questa sessione Console, quando esci dalla modalità Console. Una copia già in esecuzione prima non viene mai fermata, e se spegni l'opzione prima non viene fermato nulla.");

        private UIElement BuildSupportRow(SupportAppRow row)
        {
            SupportAppEntry e = row.Entry;
            var box = new StackPanel { Spacing = 3 };
            string owner = row.Ownership == ComponentOwnership.PARUX_MANAGED
                ? T("downloaded by PaRuX (removable)", "scaricato da PaRuX (rimovibile)") : T("your own copy (PaRuX never changes it)", "la tua copia (PaRuX non la modifica mai)");
            box.Children.Add(new TextBlock { Text = e.Name + " - " + owner + (row.Running ? " - " + T("running", "in esecuzione") : string.Empty), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            box.Children.Add(Line(e.Path + (row.Problem is null ? string.Empty : "  (" + row.Problem + ")"), 0.7));
            var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            options.Children.Add(Check(T("Start with Console", "Avvia con la Console"), e.StartWithConsole, v => e with { StartWithConsole = v }, e));
            options.Children.Add(Check(T("Stop when Console ends", "Ferma a fine Console"), e.StopWithConsole, v => e with { StopWithConsole = v }, e));
            options.Children.Add(Check(T("Replace an older running copy", "Sostituisci una copia più vecchia in esecuzione"), e.ReplaceIfStale, v => e with { ReplaceIfStale = v }, e));
            box.Children.Add(options);
            // Plain-language scope of the option that is easy to misread: what "stop" does and does not touch.
            var stopHelp = Line(StopWithConsoleHelp, 0.75);
            AutomationProperties.SetAutomationId(stopHelp, SupportIdPrefix + "_StopWithConsoleHelp_" + e.Id + "_Text");
            box.Children.Add(stopHelp);
            var remove = new Button { Content = T("Remove from the list", "Rimuovi dall'elenco") };
            AutomationProperties.SetAutomationId(remove, SupportIdPrefix + "_Remove_" + e.Id + "_Button");
            remove.Click += async (_, _) => await SaveSupportAppsAsync(_supportState!.Enabled, _supportState.Apps.Select(a => a.Entry).Where(a => a.Id != e.Id).ToList());
            box.Children.Add(remove);
            return box;
        }

        private CheckBox Check(string label, bool value, Func<bool, SupportAppEntry> change, SupportAppEntry original)
        {
            var c = new CheckBox { Content = label, IsChecked = value };
            c.Click += async (_, _) =>
            {
                SupportAppEntry updated = change(c.IsChecked == true);
                await SaveSupportAppsAsync(_supportState!.Enabled, _supportState.Apps.Select(a => a.Entry.Id == original.Id ? updated : a.Entry).ToList());
            };
            return c;
        }

        private UIElement BuildCatalogRow(SupportAppCatalogEntry c)
        {
            var box = new StackPanel { Spacing = 3 };
            box.Children.Add(new TextBlock { Text = c.Name + " (" + c.License + ")", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            box.Children.Add(Line(c.Purpose.For(UiText.IsItalian), 0.75));
            RedistStatusRow? redist = c.RedistId is null ? null : _redistRows.FirstOrDefault(r => r.Id == c.RedistId);
            if (redist is not null)
            {
                string owner = redist.Ownership switch
                {
                    ComponentOwnership.PARUX_MANAGED => T("installed by PaRuX", "installato da PaRuX"),
                    ComponentOwnership.USER_MANAGED => T("your own copy", "la tua copia"),
                    ComponentOwnership.SYSTEM_DETECTED => T("found on this PC", "trovato su questo PC"),
                    _ => T("not installed", "non installato"),
                };
                string restart = redist.RestartRequired ? "  " + T("Restart Windows to finish.", "Riavvia Windows per completare.") : string.Empty;
                var state = Line(owner + (redist.Version is null ? string.Empty : " - " + redist.Version) + " - " + (UiText.IsItalian ? redist.MessageIt : redist.Message) + restart, 0.8);
                AutomationProperties.SetAutomationId(state, SupportIdPrefix + "_Redist_" + c.Id + "_State");
                box.Children.Add(state);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            bool downloaded = _supportState?.Apps.Any(a => a.Entry.CatalogId == c.Id) == true;
            if (c.Kind == SupportAppKind.Driver)
            {
                // A kernel driver is never downloaded, started or removed from this page.
                if (redist is not { State: RedistState.Installed } && c.NoDownloadReason is { } driverWhy) box.Children.Add(Line(driverWhy.For(UiText.IsItalian), 0.75));
                box.Children.Add(Line(T("License: ", "Licenza: ") + c.License + " - " + c.ProjectUrl, 0.6));
                return box;
            }
            if (c.Download is { } d && !downloaded)
            {
                bool bundled = redist?.BundledPayloadAvailable == true;
                var get = new Button { Content = bundled
                    ? T("Install the bundled " + d.Version + " (no download)...", "Installa la versione inclusa " + d.Version + " (senza scaricare)...")
                    : T("Download " + d.Version + " (" + (d.SizeBytes / (1024 * 1024)) + " MB)...", "Scarica " + d.Version + " (" + (d.SizeBytes / (1024 * 1024)) + " MB)...") };
                AutomationProperties.SetAutomationId(get, SupportIdPrefix + "_Download_" + c.Id + "_Button");
                get.Click += async (_, _) => await DownloadSupportAppAsync(c, d);
                buttons.Children.Add(get);
            }
            else if (downloaded)
            {
                var rm = new Button { Content = T("Remove the downloaded copy", "Rimuovi la copia scaricata") };
                AutomationProperties.SetAutomationId(rm, SupportIdPrefix + "_RemoveDownload_" + c.Id + "_Button");
                rm.Click += async (_, _) => await SupportActionAsync(ct => Task.FromResult(default(SupportAppStatus)!), () => SupportAppClient.Current.RemoveAsync(c.Id, CancellationToken.None));
                buttons.Children.Add(rm);
            }
            else if (c.NoDownloadReason is { } why)
                box.Children.Add(Line(why.For(UiText.IsItalian), 0.75));
            var locate = new Button { Content = T("Locate my copy...", "Indica la mia copia...") };
            AutomationProperties.SetAutomationId(locate, SupportIdPrefix + "_Locate_" + c.Id + "_Button");
            locate.Click += async (_, _) => await AddSupportAppAsync(c.Name);
            buttons.Children.Add(locate);
            box.Children.Add(buttons);
            if (c.Download is { } src) box.Children.Add(Line(T("Source: ", "Fonte: ") + src.SourcePage, 0.6));
            return box;
        }

        private async Task DownloadSupportAppAsync(SupportAppCatalogEntry c, SupportAppDownload d)
        {
            var text = new StringBuilder();
            text.AppendLine(T("Program: ", "Programma: ") + c.Name + " " + d.Version);
            text.AppendLine(T("License: ", "Licenza: ") + c.License);
            text.AppendLine(T("Size: ", "Dimensione: ") + (d.SizeBytes / 1024.0 / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB");
            text.AppendLine(T("From: ", "Da: ") + d.Url);
            text.AppendLine("SHA-256: " + d.Sha256);
            text.AppendLine();
            text.AppendLine(T("PaRuX downloads this exact file, checks its SHA-256 before unpacking anything, and keeps it in its own folder. It is not installed system-wide, needs no administrator rights and can be removed here. It does not start with Console until you turn that on.",
                              "PaRuX scarica esattamente questo file, ne controlla lo SHA-256 prima di estrarre qualsiasi cosa e lo tiene nella propria cartella. Non è installato nel sistema, non richiede diritti di amministratore e si può rimuovere da qui. Non parte con la Console finché non lo attivi."));
            if (c.RedistId == "sunshine")
            {
                text.AppendLine();
                text.AppendLine(T("If the PaRuX setup already carries this exact package, it is unpacked from there without any download. Sunshine is a game-stream host: it opens network ports only while it is running, and PaRuX does not start it until you enable streaming. No Windows service or firewall rule is created.",
                                  "Se il setup di PaRuX contiene già questo pacchetto esatto, viene estratto da lì senza scaricare nulla. Sunshine è un host di streaming: apre porte di rete solo mentre è in esecuzione e PaRuX non lo avvia finché non abiliti lo streaming. Non viene creato alcun servizio di Windows né regola del firewall."));
            }
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = T("Download " + c.Name + "?", "Scaricare " + c.Name + "?"),
                Content = new ScrollViewer { Content = new TextBlock { Text = text.ToString(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 380 },
                PrimaryButtonText = T("Download", "Scarica"), CloseButtonText = T("Cancel", "Annulla"), DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await SupportActionAsync(ct => Task.FromResult(default(SupportAppStatus)!), () => SupportAppClient.Current.InstallAsync(new SupportAppInstallRequest(c.Id, d.Version, true), CancellationToken.None));
        }

        private async Task SupportActionAsync(Func<CancellationToken, Task<SupportAppStatus>> action, Func<Task<SupportAppActionResult>>? detailed = null)
        {
            try
            {
                _supportStatus!.Text = T("Working...", "In corso...");
                if (detailed is not null)
                {
                    SupportAppActionResult r = await detailed();
                    ApplySupportStatus(r.Status);
                    _supportStatus.Text = r.Message;
                    await RefreshSupportAppsAsync();
                    _supportStatus.Text = r.Message;
                }
                else ApplySupportStatus(await action(CancellationToken.None));
            }
            catch (Exception ex) { _supportStatus!.Text = ex.Message; }
        }

        private async Task AddSupportAppAsync(string? suggestedName)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null || _supportState == null) return;
                string name = suggestedName ?? Path.GetFileNameWithoutExtension(file.Path);
                var entry = new SupportAppEntry("app-" + Guid.NewGuid().ToString("N")[..12], name, file.Path);
                var apps = _supportState.Apps.Select(a => a.Entry).Append(entry).ToList();
                await SaveSupportAppsAsync(_supportState.Enabled, apps);
            }
            catch (Exception ex) { _supportStatus!.Text = ex.Message; }
        }

        private async Task SaveSupportAppsAsync(bool enabled, List<SupportAppEntry>? apps)
        {
            try
            {
                ApplySupportStatus(await SupportAppClient.Current.SaveAsync(new SupportAppSaveRequest(enabled, apps ?? _supportState?.Apps.Select(a => a.Entry).ToList()), CancellationToken.None));
                _supportStatus!.Text = T("Saved.", "Salvato.");
            }
            catch (Exception ex) { _supportStatus!.Text = ex.Message; await RefreshSupportAppsAsync(); }
        }

        // ---- games the pause button never pauses -----------------------------------------------------------------------------

        private StackPanel? _freezeList;
        private TextBlock? _freezeStatus;
        private TextBox? _freezeInput;

        private StackPanel BuildFreezeExclusionSection()
        {
            StackPanel section = OwnerSection("Gaming & AI Sharing", T("GAMES NEVER PAUSED", "GIOCHI MAI MESSI IN PAUSA"),
                T("The controller pause / resume button only ever pauses a game PaRuX launched, and never one with anti-cheat. Add games here that must never be paused (game name or program name). A game that is already paused can still be resumed.",
                  "Il pulsante pausa / riprendi del controller mette in pausa solo un gioco avviato da PaRuX, e mai uno con anti-cheat. Aggiungi qui i giochi che non devono mai essere messi in pausa (nome del gioco o del programma). Un gioco già in pausa può comunque essere ripreso."),
                out _freezeList, out _freezeStatus, "Settings_FreezeExclusion");
            var card = (StackPanel)((Border)section.Children[1]).Child;
            _freezeInput = new TextBox { Header = T("Game or program name", "Nome del gioco o del programma"), MaxLength = PadFreezeExclusion.MaxLength };
            AutomationProperties.SetAutomationId(_freezeInput, "Settings_FreezeExclusion_Name_TextBox");
            var add = new Button { Content = T("Add to the list", "Aggiungi all'elenco") };
            AutomationProperties.SetAutomationId(add, "Settings_FreezeExclusion_Add_Button");
            add.Click += (_, _) =>
            {
                var data = SettingsStore.Current.Data;
                data.PadFreezeExcludedGames = PadFreezeExclusion.Normalize(data.PadFreezeExcludedGames.Append(_freezeInput.Text));
                SettingsStore.Current.Save();
                _freezeInput.Text = string.Empty;
                RenderFreezeList();
            };
            card.Children.Add(_freezeInput);
            card.Children.Add(add);
            RenderFreezeList();
            return section;
        }

        private void RenderFreezeList()
        {
            if (_freezeList == null) return;
            _freezeList.Children.Clear();
            var items = SettingsStore.Current.Data.PadFreezeExcludedGames;
            if (items.Count == 0) _freezeList.Children.Add(Line(T("No games on the list.", "Nessun gioco nell'elenco."), 0.7));
            foreach (string name in items.ToList())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                var rm = new Button { Content = T("Remove", "Rimuovi") };
                rm.Click += (_, _) =>
                {
                    SettingsStore.Current.Data.PadFreezeExcludedGames = items.Where(x => x != name).ToList();
                    SettingsStore.Current.Save();
                    RenderFreezeList();
                };
                row.Children.Add(rm);
                _freezeList.Children.Add(row);
            }
        }
    }
}
