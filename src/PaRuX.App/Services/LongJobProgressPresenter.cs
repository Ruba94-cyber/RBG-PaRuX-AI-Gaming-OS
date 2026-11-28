using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Status line for a long job (image, video, music, training): the plain stage name, a percentage when known and a
    /// remaining-time estimate. The server's own estimate wins; otherwise one is derived from elapsed time and progress,
    /// but only once enough of the job is done for it to mean something.
    /// </summary>
    public static class LongJobProgressPresenter
    {
        public static string Line(string stage, int percent, int? serverEtaSeconds, TimeSpan elapsed, bool italian)
        {
            percent = Math.Clamp(percent, 0, 100);
            string head = string.IsNullOrWhiteSpace(stage) ? UiText.Pick("Working", "Elaborazione", italian) : stage.Trim();
            if (percent > 0) head += " · " + percent + "%";
            double? eta = EtaSeconds(percent, serverEtaSeconds, elapsed);
            if (eta.HasValue) return head + " · " + RemainingText(eta.Value, italian);
            return head + " · " + UiText.Pick("working for ", "in corso da ", italian) + Clock(elapsed);
        }

        public static double? EtaSeconds(int percent, int? serverEtaSeconds, TimeSpan elapsed)
        {
            if (serverEtaSeconds is >= 0) return serverEtaSeconds.Value;
            if (percent < 5 || percent >= 100 || elapsed.TotalSeconds < 5) return null;
            return elapsed.TotalSeconds * (100 - percent) / percent;
        }

        public static string RemainingText(double seconds, bool italian)
        {
            if (seconds < 10) return UiText.Pick("almost done", "quasi finito", italian);
            if (seconds < 90) return UiText.Pick("about " + (int)Math.Round(seconds / 5) * 5 + " s left", "mancano circa " + (int)Math.Round(seconds / 5) * 5 + " s", italian);
            int minutes = (int)Math.Ceiling(seconds / 60);
            if (minutes >= 90) return UiText.Pick("more than an hour left", "manca più di un'ora", italian);
            return UiText.Pick("about " + minutes + " min left", "mancano circa " + minutes + " min", italian);
        }

        public static string Clock(TimeSpan elapsed)
        {
            int total = (int)Math.Max(0, elapsed.TotalSeconds);
            return (total / 60) + ":" + (total % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
