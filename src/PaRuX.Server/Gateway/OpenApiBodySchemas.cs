using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using PaRuX.Contracts;
using PaRuX.Server.ChatCode;

namespace PaRuX.Server.Gateway;

/// <summary>
/// Request/response body schemas for the routes that are described. Typed bodies are derived from the C# DTO the route
/// really binds (so a renamed property changes the document); the OpenAI-compatible bodies are loosely typed on purpose
/// (they are forwarded to llama-server) and are written by hand. Every route not listed here keeps the plain
/// "body not described" response: nothing is invented for it.
/// </summary>
internal static class OpenApiBodySchemas
{
    internal sealed record BodyDescription(JsonObject? Request, JsonObject? Response, string? Note);

    private static readonly Lazy<IReadOnlyDictionary<string, BodyDescription>> Map = new(Build);

    internal static BodyDescription? Find(string method, string pattern) =>
        Map.Value.TryGetValue(method.ToUpperInvariant() + " " + pattern, out BodyDescription? d) ? d : null;

    internal static int DescribedCount => Map.Value.Count;

    private static JsonObject Typed<T>()
    {
        JsonObject schema = JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions.Web, typeof(T)).AsObject();
        ToOpenApi30(schema);
        return schema;
    }

    /// <summary>The exporter emits JSON Schema 2020-12 ("type":["string","null"]); the document declares OpenAPI 3.0.3, which spells that as nullable.</summary>
    private static void ToOpenApi30(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"] is JsonArray types)
                {
                    var kinds = types.Select(t => t?.GetValue<string>()).Where(t => t != null && t != "null").ToList();
                    if (types.Count != kinds.Count) obj["nullable"] = true;
                    if (kinds.Count == 1) obj["type"] = kinds[0]; else obj.Remove("type");
                }
                obj.Remove("$schema");
                foreach (KeyValuePair<string, JsonNode?> p in obj.ToList()) ToOpenApi30(p.Value);
                break;
            case JsonArray arr:
                foreach (JsonNode? item in arr) ToOpenApi30(item);
                break;
        }
    }

    private static IReadOnlyDictionary<string, BodyDescription> Build()
    {
        JsonObject status = Typed<McpServerStatus>();
        const string mcpNote = "Desktop owner only. The result is a real probe of the server, not an optimistic acknowledgement.";
        return new Dictionary<string, BodyDescription>(StringComparer.Ordinal)
        {
            ["POST /v1/chat/completions"] = new(ChatCompletionRequest(), null,
                "OpenAI-compatible. response_format (json_object/json_schema), json_schema and grammar (GBNF) are forwarded to llama-server unchanged; "
                + "a malformed or contradictory constraint is refused with HTTP 400 before it reaches the engine."),
            ["POST /v1/completions"] = new(Obj(
                    ("model", Str("Model id or alias.")),
                    ("prompt", Str("A non-empty string. Array and token-id prompts are not supported.")),
                    ("max_tokens", Int()), ("temperature", Num()), ("top_p", Num()), ("stop", StopSchema()),
                    ("response_format", ResponseFormatSchema()), ("grammar", Str("llama.cpp GBNF grammar.")), ("json_schema", GenericObject())),
                null, "Legacy text completions; streaming is refused with HTTP 400 (use /v1/chat/completions)."),
            ["POST /api/v1/mcp/servers"] = new(Typed<AddMcpServerRequest>(), Typed<McpServerConfig>(), "Desktop owner only. The credential is stored encrypted and never returned."),
            ["GET /api/v1/mcp/servers"] = new(null, Array(status), mcpNote),
            ["GET /api/v1/mcp/servers/{id}"] = new(null, status, mcpNote),
            ["POST /api/v1/mcp/servers/{id}/start"] = new(null, status, mcpNote + " 409 when the server is disabled."),
            ["POST /api/v1/mcp/servers/{id}/stop"] = new(null, status, "Desktop owner only. The server stays disconnected until started or reloaded; its saved configuration is unchanged."),
            ["POST /api/v1/mcp/servers/{id}/reload"] = new(null, status, mcpNote),
            ["POST /api/v1/mcp/servers/{id}/timeout"] = new(Typed<SetMcpServerTimeoutRequest>(), null,
                "Desktop owner only. timeoutSeconds is 0 (default) or 5 to 300; it bounds both connecting and each tool call."),
            ["POST /api/v1/mcp/servers/{id}/enabled"] = new(Typed<SetMcpServerEnabledRequest>(), null, "Desktop owner only."),
            ["POST /api/v1/mcp/servers/{id}/allowed-tools"] = new(Typed<SetMcpAllowedToolsRequest>(), null, "Desktop owner only. An empty list restores everything the server advertises."),
            ["PUT /api/v1/chat/code/settings"] = new(Typed<ChatCodeSettingsRequest>(), null, "Desktop owner only. Switching the tool on never installs anything; the runtime is installed by an explicit call."),
        };
    }

    private static JsonObject Obj(params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach ((string name, JsonObject schema) in properties) props[name] = schema;
        return new JsonObject { ["type"] = "object", ["properties"] = props };
    }

    private static JsonObject Str(string? description = null) => With("string", description);
    private static JsonObject Int() => new() { ["type"] = "integer" };
    private static JsonObject Num() => new() { ["type"] = "number" };
    private static JsonObject Bool() => new() { ["type"] = "boolean" };
    private static JsonObject GenericObject() => new() { ["type"] = "object", ["additionalProperties"] = true };
    private static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };

    private static JsonObject With(string type, string? description)
    {
        var o = new JsonObject { ["type"] = type };
        if (description != null) o["description"] = description;
        return o;
    }

    private static JsonObject StopSchema() => new()
    {
        ["oneOf"] = new JsonArray(Str(), Array(Str()))
    };

    private static JsonObject ResponseFormatSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("type"),
        ["properties"] = new JsonObject
        {
            ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("text", "json_object", "json_schema") },
            ["json_schema"] = Obj(("name", Str()), ("strict", Bool()), ("schema", GenericObject())),
            ["schema"] = GenericObject()
        },
        ["description"] = "Constrains the answer to JSON (and, for json_schema, to the given JSON Schema). Forwarded to llama-server."
    };

    private static JsonObject ChatCompletionRequest()
    {
        JsonObject message = Obj(("role", Str()), ("content", new JsonObject
        {
            ["description"] = "A string, or an array of content parts (text / image_url).",
            ["oneOf"] = new JsonArray(Str(), Array(GenericObject()))
        }), ("tool_calls", Array(GenericObject())), ("tool_call_id", Str()));
        message["required"] = new JsonArray("role");

        JsonObject schema = Obj(
            ("model", Str("A PaRuX model id or alias (parux-main, parux-auto, parux-code, ...); defaults to parux-main.")),
            ("messages", Array(message)),
            ("stream", Bool()), ("temperature", Num()), ("top_p", Num()), ("max_tokens", Int()), ("max_completion_tokens", Int()),
            ("seed", Int()), ("stop", StopSchema()),
            ("tools", Array(GenericObject())), ("tool_choice", new JsonObject { ["description"] = "none, auto, required or a named function." }),
            ("response_format", ResponseFormatSchema()),
            ("json_schema", new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = true,
                ["description"] = "llama.cpp native JSON Schema constraint. Use only one of response_format, json_schema or grammar."
            }),
            ("grammar", Str("llama.cpp GBNF grammar (must define a root rule; at most 64000 characters). Use only one of response_format, json_schema or grammar.")));
        schema["required"] = new JsonArray("messages");
        schema["additionalProperties"] = true;
        return schema;
    }
}
