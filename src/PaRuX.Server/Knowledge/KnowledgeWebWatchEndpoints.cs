namespace PaRuX.Server.Knowledge;

public sealed class WebSourceRequest
{
    public string Url { get; set; } = string.Empty;
    /// <summary>"page" (just this address) or "crawl" (pages of the same site below this address).</summary>
    public string Mode { get; set; } = "page";
    public int? MaxPages { get; set; }
    public int? MaxDepth { get; set; }
}

public sealed class WatchFolderRequest
{
    public bool Enabled { get; set; }
    /// <summary>True only after the person confirmed a broad or risky folder (whole drive, profile, application data).</summary>
    public bool ConsentBroad { get; set; }
}

/// <summary>Desktop-owner routes for websites and watched folders as Local Knowledge sources.</summary>
public static class KnowledgeWebWatchEndpoints
{
    public static void MapKnowledgeWebWatchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/knowledge/web-sources", (WebKnowledgeSources web) => Results.Ok(web.List())).RequireDesktopOwner();

        app.MapPost("/api/v1/knowledge/web-sources", (WebSourceRequest request, WebKnowledgeSources web) =>
        {
            if (request is null) return Results.BadRequest(new { error = "A request body is required." });
            try { return Results.Accepted(value: web.Begin(request.Url, request.Mode, request.MaxPages, request.MaxDepth)); }
            catch (OfflineBlockedException ex) { return Results.Json(new { error = ex.Message, code = "offline_blocked" }, statusCode: StatusCodes.Status403Forbidden); }
            catch (UnavailableDuringGamingException ex) { return GamingHardOff.Conflict(ex); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message, code = "invalid_address" }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Results.Problem("The web page folder could not be prepared."); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/knowledge/web-sources/{id}/resync", (string id, WebKnowledgeSources web) =>
        {
            try { return web.Resync(id) is { } info ? Results.Accepted(value: info) : Results.NotFound(new { error = "Web source not found." }); }
            catch (OfflineBlockedException ex) { return Results.Json(new { error = ex.Message, code = "offline_blocked" }, statusCode: StatusCodes.Status403Forbidden); }
            catch (UnavailableDuringGamingException ex) { return GamingHardOff.Conflict(ex); }
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/knowledge/web-sources/{id}", (string id, WebKnowledgeSources web, KnowledgeFolderWatcher watcher, WatchedFolderStore watched) =>
        {
            watcher.Unwatch(id);
            watched.Set(id, false);
            return web.Remove(id) ? Results.NoContent() : Results.NotFound(new { error = "Web source not found." });
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/knowledge/watched", (WatchedFolderStore watched) => Results.Ok(watched.Get().OrderBy(i => i, StringComparer.Ordinal))).RequireDesktopOwner();

        app.MapPost("/api/v1/knowledge/sources/{id}/watch", (string id, WatchFolderRequest request, RagStore rag,
            KnowledgeFolderWatcher watcher, WatchedFolderStore watched) =>
        {
            PaRuX.Contracts.RagSourceDto? source = rag.ListSources().FirstOrDefault(s => s.Id == id);
            if (source == null) return Results.NotFound(new { error = "RAG source not found" });
            if (!request.Enabled)
            {
                watcher.Unwatch(id);
                watched.Set(id, false);
                return Results.Ok(new { watching = false });
            }
            if (!watcher.Watch(id, source.FolderPath, request.ConsentBroad))
            {
                if (watcher.LastRefusal == SensitiveVerdict.NeedsConsent)
                    return Results.Json(new { error = "This folder is broad or holds application data. Confirm to keep it up to date.", code = "consent_required" },
                        statusCode: StatusCodes.Status409Conflict);
                return Results.Json(new { error = "This folder cannot be watched (missing, private application data or credentials).", code = "watch_refused" },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            watched.Set(id, true);
            return Results.Ok(new { watching = true });
        }).RequireDesktopOwner();
    }

    /// <summary>Re-creates the watchers the person turned on, once at start.</summary>
    public static void RestoreWatchers(RagStore rag, KnowledgeFolderWatcher watcher, WatchedFolderStore watched)
    {
        IReadOnlySet<string> ids = watched.Get();
        if (ids.Count == 0) return;
        foreach (PaRuX.Contracts.RagSourceDto source in rag.ListSources())
            if (ids.Contains(source.Id)) watcher.Watch(source.Id, source.FolderPath, consentBroad: true);   // consent was given when it was turned on
    }
}
