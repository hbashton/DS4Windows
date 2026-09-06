using System;

namespace DS4Windows;

/// <summary>
/// Fixed-storage copy of one borrowed state and at most one previous motion
/// sample. The caller must own the source while capturing and must not reuse
/// this snapshot while a consumer is borrowing State. This helper is not a
/// publication lock, pair-generation fence, or history queue.
/// </summary>
internal sealed class DS4StateOwnedSnapshot
{
    private readonly SixAxis currentMotion;
    private readonly SixAxis previousMotion;

    internal DS4StateOwnedSnapshot()
    {
        State = new DS4State();
        currentMotion = State.Motion;
        previousMotion = new SixAxis(0, 0, 0, 0, 0, 0, 0);
    }

    internal DS4State State { get; }

    internal void Capture(DS4State source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Capture BOTH value images before writing either owned motion slot.
        // Source can be State itself, or its current/previous references can
        // point into our storage in either order. No graph traversal is needed.
        SixAxis sourceMotion = source.Motion;
        SixAxis sourcePrevious = sourceMotion?.previousAxis;
        bool hasMotion = sourceMotion != null;
        bool hasPrevious = sourcePrevious != null;
        MotionScalars current = hasMotion ? new MotionScalars(sourceMotion) : default;
        MotionScalars previous = hasPrevious ? new MotionScalars(sourcePrevious) : default;

        source.CopyTo(State);
        // Also clear unused private storage: a later null/reuse transition
        // must never retain an earlier source reference or previous sample.
        current.WriteTo(currentMotion);
        previous.WriteTo(previousMotion);
        previousMotion.previousAxis = null;
        currentMotion.previousAxis = hasPrevious ? previousMotion : null;
        State.Motion = hasMotion ? currentMotion : null;
    }

    // SixAxis.copy is intentionally not used: it omits two full gyro axes,
    // recomputes mapped output acceleration and retains previousAxis aliases.
    // This value carrier copies all public motion scalars exactly. The cold
    // reflection regression fails if SixAxis gains an uncopied scalar field.
    private readonly struct MotionScalars
    {
        private readonly int gyroYaw, gyroPitch, gyroRoll;
        private readonly int accelX, accelY, accelZ;
        private readonly int outputAccelX, outputAccelY, outputAccelZ;
        private readonly bool outputGyroControls;
        private readonly double accelXG, accelYG, accelZG;
        private readonly double angVelYaw, angVelPitch, angVelRoll;
        private readonly int gyroYawFull, gyroPitchFull, gyroRollFull;
        private readonly int accelXFull, accelYFull, accelZFull;
        private readonly double elapsed;

        internal MotionScalars(SixAxis source)
        {
            gyroYaw = source.gyroYaw;
            gyroPitch = source.gyroPitch;
            gyroRoll = source.gyroRoll;
            accelX = source.accelX;
            accelY = source.accelY;
            accelZ = source.accelZ;
            outputAccelX = source.outputAccelX;
            outputAccelY = source.outputAccelY;
            outputAccelZ = source.outputAccelZ;
            outputGyroControls = source.outputGyroControls;
            accelXG = source.accelXG;
            accelYG = source.accelYG;
            accelZG = source.accelZG;
            angVelYaw = source.angVelYaw;
            angVelPitch = source.angVelPitch;
            angVelRoll = source.angVelRoll;
            gyroYawFull = source.gyroYawFull;
            gyroPitchFull = source.gyroPitchFull;
            gyroRollFull = source.gyroRollFull;
            accelXFull = source.accelXFull;
            accelYFull = source.accelYFull;
            accelZFull = source.accelZFull;
            elapsed = source.elapsed;
        }

        internal void WriteTo(SixAxis destination)
        {
            destination.gyroYaw = gyroYaw;
            destination.gyroPitch = gyroPitch;
            destination.gyroRoll = gyroRoll;
            destination.accelX = accelX;
            destination.accelY = accelY;
            destination.accelZ = accelZ;
            destination.outputAccelX = outputAccelX;
            destination.outputAccelY = outputAccelY;
            destination.outputAccelZ = outputAccelZ;
            destination.outputGyroControls = outputGyroControls;
            destination.accelXG = accelXG;
            destination.accelYG = accelYG;
            destination.accelZG = accelZG;
            destination.angVelYaw = angVelYaw;
            destination.angVelPitch = angVelPitch;
            destination.angVelRoll = angVelRoll;
            destination.gyroYawFull = gyroYawFull;
            destination.gyroPitchFull = gyroPitchFull;
            destination.gyroRollFull = gyroRollFull;
            destination.accelXFull = accelXFull;
            destination.accelYFull = accelYFull;
            destination.accelZFull = accelZFull;
            destination.elapsed = elapsed;
        }
    }
}
