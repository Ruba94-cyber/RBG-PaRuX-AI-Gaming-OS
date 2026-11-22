using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Server.Gateway;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OpenApiDocumentTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public OpenApiDocumentTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public void Schema_snapshot_for_a_fixed_route_table()
    {
        var routes = new[]
        {
            new OpenApiRoute("/api/v1/mcp/servers/{id}", "DELETE", true),
            new OpenApiRoute("/api/v1/mcp/servers", "GET", true),
            new OpenApiRoute("/v1/models", "GET", false),
            new OpenApiRoute("/v1/models", "GET", false)   // duplicate registrations collapse
        };
        string json = OpenApiDocument.Build(routes, "1.2.3").ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        string expected = (
            "{\"openapi\":\"3.0.3\",\"info\":{\"title\":\"PaRuX local API\",\"version\":\"1.2.3\",\"description\":\"Generated from this build's registered routes. Paths, methods and path parameters are exact; request and response bodies are not described. Served locally; nothing leaves the machine.\"},"
            + "\"servers\":[{\"url\":\"http://127.0.0.1\"}],\"components\":{\"securitySchemes\":{\"parux-token\":{\"type\":\"apiKey\",\"in\":\"header\",\"name\":\"X-PaRuX-Token\"}}},"
            + "\"paths\":{\"/api/v1/mcp/servers\":{\"get\":{\"operationId\":\"getApiV1McpServers\",\"tags\":[\"mcp\"],\"summary\":\"GET /api/v1/mcp/servers\",\"x-parux-owner-only\":true,\"security\":[{\"parux-token\":[]}],\"responses\":{\"default\":{\"description\":\"See the PaRuX documentation; the body is not described here.\"}},\"description\":\"Desktop owner only: a browser session, paired device or API key is refused with 403.\"}},"
            + "\"/api/v1/mcp/servers/{id}\":{\"delete\":{\"operationId\":\"deleteApiV1McpServersId\",\"tags\":[\"mcp\"],\"summary\":\"DELETE /api/v1/mcp/servers/{id}\",\"x-parux-owner-only\":true,\"security\":[{\"parux-token\":[]}],\"responses\":{\"default\":{\"description\":\"See the PaRuX documentation; the body is not described here.\"}},\"parameters\":[{\"name\":\"id\",\"in\":\"path\",\"required\":true,\"schema\":{\"type\":\"string\"}}],\"description\":\"Desktop owner only: a browser session, paired device or API key is refused with 403.\"}},"
            + "\"/v1/models\":{\"get\":{\"operationId\":\"getV1Models\",\"tags\":[\"gateway-models\"],\"summary\":\"GET /v1/models\",\"x-parux-owner-only\":false,\"security\":[{\"parux-token\":[]}],\"responses\":{\"default\":{\"description\":\"See the PaRuX documentation; the body is not described here.\"}}}}}}").Replace("'", "\\u0027");
        Assert.Equal(expected, json);
    }

    [Fact]
    public async Task Openapi_requires_a_token()
    {
        using HttpClient client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/openapi.json")).StatusCode);
    }

    [Fact]
    public async Task Openapi_is_served_to_the_owner_and_matches_the_registered_routes()
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        HttpResponseMessage response = await client.GetAsync("/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonObject doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("3.0.3", doc["openapi"]!.GetValue<string>());
        JsonObject paths = doc["paths"]!.AsObject();
        Assert.True(paths["/api/v1/mcp/servers"]!["post"]!["x-parux-owner-only"]!.GetValue<bool>());
        Assert.True(paths["/api/v1/agent-skills/data-sources"]!["get"]!["x-parux-owner-only"]!.GetValue<bool>());
        Assert.False(paths["/v1/chat/completions"]!["post"]!["x-parux-owner-only"]!.GetValue<bool>());
        Assert.NotNull(paths["/openapi.json"]);
        // Every registered route is documented: the document cannot silently omit one.
        var documented = paths.SelectMany(p => p.Value!.AsObject().Select(m => m.Key.ToUpperInvariant() + " " + p.Key)).ToHashSet();
        var registered = OpenApiDocument.Collect(new[] { _factory.Services.GetRequiredService<EndpointDataSource>() })
            .Select(r => r.Method + " " + r.Pattern).ToHashSet();
        Assert.Empty(registered.Except(documented));
    }

    [Fact]
    public async Task Mcp_management_routes_refuse_a_browser_session_and_a_missing_token()
    {
        using HttpClient anonymous = _factory.CreateClient();
        foreach ((HttpMethod method, string path) in new[]
        {
            (HttpMethod.Get, "/api/v1/mcp/servers"), (HttpMethod.Post, "/api/v1/mcp/servers"),
            (HttpMethod.Delete, "/api/v1/mcp/servers/x"), (HttpMethod.Post, "/api/v1/mcp/servers/x/enabled")
        })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(new HttpRequestMessage(method, path))).StatusCode);
    }
}
