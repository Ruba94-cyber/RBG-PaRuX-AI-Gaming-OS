using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PaRuX.Intelligence;

/// <summary>
/// Optional per-chat background image. The conversation stores only a generated file name (never a path or URL);
/// the host copies the picked image into its own folder under that name. Only still-image formats whose first bytes
/// match their extension are accepted, with a size cap, and a theme-aware scrim is always painted over the image so
/// the text keeps at least <see cref="ChatAppearanceTheme.MinContrast"/> against the worst possible pixel.
/// </summary>
public static class ChatBackgroundImagePolicy
{
    public const long MaxBytes = 8L * 1024 * 1024;
    public const double ScrimOpacity = 0.72;
    public const string DarkScrimColor = "#000000";
    public const string LightScrimColor = "#FFFFFF";

    private static readonly Regex StoredName = new(@"^bg-[0-9a-f]{16}\.(png|jpg|webp|bmp)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Null when the file may be used; otherwise a short English reason code (the host shows its own localized text).</summary>
    public static string? ValidateSource(string? fileName, long length, ReadOnlySpan<byte> header)
    {
        string? ext = NormalizedExtension(fileName);
        if (ext == null) return "unsupported-type";
        if (length <= 0) return "empty";
        if (length > MaxBytes) return "too-large";
        return MatchesSignature(ext, header) ? null : "not-an-image";
    }

    /// <summary>Canonical stored extension (png, jpg, webp, bmp) for a picked file name, or null if unsupported.</summary>
    public static string? NormalizedExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch { "png" => "png", "jpg" or "jpeg" => "jpg", "webp" => "webp", "bmp" => "bmp", _ => null };
    }

    /// <summary>Content-addressed stored name, e.g. bg-0123456789abcdef.png. The same image gives the same name.</summary>
    public static string StoredFileName(ReadOnlySpan<byte> content, string extension)
    {
        string ext = NormalizedExtension("x." + extension) ?? throw new ArgumentException("Unsupported image type.", nameof(extension));
        string hash = Convert.ToHexString(SHA256.HashData(content))[..16].ToLowerInvariant();
        return "bg-" + hash + "." + ext;
    }

    /// <summary>True only for names this policy generates, so a hand-edited conversation can never point outside the folder.</summary>
    public static bool IsValidStoredName(string? name) => name != null && StoredName.IsMatch(name);

    /// <summary>Scrim for the active theme: black over images in a dark theme, white in a light one.</summary>
    public static (string Color, double Opacity) Scrim(bool darkTheme) =>
        (darkTheme ? DarkScrimColor : LightScrimColor, ScrimOpacity);

    /// <summary>Contrast of <paramref name="textColor"/> over the worst pixel (pure white or pure black) after the scrim.</summary>
    public static double WorstCaseContrast(string textColor, bool darkTheme)
    {
        (string scrim, double opacity) = Scrim(darkTheme);
        int scrimValue = scrim == DarkScrimColor ? 0 : 255;
        double worst = double.MaxValue;
        foreach (int pixel in new[] { 0, 255 })
        {
            int blended = (int)Math.Round(opacity * scrimValue + (1 - opacity) * pixel);
            string hex = "#" + blended.ToString("X2") + blended.ToString("X2") + blended.ToString("X2");
            worst = Math.Min(worst, ChatAppearancePolicy.Contrast(textColor, hex));
        }
        return worst;
    }

    private static bool MatchesSignature(string ext, ReadOnlySpan<byte> h) => ext switch
    {
        "png" => h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47 && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A,
        "jpg" => h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF,
        "bmp" => h.Length >= 2 && h[0] == 0x42 && h[1] == 0x4D,
        "webp" => h.Length >= 12 && h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46 && h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50,
        _ => false
    };
}
