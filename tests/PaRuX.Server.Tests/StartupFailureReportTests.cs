using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class StartupFailureReportTests
{
    [Fact]
    public void Message_names_the_folder_and_the_reason()
    {
        string folder = Path.Combine("E:", "Corp", "PaRuXData");
        string text = StartupFailureReport.BuildMessage(folder, new UnauthorizedAccessException("Access denied"));
        Assert.Contains(folder, text, StringComparison.Ordinal);
        Assert.Contains("Access denied", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fallback_log_is_written_to_the_given_temp_folder_not_the_data_root()
    {
        string temp = Path.Combine(Path.GetTempPath(), "parux-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assert.True(StartupFailureReport.TryWriteFallbackLog(temp, "boom", new IOException("disk")));
            string log = File.ReadAllText(StartupFailureReport.FallbackLogPath(temp));
            Assert.Contains("boom", log, StringComparison.Ordinal);
            Assert.Contains("disk", log, StringComparison.Ordinal);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public void Fallback_log_never_throws_when_the_folder_is_missing() =>
        Assert.False(StartupFailureReport.TryWriteFallbackLog(
            Path.Combine(Path.GetTempPath(), "parux-missing-" + Guid.NewGuid().ToString("N")), "x", null));

    [Fact]
    public void Desktop_reports_before_exiting_with_a_nonzero_code()
    {
        string app = ReadApp();
        int catchAt = app.IndexOf("FATAL STARTUP", StringComparison.Ordinal);
        int report = app.IndexOf("ReportStartupFailure(ex);", catchAt, StringComparison.Ordinal);
        int exit = app.IndexOf("Environment.Exit(1);", catchAt, StringComparison.Ordinal);
        Assert.True(catchAt > 0 && report > catchAt && exit > report);
        Assert.Contains("StartupFailureReport.TryWriteFallbackLog(Path.GetTempPath()", app, StringComparison.Ordinal);
        Assert.Contains("MessageBoxW", app, StringComparison.Ordinal);
    }

    private static string ReadApp()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "PaRuX.App", "App.xaml.cs"));
    }
}
