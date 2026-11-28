using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.CharacterStudio;

/// <summary>Trigger word and caption format. A caption is one line: the trigger word first, then what is visible.</summary>
internal static class CharacterCaptions
{
    internal const int MaxCaptionWords = 60;
    private static readonly Regex NonWord = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly string[] Boilerplate =
    {
        "the image shows ", "this image shows ", "the image depicts ", "this image depicts ",
        "the picture shows ", "this picture shows ", "the image features ", "this is an image of ", "an image of ", "a picture of "
    };

    /// <summary>A made-up token such as "prx_mira" that the base model does not already know, so only this character answers to it.</summary>
    internal static string MakeTriggerWord(string? name)
    {
        string slug = NonWord.Replace((name ?? string.Empty).Normalize(NormalizationForm.FormD).ToLowerInvariant(), string.Empty);
        slug = new string(slug.Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());
        if (slug.Length > 14) slug = slug[..14];
        return slug.Length == 0 ? "prx_char" : "prx_" + slug;
    }

    internal static bool IsValidTriggerWord(string? word) =>
        !string.IsNullOrEmpty(word) && word.Length is >= 4 and <= 24 && Regex.IsMatch(word, "^[a-z][a-z0-9_]*$");

    /// <summary>Single-line caption that starts with the trigger word and never repeats it. Vision-model chatter is trimmed.</summary>
    internal static string Format(string trigger, string? description)
    {
        string text = (description ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        text = Regex.Replace(text, @"\s+", " ");
        text = text.Replace(trigger, string.Empty, StringComparison.OrdinalIgnoreCase).Trim(' ', ',', '.');
        foreach (string lead in Boilerplate)
            if (text.StartsWith(lead, StringComparison.OrdinalIgnoreCase)) { text = text[lead.Length..]; break; }
        text = text.Trim(' ', ',', '.');
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > MaxCaptionWords) text = string.Join(' ', words.Take(MaxCaptionWords));
        return text.Length == 0 ? trigger : trigger + ", " + text;
    }

    internal static bool HasTrigger(string? caption, string trigger) =>
        !string.IsNullOrWhiteSpace(caption) && caption.StartsWith(trigger, StringComparison.Ordinal);
}

/// <summary>Checks the reference set before a GPU is touched. Every finding says what to do next.</summary>
internal static class CharacterDatasetPolicy
{
    internal const int MinImages = 8;
    internal const int RecommendedImages = 15;
    internal const int MaxImages = 40;
    internal const int MinSide = 512;
    internal static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp" };

