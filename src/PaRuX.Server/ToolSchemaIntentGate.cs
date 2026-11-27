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
    /// <summary>Longest query still treated as a simple plain-chat turn.</summary>
    internal const int MaximumSimpleTurnCharacters = 24_000;

    // write/create/generate (and Italian equivalents) followed shortly by a producible artifact.
    private const string WriteVerbWithArtifact =
        @"|\b(scrivi\w*|crea\w*|genera\w*|write|create|generate|make|produce)\b[^.\n]{0,48}\b(excel|docx?|xlsx?|files?|documen\w*|spreadsheet|workbook|report|foglio|tabella)\b";

    // Object words (English and Italian), file extensions, paths and possessives that point at the user's data, plus write verbs.
    [GeneratedRegex(
        @"(file|files|folder|cartella|document|documento|docs?\b|docx|(?<![-\d])word\b|excel|xlsx|xls\b|csv|spreadsheet|workbook|foglio|pdf|pptx|slide|appunti|desktop|disk\b|disco|drive|path\b|percorso" +
        @"|\b(my|mine|mio|mia|miei|mie)\b" +
        // Imperative verbs that ask for an action on something (English and Italian): the object may be a file the user never named.
        @"|\b(cerca\w*|trova\w*|apri\w*|leggi\w*|elenca\w*|mostra\w*|list|show|search|find|open|read)\b" +
        // Authoring verbs only count next to something a tool could produce; "write an explanation of X" is plain prose.
        WriteVerbWithArtifact +
        @"|\b(save|export|download|upload|attach|salva|esporta|scarica|allega|invia|send|email|mail|calendar|calendario)\b" +
        // Asking for code to be executed: the run_python tool must be offered even for a short one-line request.
        @"|\b(python|run_python|script|sandbox|execute|esegui\w*)\b" +
        @"|[a-z]:\\|\\\\|/[a-z0-9_.-]+/|\.[a-z0-9]{2,5}\b|https?://|```)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ToolIntent();

    // Strong structural markers that remain meaningful even inside long pasted text.
    [GeneratedRegex(
        @"([a-z]:\\|\\\\|https?://|```|\brun_python\b|\.(docx|xlsx|xls|csv|pdf|pptx)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StructuralToolSignal();

    // Action verbs, possessives, or execution intents at the instruction edges of a long prompt.
    // Generic nouns alone ("file", "disk", "document") inside pasted prose do not trigger tools.
    [GeneratedRegex(
        @"(\b(my|mine|mio|mia|miei|mie)\b" +
        @"|\b(cerca\w*|trova\w*|apri\w*|leggi\w*|elenca\w*|mostra\w*|list|show|search|find|open|read)\b" +
        // Authoring verbs only count next to something a tool could produce; "write an explanation of X" is plain prose.
        WriteVerbWithArtifact +
        @"|\b(save|export|download|upload|attach|salva|esporta|scarica|allega|invia|send|email|mail|calendar|calendario)\b" +
        @"|\b(python|run_python|script|sandbox|execute|esegui\w*)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EdgeActionIntent();

    private const int EdgeIntentWindowCharacters = 180;

    /// <summary>True when this turn certainly needs no tool, so the schemas can be omitted from the prompt.</summary>
    internal static bool CanOmitToolSchemas(string? query, string? skillId)
    {
        if (!string.Equals(skillId, "general", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(query)) return false;
        string text = query.Trim();
        if (text.Length > MaximumSimpleTurnCharacters) return false;
        if (text.Length <= 300) return !ToolIntent().IsMatch(text);
        if (StructuralToolSignal().IsMatch(text)) return false;
        string head = text[..EdgeIntentWindowCharacters];
        string tail = text[^EdgeIntentWindowCharacters..];
        return !EdgeActionIntent().IsMatch(head) && !EdgeActionIntent().IsMatch(tail);
    }
}
