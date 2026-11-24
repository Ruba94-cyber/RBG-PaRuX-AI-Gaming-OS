using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaRuX.Server.Gateway;

/// <summary>
/// Pass-through contract for constrained generation on the OpenAI-compatible gateway. The gateway forwards
/// <c>response_format</c> (json_object / json_schema), llama.cpp's native <c>grammar</c> (GBNF) and its native
/// <c>json_schema</c> field to llama-server untouched; this guard only refuses what the runtime would reject
/// anyway or what is ambiguous, with a clear 400 instead of an opaque "failed to parse grammar" from the engine.
/// It never rewrites, "repairs" or drops a constraint: a request either reaches the engine exactly as sent or is refused.
/// </summary>
internal static class ConstrainedOutputGuard
{
    internal const int MaxGrammarCharacters = 64_000;
    internal const int MaxSchemaCharacters = 64_000;
    private const int MaxSchemaDepth = 32;

    /// <summary>Returns null when the constraints (if any) are well-formed, otherwise a user-facing reason.</summary>
    internal static string? Validate(JsonObject request)
    {
        int constraints = 0;

        if (request.TryGetPropertyValue("response_format", out JsonNode? format) && format != null)
        {
            if (format is not JsonObject formatObject) return "\"response_format\" must be an object.";
            if (formatObject["type"] is not JsonValue typeValue || !typeValue.TryGetValue(out string? type))
                return "\"response_format.type\" must be a string.";
            switch (type)
            {
                case "text":
                    break;
                case "json_object":
                    constraints++;
                    if (formatObject.TryGetPropertyValue("schema", out JsonNode? inlineSchema) && inlineSchema != null)
                    {
                        string? error = ValidateSchema(inlineSchema, "response_format.schema");
                        if (error != null) return error;
                    }
                    break;
                case "json_schema":
                    constraints++;
                    if (formatObject["json_schema"] is not JsonObject wrapper)
                        return "\"response_format.json_schema\" must be an object with a \"schema\" object.";
                    if (wrapper["schema"] == null) return "\"response_format.json_schema.schema\" is required.";
                    string? schemaError = ValidateSchema(wrapper["schema"], "response_format.json_schema.schema");
                    if (schemaError != null) return schemaError;
                    break;
                default:
                    return "\"response_format.type\" must be \"text\", \"json_object\" or \"json_schema\".";
            }
        }

        if (request.TryGetPropertyValue("json_schema", out JsonNode? nativeSchema) && nativeSchema != null)
        {
            constraints++;
            string? error = ValidateSchema(nativeSchema, "json_schema");
            if (error != null) return error;
        }

        if (request.TryGetPropertyValue("grammar", out JsonNode? grammar) && grammar != null)
        {
            constraints++;
            if (grammar is not JsonValue grammarValue || !grammarValue.TryGetValue(out string? text))
                return "\"grammar\" must be a GBNF string.";
            if (string.IsNullOrWhiteSpace(text)) return "\"grammar\" must not be empty.";
            if (text.Length > MaxGrammarCharacters)
                return "\"grammar\" exceeds " + MaxGrammarCharacters + " characters.";
            if (!text.Contains("root", StringComparison.Ordinal) || !text.Contains("::=", StringComparison.Ordinal))
                return "\"grammar\" is not GBNF: it needs a \"root ::= ...\" rule.";
        }

        return constraints > 1
            ? "Use only one output constraint per request: response_format (json_object/json_schema), json_schema, or grammar."
            : null;
    }

    private static string? ValidateSchema(JsonNode? schema, string field)
    {
        if (schema is not JsonObject) return "\"" + field + "\" must be a JSON Schema object.";
        if (schema.ToJsonString().Length > MaxSchemaCharacters) return "\"" + field + "\" exceeds " + MaxSchemaCharacters + " characters.";
        return Depth(schema, 0) > MaxSchemaDepth ? "\"" + field + "\" is nested deeper than " + MaxSchemaDepth + " levels." : null;
    }

    private static int Depth(JsonNode? node, int depth)
    {
        if (depth > MaxSchemaDepth) return depth;
        int max = depth;
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> p in obj) max = Math.Max(max, Depth(p.Value, depth + 1));
                break;
            case JsonArray arr:
                foreach (JsonNode? item in arr) max = Math.Max(max, Depth(item, depth + 1));
                break;
        }
        return max;
    }
}
