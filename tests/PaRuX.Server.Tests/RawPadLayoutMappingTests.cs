using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Synthetic raw states for several pad layouts (no hardware): presets and the generic fallback go through the mapper, and
/// the guided-mapping model detects each control kind and builds a working mapping for a pad with no preset at all.
/// </summary>
public sealed class RawPadLayoutMappingTests
{
    private static RawGamepadState State(int buttons, int axes, int hat = -1, double axisRest = 0, int[]? pressed = null,
        (int Index, double Value)[]? moved = null)
    {
        var b = new bool[buttons];
        foreach (int i in pressed ?? Array.Empty<int>()) b[i] = true;
        var a = new double[axes];
        Array.Fill(a, axisRest);
        foreach (var (index, value) in moved ?? Array.Empty<(int, double)>()) a[index] = value;
        return new RawGamepadState(b, a, hat);
    }

    // ---------------------------------------------------------------- presets through the mapper

    [Theory]
    [InlineData(0x18D1, 0x9400, 15, 6)]   // Google Stadia
    [InlineData(0x2DC8, 0x2101, 15, 6)]   // 8BitDo SN30 Pro
    [InlineData(0x054C, 0x09CC, 14, 6)]   // DualShock 4 v2
    [InlineData(0x057E, 0x2009, 14, 4)]   // Switch Pro
    public void Every_preset_validates_and_maps_south_button_hat_and_a_pushed_stick(int vid, int pid, int buttons, int axes)
    {
        PadMappingPreset preset = PadMappingPresets.Find((ushort)vid, (ushort)pid)!;
        Assert.NotNull(preset);
        Assert.Null(preset.Mapping.Validate());
        Assert.Equal(PadMappingPresets.Classify((ushort)vid, (ushort)pid), RawPadRoute.NeedsForwarding);

        // Rest: signed triggers rest at -1, sticks at 0; nothing may leak.
        var rest = State(buttons, axes, axisRest: 0, moved: Rests(preset.Mapping));
        Xbox360Report neutral = GenericPadMapper.Map(rest, preset.Mapping);
        Assert.Equal(0, neutral.Buttons);
        Assert.Equal((byte)0, neutral.LeftTrigger);
        Assert.Equal((byte)0, neutral.RightTrigger);

        int south = preset.Mapping.Buttons.First(kv => kv.Value == Xbox360Report.A).Key;
        Xbox360Report a = GenericPadMapper.Map(State(buttons, axes, pressed: new[] { south }, moved: Rests(preset.Mapping)), preset.Mapping);
        Assert.Equal(Xbox360Report.A, a.Buttons);

        Xbox360Report right = GenericPadMapper.Map(State(buttons, axes, hat: 2, moved: Rests(preset.Mapping)), preset.Mapping);
        Assert.Equal(Xbox360Report.DpadRight, right.Buttons);

        // Stick pushed up (HID value -1) must become XInput Y positive.
        int ly = preset.Mapping.LeftYAxis!.Value;
        var up = Rests(preset.Mapping).Where(r => r.Index != ly).Append((ly, -1.0)).ToArray();
        Assert.True(GenericPadMapper.Map(State(buttons, axes, moved: up), preset.Mapping).LeftY > 30000);
    }

    private static (int Index, double Value)[] Rests(PadMapping m)
    {
        double rest = m.TriggerAxisIsSigned ? -1.0 : 0.0;
        var list = new List<(int, double)>();
        if (m.LeftTriggerAxis is { } l) list.Add((l, rest));
        if (m.RightTriggerAxis is { } r) list.Add((r, rest));
        return list.ToArray();
    }

