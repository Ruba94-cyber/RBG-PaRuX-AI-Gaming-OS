using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

namespace PaRuX.Services;

/// <summary>Minimal device description used by the poller (the WinRT type lives in the App-only reader).</summary>
public sealed record PadDeviceInfo(string Id, string Name, ushort VendorId, ushort ProductId);

/// <summary>
/// Pure polling core for controller-button actions (no timers, no WinRT): each call to <see cref="Poll"/> reads every needed device once.
/// Devices are isolated (a throwing or vanished pad is dropped and retried after <see cref="RetryMs"/> without touching the others), an
/// asleep pad's all-zero frames are ignored, and with no bindings and no observer nothing is opened or read. It never writes to a
/// device, never starts a virtual pad and never injects input.
/// </summary>
public sealed class PadActionPoller
{
    public const long ScanMs = 3000;
    public const long RetryMs = 30000;
    public const long SameActionDebounceMs = 350;
    public const int MaxDevices = 8;

    private sealed class Entry
    {
        public string Key = "";
        public IRawGamepadSource? Raw;
        public PadButtonEngine Engine = new(Array.Empty<PadButtonBinding>());
        public int Slot = -1;
    }

    private readonly Func<IReadOnlyList<PadDeviceInfo>> _list;
    private readonly Func<string, IRawGamepadSource?> _open;
    private readonly Func<int, Xbox360Report?> _readXInput;
    private readonly Func<long> _clock;
    private readonly Dictionary<string, Entry> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _retryAt = new(StringComparer.Ordinal);
    private readonly Dictionary<PadActionId, long> _lastByAction = new();
    private readonly Entry?[] _slots = new Entry?[4];
    private IReadOnlyList<PadButtonBinding> _bindings = Array.Empty<PadButtonBinding>();
    private long _nextScan;

    public PadActionPoller(Func<IReadOnlyList<PadDeviceInfo>> list, Func<string, IRawGamepadSource?> open,
        Func<int, Xbox360Report?> readXInput, Func<long> clock)
    {
        _list = list; _open = open; _readXInput = readXInput; _clock = clock;
    }

    /// <summary>Raised once per accepted press: the binding that fired and the device key that produced it.</summary>
    public event Action<PadButtonBinding, string>? ActionFired;

    /// <summary>While set (Settings > Controller buttons is open) every device is read, the observer receives the pressed controls of
    /// each device each tick, and NO action fires, so learning a button never starts anything.</summary>
    public Action<string, IReadOnlyCollection<string>>? Observer { get; set; }

    public int OpenDeviceCount => _devices.Count;

    /// <summary>True when the poller has any reason to run (a binding or an observer). When false the host stops its timer completely.</summary>
    public bool WantsToRun => _bindings.Count > 0 || Observer is not null;

    /// <summary>True when a device is open, so the host should tick at the fast rate; otherwise a slow scan rate is enough.</summary>
    public bool WantsFastTick => _devices.Count > 0;

    public void SetBindings(IReadOnlyList<PadButtonBinding> bindings)
    {
        _bindings = bindings ?? Array.Empty<PadButtonBinding>();
        CloseAll();
        _nextScan = 0;
    }

    public void CloseAll()
    {
        _devices.Clear();
        Array.Clear(_slots);
    }

    public void Poll()
    {
        if (!WantsToRun) { if (_devices.Count > 0) CloseAll(); return; }
        long now = _clock();
        if (now >= _nextScan)
        {
            _nextScan = now + ScanMs;
            Scan(now);
        }
        foreach (Entry e in _devices.Values.ToList())
        {
            try { PollRaw(e, now); }
            catch (Exception)
            {
                // One failing device (for example a Bluetooth pad that throws on read) never stops the others.
                Drop(e, now);
            }
        }
        for (int slot = 0; slot < _slots.Length; slot++)
            if (_slots[slot] is { } xe)
            {
                try { PollXInput(xe, slot, now); }
                catch (Exception) { _slots[slot] = null; }
            }
    }

