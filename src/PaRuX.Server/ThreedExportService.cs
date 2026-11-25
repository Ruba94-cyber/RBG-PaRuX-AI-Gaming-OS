using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Exports local GLB artifacts stored under generated/3d to OBJ, Binary STL, or USDZ.
/// Enforces strict path-traversal protection (allowed root containment + symlink refusal)
/// and delegates mesh parsing/serialization to pure-C# <see cref="GlbMeshConverter"/>.
/// </summary>
public sealed class ThreedExportService
{
    private readonly Func<string> _allowedRootProvider;

    public ThreedExportService()
        : this(() => Path.Combine(PaRuXDataRoot.Path, "generated", "3d"))
    {
    }

    internal ThreedExportService(string allowedRoot)
        : this(() => allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(allowedRoot))
            throw new ArgumentException("Allowed root directory is required.", nameof(allowedRoot));
    }

    private ThreedExportService(Func<string> allowedRootProvider)
    {
        _allowedRootProvider = allowedRootProvider;
    }

    public async Task<ThreedExportResponse> ExportAsync(ThreedExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        string format = NormalizeFormat(request.Format);
        string allowedRoot = Path.GetFullPath(_allowedRootProvider());
        string resolvedSource = ResolveValidatedGlbPath(request.SourcePath, allowedRoot);

        var fileInfo = new FileInfo(resolvedSource);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("The requested 3D source file does not exist.", resolvedSource);
        if (fileInfo.Length == 0)
            throw new InvalidDataException("The requested GLB file is empty.");
        if (fileInfo.Length > GlbMeshConverter.MaxInputFileBytes)
            throw new InvalidDataException($"GLB file exceeds maximum supported size of {GlbMeshConverter.MaxInputFileBytes:N0} bytes.");

        byte[] glbBytes = await File.ReadAllBytesAsync(resolvedSource, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        GlbMeshData mesh = GlbMeshConverter.ParseGlb(glbBytes);
        ct.ThrowIfCancellationRequested();

        (byte[] exportedBytes, string extension, string mediaType) = format switch
        {
            "obj" => (GlbMeshConverter.ExportObj(mesh), ".obj", "text/plain"),
            "stl" => (GlbMeshConverter.ExportStlBinary(mesh), ".stl", "model/stl"),
            "usdz" => (GlbMeshConverter.ExportUsdz(mesh), ".usdz", "model/vnd.usdz+zip"),
            _ => throw new InvalidDataException("Unsupported export format. Supported formats are: obj, stl, usdz.")
        };

        string baseName = SanitizeBaseName(request.OutputFileName, Path.GetFileNameWithoutExtension(resolvedSource));
        string outputFileName = baseName + extension;
        Directory.CreateDirectory(allowedRoot);
        string outputPath = Path.Combine(allowedRoot, outputFileName);
        EnsureStrictlyInsideRoot(outputPath, allowedRoot);
        EnsureNotReparsePoint(outputPath);

        await File.WriteAllBytesAsync(outputPath, exportedBytes, ct).ConfigureAwait(false);

        return new ThreedExportResponse
        {
            Format = format,
            FileName = outputFileName,
            MediaType = mediaType,
            OutputPath = outputPath,
            Base64Data = Convert.ToBase64String(exportedBytes),
            SizeBytes = exportedBytes.LongLength,
            VertexCount = mesh.VertexCount,
            TriangleCount = mesh.TriangleCount
        };
    }

    public static string NormalizeFormat(string? format)
    {
        string normalized = (format ?? string.Empty).Trim().ToLowerInvariant().TrimStart('.');
        if (normalized is "obj" or "stl" or "usdz") return normalized;
        throw new InvalidDataException("Unsupported export format. Supported formats are: obj, stl, usdz.");
    }

    internal static string ResolveValidatedGlbPath(string? sourcePath, string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new InvalidDataException("sourcePath is required.");
        if (sourcePath.IndexOf('\0') >= 0)
            throw new InvalidDataException("sourcePath contains invalid characters.");

        string trimmed = sourcePath.Trim();
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));

        // Allow either an absolute path inside generated/3d or a plain relative path/filename inside generated/3d.
        string candidate = Path.IsPathRooted(trimmed)
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(Path.Combine(fullRoot, trimmed));

        EnsureStrictlyInsideRoot(candidate, fullRoot);

        if (!string.Equals(Path.GetExtension(candidate), ".glb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .glb source files can be exported.");

        EnsureNoReparsePointsUnderRoot(candidate, fullRoot);
        return candidate;
    }

    private static void EnsureStrictlyInsideRoot(string fullPath, string fullRoot)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullRoot));
        string normalizedPath = Path.GetFullPath(fullPath);
        string rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!normalizedPath.StartsWith(rootWithSeparator, comparison))
            throw new InvalidDataException("Access denied: 3D export only allows artifacts inside the managed generated/3d directory.");
    }

    private static void EnsureNoReparsePointsUnderRoot(string fullPath, string fullRoot)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullRoot));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string? current = fullPath;
        while (!string.IsNullOrEmpty(current) && !string.Equals(Path.TrimEndingDirectorySeparator(current), normalizedRoot, comparison))
        {
            EnsureNotReparsePoint(current);
            current = Path.GetDirectoryName(current);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        FileAttributes attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Access denied: symbolic links and reparse points are not permitted for 3D export.");
    }

    private static string SanitizeBaseName(string? requested, string fallback)
    {
        string candidate = string.IsNullOrWhiteSpace(requested) ? fallback : Path.GetFileNameWithoutExtension(requested.Trim());
        if (string.IsNullOrWhiteSpace(candidate)) candidate = "model";

        var chars = new char[candidate.Length];
        int len = 0;
        foreach (char c in candidate)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            {
                chars[len++] = c;
            }
        }
        string cleaned = new string(chars, 0, len).Trim('.');
        if (cleaned.Length == 0 || cleaned.Contains("..", StringComparison.Ordinal))
            cleaned = "model_" + Guid.NewGuid().ToString("N")[..8];
        if (cleaned.Length > 80) cleaned = cleaned[..80];
        return cleaned;
    }
}
