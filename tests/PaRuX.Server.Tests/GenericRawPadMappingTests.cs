using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GenericRawPadMappingTests
{
    [Theory]
    [InlineData(14, 6, 1)] // DualShock 4 raw
    [InlineData(18, 4, 1)] // Switch Pro raw
    [InlineData(15, 6, 1)] // 8BitDo / Stadia-like
    [InlineData(10, 2, 0)] // hat-less pad with a left stick
    [InlineData(8, 0, 1)]  // D-pad only pad (retro)
    [InlineData(4, 0, 1)]
    public void Gamepad_like_devices_get_a_valid_mapping(int buttons, int axes, int switches)
    {
        Assert.True(GenericRawPadMapping.TryBuild(buttons, axes, switches, out PadMapping? m, out string d));
        Assert.NotNull(m);
        Assert.Null(m!.Validate());
        Assert.False(string.IsNullOrWhiteSpace(d));
    }

    [Theory]
    [InlineData(3, 2, 0)]   // mouse-like
    [InlineData(0, 0, 0)]
    [InlineData(2, 6, 1)]   // flight stick with too few buttons
    [InlineData(3, 4, 1)]
    [InlineData(12, 1, 0)]  // button box: no hat, one axis
    [InlineData(20, 0, 0)]  // keyboard-like / macro pad
    [InlineData(-1, 6, 1)]
    public void Non_gamepads_are_refused(int buttons, int axes, int switches)
    {
        Assert.False(GenericRawPadMapping.TryBuild(buttons, axes, switches, out PadMapping? m, out string d));
        Assert.Null(m);
        Assert.Contains("not a gamepad", d);
    }

    [Fact]
    public void Standard_layout_is_mapped_for_a_full_pad()
    {
        Assert.True(GenericRawPadMapping.TryBuild(14, 6, 1, out PadMapping? m, out _));
        Assert.Equal(Xbox360Report.A, m!.Buttons[0]);
        Assert.Equal(Xbox360Report.B, m.Buttons[1]);
        Assert.Equal(Xbox360Report.X, m.Buttons[2]);
        Assert.Equal(Xbox360Report.Y, m.Buttons[3]);
        Assert.Equal(Xbox360Report.LeftShoulder, m.Buttons[4]);
        Assert.Equal(Xbox360Report.RightShoulder, m.Buttons[5]);
        Assert.Equal(Xbox360Report.Back, m.Buttons[6]);
        Assert.Equal(Xbox360Report.Start, m.Buttons[7]);
        Assert.Equal(Xbox360Report.LeftThumb, m.Buttons[8]);
        Assert.Equal(Xbox360Report.RightThumb, m.Buttons[9]);
        Assert.Equal(0, m.LeftXAxis);
        Assert.Equal(1, m.LeftYAxis);
        Assert.True(m.HatIsDpad);
        Assert.Equal(PadHatTarget.Dpad, m.HatTarget);
    }

    [Fact]
    public void Right_stick_and_triggers_are_never_guessed()
    {
        Assert.True(GenericRawPadMapping.TryBuild(14, 6, 1, out PadMapping? m, out _));
        Assert.Null(m!.RightXAxis);
        Assert.Null(m.RightYAxis);
        Assert.Null(m.LeftTriggerAxis);
        Assert.Null(m.RightTriggerAxis);
        Assert.Null(m.LeftTriggerButton);
        Assert.Null(m.RightTriggerButton);
    }

    [Fact]
    public void Only_existing_buttons_are_mapped_and_hatless_pad_has_no_dpad()
    {
        Assert.True(GenericRawPadMapping.TryBuild(6, 2, 0, out PadMapping? m, out _));
        Assert.Equal(6, m!.Buttons.Count);
        Assert.DoesNotContain(7, m.Buttons.Keys);
        Assert.False(m.HatIsDpad);
        Assert.Equal(0, m.LeftXAxis);
    }

    [Fact]
    public void Dpad_only_pad_has_no_stick()
    {
        Assert.True(GenericRawPadMapping.TryBuild(8, 0, 1, out PadMapping? m, out _));
        Assert.Null(m!.LeftXAxis);
        Assert.Null(m.LeftYAxis);
        Assert.True(m.HatIsDpad);
    }

    [Fact]
    public void Mapping_drives_navigation_frames_correctly()
    {
        Assert.True(GenericRawPadMapping.TryBuild(14, 6, 1, out PadMapping? m, out _));
        // Left stick pushed up (HID Y = -1), button 0 pressed, hat right (2); trigger-like axes stay at rest (-1).
        var buttons = new bool[14];
        buttons[0] = true;
        var state = new RawGamepadState(buttons, new double[] { 0, -1, 0, -1, -1, 0 }, 2);
        Xbox360Report r = GenericPadMapper.Map(state, m!);
        Assert.NotEqual(0, r.Buttons & Xbox360Report.A);
        Assert.NotEqual(0, r.Buttons & Xbox360Report.DpadRight);
        Assert.True(r.LeftY > 16000);
        Assert.Equal(0, r.RightY);
        Assert.Equal(0, r.LeftTrigger);
        Assert.Equal(0, r.RightTrigger);
    }

    [Fact]
    public void Each_call_returns_an_independent_mapping()
    {
        GenericRawPadMapping.TryBuild(14, 6, 1, out PadMapping? a, out _);
        GenericRawPadMapping.TryBuild(14, 6, 1, out PadMapping? b, out _);
        a!.Buttons.Clear();
        Assert.NotEmpty(b!.Buttons);
    }
}
