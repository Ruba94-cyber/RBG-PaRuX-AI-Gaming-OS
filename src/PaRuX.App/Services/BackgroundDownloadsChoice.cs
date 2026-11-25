namespace PaRuX.Services
{
    /// <summary>What the first-run question and the Settings switch show for "background runtime downloads and online catalog checks".</summary>
    public sealed record BackgroundDownloadsChoiceView(
        bool IsOn, bool CanChange,
        string TitleEn, string TitleIt,
        string ExplanationEn, string ExplanationIt,
        string StateEn, string StateIt);

    /// <summary>
    /// One wording and one rule for both places that ask: the first-run component step and Settings > Automatic checks. The safe default is
    /// OFF (nothing downloads or phones a catalog without a yes), and Corporate is always OFF and cannot be changed. The server stays the
    /// authority (it stamps the consent time and re-normalises Corporate to off); this only decides what the UI shows and sends.
    /// </summary>
    public static class BackgroundDownloadsChoice
    {
        public const bool DefaultOn = false;

        public const string TitleEn = "Keep runtimes up to date in the background";
        public const string TitleIt = "Mantieni i runtime aggiornati in background";

        public const string ExplanationEn =
            "If you turn this on, PaRuX may check online catalogs (for example the llama.cpp releases) and download newer builds of the runtimes it manages for you, while you are not using it. "
            + "It never downloads models, never touches software you manage yourself, and never runs during Gaming. Off by default; you can change it any time in Settings > Automatic checks.";

        public const string ExplanationIt =
            "Se lo attivi, PaRuX può consultare cataloghi online (ad esempio le release di llama.cpp) e scaricare versioni più recenti dei runtime che gestisce per te, quando non lo stai usando. "
            + "Non scarica mai modelli, non tocca il software che gestisci tu e non parte durante il Gaming. Spento per impostazione predefinita; puoi cambiarlo quando vuoi in Impostazioni > Controlli automatici.";

        public const string CorporateEn = "Corporate mode is offline: background downloads and online checks are always off.";
        public const string CorporateIt = "La modalità Corporate è offline: download in background e controlli online sono sempre spenti.";

        public static BackgroundDownloadsChoiceView Build(bool currentlyOn, bool corporate)
        {
            bool on = !corporate && currentlyOn;
            return new BackgroundDownloadsChoiceView(
                on, !corporate, TitleEn, TitleIt,
                corporate ? CorporateEn + " " + ExplanationEn : ExplanationEn,
                corporate ? CorporateIt + " " + ExplanationIt : ExplanationIt,
                on ? "On: PaRuX may check online catalogs and download runtime updates in the background." : "Off: nothing is downloaded or checked online unless you ask.",
                on ? "Attivo: PaRuX può consultare cataloghi online e scaricare aggiornamenti dei runtime in background." : "Spento: nulla viene scaricato o controllato online se non lo chiedi tu.");
        }

        /// <summary>The value to send to the server for a toggle position: Corporate can only ever send false.</summary>
        public static bool Requested(bool toggleOn, bool corporate) => !corporate && toggleOn;
    }
}
