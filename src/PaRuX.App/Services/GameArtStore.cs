using System;
using System.Buffers.Binary;
using System.IO;

namespace PaRuX.Services
{
    /// <summary>Artwork roles a game can have. Cover is the portrait box art; Hero is landscape banner art; Logo is a transparent title logo.</summary>
    public enum GameArtKind { Cover, Hero, Logo }

    /// <summary>A validated local art file ready to decode.</summary>
    public sealed record GameArtInfo(string Path, GameArtKind Kind, int Width, int Height, string Extension);

    /// <summary>
    /// Pure validation and resolution for per-game art. It adds NO new storage: covers, heroes and logos all live in the existing
    /// <c>GameCoverStore</c> index (a cover under the plain title key, hero/logo under the kind-suffixed key produced by
    /// <see cref="SteamGridDbGallery.StoreKey"/>), so libraries written before heroes/logos existed keep resolving unchanged.
    /// Every resolved file is re-checked: it must sit under the store folder (no traversal), carry a png/jpg/webp signature,
    /// stay within the byte cap and have sane, kind-appropriate dimensions. Anything else resolves to null so the UI falls back
    /// silently. Reading is bounded (header only) and meant to run off the UI thread.
    /// </summary>
    public static class GameArtStore
    {
        public const long MaxBytes = 5L * 1024 * 1024;
        public const int MinSide = 16;
        public const int MaxSide = 8192;
        public const long MaxPixels = 40_000_000;
        public const double MinHeroAspect = 1.2;
        private const int HeaderBytes = 256 * 1024;

        public static SteamGridDbArtKind ToGalleryKind(GameArtKind kind) => kind switch
        {
            GameArtKind.Hero => SteamGridDbArtKind.Hero,
            GameArtKind.Logo => SteamGridDbArtKind.Logo,
            _ => SteamGridDbArtKind.Grid,
        };

        /// <summary>Index key of a title's art of one kind. Cover keeps the legacy plain key.</summary>
        public static string StoreKey(string titleKey, GameArtKind kind) => SteamGridDbGallery.StoreKey(titleKey, ToGalleryKind(kind));

        /// <summary>True when <paramref name="path"/> resolves to a location strictly inside <paramref name="root"/>.</summary>
        public static bool IsContained(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0) return false;
            try
            {
                string fullRoot = Path.GetFullPath(root);
                if (!fullRoot.EndsWith(Path.DirectorySeparatorChar)) fullRoot += Path.DirectorySeparatorChar;
                string fullPath = Path.GetFullPath(path);
                return fullPath.Length > fullRoot.Length && fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }

        /// <summary>Whether the dimensions are acceptable for the kind (bounded decode cost, landscape for heroes).</summary>
        public static bool AreDimensionsValid(GameArtKind kind, int width, int height)
        {
            if (width < MinSide || height < MinSide || width > MaxSide || height > MaxSide) return false;
            if ((long)width * height > MaxPixels) return false;
            return kind != GameArtKind.Hero || (double)width / height >= MinHeroAspect;
        }

        /// <summary>Signature plus pixel size from the first bytes of an image, or null when it is not a png/jpg/webp with a readable size.</summary>
        public static (string Extension, int Width, int Height)? Inspect(ReadOnlySpan<byte> head)
        {
            if (head.Length >= 24 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
                && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A
                && head[12] == (byte)'I' && head[13] == (byte)'H' && head[14] == (byte)'D' && head[15] == (byte)'R')
                return (".png", (int)BinaryPrimitives.ReadUInt32BigEndian(head[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(head[20..]));
            if (head.Length >= 4 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return InspectJpeg(head);
            if (head.Length >= 30 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
                && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P')
                return InspectWebp(head);
            return null;
        }

        private static (string, int, int)? InspectJpeg(ReadOnlySpan<byte> d)
        {
            int i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                byte marker = d[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
                int length = (d[i + 2] << 8) | d[i + 3];
                if (length < 2) return null;
                bool sof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
                if (sof) return (".jpg", (d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
                i += 2 + length;
            }
            return null;
        }

        private static (string, int, int)? InspectWebp(ReadOnlySpan<byte> d)
        {
            if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == ' ')
            {
                if (d[23] != 0x9D || d[24] != 0x01 || d[25] != 0x2A) return null;
                return (".webp", (d[26] | (d[27] << 8)) & 0x3FFF, (d[28] | (d[29] << 8)) & 0x3FFF);
            }
            if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == 'L')
            {
                if (d[20] != 0x2F) return null;
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(d[21..]);
                return (".webp", (int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }
            if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == 'X')
                return (".webp", (d[24] | (d[25] << 8) | (d[26] << 16)) + 1, (d[27] | (d[28] << 8) | (d[29] << 16)) + 1);
            return null;
        }

        /// <summary>
        /// Resolves and validates the art of a title. <paramref name="lookup"/> maps a store key to the stored file path (null when none);
        /// <paramref name="root"/> is the folder every stored file must live in. Null on any problem, never throws for bad files.
        /// </summary>
        public static GameArtInfo? Resolve(string titleKey, GameArtKind kind, Func<string, string?> lookup, string root)
        {
            if (string.IsNullOrEmpty(titleKey)) return null;
            string? path = lookup(StoreKey(titleKey, kind));
            if (path is null || !IsContained(root, path)) return null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0 || info.Length > MaxBytes) return null;
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                byte[] head = new byte[(int)Math.Min(info.Length, HeaderBytes)];
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int read = 0;
                    while (read < head.Length)
                    {
                        int n = stream.Read(head, read, head.Length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    if (read < head.Length) Array.Resize(ref head, read);
                }
                var inspected = Inspect(head);
                if (inspected is not { } found || !AreDimensionsValid(kind, found.Width, found.Height)) return null;
                return new GameArtInfo(path, kind, found.Width, found.Height, found.Extension);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        }
    }
}
