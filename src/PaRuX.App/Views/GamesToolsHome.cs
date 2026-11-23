using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// The first, non-destructive step in replacing PaRuX's native game catalogue with
/// Playnite: one simple hub for Playnite and the game-management tools PaRuX keeps.
/// Existing Downloads/Add/Devices views and mod dialogs are reused; no game library
/// or owner-managed Playnite file is changed, and no game scan starts from this view.
/// </summary>
internal sealed class GamesToolsHome : StackPanel
{
    private static string T(string en, string it) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

    private readonly Action<string> _openSection;
    private readonly TextBlock _playniteStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private bool _busy;
    private bool _refreshing;
    private readonly Button _consoleAction;

    public GamesToolsHome(Action<string> openSection)
    {
        _openSection = openSection ?? throw new ArgumentNullException(nameof(openSection));
        Spacing = 16;
        AutomationProperties.SetAutomationId(this, "GamesTools_Home");
        AutomationProperties.SetName(this, T("Playnite and Games tools", "Playnite e strumenti giochi"));

        Children.Add(Heading(T("Play with Playnite", "Gioca con Playnite"), 24));
        Children.Add(Description(T(
            "Playnite is your optional Desktop / Fullscreen game frontend. PaRuX manages controller actions, downloads, AI mods, backups and the AI-to-game resource handoff.",
            "Playnite è il frontend opzionale Desktop / schermo intero. PaRuX gestisce i comandi del controller, i download, le mod IA, i backup e il passaggio delle risorse dall'IA ai giochi.")));

        AutomationProperties.SetAutomationId(_playniteStatus, "GamesTools_Playnite_Status");
        AutomationProperties.SetLiveSetting(_playniteStatus, AutomationLiveSetting.Polite);
        _playniteStatus.Text = T("Playnite status not checked yet.", "Stato Playnite non ancora controllato.");
        Children.Add(_playniteStatus);

        _consoleAction = ActionButton("PlayniteConsole",
            T("Playnite Console (Fullscreen)", "Playnite Console (schermo intero)"),
            () => OpenPlayniteAsync(PlayniteLauncherAction.Console));
        Children.Add(Actions(
            _consoleAction,
            ActionButton("PlayniteDesktop", T("Playnite Desktop", "Playnite Desktop"),
                () => OpenPlayniteAsync(PlayniteLauncherAction.Desktop)),
            ActionButton("PlayniteSetup", T("Install / locate / configure Playnite", "Installa / individua / configura Playnite"),
                // The Home page owns Detect / Locate / Install / Skip. Opening
                // the Settings subpage directly would hide that setup flow.
                () => OpenPlayniteAsync(PlayniteLauncherAction.Open)),
            ActionButton("PlayniteRefresh", T("Check Playnite status", "Controlla stato Playnite"),
                RefreshPlayniteAsync)));

        Children.Add(Heading(T("AI mod studio", "Studio mod IA"), 20));
        Children.Add(Description(T(
            "Not just skins: review AI-proposed configuration edits, texture packages and installed compatible ZIP mods. Every write requires separate consent and supported game formats; never patch executables or anti-cheat.",
            "Non solo skin: rivedi modifiche di configurazione proposte dall'IA, pacchetti texture e mod ZIP compatibili già installate. Ogni modifica richiede conferma separata e formati supportati; mai patch a eseguibili o anti-cheat.")));
        Children.Add(Actions(
            ActionButton("AiProposals", T("Review AI mod proposals", "Rivedi proposte mod IA"),
                () => TemporaryGameModsDialog.ShowPendingAiProposalsAsync(XamlRoot)),
            ActionButton("AiRestore", T("Restore AI configuration mods", "Ripristina mod configurazione IA"),
                () => TemporaryGameModsDialog.ShowRestoreListAsync(XamlRoot)),
            ActionButton("AssetStudio", T("Create an AI texture mod", "Crea una mod texture IA"),
                () => GameAssetModStudioDialog.ShowAsync(XamlRoot)),
            ActionButton("InstalledMods", T("Manage installed compatible mods", "Gestisci mod compatibili installate"),
                () => GameModDropInDialog.ShowInstalledAsync(XamlRoot))));

        Children.Add(Heading(T("Downloads, installs and saves", "Download, installazioni e salvataggi"), 20));
        Children.Add(Actions(
            ActionButton("Downloads", T("Open downloads", "Apri download"), () => OpenSectionAsync("Downloads")),
            ActionButton("Install", T("Inspect / stage / import games", "Controlla / prepara / importa giochi"), () => OpenSectionAsync("Add")),
            ActionButton("Backups", T("Save backups and Gaming status", "Backup salvataggi e stato Gaming"), () => OpenSectionAsync("Devices")),
            ActionButton("Controller", T("Configure controller buttons", "Configura pulsanti controller"),
                OpenControllerSettingsAsync)));

        // Reuses the existing no-input-injection Quick Resume / reversible mod
        // panel. It only refreshes when loaded, never scans games in the background.
        var quick = new Expander
        {
            Header = T("Quick Resume and paused-game modding", "Ripresa rapida e mod per giochi in pausa"),
            IsExpanded = false
        };
        // The Quick Resume panel inspects open applications when loaded. Create
        // it only after the owner expands this section, never on Games first paint.
        quick.Expanding += (_, _) => quick.Content ??= new QuickResumePanel();
        AutomationProperties.SetAutomationId(quick, "GamesTools_QuickResume_Expander");
        Children.Add(quick);

        Children.Add(Description(T(
            "Your existing native library is still available under Library during this migration. Nothing is deleted or moved on disk.",
            "La vecchia libreria resta accessibile in Libreria durante la migrazione. Nessun file viene eliminato o spostato.")));
        AutomationProperties.SetAutomationId(_message, "GamesTools_Message");
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        Children.Add(_message);

        // Do not start the local Core or Playnite discovery merely because
        // the parent Games page was constructed. The host refreshes this when
        // the user actually enters the Tools tab, and the button can retry.
    }

