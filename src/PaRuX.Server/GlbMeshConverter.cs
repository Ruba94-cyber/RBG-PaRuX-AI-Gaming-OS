using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

public sealed class GlbMeshData
{
    public Vector3[] Positions { get; init; } = Array.Empty<Vector3>();
    public Vector3[] Normals { get; init; } = Array.Empty<Vector3>();
    public int[] TriangleIndices { get; init; } = Array.Empty<int>();
    public int VertexCount => Positions.Length;
    public int TriangleCount => TriangleIndices.Length / 3;
}

/// <summary>
/// Pure-C#, 100% local, deterministic glTF 2.0 binary (.glb) mesh reader and exporter
/// to Wavefront OBJ, standard Binary STL, and 64-byte aligned uncompressed USDZ (model.usda).
/// Zero external libraries, zero network access, strict bounds and resource caps.
/// </summary>
public static class GlbMeshConverter
{
    public const uint GlbMagic = 0x46546C67;       // "glTF"
    public const uint GlbVersion = 2;
    public const uint ChunkTypeJson = 0x4E4F534A;  // "JSON"
    public const uint ChunkTypeBin = 0x004E4942;   // "BIN\0"

    public const int MaxInputFileBytes = 64 * 1024 * 1024;
    public const int MaxJsonChunkBytes = 16 * 1024 * 1024;
    public const int MaxVertices = 2_000_000;
    public const int MaxTriangles = 2_000_000;

    private const int ComponentUnsignedByte = 5121;
    private const int ComponentUnsignedShort = 5123;
    private const int ComponentUnsignedInt = 5125;
    private const int ComponentFloat = 5126;

    private const int ModeTriangles = 4;
    private const int ModeTriangleStrip = 5;
    private const int ModeTriangleFan = 6;

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    public static GlbMeshData ParseGlb(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 20)
            throw new InvalidDataException("GLB file is too small to contain a valid header and JSON chunk.");
        if (bytes.Length > MaxInputFileBytes)
            throw new InvalidDataException($"GLB file exceeds maximum supported size of {MaxInputFileBytes:N0} bytes.");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        if (magic != GlbMagic)
            throw new InvalidDataException("Invalid GLB magic header (expected 'glTF').");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        if (version != GlbVersion)
            throw new InvalidDataException($"Unsupported GLB version {version} (expected 2).");

        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        if (declaredLength < 20 || declaredLength > (uint)bytes.Length)
            throw new InvalidDataException("GLB header length is invalid or truncated.");

        int offset = 12;
        ReadOnlyMemory<byte> jsonChunk = default;
        ReadOnlyMemory<byte> binChunk = default;
        bool hasJson = false;
        bool hasBin = false;

        while (offset < (int)declaredLength)
        {
            if (declaredLength - (uint)offset < 8)
                throw new InvalidDataException("GLB chunk header is truncated.");

            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            offset += 8;

            long chunkEnd = (long)offset + chunkLength;
            if (chunkEnd > declaredLength || chunkEnd > bytes.Length)
                throw new InvalidDataException("GLB chunk length extends past end of file.");

            if (!hasJson)
            {
                if (chunkType != ChunkTypeJson)
                    throw new InvalidDataException("First GLB chunk must be JSON.");
                if (chunkLength == 0 || chunkLength > MaxJsonChunkBytes)
                    throw new InvalidDataException("GLB JSON chunk size is out of bounds.");
                jsonChunk = bytes.AsMemory(offset, (int)chunkLength);
                hasJson = true;
            }
            else if (chunkType == ChunkTypeBin && !hasBin)
            {
                binChunk = bytes.AsMemory(offset, (int)chunkLength);
                hasBin = true;
            }

            offset = (int)chunkEnd;
        }

        if (!hasJson)
            throw new InvalidDataException("GLB file does not contain a JSON chunk.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonChunk, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("GLB JSON chunk is malformed: " + ex.Message);
        }

