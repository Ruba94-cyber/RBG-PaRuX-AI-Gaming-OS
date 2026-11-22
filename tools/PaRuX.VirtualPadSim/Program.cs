using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

namespace PaRuX.VirtualPadSim;

/// <summary>
/// Developer/test tool: plugs ONE virtual Xbox 360 pad (through the product's own ViGEmBus client), plays a script, unplugs it.
/// Exit codes: 0 ok, 1 unexpected failure, 2 usage or script error, 3 virtual pad could not be plugged, 4 --verify: Windows did not see the press.
/// </summary>
internal static class Program
{
    private const string Usage =
        "Usage: PaRuX.VirtualPadSim (--script \"<verbs>\" | --script-file <path>) [--stay-ms N] [--verify] | --interactive (stdin: one verb per line, replies READY / OK <verb> / ERR <reason>, 'quit' ends)\n" +
        "Verbs (';'-separated): wait <ms> | press <a|b|x|y|lb|rb|back|start|guide|ls|rs> [ms] | dpad <up|down|left|right> [ms] |\n" +
        "  stick <left|right> <x -1..1> <y -1..1> [ms] | hold <lt|rt|button> <ms>   (limits: 60 s, 200 verbs)";

    private static int Main(string[] args)
    {
        string? scriptText = null; int stayMs = 0; bool verify = false, interactive = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--script" when i + 1 < args.Length: scriptText = args[++i]; break;
                    case "--script-file" when i + 1 < args.Length: scriptText = File.ReadAllText(args[++i]).Replace('\r', ' ').Replace('\n', ';'); break;
                    case "--stay-ms" when i + 1 < args.Length:
                        if (!int.TryParse(args[++i], out stayMs) || stayMs < 0 || stayMs > 60_000) throw new PadScriptException("--stay-ms must be 0..60000.");
                        break;
                    case "--verify": verify = true; break;
                    case "--interactive": interactive = true; break;
                    default: throw new PadScriptException($"Unknown or incomplete argument '{args[i]}'.");
                }
            }
            if (interactive)
            {
                if (scriptText is not null || verify || stayMs > 0) throw new PadScriptException("--interactive cannot be combined with other modes.");
                return RunInteractive();
            }
            if (scriptText is null) throw new PadScriptException("No script given.");
            return Run(PadScript.Parse(scriptText), stayMs, verify);
        }
        catch (Exception ex) when (ex is PadScriptException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("unexpected failure: " + ex);
            return 1;
        }
    }

    private static int Run(PadScript script, int stayMs, bool verify)
    {
        Stopwatch clock = Stopwatch.StartNew();
        void Log(string message) => Console.WriteLine($"[{clock.ElapsedMilliseconds,6} ms] {message}");

        uint beforeMask = verify ? XInputNative.ConnectedSlotMask() : 0;
        var backend = new ViGEmBusClient();
        using IVirtualXboxPad? pad = backend.TryCreatePad(out string? error);
        if (pad is null)
        {
            Console.Error.WriteLine("could not plug the virtual pad: " + error);
            if (verify) Console.WriteLine(JsonSerializer.Serialize(new { plugged = false, error }));
            return 3;
        }
        Log("pad plugged");

        int slot = -1; XInputPoller? poller = null;
        int gamepadsSeen = -1;
        if (verify)
        {
            pad.Submit(default);
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 3000 && slot < 0)
            {
                uint now = XInputNative.ConnectedSlotMask() & ~beforeMask;
                if (now != 0) slot = System.Numerics.BitOperations.TrailingZeroCount(now);
                else Thread.Sleep(20);
            }
            gamepadsSeen = CountWinRtGamepads();
            Log($"xinput slot={slot} winrt gamepads={gamepadsSeen}");
            if (slot >= 0) poller = new XInputPoller(slot);
        }

        // Merge the verb log and the report frames into one timeline (log first at equal times).
        clock.Restart();
        ushort expectedButtons = 0;
        foreach (var f in script.Frames) expectedButtons |= f.Report.Buttons;
        Play(script, pad, clock, Log);
        if (stayMs > 0) { Log($"staying plugged for {stayMs} ms"); Thread.Sleep(stayMs); }

        int exit = 0;
        if (verify)
        {
            poller?.Stop();
            ushort seen = poller?.SeenButtons ?? 0;
            bool pressed = slot >= 0 && (seen & expectedButtons) == expectedButtons;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                plugged = true,
                xinputSlot = slot,
                pressedSeenByXInput = pressed,
                expectedButtons = $"0x{expectedButtons:X4}",
                seenButtons = $"0x{seen:X4}",
                maxLeftTriggerSeen = poller?.MaxLeftTrigger ?? 0,
                maxLeftStickXSeen = poller?.MaxAbsLeftX ?? 0,
                polls = poller?.Polls ?? 0,
                winrtGamepads = gamepadsSeen,
            }));
            if (!pressed) exit = 4;
        }
        pad.Dispose();
        Log("pad unplugged");
        return exit;
    }

    /// <summary>Plays one script on the pad; the clock must have just been restarted. Returns when the last verb has finished.</summary>
    private static void Play(PadScript script, IVirtualXboxPad pad, Stopwatch clock, Action<string> log)
    {
        int stepIndex = 0, frameIndex = 0;
        while (stepIndex < script.Steps.Count || frameIndex < script.Frames.Count)
        {
            int nextStep = stepIndex < script.Steps.Count ? script.Steps[stepIndex].StartMs : int.MaxValue;
            int nextFrame = frameIndex < script.Frames.Count ? script.Frames[frameIndex].TimeMs : int.MaxValue;
            SleepUntil(clock, Math.Min(nextStep, nextFrame));
            if (nextStep <= nextFrame)
            {
                log($"verb: {script.Steps[stepIndex++].Text}");
            }
            else
            {
                var frame = script.Frames[frameIndex++];
                bool ok = pad.Submit(frame.Report);
                log($"report buttons=0x{frame.Report.Buttons:X4} lt={frame.Report.LeftTrigger} rt={frame.Report.RightTrigger} " +
                    $"ls=({frame.Report.LeftX},{frame.Report.LeftY}) rs=({frame.Report.RightX},{frame.Report.RightY}){(ok ? "" : " SUBMIT FAILED")}");
            }
        }
        SleepUntil(clock, script.TotalMs);
    }

    /// <summary>
    /// Protocol: plug once, print READY; per stdin line (one verb) print "OK &lt;verb&gt;" or "ERR &lt;reason&gt;" after it has fully run;
    /// "quit" or EOF unplugs and exits 0. At most 2000 verbs per session; 10 minutes without input unplugs and exits.
    /// Only protocol lines go to stdout; diagnostics go to stderr.
    /// </summary>
    private static int RunInteractive()
    {
        var backend = new ViGEmBusClient();
        using IVirtualXboxPad? pad = backend.TryCreatePad(out string? error);
        if (pad is null)
        {
            Console.Error.WriteLine("could not plug the virtual pad: " + error);
            return 3;
        }
        var clock = new Stopwatch();
        pad.Submit(default);
        Console.Out.WriteLine("READY"); Console.Out.Flush();
        int executed = 0;
        var idle = TimeSpan.FromMinutes(10);
        Task<string?> read = Task.Run(Console.In.ReadLine);
        while (true)
        {
            if (!read.Wait(idle)) { Console.Error.WriteLine("idle timeout; unplugging"); return 0; }
            string? line = read.Result;
            if (line is null || line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase)) return 0;
            read = Task.Run(Console.In.ReadLine);   // keep listening while this verb runs
            string verb = line.Trim();
            try
            {
                if (executed >= PadScript.MaxInteractiveVerbs) throw new PadScriptException($"Session limit of {PadScript.MaxInteractiveVerbs} verbs reached.");
                var script = PadScript.ParseLine(verb);
                executed++;
                clock.Restart();
                Play(script, pad, clock, _ => { });
                Console.Out.WriteLine("OK " + verb);
            }
            catch (PadScriptException ex) { Console.Out.WriteLine("ERR " + ex.Message); }
            Console.Out.Flush();
        }
    }

    private static int CountWinRtGamepads()
    {
        try
        {
            Thread.Sleep(300); // the WinRT list is populated asynchronously after a pad arrives
            return Windows.Gaming.Input.Gamepad.Gamepads.Count;
        }
        catch (Exception) { return -1; }
    }

    private static void SleepUntil(Stopwatch clock, int ms)
    {
        if (ms == int.MaxValue) return;
        while (true)
        {
            long left = ms - clock.ElapsedMilliseconds;
            if (left <= 0) return;
            if (left > 2) Thread.Sleep((int)(left - 1)); else Thread.SpinWait(200);
        }
    }
}

