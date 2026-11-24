using System.Diagnostics;
using System.Runtime.InteropServices;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WindowsCodingSandboxJobLimitsTests
{
    private const ulong Gib = 1024UL * 1024 * 1024;
    private const ulong Mib = 1024UL * 1024;

    [Theory]
    [InlineData(16UL * 1024 * 1024 * 1024, 2UL * 1024 * 1024 * 1024)]   // 25% = 4 GiB -> capped at 2 GiB
    [InlineData(4UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024)]    // 25% = 1 GiB
    [InlineData(512UL * 1024 * 1024, 256UL * 1024 * 1024)]              // 25% = 128 MiB -> floor 256 MiB
    [InlineData(0UL, 256UL * 1024 * 1024)]                              // unknown RAM -> floor
    public void Default_memory_cap_is_a_quarter_of_ram_clamped_between_256_mib_and_2_gib(ulong ram, ulong expected)
    {
        Assert.Equal(expected, WindowsCodingSandbox.ComputeJobLimits(ram).MemoryBytes);
    }

    [Fact]
    public void Environment_overrides_are_honoured_and_invalid_values_are_ignored()
    {
        Assert.Equal(512 * Mib, WindowsCodingSandbox.ComputeJobLimits(16 * Gib, "512").MemoryBytes);
        Assert.Equal(2 * Gib, WindowsCodingSandbox.ComputeJobLimits(16 * Gib, "abc").MemoryBytes);
        Assert.Equal(2 * Gib, WindowsCodingSandbox.ComputeJobLimits(16 * Gib, "0").MemoryBytes);
        Assert.Equal(2 * Gib, WindowsCodingSandbox.ComputeJobLimits(16 * Gib, "-5").MemoryBytes);
        Assert.Equal(256 * Mib, WindowsCodingSandbox.ComputeJobLimits(16 * Gib, "1").MemoryBytes);
        Assert.Equal(64u, WindowsCodingSandbox.ComputeJobLimits(16 * Gib, null, "64").ActiveProcessLimit);
        Assert.Equal(WindowsCodingSandbox.DefaultActiveProcessLimit, WindowsCodingSandbox.ComputeJobLimits(16 * Gib, null, "0").ActiveProcessLimit);
    }

    [Fact]
    public void Limits_set_kill_on_close_job_memory_process_memory_and_active_process_flags()
    {
        WindowsCodingSandbox.JobLimits limits = WindowsCodingSandbox.ComputeJobLimits(16 * Gib);
        const uint activeProcess = 0x8, processMemory = 0x100, jobMemory = 0x200, killOnClose = 0x2000;
        Assert.Equal(activeProcess | processMemory | jobMemory | killOnClose, limits.LimitFlags);
        Assert.True(limits.ActiveProcessLimit is > 0 and <= 4096);
    }

    [Fact]
    public async Task Process_allocating_past_the_job_memory_cap_is_terminated_by_the_job()
    {
        if (!OperatingSystem.IsWindows()) return;

        var limits = WindowsCodingSandbox.ComputeJobLimits(16 * Gib, "300");
        IntPtr job = WindowsCodingSandbox.CreateKillOnCloseJob(limits);
        Assert.NotEqual(IntPtr.Zero, job);
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"[void][Console]::In.ReadLine(); $p = [Runtime.InteropServices.Marshal]::AllocHGlobal(1500000000); [Runtime.InteropServices.Marshal]::WriteByte($p, 1500000000 - 1, 1); [Console]::Out.WriteLine('ALLOCATED')\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        try
        {
            Assert.True(AssignProcessToJobObject(job, process.Handle), "Win32 error " + Marshal.GetLastWin32Error());
            await process.StandardInput.WriteLineAsync("go");
            process.StandardInput.Close();
            string stdout = await process.StandardOutput.ReadToEndAsync();
            using CancellationTokenSource exit = new(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(exit.Token);

            Assert.DoesNotContain("ALLOCATED", stdout, StringComparison.Ordinal);
            Assert.NotEqual(0, process.ExitCode);
        }
        finally
        {
            CloseHandle(job);   // KillOnJobClose also reaps anything left behind
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
