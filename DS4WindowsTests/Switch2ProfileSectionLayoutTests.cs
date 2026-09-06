using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

// These check the production markup, not a duplicate view or a rendered UI.
// Actual navigation and layout still require the portable WPF acceptance pass.
[TestClass]
public sealed class Switch2ProfileSectionLayoutTests
{
    private static readonly XNamespace Wpf =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void Switch2IsItsOwnSettingsPageBetweenTriggerLabAndAdvanced()
    {
        var editor = Load("ProfileEditor.xaml");
        var settings = editor.Descendants(Wpf + "TabControl").Single(
            node => (string)node.Attribute(Xaml + "Name") == "profileSettingsTabCon");
        var tabs = settings.Elements(Wpf + "TabItem").ToArray();
        CollectionAssert.AreEqual(new[]
        {
            "Audio Haptics", "Trigger Lab", "Switch 2 Controls", "Advanced",
        }, tabs.TakeLast(4).Select(node => (string)node.Attribute("Header")).ToArray());
        Assert.AreEqual(8, tabs.Length);
        Assert.AreEqual("switch2ControlsTab", (string)tabs[6].Attribute(Xaml + "Name"));
        var panel = editor.Descendants(Wpf + "StackPanel").Single(
            node => (string)node.Attribute(Xaml + "Name") == "switch2SettingsPanel");
        Assert.AreSame(tabs[6], panel.Ancestors(Wpf + "TabItem").First());
        Assert.IsFalse(tabs[7].Descendants().Any(node =>
            (string)node.Attribute(Xaml + "Name") == "switch2SettingsPanel"));
    }

    [TestMethod]
    public void ShellIndicesMatchSettingsAndLeaveLogAfterAdvanced()
    {
        var shell = Load("Themes", "BridgeShellStyles.xaml");
        var menu = shell.Descendants(Wpf + "ListBox").Single(node =>
            ((string)node.Attribute("SelectedIndex"))?.Contains(
                "ProfileEditorNavigationIndex", StringComparison.Ordinal) == true);
        var items = menu.Elements(Wpf + "ListBoxItem").ToArray();
        Assert.AreEqual(13, items.Length);
        Assert.AreEqual("Trigger Lab", (string)items[9].Attribute("Content"));
        Assert.AreEqual("Switch 2 Controls", (string)items[10].Attribute("Content"));
        Assert.AreEqual("Advanced", (string)items[11].Attribute("Content"));
        Assert.AreEqual("Log", (string)items[12].Attribute("Content"));
    }

    [DataTestMethod]
    [DataRow("Switch2MapXboxImpulseTriggersToHdRumble")]
    [DataRow("Switch2RumbleDelayMilliseconds")]
    [DataRow("Switch2XboxImpulseDynamicFrequency")]
    [DataRow("Switch2FaceButtonLayoutIndex")]
    [DataRow("Switch2AutoDisconnectModeIndex")]
    public void ExistingControlBindingHasOneHomeInTheNewPage(string property)
    {
        var editor = Load("ProfileEditor.xaml");
        var bindings = editor.Descendants().SelectMany(node => node.Attributes())
            .Where(attribute => attribute.Value == "{Binding " + property + "}")
            .ToArray();
        Assert.AreEqual(1, bindings.Length, property);
        Assert.AreEqual("switch2ControlsTab", (string)bindings[0].Parent!
            .Ancestors(Wpf + "TabItem").First().Attribute(Xaml + "Name"));
    }

    [TestMethod]
    public void CalibrationHandlersAndCapabilityGuardRemainInTheNewPage()
    {
        var editor = Load("ProfileEditor.xaml");
        var tab = editor.Descendants(Wpf + "TabItem").Single(node =>
            (string)node.Attribute(Xaml + "Name") == "switch2ControlsTab");
        var panel = tab.Descendants(Wpf + "StackPanel").Single(node =>
            (string)node.Attribute(Xaml + "Name") == "switch2SettingsPanel");
        StringAssert.Contains((string)panel.Attribute("Visibility"), "ShowSwitch2Controls");
        Assert.AreEqual(1, tab.Descendants(Wpf + "Button").Count(node =>
            (string)node.Attribute("Click") == "Switch2StickCalibration_Click"));
        Assert.AreEqual(1, tab.Descendants(Wpf + "DataTrigger").Count(node =>
            (string)node.Attribute("Binding") == "{Binding ShowSwitch2Controls}" &&
            (string)node.Attribute("Value") == "False"));
        Assert.IsTrue(tab.Descendants(Wpf + "ScrollViewer").Any(node =>
            (string)node.Attribute("VerticalScrollBarVisibility") == "Auto"));
    }

