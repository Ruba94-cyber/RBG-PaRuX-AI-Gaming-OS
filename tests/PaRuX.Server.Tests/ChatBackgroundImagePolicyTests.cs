using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatBackgroundImagePolicyTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
    private static readonly byte[] Jpg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 };
    private static readonly byte[] Webp = { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 };

    [Theory]
    [InlineData("a.png")]
    [InlineData("A.PNG")]
    [InlineData("photo.jpeg")]
    [InlineData("x.webp")]
    public void Known_image_types_with_matching_bytes_are_accepted(string name)
    {
        string lower = name.ToLowerInvariant();
        byte[] header = lower.EndsWith("png") ? Png : lower.EndsWith("webp") ? Webp : Jpg;
        Assert.Null(ChatBackgroundImagePolicy.ValidateSource(name, 1000, header));
    }

    [Theory]
    [InlineData("a.svg")]
    [InlineData("a.gif")]
    [InlineData("a.exe")]
    [InlineData("noext")]
    [InlineData("")]
    public void Unsupported_types_are_refused(string name) =>
        Assert.Equal("unsupported-type", ChatBackgroundImagePolicy.ValidateSource(name, 1000, Png));

    [Fact]
    public void Bytes_must_match_the_extension_and_size_is_capped()
    {
        Assert.Equal("not-an-image", ChatBackgroundImagePolicy.ValidateSource("a.png", 1000, Jpg));
        Assert.Equal("not-an-image", ChatBackgroundImagePolicy.ValidateSource("a.jpg", 1000, "<svg/>"u8));
        Assert.Equal("too-large", ChatBackgroundImagePolicy.ValidateSource("a.png", ChatBackgroundImagePolicy.MaxBytes + 1, Png));
        Assert.Equal("empty", ChatBackgroundImagePolicy.ValidateSource("a.png", 0, Png));
    }

    [Fact]
    public void Stored_names_are_content_addressed_and_only_generated_names_validate()
    {
        string a = ChatBackgroundImagePolicy.StoredFileName(Png, "PNG");
        Assert.Equal(a, ChatBackgroundImagePolicy.StoredFileName(Png, "png"));
        Assert.NotEqual(a, ChatBackgroundImagePolicy.StoredFileName(Jpg, "jpg"));
        Assert.True(ChatBackgroundImagePolicy.IsValidStoredName(a));
        Assert.True(ChatBackgroundImagePolicy.IsValidStoredName(ChatBackgroundImagePolicy.StoredFileName(Webp, "webp")));
        foreach (string bad in new[] { "..\\x.png", "C:\\x.png", "bg-0123456789abcdef.exe", "bg-XYZ.png", "../bg-0123456789abcdef.png", "http://x/y.png", "", " " })
            Assert.False(ChatBackgroundImagePolicy.IsValidStoredName(bad), bad);
        Assert.False(ChatBackgroundImagePolicy.IsValidStoredName(null));
        Assert.Throws<ArgumentException>(() => ChatBackgroundImagePolicy.StoredFileName(Png, "svg"));
    }

    [Fact]
    public void The_scrim_keeps_text_readable_on_the_worst_pixel_in_both_themes()
    {
        Assert.True(ChatBackgroundImagePolicy.WorstCaseContrast("#FFFFFF", darkTheme: true) >= ChatAppearanceTheme.MinContrast);
        Assert.True(ChatBackgroundImagePolicy.WorstCaseContrast("#111111", darkTheme: false) >= ChatAppearanceTheme.MinContrast);
        Assert.Equal("#000000", ChatBackgroundImagePolicy.Scrim(true).Color);
        Assert.Equal("#FFFFFF", ChatBackgroundImagePolicy.Scrim(false).Color);
    }
}
