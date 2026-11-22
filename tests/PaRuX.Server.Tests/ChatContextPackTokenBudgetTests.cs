using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Offline token accounting for what PaRuX adds to a chat request on top of the user's own text:
/// the context-pack system text and the advertised tool schemas. A plain question must stay near the
/// raw-engine prompt size; retrieval and tool turns must keep exactly the content they had before,
/// and the stable part of the prompt must be byte-identical between requests so the KV prefix cache hits.
/// Token counts use the product's own <see cref="ConversationWindowPlanner.EstimateTokens"/>.
/// </summary>
public sealed class ChatContextPackTokenBudgetTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public ChatContextPackTokenBudgetTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>Acceptance ceiling from the lean-context-pack slice: system text added to a plain chat.</summary>
    private const int PlainChatInjectedTokenCeiling = 150;

    private static readonly string[] GeneralTools =
        ["parux_desktop_search", "parux_desktop_read", "parux_write_word_document", "parux_write_excel_workbook", "parux_edit_excel_workbook"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-pack-budget-" + Guid.NewGuid().ToString("N"));
    private McpClientRegistry? _mcp;

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private ContextEngineService CreateEngine(out MemoryStore memory)
    {
        Directory.CreateDirectory(_root);
        var embeddings = new EmbeddingService(Path.Combine(_root, "embeddings"));
        memory = new MemoryStore(embeddings, Path.Combine(_root, "memory"));
        var rag = new RagStore(embeddings, Path.Combine(_root, "rag"));
        var graph = new ContextGraphStore(Path.Combine(_root, "graph"));
        var artifacts = new ArtifactStore(Path.Combine(_root, "artifacts"));
        var conversations = new ConversationStore(Path.Combine(_root, "conversations"));
        var internet = new InternetSettingsService(Path.Combine(_root, "internet"));
        _mcp = new McpClientRegistry(Path.Combine(_root, "mcp"));
        return new ContextEngineService(memory, rag, graph, artifacts, conversations, new WebSearchService(internet), internet,
            _mcp, new RepoIndexService(graph));
    }

    private static SkillManifest General() => new() { Id = "general", AllowedTools = GeneralTools.ToList() };

    private static int Tokens(string? text) => string.IsNullOrEmpty(text) ? 0 : ConversationWindowPlanner.EstimateTokens(text);

    private static int Tokens(JsonArray schemas) => schemas.Count == 0 ? 0 : Tokens(schemas.ToJsonString());

    private static ChatCompletionRequest Request(string user, string? system = null)
    {
        var request = new ChatCompletionRequest();
        if (system != null) request.Messages.Add(new ChatCompletionMessage { Role = "system", Content = system });
        request.Messages.Add(new ChatCompletionMessage { Role = "user", Content = user });
        return request;
    }

    [Fact]
    public async Task A_plain_chat_adds_under_the_ceiling_of_system_and_tool_tokens()
    {
        ContextEngineService engine = CreateEngine(out _);
        const string question = "What is a mutex and when should I use one?";

        ChatContextResult result = await engine.BuildForChatAsync(question, General(), CancellationToken.None);
        ChatCompletionRequest request = Request(question);
        ChatProxyService.ApplyContextPack(request, result.Pack);

        int systemTokens = request.Messages.Where(m => m.Role == "system").Sum(m => Tokens(m.Content));
        int toolTokens = Tokens(result.ToolSchemas);
        Assert.True(systemTokens + toolTokens <= PlainChatInjectedTokenCeiling,
            $"plain chat injected {systemTokens} system + {toolTokens} tool tokens, ceiling {PlainChatInjectedTokenCeiling}");
        Assert.Empty(result.ToolSchemas);
        Assert.Empty(result.Pack.Items);
    }

    [Fact]
    public async Task A_tool_turn_keeps_the_full_schema_set_and_a_plain_turn_saves_at_least_five_hundred_tokens()
    {
        ContextEngineService engine = CreateEngine(out _);

        ChatContextResult plain = await engine.BuildForChatAsync("What is a mutex?", General(), CancellationToken.None);
        ChatContextResult tool = await engine.BuildForChatAsync("Create a Word document about mutexes", General(), CancellationToken.None);

        // Unchanged content: the tool turn advertises exactly what the registry offers the skill.
        JsonArray expected = await _mcp!.GetToolSchemasAsync(CancellationToken.None, GeneralTools);
        Assert.NotEmpty(tool.ToolSchemas);
        Assert.Equal(expected.ToJsonString(), tool.ToolSchemas.ToJsonString());

        _out.WriteLine($"MEASURED tool-turn schema tokens={Tokens(tool.ToolSchemas)} plain-turn={Tokens(plain.ToolSchemas)} tools={tool.ToolSchemas.Count}");
        int saved = Tokens(tool.ToolSchemas) - Tokens(plain.ToolSchemas);
        Assert.True(saved >= 500, $"plain turn saved only {saved} tokens versus a tool turn");
    }

    [Fact]
    public async Task A_retrieval_turn_keeps_the_memory_text_unchanged_and_an_unrelated_turn_adds_nothing()
    {
        ContextEngineService engine = CreateEngine(out MemoryStore memory);
        const string fact = "The release build command is `make release`, never `make all`.";
        memory.Add(new MemoryAddRequest { Scope = "User", Content = fact });

        ChatContextResult withMemory = await engine.BuildForChatAsync(
            "What is the release build command?", General(), CancellationToken.None);
        Assert.Contains(fact, withMemory.Pack.PromptText, StringComparison.Ordinal);
        Assert.Contains(withMemory.Pack.Items, item => item.Kind == "memory");

        ChatContextResult unrelated = await engine.BuildForChatAsync(
            "Spiegami la differenza tra TCP e UDP", General(), CancellationToken.None);
        Assert.Empty(unrelated.Pack.PromptText);
    }

    [Fact]
    public async Task The_system_prefix_is_byte_identical_between_plain_requests()
    {
        ContextEngineService engine = CreateEngine(out _);
        const string system = "You are a helpful assistant.";

        string Prefix(ChatCompletionRequest r) => System.Text.Json.JsonSerializer.Serialize(
            r.Messages.TakeWhile(m => m.Role == "system").ToList());

        var first = Request("What is a mutex?", system);
        ChatProxyService.ApplyContextPack(first, (await engine.BuildForChatAsync("What is a mutex?", General(), CancellationToken.None)).Pack);
        var second = Request("Explain recursion in two sentences.", system);
        ChatProxyService.ApplyContextPack(second, (await engine.BuildForChatAsync("Explain recursion in two sentences.", General(), CancellationToken.None)).Pack);

        Assert.Equal(Prefix(first), Prefix(second));
        Assert.Equal(system, first.Messages[0].Content);
    }

    [Fact]
    public async Task Tool_schemas_and_pack_text_are_byte_stable_for_identical_inputs()
    {
        ContextEngineService engine = CreateEngine(out MemoryStore memory);
        memory.Add(new MemoryAddRequest { Scope = "User", Content = "The release build command is `make release`." });
        const string query = "Create a Word document with the release build command";

        ChatContextResult a = await engine.BuildForChatAsync(query, General(), CancellationToken.None);
        ChatContextResult b = await engine.BuildForChatAsync(query, General(), CancellationToken.None);

        Assert.Equal(a.ToolSchemas.ToJsonString(), b.ToolSchemas.ToJsonString());
        Assert.Equal(a.Pack.PromptText, b.Pack.PromptText);
    }
}
