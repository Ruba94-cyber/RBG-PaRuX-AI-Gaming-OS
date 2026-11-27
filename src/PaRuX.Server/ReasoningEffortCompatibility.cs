using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Chat templates disagree on the reasoning-effort vocabulary: one accepts low/medium/high, the
/// Qwen3.8 27B template accepts only low/medium/xhigh and raises a Jinja exception (HTTP 500) for
/// anything else. PaRuX's adaptive policy speaks one canonical scale, so the value sent to the
/// served model is mapped onto what that model's own template accepts. Found by a live functional
/// test: "Create a Word document ..." failed with "Unexpected reasoning effort high".
/// </summary>
internal static class ReasoningEffortCompatibility
{
    /// <summary>Canonical order, cheapest first.</summary>
    internal static readonly string[] Scale = { "none", "minimal", "low", "medium", "high", "xhigh", "max" };

    private static readonly Regex AllowedSet = new(
        @"reasoning_effort\s+not\s+in\s*[\(\[]([^\)\]]*)[\)\]]", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Quoted = new(@"['""]([A-Za-z_-]+)['""]", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, IReadOnlyCollection<string>?> ParsedTemplates = new(StringComparer.Ordinal);

    /// <summary>The effort values a template explicitly accepts, or null when it does not validate them.</summary>
    internal static IReadOnlyCollection<string>? ParseSupported(string? template)
    {
        if (string.IsNullOrEmpty(template)) return null;
        return ParsedTemplates.GetOrAdd(template, t =>
        {
            Match match = AllowedSet.Match(t);
            if (!match.Success) return null;
            var values = Quoted.Matches(match.Groups[1].Value).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();
            return values.Count == 0 ? null : values;
        });
    }

    /// <summary>
    /// The supported value closest to <paramref name="requested"/>, preferring the next cheaper one
    /// (a hidden-reasoning budget is a cost ceiling) and otherwise the cheapest more expensive one.
    /// Unknown vocabularies or unvalidated templates keep the requested value.
    /// </summary>
    internal static string Resolve(string requested, IReadOnlyCollection<string>? supported)
    {
        if (supported == null || supported.Count == 0) return requested;
        string wanted = requested.ToLowerInvariant();
        if (supported.Contains(wanted)) return wanted;
        int wantedRank = Array.IndexOf(Scale, wanted);
        var ranked = supported.Select(s => (Value: s, Rank: Array.IndexOf(Scale, s))).Where(s => s.Rank >= 0).ToList();
        if (wantedRank < 0 || ranked.Count == 0) return requested;
        var lower = ranked.Where(s => s.Rank < wantedRank).OrderByDescending(s => s.Rank).FirstOrDefault();
        if (lower.Value != null) return lower.Value;
        return ranked.OrderBy(s => s.Rank).First().Value;
    }

    private static readonly TimeSpan SupportedTtl = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, (DateTime AtUtc, IReadOnlyCollection<string>? Supported)> SupportedCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops cached template vocabularies (a model swap serves a different template).</summary>
    internal static void Invalidate() => SupportedCache.Clear();

    /// <summary>
    /// Reads the served chat template (llama-server /props); null when unavailable. The template is
    /// large and constant for a loaded model, so a successful read is cached briefly: fetching it on
    /// every chat turn put an extra HTTP round trip and JSON parse in front of the first token.
    /// </summary>
    internal static async Task<IReadOnlyCollection<string>?> GetSupportedAsync(HttpClient http, string endpoint, CancellationToken ct)
    {
        string key = endpoint.TrimEnd('/');
        if (SupportedCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.AtUtc < SupportedTtl)
            return hit.Supported;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using HttpResponseMessage response = await http.GetAsync(key + "/props", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using JsonDocument props = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            IReadOnlyCollection<string>? supported =
                props.RootElement.TryGetProperty("chat_template", out JsonElement template) && template.ValueKind == JsonValueKind.String
                    ? ParseSupported(template.GetString())
                    : null;
            SupportedCache[key] = (DateTime.UtcNow, supported);
            return supported;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException) { return null; }
    }
}
