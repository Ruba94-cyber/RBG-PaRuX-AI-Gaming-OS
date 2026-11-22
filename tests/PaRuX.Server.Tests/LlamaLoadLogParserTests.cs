using System;
using System.Collections.Generic;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LlamaLoadLogParserTests
{
    private const string Log = @"
load_tensors: loading model tensors, this can take a while... (mmap = true)
load_tensors: offloading 36 repeating layers to GPU
load_tensors: offloading output layer to GPU
load_tensors: offloaded 37/37 layers to GPU
load_tensors:   CPU_Mapped model buffer size =   303.75 MiB
load_tensors:        CUDA0 model buffer size =  2375.55 MiB
llama_kv_cache:      CUDA0 KV buffer size =   576.00 MiB
llama_context:      CUDA0 compute buffer size =   300.75 MiB
llama_context:  CUDA_Host compute buffer size =    13.01 MiB
";

    [Fact]
    public void Parses_real_offload_count_and_device_buffers_but_not_host_memory()
    {
        RuntimeTextLoadReport r = LlamaLoadLogParser.Parse(Log, "99");

        Assert.Equal("llama-server load log", r.Source);
        Assert.Equal(37, r.OffloadedGpuLayers);
        Assert.Equal(37, r.TotalModelLayers);
        Assert.Equal(3252, r.LoadedGpuBufferMib); // 2375.55 + 576 + 300.75; CPU_Mapped and CUDA_Host excluded
        Assert.Equal("99", r.RequestedGpuLayers);
    }

    // Shape of a real llama.cpp b12109 log at -lv 4: a fitting pass (zero buffers) then the real load.
    private const string TwoPassLog = @"
0.00.418.669 I llama_prepare_model_devices: using device CUDA0 (NVIDIA GeForce RTX 5080) (0000:01:00.0) - 14987 MiB free
0.00.812.358 I load_tensors: offloaded 37/37 layers to GPU
0.00.812.362 I load_tensors:        CUDA0 model buffer size =     0.00 MiB
0.01.336.094 I sched_reserve:      CUDA0 compute buffer size =    81.08 MiB
0.01.437.881 I llama_prepare_model_devices: using device CUDA0 (NVIDIA GeForce RTX 5080) (0000:01:00.0) - 14987 MiB free
0.02.098.758 I load_tensors: offloaded 37/37 layers to GPU
0.02.098.765 I load_tensors:        CUDA0 model buffer size =  2375.91 MiB
0.03.328.919 I llama_kv_cache:      CUDA0 KV buffer size =   576.00 MiB
0.03.665.868 I sched_reserve:      CUDA0 compute buffer size =    81.08 MiB
0.03.665.870 I sched_reserve:  CUDA_Host compute buffer size =    24.08 MiB
";

    [Fact]
    public void Only_the_final_load_pass_is_counted_so_compute_buffers_are_not_doubled()
    {
        RuntimeTextLoadReport r = LlamaLoadLogParser.Parse(TwoPassLog, "99");

        Assert.Equal(37, r.OffloadedGpuLayers);
        Assert.Equal(3033, r.LoadedGpuBufferMib); // 2375.91 + 576.00 + 81.08
    }

    [Fact]
    public void Partial_offload_is_reported_as_observed_not_as_requested()
    {
        RuntimeTextLoadReport r = LlamaLoadLogParser.Parse("load_tensors: offloaded 20/49 layers to GPU\nload_tensors: Vulkan0 model buffer size = 5000.00 MiB", "all");

        Assert.Equal(20, r.OffloadedGpuLayers);
        Assert.Equal(49, r.TotalModelLayers);
        Assert.Equal("all", r.RequestedGpuLayers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("main: server is listening on http://127.0.0.1:1234\nsrv  init: starting")]
    public void A_log_without_offload_lines_is_unavailable_and_never_guessed(string? log)
    {
        RuntimeTextLoadReport r = LlamaLoadLogParser.Parse(log, "99");

        Assert.Equal("unavailable", r.Source);
        Assert.Null(r.OffloadedGpuLayers);
        Assert.Null(r.LoadedGpuBufferMib);
        Assert.False(string.IsNullOrWhiteSpace(r.Note));
        Assert.Equal("99", r.RequestedGpuLayers); // the request stays visible, labelled as such
    }

    [Theory]
    [InlineData("--gpu-layers", "all")]
    [InlineData("-ngl", "99")]
    [InlineData("--n-gpu-layers", "12")]
    public void Requested_layers_are_read_from_the_launch_arguments(string option, string value)
    {
        Assert.Equal(value, LlamaLoadLogParser.RequestedGpuLayers(new List<string> { "--model", "m.gguf", option, value }));
        Assert.Null(LlamaLoadLogParser.RequestedGpuLayers(new List<string> { "--model", "m.gguf" }));
    }
}