/// <summary>Windows' own view of XInput (xinput1_4.dll).</summary>
internal static class XInputNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct State { public uint PacketNumber; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    internal static extern uint GetState(uint index, out State state);

    internal static uint ConnectedSlotMask()
    {
        uint mask = 0;
        for (uint i = 0; i < 4; i++) if (GetState(i, out _) == 0) mask |= 1u << (int)i;
        return mask;
    }
}

/// <summary>Polls one XInput slot every few ms and accumulates everything Windows reported.</summary>
internal sealed class XInputPoller
{
    private readonly int _slot; private readonly Thread _thread; private volatile bool _stop;
    public ushort SeenButtons { get; private set; }
    public byte MaxLeftTrigger { get; private set; }
    public int MaxAbsLeftX { get; private set; }
    public int Polls { get; private set; }

    public XInputPoller(int slot)
    {
        _slot = slot;
        _thread = new Thread(Loop) { IsBackground = true, Name = "xinput-poller" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop)
        {
            if (XInputNative.GetState((uint)_slot, out var s) == 0)
            {
                Polls++;
                SeenButtons |= s.Buttons;
                if (s.LeftTrigger > MaxLeftTrigger) MaxLeftTrigger = s.LeftTrigger;
                MaxAbsLeftX = Math.Max(MaxAbsLeftX, Math.Abs((int)s.LX));
            }
            Thread.Sleep(2);
        }
    }

    public void Stop() { _stop = true; _thread.Join(500); }
}
