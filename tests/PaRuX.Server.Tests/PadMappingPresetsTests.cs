using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PadMappingPresetsTests
{
    public static IEnumerable<object[]> Presets() => PadMappingPresets.All.Select(p => new object[] { p.Id });

    private static PadMappingPreset Get(string id) => PadMappingPresets.All.Single(p => p.Id == id);

    [Fact]
    public void Table_covers_every_requested_known_pad()
    {
        foreach (var (vid, pid) in new (ushort, ushort)[]
                 { (0x18D1, 0x9400), (0x2DC8, 0x2101), (0x054C, 0x09CC), (0x054C, 0x05C4), (0x054C, 0x0CE6), (0x057E, 0x2009), (0x045E, 0x02FD) })
            Assert.NotNull(PadMappingPresets.Find(vid, pid));
        Assert.Null(PadMappingPresets.Find(0x1234, 0x5678));
        Assert.Equal(PadMappingPresets.All.Count, PadMappingPresets.All.Select(p => p.Id).Distinct().Count());
        Assert.Equal(PadMappingPresets.All.Count, PadMappingPresets.All.Select(p => (p.VendorId, p.ProductId)).Distinct().Count());
    }

    [Theory, MemberData(nameof(Presets))]
    public void Preset_is_a_valid_mapping_with_single_bit_buttons(string id)
    {
        PadMapping m = Get(id).Mapping;
        Assert.Null(m.Validate());
        Assert.NotEmpty(m.Buttons);
        Assert.All(m.Buttons.Values, bit => Assert.Equal(0, bit & (bit - 1)));
        Assert.All(m.Buttons.Values, bit => Assert.NotEqual(0, bit));
        // One Xbox button per source button and one source button per Xbox button.
        Assert.Equal(m.Buttons.Count, m.Buttons.Values.Distinct().Count());
        Assert.Contains(Xbox360Report.A, m.Buttons.Values);
        Assert.Contains(Xbox360Report.Start, m.Buttons.Values);
    }

    [Theory, MemberData(nameof(Presets))]
    public void Preset_axes_are_in_range_and_not_shared_between_controls(string id)
    {
        PadMapping m = Get(id).Mapping;
        var axes = new[] { m.LeftXAxis, m.LeftYAxis, m.RightXAxis, m.RightYAxis, m.LeftTriggerAxis, m.RightTriggerAxis }
            .Where(a => a.HasValue).Select(a => a!.Value).ToList();
        Assert.All(axes, a => Assert.InRange(a, 0, 7));      // pads in the table have at most 6 axes
        Assert.Equal(axes.Count, axes.Distinct().Count());
        Assert.NotNull(m.LeftXAxis); Assert.NotNull(m.LeftYAxis); Assert.NotNull(m.RightXAxis); Assert.NotNull(m.RightYAxis);
        Assert.True(m.TriggerAxisIsSigned);                  // RawGameController axes are normalised to -1..1
        Assert.True(m.LeftTriggerAxis.HasValue || m.LeftTriggerButton.HasValue);
        Assert.True(m.RightTriggerAxis.HasValue || m.RightTriggerButton.HasValue);
        Assert.InRange(m.StickDeadzone, 0, 0.6);
    }

    [Theory, MemberData(nameof(Presets))]
    public void Preset_is_never_claimed_verified_without_physical_proof(string id)
    {
        PadMappingPreset p = Get(id);
        Assert.False(p.Verified);
        Assert.Contains("nverified", p.Note);
    }

    [Fact]
    public void Preset_mapping_clone_is_independent_so_the_table_cannot_be_edited_by_the_ui()
    {
        PadMapping clone = Get("stadia").Mapping.Clone();
        clone.Buttons.Clear();
        clone.LeftXAxis = 9;
        Assert.NotEmpty(Get("stadia").Mapping.Buttons);
        Assert.Equal(0, Get("stadia").Mapping.LeftXAxis);
    }

    [Fact]
    public void Stadia_preset_uses_the_observed_fifteen_button_six_axis_layout()
    {
        PadMapping m = Get("stadia").Mapping;
        Assert.All(m.Buttons.Keys, k => Assert.InRange(k, 0, 14));
        Assert.True(m.HatIsDpad);
    }

    // Raw indexes measured on the owner's Stadia pad (BLE, 2026-11-21) by pressing one control at a time: the preset must turn each of them
    // into the matching Xbox button, and the idle frame (sticks 0.5, triggers 0) must be neutral.
    [Theory]
    [InlineData(0, Xbox360Report.A)] [InlineData(1, Xbox360Report.B)] [InlineData(2, Xbox360Report.X)] [InlineData(3, Xbox360Report.Y)]
    [InlineData(4, Xbox360Report.LeftShoulder)] [InlineData(5, Xbox360Report.RightShoulder)]
    [InlineData(6, Xbox360Report.Back)] [InlineData(7, Xbox360Report.Start)] [InlineData(8, Xbox360Report.Guide)]
    [InlineData(9, Xbox360Report.LeftThumb)] [InlineData(10, Xbox360Report.RightThumb)]
    public void Stadia_preset_maps_the_measured_button_indexes(int rawButton, ushort expected)
    {
        var buttons = new bool[15];
        buttons[rawButton] = true;
        var state = new RawGamepadState(buttons, new double[] { 0.5, 0.5, 0.5, 0.5, 0, 0 }, -1);
        Assert.Equal(expected, GenericPadMapper.Map(state, Get("stadia").Mapping).Buttons);
    }

    [Fact]
    public void Stadia_idle_frame_and_pulled_triggers_match_what_the_pad_reported()
    {
        PadMapping m = Get("stadia").Mapping;
        var idle = GenericPadMapper.Map(new RawGamepadState(new bool[15], new double[] { 0.5, 0.5, 0.5, 0.5, 0, 0 }, -1), m);
        Assert.Equal((ushort)0, idle.Buttons);
        Assert.Equal(0, idle.LeftTrigger); Assert.Equal(0, idle.RightTrigger);
        Assert.InRange(idle.LeftX, -400, 400); Assert.InRange(idle.LeftY, -400, 400);

        var pulled = GenericPadMapper.Map(new RawGamepadState(new bool[15], new double[] { 0.5, 0.5, 0.5, 0.5, 1.0, 1.0 }, -1), m);
        Assert.Equal(255, pulled.LeftTrigger);    // LT = axis 5
        Assert.Equal(255, pulled.RightTrigger);   // RT = axis 4
    }

    [Fact]
    public void Classification_separates_xinput_pads_from_raw_pads_that_need_forwarding()
    {
        Assert.Equal(RawPadRoute.XInputDirect, PadMappingPresets.Classify(0x045E, 0x02FD));
        Assert.Equal(RawPadRoute.XInputDirect, PadMappingPresets.Classify(0x045E, 0x028E));
        Assert.Equal(RawPadRoute.NeedsForwarding, PadMappingPresets.Classify(0x18D1, 0x9400));
        Assert.Equal(RawPadRoute.NeedsForwarding, PadMappingPresets.Classify(0x2DC8, 0x2101));
        Assert.Equal(RawPadRoute.NeedsForwarding, PadMappingPresets.Classify(0x054C, 0x0CE6));
        Assert.Equal(RawPadRoute.NeedsForwarding, PadMappingPresets.Classify(0x045E, 0x0001)); // unknown Microsoft device is not assumed XInput
    }
}
