using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Server.Knowledge;

/// <summary>The kinds of "Add source" connectors besides a plain website. Stored in the web-source manifest as <c>Connector</c>.</summary>
public static class ConnectorKind
{
    public const string GitHub = "github";
    public const string YouTube = "youtube";
    public const string Confluence = "confluence";
    public const string Paperless = "paperless";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { GitHub, YouTube, Confluence, Paperless };
    public static bool IsKnown(string? kind) => kind != null && All.Contains(kind);
}

/// <summary>One imported page of text with the address a citation should show.</summary>
public sealed record ConnectorDoc(string Url, string Title, string Text);

/// <summary>What the person asked for. The token is only ever read from the secret store and held in memory for the run.</summary>
public sealed record ConnectorRequest(string Kind, string Target, string? Ref, string? PathPrefix, string? Language,
    string? Email, string? Token, bool IncludeChildren, int? MaxItems);

public sealed class ConnectorResult
{
    public List<ConnectorDoc> Docs { get; } = new();
    /// <summary>"done" or why it stopped early ("limit" when a size or count cap cut the import short).</summary>
    public string StoppedBecause { get; set; } = "done";
    public List<string> Notes { get; } = new();
    public int SkippedFiles { get; set; }
}

/// <summary>An honest, user-readable failure. <see cref="Code"/> is stable (used for localisation); the message never holds a token.</summary>
public sealed class ConnectorException : Exception
{
    public string Code { get; }
    public ConnectorException(string code, string message) : base(message) { Code = code; }
}

public sealed record ConnectorLimits
{
    public int MaxDocs { get; init; } = 150;
    public long MaxBytesPerDoc { get; init; } = 200_000;
    public long MaxTotalBytes { get; init; } = 8_000_000;
    public int MaxCharsPerDoc { get; init; } = 300_000;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public int MaxRedirects { get; init; } = 3;
    public int MaxChildPages { get; init; } = 25;
    public const int HardMaxDocs = 300;
    public const string UserAgent = "PaRuXKnowledgeConnector/1.0 (user-initiated; local)";
}

internal sealed record ConnectorResponse(int Status, byte[]? Body, bool TooLarge, string? ContentType, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Bounded, https-only GET with a fixed host allow-list. Redirects are followed only to an allowed https host (the credential is
/// re-applied by <c>configure</c> only for those hosts), responses are read up to a byte limit and every request has a timeout.
/// </summary>
internal sealed class ConnectorHttp
{
    private readonly HttpClient _http;
    private readonly ConnectorLimits _limits;
    private readonly bool _allowLoopbackHttp;
    public ConnectorHttp(HttpClient http, ConnectorLimits limits, bool allowLoopbackHttp = false)
    { _http = http; _limits = limits; _allowLoopbackHttp = allowLoopbackHttp; }

    /// <summary>Plain http is only ever allowed for a loopback host (a self-hosted service on this very PC); never for a LAN or public host.</summary>
    internal static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? ip) && IPAddress.IsLoopback(ip));

    public async Task<ConnectorResponse> GetAsync(Uri uri, IReadOnlyCollection<string> allowedHosts, Action<HttpRequestMessage>? configure,
        long maxBytes, CancellationToken ct)
    {
        Uri current = uri;
        for (int hop = 0; hop <= _limits.MaxRedirects; hop++)
        {
            if (current.Scheme != Uri.UriSchemeHttps && !(_allowLoopbackHttp && current.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(current.Host)))
                throw new ConnectorException("insecure_address", "Only https:// addresses are used by connectors.");
            if (!allowedHosts.Contains(current.Host, StringComparer.OrdinalIgnoreCase))
                throw new ConnectorException("blocked_host", "The source pointed at a different site than the one you chose, so it was not followed.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(ConnectorLimits.UserAgent);
            configure?.Invoke(request);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_limits.RequestTimeout);
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    Uri? target = response.Headers.Location is { } l ? (l.IsAbsoluteUri ? l : new Uri(current, l)) : null;
                    if (target == null) throw new ConnectorException("redirect_without_target", "The server redirected without saying where.");
                    current = target;
                    continue;
                }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in response.Headers) headers[h.Key] = string.Join(",", h.Value);
                foreach (var h in response.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
                if (!response.IsSuccessStatusCode)
                    return new ConnectorResponse(status, null, false, null, headers);
                if (response.Content.Headers.ContentLength > maxBytes)
                    return new ConnectorResponse(status, null, true, response.Content.Headers.ContentType?.MediaType, headers);
                await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxBytes)
                        return new ConnectorResponse(status, null, true, response.Content.Headers.ContentType?.MediaType, headers);
                    buffer.Write(chunk, 0, read);
                }
                return new ConnectorResponse(status, buffer.ToArray(), false, response.Content.Headers.ContentType?.MediaType, headers);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ConnectorException("timeout", "The site did not answer in time.");
            }
            catch (HttpRequestException)
            {
                throw new ConnectorException("unreachable", "The site could not be reached (or its address is not a public one).");
            }
        }
        throw new ConnectorException("too_many_redirects", "The server redirected too many times.");
    }

    /// <summary>Maps a non-success status to an honest, specific failure. Never includes request headers or tokens.</summary>
    public static void ThrowForStatus(ConnectorResponse r, string what, bool hadToken)
    {
        if (r.Status is >= 200 and < 300) return;
        bool limited = r.Status == 429
            || (r.Status == 403 && (r.Headers.TryGetValue("x-ratelimit-remaining", out string? remaining) && remaining == "0" || r.Headers.ContainsKey("retry-after")));
        if (limited)
        {
            string when = r.Headers.TryGetValue("retry-after", out string? after) ? " Try again in about " + after + " seconds." : " Try again later.";
            throw new ConnectorException("rate_limited", what + " is rate limiting requests." + (hadToken ? string.Empty : " Adding an access token raises the limit.") + when);
        }
        if (r.Status is 401 or 403)
            throw new ConnectorException("auth_required", what + " needs a valid access token for this content (it refused access, status " + r.Status + ").");
        if (r.Status == 404)
            throw new ConnectorException("not_found", what + " was not found. Check the address (private content needs an access token).");
        if (r.Status >= 500)
            throw new ConnectorException("server_error", what + " had a server error (status " + r.Status + "). Try again later.");
        throw new ConnectorException("http_" + r.Status, what + " answered with status " + r.Status + ".");
    }
}

