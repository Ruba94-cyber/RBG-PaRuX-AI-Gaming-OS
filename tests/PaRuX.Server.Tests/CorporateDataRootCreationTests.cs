using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CorporateDataRootCreationTests
{
    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-corp-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Corporate_marker_without_data_folder_creates_it_beside_the_marker()
    {
        string root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, CorporateMode.MarkerFileName), "");
            string expected = Path.Combine(root, "PaRuXData");
            Assert.Equal(expected, PaRuXDataRoot.FindPortableDataRoot(root));
            Assert.True(Directory.Exists(expected));
            Assert.Equal(expected, PaRuXDataRoot.ResolveDefaultLocation(root, null, null));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Corporate_marker_is_found_from_the_nested_core_folder_too()
    {
        string root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, CorporateMode.MarkerFileName), "");
            string nested = Path.Combine(root, "PaRuX.Server");
            Directory.CreateDirectory(nested);
            Assert.Equal(Path.Combine(root, "PaRuXData"), PaRuXDataRoot.FindPortableDataRoot(nested));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Corporate_refuses_instead_of_falling_back_to_the_user_profile_when_creation_fails()
    {
        string root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, CorporateMode.MarkerFileName), "");
            // A file squatting on the name makes CreateDirectory fail on any machine, admin or not.
            File.WriteAllText(Path.Combine(root, "PaRuXData"), "not a directory");
            var ex = Assert.Throws<CorporateDataRootUnavailableException>(
                () => PaRuXDataRoot.ResolveDefaultLocation(root, null, null));
            Assert.Contains("PaRuXData", ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Standard_portable_marker_still_requires_an_existing_data_folder()
    {
        string root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, PaRuXDataRoot.LegacyStandardPortableMarkerFileName), "");
            Assert.Null(PaRuXDataRoot.FindPortableDataRoot(root));
            Assert.False(Directory.Exists(Path.Combine(root, "PaRuXData")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Desktop_reports_a_refused_corporate_data_root_before_using_it()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PaRuX.sln"))) d = d.Parent;
        Assert.NotNull(d);
        string app = File.ReadAllText(Path.Combine(d!.FullName, "src", "PaRuX.App", "App.xaml.cs"));
        int guard = app.IndexOf("catch (PaRuX.Contracts.CorporateDataRootUnavailableException", StringComparison.Ordinal);
        int first = app.IndexOf("AppExitSignal.NameFor(AppPaths.PaRuXRoot)", StringComparison.Ordinal);
        Assert.True(guard > 0 && first > guard);
    }
}
