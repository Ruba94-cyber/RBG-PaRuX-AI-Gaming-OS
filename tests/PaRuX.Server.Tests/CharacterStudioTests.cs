using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.CharacterStudio;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CharacterStudioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-char-" + Guid.NewGuid().ToString("N"));
    public CharacterStudioTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static CharacterImage Img(string id, string trigger = "prx_mira", int w = 1024, int h = 1024, string? sha = null, string file = ".png") => new()
    {
        Id = id, File = id + file, Width = w, Height = h, Sha256 = sha ?? id, Caption = trigger + ", portrait"
    };

    private static List<CharacterImage> Images(int n) => Enumerable.Range(0, n).Select(i => Img("i" + i)).ToList();

    // ---- dataset validation ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Too_few_pictures_blocks_with_a_next_action()
    {
        var findings = CharacterDatasetPolicy.Validate(Images(5), "prx_mira");
        CharacterDatasetFinding f = Assert.Single(findings, x => x.Code == "too_few");
        Assert.True(f.Blocking);
        Assert.False(string.IsNullOrWhiteSpace(f.NextAction));
    }

    [Theory]
    [InlineData(8, false, true)]
    [InlineData(14, false, true)]
    [InlineData(15, false, false)]
    [InlineData(40, false, false)]
    [InlineData(41, true, false)]
    public void Picture_count_limits(int count, bool blocking, bool recommendMore)
    {
        var findings = CharacterDatasetPolicy.Validate(Images(count), "prx_mira");
        Assert.Equal(blocking, CharacterDatasetPolicy.HasBlocking(findings));
        Assert.Equal(recommendMore, findings.Any(f => f.Code == "few_recommended"));
    }

    [Fact]
    public void Duplicates_tiny_and_unsupported_pictures_are_reported_by_id()
    {
        var list = Images(12);
        list.Add(Img("dup", sha: "i0"));
        list.Add(Img("small", w: 300, h: 300));
        list.Add(Img("gif", file: ".gif"));
        var findings = CharacterDatasetPolicy.Validate(list, "prx_mira");
        Assert.Equal(new[] { "dup" }, findings.Single(f => f.Code == "duplicate").ImageIds);
        Assert.Equal(new[] { "small" }, findings.Single(f => f.Code == "tiny_image").ImageIds);
        CharacterDatasetFinding bad = findings.Single(f => f.Code == "unsupported_format");
        Assert.True(bad.Blocking);
        Assert.Equal(new[] { "gif" }, bad.ImageIds);
    }

    [Fact]
    public void Pictures_without_the_trigger_in_the_caption_are_flagged_but_not_blocking()
    {
        var list = Images(10);
        list[3].Caption = "a woman";
        CharacterDatasetFinding f = CharacterDatasetPolicy.Validate(list, "prx_mira").Single(x => x.Code == "no_caption");
        Assert.False(f.Blocking);
        Assert.Equal(new[] { "i3" }, f.ImageIds);
    }

    // ---- presets by VRAM ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Sixteen_gigabytes_gets_all_three_choices_at_a_sensible_cost()
    {
        var quick = CharacterLoraPresets.Select("quick", 16, 20);
        var balanced = CharacterLoraPresets.Select("balanced", 16, 20);
        var best = CharacterLoraPresets.Select("best", 16, 20);
        Assert.All(new[] { quick, balanced, best }, p => { Assert.True(p.Supported); Assert.False(p.LowVram); });
        Assert.True(quick.EstimatedMinutes <= 20, "Quick must be about 15 minutes, was " + quick.EstimatedMinutes);
        Assert.True(quick.EstimatedMinutes < balanced.EstimatedMinutes && balanced.EstimatedMinutes < best.EstimatedMinutes);
        Assert.True(quick.Resolution < balanced.Resolution && balanced.Resolution <= best.Resolution);
        Assert.Equal(16, balanced.Rank);
    }

    [Fact]
    public void Below_twelve_gigabytes_is_refused_in_plain_words()
    {
        var p = CharacterLoraPresets.Select("balanced", 8, 20);
        Assert.False(p.Supported);
        Assert.Contains("12", p.Note);
    }

    [Fact]
    public void A_twelve_gigabyte_card_only_gets_quick_with_low_vram_mode()
    {
        var p = CharacterLoraPresets.Select("best", 12, 20);
        Assert.True(p.Supported);
        Assert.Equal("quick", p.Preset);
        Assert.True(p.LowVram);
        Assert.False(string.IsNullOrEmpty(p.Note));
    }

    [Fact]
    public void A_big_card_gets_more_rank_and_resolution_for_best()
    {
        var p = CharacterLoraPresets.Select("best", 24, 20);
        Assert.Equal(1024, p.Resolution);
        Assert.Equal(32, p.Rank);
    }

    [Fact]
    public void Advanced_overrides_are_clamped_and_steps_scale_with_pictures()
    {
        var p = CharacterLoraPresets.Select("balanced", 16, 20, new CharacterTrainingAdvanced { Rank = 999, Steps = 123, LearningRate = 5, Resolution = 100 });
        Assert.Equal(128, p.Rank);
        Assert.Equal(123, p.Steps);
        Assert.Equal(1e-3, p.LearningRate);
        Assert.Equal(256, p.Resolution);
        Assert.True(CharacterLoraPresets.Select("balanced", 16, 30).Steps > CharacterLoraPresets.Select("balanced", 16, 10).Steps);
    }

    [Fact]
    public void Unknown_preset_name_means_balanced() => Assert.Equal("balanced", CharacterTrainingPreset.Normalize("whatever"));

    // ---- caption and trigger word ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Mira Rossi", "prx_mirarossi")]
    [InlineData("  Élodie!! ", "prx_elodie")]
    [InlineData("###", "prx_char")]
    [InlineData("A very very long character name indeed", "prx_averyverylongc")]
    public void Trigger_words_are_safe_made_up_tokens(string name, string expected)
    {
        string word = CharacterCaptions.MakeTriggerWord(name);
        Assert.Equal(expected, word);
        Assert.True(CharacterCaptions.IsValidTriggerWord(word));
    }

    [Theory]
    [InlineData("prx_mira", "The image shows a woman with red hair, smiling.", "prx_mira, a woman with red hair, smiling")]
    [InlineData("prx_mira", "prx_mira in a park", "prx_mira, in a park")]
    [InlineData("prx_mira", "   ", "prx_mira")]
    [InlineData("prx_mira", "line one\nline two", "prx_mira, line one line two")]
    public void Captions_start_with_the_trigger_on_one_line(string trigger, string description, string expected) =>
        Assert.Equal(expected, CharacterCaptions.Format(trigger, description));

    [Fact]
    public void Long_descriptions_are_cut_to_the_word_limit()
    {
        string caption = CharacterCaptions.Format("prx_mira", string.Join(' ', Enumerable.Repeat("word", 200)));
        Assert.Equal(CharacterCaptions.MaxCaptionWords + 1, caption.Replace(",", string.Empty).Split(' ').Length);
        Assert.DoesNotContain('\n', caption);
    }

    [Theory]
    [InlineData("abc", false)]
    [InlineData("prx_mira", true)]
    [InlineData("1prx", false)]
    [InlineData("Prx_Mira", false)]
    public void Trigger_word_validation(string word, bool valid) => Assert.Equal(valid, CharacterCaptions.IsValidTriggerWord(word));

    // ---- reference set planning -----------------------------------------------------------------------------------------------

    [Fact]
    public void Reference_shots_keep_one_identity_prompt_and_vary_the_shot()
    {
        var shots = CharacterReferencePlanner.Plan("Mira", "red hair, green eyes", 20);
        Assert.Equal(20, shots.Count);
        Assert.All(shots, s => Assert.StartsWith("Mira, red hair, green eyes, ", s.Prompt));
        Assert.True(shots.Select(s => s.Label).Distinct().Count() >= 18, "shots must be varied");
        Assert.Equal(shots.Select(s => s.Prompt), CharacterReferencePlanner.Plan("Mira", "red hair, green eyes", 20).Select(s => s.Prompt));
    }

    [Fact]
    public void Seeds_are_reproducible_and_rerolls_differ()
    {
        Assert.Equal(CharacterReferencePlanner.SeedFor(100, 3), CharacterReferencePlanner.SeedFor(100, 3));
        Assert.NotEqual(CharacterReferencePlanner.SeedFor(100, 3), CharacterReferencePlanner.SeedFor(100, 3, attempt: 1));
        Assert.True(CharacterReferencePlanner.SeedFor(long.MaxValue, 5) >= 0);
    }

    // ---- state machine with cancel and resume ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("draft", "collecting", true)]
    [InlineData("ready", "training", true)]
    [InlineData("training", "completed", true)]
    [InlineData("training", "cancelled", true)]
    [InlineData("training", "interrupted", true)]
    [InlineData("draft", "training", false)]
    [InlineData("collecting", "training", false)]
    [InlineData("completed", "cancelled", false)]
    [InlineData("cancelled", "training", true)]
    public void Stage_transitions(string from, string to, bool allowed) => Assert.Equal(allowed, CharacterStageMachine.CanMove(from, to));

    [Theory]
    [InlineData("cancelled", true, true)]
    [InlineData("failed", true, true)]
    [InlineData("interrupted", true, true)]
    [InlineData("cancelled", false, false)]
    [InlineData("completed", true, false)]
    [InlineData("training", true, false)]
    public void Resume_needs_a_stopped_run_with_a_saved_step(string stage, bool checkpoint, bool canResume) =>
        Assert.Equal(canResume, CharacterStageMachine.CanResume(stage, checkpoint));

    [Fact]
    public void A_run_that_was_training_when_the_server_stopped_comes_back_interrupted_not_running()
    {
        string state = Path.Combine(_root, "state");
        var store = new CharacterStudioStore(state, Path.Combine(_root, "data"));
        store.Add(new CharacterProfile { Id = "abc123", Name = "Mira", Training = new CharacterTrainingStatus { Stage = CharacterStage.Training } });
        var reopened = new CharacterStudioStore(state, Path.Combine(_root, "data"));
        Assert.Equal(CharacterStage.Interrupted, reopened.Get("abc123")!.Training.Stage);
        Assert.Equal(CharacterStage.Completed, CharacterStageMachine.AfterRestart(CharacterStage.Completed));
    }

    // ---- progress parser and error mapping ------------------------------------------------------------------------------------

    [Fact]
    public void Progress_line_is_parsed_with_eta_loss_and_rate()
    {
        TrainerProgress? p = CharacterTrainerProgressParser.Parse("prx_x:  12%|#2        | 120/1000 [05:12<38:20,  2.30s/it, lr: 1.0e-04 loss: 3.100e-01]");
        Assert.NotNull(p);
        Assert.Equal(120, p!.Step);
        Assert.Equal(1000, p.Total);
        Assert.Equal(38 * 60 + 20, p.EtaSeconds);
        Assert.Equal(2.30, p.SecondsPerStep!.Value, 2);
        Assert.Equal(0.31, p.Loss!.Value, 3);
        Assert.Equal(1e-4, p.LearningRate!.Value, 6);
    }

    [Fact]
    public void Progress_handles_it_per_second_and_unknown_eta_and_noise()
    {
        TrainerProgress p = CharacterTrainerProgressParser.Parse("job: 1%| | 5/500 [00:03<?, 1.60it/s]")!;
        Assert.Equal(0.625, p.SecondsPerStep!.Value, 3);
        Assert.Null(p.EtaSeconds);
        Assert.Null(CharacterTrainerProgressParser.Parse("Loading transformer"));
        Assert.Null(CharacterTrainerProgressParser.Parse(null));
        Assert.Equal(1, CharacterTrainerProgressParser.EpochOf(0, 20));
        Assert.Equal(3, CharacterTrainerProgressParser.EpochOf(45, 20));
    }

    [Theory]
    [InlineData("torch.OutOfMemoryError: CUDA out of memory. Tried to allocate", "out_of_vram")]
    [InlineData("ModuleNotFoundError: No module named 'optimum'", "trainer_missing")]
    [InlineData("requests.exceptions.ConnectionError: Max retries exceeded", "download_failed")]
    [InlineData("OSError: [Errno 28] No space left on device", "disk_full")]
    [InlineData("something odd", "trainer_failed")]
    public void Trainer_failures_become_friendly_messages_with_a_next_action(string log, string code)
    {
        var f = CharacterTrainingErrors.Classify(log, 1);
        Assert.Equal(code, f.Code);
        Assert.False(string.IsNullOrWhiteSpace(f.NextAction));
        Assert.DoesNotContain("Traceback", f.Message);
    }

    // ---- trainer config -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Trainer_config_carries_the_plan_the_trigger_and_quotes_paths_safely()
    {
        var plan = CharacterLoraPresets.Select("balanced", 16, 20);
        string yaml = CharacterTrainerConfig.Build("prx_abc", @"C:\data\it's here\out", @"C:\data\ds", "prx_mira", plan, 20, 42, new[] { "prx_mira, portrait" });
        Assert.Contains("trigger_word: 'prx_mira'", yaml);
        Assert.Contains("linear: 16", yaml);
        Assert.Contains("steps: " + plan.Steps, yaml);
        Assert.Contains("resolution: [ 768 ]", yaml);
        Assert.Contains("arch: 'zimage:turbo'", yaml);
        Assert.Contains("qtype: 'qfloat8'", yaml);
        Assert.Contains("training_folder: 'C:\\data\\it''s here\\out'", yaml);
        Assert.Contains("low_vram: false", yaml);
        Assert.DoesNotContain("\r", yaml);
    }

    // ---- character <-> LoRA binding -------------------------------------------------------------------------------------------

    private static CharacterProfile Bound() => new()
    {
        Id = "c1", Name = "Mira", BaseFamily = "zimage",
        Binding = new CharacterLoraBinding { LoraFile = "mira.safetensors", TriggerWord = "prx_mira", Strength = 0.9 }
    };

    [Fact]
    public void A_job_for_a_bound_character_gets_the_lora_and_the_trigger_word()
    {
        var request = new MediaJobRequest { Type = "image", Prompt = "reading in a cafe" };
        Assert.True(CharacterBindingPolicy.Apply(request, Bound()));
        MediaLoraSelection lora = Assert.Single(request.Loras!);
        Assert.Equal("mira.safetensors", lora.Name);
        Assert.Equal(0.9, lora.Strength);
        Assert.Equal("prx_mira, reading in a cafe", request.Prompt);
    }

    [Fact]
    public void Applying_twice_neither_duplicates_the_lora_nor_the_trigger()
    {
        var request = new MediaJobRequest { Type = "image", Prompt = "PRX_MIRA walking" };
        CharacterBindingPolicy.Apply(request, Bound());
        CharacterBindingPolicy.Apply(request, Bound());
        Assert.Single(request.Loras!);
        Assert.Equal("PRX_MIRA walking", request.Prompt);
    }

    [Fact]
    public void A_full_lora_stack_makes_room_for_the_character()
    {
        var request = new MediaJobRequest
        {
            Type = "image", Prompt = "x",
            Loras = Enumerable.Range(0, MediaLoraStack.MaxLoras).Select(i => new MediaLoraSelection { Name = "l" + i + ".safetensors" }).ToList()
        };
        CharacterBindingPolicy.Apply(request, Bound());
        Assert.Equal(MediaLoraStack.MaxLoras, request.Loras!.Count);
        Assert.Equal("mira.safetensors", request.Loras[0].Name);
    }

    [Fact]
    public void An_unbound_or_missing_character_changes_nothing()
    {
        var request = new MediaJobRequest { Type = "image", Prompt = "x" };
        Assert.False(CharacterBindingPolicy.Apply(request, null));
        Assert.False(CharacterBindingPolicy.Apply(request, new CharacterProfile { Id = "c2" }));
        Assert.Null(request.Loras);
        Assert.Equal("x", request.Prompt);
    }

    [Theory]
    [InlineData("mira.safetensors", 1.0, true, "zimage", true)]
    [InlineData("..\\evil.safetensors", 1.0, true, "zimage", false)]
    [InlineData("a/b.safetensors", 1.0, true, "zimage", false)]
    [InlineData("mira.ckpt", 1.0, true, "zimage", false)]
    [InlineData("mira.safetensors", 1.0, false, "zimage", false)]
    [InlineData("mira.safetensors", 0.0, true, "zimage", false)]
    [InlineData("mira.safetensors", 2.5, true, "zimage", false)]
    [InlineData("mira.safetensors", 1.0, true, "flux", false)]
    [InlineData("mira.safetensors", 1.0, true, null, true)]
    public void Binding_validation(string file, double strength, bool exists, string? family, bool ok) =>
        Assert.Equal(ok, CharacterBindingPolicy.CanBind(Bound(), file, strength, exists, family).Ok);

    [Fact]
    public void The_media_job_lookup_applies_a_character_before_validation_runs()
    {
        MediaJobService.CharacterLookup = id => id == "c1" ? Bound() : null;
        try
        {
            var request = new MediaJobRequest { Type = "image", Prompt = "a portrait", CharacterId = "c1" };
            // Create validates the LoRA against installed files, so a bound but missing file is refused rather than silently dropped.
            var jobs = new MediaJobService(new MediaGenerationService(null!, null!));
            var ex = Assert.Throws<InvalidDataException>(() => jobs.Create(request));
            Assert.Contains("mira.safetensors", ex.Message);
            Assert.Equal("prx_mira, a portrait", request.Prompt);
        }
        finally { MediaJobService.CharacterLookup = null; }
    }

    // ---- store, images, trainer component, locks ------------------------------------------------------------------------------

    private static byte[] Png(int w, int h)
    {
        var b = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    [Fact]
    public void Saved_pictures_record_size_and_hash_and_non_images_are_refused()
    {
        var store = new CharacterStudioStore(Path.Combine(_root, "s"), Path.Combine(_root, "d"));
        CharacterImage image = store.SaveImage("abc", Png(1024, 768), CharacterImageSource.User, "me.png", 0);
        Assert.Equal((1024, 768), (image.Width, image.Height));
        Assert.Equal(64, image.Sha256.Length);
        Assert.NotNull(store.ImagePath("abc", image.File));
        Assert.Null(store.ImagePath("abc", "..\\x.png"));
        Assert.Throws<InvalidDataException>(() => store.SaveImage("abc", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, CharacterImageSource.User, "x", 0));
        Assert.Throws<InvalidDataException>(() => CharacterStudioStore.SafeId("../x"));
    }

    [Fact]
    public void Locating_a_users_trainer_never_writes_into_it_and_validates_the_folder()
    {
        string trainer = Path.Combine(_root, "mytrainer");
        Directory.CreateDirectory(Path.Combine(trainer, "toolkit"));
        File.WriteAllText(Path.Combine(trainer, "run.py"), "print(1)");
        string py = Path.Combine(_root, "py", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(py)!);
        File.WriteAllText(py, "x");
        var runtime = new CharacterTrainerRuntime(null, Path.Combine(_root, "rt"));
        Assert.Equal("not_installed", runtime.GetStatus().State);
        Assert.Equal("skipped", runtime.Skip().State);
        string[] before = Directory.GetFileSystemEntries(trainer, "*", SearchOption.AllDirectories);
        CharacterTrainerStatus located = runtime.Locate(trainer, py);
        Assert.Equal(("ready", "USER_MANAGED"), (located.State, located.Ownership));
        Assert.Equal(before, Directory.GetFileSystemEntries(trainer, "*", SearchOption.AllDirectories));
        Assert.Throws<InvalidDataException>(() => runtime.Locate(_root, py));
        Assert.Equal(py, runtime.TryGetLaunch()!.Python);
    }

    [Fact]
    public void The_pinned_trainer_archive_and_lock_are_well_formed()
    {
        Assert.True(VerifiedDownload.IsValidSha256(CharacterTrainerRuntime.ArchiveSha256));
        Assert.StartsWith("https://", CharacterTrainerRuntime.ArchiveUrl);
        Assert.Contains(CharacterTrainerRuntime.Commit, CharacterTrainerRuntime.ArchiveUrl);
        Assert.Equal(40, CharacterTrainerRuntime.Commit.Length);
        string lockPath = Path.Combine(AppContext.BaseDirectory, "managed-locks", "character-trainer.lock.txt");
        Assert.True(ManagedRequirementLocks.Validate(lockPath) > 50);
        Assert.Contains("--require-hashes", ManagedRequirementLocks.ArgumentsFor("character-trainer"));
    }

    /// <summary>Opt-in helper for the real training smoke test: writes captions and the trainer config for a dataset folder that
    /// already holds pictures. Does nothing unless PARUX_CHARACTER_SMOKE_DIR is set, so the normal suite never touches a GPU.</summary>
    [Fact]
    public void Smoke_writer_prepares_a_dataset_and_config_when_asked()
    {
        string? dir = Environment.GetEnvironmentVariable("PARUX_CHARACTER_SMOKE_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        string dataset = Path.Combine(dir, "dataset");
        string[] pictures = Directory.GetFiles(dataset, "*.png");
        foreach (string picture in pictures)
            File.WriteAllText(Path.ChangeExtension(picture, ".txt"), CharacterCaptions.Format("prx_smoke", "cartoon face with red hair and green eyes"));
        var plan = CharacterLoraPresets.Select("quick", 16, pictures.Length, new CharacterTrainingAdvanced { Steps = int.Parse(Environment.GetEnvironmentVariable("PARUX_CHARACTER_SMOKE_STEPS") ?? "40") });
        File.WriteAllText(Path.Combine(dir, "config.yaml"),
            CharacterTrainerConfig.Build("prx_smoke", Path.Combine(dir, "output"), dataset, "prx_smoke", plan, pictures.Length, 7,
                new[] { "prx_smoke, cartoon face with red hair" }));
    }
}
