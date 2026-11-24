using System.Security.Cryptography;
using System.Text;

namespace PaRuX.Contracts;

/// <summary>
/// Named-mutex single-instance guards scoped to one data root, so a portable copy, a Corporate
/// folder and an installed PaRuX never block each other, while two processes that would share the
/// same SQLite files and pid files cannot both run.
/// </summary>
public static class SingleInstanceGuard
{
    private const string AppPrefix = "Local\\PaRuX.App.SingleInstance.";
    private const string CorePrefix = "Local\\PaRuX.Core.SingleInstance.";

    /// <summary>Mutex name of the Desktop instance that owns <paramref name="dataRoot"/>.</summary>
    public static string AppMutexNameFor(string dataRoot) => AppPrefix + Scope(dataRoot);

    /// <summary>Mutex name of the Core (PaRuX.Server) instance that owns <paramref name="dataRoot"/>.</summary>
    public static string CoreMutexNameFor(string dataRoot) => CorePrefix + Scope(dataRoot);

    private static string Scope(string dataRoot)
    {
        string normalized = Path.GetFullPath(dataRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    /// <summary>Takes ownership of the named mutex for the life of the returned handle, or returns
    /// null when another live process already owns it. Nothing is ever terminated.</summary>
    public static IDisposable? TryAcquire(string name)
    {
        var mutex = new Mutex(true, name, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        return new Handle(mutex);
    }

    private sealed class Handle(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* released from another thread: process exit frees it */ }
            mutex.Dispose();
        }
    }
}
