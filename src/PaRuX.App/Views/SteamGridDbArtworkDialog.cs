using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Streams;

namespace PaRuX.Views;

/// <summary>
/// "Choose artwork": a gallery of SteamGridDB grids, heroes, logos and icons for one game. The owner's own key is used, nothing
/// is contacted without the click, never while Gaming mode is active. The chosen image is downloaded from the SteamGridDB CDN
/// into the existing cover store; no file the owner owns is touched.
/// </summary>
internal static class SteamGridDbArtworkDialog
{
    private static string T(string en, string it) => GameUi.T(en, it);

    /// <summary>Null when the entry point may be used; otherwise the honest reason it is disabled.</summary>
    public static string? UnavailableReason()
    {
        if (GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive())
            return T("Unavailable while Gaming mode is active.", "Non disponibile mentre la modalità Gaming è attiva.");
        if (!SteamGridDbKeyStore.HasKey)
            return T("Save a SteamGridDB key in the Gaming library first (\"SteamGridDB key...\").",
                     "Salva prima una chiave SteamGridDB nella libreria Gaming (\"Chiave SteamGridDB...\").");
        return null;
    }

    public static string StatusText(SteamGridDbStatus status) => status switch
    {
        SteamGridDbStatus.NoKey => T("No SteamGridDB key is saved.", "Nessuna chiave SteamGridDB salvata."),
        SteamGridDbStatus.KeyRejected => T("SteamGridDB rejected the key. Check or replace it.", "SteamGridDB ha rifiutato la chiave. Controllala o sostituiscila."),
        SteamGridDbStatus.RateLimited => T("SteamGridDB is limiting requests. Try again later.", "SteamGridDB sta limitando le richieste. Riprova più tardi."),
        SteamGridDbStatus.GameNotFound => T("SteamGridDB has no close match for this game.", "SteamGridDB non ha una corrispondenza per questo gioco."),
        SteamGridDbStatus.NoArtwork => T("No artwork of this kind is available.", "Nessuna immagine di questo tipo disponibile."),
        SteamGridDbStatus.BadResponse => T("SteamGridDB sent an unreadable answer.", "SteamGridDB ha inviato una risposta illeggibile."),
        SteamGridDbStatus.TooLarge => T("That image is too large (5 MB limit).", "Quell'immagine è troppo grande (limite 5 MB)."),
        SteamGridDbStatus.Blocked => T("That image address is not allowed.", "Quell'indirizzo immagine non è consentito."),
        _ => T("SteamGridDB could not be reached.", "SteamGridDB non è raggiungibile."),
    };

    private static string KindLabel(SteamGridDbArtKind kind) => kind switch
    {
        SteamGridDbArtKind.Hero => T("Hero (banner)", "Hero (banner)"),
        SteamGridDbArtKind.Logo => T("Logo", "Logo"),
        SteamGridDbArtKind.Icon => T("Icon", "Icona"),
        _ => T("Grid (cover)", "Grid (copertina)"),
    };

