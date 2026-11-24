using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// On-demand visual audit (not a regression gate): captures every workspace, Model Hub tab, Create mode and Settings category
/// for the combinations named in <c>PARUX_AUDIT_COMBOS</c> ("lang|theme|mode;..." e.g. "en|Dark|Normal;it|Light|Advanced") at the
/// geometries in <c>PARUX_AUDIT_GEOMS</c> ("1280x720;1920x1080"), into <c>PARUX_AUDIT_OUT</c> (default <c>.ai/ui-audit</c>, which
/// git ignores). Always uses an isolated, empty data root. Without PARUX_AUDIT_COMBOS it does nothing.
/// Run with <c>--filter Category=FullUiAudit</c>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class FullUiAuditUiTests
{
    private static readonly string[] NavIds =
    {
        "Navigation_Chat_Item", "Navigation_Create_Item", "Navigation_Projects_Item", "Navigation_GameLibrary_Item",
        "Navigation_DocumentSearch_Item", "Navigation_ModelHub_Item", "Navigation_ModelRouting_Item", "Navigation_Lifecycle_Item",
        "Navigation_Workflows_Item", "Navigation_Tools_Item", "Navigation_Observability_Item", "Navigation_Updates_Item",
        "Navigation_Runtime_Item", "Navigation_Settings_Item", "Navigation_Help_Item",
    };

    private static readonly string[] HubTabs = { "Start", "Discover", "Text", "Media", "Downloads" };
    private static readonly string[] CreateModes = { "Image", "Video", "Music", "Edit", "3D", "Speech", "Workflows", "Gallery" };

    [Fact, Trait("Category", "FullUiAudit"), Trait("Category", "FlaUI")]
    public void Capture_all_workspaces_for_requested_combinations()
    {
        string? combos = Environment.GetEnvironmentVariable("PARUX_AUDIT_COMBOS");
        if (string.IsNullOrWhiteSpace(combos)) return;
        string outDir = Environment.GetEnvironmentVariable("PARUX_AUDIT_OUT")
            ?? Path.Combine(Repository.Root, ".ai", "ui-audit");
        Directory.CreateDirectory(outDir);
        var geoms = (Environment.GetEnvironmentVariable("PARUX_AUDIT_GEOMS") ?? "1280x720")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(g => g.Split('x')).Select(a => (W: int.Parse(a[0]), H: int.Parse(a[1]))).ToArray();
        foreach (string combo in combos.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] c = combo.Split('|');
            RunCombo(outDir, c[0], c[1], c[2], geoms);
        }
    }

    private static void RunCombo(string outDir, string lang, string theme, string mode, (int W, int H)[] geoms)
    {
        string name = $"audit-{lang}-{theme}-{mode}";
        UiTestApplication.WaitForCorePortReleased();
        string root = Path.Combine(Repository.Root, ".ai", "artifacts", "ui-data", Guid.NewGuid().ToString("N")[..16]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["OnboardingCompleted"] = true,
            ["StudioModeEnabled"] = mode == "Advanced",
            ["BubbleMode"] = "Disabled",
            ["BlockXboxGameBar"] = false,
            ["GameBarBlockConsentAsked"] = true,
            ["Theme"] = theme,
            ["UiLanguage"] = lang,
        }));
        using var app = new UiTestApplication(name, restartDataRoot: root);
        var report = new List<string>();
        try
        {
            foreach (var (w, h) in geoms)
            {
                app.ResizeMainWindow(w, h);
                Thread.Sleep(800);
                string tag = $"{lang}-{theme}-{mode}-{w}x{h}";

                void Snap(string page)
                {
                    Thread.Sleep(900);
                    string? path = app.CaptureSnapshot(page);
                    if (path == null) { report.Add($"{tag} {page}: CAPTURE_SKIPPED"); return; }
                    File.Move(path, Path.Combine(outDir, $"{tag}__{page}.png"), true);
                    string uia = Path.ChangeExtension(path, null) + "-uia.txt";
                    if (File.Exists(uia)) File.Move(uia, Path.Combine(outDir, $"{tag}__{page}-uia.txt"), true);
                    var window = app.MainWindow.BoundingRectangle;
                    foreach (var el in app.MainWindow.FindAllDescendants())
                    {
                        try
                        {
                            if (el.Properties.IsOffscreen.ValueOrDefault) continue;
                            string id = el.Properties.AutomationId.ValueOrDefault ?? "";
                            var r = el.BoundingRectangle;
                            if (r.Width <= 0 || r.Height <= 0) continue;
                            if (r.Right > window.Right + 2 || r.Left < window.Left - 2)
                                report.Add($"{tag} {page}: HCLIP '{id}' {el.ControlType} {r} window {window.Left}..{window.Right}");
                            bool interactive = el.ControlType is ControlType.Button or ControlType.CheckBox or ControlType.ComboBox or ControlType.Edit;
                            if (interactive && string.IsNullOrWhiteSpace(el.Name) && string.IsNullOrWhiteSpace(id))
                                report.Add($"{tag} {page}: UNNAMED {el.ControlType} {r}");
                        }
                        catch { }
                    }
                }

                bool Select(string id)
                {
                    var item = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id))
                        ?? app.FindRawDescendant(app.MainWindow, el => el.Properties.AutomationId.ValueOrDefault == id);
                    if (item == null || item.Properties.IsOffscreen.ValueOrDefault) return false;
                    var sel = item.Patterns.SelectionItem.PatternOrDefault;
                    try { if (sel != null) sel.Select(); else item.Patterns.Invoke.PatternOrDefault?.Invoke(); }
                    catch (Exception ex) { report.Add($"{tag} {id}: SELECT_FAILED {ex.GetType().Name}"); return false; }
                    return true;
                }

                foreach (string id in NavIds)
                {
                    string page = id.Replace("Navigation_", "").Replace("_Item", "");
                    if (!Select(id)) { report.Add($"{tag} {page}: nav item not visible in {mode} mode"); continue; }
                    Snap(page);
                    if (page == "Help")
                    {
                        // Help opens a modal dialog; close it so the next capture is not covered by it.
                        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
                        Thread.Sleep(500);
                    }
                    if (page == "ModelHub")
                        foreach (string t in HubTabs) if (Select("ModelHub_Tab_" + t)) Snap("ModelHub-" + t);
                    if (page == "Create")
                        foreach (string m in CreateModes) if (Select("Create_Mode_" + m)) Snap("Create-" + m.Replace("3D", "ThreeD"));
                    if (page == "Settings")
                    {
                        var settings = new SettingsCategoriesPage(app.MainWindow);
                        foreach (string cat in SettingsCategoriesPage.Categories)
                        {
                            try { settings.SelectCategory(cat); Snap("Settings-" + string.Concat(cat.Where(char.IsLetterOrDigit))); }
                            catch (Exception ex) { report.Add($"{tag} Settings-{cat}: OPEN_FAILED {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
                        }
                    }
                }
                if (Select("MainWindow_ModeTab_Gaming"))
                {
                    Snap("Gaming-Games");
                    if (Select("Navigation_Settings_Item")) Snap("Gaming-Settings");
                    Select("MainWindow_ModeTab_AI");
                }
                if (w == geoms[^1].W)
                {
            // Console mode (full-screen TV surface) last: it takes over the window. No game or Playnite is launched.
            var consoleButton = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("MainWindow_ConsoleMode_Button"));
            if (Select("MainWindow_ModeTab_Gaming") && (consoleButton = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("MainWindow_ConsoleMode_Button"))) != null)
            {
                consoleButton.Patterns.Invoke.PatternOrDefault?.Invoke();
                Thread.Sleep(2500);
                string? path = app.CaptureSnapshot("console");
                if (path != null) File.Move(path, Path.Combine(outDir, $"{lang}-{theme}-{mode}__Console-mode.png"), true);
            }
                }
            }
        }
        catch (Exception ex) { report.Add("FATAL " + ex); app.CaptureFailure(name, ex); }
        finally { File.WriteAllLines(Path.Combine(outDir, $"report-{lang}-{theme}-{mode}.txt"), report); }
    }
}
