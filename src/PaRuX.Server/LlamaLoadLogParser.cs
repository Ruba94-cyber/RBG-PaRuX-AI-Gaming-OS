using System.Globalization;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Reads what llama-server itself logged when it loaded a model: how many layers it really offloaded and how much device
/// memory it committed. This is the observed truth; the <c>-ngl</c>/<c>--gpu-layers</c> value on the command line (and
/// llama-bench's <c>n_gpu_layers</c>) is only what was requested, and a request larger than the model's layer count
/// ("99") says nothing about the real split.
/// </summary>
internal static class LlamaLoadLogParser
{
    // "load_tensors: offloaded 37/37 layers to GPU"  /  "llm_load_tensors: offloaded 33/33 layers to GPU"
    private static readonly Regex Offloaded = new(@"offloaded\s+(\d+)\s*/\s*(\d+)\s+layers\s+to\s+(?:GPU|device)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "load_tensors:        CUDA0 model buffer size =  2375.55 MiB", "llama_kv_cache: Vulkan0 KV buffer size = 576.00 MiB",
    // "llama_context:      CUDA0 compute buffer size =   300.75 MiB", "CUDA_Host compute buffer size"
    private static readonly Regex Buffer = new(@"(?<dev>[A-Za-z][A-Za-z0-9_]*)\s+(?<kind>model|KV|RS|compute|output)\s+buffer\s+size\s*=\s*(?<mib>\d+(?:\.\d+)?)\s*MiB", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static RuntimeTextLoadReport Parse(string? logText, string? requestedGpuLayers = null)
    {
        var report = new RuntimeTextLoadReport { RequestedGpuLayers = requestedGpuLayers };
        if (string.IsNullOrWhiteSpace(logText))
        {
            report.Source = "unavailable";
            report.Note = "No llama-server load log is available yet.";
            return report;
        }

        double deviceMib = 0;
        bool anyDeviceBuffer = false;
        foreach (string raw in logText.Split('\n'))
        {
            // Recent builds run a memory-fitting pass first (all buffers 0.00 MiB or provisional) and then the real load; a
            // "using device" line starts each pass, so only the last pass is counted and compute buffers are never doubled.
            if (raw.Contains("using device", StringComparison.OrdinalIgnoreCase))
            {
                deviceMib = 0;
                anyDeviceBuffer = false;
                report.OffloadedGpuLayers = null;
                report.TotalModelLayers = null;
                continue;
            }
            Match offloaded = Offloaded.Match(raw);
            if (offloaded.Success)
            {
                report.OffloadedGpuLayers = int.Parse(offloaded.Groups[1].Value, CultureInfo.InvariantCulture);
                report.TotalModelLayers = int.Parse(offloaded.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            Match buffer = Buffer.Match(raw);
            if (!buffer.Success) continue;
            string device = buffer.Groups["dev"].Value;
            // CPU, CPU_Mapped, CUDA_Host (pinned host memory) and Vulkan_Host are system RAM, not VRAM.
            if (device.StartsWith("CPU", StringComparison.OrdinalIgnoreCase)
                || device.EndsWith("_Host", StringComparison.OrdinalIgnoreCase)
                || device.EndsWith("_Mapped", StringComparison.OrdinalIgnoreCase)) continue;
            deviceMib += double.Parse(buffer.Groups["mib"].Value, CultureInfo.InvariantCulture);
            anyDeviceBuffer = true;
        }

        if (report.OffloadedGpuLayers is null && !anyDeviceBuffer)
        {
            report.Source = "unavailable";
            report.Note = "The llama-server log has no offload or device-buffer lines at its current verbosity (they appear from -lv 4); the real layer split was not observed.";
            return report;
        }
        report.Source = "llama-server load log";
        if (anyDeviceBuffer) report.LoadedGpuBufferMib = (long)Math.Round(deviceMib);
        return report;
    }

    /// <summary>
    /// llama-server only logs "offloaded N/M layers" and the device buffer sizes at log verbosity 4 (the default, 3, has
    /// neither). Appends <c>-lv 4</c> to a PaRuX-managed text launch unless the list already sets a verbosity. Measured on
    /// an RTX 5080: 179.4 tok/s at the default level vs 178.9 tok/s at <c>-lv 4</c>, no startup difference beyond noise.
    /// </summary>
    internal static void EnsureLoadLogVerbosity(List<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (string option in new[] { "-lv", "--log-verbosity", "-v", "--verbose", "--verbosity" })
            if (args.Contains(option, StringComparer.Ordinal)) return;
        args.AddRange(["-lv", "4"]);
    }

    /// <summary>The value of --gpu-layers / -ngl / --n-gpu-layers from a launch argument list, or null when not given.</summary>
    internal static string? RequestedGpuLayers(IReadOnlyList<string> args)
    {
        foreach (string option in new[] { "--gpu-layers", "--n-gpu-layers", "-ngl" })
        {
            string? value = LocalAiProcessManager.ReadArgumentValue(args, option);
            if (value is not null) return value;
        }
        return null;
    }
}
