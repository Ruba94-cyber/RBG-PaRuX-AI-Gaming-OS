using System;
using System.Linq;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatModelPickerPreferencesTests
{
    private static ManagedModelInfo M(string file) => new() { FileName = file, SizeBytes = 1000 };

    [Fact]
    public void Recents_are_newest_first_deduplicated_and_capped()
    {
        var r = ChatModelPickerPolicy.RecordRecent(new[] { "a.gguf", "b.gguf", "c.gguf", "d.gguf", "e.gguf" }, "C.gguf");
        Assert.Equal(new[] { "C.gguf", "a.gguf", "b.gguf", "d.gguf", "e.gguf" }, r);
        Assert.Equal(new[] { "x.gguf" }, ChatModelPickerPolicy.RecordRecent(null, " x.gguf "));
        Assert.Equal(new[] { "a.gguf" }, ChatModelPickerPolicy.RecordRecent(new[] { "a.gguf" }, null));
    }

    [Fact]
    public void Favourite_toggle_adds_removes_and_refuses_when_full()
    {
        var (added, changed) = ChatModelPickerPolicy.ToggleFavourite(new[] { "a.gguf" }, "b.gguf");
        Assert.True(changed);
        Assert.Equal(new[] { "a.gguf", "b.gguf" }, added);
        Assert.Equal(new[] { "b.gguf" }, ChatModelPickerPolicy.ToggleFavourite(added, "A.GGUF").Favourites);
        var full = Enumerable.Range(0, ChatModelPickerPolicy.MaxFavourites).Select(i => $"m{i}.gguf").ToList();
        var (same, fullChanged) = ChatModelPickerPolicy.ToggleFavourite(full, "extra.gguf");
        Assert.False(fullChanged);
        Assert.Equal(full, same);
        Assert.False(ChatModelPickerPolicy.ToggleFavourite(full, " ").Changed);
    }

    [Fact]
    public void Quick_layout_puts_favourites_then_recents_and_drops_uninstalled_models()
    {
        var catalogue = new[] { M("a.gguf"), M("b.gguf"), M("c.gguf"), M("d.gguf") };
        var layout = ChatModelPickerPolicy.QuickLayout(catalogue, "c.gguf",
            new[] { "d.gguf", "gone.gguf" }, new[] { "b.gguf", "d.gguf", "gone2.gguf" });
        Assert.Equal(new[] { "d.gguf" }, layout.Favourites.Select(m => m.FileName).ToArray());
        Assert.Equal(new[] { "c.gguf", "b.gguf" }, layout.Recent.Select(m => m.FileName).ToArray());
        Assert.Equal(new[] { "a.gguf" }, layout.Others.Select(m => m.FileName).ToArray());
    }

    [Fact]
    public void Quick_layout_never_exceeds_the_quick_limit_and_never_repeats_a_model()
    {
        var catalogue = Enumerable.Range(0, 40).Select(i => M($"m{i:00}.gguf")).ToList();
        var layout = ChatModelPickerPolicy.QuickLayout(catalogue, "m39.gguf",
            new[] { "m05.gguf", "m06.gguf" }, new[] { "m06.gguf", "m07.gguf" });
        var all = layout.Favourites.Concat(layout.Recent).Concat(layout.Others).Select(m => m.FileName).ToList();
        Assert.True(all.Count <= ChatModelPickerPolicy.QuickChoices);
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("m39.gguf", all);
    }

    [Fact]
    public void Quick_layout_with_no_catalogue_is_empty()
    {
        var layout = ChatModelPickerPolicy.QuickLayout(null, null, null, null);
        Assert.Empty(layout.Favourites);
        Assert.Empty(layout.Recent);
        Assert.Empty(layout.Others);
    }
}
