using System.Linq;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RawPadEdgeTranslatorTests
{
    private static Xbox360Report R(ushort buttons = 0, short lx = 0, short ly = 0, short rx = 0, short ry = 0, byte lt = 0, byte rt = 0) =>
        new(buttons, lt, rt, lx, ly, rx, ry);

    private static RawPadEdgeTranslator Primed()
    {
        var t = new RawPadEdgeTranslator();
        t.Feed(R(), 0, false);
        return t;
    }

    private static ConsoleNavigationAction[] Actions(System.Collections.Generic.IReadOnlyList<RawPadNavigationEvent> e) =>
        e.Where(x => !x.IsAccept).Select(x => x.Action).ToArray();

    [Fact]
    public void FirstFrameIsOnlyABaseline()
    {
        var t = new RawPadEdgeTranslator();
        Assert.Empty(t.Feed(R((ushort)(Xbox360Report.A | Xbox360Report.DpadUp)), 0, false));
        Assert.Empty(t.Feed(R((ushort)(Xbox360Report.A | Xbox360Report.DpadUp)), 16, false));
    }

    [Theory]
    [InlineData(Xbox360Report.DpadUp, ConsoleNavigationAction.Up)]
    [InlineData(Xbox360Report.DpadDown, ConsoleNavigationAction.Down)]
    [InlineData(Xbox360Report.DpadLeft, ConsoleNavigationAction.Left)]
    [InlineData(Xbox360Report.DpadRight, ConsoleNavigationAction.Right)]
    public void DpadFiresOnceOnPress(ushort bit, ConsoleNavigationAction expected)
    {
        var t = Primed();
        Assert.Equal(new[] { expected }, Actions(t.Feed(R(bit), 16, false)));
        Assert.Empty(t.Feed(R(bit), 32, false));
    }

    [Theory]
    [InlineData(30000, 0, ConsoleNavigationAction.Right)]
    [InlineData(-30000, 0, ConsoleNavigationAction.Left)]
    [InlineData(0, 30000, ConsoleNavigationAction.Up)]
    [InlineData(0, -30000, ConsoleNavigationAction.Down)]
    public void LeftStickFiresDirection(int x, int y, ConsoleNavigationAction expected)
    {
        var t = Primed();
        Assert.Equal(new[] { expected }, Actions(t.Feed(R(lx: (short)x, ly: (short)y), 16, false)));
    }

    [Fact]
    public void StickInsideDeadZoneIsSilent()
    {
        var t = Primed();
        Assert.Empty(t.Feed(R(lx: 15000, ly: -15999, rx: 20000, ry: 15999), 16, false));
    }

    [Fact]
    public void DiagonalStickPicksTheDominantAxis()
    {
        var t = Primed();
        Assert.Equal(new[] { ConsoleNavigationAction.Right }, Actions(t.Feed(R(lx: 30000, ly: 20000), 16, false)));
    }

    [Fact]
    public void HeldDirectionRepeatsAfter400ThenEvery110()
    {
        var t = Primed();
        Assert.Single(Actions(t.Feed(R(Xbox360Report.DpadDown), 1000, false)));
        Assert.Empty(t.Feed(R(Xbox360Report.DpadDown), 1399, false));
        Assert.Single(Actions(t.Feed(R(Xbox360Report.DpadDown), 1400, false)));
        Assert.Empty(t.Feed(R(Xbox360Report.DpadDown), 1509, false));
        Assert.Single(Actions(t.Feed(R(Xbox360Report.DpadDown), 1510, false)));
        Assert.Single(Actions(t.Feed(R(Xbox360Report.DpadDown), 1620, false)));
    }

    [Fact]
    public void StalledTickDoesNotBurst()
    {
        var t = Primed();
        t.Feed(R(Xbox360Report.DpadDown), 0, false);
        Assert.Single(Actions(t.Feed(R(Xbox360Report.DpadDown), 5000, false)));
        Assert.Empty(t.Feed(R(Xbox360Report.DpadDown), 5050, false));
    }

    [Fact]
    public void ReleaseThenPressFiresImmediatelyAgain()
    {
        var t = Primed();
        t.Feed(R(Xbox360Report.DpadUp), 16, false);
        t.Feed(R(), 32, false);
        Assert.Single(Actions(t.Feed(R(Xbox360Report.DpadUp), 48, false)));
    }

    [Fact]
    public void ChangingDirectionFiresAtOnceAndRestartsTheDelay()
    {
        var t = Primed();
        t.Feed(R(Xbox360Report.DpadUp), 0, false);
        Assert.Equal(new[] { ConsoleNavigationAction.Down }, Actions(t.Feed(R(Xbox360Report.DpadDown), 100, false)));
        Assert.Empty(t.Feed(R(Xbox360Report.DpadDown), 450, false));
        Assert.Single(Actions(t.Feed(R(Xbox360Report.DpadDown), 500, false)));
    }

    [Fact]
    public void SimultaneousDpadFavoursVerticalAndNeverEmitsTwoMoves()
    {
        var t = Primed();
        Assert.Equal(new[] { ConsoleNavigationAction.Up },
            Actions(t.Feed(R((ushort)(Xbox360Report.DpadUp | Xbox360Report.DpadLeft)), 16, false)));
    }

    [Fact]
    public void SimultaneousButtonsEachFireOnce()
    {
        var t = Primed();
        ushort mask = (ushort)(Xbox360Report.A | Xbox360Report.RightShoulder | Xbox360Report.Start | Xbox360Report.B);
        var e = t.Feed(R(mask), 16, false);
        Assert.Single(e, x => x.IsAccept);
        Assert.Equal(new[] { ConsoleNavigationAction.Exit, ConsoleNavigationAction.NextPage, ConsoleNavigationAction.FocusSearch },
            Actions(e));
        Assert.Empty(t.Feed(R(mask), 32, false));
    }

    [Fact]
    public void ButtonMappings()
    {
        var t = Primed();
        Assert.Single(t.Feed(R(Xbox360Report.A), 16, false), x => x.IsAccept);
        t.Feed(R(), 32, false);
        Assert.Equal(new[] { ConsoleNavigationAction.PreviousPage }, Actions(t.Feed(R(Xbox360Report.LeftShoulder), 48, false)));
        t.Feed(R(), 64, false);
        Assert.Equal(new[] { ConsoleNavigationAction.SwitchWorkspace }, Actions(t.Feed(R(Xbox360Report.Back), 80, false)));
    }

    [Fact]
    public void TriggersNeverSwitchWorkspace()
    {
        var t = Primed();
        Assert.Empty(t.Feed(R(lt: 255), 16, false));
        Assert.Empty(t.Feed(R(lt: 255), 32, false));
        t.Feed(R(), 48, false);
        Assert.Empty(t.Feed(R(rt: 200), 64, false));
    }

    [Fact]
    public void RightStickScrollsAndRepeats()
    {
        var t = Primed();
        Assert.Equal(new[] { ConsoleNavigationAction.ScrollUp }, Actions(t.Feed(R(ry: 30000), 0, false)));
        Assert.Empty(t.Feed(R(ry: 30000), 399, false));
        Assert.Equal(new[] { ConsoleNavigationAction.ScrollUp }, Actions(t.Feed(R(ry: 30000), 400, false)));
        Assert.Equal(new[] { ConsoleNavigationAction.ScrollDown }, Actions(t.Feed(R(ry: -30000), 410, false)));
    }

    [Fact]
    public void BusyDropsExitPagingScrollSearchAndSwitchButKeepsAcceptAndMoves()
    {
        var t = Primed();
        ushort all = (ushort)(Xbox360Report.A | Xbox360Report.B | Xbox360Report.LeftShoulder | Xbox360Report.RightShoulder |
            Xbox360Report.Start | Xbox360Report.Back | Xbox360Report.DpadRight);
        var e = t.Feed(R(all, ry: 30000, lt: 255), 16, true);
        Assert.Single(e, x => x.IsAccept);
        Assert.Equal(new[] { ConsoleNavigationAction.Right }, Actions(e));
    }

    [Fact]
    public void PressWhileBusyIsConsumedNotReplayedWhenBusyEnds()
    {
        var t = Primed();
        Assert.Empty(Actions(t.Feed(R(Xbox360Report.B), 16, true)));
        Assert.Empty(t.Feed(R(Xbox360Report.B), 32, false));
        t.Feed(R(), 48, false);
        Assert.Equal(new[] { ConsoleNavigationAction.Exit }, Actions(t.Feed(R(Xbox360Report.B), 64, false)));
    }

    [Fact]
    public void ResetModelsDisconnectAndHeldButtonsDoNotFireOnReconnect()
    {
        var t = Primed();
        t.Feed(R(Xbox360Report.DpadUp), 16, false);
        t.Reset();
        ushort held = (ushort)(Xbox360Report.DpadUp | Xbox360Report.A);
        Assert.Empty(t.Feed(R(held), 5000, false));
        Assert.Empty(t.Feed(R(held), 5100, false));
        t.Feed(R(), 5200, false);
        Assert.Single(Actions(t.Feed(R(Xbox360Report.DpadUp), 5216, false)));
    }
}
