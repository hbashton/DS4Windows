using System.Numerics;
using DS4Windows.Switch2;

namespace DS4Windows;

/// <summary>Shared motion options for calibrated original Nintendo samples.</summary>
internal sealed class NintendoMotionProfileOptions
{
    private readonly Switch2HorizonStabilizer horizon = new();
    internal void Reset() => horizon.Reset();

    internal bool TryApply(in Vector3 gyro, in Vector3 acceleration, double elapsed,
        bool horizonEnabled, double softDeadzone, ulong epoch,
        out Vector3 projectedGyro, out Vector3 projectedAcceleration)
    {
        // JoyConDevice's calibrated semantic gyro tuple is (yaw,pitch,roll).
        // The corresponding Cartesian angular vector is (-pitch,yaw,-roll).
        // Rotate both sensors into a right-handed Z-up body frame before using
        // the existing estimator; no factory bias is estimated a second time.
        const float commonCountsPerNativeCount = 0.070f * 16.384f;
        Vector3 bodyGyro = new Vector3(-gyro.Y, gyro.Z, gyro.X) * commonCountsPerNativeCount;
        Vector3 bodyAcceleration = new(acceleration.X, -acceleration.Z, acceleration.Y);
        if (!horizon.TryApply(bodyGyro, bodyAcceleration, 16.384f, 8192.0f, elapsed,
                horizonEnabled, epoch, horizontal: false, out var result))
        {
            projectedGyro = projectedAcceleration = default;
            return false;
        }
        bodyGyro = Switch2MotionSoftDeadzone.Apply(result.Gyroscope, softDeadzone, horizontal: false) /
            commonCountsPerNativeCount;
        projectedGyro = new(bodyGyro.Z, -bodyGyro.X, bodyGyro.Y);
        projectedAcceleration = new(result.Accelerometer.X, result.Accelerometer.Z, -result.Accelerometer.Y);
        return true;
    }
}
