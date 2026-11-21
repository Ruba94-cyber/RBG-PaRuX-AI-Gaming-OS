using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace PaRuX.Contracts;

public enum SteamShortcutOutcome
{
    Added,
    /// <summary>The same program with the same name is already listed; nothing was written.</summary>
    AlreadyPresent,
    SteamRunning,
    /// <summary>The existing file is not a shortcuts file PaRuX can read exactly, so it was left untouched.</summary>
    UnreadableFile,
    InvalidRequest,
    Failed
}

public sealed record SteamShortcutResult(SteamShortcutOutcome Outcome, string Message, string? BackupPath = null, uint AppId = 0);

/// <summary>One Steam account folder under <c>userdata</c>.</summary>
public sealed record SteamUserFolder(string AccountId, string ConfigDirectory, DateTime LastWriteUtc);

/// <summary>What Steam should list: a non-Steam game that starts PaRuX with <c>--play &lt;titleKey&gt;</c>.</summary>
public sealed record SteamShortcutEntry(string AppName, string ExecutablePath, string StartDirectory, string LaunchOptions, string IconPath);

/// <summary>
/// Adds a library game to Steam as a "non-Steam game" by appending one entry to the account's
/// <c>userdata\&lt;id&gt;\config\shortcuts.vdf</c>.
///
/// Steam is USER_MANAGED software, so this is only ever done after an explicit user action, and conservatively:
/// - append-only: existing entries are never rewritten, reordered or removed, and their bytes are kept as they are;
/// - a timestamped backup of the existing file is written next to it first;
/// - refused while Steam is running (Steam rewrites the file when it exits and would drop or fight the change);
/// - refused when the file cannot be parsed exactly, rather than guessing;
/// - an entry for the same program and name is never added twice.
/// Art (hero, logo, icon) is not touched: Steam shows the executable's icon, and artwork stays the user's choice in Steam.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SteamShortcutsFile
{
    private const byte TypeMap = 0x00, TypeString = 0x01, TypeInt32 = 0x02, TypeFloat = 0x03, TypePointer = 0x04,
        TypeWideString = 0x05, TypeColor = 0x06, TypeUInt64 = 0x07, TypeEnd = 0x08, TypeInt64 = 0x0A;
    private const long MaxFileBytes = 8 * 1024 * 1024;
    private const int MaxDepth = 16;

    // ------------------------------------------------------------------ locating Steam

    /// <summary>The Steam folder (the one that holds steam.exe and userdata), or null when Steam is not installed.</summary>
    public static string? FindSteamRoot()
    {
        var candidates = new List<string>();
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path && !string.IsNullOrWhiteSpace(path)) candidates.Add(path.Replace('/', '\\'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFiles)) candidates.Add(Path.Combine(programFiles, "Steam"));

        foreach (string candidate in candidates)
        {
            try
            {
                if (!Path.IsPathFullyQualified(candidate)) continue;
                string root = Path.GetFullPath(candidate);
                if (File.Exists(Path.Combine(root, "steam.exe")) && Directory.Exists(Path.Combine(root, "userdata"))) return root;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    /// <summary>Numeric account folders under <c>userdata</c>, most recently used first. Never creates anything.</summary>
    public static IReadOnlyList<SteamUserFolder> FindUsers(string steamRoot)
    {
        var users = new List<SteamUserFolder>();
        try
        {
            string userdata = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userdata)) return users;
            foreach (string directory in Directory.EnumerateDirectories(userdata).Take(64))
            {
                string id = Path.GetFileName(directory);
                if (id.Length == 0 || id == "0" || !id.All(char.IsAsciiDigit)) continue;
                string config = Path.Combine(directory, "config");
                DateTime stamp = Directory.Exists(config) ? Directory.GetLastWriteTimeUtc(config) : Directory.GetLastWriteTimeUtc(directory);
                users.Add(new SteamUserFolder(id, config, stamp));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return users.OrderByDescending(u => u.LastWriteUtc).ToList();
    }

    public static bool IsSteamRunning()
    {
        try
        {
            foreach (Process process in Process.GetProcessesByName("steam"))
            {
                process.Dispose();
                return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return false;
    }

    // ------------------------------------------------------------------ the entry

    /// <summary>Steam's id for a non-Steam shortcut: CRC32 of the quoted executable plus the name, with the top bit set.</summary>
    public static uint AppIdFor(string quotedExecutable, string appName)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in Encoding.UTF8.GetBytes(quotedExecutable + appName))
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return (crc ^ 0xFFFFFFFFu) | 0x80000000u;
    }

    /// <summary>The entry PaRuX adds for one game. Paths are quoted the way Steam stores them.</summary>
    public static SteamShortcutEntry EntryFor(string gameName, string titleKey, string executablePath, string? iconPath = null)
    {
        string exe = Path.GetFullPath(executablePath);
        string icon = !string.IsNullOrWhiteSpace(iconPath) && Path.IsPathFullyQualified(iconPath) && File.Exists(iconPath)
            ? Path.GetFullPath(iconPath) : exe;
        return new SteamShortcutEntry(
            CleanName(gameName),
            "\"" + exe + "\"",
            "\"" + (Path.GetDirectoryName(exe) ?? string.Empty) + "\"",
            GameLaunchRequestChannel.ArgumentsFor(titleKey),
            icon);
    }

    private static string CleanName(string? name)
    {
        string text = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > 120) text = text[..120].TrimEnd();
        return text.Length == 0 ? "Game" : text;
    }

    // ------------------------------------------------------------------ appending

    /// <summary>
    /// Appends the entry to <paramref name="shortcutsVdfPath"/> (creating the file when the account has none yet).
    /// <paramref name="isSteamRunning"/> defaults to a real process check; tests inject their own.
    /// </summary>
    public static SteamShortcutResult TryAppend(
        string shortcutsVdfPath, SteamShortcutEntry entry, Func<bool>? isSteamRunning = null, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(shortcutsVdfPath) || !Path.IsPathFullyQualified(shortcutsVdfPath)
            || !string.Equals(Path.GetFileName(shortcutsVdfPath), "shortcuts.vdf", StringComparison.OrdinalIgnoreCase))
            return new SteamShortcutResult(SteamShortcutOutcome.InvalidRequest, "That is not a Steam shortcuts file.");
        if (string.IsNullOrWhiteSpace(entry.AppName) || string.IsNullOrWhiteSpace(entry.ExecutablePath)
            || ContainsNul(entry.AppName) || ContainsNul(entry.ExecutablePath) || ContainsNul(entry.StartDirectory)
            || ContainsNul(entry.LaunchOptions) || ContainsNul(entry.IconPath))
            return new SteamShortcutResult(SteamShortcutOutcome.InvalidRequest, "The shortcut details are not valid.");
        string? directory = Path.GetDirectoryName(shortcutsVdfPath);
        if (directory == null || !Directory.Exists(Path.GetDirectoryName(directory) ?? directory))
            return new SteamShortcutResult(SteamShortcutOutcome.InvalidRequest, "That Steam account folder does not exist.");

        if (GameShortcuts.ExecutableProblem(Unquote(entry.ExecutablePath)) is { } exeProblem)
            return new SteamShortcutResult(SteamShortcutOutcome.InvalidRequest, exeProblem);

        if ((isSteamRunning ?? IsSteamRunning)())
            return new SteamShortcutResult(SteamShortcutOutcome.SteamRunning,
                "Steam is running. Close Steam completely, then add the game again: Steam rewrites this list when it exits and would undo the change.");

        try
        {
            bool exists = File.Exists(shortcutsVdfPath);
            byte[] original = exists ? ReadBounded(shortcutsVdfPath) : Array.Empty<byte>();
            byte[] updated;
            uint appId = AppIdFor(entry.ExecutablePath, entry.AppName);

            if (original.Length == 0)
            {
                updated = BuildNewFile(entry, appId);
            }
            else
            {
                ShortcutsLayout? layout = TryParse(original);
                if (layout == null)
                    return new SteamShortcutResult(SteamShortcutOutcome.UnreadableFile,
                        "Steam's shortcut list is in a form PaRuX cannot read exactly, so it was left untouched.");
                if (layout.Entries.Any(e => Same(e.AppName, entry.AppName) && Same(Unquote(e.Exe), Unquote(entry.ExecutablePath))
                    && string.Equals(e.LaunchOptions ?? string.Empty, entry.LaunchOptions, StringComparison.Ordinal)))
                    return new SteamShortcutResult(SteamShortcutOutcome.AlreadyPresent, "This game is already in your Steam library as a non-Steam game.", null, appId);
                updated = InsertEntry(original, layout, entry, appId);
            }

            Directory.CreateDirectory(directory);
            string? backup = null;
            if (exists && original.Length > 0)
            {
                DateTime stamp = nowUtc ?? DateTime.UtcNow;
                backup = shortcutsVdfPath + ".parux-backup-" + stamp.ToString("yyyyMMddHHmmss");
                int suffix = 1;
                while (File.Exists(backup)) backup = shortcutsVdfPath + ".parux-backup-" + stamp.ToString("yyyyMMddHHmmss") + "-" + suffix++;
                File.Copy(shortcutsVdfPath, backup);
            }

            string temp = shortcutsVdfPath + ".parux-tmp";
            File.WriteAllBytes(temp, updated);
            if (exists) File.Replace(temp, shortcutsVdfPath, null);
            else File.Move(temp, shortcutsVdfPath);
            return new SteamShortcutResult(SteamShortcutOutcome.Added,
                "Added to Steam as a non-Steam game. Start Steam to see it in your library.", backup, appId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new SteamShortcutResult(SteamShortcutOutcome.Failed, "Steam's shortcut list could not be updated: " + ex.Message);
        }
    }

    private static bool ContainsNul(string value) => value.Contains('\0');
    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string Unquote(string? value) => (value ?? string.Empty).Trim().Trim('"');

    private static byte[] ReadBounded(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException("The file is unexpectedly large.");
        return File.ReadAllBytes(path);
    }

    // ------------------------------------------------------------------ binary VDF

    private sealed record ParsedEntry(string Key, string? AppName, string? Exe, string? LaunchOptions);

    /// <param name="InsertAt">Offset of the byte that closes the "shortcuts" map; the new entry goes right before it.</param>
    private sealed record ShortcutsLayout(int InsertAt, IReadOnlyList<ParsedEntry> Entries, int NextIndex);

    /// <summary>
    /// Walks the whole file and returns where a new entry goes, or null when anything is not exactly as expected (the
    /// caller then leaves the file untouched). Only the layout is read; the original bytes are never re-serialized.
    /// </summary>
    private static ShortcutsLayout? TryParse(byte[] data)
    {
        try
        {
            int position = 0;
            ShortcutsLayout? found = null;
            while (position < data.Length)
            {
                byte type = data[position];
                if (type == TypeEnd) { position++; continue; }
                if (type != TypeMap) return null;
                position++;
                string key = ReadCString(data, ref position);
                if (string.Equals(key, "shortcuts", StringComparison.OrdinalIgnoreCase))
                {
                    if (found != null) return null;
                    found = ReadShortcutsMap(data, ref position);
                    if (found == null) return null;
                }
                else
                {
                    if (!SkipMap(data, ref position, 1)) return null;
                }
            }
            return found;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException) { return null; }
    }

    private static ShortcutsLayout? ReadShortcutsMap(byte[] data, ref int position)
    {
        var entries = new List<ParsedEntry>();
        int next = 0;
        while (true)
        {
            if (position >= data.Length) return null;
            byte type = data[position];
            if (type == TypeEnd) return new ShortcutsLayout(position, entries, next);
            if (type != TypeMap) return null;
            position++;
            string index = ReadCString(data, ref position);
            if (int.TryParse(index, out int number) && number >= next) next = number + 1;
            string? name = null, exe = null, options = null;
            if (!ReadEntry(data, ref position, (field, value) =>
                {
                    if (string.Equals(field, "AppName", StringComparison.OrdinalIgnoreCase)) name = value;
                    else if (string.Equals(field, "Exe", StringComparison.OrdinalIgnoreCase)) exe = value;
                    else if (string.Equals(field, "LaunchOptions", StringComparison.OrdinalIgnoreCase)) options = value;
                })) return null;
            entries.Add(new ParsedEntry(index, name, exe, options));
        }
    }

    /// <summary>Reads one entry's fields up to and including its closing byte. False when a type is unknown.</summary>
    private static bool ReadEntry(byte[] data, ref int position, Action<string, string?> onString)
    {
        while (true)
        {
            if (position >= data.Length) return false;
            byte type = data[position++];
            if (type == TypeEnd) return true;
            string key = ReadCString(data, ref position);
            switch (type)
            {
                case TypeString: onString(key, ReadCString(data, ref position)); break;
                case TypeMap: if (!SkipMap(data, ref position, 2)) return false; break;
                default: if (!SkipValue(data, ref position, type)) return false; break;
            }
        }
    }

    private static bool SkipMap(byte[] data, ref int position, int depth)
    {
        if (depth > MaxDepth) return false;
        while (true)
        {
            if (position >= data.Length) return false;
            byte type = data[position++];
            if (type == TypeEnd) return true;
            ReadCString(data, ref position);
            if (type == TypeMap) { if (!SkipMap(data, ref position, depth + 1)) return false; }
            else if (!SkipValue(data, ref position, type)) return false;
        }
    }

    private static bool SkipValue(byte[] data, ref int position, byte type)
    {
        switch (type)
        {
            case TypeString: ReadCString(data, ref position); return true;
            case TypeWideString:
                while (position + 1 < data.Length && !(data[position] == 0 && data[position + 1] == 0)) position += 2;
                if (position + 1 >= data.Length) return false;
                position += 2;
                return true;
            case TypeInt32: case TypeFloat: case TypePointer: case TypeColor: return Advance(data, ref position, 4);
            case TypeUInt64: case TypeInt64: return Advance(data, ref position, 8);
            default: return false;
        }
    }

    private static bool Advance(byte[] data, ref int position, int count)
    {
        if (position + count > data.Length) return false;
        position += count;
        return true;
    }

    private static string ReadCString(byte[] data, ref int position)
    {
        int end = Array.IndexOf(data, (byte)0, position);
        if (end < 0) throw new InvalidDataException("Unterminated string.");
        string text = Encoding.UTF8.GetString(data, position, end - position);
        position = end + 1;
        return text;
    }

    private static byte[] InsertEntry(byte[] original, ShortcutsLayout layout, SteamShortcutEntry entry, uint appId)
    {
        byte[] block = BuildEntry(entry, appId, layout.NextIndex.ToString());
        var result = new byte[original.Length + block.Length];
        Buffer.BlockCopy(original, 0, result, 0, layout.InsertAt);
        Buffer.BlockCopy(block, 0, result, layout.InsertAt, block.Length);
        Buffer.BlockCopy(original, layout.InsertAt, result, layout.InsertAt + block.Length, original.Length - layout.InsertAt);
        return result;
    }

    private static byte[] BuildNewFile(SteamShortcutEntry entry, uint appId)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(TypeMap);
        WriteCString(stream, "shortcuts");
        byte[] block = BuildEntry(entry, appId, "0");
        stream.Write(block, 0, block.Length);
        stream.WriteByte(TypeEnd); // closes "shortcuts"
        stream.WriteByte(TypeEnd); // closes the file
        return stream.ToArray();
    }

    private static byte[] BuildEntry(SteamShortcutEntry entry, uint appId, string index)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(TypeMap);
        WriteCString(stream, index);
        WriteInt(stream, "appid", unchecked((int)appId));
        WriteString(stream, "AppName", entry.AppName);
        WriteString(stream, "Exe", entry.ExecutablePath);
        WriteString(stream, "StartDir", entry.StartDirectory);
        WriteString(stream, "icon", entry.IconPath);
        WriteString(stream, "ShortcutPath", string.Empty);
        WriteString(stream, "LaunchOptions", entry.LaunchOptions);
        WriteInt(stream, "IsHidden", 0);
        WriteInt(stream, "AllowDesktopConfig", 1);
        WriteInt(stream, "AllowOverlay", 1);
        WriteInt(stream, "OpenVR", 0);
        WriteInt(stream, "Devkit", 0);
        WriteString(stream, "DevkitGameID", string.Empty);
        WriteInt(stream, "DevkitOverrideAppID", 0);
        WriteInt(stream, "LastPlayTime", 0);
        WriteString(stream, "FlatpakAppID", string.Empty);
        stream.WriteByte(TypeMap);
        WriteCString(stream, "tags");
        stream.WriteByte(TypeEnd);
        stream.WriteByte(TypeEnd);
        return stream.ToArray();
    }

    private static void WriteString(Stream stream, string key, string value)
    {
        stream.WriteByte(TypeString);
        WriteCString(stream, key);
        WriteCString(stream, value);
    }

    private static void WriteInt(Stream stream, string key, int value)
    {
        stream.WriteByte(TypeInt32);
        WriteCString(stream, key);
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteCString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);
    }
}
