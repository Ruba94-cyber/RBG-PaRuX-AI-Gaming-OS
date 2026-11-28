using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Pure formatting shared by Model Hub and onboarding download rows.</summary>
    public static class ModelDownloadPresentationPolicy
    {
        private static string T(string en, string it) => UiText.Pick(en, it);

        public static string DescribeStatus(ModelDownloadStatus job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            return job.Status switch
            {
                "queued" => T("queued", "in coda"),
                "downloading" => T("downloading", "download in corso"),
                "retrying" => T("retrying", "nuovo tentativo"),
                "paused" when job.Error == "Paused for Gaming" => T("paused for Gaming / resumable", "in pausa per il Gaming / riprendibile"),
                "paused" => T("paused / resumable", "in pausa / riprendibile"),
                "cancelled" => T("cancelled / resumable", "annullato / riprendibile"),
                "failed" => T("failed", "non riuscito"),
                "completed" => T("completed", "completato"),
                _ => job.Status
            };
        }

        public static string FormatDetails(ModelDownloadStatus job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            var details = new List<string>();

            if (ModelDownloadStatusPolicy.IsActive(job.Status))
            {
                details.Add(FormatSpeed(job.SpeedBytesPerSecond));
                details.Add(FormatEta(job.EtaSeconds));
            }
            else if (job.SpeedBytesPerSecond.HasValue || job.EtaSeconds.HasValue)
            {
                if (job.SpeedBytesPerSecond.HasValue) details.Add(FormatSpeed(job.SpeedBytesPerSecond));
                if (job.EtaSeconds.HasValue) details.Add(FormatEta(job.EtaSeconds));
            }

            if (job.Attempt > 0)
            {
                details.Add(job.MaxAttempts > 0
                    ? T("Attempt ", "Tentativo ") + job.Attempt.ToString(CultureInfo.InvariantCulture) + "/" + job.MaxAttempts.ToString(CultureInfo.InvariantCulture)
                    : T("Attempt ", "Tentativo ") + job.Attempt.ToString(CultureInfo.InvariantCulture));
            }

            if (job.NextRetryUtc.HasValue)
                details.Add(T("Next retry: ", "Prossimo tentativo: ") + FormatUtc(job.NextRetryUtc.Value));

            if (!string.IsNullOrWhiteSpace(job.FailureClass))
                details.Add(T("Failure: ", "Errore: ") + DescribeFailureClass(job.FailureClass));

            if (!string.IsNullOrWhiteSpace(job.Error)) details.Add(job.Error.Trim());
            if (!string.IsNullOrWhiteSpace(job.Remediation)) details.Add(job.Remediation.Trim());
            return string.Join(" · ", details);
        }

        public static string GetActionAutomationId(string surface, string downloadId, string action)
        {
            string safeSurface = Sanitize(surface);
            string safeId = Sanitize(downloadId);
            string safeAction = Sanitize(action);
            if (safeSurface.Length == 0 || safeId.Length == 0 || safeAction.Length == 0)
                throw new ArgumentException("A surface, download id and action are required.");
            return safeSurface + "_Download_" + safeId + "_" + safeAction + "_Button";
        }

        private static string FormatSpeed(double? bytesPerSecond)
        {
            if (!bytesPerSecond.HasValue || !double.IsFinite(bytesPerSecond.Value) || bytesPerSecond.Value <= 0)
                return T("Speed unavailable", "Velocità non disponibile");

            double size = bytesPerSecond.Value;
            string[] units = { "B/s", "KiB/s", "MiB/s", "GiB/s", "TiB/s" };
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return T("Speed ", "Velocità ") + size.ToString("0.0", CultureInfo.CurrentCulture) + " " + units[unit];
        }

        private static string FormatEta(double? seconds)
        {
            if (!seconds.HasValue || !double.IsFinite(seconds.Value) || seconds.Value < 0
                || seconds.Value > TimeSpan.MaxValue.TotalSeconds)
                return T("ETA unavailable", "Tempo restante non disponibile");

            if (seconds.Value < 1) return T("ETA <1s", "Mancano meno di 1s");
            TimeSpan eta = TimeSpan.FromSeconds(seconds.Value);
            if (eta.TotalDays >= 1)
                return T("ETA ", "Mancano ") + ((int)eta.TotalDays).ToString(CultureInfo.InvariantCulture) + "d " + eta.Hours.ToString(CultureInfo.InvariantCulture) + "h";
            if (eta.TotalHours >= 1)
                return T("ETA ", "Mancano ") + ((int)eta.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " + eta.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
            if (eta.TotalMinutes >= 1)
                return T("ETA ", "Mancano ") + ((int)eta.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m " + eta.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
            return T("ETA ", "Mancano ") + Math.Max(1, eta.Seconds).ToString(CultureInfo.InvariantCulture) + "s";
        }

        private static string FormatUtc(DateTime value)
        {
            DateTime utc = value.Kind switch
            {
                DateTimeKind.Local => value.ToUniversalTime(),
                DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
                _ => value
            };
            return utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        }

        private static string DescribeFailureClass(string failureClass) => failureClass.ToLowerInvariant() switch
        {
            "permanent" => T("permanent", "definitivo"),
            "transient" => T("temporary", "temporaneo"),
            "auth" => T("authentication required", "accesso richiesto"),
            "disk" => T("disk space", "spazio su disco"),
            "cancelled" => T("cancelled", "annullato"),
            _ => T("download issue", "problema di download")
        };

        private static string Sanitize(string value) => string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit));
    }
}
