using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Backend decision for Corporate Lite Offline: the bundled, hash-pinned llama.cpp Vulkan engine (GPU offload) when
/// it is present AND a usable, non-software adapter exists AND IT has not switched it off; otherwise the validated
/// bundled CPU engine, which stays the safe default. Everything here is pure (no process, no network, no install):
/// the caller launches, and on any Vulkan failure falls back to CPU and records why.
///
/// Memory stays fail-closed. A GPU offload never admits a model the CPU plan would refuse: the Vulkan plan uses the
/// same profile RAM ceiling (<see cref="CorporateCpuMemoryPlanner"/>) and adds the driver/compute overhead, because an
/// integrated GPU's "video memory" is ordinary system RAM. A model larger than what the adapter can address
/// (dedicated plus shared) is never offloaded.
/// </summary>
internal static class CorporateGpuBackendPolicy
{
    /// <summary>IT switch. "0", "false", "off", "no", "cpu" or "disabled" force CPU-only; anything else (or unset) allows Vulkan.</summary>
    internal const string DisableEnvironmentVariable = "PARUX_CORPORATE_GPU";

    /// <summary>How long a model that failed on Vulkan stays on CPU before Vulkan is tried again.</summary>
    internal static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(10);

    /// <summary>The Vulkan attempt must become healthy within this time or the launch falls back to CPU.</summary>
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(150);

    /// <summary>An adapter with less dedicated video memory than this is an integrated GPU sharing system RAM.</summary>
    internal const long IntegratedDedicatedCeilingBytes = 2L * 1024 * 1024 * 1024;
    internal const double IntegratedOverheadMib = 768;
    internal const double DiscreteOverheadMib = 384;

    internal enum Backend { Cpu, Vulkan }

    internal sealed record Decision(Backend Backend, string Reason, GpuAdapterStatus? Adapter = null)
    {
        internal bool UseVulkan => Backend == Backend.Vulkan;
    }

    /// <summary>
    /// "force", "integrated" or "always" also lets an INTEGRATED (shared-memory) GPU run the model. The default is the CPU
    /// there: on the Intel UHD reference PC the Vulkan offload measured 3.6 tok/s against 6.5 tok/s on the CPU engine
    /// (first reply 107 s versus 22 s). A discrete GPU keeps Vulkan by default.
    /// </summary>
    internal static bool AllowsIntegratedByEnvironment(string? value)
    {
        string v = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return v is "force" or "integrated" or "always";
    }

    internal static bool IsDisabledByEnvironment(string? value)
    {
        string v = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return v is "0" or "false" or "off" or "no" or "cpu" or "disabled";
    }

    internal static bool IsIntegrated(GpuAdapterStatus adapter) =>
        adapter.DedicatedVideoMemoryBytes < IntegratedDedicatedCeilingBytes;

    /// <summary>The best non-software adapter: discrete before integrated, then the most dedicated, then the most shared memory.</summary>
    internal static GpuAdapterStatus? PickAdapter(GpuInventoryStatus? inventory)
    {
        if (inventory is not { Available: true }) return null;
        return inventory.Adapters
            .Where(a => !a.IsSoftware && !string.IsNullOrWhiteSpace(a.Name) && a.VendorId != 0x1414 /* Microsoft Basic Render */)
            .OrderBy(a => IsIntegrated(a) ? 1 : 0)
            .ThenByDescending(a => a.DedicatedVideoMemoryBytes)
            .ThenByDescending(a => a.SharedSystemMemoryBytes)
            .FirstOrDefault();
    }

