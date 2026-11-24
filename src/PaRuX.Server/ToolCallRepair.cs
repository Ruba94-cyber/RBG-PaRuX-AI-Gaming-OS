using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Detects malformed or schema-invalid model tool calls before they are executed and, where the fix is
/// unambiguous and lossless, repairs them locally (code fences, double-encoded arguments, a missing closing
/// bracket after a complete value, "5" for an integer, a case-different tool name). Anything else is reported
/// with a precise validation message so the tool loop can feed it back to the model (bounded) or fail honestly.
/// It never guesses: a truncated string value, a missing required argument or an unknown tool is never
/// "completed" on the model's behalf.
/// </summary>
internal static class ToolCallRepair
{
    internal enum Status { Valid, LocallyRepaired, Invalid }

    internal sealed record Outcome(Status Status, string Name, string ArgumentsJson, string? Error, IReadOnlyList<string> Fixes);

    /// <summary>A tool call the model wrote as text (Hermes/Qwen XML style) instead of a structured tool_call.</summary>
    internal sealed record TextualCall(string Name, string RawArguments, string? ParseError);

    private const int MaxErrors = 8;
    private const int MaxDepth = 12;
    private const int MaxErrorCharacters = 700;

    private static readonly Regex FenceOpen = new(@"^\s*```[A-Za-z0-9_-]*\s*", RegexOptions.CultureInvariant);
    private static readonly Regex FenceClose = new(@"\s*```\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingComma = new(@",(\s*[}\]])", RegexOptions.CultureInvariant);
    private static readonly Regex ToolCallBlock = new(@"<tool_call>(?<body>.*?)(?:</tool_call>|\z)",
        RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex FunctionBlock = new(@"<function=(?<name>[^>\s]+)>(?<body>.*?)(?<close></function>|\z)",
        RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex ParameterBlock = new(@"<parameter=(?<key>[^>\s]+)>(?<value>.*?)</parameter>",
        RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    internal static readonly string[] TextualMarkers = { "<tool_call>", "<function=" };

    /// <summary>Parameter schemas keyed by advertised tool name (null schema = tool takes whatever it is sent).</summary>
    internal static Dictionary<string, JsonObject?> SchemasOf(JsonArray tools)
    {
        var map = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        foreach (JsonNode? tool in tools)
        {
            if (tool?["function"] is not JsonObject fn) continue;
            if (fn["name"] is not JsonValue nv || !nv.TryGetValue(out string? name) || string.IsNullOrWhiteSpace(name)) continue;
            map[name] = fn["parameters"] as JsonObject;
        }
        return map;
    }

    internal static Outcome Inspect(string name, string rawArguments, IReadOnlyDictionary<string, JsonObject?> schemas)
    {
        var fixes = new List<string>();
        string resolved = ResolveName(name, schemas.Keys, fixes);
        if (!schemas.TryGetValue(resolved, out JsonObject? schema))
        {
            string available = string.Join(", ", schemas.Keys.Take(30));
            return Fail(resolved, "Unknown tool \"" + Clip(name, 80) + "\". Available tools: " + (available.Length == 0 ? "(none)" : available) + ".", fixes);
        }

        JsonNode? parsed = TryParseLenient(rawArguments, out string? parseError, fixes);
        if (parsed == null)
            return Fail(resolved, "The arguments are not valid JSON (" + parseError + "). Send one complete JSON object.", fixes);
        if (parsed is not JsonObject args)
            return Fail(resolved, "The arguments must be a JSON object, got " + KindOf(parsed) + ".", fixes);

        if (schema != null)
        {
            Coerce(args, schema, "arguments", fixes, 0);
            var errors = new List<string>();
            Validate(args, schema, "arguments", errors, 0);
            if (errors.Count > 0)
                return Fail(resolved, string.Join("; ", errors), fixes);
        }

        bool changed = fixes.Count > 0;
        return new Outcome(changed ? Status.LocallyRepaired : Status.Valid, resolved,
            changed ? args.ToJsonString() : (string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments), null, fixes);
    }

    /// <summary>Compact, bounded description of the expected arguments, appended to a validation error.</summary>
    internal static string DescribeSchema(JsonObject? schema)
    {
        if (schema == null) return "(no declared parameters)";
        var sb = new StringBuilder();
        JsonArray? required = schema["required"] as JsonArray;
        HashSet<string> requiredNames = required == null ? new() : required.Select(r => r?.GetValue<string>() ?? "").ToHashSet();
        if (schema["properties"] is JsonObject props)
        {
            foreach (KeyValuePair<string, JsonNode?> p in props)
            {
                if (sb.Length > 500) { sb.Append("..."); break; }
                string type = p.Value is JsonObject po ? TypeText(po) : "any";
                sb.Append(p.Key).Append(requiredNames.Contains(p.Key) ? " (required " : " (optional ").Append(type).Append(") ");
            }
        }
        return sb.Length == 0 ? "(no declared parameters)" : sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- text-form calls

    internal static bool CouldStartTextualCall(string trimmedStart)
    {
        foreach (string marker in TextualMarkers)
            if (marker.StartsWith(trimmedStart, StringComparison.OrdinalIgnoreCase)
                || trimmedStart.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    internal static bool StartsTextualCall(string trimmedStart) =>
        TextualMarkers.Any(m => trimmedStart.StartsWith(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>Extracts text-form calls and returns the content with those blocks removed.</summary>
    internal static List<TextualCall> ParseTextualCalls(string content, out string remainingContent)
    {
        var calls = new List<TextualCall>();
        var removed = new StringBuilder();
        int cursor = 0;

        foreach (Match block in ToolCallBlock.Matches(content))
        {
            removed.Append(content, cursor, block.Index - cursor);
            cursor = block.Index + block.Length;
            bool closed = block.Value.EndsWith("</tool_call>", StringComparison.OrdinalIgnoreCase);
            ParseBody(block.Groups["body"].Value.Trim(), closed, calls);
        }
        removed.Append(content, cursor, content.Length - cursor);

        // Qwen-coder style without the <tool_call> wrapper.
        string rest = removed.ToString();
        if (calls.Count == 0 && FunctionBlock.IsMatch(rest))
        {
            var bare = new List<TextualCall>();
            var stripped = new StringBuilder();
            int c = 0;
            foreach (Match m in FunctionBlock.Matches(rest))
            {
                stripped.Append(rest, c, m.Index - c);
                c = m.Index + m.Length;
                bare.Add(FromFunctionBlock(m));
            }
            stripped.Append(rest, c, rest.Length - c);
            calls.AddRange(bare);
            rest = stripped.ToString();
        }

        remainingContent = rest.Trim();
        return calls;
    }

    private static void ParseBody(string body, bool closed, List<TextualCall> calls)
    {
        if (body.Length == 0)
        {
            calls.Add(new TextualCall(string.Empty, "{}", "The <tool_call> block is empty."));
            return;
        }
        Match fn = FunctionBlock.Match(body);
        if (fn.Success && !body.StartsWith("{", StringComparison.Ordinal))
        {
            TextualCall call = FromFunctionBlock(fn);
            calls.Add(!closed && call.ParseError == null
                ? call with { ParseError = "The <tool_call> block was not closed (output cut off)." } : call);
            return;
        }

        var fixes = new List<string>();
        JsonNode? node = TryParseLenient(body, out string? error, fixes);
        if (node is not JsonObject obj)
        {
            calls.Add(new TextualCall(string.Empty, "{}", "The <tool_call> body is not a JSON object (" + (error ?? KindOf(node)) + ")."));
            return;
        }
        string name = obj["name"] is JsonValue nv && nv.TryGetValue(out string? n) ? n ?? string.Empty : string.Empty;
        JsonNode? args = obj["arguments"] ?? obj["parameters"];
        string raw = args switch
        {
            null => "{}",
            JsonValue v when v.TryGetValue(out string? s) => s ?? "{}",
            _ => args.ToJsonString()
        };
        string? problem = string.IsNullOrWhiteSpace(name) ? "The <tool_call> JSON has no \"name\"." : null;
        if (problem == null && !closed && args == null) problem = "The <tool_call> block was not closed (output cut off).";
        calls.Add(new TextualCall(name, raw, problem));
    }

    private static TextualCall FromFunctionBlock(Match fn)
    {
        string name = fn.Groups["name"].Value;
        bool closed = fn.Groups["close"].Value.Length > 0;
        var args = new JsonObject();
        foreach (Match p in ParameterBlock.Matches(fn.Groups["body"].Value))
            args[p.Groups["key"].Value] = p.Groups["value"].Value.Trim('\r', '\n');
        string? error = closed ? null : "The <function=" + name + "> block was not closed (output cut off).";
        return new TextualCall(name, args.ToJsonString(), error);
    }

    // ---------------------------------------------------------------- lenient JSON

    /// <summary>
    /// Parses JSON arguments, tolerating a code fence, a trailing comma, a double-encoded string and missing
    /// closing brackets after a complete value. A truncated string literal is never closed.
    /// </summary>
    internal static JsonNode? TryParseLenient(string? raw, out string? error, List<string> fixes)
    {
        error = null;
        string text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return new JsonObject();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = FenceClose.Replace(FenceOpen.Replace(text, string.Empty, 1), string.Empty, 1).Trim();
            fixes.Add("removed a markdown code fence");
        }

        JsonNode? node = TryParse(text, out error);
        if (node == null)
        {
            string noCommas = TrailingComma.Replace(text, "$1");
            string? closed = CloseTruncated(noCommas, out string? truncationNote);
            if (closed != null && TryParse(closed, out _) is { } repaired)
            {
                fixes.Add(noCommas != text && closed == noCommas ? "removed a trailing comma" : "closed missing brackets after a complete value");
                node = repaired;
                error = null;
            }
            else if (truncationNote != null)
            {
                error = truncationNote;
            }
        }

        if (node is JsonValue v && v.TryGetValue(out string? inner) && inner != null
            && inner.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            JsonNode? unwrapped = TryParse(inner, out _);
            if (unwrapped is JsonObject)
            {
                fixes.Add("unwrapped double-encoded arguments");
                return unwrapped;
            }
        }
        return node;
    }

    private static JsonNode? TryParse(string text, out string? error)
    {
        error = null;
        try
        {
            return JsonNode.Parse(text, nodeOptions: null, new JsonDocumentOptions { MaxDepth = 64 })
                   ?? throw new JsonException("null");
        }
        catch (JsonException ex)
        {
            error = Clip(ex.Message, 160);
            return null;
        }
    }

    /// <summary>Returns text with only the missing closers appended, or null when closing would mean guessing a value.</summary>
    private static string? CloseTruncated(string text, out string? note)
    {
        note = null;
        var stack = new Stack<char>();
        bool inString = false, escape = false;
        foreach (char ch in text)
        {
            if (inString)
            {
                if (escape) escape = false;
                else if (ch == '\\') escape = true;
                else if (ch == '"') inString = false;
                continue;
            }
            switch (ch)
            {
                case '"': inString = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']':
                    if (stack.Count == 0 || stack.Pop() != ch) return null;
                    break;
            }
        }
        if (inString)
        {
            note = "the output was cut off inside a string value, so it was not completed";
            return null;
        }
        if (stack.Count == 0) return text;
        string trimmed = text.TrimEnd();
        if (trimmed.EndsWith(',')) trimmed = trimmed[..^1];
        if (trimmed.EndsWith(':')) { note = "the output was cut off after a key"; return null; }
        var sb = new StringBuilder(trimmed);
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    // ---------------------------------------------------------------- names

    private static string ResolveName(string name, IEnumerable<string> known, List<string> fixes)
    {
        string candidate = (name ?? string.Empty).Trim();
        string[] names = known as string[] ?? known.ToArray();
        if (names.Contains(candidate, StringComparer.Ordinal)) return candidate;

        foreach (string prefix in new[] { "functions.", "function.", "default_api.", "tools." })
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string stripped = candidate[prefix.Length..];
                if (names.Contains(stripped, StringComparer.Ordinal)) { fixes.Add("removed tool-name prefix"); return stripped; }
                candidate = stripped;
            }

        string[] matches = names.Where(n => Canon(n) == Canon(candidate)).ToArray();
        if (matches.Length == 1) { fixes.Add("normalised tool name"); return matches[0]; }
        return candidate;
    }

    private static string Canon(string name) => name.Replace("-", "").Replace("_", "").Replace(".", "").ToLowerInvariant();

    // ---------------------------------------------------------------- schema

    private static void Coerce(JsonNode? node, JsonObject schema, string path, List<string> fixes, int depth)
    {
        if (depth > MaxDepth) return;
        if (node is JsonObject obj)
        {
            JsonObject? props = schema["properties"] as JsonObject;
            if (props != null)
            {
                foreach (KeyValuePair<string, JsonNode?> p in props)
                {
                    if (p.Value is not JsonObject propSchema) continue;
                    if (!obj.ContainsKey(p.Key))
                    {
                        if (propSchema["default"] is { } def && schema["required"] is JsonArray req
                            && req.Any(r => r is JsonValue rv && rv.TryGetValue(out string? rn) && rn == p.Key))
                        {
                            obj[p.Key] = def.DeepClone();
                            fixes.Add("filled required '" + p.Key + "' from its schema default");
                        }
                        continue;
                    }
                    JsonNode? current = obj[p.Key];
                    JsonNode? replaced = CoerceValue(current, propSchema, path + "." + p.Key, fixes, depth + 1);
                    if (!ReferenceEquals(replaced, current)) obj[p.Key] = replaced;
                }
            }
        }
    }

    private static JsonNode? CoerceValue(JsonNode? value, JsonObject schema, string path, List<string> fixes, int depth)
    {
        string[] types = TypesOf(schema);
        if (value is JsonValue jv && jv.GetValueKind() == JsonValueKind.String && jv.TryGetValue(out string? s) && s != null)
        {
            if (types.Length > 0 && !types.Contains("string"))
            {
                if ((types.Contains("integer") || types.Contains("number"))
                    && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    && double.IsFinite(d))
                {
                    fixes.Add(path + ": string to number");
                    return types.Contains("integer") && d == Math.Floor(d) && Math.Abs(d) < 9e15 ? JsonValue.Create((long)d) : JsonValue.Create(d);
                }
                if (types.Contains("boolean") && bool.TryParse(s.Trim(), out bool b))
                {
                    fixes.Add(path + ": string to boolean");
                    return JsonValue.Create(b);
                }
                if ((types.Contains("object") || types.Contains("array")) && s.TrimStart().Length > 0 && "{[".Contains(s.TrimStart()[0]))
                {
                    JsonNode? inner = TryParse(s, out _);
                    if (inner != null && ((inner is JsonObject && types.Contains("object")) || (inner is JsonArray && types.Contains("array"))))
                    {
                        fixes.Add(path + ": decoded embedded JSON");
                        Coerce(inner, schema, path, fixes, depth + 1);
                        return inner;
                    }
                }
            }
            if (schema["enum"] is JsonArray en)
            {
                bool exact = en.Any(e => e is JsonValue ev && ev.TryGetValue(out string? es) && es == s);
                if (!exact)
                {
                    string[] ci = en.Select(e => e is JsonValue ev && ev.TryGetValue(out string? es) ? es : null)
                        .Where(x => x != null && string.Equals(x, s.Trim(), StringComparison.OrdinalIgnoreCase)).Select(x => x!).ToArray();
                    if (ci.Length == 1) { fixes.Add(path + ": enum case"); return JsonValue.Create(ci[0]); }
                }
            }
        }
        else if (value is JsonValue nv && types.Length > 0 && types.Contains("string") && !types.Contains("number") && !types.Contains("integer") && !types.Contains("boolean"))
        {
            JsonValueKind kind = nv.GetValueKind();
            if (kind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                fixes.Add(path + ": scalar to string");
                return JsonValue.Create(kind == JsonValueKind.Number ? nv.ToJsonString() : kind == JsonValueKind.True ? "true" : "false");
            }
        }
        else if (value is JsonObject)
            Coerce(value, schema, path, fixes, depth + 1);
        else if (value is JsonArray arr && schema["items"] is JsonObject itemSchema)
        {
            for (int i = 0; i < arr.Count && i < 256; i++)
            {
                JsonNode? replaced = CoerceValue(arr[i], itemSchema, path + "[" + i + "]", fixes, depth + 1);
                if (!ReferenceEquals(replaced, arr[i])) arr[i] = replaced;
            }
        }
        return value;
    }

    private static void Validate(JsonNode? value, JsonObject schema, string path, List<string> errors, int depth)
    {
        if (errors.Count >= MaxErrors || depth > MaxDepth) return;
        string[] types = TypesOf(schema);
        string kind = KindOf(value);
        if (types.Length > 0 && !types.Any(t => TypeMatches(t, value)))
        {
            errors.Add(path + ": expected " + string.Join(" or ", types) + ", got " + kind);
            return;
        }
        if (schema["enum"] is JsonArray en && !en.Any(e => JsonNode.DeepEquals(e, value)))
        {
            errors.Add(path + ": must be one of [" + string.Join(", ", en.Take(12).Select(e => e?.ToJsonString() ?? "null")) + "]");
            return;
        }
        if (schema["const"] is { } constant && !JsonNode.DeepEquals(constant, value))
        {
            errors.Add(path + ": must equal " + constant.ToJsonString());
            return;
        }

        switch (value)
        {
            case JsonObject obj:
                if (schema["required"] is JsonArray required)
                    foreach (JsonNode? r in required)
                        if (r is JsonValue rv && rv.TryGetValue(out string? rn) && rn != null && !obj.ContainsKey(rn))
                            errors.Add(path + ": missing required property '" + rn + "'");
                JsonObject? props = schema["properties"] as JsonObject;
                if (props != null)
                    foreach (KeyValuePair<string, JsonNode?> prop in obj)
                        if (props[prop.Key] is JsonObject ps) Validate(prop.Value, ps, path + "." + prop.Key, errors, depth + 1);
                if (schema["additionalProperties"] is JsonValue ap && ap.GetValueKind() == JsonValueKind.False && props != null)
                    foreach (KeyValuePair<string, JsonNode?> prop in obj)
                        if (!props.ContainsKey(prop.Key)) errors.Add(path + ": unexpected property '" + prop.Key + "'");
                break;
            case JsonArray arr:
                if (schema["minItems"] is JsonValue mi && mi.TryGetValue(out int minItems) && arr.Count < minItems)
                    errors.Add(path + ": needs at least " + minItems + " items");
                if (schema["maxItems"] is JsonValue ma && ma.TryGetValue(out int maxItems) && arr.Count > maxItems)
                    errors.Add(path + ": allows at most " + maxItems + " items");
                if (schema["items"] is JsonObject itemSchema)
                    for (int i = 0; i < arr.Count && i < 256; i++) Validate(arr[i], itemSchema, path + "[" + i + "]", errors, depth + 1);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String && v.TryGetValue(out string? str) && str != null:
                if (schema["minLength"] is JsonValue ml && ml.TryGetValue(out int minLen) && str.Length < minLen)
                    errors.Add(path + ": shorter than " + minLen + " characters");
                if (schema["maxLength"] is JsonValue xl && xl.TryGetValue(out int maxLen) && str.Length > maxLen)
                    errors.Add(path + ": longer than " + maxLen + " characters");
                break;
            case JsonValue n when n.GetValueKind() == JsonValueKind.Number && Num(n, out double num):
                if (schema["minimum"] is JsonValue mn && Num(mn, out double min) && num < min)
                    errors.Add(path + ": below the minimum " + min.ToString(CultureInfo.InvariantCulture));
                if (schema["maximum"] is JsonValue mx && Num(mx, out double max) && num > max)
                    errors.Add(path + ": above the maximum " + max.ToString(CultureInfo.InvariantCulture));
                break;
        }
    }

    private static string[] TypesOf(JsonObject schema) => schema["type"] switch
    {
        JsonValue v when v.TryGetValue(out string? t) && t != null => new[] { t },
        JsonArray a => a.Select(x => x is JsonValue xv && xv.TryGetValue(out string? t) ? t ?? "" : "").Where(t => t.Length > 0).ToArray(),
        _ => Array.Empty<string>()
    };

    private static string TypeText(JsonObject schema)
    {
        string[] t = TypesOf(schema);
        string text = t.Length == 0 ? "any" : string.Join("|", t);
        if (schema["enum"] is JsonArray en) text += " enum[" + string.Join(",", en.Take(8).Select(e => e?.ToJsonString() ?? "null")) + "]";
        return text;
    }

    private static bool TypeMatches(string type, JsonNode? value) => type switch
    {
        "null" => value == null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue s && s.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value is JsonValue n && n.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue i && i.GetValueKind() == JsonValueKind.Number && Num(i, out double d) && d == Math.Floor(d),
        _ => true
    };

    private static string KindOf(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "value"
        },
        _ => "value"
    };

    private static Outcome Fail(string name, string error, List<string> fixes) =>
        new(Status.Invalid, name, "{}", Clip(error, MaxErrorCharacters), fixes);

    // JsonValue.TryGetValue<double> fails for values created from a long, so read numbers through their JSON text.
    private static bool Num(JsonValue value, out double number) =>
        double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
