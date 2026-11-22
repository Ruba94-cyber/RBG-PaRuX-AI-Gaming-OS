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
        Assert.Contains(r.Bindings, b => b.Control == "raw:11" && b.ActionId == PadActionId.MiniAgent && b.Device == "18D1:9400");
        Assert.Contains(r.Bindings, b => b.Control == "raw:12" && b.ActionId == PadActionId.OpenMenu && b.Device == "18D1:9400");
        Assert.Contains(r.Bindings, b => b.Control == "raw:8" && b.ActionId == PadActionId.PauseResumeGame && b.TriggerKind == PadTrigger.Tap && b.Device == "18D1:9400");
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

    [Fact]
    public void Guide_button_reaches_xbox_xinput_pads_through_one_any_row()
    {
        var r = LauncherConfigImporter.Parse(Sample);
        Assert.Contains(r.Bindings, b => b.Device == "any" && b.Control == "x:Guide" && b.ActionId == PadActionId.PauseResumeGame);
    }

    [Theory]
    [InlineData("054C:05C4", "raw:12")]   // DualShock 4 v1 (PS button, preset)
    [InlineData("054C:09CC", "raw:12")]   // DualShock 4 v2
    [InlineData("054C:0CE6", "raw:12")]   // DualSense
    [InlineData("057E:2009", "raw:12")]   // Switch Pro (Home)
    [InlineData("2DC8:2101", "raw:12")]   // 8BitDo SN30 Pro
    public void Guide_button_reaches_non_stadia_pads_from_preset_data(string device, string control)
    {
        var r = LauncherConfigImporter.Parse(Sample);
        Assert.Contains(r.Bindings, b => b.Device == device && b.Control == control && b.ActionId == PadActionId.PauseResumeGame);
    }

    [Fact]
    public void Assistant_and_capture_are_not_invented_for_other_pads()
    {
        var r = LauncherConfigImporter.Parse(Sample);
        Assert.Single(r.Bindings, b => b.ActionId == PadActionId.MiniAgent);
        Assert.Single(r.Bindings, b => b.ActionId == PadActionId.OpenMenu);
        Assert.DoesNotContain(r.Bindings, b => b.Device != "18D1:9400" && b.ActionId is PadActionId.MiniAgent or PadActionId.OpenMenu);
        Assert.DoesNotContain(r.Bindings, b => b.Device == "any" && b.Control.StartsWith("raw:"));
    }

    [Fact]
    public void Hold_trigger_is_kept_on_every_resolved_pad()
    {
        var r = LauncherConfigImporter.Parse("""{"buttons":{"stadia_long":{"action":"overlay"}}}""");
        Assert.NotEmpty(r.Bindings);
        Assert.All(r.Bindings, b => { Assert.Equal(PadTrigger.Hold, b.TriggerKind); Assert.Equal(PadActionId.OpenMenu, b.ActionId); });
        Assert.Contains(r.Bindings, b => b.Device == "any" && b.Control == "x:Guide");
    }

    [Fact]
    public void Logical_resolution_covers_xinput_presets_and_leaves_unknown_pads_unmapped()
    {
        Assert.Equal("x:Guide", PadLogicalButtons.ControlFor(0x045E, 0x02FD, PadLogicalButton.Guide));   // Xbox One BT
        Assert.Null(PadLogicalButtons.ControlFor(0x045E, 0x02FD, PadLogicalButton.Assistant));
        Assert.Equal("raw:12", PadLogicalButtons.ControlFor(0x054C, 0x0CE6, PadLogicalButton.Guide));
        Assert.Null(PadLogicalButtons.ControlFor(0x054C, 0x0CE6, PadLogicalButton.Capture));
        Assert.Equal("raw:12", PadLogicalButtons.ControlFor(0x18D1, 0x9400, PadLogicalButton.Capture));
        Assert.Equal("raw:8", PadLogicalButtons.ControlFor(0x18D1, 0x9400, PadLogicalButton.Guide));
        Assert.Null(PadLogicalButtons.ControlFor(0x1234, 0x5678, PadLogicalButton.Guide));   // unknown pad: mapping wizard
    }

    [Fact]
    public void Config_with_only_assistant_has_stadia_rows_and_nothing_else()
    {
        var r = LauncherConfigImporter.Parse("""{"buttons":{"assistant":{"action":"chatgpt_voice"}}}""");
        Assert.Single(r.Bindings);
        Assert.Equal("18D1:9400", r.Bindings[0].Device);
    }

    [Fact]
    public void Merge_replaces_same_slot_keeps_unrelated_rows_and_is_pure()
    {
        var existing = new[]
        {
            PadButtonBinding.Make("18D1:9400", "raw:8", PadActionId.Screenshot),
            PadButtonBinding.Make("054C:0CE6", "raw:3", PadActionId.Dictation),
        };
        var r = LauncherConfigImporter.Parse(Sample);
        var merged = LauncherConfigImporter.Merge(existing, r.Bindings);
        Assert.Contains(merged, b => b.Device == "18D1:9400" && b.Control == "raw:8" && b.ActionId == PadActionId.PauseResumeGame);
        Assert.DoesNotContain(merged, b => b.Device == "18D1:9400" && b.Control == "raw:8" && b.ActionId == PadActionId.Screenshot);
        Assert.Contains(merged, b => b.Device == "054C:0CE6" && b.Control == "raw:3" && b.ActionId == PadActionId.Dictation);
        Assert.Equal(2, existing.Length);
        Assert.Equal(merged.Count, merged.Select(b => (b.Device, b.Control, b.Trigger)).Distinct().Count());
    }

    [Fact]
    public void Importing_never_modifies_the_source_file()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "parux-launcher-" + System.Guid.NewGuid().ToString("N") + ".json");
        System.IO.File.WriteAllText(path, Sample);
        try
        {
            byte[] before = System.IO.File.ReadAllBytes(path);
            _ = LauncherConfigImporter.Parse(System.IO.File.ReadAllText(path));
            Assert.Equal(before, System.IO.File.ReadAllBytes(path));
        }
        finally { System.IO.File.Delete(path); }
    }
}
