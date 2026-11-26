using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed record TrainingExportVerifyRequest(string Path);

public sealed record TrainingExportGgufRequest(string ModelDirectory, string? Outtype);

/// <summary>
/// Desktop-owner routes added by the training parity slice: export verification, importance-matrix generation,
/// document-to-dataset recipes and the local provider presets. All are DENY for browser sessions: they read or
/// write local files, start compute, or change where chat is sent.
/// </summary>
public static class TrainingToolsEndpoints
{
    public static void MapTrainingToolsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/training/export/verify", (TrainingExportVerifyRequest request) =>
            request is null || string.IsNullOrWhiteSpace(request.Path)
                ? Results.BadRequest(new { error = "A path is required." })
                : Results.Ok(TrainingExportVerifier.Verify(request.Path))).RequireDesktopOwner();

        // A full 16-bit fine-tune ends as a complete HF model folder, not an adapter, so the adapter
        // convert/merge routes cannot reach it. This turns such a folder (or any merged HF model) into one
        // standalone GGUF next to it, through the same managed conversion venv and vendored llama.cpp script.
        app.MapPost("/api/v1/training/export/gguf", async (
            TrainingExportGgufRequest request, LoraGgufConversionService conversion, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ModelDirectory)
                || !Path.IsPathFullyQualified(request.ModelDirectory) || request.ModelDirectory.StartsWith(@"\\", StringComparison.Ordinal))
                return Results.BadRequest(new { error = "An absolute local model folder is required." });
            string outtype = string.IsNullOrWhiteSpace(request.Outtype) ? "f16" : request.Outtype.Trim().ToLowerInvariant();
            string folder = Path.GetFullPath(request.ModelDirectory);
            string output = Path.Combine(folder, LoraGgufConversionService.MergedGgufFileName(folder, outtype));
            try
            {
                string log = await conversion.ConvertModelAsync(folder, output, outtype, ct);
                return Results.Ok(new { ggufPath = output, verification = TrainingExportVerifier.Verify(output), log = log.Length <= 4_000 ? log : log[^4_000..] });
            }
            catch (UnavailableDuringGamingException gaming) { return GamingHardOff.Conflict(gaming); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/lifecycle/imatrix", async (
            GgufImatrixRequest request, GgufQuantizeService service, LocalRuntimeService runtime, CancellationToken ct) =>
        {
            if (runtime.GamingMode) return Results.Conflict(new { error = "Leave Gaming before generating an importance matrix." });
            if (!service.CanGenerateImatrix)
                return Results.Json(new
                {
                    error = "llama-imatrix is not part of the installed llama.cpp runtime, so PaRuX cannot generate an importance matrix yet. "
                        + "You can still pass an existing .imatrix file to quantization.",
                    status = "PARTIAL"
                }, statusCode: StatusCodes.Status501NotImplemented);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.GamingCancellationToken);
            try { return Results.Ok(await service.GenerateImatrixAsync(request, linked.Token)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (OperationCanceledException) { return Results.Conflict(new { error = "Gaming interrupted the importance-matrix run." }); }
            catch (GgufQuantizeException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/datasets/document-recipe", async (
            TrainingDocumentRecipeRequest request, LocalRuntimeService runtime, CancellationToken ct) =>
        {
            if (CorporateMode.IsActive)
                return Results.Conflict(new { error = "Training recipes are not available in Corporate Lite." });
            if (runtime.GamingMode) return Results.Conflict(new { error = "Dataset recipes are disabled during Gaming." });
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.GamingCancellationToken);
            try { return Results.Ok(await TrainingDocumentRecipe.BuildAsync(request, null, linked.Token)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (OperationCanceledException) { return Results.Conflict(new { error = "Gaming interrupted the data recipe." }); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FileNotFoundException)
            { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Results.Problem("The local training dataset recipe could not be written."); }
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/ai/openai-compatible/presets", () => Results.Ok(OpenAiCompatibleProviderPresets.All))
            .RequireDesktopOwner();
    }
}
