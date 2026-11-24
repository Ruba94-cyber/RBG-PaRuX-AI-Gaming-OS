using System.Globalization;
using System.Text;

namespace PaRuX.Intelligence;

/// <summary>Values the caller (the server) is willing to expose to a workspace system prompt.</summary>
public sealed record PromptVariableContext(string WorkspaceName, DateTimeOffset Now, string Language);

public sealed record PromptVariableExpansion(string Text, IReadOnlyList<string> Used, IReadOnlyList<string> Unknown, bool Refused, string? RefusalCode);

public sealed record PromptVariableInfo(string Key, string[] Aliases, string DescriptionEn, string DescriptionIt);

/// <summary>
/// Workspace system-prompt variables ({date}, {workspace}, ...). Deliberately a closed catalogue: only
/// values that are safe to put in a prompt are offered (date/time, the workspace name, the UI language).
/// There is NO variable for the user's name, machine name, paths, files, memory or environment, and an
/// unknown {name} is left as literal text and reported, never resolved from anything else.
/// Expansion is single pass (a value is never re-scanned) and values are sanitised so a workspace name
/// cannot smuggle control characters, braces or the client's internal scope marker into the prompt.
/// </summary>
public static class PromptVariablePolicy
{
    public const int MaxTemplateCharacters = 4_000;
    public const int MaxValueCharacters = 80;
    private const int MaxVariableNameCharacters = 32;

    public static readonly IReadOnlyList<PromptVariableInfo> Catalog = new[]
    {
        new PromptVariableInfo("date", new[] { "date", "data" }, "Today's date (YYYY-MM-DD)", "La data di oggi (AAAA-MM-GG)"),
        new PromptVariableInfo("time", new[] { "time", "ora" }, "Current local time (HH:mm)", "L'ora locale attuale (HH:mm)"),
        new PromptVariableInfo("weekday", new[] { "weekday", "giorno" }, "Day of the week", "Il giorno della settimana"),
        new PromptVariableInfo("workspace", new[] { "workspace", "nome workspace", "workspace name", "nome del workspace" }, "The workspace (project) name", "Il nome del workspace (progetto)"),
        new PromptVariableInfo("language", new[] { "language", "lingua" }, "The interface language (en or it)", "La lingua dell'interfaccia (en o it)"),
    };

    public static PromptVariableExpansion Expand(string? template, PromptVariableContext context)
    {
        if (string.IsNullOrEmpty(template))
            return new PromptVariableExpansion(string.Empty, Array.Empty<string>(), Array.Empty<string>(), false, null);
        if (template.Length > MaxTemplateCharacters)
            return new PromptVariableExpansion(string.Empty, Array.Empty<string>(), Array.Empty<string>(), true, "template_too_long");

        var output = new StringBuilder(template.Length + 64);
        var used = new List<string>();
        var unknown = new List<string>();
        for (int i = 0; i < template.Length; i++)
        {
            char ch = template[i];
            if (ch == '{' && i + 1 < template.Length && template[i + 1] == '{') { output.Append('{'); i++; continue; }
            if (ch == '}' && i + 1 < template.Length && template[i + 1] == '}') { output.Append('}'); i++; continue; }
            if (ch != '{') { output.Append(ch); continue; }

            int close = template.IndexOf('}', i + 1);
            if (close < 0 || close - i - 1 > MaxVariableNameCharacters || template.IndexOf('{', i + 1, close - i - 1) >= 0)
            {
                output.Append(ch); // a lone brace is plain text
                continue;
            }
            string raw = template.Substring(i + 1, close - i - 1);
            string? key = Resolve(raw);
            if (key == null)
            {
                output.Append(template, i, close - i + 1);
                string label = "{" + raw.Trim() + "}";
                if (!unknown.Contains(label, StringComparer.OrdinalIgnoreCase)) unknown.Add(label);
            }
            else
            {
                output.Append(ValueOf(key, context));
                if (!used.Contains(key)) used.Add(key);
            }
            i = close;
        }
        return new PromptVariableExpansion(output.ToString(), used, unknown, false, null);
    }

    private static string? Resolve(string raw)
    {
        string name = string.Join(' ', raw.Trim().ToLowerInvariant().Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) return null;
        foreach (PromptVariableInfo info in Catalog)
            if (info.Aliases.Contains(name, StringComparer.Ordinal)) return info.Key;
        return null;
    }

    private static string ValueOf(string key, PromptVariableContext c) => key switch
    {
        "date" => c.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "time" => c.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
        "weekday" => c.Now.ToString("dddd", string.Equals(c.Language, "it", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("it-IT") : CultureInfo.GetCultureInfo("en-US")),
        "workspace" => SanitizeValue(c.WorkspaceName),
        "language" => string.Equals(c.Language, "it", StringComparison.OrdinalIgnoreCase) ? "it" : "en",
        _ => string.Empty
    };

    /// <summary>Single line, no control characters, no braces/brackets, bounded.</summary>
    public static string SanitizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(Math.Min(value.Length, MaxValueCharacters));
        foreach (char ch in value)
        {
            if (sb.Length >= MaxValueCharacters) break;
            if (char.IsControl(ch)) { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            if (ch is '{' or '}' or '[' or ']' or '<' or '>') continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}
