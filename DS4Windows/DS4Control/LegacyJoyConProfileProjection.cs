using System;
using System.Numerics;
using DS4Windows.Switch2;

namespace DS4Windows;

/// <summary>
/// Allocation-free original Joy-Con adapter into the canonical profile mapper.
/// The coordinator owns the borrowed calibrated snapshots and serializes calls.
/// This adapter neither decodes Switch 2 reports nor creates a second output owner.
/// </summary>
internal sealed class LegacyJoyConProfileProjection : ILegacyJoyConProfileProjection
{
    internal const double MaximumSourceAgeSeconds = 0.100;
    private const float NativeGyroDegreesPerSecond = 0.070f;
    private const float FusionCountsPerDegree = 16.384f;
    private readonly DS4State neutral = new();
    private readonly DS4State lastOutput = new();
    private readonly DS4StateOwnedSnapshot history = new();
    private readonly SixAxis motion = new(0, 0, 0, 0, 0, 0, 0);
    private readonly NintendoMotionProfileOptions motionOptions = new();
    private Switch2DualGyroModeState modeState;
    private Switch2DualJoyConGyroFusionState fusionState;
    private ulong fusionEpoch;
    private Switch2DualGyroDominantSide fusionDominant;
    private NintendoInputStatus lastIdentity;
    private long lastProfileRevision;
    private int lastProfileSlot;
    private long lastLeftTimestampQpc, lastRightTimestampQpc;
    private bool hasLast;
    private uint packetCounter;

