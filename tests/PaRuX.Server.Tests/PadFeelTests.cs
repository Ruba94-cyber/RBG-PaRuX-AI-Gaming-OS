using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PadFeelTests
{
    private static Xbox360Report R(ushort buttons = 0, byte lt = 0, byte rt = 0, short lx = 0, short ly = 0, short rx = 0, short ry = 0) =>
        new(buttons, lt, rt, lx, ly, rx, ry);

    [Fact]
    public void No_profile_or_an_invalid_one_leaves_the_report_untouched()
    {
        var report = R(Xbox360Report.A, 10, 20, 100, 200, 300, 400);
        Assert.Equal(report, PadFeel.Apply(report, null));
        Assert.Equal(report, PadFeel.Apply(report, new GamingControllerProfile { LeftDeadzone = -5 }));
    }

    [Fact]
    public void A_stick_inside_the_dead_zone_reads_as_centred_and_one_outside_is_rescaled()
    {
        var profile = new GamingControllerProfile { LeftDeadzone = 8000, RightDeadzone = 0 };
        var inside = PadFeel.Apply(R(lx: 4000, ly: 4000), profile);
        Assert.Equal((0, 0), (inside.LeftX, inside.LeftY));
        var full = PadFeel.Apply(R(lx: 32767, rx: 32767), profile);
        Assert.Equal(32767, full.LeftX);
        Assert.Equal(32767, full.RightX);
        var half = PadFeel.Apply(R(lx: 20000), profile);
        Assert.InRange(half.LeftX, 14000, 16000); // (20000-8000)/(32767-8000)*32767 ~ 15877
    }

    [Fact]
    public void A_bigger_dead_zone_swallows_more_of_a_small_deflection()
    {
        var small = PadFeel.Apply(R(lx: 9000), new GamingControllerProfile { LeftDeadzone = 4000 });
        var big = PadFeel.Apply(R(lx: 9000), new GamingControllerProfile { LeftDeadzone = 8000 });
        Assert.True(small.LeftX > big.LeftX && big.LeftX > 0);
    }

    [Fact]
    public void Vertical_inversion_flips_only_the_chosen_stick_and_survives_the_extreme_value()
    {
        var profile = new GamingControllerProfile { LeftDeadzone = 0, RightDeadzone = 0, InvertLeftY = true };
        var report = PadFeel.Apply(R(ly: 10000, ry: 10000), profile);
        Assert.Equal(-10000, report.LeftY);
        Assert.Equal(10000, report.RightY);
        Assert.Equal(32767, PadFeel.Apply(R(ly: -32768), profile).LeftY);
    }

    [Fact]
    public void Trigger_threshold_releases_light_pulls_and_keeps_a_full_pull_at_full()
    {
        var profile = new GamingControllerProfile { TriggerThreshold = 100 };
        var report = PadFeel.Apply(R(lt: 90, rt: 255), profile);
        Assert.Equal(0, report.LeftTrigger);
        Assert.Equal(255, report.RightTrigger);
        Assert.InRange(PadFeel.Apply(R(lt: 178), profile).LeftTrigger, 120, 135);
    }

    [Fact]
    public void Button_remap_moves_the_bit_and_keeps_other_buttons()
    {
        var profile = new GamingControllerProfile { ButtonRemap = new() { [Xbox360Report.A] = Xbox360Report.B } };
        var report = PadFeel.Apply(R((ushort)(Xbox360Report.A | Xbox360Report.Start)), profile);
        Assert.Equal((ushort)(Xbox360Report.B | Xbox360Report.Start), report.Buttons);
    }

    [Fact]
    public void Fingerprint_is_stable_and_changes_with_any_setting()
    {
        var a = new GamingControllerProfile { ButtonRemap = new() { [Xbox360Report.A] = Xbox360Report.B } };
        var b = new GamingControllerProfile { ButtonRemap = new() { [Xbox360Report.A] = Xbox360Report.B } };
        Assert.Equal(PadFeel.Fingerprint(a), PadFeel.Fingerprint(b));
        Assert.NotEqual(PadFeel.Fingerprint(a), PadFeel.Fingerprint(a with { InvertLeftY = true }));
        Assert.Equal(string.Empty, PadFeel.Fingerprint(null));
    }
}
