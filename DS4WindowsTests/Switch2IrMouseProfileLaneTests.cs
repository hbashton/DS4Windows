using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2IrMouseProfileLaneTests
{
    private const long Frequency = 1_000_000;

    [TestMethod]
    public void AutoUsesRightForJoinedAndExplicitLeftStartsFresh()
    {
        Switch2IrMouseProfileLaneState state = default;
        Switch2JoyConRawInputStatus input = Joined(0, leftX: 100,
            rightX: 1_000);

        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Auto, ref state,
            out var first));
        Assert.IsFalse(first.ModeActive);

        input.CompletionTimestampQpc = 1_000;
        input.LeftIrX = 130;
        input.RightIrX = 1_010;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Auto, ref state,
            out var automatic));
        Assert.AreEqual(10, automatic.DeltaX);

        input.CompletionTimestampQpc = 2_000;
        input.LeftIrX = 140;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Left, ref state,
            out var leftFirst));
        Assert.IsFalse(leftFirst.ModeActive);
        Assert.AreEqual(0, leftFirst.DeltaX);

        input.CompletionTimestampQpc = 3_000;
        input.LeftIrX = 150;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Left, ref state,
            out var leftMoved));
        Assert.AreEqual(10, leftMoved.DeltaX);
    }

    [TestMethod]
    public void LifecycleChangeCannotBecomeSyntheticCursorMotion()
    {
        Switch2IrMouseProfileLaneState state = default;
        Switch2JoyConRawInputStatus input = Right(0, x: 100,
            deviceGeneration: 1, transportGeneration: 1);

        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out _));
        input.CompletionTimestampQpc = 1_000;
        input.RightIrX = 120;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var beforeReconnect));
        Assert.AreEqual(20, beforeReconnect.DeltaX);

        input.CompletionTimestampQpc = 2_000;
        input.RightDeviceGeneration = 2;
        input.RightTransportGeneration = 2;
        input.RightIrX = 40_000;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var reconnected));
        Assert.IsFalse(reconnected.ModeActive);
        Assert.AreEqual(0, reconnected.DeltaX);

        input.CompletionTimestampQpc = 3_000;
        input.RightIrX = 40_010;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var afterReconnect));
        Assert.AreEqual(10, afterReconnect.DeltaX);
    }

    [TestMethod]
    public void BothSumsPresentSidesAndFencesTheirLifecyclesIndependently()
    {
        Switch2IrMouseProfileLaneState state = default;
        Switch2JoyConRawInputStatus input = Joined(0, leftX: 100,
            rightX: 1_000);
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Both, ref state,
            out _));

        input.CompletionTimestampQpc = 1_000;
        input.LeftIrX = 110;
        input.RightIrX = 1_020;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Both, ref state,
            out var combined));
        Assert.AreEqual(30, combined.DeltaX);
        Assert.AreEqual(1.08, combined.VelocityX, 0.000001);

        input.CompletionTimestampQpc = 2_000;
        input.LeftDeviceGeneration = 9;
        input.LeftTransportGeneration = 9;
        input.LeftIrX = 50_000;
        input.RightIrX = 1_030;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Both, ref state,
            out var oneSideReconnected));
        Assert.AreEqual(10, oneSideReconnected.DeltaX);
        Assert.AreEqual(0.36, oneSideReconnected.VelocityX, 0.000001);
    }

    [TestMethod]
    public void BothAppliesEachSidesThresholdAndSensitivityIndependently()
    {
        Switch2IrMouseProfileLaneState state = default;
        Switch2JoyConRawInputStatus input = Joined(0, leftX: 100,
            rightX: 1_000);
        input.LeftIrRoughness = 4_500;
        input.RightIrRoughness = 4_500;
        Assert.IsTrue(Switch2IrMouseProfileLane.TryAdvance(input, true,
            Switch2IrMouseSource.Both,
            Switch2IrActivationThreshold.Strict, leftSensitivity: 2.0,
            Switch2IrActivationThreshold.Balanced, rightSensitivity: 5.0,
            scrollMode: Switch2IrMouseScrollMode.Vertical,
            profileRevision: 1, ref state, out _));

        input.CompletionTimestampQpc = 1_000;
        input.LeftIrX = 110;
        input.RightIrX = 1_010;
        Assert.IsTrue(Switch2IrMouseProfileLane.TryAdvance(input, true,
            Switch2IrMouseSource.Both,
            Switch2IrActivationThreshold.Strict, leftSensitivity: 2.0,
            Switch2IrActivationThreshold.Balanced, rightSensitivity: 5.0,
            scrollMode: Switch2IrMouseScrollMode.Vertical,
            profileRevision: 1, ref state, out var result));

        Assert.AreEqual(10, result.DeltaX);
        Assert.AreEqual(0.45, result.VelocityX, 0.000001);
    }

    [TestMethod]
    public void PairChangeAndClockRegressionEachRearmFromCurrentCoordinate()
    {
        Switch2IrMouseProfileLaneState state = default;
        Switch2JoyConRawInputStatus input = Right(10_000, x: 100);
        input.PairEpoch = 1;

        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out _));
        input.CompletionTimestampQpc = 11_000;
        input.RightIrX = 110;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var active));
        Assert.AreEqual(10, active.DeltaX);

        input.CompletionTimestampQpc = 12_000;
        input.PairEpoch = 2;
        input.RightIrX = 30_000;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var repaired));
        Assert.AreEqual(0, repaired.DeltaX);

        input.CompletionTimestampQpc = 11_500;
        input.RightIrX = 50_000;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var regressed));
        Assert.AreEqual(0, regressed.DeltaX);
    }

    [TestMethod]
    public void ProfileRevisionChangeRearmsEvenWhenSettingsMatch()
    {
        Switch2IrMouseProfileLaneState state = default;
        Switch2JoyConRawInputStatus input = Right(0, x: 100);
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out _, profileRevision: 1));

        input.CompletionTimestampQpc = 1_000;
        input.RightIrX = 120;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var active, profileRevision: 1));
        Assert.AreEqual(20, active.DeltaX);

        input.CompletionTimestampQpc = 2_000;
        input.RightIrX = 40_000;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var switched, profileRevision: 2));
        Assert.IsFalse(switched.ModeActive);
        Assert.AreEqual(0, switched.DeltaX);
    }

    [TestMethod]
    public void ActiveSensorScrollsFromItsPhysicalStickOnlyOutsideDeadzone()
    {
        Switch2IrMouseProfileLaneState state = default;
        Switch2JoyConRawInputStatus input = Joined(0, leftX: 100,
            rightX: 1_000);
        input.LogicalLeftStickY = short.MaxValue;
        input.LogicalRightStickY = short.MinValue;

        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var armed));
        Assert.IsFalse(armed.ModeActive);
        Assert.AreEqual(0, armed.WheelDelta);

        input.CompletionTimestampQpc = 1_000;
        input.RightIrX = 1_010;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var active));
        Assert.IsTrue(active.ModeActive);
        Assert.AreEqual(Switch2IrMouseProfileLane.StickScrollScale,
            active.WheelDelta);

        input.CompletionTimestampQpc = 2_000;
        input.RightIrX = 1_020;
        input.LogicalRightStickY = -6_553;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var inDeadzone));
        Assert.AreEqual(0, inDeadzone.WheelDelta);

        input.CompletionTimestampQpc = 3_000;
        input.RightIrDistance = 0;
        input.LogicalRightStickY = short.MinValue;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var sensorReleased));
        Assert.IsFalse(sensorReleased.ModeActive);
        Assert.AreEqual(0, sensorReleased.WheelDelta);
    }

    [TestMethod]
    public void HorizontalPresentationStillUsesPhysicalStickYForScroll()
    {
        Switch2IrMouseProfileLaneState rightState = default;
        Switch2JoyConRawInputStatus right = Right(0, x: 100);
        right.LogicalLeftStickX = short.MinValue;
        Assert.IsTrue(Advance(right, Switch2IrMouseSource.Right,
            ref rightState, out _));
        right.CompletionTimestampQpc = 1_000;
        right.RightIrX = 110;
        Assert.IsTrue(Advance(right, Switch2IrMouseSource.Right,
            ref rightState, out var rightActive));
        Assert.AreEqual(Switch2IrMouseProfileLane.StickScrollScale,
            rightActive.WheelDelta);

        Switch2IrMouseProfileLaneState leftState = default;
        Switch2JoyConRawInputStatus left = Left(0, x: 100);
        left.LogicalLeftStickX = short.MaxValue;
        Assert.IsTrue(Advance(left, Switch2IrMouseSource.Left,
            ref leftState, out _));
        left.CompletionTimestampQpc = 1_000;
        left.LeftIrX = 110;
        Assert.IsTrue(Advance(left, Switch2IrMouseSource.Left,
            ref leftState, out var leftActive));
        Assert.AreEqual(Switch2IrMouseProfileLane.StickScrollScale,
            leftActive.WheelDelta);
    }

    [TestMethod]
    public void FourWayScrollPublishesBothWheelAxesFromPhysicalStick()
    {
        Switch2IrMouseProfileLaneState state = default;
        Switch2JoyConRawInputStatus input = Joined(0, leftX: 100,
            rightX: 1_000);
        input.LogicalRightStickX = short.MaxValue;
        input.LogicalRightStickY = short.MinValue;

        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out _, scrollMode: Switch2IrMouseScrollMode.FourWay));
        input.CompletionTimestampQpc = 1_000;
        input.RightIrX = 1_010;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var active,
            scrollMode: Switch2IrMouseScrollMode.FourWay));

        Assert.IsTrue(active.ModeActive);
        Assert.AreEqual(Switch2IrMouseProfileLane.StickScrollScale,
            active.WheelDelta);
        Assert.AreEqual(Switch2IrMouseProfileLane.StickScrollScale,
            active.HorizontalWheelDelta);
    }

    [TestMethod]
    public void FourWayScrollUsesPhysicalXInEitherHorizontalOrientation()
    {
        Switch2IrMouseProfileLaneState rightState = default;
        Switch2JoyConRawInputStatus right = Right(0, x: 100);
        right.LogicalLeftStickY = short.MaxValue;
        Assert.IsTrue(Advance(right, Switch2IrMouseSource.Right,
            ref rightState, out _,
            scrollMode: Switch2IrMouseScrollMode.FourWay));
        right.CompletionTimestampQpc = 1_000;
        right.RightIrX = 110;
        Assert.IsTrue(Advance(right, Switch2IrMouseSource.Right,
            ref rightState, out var rightActive,
            scrollMode: Switch2IrMouseScrollMode.FourWay));
        Assert.AreEqual(Switch2IrMouseProfileLane.StickScrollScale,
            rightActive.HorizontalWheelDelta);

        Switch2IrMouseProfileLaneState leftState = default;
        Switch2JoyConRawInputStatus left = Left(0, x: 100);
        left.LogicalLeftStickY = short.MinValue;
        Assert.IsTrue(Advance(left, Switch2IrMouseSource.Left,
            ref leftState, out _,
            scrollMode: Switch2IrMouseScrollMode.FourWay));
        left.CompletionTimestampQpc = 1_000;
        left.LeftIrX = 110;
        Assert.IsTrue(Advance(left, Switch2IrMouseSource.Left,
            ref leftState, out var leftActive,
            scrollMode: Switch2IrMouseScrollMode.FourWay));
        Assert.AreEqual(Switch2IrMouseProfileLane.StickScrollScale,
            leftActive.HorizontalWheelDelta);
    }

    [TestMethod]
    public void VerticalScrollModeSuppressesHorizontalStickAxis()
    {
        Switch2IrMouseProfileLaneState state = default;
        Switch2JoyConRawInputStatus input = Joined(0, leftX: 100,
            rightX: 1_000);
        input.LogicalRightStickX = short.MaxValue;

        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out _));
        input.CompletionTimestampQpc = 1_000;
        input.RightIrX = 1_010;
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out var active));

        Assert.AreEqual(0, active.WheelDelta);
        Assert.AreEqual(0, active.HorizontalWheelDelta);
    }

    [TestMethod]
    public void DisabledInvalidAndUnavailableSourcesFailClosedAndReset()
    {
        Switch2IrMouseProfileLaneState state = default;
        Switch2JoyConRawInputStatus input = Right(0, x: 100);
        Assert.IsTrue(Advance(input, Switch2IrMouseSource.Right, ref state,
            out _));

        Assert.IsFalse(Switch2IrMouseProfileLane.TryAdvance(input,
            enabled: false, Switch2IrMouseSource.Right,
            Switch2IrActivationThreshold.Balanced,
            Switch2IrMouseProjection.DefaultSensitivity,
            Switch2IrActivationThreshold.Balanced,
            Switch2IrMouseProjection.DefaultSensitivity,
            Switch2IrMouseScrollMode.Vertical, profileRevision: 1,
            ref state, out _));
        Assert.IsFalse(state.HasLifecycle);

        Assert.IsFalse(Advance(input, Switch2IrMouseSource.Left, ref state,
            out _));
        input.IsValid = false;
        Assert.IsFalse(Advance(input, Switch2IrMouseSource.Right, ref state,
            out _));

        input.IsValid = true;
        Assert.IsFalse(Switch2IrMouseProfileLane.TryAdvance(input,
            enabled: true, Switch2IrMouseSource.Right,
            Switch2IrActivationThreshold.Strict,
            Switch2IrMouseProjection.DefaultSensitivity,
            Switch2IrActivationThreshold.Strict,
            rightSensitivity: 0.99,
            scrollMode: Switch2IrMouseScrollMode.Vertical,
            profileRevision: 1, ref state, out _));
        Assert.IsFalse(Switch2IrMouseProfileLane.TryAdvance(input,
            enabled: true, Switch2IrMouseSource.Right,
            Switch2IrActivationThreshold.Strict,
            Switch2IrMouseProjection.DefaultSensitivity,
            Switch2IrActivationThreshold.Strict,
            Switch2IrMouseProjection.DefaultSensitivity,
            scrollMode: (Switch2IrMouseScrollMode)99,
            profileRevision: 1, ref state, out _));
        Assert.IsFalse(Switch2IrMouseProfileLane.TryAdvance(input,
            enabled: true, Switch2IrMouseSource.Right,
            Switch2IrActivationThreshold.Strict,
            Switch2IrMouseProjection.DefaultSensitivity,
            Switch2IrActivationThreshold.Strict, rightSensitivity: 10.01,
            scrollMode: Switch2IrMouseScrollMode.Vertical,
            profileRevision: 1, ref state, out _));
    }

    [TestMethod]
    public void WarmProfileLaneAllocatesNothing()
    {
        Switch2IrMouseProfileLaneState state = default;
        Switch2JoyConRawInputStatus input = Right(0, x: 100);
        bool succeeded = true;
        for (int index = 0; index < 2_000; index++)
        {
            input.CompletionTimestampQpc = index * 1_000L;
            input.RightIrX = (ushort)index;
            succeeded &= Advance(input, Switch2IrMouseSource.Right,
                ref state, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            input.CompletionTimestampQpc = 2_000_000L + index * 1_000L;
            input.RightIrX = (ushort)index;
            succeeded &= Advance(input, Switch2IrMouseSource.Right,
                ref state, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    private static bool Advance(in Switch2JoyConRawInputStatus input,
        Switch2IrMouseSource source,
        ref Switch2IrMouseProfileLaneState state,
        out Switch2IrMouseProjectionResult result,
        long profileRevision = 1,
        Switch2IrMouseScrollMode scrollMode =
            Switch2IrMouseScrollMode.Vertical) =>
        Switch2IrMouseProfileLane.TryAdvance(input, enabled: true, source,
            Switch2IrActivationThreshold.Balanced,
            Switch2IrMouseProjection.DefaultSensitivity,
            Switch2IrActivationThreshold.Balanced,
            Switch2IrMouseProjection.DefaultSensitivity, scrollMode,
            profileRevision,
            ref state,
            out result);

    private static Switch2JoyConRawInputStatus Right(long timestamp, ushort x,
        ulong deviceGeneration = 1, ulong transportGeneration = 1) => new()
    {
        IsValid = true,
        ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
        Mode = Switch2JoyConProfileMode.StandaloneHorizontalRight,
        CompletionTimestampQpc = timestamp,
        QpcFrequency = Frequency,
        RightPresent = true,
        RightDeviceGeneration = deviceGeneration,
        RightTransportGeneration = transportGeneration,
        RightHasCommonMotion = true,
        RightIrX = x,
        RightIrY = 100,
        RightIrRoughness = 100,
        RightIrDistance = 100,
    };

    private static Switch2JoyConRawInputStatus Left(long timestamp, ushort x,
        ulong deviceGeneration = 1, ulong transportGeneration = 1) => new()
    {
        IsValid = true,
        ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
        Mode = Switch2JoyConProfileMode.StandaloneHorizontalLeft,
        CompletionTimestampQpc = timestamp,
        QpcFrequency = Frequency,
        LeftPresent = true,
        LeftDeviceGeneration = deviceGeneration,
        LeftTransportGeneration = transportGeneration,
        LeftHasCommonMotion = true,
        LeftIrX = x,
        LeftIrY = 100,
        LeftIrRoughness = 100,
        LeftIrDistance = 100,
    };

    private static Switch2JoyConRawInputStatus Joined(long timestamp,
        ushort leftX, ushort rightX) => new()
    {
        IsValid = true,
        ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
        Mode = Switch2JoyConProfileMode.Joined,
        PairEpoch = 1,
        CompletionTimestampQpc = timestamp,
        QpcFrequency = Frequency,
        LeftPresent = true,
        LeftDeviceGeneration = 1,
        LeftTransportGeneration = 1,
        LeftHasCommonMotion = true,
        LeftIrX = leftX,
        LeftIrY = 100,
        LeftIrRoughness = 100,
        LeftIrDistance = 100,
        RightPresent = true,
        RightDeviceGeneration = 2,
        RightTransportGeneration = 2,
        RightHasCommonMotion = true,
        RightIrX = rightX,
        RightIrY = 100,
        RightIrRoughness = 100,
        RightIrDistance = 100,
    };
}
