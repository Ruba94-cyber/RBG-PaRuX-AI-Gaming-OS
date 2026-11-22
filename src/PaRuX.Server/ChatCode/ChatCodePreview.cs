using System.Text.RegularExpressions;

namespace PaRuX.Server.ChatCode;

/// <summary>
/// HTML a program produced is untrusted. The preview document gets a Content-Security-Policy first (no network of any
/// kind, no frames, no forms, no base URL, only inline script/style and data: images) so even the preview cannot reach
/// out. The host must additionally show it with NavigateToString / a sandboxed iframe WITHOUT same-origin, never from a
/// file path and never with a host-object bridge.
/// </summary>
public static class ChatCodePreview
{
    public const string Policy =
        "default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; font-src data:; "
        + "media-src data:; connect-src 'none'; frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'";

    /// <summary>The iframe sandbox attribute a web host must use: scripts yes, same-origin/forms/popups/top-navigation no.</summary>
    public const string IframeSandbox = "allow-scripts";

    private static readonly Regex Head = new(@"<head(\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Html = new(@"<html(\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public static string Wrap(string html)
    {
        string meta = "<meta http-equiv=\"Content-Security-Policy\" content=\"" + Policy + "\">";
        try
        {
            Match head = Head.Match(html);
            if (head.Success) return html.Insert(head.Index + head.Length, meta);
            Match root = Html.Match(html);
            if (root.Success) return html.Insert(root.Index + root.Length, "<head>" + meta + "</head>");
        }
        catch (RegexMatchTimeoutException) { }
        return "<!doctype html><html><head>" + meta + "<meta charset=\"utf-8\"></head><body>" + html + "</body></html>";
    }
}
