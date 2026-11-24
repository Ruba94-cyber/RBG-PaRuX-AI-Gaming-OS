using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CoreSingleInstanceTests
{
    [Fact]
    public void Core_mutex_name_is_stable_per_data_root_and_differs_from_the_app_name()
    {
        string a = SingleInstanceGuard.CoreMutexNameFor(@"C:\Tools\PaRuX\PaRuXData");
        Assert.Equal(a, SingleInstanceGuard.CoreMutexNameFor(@"c:\tools\parux\paruxdata\"));
        Assert.NotEqual(a, SingleInstanceGuard.CoreMutexNameFor(@"E:\PaRuX\PaRuXData"));
        Assert.NotEqual(a, SingleInstanceGuard.AppMutexNameFor(@"C:\Tools\PaRuX\PaRuXData"));
        Assert.StartsWith(@"Local\PaRuX.Core.SingleInstance.", a, StringComparison.Ordinal);
        Assert.DoesNotContain("Tools", a, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Second_acquire_of_the_same_name_is_refused_until_the_first_is_released()
    {
        if (!OperatingSystem.IsWindows()) return;
        string name = SingleInstanceGuard.CoreMutexNameFor(
            Path.Combine(Path.GetTempPath(), "parux-core-" + Guid.NewGuid().ToString("N")));
        // Mutex ownership is per thread, but the "already exists" answer is per name, so a
        // second acquire from another thread must be refused while the first handle lives.
        IDisposable? first = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(first);
        IDisposable? second = null;
        var t = new Thread(() => second = SingleInstanceGuard.TryAcquire(name));
        t.Start();
        t.Join();
        Assert.Null(second);
        first!.Dispose();

        IDisposable? third = null;
        var t2 = new Thread(() => third = SingleInstanceGuard.TryAcquire(name));
        t2.Start();
        t2.Join();
        Assert.NotNull(third);
        third!.Dispose();
    }

    [Fact]
    public void Core_program_takes_the_guard_before_configuring_kestrel_and_reports_through_the_start_failure_channel()
    {
        string program = Read("src", "PaRuX.Server", "Program.cs");
        int guard = program.IndexOf("SingleInstanceGuard.TryAcquire(SingleInstanceGuard.CoreMutexNameFor(", StringComparison.Ordinal);
        int kestrel = program.IndexOf("ConfigureKestrel", StringComparison.Ordinal);
        Assert.True(guard > 0 && kestrel > guard, "The Core single-instance guard must run before Kestrel is configured.");
        Assert.Contains("ServerState.TryWriteStartFailure(message)", program, StringComparison.Ordinal);
    }

    private static string Read(params string[] path)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(path).ToArray()));
    }
}
