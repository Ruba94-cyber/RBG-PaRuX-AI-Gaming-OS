using System;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Optional clean-up of a dictation transcript before it lands in the composer: removes spoken hesitation sounds
    /// ("um", "uh", "er", "hmm", "ehm") and nothing else, so no word of substance is ever changed. Off by default.
    /// </summary>
    public static class DictationText
    {
        // Whole hesitation sounds only (never part of a longer word), with the comma that usually follows them.
        private static readonly Regex Filler = new(
            @"(?<![\p{L}\p{N}'’-])(?:u+m+|u+h+m*|e+r+m*|e+h+m+|h+m+)(?![\p{L}\p{N}'’-])[,;]?[ \t]*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        private static readonly Regex SpaceBeforePunctuation = new(@"[ \t]+([,.;:!?])", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        private static readonly Regex CommaBeforeStop = new(@",[ \t]*([.!?])", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        private static readonly Regex RepeatedSpaces = new(@"[ \t]{2,}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        /// <summary>Returns <paramref name="text"/> without hesitation sounds. Null or blank input comes back as an empty string.</summary>
        public static string RemoveFillers(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            try
            {
                string original = text.Trim();
                string cleaned = Filler.Replace(original, string.Empty);
                if (ReferenceEquals(cleaned, original) || cleaned == original) return original;
                cleaned = SpaceBeforePunctuation.Replace(cleaned, "$1");
                cleaned = CommaBeforeStop.Replace(cleaned, "$1");
                cleaned = RepeatedSpaces.Replace(cleaned, " ").Trim().TrimStart(',', ';', '.', '…', ' ');
                if (cleaned.Length == 0) return string.Empty;
                // A sentence that started with a filler ("Um, hello") keeps its capital letter.
                if (char.IsUpper(original[0]) && char.IsLower(cleaned[0])) cleaned = char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
                return cleaned;
            }
            catch (RegexMatchTimeoutException) { return text.Trim(); }
        }

        /// <summary>Applies the owner's clean-up choice; a disabled option returns the transcript untouched (only trimmed).</summary>
        public static string Prepare(string? transcript, bool removeFillers) =>
            removeFillers ? RemoveFillers(transcript) : (transcript ?? string.Empty).Trim();
    }

    /// <summary>What releasing the microphone control means.</summary>
    public enum PushToTalkRelease
    {
        /// <summary>Not a push-to-talk release (nothing was started by this press, or the gesture is already finished).</summary>
        None,
        /// <summary>The control was held: stop recording and transcribe now (push-to-talk).</summary>
        StopAndTranscribe,
        /// <summary>A quick tap: recording keeps running and the next click stops it (the existing click-to-toggle behaviour).</summary>
        KeepRecording,
    }

    /// <summary>
    /// Press-and-hold on the microphone control. A press while idle starts recording at once; releasing after at least
    /// <see cref="HoldThreshold"/> stops and transcribes (push-to-talk), releasing sooner leaves the recording running so a tap
    /// still works as click-to-start / click-to-stop. A press while a recording is already running is a normal click and is left
    /// to the existing handler. Pure and deterministic: the caller passes the time.
    /// </summary>
    public sealed class PushToTalkGesture
    {
        public static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(400);

        private bool _startedByPress;
        private DateTime _pressedUtc;
        private bool _suppressClick;

        /// <summary>True while a press that started a recording has not been released yet.</summary>
        public bool IsHeld => _startedByPress;

        /// <summary>Call on press. Returns true when the caller should start recording now (only from idle).</summary>
        public bool OnPress(bool idle, DateTime nowUtc, bool fromClickableControl = true)
        {
            if (!idle || _startedByPress) return false;
            _startedByPress = true;
            _pressedUtc = nowUtc;
            _suppressClick = fromClickableControl;
            return true;
        }

        /// <summary>The Click that follows a release arrives right after it; whatever is left of the suppression afterwards is dropped (release outside the control).</summary>
        public void ClearClickSuppression() => _suppressClick = false;

        /// <summary>The recording could not start (microphone blocked, Gaming Mode, no engine): the press has no further meaning.</summary>
        public void OnStartFailed()
        {
            _startedByPress = false;
            _suppressClick = false;
        }

        /// <summary>Call on release (or when pointer capture is lost, or the key is released).</summary>
        public PushToTalkRelease OnRelease(DateTime nowUtc)
        {
            if (!_startedByPress) return PushToTalkRelease.None;
            _startedByPress = false;
            return nowUtc - _pressedUtc >= HoldThreshold ? PushToTalkRelease.StopAndTranscribe : PushToTalkRelease.KeepRecording;
        }

        /// <summary>Call from the Click handler: true once after a press that this gesture owned, so the same press is not handled twice.</summary>
        public bool ConsumeClick()
        {
            bool suppress = _suppressClick;
            _suppressClick = false;
            return suppress;
        }
    }
}