    [Fact]
    public void Stadia_triggers_are_axes_and_switch_pro_triggers_are_buttons()
    {
        PadMapping stadia = PadMappingPresets.Find(0x18D1, 0x9400)!.Mapping;
        var pulled = State(15, 6, moved: new[] { (stadia.LeftTriggerAxis!.Value, 1.0), (stadia.RightTriggerAxis!.Value, 0.0) });
        Xbox360Report s = GenericPadMapper.Map(pulled, stadia);
        Assert.Equal((byte)255, s.LeftTrigger);
        Assert.InRange(s.RightTrigger, (byte)127, (byte)128);

        PadMapping pro = PadMappingPresets.Find(0x057E, 0x2009)!.Mapping;
        Assert.Null(pro.LeftTriggerAxis);
        Xbox360Report p = GenericPadMapper.Map(State(14, 4, pressed: new[] { pro.LeftTriggerButton!.Value }), pro);
        Assert.Equal((byte)255, p.LeftTrigger);
        Assert.Equal((byte)0, p.RightTrigger);
    }

    // ---------------------------------------------------------------- generic fallback as the starting point

    [Fact]
    public void Generic_start_for_an_unknown_pad_is_built_from_its_counts_and_is_never_verified()
    {
        PadMappingPreset? start = RawMappingWizardModel.GenericStart("Mystery pad", 0x1234, 0x5678, 12, 6, 1);
        Assert.NotNull(start);
        Assert.False(start!.Verified);
        Assert.Null(start.Mapping.Validate());
        Xbox360Report r = GenericPadMapper.Map(State(12, 6, hat: 0, pressed: new[] { 0, 7 }), start.Mapping);
        Assert.Equal(Xbox360Report.A | Xbox360Report.Start | Xbox360Report.DpadUp, r.Buttons);
        // Not a pad: refused, so a mouse or keyboard is never offered.
        Assert.Null(RawMappingWizardModel.GenericStart("Mouse", 1, 2, 3, 2, 0));
        Assert.Null(RawMappingWizardModel.GenericStart("Wheel", 1, 2, 12, 1, 0));
    }

    [Fact]
    public void Generic_start_without_a_hat_still_works_for_a_pad_with_two_axes()
    {
        PadMappingPreset? start = RawMappingWizardModel.GenericStart("Old pad", 0x0079, 0x0006, 10, 4, 0);
        Assert.NotNull(start);
        Assert.False(start!.Mapping.HatIsDpad);
        Xbox360Report r = GenericPadMapper.Map(State(10, 4, moved: new[] { (0, 1.0) }), start.Mapping);
        Assert.True(r.LeftX > 20000);
    }

    // ---------------------------------------------------------------- wizard model: detection per control kind

    [Fact]
    public void Button_detection_ignores_a_button_that_was_already_held_in_the_baseline()
    {
        var baseline = State(12, 4, pressed: new[] { 3 });
        var now = State(12, 4, pressed: new[] { 3, 9 });
        DetectedControl c = RawMappingWizardModel.Detect(WizardControl.Button, baseline, now);
        Assert.Equal(DetectedKind.Button, c.Kind);
        Assert.Equal(9, c.Index);
        Assert.True(RawMappingWizardModel.Detect(WizardControl.Button, baseline, baseline).IsNone);
    }

    [Fact]
    public void Axis_detection_reports_index_direction_and_rest_and_picks_the_largest_move()
    {
        var baseline = State(8, 6, axisRest: 0);
        var now = State(8, 6, moved: new[] { (2, 0.6), (4, -1.0) });
        DetectedControl c = RawMappingWizardModel.Detect(WizardControl.Axis, baseline, now);
        Assert.Equal((DetectedKind.Axis, 4, -1), (c.Kind, c.Index, c.Direction));
        // A resting-off-centre stick does not count; only the move since the baseline does.
        var offCentre = State(8, 6, moved: new[] { (1, 0.45) });
        Assert.True(RawMappingWizardModel.Detect(WizardControl.Axis, offCentre, offCentre).IsNone);
    }

