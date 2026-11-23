using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Playnite;

/// <summary>Pure parsing of the GitHub release metadata of Mike-Aniki/Aniki-ReMake (MIT).</summary>
internal static class PlayniteThemeReleaseParser
{
    internal const string Repository = "Mike-Aniki/Aniki-ReMake";
    internal const string SourceUrl = "https://github.com/" + Repository;
    internal static readonly string[] AllowedDownloadHosts =
        { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };
    internal static readonly string[] AllowedLicenseHosts = { "raw.githubusercontent.com" };

    // Aniki_ReMake_<theme id guid>_<version>.pthm: the guid part is the folder Playnite expects (it is the theme Id).
    private static readonly Regex AssetName = new(
        @"^(Aniki_ReMake_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})_[0-9A-Za-z_.]{1,32}\.pthm$", RegexOptions.Compiled);
    private static readonly Regex Sha256Digest = new(@"^sha256:([0-9a-fA-F]{64})$", RegexOptions.Compiled);

    public static PlayniteThemePlanDto Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
                return Unavailable("The latest Aniki ReMake release is a draft.");
            if (root.TryGetProperty("prerelease", out JsonElement pre) && pre.ValueKind == JsonValueKind.True)
                return Unavailable("The latest Aniki ReMake release is a pre-release; PaRuX only installs stable releases.");
            string? tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 32 || !Regex.IsMatch(tag, @"^[0-9A-Za-z._-]+$"))
                return Unavailable("The release has no usable version.");
            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                return Unavailable("The release lists no files.");
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                Match nameMatch = name is null ? Match.Empty : AssetName.Match(name);
                if (!nameMatch.Success) continue;
                string? url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                if (!IsAllowedDownloadUrl(url) || !url!.EndsWith("/" + name, StringComparison.Ordinal)) continue;
                long size = asset.TryGetProperty("size", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                if (size <= 0) continue;
                string? digest = asset.TryGetProperty("digest", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                Match m = digest is null ? Match.Empty : Sha256Digest.Match(digest);
                return new PlayniteThemePlanDto
                {
                    Available = true, Version = tag, AssetName = name, DownloadUrl = url, SizeBytes = size,
                    Sha256 = m.Success ? m.Groups[1].Value.ToLowerInvariant() : null,
                    ThemeFolderName = nameMatch.Groups[1].Value,
                    Message = m.Success ? null : "The release does not publish a SHA-256 checksum; the download cannot be verified."
                };
            }
            return Unavailable("The release has no Playnite theme package (.pthm).");
        }
        catch (JsonException) { return Unavailable("The release information could not be read."); }
        catch (InvalidOperationException) { return Unavailable("The release information could not be read."); }
        catch (FormatException) { return Unavailable("The release information could not be read."); }
    }

    internal static bool IsAllowedDownloadUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps &&
        AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) &&
        (uri.Host != "github.com" || uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal));

    internal static bool IsAllowedFinalHost(Uri? uri, bool license) =>
        uri is not null && uri.Scheme == Uri.UriSchemeHttps &&
        (license ? AllowedLicenseHosts : AllowedDownloadHosts).Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    private static PlayniteThemePlanDto Unavailable(string message) => new() { Available = false, Message = message };
}

internal sealed record ThemeArchiveInfo(string Id, string Name, string Version, string? LicenseFile);

/// <summary>
/// Validates a Playnite theme package (.pthm = zip) completely BEFORE writing anything, then extracts it into an empty staging folder.
/// It refuses path traversal, absolute or drive-qualified names, duplicate entries, oversize or lying archives, and a package whose
/// theme.yaml is not a Fullscreen "Aniki ReMake" theme. Extraction never leaves the staging folder.
/// </summary>
internal static class PlayniteThemeArchive
{
    internal const int MaxEntries = 20_000;
    internal const long MaxTotalBytes = 1_500_000_000L;
    internal const long MaxEntryBytes = 700_000_000L;
    private static readonly Regex ThemeId = new(
        @"^Aniki_ReMake_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    private static readonly Regex LicenseName = new(@"^(LICENSE|LICENCE|COPYING)(\.[A-Za-z]{2,4})?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 240) return false;
        if (name[0] is '/' or '\\') return false;
        foreach (char c in name) if (c < 32 || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return false;
        foreach (string segment in name.Split('/', '\\'))
        {
            if (segment == "." || segment == "..") return false;
            if (segment.Length > 0 && (segment.EndsWith('.') || segment.EndsWith(' '))) return false;
        }
        return true;
    }