/// <summary>Cleans imported text: no control characters, bounded length. The text stays data, never instructions.</summary>
internal static class ConnectorText
{
    public static string Sanitize(string text, int maxChars)
    {
        var sb = new StringBuilder(Math.Min(text.Length, maxChars));
        foreach (char c in text)
        {
            if (sb.Length >= maxChars) break;
            if (c == '\0') continue;
            if (char.IsControl(c) && c is not ('\n' or '\t')) { if (c == '\r') continue; continue; }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    public static string Decode(byte[] bytes)
    {
        int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    public static bool LooksBinary(byte[] bytes)
    {
        int n = Math.Min(bytes.Length, 4096);
        for (int i = 0; i < n; i++) if (bytes[i] == 0) return true;
        return false;
    }
}

// ============================================================================================ GitHub

public sealed record GitHubTarget(string Owner, string Repo, string? Ref, string? Path);

public static class GitHubTargetParser
{
    private static readonly Regex OwnerRx = new("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$", RegexOptions.Compiled);
    private static readonly Regex RepoRx = new(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex RefRx = new(@"^[A-Za-z0-9._/-]{1,200}$", RegexOptions.Compiled);

    /// <summary>Accepts <c>https://github.com/owner/repo[/tree/ref[/path]]</c> or <c>owner/repo</c>. Nothing else (no http, no other host, no user info).</summary>
    public static bool TryParse(string? input, string? explicitRef, string? explicitPath, out GitHubTarget target, out string error)
    {
        target = null!;
        error = "Enter a GitHub repository address such as https://github.com/owner/repo.";
        if (string.IsNullOrWhiteSpace(input) || input.Length > 512) return false;
        string text = input.Trim();
        string? refName = null, path = null;
        string owner, repo;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) { error = "Only https:// GitHub addresses can be used."; return false; }
            if (!string.IsNullOrEmpty(uri.UserInfo)) { error = "The address cannot contain a user name or password."; return false; }
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            { error = "Only github.com repositories are supported."; return false; }
            string[] seg = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            if (seg.Length < 2) return false;
            owner = seg[0]; repo = seg[1];
            if (seg.Length >= 4 && seg[2] is "tree" or "blob") { refName = seg[3]; if (seg.Length > 4) path = string.Join('/', seg.Skip(4)); }
        }
        else
        {
            string[] seg = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length != 2) return false;
            owner = seg[0]; repo = seg[1];
        }
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        if (!OwnerRx.IsMatch(owner) || !RepoRx.IsMatch(repo) || repo is "." or "..") return false;
        if (!string.IsNullOrWhiteSpace(explicitRef)) refName = explicitRef.Trim();
        if (!string.IsNullOrWhiteSpace(explicitPath)) path = explicitPath.Trim();
        if (refName != null && (!RefRx.IsMatch(refName) || refName.Contains("..", StringComparison.Ordinal) || refName.StartsWith('/') || refName.EndsWith('/')))
        { error = "That branch or tag name is not valid."; return false; }
        if (path != null)
        {
            path = path.Replace('\\', '/').Trim('/');
            if (!IsSafeRelativePath(path) && path.Length > 0) { error = "The folder path inside the repository is not valid."; return false; }
        }
        target = new GitHubTarget(owner, repo, refName, string.IsNullOrEmpty(path) ? null : path);
        return true;
    }

    /// <summary>A repository path from the tree API or the user: relative, no "..", no drive or control characters.</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 400 || path.StartsWith('/') || path.Contains('\\') || path.Contains(':')) return false;
        foreach (char c in path) if (char.IsControl(c)) return false;
        foreach (string part in path.Split('/')) if (part.Length == 0 || part is "." or "..") return false;
        return true;
    }
}

