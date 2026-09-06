using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2IrMouseProjectionTests
{
    [TestMethod]
    public void SourcePinnedThresholdBandsAreStrictAtTheirBounds()
    {
        Assert.IsFalse(Switch2IrMouseProjection.IsThresholdActive(
            Switch2IrActivationThreshold.Strict, 0, 0));
        Assert.IsTrue(Switch2IrMouseProjection.IsThresholdActive(
            Switch2IrActivationThreshold.Strict, 3_999, 999));
        Assert.IsFalse(Switch2IrMouseProjection.IsThresholdActive(
            Switch2IrActivationThreshold.Strict, 4_000, 999));
        Assert.IsFalse(Switch2IrMouseProjection.IsThresholdActive(
            Switch2IrActivationThreshold.Strict, 3_999, 1_000));

        Assert.IsTrue(Switch2IrMouseProjection.IsThresholdActive(
            Switch2IrActivationThreshold.Balanced, 4_999, 1_499));
        Assert.IsFalse(Switch2IrMouseProjection.IsThresholdActive(
            Switch2IrActivationThreshold.Balanced, 5_000, 1_499));
        Assert.IsFalse(Switch2IrMouseProjection.IsThresholdActive(
            Switch2IrActivationThreshold.Balanced, 4_999, 1_500));

        Assert.IsTrue(Switch2IrMouseProjection.IsThresholdActive(
            Switch2IrActivationThreshold.Relaxed, 9_999, 2_999));
        Assert.IsFalse(Switch2IrMouseProjection.IsThresholdActive(
            Switch2IrActivationThreshold.Relaxed, 10_000, 2_999));
        Assert.IsFalse(Switch2IrMouseProjection.IsThresholdActive(
            Switch2IrActivationThreshold.Relaxed, 9_999, 3_000));
    }

    [TestMethod]
    public void FreeWindowEmitsTheDisplacementThatActivatedIt()
    {
        Switch2IrMouseProjectionState state = default;
        Assert.IsTrue(Advance(65_530, 100, 0, ref state, out var first));
        Assert.IsFalse(first.ModeActive);

        Assert.IsTrue(Advance(4, 80, 1_000, ref state, out var moved));

        Assert.IsTrue(moved.ThresholdActive);
        Assert.IsTrue(moved.ModeActive);
        Assert.AreEqual(10, moved.DeltaX);
        Assert.AreEqual(-20, moved.DeltaY);
        Assert.AreEqual(0.36, moved.VelocityX, 0.000001);
        Assert.AreEqual(-0.72, moved.VelocityY, 0.000001);
    }

    [TestMethod]
    public void ThresholdLossResetsBeforeTheNextActivation()
    {
        Switch2IrMouseProjectionState state = default;
        Assert.IsTrue(Advance(100, 100, 0, ref state, out _));
        Assert.IsTrue(Advance(120, 120, 1_000, ref state, out var active));
        Assert.IsTrue(active.ModeActive);

        Assert.IsTrue(Switch2IrMouseProjection.TryAdvance(true,
            Switch2IrActivationThreshold.Strict, 130, 130, roughness: 0,
            distance: 0, nowMicroseconds: 2_000,
            Switch2IrMouseProjection.DefaultSensitivity, ref state,
            out var released));
        Assert.IsFalse(released.ThresholdActive);
        Assert.IsFalse(released.ModeActive);
        Assert.AreEqual(0, released.DeltaX);
        Assert.AreEqual(0, released.DeltaY);

        Assert.IsTrue(Advance(140, 140, 3_000, ref state, out var rearmed));
        Assert.IsFalse(rearmed.ModeActive);
    }

    [TestMethod]
    public void InvalidConfigurationFailsClosedAndClearsState()
    {
        Switch2IrMouseProjectionState state = default;
        Assert.IsTrue(Advance(100, 100, 0, ref state, out _));
        Assert.IsTrue(Advance(120, 120, 1_000, ref state, out _));

        Assert.IsFalse(Switch2IrMouseProjection.TryAdvance(true,
            (Switch2IrActivationThreshold)4, 130, 130, 0, 1,
            2_000, Switch2IrMouseProjection.DefaultSensitivity, ref state,
            out _));
        Assert.IsFalse(state.Activation.HasPreviousCoordinates);

        Assert.IsFalse(Switch2IrMouseProjection.TryAdvance(true,
            Switch2IrActivationThreshold.Strict, 130, 130, 0, 1,
            2_000, double.NaN, ref state, out _));
    }

    [TestMethod]
    public void WarmProjectionPathAllocatesNothing()
    {
        Switch2IrMouseProjectionState state = default;
        bool succeeded = true;
        for (int index = 0; index < 2_000; index++)
        {
            succeeded &= Advance((ushort)index, (ushort)(index * 3),
                index * 1_000L, ref state, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            succeeded &= Advance((ushort)index, (ushort)(index * 3),
                2_000_000L + index * 1_000L, ref state, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    private static bool Advance(ushort x, ushort y, long now,
        ref Switch2IrMouseProjectionState state,
        out Switch2IrMouseProjectionResult result) =>
        Switch2IrMouseProjection.TryAdvance(true,
            Switch2IrActivationThreshold.Strict, x, y, roughness: 100,
            distance: 100, now,
            Switch2IrMouseProjection.DefaultSensitivity, ref state,
            out result);
}
