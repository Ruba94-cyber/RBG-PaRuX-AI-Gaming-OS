using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GrapheneShellLayoutRegressionTests
{
    private static string Root()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PaRuX.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("PaRuX.sln not found.");
    }

    [Fact]
    public void Shell_uses_single_graphene_sidebar_without_mode_tabs_header()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(Path.Combine(Root(), "src", "PaRuX.App", "MainWindow.xaml"));
        // Gaming-removal step 6 collapsed the AI | Gaming top tabs into the ordinary Games sidebar item.
        Assert.Empty(doc.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "ModeTabsHost"));
        Assert.Empty(doc.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "ModeTabs"));
        var pane = Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "NavView"));
        Assert.Equal("{ThemeResource PaRuXGrapheneSidebarBrush}", (string?)pane.Attribute("Background"));
    }

    [Fact]
    public void Chat_avatar_and_settings_rail_use_semantic_graphene()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var chat = XDocument.Load(Path.Combine(Root(), "src", "PaRuX.App", "Views", "ChatView.xaml"));
        var avatar = Assert.Single(chat.Descendants().Where(e => (string?)e.Attribute(x + "Key") == "PaRuXAvatarStyle"));
        Assert.Contains(avatar.Elements(), e => (string?)e.Attribute("Property") == "Background" &&
            (string?)e.Attribute("Value") == "{ThemeResource PaRuXGrapheneSelectedBrush}");
        var settings = XDocument.Load(Path.Combine(Root(), "src", "PaRuX.App", "Views", "SettingsView.xaml"));
        var rail = Assert.Single(settings.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "SettingsRailScroll"));
        Assert.Equal("{ThemeResource PaRuXGrapheneSidebarBrush}", (string?)rail.Attribute("Background"));
    }
}
