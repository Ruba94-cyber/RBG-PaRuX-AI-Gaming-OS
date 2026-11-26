namespace PaRuX.Contracts;

// Training Lab is entirely PaRuX-managed: Python/uv/venv live under PaRuXDataRoot, never the
// system PATH. Methods are closed strings because they are a wire/API surface used by Windows,
// Android and CLI, while the Python runner is the execution source of truth.

public sealed class TrainingEngineStatus
{
    public bool Installed { get; set; }
    /// <summary>True only when the pinned PaRuX runtime identity is present and at least one method
    /// is genuinely supported by the detected execution backend.</summary>
    public bool Ready { get; set; }
    public string? PythonVersion { get; set; }
    public string? UnslothVersion { get; set; }
    public string? TorchVersion { get; set; }
    /// <summary>Legacy compatibility bit. True only for the native CUDA backend; callers should use
    /// Backend + AvailableMethods for new capability decisions.</summary>
    public bool CudaVerified { get; set; }
    /// <summary>cuda | rocm-cuda-api | cpu | xpu-unvalidated | unknown.</summary>
    public string Backend { get; set; } = "unknown";
    /// <summary>SHA-256 of the canonical complete resolved pip-freeze captured at installation.</summary>
    public string? ResolvedPackagesSha256 { get; set; }
    public string Detail { get; set; } = string.Empty;
    public List<string> AvailableMethods { get; set; } = new();
    /// <summary>Honest hardware/backend classification: cuda-validated | rocm-cuda-api |
    /// cpu-tts-only | accelerator-unvalidated | not-installed | unknown. Never a promise that an
    /// unlisted accelerator "will work".</summary>
    public string HardwareProfile { get; set; } = "unknown";
    /// <summary>Every method that is NOT advertised on this backend with the concrete reason, so an
    /// unsupported profile (for example an AMD iGPU on Windows) is recorded instead of hidden.</summary>
    public List<TrainingUnsupportedMethod> UnsupportedMethods { get; set; } = new();
}

