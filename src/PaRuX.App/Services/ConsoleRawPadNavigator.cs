using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

namespace PaRuX.Services;

/// <summary>
/// Driverless navigation of Console/TV mode with a raw HID pad (for example a Stadia controller) that WinUI does not turn into
/// gamepad key events. Polls ONE connected non-XInput pad at about 60 Hz through <see cref="WindowsRawGamepadSource"/>, maps it
/// with the existing preset/mapper and raises <see cref="ConsoleNavigationAction"/> events. It never synthesises mouse or keyboard
/// input, never starts a virtual pad, never writes device configuration and never competes with a game: while Gaming mode or a
/// game is active it releases the pad and does not poll. Must be used on the UI thread.
/// </summary>
public sealed class ConsoleRawPadNavigator : IDisposable
{
    private const int ScanEveryTicks = 60;

    private readonly Func<bool> _gameActive;
    private readonly Func<IReadOnlyList<RawGamepadDeviceInfo>> _list;
    private readonly Func<string, IRawGamepadSource?> _open;
    private readonly Func<RawGamepadDeviceInfo, (PadMapping? Mapping, string Source)> _resolveMapping;
    private readonly Func<long> _clock;
    private readonly RawPadEdgeTranslator _translator = new();
    private DispatcherQueueTimer? _timer;
    private Func<bool>? _busy;
    private IRawGamepadSource? _source;
    private PadMapping? _mapping;
    private int _ticksToScan;
    private bool _suspendedLogged;
    // Every XInput slot is read independently (its own edge state), so any number of Xbox-class pads can navigate at once and a pad
    // plugged in after the window opened is picked up without waiting for the raw pad to disappear.
    private readonly bool[] _xinputConnected = new bool[4];
    private readonly RawPadEdgeTranslator[] _xinputTranslators = { new(), new(), new(), new() };
    private int _xinputScanTicks;
    private bool _winUiKeysSeen;

    /// <summary>
    /// Called by the window when WinUI itself delivers a gamepad navigation key (an Xbox-class pad it handles natively). From then on
    /// this navigator stays quiet for the session, so a press never moves the focus twice.
    /// </summary>
    public void NoteWinUiGamepadKeysSeen()
    {
        if (_winUiKeysSeen) return;
        _winUiKeysSeen = true;
        Release();
        Log.Write("ConsoleRawPad: WinUI delivers gamepad keys itself; direct pad reading stays off for this session.");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct XInputGamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LeftX, LeftY, RightX, RightY; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }

