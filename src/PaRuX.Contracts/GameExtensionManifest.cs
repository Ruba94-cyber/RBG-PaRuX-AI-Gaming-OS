using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Contracts;

// Games-side extensions are DATA, never code. An extension is a folder under Documents\PaRuX\Extensions that holds one
// extension.json manifest (and optionally a publisher hash and a readme). It can contribute:
//   - a library theme (accent colour and optional background, applied through the existing accent system),
//   - metadata "lookup links" (a https address template the OWNER opens in the browser; PaRuX never requests it),
//   - launch presets (named values for the existing launch profile form, copied only when the owner presses Apply).
// Nothing here runs, downloads or contacts the network by itself.

public sealed class GameExtensionTheme
{
    public string Name { get; set; } = string.Empty;
    /// <summary>"dark" or "light": the app theme this palette is meant for. A background is only applied in a matching app theme.</summary>
    public string Appearance { get; set; } = "dark";
    public string Accent { get; set; } = string.Empty;
    public string Background { get; set; } = string.Empty;
}

public sealed class GameExtensionMetadataSource
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>https address with {title} and optionally {platform}; opened by the owner, never fetched by PaRuX.</summary>
    public string UrlTemplate { get; set; } = string.Empty;
}

public sealed class GameExtensionLaunchPreset
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Platforms { get; set; } = new();
    public string ControllerPreset { get; set; } = string.Empty;
    public string PerformanceProfile { get; set; } = string.Empty;
    public bool? Fullscreen { get; set; }
    public List<string> LaunchArgs { get; set; } = new();
}

