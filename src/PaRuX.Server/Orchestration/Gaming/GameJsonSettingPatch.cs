using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Previews ONE existing scalar JSON configuration property for an owner-approved,
/// paused, PaRuX-managed offline game. This pure helper never reads/writes a file.
/// Missing/duplicate paths, arrays/objects and type-changing edits fail closed.
/// The full before/after text remains subject to explicit owner consent and rollback
/// in TemporaryGameModService, exactly as for INI/CFG proposals.
/// </summary>
internal static class GameJsonSettingPatch
{
    private const int MaxTextBytes = 64 * 1024;
    private const int MaxValueLength = 256;

    private static bool Identifier(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 80 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static JsonElement UniqueProperty(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A JSON property parent must be an existing object.");
        int matches = 0;
        JsonElement found = default;
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            matches++;
            found = property.Value;
            if (matches > 1) break;
        }
        if (matches != 1)
            throw new ArgumentException("The requested JSON property is missing or duplicated.");
        return found;
    }

    private static bool Scalar(JsonValueKind kind) =>
        kind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True
            or JsonValueKind.False or JsonValueKind.Null;

    private static bool IsBoolean(JsonValueKind kind) =>
        kind is JsonValueKind.True or JsonValueKind.False;

    private static bool SameType(JsonValueKind a, JsonValueKind b) =>
        a == b || (IsBoolean(a) && IsBoolean(b));

    // JsonNode serialization would silently collapse duplicate keys anywhere in
    // the original document. Reject the file instead of altering unrelated data.
    private static void RequireUniqueKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in element.EnumerateObject())
            {
                if (!names.Add(p.Name))
                    throw new ArgumentException("The JSON file contains duplicate property names.");
                RequireUniqueKeys(p.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
                RequireUniqueKeys(child);
        }
    }

    internal static string Apply(string original, string? section, string? key, string? value)
    {
        if (original is null || Encoding.UTF8.GetByteCount(original) > MaxTextBytes)
            throw new ArgumentException("The JSON game configuration is too large.");
        if (!Identifier(key) || section is null || section.Length > 80)
            throw new ArgumentException("The JSON object path and key must use simple ASCII identifiers.");
        string[] parents = section.Length == 0 ? Array.Empty<string>() : section.Split('.');
        if (parents.Length > 4 || parents.Any(p => !Identifier(p)))
            throw new ArgumentException("Use up to four existing nested JSON object names separated by dots.");
        if (value is null || value.Length is < 1 or > MaxValueLength || value.Any(char.IsControl))
            throw new ArgumentException("Provide a JSON scalar literal of at most 256 characters on one line.");

        JsonValueKind replacementType;
        try
        {
            using JsonDocument replacement = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 8 });
            replacementType = replacement.RootElement.ValueKind;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The new value must be a valid JSON scalar literal (number, bool, quoted string or null).", ex);
        }
        if (!Scalar(replacementType))
            throw new ArgumentException("JSON object and array replacement is not supported by the safe one-key patch.");

        JsonNode node;
        using (JsonDocument doc = JsonDocument.Parse(original, new JsonDocumentOptions { MaxDepth = 32 }))
        {
            JsonElement current = doc.RootElement;
            RequireUniqueKeys(current);
            if (current.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("The game's JSON configuration must be an object.");
            foreach (string parent in parents)
            {
                current = UniqueProperty(current, parent);
                if (current.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("The JSON setting parent is not an object.");
            }
            JsonElement existing = UniqueProperty(current, key!);
            if (!Scalar(existing.ValueKind) ||
                (existing.ValueKind != JsonValueKind.Null && replacementType != JsonValueKind.Null &&
                 !SameType(existing.ValueKind, replacementType)))
                throw new ArgumentException("Only existing scalar settings of the same type can be changed.");

            node = JsonNode.Parse(original) ??
                throw new ArgumentException("The JSON object could not be parsed.");
        }

        JsonObject parentNode = node as JsonObject ??
            throw new ArgumentException("The JSON config must be an object.");
        foreach (string parent in parents)
            parentNode = parentNode[parent] as JsonObject ??
                throw new ArgumentException("The JSON setting parent changed during parsing.");
        parentNode[key!] = JsonNode.Parse(value);
        // Normalizes JSON formatting, so the owner's diff preview is required.
        string proposed = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(proposed) > MaxTextBytes)
            throw new ArgumentException("The JSON config would exceed the safe file-size limit.");
        return proposed;
    }
}
