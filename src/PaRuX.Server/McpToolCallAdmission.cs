using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>
/// Conservative, source-only preflight for model-generated MCP calls. Tool names must match
/// the exact advertised schema; no guessed names, invented fields, or truncated strings.
/// Only syntactic JSON repairs (a Markdown fence or a trailing comma) are permitted.
/// All existing caller/owner/Gaming permissions still apply in the tool implementation.
/// </summary>
internal static class McpToolCallAdmission
{
    internal const int MaxArgumentsCharacters = 64 * 1024;

    internal sealed record Result(bool Allowed, string ArgumentsJson, string? Error);

    internal static HashSet<string> AdvertisedNames(JsonArray schemas)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? entry in schemas)
        {
            if (entry is not JsonObject obj || obj["function"] is not JsonObject fn) continue;
            if (fn["name"] is JsonValue nameValue &&
                nameValue.TryGetValue(out string? name) &&
                !string.IsNullOrWhiteSpace(name) && name.Length <= 128)
                names.Add(name);
        }
        return names;
    }

    internal static Result Inspect(string? toolName, string? arguments, IReadOnlySet<string> advertised)
    {
        if (string.IsNullOrWhiteSpace(toolName) || !advertised.Contains(toolName))
            return Deny("The requested tool is not in this turn's advertised tool list.");

        string raw = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments.Trim();
        if (raw.Length > MaxArgumentsCharacters)
            return Deny("The tool arguments exceed the 64 KiB size limit.");

        string text = raw;
        bool fenced = false;
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int newline = text.IndexOf('\n');
            if (newline <= 0 || !text.EndsWith("```", StringComparison.Ordinal))
                return Deny("The JSON code fence is incomplete.");
            string language = text[..newline].Trim();
            if (!language.Equals("```", StringComparison.Ordinal) &&
                !language.Equals("```json", StringComparison.OrdinalIgnoreCase))
                return Deny("The tool arguments are not a JSON code block.");
            text = text[(newline + 1)..^3].Trim();
            fenced = true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Deny("The tool arguments must be a JSON object.");

            // Duplicate top-level properties have ambiguous meanings across providers.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name))
                    return Deny("Duplicate argument keys are not allowed.");

            // JsonElement serialization removes trailing commas without interpreting or
            // inventing the values. Do not rewrite already-valid JSON at all.
            if (!fenced)
            {
                try
                {
                    using JsonDocument strict = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
                    return new Result(true, raw, null);
                }
                catch (JsonException) { }
            }
            return new Result(true, JsonSerializer.Serialize(document.RootElement), null);
        }
        catch (JsonException)
        {
            return Deny("The tool arguments are not complete, valid JSON; provide one complete object.");
        }
    }

    private static Result Deny(string reason) => new(false, "{}", reason);
}