public sealed class GameExtensionManifest
{
    public int Schema { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public GameExtensionTheme? Theme { get; set; }
    public List<GameExtensionMetadataSource> MetadataSources { get; set; } = new();
    public List<GameExtensionLaunchPreset> LaunchPresets { get; set; } = new();
}

public sealed record GameExtensionIssue(string Path, string Message);

public sealed class GameExtensionValidationResult
{
    public GameExtensionManifest? Manifest { get; init; }
    public List<GameExtensionIssue> Issues { get; init; } = new();
    public bool Valid => Manifest is not null && Issues.Count == 0;
}

/// <summary>One extension as the owner sees it in Settings.</summary>
public sealed class GameExtensionInfo
{
    public string Id { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public bool Valid { get; set; }
    public bool Enabled { get; set; }
    /// <summary>True when the owner enabled this extension but its manifest changed afterwards; it stays off until enabled again.</summary>
    public bool ChangedSinceEnabled { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>"none" (no publisher hash file), "match" or "mismatch". A match shows the file is what the publisher listed; it is not a trust decision.</summary>
    public string PublisherHash { get; set; } = "none";
    public List<string> Issues { get; set; } = new();
    public GameExtensionTheme? Theme { get; set; }
    public List<GameExtensionMetadataSource> MetadataSources { get; set; } = new();
    public List<GameExtensionLaunchPreset> LaunchPresets { get; set; } = new();
}

public sealed class GameExtensionEnabledRequest
{
    public bool Enabled { get; set; }
}

/// <summary>Strict, fail-closed validation of an <c>extension.json</c>: unknown fields, oversize input, bad values and anything executable are refused.</summary>
public static class GameExtensionManifestValidator
{
    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxMetadataSources = 5;
    public const int MaxLaunchPresets = 10;
    public const int MaxLaunchArgs = 8;
    public const int MaxArgLength = 80;

    public static readonly string[] ControllerPresets = { "auto", "xbox", "playstation", "switch", "generic", "keyboard_mouse" };
    public static readonly string[] PerformanceProfiles = { "balanced", "performance", "quality", "quiet" };

    private static readonly Regex IdPattern = new("^[a-z0-9](?:[a-z0-9-]|\\.(?=[a-z0-9])){2,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new("^\\d{1,4}\\.\\d{1,4}\\.\\d{1,4}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HostPattern = new("^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,24}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly char[] ForbiddenArgChars = "&|;<>^`$%\"".ToCharArray();

    private static readonly HashSet<string> RootFields = new(StringComparer.Ordinal)
    { "schema", "id", "name", "version", "author", "description", "homepage", "theme", "metadataSources", "launchPresets" };
    private static readonly HashSet<string> ThemeFields = new(StringComparer.Ordinal) { "name", "appearance", "accent", "background" };
    private static readonly HashSet<string> SourceFields = new(StringComparer.Ordinal) { "id", "name", "urlTemplate" };
    private static readonly HashSet<string> PresetFields = new(StringComparer.Ordinal)
    { "id", "name", "description", "platforms", "controllerPreset", "performanceProfile", "fullscreen", "launchArgs" };

    /// <summary>Whether a folder name is a safe extension id (also the folder name: lower-case letters, digits, dashes and inner dots).</summary>
    public static bool IsValidId(string? id) => id is not null && !id.Contains("..", StringComparison.Ordinal) && IdPattern.IsMatch(id);

    public static GameExtensionValidationResult Validate(byte[]? utf8Json)
    {
        try { return ValidateCore(utf8Json); }
        // A lone surrogate escape in a value or a property name parses, then throws InvalidOperationException on read.
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or FormatException)
        { return Fail("$", "The manifest contains text that is not valid Unicode."); }
    }

    private static GameExtensionValidationResult ValidateCore(byte[]? utf8Json)
    {
        var issues = new List<GameExtensionIssue>();
        if (utf8Json is null || utf8Json.Length == 0) return Fail("$", "The manifest is empty.");
        if (utf8Json.Length > MaxManifestBytes) return Fail("$", "The manifest is larger than " + MaxManifestBytes / 1024 + " KB.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException) { return Fail("$", "The manifest is not valid JSON."); }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("$", "The manifest must be a JSON object.");
            var m = new GameExtensionManifest();
            CheckFields(root, RootFields, "$", issues);

            if (root.TryGetProperty("schema", out JsonElement schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out int sv) && sv == 1) m.Schema = 1;
            else issues.Add(new("schema", "schema must be the number 1."));

            m.Id = Text(root, "id", "id", issues, required: true, max: 64);
            if (m.Id.Length > 0 && !IsValidId(m.Id)) issues.Add(new("id", "id must be 3-64 lower-case letters, digits, dashes or inner dots."));
            m.Name = Display(root, "name", "name", issues, required: true, max: 60);
            m.Version = Text(root, "version", "version", issues, required: true, max: 20);
            if (m.Version.Length > 0 && !VersionPattern.IsMatch(m.Version)) issues.Add(new("version", "version must look like 1.2.3."));
            m.Author = Display(root, "author", "author", issues, required: true, max: 80);
            m.Description = Display(root, "description", "description", issues, required: false, max: 300);
            m.Homepage = Text(root, "homepage", "homepage", issues, required: false, max: 200);
            if (m.Homepage.Length > 0 && !TryHttpsHost(m.Homepage, out _)) issues.Add(new("homepage", "homepage must be a plain https address (shown as text only, never opened or fetched)."));

            if (root.TryGetProperty("theme", out JsonElement theme)) m.Theme = ReadTheme(theme, issues);
            if (root.TryGetProperty("metadataSources", out JsonElement sources)) ReadSources(sources, m, issues);
            if (root.TryGetProperty("launchPresets", out JsonElement presets)) ReadPresets(presets, m, issues);

            if (issues.Count == 0 && m.Theme is null && m.MetadataSources.Count == 0 && m.LaunchPresets.Count == 0)
                issues.Add(new("$", "The extension must contribute a theme, a metadata source or a launch preset."));

            return new GameExtensionValidationResult { Manifest = issues.Count == 0 ? m : null, Issues = issues };
        }
    }

    private static GameExtensionValidationResult Fail(string path, string message) =>
        new() { Issues = new List<GameExtensionIssue> { new(path, message) } };

    // ---- theme ----

    private static GameExtensionTheme? ReadTheme(JsonElement e, List<GameExtensionIssue> issues)
    {
        if (e.ValueKind != JsonValueKind.Object) { issues.Add(new("theme", "theme must be an object.")); return null; }
        CheckFields(e, ThemeFields, "theme", issues);
        var t = new GameExtensionTheme
        {
            Name = Display(e, "name", "theme.name", issues, required: false, max: 60),
            Appearance = Text(e, "appearance", "theme.appearance", issues, required: true, max: 5),
            Accent = Text(e, "accent", "theme.accent", issues, required: true, max: 7),
            Background = Text(e, "background", "theme.background", issues, required: false, max: 7),
        };
        if (t.Appearance is not ("dark" or "light")) issues.Add(new("theme.appearance", "appearance must be \"dark\" or \"light\"."));
        if (!TryHex(t.Accent, out _)) issues.Add(new("theme.accent", "accent must be a colour like #7C5CFF."));
        if (t.Background.Length > 0)
        {
            if (!TryHex(t.Background, out (byte R, byte G, byte B) bg)) issues.Add(new("theme.background", "background must be a colour like #101018."));
            else
            {
                // The app's text is near-white on dark and near-black on light: the background must keep it readable (WCAG AA 4.5:1).
                (byte, byte, byte) text = t.Appearance == "light" ? ((byte)0x0B, (byte)0x0D, (byte)0x10) : ((byte)0xFF, (byte)0xFF, (byte)0xFF);
                if (Contrast(bg, text) < 4.5) issues.Add(new("theme.background", "background is too " + (t.Appearance == "light" ? "dark" : "light") + " for " + t.Appearance + " text (contrast below 4.5:1)."));
            }
        }
        return t;
    }

    // ---- metadata sources ----

    private static void ReadSources(JsonElement e, GameExtensionManifest m, List<GameExtensionIssue> issues)
    {
        if (e.ValueKind != JsonValueKind.Array) { issues.Add(new("metadataSources", "metadataSources must be an array.")); return; }
        if (e.GetArrayLength() > MaxMetadataSources) { issues.Add(new("metadataSources", "At most " + MaxMetadataSources + " metadata sources.")); return; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        foreach (JsonElement s in e.EnumerateArray())
        {
            string path = "metadataSources[" + i++ + "]";
            if (s.ValueKind != JsonValueKind.Object) { issues.Add(new(path, "Each metadata source must be an object.")); continue; }
            CheckFields(s, SourceFields, path, issues);
            var src = new GameExtensionMetadataSource
            {
                Id = Text(s, "id", path + ".id", issues, required: true, max: 40),
                Name = Display(s, "name", path + ".name", issues, required: true, max: 60),
                UrlTemplate = Text(s, "urlTemplate", path + ".urlTemplate", issues, required: true, max: 300),
            };
            if (src.Id.Length > 0 && !IsValidId(src.Id)) issues.Add(new(path + ".id", "id must be 3-40 lower-case letters, digits, dashes or inner dots."));
            if (src.Id.Length > 0 && !ids.Add(src.Id)) issues.Add(new(path + ".id", "Duplicate id."));
            string? reason = ValidateUrlTemplate(src.UrlTemplate);
            if (src.UrlTemplate.Length > 0 && reason is not null) issues.Add(new(path + ".urlTemplate", reason));
            m.MetadataSources.Add(src);
        }
    }

    /// <summary>Null when valid. https only, a real DNS host (no IP literal, localhost, userinfo or port), only {title} and {platform} placeholders, {title} present.</summary>
    public static string? ValidateUrlTemplate(string template)
    {
        if (template.Length is 0 or > 300) return "urlTemplate must be 1-300 characters.";
        if (template.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "urlTemplate cannot contain spaces or control characters.";
        string stripped = template.Replace("{title}", "", StringComparison.Ordinal).Replace("{platform}", "", StringComparison.Ordinal);
        if (stripped.IndexOfAny(new[] { '{', '}' }) >= 0) return "Only the placeholders {title} and {platform} are allowed.";
        if (!template.Contains("{title}", StringComparison.Ordinal)) return "urlTemplate must contain {title}.";
        // The host part cannot hold a placeholder: a placeholder there would let a game name choose the destination.
        int schemeEnd = template.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0 || !template.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "urlTemplate must start with https://.";
        string afterScheme = template[(schemeEnd + 3)..];
        int hostEnd = afterScheme.IndexOfAny(new[] { '/', '?', '#' });
        string authority = hostEnd < 0 ? afterScheme : afterScheme[..hostEnd];
        if (authority.Length == 0 || authority.Contains('{') || authority.Contains('@') || authority.Contains(':')) return "The address must have a plain host name (no user, port or placeholder).";
        if (hostEnd < 0 || afterScheme[hostEnd] != '/') return "urlTemplate needs a path after the host.";
        if (!IsPublicHostName(authority)) return "The host must be a public DNS name (no IP address, localhost or local name).";
        if (template.Contains("..", StringComparison.Ordinal)) return "urlTemplate cannot contain \"..\".";
        return null;
    }

    private static bool IsPublicHostName(string host)
    {
        string h = host.ToLowerInvariant();
        if (!HostPattern.IsMatch(h)) return false;                       // also rejects IP literals (no alphabetic TLD) and single labels
        if (h.EndsWith(".local", StringComparison.Ordinal) || h.EndsWith(".localhost", StringComparison.Ordinal)
            || h.EndsWith(".internal", StringComparison.Ordinal) || h.EndsWith(".lan", StringComparison.Ordinal)
            || h.EndsWith(".home", StringComparison.Ordinal) || h.EndsWith(".corp", StringComparison.Ordinal)
            || h.EndsWith(".test", StringComparison.Ordinal) || h.EndsWith(".invalid", StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool TryHttpsHost(string url, out string host)
    {
        host = string.Empty;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '{' or '}')) return false;
        string rest = url[8..];
        int end = rest.IndexOfAny(new[] { '/', '?', '#' });
        string authority = end < 0 ? rest : rest[..end];
        if (authority.Contains('@') || authority.Contains(':') || !IsPublicHostName(authority)) return false;
        host = authority;
        return true;
    }

    // ---- launch presets ----

    private static void ReadPresets(JsonElement e, GameExtensionManifest m, List<GameExtensionIssue> issues)
    {
        if (e.ValueKind != JsonValueKind.Array) { issues.Add(new("launchPresets", "launchPresets must be an array.")); return; }
        if (e.GetArrayLength() > MaxLaunchPresets) { issues.Add(new("launchPresets", "At most " + MaxLaunchPresets + " launch presets.")); return; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        foreach (JsonElement p in e.EnumerateArray())
        {
            string path = "launchPresets[" + i++ + "]";
            if (p.ValueKind != JsonValueKind.Object) { issues.Add(new(path, "Each launch preset must be an object.")); continue; }
            CheckFields(p, PresetFields, path, issues);
            var preset = new GameExtensionLaunchPreset
            {
                Id = Text(p, "id", path + ".id", issues, required: true, max: 40),
                Name = Display(p, "name", path + ".name", issues, required: true, max: 60),
                Description = Display(p, "description", path + ".description", issues, required: false, max: 200),
                ControllerPreset = Text(p, "controllerPreset", path + ".controllerPreset", issues, required: false, max: 20),
                PerformanceProfile = Text(p, "performanceProfile", path + ".performanceProfile", issues, required: false, max: 20),
            };
            if (preset.Id.Length > 0 && !IsValidId(preset.Id)) issues.Add(new(path + ".id", "id must be 3-40 lower-case letters, digits, dashes or inner dots."));
            if (preset.Id.Length > 0 && !ids.Add(preset.Id)) issues.Add(new(path + ".id", "Duplicate id."));
            if (preset.ControllerPreset.Length > 0 && !ControllerPresets.Contains(preset.ControllerPreset)) issues.Add(new(path + ".controllerPreset", "Unknown controller preset."));
            if (preset.PerformanceProfile.Length > 0 && !PerformanceProfiles.Contains(preset.PerformanceProfile)) issues.Add(new(path + ".performanceProfile", "Unknown performance profile."));
            if (p.TryGetProperty("fullscreen", out JsonElement fs))
            {
                if (fs.ValueKind is JsonValueKind.True or JsonValueKind.False) preset.Fullscreen = fs.GetBoolean();
                else issues.Add(new(path + ".fullscreen", "fullscreen must be true or false."));
            }
            preset.Platforms = StringList(p, "platforms", path + ".platforms", issues, maxItems: 10, maxLength: 60);
            preset.LaunchArgs = StringList(p, "launchArgs", path + ".launchArgs", issues, maxItems: MaxLaunchArgs, maxLength: MaxArgLength);
            for (int a = 0; a < preset.LaunchArgs.Count; a++)
            {
                string? reason = ValidateLaunchArg(preset.LaunchArgs[a]);
                if (reason is not null) issues.Add(new(path + ".launchArgs[" + a + "]", reason));
            }
            m.LaunchPresets.Add(preset);
        }
    }

    /// <summary>Same spirit as the launch profile's own rules (the Core re-validates on save): no shell characters, no placeholders, no parent-folder traversal.</summary>
    public static string? ValidateLaunchArg(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg) || arg.Length > MaxArgLength) return "Each argument must be 1-" + MaxArgLength + " characters.";
        if (arg.Any(char.IsControl)) return "Arguments cannot contain control characters.";
        if (arg.IndexOfAny(ForbiddenArgChars) >= 0) return "Arguments cannot contain shell metacharacters (& | ; < > ^ ` $ % \").";
        if (arg.Contains('{') || arg.Contains('}')) return "Arguments cannot contain placeholders.";
        if (arg.Any(c => char.IsWhiteSpace(c))) return "Each argument is one word without spaces.";
        foreach (string segment in arg.Split('/', '\\', '='))
            if (segment.Trim() == "..") return "Arguments cannot traverse parent folders.";
        // Drive letters, UNC and alternate stream colons are refused anywhere in the argument, also after "=" ("--cfg=C:\\x").
        if (arg.IndexOf(':') >= 0) return "Arguments cannot contain a colon (drive letters and network paths are not allowed).";
        if (arg.Contains("\\\\", StringComparison.Ordinal) || arg.Contains("//", StringComparison.Ordinal)) return "Arguments cannot contain network paths.";
        foreach (string segment in arg.Split('=', '/', '\\'))
            if (segment.Length == 2 && segment[1] == ':') return "Arguments cannot be absolute paths.";
        return null;
    }

    // ---- primitives ----

    private static void CheckFields(JsonElement obj, HashSet<string> allowed, string path, List<GameExtensionIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (!seen.Add(p.Name)) issues.Add(new(path, "Duplicate field \"" + Clip(p.Name) + "\"."));
            else if (!allowed.Contains(p.Name)) issues.Add(new(path, "Unknown field \"" + Clip(p.Name) + "\"."));
        }
    }

    private static string Clip(string s) => s.Length <= 40 ? s : s[..40];

    /// <summary>A plain string field. A wrong type or an oversize value is an issue; control characters and bidi controls are refused.</summary>
    private static string Text(JsonElement obj, string name, string path, List<GameExtensionIssue> issues, bool required, int max)
    {
        if (!obj.TryGetProperty(name, out JsonElement v))
        {
            if (required) issues.Add(new(path, path + " is required."));
            return string.Empty;
        }
        if (v.ValueKind != JsonValueKind.String) { issues.Add(new(path, path + " must be text.")); return string.Empty; }
        string s = v.GetString() ?? string.Empty;
        if (s.Length > max) { issues.Add(new(path, path + " is longer than " + max + " characters.")); return string.Empty; }
        if (required && s.Trim().Length == 0) { issues.Add(new(path, path + " cannot be empty.")); return string.Empty; }
        if (HasUnsafeChars(s)) { issues.Add(new(path, path + " contains control or direction-override characters.")); return string.Empty; }
        return s;
    }

    /// <summary>A text shown to the owner: like <see cref="Text"/> but also refuses angle brackets so nothing looks like markup.</summary>
    private static string Display(JsonElement obj, string name, string path, List<GameExtensionIssue> issues, bool required, int max)
    {
        string s = Text(obj, name, path, issues, required, max);
        if (s.IndexOfAny(new[] { '<', '>' }) >= 0) { issues.Add(new(path, path + " cannot contain < or >.")); return string.Empty; }
        return s;
    }

    private static List<string> StringList(JsonElement obj, string name, string path, List<GameExtensionIssue> issues, int maxItems, int maxLength)
    {
        var list = new List<string>();
        if (!obj.TryGetProperty(name, out JsonElement v)) return list;
        if (v.ValueKind != JsonValueKind.Array) { issues.Add(new(path, path + " must be an array of text.")); return list; }
        if (v.GetArrayLength() > maxItems) { issues.Add(new(path, path + " has more than " + maxItems + " items.")); return list; }
        foreach (JsonElement item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) { issues.Add(new(path, path + " must hold only text.")); return new List<string>(); }
            string s = item.GetString() ?? string.Empty;
            if (s.Length > maxLength) { issues.Add(new(path, "An item of " + path + " is longer than " + maxLength + " characters.")); continue; }
            if (HasUnsafeChars(s)) { issues.Add(new(path, "An item of " + path + " contains control or direction-override characters.")); continue; }
            list.Add(s);
        }
        return list;
    }

    private static bool HasUnsafeChars(string s)
    {
        foreach (System.Text.Rune r in s.EnumerateRunes())
        {
            if (r.Value == 0xFFFD && !s.Contains('\uFFFD')) return true;   // an invalid surrogate was replaced
            switch (System.Text.Rune.GetUnicodeCategory(r))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:            // bidi controls, zero-width joiners, word joiner, tag characters, U+061C
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.OtherNotAssigned:
                    return true;
            }
            int v = r.Value;
            if (v is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF) return true;   // variation selectors
            if (v is 0x180B or 0x180C or 0x180D or 0x180E or 0x3164 or 0xFFA0) return true;   // invisible filler characters
        }
        return false;
    }

    private static bool TryHex(string s, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (s.Length != 7 || s[0] != '#') return false;
        if (!byte.TryParse(s.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            || !byte.TryParse(s.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            || !byte.TryParse(s.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)) return false;
        rgb = (r, g, b);
        return true;
    }

    private static double Luminance((byte R, byte G, byte B) c)
    {
        static double Channel(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG contrast ratio between two colours.</summary>
    public static double Contrast((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Parses #RRGGBB (used by the App to apply a validated theme).</summary>
    public static bool TryParseHex(string? s, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        return s is not null && TryHex(s, out rgb);
    }

    /// <summary>Builds the address a metadata source would open for a title. The title is URL-escaped; the host never changes.</summary>
    public static string? BuildLookupUrl(GameExtensionMetadataSource source, string title, string? platform)
    {
        if (ValidateUrlTemplate(source.UrlTemplate) is not null || string.IsNullOrWhiteSpace(title)) return null;
        string t = title.Length > 120 ? title[..120] : title;
        string url = source.UrlTemplate
            .Replace("{title}", Uri.EscapeDataString(t), StringComparison.Ordinal)
            .Replace("{platform}", Uri.EscapeDataString(platform ?? string.Empty), StringComparison.Ordinal);
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;
    }
}
