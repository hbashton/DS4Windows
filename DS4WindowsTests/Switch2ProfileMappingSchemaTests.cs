using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Control.DTOXml;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2ProfileMappingSchemaTests
{
    private const ushort ProSourceContractVersion =
        Switch2ProProfileInputFrame.CurrentVersion;
    private const ushort JoyConSourceContractVersion =
        Switch2JoyConProfileInputFrame.CurrentVersion;

    private static readonly DS4Controls[] Switch2SourceControls =
    [
        DS4Controls.Switch2C,
        DS4Controls.Switch2JoyConLeftPaddle1,
        DS4Controls.Switch2JoyConLeftPaddle2,
        DS4Controls.Switch2JoyConRightPaddle1,
        DS4Controls.Switch2JoyConRightPaddle2,
        DS4Controls.Switch2JoyConLeftIrSensor,
        DS4Controls.Switch2JoyConRightIrSensor,
        DS4Controls.Switch2JoyConLeftSL,
        DS4Controls.Switch2JoyConLeftSR,
        DS4Controls.Switch2JoyConRightSL,
        DS4Controls.Switch2JoyConRightSR,
    ];

    [TestMethod]
    public void JoyConIrSourcesAreAppendOnlyCanonicalShiftTriggers()
    {
        var preserved = new (int Id, DS4Controls Control, string Label)[]
        {
            (27, DS4Controls.Mute, "Mute"),
            (28, DS4Controls.FnL, "Function Left"),
            (29, DS4Controls.FnR, "Function Right"),
            (30, DS4Controls.BLP, "Bottom Left Paddle"),
            (31, DS4Controls.BRP, "Bottom Right Paddle"),
            (32, DS4Controls.Capture, "Capture"),
            (33, DS4Controls.SideL, "Side L"),
            (34, DS4Controls.SideR, "Side R"),
            (35, DS4Controls.Switch2JoyConLeftIrSensor,
                "Switch 2 Joy-Con Left IR Sensor"),
            (36, DS4Controls.Switch2JoyConRightIrSensor,
                "Switch 2 Joy-Con Right IR Sensor"),
            (38, DS4Controls.Switch2JoyConLeftSL, "Switch 2 Joy-Con Left SL"),
            (39, DS4Controls.Switch2JoyConLeftSR, "Switch 2 Joy-Con Left SR"),
            (40, DS4Controls.Switch2JoyConRightSL, "Switch 2 Joy-Con Right SL"),
            (41, DS4Controls.Switch2JoyConRightSR, "Switch 2 Joy-Con Right SR"),
            (42, DS4Controls.Switch2C, "Switch 2 C / Chat"),
        };

        foreach ((int id, DS4Controls control, string label) in preserved)
        {
            Assert.IsTrue(Mapping.TryGetShiftTriggerControl(id,
                out DS4Controls actual));
            Assert.AreEqual(control, actual);
            Assert.AreEqual(label, MappedControl.ShiftTrigger(id));
        }

        Assert.AreEqual(43, Mapping.SHIFT_TRIGGER_MAPPING_LEN);
        Assert.IsFalse(Mapping.TryGetShiftTriggerControl(0, out _));
        Assert.IsFalse(Mapping.TryGetShiftTriggerControl(26, out _),
            "Touch-finger ID 26 is intentionally not a DS4Controls value.");
        Assert.IsFalse(Mapping.TryGetShiftTriggerControl(37, out _));
        Assert.AreEqual("Switch 2 Mode Shift",
            MappedControl.ShiftTrigger(37));

        DS4State leftState = JoyConState(leftIrActive: true);
        DS4StateFieldMapping leftFields = Populate(leftState);
        Assert.IsTrue(Mapping.ShiftTrigger(35, 0, leftState,
            new DS4StateExposed(leftState), null, leftFields));
        Assert.IsFalse(Mapping.ShiftTrigger(36, 0, leftState,
            new DS4StateExposed(leftState), null, leftFields));

        DS4State rightState = JoyConState(rightIrActive: true);
        DS4StateFieldMapping rightFields = Populate(rightState);
        Assert.IsFalse(Mapping.ShiftTrigger(35, 0, rightState,
            new DS4StateExposed(rightState), null, rightFields));
        Assert.IsTrue(Mapping.ShiftTrigger(36, 0, rightState,
            new DS4StateExposed(rightState), null, rightFields));
    }

    [TestMethod]
    public void JoyConIrShiftTriggersRoundTripWithMouseActions()
    {
        BackingStore store = new();
        DS4ControlSettings left = store.ds4settings[0][
            (int)DS4Controls.L1 - 1];
        DS4ControlSettings right = store.ds4settings[0][
            (int)DS4Controls.R1 - 1];
        left.UpdateSettings(shift: true, X360Controls.LeftMouse,
            string.Empty, DS4KeyType.None, trigger: 35);
        right.UpdateSettings(shift: true, X360Controls.RightMouse,
            string.Empty, DS4KeyType.None, trigger: 36);

        ProfileDTO dto = new() { DeviceIndex = 0 };
        dto.MapFrom(store);
        dto.SerializeAppAttrs = false;
        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, dto);
            xml = writer.ToString();
        }

        StringAssert.Contains(xml, "<L1 Trigger=\"35\">Left Mouse Button</L1>");
        StringAssert.Contains(xml, "<R1 Trigger=\"36\">Right Mouse Button</R1>");

        ProfileDTO restored;
        using (var reader = new StringReader(xml))
        {
            restored = (ProfileDTO)serializer.Deserialize(reader);
        }
        BackingStore destination = new();
        restored.DeviceIndex = 0;
        restored.MapTo(destination);

        DS4ControlSettings restoredLeft = destination.ds4settings[0][
            (int)DS4Controls.L1 - 1];
        DS4ControlSettings restoredRight = destination.ds4settings[0][
            (int)DS4Controls.R1 - 1];
        Assert.AreEqual(35, restoredLeft.shiftTrigger);
        Assert.AreEqual(X360Controls.LeftMouse,
            restoredLeft.shiftAction.actionBtn);
        Assert.AreEqual(36, restoredRight.shiftTrigger);
        Assert.AreEqual(X360Controls.RightMouse,
            restoredRight.shiftAction.actionBtn);
    }

    [TestMethod]
    public void MagnetometerYawAssistIsOptInAndRoundTrips()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.IsFalse(store.switch2MagnetometerYawAssistEnabled[0]);

        input.Switch2MagnetometerYawAssistEnabled = true;
        input.MapTo(store);
        Assert.IsTrue(store.switch2MagnetometerYawAssistEnabled[0]);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.IsTrue(output.Switch2MagnetometerYawAssistEnabled);

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        output.SerializeAppAttrs = false;
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Switch2MagnetometerYawAssistEnabled>true</Switch2MagnetometerYawAssistEnabled>");
        using (var reader = new StringReader(xml))
        {
            var deserialized = (ProfileDTO)serializer.Deserialize(reader);
            Assert.IsTrue(deserialized.Switch2MagnetometerYawAssistEnabled);
        }
        using (var reader = new StringReader(
                   "<DS4Windows config_version=\"5\" />"))
        {
            var legacy = (ProfileDTO)serializer.Deserialize(reader);
            Assert.IsFalse(legacy.Switch2MagnetometerYawAssistEnabled);
        }
    }

    [TestMethod]
    public void VirtualGyroSoftDeadzoneDefaultsOffAndRoundTrips()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.AreEqual(0.0, store.switch2VirtualGyroSoftDeadzone[0]);

        input.Switch2VirtualGyroSoftDeadzone = 12.5;
        input.MapTo(store);
        Assert.AreEqual(12.5, store.switch2VirtualGyroSoftDeadzone[0]);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.AreEqual(12.5, output.Switch2VirtualGyroSoftDeadzone);

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        output.SerializeAppAttrs = false;
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Switch2VirtualGyroSoftDeadzone>12.5</Switch2VirtualGyroSoftDeadzone>");
        using (var reader = new StringReader(xml))
        {
            var deserialized = (ProfileDTO)serializer.Deserialize(reader);
            Assert.AreEqual(12.5,
                deserialized.Switch2VirtualGyroSoftDeadzone);
        }

        input.Switch2VirtualGyroSoftDeadzone = double.NaN;
        input.MapTo(store);
        Assert.AreEqual(0.0, store.switch2VirtualGyroSoftDeadzone[0]);
        input.Switch2VirtualGyroSoftDeadzone = 100.5;
        input.MapTo(store);
        Assert.AreEqual(0.0, store.switch2VirtualGyroSoftDeadzone[0]);
    }

    [TestMethod]
    public void HorizonStabilizationDefaultsOffAndRoundTrips()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.IsFalse(store.switch2HorizonStabilizationEnabled[0]);

        input.Switch2HorizonStabilizationEnabled = true;
        input.MapTo(store);
        Assert.IsTrue(store.switch2HorizonStabilizationEnabled[0]);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.IsTrue(output.Switch2HorizonStabilizationEnabled);

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        output.SerializeAppAttrs = false;
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Switch2HorizonStabilizationEnabled>true</Switch2HorizonStabilizationEnabled>");
        using (var reader = new StringReader(
                   "<DS4Windows config_version=\"5\" />"))
        {
            var legacy = (ProfileDTO)serializer.Deserialize(reader);
            Assert.IsFalse(legacy.Switch2HorizonStabilizationEnabled);
        }
    }

    [TestMethod]
    public void XboxImpulseToHdRumbleDefaultsOnAndTuningRoundTrips()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.IsTrue(store.switch2MapXboxImpulseTriggersToHdRumble[0]);
        Assert.IsTrue(store.switch2XboxImpulseDynamicFrequency[0]);
        Assert.AreEqual(10, store.switch2XboxImpulseFrequency[0]);
        Assert.AreEqual(5, store.switch2XboxImpulseStrength[0]);

        input.Switch2MapXboxImpulseTriggersToHdRumble = false;
        input.Switch2XboxImpulseDynamicFrequency = false;
        input.Switch2XboxImpulseFrequency = 3;
        input.Switch2XboxImpulseStrength = 8;
        input.MapTo(store);
        Assert.IsFalse(store.switch2MapXboxImpulseTriggersToHdRumble[0]);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.IsFalse(output.Switch2MapXboxImpulseTriggersToHdRumble);
        Assert.IsFalse(output.Switch2XboxImpulseDynamicFrequency);
        Assert.AreEqual(3, output.Switch2XboxImpulseFrequency);
        Assert.AreEqual(8, output.Switch2XboxImpulseStrength);

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        output.SerializeAppAttrs = false;
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Switch2XboxImpulseDynamicFrequency>false</Switch2XboxImpulseDynamicFrequency>");
        StringAssert.Contains(xml,
            "<Switch2XboxImpulseFrequency>3</Switch2XboxImpulseFrequency>");
        StringAssert.Contains(xml,
            "<Switch2XboxImpulseStrength>8</Switch2XboxImpulseStrength>");
        using (var reader = new StringReader(xml))
        {
            var deserialized = (ProfileDTO)serializer.Deserialize(reader);
            Assert.IsFalse(deserialized.Switch2XboxImpulseDynamicFrequency);
            Assert.AreEqual(3, deserialized.Switch2XboxImpulseFrequency);
            Assert.AreEqual(8, deserialized.Switch2XboxImpulseStrength);
        }
        using (var reader = new StringReader(
                   "<DS4Windows config_version=\"5\" />"))
        {
            var legacy = (ProfileDTO)serializer.Deserialize(reader);
            Assert.IsTrue(legacy.Switch2MapXboxImpulseTriggersToHdRumble);
            Assert.IsTrue(legacy.Switch2XboxImpulseDynamicFrequency);
            Assert.AreEqual(10, legacy.Switch2XboxImpulseFrequency);
            Assert.AreEqual(5, legacy.Switch2XboxImpulseStrength);
        }

        input.Switch2XboxImpulseFrequency = 0;
        input.Switch2XboxImpulseStrength = 11;
        input.MapTo(store);
        Assert.AreEqual(10, store.switch2XboxImpulseFrequency[0]);
        Assert.AreEqual(5, store.switch2XboxImpulseStrength[0]);
    }

    [TestMethod]
    public void Switch2BodyStrengthReusesCanonicalRumbleBoostPersistence()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.AreEqual(Switch2HdRumbleBodyTuning.DefaultStrengthPercent,
            store.rumble[0]);

        input.RumbleBoost = 150;
        input.MapTo(store);
        Assert.AreEqual((byte)150, store.rumble[0]);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.AreEqual((byte)150, output.RumbleBoost);

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        output.SerializeAppAttrs = false;
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml, "<RumbleBoost>150</RumbleBoost>");

        using var reader = new StringReader(
            "<DS4Windows config_version=\"5\" />");
        var legacy = (ProfileDTO)serializer.Deserialize(reader);
        Assert.AreEqual(Switch2HdRumbleBodyTuning.DefaultStrengthPercent,
            legacy.RumbleBoost);
    }

    [TestMethod]
    public void XboxBodyCarrierModeIsOptInAndFrequencyRoundTrips()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.IsFalse(store.switch2XboxBodyRumbleMode[0]);
        Assert.AreEqual(
            Switch2HdRumbleBodyTuning.DefaultXboxFrequencyLevel,
            store.switch2XboxBodyRumbleFrequency[0]);

        input.Switch2XboxBodyRumbleMode = true;
        input.Switch2XboxBodyRumbleFrequency = 4;
        input.MapTo(store);
        Assert.IsTrue(store.switch2XboxBodyRumbleMode[0]);
        Assert.AreEqual(4, store.switch2XboxBodyRumbleFrequency[0]);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.IsTrue(output.Switch2XboxBodyRumbleMode);
        Assert.AreEqual(4, output.Switch2XboxBodyRumbleFrequency);

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        output.SerializeAppAttrs = false;
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Switch2XboxBodyRumbleMode>true</Switch2XboxBodyRumbleMode>");
        StringAssert.Contains(xml,
            "<Switch2XboxBodyRumbleFrequency>4</Switch2XboxBodyRumbleFrequency>");

        using (var reader = new StringReader(
                   "<DS4Windows config_version=\"5\" />"))
        {
            var legacy = (ProfileDTO)serializer.Deserialize(reader);
            Assert.IsFalse(legacy.Switch2XboxBodyRumbleMode);
            Assert.AreEqual(
                Switch2HdRumbleBodyTuning.DefaultXboxFrequencyLevel,
                legacy.Switch2XboxBodyRumbleFrequency);
        }

        input.Switch2XboxBodyRumbleFrequency = 0;
        input.MapTo(store);
        Assert.AreEqual(
            Switch2HdRumbleBodyTuning.DefaultXboxFrequencyLevel,
            store.switch2XboxBodyRumbleFrequency[0]);
        input.Switch2XboxBodyRumbleFrequency = 11;
        input.MapTo(store);
        Assert.AreEqual(
            Switch2HdRumbleBodyTuning.DefaultXboxFrequencyLevel,
            store.switch2XboxBodyRumbleFrequency[0]);
    }

    [TestMethod]
    public void DualJoyConGyroFusionIsOptInAndRoundTrips()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.IsFalse(store.switch2DualJoyConGyroFusionEnabled[0]);
        Assert.AreEqual(Switch2DualGyroDominantSide.Right,
            store.switch2DualJoyConGyroDominantSide[0]);
        Assert.AreEqual(Switch2DualGyroMode.SwitchDominantSide,
            store.switch2DualJoyConGyroMode[0]);
        Assert.AreEqual(Switch2DualGyroActivationMode.Hold,
            store.switch2DualJoyConGyroActivationMode[0]);
        Assert.AreEqual(Switch2JoyConProfileButton.None,
            store.switch2DualJoyConGyroLeftActivationButton[0]);
        Assert.AreEqual(Switch2JoyConProfileButton.None,
            store.switch2DualJoyConGyroRightActivationButton[0]);

        input.Switch2DualJoyConGyroFusionEnabled = true;
        input.Switch2DualJoyConGyroDominantSide =
            Switch2DualGyroDominantSide.None;
        input.Switch2DualJoyConGyroMode =
            Switch2DualGyroMode.SingleSideToggle;
        input.Switch2DualJoyConGyroActivationMode =
            Switch2DualGyroActivationMode.Toggle;
        input.Switch2DualJoyConGyroLeftActivationButton =
            Switch2JoyConProfileButton.LeftPaddle1;
        input.Switch2DualJoyConGyroRightActivationButton =
            Switch2JoyConProfileButton.RightPaddle1;
        input.MapTo(store);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.IsTrue(output.Switch2DualJoyConGyroFusionEnabled);
        Assert.AreEqual(Switch2DualGyroDominantSide.None,
            output.Switch2DualJoyConGyroDominantSide);
        Assert.AreEqual(Switch2DualGyroMode.SingleSideToggle,
            output.Switch2DualJoyConGyroMode);
        Assert.AreEqual(Switch2DualGyroActivationMode.Toggle,
            output.Switch2DualJoyConGyroActivationMode);
        Assert.AreEqual(Switch2JoyConProfileButton.LeftPaddle1,
            output.Switch2DualJoyConGyroLeftActivationButton);
        Assert.AreEqual(Switch2JoyConProfileButton.RightPaddle1,
            output.Switch2DualJoyConGyroRightActivationButton);

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        output.SerializeAppAttrs = false;
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Switch2DualJoyConGyroMode>SingleSideToggle</Switch2DualJoyConGyroMode>");
        StringAssert.Contains(xml,
            "<Switch2DualJoyConGyroActivationMode>Toggle</Switch2DualJoyConGyroActivationMode>");
        StringAssert.Contains(xml,
            "<Switch2DualJoyConGyroLeftActivationButton>LeftPaddle1</Switch2DualJoyConGyroLeftActivationButton>");

        const string legacyDirectMerge = """
            <DS4Windows config_version="5">
              <Switch2DualJoyConGyroFusionEnabled>true</Switch2DualJoyConGyroFusionEnabled>
              <Switch2DualJoyConGyroDominantSide>None</Switch2DualJoyConGyroDominantSide>
            </DS4Windows>
            """;
        BackingStore migrated = DeserializeIntoStore(serializer,
            legacyDirectMerge);
        Assert.AreEqual(Switch2DualGyroMode.SingleSideToggle,
            migrated.switch2DualJoyConGyroMode[
                Global.TEST_PROFILE_INDEX]);
        Assert.AreEqual(Switch2DualGyroDominantSide.None,
            migrated.switch2DualJoyConGyroDominantSide[
                Global.TEST_PROFILE_INDEX]);

        input.Switch2DualJoyConGyroMode = Switch2DualGyroMode.SwitchGyroSide;
        input.Switch2DualJoyConGyroDominantSide =
            Switch2DualGyroDominantSide.None;
        input.Switch2DualJoyConGyroActivationMode =
            (Switch2DualGyroActivationMode)99;
        input.Switch2DualJoyConGyroLeftActivationButton =
            (Switch2JoyConProfileButton)(1u << 29);
        input.MapTo(store);
        Assert.AreEqual(Switch2DualGyroDominantSide.Right,
            store.switch2DualJoyConGyroDominantSide[0]);
        Assert.AreEqual(Switch2DualGyroActivationMode.Hold,
            store.switch2DualJoyConGyroActivationMode[0]);
        Assert.AreEqual(Switch2JoyConProfileButton.None,
            store.switch2DualJoyConGyroLeftActivationButton[0]);
    }

    [TestMethod]
    public void DualGyroMultipleFlagsAndIrRoundTripWithoutChangingLegacyFields()
    {
        var serializer = new XmlSerializer(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        foreach (var left in new[]
        {
            Switch2JoyConProfileButton.None,
            Switch2JoyConProfileButton.LeftPaddle1,
            Switch2JoyConProfileButton.LeftPaddle1 |
                Switch2JoyConProfileButton.LeftPaddle2 |
                Switch2JoyConProfileButton.LeftIrSensor,
        })
        {
            var right = Switch2JoyConProfileButton.C |
                Switch2JoyConProfileButton.RightIrSensor;
            var input = new ProfileDTO
            {
                DeviceIndex = Global.TEST_PROFILE_INDEX,
                SerializeAppAttrs = false,
                Switch2DualJoyConGyroLeftActivationButton = left,
                Switch2DualJoyConGyroRightActivationButton = right,
            };
            using var writer = new StringWriter();
            serializer.Serialize(writer, input);
            var store = DeserializeIntoStore(serializer, writer.ToString());
            Assert.AreEqual(left, store.switch2DualJoyConGyroLeftActivationButton[
                Global.TEST_PROFILE_INDEX]);
            Assert.AreEqual(right, store.switch2DualJoyConGyroRightActivationButton[
                Global.TEST_PROFILE_INDEX]);
            var output = new ProfileDTO { DeviceIndex = Global.TEST_PROFILE_INDEX };
            output.MapFrom(store);
            Assert.AreEqual(left, output.Switch2DualJoyConGyroLeftActivationButton);
            Assert.AreEqual(right, output.Switch2DualJoyConGyroRightActivationButton);
        }
    }

    [TestMethod]
    public void StandaloneJoyConHoldModeDefaultsVerticalAndRoundTrips()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.AreEqual(Switch2JoyConHoldMode.Vertical,
            store.switch2JoyConStandaloneHoldMode[0]);

        input.Switch2JoyConStandaloneHoldMode =
            Switch2JoyConHoldMode.Horizontal;
        input.MapTo(store);
        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.AreEqual(Switch2JoyConHoldMode.Horizontal,
            output.Switch2JoyConStandaloneHoldMode);

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        output.SerializeAppAttrs = false;
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Switch2JoyConStandaloneHoldMode>Horizontal</Switch2JoyConStandaloneHoldMode>");
        using (var reader = new StringReader(xml))
        {
            var deserialized = (ProfileDTO)serializer.Deserialize(reader);
            Assert.AreEqual(Switch2JoyConHoldMode.Horizontal,
                deserialized.Switch2JoyConStandaloneHoldMode);
        }

        input.Switch2JoyConStandaloneHoldMode =
            (Switch2JoyConHoldMode)99;
        input.MapTo(store);
        Assert.AreEqual(Switch2JoyConHoldMode.Vertical,
            store.switch2JoyConStandaloneHoldMode[0]);
    }

    [TestMethod]
    public void AutoDisconnectDefaultsToLegacyAndRoundTripsAbsolutePolicy()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.AreEqual(Switch2AutoDisconnectMode.LegacyProfile,
            store.switch2AutoDisconnectMode[0]);
        Assert.AreEqual(0L,
            store.switch2AutoDisconnectTimeoutSeconds[0]);

        input.Switch2AutoDisconnectMode =
            Switch2AutoDisconnectMode.Absolute;
        input.Switch2AutoDisconnectTimeoutSeconds =
            Switch2AutoDisconnectPolicyResolver.ComposeTimeoutSeconds(
                days: 2, hours: 3, minutes: 4);
        input.MapTo(store);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.AreEqual(Switch2AutoDisconnectMode.Absolute,
            output.Switch2AutoDisconnectMode);
        Assert.AreEqual(183_840L,
            output.Switch2AutoDisconnectTimeoutSeconds);

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        output.SerializeAppAttrs = false;
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Switch2AutoDisconnectMode>Absolute</Switch2AutoDisconnectMode>");
        StringAssert.Contains(xml,
            "<Switch2AutoDisconnectTimeoutSeconds>183840</Switch2AutoDisconnectTimeoutSeconds>");

        using var reader = new StringReader(xml);
        var restored = (ProfileDTO)serializer.Deserialize(reader);
        Assert.AreEqual(Switch2AutoDisconnectMode.Absolute,
            restored.Switch2AutoDisconnectMode);
        Assert.AreEqual(183_840L,
            restored.Switch2AutoDisconnectTimeoutSeconds);

        input.Switch2AutoDisconnectMode =
            (Switch2AutoDisconnectMode)99;
        input.Switch2AutoDisconnectTimeoutSeconds = -1;
        input.MapTo(store);
        Assert.AreEqual(Switch2AutoDisconnectMode.LegacyProfile,
            store.switch2AutoDisconnectMode[0]);
        Assert.AreEqual(0L,
            store.switch2AutoDisconnectTimeoutSeconds[0]);
    }

    [TestMethod]
    public void AutoDisconnectTimeComponentsAreBoundedAndLossless()
    {
        long seconds = Switch2AutoDisconnectPolicyResolver.
            ComposeTimeoutSeconds(days: 12, hours: 23, minutes: 59);
        Switch2AutoDisconnectPolicyResolver.DecomposeTimeoutSeconds(seconds,
            out long days, out int hours, out int minutes);
        Assert.AreEqual(12L, days);
        Assert.AreEqual(23, hours);
        Assert.AreEqual(59, minutes);
        Assert.AreEqual(seconds,
            Switch2AutoDisconnectPolicyResolver.ComposeTimeoutSeconds(
                days, hours, minutes));
        Assert.AreEqual(0L,
            Switch2AutoDisconnectPolicyResolver.ComposeTimeoutSeconds(
                days: -1, hours: 0, minutes: 0));
        Assert.AreEqual(0L,
            Switch2AutoDisconnectPolicyResolver.ComposeTimeoutSeconds(
                days: 0, hours: 24, minutes: 0));
        Assert.AreEqual(long.MaxValue,
            Switch2AutoDisconnectPolicyResolver.ComposeTimeoutSeconds(
                long.MaxValue, hours: 23, minutes: 59));
    }

    [TestMethod]
    public void FaceButtonLayoutDefaultsXboxRoundTripsAndRejectsUnknownValues()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.AreEqual(Switch2FaceButtonLayout.Xbox,
            store.switch2FaceButtonLayout[0]);

        input.Switch2FaceButtonLayout = Switch2FaceButtonLayout.Nintendo;
        input.MapTo(store);
        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.AreEqual(Switch2FaceButtonLayout.Nintendo,
            output.Switch2FaceButtonLayout);

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        output.SerializeAppAttrs = false;
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Switch2FaceButtonLayout>Nintendo</Switch2FaceButtonLayout>");

        input.Switch2FaceButtonLayout = (Switch2FaceButtonLayout)99;
        input.MapTo(store);
        Assert.AreEqual(Switch2FaceButtonLayout.Xbox,
            store.switch2FaceButtonLayout[0]);
    }

    [TestMethod]
    public void JoyConIrMouseIsOptInAndItsProfilePolicyRoundTrips()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.IsFalse(store.switch2JoyConIrMouseEnabled[0]);
        Assert.AreEqual(Switch2IrMouseSource.Auto,
            store.switch2JoyConIrMouseSource[0]);
        Assert.AreEqual(Switch2IrMouseScrollMode.Vertical,
            store.switch2JoyConIrMouseScrollMode[0]);
        Assert.AreEqual(Switch2StickScrollActivationMode.Hold,
            store.switch2LeftStickScrollActivationMode[0]);
        Assert.AreEqual(Switch2StickScrollActivationMode.Hold,
            store.switch2RightStickScrollActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2LeftStickUpActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2LeftStickDownActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2LeftStickLeftActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2LeftStickRightActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2RightStickUpActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2RightStickDownActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2RightStickLeftActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2RightStickRightActivationMode[0]);
        Assert.AreEqual(Switch2IrActivationThreshold.Strict,
            store.switch2JoyConLeftIrMouseActivationThreshold[0]);
        Assert.AreEqual(Switch2IrMouseProjection.DefaultSensitivity,
            store.switch2JoyConLeftIrMouseSensitivity[0]);
        Assert.AreEqual(Switch2IrActivationThreshold.Strict,
            store.switch2JoyConRightIrMouseActivationThreshold[0]);
        Assert.AreEqual(Switch2IrMouseProjection.DefaultSensitivity,
            store.switch2JoyConRightIrMouseSensitivity[0]);

        input.Switch2JoyConIrMouseEnabled = true;
        input.Switch2JoyConIrMouseSource = Switch2IrMouseSource.Both;
        input.Switch2JoyConIrMouseScrollMode =
            Switch2IrMouseScrollMode.FourWay;
        input.Switch2LeftStickScrollActivationMode =
            Switch2StickScrollActivationMode.Tap;
        input.Switch2RightStickScrollActivationMode =
            Switch2StickScrollActivationMode.Hold;
        input.Switch2LeftStickUpActivationMode =
            Switch2StickDirectionActivationMode.Tap;
        input.Switch2LeftStickDownActivationMode =
            Switch2StickDirectionActivationMode.Hold;
        input.Switch2LeftStickLeftActivationMode =
            Switch2StickDirectionActivationMode.Tap;
        input.Switch2LeftStickRightActivationMode =
            Switch2StickDirectionActivationMode.Hold;
        input.Switch2RightStickUpActivationMode =
            Switch2StickDirectionActivationMode.Hold;
        input.Switch2RightStickDownActivationMode =
            Switch2StickDirectionActivationMode.Tap;
        input.Switch2RightStickLeftActivationMode =
            Switch2StickDirectionActivationMode.Hold;
        input.Switch2RightStickRightActivationMode =
            Switch2StickDirectionActivationMode.Tap;
        input.Switch2JoyConLeftIrMouseActivationThreshold =
            Switch2IrActivationThreshold.Relaxed;
        input.Switch2JoyConLeftIrMouseSensitivity = 7.5;
        input.Switch2JoyConRightIrMouseActivationThreshold =
            Switch2IrActivationThreshold.Balanced;
        input.Switch2JoyConRightIrMouseSensitivity = 2.5;
        input.MapTo(store);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.IsTrue(output.Switch2JoyConIrMouseEnabled);
        Assert.AreEqual(Switch2IrMouseSource.Both,
            output.Switch2JoyConIrMouseSource);
        Assert.AreEqual(Switch2IrMouseScrollMode.FourWay,
            output.Switch2JoyConIrMouseScrollMode);
        Assert.AreEqual(Switch2StickScrollActivationMode.Tap,
            output.Switch2LeftStickScrollActivationMode);
        Assert.AreEqual(Switch2StickScrollActivationMode.Hold,
            output.Switch2RightStickScrollActivationMode);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Tap,
            output.Switch2LeftStickUpActivationMode);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            output.Switch2LeftStickDownActivationMode);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Tap,
            output.Switch2LeftStickLeftActivationMode);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            output.Switch2LeftStickRightActivationMode);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            output.Switch2RightStickUpActivationMode);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Tap,
            output.Switch2RightStickDownActivationMode);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            output.Switch2RightStickLeftActivationMode);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Tap,
            output.Switch2RightStickRightActivationMode);
        Assert.AreEqual(Switch2IrActivationThreshold.Relaxed,
            output.Switch2JoyConLeftIrMouseActivationThreshold);
        Assert.AreEqual(7.5, output.Switch2JoyConLeftIrMouseSensitivity);
        Assert.AreEqual(Switch2IrActivationThreshold.Balanced,
            output.Switch2JoyConRightIrMouseActivationThreshold);
        Assert.AreEqual(2.5, output.Switch2JoyConRightIrMouseSensitivity);

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        output.SerializeAppAttrs = false;
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml,
            "<Switch2LeftStickUpActivationMode>Tap</Switch2LeftStickUpActivationMode>");
        StringAssert.Contains(xml,
            "<Switch2RightStickDownActivationMode>Tap</Switch2RightStickDownActivationMode>");
        StringAssert.Contains(xml,
            "<Switch2RightStickRightActivationMode>Tap</Switch2RightStickRightActivationMode>");
    }

    [TestMethod]
    public void StickAssistIsOptInAndItsSensitivityRoundTrips()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.AreEqual(Switch2StickAssistProfileLane.DefaultSensitivity,
            store.switch2GyroMouseStickAssistSensitivity[0]);
        Assert.AreEqual(Switch2MappedStickMouseSensitivity.Default,
            store.switch2LeftStickMouseSensitivity[0]);
        Assert.AreEqual(Switch2MappedStickMouseSensitivity.Default,
            store.switch2RightStickMouseSensitivity[0]);

        input.Switch2GyroMouseStickAssistSensitivity = 6.4;
        input.Switch2LeftStickMouseSensitivity = 2.4;
        input.Switch2RightStickMouseSensitivity = 8.6;
        input.MapTo(store);
        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.AreEqual(6.4,
            output.Switch2GyroMouseStickAssistSensitivity);
        Assert.AreEqual(2.4, output.Switch2LeftStickMouseSensitivity);
        Assert.AreEqual(8.6, output.Switch2RightStickMouseSensitivity);

        input.Switch2GyroMouseStickAssistSensitivity = double.NaN;
        input.MapTo(store);
        Assert.AreEqual(Switch2StickAssistProfileLane.DefaultSensitivity,
            store.switch2GyroMouseStickAssistSensitivity[0]);

        input.Switch2LeftStickMouseSensitivity = double.NaN;
        input.Switch2RightStickMouseSensitivity = 10.1;
        input.MapTo(store);
        Assert.AreEqual(Switch2MappedStickMouseSensitivity.Default,
            store.switch2LeftStickMouseSensitivity[0]);
        Assert.AreEqual(Switch2MappedStickMouseSensitivity.Default,
            store.switch2RightStickMouseSensitivity[0]);
        input.Switch2GyroMouseStickAssistSensitivity = 10.1;
        input.MapTo(store);
        Assert.AreEqual(Switch2StickAssistProfileLane.DefaultSensitivity,
            store.switch2GyroMouseStickAssistSensitivity[0]);
    }

    [TestMethod]
    public void InvalidJoyConIrMouseProfileValuesFailToSafeDefaults()
    {
        BackingStore store = new();
        ProfileDTO input = new()
        {
            DeviceIndex = 0,
            Switch2JoyConIrMouseSource = (Switch2IrMouseSource)99,
            Switch2JoyConIrMouseScrollMode =
                (Switch2IrMouseScrollMode)99,
            Switch2LeftStickScrollActivationMode =
                (Switch2StickScrollActivationMode)99,
            Switch2RightStickScrollActivationMode =
                (Switch2StickScrollActivationMode)99,
            Switch2LeftStickUpActivationMode =
                (Switch2StickDirectionActivationMode)99,
            Switch2LeftStickDownActivationMode =
                (Switch2StickDirectionActivationMode)99,
            Switch2LeftStickLeftActivationMode =
                (Switch2StickDirectionActivationMode)99,
            Switch2LeftStickRightActivationMode =
                (Switch2StickDirectionActivationMode)99,
            Switch2RightStickUpActivationMode =
                (Switch2StickDirectionActivationMode)99,
            Switch2RightStickDownActivationMode =
                (Switch2StickDirectionActivationMode)99,
            Switch2RightStickLeftActivationMode =
                (Switch2StickDirectionActivationMode)99,
            Switch2RightStickRightActivationMode =
                (Switch2StickDirectionActivationMode)99,
            Switch2JoyConLeftIrMouseActivationThreshold =
                (Switch2IrActivationThreshold)99,
            Switch2JoyConLeftIrMouseSensitivity = double.NaN,
            Switch2JoyConRightIrMouseActivationThreshold =
                (Switch2IrActivationThreshold)99,
            Switch2JoyConRightIrMouseSensitivity = double.PositiveInfinity,
        };

        input.MapTo(store);

        Assert.AreEqual(Switch2IrMouseSource.Auto,
            store.switch2JoyConIrMouseSource[0]);
        Assert.AreEqual(Switch2IrMouseScrollMode.Vertical,
            store.switch2JoyConIrMouseScrollMode[0]);
        Assert.AreEqual(Switch2StickScrollActivationMode.Hold,
            store.switch2LeftStickScrollActivationMode[0]);
        Assert.AreEqual(Switch2StickScrollActivationMode.Hold,
            store.switch2RightStickScrollActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2LeftStickUpActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2LeftStickDownActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2LeftStickLeftActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2LeftStickRightActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2RightStickUpActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2RightStickDownActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2RightStickLeftActivationMode[0]);
        Assert.AreEqual(Switch2StickDirectionActivationMode.Hold,
            store.switch2RightStickRightActivationMode[0]);
        Assert.AreEqual(Switch2IrActivationThreshold.Strict,
            store.switch2JoyConLeftIrMouseActivationThreshold[0]);
        Assert.AreEqual(Switch2IrMouseProjection.DefaultSensitivity,
            store.switch2JoyConLeftIrMouseSensitivity[0]);
        Assert.AreEqual(Switch2IrActivationThreshold.Strict,
            store.switch2JoyConRightIrMouseActivationThreshold[0]);
        Assert.AreEqual(Switch2IrMouseProjection.DefaultSensitivity,
            store.switch2JoyConRightIrMouseSensitivity[0]);

        input.Switch2JoyConLeftIrMouseSensitivity = 10.01;
        input.Switch2JoyConRightIrMouseSensitivity = 0.99;
        input.MapTo(store);
        Assert.AreEqual(Switch2IrMouseProjection.DefaultSensitivity,
            store.switch2JoyConLeftIrMouseSensitivity[0]);
        Assert.AreEqual(Switch2IrMouseProjection.DefaultSensitivity,
            store.switch2JoyConRightIrMouseSensitivity[0]);
    }

    [TestMethod]
    public void JoyConIrGyroTuningDefaultsAndRoundTripsPerSensor()
    {
        BackingStore store = new();
        ProfileDTO input = new() { DeviceIndex = 0 };

        input.MapTo(store);
        Assert.AreEqual(Switch2IrGyroTuning.Default,
            store.switch2JoyConLeftIrGyroTuning[0]);
        Assert.AreEqual(Switch2IrGyroTuning.Default,
            store.switch2JoyConRightIrGyroTuning[0]);

        input.Switch2JoyConLeftIrGyroTuning = new()
        {
            DeadzoneButtons = Switch2JoyConProfileButton.LeftTrigger |
                Switch2JoyConProfileButton.FaceSouth,
            DeadzoneAmount = 12.5,
            PauseAfterPressedMilliseconds = 40,
            PauseAfterReleasedMilliseconds = 50,
            DeadzoneEffectAfterReleasedMilliseconds = 60,
            DampeningButtons = Switch2JoyConProfileButton.RightTrigger,
            DampeningAmountPercent = 75,
            DampeningEffectAfterReleasedMilliseconds = 80,
        };
        input.Switch2JoyConRightIrGyroTuning = new()
        {
            DeadzoneButtons = Switch2JoyConProfileButton.RightShoulder,
            DeadzoneAmount = 22.5,
            PauseAfterPressedMilliseconds = 140,
            PauseAfterReleasedMilliseconds = 150,
            DeadzoneEffectAfterReleasedMilliseconds = 160,
            DampeningButtons = Switch2JoyConProfileButton.LeftShoulder,
            DampeningAmountPercent = 65,
            DampeningEffectAfterReleasedMilliseconds = 180,
        };
        input.MapTo(store);

        Assert.AreEqual(12.5,
            store.switch2JoyConLeftIrGyroTuning[0].DeadzoneAmount);
        Assert.AreEqual(65.0,
            store.switch2JoyConRightIrGyroTuning[0].
                DampeningAmountPercent);

        ProfileDTO output = new() { DeviceIndex = 0 };
        output.MapFrom(store);
        Assert.AreEqual(40, output.Switch2JoyConLeftIrGyroTuning.
            PauseAfterPressedMilliseconds);
        Assert.AreEqual(Switch2JoyConProfileButton.LeftShoulder,
            output.Switch2JoyConRightIrGyroTuning.DampeningButtons);

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        output.SerializeAppAttrs = false;
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            xml = writer.ToString();
        }
        StringAssert.Contains(xml, "<Switch2JoyConLeftIrGyroTuning>");
        StringAssert.Contains(xml, "<DeadzoneAmount>12.5</DeadzoneAmount>");
        StringAssert.Contains(xml,
            "<DampeningEffectAfterReleasedMilliseconds>180</DampeningEffectAfterReleasedMilliseconds>");
        using (var reader = new StringReader(xml))
        {
            var roundTrip = (ProfileDTO)serializer.Deserialize(reader);
            Assert.AreEqual(75.0, roundTrip.
                Switch2JoyConLeftIrGyroTuning.DampeningAmountPercent);
        }

        using (var reader = new StringReader(
                   "<DS4Windows config_version=\"5\" />"))
        {
            var legacy = (ProfileDTO)serializer.Deserialize(reader);
            Assert.AreEqual(Switch2IrGyroMotionModifier.
                DefaultDeadzoneAmount,
                legacy.Switch2JoyConLeftIrGyroTuning.DeadzoneAmount);
            Assert.AreEqual(Switch2IrGyroMotionModifier.
                DefaultDampeningAmountPercent,
                legacy.Switch2JoyConRightIrGyroTuning.
                    DampeningAmountPercent);
        }
    }

    [TestMethod]
    public void InvalidJoyConIrGyroTuningFailsToBoundedDefaults()
    {
        BackingStore store = new();
        ProfileDTO input = new()
        {
            DeviceIndex = 0,
            Switch2JoyConLeftIrGyroTuning = new()
            {
                DeadzoneButtons = (Switch2JoyConProfileButton)(1u << 31),
                DeadzoneAmount = double.NaN,
                PauseAfterPressedMilliseconds = -1,
                PauseAfterReleasedMilliseconds = 60_001,
                DeadzoneEffectAfterReleasedMilliseconds = -5,
                DampeningButtons = (Switch2JoyConProfileButton)(1u << 30),
                DampeningAmountPercent = 101,
                DampeningEffectAfterReleasedMilliseconds = int.MaxValue,
            },
        };

        input.MapTo(store);
        Assert.AreEqual(Switch2IrGyroTuning.Default,
            store.switch2JoyConLeftIrGyroTuning[0]);
    }

    [TestMethod]
    public void ExistingControlNumbersRemainStableAndNewControlsAreAppended()
    {
        string legacyNames =
            "None LXNeg LXPos LYNeg LYPos RXNeg RXPos RYNeg RYPos " +
            "L1 L2 L3 R1 R2 R3 Square Triangle Circle Cross " +
            "DpadUp DpadRight DpadDown DpadLeft PS TouchLeft " +
            "TouchUpper TouchMulti TouchRight Share Options Mute FnL FnR " +
            "BLP BRP GyroXPos GyroXNeg GyroZPos GyroZNeg SwipeLeft " +
            "SwipeRight SwipeUp SwipeDown L2FullPull R2FullPull " +
            "GyroSwipeLeft GyroSwipeRight GyroSwipeUp GyroSwipeDown " +
            "Capture SideL SideR LSOuter RSOuter TouchStarted TouchEnded";

        string[] names = legacyNames.Split(' ');
        for (int expectedValue = 0; expectedValue < names.Length;
            expectedValue++)
        {
            Assert.IsTrue(Enum.TryParse(names[expectedValue],
                out DS4Controls control));
            Assert.AreEqual(expectedValue, (int)control,
                $"The serialized value of {names[expectedValue]} changed.");
        }

        Assert.AreEqual(56, (int)DS4Controls.Switch2C);
        Assert.AreEqual(57,
            (int)DS4Controls.Switch2JoyConLeftPaddle1);
        Assert.AreEqual(58,
            (int)DS4Controls.Switch2JoyConLeftPaddle2);
        Assert.AreEqual(59,
            (int)DS4Controls.Switch2JoyConRightPaddle1);
        Assert.AreEqual(60,
            (int)DS4Controls.Switch2JoyConRightPaddle2);
        Assert.AreEqual(61,
            (int)DS4Controls.Switch2JoyConLeftIrSensor);
        Assert.AreEqual(62,
            (int)DS4Controls.Switch2JoyConRightIrSensor);
        Assert.AreEqual(63, (int)DS4Controls.Switch2JoyConLeftSL);
        Assert.AreEqual(64, (int)DS4Controls.Switch2JoyConLeftSR);
        Assert.AreEqual(65, (int)DS4Controls.Switch2JoyConRightSL);
        Assert.AreEqual(66, (int)DS4Controls.Switch2JoyConRightSR);
        Assert.AreEqual(67, Enum.GetValues<DS4Controls>().Length);
        Assert.AreEqual(5, Global.CONFIG_VERSION,
            "An append-only named XML extension does not require a profile migration version bump.");
    }

    [TestMethod]
    public void MappingTablesSettingsDefaultsAndNamesHaveMatchingCardinality()
    {
        DS4Controls[] allControls = Enum.GetValues<DS4Controls>();
        int cardinality = allControls.Length;
        var store = new BackingStore();
        List<DS4ControlSettings> settings =
            store.ds4settings[Global.TEST_PROFILE_INDEX];
        var fieldMapping = new DS4StateFieldMapping();

        Assert.AreEqual(cardinality, Global.defaultButtonMapping.Length);
        Assert.AreEqual(cardinality, Global.reverseX360ButtonMapping.Length);
        Assert.AreEqual(cardinality, DS4StateFieldMapping.mappedType.Length);
        Assert.AreEqual(cardinality, fieldMapping.buttons.Length);
        Assert.AreEqual(cardinality, fieldMapping.axisdirs.Length);
        Assert.AreEqual(cardinality, fieldMapping.triggers.Length);
        Assert.AreEqual(cardinality, fieldMapping.gryodirs.Length);
        Assert.AreEqual(cardinality, fieldMapping.swipedirs.Length);
        Assert.AreEqual(cardinality, fieldMapping.swipedirbools.Length);
        Assert.AreEqual(cardinality - 1,
            DS4ControlSettings.LAST_DS4_ACTION);
        Assert.AreEqual(cardinality - 1,
            DS4StateFieldMapping.LAST_DS4_ACTION);
        Assert.AreEqual(cardinality - 1, Global.ds4inputNames.Count);
        Assert.AreEqual(cardinality - 1, settings.Count);
        CollectionAssert.AreEquivalent(
            allControls.Where(control => control != DS4Controls.None)
                .ToArray(),
            Global.ds4inputNames.Keys.ToArray());

        for (int index = 1; index < cardinality; index++)
        {
            Assert.AreEqual(allControls[index], settings[index - 1].control,
                $"Settings order diverged at DS4Controls value {index}.");
        }

        foreach (DS4Controls control in Switch2SourceControls)
        {
            Assert.AreEqual(X360Controls.None,
                Global.defaultButtonMapping[(int)control]);
            Assert.AreEqual(DS4StateFieldMapping.ControlType.Button,
                DS4StateFieldMapping.mappedType[(int)control]);
            Assert.IsFalse(string.IsNullOrWhiteSpace(
                Global.ds4inputNames[control]));

            DS4ControlSettings setting = settings[(int)control - 1];
            Assert.AreEqual(DS4ControlSettings.ActionType.Default,
                setting.actionType);
            Assert.AreEqual(DS4ControlSettings.ActionType.Default,
                setting.shiftActionType);
            Assert.AreEqual(1,
                store.ds4controlSettings[Global.TEST_PROFILE_INDEX]
                    .ControlButtons.Count(item => item.control == control),
                $"{control} must be processed exactly once by the existing button mapping loop.");
            Assert.AreEqual((int)control, Mapping.DS4ControltoInt(control),
                $"{control} must have a distinct macro execution slot.");
        }

        CollectionAssert.AreEquivalent(
            Enumerable.Range(0, cardinality).ToArray(),
            allControls.Select(Mapping.DS4ControltoInt).ToArray(),
            "Every profile source needs one collision-free macro execution slot.");
        Assert.AreEqual(Switch2SourceControls.Length,
            Switch2SourceControls.Select(control =>
                    Global.ds4inputNames[control])
                .Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void EveryValidatedRawSemanticMapsToExactlyOneSourceControl()
    {
        var cases = new (DS4Controls Expected, DS4State State)[]
        {
            (DS4Controls.Switch2C, ProState(cButton: true)),
            (DS4Controls.Switch2C, JoyConState(cButton: true)),
            (DS4Controls.Switch2JoyConLeftPaddle1,
                JoyConState(leftPaddle1: true)),
            (DS4Controls.Switch2JoyConLeftPaddle2,
                JoyConState(leftPaddle2: true)),
            (DS4Controls.Switch2JoyConRightPaddle1,
                JoyConState(rightPaddle1: true)),
            (DS4Controls.Switch2JoyConRightPaddle2,
                JoyConState(rightPaddle2: true)),
            (DS4Controls.Switch2JoyConLeftIrSensor,
                JoyConState(leftIrActive: true)),
            (DS4Controls.Switch2JoyConRightIrSensor,
                JoyConState(rightIrActive: true)),
            (DS4Controls.Switch2JoyConLeftSL, JoyConState(leftSL: true)),
            (DS4Controls.Switch2JoyConLeftSR, JoyConState(leftSR: true)),
            (DS4Controls.Switch2JoyConRightSL, JoyConState(rightSL: true)),
            (DS4Controls.Switch2JoyConRightSR, JoyConState(rightSR: true)),
        };

        foreach ((DS4Controls expected, DS4State state) in cases)
        {
            DS4StateFieldMapping fieldMapping = Populate(state);
            DS4Controls[] active = Switch2SourceControls
                .Where(control => fieldMapping.buttons[(int)control])
                .ToArray();

            CollectionAssert.AreEqual(new[] { expected }, active,
                $"The raw {expected} semantic must map exactly once.");
            Assert.IsFalse(fieldMapping.buttons[(int)DS4Controls.Mute],
                "Switch 2 C must never alias the DualSense Mute source.");

            foreach (DS4Controls control in Switch2SourceControls)
            {
                Assert.AreEqual(control == expected,
                    Mapping.GetBoolMappingExternal(0, control, state,
                        new DS4StateExposed(state), null));
            }
        }
    }

    [TestMethod]
    public void JoyConIrSourceButtonsUseTheirOwnProfileThresholds()
    {
        DS4State state = JoyConState(leftIrActive: true,
            rightIrActive: true, irDistance: 1_200, irRoughness: 4_500);
        var mapping = new DS4StateFieldMapping();

        mapping.PopulateFieldMapping(state, new DS4StateExposed(state), null,
            leftIrThreshold: Switch2IrActivationThreshold.Strict,
            rightIrThreshold: Switch2IrActivationThreshold.Balanced);

        Assert.IsFalse(mapping.buttons[
            (int)DS4Controls.Switch2JoyConLeftIrSensor]);
        Assert.IsTrue(mapping.buttons[
            (int)DS4Controls.Switch2JoyConRightIrSensor]);
        Assert.IsFalse(DS4StateFieldMapping.GetValidatedSwitch2SourceButton(
            state, DS4Controls.Switch2JoyConLeftIrSensor,
            Switch2IrActivationThreshold.Strict,
            Switch2IrActivationThreshold.Balanced));
        Assert.IsTrue(DS4StateFieldMapping.GetValidatedSwitch2SourceButton(
            state, DS4Controls.Switch2JoyConRightIrSensor,
            Switch2IrActivationThreshold.Strict,
            Switch2IrActivationThreshold.Balanced));
    }

    [TestMethod]
    public void InvalidAmbiguousDefaultAndStaleSidecarsAlwaysMapReleased()
    {
        DS4State[] invalidStates =
        [
            new DS4State(),
            ProState(cButton: true, isValid: false),
            ProState(cButton: true,
                contractVersion: (ushort)(ProSourceContractVersion + 1)),
            JoyConState(cButton: true, leftPaddle1: true,
                leftPaddle2: true, rightPaddle1: true,
                rightPaddle2: true, leftIrActive: true,
                rightIrActive: true, isValid: false),
            JoyConState(cButton: true, leftPaddle1: true,
                leftPaddle2: true, rightPaddle1: true,
                rightPaddle2: true, leftIrActive: true,
                rightIrActive: true,
                contractVersion: (ushort)(JoyConSourceContractVersion + 1)),
            BothSourcesState(),
        ];

        foreach (DS4State state in invalidStates)
        {
            AssertAllSwitch2SourcesReleased(Populate(state));
            foreach (DS4Controls control in Switch2SourceControls)
            {
                Assert.IsFalse(Mapping.GetBoolMappingExternal(0, control,
                    state, new DS4StateExposed(state), null));
            }
        }

        var reused = new DS4StateFieldMapping();
        DS4State valid = JoyConState(rightPaddle2: true);
        reused.PopulateFieldMapping(valid, new DS4StateExposed(valid), null);
        Assert.IsTrue(reused.buttons[
            (int)DS4Controls.Switch2JoyConRightPaddle2]);

        DS4State stale = JoyConState(rightPaddle2: true, isValid: false);
        reused.PopulateFieldMapping(stale, new DS4StateExposed(stale), null);
        AssertAllSwitch2SourcesReleased(reused);
    }

    [TestMethod]
    public void NewControlsRoundTripByNameWithoutChangingLegacyMappings()
    {
        const string xml = """
            <DS4Windows config_version="5">
              <Control>
                <Button>
                  <Mute>A Button</Mute>
                  <Switch2C>B Button</Switch2C>
                  <Switch2JoyConLeftPaddle1>X Button</Switch2JoyConLeftPaddle1>
                  <Switch2JoyConLeftPaddle2>Y Button</Switch2JoyConLeftPaddle2>
                  <Switch2JoyConRightPaddle1>Left Bumper</Switch2JoyConRightPaddle1>
                  <Switch2JoyConRightPaddle2>Right Bumper</Switch2JoyConRightPaddle2>
                  <Switch2JoyConLeftIrSensor>Mouse Wheel Up</Switch2JoyConLeftIrSensor>
                  <Switch2JoyConRightIrSensor>Mouse Wheel Down</Switch2JoyConRightIrSensor>
                  <Switch2JoyConLeftSL>A Button</Switch2JoyConLeftSL>
                  <Switch2JoyConLeftSR>B Button</Switch2JoyConLeftSR>
                  <Switch2JoyConRightSL>X Button</Switch2JoyConRightSL>
                  <Switch2JoyConRightSR>Y Button</Switch2JoyConRightSR>
                </Button>
              </Control>
              <ShiftControl />
            </DS4Windows>
            """;

        XmlSerializer serializer = new(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        BackingStore firstStore = DeserializeIntoStore(serializer, xml);

        X360Controls[] expectedOutputs =
        [
            X360Controls.B,
            X360Controls.X,
            X360Controls.Y,
            X360Controls.LB,
            X360Controls.RB,
            X360Controls.WUP,
            X360Controls.WDOWN,
            X360Controls.A,
            X360Controls.B,
            X360Controls.X,
            X360Controls.Y,
        ];

        AssertButton(firstStore, DS4Controls.Mute, X360Controls.A);
        for (int index = 0; index < Switch2SourceControls.Length; index++)
        {
            AssertButton(firstStore, Switch2SourceControls[index],
                expectedOutputs[index]);
        }

        ProfileDTO output = new()
        {
            DeviceIndex = Global.TEST_PROFILE_INDEX,
            SerializeAppAttrs = false,
        };
        output.MapFrom(firstStore);

        string serialized;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, output);
            serialized = writer.ToString();
        }

        StringAssert.Contains(serialized, "<Mute>A Button</Mute>");
        foreach (DS4Controls control in Switch2SourceControls)
        {
            StringAssert.Contains(serialized, $"<{control}>");
        }

        BackingStore secondStore = DeserializeIntoStore(serializer,
            serialized);
        AssertButton(secondStore, DS4Controls.Mute, X360Controls.A);
        for (int index = 0; index < Switch2SourceControls.Length; index++)
        {
            AssertButton(secondStore, Switch2SourceControls[index],
                expectedOutputs[index]);
        }
    }

    [TestMethod]
    public void MappingListShowsEachSourceOnceAndRequiresExactLiveRuntime()
    {
        MappingListViewModel offline = new(Global.TEST_PROFILE_INDEX,
            OutContType.ViiperX360);
        foreach (DS4Controls control in Switch2SourceControls)
        {
            Assert.AreEqual(1,
                offline.Mappings.Count(item => item.Control == control));
            Assert.IsTrue(offline.ControlMap[control]
                .IsAvailableOnPhysicalController);
            Assert.IsTrue(offline.ControlMap[control].IsControllerMapListOnly);
            Assert.AreEqual(Global.ds4inputNames[control],
                offline.ControlMap[control].ControlName);
        }

        foreach (InputDeviceType currentDeviceType in new[]
        {
            InputDeviceType.SwitchPro,
            InputDeviceType.JoyConL,
            InputDeviceType.JoyConR,
            InputDeviceType.JoyConGrip,
            InputDeviceType.DS4,
            InputDeviceType.DualSense,
        })
        {
            MappingListViewModel liveContext = new(
                Global.TEST_PROFILE_INDEX, OutContType.ViiperX360,
                currentDeviceType);
            AssertAllUnavailable(liveContext);
        }

        var exactRuntimeControls = new[]
        {
            (InputDeviceType.Switch2Pro, new[]
            {
                DS4Controls.Switch2C,
            }),
            (InputDeviceType.Switch2JoyConLeft, new[]
            {
                DS4Controls.Switch2JoyConLeftPaddle1,
                DS4Controls.Switch2JoyConLeftPaddle2,
                DS4Controls.Switch2JoyConLeftIrSensor,
                DS4Controls.Switch2JoyConLeftSL,
                DS4Controls.Switch2JoyConLeftSR,
            }),
            (InputDeviceType.Switch2JoyConRight, new[]
            {
                DS4Controls.Switch2C,
                DS4Controls.Switch2JoyConRightPaddle1,
                DS4Controls.Switch2JoyConRightPaddle2,
                DS4Controls.Switch2JoyConRightIrSensor,
                DS4Controls.Switch2JoyConRightSL,
                DS4Controls.Switch2JoyConRightSR,
            }),
            (InputDeviceType.Switch2JoyConJoined, Switch2SourceControls),
        };
        foreach ((InputDeviceType exactRuntimeType,
                     DS4Controls[] availableControls) in exactRuntimeControls)
        {
            MappingListViewModel liveContext = new(
                Global.TEST_PROFILE_INDEX, OutContType.ViiperX360,
                exactRuntimeType);
            foreach (DS4Controls control in Switch2SourceControls)
            {
                bool expected = availableControls.Contains(control);
                Assert.AreEqual(expected, liveContext.ControlMap[control]
                    .IsAvailableOnPhysicalController, control.ToString());
                Assert.AreEqual(expected, liveContext.ControlMap[control]
                    .IsControllerMapListOnly, control.ToString());
            }
        }

        // Even a matching VID/PID is not the complete validated identity: the
        // Pro USB contract also requires bcdDevice 0201, while BLE requires the
        // admitted GATT service/characteristic/property tuple. That provenance
        // does not reach today's profile editor, so it must remain unavailable.
        ControllerUiCapabilities[] superficiallyMatchingIdentities =
        [
            ControllerUiCapabilities.For(InputDeviceType.SwitchPro,
                ConnectionType.USB,
                Switch2AdvertisementCodec.NintendoUsbVendorId,
                Switch2AdvertisementCodec.ProController2ProductId),
            ControllerUiCapabilities.For(InputDeviceType.JoyConL,
                ConnectionType.BT,
                Switch2AdvertisementCodec.NintendoUsbVendorId,
                Switch2AdvertisementCodec.JoyCon2LeftProductId),
            ControllerUiCapabilities.For(InputDeviceType.JoyConR,
                ConnectionType.BT,
                Switch2AdvertisementCodec.NintendoUsbVendorId,
                Switch2AdvertisementCodec.JoyCon2RightProductId),
        ];
        foreach (ControllerUiCapabilities capabilities in
            superficiallyMatchingIdentities)
        {
            foreach (DS4Controls control in Switch2SourceControls)
            {
                Assert.IsFalse(capabilities.IsMappingControlAvailable(
                    control, isDualSenseEdge: false));
            }
        }
    }

    private static DS4State ProState(bool cButton = false,
        bool isValid = true,
        ushort contractVersion = ProSourceContractVersion)
    {
        return new DS4State
        {
            Switch2RawInputStatus = new Switch2RawInputStatus
            {
                IsValid = isValid,
                ContractVersion = contractVersion,
                CButton = cButton,
            },
        };
    }

    private static DS4State JoyConState(bool cButton = false,
        bool leftPaddle1 = false, bool leftPaddle2 = false,
        bool rightPaddle1 = false, bool rightPaddle2 = false,
        bool leftIrActive = false, bool rightIrActive = false,
        ushort irDistance = 999, ushort irRoughness = 3_999,
        bool isValid = true,
        ushort contractVersion = JoyConSourceContractVersion,
        bool leftSL = false, bool leftSR = false,
        bool rightSL = false, bool rightSR = false)
    {
        return new DS4State
        {
            Switch2JoyConRawInputStatus = new Switch2JoyConRawInputStatus
            {
                IsValid = isValid,
                ContractVersion = contractVersion,
                CButton = cButton,
                LeftPaddle1 = leftPaddle1,
                LeftPaddle2 = leftPaddle2,
                RightPaddle1 = rightPaddle1,
                RightPaddle2 = rightPaddle2,
                LeftPresent = leftIrActive || leftSL || leftSR,
                LeftRailSL = leftSL,
                LeftRailSR = leftSR,
                LeftIrDistance = leftIrActive ? irDistance : (ushort)0,
                LeftIrRoughness = leftIrActive ? irRoughness : (ushort)0,
                RightPresent = rightIrActive || rightSL || rightSR,
                RightRailSL = rightSL,
                RightRailSR = rightSR,
                RightIrDistance = rightIrActive ? irDistance : (ushort)0,
                RightIrRoughness = rightIrActive ? irRoughness : (ushort)0,
            },
        };
    }

    private static DS4State BothSourcesState()
    {
        DS4State state = ProState(cButton: true);
        state.Switch2JoyConRawInputStatus =
            JoyConState(cButton: true, leftPaddle1: true,
                leftPaddle2: true, rightPaddle1: true,
                rightPaddle2: true).Switch2JoyConRawInputStatus;
        return state;
    }

    private static DS4StateFieldMapping Populate(DS4State state)
    {
        var result = new DS4StateFieldMapping();
        result.PopulateFieldMapping(state, new DS4StateExposed(state), null);
        return result;
    }

    private static void AssertAllSwitch2SourcesReleased(
        DS4StateFieldMapping mapping)
    {
        foreach (DS4Controls control in Switch2SourceControls)
        {
            Assert.IsFalse(mapping.buttons[(int)control],
                $"Invalid/default source leaked {control}.");
        }
    }

    private static BackingStore DeserializeIntoStore(
        XmlSerializer serializer, string xml)
    {
        ProfileDTO dto;
        using (var reader = new StringReader(xml))
        {
            dto = (ProfileDTO)serializer.Deserialize(reader);
        }

        var store = new BackingStore();
        dto.DeviceIndex = Global.TEST_PROFILE_INDEX;
        dto.MapTo(store);
        return store;
    }

    private static void AssertButton(BackingStore store, DS4Controls control,
        X360Controls expectedButton)
    {
        DS4ControlSettings setting = store.GetDS4CSetting(
            Global.TEST_PROFILE_INDEX, control);
        Assert.AreEqual(DS4ControlSettings.ActionType.Button,
            setting.actionType);
        Assert.AreEqual(expectedButton, setting.action.actionBtn);
    }

    private static void AssertAllUnavailable(MappingListViewModel mappings)
    {
        foreach (DS4Controls control in Switch2SourceControls)
        {
            Assert.IsFalse(mappings.ControlMap[control]
                .IsAvailableOnPhysicalController);
        }
    }
}
