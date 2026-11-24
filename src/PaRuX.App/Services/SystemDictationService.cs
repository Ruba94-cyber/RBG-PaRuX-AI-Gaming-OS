using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// Opt-in dictation outside the chat box. One registered key combination toggles it (press to start, press again or use Cancel on the
    /// indicator to stop); there is no keyboard hook and no key state is read. While recording a small always-on-top indicator is visible
    /// and never takes focus. The speech is transcribed by the local engine; the text is typed into the window that was in front when you
    /// started, as Unicode characters only (no shortcuts), and only if that window is still in front. If it is not, nothing is typed and
    /// the indicator says so. Refused during Gaming Mode and while PaRuX itself is in front.
    /// </summary>
    public sealed class SystemDictationService : IDisposable
    {
        private const int DictationHotkeyId = 0xA17F;
        public static SystemDictationService Current { get; } = new SystemDictationService();

        private readonly HotkeyService _hotkey = new HotkeyService(DictationHotkeyId);
        private readonly VoiceRecordingSession _session = new VoiceRecordingSession();
        private readonly object _gate = new object();
        private MicrophoneRecorder? _recorder;
        private CancellationTokenSource? _cts;
        private IntPtr _ownWindow;
        private IntPtr _target;
        private DictationIndicator? _indicator;
        private Microsoft.UI.Dispatching.DispatcherQueue? _ui;
        private Timer? _limit;

        private SystemDictationService() { }

        public bool IsRegistered => _hotkey.IsRegistered;

        /// <summary>Attaches to the main window handle once. Registers the combination only while the setting is on.</summary>
        public void Attach(IntPtr mainWindow, Microsoft.UI.Dispatching.DispatcherQueue ui)
        {
            _ownWindow = mainWindow;
            _ui = ui;
            _hotkey.Attach(mainWindow);
            _hotkey.Pressed += () => _ = ToggleAsync();
            Apply();
        }

        /// <summary>(Re)reads the setting: unregisters when off, registers the chosen combination when on. Returns false if Windows refused it.</summary>
        public bool Apply()
        {
            AppSettings s = SettingsStore.Current.Data;
            if (!s.SystemDictationEnabled) { _hotkey.Unregister(); return true; }
            return _hotkey.Register(s.SystemDictationHotkey);
        }

        private static bool GamingActive()
        {
            try { return File.Exists(AppPaths.GameMarkerFile); } catch (IOException) { return false; }
        }

        private async Task ToggleAsync()
        {
            if (_session.Phase == VoiceRecordingPhase.Recording) { await StopAndInsertAsync(); return; }
            AppSettings s = SettingsStore.Current.Data;
            IntPtr fg = GetForegroundWindow();
            DictationRefusal refusal = SystemDictationPolicy.CanStart(s.SystemDictationEnabled, GamingActive(),
                fg == _ownWindow || IsOwnProcess(fg), _session.Phase != VoiceRecordingPhase.Idle, fg != IntPtr.Zero);
            if (refusal != DictationRefusal.None) { Notice(SystemDictationPolicy.Describe(refusal), 5); return; }
            if (!_session.TryStart(DateTime.UtcNow)) return;
            _target = fg;
            _cts = new CancellationTokenSource();
            try
            {
                _recorder = new MicrophoneRecorder();
                await _recorder.StartAsync();
            }
            catch (VoiceCaptureException ex)
            {
                _session.Cancel();
                _recorder?.Dispose(); _recorder = null;
                Notice(ex.Message, 6);
                return;
            }
            ShowIndicator(UiText.Pick("Listening… press the shortcut again to type", "In ascolto… premi di nuovo la scorciatoia per scrivere"), cancelable: true);
            _limit?.Dispose();
            _limit = new Timer(_ => _ui?.TryEnqueue(() => { if (_session.Phase == VoiceRecordingPhase.Recording) _ = StopAndInsertAsync(); }),
                null, SystemDictationPolicy.MaxRecording, Timeout.InfiniteTimeSpan);
        }

        private async Task StopAndInsertAsync()
        {
            _limit?.Dispose(); _limit = null;
            if (_session.Stop(DateTime.UtcNow) != VoiceStopOutcome.Accepted)
            {
                await DiscardRecorderAsync();
                Notice(UiText.Pick("Too short, nothing was typed.", "Troppo breve, non è stato scritto nulla."), 3);
                return;
            }
            ShowIndicator(UiText.Pick("Transcribing locally…", "Trascrizione in locale…"), cancelable: true);
            string text = string.Empty;
            string? failure = null;
            try
            {
                byte[] wav = _recorder == null ? Array.Empty<byte>() : await _recorder.StopAsync();
                _recorder = null;
                var ct = _cts?.Token ?? CancellationToken.None;
                var response = await SpeechClient.Current.TranscribeAsync(Convert.ToBase64String(wav), "dictation.wav", ct);
                text = SystemDictationPolicy.PrepareText(response.Text, SettingsStore.Current.Data.DictationRemoveFillers);
            }
            catch (OperationCanceledException) { failure = UiText.Pick("Cancelled, nothing was typed.", "Annullato, non è stato scritto nulla."); }
            catch (Exception ex)
            {
                Log.Write("SystemDictation transcription failed: " + ex.GetType().Name);
                failure = UiText.Pick("Speech recognition did not answer, nothing was typed: ", "Il riconoscimento vocale non ha risposto, non è stato scritto nulla: ") + ex.Message;
            }
            finally { _session.Finish(); _cts?.Dispose(); _cts = null; }

            if (failure != null) { Notice(failure, 6); return; }
            if (text.Length == 0) { Notice(UiText.Pick("Nothing was heard, nothing was typed.", "Non si è sentito nulla, non è stato scritto nulla."), 4); return; }
            if (GamingActive()) { Notice(SystemDictationPolicy.Describe(DictationRefusal.Gaming), 5); return; }
            if (!SystemDictationPolicy.TargetStillActive(_target.ToInt64(), GetForegroundWindow().ToInt64()))
            {
                Notice(UiText.Pick("The active window changed, so nothing was typed. Switch back and dictate again.", "La finestra attiva è cambiata, quindi non è stato scritto nulla. Torna alla finestra e detta di nuovo."), 7);
                return;
            }
            int sent = TypeText(text);
            if (sent == 0) Notice(UiText.Pick("Windows did not accept the typed text (the target may run with higher rights).", "Windows non ha accettato il testo (la finestra potrebbe avere privilegi più alti)."), 7);
            else HideIndicator();
        }

        private async Task DiscardRecorderAsync()
        {
            try { if (_recorder != null) await _recorder.CancelAsync(); } catch (Exception) { }
            _recorder = null;
            _cts?.Dispose(); _cts = null;
        }

        /// <summary>Cancel from the indicator: discards the recording or the transcription, types nothing.</summary>
        private async Task CancelAsync()
        {
            _limit?.Dispose(); _limit = null;
            _cts?.Cancel();
            if (_session.Phase == VoiceRecordingPhase.Recording) { _session.Cancel(); await DiscardRecorderAsync(); }
            HideIndicator();
        }

        // ------------------------------------------------------------------ input (characters only)

        private static int TypeText(string text)
        {
            int total = 0;
            foreach (string batch in SystemDictationPolicy.Batches(text))
            {
                var inputs = new INPUT[batch.Length * 2];
                int n = 0;
                foreach (char c in batch)
                {
                    inputs[n++] = Key(c, KEYEVENTF_UNICODE);
                    inputs[n++] = Key(c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
                }
                uint done = SendInput((uint)n, inputs, Marshal.SizeOf<INPUT>());
                if (done == 0) return total;
                total += (int)done;
            }
            return total;
        }

        private static INPUT Key(char c, uint flags) => new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = flags } } };

        private static bool IsOwnProcess(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }

        // ------------------------------------------------------------------ indicator

        private void Notice(string message, int seconds)
        {
            ShowIndicator(message, cancelable: false);
            _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => _ui?.TryEnqueue(() =>
            {
                if (_session.Phase == VoiceRecordingPhase.Idle) HideIndicator();
            }));
        }

        private void ShowIndicator(string message, bool cancelable)
        {
            void Do()
            {
                _indicator ??= new DictationIndicator(() => _ = CancelAsync());
                _indicator.Show(message, cancelable);
            }
            if (_ui == null || _ui.HasThreadAccess) Do(); else _ui.TryEnqueue(Do);
        }

        private void HideIndicator()
        {
            void Do() => _indicator?.Hide();
            if (_ui == null || _ui.HasThreadAccess) Do(); else _ui.TryEnqueue(Do);
        }

        public void Dispose()
        {
            _limit?.Dispose();
            _cts?.Cancel();
            _hotkey.Dispose();
            _indicator?.Close();
        }

        /// <summary>A small always-on-top window shown whenever the microphone is in use for dictation. It is never activated, so the
        /// application you are typing into keeps the keyboard focus.</summary>
        private sealed class DictationIndicator
        {
            private readonly Window _window = new Window();
            private readonly TextBlock _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 8, 0) };
            private readonly Button _cancel = new Button { Content = UiText.Pick("Cancel", "Annulla"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            private bool _styled;

            public DictationIndicator(Action cancel)
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_text, "Dictation_Indicator_Text");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_cancel, "Dictation_Indicator_Cancel");
                _cancel.Click += (_, _) => cancel();
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Microsoft.UI.Colors.Red), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var grid = new Grid { Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(240, 32, 32, 32)) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _text.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                Grid.SetColumn(_text, 1); Grid.SetColumn(_cancel, 2);
                grid.Children.Add(dot); grid.Children.Add(_text); grid.Children.Add(_cancel);
                _window.Content = grid;
            }

            public void Show(string message, bool cancelable)
            {
                _text.Text = message;
                _cancel.Visibility = cancelable ? Visibility.Visible : Visibility.Collapsed;
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                AppWindow app = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
                if (!_styled)
                {
                    if (app.Presenter is OverlappedPresenter p) { p.IsAlwaysOnTop = true; p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.SetBorderAndTitleBar(true, false); }
                    app.IsShownInSwitchers = false;
                    long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
                    _styled = true;
                }
                Windows.Graphics.RectInt32 work = DisplayArea.Primary.WorkArea;
                int w = 460, h = 64;
                app.MoveAndResize(new Windows.Graphics.RectInt32(work.X + (work.Width - w) / 2, work.Y + work.Height - h - 48, w, h));
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            }

            public void Hide() => ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window), SW_HIDE);
            public void Close() { try { _window.Close(); } catch (Exception) { } }
        }

        // ------------------------------------------------------------------ Win32

        private const uint KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_KEYUP = 0x0002;
        private const int GWL_EXSTYLE = -20, SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        private const long WS_EX_NOACTIVATE = 0x08000000L, WS_EX_TOOLWINDOW = 0x00000080L;

        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }

        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    }
}
