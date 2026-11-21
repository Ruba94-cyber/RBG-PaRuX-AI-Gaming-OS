using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Decides whether a turn needs the tool schemas at all. The default "general" skill advertises five built-in tools
/// (desktop search and read, Word and Excel writers); their schemas are about 900 prompt tokens that were sent with every
/// chat request, including "what is a mutex?". Measured on the RTX 5080 reference PC that is 3.4 s on a cold start on a slow GPU
/// and a permanent share of the context window. A short, single-line question with no sign of a file, a document, the user's
/// own data or a write/export request cannot use those tools, so they are left out of that turn.
///
/// Conservative by construction: anything long, multi-line, mentioning a file or document type, a path, the user's own
/// things, saving or exporting, or any skill other than "general" keeps every tool exactly as before.
/// </summary>
internal static partial class ToolSchemaIntentGate
{
    /// <summary>Longest query still treated as a simple short turn.</summary>
    internal const int MaximumSimpleTurnCharacters = 240;

    // Object words (English and Italian), file extensions, paths and possessives that point at the user's data, plus write verbs.
    [GeneratedRegex(
        @"(file|files|folder|cartella|document|documento|docs?\b|docx|word\b|excel|xlsx|xls\b|csv|spreadsheet|workbook|foglio|pdf|pptx|slide|note\b|notes\b|appunti|desktop|disk\b|disco|drive|path\b|percorso" +
        @"|\b(my|mine|mio|mia|miei|mie)\b|\b(save|export|download|upload|attach|salva|esporta|scarica|allega|invia|send|email|mail|calendar|calendario)\b" +
        @"|[a-z]:\\|\\\\|/[a-z0-9_.-]+/|\.[a-z0-9]{2,5}\b|https?://|```)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ToolIntent();

    /// <summary>True when this turn certainly needs no tool, so the schemas can be omitted from the prompt.</summary>
    internal static bool CanOmitToolSchemas(string? query, string? skillId)
    {
        if (!string.Equals(skillId, "general", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(query)) return false;
        string text = query.Trim();
        if (text.Length > MaximumSimpleTurnCharacters) return false;
        if (text.Contains('\n') || text.Contains('\r')) return false;
        return !ToolIntent().IsMatch(text);
    }
}