    /// <summary>The first focus target for PaRuX's desktop controller buttons.</summary>
    internal bool FocusPrimaryAction() => _consoleAction.Focus(FocusState.Programmatic);

    private static TextBlock Heading(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock Description(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.8
    };

    private static StackPanel Actions(params Button[] buttons)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (Button button in buttons) panel.Children.Add(button);
        return panel;
    }

    private Button ActionButton(string id, string title, Func<Task> action)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap },
            MinHeight = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetAutomationId(button, "GamesTools_" + id + "_Button");
        AutomationProperties.SetName(button, title);
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception ex)
            {
                Log.Write("GamesTools action " + id + " failed: " + ex.GetType().Name);
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

    private async Task OpenPlayniteAsync(PlayniteLauncherAction action)
    {
        // A frontend hotkey must not steal focus from a game. Core enforces the
        // same rule again at the process boundary; the UI gives early feedback.
        if (PlayniteLauncherForegroundPolicy.RequiresGamePreflight(action))
        {
            if (GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive())
            {
                _message.Text = T("A game is active. Finish playing before opening another Playnite mode.",
                    "Un gioco è in esecuzione. Termina la partita prima di aprire un'altra modalità Playnite.");
                return;
            }
            var status = await PlayniteClient.Current.GetStatusAsync();
            if (!status.Succeeded ||
                !PlayniteLauncherForegroundPolicy.MayOpen(action,
                    GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive(), status.Value))
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

        // The launcher is a modal workflow. Await its actual close rather than
        // marking the Games button idle immediately after scheduling a dialog.
        // When the user returns, reflect any Locate / Install / Skip changes.
        await host.ShowPlayniteLauncherAsync(action, gamepad: true, fromRequest: true);
        await RefreshPlayniteAsync();
    }

    private Task OpenSectionAsync(string section)
    {
        _openSection(section);
        return Task.CompletedTask;
    }

    private Task OpenControllerSettingsAsync()
    {
        // This is a direct UI navigation action, not a synthetic controller
        // event that could re-enter the listener's in-flight pad command.
        App.MainWin?.ShowPadControllerSettings();
        return Task.CompletedTask;
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
            if (status.Settings.Skipped || component.Skipped)
                _playniteStatus.Text = T("Playnite is optional and currently skipped. Use Configure to enable it.",
                    "Playnite è facoltativo ed è stato saltato. Usa Configura per abilitarlo.");
            else if (!component.Detected)
                _playniteStatus.Text = T("Playnite was not found. Locate it or choose the official installer.",
                    "Playnite non trovato. Indicalo o scegli l'installer ufficiale.");
            else if (status.Session.TrackedProcessCount > 0)
                _playniteStatus.Text = T("A game is active: PaRuX is managing the AI/GPU handoff.",
                    "Un gioco è in esecuzione: PaRuX gestisce il passaggio delle risorse IA/GPU.");
            else
                _playniteStatus.Text = component.Running
                    ? T("Playnite is running.", "Playnite è in esecuzione.")
                    : T("Playnite is detected and ready.", "Playnite rilevato e pronto.");
        }
        catch (Exception ex)
        {
            Log.Write("GamesTools Playnite status read failed: " + ex.GetType().Name);
            _playniteStatus.Text = T("Playnite status could not be read.", "Impossibile leggere lo stato Playnite.");
        }
        finally { _refreshing = false; }
    }
}
