using System.Text;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Scripts in languages PaRuX has no dedicated parser for. Every sample here is invented (it only mimics the shape of an in-house BASIC-like
/// scripting language: labels, if/endif, function/endfunction, #declare imports); no real company script is used or stored.
/// </summary>
public sealed class UniversalScriptExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-script-" + Guid.NewGuid().ToString("N"));
    public UniversalScriptExtractorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Write(string name, string text) { string p = Path.Combine(_dir, name); File.WriteAllText(p, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false)); return p; }

    private const string Demo = @"#RtVer 2.0
// Demo diagnostic sequence (invented example)
// Checks the pump and writes a report
Debug ""DEMO.PUMP_CHECK""
#declare HELPER as ScriptImport ( file = ""scripts/Helper.xscr"")
#declare NATIVE as AssemblyImport ( file = ""PumpTools.dll"", class = ""Pump"")
Threshold = 42
Result = false

function ReadPressure(channel)
    value = DEVICE.ReadAnalog(channel)
    if value > Threshold then
        Log.Write(""pressure high"")
        goto Failed
    endif
    return value
endfunction

Start:
    pressure = ReadPressure(3)
    ExecuteScript ""scripts/Cleanup.xscr""
    REPORT.AddLine(""Pressure checked"")
    goto Done

Failed:
    REPORT.AddLine(""Pump failed"")
    Result = false

Done:
    Exit
