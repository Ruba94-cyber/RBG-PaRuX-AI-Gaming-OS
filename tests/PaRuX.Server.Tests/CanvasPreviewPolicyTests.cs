using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CanvasPreviewPolicyTests
{
    [Theory]
    [InlineData("html", CanvasPreviewKind.Html)]
    [InlineData("HTML", CanvasPreviewKind.Html)]
    [InlineData(" htm ", CanvasPreviewKind.Html)]
    [InlineData("svg", CanvasPreviewKind.Svg)]
    [InlineData("python", CanvasPreviewKind.None)]
    [InlineData("javascript", CanvasPreviewKind.None)]
    [InlineData("", CanvasPreviewKind.None)]
    [InlineData(null, CanvasPreviewKind.None)]
    public void Only_html_and_svg_blocks_are_previewable(string? language, CanvasPreviewKind expected) =>
        Assert.Equal(expected, CanvasPreviewPolicy.Classify(language));

    [Fact]
    public void Csp_has_no_network_file_frame_or_form_capability_and_scripts_are_off_by_default()
    {
        string csp = CanvasPreviewPolicy.ContentSecurityPolicy(allowScripts: false);
        Assert.StartsWith("default-src 'none'", csp, StringComparison.Ordinal);
        foreach (string directive in new[] { "connect-src 'none'", "frame-src 'none'", "object-src 'none'", "form-action 'none'", "base-uri 'none'", "worker-src 'none'", "script-src 'none'" })
            Assert.Contains(directive, csp, StringComparison.Ordinal);
        Assert.Contains("img-src data:", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("http", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("*", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("file:", csp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allowing_scripts_only_adds_inline_scripts_and_never_a_network_or_file_source()
    {
        string off = CanvasPreviewPolicy.ContentSecurityPolicy(false);
        string on = CanvasPreviewPolicy.ContentSecurityPolicy(true);
        Assert.Contains("script-src 'unsafe-inline'", on, StringComparison.Ordinal);
        Assert.Equal(off.Replace("script-src 'none'", "script-src 'unsafe-inline'"), on);
        Assert.Contains("connect-src 'none'", on, StringComparison.Ordinal);
    }

    [Fact]
    public void The_policy_comes_before_any_model_authored_byte_and_model_meta_base_link_are_removed()
    {
        string code = "<meta http-equiv=\"refresh\" content=\"0;url=https://example.test\"><base href=\"https://example.test/\">"
            + "<link rel=\"stylesheet\" href=\"https://example.test/x.css\"><h1>hello</h1>";
        CanvasPreviewDocument doc = CanvasPreviewPolicy.Build(code, "html", allowScripts: false);

        Assert.True(doc.Ok);
        Assert.Equal(3, doc.RemovedElements);
        int csp = doc.Document.IndexOf("Content-Security-Policy", StringComparison.Ordinal);
        int body = doc.Document.IndexOf("<h1>hello</h1>", StringComparison.Ordinal);
        Assert.InRange(csp, 0, body - 1);
        Assert.DoesNotContain("refresh", doc.Document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", doc.Document, StringComparison.Ordinal);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(doc.Document, "http-equiv", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count);
    }

    [Fact]
    public void Embedded_frames_and_objects_are_removed_with_their_content()
    {
        CanvasPreviewDocument doc = CanvasPreviewPolicy.Build(
            "<p>a</p><iframe src=\"https://example.test\">x</iframe><object data=\"file:///c:/secret\"></object><embed src=\"x\"><p>b</p>", "html", false);
        Assert.True(doc.Ok);
        Assert.DoesNotContain("iframe", doc.Document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<object", doc.Document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<embed", doc.Document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<p>a</p>", doc.Document, StringComparison.Ordinal);
        Assert.Contains("<p>b</p>", doc.Document, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("python", "print(1)", "unsupported_language")]
    [InlineData("html", "   ", "empty")]
    [InlineData("svg", "<div>not svg</div>", "not_svg")]
    [InlineData("html", "a\0b", "invalid_characters")]
    public void Unsafe_or_unsupported_blocks_are_refused_with_a_reason(string language, string code, string reason)
    {
        CanvasPreviewDocument doc = CanvasPreviewPolicy.Build(code, language, false);
        Assert.False(doc.Ok);
        Assert.Equal(reason, doc.RefusalCode);
        Assert.Equal(string.Empty, doc.Document);
        Assert.False(string.IsNullOrWhiteSpace(CanvasPreviewPolicy.RefusalMessage(reason, italian: false)));
        Assert.False(string.IsNullOrWhiteSpace(CanvasPreviewPolicy.RefusalMessage(reason, italian: true)));
    }

    [Fact]
    public void Oversized_blocks_are_refused()
    {
        CanvasPreviewDocument doc = CanvasPreviewPolicy.Build(new string('a', CanvasPreviewPolicy.MaxSourceCharacters + 1), "html", false);
        Assert.False(doc.Ok);
        Assert.Equal("too_large", doc.RefusalCode);
    }

    [Fact]
    public void A_valid_svg_is_wrapped_and_scripts_stay_off_unless_asked()
    {
        CanvasPreviewDocument doc = CanvasPreviewPolicy.Build("<?xml version=\"1.0\"?><svg width=\"10\" height=\"10\"><rect/></svg>", "svg", false);
        Assert.True(doc.Ok);
        Assert.False(doc.ScriptsEnabled);
        Assert.Contains("<svg", doc.Document, StringComparison.Ordinal);
        Assert.Contains("script-src 'none'", doc.Document, StringComparison.Ordinal);
        Assert.True(CanvasPreviewPolicy.Build("<svg/>", "svg", true).ScriptsEnabled);
    }

    [Theory]
    [InlineData("about:blank", true)]
    [InlineData("data:text/html;charset=utf-8;base64,AAAA", true)]
    [InlineData("https://example.test/", false)]
    [InlineData("http://127.0.0.1:8080/api/v1/health", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-appx:///x", false)]
    [InlineData("blob:https://x/1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Navigation_is_limited_to_the_in_memory_document(string? uri, bool allowed) =>
        Assert.Equal(allowed, CanvasPreviewPolicy.IsAllowedNavigation(uri));

    [Theory]
    [InlineData("data:image/png;base64,AAAA", true)]
    [InlineData("https://example.test/a.png", false)]
    [InlineData("http://localhost:5000/", false)]
    [InlineData("file:///C:/a.png", false)]
    [InlineData("ftp://x/y", false)]
    [InlineData(null, false)]
    public void Sub_resources_may_only_be_inline_data(string? uri, bool allowed) =>
        Assert.Equal(allowed, CanvasPreviewPolicy.IsAllowedResourceRequest(uri));
}

/// <summary>
/// The WebView2 host lives in PaRuX.App (not referenced here), so the wiring that applies the policy is pinned as text:
/// removing any of these lines would silently open the sandbox.
/// </summary>
public sealed class CanvasPreviewSourceContractTests
{
    [Fact]
    public void The_canvas_frame_applies_every_sandbox_restriction()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PaRuX.App", "Views", "ChatView.Canvas.cs"));
        foreach (string required in new[]
        {
            "AreHostObjectsAllowed = false", "IsWebMessageEnabled = false", "AreDevToolsEnabled = false",
            "AreDefaultContextMenusEnabled = false", "NavigationStarting", "CanvasPreviewPolicy.IsAllowedNavigation",
            "NewWindowRequested", "e.Handled = true", "DownloadStarting", "PermissionRequested", "CoreWebView2PermissionState.Deny",
            "AddWebResourceRequestedFilter", "CanvasPreviewPolicy.IsAllowedResourceRequest", "403",
            "IsScriptEnabled = doc.ScriptsEnabled", "CanvasPreviewPolicy.Build"
        })
            Assert.Contains(required, source, StringComparison.Ordinal);
        // Scripts are an Advanced opt-in: the checkbox only exists outside Normal mode and starts unchecked.
        Assert.Contains("IsChecked = false", source, StringComparison.Ordinal);
        Assert.Contains("advanced ? Visibility.Visible : Visibility.Collapsed", source, StringComparison.Ordinal);
        // The frame never receives a navigable URL: only an in-memory document.
        Assert.Contains("NavigateToString(doc.Document)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Navigate(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Source = new Uri", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_blocks_offer_a_preview_only_for_previewable_languages_and_the_chat_hooks_it()
    {
        string md = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PaRuX.App", "Services", "MarkdownRenderer.cs"));
        Assert.Contains("CanvasPreviewPolicy.Classify(language)", md, StringComparison.Ordinal);
        Assert.Contains("Chat_CodeBlock_Preview_Button", md, StringComparison.Ordinal);
        string chat = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PaRuX.App", "Views", "ChatView.xaml.cs"));
        Assert.Contains("HookCanvasPreview();", chat, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}