internal sealed class GitHubConnector
{
    private static readonly string[] Hosts = { "api.github.com", "raw.githubusercontent.com" };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".rst", ".adoc", ".py", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".cs", ".java", ".kt", ".go", ".rs",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".rb", ".php", ".swift", ".sh", ".ps1", ".sql", ".json", ".yaml", ".yml", ".toml", ".ini",
        ".cfg", ".xml", ".html", ".css", ".scss", ".gradle", ".csproj", ".sln", ".bat"
    };
    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt", ".rst", ".adoc" };
    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "dist", "build", "bin", "obj", "vendor", "__pycache__", ".venv", "venv", "target", ".next", "coverage", ".idea", ".vs"
    };

    public async Task<ConnectorResult> FetchAsync(ConnectorRequest req, ConnectorHttp http, ConnectorLimits limits, CancellationToken ct)
    {
        if (!GitHubTargetParser.TryParse(req.Target, req.Ref, req.PathPrefix, out GitHubTarget target, out string error))
            throw new ConnectorException("invalid_target", error);
        bool hadToken = !string.IsNullOrEmpty(req.Token);
        Action<HttpRequestMessage> auth = r =>
        {
            r.Headers.Accept.ParseAdd("application/vnd.github+json");
            r.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (hadToken) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.Token);
        };
        var result = new ConnectorResult();
        string baseApi = "https://api.github.com/repos/" + Uri.EscapeDataString(target.Owner) + "/" + Uri.EscapeDataString(target.Repo);

        string refName = target.Ref ?? string.Empty;
        if (refName.Length == 0)
        {
            ConnectorResponse repoInfo = await http.GetAsync(new Uri(baseApi), Hosts, auth, 512_000, ct).ConfigureAwait(false);
            ConnectorHttp.ThrowForStatus(repoInfo, "GitHub repository " + target.Owner + "/" + target.Repo, hadToken);
            using JsonDocument repoDoc = JsonDocument.Parse(repoInfo.Body!);
            refName = repoDoc.RootElement.TryGetProperty("default_branch", out JsonElement db) && db.GetString() is { Length: > 0 } b ? b : "HEAD";
        }

        ConnectorResponse treeResp = await http.GetAsync(new Uri(baseApi + "/git/trees/" + Uri.EscapeDataString(refName) + "?recursive=1"),
            Hosts, auth, 8_000_000, ct).ConfigureAwait(false);
        ConnectorHttp.ThrowForStatus(treeResp, "GitHub repository " + target.Owner + "/" + target.Repo, hadToken);
        if (treeResp.TooLarge) throw new ConnectorException("too_large", "The repository file list is too large to import in one go. Pick a sub-folder.");

        var candidates = new List<(string Path, long Size)>();
        using (JsonDocument tree = JsonDocument.Parse(treeResp.Body!))
        {
            if (tree.RootElement.TryGetProperty("truncated", out JsonElement trunc) && trunc.ValueKind == JsonValueKind.True)
            { result.Notes.Add("tree_truncated"); result.StoppedBecause = "limit"; }
            if (tree.RootElement.TryGetProperty("tree", out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in entries.EnumerateArray())
                {
                    if (e.TryGetProperty("type", out JsonElement t) && t.GetString() != "blob") continue;
                    string? path = e.TryGetProperty("path", out JsonElement p) ? p.GetString() : null;
                    long size = e.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long sv) ? sv : 0;
                    if (path == null || !GitHubTargetParser.IsSafeRelativePath(path)) { result.SkippedFiles++; continue; }
                    if (target.Path != null && !(path.Equals(target.Path, StringComparison.Ordinal) || path.StartsWith(target.Path + "/", StringComparison.Ordinal))) continue;
                    if (!IsWanted(path, size, limits)) { result.SkippedFiles++; continue; }
                    candidates.Add((path, size));
                }
            }
        }

        int cap = Math.Clamp(req.MaxItems ?? limits.MaxDocs, 1, Math.Min(limits.MaxDocs, ConnectorLimits.HardMaxDocs));
        var ordered = candidates
            .OrderBy(c => DocExtensions.Contains(System.IO.Path.GetExtension(c.Path)) ? 0 : 1)
            .ThenBy(c => c.Path.Count(ch => ch == '/'))
            .ThenBy(c => c.Path, StringComparer.Ordinal).ToList();
        if (ordered.Count > cap) { result.StoppedBecause = "limit"; result.Notes.Add("file_cap:" + cap + " of " + ordered.Count); }

        long total = 0;
        foreach ((string path, long size) in ordered.Take(cap))
        {
            ct.ThrowIfCancellationRequested();
            if (total + size > limits.MaxTotalBytes) { result.StoppedBecause = "limit"; result.Notes.Add("byte_cap"); break; }
            string escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            var rawUri = new Uri("https://raw.githubusercontent.com/" + Uri.EscapeDataString(target.Owner) + "/" + Uri.EscapeDataString(target.Repo)
                + "/" + string.Join('/', refName.Split('/').Select(Uri.EscapeDataString)) + "/" + escaped);
            ConnectorResponse file = await http.GetAsync(rawUri, Hosts, r => { if (hadToken) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.Token); },
                limits.MaxBytesPerDoc, ct).ConfigureAwait(false);
            if (file.Status == 404 || file.TooLarge) { result.SkippedFiles++; continue; }
            ConnectorHttp.ThrowForStatus(file, "GitHub", hadToken);
            if (file.Body == null || ConnectorText.LooksBinary(file.Body)) { result.SkippedFiles++; continue; }
            total += file.Body.Length;
            string text = ConnectorText.Sanitize(ConnectorText.Decode(file.Body), limits.MaxCharsPerDoc);
            if (text.Length == 0) { result.SkippedFiles++; continue; }
            string ext = System.IO.Path.GetExtension(path);
            string body = DocExtensions.Contains(ext) ? text : "```" + ext.TrimStart('.') + "\n" + text.Replace("```", "'''") + "\n```";
            string url = "https://github.com/" + Uri.EscapeDataString(target.Owner) + "/" + Uri.EscapeDataString(target.Repo) + "/blob/"
                + string.Join('/', refName.Split('/').Select(Uri.EscapeDataString)) + "/" + escaped;
            result.Docs.Add(new ConnectorDoc(url, target.Owner + "/" + target.Repo + ": " + path, body));
        }
        if (result.Docs.Count == 0) throw new ConnectorException("nothing_to_import", "No readable text files were found in that repository or folder.");
        return result;
    }

    private static bool IsWanted(string path, long size, ConnectorLimits limits)
    {
        if (size > limits.MaxBytesPerDoc) return false;
        string[] parts = path.Split('/');
        if (parts.Take(parts.Length - 1).Any(p => SkippedDirs.Contains(p))) return false;
        string name = parts[^1];
        string lower = name.ToLowerInvariant();
        if (lower.EndsWith(".min.js") || lower.EndsWith(".map") || lower.Contains("lock.") || lower.EndsWith(".lock") || lower == "package-lock.json") return false;
        string ext = System.IO.Path.GetExtension(name);
        if (ext.Length == 0) return lower is "readme" or "dockerfile" or "makefile";
        return TextExtensions.Contains(ext);
    }
}

