using System;
using System.Collections.Generic;
using System.Text;

namespace PaRuX.Services
{
    public enum DictationRefusal
    {
        None,
        /// <summary>The person has not turned system dictation on.</summary>
        Disabled,
        /// <summary>Gaming Mode is active: local speech inference is off.</summary>
        Gaming,
        /// <summary>PaRuX itself is in front: its own composer already has a microphone button, so nothing is typed into PaRuX from here.</summary>
        OwnWindowActive,
        /// <summary>A dictation is already recording or being transcribed.</summary>
        Busy,
        /// <summary>No other window is in front, so there is nowhere to put the text.</summary>
        NoTarget
    }

    /// <summary>
    /// The rules of system-wide dictation, with no Windows API in them so they can be tested: when it may start, how the transcript is
    /// cleaned before it is typed, and when typing is refused because the target changed. Typing sends only characters of the transcript
    /// (Unicode text input), never key shortcuts, and the transcript has no control characters, so it cannot press Enter, Tab or a shortcut.
    /// </summary>
    public static class SystemDictationPolicy
    {
        public const int MaxInsertCharacters = 4000;
        public const int BatchCharacters = 100;
        public static readonly TimeSpan MaxRecording = TimeSpan.FromSeconds(60);

        public static DictationRefusal CanStart(bool enabled, bool gamingActive, bool ownWindowForeground, bool busy, bool hasForegroundTarget)
        {
            if (!enabled) return DictationRefusal.Disabled;
            if (gamingActive) return DictationRefusal.Gaming;
            if (busy) return DictationRefusal.Busy;
            if (!hasForegroundTarget) return DictationRefusal.NoTarget;
            if (ownWindowForeground) return DictationRefusal.OwnWindowActive;
            return DictationRefusal.None;
        }

        /// <summary>The text that will be typed: hesitation sounds optionally removed, every control character (including line breaks) replaced by a
        /// space so nothing can submit a form or run a shortcut, whitespace collapsed, bounded. Empty when nothing was heard.</summary>
        public static string PrepareText(string? transcript, bool removeFillers)
        {
            string text = DictationText.Prepare(transcript, removeFillers);
            if (text.Length == 0) return string.Empty;
            var sb = new StringBuilder(Math.Min(text.Length, MaxInsertCharacters));
            bool lastSpace = false;
            foreach (Rune rune in text.EnumerateRunes())
            {
                // A lone surrogate half enumerates as U+FFFD, noncharacters and the bidirectional override controls are never typed.
                int v = rune.Value;
                if (v is 0xFFFD or 0xFFFE or 0xFFFF || (v >= 0x202A && v <= 0x202E) || (v >= 0x2066 && v <= 0x2069)) continue;
                bool space = Rune.IsControl(rune) || Rune.IsWhiteSpace(rune);
                if (space) { if (lastSpace) continue; lastSpace = true; sb.Append(' '); }
                else { lastSpace = false; sb.Append(rune.ToString()); }
                if (sb.Length >= MaxInsertCharacters) break;
            }
            string result = sb.ToString().Trim();
            // A cut in the middle of a surrogate pair would type a broken character.
            if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result = result[..^1];
            return result;
        }

        /// <summary>Splits the text into batches of at most <paramref name="size"/> UTF-16 units that never separate a surrogate pair.</summary>
        public static IReadOnlyList<string> Batches(string text, int size = BatchCharacters)
        {
            var list = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                int take = Math.Min(size, text.Length - i);
                if (take < text.Length - i && char.IsHighSurrogate(text[i + take - 1])) take--;
                if (take <= 0) take = Math.Min(2, text.Length - i);
                list.Add(text.Substring(i, take));
                i += take;
            }
            return list;
        }

        /// <summary>Typing happens only while the window that was in front when the person started is still in front.</summary>
        public static bool TargetStillActive(long capturedWindow, long currentForegroundWindow) =>
            capturedWindow != 0 && capturedWindow == currentForegroundWindow;

        public static string Describe(DictationRefusal refusal) => refusal switch
        {
            DictationRefusal.Disabled => UiText.Pick("System dictation is off. Turn it on in Settings first.", "La dettatura di sistema è disattivata. Attivala prima nelle Impostazioni."),
            DictationRefusal.Gaming => UiText.Pick("Gaming Mode is active, so local speech recognition is off.", "La modalità Gaming è attiva, quindi il riconoscimento vocale locale è spento."),
            DictationRefusal.OwnWindowActive => UiText.Pick("PaRuX is in front. Use the microphone button in the chat box, or switch to the app you want to type into.", "PaRuX è in primo piano. Usa il pulsante del microfono nella chat, oppure passa all'app in cui vuoi scrivere."),
            DictationRefusal.Busy => UiText.Pick("A dictation is already in progress.", "Una dettatura è già in corso."),
            DictationRefusal.NoTarget => UiText.Pick("No window is in front to type into.", "Nessuna finestra in primo piano in cui scrivere."),
            _ => string.Empty
        };
    }
}
