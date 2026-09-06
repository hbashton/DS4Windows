using System.Reflection;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class DS4StateOwnedSnapshotTests
{
    private static readonly FieldInfo[] MotionScalarFields = typeof(SixAxis)
        .GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Where(field => field.FieldType.IsValueType).ToArray();
    private static readonly FieldInfo[] StateValueFields = typeof(DS4State)
        .GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Where(field => field.FieldType.IsValueType).ToArray();

    [TestMethod]
    public void EveryPublicStateAndMotionScalarIsCopiedExactly()
    {
        var snapshot = new DS4StateOwnedSnapshot();
        for (int polarity = 0; polarity < 2; polarity++)
        {
            var source = new DS4State();
            PopulateFields(source, StateValueFields, 11 + polarity);
            PopulateFields(source.Motion, MotionScalarFields, 101 + polarity);
            source.Motion.previousAxis = Motion(301 + polarity);
            snapshot.Capture(source);

            AssertFields(source, snapshot.State, StateValueFields);
            AssertMotion(source.Motion, snapshot.State.Motion);
            AssertMotion(source.Motion.previousAxis, snapshot.State.Motion.previousAxis);
            Assert.AreNotSame(source, snapshot.State);
            Assert.AreNotSame(source.Motion, snapshot.State.Motion);
            Assert.AreNotSame(source.Motion.previousAxis, snapshot.State.Motion.previousAxis);
            Assert.IsNull(snapshot.State.Motion.previousAxis.previousAxis);
            Assert.AreNotEqual(source.Motion.accelX, source.Motion.outputAccelX,
                "The oracle must distinguish source acceleration from mapped acceleration.");
        }
    }

    [TestMethod]
    public void NewReferencePayloadRequiresAnExplicitOwnershipDecision()
    {
        CollectionAssert.AreEquivalent(new[] { "Motion" }, typeof(DS4State)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(field => !field.FieldType.IsValueType).Select(field => field.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "previousAxis" }, typeof(SixAxis)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(field => !field.FieldType.IsValueType).Select(field => field.Name).ToArray());
    }

    [TestMethod]
    public void TypedAxesAndPhysicalSidecarsSurviveWithoutByteExpansion()
    {
        var source = new DS4State
        {
            LXAxis = DS4MappedStickAxis.FromSigned(16),
            LYAxis = DS4MappedStickAxis.FromSigned(-33),
            RXAxis = DS4MappedStickAxis.FromSigned(65),
            RYAxis = DS4MappedStickAxis.FromSigned(-127),
            Cross = true, L2 = 120, L2Raw = 211, L2Btn = true,
            SideL = true, FnR = true,
            Switch2RawInputStatus = new() { IsValid = true,
                ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
                DeviceGeneration = 991, TransportGeneration = 992,
                RawButtonBits = 0x12345678, LeftStickXRaw = 2049,
                LeftStickX = 16, CButton = true },
            Switch2JoyConRawInputStatus = new() { IsValid = true,
                ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
                PairEpoch = 701, LeftPresent = true, RightPresent = true,
                LeftDeviceGeneration = 711, RightDeviceGeneration = 721,
                LeftRailSL = true, RightRailSR = true,
                LeftGyroscope = new Switch2Vector3Raw(101, -202, 303) },
            DualSenseRawInputStatus = new() { IsValid = true, SensorTimestamp = 0xFEDCBA98,
                TriggerEffectModes = 0xA5, BatteryStatus = 0x12 },
            TrackPadTouch0 = new() { IsActive = true, X = 123, Y = 456, RawTrackingNum = 7 },
        };
        var snapshot = new DS4StateOwnedSnapshot();
        snapshot.Capture(source);

        Assert.AreEqual(source.LXAxis, snapshot.State.LXAxis);
        Assert.AreEqual(source.LYAxis, snapshot.State.LYAxis);
        Assert.AreEqual(source.RXAxis, snapshot.State.RXAxis);
        Assert.AreEqual(source.RYAxis, snapshot.State.RYAxis);
        Assert.IsTrue(snapshot.State.LXAxis.IsHighResolution);
        Assert.AreEqual((byte)128, snapshot.State.LX);
        AssertFields(source, snapshot.State, StateValueFields);
        source.LX = 255;
        source.Switch2RawInputStatus = default;
        source.Switch2JoyConRawInputStatus = default;
        source.Cross = false;
        Assert.AreEqual(DS4MappedStickAxis.FromSigned(16), snapshot.State.LXAxis);
        Assert.IsTrue(snapshot.State.Switch2RawInputStatus.CButton);
        Assert.IsTrue(snapshot.State.Switch2JoyConRawInputStatus.LeftRailSL);
        Assert.IsTrue(snapshot.State.Cross);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void PreviousMotionIsBoundedForSelfCyclesTwoNodeCyclesAndLongChains(int topology)
    {
        SixAxis first = Motion(10), second = Motion(100), third = Motion(200);
        first.previousAxis = topology == 0 ? first : second;
        second.previousAxis = topology == 1 ? first : third;
        third.previousAxis = second;
        var source = new DS4State { Motion = first };
        var expectedPrevious = topology == 0 ? first : second;
        var snapshot = new DS4StateOwnedSnapshot();

        snapshot.Capture(source);

        AssertMotion(first, snapshot.State.Motion);
        AssertMotion(expectedPrevious, snapshot.State.Motion.previousAxis);
        Assert.AreNotSame(snapshot.State.Motion, snapshot.State.Motion.previousAxis);
        Assert.AreNotSame(first, snapshot.State.Motion);
        Assert.AreNotSame(expectedPrevious, snapshot.State.Motion.previousAxis);
        Assert.IsNull(snapshot.State.Motion.previousAxis.previousAxis);
    }

    [TestMethod]
    public void CaptureOfOwnedStateRetainsExactCurrentAndPreviousValues()
    {
        var source = new DS4State { Motion = Motion(10), R2 = 239 };
        source.Motion.previousAxis = Motion(100);
        var snapshot = new DS4StateOwnedSnapshot();
        snapshot.Capture(source);
        SixAxis currentStorage = snapshot.State.Motion;
        SixAxis previousStorage = currentStorage.previousAxis;

        snapshot.Capture(snapshot.State);

        Assert.AreSame(currentStorage, snapshot.State.Motion);
        Assert.AreSame(previousStorage, snapshot.State.Motion.previousAxis);
        AssertMotion(source.Motion, currentStorage);
        AssertMotion(source.Motion.previousAxis, previousStorage);
        Assert.AreEqual((byte)239, snapshot.State.R2);
    }

    [TestMethod]
    public void CrossAliasedOwnedSlotsAreCapturedBeforeEitherSlotIsOverwritten()
    {
        SixAxis expectedCurrent = Motion(10), expectedPrevious = Motion(100);
        var source = new DS4State { Motion = expectedCurrent };
        source.Motion.previousAxis = expectedPrevious;
        var snapshot = new DS4StateOwnedSnapshot();
        snapshot.Capture(source);
        SixAxis currentStorage = snapshot.State.Motion;
        SixAxis previousStorage = currentStorage.previousAxis;
        previousStorage.previousAxis = currentStorage;
        snapshot.State.Motion = previousStorage;

        snapshot.Capture(snapshot.State);

        Assert.AreSame(currentStorage, snapshot.State.Motion);
        AssertMotion(expectedPrevious, snapshot.State.Motion);
        AssertMotion(expectedCurrent, snapshot.State.Motion.previousAxis);
        Assert.IsNull(snapshot.State.Motion.previousAxis.previousAxis);
    }

    [TestMethod]
    public void ExternalSourceCanPointIntoOwnedStorageWithoutAliasingTheResult()
    {
        var snapshot = new DS4StateOwnedSnapshot();
        var initial = new DS4State { Motion = Motion(300) };
        snapshot.Capture(initial);
        var source = new DS4State { Motion = Motion(10) };
        source.Motion.previousAxis = snapshot.State.Motion;

        snapshot.Capture(source);

        AssertMotion(source.Motion, snapshot.State.Motion);
        AssertMotion(initial.Motion, snapshot.State.Motion.previousAxis);
        Assert.AreNotSame(source.Motion, snapshot.State.Motion);
        Assert.IsNull(snapshot.State.Motion.previousAxis.previousAxis);
    }

    [TestMethod]
    public void NullMotionAndReuseRetirePreviousHistoryAndNeverAllocateReplacementSlots()
    {
        var snapshot = new DS4StateOwnedSnapshot();
        var source = new DS4State { Motion = Motion(100), Cross = true };
        source.Motion.previousAxis = Motion(200);
        snapshot.Capture(source);
        SixAxis currentStorage = snapshot.State.Motion;
        SixAxis previousStorage = currentStorage.previousAxis;

        source.Motion = null;
        source.Cross = false;
        snapshot.Capture(source);
        Assert.IsNull(snapshot.State.Motion);
        Assert.IsFalse(snapshot.State.Cross);
        Assert.IsNull(currentStorage.previousAxis);
        Assert.IsNull(previousStorage.previousAxis);

        source.Motion = Motion(400);
        snapshot.Capture(source);
        Assert.AreSame(currentStorage, snapshot.State.Motion);
        Assert.IsNull(snapshot.State.Motion.previousAxis);
        AssertMotion(source.Motion, snapshot.State.Motion);
        source.Motion.previousAxis = Motion(500);
        snapshot.Capture(source);
        Assert.AreSame(previousStorage, snapshot.State.Motion.previousAxis);
        AssertMotion(source.Motion.previousAxis, previousStorage);
    }

    [TestMethod]
    public void SourceAndConsumerMutationsDoNotCrossSnapshotOwnership()
    {
        var source = new DS4State { Motion = Motion(10), Cross = true };
        source.Motion.previousAxis = Motion(100);
        var snapshot = new DS4StateOwnedSnapshot();
        snapshot.Capture(source);
        int capturedPitch = snapshot.State.Motion.gyroPitchFull;
        int capturedPrevious = snapshot.State.Motion.previousAxis.gyroRollFull;
        source.Motion.gyroPitchFull = int.MinValue;
        source.Motion.previousAxis.gyroRollFull = int.MaxValue;
        source.Cross = false;
        Assert.AreEqual(capturedPitch, snapshot.State.Motion.gyroPitchFull);
        Assert.AreEqual(capturedPrevious, snapshot.State.Motion.previousAxis.gyroRollFull);
        Assert.IsTrue(snapshot.State.Cross);
        snapshot.State.Motion.outputAccelX = 987;
        snapshot.State.Motion.previousAxis.accelXG = 654;
        Assert.AreNotEqual(987, source.Motion.outputAccelX);
        Assert.AreNotEqual(654.0, source.Motion.previousAxis.accelXG);
    }

    [TestMethod]
    public void NullSourceRejectsWithoutChangingTheLastSnapshot()
    {
        var snapshot = new DS4StateOwnedSnapshot();
        var source = new DS4State { Motion = Motion(10), Cross = true };
        snapshot.Capture(source);
        Assert.ThrowsException<ArgumentNullException>(() => snapshot.Capture(null));
        Assert.IsTrue(snapshot.State.Cross);
        AssertMotion(source.Motion, snapshot.State.Motion);
    }

    [TestMethod]
    public void WarmCaptureIncludingNullSelfAndCyclicGraphsAllocatesNothing()
    {
        var snapshot = new DS4StateOwnedSnapshot();
        var source = new DS4State { Motion = Motion(10) };
        source.Motion.previousAxis = source.Motion;
        var noMotion = new DS4State { Motion = null };
        for (int i = 0; i < 2_000; i++)
        {
            snapshot.Capture(source);
            snapshot.Capture(snapshot.State);
            snapshot.Capture(noMotion);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            snapshot.Capture(source);
            snapshot.Capture(snapshot.State);
            snapshot.Capture(noMotion);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
    }

    private static SixAxis Motion(int seed)
    {
        var result = new SixAxis(0, 0, 0, 0, 0, 0, 0);
        PopulateFields(result, MotionScalarFields, seed);
        return result;
    }

    private static void AssertMotion(SixAxis expected, SixAxis actual)
    {
        Assert.IsNotNull(actual);
        AssertFields(expected, actual, MotionScalarFields);
    }

    private static void AssertFields(object expected, object actual, FieldInfo[] fields)
    {
        foreach (FieldInfo field in fields)
            Assert.AreEqual(field.GetValue(expected), field.GetValue(actual), field.Name);
    }

    private static void PopulateFields(object target, FieldInfo[] fields, int seed)
    {
        foreach (FieldInfo field in fields)
            field.SetValue(target, Value(field.FieldType, ++seed));
    }

    // Cold reflection oracle covers additions to the public value-state
    // contract. No reflection is used by Capture or the measured loop.
    private static object Value(Type type, int seed)
    {
        if (type.IsEnum) return Enum.ToObject(type, seed);
        if (type == typeof(bool)) return seed % 2 != 0;
        if (type == typeof(byte)) return (byte)(seed % 251 + 1);
        if (type == typeof(sbyte)) return (sbyte)(seed % 125 + 1);
        if (type == typeof(short)) return (short)(-seed - 1);
        if (type == typeof(ushort)) return (ushort)(seed + 1);
        if (type == typeof(int)) return -seed - 1000;
        if (type == typeof(uint)) return (uint)(seed + 1000);
        if (type == typeof(long)) return -(long)seed - 10_000;
        if (type == typeof(ulong)) return (ulong)seed + 10_000;
        if (type == typeof(float)) return seed + 0.375f;
        if (type == typeof(double)) return -seed - 0.625;
        if (type == typeof(DateTime)) return DateTime.UnixEpoch.AddTicks(seed + 1);
        if (type == typeof(Switch2Vector3Raw)) return new Switch2Vector3Raw(
            (short)(seed + 1), (short)(-seed - 2), (short)(seed + 3));
        object value = Activator.CreateInstance(type);
        FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        if (fields.Length == 0)
            throw new AssertFailedException($"Add an exact scalar oracle for {type}.");
        foreach (FieldInfo field in fields)
            field.SetValue(value, Value(field.FieldType, ++seed));
        return value;
    }
}