// ============================================================================================ YouTube

public static class YouTubeTargetParser
{
    private static readonly Regex IdRx = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    public static bool TryParseVideoId(string? input, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 512) return false;
        string text = input.Trim();
        if (IdRx.IsMatch(text)) { videoId = text; return true; }
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        string host = uri.Host.ToLowerInvariant();
        string? candidate = null;
        if (host is "youtu.be") candidate = uri.AbsolutePath.Trim('/').Split('/')[0];
        else if (host is "www.youtube.com" or "youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            string[] seg = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length >= 2 && seg[0] is "shorts" or "embed" or "live" or "v") candidate = seg[1];
            else if (seg.Length >= 1 && seg[0] == "watch")
            {
                foreach (string pair in uri.Query.TrimStart('?').Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0 && pair[..eq] == "v") { candidate = Uri.UnescapeDataString(pair[(eq + 1)..]); break; }
                }
            }
        }
        if (candidate == null || !IdRx.IsMatch(candidate)) return false;
        videoId = candidate;
        return true;
    }
}

internal sealed class YouTubeConnector
{
    private static readonly string[] Hosts = { "www.youtube.com", "youtube.com" };

    public async Task<ConnectorResult> FetchAsync(ConnectorRequest req, ConnectorHttp http, ConnectorLimits limits, CancellationToken ct)
    {
        if (!YouTubeTargetParser.TryParseVideoId(req.Target, out string id))
            throw new ConnectorException("invalid_target", "Enter a YouTube video address (https://www.youtube.com/watch?v=...) or its 11-character id.");
        string lang = string.IsNullOrWhiteSpace(req.Language) ? "en" : new string(req.Language.Where(c => char.IsLetter(c) || c == '-').Take(12).ToArray());
        var pageUri = new Uri("https://www.youtube.com/watch?v=" + id + "&hl=" + lang);
        ConnectorResponse page = await http.GetAsync(pageUri, Hosts, r => r.Headers.AcceptLanguage.ParseAdd(lang), 4_000_000, ct).ConfigureAwait(false);
        ConnectorHttp.ThrowForStatus(page, "YouTube", false);
        if (page.Body == null) throw new ConnectorException("too_large", "The video page was larger than allowed.");
        string html = ConnectorText.Decode(page.Body);
        string? json = ExtractPlayerResponse(html);
        if (json == null)
            throw new ConnectorException("no_captions", "YouTube did not return caption information for this video (it may be private, age-restricted, or YouTube showed a consent page).");

        string title = id, author = string.Empty;
        string? trackUrl = null;
        string trackLang = lang;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("videoDetails", out JsonElement vd))
            {
                if (vd.TryGetProperty("title", out JsonElement t) && t.GetString() is { Length: > 0 } ts) title = ts;
                if (vd.TryGetProperty("author", out JsonElement a) && a.GetString() is { } au) author = au;
            }
            if (root.TryGetProperty("captions", out JsonElement caps)
                && caps.TryGetProperty("playerCaptionsTracklistRenderer", out JsonElement renderer)
                && renderer.TryGetProperty("captionTracks", out JsonElement tracks) && tracks.ValueKind == JsonValueKind.Array)
            {
                var list = tracks.EnumerateArray().Select(tr => new
                {
                    Url = tr.TryGetProperty("baseUrl", out JsonElement u) ? u.GetString() : null,
                    Lang = tr.TryGetProperty("languageCode", out JsonElement l) ? l.GetString() ?? "" : "",
                    Auto = tr.TryGetProperty("kind", out JsonElement k) && k.GetString() == "asr"
                }).Where(x => !string.IsNullOrEmpty(x.Url)).ToList();
                var chosen = list.FirstOrDefault(x => x.Lang.Equals(lang, StringComparison.OrdinalIgnoreCase) && !x.Auto)
                    ?? list.FirstOrDefault(x => x.Lang.Equals(lang, StringComparison.OrdinalIgnoreCase))
                    ?? list.FirstOrDefault(x => !x.Auto) ?? list.FirstOrDefault();
                if (chosen != null) { trackUrl = chosen.Url; trackLang = chosen.Lang; }
            }
        }
        catch (JsonException) { throw new ConnectorException("no_captions", "YouTube returned data that could not be read."); }

        if (trackUrl == null)
            throw new ConnectorException("no_captions", "This video has no captions (subtitles) to import.");
        if (!Uri.TryCreate(trackUrl, UriKind.Absolute, out Uri? trackUri) || trackUri.Scheme != Uri.UriSchemeHttps
            || !Hosts.Contains(trackUri.Host, StringComparer.OrdinalIgnoreCase))
            throw new ConnectorException("blocked_host", "The caption address pointed outside YouTube, so it was not fetched.");

        var json3 = new UriBuilder(trackUri) { Query = Regex.Replace(trackUri.Query.TrimStart('?'), @"(^|&)fmt=[^&]*", string.Empty).Trim('&') + "&fmt=json3" };
        ConnectorResponse cap = await http.GetAsync(json3.Uri, Hosts, null, 3_000_000, ct).ConfigureAwait(false);
        ConnectorHttp.ThrowForStatus(cap, "YouTube captions", false);
        if (cap.Body == null || cap.Body.Length == 0) throw new ConnectorException("no_captions", "The caption track was empty.");
        string transcript = ParseTranscript(ConnectorText.Decode(cap.Body));
        transcript = ConnectorText.Sanitize(transcript, limits.MaxCharsPerDoc);
        if (transcript.Length == 0) throw new ConnectorException("no_captions", "The caption track had no text.");

        var result = new ConnectorResult();
        string header = string.IsNullOrEmpty(author) ? string.Empty : "Channel: " + author + "\n";
        result.Docs.Add(new ConnectorDoc("https://www.youtube.com/watch?v=" + id, "YouTube: " + title,
            header + "Captions language: " + trackLang + "\n\n" + transcript));
        return result;
    }

    /// <summary>Finds the JSON object assigned to ytInitialPlayerResponse by brace matching (string aware), with a hard scan limit.</summary>
    internal static string? ExtractPlayerResponse(string html)
    {
        int marker = html.IndexOf("ytInitialPlayerResponse", StringComparison.Ordinal);
        while (marker >= 0)
        {
            int start = html.IndexOf('{', marker);
            if (start < 0 || start - marker > 64) { marker = html.IndexOf("ytInitialPlayerResponse", marker + 10, StringComparison.Ordinal); continue; }
            int depth = 0; bool inString = false, escape = false;
            int limit = Math.Min(html.Length, start + 3_000_000);
            for (int i = start; i < limit; i++)
            {
                char c = html[i];
                if (inString) { if (escape) escape = false; else if (c == '\\') escape = true; else if (c == '"') inString = false; continue; }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return html.Substring(start, i - start + 1);
            }
            return null;
        }
        return null;
    }

    /// <summary>Parses json3 caption events (or, failing that, the older XML form) into "[mm:ss] text" lines.</summary>
    internal static string ParseTranscript(string body)
    {
        var sb = new StringBuilder();
        long lastStamp = -60_000;
        void Add(long startMs, string text)
        {
            text = Regex.Replace(WebUtility.HtmlDecode(text).Replace('\n', ' '), @"\s+", " ").Trim();
            if (text.Length == 0) return;
            if (startMs - lastStamp >= 30_000 || sb.Length == 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                var ts = TimeSpan.FromMilliseconds(startMs);
                sb.Append('[').Append(((int)ts.TotalMinutes).ToString("00")).Append(':').Append(ts.Seconds.ToString("00")).Append("] ");
                lastStamp = startMs;
            }
            else sb.Append(' ');
            sb.Append(text);
        }
        string trimmed = body.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("events", out JsonElement events))
                    foreach (JsonElement ev in events.EnumerateArray())
                    {
                        if (!ev.TryGetProperty("segs", out JsonElement segs)) continue;
                        long start = ev.TryGetProperty("tStartMs", out JsonElement ts) && ts.TryGetInt64(out long v) ? v : 0;
                        string text = string.Concat(segs.EnumerateArray().Select(s => s.TryGetProperty("utf8", out JsonElement u) ? u.GetString() : null));
                        Add(start, text);
                    }
            }
            catch (JsonException) { }
        }
        else
        {
            foreach (Match m in Regex.Matches(body, @"<text\b[^>]*?start=""([0-9.]+)""[^>]*>(.*?)</text>", RegexOptions.Singleline, TimeSpan.FromSeconds(3)))
                if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec))
                    Add((long)(sec * 1000), Regex.Replace(m.Groups[2].Value, "<[^>]+>", string.Empty));
        }
        return sb.ToString();
    }
}

