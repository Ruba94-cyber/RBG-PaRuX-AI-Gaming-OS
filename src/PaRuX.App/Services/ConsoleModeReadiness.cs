using System;
using System.Collections.Generic;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public enum ConsoleReadinessState { Ok, Attention, Missing, Skipped }

    public sealed record ConsoleReadinessRow(string Id, string Label, ConsoleReadinessState State, string Detail);

    /// <summary>
    /// Pure rules for the Settings &gt; Console mode state rows and the "start Playnite Fullscreen with Console mode" decision. It only turns
    /// facts Core and the pad services already reported into honest rows; it never probes, launches or edits anything itself.
    /// </summary>
    public static class ConsoleModeReadiness
    {
        /// <summary>True when starting Console mode should also start Playnite Fullscreen (found, not skipped, has the Fullscreen executable, switch on).</summary>
        public static bool ShouldStartWithConsole(PlayniteSettingsDto settings, PlayniteComponentDto component) =>
            settings.ConsoleStartsPlaynite && !settings.Skipped && !component.Skipped && component.Detected && component.FullscreenExe is not null;

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
