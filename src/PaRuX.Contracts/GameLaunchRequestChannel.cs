using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PaRuX.Contracts;

/// <summary>
/// "PaRuX.exe --play &lt;titleKey&gt;": how a desktop or Steam shortcut asks PaRuX to start one library game.
///
/// The request carries only the title key (letters and digits, or "id:" plus an installation id). It never carries a
/// path, a command or an argument, so a planted request can do nothing more than ask PaRuX to show its normal
/// "Start this game?" confirmation for a game already in the user's library. PaRuX never starts a game silently
/// because a shortcut was clicked.
///
/// A running instance receives the request through a small queue folder in the data root plus a session-local event
/// (the same pattern as <see cref="AppExitSignal"/>); a fresh instance handles the argument itself.
/// </summary>
public static class GameLaunchRequestChannel
{
    public const string PlayArgument = "--play";
    public const int MaxTitleKeyLength = 300;
    public const int MaxQueuedRequests = 16;
    private const string Extension = ".play";

    /// <summary>A request older than this is ignored, so a leftover from a crashed run never starts a game later.</summary>
    public static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(2);

    /// <summary>Same rule as the Core's title key check: letters and digits, or "id:" plus an installation id.</summary>
    public static bool IsValidTitleKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= MaxTitleKeyLength &&
        (key.StartsWith("id:", StringComparison.Ordinal)
            ? key.Length > 3 && key[3..].All(c => char.IsLetterOrDigit(c) || c is ':' or '_' or '-' or '.')
            : key.All(char.IsLetterOrDigit));

    /// <summary>The command-line arguments (without the program name) a shortcut passes for one title.</summary>
    public static string ArgumentsFor(string titleKey)
    {
        if (!IsValidTitleKey(titleKey)) throw new ArgumentException("Not a valid title key.", nameof(titleKey));
        return PlayArgument + " " + titleKey;
    }

    /// <summary>Reads <c>--play &lt;key&gt;</c> from a command line whose first element is the executable.</summary>
    public static bool TryParseCommandLine(IReadOnlyList<string>? commandLine, out string titleKey)
    {
        titleKey = string.Empty;
        if (commandLine == null) return false;
        for (int i = 1; i < commandLine.Count - 1; i++)
        {
            if (!string.Equals(commandLine[i], PlayArgument, StringComparison.OrdinalIgnoreCase)) continue;
            string candidate = commandLine[i + 1].Trim();
            if (!IsValidTitleKey(candidate)) return false;
            titleKey = candidate;
            return true;
        }
        return false;
    }

    public static string QueueDirectoryFor(string dataRoot) => Path.Combine(dataRoot, "launch-requests");

    /// <summary>Session-local event name derived from the data root, so a portable copy and an installed PaRuX never mix.</summary>
    public static string EventNameFor(string dataRoot)
    {
        string normalized = Path.GetFullPath(dataRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return "Local\\PaRuX.App.Play." + scope;
    }

    /// <summary>Queues one request in the data root. Returns false when the title key is invalid or the queue is full.</summary>
    public static bool TryEnqueue(string dataRoot, string titleKey)
    {
        if (!IsValidTitleKey(titleKey)) return false;
        try
        {
            string directory = QueueDirectoryFor(dataRoot);
            Directory.CreateDirectory(directory);
            if (Directory.EnumerateFiles(directory, "*" + Extension).Take(MaxQueuedRequests).Count() >= MaxQueuedRequests) return false;
            string finalPath = Path.Combine(directory, DateTime.UtcNow.Ticks.ToString("D19") + "-" + Guid.NewGuid().ToString("N") + Extension);
            string temp = finalPath + ".tmp";
            File.WriteAllText(temp, titleKey, Encoding.UTF8);
            File.Move(temp, finalPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Queues a request and wakes a running instance. False when it could not be queued.</summary>
    [SupportedOSPlatform("windows")]
    public static bool TrySubmit(string dataRoot, string titleKey, out bool runningInstanceNotified)
    {
        runningInstanceNotified = false;
        if (!TryEnqueue(dataRoot, titleKey)) return false;
        runningInstanceNotified = AppExitSignal.TrySignal(EventNameFor(dataRoot));
        return true;
    }

    /// <summary>Starts listening for wake-ups; <paramref name="onRequest"/> runs on a pool thread. Dispose to stop.</summary>
    [SupportedOSPlatform("windows")]
    public static IDisposable Listen(string dataRoot, Action onRequest) =>
        RepeatingListener.Start(EventNameFor(dataRoot), onRequest);

    /// <summary>Same wake-up listener for another named request channel (the Playnite launcher uses its own event name).</summary>
    [SupportedOSPlatform("windows")]
    internal static IDisposable ListenOn(string eventName, Action onRequest) => RepeatingListener.Start(eventName, onRequest);

    /// <summary>
    /// Takes the queued requests (oldest first), deleting each file. Invalid, oversized, stale and duplicate requests
    /// are dropped. At most <see cref="MaxQueuedRequests"/> files are looked at.
    /// </summary>
    public static IReadOnlyList<string> Drain(string dataRoot, DateTime? nowUtc = null)
    {
        var keys = new List<string>();
        string directory = QueueDirectoryFor(dataRoot);
        if (!Directory.Exists(directory)) return keys;
        DateTime now = nowUtc ?? DateTime.UtcNow;
        string[] files;
        try { files = Directory.EnumerateFiles(directory, "*" + Extension).OrderBy(f => f, StringComparer.Ordinal).Take(MaxQueuedRequests).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return keys; }

        foreach (string file in files)
        {
            try
            {
                var info = new FileInfo(file);
                bool fresh = now - info.LastWriteTimeUtc <= MaxRequestAge;
                string? key = null;
                if (fresh && info.Length <= MaxTitleKeyLength * 4)
                    key = File.ReadAllText(file, Encoding.UTF8).Trim();
                File.Delete(file);
                if (key != null && IsValidTitleKey(key) && !keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return keys;
    }

    /// <summary>A wait handle that fires every time it is set (the exit signal fires once).</summary>
    [SupportedOSPlatform("windows")]
    private sealed class RepeatingListener : IDisposable
    {
        private readonly EventWaitHandle _handle;
        private readonly RegisteredWaitHandle _registration;

        private RepeatingListener(EventWaitHandle handle, RegisteredWaitHandle registration)
        {
            _handle = handle;
            _registration = registration;
        }

        public static RepeatingListener Start(string name, Action onSignal)
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
                handle, (_, timedOut) => { if (!timedOut) onSignal(); }, null, Timeout.Infinite, executeOnlyOnce: false);
            return new RepeatingListener(handle, registration);
        }

        public void Dispose()
        {
            _registration.Unregister(null);
            _handle.Dispose();
        }
    }
}
