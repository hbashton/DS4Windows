using System.Numerics;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2MagnetometerYawAssistTests
{
    public TestContext TestContext { get; set; }

    [TestInitialize]
    public void StartNativeAllocationCapture()
    {
        if (TestContext.TestName == nameof(WarmAssistPathAllocatesNothing))
            NativeAllocationMeasurement.Begin();
    }

    [TestCleanup]
    public void StopNativeAllocationCapture()
    {
        if (TestContext.TestName != nameof(WarmAssistPathAllocatesNothing)) return;
        NativeAllocationMeasurement.End(-1);
    }
    private const float GyroScale = 16.384f;
    private const float TrueYawDps = 10.0f;
    private const float PositiveGyroBiasDps = 0.05f;
    private const double DeltaSeconds = 0.01;
    private static readonly Vector3 Gravity = new(0.0f, 4096.0f, 0.0f);

    [TestMethod]
    public void DisabledAssistPreservesGyroscopeExactly()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Vector3 gyro = new(12.0f, -34.0f, 56.0f);

        Assert.IsTrue(assist.TryApply(gyro, Gravity,
            new Vector3(1000.0f, 0.0f, 0.0f), GyroScale,
            DeltaSeconds, assistEnabled: false, observationEpoch: 1,
            magneticObservationFresh: true, out var result));

        Assert.AreEqual(gyro, result.Gyroscope);
        Assert.IsFalse(result.CorrectionApplied);
        Assert.AreEqual(0.0f, result.CorrectionDps);
    }

    [TestMethod]
    public void StableRelativeMotionLearnsBoundedOpposingYawBias()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Switch2MagnetometerYawAssistResult result = Train(assist,
            freshEveryFrames: 1, observationEpoch: 1);

        Assert.IsTrue(result.MagneticSampleAccepted);
        Assert.IsTrue(result.CorrectionApplied);
        Assert.AreEqual(Switch2MagnetometerYawAssist.FullConfidenceBuckets,
            result.ValidBuckets);
        Assert.IsTrue(result.CorrectionDps < 0.0f,
            "A positive gyro bias requires a negative learned correction.");
        Assert.IsTrue(MathF.Abs(result.CorrectionDps) <=
            Switch2MagnetometerYawAssist.OutputCapDps);
        Assert.IsTrue(result.Gyroscope.Y <
            (TrueYawDps + PositiveGyroBiasDps) * GyroScale);
    }

    [TestMethod]
    public void LowerRateFreshMagnetometerDoesNotTreatCachedValuesAsFrames()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Switch2MagnetometerYawAssistResult result = Train(assist,
            freshEveryFrames: 5, observationEpoch: 1);

        Assert.IsTrue(result.CorrectionApplied);
        Assert.IsTrue(result.CorrectionDps < 0.0f);
        Assert.AreEqual(Switch2MagnetometerYawAssist.FullConfidenceBuckets,
            result.ValidBuckets);
    }

    [TestMethod]
    public void MagneticMagnitudeJumpImmediatelyFallsBackToRawGyro()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Train(assist, freshEveryFrames: 1, observationEpoch: 1);
        Vector3 gyro = Gyro(TrueYawDps + PositiveGyroBiasDps);

        Assert.IsTrue(assist.TryApply(gyro, Gravity,
            new Vector3(2000.0f, 0.0f, 0.0f), GyroScale,
            DeltaSeconds, assistEnabled: true, observationEpoch: 1,
            magneticObservationFresh: true, out var disturbed));

        Assert.IsFalse(disturbed.MagneticSampleAccepted);
        Assert.IsFalse(disturbed.CorrectionApplied);
        Assert.AreEqual(gyro, disturbed.Gyroscope);
    }

    [TestMethod]
    public void NonYawDominantMotionCannotTeachBias()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Vector3 magnetometer = new(1000.0f, 0.0f, 0.0f);
        Switch2MagnetometerYawAssistResult result = default;
        for (int index = 0; index < 800; index++)
        {
            float angle = TrueYawDps * (float)(index * DeltaSeconds);
            magnetometer = Magnetometer(angle);
            Assert.IsTrue(assist.TryApply(
                new Vector3(TrueYawDps * GyroScale, 0.0f, 0.0f),
                Gravity, magnetometer, GyroScale, DeltaSeconds,
                assistEnabled: true, observationEpoch: 1,
                magneticObservationFresh: true, out result));
        }

        Assert.AreEqual(0, result.ValidBuckets);
        Assert.IsFalse(result.CorrectionApplied);
    }

    [TestMethod]
    public void TrainedAssistFallsBackDuringNonYawDominantMotion()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Assert.IsTrue(Train(assist, freshEveryFrames: 1,
            observationEpoch: 1).CorrectionApplied);
        Vector3 raw = new(TrueYawDps * GyroScale, 0.0f, 0.0f);

        Assert.IsTrue(assist.TryApply(raw, Gravity,
            Magnetometer(100.0f), GyroScale, DeltaSeconds,
            assistEnabled: true, observationEpoch: 1,
            magneticObservationFresh: true, out var result));

        Assert.IsTrue(result.MagneticSampleAccepted);
        Assert.IsFalse(result.CorrectionApplied);
        Assert.AreEqual(raw, result.Gyroscope);
    }

    [TestMethod]
    public void TrainedAssistFallsBackWhenMagneticDirectionDisagrees()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Assert.IsTrue(Train(assist, freshEveryFrames: 1,
            observationEpoch: 1).CorrectionApplied);
        Vector3 raw = Gyro(TrueYawDps + PositiveGyroBiasDps);

        Assert.IsTrue(assist.TryApply(raw, Gravity,
            Magnetometer(99.8f), GyroScale, DeltaSeconds,
            assistEnabled: true, observationEpoch: 1,
            magneticObservationFresh: true, out var result));

        Assert.IsTrue(result.MagneticSampleAccepted);
        Assert.IsFalse(result.CorrectionApplied);
        Assert.AreEqual(raw, result.Gyroscope);

        Assert.IsTrue(assist.TryApply(raw, Gravity,
            Magnetometer(99.8f), GyroScale, DeltaSeconds,
            assistEnabled: true, observationEpoch: 1,
            magneticObservationFresh: false, out var cached));
        Assert.IsFalse(cached.CorrectionApplied,
            "A rejected fresh frame must not authorize cached correction.");

        Assert.IsTrue(assist.TryApply(raw, Gravity,
            Magnetometer(99.9f), GyroScale, DeltaSeconds,
            assistEnabled: true, observationEpoch: 1,
            magneticObservationFresh: true, out var revalidated));
        Assert.IsTrue(revalidated.CorrectionApplied,
            "A new direction-consistent fresh frame should reauthorize correction.");
    }

    [TestMethod]
    public void TrainedAssistFallsBackAcrossBadTimingGap()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Assert.IsTrue(Train(assist, freshEveryFrames: 1,
            observationEpoch: 1).CorrectionApplied);
        Vector3 raw = Gyro(TrueYawDps + PositiveGyroBiasDps);

        Assert.IsTrue(assist.TryApply(raw, Gravity,
            Magnetometer(102.0f), GyroScale,
            Switch2MagnetometerYawAssist.MaximumIntegratedDeltaSeconds +
                DeltaSeconds,
            assistEnabled: true, observationEpoch: 1,
            magneticObservationFresh: true, out var result));

        Assert.IsFalse(result.MagneticSampleAccepted);
        Assert.IsFalse(result.CorrectionApplied);
        Assert.AreEqual(raw, result.Gyroscope);
    }

    [TestMethod]
    public void CachedMagnetometerCannotBridgeUnboundedObservationInterval()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Assert.IsTrue(Train(assist, freshEveryFrames: 1,
            observationEpoch: 1).CorrectionApplied);
        Vector3 raw = Gyro(TrueYawDps + PositiveGyroBiasDps);
        Switch2MagnetometerYawAssistResult result = default;
        for (int index = 0; index < 12; index++)
        {
            Assert.IsTrue(assist.TryApply(raw, Gravity,
                Magnetometer(99.9f), GyroScale, DeltaSeconds,
                assistEnabled: true, observationEpoch: 1,
                magneticObservationFresh: false, out result));
        }

        Assert.IsFalse(result.MagneticSampleAccepted);
        Assert.IsFalse(result.CorrectionApplied);
        Assert.AreEqual(raw, result.Gyroscope);
    }

    [TestMethod]
    public void LearnedBiasDecaysWithoutValidYawObservations()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Switch2MagnetometerYawAssistResult trained = Train(assist,
            freshEveryFrames: 1, observationEpoch: 1);
        float initialMagnitude = MathF.Abs(trained.EstimatedBiasDps);
        Switch2MagnetometerYawAssistResult result = trained;
        for (int index = 0; index < 700; index++)
        {
            Assert.IsTrue(assist.TryApply(
                new Vector3(TrueYawDps * GyroScale, 0.0f, 0.0f),
                Gravity, Magnetometer(100.0f), GyroScale, DeltaSeconds,
                assistEnabled: true, observationEpoch: 1,
                magneticObservationFresh: true, out result));
        }

        Assert.IsFalse(result.CorrectionApplied);
        Assert.IsTrue(MathF.Abs(result.EstimatedBiasDps) <
            initialMagnitude * 0.40f);
    }

    [TestMethod]
    public void ProfileOrSourceEpochChangeRequiresFreshConfidence()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Switch2MagnetometerYawAssistResult trained = Train(assist,
            freshEveryFrames: 1, observationEpoch: 1);
        Assert.IsTrue(trained.CorrectionApplied);

        Vector3 gyro = Gyro(TrueYawDps + PositiveGyroBiasDps);
        Assert.IsTrue(assist.TryApply(gyro, Gravity,
            Magnetometer(81.0f), GyroScale, DeltaSeconds,
            assistEnabled: true, observationEpoch: 2,
            magneticObservationFresh: true, out var replaced));

        Assert.AreEqual(0, replaced.ValidBuckets);
        Assert.IsFalse(replaced.CorrectionApplied);
        Assert.AreEqual(gyro, replaced.Gyroscope);
    }

    [TestMethod]
    public void InvalidGyroscopeFailsAndClearsLearnedState()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Assert.IsTrue(Train(assist, 1, 1).CorrectionApplied);
        Assert.IsFalse(assist.TryApply(
            new Vector3(float.NaN, 0.0f, 0.0f), Gravity,
            new Vector3(1000.0f, 0.0f, 0.0f), GyroScale,
            DeltaSeconds, assistEnabled: true, observationEpoch: 1,
            magneticObservationFresh: true, out _));

        Vector3 gyro = Gyro(TrueYawDps + PositiveGyroBiasDps);
        Assert.IsTrue(assist.TryApply(gyro, Gravity,
            new Vector3(1000.0f, 0.0f, 0.0f), GyroScale,
            DeltaSeconds, assistEnabled: true, observationEpoch: 1,
            magneticObservationFresh: true, out var recovered));
        Assert.AreEqual(0, recovered.ValidBuckets);
        Assert.IsFalse(recovered.CorrectionApplied);
    }

    [TestMethod]
    [DoNotParallelize]
    public void WarmAssistPathAllocatesNothing()
    {
        var assist = new Switch2MagnetometerYawAssist();
        Train(assist, freshEveryFrames: 1, observationEpoch: 1);
        Vector3 gyro = Gyro(TrueYawDps + PositiveGyroBiasDps);
        Vector3 magnetometer = Magnetometer(80.0f);
        bool valid = true;
        for (int index = 0; index < 1_000; index++)
        {
            valid &= assist.TryApply(gyro, Gravity, magnetometer, GyroScale,
                DeltaSeconds, assistEnabled: true, observationEpoch: 1,
                magneticObservationFresh: true, out _);
        }

        long allocated;
        using (StrictAllocationMeasurementScope.Begin())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 20_000; index++)
            {
                valid &= assist.TryApply(gyro, Gravity, magnetometer, GyroScale,
                    DeltaSeconds, assistEnabled: true, observationEpoch: 1,
                    magneticObservationFresh: true, out _);
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    [DoNotParallelize]
    public void RepeatedWarmAssistMeasurementsAllocateNothing()
    {
        for (int batch = 0; batch < 100; batch++)
            WarmAssistPathAllocatesNothing();
    }

    [TestMethod]
    public void AllocationMeasurementDetectsIntentionalAllocation()
    {
        NativeAllocationMeasurement.Begin();
        NativeAllocationMeasurement.End(0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AllocationPositiveControl)));
        long warmBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(warmBytes >= 128);
        NativeAllocationMeasurement.Begin();
        before = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AllocationPositiveControl)));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        uint objects = NativeAllocationMeasurement.End(allocated);
        Assert.IsTrue(allocated >= 128);
        if (NativeAllocationMeasurement.IsEnabled) Assert.IsTrue(objects >= 1);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 128)]
    private sealed class AllocationPositiveControl { }

    private static Switch2MagnetometerYawAssistResult Train(
        Switch2MagnetometerYawAssist assist, int freshEveryFrames,
        ulong observationEpoch)
    {
        Vector3 lastMagnetometer = Magnetometer(0.0f);
        Switch2MagnetometerYawAssistResult result = default;
        for (int index = 0; index < 1_000; index++)
        {
            bool fresh = index % freshEveryFrames == 0;
            if (fresh)
            {
                float angle = TrueYawDps *
                    (float)(index * DeltaSeconds);
                lastMagnetometer = Magnetometer(angle);
            }
            Assert.IsTrue(assist.TryApply(
                Gyro(TrueYawDps + PositiveGyroBiasDps), Gravity,
                lastMagnetometer, GyroScale, DeltaSeconds,
                assistEnabled: true, observationEpoch,
                magneticObservationFresh: fresh, out result));
        }
        return result;
    }

    private static Vector3 Gyro(float yawDps) =>
        new(0.0f, yawDps * GyroScale, 0.0f);

    private static Vector3 Magnetometer(float physicalYawDegrees)
    {
        float radians = physicalYawDegrees * MathF.PI / 180.0f;
        return new Vector3(MathF.Cos(radians) * 1000.0f, 0.0f,
            MathF.Sin(radians) * 1000.0f);
    }
}
