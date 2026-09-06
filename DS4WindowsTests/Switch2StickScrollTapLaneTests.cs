using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2StickScrollTapLaneTests
{
    [TestMethod]
    public void TapEmitsOncePerSectorAndReEmitsEveryDiagonalDirection()
    {
        Switch2StickScrollTapLaneState state = default;
        Switch2RawInputStatus pro = Pro(timestamp: 1_000);

        Assert.IsTrue(Advance(pro, 128, 128, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state,
            out Switch2StickScrollTapFrame baseline));
        AssertHandledWithoutEmission(baseline, DS4Controls.LYNeg);

        pro.CompletionTimestampQpc = 1_030;
        Assert.IsTrue(Advance(pro, 128, 0, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state,
            out Switch2StickScrollTapFrame up));
        AssertEmission(up, DS4Controls.LYNeg,
            Switch2StickScrollTapLane.MaximumTapStep);
        AssertHandledWithoutEmission(up, DS4Controls.LXPos);

        pro.CompletionTimestampQpc = 1_060;
        Assert.IsTrue(Advance(pro, 128, 0, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state,
            out Switch2StickScrollTapFrame held));
        AssertHandledWithoutEmission(held, DS4Controls.LYNeg);

        pro.CompletionTimestampQpc = 1_090;
        Assert.IsTrue(Advance(pro, 255, 0, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state,
            out Switch2StickScrollTapFrame diagonal));
        AssertEmission(diagonal, DS4Controls.LYNeg,
            Switch2StickScrollTapLane.MaximumTapStep);
        AssertEmission(diagonal, DS4Controls.LXPos,
            Switch2StickScrollTapLane.MaximumTapStep);

        pro.CompletionTimestampQpc = 1_120;
        Assert.IsTrue(Advance(pro, 128, 128, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state, out _));
        pro.CompletionTimestampQpc = 1_150;
        Assert.IsTrue(Advance(pro, 128, 0, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state,
            out Switch2StickScrollTapFrame rearmed));
        AssertEmission(rearmed, DS4Controls.LYNeg,
            Switch2StickScrollTapLane.MaximumTapStep);
    }

    [TestMethod]
    public void CenterDeadzoneAndThirtyMillisecondThrottleMatchDonorPolicy()
    {
        Switch2StickScrollTapLaneState state = default;
        Switch2RawInputStatus pro = Pro(timestamp: 2_000);
        Advance(pro, 128, 128, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state, out _);

        pro.CompletionTimestampQpc = 2_030;
        Advance(pro, 131, 128, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state,
            out Switch2StickScrollTapFrame jitter);
        AssertHandledWithoutEmission(jitter, DS4Controls.LXPos);

        pro.CompletionTimestampQpc = 2_060;
        Advance(pro, 132, 128, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state,
            out Switch2StickScrollTapFrame edge);
        AssertEmission(edge, DS4Controls.LXPos,
            Switch2StickScrollTapLane.MinimumTapStep);

        // Like Switch2Connect, reports inside the per-stick 30 ms throttle
        // window do not mutate the armed sector.
        pro.CompletionTimestampQpc = 2_070;
        Advance(pro, 128, 128, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state, out _);
        pro.CompletionTimestampQpc = 2_080;
        Advance(pro, 255, 128, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state,
            out Switch2StickScrollTapFrame throttled);
        AssertHandledWithoutEmission(throttled, DS4Controls.LXPos);
        pro.CompletionTimestampQpc = 2_090;
        Advance(pro, 255, 128, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state,
            out Switch2StickScrollTapFrame stillArmed);
        AssertHandledWithoutEmission(stillArmed, DS4Controls.LXPos);

        pro.CompletionTimestampQpc = 2_120;
        Advance(pro, 128, 128, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state, out _);
        pro.CompletionTimestampQpc = 2_150;
        Advance(pro, 255, 128, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 1, ref state,
            out Switch2StickScrollTapFrame rearmed);
        AssertEmission(rearmed, DS4Controls.LXPos,
            Switch2StickScrollTapLane.MaximumTapStep);
    }

    [TestMethod]
    public void LeftAndRightSticksHaveIndependentThrottleAndActivationModes()
    {
        Switch2StickScrollTapLaneState state = default;
        Switch2RawInputStatus pro = Pro(timestamp: 3_000);
        Advance(pro, 128, 128, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Tap, 1, ref state, out _);

        pro.CompletionTimestampQpc = 3_030;
        Advance(pro, 128, 0, 128, 255,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Tap, 1, ref state,
            out Switch2StickScrollTapFrame both);
        AssertEmission(both, DS4Controls.LYNeg,
            Switch2StickScrollTapLane.MaximumTapStep);
        AssertEmission(both, DS4Controls.RYPos,
            Switch2StickScrollTapLane.MaximumTapStep);

        pro.CompletionTimestampQpc = 3_060;
        Advance(pro, 0, 128, 255, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Tap, 1, ref state,
            out Switch2StickScrollTapFrame independent);
        AssertEmission(independent, DS4Controls.LXNeg,
            Switch2StickScrollTapLane.MaximumTapStep);
        AssertEmission(independent, DS4Controls.RXPos,
            Switch2StickScrollTapLane.MaximumTapStep);

        pro.CompletionTimestampQpc = 3_090;
        Advance(pro, 0, 128, 255, 128,
            Switch2StickScrollActivationMode.Hold,
            Switch2StickScrollActivationMode.Tap, 1, ref state,
            out Switch2StickScrollTapFrame mixed);
        Assert.IsFalse(mixed.TryHandle(DS4Controls.LXNeg, out _, out _));
        AssertHandledWithoutEmission(mixed, DS4Controls.RXPos);
    }

    [TestMethod]
    public void ProfileLifetimeModeAndTimestampChangesBaselineHeldInput()
    {
        Switch2StickScrollTapLaneState state = default;
        Switch2RawInputStatus pro = Pro(timestamp: 4_000);
        AssertBaseline(pro, 1, ref state);

        pro.CompletionTimestampQpc = 4_030;
        AssertBaseline(pro, 2, ref state);

        pro.DeviceGeneration++;
        pro.CompletionTimestampQpc = 4_060;
        AssertBaseline(pro, 2, ref state);

        pro.CompletionTimestampQpc = 4_000;
        AssertBaseline(pro, 2, ref state);

        pro.CompletionTimestampQpc = 4_090;
        Assert.IsFalse(Advance(pro, 128, 0, 128, 128,
            (Switch2StickScrollActivationMode)99,
            Switch2StickScrollActivationMode.Hold, 2, ref state,
            out Switch2StickScrollTapFrame invalid));
        Assert.IsFalse(invalid.IsValid);

        pro.CompletionTimestampQpc = 4_120;
        AssertBaseline(pro, 2, ref state);
        Assert.IsFalse(Advance(default, 128, 0, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, 2, ref state, out _));
    }

    [TestMethod]
    public void CanonicalWheelResolverProducesSignedVerticalAndHorizontalSteps()
    {
        Switch2StickScrollTapFrame frame = new(true,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollSector.Up | Switch2StickScrollSector.Left,
            Switch2StickScrollSector.Down |
                Switch2StickScrollSector.Right,
            leftStep: 47, rightStep: 89);

        AssertResolved(frame, DS4Controls.LYNeg, X360Controls.WUP, 47, 0);
        AssertResolved(frame, DS4Controls.LXNeg, X360Controls.WLEFT, 0,
            -47);
        AssertResolved(frame, DS4Controls.RYPos, X360Controls.WDOWN, -89,
            0);
        AssertResolved(frame, DS4Controls.RXPos, X360Controls.WRIGHT, 0,
            89);

        Assert.IsTrue(Mapping.TryResolveSwitch2StickScrollTap(in frame,
            DS4Controls.LXPos, X360Controls.WRIGHT, out int vertical,
            out int horizontal));
        Assert.AreEqual(0, vertical);
        Assert.AreEqual(0, horizontal);
    }

    [TestMethod]
    public void WarmPathDoesNotAllocate()
    {
        Switch2StickScrollTapLaneState state = default;
        Switch2RawInputStatus pro = Pro(timestamp: 5_000);
        Advance(pro, 128, 128, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Tap, 1, ref state, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int index = 0; index < 20_000; index++)
        {
            pro.CompletionTimestampQpc += 30;
            succeeded &= Advance(pro, (byte)(index % 2 == 0 ? 0 : 255),
                128, 128, (byte)(index % 2 == 0 ? 0 : 255),
                Switch2StickScrollActivationMode.Tap,
                Switch2StickScrollActivationMode.Tap, 1, ref state,
                out Switch2StickScrollTapFrame frame);
            frame.TryHandle(DS4Controls.LXNeg, out _, out _);
            frame.TryHandle(DS4Controls.RYNeg, out _, out _);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, after - before);
    }

    private static void AssertBaseline(Switch2RawInputStatus pro,
        long profileRevision, ref Switch2StickScrollTapLaneState state)
    {
        Assert.IsTrue(Advance(pro, 128, 0, 128, 128,
            Switch2StickScrollActivationMode.Tap,
            Switch2StickScrollActivationMode.Hold, profileRevision,
            ref state, out Switch2StickScrollTapFrame frame));
        AssertHandledWithoutEmission(frame, DS4Controls.LYNeg);
    }

    private static void AssertHandledWithoutEmission(
        in Switch2StickScrollTapFrame frame, DS4Controls control)
    {
        Assert.IsTrue(frame.TryHandle(control, out bool emit, out _));
        Assert.IsFalse(emit);
    }

    private static void AssertEmission(in Switch2StickScrollTapFrame frame,
        DS4Controls control, int expectedStep)
    {
        Assert.IsTrue(frame.TryHandle(control, out bool emit, out int step));
        Assert.IsTrue(emit);
        Assert.AreEqual(expectedStep, step);
    }

    private static void AssertResolved(in Switch2StickScrollTapFrame frame,
        DS4Controls control, X360Controls output, int expectedVertical,
        int expectedHorizontal)
    {
        Assert.IsTrue(Mapping.TryResolveSwitch2StickScrollTap(in frame,
            control, output, out int vertical, out int horizontal));
        Assert.AreEqual(expectedVertical, vertical);
        Assert.AreEqual(expectedHorizontal, horizontal);
    }

    private static bool Advance(in Switch2RawInputStatus pro, byte leftX,
        byte leftY, byte rightX, byte rightY,
        Switch2StickScrollActivationMode leftMode,
        Switch2StickScrollActivationMode rightMode, long profileRevision,
        ref Switch2StickScrollTapLaneState state,
        out Switch2StickScrollTapFrame frame) =>
        Switch2StickScrollTapLane.TryAdvance(pro, default, leftX, leftY,
            rightX, rightY, leftMode, rightMode, profileRevision, ref state,
            out frame);

    private static Switch2RawInputStatus Pro(long timestamp) => new()
    {
        IsValid = true,
        ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
        DeviceGeneration = 11,
        TransportGeneration = 12,
        CompletionTimestampQpc = timestamp,
        QpcFrequency = 1_000,
    };
}
