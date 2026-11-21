using System.Globalization;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

namespace PaRuX.VirtualPadSim;

/// <summary>A script was rejected (unknown verb, bad argument, or a hard limit was exceeded).</summary>
public sealed class PadScriptException(string message) : Exception(message);

/// <summary>One parsed verb with the time (ms from script start) at which it begins.</summary>
public sealed record PadScriptStep(int StartMs, int DurationMs, string Text);

/// <summary>The pad state that becomes active at <see cref="TimeMs"/>.</summary>
public readonly record struct PadFrame(int TimeMs, Xbox360Report Report);

/// <summary>
/// Pure parser/scheduler for the virtual pad simulator. <c>wait</c> advances the script clock; every other verb starts at
/// the current clock and does NOT advance it, so verbs placed without a <c>wait</c> in between overlap. Overlapping buttons
/// are OR-ed; for the same stick or trigger the verb that started last wins while it is active.
/// </summary>
public sealed class PadScript
{
    public const int MaxDurationMs = 60_000;
    public const int MaxVerbs = 200;
    public const int DefaultHoldMs = 120;

    public IReadOnlyList<PadScriptStep> Steps { get; }
    /// <summary>Report frames in time order; the first is always the neutral report at t=0. Only changes are listed.</summary>
    public IReadOnlyList<PadFrame> Frames { get; }
    public int TotalMs { get; }

    private PadScript(IReadOnlyList<PadScriptStep> steps, IReadOnlyList<PadFrame> frames, int totalMs)
    { Steps = steps; Frames = frames; TotalMs = totalMs; }

