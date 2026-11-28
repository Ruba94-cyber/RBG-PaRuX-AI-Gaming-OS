using PaRuX.Contracts;
using PaRuX.Server.Knowledge;

namespace PaRuX.Server;

/// <summary>
/// Two narrow companion routes for paired phones: a path-free text-runtime status (with an explicit
/// "load the model now" action) and a document inbox that lets the phone add a file to the PC's local
/// knowledge index. Neither route exposes workstation paths, and the inbox accepts only a bounded
/// allow-list of document types written under a Core-owned folder, never a caller-chosen location.
/// </summary>
public static class MobileRuntimeDocumentEndpoints
{
    public const string InboxFolderName = "phone-documents";
    public const int MaxDocumentBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log", ".html",
        ".pdf", ".docx", ".pptx", ".xlsx"
    };

    public static void MapMobileRuntimeDocumentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v2/mobile/runtime", async (RuntimeManagerService runtimeManager, CancellationToken ct) =>
            Results.Ok(ProjectRuntime(await runtimeManager.GetStatusAsync(ct))));

        // Loads the PC's current text model so the first chat turn is fast. It is the same readiness
        // path a chat request takes, so it honours Gaming Mode, GPU admission and the model the PC picked.
        app.MapPost("/api/v2/mobile/runtime/load", async (
            LocalRuntimeService runtime, RuntimeManagerService runtimeManager, CancellationToken ct) =>
        {
            if (runtime.GamingMode)
                return Results.Json(new { error = "gaming_active" }, statusCode: StatusCodes.Status409Conflict);
            try
            {
                await runtime.EnsureReadyAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Mobile model load failed: " + ex.Message);
                return Results.Json(new { error = "load_failed" }, statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Ok(ProjectRuntime(await runtimeManager.GetStatusAsync(ct)));
        });

        app.MapPost("/api/v2/mobile/documents", async (
            MobileDocumentUploadRequest request, RagStore rag, KnowledgeJobService knowledge, CancellationToken ct) =>
        {
            if (!TryValidateUpload(request, out string safeName, out byte[] bytes, out string error))
                return Results.BadRequest(new { error });

            string inbox = Path.Combine(PaRuXDataRoot.Path, InboxFolderName);
            Directory.CreateDirectory(inbox);
            string target = UniquePath(inbox, safeName);
            await File.WriteAllBytesAsync(target, bytes, ct);

            string inboxFull = Path.GetFullPath(inbox).TrimEnd('\\', '/');
            RagSourceDto source = rag.ListSources().FirstOrDefault(s =>
                string.Equals(Path.GetFullPath(s.FolderPath).TrimEnd('\\', '/'), inboxFull, StringComparison.OrdinalIgnoreCase))
                ?? rag.AddSource(inbox);
            bool started = knowledge.Start(source.Id);
            return Results.Ok(new { fileName = Path.GetFileName(target), sourceId = source.Id, indexingStarted = started });
        });
    }

    internal static bool TryValidateUpload(
        MobileDocumentUploadRequest? request, out string safeName, out byte[] bytes, out string error)
    {
        safeName = string.Empty;
        bytes = Array.Empty<byte>();
        error = string.Empty;
        if (request is null || string.IsNullOrWhiteSpace(request.FileName) || string.IsNullOrEmpty(request.ContentBase64))
        { error = "fileName and contentBase64 are required."; return false; }
        // Base64 inflates by 4/3; reject before decoding so an oversized body never allocates.
        if (request.ContentBase64.Length > (long)MaxDocumentBytes * 4 / 3 + 8)
        { error = "The document is larger than 8 MB."; return false; }

        string name = request.FileName.Replace('\\', '/').Split('/').Last().Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimStart('.');
        string extension = Path.GetExtension(name);
        if (name.Length is 0 or > 120 || !AllowedExtensions.Contains(extension))
        { error = "That file type cannot be added. Use text, Markdown, CSV, JSON, PDF, Word, PowerPoint or Excel files."; return false; }

        try { bytes = Convert.FromBase64String(request.ContentBase64); }
        catch (FormatException) { error = "contentBase64 is not valid base64."; return false; }
        if (bytes.Length is 0 or > MaxDocumentBytes)
        { error = bytes.Length == 0 ? "The document is empty." : "The document is larger than 8 MB."; return false; }
        safeName = name;
        return true;
    }

    private static string UniquePath(string folder, string fileName)
    {
        string candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate)) return candidate;
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    /// <summary>The phone-safe view of the text runtime: model file NAME only, never a path.</summary>
    internal static MobileRuntimeStatus ProjectRuntime(RuntimeManagerStatus status)
    {
        bool loaded = status.Backends.Any(b => b.Backend.StartsWith("text-", StringComparison.OrdinalIgnoreCase)
            && b.Running && b.Healthy);
        string? modelName = string.IsNullOrWhiteSpace(status.ActiveModelPath)
            ? null
            : status.ActiveModelPath.Replace('\\', '/').Split('/').Last();
        return new MobileRuntimeStatus
        {
            TextLoaded = loaded,
            ActiveRole = status.ActiveRole,
            ActiveModelName = modelName,
            HasVision = status.ActiveHasVisionSupport,
            GamingActive = status.GamingActive,
            TextUnloadedForGaming = status.TextUnloadedForGaming,
            GpuDetected = status.GpuDetected,
            VramTotalMib = status.VramTotalMib,
            VramFreeMib = status.VramFreeMib,
            RamTotalBytes = status.RamTotalBytes,
            RamAvailableBytes = status.RamAvailableBytes
        };
    }

    public sealed class MobileDocumentUploadRequest
    {
        public string FileName { get; set; } = string.Empty;
        public string ContentBase64 { get; set; } = string.Empty;
    }

    public sealed class MobileRuntimeStatus
    {
        public bool TextLoaded { get; set; }
        public string? ActiveRole { get; set; }
        public string? ActiveModelName { get; set; }
        public bool HasVision { get; set; }
        public bool GamingActive { get; set; }
        public bool TextUnloadedForGaming { get; set; }
        public bool GpuDetected { get; set; }
        public int VramTotalMib { get; set; }
        public int? VramFreeMib { get; set; }
        public long RamTotalBytes { get; set; }
        public long? RamAvailableBytes { get; set; }
    }
}
