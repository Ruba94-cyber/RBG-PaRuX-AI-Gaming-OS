using System;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Pure validation rules for AI game-mod change sets; synthetic strings and bytes only.</summary>
public sealed class GameModFileValidatorTests
{
    internal static byte[] Png(int w, int h, int shade)
    {
        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(255, shade, (x * 7) % 255, (y * 5) % 255));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    [Theory]
    [InlineData("config/Settings.INI", "config/Settings.INI")]
    [InlineData("lang\\en.txt", "lang/en.txt")]
    [InlineData("mods/a/mod.json", "mods/a/mod.json")]
    [InlineData("tex/ui/icon.png", "tex/ui/icon.png")]
    public void Safe_relative_paths_are_normalised(string input, string expected) =>
        Assert.Equal(expected, GameModFileValidator.SafeRelativePath(input));

    [Theory]
    [InlineData("../x.ini")]
    [InlineData("a/../b.ini")]
    [InlineData("a\\..\\b.ini")]
    [InlineData("/etc/x.ini")]
    [InlineData("\\\\server\\share\\x.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("x.ini:stream")]
    [InlineData("x.ini.")]
    [InlineData("a/.parux-lock.ini")]
    [InlineData("CON.ini")]
    [InlineData("a/NUL.txt")]
    [InlineData("a//b.ini")]
    [InlineData("")]
    [InlineData("noextension")]
    [InlineData("a/b?.ini")]
    public void Unsafe_paths_are_refused(string path) =>
        Assert.Throws<ArgumentException>(() => GameModFileValidator.SafeRelativePath(path));

    [Theory]
    [InlineData("Game.exe")]
    [InlineData("lib/native.dll")]
    [InlineData("run.bat")]
    [InlineData("run.cmd")]
    [InlineData("tool.ps1")]
    [InlineData("x.vbs")]
    [InlineData("x.js")]
    [InlineData("x.py")]
    [InlineData("x.msi")]
    [InlineData("x.sys")]
    [InlineData("x.jar")]
    [InlineData("x.lnk")]
    [InlineData("pack.zip")]
    [InlineData("save/slot1.sav")]
    [InlineData("data.bin")]
    public void Executables_scripts_archives_and_saves_are_not_on_the_allow_list(string path) =>
        Assert.Throws<ArgumentException>(() => GameModFileValidator.SafeRelativePath(path));

    [Fact]
    public void Too_deep_or_long_paths_are_refused()
    {
        Assert.Throws<ArgumentException>(() => GameModFileValidator.SafeRelativePath(string.Join('/', Enumerable.Repeat("d", 13)) + "/x.ini"));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.SafeRelativePath(new string('a', 250) + ".ini"));
    }

    [Fact]
    public void Ini_requires_key_value_lines_and_closed_sections()
    {
        GameModFileValidator.ValidateText(GameModFormat.Ini, "[video]\nfov=90\n; note\n", null);
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Ini, "[video\nfov=90", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Ini, "[v]\nthis is prose", null));
    }

    [Fact]
    public void Cfg_keeps_engine_command_lines_valid_for_keybindings()
    {
        GameModFileValidator.ValidateText(GameModFormat.Cfg, "bind \"w\" \"+forward\"\nbind \"space\" \"+jump\"\n", null);
    }

    [Fact]
    public void Json_must_parse_and_keep_its_root_type()
    {
        GameModFileValidator.ValidateText(GameModFormat.Json, "{\"a\":2}", "{\"a\":1}");
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Json, "{broken", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Json, "{\"a\":1} // c", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Json, "[1,2]", "{\"a\":1}"));
    }

    [Fact]
    public void Xml_is_well_formed_without_dtd_or_processing_instructions_and_keeps_its_root()
    {
        GameModFileValidator.ValidateText(GameModFormat.Xml, "<mod><name>x</name></mod>", "<mod><name>y</name></mod>");
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Xml, "<mod><name></mod>", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Xml,
            "<!DOCTYPE m [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><m>&x;</m>", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Xml,
            "<?xml-stylesheet href=\"x\"?><mod/>", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Xml, "<other/>", "<mod/>"));
    }

    [Fact]
    public void Yaml_rejects_tab_indentation_and_type_tags()
    {
        GameModFileValidator.ValidateText(GameModFormat.Yaml, "difficulty:\n  enemies: 2\n- a\n", null);
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Yaml, "a:\n\tb: 1\n", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Yaml, "a: !!python/object/apply:os.system [x]\n", null));
    }

    [Fact]
    public void Toml_checks_tables_values_and_multiline_strings()
    {
        GameModFileValidator.ValidateText(GameModFormat.Toml, "[game]\nname = \"x\"\ntext = \"\"\"\nline\n\"\"\"\n[[mods]]\nid = 1\n", null);
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Toml, "[game\nname=1", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Toml, "[game]\nname =\n", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Toml, "a = \"\"\"never closed\n", null));
    }

    [Theory]
    [InlineData("os.execute('calc')")]
    [InlineData("local f = io.open('x')")]
    [InlineData("require('socket')")]
    [InlineData("dofile('evil.lua')")]
    [InlineData("loadstring('x')()")]
    [InlineData("package.loadlib('a','b')")]
    [InlineData("debug.sethook()")]
    [InlineData("_G.x = 1")]
    public void Lua_cannot_reach_system_file_loader_or_debug_apis(string code) =>
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Lua, code, null));

    [Fact]
    public void Lua_may_change_data_but_not_add_logic()
    {
        const string original = "Config = { speed = 1, name = \"os.execute is only text here\" }\nfunction Tick() return 1 end\n";
        GameModFileValidator.ValidateText(GameModFormat.Lua,
            "Config = { speed = 2, name = \"os.execute is only text here\" } -- os.exit in a comment\nfunction Tick() return 1 end\n", original);
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Lua,
            original + "function Extra() end\n", original));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Lua,
            "while true do end\n", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Lua, "Config = { speed = 2\n", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Lua, "x = \"never closed\n", null));
    }

    [Fact]
    public void Csv_localisation_tables_keep_their_header_row()
    {
        GameModFileValidator.ValidateText(GameModFormat.Csv, "key,text\nhello,Ciao\n", "key,text\nhello,Hello\n");
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Csv, "id,txt\nhello,Ciao\n", "key,text\nhello,Hello\n"));
    }

    [Fact]
    public void Binary_control_oversized_and_empty_text_is_refused()
    {
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Text, "a\0b", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Text, "a\u0007b", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Text, "   \n", null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidateText(GameModFormat.Text, new string('x', 70_000), null));
        Assert.Throws<InvalidDataException>(() => GameModFileValidator.DecodeStrictUtf8([0xFF, 0xFE, 0x00, 0x41]));
    }

    [Fact]
    public void Warnings_report_removed_settings_and_new_files()
    {
        Assert.Equal(["NEW_FILE"], GameModFileValidator.Warnings(GameModFormat.Ini, null, "a=1"));
        Assert.Contains("REMOVES_SETTINGS:1", GameModFileValidator.Warnings(GameModFormat.Ini, "[a]\nx=1\ny=2\n", "[a]\nx=1\n"));
        Assert.Contains("REMOVES_SETTINGS:1", GameModFileValidator.Warnings(GameModFormat.Json, "{\"a\":1,\"b\":2}", "{\"a\":1}"));
        Assert.Empty(GameModFileValidator.Warnings(GameModFormat.Ini, "[a]\nx=1\n", "[a]\nx=2\n"));
    }

    [Fact]
    public void Png_must_be_structurally_sound_same_size_and_free_of_appended_data()
    {
        byte[] original = Png(32, 32, 10);
        Assert.Equal((32, 32), GameModFileValidator.ValidatePng(original, null));
        GameModFileValidator.ValidatePng(Png(32, 32, 200), (32, 32));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidatePng(Png(64, 64, 1), (32, 32)));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidatePng(original.Concat(new byte[] { 1, 2, 3 }).ToArray(), null));
        byte[] corrupt = (byte[])original.Clone();
        corrupt[corrupt.Length / 2] ^= 0xFF;
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidatePng(corrupt, null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidatePng(original[..^12], null));
        Assert.Throws<InvalidDataException>(() => GameModFileValidator.ValidatePng(Encoding.ASCII.GetBytes("MZ not a png at all, just an executable header......"), null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidatePng(Png(8, 8, 1), null));
        Assert.Throws<ArgumentException>(() => GameModFileValidator.ValidatePng(new byte[5 * 1024 * 1024], null));
    }

    [Fact]
    public void Crc32_matches_the_png_reference_value() =>
        Assert.Equal(0xCBF43926u, GameModFileValidator.Crc32("123456789"u8));
}
