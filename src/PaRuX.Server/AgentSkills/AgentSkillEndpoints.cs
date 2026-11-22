using Microsoft.AspNetCore.Mvc;

namespace PaRuX.Server.AgentSkills;

public sealed class AttachDataSourceRequest { public string Path { get; set; } = string.Empty; }

public sealed class SqlQueryRequest
{
    public string Source { get; set; } = string.Empty;
    public string Sql { get; set; } = string.Empty;
    public int MaxRows { get; set; }
}

/// <summary>
/// Desktop-owner management of the files the read-only SQL skill may read, plus a direct query route used for
/// verification. Nothing here is reachable from a browser session, a paired device or an API key.
/// </summary>
internal static class AgentSkillEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/agent-skills/data-sources", ([FromServices] AttachedDataSources sources) =>
            Results.Ok(new { sources = sources.List() })).RequireDesktopOwner();

        app.MapPost("/api/v1/agent-skills/data-sources", (AttachDataSourceRequest request, [FromServices] AttachedDataSources sources) =>
        {
            try { return Results.Ok(sources.Attach(request.Path)); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (IOException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError); }
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/agent-skills/data-sources/{name}", (string name, [FromServices] AttachedDataSources sources) =>
            sources.Remove(name) ? Results.NoContent() : Results.NotFound(new { error = "No attached data source with that name." })).RequireDesktopOwner();

        app.MapPost("/api/v1/agent-skills/sql/query", (SqlQueryRequest request, [FromServices] ReadOnlySqlQueryService sql, CancellationToken ct) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Sql))
                    return Results.Text(sql.DescribeSchema(request.Source, ct));
                return Results.Text(ReadOnlySqlQueryService.ToJson(sql.Query(request.Source, request.Sql, request.MaxRows, null, ct)), "application/json");
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (TimeoutException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status408RequestTimeout); }
        }).RequireDesktopOwner();
    }
}
