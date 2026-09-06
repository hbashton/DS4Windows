/*
DS4Windows
Copyright (C) 2026 hbashton

Calibration workflow adapted from Switch2Connect, Copyright (C) 2026 TommyWabg,
src/gui.py JoystickCalibrationWizard at 61ac6642ce12fe7217e38a860b14863b18ca7e28.
This program is free software under the GNU General Public License, version 3
or (at your option) any later version. See LICENSE for details.
*/

using System;

namespace DS4Windows.Switch2;

internal enum Switch2RawStickCalibrationStage : byte
{
    Rotate,
    Settle,
    Center,
    Ready,
    InsufficientTravel,
    Cancelled,
}

/// <summary>
/// Bounded PC-side calibration of one physical stick. The owner supplies only
/// admitted canonical frames from this exact transport lifetime and serializes
/// Observe/Cancel. This collector has no controller command or persistence API.
/// It retains raw 12-bit extrema and sums, never report objects or sample lists.
/// </summary>
internal sealed class Switch2RawStickCalibrationCollector
{
    internal const double RotationSeconds = 10;
    internal const double SettleSeconds = 2;
    internal const double CenterSeconds = 3;
    internal const int MovementThreshold = 10;
    internal const int TouchThreshold = 45;
    // Application quality gate, not a claim about Nintendo factory limits.
    internal const int MinimumTravel = 256;
    private const double ObservationSeconds = 0.05;
    private const double MaximumGapSeconds = 0.1;
    private const int MinimumCenterSamples = 30;

    private readonly Switch2InputSessionDescriptor descriptor;
    private bool hasTimestamp;
    private long previousQpc, observationQpc;
    private Switch2StickRaw previous, anchor;
    private ushort minX = 4095, minY = 4095, maxX, maxY;
    private double rotationTime, stationaryTime;
    private ulong centerXSum, centerYSum;
    private int centerSamples;
    private Switch2StickCalibration result;

    internal Switch2RawStickCalibrationCollector(
        in Switch2InputSessionDescriptor descriptor,
        Switch2PersistentPeerId peer, Switch2StickSide side)
    {
        if (!descriptor.IsValid || !peer.IsValid ||
            !SupportsSide(descriptor.Identity.Model, side))
            throw new ArgumentException("Calibration requires one exact physical stick lifetime and peer.");
        this.descriptor = descriptor;
        Peer = peer;
        Side = side;
    }

    internal Switch2PersistentPeerId Peer { get; }
    internal Switch2StickSide Side { get; }
    internal Switch2RawStickCalibrationStage Stage { get; private set; }
    internal double RotationProgress => Math.Min(1, rotationTime / RotationSeconds);
    internal double StationaryProgress => Stage switch
    {
        Switch2RawStickCalibrationStage.Settle => Math.Min(1, stationaryTime / SettleSeconds),
        Switch2RawStickCalibrationStage.Center => Math.Min(1, stationaryTime / CenterSeconds),
        Switch2RawStickCalibrationStage.Ready => 1,
        _ => 0,
    };

    internal bool TryObserve(in Switch2CanonicalInputFrame frame) =>
        TryObserveRaw(new Switch2RawStickObservation(frame));

