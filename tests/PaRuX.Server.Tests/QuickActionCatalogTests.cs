using System;
using System.IO;
using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class QuickActionCatalogTests
{
    [Fact]
    public void Every_quick_action_has_unique_ids_unique_shortcuts_and_both_languages()
    {
        Assert.Equal(QuickActionCatalog.All.Count, QuickActionCatalog.All.Select(a => a.Id).Distinct().Count());
        string[] shortcuts = QuickActionCatalog.All.Where(a => a.ShortcutText != null).Select(a => a.ShortcutText!).ToArray();
        Assert.Equal(shortcuts.Length, shortcuts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (QuickAction a in QuickActionCatalog.All)
        {
            Assert.NotEqual(a.TitleEn, a.TitleIt);
            Assert.False(string.IsNullOrWhiteSpace(a.Keywords));
            Assert.DoesNotContain(a.ShortcutText ?? "-", QuickActionCatalog.ReservedShortcuts);
            if (a.AcceleratorKey is char k) Assert.Equal("Ctrl+Shift+" + char.ToUpperInvariant(k), a.ShortcutText);   // text and key never drift
        }
    }

    [Fact]
    public void The_palette_title_teaches_the_shortcut_in_both_languages()
    {
        QuickAction attach = QuickActionCatalog.All.Single(a => a.Id == QuickActionCatalog.AttachFile);
        Assert.EndsWith("(Ctrl+Shift+A)", QuickActionCatalog.PaletteTitle(attach, false));
        Assert.StartsWith("Allega", QuickActionCatalog.PaletteTitle(attach, true));
    }

    [Fact]
    public void The_everyday_tasks_are_all_covered()
    {
        string[] ids = QuickActionCatalog.All.Select(a => a.Id).ToArray();
        foreach (string needed in new[] { QuickActionCatalog.SearchChats, QuickActionCatalog.ChooseModel, QuickActionCatalog.GenerationSettings, QuickActionCatalog.AttachFile, QuickActionCatalog.CreateImage })
            Assert.Contains(needed, ids);
    }

    private static string Read(params string[] relative)
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir)!;
        return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(relative).ToArray()));
    }

    [Fact]
    public void The_window_wires_the_palette_and_accelerators_to_the_catalog_and_the_chat_entry_points_exist()
    {
        string palette = Read("src", "PaRuX.App", "MainWindow.CommandPalette.cs");
        Assert.Contains("QuickActionCatalog.All", palette);
        Assert.Contains("VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift", palette);
        foreach (string id in new[] { "SearchChats", "ChooseModel", "GenerationSettings", "AttachFile", "CreateImage" })
            Assert.Contains("QuickActionCatalog." + id, palette);
        string chat = Read("src", "PaRuX.App", "Views", "ChatView.QuickActions.cs");
        foreach (string m in new[] { "QuickOpenAttachPicker", "QuickOpenModelPicker", "QuickOpenGenerationSettings" }) Assert.Contains(m, chat);
        Assert.Contains("ShowImageMode", Read("src", "PaRuX.App", "Views", "CreateWorkspaceView.Tabs.cs"));
    }
}