    internal static (string Id, string Name, string Version, string Mode)? ParseThemeYaml(string text)
    {
        string? id = null, name = null, version = null, mode = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            int i = line.IndexOf(':');
            if (i <= 0) continue;
            string key = line[..i].Trim(), value = line[(i + 1)..].Trim().Trim('"', '\'');
            switch (key)
            {
                case "Id": id = value; break;
                case "Name": name = value; break;
                case "Version": version = value; break;
                case "Mode": mode = value; break;
            }
        }
        return id is null || name is null || mode is null ? null : (id, name, version ?? string.Empty, mode);
    }

    public static (ThemeArchiveInfo? Info, string? Error) Validate(ZipArchive zip)
    {
        if (zip.Entries.Count == 0) return (null, "The theme package is empty.");
        if (zip.Entries.Count > MaxEntries) return (null, "The theme package has too many files.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        ZipArchiveEntry? yaml = null;
        string? license = null;
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (!IsSafeEntryName(e.FullName)) return (null, "The theme package contains an unsafe path and was refused.");
            if (!seen.Add(e.FullName.Replace('\\', '/').TrimEnd('/'))) return (null, "The theme package contains duplicate entries and was refused.");
            if (e.Length < 0 || e.Length > MaxEntryBytes) return (null, "The theme package contains an oversized file and was refused.");
            total += e.Length;
            if (total > MaxTotalBytes) return (null, "The theme package is larger than allowed and was refused.");
            if (e.FullName.Equals("theme.yaml", StringComparison.Ordinal)) yaml = e;
            else if (!e.FullName.Contains('/') && !e.FullName.Contains('\\') && LicenseName.IsMatch(e.FullName)) license ??= e.FullName;
        }
        if (yaml is null) return (null, "The package has no theme.yaml at its root; it is not a Playnite theme.");
        if (yaml.Length > 64 * 1024) return (null, "theme.yaml is unexpectedly large.");
        string yamlText;
        using (var reader = new StreamReader(yaml.Open(), Encoding.UTF8)) yamlText = reader.ReadToEnd();
        var meta = ParseThemeYaml(yamlText);
        if (meta is null) return (null, "theme.yaml could not be read.");
        if (!string.Equals(meta.Value.Mode, "Fullscreen", StringComparison.OrdinalIgnoreCase))
            return (null, "The package is not a Playnite Fullscreen theme.");
        if (!ThemeId.IsMatch(meta.Value.Id) || meta.Value.Name.IndexOf("Aniki ReMake", StringComparison.OrdinalIgnoreCase) < 0)
            return (null, "The package is not the Aniki ReMake theme.");
        return (new ThemeArchiveInfo(meta.Value.Id, meta.Value.Name, meta.Value.Version, license), null);
    }

    /// <summary>Validates then extracts into <paramref name="stagingDir"/> (created here, must not exist yet).</summary>
    public static (ThemeArchiveInfo? Info, string? Error) Extract(string zipPath, string stagingDir, CancellationToken ct)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            var (info, error) = Validate(zip);
            if (info is null) return (null, error);
            string root = Path.GetFullPath(stagingDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(stagingDir);
            long written = 0;
            byte[] buffer = new byte[81920];
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string dest = Path.GetFullPath(Path.Combine(stagingDir, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return (null, "The theme package contains an unsafe path and was refused.");
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using Stream input = e.Open();
                using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
                long entryBytes = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    entryBytes += read;
                    written += read;
                    // Headers can lie: count what is really written.
                    if (entryBytes > MaxEntryBytes || entryBytes > e.Length || written > MaxTotalBytes)
                        return (null, "The theme package is larger than it declared and was refused.");
                    output.Write(buffer, 0, read);
                }
                if (entryBytes != e.Length) return (null, "The theme package is damaged (a file is shorter than declared).");
            }
            return (info, null);
        }
        catch (InvalidDataException) { return (null, "The theme package is damaged or incomplete."); }
        catch (IOException ex) when (ex is not DirectoryNotFoundException) { return (null, "The theme package could not be unpacked: " + ex.GetType().Name + "."); }
        catch (UnauthorizedAccessException) { return (null, "The theme package could not be unpacked (access denied)."); }
    }
}

/// <summary>
/// The only edit PaRuX ever makes to Playnite's configuration: replace the value of the top-level "Theme" property in
/// fullscreenConfig.json, byte for byte everywhere else (formatting, BOM and every other setting are preserved).
/// </summary>
internal static class PlayniteThemeConfig
{
    internal const string FileName = "fullscreenConfig.json";
    internal const long MaxBytes = 4 * 1024 * 1024;
    private static readonly Regex SafeThemeName = new(@"^[A-Za-z0-9_.\-]{1,128}$", RegexOptions.Compiled);

    internal static bool IsSafeThemeName(string? name) => name is not null && SafeThemeName.IsMatch(name);

    public static string FilePath(string dataFolder) => Path.Combine(dataFolder, FileName);

    private static (int Start, int End)? FindThemeValue(byte[] bytes, out int bomLength)
    {
        bomLength = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            var reader = new Utf8JsonReader(bytes.AsSpan(bomLength), new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("Theme"))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String) return null;
                    int end = bomLength + (int)reader.BytesConsumed;
                    int start = bomLength + (int)reader.TokenStartIndex;
                    return (start, end);
                }
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Reads the selected Fullscreen theme without writing anything.</summary>
    public static (bool Readable, string? Theme) Read(string configFile)
    {
        try
        {
            var info = new FileInfo(configFile);
            if (!info.Exists || info.Length > MaxBytes) return (false, null);
            byte[] bytes = File.ReadAllBytes(configFile);
            var span = FindThemeValue(bytes, out int bom);
            if (span is null) return (false, null);
            using JsonDocument doc = JsonDocument.Parse(bytes.AsMemory(span.Value.Start, span.Value.End - span.Value.Start));
            return (true, doc.RootElement.GetString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return (false, null); }
    }

    /// <summary>The new file content with only the Theme value replaced, or null when the file has no top-level string "Theme".</summary>
    public static byte[]? WithTheme(byte[] original, string theme)
    {
        if (!IsSafeThemeName(theme)) return null;
        var span = FindThemeValue(original, out _);
        if (span is null) return null;
        byte[] value = JsonSerializer.SerializeToUtf8Bytes(theme);
        var result = new byte[original.Length - (span.Value.End - span.Value.Start) + value.Length];
        Buffer.BlockCopy(original, 0, result, 0, span.Value.Start);
        Buffer.BlockCopy(value, 0, result, span.Value.Start, value.Length);
        Buffer.BlockCopy(original, span.Value.End, result, span.Value.Start + value.Length, original.Length - span.Value.End);
        return result;
    }
}
