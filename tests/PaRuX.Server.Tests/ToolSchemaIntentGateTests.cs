using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ToolSchemaIntentGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-tool-gate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("What is a mutex and when should I use one?")]
    [InlineData("Explain recursion in two sentences.")]
    [InlineData("Spiegami la differenza tra TCP e UDP")]
    [InlineData("Translate good morning into French")]
    [InlineData("What is 17 times 23?")]
    [InlineData("The scheduler flushes pending jobs before the next tick.\n\nSummarize the notes above in two sentences.")]
    [InlineData("Primo paragrafo tecnico sulla latenza GPU.\nSecondo paragrafo sulla cache KV.\n\nRiassumi in una frase.")]
    public void Short_plain_questions_under_the_general_skill_omit_the_tool_schemas(string query) =>
        Assert.True(ToolSchemaIntentGate.CanOmitToolSchemas(query, "general"));

    [Fact]
    public void Long_multiline_plain_summary_prompt_omits_tool_schemas()
    {
        string longPrompt = string.Join(" ", Enumerable.Repeat("The allocator compacts dirty pages without blocking readers.", 60))
            + "\n\nSummarize the notes above in exactly two sentences.";
        Assert.True(longPrompt.Length > 1000);
        Assert.True(ToolSchemaIntentGate.CanOmitToolSchemas(longPrompt, "general"));
    }

    [Theory]
    [InlineData("Create a Word document with the meeting minutes")]
    [InlineData("Scrivi un file excel con le spese")]
    [InlineData("Summarize my notes from last week")]
    [InlineData("Cerca nei miei documenti il contratto")]
    [InlineData("Save this as a report")]
    [InlineData("Open C:\\Users\\me\\report.txt")]
    [InlineData("What is in ~/projects/readme.md")]
    [InlineData("Read https://example.com/page and summarize")]
    [InlineData("Export the table to csv")]
    [InlineData("Look at this:\n```\nx=1\n```")]
    [InlineData("Cerca la fattura Rossi")]
    [InlineData("Apri il preventivo")]
    [InlineData("Trova il contratto di Bianchi")]
    [InlineData("Leggi la mail di ieri")]
    [InlineData("Elenca le cartelle")]
    [InlineData("Mostra i miei appunti")]
    [InlineData("Search the Rossi invoice")]
    [InlineData("Find the quote")]
    [InlineData("Open the budget")]
    [InlineData("Generate a report")]
    [InlineData("Show the latest invoice")]
    [InlineData("List the invoices")]
    // Live finding: a one-line request to run code reached the model without the run_python schema.
    [InlineData("Run print(12345*6789) in the Python sandbox with run_python and report the output.")]
    [InlineData("Use the run_python tool to compute 12345*6789")]
    [InlineData("Esegui questo calcolo in Python: 2**64")]
    [InlineData("Execute this script and tell me what it prints")]
    public void Anything_that_can_touch_files_documents_or_the_users_data_keeps_the_tools(string query) =>
        Assert.False(ToolSchemaIntentGate.CanOmitToolSchemas(query, "general"));

    [Fact]
    public void Long_empty_and_other_skill_turns_keep_the_tools()
    {
        Assert.False(ToolSchemaIntentGate.CanOmitToolSchemas(new string('a', ToolSchemaIntentGate.MaximumSimpleTurnCharacters + 1), "general"));
        Assert.False(ToolSchemaIntentGate.CanOmitToolSchemas("", "general"));
        Assert.False(ToolSchemaIntentGate.CanOmitToolSchemas(null, "general"));
        Assert.False(ToolSchemaIntentGate.CanOmitToolSchemas("What is a mutex?", "coding"));
        Assert.False(ToolSchemaIntentGate.CanOmitToolSchemas("What is a mutex?", null));
    }

    private ContextEngineService CreateEngine()
    {
        Directory.CreateDirectory(_root);
        var embeddings = new EmbeddingService(Path.Combine(_root, "embeddings"));
        var memory = new MemoryStore(embeddings, Path.Combine(_root, "memory"));
        var rag = new RagStore(embeddings, Path.Combine(_root, "rag"));
        var graph = new ContextGraphStore(Path.Combine(_root, "graph"));
        var artifacts = new ArtifactStore(Path.Combine(_root, "artifacts"));
        var conversations = new ConversationStore(Path.Combine(_root, "conversations"));
        var internet = new InternetSettingsService(Path.Combine(_root, "internet"));
        return new ContextEngineService(memory, rag, graph, artifacts, conversations, new WebSearchService(internet), internet,
            new McpClientRegistry(Path.Combine(_root, "mcp")), new RepoIndexService(graph));
    }

    [Fact]
    public async Task The_engine_drops_the_built_in_tool_schemas_only_for_the_simple_turn()
    {
        ContextEngineService engine = CreateEngine();
        var general = new SkillManifest
        {
            Id = "general",
            AllowedTools = ["parux_desktop_search", "parux_desktop_read", "parux_write_word_document", "parux_write_excel_workbook", "parux_edit_excel_workbook"]
        };

        ChatContextResult simple = await engine.BuildForChatAsync("What is a mutex?", general, CancellationToken.None);
        Assert.Empty(simple.ToolSchemas);

        ChatContextResult withTools = await engine.BuildForChatAsync("Create a Word document about mutexes", general, CancellationToken.None);
        Assert.NotEmpty(withTools.ToolSchemas);
    }
}
