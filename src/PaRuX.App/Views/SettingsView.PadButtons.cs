using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// Settings > Gaming &amp; AI Sharing > Controller buttons: for the chosen pad (or "any pad") every control has two independent choices,
/// "Passes to the game as" (the existing PadMapping of a raw pad, saved in RawPadMappingStore) and "PaRuX action" (tap and hold), plus a
/// TV mode table (navigation keys). The section is built in code; every control has an AutomationId and is a plain ComboBox, so it is
/// operable by keyboard and by pad. "Detect button" shows the live pressed controls and selects the row of the pressed one; while it is on,
/// actions are suppressed (it ends by itself after 45 s or when this section is no longer visible).
/// </summary>
public sealed partial class SettingsView
{
    private static string PbT(string en, string it) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;
    private static bool PbItalian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";

    private sealed record PbPad(string Key, string Label, string? DeviceId, ushort Vid, ushort Pid, int Buttons, bool Hat);

    private bool _pbBuilt, _pbSyncing, _pbLearning;
    private string _pbSelectedKey = PadControlId.Any;
    private List<PbPad> _pbPads = new();
    private readonly Dictionary<string, Border> _pbRows = new(StringComparer.Ordinal);
    private HashSet<string> _pbPrevPressed = new(StringComparer.Ordinal);
    private DispatcherTimerLite? _pbLearnTimeout;

