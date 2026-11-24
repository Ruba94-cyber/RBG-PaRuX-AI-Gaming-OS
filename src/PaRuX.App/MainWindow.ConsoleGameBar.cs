using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX;

/// <summary>
/// Blocks the Xbox Game Bar (controller Guide/Nexus button, startup panel, Game DVR capture) for this Windows user while Console or a
/// PaRuX-started Playnite Fullscreen is in use. On by default, but nothing is changed until the owner has seen the explanation once
/// (this dialog, or turning the Settings switch on). The Core owns the restore point; restore is always requested at the end and never
/// overrides a value the owner changed meanwhile. A permanent block (opt-in) is kept after Console ends.
/// </summary>
public sealed partial class MainWindow
{
    internal async Task ApplyConsoleGameBarBlockAsync()
    {
        try
        {
            var data = SettingsStore.Current.Data;
            if (!data.BlockXboxGameBar || App.IsShuttingDown) return;
            if (!data.GameBarBlockConsentAsked && !await AskGameBarConsentAsync()) return;
            var result = await PlayniteClient.Current.ApplyGameBarBlockAsync(SettingsStore.Current.Data.GameBarBlockPermanent);
            Log.Write("Xbox Game Bar block: " + (result.Succeeded ? "applied" : "not applied (" + result.Error + ")"));
        }
        catch (Exception ex) { Log.Write("Xbox Game Bar block apply failed: " + ex.GetType().Name); }
    }

    internal async Task RestoreConsoleGameBarBlockAsync()
    {
        try
        {
            // A permanent block stays (the Core keeps it unless includePermanent). Restore is harmless when nothing is applied.
            var result = await PlayniteClient.Current.RestoreGameBarAsync(includePermanent: false);
            if (!result.Succeeded) Log.Write("Xbox Game Bar restore failed: " + result.Error);
        }
        catch (Exception ex) { Log.Write("Xbox Game Bar restore failed: " + ex.GetType().Name); }
    }

    /// <summary>Shows the one-time explanation. "Not now" turns the option off; nothing in Windows is touched either way until Allow.</summary>
    private async Task<bool> AskGameBarConsentAsync()
    {
        if (RootGrid.XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = ConsoleText("Block the Xbox Game Bar?", "Bloccare la Xbox Game Bar?"),
            Content = ConsoleText(
                "While Console mode or Playnite Fullscreen is in use, PaRuX can stop the controller Guide/Xbox button from opening the Xbox Game Bar. It changes four per-user Windows settings (no administrator rights, nothing else): the controller-opens-Game-Bar switch, the Game Bar startup panel and Game DVR capture. PaRuX saves their previous values and puts them back when Console ends, on exit or after a crash, and never overrides a value you changed in the meantime. The Win+G keyboard shortcut is not covered. You can turn this off in Settings > Gaming.",
                "Mentre la modalità Console o Playnite a schermo intero è in uso, PaRuX può impedire che il pulsante Guide/Xbox del controller apra la Xbox Game Bar. Modifica quattro impostazioni di Windows valide solo per il tuo utente (nessun permesso di amministratore, nient'altro): l'opzione che permette al controller di aprire la Game Bar, il pannello di avvio della Game Bar e l'acquisizione Game DVR. PaRuX salva i valori precedenti e li ripristina quando la Console termina, alla chiusura o dopo un arresto anomalo, e non sovrascrive mai un valore che hai cambiato nel frattempo. La scorciatoia da tastiera Win+G non è coperta. Puoi disattivarlo in Impostazioni > Gaming."),
            PrimaryButtonText = ConsoleText("Allow", "Consenti"),
            CloseButtonText = ConsoleText("Not now", "Non ora"),
            DefaultButton = ContentDialogButton.Primary,
        };
        bool allow = await dialog.ShowAsync() == ContentDialogResult.Primary;
        var data = SettingsStore.Current.Data;
        data.GameBarBlockConsentAsked = true;
        data.BlockXboxGameBar = allow;
        SettingsStore.Current.Save();
        return allow;
    }
}