    internal static IReadOnlyList<CharacterDatasetFinding> Validate(IReadOnlyList<CharacterImage> images, string trigger)
    {
        var findings = new List<CharacterDatasetFinding>();
        int count = images.Count;
        if (count < MinImages)
            findings.Add(new CharacterDatasetFinding
            {
                Code = "too_few", Blocking = true,
                Message = "Only " + count + " picture(s). A reliable look needs at least " + MinImages + ".",
                NextAction = "Make more pictures or add your own photos, then try again."
            });
        else if (count < RecommendedImages)
            findings.Add(new CharacterDatasetFinding
            {
                Code = "few_recommended", Blocking = false,
                Message = count + " pictures will work, but " + RecommendedImages + " to 25 give a more consistent face.",
                NextAction = "Add a few more pictures for a better result."
            });
        if (count > MaxImages)
            findings.Add(new CharacterDatasetFinding
            {
                Code = "too_many", Blocking = true,
                Message = count + " pictures is more than the " + MaxImages + " this studio trains on.",
                NextAction = "Remove the weakest pictures."
            });

        var unsupported = images.Where(i => !Extensions.Contains(Path.GetExtension(i.File).ToLowerInvariant())).Select(i => i.Id).ToList();
        if (unsupported.Count > 0)
            findings.Add(new CharacterDatasetFinding
            {
                Code = "unsupported_format", Blocking = true, ImageIds = unsupported,
                Message = unsupported.Count + " picture(s) are not PNG, JPG or WebP.",
                NextAction = "Remove them and add PNG or JPG pictures."
            });

        var tiny = images.Where(i => i.Width > 0 && i.Height > 0 && Math.Min(i.Width, i.Height) < MinSide).Select(i => i.Id).ToList();
        if (tiny.Count > 0)
            findings.Add(new CharacterDatasetFinding
            {
                Code = "tiny_image", Blocking = false, ImageIds = tiny,
                Message = tiny.Count + " picture(s) are smaller than " + MinSide + " pixels and will look blurry in training.",
                NextAction = "Remove them or replace them with larger photos."
            });

        var duplicates = images.Where(i => i.Sha256.Length > 0).GroupBy(i => i.Sha256).Where(g => g.Count() > 1)
            .SelectMany(g => g.Skip(1)).Select(i => i.Id).ToList();
        if (duplicates.Count > 0)
            findings.Add(new CharacterDatasetFinding
            {
                Code = "duplicate", Blocking = false, ImageIds = duplicates,
                Message = duplicates.Count + " picture(s) are exact copies of another one.",
                NextAction = "Remove the copies; repeating a picture makes the LoRA memorise it."
            });

        var uncaptioned = images.Where(i => !CharacterCaptions.HasTrigger(i.Caption, trigger)).Select(i => i.Id).ToList();
        if (uncaptioned.Count > 0)
            findings.Add(new CharacterDatasetFinding
            {
                Code = "no_caption", Blocking = false, ImageIds = uncaptioned,
                Message = uncaptioned.Count + " picture(s) have no caption yet.",
                NextAction = "PaRuX writes the missing captions when training starts."
            });
        return findings;
    }

    internal static bool HasBlocking(IEnumerable<CharacterDatasetFinding> findings) => findings.Any(f => f.Blocking);
}

/// <summary>The fixed identity prompt and the pose/angle/expression grid used to make a reference set.</summary>
internal static class CharacterReferencePlanner
{
    internal sealed record Shot(string Label, string Prompt);

    private static readonly (string Angle, string Framing)[] Views =
    {
        ("front view", "close-up portrait"), ("three-quarter view", "close-up portrait"), ("side profile", "close-up portrait"),
        ("front view", "upper body"), ("three-quarter view", "upper body"), ("looking over the shoulder", "upper body"),
        ("front view", "full body"), ("three-quarter view", "full body"), ("low angle", "upper body"), ("high angle", "close-up portrait")
    };

    private static readonly string[] Expressions =
    {
        "neutral expression", "smiling", "laughing", "serious", "surprised", "thoughtful", "calm", "confident"
    };

    private static readonly string[] Settings =
    {
        "plain light background", "outdoors in daylight", "indoor soft lighting", "city street", "cozy room", "studio backdrop"
    };

    /// <summary>The identity part never changes between pictures; only the shot varies. Seeds are base+index so a rerun gives the same set.</summary>
    internal static string IdentityPrompt(string name, string appearance) =>
        (name.Trim() + ", " + appearance.Trim()).Trim(' ', ',', '.');

    internal static IReadOnlyList<Shot> Plan(string name, string appearance, int count)
    {
        count = Math.Clamp(count, 1, CharacterDatasetPolicy.MaxImages);
        string identity = IdentityPrompt(name, appearance);
        var shots = new List<Shot>(count);
        for (int i = 0; i < count; i++)
        {
            (string angle, string framing) = Views[i % Views.Length];
            string expression = Expressions[(i * 3 + i / Views.Length) % Expressions.Length];
            string setting = Settings[(i * 5 + i / 3) % Settings.Length];
            string label = framing + ", " + angle + ", " + expression + ", " + setting;
            shots.Add(new Shot(label, identity + ", " + label + ", same person in every picture, detailed, sharp focus"));
        }
        return shots;
    }

