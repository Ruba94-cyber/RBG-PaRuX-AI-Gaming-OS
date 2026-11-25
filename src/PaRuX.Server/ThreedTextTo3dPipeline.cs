using System.Diagnostics;
using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed record ThreedPipelineProgress(string Stage, int Percent, string Message);

/// <summary>
/// Clean, cancellable two-stage local Text-to-3D pipeline:
/// Stage 1: prompt -> local reference image (via MediaGenerationService / ComfyUI)
/// Stage 2: reference image -> local 3D GLB mesh (via ThreedService)
/// Saves the resulting .glb inside generated/3d, respects Gaming hard-off at every boundary,
/// and reports honest failures when either the image engine or the 3D engine is unavailable.
/// </summary>
public sealed class ThreedTextTo3dPipeline
{
    private readonly Func<CancellationToken, Task<ThreedEngineStatus>> _getThreedStatus;
    private readonly Func<MediaJobRequest, CancellationToken, Task<MediaAsset>> _generateImage;
    private readonly Func<ThreedGenerateRequest, CancellationToken, Task<ThreedGenerateResponse>> _generateMesh;
    private readonly Func<bool> _isGamingActive;
    private readonly Func<string> _outputDirectoryProvider;

    public ThreedTextTo3dPipeline(
        MediaGenerationService mediaGeneration,
        ThreedService threedService,
        LocalRuntimeService runtime)
        : this(
            threedService.GetStatusAsync,
            (req, ct) => mediaGeneration.GenerateAsync(req, null, ct),
            threedService.GenerateAsync,
            () => runtime.GamingMode,
            () => Path.Combine(PaRuXDataRoot.Path, "generated", "3d"))
    {
    }

    internal ThreedTextTo3dPipeline(
        Func<CancellationToken, Task<ThreedEngineStatus>> getThreedStatus,
        Func<MediaJobRequest, CancellationToken, Task<MediaAsset>> generateImage,
        Func<ThreedGenerateRequest, CancellationToken, Task<ThreedGenerateResponse>> generateMesh,
        Func<bool> isGamingActive,
        string outputDirectory)
        : this(getThreedStatus, generateImage, generateMesh, isGamingActive, () => outputDirectory)
    {
    }

    private ThreedTextTo3dPipeline(
        Func<CancellationToken, Task<ThreedEngineStatus>> getThreedStatus,
        Func<MediaJobRequest, CancellationToken, Task<MediaAsset>> generateImage,
        Func<ThreedGenerateRequest, CancellationToken, Task<ThreedGenerateResponse>> generateMesh,
        Func<bool> isGamingActive,
        Func<string> outputDirectoryProvider)
    {
        _getThreedStatus = getThreedStatus;
        _generateImage = generateImage;
        _generateMesh = generateMesh;
        _isGamingActive = isGamingActive;
        _outputDirectoryProvider = outputDirectoryProvider;
    }

