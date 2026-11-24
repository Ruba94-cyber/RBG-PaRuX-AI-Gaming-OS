using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Desktop-owner routes for the optional local OpenAI-compatible provider. Reading, changing and testing it are
/// desktop-only: a LAN device, browser session or API key must not repoint chat or make PaRuX dial out.
/// </summary>
public static class OpenAiCompatibleProviderEndpoints
{
    public static void MapOpenAiCompatibleProviderEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/ai/openai-compatible", (OpenAiCompatibleProviderService provider) =>
            Results.Ok(provider.GetStatus())).RequireDesktopOwner();

        app.MapPut("/api/v1/ai/openai-compatible", (OpenAiCompatibleProviderRequest request, OpenAiCompatibleProviderService provider) =>
        {
            if (request is null) return Results.BadRequest(new { error = "A request body is required." });
            if (!OpenAiCompatibleProviderPresets.TryApply(request, out string? presetError))
                return Results.BadRequest(new { error = presetError });
            try { return Results.Ok(provider.Save(request)); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            { return Results.Problem("The server settings could not be saved."); }
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/ai/openai-compatible", (OpenAiCompatibleProviderService provider) =>
        {
            provider.Clear();
            return Results.NoContent();
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/ai/openai-compatible/test", async (
            OpenAiCompatibleProviderRequest? request, OpenAiCompatibleProviderService provider, CancellationToken ct) =>
        {
            if (request != null && !OpenAiCompatibleProviderPresets.TryApply(request, out string? presetError))
                return Results.BadRequest(new { error = presetError });
            try { return Results.Ok(await provider.TestAsync(request, ct)); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
        }).RequireDesktopOwner();
    }
}
