using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;

namespace PaRuX.Server.Gateway;

/// <summary>Marks a route that only the desktop owner may call; read by the OpenAPI document.</summary>
internal sealed class DesktopOwnerOnlyMarker
{
    public static readonly DesktopOwnerOnlyMarker Instance = new();
}

internal sealed record OpenApiRoute(string Pattern, string Method, bool OwnerOnly);

/// <summary>
/// OpenAPI 3.0 description of the routes this build actually registers. It is generated from the route table, so it cannot
/// drift from the code: every path, method and path parameter is real. Request and response bodies are not described (the
/// document says so), and a route is flagged owner-only when it carries the desktop-owner gate or is on the known inline-gated list.
/// </summary>
internal static class OpenApiDocument
{
    private static readonly string[] InlineOwnerPrefixes = { "/api/v1/mcp/servers", "/api/v1/orchestrator/tasks/{id}/mcp-tools" };

    internal static List<OpenApiRoute> Collect(IEnumerable<EndpointDataSource> sources)
    {
        var routes = new List<OpenApiRoute>();
        foreach (RouteEndpoint e in sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            string pattern = e.RoutePattern.RawText ?? string.Empty;
            if (!pattern.StartsWith('/')) pattern = "/" + pattern;
            bool owner = e.Metadata.GetMetadata<DesktopOwnerOnlyMarker>() != null
                || InlineOwnerPrefixes.Any(p => pattern.StartsWith(p, StringComparison.Ordinal));
            IReadOnlyList<string> methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>();
            foreach (string m in methods) routes.Add(new OpenApiRoute(pattern, m.ToUpperInvariant(), owner));
        }
        return routes;
    }

    private static string OperationId(string method, string[] segments)
    {
        var id = new System.Text.StringBuilder(method.ToLowerInvariant());
        foreach (string seg in segments)
        {
            string clean = new(seg.Where(char.IsLetterOrDigit).ToArray());
            if (clean.Length == 0) continue;
            id.Append(char.ToUpperInvariant(clean[0])).Append(clean, 1, clean.Length - 1);
        }
        return id.ToString();
    }

    internal static JsonObject Build(IEnumerable<OpenApiRoute> routes, string version)
    {
        var paths = new JsonObject();
        foreach (IGrouping<string, OpenApiRoute> group in routes.GroupBy(r => r.Pattern).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var item = new JsonObject();
            foreach (OpenApiRoute r in group.OrderBy(r => r.Method, StringComparer.Ordinal).DistinctBy(r => r.Method))
            {
                string[] segments = r.Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string tag = segments.Length == 0 ? "root"
                    : segments[0] == "api" && segments.Length > 2 ? segments[2]
                    : segments[0] == "v1" && segments.Length > 1 ? "gateway-" + segments[1]
                    : segments[0];
                var parameters = new JsonArray();
                foreach (string seg in segments.Where(s => s.StartsWith('{') && s.EndsWith('}')))
                    parameters.Add(new JsonObject
                    {
                        ["name"] = seg.Trim('{', '}').TrimStart('*').Split(':')[0],
                        ["in"] = "path",
                        ["required"] = true,
                        ["schema"] = new JsonObject { ["type"] = "string" }
                    });
                var op = new JsonObject
                {
                    ["operationId"] = OperationId(r.Method, segments),
                    ["tags"] = new JsonArray(tag),
                    ["summary"] = r.Method + " " + r.Pattern,
                    ["x-parux-owner-only"] = r.OwnerOnly,
                    ["security"] = new JsonArray(new JsonObject { ["parux-token"] = new JsonArray() }),
                    ["responses"] = new JsonObject { ["default"] = new JsonObject { ["description"] = "See the PaRuX documentation; the body is not described here." } }
                };
                if (parameters.Count > 0) op["parameters"] = parameters;
                if (r.OwnerOnly) op["description"] = "Desktop owner only: a browser session, paired device or API key is refused with 403.";
                item[r.Method.ToLowerInvariant()] = op;
            }
            paths[group.Key] = item;
        }
        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = "PaRuX local API",
                ["version"] = version,
                ["description"] = "Generated from this build's registered routes. Paths, methods and path parameters are exact; request and response bodies are not described. Served locally; nothing leaves the machine."
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "http://127.0.0.1" }),
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["parux-token"] = new JsonObject { ["type"] = "apiKey", ["in"] = "header", ["name"] = "X-PaRuX-Token" }
                }
            },
            ["paths"] = paths
        };
    }
}
