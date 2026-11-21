using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DictationTextTests
{
    [Theory]
    [InlineData("Um, hello there", "Hello there")]
    [InlineData("I, uh, think so", "I, think so")]
    [InlineData("So uhm we could try it", "So we could try it")]
    [InlineData("It was, um.", "It was.")]
    [InlineData("Hmm. Let me think", "Let me think")]
    [InlineData("ehm... va bene", "va bene")]
    [InlineData("Well, err, yes", "Well, yes")]
    [InlineData("UH what", "What")]
    public void Hesitation_sounds_are_dropped_and_the_sentence_stays_readable(string input, string expected) =>
        Assert.Equal(expected, DictationText.RemoveFillers(input));

    [Theory]
    [InlineData("The summer was hot")]          // "um" inside a word
    [InlineData("Her hummus is great")]         // "hm"/"um" inside words
    [InlineData("Ahmed and Umberto left")]      // names that contain the sounds
    [InlineData("A 5 mm gap")]                  // unit, not a hesitation
    [InlineData("Turn on the heater")]          // "er" inside words
    [InlineData("It's an uh-oh moment")]        // hyphenated compound is left alone
    public void Words_that_merely_contain_those_letters_are_never_touched(string input) =>
        Assert.Equal(input, DictationText.RemoveFillers(input));

    [Fact]
    public void Blank_or_filler_only_input_becomes_empty()
    {
        Assert.Equal(string.Empty, DictationText.RemoveFillers(null));
        Assert.Equal(string.Empty, DictationText.RemoveFillers("   "));
        Assert.Equal(string.Empty, DictationText.RemoveFillers("um, uh, hmm"));
    }

    [Fact]
    public void The_option_decides_and_off_only_trims()
    {
        Assert.Equal("Um, hello", DictationText.Prepare("  Um, hello ", removeFillers: false));
        Assert.Equal("Hello", DictationText.Prepare("  Um, hello ", removeFillers: true));
        Assert.Equal(string.Empty, DictationText.Prepare(null, removeFillers: false));
    }

    [Fact]
    public void A_pathological_long_input_finishes_quickly()
    {
        string long1 = string.Concat(Enumerable.Repeat("uh ", 20000)) + "hello";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string cleaned = DictationText.RemoveFillers(long1);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal("Hello".ToLowerInvariant(), cleaned.ToLowerInvariant());
    }
}

public sealed class PushToTalkGestureTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Holding_past_the_threshold_stops_and_transcribes_on_release()
    {
        var g = new PushToTalkGesture();
        Assert.True(g.OnPress(idle: true, T0));
        Assert.True(g.IsHeld);
        Assert.Equal(PushToTalkRelease.StopAndTranscribe, g.OnRelease(T0 + PushToTalkGesture.HoldThreshold));
        Assert.False(g.IsHeld);
    }

    [Fact]
    public void A_quick_tap_keeps_recording_so_click_to_stop_still_works()
    {
        var g = new PushToTalkGesture();
        Assert.True(g.OnPress(true, T0));
        Assert.Equal(PushToTalkRelease.KeepRecording, g.OnRelease(T0 + TimeSpan.FromMilliseconds(120)));
        Assert.True(g.ConsumeClick());        // the Click of the tap that started it is swallowed...
        Assert.False(g.ConsumeClick());       // ...exactly once, so the next click toggles normally
    }

    [Fact]
    public void A_press_while_busy_is_left_to_the_normal_click_handler()
    {
        var g = new PushToTalkGesture();
        Assert.False(g.OnPress(idle: false, T0));
        Assert.False(g.ConsumeClick());
        Assert.Equal(PushToTalkRelease.None, g.OnRelease(T0 + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void A_failed_start_clears_the_gesture_and_the_click_suppression()
    {
        var g = new PushToTalkGesture();
        Assert.True(g.OnPress(true, T0));
        g.OnStartFailed();
        Assert.Equal(PushToTalkRelease.None, g.OnRelease(T0 + TimeSpan.FromSeconds(2)));
        Assert.False(g.ConsumeClick());
    }

    [Fact]
    public void A_second_press_during_a_held_gesture_is_ignored()
    {
        var g = new PushToTalkGesture();
        Assert.True(g.OnPress(true, T0));
        Assert.False(g.OnPress(true, T0.AddSeconds(1)));
        Assert.Equal(PushToTalkRelease.StopAndTranscribe, g.OnRelease(T0.AddSeconds(2)));
    }

    [Fact]
    public void A_keyboard_hold_does_not_suppress_clicks_and_leftover_suppression_can_be_cleared()
    {
        var key = new PushToTalkGesture();
        Assert.True(key.OnPress(true, T0, fromClickableControl: false));
        Assert.False(key.ConsumeClick());

        var pointer = new PushToTalkGesture();
        Assert.True(pointer.OnPress(true, T0));
        pointer.OnRelease(T0.AddSeconds(1));      // released outside the control: no Click arrives
        pointer.ClearClickSuppression();
        Assert.False(pointer.ConsumeClick());     // the next real click is not swallowed
    }

    [Fact]
    public void Aborting_a_hold_ends_it_without_a_transcription_and_without_swallowing_clicks()
    {
        var g = new PushToTalkGesture();
        Assert.True(g.OnPress(true, T0, fromClickableControl: false));
        g.Abort();                                   // focus left the chat / window deactivated / Gaming began
        Assert.False(g.IsHeld);
        Assert.Equal(PushToTalkRelease.None, g.OnRelease(T0.AddSeconds(5)));
        Assert.False(g.ConsumeClick());
        Assert.True(g.OnPress(true, T0.AddSeconds(6)));   // a new hold works afterwards
    }
}
