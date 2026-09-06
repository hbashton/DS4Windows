/*
DS4Windows
Copyright (C) 2026 hbashton
This program is free software under the GNU General Public License, version 3
or (at your option) any later version. See LICENSE for details.
*/

namespace DS4Windows.Switch2;

/// <summary>
/// Owned physical-stick evidence retained across profile projection. It keeps
/// source calibration separate from application overrides, so final admission
/// can apply a newer local calibration (or reset) to an already queued frame.
/// No peer identity, transport handle, borrowed buffer or mutable object lives here.
/// </summary>
internal readonly struct Switch2RawStickObservation
{
    internal Switch2RawStickObservation(in Switch2CanonicalInputFrame frame)
    {
        if (frame.Version != Switch2CanonicalInputFrame.CurrentVersion ||
            !frame.Descriptor.IsValid || frame.CompletionTimestampQpc < 0 ||
            !frame.Calibration.IsValid || frame.Calibration.Model != frame.Model ||
            frame.Calibration.DeviceGeneration != frame.DeviceGeneration)
        {
            return;
        }
        Descriptor = frame.Descriptor;
        CompletionTimestampQpc = frame.CompletionTimestampQpc;
        CounterSequence = frame.CounterSequence;
        ReportKind = frame.Report.Kind;
        Calibration = frame.Calibration;
        HasLeft = frame.TryGetLeftStick(out var left);
        HasRight = frame.TryGetRightStick(out var right);
        Left = left.Raw;
        Right = right.Raw;
        IsValid = (HasLeft || HasRight) && HasExpectedFraming;
    }

    internal bool IsValid { get; }
    internal Switch2InputSessionDescriptor Descriptor { get; }
    internal long CompletionTimestampQpc { get; }
    internal Switch2CounterSequenceKind CounterSequence { get; }
    internal Switch2InputReportKind ReportKind { get; }
    internal Switch2InputCalibrationSnapshot Calibration { get; }
    internal bool HasLeft { get; }
    internal bool HasRight { get; }
    private Switch2StickRaw Left { get; }
    private Switch2StickRaw Right { get; }

    internal bool Matches(Switch2ControllerModel model, Switch2Transport transport,
        ulong deviceGeneration, ulong transportGeneration, long timestampQpc,
        long qpcFrequency, bool common) => IsValid && HasExpectedFraming &&
        Descriptor.Identity.Model == model && Descriptor.Identity.Transport == transport &&
        Descriptor.DeviceGeneration == deviceGeneration &&
        Descriptor.TransportGeneration == transportGeneration &&
        CompletionTimestampQpc == timestampQpc && Descriptor.QpcFrequency == qpcFrequency &&
        common == (ReportKind == Switch2InputReportKind.Common05);

    private bool HasExpectedFraming => Descriptor.Identity.ProtocolRevision switch
    {
        Switch2InputProtocolRevision.ProUsbCommon05Bcd0201 or
        Switch2InputProtocolRevision.BluetoothLeCommon05V1 =>
            ReportKind == Switch2InputReportKind.Common05,
        Switch2InputProtocolRevision.BluetoothLeJoyCon2Left07V1 =>
            ReportKind == Switch2InputReportKind.JoyCon2Left07,
        Switch2InputProtocolRevision.BluetoothLeJoyCon2Right08V1 =>
            ReportKind == Switch2InputReportKind.JoyCon2Right08,
        Switch2InputProtocolRevision.BluetoothLeProController2_09V1 =>
            ReportKind == Switch2InputReportKind.ProController2_09,
        _ => false,
    };

    internal bool TryGetStick(Switch2StickSide side,
        in Switch2LocalStickCalibrationOverrides local,
        out Switch2CalibratedStickPosition stick)
    {
        stick = default;
        if (!IsValid) return false;
        if (side == Switch2StickSide.Left && HasLeft)
        {
            stick = new Switch2CalibratedStickPosition(Left, Calibration.Left,
                local.HasLeft ? local.Left : null);
            return true;
        }
        if (side == Switch2StickSide.Right && HasRight)
        {
            stick = new Switch2CalibratedStickPosition(Right, Calibration.Right,
                local.HasRight ? local.Right : null);
            return true;
        }
        return false;
    }
}
