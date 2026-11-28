using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.CharacterStudio;

/// <summary>A refusal with a stable code and a next action the user can take.</summary>
internal sealed class CharacterStudioException(string code, string message, string nextAction) : InvalidOperationException(message)
{
    internal string Code { get; } = code;
    internal string NextAction { get; } = nextAction;
}

/// <summary>
/// Character Studio orchestration: reference pictures through the existing image jobs, captions, local LoRA training under the GPU lease
/// with Gaming hard-off, and binding the result to the character. State lives in <see cref="CharacterStudioStore"/>.
/// </summary>
internal sealed class CharacterStudioService : IDisposable
{
    private readonly CharacterStudioStore _store;
    private readonly CharacterTrainerRuntime _trainer;
    private readonly MediaJobService _jobs;
    private readonly LocalRuntimeService _runtime;
    private readonly PersonaService _personas;
    private readonly VisionSubagentService? _vision;
    private readonly GamingChildScope _scope;
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    private readonly Dictionary<string, Process> _processes = new();
    /// <summary>Test seam: VRAM of the training GPU in GiB. Null reads the real inventory.</summary>
    internal Func<double>? VramGibOverride { get; set; }

    public CharacterStudioService(CharacterStudioStore store, CharacterTrainerRuntime trainer, MediaJobService jobs,
        LocalRuntimeService runtime, PersonaService personas, VisionSubagentService? vision)
    {
        _store = store; _trainer = trainer; _jobs = jobs; _runtime = runtime; _personas = personas; _vision = vision;
        _scope = new GamingChildScope("character training", () => _runtime.GamingMode, _runtime.HardOff);
        MediaJobService.CharacterLookup = ResolveForJob;
    }

    internal CharacterStudioStore Store => _store;
    internal CharacterTrainerRuntime Trainer => _trainer;

    private CharacterProfile? ResolveForJob(string idOrActive) =>
        string.Equals(idOrActive, "active", StringComparison.OrdinalIgnoreCase)
            ? _store.ForPersona(_personas.GetActive().Id)
            : _store.Get(idOrActive) is { Binding: not null } c ? c : null;

    public void Dispose() => _scope.Dispose();

    // ---- characters -------------------------------------------------------------------------------------------------------------------