    internal static long SeedFor(long baseSeed, int index, int attempt = 0) =>
        unchecked(baseSeed + index + 1000L * attempt) & long.MaxValue;
}

/// <summary>Training presets. 16 GB is the design target; smaller cards get only the Quick choice, bigger ones get headroom.</summary>
internal static class CharacterLoraPresets
{
    internal const double MinimumVramGib = 12.0;
    internal const int ImagesRepeats = 10;

    /// <summary>Seconds per step, from a measured run (RTX 5080, 16 GB, 512 px, rank 8: 1.0 s/step) and scaled with picture size. Used only for the ETA shown before the first step.</summary>
    private static double SecondsPerStep(int resolution, double vramGib) =>
        (resolution switch { <= 512 => 1.2, <= 768 => 2.5, _ => 4.5 }) * (vramGib >= 23.5 ? 0.6 : 1.0);

    internal static CharacterTrainingPlan Select(string? preset, double vramGib, int imageCount, CharacterTrainingAdvanced? advanced = null)
    {
        string p = CharacterTrainingPreset.Normalize(preset);
        var plan = new CharacterTrainingPlan { Preset = p, VramGib = Math.Round(vramGib, 1) };
        if (vramGib < MinimumVramGib)
        {
            plan.Supported = false;
            plan.Label = Label(p);
            plan.Note = "This graphics card has " + plan.VramGib.ToString("0.#", CultureInfo.InvariantCulture)
                + " GB of memory; training a character needs at least " + MinimumVramGib.ToString("0", CultureInfo.InvariantCulture) + " GB.";
            return plan;
        }

        bool small = vramGib < 15.0;
        if (small && p != CharacterTrainingPreset.Quick)
        {
            p = CharacterTrainingPreset.Quick;
            plan.Preset = p;
            plan.Note = "With about " + plan.VramGib.ToString("0.#", CultureInfo.InvariantCulture) + " GB only the Quick choice fits.";
        }
        bool roomy = vramGib >= 23.5;
        (int rank, int res, int epochs, double lr) = p switch
        {
            CharacterTrainingPreset.Quick => (8, 512, 18, 1e-4),
            CharacterTrainingPreset.Best => (roomy ? 32 : 16, roomy ? 1024 : 768, 24, 1e-4),
            _ => (16, 768, 16, 1e-4)
        };
        int n = Math.Max(1, imageCount);
        int steps = Math.Clamp(n * epochs, 200, 4000);
        if (advanced != null)
        {
            if (advanced.Rank is > 0) rank = Math.Clamp(advanced.Rank.Value, 4, 128);
            if (advanced.Resolution is > 0) res = Math.Clamp(advanced.Resolution.Value / 64 * 64, 256, 1280);
            if (advanced.LearningRate is > 0) lr = Math.Clamp(advanced.LearningRate.Value, 1e-6, 1e-3);
            if (advanced.Steps is > 0) steps = Math.Clamp(advanced.Steps.Value, 10, 8000);
        }
        plan.Rank = rank;
        plan.Resolution = res;
        plan.LearningRate = lr;
        plan.Steps = steps;
        plan.Epochs = Math.Max(1, (int)Math.Round(steps / (double)n));
        plan.LowVram = small;
        plan.Label = Label(p);
        plan.EstimatedMinutes = (int)Math.Ceiling(steps * SecondsPerStep(res, vramGib) / 60.0 + 5); // + model loading and latent caching
        return plan;
    }

    internal static string Label(string preset) => preset switch
    {
        CharacterTrainingPreset.Quick => "Quick (about 15 minutes)",
        CharacterTrainingPreset.Best => "Best quality",
        _ => "Balanced"
    };
}

