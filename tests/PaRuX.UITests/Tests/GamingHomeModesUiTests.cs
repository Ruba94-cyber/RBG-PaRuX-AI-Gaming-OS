using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// The Games home in every presentation (Desktop, Console/TV), language, window size and accessibility mode, against the real app and
/// an isolated synthetic data root. Screenshots land in docs/evidence/. High contrast and reduced motion are FORCED through the app's
/// own test hooks (PARUX_GAMES_FORCE_HIGH_CONTRAST / PARUX_GAMES_FORCE_NO_MOTION): no Windows setting is touched, so they exercise
/// PaRuX's branches, not a real Windows high-contrast theme.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GamingHomeModesUiTests
{
    private const string GamingTab = "MainWindow_ModeTab_Gaming";
    private const string ConsoleButton = "MainWindow_ConsoleMode_Button";

    private static AutomationElement ById(UiTestApplication app, string id) => UiWait.ById(app.MainWindow, id);
    private static AutomationElement? TryById(UiTestApplication app, string id) => GamingSectionsPage.TryFind(app.MainWindow, id);
    private static string FocusedId(UiTestApplication app) => app.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault ?? string.Empty;

    private static void Settings(string root, params (string Key, string Value)[] values)
    {
        string path = Path.Combine(root, "settings.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var (key, value) in values) json[key] = value;
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Evidence(UiTestApplication app, string name)
    {
        string? shot = app.CaptureSnapshot(name);
        if (shot is null) return "(not captured)";
        string dir = Path.Combine(Repository.Root, "docs", "evidence");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "gaming-home-" + name + ".png");
        File.Copy(shot, target, overwrite: true);
        return target;
    }

    private static AutomationElement[] Buttons(UiTestApplication app, string prefix) =>
        app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .Where(e => (e.Properties.AutomationId.ValueOrDefault ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    private static void OpenGaming(UiTestApplication app)
    {
        new InstallProgressPage(app).Invoke(ById(app, GamingTab));
        ById(app, "GameTitles_Search_TextBox");
    }

    private static void EnterConsole(UiTestApplication app)
    {
        var button = ById(app, ConsoleButton);
        string desktopName = button.Name;
        button.Patterns.Invoke.Pattern.Invoke();
        UiWait.For(() => ById(app, ConsoleButton).Name != desktopName, "console entered", TimeSpan.FromSeconds(30));
        ById(app, "GameTitles_Search_TextBox");
        Thread.Sleep(1500);
    }

    private static double ScrollPercent(UiTestApplication app) =>
        GamingLibraryVirtualizationUiTests.PageScroller(app)?.VerticalScrollPercent.ValueOrDefault ?? -1;

    // ---------------------------------------------------------------- slice 6: quick actions in Console

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Console_quick_actions_open_in_place_keep_the_scroll_offset_and_return_focus_to_the_tile()
    {
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(300);
        using var app = new UiTestApplication(nameof(Console_quick_actions_open_in_place_keep_the_scroll_offset_and_return_focus_to_the_tile), restartDataRoot: root);
        try
        {
            OpenGaming(app);
            EnterConsole(app);
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => Buttons(app, "GameTile_").Length > 0, "tiles realized");
            GamingLibraryVirtualizationUiTests.PageScroller(app)?.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 25);
            double before = -1;
            UiWait.For(() => { double now = ScrollPercent(app); bool same = Math.Abs(now - before) < 0.01; before = now; Thread.Sleep(400); return same; }, "scroll offset settled");
            // A tile fully inside the viewport: refocusing a half-visible one would legitimately scroll it into view.
            var win = app.MainWindow.BoundingRectangle;
            AutomationElement tile = Buttons(app, "GameTile_").First(t => !t.Properties.AutomationId.Value.Contains("Play_")
                && !t.Properties.IsOffscreen.ValueOrDefault && t.BoundingRectangle.Top > win.Top + win.Height * 0.3 && t.BoundingRectangle.Bottom < win.Bottom - 40);
            string tileId = tile.Properties.AutomationId.Value;
            string key = tileId["GameTile_".Length..];
            tile.Patterns.Invoke.Pattern.Invoke();

            UiWait.For(() => TryById(app, "GameQuick_Layer") is { } layer && !layer.Properties.IsOffscreen.ValueOrDefault, "quick actions layer visible");
            UiWait.For(() => FocusedId(app) == "GameTitle_Play_" + key, "focus on the Play button of the card");
            Assert.NotNull(TryById(app, "GameDetail_Panel"));
            Assert.InRange(Math.Abs(ScrollPercent(app) - before), 0, 0.6);
            Assert.NotNull(TryById(app, tileId));   // the library behind it was not rebuilt or reflowed
            // The card holds the existing per-game settings, and every one keeps its AutomationId.
            foreach (string expander in new[] { "GameDetail_Controller_Expander_" + key, "GameDetail_Hooks_Expander_" + key })
                Assert.NotNull(TryById(app, expander));
            string shot = Evidence(app, "console-quick-actions-en");

            app.MainWindow.Focus();
            Keyboard.Type(VirtualKeyShort.ESCAPE);    // the keyboard form of pad B
            UiWait.For(() => TryById(app, "GameQuick_Layer") is null or { Properties.IsOffscreen.Value: true }, "quick actions closed by Escape");
            UiWait.For(() => FocusedId(app) == tileId, "focus back on the tile it was opened from");
            Assert.InRange(Math.Abs(ScrollPercent(app) - before), 0, 0.6);
            Assert.NotNull(UiWait.ById(app.MainWindow, ConsoleButton));   // still in Console: the first Escape only closed the card
            File.WriteAllText(Path.Combine(Repository.Root, ".ai", "artifacts", "quick-actions-evidence.txt"), shot);
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Console_quick_actions_open_in_place_keep_the_scroll_offset_and_return_focus_to_the_tile), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Desktop_detail_stays_inline_and_has_no_quick_actions_layer()
    {
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(40);
        using var app = new UiTestApplication(nameof(Desktop_detail_stays_inline_and_has_no_quick_actions_layer), restartDataRoot: root);
        try
        {
            OpenGaming(app);
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => Buttons(app, "GameTile_").Length > 0, "tiles realized");
            Buttons(app, "GameTile_").First(t => !t.Properties.AutomationId.Value.Contains("Play_")).Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => TryById(app, "GameDetail_Panel") is { } p && !p.Properties.IsOffscreen.ValueOrDefault, "inline detail");
            Assert.True(TryById(app, "GameQuick_Layer") is null or { Properties.IsOffscreen.Value: true });
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Desktop_detail_stays_inline_and_has_no_quick_actions_layer), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Console_quick_actions_reach_launch_settings_by_pad_and_B_closes_before_leaving_Console()
    {
        const string name = nameof(Console_quick_actions_reach_launch_settings_by_pad_and_B_closes_before_leaving_Console);
        using var session = new ConsolePadSession(name);
        try
        {
            session.NavigateTo("GameTile_syntheticgame0001");
            session.A();
            UiWait.For(() => session.FocusedId == "GameTitle_Play_syntheticgame0001", "card opened by A, focus on Play");
            // Launch details of the saved entry: Edit entry exposes program, arguments and working folder to the pad.
            string details = "GameDetail_LaunchDetails_synth00001";
            UiWait.For(() => session.App.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(details)) is not null, "launch details present");
            // Controls below the viewport have no usable bounds, so the pad simply presses Down (what a player does) until it lands.
            PadDownTo(session, details);
            session.A();                                  // expand
            string edit = "GameDetail_Edit_synth00001";
            PadDownTo(session, edit);
            session.A();
            PadDownTo(session, "GameDetail_Edit_Arguments_synth00001");
            PadDownTo(session, "GameDetail_Edit_Folder_synth00001");
            session.Mark("arguments and working folder reached by the pad");
            PadDownTo(session, "GameDetail_Edit_Save_synth00001");
            session.Dir("right");
            Assert.Equal("GameDetail_Edit_Cancel_synth00001", session.FocusedId);
            session.A();
            session.B();                                   // closes the card, does NOT leave Console
            UiWait.For(() => TryById(session.App, "GameQuick_Layer") is null or { Properties.IsOffscreen.Value: true }, "card closed by B");
            Assert.True(session.ConsoleOpen, "the first B only closes the card");
            UiWait.For(() => session.FocusedId == "GameTile_syntheticgame0001", "focus back on the origin tile");
            session.B();
            UiWait.For(() => !session.ConsoleOpen, "second B leaves Console");
        }
        catch (Exception ex) { session.App.CaptureFailure(name, ex); throw; }
    }

    private static void PadDownTo(ConsolePadSession session, string id, bool right = false, int max = 60)
    {
        for (int i = 0; i < max && session.FocusedId != id; i++)
        {
            string before = session.FocusedId;
            session.Dir("down");
            session.Mark($"down toward {id}: {before} -> {session.FocusedId}");
            if (right && session.FocusedId == before) session.Dir("right");
        }
        Assert.True(session.FocusedId == id, $"the pad could not reach {id}; focus is {session.FocusedId}");
    }

    // ---------------------------------------------------------------- modes and sizes (screenshots + geometry checks)

    private static void AssertNothingClippedHorizontally(UiTestApplication app, string where)
    {
        var window = app.MainWindow.BoundingRectangle;
        foreach (AutomationElement e in app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
        {
            string id = e.Properties.AutomationId.ValueOrDefault ?? string.Empty;
            if (!id.StartsWith("Game", StringComparison.Ordinal) || e.Properties.IsOffscreen.ValueOrDefault) continue;
            var r = e.BoundingRectangle;
            Assert.True(r.Left >= window.Left - 2 && r.Right <= window.Right + 2, $"{where}: {id} sticks out horizontally ({r.Left}..{r.Right} in {window.Left}..{window.Right})");
        }
    }

    [Theory, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    [InlineData("en", "shelves", 1920, 1080)]
    [InlineData("en", "shelves", 1280, 720)]
    [InlineData("en", "", 900, 600)]
    [InlineData("it", "shelves", 1280, 800)]
    [InlineData("it", "", 900, 600)]
    public void Desktop_home_renders_without_clipping_in_both_languages_and_sizes(string language, string group, int width, int height)
    {
        string name = $"desktop-{language}-{(group == "" ? "grid" : group)}-{width}x{height}";
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(120, includeCollections: true);
        Settings(root, ("UiLanguage", language), ("GameLibraryGroup", group == "" ? "none" : group));
        using var app = new UiTestApplication("DesktopHome-" + name, restartDataRoot: root);
        try
        {
            OpenGaming(app);
            app.ResizeMainWindow(width, height);
            Thread.Sleep(1200);
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app,
                () => Buttons(app, "GameShelfTile_").Length + Buttons(app, "GameTile_").Length > 0, "tiles realized");
            Assert.NotNull(TryById(app, "GameHome_Hero_Title"));
            AssertNothingClippedHorizontally(app, name);
            Evidence(app, name);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    [Theory, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    [InlineData("en", false, false)]
    [InlineData("it", false, false)]
    [InlineData("en", true, false)]
    [InlineData("en", false, true)]
    public void Console_home_renders_hero_shelves_and_extra_large_covers(string language, bool highContrast, bool noMotion)
    {
        string name = $"console-{language}{(highContrast ? "-highcontrast" : "")}{(noMotion ? "-nomotion" : "")}";
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(120, includeCollections: true);
        Settings(root, ("UiLanguage", language), ("GameLibraryGroup", "shelves"), ("GameLibraryCoverSize", "xlarge"));
        var env = new Dictionary<string, string>();
        if (highContrast) env["PARUX_GAMES_FORCE_HIGH_CONTRAST"] = "1";
        if (noMotion) env["PARUX_GAMES_FORCE_NO_MOTION"] = "1";
        using var app = new UiTestApplication("ConsoleHome-" + name, restartDataRoot: root, extraEnvironment: env);
        try
        {
            OpenGaming(app);
            EnterConsole(app);
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => Buttons(app, "GameShelfTile_").Length > 0, "shelf tiles realized");
            Assert.NotNull(TryById(app, "GameHome_Hero_Title"));
            AutomationElement tile = Buttons(app, "GameShelfTile_").First();
            tile.Focus();   // brings the first shelf row into view
            Thread.Sleep(900);
            tile = ById(app, tile.Properties.AutomationId.Value);
            // Console covers are TV-scale: clearly larger than the desktop tile footprint (about 176 x 220 DIP).
            Assert.True(tile.BoundingRectangle.Width >= 200, "console cover width " + tile.BoundingRectangle.Width);
            Keyboard.Type(VirtualKeyShort.RIGHT);
            Thread.Sleep(900);
            AssertNothingClippedHorizontally(app, name);
            Evidence(app, name);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }

    // ---------------------------------------------------------------- languages: no mixed screens

    private static readonly string[] GameViewSources =
    {
        "GameHeroCard.cs", "GameShelfFeed.cs", "GameTileGrid.cs", "GameDetailPanel.cs", "GamingHomeView.cs", "GameSideFilterPanel.cs",
    };

    [Theory, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    [InlineData("it")]
    [InlineData("en")]
    public void Home_shows_no_string_of_the_other_language(string language)
    {
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(60, includeCollections: true);
        Settings(root, ("UiLanguage", language), ("GameLibraryGroup", "shelves"));
        using var app = new UiTestApplication("HomeLanguage-" + language, restartDataRoot: root);
        try
        {
            OpenGaming(app);
            GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => Buttons(app, "GameShelfTile_").Length > 0, "tiles realized");
            Buttons(app, "GameShelfTile_").First().Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(1500);
            // Every T("en", "it") pair of the Games views; a pair whose two sides differ must never show the other side's string.
            var pair = new Regex("T\\(\"((?:[^\"\\\\]|\\\\.)+)\",\\s*\"((?:[^\"\\\\]|\\\\.)+)\"\\)");
            var foreign = new HashSet<string>(StringComparer.Ordinal);
            string views = Path.Combine(Repository.Root, "src", "PaRuX.App", "Views");
            foreach (string file in GameViewSources)
                foreach (Match m in pair.Matches(File.ReadAllText(Path.Combine(views, file))))
                {
                    string en = m.Groups[1].Value, it = m.Groups[2].Value;
                    if (en == it || en.Length < 5) continue;
                    foreign.Add(language == "it" ? en : it);
                }
            // The window's caption buttons (Minimize/Maximize/Restore/Close) are drawn and named by Windows in its own language.
            string[] caption = { "Close", "Minimize", "Maximize", "Restore", "SystemMenuBar", "TitleBar" };
            var seen = app.MainWindow.FindAllDescendants()
                .Where(e => !caption.Contains(e.Properties.AutomationId.ValueOrDefault ?? string.Empty))
                .SelectMany(e => new[] { e.Properties.Name.ValueOrDefault, e.Properties.HelpText.ValueOrDefault })
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
            string[] mixed = seen.Where(s => foreign.Contains(s!.Trim())).ToArray()!;
            File.WriteAllLines(Path.Combine(Repository.Root, ".ai", "artifacts", "home-language-" + language + ".txt"), seen!);
            Assert.True(mixed.Length == 0, "strings of the other language on screen: " + string.Join(" | ", mixed));
        }
        catch (Exception ex) { app.CaptureFailure("HomeLanguage-" + language, ex); throw; }
    }

    // ---------------------------------------------------------------- 5,000 games: honest numbers

    [Fact, Trait("Category", "Performance"), Trait("Category", "FlaUI")]
    public void Five_thousand_games_first_paint_scroll_and_memory_are_measured()
    {
        UiTestApplication.WaitForCorePortReleased();
        var lines = new List<string>();
        foreach (string group in new[] { "", "shelves" })
        {
            string root = SyntheticGameLibrary.CreateRoot(5000, includeCollections: true);
            Settings(root, ("GameLibraryGroup", group == "" ? "none" : group));
            var before = Process.GetProcessesByName("PaRuX").Select(p => p.Id).ToHashSet();
            using var app = new UiTestApplication("Games5000-" + (group == "" ? "grid" : group), restartDataRoot: root);
            try
            {
                var process = Process.GetProcessesByName("PaRuX").First(p => !before.Contains(p.Id));
                process.Refresh();
                double idleMb = process.WorkingSet64 / 1048576.0;
                var watch = Stopwatch.StartNew();
                new InstallProgressPage(app).Invoke(ById(app, GamingTab));
                UiWait.For(() => Buttons(app, "GameShelfTile_").Length + Buttons(app, "GameTile_").Length > 0, "first tile", TimeSpan.FromSeconds(120));
                double firstPaintMs = watch.Elapsed.TotalMilliseconds;
                process.Refresh();
                double afterOpenMb = process.WorkingSet64 / 1048576.0;
                var scroller = GamingLibraryVirtualizationUiTests.PageScroller(app)!;
                var stepMs = new List<double>();
                for (int i = 0; i < 40; i++)
                {
                    var step = Stopwatch.StartNew();
                    try { scroller.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement); } catch (System.Runtime.InteropServices.COMException) { }
                    // The step is complete when UI Automation answers again: a UI thread that hitches answers late.
                    _ = app.MainWindow.Properties.Name.ValueOrDefault;
                    stepMs.Add(step.Elapsed.TotalMilliseconds);
                }
                stepMs.Sort();
                process.Refresh();
                int realized = Buttons(app, "GameShelfTile_").Length + Buttons(app, "GameTile_").Length;
                lines.Add($"{(group == "" ? "flat grid" : "shelf feed")}: 5000 games; first tile {firstPaintMs:F0} ms after opening Gaming; " +
                    $"scroll step (UIA round trip) median {stepMs[stepMs.Count / 2]:F0} ms, p95 {stepMs[(int)(stepMs.Count * 0.95)]:F0} ms, max {stepMs[^1]:F0} ms; " +
                    $"realized tiles {realized}; working set {idleMb:F0} MB at start, {afterOpenMb:F0} MB after open, {process.WorkingSet64 / 1048576.0:F0} MB after scrolling");
                Assert.InRange(realized, 1, 300);
            }
            catch (Exception ex) { app.CaptureFailure("Games5000", ex); throw; }
        }
        Directory.CreateDirectory(Path.Combine(Repository.Root, "docs", "evidence"));
        File.WriteAllLines(Path.Combine(Repository.Root, "docs", "evidence", "gaming-home-5000-measurements.txt"), lines);
    }
}
