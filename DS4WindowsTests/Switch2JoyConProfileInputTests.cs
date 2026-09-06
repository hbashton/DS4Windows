using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.DS4Control;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2JoyConProfileInputTests
{
    [TestMethod]
    public void JoinedAxesRetainEveryRawPositionInMappedCoordinates()
    {
        short previousX = short.MinValue;
        byte previousByte = 0;
        bool sawByteCollision = false;
        for (ushort raw = 0; raw < 4096; raw++)
        {
            var frame = MapJoined(0, 0, raw, (ushort)(4095 - raw),
                (ushort)(4095 - raw), raw);
            var state = new DS4State();
            Assert.IsTrue(frame.TryWriteLegacyState(state));
            Assert.AreEqual(frame.LeftX.SignedValue, state.LXAxis.ToSigned16());
            Assert.AreEqual(frame.LeftY.SignedValue, state.LYAxis.ToSigned16());
            Assert.AreEqual(frame.RightX.SignedValue, state.RXAxis.ToSigned16());
            Assert.AreEqual(frame.RightY.SignedValue, state.RYAxis.ToSigned16());
            Assert.IsTrue(state.LXAxis.IsHighResolution && state.LYAxis.IsHighResolution &&
                state.RXAxis.IsHighResolution && state.RYAxis.IsHighResolution);
            if (raw != 0)
            {
                Assert.IsTrue(state.LXAxis.ToSigned16() > previousX);
                sawByteCollision |= state.LX == previousByte;
            }
            previousX = state.LXAxis.ToSigned16();
            previousByte = state.LX;
        }
        Assert.IsTrue(sawByteCollision, "Typed axes must distinguish source values which share a legacy byte.");
    }

    [DataTestMethod]
    [DataRow(Switch2JoyConProfileMode.StandaloneVerticalLeft)]
    [DataRow(Switch2JoyConProfileMode.StandaloneVerticalRight)]
    [DataRow(Switch2JoyConProfileMode.StandaloneHorizontalLeft)]
    [DataRow(Switch2JoyConProfileMode.StandaloneHorizontalRight)]
    public void StandalonePublishesLogicalMappedStickPrecision(Switch2JoyConProfileMode mode)
    {
        for (ushort raw = 2048; raw <= 2054; raw++)
        {
            var frame = MapStandalone(mode, 0, raw, (ushort)(4096 - raw));
            var state = new DS4State();
            Assert.IsTrue(frame.TryWriteLegacyState(state));
            Assert.AreEqual(frame.LeftX.SignedValue, state.LXAxis.ToSigned16());
            Assert.AreEqual(frame.LeftY.SignedValue, state.LYAxis.ToSigned16());
            Assert.AreEqual(frame.HasRightStick ? frame.RightX.SignedValue : (short)0, state.RXAxis.ToSigned16());
            Assert.AreEqual(frame.HasRightStick ? frame.RightY.SignedValue : (short)0, state.RYAxis.ToSigned16());
            Assert.IsTrue(state.LXAxis.IsHighResolution);
            Assert.IsTrue(state.RXAxis.IsHighResolution);
        }
    }

    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void OrientationBoundariesBaselineGyroLockAndTuningWithoutFalseFreeze(bool left, bool sl)
    {
        var shoulder = sl ? Switch2JoyConProfileButton.LeftShoulder : Switch2JoyConProfileButton.RightShoulder;
        var tuning = new Switch2IrGyroTuning(shoulder, 15, 100, 80, 200, shoulder, 50, 250);
        var binding = new Switch2GyroLockBinding(0, shoulder);
        Switch2GyroTriggerModifierState modifier = default;
        Switch2GyroLockState gyroLock = default;
        Switch2GyroTriggerSourceIdentity previousIdentity = default;
        long timestamp = 1_000_000;
        Read(false, true, false, false, false);
        Read(true, true, false, false, true);
        Read(true, true, false, false, true);
        Read(true, false, true, false, true); // real release starts tuning's release window
        Read(true, true, true, true, true); // real press toggles gyro lock
        Read(false, true, false, false, false); // no latch/freeze carried into another layout

        void Read(bool horizontal, bool pressed, bool freeze, bool locked, bool deadzone)
        {
            var mode = Switch2JoyConProfileInputMapper.StandaloneModeFor(
                left ? Switch2ControllerModel.JoyCon2Left : Switch2ControllerModel.JoyCon2Right,
                horizontal ? Switch2JoyConHoldMode.Horizontal : Switch2JoyConHoldMode.Vertical);
            uint bits = pressed ? 1u << (left ? (sl ? 21 : 20) : (sl ? 5 : 4)) : 0;
            var source = new DS4State();
            Assert.IsTrue(MapStandalone(mode, bits).TryWriteLegacyState(source));
            source.Switch2JoyConRawInputStatus.CompletionTimestampQpc = timestamp++;
            Assert.IsTrue(Switch2GyroTriggerModifier.TryReadInput(source, 1, 1, true, out var input));
            if (previousIdentity.JoyCon)
            {
                Assert.IsTrue(previousIdentity.HasSamePhysicalSource(input.Identity));
                Assert.AreEqual(previousIdentity.JoyConMode == mode, previousIdentity.Equals(input.Identity));
            }
            previousIdentity = input.Identity;
            Assert.IsTrue(Switch2GyroTriggerModifier.TryAdvance(input, tuning, ref modifier, out var result));
            Assert.AreEqual(freeze, result.Freeze);
            Assert.AreEqual(deadzone, result.DeadzoneActive);
            Assert.IsTrue(Switch2GyroLock.TryAdvance(input, GyroOutMode.Mouse, binding, ref gyroLock, out bool actualLocked));
            Assert.AreEqual(locked, actualLocked);
        }
    }

    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    [DoNotParallelize]
    public void OrientationChangesPreserveOrdinaryGyroHoldToggleAndAlwaysOn(bool left, bool sl)
    {
        var previousStore = Global.store;
        FieldInfo storeField = typeof(Global).GetField("m_Config", BindingFlags.Static | BindingFlags.NonPublic);
        try
        {
            storeField.SetValue(null, new BackingStore());
            const int slot = Global.TEST_PROFILE_INDEX;
            foreach (bool toggle in new[] { false, true })
            foreach (bool alwaysOn in new[] { false, true })
            {
                string token = alwaysOn ? "-1" : sl ? "4" : "6";
                Global.SATriggers[slot] = Global.SAMousestickTriggers[slot] =
                    Global.GyroControlsInf[slot].triggers = token;
                Global.SATriggerCond[slot] = Global.SAMouseStickTriggerCond[slot] =
                    Global.GyroControlsInf[slot].triggerCond = false;
                Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 1,
                    Switch2Transport.Usb, out var runtime, out _));
                var mouse = new Mouse(slot, runtime) {
                    ToggleGyroMouse = toggle, ToggleGyroStick = toggle, ToggleGyroControls = toggle };
                var source = new DS4State();
                typeof(Mouse).GetField("s", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(mouse, source);
                long timestamp = 1_000_000;
                // One Mouse instance interleaves every mode; observing a new
                // layout in Controls must not hide that boundary from Mouse.
                Read(false, false, alwaysOn);
                Read(false, true, alwaysOn);
                Read(true, true, alwaysOn || !toggle);
                Read(true, true, alwaysOn || !toggle);
                Read(true, false, alwaysOn);
                Read(true, true, true); // actual press, enables all modes
                Read(false, true, alwaysOn || toggle);
                Read(true, true, true); // preserve an enabled toggle, do not flip it off
                Read(true, false, alwaysOn || toggle);
                Read(true, true, alwaysOn || !toggle); // actual second press

                void Read(bool horizontal, bool pressed, bool expected)
                {
                    var mode = Switch2JoyConProfileInputMapper.StandaloneModeFor(
                        left ? Switch2ControllerModel.JoyCon2Left : Switch2ControllerModel.JoyCon2Right,
                        horizontal ? Switch2JoyConHoldMode.Horizontal : Switch2JoyConHoldMode.Vertical);
                    uint raw = pressed ? 1u << (left ? (sl ? 21 : 20) : (sl ? 5 : 4)) : 0;
                    Assert.IsTrue(MapStandalone(mode, raw).TryWriteLegacyState(source));
                    source.Switch2JoyConRawInputStatus.CompletionTimestampQpc = timestamp++;
                    foreach (var outputMode in new[] { GyroOutMode.Controls, GyroOutMode.Mouse, GyroOutMode.MouseJoystick })
                    {
                        Assert.AreEqual(expected, mouse.IsGyroTriggerActive(outputMode),
                            $"{outputMode}, {mode}, pressed={pressed}, toggle={toggle}, alwaysOn={alwaysOn}");
                        Assert.AreEqual(expected, mouse.IsGyroTriggerActive(outputMode),
                            "A repeated observation must not create another edge.");
                    }
                }
            }
        }
        finally { storeField.SetValue(null, previousStore); }
    }

    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    [DoNotParallelize]
    public void OrientationChangeCannotManufactureModeShiftToggleEdges(bool left, bool sl)
    {
        const int slot = 0;
        var saved = Global.Switch2ModeShiftSettings[slot];
        var rail = left ? (sl ? Switch2JoyConProfileButton.LeftRailSL : Switch2JoyConProfileButton.LeftRailSR) :
            (sl ? Switch2JoyConProfileButton.RightRailSL : Switch2JoyConProfileButton.RightRailSR);
        var shoulder = sl ? Switch2JoyConProfileButton.LeftShoulder : Switch2JoyConProfileButton.RightShoulder;
        long timestamp = 1_000_000;
        try
        {
            foreach (bool includePhysicalRail in new[] { false, true })
            {
                Global.Switch2ModeShiftSettings[slot] = new(0,
                    shoulder | (includePhysicalRail ? rail : 0));
                Mapping.ResetSwitch2ModeShiftState(slot);
                Assert.IsFalse(Read(false, false));
                Assert.AreEqual(includePhysicalRail, Read(false, true));
                Assert.IsFalse(Read(true, true), "Changing orientation is a baseline, not a press.");
                Assert.IsFalse(Read(true, true), "The held report must not re-press after the boundary.");
                Assert.IsFalse(Read(true, false));
                Assert.IsTrue(Read(true, true), "A real release/repress still toggles.");
                Assert.IsTrue(Read(true, true));
                Assert.IsFalse(Read(false, true), "Returning to vertical clears the old layout's latch.");
                Assert.IsFalse(Read(false, false));
                Assert.AreEqual(includePhysicalRail, Read(false, true));
            }
            Global.Switch2ModeShiftSettings[slot] = new(rail, 0);
            Mapping.ResetSwitch2ModeShiftState(slot);
            Assert.IsTrue(Read(false, true), "Hold remains immediate at an initial boundary.");
            Assert.IsTrue(Read(true, true), "Orientation changes must not suppress Hold.");
            Assert.IsTrue(Read(false, true));
            Assert.IsFalse(Read(false, false));
        }
        finally
        {
            Global.Switch2ModeShiftSettings[slot] = saved;
            Mapping.ResetSwitch2ModeShiftState(slot);
        }

        bool Read(bool horizontal, bool pressed)
        {
            uint raw = pressed ? 1u << (left ? (sl ? 21 : 20) : (sl ? 5 : 4)) : 0;
            var mode = Switch2JoyConProfileInputMapper.StandaloneModeFor(
                left ? Switch2ControllerModel.JoyCon2Left : Switch2ControllerModel.JoyCon2Right,
                horizontal ? Switch2JoyConHoldMode.Horizontal : Switch2JoyConHoldMode.Vertical);
            var source = new DS4State();
            Assert.IsTrue(MapStandalone(mode, raw).TryWriteLegacyState(source));
            // Same physical/transport lifetime throughout; only the layout changes.
            source.Switch2JoyConRawInputStatus.CompletionTimestampQpc = timestamp++;
            var exposed = new DS4StateExposed(source);
            var fields = new DS4StateFieldMapping();
            fields.PopulateFieldMapping(source, exposed, null);
            bool result = Mapping.ShiftTrigger(Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
                slot, source, exposed, null, fields);
            Assert.AreEqual(result, Mapping.ShiftTrigger(Mapping.SWITCH2_MODE_SHIFT_TRIGGER,
                slot, source, exposed, null, fields), "Repeated mapping of one report must be idempotent.");
            return result;
        }
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DoNotParallelize]
    public void PhysicalRailsThroughRealProfileMapperReachEveryVirtualTargetAndRelease(int commandMode)
    {
        // Only in-memory owners. No ControlService constructor, runtime start,
        // transport connection, virtual-device creation or system input calls.
        const int slot = 7;
        FieldInfo storeField = typeof(Global).GetField("m_Config",
            BindingFlags.Static | BindingFlags.NonPublic);
        var previousStore = Global.store;
        var previousHandler = Global.outputKBMHandler;
        var previousFields = Mapping.fieldMappings[slot];
        var previousOutputFields = Mapping.outputFieldMappings[slot];
        var previousDeviceState = Mapping.deviceState[slot];
        try
        {
            storeField.SetValue(null, new BackingStore());
            Global.outputKBMHandler = new NoSystemInputHandler();
            Mapping.fieldMappings[slot] = new();
            Mapping.outputFieldMappings[slot] = new();
            Mapping.deviceState[slot] = new();
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 1,
                Switch2Transport.Usb, out var runtime, out _));
            var mouse = new Mouse(slot, runtime);
            var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
            var targets = new[] { ViiperVirtualDeviceType.Xbox360,
                ViiperVirtualDeviceType.XboxOne, ViiperVirtualDeviceType.DualShock4,
                ViiperVirtualDeviceType.DualSense, ViiperVirtualDeviceType.DualSenseEdge,
                ViiperVirtualDeviceType.Switch2Pro };
            var cases = new[] {
                (true, true, DS4Controls.Switch2JoyConLeftSL),
                (true, false, DS4Controls.Switch2JoyConLeftSR),
                (false, true, DS4Controls.Switch2JoyConRightSL),
                (false, false, DS4Controls.Switch2JoyConRightSR),
            };
            foreach (var (left, sl, control) in cases)
            {
                var setting = Global.store.GetDS4CSetting(slot, control);
                setting.actionType = DS4ControlSettings.ActionType.Button;
                setting.action.actionBtn = X360Controls.A;
                var rail = control switch {
                    DS4Controls.Switch2JoyConLeftSL => Switch2JoyConProfileButton.LeftRailSL,
                    DS4Controls.Switch2JoyConLeftSR => Switch2JoyConProfileButton.LeftRailSR,
                    DS4Controls.Switch2JoyConRightSL => Switch2JoyConProfileButton.RightRailSL,
                    _ => Switch2JoyConProfileButton.RightRailSR };
                var shoulder = sl ? Switch2JoyConProfileButton.LeftShoulder : Switch2JoyConProfileButton.RightShoulder;
                Global.Switch2ModeShiftSettings[slot] = new(
                    commandMode is 1 or 5 ? rail : commandMode == 3 ? shoulder : Switch2JoyConProfileButton.None,
                    commandMode == 2 ? rail : commandMode is 4 or 5 ? shoulder : Switch2JoyConProfileButton.None);
                foreach (int orientation in new[] { 0, 1, 2 })
                {
                    bool horizontal = orientation == 2;
                    Mapping.ResetSwitch2ModeShiftState(slot);
                    bool previousPress = false;
                    bool toggled = false;
                    foreach (bool pressed in new[] { false, true, true, false, true, false })
                    {
                        uint bits = pressed ? 1u << (left ? (sl ? 21 : 20) : (sl ? 5 : 4)) : 0;
                        var frame = orientation == 0 ? MapJoined(left ? bits : 0, left ? 0 : bits) :
                            MapStandalone(Switch2JoyConProfileInputMapper.StandaloneModeFor(
                                left ? Switch2ControllerModel.JoyCon2Left : Switch2ControllerModel.JoyCon2Right,
                                horizontal ? Switch2JoyConHoldMode.Horizontal : Switch2JoyConHoldMode.Vertical), bits);
                        var source = new DS4State();
                        Assert.IsTrue(frame.TryWriteLegacyState(source));
                        var output = new DS4State();
                        source.CopyExtrasTo(output);
                        var exposed = new DS4StateExposed(source);
                        Mapping.fieldMappings[slot].PopulateFieldMapping(source, exposed, mouse);
                        bool activationPressed = pressed && (commandMode is 1 or 2 or 5 ||
                            horizontal && commandMode is 3 or 4);
                        bool toggle = commandMode is 2 or 4;
                        if (toggle && activationPressed && !previousPress)
                            toggled = !toggled;
                        Assert.AreEqual(toggle ? toggled : activationPressed,
                            Mapping.ShiftTrigger(Mapping.SWITCH2_MODE_SHIFT_TRIGGER, slot,
                                source, exposed, mouse, Mapping.fieldMappings[slot]),
                            $"Mode Shift mode {commandMode}, orientation {orientation}");
                        previousPress = activationPressed;
                        var immutableSource = source.Switch2JoyConRawInputStatus;
                        Mapping.MapCustom(slot, source, output, new DS4StateExposed(source), mouse, service);
                        Assert.AreEqual(immutableSource, source.Switch2JoyConRawInputStatus);
                        bool gamePressed = pressed && (commandMode == 0 ||
                            !horizontal && commandMode is 3 or 4);
                        Assert.AreEqual(gamePressed, output.Cross, control.ToString());
                        Assert.AreEqual(gamePressed && horizontal && sl, output.L1);
                        Assert.AreEqual(gamePressed && horizontal && !sl, output.R1);
                        foreach (var target in targets)
                        {
                            byte[] packet = ViiperStatePacketBuilder.Build(target, output, -1);
                            uint expected = 0;
                            int offset = 0;
                            switch (target)
                            {
                                case ViiperVirtualDeviceType.Xbox360:
                                    expected = 0x1000u | (horizontal ? (sl ? 0x100u : 0x200u) : 0); break;
                                case ViiperVirtualDeviceType.XboxOne:
                                    offset = 4;
                                    expected = 4u | (horizontal ? (sl ? 0x400u : 0x800u) : 0); break;
                                case ViiperVirtualDeviceType.Switch2Pro:
                                    expected = 1u | (horizontal ? (sl ? 0x1000u : 0x10u) : 0); break;
                                default:
                                    offset = 4;
                                    expected = 0x20u | (horizontal ? (sl ? 0x100u : 0x200u) : 0); break;
                            }
                            uint actual = target == ViiperVirtualDeviceType.DualShock4 ?
                                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset)) :
                                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset));
                            Assert.AreEqual(gamePressed ? expected : 0, actual,
                                $"{control}, orientation {orientation}, {target}, pressed={pressed}");
                        }
                    }
                }
                setting.actionType = DS4ControlSettings.ActionType.Default;
            }
        }
        finally
        {
            storeField.SetValue(null, previousStore);
            Global.outputKBMHandler = previousHandler;
            Mapping.fieldMappings[slot] = previousFields;
            Mapping.outputFieldMappings[slot] = previousOutputFields;
            Mapping.deviceState[slot] = previousDeviceState;
            Mapping.ResetSwitch2ModeShiftState(slot);
        }
    }

    private sealed class NoSystemInputHandler : VirtualKBMBase
    {
        private static void Reject() => Assert.Fail("Mapping this pad must not emit system mouse/keyboard input.");
        public override bool Connect() { Reject(); return false; }
        public override bool Disconnect() { Reject(); return false; }
        public override void MoveRelativeMouse(int x, int y) => Reject();
        public override void MoveAbsoluteMouse(double x, double y) => Reject();
        public override void PerformMouseWheelEvent(int vertical, int horizontal) => Reject();
        public override void PerformMouseButtonEvent(uint button) => Reject();
        public override void PerformMouseButtonPress(uint button) => Reject();
        public override void PerformMouseButtonRelease(uint button) => Reject();
        public override void PerformKeyPress(uint key) => Reject();
        public override void PerformKeyPressAlt(uint key) => Reject();
        public override void PerformKeyRelease(uint key) => Reject();
        public override void PerformKeyReleaseAlt(uint key) => Reject();
        public override string GetDisplayName() => "No system input";
        public override string GetIdentifier() => "test-only";
        public override string GetFullDisplayName() => GetDisplayName();
    }

    [TestMethod]
    public void FaceButtonLayoutAppliesToStandaloneJoyConWithoutChangingSidecar()
    {
        var cases = new[]
        {
            (0, nameof(DS4State.Square), nameof(DS4State.Triangle)),
            (1, nameof(DS4State.Triangle), nameof(DS4State.Square)),
            (2, nameof(DS4State.Cross), nameof(DS4State.Circle)),
            (3, nameof(DS4State.Circle), nameof(DS4State.Cross)),
        };

        foreach ((int rawBit, string xbox, string nintendo) in cases)
        {
            Switch2JoyConProfileInputFrame mapped = MapStandalone(
                Switch2JoyConProfileMode.StandaloneVerticalRight,
                1u << rawBit);
            var state = new DS4State();

            Assert.IsTrue(mapped.TryWriteLegacyState(state,
                Switch2FaceButtonLayout.Xbox));
            CollectionAssert.AreEqual(new[] { xbox },
                ReadActiveLegacyControls(state).ToArray());

            Assert.IsTrue(mapped.TryWriteLegacyState(state,
                Switch2FaceButtonLayout.Nintendo));
            CollectionAssert.AreEqual(new[] { nintendo },
                ReadActiveLegacyControls(state).ToArray());
            Assert.AreEqual(1u << rawBit,
                state.Switch2JoyConRawInputStatus.RightRawButtonBits);
        }

        Switch2JoyConProfileInputFrame invalid = MapStandalone(
            Switch2JoyConProfileMode.StandaloneVerticalRight, 1u << 2);
        Assert.IsFalse(invalid.TryWriteLegacyState(new DS4State(),
            (Switch2FaceButtonLayout)99));
    }

    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void PhysicalRailIdentityIsPresentInJoinedVerticalAndHorizontalSources(
        bool left, bool sl)
    {
        uint raw = 1u << (left ? (sl ? 21 : 20) : (sl ? 5 : 4));
        var rail = left ? (sl ? Switch2JoyConProfileButton.LeftRailSL :
            Switch2JoyConProfileButton.LeftRailSR) :
            (sl ? Switch2JoyConProfileButton.RightRailSL :
                Switch2JoyConProfileButton.RightRailSR);
        var joined = MapJoined(left ? raw : 0, left ? 0 : raw);
        Assert.AreEqual(rail, (left ? joined.LeftSource : joined.RightSource).Buttons);
        foreach (bool horizontal in new[] { false, true })
        {
            var mode = Switch2JoyConProfileInputMapper.StandaloneModeFor(
                left ? Switch2ControllerModel.JoyCon2Left : Switch2ControllerModel.JoyCon2Right,
                horizontal ? Switch2JoyConHoldMode.Horizontal : Switch2JoyConHoldMode.Vertical);
            var frame = MapStandalone(mode, raw);
            var source = left ? frame.LeftSource : frame.RightSource;
            Assert.IsTrue((source.Buttons & rail) != 0);
            var state = new DS4State();
            Assert.IsTrue(frame.TryWriteLegacyState(state));
            Assert.AreEqual(horizontal && sl, state.L1,
                "Physical rail identity must not change mini-controller defaults.");
            Assert.AreEqual(horizontal && !sl, state.R1);
            DS4Controls control = left ? (sl ? DS4Controls.Switch2JoyConLeftSL :
                DS4Controls.Switch2JoyConLeftSR) : (sl ? DS4Controls.Switch2JoyConRightSL :
                DS4Controls.Switch2JoyConRightSR);
            var copies = new[] { new DS4State(state), new DS4State(), new DS4State() };
            state.CopyTo(copies[1]);
            state.CopyExtrasTo(copies[2]);
            foreach (var copy in copies)
            {
                Assert.AreEqual(state.Switch2JoyConRawInputStatus, copy.Switch2JoyConRawInputStatus);
                Assert.AreEqual(state.Switch2JoyConRawInputStatus.GetHashCode(),
                    copy.Switch2JoyConRawInputStatus.GetHashCode());
                Assert.IsTrue(DS4StateFieldMapping.GetValidatedSwitch2SourceButton(copy, control));
                Assert.IsTrue(MapStandalone(mode, 0).TryWriteLegacyState(copy));
                Assert.IsFalse(DS4StateFieldMapping.GetValidatedSwitch2SourceButton(copy, control));
            }
        }
    }

    [TestMethod]
    public void ProOnlyGLGRBitsNeverManufactureJoyConRailOrPaddleInput()
    {
        var joined = MapJoined(1u << 25, 1u << 24);
        Assert.AreEqual(Switch2JoyConProfileButton.None, joined.Buttons);
        Assert.AreEqual(1u << 25, joined.LeftSource.UnknownButtonBits);
        Assert.AreEqual(1u << 24, joined.RightSource.UnknownButtonBits);
        foreach (var mode in new[] {
            Switch2JoyConProfileMode.StandaloneVerticalLeft,
            Switch2JoyConProfileMode.StandaloneVerticalRight,
            Switch2JoyConProfileMode.StandaloneHorizontalLeft,
            Switch2JoyConProfileMode.StandaloneHorizontalRight })
        {
            var frame = MapStandalone(mode, (1u << 25) | (1u << 24));
            Assert.AreEqual(Switch2JoyConProfileButton.None, frame.Buttons);
            Assert.AreEqual(Switch2JoyConProfileButton.None,
                frame.LeftSource.Buttons | frame.RightSource.Buttons);
        }
    }

    private const Switch2GattProperty InputProperties =
        Switch2GattProperty.Read | Switch2GattProperty.Notify;

    [TestMethod]
    public void JoinedHandlersMapEveryPinnedButtonExactlyOnce()
    {
        var leftMappings = new (uint Raw,
            Switch2JoyConProfileButton Semantic, string Legacy)[]
        {
            (1u << 8, Switch2JoyConProfileButton.Back,
                nameof(DS4State.Share)),
            (1u << 11, Switch2JoyConProfileButton.LeftStick,
                nameof(DS4State.L3)),
            (1u << 13, Switch2JoyConProfileButton.Capture,
                nameof(DS4State.Capture)),
            (1u << 16, Switch2JoyConProfileButton.DpadDown,
                nameof(DS4State.DpadDown)),
            (1u << 17, Switch2JoyConProfileButton.DpadUp,
                nameof(DS4State.DpadUp)),
            (1u << 18, Switch2JoyConProfileButton.DpadRight,
                nameof(DS4State.DpadRight)),
            (1u << 19, Switch2JoyConProfileButton.DpadLeft,
                nameof(DS4State.DpadLeft)),
            (1u << 22, Switch2JoyConProfileButton.LeftShoulder,
                nameof(DS4State.L1)),
            (1u << 23, Switch2JoyConProfileButton.LeftTrigger,
                nameof(DS4State.L2Btn)),
            (1u << 21, Switch2JoyConProfileButton.LeftRailSL,
                string.Empty),
            (1u << 20, Switch2JoyConProfileButton.LeftRailSR,
                string.Empty),
        };
        var rightMappings = new (uint Raw,
            Switch2JoyConProfileButton Semantic, string Legacy)[]
        {
            (1u << 0, Switch2JoyConProfileButton.FaceWest,
                nameof(DS4State.Square)),
            (1u << 1, Switch2JoyConProfileButton.FaceNorth,
                nameof(DS4State.Triangle)),
            (1u << 2, Switch2JoyConProfileButton.FaceSouth,
                nameof(DS4State.Cross)),
            (1u << 3, Switch2JoyConProfileButton.FaceEast,
                nameof(DS4State.Circle)),
            (1u << 6, Switch2JoyConProfileButton.RightShoulder,
                nameof(DS4State.R1)),
            (1u << 7, Switch2JoyConProfileButton.RightTrigger,
                nameof(DS4State.R2Btn)),
            (1u << 9, Switch2JoyConProfileButton.Start,
                nameof(DS4State.Options)),
            (1u << 10, Switch2JoyConProfileButton.RightStick,
                nameof(DS4State.R3)),
            (1u << 12, Switch2JoyConProfileButton.Guide,
                nameof(DS4State.PS)),
            (1u << 14, Switch2JoyConProfileButton.C, string.Empty),
            (1u << 5, Switch2JoyConProfileButton.RightRailSL,
                string.Empty),
            (1u << 4, Switch2JoyConProfileButton.RightRailSR,
                string.Empty),
        };

        uint leftUnion = 0;
        uint rightUnion = 0;
        foreach ((uint raw, Switch2JoyConProfileButton semantic,
            string legacy) in
            leftMappings)
        {
            leftUnion |= raw;
            Switch2JoyConProfileInputFrame mapped = MapJoined(raw, 0);
            Assert.AreEqual(semantic, mapped.Buttons,
                $"Joined-left raw mask 0x{raw:X8} mapped more than once.");
            AssertLegacyExactly(mapped, legacy);
        }
        foreach ((uint raw, Switch2JoyConProfileButton semantic,
            string legacy) in
            rightMappings)
        {
            rightUnion |= raw;
            Switch2JoyConProfileInputFrame mapped = MapJoined(0, raw);
            Assert.AreEqual(semantic, mapped.Buttons,
                $"Joined-right raw mask 0x{raw:X8} mapped more than once.");
            AssertLegacyExactly(mapped, legacy);
        }

        Assert.AreEqual(
            Switch2JoyConProfileInputMapper.CombinedLeftKnownButtonMask,
            leftUnion);
        Assert.AreEqual(
            Switch2JoyConProfileInputMapper.CombinedRightKnownButtonMask,
            rightUnion);
    }

    [TestMethod]
    public void MiniLeftHandlerMapsEveryPinnedButtonExactlyOnce()
    {
        var mappings = new (uint Raw,
            Switch2JoyConProfileButton Semantic, string Legacy)[]
        {
            (1u << 8, Switch2JoyConProfileButton.Start,
                nameof(DS4State.Options)),
            (1u << 11, Switch2JoyConProfileButton.LeftStick,
                nameof(DS4State.L3)),
            (1u << 12, Switch2JoyConProfileButton.Capture,
                nameof(DS4State.Capture)),
            (1u << 13, Switch2JoyConProfileButton.Guide,
                nameof(DS4State.PS)),
            (1u << 16, Switch2JoyConProfileButton.FaceWest,
                nameof(DS4State.Square)),
            (1u << 17, Switch2JoyConProfileButton.FaceNorth,
                nameof(DS4State.Triangle)),
            (1u << 18, Switch2JoyConProfileButton.FaceSouth,
                nameof(DS4State.Cross)),
            (1u << 19, Switch2JoyConProfileButton.FaceEast,
                nameof(DS4State.Circle)),
            (1u << 20, Switch2JoyConProfileButton.RightShoulder,
                nameof(DS4State.R1)),
            (1u << 21, Switch2JoyConProfileButton.LeftShoulder,
                nameof(DS4State.L1)),
            (1u << 22, Switch2JoyConProfileButton.LeftPaddle1,
                string.Empty),
            (1u << 23, Switch2JoyConProfileButton.LeftPaddle2,
                string.Empty),
        };

        uint union = 0;
        foreach ((uint raw, Switch2JoyConProfileButton semantic,
            string legacy) in mappings)
        {
            union |= raw;
            Switch2JoyConProfileInputFrame mapped = MapStandalone(
                Switch2JoyConProfileMode.StandaloneHorizontalLeft, raw);
            Assert.AreEqual(semantic, mapped.Buttons,
                $"Mini-left raw mask 0x{raw:X8} mapped more than once.");
            AssertLegacyExactly(mapped, legacy);
        }
        Assert.AreEqual(Switch2JoyConProfileInputMapper.MiniLeftKnownButtonMask,
            union);
    }

    [TestMethod]
    public void MiniRightHandlerMapsEveryPinnedButtonExactlyOnce()
    {
        var mappings = new (uint Raw,
            Switch2JoyConProfileButton Semantic, string Legacy)[]
        {
            (1u << 0, Switch2JoyConProfileButton.FaceWest,
                nameof(DS4State.Square)),
            (1u << 1, Switch2JoyConProfileButton.FaceNorth,
                nameof(DS4State.Triangle)),
            (1u << 2, Switch2JoyConProfileButton.FaceSouth,
                nameof(DS4State.Cross)),
            (1u << 3, Switch2JoyConProfileButton.FaceEast,
                nameof(DS4State.Circle)),
            (1u << 4, Switch2JoyConProfileButton.RightShoulder,
                nameof(DS4State.R1)),
            (1u << 5, Switch2JoyConProfileButton.LeftShoulder,
                nameof(DS4State.L1)),
            (1u << 6, Switch2JoyConProfileButton.RightPaddle1,
                string.Empty),
            (1u << 7, Switch2JoyConProfileButton.RightPaddle2,
                string.Empty),
            (1u << 9, Switch2JoyConProfileButton.Start,
                nameof(DS4State.Options)),
            (1u << 10, Switch2JoyConProfileButton.LeftStick,
                nameof(DS4State.L3)),
            (1u << 12, Switch2JoyConProfileButton.Guide,
                nameof(DS4State.PS)),
            (1u << 14, Switch2JoyConProfileButton.C, string.Empty),
        };

        uint union = 0;
        foreach ((uint raw, Switch2JoyConProfileButton semantic,
            string legacy) in mappings)
        {
            union |= raw;
            Switch2JoyConProfileInputFrame mapped = MapStandalone(
                Switch2JoyConProfileMode.StandaloneHorizontalRight, raw);
            Assert.AreEqual(semantic, mapped.Buttons,
                $"Mini-right raw mask 0x{raw:X8} mapped more than once.");
            AssertLegacyExactly(mapped, legacy);
        }
        Assert.AreEqual(
            Switch2JoyConProfileInputMapper.MiniRightKnownButtonMask, union);
    }

    [TestMethod]
    public void JoinedAndHorizontalAxesPreserveRawAndExactSdlRotation()
    {
        Switch2JoyConProfileInputFrame joined = MapJoined(0, 0,
            leftX: 0, leftY: 0x0FFF, rightX: 0x0FFF, rightY: 0);
        AssertAxis(joined.LeftX, 0, short.MinValue);
        AssertAxis(joined.LeftY, 0x0FFF, short.MinValue);
        AssertAxis(joined.RightX, 0x0FFF, short.MaxValue);
        AssertAxis(joined.RightY, 0, short.MaxValue);

        Switch2JoyConProfileInputFrame left = MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft, 0,
            physicalX: 0, physicalY: 0x0FFF);
        AssertAxis(left.LeftX, 0x0FFF, short.MinValue);
        AssertAxis(left.LeftY, 0, short.MaxValue);
        Assert.IsFalse(left.HasRightStick);

        Switch2JoyConProfileInputFrame right = MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalRight, 0,
            physicalX: 0, physicalY: 0x0FFF);
        AssertAxis(right.LeftX, 0x0FFF, short.MaxValue);
        AssertAxis(right.LeftY, 0, short.MinValue);
        Assert.IsFalse(right.HasRightStick);

        var legacy = new DS4State();
        Assert.IsTrue(right.TryWriteLegacyState(legacy));
        Assert.AreEqual(byte.MaxValue, legacy.LX);
        Assert.AreEqual((byte)0, legacy.LY);
        Assert.AreEqual((byte)128, legacy.RX);
        Assert.AreEqual((byte)128, legacy.RY);
    }

    [TestMethod]
    public void VerticalStandaloneModesMirrorPinnedCombinedSemantics()
    {
        Switch2JoyConProfileInputFrame left = MapStandalone(
            Switch2JoyConProfileMode.StandaloneVerticalLeft,
            (1u << 16) | (1u << 22) | (1u << 23),
            physicalX: 0, physicalY: 0x0FFF);
        Assert.AreEqual(Switch2JoyConProfileButton.DpadDown |
            Switch2JoyConProfileButton.LeftShoulder |
            Switch2JoyConProfileButton.LeftTrigger, left.Buttons);
        AssertAxis(left.LeftX, 0, short.MinValue);
        AssertAxis(left.LeftY, 0x0FFF, short.MinValue);
        Assert.IsFalse(left.HasRightStick);
        Assert.AreEqual(0u, left.LeftSource.UnknownButtonBits);

        Switch2JoyConProfileInputFrame right = MapStandalone(
            Switch2JoyConProfileMode.StandaloneVerticalRight,
            (1u << 2) | (1u << 7) | (1u << 10),
            physicalX: 0x0FFF, physicalY: 0);
        Assert.AreEqual(Switch2JoyConProfileButton.FaceSouth |
            Switch2JoyConProfileButton.RightTrigger |
            Switch2JoyConProfileButton.RightStick, right.Buttons);
        AssertAxis(right.RightX, 0x0FFF, short.MaxValue);
        AssertAxis(right.RightY, 0, short.MaxValue);
        Assert.IsTrue(right.HasRightStick);
        Assert.AreEqual(0u, right.RightSource.UnknownButtonBits);

        var legacy = new DS4State();
        Assert.IsTrue(right.TryWriteLegacyState(legacy));
        Assert.AreEqual((byte)128, legacy.LX);
        Assert.AreEqual((byte)128, legacy.LY);
        Assert.AreEqual(byte.MaxValue, legacy.RX);
        Assert.AreEqual(byte.MaxValue, legacy.RY);
        Assert.IsTrue(legacy.Cross);
        Assert.IsTrue(legacy.R2Btn);
        Assert.IsTrue(legacy.R3);
    }

    [TestMethod]
    public void OrientationChangePreservesLifetimeAndReplayFences()
    {
        Switch2InputSessionDescriptor descriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Left, 77, 88);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft, descriptor,
            out var initial));
        Switch2CanonicalInputFrame first = CreateCommonFrame(descriptor,
            10, 1u << 16, 0x400, 0xC00, 100);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(initial,
            first, out var accepted, out var horizontal, out var firstFailure),
            firstFailure.ToString());

        Assert.IsTrue(Switch2JoyConProfileInputMapper.TrySelectStandaloneMode(
            accepted, Switch2JoyConProfileMode.StandaloneVerticalLeft,
            out var verticalState));
        Assert.AreEqual(accepted.LastLeftCounter,
            verticalState.LastLeftCounter);
        Assert.AreEqual(accepted.LastLeftTimestampQpc,
            verticalState.LastLeftTimestampQpc);
        Assert.AreEqual(accepted.LeftDescriptor,
            verticalState.LeftDescriptor);
        Assert.IsFalse(Switch2JoyConProfileInputMapper.
            TrySelectStandaloneMode(verticalState,
                Switch2JoyConProfileMode.StandaloneVerticalRight, out _));

        Switch2CanonicalInputFrame second = CreateCommonFrame(descriptor,
            11, 1u << 16, 0x400, 0xC00, 110);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(
            verticalState, second, out var next, out var vertical,
            out var secondFailure), secondFailure.ToString());
        Assert.AreEqual(Switch2JoyConProfileMode.StandaloneVerticalLeft,
            vertical.Mode);
        Assert.IsTrue((vertical.Buttons &
            Switch2JoyConProfileButton.DpadDown) != 0);
        Assert.IsTrue((horizontal.Buttons &
            Switch2JoyConProfileButton.FaceWest) != 0);
        Assert.AreEqual(11u, next.LastLeftCounter);

        Switch2CanonicalInputFrame replay = CreateCommonFrame(descriptor,
            10, 0, 0x800, 0x800, 120);
        Assert.IsFalse(Switch2JoyConProfileInputMapper.TryMapStandalone(next,
            replay, out _, out _, out var replayFailure));
        Assert.AreEqual(Switch2JoyConProfileInputFailure.BackwardOrOutOfOrder,
            replayFailure);
    }

    [TestMethod]
    public void VerticalMotionOrientationMatchesPinnedSwitch2ConnectAxes()
    {
        var gyro = new System.Numerics.Vector3(1, 2, 3);
        var acceleration = new System.Numerics.Vector3(4, 5, 6);

        Assert.IsTrue(Switch2JoyConMotionProjection.TryOrient(
            Switch2JoyConProfileMode.StandaloneVerticalLeft,
            Switch2JoyConSide.Left, gyro, acceleration,
            out var leftGyro, out var leftAcceleration));
        Assert.AreEqual(new System.Numerics.Vector3(1, 3, 2), leftGyro);
        Assert.AreEqual(new System.Numerics.Vector3(4, 6, -5),
            leftAcceleration);

        Assert.IsTrue(Switch2JoyConMotionProjection.TryOrient(
            Switch2JoyConProfileMode.StandaloneVerticalRight,
            Switch2JoyConSide.Right, gyro, acceleration,
            out var rightGyro, out var rightAcceleration));
        Assert.AreEqual(new System.Numerics.Vector3(1, 3, 2), rightGyro);
        Assert.AreEqual(new System.Numerics.Vector3(-4, 6, -5),
            rightAcceleration);
        Assert.IsFalse(Switch2JoyConMotionProjection.TryOrient(
            Switch2JoyConProfileMode.StandaloneVerticalRight,
            Switch2JoyConSide.Left, gyro, acceleration, out _, out _));
    }

    [TestMethod]
    public void FactoryCalibrationStaysGenerationBoundAcrossRotation()
    {
        byte[] record = BuildCalibration(neutralX: 1000, neutralY: 2000,
            positiveX: 500, positiveY: 700, negativeX: 300,
            negativeY: 400);
        Switch2ControllerModel model = Switch2ControllerModel.JoyCon2Left;
        Switch2InputSessionDescriptor descriptor = CreateCommonDescriptor(
            model, deviceGeneration: 9, transportGeneration: 3);
        Switch2CanonicalInputFrame canonical = CreateCommonFrame(descriptor,
            counter: 1, buttons: 0, physicalX: 600, physicalY: 3000,
            timestamp: 100, calibrationRecord: record);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft, descriptor,
            out var state));

        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(state,
            canonical, out _, out var mapped, out var failure),
            failure.ToString());
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.AdoptedFactory,
            mapped.LeftSource.CalibrationStatus);
        AssertAxis(mapped.LeftX, 3000, short.MinValue);
        AssertAxis(mapped.LeftY, 600, short.MaxValue);
        Assert.AreEqual(9UL, mapped.LeftSource.DeviceGeneration);
    }

    [TestMethod]
    public void CAndAllRailPaddlesRemainDistinctCopiedSidecarControls()
    {
        const uint unknown = 1u << 31;
        uint rightMini = (1u << 6) | (1u << 7) | (1u << 14) | unknown;
        Switch2JoyConProfileInputFrame mapped = MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalRight, rightMini,
            physicalX: 0x123, physicalY: 0xABC);
        var state = CreateDirtyLegacyState();

        Assert.IsTrue(mapped.TryWriteLegacyState(state));
        Assert.IsTrue(mapped.CButton);
        Assert.IsTrue(mapped.RightPaddle1);
        Assert.IsTrue(mapped.RightPaddle2);
        Assert.IsFalse(state.Mute);
        Assert.IsFalse(state.BLP);
        Assert.IsFalse(state.BRP);
        Assert.IsTrue(state.Switch2JoyConRawInputStatus.CButton);
        Assert.IsTrue(state.Switch2JoyConRawInputStatus.RightPaddle1);
        Assert.IsTrue(state.Switch2JoyConRawInputStatus.RightPaddle2);
        Assert.IsFalse(state.Switch2RawInputStatus.IsValid);
        Assert.IsFalse(state.DualSenseRawInputStatus.IsValid);

        Switch2JoyConRawInputStatus expected =
            state.Switch2JoyConRawInputStatus;
        var constructed = new DS4State(state);
        var copied = new DS4State();
        state.CopyTo(copied);
        var extras = new DS4State();
        state.CopyExtrasTo(extras);
        Assert.AreEqual(expected, constructed.Switch2JoyConRawInputStatus);
        Assert.AreEqual(expected, copied.Switch2JoyConRawInputStatus);
        Assert.AreEqual(expected, extras.Switch2JoyConRawInputStatus);
        Assert.AreEqual((ushort)0x123,
            expected.RightPhysicalStickXRaw);
        Assert.AreEqual((ushort)0xABC,
            expected.RightPhysicalStickYRaw);
        Assert.AreEqual(unknown, expected.RightUnknownButtonBits);
    }

    [TestMethod]
    public void CompatibilityWriteClearsReleasedAndUnsupportedControls()
    {
        Switch2JoyConProfileInputFrame mapped = MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft, 0,
            physicalX: 0x800, physicalY: 0x800);
        var state = CreateDirtyLegacyState();

        Assert.IsTrue(mapped.TryWriteLegacyState(state));
        Assert.AreEqual(0, ReadActiveLegacyControls(state).Count);
        Assert.AreEqual((byte)0, state.L2);
        Assert.AreEqual((byte)0, state.L2Raw);
        Assert.AreEqual((byte)0, state.R2);
        Assert.AreEqual((byte)0, state.R2Raw);
        Assert.IsFalse(state.Touch1);
        Assert.IsFalse(state.Touch2);
        Assert.IsFalse(state.TouchButton);
        Assert.IsFalse(state.OutputTouchButton);
        Assert.AreEqual((byte)0, state.OutputLSOuter);
        Assert.AreEqual((byte)0, state.OutputRSOuter);
        Assert.AreEqual(0, state.SASteeringWheelEmulationUnit);
        Assert.AreEqual(0x10203040u, state.PacketCounter,
            "Independent Joy-Con counters must not replace the host sequence.");
        Assert.IsTrue(state.Switch2JoyConRawInputStatus.IsValid);
    }

    [TestMethod]
    public void MapperRejectsEpochLifetimeTimestampAndCounterRegressions()
    {
        Switch2InputSessionDescriptor leftDescriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Left, 1, 7);
        Switch2InputSessionDescriptor rightDescriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Right, 2, 9);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateJoined(44,
            leftDescriptor, rightDescriptor, out var initial));
        Switch2JoyConPairSnapshot first = CreateSnapshot(44, leftDescriptor,
            rightDescriptor, leftCounter: 100, rightCounter: 200,
            leftTimestamp: 1000, rightTimestamp: 1001);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapJoined(initial,
            first, out var accepted, out _, out var firstFailure),
            firstFailure.ToString());

        Switch2JoyConPairSnapshot wrongEpoch = new(45, first.Left,
            first.Right, first.SkewQpcTicks);
        AssertRejected(accepted, wrongEpoch,
            Switch2JoyConProfileInputFailure.PairEpochMismatch);

        Switch2JoyConPairSnapshot staleTime = CreateSnapshot(44,
            leftDescriptor, rightDescriptor, 101, 201, 999, 1002);
        AssertRejected(accepted, staleTime,
            Switch2JoyConProfileInputFailure.StaleObservation);

        Switch2JoyConPairSnapshot backward = CreateSnapshot(44,
            leftDescriptor, rightDescriptor, 90, 201, 1002, 1002);
        AssertRejected(accepted, backward,
            Switch2JoyConProfileInputFailure.BackwardOrOutOfOrder);

        Switch2InputSessionDescriptor newLeftGeneration =
            CreateCommonDescriptor(Switch2ControllerModel.JoyCon2Left, 2, 1);
        Switch2JoyConPairSnapshot mismatchedGeneration = CreateSnapshot(44,
            newLeftGeneration, rightDescriptor, 101, 201, 1002, 1002);
        AssertRejected(accepted, mismatchedGeneration,
            Switch2JoyConProfileInputFailure.LifetimeMismatch);

        Assert.AreEqual(100u, accepted.LastLeftCounter,
            "Rejected observations must not advance the accepted baseline.");
        Assert.AreEqual(200u, accepted.LastRightCounter);
    }

    [TestMethod]
    public void CommonMapperFailsClosedForDedicatedReports07And08()
    {
        AssertDedicatedReportRejected(Switch2ControllerModel.JoyCon2Left,
            Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
            Switch2JoyConProfileMode.StandaloneHorizontalLeft);
        AssertDedicatedReportRejected(Switch2ControllerModel.JoyCon2Right,
            Switch2InputCodec.JoyCon2Right08CharacteristicUuid,
            Switch2JoyConProfileMode.StandaloneHorizontalRight);
    }

    [TestMethod]
    public void CommonMapperRequiresExactReadNotifyGattProperties()
    {
        Switch2InputSessionDescriptor extraProperty = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Left, 1, 1,
            gattProperties: InputProperties | Switch2GattProperty.Write);

        Assert.IsFalse(Switch2JoyConProfileInputMapper.TryCreateStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft, extraProperty,
            out _));
    }

    [TestMethod]
    public void CommonMotionAndIrRemainExactAtProfileBoundary()
    {
        Switch2JoyConProfileInputFrame mapped = MapJoinedMotion(
            new Switch2Vector3Raw(10, -20, 30),
            new Switch2Vector3Raw(-40, 50, -60),
            new Switch2Vector3Raw(100, -200, 300),
            new Switch2Vector3Raw(-400, 500, -600));
        var state = new DS4State();
        Assert.IsTrue(mapped.TryWriteLegacyState(state));

        Assert.IsTrue(mapped.LeftSource.HasCommonMotion);
        Assert.AreEqual(new Switch2Vector3Raw(10, -20, 30),
            mapped.LeftSource.Accelerometer);
        Assert.AreEqual(new Switch2Vector3Raw(-40, 50, -60),
            mapped.LeftSource.Gyroscope);
        Assert.AreEqual(new Switch2Vector3Raw(100, -200, 300),
            mapped.RightSource.Accelerometer);
        Assert.AreEqual(new Switch2Vector3Raw(-400, 500, -600),
            mapped.RightSource.Gyroscope);
        Assert.AreEqual((ushort)111, mapped.LeftSource.IrX);
        Assert.AreEqual((ushort)222, mapped.LeftSource.IrY);
        Assert.AreEqual((ushort)555, mapped.LeftSource.IrRoughness);
        Assert.AreEqual((ushort)777, mapped.LeftSource.IrDistance);
        Assert.AreEqual((ushort)333, mapped.RightSource.IrX);
        Assert.AreEqual((ushort)444, mapped.RightSource.IrY);
        Assert.AreEqual((ushort)666, mapped.RightSource.IrRoughness);
        Assert.AreEqual((ushort)888, mapped.RightSource.IrDistance);

        Switch2JoyConRawInputStatus raw = state.Switch2JoyConRawInputStatus;
        Assert.AreEqual(Switch2JoyConProfileInputFrame.CurrentVersion,
            raw.ContractVersion);
        Assert.IsTrue(raw.LeftHasCommonMotion);
        Assert.IsTrue(raw.RightHasCommonMotion);
        Assert.AreEqual(new Switch2Vector3Raw(10, -20, 30),
            raw.LeftAccelerometer);
        Assert.AreEqual(new Switch2Vector3Raw(-40, 50, -60),
            raw.LeftGyroscope);
        Assert.AreEqual(new Switch2Vector3Raw(70, -80, 90),
            raw.LeftMagnetometer);
        Assert.AreEqual(new Switch2Vector3Raw(-100, 110, -120),
            raw.RightMagnetometer);
        Assert.AreEqual((ushort)111, raw.LeftIrX);
        Assert.AreEqual((ushort)222, raw.LeftIrY);
        Assert.AreEqual((ushort)555, raw.LeftIrRoughness);
        Assert.AreEqual((ushort)777, raw.LeftIrDistance);
        Assert.AreEqual((ushort)333, raw.RightIrX);
        Assert.AreEqual((ushort)444, raw.RightIrY);
        Assert.AreEqual((ushort)666, raw.RightIrRoughness);
        Assert.AreEqual((ushort)888, raw.RightIrDistance);
    }

    [TestMethod]
    public void JoinedMotionProjectionUsesPinnedAxesAndPhysicalScale()
    {
        Switch2JoyConProfileInputFrame mapped = MapJoinedMotion(
            default, default,
            new Switch2Vector3Raw(4096, -2048, 1024),
            new Switch2Vector3Raw(16384, 8192, -16384));
        var projection = new Switch2JoyConMotionProjection();
        var state = new DS4State();

        Assert.IsTrue(projection.TryApply(mapped, state,
            fusionEnabled: false, Switch2DualGyroDominantSide.Right));

        Assert.AreEqual(1000.0, state.Motion.angVelYaw, 0.01);
        Assert.AreEqual(1000.0, state.Motion.angVelPitch, 0.01);
        Assert.AreEqual(500.0, state.Motion.angVelRoll, 0.01);
        Assert.AreEqual(-1.0, state.Motion.accelXG, 0.0001);
        Assert.AreEqual(-0.25, state.Motion.accelYG, 0.0001);
        Assert.AreEqual(0.5, state.Motion.accelZG, 0.0001);
    }

    [TestMethod]
    public void JoinedProjectionAppliesProfileSoftDeadzoneBeforeSixAxis()
    {
        Switch2JoyConProfileInputFrame mapped = MapJoinedMotion(
            default, default,
            new Switch2Vector3Raw(4096, -2048, 1024),
            new Switch2Vector3Raw(16384, 8192, -16384));
        var baselineProjection = new Switch2JoyConMotionProjection();
        var deadzoneProjection = new Switch2JoyConMotionProjection();
        var baseline = new DS4State();
        var filtered = new DS4State();
        var policy = new Switch2DualGyroRuntimePolicy(false,
            Switch2DualGyroMode.SwitchDominantSide,
            Switch2DualGyroDominantSide.Right, leftActive: true,
            rightActive: true, configurationEpoch: 1);

        Assert.IsTrue(baselineProjection.TryApply(mapped, baseline,
            policy));
        Assert.IsTrue(deadzoneProjection.TryApply(mapped, filtered,
            policy, magnetometerYawAssistEnabled: false,
            virtualGyroSoftDeadzone: 100.0));

        Assert.IsTrue(Math.Abs(filtered.Motion.angVelYaw) <
            Math.Abs(baseline.Motion.angVelYaw));
        Assert.IsTrue(Math.Abs(filtered.Motion.angVelPitch) <
            Math.Abs(baseline.Motion.angVelPitch));
        Assert.AreEqual(baseline.Motion.angVelRoll,
            filtered.Motion.angVelRoll, 0.0001);
    }

    [TestMethod]
    public void JoinedProjectionAppliesHorizonMotionInSameSixAxisPath()
    {
        Switch2JoyConProfileInputFrame first = MapJoinedMotion(default,
            default, new Switch2Vector3Raw(0, 0, 4096),
            new Switch2Vector3Raw(160, 80, 320),
            completionTimestampQpc: 100);
        Switch2JoyConProfileInputFrame second = MapJoinedMotion(default,
            default, new Switch2Vector3Raw(0, 0, 4096),
            new Switch2Vector3Raw(160, 80, 320),
            completionTimestampQpc: 100_100);
        var projection = new Switch2JoyConMotionProjection();
        var state = new DS4State();
        var policy = new Switch2DualGyroRuntimePolicy(false,
            Switch2DualGyroMode.SwitchDominantSide,
            Switch2DualGyroDominantSide.Right, leftActive: true,
            rightActive: true, configurationEpoch: 1);

        Assert.IsTrue(projection.TryApply(first, state, policy,
            magnetometerYawAssistEnabled: false,
            virtualGyroSoftDeadzone: 0.0,
            horizonStabilizationEnabled: true));
        Assert.IsTrue(projection.TryApply(second, state, policy,
            magnetometerYawAssistEnabled: false,
            virtualGyroSoftDeadzone: 0.0,
            horizonStabilizationEnabled: true));

        Assert.AreEqual(0.0, state.Motion.angVelRoll, 0.0001,
            "Horizon projection removes the local roll lane before SixAxis.");
        Assert.IsTrue(Math.Abs(state.Motion.angVelYaw) > 0.0);
        Assert.IsTrue(Math.Abs(state.Motion.angVelPitch) > 0.0);
    }

    [TestMethod]
    public void HorizontalStandaloneProjectionUsesHorizontalHorizonAxes()
    {
        Switch2JoyConProfileInputFrame first = MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalRight, 0,
            acceleration: new Switch2Vector3Raw(0, 0, 4096),
            gyroscope: new Switch2Vector3Raw(160, 80, 320),
            completionTimestampQpc: 100);
        Switch2JoyConProfileInputFrame second = MapStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalRight, 0,
            acceleration: new Switch2Vector3Raw(0, 0, 4096),
            gyroscope: new Switch2Vector3Raw(160, 80, 320),
            completionTimestampQpc: 100_100);
        var projection = new Switch2JoyConMotionProjection();
        var state = new DS4State();
        var policy = new Switch2DualGyroRuntimePolicy(false,
            Switch2DualGyroMode.SwitchDominantSide,
            Switch2DualGyroDominantSide.Right, leftActive: true,
            rightActive: true, configurationEpoch: 1);

        Assert.IsTrue(projection.TryApply(first, state, policy,
            magnetometerYawAssistEnabled: false,
            virtualGyroSoftDeadzone: 0.0,
            horizonStabilizationEnabled: true));
        Assert.IsTrue(projection.TryApply(second, state, policy,
            magnetometerYawAssistEnabled: false,
            virtualGyroSoftDeadzone: 0.0,
            horizonStabilizationEnabled: true));

        Assert.AreEqual(0.0, state.Motion.angVelRoll, 0.0001);
        Assert.IsTrue(Math.Abs(state.Motion.angVelYaw) > 0.0);
        Assert.IsTrue(Math.Abs(state.Motion.angVelPitch) > 0.0);
    }

    [TestMethod]
    public void JoinedSidecarsRetainSideSpecificActivationButtons()
    {
        Switch2JoyConProfileInputFrame mapped = MapJoined(
            leftButtons: 1u << 21, rightButtons: 1u << 5);
        Assert.AreEqual(Switch2JoyConProfileButton.LeftRailSL,
            mapped.LeftSource.Buttons);
        Assert.AreEqual(Switch2JoyConProfileButton.RightRailSL,
            mapped.RightSource.Buttons);
        Assert.AreEqual(Switch2JoyConProfileButton.LeftRailSL |
            Switch2JoyConProfileButton.RightRailSL, mapped.Buttons);
    }

    [TestMethod]
    public void SwitchGyroSidePolicySelectsExactlyOneMotionSource()
    {
        Switch2JoyConProfileInputFrame mapped = MapJoinedMotion(
            default, new Switch2Vector3Raw(16384, 0, 0),
            default, new Switch2Vector3Raw(0, 8192, 0));
        var leftProjection = new Switch2JoyConMotionProjection();
        var leftState = new DS4State();
        var leftPolicy = new Switch2DualGyroRuntimePolicy(true,
            Switch2DualGyroMode.SwitchGyroSide,
            Switch2DualGyroDominantSide.Left, leftActive: true,
            rightActive: false, configurationEpoch: 1);
        Assert.IsTrue(leftProjection.TryApply(mapped, leftState,
            leftPolicy));
        Assert.AreEqual(1000.0, leftState.Motion.angVelPitch, 0.01);
        Assert.AreEqual(0.0, leftState.Motion.angVelRoll, 0.01);

        var rightProjection = new Switch2JoyConMotionProjection();
        var rightState = new DS4State();
        var rightPolicy = new Switch2DualGyroRuntimePolicy(true,
            Switch2DualGyroMode.SwitchGyroSide,
            Switch2DualGyroDominantSide.Right, leftActive: false,
            rightActive: true, configurationEpoch: 1);
        Assert.IsTrue(rightProjection.TryApply(mapped, rightState,
            rightPolicy));
        Assert.AreEqual(0.0, rightState.Motion.angVelPitch, 0.01);
        Assert.AreEqual(500.0, rightState.Motion.angVelRoll, 0.01);
    }

    [TestMethod]
    public void WarmMotionProjectionAllocatesNothing()
    {
        Switch2JoyConProfileInputFrame mapped = MapJoinedMotion(
            new Switch2Vector3Raw(1, 2, 3),
            new Switch2Vector3Raw(40, 0, 0),
            new Switch2Vector3Raw(4, 5, 6),
            new Switch2Vector3Raw(20, 0, 0));
        var projection = new Switch2JoyConMotionProjection();
        var state = new DS4State();
        bool succeeded = true;
        for (int warmup = 0; warmup < 2_000; warmup++)
        {
            succeeded &= projection.TryApply(mapped, state,
                fusionEnabled: true,
                Switch2DualGyroDominantSide.Left);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            succeeded &= projection.TryApply(mapped, state,
                fusionEnabled: true,
                Switch2DualGyroDominantSide.Left);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void MapperCounterFenceAcceptsWrapAndRejectsPreWrapReplay()
    {
        Switch2InputSessionDescriptor descriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Left, 3, 4);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft, descriptor,
            out var initial));
        Switch2CanonicalInputFrame beforeWrap = CreateCommonFrame(descriptor,
            0xFFFFFFFC, 0, 0x800, 0x800, 100);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(initial,
            beforeWrap, out var accepted, out _, out var firstFailure),
            firstFailure.ToString());
        Switch2CanonicalInputFrame wrapped = CreateCommonFrame(descriptor, 0,
            0, 0x800, 0x800, 101);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(accepted,
            wrapped, out var afterWrap, out _, out var wrapFailure),
            wrapFailure.ToString());
        Assert.AreEqual(0u, afterWrap.LastLeftCounter);

        Switch2CanonicalInputFrame replay = CreateCommonFrame(descriptor,
            0xFFFFFFF0, 0, 0x800, 0x800, 102);
        Assert.IsFalse(Switch2JoyConProfileInputMapper.TryMapStandalone(
            afterWrap, replay, out var unchanged, out _, out var failure));
        Assert.AreEqual(
            Switch2JoyConProfileInputFailure.BackwardOrOutOfOrder, failure);
        Assert.AreEqual(afterWrap.LastLeftCounter,
            unchanged.LastLeftCounter);
    }

    [TestMethod]
    public void WarmJoinedMapAndLegacyProjectionAllocateNothing()
    {
        Switch2InputSessionDescriptor leftDescriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Left, 1, 1);
        Switch2InputSessionDescriptor rightDescriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Right, 1, 1);
        Switch2JoyConPairSnapshot snapshot = CreateSnapshot(1,
            leftDescriptor, rightDescriptor, 1, 1, 100, 100,
            leftButtons: (1u << 21) | (1u << 20),
            rightButtons: (1u << 2) | (1u << 14) | (1u << 5) | (1u << 4));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateJoined(1,
            leftDescriptor, rightDescriptor, out var mapperState));
        var legacy = new DS4State();
        bool succeeded = true;
        for (int warmup = 0; warmup < 2000; warmup++)
        {
            succeeded &= Switch2JoyConProfileInputMapper.TryMapJoined(
                mapperState, snapshot, out var next, out var mapped, out _);
            succeeded &= mapped.TryWriteLegacyState(legacy);
            mapperState = next;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            succeeded &= Switch2JoyConProfileInputMapper.TryMapJoined(
                mapperState, snapshot, out var next, out var mapped, out _);
            succeeded &= mapped.TryWriteLegacyState(legacy);
            mapperState = next;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void WarmStandaloneMapAndLegacyProjectionAllocateNothing()
    {
        Switch2InputSessionDescriptor descriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Right, 5, 8);
        Switch2CanonicalInputFrame canonical = CreateCommonFrame(descriptor,
            7, (1u << 2) | (1u << 7) | (1u << 14), 0x123, 0xABC,
            100);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalRight, descriptor,
            out var mapperState));
        var legacy = new DS4State();
        bool succeeded = true;
        for (int warmup = 0; warmup < 2000; warmup++)
        {
            succeeded &= Switch2JoyConProfileInputMapper.TryMapStandalone(
                mapperState, canonical, out var next, out var mapped, out _);
            succeeded &= mapped.TryWriteLegacyState(legacy);
            mapperState = next;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            succeeded &= Switch2JoyConProfileInputMapper.TryMapStandalone(
                mapperState, canonical, out var next, out var mapped, out _);
            succeeded &= mapped.TryWriteLegacyState(legacy);
            mapperState = next;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void StandaloneSidesCalibrateTheirOwnPhysicalImu()
    {
        AssertStandaloneGyroCalibration(
            Switch2JoyConProfileMode.StandaloneVerticalLeft,
            new Switch2Vector3Raw(8, -4, 2));
        AssertStandaloneGyroCalibration(
            Switch2JoyConProfileMode.StandaloneVerticalRight,
            new Switch2Vector3Raw(-6, 3, -2));
    }

    private static void AssertStandaloneGyroCalibration(
        Switch2JoyConProfileMode mode, Switch2Vector3Raw bias)
    {
        Switch2ControllerModel model =
            Switch2JoyConProfileInputMapper.IsStandaloneLeftMode(mode) ?
                Switch2ControllerModel.JoyCon2Left :
                Switch2ControllerModel.JoyCon2Right;
        Switch2InputSessionDescriptor descriptor = CreateCommonDescriptor(
            model, 51, 61);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(mode,
            descriptor, out var mapperState));
        var projection = new Switch2JoyConMotionProjection();
        var state = new DS4State();
        var policy = new Switch2DualGyroRuntimePolicy(false,
            Switch2DualGyroMode.SwitchDominantSide,
            Switch2DualGyroDominantSide.Right, leftActive: true,
            rightActive: true, configurationEpoch: 1);
        for (int index = 0; index <= 501; index++)
        {
            Switch2CanonicalInputFrame canonical = CreateCommonFrame(
                descriptor, (uint)(index + 1), 0, 0x800, 0x800,
                index * 100_000L,
                acceleration: new Switch2Vector3Raw(0, 4096, 0),
                gyroscope: bias);
            Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(
                mapperState, canonical, out var next, out var mapped,
                out var failure), failure.ToString());
            mapperState = next;
            Assert.IsTrue(projection.TryApply(mapped, state, policy));
        }

        Assert.AreEqual(model == Switch2ControllerModel.JoyCon2Left,
            projection.HasCalibratedLeftGyroBias);
        Assert.AreEqual(model == Switch2ControllerModel.JoyCon2Right,
            projection.HasCalibratedRightGyroBias);
        Assert.AreEqual(0.0, state.Motion.angVelYaw, 0.01);
        Assert.AreEqual(0.0, state.Motion.angVelPitch, 0.01);
        Assert.AreEqual(0.0, state.Motion.angVelRoll, 0.01);
    }

    private static Switch2JoyConProfileInputFrame MapJoined(uint leftButtons,
        uint rightButtons, ushort leftX = 0x800, ushort leftY = 0x800,
        ushort rightX = 0x800, ushort rightY = 0x800)
    {
        Switch2InputSessionDescriptor leftDescriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Left, 1, 1);
        Switch2InputSessionDescriptor rightDescriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Right, 1, 1);
        Switch2JoyConPairSnapshot snapshot = CreateSnapshot(1,
            leftDescriptor, rightDescriptor, 1, 1, 100, 100, leftButtons,
            rightButtons, leftX, leftY, rightX, rightY);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateJoined(1,
            leftDescriptor, rightDescriptor, out var state));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapJoined(state,
            snapshot, out _, out var mapped, out var failure),
            failure.ToString());
        return mapped;
    }

    private static Switch2JoyConProfileInputFrame MapJoinedMotion(
        Switch2Vector3Raw leftAcceleration, Switch2Vector3Raw leftGyro,
        Switch2Vector3Raw rightAcceleration, Switch2Vector3Raw rightGyro,
        long completionTimestampQpc = 100)
    {
        Switch2InputSessionDescriptor leftDescriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Left, 1, 1);
        Switch2InputSessionDescriptor rightDescriptor =
            CreateCommonDescriptor(Switch2ControllerModel.JoyCon2Right, 1, 1);
        Switch2CanonicalInputFrame left = CreateCommonFrame(leftDescriptor,
            1, 0, 0x800, 0x800, completionTimestampQpc,
            acceleration: leftAcceleration,
            gyroscope: leftGyro,
            magnetometer: new Switch2Vector3Raw(70, -80, 90),
            irX: 111, irY: 222, irRoughness: 555, irDistance: 777);
        Switch2CanonicalInputFrame right = CreateCommonFrame(rightDescriptor,
            1, 0, 0x800, 0x800, completionTimestampQpc,
            acceleration: rightAcceleration,
            gyroscope: rightGyro,
            magnetometer: new Switch2Vector3Raw(-100, 110, -120),
            irX: 333, irY: 444, irRoughness: 666, irDistance: 888);
        var snapshot = new Switch2JoyConPairSnapshot(1, left, right, 0);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateJoined(1,
            leftDescriptor, rightDescriptor, out var mapper));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapJoined(mapper,
            snapshot, out _, out var mapped, out var failure),
            failure.ToString());
        return mapped;
    }

    private static Switch2JoyConProfileInputFrame MapStandalone(
        Switch2JoyConProfileMode mode, uint buttons,
        ushort physicalX = 0x800, ushort physicalY = 0x800,
        Switch2Vector3Raw acceleration = default,
        Switch2Vector3Raw gyroscope = default,
        long completionTimestampQpc = 100)
    {
        Switch2ControllerModel model =
            Switch2JoyConProfileInputMapper.IsStandaloneLeftMode(mode) ?
            Switch2ControllerModel.JoyCon2Left :
            Switch2ControllerModel.JoyCon2Right;
        Switch2InputSessionDescriptor descriptor = CreateCommonDescriptor(
            model, 1, 1);
        Switch2CanonicalInputFrame canonical = CreateCommonFrame(descriptor,
            1, buttons, physicalX, physicalY, completionTimestampQpc,
            acceleration: acceleration, gyroscope: gyroscope);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(mode,
            descriptor, out var state));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(state,
            canonical, out _, out var mapped, out var failure),
            failure.ToString());
        return mapped;
    }

    private static Switch2JoyConPairSnapshot CreateSnapshot(ulong pairEpoch,
        in Switch2InputSessionDescriptor leftDescriptor,
        in Switch2InputSessionDescriptor rightDescriptor,
        uint leftCounter, uint rightCounter, long leftTimestamp,
        long rightTimestamp, uint leftButtons = 0, uint rightButtons = 0,
        ushort leftX = 0x800, ushort leftY = 0x800,
        ushort rightX = 0x800, ushort rightY = 0x800)
    {
        Switch2CanonicalInputFrame left = CreateCommonFrame(leftDescriptor,
            leftCounter, leftButtons, leftX, leftY, leftTimestamp);
        Switch2CanonicalInputFrame right = CreateCommonFrame(rightDescriptor,
            rightCounter, rightButtons, rightX, rightY, rightTimestamp);
        ulong skew = leftTimestamp >= rightTimestamp ?
            (ulong)(leftTimestamp - rightTimestamp) :
            (ulong)(rightTimestamp - leftTimestamp);
        return new Switch2JoyConPairSnapshot(pairEpoch, left, right, skew);
    }

    private static Switch2CanonicalInputFrame CreateCommonFrame(
        in Switch2InputSessionDescriptor descriptor, uint counter,
        uint buttons, ushort physicalX, ushort physicalY, long timestamp,
        byte[] calibrationRecord = null,
        Switch2Vector3Raw acceleration = default,
        Switch2Vector3Raw gyroscope = default,
        Switch2Vector3Raw magnetometer = default, ushort irX = 0,
        ushort irY = 0, ushort irRoughness = 0, ushort irDistance = 0)
    {
        Switch2ControllerModel model = descriptor.Identity.Model;
        bool created = calibrationRecord == null ?
            Switch2InputCalibrationSnapshot.TryCreateFallback(model,
                descriptor.DeviceGeneration, out var calibration) :
            Switch2InputCalibrationSnapshot.TryCreate(model,
                descriptor.DeviceGeneration,
                model == Switch2ControllerModel.JoyCon2Left ?
                    calibrationRecord : ReadOnlySpan<byte>.Empty,
                model == Switch2ControllerModel.JoyCon2Right ?
                    calibrationRecord : ReadOnlySpan<byte>.Empty,
                out calibration);
        Assert.IsTrue(created);
        var session = new Switch2InputSession(descriptor, calibration);
        byte[] body = BuildCommonBody(counter, buttons,
            model == Switch2ControllerModel.JoyCon2Left ? physicalX :
                (ushort)0xBAD,
            model == Switch2ControllerModel.JoyCon2Left ? physicalY :
                (ushort)0xBAD,
            model == Switch2ControllerModel.JoyCon2Right ? physicalX :
                (ushort)0xBAD,
            model == Switch2ControllerModel.JoyCon2Right ? physicalY :
                (ushort)0xBAD, acceleration, gyroscope, magnetometer, irX,
            irY, irRoughness, irDistance);
        Assert.IsTrue(session.TryProcess(descriptor, body, timestamp,
            out var frame, out var failure), failure.ToString());
        return frame;
    }

    private static Switch2InputSessionDescriptor CreateCommonDescriptor(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration, long qpcFrequency = 10_000_000,
        Switch2GattProperty gattProperties = InputProperties)
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid, gattProperties,
            model, out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity,
            deviceGeneration, transportGeneration, qpcFrequency,
            out var descriptor));
        return descriptor;
    }

    private static byte[] BuildCommonBody(uint counter, uint buttons,
        ushort leftX, ushort leftY, ushort rightX, ushort rightY,
        Switch2Vector3Raw acceleration = default,
        Switch2Vector3Raw gyroscope = default,
        Switch2Vector3Raw magnetometer = default, ushort irX = 0,
        ushort irY = 0, ushort irRoughness = 0, ushort irDistance = 0)
    {
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), counter);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), buttons);
        PackStick(body, 0x0A, leftX, leftY);
        PackStick(body, 0x0D, rightX, rightY);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x10, 2), irX);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x12, 2), irY);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x14, 2),
            irRoughness);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x16, 2),
            irDistance);
        WriteVector(body, 0x19, magnetometer);
        WriteVector(body, 0x30, acceleration);
        WriteVector(body, 0x36, gyroscope);
        return body;
    }

    private static void WriteVector(byte[] destination, int offset,
        Switch2Vector3Raw value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(
            destination.AsSpan(offset, 2), value.X);
        BinaryPrimitives.WriteInt16LittleEndian(
            destination.AsSpan(offset + 2, 2), value.Y);
        BinaryPrimitives.WriteInt16LittleEndian(
            destination.AsSpan(offset + 4, 2), value.Z);
    }

    private static byte[] BuildCalibration(ushort neutralX, ushort neutralY,
        ushort positiveX, ushort positiveY, ushort negativeX,
        ushort negativeY)
    {
        var record = new byte[Switch2CalibrationCodec.StickCalibrationLength];
        PackStick(record, 0, neutralX, neutralY);
        PackStick(record, 3, positiveX, positiveY);
        PackStick(record, 6, negativeX, negativeY);
        return record;
    }

    private static void PackStick(byte[] destination, int offset, ushort x,
        ushort y)
    {
        Assert.IsTrue(x <= 0x0FFF && y <= 0x0FFF);
        destination[offset] = (byte)x;
        destination[offset + 1] = (byte)(((x >> 8) & 0x0F) |
            ((y & 0x0F) << 4));
        destination[offset + 2] = (byte)(y >> 4);
    }

    private static void AssertAxis(Switch2JoyConProfileAxis actual,
        ushort raw, short signed)
    {
        Assert.AreEqual(raw, actual.RawValue);
        Assert.AreEqual(signed, actual.SignedValue);
    }

    private static void AssertLegacyExactly(
        in Switch2JoyConProfileInputFrame mapped, string expected)
    {
        var state = new DS4State();
        Assert.IsTrue(mapped.TryWriteLegacyState(state));
        List<string> active = ReadActiveLegacyControls(state);
        if (string.IsNullOrEmpty(expected))
        {
            Assert.AreEqual(0, active.Count,
                "C and rail/paddle controls must remain sidecar-only.");
        }
        else
        {
            CollectionAssert.AreEqual(new[] { expected }, active);
        }
    }

    private static void AssertRejected(
        in Switch2JoyConProfileMapperState state,
        in Switch2JoyConPairSnapshot snapshot,
        Switch2JoyConProfileInputFailure expected)
    {
        Assert.IsFalse(Switch2JoyConProfileInputMapper.TryMapJoined(state,
            snapshot, out var next, out _, out var failure));
        Assert.AreEqual(expected, failure);
        Assert.AreEqual(state.LastLeftCounter, next.LastLeftCounter);
        Assert.AreEqual(state.LastRightCounter, next.LastRightCounter);
        Assert.AreEqual(state.LastLeftTimestampQpc,
            next.LastLeftTimestampQpc);
        Assert.AreEqual(state.LastRightTimestampQpc,
            next.LastRightTimestampQpc);
    }

    private static void AssertDedicatedReportRejected(
        Switch2ControllerModel model, Guid characteristic,
        Switch2JoyConProfileMode mode)
    {
        Switch2InputSessionDescriptor common = CreateCommonDescriptor(model,
            1, 1);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(mode,
            common, out var state));
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid, characteristic, InputProperties,
            model, out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity, 1, 1,
            10_000_000, out var dedicated));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(model,
            1, out var calibration));
        var session = new Switch2InputSession(dedicated, calibration);
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        Assert.IsTrue(session.TryProcess(dedicated, body, 1,
            out var canonical, out var sessionFailure),
            sessionFailure.ToString());

        Assert.IsFalse(Switch2JoyConProfileInputMapper.TryMapStandalone(state,
            canonical, out _, out _, out var failure));
        Assert.AreEqual(Switch2JoyConProfileInputFailure.UnsupportedReport,
            failure);
    }

    private static DS4State CreateDirtyLegacyState() => new()
    {
        PacketCounter = 0x10203040,
        Square = true,
        Triangle = true,
        Circle = true,
        Cross = true,
        DpadUp = true,
        DpadDown = true,
        DpadLeft = true,
        DpadRight = true,
        L1 = true,
        L2Btn = true,
        L3 = true,
        R1 = true,
        R2Btn = true,
        R3 = true,
        Share = true,
        Options = true,
        PS = true,
        Mute = true,
        Capture = true,
        SideL = true,
        SideR = true,
        FnL = true,
        FnR = true,
        BLP = true,
        BRP = true,
        Touch1 = true,
        Touch2 = true,
        TouchButton = true,
        OutputTouchButton = true,
        L2 = 255,
        L2Raw = 255,
        R2 = 255,
        R2Raw = 255,
        OutputLSOuter = 255,
        OutputRSOuter = 255,
        SASteeringWheelEmulationUnit = 12345,
    };

    private static List<string> ReadActiveLegacyControls(DS4State state)
    {
        var active = new List<string>();
        Add(state.Square, nameof(DS4State.Square));
        Add(state.Triangle, nameof(DS4State.Triangle));
        Add(state.Cross, nameof(DS4State.Cross));
        Add(state.Circle, nameof(DS4State.Circle));
        Add(state.L1, nameof(DS4State.L1));
        Add(state.L2Btn, nameof(DS4State.L2Btn));
        Add(state.L3, nameof(DS4State.L3));
        Add(state.R1, nameof(DS4State.R1));
        Add(state.R2Btn, nameof(DS4State.R2Btn));
        Add(state.R3, nameof(DS4State.R3));
        Add(state.Share, nameof(DS4State.Share));
        Add(state.Options, nameof(DS4State.Options));
        Add(state.PS, nameof(DS4State.PS));
        Add(state.Capture, nameof(DS4State.Capture));
        Add(state.DpadUp, nameof(DS4State.DpadUp));
        Add(state.DpadRight, nameof(DS4State.DpadRight));
        Add(state.DpadDown, nameof(DS4State.DpadDown));
        Add(state.DpadLeft, nameof(DS4State.DpadLeft));
        Add(state.BLP, nameof(DS4State.BLP));
        Add(state.BRP, nameof(DS4State.BRP));
        Add(state.Mute, nameof(DS4State.Mute));
        Add(state.FnL, nameof(DS4State.FnL));
        Add(state.FnR, nameof(DS4State.FnR));
        Add(state.SideL, nameof(DS4State.SideL));
        Add(state.SideR, nameof(DS4State.SideR));
        return active;

        void Add(bool pressed, string name)
        {
            if (pressed)
            {
                active.Add(name);
            }
        }
    }
}
