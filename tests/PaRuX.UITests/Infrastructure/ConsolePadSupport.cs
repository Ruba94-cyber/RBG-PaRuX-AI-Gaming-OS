using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace PaRuX.UITests.Infrastructure;

/// <summary>
/// A simulated Xbox 360 pad (the product's own ViGEm client, driven by tools/PaRuX.VirtualPadSim --interactive). Verbs are the
/// simulator's script verbs, for example "dpad right", "press a", "press back"; each call returns when the simulator replied OK.
/// </summary>
internal sealed class PadSim : IDisposable
{
    private readonly Process _process;

    internal PadSim()
    {
        string? exe = Environment.GetEnvironmentVariable("PARUX_VPADSIM");
        if (string.IsNullOrEmpty(exe))
        {
            string bin = Path.Combine(Repository.Root, "tools", "PaRuX.VirtualPadSim", "bin", "Debug");
            exe = Directory.Exists(bin)
                ? Directory.GetFiles(bin, "PaRuX.VirtualPadSim.exe", SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }
        if (exe is null || !File.Exists(exe))
            throw new FileNotFoundException("Build it first: dotnet build tools/PaRuX.VirtualPadSim -c Debug (or set PARUX_VPADSIM).");
        _process = Process.Start(new ProcessStartInfo(exe, "--interactive")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true
        })!;
        Assert.Equal("READY", ReadLine(TimeSpan.FromSeconds(30)));
    }

    private string? ReadLine(TimeSpan timeout)
    {
        var read = _process.StandardOutput.ReadLineAsync();
        return read.Wait(timeout) ? read.Result : null;
    }

    internal void Send(string verb)
    {
        _process.StandardInput.WriteLine(verb);
        _process.StandardInput.Flush();
        string? reply = ReadLine(TimeSpan.FromSeconds(20));
        Assert.True(reply is not null && reply.StartsWith("OK", StringComparison.Ordinal), "pad simulator replied: " + reply);
    }

