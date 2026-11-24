namespace PaRuX.Contracts;

/// <summary>
/// What the Desktop tells the user when it cannot start. The normal log lives in the data folder,
/// which is exactly what is unusable when the folder is read-only, so the fallback log goes to the
/// temp folder and a visible message states the reason.
/// </summary>
public static class StartupFailureReport
{
    public const string FallbackLogFileName = "PaRuX-startup-error.log";

    public static string FallbackLogPath(string tempDirectory) => Path.Combine(tempDirectory, FallbackLogFileName);

    /// <summary>One user-readable text: the reason and the folder that could not be used.</summary>
    public static string BuildMessage(string? dataRoot, Exception exception)
    {
        string where = string.IsNullOrWhiteSpace(dataRoot) ? "the PaRuX data folder" : "\"" + dataRoot + "\"";
        return "PaRuX cannot start because " + where + " cannot be used.\n\n"
            + exception.Message
            + "\n\nMove PaRuX to a folder you can write to (or fix the folder permissions) and start it again.";
    }

    /// <summary>Appends the failure to the fallback log. Never throws; false when it could not be written.</summary>
    public static bool TryWriteFallbackLog(string tempDirectory, string message, Exception? exception)
    {
        try
        {
            File.AppendAllText(FallbackLogPath(tempDirectory),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  FATAL STARTUP: " + message
                + (exception == null ? "" : "\n" + exception) + Environment.NewLine);
            return true;
        }
        catch (Exception) { return false; }
    }
}
