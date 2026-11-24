using System.Text.Json;

namespace PaRuX.Server;

public sealed class TrainingExportVerification
{
    public string Kind { get; set; } = string.Empty;
    public bool Valid { get; set; }
    public List<string> Problems { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public int TensorCount { get; set; }
    public long ParameterCount { get; set; }
    public List<string> DataTypes { get; set; } = new();
    /// <summary>True when every floating-point tensor is F16 or BF16 (a 16-bit export).</summary>
    public bool SixteenBit { get; set; }
    public long TotalBytes { get; set; }
}

/// <summary>
/// Structural verification of a training export, without loading the model: safetensors files (header, dtypes,
/// shapes versus byte ranges, contiguity), a merged model folder (config + weights + tokenizer, shards listed in
/// the index all present) and GGUF files (magic, version, plausible tensor count). It proves the files are
/// well-formed and complete; it does not prove the model answers well - that needs a real load and an eval.
/// </summary>
public static class TrainingExportVerifier
{
    private const long MaxHeaderBytes = 100L * 1024 * 1024;

    private static readonly Dictionary<string, int> DtypeBytes = new(StringComparer.Ordinal)
    {
        ["F64"] = 8, ["F32"] = 4, ["F16"] = 2, ["BF16"] = 2, ["I64"] = 8, ["I32"] = 4, ["I16"] = 2, ["I8"] = 1,
        ["U8"] = 1, ["BOOL"] = 1, ["U16"] = 2, ["U32"] = 4, ["U64"] = 8, ["F8_E4M3"] = 1, ["F8_E5M2"] = 1,
    };

    public static TrainingExportVerification VerifySafetensors(string path)
    {
        var result = new TrainingExportVerification { Kind = "safetensors" };
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            result.TotalBytes = stream.Length;
            if (stream.Length < 10) { result.Problems.Add("The file is too small to be a safetensors file."); return Finish(result); }
            Span<byte> lengthBytes = stackalloc byte[8];
            stream.ReadExactly(lengthBytes);
            ulong headerLength = BitConverter.ToUInt64(lengthBytes);
            if (headerLength == 0 || headerLength > (ulong)MaxHeaderBytes || (long)headerLength + 8 > stream.Length)
            {
                result.Problems.Add("The safetensors header length is invalid.");
                return Finish(result);
            }
            byte[] header = new byte[(int)headerLength];
            stream.ReadExactly(header);
            using JsonDocument doc = JsonDocument.Parse(header);
            long dataLength = stream.Length - 8 - (long)headerLength;
            var ranges = new List<(long Begin, long End)>();
            var types = new SortedSet<string>(StringComparer.Ordinal);
            bool allSixteen = true, anyFloat = false;
            foreach (JsonProperty tensor in doc.RootElement.EnumerateObject())
            {
                if (tensor.Name == "__metadata__") continue;
                JsonElement t = tensor.Value;
                if (!t.TryGetProperty("dtype", out JsonElement dtypeElement) || dtypeElement.GetString() is not { } dtype
                    || !DtypeBytes.TryGetValue(dtype, out int size))
                { result.Problems.Add("Tensor '" + tensor.Name + "' has an unknown dtype."); continue; }
                if (!t.TryGetProperty("shape", out JsonElement shape) || shape.ValueKind != JsonValueKind.Array
                    || !t.TryGetProperty("data_offsets", out JsonElement offsets) || offsets.GetArrayLength() != 2)
                { result.Problems.Add("Tensor '" + tensor.Name + "' is missing shape or data_offsets."); continue; }
                long elements = 1;
                foreach (JsonElement dim in shape.EnumerateArray()) elements = checked(elements * dim.GetInt64());
                long begin = offsets[0].GetInt64(), end = offsets[1].GetInt64();
                if (begin < 0 || end < begin || end > dataLength)
                    result.Problems.Add("Tensor '" + tensor.Name + "' points outside the file (truncated download or write?).");
                else if (end - begin != elements * size)
                    result.Problems.Add("Tensor '" + tensor.Name + "' byte range does not match its shape and dtype.");
                ranges.Add((begin, end));
                types.Add(dtype);
                result.TensorCount++;
                result.ParameterCount += elements;
                if (dtype is "F32" or "F64" or "F16" or "BF16")
                {
                    anyFloat = true;
                    if (dtype is "F32" or "F64") allSixteen = false;
                }
            }
            if (result.TensorCount == 0) result.Problems.Add("The file contains no tensors.");
            ranges.Sort();
            for (int i = 1; i < ranges.Count; i++)
                if (ranges[i].Begin < ranges[i - 1].End) { result.Problems.Add("Tensor byte ranges overlap."); break; }
            if (ranges.Count > 0 && ranges[^1].End != dataLength)
                result.Warnings.Add("The data section has bytes that no tensor references.");
            result.DataTypes = types.ToList();
            result.SixteenBit = anyFloat && allSixteen;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or OverflowException or FormatException or ArgumentException)
        {
            result.Problems.Add("The file could not be read as safetensors: " + ex.GetType().Name + ".");
        }
        return Finish(result);
    }