/// <summary>Builds the trainer's YAML job file. Z-Image Turbo with its training adapter, 8-bit weights, 8-bit optimizer.</summary>
internal static class CharacterTrainerConfig
{
    internal const string ZImageTurboRepo = "Tongyi-MAI/Z-Image-Turbo";
    internal const string ZImageAdapter = "ostris/zimage_turbo_training_adapter/zimage_turbo_training_adapter_v2.safetensors";

    internal static string Build(string jobName, string trainingFolder, string datasetFolder, string trigger,
        CharacterTrainingPlan plan, int imageCount, long seed, IReadOnlyList<string> samplePrompts)
    {
        int saveEvery = Math.Max(10, Math.Min(250, plan.Steps / Math.Max(1, plan.Epochs)));
        int sampleEvery = saveEvery;
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append('\n');
        L("job: extension");
        L("config:");
        L("  name: " + Q(jobName));
        L("  process:");
        L("    - type: 'sd_trainer'");
        L("      training_folder: " + Q(trainingFolder));
        L("      device: cuda:0");
        L("      trigger_word: " + Q(trigger));
        L("      network:");
        L("        type: 'lora'");
        L("        linear: " + plan.Rank);
        L("        linear_alpha: " + plan.Rank);
        L("      save:");
        L("        dtype: float16");
        L("        save_every: " + saveEvery);
        L("        max_step_saves_to_keep: 3");
        L("      datasets:");
        L("        - folder_path: " + Q(datasetFolder));
        L("          caption_ext: 'txt'");
        L("          caption_dropout_rate: 0.05");
        L("          cache_latents_to_disk: true");
        L("          resolution: [ " + plan.Resolution + " ]");
        L("      train:");
        L("        batch_size: 1");
        L("        steps: " + plan.Steps);
        L("        gradient_accumulation_steps: 1");
        L("        train_unet: true");
        L("        train_text_encoder: false");
        L("        gradient_checkpointing: true");
        L("        noise_scheduler: 'flowmatch'");
        L("        optimizer: 'adamw8bit'");
        L("        timestep_type: 'weighted'");
        L("        lr: " + plan.LearningRate.ToString("0.#######e+0", CultureInfo.InvariantCulture));
        L("        skip_first_sample: true");
        L("        dtype: bf16");
        L("      model:");
        L("        name_or_path: " + Q(ZImageTurboRepo));
        L("        arch: 'zimage:turbo'");
        L("        quantize: true");
        L("        quantize_te: true");
        L("        qtype: 'qfloat8'");
        L("        low_vram: " + (plan.LowVram ? "true" : "false"));
        L("        assistant_lora_path: " + Q(ZImageAdapter));
        L("      sample:");
        L("        sampler: 'flowmatch'");
        L("        sample_every: " + sampleEvery);
        L("        width: " + Math.Min(plan.Resolution, 768));
        L("        height: " + Math.Min(plan.Resolution, 768));
        L("        prompts:");
        foreach (string prompt in samplePrompts) L("          - " + Q(prompt));
        L("        neg: ''");
        L("        seed: " + (seed & 0x7FFFFFFF));
        L("        walk_seed: true");
        L("        guidance_scale: 1");
        L("        sample_steps: 8");
        L("meta:");
        L("  name: '[name]'");
        L("  version: '1.0'");
        return sb.ToString();
    }

    /// <summary>YAML single-quoted scalar; a quote is escaped by doubling it.</summary>
    internal static string Q(string value) => "'" + (value ?? string.Empty).Replace("'", "''").Replace("\r", " ").Replace("\n", " ") + "'";
}

/// <summary>One parsed progress reading from the trainer's console.</summary>
internal sealed record TrainerProgress(int Step, int Total, double? SecondsPerStep, int? EtaSeconds, double? Loss, double? LearningRate);

