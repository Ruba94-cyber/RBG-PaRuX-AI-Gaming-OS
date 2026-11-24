using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PadActionTests
{
    private const string Stadia = "18D1:9400";

    // Synthetic Stadia frames (measured indexes: Guide 8, Assistant 11, Capture 12).
    private static RawGamepadState Frame(int[]? down = null, int hat = -1, double axis = 0.0, int buttons = 15)
    {
        var b = new bool[buttons];
        foreach (int i in down ?? Array.Empty<int>()) b[i] = true;
        return new RawGamepadState(b, new[] { axis, axis, axis, axis, -1.0, -1.0 }, hat);
    }

    private static HashSet<string> Pressed(params int[] raw) => PadFrameControls.FromRaw(Frame(raw));

    private static PadButtonEngine StadiaEngine() =>
        new(PadActionSettings.DefaultBindings.Where(b => b.Device == Stadia));

    // ---- model ----
    [Fact]
    public void DefaultsBindOnlyTheStadiaPad()
    {
        var s = new PadActionSettings();
        Assert.Equal(3, s.EffectiveBindings.Count);
        Assert.All(s.EffectiveBindings, b => Assert.Equal(Stadia, b.Device));
        Assert.Contains(s.EffectiveBindings, b => b.Control == "raw:8" && b.ActionId == PadActionId.OpenMenu);
        Assert.Contains(s.EffectiveBindings, b => b.Control == "raw:11" && b.ActionId == PadActionId.MiniAgent);
        Assert.Contains(s.EffectiveBindings, b => b.Control == "raw:12" && b.ActionId == PadActionId.Screenshot);
    }

    [Fact]
    public void DisabledOrClearedMeansNoBindingsSoNoPolling()
    {
        Assert.Empty(new PadActionSettings { Enabled = false }.EffectiveBindings);
        Assert.Empty(new PadActionSettings { Bindings = new() }.EffectiveBindings);
    }

    [Fact]
    public void ValidationRejectsMalformedRows()
    {
        Assert.False(new PadButtonBinding("xx", "raw:1", "openMenu").IsValid());
        Assert.False(new PadButtonBinding("any", "raw:64", "openMenu").IsValid());
        Assert.False(new PadButtonBinding("any", "raw:07", "openMenu").IsValid());
        Assert.False(new PadButtonBinding("any", "x:Nope", "openMenu").IsValid());
        Assert.False(new PadButtonBinding("any", "raw:1", "launchMissiles").IsValid());
        Assert.False(new PadButtonBinding("any", "raw:1", "openMenu", "double").IsValid());
        Assert.False(new PadButtonBinding("18d1:9400", "raw:1", "openMenu").IsValid());
        Assert.True(new PadButtonBinding("any", "x:Guide", "openMenu", "hold").IsValid());
        Assert.True(new PadButtonBinding(Stadia, "hat:Up", "none").IsValid());
    }

    [Fact]
    public void DuplicateRowsAreFlaggedNotSilentlyDropped()
    {
        var rows = new[]
        {
            PadButtonBinding.Make(Stadia, "raw:8", PadActionId.OpenMenu),
            PadButtonBinding.Make(Stadia, "raw:8", PadActionId.MiniAgent),
            PadButtonBinding.Make("any", "raw:8", PadActionId.Screenshot),
        };
        Assert.Equal(2, PadActionSettings.FindConflicts(rows).Count);   // one duplicate + one any-pad override
        var kept = PadActionSettings.Normalize(rows);
        Assert.Equal(2, kept.Count);
        Assert.Equal("openMenu", kept[0].Action);
    }

    [Fact]
    public void BindingCountIsBounded()
    {
        var many = Enumerable.Range(0, 64).SelectMany(i => new[]
        {
            PadButtonBinding.Make(Stadia, PadControlId.Raw(i), PadActionId.OpenMenu),
            PadButtonBinding.Make(Stadia, PadControlId.Raw(i), PadActionId.OpenMenu, PadTrigger.Hold),
            PadButtonBinding.Make("any", PadControlId.Raw(i), PadActionId.OpenMenu),
        });
        Assert.Equal(PadActionSettings.MaxBindings, PadActionSettings.Normalize(many).Count);
    }

    [Fact]
    public void SettingsRoundTripKeepsEditedRowsAndTv()
    {
        var s = new PadActionSettings
        {
            Enabled = false,
            Bindings = new() { PadButtonBinding.Make("any", "x:Guide", PadActionId.OpenMenu, PadTrigger.Hold) },
            Tv = new() { ["x:X"] = "search" }
        };
        var back = JsonSerializer.Deserialize<PadActionSettings>(JsonSerializer.Serialize(s))!;
        Assert.False(back.Enabled);
        Assert.Single(back.Bindings!);
        Assert.Equal(PadTrigger.Hold, back.Bindings![0].TriggerKind);
        Assert.Equal("search", back.Tv!["x:X"]);
    }

    [Fact]
    public void NeverEditedSettingsPersistNoBindingsSoDefaultsKeepApplying()
    {
        string json = JsonSerializer.Serialize(new PadActionSettings());
        Assert.DoesNotContain("bindings", json);
        Assert.Equal(3, JsonSerializer.Deserialize<PadActionSettings>(json)!.EffectiveBindings.Count);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("[1,2]")]
    [InlineData("{\"bindings\":42}")]
    [InlineData("{\"bindings\":[1,\"x\",null,{\"device\":5}]}")]
    [InlineData("{\"enabled\":\"yes\",\"tv\":[]}")]
    [InlineData("{\"version\":99,\"future\":{\"a\":1}}")]
    public void CorruptOrFutureSettingsFallBackSafely(string json)
    {
        var s = JsonSerializer.Deserialize<PadActionSettings>(json);
        Assert.NotNull(s);
        Assert.True(s!.Enabled);
        Assert.All(s.EffectiveBindings, b => Assert.True(b.IsValid()));
    }

    [Fact]
    public void CorruptPadActionsInsideAppSettingsDoNotBreakTheRestOfTheFile()
    {
        var s = JsonSerializer.Deserialize<PaRuX.Models.AppSettings>("{\"Theme\":\"Light\",\"PadActions\":{\"bindings\":\"oops\",\"tv\":5}}")!;
        Assert.Equal("Light", s.Theme);
        Assert.Equal(3, s.PadActions.EffectiveBindings.Count);
    }

    [Fact]
    public void HostileFileIsBoundedOnLoad()
    {
        string rows = string.Join(",", Enumerable.Range(0, 5000).Select(i =>
            $"{{\"device\":\"any\",\"control\":\"raw:{i % 64}\",\"action\":\"openMenu\",\"trigger\":\"{(i % 2 == 0 ? "tap" : "hold")}\"}}"));
        var s = JsonSerializer.Deserialize<PadActionSettings>("{\"bindings\":[" + rows + "]}")!;
        Assert.True(s.Bindings!.Count <= PadActionSettings.MaxBindings);
        string huge = new string('a', 100000);
        var t = JsonSerializer.Deserialize<PadActionSettings>("{\"bindings\":[{\"device\":\"any\",\"control\":\"raw:1\",\"action\":\"" + huge + "\"}]}")!;
        Assert.Empty(t.Bindings!);
    }

    // ---- control catalog ----
    [Fact]
    public void StadiaListsEveryButtonWithMeasuredNamesAndTheHat()
    {
        var rows = PadControlCatalog.ForRaw(0x18D1, 0x9400, 15, true);
        Assert.Equal(15 + 4, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
        Assert.Contains("Assistant", rows.First(r => r.Id == "raw:11").En);
        Assert.Contains("Capture", rows.First(r => r.Id == "raw:12").En);
        Assert.Contains("Stadia", rows.First(r => r.Id == "raw:8").En);
    }

    [Fact]
    public void XInputListsEveryXboxControlOnce()
    {
        var rows = PadControlCatalog.ForXInput();
        Assert.Equal(PadControlId.XboxNames.Length, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
        Assert.All(rows, r => Assert.True(PadControlId.IsValid(r.Id)));
    }

    // ---- engine: press / hold / debounce ----
    [Fact]
    public void TapOnlyFiresOncePerPressOnPress()
    {
        var e = StadiaEngine();
        e.Feed(Pressed(), 0);
        Assert.Equal(PadActionId.OpenMenu, Assert.Single(e.Feed(Pressed(8), 100)).ActionId);
        Assert.Empty(e.Feed(Pressed(8), 133));
        Assert.Empty(e.Feed(Pressed(8), 5000));
        Assert.Empty(e.Feed(Pressed(), 5100));
    }

    [Fact]
    public void ButtonHeldAtConnectIsBaselineUntilReleased()
    {
        var e = StadiaEngine();
        Assert.Empty(e.Feed(Pressed(8), 0));
        Assert.Empty(e.Feed(Pressed(8), 1000));
        Assert.Empty(e.Feed(Pressed(), 1100));
        Assert.Single(e.Feed(Pressed(8), 1400));
    }

    [Fact]
    public void ResetMakesTheNextFrameANewBaseline()
    {
        var e = StadiaEngine();
        e.Feed(Pressed(), 0);
        e.Reset();
        Assert.Empty(e.Feed(Pressed(8), 100));
    }

    [Fact]
    public void ControlsWithoutBindingsDoNothing()
    {
        var e = StadiaEngine();
        e.Feed(Pressed(), 0);
        Assert.Empty(e.Feed(Pressed(0, 1, 2, 3, 4, 9, 10), 100));
    }

    [Fact]
    public void TapAndHoldOnTheSameControlAreDistinguished()
    {
        var rows = new[]
        {
            PadButtonBinding.Make(Stadia, "raw:8", PadActionId.OpenMenu),
            PadButtonBinding.Make(Stadia, "raw:8", PadActionId.OpenSettings, PadTrigger.Hold),
        };
        var e = new PadButtonEngine(rows);
        e.Feed(Pressed(), 0);
        Assert.Empty(e.Feed(Pressed(8), 100));
        Assert.Equal(PadActionId.OpenMenu, Assert.Single(e.Feed(Pressed(), 300)).ActionId);

        e = new PadButtonEngine(rows);
        e.Feed(Pressed(), 0);
        Assert.Empty(e.Feed(Pressed(8), 100));
        Assert.Empty(e.Feed(Pressed(8), 600));
        Assert.Equal(PadActionId.OpenSettings, Assert.Single(e.Feed(Pressed(8), 700)).ActionId);
        Assert.Empty(e.Feed(Pressed(8), 2000));
        Assert.Empty(e.Feed(Pressed(), 2100));
    }

    [Fact]
    public void HoldOnlyIgnoresShortPresses()
    {
        var e = new PadButtonEngine(new[] { PadButtonBinding.Make(Stadia, "raw:11", PadActionId.MiniAgent, PadTrigger.Hold) });
        e.Feed(Pressed(), 0);
        Assert.Empty(e.Feed(Pressed(11), 100));
        Assert.Empty(e.Feed(Pressed(), 300));
        Assert.Empty(e.Feed(Pressed(11), 1000));
        Assert.Single(e.Feed(Pressed(11), 1600));
    }

    [Fact]
    public void TwoControlsPressedWithinTheDebounceWindowFireOnlyOnce()
    {
        var e = StadiaEngine();
        e.Feed(Pressed(), 0);
        Assert.Single(e.Feed(Pressed(8, 11), 100));
        e.Feed(Pressed(), 150);
        Assert.Empty(e.Feed(Pressed(12), 200));
        e.Feed(Pressed(), 250);
        Assert.Single(e.Feed(Pressed(12), 400));
    }

    [Fact]
    public void PadSpecificRowWinsOverAnyPadRow()
    {
        var all = new[]
        {
            PadButtonBinding.Make("any", "raw:8", PadActionId.Screenshot),
            PadButtonBinding.Make(Stadia, "raw:8", PadActionId.OpenMenu),
            PadButtonBinding.Make("any", "raw:3", PadActionId.OpenSettings),
        };
        var rows = PadBindingResolver.ForDevice(all, Stadia);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Control == "raw:8" && r.ActionId == PadActionId.OpenMenu);
        var other = PadBindingResolver.ForDevice(all, "2DC8:2101");
        Assert.Equal(2, other.Count);
        Assert.All(other, r => Assert.Equal("any", r.Device));
    }

    [Fact]
    public void XboxNamedControlsComeFromTheReport()
    {
        var r = new Xbox360Report((ushort)(Xbox360Report.Guide | Xbox360Report.DpadUp), 200, 0, 0, 20000, 0, -20000);
        var set = PadFrameControls.FromXbox(r);
        Assert.Contains("x:Guide", set);
        Assert.Contains("x:DUp", set);
        Assert.Contains("x:LT", set);
        Assert.Contains("x:LSUp", set);
        Assert.Contains("x:RSDown", set);
        Assert.DoesNotContain("x:RT", set);
    }

    [Fact]
    public void AllZeroAxisFramesAreAsleepAndAnAwakeCenteredPadIsNot()
    {
        var asleep = new RawGamepadState(new bool[15], Enumerable.Repeat(-1.0, 6).ToArray(), -1);
        Assert.True(PadFrameControls.IsAsleepFrame(asleep));
        Assert.False(PadFrameControls.IsAsleepFrame(Frame()));
        Assert.False(PadFrameControls.IsAsleepFrame(Frame(hat: 0, axis: -1.0)));
    }

    // ---- poller ----
    private sealed class FakeSource : IRawGamepadSource
    {
        public string DeviceId { get; }
        public Func<RawGamepadState?> Next = () => Frame();
        public int Reads;
        public FakeSource(string id) => DeviceId = id;
        public bool TryRead(out RawGamepadState? state)
        {
            Reads++;
            state = Next();
            if (state is null) throw new System.Runtime.InteropServices.COMException("bluetooth");
            return true;
        }
    }

    private static (PadActionPoller Poller, long[] Clock, List<(PadButtonBinding, string)> Fired) MakePoller(
        IReadOnlyList<PadDeviceInfo> devices, Dictionary<string, FakeSource> sources, Func<int, Xbox360Report?>? xinput = null)
    {
        var clock = new long[] { 0 };
        var fired = new List<(PadButtonBinding, string)>();
        var p = new PadActionPoller(() => devices, id => sources.GetValueOrDefault(id), xinput ?? (_ => null), () => clock[0]);
        p.ActionFired += (b, d) => fired.Add((b, d));
        return (p, clock, fired);
    }

    [Fact]
    public void NoBindingsMeansNothingIsOpenedOrRead()
    {
        var src = new FakeSource("s1");
        int listed = 0;
        var p = new PadActionPoller(() => { listed++; return new[] { new PadDeviceInfo("s1", "Stadia", 0x18D1, 0x9400) }; },
            _ => src, _ => null, () => 0);
        p.SetBindings(Array.Empty<PadButtonBinding>());
        Assert.False(p.WantsToRun);
        p.Poll();
        Assert.Equal(0, listed);
        Assert.Equal(0, src.Reads);
        Assert.False(p.WantsFastTick);
    }

    [Fact]
    public void StadiaGuideAssistantCaptureFireTheirDefaultActions()
    {
        var src = new FakeSource("s1");
        var (p, clock, fired) = MakePoller(new[] { new PadDeviceInfo("s1", "Stadia", 0x18D1, 0x9400) }, new() { ["s1"] = src });
        p.SetBindings(new PadActionSettings().EffectiveBindings);
        void Tick(int[]? down, long t) { clock[0] = t; src.Next = () => Frame(down); p.Poll(); }
        Tick(null, 0);
        Assert.True(p.WantsFastTick);
        Tick(new[] { 8 }, 100);
        Tick(null, 200);
        Tick(new[] { 11 }, 500);
        Tick(null, 600);
        Tick(new[] { 12 }, 900);
        Assert.Equal(new[] { PadActionId.OpenMenu, PadActionId.MiniAgent, PadActionId.Screenshot }, fired.Select(f => f.Item1.ActionId).ToArray());
        Assert.All(fired, f => Assert.Equal(Stadia, f.Item2));
    }

    [Fact]
    public void PadsWithoutBindingsAreNeverOpened()
    {
        var other = new FakeSource("o1");
        var (p, _, _) = MakePoller(new[] { new PadDeviceInfo("o1", "8BitDo", 0x2DC8, 0x2101) }, new() { ["o1"] = other });
        p.SetBindings(new PadActionSettings().EffectiveBindings);
        p.Poll();
        Assert.Equal(0, other.Reads);
        Assert.Equal(0, p.OpenDeviceCount);
    }

    [Fact]
    public void OneThrowingPadNeverStopsTheOthersAndIsRetriedLater()
    {
        var bad = new FakeSource("bad") { Next = () => null };
        var good = new FakeSource("good");
        var (p, clock, fired) = MakePoller(new[]
        {
            new PadDeviceInfo("bad", "8BitDo", 0x2DC8, 0x2101),
            new PadDeviceInfo("good", "Stadia", 0x18D1, 0x9400),
        }, new() { ["bad"] = bad, ["good"] = good });
        p.SetBindings(new[] { PadButtonBinding.Make("any", "raw:8", PadActionId.OpenMenu) });
        void Tick(int[]? down, long t) { clock[0] = t; good.Next = () => Frame(down); p.Poll(); }
        Tick(null, 0);
        Tick(new[] { 8 }, 100);
        Assert.Single(fired);
        Assert.Equal(1, bad.Reads);
        Tick(null, 10000);
        Tick(null, 20000);
        Assert.Equal(1, bad.Reads);
        Tick(null, 40000);
        Assert.True(bad.Reads >= 2);
    }

    [Fact]
    public void AsleepPadFramesAreIgnoredAndWakingDoesNotFire()
    {
        var src = new FakeSource("s1");
        var (p, clock, fired) = MakePoller(new[] { new PadDeviceInfo("s1", "Stadia", 0x18D1, 0x9400) }, new() { ["s1"] = src });
        p.SetBindings(new PadActionSettings().EffectiveBindings);
        var asleep = new RawGamepadState(new bool[15], Enumerable.Repeat(-1.0, 6).ToArray(), -1);
        clock[0] = 0; src.Next = () => asleep; p.Poll();
        clock[0] = 100; p.Poll();
        clock[0] = 200; src.Next = () => Frame(new[] { 8 }); p.Poll();
        Assert.Empty(fired);
        clock[0] = 300; src.Next = () => Frame(); p.Poll();
        clock[0] = 600; src.Next = () => Frame(new[] { 8 }); p.Poll();
        Assert.Single(fired);
    }

    [Fact]
    public void ObserverSuppressesActionsAndSeesPressedControls()
    {
        var src = new FakeSource("s1");
        var (p, clock, fired) = MakePoller(new[] { new PadDeviceInfo("s1", "Stadia", 0x18D1, 0x9400) }, new() { ["s1"] = src });
        var seen = new List<string>();
        p.Observer = (dev, pressed) => seen.AddRange(pressed);
        p.SetBindings(Array.Empty<PadButtonBinding>());
        Assert.True(p.WantsToRun);
        clock[0] = 0; p.Poll();
        clock[0] = 100; src.Next = () => Frame(new[] { 11 }); p.Poll();
        Assert.Contains("raw:11", seen);
        Assert.Empty(fired);
    }

    [Fact]
    public void XInputAnyPadBindingFiresFromTheNamedGuideButton()
    {
        ushort buttons = 0;
        var (p, clock, fired) = MakePoller(Array.Empty<PadDeviceInfo>(), new(),
            slot => slot == 0 ? new Xbox360Report(buttons, 0, 0, 0, 0, 0, 0) : null);
        p.SetBindings(new[] { PadButtonBinding.Make("any", "x:Guide", PadActionId.OpenMenu) });
        clock[0] = 0; p.Poll();
        buttons = Xbox360Report.Guide; clock[0] = 100; p.Poll();
        Assert.Equal(PadActionId.OpenMenu, Assert.Single(fired).Item1.ActionId);
    }

    [Fact]
    public void SamePressSeenTwiceThroughTwoPathsFiresOnce()
    {
        var src = new FakeSource("s1");
        ushort buttons = 0;
        var (p, clock, fired) = MakePoller(new[] { new PadDeviceInfo("s1", "Stadia", 0x18D1, 0x9400) }, new() { ["s1"] = src },
            slot => slot == 0 ? new Xbox360Report(buttons, 0, 0, 0, 0, 0, 0) : null);
        p.SetBindings(new[]
        {
            PadButtonBinding.Make(Stadia, "raw:8", PadActionId.OpenMenu),
            PadButtonBinding.Make("any", "x:Guide", PadActionId.OpenMenu),
        });
        clock[0] = 0; p.Poll();
        buttons = Xbox360Report.Guide; src.Next = () => Frame(new[] { 8 }); clock[0] = 100; p.Poll();
        Assert.Single(fired);
    }

    // ---- hard-off refusal matrix: (action, gamingActive, onGamingWorkspace) -> allowed ----
    [Theory]
    [InlineData(PadActionId.MiniAgent, true, false, false)]
    [InlineData(PadActionId.MiniAgent, true, true, false)]
    [InlineData(PadActionId.MiniAgent, false, true, true)]
    [InlineData(PadActionId.Dictation, true, false, false)]
    [InlineData(PadActionId.Dictation, false, false, true)]
    [InlineData(PadActionId.OpenMenu, true, true, true)]
    [InlineData(PadActionId.OpenMenu, true, false, true)]
    [InlineData(PadActionId.GamingLibrary, true, true, true)]
    [InlineData(PadActionId.Screenshot, true, false, true)]
    [InlineData(PadActionId.OpenSettings, true, false, true)]
    [InlineData(PadActionId.None, true, false, true)]
    [InlineData(PadActionId.SwitchWorkspace, true, true, false)]
    [InlineData(PadActionId.SwitchWorkspace, true, false, true)]
    [InlineData(PadActionId.SwitchWorkspace, false, true, true)]
    [InlineData(PadActionId.PauseResumeGame, false, true, false)]
    [InlineData(PadActionId.PauseResumeGame, true, true, true)]
    [InlineData(PadActionId.LaunchPlayniteConsole, true, true, false)]
    [InlineData(PadActionId.LaunchPlayniteConsole, false, false, true)]
    [InlineData(PadActionId.LaunchPlayniteDesktop, true, false, false)]
    [InlineData(PadActionId.LaunchPlayniteDesktop, false, true, true)]
    public void HardOffMatrix(PadActionId action, bool gamingActive, bool onGaming, bool allowed)
    {
        var d = PadActionPolicy.Decide(action, new PadActionContext(gamingActive, true, onGaming));
        Assert.Equal(allowed, d.Allowed);
        if (!d.Allowed) { Assert.NotEmpty(d.ReasonEn); Assert.NotEmpty(d.ReasonIt); }
    }

    [Fact]
    public void EveryAiActionIsRefusedWhileGamingAndEveryActionHasBothLabels()
    {
        Assert.True(PadActionCatalog.Get(PadActionId.MiniAgent).NeedsLocalAi);
        Assert.True(PadActionCatalog.Get(PadActionId.Dictation).NeedsLocalAi);
        Assert.All(PadActionCatalog.All, a => { Assert.NotEmpty(a.En); Assert.NotEmpty(a.It); });
        foreach (var a in PadActionCatalog.All.Where(a => a.NeedsLocalAi))
            Assert.False(PadActionPolicy.Decide(a.Id, new PadActionContext(true, false, false)).Allowed);
        Assert.Equal(PadActionCatalog.All.Count, PadActionCatalog.All.Select(a => a.Key).Distinct().Count());
    }

    [Fact]
    public void PlaynitePadActionsHaveStableKeysAndSurviveSettingsRoundTrip()
    {
        // Existing actions retain their string keys. These bindings opt in to
        // launching the optional Playnite frontend; the default pad layout is untouched.
        Assert.Equal("switchPlaynite", PadActionCatalog.KeyOf(PadActionId.SwitchPlaynite));
        Assert.Equal("playniteConsole", PadActionCatalog.KeyOf(PadActionId.LaunchPlayniteConsole));
        Assert.Equal("playniteDesktop", PadActionCatalog.KeyOf(PadActionId.LaunchPlayniteDesktop));
        var settings = new PadActionSettings
        {
            Bindings = new()
            {
                PadButtonBinding.Make("any", "x:Guide", PadActionId.LaunchPlayniteConsole, PadTrigger.Hold),
                PadButtonBinding.Make("any", "x:Back", PadActionId.LaunchPlayniteDesktop),
            }
        };
        var loaded = JsonSerializer.Deserialize<PadActionSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(PadActionId.LaunchPlayniteConsole, loaded.EffectiveBindings[0].ActionId);
        Assert.Equal(PadTrigger.Hold, loaded.EffectiveBindings[0].TriggerKind);
        Assert.Equal(PadActionId.LaunchPlayniteDesktop, loaded.EffectiveBindings[1].ActionId);
        Assert.DoesNotContain(new PadActionSettings().EffectiveBindings,
            x => x.ActionId is PadActionId.LaunchPlayniteConsole or PadActionId.LaunchPlayniteDesktop);
    }

    [Fact]
    public void PlayniteActionsAreLocalizedAndNeverRequireInference()
    {
        foreach (PadActionId action in new[] { PadActionId.LaunchPlayniteDesktop, PadActionId.LaunchPlayniteConsole })
        {
            PadActionInfo info = PadActionCatalog.Get(action);
            Assert.False(info.NeedsLocalAi);
            Assert.True(info.Available);
            Assert.False(string.IsNullOrWhiteSpace(info.En));
            Assert.False(string.IsNullOrWhiteSpace(info.It));
            Assert.True(PadActionPolicy.Decide(action, new PadActionContext(false, false, false)).Allowed);
            Assert.True(PadActionPolicy.Decide(action, new PadActionContext(false, true, false)).Allowed);
            Assert.False(PadActionPolicy.Decide(action, new PadActionContext(true, false, false)).Allowed);
        }
    }

    [Fact]
    public void Existing_Games_controller_binding_keeps_its_persisted_key_during_Playnite_migration()
    {
        // This binding already exists on owner pads. Its stored key must not
        // change when Desktop starts opening Tools and legacy TV keeps Library.
        Assert.Equal("gamingLibrary", PadActionCatalog.KeyOf(PadActionId.GamingLibrary));
        Assert.True(PadActionCatalog.TryParse("gamingLibrary", out PadActionId id));
        Assert.Equal(PadActionId.GamingLibrary, id);
        PadActionInfo action = PadActionCatalog.Get(id);
        Assert.Contains("Games tools", action.En);
        Assert.Contains("strumenti Giochi", action.It);
        Assert.True(PadActionPolicy.Decide(id, new PadActionContext(false, false, false)).Allowed);
    }

    [Fact]
    public void ScreenshotNamesAreSafeAndTheFolderIsBounded()
    {
        Assert.Equal("parux-20261121-100203-045.png", PadActionPolicy.ScreenshotFileName(new DateTime(2026, 11, 21, 10, 2, 3, 45)));
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "parux-padshots-" + Guid.NewGuid().ToString("N"));
        Assert.True(PadActionPolicy.ScreenshotFolderHasRoom(dir));
    }

    // ---- pass-through editor (existing PadMapping) ----
    [Fact]
    public void PassThroughEditsTheExistingMappingAndNothingSwallowsAButton()
    {
        PadMapping m = PadMappingPresets.Find(0x2DC8, 0x2101)!.Mapping;
        Assert.Equal("A", PadPassThroughEditor.TargetOf(m, 0));
        var swapped = PadPassThroughEditor.SetTarget(m, 0, "B");
        Assert.Equal("B", PadPassThroughEditor.TargetOf(swapped, 0));
        Assert.Equal("A", PadPassThroughEditor.TargetOf(m, 0));
        var swallowed = PadPassThroughEditor.SetTarget(m, 0, "none");
        Assert.Equal("none", PadPassThroughEditor.TargetOf(swallowed, 0));
        Assert.Null(swallowed.Validate());
        var trig = PadPassThroughEditor.SetTarget(m, 9, "LT");
        Assert.Equal("LT", PadPassThroughEditor.TargetOf(trig, 9));
        Assert.Null(trig.Validate());
        Assert.Equal(m.Buttons.Count, PadPassThroughEditor.SetTarget(m, 99, "A").Buttons.Count);
        Assert.Equal(m.Buttons.Count, PadPassThroughEditor.SetTarget(m, 0, "Nope").Buttons.Count);
    }

    [Fact]
    public void TwoButtonsFeedingOneGameControlIsFlagged()
    {
        PadMapping m = PadMappingPresets.Find(0x2DC8, 0x2101)!.Mapping;
        var dup = PadPassThroughEditor.SetTarget(m, 5, "A");
        Assert.Contains("A", PadPassThroughEditor.Conflicts(dup, 15));
        Assert.Empty(PadPassThroughEditor.Conflicts(m, 15));
    }

    // ---- TV bindings ----
    [Fact]
    public void TvDefaultsAreExactlyTodaysNavigation()
    {
        var tv = ConsoleNavigationBindings.Defaults();
        Assert.Equal(TvAction.Accept, tv.Resolve("x:A"));
        Assert.Equal(TvAction.Back, tv.Resolve("x:B"));
        Assert.Equal(TvAction.SwitchWorkspace, tv.Resolve("x:Back"));
        Assert.Equal(TvAction.Search, tv.Resolve("x:Start"));
        Assert.Equal(TvAction.PagePrevious, tv.Resolve("x:LB"));
        Assert.Equal(TvAction.PageNext, tv.Resolve("x:RB"));
        Assert.Equal(TvAction.ScrollUp, tv.Resolve("x:RSUp"));
        Assert.Equal(TvAction.ScrollDown, tv.Resolve("x:RSDown"));
        Assert.Equal(TvAction.Up, tv.Resolve("x:DUp"));
        Assert.Equal(TvAction.Left, tv.Resolve("x:LSLeft"));
        Assert.Equal(TvAction.None, tv.Resolve("x:LT"));
        Assert.Equal(TvAction.None, tv.Resolve("x:X"));
        Assert.Null(tv.ToStored());
        Assert.Empty(tv.UnreachableEssentials());
    }

    [Fact]
    public void TvDefaultsMapToTheNavigatorsVocabulary()
    {
        var tv = ConsoleNavigationBindings.Defaults();
        Assert.Equal((ConsoleNavigationAction.None, true), ConsoleNavigationBindings.ToNavigation(tv.Resolve("x:A")));
        Assert.Equal((ConsoleNavigationAction.Exit, false), ConsoleNavigationBindings.ToNavigation(tv.Resolve("x:B")));
        Assert.Equal((ConsoleNavigationAction.PreviousPage, false), ConsoleNavigationBindings.ToNavigation(tv.Resolve("x:LB")));
        Assert.Equal((ConsoleNavigationAction.NextPage, false), ConsoleNavigationBindings.ToNavigation(tv.Resolve("x:RB")));
        Assert.Equal((ConsoleNavigationAction.FocusSearch, false), ConsoleNavigationBindings.ToNavigation(tv.Resolve("x:Start")));
        Assert.Equal((ConsoleNavigationAction.SwitchWorkspace, false), ConsoleNavigationBindings.ToNavigation(tv.Resolve("x:Back")));
    }

    [Fact]
    public void TvOverridesStoreOnlyDifferencesAndRestoreIsNull()
    {
        var tv = ConsoleNavigationBindings.Defaults().With("x:X", TvAction.Accept).With("x:A", TvAction.None);
        var stored = tv.ToStored()!;
        Assert.Equal(2, stored.Count);
        Assert.Equal("accept", stored["x:X"]);
        Assert.Equal("none", stored["x:A"]);
        var back = ConsoleNavigationBindings.FromStored(stored);
        Assert.Equal(TvAction.Accept, back.Resolve("x:X"));
        Assert.Equal(TvAction.None, back.Resolve("x:A"));
        Assert.Null(ConsoleNavigationBindings.FromStored(null).ToStored());
    }

    [Fact]
    public void TvLockoutIsFlaggedAndCorruptOverridesAreIgnored()
    {
        var locked = ConsoleNavigationBindings.Defaults().With("x:A", TvAction.None);
        Assert.Contains(TvAction.Accept, locked.UnreachableEssentials());
        var junk = ConsoleNavigationBindings.FromStored(new Dictionary<string, string> { ["x:Nope"] = "accept", ["x:X"] = "fly", ["raw:3"] = "accept" });
        Assert.Null(junk.ToStored());
        var big = Enumerable.Range(0, 500).ToDictionary(i => "k" + i, _ => "accept");
        Assert.Null(ConsoleNavigationBindings.FromStored(big).ToStored());
    }

    [Fact]
    public void TvTableHasNoDuplicateControlRows()
    {
        Assert.Equal(ConsoleNavigationBindings.Controls.Length, ConsoleNavigationBindings.Controls.Distinct().Count());
        Assert.All(ConsoleNavigationBindings.Controls, c => Assert.True(PadControlId.IsValid(c)));
        Assert.All(TvActionCatalog.All, a => { Assert.NotEmpty(a.En); Assert.NotEmpty(a.It); });
    }

    [Fact]
    public void HoldTimeIsConfigurableClampedAndPersisted()
    {
        Assert.Equal(PadActionSettings.HoldMs, new PadActionSettings().EffectiveHoldMs);
        Assert.Equal(1000, new PadActionSettings { HoldMsOverride = 1000 }.EffectiveHoldMs);
        Assert.Equal(PadActionSettings.MinHoldMs, new PadActionSettings { HoldMsOverride = 1 }.EffectiveHoldMs);
        Assert.Equal(PadActionSettings.MaxHoldMs, new PadActionSettings { HoldMsOverride = 999999 }.EffectiveHoldMs);

        var back = JsonSerializer.Deserialize<PadActionSettings>(JsonSerializer.Serialize(new PadActionSettings { HoldMsOverride = 1200 }))!;
        Assert.Equal(1200, back.HoldMsOverride);
        Assert.Equal(1200, back.Clone().HoldMsOverride);
        Assert.Null(JsonSerializer.Deserialize<PadActionSettings>("{\"holdMs\": 5}")!.HoldMsOverride);      // out of range: ignored
        Assert.Null(JsonSerializer.Deserialize<PadActionSettings>("{\"holdMs\": \"x\"}")!.HoldMsOverride);  // wrong type: ignored
        Assert.DoesNotContain("holdMs", JsonSerializer.Serialize(new PadActionSettings()));
    }

    [Fact]
    public void EngineHonoursACustomHoldTime()
    {
        var rows = new[] { PadButtonBinding.Make(Stadia, "raw:11", PadActionId.MiniAgent, PadTrigger.Hold) };
        var e = new PadButtonEngine(rows, 1000);
        e.Feed(Pressed(), 0);
        Assert.Empty(e.Feed(Pressed(11), 100));
        Assert.Empty(e.Feed(Pressed(11), 900));    // the default 600 ms would already have fired
        Assert.Single(e.Feed(Pressed(11), 1200));

        var fast = new PadButtonEngine(rows, 300);
        fast.Feed(Pressed(), 0);
        Assert.Empty(fast.Feed(Pressed(11), 100));
        Assert.Single(fast.Feed(Pressed(11), 450));

        var clamped = new PadButtonEngine(rows, 1);   // never below the minimum
        clamped.Feed(Pressed(), 0);
        Assert.Empty(clamped.Feed(Pressed(11), 100));
        Assert.Empty(clamped.Feed(Pressed(11), 250));
        Assert.Single(clamped.Feed(Pressed(11), 400));
    }
}
