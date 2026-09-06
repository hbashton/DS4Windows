using System.Reflection;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class PostMapStickConcurrentPublicationTests
{
    private const GyroMouseStickInfo.OutputStick Left = GyroMouseStickInfo.OutputStick.LeftStick;
    private const GyroMouseStickInfo.OutputStick Right = GyroMouseStickInfo.OutputStick.RightStick;

    private delegate void GyroProducer(SixAxisEventArgs args,
        in Switch2GyroTriggerModifierResult modifier);
    private delegate void CapturedGyroProducer(SixAxisEventArgs args,
        in Switch2GyroTriggerModifierResult modifier,
        Mapping.PostMapStickData accumulator, long epoch);

    [TestMethod]
    public void ResetThenSuccessorCannotRelabelLatePredecessorPublicationOrNeutral()
    {
        var data = new Mapping.PostMapStickData();
        using var oldCaptured = new ManualResetEventSlim();
        using var successorPublished = new ManualResetEventSlim();
        bool admittedOld = true, admittedNeutral = true;
        var old = Task.Run(() =>
        {
            long epoch = data.CaptureEpoch();
            oldCaptured.Set();
            if (!successorPublished.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Successor did not publish.");
            admittedOld = data.TrySubmit(epoch, Right, true, true, 255, 0, true);
            admittedNeutral = data.TryClearGyro(epoch);
        });
        try
        {
            Assert.IsTrue(oldCaptured.Wait(TimeSpan.FromSeconds(5)));
            data.RequestReset();
            Assert.IsTrue(data.TrySubmit(data.CaptureEpoch(), Right, true, true, 150, 100, true));
        }
        finally { successorPublished.Set(); }
        Assert.IsTrue(old.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(admittedOld);
        Assert.IsFalse(admittedNeutral);
        var state = new DS4State();
        data.ApplyTo(state);
        Assert.AreEqual((byte)150, state.RX);
        Assert.AreEqual((byte)100, state.RY);
        state = new DS4State();
        Assert.IsTrue(data.TryApplyCurrentGyro(data.CaptureEpoch(), state, Right, true, true));
        Assert.AreEqual((byte)150, state.RX);
        Assert.AreEqual((byte)100, state.RY);
    }

    [TestMethod]
    public void TwoProducersAtomicallyRetainStrongerIndependentAxes()
    {
        var data = new Mapping.PostMapStickData();
        using var phases = new Barrier(3);
        var gyro = Task.Run(() => Publish(240, 130, true));
        var touch = Task.Run(() => Publish(140, 20, false));
        for (int i = 0; i < 256; i++)
        {
            data.RequestReset();
            Phase();
            Phase();
            var state = new DS4State();
            data.ApplyTo(state);
            Assert.AreEqual((byte)240, state.LX);
            Assert.AreEqual((byte)20, state.LY);
            Assert.IsFalse(data.dirty);
        }
        Assert.IsTrue(Task.WaitAll(new[] { gyro, touch }, TimeSpan.FromSeconds(5)));

        void Publish(byte x, byte y, bool updateGyro)
        {
            for (int i = 0; i < 256; i++)
            {
                Phase();
                if (!data.TrySubmit(data.CaptureEpoch(), Left, true, true, x, y, updateGyro))
                    throw new InvalidOperationException("No reset occurs inside this publication phase.");
                Phase();
            }
        }
        void Phase()
        {
            if (!phases.SignalAndWait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Publication phase did not complete.");
        }
    }

    [TestMethod]
    public void ConcurrentConsumptionNeverSeesHalfOfAPublishedVector()
    {
        var data = new Mapping.PostMapStickData();
        using var start = new ManualResetEventSlim();
        var producer = Task.Run(() =>
        {
            if (!start.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            for (int i = 0; i < 10000; i++)
                data.TrySubmit(data.CaptureEpoch(), Right, true, true, 240, 16, false);
        });
        start.Set();
        for (int i = 0; i < 10000; i++)
        {
            var state = new DS4State();
            data.ApplyTo(state);
            Assert.IsTrue((state.RX == 128 && state.RY == 128) ||
                (state.RX == 240 && state.RY == 16));
        }
        Assert.IsTrue(producer.Wait(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void ImmediateGyroNeverRetainsThePhysicalWinnerOrAdmitsAnOldEpoch()
    {
        var data = new Mapping.PostMapStickData();
        long old = data.CaptureEpoch();
        Assert.IsTrue(data.TrySubmit(old, Right, true, true, 129, 127, true));
        data.Reset();
        var physical = new DS4State { RXAxis = Axis(200.125), RYAxis = Axis(30.25) };
        Assert.IsTrue(data.TryApplyCurrentGyro(old, physical, Right, true, true));
        Assert.AreEqual(200.125, physical.RXAxis.ProfileCoordinate);
        Assert.AreEqual(30.25, physical.RYAxis.ProfileCoordinate);
        var released = new DS4State();
        data.ApplyTo(released);
        Assert.AreEqual((byte)129, released.RX);
        Assert.AreEqual((byte)127, released.RY);

        data.RequestReset();
        Assert.IsTrue(data.TrySubmit(data.CaptureEpoch(), Right, true, true, 170, 80, true));
        var untouched = new DS4State();
        Assert.IsFalse(data.TryApplyCurrentGyro(old, untouched, Right, true, true));
        Assert.AreEqual((byte)128, untouched.RX);
        data.ApplyTo(untouched);
        Assert.AreEqual((byte)170, untouched.RX);
    }

    [TestMethod]
    public void DisabledAxesNeutralizeCurrentGyroWithoutErasingOtherPendingContributors()
    {
        var data = new Mapping.PostMapStickData();
        long epoch = data.CaptureEpoch();
        data.TrySubmit(epoch, Right, true, true, 150, 100, true);
        data.Reset();
        data.TrySubmit(epoch, Left, true, true, 180, 70, false);
        data.TrySubmit(epoch, Right, true, false, 140, 0, true);
        var state = new DS4State();
        data.TryApplyCurrentGyro(epoch, state, Right, true, true);
        Assert.AreEqual((byte)140, state.RX);
        Assert.AreEqual((byte)128, state.RY);
        data.TryClearGyro(epoch);
        data.ApplyTo(state);
        Assert.AreEqual((byte)180, state.LX);
        Assert.AreEqual((byte)70, state.LY);
    }

    [TestMethod]
    public void ResetAndDiscardRetireCurrentGyroAndIgnoreCompatibilityArrayWrites()
    {
        using var fixture = new ProducerFixture();
        var data = fixture.Data;
        long epoch = data.CaptureEpoch();
        data.TrySubmit(epoch, Right, true, true, 170, 80, true, fixture.Slot);
        Mapping.DiscardPostMapStickData(fixture.Slot);
        Assert.IsFalse(data.TrySubmit(epoch, Right, true, true, 255, 0, true, fixture.Slot));
        Assert.AreEqual((byte)128, Mapping.gyroStickX[fixture.Slot]);
        Mapping.gyroStickX[fixture.Slot] = 255;
        Mapping.gyroStickY[fixture.Slot] = 0;
        var state = new DS4State();
        Mapping.TempMouseJoystick(fixture.Slot, state);
        Assert.AreEqual((byte)128, state.RX);
        Assert.AreEqual((byte)128, state.RY);
        data.TrySubmit(data.CaptureEpoch(), Right, true, true, 170, 80, true, fixture.Slot);
        fixture.Mouse.ResetToggleGyroModes();
        data.ApplyTo(state);
        Mapping.TempMouseJoystick(fixture.Slot, state);
        Assert.AreEqual((byte)128, state.RX);
    }

    [DataTestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public void RealGyroAndTouchProducersCompareBothTypedAxes(bool gyro, bool left, bool negative)
    {
        using var fixture = new ProducerFixture();
        fixture.GyroInfo.outputStick = left ? Left : Right;
        fixture.TouchInfo.outputStick = left ? TouchMouseStickInfo.OutputStick.LeftStick :
            TouchMouseStickInfo.OutputStick.RightStick;
        var args = Args(negative ? -32 : 32, negative ? -32 : 32, negative ? 32 : -32);
        Publish();
        byte x = left ? fixture.Data.LX : fixture.Data.RX;
        byte y = left ? fixture.Data.LY : fixture.Data.RY;
        Assert.IsTrue(x != 128 && y != 128 && x != 0 && x != 255 && y != 0 && y != 255);
        fixture.Data.Reset();
        var preciseX = Axis(x > 128 ? x - 0.25 : x + 0.25);
        var preciseY = Axis(y > 128 ? y - 0.25 : y + 0.25);
        Assert.AreEqual(x, preciseX.LegacyValue, "Byte-only comparisons would miss this stronger candidate.");
        Assert.AreEqual(y, preciseY.LegacyValue);
        if (left) { fixture.Data.LXAxis = preciseX; fixture.Data.LYAxis = preciseY; }
        else { fixture.Data.RXAxis = preciseX; fixture.Data.RYAxis = preciseY; }
        fixture.Data.dirty = true;
        Publish();
        var state = new DS4State();
        fixture.Data.ApplyTo(state);
        Assert.AreEqual(x, left ? state.LX : state.RX);
        Assert.AreEqual(y, left ? state.LY : state.RY);
        Assert.IsFalse(left ? state.LXAxis.IsHighResolution || state.LYAxis.IsHighResolution :
            state.RXAxis.IsHighResolution || state.RYAxis.IsHighResolution);

        void Publish()
        {
            if (gyro) fixture.Gyro(args, default);
            else fixture.Touch(negative ? -32 : 32, negative ? -32 : 32);
        }
    }

    [TestMethod]
    public void SlotOneGyroSelectsItsOwnYawOrRollSetting()
    {
        using var fixture = new ProducerFixture();
        Global.GyroMouseStickHorizontalAxis[0] = 0;
        Global.GyroMouseStickHorizontalAxis[fixture.Slot] = 1;
        var args = Args(16, 64, -32);
        fixture.Gyro(args, default);
        byte roll = fixture.Data.RX;
        fixture.Data.Reset();
        Global.GyroMouseStickHorizontalAxis[fixture.Slot] = 0;
        fixture.Gyro(args, default);
        byte yaw = fixture.Data.RX;
        Assert.AreEqual((byte)(128 + 127 * (64.0 / 128)), roll);
        Assert.AreEqual((byte)(128 + 127 * (16.0 / 128)), yaw);
        Assert.IsTrue(roll > yaw);
    }

    [TestMethod]
    public void ActualGyroCoreCannotPublishAfterItsCapturedOperationIsReset()
    {
        using var fixture = new ProducerFixture();
        long old = fixture.Data.CaptureEpoch();
        fixture.Mouse.ResetToggleGyroModes();
        fixture.Data.TrySubmit(fixture.Data.CaptureEpoch(), Right, true, true, 140, 110, true);
        var core = typeof(Mouse).GetMethod("SixMouseStickCore", BindingFlags.NonPublic | BindingFlags.Instance)
            .CreateDelegate<CapturedGyroProducer>(fixture.Mouse);
        core(Args(128, 128, -128), default, fixture.Data, old);
        var state = new DS4State();
        fixture.Data.ApplyTo(state);
        Assert.AreEqual((byte)140, state.RX);
        Assert.AreEqual((byte)110, state.RY);
    }

    [TestMethod]
    public void ScalarAdmissionConsumptionAndResetAllocateNothingAfterWarmup()
    {
        var data = new Mapping.PostMapStickData();
        var state = new DS4State();
        for (int i = 0; i < 10000; i++) Cycle();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) Cycle();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);

        void Cycle()
        {
            data.RequestReset();
            long epoch = data.CaptureEpoch();
            data.TrySubmit(epoch, Left, true, true, 150, 100, true);
            data.TrySubmit(epoch, Right, true, true, 180, 70, false);
            data.TryApplyCurrentGyro(epoch, state, Left, true, true);
            data.ApplyTo(state);
            data.TryClearGyro(epoch);
        }
    }

    private static DS4MappedStickAxis Axis(double coordinate)
    {
        Assert.IsTrue(DS4MappedStickAxis.TryFromProfileCoordinate(coordinate, out var result));
        return result;
    }

    private static SixAxisEventArgs Args(int yaw, int roll, int pitch) => new(DateTime.UnixEpoch,
        new SixAxis(0, 0, 0, 0, 0, 0, 0.004)
        { gyroYawFull = yaw, gyroRollFull = roll, gyroPitchFull = pitch });

    private sealed class ProducerFixture : IDisposable
    {
        private readonly BackingStore previousStore = Global.store;
        private readonly Mapping.PostMapStickData previousData;
        private readonly byte previousX, previousY;
        private static readonly FieldInfo StoreField = typeof(Global).GetField("m_Config",
            BindingFlags.Static | BindingFlags.NonPublic);
        internal int Slot => 1;
        internal Mapping.PostMapStickData Data { get; }
        internal Mouse Mouse { get; }
        internal GyroMouseStickInfo GyroInfo { get; }
        internal TouchMouseStickInfo TouchInfo { get; }
        internal GyroProducer Gyro { get; }
        internal Action<int, int> Touch { get; }

        internal ProducerFixture()
        {
            previousData = Mapping.mapStickActionData[Slot];
            previousX = Mapping.gyroStickX[Slot]; previousY = Mapping.gyroStickY[Slot];
            StoreField.SetValue(null, new BackingStore());
            Mapping.mapStickActionData[Slot] = Data = new Mapping.PostMapStickData();
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(1, 1, Switch2Transport.Usb,
                out var runtime, out _));
            Mouse = new Mouse(Slot, runtime);
            Global.GyroOutputMode[Slot] = GyroOutMode.MouseJoystick;
            Global.GyroMouseStickHorizontalAxis[Slot] = 0;
            GyroInfo = Global.GetGyroMouseStickInfo(Slot);
            GyroInfo.deadZone = 0; GyroInfo.maxZone = 128;
            GyroInfo.antiDeadX = GyroInfo.antiDeadY = 0;
            GyroInfo.useSmoothing = false; GyroInfo.jitterCompensation = false;
            GyroInfo.outputStick = Right;
            GyroInfo.outputStickDir = GyroMouseStickInfo.OutputStickAxes.XY;
            TouchInfo = Global.GetTouchMouseStickInfo(Slot);
            TouchInfo.deadZone = 0; TouchInfo.maxZone = 128;
            TouchInfo.antiDeadX = TouchInfo.antiDeadY = 0;
            TouchInfo.UseSmoothing = false;
            TouchInfo.outputStick = TouchMouseStickInfo.OutputStick.RightStick;
            TouchInfo.outputStickDir = TouchMouseStickInfo.OutputStickAxes.XY;
            Gyro = typeof(Mouse).GetMethod("SixMouseStick", BindingFlags.NonPublic | BindingFlags.Instance)
                .CreateDelegate<GyroProducer>(Mouse);
            Touch = typeof(Mouse).GetMethod("TouchpadMouseStick", BindingFlags.NonPublic | BindingFlags.Instance)
                .CreateDelegate<Action<int, int>>(Mouse);
        }

        public void Dispose()
        {
            Mapping.mapStickActionData[Slot] = previousData;
            Mapping.gyroStickX[Slot] = previousX; Mapping.gyroStickY[Slot] = previousY;
            StoreField.SetValue(null, previousStore);
        }
    }
}
