using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.CharacterStudio;

/// <summary>Reads width, height and type from the first bytes of a PNG, JPEG or WebP, so no image library is needed.</summary>
internal static class CharacterImageHeader
{
    internal static bool TryRead(byte[] b, out int width, out int height, out string mediaType, out string extension)
    {
        width = height = 0; mediaType = string.Empty; extension = string.Empty;
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        {
            width = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            height = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            mediaType = "image/png"; extension = ".png";
            return width > 0 && height > 0;
        }
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                byte marker = b[i + 1];
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
                int len = (b[i + 2] << 8) | b[i + 3];
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    height = (b[i + 5] << 8) | b[i + 6];
                    width = (b[i + 7] << 8) | b[i + 8];
                    mediaType = "image/jpeg"; extension = ".jpg";
                    return width > 0 && height > 0;
                }
                if (len < 2) break;
                i += 2 + len;
            }
            return false;
        }
        if (b.Length > 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        {
            mediaType = "image/webp"; extension = ".webp";
            if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X') { width = 1 + (b[24] | b[25] << 8 | b[26] << 16); height = 1 + (b[27] | b[28] << 8 | b[29] << 16); }
            else if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == ' ') { width = (b[26] | b[27] << 8) & 0x3FFF; height = (b[28] | b[29] << 8) & 0x3FFF; }
            else if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L') { int v = b[21] | b[22] << 8 | b[23] << 16 | b[24] << 24; width = (v & 0x3FFF) + 1; height = ((v >> 14) & 0x3FFF) + 1; }
            return width > 0 && height > 0;
        }
        return false;
    }
}

/// <summary>Characters on disk: one JSON index in the state folder, pictures and training runs under the data root. Atomic writes.</summary>
internal sealed class CharacterStudioStore
{
    internal const int MaxImageBytes = 25 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _stateDir;
    private readonly string _dataRoot;
    private List<CharacterProfile>? _cache;

    internal CharacterStudioStore() : this(ServerState.StateDirectory, Path.Combine(LocalAiPaths.Root, "character-studio")) { }
    internal CharacterStudioStore(string stateDir, string dataRoot) { _stateDir = stateDir; _dataRoot = dataRoot; }

    private string IndexPath => Path.Combine(_stateDir, "characters.json");
    internal string CharacterDir(string id) => Path.Combine(_dataRoot, SafeId(id));
    internal string ImagesDir(string id) => Path.Combine(CharacterDir(id), "images");
    internal string RunDir(string id) => Path.Combine(CharacterDir(id), "run");

    internal static string SafeId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 40 || !id.All(char.IsAsciiLetterOrDigit)) throw new InvalidDataException("Unknown character.");
        return id;
    }

    private List<CharacterProfile> Load()
    {
        if (_cache != null) return _cache;
        _cache = new List<CharacterProfile>();
        try
        {
            if (File.Exists(IndexPath))
                _cache = JsonSerializer.Deserialize<List<CharacterProfile>>(File.ReadAllText(IndexPath)) ?? new List<CharacterProfile>();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* a damaged index starts empty; the pictures stay on disk */ }
        foreach (CharacterProfile c in _cache) c.Training.Stage = CharacterStageMachine.AfterRestart(c.Training.Stage);
        return _cache;
    }

    private void Save()
    {
        Directory.CreateDirectory(_stateDir);
        string tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_cache));
        File.Move(tmp, IndexPath, true);
    }

    internal IReadOnlyList<CharacterProfile> List() { lock (_gate) return Load().Select(Clone).ToList(); }

    internal CharacterProfile? Get(string id) { lock (_gate) return Load().Where(c => c.Id == id).Select(Clone).FirstOrDefault(); }

    /// <summary>The character attached to a chat persona, if it has a trained LoRA.</summary>
    internal CharacterProfile? ForPersona(string? personaId)
    {
        if (string.IsNullOrEmpty(personaId)) return null;
        lock (_gate) return Load().Where(c => c.PersonaId == personaId && c.Binding != null).Select(Clone).FirstOrDefault();
    }

    internal CharacterProfile Add(CharacterProfile c)
    {
        lock (_gate) { Load().Add(c); Save(); return Clone(c); }
    }

    /// <summary>Applies a change to the live record under the lock and saves. Returns a copy.</summary>
    internal CharacterProfile Mutate(string id, Action<CharacterProfile> change)
    {
        lock (_gate)
        {
            CharacterProfile c = Load().FirstOrDefault(x => x.Id == id) ?? throw new InvalidDataException("Unknown character.");
            change(c);
            c.UpdatedUtc = DateTime.UtcNow;
            c.Training.UpdatedUtc = c.UpdatedUtc;
            Save();
            return Clone(c);
        }
    }

    internal bool Delete(string id)
    {
        lock (_gate)
        {
            int n = Load().RemoveAll(c => c.Id == id);
            if (n == 0) return false;
            Save();
        }
        try { string dir = CharacterDir(id); if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return true;
    }

    /// <summary>Validates and stores one picture, returning its record. Rejects anything that is not a real PNG, JPEG or WebP.</summary>
    internal CharacterImage SaveImage(string characterId, byte[] bytes, string source, string label, long seed)
    {
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes) throw new InvalidDataException("A picture must be between 1 byte and " + MaxImageBytes / (1024 * 1024) + " MB.");
        if (!CharacterImageHeader.TryRead(bytes, out int w, out int h, out string _, out string ext))
            throw new InvalidDataException("That file is not a PNG, JPG or WebP picture.");
        string id = Guid.NewGuid().ToString("N")[..12];
        string dir = ImagesDir(characterId);
        Directory.CreateDirectory(dir);
        string file = id + ext;
        File.WriteAllBytes(Path.Combine(dir, file), bytes);
        return new CharacterImage
        {
            Id = id, File = file, Source = source, Label = label.Length > 200 ? label[..200] : label, Seed = seed,
            Width = w, Height = h, SizeBytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        };
    }

    internal string? ImagePath(string characterId, string file)
    {
        if (string.IsNullOrEmpty(file) || file != Path.GetFileName(file)) return null;
        string path = Path.Combine(ImagesDir(characterId), file);
        return File.Exists(path) ? path : null;
    }

    private static CharacterProfile Clone(CharacterProfile c) =>
        JsonSerializer.Deserialize<CharacterProfile>(JsonSerializer.Serialize(c))!;
}
