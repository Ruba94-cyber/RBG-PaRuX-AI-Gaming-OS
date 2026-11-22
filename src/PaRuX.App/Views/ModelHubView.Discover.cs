using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// The Model Hub "Discover" tab: a curated, searchable catalogue grouped by task, with a fit badge computed from the real
    /// detected hardware, a speed note from this PC's own benchmarks, a "Recommended for your PC" pick, and the download state of
    /// every card (progress, pause/resume, cancel, plain-language failure and its fix). It reuses the Hub's existing
    /// consent-gated, licence-reviewed, hash-verified download path; it adds no second way to download. Model Hub still owns models.
    /// All ranking, grouping, fit, disk and wording decisions live in <see cref="ModelDiscoverPolicy"/>.
    /// </summary>
    public sealed partial class ModelHubView
    {
        private readonly StackPanel _discoverBody = new StackPanel { Spacing = 14 };
        private readonly TextBlock _discoverHardware = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78 };
        private readonly TextBlock _discoverOnlineNote = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 };
        private readonly TextBlock _discoverStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel _discoverHero = new StackPanel { Spacing = 8 };
        private readonly StackPanel _discoverFilters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        private readonly StackPanel _discoverGroups = new StackPanel { Spacing = 16 };
        private readonly TextBox _discoverSearch = new TextBox { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly Button _discoverSearchButton = new Button { MinHeight = 44, MinWidth = 80 };
        private readonly Dictionary<string, DiscoverCardView> _discoverCards = new Dictionary<string, DiscoverCardView>(StringComparer.Ordinal);
        private List<RecommendedCheckpoint> _discoverMediaOffers = new List<RecommendedCheckpoint>();
        private List<ModelSearchResult> _discoverOnlineResults = new List<ModelSearchResult>();
        private List<DiscoverEntry> _discoverEntries = new List<DiscoverEntry>();
        private readonly Dictionary<string, RecommendedModel> _discoverTextSource = new Dictionary<string, RecommendedModel>(StringComparer.Ordinal);
        private readonly Dictionary<string, RecommendedCheckpoint> _discoverMediaSource = new Dictionary<string, RecommendedCheckpoint>(StringComparer.Ordinal);
        private DiscoverTask? _discoverFilter;
        private bool _discoverInternetOn;
        private bool _discoverInternetKnown;
        private DispatcherTimer? _discoverPoll;
        private bool _discoverPollBusy;
        private bool _discoverSearching;

        private sealed class DiscoverCardView
        {
            public Border Root = null!;
            public TextBlock Title = null!, Facts = null!, Description = null!, Fit = null!, Speed = null!, State = null!, Fix = null!;
            public ProgressBar Bar = null!;
            public Button Primary = null!, Cancel = null!;
            public DiscoverEntry Entry = null!;
            public ModelDiscoverPolicy.CardAction Action;
            public ModelDownloadStatus? Job;
        }

        private UIElement BuildDiscoverPanel()
        {
            bool it = StartItalian;
            var panel = new StackPanel { Spacing = 14 };
            AutomationProperties.SetAutomationId(panel, "ModelHub_Discover_Panel");

            var heading = new TextBlock
            {
                Text = T("Discover models", "Scopri i modelli"),
                FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            panel.Children.Add(heading);
            panel.Children.Add(new TextBlock
            {
                Text = T("Pick a task, see how each model runs on this PC, and download it with one click. Everything is checked against your real graphics card and memory, and nothing downloads without your OK.",
                    "Scegli un compito, guarda come gira ogni modello su questo PC e scaricalo con un clic. Tutto è verificato sulla tua scheda grafica e sulla tua memoria reali, e nulla viene scaricato senza il tuo OK."),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.82
            });
            AutomationProperties.SetAutomationId(_discoverHardware, "ModelHub_Discover_Hardware");
            panel.Children.Add(_discoverHardware);

            AutomationProperties.SetAutomationId(_discoverHero, "ModelHub_Discover_Recommended");
            panel.Children.Add(_discoverHero);

            _discoverSearch.PlaceholderText = it ? "Cerca un modello, un marchio o un compito" : "Search a model, a brand or a task";
            AutomationProperties.SetAutomationId(_discoverSearch, "ModelHub_Discover_Search");
            AutomationProperties.SetName(_discoverSearch, T("Search models", "Cerca modelli"));
            _discoverSearch.TextChanged += (_, _) => RenderDiscover();
            _discoverSearch.KeyDown += async (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter) await DiscoverSearchOnlineAsync();
            };
            _discoverSearchButton.Content = T("Search online", "Cerca online");
            AutomationProperties.SetAutomationId(_discoverSearchButton, "ModelHub_Discover_Search_Button");
            _discoverSearchButton.Click += async (_, _) => await DiscoverSearchOnlineAsync();
            var searchRow = new Grid { ColumnSpacing = 8 };
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_discoverSearch, 0); searchRow.Children.Add(_discoverSearch);
            Grid.SetColumn(_discoverSearchButton, 1); searchRow.Children.Add(_discoverSearchButton);
            panel.Children.Add(searchRow);
            AutomationProperties.SetAutomationId(_discoverOnlineNote, "ModelHub_Discover_Online_Note");
            panel.Children.Add(_discoverOnlineNote);

            var filterScroll = new ScrollViewer
            {
                HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _discoverFilters
            };
            AutomationProperties.SetAutomationId(_discoverFilters, "ModelHub_Discover_Filters");
            panel.Children.Add(filterScroll);

            AutomationProperties.SetAutomationId(_discoverStatus, "ModelHub_Discover_Status");
            panel.Children.Add(_discoverStatus);
            AutomationProperties.SetAutomationId(_discoverGroups, "ModelHub_Discover_Groups");
            panel.Children.Add(_discoverGroups);
            Unloaded += (_, _) => _discoverPoll?.Stop();
            RenderDiscover();
            return panel;
        }

        /// <summary>Selects the Discover tab (used by the Chat model picker).</summary>
        internal void ShowDiscoverTab() => ShowHubTab(ModelHubTab.Discover);

        // ---- Data -> entries -------------------------------------------------------------------------------------------

        private bool DiscoverGpuUsable => _hardware?.GpuDetected == true;

        private DiscoverFit DiscoverFitFor(long sizeBytes)
        {
            if (sizeBytes <= 0 || _hardware == null) return DiscoverFit.Unknown;
            try { return ModelDiscoverPolicy.FitFromEstimate(ModelHardwareFitEstimator.Estimate(sizeBytes, _hardware), DiscoverGpuUsable); }
            catch { return DiscoverFit.Unknown; }
        }

        private List<DiscoverEntry> BuildDiscoverEntries()
        {
            var entries = new List<DiscoverEntry>();
            _discoverTextSource.Clear();
            _discoverMediaSource.Clear();
            var benchmarks = _managedModels.Where(ChatModelPickerPolicy.HasMeasuredSpeed)
                .Select(m => new DiscoverBenchmarkPoint(m.SizeBytes, m.LastBenchmarkTokensPerSecond!.Value)).ToList();

            foreach (RecommendedModel model in _startRecommended)
            {
                if (string.IsNullOrWhiteSpace(model.HuggingFaceRepoId)) continue;
                DiscoverTask task = ModelDiscoverPolicy.Classify(model.Category, model.CapabilityIds, model.ContentProfileId);
                DiscoverFit fit = DiscoverFitFor(model.EstimatedSizeBytes);
                ManagedModelInfo? installed = _managedModels.FirstOrDefault(m =>
                    string.Equals(m.RepoId, model.HuggingFaceRepoId, StringComparison.OrdinalIgnoreCase)
                    || ModelDiscoverPolicy.JobBelongsToRepo(m.FileName, model.HuggingFaceRepoId));
                (double? tps, bool measured) = ModelDiscoverPolicy.SpeedFor(model.EstimatedSizeBytes, fit,
                    installed?.LastBenchmarkTokensPerSecond, benchmarks);
                string id = "rec:" + model.Id;
                _discoverTextSource[id] = model;
                entries.Add(new DiscoverEntry(id, model.Title, model.Description, model.HuggingFaceRepoId, task, true,
                    model.EstimatedSizeBytes, model.QuantHint, model.ApproxParams, 0, fit, tps, measured, installed != null, false));
            }

            foreach (RecommendedCheckpoint checkpoint in _discoverMediaOffers)
            {
                DiscoverTask task = ModelDiscoverPolicy.Classify(null, new[] { checkpoint.CapabilityId }, null);
                if (task is not (DiscoverTask.Image or DiscoverTask.Video)) task = checkpoint.Type == "video" ? DiscoverTask.Video : DiscoverTask.Image;
                bool done = _downloadJobs.Any(j => j.Status == "completed"
                    && string.Equals(j.FileName, checkpoint.FileName, StringComparison.OrdinalIgnoreCase));
                string id = "media:" + checkpoint.Id;
                _discoverMediaSource[id] = checkpoint;
                entries.Add(new DiscoverEntry(id, checkpoint.Title, checkpoint.Description, checkpoint.HuggingFaceRepoId, task, true,
                    checkpoint.EstimatedSizeBytes, string.Empty, string.Empty, 0, DiscoverFitFor(checkpoint.EstimatedSizeBytes),
                    null, false, done, false));
            }

            foreach (ModelSearchResult result in _discoverOnlineResults)
            {
                if (string.IsNullOrWhiteSpace(result.RepoId)) continue;
                ModelQuantChoice? choice = result.QuantChoices.Where(c => c.Complete && c.SizeBytes > 0)
                    .OrderBy(c => string.Equals(c.Quantization, "Q4_K_M", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(c => c.SizeBytes).FirstOrDefault();
                long size = choice?.SizeBytes ?? 0;
                DiscoverFit fit = DiscoverFitFor(size);
                string repoName = result.RepoId.Split('/').Last();
                string description = (string.IsNullOrWhiteSpace(result.Author) ? string.Empty : result.Author + " · ")
                    + result.Downloads.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + T(" downloads", " download");
                string id = "hf:" + result.RepoId;
                _discoverTextSource[id] = new RecommendedModel
                {
                    Id = id, Title = repoName, HuggingFaceRepoId = result.RepoId, QuantHint = choice?.Quantization ?? "Q4_K_M",
                    Category = result.Category
                };
                bool installed = _managedModels.Any(m => string.Equals(m.RepoId, result.RepoId, StringComparison.OrdinalIgnoreCase));
                entries.Add(new DiscoverEntry(id, repoName, description, result.RepoId,
                    ModelDiscoverPolicy.Classify(result.Category, null, null), false, size, choice?.Quantization ?? string.Empty,
                    string.Empty, 0, fit, null, false, installed, true));
            }
            return entries;
        }

        // ---- Rendering --------------------------------------------------------------------------------------------------

        private void RenderDiscover()
        {
            bool it = StartItalian;
            _discoverHardware.Text = _hardwareSummary.Text;
            _discoverEntries = BuildDiscoverEntries();
            IReadOnlyList<DiscoverEntry> shown = ModelDiscoverPolicy.Search(_discoverEntries, _discoverSearch.Text);

            RenderDiscoverOnlineNote();
            RenderDiscoverHero();
            RenderDiscoverFilters(ModelDiscoverPolicy.Group(shown));

            _discoverGroups.Children.Clear();
            _discoverCards.Clear();
            IReadOnlyList<DiscoverGroup> groups = ModelDiscoverPolicy.Group(shown, _discoverFilter);
            if (groups.Count == 0)
            {
                _discoverStatus.Text = _startRecommended.Count == 0 && _discoverEntries.Count == 0
                    ? T("The catalogue is still loading. If this stays empty, open Chat models and tap Refresh.",
                        "Il catalogo si sta caricando. Se resta vuoto, apri Modelli di chat e tocca Aggiorna.")
                    : T("No model matches. Try another word or choose All.", "Nessun modello corrisponde. Prova un'altra parola oppure scegli Tutti.");
                return;
            }
            _discoverStatus.Text = string.Empty;
            foreach (DiscoverGroup group in groups)
            {
                var section = new StackPanel { Spacing = 8 };
                var title = new TextBlock
                {
                    Text = ModelDiscoverPolicy.TaskTitle(group.Task, it),
                    FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                };
                AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level3);
                section.Children.Add(title);
                string hint = ModelDiscoverPolicy.TaskHint(group.Task, it);
                if (hint.Length > 0)
                    section.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 });
                foreach (DiscoverEntry entry in group.Entries)
                {
                    DiscoverCardView card = BuildDiscoverCard(entry);
                    _discoverCards[entry.Id] = card;
                    section.Children.Add(card.Root);
                }
                _discoverGroups.Children.Add(section);
            }
        }

        private void RenderDiscoverOnlineNote()
        {
            if (!_discoverInternetKnown)
            {
                _discoverOnlineNote.Text = T("Curated models work offline. Checking the Internet switch…", "I modelli selezionati funzionano offline. Controllo l'interruttore Internet…");
                _discoverSearchButton.IsEnabled = false;
            }
            else if (_discoverInternetOn)
            {
                _discoverOnlineNote.Text = T("Type a name and press Search online to look through Hugging Face. Results there are not reviewed by PaRuX.",
                    "Scrivi un nome e premi Cerca online per cercare su Hugging Face. I risultati lì non sono controllati da PaRuX.");
                _discoverSearchButton.IsEnabled = !_discoverSearching && !(CorporateMode.IsActive && !CorporateMode.ModelDownloadsAllowed);
            }
            else
            {
                _discoverOnlineNote.Text = T("The Internet switch is off, so only the curated list is shown. Turn it on in Settings to search Hugging Face.",
                    "L'interruttore Internet è spento, quindi vedi solo l'elenco selezionato. Accendilo nelle Impostazioni per cercare su Hugging Face.");
                _discoverSearchButton.IsEnabled = false;
            }
        }

        private void RenderDiscoverHero()
        {
            _discoverHero.Children.Clear();
            DiscoverEntry? pick = ModelDiscoverPolicy.RecommendForThisPc(_discoverEntries);
            if (pick == null)
            {
                bool installedChat = _discoverEntries.Any(e => e.Task == DiscoverTask.Chat && e.Installed);
                if (installedChat)
                    _discoverHero.Children.Add(new TextBlock
                    {
                        Text = T("You already have a chat model that suits this PC. Browse below to add more.",
                            "Hai già un modello di chat adatto a questo PC. Sfoglia qui sotto per aggiungerne altri."),
                        TextWrapping = TextWrapping.Wrap
                    });
                return;
            }
            string fitWords = ModelDiscoverPolicy.FitLabel(pick.Fit, StartItalian);
            var heading = new TextBlock
            {
                Text = T("Recommended for your PC", "Consigliato per il tuo PC"),
                FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
            var line = new TextBlock
            {
                Text = pick.Title + " · " + ModelDiscoverPolicy.Facts(pick, StartItalian) + " · " + ModelDiscoverPolicy.FitGlyph(pick.Fit) + " " + fitWords,
                TextWrapping = TextWrapping.Wrap
            };
            var go = new Button { Content = T("Download this model", "Scarica questo modello"), MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(go, "ModelHub_Discover_Recommended_Download");
            AutomationProperties.SetName(go, T("Download the recommended model ", "Scarica il modello consigliato ") + pick.Title);
            go.Click += async (_, _) => await DiscoverDownloadAsync(pick, go);
            _discoverHero.Children.Add(new Border
            {
                Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                Child = new StackPanel { Spacing = 8, Children = { heading, line, go } }
            });
        }

        private void RenderDiscoverFilters(IReadOnlyList<DiscoverGroup> available)
        {
            _discoverFilters.Children.Clear();
            if (_discoverFilter is DiscoverTask current && available.All(g => g.Task != current)) _discoverFilter = null;
            AddFilterButton(null, T("All", "Tutti"), "ModelHub_Discover_Filter_All");
            foreach (DiscoverGroup group in available)
                AddFilterButton(group.Task, ModelDiscoverPolicy.TaskTitle(group.Task, StartItalian), ModelDiscoverPolicy.TaskAutomationId(group.Task));

            void AddFilterButton(DiscoverTask? task, string label, string automationId)
            {
                var button = new ToggleButton { Content = label, IsChecked = _discoverFilter == task, MinHeight = 36 };
                AutomationProperties.SetAutomationId(button, automationId);
                AutomationProperties.SetName(button, T("Show ", "Mostra ") + label);
                button.Click += (_, _) => { _discoverFilter = task; RenderDiscover(); };
                _discoverFilters.Children.Add(button);
            }
        }

        private DiscoverCardView BuildDiscoverCard(DiscoverEntry entry)
        {
            var v = new DiscoverCardView { Entry = entry };
            v.Title = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            v.Facts = new TextBlock { FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            v.Description = new TextBlock { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
            v.Fit = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            v.Speed = new TextBlock { FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
            v.State = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            v.Fix = new TextBlock { FontSize = 12, Opacity = 0.9, TextWrapping = TextWrapping.Wrap };
            v.Bar = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
            v.Primary = new Button { MinHeight = 44, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Right };
            v.Cancel = new Button { MinHeight = 36, Content = T("Cancel", "Annulla"), HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(v.Primary, "ModelHub_Discover_Action_" + entry.Id);
            AutomationProperties.SetAutomationId(v.Cancel, "ModelHub_Discover_Cancel_" + entry.Id);
            AutomationProperties.SetAutomationId(v.Bar, "ModelHub_Discover_Progress_" + entry.Id);
            v.Primary.Click += async (_, _) => await DiscoverPrimaryAsync(v);
            v.Cancel.Click += async (_, _) => await DiscoverCancelAsync(v);

            var text = new StackPanel { Spacing = 3 };
            foreach (UIElement e in new UIElement[] { v.Title, v.Facts, v.Description, v.Fit, v.Speed, v.State, v.Fix, v.Bar }) text.Children.Add(e);
            var actions = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(v.Primary);
            actions.Children.Add(v.Cancel);
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 0); grid.Children.Add(text);
            Grid.SetColumn(actions, 1); grid.Children.Add(actions);
            v.Root = new Border
            {
                Padding = new Thickness(14), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = grid,
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
            };
            AutomationProperties.SetAutomationId(v.Root, "ModelHub_Discover_Card_" + entry.Id);
            UpdateDiscoverCard(v);
            return v;
        }

        /// <summary>Refreshes one card in place from the current job list, so polling never rebuilds the page or steals focus.</summary>
        private void UpdateDiscoverCard(DiscoverCardView v)
        {
            bool it = StartItalian;
            DiscoverEntry e = v.Entry;
            ModelDownloadStatus? job = ModelDiscoverPolicy.FindJobForFile(_downloadJobs, e.RepoId);
            if (e.Task is DiscoverTask.Image or DiscoverTask.Video && _discoverMediaSource.TryGetValue(e.Id, out RecommendedCheckpoint? cp))
                job = _downloadJobs.Where(j => string.Equals(j.FileName, cp.FileName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(j => j.UpdatedUtc).FirstOrDefault(j => j.Status != "completed");
            long done = job?.BytesDownloaded ?? 0;
            DiscoverDiskCheck disk = ModelDiscoverPolicy.CheckDisk(e.SizeBytes, done,
                _storageBudget?.DestinationFreeBytes, _storageBudget?.QuotaHeadroomBytes);
            ModelDiscoverPolicy.CardState state = ModelDiscoverPolicy.DescribeCard(e, job, _discoverInternetOn, disk, it);

            v.Job = job;
            v.Action = state.Primary;
            v.Title.Text = e.Title + (e.Curated ? string.Empty : T("  (from Hugging Face)", "  (da Hugging Face)"));
            string facts = ModelDiscoverPolicy.Facts(e, it);
            v.Facts.Text = facts;
            v.Facts.Visibility = facts.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            v.Description.Text = e.Description;
            v.Description.Visibility = string.IsNullOrWhiteSpace(e.Description) ? Visibility.Collapsed : Visibility.Visible;
            v.Fit.Text = ModelDiscoverPolicy.FitGlyph(e.Fit) + " " + ModelDiscoverPolicy.FitLabel(e.Fit, it);
            string speed = ModelDiscoverPolicy.SpeedText(e.SpeedTokensPerSecond, e.SpeedMeasured, it);
            v.Speed.Text = speed;
            v.Speed.Visibility = speed.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            v.State.Text = state.Headline;
            v.Fix.Text = state.Fix;
            v.Fix.Visibility = state.Fix.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            v.Bar.Visibility = state.Progress.HasValue && state.Primary is ModelDiscoverPolicy.CardAction.Pause or ModelDiscoverPolicy.CardAction.Resume
                ? Visibility.Visible : Visibility.Collapsed;
            if (state.Progress.HasValue) v.Bar.Value = state.Progress.Value;

            (string label, bool enabled) = state.Primary switch
            {
                ModelDiscoverPolicy.CardAction.Download => (T("Download", "Scarica") + (e.SizeBytes > 0 ? " (" + ModelDiscoverPolicy.FormatSize(e.SizeBytes) + ")" : string.Empty), true),
                ModelDiscoverPolicy.CardAction.Resume => (T("Resume", "Riprendi"), true),
                ModelDiscoverPolicy.CardAction.Pause => (T("Pause", "Pausa"), true),
                ModelDiscoverPolicy.CardAction.Use => (e.Task is DiscoverTask.Image or DiscoverTask.Video ? T("Open in Image & video", "Apri in Immagini e video") : T("Use in Chat", "Usa in Chat"), true),
                ModelDiscoverPolicy.CardAction.GoOnline => (T("Open settings", "Apri impostazioni"), true),
                _ => (state.IsProblem ? T("Unavailable", "Non disponibile") : T("Not available", "Non disponibile"), false)
            };
            if (e.Task == DiscoverTask.Uncensored && state.Primary == ModelDiscoverPolicy.CardAction.Download)
                label = T("Download (unfiltered)", "Scarica (senza filtri)") + (e.SizeBytes > 0 ? " · " + ModelDiscoverPolicy.FormatSize(e.SizeBytes) : string.Empty);
            v.Primary.Content = label;
            v.Primary.IsEnabled = enabled;
            AutomationProperties.SetName(v.Primary, label + " " + e.Title);
            bool canCancel = job != null && (ModelDownloadStatusPolicy.IsActive(job.Status) || job.Status == "paused");
            v.Cancel.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(v.Cancel, T("Cancel the download of ", "Annulla il download di ") + e.Title);
            AutomationProperties.SetName(v.Root, e.Title + ". " + facts + ". " + ModelDiscoverPolicy.FitLabel(e.Fit, it) + ". " + state.Headline);
        }

        // ---- Actions ---------------------------------------------------------------------------------------------------

        private async Task DiscoverPrimaryAsync(DiscoverCardView v)
        {
            try
            {
                switch (v.Action)
                {
                    case ModelDiscoverPolicy.CardAction.Download:
                        await DiscoverDownloadAsync(v.Entry, v.Primary);
                        break;
                    case ModelDiscoverPolicy.CardAction.Pause when v.Job != null:
                        await ModelDownloadClient.Current.PauseDownloadAsync(v.Job.Id, CancellationToken.None);
                        _discoverStatus.Text = T("Paused. The part already downloaded is kept.", "In pausa. La parte già scaricata è conservata.");
                        await RefreshAllAsync();
                        break;
                    case ModelDiscoverPolicy.CardAction.Resume when v.Job != null:
                    {
                        DiscoverDiskCheck disk = ModelDiscoverPolicy.CheckDisk(v.Entry.SizeBytes > 0 ? v.Entry.SizeBytes : v.Job.TotalBytes,
                            v.Job.BytesDownloaded, _storageBudget?.DestinationFreeBytes, _storageBudget?.QuotaHeadroomBytes);
                        if (!disk.CanStart) { _discoverStatus.Text = ModelDiscoverPolicy.DiskText(disk, StartItalian); break; }
                        v.Primary.IsEnabled = false;
                        await ModelDownloadClient.Current.StartDownloadAsync(
                            new ModelDownloadRequest { ResumeFromDownloadId = v.Job.Id }, CancellationToken.None);
                        _discoverStatus.Text = T("Resuming from where it stopped.", "Riprendo da dove si era fermato.");
                        await RefreshAllAsync();
                        break;
                    }
                    case ModelDiscoverPolicy.CardAction.Use:
                        await DiscoverUseAsync(v.Entry);
                        break;
                    case ModelDiscoverPolicy.CardAction.GoOnline:
                        App.MainWin?.ShowSettings();
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _discoverStatus.Text = T("That did not work: ", "Non è riuscito: ") + CustomCheckpointPresenter.ExtractServerError(ex.Message)
                    + " " + T("Check the Downloads tab for details, then try again.", "Controlla la scheda Download per i dettagli, poi riprova.");
                v.Primary.IsEnabled = true;
            }
        }

        private async Task DiscoverCancelAsync(DiscoverCardView v)
        {
            if (v.Job == null) return;
            try
            {
                v.Cancel.IsEnabled = false;
                await ModelDownloadClient.Current.CancelDownloadAsync(v.Job.Id, CancellationToken.None);
                _discoverStatus.Text = T("Cancelled. You can resume it later from where it stopped.", "Annullato. Puoi riprenderlo più tardi da dove si è fermato.");
                await RefreshAllAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _discoverStatus.Text = T("Could not cancel: ", "Impossibile annullare: ") + ex.Message;
            }
            finally { v.Cancel.IsEnabled = true; }
        }

        /// <summary>One click starts the Hub's own flow: it resolves the quantisation that suits this PC, shows the licence and the
        /// complete plan once for confirmation (never skipped), checks the disk and then downloads with SHA-256 verification.</summary>
        private async Task DiscoverDownloadAsync(DiscoverEntry entry, Button trigger)
        {
            DiscoverDiskCheck disk = ModelDiscoverPolicy.CheckDisk(entry.SizeBytes, 0,
                _storageBudget?.DestinationFreeBytes, _storageBudget?.QuotaHeadroomBytes);
            if (!disk.CanStart)
            {
                _discoverStatus.Text = ModelDiscoverPolicy.DiskText(disk, StartItalian);
                return;
            }
            if (disk.Verdict == DiskVerdict.Tight) _discoverStatus.Text = ModelDiscoverPolicy.DiskText(disk, StartItalian);
            trigger.IsEnabled = false;
            try
            {
                if (_discoverMediaSource.TryGetValue(entry.Id, out RecommendedCheckpoint? checkpoint))
                    await DownloadMediaCheckpointAsync(checkpoint, trigger);
                else if (_discoverTextSource.TryGetValue(entry.Id, out RecommendedModel? model))
                    await ChooseRecommendedFileAsync(model);
                await RefreshAllAsync();
            }
            finally { trigger.IsEnabled = true; }
        }

        private async Task DiscoverUseAsync(DiscoverEntry entry)
        {
            if (entry.Task is DiscoverTask.Image or DiscoverTask.Video)
            {
                ShowHubTab(ModelHubTab.Media);
                return;
            }
            ManagedModelInfo? file = _managedModels
                .Where(m => string.Equals(m.RepoId, entry.RepoId, StringComparison.OrdinalIgnoreCase)
                    || ModelDiscoverPolicy.JobBelongsToRepo(m.FileName, entry.RepoId))
                .OrderByDescending(m => m.LastWriteTimeUtc).FirstOrDefault();
            if (file == null)
            {
                _discoverStatus.Text = T("Could not find the downloaded file. Open Chat models to see what is installed.",
                    "Impossibile trovare il file scaricato. Apri Modelli di chat per vedere cosa è installato.");
                return;
            }
            if (entry.Task == DiscoverTask.Embeddings)
            {
                _discoverStatus.Text = T("Embedding models are used automatically for document search. Nothing to switch here.",
                    "I modelli di embedding vengono usati in automatico per la ricerca nei documenti. Qui non c'è nulla da cambiare.");
                return;
            }
            await ModelRoutingClient.Current.SetChatPreferenceAsync(
                new SetChatRoutingPreferenceRequest { Mode = RouteMode.Manual, ManualModelFileName = file.FileName }, CancellationToken.None);
            SettingsStore.Current.Data.ChatModelSelection = "parux-manual:" + file.FileName;
            SettingsStore.Current.Data.ChatModelRecents = ChatModelPickerPolicy.RecordRecent(
                SettingsStore.Current.Data.ChatModelRecents, file.FileName);
            SettingsStore.Current.Save();
            App.MainWin?.ShowChat();
        }

        private async Task DiscoverSearchOnlineAsync()
        {
            string query = (_discoverSearch.Text ?? string.Empty).Trim();
            if (_discoverSearching || query.Length == 0) return;
            await RefreshDiscoverInternetAsync();
            if (!_discoverInternetOn) { RenderDiscoverOnlineNote(); return; }
            _discoverSearching = true;
            _discoverSearchButton.IsEnabled = false;
            _discoverStatus.Text = T("Searching Hugging Face…", "Cerco su Hugging Face…");
            try
            {
                _discoverOnlineResults = await ModelDownloadClient.Current.SearchAsync(query, 24, CancellationToken.None);
                RenderDiscover();
                if (_discoverOnlineResults.Count == 0)
                    _discoverStatus.Text = T("Nothing found online for that. Try fewer or different words.", "Nessun risultato online. Prova con meno parole o parole diverse.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _discoverStatus.Text = T("The online search did not work: ", "La ricerca online non ha funzionato: ") + ex.Message
                    + " " + T("Check your connection. The curated list still works offline.", "Controlla la connessione. L'elenco selezionato funziona comunque offline.");
            }
            finally
            {
                _discoverSearching = false;
                RenderDiscoverOnlineNote();
            }
        }

        private async Task RefreshDiscoverInternetAsync()
        {
            try
            {
                InternetStatus status = await InternetClient.Current.GetStatusAsync(CancellationToken.None);
                _discoverInternetOn = status.Enabled;
                _discoverInternetKnown = true;
            }
            catch
            {
                _discoverInternetOn = false;
                _discoverInternetKnown = true;
            }
        }

        // ---- Live progress -----------------------------------------------------------------------------------------------

        private void StartDiscoverPolling()
        {
            _ = RefreshDiscoverInternetAsync().ContinueWith(_ => DispatcherQueue.TryEnqueue(RenderDiscoverOnlineNote), TaskScheduler.Default);
            if (_discoverPoll == null)
            {
                _discoverPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _discoverPoll.Tick += async (_, _) => await PollDiscoverAsync();
            }
            _discoverPoll.Start();
        }

        private async Task PollDiscoverAsync()
        {
            if (_discoverPollBusy || _activeTab != ModelHubTab.Discover) return;
            _discoverPollBusy = true;
            try
            {
                bool wasBusy = _downloadJobs.Any(j => ModelDownloadStatusPolicy.IsActive(j.Status));
                List<ModelDownloadStatus> jobs = await ModelDownloadClient.Current.ListDownloadsAsync(CancellationToken.None);
                bool nowBusy = jobs.Any(j => ModelDownloadStatusPolicy.IsActive(j.Status));
                _downloadJobs = jobs;
                foreach (DiscoverCardView card in _discoverCards.Values) UpdateDiscoverCard(card);
                // A transfer that just ended changes what is installed: reload the catalogue once.
                if (wasBusy && !nowBusy) await RefreshAllAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _discoverStatus.Text = T("Lost contact with the download service. It will retry on its own.",
                    "Contatto perso con il servizio di download. Riproverà da solo.");
                Log.Write("Discover poll failed: " + ex.Message);
            }
            finally { _discoverPollBusy = false; }
        }
    }
}
