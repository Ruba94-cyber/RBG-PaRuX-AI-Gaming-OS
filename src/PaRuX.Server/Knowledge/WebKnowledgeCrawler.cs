using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Server.Knowledge;

/// <summary>Limits for one web fetch. Every value is a hard ceiling, not a hint.</summary>
public sealed record WebCrawlOptions
{
    public int MaxPages { get; init; } = 1;
    public int MaxDepth { get; init; }
    public long MaxBytesPerPage { get; init; } = 1_500_000;
    public long MaxTotalBytes { get; init; } = 20_000_000;
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Pause between requests to one site (also the floor for a robots.txt Crawl-delay).</summary>
    public TimeSpan PoliteDelay { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan MaxCrawlDelay { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxRedirects { get; init; } = 4;
    public string UserAgent { get; init; } = "PaRuXKnowledgeBot/1.0 (user-initiated; local)";

    public const int HardMaxPages = 100;
    public const int HardMaxDepth = 4;

    public static WebCrawlOptions SinglePage { get; } = new();

    public static WebCrawlOptions Crawl(int maxPages, int maxDepth) => new()
    {
        MaxPages = Math.Clamp(maxPages, 1, HardMaxPages),
        MaxDepth = Math.Clamp(maxDepth, 0, HardMaxDepth)
    };
}

public sealed class WebPageRecord
{
    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>SHA-256 of the extracted text; decides whether a page changed.</summary>
    public string ContentSha256 { get; set; } = string.Empty;
    /// <summary>SHA-256 of the file PaRuX wrote; a different hash on disk means the person edited the copy.</summary>
    public string WrittenSha256 { get; set; } = string.Empty;
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public DateTime FetchedUtc { get; set; }
    public List<string> Links { get; set; } = new();
}

public sealed class WebSourceManifest
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Mode { get; set; } = "page";
    public int MaxPages { get; set; } = 1;
    public int MaxDepth { get; set; }
    public string Folder { get; set; } = string.Empty;
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSyncUtc { get; set; }
    /// <summary>Empty for a website; otherwise a <see cref="ConnectorKind"/> (github, youtube, confluence). Tokens are never stored here.</summary>
    public string Connector { get; set; } = string.Empty;
    public string? ConnectorRef { get; set; }
    public string? ConnectorPath { get; set; }
    public string? ConnectorLanguage { get; set; }
    public string? ConnectorEmail { get; set; }
    public bool ConnectorChildren { get; set; }
    public Dictionary<string, WebPageRecord> Pages { get; set; } = new(StringComparer.Ordinal);
}

public sealed record WebPageResult(string Url, string Outcome, string? Reason, string? FileName);

public sealed class WebCrawlReport
{
    public List<WebPageResult> Pages { get; } = new();
    public int Written => Pages.Count(p => p.Outcome == "written");
    public int Unchanged => Pages.Count(p => p.Outcome == "unchanged");
    public int Skipped => Pages.Count(p => p.Outcome == "skipped");
    public int Failed => Pages.Count(p => p.Outcome == "failed");
    public int KeptEdited => Pages.Count(p => p.Outcome == "kept-edited");
    public int Removed { get; set; }
    public long Bytes { get; set; }
    /// <summary>done, page_limit, byte_limit, timeout, gaming or cancelled.</summary>
    public string StoppedBecause { get; set; } = "done";
}

/// <summary>robots.txt rules for one user agent (RFC 9309 matching: longest rule wins, Allow wins a tie).</summary>
public sealed class RobotsRules
{
    private readonly List<(bool Allow, string Pattern)> _rules = new();
    public bool DisallowAll { get; private init; }
    /// <summary>True when robots.txt could not be read (server error, timeout, network or redirect): everything is held back
    /// and nothing already saved may be judged missing.</summary>
    public bool Unavailable { get; private init; }
    internal static RobotsRules CouldNotRead() => new() { DisallowAll = true, Unavailable = true };
    public TimeSpan? CrawlDelay { get; private set; }

    public static RobotsRules AllowEverything { get; } = new();
    public static RobotsRules BlockEverything { get; } = new() { DisallowAll = true };