    [Theory]
    [InlineData(-1, true)]    // up lowers the value (typical HID): invert
    [InlineData(1, false)]    // up raises the value: no inversion
    public void Stick_up_decides_the_vertical_inversion(int direction, bool invert)
    {
        double value = direction;
        DetectedControl c = RawMappingWizardModel.Detect(WizardControl.StickUp, State(4, 4), State(4, 4, moved: new[] { (1, value) }));
        Assert.Equal(invert, RawMappingWizardModel.StickUpNeedsInvert(c));
    }

    [Fact]
    public void Trigger_detection_finds_an_axis_with_its_range_or_a_plain_button()
    {
        // Signed (rest -1) analogue trigger that also raises a button at full travel: the axis wins.
        var baseline = State(12, 6, moved: new[] { (5, -1.0) });
        var now = State(12, 6, pressed: new[] { 6 }, moved: new[] { (5, 1.0) });
        DetectedControl axis = RawMappingWizardModel.Detect(WizardControl.Trigger, baseline, now);
        Assert.Equal((DetectedKind.Axis, 5), (axis.Kind, axis.Index));
        var map = new PadMapping();
        RawMappingWizardModel.ApplyTrigger(map, left: true, axis);
        Assert.Equal(5, map.LeftTriggerAxis);
        Assert.True(map.TriggerAxisIsSigned);

        // Unsigned (rest 0..1) trigger.
        var unsignedNow = State(12, 6, moved: new[] { (5, 1.0) });
        DetectedControl u = RawMappingWizardModel.Detect(WizardControl.Trigger, State(12, 6), unsignedNow);
        RawMappingWizardModel.ApplyTrigger(map, left: false, u);
        Assert.False(map.TriggerAxisIsSigned);
        Assert.Equal((byte)255, GenericPadMapper.Map(unsignedNow, map).RightTrigger);

        // Digital trigger: only a button changes.
        DetectedControl btn = RawMappingWizardModel.Detect(WizardControl.Trigger, State(14, 4), State(14, 4, pressed: new[] { 6 }));
        Assert.Equal((DetectedKind.Button, 6), (btn.Kind, btn.Index));
        RawMappingWizardModel.ApplyTrigger(map, left: true, btn);
        Assert.Null(map.LeftTriggerAxis);
        Assert.Equal(6, map.LeftTriggerButton);
    }

    // ---------------------------------------------------------------- wizard model: D-pad kinds and a full build for a pad with no preset

    [Fact]
    public void Dpad_as_hat_covers_all_four_directions_in_one_step_and_clears_other_dpad_sources()
    {
        var map = new PadMapping { DpadXAxis = 6, DpadYAxis = 7 };
        map.Buttons[14] = Xbox360Report.DpadUp;
        DetectedControl hat = RawMappingWizardModel.Detect(WizardControl.Dpad, State(8, 4, hat: -1), State(8, 4, hat: 0));
        Assert.Equal(DetectedKind.Hat, hat.Kind);
        Assert.True(RawMappingWizardModel.ApplyDpad(map, DpadDirection.Up, hat));
        Assert.True(map.HatIsDpad);
        Assert.Null(map.DpadXAxis);
        Assert.DoesNotContain(map.Buttons, kv => kv.Value == Xbox360Report.DpadUp);
    }