// ============================================================================================ Confluence

public sealed record ConfluenceTarget(Uri Origin, string ContextPath, string PageId);

public static class ConfluenceTargetParser
{
    /// <summary>Parses a Confluence page address (Cloud <c>/wiki/spaces/KEY/pages/123/Title</c> or Data Center <c>/pages/viewpage.action?pageId=123</c>).
    /// https only; the address rule for public hosts is applied by the caller.</summary>
    public static bool TryParse(string? input, out ConfluenceTarget target, out string error)
    {
        target = null!;
        error = "Enter the address of a Confluence page, for example https://yourteam.atlassian.net/wiki/spaces/KEY/pages/12345/Title.";
        if (string.IsNullOrWhiteSpace(input) || input.Length > 2048) return false;
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) { error = "Only https:// Confluence addresses can be used."; return false; }
        if (!string.IsNullOrEmpty(uri.UserInfo)) { error = "The address cannot contain a user name or password."; return false; }
        string[] seg = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? pageId = null;
        int idx = Array.IndexOf(seg, "pages");
        if (idx >= 0 && idx + 1 < seg.Length && seg[idx + 1].All(char.IsAsciiDigit) && seg[idx + 1].Length > 0) pageId = seg[idx + 1];
        if (pageId == null)
            foreach (string pair in uri.Query.TrimStart('?').Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq].Equals("pageId", StringComparison.OrdinalIgnoreCase) && pair[(eq + 1)..].All(char.IsAsciiDigit) && pair.Length > eq + 1)
                { pageId = pair[(eq + 1)..]; break; }
            }
        if (pageId == null || pageId.Length > 20) return false;
        int stop = seg.Length;
        foreach (string marker in new[] { "spaces", "pages", "display" })
        {
            int i = Array.IndexOf(seg, marker);
            if (i >= 0 && i < stop) stop = i;
        }
        string context = stop > 0 ? "/" + string.Join('/', seg.Take(stop)) : string.Empty;
        foreach (string part in seg.Take(stop))
            if (part.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) || part == "..") return false;
        target = new ConfluenceTarget(new Uri(uri.GetLeftPart(UriPartial.Authority)), context, pageId);
        return true;
    }
}

