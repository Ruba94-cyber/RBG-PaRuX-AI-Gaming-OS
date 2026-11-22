using System;
using System.Collections.Generic;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

namespace PaRuX.Services;

/// <summary>One navigation event produced from a pad frame. <see cref="IsAccept"/> means "invoke the focused control".</summary>
public readonly record struct RawPadNavigationEvent(ConsoleNavigationAction Action, bool IsAccept, int PadKey = 0);

/// <summary>
/// Pure edge/repeat translator: feeds mapped <see cref="Xbox360Report"/> frames (with a monotonic millisecond clock) and
/// produces Console/TV navigation events. No WinUI, no hardware, no input synthesis. Busy suspension reuses
/// <see cref="ConsoleNavigationPolicy.Resolve"/>, so Exit, paging, scrolling, search and workspace switching are dropped
/// while a command or dialog is active (Accept and directional moves stay available so the dialog remains operable).
/// Presses that arrive while busy are consumed, never replayed afterwards. Buttons already held when a connection starts
/// (the first frame after <see cref="Reset"/>) are only a baseline and produce no event.
/// </summary>
public sealed class RawPadEdgeTranslator
{
    public const long InitialRepeatMs = 400;
    public const long RepeatMs = 110;
    /// <summary>A direction held longer than this stops repeating until it is released or changes: a stuck stick or a pad that went to
    /// sleep (all axes at zero) must not drive the focus forever.</summary>
    public const long MaxHeldRepeatMs = 3000;
    /// <summary>Stick magnitude (of 32767) that counts as a push; below it the stick is in the dead zone.</summary>
    public const int StickThreshold = 16000;
    public const byte TriggerThreshold = 128;

    private static readonly (ushort Bit, ConsoleNavigationAction Action, bool Accept)[] ButtonEdges =
    {
        (Xbox360Report.A, ConsoleNavigationAction.None, true),
        (Xbox360Report.B, ConsoleNavigationAction.Exit, false),
        (Xbox360Report.LeftShoulder, ConsoleNavigationAction.PreviousPage, false),
        (Xbox360Report.RightShoulder, ConsoleNavigationAction.NextPage, false),
        (Xbox360Report.Start, ConsoleNavigationAction.FocusSearch, false),
        (Xbox360Report.Back, ConsoleNavigationAction.SwitchWorkspace, false),
    };

    private static readonly (ushort Bit, int Vk)[] KeyboardButtons =
    {
        (Xbox360Report.X, 197), (Xbox360Report.Y, 198), (Xbox360Report.Start, 207),
        (Xbox360Report.LeftShoulder, 200), (Xbox360Report.RightShoulder, 199),
    };

    private bool _primed;
    private ushort _heldButtons;
    private bool _leftTrigger, _rightTrigger;
    private ConsoleNavigationAction _moveDir, _scrollDir;
    private long _moveNext, _scrollNext, _moveSince, _scrollSince;

    /// <summary>Forget all held state (disconnect, device change, suspension). The next frame becomes a new baseline.</summary>
    public void Reset()
    {
        _primed = false;
        _heldButtons = 0;
        _leftTrigger = _rightTrigger = false;
        _moveDir = _scrollDir = ConsoleNavigationAction.None;
        _moveSince = _scrollSince = 0;
    }

