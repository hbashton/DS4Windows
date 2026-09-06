using System.Numerics;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2DualJoyConGyroFusionTests
{
    [TestMethod]
    public void SameDirectionSubContributionRampsAndIsCapped()
    {
        Switch2DualJoyConGyroFusionState state = default;
        Switch2JoyConMotionSample left = Sample(
            new Vector3(40.0f, 100.0f, 100.0f), Vector3.One, true);
        Switch2JoyConMotionSample right = Sample(
            new Vector3(20.0f, 500.0f, -500.0f), Vector3.One, true);

        Assert.IsTrue(Switch2DualJoyConGyroFusion.TryFuse(left, right,
            Switch2DualGyroDominantSide.Left, Vector3.Zero, ref state,
            out Switch2DualJoyConGyroFusionResult result));

        float expectedScale = (MathF.Sqrt(21_600.0f) - 30.0f) / 30.0f;
        expectedScale = MathF.Min(1.0f, expectedScale);
        Assert.AreEqual(expectedScale, result.SubContributionScale,
            0.0001f);
        Assert.AreEqual(60.0f, result.Gyroscope.X, 0.0001f);
        Assert.AreEqual(200.0f, result.Gyroscope.Y, 0.0001f);
        Assert.AreEqual(100.0f, result.Gyroscope.Z, 0.0001f);
        Assert.AreEqual(Switch2JoyConSide.Left, result.OutputOwner);
    }

    [TestMethod]
    public void SubContributionIsZeroBelowDominantMagnitudeThreshold()
    {
        Switch2DualJoyConGyroFusionState state = default;
        Switch2JoyConMotionSample left = Sample(
            new Vector3(29.0f, 0.0f, 0.0f), Vector3.One, true);
        Switch2JoyConMotionSample right = Sample(
            new Vector3(20.0f, 0.0f, 0.0f), Vector3.One, true);

        Assert.IsTrue(Switch2DualJoyConGyroFusion.TryFuse(left, right,
            Switch2DualGyroDominantSide.Left, Vector3.Zero, ref state,
            out Switch2DualJoyConGyroFusionResult result));

        Assert.AreEqual(0.0f, result.SubContributionScale);
        Assert.AreEqual(29.0f, result.Gyroscope.X);
    }

    [TestMethod]
    public void DirectModeSumsGyrosAndAveragesNonzeroAccelerometers()
    {
        Switch2DualJoyConGyroFusionState state = default;
        Switch2JoyConMotionSample left = Sample(new Vector3(1, 2, 3),
            new Vector3(2, 4, 6), true);
        Switch2JoyConMotionSample right = Sample(new Vector3(4, 5, 6),
            new Vector3(6, 8, 10), true);

        Assert.IsTrue(Switch2DualJoyConGyroFusion.TryFuse(left, right,
            Switch2DualGyroDominantSide.None, Vector3.Zero, ref state,
            out Switch2DualJoyConGyroFusionResult result));

        Assert.AreEqual(new Vector3(5, 7, 9), result.Gyroscope);
        Assert.AreEqual(new Vector3(4, 6, 8), result.Accelerometer);
        Assert.AreEqual(Switch2JoyConSide.Left, result.OutputOwner);
    }

    [TestMethod]
    public void DirectModeSubtractsEachPhysicalImuBiasBeforeMerge()
    {
        Switch2DualJoyConGyroFusionState state = default;
        Switch2JoyConMotionSample left = new(new Vector3(11, 22, 33),
            Vector3.One, new Vector3(1, 2, 3), active: true);
        Switch2JoyConMotionSample right = new(new Vector3(44, 55, 66),
            Vector3.One, new Vector3(4, 5, 6), active: true);

        Assert.IsTrue(Switch2DualJoyConGyroFusion.TryFuse(left, right,
            Switch2DualGyroDominantSide.None, Vector3.Zero, ref state,
            out Switch2DualJoyConGyroFusionResult result));

        Assert.AreEqual(new Vector3(50, 70, 90), result.Gyroscope);
    }

    [TestMethod]
    public void DominantFailoverPreservesSoleOutputOwnerAndTracksOffset()
    {
        Switch2DualJoyConGyroFusionState state = default;
        Switch2JoyConMotionSample left = Sample(new Vector3(40, 0, 0),
            new Vector3(10, 0, 0), true);
        Switch2JoyConMotionSample right = Sample(new Vector3(20, 0, 0),
            new Vector3(4, 0, 0), true);
        Assert.IsTrue(Switch2DualJoyConGyroFusion.TryFuse(left, right,
            Switch2DualGyroDominantSide.Left, Vector3.Zero, ref state,
            out _));

        left = Sample(new Vector3(40, 0, 0), new Vector3(10, 0, 0),
            false);
        Assert.IsTrue(Switch2DualJoyConGyroFusion.TryFuse(left, right,
            Switch2DualGyroDominantSide.Left, Vector3.Zero, ref state,
            out Switch2DualJoyConGyroFusionResult result));

        Assert.AreEqual(new Vector3(20, 0, 0), result.Gyroscope);
        Assert.AreEqual(new Vector3(4, 0, 0), result.Accelerometer);
        Assert.AreEqual(Switch2JoyConSide.Left, result.OutputOwner);
        Assert.AreEqual(5.94f, state.AccelerometerOffset.X, 0.0001f);
    }

    [TestMethod]
    public void NonFiniteInputIsRejectedWithoutChangingState()
    {
        Switch2DualJoyConGyroFusionState state = new()
        {
            AccelerometerOffset = new Vector3(1, 2, 3),
        };
        Switch2DualJoyConGyroFusionState initial = state;
        Switch2JoyConMotionSample left = Sample(
            new Vector3(float.NaN, 0, 0), Vector3.One, true);
        Switch2JoyConMotionSample right = Sample(Vector3.Zero,
            Vector3.One, true);

        Assert.IsFalse(Switch2DualJoyConGyroFusion.TryFuse(left, right,
            Switch2DualGyroDominantSide.Left, Vector3.Zero, ref state,
            out _));
        Assert.AreEqual(initial.AccelerometerOffset,
            state.AccelerometerOffset);
        Assert.IsFalse(state.HasPreviousActivation);
    }

    private static Switch2JoyConMotionSample Sample(Vector3 gyro,
        Vector3 acceleration, bool active) => new(gyro, acceleration,
            Vector3.Zero, active);
}
