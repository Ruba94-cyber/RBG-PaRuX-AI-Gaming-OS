using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PaRuX.Intelligence;

/// <summary>
/// Chat appearance: bubble/background colours, font and size. A closed, validated data shape - never CSS,
/// never a URL or file path. Colours are #RRGGBB only; the font must be one of <see cref="AllowedFonts"/>;
/// unknown JSON properties are rejected; text/bubble pairs must keep a readable contrast. Windows High
/// Contrast always wins over any of this (the host skips the theme), see ChatView.Appearance.
/// </summary>
public sealed class ChatAppearanceTheme
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxJsonBytes = 4_096;
    public const double MinContrast = 4.5;

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonPropertyName("name")] public string Name { get; set; } = "Custom";
    [JsonPropertyName("userBubbleColor")] public string? UserBubbleColor { get; set; }
    [JsonPropertyName("userTextColor")] public string? UserTextColor { get; set; }
    [JsonPropertyName("assistantBubbleColor")] public string? AssistantBubbleColor { get; set; }
    [JsonPropertyName("assistantTextColor")] public string? AssistantTextColor { get; set; }
    [JsonPropertyName("backgroundColor")] public string? BackgroundColor { get; set; }
    [JsonPropertyName("fontFamily")] public string? FontFamily { get; set; }
    [JsonPropertyName("fontSize")] public double? FontSize { get; set; }

    [JsonIgnore] public bool IsEmpty => UserBubbleColor == null && UserTextColor == null && AssistantBubbleColor == null
        && AssistantTextColor == null && BackgroundColor == null && FontFamily == null && FontSize == null;
}

public sealed record ResolvedChatAppearance(
    string? Background, string? UserBubble, string? UserText, string? AssistantBubble, string? AssistantText,
    string? FontFamily, double? FontSize);

public sealed record ChatAppearanceImportResult(bool Ok, ChatAppearanceTheme? Theme, IReadOnlyList<string> Errors);

public static class ChatAppearancePolicy
{
    public static readonly IReadOnlyList<string> AllowedFonts = new[]
    {
        "Segoe UI Variable", "Segoe UI", "Calibri", "Arial", "Verdana", "Tahoma", "Trebuchet MS", "Georgia", "Cambria", "Consolas", "Cascadia Mono"
    };

    public const double MinFontSize = 12, MaxFontSize = 22;

    private static readonly Regex Hex = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NameRx = new(@"^[\p{L}\p{N} _\-\.]{1,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "name", "userBubbleColor", "userTextColor", "assistantBubbleColor", "assistantTextColor",
        "backgroundColor", "fontFamily", "fontSize"
    };

    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static bool IsValidColor(string? value) => value != null && Hex.IsMatch(value);

    public static IReadOnlyList<string> Validate(ChatAppearanceTheme theme)
    {
        var errors = new List<string>();
        if (theme.SchemaVersion != ChatAppearanceTheme.CurrentSchemaVersion) errors.Add("schemaVersion must be " + ChatAppearanceTheme.CurrentSchemaVersion);
        if (string.IsNullOrEmpty(theme.Name) || !NameRx.IsMatch(theme.Name)) errors.Add("name must be 1-40 letters, digits, spaces, - _ .");
        foreach ((string label, string? v) in new[]
        {
            ("userBubbleColor", theme.UserBubbleColor), ("userTextColor", theme.UserTextColor),
            ("assistantBubbleColor", theme.AssistantBubbleColor), ("assistantTextColor", theme.AssistantTextColor),
            ("backgroundColor", theme.BackgroundColor)
        })
            if (v != null && !IsValidColor(v)) errors.Add(label + " must be #RRGGBB");
        if (theme.FontFamily != null && !AllowedFonts.Contains(theme.FontFamily, StringComparer.OrdinalIgnoreCase))
            errors.Add("fontFamily is not in the allowed list");
        if (theme.FontSize is { } size && (!double.IsFinite(size) || size < MinFontSize || size > MaxFontSize))
            errors.Add("fontSize must be between " + MinFontSize + " and " + MaxFontSize);
        if (errors.Count == 0)
        {
            CheckPair(errors, "userTextColor", theme.UserTextColor, theme.UserBubbleColor);
            CheckPair(errors, "assistantTextColor", theme.AssistantTextColor, theme.AssistantBubbleColor ?? theme.BackgroundColor);
        }
        return errors;
    }

