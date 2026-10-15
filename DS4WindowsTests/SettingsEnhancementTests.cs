using DS4Windows;
using DS4WinWPF.DS4Forms;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests;

[TestClass]
public class SettingsEnhancementTests
{
    [TestMethod]
    public void SearchHandlesPunctuationAccentsAndMultipleTerms()
    {
        Assert.IsTrue(SettingsSearchMatcher.Matches("usbip repair", "Install / Repair USB/IP"));
        Assert.IsTrue(SettingsSearchMatcher.Matches("cafe", "CAFÉ"));
        Assert.IsFalse(SettingsSearchMatcher.Matches("usbip startup", "USB/IP repair"));
        Assert.IsFalse(SettingsSearchMatcher.HasQuery(" / "));
    }

    [TestMethod]
    public void SectionPreferenceDefaultsAndRejectsUnknownSections()
    {
        var settings = new AppSettingsDTO();
        Assert.AreEqual("General", settings.LastSettingsSection);
        settings.LastSettingsSection = "Controllers";
        Assert.AreEqual("Controllers", settings.LastSettingsSection);
        settings.LastSettingsSection = "removed-section";
        Assert.AreEqual("General", settings.LastSettingsSection);
        var source = new BackingStore { lastSettingsSection = "Appearance" };
        settings.MapFrom(source);
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(AppSettingsDTO));
        using var writer = new StringWriter();
        serializer.Serialize(writer, settings);
        using var reader = new StringReader(writer.ToString());
        var restored = (AppSettingsDTO)serializer.Deserialize(reader);
        var destination = new BackingStore();
        restored.MapTo(destination);
        Assert.AreEqual("Appearance", destination.lastSettingsSection);
    }

    [TestMethod]
    public void RestartReminderTracksAppliedValuesAndRevertingChanges()
    {
        Assert.IsTrue(SettingsRestartRequirements.Language("de", ""));
        Assert.IsFalse(SettingsRestartRequirements.Language("de", "DE"));
        Assert.IsTrue(SettingsRestartRequirements.Executable("DS4Windows.exe", "Controller"));
        Assert.IsFalse(SettingsRestartRequirements.Executable("Controller.exe", "Controller"));
        Assert.IsTrue(SettingsRestartRequirements.Executable("Controller.exe", ""));
        Assert.IsFalse(SettingsRestartRequirements.Executable("DS4Windows.exe", ""));
    }

    [TestMethod]
    public void DeploymentModeUsesTheRunningFolderRatherThanProfileStorage()
    {
        Assert.IsTrue(InstallationModeInfo.Describe(@"C:\Apps\DS4Windows\Custom.exe", @"c:\apps\ds4windows\").Contains("Installed"));
        Assert.IsTrue(InstallationModeInfo.Describe(@"C:\Portable\DS4Windows.exe", @"C:\Apps\DS4Windows").Contains("Portable"));
        Assert.IsTrue(InstallationModeInfo.Describe(@"C:\Portable\DS4Windows.exe", null).Contains("Portable"));
    }
}