    public bool TryApply(in LegacyJoyConProjectionInput input, DS4State destination)
    {
        bool leftPresent = input.Left != null, rightPresent = input.Right != null;
        bool joined = leftPresent && rightPresent;
        if (destination == null || ReferenceEquals(destination, input.Left) || ReferenceEquals(destination, input.Right) ||
            input.ProfileSlot < 0 || input.ProfileSlot >= Global.Switch2JoyConStandaloneHoldMode.Length ||
            input.CompletionQpc < 0 || input.QpcFrequency <= 0 || (!leftPresent && !rightPresent) ||
            leftPresent != (input.LeftGeneration != 0) || rightPresent != (input.RightGeneration != 0) ||
            joined != (input.PairEpoch != 0) ||
            !ValidSource(input.Left, input.LeftTimestampQpc, input.CompletionQpc) ||
            !ValidSource(input.Right, input.RightTimestampQpc, input.CompletionQpc))
            return Invalidate();

        bool leftFresh = IsFresh(input.Left, input.LeftTimestampQpc, input);
        bool rightFresh = IsFresh(input.Right, input.RightTimestampQpc, input);
        if (!leftFresh && !rightFresh) return Invalidate();
        if (!ControllerFeedbackClock.TryConvertQpcTicks((ulong)input.CompletionQpc,
                (ulong)input.QpcFrequency, out ulong completionMicroseconds)) return Invalidate();
        DS4State left = leftFresh ? input.Left : null, right = rightFresh ? input.Right : null;
        bool horizontal = !joined && Global.Switch2JoyConStandaloneHoldMode[input.ProfileSlot] == Switch2JoyConHoldMode.Horizontal;
        Switch2JoyConProfileMode mode = joined ? Switch2JoyConProfileMode.Joined : leftPresent ?
            horizontal ? Switch2JoyConProfileMode.StandaloneHorizontalLeft : Switch2JoyConProfileMode.StandaloneVerticalLeft :
            horizontal ? Switch2JoyConProfileMode.StandaloneHorizontalRight : Switch2JoyConProfileMode.StandaloneVerticalRight;
        Switch2FaceButtonLayout layout = Global.Switch2FaceButtonLayout[input.ProfileSlot];
        if (!Switch2FaceButtonLayoutProjection.IsValid(layout)) return Invalidate();
        long revision = Math.Max(0, Global.ReadProfileSwitchRevision(input.ProfileSlot));
        var identity = new NintendoInputStatus(NintendoInputStatus.CurrentVersion, mode, input.PairEpoch,
            input.LeftGeneration, input.RightGeneration, leftPresent, rightPresent,
            input.CompletionQpc, input.QpcFrequency, 0, 0, 0);
        bool sameLifetime = hasLast && NintendoProfileInput.Identity(identity).Equals(
            NintendoProfileInput.Identity(lastIdentity)) && lastIdentity.QpcFrequency == input.QpcFrequency &&
            lastProfileRevision == revision && lastProfileSlot == input.ProfileSlot;
        if (sameLifetime && (input.CompletionQpc < lastIdentity.CompletionTimestampQpc ||
            leftPresent && input.LeftTimestampQpc < lastLeftTimestampQpc ||
            rightPresent && input.RightTimestampQpc < lastRightTimestampQpc)) return false;
        if (sameLifetime && input.CompletionQpc == lastIdentity.CompletionTimestampQpc) return false;
        if (!sameLifetime || input.CompletionQpc < lastIdentity.CompletionTimestampQpc)
        {
            modeState = default;
            fusionState = default;
            fusionEpoch = 0;
            motionOptions.Reset();
            hasLast = false;
        }
        double elapsed = hasLast ? (input.CompletionQpc - lastIdentity.CompletionTimestampQpc) / (double)input.QpcFrequency : 0;
        if (elapsed > MaximumSourceAgeSeconds) elapsed = 0;
        if (hasLast) history.Capture(lastOutput);

        Switch2JoyConProfileButton leftButtons = ReadPhysicalButtons(left, true);
        Switch2JoyConProfileButton rightButtons = ReadPhysicalButtons(right, false);
        Switch2JoyConProfileButton buttons = horizontal ?
            ToHorizontal(leftPresent ? leftButtons : rightButtons, leftPresent) : leftButtons | rightButtons;
        if (!TryProjectMotion(input.ProfileSlot, input.PairEpoch, revision, joined,
                left, right, leftButtons, rightButtons, horizontal, leftPresent,
                elapsed, out Vector3 gyro, out Vector3 accel)) return Invalidate();

        neutral.CopyTo(destination);
        WriteButtons(destination, buttons, layout);
        if (horizontal)
        {
            DS4State half = leftPresent ? left : right;
            byte x = half == null ? (byte)128 : leftPresent ? half.LX : half.RX;
            byte y = half == null ? (byte)128 : leftPresent ? half.LY : half.RY;
            // Raw legacy Y already points down. Equivalent to SDL's mini-left
            // (-physicalY,-physicalX) and mini-right (physicalY,physicalX).
            destination.LX = leftPresent ? y : NegateAxis(y);
            destination.LY = leftPresent ? NegateAxis(x) : x;
        }
        else
        {
            destination.LXAxis = left?.LXAxis ?? DS4MappedStickAxis.FromLegacy(128);
            destination.LYAxis = left?.LYAxis ?? DS4MappedStickAxis.FromLegacy(128);
            destination.RXAxis = right?.RXAxis ?? DS4MappedStickAxis.FromLegacy(128);
            destination.RYAxis = right?.RYAxis ?? DS4MappedStickAxis.FromLegacy(128);
        }
        // Preserve old original-Joy-Con rail bindings while exposing all four
        // unambiguous rails through the neutral metadata for Nintendo profiles.
        destination.SideL = (leftPresent ? left : right)?.SideL ?? false;
        destination.SideR = (leftPresent ? left : right)?.SideR ?? false;
        destination.FnL = joined && (right?.SideL ?? false);
        destination.FnR = joined && (right?.SideR ?? false);
        destination.NintendoInputStatus = identity with { Buttons = buttons, LeftButtons = leftButtons, RightButtons = rightButtons };
        WriteMotion(gyro, accel, elapsed, hasLast ? history.State.Motion : null);
        destination.Motion = motion;
        destination.PacketCounter = unchecked(++packetCounter);
        DS4State latest = right != null && (left == null || input.RightTimestampQpc >= input.LeftTimestampQpc) ? right : left;
        destination.ReportTimeStamp = latest.ReportTimeStamp;
        destination.Battery = left != null && right != null ? Math.Min(left.Battery, right.Battery) : latest.Battery;
        destination.FrameCounter = latest.FrameCounter;
        destination.ds4Timestamp = latest.ds4Timestamp;
        // Each physical half has its own connection-relative counter. Choosing
        // the latest half's counter makes DSU time jump backwards as reports
        // alternate. One host clock also stays monotonic when a pad is reused.
        destination.totalMicroSec = completionMicroseconds;
        destination.elapsedTime = elapsed;
        destination.CopyTo(lastOutput);
        lastIdentity = identity;
        lastProfileRevision = revision;
        lastProfileSlot = input.ProfileSlot;
        lastLeftTimestampQpc = input.LeftTimestampQpc;
        lastRightTimestampQpc = input.RightTimestampQpc;
        hasLast = true;
        return true;
    }