public sealed class TrainingUnsupportedMethod
{
    public string Method { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Outcome of removing the managed training runtime. Provisioning several gigabytes on
/// demand is only acceptable if the disk can be handed back.</summary>
public sealed class TrainingEngineRemovalResult
{
    public bool Removed { get; set; }
    public long FreedBytes { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public static class TrainingMethod
{
    public const string Sft = "sft";
    public const string Dpo = "dpo";
    public const string Orpo = "orpo";
    public const string Kto = "kto";
    public const string ContinuedPretraining = "continued-pretraining";
    public const string Grpo = "grpo";
    public const string VisionSft = "vision-sft";
    public const string TtsSft = "tts-sft";
    /// <summary>Contrastive (in-batch-negatives) LoRA fine-tune of an embedding / retrieval model.
    /// Rows are {query, positive[, negative]}; the output is a PEFT adapter that merges into the encoder.</summary>
    public const string EmbeddingFt = "embedding-ft";
    public static readonly string[] All = { Sft, Dpo, Orpo, Kto, ContinuedPretraining, Grpo, VisionSft, TtsSft, EmbeddingFt };

    public static string Normalize(string? value)
    {
        string method = string.IsNullOrWhiteSpace(value) ? Sft : value.Trim().ToLowerInvariant();
        if (!All.Contains(method, StringComparer.Ordinal))
            throw new InvalidDataException("Unknown training method. Known methods: " + string.Join(", ", All) + ".");
        return method;
    }
}

/// <summary>
/// PaRuX's supported LLM base loading modes. 16-bit full fine-tuning is admitted only by the VRAM
/// policy (<c>TrainingPrecisionAdmission</c>); FP8 stays deliberately unadvertised until the native
/// runner, hardware admission and benchmarks exist.
/// </summary>
public static class TrainingPrecision
{
    public const string Qlora4Bit = "qlora-4bit";
    public const string Lora16Bit = "lora-16bit";
    /// <summary>Every weight is trained in bf16/fp16 (Unsloth <c>full_finetuning</c>). The result is a
    /// complete model directory, not a LoRA adapter.</summary>
    public const string Full16Bit = "full-16bit";
    public static readonly string[] All = { Qlora4Bit, Lora16Bit, Full16Bit };
    /// <summary>Recognised only so it can be refused with a reason. It is NOT in <see cref="All"/>.</summary>
    public const string Fp8 = "fp8";

    public const string Fp8RefusalReason =
        "FP8 training is not available in PaRuX: it needs an Ada/Hopper/Blackwell GPU (compute capability 8.9 or newer) "
        + "plus a validated FP8 training runner, and PaRuX has neither verified that runner nor benchmarked it. "
        + "Use QLoRA 4-bit, LoRA 16-bit or full 16-bit fine-tuning.";

    public static string Normalize(string? value)
    {
        string precision = string.IsNullOrWhiteSpace(value) ? Qlora4Bit : value.Trim().ToLowerInvariant();
        if (string.Equals(precision, Fp8, StringComparison.Ordinal))
            throw new InvalidDataException(Fp8RefusalReason);
        if (!All.Contains(precision, StringComparer.Ordinal))
            throw new InvalidDataException("Unsupported training precision: " + precision + ".");
        return precision;
    }
}

/// <summary>GRPO importance-sampling granularity. <c>sequence</c> is GSPO (Group Sequence Policy Optimization):
/// the importance ratio is computed per sequence instead of per token. It is available only when the installed
/// TRL exposes <c>importance_sampling_level</c>; the runner fails with a clear error otherwise.</summary>
public static class GrpoImportanceSampling
{
    public const string Token = "token";
    public const string Sequence = "sequence";
    public static readonly string[] All = { Token, Sequence };

    public static string Normalize(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? Token : value.Trim().ToLowerInvariant();
        if (!All.Contains(normalized, StringComparer.Ordinal))
            throw new InvalidDataException("Unknown GRPO importance sampling level. Known levels: " + string.Join(", ", All) + ".");
        return normalized;
    }
}

public static class TrainingTargetPreset
{
    public const string Attention = "attention";
    public const string AllLinear = "all-linear";
    public static readonly string[] All = { Attention, AllLinear };
    public static string Normalize(string? value)
    {
        string preset = string.IsNullOrWhiteSpace(value) ? Attention : value.Trim().ToLowerInvariant();
        if (!All.Contains(preset, StringComparer.Ordinal))
            throw new InvalidDataException("Unsupported LoRA target preset: " + preset + ".");
        return preset;
    }
}

public static class GrpoRewardMode
{
    public const string ExactReference = "exact-reference";
    public const string ContainsReference = "contains-reference";
    public const string XmlFormat = "xml-format";
    public const string JsonFormat = "json-format";
    public static readonly string[] All = { ExactReference, ContainsReference, XmlFormat, JsonFormat };

    public static string Normalize(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? ExactReference : value.Trim().ToLowerInvariant();
        if (!All.Contains(normalized, StringComparer.Ordinal))
            throw new InvalidDataException("Unknown GRPO reward mode. Known modes: " + string.Join(", ", All) + ".");
        return normalized;
    }
}

/// <summary>
/// GPU execution strategy for local Training Lab runs. Defaults to single-GPU for full backwards
/// compatibility. Multi-GPU strategies require at least two detected GPUs and are validated both
/// by the preflight planner and at job admission.
/// </summary>
public static class TrainingMultiGpuStrategy
{
    public const string Single = "single";
    /// <summary>Model-parallel split across 2+ GPUs via <c>device_map="auto"</c> and per-GPU <c>max_memory</c>,
    /// allowing models larger than a single GPU's VRAM to fit across multiple cards.</summary>
    public const string DeviceMapAuto = "device-map-auto";
    /// <summary>Distributed Data Parallel (DDP) across 2+ GPUs via <c>torchrun --standalone --nproc_per_node=N</c>,
    /// replicating the model on each GPU to scale effective batch size and throughput.</summary>
    public const string DataParallel = "data-parallel";
    public static readonly string[] All = { Single, DeviceMapAuto, DataParallel };

    public static string Normalize(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? Single : value.Trim().ToLowerInvariant();
        if (!All.Contains(normalized, StringComparer.Ordinal))
            throw new InvalidDataException("Unknown multi-GPU strategy. Known strategies: " + string.Join(", ", All) + ".");
        return normalized;
    }
}

public sealed class TrainingJobRequest
{
    /// <summary>
    /// JSONL dataset. SFT: {messages:[...]}; DPO/ORPO: {prompt,chosen,rejected};
    /// KTO: {prompt,completion,label:true|false}; continued pretraining:
    /// {text}; GRPO: {prompt,reference} for reference rewards (format rewards need prompt only);
    /// Vision SFT: {image,messages:[...]}; TTS SFT: {audio,text}.
    /// Relative media paths are resolved from the JSONL directory.
    /// </summary>
    public string TrainJsonlPath { get; set; } = string.Empty;
    /// <summary>Optional SHA-256 from a complete Quick dataset preflight. If supplied,
    /// job admission fails if the file changed, rather than silently training new bytes.</summary>
    public string? ExpectedDatasetSha256 { get; set; }
    public string BaseModel { get; set; } = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit";
    public int LoraRank { get; set; } = 16;
    public int MaxSteps { get; set; } = 60;
    public double LearningRate { get; set; } = 0.0002;
    public string Method { get; set; } = TrainingMethod.Sft;
    /// <summary>Preference objective strength for DPO, ORPO and KTO.</summary>
    public double DpoBeta { get; set; } = 0.1;
    public int MaxSequenceLength { get; set; } = 2048;
    /// <summary>4-bit QLoRA by default; 16-bit LoRA uses more memory and is opt-in.</summary>
    public string Precision { get; set; } = TrainingPrecision.Qlora4Bit;
    /// <summary>Attention projection adapters or all MLP + attention projections.</summary>
    public string TargetPreset { get; set; } = TrainingTargetPreset.Attention;
    /// <summary>Optional SFT-only sequence packing; continued pretraining always packs.</summary>
    public bool PackSequences { get; set; }
    /// <summary>Weight-decomposed LoRA (DoRA). Ignored by full fine-tuning; slower and heavier than plain LoRA.</summary>
    public bool UseDora { get; set; }
    /// <summary>Unsloth offloaded gradient checkpointing. On by default; turning it off is only safe when
    /// the activations clearly fit (the Teach-your-model policy decides that from detected VRAM).</summary>
    public bool GradientCheckpointing { get; set; } = true;

    // GRPO is deliberately restricted to built-in deterministic reward functions. Training data may
    // not inject Python/code into the managed runner.
    public string GrpoRewardMode { get; set; } = Contracts.GrpoRewardMode.ExactReference;
    public int GrpoGenerations { get; set; } = 4;
    /// <summary>"token" (default GRPO) or "sequence" (GSPO).</summary>
    public string GrpoImportanceSampling { get; set; } = Contracts.GrpoImportanceSampling.Token;

    // Generic field names are configurable only as JSON property names, never expressions/scripts.
    public string TextField { get; set; } = "text";
    public string ImageField { get; set; } = "image";
    public string AudioField { get; set; } = "audio";

    /// <summary>GPU execution strategy: "single" (default), "device-map-auto" (model parallel), or "data-parallel" (DDP).</summary>
    public string MultiGpuStrategy { get; set; } = TrainingMultiGpuStrategy.Single;
    /// <summary>Optional non-negative CUDA device indices (e.g. [0, 1]). Null/empty uses automatic GPU selection.</summary>
    public int[]? GpuDeviceIds { get; set; }
}

public sealed class TrainingJobStatus
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "Queued";
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public double? LastLoss { get; set; }
    public double? LastGradientNorm { get; set; }
    public double? LastLearningRate { get; set; }
    public double? LastEpoch { get; set; }
    public List<TrainingMetricSample> History { get; set; } = new();
    public string? Error { get; set; }
    public string? AdapterPath { get; set; }
    /// <summary>Complete Hugging Face model folder produced by full 16-bit fine-tuning (not a PEFT adapter).</summary>
    public string? OutputModelPath { get; set; }
    /// <summary>Base precision / mode used for this job (qlora-4bit, lora-16bit, full-16bit).</summary>
    public string? Precision { get; set; }
    public string Method { get; set; } = TrainingMethod.Sft;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class TrainingMetricSample
{
    public int Step { get; set; }
    public double? Loss { get; set; }
    public double? GradientNorm { get; set; }
    public double? LearningRate { get; set; }
    public double? Epoch { get; set; }
    /// <summary>Throughput when the trainer logs it; null when unknown (never zero-filled).</summary>
    public double? TokensPerSecond { get; set; }
    /// <summary>Sampled GPU utilisation 0-100; null when no sampler could read it.</summary>
    public double? GpuUtilPercent { get; set; }
    /// <summary>Sampled VRAM in use, MiB; null when no sampler could read it.</summary>
    public double? VramMib { get; set; }
}

public sealed class TrainingPlanRequest
{
    public string TrainJsonlPath { get; set; } = string.Empty;
    public string Method { get; set; } = TrainingMethod.Sft;
    public string BaseModel { get; set; } = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit";
    public int LoraRank { get; set; } = 16;
    public int MaxSteps { get; set; } = 60;
    public double LearningRate { get; set; } = 0.0002;
    public int MaxSequenceLength { get; set; } = 2048;
    /// <summary>4-bit QLoRA by default; 16-bit LoRA uses more memory and is opt-in.</summary>
    public string Precision { get; set; } = TrainingPrecision.Qlora4Bit;
    /// <summary>Attention projection adapters or all MLP + attention projections.</summary>
    public string TargetPreset { get; set; } = TrainingTargetPreset.Attention;
    /// <summary>Optional SFT-only sequence packing; continued pretraining always packs.</summary>
    public bool PackSequences { get; set; }
    public bool UseDora { get; set; }
    /// <summary>Opt in to a bounded full-file SFT/DPO scan instead of only 2,000 rows.
    /// Intended for Quick Training; Advanced can continue using sampled previews.</summary>
    public bool ValidateEntireDataset { get; set; }
    public string GrpoRewardMode { get; set; } = Contracts.GrpoRewardMode.ExactReference;
    public string MultiGpuStrategy { get; set; } = TrainingMultiGpuStrategy.Single;
    public int[]? GpuDeviceIds { get; set; }
}

public sealed class TrainingPlanFinding
{
    public string Severity { get; set; } = "Warning";
    public string Field { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class TrainingPlanResult
{
    public bool CanStart { get; set; }
    public string DetectedMethod { get; set; } = TrainingMethod.Sft;

    public bool DatasetSupportsSupervised { get; set; }
    public bool DatasetSupportsPreference { get; set; }
    public bool DatasetSupportsKto { get; set; }
    public bool DatasetSupportsContinuedPretraining { get; set; }
    public bool DatasetSupportsGrpo { get; set; }
    public bool DatasetSupportsVision { get; set; }
    public bool DatasetSupportsTts { get; set; }
    public int DatasetExamples { get; set; }
    public int DatasetValidChatRecords { get; set; }
    public int DatasetValidPreferenceRecords { get; set; }
    public int DatasetValidKtoRecords { get; set; }
    public int DatasetDesirableKtoRecords { get; set; }
    public int DatasetUndesirableKtoRecords { get; set; }
    public int DatasetUnparseableLines { get; set; }
    public bool DatasetSampleTruncated { get; set; }
    /// <summary>True only after all nonblank SFT/DPO JSONL rows passed strict validation.</summary>
    public bool DatasetFullyValidated { get; set; }
    public int DatasetVerifiedRecords { get; set; }
    public string? DatasetSha256 { get; set; }
    public string DatasetSummary { get; set; } = string.Empty;

    public int RecommendedLoraRank { get; set; }
    public int RecommendedMaxSteps { get; set; }
    public double RecommendedLearningRate { get; set; }
    public string RecommendationRationale { get; set; } = string.Empty;
    public int RecommendedMaxSequenceLength { get; set; }

    public double EstimatedGib { get; set; }
    public double AvailableGib { get; set; }
    public string MultiGpuStrategy { get; set; } = TrainingMultiGpuStrategy.Single;
    public int WorldSize { get; set; } = 1;
    public int EffectiveBatchSize { get; set; } = 4;
    public int[] SelectedGpuDeviceIds { get; set; } = Array.Empty<int>();
    public List<TrainingPlanFinding> Findings { get; set; } = new();
}
