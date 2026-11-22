using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Document index status and options inside the existing RAG card: per-folder progress, pause/resume, the list of files that
    /// need attention, OCR opt-in, the resource profile and strict-offline. No new page; the Core owns the state.
    /// </summary>
    public sealed partial class SettingsView
    {
        private sealed class KnowledgeRowUi
        {
            public TextBlock Status = null!;
            public ProgressBar Progress = null!;
            public Button PauseResume = null!;
            public Expander Problems = null!;
            public StackPanel ProblemList = null!;
            public string State = "idle";
        }

        private readonly Dictionary<string, KnowledgeRowUi> _knowledgeRows = new Dictionary<string, KnowledgeRowUi>();
        private DispatcherTimer? _knowledgePoll;
        private bool _knowledgeSettingsBuilt;
        private bool _knowledgeSettingsLoading;

        private static string KT(string en, string it) =>
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

        private void AddKnowledgeStatusRows(StackPanel host, PaRuX.Contracts.RagSourceDto source)
        {
            string id = source.Id;
            var ui = new KnowledgeRowUi();
            ui.Status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            ApplyCaptionStyle(ui.Status);
            AutomationProperties.SetAutomationId(ui.Status, "Settings_Rag_Status_" + id);
            ui.Progress = new ProgressBar { IsIndeterminate = false, Value = 0, Maximum = 1, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(ui.Progress, "Settings_Rag_Progress_" + id);
            ui.PauseResume = new Button { Content = KT("Pause", "Pausa"), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(ui.PauseResume, "Settings_Rag_PauseResume_" + id);
            ui.PauseResume.Click += async (s, e) =>
            {
                try
                {
                    if (ui.State == "paused") await KnowledgeClient.Current.ResumeAsync(id, CancellationToken.None);
                    else await KnowledgeClient.Current.PauseAsync(id, CancellationToken.None);
                }
                catch (Exception ex) { ui.Status.Text = ex.Message; }
                await UpdateKnowledgeRowAsync(id, ui);
            };
            ui.ProblemList = new StackPanel { Spacing = 2 };
            AutomationProperties.SetAutomationId(ui.ProblemList, "Settings_Rag_ProblemsList_" + id);
            ui.Problems = new Expander
            {
                Header = KT("Files that need attention", "File da controllare"), Content = ui.ProblemList, Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(ui.Problems, "Settings_Rag_Problems_" + id);
            host.Children.Add(ui.Status);
            host.Children.Add(ui.Progress);
            host.Children.Add(ui.PauseResume);
            host.Children.Add(ui.Problems);
            _knowledgeRows[id] = ui;
            _ = UpdateKnowledgeRowAsync(id, ui);
        }

        private async Task UpdateKnowledgeRowAsync(string id, KnowledgeRowUi ui)
        {
            try
            {
                KnowledgeFullStatus st = await KnowledgeClient.Current.FullStatusAsync(id, CancellationToken.None);
                ui.State = st.State;
                bool running = st.State is "running" or "paused";
                string stateText = st.State switch
                {
                    "running" => KT("Indexing", "Indicizzazione"),
                    "paused" => KT("Paused", "In pausa"),
                    "done" => KT("Up to date", "Aggiornata"),
                    "cancelled" => KT("Stopped", "Interrotta"),
                    "failed" => st.Error == "blocked_sensitive_path" ? KT("Blocked: credential or private folder", "Bloccata: cartella con credenziali o dati privati") : KT("Failed", "Non riuscita"),
                    _ => KT("Not indexed by the document index yet", "Non ancora indicizzata")
                };
                string counts = st.Progress == null ? string.Empty
                    : " · " + st.Progress.FilesSeen + KT(" files seen", " file esaminati") + ", " + st.Progress.Indexed + KT(" indexed", " indicizzati")
                      + ", " + st.Progress.Unchanged + KT(" unchanged", " invariati") + ", " + st.Progress.MetadataOnly + KT(" by name only", " solo per nome")
                      + ", " + st.Progress.Failed + KT(" failed", " falliti") + ", " + st.Progress.Skipped + KT(" skipped", " saltati");
                string location = st.Progress?.CurrentPage is int page && st.Progress.TotalPages is int pages
                    ? " · " + KT("PDF page ", "PDF pagina ") + page.ToString("N0")
                      + "/" + pages.ToString("N0")
                    : st.Progress?.Current is { Length: > 0 } current
                        ? " · " + current : string.Empty;
                ui.Status.Text = stateText + " · " + st.Files + KT(" documents", " documenti")
                    + ", " + st.Chunks + KT(" passages", " passaggi") + counts + location;
                ui.Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
                // Only claim a real percentage when the PDF parser supplies
                // concrete page/total counts. Folder traversal has no total.
                bool pageProgress = running && st.Progress?.CurrentPage is > 0
                    && st.Progress?.TotalPages is > 0;
                ui.Progress.IsIndeterminate = running && !pageProgress;
                if (pageProgress)
                {
                    ui.Progress.Maximum = st.Progress!.TotalPages!.Value;
                    ui.Progress.Value = st.Progress.CurrentPage!.Value;
                }
                ui.PauseResume.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
                ui.PauseResume.Content = st.State == "paused" ? KT("Resume", "Riprendi") : KT("Pause", "Pausa");

                ui.ProblemList.Children.Clear();
                foreach (KnowledgeProblem problem in st.Problems.Take(200))
                {
                    var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = problem.Kind + " · " + problem.RelativePath + " — " + problem.Reason };
                    ApplyCaptionStyle(line);
                    ui.ProblemList.Children.Add(line);
                }
                ui.Problems.Visibility = st.Problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                ui.Problems.Header = KT("Files that need attention", "File da controllare") + " (" + st.Problems.Count + ")";
                if (running) EnsureKnowledgePoll();
            }
            catch (Exception ex) { Log.Write("UpdateKnowledgeRowAsync failed: " + ex.Message); }
        }

        private void EnsureKnowledgePoll()
        {
            if (_knowledgePoll == null)
            {
                _knowledgePoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _knowledgePoll.Tick += async (s, e) =>
                {
                    bool any = false;
                    foreach (KeyValuePair<string, KnowledgeRowUi> row in _knowledgeRows.ToList())
                    {
                        await UpdateKnowledgeRowAsync(row.Key, row.Value);
                        any |= row.Value.State is "running" or "paused";
                    }
                    if (!any) _knowledgePoll?.Stop();
                };
                Unloaded += (s, e) => _knowledgePoll?.Stop();
            }
            if (!_knowledgePoll.IsEnabled) _knowledgePoll.Start();
        }

        /// <summary>OCR opt-in, resource profile and strict offline. Built once; values come from the Core.</summary>
        private async Task EnsureKnowledgeSettingsPanelAsync()
        {
            if (KnowledgeSettingsPanel == null || _knowledgeSettingsBuilt) return;
            _knowledgeSettingsBuilt = true;
            _knowledgeSettingsLoading = true;
            try
            {
                KnowledgeSettingsView s = await KnowledgeClient.Current.GetSettingsAsync(probeOcr: true, CancellationToken.None);

                var ocr = new ToggleSwitch { Header = KT("Read text in images and scanned PDFs (OCR)", "Leggi il testo in immagini e PDF scansionati (OCR)"), IsOn = s.OcrEnabled };
                AutomationProperties.SetAutomationId(ocr, "Settings_Rag_Ocr_Switch");
                var ocrNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(ocrNote);
                AutomationProperties.SetAutomationId(ocrNote, "Settings_Rag_Ocr_Note");
                ocrNote.Text = s.OcrAvailable == true
                    ? KT("Off by default. Uses Windows OCR on this PC (languages: ", "Disattivato di default. Usa l'OCR di Windows su questo PC (lingue: ") + s.OcrLanguages + "). " + KT("Nothing leaves the PC.", "Nulla esce dal PC.")
                    : KT("Off by default. Windows OCR is not available here: ", "Disattivato di default. L'OCR di Windows non è disponibile qui: ") + (s.OcrReason ?? "?")
                      + KT(" Images and scanned PDFs stay searchable by name only.", " Immagini e PDF scansionati restano ricercabili solo per nome.");
                ocr.Toggled += async (a, b) =>
                {
                    if (_knowledgeSettingsLoading) return;
                    try { await KnowledgeClient.Current.SaveSettingsAsync(ocr.IsOn, null, null, CancellationToken.None); }
                    catch (Exception ex) { ocrNote.Text = ex.Message; }
                };

                var profile = new ComboBox { Header = KT("Indexing effort", "Impegno dell'indicizzazione"), MinWidth = 260 };
                foreach ((string tag, string label) in new[]
                {
                    ("auto", KT("Automatic (recommended: ", "Automatico (consigliato: ") + s.RecommendedProfile + ")"),
                    ("Eco", KT("Eco - light, slow, for 16 GB PCs", "Eco - leggero, lento, per PC da 16 GB")),
                    ("Balanced", KT("Balanced", "Bilanciato")),
                    ("Performance", KT("Performance - fastest", "Prestazioni - più veloce"))
                })
                    profile.Items.Add(new ComboBoxItem { Tag = tag, Content = label });
                profile.SelectedIndex = s.ProfileIsAutomatic ? 0 : Math.Max(0, new[] { "auto", "Eco", "Balanced", "Performance" }.ToList().IndexOf(s.Profile));
                AutomationProperties.SetAutomationId(profile, "Settings_Rag_Profile_ComboBox");
                var profileNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(profileNote);
                profileNote.Text = KT("Current plan: ", "Piano attuale: ") + s.Profile + " · " + s.EmbeddingThreads + KT(" thread(s), memory budget ", " thread, budget memoria ")
                    + s.MemoryBudgetMb + " MB · " + KT("indexing runs at low priority and unloads the search model when idle.", "l'indicizzazione gira a bassa priorità e scarica il modello di ricerca quando è inattivo.");
                profile.SelectionChanged += async (a, b) =>
                {
                    if (_knowledgeSettingsLoading || profile.SelectedItem is not ComboBoxItem item) return;
                    try { await KnowledgeClient.Current.SaveSettingsAsync(null, (string)item.Tag, null, CancellationToken.None); }
                    catch (Exception ex) { profileNote.Text = ex.Message; }
                };

                var offline = new ToggleSwitch
                {
                    Header = KT("Strict offline (block model downloads)", "Solo offline (blocca i download dei modelli)"),
                    IsOn = s.StrictOffline, IsEnabled = !s.CorporateOffline
                };
                AutomationProperties.SetAutomationId(offline, "Settings_Rag_StrictOffline_Switch");
                var offlineNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(offlineNote);
                offlineNote.Text = s.CorporateOffline
                    ? KT("Always on in the Corporate edition.", "Sempre attivo nell'edizione Corporate.")
                    : KT("When on, the semantic-search and reranker models cannot be downloaded. Downloads are checked against a pinned SHA-256 before use.",
                        "Se attivo, i modelli di ricerca semantica e di riordino non possono essere scaricati. I download sono verificati con SHA-256 prima dell'uso.");
                offline.Toggled += async (a, b) =>
                {
                    if (_knowledgeSettingsLoading) return;
                    try { await KnowledgeClient.Current.SaveSettingsAsync(null, null, offline.IsOn, CancellationToken.None); }
                    catch (Exception ex) { offlineNote.Text = ex.Message; }
                };

                var wipe = new Button { Content = KT("Delete everything (secure)", "Elimina tutto (sicuro)") };
                AutomationProperties.SetAutomationId(wipe, "Settings_Rag_DeleteEverything_Button");
                var wipeNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(wipeNote);
                AutomationProperties.SetAutomationId(wipeNote, "Settings_Rag_DeleteEverything_Note");
                wipeNote.Text = KT("Removes every indexed folder and wipes the search index (text, word and partial-match entries, vectors, database files). Your documents are not touched.",
                    "Rimuove ogni cartella indicizzata e cancella l'indice di ricerca (testo, voci per parole e corrispondenze parziali, vettori, file del database). I tuoi documenti non vengono toccati.");
                wipe.Click += async (a, b) =>
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot, Title = KT("Delete the whole document index?", "Eliminare tutto l'indice dei documenti?"),
                        Content = new TextBlock { Text = wipeNote.Text, TextWrapping = TextWrapping.Wrap },
                        PrimaryButtonText = KT("Delete everything", "Elimina tutto"), CloseButtonText = KT("Cancel", "Annulla"), DefaultButton = ContentDialogButton.Close
                    };
                    AutomationProperties.SetAutomationId(dialog, "Settings_Rag_DeleteEverything_Dialog");
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                    try
                    {
                        wipe.IsEnabled = false;
                        (int files, int chunks, bool onDisk) = await KnowledgeClient.Current.DeleteEverythingAsync(CancellationToken.None);
                        wipeNote.Text = KT("Deleted ", "Eliminati ") + files + KT(" files / ", " file / ") + chunks + KT(" passages from the index. ", " passaggi dall'indice. ")
                            + (onDisk ? KT("Database files were zeroed and removed.", "I file del database sono stati azzerati e rimossi.") : KT("Some database files could not be removed (in use).", "Alcuni file del database non sono stati rimossi (in uso)."));
                        await RefreshRagSourcesAsync();
                    }
                    catch (Exception ex) { wipeNote.Text = KT("Could not delete: ", "Eliminazione non riuscita: ") + ex.Message; }
                    finally { wipe.IsEnabled = true; }
                };

                KnowledgeSettingsPanel.Children.Clear();
                foreach (UIElement e in new UIElement[] { ocr, ocrNote, profile, profileNote, offline, offlineNote }.Concat(await BuildRetrievalControlsAsync()).Concat(new UIElement[] { wipe, wipeNote })) KnowledgeSettingsPanel.Children.Add(e);
            }
            catch (Exception ex)
            {
                _knowledgeSettingsBuilt = false;
                Log.Write("EnsureKnowledgeSettingsPanelAsync failed: " + ex.Message);
            }
            finally { _knowledgeSettingsLoading = false; }
        }

        private static string ModeLabel(string mode) => mode switch
        {
            "keyword" => KT("Keyword only", "Solo parole chiave"),
            "embedding" => KT("Meaning only (embeddings)", "Solo significato (embedding)"),
            _ => KT("Hybrid (keyword + meaning)", "Ibrida (parole chiave + significato)")
        };

        /// <summary>Embedder picker, per-workspace retrieval mode, bounded re-embed and the in-app retrieval check. The Core owns every state.</summary>
        private async Task<UIElement[]> BuildRetrievalControlsAsync()
        {
            const string workspace = "default";
            bool loading = true;
            var embedder = new ComboBox { Header = KT("Search model (embedder)", "Modello di ricerca (embedder)"), MinWidth = 260 };
            AutomationProperties.SetAutomationId(embedder, "Settings_Rag_Embedder_ComboBox");
            var mode = new ComboBox { Header = KT("Retrieval mode", "Modalità di recupero"), MinWidth = 260 };
            foreach (string m in new[] { "hybrid", "embedding", "keyword" }) mode.Items.Add(new ComboBoxItem { Tag = m, Content = ModeLabel(m) });
            AutomationProperties.SetAutomationId(mode, "Settings_Rag_RetrievalMode_ComboBox");
            var note = new TextBlock { TextWrapping = TextWrapping.Wrap };
            ApplyCaptionStyle(note);
            AutomationProperties.SetAutomationId(note, "Settings_Rag_RetrievalNote");
            var reembed = new Button { Content = KT("Re-embed index", "Rigenera i vettori dell’indice"), Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(reembed, "Settings_Rag_Reembed_Button");
            var cancel = new Button { Content = KT("Cancel", "Annulla"), Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(cancel, "Settings_Rag_ReembedCancel_Button");
            var reembedStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
            ApplyCaptionStyle(reembedStatus);
            AutomationProperties.SetAutomationId(reembedStatus, "Settings_Rag_ReembedStatus");
            var bench = new Button { Content = KT("Check retrieval quality", "Verifica la qualità del recupero") };
            AutomationProperties.SetAutomationId(bench, "Settings_Rag_Benchmark_Button");
            var benchResult = new TextBlock { TextWrapping = TextWrapping.Wrap };
            ApplyCaptionStyle(benchResult);
            AutomationProperties.SetAutomationId(benchResult, "Settings_Rag_Benchmark_Result");
            benchResult.Text = KT("Runs a short test on a built-in synthetic corpus (no personal documents) and compares keyword, meaning and hybrid search.",
                "Esegue un breve test su un corpus sintetico incluso (nessun documento personale) e confronta ricerca per parole chiave, per significato e ibrida.");
            DispatcherTimer? poll = null;

            void Apply(KnowledgeRetrievalPlanView plan)
            {
                loading = true;
                embedder.Items.Clear();
                foreach (KnowledgeEmbedderView o in plan.Embedders)
                    embedder.Items.Add(new ComboBoxItem { Tag = o.Id, Content = o.Label + " (" + o.Dimensions + ")" + (o.Installed ? string.Empty : KT(" - not installed", " - non installato")) });
                embedder.SelectedIndex = Math.Max(0, plan.Embedders.ToList().FindIndex(o => o.Selected));
                embedder.IsEnabled = plan.Embedders.Count > 0 && plan.ReembedState != "running";
                mode.SelectedIndex = Math.Max(0, new[] { "hybrid", "embedding", "keyword" }.ToList().IndexOf(plan.RequestedMode));
                mode.IsEnabled = plan.ReembedState != "running";
                string text = KT("Search now: ", "Ricerca attuale: ") + ModeLabel(plan.EffectiveMode)
                    + (plan.RerankerInstalled ? KT(" · local reranker on", " · riordino locale attivo") : KT(" · local reranker not installed", " · riordino locale non installato"));
                if (plan.DegradedReason != null)
                    text += "\n" + KT("Not as chosen: ", "Non come scelto: ") + plan.DegradedReason;
                if (plan.TotalChunks > 0)
                    text += "\n" + plan.CurrentVectors + "/" + plan.TotalChunks + KT(" passages have a vector from this model", " passaggi hanno un vettore di questo modello")
                        + (plan.StaleVectors > 0 ? " · " + plan.StaleVectors + KT(" from another model (kept, not used)", " da un altro modello (conservati, non usati)") : string.Empty);
                note.Text = text;
                bool running = plan.ReembedState == "running";
                reembed.Visibility = plan.NeedsReembed && !running ? Visibility.Visible : Visibility.Collapsed;
                cancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
                reembedStatus.Text = plan.ReembedState switch
                {
                    "running" => KT("Re-embedding: ", "Rigenerazione: ") + plan.ReembedProcessed + "/" + plan.ReembedTotal,
                    "cancelled" => KT("Re-embed stopped. Nothing was lost; run it again to finish.", "Rigenerazione interrotta. Nulla è andato perso; riavviala per completare."),
                    "partial" => KT("Re-embed reached its limit; ", "La rigenerazione ha raggiunto il limite; ") + plan.ReembedRemaining + KT(" passages remain.", " passaggi rimasti."),
                    "recovered" => KT("The index file was damaged and was moved aside (not deleted). Re-index your folders.", "Il file dell’indice era danneggiato ed è stato spostato (non eliminato). Reindicizza le cartelle."),
                    "failed" => KT("Re-embed failed.", "Rigenerazione non riuscita."),
                    _ => plan.NeedsReembed ? KT("The index has passages without a vector from this model. Re-embed to use meaning search on all of them.",
                        "L’indice ha passaggi senza vettore di questo modello. Rigenera per usare la ricerca per significato su tutti.") : string.Empty
                };
                loading = false;
                if (running && poll == null)
                {
                    poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    poll.Tick += async (a, b) =>
                    {
                        try
                        {
                            KnowledgeRetrievalPlanView p = await KnowledgeClient.Current.GetRetrievalPlanAsync(workspace, CancellationToken.None);
                            Apply(p);
                            if (p.ReembedState != "running") { poll?.Stop(); poll = null; }
                        }
                        catch (Exception) { poll?.Stop(); poll = null; }
                    };
                    Unloaded += (a, b) => poll?.Stop();
                    poll.Start();
                }
            }

            async Task Change(string? newMode, string? newEmbedder)
            {
                try { Apply(await KnowledgeClient.Current.SetRetrievalAsync(workspace, newMode, newEmbedder, CancellationToken.None)); }
                catch (Exception ex) { note.Text = KT("Could not change the setting: ", "Impossibile cambiare l’impostazione: ") + ex.Message; }
            }
            embedder.SelectionChanged += async (a, b) => { if (!loading && embedder.SelectedItem is ComboBoxItem { Tag: string id }) await Change(null, id); };
            mode.SelectionChanged += async (a, b) => { if (!loading && mode.SelectedItem is ComboBoxItem { Tag: string m }) await Change(m, null); };
            reembed.Click += async (a, b) =>
            {
                try
                {
                    await KnowledgeClient.Current.StartReembedAsync(CancellationToken.None);
                    Apply(await KnowledgeClient.Current.GetRetrievalPlanAsync(workspace, CancellationToken.None));
                }
                catch (Exception ex) { reembedStatus.Text = KT("Could not start: ", "Impossibile avviare: ") + ex.Message; }
            };
            cancel.Click += async (a, b) =>
            {
                try { await KnowledgeClient.Current.CancelReembedAsync(CancellationToken.None); } catch (Exception ex) { reembedStatus.Text = ex.Message; }
            };
            bench.Click += async (a, b) =>
            {
                bench.IsEnabled = false;
                benchResult.Text = KT("Running the check...", "Verifica in corso...");
                try
                {
                    IReadOnlyList<KnowledgeBenchmarkRow> rows = await KnowledgeClient.Current.RunBenchmarkAsync(CancellationToken.None);
                    benchResult.Text = string.Join("\n", rows.Select(r => ModeLabel(r.Mode) + ": " + KT("precision ", "precisione ") + r.Precision.ToString("0.00") + ", " + KT("recall ", "richiamo ") + r.Recall.ToString("0.00")
                        + ", MRR " + r.Mrr.ToString("0.00") + ", " + KT("latency ", "latenza ") + r.MedianLatencyMs.ToString("0.0") + " ms (p95 " + r.P95LatencyMs.ToString("0.0") + "), "
                        + r.DocumentsPerSecond.ToString("0") + KT(" docs/s", " doc/s") + (r.Note != null ? " - " + r.Note : string.Empty)));
                }
                catch (Exception ex) { benchResult.Text = KT("The check could not run (it is unavailable during Gaming Mode): ", "La verifica non è partita (non è disponibile durante la modalità Gaming): ") + ex.Message; }
                finally { bench.IsEnabled = true; }
            };

            try { Apply(await KnowledgeClient.Current.GetRetrievalPlanAsync(workspace, CancellationToken.None)); }
            catch (Exception ex) { Log.Write("BuildRetrievalControlsAsync failed: " + ex.Message); note.Text = KT("Search status unavailable: ", "Stato della ricerca non disponibile: ") + ex.Message; loading = false; }
            return new UIElement[] { embedder, mode, note, reembed, cancel, reembedStatus, bench, benchResult };
        }
    }
}
