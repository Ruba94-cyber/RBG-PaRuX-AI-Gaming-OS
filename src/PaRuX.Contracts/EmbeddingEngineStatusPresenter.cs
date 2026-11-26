using System;

namespace PaRuX.Contracts;

public sealed record EmbeddingEngineStatusPresentation(string Text, bool ShowInstallButton);

/// <summary>
/// Pure presentation helper for the shared Memory + classic RAG embedding engine status.
/// Corporate mode keeps idle ONNX sessions unloaded to save RAM, so Installed &amp;&amp; !Loaded
/// with no LastError means "ready on demand", never "failed to load".
/// </summary>
public static class EmbeddingEngineStatusPresenter
{
    public static EmbeddingEngineStatusPresentation Present(
        EmbeddingEngineStatus status,
        bool corporate,
        bool allowsExecutableDownloads,
        bool italian,
        string? installFailure = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        int dims = status.Dimensions ?? 384;

        if (status.Loaded)
        {
            string text = italian
                ? $"Installato e attivo ({dims}-dim) - Memoria e RAG ora ordinano per reale somiglianza semantica, non solo per parole chiave."
                : $"Installed and active ({dims}-dim) - Memory and RAG now rank by real semantic similarity, not just keyword overlap.";
            return new EmbeddingEngineStatusPresentation(text, ShowInstallButton: false);
        }

        if (status.Installed)
        {
            if (string.IsNullOrWhiteSpace(status.LastError))
            {
                string text = italian
                    ? $"Installato e pronto su richiesta ({dims}-dim) — scaricato dalla memoria quando inattivo per risparmiare RAM; si carica automaticamente durante la ricerca in Memoria o nei documenti."
                    : $"Installed and ready on demand ({dims}-dim) — kept unloaded while idle to save memory; loads automatically when Memory or document search runs.";
                return new EmbeddingEngineStatusPresentation(text, ShowInstallButton: false);
            }

            string errText = (italian ? "Installato ma caricamento non riuscito: " : "Installed but failed to load: ") + status.LastError;
            return new EmbeddingEngineStatusPresentation(errText, ShowInstallButton: !corporate && allowsExecutableDownloads);
        }

        if (corporate)
        {
            string text = italian
                ? "Non presente in questa cartella dati Corporate — Memoria e RAG classico usano la ricerca per parole chiave finché non viene ripristinata la cartella ai-runtimes del pacchetto Corporate."
                : "Not included in this Corporate data folder — Memory and classic RAG use keyword matching unless the offline Corporate package's ai-runtimes folder is restored.";
            return new EmbeddingEngineStatusPresentation(text, ShowInstallButton: false);
        }

        string baseMissing = italian
            ? "Non installato - Memoria e RAG attualmente ordinano solo per sovrapposizione di parole chiave. L'installazione aggiunge la vera ricerca semantica per entrambi, interamente locale (sentence-transformers/all-MiniLM-L6-v2, ~90 MB, download singolo, verificato tramite SHA-256 bloccato)."
            : "Not installed - Memory and RAG currently rank by keyword overlap only. Installing adds real semantic search for both, entirely local (sentence-transformers/all-MiniLM-L6-v2, ~90 MB, one-time download, verified against a pinned SHA-256).";
        if (!string.IsNullOrEmpty(installFailure))
            baseMissing += (italian ? " Ultimo tentativo di installazione non riuscito: " : " Last install attempt failed: ") + installFailure;

        return new EmbeddingEngineStatusPresentation(baseMissing, ShowInstallButton: allowsExecutableDownloads);
    }
}

/// <summary>
/// Pure localization and Corporate-mode text formatting helpers for Local Knowledge retrieval UI
/// (shared by SettingsView.Knowledge and KnowledgeWebPanel so unit tests cover all variants cleanly).
/// </summary>
public static class KnowledgeRetrievalText
{
    public const string MissingEmbedderEnglishReason =
        "The search model is not installed or is switched off, so keyword search is used.";