    internal CharacterProfile Create(CreateCharacterRequest request)
    {
        string name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new CharacterStudioException("name_missing", "A character needs a name.", "Type a name and continue.");
        if (name.Length > 80) name = name[..80];
        string appearance = (request.Appearance ?? string.Empty).Trim();
        if (appearance.Length < 8)
            throw new CharacterStudioException("appearance_missing", "Describe what the character looks like (hair, eyes, clothes, age).",
                "Write one or two sentences about the look, then continue.");
        if (appearance.Length > 1200) appearance = appearance[..1200];
        var now = DateTime.UtcNow;
        var c = new CharacterProfile
        {
            Id = Guid.NewGuid().ToString("N")[..16], Name = name, Appearance = appearance, PersonaId = Clean(request.PersonaId),
            TriggerWord = CharacterCaptions.MakeTriggerWord(name), BaseSeed = Random.Shared.NextInt64(1, int.MaxValue),
            CreatedUtc = now, UpdatedUtc = now
        };
        c.Training.Stage = CharacterStage.Draft;
        c.Training.UpdatedUtc = now;
        return _store.Add(c);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal CharacterProfile Update(string id, UpdateCharacterRequest r) => _store.Mutate(id, c =>
    {
        if (!string.IsNullOrWhiteSpace(r.Name)) c.Name = r.Name.Trim()[..Math.Min(80, r.Name.Trim().Length)];
        if (!string.IsNullOrWhiteSpace(r.Appearance)) c.Appearance = r.Appearance.Trim()[..Math.Min(1200, r.Appearance.Trim().Length)];
        if (r.PersonaId != null) c.PersonaId = Clean(r.PersonaId);
        if (!string.IsNullOrWhiteSpace(r.TriggerWord))
        {
            string word = r.TriggerWord.Trim().ToLowerInvariant();
            if (!CharacterCaptions.IsValidTriggerWord(word))
                throw new CharacterStudioException("trigger_invalid", "The trigger word must be 4 to 24 letters, digits or underscores, starting with a letter.", "Pick a simple made-up word such as prx_mira.");
            if (c.Binding != null || c.Training.Stage == CharacterStage.Training)
                throw new CharacterStudioException("trigger_locked", "The trigger word cannot change after training.", "Train again to use a new word.");
            c.TriggerWord = word;
            foreach (CharacterImage i in c.Images) i.Caption = string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(r.PortraitImageId))
            c.PortraitFile = c.Images.FirstOrDefault(i => i.Id == r.PortraitImageId)?.File ?? c.PortraitFile;
    });

    internal void Delete(string id)
    {
        Cancel(id);
        _store.Delete(id);
    }

    // ---- pictures ---------------------------------------------------------------------------------------------------------------------

    internal CharacterProfile AddUserImage(string id, string fileName, byte[] bytes)
    {
        CharacterImage image = _store.SaveImage(SafeCharacter(id).Id, bytes, CharacterImageSource.User, Path.GetFileName(fileName ?? "photo"), 0);
        return _store.Mutate(id, c =>
        {
            if (c.Images.Count >= CharacterDatasetPolicy.MaxImages)
                throw new CharacterStudioException("too_many", "This character already has " + CharacterDatasetPolicy.MaxImages + " pictures.", "Remove a few pictures first.");
            c.Images.Add(image);
            c.PortraitFile ??= image.File;
            MarkEdited(c);
        });
    }

    internal CharacterProfile RemoveImage(string id, string imageId) => _store.Mutate(id, c =>
    {
        CharacterImage? image = c.Images.FirstOrDefault(i => i.Id == imageId);
        if (image == null) return;
        c.Images.Remove(image);
        try { string? path = _store.ImagePath(id, image.File); if (path != null) File.Delete(path); } catch (IOException) { }
        if (c.PortraitFile == image.File) c.PortraitFile = c.Images.FirstOrDefault()?.File;
        MarkEdited(c);
    });

    private static void MarkEdited(CharacterProfile c)
    {
        if (c.Training.Stage is CharacterStage.Draft or CharacterStage.Ready or CharacterStage.Completed or CharacterStage.Failed or CharacterStage.Cancelled or CharacterStage.Interrupted or CharacterStage.Captioning)
            c.Training.Stage = c.Images.Count == 0 ? CharacterStage.Draft : CharacterStage.Collecting;
    }

    private CharacterProfile SafeCharacter(string id) => _store.Get(id) ?? throw new CharacterStudioException("unknown_character", "That character no longer exists.", "Go back to the character list.");

    /// <summary>Starts making reference pictures in the background; progress is in the character's status message.</summary>
    internal CharacterProfile StartGenerateReferences(string id, int count)
    {
        CharacterProfile c = SafeCharacter(id);
        if (_runtime.GamingMode) throw new UnavailableDuringGamingException("Gaming Mode is active, so pictures cannot be made right now.");
        int room = CharacterDatasetPolicy.MaxImages - c.Images.Count;
        count = Math.Min(Math.Clamp(count, 1, CharacterDatasetPolicy.MaxImages), room);
        if (count <= 0) throw new CharacterStudioException("too_many", "This character already has the most pictures it can use.", "Remove a few pictures first.");
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_running.ContainsKey(id)) throw new CharacterStudioException("busy", "This character is already busy.", "Wait for it to finish or cancel it.");
            _running[id] = cts;
        }
        int startIndex = c.Images.Count;
        CharacterProfile started = _store.Mutate(id, x =>
        {
            x.Training.Stage = CharacterStage.Collecting;
            x.Training.ErrorCode = x.Training.NextAction = string.Empty;
            x.Training.Message = "Making picture 1 of " + count + "...";
        });
        _ = Task.Run(() => GenerateAsync(started, count, startIndex, cts));
        return started;
    }

    private async Task GenerateAsync(CharacterProfile c, int count, int startIndex, CancellationTokenSource cts)
    {
        string? error = null, code = null, next = null;
        try
        {
            IReadOnlyList<CharacterReferencePlanner.Shot> shots = CharacterReferencePlanner.Plan(c.Name, c.Appearance, startIndex + count);
            for (int n = 0; n < count && !cts.IsCancellationRequested; n++)
            {
                int index = startIndex + n;
                CharacterReferencePlanner.Shot shot = shots[index];
                long seed = CharacterReferencePlanner.SeedFor(c.BaseSeed, index);
                MediaJobStatus job = _jobs.Create(new MediaJobRequest
                {
                    Type = "image", Prompt = shot.Prompt, Width = 1024, Height = 1024, Seed = seed, PromptAlreadyReviewed = true
                }, "character-studio");
                MediaJobStatus? done = await WaitForJobAsync(job.Id, cts.Token).ConfigureAwait(false);
                if (done is not { Status: "completed" } || done.Asset == null)
                {
                    error = done?.Error ?? "The picture could not be made.";
                    code = "image_failed";
                    next = "Open Create once to check that the image model is installed, then try again.";
                    break;
                }
                byte[] bytes = !string.IsNullOrEmpty(done.Asset.Base64Data) ? Convert.FromBase64String(done.Asset.Base64Data)
                    : done.Asset.FilePath != null && File.Exists(done.Asset.FilePath) ? await File.ReadAllBytesAsync(done.Asset.FilePath, cts.Token).ConfigureAwait(false)
                    : throw new InvalidDataException("The image job returned no picture.");
                CharacterImage image = _store.SaveImage(c.Id, bytes, CharacterImageSource.Generated, shot.Label, seed);
                int made = n + 1;
                _store.Mutate(c.Id, x =>
                {
                    x.Images.Add(image);
                    x.PortraitFile ??= image.File;
                    x.Training.Message = made < count ? "Making picture " + (made + 1) + " of " + count + "..." : "Pictures ready.";
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message; code = ex is UnavailableDuringGamingException ? "gaming_active" : "image_failed";
            next = code == "gaming_active" ? "Leave Gaming Mode, then try again." : "Check that the image model is installed, then try again.";
        }
        finally
        {
            lock (_gate) _running.Remove(c.Id);
            cts.Dispose();
        }
        _store.Mutate(c.Id, x =>
        {
            x.Training.Stage = x.Images.Count == 0 ? CharacterStage.Draft : CharacterStage.Collecting;
            if (error != null) { x.Training.Message = error; x.Training.ErrorCode = code ?? "image_failed"; x.Training.NextAction = next ?? string.Empty; }
            else if (x.Training.Message.StartsWith("Making", StringComparison.Ordinal)) x.Training.Message = "Stopped.";
        });
    }

    private async Task<MediaJobStatus?> WaitForJobAsync(string jobId, CancellationToken ct)
    {
        var limit = DateTime.UtcNow.AddMinutes(25);
        try
        {
            while (DateTime.UtcNow < limit)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                MediaJobStatus? s = _jobs.Get(jobId);
                if (s == null) return null;
                if (s.Status is "completed" or "failed" or "cancelled") return s;
            }
        }
        catch (OperationCanceledException) { _jobs.Cancel(jobId); throw; }
        _jobs.Cancel(jobId);
        return null;
    }

    // ---- captions ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Writes a caption for every picture that lacks one: vision model for user photos when one is available, shot text for made pictures.</summary>
    internal async Task<CharacterProfile> CaptionAsync(string id, CancellationToken ct)
    {
        CharacterProfile c = SafeCharacter(id);
        var missing = c.Images.Where(i => !CharacterCaptions.HasTrigger(i.Caption, c.TriggerWord)).ToList();
        if (missing.Count == 0) return c;
        var described = new Dictionary<string, string>();
        var photos = missing.Where(i => i.Source == CharacterImageSource.User).ToList();
        if (photos.Count > 0 && _vision != null && !_runtime.GamingMode)
        {
            try
            {
                var batch = photos.Select(i => (i.Id, File.ReadAllBytes(_store.ImagePath(id, i.File)!),
                    i.File.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : i.File.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/jpeg")).ToList();
                foreach (var kv in await _vision.DescribeForCaptionsAsync(batch, ct).ConfigureAwait(false)) described[kv.Key] = kv.Value;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* no vision model: user photos get the plain trigger caption below */ }
        }
        return _store.Mutate(id, x =>
        {
            foreach (CharacterImage i in x.Images.Where(m => missing.Any(z => z.Id == m.Id)))
                i.Caption = CharacterCaptions.Format(x.TriggerWord,
                    described.TryGetValue(i.Id, out string? d) ? d : i.Source == CharacterImageSource.Generated ? i.Label : "photo");
        });
    }

    // ---- plan and training ------------------------------------------------------------------------------------------------------------

    private double VramGib()
    {
        if (VramGibOverride != null) return VramGibOverride();
        var gpus = TrainingLabService.DetectAvailableGpus();
        return gpus.Count == 0 ? 0 : gpus.Max(g => g.VramGib);
    }

    internal CharacterTrainingPlan Plan(string id, string? preset, CharacterTrainingAdvanced? advanced)
    {
        CharacterProfile c = SafeCharacter(id);
        CharacterTrainingPlan plan = CharacterLoraPresets.Select(preset, VramGib(), c.Images.Count, advanced ?? c.Advanced);
        foreach (CharacterDatasetFinding f in CharacterDatasetPolicy.Validate(c.Images, c.TriggerWord)) plan.Findings.Add(f);
        CharacterTrainerStatus t = _trainer.GetStatus();
        if (t.State != "ready")
            plan.Findings.Add(new CharacterDatasetFinding
            {
                Code = "trainer_missing", Blocking = true,
                Message = "The training engine is not set up.", NextAction = "Install it from this page (one-time download), or point PaRuX at your own."
            });
        if (!plan.Supported)
            plan.Findings.Add(new CharacterDatasetFinding { Code = "gpu_too_small", Blocking = true, Message = plan.Note, NextAction = "Character training needs an NVIDIA card with 12 GB or more." });
        plan.CanStart = plan.Supported && !CharacterDatasetPolicy.HasBlocking(plan.Findings);
        return plan;
    }

    private string JobName(string id) => "prx_" + id;
    private string OutputDir(string id) => Path.Combine(_store.RunDir(id), "output", JobName(id));

    private static IEnumerable<string> Checkpoints(string outDir) => Directory.Exists(outDir)
        ? Directory.EnumerateFiles(outDir, "*.safetensors").Where(f => !f.EndsWith(".parux-lora.json", StringComparison.Ordinal))
        : Enumerable.Empty<string>();

    internal bool HasCheckpoint(string id) => Checkpoints(OutputDir(id)).Any();

    internal async Task<CharacterProfile> StartTrainingAsync(string id, StartCharacterTrainingRequest request, bool resume, CancellationToken ct)
    {
        CharacterProfile c = SafeCharacter(id);
        if (_runtime.GamingMode) throw new UnavailableDuringGamingException("Gaming Mode is active, so training cannot start. Leave Gaming Mode first.");
        if (resume && !CharacterStageMachine.CanResume(c.Training.Stage, HasCheckpoint(id)))
            throw new CharacterStudioException("cannot_resume", "There is no saved step to continue from.", "Start training again.");
        if (!resume && !CharacterStageMachine.CanMove(c.Training.Stage, CharacterStage.Training))
            throw new CharacterStudioException("busy", "This character is busy right now.", "Wait for the current step to finish.");

        CharacterTrainingAdvanced? adv = request.Advanced ?? c.Advanced;
        CharacterTrainingPlan plan = Plan(id, resume ? c.Preset : request.Preset, adv);
        CharacterDatasetFinding? block = plan.Findings.FirstOrDefault(f => f.Blocking);
        if (block != null)
        {
            string code = block.Code == "too_few" ? "too_few_pictures" : block.Code;
            throw new CharacterStudioException(code, block.Message, block.NextAction);
        }
        CharacterTrainerLaunch launch = _trainer.TryGetLaunch() ?? throw new CharacterStudioException("trainer_missing", "The training engine is not set up.", "Install it from this page.");

        c = await CaptionAsync(id, ct).ConfigureAwait(false);
        string runDir = _store.RunDir(id), dataset = Path.Combine(runDir, "dataset");
        if (!resume && Directory.Exists(OutputDir(id))) Directory.Delete(OutputDir(id), true);
        Directory.CreateDirectory(runDir);
        if (Directory.Exists(dataset)) Directory.Delete(dataset, true);
        Directory.CreateDirectory(dataset);
        int n = 0;
        foreach (CharacterImage i in c.Images)
        {
            string? src = _store.ImagePath(id, i.File);
            if (src == null) continue;
            string stem = "img" + (++n).ToString("000");
            File.Copy(src, Path.Combine(dataset, stem + Path.GetExtension(i.File)), true);
            File.WriteAllText(Path.Combine(dataset, stem + ".txt"), i.Caption, new UTF8Encoding(false));
        }
        string yaml = CharacterTrainerConfig.Build(JobName(id), Path.Combine(runDir, "output"), dataset, c.TriggerWord, plan, n, c.BaseSeed,
            SamplePrompts(c));
        string configPath = Path.Combine(runDir, "config.yaml");
        File.WriteAllText(configPath, yaml, new UTF8Encoding(false));

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_running.ContainsKey(id)) { cts.Dispose(); throw new CharacterStudioException("busy", "This character is already busy.", "Wait for it to finish or cancel it."); }
            _running[id] = cts;
        }
        CharacterProfile started = _store.Mutate(id, x =>
        {
            x.Preset = plan.Preset;
            x.Advanced = adv ?? new CharacterTrainingAdvanced();
            x.Training = new CharacterTrainingStatus
            {
                Stage = CharacterStage.Training, Preset = plan.Preset, Message = resume ? "Resuming..." : "Getting the graphics card ready...",
                TotalSteps = plan.Steps, TotalEpochs = plan.Epochs, Samples = new List<string>(), UpdatedUtc = DateTime.UtcNow
            };
        });
        _ = Task.Run(() => RunTrainingAsync(id, plan, launch, configPath, resume, cts));
        return started;
    }

    private static IReadOnlyList<string> SamplePrompts(CharacterProfile c) => new[]
    {
        c.TriggerWord + ", portrait, smiling, soft light",
        c.TriggerWord + ", full body, standing outdoors",
        c.TriggerWord + ", three-quarter view, indoors, serious expression"
    };

    private async Task RunTrainingAsync(string id, CharacterTrainingPlan plan, CharacterTrainerLaunch launch, string configPath, bool resume, CancellationTokenSource cts)
    {
        string finalStage = CharacterStage.Failed;
        string message = string.Empty, code = string.Empty, next = string.Empty;
        var tail = new Queue<string>();
        try
        {
            // The text model must be unloaded under its own lease first; the training lease then keeps text and media off the GPU.
            await _runtime.UnloadTextExclusivelyAsync(cts.Token).ConfigureAwait(false);
            await using MediaExecutionCoordinator.Lease lease = await MediaExecutionCoordinator.Current.AcquireAsync("character-lora-training", cts.Token).ConfigureAwait(false);
            await _runtime.PrepareForExclusiveTrainingAsync(cts.Token).ConfigureAwait(false);

            long ticket = _scope.Capture();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _scope.Token);
            var psi = new ProcessStartInfo(launch.Python)
            {
                WorkingDirectory = launch.WorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-u"); psi.ArgumentList.Add(launch.RunScript); psi.ArgumentList.Add(configPath);
            if (launch.Ownership == CharacterTrainerRuntime.Managed) psi.Environment["HF_HOME"] = _trainer.HfHome;
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
            psi.Environment["DISABLE_TELEMETRY"] = "YES";
            psi.Environment["PYTORCH_CUDA_ALLOC_CONF"] = "expandable_segments:True";
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            lock (_gate) _processes[id] = process;
            using var _ = linked.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception) { } });

            int lastSample = 0;
            void OnText(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                lock (tail) { tail.Enqueue(text.Length > 400 ? text[..400] : text); while (tail.Count > 60) tail.Dequeue(); }
                TrainerProgress? p = CharacterTrainerProgressParser.Parse(text);
                if (p == null)
                {
                    string low = text.ToLowerInvariant();
                    string? stage = low.Contains("loading") || low.Contains("quantiz") ? "Loading the base model..." : low.Contains("caching latents") ? "Preparing pictures..." : null;
                    if (stage != null) _store.Mutate(id, x => { if (x.Training.Stage == CharacterStage.Training) x.Training.Message = stage; });
                    return;
                }
                int perEpoch = Math.Max(1, p.Total / Math.Max(1, plan.Epochs));
                List<string> samples = ListSamples(id);
                bool newSamples = samples.Count != lastSample;
                lastSample = samples.Count;
                _store.Mutate(id, x =>
                {
                    if (x.Training.Stage != CharacterStage.Training) return;
                    x.Training.Step = p.Step; x.Training.TotalSteps = p.Total;
                    x.Training.Epoch = CharacterTrainerProgressParser.EpochOf(p.Step, perEpoch);
                    x.Training.Loss = p.Loss; x.Training.SecondsPerStep = p.SecondsPerStep; x.Training.EtaSeconds = p.EtaSeconds;
                    x.Training.Message = "Training: step " + p.Step + " of " + p.Total;
                    if (newSamples) x.Training.Samples = samples;
                    x.Training.CanResume = HasCheckpoint(id);
                });
            }

            _scope.StartOwned(process, ticket); // starts the child inside the Gaming admission gate
            Task pumpOut = PumpAsync(process.StandardOutput, OnText);
            Task pumpErr = PumpAsync(process.StandardError, OnText);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(pumpOut, pumpErr).ConfigureAwait(false);
            _scope.Unregister(process);

            string outDir = OutputDir(id);
            string finalFile = Path.Combine(outDir, JobName(id) + ".safetensors");
            if (_scope.Token.IsCancellationRequested || _runtime.GamingMode)
            {
                finalStage = CharacterStage.Interrupted; message = "Paused because Gaming Mode started."; next = "Press Resume after Gaming to continue from the last saved step.";
                code = "gaming_active";
            }
            else if (cts.IsCancellationRequested) { finalStage = CharacterStage.Cancelled; message = "Training was cancelled."; next = HasCheckpoint(id) ? "Press Resume to continue from the last saved step." : "Start again when you are ready."; }
            else if (process.ExitCode == 0 && File.Exists(finalFile))
            {
                string loraName = PublishLora(id, finalFile);
                finalStage = CharacterStage.Completed; message = "Done. " + loraName + " is attached to the character.";
            }
            else
            {
                string log; lock (tail) log = string.Join("\n", tail);
                CharacterTrainingErrors.Friendly f = CharacterTrainingErrors.Classify(log, process.ExitCode);
                finalStage = CharacterStage.Failed; message = f.Message; code = f.Code; next = f.NextAction;
            }
        }
        catch (OperationCanceledException)
        {
            bool gaming = _scope.Token.IsCancellationRequested || _runtime.GamingMode;
            finalStage = gaming ? CharacterStage.Interrupted : CharacterStage.Cancelled;
            message = gaming ? "Paused because Gaming Mode started." : "Training was cancelled.";
            code = gaming ? "gaming_active" : string.Empty;
            next = HasCheckpoint(id) ? "Press Resume to continue from the last saved step." : "Start again when you are ready.";
        }
        catch (UnavailableDuringGamingException ex) { finalStage = CharacterStage.Interrupted; message = ex.Message; code = "gaming_active"; next = "Leave Gaming Mode, then press Resume."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string log; lock (tail) log = string.Join("\n", tail) + "\n" + ex.Message;
            CharacterTrainingErrors.Friendly f = CharacterTrainingErrors.Classify(log, -1);
            finalStage = CharacterStage.Failed; message = f.Code == "trainer_failed" ? ex.Message : f.Message; code = f.Code; next = f.NextAction;
        }
        finally
        {
            lock (_gate) { _running.Remove(id); _processes.Remove(id); }
            cts.Dispose();
        }
        _store.Mutate(id, x =>
        {
            x.Training.Stage = finalStage; x.Training.Message = message; x.Training.ErrorCode = code; x.Training.NextAction = next;
            x.Training.EtaSeconds = null; x.Training.CanResume = CharacterStageMachine.CanResume(finalStage, HasCheckpoint(id));
            x.Training.Samples = ListSamples(id);
        });
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onText)
    {
        // tqdm redraws with carriage returns, so split on both CR and LF.
        var sb = new StringBuilder();
        char[] buf = new char[2048];
        int n;
        while ((n = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                if (buf[i] is '\r' or '\n') { if (sb.Length > 0) { onText(sb.ToString()); sb.Clear(); } }
                else if (sb.Length < 4000) sb.Append(buf[i]);
            }
        }
        if (sb.Length > 0) onText(sb.ToString());
    }

    private List<string> ListSamples(string id)
    {
        string dir = Path.Combine(OutputDir(id), "samples");
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => File.GetLastWriteTimeUtc(f)).Select(Path.GetFileName).Where(f => f != null).Select(f => f!).ToList()
            : new List<string>();
    }

    internal string? SamplePath(string id, string file)
    {
        if (string.IsNullOrEmpty(file) || file != Path.GetFileName(file)) return null;
        string path = Path.Combine(OutputDir(id), "samples", file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Copies the finished LoRA into ComfyUI's loras folder with a sidecar, then binds it to the character.</summary>
    private string PublishLora(string id, string trainedFile)
    {
        CharacterProfile c = SafeCharacter(id);
        string slug = new string(c.Name.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "character";
        string name = "parux-character-" + slug + "-" + id[..6] + ".safetensors";
        Directory.CreateDirectory(LoraLibrary.DefaultFolder);
        string target = Path.Combine(LoraLibrary.DefaultFolder, name);
        File.Copy(trainedFile, target, true);
        File.WriteAllText(target + LoraLibrary.SidecarSuffix, JsonSerializer.Serialize(new
        {
            baseModel = "Z-Image Turbo", source = "PaRuX Character Studio", triggerWords = new[] { c.TriggerWord }
        }));
        _store.Mutate(id, x => x.Binding = new CharacterLoraBinding
        {
            LoraFile = name, TriggerWord = x.TriggerWord, Strength = CharacterBindingPolicy.DefaultStrength, Family = x.BaseFamily, BoundUtc = DateTime.UtcNow
        });
        return name;
    }

    internal bool Cancel(string id)
    {
        CancellationTokenSource? cts;
        lock (_gate) _running.TryGetValue(id, out cts);
        if (cts == null) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        lock (_gate) if (_processes.TryGetValue(id, out Process? p)) { try { p.Kill(entireProcessTree: true); } catch (Exception) { } }
        return true;
    }

    // ---- binding ----------------------------------------------------------------------------------------------------------------------

    internal CharacterProfile Bind(string id, BindCharacterLoraRequest r)
    {
        CharacterProfile c = SafeCharacter(id);
        InstalledLora? lora = LoraLibrary.List().FirstOrDefault(l => string.Equals(l.Name, r.LoraFile, StringComparison.Ordinal));
        string? family = lora == null ? null : MediaLoraStack.LoraKey(lora);
        (bool ok, string? error) = CharacterBindingPolicy.CanBind(c, r.LoraFile, r.Strength, lora != null, family);
        if (!ok) throw new CharacterStudioException("bind_refused", error!, "Pick a LoRA trained for " + MediaLoraStack.DisplayName(c.BaseFamily) + " from the list.");
        return _store.Mutate(id, x => x.Binding = new CharacterLoraBinding
        {
            LoraFile = r.LoraFile, TriggerWord = x.TriggerWord, Strength = Math.Round(r.Strength, 3), Family = x.BaseFamily, BoundUtc = DateTime.UtcNow
        });
    }

    internal string? ActivePortraitPath()
    {
        CharacterProfile? c = _store.ForPersona(_personas.GetActive().Id);
        return c?.PortraitFile == null ? null : _store.ImagePath(c.Id, c.PortraitFile);
    }

    internal CharacterProfile Unbind(string id) => _store.Mutate(id, x => x.Binding = null);

    /// <summary>Training status with the resume flag recomputed from disk, so a restart shows the right buttons.</summary>
    internal CharacterProfile Get(string id)
    {
        CharacterProfile c = SafeCharacter(id);
        c.Training.CanResume = CharacterStageMachine.CanResume(c.Training.Stage, HasCheckpoint(id));
        return c;
    }
}
