using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    /// <summary>
    /// Localizes the fixed English problem sentences returned by the Steam achievements service so the
    /// card never mixes a translated label with an untranslated English message. The server text stays
    /// English (API contract); unknown text is shown unchanged.
    /// </summary>
    public static class SteamAchievementsText
    {
        private static readonly Dictionary<string, string> Italian = new(StringComparer.Ordinal)
        {
            ["Not a valid Steam game id."] = "ID del gioco Steam non valido.",
            ["Save your Steam Web API key and SteamID first."] = "Salva prima la tua chiave Steam Web API e lo SteamID.",
            ["Turn the Internet switch on to read Steam achievements."] = "Attiva l'interruttore Internet per leggere i risultati Steam.",
            ["The saved Steam key can no longer be read. Save it again."] = "La chiave Steam salvata non è più leggibile. Salvala di nuovo.",
            ["Steam answered in an unexpected format."] = "Steam ha risposto in un formato inatteso.",
            ["This game has no achievements."] = "Questo gioco non ha risultati.",
            ["Steam refused the key or the profile is private (set Game details to Public)."] = "Steam ha rifiutato la chiave oppure il profilo è privato (imposta Dettagli di gioco su Pubblico).",
            ["Could not reach Steam or read its answer."] = "Impossibile raggiungere Steam o leggerne la risposta.",
        };

        public static IReadOnlyCollection<string> KnownProblems => Italian.Keys;

        public static string LocalizeProblem(string? problem) => LocalizeProblem(problem, UiText.IsItalian);

        public static string LocalizeProblem(string? problem, bool italian) =>
            problem is null ? string.Empty
            : italian && Italian.TryGetValue(problem, out string? it) ? it
            : problem;
    }
}
