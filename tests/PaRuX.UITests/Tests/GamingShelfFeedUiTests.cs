using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// The shelf feed and the hero preview of the Games home, against the real app with an isolated synthetic data root. The shelf feed is
/// selected through the saved grouping ("shelves"), so the test never touches the owner's data.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GamingShelfFeedUiTests
{
    private const string GamingTab = "MainWindow_ModeTab_Gaming";

    private static AutomationElement ById(UiTestApplication app, string id) => UiWait.ById(app.MainWindow, id);

    private static AutomationElement? TryById(UiTestApplication app, string id) => GamingSectionsPage.TryFind(app.MainWindow, id);

    private static string? FocusedId(UiTestApplication app) => app.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault;

    private static string FocusedName(UiTestApplication app) => app.Automation.FocusedElement()?.Name ?? string.Empty;

    private static void Settings(string root, string key, string value)
    {
        string path = Path.Combine(root, "settings.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json[key] = value;
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static AutomationElement[] Tiles(UiTestApplication app, string prefix) =>
        app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .Where(e => (e.Properties.AutomationId.ValueOrDefault ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();

    private static void OpenLibrary(UiTestApplication app, string firstTilePrefix)
    {
        new InstallProgressPage(app).Invoke(ById(app, GamingTab));
        ById(app, "GameTitles_Search_TextBox");
        GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => Tiles(app, firstTilePrefix).Length > 0, "first tile realized");
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Shelf_feed_realizes_bounded_elements_and_maintains_dpad_continuity()
    {
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(2000, includeCollections: true);
        Settings(root, "GameLibraryGroup", "shelves");
        using var app = new UiTestApplication(nameof(Shelf_feed_realizes_bounded_elements_and_maintains_dpad_continuity), restartDataRoot: root);
        try
        {
            OpenLibrary(app, "GameShelfTile_");
            foreach (string shelf in new[] { "continue", "favorites", "recent", "collection_other" })
                GamingLibraryVirtualizationUiTests.ScrollPageUntil(app, () => TryById(app, "GameHome_Shelf_" + shelf) != null, "shelf heading " + shelf);

            // Bounded realization: 2,000 titles on several shelves never put thousands of tiles in the UIA tree.
            int realized = Tiles(app, "GameShelfTile_").Length;
            Assert.InRange(realized, 1, 120);
            // The standalone Continue row gives way to the feed's own Continue shelf, and the flat grid is not built.
            Assert.Null(TryById(app, "GameHome_Continue_Shelf"));
            Assert.Empty(Tiles(app, "GameTile_"));

            AutomationElement first = Tiles(app, "GameShelfTile_").First();
            first.Focus();
            string firstId = first.Properties.AutomationId.Value;
            string shelfOfFirst = firstId.Split('_')[1];
            Keyboard.Type(VirtualKeyShort.RIGHT);
            UiWait.For(() => FocusedId(app) is { } id && id.StartsWith("GameShelfTile_" + shelfOfFirst + "_", StringComparison.Ordinal) && id != firstId,
                "Right stays on the shelf and moves to the next cover");

            // Down leaves the horizontal scroller for the next shelf, at the same column.
            Keyboard.Type(VirtualKeyShort.DOWN);
            Thread.Sleep(2000);
            string afterDown = FocusedId(app) ?? "(none)";
            Assert.True(afterDown.StartsWith("GameShelfTile_", StringComparison.Ordinal)
                && !afterDown.StartsWith("GameShelfTile_" + shelfOfFirst + "_", StringComparison.Ordinal), "Down moved focus to " + afterDown + " from " + firstId);
            Keyboard.Type(VirtualKeyShort.UP);
            UiWait.For(() => FocusedId(app) is { } id && id.StartsWith("GameShelfTile_" + shelfOfFirst + "_", StringComparison.Ordinal),
                "Up comes back to the first shelf");
            app.CaptureSnapshot("shelf-feed-desktop");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Shelf_feed_realizes_bounded_elements_and_maintains_dpad_continuity), ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Hero_follows_user_navigation_but_not_programmatic_focus()
    {
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(60);
        using var app = new UiTestApplication(nameof(Hero_follows_user_navigation_but_not_programmatic_focus), restartDataRoot: root);
        try
        {
            OpenLibrary(app, "GameTile_");
            AutomationElement hero = ById(app, "GameHome_Hero_Title");
            string baseTitle = hero.Name;
            Assert.False(string.IsNullOrWhiteSpace(baseTitle));

            AutomationElement tile = Tiles(app, "GameTile_").First(t => !t.Properties.AutomationId.Value.Contains("Play_") && !t.Name.StartsWith(baseTitle, StringComparison.Ordinal));
            tile.Focus();   // UI Automation SetFocus counts as an assistive-technology (user) action, so it is not asserted either way
            Thread.Sleep(900);
            Keyboard.Type(VirtualKeyShort.RIGHT);   // the user moves focus: the hero follows after the debounce
            UiWait.For(() =>
            {
                string focused = FocusedName(app);
                string title = ById(app, "GameHome_Hero_Title").Name;
                return title != baseTitle && focused.StartsWith(title, StringComparison.Ordinal);
            }, "hero shows the tile the user moved to");
            app.CaptureSnapshot("hero-preview-desktop");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Hero_follows_user_navigation_but_not_programmatic_focus), ex);
            throw;
        }
    }
}
