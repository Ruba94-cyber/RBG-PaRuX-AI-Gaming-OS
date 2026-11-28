namespace PaRuX.Contracts;

/// <summary>A setting change the model proposed in chat. It is only a proposal: the desktop UI applies it after the owner presses Apply.</summary>
public sealed class ChatSettingProposalDto
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime ExpiresUtc { get; set; }
}

/// <summary>
/// The only settings a chat may even propose to change: small, reversible, and unrelated to security. Everything else
/// (pairing, LAN, Corporate, updates, Gaming hard-off, tokens, keys, paths, downloads) is not in the list and is
/// refused by name, so a model or an injected page cannot widen it. A proposal never changes anything by itself: the
/// desktop UI shows an Apply / Cancel card and applies the change only after the owner confirms.
/// </summary>
public static class ChatSettingsAllowList
{
    public sealed record Definition(string Key, IReadOnlyList<string> Values, string Description, bool BlockedInCorporate);

    public static readonly IReadOnlyList<Definition> All = new[]
    {
        new Definition("theme", new[] { "system", "light", "dark" }, "App theme", false),
        new Definition("language", new[] { "en", "it" }, "Interface language (English or Italian)", false),
        new Definition("interface", new[] { "simple", "advanced" }, "Simple or Advanced interface", true),
        new Definition("textProfile", new[] { "standard", "uncensored" }, "Default chat model profile", true),
        new Definition("webSearch", new[] { "on", "off" }, "Web search (Internet access for search)", true),
        new Definition("voiceAutoSend", new[] { "on", "off" }, "Send voice messages automatically after transcription", false)
    };

    /// <summary>Validates one proposal. Unknown keys and values are refused with an explanation the model can relay.</summary>
    public static bool TryValidate(string? key, string? value, bool corporate, out ChatSettingChange? change, out string error)
    {
        change = null;
        string k = (key ?? string.Empty).Trim();
        string v = (value ?? string.Empty).Trim().ToLowerInvariant();
        Definition? definition = All.FirstOrDefault(d => string.Equals(d.Key, k, StringComparison.OrdinalIgnoreCase));
        if (definition == null)
        {
            error = "That setting cannot be changed from chat. Allowed settings: " + string.Join(", ", All.Select(d => d.Key))
                + ". Security, pairing, network, Corporate, update and Gaming settings are never available here.";
            return false;
        }
        if (corporate && definition.BlockedInCorporate)
        {
            error = "This setting is locked in Corporate mode.";
            return false;
        }
        if (!definition.Values.Contains(v, StringComparer.Ordinal))
        {
            error = "Invalid value for " + definition.Key + ". Allowed values: " + string.Join(", ", definition.Values) + ".";
            return false;
        }
        change = new ChatSettingChange(definition.Key, v, definition.Description);
        error = string.Empty;
        return true;
    }
}

public sealed record ChatSettingChange(string Key, string Value, string Description);
