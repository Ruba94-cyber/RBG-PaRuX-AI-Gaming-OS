using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// The slim "Games tools" workspace page (additive step 1 of the gaming-removal map). It is a launch pad, not a game library: every
/// control reuses an existing dialog, panel, service or Settings section, so no behaviour, consent gate or stored data changes.
///
/// Normal: Playnite (Console / Desktop / setup / status, streaming, Game Bar, apps after sign-in, controller actions), AI mod studio
/// (AI proposals, restore, texture mods, installed mods, Minecraft skins, Quick Resume), downloads and installs, the AI/game resource
/// handoff and the local game guide.
/// Advanced only: save backups, and "Controller tools" (virtual pad, HidHide, XInput shim, mapping wizard, live view) with display
/// modes, hosted by the existing <see cref="GamingDevicesPanel"/>.
///
/// Nothing is detected, scanned, downloaded or started merely by opening the page: heavy panels are created only when their section
/// is expanded, and the Playnite status is read when the page is shown (one read-only call).
/// </summary>
internal sealed class GamesToolsView : UserControl
{
    private static string T(string en, string it) => UiText.Pick(en, it);

    private readonly StackPanel _root = new() { Spacing = 16, Padding = new Thickness(24, 16, 24, 32), MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _advanced = new() { Spacing = 16, Visibility = Visibility.Collapsed };
    private readonly TextBlock _playniteStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _handoffStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _guideButton;
    private Button? _primaryAction;
    private bool _busy;
    private bool _refreshing;

    /// <summary>Returns true only if the existing Chat accepted the read-only draft (wired by the shell).</summary>
    public event Func<string, IReadOnlyList<string>, bool>? GameGuideChatRequested;

    public GamesToolsView()
    {
        var scroll = new ScrollViewer
        {
            Content = _root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Content = scroll;

        var title = new TextBlock { Text = T("Games tools", "Strumenti giochi"), TextWrapping = TextWrapping.Wrap };
        if (Style("PaRuXSectionHeaderTextStyle") is { } headerStyle) title.Style = headerStyle;
        else title.FontSize = 24;
        AutomationProperties.SetAutomationId(title, "GamesToolsPage_Heading");
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        _root.Children.Add(title);
        _root.Children.Add(Caption(T(
            "Playnite is your optional game frontend. PaRuX keeps what is its own: AI mods, downloads and installs, the AI-to-game resource handoff, controller actions and the local game guide.",
            "Playnite è il tuo frontend giochi facoltativo. PaRuX mantiene ciò che è suo: mod IA, download e installazioni, passaggio risorse IA-gioco, azioni del controller e guida giochi locale.")));

        // ---- Playnite ----------------------------------------------------------------------------------------------
        StackPanel play = Card(T("Playnite", "Playnite"), T(
            "Open Playnite in Console (Fullscreen) or Desktop mode, or install, locate or skip it. Nothing is changed in your Playnite library.",
            "Apri Playnite in modalità Console (schermo intero) o Desktop, oppure installalo, indicalo o saltalo. La tua libreria Playnite non viene modificata."), _root);
        AutomationProperties.SetAutomationId(_playniteStatus, "GamesToolsPage_Playnite_Status");
        AutomationProperties.SetLiveSetting(_playniteStatus, AutomationLiveSetting.Polite);
        _playniteStatus.Text = T("Playnite status not checked yet.", "Stato Playnite non ancora controllato.");
        play.Children.Add(_playniteStatus);
        play.Children.Add(Row(
            _primaryAction = Action("PlayniteConsole", T("Playnite Console (Fullscreen)", "Playnite Console (schermo intero)"), () => OpenPlayniteAsync(PlayniteLauncherAction.Console)),
            Action("PlayniteDesktop", T("Playnite Desktop", "Playnite Desktop"), () => OpenPlayniteAsync(PlayniteLauncherAction.Desktop)),
            // The launcher owns Detect / Locate / Install with PaRuX / Skip.
            Action("PlayniteSetup", T("Install / locate / skip Playnite", "Installa / individua / salta Playnite"), () => OpenPlayniteAsync(PlayniteLauncherAction.Open)),
            Action("PlayniteRefresh", T("Check Playnite status", "Controlla stato Playnite"), RefreshPlayniteAsync)));
        play.Children.Add(Row(
            Action("StreamingSettings", T("Streaming mode and Playnite settings", "Modalità streaming e impostazioni Playnite"),
                () => OpenSettings(SettingsView.GamingSectionPlaynite)),
            Action("GameBarSettings", T("Console mode and Windows Game Bar", "Modalità Console e Windows Game Bar"),
                () => OpenSettings(SettingsView.GamingSectionConsoleDisplays)),
            Action("SignInApps", T("Apps after sign-in", "App dopo l'accesso"),
                () => OpenSettings(SettingsView.GamingSectionSignInApps)),
            Action("PadActions", T("Controller actions", "Azioni del controller"),
                () => { App.MainWin?.ShowPadControllerSettings(); return Task.CompletedTask; })));

        // ---- AI mod studio ------------------------------------------------------------------------------------------
        StackPanel mods = Card(T("AI mod studio", "Studio mod IA"), T(
            "Review AI-proposed configuration edits, texture packages and installed compatible ZIP mods. Every write needs its own consent and a supported game format; executables and anti-cheat are never patched.",
            "Rivedi modifiche di configurazione proposte dall'IA, pacchetti texture e mod ZIP compatibili installate. Ogni modifica richiede un consenso separato e un formato di gioco supportato; eseguibili e anti-cheat non vengono mai modificati."), _root);
        mods.Children.Add(Row(
            Action("AiProposals", T("Review AI mod proposals", "Rivedi proposte mod IA"), () => TemporaryGameModsDialog.ShowPendingAiProposalsAsync(XamlRoot)),
            Action("AiRestore", T("Restore AI configuration mods", "Ripristina mod di configurazione IA"), () => TemporaryGameModsDialog.ShowRestoreListAsync(XamlRoot)),
            Action("AssetStudio", T("Create an AI texture mod", "Crea una mod texture IA"), () => GameAssetModStudioDialog.ShowAsync(XamlRoot)),
            Action("InstalledMods", T("Manage installed compatible mods", "Gestisci mod compatibili installate"), () => GameModDropInDialog.ShowInstalledAsync(XamlRoot)),
            Action("SkinStudio", T("Minecraft skin studio", "Studio skin Minecraft"), () => MinecraftSkinStudioDialog.ShowAsync(XamlRoot))));
        mods.Children.Add(Lazy("QuickResume", T("Quick Resume and paused-game modding", "Ripresa rapida e mod per giochi in pausa"), () =>
        {
            // Both panels look at open applications / paused games when loaded: they only exist once the owner expands this.
            var inner = new StackPanel { Spacing = 12 };
            inner.Children.Add(new PausedGameBanner());
            inner.Children.Add(new QuickResumePanel());
            return inner;
        }));

        // ---- Downloads and installs ---------------------------------------------------------------------------------
        StackPanel downloads = Card(T("Downloads and installs", "Download e installazioni"), T(
            "Game downloads, installer inspection and staging. Installers are never run without your confirmation.",
            "Download di giochi, controllo e preparazione degli installer. Gli installer non vengono mai eseguiti senza la tua conferma."), _root);
        downloads.Children.Add(Lazy("Downloads", T("Game downloads", "Download dei giochi"), () => new GameDownloadsPanel()));
        downloads.Children.Add(Row(
            Action("InstallStage", T("Inspect / stage / import an installer", "Controlla / prepara / importa un installer"),
                () => OpenLibrarySection("Add"))));

        // ---- AI <-> game resource handoff ---------------------------------------------------------------------------
        StackPanel handoff = Card(T("AI and game resource handoff", "Passaggio risorse IA e gioco"), T(
            "While a game runs, PaRuX stops its own local AI and frees the graphics card, then gives AI back when the game ends. It never closes programs it did not start.",
            "Mentre un gioco è in esecuzione, PaRuX ferma la propria IA locale e libera la scheda video, poi restituisce l'IA a fine partita. Non chiude programmi che non ha avviato."), _root);
        AutomationProperties.SetAutomationId(_handoffStatus, "GamesToolsPage_Handoff_Status");
        AutomationProperties.SetLiveSetting(_handoffStatus, AutomationLiveSetting.Polite);
        handoff.Children.Add(_handoffStatus);
        handoff.Children.Add(Row(Action("HandoffSettings", T("Gaming and AI sharing settings", "Impostazioni condivisione Gaming e IA"),
            () => OpenSettings(null))));

        // ---- Local game guide ---------------------------------------------------------------------------------------
        StackPanel guide = Card(T("Local game guide", "Guida giochi locale"), T(
            "Ask about a local game using game guides you indexed yourself. The question is only prepared in Chat; you review and send it.",
            "Chiedi di un gioco locale usando guide che hai indicizzato tu. La domanda viene solo preparata in Chat: la controlli e la invii tu."), _root);
        _guideButton = Action("LocalGuide", T("Prepare a question in Chat", "Prepara una domanda in Chat"),
            () => LocalGameGuideFlow.RunAsync(XamlRoot, text => _message.Text = text, (q, s) => GameGuideChatRequested?.Invoke(q, s) == true));
        guide.Children.Add(Row(_guideButton));

        AutomationProperties.SetAutomationId(_message, "GamesToolsPage_Message");
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        _root.Children.Add(_message);

        // ---- Advanced ----------------------------------------------------------------------------------------------
        StackPanel saves = Card(T("Save backups", "Backup dei salvataggi"), T(
            "Back up and restore game saves. Your data is never deleted by this page.",
            "Backup e ripristino dei salvataggi. I tuoi dati non vengono mai eliminati da questa pagina."), _advanced);
        saves.Children.Add(Lazy("SaveBackups", T("Game save backups", "Backup dei salvataggi"), () => new GameSaveBackupPanel()));

        StackPanel controller = Card(T("Controller tools and display", "Strumenti controller e schermo"), T(
            "Virtual pad (ViGEm), HidHide guided setup, the XInput shim installer, the mapping wizard and live view, plus display modes. These can change drivers, third-party game folders or display modes: each action asks for its own confirmation.",
            "Pad virtuale (ViGEm), configurazione guidata di HidHide, installer dello shim XInput, procedura di mappatura e vista in tempo reale, oltre alle modalità schermo. Possono modificare driver, cartelle di giochi di terze parti o modalità schermo: ogni azione chiede la propria conferma."), _advanced);
        controller.Children.Add(Lazy("ControllerTools", T("Controller tools and display", "Strumenti controller e schermo"), () => new GamingDevicesPanel()));
        _root.Children.Add(_advanced);
    }

    /// <summary>Called by the shell each time the page is shown: applies Normal vs Advanced and reads status (read-only).</summary>
    public void OnShown()
    {
        bool advanced = SettingsStore.Current.Data.StudioModeEnabled;
        _advanced.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        _guideButton.Visibility = LocalGameGuideFlow.IsAvailableForSetupProfile() ? Visibility.Visible : Visibility.Collapsed;
        _message.Text = string.Empty;
        UpdateHandoffStatus(activeSession: false);
        _ = RefreshPlayniteAsync();
    }

    /// <summary>Controller entry: focus the first Playnite action (Console). Focus only, nothing is started.</summary>
    internal bool FocusPrimaryAction() => _primaryAction?.Focus(FocusState.Keyboard) == true;

    // ---- building blocks ---------------------------------------------------------------------------------------------

    private static Style? Style(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) ? value as Style : null;

    private static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        if (Style("PaRuXCaptionTextStyle") is { } style) block.Style = style;
        return block;
    }

    /// <summary>Creates a themed card (PRX Orbit card style), adds it to <paramref name="parent"/> and returns its body.</summary>
    private static StackPanel Card(string heading, string description, Panel parent)
    {
        var inner = new StackPanel { Spacing = 10 };
        var head = new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap };
        if (Style("PaRuXBodyStrongTextStyle") is { } style) head.Style = style;
        else head.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(head, AutomationHeadingLevel.Level2);
        inner.Children.Add(head);
        inner.Children.Add(Caption(description));
        var border = new Border { Child = inner };
        if (Style("PaRuXCardStyle") is { } cardStyle) border.Style = cardStyle;
        else border.Padding = new Thickness(16);
        parent.Children.Add(border);
        return inner;
    }

