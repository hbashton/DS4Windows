using System.Buffers.Binary;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2ProProfileInputTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SourceProjectionPublishesAdjacentMappedStickValuesBeforeLegacyQuantization(bool bluetooth)
    {
        for (ushort raw = 2048; raw <= 2054; raw++)
        {
            var canonical = bluetooth ? CreateBleProFrame(leftX: raw, leftY: (ushort)(4096 - raw),
                rightX: (ushort)(4096 - raw), rightY: raw) :
                CreateUsbFrame(leftX: raw, leftY: (ushort)(4096 - raw), rightX: (ushort)(4096 - raw), rightY: raw);
            Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(canonical, out var frame, out _));
            var source = new DS4State();
            Assert.IsTrue(frame.TryWriteLegacyState(source));
            Assert.AreEqual(frame.LeftX.SignedValue, source.LXAxis.ToSigned16());
            Assert.AreEqual(frame.LeftY.SignedValue, source.LYAxis.ToSigned16());
            Assert.AreEqual(frame.RightX.SignedValue, source.RXAxis.ToSigned16());
            Assert.AreEqual(frame.RightY.SignedValue, source.RYAxis.ToSigned16());
            Assert.IsTrue(source.LXAxis.IsHighResolution);
            Assert.AreEqual((byte)128, source.LX, "All these distinct reports share one compatibility byte.");
        }
    }

    [TestMethod]
    public void FaceButtonLayoutCanPreserveXboxPositionsOrNintendoLabels()
    {
        var cases = new[]
        {
            (Switch2ProButton.FaceWest, nameof(DS4State.Square),
                nameof(DS4State.Triangle)),
            (Switch2ProButton.FaceNorth, nameof(DS4State.Triangle),
                nameof(DS4State.Square)),
            (Switch2ProButton.FaceSouth, nameof(DS4State.Cross),
                nameof(DS4State.Circle)),
            (Switch2ProButton.FaceEast, nameof(DS4State.Circle),
                nameof(DS4State.Cross)),
        };

        foreach ((Switch2ProButton button, string xbox,
            string nintendo) in cases)
        {
            Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(
                CreateUsbFrame(buttons: (uint)button), out var mapped,
                out var failure), failure.ToString());

            var state = new DS4State();
            Assert.IsTrue(mapped.TryWriteLegacyState(state,
                Switch2FaceButtonLayout.Xbox));
            CollectionAssert.AreEqual(new[] { xbox },
                ReadActiveLegacyControls(state));

            Assert.IsTrue(mapped.TryWriteLegacyState(state,
                Switch2FaceButtonLayout.Nintendo));
            CollectionAssert.AreEqual(new[] { nintendo },
                ReadActiveLegacyControls(state));
            Assert.AreEqual((uint)button,
                state.Switch2RawInputStatus.RawButtonBits,
                "Layout presentation must not rewrite the raw sidecar.");
        }

        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(
            CreateUsbFrame(buttons: (uint)Switch2ProButton.FaceSouth),
            out var invalid, out var invalidFailure),
            invalidFailure.ToString());
        Assert.IsFalse(invalid.TryWriteLegacyState(new DS4State(),
            (Switch2FaceButtonLayout)99));
    }

    [TestMethod]
    public void EveryProSemanticMapsExactlyOnceAndCRemainsDistinct()
    {
        var mappings = new (Switch2ProButton Button, string LegacyName)[]
        {
            (Switch2ProButton.FaceWest, nameof(DS4State.Square)),
            (Switch2ProButton.FaceNorth, nameof(DS4State.Triangle)),
            (Switch2ProButton.FaceSouth, nameof(DS4State.Cross)),
            (Switch2ProButton.FaceEast, nameof(DS4State.Circle)),
            (Switch2ProButton.LeftShoulder, nameof(DS4State.L1)),
            (Switch2ProButton.RightShoulder, nameof(DS4State.R1)),
            (Switch2ProButton.LeftTrigger, nameof(DS4State.L2Btn)),
            (Switch2ProButton.RightTrigger, nameof(DS4State.R2Btn)),
            (Switch2ProButton.Back, nameof(DS4State.Share)),
            (Switch2ProButton.Start, nameof(DS4State.Options)),
            (Switch2ProButton.LeftStick, nameof(DS4State.L3)),
            (Switch2ProButton.RightStick, nameof(DS4State.R3)),
            (Switch2ProButton.Guide, nameof(DS4State.PS)),
            (Switch2ProButton.Capture, nameof(DS4State.Capture)),
            (Switch2ProButton.DpadUp, nameof(DS4State.DpadUp)),
            (Switch2ProButton.DpadRight, nameof(DS4State.DpadRight)),
            (Switch2ProButton.DpadDown, nameof(DS4State.DpadDown)),
            (Switch2ProButton.DpadLeft, nameof(DS4State.DpadLeft)),
            (Switch2ProButton.LeftPaddle, nameof(DS4State.BLP)),
            (Switch2ProButton.RightPaddle, nameof(DS4State.BRP)),
            (Switch2ProButton.C, string.Empty),
        };

        uint union = 0;
        foreach ((Switch2ProButton button, string expected) in mappings)
        {
            union |= (uint)button;
            Switch2CanonicalInputFrame canonical = CreateUsbFrame(
                buttons: (uint)button);
            Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(canonical,
                out var mapped, out var failure), failure.ToString());
            var state = new DS4State();
            Assert.IsTrue(mapped.TryWriteLegacyState(state));

            string[] active = ReadActiveLegacyControls(state);
            if (button == Switch2ProButton.C)
            {
                Assert.AreEqual(0, active.Length,
                    "C must not masquerade as a DualSense mute press.");
                Assert.IsTrue(mapped.CButton);
                Assert.IsTrue(state.Switch2RawInputStatus.CButton);
                Assert.IsFalse(state.Mute);
            }
            else
            {
                CollectionAssert.AreEqual(new[] { expected }, active,
                    $"Unexpected legacy projection for {button}.");
                Assert.IsFalse(mapped.CButton);
            }
        }

        Assert.AreEqual(Switch2ProUsbInputProjection.KnownButtonMask, union,
            "The test table must cover every evidenced Pro button exactly once.");
    }

    [TestMethod]
    public void CalibratedAxesPreserveTwelveBitsInvertYAndReachEndpoints()
    {
        Switch2CanonicalInputFrame endpoints = CreateUsbFrame(
            leftX: 0, leftY: 0x0FFF, rightX: 0x0FFF, rightY: 0);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(endpoints,
            out var mapped, out var failure), failure.ToString());

        AssertAxis(mapped.LeftX, 0, short.MinValue, 0);
        AssertAxis(mapped.LeftY, 0x0FFF, short.MinValue, 0);
        AssertAxis(mapped.RightX, 0x0FFF, short.MaxValue, 255);
        AssertAxis(mapped.RightY, 0, short.MaxValue, 255);
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.FallbackMissing,
            mapped.LeftCalibrationStatus);

        Switch2CanonicalInputFrame center = CreateUsbFrame(leftX: 0x0800,
            leftY: 0x0800, rightX: 0x0800, rightY: 0x0800);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(center,
            out mapped, out failure), failure.ToString());
        AssertAxis(mapped.LeftX, 0x0800, 0, 128);
        AssertAxis(mapped.LeftY, 0x0800, 0, 128);
        AssertAxis(mapped.RightX, 0x0800, 0, 128);
        AssertAxis(mapped.RightY, 0x0800, 0, 128);
    }

    [TestMethod]
    public void FactoryCalibrationIsGenerationBoundAndClampsBeyondEndpoints()
    {
        byte[] calibration = BuildCalibration(neutralX: 1000,
            neutralY: 2000, positiveX: 500, positiveY: 700,
            negativeX: 300, negativeY: 400);
        Switch2CanonicalInputFrame frame = CreateUsbFrame(leftX: 600,
            leftY: 3000, rightX: 1600, rightY: 1500,
            calibrationRecord: calibration, deviceGeneration: 9);

        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(frame,
            out var mapped, out var failure), failure.ToString());
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.AdoptedFactory,
            mapped.LeftCalibrationStatus);
        AssertAxis(mapped.LeftX, 600, short.MinValue, 0);
        AssertAxis(mapped.LeftY, 3000, short.MinValue, 0);
        AssertAxis(mapped.RightX, 1600, short.MaxValue, 255);
        Assert.AreEqual((short)32767, mapped.RightY.SignedValue,
            "Raw Y below neutral maps down after the evidenced inversion.");
    }

    [TestMethod]
    public void CompatibilityWriteClearsReleasedAndUnsupportedControls()
    {
        var state = new DS4State
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
        Switch2CanonicalInputFrame canonical = CreateUsbFrame(buttons: 0,
            leftX: 0x0800, leftY: 0x0800, rightX: 0x0800,
            rightY: 0x0800);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(canonical,
            out var mapped, out var failure), failure.ToString());

        Assert.IsTrue(mapped.TryWriteLegacyState(state));
        Assert.AreEqual(0, ReadActiveLegacyControls(state).Length);
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
            "The input lane owns DS4State's host packet sequence.");
        Assert.IsTrue(state.Switch2RawInputStatus.IsValid);
        Assert.IsFalse(state.DualSenseRawInputStatus.IsValid);
    }

    [TestMethod]
    public void RawStatusSurvivesAllMappingCopyPaths()
    {
        const uint unknownBit = 0x80000000;
        Switch2CanonicalInputFrame canonical = CreateUsbFrame(
            buttons: (uint)Switch2ProButton.C | unknownBit,
            leftX: 0x123, leftY: 0x456, rightX: 0x789, rightY: 0xABC,
            deviceGeneration: 7, transportGeneration: 11,
            timestamp: 1234567, qpcFrequency: 10_000_000);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(canonical,
            out var mapped, out var failure), failure.ToString());
        var source = new DS4State();
        Assert.IsTrue(mapped.TryWriteLegacyState(source));
        Switch2RawInputStatus expected = source.Switch2RawInputStatus;

        var constructed = new DS4State(source);
        var copied = new DS4State();
        source.CopyTo(copied);
        var extras = new DS4State();
        source.CopyExtrasTo(extras);

        Assert.AreEqual(expected, constructed.Switch2RawInputStatus);
        Assert.AreEqual(expected, copied.Switch2RawInputStatus);
        Assert.AreEqual(expected, extras.Switch2RawInputStatus);
        Assert.AreEqual(7UL, expected.DeviceGeneration);
        Assert.AreEqual(11UL, expected.TransportGeneration);
        Assert.AreEqual(1234567L, expected.CompletionTimestampQpc);
        Assert.AreEqual(unknownBit, expected.UnknownButtonBits);
        Assert.IsTrue(expected.CButton);
        Assert.AreEqual((ushort)0x123, expected.LeftStickXRaw);
        Assert.AreEqual((ushort)0xABC, expected.RightStickYRaw);
    }

    [TestMethod]
    public void UsbAndBleCommon05ProduceTheSameProfileProjection()
    {
        const uint buttons = (uint)(Switch2ProButton.FaceSouth |
            Switch2ProButton.LeftShoulder | Switch2ProButton.RightTrigger |
            Switch2ProButton.C);
        Switch2CanonicalInputFrame usb = CreateUsbFrame(counter: 71,
            buttons: buttons, leftX: 0x123, leftY: 0x456,
            rightX: 0x789, rightY: 0xABC, timestamp: 91);
        Switch2CanonicalInputFrame ble = CreateBleProFrame(counter: 71,
            buttons: buttons, leftX: 0x123, leftY: 0x456,
            rightX: 0x789, rightY: 0xABC, timestamp: 91);

        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(usb,
            out var usbProfile, out var usbFailure), usbFailure.ToString());
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(ble,
            out var bleProfile, out var bleFailure), bleFailure.ToString());

        Assert.AreEqual(usbProfile.Buttons, bleProfile.Buttons);
        Assert.AreEqual(usbProfile.RawButtonBits, bleProfile.RawButtonBits);
        Assert.AreEqual(usbProfile.UnknownButtonBits,
            bleProfile.UnknownButtonBits);
        Assert.AreEqual(usbProfile.LeftX, bleProfile.LeftX);
        Assert.AreEqual(usbProfile.LeftY, bleProfile.LeftY);
        Assert.AreEqual(usbProfile.RightX, bleProfile.RightX);
        Assert.AreEqual(usbProfile.RightY, bleProfile.RightY);
        Assert.AreEqual(usbProfile.DeviceCounterRaw,
            bleProfile.DeviceCounterRaw);
        Assert.AreEqual(usbProfile.CompletionTimestampQpc,
            bleProfile.CompletionTimestampQpc);
        Assert.AreEqual(Switch2Transport.Usb, usbProfile.Transport);
        Assert.AreEqual(Switch2Transport.BluetoothLe, bleProfile.Transport);
        Assert.AreEqual(
            Switch2InputProtocolRevision.ProUsbCommon05Bcd0201,
            usbProfile.ProtocolRevision);
        Assert.AreEqual(
            Switch2InputProtocolRevision.BluetoothLeCommon05V1,
            bleProfile.ProtocolRevision);

        var usbState = new DS4State();
        var bleState = new DS4State();
        Assert.IsTrue(usbProfile.TryWriteLegacyState(usbState));
        Assert.IsTrue(bleProfile.TryWriteLegacyState(bleState));
        CollectionAssert.AreEqual(ReadActiveLegacyControls(usbState),
            ReadActiveLegacyControls(bleState));
        Assert.AreEqual(usbState.LX, bleState.LX);
        Assert.AreEqual(usbState.LY, bleState.LY);
        Assert.AreEqual(usbState.RX, bleState.RX);
        Assert.AreEqual(usbState.RY, bleState.RY);
        Assert.AreEqual(usbState.Switch2RawInputStatus.CButton,
            bleState.Switch2RawInputStatus.CButton);
        Assert.AreEqual(Switch2Transport.Usb,
            usbState.Switch2RawInputStatus.Transport);
        Assert.AreEqual(Switch2Transport.BluetoothLe,
            bleState.Switch2RawInputStatus.Transport);
        Assert.AreEqual(usbProfile.ProtocolRevision,
            usbState.Switch2RawInputStatus.ProtocolRevision);
        Assert.AreEqual(bleProfile.ProtocolRevision,
            bleState.Switch2RawInputStatus.ProtocolRevision);
    }

    [TestMethod]
    public void CommonMotionIsRetainedAndUsesPinnedProAxesAndScale()
    {
        Switch2CanonicalInputFrame canonical = CreateUsbMotionFrame(
            new Switch2Vector3Raw(4096, -2048, 1024),
            new Switch2Vector3Raw(16384, 8192, -16384),
            new Switch2Vector3Raw(11, -22, 33), motionTimestamp: 0x10203040);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(canonical,
            out var mapped, out var failure), failure.ToString());
        Assert.IsTrue(mapped.HasCommonMotion);
        Assert.AreEqual(0x10203040u, mapped.MotionTimestamp);
        Assert.AreEqual(new Switch2Vector3Raw(4096, -2048, 1024),
            mapped.Accelerometer);
        Assert.AreEqual(new Switch2Vector3Raw(16384, 8192, -16384),
            mapped.Gyroscope);
        Assert.AreEqual(new Switch2Vector3Raw(11, -22, 33),
            mapped.Magnetometer);

        var projection = new Switch2ProMotionProjection();
        var state = new DS4State();
        Assert.IsTrue(projection.TryApply(mapped, state));
        Assert.AreEqual(14.285714f,
            Switch2ProMotionProjection.NativeGyroLsbPerDegreeSecond,
            0.000001f);
        Assert.AreEqual(-1146.88, state.Motion.angVelYaw, 0.01);
        Assert.AreEqual(1146.88, state.Motion.angVelPitch, 0.01);
        Assert.AreEqual(-573.44, state.Motion.angVelRoll, 0.01);
        Assert.AreEqual(1.0, state.Motion.accelXG, 0.0001);
        Assert.AreEqual(0.25, state.Motion.accelYG, 0.0001);
        Assert.AreEqual(-0.5, state.Motion.accelZG, 0.0001);
    }

    [TestMethod]
    public void ProProjectionAppliesProfileSoftDeadzoneBeforeSixAxis()
    {
        Switch2CanonicalInputFrame canonical = CreateUsbMotionFrame(
            new Switch2Vector3Raw(4096, -2048, 1024),
            new Switch2Vector3Raw(16384, 8192, -16384), default,
            motionTimestamp: 1);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(canonical,
            out var mapped, out var failure), failure.ToString());
        var baselineProjection = new Switch2ProMotionProjection();
        var deadzoneProjection = new Switch2ProMotionProjection();
        var baseline = new DS4State();
        var filtered = new DS4State();

        Assert.IsTrue(baselineProjection.TryApply(mapped, baseline));
        Assert.IsTrue(deadzoneProjection.TryApply(mapped, filtered,
            magnetometerYawAssistEnabled: false,
            virtualGyroSoftDeadzone: 100.0));

        Assert.IsTrue(Math.Abs(filtered.Motion.angVelYaw) <
            Math.Abs(baseline.Motion.angVelYaw));
        Assert.IsTrue(Math.Abs(filtered.Motion.angVelPitch) <
            Math.Abs(baseline.Motion.angVelPitch));
        Assert.AreEqual(baseline.Motion.angVelRoll,
            filtered.Motion.angVelRoll, 0.0001);
    }

    [TestMethod]
    public void ProProjectionAppliesHorizonMotionInSameSixAxisPath()
    {
        Switch2CanonicalInputFrame firstCanonical = CreateUsbMotionFrame(
            new Switch2Vector3Raw(0, 0, 4096),
            new Switch2Vector3Raw(160, 80, 320), default,
            motionTimestamp: 1, completionTimestampQpc: 1);
        Switch2CanonicalInputFrame secondCanonical = CreateUsbMotionFrame(
            new Switch2Vector3Raw(0, 0, 4096),
            new Switch2Vector3Raw(160, 80, 320), default,
            motionTimestamp: 2, completionTimestampQpc: 100_001);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(firstCanonical,
            out var first, out var firstFailure), firstFailure.ToString());
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(secondCanonical,
            out var second, out var secondFailure), secondFailure.ToString());
        var projection = new Switch2ProMotionProjection();
        var state = new DS4State();

        Assert.IsTrue(projection.TryApply(first, state,
            magnetometerYawAssistEnabled: false,
            virtualGyroSoftDeadzone: 0.0,
            horizonStabilizationEnabled: true));
        Assert.IsTrue(projection.TryApply(second, state,
            magnetometerYawAssistEnabled: false,
            virtualGyroSoftDeadzone: 0.0,
            horizonStabilizationEnabled: true));

        Assert.AreEqual(0.0, state.Motion.angVelRoll, 0.0001,
            "Horizon projection removes the local roll lane before SixAxis.");
        Assert.IsTrue(Math.Abs(state.Motion.angVelYaw) > 0.0);
        Assert.IsTrue(Math.Abs(state.Motion.angVelPitch) > 0.0);
    }

    [TestMethod]
    public void WarmProMotionProjectionAllocatesNothing()
    {
        Switch2CanonicalInputFrame canonical = CreateUsbMotionFrame(
            new Switch2Vector3Raw(1, 2, 3),
            new Switch2Vector3Raw(4, 5, 6), default, 7);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(canonical,
            out var mapped, out var failure), failure.ToString());
        var projection = new Switch2ProMotionProjection();
        var state = new DS4State();
        bool succeeded = true;
        for (int warmup = 0; warmup < 2_000; warmup++)
        {
            succeeded &= projection.TryApply(mapped, state);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            succeeded &= projection.TryApply(mapped, state);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void ProProjectionLearnsStationaryBiasFromPhysicalQpcTime()
    {
        var projection = new Switch2ProMotionProjection();
        var state = new DS4State();
        for (int index = 0; index <= 501; index++)
        {
            Switch2CanonicalInputFrame canonical = CreateUsbMotionFrame(
                new Switch2Vector3Raw(0, 4096, 0),
                new Switch2Vector3Raw(10, -5, 2), default,
                motionTimestamp: (uint)index,
                completionTimestampQpc: index * 100_000L);
            Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(canonical,
                out var mapped, out var failure), failure.ToString());
            Assert.IsTrue(projection.TryApply(mapped, state));
        }

        Assert.IsTrue(projection.HasCalibratedGyroBias);
        Assert.AreEqual(0.0, state.Motion.angVelYaw, 0.01);
        Assert.AreEqual(0.0, state.Motion.angVelPitch, 0.01);
        Assert.AreEqual(0.0, state.Motion.angVelRoll, 0.01);

        projection.RestartGyroCalibration();
        Assert.IsTrue(projection.HasCalibratedGyroBias,
            "Manual recalibration must retain the last safe bias.");
    }

    [TestMethod]
    public void BleProfileRequiresExactReadNotifyGattProperties()
    {
        Switch2CanonicalInputFrame extraProperty = CreateBleProFrame(
            properties: Switch2GattProperty.Read |
                Switch2GattProperty.Notify | Switch2GattProperty.Write);

        Assert.IsFalse(Switch2ProProfileInputMapper.TryMap(extraProperty,
            out _, out var failure));
        Assert.AreEqual(Switch2ProProfileInputFailure.UnsupportedIdentity,
            failure);
    }

    [TestMethod]
    public void ExactIdentityGateAndUsbCounterDiagnosticsRemainDistinct()
    {
        Assert.IsFalse(Switch2ProProfileInputMapper.TryMap(default,
            out _, out var invalid));
        Assert.AreEqual(Switch2ProProfileInputFailure.InvalidCanonicalFrame,
            invalid);

        Switch2CanonicalInputFrame joyCon = CreateBleJoyConFrame();
        Assert.IsFalse(Switch2ProProfileInputMapper.TryMap(joyCon,
            out _, out var transport));
        Assert.AreEqual(Switch2ProProfileInputFailure.UnsupportedIdentity,
            transport);

        Switch2InputSessionDescriptor descriptor = CreateUsbDescriptor(1, 1,
            10_000_000);
        var session = new Switch2InputSession(descriptor,
            CreateCalibration(1, null));
        Assert.IsTrue(session.TryProcess(descriptor,
            BuildUsbPacket(100, 0, 0x800, 0x800, 0x800, 0x800), 1,
            out _, out _));
        Assert.IsTrue(session.TryProcess(descriptor,
            BuildUsbPacket(90, 0, 0x800, 0x800, 0x800, 0x800), 2,
            out var backward, out _));
        Assert.AreEqual(Switch2CounterSequenceKind.BackwardOrOutOfOrder,
            backward.CounterSequence);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(backward,
            out _, out var sequence));
        Assert.AreEqual(Switch2ProProfileInputFailure.None,
            sequence);
    }

    [TestMethod]
    public void ProfileProjectionAndCompatibilityWriteAllocateNothing()
    {
        Switch2CanonicalInputFrame canonical = CreateUsbFrame(
            buttons: (uint)(Switch2ProButton.FaceSouth |
                Switch2ProButton.RightTrigger | Switch2ProButton.C),
            leftX: 0x111, leftY: 0x222, rightX: 0xDDD, rightY: 0xEEE);
        var state = new DS4State();
        bool succeeded = true;
        for (int warmup = 0; warmup < 2000; warmup++)
        {
            succeeded &= Switch2ProProfileInputMapper.TryMap(canonical,
                out var mapped, out _);
            succeeded &= mapped.TryWriteLegacyState(state);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            succeeded &= Switch2ProProfileInputMapper.TryMap(canonical,
                out var mapped, out _);
            succeeded &= mapped.TryWriteLegacyState(state);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    private static void AssertAxis(Switch2ProfileAxis axis, ushort raw,
        short signed, byte legacy)
    {
        Assert.AreEqual(raw, axis.RawValue);
        Assert.AreEqual(signed, axis.SignedValue);
        Assert.AreEqual(legacy, axis.LegacyValue);
    }

    private static string[] ReadActiveLegacyControls(DS4State state)
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
        return active.ToArray();

        void Add(bool pressed, string name)
        {
            if (pressed)
            {
                active.Add(name);
            }
        }
    }

    private static Switch2CanonicalInputFrame CreateUsbFrame(uint counter = 1,
        uint buttons = 0, ushort leftX = 0x800, ushort leftY = 0x800,
        ushort rightX = 0x800, ushort rightY = 0x800,
        byte[] calibrationRecord = null, ulong deviceGeneration = 1,
        ulong transportGeneration = 1, long timestamp = 1,
        long qpcFrequency = 10_000_000)
    {
        Switch2InputSessionDescriptor descriptor = CreateUsbDescriptor(
            deviceGeneration, transportGeneration, qpcFrequency);
        var session = new Switch2InputSession(descriptor,
            CreateCalibration(deviceGeneration, calibrationRecord));
        Assert.IsTrue(session.TryProcess(descriptor,
            BuildUsbPacket(counter, buttons, leftX, leftY, rightX, rightY),
            timestamp, out var frame, out var failure), failure.ToString());
        return frame;
    }

    private static Switch2CanonicalInputFrame CreateUsbMotionFrame(
        Switch2Vector3Raw accelerometer, Switch2Vector3Raw gyroscope,
        Switch2Vector3Raw magnetometer, uint motionTimestamp,
        long completionTimestampQpc = 1)
    {
        Switch2InputSessionDescriptor descriptor = CreateUsbDescriptor(1, 1,
            10_000_000);
        var session = new Switch2InputSession(descriptor,
            CreateCalibration(1, null));
        byte[] packet = BuildUsbPacket(1, 0, 0x800, 0x800, 0x800, 0x800);
        WriteVector(packet, 1 + 0x19, magnetometer);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1 + 0x2A, 4),
            motionTimestamp);
        WriteVector(packet, 1 + 0x30, accelerometer);
        WriteVector(packet, 1 + 0x36, gyroscope);
        Assert.IsTrue(session.TryProcess(descriptor, packet,
            completionTimestampQpc,
            out var frame, out var failure), failure.ToString());
        return frame;
    }

    private static Switch2CanonicalInputFrame CreateBleProFrame(
        uint counter = 1, uint buttons = 0, ushort leftX = 0x800,
        ushort leftY = 0x800, ushort rightX = 0x800,
        ushort rightY = 0x800, long timestamp = 1,
        Switch2GattProperty properties = Switch2GattProperty.Read |
            Switch2GattProperty.Notify)
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid,
            properties,
            Switch2ControllerModel.ProController2, out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity, 1, 1,
            10_000_000, out var descriptor));
        var session = new Switch2InputSession(descriptor,
            CreateCalibration(1, null));
        byte[] packet = BuildUsbPacket(counter, buttons, leftX, leftY,
            rightX, rightY);
        Assert.IsTrue(session.TryProcess(descriptor, packet.AsSpan(1),
            timestamp,
            out var frame, out var failure), failure.ToString());
        return frame;
    }

    private static Switch2CanonicalInputFrame CreateBleJoyConFrame()
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid,
            Switch2GattProperty.Read | Switch2GattProperty.Notify,
            Switch2ControllerModel.JoyCon2Left, out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity, 1, 1,
            10_000_000, out var descriptor));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Left, 1, out var calibration));
        var session = new Switch2InputSession(descriptor, calibration);
        byte[] packet = BuildUsbPacket(1, 0, 0x800, 0x800, 0x800, 0x800);
        Assert.IsTrue(session.TryProcess(descriptor, packet.AsSpan(1), 1,
            out var frame, out var failure), failure.ToString());
        return frame;
    }

    private static Switch2InputSessionDescriptor CreateUsbDescriptor(
        ulong deviceGeneration, ulong transportGeneration, long qpcFrequency)
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateProController2Usb(
            Switch2InputProtocolIdentity.NintendoUsbVendorId,
            Switch2InputProtocolIdentity.ProController2UsbProductId,
            Switch2InputProtocolIdentity.AuditedProController2UsbBcdDevice,
            out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity,
            deviceGeneration, transportGeneration, qpcFrequency,
            out var descriptor));
        return descriptor;
    }

    private static Switch2InputCalibrationSnapshot CreateCalibration(
        ulong deviceGeneration, byte[] record)
    {
        bool created = record == null ?
            Switch2InputCalibrationSnapshot.TryCreateFallback(
                Switch2ControllerModel.ProController2, deviceGeneration,
                out var fallback) :
            Switch2InputCalibrationSnapshot.TryCreate(
                Switch2ControllerModel.ProController2, deviceGeneration,
                record, record, out fallback);
        Assert.IsTrue(created);
        return fallback;
    }

    private static byte[] BuildUsbPacket(uint counter, uint buttons,
        ushort leftX, ushort leftY, ushort rightX, ushort rightY)
    {
        var packet = new byte[Switch2InputCodec.UsbPacketLength];
        packet[0] = (byte)Switch2InputReportKind.Common05;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), counter);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1 + 0x04, 4),
            buttons);
        PackStick(packet, 1 + 0x0A, leftX, leftY);
        PackStick(packet, 1 + 0x0D, rightX, rightY);
        return packet;
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

    private static void PackStick(byte[] destination, int offset,
        ushort x, ushort y)
    {
        Assert.IsTrue(x <= 0x0FFF && y <= 0x0FFF);
        destination[offset] = (byte)x;
        destination[offset + 1] = (byte)(((x >> 8) & 0x0F) |
            ((y & 0x0F) << 4));
        destination[offset + 2] = (byte)(y >> 4);
    }

    private static void WriteVector(byte[] destination, int offset,
        Switch2Vector3Raw value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(destination.AsSpan(offset, 2),
            value.X);
        BinaryPrimitives.WriteInt16LittleEndian(
            destination.AsSpan(offset + 2, 2), value.Y);
        BinaryPrimitives.WriteInt16LittleEndian(
            destination.AsSpan(offset + 4, 2), value.Z);
    }
}