    public static RobotsRules Parse(string text, string agentToken)
    {
        var specific = new RobotsRules();
        var star = new RobotsRules();
        RobotsRules? current = null;
        bool inGroupHeader = false;
        var groupTargets = new List<RobotsRules>();
        bool sawSpecific = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string field = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();
            if (field == "user-agent")
            {
                if (!inGroupHeader) { groupTargets = new List<RobotsRules>(); inGroupHeader = true; }
                string agent = value.ToLowerInvariant();
                if (agent == "*") groupTargets.Add(star);
                else if (AgentMatches(agent, agentToken))
                {
                    groupTargets.Add(specific);
                    sawSpecific = true;
                }
                continue;
            }
            inGroupHeader = false;
            current = null;
            foreach (RobotsRules target in groupTargets)
            {
                if (field == "disallow") { if (value.Length > 0) target._rules.Add((false, value)); }
                else if (field == "allow") { if (value.Length > 0) target._rules.Add((true, value)); }
                else if (field == "crawl-delay" && double.TryParse(value, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds is >= 0 and <= 3600)
                    target.CrawlDelay = TimeSpan.FromSeconds(seconds);
            }
        }
        _ = current;
        return sawSpecific ? specific : star;
    }

    /// <summary>A group applies only when its product token IS ours (a "/version" suffix is ignored). A shorter or longer
    /// token, such as "bot" or "p", names a different crawler.</summary>
    internal static bool AgentMatches(string groupAgent, string ourToken)
    {
        int slash = groupAgent.IndexOf('/');
        string product = (slash >= 0 ? groupAgent[..slash] : groupAgent).Trim();
        return product.Length > 0 && string.Equals(product, ourToken, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsAllowed(string pathAndQuery)
    {
        if (DisallowAll) return false;
        int bestLength = -1;
        bool allowed = true;
        foreach ((bool allow, string pattern) in _rules)
        {
            if (!Matches(pattern, pathAndQuery)) continue;
            int length = pattern.Length;
            if (length > bestLength || (length == bestLength && allow)) { bestLength = length; allowed = allow; }
        }
        return allowed;
    }

    private static bool Matches(string pattern, string path)
    {
        bool anchored = pattern.EndsWith('$');
        if (anchored) pattern = pattern[..^1];
        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + (anchored ? "$" : string.Empty);
        try { return Regex.IsMatch(path, regex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}

/// <summary>Plain-text extraction from HTML. No script is run and nothing is fetched.</summary>
public static class WebPageExtractor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;
    private static readonly Regex Comments = new("<!--.*?-->", Opt, Timeout);
    private static readonly Regex Blocks = new(@"<(script|style|noscript|svg|template|nav|footer|aside|iframe|form|select|button)\b[^>]*>.*?</\1\s*>", Opt, Timeout);
    private static readonly Regex Title = new(@"<title\b[^>]*>(.*?)</title\s*>", Opt, Timeout);
    private static readonly Regex Heading = new(@"<h([1-6])\b[^>]*>(.*?)</h\1\s*>", Opt, Timeout);
    private static readonly Regex ListItem = new(@"<li\b[^>]*>", Opt, Timeout);
    private static readonly Regex BlockBreak = new(@"</?(p|div|br|tr|section|article|main|ul|ol|table|pre|blockquote|dd|dt|dl|figure|figcaption|header)\b[^>]*>", Opt, Timeout);
    private static readonly Regex AnyTag = new("<[^>]+>", Opt, Timeout);
    private static readonly Regex Spaces = new(@"[ \t ]+", Opt, Timeout);
    private static readonly Regex BlankLines = new(@"\n\s*\n\s*(\n\s*)+", Opt, Timeout);
    private static readonly Regex Anchor = new(@"<a\s[^>]*?href\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))[^>]*>", Opt, Timeout);
    private static readonly Regex Meta = new(@"<meta\s[^>]*>", Opt, Timeout);

    public sealed record Extracted(string Title, string Text, bool NoIndex, IReadOnlyList<string> Links);

    public static Extracted Extract(string html, Uri baseUri)
    {
        bool noIndex = false;
        foreach (Match meta in Meta.Matches(html))
        {
            string tag = meta.Value;
            if (Regex.IsMatch(tag, @"name\s*=\s*[""']?robots", RegexOptions.IgnoreCase, Timeout)
                && Regex.IsMatch(tag, @"content\s*=\s*[""'][^""']*noindex", RegexOptions.IgnoreCase, Timeout))
                noIndex = true;
        }

        var links = new List<string>();
        foreach (Match a in Anchor.Matches(html))
        {
            string tag = a.Value;
            if (Regex.IsMatch(tag, @"rel\s*=\s*[""'][^""']*nofollow", RegexOptions.IgnoreCase, Timeout)) continue;
            string href = WebUtility.HtmlDecode(a.Groups[1].Success ? a.Groups[1].Value : a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Value).Trim();
            if (href.Length == 0 || href.StartsWith('#')) continue;
            if (Uri.TryCreate(baseUri, href, out Uri? absolute) && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
                links.Add(WebUrl.Normalize(absolute));
        }

        string title = string.Empty;
        Match titleMatch = Title.Match(html);
        if (titleMatch.Success) title = Clean(WebUtility.HtmlDecode(AnyTag.Replace(titleMatch.Groups[1].Value, " ")));

        string body = Comments.Replace(html, " ");
        int headStart = body.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        int headEnd = body.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headStart >= 0 && headEnd > headStart) body = body.Remove(headStart, headEnd + 7 - headStart);
        body = Blocks.Replace(body, " ");
        body = Heading.Replace(body, m => "\n\n" + new string('#', int.Parse(m.Groups[1].Value)) + " " + Clean(AnyTag.Replace(m.Groups[2].Value, " ")) + "\n\n");
        body = ListItem.Replace(body, "\n- ");
        body = BlockBreak.Replace(body, "\n");
        body = AnyTag.Replace(body, " ");
        body = WebUtility.HtmlDecode(body).Replace("\r", string.Empty);
        body = string.Join('\n', body.Split('\n').Select(l => Spaces.Replace(l, " ").Trim()));
        body = BlankLines.Replace(body, "\n\n").Trim();

        if (title.Length == 0)
        {
            Match h1 = Regex.Match(body, @"^# (.+)$", RegexOptions.Multiline, Timeout);
            title = h1.Success ? h1.Groups[1].Value.Trim() : string.Empty;
        }
        return new Extracted(title, body, noIndex, links.Distinct(StringComparer.Ordinal).Take(300).ToList());
    }

    private static string Clean(string value) => Spaces.Replace(value.Replace('\n', ' ').Replace('\r', ' '), " ").Trim();
}

public static class WebUrl
{
    public const int MaxLength = 2048;

    /// <summary>Drops the fragment and a default port; keeps path and query.</summary>
    public static string Normalize(Uri uri)
    {
        return uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    }

    /// <summary>Validates a user-entered address. Literal IPs are checked with the same rule as connections, so a private
    /// address is refused before any socket is opened; host names are checked again at connect time (DNS rebinding).</summary>
    public static bool TryValidate(string? input, Func<string, IPAddress, bool> addressAllowed, out Uri uri, out string error)
    {
        uri = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxLength)
        { error = "Enter a web address (http:// or https://), up to 2048 characters."; return false; }
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out Uri? parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        { error = "Only http:// and https:// addresses can be added."; return false; }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        { error = "The address cannot contain a user name or password."; return false; }
        if (string.IsNullOrEmpty(parsed.Host))
        { error = "The address has no host name."; return false; }
        if (IPAddress.TryParse(parsed.Host, out IPAddress? literal) && !addressAllowed(parsed.Host, literal))
        { error = "That address is on this PC or a private network. Only public websites can be added."; return false; }
        uri = parsed;
        return true;
    }
}

/// <summary>
/// Fetches one page or a bounded crawl of one site into plain-text files. It is only ever started by the person, honours
/// robots.txt, never leaves the starting host, checks every connection against the address rule (public addresses only
/// by default) and stops at hard page, byte and time limits.
/// </summary>
public sealed class WebKnowledgeCrawler
{
    private readonly WebCrawlOptions _options;
    private readonly Func<string, IPAddress, bool> _addressAllowed;
    private readonly ValidatedRemoteConnector.HostResolver? _resolver;
    private readonly Func<bool> _gamingActive;
    private readonly Func<bool> _offlineBlocked;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal WebKnowledgeCrawler(
        WebCrawlOptions options,
        Func<string, IPAddress, bool>? addressAllowed = null,
        ValidatedRemoteConnector.HostResolver? resolver = null,
        Func<bool>? gamingActive = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<bool>? offlineBlocked = null)
    {
        _offlineBlocked = offlineBlocked ?? (() => false);
        _options = options;
        _addressAllowed = addressAllowed ?? WebAddressRule.PublicOnly;
        _resolver = resolver;
        _gamingActive = gamingActive ?? GamingHardOffWatch.MarkerActive;
        _delay = delay ?? ((span, ct) => span <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(span, ct));
    }

    public Func<string, IPAddress, bool> AddressRule => _addressAllowed;

    public async Task<WebCrawlReport> CrawlAsync(Uri start, string folder, WebSourceManifest manifest, CancellationToken ct)
    {
        var report = new WebCrawlReport();
        Directory.CreateDirectory(folder);
        using var gaming = new GamingHardOffWatch(_gamingActive, ct, TimeSpan.FromMilliseconds(200));
        using var total = CancellationTokenSource.CreateLinkedTokenSource(gaming.Token);
        total.CancelAfter(_options.TotalTimeout);
        using var handler = ValidatedRemoteConnector.CreateHandler(_addressAllowed, _resolver);
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        var robotsByOrigin = new Dictionary<string, RobotsRules>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(Uri Url, int Depth)>();
        var reachedThisRun = new HashSet<string>(StringComparer.Ordinal);
        string startKey = WebUrl.Normalize(start);
        string prefix = ScopePrefix(start);
        queue.Enqueue((new Uri(startKey), 0));
        seen.Add(startKey);
        bool first = true;
        int processed = 0;
        bool robotsUnavailable = false;
        bool scoped = _options.MaxPages > 1 || _options.MaxDepth > 0;

        try
        {
            while (queue.Count > 0)
            {
                if (_offlineBlocked()) { report.StoppedBecause = "offline"; break; }
                if (processed >= _options.MaxPages) { report.StoppedBecause = "page_limit"; break; }
                if (report.Bytes >= _options.MaxTotalBytes) { report.StoppedBecause = "byte_limit"; break; }
                (Uri url, int depth) = queue.Dequeue();
                string key = WebUrl.Normalize(url);

                RobotsRules robots = await RobotsFor(http, url, robotsByOrigin, total.Token).ConfigureAwait(false);
                if (robots.Unavailable) robotsUnavailable = true;
                if (!robots.IsAllowed(url.PathAndQuery))
                {
                    report.Pages.Add(new WebPageResult(key, "skipped", robots.DisallowAll ? "robots_unavailable" : "blocked_by_robots", null));
                    continue;
                }

                if (!first)
                {
                    TimeSpan wait = _options.PoliteDelay;
                    if (robots.CrawlDelay is { } asked) wait = asked > _options.MaxCrawlDelay ? _options.MaxCrawlDelay : (asked > wait ? asked : wait);
                    await _delay(wait, total.Token).ConfigureAwait(false);
                }
                first = false;
                processed++;
                reachedThisRun.Add(key);

                PageFetch page = await FetchPageAsync(http, url, manifest.Pages.GetValueOrDefault(key), robots, scoped ? prefix : null, total.Token).ConfigureAwait(false);
                report.Bytes += page.Bytes;
                if (page.Failure != null)
                {
                    report.Pages.Add(new WebPageResult(key, page.IsSkip ? "skipped" : "failed", page.Failure, null));
                    continue;
                }

                IReadOnlyList<string> links;
                if (page.NotModified)
                {
                    WebPageRecord record = manifest.Pages[key];
                    report.Pages.Add(new WebPageResult(key, "unchanged", "not_modified", record.FileName));
                    links = record.Links;
                }
                else
                {
                    WebPageExtractor.Extracted extracted;
                    try { extracted = WebPageExtractor.Extract(page.Body!, page.FinalUrl!); }
                    catch (RegexMatchTimeoutException)
                    {
                        // A hostile or malformed page that defeats text extraction costs only that page.
                        report.Pages.Add(new WebPageResult(key, "failed", "extraction_timeout", null));
                        continue;
                    }
                    links = extracted.Links;
                    if (extracted.NoIndex) { report.Pages.Add(new WebPageResult(key, "skipped", "noindex", null)); }
                    else if (extracted.Text.Length < 20) { report.Pages.Add(new WebPageResult(key, "skipped", "no_text", null)); }
                    else report.Pages.Add(Store(folder, manifest, key, page, extracted));
                    if (manifest.Pages.TryGetValue(key, out WebPageRecord? stored)) stored.Links = links.Take(300).ToList();
                }

                if (depth < _options.MaxDepth)
                {
                    foreach (string link in links)
                    {
                        if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? next)) continue;
                        if (!string.Equals(next.Authority, start.Authority, StringComparison.OrdinalIgnoreCase) || next.Scheme != start.Scheme && !(start.Scheme == "http" && next.Scheme == "https")) continue;
                        if (!next.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        if (LooksBinary(next.AbsolutePath)) continue;
                        string nextKey = WebUrl.Normalize(next);
                        if (seen.Add(nextKey)) queue.Enqueue((new Uri(nextKey), depth + 1));
                    }
            }
        }

            // Only after a finished walk with every robots.txt readable are pages that were not reached looked at again; one
            // is forgotten only if the site itself now says 404 or 410.
            if (report.StoppedBecause == "done" && !robotsUnavailable && _options.MaxDepth > 0)
                report.Removed = await PruneGoneAsync(http, folder, manifest, reachedThisRun, robotsByOrigin, total.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (gaming.Triggered) report.StoppedBecause = "gaming";
            else if (ct.IsCancellationRequested) report.StoppedBecause = "cancelled";
            else report.StoppedBecause = "timeout";
        }

        manifest.LastSyncUtc = DateTime.UtcNow;
        return report;
    }

    private static string ScopePrefix(Uri start)
    {
        string path = start.AbsolutePath;
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..(slash + 1)];
    }

    private static bool LooksBinary(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".ico" or ".pdf" or ".zip" or ".gz" or ".7z" or ".exe"
            or ".msi" or ".mp3" or ".mp4" or ".mkv" or ".avi" or ".mov" or ".css" or ".js" or ".woff" or ".woff2" or ".ttf" or ".xml" or ".json";
    }

    private sealed record PageFetch(Uri? FinalUrl, string? Body, long Bytes, string? Failure, bool IsSkip, bool NotModified, string? ETag, string? LastModified);

    private async Task<RobotsRules> RobotsFor(HttpClient http, Uri url, Dictionary<string, RobotsRules> cache, CancellationToken ct)
    {
        string origin = url.GetLeftPart(UriPartial.Authority);
        if (cache.TryGetValue(origin, out RobotsRules? known)) return known;
        RobotsRules rules;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, origin + "/robots.txt");
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.RequestTimeout);
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                (string text, _) = await ReadBoundedAsync(response, 512 * 1024, timeout.Token).ConfigureAwait(false);
                rules = RobotsRules.Parse(text ?? string.Empty, "paruxknowledgebot");
            }
            else if (status is >= 400 and < 500) rules = RobotsRules.AllowEverything;   // no robots file: nothing is restricted
            else rules = RobotsRules.CouldNotRead();                                       // server error or redirect: assume disallowed
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { rules = RobotsRules.CouldNotRead(); }
        catch (HttpRequestException) { rules = RobotsRules.CouldNotRead(); }
        cache[origin] = rules;
        return rules;
    }

