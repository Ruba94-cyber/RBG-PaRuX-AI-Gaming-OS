using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Opt-in real-game acceptance (set PARUX_REAL_GAMES=1): the real app opens in Console/TV mode on the Gaming library, a SIMULATED
/// Xbox-class controller (the product's own ViGEm client, driven by tools/PaRuX.VirtualPadSim) navigates it exactly as Windows
/// delivers a real pad, switches to the AI page and back, selects one of the owner's installed games and launches it, and the test
/// checks the game process starts and the app survives the game's exit. The owner's real games are launched; nothing is installed,
/// changed or deleted, and the data root is an isolated copy.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class RealGameConsolePadTests
{
    private sealed class PadSim : IDisposable
    {
        private readonly Process _process;
        internal PadSim()
        {
            string exe = Environment.GetEnvironmentVariable("PARUX_VPADSIM")
                ?? Path.Combine(Repository.Root, "tools", "PaRuX.VirtualPadSim", "bin", "Debug", "net10.0", "PaRuX.VirtualPadSim.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("Build tools/PaRuX.VirtualPadSim first.", exe);
            _process = Process.Start(new ProcessStartInfo(exe, "--interactive")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true
            })!;
            string? first = ReadLine(TimeSpan.FromSeconds(30));
            Assert.Equal("READY", first);
        }

        private string? ReadLine(TimeSpan timeout)
        {
            var read = _process.StandardOutput.ReadLineAsync();
            return read.Wait(timeout) ? read.Result : null;
        }

        /// <summary>Sends one verb (for example "dpad right") and waits until the simulator finished it.</summary>
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
    private struct MonitorInfoEx
    {
        public int Size;
        public int L1, T1, R1, B1, L2, T2, R2, B2;
        public int Flags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(long point, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);

    private static string PrimaryDisplayId()
    {
        IntPtr monitor = MonitorFromPoint(0, 1);
        var info = new MonitorInfoEx { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfoEx>(), Device = string.Empty };
        Assert.True(GetMonitorInfoW(monitor, ref info));
        return info.Device;
    }

    private static string FocusedId(UiTestApplication app) => app.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault ?? string.Empty;
    private static string FocusedName(UiTestApplication app) => app.Automation.FocusedElement()?.Properties.Name.ValueOrDefault ?? string.Empty;

    private static bool ConsoleOpen(UiTestApplication app) =>
        app.FindRawDescendant(app.MainWindow, e => (e.Properties.Name.ValueOrDefault ?? string.Empty) == "Exit Console mode") != null;

    private static string NameOnly(string path) => Path.GetFileNameWithoutExtension(path);

    [Theory, Trait("Category", "RealGames"), Trait("Category", "HardwareE2E"), Trait("Category", "FlaUI")]
    // Mario Kart World runs on the owner's Eden 2 build (lichii.exe) from a .nup package: an EMULATED title, launched like the
    // emulator import registers it (eden recipe: -f -g <package>).
    [InlineData("Mario Kart World", @"C:\Games\Mario Kart World\lichii.exe", @"C:\Games\Mario Kart World\games\MKW.nup")]
    [InlineData("EA SPORTS FC 27", @"C:\Games\EA SPORTS FC 27\FC27.exe", "")]
    [InlineData("The Last of Us Part II", @"C:\Games\The Last of Us - Part II Remastered\tlou-ii.exe", "")]
    public void Console_tv_mode_is_navigated_by_a_simulated_controller_and_launches_a_real_game(string gameName, string exePath, string gameFile)
    {
        if (Environment.GetEnvironmentVariable("PARUX_REAL_GAMES") != "1" || !File.Exists(exePath)) return;   // opt-in, and only where the game exists

        string processName = NameOnly(exePath);
        foreach (Process stale in Process.GetProcessesByName(processName)) { try { stale.Kill(true); } catch { } }

        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(3);
        // Replace the first synthetic game with the real one (the library file is the same store the app reads).
        string gamesPath = Path.Combine(root, "server", "manual-games.json");
        JsonNode games = JsonNode.Parse(File.ReadAllText(gamesPath))!;
        JsonObject first = games["Games"]![0]!.AsObject();
        first["Name"] = gameName;
        first["ExecutablePath"] = exePath;
        first["WorkingDirectory"] = Path.GetDirectoryName(exePath)!;
        if (gameFile.Length > 0)
        {
            first["Arguments"] = "-f -g \"" + gameFile + "\"";
            first["Platform"] = "Nintendo Switch";
            first["EmulatorId"] = "eden";
            first["GameFilePath"] = gameFile;
        }
        if (gameFile.Length > 0)
        {
            first["Arguments"] = "-f -g \"" + gameFile + "\"";
            first["Platform"] = "Nintendo Switch";
            first["EmulatorId"] = "eden";
            first["GameFilePath"] = gameFile;
        }
        File.WriteAllText(gamesPath, games.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        // Console start on this PC's own screen marked as the TV.
        string settingsPath = Path.Combine(root, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        string display = PrimaryDisplayId();
        settings["ConsoleStartOnTvWhenPresent"] = true;
        settings["ConsoleTargetDisplayId"] = display;
        settings["StartupWithoutTv"] = "normal";
        settings["ConsoleDisplays"] = new JsonArray(new JsonObject { ["DisplayId"] = display, ["Role"] = "tv" });
        File.WriteAllText(settingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var timeline = new List<string>();
        void Mark(string text) => timeline.Add(DateTime.UtcNow.ToString("HH:mm:ss.fff") + "  " + text);
        using var app = new UiTestApplication(nameof(Console_tv_mode_is_navigated_by_a_simulated_controller_and_launches_a_real_game) + "-" + processName, restartDataRoot: root);
        try
        {
            UiWait.For(() => ConsoleOpen(app), "app opened in Console/TV mode", TimeSpan.FromSeconds(60));
            UiWait.ById(app.MainWindow, "GameTitles_Search_TextBox");
            Mark("console mode open on the Gaming library");

            using var pad = new PadSim();
            Mark("simulated controller plugged");

            // 1. The pad moves the focus (D-pad right, then down) and the focus really changes.
            string before = FocusedId(app);
            pad.Send("dpad right");
            UiWait.For(() => FocusedId(app) != before, "focus moved after D-pad right");
            Mark("D-pad right moved focus: " + before + " -> " + FocusedId(app));

            // 2. View/Back switches to the AI (Chat) page without leaving fullscreen; B returns to Gaming.
            pad.Send("press back");
            UiWait.ById(app.MainWindow, "Chat_Send_Button");
            Assert.True(ConsoleOpen(app), "still in Console/TV mode on the AI page");
            Mark("View switched to the AI page");
            pad.Send("press b");
            UiWait.ById(app.MainWindow, "GameTitles_Search_TextBox");
            Assert.True(ConsoleOpen(app), "B on the AI page returns to Gaming, not to the desktop");
            Mark("B returned to the Gaming library");

            // 3. Navigate to the chosen game with the pad only (bounded), then select it and launch it.
            AutomationElement? tile = null;
            for (int step = 0; step < 40 && tile == null; step++)
            {
                if (FocusedName(app).Contains(gameName, StringComparison.OrdinalIgnoreCase)) { tile = app.Automation.FocusedElement(); break; }
                pad.Send(step % 7 == 6 ? "dpad down" : "dpad right");
                Thread.Sleep(250);
            }
            Assert.NotNull(tile);
            Mark("focused the game tile: " + FocusedId(app) + " / " + FocusedName(app));
            pad.Send("press a");
            Mark("pressed A on the tile");

            // The tile opens the game detail (or launches straight away): press A on the Play button when it is focused.
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < deadline && Process.GetProcessesByName(processName).Length == 0)
            {
                string id = FocusedId(app);
                if (id.StartsWith("GameTitle_Play_", StringComparison.Ordinal) || id.StartsWith("GameTilePlay_", StringComparison.Ordinal))
                {
                    Mark("Play focused (" + id + "): pressing A");
                    pad.Send("press a");
                }
                else
                {
                    // Move toward the Play button of the detail card.
                    pad.Send("dpad down");
                }
                Thread.Sleep(1500);
            }
            Process[] running = Process.GetProcessesByName(processName);
            Assert.NotEmpty(running);
            Mark("the game process started: " + processName + " pid " + running[0].Id);

            // 4. Gaming mode took over: no AI runtime is left running next to the game.
            Thread.Sleep(5000);
            Assert.Empty(Process.GetProcessesByName("llama-server"));
            Mark("no llama-server next to the game (AI stepped aside)");
            app.CaptureSnapshot("real-game-running-" + processName);

            // 5. The game exits: PaRuX must survive and stay in Console/TV mode.
            foreach (Process p in Process.GetProcessesByName(processName)) { try { p.Kill(true); } catch { } }
            UiWait.For(() => Process.GetProcessesByName(processName).Length == 0, "game process ended");
            Thread.Sleep(3000);
            Assert.False(app.MainWindow.Properties.IsOffscreen.ValueOrDefault && !ConsoleOpen(app), "app still present after the game exit");
            Mark("game closed; app still running");
        }
        catch (Exception ex)
        {
            File.WriteAllLines(Path.Combine(root, "real-game-timeline.txt"), timeline);
            app.CaptureFailure(nameof(Console_tv_mode_is_navigated_by_a_simulated_controller_and_launches_a_real_game) + "-" + processName, ex);
            throw;
        }
        finally
        {
            File.WriteAllLines(Path.Combine(Repository.Root, ".ai", "artifacts", "real-game-" + processName + ".txt"), timeline);
            foreach (Process p in Process.GetProcessesByName(processName)) { try { p.Kill(true); } catch { } }
        }
    }
}
