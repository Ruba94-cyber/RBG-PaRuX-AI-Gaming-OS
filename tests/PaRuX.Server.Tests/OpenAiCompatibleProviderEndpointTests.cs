using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Routes of the optional local OpenAI-compatible provider. Every request here is refused or rejected by validation
/// before anything is stored or any server is contacted, so the tests never touch the real provider settings.
/// </summary>
public sealed class OpenAiCompatibleProviderEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public OpenAiCompatibleProviderEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private HttpClient Desktop()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    [Fact]
    public async Task The_desktop_can_read_the_status()
    {
        using HttpClient desktop = Desktop();
        HttpResponseMessage response = await desktop.GetAsync("/api/v1/ai/openai-compatible");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        OpenAiCompatibleProviderStatus? status = await response.Content.ReadFromJsonAsync<OpenAiCompatibleProviderStatus>();
        Assert.Equal("USER_MANAGED", status!.Ownership);
    }

    [Theory]
    [InlineData("https://api.openai.com")]
    [InlineData("http://8.8.8.8:8000")]
    [InlineData("http://example.com")]
    public async Task Saving_a_public_address_is_refused_with_a_reason(string url)
    {
        using HttpClient desktop = Desktop();
        HttpResponseMessage response = await desktop.PutAsJsonAsync("/api/v1/ai/openai-compatible",
            new OpenAiCompatibleProviderRequest { BaseUrl = url, ApiKey = "test-only-key", UseForChat = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("error", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Testing_an_invalid_candidate_reports_invalid_config_without_dialling_out()
    {
        using HttpClient desktop = Desktop();
        HttpResponseMessage response = await desktop.PostAsJsonAsync("/api/v1/ai/openai-compatible/test",
            new OpenAiCompatibleProviderRequest { BaseUrl = "https://api.openai.com" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        OpenAiCompatibleTestResult? result = await response.Content.ReadFromJsonAsync<OpenAiCompatibleTestResult>();
        Assert.Equal(OpenAiCompatibleStates.InvalidConfig, result!.State);
    }

    [Fact]
    public async Task A_request_without_the_desktop_token_is_refused()
    {
        using HttpClient anonymous = _factory.CreateClient();
        HttpResponseMessage put = await anonymous.PutAsJsonAsync("/api/v1/ai/openai-compatible",
            new OpenAiCompatibleProviderRequest { BaseUrl = "http://127.0.0.1:8000" });
        HttpResponseMessage get = await anonymous.GetAsync("/api/v1/ai/openai-compatible");
        Assert.NotEqual(HttpStatusCode.OK, put.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, get.StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, put.StatusCode);
    }
}