internal sealed class ConfluenceConnector
{
    public async Task<ConnectorResult> FetchAsync(ConnectorRequest req, ConnectorHttp http, ConnectorLimits limits, CancellationToken ct)
    {
        if (!ConfluenceTargetParser.TryParse(req.Target, out ConfluenceTarget target, out string error))
            throw new ConnectorException("invalid_target", error);
        bool hadToken = !string.IsNullOrEmpty(req.Token);
        string[] hosts = { target.Origin.Host };
        Action<HttpRequestMessage> auth = r =>
        {
            r.Headers.Accept.ParseAdd("application/json");
            if (!hadToken) return;
            r.Headers.Authorization = string.IsNullOrWhiteSpace(req.Email)
                ? new AuthenticationHeaderValue("Bearer", req.Token)
                : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(req.Email.Trim() + ":" + req.Token)));
        };
        string api = target.Origin.GetLeftPart(UriPartial.Authority) + target.ContextPath + "/rest/api/content/";
        var result = new ConnectorResult();

        await AddPageAsync(target.PageId).ConfigureAwait(false);
        if (req.IncludeChildren)
        {
            int cap = Math.Clamp(req.MaxItems ?? limits.MaxChildPages, 1, limits.MaxChildPages);
            ConnectorResponse kids = await http.GetAsync(new Uri(api + target.PageId + "/child/page?limit=" + cap), hosts, auth, 2_000_000, ct).ConfigureAwait(false);
            ConnectorHttp.ThrowForStatus(kids, "Confluence child pages", hadToken);
            using JsonDocument kd = JsonDocument.Parse(kids.Body!);
            if (kd.RootElement.TryGetProperty("results", out JsonElement results))
            {
                var ids = results.EnumerateArray().Select(r => r.TryGetProperty("id", out JsonElement i) ? i.GetString() : null)
                    .Where(i => !string.IsNullOrEmpty(i) && i!.All(char.IsAsciiDigit)).Take(cap).ToList();
                if (results.GetArrayLength() > cap) { result.StoppedBecause = "limit"; result.Notes.Add("child_cap:" + cap); }
                foreach (string? childId in ids) { ct.ThrowIfCancellationRequested(); await AddPageAsync(childId!).ConfigureAwait(false); }
            }
        }
        return result;

        async Task AddPageAsync(string pageId)
        {
            ConnectorResponse page = await http.GetAsync(new Uri(api + pageId + "?expand=body.storage,space"), hosts, auth, limits.MaxBytesPerDoc * 4, ct).ConfigureAwait(false);
            ConnectorHttp.ThrowForStatus(page, "Confluence page", hadToken);
            if (page.Body == null) { result.SkippedFiles++; return; }
            using JsonDocument doc = JsonDocument.Parse(page.Body);
            JsonElement root = doc.RootElement;
            string title = root.TryGetProperty("title", out JsonElement t) && t.GetString() is { Length: > 0 } ts ? ts : "Confluence page " + pageId;
            string html = root.TryGetProperty("body", out JsonElement b) && b.TryGetProperty("storage", out JsonElement s) && s.TryGetProperty("value", out JsonElement v) ? v.GetString() ?? string.Empty : string.Empty;
            WebPageExtractor.Extracted extracted = WebPageExtractor.Extract("<html><body>" + html + "</body></html>", target.Origin);
            string text = ConnectorText.Sanitize(extracted.Text, limits.MaxCharsPerDoc);
            if (text.Length == 0) { result.SkippedFiles++; return; }
            string space = root.TryGetProperty("space", out JsonElement sp) && sp.TryGetProperty("key", out JsonElement sk) ? sk.GetString() ?? string.Empty : string.Empty;
            string url = target.Origin.GetLeftPart(UriPartial.Authority) + target.ContextPath + "/pages/viewpage.action?pageId=" + pageId;
            if (target.ContextPath == "/wiki" && space.Length > 0 && space.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '~'))
                url = target.Origin.GetLeftPart(UriPartial.Authority) + "/wiki/spaces/" + space + "/pages/" + pageId;
            result.Docs.Add(new ConnectorDoc(url, title, text));
        }
    }
}

// ============================================================================================ Paperless-ngx

