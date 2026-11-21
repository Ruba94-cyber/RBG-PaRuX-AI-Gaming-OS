using System.Xml.Linq;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Icon-only buttons have no visible text, so a screen reader announces them by their accessible name alone.
/// These checks read the shipped XAML and fail when a named control loses its name.
/// </summary>
public sealed class IconOnlyButtonAccessibilityTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName AutomationName = "AutomationProperties.Name";

    [Theory]
    [InlineData("src/PaRuX.App/Views/ChatView.xaml", "AttachButton", "Attach images")]
    [InlineData("src/PaRuX.App/Views/ChatView.xaml", "StopButton", "Stop generating")]
    [InlineData("src/PaRuX.App/Views/ChatView.xaml", "SendButton", "Send message")]
    [InlineData("src/PaRuX.App/Views/ChatView.xaml", "CodeFolderClearButton", "Clear code folder")]
    [InlineData("src/PaRuX.App/Windows/BubbleWindow.xaml", "BubbleSend", "Send from Quick Chat")]
    public void Icon_only_button_has_a_named_accessible_name(string relativePath, string buttonName, string expectedName)
    {
        XElement button = FindNamedButton(relativePath, buttonName);

        Assert.Equal(expectedName, (string?)button.Attribute(AutomationName));
    }

    [Fact]
    public void Quick_Chat_collapse_button_is_named_for_screen_readers()
    {
        // The collapse control is the Button wired to Collapse_Click; it has no x:Name of its own.
        string path = Path.Combine(FindRepositoryRoot(), "src", "PaRuX.App", "Windows", "BubbleWindow.xaml");
        XDocument document = XDocument.Load(path);
        XElement collapse = Assert.Single(document.Descendants()
            .Where(element => element.Name.LocalName == "Button"
                && (string?)element.Attribute("Click") == "Collapse_Click"));

        Assert.Equal("Close Quick Chat", (string?)collapse.Attribute(AutomationName));
    }

    [Fact]
    public void Every_XAML_button_has_an_accessible_name_or_visible_text()
    {
        string root = FindRepositoryRoot();
        var offenders = new List<string>();
        string[] kinds = { "Button", "ToggleButton", "HyperlinkButton", "AppBarButton", "RepeatButton" };
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", "PaRuX.App"), "*.xaml", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(file);
            foreach (XElement button in document.Descendants().Where(e => kinds.Contains(e.Name.LocalName)))
            {
                bool named = !string.IsNullOrWhiteSpace((string?)button.Attribute(AutomationName))
                    || !string.IsNullOrWhiteSpace((string?)button.Attribute("Content"))
                    || !string.IsNullOrWhiteSpace((string?)button.Attribute("Label"))
                    || button.Descendants().Any(d => d.Name.LocalName == "TextBlock")
                    || button.Elements().Any(d => d.Name.LocalName.EndsWith(".Content", StringComparison.Ordinal) && d.HasElements);
                if (named) continue;
                // Content assigned (localized) in code-behind: OnboardingView.SetupProfile.cs sets it on load.
                if ((string?)button.Attribute(Xaml + "Name") == "GamingOnlyStartButton") continue;
                offenders.Add(Path.GetRelativePath(root, file) + " :: " + ((string?)button.Attribute(Xaml + "Name") ?? (string?)button.Attribute("Click") ?? "(unnamed)"));
            }
        }

        Assert.True(offenders.Count == 0, "Buttons without an accessible name or visible text:\n" + string.Join("\n", offenders));
    }

    private static XElement FindNamedButton(string relativePath, string buttonName)
    {
        string path = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        XDocument document = XDocument.Load(path);
        return Assert.Single(document.Descendants()
            .Where(element => element.Name.LocalName == "Button"
                && (string?)element.Attribute(Xaml + "Name") == buttonName));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
            current = current.Parent;
        }
        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("PaRuX.sln was not found above the test output directory.");
    }
}
