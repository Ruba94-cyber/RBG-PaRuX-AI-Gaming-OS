using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using PaRuX.VirtualPadSim;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class VirtualPadSimScriptTests
{
    [Theory]
    [InlineData("a", Xbox360Report.A)] [InlineData("b", Xbox360Report.B)] [InlineData("x", Xbox360Report.X)] [InlineData("y", Xbox360Report.Y)]
    [InlineData("lb", Xbox360Report.LeftShoulder)] [InlineData("rb", Xbox360Report.RightShoulder)]
    [InlineData("back", Xbox360Report.Back)] [InlineData("start", Xbox360Report.Start)] [InlineData("guide", Xbox360Report.Guide)]
    [InlineData("ls", Xbox360Report.LeftThumb)] [InlineData("rs", Xbox360Report.RightThumb)]
    public void Press_every_button_uses_default_hold(string name, ushort mask)
    {
        var s = PadScript.Parse($"press {name}");
        Assert.Equal(2, s.Frames.Count);
        Assert.Equal(new PadFrame(0, new Xbox360Report(mask, 0, 0, 0, 0, 0, 0)), s.Frames[0]);
        Assert.Equal(new PadFrame(PadScript.DefaultHoldMs, default), s.Frames[1]);
        Assert.Equal(PadScript.DefaultHoldMs, s.TotalMs);
    }

    [Theory]
    [InlineData("up", Xbox360Report.DpadUp)] [InlineData("down", Xbox360Report.DpadDown)]
    [InlineData("left", Xbox360Report.DpadLeft)] [InlineData("right", Xbox360Report.DpadRight)]
    public void Dpad_every_direction_with_explicit_hold(string dir, ushort mask)
    {
        var s = PadScript.Parse($"dpad {dir} 300");
        Assert.Equal(mask, s.Frames[0].Report.Buttons);
        Assert.Equal(300, s.Frames[1].TimeMs);
        Assert.Equal(0, s.Frames[1].Report.Buttons);
    }

    [Fact]
    public void Wait_advances_the_clock_and_neutral_frame_is_first()
    {
        var s = PadScript.Parse("wait 2000; dpad right; wait 250; press a 100");
        Assert.Equal(new PadFrame(0, default), s.Frames[0]);
        Assert.Equal(2000, s.Frames[1].TimeMs);
        Assert.Equal(Xbox360Report.DpadRight, s.Frames[1].Report.Buttons);
        Assert.Equal(2120, s.Frames[2].TimeMs);      // dpad released (120 ms default)
        Assert.Equal(2250, s.Frames[3].TimeMs);      // a pressed after the 250 ms wait
        Assert.Equal(Xbox360Report.A, s.Frames[3].Report.Buttons);
        Assert.Equal(2350, s.TotalMs);
        Assert.Equal(4, s.Steps.Count);
        Assert.Equal(2250, s.Steps[3].StartMs);
        Assert.Equal(0, s.Steps[0].StartMs);         // a wait is logged when it begins
        Assert.Equal(2000, s.Steps[2].StartMs);
        Assert.Equal(250, s.Steps[2].DurationMs);
    }

    [Fact]
    public void Trailing_wait_extends_total()
    {
        Assert.Equal(500, PadScript.Parse("wait 500").TotalMs);
    }

    [Fact]
    public void Hold_trigger_and_button()
    {
        var s = PadScript.Parse("hold lt 1500; hold rt 100; hold start 50");
        Assert.Equal(new Xbox360Report(Xbox360Report.Start, 255, 255, 0, 0, 0, 0), s.Frames[0].Report);
        Assert.Equal(50, s.Frames[1].TimeMs);
        Assert.Equal(new Xbox360Report(0, 255, 255, 0, 0, 0, 0), s.Frames[1].Report);
        Assert.Equal(new Xbox360Report(0, 255, 0, 0, 0, 0, 0), s.Frames[2].Report);
        Assert.Equal(1500, s.TotalMs);
    }

    [Fact]
    public void Stick_scales_to_short_and_overlaps_a_trigger_hold()
    {
        var s = PadScript.Parse("wait 300; hold lt 1500; stick left 1 0 400; wait 500; press b");
        var atStick = s.Frames.Single(f => f.TimeMs == 300).Report;
        Assert.Equal(new Xbox360Report(0, 255, 0, 32767, 0, 0, 0), atStick);
        Assert.Equal(new Xbox360Report(0, 255, 0, 0, 0, 0, 0), s.Frames.Single(f => f.TimeMs == 700).Report);
        Assert.Equal(new Xbox360Report(Xbox360Report.B, 255, 0, 0, 0, 0, 0), s.Frames.Single(f => f.TimeMs == 800).Report);
        Assert.Equal(1800, s.TotalMs);
    }

    [Fact]
    public void Right_stick_negative_and_fractional_values()
    {
        var s = PadScript.Parse("stick right -1 0.5 200");
        Assert.Equal(new Xbox360Report(0, 0, 0, 0, 0, -32767, 16384), s.Frames[0].Report);
    }

    [Fact]
    public void Overlapping_buttons_are_ored_and_released_independently()
    {
        var s = PadScript.Parse("press a 300; press b 100");
        Assert.Equal((ushort)(Xbox360Report.A | Xbox360Report.B), s.Frames[0].Report.Buttons);
        Assert.Equal(100, s.Frames[1].TimeMs);
        Assert.Equal(Xbox360Report.A, s.Frames[1].Report.Buttons);
        Assert.Equal(300, s.Frames[2].TimeMs);
        Assert.Equal(0, s.Frames[2].Report.Buttons);
    }

    [Fact]
    public void Later_stick_verb_wins_while_active_then_earlier_resumes()
    {
        var s = PadScript.Parse("stick left 1 0 500; wait 100; stick left -1 0 100");
        Assert.Equal(32767, s.Frames.Single(f => f.TimeMs == 0).Report.LeftX);
        Assert.Equal(-32767, s.Frames.Single(f => f.TimeMs == 100).Report.LeftX);
        Assert.Equal(32767, s.Frames.Single(f => f.TimeMs == 200).Report.LeftX);
        Assert.Equal(0, s.Frames.Single(f => f.TimeMs == 500).Report.LeftX);
    }

    [Fact]
    public void Identical_consecutive_states_collapse_into_one_frame()
    {
        var s = PadScript.Parse("press a 100; wait 100; press a 100");
        Assert.Equal(2, s.Frames.Count);                   // a held 0..200 without a release/press glitch, then released
        Assert.Equal(200, s.Frames[1].TimeMs);
    }

    [Fact]
    public void Verbs_and_arguments_are_case_insensitive_and_whitespace_tolerant()
    {
        var s = PadScript.Parse("  PRESS   A ;; Dpad UP 10 ;");
        Assert.Equal(2, s.Steps.Count);
        Assert.Equal((ushort)(Xbox360Report.A | Xbox360Report.DpadUp), s.Frames[0].Report.Buttons);
    }

    [Theory]
    [InlineData("")] [InlineData("   ;  ")]
    [InlineData("jump a")] [InlineData("press")] [InlineData("press q")] [InlineData("press a 0")] [InlineData("press a -5")]
    [InlineData("press a 12x")] [InlineData("press a 1 2")] [InlineData("dpad diagonal")] [InlineData("wait")] [InlineData("wait -1")]
    [InlineData("stick middle 0 0")] [InlineData("stick left 2 0")] [InlineData("stick left 0")] [InlineData("stick left NaN 0")]
    [InlineData("hold lt")] [InlineData("hold zz 100")] [InlineData("hold lt 0")] [InlineData("hold lt 60001")]
    public void Invalid_scripts_are_rejected(string script)
    {
        Assert.Throws<PadScriptException>(() => PadScript.Parse(script));
    }

    [Theory]
    [InlineData("dpad right", 120)] [InlineData("press a 300", 300)] [InlineData("wait 200", 200)]
    [InlineData("stick left 1 0 400", 400)] [InlineData("hold lt 10000", 10000)]
    public void Interactive_line_accepts_one_verb(string line, int totalMs)
    {
        Assert.Equal(totalMs, PadScript.ParseLine(line).TotalMs);
    }

    [Theory]
    [InlineData("")] [InlineData("   ")] [InlineData("press a; press b")] [InlineData("hold lt 10001")] [InlineData("wait 10001")]
    [InlineData("jump")] [InlineData("quit")]
    public void Interactive_line_rejects_bad_input(string line)
    {
        Assert.Throws<PadScriptException>(() => PadScript.ParseLine(line));
    }

    [Fact]
    public void Interactive_session_limits_are_defined()
    {
        Assert.Equal(2000, PadScript.MaxInteractiveVerbs);
        Assert.Equal(10_000, PadScript.MaxLineDurationMs);
    }

    [Fact]
    public void Duration_limit_is_60_seconds()
    {
        Assert.Equal(60_000, PadScript.Parse("hold a 60000").TotalMs);
        Assert.Throws<PadScriptException>(() => PadScript.Parse("wait 59000; press a 1001"));
        Assert.Throws<PadScriptException>(() => PadScript.Parse("wait 30000; wait 30001"));
        Assert.Equal(60_000, PadScript.Parse("wait 60000").TotalMs);
    }

    [Fact]
    public void Verb_limit_is_200()
    {
        Assert.Equal(200, PadScript.Parse(string.Join(';', Enumerable.Repeat("wait 1", 200))).Steps.Count);
        Assert.Throws<PadScriptException>(() => PadScript.Parse(string.Join(';', Enumerable.Repeat("wait 1", 201))));
    }

    [Fact]
    public void Documented_example_parses()
    {
        var s = PadScript.Parse("wait 2000; dpad right; wait 250; press a; wait 300; hold lt 1500; stick left 1 0 400; wait 500; press b");
        Assert.Equal(9, s.Steps.Count);
        Assert.Equal(4050, s.TotalMs);   // lt held 2550..4050 is the last thing to end
    }
}
