using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CorporateTempFilesTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-corptmp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Corporate_scratch_lives_under_the_data_root_and_other_editions_keep_the_os_temp()
    {
        string data = Path.Combine(_work, "PaRuXData");
        string osTemp = Path.Combine(_work, "os-temp");

        Assert.Equal(Path.Combine(data, "tmp"), CorporateTempFiles.ResolveRoot(true, data, osTemp));
        Assert.Equal(osTemp, CorporateTempFiles.ResolveRoot(false, data, osTemp));
    }

    [Fact]
    public void Sweep_creates_the_folder_and_removes_residue_left_by_an_earlier_run()
    {
        string root = Path.Combine(_work, "PaRuXData", "tmp");
        string leftover = Path.Combine(root, "parux-att-deadbeef");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "secret.docx"), "x");
        File.WriteAllText(Path.Combine(root, "parux-backup-stray.zip"), "x");

        CorporateTempFiles.SweepResidue(root);

        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void Sweep_of_a_missing_folder_creates_it_and_never_throws()
    {
        string root = Path.Combine(_work, "fresh", "tmp");
        CorporateTempFiles.SweepResidue(root);
        Assert.True(Directory.Exists(root));
    }

    [Theory]
    [InlineData("Knowledge", "DocumentContainerExtractors.cs")]
    [InlineData("Knowledge", "LegacyOfficeExtractors.cs")]
    [InlineData("", "BackupService.cs")]
    public void Attachment_and_backup_staging_use_the_corporate_aware_root(string subfolder, string file)
    {
        string path = Path.Combine(new[] { RepoRoot(), "src", "PaRuX.Server" }.Concat(subfolder.Length == 0 ? Array.Empty<string>() : new[] { subfolder }).Append(file).ToArray());
        string source = File.ReadAllText(path);
        Assert.DoesNotContain("Path.GetTempPath()", source, StringComparison.Ordinal);
        Assert.Contains("CorporateTempFiles.Root", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}
