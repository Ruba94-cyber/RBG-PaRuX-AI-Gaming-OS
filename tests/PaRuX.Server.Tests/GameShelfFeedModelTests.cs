using System.Diagnostics;
using PaRuX.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>Shelf partitioning, D-pad movement across shelves and the cover size choices (slices 4 and 5). Synthetic data only.</summary>
public sealed class GameShelfFeedModelTests
{
    private readonly ITestOutputHelper _output;
    public GameShelfFeedModelTests(ITestOutputHelper output) => _output = output;

    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    private static GameTitleDto Title(int n, bool favorite = false, bool played = false, bool added = false, bool hidden = false,
        string[]? collections = null)
    {
        string name = "Synthetic Game " + n.ToString("D5");
        return new GameTitleDto(GameLibraryQuery.NormalizeName(name), name, favorite, Array.Empty<string>(),
            played ? 3600 + n : 0, played ? Now.AddMinutes(-n) : null, null,
            new[] { new GameInstallationDto("i:" + n, GameInstallationSources.Manual, n.ToString(), ManualEntryKinds.Game, name)
                { InstallState = GameInstallStates.Installed, Platform = "PC" } })
        {
            Collections = collections ?? Array.Empty<string>(),
            Hidden = hidden,
            AddedUtc = added ? Now.AddHours(-n) : null,
        };
    }

    [Fact]
    public void Feed_partitions_catalog_into_distinct_shelves_without_data_duplication()
    {
        var titles = new List<GameTitleDto>
        {
            Title(1, favorite: true, played: true, added: true, collections: new[] { "Co-op", "co-op ", "Retro" }),
            Title(2, favorite: true, collections: new[] { "Retro" }),
            Title(3, played: true),
            Title(4, hidden: true, favorite: true, played: true, collections: new[] { "Retro" }),
            Title(5),
        };
        IReadOnlyList<GameShelf> shelves = GameShelfFeedModel.Build(titles);

        Assert.Equal(new[] { "continue", "favorites", "recent", "collection_retro", "collection_co_op" }, shelves.Select(s => s.Id));
        foreach (GameShelf shelf in shelves)   // a title is on a shelf at most once
            Assert.Equal(shelf.Titles.Count, shelf.Titles.Select(t => t.TitleKey).Distinct().Count());
        Assert.DoesNotContain(shelves.SelectMany(s => s.Titles), t => t.Hidden);          // hidden stay out ...
        Assert.Contains(GameShelfFeedModel.Build(titles, includeHidden: true)[1].Titles, t => t.Hidden);   // ... unless asked
        // Shelves hold the caller's instances (no copies of title data).
        Assert.All(shelves.SelectMany(s => s.Titles), t => Assert.Contains(titles, o => ReferenceEquals(o, t)));
        Assert.Equal(new[] { "Synthetic Game 00001", "Synthetic Game 00003" }, shelves[0].Titles.Select(t => t.Name));   // most recent first
        Assert.Equal(2, shelves[3].Titles.Count);                                         // "Co-op" and "co-op " are one shelf, listed once per title
        Assert.Equal("Co-op", shelves.Single(s => s.Id == "collection_co_op").Name);
    }

    [Fact]
    public void Empty_shelves_are_omitted_and_an_empty_library_has_no_shelves()
    {
        Assert.Empty(GameShelfFeedModel.Build(Array.Empty<GameTitleDto>()));
        Assert.Empty(GameShelfFeedModel.Build(new[] { Title(1), Title(2) }));
    }

