using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>System-wide dictation rules (pure logic) and a static guard that the dictation code never installs a keyboard hook.</summary>
public sealed class SystemDictationPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false, true, DictationRefusal.Disabled)]
    [InlineData(true, true, false, false, true, DictationRefusal.Gaming)]
    [InlineData(true, false, false, true, true, DictationRefusal.Busy)]
    [InlineData(true, false, false, false, false, DictationRefusal.NoTarget)]
    [InlineData(true, false, true, false, true, DictationRefusal.OwnWindowActive)]
    [InlineData(true, false, false, false, true, DictationRefusal.None)]
    public void Start_is_allowed_only_when_enabled_outside_gaming_with_another_window_in_front(
        bool enabled, bool gaming, bool own, bool busy, bool target, DictationRefusal expected) =>
        Assert.Equal(expected, SystemDictationPolicy.CanStart(enabled, gaming, own, busy, target));

    [Fact]
    public void Every_refusal_has_a_message()
    {
        foreach (DictationRefusal r in Enum.GetValues<DictationRefusal>().Where(r => r != DictationRefusal.None))
            Assert.NotEmpty(SystemDictationPolicy.Describe(r));
    }

    [Fact]
    public void Text_never_contains_control_characters_so_it_cannot_press_enter_tab_or_a_shortcut()
    {
        string t = SystemDictationPolicy.PrepareText("hello\r\nworld\tnext\u001b[31m  line‮evil\0end", false);
        Assert.Equal("hello world next [31m lineevil end", t);
        Assert.DoesNotContain(t, c => char.IsControl(c));
        Assert.Equal(string.Empty, SystemDictationPolicy.PrepareText("   \n\t ", false));
        Assert.Equal(string.Empty, SystemDictationPolicy.PrepareText(null, true));
    }

    [Fact]
    public void Fillers_are_removed_only_when_asked_and_text_is_bounded()
    {
        Assert.Equal("Hello there", SystemDictationPolicy.PrepareText("Um, hello there", true));
        Assert.Equal("Um, hello there", SystemDictationPolicy.PrepareText("Um, hello there", false));
        string big = SystemDictationPolicy.PrepareText(new string('a', 10_000), false);
        Assert.Equal(SystemDictationPolicy.MaxInsertCharacters, big.Length);
    }

    [Fact]
    public void Batches_never_split_a_surrogate_pair_and_reassemble_exactly()
    {
        string text = string.Concat(Enumerable.Repeat("a\U0001F600", 120)); // 3 UTF-16 units per repetition
        IReadOnlyList<string> batches = SystemDictationPolicy.Batches(text, 7);
        Assert.Equal(text, string.Concat(batches));
        Assert.All(batches, b => { Assert.False(char.IsHighSurrogate(b[^1])); Assert.True(b.Length <= 7); });
        Assert.Empty(SystemDictationPolicy.Batches(string.Empty));
    }

    [Fact]
    public void Text_is_typed_only_while_the_original_window_is_still_in_front()
    {
        Assert.True(SystemDictationPolicy.TargetStillActive(1234, 1234));
        Assert.False(SystemDictationPolicy.TargetStillActive(1234, 5678));
        Assert.False(SystemDictationPolicy.TargetStillActive(0, 0));
    }

    [Fact]
    public void The_dictation_sources_install_no_keyboard_hook_and_read_no_key_state()
    {
        string dir = FindServicesDir();
        foreach (string file in new[] { "SystemDictationPolicy.cs", "SystemDictationService.cs" })
        {
            string path = Path.Combine(dir, file);
            Assert.True(File.Exists(path), path);
            string code = File.ReadAllText(path);
            foreach (string forbidden in new[] { "SetWindowsHookEx", "GetAsyncKeyState", "GetKeyState", "GetKeyboardState", "RegisterRawInputDevices", "GetRawInputData", "WH_KEYBOARD" })
                Assert.DoesNotContain(forbidden, code);
        }
        Assert.Contains("RegisterHotKey", File.ReadAllText(Path.Combine(dir, "HotkeyService.cs")));
    }

    private static string FindServicesDir()
    {
        string? d = AppContext.BaseDirectory;
        while (d != null && !Directory.Exists(Path.Combine(d, "src", "PaRuX.App", "Services"))) d = Path.GetDirectoryName(d);
        return Path.Combine(d ?? throw new DirectoryNotFoundException("repository root"), "src", "PaRuX.App", "Services");
    }
}
