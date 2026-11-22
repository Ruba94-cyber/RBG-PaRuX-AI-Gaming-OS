using System;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// The Detect / Locate / Install with PaRuX / Skip flow for Playnite, shared by Settings and the Console Mode launcher so both say
    /// the same thing and use the same Core calls. Nothing is downloaded until the caller passes a plan the owner confirmed.
    /// </summary>
    public static class PlayniteSetupFlow
    {
        private static string T(string en, string it) => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

        /// <summary>The exact release, size, checksum and target folder in plain words, or the reason nothing can be installed.</summary>
        public static (PlayniteInstallPlanDto? Plan, string Text) Describe(GamingCallResult<PlayniteInstallPlanDto> plan)
        {
            if (!plan.Succeeded || plan.Value is null) return (null, plan.Error ?? T("PaRuX could not check the Playnite release.", "PaRuX non ha potuto controllare la versione di Playnite."));
            if (!plan.Value.Available) return (null, plan.Value.Message ?? T("No installable release.", "Nessuna versione installabile."));
            string sha = plan.Value.Sha256 is { Length: > 0 } h ? "SHA-256 " + h : T("no checksum published by the release (unverified)", "nessun checksum pubblicato dalla release (non verificabile)");
            return (plan.Value, T("Official release ", "Versione ufficiale ") + plan.Value.Version + ", " + plan.Value.AssetName + ", " + (plan.Value.SizeBytes / 1048576) + " MB, " + sha +
                T(". It installs for your user only into ", ". Si installa solo per il tuo utente in ") + plan.Value.TargetFolder + T(". Press confirm to download and install.", ". Premi conferma per scaricare e installare."));
        }

        /// <summary>Starts the confirmed install and reports progress until it ends (bounded to 30 minutes). Returns the final line.</summary>
        public static async Task<string> InstallAsync(PlayniteInstallPlanDto plan, Action<string> progress)
        {
            var started = await PlayniteClient.Current.InstallAsync(new PlayniteInstallRequest
            {
                Consent = true, Version = plan.Version ?? string.Empty, Sha256 = plan.Sha256, AcceptUnverified = false
            });
            if (!started.Succeeded) return started.Error ?? T("The install could not start.", "L'installazione non è partita.");
            string last = string.Empty;
            for (int i = 0; i < 1800; i++)
            {
                await Task.Delay(1000);
                var st = await PlayniteClient.Current.GetInstallStatusAsync();
                if (st.Value is not { } s) continue;
                last = T("Install: ", "Installazione: ") + s.Stage + (s.TotalBytes > 0 ? " " + (s.BytesDownloaded * 100 / Math.Max(1, s.TotalBytes)) + "%" : string.Empty) + (s.Error is null ? string.Empty : " - " + s.Error);
                progress(last);
                if (s.Stage is "completed" or "failed" or "cancelled") break;
            }
            return last;
        }
    }
}
