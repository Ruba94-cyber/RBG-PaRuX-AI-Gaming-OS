using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PaRuX.Intelligence;

/// <summary>
/// Token usage of ONE reply exactly as the model server reported it (OpenAI-style "usage" frame and/or
/// llama.cpp "timings"). Nothing here is estimated: a field the server did not send stays null and is
/// never filled from a guess.
/// </summary>
public sealed record ChatTokenUsage
{
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    /// <summary>Prompt tokens served from the server's prompt cache (llama.cpp timings.cache_n).</summary>
    public int? CachedPromptTokens { get; init; }
    public double? CompletionTokensPerSecond { get; init; }
    public double? PromptTokensPerSecond { get; init; }

    public int? TotalTokens => PromptTokens is { } p && CompletionTokens is { } c ? p + c : null;
    public bool HasAnyData => PromptTokens != null || CompletionTokens != null || CompletionTokensPerSecond != null;
}

public static class ChatTokenUsageParser
{
    /// <summary>
    /// Reads one SSE data payload. Returns null when it carries neither "usage" nor "timings".
    /// When both are present token counts come from "usage" (the server's own total) and speeds from "timings".
    /// </summary>
    public static ChatTokenUsage? TryParse(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson)
            || (!dataJson.Contains("\"usage\"", StringComparison.Ordinal) && !dataJson.Contains("\"timings\"", StringComparison.Ordinal)))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(dataJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            int? usagePrompt = null, usageCompletion = null;
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                usagePrompt = ReadCount(usage, "prompt_tokens");
                usageCompletion = ReadCount(usage, "completion_tokens");
            }

            int? timingsPrompt = null, timingsCompletion = null, cached = null;
            double? genSpeed = null, promptSpeed = null;
            if (root.TryGetProperty("timings", out JsonElement timings) && timings.ValueKind == JsonValueKind.Object)
            {
                int? promptN = ReadCount(timings, "prompt_n");
                cached = ReadCount(timings, "cache_n");
                // llama.cpp reports only the freshly evaluated prompt tokens in prompt_n; the cached prefix is separate.
                if (promptN != null) timingsPrompt = promptN.Value + (cached ?? 0);
                timingsCompletion = ReadCount(timings, "predicted_n");
                genSpeed = ReadRate(timings, "predicted_per_second");
                promptSpeed = ReadRate(timings, "prompt_per_second");
            }

            var result = new ChatTokenUsage
            {
                PromptTokens = usagePrompt ?? timingsPrompt,
                CompletionTokens = usageCompletion ?? timingsCompletion,
                CachedPromptTokens = cached,
                CompletionTokensPerSecond = genSpeed,
                PromptTokensPerSecond = promptSpeed
            };
            return result.HasAnyData ? result : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Folds a later frame into an earlier one: later non-null values win.</summary>
    public static ChatTokenUsage Merge(ChatTokenUsage? earlier, ChatTokenUsage later) => earlier == null ? later : new ChatTokenUsage
    {
        PromptTokens = later.PromptTokens ?? earlier.PromptTokens,
        CompletionTokens = later.CompletionTokens ?? earlier.CompletionTokens,
        CachedPromptTokens = later.CachedPromptTokens ?? earlier.CachedPromptTokens,
        CompletionTokensPerSecond = later.CompletionTokensPerSecond ?? earlier.CompletionTokensPerSecond,
        PromptTokensPerSecond = later.PromptTokensPerSecond ?? earlier.PromptTokensPerSecond
    };

    private static int? ReadCount(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) && n >= 0 ? n : null;

    private static double? ReadRate(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)
            && double.IsFinite(d) && d > 0 ? d : null;
}

public sealed record ChatUsageSummary(int Replies, int RepliesWithUsage, long PromptTokens, long CompletionTokens);

public static class ChatTokenUsageFormatter
{
    /// <summary>One-line indicator under a reply, or null when the server reported nothing.</summary>
    public static string? FormatLine(ChatTokenUsage? usage, bool italian)
    {
        if (usage == null || !usage.HasAnyData) return null;
        var parts = new List<string>();
        if (usage.PromptTokens is { } p) parts.Add(N(p) + (italian ? " in ingresso" : " in"));
        if (usage.CompletionTokens is { } c) parts.Add(N(c) + (italian ? " in uscita" : " out"));
        if (usage.CompletionTokensPerSecond is { } s) parts.Add(s.ToString("0.0", CultureInfo.InvariantCulture) + " tok/s");
        if (usage.CachedPromptTokens is { } k && k > 0) parts.Add(N(k) + (italian ? " dalla cache" : " cached"));
        return string.Join(" · ", parts);
    }

    public static ChatUsageSummary Summarize(IEnumerable<ChatTokenUsage?> repliesInOrder)
    {
        int replies = 0, withUsage = 0;
        long prompt = 0, completion = 0;
        foreach (ChatTokenUsage? u in repliesInOrder)
        {
            replies++;
            if (u == null || !u.HasAnyData) continue;
            withUsage++;
            prompt += u.PromptTokens ?? 0;
            completion += u.CompletionTokens ?? 0;
        }
        return new ChatUsageSummary(replies, withUsage, prompt, completion);
    }

    /// <summary>Text for the /usage command. Says plainly when the server reported no data.</summary>
    public static string BuildReport(ChatUsageSummary summary, ChatTokenUsage? last, bool italian)
    {
        var sb = new StringBuilder();
        if (summary.RepliesWithUsage == 0)
        {
            sb.Append(italian
                ? "Nessun dato sull'uso dei token: il server del modello non ne ha riportati per le risposte di questa chat (succede con provider esterni e con i turni che usano strumenti)."
                : "No token usage yet: the model server reported none for the replies in this chat (this happens with external providers and with tool-using turns).");
            return sb.ToString();
        }
        string? lastLine = FormatLine(last, italian);
        if (lastLine != null)
            sb.Append(italian ? "Ultima risposta: " : "Last reply: ").Append(lastLine).Append('\n');
        sb.Append(italian ? "Questa chat: " : "This chat: ")
          .Append(N(summary.PromptTokens)).Append(italian ? " token in ingresso, " : " tokens in, ")
          .Append(N(summary.CompletionTokens)).Append(italian ? " in uscita, su " : " out, over ")
          .Append(summary.RepliesWithUsage).Append(italian ? " risposte con dati" : " replies with data");
        if (summary.RepliesWithUsage < summary.Replies)
            sb.Append(italian ? " (" + (summary.Replies - summary.RepliesWithUsage) + " senza dati dal server)" : " (" + (summary.Replies - summary.RepliesWithUsage) + " without server data)");
        sb.Append(italian ? ". Valori riportati dal server, non stimati." : ". Values reported by the server, not estimated.");
        return sb.ToString();
    }

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
