using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// "Shortcuts" in a game's detail panel: a desktop shortcut and a Steam "non-Steam game" entry. Both start
/// <c>PaRuX.exe --play &lt;game&gt;</c>, so the game still starts through PaRuX with its normal confirmation.
/// Nothing is created until a button is pressed. The desktop shortcut never replaces a file that is already there; the
/// Steam entry is appended to Steam's list after a backup, only with Steam closed and only after the person confirms.
/// Artwork is not touched: Steam shows the program's icon and the person picks art in Steam.
/// </summary>
internal sealed class GameShortcutsSection : StackPanel
{
    private static string T(string en, string it) => GameUi.T(en, it);

    private readonly GameTitleDto _title;
    private readonly Button _desktop = new() { MinHeight = 44 };
    private readonly Button _steam = new() { MinHeight = 44 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private bool _busy;

    internal GameShortcutsSection(GameTitleDto title)
    {
        _title = title;
        Spacing = 8;
        string key = title.TitleKey;
        AutomationProperties.SetAutomationId(this, "GameDetail_Shortcuts_Panel_" + key);

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Text = T(
                "A shortcut starts this game through PaRuX, with the same confirmation as the Play button. Nothing is changed until you press a button.",
                "Un collegamento avvia questo gioco tramite PaRuX, con la stessa conferma del pulsante Gioca. Non viene cambiato nulla finché non premi un pulsante."),
        };

        _desktop.Content = T("Create a desktop shortcut", "Crea un collegamento sul desktop");
        _desktop.Click += async (_, _) => await CreateDesktopAsync();
        AutomationProperties.SetAutomationId(_desktop, "GameDetail_Shortcuts_Desktop_" + key);

        _steam.Content = T("Add to Steam as a non-Steam game", "Aggiungi a Steam come gioco non Steam");
        _steam.Click += async (_, _) => await AddToSteamAsync();
        AutomationProperties.SetAutomationId(_steam, "GameDetail_Shortcuts_Steam_" + key);

        AutomationProperties.SetAutomationId(_status, "GameDetail_Shortcuts_Status_" + key);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        Children.Add(intro);
        Children.Add(_desktop);
        Children.Add(_steam);
        Children.Add(_status);
    }

    /// <summary>PaRuX.exe when a shortcut can safely point at it; otherwise null with the reason in <paramref name="problem"/>.</summary>
    private static string? PaRuXExecutable(out string? problem)
    {
        problem = GameShortcuts.CheckExecutable(Environment.ProcessPath) switch
        {
            ShortcutExecutableProblem.None => null,
            ShortcutExecutableProblem.MicrosoftStore => T(
                "This copy of PaRuX is installed from the Microsoft Store, whose program path changes with every update, so a shortcut to it would break. Start games from inside PaRuX, or pin PaRuX itself to Start.",
                "Questa copia di PaRuX è installata dal Microsoft Store, il cui percorso cambia a ogni aggiornamento, quindi un collegamento si romperebbe. Avvia i giochi da PaRuX, oppure fissa PaRuX stesso su Start."),
            ShortcutExecutableProblem.NotPaRuXExe => T(
                "Shortcuts can only point at PaRuX.exe, and this is not it.",
                "I collegamenti possono puntare solo a PaRuX.exe, e questo non lo è."),
            _ => T("PaRuX could not find its own program file.", "PaRuX non ha trovato il proprio file di programma."),
        };
        return problem is null ? Environment.ProcessPath : null;
    }

    private async Task CreateDesktopAsync()
    {
        if (_busy) return;
        string? exe = PaRuXExecutable(out string? exeProblem);
        if (exe is null)
        {
            _status.Text = T("No shortcut was created. ", "Nessun collegamento creato. ") + exeProblem;
            return;
        }
        _busy = true;
        _desktop.IsEnabled = _steam.IsEnabled = false;
        try
        {
            GameShortcutResult result = await Task.Run(() => GameShortcuts.CreateOnDesktop(_title.Name, _title.TitleKey, exe));
            _status.Text = result.Outcome switch
            {
                GameShortcutOutcome.Created => T("Shortcut created on your desktop: ", "Collegamento creato sul desktop: ") + System.IO.Path.GetFileName(result.Path),
                GameShortcutOutcome.AlreadyExists => T("This shortcut is already on your desktop: ", "Questo collegamento è già sul desktop: ") + System.IO.Path.GetFileName(result.Path),
                _ => T("No shortcut was created. ", "Nessun collegamento creato. ") + result.Message,
            };
        }
        catch (Exception ex)
        {
            Log.Write("GameShortcutsSection desktop failed: " + ex.Message);
            _status.Text = T("No shortcut was created: ", "Nessun collegamento creato: ") + ex.Message;
        }
        finally
        {
            _busy = false;
            _desktop.IsEnabled = _steam.IsEnabled = true;
        }
    }