public sealed record PaperlessTarget(Uri Origin, string BasePath);

public static class PaperlessTargetParser
{
    private static readonly Regex SegmentRx = new("^[A-Za-z0-9._~-]{1,64}$", RegexOptions.Compiled);

    /// <summary>Parses the address of a self-hosted Paperless-ngx. https is required; plain http only for this PC (loopback).
    /// No credentials, query or fragment. Which machines may be reached is decided at connect time by the validated handler.</summary>
    public static bool TryParse(string? input, out PaperlessTarget target, out string error)
    {
        target = null!;
        error = "Enter the address of your Paperless-ngx, for example https://paperless.example.lan:8000.";
        if (string.IsNullOrWhiteSpace(input) || input.Length > 2048) return false;
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out Uri? uri)) return false;
        bool https = uri.Scheme == Uri.UriSchemeHttps;
        bool loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && ConnectorHttp.IsLoopbackHost(uri.Host);
        if (!https && !loopbackHttp)
        {
            error = uri.Scheme == Uri.UriSchemeHttp
                ? "Plain http:// is only accepted for this PC (localhost). Use https:// for another machine so the token is not sent in clear text."
                : "Only https:// addresses can be used.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo)) { error = "The address cannot contain a user name or password."; return false; }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) { error = "The address must not contain a query or fragment."; return false; }
        string[] seg = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length > 4 || seg.Any(p => !SegmentRx.IsMatch(p) || p is "." or "..")) return false;
        target = new PaperlessTarget(new Uri(uri.GetLeftPart(UriPartial.Authority)), seg.Length == 0 ? string.Empty : "/" + string.Join('/', seg));
        return true;
    }
}

/// <summary>Imports the OCR text of documents from a self-hosted Paperless-ngx through its read-only REST API (GET only, token header,
/// bounded pages). Each document carries the address of its Paperless page as citation.</summary>
internal sealed class PaperlessConnector
{
    public const int PageSize = 25;

    public async Task<ConnectorResult> FetchAsync(ConnectorRequest req, ConnectorHttp http, ConnectorLimits limits, CancellationToken ct)
    {
        if (!PaperlessTargetParser.TryParse(req.Target, out PaperlessTarget target, out string error))
            throw new ConnectorException("invalid_target", error);
        bool hadToken = !string.IsNullOrEmpty(req.Token);
        if (!hadToken) throw new ConnectorException("auth_required", "Paperless-ngx needs an API token. Create one in your Paperless profile and paste it here.");
        string[] hosts = { target.Origin.Host };
        Action<HttpRequestMessage> auth = r =>
        {
            r.Headers.Accept.ParseAdd("application/json");
            r.Headers.Authorization = new AuthenticationHeaderValue("Token", req.Token);
        };
        string root = target.Origin.GetLeftPart(UriPartial.Authority) + target.BasePath;
        int cap = Math.Clamp(req.MaxItems ?? limits.MaxDocs, 1, Math.Min(limits.MaxDocs, ConnectorLimits.HardMaxDocs));
        string? filter = string.IsNullOrWhiteSpace(req.Ref) ? null : Uri.EscapeDataString(req.Ref.Trim().Length > 200 ? req.Ref.Trim()[..200] : req.Ref.Trim());
        var result = new ConnectorResult();
        long totalBytes = 0;

        for (int page = 1; page <= 50; page++)
        {
            ct.ThrowIfCancellationRequested();
            string url = root + "/api/documents/?page=" + page + "&page_size=" + PageSize + "&ordering=-modified"
                + (filter != null ? "&title_content=" + filter : string.Empty);
            ConnectorResponse response = await http.GetAsync(new Uri(url), hosts, auth, limits.MaxBytesPerDoc * PageSize, ct).ConfigureAwait(false);
            if (response.TooLarge) { result.StoppedBecause = "limit"; result.Notes.Add("page_too_large"); break; }
            ConnectorHttp.ThrowForStatus(response, "Paperless-ngx", hadToken);
            if (response.Body == null) break;
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            if (!doc.RootElement.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
                throw new ConnectorException("unreadable_response", "The server answered, but not like a Paperless-ngx API.");
            foreach (JsonElement item in results.EnumerateArray())
            {
                if (result.Docs.Count >= cap) { result.StoppedBecause = "limit"; result.Notes.Add("document_cap:" + cap); break; }
                if (!item.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt64(out long id) || id <= 0) { result.SkippedFiles++; continue; }
                string title = item.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } ts
                    ? ConnectorText.Sanitize(ts, 200).Replace('\n', ' ') : "Paperless document " + id;
                string text = item.TryGetProperty("content", out JsonElement c) && c.ValueKind == JsonValueKind.String
                    ? ConnectorText.Sanitize(c.GetString() ?? string.Empty, limits.MaxCharsPerDoc) : string.Empty;
                if (text.Length == 0) { result.SkippedFiles++; continue; }
                totalBytes += Encoding.UTF8.GetByteCount(text);
                if (totalBytes > limits.MaxTotalBytes) { result.StoppedBecause = "limit"; result.Notes.Add("size_cap"); return result; }
                result.Docs.Add(new ConnectorDoc(root + "/documents/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/details", title, text));
            }
            if (result.StoppedBecause != "done") break;
            if (!(doc.RootElement.TryGetProperty("next", out JsonElement next) && next.ValueKind == JsonValueKind.String)) break;
        }
        return result;
    }
}

// ============================================================================================ orchestration

