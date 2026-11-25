namespace PaRuX.Contracts;

// ---- 3D Studio: image-to-3D (and text->image->3D) via a managed Python venv
// ---- outside the repo (%LOCALAPPDATA%\PaRuX\ai-runtimes\3d-studio\), the same
// ---- arm's-length pattern as Speech/Training Lab. Unlike those two, this engine
// ---- needs custom CUDA extensions (kaolin, spconv, nvdiffrast) pinned to specific
// ---- torch/CUDA/Python combinations - a real, disclosed reliability risk this
// ---- service is honest about via ThreedEngineStatus rather than assuming success. ----

public sealed class ThreedEngineStatus
{
    public bool Installed { get; set; }
    public string? TorchVersion { get; set; }
    public bool CudaVerified { get; set; }
    /// <summary>Set when a prior install attempt failed, so the UI can show the real reason
    /// instead of a generic "not installed" - these pipelines are known to be dependency-fragile.</summary>
    public string? LastError { get; set; }
}

public sealed class ThreedGenerateRequest
{
    /// <summary>Base64-encoded source image (PNG/JPEG). Required - text prompts go through
    /// Image Generation first (text -> image -> 3D), matching spec section 70's two modes.</summary>
    public string SourceImageBase64 { get; set; } = string.Empty;
    public string FileName { get; set; } = "source.png";
    public int Seed { get; set; } = -1;
}

public sealed class ThreedGenerateResponse
{
    public string FileName { get; set; } = string.Empty;
    /// <summary>"model/gltf-binary" for .glb.</summary>
    public string MediaType { get; set; } = "model/gltf-binary";
    public string Base64Data { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    /// <summary>Managed path under generated/3d when persisted locally.</summary>
    public string? SavedPath { get; set; }
}

public sealed class ThreedTextTo3dRequest
{
    public string Prompt { get; set; } = string.Empty;
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public int Seed { get; set; } = -1;
}

public sealed class ThreedTextTo3dResponse
{
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = "model/gltf-binary";
    public string Base64Data { get; set; } = string.Empty;
    public string SavedPath { get; set; } = string.Empty;
    public string ReferenceImageFileName { get; set; } = string.Empty;
    public double ImageDurationSeconds { get; set; }
    public double MeshDurationSeconds { get; set; }
    public double TotalDurationSeconds { get; set; }
}

public sealed class ThreedExportRequest
{
    /// <summary>Path to an existing .glb artifact inside generated/3d.</summary>
    public string SourcePath { get; set; } = string.Empty;
    /// <summary>Target format: "obj", "stl", or "usdz".</summary>
    public string Format { get; set; } = string.Empty;
    /// <summary>Optional base file name override (sanitized, without directory segments).</summary>
    public string? OutputFileName { get; set; }
}

public sealed class ThreedExportResponse
{
    public string Format { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string Base64Data { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int VertexCount { get; set; }
    public int TriangleCount { get; set; }
}