    public IReadOnlyList<RawPadNavigationEvent> Feed(in Xbox360Report frame, long nowMs, bool interactionBusy, bool overlayOpen = false)
    {
        var events = new List<RawPadNavigationEvent>(2);

        bool lt = frame.LeftTrigger >= TriggerThreshold, rt = frame.RightTrigger >= TriggerThreshold;
        ConsoleNavigationAction move = Direction(frame.Buttons, frame.LeftX, frame.LeftY);
        ConsoleNavigationAction scroll = frame.RightY >= StickThreshold ? ConsoleNavigationAction.ScrollUp
            : frame.RightY <= -StickThreshold ? ConsoleNavigationAction.ScrollDown : ConsoleNavigationAction.None;

        if (!_primed)
        {
            // Baseline: a direction or button already held at connect must be released before it acts.
            _primed = true;
            _heldButtons = frame.Buttons;
            _leftTrigger = lt; _rightTrigger = rt;
            _moveDir = move; _moveNext = nowMs + InitialRepeatMs;
            _scrollDir = scroll; _scrollNext = nowMs + InitialRepeatMs;
            return events;
        }

        ushort pressed = (ushort)(frame.Buttons & ~_heldButtons);
        foreach (var (bit, action, accept) in ButtonEdges)
            if ((pressed & bit) != 0) Emit(events, action, accept, interactionBusy, overlayOpen);
        if (overlayOpen)
        {
            // The on-screen keyboard is driven by pad keys (X backspace, Y space, Start done, LB/RB caret, LT shift, RT layer).
            foreach (var (bit, vk) in KeyboardButtons)
                if ((pressed & bit) != 0) events.Add(new RawPadNavigationEvent(ConsoleNavigationAction.None, false, vk));
            if (lt && !_leftTrigger) events.Add(new RawPadNavigationEvent(ConsoleNavigationAction.None, false, 201));
            if (rt && !_rightTrigger) events.Add(new RawPadNavigationEvent(ConsoleNavigationAction.None, false, 202));
        }
        // The analog triggers never switch workspace (they are pressed by accident too easily); only View/Back does.

        Repeat(events, move, ref _moveDir, ref _moveNext, ref _moveSince, nowMs, interactionBusy, overlayOpen);
        Repeat(events, scroll, ref _scrollDir, ref _scrollNext, ref _scrollSince, nowMs, interactionBusy, overlayOpen);

        _heldButtons = frame.Buttons;
        _leftTrigger = lt; _rightTrigger = rt;
        return events;
    }

    private static ConsoleNavigationAction Direction(ushort buttons, short x, short y)
    {
        // D-pad wins over the stick; vertical wins over horizontal when several are held at once.
        if ((buttons & Xbox360Report.DpadUp) != 0) return ConsoleNavigationAction.Up;
        if ((buttons & Xbox360Report.DpadDown) != 0) return ConsoleNavigationAction.Down;
        if ((buttons & Xbox360Report.DpadLeft) != 0) return ConsoleNavigationAction.Left;
        if ((buttons & Xbox360Report.DpadRight) != 0) return ConsoleNavigationAction.Right;
        int ax = Math.Abs((int)x), ay = Math.Abs((int)y);
        if (Math.Max(ax, ay) < StickThreshold) return ConsoleNavigationAction.None;
        if (ay >= ax) return y > 0 ? ConsoleNavigationAction.Up : ConsoleNavigationAction.Down;
        return x > 0 ? ConsoleNavigationAction.Right : ConsoleNavigationAction.Left;
    }

    private static void Repeat(List<RawPadNavigationEvent> events, ConsoleNavigationAction now, ref ConsoleNavigationAction current,
        ref long next, ref long since, long nowMs, bool busy, bool overlayOpen)
    {
        if (now == ConsoleNavigationAction.None) { current = now; since = 0; return; }
        if (now != current)
        {
            current = now;
            since = nowMs;
            next = nowMs + InitialRepeatMs;
            Emit(events, now, false, busy, overlayOpen);
        }
        else if (nowMs - since > MaxHeldRepeatMs)
        {
            return;   // held "forever": stuck input, stop repeating until released
        }
        else if (nowMs >= next)
        {
            next = nowMs + RepeatMs; // no catch-up burst after a stalled tick
            Emit(events, now, false, busy, overlayOpen);
        }
    }

    private static void Emit(List<RawPadNavigationEvent> events, ConsoleNavigationAction action, bool accept, bool busy, bool overlayOpen)
    {
        if (accept) { events.Add(new RawPadNavigationEvent(ConsoleNavigationAction.None, true)); return; }
        // Policy virtual keys: GamepadB 196, DPad 203-206, shoulders 200/199, right stick 215/216, Menu 207, View 208.
        int vk = action switch
        {
            ConsoleNavigationAction.Exit => 196,
            ConsoleNavigationAction.Up => 203,
            ConsoleNavigationAction.Down => 204,
            ConsoleNavigationAction.Left => 205,
            ConsoleNavigationAction.Right => 206,
            ConsoleNavigationAction.PreviousPage => 200,
            ConsoleNavigationAction.NextPage => 199,
            ConsoleNavigationAction.ScrollUp => 215,
            ConsoleNavigationAction.ScrollDown => 216,
            ConsoleNavigationAction.FocusSearch => 207,
            ConsoleNavigationAction.SwitchWorkspace => 208,
            _ => 0
        };
        ConsoleNavigationAction resolved = ConsoleNavigationPolicy.Resolve(vk, busy, overlayOpen);
        if (resolved != ConsoleNavigationAction.None) events.Add(new RawPadNavigationEvent(resolved, false));
    }
}