    [TestMethod]
    public void PageHasFiveThemedCardsWithOnlyFeedbackInitiallyExpanded()
    {
        var editor = Load("ProfileEditor.xaml");
        var panel = editor.Descendants(Wpf + "StackPanel").Single(node =>
            (string)node.Attribute(Xaml + "Name") == "switch2SettingsPanel");
        var borders = panel.Elements(Wpf + "Border").ToArray();
        Assert.AreEqual(5, borders.Length);
        Assert.IsTrue(borders.All(node => (string)node.Attribute("Style") ==
            "{StaticResource Switch2SettingsCardStyle}"));
        var cards = borders.Select(node => node.Element(Wpf + "Expander")!).ToArray();
        CollectionAssert.AreEqual(new[]
        {
            "switch2FeedbackCard", "switch2MotionCard", "switch2JoyConCard",
            "switch2ConnectionCard", "switch2CalibrationCard",
        }, cards.Select(node => (string)node.Attribute(Xaml + "Name")).ToArray());
        CollectionAssert.AreEqual(new[] { "True", "False", "False", "False", "False" },
            cards.Select(node => (string)node.Attribute("IsExpanded")).ToArray());
        foreach (var card in cards)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(
                (string)card.Attribute("AutomationProperties.Name")));
            var heading = card.Element(Wpf + "Expander.Header")!;
            Assert.AreEqual(2, heading.Descendants(Wpf + "TextBlock").Count());
            Assert.IsTrue(heading.Descendants(Wpf + "TextBlock").All(node =>
                (string)node.Attribute("TextWrapping") == "Wrap"));
        }
    }

    [DataTestMethod]
    [DataRow("Switch2MapXboxImpulseTriggersToHdRumble", "switch2FeedbackCard")]
    [DataRow("Switch2DualSenseAudioHapticsEnabled", "switch2FeedbackCard")]
    [DataRow("Switch2RumbleDelayMilliseconds", "switch2FeedbackCard")]
    [DataRow("Switch2HighRateMousePresentation", "switch2MotionCard")]
    [DataRow("Switch2VirtualGyroSoftDeadzone", "switch2MotionCard")]
    [DataRow("Switch2DualGyroEditor.Enabled", "switch2JoyConCard")]
    [DataRow("Switch2JoyConIrMouseEnabled", "switch2JoyConCard")]
    [DataRow("Switch2FaceButtonLayoutIndex", "switch2ConnectionCard")]
    [DataRow("Switch2AutoDisconnectModeIndex", "switch2ConnectionCard")]
    [DataRow("Switch2ConnectionHapticEnabled", "switch2ConnectionCard")]
    public void SettingRemainsBoundInItsFeatureCard(string property, string cardName)
    {
        var editor = Load("ProfileEditor.xaml");
        var binding = editor.Descendants().SelectMany(node => node.Attributes())
            .Single(attribute => attribute.Value == "{Binding " + property + "}");
        Assert.IsTrue(binding.Parent!.Ancestors(Wpf + "Expander").Any(node =>
            (string)node.Attribute(Xaml + "Name") == cardName), property);
    }

    [TestMethod]
    public void CalibrationActionsRemainExplicitAndElementBindingsKeepTheirTargets()
    {
        var editor = Load("ProfileEditor.xaml");
        var calibration = editor.Descendants(Wpf + "Expander").Single(node =>
            (string)node.Attribute(Xaml + "Name") == "switch2CalibrationCard");
        CollectionAssert.AreEquivalent(new[]
        {
            "Switch2StickCalibration_Click", "GyroCalibration_Click",
            "Switch2MagnetometerCalibration_Click", "Switch2MagnetometerCalibrationCancel_Click",
        }, calibration.Descendants(Wpf + "Button").Select(node =>
            (string)node.Attribute("Click")).ToArray());
        var tab = calibration.Ancestors(Wpf + "TabItem").First();
        var names = tab.Descendants().Attributes(Xaml + "Name").Select(a => a.Value).ToArray();
        foreach (var reference in tab.Descendants().SelectMany(node => node.Attributes())
                     .SelectMany(attribute => System.Text.RegularExpressions.Regex.Matches(
                         attribute.Value, @"ElementName=(Switch2\w+)")
                         .Select(match => match.Groups[1].Value)))
        {
            Assert.AreEqual(1, names.Count(name => name == reference), reference);
        }
        // Moving controls into cards must not introduce another namescope/template.
        Assert.IsFalse(calibration.Ancestors().Any(node =>
            node.Name == Wpf + "DataTemplate" || node.Name == Wpf + "ControlTemplate"));
    }

    private static XDocument Load(params string[] relative)
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory);
             root != null; root = root.Parent)
        {
            string path = Path.Combine(new[] { root.FullName, "DS4Windows", "DS4Forms" }
                .Concat(relative).ToArray());
            if (File.Exists(path)) return XDocument.Load(path);
        }
        throw new FileNotFoundException("Production markup not found: " + Path.Combine(relative));
    }
}
