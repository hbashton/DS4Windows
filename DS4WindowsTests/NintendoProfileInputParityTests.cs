using System.Numerics;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class NintendoProfileInputParityTests
{
    [TestMethod]
    public void DisabledMotionOptionsPreserveNativeScaleAndCartesianAxes()
    {
        var options = new NintendoMotionProfileOptions();
        Vector3 gyro = new(100, -20, 30), accel = new(21, 8192, -40);
        Assert.IsTrue(options.TryApply(gyro, accel, .01, false, 0, 1, out var result, out var resultAccel));
        Assert.IsTrue(Vector3.Distance(gyro, result) < 0.001f);
        Assert.AreEqual(accel, resultAccel);
    }

    [TestMethod]
    public void SoftDeadzoneUsesSameDegreesPerSecondAsSwitch2WithoutChangingRoll()
    {
        var options = new NintendoMotionProfileOptions();
        Assert.IsTrue(options.TryApply(new Vector3(100, 20, 30), new Vector3(0, 8192, 0),
            .01, false, 50, 1, out var result, out _));
        Assert.AreEqual(100.0 - 50.0 / (.070 * 16.384), result.X, .001);
        Assert.AreEqual(0.0f, result.Y);
        Assert.AreEqual(30.0f, result.Z, .001f);
    }

    [TestMethod]
    public void HorizonUsesBothSensorsAndReturnsToRawWhenDisabled()
    {
        var options = new NintendoMotionProfileOptions();
        Vector3 gyro = new(100, 20, 30), accel = new(0, 8192, 0);
        Assert.IsTrue(options.TryApply(gyro, accel, 0, true, 0, 1, out var baseline, out _));
        Assert.IsTrue(Vector3.Distance(gyro, baseline) < .001f);
        Assert.IsTrue(options.TryApply(gyro, accel, .01, true, 0, 1, out var result, out var resultAccel));
        Assert.AreEqual(0.0f, result.Z, .001f, "Horizon projects aiming onto yaw/pitch rather than retaining roll as an output axis.");
        Assert.AreEqual(8192.0f, resultAccel.Length(), .1f);
        Assert.IsTrue(options.TryApply(gyro, accel, .01, false, 0, 1, out result, out resultAccel));
        Assert.IsTrue(Vector3.Distance(gyro, result) < .001f);
        Assert.AreEqual(accel, resultAccel);
    }

    [TestMethod]
    public void MotionOptionsRejectNonfiniteSensorsAndWarmPathDoesNotAllocate()
    {
        var options = new NintendoMotionProfileOptions();
        Assert.IsFalse(options.TryApply(new Vector3(float.NaN, 0, 0), Vector3.Zero, .01, true, 0, 1, out _, out _));
        Vector3 gyro = new(20, 10, 0), accel = new(0, 8192, 0);
        for (int i = 0; i < 100; i++) options.TryApply(gyro, accel, .01, true, 1, 1, out _, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool success = true;
        for (int i = 0; i < 1_000; i++) success &= options.TryApply(gyro, accel, .01, true, 1, 1, out _, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(success);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void PairedMouseTimingUsesLogicalReportNotPhysicalOwnerInterval()
    {
        DS4State state = State(1_000);
        state.elapsedTime = .0075;
        Assert.AreEqual(7.5, NintendoProfileInput.ResolveReportIntervalMilliseconds(state, 15.0));
        state.elapsedTime = 0;
        Assert.AreEqual(0.0, NintendoProfileInput.ResolveReportIntervalMilliseconds(state, 15.0));
        state.elapsedTime = double.NaN;
        Assert.AreEqual(0.0, NintendoProfileInput.ResolveReportIntervalMilliseconds(state, 15.0));
        state.NintendoInputStatus = default;
        Assert.AreEqual(15.0, NintendoProfileInput.ResolveReportIntervalMilliseconds(state, 15.0));
    }

    [TestMethod]
    public void OriginalStickSensitivityUsesCanonicalProfileSide()
    {
        DS4State state = State(1_000);
        Assert.AreEqual(0.4, Switch2MappedStickMouseSensitivity.ResolveGain(state, DS4Controls.LXPos, 2, 8), .000001);
        Assert.AreEqual(1.6, Switch2MappedStickMouseSensitivity.ResolveGain(state, DS4Controls.RYNeg, 2, 8), .000001);
        Assert.AreEqual(1.0, Switch2MappedStickMouseSensitivity.ResolveGain(state, DS4Controls.Cross, 2, 8));
        state.NintendoInputStatus = state.NintendoInputStatus with { ContractVersion = 99 };
        Assert.AreEqual(1.0, Switch2MappedStickMouseSensitivity.ResolveGain(state, DS4Controls.LXPos, 2, 8));
    }

    [TestMethod]
    public void OriginalStickScrollTapUsesExistingSectorGateAndResetsAtPairBoundary()
    {
        DS4State state = State(1_000);
        Switch2StickScrollTapLaneState lane = default;
        Assert.IsTrue(Scroll(state, 128, ref lane, out var frame));
        state.NintendoInputStatus = state.NintendoInputStatus with { CompletionTimestampQpc = 1_040 };
        Assert.IsTrue(Scroll(state, 255, ref lane, out frame));
        Assert.IsTrue(frame.TryHandle(DS4Controls.LXPos, out bool emit, out int step));
        Assert.IsTrue(emit);
        Assert.IsTrue(step > 0);
        state.NintendoInputStatus = state.NintendoInputStatus with { CompletionTimestampQpc = 1_080, PairEpoch = 42 };
        Assert.IsTrue(Scroll(state, 255, ref lane, out frame));
        Assert.IsTrue(frame.TryHandle(DS4Controls.LXPos, out emit, out _));
        Assert.IsFalse(emit, "A newly linked controller held off-center is a baseline, not a scroll edge.");
    }

    [TestMethod]
    public void UnsupportedHardwareBitsCannotBeInjectedThroughNeutralMetadata()
    {
        DS4State state = State(1_000);
        foreach (var unsupported in new[] { Switch2JoyConProfileButton.C, Switch2JoyConProfileButton.LeftIrSensor,
                     Switch2JoyConProfileButton.RightIrSensor, (Switch2JoyConProfileButton)(1u << 31) })
        {
            state.NintendoInputStatus = state.NintendoInputStatus with { Buttons = unsupported };
            Assert.IsFalse(NintendoProfileInput.TryRead(state, out _));
            Assert.IsFalse(Switch2GyroTriggerModifier.TryReadInput(state, 1, 1, true, out _));
        }
    }

    [TestMethod]
    public void OriginalDirectionTapExpiresAndDoesNotReplayAcrossOrientation()
    {
        DS4State state = State(1_000);
        state.NintendoInputStatus = state.NintendoInputStatus with
        {
            Mode = Switch2JoyConProfileMode.StandaloneVerticalLeft, PairEpoch = 0, RightPresent = false, RightGeneration = 0,
        };
        Switch2StickDirectionTapLaneState lane = default;
        var modes = new Switch2StickDirectionActivationModes(0, 0, 0, Switch2StickDirectionActivationMode.Tap, 0, 0, 0, 0);
        Assert.IsTrue(Direction(128, out _));
        state.NintendoInputStatus = state.NintendoInputStatus with { CompletionTimestampQpc = 1_010 };
        Assert.IsTrue(Direction(255, out var frame));
        Assert.IsTrue(frame.TryOverride(DS4Controls.LXPos, out bool active));
        Assert.IsTrue(active);
        state.NintendoInputStatus = state.NintendoInputStatus with { CompletionTimestampQpc = 1_100 };
        Assert.IsTrue(Direction(255, out frame));
        Assert.IsTrue(frame.TryOverride(DS4Controls.LXPos, out active));
        Assert.IsFalse(active);
        state.NintendoInputStatus = state.NintendoInputStatus with
        {
            CompletionTimestampQpc = 1_110, Mode = Switch2JoyConProfileMode.StandaloneHorizontalLeft,
        };
        Assert.IsTrue(Direction(255, out frame));
        Assert.IsTrue(frame.TryOverride(DS4Controls.LXPos, out active));
        Assert.IsFalse(active);

        bool Direction(double x, out Switch2StickDirectionTapFrame output) =>
            Switch2StickDirectionTapLane.TryAdvance(state, x, 128, 128, 128, modes, 1, ref lane, out output);
    }

    [TestMethod]
    public void HighRateOwnerFenceRejectsPausedRetiredDisconnectedAndSecondarySessions()
    {
        var left = new LegacyJoyConConnection(new FakeDevice(InputDeviceType.JoyConL), 0, 1);
        var right = new LegacyJoyConConnection(new FakeDevice(InputDeviceType.JoyConR), 1, 2);
        var leftStandalone = new LegacyJoyConGroup(left, null, 0, new NoProjection());
        var rightStandalone = new LegacyJoyConGroup(right, null, 0, new NoProjection());
        var pair = new LegacyJoyConGroup(left, right, 3, new NoProjection(), leftStandalone, rightStandalone);
        left.Group = right.Group = pair;
        int outputCount = 0;
        var lease = new NintendoMousePresentationLease((_, _) => Interlocked.Increment(ref outputCount));
        try
        {
            Assert.IsFalse(lease.TrySetSource(right, Switch2ContinuousMouseSource.Gyro, false, 0, 0, 1));
            Assert.IsTrue(lease.TrySetSource(left, Switch2ContinuousMouseSource.Gyro, false, 0, 0, 1));
            Assert.IsTrue(lease.HasCurrentOwner);
            left.Paused = true;
            Assert.IsFalse(lease.HasCurrentOwner);
            Assert.AreEqual(0, Volatile.Read(ref outputCount), "Inactive sources must not emit.");
            Assert.IsFalse(lease.TrySetSource(left, Switch2ContinuousMouseSource.Gyro, false, 0, 0, 1));
            left.Paused = false;
            pair.Active = false;
            Assert.IsFalse(lease.HasCurrentOwner);
            pair.Active = true;
            left.Group = leftStandalone;
            Assert.IsFalse(lease.HasCurrentOwner, "Same physical owner does not authorize the old pair.");
            Assert.IsTrue(lease.TrySetSource(left, Switch2ContinuousMouseSource.Gyro, false, 0, 0, 1));
            Assert.IsTrue(lease.HasCurrentOwner);
            left.Connected = false;
            Assert.IsFalse(lease.HasCurrentOwner);
            Assert.IsTrue(lease.Clear(CancellationToken.None));
            Assert.IsFalse(lease.HasCurrentOwner);
        }
        finally { lease.Stop(); }
    }

    [TestMethod]
    public void PresentationValidatorRunsAtEmissionNotOnlyAtSourceAdmission()
    {
        using var checkedOwner = new ManualResetEventSlim();
        int emitted = 0;
        var presenter = new Switch2HighRateMousePresenter((_, _) => Interlocked.Increment(ref emitted), () =>
        {
            checkedOwner.Set();
            return false;
        });
        try
        {
            Assert.IsTrue(presenter.TrySetSource(Switch2ContinuousMouseSource.Gyro, true, 100_000, 0, 1));
            Assert.IsTrue(checkedOwner.Wait(1_000));
            Assert.AreEqual(0, Volatile.Read(ref emitted));
        }
        finally { presenter.Stop(); }
    }

    [TestMethod]
    public void ProjectedMotionEnvelopeCarriesExactLogicalButtonsAndClearsBorrowOnReset()
    {
        var sixAxis = new DS4SixAxis();
        DS4State logical = State(1_000);
        logical.R1 = true;
        SixAxisEventArgs observed = null;
        sixAxis.SixAccelMoved += (_, args) =>
        {
            observed = args;
            Assert.AreSame(logical, args.SourceState);
            Assert.AreSame(logical.Motion, args.sixAxis);
        };
        sixAxis.FireProjectedSixAxisEvent(logical);
        Assert.IsNotNull(observed);
        observed.Reset(DateTime.UnixEpoch, new SixAxis(0, 0, 0, 0, 0, 0, .01));
        Assert.IsNull(observed.SourceState);
    }

    [TestMethod]
    [DoNotParallelize]
    public void GyroControlsActivationUsesProjectedOppositeHandNotPhysicalOwner()
    {
        const int slot = Global.TEST_PROFILE_INDEX;
        var oldMode = Global.GyroOutputMode[slot];
        var oldTrigger = Global.GyroControlsInf[slot].triggers;
        bool oldCond = Global.GyroControlsInf[slot].triggerCond;
        bool oldTurns = Global.GyroTriggerTurns[slot];
        try
        {
            Global.GyroOutputMode[slot] = GyroOutMode.Controls;
            Global.GyroControlsInf[slot].triggers = "6"; // R1
            Global.GyroControlsInf[slot].triggerCond = false;
            Global.GyroTriggerTurns[slot] = true;
            var device = new FakeDevice(InputDeviceType.JoyConL);
            var mouse = new Mouse(slot, device) { ToggleGyroControls = false };
            DS4State logical = State(1_000);
            logical.R1 = true;
            Assert.IsFalse(device.getCurrentStateRef().R1);
            var args = new SixAxisEventArgs(DateTime.UnixEpoch, logical.Motion);
            args.Reset(DateTime.UnixEpoch, logical.Motion, logical);
            mouse.sixaxisMoved(device.SixAxis, args);
            Assert.IsTrue(logical.Motion.outputGyroControls);
            Assert.IsFalse(device.getCurrentStateRef().Motion.outputGyroControls);
            logical.R1 = false;
            mouse.sixaxisMoved(device.SixAxis, args);
            Assert.IsFalse(logical.Motion.outputGyroControls);
        }
        finally
        {
            Global.GyroOutputMode[slot] = oldMode;
            Global.GyroControlsInf[slot].triggers = oldTrigger;
            Global.GyroControlsInf[slot].triggerCond = oldCond;
            Global.GyroTriggerTurns[slot] = oldTurns;
        }
    }

    [TestMethod]
    public void OriginalOrientationObserverBaselinesOnlySamePhysicalStandaloneLayoutChange()
    {
        DS4State state = State(1_000);
        state.NintendoInputStatus = state.NintendoInputStatus with
        {
            Mode = Switch2JoyConProfileMode.StandaloneVerticalLeft, PairEpoch = 0, RightPresent = false, RightGeneration = 0,
        };
        Switch2GyroActivationOrientation orientation = default;
        Assert.IsFalse(orientation.Observe(state));
        state.NintendoInputStatus = state.NintendoInputStatus with { Mode = Switch2JoyConProfileMode.StandaloneHorizontalLeft };
        Assert.IsTrue(orientation.Observe(state));
        state.NintendoInputStatus = state.NintendoInputStatus with { Mode = Switch2JoyConProfileMode.StandaloneVerticalLeft, LeftGeneration = 9 };
        Assert.IsFalse(orientation.Observe(state));
    }

    private static bool Scroll(DS4State state, double x, ref Switch2StickScrollTapLaneState lane, out Switch2StickScrollTapFrame frame) =>
        Switch2StickScrollTapLane.TryAdvance(state, x, 128, 128, 128, Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref lane, out frame);

    private static DS4State State(long timestamp) => new()
    {
        NintendoInputStatus = new(NintendoInputStatus.CurrentVersion, Switch2JoyConProfileMode.Joined,
            3, 1, 2, true, true, timestamp, 1_000, 0, 0, 0),
    };

    private sealed class NoProjection : ILegacyJoyConProfileProjection
    {
        public bool TryApply(in LegacyJoyConProjectionInput input, DS4State state) => false;
    }

    private sealed class FakeDevice : DS4Device
    {
        internal FakeDevice(InputDeviceType type) : base("Nintendo input-only test", type, ConnectionType.BT) { }
    }
}
