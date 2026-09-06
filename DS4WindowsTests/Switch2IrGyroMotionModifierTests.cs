using System.Buffers.Binary;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2IrGyroMotionModifierTests
{
    private const long Frequency = 10_000_000;
    private const Switch2GattProperty InputProperties =
        Switch2GattProperty.Read | Switch2GattProperty.Notify;

    [TestMethod]
    public void SerializedTriggerScanIsExactAndAllocationFree()
    {
        Assert.IsTrue(Switch2IrGyroMotionModifier.ContainsSerializedTrigger(
            "1, 27,28", 27));
        Assert.IsTrue(Switch2IrGyroMotionModifier.ContainsSerializedTrigger(
            "1, 27,28", 28));
        Assert.IsFalse(Switch2IrGyroMotionModifier.ContainsSerializedTrigger(
            "1, 127,280", 27));
        Assert.IsFalse(Switch2IrGyroMotionModifier.ContainsSerializedTrigger(
            "", 27));

        for (int index = 0; index < 32; index++)
        {
            _ = Switch2IrGyroMotionModifier.ContainsSerializedTrigger(
                "1, 27,28", 27);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allFound = true;
        for (int index = 0; index < 1_000; index++)
        {
            allFound &= Switch2IrGyroMotionModifier.
                ContainsSerializedTrigger("1, 27,28", 27);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allFound);
        Assert.AreEqual(0, allocated);
    }

    [TestMethod]
    public void PressAndReleaseEdgesFreezeThenRetainDeadzoneAndDampening()
    {
        Switch2IrGyroMotionModifierState state = default;
        Switch2IrGyroConfiguration configuration = Configuration(
            leftEnabled: false, rightEnabled: true,
            leftTuning: Switch2IrGyroTuning.Default,
            rightTuning: new Switch2IrGyroTuning(
                Switch2JoyConProfileButton.FaceSouth, 15.0,
                pauseAfterPressedMilliseconds: 100,
                pauseAfterReleasedMilliseconds: 100,
                deadzoneEffectAfterReleasedMilliseconds: 200,
                Switch2JoyConProfileButton.FaceSouth, 90.0,
                dampeningEffectAfterReleasedMilliseconds: 200));

        AssertResult(Frame(0, 0, 0, rightIr: true), configuration,
            ref state, freeze: false, deadzone: false, dampening: false);
        Switch2IrGyroMotionModifierResult pressed = AssertResult(
            Frame(10, 0, 1u << 2, rightIr: true), configuration,
            ref state, freeze: true, deadzone: true, dampening: true);
        Assert.AreEqual(Switch2JoyConSide.Right, pressed.SourceSide);
        Assert.AreEqual(15.0, pressed.DeadzoneAmount);
        Assert.AreEqual(0.1, pressed.DampeningMultiplier, 0.000001);

        AssertResult(Frame(109, 0, 1u << 2, rightIr: true), configuration,
            ref state, freeze: true, deadzone: true, dampening: true);
        AssertResult(Frame(111, 0, 1u << 2, rightIr: true), configuration,
            ref state, freeze: false, deadzone: true, dampening: true);
        AssertResult(Frame(130, 0, 0, rightIr: true), configuration,
            ref state, freeze: true, deadzone: true, dampening: true);
        AssertResult(Frame(231, 0, 0, rightIr: true), configuration,
            ref state, freeze: false, deadzone: true, dampening: true);
        AssertResult(Frame(331, 0, 0, rightIr: true), configuration,
            ref state, freeze: false, deadzone: false, dampening: false);
    }

    [TestMethod]
    public void NewlyActiveSensorChoosesItsOwnTuningAcrossJoinedPair()
    {
        Switch2IrGyroMotionModifierState state = default;
        Switch2IrGyroConfiguration configuration = Configuration(true, true,
            new Switch2IrGyroTuning(
                Switch2JoyConProfileButton.LeftTrigger, 10.0, 0, 0, 0,
                Switch2JoyConProfileButton.None, 90.0, 0),
            new Switch2IrGyroTuning(
                Switch2JoyConProfileButton.LeftTrigger, 30.0, 0, 0, 0,
                Switch2JoyConProfileButton.None, 90.0, 0));

        Switch2IrGyroMotionModifierResult right = AssertResult(
            Frame(0, 0, 0, rightIr: true), configuration, ref state,
            freeze: false, deadzone: false, dampening: false);
        Assert.AreEqual(Switch2JoyConSide.Right, right.SourceSide);

        Switch2IrGyroMotionModifierResult left = AssertResult(
            Frame(10, 1u << 23, 0, leftIr: true, rightIr: true),
            configuration, ref state, freeze: false, deadzone: true,
            dampening: false);
        Assert.AreEqual(Switch2JoyConSide.Left, left.SourceSide);
        Assert.AreEqual(10.0, left.DeadzoneAmount);
    }

    [TestMethod]
    public void ReentryProfileChangeAndLifecycleChangeRearmWithoutFreeze()
    {
        Switch2IrGyroMotionModifierState state = default;
        Switch2IrGyroTuning tuning = new(
            Switch2JoyConProfileButton.FaceSouth, 15.0, 100, 100, 200,
            Switch2JoyConProfileButton.None, 90.0, 200);
        Switch2IrGyroConfiguration configuration = Configuration(false, true,
            Switch2IrGyroTuning.Default, tuning, profileRevision: 1);

        AssertResult(Frame(0, 0, 1u << 2, rightIr: true), configuration,
            ref state, false, true, false);
        Assert.IsTrue(Switch2IrGyroMotionModifier.TryAdvance(
            Frame(10, 0, 1u << 2, rightIr: false), configuration,
            ref state, out var inactive));
        Assert.IsFalse(inactive.Active);
        AssertResult(Frame(20, 0, 1u << 2, rightIr: true), configuration,
            ref state, false, true, false);

        Switch2IrGyroConfiguration switched = Configuration(false, true,
            Switch2IrGyroTuning.Default, tuning, profileRevision: 2);
        AssertResult(Frame(30, 0, 1u << 2, rightIr: true), switched,
            ref state, false, true, false);

        AssertResult(Frame(40, 0, 1u << 2, rightIr: true,
                deviceGeneration: 2, transportGeneration: 2), switched,
            ref state, false, true, false);
    }

    [TestMethod]
    public void StableReportPathDoesNotAllocate()
    {
        Switch2IrGyroMotionModifierState state = default;
        Switch2JoyConProfileInputFrame frame = Frame(100, 0, 0,
            rightIr: true);
        Switch2IrGyroConfiguration configuration = Configuration(false, true,
            Switch2IrGyroTuning.Default, Switch2IrGyroTuning.Default);
        for (int index = 0; index < 32; index++)
        {
            Assert.IsTrue(Switch2IrGyroMotionModifier.TryAdvance(frame,
                configuration, ref state, out _));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int index = 0; index < 1_000; index++)
        {
            succeeded &= Switch2IrGyroMotionModifier.TryAdvance(frame,
                configuration, ref state, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(succeeded);
        Assert.AreEqual(0, allocated);
    }

    private static Switch2IrGyroMotionModifierResult AssertResult(
        in Switch2JoyConProfileInputFrame frame,
        in Switch2IrGyroConfiguration configuration,
        ref Switch2IrGyroMotionModifierState state, bool freeze,
        bool deadzone, bool dampening)
    {
        Assert.IsTrue(Switch2IrGyroMotionModifier.TryAdvance(frame,
            configuration, ref state, out var result));
        Assert.IsTrue(result.Active);
        Assert.AreEqual(freeze, result.Freeze);
        Assert.AreEqual(deadzone, result.DeadzoneActive);
        Assert.AreEqual(dampening, result.DampeningActive);
        return result;
    }

    private static Switch2IrGyroConfiguration Configuration(bool leftEnabled,
        bool rightEnabled, in Switch2IrGyroTuning leftTuning,
        in Switch2IrGyroTuning rightTuning, long profileRevision = 1)
    {
        var left = new Switch2IrGyroSideConfiguration(leftEnabled,
            Switch2IrActivationThreshold.Strict, leftTuning);
        var right = new Switch2IrGyroSideConfiguration(rightEnabled,
            Switch2IrActivationThreshold.Strict, rightTuning);
        return new Switch2IrGyroConfiguration(left, right, profileRevision);
    }

    private static Switch2JoyConProfileInputFrame Frame(int milliseconds,
        uint leftButtons, uint rightButtons, bool leftIr = false,
        bool rightIr = false, ulong deviceGeneration = 1,
        ulong transportGeneration = 1)
    {
        Switch2InputSessionDescriptor leftDescriptor = Descriptor(
            Switch2ControllerModel.JoyCon2Left, deviceGeneration,
            transportGeneration);
        Switch2InputSessionDescriptor rightDescriptor = Descriptor(
            Switch2ControllerModel.JoyCon2Right, deviceGeneration,
            transportGeneration);
        long timestamp = milliseconds * (Frequency / 1_000);
        Switch2CanonicalInputFrame left = Canonical(leftDescriptor,
            leftButtons, timestamp, leftIr);
        Switch2CanonicalInputFrame right = Canonical(rightDescriptor,
            rightButtons, timestamp, rightIr);
        var snapshot = new Switch2JoyConPairSnapshot(1, left, right, 0);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateJoined(1,
            leftDescriptor, rightDescriptor, out var mapper));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapJoined(mapper,
            snapshot, out _, out var mapped, out var failure),
            failure.ToString());
        return mapped;
    }

    private static Switch2CanonicalInputFrame Canonical(
        in Switch2InputSessionDescriptor descriptor, uint buttons,
        long timestamp, bool irActive)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            descriptor.Identity.Model, descriptor.DeviceGeneration,
            out var calibration));
        var session = new Switch2InputSession(descriptor, calibration);
        byte[] body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), buttons);
        PackStick(body, 0x0A, 0x800, 0x800);
        PackStick(body, 0x0D, 0x800, 0x800);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x14, 2),
            irActive ? (ushort)100 : (ushort)0);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x16, 2),
            irActive ? (ushort)500 : (ushort)0);
        Assert.IsTrue(session.TryProcess(descriptor, body, timestamp,
            out var frame, out var failure), failure.ToString());
        return frame;
    }

    private static Switch2InputSessionDescriptor Descriptor(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration)
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid, InputProperties,
            model, out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity,
            deviceGeneration, transportGeneration, Frequency,
            out var descriptor));
        return descriptor;
    }

    private static void PackStick(byte[] destination, int offset, ushort x,
        ushort y)
    {
        destination[offset] = (byte)x;
        destination[offset + 1] = (byte)(((x >> 8) & 0x0F) |
            ((y & 0x0F) << 4));
        destination[offset + 2] = (byte)(y >> 4);
    }
}