    public async Task<ThreedTextTo3dResponse> GenerateAsync(
        ThreedTextTo3dRequest request,
        IProgress<ThreedPipelineProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string prompt = (request.Prompt ?? string.Empty).Trim();
        if (prompt.Length == 0)
            throw new InvalidDataException("A text prompt is required for Text-to-3D generation.");

        EnsureNotGaming();
        ct.ThrowIfCancellationRequested();

        // Fail fast before running image generation if the local 3D engine is not installed.
        progress?.Report(new ThreedPipelineProgress("checking_3d_engine", 5, "Checking local 3D engine availability…"));
        ThreedEngineStatus threedStatus = await _getThreedStatus(ct).ConfigureAwait(false);
        if (!threedStatus.Installed)
        {
            string reason = string.IsNullOrWhiteSpace(threedStatus.LastError)
                ? "The local 3D Studio engine is not installed yet. Install it in Create / Settings before running Text-to-3D."
                : "The local 3D Studio engine is not installed: " + threedStatus.LastError;
            throw new InvalidOperationException(reason);
        }

        EnsureNotGaming();
        ct.ThrowIfCancellationRequested();

        var totalSw = Stopwatch.StartNew();

        // Stage 1: local reference image generation from text prompt
        progress?.Report(new ThreedPipelineProgress("generating_reference_image", 15, "Stage 1/2: Generating local reference image…"));
        var stage1Sw = Stopwatch.StartNew();
        MediaAsset referenceImage;
        try
        {
            var imageRequest = new MediaJobRequest
            {
                Type = "image",
                Prompt = prompt,
                Width = request.Width > 0 ? Math.Clamp(request.Width, 256, 2048) : 1024,
                Height = request.Height > 0 ? Math.Clamp(request.Height, 256, 2048) : 1024,
                Seed = request.Seed
            };
            referenceImage = await _generateImage(imageRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnavailableDuringGamingException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Text-to-3D Stage 1 (local image generation) failed: " + ex.Message, ex);
        }
        stage1Sw.Stop();

        if (referenceImage == null || string.IsNullOrWhiteSpace(referenceImage.Base64Data))
            throw new InvalidOperationException("Text-to-3D Stage 1 produced an empty reference image.");

        EnsureNotGaming();
        ct.ThrowIfCancellationRequested();

        // Stage 2: local image-to-3D mesh generation
        progress?.Report(new ThreedPipelineProgress("generating_3d_mesh", 60, "Stage 2/2: Generating local 3D GLB mesh from reference image…"));
        var stage2Sw = Stopwatch.StartNew();
        ThreedGenerateResponse meshResult;
        try
        {
            meshResult = await _generateMesh(new ThreedGenerateRequest
            {
                SourceImageBase64 = referenceImage.Base64Data,
                FileName = string.IsNullOrWhiteSpace(referenceImage.FileName) ? "reference.png" : referenceImage.FileName,
                Seed = request.Seed
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnavailableDuringGamingException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Text-to-3D Stage 2 (local 3D mesh generation) failed: " + ex.Message, ex);
        }
        stage2Sw.Stop();

        if (meshResult == null || string.IsNullOrWhiteSpace(meshResult.Base64Data))
            throw new InvalidOperationException("Text-to-3D Stage 2 produced an empty 3D model.");

        byte[] glbBytes;
        try { glbBytes = Convert.FromBase64String(meshResult.Base64Data); }
        catch (FormatException) { throw new InvalidDataException("3D engine returned invalid Base64 GLB payload."); }
        if (glbBytes.Length == 0)
            throw new InvalidOperationException("3D engine returned an empty GLB payload.");

        string outputDir = Path.GetFullPath(_outputDirectoryProvider());
        Directory.CreateDirectory(outputDir);
        string fileName = "text3d_" + Guid.NewGuid().ToString("N")[..8] + ".glb";
        string savedPath = Path.Combine(outputDir, fileName);
        await File.WriteAllBytesAsync(savedPath, glbBytes, ct).ConfigureAwait(false);

        totalSw.Stop();
        progress?.Report(new ThreedPipelineProgress("completed", 100, "Text-to-3D generation completed."));

        return new ThreedTextTo3dResponse
        {
            FileName = fileName,
            MediaType = "model/gltf-binary",
            Base64Data = meshResult.Base64Data,
            SavedPath = savedPath,
            ReferenceImageFileName = string.IsNullOrWhiteSpace(referenceImage.FileName) ? "reference.png" : referenceImage.FileName,
            ImageDurationSeconds = stage1Sw.Elapsed.TotalSeconds,
            MeshDurationSeconds = meshResult.DurationSeconds > 0 ? meshResult.DurationSeconds : stage2Sw.Elapsed.TotalSeconds,
            TotalDurationSeconds = totalSw.Elapsed.TotalSeconds
        };
    }

    private void EnsureNotGaming()
    {
        if (_isGamingActive())
            throw new UnavailableDuringGamingException("Gaming Mode is active, so Text-to-3D generation is unavailable.");
    }
}