    private static StackPanel Row(params Button[] buttons)
    {
        var wrap = new StackPanel { Spacing = 8 };
        foreach (Button button in buttons) wrap.Children.Add(button);
        return wrap;
    }

    private static Expander Lazy(string id, string header, Func<UIElement> create)
    {
        var expander = new Expander
        {
            Header = header,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(expander, "GamesToolsPage_" + id + "_Expander");
        AutomationProperties.SetName(expander, header);
        expander.Expanding += (_, _) => expander.Content ??= create();
        return expander;
    }

    private Button Action(string id, string title, Func<Task> action)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap },
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetAutomationId(button, "GamesToolsPage_" + id + "_Button");
        AutomationProperties.SetName(button, title);
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception ex)
            {
                Log.Write("GamesToolsPage action " + id + " failed: " + ex.GetType().Name);
                _message.Text = T("This action could not be completed. Check the Core connection or try again.",
                    "L'azione non è riuscita. Controlla la connessione al Core o riprova.");
            }
            finally
            {
                button.IsEnabled = true;
                _busy = false;
            }
        };
        return button;
    }

    // ---- actions -----------------------------------------------------------------------------------------------------

    private Task OpenSettings(string? sectionAutomationId)
    {
        App.MainWin?.ShowGamingAiSharingSettings(sectionAutomationId);
        return Task.CompletedTask;
    }

    private Task OpenLibrarySection(string section)
    {
        App.MainWin?.ShowGamesLibrarySection(section);
        return Task.CompletedTask;
    }

    private async Task OpenPlayniteAsync(PlayniteLauncherAction action)
    {
        // A frontend must not take focus from a running game. Core enforces the same rule again; this is early feedback.
        if (PlayniteLauncherForegroundPolicy.RequiresGamePreflight(action))
        {
            bool gameActive = GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive();
            if (gameActive)
            {
                _message.Text = T("A game is active. Finish playing before opening another Playnite mode.",
                    "Un gioco è in esecuzione. Termina la partita prima di aprire un'altra modalità Playnite.");
                return;
            }
            var status = await PlayniteClient.Current.GetStatusAsync();
            if (!status.Succeeded || !PlayniteLauncherForegroundPolicy.MayOpen(action, gameActive, status.Value))
            {
                _message.Text = T("Cannot safely open Playnite while a game is active or its status is unknown.",
                    "Impossibile aprire Playnite in sicurezza mentre un gioco è attivo o lo stato è sconosciuto.");
                return;
            }
        }
        _message.Text = string.Empty;
        if (App.MainWin is not { } host)
        {
            _message.Text = T("The PaRuX window is unavailable.", "La finestra PaRuX non è disponibile.");
            return;
        }
        await host.ShowPlayniteLauncherAsync(action, gamepad: false, fromRequest: true);
        await RefreshPlayniteAsync();
    }

    private void UpdateHandoffStatus(bool activeSession)
    {
        bool gameActive = activeSession || GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive();
        _handoffStatus.Text = gameActive
            ? T("A game is active: PaRuX is managing the AI/GPU handoff.", "Un gioco è in esecuzione: PaRuX gestisce il passaggio delle risorse IA/GPU.")
            : T("No game is active: local AI keeps its resources.", "Nessun gioco in esecuzione: l'IA locale mantiene le proprie risorse.");
    }

    internal async Task RefreshPlayniteAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var result = await PlayniteClient.Current.GetStatusAsync();
            if (!result.Succeeded || result.Value is not { } status)
            {
                _playniteStatus.Text = T("Playnite status unavailable. Check the local Core connection.",
                    "Stato Playnite non disponibile. Controlla il Core locale.");
                return;
            }
            PlayniteComponentDto component = status.Component;
            UpdateHandoffStatus(status.Session.TrackedProcessCount > 0);
            if (status.Settings.Skipped || component.Skipped)
                _playniteStatus.Text = T("Playnite is optional and currently skipped. Use the setup button to enable it.",
                    "Playnite è facoltativo ed è stato saltato. Usa il pulsante di configurazione per abilitarlo.");
            else if (!component.Detected)
                _playniteStatus.Text = T("Playnite was not found. Locate it or choose the official installer.",
                    "Playnite non trovato. Indicalo o scegli l'installer ufficiale.");
            else
                _playniteStatus.Text = component.Running
                    ? T("Playnite is running.", "Playnite è in esecuzione.")
                    : T("Playnite is detected and ready.", "Playnite rilevato e pronto.");
        }
        catch (Exception ex)
        {
            Log.Write("GamesToolsPage Playnite status read failed: " + ex.GetType().Name);
            _playniteStatus.Text = T("Playnite status could not be read.", "Impossibile leggere lo stato Playnite.");
        }
        finally { _refreshing = false; }
    }
}
