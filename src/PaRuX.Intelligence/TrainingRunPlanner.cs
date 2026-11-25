namespace PaRuX.Intelligence;

/// <summary>
/// Preflight planner for every PaRuX-managed Training Lab method. It is deliberately conservative:
/// when model/dataset compatibility cannot be established, the wizard says so before allocating
/// VRAM instead of converting a Python traceback into the product's normal validation UX.
/// </summary>
public static class TrainingRunPlanner
{
    private const double GibPerBillionParameters4Bit = 1.1;
    private const double MinimumRunGib = 3.0;

    public enum Severity { Blocking, Warning }
    public sealed record Finding(Severity Severity, string Field, string Message);
    public sealed record Recommendation(int LoraRank, int MaxSteps, double LearningRate, string Rationale);
    public sealed record GpuDeviceSpec(int Index, string Name, double VramGib);
    public sealed record RunPlan(
        bool CanStart,
        string DetectedMethod,
        TrainingDatasetShape.Shape Dataset,
        Recommendation Recommended,
        IReadOnlyList<Finding> Findings,
        double EstimatedGib,
        double AvailableGib,
        string MultiGpuStrategy = "single",
        int WorldSize = 1,
        int EffectiveBatchSize = TrainingBatchShapePolicy.DefaultEffectiveBatch,
        int[]? SelectedGpuDeviceIds = null)
    {
        public IEnumerable<Finding> Blocking => Findings.Where(finding => finding.Severity == Severity.Blocking);
        public IEnumerable<Finding> Warnings => Findings.Where(finding => finding.Severity == Severity.Warning);
    }