    [Fact]
    public void Shelf_ids_stay_unique_and_collection_shelves_are_bounded_largest_first()
    {
        var titles = new List<GameTitleDto> { Title(1, collections: new[] { "A B", "A_B" }) };
        for (int c = 0; c < 20; c++) titles.Add(Title(100 + c, collections: new[] { "Col " + c.ToString("D2"), "Big" }));
        IReadOnlyList<GameShelf> shelves = GameShelfFeedModel.Build(titles);
        Assert.Equal(GameShelfFeedModel.MaxCollectionShelves, shelves.Count(s => s.Kind == GameShelfKind.Collection));
        Assert.Equal("Big", shelves[0].Name);
        Assert.Equal(shelves.Count, shelves.Select(s => s.Id).Distinct().Count());
        var ab = GameShelfFeedModel.Build(new[] { Title(1, collections: new[] { "A B", "A_B" }) });
        Assert.Equal(2, ab.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Same_membership_detects_a_title_that_moved_between_shelves()
    {
        GameTitleDto a = Title(1, favorite: true), b = Title(2, favorite: true);
        IReadOnlyList<GameShelf> before = GameShelfFeedModel.Build(new[] { a, b });
        Assert.True(GameShelfFeedModel.SameMembership(before, GameShelfFeedModel.Build(new[] { a, b })));
        Assert.False(GameShelfFeedModel.SameMembership(before, GameShelfFeedModel.Build(new[] { a, b with { Favorite = false } })));
    }

    [Theory]
    [InlineData(0, 3, GameLibraryHome.Direction.Down, 1, 3)]     // same column of the next shelf (D-pad down)
    [InlineData(1, 3, GameLibraryHome.Direction.Up, 0, 3)]
    [InlineData(0, 9, GameLibraryHome.Direction.Down, 1, 5)]     // the next shelf is shorter: its last tile
    [InlineData(0, 3, GameLibraryHome.Direction.Right, 0, 4)]
    [InlineData(0, 0, GameLibraryHome.Direction.Left, 0, 0)]     // stops at the start of a shelf
    [InlineData(0, 3, GameLibraryHome.Direction.Up, 0, 3)]       // first shelf: unchanged, the page takes focus further up
    [InlineData(2, 1, GameLibraryHome.Direction.Down, 2, 1)]     // last shelf: unchanged, the page takes focus further down
    [InlineData(0, 3, GameLibraryHome.Direction.First, 0, 0)]
    [InlineData(0, 3, GameLibraryHome.Direction.Last, 0, 9)]
    [InlineData(0, 3, GameLibraryHome.Direction.PageDown, 0, 8)]
    [InlineData(0, 3, GameLibraryHome.Direction.PageUp, 0, 0)]
    public void Dpad_moves_inside_a_shelf_and_between_shelves(int shelf, int index, GameLibraryHome.Direction direction, int expectedShelf, int expectedIndex)
    {
        var sizes = new[] { 10, 6, 3 };
        Assert.Equal((expectedShelf, expectedIndex), GameShelfFeedModel.Move(sizes, shelf, index, direction, pageSize: 5));
    }

    [Fact]
    public void Dpad_skips_empty_shelves_and_reports_nothing_to_focus_for_an_empty_feed()
    {
        Assert.Equal((2, 1), GameShelfFeedModel.Move(new[] { 4, 0, 5 }, 0, 1, GameLibraryHome.Direction.Down));
        Assert.Equal((0, 1), GameShelfFeedModel.Move(new[] { 4, 0, 5 }, 2, 1, GameLibraryHome.Direction.Up));
        Assert.Equal((-1, -1), GameShelfFeedModel.Move(new[] { 0, 0 }, 0, 0, GameLibraryHome.Direction.Down));
        Assert.Equal((-1, -1), GameShelfFeedModel.Move(Array.Empty<int>(), 0, 0, GameLibraryHome.Direction.Down));
    }

    [Fact]
    public void Extra_large_cover_scale_normalizes_and_preserves_aspect_ratio()
    {
        Assert.Equal(GameCoverSizes.ExtraLarge, GameCoverSizes.Normalize(" XLARGE "));
        Assert.Contains(GameCoverSizes.ExtraLarge, GameCoverSizes.All);
        Assert.True(GameCoverSizes.Scale(GameCoverSizes.ExtraLarge) > GameCoverSizes.Scale(GameCoverSizes.Large));
        // Width and height are both multiplied by the same factor, so the 176 x 220 cover keeps its 4:5 aspect ratio.
        double scale = GameCoverSizes.Scale(GameCoverSizes.ExtraLarge);
        Assert.Equal(176.0 / 220.0, 176 * scale / (220 * scale), 6);
        Assert.Equal(GameGroupModes.Shelves, GameGroupModes.Normalize("Shelves"));
        Assert.Single(GameLibraryGrouping.Build(new[] { Title(1) }, GameGroupModes.Shelves));
    }

    /// <summary>
    /// Measured, not claimed: the pure part of the feed (shelf partitioning + D-pad movement) over 5,000 synthetic games. The number is
    /// printed in the test output; the assertion is only a generous regression guard. It says nothing about WinUI realization cost,
    /// which must be measured in the real app.
    /// </summary>
    [Fact]
    public void Five_thousand_synthetic_games_partition_into_bounded_shelves_quickly()
    {
        const int total = 5000;
        var titles = new List<GameTitleDto>(total);
        for (int n = 0; n < total; n++)
        {
            string[] collections = n % 7 == 0 ? new[] { "Collection " + (n % 12) } : Array.Empty<string>();
            titles.Add(Title(n, favorite: n % 10 == 0, played: n % 25 == 0, added: n % 3 == 0, collections: collections));
        }

        GameShelfFeedModel.Build(titles);   // warm-up (JIT)
        var timer = Stopwatch.StartNew();
        IReadOnlyList<GameShelf> shelves = Array.Empty<GameShelf>();
        const int runs = 20;
        for (int i = 0; i < runs; i++) shelves = GameShelfFeedModel.Build(titles);
        timer.Stop();
        double buildMs = timer.Elapsed.TotalMilliseconds / runs;

        var sizes = shelves.Select(s => s.Titles.Count).ToList();
        timer.Restart();
        for (int i = 0; i < 100_000; i++) GameShelfFeedModel.Move(sizes, i % sizes.Count, i % 7, GameLibraryHome.Direction.Down);
        double moveMicros = timer.Elapsed.TotalMilliseconds * 1000 / 100_000;

        _output.WriteLine($"synthetic games={total} shelves={shelves.Count} tiles/shelf=[{string.Join(",", sizes)}] "
            + $"Build={buildMs:0.00} ms/run (avg of {runs}) Move={moveMicros:0.000} us/call");

        Assert.Equal(1 + 1 + 1 + GameShelfFeedModel.MaxCollectionShelves, shelves.Count);
        Assert.True(sizes[0] <= GameShelfFeedModel.ContinueLimit);
        Assert.True(sizes[2] <= GameShelfFeedModel.RecentlyAddedLimit);
        Assert.Equal(total / 10, sizes[1]);                 // favorites are every title once: no duplicates, no copies
        Assert.True(buildMs < 250, $"Build took {buildMs:0.0} ms for {total} titles.");
    }
}
