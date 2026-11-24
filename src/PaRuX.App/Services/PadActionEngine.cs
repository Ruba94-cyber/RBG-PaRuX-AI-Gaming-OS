using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

namespace PaRuX.Services;

/// <summary>Turns raw device frames into the set of pressed control ids (pure, no hardware).</summary>
public static class PadFrameControls
{
    public const int StickThreshold = 16000;
    public const byte TriggerThreshold = 128;

    /// <summary>A pad that is asleep or not ready (seen with Bluetooth Stadia and 8BitDo pads) reports every axis at exactly zero, which
    /// Windows normalises to -1. A real, awake pad never has all axes at the minimum (sticks rest near 0). Such a frame is ignored.</summary>
    public static bool IsAsleepFrame(RawGamepadState state) =>
        state.Axes.Count > 0 && state.HatDirection < 0 && state.Axes.All(a => !double.IsFinite(a) || a <= -0.999);

    public static HashSet<string> FromRaw(RawGamepadState state)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        int n = Math.Min(state.Buttons.Count, PadControlId.MaxRawIndex + 1);
        for (int i = 0; i < n; i++) if (state.Buttons[i]) set.Add(PadControlId.Raw(i));
        switch (state.HatDirection)
        {
            case 0: set.Add(PadControlId.Hat("Up")); break;
            case 1: set.Add(PadControlId.Hat("Up")); set.Add(PadControlId.Hat("Right")); break;
            case 2: set.Add(PadControlId.Hat("Right")); break;
            case 3: set.Add(PadControlId.Hat("Down")); set.Add(PadControlId.Hat("Right")); break;
            case 4: set.Add(PadControlId.Hat("Down")); break;
            case 5: set.Add(PadControlId.Hat("Down")); set.Add(PadControlId.Hat("Left")); break;
            case 6: set.Add(PadControlId.Hat("Left")); break;
            case 7: set.Add(PadControlId.Hat("Up")); set.Add(PadControlId.Hat("Left")); break;
        }
        return set;
    }

    public static HashSet<string> FromXbox(Xbox360Report r)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void B(ushort bit, string name) { if ((r.Buttons & bit) != 0) set.Add(PadControlId.Xbox(name)); }
        B(Xbox360Report.A, "A"); B(Xbox360Report.B, "B"); B(Xbox360Report.X, "X"); B(Xbox360Report.Y, "Y");
        B(Xbox360Report.LeftShoulder, "LB"); B(Xbox360Report.RightShoulder, "RB");
        B(Xbox360Report.LeftThumb, "L3"); B(Xbox360Report.RightThumb, "R3");
        B(Xbox360Report.Back, "Back"); B(Xbox360Report.Start, "Start"); B(Xbox360Report.Guide, "Guide");
        B(Xbox360Report.DpadUp, "DUp"); B(Xbox360Report.DpadDown, "DDown"); B(Xbox360Report.DpadLeft, "DLeft"); B(Xbox360Report.DpadRight, "DRight");
        if (r.LeftTrigger >= TriggerThreshold) set.Add(PadControlId.Xbox("LT"));
        if (r.RightTrigger >= TriggerThreshold) set.Add(PadControlId.Xbox("RT"));
        if (r.LeftY >= StickThreshold) set.Add(PadControlId.Xbox("LSUp"));
        if (r.LeftY <= -StickThreshold) set.Add(PadControlId.Xbox("LSDown"));
        if (r.LeftX <= -StickThreshold) set.Add(PadControlId.Xbox("LSLeft"));
        if (r.LeftX >= StickThreshold) set.Add(PadControlId.Xbox("LSRight"));
        if (r.RightY >= StickThreshold) set.Add(PadControlId.Xbox("RSUp"));
        if (r.RightY <= -StickThreshold) set.Add(PadControlId.Xbox("RSDown"));
        if (r.RightX <= -StickThreshold) set.Add(PadControlId.Xbox("RSLeft"));
        if (r.RightX >= StickThreshold) set.Add(PadControlId.Xbox("RSRight"));
        return set;
    }
}

/// <summary>
/// Press / hold / debounce state machine for ONE device. Rules: the first frame after construction or <see cref="Reset"/> is only a
/// baseline (a control held at connect never acts until it is released); a control with only a tap binding fires on press; with a hold
/// binding too, the tap fires on release if shorter than <see cref="HoldMs"/> and the hold fires once at <see cref="HoldMs"/>; a
/// control with only a hold binding fires once at <see cref="HoldMs"/>; one press never fires twice; any two firings of this device
/// closer than <see cref="DebounceMs"/> are collapsed (the second is dropped).
/// </summary>
public sealed class PadButtonEngine
{
    public const long HoldMs = PadActionSettings.HoldMs;
    public const long DebounceMs = 250;

