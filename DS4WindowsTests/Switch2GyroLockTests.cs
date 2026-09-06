using System.IO;
using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Control.DTOXml;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2GyroLockTests
{
    private const long Frequency = 1_000_000;
    private static readonly Switch2GyroTriggerSourceIdentity Identity = new(
        joyCon: true, pairEpoch: 10, leftDeviceGeneration: 11,
        leftTransportGeneration: 12, rightDeviceGeneration: 13,
        rightTransportGeneration: 14);
    private static readonly Switch2GyroLockBinding Binding = new(
        Switch2JoyConProfileButton.LeftShoulder,
        Switch2JoyConProfileButton.RightShoulder);

    [TestMethod]
    public void HoldLocksOnlyWhileSelectedButtonIsPressed()
    {
        Switch2GyroLockState state = default;
        AssertAdvance(Input(1_000_000, 0, true), Binding, ref state,
            out bool initial);
        Assert.IsFalse(initial);
        AssertAdvance(Input(1_001_000,
            Switch2JoyConProfileButton.LeftShoulder, true), Binding,
            ref state, out bool held);
        Assert.IsTrue(held);
        AssertAdvance(Input(1_002_000, 0, true), Binding, ref state,
            out bool released);
        Assert.IsFalse(released);
    }

    [TestMethod]
    public void ToggleUsesPressEdgesAndPersistsAcrossRelease()
    {
        Switch2GyroLockState state = default;
        AssertAdvance(Input(2_000_000, 0, true), Binding, ref state, out _);
        AssertAdvance(Input(2_001_000,
            Switch2JoyConProfileButton.RightShoulder, true), Binding,
            ref state, out bool toggledOn);
        Assert.IsTrue(toggledOn);
        AssertAdvance(Input(2_002_000, 0, true), Binding, ref state,
            out bool afterRelease);
        Assert.IsTrue(afterRelease);
        AssertAdvance(Input(2_003_000,
            Switch2JoyConProfileButton.RightShoulder, true), Binding,
            ref state, out bool toggledOff);
        Assert.IsFalse(toggledOff);
    }

    [TestMethod]
    public void HeldButtonAtActivationCannotManufactureToggleEdge()
    {
        Switch2GyroLockState state = default;
        AssertAdvance(Input(3_000_000,
            Switch2JoyConProfileButton.RightShoulder, true), Binding,
            ref state, out bool heldAtActivation);
        Assert.IsFalse(heldAtActivation);
        AssertAdvance(Input(3_001_000, 0, false), Binding, ref state,
            out bool inactive);
        Assert.IsFalse(inactive);
        AssertAdvance(Input(3_002_000,
            Switch2JoyConProfileButton.RightShoulder, true), Binding,
            ref state, out bool heldAtReactivation);
        Assert.IsFalse(heldAtReactivation);
    }

    [TestMethod]
    public void SimultaneousToggleButtonsUseDonorParity()
    {
        Switch2GyroLockBinding twoToggles = new(0,
            Switch2JoyConProfileButton.FaceSouth |
            Switch2JoyConProfileButton.FaceEast);
        Switch2GyroLockState state = default;
        AssertAdvance(Input(4_000_000, 0, true), twoToggles, ref state,
            out _);
        AssertAdvance(Input(4_001_000,
            Switch2JoyConProfileButton.FaceSouth |
            Switch2JoyConProfileButton.FaceEast, true), twoToggles,
            ref state, out bool evenEdges);
        Assert.IsFalse(evenEdges);
        AssertAdvance(Input(4_002_000, 0, true), twoToggles, ref state,
            out _);
        AssertAdvance(Input(4_003_000,
            Switch2JoyConProfileButton.FaceSouth, true), twoToggles,
            ref state, out bool oddEdge);
        Assert.IsTrue(oddEdge);
    }

    [TestMethod]
    public void LifecycleProfileModeAndTimestampBoundariesResetToggle()
    {
        Switch2GyroLockState state = default;
        AssertAdvance(Input(5_000_000, 0, true), Binding, ref state, out _);
        AssertAdvance(Input(5_001_000,
            Switch2JoyConProfileButton.RightShoulder, true), Binding,
            ref state, out bool on);
        Assert.IsTrue(on);

        var changedProfile = new Switch2GyroTriggerModifierInput(Identity, 0,
            5_002_000, Frequency, profileRevision: 2, tuningSourceKey: 4,
            outputActive: true);
        AssertAdvance(changedProfile, Binding, ref state,
            out bool profileReset);
        Assert.IsFalse(profileReset);
        Assert.IsTrue(Switch2GyroLock.TryAdvance(changedProfile,
            GyroOutMode.MouseJoystick, Binding, ref state,
            out bool modeReset));
        Assert.IsFalse(modeReset);

        var regressed = new Switch2GyroTriggerModifierInput(Identity,
            Switch2JoyConProfileButton.RightShoulder, 5_001_000, Frequency,
            profileRevision: 2, tuningSourceKey: 4, outputActive: true);
        AssertAdvance(regressed, Binding, ref state,
            out bool timestampReset);
        Assert.IsFalse(timestampReset);
    }

    [TestMethod]
    public void InvalidInputClearsStateAndWarmPathDoesNotAllocate()
    {
        Switch2GyroLockState state = default;
        AssertAdvance(Input(6_000_000, 0, true), Binding, ref state, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= 20_000; i++)
        {
            AssertAdvance(Input(6_000_000 + i,
                (i & 1) == 0 ?
                    Switch2JoyConProfileButton.LeftShoulder : 0,
                true), Binding, ref state, out _);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.AreEqual(0L, after - before);

        var invalid = new Switch2GyroTriggerModifierInput(Identity, 0,
            6_100_000, qpcFrequency: 0, profileRevision: 1,
            tuningSourceKey: 4, outputActive: true);
        Assert.IsFalse(Switch2GyroLock.TryAdvance(invalid,
            GyroOutMode.Mouse, Binding, ref state, out _));
        Assert.IsFalse(state.HasSource);
    }

    [TestMethod]
    public void ProfileXmlRoundTripsBothScopesAndLegacyDefaults()
    {
        var source = new BackingStore();
        Switch2GyroLockBinding mouse = new(
            Switch2JoyConProfileButton.FaceSouth,
            Switch2JoyConProfileButton.RightTrigger);
        Switch2GyroLockBinding joystick = new(
            Switch2JoyConProfileButton.LeftPaddle1,
            Switch2JoyConProfileButton.Capture);
        source.switch2GyroLockBindings[0].TrySet(GyroOutMode.Mouse, mouse);
        source.switch2GyroLockBindings[0].TrySet(
            GyroOutMode.MouseJoystick, joystick);
        var dto = new ProfileDTO { DeviceIndex = 0 };
        dto.MapFrom(source);

        var serializer = new XmlSerializer(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        using var writer = new StringWriter();
        serializer.Serialize(writer, dto);
        string xml = writer.ToString();
        StringAssert.Contains(xml, "<Switch2GyroMouseLock>");
        StringAssert.Contains(xml, "<Switch2GyroMouseJoystickLock>");

        using var reader = new StringReader(xml);
        var roundTrip = (ProfileDTO)serializer.Deserialize(reader);
        roundTrip.DeviceIndex = 0;
        var target = new BackingStore();
        roundTrip.MapTo(target);
        Assert.AreEqual(mouse, target.switch2GyroLockBindings[0].Get(
            GyroOutMode.Mouse));
        Assert.AreEqual(joystick, target.switch2GyroLockBindings[0].Get(
            GyroOutMode.MouseJoystick));

        using var legacyReader = new StringReader("<DS4Windows />");
        var legacy = (ProfileDTO)serializer.Deserialize(legacyReader);
        legacy.DeviceIndex = 0;
        var legacyTarget = new BackingStore();
        legacy.MapTo(legacyTarget);
        Assert.IsFalse(legacyTarget.switch2GyroLockBindings[0].Get(
            GyroOutMode.Mouse).Enabled);
    }

    [TestMethod]
    public void EditorKeepsScopesIndependentAndButtonModesExclusive()
    {
        const int testProfile = Global.TEST_PROFILE_ITEM_COUNT - 1;
        Switch2GyroLockBindingTable previous =
            Global.Switch2GyroLockBindings[testProfile];
        try
        {
            Global.Switch2GyroLockBindings[testProfile] = new();
            var buttons = new[]
            {
                (Switch2JoyConProfileButton.FaceSouth, "A / South"),
                (Switch2JoyConProfileButton.RightTrigger, "ZR"),
            };
            var editor = new Switch2GyroLockEditorViewModel(testProfile,
                buttons);
            editor.HoldButtonChoices[0].IsSelected = true;
            Assert.IsTrue(editor.HoldButtonChoices[0].IsSelected);
            editor.ToggleButtonChoices[0].IsSelected = true;
            Assert.IsFalse(editor.HoldButtonChoices[0].IsSelected);
            Assert.IsTrue(editor.ToggleButtonChoices[0].IsSelected);

            editor.SelectedModeIndex = 1;
            Assert.IsFalse(editor.ToggleButtonChoices[0].IsSelected);
            editor.HoldButtonChoices[1].IsSelected = true;
            editor.SelectedModeIndex = 0;
            Assert.IsTrue(editor.ToggleButtonChoices[0].IsSelected);
            Assert.IsFalse(editor.HoldButtonChoices[1].IsSelected);
        }
        finally
        {
            Global.Switch2GyroLockBindings[testProfile] = previous;
        }
    }

    private static Switch2GyroTriggerModifierInput Input(long timestamp,
        Switch2JoyConProfileButton buttons, bool active) => new(Identity,
            buttons, timestamp, Frequency, profileRevision: 1,
            tuningSourceKey: 4, outputActive: active);

    private static void AssertAdvance(
        in Switch2GyroTriggerModifierInput input,
        in Switch2GyroLockBinding binding, ref Switch2GyroLockState state,
        out bool locked) => Assert.IsTrue(Switch2GyroLock.TryAdvance(input,
            GyroOutMode.Mouse, binding, ref state, out locked));
}