    public void Dispose()
    {
        try { _process.StandardInput.WriteLine("quit"); _process.StandardInput.Flush(); } catch { }
        try { if (!_process.WaitForExit(5000)) _process.Kill(true); } catch { }
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
internal struct ConsoleMonitorInfoEx
{
    public int Size;
    public int L1, T1, R1, B1, L2, T2, R2, B2;
    public int Flags;
    [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
}

/// <summary>
/// Reusable harness for pad-only tests of Console/TV mode: a synthetic Gaming library, the app started in TV mode on this PC's primary
/// display (treated as the TV), a simulated pad, and pad-driven navigation by the focused element's AutomationId / bounds.
/// </summary>
internal sealed class ConsolePadSession : IDisposable
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(long point, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref ConsoleMonitorInfoEx info);

    internal static string PrimaryDisplayId()
    {
        IntPtr monitor = MonitorFromPoint(0, 1);
        var info = new ConsoleMonitorInfoEx { Size = System.Runtime.InteropServices.Marshal.SizeOf<ConsoleMonitorInfoEx>(), Device = string.Empty };
        Assert.True(GetMonitorInfoW(monitor, ref info));
        return info.Device;
    }

    /// <summary>Writes the "start in TV mode when the chosen TV is on" settings into a data root (this PC's primary display = the TV).</summary>
    internal static void SeedTvStart(string root)
    {
        string settingsPath = Path.Combine(root, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        string display = PrimaryDisplayId();
        settings["ConsoleStartOnTvWhenPresent"] = true;
        settings["ConsoleTargetDisplayId"] = display;
        settings["StartupWithoutTv"] = "normal";
        settings["ConsoleDisplays"] = new JsonArray(new JsonObject { ["DisplayId"] = display, ["Role"] = "tv" });
        File.WriteAllText(settingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    internal UiTestApplication App { get; }
    internal PadSim Pad { get; }
    private readonly List<string> _timeline = new();
    private readonly string _name;

    internal ConsolePadSession(string testName, int gameCount = 12)
    {
        _name = testName;
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(gameCount);
        SeedTvStart(root);
        App = new UiTestApplication(testName, restartDataRoot: root);
        try
        {
            UiWait.For(() => ConsoleOpen, "app opened in Console/TV mode", TimeSpan.FromSeconds(60));
            UiWait.ById(App.MainWindow, "GameTitles_Search_TextBox");
            try { App.MainWindow.SetForeground(); } catch (FlaUI.Core.Exceptions.FlaUIException) { }
            Pad = new PadSim();
            AttachPad();
        }
        catch { App.Dispose(); throw; }
    }

    internal bool ConsoleOpen =>
        App.FindRawDescendant(App.MainWindow, e => (e.Properties.Name.ValueOrDefault ?? string.Empty) == "Exit Console mode") != null;

    internal string FocusedId => App.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault ?? string.Empty;
    internal string FocusedName => App.Automation.FocusedElement()?.Properties.Name.ValueOrDefault ?? string.Empty;
    internal AutomationElement? Focused => App.Automation.FocusedElement();

    internal void Mark(string text)
    {
        string line = DateTime.UtcNow.ToString("HH:mm:ss.fff") + "  " + text;
        _timeline.Add(line);
        // Written through at once: a crashing test host must not lose the evidence.
        try { File.AppendAllText(Path.Combine(Repository.Root, ".ai", "artifacts", "console-pad-live-" + _name + ".txt"), line + Environment.NewLine); } catch { }
    }

    /// <summary>The app attaches to a newly plugged pad within about a second: wait, then press until the focus really moves.</summary>
    private void AttachPad()
    {
        Thread.Sleep(2500);
        string before = FocusedId;
        for (int attempt = 0; attempt < 8 && FocusedId == before; attempt++) { Pad.Send("dpad right"); Thread.Sleep(900); }
        Assert.NotEqual(before, FocusedId);
        Mark("pad attached; focus " + FocusedId);
    }

    internal void Press(string verb, int settleMs = 450) { Pad.Send(verb); Thread.Sleep(settleMs); }
    internal void A() => Press("press a", 700);
    internal void B() => Press("press b", 700);
    internal void Dir(string direction) => Press("dpad " + direction);

    /// <summary>True while any ContentDialog button (default PrimaryButton/CloseButton ids) is on screen.</summary>
    internal bool DialogOpen() =>
        App.FindRawDescendant(App.MainWindow, e =>
        {
            string id = e.Properties.AutomationId.ValueOrDefault ?? string.Empty;
            return id is "PrimaryButton" or "CloseButton" or "SecondaryButton";
        }) != null
        || App.Automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton")) != null
        || App.Automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")) != null;

    /// <summary>
    /// Presses the D-pad toward the element with <paramref name="automationId"/> (decided from the focused element's and the target's
    /// bounds each step) until it is focused. Fails after <paramref name="maxPresses"/> presses: that is a pad-reachability bug.
    /// </summary>
    internal AutomationElement NavigateTo(string automationId, int maxPresses = 60)
    {
        for (int i = 0; i < maxPresses; i++)
        {
            if (FocusedId == automationId) return Focused!;
            AutomationElement? target = App.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            AutomationElement? focused = Focused;
            if (target is null || focused is null)
            {
                Dir(i % 2 == 0 ? "down" : "right");
                continue;
            }
            var t = target.BoundingRectangle; var f = focused.BoundingRectangle;
            double dx = (t.Left + t.Width / 2) - (f.Left + f.Width / 2), dy = (t.Top + t.Height / 2) - (f.Top + f.Height / 2);
            bool vertical = Math.Abs(dy) >= Math.Abs(dx);
            // Alternate the axis when the same press did not move the focus (focus can need a detour around a row boundary).
            if (i % 5 == 4) vertical = !vertical;
            string dir = vertical ? (dy >= 0 ? "down" : "up") : (dx >= 0 ? "right" : "left");
            string before = FocusedId + "|" + FocusedName;
            Dir(dir);
            Mark($"toward {automationId}: {dir}, focus {before} -> {FocusedId}|{FocusedName}");
        }
        Assert.Fail($"The pad could not reach '{automationId}' in {maxPresses} presses; focus is on '{FocusedId}' / '{FocusedName}'.");
        return null!;
    }

    /// <summary>Opens the launch confirmation of the first synthetic game with the pad only (A on the tile, then to Play).</summary>
    internal void OpenFirstGameLaunchDialog()
    {
        UiWait.ById(App.MainWindow, "GameTitles_Search_TextBox");
        const string play = "GameTitle_Play_syntheticgame0001";
        for (int step = 0; step < 6 && !DialogOpen(); step++)
        {
            // A on the first tile opens its detail; the Play button is then reached by the pad and pressed.
            if (FocusedId == play) { Mark("A on Play"); A(); }
            else if (FocusedId == "GameTile_syntheticgame0001") { Mark("A on the tile"); A(); }
            else if (App.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(play)) is not null)
            {
                // After the dialog closes the focus lands on a control of the same detail card (Favorite): Play is above it.
                for (int up = 0; up < 5 && FocusedId != play; up++) Dir("up");
                Mark("after dialog, focus " + FocusedId);
            }
            else Dir("down");
        }
        UiWait.For(DialogOpen, "a dialog opened from the pad", TimeSpan.FromSeconds(15));
    }

    public void Dispose()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(Repository.Root, ".ai", "artifacts"));
            File.WriteAllLines(Path.Combine(Repository.Root, ".ai", "artifacts", "console-pad-" + _name + ".txt"), _timeline);
        }
        catch { }
        try { Pad?.Dispose(); } catch { }
        try { App.Dispose(); } catch { }
    }
}