    private readonly long _holdMs;
    private readonly Dictionary<(string Control, bool Hold), PadButtonBinding> _bindings = new();
    private readonly Dictionary<string, long> _since = new(StringComparer.Ordinal);
    private readonly HashSet<string> _holdFired = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ignoreUntilReleased = new(StringComparer.Ordinal);
    private bool _primed;
    private long _lastFire = long.MinValue / 2;

    /// <summary>Controls that went down in the last <see cref="Feed"/> call (empty on the baseline frame). Used by learn mode.</summary>
    public IReadOnlyList<string> LastPressEdges { get; private set; } = Array.Empty<string>();

    /// <param name="bindings">Already resolved for this device (device-specific rows win over "any pad" rows for the same control and trigger).</param>
    /// <param name="holdMs">The hold time; clamped to the owner-settable range. Defaults to <see cref="HoldMs"/>.</param>
    public PadButtonEngine(IEnumerable<PadButtonBinding> bindings, long holdMs = HoldMs)
    {
        _holdMs = Math.Clamp(holdMs, PadActionSettings.MinHoldMs, PadActionSettings.MaxHoldMs);
        foreach (PadButtonBinding b in bindings)
            if (b.IsValid() && b.ActionId != PadActionId.None)
                _bindings.TryAdd((b.Control, b.TriggerKind == PadTrigger.Hold), b);
    }

    public bool HasBindings => _bindings.Count > 0;

    public void Reset()
    {
        _primed = false;
        _since.Clear();
        _holdFired.Clear();
        _ignoreUntilReleased.Clear();
        LastPressEdges = Array.Empty<string>();
    }

    public IReadOnlyList<PadButtonBinding> Feed(IReadOnlyCollection<string> pressed, long nowMs)
    {
        var fired = new List<PadButtonBinding>();
        if (!_primed)
        {
            _primed = true;
            foreach (string c in pressed) _ignoreUntilReleased.Add(c);
            LastPressEdges = Array.Empty<string>();
            return fired;
        }

        _ignoreUntilReleased.RemoveWhere(c => !pressed.Contains(c));
        var edges = new List<string>();

        foreach (string c in pressed)
        {
            if (_ignoreUntilReleased.Contains(c) || _since.ContainsKey(c)) continue;
            _since[c] = nowMs;
            edges.Add(c);
            bool hasTap = _bindings.TryGetValue((c, false), out PadButtonBinding? tap);
            bool hasHold = _bindings.ContainsKey((c, true));
            if (hasTap && !hasHold) Fire(fired, tap!, nowMs);
        }
        LastPressEdges = edges;

        foreach (string c in _since.Keys.ToList())
        {
            bool down = pressed.Contains(c);
            long since = _since[c];
            if (down)
            {
                if (!_holdFired.Contains(c) && nowMs - since >= _holdMs && _bindings.TryGetValue((c, true), out PadButtonBinding? hold))
                {
                    _holdFired.Add(c);
                    Fire(fired, hold, nowMs);
                }
            }
            else
            {
                if (!_holdFired.Contains(c) && _bindings.ContainsKey((c, true)) && _bindings.TryGetValue((c, false), out PadButtonBinding? tap))
                    Fire(fired, tap, nowMs);   // released before the hold time: it was a tap
                _since.Remove(c);
                _holdFired.Remove(c);
            }
        }
        return fired;
    }

    private void Fire(List<PadButtonBinding> fired, PadButtonBinding b, long nowMs)
    {
        if (nowMs - _lastFire < DebounceMs) return;
        _lastFire = nowMs;
        fired.Add(b);
    }
}

/// <summary>Resolves the bindings that apply to one device: its own rows plus "any pad" rows, the pad-specific row winning per control and trigger.</summary>
public static class PadBindingResolver
{
    public static List<PadButtonBinding> ForDevice(IEnumerable<PadButtonBinding> all, string deviceKey)
    {
        var rows = all.Where(b => b.Device == deviceKey || b.Device == PadControlId.Any).ToList();
        var specific = new HashSet<(string, string)>(rows.Where(b => b.Device == deviceKey).Select(b => (b.Control, b.Trigger)));
        return rows.Where(b => b.Device == deviceKey || !specific.Contains((b.Control, b.Trigger))).ToList();
    }
}
