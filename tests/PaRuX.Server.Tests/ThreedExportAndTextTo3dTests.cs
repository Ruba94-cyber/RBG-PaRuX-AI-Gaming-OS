using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ThreedExportAndTextTo3dTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _threedDir;

    public ThreedExportAndTextTo3dTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "parux-3d-tests-" + Guid.NewGuid().ToString("N"));
        _threedDir = Path.Combine(_tempRoot, "generated", "3d");
        Directory.CreateDirectory(_threedDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ---- Minimal valid GLB builder for deterministic unit tests ----

    private static byte[] BuildCubeGlb(bool includeNormals = true, bool useUint32Indices = false)
    {
        Vector3[] positions =
        {
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
            new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)
        };
        int[] indices =
        {
            0, 2, 1, 0, 3, 2, // back
            4, 5, 6, 4, 6, 7, // front
            0, 1, 5, 0, 5, 4, // bottom
            2, 3, 7, 2, 7, 6, // top
            0, 4, 7, 0, 7, 3, // left
            1, 2, 6, 1, 6, 5  // right
        };
        Vector3[]? normals = includeNormals
            ? positions.Select(p => Vector3.Normalize(p - new Vector3(0.5f, 0.5f, 0.5f))).ToArray()
            : null;
        return BuildMinimalGlb(positions, indices, normals, useUint32Indices);
    }

    private static byte[] BuildMinimalGlb(
        Vector3[] positions,
        int[] indices,
        Vector3[]? normals = null,
        bool useUint32Indices = false)
    {
        using var binMs = new MemoryStream();
        int posOffset = 0;
        foreach (Vector3 p in positions)
        {
            Span<byte> buf = stackalloc byte[12];
            BinaryPrimitives.WriteSingleLittleEndian(buf[0..4], p.X);
            BinaryPrimitives.WriteSingleLittleEndian(buf[4..8], p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(buf[8..12], p.Z);
            binMs.Write(buf);
        }
        int posLength = (int)binMs.Position - posOffset;

        int normOffset = -1;
        int normLength = 0;
        if (normals != null)
        {
            normOffset = (int)binMs.Position;
            foreach (Vector3 n in normals)
            {
                Span<byte> buf = stackalloc byte[12];
                BinaryPrimitives.WriteSingleLittleEndian(buf[0..4], n.X);
                BinaryPrimitives.WriteSingleLittleEndian(buf[4..8], n.Y);
                BinaryPrimitives.WriteSingleLittleEndian(buf[8..12], n.Z);
                binMs.Write(buf);
            }
            normLength = (int)binMs.Position - normOffset;
        }

        int idxOffset = (int)binMs.Position;
        foreach (int idx in indices)
        {
            if (useUint32Indices)
            {
                Span<byte> buf = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)idx);
                binMs.Write(buf);
            }
            else
            {
                Span<byte> buf = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)idx);
                binMs.Write(buf);
            }
        }
        int idxLength = (int)binMs.Position - idxOffset;
        while (binMs.Position % 4 != 0) binMs.WriteByte(0);
        byte[] binChunk = binMs.ToArray();

        var bufferViews = new List<object>
        {
            new { buffer = 0, byteOffset = posOffset, byteLength = posLength }
        };
        var accessors = new List<object>
        {
            new { bufferView = 0, componentType = 5126, count = positions.Length, type = "VEC3" }
        };
        var attributes = new Dictionary<string, int> { ["POSITION"] = 0 };

        if (normals != null)
        {
            int bvIdx = bufferViews.Count;
            int accIdx = accessors.Count;
            bufferViews.Add(new { buffer = 0, byteOffset = normOffset, byteLength = normLength });
            accessors.Add(new { bufferView = bvIdx, componentType = 5126, count = normals.Length, type = "VEC3" });
            attributes["NORMAL"] = accIdx;
        }

        int idxBvIdx = bufferViews.Count;
        int idxAccIdx = accessors.Count;
        bufferViews.Add(new { buffer = 0, byteOffset = idxOffset, byteLength = idxLength });
        accessors.Add(new
        {
            bufferView = idxBvIdx,
            componentType = useUint32Indices ? 5125 : 5123,
            count = indices.Length,
            type = "SCALAR"
        });

        var gltf = new
        {
            asset = new { version = "2.0" },
            scene = 0,
            scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[] { new { mesh = 0 } },
            meshes = new[]
            {
                new
                {
                    primitives = new[]
                    {
                        new { attributes, indices = idxAccIdx, mode = 4 }
                    }
                }
            },
            buffers = new[] { new { byteLength = binChunk.Length } },
            bufferViews,
            accessors
        };

        byte[] rawJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(gltf));
        int paddedJsonLen = (rawJson.Length + 3) & ~3;
        byte[] jsonChunk = new byte[paddedJsonLen];
        Buffer.BlockCopy(rawJson, 0, jsonChunk, 0, rawJson.Length);
        for (int i = rawJson.Length; i < paddedJsonLen; i++) jsonChunk[i] = 0x20; // space padding per glTF 2.0 spec

        int totalLength = 12 + 8 + jsonChunk.Length + 8 + binChunk.Length;
        byte[] glb = new byte[totalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0, 4), GlbMeshConverter.GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), GlbMeshConverter.GlbVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)totalLength);

        int o = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(o, 4), (uint)jsonChunk.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(o + 4, 4), GlbMeshConverter.ChunkTypeJson);
        Buffer.BlockCopy(jsonChunk, 0, glb, o + 8, jsonChunk.Length);
        o += 8 + jsonChunk.Length;

        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(o, 4), (uint)binChunk.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(o + 4, 4), GlbMeshConverter.ChunkTypeBin);
        Buffer.BlockCopy(binChunk, 0, glb, o + 8, binChunk.Length);

        return glb;
    }

    // ---- 1. GLB -> OBJ / STL / USDZ round-trip tests ----

    [Fact]
    public void Glb_to_Obj_exports_vertices_normals_and_faces_deterministically()
    {
        byte[] glb = BuildCubeGlb(includeNormals: true);
        GlbMeshData mesh = GlbMeshConverter.ParseGlb(glb);
        Assert.Equal(8, mesh.VertexCount);
        Assert.Equal(12, mesh.TriangleCount);

        byte[] obj1 = GlbMeshConverter.ExportObj(mesh);
        byte[] obj2 = GlbMeshConverter.ExportObj(mesh);
        Assert.Equal(obj1, obj2);

        string text = Encoding.UTF8.GetString(obj1);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("o model", lines);
        Assert.Equal(8, lines.Count(l => l.StartsWith("v ", StringComparison.Ordinal)));
        Assert.Equal(8, lines.Count(l => l.StartsWith("vn ", StringComparison.Ordinal)));
        Assert.Equal(12, lines.Count(l => l.StartsWith("f ", StringComparison.Ordinal)));
        Assert.Contains("f 1//1 3//3 2//2", lines);
    }

    [Fact]
    public void Glb_to_Binary_Stl_has_valid_header_triangle_count_and_computed_normals()
    {
        byte[] glb = BuildCubeGlb(includeNormals: false, useUint32Indices: true);
        GlbMeshData mesh = GlbMeshConverter.ParseGlb(glb);
        byte[] stl = GlbMeshConverter.ExportStlBinary(mesh);

        // 80-byte header + 4-byte count + 12 triangles * 50 bytes
        Assert.Equal(80 + 4 + 12 * 50, stl.Length);
        string header = Encoding.ASCII.GetString(stl, 0, 80).TrimEnd('\0');
        Assert.Contains("PaRuX", header);

        uint triCount = BinaryPrimitives.ReadUInt32LittleEndian(stl.AsSpan(80, 4));
        Assert.Equal(12u, triCount);

        // Inspect every triangle record
        for (int t = 0; t < 12; t++)
        {
            int off = 84 + t * 50;
            var normal = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(off, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(off + 4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(off + 8, 4)));
            var v0 = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(off + 12, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(off + 16, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(off + 20, 4)));
            var v1 = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(off + 24, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(off + 28, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(off + 32, 4)));
            var v2 = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(off + 36, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(off + 40, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(off + 44, 4)));
            ushort attr = BinaryPrimitives.ReadUInt16LittleEndian(stl.AsSpan(off + 48, 2));

            Assert.Equal((ushort)0, attr);
            Vector3 expectedNormal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
            Assert.InRange(Vector3.Distance(normal, expectedNormal), 0f, 1e-5f);
        }
    }

    [Fact]
    public void Glb_to_Usdz_produces_uncompressed_64byte_aligned_zip_with_valid_model_usda()
    {
        byte[] glb = BuildCubeGlb(includeNormals: true);
        GlbMeshData mesh = GlbMeshConverter.ParseGlb(glb);
        byte[] usdz = GlbMeshConverter.ExportUsdz(mesh);

        // Verify local file header: signature, STORE compression (0), and 64-byte payload alignment
        Assert.Equal(0x04034B50u, BinaryPrimitives.ReadUInt32LittleEndian(usdz.AsSpan(0, 4)));
        ushort compressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(usdz.AsSpan(8, 2));
        Assert.Equal((ushort)0, compressionMethod);

        ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(usdz.AsSpan(26, 2));
        ushort extraLen = BinaryPrimitives.ReadUInt16LittleEndian(usdz.AsSpan(28, 2));
        int payloadOffset = 30 + nameLen + extraLen;
        Assert.Equal(0, payloadOffset % 64);

        // Open with standard ZipArchive and verify model.usda contents
        using var ms = new MemoryStream(usdz, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        ZipArchiveEntry entry = Assert.Single(zip.Entries);
        Assert.Equal("model.usda", entry.FullName);
        Assert.Equal(entry.Length, entry.CompressedLength);

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        string usda = reader.ReadToEnd();
        Assert.StartsWith("#usda 1.0", usda);
        Assert.Contains("defaultPrim = \"Model\"", usda);
        Assert.Contains("def Xform \"Model\"", usda);
        Assert.Contains("def Mesh \"Mesh\"", usda);
        Assert.Contains("int[] faceVertexCounts = [3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3]", usda);
        Assert.Contains("int[] faceVertexIndices = [0, 2, 1,", usda);
        Assert.Contains("point3f[] points = [(0, 0, 0),", usda);
        Assert.Contains("normal3f[] normals = [", usda);
        Assert.Contains("uniform token subdivisionScheme = \"none\"", usda);
    }

    // ---- 2. Malformed / truncated / unsafe GLB rejection tests ----

    [Fact]
    public void Malformed_or_truncated_glb_is_rejected()
    {
        // Too small
        Assert.Throws<InvalidDataException>(() => GlbMeshConverter.ParseGlb(new byte[8]));

        // Bad magic
        byte[] badMagic = BuildCubeGlb();
        badMagic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => GlbMeshConverter.ParseGlb(badMagic));

        // Unsupported version
        byte[] badVersion = BuildCubeGlb();
        BinaryPrimitives.WriteUInt32LittleEndian(badVersion.AsSpan(4, 4), 1);
        Assert.Throws<InvalidDataException>(() => GlbMeshConverter.ParseGlb(badVersion));

        // Truncated file
        byte[] truncated = BuildCubeGlb()[..40];
        Assert.Throws<InvalidDataException>(() => GlbMeshConverter.ParseGlb(truncated));

        // Out-of-range vertex index
        byte[] badIndex = BuildMinimalGlb(
            new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) },
            new[] { 0, 1, 99 });
        Assert.Throws<InvalidDataException>(() => GlbMeshConverter.ParseGlb(badIndex));

        // Non-finite vertex position
        byte[] nanVertex = BuildMinimalGlb(
            new[] { new Vector3(float.NaN, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) },
            new[] { 0, 1, 2 });
        Assert.Throws<InvalidDataException>(() => GlbMeshConverter.ParseGlb(nanVertex));
    }

    [Fact]
    public void External_buffer_uri_in_glb_is_rejected()
    {
        string json = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":36,\"uri\":\"../secret.bin\"}],\"bufferViews\":[],\"accessors\":[],\"meshes\":[{\"primitives\":[]}]}";
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int paddedLen = (jsonBytes.Length + 3) & ~3;
        byte[] glb = new byte[12 + 8 + paddedLen];
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0, 4), GlbMeshConverter.GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), GlbMeshConverter.GlbVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)glb.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12, 4), (uint)paddedLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16, 4), GlbMeshConverter.ChunkTypeJson);
        Buffer.BlockCopy(jsonBytes, 0, glb, 20, jsonBytes.Length);
        for (int i = jsonBytes.Length; i < paddedLen; i++) glb[20 + i] = 0x20;

        Assert.Throws<InvalidDataException>(() => GlbMeshConverter.ParseGlb(glb));
    }

    // ---- 3. ThreedExportService path-traversal & export tests ----

    [Fact]
    public async Task Export_service_exports_all_three_formats_and_blocks_path_traversal()
    {
        var service = new ThreedExportService(_threedDir);
        string glbPath = Path.Combine(_threedDir, "cube.glb");
        await File.WriteAllBytesAsync(glbPath, BuildCubeGlb());

        foreach (string fmt in new[] { "obj", "stl", "usdz" })
        {
            ThreedExportResponse resp = await service.ExportAsync(new ThreedExportRequest
            {
                SourcePath = glbPath,
                Format = fmt
            }, CancellationToken.None);

            Assert.Equal(fmt, resp.Format);
            Assert.Equal(8, resp.VertexCount);
            Assert.Equal(12, resp.TriangleCount);
            Assert.True(File.Exists(resp.OutputPath));
            Assert.StartsWith(_threedDir, resp.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("." + fmt, Path.GetExtension(resp.OutputPath));
            Assert.True(resp.SizeBytes > 0);
        }

        // Relative path traversal
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExportAsync(new ThreedExportRequest
        {
            SourcePath = Path.Combine("..", "outside.glb"),
            Format = "obj"
        }, CancellationToken.None));

        // Absolute path outside generated/3d
        string outsideGlb = Path.Combine(_tempRoot, "outside.glb");
        await File.WriteAllBytesAsync(outsideGlb, BuildCubeGlb());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExportAsync(new ThreedExportRequest
        {
            SourcePath = outsideGlb,
            Format = "stl"
        }, CancellationToken.None));

        // Sibling prefix-spoof directory (generated/3d-evil/cube.glb)
        string spoofDir = Path.Combine(_tempRoot, "generated", "3d-evil");
        Directory.CreateDirectory(spoofDir);
        string spoofGlb = Path.Combine(spoofDir, "cube.glb");
        await File.WriteAllBytesAsync(spoofGlb, BuildCubeGlb());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExportAsync(new ThreedExportRequest
        {
            SourcePath = spoofGlb,
            Format = "usdz"
        }, CancellationToken.None));

        // Non-GLB extension
        string fakePng = Path.Combine(_threedDir, "not-a-model.png");
        await File.WriteAllBytesAsync(fakePng, new byte[] { 1, 2, 3, 4 });
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExportAsync(new ThreedExportRequest
        {
            SourcePath = fakePng,
            Format = "obj"
        }, CancellationToken.None));

        // Unsupported format
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExportAsync(new ThreedExportRequest
        {
            SourcePath = glbPath,
            Format = "fbx"
        }, CancellationToken.None));
    }

    // ---- 4. Text-to-3D two-stage pipeline tests with stubs/fakes ----

    [Fact]
    public async Task Text_to_3d_pipeline_runs_both_stages_reports_progress_and_saves_glb()
    {
        byte[] fakePng = { 0x89, 0x50, 0x4E, 0x47 };
        byte[] validGlb = BuildCubeGlb();
        string? capturedPrompt = null;
        string? capturedImageB64 = null;
        var stages = new List<string>();

        var pipeline = new ThreedTextTo3dPipeline(
            getThreedStatus: _ => Task.FromResult(new ThreedEngineStatus { Installed = true, CudaVerified = true }),
            generateImage: (req, _) =>
            {
                capturedPrompt = req.Prompt;
                return Task.FromResult(new MediaAsset
                {
                    FileName = "ref_01.png",
                    MediaType = "image/png",
                    Base64Data = Convert.ToBase64String(fakePng)
                });
            },
            generateMesh: (req, _) =>
            {
                capturedImageB64 = req.SourceImageBase64;
                return Task.FromResult(new ThreedGenerateResponse
                {
                    FileName = "model.glb",
                    MediaType = "model/gltf-binary",
                    Base64Data = Convert.ToBase64String(validGlb),
                    DurationSeconds = 1.25
                });
            },
            isGamingActive: () => false,
            outputDirectory: _threedDir);

        var progress = new SyncProgress<ThreedPipelineProgress>(p => stages.Add(p.Stage));
        ThreedTextTo3dResponse result = await pipeline.GenerateAsync(
            new ThreedTextTo3dRequest { Prompt = "a low-poly wooden treasure chest" },
            progress,
            CancellationToken.None);

        Assert.Equal("a low-poly wooden treasure chest", capturedPrompt);
        Assert.Equal(Convert.ToBase64String(fakePng), capturedImageB64);
        Assert.True(File.Exists(result.SavedPath));
        Assert.StartsWith(_threedDir, result.SavedPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(".glb", Path.GetExtension(result.SavedPath));
        Assert.Equal("ref_01.png", result.ReferenceImageFileName);
        Assert.Equal(new[] { "checking_3d_engine", "generating_reference_image", "generating_3d_mesh", "completed" }, stages);

        // Verify the saved GLB is immediately exportable to OBJ/STL/USDZ
        var exporter = new ThreedExportService(_threedDir);
        ThreedExportResponse usdz = await exporter.ExportAsync(
            new ThreedExportRequest { SourcePath = result.SavedPath, Format = "usdz" },
            CancellationToken.None);
        Assert.True(File.Exists(usdz.OutputPath));
        Assert.Equal(12, usdz.TriangleCount);
    }

    [Fact]
    public async Task Text_to_3d_pipeline_fails_honestly_when_3d_engine_missing_or_gaming_active_or_cancelled()
    {
        bool imageCalled = false;
        var missing3d = new ThreedTextTo3dPipeline(
            getThreedStatus: _ => Task.FromResult(new ThreedEngineStatus { Installed = false, LastError = "CUDA wheel missing" }),
            generateImage: (_, _) => { imageCalled = true; throw new Exception("should not run"); },
            generateMesh: (_, _) => throw new Exception("should not run"),
            isGamingActive: () => false,
            outputDirectory: _threedDir);

        InvalidOperationException missingEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            missing3d.GenerateAsync(new ThreedTextTo3dRequest { Prompt = "a ceramic mug" }, null, CancellationToken.None));
        Assert.Contains("CUDA wheel missing", missingEx.Message);
        Assert.False(imageCalled);

        // Gaming hard-off refusal
        var gamingPipeline = new ThreedTextTo3dPipeline(
            getThreedStatus: _ => Task.FromResult(new ThreedEngineStatus { Installed = true }),
            generateImage: (_, _) => throw new Exception("should not run"),
            generateMesh: (_, _) => throw new Exception("should not run"),
            isGamingActive: () => true,
            outputDirectory: _threedDir);
        await Assert.ThrowsAsync<UnavailableDuringGamingException>(() =>
            gamingPipeline.GenerateAsync(new ThreedTextTo3dRequest { Prompt = "a ceramic mug" }, null, CancellationToken.None));

        // Cancellation between Stage 1 and Stage 2
        using var cts = new CancellationTokenSource();
        bool stage2Called = false;
        var cancelPipeline = new ThreedTextTo3dPipeline(
            getThreedStatus: _ => Task.FromResult(new ThreedEngineStatus { Installed = true }),
            generateImage: (_, _) =>
            {
                cts.Cancel();
                return Task.FromResult(new MediaAsset { FileName = "ref.png", Base64Data = Convert.ToBase64String(new byte[] { 1, 2, 3 }) });
            },
            generateMesh: (_, _) => { stage2Called = true; throw new Exception("should not run"); },
            isGamingActive: () => false,
            outputDirectory: _threedDir);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelPipeline.GenerateAsync(new ThreedTextTo3dRequest { Prompt = "a ceramic mug" }, null, cts.Token));
        Assert.False(stage2Called);
    }

    [Fact]
    public async Task Core_http_routes_reject_invalid_export_and_empty_text_to_3d_prompt()
    {
        using var factory = new ApiSecurityTests.PaRuXFactory();
        using HttpClient client = factory.CreateClient();
        ServerState state = factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage badExport = await client.PostAsJsonAsync("/api/v1/threed/export", new ThreedExportRequest
        {
            SourcePath = "../escape.glb",
            Format = "obj"
        });
        Assert.Equal(HttpStatusCode.BadRequest, badExport.StatusCode);

        HttpResponseMessage badText3d = await client.PostAsJsonAsync("/api/v1/threed/text-to-3d", new ThreedTextTo3dRequest
        {
            Prompt = "   "
        });
        Assert.Equal(HttpStatusCode.BadRequest, badText3d.StatusCode);
    }

    [Fact]
    public void Create_workspace_exposes_text_to_3d_and_export_controls_with_automation_ids_and_localization()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PaRuX.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string view = File.ReadAllText(Path.Combine(dir!.FullName, "src", "PaRuX.App", "Views", "CreateWorkspaceView.cs"));

        foreach (string id in new[]
        {
            "Create_Threed_Mode_ComboBox",
            "Create_Threed_Mode_Image_Item",
            "Create_Threed_Mode_Text_Item",
            "Create_Threed_Prompt_Box",
            "Create_Threed_Run_Button",
            "Create_Threed_Export_Panel",
            "Create_Threed_Export_Obj_Button",
            "Create_Threed_Export_Stl_Button",
            "Create_Threed_Export_Usdz_Button"
        })
        {
            Assert.Contains(id, view, StringComparison.Ordinal);
        }

        Assert.Contains("Testo → 3D", view, StringComparison.Ordinal);
        Assert.Contains("Immagine → 3D", view, StringComparison.Ordinal);
        Assert.Contains("Esporta OBJ", view, StringComparison.Ordinal);
        Assert.Contains("Esporta STL", view, StringComparison.Ordinal);
        Assert.Contains("Esporta USDZ", view, StringComparison.Ordinal);
    }

    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
