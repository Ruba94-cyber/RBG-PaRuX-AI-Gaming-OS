using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using H.NotifyIcon.Core;
using Microsoft.UI.Windowing;
using PaRuX.Contracts;
using PaRuX.Services;
using WinRT.Interop;

namespace PaRuX;

/// <summary>
/// Runs the PaRuX actions bound to controller buttons (Settings > Controller buttons). The pad is read by <see cref="PadActionListener"/>;
/// this partial only decides (through <see cref="PadActionPolicy"/>, the Gaming hard-off rules) and executes on the UI thread. Nothing is
/// ever injected into a game, a game is never closed or suspended by "Open PaRuX menu", and a refusal shows a short tray balloon.
/// </summary>
public sealed partial class MainWindow
{
    private bool _padActionsStarted;
    private bool _padActionBusy;

    internal void StartPadActions()
    {
        if (_padActionsStarted) return;
        _padActionsStarted = true;
        PadActionListener.Current.ActionFired += OnPadActionFired;
        PadActionListener.Current.Start(_dispatcher);
    }

    private static bool PadItalian => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    private static string PadT(string en, string it) => PadItalian ? it : en;

    private void PadToast(string message)
    {
        if (_trayDisposed) return;
        try { TrayIcon.ShowNotification("PaRuX", message, NotificationIcon.Info); }
        catch (Exception ex) { Log.Write("PadActions: notification failed: " + ex.GetType().Name); }
    }

    private void OnPadActionFired(PadButtonBinding binding, string device)
    {
        if (App.IsShuttingDown || _padActionBusy) return;   // one action at a time: a press during a running action is dropped
        _ = RunPadActionAsync(binding.ActionId);
    }

