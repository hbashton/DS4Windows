using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2StickDirectionTapLaneTests
{
    [TestMethod]
    public void TapPulseLastsEightyMillisecondsAndExpiresAtBoundary()
    {
        Switch2StickDirectionTapLaneState state = default;
        Switch2RawInputStatus pro = Pro(1_000);
        Assert.IsTrue(Advance(pro, 128, 128, 128, 128, AllTap(), 1,
            ref state, out Switch2StickDirectionTapFrame baseline));
        AssertOverride(baseline, DS4Controls.LYNeg, false);

        pro.CompletionTimestampQpc = 1_010;
        Assert.IsTrue(Advance(pro, 128, 0, 128, 128, AllTap(), 1,
            ref state, out Switch2StickDirectionTapFrame pressed));
        AssertOverride(pressed, DS4Controls.LYNeg, true);

        pro.CompletionTimestampQpc = 1_089;
        Advance(pro, 128, 0, 128, 128, AllTap(), 1, ref state,
            out Switch2StickDirectionTapFrame held);
        AssertOverride(held, DS4Controls.LYNeg, true);

        pro.CompletionTimestampQpc = 1_090;
        Advance(pro, 128, 0, 128, 128, AllTap(), 1, ref state,
            out Switch2StickDirectionTapFrame expired);
        AssertOverride(expired, DS4Controls.LYNeg, false);
    }

    [TestMethod]
    public void CardinalToDiagonalPulsesOnlyNewDirection()
    {
        Switch2StickDirectionTapLaneState state = default;
        Switch2RawInputStatus pro = Pro(2_000);
        Advance(pro, 128, 128, 128, 128, AllTap(), 1, ref state, out _);

        pro.CompletionTimestampQpc = 2_100;
        Advance(pro, 128, 0, 128, 128, AllTap(), 1, ref state,
            out Switch2StickDirectionTapFrame up);
        AssertOverride(up, DS4Controls.LYNeg, true);

        // Let Up expire before changing sectors so the transition assertion
        // observes only the direction newly introduced by the diagonal.
        pro.CompletionTimestampQpc = 2_200;
        Advance(pro, 255, 0, 128, 128, AllTap(), 1, ref state,
            out Switch2StickDirectionTapFrame diagonal);
        AssertOverride(diagonal, DS4Controls.LYNeg, false);
        AssertOverride(diagonal, DS4Controls.LXPos, true);
    }

    [TestMethod]
    public void DiagonalToCardinalSuppressesPreviouslyTriggeredDirection()
    {
        Switch2StickDirectionTapLaneState state = default;
        Switch2RawInputStatus pro = Pro(3_000);
        Advance(pro, 128, 128, 128, 128, AllTap(), 1, ref state, out _);

        pro.CompletionTimestampQpc = 3_100;
        Advance(pro, 255, 0, 128, 128, AllTap(), 1, ref state,
            out Switch2StickDirectionTapFrame diagonal);
        AssertOverride(diagonal, DS4Controls.LYNeg, true);
        AssertOverride(diagonal, DS4Controls.LXPos, true);

        pro.CompletionTimestampQpc = 3_200;
        Advance(pro, 255, 128, 128, 128, AllTap(), 1, ref state,
            out Switch2StickDirectionTapFrame right);
        AssertOverride(right, DS4Controls.LXPos, false);
    }

    [TestMethod]
    public void CenterRearmsAndLeftRightModesAreIndependent()
    {
        Switch2StickDirectionTapLaneState state = default;
        Switch2RawInputStatus pro = Pro(4_000);
        Switch2StickDirectionActivationModes modes = new(
            leftUp: Switch2StickDirectionActivationMode.Hold,
            leftDown: Switch2StickDirectionActivationMode.Hold,
            leftLeft: Switch2StickDirectionActivationMode.Hold,
            leftRight: Switch2StickDirectionActivationMode.Hold,
            rightUp: Switch2StickDirectionActivationMode.Tap,
            rightDown: Switch2StickDirectionActivationMode.Hold,
            rightLeft: Switch2StickDirectionActivationMode.Hold,
            rightRight: Switch2StickDirectionActivationMode.Hold);
        Advance(pro, 128, 128, 128, 128, modes, 1, ref state, out _);

        pro.CompletionTimestampQpc = 4_100;
        Advance(pro, 128, 0, 128, 0, modes, 1, ref state,
            out Switch2StickDirectionTapFrame first);
        Assert.IsFalse(first.TryOverride(DS4Controls.LYNeg, out _));
        AssertOverride(first, DS4Controls.RYNeg, true);

        pro.CompletionTimestampQpc = 4_200;
        Advance(pro, 128, 128, 128, 128, modes, 1, ref state, out _);
        pro.CompletionTimestampQpc = 4_210;
        Advance(pro, 128, 128, 128, 0, modes, 1, ref state,
            out Switch2StickDirectionTapFrame rearmed);
        AssertOverride(rearmed, DS4Controls.RYNeg, true);
    }

    [TestMethod]
    public void LifetimeProfileModeAndTimestampChangesBaselineHeldInput()
    {
        Switch2StickDirectionTapLaneState state = default;
        Switch2RawInputStatus pro = Pro(5_000);
        AssertBaseline(pro, AllTap(), 1, ref state);

        pro.CompletionTimestampQpc = 5_100;
        AssertBaseline(pro, AllTap(), 2, ref state);

        pro.DeviceGeneration++;
        pro.CompletionTimestampQpc = 5_200;
        AssertBaseline(pro, AllTap(), 2, ref state);

        pro.CompletionTimestampQpc = 5_000;
        AssertBaseline(pro, AllTap(), 2, ref state);

        Switch2StickDirectionActivationModes invalid = new(
            (Switch2StickDirectionActivationMode)99,
            Switch2StickDirectionActivationMode.Hold,
            Switch2StickDirectionActivationMode.Hold,
            Switch2StickDirectionActivationMode.Hold,
            Switch2StickDirectionActivationMode.Hold,
            Switch2StickDirectionActivationMode.Hold,
            Switch2StickDirectionActivationMode.Hold,
            Switch2StickDirectionActivationMode.Hold);
        pro.CompletionTimestampQpc = 5_300;
        Assert.IsFalse(Advance(pro, 128, 0, 128, 128, invalid, 2,
            ref state, out Switch2StickDirectionTapFrame invalidFrame));
        Assert.IsFalse(invalidFrame.IsValid);
        Assert.IsFalse(Advance(default, 128, 0, 128, 128, AllTap(), 2,
            ref state, out _));
    }

    [TestMethod]
    public void WarmPathDoesNotAllocate()
    {
        Switch2StickDirectionTapLaneState state = default;
        Switch2RawInputStatus pro = Pro(6_000);
        Switch2StickDirectionActivationModes modes = AllTap();
        Advance(pro, 128, 128, 128, 128, modes, 1, ref state, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int index = 0; index < 20_000; index++)
        {
            pro.CompletionTimestampQpc++;
            byte axis = (byte)(index % 3 switch
            {
                0 => 0,
                1 => 128,
                _ => 255,
            });
            succeeded &= Advance(pro, axis, axis, (byte)(255 - axis),
                axis, modes, 1, ref state,
                out Switch2StickDirectionTapFrame frame);
            frame.TryOverride(DS4Controls.LXNeg, out _);
            frame.TryOverride(DS4Controls.RYPos, out _);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, after - before);
    }

    private static void AssertBaseline(Switch2RawInputStatus pro,
        in Switch2StickDirectionActivationModes modes, long profileRevision,
        ref Switch2StickDirectionTapLaneState state)
    {
        Assert.IsTrue(Advance(pro, 128, 0, 128, 128, modes,
            profileRevision, ref state,
            out Switch2StickDirectionTapFrame frame));
        AssertOverride(frame, DS4Controls.LYNeg, false);
    }

    private static void AssertOverride(
        in Switch2StickDirectionTapFrame frame, DS4Controls control,
        bool expected)
    {
        Assert.IsTrue(frame.TryOverride(control, out bool active));
        Assert.AreEqual(expected, active);
    }

    private static Switch2StickDirectionActivationModes AllTap() => new(
        Switch2StickDirectionActivationMode.Tap,
        Switch2StickDirectionActivationMode.Tap,
        Switch2StickDirectionActivationMode.Tap,
        Switch2StickDirectionActivationMode.Tap,
        Switch2StickDirectionActivationMode.Tap,
        Switch2StickDirectionActivationMode.Tap,
        Switch2StickDirectionActivationMode.Tap,
        Switch2StickDirectionActivationMode.Tap);

    private static bool Advance(in Switch2RawInputStatus pro, byte leftX,
        byte leftY, byte rightX, byte rightY,
        in Switch2StickDirectionActivationModes modes, long profileRevision,
        ref Switch2StickDirectionTapLaneState state,
        out Switch2StickDirectionTapFrame frame) =>
        Switch2StickDirectionTapLane.TryAdvance(pro, default, leftX, leftY,
            rightX, rightY, modes, profileRevision, ref state, out frame);

    private static Switch2RawInputStatus Pro(long timestamp) => new()
    {
        IsValid = true,
        ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
        DeviceGeneration = 21,
        TransportGeneration = 22,
        CompletionTimestampQpc = timestamp,
        QpcFrequency = 1_000,
    };
}
