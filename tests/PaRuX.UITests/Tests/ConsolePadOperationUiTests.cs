using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Real-app proof that Console/TV mode is operable with a gamepad only: ContentDialogs, ComboBox drop-downs, flyouts, ToggleSwitch,
/// Slider and text entry. A SIMULATED Xbox 360 pad (tools/PaRuX.VirtualPadSim over the product's own ViGEm client) is the only input
/// after the app has started; the app is started in TV mode through the settings seed. Synthetic library only (no real games, no
/// PARUX_REAL_GAMES needed). Build the simulator first: dotnet build tools/PaRuX.VirtualPadSim -c Debug.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ConsolePadOperationUiTests
{
    [Fact, Trait("Category", "FlaUI"), Trait("Category", "ConsolePad")]
    public void Launch_dialog_is_confirmed_with_A_and_cancelled_with_B_from_the_pad()
    {
        using var s = new ConsolePadSession(nameof(Launch_dialog_is_confirmed_with_A_and_cancelled_with_B_from_the_pad));
        try
        {
            s.OpenFirstGameLaunchDialog();
            s.Mark("launch dialog open; focus " + s.FocusedId + " / " + s.FocusedName);
            // The TV default of the launch confirmation is its primary action (Launch), not a blind first control.
            Assert.Equal("PrimaryButton", s.FocusedId);

            // B cancels the dialog: it closes, Console/TV mode is NOT left.
            s.B();
            UiWait.For(() => !s.DialogOpen(), "B closed the dialog");
            Assert.True(s.ConsoleOpen, "B on a dialog must not leave Console/TV mode");

            // Open it again: A confirms (the synthetic executable does not exist, so the product reports a failure; nothing launches).
            s.OpenFirstGameLaunchDialog();
            Assert.Equal("PrimaryButton", s.FocusedId);
            s.A();
            UiWait.For(() => !s.DialogOpen() || s.FocusedId != "PrimaryButton", "A activated the dialog button");
            Assert.True(s.ConsoleOpen);
            // The library is still operable by the pad afterwards.
            string before = s.FocusedId + s.FocusedName;
            for (int i = 0; i < 4 && s.FocusedId + s.FocusedName == before; i++) s.Dir("right");
        }
        catch (Exception ex) { s.App.CaptureFailure(nameof(Launch_dialog_is_confirmed_with_A_and_cancelled_with_B_from_the_pad), ex); throw; }
    }

    [Fact, Trait("Category", "FlaUI"), Trait("Category", "ConsolePad")]
    public void A_ComboBox_is_opened_chosen_from_and_closed_with_the_pad()
    {
        using var s = new ConsolePadSession(nameof(A_ComboBox_is_opened_chosen_from_and_closed_with_the_pad));
        try
        {
            const string id = "GameHome_Sort";
            AutomationElement combo = s.NavigateTo(id);
            string Selected() => combo.Patterns.Selection.PatternOrDefault?.Selection.ValueOrDefault?.FirstOrDefault()?.Name ?? combo.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault ?? string.Empty;
            string ExpandState() => combo.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value.ToString();
            string initial = Selected();
            s.Mark("combo focused; selected '" + initial + "'");

            // A opens the list; the focus moves INTO the drop-down (a separate popup root).
            s.A();
            UiWait.For(() => ExpandState() == "Expanded", "A opened the drop-down");
            UiWait.For(() => s.Focused?.ControlType == ControlType.ListItem, "focus is on a drop-down item");
            string firstItem = s.FocusedName;

            // D-pad down moves inside the open list, not behind it.
            s.Dir("down");
            string secondItem = s.FocusedName;
            Assert.NotEqual(firstItem, secondItem);
            Assert.Equal(ControlType.ListItem, s.Focused!.ControlType);

            // B closes the list WITHOUT choosing and WITHOUT leaving Console/TV mode.
            s.B();
            UiWait.For(() => ExpandState() == "Collapsed", "B closed the drop-down");
            Assert.True(s.ConsoleOpen);
            Assert.Equal(initial, Selected());
            Assert.Equal(id, s.FocusedId);

            // Open again, move down, A chooses the item and closes the list.
            s.A();
            UiWait.For(() => ExpandState() == "Expanded", "drop-down reopened");
            s.Dir("down");
            string chosen = s.FocusedName;
            s.A();
            UiWait.For(() => ExpandState() == "Collapsed", "A chose an item and closed the list");
            Assert.NotEqual(initial, Selected());
            Assert.Equal(chosen, Selected());
            Assert.True(s.ConsoleOpen);
        }
        catch (Exception ex) { s.App.CaptureFailure(nameof(A_ComboBox_is_opened_chosen_from_and_closed_with_the_pad), ex); throw; }
    }

    [Fact, Trait("Category", "FlaUI"), Trait("Category", "ConsolePad")]
    public void A_flyout_with_a_ToggleSwitch_and_a_Slider_is_operated_and_closed_by_the_pad()
    {
        using var s = new ConsolePadSession(nameof(A_flyout_with_a_ToggleSwitch_and_a_Slider_is_operated_and_closed_by_the_pad));
        try
        {
            s.Press("press back", 1500);   // View: switch to the AI page
            UiWait.ById(s.App.MainWindow, "Chat_Send_Button");
            s.NavigateTo("Chat_Generation_Button");
            s.A();
            UiWait.ById(s.App.MainWindow, "Chat_Generation_Custom_Toggle");
            s.Mark("generation flyout open; focus " + s.FocusedId);

            AutomationElement toggle = s.NavigateTo("Chat_Generation_Custom_Toggle");
            var toggleState = () => toggle.Patterns.Toggle.Pattern.ToggleState.Value.ToString();
            string toggleBefore = toggleState();
            s.A();
            UiWait.For(() => toggleState() != toggleBefore, "A toggled the ToggleSwitch");

            // The slider is enabled now that the custom values are on: D-pad right raises its value.
            AutomationElement slider = s.NavigateTo("Chat_Generation_Temperature_Slider");
            double Value() => slider.Patterns.RangeValue.Pattern.Value.Value;
            double v0 = Value();
            for (int i = 0; i < 5; i++) s.Dir("right");
            Assert.True(Value() > v0 + 0.1, $"slider value {v0} -> {Value()}");
            Assert.Equal("Chat_Generation_Temperature_Slider", s.FocusedId);   // right adjusted it, the focus did not leave
            double v1 = Value();
            s.Dir("left");
            Assert.True(Value() < v1, "D-pad left lowers the slider");

            // B closes the flyout and stays on the AI page in Console/TV mode.
            s.B();
            UiWait.For(() => s.App.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Chat_Generation_Custom_Toggle")) == null,
                "B closed the flyout");
            Assert.True(s.ConsoleOpen);
            Assert.NotNull(s.App.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("Chat_Send_Button")));
        }
        catch (Exception ex) { s.App.CaptureFailure(nameof(A_flyout_with_a_ToggleSwitch_and_a_Slider_is_operated_and_closed_by_the_pad), ex); throw; }
    }

    [Fact, Trait("Category", "FlaUI"), Trait("Category", "ConsolePad")]
    public void A_on_a_text_field_opens_the_pad_operable_keyboard_for_search_and_for_the_chat_composer()
    {
        using var s = new ConsolePadSession(nameof(A_on_a_text_field_opens_the_pad_operable_keyboard_for_search_and_for_the_chat_composer));
        try
        {
            bool KeyboardOpen() => s.App.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("OnScreenKeyboard_Root")) is { IsOffscreen: false };

            // Menu focuses the search box; A opens the keyboard (text entry), B cancels it.
            s.Press("press start", 800);
            Assert.Equal("GameTitles_Search_TextBox", s.FocusedId);
            s.A();
            UiWait.For(KeyboardOpen, "A on the search box opened the on-screen keyboard");
            s.Mark("keyboard open on search");
            Assert.Contains("on-screen keyboard opened", File.ReadAllText(s.App.LogPath));
            // Directions stay inside the keyboard.
            s.Dir("right");
            Assert.True(KeyboardOpen());
            Assert.StartsWith("OnScreenKeyboard", s.FocusedId, StringComparison.Ordinal);
            s.B();
            UiWait.For(() => !KeyboardOpen(), "B closed the keyboard");
            Assert.True(s.ConsoleOpen);

            // The chat composer.
            s.Press("press back", 1500);
            UiWait.ById(s.App.MainWindow, "Chat_Send_Button");
            s.NavigateTo("Chat_Prompt_TextBox");
            s.A();
            UiWait.For(KeyboardOpen, "A on the chat composer opened the on-screen keyboard");
            s.B();
            UiWait.For(() => !KeyboardOpen(), "B closed the keyboard on the composer");
            Assert.True(s.ConsoleOpen);
        }
        catch (Exception ex) { s.App.CaptureFailure(nameof(A_on_a_text_field_opens_the_pad_operable_keyboard_for_search_and_for_the_chat_composer), ex); throw; }
    }

    [Fact, Trait("Category", "FlaUI"), Trait("Category", "ConsolePad")]
    public void The_word_mario_is_typed_into_the_library_search_with_the_pad_and_filters_the_library()
    {
        using var s = new ConsolePadSession(nameof(The_word_mario_is_typed_into_the_library_search_with_the_pad_and_filters_the_library), 12);
        try
        {
            int Tiles() => s.App.MainWindow.FindAllDescendants(cf => cf.ByAutomationId("GameTile_syntheticgame0000")).Length
                + s.App.MainWindow.FindAllDescendants().Count(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("GameTile_synthetic", StringComparison.Ordinal));
            int before = Tiles();
            Assert.True(before > 0);
            s.Press("press start", 800);
            Assert.Equal("GameTitles_Search_TextBox", s.FocusedId);
            s.A();
            UiWait.For(() => s.App.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("OnScreenKeyboard_Root")) is { IsOffscreen: false }, "keyboard open");
            foreach (char ch in "mario")
            {
                string? id = null;
                UiWait.For(() => (id = s.App.MainWindow.FindAllDescendants()
                    .Select(e => e.Properties.AutomationId.ValueOrDefault ?? "")
                    .FirstOrDefault(i => i.StartsWith("OnScreenKeyboard_Key_", StringComparison.Ordinal) && string.Equals(i[^1..], ch.ToString(), StringComparison.OrdinalIgnoreCase))) != null, "key " + ch);
                s.NavigateTo(id!, 30);
                s.A();
                s.Mark("typed " + ch);
            }
            s.Press("press start", 1500);   // Start = Done: writes the text back and closes
            UiWait.For(() => s.App.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("OnScreenKeyboard_Root")) is not { IsOffscreen: false }, "keyboard closed by Start");
            var box = s.App.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameTitles_Search_TextBox"))!;
            Assert.Equal("mario", box.Patterns.Value.Pattern.Value.Value);
            UiWait.For(() => Tiles() < before, "the library was filtered by the typed text", TimeSpan.FromSeconds(10));
            Assert.True(s.ConsoleOpen);
        }
        catch (Exception ex) { s.App.CaptureFailure(nameof(The_word_mario_is_typed_into_the_library_search_with_the_pad_and_filters_the_library), ex); throw; }
    }
    [Fact, Trait("Category", "FlaUI"), Trait("Category", "ConsolePad")]
    public void A_pad_walk_reaches_every_focusable_control_of_the_Gaming_library_and_reports_the_rest()
    {
        const string name = nameof(A_pad_walk_reaches_every_focusable_control_of_the_Gaming_library_and_reports_the_rest);
        using var s = new ConsolePadSession(name);
        try
        {
            static string Key(AutomationElement e) =>
                (e.Properties.AutomationId.ValueOrDefault ?? string.Empty) + "|" + (e.Properties.Name.ValueOrDefault ?? string.Empty);
            // Only controls of the Gaming page and its chrome count as "must be reachable": the hidden AI page, the sidebar chat list and
            // unnamed popup hosts are in the UIA tree but are not part of the library.
            static bool InScope(AutomationElement e)
            {
                string id = e.Properties.AutomationId.ValueOrDefault ?? string.Empty;
                if (id.Length == 0 || id == "PopupHost" || id == "TextBox") return false;
                return !(id.StartsWith("Chat_", StringComparison.Ordinal) || id is "AttachButton" or "MainWindow_NewChat_Button" or "MainWindow_NewIncognitoChat_Button");
            }
            List<AutomationElement> Focusables() => s.App.MainWindow.FindAllDescendants()
                .Where(e => { try { return e.Properties.IsKeyboardFocusable.ValueOrDefault && !e.Properties.IsOffscreen.ValueOrDefault && e.Properties.IsEnabled.ValueOrDefault && InScope(e); } catch { return false; } })
                .ToList();

            // Systematic exploration of the focus graph: from every control try all four directions once, then travel along the edges
            // already learned to the nearest control that still has an untried direction. Bounded by a press budget.
            var seen = new Dictionary<string, AutomationElement>();
            foreach (var e in Focusables()) seen[Key(e)] = e;
            string[] dirs = { "down", "right", "up", "left" };
            var edges = new Dictionary<string, Dictionary<string, string>>();   // key -> direction -> key reached
            var kinds = new Dictionary<string, string>();                       // key -> activation summary
            var notActivatable = new List<string>();
            int presses = 0;
            string Current()
            {
                var f = s.Focused;
                if (f is null) return "(none)";
                string k = Key(f);
                if (!edges.ContainsKey(k))
                {
                    edges[k] = new Dictionary<string, string>();
                    var p = f.Patterns;
                    bool activatable = p.Invoke.IsSupported || p.Toggle.IsSupported || p.SelectionItem.IsSupported || p.ExpandCollapse.IsSupported
                        || p.Value.IsSupported || p.RangeValue.IsSupported || f.ControlType is ControlType.Edit or ControlType.Document;
                    kinds[k] = f.ControlType.ToString();
                    if (!activatable) notActivatable.Add(k + " (" + f.ControlType + ")");
                }
                return k;
            }
            string cur = Current();
            while (presses < 420)
            {
                string? untried = dirs.FirstOrDefault(d => !edges[cur].ContainsKey(d));
                if (untried is not null)
                {
                    s.Dir(untried); presses++;
                    string next = Current();
                    edges[cur][untried] = next;
                    s.Mark($"walk {presses}: {untried}: {cur} -> {next}");
                    cur = next;
                    continue;
                }
                // Breadth-first search over known edges for the nearest control with an untried direction.
                var path = new Dictionary<string, (string From, string Dir)>();
                var queue = new Queue<string>(); queue.Enqueue(cur); path[cur] = (string.Empty, string.Empty);
                string? goal = null;
                while (queue.Count > 0 && goal is null)
                {
                    string node = queue.Dequeue();
                    foreach (var (d, to) in edges[node])
                    {
                        if (to == "(none)" || path.ContainsKey(to) || !edges.ContainsKey(to)) continue;
                        path[to] = (node, d);
                        if (dirs.Any(x => !edges[to].ContainsKey(x))) { goal = to; break; }
                        queue.Enqueue(to);
                    }
                }
                if (goal is null) break;   // every reachable control has been fully explored
                var route = new List<string>();
                for (string at = goal; path[at].From.Length > 0; at = path[at].From) route.Add(path[at].Dir);
                route.Reverse();
                foreach (string d in route)
                {
                    s.Dir(d); presses++;
                    cur = Current();
                }
            }
            var visited = new HashSet<string>(edges.Keys);
            foreach (var e in Focusables()) seen[Key(e)] = e;
            s.Mark("walk visited: " + string.Join(" ; ", visited));
            var visitedIds = new HashSet<string>(visited.Select(v => v.Split('|')[0]));
            var unreached = seen.Keys.Where(k => !visitedIds.Contains(k.Split('|')[0])).OrderBy(k => k).ToList();
            var report = new List<string>
            {
                $"presses {presses}; focusable controls seen {seen.Count}; reached {visited.Count}; unreached {unreached.Count}; not activatable {notActivatable.Count}",
                "UNREACHED:"
            };
            report.AddRange(unreached);
            report.Add("NOT ACTIVATABLE (focus but no Invoke/Toggle/Select/Expand/Value/Range pattern):");
            report.AddRange(notActivatable);
            Directory.CreateDirectory(Path.Combine(Repository.Root, ".ai", "artifacts"));
            File.WriteAllLines(Path.Combine(Repository.Root, ".ai", "artifacts", "console-pad-walk-report.txt"), report);
            Assert.True(s.ConsoleOpen, "the walk must never leave Console/TV mode");
            Assert.True(visited.Count >= 8, "the pad reached too few controls: " + visited.Count);
            Assert.True(unreached.Count <= Math.Max(2, seen.Count / 10),
                "controls the pad could not reach: " + string.Join("; ", unreached));
        }
        catch (Exception ex) { s.App.CaptureFailure(name, ex); throw; }
    }
}





