namespace PaRuX.Server;

/// <summary>
/// Reads a small text file that changes only on a model swap (active role, active model path) without re-opening it
/// on every chat turn. A cached read is reused only while the file's size and last-write time are unchanged AND the
/// write is old enough that a second write could not share its timestamp (the same "racy timestamp" guard Git uses),
/// so a swap that rewrites the file is always seen on the next call. A missing file reads as null.
/// </summary>
internal sealed class StampedTextFileCache
{
    /// <summary>A file written more recently than this is re-read every time: a later write in the same clock tick would be invisible to the stamp.</summary>
    internal static readonly TimeSpan RacyWindow = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private string? _path;
    private long _ticks;
    private long _length;
    private string? _text;
    private bool _valid;

    internal string? Read(string path, DateTime? utcNow = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { lock (_gate) _valid = false; return null; }
            long ticks = info.LastWriteTimeUtc.Ticks;
            long length = info.Length;
            lock (_gate)
            {
                if (_valid && string.Equals(_path, path, StringComparison.Ordinal) && _ticks == ticks && _length == length)
                    return _text;
            }

            string text = File.ReadAllText(path).Trim();
            bool settled = (utcNow ?? DateTime.UtcNow) - info.LastWriteTimeUtc > RacyWindow;
            lock (_gate)
            {
                _valid = settled;
                _path = path;
                _ticks = ticks;
                _length = length;
                _text = text;
            }
            return text;
        }
        catch
        {
            lock (_gate) _valid = false;
            return null;
        }
    }
}
