using System.Text;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Desktop and Steam non-Steam shortcuts for library games, and the "--play" hand-off they use. Everything runs in
/// temporary folders with synthetic content; nothing touches the real desktop or a real Steam installation.
/// </summary>
public sealed class GameShortcutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-shortcut-" + Guid.NewGuid().ToString("N"));

    public GameShortcutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string FakeExe()
    {
        string path = Path.Combine(_dir, "app", "PaRuX.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A });
        return path;
    }

    // ------------------------------------------------------------------ title keys and the command line

    [Theory]
    [InlineData("abc123", true)]
    [InlineData("A1b2C3", true)]
    [InlineData("id:steam:12345", true)]
    [InlineData("id:manual:ab-cd_ef.gh", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("id:", false)]
    [InlineData("has space", false)]
    [InlineData("quote\"inside", false)]
    [InlineData("path\\..\\x", false)]
    [InlineData("a&b", false)]
    [InlineData("id:bad key", false)]
    public void Title_key_rule_matches_the_core_rule(string key, bool expected)
    {
        Assert.Equal(expected, GameLaunchRequestChannel.IsValidTitleKey(key));
        // The Core and the shortcut channel must never disagree about which keys exist.
        Assert.Equal(GameTitleEndpoints.ValidTitleKey(key), GameLaunchRequestChannel.IsValidTitleKey(key));
    }

    [Fact]
    public void Title_key_longer_than_the_limit_is_rejected()
    {
        Assert.False(GameLaunchRequestChannel.IsValidTitleKey(new string('a', GameLaunchRequestChannel.MaxTitleKeyLength + 1)));
        Assert.True(GameLaunchRequestChannel.IsValidTitleKey(new string('a', GameLaunchRequestChannel.MaxTitleKeyLength)));
    }

    [Fact]
    public void Arguments_for_a_key_round_trip_through_the_command_line_parser()
    {
        string arguments = GameLaunchRequestChannel.ArgumentsFor("id:steam:620");
        Assert.Equal("--play id:steam:620", arguments);
        var commandLine = new List<string> { "PaRuX.exe" };
        commandLine.AddRange(arguments.Split(' '));
        Assert.True(GameLaunchRequestChannel.TryParseCommandLine(commandLine, out string key));
        Assert.Equal("id:steam:620", key);
        Assert.Throws<ArgumentException>(() => GameLaunchRequestChannel.ArgumentsFor("not valid"));
    }

    [Theory]
    [InlineData("PaRuX.exe")]
    [InlineData("PaRuX.exe --exit")]
    [InlineData("PaRuX.exe --play")]
    [InlineData("PaRuX.exe --play C:\\evil.exe")]
    [InlineData("PaRuX.exe --play a;b")]
    [InlineData("--play abc")] // the first element is the program name, never an argument
    public void Command_line_without_a_valid_play_request_is_not_a_request(string line)
    {
        Assert.False(GameLaunchRequestChannel.TryParseCommandLine(line.Split(' '), out string key));
        Assert.Equal(string.Empty, key);
        Assert.False(GameLaunchRequestChannel.TryParseCommandLine(null, out _));
    }

    [Fact]
    public void Play_argument_is_case_insensitive_and_found_anywhere_after_the_program_name()
    {
        Assert.True(GameLaunchRequestChannel.TryParseCommandLine(new[] { "PaRuX.exe", "--other", "--PLAY", "key1" }, out string key));
        Assert.Equal("key1", key);
    }

    // ------------------------------------------------------------------ the queue

    [Fact]
    public void Queue_hands_over_requests_in_order_once_and_removes_them()
    {
        Assert.True(GameLaunchRequestChannel.TryEnqueue(_dir, "first"));
        Assert.True(GameLaunchRequestChannel.TryEnqueue(_dir, "second"));
        Assert.True(GameLaunchRequestChannel.TryEnqueue(_dir, "first")); // duplicate

        IReadOnlyList<string> keys = GameLaunchRequestChannel.Drain(_dir);

        Assert.Equal(new[] { "first", "second" }, keys);
        Assert.Empty(GameLaunchRequestChannel.Drain(_dir));
        Assert.Empty(Directory.GetFiles(GameLaunchRequestChannel.QueueDirectoryFor(_dir)));
    }

    [Fact]
    public void Queue_refuses_an_invalid_key_and_never_writes_it()
    {
        Assert.False(GameLaunchRequestChannel.TryEnqueue(_dir, "a b"));
        Assert.False(Directory.Exists(GameLaunchRequestChannel.QueueDirectoryFor(_dir)));
    }

    [Fact]
    public void Queue_ignores_stale_tampered_and_oversized_files_and_still_deletes_them()
    {
        string queue = GameLaunchRequestChannel.QueueDirectoryFor(_dir);
        Directory.CreateDirectory(queue);
        string stale = Path.Combine(queue, "1-stale.play");
        File.WriteAllText(stale, "oldgame");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - GameLaunchRequestChannel.MaxRequestAge - TimeSpan.FromMinutes(1));
        File.WriteAllText(Path.Combine(queue, "2-bad.play"), "C:\\Windows\\system32\\cmd.exe /c calc");
        File.WriteAllText(Path.Combine(queue, "3-huge.play"), new string('a', 5000));
        File.WriteAllText(Path.Combine(queue, "4-ok.play"), "goodgame\r\n");

        IReadOnlyList<string> keys = GameLaunchRequestChannel.Drain(_dir);

        Assert.Equal(new[] { "goodgame" }, keys);
        Assert.Empty(Directory.GetFiles(queue));
    }

    [Fact]
    public void Queue_is_bounded()
    {
        for (int i = 0; i < GameLaunchRequestChannel.MaxQueuedRequests; i++)
            Assert.True(GameLaunchRequestChannel.TryEnqueue(_dir, "game" + i));
        Assert.False(GameLaunchRequestChannel.TryEnqueue(_dir, "overflow"));
        Assert.Equal(GameLaunchRequestChannel.MaxQueuedRequests, GameLaunchRequestChannel.Drain(_dir).Count);
    }

    [Fact]
    public async Task A_running_instance_is_woken_for_every_request_not_just_the_first()
    {
        string root = Path.Combine(_dir, "root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var woken = new SemaphoreSlim(0);
        using IDisposable listener = GameLaunchRequestChannel.Listen(root, () => woken.Release());

        Assert.True(GameLaunchRequestChannel.TrySubmit(root, "one", out bool first));
        Assert.True(first);
        Assert.True(await woken.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { "one" }, GameLaunchRequestChannel.Drain(root));

        Assert.True(GameLaunchRequestChannel.TrySubmit(root, "two", out bool second));
        Assert.True(second);
        Assert.True(await woken.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { "two" }, GameLaunchRequestChannel.Drain(root));
    }

    [Fact]
    public void Submitting_with_no_running_instance_still_queues_for_the_next_start()
    {
        string root = Path.Combine(_dir, "root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Assert.True(GameLaunchRequestChannel.TrySubmit(root, "later", out bool notified));
        Assert.False(notified);
        Assert.Equal(new[] { "later" }, GameLaunchRequestChannel.Drain(root));
    }

    [Fact]
    public void Event_names_differ_per_data_root()
    {
        string a = GameLaunchRequestChannel.EventNameFor(Path.Combine(_dir, "a"));
        string b = GameLaunchRequestChannel.EventNameFor(Path.Combine(_dir, "b"));
        Assert.NotEqual(a, b);
        Assert.Equal(a, GameLaunchRequestChannel.EventNameFor(Path.Combine(_dir, "a") + Path.DirectorySeparatorChar));
        Assert.NotEqual(AppExitSignal.NameFor(Path.Combine(_dir, "a")), a);
    }

    // ------------------------------------------------------------------ desktop shortcuts

    [Theory]
    [InlineData("Hades", "Hades")]
    [InlineData("  Half-Life: Alyx?  ", "Half-Life Alyx")]
    [InlineData("a/b\\c*d", "a b c d")]
    [InlineData("Dots...", "Dots")]
    [InlineData("", "Game")]
    [InlineData("???", "Game")]
    [InlineData("CON", "CON (PaRuX)")]
    public void Shortcut_file_names_are_safe(string input, string expected)
    {
        Assert.Equal(expected, GameShortcuts.SafeFileName(input));
    }

    [Fact]
    public void Shortcut_file_name_is_length_limited()
    {
        Assert.True(GameShortcuts.SafeFileName(new string('x', 500)).Length <= 80);
    }

    [Fact]
    public void Desktop_shortcut_starts_paRuX_with_the_play_request_and_is_readable_back()
    {
        string exe = FakeExe();
        string desktop = Path.Combine(_dir, "desktop");
        Directory.CreateDirectory(desktop);

        GameShortcutResult result = GameShortcuts.CreateInFolder(desktop, "Hades: Redux", "id:steam:1145360", exe);

        Assert.Equal(GameShortcutOutcome.Created, result.Outcome);
        Assert.Equal(Path.Combine(desktop, "Hades Redux.lnk"), result.Path);
        ShortcutTarget? read = WindowsShortcutFile.TryRead(result.Path!);
        Assert.NotNull(read);
        Assert.Equal(exe, read!.TargetPath, ignoreCase: true);
        Assert.Equal("--play id:steam:1145360", read.Arguments);
        Assert.Equal(Path.GetDirectoryName(exe)!, read.WorkingDirectory, ignoreCase: true);
        Assert.Contains("Hades: Redux", read.Description);
        Assert.Equal(exe, read.IconPath, ignoreCase: true);
    }

    [Fact]
    public void Desktop_shortcut_uses_the_games_own_icon_when_it_exists()
    {
        string exe = FakeExe();
        string gameExe = Path.Combine(_dir, "game", "game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(gameExe)!);
        File.WriteAllBytes(gameExe, new byte[] { 0x4D, 0x5A });
        string desktop = Path.Combine(_dir, "desktop");
        Directory.CreateDirectory(desktop);

        GameShortcutResult result = GameShortcuts.CreateInFolder(desktop, "G", "gkey", exe, gameExe);

        Assert.Equal(gameExe, WindowsShortcutFile.TryRead(result.Path!)!.IconPath, ignoreCase: true);
        // The target is still PaRuX, never the game: the game starts through PaRuX.
        Assert.Equal(exe, WindowsShortcutFile.TryRead(result.Path!)!.TargetPath, ignoreCase: true);
    }

    [Fact]
    public void An_identical_shortcut_is_left_alone()
    {
        string exe = FakeExe();
        string desktop = Path.Combine(_dir, "desktop");
        Directory.CreateDirectory(desktop);
        GameShortcutResult first = GameShortcuts.CreateInFolder(desktop, "Game", "gkey", exe);
        DateTime stamp = File.GetLastWriteTimeUtc(first.Path!);

        GameShortcutResult second = GameShortcuts.CreateInFolder(desktop, "Game", "gkey", exe);

        Assert.Equal(GameShortcutOutcome.AlreadyExists, second.Outcome);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(first.Path!));
        Assert.Single(Directory.GetFiles(desktop));
    }

    [Fact]
    public void A_file_someone_else_made_is_never_overwritten()
    {
        string exe = FakeExe();
        string desktop = Path.Combine(_dir, "desktop");
        Directory.CreateDirectory(desktop);
        string theirs = Path.Combine(desktop, "Game.lnk");
        byte[] theirBytes = Encoding.UTF8.GetBytes("this is not a shell link, but it is the user's file");
        File.WriteAllBytes(theirs, theirBytes);

        GameShortcutResult result = GameShortcuts.CreateInFolder(desktop, "Game", "gkey", exe);

        Assert.Equal(GameShortcutOutcome.Created, result.Outcome);
        Assert.Equal(Path.Combine(desktop, "Game (PaRuX).lnk"), result.Path);
        Assert.Equal(theirBytes, File.ReadAllBytes(theirs));
    }

    [Fact]
    public void A_shortcut_for_another_game_with_the_same_name_gets_its_own_file()
    {
        string exe = FakeExe();
        string desktop = Path.Combine(_dir, "desktop");
        Directory.CreateDirectory(desktop);
        GameShortcutResult first = GameShortcuts.CreateInFolder(desktop, "Game", "keyone", exe);
        GameShortcutResult second = GameShortcuts.CreateInFolder(desktop, "Game", "keytwo", exe);

        Assert.Equal(GameShortcutOutcome.Created, second.Outcome);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal("--play keyone", WindowsShortcutFile.TryRead(first.Path!)!.Arguments);
        Assert.Equal("--play keytwo", WindowsShortcutFile.TryRead(second.Path!)!.Arguments);
    }

    [Fact]
    public void Shortcut_requests_that_cannot_be_honoured_fail_without_writing()
    {
        string exe = FakeExe();
        string desktop = Path.Combine(_dir, "desktop");
        Directory.CreateDirectory(desktop);

        Assert.Equal(GameShortcutOutcome.InvalidRequest, GameShortcuts.CreateInFolder(desktop, "G", "bad key", exe).Outcome);
        Assert.Equal(GameShortcutOutcome.InvalidRequest, GameShortcuts.CreateInFolder(desktop, "G", "gkey", Path.Combine(_dir, "missing.exe")).Outcome);
        Assert.Equal(GameShortcutOutcome.InvalidRequest, GameShortcuts.CreateInFolder(desktop, "G", "gkey", "relative.exe").Outcome);
        Assert.Equal(GameShortcutOutcome.FolderUnavailable, GameShortcuts.CreateInFolder(Path.Combine(_dir, "nope"), "G", "gkey", exe).Outcome);
        Assert.Empty(Directory.GetFiles(desktop));
    }

    [Fact]
    public void Reading_something_that_is_not_a_shortcut_returns_null()
    {
        string path = Path.Combine(_dir, "junk.lnk");
        File.WriteAllText(path, "junk");
        Assert.Null(WindowsShortcutFile.TryRead(path));
        Assert.Null(WindowsShortcutFile.TryRead(Path.Combine(_dir, "absent.lnk")));
    }

    // ------------------------------------------------------------------ Steam non-Steam shortcuts

    private static readonly Func<bool> SteamClosed = () => false;

    private static byte[] Cstr(string s) => Encoding.UTF8.GetBytes(s).Append((byte)0).ToArray();

    private static byte[] ExistingEntry(string index, string name, string exe, string options, bool extraTypes)
    {
        using var m = new MemoryStream();
        void Str(string k, string v) { m.WriteByte(1); m.Write(Cstr(k)); m.Write(Cstr(v)); }
        void Int(string k, int v) { m.WriteByte(2); m.Write(Cstr(k)); m.Write(BitConverter.GetBytes(v)); }
        m.WriteByte(0); m.Write(Cstr(index));
        Int("appid", -1234567);
        Str("AppName", name);
        Str("Exe", exe);
        Str("StartDir", "\"C:\\Games\\\"");
        Str("LaunchOptions", options);
        Int("IsHidden", 0);
        if (extraTypes)
        {
            m.WriteByte(7); m.Write(Cstr("U64")); m.Write(BitConverter.GetBytes(123456789012345UL));
            m.WriteByte(3); m.Write(Cstr("Flt")); m.Write(BitConverter.GetBytes(1.5f));
            m.WriteByte(0); m.Write(Cstr("tags")); m.WriteByte(1); m.Write(Cstr("0")); m.Write(Cstr("favorite")); m.WriteByte(8);
        }
        m.WriteByte(8);
        return m.ToArray();
    }

    private static byte[] ShortcutsFile(params byte[][] entries)
    {
        using var m = new MemoryStream();
        m.WriteByte(0); m.Write(Cstr("shortcuts"));
        foreach (byte[] e in entries) m.Write(e);
        m.WriteByte(8); m.WriteByte(8);
        return m.ToArray();
    }

    private string SteamConfig(out string vdf)
    {
        string config = Path.Combine(_dir, "steam", "userdata", "1001", "config");
        Directory.CreateDirectory(config);
        vdf = Path.Combine(config, "shortcuts.vdf");
        return config;
    }

    private SteamShortcutEntry Entry(string name = "Hades", string key = "gkey") =>
        SteamShortcutsFile.EntryFor(name, key, FakeExe());

    [Fact]
    public void Steam_app_id_is_a_crc32_with_the_top_bit_set()
    {
        Assert.Equal(0xCBF43926u, SteamShortcutsFile.AppIdFor("123456", "789"));   // CRC-32 check value of "123456789"
        Assert.Equal(0xB52441C2u, SteamShortcutsFile.AppIdFor("a", "bc"));          // crc32("abc") = 0x352441C2, top bit set
        Assert.Equal(0x80000000u, SteamShortcutsFile.AppIdFor("", ""));
        Assert.NotEqual(SteamShortcutsFile.AppIdFor("\"x\"", "A"), SteamShortcutsFile.AppIdFor("\"x\"", "B"));
    }

    [Fact]
    public void Steam_entry_quotes_the_paths_and_starts_paRuX_with_the_play_request()
    {
        SteamShortcutEntry entry = Entry("Hades", "id:steam:7");
        string exe = FakeExe();
        Assert.Equal("\"" + exe + "\"", entry.ExecutablePath);
        Assert.Equal("\"" + Path.GetDirectoryName(exe) + "\"", entry.StartDirectory);
        Assert.Equal("--play id:steam:7", entry.LaunchOptions);
        Assert.Equal("Hades", entry.AppName);
    }

    [Fact]
    public void A_new_shortcuts_file_is_created_when_the_account_has_none_and_a_second_add_is_a_no_op()
    {
        SteamConfig(out string vdf);

        SteamShortcutResult first = SteamShortcutsFile.TryAppend(vdf, Entry(), SteamClosed);
        Assert.Equal(SteamShortcutOutcome.Added, first.Outcome);
        Assert.Null(first.BackupPath);
        byte[] created = File.ReadAllBytes(vdf);
        Assert.Equal(0, created[0]);
        Assert.StartsWith("\0shortcuts\0\0" + "0\0", Encoding.UTF8.GetString(created), StringComparison.Ordinal);
        Assert.Contains("AppName\0Hades\0", Encoding.UTF8.GetString(created));
        Assert.Contains("LaunchOptions\0--play gkey\0", Encoding.UTF8.GetString(created));
        Assert.Equal(new byte[] { 8, 8 }, created[^2..]);

        SteamShortcutResult second = SteamShortcutsFile.TryAppend(vdf, Entry(), SteamClosed);
        Assert.Equal(SteamShortcutOutcome.AlreadyPresent, second.Outcome);
        Assert.Equal(created, File.ReadAllBytes(vdf));
    }

    [Fact]
    public void Existing_entries_are_kept_byte_for_byte_and_the_new_one_is_appended_after_a_backup()
    {
        SteamConfig(out string vdf);
        byte[] original = ShortcutsFile(
            ExistingEntry("0", "My Tool", "\"C:\\Tools\\tool.exe\"", "", extraTypes: true),
            ExistingEntry("1", "Other", "\"C:\\Games\\other.exe\"", "-windowed", extraTypes: false));
        File.WriteAllBytes(vdf, original);

        SteamShortcutResult result = SteamShortcutsFile.TryAppend(vdf, Entry(), SteamClosed, new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(SteamShortcutOutcome.Added, result.Outcome);
        Assert.NotNull(result.BackupPath);
        Assert.EndsWith(".parux-backup-20261121100000", result.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(result.BackupPath!));

        byte[] updated = File.ReadAllBytes(vdf);
        int insertAt = original.Length - 2;
        Assert.Equal(original[..insertAt], updated[..insertAt]);                      // nothing before the insert point changed
        Assert.Equal(original[insertAt..], updated[^2..]);                            // nor the closing bytes
        string added = Encoding.UTF8.GetString(updated[insertAt..^2]);
        Assert.StartsWith("\0" + "2\0", added, StringComparison.Ordinal);                                       // next free index
        Assert.Contains("AppName\0Hades\0", added);
        Assert.Contains("LaunchOptions\0--play gkey\0", added);
        Assert.Equal(1, Directory.GetFiles(Path.GetDirectoryName(vdf)!, "*.parux-tmp").Length + 1); // no temp file left behind
    }

    [Fact]
    public void Next_index_follows_the_highest_existing_index()
    {
        SteamConfig(out string vdf);
        File.WriteAllBytes(vdf, ShortcutsFile(
            ExistingEntry("0", "A", "\"C:\\a.exe\"", "", false),
            ExistingEntry("5", "B", "\"C:\\b.exe\"", "", false)));

        Assert.Equal(SteamShortcutOutcome.Added, SteamShortcutsFile.TryAppend(vdf, Entry(), SteamClosed).Outcome);

        Assert.Contains("\0" + "6\0", Encoding.UTF8.GetString(File.ReadAllBytes(vdf)));
    }

    [Fact]
    public void The_same_game_with_another_launch_request_is_a_different_entry()
    {
        SteamConfig(out string vdf);
        Assert.Equal(SteamShortcutOutcome.Added, SteamShortcutsFile.TryAppend(vdf, Entry("Same", "keyone"), SteamClosed).Outcome);
        Assert.Equal(SteamShortcutOutcome.Added, SteamShortcutsFile.TryAppend(vdf, Entry("Same", "keytwo"), SteamClosed).Outcome);
        Assert.Equal(SteamShortcutOutcome.AlreadyPresent, SteamShortcutsFile.TryAppend(vdf, Entry("Same", "keytwo"), SteamClosed).Outcome);
    }

    [Fact]
    public void While_steam_is_running_nothing_is_written_and_no_backup_is_made()
    {
        SteamConfig(out string vdf);
        byte[] original = ShortcutsFile(ExistingEntry("0", "A", "\"C:\\a.exe\"", "", false));
        File.WriteAllBytes(vdf, original);

        SteamShortcutResult result = SteamShortcutsFile.TryAppend(vdf, Entry(), () => true);

        Assert.Equal(SteamShortcutOutcome.SteamRunning, result.Outcome);
        Assert.Contains("Close Steam", result.Message);
        Assert.Equal(original, File.ReadAllBytes(vdf));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(vdf)!));
    }

    public static IEnumerable<object[]> UnreadableFiles()
    {
        byte[] good = ShortcutsFile(ExistingEntry("0", "A", "\"C:\\a.exe\"", "", false));
        yield return new object[] { good[..(good.Length - 5)] };                      // truncated
        yield return new object[] { Encoding.UTF8.GetBytes("not a binary vdf at all") };
        yield return new object[] { new byte[] { 0, (byte)'x', 0, 0x77, (byte)'k', 0, 1, 2, 3, 8, 8 } }; // unknown value type
        yield return new object[] { new byte[] { 0, (byte)'o', (byte)'t', (byte)'h', (byte)'e', (byte)'r', 0, 8 } }; // no shortcuts map
        yield return new object[] { new byte[] { 1, 2, 3 } };
    }

    [Theory]
    [MemberData(nameof(UnreadableFiles))]
    public void A_file_that_cannot_be_read_exactly_is_left_untouched(byte[] content)
    {
        SteamConfig(out string vdf);
        File.WriteAllBytes(vdf, content);

        SteamShortcutResult result = SteamShortcutsFile.TryAppend(vdf, Entry(), SteamClosed);

        Assert.Equal(SteamShortcutOutcome.UnreadableFile, result.Outcome);
        Assert.Equal(content, File.ReadAllBytes(vdf));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(vdf)!)); // no backup, no temp
    }

    [Fact]
    public void Requests_for_other_files_or_missing_accounts_are_refused()
    {
        SteamConfig(out string vdf);
        string other = Path.Combine(Path.GetDirectoryName(vdf)!, "localconfig.vdf");
        Assert.Equal(SteamShortcutOutcome.InvalidRequest, SteamShortcutsFile.TryAppend(other, Entry(), SteamClosed).Outcome);
        Assert.Equal(SteamShortcutOutcome.InvalidRequest, SteamShortcutsFile.TryAppend("shortcuts.vdf", Entry(), SteamClosed).Outcome);
        string missingAccount = Path.Combine(_dir, "steam", "userdata", "9999", "config", "shortcuts.vdf");
        Assert.Equal(SteamShortcutOutcome.InvalidRequest, SteamShortcutsFile.TryAppend(missingAccount, Entry(), SteamClosed).Outcome);
        Assert.False(Directory.Exists(Path.Combine(_dir, "steam", "userdata", "9999")));
        Assert.Equal(SteamShortcutOutcome.InvalidRequest,
            SteamShortcutsFile.TryAppend(vdf, new SteamShortcutEntry("Bad\0Name", "\"x\"", "", "", ""), SteamClosed).Outcome);
    }

    [Fact]
    public void Steam_accounts_are_the_numeric_folders_most_recent_first()
    {
        string root = Path.Combine(_dir, "steam2");
        string userdata = Path.Combine(root, "userdata");
        Directory.CreateDirectory(Path.Combine(userdata, "111", "config"));
        Directory.CreateDirectory(Path.Combine(userdata, "222", "config"));
        Directory.CreateDirectory(Path.Combine(userdata, "0"));
        Directory.CreateDirectory(Path.Combine(userdata, "anonymous"));
        Directory.SetLastWriteTimeUtc(Path.Combine(userdata, "111", "config"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(Path.Combine(userdata, "222", "config"), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        IReadOnlyList<SteamUserFolder> users = SteamShortcutsFile.FindUsers(root);

        Assert.Equal(new[] { "222", "111" }, users.Select(u => u.AccountId));
        Assert.Empty(SteamShortcutsFile.FindUsers(Path.Combine(_dir, "no-steam")));
    }
}