    /// <summary>Shows the dialog. <paramref name="changed"/> runs after a new cover (grid) was stored.</summary>
    public static async Task ShowAsync(XamlRoot root, GameTitleDto title, Func<Task> changed, Action<string> report)
    {
        if (UnavailableReason() is { } blocked) { report(blocked); return; }

        using var cts = new CancellationTokenSource();
        SteamGridDbGallery gallery = SteamGridDbArtworkPicker.Gallery;
        long? gameId = null;
        SteamGridDbCandidate? selected = null;
        int generation = 0;

        var kindBox = new ComboBox { MinWidth = 220, Header = T("Artwork type", "Tipo di immagine") };
        AutomationProperties.SetAutomationId(kindBox, "GameDetail_Artwork_Kind");
        foreach (SteamGridDbArtKind kind in Enum.GetValues<SteamGridDbArtKind>())
            kindBox.Items.Add(new ComboBoxItem { Content = KindLabel(kind), Tag = kind });
        kindBox.SelectedIndex = 0;

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = T("Loading…", "Caricamento…") };
        AutomationProperties.SetAutomationId(status, "GameDetail_Artwork_Status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var grid = new GridView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 420, MinWidth = 520 };
        AutomationProperties.SetAutomationId(grid, "GameDetail_Artwork_Gallery");

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(kindBox);
        content.Children.Add(status);
        content.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 440 });

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = T("Choose artwork", "Scegli immagine"),
            Content = content,
            PrimaryButtonText = T("Use selected", "Usa selezionata"),
            CloseButtonText = T("Close", "Chiudi"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false
        };
        AutomationProperties.SetAutomationId(dialog, "GameDetail_Artwork_Dialog");

        grid.SelectionChanged += (_, _) =>
        {
            selected = (grid.SelectedItem as FrameworkElement)?.Tag as SteamGridDbCandidate;
            dialog.IsPrimaryButtonEnabled = selected is not null;
        };

        async Task LoadAsync()
        {
            int mine = ++generation;
            grid.Items.Clear();
            selected = null;
            dialog.IsPrimaryButtonEnabled = false;
            var kind = (SteamGridDbArtKind)((ComboBoxItem)kindBox.SelectedItem).Tag;
            status.Text = T("Loading…", "Caricamento…");
            try
            {
                if (gameId is null)
                {
                    string name = title.Name;
                    int bracket = name.IndexOf('[');
                    var found = await gallery.FindGameAsync(bracket > 0 ? name[..bracket] : name, cts.Token);
                    if (mine != generation) return;
                    if (found.Value is not long id) { status.Text = StatusText(found.Status); return; }
                    gameId = id;
                }
                var listed = await gallery.ListAsync(gameId.Value, kind, cts.Token);
                if (mine != generation) return;
                if (!listed.Ok || listed.Value is null) { status.Text = StatusText(listed.Status); return; }
                status.Text = T($"{listed.Value.Count} images from SteamGridDB. Pick one.", $"{listed.Value.Count} immagini da SteamGridDB. Sceglierne una.");
                foreach (SteamGridDbCandidate candidate in listed.Value)
                {
                    var image = new Image { Width = kind == SteamGridDbArtKind.Grid ? 100 : 160, Height = kind == SteamGridDbArtKind.Grid ? 150 : 90, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
                    var cell = new StackPanel { Spacing = 2, Tag = candidate };
                    cell.Children.Add(image);
                    cell.Children.Add(new TextBlock { Text = candidate.Width > 0 ? $"{candidate.Width}x{candidate.Height}" : string.Empty, FontSize = 11 });
                    AutomationProperties.SetName(cell, T($"Artwork {candidate.Id}", $"Immagine {candidate.Id}"));
                    AutomationProperties.SetAutomationId(cell, "GameDetail_Artwork_Item_" + candidate.Id);
                    grid.Items.Add(cell);
                }
                foreach (SteamGridDbCandidate candidate in listed.Value) // sequential: kind to the service
                {
                    if (mine != generation || cts.IsCancellationRequested) return;
                    var thumb = await gallery.FetchImageAsync(candidate.ThumbUrl, SteamGridDbGallery.MaxThumbBytes, cts.Token);
                    if (!thumb.Ok || thumb.Value is null) continue;
                    var cell = grid.Items.OfType<StackPanel>().FirstOrDefault(c => ReferenceEquals(c.Tag, candidate));
                    if (cell?.Children[0] is not Image target) continue;
                    try
                    {
                        using var stream = new InMemoryRandomAccessStream();
                        await stream.WriteAsync(thumb.Value.AsBuffer());
                        stream.Seek(0);
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(stream);
                        target.Source = bitmap;
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { /* undecodable thumbnail stays blank */ }
                }
            }
            catch (OperationCanceledException) { }
        }

        kindBox.SelectionChanged += async (_, _) => await LoadAsync();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (selected is null) { args.Cancel = true; return; }
            var deferral = args.GetDeferral();
            try
            {
                if (UnavailableReason() is { } reason) { status.Text = reason; args.Cancel = true; return; }
                dialog.IsPrimaryButtonEnabled = false;
                status.Text = T("Downloading…", "Download in corso…");
                SteamGridDbArtKind kind = selected.Kind;
                GameCoverResult result = await SteamGridDbArtworkPicker.ChooseAsync(title.TitleKey, selected, cts.Token);
                if (result == GameCoverResult.Saved)
                {
                    report(T($"{KindLabel(kind)} saved.", $"{KindLabel(kind)} salvata."));
                    if (kind == SteamGridDbArtKind.Grid) await changed();
                    return;
                }
                status.Text = result switch
                {
                    GameCoverResult.TooLarge => StatusText(SteamGridDbStatus.TooLarge),
                    GameCoverResult.UnsupportedType => T("That image type is not supported.", "Tipo di immagine non supportato."),
                    _ => T("The image could not be downloaded or saved.", "Impossibile scaricare o salvare l'immagine."),
                };
                dialog.IsPrimaryButtonEnabled = true;
                args.Cancel = true;
            }
            catch (OperationCanceledException) { args.Cancel = true; }
            finally { deferral.Complete(); }
        };

        _ = LoadAsync();
        try { await dialog.ShowAsync(); }
        finally { cts.Cancel(); }
    }
}
