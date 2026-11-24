using System.Runtime.InteropServices;

namespace PaRuX.Launcher;

internal static class Program
{
    private const uint MbOk = 0x0, MbIconError = 0x10, MbTopmost = 0x40000;

    private static int Main(string[] args)
    {
        string? self = Environment.ProcessPath;
        string directory = string.IsNullOrEmpty(self) ? AppContext.BaseDirectory : Path.GetDirectoryName(self) ?? AppContext.BaseDirectory;
        return LauncherCore.Run(directory, args, ShowError);
    }

    private static void ShowError(string message)
    {
        // Harnesses and unattended runs set PARUX_LAUNCHER_NO_UI=1 to get the text on stderr instead of a dialog.
        if (Environment.GetEnvironmentVariable("PARUX_LAUNCHER_NO_UI") == "1")
        {
            try { Console.Error.WriteLine(message); } catch (IOException) { }
            return;
        }
        MessageBoxW(IntPtr.Zero, message, "PaRuX", MbOk | MbIconError | MbTopmost);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