    internal bool TryObserveRaw(in Switch2RawStickObservation frame)
    {
        if (IsTerminal || !frame.IsValid ||
            !frame.Descriptor.Equals(descriptor) || frame.CompletionTimestampQpc < 0 ||
            (hasTimestamp && frame.CompletionTimestampQpc <= previousQpc) ||
            (frame.CounterSequence == Switch2CounterSequenceKind.BackwardOrOutOfOrder &&
                !Switch2CounterSequence.UsesArrivalOrdering(frame.Descriptor.Identity.Model,
                    frame.Descriptor.Identity.Transport, frame.ReportKind)))
            return false;

        bool hasStick = frame.TryGetStick(Side, default, out var stick);
        if (!hasStick || stick.Raw.X > 4095 || stick.Raw.Y > 4095)
            return false;

        Switch2StickRaw raw = stick.Raw;
        if (Stage == Switch2RawStickCalibrationStage.Rotate)
        {
            minX = Math.Min(minX, raw.X); maxX = Math.Max(maxX, raw.X);
            minY = Math.Min(minY, raw.Y); maxY = Math.Max(maxY, raw.Y);
        }

        long now = frame.CompletionTimestampQpc;
        if (!hasTimestamp)
        {
            hasTimestamp = true;
            previousQpc = observationQpc = now;
            previous = anchor = raw;
            return true;
        }
        double frameInterval = (now - previousQpc) / (double)descriptor.QpcFrequency;
        previousQpc = now;
        double elapsed = (now - observationQpc) / (double)descriptor.QpcFrequency;
        if (frameInterval > MaximumGapSeconds || elapsed > MaximumGapSeconds)
        {
            // A pause earns no elapsed calibration time. Stationary evidence
            // must be continuous; earlier rotation extrema remain useful.
            if (Stage != Switch2RawStickCalibrationStage.Rotate)
                ResetStationary(raw);
            previous = anchor = raw;
            observationQpc = now;
            return true;
        }

        // Observe touch on every admitted raw report, including excursions
        // between UI-cadence samples that return to the same position.
        if (Stage != Switch2RawStickCalibrationStage.Rotate &&
            Distance(raw, anchor) >= TouchThreshold)
        {
            ResetStationary(raw);
            previous = raw;
            observationQpc = now;
            return true;
        }
        if (elapsed < ObservationSeconds)
            return true;

        observationQpc = now;
        bool moved = Distance(raw, previous) >= MovementThreshold;
        previous = raw;
        switch (Stage)
        {
            case Switch2RawStickCalibrationStage.Rotate:
                // Match the reference's ~20 Hz movement comparison. Comparing
                // its 10-count threshold at 250/500 Hz would be rate-dependent.
                if (moved) rotationTime += elapsed;
                if (rotationTime + 1e-9 >= RotationSeconds)
                    ResetStationary(raw);
                break;
            case Switch2RawStickCalibrationStage.Settle:
            case Switch2RawStickCalibrationStage.Center:
                if (moved)
                {
                    ResetStationary(raw);
                    break;
                }
                stationaryTime += elapsed;
                if (Stage == Switch2RawStickCalibrationStage.Settle)
                {
                    if (stationaryTime + 1e-9 >= SettleSeconds)
                    {
                        Stage = Switch2RawStickCalibrationStage.Center;
                        stationaryTime = 0;
                    }
                }
                else
                {
                    centerXSum += raw.X; centerYSum += raw.Y;
                    centerSamples++;
                    if (stationaryTime + 1e-9 >= CenterSeconds &&
                        centerSamples >= MinimumCenterSamples)
                        Finish();
                }
                break;
        }
        return true;
    }

    internal bool TryGetResult(out Switch2StickCalibration calibration)
    {
        calibration = Stage == Switch2RawStickCalibrationStage.Ready ? result : default;
        return Stage == Switch2RawStickCalibrationStage.Ready;
    }

    internal void Cancel()
    {
        Stage = Switch2RawStickCalibrationStage.Cancelled;
        result = default;
        centerXSum = centerYSum = 0;
        centerSamples = 0;
    }

    private bool IsTerminal => Stage is Switch2RawStickCalibrationStage.Ready or
        Switch2RawStickCalibrationStage.InsufficientTravel or Switch2RawStickCalibrationStage.Cancelled;

    private void ResetStationary(in Switch2StickRaw raw)
    {
        Stage = Switch2RawStickCalibrationStage.Settle;
        anchor = raw;
        stationaryTime = 0;
        centerXSum = centerYSum = 0;
        centerSamples = 0;
    }

    private void Finish()
    {
        // Bounded sample count: at most ~60 samples in a continuous 3-second
        // window. Midpoint-to-even matches Python round in the source wizard.
        ushort x = (ushort)Math.Round(centerXSum / (double)centerSamples);
        ushort y = (ushort)Math.Round(centerYSum / (double)centerSamples);
        if (x - minX < MinimumTravel || maxX - x < MinimumTravel ||
            y - minY < MinimumTravel || maxY - y < MinimumTravel)
        {
            Stage = Switch2RawStickCalibrationStage.InsufficientTravel;
            return;
        }
        var candidate = new Switch2StickCalibration(x, y,
            (ushort)(maxX - x), (ushort)(maxY - y),
            (ushort)(x - minX), (ushort)(y - minY));
        if (!Switch2CalibrationCodec.TryValidateAdoptable(candidate, out _))
        {
            Stage = Switch2RawStickCalibrationStage.InsufficientTravel;
            return;
        }
        result = candidate;
        Stage = Switch2RawStickCalibrationStage.Ready;
    }

    private static int Distance(in Switch2StickRaw a, in Switch2StickRaw b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    internal static bool SupportsSide(Switch2ControllerModel model, Switch2StickSide side) =>
        (side is Switch2StickSide.Left or Switch2StickSide.Right) && (model switch
        {
            Switch2ControllerModel.ProController2 => true,
            Switch2ControllerModel.JoyCon2Left => side == Switch2StickSide.Left,
            Switch2ControllerModel.JoyCon2Right => side == Switch2StickSide.Right,
            _ => false,
        });
}
