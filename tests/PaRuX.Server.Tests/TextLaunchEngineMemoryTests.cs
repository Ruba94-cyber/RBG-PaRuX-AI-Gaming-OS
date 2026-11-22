using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TextLaunchEngineMemoryTests
{
    private const string Cuda = @"C:\rt\llama.cpp-b1-cuda13.4";
    private const string Model = @"C:\models\a.gguf";

    private static IReadOnlyList<TextLaunchLadder.Rung> Ladder() =>
        TextLaunchLadder.Plan(TextLaunchLadder.Engine.Cuda, cpuOnly: false,
            ["--flash-attn", "on", "--gpu-layers", "all"], vulkanAvailable: true);

    [Fact]
    public void NothingRemembered_RunsTheWholeLadder()
    {
        var memory = new TextLaunchEngineMemory();
        var planned = Ladder();
        Assert.Same(planned, TextLaunchEngineMemory.StartFrom(planned, memory.Recall(Cuda, Model)));
    }

    [Fact]
    public void RememberedVulkan_SkipsTheFailingCudaRungs()
    {
        var memory = new TextLaunchEngineMemory();
        var planned = Ladder();
        var vulkan = planned.First(r => r.Engine == TextLaunchLadder.Engine.Vulkan);
        memory.Record(Cuda, Model, vulkan, wasFirstPlannedRung: false);

        var run = TextLaunchEngineMemory.StartFrom(planned, memory.Recall(Cuda, Model));

        Assert.Equal(TextLaunchLadder.Engine.Vulkan, run[0].Engine);
        Assert.Equal(TextLaunchLadder.Engine.Cpu, run[^1].Engine);
        Assert.True(run.Count < planned.Count);
    }

    [Fact]
    public void MemoryIsPerModelAndPerPrimaryRuntime()
    {
        var memory = new TextLaunchEngineMemory();
        var vulkan = Ladder().First(r => r.Engine == TextLaunchLadder.Engine.Vulkan);
        memory.Record(Cuda, Model, vulkan, false);
        Assert.Null(memory.Recall(Cuda, @"C:\models\other.gguf"));
        Assert.Null(memory.Recall(@"C:\rt\llama.cpp-b2-cuda13.4", Model));
        Assert.NotNull(memory.Recall(Cuda, Model));
    }

    [Fact]
    public void MemoryExpires_SoAFixedDriverIsRetried()
    {
        DateTime now = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var memory = new TextLaunchEngineMemory(() => now);
        var vulkan = Ladder().First(r => r.Engine == TextLaunchLadder.Engine.Vulkan);
        memory.Record(Cuda, Model, vulkan, false);

        now += TextLaunchEngineMemory.Lifetime - TimeSpan.FromSeconds(1);
        Assert.NotNull(memory.Recall(Cuda, Model));
        now += TimeSpan.FromSeconds(2);
        Assert.Null(memory.Recall(Cuda, Model));
    }

    [Fact]
    public void PrimarySuccess_ClearsTheMemory()
    {
        var memory = new TextLaunchEngineMemory();
        var planned = Ladder();
        memory.Record(Cuda, Model, planned.First(r => r.Engine == TextLaunchLadder.Engine.Vulkan), false);
        memory.Record(Cuda, Model, planned[0], wasFirstPlannedRung: true);
        Assert.Null(memory.Recall(Cuda, Model));
    }

    [Fact]
    public void Forget_RemovesTheEntry()
    {
        var memory = new TextLaunchEngineMemory();
        memory.Record(Cuda, Model, Ladder().First(r => r.Engine == TextLaunchLadder.Engine.Cpu), false);
        memory.Forget(Cuda, Model);
        Assert.Null(memory.Recall(Cuda, Model));
    }

    [Fact]
    public void RememberedRungMissingFromThePlan_FallsBackToTheWholeLadder()
    {
        var memory = new TextLaunchEngineMemory();
        // Vulkan was remembered, but this launch has no Vulkan build installed.
        var noVulkan = TextLaunchLadder.Plan(TextLaunchLadder.Engine.Cuda, false, ["--flash-attn", "on", "--gpu-layers", "all"], vulkanAvailable: false);
        var vulkan = Ladder().First(r => r.Engine == TextLaunchLadder.Engine.Vulkan);
        memory.Record(Cuda, Model, vulkan, false);
        Assert.Same(noVulkan, TextLaunchEngineMemory.StartFrom(noVulkan, memory.Recall(Cuda, Model)));
    }
}
