using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.DesktopIntegration;

namespace PaRuX.Server.AgentSkills;

/// <summary>
/// Built-in tools behind the "sql-read", "charts" and "document-writer" skills. Lives in its own file so the tool-call
/// loop is untouched: <see cref="McpClientRegistry"/> only asks <see cref="IsBuiltInTool"/>, adds
/// <see cref="GetToolSchemas"/> and forwards <see cref="CallAsync"/>. Every failure is returned as text beginning with
/// <see cref="FailurePrefix"/> so the model sees an honest error instead of an exception.
/// </summary>
public sealed class AgentSkillTools
{
    public const string SqlToolName = "parux_sql_query";
    public const string ChartToolName = "parux_make_chart";
    public const string PresentationToolName = "parux_write_presentation";
    public const string ChatSearchToolName = "parux_search_chats";
    public const string ProposeSettingToolName = "parux_propose_setting";
    public const string FailurePrefix = "Agent skill tool failed:";
    public const int MaxSlides = 50;
    public const int MaxBulletsPerSlide = 12;
    public const int MaxTextCharacters = 600;

    private readonly ReadOnlySqlQueryService _sql;
    private readonly DocumentWriteService _documents;
    private readonly ArtifactStore? _artifacts;
    private readonly ConversationStore? _conversations;
    private readonly ChatSettingProposalService? _settingProposals;

    public AgentSkillTools(ReadOnlySqlQueryService sql, DocumentWriteService documents, ArtifactStore? artifacts = null, ConversationStore? conversations = null,
        ChatSettingProposalService? settingProposals = null)
    {
        _settingProposals = settingProposals;
        _conversations = conversations;
        _sql = sql;
        _documents = documents;
        _artifacts = artifacts;
    }

    public static bool IsBuiltInTool(string? name) =>
        name is SqlToolName or ChartToolName or PresentationToolName or ChatSearchToolName or ProposeSettingToolName;