    private bool Invalidate()
    {
        hasLast = false;
        modeState = default;
        fusionState = default;
        fusionEpoch = 0;
        motionOptions.Reset();
        return false;
    }

    private static bool ValidSource(DS4State source, long timestamp, long completion) => source == null ||
        (timestamp >= 0 && timestamp <= completion && !source.NintendoInputStatus.IsDeclared &&
         !source.Switch2RawInputStatus.IsValid && !source.Switch2JoyConRawInputStatus.IsValid);

    private static bool IsFresh(DS4State source, long timestamp, in LegacyJoyConProjectionInput input) =>
        source != null && (input.CompletionQpc - timestamp) / (double)input.QpcFrequency <= MaximumSourceAgeSeconds;

    private static byte NegateAxis(byte value) => (byte)Math.Clamp(256 - value, 0, 255);

    private bool TryProjectMotion(int profile, ulong pairEpoch, long revision, bool joined,
        DS4State left, DS4State right, Switch2JoyConProfileButton leftButtons,
        Switch2JoyConProfileButton rightButtons, bool horizontal, bool leftSide,
        double elapsed, out Vector3 gyro, out Vector3 accel)
    {
        gyro = accel = Vector3.Zero;
        if (!Switch2DualGyroConfiguration.TryCreate(joined && Global.Switch2DualJoyConGyroFusionEnabled[profile],
                Global.Switch2DualJoyConGyroMode[profile], Global.Switch2DualJoyConGyroDominantSide[profile],
                Global.Switch2DualJoyConGyroActivationMode[profile], Global.Switch2DualJoyConGyroLeftActivationButton[profile],
                Global.Switch2DualJoyConGyroRightActivationButton[profile], out var configuration, profileRevision: revision) ||
            !Switch2DualJoyConGyroMode.TryResolve(ref modeState, pairEpoch, leftButtons, rightButtons, configuration, out var policy))
            return false;
        Switch2JoyConMotionSample leftMotion = ReadMotion(left, policy.LeftActive);
        Switch2JoyConMotionSample rightMotion = ReadMotion(right, policy.RightActive);
        if (policy.FusionEnabled)
        {
            if (fusionEpoch != policy.ConfigurationEpoch || fusionDominant != policy.DominantSide)
                fusionState = default;
            fusionEpoch = policy.ConfigurationEpoch;
            fusionDominant = policy.DominantSide;
            if (!Switch2DualJoyConGyroFusion.TryFuse(leftMotion, rightMotion, policy.DominantSide,
                    Vector3.Zero, ref fusionState, out var fused)) return false;
            gyro = fused.Gyroscope / (NativeGyroDegreesPerSecond * FusionCountsPerDegree);
            accel = fused.Accelerometer * 2;
        }
        else
        {
            SixAxis selected = right?.Motion ?? left?.Motion;
            if (selected != null)
            {
                gyro = new Vector3(selected.gyroYawFull, selected.gyroPitchFull, selected.gyroRollFull);
                accel = new Vector3(selected.accelXFull, selected.accelYFull, selected.accelZFull);
            }
            fusionState = default;
            fusionEpoch = 0;
        }
        if (horizontal)
        {
            gyro = leftSide ? new Vector3(gyro.X, gyro.Z, -gyro.Y) : new Vector3(gyro.X, -gyro.Z, gyro.Y);
            // Rotate acceleration in the same Cartesian basis as the angular
            // vector (-pitch,yaw,-roll); unlike the semantic gyro tuple its
            // members are spatial X/Y/Z, not yaw/pitch/roll.
            accel = leftSide ? new Vector3(accel.Z, accel.Y, -accel.X) : new Vector3(-accel.Z, accel.Y, accel.X);
        }
        ulong epoch = unchecked(policy.ConfigurationEpoch * 1099511628211UL ^ (ulong)policy.DominantSide);
        return motionOptions.TryApply(gyro, accel, elapsed,
            Global.Switch2HorizonStabilizationEnabled[profile], Global.Switch2VirtualGyroSoftDeadzone[profile], epoch,
            out gyro, out accel);
    }

