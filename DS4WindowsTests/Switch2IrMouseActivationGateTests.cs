using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2IrMouseActivationGateTests
{
    [TestMethod]
    public void ReleasedThresholdResetsAllActivationState()
    {
        Switch2IrMouseActivationState state = default;
        Advance(true, 100, 100, 0, ref state, out _, out _, out _, out _);
        Advance(true, 110, 100, 1_000, ref state, out _, out _, out _,
            out _);
        Assert.IsTrue(state.ModeActive);

        Advance(false, 110, 100, 2_000, ref state, out bool hasOrigin,
            out _, out bool hasEvent, out _);

        Assert.AreEqual(default, state);
        Assert.IsFalse(hasOrigin);
        Assert.IsFalse(hasEvent);
    }

    [TestMethod]
    public void CircularCoordinatesUseShortestSignedDelta()
    {
        Assert.AreEqual(2,
            Switch2IrMouseActivationGate.LoopingDifference16Bit(65_535, 1));
        Assert.AreEqual(-2,
            Switch2IrMouseActivationGate.LoopingDifference16Bit(1, 65_535));
    }

    [TestMethod]
    public void FirstFreePhaseDisplacementPublishesPreviousOrigin()
    {
        Switch2IrMouseActivationState state = default;
        Advance(true, 100, 200, 0, ref state, out _, out _, out _, out _);

        Advance(true, 105, 207, 1_000, ref state, out bool hasOrigin,
            out Switch2IrCoordinate origin, out bool hasEvent, out _);

        Assert.IsTrue(hasOrigin);
        Assert.AreEqual((ushort)100, origin.X);
        Assert.AreEqual((ushort)200, origin.Y);
        Assert.IsTrue(state.ModeActive);
        Assert.IsFalse(hasEvent);
    }

    [TestMethod]
    public void InsufficientVerificationWindowIsRejectedAndRestarted()
    {
        Switch2IrMouseActivationState state = default;
        Advance(true, 0, 0, 0, ref state, out _, out _, out _, out _);
        Advance(true, 0, 0, 600_000, ref state, out _, out _, out _,
            out _);

        Advance(true, 0, 0, 900_000, ref state, out _, out _,
            out bool hasEvent, out Switch2IrVerificationEvent result);

        Assert.IsTrue(hasEvent);
        Assert.AreEqual(Switch2IrVerificationResult.Reject, result.Result);
        Assert.AreEqual(Switch2IrVerificationReason.InsufficientMotion,
            result.Reason);
        Assert.IsTrue(state.HasWindowSince);
        Assert.AreEqual(900_000, state.WindowSinceMicroseconds);
        Assert.AreEqual(0, state.QualifiedStreak);
    }

    [TestMethod]
    public void TwoQualifiedWindowsLatchSustainedMotion()
    {
        Switch2IrMouseActivationState state = default;
        Advance(true, 0, 0, 0, ref state, out _, out _, out _, out _);
        StepMotionWindow(ref state, 600_000, 0);
        Assert.IsFalse(state.Latched);
        Assert.AreEqual(1, state.QualifiedStreak);

        StepMotionWindow(ref state, 900_000, 56);

        Assert.IsTrue(state.Latched);
        Assert.IsTrue(state.ModeActive);
    }

    [TestMethod]
    public void SingleFastWindowLatchesAndRemainsStable()
    {
        Switch2IrMouseActivationState state = default;
        Advance(true, 0, 0, 0, ref state, out _, out _, out _, out _);
        Advance(true, 50, 0, 600_000, ref state, out _, out _, out _,
            out _);
        Advance(true, 100, 0, 700_000, ref state, out _, out _, out _,
            out _);
        Advance(true, 150, 0, 800_000, ref state, out _, out _, out _,
            out _);
        Advance(true, 300, 0, 900_000, ref state, out _, out _,
            out bool hasEvent, out Switch2IrVerificationEvent result);

        Assert.IsTrue(hasEvent);
        Assert.AreEqual(Switch2IrVerificationResult.Latch, result.Result);
        Assert.AreEqual(Switch2IrVerificationReason.FastMotion,
            result.Reason);
        Assert.IsTrue(state.Latched);

        Advance(true, 300, 0, 1_000_000, ref state, out bool hasOrigin,
            out _, out hasEvent, out _);
        Assert.IsTrue(state.Latched);
        Assert.IsTrue(state.ModeActive);
        Assert.IsFalse(hasOrigin);
        Assert.IsFalse(hasEvent);
    }

    private static void StepMotionWindow(
        ref Switch2IrMouseActivationState state, long start,
        ushort startCoordinate)
    {
        ushort[] offsets = { 8, 16, 24, 32, 40, 56 };
        long[] elapsed = { 0, 50_000, 100_000, 150_000, 200_000,
            300_000 };
        Switch2IrVerificationEvent result = default;
        bool hasEvent = false;
        for (int i = 0; i < offsets.Length; i++)
        {
            Advance(true, (ushort)(startCoordinate + offsets[i]), 0,
                start + elapsed[i], ref state, out _, out _, out hasEvent,
                out result);
        }

        Assert.IsTrue(hasEvent);
        if (!state.Latched)
        {
            Assert.AreEqual(Switch2IrVerificationResult.Continue,
                result.Result);
            Assert.AreEqual(Switch2IrVerificationReason.Qualified,
                result.Reason);
        }
        else
        {
            Assert.AreEqual(Switch2IrVerificationResult.Latch,
                result.Result);
            Assert.AreEqual(Switch2IrVerificationReason.SustainedMotion,
                result.Reason);
        }
    }

    private static void Advance(bool thresholdActive, ushort x, ushort y,
        long now, ref Switch2IrMouseActivationState state,
        out bool hasOrigin, out Switch2IrCoordinate origin,
        out bool hasEvent, out Switch2IrVerificationEvent result)
    {
        Switch2IrCoordinate coordinate = new(x, y);
        Switch2IrMouseActivationGate.Advance(thresholdActive, coordinate,
            now, ref state, out hasOrigin, out origin, out hasEvent,
            out result);
    }
}