    public static RunPlan Plan(
        TrainingDatasetShape.Shape dataset,
        string method,
        string baseModelId,
        int loraRank,
        int maxSteps,
        double learningRate,
        double availableGib,
        string precision = "qlora-4bit",
        string targetPreset = "attention",
        int maxSequenceLength = 2048,
        bool useDora = false,
        string multiGpuStrategy = "single",
        int[]? gpuDeviceIds = null,
        IReadOnlyList<GpuDeviceSpec>? availableGpus = null)
    {
        var findings = new List<Finding>();
        string normalizedMethod = NormalizeMethod(method);
        dataset ??= TrainingDatasetShape.Empty;

        if (dataset.LinesRead == 0)
        {
            findings.Add(new Finding(Severity.Blocking, "dataset", "No readable training records were found in this file."));
        }
        else
        {
            bool supported = Supports(dataset, normalizedMethod);
            if (!supported)
            {
                string alternative = (normalizedMethod is "dpo" or "orpo" or "kto") && dataset.SupportsSupervisedFineTuning
                    ? " This file is chat-shaped, so supervised fine-tuning would accept it."
                    : normalizedMethod is not ("dpo" or "orpo" or "kto") && dataset.SupportsPreferenceTuning
                        ? " This file is preference-shaped, so DPO preference tuning would accept it."
                        : string.Empty;
                findings.Add(new Finding(Severity.Blocking, "dataset",
                    dataset.Summary + alternative + " Required shape for " + FriendlyMethod(normalizedMethod) + ": " + RequiredShape(normalizedMethod)));
            }
            if (dataset.MissingMediaFiles > 0 && normalizedMethod is "vision-sft" or "tts-sft")
            {
                findings.Add(new Finding(Severity.Blocking, "dataset",
                    $"{dataset.MissingMediaFiles:N0} referenced media file(s) do not exist. Resolve media paths before training."));
            }
            if (dataset.UnparseableLines > 0)
            {
                findings.Add(new Finding(Severity.Warning, "dataset",
                    $"{dataset.UnparseableLines:N0} line(s) are not valid JSON and will be skipped"
                    + (dataset.FirstProblemLines.Count > 0 ? " - first at line " + dataset.FirstProblemLines[0].ToString("N0") + "." : ".")));
            }
            if (dataset.LinesRead < 50)
            {
                findings.Add(new Finding(Severity.Warning, "dataset",
                    $"Only {dataset.LinesRead:N0} examples were observed. The run can fit them, but it may memorise them rather than generalise; treat quality evidence as weak."));
            }
        }

        if (normalizedMethod == "kto" && dataset.ValidKtoRecords > 0)
        {
            if (dataset.DesirableKtoRecords == 0 || dataset.UndesirableKtoRecords == 0)
                findings.Add(new Finding(Severity.Blocking, "dataset",
                    "KTO needs at least one positive and one negative binary label."));
            else
            {
                double ratio = (double)Math.Max(dataset.DesirableKtoRecords, dataset.UndesirableKtoRecords)
                    / dataset.ValidKtoRecords;
                if (ratio > 0.9)
                    findings.Add(new Finding(Severity.Warning, "dataset",
                        "More than 90% of KTO examples have the same label. Rebalance before tuning for reliable results."));
            }
            if (dataset.ValidKtoRecords < 4)
                findings.Add(new Finding(Severity.Blocking, "dataset",
                    "KTO requires at least four well-formed labelled examples to construct informative batches."));
        }

        string normalizedStrategy = string.IsNullOrWhiteSpace(multiGpuStrategy)
            ? "single"
            : multiGpuStrategy.Trim().ToLowerInvariant();
        if (normalizedStrategy is not ("single" or "device-map-auto" or "data-parallel"))
        {
            findings.Add(new Finding(Severity.Blocking, "multiGpuStrategy",
                "Unsupported multi-GPU strategy '" + normalizedStrategy + "'. Choose single, device-map-auto or data-parallel."));
            normalizedStrategy = "single";
        }

        double effectiveAvailableGib = availableGib;
        double perGpuGibForBatch = availableGib;
        int worldSize = 1;
        int[] selectedGpuIds = Array.Empty<int>();
        IReadOnlyList<GpuDeviceSpec> selectedGpus = Array.Empty<GpuDeviceSpec>();

        bool gpuIdsValid = true;
        if (gpuDeviceIds is { Length: > 0 })
        {
            var seen = new HashSet<int>();
            foreach (int id in gpuDeviceIds)
            {
                if (id < 0 || id >= 64)
                {
                    findings.Add(new Finding(Severity.Blocking, "gpuDeviceIds",
                        $"GPU device index {id} is invalid. Device indices must be non-negative integers (0-63)."));
                    gpuIdsValid = false;
                }
                else if (!seen.Add(id))
                {
                    findings.Add(new Finding(Severity.Blocking, "gpuDeviceIds",
                        $"GPU device index {id} is listed more than once. Remove duplicate GPU selections."));
                    gpuIdsValid = false;
                }
            }
            if (gpuIdsValid && availableGpus != null)
            {
                var byIndex = availableGpus.ToDictionary(g => g.Index);
                var matched = new List<GpuDeviceSpec>();
                foreach (int id in gpuDeviceIds)
                {
                    if (!byIndex.TryGetValue(id, out GpuDeviceSpec? gpu))
                    {
                        findings.Add(new Finding(Severity.Blocking, "gpuDeviceIds",
                            $"GPU device index {id} was not detected on this PC ({availableGpus.Count} CUDA-capable GPU(s) detected)."));
                        gpuIdsValid = false;
                    }
                    else
                    {
                        matched.Add(gpu);
                    }
                }
                if (gpuIdsValid)
                {
                    selectedGpus = matched;
                    selectedGpuIds = gpuDeviceIds.ToArray();
                }
            }
            else if (gpuIdsValid)
            {
                selectedGpuIds = gpuDeviceIds.ToArray();
            }
        }

        if (normalizedStrategy == "single")
        {
            if (gpuDeviceIds is { Length: > 1 })
            {
                findings.Add(new Finding(Severity.Blocking, "gpuDeviceIds",
                    "Single-GPU strategy accepts at most one GPU device ID. Choose device-map-auto or data-parallel to use multiple GPUs."));
            }
            else if (selectedGpus.Count == 1)
            {
                effectiveAvailableGib = selectedGpus[0].VramGib;
                perGpuGibForBatch = selectedGpus[0].VramGib;
            }
        }
        else
        {
            int detectedCount = availableGpus?.Count ?? (availableGib > 0 ? 1 : 0);
            if (detectedCount < 2)
            {
                findings.Add(new Finding(Severity.Blocking, "multiGpuStrategy",
                    $"Multi-GPU training ({normalizedStrategy}) requires at least 2 GPUs, but PaRuX detected only {detectedCount}. Use single-GPU mode on this machine."));
            }
            else
            {
                if (gpuDeviceIds is not { Length: > 0 })
                {
                    selectedGpus = availableGpus!;
                    selectedGpuIds = selectedGpus.Select(g => g.Index).ToArray();
                }
                else if (gpuIdsValid && selectedGpuIds.Length < 2)
                {
                    findings.Add(new Finding(Severity.Blocking, "gpuDeviceIds",
                        $"Multi-GPU training ({normalizedStrategy}) requires selecting at least 2 GPUs."));
                }
            }

            if (normalizedStrategy == "device-map-auto" && normalizedMethod is "tts-sft" or "embedding-ft")
            {
                findings.Add(new Finding(Severity.Blocking, "multiGpuStrategy",
                    $"Auto model splitting (device-map-auto) is supported for text and vision LLM methods, not {FriendlyMethod(normalizedMethod)}."));
            }
            if (normalizedStrategy == "data-parallel" && normalizedMethod == "embedding-ft")
            {
                findings.Add(new Finding(Severity.Blocking, "multiGpuStrategy",
                    "Distributed Data Parallel (torchrun) is not supported for the custom embedding fine-tuning loop yet. Use single-GPU mode for embedding fine-tuning."));
            }

            if (selectedGpus.Count >= 2)
            {
                double minVram = selectedGpus.Min(g => g.VramGib);
                double maxVram = selectedGpus.Max(g => g.VramGib);
                double sumVram = selectedGpus.Sum(g => g.VramGib);
                if (normalizedStrategy == "device-map-auto")
                {
                    effectiveAvailableGib = sumVram;
                    perGpuGibForBatch = minVram;
                    worldSize = 1;
                }
                else // data-parallel
                {
                    effectiveAvailableGib = minVram;
                    perGpuGibForBatch = minVram;
                    worldSize = selectedGpus.Count;
                    if (minVram > 0 && maxVram > minVram * 1.15)
                    {
                        findings.Add(new Finding(Severity.Warning, "gpuDeviceIds",
                            $"Selected GPUs have uneven VRAM ({minVram:0.#} GB vs {maxVram:0.#} GB). Data Parallel replicates the full model on each GPU and is limited by the smallest selected card."));
                    }
                }
            }
            else if (gpuIdsValid && selectedGpuIds.Length >= 2 && normalizedStrategy == "data-parallel")
            {
                worldSize = selectedGpuIds.Length;
            }
        }

        long perGpuVramBytes = perGpuGibForBatch > 0 && double.IsFinite(perGpuGibForBatch)
            ? (long)Math.Min(perGpuGibForBatch * 1024d * 1024 * 1024, long.MaxValue)
            : 0;
        var batchPlan = TrainingBatchShapePolicy.PlanMultiGpu(maxSequenceLength, perGpuVramBytes, worldSize);
        int effectiveBatchSize = normalizedMethod == "kto"
            ? Math.Max(16, batchPlan.EffectiveBatch)
            : batchPlan.EffectiveBatch;

        if (normalizedMethod == "kto")
        {
            if (batchPlan.MicroBatch < 4)
                findings.Add(new Finding(Severity.Blocking, "maxSequenceLength",
                    "KTO needs four physical GPU samples (effective batch 16), but the selected sequence length "
                    + "and reported VRAM allow only " + batchPlan.MicroBatch
                    + ". Reduce the context length or select a larger GPU."));
        }

        ValidateModelFamily(baseModelId, normalizedMethod, findings);

        // 16-bit LoRA holds a much larger base model in memory than 4-bit QLoRA.
        // This is an estimate and MUST be followed by actual device admission/measurement.
        bool sixteenBit = string.Equals(precision, "lora-16bit", StringComparison.Ordinal);
        bool fullFineTune = string.Equals(precision, "full-16bit", StringComparison.Ordinal);
        if (targetPreset is not ("attention" or "all-linear"))
            findings.Add(new Finding(Severity.Blocking, "targetPreset",
                "Choose a supported LoRA target preset: attention or all-linear."));
        if (targetPreset == "all-linear" && (normalizedMethod is "vision-sft" or "tts-sft"))
            findings.Add(new Finding(Severity.Blocking, "targetPreset",
                "Vision/TTS use fixed target modules; all-linear is currently supported only for text methods."));


        if (precision is not ("qlora-4bit" or "lora-16bit" or "full-16bit"))
            findings.Add(new Finding(Severity.Blocking, "precision",
                "Select a supported precision: QLoRA 4-bit, LoRA 16-bit or full 16-bit fine-tuning."));
        string? admission = TrainingPrecisionAdmission.Refusal(normalizedMethod, precision, baseModelId, useDora);
        if (admission != null)
            findings.Add(new Finding(Severity.Blocking, "precision", admission));
        if (sixteenBit && (normalizedMethod is "vision-sft" or "tts-sft"))
            findings.Add(new Finding(Severity.Blocking, "precision",
                "16-bit LoRA is available only for text SFT, DPO, GRPO and continued pretraining in this runtime."));

        if ((sixteenBit || fullFineTune) && (baseModelId?.Contains("bnb-4bit", StringComparison.OrdinalIgnoreCase) == true
            || baseModelId?.Contains("4bit", StringComparison.OrdinalIgnoreCase) == true))
            findings.Add(new Finding(Severity.Blocking, "baseModel",
                "16-bit LoRA and full fine-tuning must use an unquantized Hugging Face base model. A repository named 4bit/bnb-4bit cannot be assumed to contain full-precision weights."));

        double? parameters = ParameterBillionsFrom(baseModelId);
        double methodMultiplier = normalizedMethod switch
        {
            "continued-pretraining" => 1.25,
            "grpo" => 1.45,
            "vision-sft" => 1.55,
            "tts-sft" => 1.70,
            "dpo" => 1.15,
            "orpo" => 1.15,
            "kto" => 1.30,
            _ => 1.0
        };
        double estimatedGib = parameters.HasValue
            ? fullFineTune
                ? Math.Max(MinimumRunGib, TrainingPrecisionAdmission.FullFineTuneGib(parameters.Value) * methodMultiplier)
                : Math.Max(MinimumRunGib, parameters.Value * GibPerBillionParameters4Bit
                    * methodMultiplier * (sixteenBit ? 2.8 : 1.0))
            : 0;
        if (useDora && !fullFineTune)
            estimatedGib *= 1.1; // DoRA keeps a per-layer magnitude vector and an extra weight-norm pass.
        if (normalizedStrategy == "device-map-auto")
            estimatedGib *= 1.1; // Cross-GPU activation transfers and uneven layer boundaries add ~10% overhead.
        else if (normalizedStrategy == "data-parallel")
            estimatedGib *= 1.05; // DDP gradient buckets add ~5% per-GPU overhead.

        if (parameters.HasValue && effectiveAvailableGib > 0 && estimatedGib > effectiveAvailableGib)
        {
            string hint = normalizedStrategy == "data-parallel" && selectedGpus.Count >= 2
                && estimatedGib <= selectedGpus.Sum(g => g.VramGib) * 0.95
                ? " Each GPU holds a full copy in Data Parallel; switch to Auto Split (device-map-auto) to shard the model across GPUs."
                : string.Empty;
            findings.Add(new Finding(Severity.Blocking, "baseModel",
                $"{FriendlyMethod(normalizedMethod)} with a {parameters.Value:0.#}B base needs roughly {estimatedGib:0.#} GB, "
                + $"but PaRuX observed only {effectiveAvailableGib:0.#} GB available"
                + (normalizedStrategy == "data-parallel" ? " per GPU." : ".") + hint));
        }
        else if (!parameters.HasValue)
        {
            findings.Add(new Finding(Severity.Warning, "baseModel",
                "PaRuX could not establish parameter count from the model id, so it has not checked whether this model fits; memory fit is unproven rather than assumed."));
        }

        if (!sixteenBit && !fullFineTune && normalizedMethod is not "tts-sft" and not "embedding-ft" && !string.IsNullOrWhiteSpace(baseModelId)
            && !baseModelId.Contains("bnb-4bit", StringComparison.OrdinalIgnoreCase)
            && !baseModelId.Contains("4bit", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new Finding(Severity.Warning, "baseModel",
                "This model id does not look pre-quantized 4-bit. Actual training memory may be substantially higher."));
        }

        Recommendation recommended = Recommend(dataset, normalizedMethod);
        if (loraRank <= 0)
            findings.Add(new Finding(Severity.Blocking, "loraRank", "LoRA rank must be at least 1."));
        else if (loraRank > 128)
            findings.Add(new Finding(Severity.Warning, "loraRank", "Rank above 128 is unusually expensive and rarely justified by local fine-tuning evidence."));

        if (maxSteps <= 0)
            findings.Add(new Finding(Severity.Blocking, "maxSteps", "The number of steps must be at least 1."));
        else if (dataset.LinesRead > 0 && maxSteps < Math.Max(1, dataset.LinesRead / 8))
            findings.Add(new Finding(Severity.Warning, "maxSteps",
                $"{maxSteps:N0} steps will not cover most of {dataset.LinesRead:N0} examples at the default effective batch; about {recommended.MaxSteps:N0} is a better starting point."));

        if (learningRate <= 0)
            findings.Add(new Finding(Severity.Blocking, "learningRate", "The learning rate must be greater than zero."));
        else
        {
            double warningThreshold = normalizedMethod switch
            {
                "kto" => 1e-6,
                "grpo" or "dpo" or "orpo" => 1e-4,
                "tts-sft" => 2e-4,
                _ => 1e-3
            };
            if (learningRate > warningThreshold)
                findings.Add(new Finding(Severity.Warning, "learningRate",
                    $"{learningRate:G} is aggressive for {FriendlyMethod(normalizedMethod)}; {recommended.LearningRate:G} is the safer starting range."));
        }

        return new RunPlan(
            findings.All(finding => finding.Severity != Severity.Blocking), normalizedMethod, dataset,
            recommended, findings, estimatedGib, effectiveAvailableGib,
            normalizedStrategy, worldSize, effectiveBatchSize, selectedGpuIds);
    }

