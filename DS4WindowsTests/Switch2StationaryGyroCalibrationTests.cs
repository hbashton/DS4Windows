using System.Numerics;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2StationaryGyroCalibrationTests
{
    private const float GyroScale = 16.384f;
    private const float AccelerometerScale = 4096.0f;
    private const long QpcFrequency = 1_000_000;
    private const long StepQpc = 10_000;
    private static readonly Vector3 Gravity = new(0.0f, 4096.0f, 0.0f);

    [TestMethod]
    public void ContiguousStationaryIntervalCommitsAndSubtractsBias()
    {
        var calibration = new Switch2StationaryGyroCalibration();
        Vector3 bias = new(12.0f, -7.0f, 3.0f);
        Vector3 corrected = ObserveRange(calibration, bias, Gravity,
            firstIndex: 0, lastIndexInclusive: 501);

        Assert.IsTrue(calibration.HasCommittedBias);
        Assert.IsFalse(calibration.IsCalibrating);
        Assert.AreEqual(bias.X, calibration.Bias.X, 0.0001f);
        Assert.AreEqual(bias.Y, calibration.Bias.Y, 0.0001f);
        Assert.AreEqual(bias.Z, calibration.Bias.Z, 0.0001f);
        Assert.AreEqual(0.0f, corrected.Length(), 0.0001f);
    }

    [TestMethod]
    public void MotionBreaksContiguousStationaryQualification()
    {
        var calibration = new Switch2StationaryGyroCalibration();
        Vector3 bias = new(8.0f, -4.0f, 2.0f);
        ObserveRange(calibration, bias, Gravity, 0, 300);
        Assert.IsTrue(calibration.TryObserve(
            new Vector3(1000.0f, 0.0f, 0.0f), Gravity,
            GyroScale, AccelerometerScale, 301 * StepQpc, QpcFrequency,
            out _));
        ObserveRange(calibration, bias, Gravity, 302, 700);

        Assert.IsFalse(calibration.HasCommittedBias,
            "Only 3.99 seconds followed the motion discontinuity.");

        ObserveRange(calibration, bias, Gravity, 701, 803);
        Assert.IsTrue(calibration.HasCommittedBias);
    }

    [TestMethod]
    public void DuplicateJoinedHalfTimestampNeverAdvancesCalibration()
    {
        var calibration = new Switch2StationaryGyroCalibration();
        Vector3 bias = new(5.0f, 0.0f, 0.0f);
        Assert.IsTrue(calibration.TryObserve(bias, Gravity, GyroScale,
            AccelerometerScale, 1, QpcFrequency, out _));
        for (int index = 0; index < 10_000; index++)
        {
            Assert.IsTrue(calibration.TryObserve(bias, Gravity, GyroScale,
                AccelerometerScale, 1, QpcFrequency, out _));
        }

        Assert.IsFalse(calibration.HasCommittedBias);
        Assert.AreEqual(0L,
            calibration.CalibrationElapsedMilliseconds);
    }

    [TestMethod]
    public void ManualRestartPreservesBiasUntilReplacementCommits()
    {
        var calibration = new Switch2StationaryGyroCalibration();
        Vector3 original = new(4.0f, -2.0f, 1.0f);
        ObserveRange(calibration, original, Gravity, 0, 501);
        Assert.IsTrue(calibration.HasCommittedBias);

        calibration.RestartPreservingBias();
        Vector3 replacement = new(7.0f, -3.0f, 2.0f);
        Assert.IsTrue(calibration.TryObserve(replacement, Gravity,
            GyroScale, AccelerometerScale, 0, QpcFrequency,
            out Vector3 duringReplacement));
        Assert.AreEqual(replacement - original, duringReplacement);
        Assert.AreEqual(original, calibration.Bias);

        ObserveRange(calibration, replacement, Gravity, 1, 502);
        Assert.IsFalse(calibration.IsCalibrating);
        Assert.AreEqual(replacement.X, calibration.Bias.X, 0.0001f);
        Assert.AreEqual(replacement.Y, calibration.Bias.Y, 0.0001f);
        Assert.AreEqual(replacement.Z, calibration.Bias.Z, 0.0001f);
    }

    [TestMethod]
    public void PersistedDpsBiasAdoptsWithoutAutomaticRecalibration()
    {
        var calibration = new Switch2StationaryGyroCalibration();
        Vector3 biasDps = new(0.5f, -0.25f, 0.125f);

        Assert.IsTrue(calibration.TryAdoptBiasDps(biasDps, GyroScale));
        Assert.IsTrue(calibration.HasCommittedBias);
        Assert.IsFalse(calibration.IsCalibrating);
        Assert.AreEqual(1UL, calibration.BiasRevision);
        Assert.IsTrue(calibration.TryGetBiasDps(GyroScale,
            out Vector3 roundTrip));
        Assert.AreEqual(biasDps, roundTrip);
        Assert.IsTrue(calibration.TryObserve(biasDps * GyroScale, Gravity,
            GyroScale, AccelerometerScale, 1, QpcFrequency,
            out Vector3 corrected));
        Assert.AreEqual(0.0f, corrected.Length(), 0.0001f);
    }

    [TestMethod]
    public void ObservationResetPreservesCommittedBiasAndRevision()
    {
        var calibration = new Switch2StationaryGyroCalibration();
        Vector3 bias = new(4.0f, -2.0f, 1.0f);
        ObserveRange(calibration, bias, Gravity, 0, 501);
        ulong revision = calibration.BiasRevision;

        calibration.ResetObservationState();

        Assert.IsTrue(calibration.HasCommittedBias);
        Assert.AreEqual(revision, calibration.BiasRevision);
        Assert.AreEqual(bias, calibration.Bias);
        Assert.IsFalse(calibration.IsCalibrating);
    }

    [TestMethod]
    public void BadAccelerationAndLongGapCannotQualify()
    {
        var calibration = new Switch2StationaryGyroCalibration();
        Vector3 bias = new(4.0f, 0.0f, 0.0f);
        ObserveRange(calibration, bias, Gravity, 0, 400);
        Assert.IsTrue(calibration.TryObserve(bias, Vector3.Zero,
            GyroScale, AccelerometerScale, 401 * StepQpc, QpcFrequency,
            out _));
        ObserveRange(calibration, bias, Gravity, 402, 800);
        Assert.IsFalse(calibration.HasCommittedBias);

        Assert.IsTrue(calibration.TryObserve(bias, Gravity, GyroScale,
            AccelerometerScale, 1_000 * StepQpc, QpcFrequency, out _));
        ObserveRange(calibration, bias, Gravity, 1_001, 1_499);
        Assert.IsFalse(calibration.HasCommittedBias);
    }

    [TestMethod]
    public void RecalibrationCannotWalkCommittedBiasPastAbsoluteCap()
    {
        var calibration = new Switch2StationaryGyroCalibration();
        Vector3 first = new(0.9f * GyroScale, 0.0f, 0.0f);
        ObserveRange(calibration, first, Gravity, 0, 501);
        Assert.IsTrue(calibration.HasCommittedBias);

        calibration.RestartPreservingBias();
        Vector3 second = new(1.8f * GyroScale, 0.0f, 0.0f);
        ObserveRange(calibration, second, Gravity, 0, 501);
        Assert.AreEqual(second.X, calibration.Bias.X, 0.001f);

        calibration.RestartPreservingBias();
        Vector3 excessive = new(2.7f * GyroScale, 0.0f, 0.0f);
        ObserveRange(calibration, excessive, Gravity, 0, 501);

        Assert.IsTrue(calibration.IsCalibrating);
        Assert.AreEqual(second.X, calibration.Bias.X, 0.001f);
        Assert.IsTrue(calibration.Bias.Length() / GyroScale <=
            Switch2StationaryGyroCalibration.MaximumCommittedBiasDps);
    }

    [TestMethod]
    public void WarmObservationPathAllocatesNothing()
    {
        var calibration = new Switch2StationaryGyroCalibration();
        Vector3 bias = new(4.0f, -2.0f, 1.0f);
        ObserveRange(calibration, bias, Gravity, 0, 501);
        bool valid = true;
        long timestamp = 502 * StepQpc;
        for (int index = 0; index < 1_000; index++)
        {
            valid &= calibration.TryObserve(bias, Gravity, GyroScale,
                AccelerometerScale, timestamp, QpcFrequency, out _);
            timestamp += StepQpc;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            valid &= calibration.TryObserve(bias, Gravity, GyroScale,
                AccelerometerScale, timestamp, QpcFrequency, out _);
            timestamp += StepQpc;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
    }

    private static Vector3 ObserveRange(
        Switch2StationaryGyroCalibration calibration,
        in Vector3 gyroscope, in Vector3 accelerometer,
        int firstIndex, int lastIndexInclusive)
    {
        Vector3 corrected = default;
        for (int index = firstIndex; index <= lastIndexInclusive; index++)
        {
            Assert.IsTrue(calibration.TryObserve(gyroscope, accelerometer,
                GyroScale, AccelerometerScale, index * StepQpc,
                QpcFrequency, out corrected));
        }
        return corrected;
    }
}
