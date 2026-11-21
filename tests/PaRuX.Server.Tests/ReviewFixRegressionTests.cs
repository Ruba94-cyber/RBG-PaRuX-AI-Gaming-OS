using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Smaller regressions from the second independent review.</summary>
public sealed class ReviewFixRegressionTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public ReviewFixRegressionTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private sealed class ThrowingEngine : ILocalInferenceEngine
    {
        public LocalInferenceEngineDescriptor Descriptor { get; } = new("t", "t", EngineImplementationState.Integrated,
            new HashSet<ManagedModelFormat>(), LocalInferenceCapabilities.Text, new HashSet<string>(), false, false);
        public Task<LocalInferenceEngineStatus> GetStatusAsync(CancellationToken c) => Task.FromResult(new LocalInferenceEngineStatus("t", true, true, null, null));
        public Task EnsureReadyAsync(string m, CancellationToken c) => Task.CompletedTask;
        public async IAsyncEnumerable<LocalInferenceChunk> StreamAsync(LocalInferenceRequest r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken c)
        {
            await Task.Yield();
            if (c.CanBeCanceled || true) throw new InvalidOperationException(@"secret C:\Users\someone\models\x.gguf at 192.168.1.50");
            yield break;
        }
        public Task StopAsync(CancellationToken c) => Task.CompletedTask;
    }

    [Fact]
    public async Task An_unexpected_stream_exception_is_not_reflected_to_the_chat_caller()
    {
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;

        LocalInferenceChatStreamResult result = await LocalInferenceChatBridge.StreamExternalProviderAsync(
            new ThrowingEngine(), new LocalInferenceRequest("m", new List<(string, string)> { ("user", "x") }, 0, double.NaN, true), http.Response, default);

        string sse = Encoding.UTF8.GetString(body.ToArray());
        Assert.False(result.Completed);
        Assert.DoesNotContain("secret", sse);
        Assert.DoesNotContain("192.168", sse);
        Assert.DoesNotContain("someone", result.Error);
        Assert.Contains("failed unexpectedly", sse);
    }

    [Fact]
    public async Task Deleting_a_knowledge_source_requires_the_desktop_owner()
    {
        using HttpClient anonymous = _factory.CreateClient();
        HttpResponseMessage response = await anonymous.DeleteAsync("/api/v1/rag/sources/does-not-exist");
        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, "status " + response.StatusCode);

        using HttpClient desktop = _factory.CreateClient();
        desktop.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        Assert.Equal(HttpStatusCode.NoContent, (await desktop.DeleteAsync("/api/v1/rag/sources/does-not-exist")).StatusCode);
    }
}
