using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameArtStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-art-test-" + Guid.NewGuid().ToString("N"));

    public GameArtStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    // Synthetic header-only fixtures: only the signature and size fields matter to the inspector.
    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        b[11] = 13;
        "IHDR"u8.CopyTo(b.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)h);
        return b;
    }

    private static byte[] Jpeg(int w, int h)
    {
        // SOI, an APP0 segment to be skipped, then SOF0 (height before width).
        var list = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x06, 0, 0, 0, 0, 0xFF, 0xC0, 0x00, 0x11, 8 };
        list.AddRange(new[] { (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w });
        list.AddRange(new byte[12]);
        return list.ToArray();
    }

    private static byte[] WebpLossless(int w, int h)
    {
        var b = new byte[40];
        "RIFF"u8.CopyTo(b); "WEBP"u8.CopyTo(b.AsSpan(8)); "VP8L"u8.CopyTo(b.AsSpan(12));
        b[20] = 0x2F;
        uint bits = (uint)(w - 1) | ((uint)(h - 1) << 14);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(21), bits);
        return b;
    }

    private string Store(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Inspect_reads_size_of_png_jpeg_and_webp_and_rejects_other_content()
    {
        Assert.Equal((".png", 1920, 620), GameArtStore.Inspect(Png(1920, 620)));
        Assert.Equal((".jpg", 1600, 900), GameArtStore.Inspect(Jpeg(1600, 900)));
        Assert.Equal((".webp", 1280, 720), GameArtStore.Inspect(WebpLossless(1280, 720)));
        Assert.Null(GameArtStore.Inspect("<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
        Assert.Null(GameArtStore.Inspect(new byte[64]));
        Assert.Null(GameArtStore.Inspect(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData(GameArtKind.Hero, 1920, 620, true)]
    [InlineData(GameArtKind.Hero, 600, 900, false)]      // portrait is not a hero
    [InlineData(GameArtKind.Hero, 9000, 3000, false)]    // side cap
    [InlineData(GameArtKind.Hero, 8000, 7000, false)]    // pixel cap
    [InlineData(GameArtKind.Hero, 8, 4, false)]          // too small
    [InlineData(GameArtKind.Logo, 800, 300, true)]
    [InlineData(GameArtKind.Logo, 300, 300, true)]
    [InlineData(GameArtKind.Cover, 600, 900, true)]
    public void Dimensions_are_bounded_per_kind(GameArtKind kind, int w, int h, bool ok) =>
        Assert.Equal(ok, GameArtStore.AreDimensionsValid(kind, w, h));

    [Fact]
    public void Containment_rejects_traversal_siblings_and_the_root_itself()
    {
        Assert.True(GameArtStore.IsContained(_root, Path.Combine(_root, "a.png")));
        Assert.False(GameArtStore.IsContained(_root, Path.Combine(_root, "..", "a.png")));
        Assert.False(GameArtStore.IsContained(_root, _root));
        Assert.False(GameArtStore.IsContained(_root, _root + "-sibling" + Path.DirectorySeparatorChar + "a.png"));
        Assert.False(GameArtStore.IsContained(_root, ""));
        Assert.False(GameArtStore.IsContained("", Path.Combine(_root, "a.png")));
    }

    [Fact]
    public void Resolve_returns_valid_hero_and_logo_and_null_for_everything_else()
    {
        string hero = Store("h.png", Png(1920, 620));
        string logo = Store("l.png", Png(640, 240));
        string portraitAsHero = Store("p.png", Png(600, 900));
        string notImage = Store("x.png", "not an image at all, just text"u8.ToArray());
        string empty = Store("e.png", Array.Empty<byte>());
        string outside = Path.Combine(Path.GetTempPath(), "parux-art-outside.png");
        File.WriteAllBytes(outside, Png(1920, 620));
        try
        {
            var map = new Dictionary<string, string>
            {
                [GameArtStore.StoreKey("good", GameArtKind.Hero)] = hero,
                [GameArtStore.StoreKey("good", GameArtKind.Logo)] = logo,
                [GameArtStore.StoreKey("portrait", GameArtKind.Hero)] = portraitAsHero,
                [GameArtStore.StoreKey("junk", GameArtKind.Hero)] = notImage,
                [GameArtStore.StoreKey("empty", GameArtKind.Hero)] = empty,
                [GameArtStore.StoreKey("escape", GameArtKind.Hero)] = outside,
                [GameArtStore.StoreKey("gone", GameArtKind.Hero)] = Path.Combine(_root, "missing.png"),
            };
            string? Lookup(string k) => map.TryGetValue(k, out string? v) ? v : null;

            GameArtInfo? h = GameArtStore.Resolve("good", GameArtKind.Hero, Lookup, _root);
            Assert.NotNull(h);
            Assert.Equal((1920, 620), (h!.Width, h.Height));
            Assert.Equal(GameArtKind.Hero, h.Kind);
            Assert.NotNull(GameArtStore.Resolve("good", GameArtKind.Logo, Lookup, _root));
            Assert.Null(GameArtStore.Resolve("good", GameArtKind.Cover, Lookup, _root));
            foreach (string t in new[] { "portrait", "junk", "empty", "escape", "gone", "unknown", "" })
                Assert.Null(GameArtStore.Resolve(t, GameArtKind.Hero, Lookup, _root));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void Oversized_files_are_refused_without_reading_them()
    {
        string path = Path.Combine(_root, "big.png");
        using (var f = File.Create(path)) { f.Write(Png(1920, 620)); f.SetLength(GameArtStore.MaxBytes + 1); }
        Assert.Null(GameArtStore.Resolve("t", GameArtKind.Hero, _ => path, _root));
    }

    [Fact]
    public void Existing_cover_index_still_resolves_and_has_no_hero_or_logo()
    {
        // covers.json written before hero/logo existed: plain title key -> stored name. Cover keys must be unchanged.
        string cover = Store("0123456789abcdef0123456789abcdef.png", Png(600, 900));
        var index = JsonSerializer.Deserialize<Dictionary<string, string>>(
            "{\"steam:100\":\"0123456789abcdef0123456789abcdef.png\"}")!;
        string? Lookup(string k) => index.TryGetValue(k, out string? n) ? Path.Combine(_root, n) : null;

        Assert.Equal("steam:100", GameArtStore.StoreKey("steam:100", GameArtKind.Cover));
        Assert.Equal(cover, GameArtStore.Resolve("steam:100", GameArtKind.Cover, Lookup, _root)!.Path);
        Assert.Null(GameArtStore.Resolve("steam:100", GameArtKind.Hero, Lookup, _root));
        Assert.Null(GameArtStore.Resolve("steam:100", GameArtKind.Logo, Lookup, _root));
        Assert.NotEqual(GameArtStore.StoreKey("steam:100", GameArtKind.Hero), GameArtStore.StoreKey("steam:100", GameArtKind.Logo));
    }
}