    [Fact]
    public void Dpad_as_digital_axes_is_detected_per_direction_and_maps_both_ways()
    {
        var map = new PadMapping();
        // This pad: axis 6 horizontal (right = +1), axis 7 vertical but UP = +1 (inverted relative to the default).
        foreach (var (dir, axis, value) in new[]
                 { (DpadDirection.Up, 7, 1.0), (DpadDirection.Down, 7, -1.0), (DpadDirection.Left, 6, -1.0), (DpadDirection.Right, 6, 1.0) })
        {
            DetectedControl c = RawMappingWizardModel.Detect(WizardControl.Dpad, State(8, 8), State(8, 8, moved: new[] { (axis, value) }));
            Assert.False(RawMappingWizardModel.ApplyDpad(map, dir, c));
        }
        Assert.False(map.HatIsDpad);
        Assert.Equal((6, 7, false, true), (map.DpadXAxis!.Value, map.DpadYAxis!.Value, map.InvertDpadX, map.InvertDpadY));
        Assert.Equal(Xbox360Report.DpadUp, GenericPadMapper.Map(State(8, 8, moved: new[] { (7, 1.0) }), map).Buttons);
        Assert.Equal(Xbox360Report.DpadDown, GenericPadMapper.Map(State(8, 8, moved: new[] { (7, -1.0) }), map).Buttons);
        Assert.Equal(Xbox360Report.DpadLeft | Xbox360Report.DpadDown,
            GenericPadMapper.Map(State(8, 8, moved: new[] { (6, -1.0), (7, -1.0) }), map).Buttons);
        Assert.Equal(0, GenericPadMapper.Map(State(8, 8, moved: new[] { (6, 0.3) }), map).Buttons);
    }

    [Fact]
    public void Dpad_as_four_buttons_is_detected_and_maps()
    {
        var map = new PadMapping();
        foreach (var (dir, index) in new[] { (DpadDirection.Up, 12), (DpadDirection.Down, 13), (DpadDirection.Left, 14), (DpadDirection.Right, 15) })
        {
            DetectedControl c = RawMappingWizardModel.Detect(WizardControl.Dpad, State(16, 4), State(16, 4, pressed: new[] { index }));
            Assert.Equal(DetectedKind.Button, c.Kind);
            RawMappingWizardModel.ApplyDpad(map, dir, c);
        }
        Assert.Null(map.Validate());
        Assert.Equal(Xbox360Report.DpadLeft, GenericPadMapper.Map(State(16, 4, pressed: new[] { 14 }), map).Buttons);
    }

    [Fact]
    public void A_pad_with_no_preset_and_an_axis_dpad_with_button_triggers_can_be_mapped_end_to_end()
    {
        // Start from the generic layout, then let the wizard model override what this particular pad does differently.
        PadMapping map = RawMappingWizardModel.GenericStart("Odd pad", 0x1111, 0x2222, 13, 8, 0)!.Mapping.Clone();
        RawMappingWizardModel.ApplyTrigger(map, true, new DetectedControl(DetectedKind.Button, 10));
        RawMappingWizardModel.ApplyTrigger(map, false, new DetectedControl(DetectedKind.Button, 11));
        RawMappingWizardModel.ApplyDpad(map, DpadDirection.Right, new DetectedControl(DetectedKind.Axis, 6, 1, 0));
        RawMappingWizardModel.ApplyDpad(map, DpadDirection.Down, new DetectedControl(DetectedKind.Axis, 7, 1, 0));
        Assert.Null(map.Validate());

        Xbox360Report r = GenericPadMapper.Map(State(13, 8, pressed: new[] { 1, 10 }, moved: new[] { (6, 1.0) }), map);
        Assert.Equal(Xbox360Report.B | Xbox360Report.DpadRight, r.Buttons);
        Assert.Equal((byte)255, r.LeftTrigger);
        Assert.Equal((byte)0, r.RightTrigger);
    }

    [Fact]
    public void An_extended_mapping_survives_the_store_round_trip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RawPadMappingStore(dir);
            var map = new PadMapping { DpadXAxis = 6, DpadYAxis = 7, InvertDpadY = true, LeftTriggerButton = 10, HatIsDpad = false };
            Assert.True(store.Save("pad-xyz", map, out _));
            PadMapping loaded = store.Load("pad-xyz", out string? error)!;
            Assert.Null(error);
            Assert.Equal((6, 7, true, 10, false), (loaded.DpadXAxis!.Value, loaded.DpadYAxis!.Value, loaded.InvertDpadY, loaded.LeftTriggerButton!.Value, loaded.HatIsDpad));
            Assert.NotNull(map.Clone().DpadXAxis);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