    public const string MissingVectorsEnglishReason =
        "No passage has a vector from the chosen search model yet; re-embed the index. Keyword search is used meanwhile.";

    public static string FormatEmbedderOption(string label, int dimensions, bool installed, bool corporate, bool italian)
    {
        string baseLabel = $"{label} ({dimensions})";
        if (installed)
            return corporate
                ? baseLabel + (italian ? " (pronto su richiesta)" : " (ready on demand)")
                : baseLabel;

        if (corporate)
            return baseLabel + (italian ? " - non incluso nella cartella dati" : " - not included in data folder");

        return baseLabel + (italian ? " - non installato" : " - not installed");
    }

    public static string FormatRerankerNote(bool installed, bool corporate, bool italian)
    {
        if (installed)
            return italian ? " · riordino locale pronto" : " · local reranker ready";
        if (corporate)
            return italian ? " · riordino locale non incluso" : " · local reranker not included";
        return italian ? " · riordino locale non installato" : " · local reranker not installed";
    }

    public static string LocalizeDegradedReason(string? reason, bool corporate, bool italian)
    {
        if (string.IsNullOrWhiteSpace(reason)) return string.Empty;

        if (string.Equals(reason, MissingEmbedderEnglishReason, StringComparison.Ordinal))
        {
            if (italian)
                return corporate
                    ? "Il modello di ricerca non è presente nella cartella dati o è disattivato, quindi viene usata la ricerca per parole chiave."
                    : "Il modello di ricerca non è installato o è disattivato, quindi viene usata la ricerca per parole chiave.";
            return corporate
                ? "The search model is not included in the data folder or is switched off, so keyword search is used."
                : reason;
        }

        if (string.Equals(reason, MissingVectorsEnglishReason, StringComparison.Ordinal))
        {
            return italian
                ? "Nessun passaggio ha ancora un vettore del modello di ricerca scelto; rigenera i vettori dell'indice. Nel frattempo viene usata la ricerca per parole chiave."
                : reason;
        }

        return reason;
    }

    public static string DescribeWebRetrieval(
        string method, string mode, string? embedderModel, bool offlineBlocked, bool corporate, bool italian)
    {
        if (string.Equals(mode, "hybrid", StringComparison.OrdinalIgnoreCase))
        {
            string model = string.IsNullOrWhiteSpace(embedderModel)
                ? (italian ? "modello locale" : "local model")
                : embedderModel;
            return italian
                ? $"La ricerca ibrida è attiva: parole chiave più corrispondenza per significato ({model}). Un codice o identificatore esatto ha sempre la precedenza."
                : $"Hybrid search is on: keyword matching plus meaning-based matching ({model}). An exact code or identifier still wins.";
        }

        if (string.Equals(method, "keyword", StringComparison.OrdinalIgnoreCase))
        {
            return italian
                ? "Solo ricerca per parole chiave, come hai scelto. Una domanda deve condividere parole con il passaggio."
                : "Keyword search only, as you chose. A question must share words with the passage.";
        }

        if (corporate)
        {
            return italian
                ? "Solo ricerca per parole chiave: i file del modello di ricerca semantica mancano nella cartella dati ai-runtimes del pacchetto Corporate. Ripristina la cartella ai-runtimes del pacchetto per abilitare la ricerca per significato."
                : "Keyword search only: the semantic search model files are missing from the Corporate package's ai-runtimes data folder. Restore the package's ai-runtimes folder to enable meaning-based matching.";
        }

        if (offlineBlocked)
        {
            return italian
                ? "Solo ricerca per parole chiave: il modello di ricerca non è installato e la modalità offline rigorosa ne blocca l'installazione."
                : "Keyword search only: the search model is not installed and strict offline mode blocks installing it.";
        }

        return italian
            ? "Solo ricerca per parole chiave: il modello di ricerca non è ancora installato. Installalo per aggiungere la corrispondenza per significato."
            : "Keyword search only: the search model is not installed yet. Install it to add meaning-based matching.";
    }
}