    private static readonly Dictionary<string, ushort> Buttons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = Xbox360Report.A, ["b"] = Xbox360Report.B, ["x"] = Xbox360Report.X, ["y"] = Xbox360Report.Y,
        ["lb"] = Xbox360Report.LeftShoulder, ["rb"] = Xbox360Report.RightShoulder,
        ["back"] = Xbox360Report.Back, ["start"] = Xbox360Report.Start, ["guide"] = Xbox360Report.Guide,
        ["ls"] = Xbox360Report.LeftThumb, ["rs"] = Xbox360Report.RightThumb,
    };

    private static readonly Dictionary<string, ushort> Dpad = new(StringComparer.OrdinalIgnoreCase)
    {
        ["up"] = Xbox360Report.DpadUp, ["down"] = Xbox360Report.DpadDown,
        ["left"] = Xbox360Report.DpadLeft, ["right"] = Xbox360Report.DpadRight,
    };

    private enum Kind { Buttons, LeftTrigger, RightTrigger, LeftStick, RightStick }
    private sealed record Interval(Kind Kind, int Start, int End, ushort Mask, byte Trigger, short X, short Y);

    public static PadScript Parse(string text)
    {
        if (text is null) throw new PadScriptException("The script is empty.");
        var verbs = text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (verbs.Length == 0) throw new PadScriptException("The script is empty.");
        if (verbs.Length > MaxVerbs) throw new PadScriptException($"The script has {verbs.Length} verbs; the limit is {MaxVerbs}.");

        var steps = new List<PadScriptStep>();
        var intervals = new List<Interval>();
        int clock = 0, end = 0;
        foreach (string verb in verbs)
        {
            string[] t = verb.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string name = t[0].ToLowerInvariant();
            int duration = 0;
            switch (name)
            {
                case "wait":
                    Arity(verb, t, 2, 2);
                    int waitMs = Ms(verb, t[1], allowZero: true);
                    steps.Add(new PadScriptStep(clock, waitMs, verb));   // logged when the wait begins
                    clock += waitMs;
                    if (clock > MaxDurationMs) throw TooLong();
                    continue;
                case "press":
                    Arity(verb, t, 2, 3);
                    duration = t.Length == 3 ? Ms(verb, t[2]) : DefaultHoldMs;
                    intervals.Add(new Interval(Kind.Buttons, clock, clock + duration, Lookup(verb, Buttons, t[1], "button"), 0, 0, 0));
                    break;
                case "dpad":
                    Arity(verb, t, 2, 3);
                    duration = t.Length == 3 ? Ms(verb, t[2]) : DefaultHoldMs;
                    intervals.Add(new Interval(Kind.Buttons, clock, clock + duration, Lookup(verb, Dpad, t[1], "direction"), 0, 0, 0));
                    break;
                case "stick":
                    Arity(verb, t, 4, 5);
                    Kind side = t[1].ToLowerInvariant() switch
                    {
                        "left" => Kind.LeftStick, "right" => Kind.RightStick,
                        _ => throw new PadScriptException($"'{verb}': stick must be 'left' or 'right'."),
                    };
                    short x = Axis(verb, t[2]), y = Axis(verb, t[3]);
                    duration = t.Length == 5 ? Ms(verb, t[4]) : DefaultHoldMs;
                    intervals.Add(new Interval(side, clock, clock + duration, 0, 0, x, y));
                    break;
                case "hold":
                    Arity(verb, t, 3, 3);
                    duration = Ms(verb, t[2]);
                    string target = t[1].ToLowerInvariant();
                    if (target == "lt") intervals.Add(new Interval(Kind.LeftTrigger, clock, clock + duration, 0, 255, 0, 0));
                    else if (target == "rt") intervals.Add(new Interval(Kind.RightTrigger, clock, clock + duration, 0, 255, 0, 0));
                    else if (Buttons.TryGetValue(target, out ushort mask)) intervals.Add(new Interval(Kind.Buttons, clock, clock + duration, mask, 0, 0, 0));
                    else throw new PadScriptException($"'{verb}': unknown hold target '{t[1]}' (expected lt, rt or a button).");
                    break;
                default:
                    throw new PadScriptException($"Unknown verb '{t[0]}' in '{verb}'.");
            }
            end = Math.Max(end, clock + duration);
            if (end > MaxDurationMs) throw TooLong();
            steps.Add(new PadScriptStep(clock, duration, verb));
        }
        int total = Math.Max(end, clock);
        return new PadScript(steps, BuildFrames(intervals), total);
    }

    public const int MaxLineDurationMs = 10_000;
    public const int MaxInteractiveVerbs = 2000;

    /// <summary>Parses ONE interactive-mode line: exactly one verb, same grammar, at most 10 s long. Frames are relative to the line start.</summary>
    public static PadScript ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) throw new PadScriptException("Empty line.");
        if (line.Contains(';')) throw new PadScriptException("Send one verb per line.");
        var script = Parse(line);
        if (script.TotalMs > MaxLineDurationMs) throw new PadScriptException($"A single verb may last at most {MaxLineDurationMs} ms.");
        return script;
    }

    private static PadScriptException TooLong() => new($"The script is longer than {MaxDurationMs} ms.");

    private static void Arity(string verb, string[] t, int min, int max)
    {
        if (t.Length < min || t.Length > max) throw new PadScriptException($"'{verb}': wrong number of arguments.");
    }

    private static int Ms(string verb, string s, bool allowZero = false)
    {
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v > MaxDurationMs || (v == 0 && !allowZero))
            throw new PadScriptException($"'{verb}': '{s}' is not a valid duration in ms (1..{MaxDurationMs}).");
        return v;
    }

    private static short Axis(string verb, string s)
    {
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || v < -1 || v > 1)
            throw new PadScriptException($"'{verb}': '{s}' is not an axis value between -1 and 1.");
        return (short)Math.Round(v * 32767.0);
    }

    private static ushort Lookup(string verb, Dictionary<string, ushort> map, string key, string what) =>
        map.TryGetValue(key, out ushort v) ? v : throw new PadScriptException($"'{verb}': unknown {what} '{key}'.");

    private static List<PadFrame> BuildFrames(List<Interval> intervals)
    {
        var times = new SortedSet<int> { 0 };
        foreach (var i in intervals) { times.Add(i.Start); times.Add(i.End); }
        var frames = new List<PadFrame>();
        Xbox360Report? last = null;
        foreach (int time in times)
        {
            ushort buttons = 0; byte lt = 0, rt = 0; short lx = 0, ly = 0, rx = 0, ry = 0;
            foreach (var i in intervals)   // list order = start order, so a later verb overwrites an earlier one
            {
                if (time < i.Start || time >= i.End) continue;
                switch (i.Kind)
                {
                    case Kind.Buttons: buttons |= i.Mask; break;
                    case Kind.LeftTrigger: lt = i.Trigger; break;
                    case Kind.RightTrigger: rt = i.Trigger; break;
                    case Kind.LeftStick: lx = i.X; ly = i.Y; break;
                    case Kind.RightStick: rx = i.X; ry = i.Y; break;
                }
            }
            var report = new Xbox360Report(buttons, lt, rt, lx, ly, rx, ry);
            if (last is null || report != last) { frames.Add(new PadFrame(time, report)); last = report; }
        }
        return frames;
    }
}
