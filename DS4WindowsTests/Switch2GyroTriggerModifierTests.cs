using System.IO;
using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Control.DTOXml;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2GyroTriggerModifierTests
{
    private const long Frequency = 1_000_000;

    private static readonly Switch2GyroTriggerSourceIdentity Identity = new(
        joyCon: true, pairEpoch: 3, leftDeviceGeneration: 4,
        leftTransportGeneration: 5, rightDeviceGeneration: 6,
        rightTransportGeneration: 7);

    private static readonly Switch2IrGyroTuning Tuning = new(
        Switch2JoyConProfileButton.FaceSouth, 15.0, 100, 80, 200,
        Switch2JoyConProfileButton.RightTrigger, 90.0, 250);

    [TestMethod]
    public void HeldButtonsAtActivationEstablishBaselineWithoutFalseFreeze()
    {
        Switch2GyroTriggerModifierState state = default;
        var input = Input(1_000_000,
            Switch2JoyConProfileButton.FaceSouth |
            Switch2JoyConProfileButton.RightTrigger, active: true);

        Assert.IsTrue(Switch2GyroTriggerModifier.TryAdvance(input, Tuning,
            ref state, out Switch2GyroTriggerModifierResult result));

        Assert.IsTrue(result.OutputActive);
        Assert.IsFalse(result.Freeze);
        Assert.IsTrue(result.DeadzoneActive);
        Assert.AreEqual(15.0, result.DeadzoneAmount);
        Assert.IsTrue(result.DampeningActive);
        Assert.AreEqual(0.1, result.DampeningMultiplier, 1.0e-12);
    }

    [TestMethod]
    public void DeadzonePressAndReleaseEdgesUseIndependentFreezeDurations()
    {
        Switch2GyroTriggerModifierState state = default;
        AssertAdvance(Input(1_000_000, 0, true), ref state, out _);

        AssertAdvance(Input(1_001_000,
            Switch2JoyConProfileButton.FaceSouth, true), ref state,
            out Switch2GyroTriggerModifierResult pressed);
        Assert.IsTrue(pressed.Freeze);
        Assert.IsTrue(pressed.DeadzoneActive);

        AssertAdvance(Input(1_102_000,
            Switch2JoyConProfileButton.FaceSouth, true), ref state,
            out Switch2GyroTriggerModifierResult afterPressWindow);
        Assert.IsFalse(afterPressWindow.Freeze);

        AssertAdvance(Input(1_103_000, 0, true), ref state,
            out Switch2GyroTriggerModifierResult released);
        Assert.IsTrue(released.Freeze);
        Assert.IsTrue(released.DeadzoneActive);

        AssertAdvance(Input(1_184_000, 0, true), ref state,
            out Switch2GyroTriggerModifierResult afterReleaseWindow);
        Assert.IsFalse(afterReleaseWindow.Freeze);
        Assert.IsTrue(afterReleaseWindow.DeadzoneActive);

        AssertAdvance(Input(1_304_000, 0, true), ref state,
            out Switch2GyroTriggerModifierResult afterLatch);
        Assert.IsFalse(afterLatch.DeadzoneActive);
    }

    [TestMethod]
    public void DampeningReleaseLatchRetainsThenRestoresFullStrength()
    {
        Switch2GyroTriggerModifierState state = default;
        AssertAdvance(Input(2_000_000,
            Switch2JoyConProfileButton.RightTrigger, true), ref state,
            out Switch2GyroTriggerModifierResult held);
        Assert.AreEqual(0.1, held.DampeningMultiplier, 1.0e-12);

        AssertAdvance(Input(2_010_000, 0, true), ref state,
            out Switch2GyroTriggerModifierResult released);
        Assert.IsTrue(released.DampeningActive);

        AssertAdvance(Input(2_261_000, 0, true), ref state,
            out Switch2GyroTriggerModifierResult expired);
        Assert.IsFalse(expired.DampeningActive);
        Assert.AreEqual(1.0, expired.DampeningMultiplier);
    }

    [TestMethod]
    public void InactiveAndBoundaryChangesCannotManufactureEdges()
    {
        Switch2GyroTriggerModifierState state = default;
        AssertAdvance(Input(3_000_000, 0, true), ref state, out _);
        AssertAdvance(Input(3_001_000,
            Switch2JoyConProfileButton.FaceSouth, false), ref state,
            out Switch2GyroTriggerModifierResult inactive);
        Assert.IsFalse(inactive.OutputActive);

        AssertAdvance(Input(3_002_000,
            Switch2JoyConProfileButton.FaceSouth, true), ref state,
            out Switch2GyroTriggerModifierResult reactivated);
        Assert.IsFalse(reactivated.Freeze);
        Assert.IsTrue(reactivated.DeadzoneActive);

        var newProfile = new Switch2GyroTriggerModifierInput(Identity,
            Switch2JoyConProfileButton.FaceSouth, 3_003_000, Frequency,
            profileRevision: 2, tuningSourceKey: 5, outputActive: true);
        AssertAdvance(newProfile, ref state,
            out Switch2GyroTriggerModifierResult profileChanged);
        Assert.IsFalse(profileChanged.Freeze);
    }

    [TestMethod]
    public void ActivationSourceChangeResetsBaselineEvenWithSameTuning()
    {
        Switch2GyroTriggerModifierState state = default;
        AssertAdvance(Input(3_100_000, 0, true), ref state, out _);
        AssertAdvance(Input(3_101_000,
            Switch2JoyConProfileButton.FaceSouth, true), ref state,
            out Switch2GyroTriggerModifierResult pressed);
        Assert.IsTrue(pressed.Freeze);

        var changedSource = new Switch2GyroTriggerModifierInput(Identity,
            Switch2JoyConProfileButton.FaceSouth, 3_102_000, Frequency,
            profileRevision: 1, tuningSourceKey: 6, outputActive: true);
        AssertAdvance(changedSource, ref state,
            out Switch2GyroTriggerModifierResult reset);
        Assert.IsFalse(reset.Freeze);
        Assert.IsTrue(reset.DeadzoneActive);
    }

    [TestMethod]
    public void SourceReaderIncludesCanonicalAndSwitch2OnlyButtons()
    {
        DS4State state = new()
        {
            Cross = true,
            R2Btn = true,
            DpadLeft = true,
            Switch2JoyConRawInputStatus = new Switch2JoyConRawInputStatus
            {
                IsValid = true,
                ContractVersion = Switch2JoyConProfileInputFrame.
                    CurrentVersion,
                PairEpoch = 8,
                LeftDeviceGeneration = 9,
                LeftTransportGeneration = 10,
                RightDeviceGeneration = 11,
                RightTransportGeneration = 12,
                CompletionTimestampQpc = 4_000_000,
                QpcFrequency = Frequency,
                CButton = true,
                LeftPaddle2 = true,
                RightPaddle1 = true,
            },
        };

        Assert.IsTrue(Switch2GyroTriggerModifier.TryReadInput(state, 5, 7,
            outputActive: true, out Switch2GyroTriggerModifierInput input));

        Switch2JoyConProfileButton expected =
            Switch2JoyConProfileButton.FaceSouth |
            Switch2JoyConProfileButton.RightTrigger |
            Switch2JoyConProfileButton.DpadLeft |
            Switch2JoyConProfileButton.C |
            Switch2JoyConProfileButton.LeftPaddle2 |
            Switch2JoyConProfileButton.RightPaddle1;
        Assert.AreEqual(expected, input.Buttons);
        Assert.IsTrue(input.Identity.JoyCon);
        Assert.AreEqual(8UL, input.Identity.PairEpoch);
    }

    [TestMethod]
    public void ProReaderUsesExactGenerationAndPaddleSidecar()
    {
        DS4State state = new()
        {
            BLP = true,
            BRP = true,
            Switch2RawInputStatus = new Switch2RawInputStatus
            {
                IsValid = true,
                ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
                DeviceGeneration = 20,
                TransportGeneration = 21,
                CompletionTimestampQpc = 5_000_000,
                QpcFrequency = Frequency,
                CButton = true,
            },
        };

        Assert.IsTrue(Switch2GyroTriggerModifier.TryReadInput(state, 1, 4,
            outputActive: true, out Switch2GyroTriggerModifierInput input));

        Assert.IsFalse(input.Identity.JoyCon);
        Assert.AreEqual(20UL, input.Identity.LeftDeviceGeneration);
        Assert.AreEqual(21UL, input.Identity.LeftTransportGeneration);
        Assert.AreEqual(Switch2JoyConProfileButton.C |
            Switch2JoyConProfileButton.LeftPaddle1 |
            Switch2JoyConProfileButton.RightPaddle1, input.Buttons);
    }

    [TestMethod]
    public void InvalidTimingFailsClosedAndClearsState()
    {
        Switch2GyroTriggerModifierState state = default;
        AssertAdvance(Input(6_000_000, 0, true), ref state, out _);
        var invalid = new Switch2GyroTriggerModifierInput(Identity, 0,
            6_001_000, qpcFrequency: 0, profileRevision: 1,
            tuningSourceKey: 5, outputActive: true);

        Assert.IsFalse(Switch2GyroTriggerModifier.TryAdvance(invalid,
            Tuning, ref state, out _));
        Assert.IsFalse(state.HasSource);
    }

    [TestMethod]
    public void WarmAdvanceDoesNotAllocate()
    {
        Switch2GyroTriggerModifierState state = default;
        AssertAdvance(Input(7_000_000, 0, true), ref state, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 1; i <= 20_000; i++)
        {
            var input = Input(7_000_000 + i,
                (i & 1) == 0 ? Switch2JoyConProfileButton.RightTrigger : 0,
                true);
            AssertAdvance(input, ref state, out _);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.AreEqual(0L, after - before);
    }

    [TestMethod]
    public void PresentationTransformsAreSubtractiveFiniteAndOptIn()
    {
        Assert.AreEqual(18.0,
            Switch2GyroTriggerModifier.ApplySoftDeadzone(30.0, 0.6, 20.0),
            1.0e-12);
        Assert.AreEqual(-18.0,
            Switch2GyroTriggerModifier.ApplySoftDeadzone(-30.0, 0.6, 20.0),
            1.0e-12);
        Assert.AreEqual(0.0,
            Switch2GyroTriggerModifier.ApplySoftDeadzone(10.0, 0.6, 20.0));
        Assert.AreEqual(0.0,
            Switch2GyroTriggerModifier.ApplySoftDeadzone(double.NaN,
                0.6, 20.0));

        Switch2GyroTriggerModifierResult inactive = default;
        Assert.AreEqual(8.0,
            Switch2GyroTriggerModifier.ApplyDampening(8.0, inactive));
        var active = new Switch2GyroTriggerModifierResult(true, false,
            false, 0.0, true, 0.25);
        Assert.AreEqual(2.0,
            Switch2GyroTriggerModifier.ApplyDampening(8.0, active));
        Assert.AreEqual(0.0,
            Switch2GyroTriggerModifier.ApplyDampening(double.PositiveInfinity,
                active));
    }

    [TestMethod]
    public void ProfileXmlRoundTripsAndLegacyProfilesUseSourceDefaults()
    {
        Switch2IrGyroTuning custom = new(
            Switch2JoyConProfileButton.FaceWest |
                Switch2JoyConProfileButton.LeftPaddle2,
            22.5, 31, 47, 59,
            Switch2JoyConProfileButton.RightShoulder, 63.0, 71);
        var source = new BackingStore();
        Assert.IsTrue(source.switch2GyroTriggerTunings[0].TrySet(
            GyroOutMode.Mouse, triggerIndex: 5, custom));
        Switch2IrGyroTuning joystickCustom = new(
            Switch2JoyConProfileButton.FaceNorth, 18.0, 20, 21, 22,
            Switch2JoyConProfileButton.LeftTrigger, 54.0, 23);
        Assert.IsTrue(source.switch2GyroTriggerTunings[0].TrySet(
            GyroOutMode.MouseJoystick, triggerIndex: 5, joystickCustom));
        var dto = new ProfileDTO { DeviceIndex = 0 };
        dto.MapFrom(source);

        var serializer = new XmlSerializer(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        using var writer = new StringWriter();
        serializer.Serialize(writer, dto);
        string xml = writer.ToString();
        StringAssert.Contains(xml, "<Switch2GyroTriggerTunings>");
        StringAssert.Contains(xml,
            "<Trigger Mode=\"Mouse\" TriggerIndex=\"5\">");
        StringAssert.Contains(xml,
            "<Trigger Mode=\"MouseJoystick\" TriggerIndex=\"5\">");
        StringAssert.Contains(xml, "<DeadzoneAmount>22.5</DeadzoneAmount>");

        using var reader = new StringReader(xml);
        var roundTrip = (ProfileDTO)serializer.Deserialize(reader);
        roundTrip.DeviceIndex = 0;
        var target = new BackingStore();
        roundTrip.MapTo(target);
        Assert.AreEqual(custom, target.switch2GyroTriggerTunings[0].Get(
            GyroOutMode.Mouse, 5));
        Assert.AreEqual(joystickCustom,
            target.switch2GyroTriggerTunings[0].Get(
                GyroOutMode.MouseJoystick, 5));
        Assert.AreEqual(Switch2IrGyroTuning.Default,
            target.switch2GyroTriggerTunings[0].Get(
                GyroOutMode.Mouse, 6));

        using var legacyReader = new StringReader("<DS4Windows />");
        var legacy = (ProfileDTO)serializer.Deserialize(legacyReader);
        legacy.DeviceIndex = 0;
        var legacyTarget = new BackingStore();
        legacy.MapTo(legacyTarget);
        Assert.AreEqual(Switch2IrGyroTuning.Default,
            legacyTarget.switch2GyroTriggerTunings[0].Get(
                GyroOutMode.Mouse, 5));
    }

    [TestMethod]
    public void TriggerSourceSelectionTracksActivationEdgesAndScopes()
    {
        Assert.AreEqual(7, Mouse.SelectGyroTriggerTuningIndex(
            rawActive: true, previousRawActive: false,
            activeTriggerIndex: 7, firstTriggerIndex: 4,
            currentTriggerIndex: -1));
        Assert.AreEqual(7, Mouse.SelectGyroTriggerTuningIndex(
            rawActive: true, previousRawActive: true,
            activeTriggerIndex: 4, firstTriggerIndex: 4,
            currentTriggerIndex: 7));
        Assert.AreEqual(4, Mouse.SelectGyroTriggerTuningIndex(
            rawActive: false, previousRawActive: false,
            activeTriggerIndex: -1, firstTriggerIndex: 4,
            currentTriggerIndex: -1));

        var table = new Switch2GyroTriggerTuningTable();
        Assert.AreEqual(29, Switch2GyroTriggerTuningTable.AlwaysOnTriggerIndex);
        Assert.AreEqual(35, Switch2GyroTriggerTuningTable.TriggerCount);
        Assert.IsTrue(table.TrySet(GyroOutMode.Mouse, 7, Tuning));
        Assert.AreEqual(Tuning, table.Get(GyroOutMode.Mouse, 7));
        Assert.AreEqual(Switch2IrGyroTuning.Default,
            table.Get(GyroOutMode.MouseJoystick, 7));
        Assert.IsFalse(table.TrySet(GyroOutMode.Controls, 7, Tuning));
        Assert.IsFalse(table.TrySet(GyroOutMode.Mouse,
            Switch2GyroTriggerTuningTable.TriggerCount, Tuning));
    }

    [TestMethod]
    public void AlwaysOnAndAllAppendedTriggerTuningsRoundTripInBothScopes()
    {
        var source = new BackingStore();
        var modes = new[] { GyroOutMode.Mouse, GyroOutMode.MouseJoystick };
        foreach (var mode in modes)
        {
            for (int index = 29; index <= 34; index++)
            {
                var tuning = new Switch2IrGyroTuning(
                    Switch2JoyConProfileButton.LeftRailSL | Switch2JoyConProfileButton.RightRailSR,
                    index, 31, 47, 59, Switch2JoyConProfileButton.LeftRailSR,
                    mode == GyroOutMode.Mouse ? 20 : 70, 71);
                Assert.IsTrue(source.switch2GyroTriggerTunings[0].TrySet(mode, index, tuning));
            }
        }
        var dto = new ProfileDTO { DeviceIndex = 0 };
        dto.MapFrom(source);
        var serializer = new XmlSerializer(typeof(ProfileDTO), ProfileDTO.GetAttributeOverrides());
        using var writer = new StringWriter();
        serializer.Serialize(writer, dto);
        using var reader = new StringReader(writer.ToString());
        var loaded = (ProfileDTO)serializer.Deserialize(reader);
        loaded.DeviceIndex = 0;
        var target = new BackingStore();
        loaded.MapTo(target);
        foreach (var mode in modes)
        {
            for (int index = 29; index <= 34; index++)
                Assert.AreEqual(source.switch2GyroTriggerTunings[0].Get(mode, index),
                    target.switch2GyroTriggerTunings[0].Get(mode, index), $"{mode}/{index}");
        }
        Assert.AreEqual(29, Switch2GyroTriggerTuningTable.AlwaysOnTriggerIndex);
    }

    [TestMethod]
    public void EditorSwitchesTriggerAndModeWithoutSharingTuning()
    {
        const int testProfile = Global.TEST_PROFILE_ITEM_COUNT - 1;
        Switch2GyroTriggerTuningTable previous =
            Global.Switch2GyroTriggerTunings[testProfile];
        try
        {
            Global.Switch2GyroTriggerTunings[testProfile] = new();
            string[] triggers = new string[
                Switch2GyroTriggerTuningTable.TriggerCount];
            for (int i = 0; i < triggers.Length; i++)
            {
                triggers[i] = $"Trigger {i}";
            }
            var buttons = new[]
            {
                (Switch2JoyConProfileButton.FaceSouth, "A / South"),
                (Switch2JoyConProfileButton.RightTrigger, "ZR"),
            };
            var editor = new Switch2GyroTriggerTuningEditorViewModel(
                testProfile, triggers, buttons);

            editor.SelectedTriggerIndex = 5;
            editor.DeadzoneAmount = 22.5;
            editor.DeadzoneButtonChoices[0].IsSelected = true;
            editor.SelectedModeIndex = 1;
            Assert.AreEqual(Switch2IrGyroTuning.Default.DeadzoneAmount,
                editor.DeadzoneAmount);
            editor.DampeningAmountPercent = 61.0;
            editor.DampeningButtonChoices[1].IsSelected = true;

            editor.SelectedModeIndex = 0;
            Assert.AreEqual(22.5, editor.DeadzoneAmount);
            Assert.IsTrue(editor.DeadzoneButtonChoices[0].IsSelected);
            Assert.IsFalse(editor.DampeningButtonChoices[1].IsSelected);
            editor.SelectedModeIndex = 1;
            Assert.AreEqual(61.0, editor.DampeningAmountPercent);
            Assert.IsTrue(editor.DampeningButtonChoices[1].IsSelected);
        }
        finally
        {
            Global.Switch2GyroTriggerTunings[testProfile] = previous;
        }
    }

    private static Switch2GyroTriggerModifierInput Input(long timestamp,
        Switch2JoyConProfileButton buttons, bool active) => new(Identity,
            buttons, timestamp, Frequency, profileRevision: 1,
            tuningSourceKey: 5, outputActive: active);

    private static void AssertAdvance(
        in Switch2GyroTriggerModifierInput input,
        ref Switch2GyroTriggerModifierState state,
        out Switch2GyroTriggerModifierResult result) =>
        Assert.IsTrue(Switch2GyroTriggerModifier.TryAdvance(input, Tuning,
            ref state, out result));
}
