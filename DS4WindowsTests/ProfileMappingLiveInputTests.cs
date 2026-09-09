using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DS4WindowsTests;

// Integration contracts complement the live control's snapshot and STA tests.
[TestClass]
public sealed class ProfileMappingLiveInputTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void ControlsPreviewIsOptionalAndSpansTheMappingWorkspace()
    {
        var markup = XDocument.Load(SourcePath("ProfileEditor.xaml"));
        var preview = Named(markup, "mappingLiveInput");
        Assert.AreEqual("MappingLiveInputControl", preview.Name.LocalName);
        Assert.AreEqual("Collapsed", (string)preview.Attribute("Visibility"));
        Assert.AreEqual("2", (string)preview.Attribute("Grid.ColumnSpan"));
        Assert.AreEqual("1", (string)preview.Attribute("Grid.Row"));
        Assert.AreSame(Named(markup, "controlsTab"),
            preview.Ancestors().First(node => node.Name.LocalName == "TabItem"));
        var button = Named(markup, "liveInputPreviewButton");
        Assert.AreEqual("Show live preview", (string)button.Attribute("Content"));
        Assert.AreEqual("LiveInputPreviewButton_Click", (string)button.Attribute("Click"));
        Assert.AreEqual("WrapPanel", button.Parent.Name.LocalName,
            "The preview button must not squeeze the mapping heading on narrow windows.");
    }

    [TestMethod]
    public void AllProfileMappingDialogsReuseTheSamePreviewContextAndLifecycle()
    {
        string source = File.ReadAllText(SourcePath("ProfileEditor.xaml.cs"));
        int callers = Regex.Matches(source, @"new BindingWindow\(").Count;
        Assert.IsTrue(callers >= 7, "Cover diagram, list, second action and motion mappings.");
        Assert.AreEqual(callers, Regex.Matches(source, @"ShowMappingBindingWindow\(window\);").Count);
        string helper = Between(source, "private void ShowMappingBindingWindow(",
            "private void ShowControlBindingWindow(");
        StringAssert.Contains(helper, "window.SetLiveInputDevice(mappingReadingsDevice, mappingReadingsSource)");
        StringAssert.Contains(helper, "window.LiveInputPreviewVisible = liveInputPreviewVisible");
        StringAssert.Contains(helper, "showingMappingDialog = true");
        StringAssert.Contains(helper, "finally");
        StringAssert.Contains(helper, "SetLiveInputPreviewVisible(window.LiveInputPreviewVisible)");
        Assert.IsFalse(helper.Contains("SetLiveInputDevice(deviceNum"));
    }

    [TestMethod]
    public void HiddenUnselectedAndClosedPreviewCannotEnablePolling()
    {
        string source = File.ReadAllText(SourcePath("ProfileEditor.xaml.cs"));
        string refresh = Between(source, "private void RefreshMappingLiveInput(",
            "private void LiveInputPreviewButton_Click(");
        StringAssert.Contains(refresh, "liveInputPreviewVisible && mappingReadingsActive && !showingMappingDialog");
        StringAssert.Contains(refresh, "sidebarTabControl.SelectedItem == controlsTab");
        string close = Between(source, "private void ProfileEditor_Closed(",
            "private void UseControllerReadoutCk_Click(");
        StringAssert.Contains(close, "mappingReadingsActive = false");
        StringAssert.Contains(close, "mappingLiveInput.EnableControl(false)");
        string toggle = Between(source, "private void SetLiveInputPreviewVisible(",
            "private void ShowMappingBindingWindow(");
        StringAssert.Contains(toggle, "Visibility.Visible : Visibility.Collapsed");
        StringAssert.Contains(toggle, "RefreshMappingLiveInput()");
        Assert.IsFalse(toggle.Contains("Global."), "The display preference does not change saved bindings.");
    }

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(node => (string)node.Attribute(Xaml + "Name") == name);

    private static string SourcePath(string name, [CallerFilePath] string testPath = "") =>
        Path.Combine(Path.GetDirectoryName(testPath), "..", "DS4Windows", "DS4Forms", name);

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0 && last > first);
        return source[first..last];
    }
}
