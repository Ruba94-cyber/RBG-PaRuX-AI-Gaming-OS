namespace PaRuX.Intelligence;

/// <summary>
/// Pure admission rules for the training "precision" axis (QLoRA 4-bit, LoRA 16-bit, full 16-bit) and
/// for the LoRA-variant flag (DoRA), per method. One policy is shared by the preflight planner and by
/// the server's job validation, so the UI can never preview "ready" for a combination the worker will refuse.
/// </summary>
public static class TrainingPrecisionAdmission
{
    /// <summary>Methods whose runner path can train every weight. Preference/RL objectives need a
    /// separate frozen reference policy and are not validated for full fine-tuning.</summary>
    public static readonly string[] FullFineTuneMethods = { "sft", "continued-pretraining" };

    /// <summary>
    /// Full 16-bit fine-tune footprint in GiB for <paramref name="billionParameters"/>: bf16 weights (2 B),
    /// bf16 gradients (2 B) and 8-bit AdamW state (2 B) per parameter, plus a fixed allowance for the CUDA
    /// context and activations at a modest sequence length. An estimate that is always re-checked against
    /// live free VRAM at admission and by the measured peak of a real run.
    /// </summary>
    public static double FullFineTuneGib(double billionParameters) =>
        Math.Max(0, billionParameters) * 6.0 * 1e9 / (1024d * 1024 * 1024) + 1.5;

    public static bool LooksPrequantized(string? baseModelId) =>
        !string.IsNullOrWhiteSpace(baseModelId)
        && baseModelId.Contains("4bit", StringComparison.OrdinalIgnoreCase);

    /// <summary>A blocking reason, or null when the combination is admitted.</summary>
    public static string? Refusal(string method, string precision, string? baseModelId, bool useDora)
    {
        if (precision == "full-16bit")
        {
            if (!FullFineTuneMethods.Contains(method, StringComparer.Ordinal))
                return "Full 16-bit fine-tuning is validated only for supervised fine-tuning and continued pretraining. "
                    + "Preference, RL, vision and speech methods use LoRA.";
            if (useDora)
                return "DoRA is a LoRA variant. Full fine-tuning updates every weight and has no adapter to decompose; turn DoRA off.";
            if (LooksPrequantized(baseModelId))
                return "Full fine-tuning needs an unquantized Hugging Face checkpoint, not a 4-bit repository.";
        }
        if (method == "embedding-ft")
        {
            if (precision != "lora-16bit")
                return "Embedding fine-tuning trains a 16-bit LoRA adapter on the encoder; choose LoRA 16-bit.";
            if (LooksPrequantized(baseModelId))
                return "Embedding fine-tuning needs an unquantized encoder checkpoint, not a 4-bit repository.";
        }
        if (useDora && (method is "vision-sft" or "tts-sft"))
            return "DoRA is validated for text LLM and embedding methods only.";
        return null;
    }

    /// <summary>True when the free VRAM covers the estimated full fine-tune footprint with a 10% margin.</summary>
    public static bool FullFineTuneFits(double billionParameters, double availableGib) =>
        availableGib > 0 && FullFineTuneGib(billionParameters) * 1.10 <= availableGib;
}
