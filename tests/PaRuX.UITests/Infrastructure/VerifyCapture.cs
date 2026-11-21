using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PaRuX.UITests.Infrastructure;

/// <summary>Verification-only window capture through PrintWindow(PW_RENDERFULLCONTENT): works when another top-level window
/// (for example a Windows Security prompt owned by something else) holds the foreground and covers the app.</summary>
internal static class VerifyCapture
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    internal static string Shot(UiTestApplication app, string name)
    {
        string dir = Path.Combine(Repository.Root, ".ai", "artifacts", "lazy-pages-20261121", "shots");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".png");
        IntPtr h = new(app.MainWindow.Properties.NativeWindowHandle.ValueOrDefault);
        IntPtr prev = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            GetWindowRect(h, out RECT r);
            using var bmp = new Bitmap(r.R - r.L, r.B - r.T);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(h, hdc, 2); } finally { g.ReleaseHdc(hdc); }
            }
            bmp.Save(path, ImageFormat.Png);
        }
        finally { if (prev != IntPtr.Zero) SetThreadDpiAwarenessContext(prev); }
        return path;
    }
}
