using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views;

public sealed partial class ManualGameLibraryView : UserControl
{
    private ScrollViewer? _pageScroll;
    private readonly TextBox _name = new();
    private readonly TextBlock _manualAddIntro = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _manualAddActions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBox _path = new();
    private readonly TextBox _arguments = new();
    private readonly TextBox _workingDirectory = new();
    private readonly ComboBox _kind = new() { MinWidth = 220 };
    private readonly StackPanel _games = new() { Spacing = 8 };
    private readonly StackPanel _root = new() { Spacing = 16, Padding = new Thickness(24), MaxWidth = 1500, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _heading = new();
    private bool _consolePresentation;
    private SelectorBar? _sectionBar;
    private readonly StackPanel _discoveredGames = new() { Spacing = 8 };
    private readonly TextBlock _discoveryStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _steamRefresh = new();
    private readonly Button _epicRefresh = new();
    private readonly Button _gogRefresh = new();
    private readonly Button _allStoresRefresh = new();
    // First-visit voluntary local store scan; no disk scan on app startup.
    private readonly Border _storeDiscoveryOffer = new()
    {
        Visibility = Visibility.Collapsed, Padding = new Thickness(16),
        CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1)
    };
    private readonly TextBlock _storeDiscoveryOfferText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _storeDiscoveryOfferScan = new() { MinHeight = 44 };
    private readonly Button _storeDiscoveryOfferDismiss = new() { MinHeight = 44 };
    private readonly Button _displayRefresh = new();
    private readonly Button _controllerRefresh = new();
    private readonly Button _returnToAi = new();
    // Compact strip above every section: shown only while Gaming mode is active, so Return to AI is one action away from any section.
    private readonly Border _gamingStrip = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(12, 4, 12, 4), CornerRadius = new CornerRadius(8) };
    private readonly TextBlock _gamingStripText = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _gamingStripReturn = new();
    private readonly TextBlock _sessionHeading = new();
    private readonly StackPanel _installerSection = new() { Spacing = 8 };
    private readonly Button _inspectInstaller = new();
    private readonly TextBox _expectedPublisherSha = new();
    private readonly Button _stagePackage = new();
    private readonly Button _refreshStaged = new();
    private readonly StackPanel _stagedPackages = new() { Spacing = 6 };
    private readonly TextBlock _stagedStatus = new() { TextWrapping = TextWrapping.Wrap };
    private string? _inspectedInstallerPath;
    private readonly Button _cancelInspection = new();
    private CancellationTokenSource? _inspectionCancellation;
    private readonly TextBlock _installerStatus = new() { TextWrapping = TextWrapping.Wrap };
    private bool _installerInspectionAllowed;
    private readonly Button _gamingStatusRefresh = new();
    private readonly TextBlock _gamingStatus = new() { TextWrapping = TextWrapping.Wrap };
    private bool _returnToAiAvailable;
    // An unreachable Core may temporarily disable actions, but must not erase the
    // last confirmed Gaming observation needed for foreground-return reconciliation.
    private bool _lastConfirmedGamingActive;
    private bool _consoleForegroundRefreshInFlight;
    private readonly TextBlock _controllerStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _displayStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _displayItems = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _add = new();
    private readonly Button _browse = new();
    private bool _busy;
    private readonly TextBox _filter = new();
    /// <summary>On-demand, manually confirmed, no auto-send Game-to-Chat handoff.</summary>
    public event Func<string, IReadOnlyList<string>, bool>? GameGuideChatRequested;
    private UniversalLibraryPanel? _universal;
    private readonly GameDownloadsPanel _downloads = new();
    private readonly ComboBox _storeFilter = new() { MinWidth = 180 };
    private readonly ComboBox _stateFilter = new() { MinWidth = 180 };
    private List<ManualGameDto> _allGames = new();
    private List<DiscoveredGameDto> _lastDetectedGames = new();
    private int _page;
    private int _detectedPage;
    private GameLibraryPageWindow _lastDetectedWindow;
    private readonly Button _previous = new();
    private readonly Button _next = new();
    private readonly Button _detectedPrevious = new();
    private readonly Button _detectedNext = new();
    private readonly TextBlock _detectedRange = new() { TextWrapping = TextWrapping.Wrap };
    private static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    private static string T(string en, string it) => Italian ? it : en;

    private static string GamingStateLabel(string state) => state switch
    {
        "AiReady" => T("AI ready", "AI pronta"),
        "EnteringGaming" => T("Preparing Gaming", "Preparazione Gaming"),
        "GamingLocal" => T("Local Gaming", "Gioco locale"),
        "GamingRemote" => T("Remote Gaming", "Gioco remoto"),
        "ExitingGaming" => T("Leaving Gaming", "Uscita da Gaming"),
        "RestoringAi" => T("Restoring AI", "Ripristino AI"),
        "GameSuspended" => T("Game suspended", "Gioco sospeso"),
        "Error" => T("Recovery required", "Recupero necessario"),
        _ => T("Unknown", "Sconosciuto")
    };

    public ManualGameLibraryView()
    {
        var root = _root;
        _heading.Text = T("Games", "Giochi");
        _heading.FontSize = 28;
        _heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        root.Children.Add(_heading);
        BuildStoreDiscoveryOffer(root);

        // The Gaming home (Continue shelf, tile library, detail panel) opens first; it also hosts the opt-in advanced section.
        _universal = new UniversalLibraryPanel(LaunchInstallationAsync, LoadAsync);
        _universal.GameGuideChatRequested += (question, sources) =>
            GameGuideChatRequested?.Invoke(question, sources) == true;
        root.Children.Add(_universal);

        // Real resource/session status lives on the same Games home in both TV and
        // desktop presentations. No background polling or controller worker.
        int sessionStart = root.Children.Count;
        _sessionHeading.Text = T("AI / Gaming session", "Sessione AI / Gaming");
        _sessionHeading.FontSize = 20;
        _sessionHeading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        root.Children.Add(_sessionHeading);
        _gamingStatus.Text = T("Check the Gaming status to begin.", "Controlla lo stato Gaming per iniziare.");
        AutomationProperties.SetAutomationId(_gamingStatus, "GameLibrary_Gaming_Status_Text");
        AutomationProperties.SetLiveSetting(_gamingStatus, AutomationLiveSetting.Polite);
        root.Children.Add(_gamingStatus);
        _gamingStatusRefresh.Content = T("Refresh Gaming status", "Aggiorna stato Gaming");
        AutomationProperties.SetAutomationId(_gamingStatusRefresh, "GameLibrary_Gaming_Refresh_Button");
        _gamingStatusRefresh.Click += async (_, _) => await RefreshGamingStatusButtonAsync();
        _returnToAi.Content = T("Return to AI", "Torna all'AI");
        _returnToAi.IsEnabled = false;
        AutomationProperties.SetAutomationId(_returnToAi, "GameLibrary_ReturnToAi_Button");
        AutomationProperties.SetName(_returnToAi, T("Return to AI without closing the game", "Torna all'AI senza chiudere il gioco"));
        _returnToAi.Click += async (_, _) => await ReturnToAiAsync();
        var sessionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        sessionActions.Children.Add(_gamingStatusRefresh);
        sessionActions.Children.Add(_returnToAi);
        root.Children.Add(sessionActions);

        // Explicit, desktop-only downloads; never create a polling workload in the TV/Console presentation.
        int downloadsStart = root.Children.Count;
        root.Children.Add(_downloads);

        int addStart = root.Children.Count;
        _manualAddIntro.Text = T("Add local games and launch them only after explicit confirmation. PaRuX never installs them here.",
            "Aggiungi giochi locali e avviali solo dopo una conferma esplicita. PaRuX non li installa da questa schermata.");
        root.Children.Add(_manualAddIntro);
        _name.Header = T("Game name", "Nome del gioco");
        _name.MaxLength = 200;
        AutomationProperties.SetAutomationId(_name, "GameLibrary_Name_TextBox");
        _path.Header = T("Local executable (.exe)", "Eseguibile locale (.exe)");
        _path.MaxLength = 1024;
        AutomationProperties.SetAutomationId(_path, "GameLibrary_Path_TextBox");
        _browse.Content = T("Browse", "Sfoglia");
        AutomationProperties.SetAutomationId(_browse, "GameLibrary_Browse_Button");
        _browse.Click += Browse_Click;
        _add.Content = T("Add game", "Aggiungi gioco");
        AutomationProperties.SetAutomationId(_add, "GameLibrary_Add_Button");
        _add.Click += Add_Click;
        _kind.Header = T("Entry type", "Tipo di voce");
        _kind.Items.Add(new ComboBoxItem { Content = T("Game (prepares Gaming Mode)", "Gioco (prepara la modalità Gaming)"), Tag = ManualEntryKinds.Game });
        _kind.Items.Add(new ComboBoxItem { Content = T("Application / tool (started as-is)", "Applicazione / strumento (avviata così com'è)"), Tag = ManualEntryKinds.Application });
        _kind.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_kind, "GameLibrary_Kind_ComboBox");
        _arguments.Header = T("Arguments (optional)", "Argomenti (facoltativi)");
        _arguments.MaxLength = 1024;
        AutomationProperties.SetAutomationId(_arguments, "GameLibrary_Arguments_TextBox");
        _workingDirectory.Header = T("Working folder (optional)", "Cartella di lavoro (facoltativa)");
        _workingDirectory.MaxLength = 1024;
        AutomationProperties.SetAutomationId(_workingDirectory, "GameLibrary_WorkingFolder_TextBox");
        root.Children.Add(_name); root.Children.Add(_path);
        root.Children.Add(_kind); root.Children.Add(_arguments); root.Children.Add(_workingDirectory);
        _manualAddActions.Children.Add(_browse);
        _manualAddActions.Children.Add(_add);
        root.Children.Add(_manualAddActions);
        AutomationProperties.SetAutomationId(_status, "GameLibrary_Status_Text");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        root.Children.Add(_status);
        _filter.Header = T("Filter by name", "Filtra per nome");
        AutomationProperties.SetAutomationId(_filter, "GameLibrary_Filter_TextBox");
        _filter.TextChanged += (_, _) => { _page = 0; _detectedPage = 0; RenderGames(); RenderDiscoveredGames(); };
        // Console/TV: the window-level on-screen keyboard opens for this field (MainWindow.OnScreenKeyboard.cs).
        root.Children.Add(_filter);
        _storeFilter.Header = T("Store", "Negozio");
        _stateFilter.Header = T("Install state", "Stato installazione");
        foreach (var (value, label) in new[] { ("", T("All stores", "Tutti i negozi")), ("steam", "Steam"), ("epic", "Epic"), ("gog", "GOG") })
            _storeFilter.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        foreach (var (value, label) in new[]
        {
            ("", T("Any state", "Qualsiasi stato")), (GameInstallStates.Installed, T("Installed", "Installato")),
            (GameInstallStates.Updating, T("Updating", "In aggiornamento")), (GameInstallStates.Incomplete, T("Incomplete", "Incompleto")),
            (GameInstallStates.Unknown, T("Not reported", "Non riportato"))
        })
            _stateFilter.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _storeFilter.SelectedIndex = 0; _stateFilter.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_storeFilter, "GameLibrary_StoreFilter_ComboBox");
        AutomationProperties.SetAutomationId(_stateFilter, "GameLibrary_StateFilter_ComboBox");
        _storeFilter.SelectionChanged += (_, _) => { _detectedPage = 0; RenderDiscoveredGames(); };
        _stateFilter.SelectionChanged += (_, _) => { _detectedPage = 0; RenderDiscoveredGames(); };
        var storeFilters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        storeFilters.Children.Add(_storeFilter); storeFilters.Children.Add(_stateFilter);
        root.Children.Add(storeFilters);
        root.Children.Add(_games);
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _previous.Content = T("Previous", "Precedente"); _next.Content = T("Next", "Successiva");
        _previous.Click += (_, _) => { if (_page > 0) { _page--; RenderGames(); } };
        _next.Click += (_, _) => { _page++; RenderGames(); };
        pages.Children.Add(_previous); pages.Children.Add(_next); root.Children.Add(pages);

        root.Children.Add(new TextBlock
        {
            Text = T("Installed store games", "Giochi degli store installati"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = T("Steam, Epic and GOG detection reads local installation metadata. Official-client launch requests require explicit confirmation, and do not prove the game started. No store installs or modifies games.",
                "Il rilevamento Steam, Epic e GOG legge i dati locali di installazione. Le richieste di avvio ai client richiedono conferma esplicita e non dimostrano che il gioco sia partito. PaRuX non installa né modifica i giochi."),
            TextWrapping = TextWrapping.Wrap
        });
        _allStoresRefresh.Content = T("Detect all installed games", "Rileva tutti i giochi installati");
        AutomationProperties.SetAutomationId(_allStoresRefresh, "GameLibrary_AllStores_Refresh_Button");
        AutomationProperties.SetName(_allStoresRefresh, T("Detect installed Steam Epic and GOG games", "Rileva i giochi Steam Epic e GOG installati"));
        _allStoresRefresh.Click += async (_, _) => await RefreshAllStoresAsync();
        root.Children.Add(_allStoresRefresh);

        _steamRefresh.Content = T("Detect Steam games", "Rileva giochi Steam");
        AutomationProperties.SetAutomationId(_steamRefresh, "GameLibrary_Steam_Refresh_Button");
        _steamRefresh.Click += async (_, _) => await RefreshSteamAsync();
        root.Children.Add(_steamRefresh);
        _epicRefresh.Content = T("Detect Epic games", "Rileva giochi Epic");
        AutomationProperties.SetAutomationId(_epicRefresh, "GameLibrary_Epic_Refresh_Button");
        _epicRefresh.Click += async (_, _) => await RefreshEpicAsync();
        root.Children.Add(_epicRefresh);
        _gogRefresh.Content = T("Detect GOG games", "Rileva giochi GOG");
        AutomationProperties.SetAutomationId(_gogRefresh, "GameLibrary_Gog_Refresh_Button");
        AutomationProperties.SetName(_gogRefresh, T("Detect installed GOG games", "Rileva i giochi GOG installati"));
        _gogRefresh.Click += async (_, _) => await RefreshGogAsync();
        root.Children.Add(_gogRefresh);
        AutomationProperties.SetAutomationId(_discoveryStatus, "GameLibrary_Steam_Status_Text");
        AutomationProperties.SetLiveSetting(_discoveryStatus, AutomationLiveSetting.Polite);
        root.Children.Add(_discoveryStatus);
        root.Children.Add(_discoveredGames);
        // Console and desktop share the same cached catalog, with bounded
        // page rendering instead of constructing thousands of WinUI controls.
        _detectedPrevious.IsEnabled = false;
        _detectedNext.IsEnabled = false;
        _detectedPrevious.Content = T("Previous store games", "Giochi store precedenti");
        _detectedNext.Content = T("Next store games", "Giochi store successivi");
        AutomationProperties.SetAutomationId(_detectedPrevious, "GameLibrary_Store_Previous_Button");
        AutomationProperties.SetAutomationId(_detectedNext, "GameLibrary_Store_Next_Button");
        _detectedRange.Text = T("No store games scanned yet.", "Nessun gioco store ancora rilevato.");
        AutomationProperties.SetAutomationId(_detectedRange, "GameLibrary_Store_Page_Status");
        AutomationProperties.SetLiveSetting(_detectedRange, AutomationLiveSetting.Polite);
        _detectedPrevious.Click += (_, _) =>
        {
            if (!_busy && _detectedPage > 0) { _detectedPage--; RenderDiscoveredGames(); }
        };
        _detectedNext.Click += (_, _) =>
        {
            if (!_busy && _lastDetectedWindow.HasNext) { _detectedPage++; RenderDiscoveredGames(); }
        };
        var detectedNavigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        detectedNavigation.Children.Add(_detectedPrevious);
        detectedNavigation.Children.Add(_detectedNext);
        root.Children.Add(_detectedRange);
        root.Children.Add(detectedNavigation);

        int displayStart = root.Children.Count;
        root.Children.Add(new TextBlock
        {
            Text = T("Display & streaming readiness", "Preparazione schermi e streaming"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = T("Inspect the current Windows monitor layout. No display settings are changed.",
                     "Esamina la disposizione attuale dei monitor Windows. Nessuna impostazione video viene modificata."),
            TextWrapping = TextWrapping.Wrap
        });
        _displayRefresh.Content = T("Inspect connected displays", "Controlla gli schermi collegati");
        AutomationProperties.SetAutomationId(_displayRefresh, "GameLibrary_Displays_Refresh_Button");
        _displayRefresh.Click += async (_, _) => await InspectDisplaysAsync();
        root.Children.Add(_displayRefresh);
        AutomationProperties.SetAutomationId(_displayStatus, "GameLibrary_Displays_Status_Text");
        AutomationProperties.SetLiveSetting(_displayStatus, AutomationLiveSetting.Polite);
        root.Children.Add(_displayStatus);
        root.Children.Add(_displayItems);

        root.Children.Add(new TextBlock
        {
            Text = T("Controller diagnostics", "Diagnostica controller"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = T("Read connected XInput controllers only when requested. No drivers, mapping changes or persistent recording.",
                     "Legge i controller XInput solo su richiesta. Nessun driver, modifica delle mappature o registrazione continua."),
            TextWrapping = TextWrapping.Wrap
        });
        _controllerRefresh.Content = T("Check controllers", "Controlla i controller");
        AutomationProperties.SetAutomationId(_controllerRefresh, "GameLibrary_Controllers_Refresh_Button");
        _controllerRefresh.Click += async (_, _) => await InspectControllersAsync();
        root.Children.Add(_controllerRefresh);
        AutomationProperties.SetAutomationId(_controllerStatus, "GameLibrary_Controllers_Status_Text");
        AutomationProperties.SetLiveSetting(_controllerStatus, AutomationLiveSetting.Polite);
        root.Children.Add(_controllerStatus);
        root.Children.Add(new GamingDevicesPanel());

        _installerSection.Children.Add(new TextBlock
        {
            Text = T("Game installer safety preflight", "Controllo preliminare installer giochi"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        _installerSection.Children.Add(new TextBlock
        {
            Text = T("Select a completed local installer/archive and calculate SHA-256. This does not install, execute, extract, download or prove the file is safe. Disabled during Gaming.",
                "Seleziona un installer/archivio locale completo e calcola SHA-256. Non installa, esegue, estrae, scarica né dimostra la sicurezza del file. Disattivato durante il Gaming."),
            TextWrapping = TextWrapping.Wrap
        });
        _inspectInstaller.Content = T("Inspect local installer", "Controlla installer locale");
        _inspectInstaller.IsEnabled = false;
        AutomationProperties.SetAutomationId(_inspectInstaller, "GameLibrary_Installer_Inspect_Button");
        AutomationProperties.SetName(_inspectInstaller, T("Calculate SHA-256 of a selected game installer", "Calcola SHA-256 di un installer selezionato"));
        _inspectInstaller.Click += async (_, _) => await InspectInstallerAsync();
        var inspectionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        inspectionActions.Children.Add(_inspectInstaller);
        _cancelInspection.Content = T("Cancel inspection", "Annulla controllo");
        _cancelInspection.IsEnabled = false;
        AutomationProperties.SetAutomationId(_cancelInspection, "GameLibrary_Installer_Cancel_Button");
        _cancelInspection.Click += (_, _) =>
        {
            if (_inspectionCancellation is null) return;
            _inspectionCancellation.Cancel();
            _cancelInspection.IsEnabled = false;
            _installerStatus.Text = T("Cancelling inspection…", "Annullamento controllo…");
        };
        inspectionActions.Children.Add(_cancelInspection);
        _installerSection.Children.Add(inspectionActions);
        _installerSection.Children.Add(new TextBlock
        {
            Text = T("Optional: independently verify and stage a copy of the selected package. Paste the SHA-256 published by a trusted independent source. PaRuX never runs or installs the package.",
                     "Facoltativo: verifica e prepara una copia del pacchetto selezionato. Incolla lo SHA-256 pubblicato da una fonte attendibile indipendente. PaRuX non esegue né installa il pacchetto."),
            TextWrapping = TextWrapping.Wrap
        });
        _expectedPublisherSha.PlaceholderText = T("Publisher SHA-256 (64 hex characters)",
            "SHA-256 del produttore (64 caratteri esadecimali)");
        _expectedPublisherSha.MaxLength = 64;
        _expectedPublisherSha.IsEnabled = true;
        AutomationProperties.SetAutomationId(_expectedPublisherSha, "GameLibrary_Installer_PublisherHash_Input");
        AutomationProperties.SetName(_expectedPublisherSha, T("Independent publisher SHA-256",
            "SHA-256 indipendente del produttore"));
        _expectedPublisherSha.TextChanged += (_, _) => UpdatePackageStageAvailability();
        _installerSection.Children.Add(_expectedPublisherSha);
        _stagePackage.Content = T("Verify and stage copy", "Verifica e prepara una copia");
        _stagePackage.IsEnabled = false;
        AutomationProperties.SetAutomationId(_stagePackage, "GameLibrary_Installer_Stage_Button");
        AutomationProperties.SetName(_stagePackage, T("Verify package and stage a copy without executing",
            "Verifica il pacchetto e prepara una copia senza eseguirlo"));
        _stagePackage.Click += async (_, _) => await StageInspectedPackageAsync();
        _installerSection.Children.Add(_stagePackage);
        _refreshStaged.Content = T("Show staged packages", "Mostra pacchetti preparati");
        AutomationProperties.SetAutomationId(_refreshStaged, "GameLibrary_Installer_Staged_Refresh_Button");
        AutomationProperties.SetName(_refreshStaged, T("List managed verified package copies",
            "Elenca le copie di pacchetti verificate e gestite"));
        _refreshStaged.IsEnabled = false;
        _refreshStaged.Click += async (_, _) => await RefreshStagedPackagesAsync();
        _installerSection.Children.Add(_refreshStaged);
        AutomationProperties.SetAutomationId(_stagedStatus, "GameLibrary_Installer_Staged_Status");
        AutomationProperties.SetLiveSetting(_stagedStatus, AutomationLiveSetting.Polite);
        _installerSection.Children.Add(_stagedStatus);
        _installerSection.Children.Add(_stagedPackages);
        AutomationProperties.SetAutomationId(_installerStatus, "GameLibrary_Installer_Status_Text");
        AutomationProperties.SetLiveSetting(_installerStatus, AutomationLiveSetting.Polite);
        _installerSection.Children.Add(_installerStatus);
        int displayEnd = root.Children.Count;
        root.Children.Add(_installerSection);

        // One endless page was hard to use. The same controls (and AutomationIds) are grouped into four sections,
        // like a launcher: Library first; downloads, adding/importing and devices one click away.
        List<UIElement> all = root.Children.ToList();
        root.Children.Clear();
        root.Children.Add(all[0]);   // page title
        // Since step 2 of the gaming-removal map this page is a sub-page of the slim Games tools page (the Games home): it only
        // keeps downloads, adding/importing/installer staging and the Gaming status. The native library is no longer shown.
        var openToolsPage = new Button
        {
            Content = T("Back to Games tools", "Torna a Strumenti giochi"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinHeight = 48
        };
        AutomationProperties.SetAutomationId(openToolsPage, "GameLibrary_OpenToolsPage_Button");
        openToolsPage.Click += (_, _) => App.MainWin?.ShowGamesTools();
        root.Children.Add(openToolsPage);
        var sections = new (string Id, string Title, List<UIElement> Items)[]
        {
            ("Downloads", T("Downloads", "Download"), all.Skip(downloadsStart).Take(addStart - downloadsStart).ToList()),
            ("Add", T("Add & import", "Aggiungi e importa"),
                new UIElement[] { _storeDiscoveryOffer, new PlayniteImportPanel(async () => { await LoadAsync(); if (_universal is not null) await _universal.ReloadTitlesNowAsync(); }), _universal.AddTools }.Concat(all.Skip(addStart).Take(displayStart - addStart)).Concat(all.Skip(displayEnd)).ToList()),
            ("Devices", T("Gaming settings", "Impostazioni gaming"),
                all.Skip(sessionStart).Take(downloadsStart - sessionStart)
                    .Concat(all.Skip(displayStart).Take(displayEnd - displayStart)).Append(_universal.BackupTools).ToList()),
        };
        var bar = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Left };
        _sectionBar = bar;
        AutomationProperties.SetAutomationId(bar, "GameLibrary_Sections");
        AutomationProperties.SetName(bar, T("Game library sections", "Sezioni della libreria giochi"));
        var panels = new Dictionary<string, StackPanel>();
        foreach (var section in sections)
        {
            var item = new SelectorBarItem { Text = section.Title, Tag = section.Id, MinHeight = 44 };
            AutomationProperties.SetAutomationId(item, "GameLibrary_Section_" + section.Id);
            bar.Items.Add(item);
            var panel = new StackPanel { Spacing = 16, Visibility = section.Id == "Downloads" ? Visibility.Visible : Visibility.Collapsed };
            foreach (UIElement element in section.Items) panel.Children.Add(element);
            panels[section.Id] = panel;
        }
        bar.SelectionChanged += (sender, args) =>
        {
            string? tag = (sender.SelectedItem as SelectorBarItem)?.Tag as string;
            foreach (var pair in panels) pair.Value.Visibility = pair.Key == tag ? Visibility.Visible : Visibility.Collapsed;
        };
        _universal.ImportGamesRequested += () =>
        {
            var addItem = bar.Items.OfType<SelectorBarItem>().First(item => (string?)item.Tag == "Add");
            bar.SelectedItem = addItem;
            _pageScroll?.ChangeView(null, 0, null, disableAnimation: true);
            addItem.Focus(FocusState.Programmatic);
        };
        // Five sections do not fit a narrow window: the strip scrolls sideways instead of cutting the last tab off.
        root.Children.Add(new ScrollViewer
        {
            Content = bar,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        BuildGamingStrip();
        root.Children.Add(_gamingStrip);
        foreach (var panel in panels.Values) root.Children.Add(panel);
        bar.SelectedItem = bar.Items[0];

        _pageScroll = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        // Console/TV quick actions live in a layer above the scroller (not inside it), so opening one never moves the library.
        var pageHost = new Grid();
        pageHost.Children.Add(_pageScroll);
        pageHost.Children.Add(_universal.QuickActionsLayer);
        Content = pageHost;
        Loaded += (_, _) => { LibraryThemeState.Changed += ApplyLibraryThemeBackground; ApplyLibraryThemeBackground(); };
        Unloaded += (_, _) => LibraryThemeState.Changed -= ApplyLibraryThemeBackground;
    }

    /// <summary>Opens one section of this page from the slim Games tools page. Navigation only.</summary>
    internal void OpenSection(string section) => OpenGamesSection(section);

    /// <summary>Shared navigation for Games tools. No refresh, scan or install happens merely by changing sections.</summary>
    private void OpenGamesSection(string section)
    {
        SelectorBar? bar = _sectionBar;
        if (bar is null) return;
        SelectorBarItem? item = bar.Items.OfType<SelectorBarItem>()
            .FirstOrDefault(x => string.Equals(x.Tag as string, section, StringComparison.Ordinal));
        if (item is null) return;
        bar.SelectedItem = item;
        _pageScroll?.ChangeView(null, 0, null, disableAnimation: true);
        item.Focus(FocusState.Programmatic);
    }
}
