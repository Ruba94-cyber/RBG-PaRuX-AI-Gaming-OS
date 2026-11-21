using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Picks the llama.cpp Vulkan device for the text runtime. Vulkan enumerates every adapter, and the
/// integrated GPU is often <c>Vulkan0</c> (measured: Radeon 780M as Vulkan0, RTX 5080 as Vulkan1,
/// about 30 vs about 190 tokens/s on the same model), so a hard-coded index silently runs on the
/// slow device. The choice prefers a discrete adapter over an integrated one, then the most dedicated
/// video memory, and only considers devices that fit the model and pass the health probe. An explicit
/// user device always wins. Without a usable listing the previous behaviour (Vulkan0) is kept, with the
/// reason surfaced.
/// </summary>
internal static partial class VulkanDeviceSelector
{
    internal const string DefaultDevice = "Vulkan0";
    internal const string UserDeviceEnvironmentVariable = "PARUX_LLAMA_DEVICE";
    private const long IntegratedDedicatedCeilingBytes = 2L * 1024 * 1024 * 1024;

    /// <param name="FreeMib">Free memory the driver reported, or -1 when the listing did not carry it.</param>
    internal readonly record struct VulkanDevice(int Index, string Name, long TotalMib, long FreeMib = -1)
    {
        internal string Id => "Vulkan" + Index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The outcome of a device choice. <see cref="Device"/> is null only when <see cref="CpuFallback"/> is true
    /// (every candidate failed its health probe), in which case the launch must use CPU arguments.</summary>
    internal sealed record DeviceChoice(string? Device, string Reason, bool UsedFallback = false, bool CpuFallback = false);

    [GeneratedRegex(@"^\s*Vulkan(?<i>\d+):\s*(?<name>.+?)\s*\((?<total>\d+)\s*MiB(?:,\s*(?<free>\d+)\s*MiB free)?", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();

    /// <summary>Parses <c>llama-server --list-devices</c> output; non-Vulkan and malformed lines are ignored.</summary>
    internal static List<VulkanDevice> ParseDevices(string? output)
    {
        var devices = new List<VulkanDevice>();
        if (string.IsNullOrWhiteSpace(output)) return devices;
        foreach (string line in output.Split('\n'))
        {
            Match m = DeviceLine().Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups["i"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) continue;
            long.TryParse(m.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long total);
            long free = m.Groups["free"].Success
                && long.TryParse(m.Groups["free"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long f) ? f : -1;
            devices.Add(new VulkanDevice(index, m.Groups["name"].Value.Trim(), total, free));
        }
        return devices;
    }

    /// <summary>Compatibility wrapper: the chosen device id, with no memory requirement and no override.</summary>
    internal static string Choose(IReadOnlyList<VulkanDevice> devices, GpuInventoryStatus? inventory) =>
        Select(devices, inventory).Device ?? DefaultDevice;

    private sealed record Ranked(VulkanDevice Device, bool Integrated, long DedicatedMib);

    /// <summary>
    /// Chooses the llama.cpp device. Precedence: an explicit user device; then, among devices that pass the health probe and
    /// (when a requirement is known) have enough free memory for the model and KV cache: discrete before integrated, then the
    /// most dedicated memory, then the most free memory. A single device is used as is, with no probe. An unusable device list
    /// keeps the previous default. When every candidate fails its probe the result is a CPU fallback, never a failed device.
    /// </summary>
    /// <param name="requiredMib">Model plus KV estimate in MiB; 0 when unknown (fit is then not considered).</param>
    /// <param name="userDevice">An explicit user choice such as "Vulkan1"; always honoured.</param>
    /// <param name="isHealthy">Null means every device is considered healthy.</param>
    internal static DeviceChoice Select(IReadOnlyList<VulkanDevice> devices, GpuInventoryStatus? inventory,
        long requiredMib = 0, string? userDevice = null, Func<VulkanDevice, bool>? isHealthy = null)
    {
        if (!string.IsNullOrWhiteSpace(userDevice))
            return new DeviceChoice(userDevice.Trim(), "Device set explicitly by the user (" + userDevice.Trim() + ").");
        if (devices.Count == 0)
            return new DeviceChoice(DefaultDevice, "llama.cpp device list unavailable; kept " + DefaultDevice + ".", UsedFallback: true);
        if (devices.Count == 1)
            return new DeviceChoice(devices[0].Id, "Single GPU device (" + devices[0].Name + ").");
        if (inventory is not { Available: true })
            return new DeviceChoice(DefaultDevice, "GPU inventory unavailable; kept " + DefaultDevice + ".", UsedFallback: true);

        List<Ranked> ranked = Rank(devices, inventory);
        List<Ranked> healthy = isHealthy == null ? ranked : ranked.Where(r => isHealthy(r.Device)).ToList();
        if (healthy.Count == 0)
            return new DeviceChoice(null, "Every GPU device failed its health probe; using the CPU.", UsedFallback: true, CpuFallback: true);

        List<Ranked> pool = healthy;
        bool noneFits = false;
        if (requiredMib > 0)
        {
            List<Ranked> fitting = healthy.Where(r => FreeOrTotal(r.Device) >= requiredMib).ToList();
            if (fitting.Count > 0) pool = fitting; else noneFits = true;
        }
        Ranked best = pool
            .OrderBy(r => r.Integrated ? 1 : 0)
            .ThenByDescending(r => r.DedicatedMib)
            .ThenByDescending(r => FreeOrTotal(r.Device))
            .ThenBy(r => r.Device.Index)
            .First();
        string reason = best.Device.Id + " (" + best.Device.Name + ", " + (best.Integrated ? "integrated" : "discrete") + ") chosen from "
            + string.Join(", ", ranked.Select(r => r.Device.Id + " " + r.Device.Name))
            + (noneFits ? "; no device reports enough free memory for the model, so the best device is used and layers are fitted" : "")
            + (healthy.Count < ranked.Count ? "; devices that failed the health probe were skipped" : "") + ".";
        // Not a fallback when the best device is not Vulkan0 or when more than one device competed: it is a decision, and it is surfaced.
        return new DeviceChoice(best.Device.Id, reason);
    }

    private static long FreeOrTotal(VulkanDevice d) => d.FreeMib >= 0 ? d.FreeMib : d.TotalMib;

    private static List<Ranked> Rank(IReadOnlyList<VulkanDevice> devices, GpuInventoryStatus inventory)
    {
        List<GpuAdapterStatus> adapters = inventory.Adapters.Where(a => !a.IsSoftware && !string.IsNullOrWhiteSpace(a.Name)).ToList();
        var matched = new Dictionary<int, GpuAdapterStatus>();
        var used = new HashSet<GpuAdapterStatus>();
        foreach (VulkanDevice d in devices)
        {
            GpuAdapterStatus? a = adapters.FirstOrDefault(x => !used.Contains(x) && NamesMatch(d.Name, x.Name));
            if (a != null) { matched[d.Index] = a; used.Add(a); }
        }
        // No name matched: the device whose memory is closest to a still-unmatched adapter's dedicated memory (within 15%) is the same card.
        foreach (GpuAdapterStatus a in adapters.Where(x => !used.Contains(x) && x.DedicatedVideoMemoryBytes > 0))
        {
            long wanted = a.DedicatedVideoMemoryBytes / (1024 * 1024);
            VulkanDevice? close = devices
                .Where(d => !matched.ContainsKey(d.Index) && d.TotalMib > 0 && Math.Abs(d.TotalMib - wanted) <= wanted * 0.15)
                .OrderBy(d => Math.Abs(d.TotalMib - wanted)).Cast<VulkanDevice?>().FirstOrDefault();
            if (close is { } c) { matched[c.Index] = a; used.Add(a); }
        }
        return devices.Select(d =>
        {
            matched.TryGetValue(d.Index, out GpuAdapterStatus? a);
            // An unmatched device has no known dedicated memory; it ranks as integrated so a known discrete card always wins.
            bool integrated = a == null || a.DedicatedVideoMemoryBytes < IntegratedDedicatedCeilingBytes;
            return new Ranked(d, integrated, a == null ? 0 : a.DedicatedVideoMemoryBytes / (1024 * 1024));
        }).ToList();
    }

    /// <summary>Windows names carry trademark marks the Vulkan driver omits ("Radeon(TM) RX 7900" vs "Radeon RX 7900"), so both are
    /// compared as plain letters and digits.</summary>
    internal static string Plain(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.Replace("(TM)", "").Replace("(R)", "").Replace("™", "").Replace("®", ""))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static bool NamesMatch(string vulkanName, string adapterName)
    {
        string a = Plain(vulkanName), b = Plain(adapterName);
        return a.Length > 0 && b.Length > 0 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }

    // A successful listing is kept for the life of the process; an empty one (timeout, antivirus scan) only for two minutes.
    private static readonly Dictionary<string, (List<VulkanDevice> Devices, DateTime ExpiresUtc)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> FailedUntilUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();
    internal static Func<string, CancellationToken, Task<string>>? ListDevicesOverrideForTests;

    /// <summary>Records that a launch on this device did not become healthy, so it is skipped for the next ten minutes.</summary>
    internal static void MarkFailed(string? device)
    {
        if (string.IsNullOrWhiteSpace(device)) return;
        lock (CacheLock) FailedUntilUtc[device.Trim()] = DateTime.UtcNow.AddMinutes(10);
    }

    internal static void ResetForTests()
    {
        lock (CacheLock) { Cache.Clear(); FailedUntilUtc.Clear(); }
        ListDevicesOverrideForTests = null;
    }

    private static bool IsMarkedFailed(VulkanDevice d)
    {
        lock (CacheLock) return FailedUntilUtc.TryGetValue(d.Id, out DateTime until) && until > DateTime.UtcNow;
    }

    internal static string? UserDevice() =>
        Environment.GetEnvironmentVariable(UserDeviceEnvironmentVariable) is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    /// <summary>Resolves the device for the installed Vulkan runtime; never throws and never blocks longer than a few seconds.</summary>
    internal static async Task<string> ResolveAsync(GpuInventoryStatus? inventory, CancellationToken ct) =>
        (await ChooseAsync(inventory, 0, null, ct).ConfigureAwait(false)).Device ?? DefaultDevice;

    /// <summary>The full decision with its surfaced reason. A user device (argument, else the environment variable) wins.</summary>
    internal static async Task<DeviceChoice> ChooseAsync(GpuInventoryStatus? inventory, long requiredMib, string? userDevice, CancellationToken ct)
    {
        userDevice = string.IsNullOrWhiteSpace(userDevice) ? UserDevice() : userDevice;
        if (!string.IsNullOrWhiteSpace(userDevice)) return Select([], inventory, requiredMib, userDevice);
        try
        {
            string exe = Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable);
            if (ListDevicesOverrideForTests == null && !File.Exists(exe))
                return new DeviceChoice(DefaultDevice, "Vulkan runtime not installed; kept " + DefaultDevice + ".", UsedFallback: true);
            List<VulkanDevice>? devices = null;
            lock (CacheLock)
                if (Cache.TryGetValue(exe, out var cached) && cached.ExpiresUtc > DateTime.UtcNow) devices = cached.Devices;
            if (devices == null)
            {
                try
                {
                    string output = ListDevicesOverrideForTests != null
                        ? await ListDevicesOverrideForTests(exe, ct).ConfigureAwait(false)
                        : await ListDevicesAsync(exe, ct).ConfigureAwait(false);
                    devices = ParseDevices(output);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { devices = []; }
                catch (Exception) when (!ct.IsCancellationRequested) { devices = []; }
                lock (CacheLock) Cache[exe] = (devices, devices.Count > 0 ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(2));
            }
            return Select(devices, inventory, requiredMib, null, d => !IsMarkedFailed(d));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new DeviceChoice(DefaultDevice, "Device selection failed; kept " + DefaultDevice + ".", UsedFallback: true); }
    }

    /// <summary>Synchronous form for launch builders that have no async context; reads DXGI when no inventory is supplied.</summary>
    internal static string ResolveBlocking(GpuInventoryStatus? inventory = null)
    {
        try
        {
            return Task.Run(async () =>
            {
                GpuInventoryStatus inv = inventory ?? new GpuInventoryService().GetStatus();
                return (await ChooseAsync(inv, 0, null, CancellationToken.None).ConfigureAwait(false)).Device ?? DefaultDevice;
            }).GetAwaiter().GetResult();
        }
        catch { return DefaultDevice; }
    }

    private static async Task<string> ListDevicesAsync(string exe, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, "--list-devices")
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }
}
