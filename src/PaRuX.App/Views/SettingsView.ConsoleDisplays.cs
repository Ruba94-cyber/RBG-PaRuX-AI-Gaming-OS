using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// Settings > Gaming & AI Sharing: user-defined roles and one explicit Console TV.
/// This does not switch displays, change HDR/VRR, create virtual displays, or
/// claim control over the output monitor of an independently launched game.
/// </summary>
public sealed partial class SettingsView
{
    private bool _syncingOwnedGameRouteSwitch;
    private bool _consoleTvSettingsInitialized;
    private int _consoleDisplayRefreshVersion;

    private void ConsoleRouteOwnedGamesSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_consoleTvSettingsInitialized || _syncingOwnedGameRouteSwitch) return;
        SettingsStore.Current.Data.RouteVerifiedWindowedGamesToTv =
            ConsoleRouteOwnedGamesSwitch.IsOn;
        SettingsStore.Current.Save();
    }

    private void ConsoleAutoReturnHomeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_consoleTvSettingsInitialized || _syncingOwnedGameRouteSwitch) return;
        SettingsStore.Current.Data.AutoShowConsoleAfterVerifiedGameExit =
            ConsoleAutoReturnHomeSwitch.IsOn;
        SettingsStore.Current.Save();
    }
    private void ConsoleStartOnTvSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_consoleTvSettingsInitialized || _syncingOwnedGameRouteSwitch) return;
        SettingsStore.Current.Data.ConsoleStartOnTvWhenPresent = ConsoleStartOnTvSwitch.IsOn;
        SettingsStore.Current.Save();
    }

    private void ConsolePowerSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_consoleTvSettingsInitialized || _syncingOwnedGameRouteSwitch) return;
        SettingsStore.Current.Data.ConsolePowerProfileEnabled = ConsolePowerSwitch.IsOn;
        SettingsStore.Current.Save();
        ConsolePowerPlanCombo.IsEnabled = ConsolePowerSwitch.IsOn;
        // Switching it off while a plan is applied puts the previous plan back right away.
        if (!ConsolePowerSwitch.IsOn) _ = PlayniteClient.Current.RestorePowerProfileAsync();
    }

    private void GameBarBlockSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_consoleTvSettingsInitialized || _syncingOwnedGameRouteSwitch) return;
        var data = SettingsStore.Current.Data;
        data.BlockXboxGameBar = GameBarBlockSwitch.IsOn;
        // The explanation is shown right under this switch, so turning it on here is the owner's explicit consent.
        if (GameBarBlockSwitch.IsOn) data.GameBarBlockConsentAsked = true;
        SettingsStore.Current.Save();
        GameBarPermanentSwitch.IsEnabled = GameBarBlockSwitch.IsOn;
        // Turning it off puts the previous values back right away, including a permanent block.
        if (!GameBarBlockSwitch.IsOn) _ = PlayniteClient.Current.RestoreGameBarAsync(includePermanent: true);
    }

    private void GameBarPermanentSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_consoleTvSettingsInitialized || _syncingOwnedGameRouteSwitch) return;
        SettingsStore.Current.Data.GameBarBlockPermanent = GameBarPermanentSwitch.IsOn;
        SettingsStore.Current.Save();
        // Back to temporary: release a permanent block now (it is re-applied for the next Console session).
        if (!GameBarPermanentSwitch.IsOn) _ = PlayniteClient.Current.RestoreGameBarAsync(includePermanent: true);
    }

    private void ConsolePowerPlanCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_consoleTvSettingsInitialized || _syncingOwnedGameRouteSwitch) return;
        if ((ConsolePowerPlanCombo.SelectedItem as ComboBoxItem)?.Tag is not string id) return;
        SettingsStore.Current.Data.ConsolePowerPlanId = id;
        SettingsStore.Current.Save();
    }

    /// <summary>Fills the plan list from the Core (existing Windows plans only). The saved choice stays selected when it still exists.</summary>
    private async Task RefreshConsolePowerPlansAsync()
    {
        ConsolePowerSwitch.Header = TvT("Use a Windows power plan while Console is in use", "Usa un piano di alimentazione Windows mentre la Console è in uso");
        ConsolePowerPlanCombo.Header = TvT("Power plan to use", "Piano di alimentazione da usare");
        ConsolePowerExplanation.Text = TvT(
            "PaRuX only selects a plan that already exists in Windows and puts your previous plan back when Console ends or after a crash. If you change the plan yourself meanwhile, PaRuX leaves your choice alone.",
            "PaRuX seleziona solo un piano già presente in Windows e rimette il tuo piano precedente quando la Console termina o dopo un arresto anomalo. Se nel frattempo cambi tu il piano, PaRuX rispetta la tua scelta.");
        // The automatic "Console Home" return belonged to the native Console/TV shell (removed in gaming-removal step 3). The stored
        // value is kept untouched (rollback-safe); only the switch and its explanation are no longer shown.
        ConsoleAutoReturnHomeSwitch.Visibility = Visibility.Collapsed;
        ConsoleAutoReturnExplanation.Visibility = Visibility.Collapsed;
        _syncingOwnedGameRouteSwitch = true;
        try
        {
            ConsolePowerSwitch.IsOn = SettingsStore.Current.Data.ConsolePowerProfileEnabled;
            ConsolePowerPlanCombo.IsEnabled = ConsolePowerSwitch.IsOn;
            var reply = await PlayniteClient.Current.GetPowerProfileAsync();
            ConsolePowerPlanCombo.Items.Clear();
            if (!reply.Succeeded || reply.Value.ValueKind != System.Text.Json.JsonValueKind.Object
                || !reply.Value.TryGetProperty("available", out var plans) || plans.ValueKind != System.Text.Json.JsonValueKind.Array) return;
            string saved = SettingsStore.Current.Data.ConsolePowerPlanId ?? string.Empty;
            foreach (var plan in plans.EnumerateArray())
            {
                string? id = plan.TryGetProperty("id", out var i) ? i.GetString() : null;
                string? name = plan.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                var item = new ComboBoxItem { Content = string.IsNullOrWhiteSpace(name) ? id : name, Tag = id };
                ConsolePowerPlanCombo.Items.Add(item);
                if (string.Equals(id, saved, StringComparison.OrdinalIgnoreCase)) ConsolePowerPlanCombo.SelectedItem = item;
            }
        }
        catch (Exception ex) { Log.Write("Console power plan list failed: " + ex.GetType().Name); }
        finally { _syncingOwnedGameRouteSwitch = false; }
    }

    private void ConsoleStartWithoutTvCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_consoleTvSettingsInitialized || _syncingOwnedGameRouteSwitch) return;
        string chosen = (ConsoleStartWithoutTvCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "normal";
        SettingsStore.Current.Data.StartupWithoutTv = chosen == "minimized" ? "minimized" : "normal";
        SettingsStore.Current.Save();
    }

    private GamingDisplaySnapshotDto? _lastConsoleDisplaySnapshot;

    /// <summary>"Use the current monitor as TV": the primary connected monitor is remembered and selected. Nothing in Windows changes.</summary>
    private async void ConsoleDisplaysUseCurrent_Click(object sender, RoutedEventArgs e)
    {
        GamingDisplaySnapshotDto? snapshot = _lastConsoleDisplaySnapshot is { Supported: true }
            ? _lastConsoleDisplaySnapshot
            : await GamingCoreTopologyProvider.Provider.ReadAsync(default);
        if (snapshot is not { Supported: true })
        {
            ConsoleDisplaysStatus.Text = TvT(
                "Displays are unavailable, so the current monitor could not be identified. Nothing was changed.",
                "Schermi non disponibili: il monitor attuale non è stato identificato. Nessuna modifica.");
            return;
        }
        _lastConsoleDisplaySnapshot = snapshot;
        var data = SettingsStore.Current.Data;
        data.ConsoleTvs = ConsoleTvRegistry.SelectCurrentMonitor(data.ConsoleTvs, snapshot, DateTime.UtcNow);
        data.ConsoleTvsMigrated = true;
        ApplyConsoleTvSelection(snapshot);
    }

    /// <summary>Deselects every TV: Console then opens on the current screen again.</summary>
    private void ConsoleDisplaysClear_Click(object sender, RoutedEventArgs e)
    {
        var data = SettingsStore.Current.Data;
        data.ConsoleTvs = ConsoleTvRegistry.Sanitize(data.ConsoleTvs).Select(t => { t.Selected = false; return t; }).ToList();
        data.ConsoleTvsMigrated = true;
        ApplyConsoleTvSelection(_lastConsoleDisplaySnapshot);
    }

    /// <summary>Re-derives the older single-target fields from the TV list, saves, and redraws.</summary>
    private void ApplyConsoleTvSelection(GamingDisplaySnapshotDto? snapshot)
    {
        var data = SettingsStore.Current.Data;
        if (snapshot is { Supported: true })
            ConsoleTvSettingsSync.Apply(data, snapshot, ConsoleTvSimulation.Mode.Off, DateTime.UtcNow, out _);
        else if (!data.ConsoleTvs.Any(t => t.Selected))
            data.ConsoleTargetDisplayId = string.Empty;
        SettingsStore.Current.Save();
        RenderConsoleDisplays(snapshot);
    }

    private static bool TvSettingsItalian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    private static string TvT(string en, string it) => TvSettingsItalian ? it : en;

    private async void ConsoleDisplaysRefresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshConsoleDisplaysAsync();

    private async Task RefreshConsoleDisplaysAsync()
    {
        ConsoleDisplaysHeading.Text = TvT("Console / TV display routing", "Destinazione schermo Console / TV");
        ConsoleDisplaysIntro.Text = TvT(
            "Choose which monitors are TVs, among the ones connected now and the ones PaRuX has seen before (kept even when unplugged). You can choose several. Each monitor is recognised by its own identity, so a TV is found again on any port. Console always starts, with or without a TV; the TV list only decides where it opens and whether PaRuX starts in TV mode. PaRuX moves its own window only and never changes Windows display settings.",
            "Scegli quali monitor sono TV, tra quelli collegati adesso e quelli già visti da PaRuX (conservati anche se scollegati). Puoi sceglierne più di uno. Ogni monitor è riconosciuto dalla sua identità, quindi una TV viene ritrovata su qualsiasi porta. La Console parte sempre, con o senza TV; l'elenco TV decide solo dove si apre e se PaRuX parte in modalità TV. PaRuX sposta soltanto la propria finestra e non cambia mai le impostazioni schermo di Windows.");
        ConsoleDisplaysRefresh.Content = TvT("Detect connected displays", "Rileva schermi collegati");
        ConsoleDisplaysUseCurrent.Content = TvT("Use the current monitor as TV", "Usa il monitor attuale come TV");
        ConsoleDisplaysClear.Content = TvT("Clear TV choice", "Azzera la scelta TV");
        ConsoleRouteOwnedGamesSwitch.Header = TvT(
            "Prefer selected TV for directly launched windowed games (never force fullscreen or third-party launchers)",
            "Preferisci la TV scelta per i giochi in finestra avviati direttamente (mai forzare fullscreen o launcher esterni)");
        ConsoleAutoReturnHomeSwitch.Header = TvT(
            "Automatically show Console Home after a verified game exit (off by default)",
            "Mostra automaticamente la Home Console dopo la chiusura verificata del gioco (disattivo di base)");
        ConsoleStartOnTvSwitch.Header = TvT(
            "Start PaRuX directly in TV mode when a chosen TV is on",
            "Avvia PaRuX direttamente in modalità TV quando una TV scelta è accesa");
        ConsoleStartOnTvExplanation.Text = TvT(
            "When PaRuX starts and the TV chosen above is connected, it starts Console mode: Playnite Fullscreen (with your theme) on that TV, and PaRuX stays available for AI. If Playnite is missing or skipped, PaRuX simply opens in its normal window.",
            "All'avvio, se la TV scelta sopra è collegata, PaRuX avvia la modalità Console: Playnite a schermo intero (con il tuo tema) su quella TV, e PaRuX resta disponibile per l'IA. Se Playnite manca o è stato saltato, PaRuX si apre semplicemente nella finestra normale.");
        ConsoleStartWithoutTvCombo.Header = TvT("When PaRuX starts (also at sign-in) and no chosen TV is connected, the window", "Quando PaRuX parte (anche all'accesso) e nessuna TV scelta è collegata, la finestra");
        ConsoleAutoReturnExplanation.Text = TvT(
            "Only a verified directly launched process exit and completed AI recovery can trigger this. Manual Return, store handoffs and unknown child processes are ignored. Windows may deny foreground activation.",
            "Si attiva soltanto dopo la chiusura verificata di un processo avviato direttamente e il ripristino dell'AI. Il ritorno manuale, gli store e i processi secondari sconosciuti sono esclusi. Windows può impedire l'attivazione in primo piano.");
        _syncingOwnedGameRouteSwitch = true;
        try
        {
            ConsoleRouteOwnedGamesSwitch.IsOn =
                SettingsStore.Current.Data.RouteVerifiedWindowedGamesToTv;
            ConsoleAutoReturnHomeSwitch.IsOn =
                SettingsStore.Current.Data.AutoShowConsoleAfterVerifiedGameExit;
            ConsoleStartOnTvSwitch.IsOn = SettingsStore.Current.Data.ConsoleStartOnTvWhenPresent;
            var gb = SettingsStore.Current.Data;
            GameBarBlockSwitch.IsOn = gb.BlockXboxGameBar;
            GameBarPermanentSwitch.IsOn = gb.GameBarBlockPermanent;
            GameBarPermanentSwitch.IsEnabled = gb.BlockXboxGameBar;
            GameBarBlockSwitch.Header = TvT("Block the Xbox Game Bar", "Blocca la Xbox Game Bar");
            GameBarPermanentSwitch.Header = TvT("Keep the block after Console ends (permanent)", "Mantieni il blocco anche dopo la Console (permanente)");
            GameBarBlockExplanation.Text = TvT(
                "While Console or Playnite Fullscreen is in use, the controller Guide/Xbox button no longer opens the Xbox Game Bar. PaRuX changes only four per-user Windows settings (no administrator rights), saves their previous values and puts them back when Console ends, on exit or after a crash; a value you change yourself meanwhile is left alone. The Win+G shortcut is not covered.",
                "Mentre Console o Playnite a schermo intero è in uso, il pulsante Guide/Xbox del controller non apre più la Xbox Game Bar. PaRuX modifica solo quattro impostazioni di Windows per il tuo utente (nessun permesso di amministratore), salva i valori precedenti e li ripristina quando la Console termina, alla chiusura o dopo un arresto anomalo; un valore che cambi tu nel frattempo viene rispettato. La scorciatoia Win+G non è coperta.");
            ConsoleStartWithoutTvCombo.SelectedIndex =
                string.Equals(SettingsStore.Current.Data.StartupWithoutTv, "minimized", StringComparison.Ordinal) ? 1 : 0;
        }
        finally { _syncingOwnedGameRouteSwitch = false; }
        _consoleTvSettingsInitialized = true;
        _ = RefreshConsolePowerPlansAsync();
        int version = ++_consoleDisplayRefreshVersion;
        ConsoleDisplaysRefresh.IsEnabled = false;
        ConsoleDisplaysStatus.Text = TvT("Detecting connected screens…", "Rilevamento schermi collegati…");
        try
        {
            var reply = await GamingOrchestratorClient.Current.GetDisplayTopologyAsync();
            if (version != _consoleDisplayRefreshVersion) return;
            if (!reply.Succeeded || reply.Value is null || !reply.Value.Supported)
            {
                // The remembered monitors can still be reviewed (and forgotten) without the live list.
                RenderConsoleDisplays(null);
                ConsoleDisplaysStatus.Text = TvT(
                    "Displays are unavailable. Check the local Core. Saved TV choices were not changed.",
                    "Schermi non disponibili. Controlla il Core locale. Le scelte TV salvate non sono state modificate.");
                return;
            }

            GamingDisplaySnapshotDto snapshot = reply.Value;
            _lastConsoleDisplaySnapshot = snapshot;
            // Remember what is connected and bring the older single-target fields in line with the selected TVs.
            if (ConsoleTvSettingsSync.Apply(SettingsStore.Current.Data, snapshot, ConsoleTvSimulation.Mode.Off, DateTime.UtcNow, out _))
                SettingsStore.Current.Save();
            RenderConsoleDisplays(snapshot);
        }
        catch (Exception)
        {
            if (version == _consoleDisplayRefreshVersion)
                ConsoleDisplaysStatus.Text = TvT(
                    "Unable to read Windows displays. The saved assignment is unchanged.",
                    "Impossibile leggere gli schermi Windows. L'assegnazione salvata resta invariata.");
        }
        finally
        {
            if (version == _consoleDisplayRefreshVersion)
                ConsoleDisplaysRefresh.IsEnabled = true;
        }
    }

    private void RenderConsoleDisplays(GamingDisplaySnapshotDto? snapshot)
    {
        ConsoleDisplaysList.Children.Clear();
        var settings = SettingsStore.Current.Data;
        settings.ConsoleTvs = ConsoleTvRegistry.Sanitize(settings.ConsoleTvs);
        bool live = snapshot is { Supported: true };
        ConsoleTvSimulation.Mode simulation = ConsoleTvSimulation.FromEnvironment();
        var connectedTvs = ConsoleTvRegistry.ConnectedTvs(settings.ConsoleTvs, snapshot, ConsoleTvSimulation.Mode.Off);
        int selectedCount = settings.ConsoleTvs.Count(t => t.Selected);

        string state = selectedCount == 0
            ? TvT("No TV chosen. Console opens on the current screen and starts normally.",
                "Nessuna TV scelta. La Console si apre sullo schermo attuale e parte normalmente.")
            : live && connectedTvs.Count > 0
                ? TvT("Chosen TVs connected now: ", "TV scelte collegate adesso: ") +
                    string.Join(", ", connectedTvs.Select(ConsoleTvRegistry.DisplayName))
                : live
                    ? TvT("Chosen TVs are not connected. The TV start preference uses its fallback; Console started from Playnite or --playnite-console still starts.",
                        "Le TV scelte non sono collegate. L'avvio in modalità TV usa l'alternativa scelta; la Console avviata da Playnite o con --playnite-console parte comunque.")
                    : string.Empty;
        if (simulation != ConsoleTvSimulation.Mode.Off)
            state += " " + TvT("[Test mode: the current monitor is simulated as a TV.]", "[Modalità di test: il monitor attuale è simulato come TV.]");
        if (state.Length > 0) ConsoleDisplaysStatus.Text = state.Trim();

        // Connected monitors first (in Windows order), then the ones only remembered, most recently seen first.
        var connected = new List<(ConsoleTvEntry Entry, GamingDisplayModeDto? Display)>();
        var remembered = new List<(ConsoleTvEntry Entry, GamingDisplayModeDto? Display)>();
        var shownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (live)
            foreach (GamingDisplayModeDto display in snapshot!.Displays)
            {
                if (!ConsoleDisplaySelectionPolicy.ValidDisplayId(display.DisplayId)) continue;
                string id = ConsoleMonitorIdentity.IdOf(display);
                ConsoleTvEntry? entry = settings.ConsoleTvs.FirstOrDefault(t => string.Equals(t.StableId, id, StringComparison.OrdinalIgnoreCase));
                if (entry is null || !shownIds.Add(entry.StableId)) continue;
                connected.Add((entry, display));
            }
        foreach (ConsoleTvEntry entry in settings.ConsoleTvs.OrderByDescending(t => t.LastSeenUtc))
            if (shownIds.Add(entry.StableId)) remembered.Add((entry, null));

        int connectedIndex = 0, rememberedIndex = 0;
        foreach (var (entry, display) in connected.Concat(remembered))
        {
            // Index 0.. over connected monitors keeps the identifiers the display cards always had.
            string idPrefix = display is not null
                ? $"Settings_ConsoleDisplay_{connectedIndex++}"
                : $"Settings_ConsoleTvHistory_{rememberedIndex++}";
            string stableId = entry.StableId;

            var panel = new StackPanel { Spacing = 8, Padding = new Thickness(10) };
            string detail = display is not null
                ? TvT("Connected", "Collegato") + $" · {display.Width} × {display.Height}" +
                    (display.RefreshRateHz > 0 ? $" · {display.RefreshRateHz} Hz" : "") +
                    (display.Primary ? TvT(" · Primary", " · Principale") : "") +
                    " · " + (display.ColorMode switch
                    {
                        "hdr" => "HDR",
                        "wcg" => TvT("WCG (not HDR)", "WCG (non HDR)"),
                        "sdr" => "SDR",
                        "advanced-color-unknown" => TvT("Advanced Color, HDR unknown", "Colore avanzato, HDR non verificato"),
                        _ => TvT("HDR status unavailable", "Stato HDR non disponibile")
                    })
                : TvT("Not connected · last seen ", "Non collegato · visto l'ultima volta il ") +
                    entry.LastSeenUtc.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture);
            panel.Children.Add(new TextBlock { Text = entry.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = detail + (string.IsNullOrEmpty(entry.MonitorCode) ? "" : " · " + entry.MonitorCode), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });

            var roleBox = new ComboBox
            {
                Header = TvT("Display type", "Tipo di schermo"),
                MinWidth = 200,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetAutomationId(roleBox, idPrefix + "_Role");
            roleBox.Items.Add(new ComboBoxItem { Content = TvT("PC monitor", "Monitor PC"), Tag = ConsoleDisplaySelectionPolicy.MonitorRole });
            roleBox.Items.Add(new ComboBoxItem { Content = "TV", Tag = ConsoleDisplaySelectionPolicy.TvRole });
            roleBox.SelectedIndex = entry.Selected ? 1 : 0;
            roleBox.SelectionChanged += (_, _) =>
            {
                string chosen = (roleBox.SelectedItem as ComboBoxItem)?.Tag as string ?? ConsoleDisplaySelectionPolicy.MonitorRole;
                if (!ConsoleDisplaySelectionPolicy.ValidRole(chosen)) return;
                bool wantTv = chosen == ConsoleDisplaySelectionPolicy.TvRole;
                var data = SettingsStore.Current.Data;
                if (data.ConsoleTvs.FirstOrDefault(t => string.Equals(t.StableId, stableId, StringComparison.OrdinalIgnoreCase))?.Selected == wantTv) return;
                data.ConsoleTvs = ConsoleTvRegistry.SetSelected(data.ConsoleTvs, stableId, wantTv);
                data.ConsoleTvsMigrated = true;
                // Defer rebuilding the list until WinUI has finished raising SelectionChanged for the ComboBox being removed.
                _dispatcher.TryEnqueue(() => ApplyConsoleTvSelection(snapshot));
            };
            panel.Children.Add(roleBox);

            var forget = new Button { Content = TvT("Forget this monitor", "Dimentica questo monitor"), HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(forget, idPrefix + "_Forget");
            forget.Click += (_, _) =>
            {
                var data = SettingsStore.Current.Data;
                data.ConsoleTvs = ConsoleTvRegistry.Forget(data.ConsoleTvs, stableId);
                data.ConsoleTvsMigrated = true;
                ApplyConsoleTvSelection(snapshot);
            };
            panel.Children.Add(forget);

            // Reuse the existing theme's subtle surface; no custom brush keys or light/dark-specific inline colors are required.
            var card = new Border { Child = panel, CornerRadius = new CornerRadius(10) };
            if (Application.Current.Resources.TryGetValue("PaRuXSurfaceSubtleBrush", out object? brush) &&
                brush is Microsoft.UI.Xaml.Media.Brush surface)
                card.Background = surface;
            ConsoleDisplaysList.Children.Add(card);
        }
    }
}
