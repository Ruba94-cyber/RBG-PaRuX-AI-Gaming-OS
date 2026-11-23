using System;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// The Detect / Locate / Install with PaRuX / Skip flow for the optional Aniki ReMake Playnite theme, shared by Settings and first-run
    /// setup. Nothing is downloaded until the caller passes a plan the owner confirmed, and nothing in Playnite's configuration changes
    /// without the consent text below having been shown first.
    /// </summary>
    public static class PlayniteThemeSetupFlow
    {
        private static string T(string en, string it) => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

        /// <summary>Source, version, size, checksum, license and target in plain words, or the reason nothing can be installed.</summary>
        public static (PlayniteThemePlanDto? Plan, string Text) Describe(GamingCallResult<PlayniteThemePlanDto> result)
        {
            if (!result.Succeeded || result.Value is null)
                return (null, result.Error ?? T("PaRuX could not check the Aniki ReMake release.", "PaRuX non ha potuto controllare la versione di Aniki ReMake."));
            PlayniteThemePlanDto p = result.Value;
            if (!p.Available) return (null, p.Message ?? T("No installable release.", "Nessuna versione installabile."));
            string sha = p.Sha256 is { Length: > 0 } h ? "SHA-256 " + h : T("no checksum published by the release (unverified)", "nessun checksum pubblicato dalla release (non verificabile)");
            string text = T("Official release ", "Versione ufficiale ") + p.Version + T(" from ", " da ") + p.SourceUrl + T(" (license ", " (licenza ") + p.License + "), " +
                (p.SizeBytes / 1048576) + " MB, " + sha + T(". It is installed into ", ". Viene installata in ") + p.TargetFolder +
                T(", staged first and swapped in at the end; the MIT license text is kept with the theme. Nothing else in Playnite is touched.",
                  ", prima in una cartella temporanea e poi spostata alla fine; il testo della licenza MIT resta con il tema. Nient'altro in Playnite viene toccato.");
            if (p.PlayniteRunning) text += " " + T("Playnite is running: close it before confirming.", "Playnite è in esecuzione: chiudilo prima di confermare.");
            if (p.ExistingFolder)
                text += " " + T("A folder with this theme already exists" + (p.ExistingVersion is null ? "" : " (version " + p.ExistingVersion + ")") +
                    ". Confirming replaces it and keeps it as a backup you can roll back to.",
                    "Esiste già una cartella con questo tema" + (p.ExistingVersion is null ? "" : " (versione " + p.ExistingVersion + ")") +
                    ". Confermando viene sostituita e conservata come backup a cui puoi tornare.");
            else text += " " + T("Press confirm to download and install.", "Premi conferma per scaricare e installare.");
            return (p, text);
        }

        public static string SelectConsentText() =>
            T("This changes ONE setting in Playnite: the Fullscreen theme becomes Aniki ReMake. PaRuX saves a backup of Playnite's Fullscreen settings first, changes nothing else, and you can restore the previous theme here at any time. Playnite must be closed. Press confirm to continue.",
              "Questo cambia UNA sola impostazione di Playnite: il tema Fullscreen diventa Aniki ReMake. PaRuX salva prima un backup delle impostazioni Fullscreen di Playnite, non cambia altro e puoi ripristinare il tema precedente da qui in qualsiasi momento. Playnite deve essere chiuso. Premi conferma per continuare.");

        public static string RestoreConsentText() =>
            T("This puts the previous Fullscreen theme back in Playnite (only if PaRuX changed it and nothing changed it since). Playnite must be closed. Press confirm to continue.",
              "Questo rimette in Playnite il tema Fullscreen precedente (solo se l'ha cambiato PaRuX e nulla l'ha modificato dopo). Playnite deve essere chiuso. Premi conferma per continuare.");

        public static string RollbackConsentText(bool hasBackup) => hasBackup
            ? T("This puts back the theme folder PaRuX replaced and removes the copy PaRuX installed. Playnite must be closed. Press confirm to continue.",
                "Questo rimette la cartella del tema che PaRuX aveva sostituito e rimuove la copia installata da PaRuX. Playnite deve essere chiuso. Premi conferma per continuare.")
            : T("This removes the Aniki ReMake folder that PaRuX installed (never one you installed yourself). Playnite must be closed. Press confirm to continue.",
                "Questo rimuove la cartella Aniki ReMake installata da PaRuX (mai una che hai installato tu). Playnite deve essere chiuso. Premi conferma per continuare.");

        public static string StageText(PlayniteThemeInstallStatusDto s) =>
            T("Install: ", "Installazione: ") + s.Stage + (s.TotalBytes > 0 && s.Stage == "downloading" ? " " + (s.BytesDownloaded * 100 / Math.Max(1, s.TotalBytes)) + "%" : string.Empty) +
            (s.Error is null ? string.Empty : " - " + s.Error) + (s.RolledBack ? T(" (your previous folder was put back)", " (la tua cartella precedente è stata rimessa)") : string.Empty);

        /// <summary>Starts the confirmed install and reports progress until it ends (bounded to 40 minutes). Returns the final line and whether it completed.</summary>
        public static async Task<(string Text, bool Completed)> InstallAsync(PlayniteThemePlanDto plan, Action<string> progress)
        {
            var started = await PlayniteClient.Current.InstallThemeAsync(new PlayniteThemeInstallRequest
            {
                Consent = true, Version = plan.Version ?? string.Empty, Sha256 = plan.Sha256, AcceptUnverified = false, ReplaceExisting = plan.ExistingFolder
            });
            if (!started.Succeeded) return (started.Error ?? T("The install could not start.", "L'installazione non è partita."), false);
            string last = string.Empty;
            for (int i = 0; i < 2400; i++)
            {
                await Task.Delay(1000);
                var st = await PlayniteClient.Current.GetThemeInstallStatusAsync();
                if (st.Value is not { } s) continue;
                last = StageText(s);
                progress(last);
                if (s.Stage is "completed") return (last, true);
                if (s.Stage is "failed" or "cancelled") return (last, false);
            }
            return (last, false);
        }
    }
}
