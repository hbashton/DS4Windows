using System.Numerics;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2HorizonStabilizerTests
{
    private const float GyroScale = 16.0f;
    private const float AccelerationScale = 4096.0f;

    [TestMethod]
    public void IdentityOrientationPreservesSelectedVerticalAxes()
    {
        Assert.IsTrue(Switch2HorizonStabilizer.TryProject(
            new Vector3(10.0f, 20.0f, 30.0f),
            new Vector3(100.0f, 200.0f, 300.0f),
            Quaternion.Identity, horizontal: false,
            out Vector3 gyroscope, out Vector3 accelerometer));

        Assert.AreEqual(new Vector3(10.0f, 0.0f, 30.0f), gyroscope);
        Assert.AreEqual(new Vector3(100.0f, 200.0f, 300.0f),
            accelerometer);
    }

    [TestMethod]
    public void IdentityOrientationPreservesSelectedHorizontalAxes()
    {
        Assert.IsTrue(Switch2HorizonStabilizer.TryProject(
            new Vector3(10.0f, 20.0f, 30.0f),
            new Vector3(100.0f, 200.0f, 300.0f),
            Quaternion.Identity, horizontal: true,
            out Vector3 gyroscope, out Vector3 accelerometer));

        Assert.AreEqual(new Vector3(0.0f, 20.0f, 30.0f), gyroscope);
        Assert.AreEqual(new Vector3(100.0f, 200.0f, 300.0f),
            accelerometer);
    }

    [TestMethod]
    public void NontrivialProjectionMatchesPinnedSwitch2ConnectLaw()
    {
        Quaternion bodyToWorld = Quaternion.CreateFromAxisAngle(
            Vector3.UnitY, MathF.PI / 4.0f);

        Assert.IsTrue(Switch2HorizonStabilizer.TryProject(
            new Vector3(10.0f, 20.0f, 30.0f),
            new Vector3(100.0f, 200.0f, 300.0f), bodyToWorld,
            horizontal: false, out Vector3 gyroscope,
            out Vector3 accelerometer));

        Assert.AreEqual(28.284271f, gyroscope.X, 0.0001f);
        Assert.AreEqual(0.0f, gyroscope.Y, 0.0001f);
        Assert.AreEqual(14.142136f, gyroscope.Z, 0.0001f);
        Assert.AreEqual(282.84271f, accelerometer.X, 0.001f);
        Assert.AreEqual(200.0f, accelerometer.Y, 0.001f);
        Assert.AreEqual(141.42136f, accelerometer.Z, 0.001f);
    }

    [TestMethod]
    public void EnabledEstimatorInitializesThenProjectsWithoutRollAxis()
    {
        var stabilizer = new Switch2HorizonStabilizer();
        Vector3 gyro = new(160.0f, 80.0f, 320.0f);
        Vector3 acceleration = new(0.0f, 0.0f, AccelerationScale);

        Assert.IsTrue(stabilizer.TryApply(gyro, acceleration, GyroScale,
            AccelerationScale, elapsedSeconds: 0.0,
            horizonEnabled: true, observationEpoch: 1,
            horizontal: false, out var initial));
        Assert.IsFalse(initial.Applied);
        Assert.IsTrue(initial.OrientationInitialized);
        Assert.AreEqual(gyro, initial.Gyroscope);

        Assert.IsTrue(stabilizer.TryApply(gyro, acceleration, GyroScale,
            AccelerationScale, elapsedSeconds: 0.01,
            horizonEnabled: true, observationEpoch: 1,
            horizontal: false, out var projected));
        Assert.IsTrue(projected.Applied);
        Assert.IsTrue(projected.OrientationInitialized);
        Assert.AreEqual(0.0f, projected.Gyroscope.Y);
        Assert.IsTrue(float.IsFinite(projected.Gyroscope.X));
        Assert.IsTrue(float.IsFinite(projected.Gyroscope.Z));
    }

    [TestMethod]
    public void GapAndDisabledModeFallBackToRawMotion()
    {
        var stabilizer = new Switch2HorizonStabilizer();
        Vector3 gyro = new(160.0f, 80.0f, 320.0f);
        Vector3 acceleration = new(0.0f, 0.0f, AccelerationScale);
        Assert.IsTrue(stabilizer.TryApply(gyro, acceleration, GyroScale,
            AccelerationScale, 0.0, true, 1, false, out _));
        Assert.IsTrue(stabilizer.TryApply(gyro, acceleration, GyroScale,
            AccelerationScale, 0.01, true, 1, false, out var projected));
        Assert.IsTrue(projected.Applied);

        Assert.IsTrue(stabilizer.TryApply(gyro, acceleration, GyroScale,
            AccelerationScale, 0.101, true, 1, false, out var gap));
        Assert.IsFalse(gap.Applied);
        Assert.AreEqual(gyro, gap.Gyroscope);

        Assert.IsTrue(stabilizer.TryApply(gyro, acceleration, GyroScale,
            AccelerationScale, 0.01, false, 1, false, out var disabled));
        Assert.IsFalse(disabled.Applied);
        Assert.AreEqual(gyro, disabled.Gyroscope);
        Assert.AreEqual(acceleration, disabled.Accelerometer);
    }

    [TestMethod]
    public void SourceEpochChangeMatchesACompletelyFreshEstimator()
    {
        var reused = new Switch2HorizonStabilizer();
        Vector3 acceleration = new(0.0f, 0.0f, AccelerationScale);
        Assert.IsTrue(reused.TryApply(new Vector3(3200.0f, 0.0f, 0.0f),
            acceleration, GyroScale, AccelerationScale, 0.0, true, 1,
            false, out _));
        for (int index = 0; index < 100; index++)
        {
            Assert.IsTrue(reused.TryApply(
                new Vector3(3200.0f, 0.0f, 0.0f), acceleration,
                GyroScale, AccelerationScale, 0.01, true, 1, false,
                out _));
        }

        Vector3 newAcceleration = new(AccelerationScale, 0.0f, 0.0f);
        Vector3 newGyro = new(160.0f, 80.0f, 320.0f);
        Assert.IsTrue(reused.TryApply(newGyro, newAcceleration, GyroScale,
            AccelerationScale, 0.01, true, 2, false,
            out var afterEpochChange));

        var fresh = new Switch2HorizonStabilizer();
        Assert.IsTrue(fresh.TryApply(newGyro, newAcceleration, GyroScale,
            AccelerationScale, 0.01, true, 2, false,
            out var freshResult));

        Assert.AreEqual(freshResult.Gyroscope.X,
            afterEpochChange.Gyroscope.X, 0.0001f);
        Assert.AreEqual(freshResult.Gyroscope.Y,
            afterEpochChange.Gyroscope.Y, 0.0001f);
        Assert.AreEqual(freshResult.Gyroscope.Z,
            afterEpochChange.Gyroscope.Z, 0.0001f);
        Assert.AreEqual(freshResult.Accelerometer.X,
            afterEpochChange.Accelerometer.X, 0.0001f);
        Assert.AreEqual(freshResult.Accelerometer.Y,
            afterEpochChange.Accelerometer.Y, 0.0001f);
        Assert.AreEqual(freshResult.Accelerometer.Z,
            afterEpochChange.Accelerometer.Z, 0.0001f);
    }

    [TestMethod]
    public void InvalidPhysicalSampleFailsClosedAndResetsState()
    {
        var stabilizer = new Switch2HorizonStabilizer();

        Assert.IsFalse(stabilizer.TryApply(
            new Vector3(float.NaN, 0.0f, 0.0f), Vector3.UnitZ,
            GyroScale, AccelerationScale, 0.01, true, 1, false,
            out _));
        Assert.IsFalse(Switch2HorizonStabilizer.TryProject(Vector3.Zero,
            Vector3.Zero, default, false, out _, out _));
    }

    [TestMethod]
    public void WarmProjectionAllocatesNothing()
    {
        var stabilizer = new Switch2HorizonStabilizer();
        Vector3 gyro = new(16.0f, 8.0f, 32.0f);
        Vector3 acceleration = new(0.0f, 0.0f, AccelerationScale);
        bool succeeded = stabilizer.TryApply(gyro, acceleration,
            GyroScale, AccelerationScale, 0.0, true, 1, false, out _);
        for (int index = 0; index < 2_000; index++)
        {
            succeeded &= stabilizer.TryApply(gyro, acceleration,
                GyroScale, AccelerationScale, 0.01, true, 1, false,
                out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            succeeded &= stabilizer.TryApply(gyro, acceleration,
                GyroScale, AccelerationScale, 0.01, true, 1, false,
                out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }
}
