using System.Net;
using System.Net.Http.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MobileRuntimeDocumentEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public MobileRuntimeDocumentEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Theory]
    [InlineData("GET", "/api/v2/mobile/runtime", true)]
    [InlineData("POST", "/api/v2/mobile/runtime/load", true)]
    [InlineData("POST", "/api/v2/mobile/documents", true)]
    [InlineData("POST", "/api/v2/mobile/runtime", false)]
    [InlineData("GET", "/api/v2/mobile/runtime/load", false)]
    [InlineData("PUT", "/api/v2/mobile/documents", false)]
    [InlineData("DELETE", "/api/v2/mobile/documents", false)]
    [InlineData("POST", "/api/v1/runtime/actions", false)]
    [InlineData("POST", "/api/v1/rag/sources", false)]
    public void Paired_device_policy_allows_only_the_exact_mobile_routes(string method, string path, bool allowed) =>
        Assert.Equal(allowed, PairedDeviceEndpointPolicy.IsAllowed(method, path));

    [Theory]
    [InlineData("GET", "/api/v2/mobile/runtime")]
    [InlineData("POST", "/api/v2/mobile/runtime/load")]
    [InlineData("POST", "/api/v2/mobile/documents")]
    public async Task Routes_refuse_requests_without_a_token(string method, string path)
    {
        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = JsonContent.Create(new { });
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Runtime_projection_exposes_the_model_file_name_but_never_a_path()
    {
        var status = new RuntimeManagerStatus
        {
            ActiveRole = "standard",
            ActiveModelPath = @"D:\store\models\Qwen3-8B-Q4_K_M.gguf",
            ActiveHasVisionSupport = true,
            VramTotalMib = 16000,
            Backends = { new RuntimeBackendStatus { Backend = "text-cuda", Running = true, Healthy = true, ProcessId = 42 } }
        };

        var projected = MobileRuntimeDocumentEndpoints.ProjectRuntime(status);
        string json = System.Text.Json.JsonSerializer.Serialize(projected);

        Assert.True(projected.TextLoaded);
        Assert.Equal("Qwen3-8B-Q4_K_M.gguf", projected.ActiveModelName);
        Assert.DoesNotContain("store", json);
        Assert.DoesNotContain("42", json.Replace("16000", ""));
    }

    [Fact]
    public void Runtime_projection_reports_not_loaded_when_no_text_backend_is_healthy()
    {
        var status = new RuntimeManagerStatus
        {
            Backends = { new RuntimeBackendStatus { Backend = "text-cuda", Running = true, Healthy = false },
                         new RuntimeBackendStatus { Backend = "media-comfyui", Running = true, Healthy = true } }
        };
        var projected = MobileRuntimeDocumentEndpoints.ProjectRuntime(status);
        Assert.False(projected.TextLoaded);
        Assert.Null(projected.ActiveModelName);
    }

    private static MobileRuntimeDocumentEndpoints.MobileDocumentUploadRequest Upload(string name, byte[] data) =>
        new() { FileName = name, ContentBase64 = Convert.ToBase64String(data) };

    [Theory]
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("Report.PDF", "Report.PDF")]
    [InlineData(@"..\..\Windows\evil.md", "evil.md")]
    [InlineData("/etc/passwd.txt", "passwd.txt")]
    [InlineData(".hidden.txt", "hidden.txt")]
    public void Upload_validation_accepts_allowed_types_and_strips_any_directory(string input, string expected)
    {
        Assert.True(MobileRuntimeDocumentEndpoints.TryValidateUpload(Upload(input, new byte[] { 1, 2, 3 }), out string name, out byte[] bytes, out _));
        Assert.Equal(expected, name);
        Assert.Equal(3, bytes.Length);
    }

    [Theory]
    [InlineData("tool.exe")]
    [InlineData("script.ps1")]
    [InlineData("noextension")]
    [InlineData("archive.zip")]
    [InlineData("")]
    public void Upload_validation_rejects_executable_and_unknown_types(string input) =>
        Assert.False(MobileRuntimeDocumentEndpoints.TryValidateUpload(Upload(input, new byte[] { 1 }), out _, out _, out _));

    [Fact]
    public void Upload_validation_rejects_empty_oversized_and_malformed_content()
    {
        Assert.False(MobileRuntimeDocumentEndpoints.TryValidateUpload(Upload("a.txt", Array.Empty<byte>()), out _, out _, out _));
        Assert.False(MobileRuntimeDocumentEndpoints.TryValidateUpload(
            Upload("a.txt", new byte[MobileRuntimeDocumentEndpoints.MaxDocumentBytes + 1]), out _, out _, out _));
        Assert.False(MobileRuntimeDocumentEndpoints.TryValidateUpload(
            new() { FileName = "a.txt", ContentBase64 = "not base64 !!" }, out _, out _, out _));
        Assert.False(MobileRuntimeDocumentEndpoints.TryValidateUpload(null, out _, out _, out _));
    }
}
