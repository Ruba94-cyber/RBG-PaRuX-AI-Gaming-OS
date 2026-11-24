using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace PaRuX.Server.Orchestration.Gaming;

internal enum GameModFormat { Ini, Properties, Cfg, Json, Xml, Yaml, Toml, Lua, Text, Csv, Png }

/// <summary>
/// Allow-list and per-format validation for AI game-mod change sets. Only inert data formats are
/// accepted: there is no executable, library, script or archive extension on the list, and the
/// validators never run, load or evaluate content. Lua is accepted as a data format only: the
/// change may not call system APIs and may not add control flow.
/// </summary>
internal static class GameModFileValidator
{
    internal const int MaxTextBytes = 64 * 1024;
    internal const int MaxImageBytes = 4 * 1024 * 1024;
    internal const int MaxPathLength = 240;
    internal const int MaxSegments = 12;

    private static readonly Dictionary<string, GameModFormat> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ini"] = GameModFormat.Ini,
        [".properties"] = GameModFormat.Properties,
        [".cfg"] = GameModFormat.Cfg, [".conf"] = GameModFormat.Cfg,
        [".keys"] = GameModFormat.Cfg, [".bindings"] = GameModFormat.Cfg, [".keymap"] = GameModFormat.Cfg,
        [".json"] = GameModFormat.Json,
        [".xml"] = GameModFormat.Xml,
        [".yaml"] = GameModFormat.Yaml, [".yml"] = GameModFormat.Yaml,
        [".toml"] = GameModFormat.Toml,
        [".lua"] = GameModFormat.Lua,
        [".txt"] = GameModFormat.Text, [".lang"] = GameModFormat.Text, [".loc"] = GameModFormat.Text,
        [".strings"] = GameModFormat.Text,
        [".csv"] = GameModFormat.Csv, [".tsv"] = GameModFormat.Csv,
        [".png"] = GameModFormat.Png,
    };

    private static readonly HashSet<string> LuaForbiddenIdentifiers = new(StringComparer.Ordinal)
    {
        "os", "io", "require", "dofile", "loadfile", "loadstring", "load", "package", "debug", "ffi", "jit",
        "_G", "_ENV", "getfenv", "setfenv", "rawset", "rawget", "setmetatable", "getmetatable", "collectgarbage",
        "string.dump", "coroutine", "module", "newproxy",
    };

    private static readonly string[] LuaLogicKeywords = ["function", "while", "repeat", "goto"];

    internal static bool TryGetFormat(string? path, out GameModFormat format)
    {
        format = default;
        return !string.IsNullOrEmpty(path) && ByExtension.TryGetValue(Path.GetExtension(path), out format);
    }

    internal static bool IsImage(GameModFormat format) => format == GameModFormat.Png;

    /// <summary>Normalises to forward slashes. Throws ArgumentException for anything unsafe.</summary>
    internal static string SafeRelativePath(string? path)
    {
        const string Refusal = "Choose a safe relative path to an existing .ini/.cfg/.json/.xml/.yaml/.toml/.lua/.txt/.csv or .png file inside the game folder.";
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength || path.Any(char.IsControl) ||
            path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\') || Path.IsPathRooted(path) ||
            path.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            throw new ArgumentException(Refusal);
        string normalized = path.Replace('\\', '/');
        string[] segments = normalized.Split('/');
        if (segments.Length is < 1 or > MaxSegments || segments.Any(s =>
                s.Length is < 1 or > 100 || s is "." or ".." || s.EndsWith(' ') || s.EndsWith('.') ||
                s.StartsWith(".parux-", StringComparison.OrdinalIgnoreCase) || IsReservedDeviceName(s)))
            throw new ArgumentException(Refusal);
        if (!TryGetFormat(normalized, out _)) throw new ArgumentException(Refusal);
        return normalized;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        string name = segment.Split('.')[0].ToUpperInvariant();
        if (name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") return true;
        return name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) ||
            name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9';
    }

    // ---------------------------------------------------------------- text

    internal static string DecodeStrictUtf8(byte[] bytes)
    {
        if (bytes.Length > MaxTextBytes)
            throw new InvalidDataException("The file is larger than the 64 KiB text limit.");
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Only UTF-8 text files can be modified."); }
    }

    /// <summary>
    /// Validates the proposed text. <paramref name="original"/> (when the file already exists) lets
    /// the validators keep the schema stable: JSON root kind, XML root element, CSV header.
    /// </summary>
    internal static void ValidateText(GameModFormat format, string text, string? original)
    {
        if (text is null) throw new ArgumentException("The proposed file content is missing.");
        if (Encoding.UTF8.GetByteCount(text) > MaxTextBytes)
            throw new ArgumentException("The proposed file is larger than the 64 KiB text limit.");
        foreach (char c in text)
            if (char.IsControl(c) && c is not ('\t' or '\r' or '\n'))
                throw new ArgumentException("The proposed file contains binary or control characters.");
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("The proposed file is empty.");
        try { _ = new UTF8Encoding(false, true).GetBytes(text); }
        catch (EncoderFallbackException) { throw new ArgumentException("The proposed file is not valid Unicode text."); }
        switch (format)
        {
            case GameModFormat.Ini: ValidateIniLike(text, allowSections: true, allowColon: false, strict: true); break;
            case GameModFormat.Properties: ValidateIniLike(text, allowSections: false, allowColon: true, strict: true); break;
            case GameModFormat.Cfg: ValidateIniLike(text, allowSections: true, allowColon: true, strict: false); break;
            case GameModFormat.Json: ValidateJson(text, original); break;
            case GameModFormat.Xml: ValidateXml(text, original); break;
            case GameModFormat.Yaml: ValidateYaml(text); break;
            case GameModFormat.Toml: ValidateToml(text); break;
            case GameModFormat.Lua: ValidateLua(text, original); break;
            case GameModFormat.Csv: ValidateCsv(text, original); break;
            case GameModFormat.Text: break;
            default: throw new ArgumentException("This format is not a text format.");
        }
    }

    private static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static void ValidateIniLike(string text, bool allowSections, bool allowColon, bool strict)
    {
        int number = 0;
        foreach (string raw in Lines(text))
        {
            number++;
            if (raw.Length > 4096)
                throw new ArgumentException($"Line {number} is longer than 4096 characters.");
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith("//") ||
                (!allowSections && line.StartsWith('!'))) continue;
            if (line.StartsWith('['))
            {
                if (!allowSections) throw new ArgumentException($"Line {number}: section headers are not valid here.");
                if (!line.EndsWith(']') || line.Length < 3)
                    throw new ArgumentException($"Line {number}: the section header is not closed with ].");
                continue;
            }
            if (!strict) continue;
            if (!(line.Contains('=') || (allowColon && line.Contains(':'))))
                throw new ArgumentException($"Line {number} is not a 'key=value' setting, section or comment.");
        }
    }

    private static void ValidateJson(string text, string? original)
    {
        JsonDocumentOptions options = new() { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow };
        JsonValueKind rootKind;
        try { using JsonDocument doc = JsonDocument.Parse(text, options); rootKind = doc.RootElement.ValueKind; }
        catch (JsonException ex) { throw new ArgumentException("The proposed JSON is invalid: " + ex.Message); }
        if (original is null) return;
        try
        {
            using JsonDocument before = JsonDocument.Parse(original, options);
            if (before.RootElement.ValueKind != rootKind)
                throw new ArgumentException("The proposed JSON changes the type of the root value; refused.");
        }
        catch (JsonException) { /* the original is not strict JSON: nothing to compare against */ }
    }

    private static string? XmlRootName(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), SafeXmlSettings());
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element) return reader.LocalName;
        return null;
    }

    private static XmlReaderSettings SafeXmlSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxTextBytes * 2,
        MaxCharactersFromEntities = 0,
        IgnoreComments = false,
    };

    private static void ValidateXml(string text, string? original)
    {
        string? root = null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), SafeXmlSettings());
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.ProcessingInstruction)
                    throw new ArgumentException("XML processing instructions are not allowed.");
                if (reader.NodeType == XmlNodeType.Element && root is null) root = reader.LocalName;
            }
        }
        catch (XmlException ex) { throw new ArgumentException("The proposed XML is not well-formed: " + ex.Message); }
        if (root is null) throw new ArgumentException("The proposed XML has no root element.");
        if (original is null) return;
        string? before;
        try { before = XmlRootName(original); }
        catch (XmlException) { return; }
        if (before is not null && !string.Equals(before, root, StringComparison.Ordinal))
            throw new ArgumentException("The proposed XML changes the root element; refused.");
    }

    private static void ValidateYaml(string text)
    {
        int number = 0;
        foreach (string line in Lines(text))
        {
            number++;
            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t')) indent++;
            if (line[..indent].Contains('\t'))
                throw new ArgumentException($"Line {number}: YAML must be indented with spaces, not tabs.");
            string body = line[indent..];
            if (body.StartsWith('#')) continue;
            if (body.Contains("!!") || body.Contains("!<") || body.TrimStart('-', ' ').StartsWith("!"))
                throw new ArgumentException($"Line {number}: YAML type tags are not allowed.");
        }
    }

    private static void ValidateToml(string text)
    {
        int number = 0;
        string? multiline = null;
        foreach (string raw in Lines(text))
        {
            number++;
            string line = raw.Trim();
            if (multiline is not null)
            {
                if (line.Contains(multiline)) multiline = null;
                continue;
            }
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                bool arrayTable = line.StartsWith("[[");
                string closer = arrayTable ? "]]" : "]";
                int end = line.IndexOf(closer, StringComparison.Ordinal);
                if (end < 0 || (line[(end + closer.Length)..].Trim() is { Length: > 0 } rest && !rest.StartsWith('#')))
                    throw new ArgumentException($"Line {number}: the TOML table header is not closed.");
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || line[(eq + 1)..].Trim().Length == 0)
                throw new ArgumentException($"Line {number}: expected 'key = value'.");
            string value = line[(eq + 1)..].Trim();
            foreach (string triple in new[] { "\"\"\"", "'''" })
                if (value.StartsWith(triple) && value.IndexOf(triple, triple.Length, StringComparison.Ordinal) < 0)
                    multiline = triple;
        }
        if (multiline is not null) throw new ArgumentException("A TOML multi-line string is not closed.");
    }

    /// <summary>Strips Lua comments and string literals; the remainder is code skeleton only.</summary>
    private static string LuaSkeleton(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                i += 2;
                int level = LongBracketLevel(text, i);
                if (level >= 0) i = SkipLongBracket(text, i, level);
                else while (i < text.Length && text[i] != '\n') i++;
                sb.Append(' ');
            }
            else if (c is '"' or '\'')
            {
                char quote = c; i++;
                while (i < text.Length && text[i] != quote && text[i] != '\n')
                    i += text[i] == '\\' ? 2 : 1;
                if (i >= text.Length || text[i] == '\n')
                    throw new ArgumentException("A Lua string literal is not closed.");
                i++;
                sb.Append("\"\"");
            }
            else if (c == '[' && LongBracketLevel(text, i) >= 0)
            {
                i = SkipLongBracket(text, i, LongBracketLevel(text, i));
                sb.Append("\"\"");
            }
            else { sb.Append(c); i++; }
        }
        return sb.ToString();
    }

    private static int LongBracketLevel(string text, int i)
    {
        if (i >= text.Length || text[i] != '[') return -1;
        int j = i + 1, level = 0;
        while (j < text.Length && text[j] == '=') { level++; j++; }
        return j < text.Length && text[j] == '[' ? level : -1;
    }

    private static int SkipLongBracket(string text, int i, int level)
    {
        string closer = "]" + new string('=', level) + "]";
        int end = text.IndexOf(closer, i, StringComparison.Ordinal);
        if (end < 0) throw new ArgumentException("A Lua long string or comment is not closed.");
        return end + closer.Length;
    }

    private static void ValidateLua(string text, string? original)
    {
        string code = LuaSkeleton(text);
        int curly = 0, paren = 0, square = 0;
        foreach (char c in code)
        {
            switch (c)
            {
                case '{': curly++; break;
                case '}': curly--; break;
                case '(': paren++; break;
                case ')': paren--; break;
                case '[': square++; break;
                case ']': square--; break;
            }
            if (curly < 0 || paren < 0 || square < 0)
                throw new ArgumentException("The Lua brackets are unbalanced.");
        }
        if (curly != 0 || paren != 0 || square != 0)
            throw new ArgumentException("The Lua brackets are unbalanced.");
        var words = System.Text.RegularExpressions.Regex.Matches(code, @"[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?");
        foreach (System.Text.RegularExpressions.Match m in words)
        {
            string word = m.Value;
            string head = word.Split('.')[0];
            if (LuaForbiddenIdentifiers.Contains(word) || LuaForbiddenIdentifiers.Contains(head))
                throw new ArgumentException($"Lua mods may only change data: '{head}' is not allowed (no system, file, loader or debug access).");
        }
        string? originalCode = original is null ? null : SafeLuaSkeleton(original);
        foreach (string keyword in LuaLogicKeywords)
        {
            int added = CountWord(code, keyword);
            int before = originalCode is null ? 0 : CountWord(originalCode, keyword);
            if (added > before)
                throw new ArgumentException($"Lua mods may change values but not add logic: a new '{keyword}' was proposed.");
        }
    }

    private static string? SafeLuaSkeleton(string text)
    {
        try { return LuaSkeleton(text); }
        catch (ArgumentException) { return string.Empty; }
    }

    private static int CountWord(string code, string word) =>
        System.Text.RegularExpressions.Regex.Matches(code, @"\b" + word + @"\b").Count;

    private static void ValidateCsv(string text, string? original)
    {
        if (original is null) return;
        string before = Lines(original).FirstOrDefault() ?? "";
        string after = Lines(text).FirstOrDefault() ?? "";
        if (!string.Equals(before.Trim('﻿', ' '), after.Trim('﻿', ' '), StringComparison.Ordinal))
            throw new ArgumentException("The proposed table changes its header row; refused.");
    }

    /// <summary>Non-blocking observations for the review screen (removed settings and similar).</summary>
    internal static IReadOnlyList<string> Warnings(GameModFormat format, string? before, string after)
    {
        var warnings = new List<string>();
        if (before is null) { warnings.Add("NEW_FILE"); return warnings; }
        if (format is GameModFormat.Ini or GameModFormat.Cfg or GameModFormat.Properties)
        {
            int lost = SettingKeys(before).Except(SettingKeys(after), StringComparer.OrdinalIgnoreCase).Count();
            if (lost > 0) warnings.Add("REMOVES_SETTINGS:" + lost);
        }
        else if (format == GameModFormat.Json)
        {
            try
            {
                using JsonDocument a = JsonDocument.Parse(before), b = JsonDocument.Parse(after);
                if (a.RootElement.ValueKind == JsonValueKind.Object && b.RootElement.ValueKind == JsonValueKind.Object)
                {
                    int lost = a.RootElement.EnumerateObject().Select(p => p.Name)
                        .Except(b.RootElement.EnumerateObject().Select(p => p.Name)).Count();
                    if (lost > 0) warnings.Add("REMOVES_SETTINGS:" + lost);
                }
            }
            catch (JsonException) { }
        }
        return warnings;
    }

    private static IEnumerable<string> SettingKeys(string text)
    {
        string section = "";
        foreach (string raw in Lines(text))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line; continue; }
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith("//")) continue;
            int eq = line.IndexOf('=');
            if (eq > 0) yield return section + "/" + line[..eq].Trim();
        }
    }

    // ---------------------------------------------------------------- images

    /// <summary>
    /// Structural PNG check shared by every texture change: signature, IHDR bounds, every chunk's CRC,
    /// IEND present and nothing after it (no appended payload). A replacement keeps the original dimensions.
    /// </summary>
    internal static (int Width, int Height) ValidatePng(byte[] bytes, (int Width, int Height)? mustMatch)
    {
        if (bytes is null || bytes.Length > MaxImageBytes)
            throw new ArgumentException("The PNG is missing or larger than 4 MiB.");
        (int w, int h) = PngAssetGuard.ReadDimensions(bytes);
        if (w < 16 || h < 16)
            throw new ArgumentException("The PNG must be at least 16 pixels per side.");
        if (mustMatch is { } expected && (expected.Width != w || expected.Height != h))
            throw new ArgumentException($"The replacement PNG must keep the original size {expected.Width}x{expected.Height}.");
        int pos = 8;
        bool seenEnd = false;
        while (pos + 12 <= bytes.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos, 4));
            if (length > int.MaxValue || pos + 12L + length > bytes.Length)
                throw new ArgumentException("The PNG has a truncated or oversized chunk.");
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos + 8 + (int)length, 4));
            if (Crc32(bytes.AsSpan(pos + 4, 4 + (int)length)) != stored)
                throw new ArgumentException("The PNG has a corrupted chunk (CRC mismatch).");
            bool isEnd = bytes.AsSpan(pos + 4, 4).SequenceEqual("IEND"u8);
            pos += 12 + (int)length;
            if (isEnd) { seenEnd = true; break; }
        }
        if (!seenEnd) throw new ArgumentException("The PNG has no IEND chunk.");
        if (pos != bytes.Length)
            throw new ArgumentException("The PNG has extra data after its end; refused.");
        return (w, h);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
