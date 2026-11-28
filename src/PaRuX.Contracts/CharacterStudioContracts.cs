namespace PaRuX.Contracts;

/// <summary>Where a picture of a character came from.</summary>
public static class CharacterImageSource
{
    public const string Generated = "generated";
    public const string User = "user";
}

/// <summary>Plain-language training choices of the Character Studio wizard.</summary>
public static class CharacterTrainingPreset
{
    public const string Quick = "quick";
    public const string Balanced = "balanced";
    public const string Best = "best";

    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Quick => Quick,
        Best => Best,
        _ => Balanced
    };
}

/// <summary>Lifecycle of one character's look. Wire values are the lower-case names.</summary>
public static class CharacterStage
{
    public const string Draft = "draft";
    public const string Collecting = "collecting";
    public const string Captioning = "captioning";
    public const string Ready = "ready";
    public const string Training = "training";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
}

/// <summary>One picture in a character's reference set.</summary>
public sealed class CharacterImage
{
    public string Id { get; set; } = string.Empty;
    /// <summary>File name inside the character's images folder. Never a path.</summary>
    public string File { get; set; } = string.Empty;
    public string Source { get; set; } = CharacterImageSource.Generated;
    /// <summary>The shot description (pose, angle, expression) or the user's file name.</summary>
    public string Label { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public long Seed { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>The trained LoRA attached to a character. It is a file in ComfyUI's loras folder plus the words that activate it.</summary>
public sealed class CharacterLoraBinding
{
    public string LoraFile { get; set; } = string.Empty;
    public string TriggerWord { get; set; } = string.Empty;
    public double Strength { get; set; } = 1.0;
    /// <summary>LoRA family key ("zimage", "flux", ...). Used to refuse the LoRA on a checkpoint of another family.</summary>
    public string Family { get; set; } = "zimage";
    public DateTime BoundUtc { get; set; }
}

/// <summary>Advanced overrides. Null means "use the preset".</summary>
public sealed class CharacterTrainingAdvanced
{
    public int? Rank { get; set; }
    public int? Steps { get; set; }
    public double? LearningRate { get; set; }
    public int? Resolution { get; set; }
}

public sealed class CharacterTrainingStatus
{
    public string Stage { get; set; } = CharacterStage.Draft;
    public string Message { get; set; } = string.Empty;
    /// <summary>Stable code of the last failure, empty when none: out_of_vram, trainer_missing, too_few_pictures, gaming_active, ...</summary>
    public string ErrorCode { get; set; } = string.Empty;
    /// <summary>What the user can do about the failure. English text; the app translates by code.</summary>
    public string NextAction { get; set; } = string.Empty;
    public string Preset { get; set; } = CharacterTrainingPreset.Balanced;
    public int Step { get; set; }
    public int TotalSteps { get; set; }
    public int Epoch { get; set; }
    public int TotalEpochs { get; set; }
    public double? Loss { get; set; }
    public double? SecondsPerStep { get; set; }
    public int? EtaSeconds { get; set; }
    public bool CanResume { get; set; }
    /// <summary>File names of the preview pictures the trainer produced, oldest first.</summary>
    public List<string> Samples { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}

public sealed class CharacterProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>What the character looks like, in words. Feeds the fixed identity prompt.</summary>
    public string Appearance { get; set; } = string.Empty;
    /// <summary>The chat persona this character is attached to, if any.</summary>
    public string? PersonaId { get; set; }
    public string TriggerWord { get; set; } = string.Empty;
    public long BaseSeed { get; set; }
    public string BaseFamily { get; set; } = "zimage";
    public string Preset { get; set; } = CharacterTrainingPreset.Balanced;
    public CharacterTrainingAdvanced Advanced { get; set; } = new();
    public List<CharacterImage> Images { get; set; } = new();
    public CharacterTrainingStatus Training { get; set; } = new();
    public CharacterLoraBinding? Binding { get; set; }
    /// <summary>File name of the portrait inside the character's folder.</summary>
    public string? PortraitFile { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class CharacterListResponse
{
    public List<CharacterProfile> Characters { get; set; } = new();
}

public sealed class CreateCharacterRequest
{
    public string Name { get; set; } = string.Empty;
    public string Appearance { get; set; } = string.Empty;
    public string? PersonaId { get; set; }
}

public sealed class UpdateCharacterRequest
{
    public string? Name { get; set; }
    public string? Appearance { get; set; }
    public string? PersonaId { get; set; }
    public string? TriggerWord { get; set; }
    public string? PortraitImageId { get; set; }
}

public sealed class GenerateReferencesRequest
{
    /// <summary>How many pictures to make. Clamped to the dataset limits.</summary>
    public int Count { get; set; } = 20;
}

public sealed class AddCharacterImageRequest
{
    public string FileName { get; set; } = string.Empty;
    public string DataBase64 { get; set; } = string.Empty;
}

public sealed class StartCharacterTrainingRequest
{
    public string Preset { get; set; } = CharacterTrainingPreset.Balanced;
    public CharacterTrainingAdvanced? Advanced { get; set; }
}

public sealed class BindCharacterLoraRequest
{
    public string LoraFile { get; set; } = string.Empty;
    public double Strength { get; set; } = 1.0;
}

public sealed class CharacterDatasetFinding
{
    /// <summary>too_few | too_many | tiny_image | duplicate | unsupported_format | no_caption | few_recommended</summary>
    public string Code { get; set; } = string.Empty;
    public bool Blocking { get; set; }
    public string Message { get; set; } = string.Empty;
    public string NextAction { get; set; } = string.Empty;
    public List<string> ImageIds { get; set; } = new();
}

public sealed class CharacterTrainingPlan
{
    public string Preset { get; set; } = CharacterTrainingPreset.Balanced;
    public string Label { get; set; } = string.Empty;
    public int Rank { get; set; }
    public int Steps { get; set; }
    public int Epochs { get; set; }
    public int Resolution { get; set; }
    public double LearningRate { get; set; }
    public int EstimatedMinutes { get; set; }
    public double VramGib { get; set; }
    public bool LowVram { get; set; }
    public bool Supported { get; set; } = true;
    public string Note { get; set; } = string.Empty;
    public List<CharacterDatasetFinding> Findings { get; set; } = new();
    public bool CanStart { get; set; }
}

public sealed class CharacterTrainerStatus
{
    /// <summary>not_installed | ready | broken | skipped</summary>
    public string State { get; set; } = "not_installed";
    /// <summary>PARUX_MANAGED | USER_MANAGED | SYSTEM_DETECTED | NONE</summary>
    public string Ownership { get; set; } = "NONE";
    public string Detail { get; set; } = string.Empty;
    public string? Version { get; set; }
    public bool Installing { get; set; }
}

public sealed class LocateCharacterTrainerRequest
{
    /// <summary>Folder that contains run.py of an existing trainer checkout.</summary>
    public string TrainerFolder { get; set; } = string.Empty;
    /// <summary>python.exe of the environment that has that trainer's dependencies.</summary>
    public string PythonExe { get; set; } = string.Empty;
}
