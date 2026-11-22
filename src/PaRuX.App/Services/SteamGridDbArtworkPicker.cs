using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Stores the artwork the owner chose from the SteamGridDB gallery. It reuses <see cref="GameCoverStore"/> (validated by file
    /// signature, size capped, content-hash named, original never modified): the grid is the title's cover, hero/logo/icon are kept
    /// under a kind-suffixed key of the same index. Nothing is deleted except the replaced copy the store itself owns.
    /// </summary>
    public static class SteamGridDbArtworkPicker
    {
        private static readonly Lazy<SteamGridDbGallery> Shared = new(() => new SteamGridDbGallery(SteamGridDbGallery.CreateClient(), SteamGridDbKeyStore.TryGet));

        public static SteamGridDbGallery Gallery => Shared.Value;

        /// <summary>Path of the chosen hero/logo/icon (or the cover for Grid), or null when none was chosen.</summary>
        public static string? TryGetPath(string titleKey, SteamGridDbArtKind kind) =>
            GameCoverStore.TryGetPath(SteamGridDbGallery.StoreKey(titleKey, kind));

        /// <summary>Downloads the candidate (https, SteamGridDB CDN only, capped at the cover limit) and stores it for the title.</summary>
        public static async Task<GameCoverResult> ChooseAsync(string titleKey, SteamGridDbCandidate candidate, CancellationToken ct)
        {
            SteamGridDbResult<byte[]> image = await Gallery.FetchImageAsync(candidate.Url, GameCoverStore.MaxBytes, ct).ConfigureAwait(false);
            if (!image.Ok || image.Value is null)
                return image.Status == SteamGridDbStatus.TooLarge ? GameCoverResult.TooLarge : GameCoverResult.Failed;
            string? extension = GameCoverStore.DetectExtension(image.Value);
            if (extension is null) return GameCoverResult.UnsupportedType;
            string temp = Path.Combine(Path.GetTempPath(), "parux-art-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                await File.WriteAllBytesAsync(temp, image.Value, ct).ConfigureAwait(false);
                return GameCoverStore.Set(SteamGridDbGallery.StoreKey(titleKey, candidate.Kind), temp);
            }
            finally { try { File.Delete(temp); } catch (IOException) { } }
        }
    }
}