    private static Switch2JoyConMotionSample ReadMotion(DS4State source, bool active)
    {
        SixAxis value = source?.Motion;
        return value == null ? default : new Switch2JoyConMotionSample(
            new Vector3(value.gyroYawFull, value.gyroPitchFull, value.gyroRollFull) *
                (NativeGyroDegreesPerSecond * FusionCountsPerDegree),
            new Vector3(value.accelXFull, value.accelYFull, value.accelZFull) * 0.5f,
            Vector3.Zero, active);
    }

    private void WriteMotion(Vector3 gyro, Vector3 accel, double elapsed, SixAxis previous)
    {
        // Keep the original calibrated Joy-Con scale. SixAxis.populate's default
        // 16-count gyro scale is for Sony devices and must not change this path.
        int yaw = ToInt(gyro.X), pitch = ToInt(gyro.Y), roll = ToInt(gyro.Z);
        int ax = ToInt(accel.X), ay = ToInt(accel.Y), az = ToInt(accel.Z);
        motion.populate(-yaw, pitch, -roll, -ax, -ay, az, elapsed, previous);
        motion.angVelYaw = yaw * 0.070;
        motion.angVelPitch = pitch * 0.070;
        motion.angVelRoll = roll * 0.070;
        motion.accelX = ax / 62;
        motion.accelY = ay / 62;
        motion.accelZ = az / 62;
    }

    private static int ToInt(float value) => (int)Math.Clamp(Math.Round((double)value), int.MinValue + 1.0, int.MaxValue);

    private static Switch2JoyConProfileButton ReadPhysicalButtons(DS4State source, bool left)
    {
        if (source == null) return 0;
        Switch2JoyConProfileButton buttons = 0;
        if (left)
        {
            Add(ref buttons, source.DpadUp, Switch2JoyConProfileButton.DpadUp);
            Add(ref buttons, source.DpadDown, Switch2JoyConProfileButton.DpadDown);
            Add(ref buttons, source.DpadLeft, Switch2JoyConProfileButton.DpadLeft);
            Add(ref buttons, source.DpadRight, Switch2JoyConProfileButton.DpadRight);
            Add(ref buttons, source.L1, Switch2JoyConProfileButton.LeftShoulder);
            Add(ref buttons, source.L2Btn || source.L2 != 0, Switch2JoyConProfileButton.LeftTrigger);
            Add(ref buttons, source.L3, Switch2JoyConProfileButton.LeftStick);
            Add(ref buttons, source.Share, Switch2JoyConProfileButton.Back);
            Add(ref buttons, source.Capture, Switch2JoyConProfileButton.Capture);
        }
        else
        {
            Add(ref buttons, source.Square, Switch2JoyConProfileButton.FaceWest);
            Add(ref buttons, source.Triangle, Switch2JoyConProfileButton.FaceNorth);
            Add(ref buttons, source.Cross, Switch2JoyConProfileButton.FaceSouth);
            Add(ref buttons, source.Circle, Switch2JoyConProfileButton.FaceEast);
            Add(ref buttons, source.R1, Switch2JoyConProfileButton.RightShoulder);
            Add(ref buttons, source.R2Btn || source.R2 != 0, Switch2JoyConProfileButton.RightTrigger);
            Add(ref buttons, source.R3, Switch2JoyConProfileButton.RightStick);
            Add(ref buttons, source.Options, Switch2JoyConProfileButton.Start);
            Add(ref buttons, source.PS, Switch2JoyConProfileButton.Guide);
        }
        Add(ref buttons, source.SideL, left ? Switch2JoyConProfileButton.LeftRailSL : Switch2JoyConProfileButton.RightRailSL);
        Add(ref buttons, source.SideR, left ? Switch2JoyConProfileButton.LeftRailSR : Switch2JoyConProfileButton.RightRailSR);
        return buttons;
    }