    private sealed class DispatcherTimerLite
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _t;
        public DispatcherTimerLite(Microsoft.UI.Dispatching.DispatcherQueue q, TimeSpan after, Action fire)
        {
            _t = q.CreateTimer();
            _t.Interval = after;
            _t.IsRepeating = false;
            _t.Tick += (_, _) => fire();
            _t.Start();
        }
        public void Stop() { try { _t.Stop(); } catch (Exception) { } }
    }

    /// <summary>Called by the shell for "Open Settings > Controller buttons".</summary>
    public void ShowPadButtons()
    {
        SelectSettingsCategory("Gaming & AI Sharing");
        RefreshPadButtons();
        try { PadButtonsCard.StartBringIntoView(); } catch (Exception) { }
    }

    /// <summary>Settings sections the slim Games tools page links to (Settings_Playnite_Section and Settings_SignInApps_Section are
    /// built in code; the Console display / Game Bar card is declared in XAML).</summary>
    internal const string GamingSectionPlaynite = "Settings_Playnite_Section";
    internal const string GamingSectionSignInApps = "Settings_SignInApps_Section";
    internal const string GamingSectionConsoleDisplays = "ConsoleDisplaysHeading";

    /// <summary>Called by the Games tools page: opens "Gaming &amp; AI Sharing" and brings one existing section into view.
    /// It only navigates; nothing is changed, detected or started.</summary>
    internal void ShowGamingAiSharingSection(string? sectionAutomationId)
    {
        SelectSettingsCategory("Gaming & AI Sharing");
        if (string.IsNullOrEmpty(sectionAutomationId)) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                FrameworkElement? target = sectionAutomationId == GamingSectionConsoleDisplays
                    ? ConsoleDisplaysHeading
                    : FindByAutomationId(SettingsContentPanel, sectionAutomationId);
                target?.StartBringIntoView();
            }
            catch (Exception) { /* scrolling is a convenience; the category is already open */ }
        });
    }

    private static FrameworkElement? FindByAutomationId(DependencyObject root, string id)
    {
        if (root is FrameworkElement fe && AutomationProperties.GetAutomationId(fe) == id) return fe;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            if (FindByAutomationId(VisualTreeHelper.GetChild(root, i), id) is { } hit) return hit;
        return null;
    }

    private PadActionSettings PbSettings => SettingsStore.Current.Data.PadActions ??= new PadActionSettings();

    private List<PadButtonBinding> PbEditable() =>
        PadActionSettings.Normalize(PbSettings.Bindings ?? PadActionSettings.DefaultBindings.ToList());

    private void PbSaveBindings(List<PadButtonBinding> rows)
    {
        PbSettings.Bindings = PadActionSettings.Normalize(rows);
        SettingsStore.Current.Save();
    }

    private void RefreshPadButtons()
    {
        PadButtonsHeading.Text = PbT("Controller buttons", "Pulsanti del controller");
        PadButtonsIntro.Text = PbT(
            "Choose what every control of a pad does. 'Passes to the game as' remaps what the game sees (needs PaRuX pad forwarding); 'PaRuX action' runs a PaRuX function and works from the raw device while a game runs. Nothing is ever sent into a game.",
            "Scegli cosa fa ogni controllo di un pad. 'Arriva al gioco come' rimappa ciò che vede il gioco (richiede l'inoltro del pad di PaRuX); 'Azione PaRuX' esegue una funzione di PaRuX e funziona dal dispositivo raw anche mentre un gioco è in esecuzione. Nulla viene mai inviato dentro un gioco.");
        _pbSyncing = true;
        try { PadButtonsEnabledSwitch.IsOn = PbSettings.Enabled; PadButtonsHoldBox.Value = PbSettings.EffectiveHoldMs; }
        finally { _pbSyncing = false; }
        PadButtonsHoldBox.Header = PbT("Hold time (ms): how long a button is held for its 'hold' action", "Durata pressione lunga (ms): quanto tenere premuto per l'azione 'lunga'");
        PadButtonsEnabledSwitch.Header = PbT("Use controller buttons for PaRuX actions", "Usa i pulsanti del controller per le azioni di PaRuX");
        PadButtonsRefreshButton.Content = PbT("Detect pads", "Rileva i pad");
        PbRebuildPads();
        PbRebuildTable();
        PbRebuildTv();
    }

    private void PadButtonsEnabledSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_pbSyncing) return;
        PbSettings.Enabled = PadButtonsEnabledSwitch.IsOn;
        SettingsStore.Current.Save();
    }

    private void PadButtonsHoldBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_pbSyncing) return;
        double v = double.IsNaN(args.NewValue) ? PadActionSettings.HoldMs : args.NewValue;
        int ms = (int)Math.Clamp(Math.Round(v), PadActionSettings.MinHoldMs, PadActionSettings.MaxHoldMs);
        PbSettings.HoldMsOverride = ms == PadActionSettings.HoldMs ? null : ms;
        SettingsStore.Current.Save();
    }

    private void PadButtonsRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        PbRebuildPads();
        PbRebuildTable();
    }

    private void PbRebuildPads()
    {
        var pads = new List<PbPad> { new(PadControlId.Any, PbT("Any pad (Xbox-style names)", "Qualsiasi pad (nomi stile Xbox)"), null, 0, 0, 0, false) };
        try
        {
            foreach (var d in WindowsRawGamepadSource.ListDevices())
            {
                if (PadMappingPresets.IsXInputFamily(d.VendorId, d.ProductId)) continue;   // Xbox-class pads are covered by "any pad"
                string key = PadControlId.DeviceKey(d.VendorId, d.ProductId);
                if (pads.Any(p => p.Key == key)) continue;
                pads.Add(new PbPad(key, $"{d.Name} ({key})", d.Id, d.VendorId, d.ProductId, d.ButtonCount, d.SwitchCount > 0));
            }
        }
        catch (Exception) { /* enumeration unavailable: only "any pad" is offered */ }
        _pbPads = pads;
        if (_pbPads.All(p => p.Key != _pbSelectedKey)) _pbSelectedKey = PadControlId.Any;

        _pbSyncing = true;
        try
        {
            PadButtonsPadCombo.Items.Clear();
            foreach (PbPad p in _pbPads) PadButtonsPadCombo.Items.Add(new ComboBoxItem { Content = p.Label, Tag = p.Key });
            PadButtonsPadCombo.SelectedIndex = Math.Max(0, _pbPads.FindIndex(p => p.Key == _pbSelectedKey));
            PadButtonsPadCombo.Header = PbT("Pad", "Pad");
        }
        finally { _pbSyncing = false; }
        _pbBuilt = true;
    }

    private void PadButtonsPadCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pbSyncing || !_pbBuilt) return;
        _pbSelectedKey = (PadButtonsPadCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? PadControlId.Any;
        PbRebuildTable();
    }

    private static string PbId(string s) => s.Replace(':', '_');

    private ComboBox PbCombo(string autoId, string name, IEnumerable<(string Key, string Label)> items, string current, Action<string> onChange)
    {
        var combo = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
        int index = 0, i = 0;
        foreach (var (key, label) in items)
        {
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            if (key == current) index = i;
            i++;
        }
        combo.SelectedIndex = index;
        AutomationProperties.SetAutomationId(combo, autoId);
        AutomationProperties.SetName(combo, name);
        combo.SelectionChanged += (_, _) =>
        {
            if (_pbSyncing) return;
            if ((combo.SelectedItem as ComboBoxItem)?.Tag is string key) onChange(key);
        };
        return combo;
    }

    private static IEnumerable<(string, string)> PbActionItems() =>
        PadActionCatalog.All.Select(a => (a.Key, a.Label(PbItalian)));

    private void PbRebuildTable()
    {
        PadButtonsTable.Children.Clear();
        _pbRows.Clear();
        PbPad pad = _pbPads.FirstOrDefault(p => p.Key == _pbSelectedKey) ?? _pbPads[0];
        IReadOnlyList<PadControlInfo> controls = pad.DeviceId is null
            ? PadControlCatalog.ForXInput()
            : PadControlCatalog.ForRaw(pad.Vid, pad.Pid, pad.Buttons, pad.Hat);

        PadMapping? mapping = null;
        RawPadMappingStore? store = null;
        if (pad.DeviceId is not null)
        {
            store = new RawPadMappingStore();
            mapping = store.Load(pad.DeviceId, out _) ?? PadMappingPresets.Find(pad.Vid, pad.Pid)?.Mapping.Clone()
                ?? (GenericRawPadMapping.TryBuild(pad.Buttons, 6, pad.Hat ? 1 : 0, out PadMapping? g, out _) ? g : null);
        }

        var rows = PbEditable();
        string safeKey = PbId(pad.Key);
        foreach (PadControlInfo c in controls)
        {
            string ctlId = PbId(c.Id);
            string label = PbItalian ? c.It : c.En;
            string Current(PadTrigger t) =>
                rows.FirstOrDefault(r => r.Device == pad.Key && r.Control == c.Id && r.TriggerKind == t)?.Action ?? "none";

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            grid.Children.Add(text);

            if (pad.DeviceId is not null && c.RawIndex is int raw && mapping is not null)
            {
                var targets = PadPassThroughEditor.Targets.Select(t => (t, t == "none" ? PbT("Nothing (swallowed)", "Niente (bloccato)") : PbT("Xbox ", "Xbox ") + PadControlCatalog.XboxLabel(t, PbItalian)));
                ComboBox pass = PbCombo($"Settings_PadButtons_{safeKey}_{ctlId}_Passes_Combo", label + " - " + PbT("Passes to the game as", "Arriva al gioco come"),
                    targets, PadPassThroughEditor.TargetOf(mapping, raw), target =>
                    {
                        PadMapping updated = PadPassThroughEditor.SetTarget(mapping, raw, target);
                        if (store!.Save(pad.DeviceId, updated, out string? error)) mapping = updated;
                        PadButtonsStatus.Text = error ?? PbT("Saved. Takes effect the next time PaRuX forwards this pad.", "Salvato. Ha effetto alla prossima inoltro del pad da parte di PaRuX.");
                        PbShowConflicts(pad, mapping, PbEditable());
                    });
                Grid.SetColumn(pass, 1);
                grid.Children.Add(pass);
            }

            ComboBox tap = PbCombo($"Settings_PadButtons_{safeKey}_{ctlId}_Action_Combo", label + " - " + PbT("PaRuX action (tap)", "Azione PaRuX (tocco)"),
                PbActionItems(), Current(PadTrigger.Tap), key => PbSetRow(pad, c.Id, PadTrigger.Tap, key, mapping));
            Grid.SetColumn(tap, 2);
            grid.Children.Add(tap);
            ComboBox hold = PbCombo($"Settings_PadButtons_{safeKey}_{ctlId}_Hold_Combo", label + " - " + PbT("PaRuX action (long press)", "Azione PaRuX (pressione lunga)"),
                PbActionItems(), Current(PadTrigger.Hold), key => PbSetRow(pad, c.Id, PadTrigger.Hold, key, mapping));
            Grid.SetColumn(hold, 3);
            grid.Children.Add(hold);

            var border = new Border { Padding = new Thickness(6), CornerRadius = new CornerRadius(4), Child = grid };
            _pbRows[c.Id] = border;
            PadButtonsTable.Children.Add(border);
        }

        PadButtonsHeaderRow.Text = pad.DeviceId is null
            ? PbT("Control  |  PaRuX action (tap)  |  PaRuX action (long press)", "Controllo  |  Azione PaRuX (tocco)  |  Azione PaRuX (pressione lunga)")
            : PbT("Control  |  Passes to the game as  |  PaRuX action (tap)  |  PaRuX action (long press)", "Controllo  |  Arriva al gioco come  |  Azione PaRuX (tocco)  |  Azione PaRuX (pressione lunga)");
        PadButtonsDetectButton.Content = _pbLearning ? PbT("Stop detecting", "Ferma il rilevamento") : PbT("Detect button", "Rileva pulsante");
        PadButtonsRestoreButton.Content = PbT("Restore defaults (this pad)", "Ripristina predefiniti (questo pad)");
        PbShowConflicts(pad, mapping, rows);
    }

    private void PbSetRow(PbPad pad, string control, PadTrigger trigger, string actionKey, PadMapping? mapping)
    {
        var rows = PbEditable();
        rows.RemoveAll(r => r.Device == pad.Key && r.Control == control && r.TriggerKind == trigger);
        if (PadActionCatalog.TryParse(actionKey, out PadActionId id) && id != PadActionId.None)
            rows.Add(PadButtonBinding.Make(pad.Key, control, id, trigger));
        PbSaveBindings(rows);
        PadButtonsStatus.Text = PbT("Saved.", "Salvato.");
        PbShowConflicts(pad, mapping, rows);
    }

    /// <summary>Flags (never drops) conflicting assignments: duplicate rows, pad-specific rows overriding 'any pad', two buttons feeding the
    /// same virtual control, and a button that both runs a PaRuX action and still reaches the game.</summary>
    private void PbShowConflicts(PbPad pad, PadMapping? mapping, List<PadButtonBinding> rows)
    {
        var lines = new List<string>();
        foreach (PadBindingConflict c in PadActionSettings.FindConflicts(rows))
            lines.Add($"{c.Device} {c.Control} ({c.Trigger}): {c.Message}");
        if (mapping is not null)
        {
            foreach (string t in PadPassThroughEditor.Conflicts(mapping, pad.Buttons))
                lines.Add(PbT($"Two buttons feed the same game control ({t}).", $"Due pulsanti alimentano lo stesso controllo del gioco ({t})."));
            foreach (PadButtonBinding b in rows.Where(r => r.Device == pad.Key && PadControlId.TryRawIndex(r.Control, out _)))
                if (PadControlId.TryRawIndex(b.Control, out int ix) && PadPassThroughEditor.TargetOf(mapping, ix) != "none")
                    lines.Add(PbT($"{b.Control} runs a PaRuX action and also reaches the game; set 'Passes to the game as' to Nothing to keep it for PaRuX only.",
                        $"{b.Control} esegue un'azione PaRuX e arriva anche al gioco; imposta 'Arriva al gioco come' su Niente per riservarlo a PaRuX."));
        }
        PadButtonsConflicts.Text = lines.Count == 0 ? string.Empty : PbT("Check: ", "Controlla: ") + string.Join("  ", lines);
        PadButtonsConflicts.Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PadButtonsRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        PbPad pad = _pbPads.FirstOrDefault(p => p.Key == _pbSelectedKey) ?? _pbPads[0];
        var rows = PbEditable();
        rows.RemoveAll(r => r.Device == pad.Key);
        rows.AddRange(PadActionSettings.DefaultBindings.Where(d => d.Device == pad.Key));
        PbSaveBindings(rows);
        string? note = null;
        if (pad.DeviceId is not null && PadMappingPresets.Find(pad.Vid, pad.Pid) is { } preset
            && !new RawPadMappingStore().Save(pad.DeviceId, preset.Mapping.Clone(), out note))
            PadButtonsStatus.Text = note ?? string.Empty;
        else
            PadButtonsStatus.Text = PbT("Defaults restored for this pad.", "Predefiniti ripristinati per questo pad.");
        PbRebuildTable();
    }

    // ---- TV mode table -----------------------------------------------------------------------------------------------------------
    private void PbRebuildTv()
    {
        PadButtonsTvHeading.Text = PbT("TV mode (Console navigation)", "Modalità TV (navigazione Console)");
        PadButtonsTvRestore.Content = PbT("Restore defaults (TV mode)", "Ripristina predefiniti (modalità TV)");
        PadButtonsTvTable.Children.Clear();
        ConsoleNavigationBindings tv = PbSettings.EffectiveTv;
        foreach (string control in ConsoleNavigationBindings.Controls)
        {
            string name = control[2..];
            string label = PadControlCatalog.XboxLabel(name, PbItalian);
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var items = TvActionCatalog.All.Select(a => (a.Key, PbItalian ? a.It : a.En));
            string captured = control;
            ComboBox combo = PbCombo($"Settings_PadButtons_Tv_{PbId(control)}_Combo", PbT("TV mode: ", "Modalità TV: ") + label, items,
                TvActionCatalog.Get(tv.Resolve(control)).Key, key =>
                {
                    if (!TvActionCatalog.TryParse(key, out TvAction action)) return;
                    ConsoleNavigationBindings updated = PbSettings.EffectiveTv.With(captured, action);
                    PbSettings.Tv = updated.ToStored();
                    SettingsStore.Current.Save();
                    PbShowTvWarnings(updated);
                });
            Grid.SetColumn(combo, 1);
            grid.Children.Add(combo);
            PadButtonsTvTable.Children.Add(grid);
        }
        PbShowTvWarnings(tv);
    }

    private void PbShowTvWarnings(ConsoleNavigationBindings tv)
    {
        var missing = tv.UnreachableEssentials();
        PadButtonsTvWarning.Text = missing.Count == 0 ? string.Empty
            : PbT("Warning: no control is assigned to: ", "Attenzione: nessun controllo è assegnato a: ")
              + string.Join(", ", missing.Select(m => PbItalian ? TvActionCatalog.Get(m).It : TvActionCatalog.Get(m).En));
        PadButtonsTvWarning.Visibility = missing.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PadButtonsTvRestore_Click(object sender, RoutedEventArgs e)
    {
        PbSettings.Tv = null;
        SettingsStore.Current.Save();
        PbRebuildTv();
    }

    // ---- Detect button (learn mode / live view) ----------------------------------------------------------------------------------
    private void PadButtonsDetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pbLearning) PbStopLearning(); else PbStartLearning();
    }

    private void PbStartLearning()
    {
        _pbLearning = true;
        _pbPrevPressed = new HashSet<string>(StringComparer.Ordinal);
        PadButtonsDetectButton.Content = PbT("Stop detecting", "Ferma il rilevamento");
        PadButtonsLive.Text = PbT("Press a control on the pad. PaRuX actions are paused while detecting (it stops by itself after 45 seconds).",
            "Premi un controllo sul pad. Le azioni PaRuX sono sospese durante il rilevamento (si ferma da solo dopo 45 secondi).");
        PadActionListener.Current.SetObserver(OnPbObserved);
        _pbLearnTimeout?.Stop();
        _pbLearnTimeout = new DispatcherTimerLite(DispatcherQueue, TimeSpan.FromSeconds(45), PbStopLearning);
    }

    private void PbStopLearning()
    {
        if (!_pbLearning) return;
        _pbLearning = false;
        _pbLearnTimeout?.Stop();
        _pbLearnTimeout = null;
        PadActionListener.Current.SetObserver(null);
        foreach (Border b in _pbRows.Values) b.Background = null;
        PadButtonsDetectButton.Content = PbT("Detect button", "Rileva pulsante");
        PadButtonsLive.Text = string.Empty;
    }

    private void OnPbObserved(string deviceKey, IReadOnlyCollection<string> pressed)
    {
        // Leaving this section (or Settings) must never leave actions suppressed.
        if (!_pbLearning || PadButtonsCard.ActualHeight <= 0) { PbStopLearning(); return; }
        if (deviceKey != _pbSelectedKey && !(_pbSelectedKey == PadControlId.Any && deviceKey == PadControlId.Any)) return;
        Brush hit = BrandTint(PadButtonsCard, 70);
        foreach (var (id, border) in _pbRows) border.Background = pressed.Contains(id) ? hit : null;
        PadButtonsLive.Text = pressed.Count == 0
            ? PbT("Press a control on the pad.", "Premi un controllo sul pad.")
            : PbT("Pressed: ", "Premuti: ") + string.Join(", ", pressed.OrderBy(p => p, StringComparer.Ordinal));
        string? fresh = pressed.FirstOrDefault(p => !_pbPrevPressed.Contains(p) && _pbRows.ContainsKey(p));
        _pbPrevPressed = new HashSet<string>(pressed, StringComparer.Ordinal);
        if (fresh is not null && _pbRows.TryGetValue(fresh, out Border? row))
        {
            PadButtonsLive.Text += "  -  " + PbT("Detected: ", "Rilevato: ") + fresh;
            try { row.StartBringIntoView(); } catch (Exception) { }
        }
    }
}