    public static TrainingExportVerification VerifyModelFolder(string folder)
    {
        var result = new TrainingExportVerification { Kind = "model-folder" };
        if (!Directory.Exists(folder)) { result.Problems.Add("The folder does not exist."); return Finish(result); }
        if (!File.Exists(Path.Combine(folder, "config.json"))) result.Problems.Add("config.json is missing.");
        bool tokenizer = File.Exists(Path.Combine(folder, "tokenizer.json")) || File.Exists(Path.Combine(folder, "tokenizer.model"))
            || File.Exists(Path.Combine(folder, "tokenizer_config.json"));
        if (!tokenizer) result.Warnings.Add("No tokenizer files were found in the folder.");
        if (File.Exists(Path.Combine(folder, "adapter_config.json")) && !File.Exists(Path.Combine(folder, "config.json")))
            result.Problems.Add("This is a LoRA adapter folder, not a merged model. Merge it into the base model first.");

        var shards = new List<string>();
        string index = Path.Combine(folder, "model.safetensors.index.json");
        if (File.Exists(index))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(index));
                if (doc.RootElement.TryGetProperty("weight_map", out JsonElement map))
                    foreach (string file in map.EnumerateObject().Select(p => p.Value.GetString() ?? string.Empty).Distinct())
                    {
                        if (file.Length == 0 || file.Contains("..") || Path.IsPathRooted(file)) { result.Problems.Add("The shard index names an unsafe path."); continue; }
                        string full = Path.Combine(folder, file);
                        if (!File.Exists(full)) result.Problems.Add("Shard listed in the index is missing: " + file);
                        else shards.Add(full);
                    }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            { result.Problems.Add("model.safetensors.index.json could not be read."); }
        }
        else shards.AddRange(Directory.EnumerateFiles(folder, "*.safetensors"));
        if (shards.Count == 0) result.Problems.Add("No safetensors weight files were found.");

        bool allSixteen = shards.Count > 0;
        var types = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string shard in shards)
        {
            TrainingExportVerification one = VerifySafetensors(shard);
            result.TensorCount += one.TensorCount;
            result.ParameterCount += one.ParameterCount;
            result.TotalBytes += one.TotalBytes;
            foreach (string type in one.DataTypes) types.Add(type);
            allSixteen &= one.SixteenBit;
            foreach (string problem in one.Problems) result.Problems.Add(Path.GetFileName(shard) + ": " + problem);
        }
        result.DataTypes = types.ToList();
        result.SixteenBit = allSixteen;
        return Finish(result);
    }

    public static TrainingExportVerification VerifyGguf(string path)
    {
        var result = new TrainingExportVerification { Kind = "gguf" };
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            result.TotalBytes = stream.Length;
            Span<byte> head = stackalloc byte[24];
            if (stream.Length < 24 || stream.Read(head) != 24) { result.Problems.Add("The file is too small to be a GGUF."); return Finish(result); }
            if (head[0] != 'G' || head[1] != 'G' || head[2] != 'U' || head[3] != 'F') { result.Problems.Add("Missing GGUF magic."); return Finish(result); }
            uint version = BitConverter.ToUInt32(head[4..8]);
            ulong tensors = BitConverter.ToUInt64(head[8..16]);
            ulong metadata = BitConverter.ToUInt64(head[16..24]);
            if (version is < 2 or > 3) result.Problems.Add("Unsupported GGUF version " + version + ".");
            if (tensors == 0 || tensors > 1_000_000) result.Problems.Add("The GGUF tensor count is implausible.");
            if (metadata == 0 || metadata > 1_000_000) result.Problems.Add("The GGUF metadata count is implausible.");
            result.TensorCount = (int)Math.Min(tensors, int.MaxValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { result.Problems.Add("The file could not be read."); }
        return Finish(result);
    }

    public static TrainingExportVerification Verify(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return Finish(new TrainingExportVerification { Kind = "unknown", Problems = { "Give an absolute local path." } });
        if (Directory.Exists(path)) return VerifyModelFolder(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (!File.Exists(path)) return Finish(new TrainingExportVerification { Kind = "unknown", Problems = { "The path does not exist." } });
        return ext switch
        {
            ".safetensors" => VerifySafetensors(path),
            ".gguf" => VerifyGguf(path),
            _ => Finish(new TrainingExportVerification { Kind = "unknown", Problems = { "Only .safetensors, .gguf or a model folder can be verified." } })
        };
    }

    private static TrainingExportVerification Finish(TrainingExportVerification result)
    {
        result.Valid = result.Problems.Count == 0;
        return result;
    }
}