    private bool NeedsDevice(string key) =>
        Observer is not null || _bindings.Any(b => b.Device == key || b.Device == PadControlId.Any);

    private bool WantsXInput => Observer is not null || _bindings.Any(b => b.Device == PadControlId.Any && b.Control.StartsWith("x:", StringComparison.Ordinal));

    private void Scan(long now)
    {
        IReadOnlyList<PadDeviceInfo> list;
        try { list = _list(); } catch (Exception) { list = Array.Empty<PadDeviceInfo>(); }

        var present = new HashSet<string>(list.Select(d => d.Id), StringComparer.Ordinal);
        foreach (string gone in _devices.Keys.Where(id => !present.Contains(id)).ToList()) _devices.Remove(gone);

        foreach (PadDeviceInfo d in list)
        {
            if (_devices.Count >= MaxDevices) break;
            if (_devices.ContainsKey(d.Id)) continue;
            // Windows serves Xbox-family pads through XInput; reading their raw device as well would double every press.
            if (PadMappingPresets.IsXInputFamily(d.VendorId, d.ProductId)) continue;
            string key = PadControlId.DeviceKey(d.VendorId, d.ProductId);
            if (!NeedsDevice(key)) continue;
            if (_retryAt.TryGetValue(d.Id, out long at) && now < at) continue;
            IRawGamepadSource? src;
            try { src = _open(d.Id); } catch (Exception) { src = null; }
            if (src is null) { _retryAt[d.Id] = now + RetryMs; continue; }
            var rows = PadBindingResolver.ForDevice(_bindings, key);
            _devices[d.Id] = new Entry { Key = key, Raw = src, Engine = new PadButtonEngine(rows) };
        }

        if (WantsXInput)
            for (int slot = 0; slot < _slots.Length; slot++)
            {
                if (_slots[slot] is not null) continue;
                Xbox360Report? r;
                try { r = _readXInput(slot); } catch (Exception) { r = null; }
                if (r is null) continue;
                _slots[slot] = new Entry { Key = PadControlId.Any, Slot = slot, Engine = new PadButtonEngine(PadBindingResolver.ForDevice(_bindings, PadControlId.Any)) };
            }
    }

    private void Drop(Entry e, long now)
    {
        foreach (var kv in _devices.Where(kv => ReferenceEquals(kv.Value, e)).ToList())
        {
            _devices.Remove(kv.Key);
            _retryAt[kv.Key] = now + RetryMs;
        }
    }

    private void PollRaw(Entry e, long now)
    {
        if (e.Raw is null || !e.Raw.TryRead(out RawGamepadState? state) || state is null)
            throw new InvalidOperationException("device read failed");
        if (PadFrameControls.IsAsleepFrame(state))
        {
            e.Engine.Reset();   // the first real frame afterwards is a baseline: waking the pad never fires an action
            return;
        }
        Handle(e, PadFrameControls.FromRaw(state), now);
    }

    private void PollXInput(Entry e, int slot, long now)
    {
        Xbox360Report? r = _readXInput(slot);
        if (r is null) { _slots[slot] = null; return; }
        Handle(e, PadFrameControls.FromXbox(r.Value), now);
    }

    private void Handle(Entry e, HashSet<string> pressed, long now)
    {
        if (Observer is { } observer)
        {
            e.Engine.Feed(pressed, now);   // keeps the baseline/edge logic; actions are discarded while observing
            observer(e.Key, pressed);
            return;
        }
        foreach (PadButtonBinding b in e.Engine.Feed(pressed, now))
        {
            PadActionId id = b.ActionId;
            if (_lastByAction.TryGetValue(id, out long last) && now - last < SameActionDebounceMs) continue;   // e.g. the same pad seen both raw and through XInput
            _lastByAction[id] = now;
            ActionFired?.Invoke(b, e.Key);
        }
    }
}
