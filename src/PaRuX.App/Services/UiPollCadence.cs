using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure policy for the always-on UI to Core status poll (background-work notifications and the running-task badges).
    /// It used to tick every 2 s no matter what. Now it is as fast as before while something can change (a code task is running, the
    /// chat is busy, a model download or runtime install is in flight) and slower only when nothing is happening; a hidden or minimized
    /// window skips UI refresh work but keeps polling, because tray notifications for finished downloads/installs depend on it.
    /// </summary>
    internal static class UiPollCadence
    {
        /// <summary>The original tick, kept whenever work is in flight.</summary>
        public static readonly TimeSpan Active = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan VisibleIdle = TimeSpan.FromSeconds(4);
        public static readonly TimeSpan HiddenIdle = TimeSpan.FromSeconds(6);

        public static TimeSpan Next(bool windowHidden, bool anyActivity)
        {
            if (anyActivity) return Active;
            return windowHidden ? HiddenIdle : VisibleIdle;
        }

        /// <summary>A model download that can still change state (and so can still finish).</summary>
        public static bool DownloadInFlight(string? status) => status is "queued" or "downloading" or "retrying";

        /// <summary>A runtime install that can still change stage.</summary>
        public static bool InstallInFlight(string? stage) =>
            stage is "queued" or "downloading" or "verifying" or "extracting" or "swapping" or "performance-check";

        /// <summary>Set by the main window; null in tests. Called on the UI thread by display-only timers.</summary>
        internal static Func<bool>? WindowHiddenProbe;

        /// <summary>True when the main window is minimized or hidden to the tray: display-only timers skip their tick.</summary>
        public static bool IsWindowHidden()
        {
            try { return WindowHiddenProbe?.Invoke() ?? false; }
            catch { return false; }
        }

        /// <summary>UI refresh work (badge updates) is pointless while the window is hidden; it resumes on activation.</summary>
        public static bool ShouldRefreshUi(bool windowHidden) => !windowHidden;
    }

    /// <summary>
    /// Shares one task-list answer between the five pollers that ask Core for it (four desktop workers and the badge timer). The answer
    /// is reused for <see cref="BusyTtl"/> while any task is non-terminal and for <see cref="IdleTtl"/> when none is, and is dropped at once
    /// by any call that changes tasks through this client. Pure time/state logic so it can be tested without HTTP.
    /// </summary>
    internal sealed class SharedSnapshotPolicy
    {
        public static readonly TimeSpan BusyTtl = TimeSpan.FromMilliseconds(1500);
        public static readonly TimeSpan IdleTtl = TimeSpan.FromSeconds(4);

        private DateTime _atUtc = DateTime.MinValue;
        private bool _anyNonTerminal;
        private bool _has;

        public void Record(DateTime nowUtc, bool anyNonTerminal) { _atUtc = nowUtc; _anyNonTerminal = anyNonTerminal; _has = true; }

        public void Invalidate() => _has = false;

        public bool IsFresh(DateTime nowUtc) =>
            _has && nowUtc - _atUtc < (_anyNonTerminal ? BusyTtl : IdleTtl);
    }
}
