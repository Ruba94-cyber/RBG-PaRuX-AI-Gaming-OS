using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// Settings card for Local Knowledge beyond plain folders: the search method (hybrid by default, with its reason), websites
/// added as sources (a page or a bounded crawl of one site; only when the person presses Add or Re-sync), and "keep this
/// folder up to date". Everything shown comes from the Core; nothing is fetched or watched until the person asks.
/// </summary>
internal sealed class KnowledgeWebPanel : StackPanel
{
    private static string T(string en, string it) => UiText.Pick(en, it);

    private readonly TextBlock _searchStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _method = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _installModel = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _url = new();
    private readonly ComboBox _mode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _add = new();
    private readonly TextBlock _webStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _webList = new() { Spacing = 6 };
    private readonly StackPanel _watchList = new() { Spacing = 6 };
    private readonly TextBlock _watchStatus = new() { TextWrapping = TextWrapping.Wrap };
    private bool _loading;
    private bool _busy;

    internal KnowledgeWebPanel()
    {
        Spacing = 10;
        AutomationProperties.SetAutomationId(this, "Settings_KnowledgeWeb_Panel");

        // --- search method
        _searchStatus.Text = T("Checking how search works…", "Verifica del funzionamento della ricerca…");
        AutomationProperties.SetAutomationId(_searchStatus, "Settings_KnowledgeSearch_Status");
        AutomationProperties.SetLiveSetting(_searchStatus, AutomationLiveSetting.Polite);
        _method.Header = T("Search method", "Metodo di ricerca");
        _method.Items.Add(new ComboBoxItem { Content = T("Automatic (keyword + meaning when the search model is installed)", "Automatico (parole chiave + significato se il modello di ricerca è installato)"), Tag = "auto" });
        _method.Items.Add(new ComboBoxItem { Content = T("Keyword only", "Solo parole chiave"), Tag = "keyword" });
        _method.SelectionChanged += async (_, _) => await ChangeMethodAsync();
        AutomationProperties.SetAutomationId(_method, "Settings_KnowledgeSearch_Method");
        _installModel.Content = T("Install the search model", "Installa il modello di ricerca");
        _installModel.Click += async (_, _) => await InstallModelAsync();
        AutomationProperties.SetAutomationId(_installModel, "Settings_KnowledgeSearch_Install");

        // --- websites
        var webIntro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Text = T(
                "Add a public website as a knowledge source. PaRuX saves the page text as files, follows the site's robots.txt, stays on that site and stops at fixed page, size and time limits. It fetches only when you press Add or Re-sync, never in the background, and never while strict offline mode or Gaming Mode is on.",
                "Aggiungi un sito pubblico come fonte di conoscenza. PaRuX salva il testo delle pagine come file, rispetta il robots.txt del sito, resta su quel sito e si ferma a limiti fissi di pagine, dimensione e tempo. Scarica solo quando premi Aggiungi o Risincronizza, mai in background, e mai con la modalità offline rigorosa o la modalità Gaming attive."),
        };
        _url.Header = T("Website address", "Indirizzo del sito");
        _url.PlaceholderText = "https://docs.example.com/guide/";
        AutomationProperties.SetAutomationId(_url, "Settings_KnowledgeWeb_Url");
        _mode.Header = T("What to add", "Cosa aggiungere");
        _mode.Items.Add(new ComboBoxItem { Content = T("This page only", "Solo questa pagina"), Tag = "page" });
        _mode.Items.Add(new ComboBoxItem { Content = T("This page and linked pages on the same site (up to 25 pages, 2 levels)", "Questa pagina e le pagine collegate dello stesso sito (fino a 25 pagine, 2 livelli)"), Tag = "crawl" });
        _mode.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_mode, "Settings_KnowledgeWeb_Mode");
        _add.Content = T("Add website", "Aggiungi sito");
        _add.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _add.Click += async (_, _) => await AddAsync();
        AutomationProperties.SetAutomationId(_add, "Settings_KnowledgeWeb_Add");
        AutomationProperties.SetAutomationId(_webStatus, "Settings_KnowledgeWeb_Status");
        AutomationProperties.SetLiveSetting(_webStatus, AutomationLiveSetting.Polite);

        // --- watched folders
        var watchIntro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Text = T(
                "Keep a folder up to date: when files in it change and stay quiet for a few seconds, PaRuX re-indexes it (only what changed). It never runs while Gaming Mode is on.",
                "Mantieni aggiornata una cartella: quando i file al suo interno cambiano e restano fermi per qualche secondo, PaRuX la reindicizza (solo ciò che è cambiato). Non viene eseguito durante la modalità Gaming."),
        };
        AutomationProperties.SetAutomationId(_watchStatus, "Settings_KnowledgeWatch_Status");

        Children.Add(Heading(T("Search method", "Metodo di ricerca")));
        Children.Add(_searchStatus);
        Children.Add(_method);
        Children.Add(_installModel);
        Children.Add(Heading(T("Websites", "Siti web")));
        Children.Add(webIntro);
        Children.Add(_url);
        Children.Add(_mode);
        Children.Add(_add);
        Children.Add(_webStatus);
        Children.Add(_webList);
        Children.Add(Heading(T("Folders kept up to date", "Cartelle mantenute aggiornate")));
        Children.Add(watchIntro);
        Children.Add(_watchStatus);
        Children.Add(_watchList);

        Loaded += async (_, _) => await RefreshAsync();
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };

    internal async Task RefreshAsync()
    {
        _loading = true;
        try
        {
            RetrievalView? r = await KnowledgeWebClient.Current.GetRetrievalAsync(CancellationToken.None);
            if (r != null)
            {
                _searchStatus.Text = DescribeSearch(r);
                _method.SelectedIndex = r.Method == "keyword" ? 1 : 0;
                _installModel.Visibility = r.CanInstallEmbedder ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Log.Write("KnowledgeWebPanel retrieval failed: " + ex.Message);
            _searchStatus.Text = T("Couldn't read the search status: ", "Impossibile leggere lo stato della ricerca: ") + ex.Message;
        }
        finally { _loading = false; }

        await RefreshWebAsync();
        await RefreshWatchAsync();
    }

    // The Core's reasons are English; these cover each state in the person's language.
    private static string DescribeSearch(RetrievalView r) =>
        r.Mode == "hybrid"
            ? T($"Hybrid search is on: keyword matching plus meaning-based matching ({r.EmbedderModel}). An exact code or identifier still wins.",
                $"La ricerca ibrida è attiva: parole chiave più corrispondenza per significato ({r.EmbedderModel}). Un codice o identificatore esatto ha sempre la precedenza.")
            : r.Method == "keyword"
                ? T("Keyword search only, as you chose. A question must share words with the passage.",
                    "Solo ricerca per parole chiave, come hai scelto. Una domanda deve condividere parole con il passaggio.")
                : r.OfflineBlocked
                    ? T("Keyword search only: the search model is not installed and strict offline mode blocks installing it.",
                        "Solo ricerca per parole chiave: il modello di ricerca non è installato e la modalità offline rigorosa ne blocca l'installazione.")
                    : T("Keyword search only: the search model is not installed yet. Install it to add meaning-based matching.",
                        "Solo ricerca per parole chiave: il modello di ricerca non è ancora installato. Installalo per aggiungere la corrispondenza per significato.");

    private async Task ChangeMethodAsync()
    {
        if (_loading || _method.SelectedItem is not ComboBoxItem { Tag: string method }) return;
        try
        {
            await KnowledgeWebClient.Current.SetSearchMethodAsync(method, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Write("KnowledgeWebPanel method failed: " + ex.Message);
            _searchStatus.Text = T("Couldn't change the search method: ", "Impossibile cambiare il metodo di ricerca: ") + ex.Message;
        }
    }

    private async Task InstallModelAsync()
    {
        if (_busy) return;
        _busy = true;
        _installModel.IsEnabled = false;
        _searchStatus.Text = T("Downloading the search model (about 90 MB, checked against a pinned hash)…", "Download del modello di ricerca (circa 90 MB, verificato con un hash fissato)…");
        try
        {
            await EmbeddingClient.Current.InstallEngineAsync(CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Write("KnowledgeWebPanel install failed: " + ex.Message);
            _searchStatus.Text = T("The search model was not installed: ", "Il modello di ricerca non è stato installato: ") + ex.Message;
        }
        finally { _busy = false; _installModel.IsEnabled = true; }
    }

    private static string Reason(KnowledgeWebException ex) => ex.Code switch
    {
        "offline_blocked" => T("Strict offline mode is on, so websites cannot be fetched. Nothing was downloaded.", "La modalità offline rigorosa è attiva, quindi i siti non possono essere scaricati. Non è stato scaricato nulla."),
        "gaming_active" => T("Gaming Mode is active, so PaRuX does not fetch websites. Try again when the game closes.", "La modalità Gaming è attiva, quindi PaRuX non scarica siti. Riprova quando il gioco si chiude."),
        "invalid_address" => T("That address cannot be added. Use a public http:// or https:// address.", "Questo indirizzo non può essere aggiunto. Usa un indirizzo pubblico http:// o https://."),
        _ => ex.Message
    };

    private async Task AddAsync()
    {
        if (_busy) return;
        string url = _url.Text?.Trim() ?? string.Empty;
        if (url.Length == 0) { _webStatus.Text = T("Enter a website address first.", "Inserisci prima l'indirizzo di un sito."); return; }
        _busy = true;
        _add.IsEnabled = false;
        try
        {
            bool crawl = _mode.SelectedItem is ComboBoxItem { Tag: "crawl" };
            await KnowledgeWebClient.Current.AddWebSourceAsync(url, crawl, CancellationToken.None);
            _webStatus.Text = T("Added. PaRuX is fetching and indexing the pages now.", "Aggiunto. PaRuX sta scaricando e indicizzando le pagine.");
            _url.Text = string.Empty;
            await RefreshWebAsync();
            _ = PollWebAsync();
        }
        catch (KnowledgeWebException ex) { _webStatus.Text = Reason(ex); }
        catch (Exception ex)
        {
            Log.Write("KnowledgeWebPanel add failed: " + ex.Message);
            _webStatus.Text = T("Couldn't add the website: ", "Impossibile aggiungere il sito: ") + ex.Message;
        }
        finally { _busy = false; _add.IsEnabled = true; }
    }

    private async Task PollWebAsync()
    {
        for (int i = 0; i < 90; i++)
        {
            await Task.Delay(2000);
            try
            {
                List<WebSourceView> sources = await KnowledgeWebClient.Current.ListWebSourcesAsync(CancellationToken.None);
                Render(sources);
                if (sources.All(s => s.State is not ("crawling" or "indexing"))) return;
            }
            catch (Exception) { return; }
        }
    }

    private async Task RefreshWebAsync()
    {
        try { Render(await KnowledgeWebClient.Current.ListWebSourcesAsync(CancellationToken.None)); }
        catch (Exception ex) { Log.Write("KnowledgeWebPanel list failed: " + ex.Message); }
    }

    private static string StateText(WebSourceView s) => s.State switch
    {
        "crawling" => T("fetching pages…", "scaricamento delle pagine…"),
        "indexing" => T("indexing…", "indicizzazione…"),
        "ready" => T($"ready · {s.Pages} page(s)", $"pronto · {s.Pages} pagina/e"),
        "stopped" => s.Error == "gaming_active" ? T("stopped by Gaming Mode", "interrotto dalla modalità Gaming") : T("stopped", "interrotto"),
        "failed" => T("failed", "non riuscito"),
        _ => T("not fetched yet", "non ancora scaricato")
    };

    private void Render(List<WebSourceView> sources)
    {
        _webList.Children.Clear();
        foreach (WebSourceView s in sources)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string detail = s.KeptEdited > 0 ? T($" · {s.KeptEdited} edited copy kept", $" · {s.KeptEdited} copia modificata conservata") : string.Empty;
            var label = new TextBlock { Text = s.Url + "  ·  " + StateText(s) + detail, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 0);
            var resync = new Button { Content = T("Re-sync", "Risincronizza"), IsEnabled = s.State is not ("crawling" or "indexing") };
            string id = s.Id;
            resync.Click += async (_, _) => await ResyncAsync(id);
            AutomationProperties.SetAutomationId(resync, "Settings_KnowledgeWeb_Resync_" + id);
            Grid.SetColumn(resync, 1);
            var remove = new Button { Content = T("Remove", "Rimuovi") };
            remove.Click += async (_, _) => await RemoveAsync(id);
            AutomationProperties.SetAutomationId(remove, "Settings_KnowledgeWeb_Remove_" + id);
            Grid.SetColumn(remove, 2);
            row.Children.Add(label);
            row.Children.Add(resync);
            row.Children.Add(remove);
            _webList.Children.Add(row);
        }
    }

    private async Task ResyncAsync(string id)
    {
        try
        {
            await KnowledgeWebClient.Current.ResyncAsync(id, CancellationToken.None);
            await RefreshWebAsync();
            _ = PollWebAsync();
        }
        catch (KnowledgeWebException ex) { _webStatus.Text = Reason(ex); }
        catch (Exception ex) { _webStatus.Text = T("Couldn't re-sync: ", "Impossibile risincronizzare: ") + ex.Message; }
    }

    private async Task RemoveAsync(string id)
    {
        try
        {
            await KnowledgeWebClient.Current.RemoveWebSourceAsync(id, CancellationToken.None);
            _webStatus.Text = T("Removed. Pages you edited were kept in the folder.", "Rimosso. Le pagine che hai modificato sono rimaste nella cartella.");
            await RefreshWebAsync();
        }
        catch (Exception ex) { _webStatus.Text = T("Couldn't remove it: ", "Impossibile rimuoverlo: ") + ex.Message; }
    }

    private async Task<bool> ConfirmBroadAsync(FrameworkElement anchor)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = anchor.XamlRoot,
            Title = T("Keep this folder up to date?", "Mantenere aggiornata questa cartella?"),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = T("This folder is broad or holds application data. PaRuX will re-index it whenever its files change. Continue only if that is what you want.",
                    "Questa cartella è ampia o contiene dati di applicazioni. PaRuX la reindicizzerà ogni volta che i suoi file cambiano. Continua solo se è ciò che vuoi.")
            },
            PrimaryButtonText = T("Keep it up to date", "Mantieni aggiornata"),
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task RefreshWatchAsync()
    {
        try
        {
            List<RagSourceDto> sources = await RagClient.Current.ListSourcesAsync(CancellationToken.None);
            List<WebSourceView> web = await KnowledgeWebClient.Current.ListWebSourcesAsync(CancellationToken.None);
            HashSet<string> webIds = web.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
            HashSet<string> watched = await KnowledgeWebClient.Current.WatchedAsync(CancellationToken.None);
            _watchList.Children.Clear();
            List<RagSourceDto> folders = sources.Where(s => !webIds.Contains(s.Id)).ToList();
            _watchStatus.Text = folders.Count == 0 ? T("No indexed folders yet. Add one above.", "Nessuna cartella indicizzata. Aggiungine una sopra.") : string.Empty;
            foreach (RagSourceDto source in folders)
            {
                var toggle = new ToggleSwitch
                {
                    Header = source.FolderPath,
                    IsOn = watched.Contains(source.Id),
                    OnContent = T("Kept up to date", "Mantenuta aggiornata"),
                    OffContent = T("Not watched", "Non monitorata")
                };
                string id = source.Id;
                AutomationProperties.SetAutomationId(toggle, "Settings_KnowledgeWatch_" + id);
                bool initial = true, reverting = false;
                toggle.Toggled += async (_, _) =>
                {
                    if (initial || reverting) return;
                    try
                    {
                        await KnowledgeWebClient.Current.SetWatchedAsync(id, toggle.IsOn, CancellationToken.None);
                        _watchStatus.Text = string.Empty;
                    }
                    catch (KnowledgeWebException ex) when (ex.Code == "consent_required" && toggle.IsOn)
                    {
                        // A whole drive, profile or application-data folder: only after an explicit yes.
                        bool yes = await ConfirmBroadAsync(toggle);
                        try
                        {
                            if (!yes) throw new KnowledgeWebException(string.Empty, "declined");
                            await KnowledgeWebClient.Current.SetWatchedAsync(id, true, CancellationToken.None, consentBroad: true);
                            _watchStatus.Text = string.Empty;
                        }
                        catch (Exception)
                        {
                            reverting = true; toggle.IsOn = false; reverting = false;
                        }
                    }
                    catch (KnowledgeWebException ex)
                    {
                        reverting = true; toggle.IsOn = !toggle.IsOn; reverting = false;
                        _watchStatus.Text = ex.Code == "watch_refused"
                            ? T("This folder cannot be watched (it is missing, or holds private application data or credentials).", "Questa cartella non può essere monitorata (manca, oppure contiene dati privati di applicazioni o credenziali).")
                            : ex.Message;
                    }
                };
                _watchList.Children.Add(toggle);
                initial = false;
            }
        }
        catch (Exception ex) { Log.Write("KnowledgeWebPanel watch list failed: " + ex.Message); }
    }
}