    public static Recommendation Recommend(TrainingDatasetShape.Shape dataset, string method)
    {
        int examples = dataset?.LinesRead ?? 0;
        string normalized = NormalizeMethod(method);
        int rank = examples switch { < 200 => 8, < 2_000 => 16, < 20_000 => 32, _ => 64 };
        int steps = Math.Clamp(examples * (normalized == "continued-pretraining" ? 2 : 3) / 8, 60, 5_000);
        double learningRate = normalized switch
        {
            "dpo" => examples < 2_000 ? 5e-6 : 2e-6,
            "orpo" => examples < 2_000 ? 1e-5 : 5e-6,
            "kto" => examples < 2_000 ? 1e-6 : 5e-7,
            "grpo" => examples < 2_000 ? 5e-6 : 2e-6,
            "continued-pretraining" => examples < 2_000 ? 5e-5 : 2e-5,
            "vision-sft" => examples < 2_000 ? 1e-4 : 5e-5,
            "tts-sft" => examples < 2_000 ? 2e-5 : 1e-5,
            "embedding-ft" => examples < 2_000 ? 1e-4 : 5e-5,
            _ => examples < 2_000 ? 2e-4 : 1e-4
        };
        string rationale = examples == 0
            ? "No dataset has been inspected yet; these are conservative defaults."
            : $"Scaled to {examples:N0} example(s) for {FriendlyMethod(normalized)}: rank {rank}, bounded multi-pass coverage and a method-specific learning rate.";
        return new Recommendation(rank, steps, learningRate, rationale);
    }

