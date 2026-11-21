using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Source-level guard for the Devices panel: hiding is optional, every new control is addressable and bilingual.</summary>
public sealed class GamingDevicesHidingOptionalContractTests
{
    private static string Panel()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "PaRuX.App", "Views", "GamingDevicesPanel.cs"));
    }

    [Theory]
    [InlineData("GameDevices_Guided_HideConsent_CheckBox")]
    [InlineData("GameDevices_Guided_NoHideWarning_Text")]
    [InlineData("GameDevices_RawMapping_DpadX_Index")]
    [InlineData("GameDevices_RawMapping_DpadY_Index")]
    [InlineData("GameDevices_RawMapping_InvertDpadX_CheckBox")]
    [InlineData("GameDevices_RawMapping_InvertDpadY_CheckBox")]
    [InlineData("GameDevices_RawMapping_DpadUp_Index")]
    [InlineData("GameDevices_RawMapping_DpadRight_Index")]
    public void New_controls_carry_automation_ids(string id) => Assert.Contains("\"" + id + "\"", Panel(), StringComparison.Ordinal);

    [Fact]
    public void Start_never_hides_without_the_checked_consent_and_no_longer_requires_it()
    {
        string code = Panel();
        Assert.Contains("StartAsync(device, hide, SelectedGameId, allowUnhidden: true)", code, StringComparison.Ordinal);
        Assert.Contains("bool hide = _hideConsent.IsChecked == true;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("forwarding does not start, because every input would be doubled", code, StringComparison.Ordinal);
        // The missing-driver message names ViGEmBus and says HidHide is not required.
        Assert.Contains("ViGEmBus", code, StringComparison.Ordinal);
        Assert.Contains("HidHide is not required", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Warning_texts_exist_in_english_and_italian()
    {
        string code = Panel();
        Assert.Contains("Warning: with hiding off", code, StringComparison.Ordinal);
        Assert.Contains("Attenzione: con il nascondimento spento", code, StringComparison.Ordinal);
        Assert.Contains("HidHide non è richiesto", code, StringComparison.Ordinal);
    }
}
