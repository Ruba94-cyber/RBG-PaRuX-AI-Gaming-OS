using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>The main readable content of a web page.</summary>
public sealed record ReadableArticle(string Title, string Text, bool UsedMainContentHeuristic);

/// <summary>
/// A Readability-style main-content extractor with no dependency: HTML is tokenised into a small bounded tree, boilerplate
/// (navigation, footers, sidebars, forms, ads, comments, scripts) is dropped, every paragraph scores its parent and grandparent
/// by text length and commas, link-heavy blocks are penalised, and the best-scoring block becomes the article. Scripts are never
/// run and nothing is fetched. When no block scores (a page with no paragraphs) the whole body text is returned and flagged.
/// The text is untrusted data; callers label it before it enters a prompt.
/// </summary>
public static class WebReadability
{
    public const int MaxInputChars = 600_000;
    private const int MaxNodes = 40_000;
    private const int MaxDepth = 120;

    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
    { "br", "hr", "img", "input", "meta", "link", "area", "base", "col", "embed", "source", "track", "wbr" };
    private static readonly HashSet<string> Drop = new(StringComparer.OrdinalIgnoreCase)
    { "script", "style", "noscript", "svg", "template", "nav", "footer", "aside", "iframe", "form", "select", "button", "head", "dialog", "canvas", "object" };
    private static readonly HashSet<string> Block = new(StringComparer.OrdinalIgnoreCase)
    { "p", "div", "section", "article", "main", "li", "ul", "ol", "pre", "blockquote", "table", "tr", "td", "th", "h1", "h2", "h3", "h4", "h5", "h6", "dd", "dt", "figcaption", "header", "br" };
    private static readonly Regex Unlikely = new(
        "(?:^|[\\s_-])(?:comment|disqus|sidebar|sponsor|advert|ads?|banner|cookie|consent|popup|modal|share|social|related|newsletter|subscribe|promo|breadcrumb|menu|navbar|footer|toolbar|widget)(?:$|[\\s_-])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex Likely = new(
        "(?:^|[\\s_-])(?:article|body|content|entry|main|page|post|story|text|blog|hentry)(?:$|[\\s_-])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex Tag = new(
        "<(/?)([a-zA-Z][a-zA-Z0-9]*)((?:\"[^\"]*\"|'[^']*'|[^'\">])*)>|<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromSeconds(3));
    private static readonly Regex Attr = new(
        "\\b(class|id)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex HiddenAttr = new("(?:^|\\s)hidden(?:\\s|=|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex Spaces =new("[ \\t\\u00a0]+", RegexOptions.Compiled);
    private static readonly Regex Blank = new("\\n{3,}", RegexOptions.Compiled);

    private sealed class Node
    {
        public string Name = string.Empty;
        public string Hint = string.Empty;
        public Node? Parent;
        public List<object> Children = new(); // Node or string
        public double Score;
        public int Depth;
    }

    public static ReadableArticle Extract(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return new ReadableArticle(string.Empty, string.Empty, false);
        if (html.Length > MaxInputChars) html = html[..MaxInputChars];
        try { return ExtractCore(html); }
        catch (RegexMatchTimeoutException) { return new ReadableArticle(string.Empty, string.Empty, false); }
    }

    private static ReadableArticle ExtractCore(string html)
    {
        string title = FindTitle(html);
        Node root = Parse(html);
        var candidates = new List<Node>();
        Score(root, candidates);
        foreach (Node c in candidates) c.Score *= 1.0 - LinkDensity(c);

        Node? best = candidates.OrderByDescending(c => c.Score).FirstOrDefault();
        bool heuristic = best != null && best.Score > 20;
        string text = heuristic ? Render(best!) : Render(root);
        text = Blank.Replace(string.Join('\n', text.Split('\n').Select(l => Spaces.Replace(l, " ").Trim())), "\n\n").Trim();
        if (title.Length == 0)
        {
            Match h1 = Regex.Match(text, @"^# (.+)$", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
            if (h1.Success) title = h1.Groups[1].Value.Trim();
        }
        return new ReadableArticle(title, text, heuristic);
    }

    private static string FindTitle(string html)
    {
        Match m = Regex.Match(html, "<title\\b[^>]*>(.*?)</title\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        if (!m.Success) return string.Empty;
        string t = WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(1)));
        t = Spaces.Replace(t.Replace('\n', ' ').Replace('\r', ' '), " ").Trim();
        return t.Length > 200 ? t[..200] : t;
    }

    private static Node Parse(string html)
    {
        var root = new Node { Name = "#root" };
        Node cur = root;
        int nodes = 0, pos = 0;
        int skipDepth = 0; // inside a dropped element
        string? skipName = null;
        foreach (Match m in Tag.Matches(html))
        {
            if (m.Index > pos && skipDepth == 0) AddText(cur, html.AsSpan(pos, m.Index - pos));
            pos = m.Index + m.Length;
            if (!m.Groups[2].Success) continue; // comment
            string name = m.Groups[2].Value.ToLowerInvariant();
            bool closing = m.Groups[1].Length > 0;
            bool selfClosed = m.Value.EndsWith("/>", StringComparison.Ordinal) || Void.Contains(name);

            if (skipDepth > 0)
            {
                if (name == skipName) { if (closing) skipDepth--; else if (!selfClosed) skipDepth++; }
                continue;
            }
            if (closing)
            {
                for (Node? n = cur; n != null && n != root; n = n.Parent)
                    if (n.Name == name) { cur = n.Parent ?? root; break; }
                continue;
            }
            string attrs = m.Groups[3].Value;
            string hint = string.Empty;
            foreach (Match a in Attr.Matches(attrs)) hint += " " + (a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Value);
            hint = hint.Trim();
            bool hiddenAttr = HiddenAttr.IsMatch(attrs)
                || attrs.Replace(" ", string.Empty).Contains("display:none", StringComparison.OrdinalIgnoreCase)
                || attrs.Contains("aria-hidden=\"true\"", StringComparison.OrdinalIgnoreCase);
            bool unlikely = hint.Length > 0 && Unlikely.IsMatch(hint) && !Likely.IsMatch(hint) && name is not ("body" or "html" or "article" or "main");
            if (Drop.Contains(name) || hiddenAttr || unlikely)
            {
                if (!selfClosed) { skipDepth = 1; skipName = name; }
                continue;
            }
            if (selfClosed)
            {
                if (name == "br") AddText(cur, "\n");
                continue;
            }
            if (++nodes > MaxNodes || cur.Depth >= MaxDepth) break;
            var node = new Node { Name = name, Hint = hint, Parent = cur, Depth = cur.Depth + 1 };
            cur.Children.Add(node);
            cur = node;
        }
        if (pos < html.Length && skipDepth == 0) AddText(cur, html.AsSpan(pos));
        return root;
    }

    private static void AddText(Node node, ReadOnlySpan<char> raw)
    {
        if (raw.IsWhiteSpace()) return;
        string text = WebUtility.HtmlDecode(raw.ToString());
        if (text.Trim().Length > 0) node.Children.Add(text);
    }

    private static void Score(Node node, List<Node> candidates)
    {
        foreach (object child in node.Children) if (child is Node n) Score(n, candidates);
        if (node.Name is not ("p" or "pre" or "td" or "blockquote") || node.Parent == null) return;
        string own = Plain(node);
        if (own.Length < 25) return;
        double points = 1 + own.Count(c => c == ',' || c == '،') + Math.Min(own.Length / 100, 3);
        Node parent = node.Parent;
        Init(parent, candidates);
        parent.Score += points;
        if (parent.Parent != null)
        {
            Init(parent.Parent, candidates);
            parent.Parent.Score += points / 2;
        }
    }

    private static void Init(Node n, List<Node> candidates)
    {
        if (candidates.Contains(n)) return;
        n.Score += n.Name switch { "article" => 8, "main" => 8, "div" => 5, "section" => 3, "pre" or "td" or "blockquote" => 3, "body" or "html" or "#root" => -5, _ => 0 };
        if (n.Hint.Length > 0) n.Score += Likely.IsMatch(n.Hint) ? 25 : 0;
        candidates.Add(n);
    }

    private static double LinkDensity(Node n)
    {
        int total = Plain(n).Length;
        if (total == 0) return 0;
        return Math.Min(1.0, LinkChars(n) / (double)total);
    }

    private static int LinkChars(Node n)
    {
        int sum = 0;
        foreach (object c in n.Children)
            if (c is Node child) sum += child.Name == "a" ? Plain(child).Length : LinkChars(child);
        return sum;
    }

    private static string Plain(Node n)
    {
        var sb = new StringBuilder();
        Collect(n, sb);
        return sb.ToString().Trim();
    }

    private static void Collect(Node n, StringBuilder sb)
    {
        foreach (object c in n.Children)
        {
            if (c is string s) sb.Append(s);
            else Collect((Node)c, sb);
            if (sb.Length > 200_000) return;
        }
    }

    private static string Render(Node n)
    {
        var sb = new StringBuilder();
        RenderInto(n, sb);
        return sb.ToString();
    }

    private static void RenderInto(Node n, StringBuilder sb)
    {
        if (sb.Length > 400_000) return;
        bool block = Block.Contains(n.Name);
        if (n.Name.Length == 2 && n.Name[0] == 'h' && char.IsAsciiDigit(n.Name[1]))
        {
            sb.Append("\n\n").Append('#', n.Name[1] - '0').Append(' ').Append(Spaces.Replace(Plain(n).Replace('\n', ' '), " ").Trim()).Append("\n\n");
            return;
        }
        if (block) sb.Append(n.Name == "li" ? "\n- " : "\n");
        foreach (object c in n.Children)
        {
            if (c is string s) sb.Append(s.Replace('\r', ' ').Replace('\n', ' '));
            else RenderInto((Node)c, sb);
        }
        if (block) sb.Append(n.Name is "p" or "pre" or "blockquote" ? "\n\n" : "\n");
    }
}
