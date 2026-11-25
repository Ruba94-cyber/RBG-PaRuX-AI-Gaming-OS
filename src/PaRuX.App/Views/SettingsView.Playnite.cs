using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Settings > Gaming > Playnite Console Mode. Playnite is an OPTIONAL component the owner keeps (USER_MANAGED when located, SYSTEM_DETECTED when
    /// found): PaRuX detects it, can launch Desktop or Fullscreen, hands the GPU to a running game and gives AI back afterwards, and never edits
    /// Playnite, its config, themes or library. Core owns every decision; this panel shows its answers and never claims what it did not get.
    /// </summary>
    public sealed partial class SettingsView
    {
        private const string PlayniteIdPrefix = "Settings_Playnite";
        private StackPanel? _plRows;
        private TextBlock? _plStatus;
        private ToggleSwitch? _plYield, _plOnlyWhenTv, _plReturn, _plStartup;
        private ComboBox? _plMode, _plFallback;
        private TextBox? _plPath;
        private Button? _plInstallConfirm, _plSkip;
        private ToggleSwitch? _plConsoleStarts, _plStartConsole;
        private ConsoleComponentsPanel? _plTheme;
        private TextBlock? _plReadiness;
        private PlayniteStatusDto? _plState;
        private PlayniteInstallPlanDto? _plPlan;
        private bool _plLoading, _plApplying;

        private StackPanel BuildPlayniteSection()
        {
            StackPanel section = OwnerSection("Gaming & AI Sharing", T("PLAYNITE CONSOLE MODE", "PLAYNITE MODALITÀ CONSOLE"),
                T("Playnite is optional and stays yours: PaRuX finds it, can open it as Desktop or Fullscreen (Console) on the TV you chose, and while a Playnite game runs it stops PaRuX's own AI and frees the graphics card, then gives AI back when the game ends. PaRuX never changes your Playnite library; it only installs or selects the optional Aniki ReMake theme after your explicit confirmation and can restore the previous choice. It never closes programs it did not start.",
                  "Playnite è facoltativo e resta tuo: PaRuX lo trova, può aprirlo come Desktop o Fullscreen (Console) sulla TV scelta e, mentre un gioco di Playnite è in corso, ferma l'IA di PaRuX e libera la scheda video, poi restituisce l'IA a fine gioco. PaRuX non modifica la tua libreria Playnite; installa o seleziona il tema Aniki ReMake solo dopo una tua conferma esplicita e può ripristinare la scelta precedente. Non chiude programmi che non ha avviato."),
                out _plRows, out _plStatus, PlayniteIdPrefix,
                (T("Refresh", "Aggiorna"), () => _ = RefreshPlayniteAsync()),
                (T("Open Playnite Desktop", "Apri Playnite Desktop"), () => _ = LaunchPlayniteAsync(PlayniteModes.Desktop)),
                (T("Open Playnite Console", "Apri Playnite Console"), () => _ = LaunchPlayniteAsync(PlayniteModes.Console)),
                (T("Switch Console / Desktop", "Passa Console / Desktop"), () => _ = SwitchPlayniteAsync()),
                (T("Skip Playnite", "Salta Playnite"), () => _ = TogglePlayniteSkipAsync()),
                (T("Install Playnite (official release)", "Installa Playnite (versione ufficiale)"), () => _ = PlayniteInstallPlanAsync()),
                (T("Open the Console Mode launcher", "Apri il launcher Console Mode"), () => App.MainWin?.ShowPlayniteLauncher(PlayniteLauncherAction.Open)),
                (T("Test console mode", "Prova la modalità console"), () => _ = TestConsoleModeAsync()));
            AutomationProperties.SetAutomationId(section, PlayniteIdPrefix + "_Section");
            var card = (StackPanel)((Border)section.Children[1]).Child;

            if (card.Children.LastOrDefault() is StackPanel buttons)
            {
                string[] ids = { "Refresh", "LaunchDesktop", "LaunchConsole", "Switch", "Skip", "Install", "OpenLauncher", "TestConsole" };
                for (int i = 0; i < buttons.Children.Count && i < ids.Length; i++)
                    AutomationProperties.SetAutomationId(buttons.Children[i], PlayniteIdPrefix + "_" + ids[i] + "_Button");
                _plSkip = buttons.Children[4] as Button;
            }

            // Locate an existing Playnite (USER_MANAGED): a folder or one of the two official executables.
            _plPath = new TextBox { PlaceholderText = T(@"Folder of Playnite, e.g. D:\Apps\Playnite", @"Cartella di Playnite, es. D:\Apps\Playnite"), MinWidth = 320 };
            AutomationProperties.SetAutomationId(_plPath, PlayniteIdPrefix + "_LocatePath_TextBox");
            var locate = new Button { Content = T("Use this Playnite", "Usa questo Playnite") };
            AutomationProperties.SetAutomationId(locate, PlayniteIdPrefix + "_Locate_Button");
            locate.Click += async (_, _) => await LocatePlayniteAsync();
            var locateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            locateRow.Children.Add(_plPath);
            locateRow.Children.Add(locate);
            card.Children.Insert(card.Children.Count - 1, locateRow);

            _plInstallConfirm = new Button { Content = T("Confirm and install per user", "Conferma e installa per l'utente"), Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(_plInstallConfirm, PlayniteIdPrefix + "_InstallConfirm_Button");
            _plInstallConfirm.Click += async (_, _) => await PlayniteInstallConfirmAsync();
            card.Children.Insert(card.Children.Count - 1, _plInstallConfirm);

            // The AI-yield policy and the console profile.
            _plYield = PlayniteToggle(card, "AiYield", T("Free the graphics card and stop PaRuX AI while a Playnite game runs", "Libera la scheda video e ferma l'IA di PaRuX mentre un gioco di Playnite è in corso"));
            _plMode = new ComboBox { Header = T("Start Playnite as", "Avvia Playnite come"), MinWidth = 260 };
            _plMode.Items.Add(new ComboBoxItem { Content = T("Desktop", "Desktop"), Tag = PlayniteModes.Desktop });
            _plMode.Items.Add(new ComboBoxItem { Content = T("Console (Fullscreen)", "Console (Fullscreen)"), Tag = PlayniteModes.Console });
            AutomationProperties.SetAutomationId(_plMode, PlayniteIdPrefix + "_StartMode_ComboBox");
            _plMode.SelectionChanged += async (_, _) => await SavePlayniteSettingsAsync();
            card.Children.Insert(card.Children.Count - 1, _plMode);
            _plOnlyWhenTv = PlayniteToggle(card, "OnlyWhenTv", T("Console only when the chosen TV is connected", "Console solo quando la TV scelta è collegata"));
            _plFallback = new ComboBox { Header = T("When the TV is not connected", "Quando la TV non è collegata"), MinWidth = 260 };
            _plFallback.Items.Add(new ComboBoxItem { Content = T("Open Desktop normally", "Apri Desktop normalmente"), Tag = ConsoleStartupPolicy.FallbackNormal });
            _plFallback.Items.Add(new ComboBoxItem { Content = T("Open Desktop minimized", "Apri Desktop ridotto a icona"), Tag = ConsoleStartupPolicy.FallbackMinimized });
            AutomationProperties.SetAutomationId(_plFallback, PlayniteIdPrefix + "_Fallback_ComboBox");
            _plFallback.SelectionChanged += async (_, _) => await SavePlayniteSettingsAsync();
            card.Children.Insert(card.Children.Count - 1, _plFallback);
            _plReturn = PlayniteToggle(card, "ReturnToPaRuX", T("Return to PaRuX when Playnite (started by PaRuX) closes", "Torna a PaRuX quando Playnite (avviato da PaRuX) si chiude"));
            _plStartup = PlayniteToggle(card, "StartWithPaRuX", T("Start Playnite when PaRuX starts", "Avvia Playnite quando si avvia PaRuX"));
            _plConsoleStarts = PlayniteToggle(card, "ConsoleStartsPlaynite", T("Start Playnite Fullscreen (Aniki ReMake) in Console mode", "Avvia Playnite Fullscreen (Aniki ReMake) in modalità Console"));

            // The simple switch: every PaRuX start opens Playnite Fullscreen (theme chosen in Playnite), no TV needed, any controller.
            _plStartConsole = new ToggleSwitch
            {
                Header = T("Start PaRuX in Console mode (Playnite Fullscreen, any controller)", "Avvia PaRuX in modalità Console (Playnite Fullscreen, qualsiasi controller)"),
                OnContent = T("On", "Acceso"), OffContent = T("Off", "Spento")
            };
            AutomationProperties.SetAutomationId(_plStartConsole, PlayniteIdPrefix + "_StartInConsoleMode_Toggle");
            _plStartConsole.IsOn = SettingsStore.Current.Data.StartInConsoleMode;
            _plStartConsole.Toggled += (_, _) =>
            {
                if (_plApplying) return;
                SettingsStore.Current.Data.StartInConsoleMode = _plStartConsole.IsOn;
                SettingsStore.Current.Save();
                SetPlayniteStatus(_plStartConsole.IsOn
                    ? T("PaRuX will open Playnite Fullscreen at every start and stay minimized for the AI. If Playnite is missing it opens normally. Command line: PaRuX.exe --playnite-console.",
                        "PaRuX aprirà Playnite Fullscreen a ogni avvio e resterà ridotto a icona per l'IA. Se Playnite manca si apre normalmente. Riga di comando: PaRuX.exe --playnite-console.")
                    : T("PaRuX starts normally.", "PaRuX si avvia normalmente."));
            };
            card.Children.Insert(card.Children.Count - 1, _plStartConsole);

            // Optional Aniki ReMake theme component (Detect -> Locate / Install / Skip) and the live state rows of the whole Console flow.
            _plReadiness = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85,
                Text = T("Console mode starts Playnite Fullscreen with the theme you selected in Playnite, on the TV you chose, for any controller.",
                         "La modalità Console avvia Playnite Fullscreen con il tema che hai selezionato in Playnite, sulla TV scelta, per qualsiasi controller.") };
            AutomationProperties.SetAutomationId(_plReadiness, PlayniteIdPrefix + "_ConsoleFlow_Text");
            card.Children.Insert(card.Children.Count - 1, _plReadiness);
            _plTheme = new ConsoleComponentsPanel(PlayniteIdPrefix + "_Theme", includePlaynite: false);
            _plTheme.StateChanged += () => _ = RefreshPlayniteAsync();
            card.Children.Insert(card.Children.Count - 1, _plTheme.Root);
            _ = _plTheme.RefreshAsync();

            // Pieces that already exist elsewhere: shown here so the whole Console Mode is one page.
            var streamStart = new Button { Content = T("Start streaming mode (Sunshine already installed)", "Avvia la modalità streaming (Sunshine già installato)") };
            AutomationProperties.SetAutomationId(streamStart, PlayniteIdPrefix + "_StreamingStart_Button");
            streamStart.Click += async (_, _) => await StreamingActionAsync(start: true);
            var streamStop = new Button { Content = T("Stop streaming mode and restore the display", "Ferma la modalità streaming e ripristina lo schermo") };
            AutomationProperties.SetAutomationId(streamStop, PlayniteIdPrefix + "_StreamingStop_Button");
            streamStop.Click += async (_, _) => await StreamingActionAsync(start: false);
            var extras = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            extras.Children.Add(streamStart);
            extras.Children.Add(streamStop);
            card.Children.Insert(card.Children.Count - 1, extras);

            _ = RefreshPlayniteAsync();
            return section;
        }

        private ToggleSwitch PlayniteToggle(StackPanel card, string id, string header)
        {
            var toggle = new ToggleSwitch { Header = header, OnContent = T("On", "Acceso"), OffContent = T("Off", "Spento") };
            AutomationProperties.SetAutomationId(toggle, PlayniteIdPrefix + "_" + id + "_Toggle");
            toggle.Toggled += async (_, _) => await SavePlayniteSettingsAsync();
            card.Children.Insert(card.Children.Count - 1, toggle);
            return toggle;
        }

        private void SetPlayniteStatus(string text) { if (_plStatus is not null) _plStatus.Text = text; }

        private void AddPlayniteRow(string id, string label, string value)
        {
            if (_plRows is null) return;
            var row = Line(label + ": " + value);
            AutomationProperties.SetAutomationId(row, PlayniteIdPrefix + "_Row_" + id);
            _plRows.Children.Add(row);
        }

        private async Task RefreshPlayniteAsync()
        {
            if (_plLoading || _plRows is null) return;
            _plLoading = true;
            try
            {
                var status = await PlayniteClient.Current.GetStatusAsync();
                if (!status.Succeeded || status.Value is null)
                {
                    SetPlayniteStatus(T("PaRuX could not read the Playnite state: ", "PaRuX non ha potuto leggere lo stato di Playnite: ") + status.Error);
                    return;
                }
                _plState = status.Value;
                RenderPlaynite(status.Value);
                // Streaming and sign-in apps already have their own services; their state is read, never faked.
                string streaming = T("not available", "non disponibile");
                var s = await PlayniteClient.Current.GetStreamingStatusAsync();
                if (s.Succeeded && s.Value.ValueKind == JsonValueKind.Object &&
                    (s.Value.TryGetProperty("state", out JsonElement st) || s.Value.TryGetProperty("State", out st)))
                    streaming = st.GetString() ?? streaming;
                AddPlayniteRow("Streaming", T("Streaming mode", "Modalità streaming"), streaming);
                try
                {
                    SignInAppListStatus apps = await SignInAppListClient.Current.GetAsync(System.Threading.CancellationToken.None);
                    AddPlayniteRow("SignInApps", T("After-sign-in apps (skipped in Console when set)", "App dopo l'accesso (saltate in Console se impostato)"),
                        apps.Enabled ? T("on, ", "attivo, ") + apps.Apps.Count + T(" apps", " app") : T("off", "spento"));
                }
                catch (Exception) { AddPlayniteRow("SignInApps", T("After-sign-in apps", "App dopo l'accesso"), T("not available", "non disponibile")); }
                var pad = SettingsStore.Current.Data.PadActions;
                AddPlayniteRow("Pad", T("Controller buttons (any pad)", "Pulsanti del controller (qualsiasi pad)"),
                    (pad?.Enabled ?? true) ? T("on - set actions in Controller buttons", "attivo - imposta le azioni in Pulsanti del controller") : T("off", "spento"));
                await AddConsoleReadinessRowsAsync(status.Value);
            }
            finally { _plLoading = false; }
        }

        private void RenderPlaynite(PlayniteStatusDto st)
        {
            if (_plRows is null) return;
            _plRows.Children.Clear();
            PlayniteComponentDto c = st.Component;
            string found = !c.Detected ? T("not found", "non trovato")
                : c.Version is { Length: > 0 } v ? v + " (" + c.InstallKind + ", " + c.Source + ")" : c.InstallKind + ", " + c.Source;
            if (c.Skipped) found = T("skipped by you", "saltato da te");
            AddPlayniteRow("Component", T("Playnite", "Playnite"), found + " - " + c.Ownership);
            if (c.Folder is not null) AddPlayniteRow("Folder", T("Folder", "Cartella"), c.Folder);
            AddPlayniteRow("Running", T("Running now", "In esecuzione ora"), c.Running ? T("yes", "sì") : T("no", "no"));
            PlayniteSessionDto se = st.Session;
            AddPlayniteRow("Session", T("Game session", "Sessione di gioco"), PlayniteStateText(se));
            if (!string.IsNullOrEmpty(se.GameProcessName)) AddPlayniteRow("Game", T("Game process", "Processo del gioco"), se.GameProcessName + " (" + se.TrackedProcessCount + ")");
            if (!string.IsNullOrEmpty(se.LastError)) AddPlayniteRow("Error", T("Last problem", "Ultimo problema"), se.LastError);

            _plApplying = true;
            try
            {
                _plYield!.IsOn = st.Settings.AiYieldEnabled;
                _plOnlyWhenTv!.IsOn = st.Settings.ConsoleOnlyWhenTargetConnected;
                _plReturn!.IsOn = st.Settings.ReturnToPaRuXOnClose;
                _plStartup!.IsOn = st.Settings.LaunchAtStartup;
                _plConsoleStarts!.IsOn = st.Settings.ConsoleStartsPlaynite;
                if (_plStartConsole is not null) _plStartConsole.IsOn = SettingsStore.Current.Data.StartInConsoleMode;
                SelectByTag(_plMode!, st.Settings.StartMode);
                SelectByTag(_plFallback!, st.Settings.FallbackWithoutDisplay);
            }
            finally { _plApplying = false; }
            if (_plSkip is not null) _plSkip.Content = c.Skipped ? T("Use Playnite again", "Usa di nuovo Playnite") : T("Skip Playnite", "Salta Playnite");
            SetPlayniteStatus(c.Detected || c.Skipped ? string.Empty
                : T("Playnite was not found. Locate your copy, install the official release, or skip.", "Playnite non è stato trovato. Indica la tua copia, installa la versione ufficiale o salta."));
        }

        private static string PlayniteStateText(PlayniteSessionDto s) => s.State switch
        {
            "idle" => T("idle", "inattivo"),
            "game-detected" => T("game detected, preparing", "gioco rilevato, preparazione"),
            "yielding" => T("freeing the graphics card", "libero la scheda video"),
            "yielded" => T("game running, AI stopped", "gioco in corso, IA ferma"),
            "restoring" => T("restoring AI", "ripristino l'IA"),
            "user-override" => T("you returned to AI during the game", "sei tornato all'IA durante il gioco"),
            "failed" => T("failed (see the problem below)", "non riuscito (vedi il problema sotto)"),
            "disabled" => T("off (AI yield is off or Playnite skipped)", "spento (cessione IA disattivata o Playnite saltato)"),
            _ => s.State
        };

        private async Task SavePlayniteSettingsAsync()
        {
            if (_plApplying || _plState is null || _plMode is null) return;
            var s = new PlayniteSettingsDto
            {
                AiYieldEnabled = _plYield!.IsOn,
                StartMode = (_plMode.SelectedItem as ComboBoxItem)?.Tag as string ?? PlayniteModes.Desktop,
                TargetDisplayId = _plState.Settings.TargetDisplayId,
                ConsoleOnlyWhenTargetConnected = _plOnlyWhenTv!.IsOn,
                FallbackWithoutDisplay = (_plFallback!.SelectedItem as ComboBoxItem)?.Tag as string ?? ConsoleStartupPolicy.FallbackNormal,
                ReturnToPaRuXOnClose = _plReturn!.IsOn,
                LaunchAtStartup = _plStartup!.IsOn,
                ConsoleStartsPlaynite = _plConsoleStarts!.IsOn
            };
            var saved = await PlayniteClient.Current.SaveSettingsAsync(s);
            SetPlayniteStatus(saved.Succeeded ? T("Saved.", "Salvato.") : T("Not saved: ", "Non salvato: ") + saved.Error);
            await RefreshPlayniteAsync();
        }

        private async Task LaunchPlayniteAsync(string mode)
        {
            // Baseline BEFORE the launch: a Playnite that closes before the first poll must still bring PaRuX back.
            long? baseline = (await PlayniteClient.Current.GetStatusAsync()).Value?.Session.PaRuXLaunchedClosedCounter;
            var result = await PlayniteClient.Current.LaunchAsync(mode, MainWindow.PlayniteDisplayId());
            SetPlayniteStatus(result.Error ?? result.Value?.Message ?? (result.Value?.Outcome == "started" ? T("Playnite started.", "Playnite avviato.") : T("Done.", "Fatto.")));
            if (result.Value is { Ok: true }) App.MainWin?.BeginPlayniteReturnWatch(baseline);
            await RefreshPlayniteAsync();
        }

        private async Task SwitchPlayniteAsync()
        {
            var result = await PlayniteClient.Current.SwitchAsync(MainWindow.PlayniteDisplayId());
            SetPlayniteStatus(result.Error ?? result.Value?.Message ?? T("Switched.", "Cambiato."));
            App.MainWin?.BeginPlayniteReturnWatch();
            await RefreshPlayniteAsync();
        }

        private async Task TogglePlayniteSkipAsync()
        {
            bool skip = _plState?.Component.Skipped != true;
            var result = await PlayniteClient.Current.SkipAsync(skip);
            if (!result.Succeeded) SetPlayniteStatus(result.Error ?? string.Empty);
            await RefreshPlayniteAsync();
        }

        private async Task LocatePlayniteAsync()
        {
            string path = _plPath?.Text?.Trim() ?? string.Empty;
            if (path.Length == 0) { SetPlayniteStatus(T("Type or paste the Playnite folder first.", "Scrivi o incolla prima la cartella di Playnite.")); return; }
            var result = await PlayniteClient.Current.LocateAsync(path);
            SetPlayniteStatus(result.Succeeded ? T("Your Playnite was found. PaRuX will only read and start it.", "Il tuo Playnite è stato trovato. PaRuX lo leggerà e avvierà soltanto.") : result.Error ?? string.Empty);
            await RefreshPlayniteAsync();
        }

        // Install: show the exact release, size and SHA-256 first; nothing is downloaded until the owner confirms.
        private async Task PlayniteInstallPlanAsync()
        {
            SetPlayniteStatus(T("Checking the latest official Playnite release...", "Controllo l'ultima versione ufficiale di Playnite..."));
            (PlayniteInstallPlanDto? plan, string text) = PlayniteSetupFlow.Describe(await PlayniteClient.Current.GetInstallPlanAsync());
            _plPlan = plan;
            SetPlayniteStatus(text);
            if (_plInstallConfirm is not null) _plInstallConfirm.Visibility = plan is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task PlayniteInstallConfirmAsync()
        {
            if (_plPlan is not { Available: true } plan) return;
            if (_plInstallConfirm is not null) _plInstallConfirm.Visibility = Visibility.Collapsed;
            SetPlayniteStatus(await PlayniteSetupFlow.InstallAsync(plan, SetPlayniteStatus));
            await RefreshPlayniteAsync();
        }

        // The state rows of the Console flow come from Core (Playnite, theme), the saved display and the existing pad services (any pad).
        private async Task AddConsoleReadinessRowsAsync(PlayniteStatusDto status)
        {
            PlayniteThemeComponentDto? theme = null;
            try { theme = (await PlayniteClient.Current.GetThemeStatusAsync()).Value?.Component; }
            catch (Exception) { /* shown as "not checked yet" */ }
            bool displayChosen = !string.IsNullOrWhiteSpace(SettingsStore.Current.Data.ConsoleTargetDisplayId);
            var pads = new System.Collections.Generic.List<string>();
            try { foreach (var d in WindowsRawGamepadSource.ListDevices()) pads.Add(string.IsNullOrWhiteSpace(d.Name) ? "?" : d.Name); }
            catch (Exception) { /* no pad enumeration available: reported as none found */ }
            foreach (ConsoleReadinessRow row in ConsoleModeReadiness.Build(status.Component, theme, displayChosen, pads, T))
            {
                if (row.Id == "Playnite") continue;   // already shown as the Playnite component row above
                string mark = row.State switch { ConsoleReadinessState.Ok => "[OK] ", ConsoleReadinessState.Missing => "[x] ", ConsoleReadinessState.Skipped => "[-] ", _ => "[!] " };
                AddPlayniteRow(row.Id, mark + row.Label, row.Detail);
            }
        }

        // One button: start Playnite Fullscreen exactly as Console mode would, and come back to PaRuX when it closes.
        private async Task TestConsoleModeAsync()
        {
            var status = await PlayniteClient.Current.GetStatusAsync();
            if (!status.Succeeded || status.Value is null) { SetPlayniteStatus(status.Error ?? T("PaRuX could not read the Playnite state.", "PaRuX non ha potuto leggere lo stato di Playnite.")); return; }
            if (!ConsoleModeReadiness.CanTest(status.Value.Component))
            {
                SetPlayniteStatus(T("Playnite with its Fullscreen app is needed first: locate it or install it above.", "Serve prima Playnite con la sua app Fullscreen: indicalo o installalo qui sopra."));
                return;
            }
            SetPlayniteStatus(T("Starting Playnite Fullscreen. Close it (Playnite menu > Exit) and PaRuX comes back.", "Avvio Playnite Fullscreen. Chiudilo (menu di Playnite > Esci) e PaRuX torna in primo piano."));
            var result = await PlayniteClient.Current.LaunchAsync(PlayniteModes.Console, MainWindow.PlayniteDisplayId(), default, explicitRequest: true);
            if (result.Error is { } error) { SetPlayniteStatus(error); return; }
            SetPlayniteStatus(result.Value?.Message ?? (result.Value?.Outcome == "started"
                ? T("Playnite Fullscreen started. Close it and PaRuX comes back.", "Playnite Fullscreen avviato. Chiudilo e PaRuX torna in primo piano.")
                : T("Playnite was already running; nothing was started twice.", "Playnite era già in esecuzione; non è stato avviato due volte.")));
            if (result.Value is { Ok: true, Outcome: "started" })
                App.MainWin?.BeginPlayniteReturnWatch(status.Value.Session.PaRuXLaunchedClosedCounter, forceReturn: true);
            await RefreshPlayniteAsync();
        }

        private async Task StreamingActionAsync(bool start)
        {
            var result = start ? await PlayniteClient.Current.StartStreamingAsync(allowStartSunshine: false) : await PlayniteClient.Current.StopStreamingAsync();
            string text = result.Error ?? T("Streaming mode updated.", "Modalità streaming aggiornata.");
            if (result.Succeeded && result.Value.ValueKind == JsonValueKind.Object && result.Value.TryGetProperty("message", out JsonElement m) && m.GetString() is { Length: > 0 } msg) text = msg;
            SetPlayniteStatus(text);
            await RefreshPlayniteAsync();
        }
    }
}