    private static Switch2JoyConProfileButton ToHorizontal(Switch2JoyConProfileButton raw, bool left)
    {
        Switch2JoyConProfileButton result = raw & (Switch2JoyConProfileButton.LeftRailSL | Switch2JoyConProfileButton.LeftRailSR |
            Switch2JoyConProfileButton.RightRailSL | Switch2JoyConProfileButton.RightRailSR);
        if (left)
        {
            Add(ref result, Has(raw, Switch2JoyConProfileButton.DpadDown), Switch2JoyConProfileButton.FaceWest);
            Add(ref result, Has(raw, Switch2JoyConProfileButton.DpadUp), Switch2JoyConProfileButton.FaceNorth);
            Add(ref result, Has(raw, Switch2JoyConProfileButton.DpadRight), Switch2JoyConProfileButton.FaceSouth);
            Add(ref result, Has(raw, Switch2JoyConProfileButton.DpadLeft), Switch2JoyConProfileButton.FaceEast);
            Add(ref result, Has(raw, Switch2JoyConProfileButton.Back), Switch2JoyConProfileButton.Start);
            Add(ref result, Has(raw, Switch2JoyConProfileButton.Capture), Switch2JoyConProfileButton.Guide);
            Add(ref result, Has(raw, Switch2JoyConProfileButton.LeftStick), Switch2JoyConProfileButton.LeftStick);
        }
        else
        {
            result |= raw & (Switch2JoyConProfileButton.FaceWest | Switch2JoyConProfileButton.FaceNorth |
                Switch2JoyConProfileButton.FaceSouth | Switch2JoyConProfileButton.FaceEast | Switch2JoyConProfileButton.Start | Switch2JoyConProfileButton.Guide);
            Add(ref result, Has(raw, Switch2JoyConProfileButton.RightStick), Switch2JoyConProfileButton.LeftStick);
        }
        Add(ref result, Has(raw, left ? Switch2JoyConProfileButton.LeftRailSL : Switch2JoyConProfileButton.RightRailSL), Switch2JoyConProfileButton.LeftShoulder);
        Add(ref result, Has(raw, left ? Switch2JoyConProfileButton.LeftRailSR : Switch2JoyConProfileButton.RightRailSR), Switch2JoyConProfileButton.RightShoulder);
        Add(ref result, Has(raw, left ? Switch2JoyConProfileButton.LeftShoulder : Switch2JoyConProfileButton.RightShoulder),
            left ? Switch2JoyConProfileButton.LeftPaddle1 : Switch2JoyConProfileButton.RightPaddle1);
        Add(ref result, Has(raw, left ? Switch2JoyConProfileButton.LeftTrigger : Switch2JoyConProfileButton.RightTrigger),
            left ? Switch2JoyConProfileButton.LeftPaddle2 : Switch2JoyConProfileButton.RightPaddle2);
        return result;
    }

    private static bool Has(Switch2JoyConProfileButton value, Switch2JoyConProfileButton bit) => (value & bit) != 0;
    private static void Add(ref Switch2JoyConProfileButton value, bool down, Switch2JoyConProfileButton bit) { if (down) value |= bit; }

    private static void WriteButtons(DS4State destination, Switch2JoyConProfileButton buttons, Switch2FaceButtonLayout layout)
    {
        Switch2FaceButtonLayoutProjection.TryProject(layout, Has(buttons, Switch2JoyConProfileButton.FaceWest), Has(buttons, Switch2JoyConProfileButton.FaceNorth),
            Has(buttons, Switch2JoyConProfileButton.FaceSouth), Has(buttons, Switch2JoyConProfileButton.FaceEast),
            out destination.Square, out destination.Triangle, out destination.Cross, out destination.Circle);
        destination.DpadUp = Has(buttons, Switch2JoyConProfileButton.DpadUp);
        destination.DpadDown = Has(buttons, Switch2JoyConProfileButton.DpadDown);
        destination.DpadLeft = Has(buttons, Switch2JoyConProfileButton.DpadLeft);
        destination.DpadRight = Has(buttons, Switch2JoyConProfileButton.DpadRight);
        destination.L1 = Has(buttons, Switch2JoyConProfileButton.LeftShoulder);
        destination.R1 = Has(buttons, Switch2JoyConProfileButton.RightShoulder);
        destination.L3 = Has(buttons, Switch2JoyConProfileButton.LeftStick);
        destination.R3 = Has(buttons, Switch2JoyConProfileButton.RightStick);
        destination.L2Btn = Has(buttons, Switch2JoyConProfileButton.LeftTrigger);
        destination.R2Btn = Has(buttons, Switch2JoyConProfileButton.RightTrigger);
        destination.L2 = destination.L2Raw = destination.L2Btn ? (byte)255 : (byte)0;
        destination.R2 = destination.R2Raw = destination.R2Btn ? (byte)255 : (byte)0;
        destination.Share = Has(buttons, Switch2JoyConProfileButton.Back);
        destination.Options = Has(buttons, Switch2JoyConProfileButton.Start);
        destination.PS = Has(buttons, Switch2JoyConProfileButton.Guide);
        destination.Capture = Has(buttons, Switch2JoyConProfileButton.Capture);
    }
}
