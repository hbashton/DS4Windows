using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ControllerDiagramMappingTests
{
    [TestMethod]
    public void ProCButtonHasAnInteractiveTargetAndCanonicalHighlightIndex()
    {
        XDocument xaml = XDocument.Load(SourcePath("ProfileEditor.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement button = xaml.Descendants().SingleOrDefault(element =>
            (string)element.Attribute(x + "Name") == "switch2CConBtn");
        Assert.IsNotNull(button, "The physical C button must have a diagram target.");
        Assert.AreEqual("HoverConBtn_Click", (string)button.Attribute("Click"));
        Assert.AreEqual("ContBtn_MouseEnter", (string)button.Attribute("MouseEnter"));
        Assert.AreEqual("ContBtn_MouseLeave", (string)button.Attribute("MouseLeave"));
        Assert.AreEqual("Collapsed", (string)button.Attribute("Visibility"));

        string source = File.ReadAllText(SourcePath("ProfileEditor.xaml.cs"));
        string pro = Extract(source, "private void ConfigureSwitch2ProDiagram()",
            "private void RefreshJoyConDiagram(");
        StringAssert.Contains(pro, "SetCanvasButtonBounds(switch2CConBtn, 210, 166, 16, 16)");
        StringAssert.Contains(pro, "switch2CConBtn.Visibility = Visibility.Visible");
        string geometry = Extract(source, "private void PopulateSwitch2ProHitGeometries()",
            "private void ControllerDiagramSelector_SelectionChanged(");
        StringAssert.Contains(geometry,
            "vectorHoverGeometries[switch2CConBtn] = RoundedHighlight(210, 166, 16, 16, 3)");
        string indexes = Extract(source, "private void PopulateHoverIndexes()",
            "private void PopulateHoverLocations()");
        StringAssert.Contains(indexes,
            "hoverIndexes[switch2CConBtn] = mappingListVM.ControlIndexMap[DS4Controls.Switch2C]");
        StringAssert.Contains(Extract(source, "private IEnumerable<Button> ControllerDiagramButtons()",
            "private void ClearControllerButtonClips()"), "yield return switch2CConBtn");
        StringAssert.Contains(Extract(source, "private void ConfigureControllerDiagram(",
            "private void ConfigureDualSenseDiagram()"), "switch2CConBtn.Visibility = Visibility.Collapsed");
    }

    [DataTestMethod]
    [DataRow(3, DS4Controls.PS, "Capture")]
    [DataRow(1, DS4Controls.Capture, "Capture")]
    [DataRow(4, DS4Controls.PS, "Home")]
    [DataRow(2, DS4Controls.PS, "Home")]
    public void PhysicalLabelFollowsOrientationWithoutChangingTheCanonicalControl(
        int view, DS4Controls control, string physicalName)
    {
        var target = JoyConArtwork.Targets((JoyConView)view, Switch2FaceButtonLayout.Xbox)
            .Single(target => target.Control == control);
        Assert.AreEqual(physicalName, target.DisplayName);
        var mapped = new MappedControl(Global.TEST_PROFILE_INDEX, control,
            control == DS4Controls.PS ? "PS" : "Capture", OutContType.X360, initMap: true);
        var originalSetting = mapped.Setting;
        string originalMapping = mapped.MappingName;
        Assert.AreEqual($"{physicalName}: {originalMapping}",
            ProfileEditor.FormatHighlightLabel(mapped, target.DisplayName));
        Assert.AreEqual(control, mapped.Control);
        Assert.AreSame(originalSetting, mapped.Setting);
        Assert.AreEqual(originalMapping, mapped.MappingName);
    }

    [TestMethod]
    public void PhysicalHoverNameDoesNotReplaceTheMappedOutputOrGenericFallback()
    {
        var mapped = new MappedControl(Global.TEST_PROFILE_INDEX, DS4Controls.PS,
            "PS", OutContType.X360, initMap: true);
        Assert.AreEqual($"Capture: {mapped.MappingName}",
            ProfileEditor.FormatHighlightLabel(mapped, "Capture"));
        Assert.AreEqual($"PS: {mapped.MappingName}", ProfileEditor.FormatHighlightLabel(mapped));
        Assert.AreEqual("PS", mapped.ControlName);
    }

    [TestMethod]
    public void EveryJoyConTargetCarriesItsPhysicalLabelForHoverAndAccessibility()
    {
        foreach (JoyConView view in Enum.GetValues<JoyConView>())
        foreach (Switch2FaceButtonLayout layout in Enum.GetValues<Switch2FaceButtonLayout>())
        foreach (var target in JoyConArtwork.Targets(view, layout))
            Assert.IsFalse(string.IsNullOrWhiteSpace(target.DisplayName),
                $"{view}/{layout}/{target.Control}");
    }

    [TestMethod]
    public void EditorUsesAndClearsPhysicalLabelsWithoutChangingTheMappingRow()
    {
        string source = File.ReadAllText(SourcePath("ProfileEditor.xaml.cs"));
        string joyCon = Extract(source, "private void ConfigureJoyConDiagram(",
            "private void ConfigureControllerRaster(");
        StringAssert.Contains(joyCon, "target.DisplayName");
        StringAssert.Contains(joyCon, "controllerDiagramControlNames[control] = displayName");
        StringAssert.Contains(joyCon,
            "AutomationProperties.SetName(button, displayName ?? control.ToString())");
        string label = Extract(source, "private void UpdateHighlightLabel(",
            "internal static string FormatHighlightLabel(");
        StringAssert.Contains(label, "controllerDiagramControlNames.TryGetValue(mapped.Control");
        StringAssert.Contains(label, "FormatHighlightLabel(mapped, physicalControlName)");
        StringAssert.Contains(Extract(source, "private void ConfigureControllerDiagram(",
            "private void ConfigureDualSenseDiagram()"), "controllerDiagramControlNames.Clear()");
    }

    [DataTestMethod]
    [DataRow(DS4Controls.Switch2C)]
    [DataRow(DS4Controls.Capture)]
    [DataRow(DS4Controls.TouchLeft)]
    [DataRow(DS4Controls.TouchRight)]
    [DataRow(DS4Controls.TouchMulti)]
    [DataRow(DS4Controls.TouchUpper)]
    [DataRow(DS4Controls.LYNeg)]
    [DataRow(DS4Controls.RXPos)]
    [DataRow(DS4Controls.BLP)]
    [DataRow(DS4Controls.BRP)]
    public void LiveReadoutFindsCanonicalRowsInsteadOfHistoricNumericPositions(DS4Controls control)
    {
        var mappings = new MappingListViewModel(Global.TEST_PROFILE_INDEX, OutContType.X360);
        int index = ProfileEditor.GetInputControlMappingIndex(mappings, control);
        Assert.IsTrue(index >= 0);
        Assert.AreEqual(control, mappings.Mappings[index].Control);
        Assert.AreEqual(mappings.ControlIndexMap[control], index);
        Assert.AreEqual(-1, ProfileEditor.GetInputControlMappingIndex(mappings, DS4Controls.None));
    }

    [TestMethod]
    public void PressToRemapRecognizesCaptureAndCWithoutChangingExistingPriority()
    {
        var state = new DS4State { LX = 128, LY = 128, RX = 128, RY = 128 };
        Assert.AreEqual(DS4Controls.None, ControlService.GetActiveInputControl(state));
        state.Switch2RawInputStatus = new Switch2RawInputStatus
        {
            IsValid = true,
            ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
            CButton = true,
        };
        Assert.AreEqual(DS4Controls.Switch2C, ControlService.GetActiveInputControl(state));
        state.Capture = true;
        Assert.AreEqual(DS4Controls.Capture, ControlService.GetActiveInputControl(state));
        state.PS = true;
        Assert.AreEqual(DS4Controls.PS, ControlService.GetActiveInputControl(state));
        state.Cross = true;
        Assert.AreEqual(DS4Controls.Cross, ControlService.GetActiveInputControl(state));
        state.Cross = state.PS = state.Capture = false;
        state.Switch2RawInputStatus = default;
        Assert.AreEqual(DS4Controls.TouchLeft,
            ControlService.GetActiveInputControl(state, leftTouch: true, rightTouch: true));
        state.LX = 255;
        Assert.AreEqual(DS4Controls.LXPos,
            ControlService.GetActiveInputControl(state, leftTouch: true));
        Assert.AreEqual((byte)255, state.LX);
        Assert.AreEqual(DS4Controls.None, ControlService.GetActiveInputControl(null));
    }

    [TestMethod]
    public void CReadoutUsesTheExistingValidatedSourceBoundaryForEitherModel()
    {
        var state = new DS4State { LX = 128, LY = 128, RX = 128, RY = 128 };
        state.Switch2JoyConRawInputStatus = new Switch2JoyConRawInputStatus
        {
            IsValid = true,
            ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
            CButton = true,
        };
        Assert.AreEqual(DS4Controls.Switch2C, ControlService.GetActiveInputControl(state));
        state.Switch2JoyConRawInputStatus.IsValid = false;
        Assert.AreEqual(DS4Controls.None, ControlService.GetActiveInputControl(state));
        state.Switch2JoyConRawInputStatus.IsValid = true;
        state.Switch2JoyConRawInputStatus.ContractVersion++;
        Assert.AreEqual(DS4Controls.None, ControlService.GetActiveInputControl(state));
        state.Switch2JoyConRawInputStatus.ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion;
        state.Switch2RawInputStatus = new Switch2RawInputStatus
        {
            IsValid = true,
            ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
            CButton = true,
        };
        Assert.AreEqual(DS4Controls.None, ControlService.GetActiveInputControl(state),
            "Ambiguous dual-model source metadata must not activate a C mapping.");
    }

    [TestMethod]
    public void OfflineReadingsUseSelectedPhysicalContextAndRejectUnavailableSlots()
    {
        Assert.AreEqual(2, ProfileEditor.ResolveReadingsDeviceIndex(Global.TEST_PROFILE_INDEX, 2));
        Assert.AreEqual(0, ProfileEditor.ResolveReadingsDeviceIndex(Global.TEST_PROFILE_INDEX, 0));
        Assert.AreEqual(-1, ProfileEditor.ResolveReadingsDeviceIndex(Global.TEST_PROFILE_INDEX, -1));
        Assert.AreEqual(-1, ProfileEditor.ResolveReadingsDeviceIndex(Global.TEST_PROFILE_INDEX,
            ControlService.CURRENT_DS4_CONTROLLER_LIMIT));
        Assert.AreEqual(1, ProfileEditor.ResolveReadingsDeviceIndex(1, 2));
        Assert.AreEqual(-1, ProfileEditor.ResolveReadingsDeviceIndex(-1, 2));
        string source = File.ReadAllText(SourcePath("ProfileEditor.xaml.cs"));
        StringAssert.Contains(source, "ResolveReadingsDeviceIndex(device, triggerPreviewDeviceIndex)");
        StringAssert.Contains(source, "conReadingsUserCon.UseDevice(readingsDevice, device)");
        Assert.IsFalse(source.Contains("UseDevice(0, Global.TEST_PROFILE_INDEX)"));
        string input = Extract(source, "private void InputDS4(", "private void ProfileEditor_Closed(");
        StringAssert.Contains(input, "GetInputControlMappingIndex(mappingListVM, activeControl)");
        Assert.IsFalse(input.Contains("case DS4Controls."));
    }

    private static string SourcePath(string file, [CallerFilePath] string testPath = "") =>
        Path.Combine(Path.GetDirectoryName(testPath), "..", "DS4Windows", "DS4Forms", file);

    private static string Extract(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0, start);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(last > first, end);
        return source[first..last];
    }
}
