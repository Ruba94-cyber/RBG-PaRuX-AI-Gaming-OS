using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Forwarding with physical-pad hiding OFF (HidHide optional). Fakes only: no driver is touched.</summary>
public sealed class VirtualPadUnhiddenForwardingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-unhid-" + Guid.NewGuid().ToString("N"));
    private const string Pad = "{wgi/nrid/unhidden-pad}";

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Source : IRawGamepadSource
    {
        public string DeviceId => Pad;
        public volatile bool Connected = true;
        public bool TryRead(out RawGamepadState? state)
        {
            state = Connected ? new RawGamepadState(new[] { true }, new[] { 0.0 }, -1) : null;
            return Connected;
        }
    }

    private sealed class FakePad : IVirtualXboxPad
    {
        public readonly List<Xbox360Report> Reports = new();
        public bool Submit(Xbox360Report report) { lock (Reports) Reports.Add(report); return true; }
        public void Dispose() { }
        public Xbox360Report? Last { get { lock (Reports) return Reports.Count == 0 ? null : Reports[^1]; } }
    }

    private sealed class Backend : IVirtualControllerBackend
    {
        public string Id => "fake";
        public readonly FakePad Pad = new();
        public IVirtualXboxPad? TryCreatePad(out string? error) { error = null; return Pad; }
    }

    private sealed class Isolation : IPadIsolation
    {
        public int IsolateCalls, StillCalls;
        public Task<PadIsolationOutcome> IsolateAsync(PadDeviceIdentity device, CancellationToken ct)
        { IsolateCalls++; return Task.FromResult(new PadIsolationOutcome(true, "hidden", @"HID\VID_18D1&PID_9400\1")); }
        public Task<bool> IsStillIsolatedAsync(string path, CancellationToken ct) { StillCalls++; return Task.FromResult(true); }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly Backend Backend = new();
        public readonly Isolation Isolation = new();
        public readonly Source Source = new();
        public bool DriverPresent = true;
        public VirtualPadForwardingService Service = null!;
        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    private Rig MakeRig()
    {
        var rig = new Rig();
        var deviceStore = new RawPadMappingStore(Path.Combine(_dir, "device"));
        Assert.True(deviceStore.Save(Pad, new PadMapping { Buttons = new() { [0] = Xbox360Report.A }, LeftXAxis = 0 }, out _));
        rig.Service = new VirtualPadForwardingService(rig.Backend, id => id == Pad ? rig.Source : null,
            () => rig.DriverPresent ? (true, null) : (false, "ViGEmBus is not installed."), rig.Isolation,
            new GamePadProfileStore(Path.Combine(_dir, "game"), deviceStore),
            new VirtualPadServiceOptions { Interval = TimeSpan.FromMilliseconds(10), RetryDelay = TimeSpan.FromMilliseconds(5) });
        return rig;
    }

    private static PadDeviceIdentity Identity() => new(Pad, "Stadia", 0x18D1, 0x9400, 2);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Never reached: " + what);
    }

    [Fact]
    public async Task Without_consent_and_without_the_option_nothing_starts_exactly_as_before()
    {
        await using var rig = MakeRig();
        var status = await rig.Service.StartAsync(Identity(), false, null);
        Assert.Equal(VirtualPadPhase.Failed, status.Phase);
        Assert.Equal(0, rig.Isolation.IsolateCalls);
    }

    [Fact]
    public async Task Hiding_off_starts_forwarding_without_touching_hidhide_and_says_so()
    {
        await using var rig = MakeRig();
        var status = await rig.Service.StartAsync(Identity(), false, null, allowUnhidden: true);
        Assert.Equal(VirtualPadPhase.Running, status.Phase);
        Assert.False(status.PhysicalPadHidden);
        Assert.Contains("NOT hidden", status.Message);
        Assert.Contains("recommended", status.Message);
        Assert.Equal(0, rig.Isolation.IsolateCalls);
        Assert.Equal(0, rig.Isolation.StillCalls);
        await WaitUntil(() => rig.Backend.Pad.Last is { Buttons: Xbox360Report.A }, "virtual pad reports the A press");
    }

    [Fact]
    public async Task Consent_wins_over_the_hiding_off_option_and_hides_as_before()
    {
        await using var rig = MakeRig();
        var status = await rig.Service.StartAsync(Identity(), true, null, allowUnhidden: true);
        Assert.Equal(VirtualPadPhase.Running, status.Phase);
        Assert.True(status.PhysicalPadHidden);
        Assert.Equal(1, rig.Isolation.IsolateCalls);
        Assert.DoesNotContain("NOT hidden", status.Message);
    }

    [Fact]
    public async Task Missing_virtual_driver_is_reported_precisely_and_nothing_is_hidden_even_with_hiding_off()
    {
        await using var rig = MakeRig();
        rig.DriverPresent = false;
        var status = await rig.Service.StartAsync(Identity(), false, null, allowUnhidden: true);
        Assert.Equal(VirtualPadPhase.Failed, status.Phase);
        Assert.Contains("ViGEmBus", status.Message);
        Assert.Equal(0, rig.Isolation.IsolateCalls);
    }

    [Fact]
    public async Task Stop_reports_that_nothing_was_hidden_and_the_pad_can_start_again()
    {
        await using var rig = MakeRig();
        await rig.Service.StartAsync(Identity(), false, null, allowUnhidden: true);
        var stopped = await rig.Service.StopAsync();
        Assert.Equal(VirtualPadPhase.Idle, stopped.Phase);
        Assert.Contains("Nothing was hidden", stopped.Message);
        Assert.Equal(VirtualPadPhase.Running, (await rig.Service.StartAsync(Identity(), false, null, allowUnhidden: true)).Phase);
    }

    [Fact]
    public async Task Unplug_and_replug_offers_a_rearm_that_works_without_any_isolation_check()
    {
        await using var rig = MakeRig();
        await rig.Service.StartAsync(Identity(), false, null, allowUnhidden: true);
        await rig.Service.OnDeviceRemovedAsync(Pad);
        Assert.Equal(VirtualPadPhase.PausedRemoved, rig.Service.Status.Phase);
        rig.Service.OnDeviceAdded(Pad);
        Assert.True(rig.Service.Status.CanRearm);
        var again = await rig.Service.RearmAsync();
        Assert.Equal(VirtualPadPhase.Running, again.Phase);
        Assert.False(again.PhysicalPadHidden);
        Assert.Equal(0, rig.Isolation.IsolateCalls);
        Assert.Equal(0, rig.Isolation.StillCalls);
    }

    [Fact]
    public async Task Switching_from_hiding_off_to_consented_hiding_hides_only_then()
    {
        await using var rig = MakeRig();
        await rig.Service.StartAsync(Identity(), false, null, allowUnhidden: true);
        await rig.Service.StopAsync();
        Assert.Equal(0, rig.Isolation.IsolateCalls);
        var hidden = await rig.Service.StartAsync(Identity(), true, null);
        Assert.Equal(VirtualPadPhase.Running, hidden.Phase);
        Assert.True(hidden.PhysicalPadHidden);
        Assert.Equal(1, rig.Isolation.IsolateCalls);
    }
}
