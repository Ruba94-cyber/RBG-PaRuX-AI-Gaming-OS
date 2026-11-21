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

    public event Action<ConsoleNavigationAction>? ActionRaised;
    public event Action? AcceptRequested;

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

    public void Stop()
    {
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

    private void OnTick(DispatcherQueueTimer sender, object args) => Poll();

    /// <summary>One poll step (internal so a test can drive it without a dispatcher).</summary>
    internal void Poll()
    {
        if (SafeGameActive())
        {
            if (!_suspendedLogged) { Log.Write("ConsoleRawPad: suspended, a game or Gaming mode is active."); _suspendedLogged = true; }
            Release();
            return;
        }
        _suspendedLogged = false;

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

        bool busy;
        try { busy = _busy?.Invoke() ?? false; } catch (Exception) { busy = true; }
        Xbox360Report report = GenericPadMapper.Map(state, _mapping);
        foreach (RawPadNavigationEvent e in _translator.Feed(report, _clock(), busy))
        {
            if (e.IsAccept) AcceptRequested?.Invoke();
            else ActionRaised?.Invoke(e.Action);
        }
    }

    private void TryConnect()
    {
        RawGamepadDeviceInfo? pick = null;
        try
        {
            // If Windows already exposes an Xbox-class gamepad (a real one, or this pad forwarded as a virtual Xbox pad), WinUI
            // navigates with its own gamepad keys: polling the raw pad too would move the focus twice per press.
            if (Windows.Gaming.Input.Gamepad.Gamepads.Count > 0)
            {
                _ticksToScan = ScanEveryTicks;
                return;
            }
            // Xbox-family pads already navigate through WinUI's own gamepad keys; only raw pads need this path.
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