/// <summary>Reads the trainer's tqdm line, e.g. "job:  12%|#2   | 120/1000 [05:12&lt;38:20,  2.30s/it, lr: 1.0e-04 loss: 3.1e-01]".</summary>
internal static partial class CharacterTrainerProgressParser
{
    [GeneratedRegex(@"(?<done>\d+)/(?<total>\d+)\s*\[(?<el>[\d:]+)<(?<eta>[\d:?]+),\s*(?<rate>[\d.]+)\s*(?<unit>s/it|it/s)(?:,\s*(?<post>[^\]]*))?\]")]
    private static partial Regex Line();

    internal static TrainerProgress? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        MatchCollection all = Line().Matches(text);
        if (all.Count == 0) return null;
        Match m = all[^1];
        if (!int.TryParse(m.Groups["done"].Value, out int done) || !int.TryParse(m.Groups["total"].Value, out int total) || total <= 0) return null;
        double.TryParse(m.Groups["rate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate);
        double? spi = rate <= 0 ? null : m.Groups["unit"].Value == "s/it" ? rate : 1.0 / rate;
        int? eta = Clock(m.Groups["eta"].Value);
        double? loss = null, lr = null;
        string post = m.Groups["post"].Value;
        Match l = Regex.Match(post, @"loss:\s*([0-9.]+(?:e[-+]?\d+)?)", RegexOptions.IgnoreCase);
        if (l.Success && double.TryParse(l.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lv)) loss = lv;
        Match r = Regex.Match(post, @"lr:\s*([0-9.]+(?:e[-+]?\d+)?)", RegexOptions.IgnoreCase);
        if (r.Success && double.TryParse(r.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rv)) lr = rv;
        return new TrainerProgress(Math.Min(done, total), total, spi, eta, loss, lr);
    }

    private static int? Clock(string value)
    {
        if (value.Contains('?')) return null;
        string[] parts = value.Split(':');
        int total = 0;
        foreach (string p in parts)
        {
            if (!int.TryParse(p, out int v)) return null;
            total = total * 60 + v;
        }
        return total;
    }

    /// <summary>Maps the step to a 1-based epoch for display.</summary>
    internal static int EpochOf(int step, int stepsPerEpoch) => stepsPerEpoch <= 0 ? 1 : step / stepsPerEpoch + 1;
}

/// <summary>Maps trainer output to a friendly failure with a next action.</summary>
internal static class CharacterTrainingErrors
{
    internal sealed record Friendly(string Code, string Message, string NextAction);

    internal static Friendly Classify(string? log, int exitCode)
    {
        string t = (log ?? string.Empty).ToLowerInvariant();
        if (t.Contains("out of memory") || t.Contains("cuda error: out of memory") || t.Contains("cublas_status_alloc_failed"))
            return new("out_of_vram", "The graphics card ran out of memory.",
                "Close other apps that use the GPU, then choose the Quick option and try again.");
        if (t.Contains("no module named") || t.Contains("modulenotfounderror"))
            return new("trainer_missing", "The training engine is incomplete.", "Open Character Studio, choose Repair the training engine and try again.");
        if (t.Contains("connectionerror") || t.Contains("max retries exceeded") || t.Contains("couldn't connect") || t.Contains("offline"))
            return new("download_failed", "PaRuX could not download the base model.", "Check the internet connection and press Resume.");
        if (t.Contains("no space left") || t.Contains("not enough space"))
            return new("disk_full", "The disk is full.", "Free some disk space, then press Resume.");
        return new("trainer_failed", "Training stopped unexpectedly (exit code " + exitCode + ").", "Press Resume to continue from the last saved step, or start again with Quick.");
    }
}

/// <summary>Allowed stage changes. Pure, so cancel/resume rules are testable without a GPU.</summary>
internal static class CharacterStageMachine
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [CharacterStage.Draft] = new[] { CharacterStage.Collecting, CharacterStage.Ready },
        [CharacterStage.Collecting] = new[] { CharacterStage.Collecting, CharacterStage.Captioning, CharacterStage.Ready, CharacterStage.Draft },
        [CharacterStage.Captioning] = new[] { CharacterStage.Ready, CharacterStage.Collecting, CharacterStage.Failed },
        [CharacterStage.Ready] = new[] { CharacterStage.Training, CharacterStage.Collecting, CharacterStage.Captioning, CharacterStage.Draft },
        [CharacterStage.Training] = new[] { CharacterStage.Completed, CharacterStage.Failed, CharacterStage.Cancelled, CharacterStage.Interrupted },
        [CharacterStage.Completed] = new[] { CharacterStage.Training, CharacterStage.Collecting, CharacterStage.Ready },
        [CharacterStage.Failed] = new[] { CharacterStage.Training, CharacterStage.Collecting, CharacterStage.Ready },
        [CharacterStage.Cancelled] = new[] { CharacterStage.Training, CharacterStage.Collecting, CharacterStage.Ready },
        [CharacterStage.Interrupted] = new[] { CharacterStage.Training, CharacterStage.Collecting, CharacterStage.Ready }
    };

    internal static bool CanMove(string from, string to) => Allowed.TryGetValue(from, out string[]? next) && next.Contains(to);

    /// <summary>Resume only continues a run that stopped midway and left a saved step behind.</summary>
    internal static bool CanResume(string stage, bool hasCheckpoint) =>
        hasCheckpoint && stage is CharacterStage.Cancelled or CharacterStage.Failed or CharacterStage.Interrupted;

    /// <summary>A run that was "training" when the server stopped is interrupted, never silently "running".</summary>
    internal static string AfterRestart(string stage) => stage == CharacterStage.Training ? CharacterStage.Interrupted : stage;
}

