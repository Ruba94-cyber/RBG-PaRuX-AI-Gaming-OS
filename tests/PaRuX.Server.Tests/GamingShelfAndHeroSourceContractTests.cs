using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source-level guards for the hero preview, the shelf feed and the focus visuals of the Games home. They protect decisions that
/// have no runtime test here (no live regions, motion gating, virtualization, preserved automation ids); they do not replace a run
/// of the real app.
/// </summary>
public sealed class GamingShelfAndHeroSourceContractTests
{
    private static string Source(params string[] parts)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaRuX.sln"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("PaRuX.sln not found.");
        return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    private static string View(string file) => Source("src", "PaRuX.App", "Views", file);

    [Fact]
    public void Hero_preview_is_silent_for_assistive_technology_and_motion_is_gated()
    {
        string hero = View("GameHeroCard.cs");
        Assert.DoesNotContain("SetLiveSetting", hero);
        Assert.Contains("GameMotion.Allowed(this)", hero);
        Assert.Contains("\"GameHome_Hero_Play\"", hero);     // existing automation ids are kept
        Assert.Contains("\"GameHome_Hero_Title\"", hero);
        Assert.Contains("if (_base is null) return;", hero);   // a hero that is not there never pops in and moves the tiles
        string motion = View("GameMotion.cs");
        Assert.Contains("IsHighContrast", motion);
        Assert.Contains("AnimationsEnabled", motion);
    }

    [Fact]
    public void Hero_follows_only_user_initiated_focus_through_the_debounced_policy()
    {
        string home = View("GamingHomeView.cs");
        Assert.Contains("if (!userInitiated || _hero.Visibility != Visibility.Visible) return;", home);
        Assert.Contains("GameHeroSyncPolicy", home);
        Assert.Contains("_hero.Preview(", home);
        string grid = View("GameTileGrid.cs");
        Assert.Contains("IsUserFocus(e)", grid);
        Assert.Contains("PointerDeviceType.Mouse", grid);
    }

    [Fact]
    public void Shelf_rows_are_virtualized_horizontal_repeaters_with_bounded_pools_and_unique_ids()
    {
        string grid = View("GameTileGrid.cs");
        Assert.Contains("Presentation.Row", grid);
        Assert.Contains("new StackLayout { Orientation = Orientation.Horizontal, Spacing = TileGap }", grid);
        Assert.Contains("VerticalCacheLength = rowPresentation ? 0 : 1.0", grid);
        Assert.Contains("MaxPooled => _owner._presentation == Presentation.Row ? 12 : 48", grid);
        Assert.Contains("\"GameShelfTile_\"", grid);
        Assert.DoesNotContain("_items.Children.Add", grid);
        string feed = View("GameShelfFeed.cs");
        Assert.Contains("GameShelfFeedModel.Move(", feed);
        Assert.Contains("GameHome_Shelf_", feed);
        Assert.Contains("GameUi.T(\"Favorites\", \"Preferiti\")", feed);       // EN + IT
        Assert.Contains("GameUi.T(\"Recently added\", \"Aggiunti di recente\")", feed);
        Assert.Contains("GameUi.T(\"Continue playing\", \"Continua a giocare\")", feed);
    }

    [Fact]
    public void Existing_continue_shelf_ids_and_console_focus_path_are_preserved()
    {
        string home = View("GamingHomeView.cs");
        Assert.Contains("\"GameHome_Continue_Shelf\"", home);
        Assert.Contains("\"GameHome_Grid\"", home);
        Assert.Contains("internal bool FocusFirstGame()", home);
        string grid = View("GameTileGrid.cs");
        Assert.Contains("\"GameTile_\"", grid);
        Assert.Contains("\"GameContinue_\"", grid);
        Assert.Contains("T(\"View: Shelves\"", home);
        Assert.Contains("Vista: Scaffali", home);
        Assert.Contains("Copertine: Molto grandi", home);
    }

    [Fact]
    public void Tile_focus_visual_is_a_solid_border_with_a_composited_lift_that_never_runs_in_high_contrast()
    {
        string grid = View("GameTileGrid.cs");
        Assert.Contains("parts.Focused ? \"PaRuXBrandBrush\"", grid);
        Assert.Contains("GameMotion.Allowed(this)", grid);
        Assert.Contains("card.LosingFocus", grid);
        Assert.Contains("Canvas.SetZIndex", grid);
        Assert.Contains("ScaleTransition", grid);
    }
}
