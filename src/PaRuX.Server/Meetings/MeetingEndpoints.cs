using PaRuX.Server.Knowledge;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server.Meetings;

public sealed class MeetingSettingsUpdate
{
    public bool Enabled { get; set; }
    /// <summary>The consent wording version the person accepted (the app shows the text for <see cref="MeetingSettingsStore.RequiredConsentVersion"/>).</summary>
    public int ConsentVersion { get; set; }
}

public sealed class MeetingStartRequest
{
    public string? Title { get; set; }
    public string? Language { get; set; }
}

public sealed class MeetingChunkRequest
{
    public string AudioBase64 { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public double OffsetSeconds { get; set; }
}

public sealed class MeetingFinishRequest
{
    public bool AddToKnowledge { get; set; }
}

/// <summary>Meeting assistant routes. All are desktop-owner only; a browser session is default-deny (web-session-route-decisions.txt).</summary>
public static class MeetingEndpoints
{
    private static IResult Refuse(MeetingException ex) => Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: ex.Status);

    public static void MapMeetingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/meetings/settings", (MeetingAssistantService svc) =>
        {
            MeetingSettings s = svc.Settings.Get();
            return Results.Ok(new
            {
                enabled = s.Enabled, consentUtc = s.ConsentUtc, requiredConsentVersion = MeetingSettingsStore.RequiredConsentVersion,
                gamingActive = GamingHardOffWatch.MarkerActive(), active = svc.List().FirstOrDefault(m => m.State == "recording")?.Id
            });
        }).RequireDesktopOwner();

        app.MapPut("/api/v1/meetings/settings", (MeetingSettingsUpdate update, MeetingAssistantService svc) =>
        {
            try
            {
                MeetingSettings s = svc.Settings.Set(update.Enabled, update.ConsentVersion);
                AuditLogService.TryRecord("meeting_assistant", s.Enabled ? "enabled" : "disabled");
                return Results.Ok(new { enabled = s.Enabled, consentUtc = s.ConsentUtc });
            }
            catch (MeetingException ex) { return Refuse(ex); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/meetings", (MeetingAssistantService svc) => Results.Ok(svc.List())).RequireDesktopOwner();

        app.MapPost("/api/v1/meetings", (MeetingStartRequest request, MeetingAssistantService svc) =>
        {
            try { return Results.Ok(svc.Start(request?.Title, request?.Language)); }
            catch (MeetingException ex) { return Refuse(ex); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/meetings/{id}/chunks", async (string id, MeetingChunkRequest request, MeetingAssistantService svc, CancellationToken ct) =>
        {
            byte[] audio;
            try { audio = Convert.FromBase64String(request?.AudioBase64 ?? string.Empty); }
            catch (FormatException) { return Results.BadRequest(new { error = "audioBase64 is not valid Base64 audio data.", code = "bad_audio" }); }
            try { return Results.Ok(await svc.AddChunkAsync(id, audio, request!.FileName ?? "chunk.wav", request.OffsetSeconds, ct)); }
            catch (MeetingException ex) { return Refuse(ex); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/meetings/{id}/finish", async (string id, MeetingFinishRequest? request, MeetingAssistantService svc, CancellationToken ct) =>
        {
            try { return Results.Ok(await svc.FinishAsync(id, request?.AddToKnowledge ?? false, ct)); }
            catch (MeetingException ex) { return Refuse(ex); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/meetings/{id}/cancel", (string id, MeetingAssistantService svc) =>
            svc.Cancel(id) ? Results.NoContent() : Results.NotFound(new { error = "No recording meeting with that id." })).RequireDesktopOwner();

        app.MapGet("/api/v1/meetings/{id}/notes", (string id, MeetingAssistantService svc) =>
            svc.Read(id) is { } r ? Results.Ok(new { notes = r.Notes, transcript = r.Transcript }) : Results.NotFound(new { error = "Meeting not found." })).RequireDesktopOwner();

        app.MapDelete("/api/v1/meetings/{id}", (string id, MeetingAssistantService svc) =>
        {
            bool removed = svc.Delete(id);
            if (removed) AuditLogService.TryRecord("meeting_assistant", "deleted");
            return removed ? Results.NoContent() : Results.NotFound(new { error = "Meeting not found." });
        }).RequireDesktopOwner();
    }
}