/// <summary>Character to LoRA binding: validation and how a bound character changes an image request.</summary>
internal static class CharacterBindingPolicy
{
    internal const double DefaultStrength = 1.0;

    internal static (bool Ok, string? Error) CanBind(CharacterProfile character, string loraFile, double strength, bool fileExists, string? loraFamily)
    {
        if (string.IsNullOrWhiteSpace(loraFile) || loraFile.IndexOfAny(new[] { '/', '\\' }) >= 0 || loraFile.Contains(".."))
            return (false, "Choose a LoRA file by name.");
        if (!loraFile.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            return (false, "Only .safetensors LoRA files can be attached.");
        if (!fileExists) return (false, "That LoRA is not in the LoRA folder yet.");
        if (double.IsNaN(strength) || strength <= 0 || strength > MediaLoraStack.MaxStrength)
            return (false, "Strength must be above 0 and at most " + MediaLoraStack.MaxStrength.ToString("0.0", CultureInfo.InvariantCulture) + ".");
        if (loraFamily != null && !string.Equals(loraFamily, character.BaseFamily, StringComparison.Ordinal))
            return (false, "That LoRA was trained for a different model family than this character.");
        return (true, null);
    }

    /// <summary>Adds the character's LoRA (if room in the stack) and its trigger word to an image request. Returns true when applied.</summary>
    internal static bool Apply(MediaJobRequest request, CharacterProfile? character)
    {
        if (character?.Binding is not { } b || string.IsNullOrEmpty(b.LoraFile)) return false;
        request.Loras ??= new List<MediaLoraSelection>();
        if (!request.Loras.Any(l => string.Equals(l.Name, b.LoraFile, StringComparison.Ordinal)))
        {
            if (request.Loras.Count >= MediaLoraStack.MaxLoras) request.Loras.RemoveAt(request.Loras.Count - 1);
            request.Loras.Insert(0, new MediaLoraSelection { Name = b.LoraFile, Strength = b.Strength });
        }
        string prompt = request.Prompt ?? string.Empty;
        if (b.TriggerWord.Length > 0 && prompt.IndexOf(b.TriggerWord, StringComparison.OrdinalIgnoreCase) < 0)
            request.Prompt = (b.TriggerWord + ", " + prompt).Trim(' ', ',');
        return true;
    }
}