    /// <summary>Black or white, whichever reads better on <paramref name="background"/> (#RRGGBB).</summary>
    public static string ReadableTextFor(string background) =>
        Contrast("#111111", background) >= Contrast("#FFFFFF", background) ? "#111111" : "#FFFFFF";

    /// <summary>
    /// What the chat actually paints: the theme plus an optional per-chat background. A colour the owner picked is
    /// used only while it stays readable; otherwise (or when none was picked for a surface that has a custom
    /// background) a readable black/white is chosen. Null means "leave PaRuX's own colour".
    /// </summary>
    public static ResolvedChatAppearance Resolve(ChatAppearanceTheme? theme, string? chatBackground)
    {
        string? background = IsValidColor(chatBackground) ? chatBackground!.ToUpperInvariant() : theme?.BackgroundColor;
        string? userBubble = theme?.UserBubbleColor;
        string? assistantBubble = theme?.AssistantBubbleColor;

        string? Text(string? chosen, string? surface)
        {
            // On PaRuX's own surface the theme's colour cannot be checked here, so a lone text colour is ignored.
            if (surface == null) return null;
            return IsValidColor(chosen) && Contrast(chosen!, surface) >= ChatAppearanceTheme.MinContrast ? chosen : ReadableTextFor(surface);
        }

        return new ResolvedChatAppearance(
            background, userBubble, Text(theme?.UserTextColor, userBubble),
            assistantBubble, Text(theme?.AssistantTextColor, assistantBubble ?? background),
            theme?.FontFamily, theme?.FontSize);
    }

    private static void CheckPair(List<string> errors, string label, string? text, string? background)
    {
        if (text == null || background == null) return; // a half-set pair falls back to the app's own colour; the host re-checks
        if (Contrast(text, background) < ChatAppearanceTheme.MinContrast)
            errors.Add(label + " does not have enough contrast with its bubble (4.5:1 required)");
    }

    /// <summary>Strict import: size cap, valid JSON object, only known properties, then <see cref="Validate"/>.</summary>
    public static ChatAppearanceImportResult Import(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Fail("empty theme");
        if (System.Text.Encoding.UTF8.GetByteCount(json) > ChatAppearanceTheme.MaxJsonBytes) return Fail("theme file is larger than 4 KB");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4, AllowTrailingCommas = false });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Fail("theme must be a JSON object");
            var errors = new List<string>();
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                if (!KnownProperties.Contains(p.Name)) errors.Add("unknown property: " + Truncate(p.Name));
            if (errors.Count > 0) return new ChatAppearanceImportResult(false, null, errors);

            ChatAppearanceTheme? theme = doc.RootElement.Deserialize<ChatAppearanceTheme>();
            if (theme == null) return Fail("theme could not be read");
            IReadOnlyList<string> validation = Validate(theme);
            return validation.Count == 0
                ? new ChatAppearanceImportResult(true, Normalize(theme), Array.Empty<string>())
                : new ChatAppearanceImportResult(false, null, validation);
        }
        catch (JsonException) { return Fail("theme is not valid JSON"); }
    }

    public static string Export(ChatAppearanceTheme theme) => JsonSerializer.Serialize(Normalize(theme), Write);

    private static ChatAppearanceTheme Normalize(ChatAppearanceTheme t) => new()
    {
        SchemaVersion = ChatAppearanceTheme.CurrentSchemaVersion,
        Name = t.Name,
        UserBubbleColor = t.UserBubbleColor?.ToUpperInvariant(),
        UserTextColor = t.UserTextColor?.ToUpperInvariant(),
        AssistantBubbleColor = t.AssistantBubbleColor?.ToUpperInvariant(),
        AssistantTextColor = t.AssistantTextColor?.ToUpperInvariant(),
        BackgroundColor = t.BackgroundColor?.ToUpperInvariant(),
        FontFamily = t.FontFamily == null ? null : AllowedFonts.First(f => f.Equals(t.FontFamily, StringComparison.OrdinalIgnoreCase)),
        FontSize = t.FontSize
    };

    private static ChatAppearanceImportResult Fail(string message) => new(false, null, new[] { message });
    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "…";

    /// <summary>WCAG contrast ratio of two #RRGGBB colours (1..21).</summary>
    public static double Contrast(string foreground, string background)
    {
        double a = Luminance(foreground), b = Luminance(background);
        double hi = Math.Max(a, b), lo = Math.Min(a, b);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int start)
        {
            double c = int.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}
