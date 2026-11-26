using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Competitor-parity training slice: admission, GSPO, GPU sampler, export verification, imatrix,
/// document recipes and provider presets. Everything here is hermetic; no GPU or Python is needed.</summary>
public sealed class TrainingParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-parity-" + Guid.NewGuid().ToString("N"));

    public TrainingParityTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---------------------------------------------------------------- precision admission

    [Fact]
    public void Fp8_is_refused_with_a_reason_everywhere_and_never_advertised()
    {
        Assert.DoesNotContain("fp8", TrainingPrecision.All);
        var ex = Assert.Throws<InvalidDataException>(() => TrainingPrecision.Normalize("FP8"));
        Assert.Contains("FP8", ex.Message);
        Assert.Equal(TrainingPrecision.Fp8RefusalReason, TrainingPrecisionAdmission.Fp8RefusalReason);
        Assert.Equal(TrainingPrecision.Fp8RefusalReason,
            TrainingPrecisionAdmission.Refusal("sft", "fp8", "meta-llama/Llama-3.2-1B", false));
    }

    [Theory]
    [InlineData("sft", "full-16bit", "Qwen/Qwen2.5-0.5B", false, null)]
    [InlineData("continued-pretraining", "full-16bit", "Qwen/Qwen2.5-0.5B", false, null)]
    [InlineData("dpo", "full-16bit", "Qwen/Qwen2.5-0.5B", false, "validated only")]
    [InlineData("sft", "full-16bit", "Qwen/Qwen2.5-0.5B", true, "DoRA")]
    [InlineData("sft", "full-16bit", "unsloth/Qwen2.5-0.5B-bnb-4bit", false, "unquantized")]
    [InlineData("embedding-ft", "qlora-4bit", "BAAI/bge-small-en-v1.5", false, "LoRA 16-bit")]
    [InlineData("embedding-ft", "lora-16bit", "BAAI/bge-small-en-v1.5", true, null)]
    [InlineData("vision-sft", "qlora-4bit", "unsloth/Qwen2-VL-2B", true, "text LLM")]
    [InlineData("sft", "lora-16bit", "Qwen/Qwen2.5-0.5B", true, null)]
    public void Admission_matrix(string method, string precision, string model, bool dora, string? expectedFragment)
    {
        string? refusal = TrainingPrecisionAdmission.Refusal(method, precision, model, dora);
        if (expectedFragment is null) Assert.Null(refusal);
        else Assert.Contains(expectedFragment, refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Full_finetune_footprint_is_much_larger_than_qlora_and_is_checked_against_vram()
    {
        Assert.False(TrainingPrecisionAdmission.FullFineTuneFits(7, 16));     // 7B full fine-tune never fits 16 GiB
        Assert.True(TrainingPrecisionAdmission.FullFineTuneFits(0.5, 16));
        Assert.False(TrainingPrecisionAdmission.FullFineTuneFits(0.5, 0));    // unknown VRAM is not "fits"
    }

    [Fact]
    public void Planner_blocks_full_finetune_that_cannot_fit_and_dora_on_full()
    {
        var shape = TrainingDatasetShape.Empty;
        var plan = TrainingRunPlanner.Plan(shape, "sft", "Qwen/Qwen2.5-7B", 16, 60, 2e-4, 16, "full-16bit");
        Assert.False(plan.CanStart);
        var dora = TrainingRunPlanner.Plan(shape, "sft", "Qwen/Qwen2.5-0.5B", 16, 60, 2e-4, 16, "full-16bit", useDora: true);
        Assert.Contains(dora.Findings, f => f.Message.Contains("DoRA", StringComparison.Ordinal));
    }

    [Fact]
    public void Embedding_rows_are_recognised_and_need_two_pairs()
    {
        string path = Path.Combine(_dir, "emb.jsonl");
        File.WriteAllLines(path, new[]
        {
            "{\"query\":\"what is a cat\",\"positive\":\"a small feline\"}",
            "{\"query\":\"what is a dog\",\"positive\":\"a canine\",\"negative\":\"a feline\"}",
        });
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(path);
        Assert.True(shape.SupportsEmbedding);
        Assert.True(TrainingRunPlanner.Supports(shape, "embedding-ft"));
        Assert.False(TrainingRunPlanner.Supports(shape, "sft"));
    }

    // ---------------------------------------------------------------- GSPO

    [Fact]
    public void Gspo_is_a_grpo_option_that_other_methods_reject_and_the_runner_guards()
    {
        Assert.Equal("sequence", GrpoImportanceSampling.Normalize(" Sequence "));
        Assert.Throws<InvalidDataException>(() => GrpoImportanceSampling.Normalize("batch"));

        var request = new TrainingJobRequest { BaseModel = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit", GrpoImportanceSampling = "sequence" };
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateRequest(request, TrainingMethod.Sft));
        TrainingLabService.ValidateRequest(request, TrainingMethod.Grpo); // does not throw

        string script = (string)typeof(TrainingLabService)
            .GetField("TrainScriptSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;
        Assert.Contains("importance_sampling_level", script, StringComparison.Ordinal);
        Assert.Contains("does not support GSPO", script, StringComparison.Ordinal);
        Assert.Contains("num_iterations", script, StringComparison.Ordinal);

        var status = new TrainingJobStatus
        {
            Id = "abc",
            Method = TrainingMethod.Sft,
            Precision = TrainingPrecision.Full16Bit,
            OutputModelPath = @"C:\tmp\full-model"
        };
        string json = JsonSerializer.Serialize(status);
        TrainingJobStatus roundTrip = JsonSerializer.Deserialize<TrainingJobStatus>(json)!;
        Assert.Equal(TrainingPrecision.Full16Bit, roundTrip.Precision);
        Assert.Equal(@"C:\tmp\full-model", roundTrip.OutputModelPath);
    }

    [Fact]
    public void Runner_script_has_full_finetune_dora_and_embedding_paths()
    {
        string script = (string)typeof(TrainingLabService)
            .GetField("TrainScriptSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;
        Assert.Contains("full_finetuning=full_finetune", script, StringComparison.Ordinal);
        Assert.Contains("use_dora", script, StringComparison.Ordinal);
        Assert.Contains("method == \"embedding-ft\"", script.Replace("elif method == \"embedding-ft\"", "method == \"embedding-ft\""), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- GPU sampler

    [Fact]
    public async Task Gpu_sampler_never_blocks_never_invents_zero_and_expires_stale_samples()
    {
        DateTime now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var devices = new List<GpuPerformanceTelemetryDto> { new() { Index = 0, UtilizationPercent = 73, MemoryUsedMib = 5120 } };
        var sampler = new TrainingGpuSampler(
            _ => Task.FromResult<GpuPerformanceTelemetrySnapshot?>(new GpuPerformanceTelemetrySnapshot(now, devices)),
            () => now);

        Assert.Null(sampler.Read());                 // first read only schedules a refresh
        await sampler.RefreshAsync();
        now = now.AddSeconds(1);
        var sample = sampler.Read();
        Assert.Equal(73, sample!.Value.GpuUtilPercent);
        Assert.Equal(5120, sample.Value.VramMib);

        now = now.Add(TrainingGpuSampler.MaxAge).AddSeconds(1);
        Assert.Null(sampler.Read());                 // stale -> a gap, not a stale number
    }

    [Fact]
    public async Task Gpu_sampler_reports_nothing_when_no_sensor_answers()
    {
        var sampler = new TrainingGpuSampler(_ => Task.FromResult<GpuPerformanceTelemetrySnapshot?>(null));
        _ = sampler.Read();
        await sampler.RefreshAsync();
        Assert.Null(sampler.Read());
    }

    // ---------------------------------------------------------------- export verification

    private static byte[] Safetensors(string headerJson, int dataBytes)
    {
        byte[] header = Encoding.UTF8.GetBytes(headerJson);
        var all = new byte[8 + header.Length + dataBytes];
        BitConverter.GetBytes((ulong)header.Length).CopyTo(all, 0);
        header.CopyTo(all, 8);
        return all;
    }

    [Fact]
    public void Safetensors_16bit_file_verifies_and_truncation_is_detected()
    {
        string json = "{\"a\":{\"dtype\":\"BF16\",\"shape\":[2,4],\"data_offsets\":[0,16]},\"b\":{\"dtype\":\"F16\",\"shape\":[4],\"data_offsets\":[16,24]}}";
        string good = Path.Combine(_dir, "good.safetensors");
        File.WriteAllBytes(good, Safetensors(json, 24));
        var ok = TrainingExportVerifier.VerifySafetensors(good);
        Assert.True(ok.Valid, string.Join("; ", ok.Problems));
        Assert.True(ok.SixteenBit);
        Assert.Equal(12, ok.ParameterCount);
        Assert.Equal(2, ok.TensorCount);

        string cut = Path.Combine(_dir, "cut.safetensors");
        File.WriteAllBytes(cut, Safetensors(json, 20));
        Assert.False(TrainingExportVerifier.VerifySafetensors(cut).Valid);

        string f32 = Path.Combine(_dir, "f32.safetensors");
        File.WriteAllBytes(f32, Safetensors("{\"a\":{\"dtype\":\"F32\",\"shape\":[2],\"data_offsets\":[0,8]}}", 8));
        var full = TrainingExportVerifier.VerifySafetensors(f32);
        Assert.True(full.Valid);
        Assert.False(full.SixteenBit);

        string wrongSize = Path.Combine(_dir, "wrong.safetensors");
        File.WriteAllBytes(wrongSize, Safetensors("{\"a\":{\"dtype\":\"F32\",\"shape\":[3],\"data_offsets\":[0,8]}}", 8));
        Assert.False(TrainingExportVerifier.VerifySafetensors(wrongSize).Valid);
    }

    [Fact]
    public void Model_folder_needs_config_weights_and_every_indexed_shard()
    {
        string folder = Path.Combine(_dir, "merged");
        Directory.CreateDirectory(folder);
        Assert.False(TrainingExportVerifier.VerifyModelFolder(folder).Valid);

        File.WriteAllText(Path.Combine(folder, "config.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "tokenizer.json"), "{}");
        File.WriteAllBytes(Path.Combine(folder, "model-00001-of-00002.safetensors"),
            Safetensors("{\"a\":{\"dtype\":\"F16\",\"shape\":[2],\"data_offsets\":[0,4]}}", 4));
        File.WriteAllText(Path.Combine(folder, "model.safetensors.index.json"),
            "{\"weight_map\":{\"a\":\"model-00001-of-00002.safetensors\",\"b\":\"model-00002-of-00002.safetensors\"}}");
        var missing = TrainingExportVerifier.VerifyModelFolder(folder);
        Assert.False(missing.Valid);
        Assert.Contains(missing.Problems, p => p.Contains("00002", StringComparison.Ordinal));

        File.WriteAllBytes(Path.Combine(folder, "model-00002-of-00002.safetensors"),
            Safetensors("{\"b\":{\"dtype\":\"F16\",\"shape\":[2],\"data_offsets\":[0,4]}}", 4));
        var complete = TrainingExportVerifier.VerifyModelFolder(folder);
        Assert.True(complete.Valid, string.Join("; ", complete.Problems));
        Assert.True(complete.SixteenBit);
    }

    [Fact]
    public void Gguf_header_is_checked_and_adapter_folders_are_not_mistaken_for_merged_models()
    {
        string gguf = Path.Combine(_dir, "m.gguf");
        var bytes = new byte[64];
        Encoding.ASCII.GetBytes("GGUF").CopyTo(bytes, 0);
        BitConverter.GetBytes(3u).CopyTo(bytes, 4);
        BitConverter.GetBytes(10UL).CopyTo(bytes, 8);
        BitConverter.GetBytes(20UL).CopyTo(bytes, 16);
        File.WriteAllBytes(gguf, bytes);
        Assert.True(TrainingExportVerifier.VerifyGguf(gguf).Valid);

        File.WriteAllBytes(gguf, new byte[64]);
        Assert.False(TrainingExportVerifier.VerifyGguf(gguf).Valid);

        string adapter = Path.Combine(_dir, "adapter");
        Directory.CreateDirectory(adapter);
        File.WriteAllText(Path.Combine(adapter, "adapter_config.json"), "{}");
        var result = TrainingExportVerifier.VerifyModelFolder(adapter);
        Assert.False(result.Valid);
        Assert.Contains(result.Problems, p => p.Contains("adapter", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- imatrix / quantize

    private string Quantizer()
    {
        string path = Path.Combine(_dir, "llama-quantize.exe");
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A });
        return path;
    }

    private string Gguf(string name)
    {
        string path = Path.Combine(_dir, name);
        var data = new byte[4096];
        Encoding.ASCII.GetBytes("GGUF").CopyTo(data, 0);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public async Task Quantize_passes_the_imatrix_and_records_its_hash()
    {
        string imatrix = Path.Combine(_dir, "calib.imatrix");
        File.WriteAllBytes(imatrix, new byte[] { 1, 2, 3, 4 });
        string input = Gguf("in.gguf"), output = Path.Combine(_dir, "out.gguf");
        IReadOnlyList<string>? args = null;
        var service = new GgufQuantizeService(() => Quantizer(), (spec, _) =>
        {
            args = spec.Arguments;
            File.WriteAllBytes(spec.Arguments[^2], new byte[] { (byte)'G', (byte)'G', (byte)'U', (byte)'F', 0, 0, 0, 0 });
            return Task.FromResult((0, "ok"));
        }, _ => long.MaxValue, TimeSpan.FromMinutes(1));

        await service.QuantizeAsync(new GgufQuantizeRequest(input, output, "IQ2_XS", imatrix), CancellationToken.None);

        Assert.Equal("--imatrix", args![0]);
        Assert.Equal(imatrix, args[1]);
        Assert.Equal("IQ2_XS", args[^1]);
        using var manifest = JsonDocument.Parse(File.ReadAllText(output + ".quant.json"));
        Assert.Equal("calib.imatrix", manifest.RootElement.GetProperty("imatrix").GetString());
        Assert.Equal(64, manifest.RootElement.GetProperty("imatrixSha256").GetString()!.Length);
    }

    [Fact]
    public async Task Very_low_bit_types_are_refused_without_an_imatrix()
    {
        var service = new GgufQuantizeService(() => Quantizer(), (_, _) => Task.FromResult((0, "")), _ => long.MaxValue,
            TimeSpan.FromMinutes(1));
        var ex = await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(
            new GgufQuantizeRequest(Gguf("in2.gguf"), Path.Combine(_dir, "o2.gguf"), "IQ1_S"), CancellationToken.None));
        Assert.Contains("importance matrix", ex.Message);
    }

    [Fact]
    public async Task Imatrix_generation_is_honestly_unavailable_without_llama_imatrix_and_works_with_it()
    {
        var absent = new GgufQuantizeService(() => Quantizer(), (_, _) => Task.FromResult((0, "")), _ => long.MaxValue,
            TimeSpan.FromMinutes(1));
        Assert.False(absent.CanGenerateImatrix);
        await Assert.ThrowsAsync<GgufQuantizeException>(() => absent.GenerateImatrixAsync(
            new GgufImatrixRequest(Gguf("m.gguf"), Path.Combine(_dir, "c.txt"), Path.Combine(_dir, "o.imatrix")), CancellationToken.None));

        string tool = Path.Combine(_dir, "llama-imatrix.exe");
        File.WriteAllBytes(tool, new byte[] { 0x4D, 0x5A });
        string calibration = Path.Combine(_dir, "calib.txt");
        File.WriteAllText(calibration, new string('a', 2000));
        var present = new GgufQuantizeService(() => Quantizer(), (spec, _) =>
        {
            File.WriteAllBytes(spec.Arguments[^1], new byte[] { 9, 9, 9 });
            return Task.FromResult((0, "ok"));
        }, _ => long.MaxValue, TimeSpan.FromMinutes(1), () => tool);
        Assert.True(present.CanGenerateImatrix);
        string output = Path.Combine(_dir, "out.imatrix");
        var result = await present.GenerateImatrixAsync(new GgufImatrixRequest(Gguf("m2.gguf"), calibration, output), CancellationToken.None);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(output + ".partial"));
        Assert.Equal(3, result.OutputBytes);
        await Assert.ThrowsAsync<GgufQuantizeException>(() => present.GenerateImatrixAsync(
            new GgufImatrixRequest(Gguf("m3.gguf"), calibration, output), CancellationToken.None)); // never overwrites
    }

    // ---------------------------------------------------------------- document recipes

    [Fact]
    public async Task Document_recipe_previews_without_writing_and_then_writes_only_to_the_managed_root()
    {
        string source = Path.Combine(_dir, "notes.md");
        File.WriteAllText(source, string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i}. " + new string('x', 300))));
        string root = Path.Combine(_dir, "out");

        var preview = await TrainingDocumentRecipe.BuildAsync(
            new TrainingDocumentRecipeRequest { SourcePath = source, ChunkCharacters = 1000, OverlapCharacters = 100 }, root, CancellationToken.None);
        Assert.True(preview.Rows > 5);
        Assert.Equal(5, preview.Preview.Count);
        Assert.Null(preview.TrainJsonlPath);
        Assert.False(Directory.Exists(root));

        var written = await TrainingDocumentRecipe.BuildAsync(
            new TrainingDocumentRecipeRequest { SourcePath = source, ChunkCharacters = 1000, OverlapCharacters = 100, PreviewOnly = false },
            root, CancellationToken.None);
        string[] lines = File.ReadAllLines(written.TrainJsonlPath!);
        Assert.Equal(written.Rows, lines.Length);
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(JsonDocument.Parse(line).RootElement.GetProperty("text").GetString())));
        Assert.StartsWith(root, written.TrainJsonlPath);
        // and the result is a valid continued-pretraining dataset for the trainer's own shape check
        Assert.True(TrainingDatasetShape.Inspect(written.TrainJsonlPath!).SupportsContinuedPretraining);
    }

    [Fact]
    public async Task Document_recipe_refuses_tiny_documents_unsupported_formats_and_bad_settings()
    {
        string tiny = Path.Combine(_dir, "tiny.txt");
        File.WriteAllText(tiny, "too short");
        await Assert.ThrowsAsync<InvalidDataException>(() => TrainingDocumentRecipe.BuildAsync(
            new TrainingDocumentRecipeRequest { SourcePath = tiny }, null, CancellationToken.None));

        string exe = Path.Combine(_dir, "x.exe");
        File.WriteAllBytes(exe, new byte[100]);
        await Assert.ThrowsAsync<InvalidDataException>(() => TrainingDocumentRecipe.BuildAsync(
            new TrainingDocumentRecipeRequest { SourcePath = exe }, null, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidDataException>(() => TrainingDocumentRecipe.BuildAsync(
            new TrainingDocumentRecipeRequest { SourcePath = tiny, ChunkCharacters = 10 }, null, CancellationToken.None));

        string notPdf = Path.Combine(_dir, "broken.pdf");
        File.WriteAllText(notPdf, "this is not a pdf");
        await Assert.ThrowsAsync<InvalidDataException>(() => TrainingDocumentRecipe.BuildAsync(
            new TrainingDocumentRecipeRequest { SourcePath = notPdf }, null, CancellationToken.None));
    }

    [Fact]
    public void Chunking_always_makes_progress_and_respects_the_size()
    {
        string text = string.Concat(Enumerable.Repeat("Sentence number one. ", 400));
        var chunks = TrainingDocumentRecipe.Chunk(text, 600, 100);
        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, 600));
        Assert.EndsWith(".", chunks[0]);
    }

    [Fact]
    public async Task Tabular_recipe_preview_validates_everything_and_writes_nothing()
    {
        string csv = Path.Combine(_dir, "qa.csv");
        File.WriteAllLines(csv, new[] { "q,a" }.Concat(Enumerable.Range(1, 10).Select(i => $"question {i},answer {i}")));
        string root = Path.Combine(_dir, "recipes");
        var curation = new DatasetCurationService { TrainingRecipeTestOutputRoot = root };
        var result = await curation.CreateTrainingRecipeAsync(new TrainingDatasetRecipeRequest
        {
            SourcePath = csv, Method = "sft", PromptColumn = "q", ResponseColumn = "a", PreviewOnly = true
        }, CancellationToken.None);
        Assert.Equal(10, result.WrittenRows);
        Assert.NotEmpty(result.Preview);
        Assert.Equal(string.Empty, result.TrainJsonlPath);
        Assert.False(Directory.Exists(root));
    }

    // ---------------------------------------------------------------- provider presets

    [Fact]
    public void Provider_presets_cover_vllm_ollama_lmstudio_and_pass_the_loopback_validation()
    {
        Assert.Equal(new[] { "vllm", "ollama", "lmstudio" }, OpenAiCompatibleProviderPresets.All.Select(p => p.Id));
        foreach (var preset in OpenAiCompatibleProviderPresets.All)
        {
            Assert.True(OpenAiCompatibleProviderService.TryNormalizeBaseUrl(preset.BaseUrl, out _, out string error), error);
            Assert.False(string.IsNullOrWhiteSpace(preset.NoteIt));
        }
    }

    [Fact]
    public void Preset_fills_blanks_but_never_overrides_a_typed_address_and_rejects_unknown_ids()
    {
        var blank = new OpenAiCompatibleProviderRequest { PresetId = "Ollama" };
        Assert.True(OpenAiCompatibleProviderPresets.TryApply(blank, out _));
        Assert.Equal("http://127.0.0.1:11434/v1", blank.BaseUrl);
        Assert.Equal("Ollama", blank.DisplayName);

        var typed = new OpenAiCompatibleProviderRequest { PresetId = "vllm", BaseUrl = "http://192.168.1.20:9000" };
        Assert.True(OpenAiCompatibleProviderPresets.TryApply(typed, out _));
        Assert.Equal("http://192.168.1.20:9000", typed.BaseUrl);

        Assert.False(OpenAiCompatibleProviderPresets.TryApply(new OpenAiCompatibleProviderRequest { PresetId = "openai-cloud" }, out string? error));
        Assert.Contains("Unknown", error);
    }
}
