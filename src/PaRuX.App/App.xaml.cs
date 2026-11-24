using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PaRuX.Services;
using PaRuX.Shell;

namespace PaRuX
{
    public partial class App : Application
    {
        public static MainWindow? MainWin { get; private set; }
        public static BubbleWindow? BubbleWin { get; private set; }
        public static HotkeyService Hotkey { get; } = new HotkeyService();

        /// <summary>Set only by the tray "Exit" path, so window close can mean "hide".</summary>
        public static bool IsShuttingDown { get; private set; }

        // Per data root (also in Release): a Corporate folder, a portable copy and an installed PaRuX each own their
        // own instance, so one edition no longer makes another exit silently. Computed in OnLaunched.
        private static Mutex? _singleInstanceMutex;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private const int SW_RESTORE = 9;

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        private static IDisposable? _exitSignal;

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // "PaRuX.exe --exit" closes the running instance that uses this data folder and returns.
            // Handled before anything else so it never starts a second UI, Core or model.
            try { _ = AppPaths.PaRuXRoot; }
            catch (PaRuX.Contracts.CorporateDataRootUnavailableException ex)
            {
                // Corporate must never fall back to %LOCALAPPDATA%: explain and stop.
                ReportStartupFailure(ex);
                Environment.Exit(1);
                return;
            }
            string exitSignalName = PaRuX.Contracts.AppExitSignal.NameFor(AppPaths.PaRuXRoot);
            if (PaRuX.Contracts.AppExitSignal.IsExitRequest(Environment.GetCommandLineArgs()))
            {
                bool delivered = PaRuX.Contracts.AppExitSignal.TrySignal(exitSignalName);
                Environment.Exit(delivered ? 0 : 2);
                return;
            }

            bool createdNew;
            string mutexName = PaRuX.Contracts.SingleInstanceGuard.AppMutexNameFor(AppPaths.PaRuXRoot);
#if DEBUG
            string? uiTestDataRoot = Environment.GetEnvironmentVariable("PARUX_DATA_ROOT");
            // PARUX_UI_TEST_ISOLATED is set for every FlaUI-launched instance (PARUX_UI_TESTS only marks
            // fixture runs), so a test never collides with a real or concurrently running PaRuX.
            if ((string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal)
                    || string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_ISOLATED"), "1", StringComparison.Ordinal))
                && !string.IsNullOrWhiteSpace(uiTestDataRoot))
            {
                string testScope = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(uiTestDataRoot))))[..16];
                mutexName += ".UITest." + testScope;
            }
