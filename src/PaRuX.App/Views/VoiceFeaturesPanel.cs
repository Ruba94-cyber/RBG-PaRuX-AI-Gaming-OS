using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Models;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// Two opt-in voice features, both local: the meeting assistant (record or import audio, local transcription, notes by the local
    /// model) and system-wide dictation. Each stays off until the person reads the explanation and switches it on.
    /// </summary>
    public sealed class VoiceFeaturesPanel : StackPanel
    {
        private static string T(string en, string it) => UiText.Pick(en, it);
        private const int ChunkSeconds = 45;
        private const long MaxImportBytes = 12L * 1024 * 1024;

        // Must match MeetingSettingsStore.RequiredConsentVersion in the Core, which refuses anything else.
        private const int ConsentVersion = 1;

        private readonly TextBlock _meetStatus = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _recordingBanner = new() { Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Visibility = Visibility.Collapsed };
        private readonly ToggleSwitch _meetEnabled = new() { Header = T("Meeting assistant", "Assistente riunioni") };
        private readonly TextBox _title = new() { PlaceholderText = T("Meeting title (optional)", "Titolo della riunione (facoltativo)"), MinWidth = 300 };
        private readonly TextBox _language = new() { PlaceholderText = T("Language code, e.g. en, it (empty = detect)", "Codice lingua, es. en, it (vuoto = rileva)"), MinWidth = 300 };
        private readonly CheckBox _toKnowledge = new() { Content = T("Add the notes to my knowledge sources when done", "Aggiungi le note alle mie fonti di conoscenza al termine") };
        private readonly Button _start = new() { Content = T("Start recording", "Avvia la registrazione") };
        private readonly Button _stop = new() { Content = T("Stop and write notes", "Ferma e scrivi le note"), Visibility = Visibility.Collapsed };
        private readonly Button _discard = new() { Content = T("Discard", "Scarta"), Visibility = Visibility.Collapsed };
        private readonly Button _import = new() { Content = T("Import an audio file", "Importa un file audio") };
        private readonly StackPanel _meetings = new() { Spacing = 6 };
        private readonly ToggleSwitch _dictation = new() { Header = T("System dictation", "Dettatura di sistema") };
        private readonly TextBox _dictationKey = new() { Header = T("Shortcut (toggle: press to start, press again to type)", "Scorciatoia (alterna: premi per iniziare, premi di nuovo per scrivere)"), MinWidth = 220 };
        private readonly TextBlock _dictationStatus = new() { TextWrapping = TextWrapping.Wrap };

        private string? _meetingId;
        private MicrophoneRecorder? _recorder;
        private CancellationTokenSource? _loopCts;
        private Task? _loop;
        private bool _loading = true;

        public VoiceFeaturesPanel()
        {
            Spacing = 10;
            AutomationProperties.SetAutomationId(_meetEnabled, "Meetings_Enable_Switch");
            AutomationProperties.SetAutomationId(_start, "Meetings_Start_Button");
            AutomationProperties.SetAutomationId(_stop, "Meetings_Stop_Button");
            AutomationProperties.SetAutomationId(_discard, "Meetings_Discard_Button");
            AutomationProperties.SetAutomationId(_import, "Meetings_Import_Button");
            AutomationProperties.SetAutomationId(_meetStatus, "Meetings_Status");
            AutomationProperties.SetAutomationId(_recordingBanner, "Meetings_Recording_Indicator");
            AutomationProperties.SetAutomationId(_dictation, "Dictation_Enable_Switch");
            AutomationProperties.SetAutomationId(_dictationKey, "Dictation_Hotkey_Text");
            AutomationProperties.SetAutomationId(_dictationStatus, "Dictation_Status");
            AutomationProperties.SetLiveSetting(_meetStatus, AutomationLiveSetting.Polite);

            Children.Add(Heading(T("Meeting assistant", "Assistente riunioni")));
            Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
                Text = T("Records your microphone in short pieces, transcribes them with the speech engine on this PC, and asks the local model for a summary, decisions and action items. Audio is never saved (only the text), nothing is sent over the Internet, and it stops during Gaming Mode. Only record when everyone in the meeting knows and agrees. It is off until you switch it on.",
                    "Registra il microfono a brevi pezzi, li trascrive con il motore vocale di questo PC e chiede al modello locale riassunto, decisioni e azioni da fare. L'audio non viene mai salvato (solo il testo), nulla viene inviato via Internet e si ferma durante la modalità Gaming. Registra solo se tutti i partecipanti lo sanno e sono d'accordo. Resta spento finché non lo attivi.")
            });
            Children.Add(_meetEnabled);
            Children.Add(_title); Children.Add(_language); Children.Add(_toKnowledge);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(_start); row.Children.Add(_stop); row.Children.Add(_discard); row.Children.Add(_import);
            Children.Add(row);
            Children.Add(_recordingBanner);
            Children.Add(_meetStatus);
            Children.Add(_meetings);

            Children.Add(Heading(T("System dictation", "Dettatura di sistema")));
            Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
                Text = T("Speak into any app: press the shortcut, talk, press it again, and the text is typed into the window that was active. Only one shortcut is registered with Windows; PaRuX does not watch your keyboard. A red indicator is visible while the microphone is on, speech is recognised on this PC, and text is typed only if the same window is still in front. It is off until you switch it on and does nothing during Gaming Mode.",
                    "Parla in qualsiasi app: premi la scorciatoia, parla, premila di nuovo e il testo viene scritto nella finestra che era attiva. Windows registra una sola scorciatoia; PaRuX non osserva la tua tastiera. Un indicatore rosso è visibile mentre il microfono è acceso, il parlato viene riconosciuto su questo PC e il testo viene scritto solo se la stessa finestra è ancora in primo piano. Resta spenta finché non la attivi e non fa nulla durante la modalità Gaming.")
            });
            Children.Add(_dictation);
            Children.Add(_dictationKey);
            Children.Add(_dictationStatus);

            _meetEnabled.Toggled += async (_, _) => { if (!_loading) await SetMeetingEnabledAsync(_meetEnabled.IsOn); };
            _start.Click += async (_, _) => await StartAsync();
            _stop.Click += async (_, _) => await StopAsync(finish: true);
            _discard.Click += async (_, _) => await StopAsync(finish: false);
            _import.Click += async (_, _) => await ImportAsync();
            _dictation.Toggled += async (_, _) => { if (!_loading) await SetDictationAsync(_dictation.IsOn); };
            _dictationKey.LostFocus += (_, _) => { if (!_loading && _dictation.IsOn) _ = SetDictationAsync(true); };
            Loaded += async (_, _) => await RefreshAsync();
            Unloaded += (_, _) => _ = StopAsync(finish: false);
        }

        private static TextBlock Heading(string t) => new() { Text = t, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        private XamlRoot? Root => XamlRoot;

        // ------------------------------------------------------------------ state

        private async Task RefreshAsync()
        {
            _loading = true;
            try
            {
                AppSettings s = SettingsStore.Current.Data;
                _dictation.IsOn = s.SystemDictationEnabled;
                _dictationKey.Text = s.SystemDictationHotkey;
                _dictationStatus.Text = s.SystemDictationEnabled
                    ? (SystemDictationService.Current.IsRegistered ? T("Ready: ", "Pronta: ") + s.SystemDictationHotkey : T("The shortcut could not be registered (another app may own it). Pick another.", "Impossibile registrare la scorciatoia (potrebbe essere usata da un'altra app). Scegline un'altra."))
                    : string.Empty;
                JsonElement st = await CoreJson.SendAsync(HttpMethod.Get, "/api/v1/meetings/settings", null, CancellationToken.None);
                _meetEnabled.IsOn = CoreJson.Bool(st, "enabled");
                await ListAsync();
            }
            catch (Exception ex) { Log.Write("VoiceFeaturesPanel refresh failed: " + ex.Message); _meetStatus.Text = T("Could not read the meeting settings: ", "Impossibile leggere le impostazioni delle riunioni: ") + ex.Message; }
            finally { _loading = false; }
        }

        private async Task SetMeetingEnabledAsync(bool on)
        {
            try
            {
                if (on)
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = Root, Title = T("Turn on the meeting assistant?", "Attivare l'assistente riunioni?"),
                        Content = new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = T("PaRuX will record your microphone while you press Start, turn the speech into text on this PC and keep that text (not the audio) in your Documents. Recording other people without their knowledge can be illegal; only use it when everyone agrees. You can stop, discard or delete a meeting at any time.",
                                "PaRuX registrerà il tuo microfono mentre premi Avvia, trasformerà il parlato in testo su questo PC e conserverà quel testo (non l'audio) nei tuoi Documenti. Registrare altre persone a loro insaputa può essere illegale; usalo solo se tutti sono d'accordo. Puoi fermare, scartare o eliminare una riunione in qualsiasi momento.")
                        },
                        PrimaryButtonText = T("I understand and agree", "Ho capito e accetto"), CloseButtonText = T("Cancel", "Annulla"), DefaultButton = ContentDialogButton.Close
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) { await RefreshAsync(); return; }
                }
                await CoreJson.SendAsync(HttpMethod.Put, "/api/v1/meetings/settings", new { enabled = on, consentVersion = on ? ConsentVersion : 0 }, CancellationToken.None);
                _meetStatus.Text = on ? T("Meeting assistant is on.", "L'assistente riunioni è attivo.") : T("Meeting assistant is off.", "L'assistente riunioni è spento.");
                if (!on) await StopAsync(finish: false);
            }
            catch (Exception ex) { _meetStatus.Text = T("Could not change it: ", "Impossibile cambiare: ") + ex.Message; await RefreshAsync(); }
        }

        private async Task SetDictationAsync(bool on)
        {
            try
            {
                AppSettings s = SettingsStore.Current.Data;
                if (on && !s.SystemDictationEnabled)
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = Root, Title = T("Turn on system dictation?", "Attivare la dettatura di sistema?"),
                        Content = new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = T("One keyboard shortcut will start and stop dictation in any app. While it listens, a red indicator is shown. The text is typed into the window you were using. PaRuX does not record or log what you type.",
                                "Una scorciatoia da tastiera avvierà e fermerà la dettatura in qualsiasi app. Mentre ascolta, viene mostrato un indicatore rosso. Il testo viene scritto nella finestra che stavi usando. PaRuX non registra né salva ciò che digiti.")
                        },
                        PrimaryButtonText = T("Turn on", "Attiva"), CloseButtonText = T("Cancel", "Annulla"), DefaultButton = ContentDialogButton.Close
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) { await RefreshAsync(); return; }
                }
                string combo = (_dictationKey.Text ?? string.Empty).Trim();
                if (on && !HotkeyService.TryParse(combo, out _, out _)) { _dictationStatus.Text = T("That shortcut is not valid. Use modifiers plus a key, for example Ctrl+Alt+D.", "Scorciatoia non valida. Usa modificatori più un tasto, per esempio Ctrl+Alt+D."); await RefreshAsync(); return; }
                s.SystemDictationEnabled = on;
                if (on) { s.SystemDictationHotkey = combo; s.SystemDictationAcceptedUtc = DateTime.UtcNow.ToString("O"); } else s.SystemDictationAcceptedUtc = string.Empty;
                SettingsStore.Current.Save();
                bool ok = SystemDictationService.Current.Apply();
                _dictationStatus.Text = !on ? T("System dictation is off.", "La dettatura di sistema è spenta.")
                    : ok ? T("Ready: ", "Pronta: ") + combo : T("The shortcut could not be registered (another app may own it). Pick another.", "Impossibile registrare la scorciatoia (potrebbe essere usata da un'altra app). Scegline un'altra.");
            }
            catch (Exception ex) { _dictationStatus.Text = T("Could not change it: ", "Impossibile cambiare: ") + ex.Message; }
        }

        // ------------------------------------------------------------------ recording

        private async Task StartAsync()
        {
            if (_meetingId != null) return;
            try
            {
                JsonElement m = await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/meetings",
                    new { title = _title.Text?.Trim(), language = string.IsNullOrWhiteSpace(_language.Text) ? null : _language.Text.Trim() }, CancellationToken.None);
                _meetingId = CoreJson.Str(m, "id");
                _recorder = new MicrophoneRecorder();
                await _recorder.StartAsync();
            }
            catch (CoreRequestException ex) { _meetStatus.Text = ex.Message; await DropAsync(); return; }
            catch (VoiceCaptureException ex) { _meetStatus.Text = ex.Message; await DropAsync(); return; }
            catch (Exception ex) { _meetStatus.Text = T("Could not start: ", "Impossibile avviare: ") + ex.Message; await DropAsync(); return; }
            _start.Visibility = Visibility.Collapsed; _import.IsEnabled = false;
            _stop.Visibility = _discard.Visibility = _recordingBanner.Visibility = Visibility.Visible;
            _recordingBanner.Text = T("● Recording the meeting (microphone is on)", "● Registrazione della riunione (il microfono è acceso)");
            _loopCts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_meetingId!, _loopCts.Token));
        }

        /// <summary>Records fixed pieces, sends each for transcription and reports any failure; the recorder is restarted for each piece.</summary>
        private async Task LoopAsync(string id, CancellationToken ct)
        {
            DateTime started = DateTime.UtcNow;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(ChunkSeconds), ct);
                    await SendPieceAsync(id, started, restart: true, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Write("Meeting loop stopped: " + ex.GetType().Name);
                DispatcherQueue.TryEnqueue(() => { _meetStatus.Text = T("Recording stopped: ", "Registrazione interrotta: ") + ex.Message; _recordingBanner.Text = T("● Stopped", "● Interrotta"); });
            }
        }

        private async Task SendPieceAsync(string id, DateTime started, bool restart, CancellationToken ct)
        {
            MicrophoneRecorder? recorder = _recorder;
            if (recorder == null) return;
            byte[] wav = await recorder.StopAsync();
            _recorder = null;
            if (restart) { _recorder = new MicrophoneRecorder(); await _recorder.StartAsync(); }
            if (wav.Length < 2000) return; // nothing worth transcribing
            double offset = (DateTime.UtcNow - started).TotalSeconds - ChunkSeconds;
            try
            {
                JsonElement info = await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/meetings/" + id + "/chunks",
                    new { audioBase64 = Convert.ToBase64String(wav), fileName = "chunk.wav", offsetSeconds = Math.Max(0, offset) }, ct);
                DispatcherQueue.TryEnqueue(() => _meetStatus.Text = T("Transcribed pieces: ", "Pezzi trascritti: ") + CoreJson.Int(info, "chunks") + " · " + CoreJson.Int(info, "characters") + T(" characters", " caratteri"));
            }
            catch (CoreRequestException ex) when (ex.Code == "gaming_active" || ex.Code == "too_long")
            {
                _loopCts?.Cancel();
                DispatcherQueue.TryEnqueue(() => { _meetStatus.Text = ex.Message; _recordingBanner.Text = T("● Stopped; press Stop to save what was transcribed", "● Interrotta; premi Ferma per salvare ciò che è stato trascritto"); });
            }
            catch (CoreRequestException ex)
            {
                // One piece failed (for example the speech engine is not installed): say so, keep recording what can still be saved.
                DispatcherQueue.TryEnqueue(() => _meetStatus.Text = T("A piece could not be transcribed: ", "Un pezzo non è stato trascritto: ") + ex.Message);
            }
        }

        private async Task StopAsync(bool finish)
        {
            string? id = _meetingId;
            if (id == null) return;
            _meetingId = null;
            _loopCts?.Cancel();
            try { if (_loop != null) await _loop; } catch (Exception) { }
            _loop = null;
            try
            {
                if (finish && _recorder != null) await SendPieceAsync(id, DateTime.UtcNow.AddSeconds(-1), restart: false, CancellationToken.None);
                else if (_recorder != null) await _recorder.CancelAsync();
            }
            catch (Exception ex) { Log.Write("Meeting final piece failed: " + ex.GetType().Name); }
            _recorder?.Dispose(); _recorder = null;
            _stop.Visibility = _discard.Visibility = _recordingBanner.Visibility = Visibility.Collapsed;
            _start.Visibility = Visibility.Visible; _import.IsEnabled = true;
            try
            {
                if (finish)
                {
                    _meetStatus.Text = T("Writing the notes with the local model…", "Scrittura delle note con il modello locale…");
                    JsonElement done = await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/meetings/" + id + "/finish", new { addToKnowledge = _toKnowledge.IsChecked == true }, CancellationToken.None);
                    _meetStatus.Text = DescribeNotes(done);
                }
                else
                {
                    await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/meetings/" + id + "/cancel", null, CancellationToken.None);
                    _meetStatus.Text = T("Discarded. Nothing was kept.", "Scartata. Non è stato conservato nulla.");
                }
            }
            catch (Exception ex) { _meetStatus.Text = T("Could not finish: ", "Impossibile concludere: ") + ex.Message; }
            await ListAsync();
        }

        private async Task DropAsync()
        {
            string? id = _meetingId; _meetingId = null;
            try { if (_recorder != null) await _recorder.CancelAsync(); } catch (Exception) { }
            _recorder?.Dispose(); _recorder = null;
            if (id != null) { try { await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/meetings/" + id + "/cancel", null, CancellationToken.None); } catch (Exception) { } }
        }

        private static string DescribeNotes(JsonElement m)
        {
            string state = CoreJson.Str(m, "notesState"), reason = CoreJson.Str(m, "notesReason");
            return state switch
            {
                "written" => T("Done: notes and transcript saved.", "Fatto: note e trascrizione salvate."),
                "none" when reason == "empty_transcript" => T("Nothing was transcribed, so there are no notes.", "Non è stato trascritto nulla, quindi non ci sono note."),
                _ => T("Transcript saved, but the notes could not be written (", "Trascrizione salvata, ma le note non sono state scritte (") + reason + ")."
            };
        }

        // ------------------------------------------------------------------ import and list

        private async Task ImportAsync()
        {
            try
            {
                var picker = new FileOpenPicker();
                foreach (string ext in new[] { ".wav", ".mp3", ".m4a", ".ogg", ".flac" }) picker.FileTypeFilter.Add(ext);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                long size = new FileInfo(file.Path).Length;
                if (size > MaxImportBytes) { _meetStatus.Text = T("That file is larger than 12 MB. Cut it into shorter parts first.", "Il file supera i 12 MB. Dividilo prima in parti più brevi."); return; }
                _meetStatus.Text = T("Transcribing the file locally…", "Trascrizione locale del file…");
                JsonElement m = await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/meetings",
                    new { title = string.IsNullOrWhiteSpace(_title.Text) ? Path.GetFileNameWithoutExtension(file.Path) : _title.Text.Trim(), language = string.IsNullOrWhiteSpace(_language.Text) ? null : _language.Text.Trim() }, CancellationToken.None);
                string id = CoreJson.Str(m, "id");
                try
                {
                    byte[] bytes = await File.ReadAllBytesAsync(file.Path);
                    await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/meetings/" + id + "/chunks", new { audioBase64 = Convert.ToBase64String(bytes), fileName = file.Name, offsetSeconds = 0 }, CancellationToken.None);
                    JsonElement done = await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/meetings/" + id + "/finish", new { addToKnowledge = _toKnowledge.IsChecked == true }, CancellationToken.None);
                    _meetStatus.Text = DescribeNotes(done);
                }
                catch (Exception)
                {
                    try { await CoreJson.SendAsync(HttpMethod.Post, "/api/v1/meetings/" + id + "/cancel", null, CancellationToken.None); } catch (Exception) { }
                    throw;
                }
                await ListAsync();
            }
            catch (CoreRequestException ex) { _meetStatus.Text = ex.Message; }
            catch (Exception ex) { _meetStatus.Text = T("Could not import: ", "Impossibile importare: ") + ex.Message; }
        }

        private async Task ListAsync()
        {
            JsonElement list = await CoreJson.SendAsync(HttpMethod.Get, "/api/v1/meetings", null, CancellationToken.None);
            _meetings.Children.Clear();
            if (list.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement m in list.EnumerateArray().Where(x => CoreJson.Str(x, "state") != "recording"))
            {
                string id = CoreJson.Str(m, "id");
                var text = new TextBlock { Text = CoreJson.Str(m, "title") + " · " + CoreJson.Str(m, "notesState") + " · " + CoreJson.Int(m, "characters") + T(" chars", " car."), VerticalAlignment = VerticalAlignment.Center };
                var open = new Button { Content = T("Show", "Mostra") };
                var del = new Button { Content = T("Delete", "Elimina") };
                var body = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed, FontSize = 12 };
                open.Click += async (_, _) =>
                {
                    try
                    {
                        JsonElement r = await CoreJson.SendAsync(HttpMethod.Get, "/api/v1/meetings/" + id + "/notes", null, CancellationToken.None);
                        string notes = CoreJson.Str(r, "notes"), transcript = CoreJson.Str(r, "transcript");
                        body.Text = (notes.Length > 0 ? notes + "\n\n" : string.Empty) + transcript;
                        body.Visibility = body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                    }
                    catch (Exception ex) { _meetStatus.Text = ex.Message; }
                };
                del.Click += async (_, _) =>
                {
                    try { await CoreJson.SendAsync(HttpMethod.Delete, "/api/v1/meetings/" + id, null, CancellationToken.None); await ListAsync(); }
                    catch (Exception ex) { _meetStatus.Text = ex.Message; }
                };
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                line.Children.Add(text); line.Children.Add(open); line.Children.Add(del);
                _meetings.Children.Add(line);
                _meetings.Children.Add(body);
            }
        }
    }
}
