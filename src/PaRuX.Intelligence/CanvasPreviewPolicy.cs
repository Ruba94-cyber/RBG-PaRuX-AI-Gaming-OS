using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Intelligence;

public enum CanvasPreviewKind { None, Html, Svg }

/// <summary>Result of preparing a code block for the Canvas preview frame.</summary>
/// <param name="Ok">False when the block must not be previewed; <see cref="RefusalCode"/> says why.</param>
/// <param name="Document">The complete, self-contained document to hand to the sandboxed frame.</param>
/// <param name="ScriptsEnabled">True only when the owner explicitly allowed scripts for this preview.</param>
public sealed record CanvasPreviewDocument(
    bool Ok, CanvasPreviewKind Kind, string Document, string? RefusalCode, bool ScriptsEnabled, int RemovedElements);

/// <summary>
/// Pure policy for the chat Canvas: an HTML/SVG code block can be previewed in a frame that has no
/// network and no file access. This class only builds the document and decides what navigation or
/// sub-resource is acceptable; the WebView2 host (ChatView.Canvas) applies the decisions and a source
/// contract test pins that it does.
///
/// Defence in depth, in order of strength:
///  1. a strict Content-Security-Policy meta placed BEFORE any model-authored byte (default-src 'none',
///     connect/frame/object/form/base all 'none', images/fonts/media from data: only);
///  2. the host cancels every navigation except the initial in-memory document and blocks every
///     sub-resource request that is not data:/about: (so even a CSP bypass finds no network);
///  3. scripts are OFF by default and, when the owner turns them on, still run under rule 1 and 2;
///  4. model-authored meta/base/link/iframe-type elements are removed before the document is built.
/// </summary>
public static class CanvasPreviewPolicy
{
    public const int MaxSourceCharacters = 200_000;

    public static CanvasPreviewKind Classify(string? language)
    {
        string l = (language ?? string.Empty).Trim().ToLowerInvariant();
        return l switch
        {
            "html" or "htm" or "xhtml" or "html5" => CanvasPreviewKind.Html,
            "svg" => CanvasPreviewKind.Svg,
            _ => CanvasPreviewKind.None
        };
    }

    public static string ContentSecurityPolicy(bool allowScripts) =>
        "default-src 'none'; "
        + "img-src data:; media-src data:; font-src data:; "
        + "style-src 'unsafe-inline'; "
        + (allowScripts ? "script-src 'unsafe-inline'; " : "script-src 'none'; ")
        + "connect-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; manifest-src 'none'; "
        + "object-src 'none'; form-action 'none'; base-uri 'none'";

    // Elements that can start a navigation, load a sub-document, change the base URL or re-declare the
    // policy. CSP and the host already refuse them; removing them keeps the preview honest as well
    // (a blocked iframe would otherwise render as a broken empty box).
    private static readonly Regex Dangerous = new(
        @"<\s*(?:meta\b[^>]*http-equiv[^>]*|base\b[^>]*|link\b[^>]*|iframe\b[^>]*>.*?<\s*/\s*iframe\s*|iframe\b[^>]*|frame\b[^>]*|frameset\b[^>]*|object\b[^>]*>.*?<\s*/\s*object\s*|object\b[^>]*|embed\b[^>]*|applet\b[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static readonly Regex SvgRoot = new(
        @"^\s*(?:<\?xml[^>]*\?>\s*)?(?:<!--.*?-->\s*)*<svg[\s>/]",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static CanvasPreviewDocument Build(string? code, string? language, bool allowScripts)
    {
        CanvasPreviewKind kind = Classify(language);
        if (kind == CanvasPreviewKind.None)
            return Refuse(kind, "unsupported_language");
        if (string.IsNullOrWhiteSpace(code))
            return Refuse(kind, "empty");
        if (code.Length > MaxSourceCharacters)
            return Refuse(kind, "too_large");
        if (code.Contains('\0'))
            return Refuse(kind, "invalid_characters");
        if (kind == CanvasPreviewKind.Svg && !SvgRoot.IsMatch(code))
            return Refuse(kind, "not_svg");

        string body = code;
        int removed = 0;
        try
        {
            body = Dangerous.Replace(code, _ => { removed++; return string.Empty; });
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological block is refused rather than previewed unsanitised.
            return Refuse(kind, "sanitize_timeout");
        }

        var doc = new StringBuilder(body.Length + 700);
        doc.Append("<!doctype html><html><head><meta charset=\"utf-8\">")
           .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentSecurityPolicy(allowScripts)).Append("\">")
           .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
           .Append("<style>:root{color-scheme:light}html,body{margin:0;padding:8px;background:#fff;color:#111;font-family:Segoe UI,sans-serif}")
           .Append(kind == CanvasPreviewKind.Svg ? "svg{max-width:100%;height:auto}" : string.Empty)
           .Append("</style></head><body>")
           .Append(body)
           .Append("</body></html>");
        return new CanvasPreviewDocument(true, kind, doc.ToString(), null, allowScripts, removed);
    }

    private static CanvasPreviewDocument Refuse(CanvasPreviewKind kind, string code) =>
        new(false, kind, string.Empty, code, false, 0);

    /// <summary>
    /// The frame may only show its own in-memory document. NavigateToString surfaces as about:blank or a
    /// text/html data: URL depending on the WebView2 build; everything else (http, https, file, ftp,
    /// ms-appx, blob, javascript:) is refused.
    /// </summary>
    public static bool IsAllowedNavigation(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        string u = uri.Trim();
        return u.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sub-resources (images, fonts, media) may be inline data only; nothing else leaves the frame.</summary>
    public static bool IsAllowedResourceRequest(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        string u = uri.Trim();
        return u.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || u.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
    }

    public static string RefusalMessage(string? code, bool italian) => code switch
    {
        "unsupported_language" => italian ? "Questo blocco non è HTML o SVG: non c'è nulla da mostrare in anteprima." : "This block is not HTML or SVG, so there is nothing to preview.",
        "empty" => italian ? "Il blocco è vuoto." : "The block is empty.",
        "too_large" => italian ? "Il blocco è troppo grande per l'anteprima (massimo 200.000 caratteri)." : "The block is too large to preview (200,000 characters at most).",
        "invalid_characters" => italian ? "Il blocco contiene caratteri non validi." : "The block contains invalid characters.",
        "not_svg" => italian ? "Il blocco non inizia con un elemento <svg>." : "The block does not start with an <svg> element.",
        "sanitize_timeout" => italian ? "Il blocco non si può verificare in modo sicuro, quindi non viene mostrato." : "The block could not be checked safely, so it is not shown.",
        _ => italian ? "Anteprima non disponibile." : "Preview is not available."
    };
}
