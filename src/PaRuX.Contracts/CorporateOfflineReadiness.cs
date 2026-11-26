namespace PaRuX.Contracts;

/// <summary>
/// Cheap, offline, read-only packaging preflight for Corporate Lite. This is intentionally
/// NOT a native llama.cpp / ONNX load or a proof of RAG quality. Never scans user documents,
/// tries the network or starts a runtime. A separate live acceptance test is still required.
/// </summary>
public sealed record CorporateOfflineReadiness(
    bool CpuRuntimePresent, bool ChatModelPresent, bool EmbeddingPresent,
    bool RerankerPresent, string? StarterModelName,
    bool MultilingualEmbeddingPresent = false,
    bool MultilingualRerankerPresent = false)
{
    public bool HasChatAssets => CpuRuntimePresent && ChatModelPresent;
    public bool HasDocumentAssets => HasChatAssets && (EmbeddingPresent || MultilingualEmbeddingPresent);

    public string ToStatusText(bool italian)
    {
        static string Mark(bool found, bool italian) => found
            ? (italian ? "presente" : "present")
            : (italian ? "MANCANTE" : "MISSING");

        string embeddingState = MultilingualEmbeddingPresent
            ? (italian
                ? "presente (multilingue E5" + (EmbeddingPresent ? " + MiniLM)" : ")")
                : "present (multilingual E5" + (EmbeddingPresent ? " + MiniLM)" : ")"))
            : EmbeddingPresent
                ? (italian ? "presente (MiniLM)" : "present (MiniLM)")
                : (italian ? "MANCANTE" : "MISSING");

        string rerankerState = MultilingualRerankerPresent
            ? (italian
                ? "presente (multilingue mmarco" + (RerankerPresent ? " + fallback)" : ")")
                : "present (multilingual mmarco" + (RerankerPresent ? " + fallback)" : ")"))
            : Mark(RerankerPresent, italian);

        if (italian)
            return "Verifica locale (solo file, non prova di funzionamento): motore CPU "
                + Mark(CpuRuntimePresent, true) + " · modello Chat " + Mark(ChatModelPresent, true)
                + " · ricerca semantica documenti " + embeddingState
                + " · reranker " + rerankerState
                + (HasDocumentAssets ? ". Pronto per il test reale di Chat e documenti."
                    : ". Importa o ripristina i componenti mancanti prima di usare Chat e documenti.");
        return "Offline file check (not a functional test): CPU engine "
            + Mark(CpuRuntimePresent, false) + " · Chat model " + Mark(ChatModelPresent, false)
            + " · document semantic search " + embeddingState
            + " · reranker " + rerankerState
            + (HasDocumentAssets ? ". Ready for a real chat-and-document test."
                : ". Restore or import missing components before using Chat and documents.");
    }

    public static CorporateOfflineReadiness Inspect(string? dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            return new(false, false, false, false, null);
        try
        {
            string runtimeRoot = Path.Combine(dataRoot, "LocalAI", "runtime");
            bool cpu = Directory.Exists(runtimeRoot) && Directory
                .EnumerateDirectories(runtimeRoot, "llama.cpp-*-cpu", SearchOption.TopDirectoryOnly)
                .Any(dir => Nonempty(Path.Combine(dir, "llama-server.exe"), 4096));

            string models = Path.Combine(dataRoot, "models");
            string? selected = null;
            if (Directory.Exists(models))
            {
                string marker = Path.Combine(models, ".corporate-default-model");
                if (File.Exists(marker) && new FileInfo(marker).Length <= 4096)
                {
                    string name = File.ReadAllText(marker).Trim().Trim('\uFEFF');
                    if (SafeGgufName(name))
                    {
                        string candidate = Path.Combine(models, name);
                        if (PlausibleGguf(candidate)) selected = name;
                    }
                }
                if (selected == null)
                {
                    selected = Directory.EnumerateFiles(models, "*.gguf", SearchOption.TopDirectoryOnly)
                        .Where(f => SafeGgufName(Path.GetFileName(f)) && PlausibleGguf(f))
                        .Select(Path.GetFileName).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                }
            }

            string embed = Path.Combine(dataRoot, "ai-runtimes", "embeddings");
            bool embeddings = Nonempty(Path.Combine(embed, "model.onnx"), 100_000)
                && Nonempty(Path.Combine(embed, "vocab.txt"), 1_000)
                && Nonempty(Path.Combine(embed, "config.json"), 1)
                && Nonempty(Path.Combine(embed, "tokenizer_config.json"), 1);
            string rerank = Path.Combine(dataRoot, "ai-runtimes", "reranker");
            bool reranker = Nonempty(Path.Combine(rerank, "model.onnx"), 100_000)
                && Nonempty(Path.Combine(rerank, "vocab.txt"), 1_000);
            string e5 = Path.Combine(dataRoot, "ai-runtimes", "embeddings-e5");
            bool e5Present = Nonempty(Path.Combine(e5, "model.onnx"), 100_000)
                && Nonempty(Path.Combine(e5, "tokenizer.json"), 1_000);
            string mmarco = Path.Combine(dataRoot, "ai-runtimes", "rerank-mmarco");
            bool mmarcoPresent = Nonempty(Path.Combine(mmarco, "model.onnx"), 100_000)
                && Nonempty(Path.Combine(mmarco, "tokenizer.json"), 1_000);
            return new(cpu, selected != null, embeddings, reranker, selected, e5Present, mmarcoPresent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // An unreadable package is not ready. No exception should crash the first screen.
            return new(false, false, false, false, null);
        }
    }

    private static bool Nonempty(string path, long minimumBytes)
    {
        try { return File.Exists(path) && new FileInfo(path).Length >= minimumBytes; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool SafeGgufName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name == Path.GetFileName(name)
        && !name.Contains('/') && !name.Contains('\\')
        && name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("..", StringComparison.Ordinal)
        && !name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("projector", StringComparison.OrdinalIgnoreCase);

    private static bool PlausibleGguf(string path) =>
        Nonempty(path, 65536) && CorporateModelImportPolicy.HasGgufHeader(path);
}