    internal async Task RunPadActionAsync(PadActionId action)
    {
        _padActionBusy = true;
        try
        {
            var ctx = new PadActionContext(GamingManager.Current.IsActive, _consoleModeActive, ConsoleOnGamingWorkspace());
            PadActionDecision decision = PadActionPolicy.Decide(action, ctx);
            if (!decision.Allowed)
            {
                PadToast(decision.Reason(PadItalian));
                return;
            }
            switch (action)
            {
                case PadActionId.OpenMenu:
                    PadBringForward();
                    if (_consoleModeActive && !ConsoleOnGamingWorkspace()) SwitchConsoleWorkspace();   // TV mode: home is the Gaming library
                    break;
                case PadActionId.GamingLibrary:
                    PadBringForward();
                    if (_consoleModeActive) { if (!ConsoleOnGamingWorkspace()) SwitchConsoleWorkspace(); }
                    else
                    {
                        ShowStudioWorkspace(StudioWorkspace.Games);
                        // Set the lightweight Tools surface BEFORE the page's
                        // on-shown refresh so no native catalogue scan is queued.
                        GameLibraryPage.OpenToolsFromController();
                        GameLibraryPage.OnShown();
                    }
                    break;
                case PadActionId.SwitchWorkspace:
                    PadBringForward();
                    if (_consoleModeActive) SwitchConsoleWorkspace();
                    else if (ConsoleOnGamingWorkspace()) ShowChat();
                    else
                    {
                        ShowStudioWorkspace(StudioWorkspace.Games);
                        // Set the lightweight Tools surface BEFORE the page's
                        // on-shown refresh so no native catalogue scan is queued.
                        GameLibraryPage.OpenToolsFromController();
                        GameLibraryPage.OnShown();
                    }
                    break;
                case PadActionId.MiniAgent:
                    if (App.BubbleWin is { } bubble) bubble.ShowExpanded();
                    else PadToast(PadT("The mini agent is not ready yet.", "Il mini agente non è ancora pronto."));
                    break;
                case PadActionId.Dictation:
                    PadBringForward();
                    ShowChat();
                    ChatPage.ToggleDictationFromPad();   // refuses itself while Gaming is active
                    break;
                case PadActionId.Screenshot:
                    await PadScreenshotAsync();
                    break;
                case PadActionId.PauseResumeGame:
                    await PadPauseResumeAsync();
                    break;
                case PadActionId.LaunchPlayniteConsole:
                    // PaRuX owns the button binding and opens its controller-friendly
                    // launcher; Playnite remains the optional games frontend.
                    ShowPlayniteLauncher(PlayniteLauncherAction.Console, gamepad: true, fromRequest: true);
                    break;
                case PadActionId.LaunchPlayniteDesktop:
                    ShowPlayniteLauncher(PlayniteLauncherAction.Desktop, gamepad: true, fromRequest: true);
                    break;
                case PadActionId.SwitchPlaynite:
                    await PadSwitchPlayniteAsync();
                    break;
                case PadActionId.OpenSettings:
                    ShowPadControllerSettings();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write("PadActions: " + action + " failed: " + ex.GetType().Name + ": " + ex.Message);
            PadToast(PadT("The controller action failed.", "L'azione del controller non è riuscita."));
        }
        finally { _padActionBusy = false; }
    }

    /// <summary>One shared entry for explicit controller settings requests from Games tools and pad actions.</summary>
    internal void ShowPadControllerSettings()
    {
        PadBringForward();
        ShowSettings();
        SettingsPage.ShowPadButtons();
    }

    // ---- bring PaRuX forward (only ever called because the owner pressed the bound button) --------------------------------------
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

    private void PadBringForward()
    {
        App.ShowMainWindow();
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        if (GetForegroundWindow() != hwnd) SwitchToThisWindow(hwnd, true);
        if (GetForegroundWindow() != hwnd)
            PadToast(PadT("Windows kept the focus on the game; PaRuX is ready in the taskbar.", "Windows ha lasciato il focus al gioco; PaRuX è pronto nella barra delle applicazioni."));
    }

    // ---- pause / resume through the existing owner-only Core API (only a game PaRuX launched) -----------------------------------
    private async Task PadPauseResumeAsync()
    {
        var client = GamingOrchestratorClient.Current;
        GamingStatusDto? status = await client.GetStatusAsync();
        bool suspended = string.Equals(status?.State, "GameSuspended", StringComparison.Ordinal);
        var result = suspended ? await client.ResumeGameAsync() : await client.SuspendGameAsync();
        if (result.Error is { } error) PadToast(error);
        else PadToast(suspended ? PadT("Game resumed.", "Gioco ripreso.") : PadT("Game paused.", "Gioco in pausa."));
    }

    // ---- screenshot of the focused window into a PaRuX-managed folder (GDI copy of what is on screen; nothing is injected or hooked) ----
    [StructLayout(LayoutKind.Sequential)] private struct PadRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PadBitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage;
        public int XPels, YPels; public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out PadRect r);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[]? bits, ref PadBitmapInfoHeader bi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    private async Task PadScreenshotAsync()
    {
        string folder = Path.Combine(AppPaths.PaRuXRoot, "Screenshots");
        if (!PadActionPolicy.ScreenshotFolderHasRoom(folder))
        {
            PadToast(PadT("The screenshot folder is full; clean it up first.", "La cartella degli screenshot è piena; svuotala prima."));
            return;
        }
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || IsIconic(hwnd) || !GetWindowRect(hwnd, out PadRect r)) { PadToast(PadT("No window to capture.", "Nessuna finestra da catturare.")); return; }
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w < 16 || h < 16 || w > 16384 || h > 16384) { PadToast(PadT("No window to capture.", "Nessuna finestra da catturare.")); return; }

        byte[] pixels = new byte[checked(w * h * 4)];
        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, w, h);
            IntPtr old = SelectObject(mem, bmp);
            bool ok = BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020 /* SRCCOPY */ | 0x40000000 /* CAPTUREBLT */);
            SelectObject(mem, old);
            var bi = new PadBitmapInfoHeader { Size = (uint)Marshal.SizeOf<PadBitmapInfoHeader>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            if (!ok || GetDIBits(mem, bmp, 0, (uint)h, pixels, ref bi, 0) == 0) { PadToast(PadT("The capture failed.", "La cattura non è riuscita.")); return; }
        }
        finally
        {
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;   // GDI leaves alpha undefined

        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, PadActionPolicy.ScreenshotFileName(DateTime.Now));
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            using var ras = stream.AsRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, ras);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                (uint)w, (uint)h, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        PadToast(PadT("Screenshot saved: ", "Screenshot salvato: ") + Path.GetFileName(path));
    }
}
