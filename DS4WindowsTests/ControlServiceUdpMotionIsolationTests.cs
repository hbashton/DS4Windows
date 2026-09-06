using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ControlServiceUdpMotionIsolationTests
{
    private static readonly FieldInfo[] MotionScalarFields = typeof(SixAxis)
        .GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Where(field => field.FieldType.IsValueType).ToArray();
    private static readonly FieldInfo[] StateValueFields = typeof(DS4State)
        .GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Where(field => field.FieldType.IsValueType).ToArray();

    [TestMethod]
    public void SmoothingAndSwitch2YawTransformOnlyTheHandlerOwnedObservation()
    {
        using var fixture = new Fixture(smoothing: true);
        var expectedAccel = new OneEuroFilter3D();
        var expectedGyro = new OneEuroFilter3D();
        DS4State ownedState = fixture.Observation.State;
        SixAxis ownedMotion = ownedState.Motion;
        SixAxis ownedPrevious = null;

        // The first sample primes the actual filters; the second must be
        // filtered, not merely copied. Only the output yaw receives level 4's
        // exact 1.25 multiplier, after smoothing.
        foreach (int seed in new[] { 10, 70 })
        {
            fixture.Current.Motion = Motion(seed);
            fixture.Current.Motion.previousAxis = Motion(seed + 100);
            var canonicalBefore = new StateImage(fixture.Current);
            var tempBefore = new StateImage(fixture.Temp);
            double rate = 1.0 / fixture.Current.elapsedTime;
            double[] expected = {
                expectedAccel.axis1Filter.Filter(fixture.Current.Motion.accelXG, rate),
                expectedAccel.axis2Filter.Filter(fixture.Current.Motion.accelYG, rate),
                expectedAccel.axis3Filter.Filter(fixture.Current.Motion.accelZG, rate),
                expectedGyro.axis1Filter.Filter(fixture.Current.Motion.angVelYaw, rate) * 1.25,
                expectedGyro.axis2Filter.Filter(fixture.Current.Motion.angVelPitch, rate),
                expectedGyro.axis3Filter.Filter(fixture.Current.Motion.angVelRoll, rate),
            };

            fixture.Invoke();

            Assert.AreSame(ownedState, fixture.Observation.State);
            Assert.AreSame(ownedMotion, fixture.Observation.State.Motion);
            if (ownedPrevious != null)
                Assert.AreSame(ownedPrevious, ownedMotion.previousAxis);
            ownedPrevious = ownedMotion.previousAxis;
            Assert.AreNotSame(fixture.Current.Motion, ownedMotion);
            Assert.AreNotSame(fixture.Current.Motion.previousAxis, ownedPrevious);
            CollectionAssert.AreEqual(expected, UdpMotionValues(ownedMotion));
            if (seed == 70)
            {
                Assert.AreNotEqual(fixture.Current.Motion.accelXG, ownedMotion.accelXG);
                Assert.AreNotEqual(fixture.Current.Motion.angVelPitch, ownedMotion.angVelPitch);
                Assert.AreNotEqual(fixture.Current.Motion.angVelYaw * 1.25, ownedMotion.angVelYaw);
            }
            canonicalBefore.AssertUnchanged();
            tempBefore.AssertUnchanged();
            AssertMotionScalars(fixture.Current.Motion.previousAxis, ownedPrevious);
        }
    }

    [TestMethod]
    public void RepeatedYawAdjustmentDoesNotCompoundOrWriteTheMapperTempState()
    {
        using var fixture = new Fixture(smoothing: false);
        fixture.Current.Motion.angVelYaw = 12;
        var canonicalBefore = new StateImage(fixture.Current);
        var tempBefore = new StateImage(fixture.Temp);
        for (int index = 0; index < 3; index++)
        {
            fixture.Invoke();
            Assert.AreEqual(15.0, fixture.Observation.State.Motion.angVelYaw);
            canonicalBefore.AssertUnchanged();
            tempBefore.AssertUnchanged();
        }

        fixture.Current.Motion.angVelYaw = -8;
        fixture.Invoke();
        Assert.AreEqual(-10.0, fixture.Observation.State.Motion.angVelYaw);
        Assert.AreEqual(-8.0, fixture.Current.Motion.angVelYaw);
        tempBefore.AssertUnchanged();
    }

    [TestMethod]
    public void WrongSenderAndDifferentObjectSuccessorCannotRefreshAnOldHandler()
    {
        using var fixture = new Fixture(smoothing: true);
        fixture.Invoke();
        var observationBefore = new StateImage(fixture.Observation.State);
        Switch2RuntimeInputDevice successor = CreateRuntime(91_003, 91_004);
        successor.DeviceSlotNumber = Fixture.Slot;
        fixture.Current.Motion = Motion(700);
        fixture.Current.Cross = !fixture.Current.Cross;
        var canonicalBefore = new StateImage(fixture.Current);
        var tempBefore = new StateImage(fixture.Temp);

        // A matching numeric slot is not sender authority, either before or
        // after the slot contains a replacement object.
        fixture.Handler(successor, EventArgs.Empty);
        observationBefore.AssertUnchanged();
        fixture.Service.DS4Controllers[Fixture.Slot] = successor;
        fixture.Invoke();
        fixture.Handler(successor, EventArgs.Empty);
        observationBefore.AssertUnchanged();
        canonicalBefore.AssertUnchanged();
        tempBefore.AssertUnchanged();
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NullMotionIsSafeAndDoesNotRetainTheLastObservedMotion(bool smoothing)
    {
        using var fixture = new Fixture(smoothing);
        fixture.Invoke();
        Assert.IsNotNull(fixture.Observation.State.Motion);
        fixture.Current.Motion = null;
        var canonicalBefore = new StateImage(fixture.Current);
        var tempBefore = new StateImage(fixture.Temp);

        fixture.Invoke();
        fixture.Invoke();

        Assert.IsNull(fixture.Observation.State.Motion);
        canonicalBefore.AssertUnchanged();
        tempBefore.AssertUnchanged();
    }

    [TestMethod]
    public void EachColdHandlerOwnsAnIndependentReusableSnapshot()
    {
        using var fixture = new Fixture(smoothing: false);
        DS4Device.ReportHandler<EventArgs> other = CreateHandler(fixture.Service, fixture.Source);
        DS4StateOwnedSnapshot otherObservation = GetObservation(other);
        Assert.AreNotSame(fixture.Observation, otherObservation);
        Assert.AreNotSame(fixture.Observation.State, otherObservation.State);
        Assert.AreNotSame(fixture.Observation.State.Motion, otherObservation.State.Motion);
        fixture.Invoke();
        var firstBefore = new StateImage(fixture.Observation.State);
        fixture.Current.Motion = Motion(900);
        other(fixture.Source, EventArgs.Empty);
        firstBefore.AssertUnchanged();
        Assert.AreEqual(fixture.Current.Motion.angVelYaw * 1.25,
            otherObservation.State.Motion.angVelYaw);
    }

    [TestMethod]
    public void DecodedSwitch2CurrentAndPreviousMotionRemainRawAfterProductionUdpObservation()
    {
        using var fixture = new Fixture(smoothing: true);
        // This unbound runtime has no transport, output owner or OS input
        // callback. Its frames still traverse the real raw Common05 decoder,
        // profile projection, publication and previous-state commit.
        fixture.Source.StartUpdate();
        var tempBefore = new StateImage(fixture.Temp);
        foreach (uint counter in new uint[] { 1, 2 })
        {
            short sample = checked((short)(counter * 800));
            var frame = Switch2RuntimeInputDeviceTests.CreateProFrame(
                91_001, 91_002, (uint)Switch2ProButton.FaceSouth,
                counter: counter, leftX: 0x801,
                timestamp: 100_000 + counter * 20_000,
                accelerometer: new Switch2Vector3Raw(sample, checked((short)-sample), checked((short)(sample / 2))),
                gyroscope: new Switch2Vector3Raw(checked((short)(sample / 2)), sample, checked((short)-sample)));
            Assert.IsTrue(fixture.Source.TryPublishPro(frame));
            DS4State rawCurrent = fixture.Source.getCurrentStateRef();
            DS4State rawPrevious = fixture.Source.getPreviousStateRef();
            Assert.IsTrue(rawCurrent.Switch2RawInputStatus.IsValid);
            Assert.AreNotEqual(0.0, rawCurrent.Motion.angVelYaw);
            rawCurrent.CopyTo(fixture.Current);
            Assert.AreSame(rawCurrent.Motion, fixture.Current.Motion,
                "Exercise the borrowed canonical motion reference used by the mapper.");
            var currentBefore = new StateImage(rawCurrent);
            var previousBefore = new StateImage(rawPrevious);
            var canonicalBefore = new StateImage(fixture.Current);

            // Invoke the actual production factory's handler explicitly.
            // This tests ownership, not automatic Switch 2 UDP registration.
            fixture.Invoke();
            fixture.Invoke();

            currentBefore.AssertUnchanged();
            previousBefore.AssertUnchanged();
            canonicalBefore.AssertUnchanged();
            tempBefore.AssertUnchanged();
            Assert.AreNotSame(rawCurrent.Motion, fixture.Observation.State.Motion);
            Assert.AreNotSame(rawPrevious.Motion, fixture.Observation.State.Motion);
            Assert.AreEqual(rawCurrent.Switch2RawInputStatus,
                fixture.Observation.State.Switch2RawInputStatus);
        }
    }

    private static DS4Device.ReportHandler<EventArgs> CreateHandler(
        ControlService service, DS4Device source)
    {
        MethodInfo factory = typeof(ControlService).GetMethod("CreateDevUDPMotionHandler",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(factory);
        return (DS4Device.ReportHandler<EventArgs>)factory.Invoke(service, new object[] { Fixture.Slot, source });
    }

    private static DS4StateOwnedSnapshot GetObservation(DS4Device.ReportHandler<EventArgs> handler)
    {
        FieldInfo[] fields = handler.Target.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(DS4StateOwnedSnapshot)).ToArray();
        Assert.AreEqual(1, fields.Length,
            "The production closure must own exactly one cold snapshot, not share mapper scratch.");
        return (DS4StateOwnedSnapshot)fields[0].GetValue(handler.Target);
    }

    private static Switch2RuntimeInputDevice CreateRuntime(ulong deviceGeneration, ulong transportGeneration)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(deviceGeneration,
            transportGeneration, Switch2Transport.Usb, out var runtime, out var failure), failure.ToString());
        return runtime;
    }

    private static SixAxis Motion(int seed) => new(0, 0, 0, 0, 0, 0, 0.002)
    {
        gyroYaw = seed + 1, gyroPitch = seed + 2, gyroRoll = -seed - 3,
        accelX = seed + 4, accelY = -seed - 5, accelZ = seed + 6,
        outputAccelX = seed + 7, outputAccelY = seed + 8, outputAccelZ = -seed - 9,
        outputGyroControls = true,
        accelXG = seed + 0.25, accelYG = -seed - 0.5, accelZG = seed + 0.75,
        angVelYaw = seed + 0.5, angVelPitch = -seed - 0.75, angVelRoll = seed + 1.25,
        gyroYawFull = seed + 10, gyroPitchFull = -seed - 11, gyroRollFull = seed + 12,
        accelXFull = seed + 13, accelYFull = seed + 14, accelZFull = -seed - 15,
    };

    private static double[] UdpMotionValues(SixAxis motion) => new[] {
        motion.accelXG, motion.accelYG, motion.accelZG,
        motion.angVelYaw, motion.angVelPitch, motion.angVelRoll,
    };

    private static void AssertMotionScalars(SixAxis expected, SixAxis actual)
    {
        foreach (FieldInfo field in MotionScalarFields)
            Assert.AreEqual(field.GetValue(expected), field.GetValue(actual), field.Name);
    }

    private sealed class StateImage
    {
        private readonly DS4State source;
        private readonly object[] values;
        private readonly SixAxis motion, previous;
        private readonly object[] motionValues, previousValues;

        internal StateImage(DS4State source)
        {
            this.source = source;
            values = StateValueFields.Select(field => field.GetValue(source)).ToArray();
            motion = source.Motion;
            previous = motion?.previousAxis;
            motionValues = motion == null ? null : MotionScalarFields.Select(field => field.GetValue(motion)).ToArray();
            previousValues = previous == null ? null : MotionScalarFields.Select(field => field.GetValue(previous)).ToArray();
        }

        internal void AssertUnchanged()
        {
            for (int index = 0; index < StateValueFields.Length; index++)
                Assert.AreEqual(values[index], StateValueFields[index].GetValue(source), StateValueFields[index].Name);
            Assert.AreSame(motion, source.Motion);
            if (motion == null) return;
            Assert.AreSame(previous, motion.previousAxis);
            for (int index = 0; index < MotionScalarFields.Length; index++)
            {
                Assert.AreEqual(motionValues[index], MotionScalarFields[index].GetValue(motion), MotionScalarFields[index].Name);
                if (previous != null)
                    Assert.AreEqual(previousValues[index], MotionScalarFields[index].GetValue(previous), "previous." + MotionScalarFields[index].Name);
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal const int Slot = 2;
        private readonly BackingStore previousStore = Global.store;
        private static readonly FieldInfo StoreField = typeof(Global)
            .GetField("m_Config", BindingFlags.Static | BindingFlags.NonPublic);
        internal readonly ControlService Service;
        internal readonly Switch2RuntimeInputDevice Source;
        internal readonly DS4State Current, Temp;
        internal readonly DS4Device.ReportHandler<EventArgs> Handler;
        internal readonly DS4StateOwnedSnapshot Observation;

        internal Fixture(bool smoothing)
        {
            StoreField.SetValue(null, new BackingStore());
            try
            {
                Global.UseUDPSeverSmoothing = smoothing;
                Global.Switch2CemuhookYawSensitivity[Slot] = 4;
                Source = CreateRuntime(91_001, 91_002);
                Source.DeviceSlotNumber = Slot;
                Service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
                Service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
                Service.DS4Controllers[Slot] = Source;
                Current = new DS4State { Motion = Motion(10), elapsedTime = 0.002, Cross = true };
                Current.Motion.previousAxis = Motion(100);
                Temp = new DS4State { Motion = Motion(1000), elapsedTime = 0.003, Triangle = true, LX = 17 };
                Temp.Motion.previousAxis = Motion(1100);
                SetSlotArray(Service, "CurrentState", Current);
                SetSlotArray(Service, "TempState", Temp);
                SetSlotArray(Service, "udpEuroPairAccel", new OneEuroFilter3D());
                SetSlotArray(Service, "udpEuroPairGyro", new OneEuroFilter3D());
                // _udpServer stays null: no socket, driver, ControlService
                // constructor, discovery or physical device is involved.
                Handler = CreateHandler(Service, Source);
                Observation = GetObservation(Handler);
            }
            catch
            {
                StoreField.SetValue(null, previousStore);
                throw;
            }
        }

        internal void Invoke() => Handler(Source, EventArgs.Empty);

        public void Dispose() => StoreField.SetValue(null, previousStore);

        private static void SetSlotArray<T>(ControlService service, string fieldName, T value)
        {
            var slots = new T[Global.MAX_DS4_CONTROLLER_COUNT];
            slots[Slot] = value;
            typeof(ControlService).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(service, slots);
        }
    }
}
