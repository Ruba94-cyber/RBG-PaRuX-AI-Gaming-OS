using System.ComponentModel;
using System.Diagnostics;

namespace PaRuX.Launcher;

/// <summary>Where the launcher starts the real application from, and with what.</summary>
internal sealed record LaunchPlan(string AppExecutable, string WorkingDirectory, IReadOnlyList<string> Arguments);

/// <summary>
/// The whole launcher behaviour, kept apart from <c>Main</c> so it can be exercised with a stand-in
/// executable. The launcher sits at the package root; the application lives in <c>app\</c>.
/// </summary>
internal static class LauncherCore
{
    internal const string AppFolderName = "app";
    internal const string AppExecutableName = "PaRuX.exe";

    /// <summary>The application executable is missing (damaged or partially extracted package).</summary>
    internal const int ExitAppMissing = 3;
    /// <summary>Windows refused to start the application (blocked by policy, antivirus, access denied...).</summary>
    internal const int ExitStartFailed = 4;

    internal static LaunchPlan Plan(string launcherDirectory, IReadOnlyList<string> arguments)
    {
        string appDirectory = Path.Combine(Path.GetFullPath(launcherDirectory), AppFolderName);
        return new LaunchPlan(Path.Combine(appDirectory, AppExecutableName), appDirectory, arguments);
    }

    internal static string MissingAppMessage(string executable) =>
        "PaRuX cannot start because its application files are missing:\r\n\r\n" + executable +
        "\r\n\r\nExtract the complete ZIP again (keep the \"app\" folder next to PaRuX.exe) and make sure your " +
        "antivirus did not remove files.";

    /// <summary>Starts the application, waits for it and returns its exit code. When the child cannot be
    /// started a message is reported and a non-zero code is returned. <paramref name="useJob"/> ties the
    /// child's life to this process (kill-on-close Job Object).</summary>
    internal static int Run(string launcherDirectory, IReadOnlyList<string> arguments, Action<string> reportError, bool useJob = true)
    {
        LaunchPlan plan = Plan(launcherDirectory, arguments);
        if (!File.Exists(plan.AppExecutable))
        {
            reportError(MissingAppMessage(plan.AppExecutable));
            return ExitAppMissing;
        }

        var info = new ProcessStartInfo(plan.AppExecutable)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (string argument in plan.Arguments) info.ArgumentList.Add(argument);

        using var job = useJob ? KillOnCloseJob.TryCreate() : null;
        try
        {
            using Process? child = Process.Start(info);
            if (child == null)
            {
                reportError("Windows could not start PaRuX (" + plan.AppExecutable + ").");
                return ExitStartFailed;
            }
            // A failed assignment (the launcher itself runs inside a restrictive job) must not stop the app.
            job?.TryAssign(child);
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            reportError("Windows could not start PaRuX (" + plan.AppExecutable + "): " + ex.Message);
            return ExitStartFailed;
        }
    }
}