        using (doc)
        {
            return ExtractMeshData(doc.RootElement, binChunk.Span);
        }
    }

    private static GlbMeshData ExtractMeshData(JsonElement root, ReadOnlySpan<byte> binChunk)
    {
        List<byte[]> buffers = ResolveBuffers(root, binChunk);
        if (!root.TryGetProperty("bufferViews", out JsonElement bufferViewsEl) || bufferViewsEl.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GLB JSON is missing 'bufferViews'.");
        if (!root.TryGetProperty("accessors", out JsonElement accessorsEl) || accessorsEl.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GLB JSON is missing 'accessors'.");
        if (!root.TryGetProperty("meshes", out JsonElement meshesEl) || meshesEl.ValueKind != JsonValueKind.Array || meshesEl.GetArrayLength() == 0)
            throw new InvalidDataException("GLB JSON contains no meshes.");

        int bufferViewCount = bufferViewsEl.GetArrayLength();
        int accessorCount = accessorsEl.GetArrayLength();

        var allPositions = new List<Vector3>();
        var allNormals = new List<Vector3>();
        var allIndices = new List<int>();
        bool hasAnyExplicitNormals = false;

        List<(int MeshIndex, Matrix4x4 Transform)> instances = CollectMeshInstances(root, meshesEl.GetArrayLength());

        foreach ((int meshIndex, Matrix4x4 worldTransform) in instances)
        {
            JsonElement meshEl = meshesEl[meshIndex];
            if (!meshEl.TryGetProperty("primitives", out JsonElement primitivesEl) || primitivesEl.ValueKind != JsonValueKind.Array)
                continue;

            bool hasNormalMatrix = Matrix4x4.Invert(worldTransform, out Matrix4x4 invTransform);
            Matrix4x4 normalMatrix = hasNormalMatrix ? Matrix4x4.Transpose(invTransform) : Matrix4x4.Identity;

            foreach (JsonElement prim in primitivesEl.EnumerateArray())
            {
                int mode = prim.TryGetProperty("mode", out JsonElement modeEl) && modeEl.ValueKind == JsonValueKind.Number
                    ? modeEl.GetInt32()
                    : ModeTriangles;
                if (mode is not (ModeTriangles or ModeTriangleStrip or ModeTriangleFan))
                    continue;

                if (!prim.TryGetProperty("attributes", out JsonElement attrs) || attrs.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Mesh primitive is missing 'attributes'.");
                if (!attrs.TryGetProperty("POSITION", out JsonElement posAccIdxEl) || posAccIdxEl.ValueKind != JsonValueKind.Number)
                    throw new InvalidDataException("Mesh primitive is missing 'POSITION' attribute.");

                int posAccessorIndex = posAccIdxEl.GetInt32();
                Vector3[] positions = ReadVec3FloatAccessor(posAccessorIndex, accessorsEl, accessorCount, bufferViewsEl, bufferViewCount, buffers);

                if ((long)allPositions.Count + positions.Length > MaxVertices)
                    throw new InvalidDataException($"Mesh exceeds maximum supported vertex count ({MaxVertices:N0}).");

                Vector3[]? normals = null;
                if (attrs.TryGetProperty("NORMAL", out JsonElement normAccIdxEl) && normAccIdxEl.ValueKind == JsonValueKind.Number)
                {
                    normals = ReadVec3FloatAccessor(normAccIdxEl.GetInt32(), accessorsEl, accessorCount, bufferViewsEl, bufferViewCount, buffers);
                    if (normals.Length != positions.Length)
                        throw new InvalidDataException("Mesh NORMAL accessor count does not match POSITION count.");
                    hasAnyExplicitNormals = true;
                }

                int[] rawIndices;
                if (prim.TryGetProperty("indices", out JsonElement idxAccEl) && idxAccEl.ValueKind == JsonValueKind.Number)
                {
                    rawIndices = ReadScalarIndexAccessor(idxAccEl.GetInt32(), positions.Length, accessorsEl, accessorCount, bufferViewsEl, bufferViewCount, buffers);
                }
                else
                {
                    rawIndices = new int[positions.Length];
                    for (int i = 0; i < positions.Length; i++) rawIndices[i] = i;
                }

                int baseVertex = allPositions.Count;
                for (int i = 0; i < positions.Length; i++)
                {
                    Vector3 p = Vector3.Transform(positions[i], worldTransform);
                    EnsureFinite(p, "vertex position");
                    allPositions.Add(p);

                    if (normals != null)
                    {
                        Vector3 n = Vector3.TransformNormal(normals[i], normalMatrix);
                        float len = n.Length();
                        n = len > 1e-8f ? n / len : Vector3.UnitY;
                        EnsureFinite(n, "vertex normal");
                        allNormals.Add(n);
                    }
                    else
                    {
                        allNormals.Add(Vector3.Zero);
                    }
                }

                AppendTriangles(rawIndices, mode, baseVertex, allIndices);
            }
        }

        if (allPositions.Count == 0 || allIndices.Count < 3)
            throw new InvalidDataException("GLB file contains no valid triangle geometry.");

        Vector3[] finalPositions = allPositions.ToArray();
        int[] finalIndices = allIndices.ToArray();
        Vector3[] finalNormals = hasAnyExplicitNormals
            ? FillMissingNormals(finalPositions, finalIndices, allNormals.ToArray())
            : ComputeVertexNormals(finalPositions, finalIndices);

        return new GlbMeshData
        {
            Positions = finalPositions,
            Normals = finalNormals,
            TriangleIndices = finalIndices
        };
    }

    private static List<byte[]> ResolveBuffers(JsonElement root, ReadOnlySpan<byte> binChunk)
    {
        var result = new List<byte[]>();
        if (!root.TryGetProperty("buffers", out JsonElement buffersEl) || buffersEl.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GLB JSON is missing 'buffers'.");

        int index = 0;
        foreach (JsonElement buf in buffersEl.EnumerateArray())
        {
            long byteLength = buf.TryGetProperty("byteLength", out JsonElement lenEl) && lenEl.ValueKind == JsonValueKind.Number
                ? lenEl.GetInt64()
                : -1;
            if (byteLength < 0 || byteLength > MaxInputFileBytes)
                throw new InvalidDataException("GLB buffer byteLength is invalid or exceeds limits.");

            if (buf.TryGetProperty("uri", out JsonElement uriEl) && uriEl.ValueKind == JsonValueKind.String)
            {
                string uri = uriEl.GetString() ?? string.Empty;
                const string dataPrefix = "data:";
                int base64Marker = uri.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
                if (!uri.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase) || base64Marker < 0)
                    throw new InvalidDataException("External buffer URIs are not permitted in local GLB export.");

                byte[] decoded;
                try { decoded = Convert.FromBase64String(uri[(base64Marker + ";base64,".Length)..]); }
                catch (FormatException) { throw new InvalidDataException("Embedded buffer data URI is not valid Base64."); }

                if (decoded.Length < byteLength)
                    throw new InvalidDataException("Embedded buffer data URI is shorter than declared byteLength.");
                result.Add(decoded);
            }
            else
            {
                if (index != 0 || binChunk.IsEmpty)
                    throw new InvalidDataException("GLB buffer references missing BIN chunk.");
                if (binChunk.Length < byteLength)
                    throw new InvalidDataException("GLB BIN chunk is shorter than declared buffer byteLength.");
                result.Add(binChunk.ToArray());
            }
            index++;
        }
        return result;
    }

    private static List<(int MeshIndex, Matrix4x4 Transform)> CollectMeshInstances(JsonElement root, int meshCount)
    {
        var instances = new List<(int MeshIndex, Matrix4x4 Transform)>();
        if (!root.TryGetProperty("nodes", out JsonElement nodesEl) || nodesEl.ValueKind != JsonValueKind.Array || nodesEl.GetArrayLength() == 0)
        {
            for (int i = 0; i < meshCount; i++) instances.Add((i, Matrix4x4.Identity));
            return instances;
        }

        int nodeCount = nodesEl.GetArrayLength();
        var rootNodeIndices = new List<int>();
        if (root.TryGetProperty("scenes", out JsonElement scenesEl) && scenesEl.ValueKind == JsonValueKind.Array && scenesEl.GetArrayLength() > 0)
        {
            int sceneIdx = root.TryGetProperty("scene", out JsonElement defaultSceneEl) && defaultSceneEl.ValueKind == JsonValueKind.Number
                ? defaultSceneEl.GetInt32()
                : 0;
            if (sceneIdx >= 0 && sceneIdx < scenesEl.GetArrayLength()
                && scenesEl[sceneIdx].TryGetProperty("nodes", out JsonElement sceneNodes) && sceneNodes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement n in sceneNodes.EnumerateArray())
                {
                    if (n.ValueKind == JsonValueKind.Number)
                    {
                        int idx = n.GetInt32();
                        if (idx >= 0 && idx < nodeCount) rootNodeIndices.Add(idx);
                    }
                }
            }
        }

        if (rootNodeIndices.Count == 0)
        {
            for (int i = 0; i < nodeCount; i++) rootNodeIndices.Add(i);
        }

        var visited = new HashSet<int>();
        void Walk(int nodeIndex, Matrix4x4 parentTransform, int depth)
        {
            if (depth > 64 || nodeIndex < 0 || nodeIndex >= nodeCount || !visited.Add(nodeIndex)) return;
            JsonElement node = nodesEl[nodeIndex];
            Matrix4x4 local = ReadNodeTransform(node);
            Matrix4x4 world = local * parentTransform;
            if (node.TryGetProperty("mesh", out JsonElement meshIdxEl) && meshIdxEl.ValueKind == JsonValueKind.Number)
            {
                int meshIdx = meshIdxEl.GetInt32();
                if (meshIdx >= 0 && meshIdx < meshCount) instances.Add((meshIdx, world));
            }
            if (node.TryGetProperty("children", out JsonElement childrenEl) && childrenEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement childEl in childrenEl.EnumerateArray())
                {
                    if (childEl.ValueKind == JsonValueKind.Number) Walk(childEl.GetInt32(), world, depth + 1);
                }
            }
        }

        foreach (int r in rootNodeIndices) Walk(r, Matrix4x4.Identity, 0);

        if (instances.Count == 0)
        {
            for (int i = 0; i < meshCount; i++) instances.Add((i, Matrix4x4.Identity));
        }

        return instances;
    }

    private static Matrix4x4 ReadNodeTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out JsonElement matrixEl) && matrixEl.ValueKind == JsonValueKind.Array && matrixEl.GetArrayLength() == 16)
        {
            float[] m = new float[16];
            for (int i = 0; i < 16; i++) m[i] = matrixEl[i].GetSingle();
            // glTF matrices are column-major; System.Numerics.Matrix4x4 uses row-vector multiplication (v * M),
            // so column-major elements map directly to M11..M44 in row-major storage.
            return new Matrix4x4(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);
        }

        Vector3 scale = Vector3.One;
        if (node.TryGetProperty("scale", out JsonElement scaleEl) && scaleEl.ValueKind == JsonValueKind.Array && scaleEl.GetArrayLength() == 3)
            scale = new Vector3(scaleEl[0].GetSingle(), scaleEl[1].GetSingle(), scaleEl[2].GetSingle());

        Quaternion rot = Quaternion.Identity;
        if (node.TryGetProperty("rotation", out JsonElement rotEl) && rotEl.ValueKind == JsonValueKind.Array && rotEl.GetArrayLength() == 4)
            rot = Quaternion.Normalize(new Quaternion(rotEl[0].GetSingle(), rotEl[1].GetSingle(), rotEl[2].GetSingle(), rotEl[3].GetSingle()));

        Vector3 trans = Vector3.Zero;
        if (node.TryGetProperty("translation", out JsonElement transEl) && transEl.ValueKind == JsonValueKind.Array && transEl.GetArrayLength() == 3)
            trans = new Vector3(transEl[0].GetSingle(), transEl[1].GetSingle(), transEl[2].GetSingle());

        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(trans);
    }

    private static Vector3[] ReadVec3FloatAccessor(
        int accessorIndex,
        JsonElement accessorsEl, int accessorCount,
        JsonElement bufferViewsEl, int bufferViewCount,
        List<byte[]> buffers)
    {
        (byte[] buffer, int baseOffset, int stride, int count, int componentType, string type) =
            ResolveAccessor(accessorIndex, accessorsEl, accessorCount, bufferViewsEl, bufferViewCount, buffers, 12);

        if (!string.Equals(type, "VEC3", StringComparison.Ordinal) || componentType != ComponentFloat)
            throw new InvalidDataException("Expected VEC3 FLOAT accessor for vertex positions/normals.");
        if (count <= 0 || count > MaxVertices)
            throw new InvalidDataException($"Vertex accessor count ({count}) is out of allowed bounds.");

        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int pos = baseOffset + i * stride;
            float x = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(pos, 4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(pos + 4, 4));
            float z = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(pos + 8, 4));
            result[i] = new Vector3(x, y, z);
        }
        return result;
    }

    private static int[] ReadScalarIndexAccessor(
        int accessorIndex, int vertexCount,
        JsonElement accessorsEl, int accessorCount,
        JsonElement bufferViewsEl, int bufferViewCount,
        List<byte[]> buffers)
    {
        if (accessorIndex < 0 || accessorIndex >= accessorCount)
            throw new InvalidDataException("Index accessor index is out of range.");
        JsonElement acc = accessorsEl[accessorIndex];
        int componentType = acc.TryGetProperty("componentType", out JsonElement ctEl) ? ctEl.GetInt32() : -1;
        int elemSize = componentType switch
        {
            ComponentUnsignedByte => 1,
            ComponentUnsignedShort => 2,
            ComponentUnsignedInt => 4,
            _ => throw new InvalidDataException($"Unsupported index componentType {componentType}.")
        };

        (byte[] buffer, int baseOffset, int stride, int count, _, string type) =
            ResolveAccessor(accessorIndex, accessorsEl, accessorCount, bufferViewsEl, bufferViewCount, buffers, elemSize);

        if (!string.Equals(type, "SCALAR", StringComparison.Ordinal))
            throw new InvalidDataException("Index accessor must have type SCALAR.");
        if (count <= 0 || count > MaxTriangles * 3)
            throw new InvalidDataException($"Index accessor count ({count}) exceeds allowed bounds.");

        var indices = new int[count];
        for (int i = 0; i < count; i++)
        {
            int pos = baseOffset + i * stride;
            uint idx = componentType switch
            {
                ComponentUnsignedByte => buffer[pos],
                ComponentUnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos, 2)),
                ComponentUnsignedInt => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos, 4)),
                _ => throw new InvalidDataException("Unsupported index componentType.")
            };
            if (idx >= (uint)vertexCount)
                throw new InvalidDataException($"Triangle vertex index {idx} is out of bounds (vertex count {vertexCount}).");
            indices[i] = (int)idx;
        }
        return indices;
    }

    private static (byte[] Buffer, int BaseOffset, int Stride, int Count, int ComponentType, string Type) ResolveAccessor(
        int accessorIndex,
        JsonElement accessorsEl, int accessorCount,
        JsonElement bufferViewsEl, int bufferViewCount,
        List<byte[]> buffers,
        int elementByteSize)
    {
        if (accessorIndex < 0 || accessorIndex >= accessorCount)
            throw new InvalidDataException("Accessor index is out of range.");

        JsonElement acc = accessorsEl[accessorIndex];
        if (!acc.TryGetProperty("bufferView", out JsonElement bvIdxEl) || bvIdxEl.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException("Accessor is missing 'bufferView'.");
        int bvIdx = bvIdxEl.GetInt32();
        if (bvIdx < 0 || bvIdx >= bufferViewCount)
            throw new InvalidDataException("BufferView index is out of range.");

        int count = acc.TryGetProperty("count", out JsonElement countEl) && countEl.ValueKind == JsonValueKind.Number
            ? countEl.GetInt32()
            : -1;
        long accOffset = acc.TryGetProperty("byteOffset", out JsonElement aoEl) && aoEl.ValueKind == JsonValueKind.Number
            ? aoEl.GetInt64()
            : 0;
        int componentType = acc.TryGetProperty("componentType", out JsonElement ctEl) && ctEl.ValueKind == JsonValueKind.Number
            ? ctEl.GetInt32()
            : -1;
        string type = acc.TryGetProperty("type", out JsonElement typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString() ?? string.Empty
            : string.Empty;

        JsonElement bv = bufferViewsEl[bvIdx];
        int bufferIndex = bv.TryGetProperty("buffer", out JsonElement bIdxEl) && bIdxEl.ValueKind == JsonValueKind.Number
            ? bIdxEl.GetInt32()
            : -1;
        if (bufferIndex < 0 || bufferIndex >= buffers.Count)
            throw new InvalidDataException("BufferView references an out-of-range buffer.");

        long bvOffset = bv.TryGetProperty("byteOffset", out JsonElement bvoEl) && bvoEl.ValueKind == JsonValueKind.Number
            ? bvoEl.GetInt64()
            : 0;
        long bvLength = bv.TryGetProperty("byteLength", out JsonElement bvlEl) && bvlEl.ValueKind == JsonValueKind.Number
            ? bvlEl.GetInt64()
            : -1;
        int stride = bv.TryGetProperty("byteStride", out JsonElement bvsEl) && bvsEl.ValueKind == JsonValueKind.Number
            ? bvsEl.GetInt32()
            : elementByteSize;
        if (stride == 0) stride = elementByteSize;

        if (count <= 0 || accOffset < 0 || bvOffset < 0 || bvLength <= 0 || stride < elementByteSize)
            throw new InvalidDataException("Accessor or BufferView has invalid offset, length, or stride.");

        byte[] buffer = buffers[bufferIndex];
        long bvEnd = bvOffset + bvLength;
        if (bvEnd > buffer.Length)
            throw new InvalidDataException("BufferView extends past the end of its buffer.");

        long requiredInView = accOffset + (long)(count - 1) * stride + elementByteSize;
        if (requiredInView > bvLength)
            throw new InvalidDataException("Accessor extends past the end of its BufferView.");

        long totalBaseOffset = bvOffset + accOffset;
        if (totalBaseOffset > int.MaxValue || totalBaseOffset + (long)(count - 1) * stride + elementByteSize > buffer.Length)
            throw new InvalidDataException("Accessor data extends past the end of its buffer.");

        return (buffer, (int)totalBaseOffset, stride, count, componentType, type);
    }

    private static void AppendTriangles(int[] rawIndices, int mode, int baseVertex, List<int> dest)
    {
        if (mode == ModeTriangles)
        {
            int triCount = rawIndices.Length / 3;
            EnsureTriangleBudget(dest.Count / 3 + triCount);
            for (int i = 0; i + 2 < rawIndices.Length; i += 3)
            {
                dest.Add(baseVertex + rawIndices[i]);
                dest.Add(baseVertex + rawIndices[i + 1]);
                dest.Add(baseVertex + rawIndices[i + 2]);
            }
        }
        else if (mode == ModeTriangleStrip && rawIndices.Length >= 3)
        {
            int triCount = rawIndices.Length - 2;
            EnsureTriangleBudget(dest.Count / 3 + triCount);
            for (int i = 0; i < triCount; i++)
            {
                if ((i & 1) == 0)
                {
                    dest.Add(baseVertex + rawIndices[i]);
                    dest.Add(baseVertex + rawIndices[i + 1]);
                    dest.Add(baseVertex + rawIndices[i + 2]);
                }
                else
                {
                    dest.Add(baseVertex + rawIndices[i + 1]);
                    dest.Add(baseVertex + rawIndices[i]);
                    dest.Add(baseVertex + rawIndices[i + 2]);
                }
            }
        }
        else if (mode == ModeTriangleFan && rawIndices.Length >= 3)
        {
            int triCount = rawIndices.Length - 2;
            EnsureTriangleBudget(dest.Count / 3 + triCount);
            for (int i = 1; i + 1 < rawIndices.Length; i++)
            {
                dest.Add(baseVertex + rawIndices[0]);
                dest.Add(baseVertex + rawIndices[i]);
                dest.Add(baseVertex + rawIndices[i + 1]);
            }
        }
    }

    private static void EnsureTriangleBudget(long totalTriangles)
    {
        if (totalTriangles > MaxTriangles)
            throw new InvalidDataException($"Mesh exceeds maximum supported triangle count ({MaxTriangles:N0}).");
    }

    private static void EnsureFinite(Vector3 v, string label)
    {
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
            throw new InvalidDataException($"GLB contains non-finite {label} coordinates.");
    }

    private static Vector3[] ComputeVertexNormals(Vector3[] positions, int[] indices)
    {
        var normals = new Vector3[positions.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
            Vector3 cross = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
            normals[i0] += cross;
            normals[i1] += cross;
            normals[i2] += cross;
        }
        for (int i = 0; i < normals.Length; i++)
        {
            float len = normals[i].Length();
            normals[i] = len > 1e-8f ? normals[i] / len : Vector3.UnitY;
        }
        return normals;
    }

    private static Vector3[] FillMissingNormals(Vector3[] positions, int[] indices, Vector3[] normals)
    {
        bool anyZero = false;
        for (int i = 0; i < normals.Length; i++)
        {
            if (normals[i].LengthSquared() < 1e-10f) { anyZero = true; break; }
        }
        if (!anyZero) return normals;

        Vector3[] computed = ComputeVertexNormals(positions, indices);
        for (int i = 0; i < normals.Length; i++)
        {
            if (normals[i].LengthSquared() < 1e-10f) normals[i] = computed[i];
        }
        return normals;
    }

    public static byte[] ExportObj(GlbMeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var sb = new StringBuilder(Math.Max(256, mesh.VertexCount * 48 + mesh.TriangleCount * 24));
        sb.Append("# Exported by PaRuX Local 3D Exporter\n");
        sb.Append("o model\n");

        foreach (Vector3 v in mesh.Positions)
        {
            sb.Append("v ")
              .Append(FormatFloat(v.X)).Append(' ')
              .Append(FormatFloat(v.Y)).Append(' ')
              .Append(FormatFloat(v.Z)).Append('\n');
        }

        bool hasNormals = mesh.Normals.Length == mesh.Positions.Length && mesh.Normals.Length > 0;
        if (hasNormals)
        {
            foreach (Vector3 n in mesh.Normals)
            {
                sb.Append("vn ")
                  .Append(FormatFloat(n.X)).Append(' ')
                  .Append(FormatFloat(n.Y)).Append(' ')
                  .Append(FormatFloat(n.Z)).Append('\n');
            }
        }

        for (int i = 0; i + 2 < mesh.TriangleIndices.Length; i += 3)
        {
            int a = mesh.TriangleIndices[i] + 1;
            int b = mesh.TriangleIndices[i + 1] + 1;
            int c = mesh.TriangleIndices[i + 2] + 1;
            if (hasNormals)
            {
                sb.Append("f ")
                  .Append(a).Append("//").Append(a).Append(' ')
                  .Append(b).Append("//").Append(b).Append(' ')
                  .Append(c).Append("//").Append(c).Append('\n');
            }
            else
            {
                sb.Append("f ").Append(a).Append(' ').Append(b).Append(' ').Append(c).Append('\n');
            }
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(sb.ToString());
    }

    public static byte[] ExportStlBinary(GlbMeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        int triangleCount = mesh.TriangleCount;
        long totalBytes = 84L + (long)triangleCount * 50L;
        if (totalBytes > int.MaxValue)
            throw new InvalidDataException("Exported STL exceeds maximum buffer size.");

        byte[] output = new byte[totalBytes];
        byte[] headerText = Encoding.ASCII.GetBytes("PaRuX Local 3D Binary STL Export");
        Buffer.BlockCopy(headerText, 0, output, 0, Math.Min(80, headerText.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(80, 4), (uint)triangleCount);

        int offset = 84;
        for (int i = 0; i + 2 < mesh.TriangleIndices.Length; i += 3)
        {
            Vector3 v0 = mesh.Positions[mesh.TriangleIndices[i]];
            Vector3 v1 = mesh.Positions[mesh.TriangleIndices[i + 1]];
            Vector3 v2 = mesh.Positions[mesh.TriangleIndices[i + 2]];

            Vector3 cross = Vector3.Cross(v1 - v0, v2 - v0);
            float len = cross.Length();
            Vector3 normal = len > 1e-8f ? cross / len : Vector3.Zero;

            WriteVec3(output.AsSpan(offset, 12), normal);
            WriteVec3(output.AsSpan(offset + 12, 12), v0);
            WriteVec3(output.AsSpan(offset + 24, 12), v1);
            WriteVec3(output.AsSpan(offset + 36, 12), v2);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(offset + 48, 2), 0);
            offset += 50;
        }

        return output;
    }

    public static byte[] ExportUsdz(GlbMeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        string usda = BuildUsdaText(mesh);
        byte[] usdaBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(usda);
        return BuildUncompressedAlignedUsdzArchive("model.usda", usdaBytes);
    }

    public static string BuildUsdaText(GlbMeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var sb = new StringBuilder(Math.Max(512, mesh.VertexCount * 40 + mesh.TriangleCount * 16));

        Vector3 min = mesh.Positions[0];
        Vector3 max = mesh.Positions[0];
        for (int i = 1; i < mesh.Positions.Length; i++)
        {
            min = Vector3.Min(min, mesh.Positions[i]);
            max = Vector3.Max(max, mesh.Positions[i]);
        }

        sb.Append("#usda 1.0\n");
        sb.Append("(\n");
        sb.Append("    defaultPrim = \"Model\"\n");
        sb.Append("    metersPerUnit = 1\n");
        sb.Append("    upAxis = \"Y\"\n");
        sb.Append(")\n\n");
        sb.Append("def Xform \"Model\"\n");
        sb.Append("{\n");
        sb.Append("    def Mesh \"Mesh\"\n");
        sb.Append("    {\n");
        sb.Append("        float3[] extent = [(")
          .Append(FormatFloat(min.X)).Append(", ").Append(FormatFloat(min.Y)).Append(", ").Append(FormatFloat(min.Z))
          .Append("), (")
          .Append(FormatFloat(max.X)).Append(", ").Append(FormatFloat(max.Y)).Append(", ").Append(FormatFloat(max.Z))
          .Append(")]\n");

        sb.Append("        int[] faceVertexCounts = [");
        int triCount = mesh.TriangleCount;
        for (int i = 0; i < triCount; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('3');
        }
        sb.Append("]\n");

        sb.Append("        int[] faceVertexIndices = [");
        for (int i = 0; i < mesh.TriangleIndices.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(mesh.TriangleIndices[i]);
        }
        sb.Append("]\n");

        sb.Append("        point3f[] points = [");
        for (int i = 0; i < mesh.Positions.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            Vector3 p = mesh.Positions[i];
            sb.Append('(').Append(FormatFloat(p.X)).Append(", ").Append(FormatFloat(p.Y)).Append(", ").Append(FormatFloat(p.Z)).Append(')');
        }
        sb.Append("]\n");

        if (mesh.Normals.Length == mesh.Positions.Length && mesh.Normals.Length > 0)
        {
            sb.Append("        normal3f[] normals = [");
            for (int i = 0; i < mesh.Normals.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                Vector3 n = mesh.Normals[i];
                sb.Append('(').Append(FormatFloat(n.X)).Append(", ").Append(FormatFloat(n.Y)).Append(", ").Append(FormatFloat(n.Z)).Append(')');
            }
            sb.Append("] (\n");
            sb.Append("            interpolation = \"vertex\"\n");
            sb.Append("        )\n");
        }

        sb.Append("        uniform token subdivisionScheme = \"none\"\n");
        sb.Append("    }\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Writes a spec-compliant USDZ archive: uncompressed (method 0 / STORE) ZIP where the entry's
    /// payload starts on a 64-byte boundary using the 0x1986 alignment extra field.
    /// </summary>
    private static byte[] BuildUncompressedAlignedUsdzArchive(string entryName, byte[] payload)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(entryName);
        uint crc = ComputeCrc32(payload);
        uint size = (uint)payload.Length;

        int headerWithoutExtra = 30 + nameBytes.Length;
        int remainder = headerWithoutExtra % 64;
        int extraLength = remainder == 0 ? 0 : 64 - remainder;
        if (extraLength > 0 && extraLength < 4) extraLength += 64;

        using var ms = new MemoryStream(headerWithoutExtra + extraLength + payload.Length + 128);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Local File Header
        bw.Write(0x04034B50u); // signature
        bw.Write((ushort)20);  // version needed to extract (2.0)
        bw.Write((ushort)0);   // general purpose bit flag
        bw.Write((ushort)0);   // compression method: 0 = STORE (uncompressed)
        bw.Write((ushort)0);   // last mod file time
        bw.Write((ushort)0);   // last mod file date
        bw.Write(crc);         // CRC-32
        bw.Write(size);        // compressed size
        bw.Write(size);        // uncompressed size
        bw.Write((ushort)nameBytes.Length);
        bw.Write((ushort)extraLength);
        bw.Write(nameBytes);

        if (extraLength > 0)
        {
            // USDZ alignment extra field: header id 0x1986 + data size + zero padding
            bw.Write((ushort)0x1986);
            bw.Write((ushort)(extraLength - 4));
            for (int i = 0; i < extraLength - 4; i++) bw.Write((byte)0);
        }

        // Payload starts at exact 64-byte aligned offset
        bw.Write(payload);

        uint centralDirOffset = (uint)ms.Position;

        // Central Directory File Header
        bw.Write(0x02014B50u); // signature
        bw.Write((ushort)20);  // version made by
        bw.Write((ushort)20);  // version needed to extract
        bw.Write((ushort)0);   // flags
        bw.Write((ushort)0);   // compression method: STORE
        bw.Write((ushort)0);   // mod time
        bw.Write((ushort)0);   // mod date
        bw.Write(crc);
        bw.Write(size);
        bw.Write(size);
        bw.Write((ushort)nameBytes.Length);
        bw.Write((ushort)0);   // extra field length in CD
        bw.Write((ushort)0);   // file comment length
        bw.Write((ushort)0);   // disk number start
        bw.Write((ushort)0);   // internal file attributes
        bw.Write(0u);          // external file attributes
        bw.Write(0u);          // relative offset of local header
        bw.Write(nameBytes);

        uint centralDirSize = (uint)ms.Position - centralDirOffset;

        // End of Central Directory Record
        bw.Write(0x06054B50u); // signature
        bw.Write((ushort)0);   // disk number
        bw.Write((ushort)0);   // disk with start of CD
        bw.Write((ushort)1);   // entries on this disk
        bw.Write((ushort)1);   // total entries
        bw.Write(centralDirSize);
        bw.Write(centralDirOffset);
        bw.Write((ushort)0);   // comment length

        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteVec3(Span<byte> dest, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(0, 4), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(4, 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(8, 4), v.Z);
    }

    private static string FormatFloat(float value)
    {
        if (MathF.Abs(value) < 1e-7f) value = 0f;
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
            crc = Crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
