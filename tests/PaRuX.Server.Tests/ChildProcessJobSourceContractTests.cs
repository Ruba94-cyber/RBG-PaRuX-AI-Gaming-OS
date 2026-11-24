using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source contract: the desktop app's job-object wrapper (PaRuX.App is not referenced by this project, so it is read as
/// text) must report whether a child is bound to the kill-on-close job, as the Server's own wrapper does.
/// </summary>
public sealed class ChildProcessJobSourceContractTests
{
    [Fact]
    public void App_child_process_job_reports_and_traces_the_assignment_outcome()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PaRuX.App", "Services", "ChildProcessJob.cs"));

        Assert.Contains("public static bool Assign(Process process)", source, StringComparison.Ordinal);
        Assert.Contains("Trace.TraceWarning", source, StringComparison.Ordinal);
        // The old shape swallowed every failure: an unavailable job and a failed assignment both returned silently.
        Assert.DoesNotContain("catch { }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("try { AssignProcessToJobObject(Handle, process.Handle); }", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}