    [System.Runtime.InteropServices.DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    /// <summary>True while a dialog, flyout, drop-down or the on-screen keyboard is open: B and the directions then stay available.</summary>
    public Func<bool>? OverlayOpen { get; set; }

    private bool OverlayOpenNow()
    {
        try { return OverlayOpen?.Invoke() ?? false; } catch (Exception) { return false; }
    }

    public event Action<ConsoleNavigationAction>? ActionRaised;
    public event Action? AcceptRequested;
    /// <summary>A gamepad virtual key meant for the open on-screen keyboard (X, Y, Start, shoulders, triggers).</summary>
    public event Action<int>? PadKeyRaised;

    public ConsoleRawPadNavigator() : this(null, null, null, null, null) { }

    /// <summary>Everything is injectable: the mapping resolver in particular, because the Stadia preset is unverified on hardware.</summary>
    public ConsoleRawPadNavigator(
        Func<bool>? gameActive,
        Func<IReadOnlyList<RawGamepadDeviceInfo>>? list,
        Func<string, IRawGamepadSource?>? open,
        Func<RawGamepadDeviceInfo, (PadMapping? Mapping, string Source)>? resolveMapping,
        Func<long>? clock)
    {
        _gameActive = gameActive ?? DefaultGameActive;
        _list = list ?? WindowsRawGamepadSource.ListDevices;
        _open = open ?? (id => WindowsRawGamepadSource.TryOpen(id));
        _resolveMapping = resolveMapping ?? DefaultResolve;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public bool IsRunning => _timer is not null;
    public bool HasPad => _source is not null;

    /// <summary>
    /// Begins polling on <paramref name="queue"/>. <paramref name="isBusyOrGameActive"/> returns true while a command or dialog is
    /// active (only Accept and directional moves then pass, per <see cref="ConsoleNavigationPolicy"/>). A running game is detected
    /// independently and suspends polling entirely.
    /// </summary>
    public void Start(DispatcherQueue queue, Func<bool> isBusyOrGameActive)
    {
        if (_timer is not null) return;
        _busy = isBusyOrGameActive;
        _ticksToScan = 0;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void ResetXInput()
    {
        for (int i = 0; i < 4; i++) { _xinputConnected[i] = false; _xinputTranslators[i].Reset(); }
        _xinputScanTicks = 0;
    }

    public void Stop()
    {
        ResetXInput();
        if (_timer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnTick;
            _timer = null;
        }
        Release();
        _busy = null;
    }

    public void Dispose() => Stop();

    private DateTime _lastTickErrorUtc, _lastHandlerErrorUtc;

    /// <summary>Raises one event; a failing handler (UI code) is logged (at most every 10 s) and never stops the polling loop.</summary>
    private void Dispatch(RawPadNavigationEvent e)
    {
        try
        {
            if (e.PadKey != 0) PadKeyRaised?.Invoke(e.PadKey);
            else if (e.IsAccept) AcceptRequested?.Invoke();
            else ActionRaised?.Invoke(e.Action);
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow - _lastHandlerErrorUtc > TimeSpan.FromSeconds(10))
            {
                _lastHandlerErrorUtc = DateTime.UtcNow;
                Log.Write("ConsoleRawPad: handling " + (e.IsAccept ? "Accept" : e.Action.ToString()) + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        try { Poll(); }
        catch (Exception ex)
        {
            // One misbehaving device must never stop navigation (or the timer): drop the raw pad, keep reading the others.
            if (DateTime.UtcNow - _lastTickErrorUtc > TimeSpan.FromSeconds(30))
            {
                _lastTickErrorUtc = DateTime.UtcNow;
                Log.Write("ConsoleRawPad: poll failed and the raw pad was released: " + ex.GetType().Name + ": " + ex.Message);
            }
            Release();
            _ticksToScan = ScanEveryTicks * 30;   // a pad that throws on read (seen with a Bluetooth 8BitDo) is retried every ~30 s, not every second
        }
    }

    /// <summary>One poll step (internal so a test can drive it without a dispatcher).</summary>
    internal void Poll()
    {
        if (SafeGameActive())
        {
            if (!_suspendedLogged) { Log.Write("ConsoleRawPad: suspended, a game or Gaming mode is active."); _suspendedLogged = true; }
            Release();
            ResetXInput();
            return;
        }
        _suspendedLogged = false;
        if (_winUiKeysSeen) return;

        // Any pad Windows exposes as an XInput gamepad (a real Xbox pad, DS4Windows/Steam Input output, or a pad forwarded to a virtual
        // Xbox pad) is read directly: a WinUI 3 desktop window does not turn it into navigation keys on its own. Connected slots are
        // read every tick; disconnected slots are probed about once a second (probing an empty slot every frame is slow).
        PollXInputSlots();

        // A pad Windows already exposes as XInput (including a Stadia/other pad forwarded to a virtual Xbox pad) is THE navigation pad:
        // the physical raw device it comes from stays unread, otherwise one press would move the focus twice.
        if (_xinputConnected.Any(c => c))
        {
            if (_source is not null) Release();
            return;
        }

        if (_source is null)
        {
            if (_ticksToScan-- > 0) return;
            _ticksToScan = ScanEveryTicks;
            TryConnect();
            return;
        }

        if (!_source.TryRead(out RawGamepadState? state) || state is null || _mapping is null)
        {
            Log.Write("ConsoleRawPad: pad disappeared, polling stopped until one is connected again.");
            Release();
            return;
        }

        // A Bluetooth pad that is asleep or not ready reports every axis at exactly zero, which a 0..1 axis mapping reads as the stick pushed
        // fully up-left: ignore such a frame instead of navigating with it (seen with a real 8BitDo SN30 Pro).
        if (state.HatDirection < 0 && state.Axes.Count > 0 && state.Axes.All(a => a <= 0.001))
        {
            _translator.Reset();
            return;
        }

        bool busy;
        try { busy = _busy?.Invoke() ?? false; } catch (Exception) { busy = true; }
        Xbox360Report report = GenericPadMapper.Map(state, _mapping);
        foreach (RawPadNavigationEvent e in _translator.Feed(report, _clock(), busy, OverlayOpenNow()))
            Dispatch(e);
    }

    private void TryConnect()
    {
        RawGamepadDeviceInfo? pick = null;
        try
        {
            // Xbox-family pads are read through XInput above; only non-XInput raw pads reach this path.
            pick = _list().FirstOrDefault(d => !PadMappingPresets.IsXInputFamily(d.VendorId, d.ProductId));
        }
        catch (Exception) { }
        if (pick is null) return;

        var (mapping, source) = _resolveMapping(pick);
        if (mapping is null || mapping.Validate() is not null)
        {
            Log.Write($"ConsoleRawPad: found {Describe(pick)} but no usable mapping ({source}); not polling it.");
            _ticksToScan = ScanEveryTicks * 10; // do not spam the log every second
            return;
        }
        IRawGamepadSource? opened = _open(pick.Id);
        if (opened is null) return;

        _mapping = mapping;
        _source = opened;
        _translator.Reset();
        Log.Write($"ConsoleRawPad: navigating with {Describe(pick)}; mapping source: {source}.");
    }

    private void PollXInputSlots()
    {
        bool probe = _xinputScanTicks-- <= 0;
        if (probe) _xinputScanTicks = ScanEveryTicks;
        bool busy;
        try { busy = _busy?.Invoke() ?? false; } catch (Exception) { busy = true; }
        for (int slot = 0; slot < 4; slot++)
        {
            if (!_xinputConnected[slot] && !probe) continue;
            if (!TryReadXInput(slot, out Xbox360Report report))
            {
                if (_xinputConnected[slot])
                {
                    _xinputConnected[slot] = false;
                    _xinputTranslators[slot].Reset();
                    Log.Write("ConsoleRawPad: XInput pad in slot " + slot + " disappeared.");
                }
                continue;
            }
            if (!_xinputConnected[slot])
            {
                _xinputConnected[slot] = true;
                _xinputTranslators[slot].Reset();   // the first frame after connecting is only a baseline
                Log.Write("ConsoleRawPad: navigating with the XInput (Xbox-class) pad in slot " + slot + ".");
            }
            foreach (RawPadNavigationEvent e in _xinputTranslators[slot].Feed(report, _clock(), busy, OverlayOpenNow()))
                Dispatch(e);
        }
    }

    private static bool TryReadXInput(int slot, out Xbox360Report report)
    {
        report = default;
        try
        {
            if (XInputGetState((uint)slot, out XInputState st) != 0) return false;
            XInputGamepad g = st.Gamepad;
            report = new Xbox360Report(g.Buttons, g.LeftTrigger, g.RightTrigger, g.LeftX, g.LeftY, g.RightX, g.RightY);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private void Release()
    {
        _source = null;
        _mapping = null;
        _translator.Reset();
    }

    private bool SafeGameActive()
    {
        try { return _gameActive(); } catch (Exception) { return true; }
    }

    private static bool DefaultGameActive() =>
        GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive();

    private static string Describe(RawGamepadDeviceInfo d) =>
        $"\"{d.Name}\" VID {d.VendorId:X4} PID {d.ProductId:X4} ({d.ButtonCount} buttons, {d.AxisCount} axes, {d.SwitchCount} hat)";

    private static (PadMapping? Mapping, string Source) DefaultResolve(RawGamepadDeviceInfo device)
    {
        // The owner's saved mapping for this exact device (read-only) wins; otherwise the built-in preset by VID/PID.
        try
        {
            PadMapping? saved = new RawPadMappingStore().Load(device.Id, out _);
            if (saved is not null) return (saved, "saved device mapping");
        }
        catch (Exception) { }
        PadMappingPreset? preset = PadMappingPresets.Find(device.VendorId, device.ProductId);
        if (preset is not null)
            return (preset.Mapping.Clone(), $"built-in preset '{preset.Id}' ({(preset.Verified ? "verified" : "UNVERIFIED on hardware")})");
        // Last resort: a conservative generic HID layout (UNVERIFIED), only for devices that look like a gamepad.
        if (GenericRawPadMapping.TryBuild(device.ButtonCount, device.AxisCount, device.SwitchCount, out PadMapping? generic, out string why) && generic is not null)
            return (generic, "generic fallback (UNVERIFIED for this model)");
        return (null, $"no saved mapping, no preset, and the generic fallback refused it: {why}");
    }
}

