using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class StampedTextFileCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-stamped-" + Guid.NewGuid().ToString("N"));

    public StampedTextFileCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Write(string name, string text, DateTime lastWriteUtc)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void Missing_file_reads_as_null_and_text_is_trimmed()
    {
        var cache = new StampedTextFileCache();
        Assert.Null(cache.Read(Path.Combine(_dir, "none.txt")));
        string path = Write("a.txt", "standard\r\n", DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal("standard", cache.Read(path));
    }

    [Fact]
    public void Settled_unchanged_file_is_served_from_the_stamp_without_reopening()
    {
        var cache = new StampedTextFileCache();
        DateTime old = DateTime.UtcNow.AddMinutes(-5);
        string path = Write("role.txt", "coding", old);
        Assert.Equal("coding", cache.Read(path));

        // Same size and timestamp but different bytes: only a cache hit can still answer with the first text.
        File.WriteAllText(path, "vision");
        File.SetLastWriteTimeUtc(path, old);
        Assert.Equal("coding", cache.Read(path));
    }

    [Fact]
    public void A_rewrite_with_a_new_timestamp_or_size_is_always_seen()
    {
        var cache = new StampedTextFileCache();
        DateTime old = DateTime.UtcNow.AddMinutes(-5);
        string path = Write("role.txt", "coding", old);
        Assert.Equal("coding", cache.Read(path));

        File.WriteAllText(path, "vision");
        File.SetLastWriteTimeUtc(path, old.AddSeconds(1));
        Assert.Equal("vision", cache.Read(path));

        File.WriteAllText(path, "reasoning");
        File.SetLastWriteTimeUtc(path, old.AddSeconds(1));
        Assert.Equal("reasoning", cache.Read(path));
    }

    [Fact]
    public void A_file_written_inside_the_racy_window_is_never_trusted_from_the_stamp()
    {
        var cache = new StampedTextFileCache();
        DateTime now = DateTime.UtcNow;
        string path = Write("role.txt", "coding", now);
        Assert.Equal("coding", cache.Read(path, now));

        File.WriteAllText(path, "vision");
        File.SetLastWriteTimeUtc(path, now);
        Assert.Equal("vision", cache.Read(path, now));
    }

    [Fact]
    public void A_deleted_file_reads_as_null_again()
    {
        var cache = new StampedTextFileCache();
        string path = Write("role.txt", "coding", DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal("coding", cache.Read(path));
        File.Delete(path);
        Assert.Null(cache.Read(path));
    }
}
