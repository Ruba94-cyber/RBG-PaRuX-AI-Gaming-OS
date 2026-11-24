using System;
using System.Collections.Generic;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public enum ConsoleReadinessState { Ok, Attention, Missing, Skipped }

    public enum ConsolePlayniteStartResult { Unavailable, Fullscreen, DesktopFallback }

    /// <summary>What PaRuX's own window does after a Console-mode start attempt.</summary>
    public enum ConsoleStartFollowUp
    {
        /// <summary>Playnite Fullscreen owns the screen: minimize PaRuX (the AI keeps running, a pad action or Playnite's exit brings it back).</summary>
        MinimizePaRuX,
        /// <summary>Leave the PaRuX window as it is (Playnite opened Desktop, or an always-Console start could not start Playnite).</summary>
        StayOpen,
        /// <summary>Playnite was not started and the TV start preference is on: use the legacy native Console fallback.</summary>
        LegacyConsoleFallback
    }

    public sealed record ConsoleReadinessRow(string Id, string Label, ConsoleReadinessState State, string Detail);

    /// <summary>
    /// Pure rules for the Settings &gt; Console mode state rows and the "start Playnite Fullscreen with Console mode" decision. It only turns
    /// facts Core and the pad services already reported into honest rows; it never probes, launches or edits anything itself.
    /// </summary>
    public static class ConsoleModeReadiness
    {
        /// <summary>True when starting Console mode should also start Playnite Fullscreen (found, not skipped, has the Fullscreen executable, switch on).</summary>
        /// <param name="explicitRequest">The owner asked for Console right now (argument, shortcut, pad action, "always start in Console mode"): the
        /// "start Playnite with the TV preference" switch is then not consulted, only whether Playnite can actually start.</param>
        public static bool ShouldStartWithConsole(PlayniteSettingsDto settings, PlayniteComponentDto component, bool explicitRequest = false) =>
            (explicitRequest || settings.ConsoleStartsPlaynite) && !settings.Skipped && !component.Skipped && component.Detected && component.FullscreenExe is not null;

        /// <summary>
        /// What PaRuX's own window does once the Console start attempt is classified. Only a confirmed Fullscreen hides PaRuX. A Desktop
        /// fallback is left alone. When Playnite was not started, the "always start in Console mode" owner stays on the normal AI window
        /// (never a native TV takeover on a PC monitor); only the TV start preference may use the legacy native Console fallback.
        /// </summary>
        public static ConsoleStartFollowUp FollowUp(ConsolePlayniteStartResult result, bool alwaysConsole) => result switch
        {
            ConsolePlayniteStartResult.Fullscreen => ConsoleStartFollowUp.MinimizePaRuX,
            ConsolePlayniteStartResult.DesktopFallback => ConsoleStartFollowUp.StayOpen,
            _ => alwaysConsole ? ConsoleStartFollowUp.StayOpen : ConsoleStartFollowUp.LegacyConsoleFallback
        };

        /// <summary>
        /// A short, honest line when Console mode will start Playnite Fullscreen WITHOUT the Aniki ReMake look (theme missing or not selected),
        /// or null when nothing needs saying (theme fine, skipped by the owner, or unknown). It never claims the theme loaded.
        /// </summary>
        public static string? ThemeAdvice(PlayniteThemeComponentDto? theme, Func<string, string, string> t)
        {
            if (theme is null || theme.Skipped) return null;
            if (!theme.Detected)
                return t("Aniki ReMake is not installed: Playnite Fullscreen uses its current theme. You can install it in Settings > Console mode.",
                         "Aniki ReMake non è installato: Playnite Fullscreen usa il suo tema attuale. Puoi installarlo in Impostazioni > Modalità Console.");
            if (theme.ConfigReadable && !theme.Selected)
                return t("Aniki ReMake is installed but Playnite Fullscreen uses " + (theme.SelectedTheme ?? "another theme") + ". Select it in Settings > Console mode.",
                         "Aniki ReMake è installato ma Playnite Fullscreen usa " + (theme.SelectedTheme ?? "un altro tema") + ". Selezionalo in Impostazioni > Modalità Console.");
            return null;
        }

        /// <summary>
        /// A Console-first handoff is complete only when the Core confirms an actual Fullscreen
        /// start or an existing Fullscreen instance. A Desktop fallback, refusal or unknown mode
        /// must not hide the PaRuX UI.
        /// </summary>
        public static bool AcceptedConsoleHandoff(PlayniteLaunchResultDto? launch) =>
            launch is { Ok: true, Mode: PlayniteModes.Console, Outcome: "started" or "already-running" };

        /// <summary>
        /// A Desktop fallback is a real Playnite launch, but it must NOT be treated as
        /// Fullscreen success or followed by a second native Console presenter.
        /// </summary>
        public static ConsolePlayniteStartResult ClassifyStart(PlayniteLaunchResultDto? launch) =>
            AcceptedConsoleHandoff(launch) ? ConsolePlayniteStartResult.Fullscreen :
            launch is { Ok: true, Mode: PlayniteModes.Desktop, Outcome: "started" or "already-running" }
                ? ConsolePlayniteStartResult.DesktopFallback
                : ConsolePlayniteStartResult.Unavailable;

        /// <summary>The one-button test needs a Playnite with the Fullscreen executable that the owner has not skipped.</summary>
        public static bool CanTest(PlayniteComponentDto component) =>
            component.Detected && !component.Skipped && component.FullscreenExe is not null;

        public static IReadOnlyList<ConsoleReadinessRow> Build(PlayniteComponentDto playnite, PlayniteThemeComponentDto? theme,
            bool displayChosen, IReadOnlyList<string> padNames, Func<string, string, string> t)
        {
            var rows = new List<ConsoleReadinessRow>();

            if (playnite.Skipped)
                rows.Add(new("Playnite", t("Playnite found", "Playnite trovato"), ConsoleReadinessState.Skipped, t("skipped by you", "saltato da te")));
            else if (!playnite.Detected)
                rows.Add(new("Playnite", t("Playnite found", "Playnite trovato"), ConsoleReadinessState.Missing, t("not found: locate it or install it", "non trovato: indicalo o installalo")));
            else if (playnite.FullscreenExe is null)
                rows.Add(new("Playnite", t("Playnite found", "Playnite trovato"), ConsoleReadinessState.Attention, t("found, but this folder has no Fullscreen app", "trovato, ma questa cartella non ha l'app Fullscreen")));
            else
                rows.Add(new("Playnite", t("Playnite found", "Playnite trovato"), ConsoleReadinessState.Ok,
                    (playnite.Version is { Length: > 0 } v ? v + ", " : string.Empty) + playnite.Ownership));

            if (theme is null)
            {
                rows.Add(new("ThemeInstalled", t("Aniki ReMake theme installed", "Tema Aniki ReMake installato"), ConsoleReadinessState.Attention, t("not checked yet", "non ancora controllato")));
                rows.Add(new("ThemeSelected", t("Aniki ReMake selected in Playnite", "Aniki ReMake selezionato in Playnite"), ConsoleReadinessState.Attention, t("not checked yet", "non ancora controllato")));
            }
            else
            {
                if (theme.Skipped)
                    rows.Add(new("ThemeInstalled", t("Aniki ReMake theme installed", "Tema Aniki ReMake installato"), ConsoleReadinessState.Skipped, t("skipped by you (Playnite keeps its own theme)", "saltato da te (Playnite usa il suo tema)")));
                else if (!theme.Detected)
                    rows.Add(new("ThemeInstalled", t("Aniki ReMake theme installed", "Tema Aniki ReMake installato"), ConsoleReadinessState.Missing,
                        theme.PlayniteDataFolderFound ? t("not installed: locate it or install it (optional)", "non installato: indicalo o installalo (facoltativo)")
                                                       : t("Playnite has no data folder yet: start Playnite once", "Playnite non ha ancora la cartella dati: avvia Playnite una volta")));
                else
                    rows.Add(new("ThemeInstalled", t("Aniki ReMake theme installed", "Tema Aniki ReMake installato"), ConsoleReadinessState.Ok,
                        (theme.Version is { Length: > 0 } tv ? tv + ", " : string.Empty) + theme.Ownership));

                if (theme.Skipped || !theme.Detected)
                    rows.Add(new("ThemeSelected", t("Aniki ReMake selected in Playnite", "Aniki ReMake selezionato in Playnite"), ConsoleReadinessState.Skipped, t("needs the theme first", "serve prima il tema")));
                else if (!theme.ConfigReadable)
                    rows.Add(new("ThemeSelected", t("Aniki ReMake selected in Playnite", "Aniki ReMake selezionato in Playnite"), ConsoleReadinessState.Attention,
                        t("Playnite's Fullscreen settings could not be read (start Playnite Fullscreen once)", "Le impostazioni Fullscreen di Playnite non sono leggibili (avvia Playnite Fullscreen una volta)")));
                else if (theme.Selected)
                    rows.Add(new("ThemeSelected", t("Aniki ReMake selected in Playnite", "Aniki ReMake selezionato in Playnite"), ConsoleReadinessState.Ok, t("yes", "sì")));
                else
                    rows.Add(new("ThemeSelected", t("Aniki ReMake selected in Playnite", "Aniki ReMake selezionato in Playnite"), ConsoleReadinessState.Attention,
                        t("no: Playnite uses ", "no: Playnite usa ") + (theme.SelectedTheme ?? "?")));
            }

            // The theme's companion is necessary for the actual Aniki UX, but a manifest on disk
            // does not prove Playnite loaded a compatible version. Never label runtime as verified.
            bool helperFound = theme?.AnikiHelperFoundOnDisk == true;
            rows.Add(new("AnikiHelper", t("Aniki Helper extension", "Estensione Aniki Helper"),
                theme?.Skipped == true ? ConsoleReadinessState.Skipped :
                    ConsoleReadinessState.Attention, // an on-disk manifest does not prove the helper loaded successfully
                theme?.Skipped == true
                    ? t("theme skipped", "tema saltato")
                    : helperFound
                        ? (theme?.AnikiHelperVersion is { Length: > 0 } hv ? hv + ": " : "") +
                            t("extension manifest found; check that Playnite loads it", "manifest trovato; verifica che Playnite carichi l'estensione")
                        : t("required by Aniki: install/update in Playnite Desktop > Add-ons (F9) > Browse > Generic",
                            "richiesta da Aniki: installa/aggiorna in Playnite Desktop > Add-ons (F9) > Browse > Generic")));

            rows.Add(new("Display", t("TV / display chosen", "TV / schermo scelto"),
                displayChosen ? ConsoleReadinessState.Ok : ConsoleReadinessState.Attention,
                displayChosen ? t("chosen", "scelto") : t("not chosen (set it in Console / TV)", "non scelto (impostalo in Console / TV)")));

            rows.Add(new("Controller", t("Controller detected (any pad)", "Controller rilevato (qualsiasi pad)"),
                padNames.Count > 0 ? ConsoleReadinessState.Ok : ConsoleReadinessState.Attention,
                padNames.Count > 0 ? string.Join(", ", padNames) : t("no controller found right now", "nessun controller trovato adesso")));
            return rows;
        }
    }
}