    private async Task<PageFetch> FetchPageAsync(HttpClient http, Uri url, WebPageRecord? previous, RobotsRules robots, string? scopePrefix, CancellationToken ct)
    {
        Uri current = url;
        for (int hop = 0; hop <= _options.MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            request.Headers.Accept.ParseAdd("text/html, text/plain;q=0.9, text/markdown;q=0.9");
            if (hop == 0 && previous != null)
            {
                if (!string.IsNullOrEmpty(previous.ETag) && EntityTagHeaderValue.TryParse(previous.ETag, out EntityTagHeaderValue? tag)) request.Headers.IfNoneMatch.Add(tag);
                if (!string.IsNullOrEmpty(previous.LastModified) && DateTimeOffset.TryParse(previous.LastModified, out DateTimeOffset since)) request.Headers.IfModifiedSince = since;
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_options.RequestTimeout);
                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    Uri? target = response.Headers.Location is { } l ? (l.IsAbsoluteUri ? l : new Uri(current, l)) : null;
                    if (target == null) return Fail("redirect_without_target");
                    if (!string.Equals(target.Authority, url.Authority, StringComparison.OrdinalIgnoreCase)) return Fail("redirected_to_another_site", skip: true);
                    if (target.Scheme == Uri.UriSchemeHttp && current.Scheme == Uri.UriSchemeHttps) return Fail("redirected_to_plain_http", skip: true);
                    // The target is a new page: robots.txt and the crawl scope apply to it exactly as to a link.
                    if (!robots.IsAllowed(target.PathAndQuery)) return Fail("redirect_blocked_by_robots", skip: true);
                    if (scopePrefix != null && !target.AbsolutePath.StartsWith(scopePrefix, StringComparison.Ordinal)) return Fail("redirected_outside_scope", skip: true);
                    current = target;
                    continue;
                }
                if (status == 304 && previous != null) return new PageFetch(current, null, 0, null, false, true, previous.ETag, previous.LastModified);
                if (!response.IsSuccessStatusCode) return Fail("http_" + status);
                string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!(mediaType is "text/html" or "application/xhtml+xml" or "text/plain" or "text/markdown"))
                    return Fail("unsupported_content_type", skip: true);
                if (response.Content.Headers.ContentLength > _options.MaxBytesPerPage) return Fail("too_large", skip: true);
                (string? body, long bytes) = await ReadBoundedAsync(response, _options.MaxBytesPerPage, timeout.Token).ConfigureAwait(false);
                if (body == null) return new PageFetch(null, null, bytes, "too_large", true, false, null, null);
                if (mediaType is "text/plain" or "text/markdown")
                    body = "<html><body><pre>" + WebUtility.HtmlEncode(body) + "</pre></body></html>";
                return new PageFetch(current, body, bytes, null, false, false,
                    response.Headers.ETag?.ToString(), response.Content.Headers.LastModified?.ToString("R"));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Fail("timeout"); }
            catch (HttpRequestException) { return Fail("unreachable_or_blocked_address"); }
        }
        return Fail("too_many_redirects");

        static PageFetch Fail(string reason, bool skip = false) => new(null, null, 0, reason, skip, false, null, null);
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes; returns null text when the body is longer.</summary>
    private static async Task<(string? Text, long Bytes)> ReadBoundedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        long total = 0;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit) return (null, total);
            buffer.Write(chunk, 0, read);
        }
        Encoding encoding = Encoding.UTF8;
        string? charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrEmpty(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); } catch (ArgumentException) { }
        }
        return (encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), total);
    }

    private static WebPageResult Store(string folder, WebSourceManifest manifest, string key, PageFetch page, WebPageExtractor.Extracted extracted)
    {
        string contentHash = Sha(extracted.Text);
        manifest.Pages.TryGetValue(key, out WebPageRecord? record);
        string fileName = record != null && SafePath(folder, record.FileName) != null ? record.FileName : FileNameFor(key);
        if (record != null && fileName != record.FileName) record = null;   // a manifest entry that is not a plain file name is ignored
        string path = SafePath(folder, fileName)!;

        if (record != null && record.ContentSha256 == contentHash && File.Exists(path))
        {
            record.ETag = page.ETag; record.LastModified = page.LastModified; record.FetchedUtc = DateTime.UtcNow;
            return new WebPageResult(key, "unchanged", null, fileName);
        }

        // Never overwrite a copy the person edited: the file on disk no longer matches what PaRuX wrote.
        if (record != null && File.Exists(path) && Sha(File.ReadAllBytes(path)) != record.WrittenSha256)
        {
            record.ContentSha256 = contentHash;
            return new WebPageResult(key, "kept-edited", "your_edited_copy_was_kept", fileName);
        }
        if (record == null && File.Exists(path))
            return new WebPageResult(key, "kept-edited", "a_file_with_this_name_already_exists", fileName);

        string title = extracted.Title.Length > 0 ? extracted.Title : key;
        string markdown = "# " + title + "\n\nSource: " + key + "\nRetrieved: " + DateTime.UtcNow.ToString("yyyy-MM-dd") + "\n\n" + extracted.Text + "\n";
        byte[] bytes = new UTF8Encoding(false).GetBytes(markdown);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        manifest.Pages[key] = new WebPageRecord
        {
            Url = key,
            FileName = fileName,
            ContentSha256 = contentHash,
            WrittenSha256 = Sha(bytes),
            ETag = page.ETag,
            LastModified = page.LastModified,
            FetchedUtc = DateTime.UtcNow,
            Links = extracted.Links.Take(300).ToList()
        };
        return new WebPageResult(key, "written", null, fileName);
    }

    private async Task<int> PruneGoneAsync(HttpClient http, string folder, WebSourceManifest manifest, HashSet<string> reached,
        Dictionary<string, RobotsRules> robotsByOrigin, CancellationToken ct)
    {
        int removed = 0;
        foreach (string key in manifest.Pages.Keys.Where(k => !reached.Contains(k)).ToList())
        {
            WebPageRecord record = manifest.Pages[key];
            if (!Uri.TryCreate(key, UriKind.Absolute, out Uri? uri)) continue;
            RobotsRules robots = await RobotsFor(http, uri, robotsByOrigin, ct).ConfigureAwait(false);
            if (robots.Unavailable || !robots.IsAllowed(uri.PathAndQuery)) continue;
            PageFetch check = await FetchPageAsync(http, uri, null, robots, null, ct).ConfigureAwait(false);
            if (check.Failure is not ("http_404" or "http_410")) continue;   // still there, or unknown: keep it

            string? path = SafePath(folder, record.FileName);
            try
            {
                if (path != null && File.Exists(path) && Sha(File.ReadAllBytes(path)) == record.WrittenSha256) { File.Delete(path); removed++; }
                manifest.Pages.Remove(key);
            }
            catch (IOException) { }
        }
        return removed;
    }

    /// <summary>The file inside <paramref name="folder"/> for a manifest entry, or null when the entry is not a plain file name
    /// there (a hand-edited manifest must not point deletes or writes elsewhere).</summary>
    internal static string? SafePath(string folder, string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)) return null;
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName is "." or "..") return null;
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(folder, fileName));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    internal static string FileNameFor(string url)
    {
        var uri = new Uri(url);
        string stem = (uri.Host + uri.AbsolutePath).Trim('/');
        var builder = new StringBuilder();
        foreach (char c in stem) builder.Append(char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_');
        string slug = builder.ToString().Trim('_', '.');
        if (slug.Length > 80) slug = slug[..80];
        if (slug.Length == 0) slug = "page";
        return slug + "-" + Sha(url)[..8] + ".md";
    }

    private static string Sha(string text) => Sha(Encoding.UTF8.GetBytes(text));
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public static class WebAddressRule
{
    /// <summary>Production rule: only public internet addresses (no loopback, private, link-local, CGNAT or multicast).</summary>
    public static bool PublicOnly(string host, IPAddress address) => ExplicitApiInferenceTransport.IsPublicAddress(address);
}
