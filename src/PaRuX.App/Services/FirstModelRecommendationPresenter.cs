using System;
using System.Globalization;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Plain-words recommendation for the first-run model choice, built only from the hardware-fit verdict the Core already computed
    /// (Suitability, VRAM, RAM). It states what fits and how replies will feel; it never claims answer quality.
    /// </summary>
    public static class FirstModelRecommendationPresenter
    {
        /// <summary>The general-chat candidate to preselect, or null when none is recommended or it is no longer offered.</summary>
        public static FirstModelCandidate? PickRecommended(FirstModelAdvice? advice)
        {
            if (advice == null || string.IsNullOrWhiteSpace(advice.RecommendedId)) return null;
            FirstModelCandidate? c = advice.Candidates.FirstOrDefault(x => x.Model.Id == advice.RecommendedId);
            if (c == null || c.Rank <= 0) return null;
            if (string.Equals(c.Suitability, "TooLarge", StringComparison.OrdinalIgnoreCase)) return null;
            if (c.RuntimeRequirementState is "NeedsRuntimeUpdate" or "NeedsUserAction") return null;
            return c;
        }

        /// <summary>One short sentence, e.g. "Fits your 16 GB graphics card, so replies are fast."</summary>
        public static string Reason(FirstModelCandidate candidate, FirstModelAdvice advice, bool italian)
        {
            if (candidate.Model.Id.StartsWith("local:", StringComparison.Ordinal))
                return italian ? "È già sul tuo PC e pronto all'uso, senza download." : "Already on your PC and ready to use, no download needed.";

            string vram = Gb(advice.VramBytes), ram = Gb(advice.RamBytes);
            switch (candidate.Suitability)
            {
                case "FitsOnGpu" when vram.Length > 0:
                    return italian ? $"Sta nella tua scheda video da {vram} GB, quindi risponde in fretta."
                                   : $"Fits your {vram} GB graphics card, so replies are fast.";
                case "SplitWithRam" when vram.Length > 0 && ram.Length > 0:
                    return italian ? $"Usa insieme la scheda video ({vram} GB) e la memoria ({ram} GB): risposte un po' più lente."
                                   : $"Uses your graphics card ({vram} GB) and memory ({ram} GB) together, so replies are a little slower.";
                case "CpuOnly" when ram.Length > 0:
                    return italian ? $"Gira sul processore con i tuoi {ram} GB di memoria: funziona, ma le risposte sono più lente."
                                   : $"Runs on your processor with your {ram} GB of memory: it works, but replies are slower.";
                default:
                    return candidate.Reason;
            }
        }

        public static string Headline(FirstModelCandidate candidate, FirstModelAdvice advice, bool italian) =>
            (italian ? "Consigliato per il tuo PC: " : "Recommended for your PC: ") + candidate.Model.Title + ". " + Reason(candidate, advice, italian);

        /// <summary>Label for the single empty-chat button when no model is installed.</summary>
        public static string GetModelButtonText(FirstModelAdvice? advice, bool italian)
        {
            FirstModelCandidate? pick = PickRecommended(advice);
            if (pick == null) return italian ? "Scarica un modello" : "Get a model";
            return (italian ? "Scarica un modello — consigliato: " : "Get a model — recommended: ") + pick.Model.Title;
        }

        /// <summary>Calm explanation under the empty-chat headline (no error wording).</summary>
        public static string NoModelDescription(FirstModelAdvice? advice, bool italian)
        {
            FirstModelCandidate? pick = PickRecommended(advice);
            string basis = italian
                ? "PaRuX ha bisogno di un modello IA sul tuo PC per rispondere. Ci vuole un solo clic."
                : "PaRuX needs an AI model on your PC to reply. It takes one click.";
            return pick == null ? basis : basis + " " + Reason(pick, advice!, italian);
        }

        private static string Gb(long bytes) =>
            bytes <= 0 ? string.Empty : Math.Max(1, (int)Math.Round(bytes / 1024d / 1024d / 1024d)).ToString(CultureInfo.InvariantCulture);
    }
}
