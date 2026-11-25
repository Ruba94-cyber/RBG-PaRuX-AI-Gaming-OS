using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MultiGpuTrainingTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static TrainingDatasetShape.Shape ChatDataset(int count = 200) =>
        TrainingDatasetShape.Inspect(Enumerable.Range(0, count).Select(i =>
            "{\"messages\":[{\"role\":\"user\",\"content\":\"q" + i + "\"},{\"role\":\"assistant\",\"content\":\"a" + i + "\"}]}"));

    private static IReadOnlyList<TrainingRunPlanner.GpuDeviceSpec> TwoBy16Gb => new[]
    {
        new TrainingRunPlanner.GpuDeviceSpec(0, "GPU A 16GB", 16.0),
        new TrainingRunPlanner.GpuDeviceSpec(1, "GPU B 16GB", 16.0)
    };

    private static IReadOnlyList<TrainingRunPlanner.GpuDeviceSpec> TwoBy24Gb => new[]
    {
        new TrainingRunPlanner.GpuDeviceSpec(0, "GPU A 24GB", 24.0),
        new TrainingRunPlanner.GpuDeviceSpec(1, "GPU B 24GB", 24.0)
    };

    private static IReadOnlyList<TrainingRunPlanner.GpuDeviceSpec> Single16Gb => new[]
    {
        new TrainingRunPlanner.GpuDeviceSpec(0, "GPU A 16GB", 16.0)
    };

    // ---- Contracts ----

    [Theory]
    [InlineData(null, TrainingMultiGpuStrategy.Single)]
    [InlineData("", TrainingMultiGpuStrategy.Single)]
    [InlineData("SINGLE", TrainingMultiGpuStrategy.Single)]
    [InlineData("device-map-auto", TrainingMultiGpuStrategy.DeviceMapAuto)]
    [InlineData("DATA-PARALLEL", TrainingMultiGpuStrategy.DataParallel)]
    public void Strategy_normalization_accepts_known_modes_and_defaults_to_single(string? raw, string expected) =>
        Assert.Equal(expected, TrainingMultiGpuStrategy.Normalize(raw));

    [Theory]
    [InlineData("fsdp")]
    [InlineData("deepspeed")]
    [InlineData("auto")]
    public void Unknown_multi_gpu_strategy_is_refused(string raw) =>
        Assert.Throws<InvalidDataException>(() => TrainingMultiGpuStrategy.Normalize(raw));

    [Fact]
    public void Requests_default_to_single_gpu_and_round_trip_multi_gpu_fields()
    {
        var defaults = new TrainingJobRequest();
        Assert.Equal(TrainingMultiGpuStrategy.Single, defaults.MultiGpuStrategy);
        Assert.Null(defaults.GpuDeviceIds);

        var multi = new TrainingJobRequest
        {
            TrainJsonlPath = "train.jsonl",
            BaseModel = "unsloth/Qwen2.5-14B-Instruct-bnb-4bit",
            MultiGpuStrategy = TrainingMultiGpuStrategy.DataParallel,
            GpuDeviceIds = new[] { 0, 1 }
        };
        string json = JsonSerializer.Serialize(multi);
        TrainingJobRequest restored = JsonSerializer.Deserialize<TrainingJobRequest>(json)!;
        Assert.Equal(TrainingMultiGpuStrategy.DataParallel, restored.MultiGpuStrategy);
        Assert.Equal(new[] { 0, 1 }, restored.GpuDeviceIds);
    }

    // ---- Planner: 2x16GB and 2x24GB ----

    [Fact]
    public void DeviceMapAuto_on_two_16gb_gpus_fits_24b_4bit_that_single_16gb_blocks()
    {
        TrainingRunPlanner.RunPlan single = TrainingRunPlanner.Plan(
            ChatDataset(), TrainingMethod.Sft, "unsloth/Qwen2.5-24B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 100, learningRate: 2e-4, availableGib: 16,
            multiGpuStrategy: TrainingMultiGpuStrategy.Single, availableGpus: Single16Gb);
        Assert.False(single.CanStart);
        Assert.Contains(single.Blocking, f => f.Field == "baseModel");

        TrainingRunPlanner.RunPlan split = TrainingRunPlanner.Plan(
            ChatDataset(), TrainingMethod.Sft, "unsloth/Qwen2.5-24B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 100, learningRate: 2e-4, availableGib: 16,
            multiGpuStrategy: TrainingMultiGpuStrategy.DeviceMapAuto, availableGpus: TwoBy16Gb);
        Assert.True(split.CanStart);
        Assert.Equal(TrainingMultiGpuStrategy.DeviceMapAuto, split.MultiGpuStrategy);
        Assert.Equal(32.0, split.AvailableGib, 2);
        Assert.Equal(1, split.WorldSize);
        Assert.Equal(new[] { 0, 1 }, split.SelectedGpuDeviceIds);
    }

    [Fact]
    public void DeviceMapAuto_on_two_24gb_gpus_fits_32b_4bit_while_two_16gb_blocks_it()
    {
        TrainingRunPlanner.RunPlan on16 = TrainingRunPlanner.Plan(
            ChatDataset(), TrainingMethod.Sft, "unsloth/Qwen2.5-32B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 100, learningRate: 2e-4, availableGib: 16,
            multiGpuStrategy: TrainingMultiGpuStrategy.DeviceMapAuto, availableGpus: TwoBy16Gb);
        Assert.False(on16.CanStart);
        Assert.Contains(on16.Blocking, f => f.Field == "baseModel");

        TrainingRunPlanner.RunPlan on24 = TrainingRunPlanner.Plan(
            ChatDataset(), TrainingMethod.Sft, "unsloth/Qwen2.5-32B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 100, learningRate: 2e-4, availableGib: 24,
            multiGpuStrategy: TrainingMultiGpuStrategy.DeviceMapAuto, availableGpus: TwoBy24Gb);
        Assert.True(on24.CanStart);
        Assert.Equal(48.0, on24.AvailableGib, 2);
        Assert.Equal(new[] { 0, 1 }, on24.SelectedGpuDeviceIds);
    }

    [Fact]
    public void DataParallel_on_two_24gb_gpus_scales_world_size_and_effective_batch_and_enforces_per_gpu_vram()
    {
        TrainingRunPlanner.RunPlan fits = TrainingRunPlanner.Plan(
            ChatDataset(), TrainingMethod.Sft, "unsloth/Qwen2.5-14B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 100, learningRate: 2e-4, availableGib: 24,
            multiGpuStrategy: TrainingMultiGpuStrategy.DataParallel,
            gpuDeviceIds: new[] { 0, 1 }, availableGpus: TwoBy24Gb);
        Assert.True(fits.CanStart);
        Assert.Equal(TrainingMultiGpuStrategy.DataParallel, fits.MultiGpuStrategy);
        Assert.Equal(2, fits.WorldSize);
        Assert.Equal(8, fits.EffectiveBatchSize);
        Assert.Equal(24.0, fits.AvailableGib, 2);

        // 32B 4-bit (~36.96 GB with DDP overhead) does not fit on a single 24 GB replica;
        // planner must block honestly and suggest device-map-auto.
        TrainingRunPlanner.RunPlan tooLargePerGpu = TrainingRunPlanner.Plan(
            ChatDataset(), TrainingMethod.Sft, "unsloth/Qwen2.5-32B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 100, learningRate: 2e-4, availableGib: 24,
            multiGpuStrategy: TrainingMultiGpuStrategy.DataParallel,
            gpuDeviceIds: new[] { 0, 1 }, availableGpus: TwoBy24Gb);
        Assert.False(tooLargePerGpu.CanStart);
        Assert.Contains(tooLargePerGpu.Blocking,
            f => f.Field == "baseModel" && f.Message.Contains("device-map-auto"));
    }

    [Fact]
    public void DataParallel_on_heterogeneous_gpus_warns_and_uses_smallest_gpu_vram()
    {
        var uneven = new[]
        {
            new TrainingRunPlanner.GpuDeviceSpec(0, "GPU 24GB", 24.0),
            new TrainingRunPlanner.GpuDeviceSpec(1, "GPU 12GB", 12.0)
        };
        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            ChatDataset(), TrainingMethod.Sft, "unsloth/Qwen2.5-7B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 100, learningRate: 2e-4, availableGib: 24,
            multiGpuStrategy: TrainingMultiGpuStrategy.DataParallel, availableGpus: uneven);
        Assert.True(plan.CanStart);
        Assert.Equal(12.0, plan.AvailableGib, 2);
        Assert.Contains(plan.Warnings, f => f.Field == "gpuDeviceIds" && f.Message.Contains("uneven VRAM"));
    }

    // ---- Honest refusal on 1 GPU & invalid GPU IDs ----

    [Theory]
    [InlineData(TrainingMultiGpuStrategy.DeviceMapAuto)]
    [InlineData(TrainingMultiGpuStrategy.DataParallel)]
    public void Multi_gpu_strategy_is_honestly_blocked_when_only_one_gpu_is_available(string strategy)
    {
        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            ChatDataset(), TrainingMethod.Sft, "unsloth/Qwen2.5-7B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 100, learningRate: 2e-4, availableGib: 16,
            multiGpuStrategy: strategy, availableGpus: Single16Gb);
        Assert.False(plan.CanStart);
        Assert.Contains(plan.Blocking, f => f.Field == "multiGpuStrategy" && f.Message.Contains("at least 2"));
    }

    [Theory]
    [InlineData(new[] { -1, 0 })]
    [InlineData(new[] { 0, 0 })]
    [InlineData(new[] { 0, 64 })]
    [InlineData(new[] { 0, 2 })]
    public void Invalid_or_duplicate_or_missing_gpu_ids_are_blocked_by_planner(int[] gpuIds)
    {
        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            ChatDataset(), TrainingMethod.Sft, "unsloth/Qwen2.5-7B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 100, learningRate: 2e-4, availableGib: 16,
            multiGpuStrategy: TrainingMultiGpuStrategy.DataParallel,
            gpuDeviceIds: gpuIds, availableGpus: TwoBy16Gb);
        Assert.False(plan.CanStart);
        Assert.Contains(plan.Blocking, f => f.Field == "gpuDeviceIds");
    }

    [Fact]
    public void Single_gpu_strategy_rejects_multiple_gpu_ids_and_multi_gpu_rejects_single_id()
    {
        TrainingRunPlanner.RunPlan singleWithTwo = TrainingRunPlanner.Plan(
            ChatDataset(), TrainingMethod.Sft, "unsloth/Qwen2.5-7B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 100, learningRate: 2e-4, availableGib: 16,
            multiGpuStrategy: TrainingMultiGpuStrategy.Single,
            gpuDeviceIds: new[] { 0, 1 }, availableGpus: TwoBy16Gb);
        Assert.False(singleWithTwo.CanStart);
        Assert.Contains(singleWithTwo.Blocking, f => f.Field == "gpuDeviceIds");

        TrainingRunPlanner.RunPlan multiWithOne = TrainingRunPlanner.Plan(
            ChatDataset(), TrainingMethod.Sft, "unsloth/Qwen2.5-7B-Instruct-bnb-4bit",
            loraRank: 16, maxSteps: 100, learningRate: 2e-4, availableGib: 16,
            multiGpuStrategy: TrainingMultiGpuStrategy.DataParallel,
            gpuDeviceIds: new[] { 0 }, availableGpus: TwoBy16Gb);
        Assert.False(multiWithOne.CanStart);
        Assert.Contains(multiWithOne.Blocking, f => f.Field == "gpuDeviceIds");
    }

    [Fact]
    public void Batch_shape_policy_scales_gradient_accumulation_with_world_size()
    {
        var single8 = TrainingBatchShapePolicy.PlanMultiGpu(2048, 8 * Gb, worldSize: 1);
        var ddp8 = TrainingBatchShapePolicy.PlanMultiGpu(2048, 8 * Gb, worldSize: 2);
        var ddp16 = TrainingBatchShapePolicy.PlanMultiGpu(2048, 16 * Gb, worldSize: 2);

        Assert.Equal(4, single8.EffectiveBatch);
        Assert.True(ddp8.GradientAccumulation <= single8.GradientAccumulation);
        Assert.Equal(ddp8.MicroBatch * ddp8.GradientAccumulation * 2, ddp8.EffectiveBatch);
        Assert.Equal(8, ddp16.EffectiveBatch);
    }

    // ---- Runner & Service: CUDA_VISIBLE_DEVICES, max_memory, torchrun, script ----

    [Fact]
    public void ResolveCudaGpuSpecs_prefers_nvidia_discrete_adapters_and_skips_software()
    {
        var inventory = new GpuInventoryStatus
        {
            Available = true,
            Adapters = new List<GpuAdapterStatus>
            {
                new() { Index = 0, Name = "Integrated GPU", VendorId = 0x8086, DedicatedVideoMemoryBytes = 512L * 1024 * 1024, IsSoftware = false },
                new() { Index = 1, Name = "NVIDIA RTX A", VendorId = 0x10DE, DedicatedVideoMemoryBytes = 16 * Gb, IsSoftware = false },
                new() { Index = 2, Name = "NVIDIA RTX B", VendorId = 0x10DE, DedicatedVideoMemoryBytes = 24 * Gb, IsSoftware = false },
                new() { Index = 3, Name = "Microsoft Basic Render Driver", VendorId = 0x1414, DedicatedVideoMemoryBytes = 0, IsSoftware = true }
            }
        };

        IReadOnlyList<TrainingRunPlanner.GpuDeviceSpec> specs = TrainingLabService.ResolveCudaGpuSpecs(inventory);
        Assert.Equal(2, specs.Count);
        Assert.Equal(0, specs[0].Index);
        Assert.Equal(16.0, specs[0].VramGib, 2);
        Assert.Equal(1, specs[1].Index);
        Assert.Equal(24.0, specs[1].VramGib, 2);
    }

    [Fact]
    public void BuildCudaVisibleDevices_formats_validated_integers_and_rejects_invalid_inputs()
    {
        Assert.Null(TrainingLabService.BuildCudaVisibleDevices(null));
        Assert.Null(TrainingLabService.BuildCudaVisibleDevices(Array.Empty<int>()));
        Assert.Equal("0,1", TrainingLabService.BuildCudaVisibleDevices(new[] { 0, 1 }));
        Assert.Equal("1", TrainingLabService.BuildCudaVisibleDevices(new[] { 1 }));

        Assert.Throws<InvalidDataException>(() => TrainingLabService.BuildCudaVisibleDevices(new[] { -1 }));
        Assert.Throws<InvalidDataException>(() => TrainingLabService.BuildCudaVisibleDevices(new[] { 0, 0 }));
        Assert.Throws<InvalidDataException>(() => TrainingLabService.BuildCudaVisibleDevices(new[] { 99 }));
    }

    [Fact]
    public void ComputePerGpuMaxMemoryGib_reserves_headroom_per_selected_gpu()
    {
        Assert.Equal(new[] { 14.0, 14.0 }, TrainingLabService.ComputePerGpuMaxMemoryGib(TwoBy16Gb, new[] { 0, 1 }));
        Assert.Equal(new[] { 21.0, 21.0 }, TrainingLabService.ComputePerGpuMaxMemoryGib(TwoBy24Gb, null));
    }

    [Fact]
    public void ProcessStartInfo_sets_cuda_visible_devices_and_uses_torchrun_for_data_parallel()
    {
        ProcessStartInfo single = TrainingLabService.BuildTrainingProcessStartInfo(
            @"C:\venv\Scripts\python.exe", @"C:\runtime", @"C:\runtime\parux_train.py", @"C:\runtime\cfg.json",
            TrainingMultiGpuStrategy.Single, new[] { 1 }, detectedGpuCount: 2);
        Assert.Equal(@"C:\venv\Scripts\python.exe", single.FileName);
        Assert.Equal("1", single.Environment["CUDA_VISIBLE_DEVICES"]);
        Assert.Equal(new[] { @"C:\runtime\parux_train.py", @"C:\runtime\cfg.json" }, single.ArgumentList);

        ProcessStartInfo autoSplit = TrainingLabService.BuildTrainingProcessStartInfo(
            @"C:\venv\Scripts\python.exe", @"C:\runtime", @"C:\runtime\parux_train.py", @"C:\runtime\cfg.json",
            TrainingMultiGpuStrategy.DeviceMapAuto, new[] { 0, 1 }, detectedGpuCount: 2);
        Assert.Equal(@"C:\venv\Scripts\python.exe", autoSplit.FileName);
        Assert.Equal("0,1", autoSplit.Environment["CUDA_VISIBLE_DEVICES"]);
        Assert.Equal(new[] { @"C:\runtime\parux_train.py", @"C:\runtime\cfg.json" }, autoSplit.ArgumentList);

        ProcessStartInfo ddp = TrainingLabService.BuildTrainingProcessStartInfo(
            @"C:\venv\Scripts\python.exe", @"C:\runtime", @"C:\runtime\parux_train.py", @"C:\runtime\cfg.json",
            TrainingMultiGpuStrategy.DataParallel, new[] { 0, 1 }, detectedGpuCount: 2);
        Assert.Equal("0,1", ddp.Environment["CUDA_VISIBLE_DEVICES"]);
        Assert.Contains("--standalone", ddp.ArgumentList);
        Assert.Contains("--nproc_per_node=2", ddp.ArgumentList);
        Assert.Contains(@"C:\runtime\parux_train.py", ddp.ArgumentList);

        Assert.Throws<InvalidOperationException>(() => TrainingLabService.BuildTrainingProcessStartInfo(
            @"C:\venv\Scripts\python.exe", @"C:\runtime", @"C:\runtime\parux_train.py", @"C:\runtime\cfg.json",
            TrainingMultiGpuStrategy.DataParallel, null, detectedGpuCount: 1));
    }

    [Fact]
    public void Embedded_runner_script_wires_device_map_auto_max_memory_ddp_and_single_gpu_fallback()
    {
        FieldInfo? field = typeof(TrainingLabService).GetField("TrainScriptSource",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        string script = Assert.IsType<string>(field!.GetRawConstantValue());

        Assert.Contains("multi_gpu_strategy", script);
        Assert.Contains("gpu_max_memory_gib", script);
        Assert.Contains("device_map", script);
        Assert.Contains("max_memory", script);
        Assert.Contains("ddp_find_unused_parameters", script);
        Assert.Contains("torch.cuda.device_count()", script);
        Assert.Contains("Falling back to single-GPU execution.", script);
        Assert.Contains("if is_main_process:", script);
    }

    [Fact]
    public void ValidateRequest_rejects_invalid_multi_gpu_combinations()
    {
        var baseReq = new TrainingJobRequest
        {
            TrainJsonlPath = "synthetic.jsonl",
            BaseModel = "Qwen/Qwen2.5-7B-Instruct",
            Method = TrainingMethod.Sft
        };

        baseReq.MultiGpuStrategy = TrainingMultiGpuStrategy.Single;
        baseReq.GpuDeviceIds = new[] { 0, 1 };
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateRequest(baseReq, TrainingMethod.Sft));

        baseReq.MultiGpuStrategy = TrainingMultiGpuStrategy.DataParallel;
        baseReq.GpuDeviceIds = new[] { 0 };
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateRequest(baseReq, TrainingMethod.Sft));

        baseReq.MultiGpuStrategy = TrainingMultiGpuStrategy.DeviceMapAuto;
        baseReq.GpuDeviceIds = new[] { 0, 1 };
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateRequest(baseReq, TrainingMethod.TtsSft));

        baseReq.MultiGpuStrategy = TrainingMultiGpuStrategy.DataParallel;
        baseReq.GpuDeviceIds = new[] { 0, 1 };
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateRequest(baseReq, TrainingMethod.EmbeddingFt));
    }
}