#endif
            // "PaRuX.exe --play key" comes from a desktop or Steam shortcut for one library game.
            bool hasPlayRequest = PaRuX.Contracts.GameLaunchRequestChannel.TryParseCommandLine(Environment.GetCommandLineArgs(), out string playTitleKey);

            // "PaRuX.exe --playnite-console | --playnite-desktop | --streaming | --playnite-settings | --playnite-launcher": Playnite Console Mode.
            PaRuX.Contracts.PlayniteLauncherRequest launcherRequest = PaRuX.Contracts.PlayniteLauncherArguments.Parse(Environment.GetCommandLineArgs());

            _singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
            if (!createdNew)
            {
                Log.Write("PaRuX already running - activating existing instance and exiting.");
                if (launcherRequest.HasAction)
                {
                    // Only an explicit argument is forwarded (never a held key): the running instance opens its launcher or runs the action.
                    bool queued = PaRuX.Contracts.PlayniteLauncherRequestChannel.TrySubmit(AppPaths.PaRuXRoot, launcherRequest.Action, out _);
                    Log.Write("Playnite launcher request " + (queued ? "handed to the running instance." : "could not be queued."));
                }
                if (hasPlayRequest)
                {
                    // Hand the request to the running instance; it shows the normal Play confirmation.
                    bool queued = PaRuX.Contracts.GameLaunchRequestChannel.TrySubmit(AppPaths.PaRuXRoot, playTitleKey, out _);
                    Log.Write("Game shortcut request " + (queued ? "handed to the running instance." : "could not be queued."));
                }
                ActivateExistingInstance();
                Environment.Exit(0);
                return;
            }

            // Startup is different from an ordinary post-launch rendering exception. If any
            // prerequisite fails before MainWindow exists, swallowing the WinUI unhandled event
            // can leave an invisible process holding the single-instance mutex. Fail closed here:
            // preserve the full diagnostic and terminate instead of creating a zombie instance.
            // Start-up resolution input for this (first) instance: the explicit argument, else Ctrl/Shift held right now (read-only).
            StartupLauncherRequest = launcherRequest;
            if (!launcherRequest.HasAction && !hasPlayRequest)
            {
                (StartupCtrlHeld, StartupShiftHeld) = PaRuX.Services.LauncherModifierKeys.Sample(TimeSpan.Zero);
            }

            try
            {
                AppPaths.EnsureCreated();
                // Read the sidebar history on a worker while the window is built (consumed by the first RefreshHistory).
                HistoryStore.BeginPreload();
                SettingsStore.Current.Load();
                // English is the base language; Italian only when chosen in Settings (not from the Windows language).
                PaRuX.Services.UiLanguage.Apply(SettingsStore.Current.Data.UiLanguage);
                // Framework (WinUI) strings follow the same choice; must happen before the first window is created.
                PaRuX.Services.FrameworkResourceLanguage.Apply(SettingsStore.Current.Data.UiLanguage);
                // The setup profile (AI only / Gaming only / Both; Both for every existing install) decides what starts.
                SetupProfilePlan setup = SetupProfilePolicy.Plan(SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId));
                if (setup.StartLocalAi) LocalAiController.Current.StartWatching();
                if (setup.StartGaming) GamingManager.Current.Start();
                SetupProfileRuntime.MarkStartedBy(setup);
                Log.Write("PaRuX starting (setup profile: " + SetupProfilePolicy.ToStorage(SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId)) + ").");

                MainWin = new MainWindow();
                MainWin.Activate();
                Microsoft.UI.Dispatching.DispatcherQueue uiQueue = MainWin.DispatcherQueue;
                _exitSignal = PaRuX.Contracts.AppExitSignal.Listen(exitSignalName, () =>
                {
                    Log.Write("Exit requested through PaRuX.exe --exit.");
                    if (!uiQueue.TryEnqueue(ExitApplication)) Environment.Exit(0);
                });

                // Shortcut requests: later ones arrive through the event; one given on this launch (or left in the
                // queue by an earlier launch that could not reach us) is handled now. Stale requests are dropped.
                _playSignal = PaRuX.Contracts.GameLaunchRequestChannel.Listen(AppPaths.PaRuXRoot, () =>
                    uiQueue.TryEnqueue(ProcessGameShortcutRequests));
                if (hasPlayRequest) PaRuX.Contracts.GameLaunchRequestChannel.TryEnqueue(AppPaths.PaRuXRoot, playTitleKey);
                uiQueue.TryEnqueue(ProcessGameShortcutRequests);

                // Console Mode launcher requests from other PaRuX processes (shortcut, Jump List task, second launch).
                _launcherSignal = PaRuX.Contracts.PlayniteLauncherRequestChannel.Listen(AppPaths.PaRuXRoot, () =>
                    uiQueue.TryEnqueue(ProcessPlayniteLauncherRequests));

                ApplyTheme();

                // Console/TV start preference (opt-in): TV connected -> fullscreen Gaming library on it; otherwise the chosen
                // fallback (normal window or minimized). Skipped when this launch is a game shortcut. Runs after the first paint.
                // Then (opt-in, off by default) the after-sign-in app list, once per Windows sign-in, after the mode is settled.
                uiQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = MainWin.RunStartupSequenceAsync(hasPlayRequest));

                // Not needed for the first paint: the Quick Chat bubble window, the agent worker poll loops and the model
                // warmup start once the dispatcher has drained the shell's own first layout work (Low priority). Gaming and
                // Local AI watching (the hard-off safety path) still start above, before any window exists. Every user of
                // BubbleWin is null-safe and the workers only start polling.
                if (!uiQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FinishDeferredStartup))
                    FinishDeferredStartup();
            }
            catch (Exception ex)
            {
                Log.Write("FATAL STARTUP: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex);
                ReportStartupFailure(ex);
                try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
                try { _singleInstanceMutex?.Dispose(); } catch { }
                Environment.Exit(1);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        /// <summary>The data folder (and therefore Log) may be exactly what failed: write a fallback log to %TEMP% and
        /// show the reason before exiting, so a startup refusal is never silent.</summary>
        private static void ReportStartupFailure(Exception ex)
        {
            string? root = null;
            try { root = AppPaths.PaRuXRoot; } catch { }
            string message = PaRuX.Contracts.StartupFailureReport.BuildMessage(root, ex);
            PaRuX.Contracts.StartupFailureReport.TryWriteFallbackLog(Path.GetTempPath(), message, ex);
            try { MessageBox(IntPtr.Zero, message, "PaRuX", 0x00000010 /* MB_ICONERROR */ | 0x00010000 /* MB_SETFOREGROUND */); }
            catch { }
        }

        private static IDisposable? _playSignal;
        private static IDisposable? _launcherSignal;

        /// <summary>What this process was asked at start (argument) and which modifiers were held; read once by the start-up sequence.</summary>
        internal static PaRuX.Contracts.PlayniteLauncherRequest StartupLauncherRequest { get; private set; } = PaRuX.Contracts.PlayniteLauncherRequest.Nothing;
        internal static bool StartupCtrlHeld { get; private set; }
        internal static bool StartupShiftHeld { get; private set; }

        /// <summary>Takes queued launcher requests from other processes; the latest wins and opens the launcher (or runs its action) in this instance.</summary>
        private static void ProcessPlayniteLauncherRequests()
        {
            try
            {
                var actions = PaRuX.Contracts.PlayniteLauncherRequestChannel.Drain(AppPaths.PaRuXRoot);
                PaRuX.Contracts.PlayniteLauncherAction winner = PaRuX.Contracts.PlayniteLauncherRequestChannel.PickWinner(actions);
                if (winner == PaRuX.Contracts.PlayniteLauncherAction.None || MainWin == null) return;
                Log.Write("Playnite launcher request from another process: " + winner + ".");
                MainWin.ShowPlayniteLauncher(winner, gamepad: false, fromRequest: true);
            }
            catch (Exception ex)
            {
                Log.Write("ProcessPlayniteLauncherRequests failed: " + ex.Message);
            }
        }

        /// <summary>Non-first-paint startup work, run after the main window is shown (see OnLaunched).</summary>
        private static void FinishDeferredStartup()
        {
            try
            {
                BubbleWin = new BubbleWindow();
                BubbleWin.ApplyVisibilityMode();
                ApplyTheme();
            }
            catch (Exception ex)
            {
                Log.Write("Deferred Quick Chat window creation failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            try { MainWin.StartPadActions(); }
            catch (Exception ex) { Log.Write("PadActions start failed: " + ex.GetType().Name + ": " + ex.Message); }

            // Honour a setup-profile switch or an exit that happened before this ran: start only what is still wanted.
            if (IsShuttingDown) return;
            // Start the model load (fire-and-forget; OFF and Gaming modes are respected by Core).
            if (SetupProfileRuntime.Running.LocalAi) _ = WarmLocalAiAsync();
            if (!SetupProfileRuntime.Running.AgentWorkers) return;
            try
            {
                AgentCodingWorkerService.Current.Start();
                AgentBinaryAnalysisWorkerService.Current.Start();
                UiVerificationWorkerService.Current.Start();
                ComputerUseWorkerService.Current.Start();
            }
            catch (Exception ex)
            {
                Log.Write("Deferred agent worker start failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Takes the queued "--play" requests and opens the first one. One game at a time: starting several
        /// games from a burst of clicks would stack confirmation dialogs.</summary>
        private static void ProcessGameShortcutRequests()
        {
            try
            {
                System.Collections.Generic.IReadOnlyList<string> keys = PaRuX.Contracts.GameLaunchRequestChannel.Drain(AppPaths.PaRuXRoot);
                if (keys.Count == 0 || MainWin == null) return;
                if (keys.Count > 1) Log.Write("Several game shortcut requests arrived together; only the first is opened.");
                MainWin.OpenGameFromShortcut(keys[0]);
            }
            catch (Exception ex)
            {
                Log.Write("ProcessGameShortcutRequests failed: " + ex.Message);
            }
        }

        private static async Task WarmLocalAiAsync()
        {
            try
            {
                LocalAiController.ActionResult result = await LocalAiController.Current
                    .RunActionAsync("Warmup", CancellationToken.None).ConfigureAwait(false);
                Log.Write("Startup model warmup -> success=" + result.Success + " : " + result.Message);
            }
            catch (Exception ex)
            {
                Log.Write("Startup model warmup failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ActivateExistingInstance()
        {
            try
            {
                // New title first, then both legacy titles so a running pre-rebrand instance is still found after an upgrade.
                IntPtr hwnd = FindWindow(null, "RBG – PaRuX AI & Gaming OS");
                if (hwnd == IntPtr.Zero) hwnd = FindWindow(null, "PaRuX AI & Gaming OS");
                if (hwnd == IntPtr.Zero) hwnd = FindWindow(null, "RBG PaRuX AI Studio");
                if (hwnd == IntPtr.Zero) return;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            catch (Exception ex)
            {
                Log.Write("ActivateExistingInstance failed: " + ex.Message);
            }
        }

        public static ElementTheme CurrentElementTheme
        {
            get
            {
                string theme = SettingsStore.Current.Data.Theme;
                if (theme == "Light") return ElementTheme.Light;
                if (theme == "Dark") return ElementTheme.Dark;
                return ElementTheme.Default;
            }
        }

        public static void ApplyTheme()
        {
            ElementTheme theme = CurrentElementTheme;
            (string? accentId, string? accentHex) = PaRuX.Services.LibraryThemeState.EffectiveAccent(SettingsStore.Current.Data.AccentId, SettingsStore.Current.Data.AccentCustomHex);
            PaRuX.Services.AccentApplier.Apply(accentId, accentHex);

            if (MainWin != null)
            {
                FrameworkElement? root = MainWin?.Content as FrameworkElement;
                if (root != null) root.RequestedTheme = theme;
            }
            if (BubbleWin != null)
            {
                FrameworkElement? root = BubbleWin?.Content as FrameworkElement;
                if (root != null) root.RequestedTheme = theme;
            }
        }

        public static void ShowMainWindow()
        {
            if (MainWin == null) return;
            try
            {
                MainWin.AppWindow.Show();
                MainWin.Activate();
                MainWin.BringToFront();
            }
            catch (Exception ex)
            {
                Log.Write("ShowMainWindow failed: " + ex.Message);
            }
        }

        public static void ExitApplication()
        {
            IsShuttingDown = true;
            try
            {
                ChatSession.Current.Cancel();
                ChatSession.Current.Persist();
                Hotkey.Dispose();
                PadActionListener.Current.Stop();
                if (BubbleWin != null) BubbleWin.PersistPosition();
                if (MainWin != null) MainWin.DisposeTray();
            }
            catch (Exception ex)
            {
                Log.Write("Shutdown cleanup failed: " + ex.Message);
            }
            Log.Write("PaRuX exiting.");
            Current.Exit();
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Log.Write("UNHANDLED: " + e.Message + "\n" + (e.Exception == null ? "" : e.Exception.ToString()));
            // Post-launch UI exceptions are logged and kept recoverable where WinUI allows it.
            // Fatal bootstrap work is handled separately in OnLaunched and never swallowed here.
            e.Handled = true;
        }
    }
}
