using System;
using System.IO;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CorporateDocumentRuntimePresentationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-corp-doc-pres-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void Embedding_loaded_always_reports_active_and_hides_install_button(
        bool corporate, bool allowsExecutableDownloads, bool italian)
    {
        var status = new EmbeddingEngineStatus { Installed = true, Loaded = true, Dimensions = 384 };
        EmbeddingEngineStatusPresentation result = EmbeddingEngineStatusPresenter.Present(
            status, corporate, allowsExecutableDownloads, italian);

        Assert.False(result.ShowInstallButton);
        Assert.Contains("384-dim", result.Text, StringComparison.Ordinal);
        Assert.Contains(italian ? "Installato e attivo" : "Installed and active", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void Embedding_installed_and_idle_unloaded_without_error_reports_ready_on_demand(
        bool corporate, bool allowsExecutableDownloads, bool italian)
    {
        var status = new EmbeddingEngineStatus { Installed = true, Loaded = false, Dimensions = 384, LastError = null };
        EmbeddingEngineStatusPresentation result = EmbeddingEngineStatusPresenter.Present(
            status, corporate, allowsExecutableDownloads, italian);

        Assert.False(result.ShowInstallButton);
        Assert.Contains("384-dim", result.Text, StringComparison.Ordinal);
        Assert.Contains(italian ? "Installato e pronto su richiesta" : "Installed and ready on demand", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(italian ? "caricamento non riuscito" : "failed to load", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, true)]
    public void Embedding_installed_with_load_error_shows_error_and_hides_install_in_corporate_or_store(
        bool corporate, bool allowsExecutableDownloads, bool italian, bool expectedButton)
    {
        var status = new EmbeddingEngineStatus
        {
            Installed = true,
            Loaded = false,
            Dimensions = 384,
            LastError = "onnxruntime native init failed"
        };
        EmbeddingEngineStatusPresentation result = EmbeddingEngineStatusPresenter.Present(
            status, corporate, allowsExecutableDownloads, italian);

        Assert.Equal(expectedButton, result.ShowInstallButton);
        Assert.Contains("onnxruntime native init failed", result.Text, StringComparison.Ordinal);
        Assert.Contains(italian ? "Installato ma caricamento non riuscito: " : "Installed but failed to load: ", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Embedding_not_installed_in_corporate_explains_ai_runtimes_and_never_shows_download(bool italian)
    {
        var status = new EmbeddingEngineStatus { Installed = false, Loaded = false };
        EmbeddingEngineStatusPresentation result = EmbeddingEngineStatusPresenter.Present(
            status, corporate: true, allowsExecutableDownloads: true, italian);

        Assert.False(result.ShowInstallButton);
        Assert.Contains("ai-runtimes", result.Text, StringComparison.Ordinal);
        Assert.Contains("Corporate", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, false, "timeout")]
    [InlineData(true, true, null)]
    [InlineData(false, true, "network error")]
    public void Embedding_not_installed_in_standard_respects_store_downloads_and_shows_last_install_failure(
        bool allowsExecutableDownloads, bool italian, string? failure)
    {
        var status = new EmbeddingEngineStatus { Installed = false, Loaded = false };
        EmbeddingEngineStatusPresentation result = EmbeddingEngineStatusPresenter.Present(
            status, corporate: false, allowsExecutableDownloads, italian, failure);

        Assert.Equal(allowsExecutableDownloads, result.ShowInstallButton);
        Assert.Contains(italian ? "Non installato" : "Not installed", result.Text, StringComparison.Ordinal);
        if (failure is not null)
            Assert.Contains(failure, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Offline_readiness_detects_multilingual_e5_and_mmarco_assets_alongside_legacy_fallback()
    {
        string root = Path.Combine(_root, "full-corp");
        string cpu = Path.Combine(root, "LocalAI", "runtime", "llama.cpp-b1-cpu");
        Directory.CreateDirectory(cpu);
        File.WriteAllBytes(Path.Combine(cpu, "llama-server.exe"), new byte[8192]);

        string models = Path.Combine(root, "models");
        Directory.CreateDirectory(models);
        string gguf = Path.Combine(models, "starter.gguf");
        byte[] ggufBytes = new byte[70_000];
        ggufBytes[0] = (byte)'G'; ggufBytes[1] = (byte)'G'; ggufBytes[2] = (byte)'U'; ggufBytes[3] = (byte)'F';
        File.WriteAllBytes(gguf, ggufBytes);

        // Multilingual E5 + mmarco only (no MiniLM / legacy reranker yet).
        string e5 = Path.Combine(root, "ai-runtimes", "embeddings-e5");
        Directory.CreateDirectory(e5);
        File.WriteAllBytes(Path.Combine(e5, "model.onnx"), new byte[120_000]);
        File.WriteAllBytes(Path.Combine(e5, "tokenizer.json"), new byte[2_000]);

        string mmarco = Path.Combine(root, "ai-runtimes", "rerank-mmarco");
        Directory.CreateDirectory(mmarco);
        File.WriteAllBytes(Path.Combine(mmarco, "model.onnx"), new byte[120_000]);
        File.WriteAllBytes(Path.Combine(mmarco, "tokenizer.json"), new byte[2_000]);

        CorporateOfflineReadiness multilingualOnly = CorporateOfflineReadiness.Inspect(root);
        Assert.True(multilingualOnly.HasChatAssets);
        Assert.True(multilingualOnly.HasDocumentAssets);
        Assert.False(multilingualOnly.EmbeddingPresent);
        Assert.False(multilingualOnly.RerankerPresent);
        Assert.True(multilingualOnly.MultilingualEmbeddingPresent);
        Assert.True(multilingualOnly.MultilingualRerankerPresent);

        string en = multilingualOnly.ToStatusText(false);
        string it = multilingualOnly.ToStatusText(true);
        Assert.Contains("not a functional test", en, StringComparison.Ordinal);
        Assert.Contains("multilingual E5", en, StringComparison.Ordinal);
        Assert.Contains("multilingual mmarco", en, StringComparison.Ordinal);
        Assert.DoesNotContain("MISSING", en, StringComparison.Ordinal);
        Assert.Contains("multilingue E5", it, StringComparison.Ordinal);
        Assert.Contains("multilingue mmarco", it, StringComparison.Ordinal);
        Assert.DoesNotContain("MANCANTE", it, StringComparison.Ordinal);

        // Add MiniLM + legacy reranker fallback too.
        string emb = Path.Combine(root, "ai-runtimes", "embeddings");
        Directory.CreateDirectory(emb);
        File.WriteAllBytes(Path.Combine(emb, "model.onnx"), new byte[120_000]);
        File.WriteAllBytes(Path.Combine(emb, "vocab.txt"), new byte[2_000]);
        File.WriteAllText(Path.Combine(emb, "config.json"), "{}");
        File.WriteAllText(Path.Combine(emb, "tokenizer_config.json"), "{}");

        string rer = Path.Combine(root, "ai-runtimes", "reranker");
        Directory.CreateDirectory(rer);
        File.WriteAllBytes(Path.Combine(rer, "model.onnx"), new byte[120_000]);
        File.WriteAllBytes(Path.Combine(rer, "vocab.txt"), new byte[2_000]);

        CorporateOfflineReadiness full = CorporateOfflineReadiness.Inspect(root);
        Assert.True(full.EmbeddingPresent);
        Assert.True(full.RerankerPresent);
        Assert.True(full.MultilingualEmbeddingPresent);
        Assert.True(full.MultilingualRerankerPresent);
        Assert.Contains("multilingual E5 + MiniLM", full.ToStatusText(false), StringComparison.Ordinal);
        Assert.Contains("multilingue mmarco + fallback", full.ToStatusText(true), StringComparison.Ordinal);
    }

    [Fact]
    public void Knowledge_retrieval_text_formats_corporate_and_italian_strings_truthfully()
    {
        Assert.Equal("intfloat/multilingual-e5-small (384) (ready on demand)",
            KnowledgeRetrievalText.FormatEmbedderOption("intfloat/multilingual-e5-small", 384, installed: true, corporate: true, italian: false));
        Assert.Equal("intfloat/multilingual-e5-small (384) (pronto su richiesta)",
            KnowledgeRetrievalText.FormatEmbedderOption("intfloat/multilingual-e5-small", 384, installed: true, corporate: true, italian: true));
        Assert.Equal("intfloat/multilingual-e5-small (384) - not included in data folder",
            KnowledgeRetrievalText.FormatEmbedderOption("intfloat/multilingual-e5-small", 384, installed: false, corporate: true, italian: false));
        Assert.Equal("intfloat/multilingual-e5-small (384) - non incluso nella cartella dati",
            KnowledgeRetrievalText.FormatEmbedderOption("intfloat/multilingual-e5-small", 384, installed: false, corporate: true, italian: true));
        Assert.Equal("intfloat/multilingual-e5-small (384) - non installato",
            KnowledgeRetrievalText.FormatEmbedderOption("intfloat/multilingual-e5-small", 384, installed: false, corporate: false, italian: true));

        Assert.Equal(" · local reranker ready", KnowledgeRetrievalText.FormatRerankerNote(true, true, false));
        Assert.Equal(" · riordino locale pronto", KnowledgeRetrievalText.FormatRerankerNote(true, true, true));
        Assert.Equal(" · local reranker not included", KnowledgeRetrievalText.FormatRerankerNote(false, true, false));
        Assert.Equal(" · riordino locale non incluso", KnowledgeRetrievalText.FormatRerankerNote(false, true, true));
        Assert.Equal(" · riordino locale non installato", KnowledgeRetrievalText.FormatRerankerNote(false, false, true));

        string localizedMissingIt = KnowledgeRetrievalText.LocalizeDegradedReason(
            KnowledgeRetrievalText.MissingEmbedderEnglishReason, corporate: true, italian: true);
        Assert.Contains("cartella dati", localizedMissingIt, StringComparison.Ordinal);

        string localizedReembedIt = KnowledgeRetrievalText.LocalizeDegradedReason(
            KnowledgeRetrievalText.MissingVectorsEnglishReason, corporate: true, italian: true);
        Assert.Contains("rigenera i vettori", localizedReembedIt, StringComparison.Ordinal);

        string corpMissingWebEn = KnowledgeRetrievalText.DescribeWebRetrieval(
            "auto", "keyword", null, offlineBlocked: true, corporate: true, italian: false);
        Assert.Contains("ai-runtimes", corpMissingWebEn, StringComparison.Ordinal);
        Assert.DoesNotContain("strict offline mode blocks installing", corpMissingWebEn, StringComparison.OrdinalIgnoreCase);

        string corpHybridIt = KnowledgeRetrievalText.DescribeWebRetrieval(
            "auto", "hybrid", "intfloat/multilingual-e5-small", offlineBlocked: true, corporate: true, italian: true);
        Assert.Contains("intfloat/multilingual-e5-small", corpHybridIt, StringComparison.Ordinal);
        Assert.Contains("ibrida", corpHybridIt, StringComparison.OrdinalIgnoreCase);
    }
}
