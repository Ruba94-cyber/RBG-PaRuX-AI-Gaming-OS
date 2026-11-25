using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Data-root detection for the layered portable packages (PaRuX.exe launcher + app\ + PaRuXData\) and
/// for every older layout that must keep working: flat ZIPs already extracted, MSI and MSIX.
/// </summary>
public sealed class LayeredPortableDataRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-layered-" + Guid.NewGuid().ToString("N"));

    public LayeredPortableDataRootTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Dir(params string[] parts)
    {
        string path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Marker(string directory, string name) => File.WriteAllText(Path.Combine(directory, name), "marker");

    [Fact]
    public void Layered_standard_package_finds_PaRuXData_beside_app_from_the_desktop_the_core_and_the_cli()
    {
        string app = Dir("pkg", "app");
        string data = Dir("pkg", "PaRuXData");
        Marker(app, PaRuXDataRoot.PortableMarkerFileName);

        Assert.Equal(data, PaRuXDataRoot.FindPortableDataRoot(app));                                   // app\PaRuX.exe
        Assert.Equal(data, PaRuXDataRoot.FindPortableDataRoot(Dir("pkg", "app", "PaRuX.Server")));      // Core
        Assert.Equal(data, PaRuXDataRoot.FindPortableDataRoot(Dir("pkg", "app", "Cli")));               // optional CLI
        Assert.Equal(data, PaRuXDataRoot.ResolveDefaultLocation(app, @"C:\somewhere\else", null));      // marker beats inherited override
    }

    [Fact]
    public void Layered_package_without_PaRuXData_is_not_portable_for_the_standard_edition()
    {
        string app = Dir("pkg", "app");
        Marker(app, PaRuXDataRoot.PortableMarkerFileName);

        Assert.Null(PaRuXDataRoot.FindPortableDataRoot(app));
        Assert.False(Directory.Exists(Path.Combine(_root, "pkg", "PaRuXData")));
    }

    [Fact]
    public void Layered_corporate_package_creates_PaRuXData_at_the_package_root_never_inside_app()
    {
        string app = Dir("pkg", "app");
        Marker(app, CorporateMode.MarkerFileName);
        string expected = Path.Combine(_root, "pkg", "PaRuXData");

        Assert.Equal(expected, PaRuXDataRoot.FindPortableDataRoot(Dir("pkg", "app", "PaRuX.Server")));
        Assert.True(Directory.Exists(expected));
        Assert.False(Directory.Exists(Path.Combine(app, "PaRuXData")));
        Assert.Equal(expected, PaRuXDataRoot.FindCorporatePortableDataRoot(app));
        Assert.Equal(expected, PaRuXDataRoot.ResolveDefaultLocation(app, null, null));
    }

    [Fact]
    public void Layered_corporate_package_refuses_to_start_instead_of_falling_back_to_the_profile()
    {
        string app = Dir("pkg", "app");
        Marker(app, CorporateMode.MarkerFileName);
        // A file squatting on the name makes CreateDirectory fail for any user.
        File.WriteAllText(Path.Combine(_root, "pkg", "PaRuXData"), "not a directory");

        var ex = Assert.Throws<CorporateDataRootUnavailableException>(
            () => PaRuXDataRoot.ResolveDefaultLocation(Dir("pkg", "app", "PaRuX.Server"), null, null));
        Assert.Equal(Path.Combine(_root, "pkg", "PaRuXData"), ex.Folder);
    }

    [Fact]
    public void Corporate_mode_is_detected_from_the_desktop_and_from_the_core_in_the_layered_package()
    {
        string app = Dir("pkg", "app");
        Marker(app, CorporateMode.MarkerFileName);

        Assert.True(CorporateMode.Evaluate(null, app));
        Assert.True(CorporateMode.Evaluate(null, Dir("pkg", "app", "PaRuX.Server")));
        Assert.False(CorporateMode.Evaluate(null, Dir("elsewhere", "app")));
    }

    [Fact]
    public void Legacy_flat_zip_layout_keeps_working_for_both_editions()
    {
        // Standard: marker and PaRuXData beside PaRuX.exe, Core one level below.
        string standard = Dir("legacy-standard");
        Dir("legacy-standard", "PaRuXData");
        Marker(standard, PaRuXDataRoot.PortableMarkerFileName);
        Assert.Equal(Path.Combine(standard, "PaRuXData"), PaRuXDataRoot.FindPortableDataRoot(standard));
        Assert.Equal(Path.Combine(standard, "PaRuXData"), PaRuXDataRoot.FindPortableDataRoot(Dir("legacy-standard", "PaRuX.Server")));

        // Corporate: marker beside PaRuX.exe, data folder created beside it.
        string corporate = Dir("legacy-corporate");
        Marker(corporate, CorporateMode.MarkerFileName);
        Assert.Equal(Path.Combine(corporate, "PaRuXData"), PaRuXDataRoot.FindPortableDataRoot(Dir("legacy-corporate", "PaRuX.Server")));
        Assert.True(CorporateMode.Evaluate(null, Dir("legacy-corporate", "PaRuX.Server")));
    }

    [Fact]
    public void Installed_editions_without_markers_do_not_become_portable()
    {
        // MSI: Program Files\PaRuX\PaRuX.exe, Core in PaRuX.Server, no marker; MSIX: same without any data folder.
        string install = Dir("Program Files", "PaRuX");
        Dir("Program Files", "PaRuX", "PaRuX.Server");
        Dir("Program Files", "PaRuX", "PaRuXData"); // even a data-looking folder is ignored without a marker

        Assert.Null(PaRuXDataRoot.FindPortableDataRoot(install));
        Assert.Null(PaRuXDataRoot.FindPortableDataRoot(Path.Combine(install, "PaRuX.Server")));
        Assert.Null(PaRuXDataRoot.FindCorporatePortableDataRoot(install));
        Assert.False(CorporateMode.Evaluate(null, install));
    }

    [Fact]
    public void A_marker_in_a_folder_that_is_not_the_app_folder_does_not_borrow_a_sibling_PaRuXData()
    {
        string other = Dir("pkg", "bin");
        Dir("pkg", "PaRuXData");
        Marker(other, PaRuXDataRoot.PortableMarkerFileName);

        Assert.Null(PaRuXDataRoot.FindPortableDataRoot(other));
    }

    [Fact]
    public void The_search_climbs_at_most_three_levels()
    {
        string app = Dir("pkg", "app");
        string data = Dir("pkg", "PaRuXData");
        Marker(app, PaRuXDataRoot.PortableMarkerFileName);

        Assert.Equal(data, PaRuXDataRoot.FindPortableDataRoot(Dir("pkg", "app", "a", "b", "c")));  // marker 3 levels up
        Assert.Null(PaRuXDataRoot.FindPortableDataRoot(Dir("pkg", "app", "a", "b", "c", "d")));    // 4 levels up: not ours
    }

    [Fact]
    public void Standard_data_inside_the_layered_package_is_found_by_the_pure_resolver_with_a_trailing_separator()
    {
        string app = Dir("pkg", "app");
        string data = Dir("pkg", "PaRuXData");
        Marker(app, PaRuXDataRoot.PortableMarkerFileName);

        Assert.Equal(data, PaRuXDataRoot.FindPortableDataRoot(app + Path.DirectorySeparatorChar));
    }
    [Fact]
    public void Startup_entries_and_shortcuts_target_the_root_launcher_only_in_a_layered_package()
    {
        string app = Dir("pkg", "app");
        Marker(app, PaRuXDataRoot.PortableMarkerFileName);
        File.WriteAllText(Path.Combine(_root, "pkg", "PaRuX.exe"), "launcher");
        File.WriteAllText(Path.Combine(app, "PaRuX.exe"), "app");

        Assert.Equal(Path.Combine(_root, "pkg", "PaRuX.exe"), PaRuXDataRoot.LaunchTargetFor(Path.Combine(app, "PaRuX.exe")));

        // No marker (a folder that merely happens to be called app) or no launcher: the running program itself.
        string plain = Dir("other", "app");
        File.WriteAllText(Path.Combine(_root, "other", "PaRuX.exe"), "x");
        File.WriteAllText(Path.Combine(plain, "PaRuX.exe"), "x");
        Assert.Equal(Path.Combine(plain, "PaRuX.exe"), PaRuXDataRoot.LaunchTargetFor(Path.Combine(plain, "PaRuX.exe")));
        string flat = Dir("flat");
        Assert.Equal(Path.Combine(flat, "PaRuX.exe"), PaRuXDataRoot.LaunchTargetFor(Path.Combine(flat, "PaRuX.exe")));
        Assert.Null(PaRuXDataRoot.LaunchTargetFor(null));
    }

    [Fact]
    public void User_content_stays_inside_PaRuXData_for_the_portable_and_in_Documents_otherwise()
    {
        string app = Dir("ucpkg", "app");
        string data = Dir("ucpkg", "PaRuXData");
        Marker(app, PaRuXDataRoot.PortableMarkerFileName);
        string docs = Dir("docs");

        Assert.Equal(Path.Combine(data, "user-content", "Meeting notes"),
            PaRuXDataRoot.ResolveUserContentRoot("Meeting notes", app, data, docs));
        Assert.Equal(Path.Combine(docs, "PaRuX", "Web knowledge"),
            PaRuXDataRoot.ResolveUserContentRoot("Web knowledge", Dir("plain-install"), data, docs));
    }
}
