using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Real-app proof of the opt-in "start in TV mode when the chosen TV is on" preference. The "TV connected" case marks this PC's
/// own primary display as the TV (the preference only needs the display to be classified TV, chosen and connected); the "TV not
/// connected" case chooses a display id that does not exist. No Windows display setting is changed.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ConsoleStartupUiTests
{
    private static void Seed(string root, bool enabled, string displayId, string withoutTv, bool markAsTv = true)
    {
        string path = Path.Combine(root, "settings.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["ConsoleStartOnTvWhenPresent"] = enabled;
        json["ConsoleTargetDisplayId"] = displayId;
        json["StartupWithoutTv"] = withoutTv;
        json["ConsoleDisplays"] = new JsonArray(new JsonObject { ["DisplayId"] = displayId, ["Role"] = markAsTv ? "tv" : "monitor" });
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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

    /// <summary>The Windows device name of the primary display (for example \.\DISPLAY1), the same id Core lists in Settings.</summary>
    private static string PrimaryDisplayId()
    {
        IntPtr monitor = MonitorFromPoint(0, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var info = new MonitorInfoEx { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfoEx>(), Device = string.Empty };
        Assert.True(GetMonitorInfoW(monitor, ref info));
        return info.Device;
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Chosen_tv_connected_at_launch_opens_console_mode_on_the_gaming_library()
    {
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(8);
        Seed(root, enabled: true, PrimaryDisplayId(), withoutTv: "minimized");

        using var app = new UiTestApplication(nameof(Chosen_tv_connected_at_launch_opens_console_mode_on_the_gaming_library), restartDataRoot: root);
        try
        {
            // Console mode: the exit button is on screen and the Gaming library (search box) is the visible workspace.
            UiWait.For(() => app.FindRawDescendant(app.MainWindow, e =>
                    (e.Properties.Name.ValueOrDefault ?? string.Empty) == "Exit Console mode") != null,
                "app opened in Console/TV mode", TimeSpan.FromSeconds(60));
            UiWait.ById(app.MainWindow, "GameTitles_Search_TextBox");
            app.CaptureSnapshot("tv-start-console");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Chosen_tv_connected_at_launch_opens_console_mode_on_the_gaming_library), ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Chosen_tv_absent_at_launch_follows_the_fallback_minimized()
    {
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(8);
        Seed(root, enabled: true, @"\\.\DISPLAY99", withoutTv: "minimized");

        using var app = new UiTestApplication(nameof(Chosen_tv_absent_at_launch_follows_the_fallback_minimized), restartDataRoot: root);
        try
        {
            UiWait.For(() =>
            {
                try { return app.MainWindow.Patterns.Window.Pattern.WindowVisualState.Value == WindowVisualState.Minimized; }
                catch (FlaUI.Core.Exceptions.FlaUIException) { return false; }
            }, "window minimized because the chosen TV is not connected", TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Chosen_tv_absent_at_launch_follows_the_fallback_minimized), ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Chosen_tv_absent_at_launch_with_the_normal_fallback_keeps_the_ai_window_visible()
    {
        UiTestApplication.WaitForCorePortReleased();
        string root = SyntheticGameLibrary.CreateRoot(8);
        Seed(root, enabled: true, @"\\.\DISPLAY99", withoutTv: "normal");

        using var app = new UiTestApplication(nameof(Chosen_tv_absent_at_launch_with_the_normal_fallback_keeps_the_ai_window_visible), restartDataRoot: root);
        try
        {
            UiWait.ById(app.MainWindow, "Chat_Send_Button");   // the usual AI (Chat) window
            Thread.Sleep(20_000);                              // longer than the whole TV probe window: it must not minimize or enter Console
            Assert.NotEqual(WindowVisualState.Minimized, app.MainWindow.Patterns.Window.Pattern.WindowVisualState.Value);
            Assert.Null(app.FindRawDescendant(app.MainWindow, e => (e.Properties.Name.ValueOrDefault ?? string.Empty) == "Exit Console mode"));
        }
        catch (Exception ex)
        {
            app.CaptureFailure(nameof(Chosen_tv_absent_at_launch_with_the_normal_fallback_keeps_the_ai_window_visible), ex);
            throw;
        }
    }
}