/// <summary>Runs one connector import. The HTTP client comes from a factory so production uses the public-address-only handler and tests a fake one.</summary>
public sealed class KnowledgeConnectors
{
    private readonly Func<HttpClient> _httpFactory;
    private readonly Func<HttpClient> _lanHttpFactory;
    private readonly ConnectorLimits _limits;

    public KnowledgeConnectors(Func<HttpClient>? httpFactory = null, ConnectorLimits? limits = null)
    {
        _httpFactory = httpFactory ?? DefaultClient;
        _lanHttpFactory = httpFactory ?? DefaultLanClient;
        _limits = limits ?? new ConnectorLimits();
    }

    internal static HttpClient DefaultClient() =>
        new(ValidatedRemoteConnector.CreateHandler(WebAddressRule.PublicOnly), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Self-hosted sources (Paperless-ngx): public addresses plus this PC and private LAN ranges, never link-local/metadata/CGNAT.</summary>
    internal static HttpClient DefaultLanClient() =>
        new(ValidatedRemoteConnector.CreateHandler(WebAddressRule.PublicOrPrivateLan), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Validates the target without any network traffic. Returns an English message on failure.</summary>
    public static bool TryValidate(string kind, string target, string? @ref, string? path, out string error)
    {
        error = string.Empty;
        switch (kind)
        {
            case ConnectorKind.GitHub: return GitHubTargetParser.TryParse(target, @ref, path, out _, out error);
            case ConnectorKind.YouTube:
                if (YouTubeTargetParser.TryParseVideoId(target, out _)) return true;
                error = "Enter a YouTube video address (https://www.youtube.com/watch?v=...) or its 11-character id.";
                return false;
            case ConnectorKind.Confluence:
            {
                if (!ConfluenceTargetParser.TryParse(target, out ConfluenceTarget c, out error)) return false;
                if (!WebUrl.TryValidate(c.Origin.ToString(), WebAddressRule.PublicOnly, out _, out error)) return false;
                return true;
            }
            case ConnectorKind.Paperless: return PaperlessTargetParser.TryParse(target, out _, out error);
            default: error = "Unknown connector."; return false;
        }
    }

    public async Task<ConnectorResult> RunAsync(ConnectorRequest request, CancellationToken ct)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(_limits.TotalTimeout);
        bool lan = request.Kind == ConnectorKind.Paperless;
        using HttpClient client = lan ? _lanHttpFactory() : _httpFactory();
        var http = new ConnectorHttp(client, _limits, allowLoopbackHttp: lan);
        try
        {
            return request.Kind switch
            {
                ConnectorKind.GitHub => await new GitHubConnector().FetchAsync(request, http, _limits, total.Token).ConfigureAwait(false),
                ConnectorKind.YouTube => await new YouTubeConnector().FetchAsync(request, http, _limits, total.Token).ConfigureAwait(false),
                ConnectorKind.Confluence => await new ConfluenceConnector().FetchAsync(request, http, _limits, total.Token).ConfigureAwait(false),
                ConnectorKind.Paperless => await new PaperlessConnector().FetchAsync(request, http, _limits, total.Token).ConfigureAwait(false),
                _ => throw new ConnectorException("invalid_target", "Unknown connector.")
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException("timeout", "The import took longer than the time limit and was stopped.");
        }
        catch (JsonException)
        {
            throw new ConnectorException("unreadable_response", "The site answered with data that could not be read.");
        }
    }
}

/// <summary>
/// Access tokens for connectors, encrypted at rest through <see cref="ISecretProtector"/> in their own file (never in the
/// source manifest, never in a backup of the manifests, never logged). Keyed by source id.
/// </summary>
public sealed class ConnectorSecretStore
{
    private const string Purpose = "PaRuX.KnowledgeConnector.v1";
    private readonly string _stateDirectory;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();

    private readonly string _fileName;

    public ConnectorSecretStore(string stateDirectory) : this(stateDirectory, SecretProtection.For(stateDirectory)) { }
    /// <summary>A separate store file (for example the web-search provider key) so two owners never rewrite the same file.</summary>
    public ConnectorSecretStore(string stateDirectory, string fileName) : this(stateDirectory, SecretProtection.For(stateDirectory), fileName) { }
    internal ConnectorSecretStore(string stateDirectory, ISecretProtector protector, string fileName = "knowledge-connector-secrets.json")
    { _stateDirectory = stateDirectory; _protector = protector; _fileName = fileName; }

    private string StorePath => Path.Combine(_stateDirectory, _fileName);

    public void Set(string sourceId, string? secret)
    {
        lock (_gate)
        {
            Dictionary<string, string> store = Load();
            if (string.IsNullOrWhiteSpace(secret)) store.Remove(sourceId);
            else store[sourceId] = _protector.Protect(Purpose + ":" + sourceId, secret.Trim());
            Save(store);
        }
    }

    public string? Get(string sourceId)
    {
        lock (_gate) return Load().TryGetValue(sourceId, out string? blob) ? _protector.Unprotect(Purpose + ":" + sourceId, blob) : null;
    }

    public bool Has(string sourceId) { lock (_gate) return Load().ContainsKey(sourceId); }
    public void Forget(string sourceId) => Set(sourceId, null);

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, string> store)
    {
        Directory.CreateDirectory(_stateDirectory);
        string temp = StorePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(store));
        File.Move(temp, StorePath, overwrite: true);
    }
}
