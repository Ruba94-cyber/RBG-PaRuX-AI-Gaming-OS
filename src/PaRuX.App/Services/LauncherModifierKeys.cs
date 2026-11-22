using System;
using System.Runtime.InteropServices;
using System.Threading;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Reads whether Ctrl or Shift is physically held at start (read-only, nothing is injected). The decision itself is the pure
    /// <see cref="PlayniteLauncherModeResolver"/>; this only supplies the key state and a real clock/sleep.
    /// </summary>
    public static class LauncherModifierKeys
    {
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        private const int VkShift = 0x10, VkControl = 0x11;

        public static (bool Ctrl, bool Shift) Now() =>
            ((GetAsyncKeyState(VkControl) & 0x8000) != 0, (GetAsyncKeyState(VkShift) & 0x8000) != 0);

        /// <summary>First sample plus at most <paramref name="grace"/> of polling (a key pressed with the click can arrive a moment later).</summary>
        public static (bool Ctrl, bool Shift) Sample(TimeSpan grace) =>
            PlayniteLauncherModeResolver.Sample(Now(), Now, Thread.Sleep, () => DateTime.UtcNow, grace, TimeSpan.FromMilliseconds(20));
    }
}