    /// <summary>The pure backend decision. A Corporate-only edition rule: the standard editions never reach this code.</summary>
    internal static Decision Decide(bool corporate, string? disableEnvironmentValue, bool vulkanRuntimePresent,
        GpuInventoryStatus? inventory, bool recentlyFailed)
    {
        if (!corporate) return new Decision(Backend.Cpu, "Not a Corporate launch.");
        if (IsDisabledByEnvironment(disableEnvironmentValue))
            return new Decision(Backend.Cpu, DisableEnvironmentVariable + " disables GPU use; running on the CPU (IT policy).");
        if (!vulkanRuntimePresent)
            return new Decision(Backend.Cpu, "The bundled Vulkan runtime is not present; running on the CPU.");
        GpuAdapterStatus? adapter = PickAdapter(inventory);
        if (adapter is null)
            return new Decision(Backend.Cpu, "No usable GPU adapter was detected; running on the CPU.");
        if (recentlyFailed)
            return new Decision(Backend.Cpu, "Vulkan failed for this model a few minutes ago; running on the CPU until it is retried.");
        if (IsIntegrated(adapter) && !AllowsIntegratedByEnvironment(disableEnvironmentValue))
            return new Decision(Backend.Cpu, "Only an integrated GPU (" + adapter.Name + ", shared memory) is present; it measured slower than the CPU engine, so the CPU is used. Set "
                + DisableEnvironmentVariable + "=force to try the GPU anyway.", adapter);
        return new Decision(Backend.Vulkan, "Vulkan adapter " + adapter.Name + (IsIntegrated(adapter) ? " (integrated, shared memory)" : " (discrete)") + ".", adapter);
    }

    /// <summary>Driver, command-buffer and compute-buffer overhead of a Vulkan offload on top of the CPU estimate.</summary>
    internal static double OverheadMib(GpuAdapterStatus adapter) =>
        IsIntegrated(adapter) ? IntegratedOverheadMib : DiscreteOverheadMib;

    /// <summary>True when the adapter can address the offloaded model (dedicated plus shared memory).</summary>
    internal static bool AdapterCanHold(GpuAdapterStatus adapter, double neededMib)
    {
        double capacityMib = (Math.Max(0, adapter.DedicatedVideoMemoryBytes) + Math.Max(0, adapter.SharedSystemMemoryBytes)) / 1048576d;
        return capacityMib > 0 && neededMib <= capacityMib;
    }

    /// <summary>
    /// RAM admission for the Vulkan launch. Same ceiling, same snapshot, same context ladder as the CPU plan, with the
    /// adapter overhead added to every candidate. Whatever lands in an iGPU's shared memory is counted against the same
    /// RAM budget, never on top of it. The result may use a smaller context than the CPU plan, or not fit at all, in
    /// which case the caller runs the (already admitted) CPU plan.
    /// </summary>
    internal static CorporateCpuMemoryPlanner.Plan PlanVulkan(SystemRamSnapshot? ram, double maxSystemRamPercent,
        long reclaimableOwnedTextBytes, Func<int, double> cpuEstimateMib, GpuAdapterStatus adapter)
    {
        double overhead = OverheadMib(adapter);
        CorporateCpuMemoryPlanner.Plan plan = CorporateCpuMemoryPlanner.Select(ram, maxSystemRamPercent,
            reclaimableOwnedTextBytes, ctx => cpuEstimateMib(ctx) + overhead);
        if (plan.Fits && !AdapterCanHold(adapter, plan.NeededMib))
            return plan with { Fits = false };
        return plan;
    }

    // ---- bounded memory of a failed Vulkan launch (per model) ----
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> FailedUntilUtc = new(StringComparer.OrdinalIgnoreCase);

    internal static void MarkFailed(string modelPath, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return;
        lock (Gate) FailedUntilUtc[modelPath] = (nowUtc ?? DateTime.UtcNow) + FailureMemory;
    }

    internal static bool RecentlyFailed(string modelPath, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return false;
        lock (Gate) return FailedUntilUtc.TryGetValue(modelPath, out DateTime until) && until > (nowUtc ?? DateTime.UtcNow);
    }

    internal static void ResetForTests() { lock (Gate) FailedUntilUtc.Clear(); }
}
