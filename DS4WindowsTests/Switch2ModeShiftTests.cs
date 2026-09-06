using System.IO;
using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Control.DTOXml;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2ModeShiftTests
{
    [DataTestMethod]
    [DataRow(false, Switch2IrActivationThreshold.Balanced, 4_999, 1_499)]
    [DataRow(true, Switch2IrActivationThreshold.Balanced, 4_999, 1_499)]
    [DataRow(false, Switch2IrActivationThreshold.Relaxed, 9_999, 2_999)]
    [DataRow(true, Switch2IrActivationThreshold.Relaxed, 9_999, 2_999)]
    public void IrModeShiftUsesOwnConfiguredThresholdAndRejectsInvalidSource(bool left,
        Switch2IrActivationThreshold threshold, int roughness, int distance)
    {
        const int slot = 0;
        var saved = Global.Switch2ModeShiftSettings[slot];
        var savedLeft = Global.Switch2JoyConLeftIrMouseActivationThreshold[slot];
        var savedRight = Global.Switch2JoyConRightIrMouseActivationThreshold[slot];
        try
        {
            Global.Switch2ModeShiftSettings[slot] = new(left ?
                Switch2JoyConProfileButton.LeftIrSensor : Switch2JoyConProfileButton.RightIrSensor, 0);
            Global.Switch2JoyConLeftIrMouseActivationThreshold[slot] = left ? threshold : Switch2IrActivationThreshold.Strict;
            Global.Switch2JoyConRightIrMouseActivationThreshold[slot] = left ? Switch2IrActivationThreshold.Strict : threshold;
            Mapping.ResetSwitch2ModeShiftState(slot);
            var source = new DS4State { Switch2JoyConRawInputStatus = new() {
                IsValid = true, ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
                Mode = Switch2JoyConProfileMode.Joined, PairEpoch = 1,
                LeftPresent = true, RightPresent = true,
                LeftDeviceGeneration = 1, LeftTransportGeneration = 1,
                RightDeviceGeneration = 2, RightTransportGeneration = 2,
                CompletionTimestampQpc = 1_000_000, QpcFrequency = Frequency,
                LeftIrRoughness = (ushort)roughness, RightIrRoughness = (ushort)roughness,
                LeftIrDistance = (ushort)distance, RightIrDistance = (ushort)distance } };
            var fields = new DS4StateFieldMapping(); // deliberately not an authority for the source
            var exposed = new DS4StateExposed(source);
            Assert.IsTrue(Read());
            if (left) source.Switch2JoyConRawInputStatus.LeftPresent = false;
            else source.Switch2JoyConRawInputStatus.RightPresent = false;
            Assert.IsFalse(Read());
            source.Switch2JoyConRawInputStatus.LeftPresent = source.Switch2JoyConRawInputStatus.RightPresent = true;
            source.Switch2JoyConRawInputStatus.ContractVersion--;
            Assert.IsFalse(Read());
            source.Switch2JoyConRawInputStatus.ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion;
            source.Switch2RawInputStatus = new() { IsValid = true,
                ContractVersion = Switch2ProProfileInputFrame.CurrentVersion };
            Assert.IsFalse(Read());
            source.Switch2RawInputStatus = default;
            Assert.IsTrue(Read());

            bool Read() => Mapping.ShiftTrigger(Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
                slot, source, exposed, null, fields);
        }
        finally
        {
            Global.Switch2ModeShiftSettings[slot] = saved;
            Global.Switch2JoyConLeftIrMouseActivationThreshold[slot] = savedLeft;
            Global.Switch2JoyConRightIrMouseActivationThreshold[slot] = savedRight;
            Mapping.ResetSwitch2ModeShiftState(slot);
        }
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ConsumingIrCommandCannotChangeLaterBindingsOrRepressHeldToggle(bool left, bool toggle)
    {
        const int slot = 0;
        var saved = Global.Switch2ModeShiftSettings[slot];
        var leftThreshold = Global.Switch2JoyConLeftIrMouseActivationThreshold[slot];
        var rightThreshold = Global.Switch2JoyConRightIrMouseActivationThreshold[slot];
        try
        {
            var button = left ? Switch2JoyConProfileButton.LeftIrSensor : Switch2JoyConProfileButton.RightIrSensor;
            var control = left ? DS4Controls.Switch2JoyConLeftIrSensor : DS4Controls.Switch2JoyConRightIrSensor;
            Global.Switch2ModeShiftSettings[slot] = new(toggle ? 0 : button, toggle ? button : 0);
            Global.Switch2JoyConLeftIrMouseActivationThreshold[slot] = Switch2IrActivationThreshold.Strict;
            Global.Switch2JoyConRightIrMouseActivationThreshold[slot] = Switch2IrActivationThreshold.Strict;
            Mapping.ResetSwitch2ModeShiftState(slot);
            var source = new DS4State { Switch2JoyConRawInputStatus = new() {
                IsValid = true, ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
                Mode = Switch2JoyConProfileMode.Joined, PairEpoch = 1, LeftPresent = true, RightPresent = true,
                LeftDeviceGeneration = 1, LeftTransportGeneration = 1,
                RightDeviceGeneration = 2, RightTransportGeneration = 2,
                QpcFrequency = Frequency, CompletionTimestampQpc = 1_000,
                LeftIrRoughness = 3_999, RightIrRoughness = 3_999 } };
            var fields = new DS4StateFieldMapping();
            var outputFields = new DS4StateFieldMapping();
            var exposed = new DS4StateExposed(source);
            bool latched = false;
            bool previous = false;
            foreach (bool pressed in new[] { false, true, true, true, false, true, false })
            {
                source.Switch2JoyConRawInputStatus.CompletionTimestampQpc++;
                source.Switch2JoyConRawInputStatus.LeftIrDistance = left && pressed ? (ushort)999 : (ushort)0;
                source.Switch2JoyConRawInputStatus.RightIrDistance = !left && pressed ? (ushort)999 : (ushort)0;
                fields.PopulateFieldMapping(source, exposed, null);
                if (toggle && pressed && !previous) latched = !latched;
                bool expected = toggle ? latched : pressed;
                Assert.AreEqual(expected, Mapping.ShiftTrigger(Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
                    slot, source, exposed, null, fields), "Early mapping");
                Mapping.SuppressSwitch2ModeShiftActivation(control, source, fields,
                    new DS4State(), outputFields);
                Assert.IsFalse(fields.buttons[(int)control]);
                Assert.AreEqual(expected, Mapping.ShiftTrigger(Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
                    slot, source, exposed, null, fields), "Mapping after IR command consumption");
                previous = pressed;
            }

            if (!toggle)
            {
                source.Switch2JoyConRawInputStatus.LeftIrDistance = left ? (ushort)999 : (ushort)0;
                source.Switch2JoyConRawInputStatus.RightIrDistance = left ? (ushort)0 : (ushort)999;
                var mapped = new DS4State();
                bool observed = true;
                for (int i = 0; i < 2_000; i++) Step();
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 20_000; i++) Step();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.IsTrue(observed);
                Assert.AreEqual(0L, allocated);

                void Step()
                {
                    source.Switch2JoyConRawInputStatus.CompletionTimestampQpc++;
                    observed &= Mapping.ShiftTrigger(Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
                        slot, source, exposed, null, fields);
                    Mapping.SuppressSwitch2ModeShiftActivation(control, source, fields, mapped, outputFields);
                    observed &= Mapping.ShiftTrigger(Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
                        slot, source, exposed, null, fields);
                }
            }
        }
        finally
        {
            Global.Switch2ModeShiftSettings[slot] = saved;
            Global.Switch2JoyConLeftIrMouseActivationThreshold[slot] = leftThreshold;
            Global.Switch2JoyConRightIrMouseActivationThreshold[slot] = rightThreshold;
            Mapping.ResetSwitch2ModeShiftState(slot);
        }
    }

    private const long Frequency = 1_000_000;
    private static readonly Switch2GyroTriggerSourceIdentity Identity = new(
        joyCon: true, pairEpoch: 20, leftDeviceGeneration: 21,
        leftTransportGeneration: 22, rightDeviceGeneration: 23,
        rightTransportGeneration: 24);
    private static readonly Switch2ModeShiftSettings Settings = new(
        Switch2JoyConProfileButton.LeftShoulder,
        Switch2JoyConProfileButton.RightShoulder);

    [TestMethod]
    public void TapPersistsAndHoldTemporarilyInvertsIt()
    {
        Switch2ModeShiftState state = default;
        AssertAdvance(Input(1_000_000, 0), Settings, false, ref state,
            out bool initial);
        Assert.IsFalse(initial);

        AssertAdvance(Input(1_001_000,
            Switch2JoyConProfileButton.RightShoulder), Settings, false,
            ref state, out bool tapOn);
        Assert.IsTrue(tapOn);
        AssertAdvance(Input(1_002_000, 0), Settings, false, ref state,
            out bool released);
        Assert.IsTrue(released);

        AssertAdvance(Input(1_003_000,
            Switch2JoyConProfileButton.LeftShoulder), Settings, false,
            ref state, out bool holdInverted);
        Assert.IsFalse(holdInverted);
        AssertAdvance(Input(1_004_000, 0), Settings, false, ref state,
            out bool holdReleased);
        Assert.IsTrue(holdReleased);
    }

    [TestMethod]
    public void AutoApplyXorsButtonStateAndClosingItClearsTap()
    {
        Switch2ModeShiftState state = default;
        AssertAdvance(Input(2_000_000, 0), Settings, false, ref state,
            out _);
        AssertAdvance(Input(2_001_000, 0), Settings, true, ref state,
            out bool autoOn);
        Assert.IsTrue(autoOn);

        AssertAdvance(Input(2_002_000,
            Switch2JoyConProfileButton.RightShoulder), Settings, true,
            ref state, out bool tapInvertsAuto);
        Assert.IsFalse(tapInvertsAuto);
        AssertAdvance(Input(2_003_000, 0), Settings, false, ref state,
            out bool autoClosed);
        Assert.IsFalse(autoClosed);
        Assert.IsFalse(state.ToggleActive,
            "Leaving an auto-applied gyro scope must clear Tap state.");
    }

    [TestMethod]
    public void SimultaneousTapButtonsProduceOneDonorEdge()
    {
        Switch2ModeShiftSettings twoToggles = new(0,
            Switch2JoyConProfileButton.FaceSouth |
            Switch2JoyConProfileButton.FaceEast);
        Switch2ModeShiftState state = default;
        AssertAdvance(Input(3_000_000, 0), twoToggles, false, ref state,
            out _);
        AssertAdvance(Input(3_001_000,
            Switch2JoyConProfileButton.FaceSouth |
            Switch2JoyConProfileButton.FaceEast), twoToggles, false,
            ref state, out bool active);
        Assert.IsTrue(active,
            "Switch2Connect collapses simultaneous Tap presses to one edge.");
    }

    [TestMethod]
    public void HeldTapAtBoundaryIsBaselineAndLifecycleResetsToggle()
    {
        Switch2ModeShiftState state = default;
        AssertAdvance(Input(4_000_000,
            Switch2JoyConProfileButton.RightShoulder), Settings, false,
            ref state, out bool heldAtStart);
        Assert.IsFalse(heldAtStart);
        AssertAdvance(Input(4_001_000, 0), Settings, false, ref state,
            out _);
        AssertAdvance(Input(4_002_000,
            Switch2JoyConProfileButton.RightShoulder), Settings, false,
            ref state, out bool on);
        Assert.IsTrue(on);

        var newSource = new Switch2GyroTriggerModifierInput(
            new Switch2GyroTriggerSourceIdentity(true, 25, 21, 22, 23, 24),
            0, 4_003_000, Frequency, profileRevision: 1,
            tuningSourceKey: Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
            outputActive: false);
        AssertAdvance(newSource, Settings, false, ref state,
            out bool lifecycleReset);
        Assert.IsFalse(lifecycleReset);

        var changedProfile = new Switch2GyroTriggerModifierInput(Identity,
            0, 4_004_000, Frequency, profileRevision: 2,
            tuningSourceKey: Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
            outputActive: false);
        AssertAdvance(changedProfile, Settings, false, ref state,
            out bool profileReset);
        Assert.IsFalse(profileReset);
    }

    [TestMethod]
    public void ActivationButtonsAreConsumedByCanonicalControlIdentity()
    {
        Assert.IsTrue(Switch2ModeShift.IsActivationControl(DS4Controls.L1,
            Settings));
        Assert.IsTrue(Switch2ModeShift.IsActivationControl(DS4Controls.R1,
            Settings));
        Assert.IsFalse(Switch2ModeShift.IsActivationControl(
            DS4Controls.Cross, Settings));

        Switch2ModeShiftSettings switch2Only = new(
            Switch2JoyConProfileButton.LeftPaddle2 |
                Switch2JoyConProfileButton.LeftIrSensor,
            Switch2JoyConProfileButton.C);
        Assert.IsTrue(Switch2ModeShift.IsActivationControl(
            DS4Controls.Switch2JoyConLeftPaddle2, switch2Only));
        Assert.IsTrue(Switch2ModeShift.IsActivationControl(
            DS4Controls.Switch2JoyConLeftIrSensor, switch2Only));
        Assert.IsTrue(Switch2ModeShift.IsActivationControl(
            DS4Controls.Switch2C, switch2Only));

        Switch2ModeShiftSettings overlap = new(
            Switch2JoyConProfileButton.FaceSouth,
            Switch2JoyConProfileButton.FaceSouth);
        Switch2ModeShiftSettings normalized =
            Switch2ModeShiftSettings.Normalize(overlap);
        Assert.AreEqual(Switch2JoyConProfileButton.FaceSouth,
            normalized.HoldButtons);
        Assert.AreEqual(Switch2JoyConProfileButton.None,
            normalized.ToggleButtons);
    }

    [TestMethod]
    public void HorizontalRailAliasesRequireExclusiveExactStandaloneSource()
    {
        var settings = new Switch2ModeShiftSettings(Switch2JoyConProfileButton.LeftRailSL,
            Switch2JoyConProfileButton.LeftShoulder);
        var source = new DS4State { Switch2JoyConRawInputStatus = new() {
            IsValid = true, ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
            Mode = Switch2JoyConProfileMode.StandaloneHorizontalLeft,
            LeftPresent = true, LeftDeviceGeneration = 1, LeftTransportGeneration = 1 } };
        Assert.IsTrue(Switch2ModeShift.IsActivationControl(DS4Controls.L1, settings, source));
        Assert.AreEqual(Switch2JoyConProfileButton.None,
            Switch2ModeShift.NormalizeForSource(source, settings).ToggleButtons);
        var railOnly = new Switch2ModeShiftSettings(Switch2JoyConProfileButton.LeftRailSL, 0);
        var invalid = Enumerable.Range(0, 7).Select(_ => new DS4State(source)).ToArray();
        invalid[0].Switch2JoyConRawInputStatus.PairEpoch = 1;
        invalid[1].Switch2JoyConRawInputStatus.RightPresent = true;
        invalid[2].Switch2JoyConRawInputStatus.LeftDeviceGeneration = 0;
        invalid[3].Switch2JoyConRawInputStatus.LeftTransportGeneration = 0;
        invalid[4].Switch2JoyConRawInputStatus.ContractVersion = 2;
        invalid[5].Switch2JoyConRawInputStatus.Mode = Switch2JoyConProfileMode.StandaloneVerticalLeft;
        invalid[6].Switch2RawInputStatus = new() { IsValid = true,
            ContractVersion = Switch2ProProfileInputFrame.CurrentVersion };
        foreach (var state in invalid)
        {
            Assert.IsFalse(Switch2ModeShift.IsActivationControl(DS4Controls.L1, railOnly, state));
            Assert.AreEqual(settings.ToggleButtons,
                Switch2ModeShift.NormalizeForSource(state, settings).ToggleButtons);
        }
        bool allValid = true;
        for (int i = 0; i < 2_000; i++)
            allValid &= Switch2ModeShift.IsActivationControl(DS4Controls.L1,
                Switch2ModeShift.NormalizeForSource(source, settings), source);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++)
            allValid &= Switch2ModeShift.IsActivationControl(DS4Controls.L1,
                Switch2ModeShift.NormalizeForSource(source, settings), source);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allValid);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void WarmPolicyPathDoesNotAllocateAndInvalidInputClearsState()
    {
        Switch2ModeShiftState state = default;
        AssertAdvance(Input(5_000_000, 0), Settings, false, ref state,
            out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= 20_000; i++)
        {
            AssertAdvance(Input(5_000_000 + i,
                (i & 1) == 0 ?
                    Switch2JoyConProfileButton.LeftShoulder : 0),
                Settings, false, ref state, out _);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.AreEqual(0L, after - before);

        var invalid = new Switch2GyroTriggerModifierInput(Identity, 0,
            5_100_000, qpcFrequency: 0, profileRevision: 1,
            tuningSourceKey: Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
            outputActive: false);
        Assert.IsFalse(Switch2ModeShift.TryAdvance(invalid, Settings,
            false, ref state, out _));
        Assert.IsFalse(state.HasSource);
    }

    [TestMethod]
    public void ProfileXmlAndEditorRoundTripCompletePolicy()
    {
        var source = new BackingStore();
        Switch2ModeShiftSettings configured = new(
            Switch2JoyConProfileButton.FaceSouth,
            Switch2JoyConProfileButton.RightTrigger,
            autoApplyGyroMouse: false,
            autoApplyGyroMouseJoystick: true,
            autoApplySteering: true);
        source.switch2ModeShiftSettings[0] = configured;
        var dto = new ProfileDTO { DeviceIndex = 0 };
        dto.MapFrom(source);

        var serializer = new XmlSerializer(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        using var writer = new StringWriter();
        serializer.Serialize(writer, dto);
        string xml = writer.ToString();
        StringAssert.Contains(xml, "<Switch2ModeShift>");
        StringAssert.Contains(xml,
            "<AutoApplyGyroMouseJoystick>true</AutoApplyGyroMouseJoystick>");

        using var reader = new StringReader(xml);
        var roundTrip = (ProfileDTO)serializer.Deserialize(reader);
        roundTrip.DeviceIndex = 0;
        var target = new BackingStore();
        roundTrip.MapTo(target);
        Assert.AreEqual(configured, target.switch2ModeShiftSettings[0]);

        using var legacyReader = new StringReader("<DS4Windows />");
        var legacy = (ProfileDTO)serializer.Deserialize(legacyReader);
        legacy.DeviceIndex = 0;
        var legacyTarget = new BackingStore();
        legacy.MapTo(legacyTarget);
        Assert.AreEqual(Switch2ModeShiftSettings.Default,
            legacyTarget.switch2ModeShiftSettings[0]);

        const int testProfile = Global.TEST_PROFILE_ITEM_COUNT - 1;
        Switch2ModeShiftSettings previous =
            Global.Switch2ModeShiftSettings[testProfile];
        try
        {
            Global.Switch2ModeShiftSettings[testProfile] =
                Switch2ModeShiftSettings.Default;
            var editor = new Switch2ModeShiftEditorViewModel(testProfile,
                new[]
                {
                    (Switch2JoyConProfileButton.FaceSouth, "A / South"),
                    (Switch2JoyConProfileButton.RightTrigger, "ZR"),
                });
            editor.HoldButtonChoices[0].IsSelected = true;
            editor.ToggleButtonChoices[0].IsSelected = true;
            Assert.IsFalse(editor.HoldButtonChoices[0].IsSelected);
            Assert.IsTrue(editor.ToggleButtonChoices[0].IsSelected);
            editor.AutoApplyGyroMouse = false;
            editor.AutoApplyGyroMouseJoystick = true;
            editor.AutoApplySteering = true;
            Assert.IsFalse(Global.Switch2ModeShiftSettings[testProfile].
                AutoApplyGyroMouse);
            Assert.IsTrue(Global.Switch2ModeShiftSettings[testProfile].
                AutoApplyGyroMouseJoystick);
            Assert.IsTrue(Global.Switch2ModeShiftSettings[testProfile].
                AutoApplySteering);
        }
        finally
        {
            Global.Switch2ModeShiftSettings[testProfile] = previous;
        }
    }

    [TestMethod]
    public void ModeScopedMappingActionsRoundTripIndependently()
    {
        var source = new BackingStore();
        DS4ControlSettings setting = source.GetDS4CSetting(0,
            DS4Controls.L1);
        setting.shiftTrigger = Mapping.SWITCH2_MODE_SHIFT_TRIGGER;

        Switch2ModeShiftAction mouse = setting.GetSwitch2ModeShiftAction(
            Switch2ModeShiftScope.Mouse);
        mouse.ActionType = DS4ControlSettings.ActionType.Button;
        mouse.Action.actionBtn = X360Controls.A;

        Switch2ModeShiftAction joystick =
            setting.GetSwitch2ModeShiftAction(
                Switch2ModeShiftScope.MouseJoystick);
        joystick.ActionType = DS4ControlSettings.ActionType.Key;
        joystick.Action.actionKey = 0x41;
        joystick.KeyType = DS4KeyType.Toggle;

        Switch2ModeShiftAction steering =
            setting.GetSwitch2ModeShiftAction(
                Switch2ModeShiftScope.Steering);
        steering.ActionType = DS4ControlSettings.ActionType.Macro;
        steering.Action.actionMacro = new[] { 65, 65 + 256 };
        steering.KeyType = DS4KeyType.Macro;
        steering.Extras = "10,20,0,0,0,0,0,0,0";

        var dto = new ProfileDTO { DeviceIndex = 0 };
        dto.MapFrom(source);
        Assert.AreEqual(3, dto.Switch2ModeShiftMappings.Count);
        var serializer = new XmlSerializer(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, dto);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Mapping Control=\"L1\" Scope=\"Mouse\">");
        StringAssert.Contains(xml,
            "<Mapping Control=\"L1\" Scope=\"MouseJoystick\">");
        StringAssert.Contains(xml,
            "<Mapping Control=\"L1\" Scope=\"Steering\">");

        ProfileDTO restored;
        using (var reader = new StringReader(xml))
        {
            restored = (ProfileDTO)serializer.Deserialize(reader);
        }
        restored.DeviceIndex = 0;
        var target = new BackingStore();
        restored.MapTo(target);
        DS4ControlSettings actual = target.GetDS4CSetting(0,
            DS4Controls.L1);
        Assert.AreEqual(Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
            actual.shiftTrigger);
        Assert.AreEqual(X360Controls.A,
            actual.GetSwitch2ModeShiftAction(
                Switch2ModeShiftScope.Mouse).Action.actionBtn);
        Assert.AreEqual(0x41, actual.GetSwitch2ModeShiftAction(
            Switch2ModeShiftScope.MouseJoystick).Action.actionKey);
        CollectionAssert.AreEqual(new[] { 65, 65 + 256 },
            actual.GetSwitch2ModeShiftAction(
                Switch2ModeShiftScope.Steering).Action.actionMacro);
        Assert.AreEqual("10,20,0,0,0,0,0,0,0",
            actual.GetSwitch2ModeShiftAction(
                Switch2ModeShiftScope.Steering).Extras);
    }

    [TestMethod]
    public void SharedTrigger37ProfilesMigrateIntoEveryModeScope()
    {
        var settings = new List<DS4ControlSettings>
        {
            new(DS4Controls.Cross),
        };
        DS4ControlSettings legacy = settings[0];
        legacy.shiftTrigger = Mapping.SWITCH2_MODE_SHIFT_TRIGGER;
        legacy.shiftActionType = DS4ControlSettings.ActionType.Button;
        legacy.shiftAction.actionBtn = X360Controls.Y;
        legacy.shiftExtras = "1,2,0,0,0,0,0,0,0";

        Switch2ModeShiftMappingDTO.Apply(null, settings);

        foreach (Switch2ModeShiftScope scope in Enum.GetValues<
            Switch2ModeShiftScope>())
        {
            Switch2ModeShiftAction lane =
                legacy.GetSwitch2ModeShiftAction(scope);
            Assert.AreEqual(DS4ControlSettings.ActionType.Button,
                lane.ActionType);
            Assert.AreEqual(X360Controls.Y, lane.Action.actionBtn);
            Assert.AreEqual("1,2,0,0,0,0,0,0,0", lane.Extras);
        }
        Assert.AreEqual(DS4ControlSettings.ActionType.Default,
            legacy.shiftActionType);
        Assert.IsNull(legacy.shiftExtras);
    }

    [TestMethod]
    public void LegacySharedLayerXmlMigratesOnceWithoutLeakingExtras()
    {
        var source = new BackingStore();
        DS4ControlSettings legacy = source.GetDS4CSetting(0,
            DS4Controls.Cross);
        legacy.shiftTrigger = Mapping.SWITCH2_MODE_SHIFT_TRIGGER;
        legacy.shiftActionType = DS4ControlSettings.ActionType.Button;
        legacy.shiftAction.actionBtn = X360Controls.Y;
        legacy.shiftExtras = "1,2,0,0,0,0,0,0,0";

        var dto = new ProfileDTO { DeviceIndex = 0 };
        dto.MapFrom(source);
        Assert.IsNotNull(dto.ShiftControl.Extras);
        Assert.AreEqual(legacy.shiftExtras,
            dto.ShiftControl.Extras.CustomMapExtras[DS4Controls.Cross]);

        var serializer = new XmlSerializer(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, dto);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml, "<Cross Trigger=\"37\">1,2");

        ProfileDTO restored;
        using (var reader = new StringReader(xml))
        {
            restored = (ProfileDTO)serializer.Deserialize(reader);
        }
        restored.DeviceIndex = 0;
        var target = new BackingStore();
        restored.MapTo(target);
        DS4ControlSettings actual = target.GetDS4CSetting(0,
            DS4Controls.Cross);
        Assert.IsNull(actual.extras,
            "Shift extras must not be loaded into the regular layer.");
        Assert.AreEqual(DS4ControlSettings.ActionType.Default,
            actual.shiftActionType);
        Assert.IsNull(actual.shiftExtras);
        foreach (Switch2ModeShiftScope scope in Enum.GetValues<
            Switch2ModeShiftScope>())
        {
            Switch2ModeShiftAction lane =
                actual.GetSwitch2ModeShiftAction(scope);
            Assert.AreEqual(DS4ControlSettings.ActionType.Button,
                lane.ActionType);
            Assert.AreEqual(X360Controls.Y, lane.Action.actionBtn);
            Assert.AreEqual("1,2,0,0,0,0,0,0,0", lane.Extras);
        }
    }

    [TestMethod]
    public void RuntimeAndEditorScopesRemainIndependent()
    {
        const int testProfile = Global.TEST_PROFILE_ITEM_COUNT - 1;
        GyroOutMode previousGyro = Global.GyroOutputMode[testProfile];
        SASteeringWheelEmulationAxisType previousSteering =
            Global.SASteeringWheelEmulationAxis[testProfile];
        try
        {
            Global.SASteeringWheelEmulationAxis[testProfile] =
                SASteeringWheelEmulationAxisType.None;
            Global.GyroOutputMode[testProfile] = GyroOutMode.Mouse;
            Assert.AreEqual(Switch2ModeShiftScope.Mouse,
                Switch2ModeShift.ResolveScope(testProfile));
            Global.GyroOutputMode[testProfile] =
                GyroOutMode.MouseJoystick;
            Assert.AreEqual(Switch2ModeShiftScope.MouseJoystick,
                Switch2ModeShift.ResolveScope(testProfile));
            Global.SASteeringWheelEmulationAxis[testProfile] =
                SASteeringWheelEmulationAxisType.LX;
            Assert.AreEqual(Switch2ModeShiftScope.Steering,
                Switch2ModeShift.ResolveScope(testProfile));

            Switch2ModeShift.SetEditingScope(testProfile,
                Switch2ModeShiftScope.MouseJoystick);
            Assert.AreEqual(Switch2ModeShiftScope.MouseJoystick,
                Switch2ModeShift.ResolveEditingScope(testProfile));

            var setting = new DS4ControlSettings(DS4Controls.Cross)
            {
                shiftTrigger = Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
            };
            var vm = new BindingWindowViewModel(testProfile, setting);
            vm.ShiftOutBind.shiftTrigger =
                Mapping.SWITCH2_MODE_SHIFT_TRIGGER;
            vm.ShiftOutBind.outputType = OutBinding.OutType.Button;
            vm.ShiftOutBind.control = X360Controls.B;
            vm.WriteBinds();
            Assert.AreEqual(X360Controls.B,
                setting.GetSwitch2ModeShiftAction(
                    Switch2ModeShiftScope.MouseJoystick).Action.actionBtn);
            Assert.IsTrue(setting.GetSwitch2ModeShiftAction(
                Switch2ModeShiftScope.Mouse).IsDefault);
            Assert.IsTrue(setting.GetSwitch2ModeShiftAction(
                Switch2ModeShiftScope.Steering).IsDefault);
        }
        finally
        {
            Global.GyroOutputMode[testProfile] = previousGyro;
            Global.SASteeringWheelEmulationAxis[testProfile] =
                previousSteering;
            Switch2ModeShift.SetEditingScope(testProfile,
                Switch2ModeShiftScope.Mouse);
        }
    }

    [TestMethod]
    public void ScopedLayersParticipateInCanonicalCustomMappingGate()
    {
        var store = new BackingStore();
        DS4ControlSettings setting = store.GetDS4CSetting(0,
            DS4Controls.Cross);

        Assert.IsFalse(store.HasCustomActions(0));
        Assert.IsFalse(store.HasCustomExtras(0));

        Switch2ModeShiftAction mouseJoystick =
            setting.GetSwitch2ModeShiftAction(
                Switch2ModeShiftScope.MouseJoystick);
        mouseJoystick.ActionType = DS4ControlSettings.ActionType.Button;
        mouseJoystick.Action.actionBtn = X360Controls.X;
        Assert.IsTrue(store.HasCustomActions(0),
            "A profile containing only a scoped Mode Shift action must " +
            "enter Mapping.MapCustom.");
        Assert.IsFalse(store.HasCustomExtras(0));

        mouseJoystick.Reset();
        Switch2ModeShiftAction steering =
            setting.GetSwitch2ModeShiftAction(
                Switch2ModeShiftScope.Steering);
        steering.Extras = "1,2,0,0,0,0,0,0,0";
        Assert.IsFalse(store.HasCustomActions(0));
        Assert.IsTrue(store.HasCustomExtras(0),
            "A profile containing only scoped Mode Shift extras must " +
            "enter Mapping.MapCustom.");

        setting.Reset();
        Assert.IsFalse(store.HasCustomActions(0));
        Assert.IsFalse(store.HasCustomExtras(0));
    }

    [TestMethod]
    public void ConsumedActivationNeutralizesSourceAndOutputFields()
    {
        var physical = new DS4State
        {
            Cross = true,
            L2 = 173,
            L2Raw = 173,
        };
        var exposed = new DS4StateExposed(physical);
        var sourceFields = new DS4StateFieldMapping(physical, exposed,
            tp: null);
        var outputFields = new DS4StateFieldMapping(physical, exposed,
            tp: null);
        var mapped = new DS4State
        {
            Cross = true,
            L2 = 173,
        };

        Mapping.SuppressSwitch2ModeShiftActivation(DS4Controls.Cross,
            physical, sourceFields, mapped, outputFields);
        Mapping.SuppressSwitch2ModeShiftActivation(DS4Controls.L2,
            physical, sourceFields, mapped, outputFields);

        Assert.IsFalse(sourceFields.buttons[(int)DS4Controls.Cross]);
        Assert.IsFalse(outputFields.buttons[(int)DS4Controls.Cross]);
        Assert.AreEqual(0, sourceFields.triggers[(int)DS4Controls.L2]);
        Assert.AreEqual(0, outputFields.triggers[(int)DS4Controls.L2]);
        outputFields.PopulateState(mapped);
        Assert.IsFalse(mapped.Cross);
        Assert.AreEqual(0, mapped.L2);
    }

    [TestMethod]
    public void EditorScopeChangeNotifiesMappingListExactlyOnce()
    {
        const int testProfile = Global.TEST_PROFILE_ITEM_COUNT - 1;
        Switch2ModeShiftScope previous =
            Switch2ModeShift.ResolveEditingScope(testProfile);
        try
        {
            Switch2ModeShift.SetEditingScope(testProfile,
                Switch2ModeShiftScope.Mouse);
            var editor = new Switch2ModeShiftEditorViewModel(testProfile,
                Array.Empty<(Switch2JoyConProfileButton, string)>());
            int notifications = 0;
            editor.MappingScopeChanged += (_, _) => notifications++;

            editor.SelectedMappingScopeIndex = 1;
            Assert.AreEqual(Switch2ModeShiftScope.MouseJoystick,
                Switch2ModeShift.ResolveEditingScope(testProfile));
            Assert.AreEqual(1, notifications);

            editor.SelectedMappingScopeIndex = 1;
            Assert.AreEqual(1, notifications,
                "Re-selecting the active scope must not refresh every row.");
        }
        finally
        {
            Switch2ModeShift.SetEditingScope(testProfile, previous);
        }
    }

    private static Switch2GyroTriggerModifierInput Input(long timestamp,
        Switch2JoyConProfileButton buttons) => new(Identity, buttons,
            timestamp, Frequency, profileRevision: 1,
            tuningSourceKey: Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
            outputActive: false);

    private static void AssertAdvance(
        in Switch2GyroTriggerModifierInput input,
        in Switch2ModeShiftSettings settings, bool autoApply,
        ref Switch2ModeShiftState state, out bool layerActive) =>
        Assert.IsTrue(Switch2ModeShift.TryAdvance(input, settings,
            autoApply, ref state, out layerActive));
}