";

    [Fact]
    public void An_unknown_script_language_is_read_with_definitions_dependencies_and_external_calls()
    {
        string path = Write("PumpCheck.xscr", Demo);
        ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
        Assert.Equal("script", d.Extractor);
        Assert.Equal(DocumentTier.Structured, d.Tier);
        string overview = d.Segments[0].Text;
        Assert.Contains("PumpCheck.xscr", overview);
        Assert.Contains("ReadPressure", overview);                      // function
        Assert.Contains("Failed", overview); Assert.Contains("Done", overview);    // labels
        Assert.Contains("scripts/Helper.xscr", overview);               // #declare ... file=
        Assert.Contains("PumpTools.dll", overview);
        Assert.Contains("scripts/Cleanup.xscr", overview);              // a quoted file name passed to a call
        Assert.Contains("DEVICE.ReadAnalog", overview);                 // external object calls
        Assert.Contains("REPORT.AddLine x2", overview);
        Assert.Contains("Demo diagnostic sequence", overview);          // header comment
    }

    [Fact]
    public void Blocks_are_small_positioned_and_name_the_enclosing_function()
    {
        ExtractedDocument d = StructuredDocumentExtractor.Extract(Write("PumpCheck.xscr", Demo));
        var blocks = d.Segments.Skip(1).ToList();
        Assert.True(blocks.Count >= 3);
        Assert.All(blocks, b => { Assert.True(b.Text.Length <= 900); Assert.Contains("PumpCheck.xscr lines ", b.Text); });
        Segment(blocks, "ReadPressure", "value = DEVICE.ReadAnalog(channel)");
        Assert.Contains(blocks, b => b.Position.Heading!.Contains("lines ") && b.Text.Contains("pressure high"));
    }

    private static void Segment(IEnumerable<DocumentSegment> blocks, string def, string text) =>
        Assert.Contains(blocks, b => b.Text.Contains(text) && b.Text.Contains("in " + def));

    [Theory]
    [InlineData("tool.vbs", "' VBScript\r\nSet fso = CreateObject(\"Scripting.FileSystemObject\")\r\nSub Main()\r\n  WScript.Echo \"x\"\r\nEnd Sub\r\nMain\r\n")]
    [InlineData("calib.m", "% MATLAB script\r\nfunction y = scale(x)\r\n  y = x * 2;\r\nend\r\nresult = scale(4);\r\ndisp(result)\r\n")]
    [InlineData("hotkey.ahk", "; AutoHotkey\r\nf1::\r\n  Send, hello\r\nReturn\r\nMyFunc(a) {\r\n  return a+1\r\n}\r\nx := MyFunc(2)\r\n")]
    [InlineData("seq.can", "variables { int counter = 0; }\r\non start { counter = 1; write(\"hi\"); }\r\non key 'a' { counter++; }\r\n")]
    public void Other_scripting_languages_are_claimed_without_a_dedicated_parser(string file, string text)
    {
        ExtractedDocument d = StructuredDocumentExtractor.Extract(Write(file, text));
        Assert.True(d.Extractor is "script" or "source", file + " -> " + d.Extractor);
        Assert.Equal(DocumentTier.Structured, d.Tier);
        Assert.Contains(file, string.Join(" ", d.Segments.Select(s => s.Text)));
    }

    [Fact]
    public void Prose_and_binary_files_with_an_unknown_extension_are_not_taken_for_scripts()
    {
        string prose = Write("notes.zzz", string.Concat(Enumerable.Repeat("Questa e' una nota di riunione scritta in italiano senza alcun codice. Si parla di budget e di fornitori.\r\n", 40)));
        Assert.NotEqual("script", StructuredDocumentExtractor.Extract(prose).Extractor);
        string bin = Path.Combine(_dir, "blob.zzz"); File.WriteAllBytes(bin, Enumerable.Range(0, 4000).Select(i => (byte)(i * 31 % 256)).ToArray());
        Assert.NotEqual("script", StructuredDocumentExtractor.Extract(bin).Extractor);
    }

    [Fact]
    public void A_dependency_can_be_found_by_its_name_so_the_question_who_uses_this_file_has_an_answer()
    {
        string path = Write("Main.xscr", Demo);
        ExtractedDocument d = StructuredDocumentExtractor.Extract(path);
        Assert.Contains(d.Segments, s => s.Text.Contains("Helper.xscr") && s.Text.Contains("Main.xscr"));
    }

    /// <summary>
    /// Local probe over a folder of real scripts of an in-house application, supplied by the developer through PARUX_PROBE_SCRIPTS (never in the repo):
    /// every file must be read, and what was extracted is written to PARUX_CAN_PROBE_OUT for inspection. Skipped when the variable is not set.
    /// </summary>
    [Fact]
    public void Real_scripts_of_a_local_folder_are_all_indexed_when_a_probe_folder_is_given()
    {
        string? folder = Environment.GetEnvironmentVariable("PARUX_PROBE_SCRIPTS");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".eslx", StringComparison.OrdinalIgnoreCase)).Take(60).ToList();
        var report = new StringBuilder();
        int claimed = 0;
        foreach (string f in files)
        {
            ExtractedDocument d = StructuredDocumentExtractor.Extract(f);
            if (d.Extractor == "script" && d.Tier == DocumentTier.Structured) claimed++;
            report.AppendLine($"{Path.GetFileName(f)}: {d.Extractor}/{d.Tier} segments={d.Segments.Count} note={d.Note}");
        }
        string outDir = Environment.GetEnvironmentVariable("PARUX_CAN_PROBE_OUT") ?? "";
        if (outDir.Length > 0)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "script-probe.txt"), report.ToString());
            if (files.Count > 0)
            {
                ExtractedDocument big = StructuredDocumentExtractor.Extract(files.OrderByDescending(f => new FileInfo(f).Length).First());
                File.WriteAllText(Path.Combine(outDir, "script-probe-overview.txt"), string.Join("\n-----\n", big.Segments.Take(4).Select(s => "[" + s.Position.Describe() + "]\n" + s.Text)));
            }
        }
        Assert.True(files.Count == 0 || claimed >= files.Count * 0.9, $"only {claimed} of {files.Count} scripts were read as scripts");
    }

    [Fact]
    public void Utf16_scripts_with_a_byte_order_mark_are_read()
    {
        string p = Path.Combine(_dir, "wide.xscr");
        File.WriteAllText(p, Demo.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UnicodeEncoding(false, true));
        ExtractedDocument d = StructuredDocumentExtractor.Extract(p);
        Assert.Equal("script", d.Extractor);
        Assert.Contains("ReadPressure", d.Segments[0].Text);
    }
}