    public static bool Supports(TrainingDatasetShape.Shape dataset, string method) => NormalizeMethod(method) switch
    {
        "dpo" or "orpo" => dataset.SupportsPreferenceTuning,
        "kto" => dataset.SupportsKto,
        "continued-pretraining" => dataset.SupportsContinuedPretraining,
        "grpo" => dataset.SupportsGrpo,
        "vision-sft" => dataset.SupportsVisionFineTuning,
        "tts-sft" => dataset.SupportsTtsFineTuning,
        "embedding-ft" => dataset.SupportsEmbedding,
        _ => dataset.SupportsSupervisedFineTuning
    };

    private static void ValidateModelFamily(string modelId, string method, List<Finding> findings)
    {
        string id = modelId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            findings.Add(new Finding(Severity.Blocking, "baseModel", "A base model is required."));
            return;
        }
        if (method == "vision-sft")
        {
            bool looksVision = ContainsAny(id, "vision", "-vl", "vl-", "llava", "pixtral", "qwen2.5-vl", "qwen2-vl", "idefics");
            if (!looksVision)
                findings.Add(new Finding(Severity.Blocking, "baseModel",
                    "Vision SFT requires a vision-language base model. Choose a VL/Vision/LLaVA/Pixtral-compatible model instead of a text-only model."));
        }
        if (method == "tts-sft")
        {
            // Keep planner and runner honest: the current executable adapter is SpeechT5. Other TTS
            // model families are not advertised until PaRuX owns a real training adapter for them.
            if (!id.Contains("speecht5", StringComparison.OrdinalIgnoreCase))
                findings.Add(new Finding(Severity.Blocking, "baseModel",
                    "The current PaRuX TTS training adapter supports SpeechT5-family models. Other TTS families are not advertised until they have a real PaRuX-managed trainer."));
        }
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string RequiredShape(string method) => method switch
    {
        "dpo" or "orpo" => "JSONL objects with prompt, chosen and rejected strings.",
        "kto" => "JSONL objects with prompt, completion strings and a boolean label (true/false); at least one of each class.",
        "continued-pretraining" => "JSONL objects with a non-empty text string.",
        "grpo" => "JSONL objects with prompt and, for reference rewards, reference strings.",
        "vision-sft" => "JSONL objects with image plus messages, or image + prompt + response.",
        "tts-sft" => "JSONL objects with audio and text; referenced audio files must exist.",
        "embedding-ft" => "JSONL objects with query and positive strings (optional negative string).",
        _ => "JSONL objects with a messages array of role/content pairs."
    };

    private static string FriendlyMethod(string method) => method switch
    {
        "dpo" => "DPO",
        "orpo" => "ORPO",
        "kto" => "KTO",
        "continued-pretraining" => "continued pretraining",
        "grpo" => "GRPO",
        "vision-sft" => "Vision SFT",
        "tts-sft" => "TTS SFT",
        "embedding-ft" => "embedding fine-tuning",
        _ => "supervised fine-tuning"
    };

    private static string NormalizeMethod(string? method)
    {
        string value = string.IsNullOrWhiteSpace(method) ? "sft" : method.Trim().ToLowerInvariant();
        return value is "sft" or "dpo" or "orpo" or "kto" or "continued-pretraining" or "grpo" or "vision-sft" or "tts-sft" or "embedding-ft" ? value : "sft";
    }

    public static double? ParameterBillionsFrom(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            modelId, @"(?<![A-Za-z0-9.])(?<value>\d+(?:\.\d+)?)\s*(?<unit>[bBmM])(?![A-Za-z0-9])");
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups["value"].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value)) return null;
        double billions = match.Groups["unit"].Value is "m" or "M" ? value / 1000.0 : value;
        return billions is > 0 and < 2_000 ? billions : null;
    }
}
