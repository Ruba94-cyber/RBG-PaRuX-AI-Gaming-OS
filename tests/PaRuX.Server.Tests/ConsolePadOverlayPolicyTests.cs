using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConsolePadOverlayPolicyTests
{
    [Theory]
    [InlineData(27)]
    [InlineData(196)]
    public void B_and_Escape_close_an_overlay_even_while_the_library_is_busy(int key)
    {
        Assert.Equal(ConsoleNavigationAction.None, ConsoleNavigationPolicy.Resolve(key, interactionBusy: true));
        Assert.Equal(ConsoleNavigationAction.Exit, ConsoleNavigationPolicy.Resolve(key, interactionBusy: true, overlayOpen: true));
        Assert.Equal(ConsoleNavigationAction.Exit, ConsoleNavigationPolicy.Resolve(key, interactionBusy: false, overlayOpen: true));
    }

    [Theory]
    [InlineData(200)] [InlineData(199)] [InlineData(215)] [InlineData(216)] [InlineData(207)] [InlineData(208)]
    public void Paging_scrolling_search_and_workspace_switch_stay_suspended_behind_an_overlay(int key)
    {
        Assert.Equal(ConsoleNavigationAction.None, ConsoleNavigationPolicy.Resolve(key, interactionBusy: false, overlayOpen: true));
    }

    [Theory]
    [InlineData(203, ConsoleNavigationAction.Up)]
    [InlineData(204, ConsoleNavigationAction.Down)]
    [InlineData(205, ConsoleNavigationAction.Left)]
    [InlineData(206, ConsoleNavigationAction.Right)]
    public void Directions_stay_available_inside_an_overlay(int key, ConsoleNavigationAction expected)
    {
        Assert.Equal(expected, ConsoleNavigationPolicy.Resolve(key, interactionBusy: true, overlayOpen: true));
    }

    [Fact]
    public void Without_an_overlay_the_old_behaviour_is_unchanged()
    {
        Assert.Equal(ConsoleNavigationPolicy.Resolve(196, true), ConsoleNavigationPolicy.Resolve(196, true, overlayOpen: false));
        Assert.Equal(ConsoleNavigationPolicy.Resolve(208, false), ConsoleNavigationPolicy.Resolve(208, false, overlayOpen: false));
    }

    [Fact]
    public void Translator_emits_B_as_exit_while_busy_only_when_an_overlay_is_open()
    {
        static Xbox360Report R(ushort buttons) => new() { Buttons = buttons };
        foreach (bool overlay in new[] { false, true })
        {
            var t = new RawPadEdgeTranslator();
            t.Feed(R(0), 0, true, overlay);
            var events = t.Feed(R(Xbox360Report.B), 16, true, overlay);
            Assert.Equal(overlay ? 1 : 0, events.Count);
            if (overlay) Assert.Equal(ConsoleNavigationAction.Exit, events[0].Action);
        }
    }

    [Fact]
    public void Dialog_button_choice_prefers_the_declared_default_then_the_safe_button()
    {
        const ConsoleDialogButton none = ConsoleDialogButton.None;
        Assert.Equal(ConsoleDialogButton.Primary, ConsolePadOverlayPolicy.PickDialogButton(ConsoleDialogButton.Primary, true, false, true));
        Assert.Equal(ConsoleDialogButton.Close, ConsolePadOverlayPolicy.PickDialogButton(ConsoleDialogButton.Close, true, false, true));
        // No declared default: Cancel/Close wins, so a destructive confirmation is never confirmed by an accidental A.
        Assert.Equal(ConsoleDialogButton.Close, ConsolePadOverlayPolicy.PickDialogButton(none, true, false, true));
        // A declared default that does not exist falls back the same way.
        Assert.Equal(ConsoleDialogButton.Close, ConsolePadOverlayPolicy.PickDialogButton(ConsoleDialogButton.Secondary, true, false, true));
        Assert.Equal(ConsoleDialogButton.Primary, ConsolePadOverlayPolicy.PickDialogButton(none, true, false, false));
        Assert.Equal(ConsoleDialogButton.Secondary, ConsolePadOverlayPolicy.PickDialogButton(none, false, true, false));
        Assert.Equal(none, ConsolePadOverlayPolicy.PickDialogButton(none, false, false, false));
    }

    [Theory]
    [InlineData(50, 0, 100, 1, 1, 51)]
    [InlineData(50, 0, 100, 1, -1, 49)]
    [InlineData(100, 0, 100, 1, 1, 100)]
    [InlineData(0, 0, 100, 1, -1, 0)]
    [InlineData(0.5, 0, 1, 0.1, 1, 0.6)]
    [InlineData(10, 0, 100, 0, 1, 11)]
    public void Slider_steps_are_clamped_and_snapped(double value, double min, double max, double step, int direction, double expected)
    {
        Assert.Equal(expected, ConsolePadOverlayPolicy.NextSliderValue(value, min, max, step, direction), 6);
    }

    [Fact]
    public void A_slider_can_cross_its_whole_range_in_a_bounded_number_of_presses()
    {
        double v = 0; int presses = 0;
        while (v < 4096 && presses < 1000) { v = ConsolePadOverlayPolicy.NextSliderValue(v, 0, 4096, 40.96, 1); presses++; }
        Assert.Equal(4096, v, 6);
        Assert.InRange(presses, 99, 101);
    }
}
