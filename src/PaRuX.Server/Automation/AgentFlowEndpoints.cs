using PaRuX.Contracts;

namespace PaRuX.Server.Automation;

/// <summary>
/// Agent Flows API for the no-code builder. Every route is desktop-owner only (a browser session is default-deny, see
/// web-session-route-decisions.txt). Enabling, disabling and deleting a flow use the existing automation routes; stopping a run
/// uses the existing orchestrator task cancel route on the task id the run log shows.
/// </summary>
public static class AgentFlowEndpoints
{
    public static void MapAgentFlowEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/agent-flows", (AutomationStore store) =>
            Results.Ok(store.List().Select(AgentFlowCompiler.Describe).Where(f => f != null).ToList())).RequireDesktopOwner();

        // Dry validation for the builder: the same compiler and validator as create, nothing is stored.
        app.MapPost("/api/v1/agent-flows/validate", (AgentFlowRequest request) =>
        {
            try { AgentFlowCompiler.Compile(request); return Results.Ok(new { valid = true }); }
            catch (InvalidDataException ex) { return Results.Json(new { valid = false, error = ex.Message }, statusCode: StatusCodes.Status400BadRequest); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/agent-flows", (AgentFlowRequest request, AutomationStore store) =>
        {
            try
            {
                AutomationDto created = store.Create(AgentFlowCompiler.Compile(request));
                AuditLogService.TryRecord("agent_flow.created", created.Id);
                return Results.Ok(AgentFlowCompiler.Describe(created));
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/agent-flows/{id}/runs", (string id, int? limit, AutomationStore store) =>
            store.Get(id) == null ? Results.NotFound(new { error = "Flow not found" }) : Results.Ok(store.ListRuns(id, limit ?? 50))).RequireDesktopOwner();

        // "Run now": same conditions and the same task creation as a trigger. Refused while Gaming Mode is on.
        app.MapPost("/api/v1/agent-flows/{id}/run", (string id, AutomationStore store, AutomationEngine engine) =>
        {
            AutomationDto? automation = store.Get(id);
            if (automation == null || AgentFlowCompiler.Describe(automation) == null) return Results.NotFound(new { error = "Flow not found" });
            if (GamingHardOffWatch.MarkerActive())
                return Results.Json(new { error = "Gaming Mode is active; flows do not run until it ends.", code = "gaming_active" }, statusCode: StatusCodes.Status409Conflict);
            AgentFlowRunDto? run = engine.RunNow(id);
            return run == null ? Results.NotFound(new { error = "Flow not found" }) : Results.Ok(run);
        }).RequireDesktopOwner();
    }
}