    public JsonArray GetToolSchemas(IReadOnlyCollection<string>? allowedTools = null)
    {
        var schemas = new JsonArray();
        // The SQL tool is only offered when the owner attached at least one data file; there is nothing to query otherwise.
        if (ContextEngineService.ToolNameAllowed(SqlToolName, allowedTools) && _sql.Sources.List().Count > 0)
            schemas.Add(Function(SqlToolName,
                "Run ONE read-only SELECT over a SQLite or CSV file the user attached. Omit sql to list tables and columns. A CSV is the table \"data\". Results are capped; write statements are refused.",
                new JsonObject
                {
                    ["source"] = new JsonObject { ["type"] = "string", ["description"] = "File name of an attached data source, e.g. sales.csv" },
                    ["sql"] = new JsonObject { ["type"] = "string", ["description"] = "A single SELECT, WITH ... SELECT, or EXPLAIN SELECT statement" },
                    ["maxRows"] = new JsonObject { ["type"] = "integer", ["description"] = "Row cap, at most " + ReadOnlySqlQueryService.HardMaxRows }
                }, "source"));
        if (ContextEngineService.ToolNameAllowed(ChartToolName, allowedTools))
            schemas.Add(Function(ChartToolName,
                "Draw a bar, line, scatter or pie chart as an SVG file saved in PaRuX's documents folder and the Artifact Workspace. Returns the saved path.",
                new JsonObject
                {
                    ["fileName"] = new JsonObject { ["type"] = "string" },
                    ["type"] = new JsonObject { ["type"] = "string", ["description"] = "bar | line | scatter | pie" },
                    ["title"] = new JsonObject { ["type"] = "string" },
                    ["labels"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                    ["series"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["name"] = new JsonObject { ["type"] = "string" },
                                ["values"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" } }
                            }
                        }
                    }
                }, "fileName", "type", "labels", "series"));
        if (ContextEngineService.ToolNameAllowed(PresentationToolName, allowedTools))
            schemas.Add(Function(PresentationToolName,
                "Create a PowerPoint (.pptx) from slides you already wrote, saved locally. An existing file is never overwritten.",
                new JsonObject
                {
                    ["fileName"] = new JsonObject { ["type"] = "string" },
                    ["title"] = new JsonObject { ["type"] = "string" },
                    ["slides"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["title"] = new JsonObject { ["type"] = "string" },
                                ["bullets"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                                ["notes"] = new JsonObject { ["type"] = "string" }
                            }
                        }
                    }
                }, "fileName", "slides"));
        // Offered only when the prompt asks about earlier chats (ToolSchemaIntentGate.KeepChatSearchSchema), so ordinary turns carry no extra schema.
        if (_conversations != null && ContextEngineService.ToolNameAllowed(ChatSearchToolName, allowedTools))
            schemas.Add(Function(ChatSearchToolName,
                "Search the user's OWN saved earlier chats on this PC for what was said before. Read-only. Searches the current project by default; set allProjects to true only when the user asks for every project. Returns short snippets with chat title, date and id.",
                new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Words to look for in earlier chats" },
                    ["allProjects"] = new JsonObject { ["type"] = "boolean", ["description"] = "Search every project instead of only the current one (default false)" },
                    ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum chats to return, at most " + ChatHistorySearch.MaxLimit }
                }, "query"));
        // Offered only when the prompt asks to change a setting. It can only PROPOSE: the owner confirms on a card before anything changes.
        if (_settingProposals != null && ContextEngineService.ToolNameAllowed(ProposeSettingToolName, allowedTools))
            schemas.Add(Function(ProposeSettingToolName,
                "Propose ONE change to a PaRuX setting. Nothing changes until the user presses Apply on a confirmation card, so tell them to confirm. Allowed settings: "
                + string.Join("; ", ChatSettingsAllowList.All.Select(d => d.Key + " = " + string.Join("|", d.Values))) + ". Any other setting is refused.",
                new JsonObject
                {
                    ["setting"] = new JsonObject { ["type"] = "string", ["description"] = "One of: " + string.Join(", ", ChatSettingsAllowList.All.Select(d => d.Key)) },
                    ["value"] = new JsonObject { ["type"] = "string", ["description"] = "The new value" }
                }, "setting", "value"));
        return schemas;
    }

    public Task<string> CallAsync(string toolName, string argumentsJson, CancellationToken ct, ChatHistoryScope? historyScope = null)
    {
        if (!IsBuiltInTool(toolName)) throw new InvalidOperationException("Unknown PaRuX agent skill tool.");
        ct.ThrowIfCancellationRequested();
        JsonObject? root;
        try { root = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject; }
        catch (JsonException) { return Task.FromResult(FailurePrefix + " the arguments were not valid JSON."); }
        if (root == null) return Task.FromResult(FailurePrefix + " the arguments must be a JSON object.");

        try
        {
            return Task.FromResult(toolName switch
            {
                SqlToolName => RunSql(root, ct),
                ChartToolName => RunChart(root),
                ChatSearchToolName => RunChatSearch(root, historyScope),
                ProposeSettingToolName => RunProposeSetting(root, historyScope),
                _ => RunPresentation(root)
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException ex) { return Task.FromResult(FailurePrefix + " " + ex.Message); }
        catch (InvalidDataException ex) { return Task.FromResult(FailurePrefix + " " + ex.Message); }
        catch (InvalidOperationException ex) { return Task.FromResult(FailurePrefix + " " + ex.Message); }
        catch (JsonException) { return Task.FromResult(FailurePrefix + " an argument had the wrong type."); }
        catch (IOException ex) { return Task.FromResult(FailurePrefix + " " + ex.Message); }
        catch (UnauthorizedAccessException) { return Task.FromResult(FailurePrefix + " PaRuX cannot write to the documents folder."); }
    }

    private string RunSql(JsonObject args, CancellationToken ct)
    {
        string source = Str(args, "source");
        string sql = Str(args, "sql");
        if (string.IsNullOrWhiteSpace(sql)) return _sql.DescribeSchema(source, ct);
        int maxRows = args["maxRows"] is JsonValue v && v.TryGetValue(out int n) ? n : 0;
        return ReadOnlySqlQueryService.ToJson(_sql.Query(source, sql, maxRows, null, ct));
    }

    private string RunProposeSetting(JsonObject args, ChatHistoryScope? scope)
    {
        if (_settingProposals == null || scope == null || string.IsNullOrWhiteSpace(scope.ConversationId))
            throw new InvalidOperationException("Changing settings from chat is not available for this request.");
        if (!ChatSettingsAllowList.TryValidate(Str(args, "setting"), Str(args, "value"), PaRuX.Contracts.CorporateMode.IsActive, out ChatSettingChange? change, out string error))
            throw new InvalidOperationException(error);
        _settingProposals.Propose(scope.ConversationId!, change!);
        return "Proposed: " + change!.Description + " -> " + change.Value + ". NOTHING has changed yet: a confirmation card with Apply and Cancel is now shown to the user. Tell them to press Apply to confirm.";
    }

    private string RunChatSearch(JsonObject args, ChatHistoryScope? scope)
    {
        // Without a scope the caller is not the desktop owner on a local engine (LAN client, cloud provider): history stays private.
        if (_conversations == null || scope == null) throw new InvalidOperationException("Searching earlier chats is not available for this request.");
        bool all = args["allProjects"] is JsonValue a && a.TryGetValue(out bool allValue) && allValue;
        int limit = args["limit"] is JsonValue v && v.TryGetValue(out int n) ? n : ChatHistorySearch.DefaultLimit;
        var documents = _conversations.List()
            .OrderByDescending(c => c.UpdatedUtc).Take(ChatHistorySearch.MaxScannedConversations)
            .Select(c => _conversations.Get(c.Id)).OfType<ConversationDocument>();
        IReadOnlyList<ChatHistoryHit> hits = ChatHistorySearch.Search(documents,
            new ChatHistorySearchRequest(Str(args, "query"), scope, all, limit, PaRuX.Contracts.CorporateMode.IsActive));
        return ChatHistorySearch.Format(hits, all);
    }

    private string RunChart(JsonObject args)
    {
        var spec = new ChartSpec
        {
            Type = Str(args, "type"),
            Title = Str(args, "title"),
            Labels = (args["labels"] as JsonArray)?.Select(l => l?.ToString() ?? string.Empty).ToList() ?? new List<string>(),
            Series = (args["series"] as JsonArray)?.OfType<JsonObject>().Select(s => new ChartSeries
            {
                Name = Str(s, "name"),
                Values = (s["values"] as JsonArray)?.Select(x => x is JsonValue jv && jv.TryGetValue(out double d) ? d : double.NaN).ToList() ?? new List<double>()
            }).ToList() ?? new List<ChartSeries>()
        };
        string svg = ChartRenderer.RenderSvg(spec);
        string name = Str(args, "fileName");
        string baseName = name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        DocumentWriteResult saved = _documents.PublishNew(baseName, ".svg", System.Text.Encoding.UTF8.GetBytes(svg));
        string artifactNote = string.Empty;
        if (_artifacts != null)
        {
            try
            {
                _artifacts.Create(new CreateArtifactRequest
                {
                    ArtifactType = "chart",
                    Title = string.IsNullOrWhiteSpace(spec.Title) ? saved.FileName : spec.Title,
                    ContentRef = saved.Path,
                    Source = "skill:charts"
                });
                artifactNote = " It is also listed in the Artifact Workspace.";
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                artifactNote = " It could not be added to the Artifact Workspace.";
            }
        }
        return "Saved chart " + saved.FileName + " (" + saved.SizeBytes + " bytes) to " + saved.Path + "." + artifactNote + " Tell the user this exact path.";
    }

    private string RunPresentation(JsonObject args)
    {
        if (args["slides"] is not JsonArray slideNodes || slideNodes.Count == 0) throw new InvalidDataException("slides is required.");
        if (slideNodes.Count > MaxSlides) throw new InvalidDataException("At most " + MaxSlides + " slides are allowed.");
        var slides = new List<PresentationSlideDto>();
        foreach (JsonObject slide in slideNodes.OfType<JsonObject>())
            slides.Add(new PresentationSlideDto
            {
                Title = Clip(Str(slide, "title")),
                Bullets = (slide["bullets"] as JsonArray)?.Take(MaxBulletsPerSlide).Select(b => Clip(b?.ToString() ?? string.Empty)).ToList() ?? new List<string>(),
                SpeakerNotes = Clip(Str(slide, "notes"))
            });
        if (slides.Count == 0) throw new InvalidDataException("slides must be objects with a title and bullets.");
        byte[] bytes = PresentationService.BuildPptx(Clip(Str(args, "title")), slides);
        string name = Str(args, "fileName");
        string baseName = name.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        DocumentWriteResult saved = _documents.PublishNew(baseName, ".pptx", bytes);
        return "Saved " + saved.FileName + " (" + saved.SizeBytes + " bytes, " + slides.Count + " slides) to " + saved.Path + ". Tell the user this exact path.";
    }

    private static string Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? (s ?? string.Empty).Trim() : string.Empty;
    private static string Clip(string s) => s.Length <= MaxTextCharacters ? s : s[..MaxTextCharacters];

    private static JsonObject Function(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray())
            }
        }
    };
}
