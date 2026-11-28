using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public enum ModelBadgeTone { Good, Caution, Neutral }

    /// <summary>A short plain-language chip for Simple mode; <see cref="Explanation"/> is the tooltip / accessible description.</summary>
    public sealed record ModelBadge(string Text, ModelBadgeTone Tone, string Explanation);

    /// <summary>
    /// Plain-language badges for Model Hub cards in Simple mode ("Fast", "Best quality", "Fits your PC" / "Too big for your PC",
    /// "Uncensored"), so a first-time user never has to read quantization names or parameter counts. Honest by construction:
    /// "Fast" and "Best quality" are relative to the other models on screen and derived from file size and the hardware-fit verdict,
    /// not from a benchmark, and the tooltip says so.
    /// </summary>
    public static class ModelBadgePresenter
    {
        private const long FastCeilingBytes = 5L * 1024 * 1024 * 1024;

        public static IReadOnlyList<ModelBadge> For(
            RecommendedModel model,
            ModelFitVerdict verdict,
            IReadOnlyList<(RecommendedModel Model, ModelFitVerdict Verdict)> all,
            bool italian)
        {
            var badges = new List<ModelBadge>();
            string T(string en, string it) => UiText.Pick(en, it, italian);

            switch (verdict)
            {
                case ModelFitVerdict.Fits:
                    badges.Add(new ModelBadge(T("Fits your PC", "Adatto al tuo PC"), ModelBadgeTone.Good,
                        T("Estimated to run comfortably on this PC.", "Stimato per funzionare bene su questo PC.")));
                    break;
                case ModelFitVerdict.Tight:
                    badges.Add(new ModelBadge(T("Tight fit", "Al limite per il tuo PC"), ModelBadgeTone.Caution,
                        T("Should run, but it will use most of your memory and may be slow.", "Dovrebbe funzionare, ma userà quasi tutta la memoria e potrebbe essere lento.")));
                    break;
                case ModelFitVerdict.Big:
                    badges.Add(new ModelBadge(T("Too big for your PC", "Troppo grande per il tuo PC"), ModelBadgeTone.Caution,
                        T("This PC does not have enough memory for it. Pick a smaller one.", "Questo PC non ha abbastanza memoria. Scegline uno più piccolo.")));
                    break;
            }

            if (verdict == ModelFitVerdict.Fits && model.EstimatedSizeBytes > 0 && model.EstimatedSizeBytes <= FastCeilingBytes)
                badges.Add(new ModelBadge(T("Fast", "Veloce"), ModelBadgeTone.Good,
                    T("Small enough to answer quickly on this PC (based on size, not a speed test).",
                      "Abbastanza piccolo da rispondere in fretta su questo PC (in base alla dimensione, non a un test di velocità).")));

            if (IsBestQuality(model, verdict, all))
                badges.Add(new ModelBadge(T("Best quality", "Qualità migliore"), ModelBadgeTone.Good,
                    T("The largest model in this list that still fits your PC; larger models usually write better answers but reply more slowly.",
                      "Il modello più grande di questo elenco che sta ancora nel tuo PC: di solito risponde meglio ma è più lento.")));

            if (string.Equals(model.ContentProfileId, ModelContentProfileIds.Uncensored, StringComparison.Ordinal))
                badges.Add(new ModelBadge(T("Uncensored", "Senza censura"), ModelBadgeTone.Neutral,
                    T("Answers without the usual content limits. You choose it and you are responsible for how you use it.",
                      "Risponde senza le solite limitazioni sui contenuti. Lo scegli tu e te ne assumi la responsabilità.")));

            return badges;
        }

        private static bool IsBestQuality(RecommendedModel model, ModelFitVerdict verdict,
            IReadOnlyList<(RecommendedModel Model, ModelFitVerdict Verdict)> all)
        {
            if (verdict != ModelFitVerdict.Fits || model.EstimatedSizeBytes <= 0) return false;
            var fitting = all.Where(x => x.Verdict == ModelFitVerdict.Fits && x.Model.EstimatedSizeBytes > 0).ToList();
            if (fitting.Count < 2) return false;
            long max = fitting.Max(x => x.Model.EstimatedSizeBytes);
            return model.EstimatedSizeBytes == max && fitting.Count(x => x.Model.EstimatedSizeBytes == max) == 1;
        }
    }
}
