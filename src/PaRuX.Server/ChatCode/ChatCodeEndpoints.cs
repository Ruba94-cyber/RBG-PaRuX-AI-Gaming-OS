using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server.ChatCode;

public sealed class ChatCodeSettingsRequest { public bool? Enabled { get; set; } }

public static class ChatCodeEndpoints
{
    public static IServiceCollection AddChatCodeExecution(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            LocalRuntimeService runtime = sp.GetRequiredService<LocalRuntimeService>();
            return ChatCodeRuntime.CreateDefault(() => runtime.GamingMode);
        });
        services.AddSingleton<IChatCodeProcessLauncher, AppContainerChatCodeLauncher>();
        services.AddSingleton(sp =>
        {
            LocalRuntimeService runtime = sp.GetRequiredService<LocalRuntimeService>();
            ChatCodeRuntime component = sp.GetRequiredService<ChatCodeRuntime>();
            return new ChatCodeRunner(sp.GetRequiredService<IChatCodeProcessLauncher>(), component.UsableInterpreter,
                () => runtime.GamingMode, PaRuXDataRoot.Combine("chat-code", "runs"));
        });
        services.AddSingleton<ChatCodeRunStore>();
        services.AddSingleton(sp =>
        {
            LocalRuntimeService runtime = sp.GetRequiredService<LocalRuntimeService>();
            // Own consent store: chat code approvals never mix with the Gaming library confirmations.
            return new ChatCodeTools(sp.GetRequiredService<ChatCodeRunner>(), sp.GetRequiredService<ChatCodeRuntime>(),
                new AgentLibraryConsentStore(), sp.GetRequiredService<ChatCodeRunStore>(), () => runtime.GamingMode);
        });
        return services;
    }

    /// <summary>Owner-only. There is deliberately no tool that reaches these routes: approving code is a human act.</summary>
    public static void MapChatCodeEndpoints(this WebApplication app)
    {
        const string root = "/api/v1/chat/code";

        app.MapGet(root + "/status", (ChatCodeTools tools) => Results.Ok(new
        {
            runtime = tools.Runtime.GetStatus(),
            toolAvailable = tools.Runtime.ToolAvailable,
            network = "none",
            isolation = "windows-appcontainer-job-object"
        })).RequireDesktopOwner();

        app.MapPut(root + "/settings", (ChatCodeSettingsRequest request, ChatCodeTools tools) =>
        {
            if (request?.Enabled is not bool enabled) return Results.BadRequest(new { error = "Send {\"enabled\": true|false}." });
            return tools.Runtime.SetEnabled(enabled) ? Results.Ok(tools.Runtime.GetStatus()) : Results.Problem("The setting could not be saved.");
        }).RequireDesktopOwner();

        app.MapPost(root + "/runtime/install", (ChatCodeTools tools) =>
        {
            string? refusal = tools.Runtime.BeginInstall();
            return refusal is null ? Results.Accepted(root + "/status", tools.Runtime.GetStatus()) : Results.Conflict(new { error = refusal });
        }).RequireDesktopOwner();

        app.MapPost(root + "/runtime/install/cancel", (ChatCodeTools tools) =>
        {
            tools.Runtime.CancelInstall();
            return Results.Ok(tools.Runtime.GetStatus());
        }).RequireDesktopOwner();

        app.MapPost(root + "/runtime/skip", (ChatCodeTools tools) =>
            tools.Runtime.SetSkipped(true) ? Results.Ok(tools.Runtime.GetStatus()) : Results.Problem("The choice could not be saved."))
            .RequireDesktopOwner();

        app.MapPost(root + "/runtime/unskip", (ChatCodeTools tools) =>
            tools.Runtime.SetSkipped(false) ? Results.Ok(tools.Runtime.GetStatus()) : Results.Problem("The choice could not be saved."))
            .RequireDesktopOwner();

        app.MapGet(root + "/approvals", (ChatCodeTools tools) => Results.Ok(tools.Consent.List())).RequireDesktopOwner();
        app.MapPost(root + "/approvals/{id}/approve", (string id, ChatCodeTools tools) =>
            tools.Consent.Approve(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();
        app.MapPost(root + "/approvals/{id}/deny", (string id, ChatCodeTools tools) =>
            tools.Consent.Deny(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();

        app.MapGet(root + "/runs/{id}", (string id, ChatCodeTools tools) =>
            tools.Runs.Get(id) is { } run ? Results.Ok(run) : Results.NotFound()).RequireDesktopOwner();
    }
}
