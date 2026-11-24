using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

namespace PaRuX.Services;

/// <summary>
/// Hosts <see cref="PadActionPoller"/> on the UI dispatcher: reads the special buttons of connected pads without a driver, also while a
/// game runs (that is the point of the Guide button), at about 30 Hz while a needed pad is open and only about once a second while none
/// is. With no binding (or the setting off) and no Settings observer the timer is stopped: zero polling cost. Works from the raw device
/// even when PaRuX itself forwards the pad to a virtual Xbox pad. Never writes to a device and never injects input. UI thread only.
/// </summary>
public sealed class PadActionListener : IDisposable
{
    public static PadActionListener Current { get; } = new();

    private const int FastMs = 33;
    private const int SlowMs = 1000;

    private readonly PadActionPoller _poller;
    private DispatcherQueue? _queue;
    private DispatcherQueueTimer? _timer;
    private bool _fast;
    private DateTime _lastErrorUtc;

    private PadActionListener()
    {
        _poller = new PadActionPoller(ListRaw, id => WindowsRawGamepadSource.TryOpen(id), ReadXInput, () => Environment.TickCount64);
        _poller.ActionFired += (binding, device) => ActionFired?.Invoke(binding, device);
    }

    /// <summary>Raised on the UI thread for each accepted press.</summary>
    public event Action<PadButtonBinding, string>? ActionFired;

    public bool IsRunning => _timer is not null;

    /// <summary>Settings > Controller buttons learn mode / live view. Setting it (or clearing it) restarts the polling as needed.</summary>
    public void SetObserver(Action<string, IReadOnlyCollection<string>>? observer)
    {
        _poller.Observer = observer;
        Reconfigure();
    }

    public void Start(DispatcherQueue queue)
    {
        _queue = queue;
        SettingsStore.Current.Changed -= OnSettingsChanged;
        SettingsStore.Current.Changed += OnSettingsChanged;
        Reconfigure();
    }

    public void Stop()
    {
        SettingsStore.Current.Changed -= OnSettingsChanged;
        StopTimer();
        _poller.CloseAll();
    }

    public void Dispose() => Stop();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_queue is { } q && !q.HasThreadAccess) q.TryEnqueue(Reconfigure); else Reconfigure();
        }
        catch (Exception) { }
    }

    /// <summary>Re-reads the saved bindings and starts or stops the timer accordingly.</summary>
    public void Reconfigure()
    {
        if (_queue is null) return;
        try
        {
            _poller.SetBindings(SettingsStore.Current.Data.PadActions.EffectiveBindings, SettingsStore.Current.Data.PadActions.EffectiveHoldMs);
            if (_poller.WantsToRun) StartTimer(); else StopTimer();
        }
        catch (Exception ex) { Log.Write("PadActions: reconfigure failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private void StartTimer()
    {
        if (_timer is null && _queue is not null)
        {
            _timer = _queue.CreateTimer();
            _timer.IsRepeating = true;
            _timer.Tick += OnTick;
            _fast = false;
            _timer.Interval = TimeSpan.FromMilliseconds(SlowMs);
            _timer.Start();
        }
    }

    private void StopTimer()
    {
        if (_timer is { } t)
        {
            t.Stop();
            t.Tick -= OnTick;
            _timer = null;
        }
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            _poller.Poll();
            if (!_poller.WantsToRun) { StopTimer(); return; }
            bool fast = _poller.WantsFastTick;
            if (fast != _fast && _timer is not null)
            {
                _fast = fast;
                _timer.Interval = TimeSpan.FromMilliseconds(fast ? FastMs : SlowMs);
            }
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow - _lastErrorUtc > TimeSpan.FromSeconds(30))
            {
                _lastErrorUtc = DateTime.UtcNow;
                Log.Write("PadActions: poll failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private static IReadOnlyList<PadDeviceInfo> ListRaw() =>
        WindowsRawGamepadSource.ListDevices().Select(d => new PadDeviceInfo(d.Id, d.Name, d.VendorId, d.ProductId)).ToList();

    // ---- XInput (named Xbox controls; the Guide button needs the undocumented XInputGetStateEx, ordinal 100) ----------------------
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct XInputGamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LeftX, LeftY, RightX, RightY; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }

    [System.Runtime.InteropServices.DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint XInputGetStateEx(uint userIndex, out XInputState state);

    [System.Runtime.InteropServices.DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    private static bool _noStateEx;

    private static Xbox360Report? ReadXInput(int slot)
    {
        try
        {
            XInputState st;
            uint rc;
            if (_noStateEx) rc = XInputGetState((uint)slot, out st);
            else
            {
                try { rc = XInputGetStateEx((uint)slot, out st); }
                catch (EntryPointNotFoundException) { _noStateEx = true; rc = XInputGetState((uint)slot, out st); }
            }
            if (rc != 0) return null;
            XInputGamepad g = st.Gamepad;
            return new Xbox360Report(g.Buttons, g.LeftTrigger, g.RightTrigger, g.LeftX, g.LeftY, g.RightX, g.RightY);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}
