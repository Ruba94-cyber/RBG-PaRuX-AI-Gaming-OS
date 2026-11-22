using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LauncherConfigImporterTests
{
    // Synthetic config shaped like the legacy launcher's file; no personal data.
    private const string Sample = """
    {
      "version": 1, "longPressMs": 1000, "overlayHotkey": "alt+r", "noFreezeGames": [], "tvNames": ["TV1", " TV2 "],
      "buttons": {
        "assistant": {"action": "chatgpt_voice"},
        "capture": {"action": "overlay"},
        "stadia_short": {"action": "freeze_playnite"},
        "stadia_long": {"action": "desktop_mode"}
      }
    }
    """;

    [Fact]
    public void Known_actions_become_stadia_bindings_and_unknown_ones_are_reported()
    {
        var r = LauncherConfigImporter.Parse(Sample);
        Assert.True(r.Parsed);
        Assert.Equal(3, r.Bindings.Count);
        Assert.Contains(r.Bindings, b => b.Control == "raw:11" && b.ActionId == PadActionId.MiniAgent && b.Device == "18D1:9400");
        Assert.Contains(r.Bindings, b => b.Control == "raw:12" && b.ActionId == PadActionId.OpenMenu);
        Assert.Contains(r.Bindings, b => b.Control == "raw:8" && b.ActionId == PadActionId.PauseResumeGame && b.TriggerKind == PadTrigger.Tap);
        Assert.Contains(r.NotImported, s => s.Contains("desktop_mode"));
        Assert.Contains(r.NotImported, s => s.StartsWith("longPressMs") || s.StartsWith("overlayHotkey"));
    }

    [Fact]
    public void Tv_names_are_trimmed_hints_only()
    {
        var r = LauncherConfigImporter.Parse(Sample);
        Assert.Equal(new[] { "TV1", "TV2" }, r.TvNames);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Bad_input_never_throws_and_yields_no_bindings(string? json)
    {
        var r = LauncherConfigImporter.Parse(json);
        Assert.False(r.Parsed);
        Assert.Empty(r.Bindings);
        Assert.NotEmpty(r.NotImported);
    }

    [Fact]
    public void Oversized_input_is_refused()
    {
        var r = LauncherConfigImporter.Parse(new string(' ', LauncherConfigImporter.MaxBytes + 1));
        Assert.False(r.Parsed);
    }

    [Fact]
    public void Resulting_bindings_are_valid_and_unique()
    {
        var r = LauncherConfigImporter.Parse(Sample);
        Assert.All(r.Bindings, b => Assert.True(b.IsValid()));
        Assert.Equal(r.Bindings.Count, r.Bindings.Select(b => (b.Device, b.Control, b.Trigger)).Distinct().Count());
    }
}