    private async Task AddToSteamAsync()
    {
        if (_busy) return;
        string? exe = PaRuXExecutable(out string? exeProblem);
        if (exe is null)
        {
            _status.Text = T("No shortcut was created. ", "Nessun collegamento creato. ") + exeProblem;
            return;
        }
        _busy = true;
        _desktop.IsEnabled = _steam.IsEnabled = false;
        try
        {
            string? steamRoot = await Task.Run(SteamShortcutsFile.FindSteamRoot);
            if (steamRoot is null)
            {
                _status.Text = T("Steam was not found on this PC.", "Steam non è stato trovato su questo PC.");
                return;
            }
            IReadOnlyList<SteamUserFolder> users = await Task.Run(() => SteamShortcutsFile.FindUsers(steamRoot));
            if (users.Count == 0)
            {
                _status.Text = T("Steam has no signed-in account on this PC yet. Sign in to Steam once, then try again.",
                    "Steam non ha ancora nessun account su questo PC. Accedi a Steam una volta, poi riprova.");
                return;
            }
            if (await Task.Run(SteamShortcutsFile.IsSteamRunning))
            {
                _status.Text = T("Steam is running. Close Steam completely, then add the game again.",
                    "Steam è in esecuzione. Chiudi Steam completamente, poi aggiungi di nuovo il gioco.");
                return;
            }

            SteamUserFolder? chosen = await ConfirmAsync(users);
            if (chosen is null)
            {
                _status.Text = T("Nothing was added to Steam.", "Non è stato aggiunto nulla a Steam.");
                return;
            }

            SteamShortcutEntry entry = SteamShortcutsFile.EntryFor(_title.Name, _title.TitleKey, exe);
            string vdf = System.IO.Path.Combine(chosen.ConfigDirectory, "shortcuts.vdf");
            SteamShortcutResult result = await Task.Run(() => SteamShortcutsFile.TryAppend(vdf, entry));
            _status.Text = result.Outcome switch
            {
                SteamShortcutOutcome.Added => T("Added to Steam. Start Steam to see it in your library. A backup of the old list is kept next to it.",
                    "Aggiunto a Steam. Avvia Steam per vederlo nella libreria. Una copia dell'elenco precedente è conservata accanto.")
                    + (result.BackupPath is null ? string.Empty : " (" + System.IO.Path.GetFileName(result.BackupPath) + ")"),
                SteamShortcutOutcome.AlreadyPresent => T("This game is already in your Steam library as a non-Steam game.",
                    "Questo gioco è già nella tua libreria Steam come gioco non Steam."),
                SteamShortcutOutcome.SteamRunning => T("Steam is running. Close Steam completely, then add the game again.",
                    "Steam è in esecuzione. Chiudi Steam completamente, poi aggiungi di nuovo il gioco."),
                SteamShortcutOutcome.UnreadableFile => T("Steam's list is in a form PaRuX cannot read exactly, so it was left untouched.",
                    "L'elenco di Steam ha un formato che PaRuX non sa leggere con precisione, quindi non è stato toccato."),
                _ => T("Nothing was added to Steam. ", "Non è stato aggiunto nulla a Steam. ") + result.Message,
            };
        }
        catch (Exception ex)
        {
            Log.Write("GameShortcutsSection steam failed: " + ex.Message);
            _status.Text = T("Nothing was added to Steam: ", "Non è stato aggiunto nulla a Steam: ") + ex.Message;
        }
        finally
        {
            _busy = false;
            _desktop.IsEnabled = _steam.IsEnabled = true;
        }
    }

    /// <summary>Says exactly what will change and lets the person pick the Steam account. Null when they decline.</summary>
    private async Task<SteamUserFolder?> ConfirmAsync(IReadOnlyList<SteamUserFolder> users)
    {
        var accounts = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = T("Steam account folder", "Cartella dell'account Steam") };
        foreach (SteamUserFolder user in users) accounts.Items.Add(user.AccountId);
        accounts.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(accounts, "GameDetail_Shortcuts_SteamAccount");

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = T(
                $"PaRuX will add one entry, \"{_title.Name}\", to Steam's list of non-Steam games. It starts PaRuX, which then asks before starting the game. "
                + "Only this entry is added: your other Steam entries are not touched, and a backup of the current list is saved next to it first. "
                + "Steam must be closed for this to work.",
                $"PaRuX aggiungerà una voce, \"{_title.Name}\", all'elenco dei giochi non Steam. Avvia PaRuX, che poi chiede conferma prima di avviare il gioco. "
                + "Viene aggiunta solo questa voce: le altre voci di Steam non vengono toccate e prima viene salvata una copia dell'elenco attuale accanto ad esso. "
                + "Steam deve essere chiuso."),
        });
        if (users.Count > 1) content.Children.Add(accounts);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("Add to Steam?", "Aggiungere a Steam?"),
            Content = content,
            PrimaryButtonText = T("Add to Steam", "Aggiungi a Steam"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return users.FirstOrDefault(u => u.AccountId == accounts.SelectedItem as string) ?? users[0];
    }
}
