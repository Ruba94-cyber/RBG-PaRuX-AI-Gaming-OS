namespace PaRuX.Server;

/// <summary>Reads free bytes on the volume used by PaRuX-managed model downloads. A null result
/// means the destination volume could not be inspected; it must not be represented as zero bytes.</summary>
public interface IModelDestinationSpaceProvider
{
    long? GetAvailableBytes(string destinationDirectory);
}

/// <summary>Windows/Linux-compatible volume probe. It never creates the target directory or returns
/// the path to clients; only the current user's available free-space count crosses the API.</summary>
public sealed class ModelDestinationSpaceProvider : IModelDestinationSpaceProvider
{
    public long? GetAvailableBytes(string destinationDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory)) return null;
            string fullPath = Path.GetFullPath(destinationDirectory);
            string? volumeRoot = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(volumeRoot)) return null;

            try
            {
                var volume = new DriveInfo(volumeRoot);
                if (volume.IsReady)
                {
                    long available = volume.AvailableFreeSpace;
                    if (available >= 0) return available;
                }
            }
            catch (ArgumentException) { /* UNC / redirected folder: DriveInfo only accepts drive letters */ }
            return TryGetFreeBytesByPath(volumeRoot);
        }
        catch
        {
            // Unsupported, unavailable or invalid destinations are an explicit unknown.
            return null;
        }
    }

    /// <summary>
    /// Corporate PCs often keep user folders on a network share or redirected path (\server\share), which
    /// <see cref="DriveInfo"/> rejects; that used to surface as "free space unavailable". The per-user free space of
    /// any path is available from GetDiskFreeSpaceEx.
    /// </summary>
    private static long? TryGetFreeBytesByPath(string root)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return GetDiskFreeSpaceExW(root, out ulong free, out _, out _) && free <= long.MaxValue ? (long)free : null;
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);
}
