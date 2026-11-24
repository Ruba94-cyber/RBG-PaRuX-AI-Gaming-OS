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

    [Fact]
    public void App_mutex_name_is_scoped_to_the_data_root_so_editions_do_not_block_each_other()
    {
        string corporate = SingleInstanceGuard.AppMutexNameFor(Path.Combine(Path.GetTempPath(), "Corp", "PaRuXData"));
        string installed = SingleInstanceGuard.AppMutexNameFor(Path.Combine(Path.GetTempPath(), "Installed", "PaRuX"));
        Assert.NotEqual(corporate, installed);
        Assert.Equal(corporate, SingleInstanceGuard.AppMutexNameFor(
            Path.Combine(Path.GetTempPath(), "corp", "paruxdata") + Path.DirectorySeparatorChar));
        Assert.StartsWith("Local", corporate, StringComparison.Ordinal);
        Assert.Contains("PaRuX.App.SingleInstance.", corporate, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_no_longer_uses_a_fixed_single_instance_mutex_name_in_release()
    {
        string app = Read("src", "PaRuX.App", "App.xaml.cs");
        Assert.DoesNotContain("SingleInstanceMutexName", app, StringComparison.Ordinal);
        int name = app.IndexOf("string mutexName = PaRuX.Contracts.SingleInstanceGuard.AppMutexNameFor(AppPaths.PaRuXRoot);", StringComparison.Ordinal);
        int debug = app.IndexOf("#if DEBUG", StringComparison.Ordinal);
        Assert.True(name > 0 && (debug < 0 || name < debug), "The data-root scoped name must be set outside the DEBUG-only block.");
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
