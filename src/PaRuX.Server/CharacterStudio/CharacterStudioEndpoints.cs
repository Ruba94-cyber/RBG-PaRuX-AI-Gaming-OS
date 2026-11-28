using PaRuX.Contracts;

namespace PaRuX.Server.CharacterStudio;

/// <summary>Character Studio routes (/api/v2/characters). Desktop owner only: they write files, use the GPU and install software.</summary>
internal static class CharacterStudioEndpoints
{
    internal static void MapCharacterStudioEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v2/characters", (CharacterStudioService s) => Results.Ok(new CharacterListResponse { Characters = s.Store.List().ToList() })).RequireDesktopOwner();
        app.MapPost("/api/v2/characters", (CreateCharacterRequest r, CharacterStudioService s) => Guard(() => Results.Ok(s.Create(r)))).RequireDesktopOwner();
        app.MapGet("/api/v2/characters/{id}", (string id, CharacterStudioService s) => Guard(() => Results.Ok(s.Get(id)))).RequireDesktopOwner();
        app.MapPut("/api/v2/characters/{id}", (string id, UpdateCharacterRequest r, CharacterStudioService s) => Guard(() => Results.Ok(s.Update(id, r)))).RequireDesktopOwner();
        app.MapDelete("/api/v2/characters/{id}", (string id, CharacterStudioService s) => Guard(() => { s.Delete(id); return Results.NoContent(); })).RequireDesktopOwner();

        app.MapPost("/api/v2/characters/{id}/references/generate", (string id, GenerateReferencesRequest r, CharacterStudioService s) =>
            Guard(() => Results.Accepted(null, s.StartGenerateReferences(id, r.Count)))).RequireDesktopOwner();
        app.MapPost("/api/v2/characters/{id}/images", (string id, AddCharacterImageRequest r, CharacterStudioService s) => Guard(() =>
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(r.DataBase64 ?? string.Empty); }
            catch (FormatException) { throw new CharacterStudioException("bad_image", "The picture data is not valid.", "Choose the file again."); }
            return Results.Ok(s.AddUserImage(id, r.FileName, bytes));
        })).RequireDesktopOwner();
        app.MapDelete("/api/v2/characters/{id}/images/{imageId}", (string id, string imageId, CharacterStudioService s) =>
            Guard(() => Results.Ok(s.RemoveImage(id, imageId)))).RequireDesktopOwner();
        app.MapGet("/api/v2/characters/{id}/images/{file}", (string id, string file, CharacterStudioService s) => Guard(() =>
        {
            string? path = s.Store.ImagePath(CharacterStudioStore.SafeId(id), file);
            return path == null ? Results.NotFound() : Results.File(path, MediaType(path));
        })).RequireDesktopOwner();
        app.MapGet("/api/v2/characters/{id}/samples/{file}", (string id, string file, CharacterStudioService s) => Guard(() =>
        {
            string? path = s.SamplePath(CharacterStudioStore.SafeId(id), file);
            return path == null ? Results.NotFound() : Results.File(path, MediaType(path));
        })).RequireDesktopOwner();
        app.MapPost("/api/v2/characters/{id}/captions", async (string id, CharacterStudioService s, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await s.CaptionAsync(id, ct)))).RequireDesktopOwner();

        app.MapGet("/api/v2/characters/{id}/plan", (string id, string? preset, CharacterStudioService s) => Guard(() => Results.Ok(s.Plan(id, preset, null)))).RequireDesktopOwner();
        app.MapPost("/api/v2/characters/{id}/training", async (string id, StartCharacterTrainingRequest r, CharacterStudioService s, CancellationToken ct) =>
            await GuardAsync(async () => Results.Accepted(null, await s.StartTrainingAsync(id, r, resume: false, ct)))).RequireDesktopOwner();
        app.MapPost("/api/v2/characters/{id}/training/resume", async (string id, CharacterStudioService s, CancellationToken ct) =>
            await GuardAsync(async () => Results.Accepted(null, await s.StartTrainingAsync(id, new StartCharacterTrainingRequest(), resume: true, ct)))).RequireDesktopOwner();
        app.MapPost("/api/v2/characters/{id}/training/cancel", (string id, CharacterStudioService s) =>
            Guard(() => { s.Cancel(id); return Results.Ok(s.Get(id)); })).RequireDesktopOwner();

        app.MapPost("/api/v2/characters/{id}/binding", (string id, BindCharacterLoraRequest r, CharacterStudioService s) => Guard(() => Results.Ok(s.Bind(id, r)))).RequireDesktopOwner();
        app.MapDelete("/api/v2/characters/{id}/binding", (string id, CharacterStudioService s) => Guard(() => Results.Ok(s.Unbind(id)))).RequireDesktopOwner();

        // The portrait of the character attached to the active chat persona, for the chat avatar. 404 when there is none.
        app.MapGet("/api/v2/characters/active/portrait", (CharacterStudioService s) =>
        {
            string? path = s.ActivePortraitPath();
            return path == null ? Results.NotFound() : Results.File(path, MediaType(path));
        }).RequireDesktopOwner();

        app.MapGet("/api/v2/characters/trainer/status", (CharacterStudioService s) => Results.Ok(s.Trainer.GetStatus())).RequireDesktopOwner();
        app.MapPost("/api/v2/characters/trainer/install", async (CharacterStudioService s, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await s.Trainer.InstallAsync(null, ct)))).RequireDesktopOwner();
        app.MapPost("/api/v2/characters/trainer/locate", (LocateCharacterTrainerRequest r, CharacterStudioService s) =>
            Guard(() => Results.Ok(s.Trainer.Locate(r.TrainerFolder, r.PythonExe)))).RequireDesktopOwner();
        app.MapPost("/api/v2/characters/trainer/skip", (CharacterStudioService s) => Results.Ok(s.Trainer.Skip())).RequireDesktopOwner();
    }

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg"
    };

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception ex) when (Map(ex) is not null) { return Map(ex)!; }
    }

    private static IResult Guard(Func<IResult> action)
    {
        try { return action(); }
        catch (Exception ex) when (Map(ex) is not null) { return Map(ex)!; }
    }

    private static IResult? Map(Exception ex) => ex switch
    {
        UnavailableDuringGamingException g => GamingHardOff.Conflict(g),
        CharacterStudioException c => Results.Json(new { error = c.Message, code = c.Code, nextAction = c.NextAction }, statusCode: StatusCodes.Status409Conflict),
        TrainingRuntimeRefusalException t => Results.Json(new { error = t.Message, code = "trainer_unsupported", nextAction = "Character training needs an NVIDIA graphics card." }, statusCode: StatusCodes.Status409Conflict),
        InvalidDataException d => Results.BadRequest(new { error = d.Message, code = "invalid" }),
        InvalidOperationException i => Results.Json(new { error = i.Message, code = "trainer_failed", nextAction = "Try again; if it keeps failing, repair the training engine." }, statusCode: StatusCodes.Status409Conflict),
        _ => null
    };
}
