namespace PaRuX.Server.Gateway;

internal static class OpenApiEndpoints
{
    internal static void Map(WebApplication app)
    {
        // Desktop owner only: the route table is this machine's own surface, not something a browser session needs.
        app.MapGet("/openapi.json", (EndpointDataSource source) =>
            Results.Json(OpenApiDocument.Build(OpenApiDocument.Collect(new[] { source }),
                typeof(OpenApiDocument).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"))).RequireDesktopOwner();
    }
}
