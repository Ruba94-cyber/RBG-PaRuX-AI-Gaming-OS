using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// The compact "Playnite Console Mode" launcher: big Console / Desktop / Streaming / Settings buttons, live status of each piece, the
    /// Detect / Locate / Install / Skip setup when Playnite is missing, a plain-language settings page and the optional shortcuts page.
    /// It is only a surface: every decision comes from Core (PlayniteClient), <see cref="ConsoleStartupPolicy"/> and the launcher
    /// contracts, and it never claims what it did not get back. It lives inside a ContentDialog, so the existing Console/TV pad
    /// navigation (directions, A to accept, B to close or go back) operates it exactly like any other dialog; mouse and keyboard work too.
    /// </summary>
    public sealed class PlayniteLauncherSurface
    {
        private enum Page { Home, Settings, Shortcuts }

        private static string T(string en, string it) => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;
        private const string Id = "PlayniteLauncher";

        private readonly DispatcherQueue _queue;
        private readonly DispatcherQueueTimer _timer;
        private readonly bool _large;
        private readonly Action _closeDialog;
        private readonly Action<bool> _titleBack;

        private readonly StackPanel _root = new() { Spacing = 12 };
        private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 24 };
        private readonly StackPanel _rows = new() { Spacing = 4 };
        private readonly StackPanel _home = new() { Spacing = 10 };
        private readonly StackPanel _setup = new() { Spacing = 8 };
        private readonly StackPanel _settingsPage = new() { Spacing = 10, Visibility = Visibility.Collapsed };
        private readonly StackPanel _shortcutsPage = new() { Spacing = 10, Visibility = Visibility.Collapsed };
        private Button _console = null!, _desktop = null!, _streaming = null!, _settingsButton = null!, _switch = null!, _stopStreaming = null!;
        private Button _skip = null!, _installConfirm = null!;
        private TextBox _path = null!;
        private TextBlock _installText = null!;
        private ToggleSwitch _yield = null!, _onlyTv = null!, _return = null!, _startup = null!;
        private ComboBox _startMode = null!, _fallback = null!;
        private TextBlock _shortcutPreview = null!;
        private CheckBox[] _shortcutBoxes = Array.Empty<CheckBox>();
        private CheckBox _desktopBox = null!, _jumpBox = null!;
        private Button _shortcutsApply = null!, _shortcutsRemove = null!;
        private IReadOnlyList<PlannedShortcut> _plannedShortcuts = Array.Empty<PlannedShortcut>();
        private IReadOnlyList<string> _plannedRemovals = Array.Empty<string>();

        private PlayniteStatusDto? _status;
        private PlayniteInstallPlanDto? _plan;
        private string _streamingState = string.Empty;
        private string _tvState = string.Empty;
        private int _tick;
        private bool _busy, _applying, _disposed, _settingsOnly;
        private Page _page = Page.Home;
        private readonly CancellationTokenSource _lifetime = new();

        public FrameworkElement Root => _root;
        public bool IsOnSubPage => _page == Page.Shortcuts || (_page == Page.Settings && !_settingsOnly);

        public PlayniteLauncherSurface(DispatcherQueue queue, bool large, bool settingsOnly, Action closeDialog, Action<bool> titleBack)
        {
            _settingsOnly = settingsOnly;
            _queue = queue;
            _large = large;
            _closeDialog = closeDialog;
            _titleBack = titleBack;
            _root.MinWidth = large ? 640 : 520;
            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(2500);
            _timer.Tick += (_, _) => _ = RefreshAsync();
            Build();
        }

        // ---- lifetime ---------------------------------------------------------------------------------------------------------
        public void Start()
        {
            _ = RefreshAsync();
            _timer.Start();
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Stop();
            _lifetime.Cancel();
        }

        /// <summary>Back from Settings or Shortcuts to the main buttons. False when already on the main page (the dialog may close).</summary>
        public bool TryBack()
        {
            if (!IsOnSubPage) return false;
            Show(_page == Page.Shortcuts ? Page.Settings : Page.Home);
            return true;
        }

        /// <summary>Runs one requested action. <paramref name="fromRequest"/>: it came from an argument, shortcut or another process.</summary>
        public async Task RunAsync(PlayniteLauncherAction action, bool fromRequest)
        {
            switch (action)
            {
                case PlayniteLauncherAction.Settings:
                    Show(Page.Settings);
                    break;
                case PlayniteLauncherAction.Console: await LaunchAsync(PlayniteModes.Console, fromRequest); break;
                case PlayniteLauncherAction.Desktop: await LaunchAsync(PlayniteModes.Desktop, fromRequest); break;
                case PlayniteLauncherAction.Streaming: await StreamingAsync(start: true, fromRequest); break;
                default: Show(Page.Home); break;
            }
        }

        // ---- layout -----------------------------------------------------------------------------------------------------------
        private void Build()
        {
            AutomationProperties.SetAutomationId(_root, Id + "_Root");
            AutomationProperties.SetLiveSetting(_message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            AutomationProperties.SetAutomationId(_message, Id + "_Message_Text");
            _root.Children.Add(_message);
            _root.Children.Add(_rows);
            AutomationProperties.SetAutomationId(_rows, Id + "_Status_List");

            BuildHome();
            BuildSettings();
            BuildShortcuts();
            _root.Children.Add(_home);
            _root.Children.Add(_settingsPage);
            _root.Children.Add(_shortcutsPage);
            _message.Text = T("Looking at Playnite, the TV and streaming...", "Controllo Playnite, la TV e lo streaming...");
        }

        private Button Big(string id, string title, string subtitle, Func<Task> run)
        {
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = title, FontSize = _large ? 26 : 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = subtitle, FontSize = _large ? 15 : 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
            var button = new Button
            {
                Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                MinHeight = _large ? 112 : 84, Padding = new Thickness(16, 10, 16, 10), UseSystemFocusVisuals = true
            };
            AutomationProperties.SetAutomationId(button, Id + "_" + id + "_Button");
            AutomationProperties.SetName(button, title + ". " + subtitle);
            button.Click += async (_, _) => await run();
            return button;
        }

        private Button Plain(string id, string label, Func<Task> run)
        {
            var b = new Button { Content = label, MinHeight = _large ? 56 : 40, Padding = new Thickness(14, 6, 14, 6) };
            AutomationProperties.SetAutomationId(b, Id + "_" + id + "_Button");
            b.Click += async (_, _) => await run();
            return b;
        }

        private static TextBlock Note(string text, double opacity = 0.75) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity };

        private void BuildHome()
        {
            // Setup (only shown when Playnite is missing or skipped): Detect again / Locate / Install with PaRuX / Skip.
            _setup.Children.Add(new TextBlock { Text = T("Playnite is not set up yet", "Playnite non è ancora configurato"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            _setup.Children.Add(Note(T("Pick one: look again, show PaRuX where your Playnite is, let PaRuX install the official release for you only, or skip Playnite.",
                                       "Scegli: cerca di nuovo, indica a PaRuX dov'è il tuo Playnite, lascia che PaRuX installi la versione ufficiale solo per te, oppure salta Playnite.")));
            _setup.Children.Add(Plain("Detect", T("Look again", "Cerca di nuovo"), () => RefreshAsync()));
            _path = new TextBox { PlaceholderText = T(@"Folder of Playnite, e.g. D:\Apps\Playnite", @"Cartella di Playnite, es. D:\Apps\Playnite"), MinHeight = 40 };
            AutomationProperties.SetAutomationId(_path, Id + "_LocatePath_TextBox");
            _setup.Children.Add(_path);
            var locateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            locateRow.Children.Add(Plain("Browse", T("Choose the folder...", "Scegli la cartella..."), BrowseAsync));
            locateRow.Children.Add(Plain("Locate", T("Use this Playnite", "Usa questo Playnite"), LocateAsync));
            _setup.Children.Add(locateRow);
            _setup.Children.Add(Plain("Install", T("Install Playnite (official release)", "Installa Playnite (versione ufficiale)"), InstallPlanAsync));
            _installText = Note(string.Empty);
            _installText.Visibility = Visibility.Collapsed;
            _setup.Children.Add(_installText);
            _installConfirm = Plain("InstallConfirm", T("Confirm and install for my user", "Conferma e installa per il mio utente"), InstallConfirmAsync);
            _installConfirm.Visibility = Visibility.Collapsed;
            _setup.Children.Add(_installConfirm);
            _skip = Plain("Skip", T("Skip Playnite", "Salta Playnite"), ToggleSkipAsync);
            _setup.Children.Add(_skip);
            _home.Children.Add(_setup);

            _console = Big("Console", T("Console", "Console"), T("Playnite Fullscreen on your TV", "Playnite a schermo intero sulla TV"), () => LaunchAsync(PlayniteModes.Console, false));
            _desktop = Big("Desktop", T("Desktop", "Desktop"), T("Playnite in a normal window", "Playnite in una finestra normale"), () => LaunchAsync(PlayniteModes.Desktop, false));
            _streaming = Big("Streaming", T("Streaming", "Streaming"), T("Sunshine plus the display plan", "Sunshine più il piano degli schermi"), () => StreamingAsync(true, false));
            _settingsButton = Big("Settings", T("Settings", "Impostazioni"), T("Choose how Console Mode starts", "Scegli come parte la modalità Console"), () => { Show(Page.Settings); return Task.CompletedTask; });
            var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            void Place(Button b, int row, int col) { Grid.SetRow(b, row); Grid.SetColumn(b, col); grid.Children.Add(b); }
            Place(_console, 0, 0); Place(_desktop, 0, 1); Place(_streaming, 1, 0); Place(_settingsButton, 1, 1);
            _home.Children.Add(grid);
            _switch = Plain("Switch", T("Switch Playnite between Console and Desktop", "Passa Playnite tra Console e Desktop"), SwitchAsync);
            _switch.Visibility = Visibility.Collapsed;
            _home.Children.Add(_switch);
            _stopStreaming = Plain("StopStreaming", T("Stop streaming and restore the display", "Ferma lo streaming e ripristina lo schermo"), () => StreamingAsync(false, false));
            _stopStreaming.Visibility = Visibility.Collapsed;
            _home.Children.Add(_stopStreaming);
        }

        private void BuildSettings()
        {
            _settingsPage.Children.Add(Plain("Back", T("< Back", "< Indietro"), () => { if (_settingsOnly) _closeDialog(); else Show(Page.Home); return Task.CompletedTask; }));
            _settingsPage.Children.Add(new TextBlock { Text = T("Settings", "Impostazioni"), FontSize = _large ? 24 : 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            _settingsPage.Children.Add(Note(T("Console Mode starts Playnite in Console (Fullscreen) or Desktop. PaRuX never changes Playnite, its themes or your library.",
                                              "La modalità Console avvia Playnite in Console (Fullscreen) o Desktop. PaRuX non modifica Playnite, i suoi temi o la tua libreria.")));
            _startMode = new ComboBox { Header = T("When PaRuX starts Playnite, open it as", "Quando PaRuX avvia Playnite, aprilo come"), MinWidth = 300, MinHeight = 40 };
            _startMode.Items.Add(new ComboBoxItem { Content = T("Desktop", "Desktop"), Tag = PlayniteModes.Desktop });
            _startMode.Items.Add(new ComboBoxItem { Content = T("Console (Fullscreen)", "Console (Fullscreen)"), Tag = PlayniteModes.Console });
            AutomationProperties.SetAutomationId(_startMode, Id + "_StartMode_ComboBox");
            _startMode.SelectionChanged += async (_, _) => await SaveAsync();
            _settingsPage.Children.Add(_startMode);
            _onlyTv = Toggle("OnlyWhenTv", T("Open Console only when my TV is connected", "Apri Console solo quando la mia TV è collegata"));
            _fallback = new ComboBox { Header = T("If my TV is not connected", "Se la mia TV non è collegata"), MinWidth = 300, MinHeight = 40 };
            _fallback.Items.Add(new ComboBoxItem { Content = T("Open Desktop normally", "Apri Desktop normalmente"), Tag = ConsoleStartupPolicy.FallbackNormal });
            _fallback.Items.Add(new ComboBoxItem { Content = T("Open Desktop minimized", "Apri Desktop ridotto a icona"), Tag = ConsoleStartupPolicy.FallbackMinimized });
            AutomationProperties.SetAutomationId(_fallback, Id + "_Fallback_ComboBox");
            _fallback.SelectionChanged += async (_, _) => await SaveAsync();
            _settingsPage.Children.Add(_fallback);
            _startup = Toggle("StartWithPaRuX", T("Start Playnite when PaRuX starts", "Avvia Playnite quando si avvia PaRuX"));
            _return = Toggle("ReturnToPaRuX", T("Come back to PaRuX when Playnite (started by PaRuX) closes", "Torna a PaRuX quando Playnite (avviato da PaRuX) si chiude"));
            _yield = Toggle("AiYield", T("Free the graphics card and stop PaRuX AI while a Playnite game runs", "Libera la scheda video e ferma l'IA di PaRuX mentre un gioco di Playnite è in corso"));
            _settingsPage.Children.Add(Note(T("Which screen is your TV, and what the controller buttons do, are chosen in PaRuX Settings > Gaming > Console / TV and Controller buttons.",
                                              "Quale schermo è la tua TV, e cosa fanno i pulsanti del controller, si sceglie in Impostazioni PaRuX > Gaming > Console / TV e Pulsanti del controller.")));
            _settingsPage.Children.Add(Plain("Shortcuts", T("Shortcuts and taskbar menu (optional)...", "Collegamenti e menu della barra (facoltativo)..."), () => { Show(Page.Shortcuts); return Task.CompletedTask; }));
        }

        private ToggleSwitch Toggle(string id, string header)
        {
            var t = new ToggleSwitch { Header = header, OnContent = T("On", "Acceso"), OffContent = T("Off", "Spento"), MinHeight = 48 };
            AutomationProperties.SetAutomationId(t, Id + "_" + id + "_Toggle");
            t.Toggled += async (_, _) => await SaveAsync();
            _settingsPage.Children.Add(t);
            return t;
        }

        private void BuildShortcuts()
        {
            _shortcutsPage.Children.Add(Plain("ShortcutsBack", T("< Back", "< Indietro"), () => { Show(Page.Settings); return Task.CompletedTask; }));
            _shortcutsPage.Children.Add(new TextBlock { Text = T("Shortcuts and taskbar menu", "Collegamenti e menu della barra"), FontSize = _large ? 24 : 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            _shortcutsPage.Children.Add(Note(T("Nothing is created until you press the button. PaRuX never replaces a shortcut you already have: if a name is taken it uses the next free one. You can remove exactly what PaRuX created from here. Windows does not let an app pin itself to the taskbar, so the taskbar menu adds these actions to PaRuX's taskbar icon right-click menu instead.",
                                               "Non viene creato nulla finché non premi il pulsante. PaRuX non sostituisce mai un collegamento che hai già: se il nome è occupato usa il successivo libero. Da qui puoi rimuovere esattamente ciò che PaRuX ha creato. Windows non permette a un'app di fissarsi alla barra, quindi il menu della barra aggiunge queste azioni al menu del tasto destro sull'icona di PaRuX.")));
            var specs = PlayniteLauncherShortcuts.Specs;
            _shortcutBoxes = specs.Select(s => new CheckBox { Content = s.Name, IsChecked = true, MinHeight = 40, Tag = s.Action }).ToArray();
            foreach (CheckBox box in _shortcutBoxes)
            {
                AutomationProperties.SetAutomationId(box, Id + "_Shortcut_" + box.Tag + "_CheckBox");
                box.Checked += async (_, _) => await PreviewShortcutsAsync();
                box.Unchecked += async (_, _) => await PreviewShortcutsAsync();
                _shortcutsPage.Children.Add(box);
            }
            _desktopBox = new CheckBox { Content = T("Put them on my desktop", "Mettili sul mio desktop"), IsChecked = true, MinHeight = 40 };
            _jumpBox = new CheckBox { Content = T("Add them to PaRuX's taskbar icon menu", "Aggiungili al menu dell'icona di PaRuX nella barra"), IsChecked = false, MinHeight = 40 };
            AutomationProperties.SetAutomationId(_desktopBox, Id + "_DesktopTarget_CheckBox");
            AutomationProperties.SetAutomationId(_jumpBox, Id + "_JumpListTarget_CheckBox");
            _desktopBox.Checked += async (_, _) => await PreviewShortcutsAsync();
            _desktopBox.Unchecked += async (_, _) => await PreviewShortcutsAsync();
            _shortcutsPage.Children.Add(_desktopBox);
            _shortcutsPage.Children.Add(_jumpBox);
            _shortcutPreview = Note(string.Empty, 0.85);
            AutomationProperties.SetAutomationId(_shortcutPreview, Id + "_ShortcutPreview_Text");
            _shortcutsPage.Children.Add(_shortcutPreview);
            _shortcutsApply = Plain("ShortcutsCreate", T("Create what is ticked", "Crea ciò che è selezionato"), CreateShortcutsAsync);
            _shortcutsRemove = Plain("ShortcutsRemove", T("Remove what PaRuX created", "Rimuovi ciò che PaRuX ha creato"), RemoveShortcutsAsync);
            _shortcutsPage.Children.Add(_shortcutsApply);
            _shortcutsPage.Children.Add(_shortcutsRemove);
        }

        // ---- pages ------------------------------------------------------------------------------------------------------------
        private void Show(Page page)
        {
            _page = page;
            _home.Visibility = page == Page.Home ? Visibility.Visible : Visibility.Collapsed;
            _settingsPage.Visibility = page == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
            _shortcutsPage.Visibility = page == Page.Shortcuts ? Visibility.Visible : Visibility.Collapsed;
            _rows.Visibility = page == Page.Home ? Visibility.Visible : Visibility.Collapsed;
            _titleBack(IsOnSubPage);
            if (page == Page.Shortcuts) _ = PreviewShortcutsAsync();
            // The pad (or keyboard) must land on something sensible on the new page.
            _queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                StackPanel target = page == Page.Home ? _home : page == Page.Settings ? _settingsPage : _shortcutsPage;
                (FocusManager.FindFirstFocusableElement(target) as Control)?.Focus(FocusState.Programmatic);
            });
            if (page == Page.Home) _ = RefreshAsync();
        }

        private void SetMessage(string text) { if (!_disposed) _message.Text = text; }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            foreach (Button b in new[] { _console, _desktop, _streaming, _switch, _stopStreaming }) b.IsEnabled = !busy && ActionEnabled(b);
        }

        private bool ActionEnabled(Button b)
        {
            bool playnite = _status?.Component is { Detected: true, Skipped: false };
            return ReferenceEquals(b, _console) || ReferenceEquals(b, _desktop) || ReferenceEquals(b, _switch) ? playnite : true;
        }

        // ---- status -----------------------------------------------------------------------------------------------------------
        private async Task RefreshAsync()
        {
            if (_disposed) return;
            try
            {
                var status = await PlayniteClient.Current.GetStatusAsync(_lifetime.Token);
                if (_disposed) return;
                if (!status.Succeeded || status.Value is null)
                {
                    SetMessage(T("PaRuX could not read the Playnite state: ", "PaRuX non ha potuto leggere lo stato di Playnite: ") + status.Error);
                    return;
                }
                _status = status.Value;
                // Streaming state is read only; a slower read of the TV runs every few ticks so the list stays calm.
                var s = await PlayniteClient.Current.GetStreamingStatusAsync(_lifetime.Token);
                _streamingState = s.Succeeded && s.Value.ValueKind == JsonValueKind.Object &&
                    (s.Value.TryGetProperty("state", out JsonElement st) || s.Value.TryGetProperty("State", out st)) ? st.GetString() ?? string.Empty : string.Empty;
                if (_tick++ % 4 == 0) _tvState = await ReadTvAsync();
                if (_disposed) return;
                Render();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Write("Playnite launcher refresh failed: " + ex.GetType().Name); }
        }

        private async Task<string> ReadTvAsync()
        {
            string targetId = SettingsStore.Current.Data.ConsoleTargetDisplayId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetId)) return "none";
            var topology = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync(_lifetime.Token);
            if (!topology.Succeeded) return "unknown";
            return ConsoleDisplaySelectionPolicy.SelectTarget(topology.Value, SettingsStore.Current.Data.ConsoleDisplays, targetId) is not null ? "connected" : "absent";
        }

        private void Row(string key, string state, string label, string value)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var color = state switch
            {
                "ok" => Windows.UI.Color.FromArgb(255, 0x2E, 0x9D, 0x4F),
                "warn" => Windows.UI.Color.FromArgb(255, 0xD9, 0xA2, 0x00),
                _ => Windows.UI.Color.FromArgb(255, 0x9A, 0x9A, 0x9A)
            };
            row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MinWidth = _large ? 200 : 170 });
            row.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
            AutomationProperties.SetAutomationId(row, Id + "_Row_" + key);
            AutomationProperties.SetName(row, label + ": " + value);
            _rows.Children.Add(row);
        }

        private void Render()
        {
            PlayniteStatusDto st = _status!;
            PlayniteComponentDto c = st.Component;
            _rows.Children.Clear();
            string found = c.Skipped ? T("skipped by you", "saltato da te")
                : !c.Detected ? T("not found", "non trovato")
                : (c.Version is { Length: > 0 } v ? v + " " : string.Empty) + (c.Running ? T("running", "in esecuzione") : T("ready", "pronto"));
            Row("Playnite", c.Detected && !c.Skipped ? "ok" : c.Skipped ? "off" : "warn", "Playnite", found);
            PlayniteSessionDto se = st.Session;
            Row("Game", se.State is "yielded" or "yielding" ? "ok" : se.State == "failed" ? "warn" : "off", T("Game session", "Sessione di gioco"), SessionText(se));
            Row("Tv", _tvState == "connected" ? "ok" : _tvState == "absent" ? "warn" : "off", T("TV for Console", "TV per Console"), _tvState switch
            {
                "connected" => T("connected", "collegata"),
                "absent" => T("chosen but not connected", "scelta ma non collegata"),
                "unknown" => T("could not read the displays", "impossibile leggere gli schermi"),
                _ => T("not chosen (Settings > Gaming > Console / TV)", "non scelta (Impostazioni > Gaming > Console / TV)")
            });
            bool streamOn = _streamingState is "active" or "Active" or "running" or "Running" or "started";
            Row("Streaming", streamOn ? "ok" : "off", T("Streaming", "Streaming"), string.IsNullOrEmpty(_streamingState) ? T("not available", "non disponibile") : _streamingState);
            bool padOn = SettingsStore.Current.Data.PadActions?.Enabled ?? true;
            Row("Pad", padOn ? "ok" : "off", T("Controller buttons", "Pulsanti del controller"), padOn ? T("on (any pad)", "attivi (qualsiasi pad)") : T("off", "spenti"));
            if (!string.IsNullOrEmpty(se.LastError)) Row("Error", "warn", T("Last problem", "Ultimo problema"), se.LastError);

            bool missing = !c.Detected || c.Skipped;
            _setup.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
            _skip.Content = c.Skipped ? T("Use Playnite again", "Usa di nuovo Playnite") : T("Skip Playnite", "Salta Playnite");
            _switch.Visibility = !missing && se.PaRuXLaunchedPlaynite ? Visibility.Visible : Visibility.Collapsed;
            _stopStreaming.Visibility = streamOn ? Visibility.Visible : Visibility.Collapsed;
            if (_message.Text.StartsWith(T("Looking at", "Controllo"), StringComparison.Ordinal))
                SetMessage(missing ? T("Playnite was not found. Look again, point PaRuX to your copy, install the official release, or skip it.",
                                       "Playnite non è stato trovato. Cerca di nuovo, indica la tua copia, installa la versione ufficiale o salta.")
                                   : T("Ready. Pick Console, Desktop, Streaming or Settings.", "Pronto. Scegli Console, Desktop, Streaming o Impostazioni."));
            SetBusy(_busy);

            _applying = true;
            try
            {
                _yield.IsOn = st.Settings.AiYieldEnabled;
                _onlyTv.IsOn = st.Settings.ConsoleOnlyWhenTargetConnected;
                _return.IsOn = st.Settings.ReturnToPaRuXOnClose;
                _startup.IsOn = st.Settings.LaunchAtStartup;
                Select(_startMode, st.Settings.StartMode);
                Select(_fallback, st.Settings.FallbackWithoutDisplay);
            }
            finally { _applying = false; }
        }

        private static void Select(ComboBox box, string tag)
        {
            foreach (object item in box.Items)
                if (item is ComboBoxItem { Tag: string t } ci && t == tag) { if (!ReferenceEquals(box.SelectedItem, ci)) box.SelectedItem = ci; return; }
        }

        private static string SessionText(PlayniteSessionDto s) => s.State switch
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

        // ---- actions ----------------------------------------------------------------------------------------------------------
        private async Task LaunchAsync(string mode, bool fromRequest)
        {
            if (_busy) return;
            Show(Page.Home);
            if (_status is null) await RefreshAsync();
            if (_status?.Component is not { Detected: true, Skipped: false })
            {
                SetMessage(T("Playnite was not found, so it cannot be opened. Use the options below to locate or install it, or skip it.",
                             "Playnite non è stato trovato, quindi non può essere aperto. Usa le opzioni qui sotto per indicarlo, installarlo o saltarlo."));
                return;
            }
            SetBusy(true);
            SetMessage(mode == PlayniteModes.Console ? T("Starting Playnite in Console mode...", "Avvio Playnite in modalità Console...") : T("Starting Playnite in Desktop mode...", "Avvio Playnite in modalità Desktop..."));
            try
            {
                long? beforeCounter = _status?.Session.PaRuXLaunchedClosedCounter;
                bool wasOwned = _status?.Session.PaRuXLaunchedPlaynite == true;
                var result = await PlayniteClient.Current.LaunchAsync(mode, MainWindow.PlayniteDisplayId(), _lifetime.Token, explicitRequest: true);
                bool ok = result.Succeeded && result.Value is { Ok: true };
                SetMessage(result.Error ?? result.Value?.Message ?? (ok ? T("Playnite started.", "Playnite avviato.") : T("Playnite was not started.", "Playnite non è stato avviato.")));
                if (ok && (result.Value?.Outcome == "started" || wasOwned))
                    App.MainWin?.BeginPlayniteReturnWatch(beforeCounter);
                if (ok && fromRequest) { await Task.Delay(1500, _lifetime.Token); _closeDialog(); }
            }
            catch (OperationCanceledException) { }
            finally { SetBusy(false); }
            await RefreshAsync();
        }

        private async Task SwitchAsync()
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                long? beforeCounter = _status?.Session.PaRuXLaunchedClosedCounter;
                var result = await PlayniteClient.Current.SwitchAsync(MainWindow.PlayniteDisplayId(), _lifetime.Token);
                SetMessage(result.Error ?? result.Value?.Message ?? T("Switched.", "Cambiato."));
                if (result.Succeeded && result.Value is { Ok: true })
                    App.MainWin?.BeginPlayniteReturnWatch(beforeCounter);
            }
            catch (OperationCanceledException) { }
            finally { SetBusy(false); }
            await RefreshAsync();
        }

        private async Task StreamingAsync(bool start, bool fromRequest)
        {
            if (_busy) return;
            Show(Page.Home);
            SetBusy(true);
            SetMessage(start ? T("Starting streaming mode...", "Avvio la modalità streaming...") : T("Stopping streaming mode and restoring the display...", "Fermo la modalità streaming e ripristino lo schermo..."));
            try
            {
                var result = start ? await PlayniteClient.Current.StartStreamingAsync(allowStartSunshine: false, _lifetime.Token)
                                   : await PlayniteClient.Current.StopStreamingAsync(_lifetime.Token);
                string text = result.Error ?? T("Streaming mode updated.", "Modalità streaming aggiornata.");
                if (result.Succeeded && result.Value.ValueKind == JsonValueKind.Object && result.Value.TryGetProperty("message", out JsonElement m) && m.GetString() is { Length: > 0 } msg) text = msg;
                SetMessage(text);
            }
            catch (OperationCanceledException) { }
            finally { SetBusy(false); }
            await RefreshAsync();
        }

        private async Task ToggleSkipAsync()
        {
            bool skip = _status?.Component.Skipped != true;
            var result = await PlayniteClient.Current.SkipAsync(skip, _lifetime.Token);
            SetMessage(result.Succeeded ? (skip ? T("Playnite is skipped. You can use it again at any time.", "Playnite è saltato. Puoi riusarlo in qualsiasi momento.") : T("Playnite is back on.", "Playnite è di nuovo attivo."))
                                       : result.Error ?? string.Empty);
            await RefreshAsync();
        }

        private async Task LocateAsync()
        {
            string path = _path.Text?.Trim() ?? string.Empty;
            if (path.Length == 0) { SetMessage(T("Type or choose the Playnite folder first.", "Scrivi o scegli prima la cartella di Playnite.")); return; }
            var result = await PlayniteClient.Current.LocateAsync(path, _lifetime.Token);
            SetMessage(result.Succeeded ? T("Your Playnite was found. PaRuX will only read and start it.", "Il tuo Playnite è stato trovato. PaRuX lo leggerà e avvierà soltanto.") : result.Error ?? string.Empty);
            await RefreshAsync();
        }

        private async Task BrowseAsync()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder is not null) _path.Text = folder.Path;
            }
            catch (Exception ex) { SetMessage(T("The folder picker could not open: ", "Il selettore cartelle non si è aperto: ") + ex.Message); }
        }

        private async Task InstallPlanAsync()
        {
            SetMessage(T("Checking the latest official Playnite release...", "Controllo l'ultima versione ufficiale di Playnite..."));
            (PlayniteInstallPlanDto? plan, string text) = PlayniteSetupFlow.Describe(await PlayniteClient.Current.GetInstallPlanAsync(_lifetime.Token));
            _plan = plan;
            _installText.Text = text;
            _installText.Visibility = Visibility.Visible;
            _installConfirm.Visibility = plan is null ? Visibility.Collapsed : Visibility.Visible;
            SetMessage(string.Empty);
        }

        private async Task InstallConfirmAsync()
        {
            if (_plan is not { Available: true } plan) return;
            _installConfirm.Visibility = Visibility.Collapsed;
            SetMessage(await PlayniteSetupFlow.InstallAsync(plan, SetMessage));
            await RefreshAsync();
        }

        private async Task SaveAsync()
        {
            if (_applying || _status is null || _startMode is null) return;
            var s = new PlayniteSettingsDto
            {
                AiYieldEnabled = _yield.IsOn,
                StartMode = (_startMode.SelectedItem as ComboBoxItem)?.Tag as string ?? PlayniteModes.Desktop,
                TargetDisplayId = _status.Settings.TargetDisplayId,
                ConsoleOnlyWhenTargetConnected = _onlyTv.IsOn,
                FallbackWithoutDisplay = (_fallback.SelectedItem as ComboBoxItem)?.Tag as string ?? ConsoleStartupPolicy.FallbackNormal,
                ReturnToPaRuXOnClose = _return.IsOn,
                LaunchAtStartup = _startup.IsOn,
                ConsoleStartsPlaynite = _status.Settings.ConsoleStartsPlaynite   // set in Settings > Console mode; never reset here
            };
            var saved = await PlayniteClient.Current.SaveSettingsAsync(s, _lifetime.Token);
            SetMessage(saved.Succeeded ? T("Saved.", "Salvato.") : T("Not saved: ", "Non salvato: ") + saved.Error);
            await RefreshAsync();
        }

        // ---- shortcuts --------------------------------------------------------------------------------------------------------
        private IEnumerable<PlayniteLauncherAction> TickedActions() =>
            _shortcutBoxes.Where(b => b.IsChecked == true).Select(b => (PlayniteLauncherAction)b.Tag);

        private Task PreviewShortcutsAsync()
        {
            string? exe = PlayniteLauncherShortcutService.ExecutableOrReason(out string? reason);
            if (exe is null)
            {
                _shortcutPreview.Text = reason ?? string.Empty;
                _shortcutsApply.IsEnabled = _shortcutsRemove.IsEnabled = false;
                return Task.CompletedTask;
            }
            var lines = new List<string>();
            _plannedShortcuts = Array.Empty<PlannedShortcut>();
            if (_desktopBox.IsChecked == true)
            {
                _plannedShortcuts = PlayniteLauncherShortcutService.Plan(PlayniteLauncherShortcutService.DesktopFolder, exe, TickedActions());
                foreach (PlannedShortcut p in _plannedShortcuts)
                    lines.Add((p.State == PlannedShortcutState.Create ? T("Will create: ", "Verrà creato: ") : T("Already there: ", "Già presente: ")) + p.Path);
            }
            if (_jumpBox.IsChecked == true) lines.Add(T("Will add to the taskbar menu: ", "Verrà aggiunto al menu della barra: ") + string.Join(", ", TickedActions().Select(NameOf)));
            _plannedRemovals = PlayniteLauncherShortcutService.PlanRemoval(PlayniteLauncherShortcutService.DesktopFolder, exe);
            lines.Add(_plannedRemovals.Count == 0 ? T("PaRuX found no launcher shortcuts of its own on your desktop.", "PaRuX non ha trovato suoi collegamenti del launcher sul desktop.")
                : T("PaRuX launcher shortcuts found on the desktop (removal touches only these): ", "Collegamenti del launcher di PaRuX sul desktop (la rimozione tocca solo questi): ") + string.Join(", ", _plannedRemovals.Select(System.IO.Path.GetFileName)));
            _shortcutPreview.Text = string.Join(Environment.NewLine, lines);
            _shortcutsApply.IsEnabled = _plannedShortcuts.Any(p => p.State == PlannedShortcutState.Create) || (_jumpBox.IsChecked == true && TickedActions().Any());
            _shortcutsRemove.IsEnabled = true;
            return Task.CompletedTask;
        }

        private static string NameOf(PlayniteLauncherAction a) => PlayniteLauncherShortcuts.Specs.First(s => s.Action == a).Name;

        private async Task CreateShortcutsAsync()
        {
            string? exe = PlayniteLauncherShortcutService.ExecutableOrReason(out string? reason);
            if (exe is null) { SetMessage(reason ?? string.Empty); return; }
            var done = new List<string>();
            if (_desktopBox.IsChecked == true)
            {
                var results = PlayniteLauncherShortcutService.Create(_plannedShortcuts, exe);
                done.Add(T("Desktop: ", "Desktop: ") + results.Count(r => r.Ok && r.Message == "created") + T(" created, ", " creati, ") + results.Count(r => !r.Ok) + T(" problem(s)", " problema/i"));
            }
            if (_jumpBox.IsChecked == true)
            {
                string? err = PlayniteJumpList.TryApply(exe, PlayniteLauncherShortcuts.Specs.Where(s => TickedActions().Contains(s.Action)).ToArray());
                done.Add(err ?? T("Taskbar menu updated.", "Menu della barra aggiornato."));
            }
            SetMessage(string.Join(" ", done));
            await PreviewShortcutsAsync();
        }

        private async Task RemoveShortcutsAsync()
        {
            string? exe = PlayniteLauncherShortcutService.ExecutableOrReason(out _);
            var done = new List<string>();
            if (exe is not null && _plannedRemovals.Count > 0)
            {
                var results = PlayniteLauncherShortcutService.Remove(_plannedRemovals);
                done.Add(T("Removed ", "Rimossi ") + results.Count(r => r.Ok) + T(" shortcut(s).", " collegamento/i."));
            }
            string? jump = PlayniteJumpList.TryRemove();
            done.Add(jump ?? T("Taskbar menu cleared.", "Menu della barra svuotato."));
            SetMessage(string.Join(" ", done));
            await PreviewShortcutsAsync();
        }
    }
}
