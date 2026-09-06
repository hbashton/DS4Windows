/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Buffers.Binary;

namespace DS4Windows.Switch2;

/// <summary>
/// The six capture-backed, payload-free USB player-LED operations admitted by
/// <see cref="Switch2UsbCommandCodec"/>. Values outside this closed list can
/// still be manufactured with an explicit CLR enum cast, so every codec entry
/// point validates the value before reading or writing bytes.
/// </summary>
public enum Switch2PlayerLedCommand : byte
{
    Player1Only = 0x01,
    Player2Only = 0x02,
    Player3Only = 0x03,
    Player4Only = 0x04,
    AllOn = 0x05,
    AllOff = 0x06,
}

/// <summary>
/// Volatile, capture-backed USB initialisation operations that are safe to
/// encode without a host address, pairing data, memory access, or firmware
/// state. This is intentionally not a general command-id/subcommand escape
/// hatch.
/// </summary>
public enum Switch2UsbInitializationStep : byte
{
    EnableUsbHidReports = 0x03,
    SelectCommonInputReport = 0x0A,
}

/// <summary>
/// The two capture-backed feature-mask operations required to declare and
/// enable a feature set. They are kept distinct so a caller cannot accidentally
/// substitute a clear/disable/configure command.
/// </summary>
public enum Switch2UsbFeatureStep : byte
{
    SetFeatureMask = 0x02,
    EnableFeatures = 0x04,
}

/// <summary>
/// Closed feature masks admitted by the USB request encoder. The value enables
/// button state, analog sticks, IMU, and rumble; unused and magnetometer bits
/// remain clear.
/// </summary>
public enum Switch2UsbFeatureMask : byte
{
    ButtonsSticksImuAndRumble = 0x27,
}

/// <summary>
/// The four read-only, per-unit stick-calibration records admitted on the
/// Switch 2 Pro USB command interface. This closed list deliberately excludes
/// the flash write, erase, block-read, and arbitrary-address command forms.
/// </summary>
public enum Switch2UsbCalibrationRead : byte
{
    Invalid = 0,
    FactoryPrimary,
    FactorySecondary,
    UserPrimary,
    UserSecondary,
}

/// <summary>
/// Strict replay-validation failures for the source-pinned command forms.
/// Header byte 4 is deliberately unnamed because its meaning is not established
/// by the capture; only its exact captured value is admitted.
/// </summary>
public enum Switch2UsbCommandFailure : byte
{
    None = 0,
    InvalidLength,
    InvalidCommand,
    InvalidDirection,
    InvalidTransport,
    InvalidSubcommand,
    UnexpectedCapturedHeaderByte4,
    InvalidRequestDataLength,
    InvalidRequestPayload,
    InvalidResponsePayload,
    InvalidAcknowledgement,
    NonzeroHeaderReserved,
    NonzeroPayloadReserved,
}

/// <summary>
/// Exact paired response-header forms observed for the narrowly allowlisted
/// battery and player-LED USB commands. The two bytes are one indivisible
/// style; this enum does not assign either byte a protocol meaning.
/// </summary>
public enum Switch2UsbCommandResponseStyle : byte
{
    OriginalCapture10_78 = 1,
    InitializedHardware00_F8 = 2,
}

/// <summary>
/// Allocation-free replay codec for narrowly allowlisted Switch 2 USB command
/// families. It performs no discovery or I/O and cannot forward arbitrary
/// commands. Bytes are pinned to observed examples rather than generalized
/// from fields whose semantics are still unknown. In particular, this codec
/// does not expose the USB-initialise form that carries a host address.
/// </summary>
public static class Switch2UsbCommandCodec
{
    private const int FourBytePayloadRequestLength = 12;

    public const int RequestLength = 8;
    public const int InitializationRequestLength =
        FourBytePayloadRequestLength;
    public const int FeatureRequestLength = FourBytePayloadRequestLength;
    public const int FeatureResponseLength = FourBytePayloadRequestLength;
    public const int InitializationAckResponseLength = 8;
    public const int EnableUsbHidReportsResponseLength = 12;
    public const int PlayerLedResponseLength = 8;
    public const int BatteryVoltageResponseLength = 12;
    public const int CalibrationReadRequestLength = 16;
    public const int CalibrationReadResponseHeaderLength = 16;

    private const byte InitializationCommandId = 0x03;
    private const byte FeatureCommandId = 0x0C;
    private const byte BatteryCommandId = 0x0B;
    private const byte BatteryVoltageSubcommand = 0x03;
    private const byte PlayerLedCommandId = 0x09;
    private const byte MemoryCommandId = 0x02;
    private const byte MemoryReadSubcommand = 0x04;
    private const byte RequestDirection = 0x91;
    private const byte ResponseDirection = 0x01;
    private const byte UsbTransport = 0x00;
    private const byte RequestHeaderByte4 = 0x00;
    private const byte RequestDataLength = 0x00;
    private const byte CapturedResponseHeaderByte4 = 0x10;
    private const byte CapturedResponseAcknowledgement = 0x78;
    private const byte CapturedInitializationHeaderByte4 = 0x00;
    private const byte CapturedInitializationAcknowledgement = 0xF8;
    private const byte CommonInputReportId = 0x05;
    private const byte MemoryReadCapturedByte = 0x7E;

    public static bool TryWriteInitializationRequest(
        Switch2UsbInitializationStep step, Span<byte> destination,
        out Switch2UsbCommandFailure failure)
    {
        if (!TryGetInitializationPayload(step, out byte payload))
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }

        return TryWriteFourBytePayloadRequest(InitializationCommandId,
            (byte)step, payload, destination, out failure);
    }

    public static bool TryValidateInitializationRequest(
        ReadOnlySpan<byte> source, Switch2UsbInitializationStep expectedStep,
        out Switch2UsbCommandFailure failure)
    {
        if (!TryGetInitializationPayload(expectedStep, out byte payload))
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }

        return TryValidateFourBytePayloadRequest(source,
            InitializationCommandId, (byte)expectedStep, payload,
            out failure);
    }

    public static bool TryGetInitializationResponseLength(
        Switch2UsbInitializationStep step, out int responseLength)
    {
        switch (step)
        {
            case Switch2UsbInitializationStep.EnableUsbHidReports:
                responseLength = EnableUsbHidReportsResponseLength;
                return true;
            case Switch2UsbInitializationStep.SelectCommonInputReport:
                responseLength = InitializationAckResponseLength;
                return true;
            default:
                responseLength = 0;
                return false;
        }
    }

    /// <summary>
    /// Validates only the two exact USB response tuples pinned for the matching
    /// initialisation request. The enable response must echo its captured
    /// four-byte payload; select-report has no response payload.
    /// </summary>
    public static bool TryValidateInitializationResponse(
        ReadOnlySpan<byte> source, Switch2UsbInitializationStep expectedStep,
        out Switch2UsbCommandFailure failure)
    {
        if (!TryGetInitializationResponseLength(expectedStep,
                out int responseLength))
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }
        if (!TryValidateResponseHeader(source, responseLength,
                InitializationCommandId, (byte)expectedStep,
                CapturedInitializationHeaderByte4,
                CapturedInitializationAcknowledgement, out failure))
        {
            return false;
        }

        if (expectedStep ==
                Switch2UsbInitializationStep.EnableUsbHidReports &&
            (source[8] != 0x01 || source[9] != 0 || source[10] != 0 ||
                source[11] != 0))
        {
            failure = Switch2UsbCommandFailure.InvalidResponsePayload;
            return false;
        }

        failure = Switch2UsbCommandFailure.None;
        return true;
    }

    /// <summary>
    /// Encodes the independently reproduced USB request form for the closed
    /// 0x27 feature mask.
    /// </summary>
    public static bool TryWriteFeatureRequest(Switch2UsbFeatureStep step,
        Switch2UsbFeatureMask mask, Span<byte> destination,
        out Switch2UsbCommandFailure failure)
    {
        if (!IsSupportedFeatureStep(step))
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }
        if (!IsSupportedFeatureMask(mask))
        {
            failure = Switch2UsbCommandFailure.InvalidRequestPayload;
            return false;
        }

        return TryWriteFourBytePayloadRequest(FeatureCommandId, (byte)step,
            (byte)mask, destination, out failure);
    }

    public static bool TryValidateFeatureRequest(ReadOnlySpan<byte> source,
        Switch2UsbFeatureStep expectedStep, Switch2UsbFeatureMask expectedMask,
        out Switch2UsbCommandFailure failure)
    {
        if (!IsSupportedFeatureStep(expectedStep))
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }
        if (!IsSupportedFeatureMask(expectedMask))
        {
            failure = Switch2UsbCommandFailure.InvalidRequestPayload;
            return false;
        }

        return TryValidateFourBytePayloadRequest(source, FeatureCommandId,
            (byte)expectedStep, (byte)expectedMask, out failure);
    }

    /// <summary>
    /// Validates the exact 12-byte response tuples captured from the admitted
    /// 057E:2069, bcdDevice 0x0201 USB lifetime for the matching feature
    /// request. The response payload is four captured zero bytes; no wider
    /// feature-command family or transport is inferred from this tuple.
    /// </summary>
    public static bool TryValidateFeatureResponse(ReadOnlySpan<byte> source,
        Switch2UsbFeatureStep expectedStep,
        out Switch2UsbCommandFailure failure)
    {
        if (!IsSupportedFeatureStep(expectedStep))
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }
        if (!TryValidateResponseHeader(source, FeatureResponseLength,
                FeatureCommandId, (byte)expectedStep,
                CapturedInitializationHeaderByte4,
                CapturedInitializationAcknowledgement, out failure))
        {
            return false;
        }
        if (source[8] != 0 || source[9] != 0 || source[10] != 0 ||
            source[11] != 0)
        {
            failure = Switch2UsbCommandFailure.NonzeroPayloadReserved;
            return false;
        }

        failure = Switch2UsbCommandFailure.None;
        return true;
    }

    public static bool TryWriteGetBatteryVoltageRequest(
        Span<byte> destination, out Switch2UsbCommandFailure failure) =>
        TryWriteRequest(BatteryCommandId, BatteryVoltageSubcommand,
            destination, out failure);

    public static bool TryValidateGetBatteryVoltageRequest(
        ReadOnlySpan<byte> source, out Switch2UsbCommandFailure failure) =>
        TryValidateRequest(source, BatteryCommandId,
            BatteryVoltageSubcommand, out failure);

    /// <summary>
    /// Returns the raw little-endian 16-bit voltage field. The two following
    /// payload bytes must retain the zeros in the pinned capture.
    /// </summary>
    public static bool TryParseGetBatteryVoltageResponse(
        ReadOnlySpan<byte> source, out ushort rawVoltage,
        out Switch2UsbCommandFailure failure) =>
        TryParseGetBatteryVoltageResponse(source, out rawVoltage, out _,
            out failure);

    public static bool TryParseGetBatteryVoltageResponse(
        ReadOnlySpan<byte> source, out ushort rawVoltage,
        out Switch2UsbCommandResponseStyle responseStyle,
        out Switch2UsbCommandFailure failure)
    {
        if (!TryValidateCapturedOrInitializedResponseHeader(source,
                BatteryVoltageResponseLength, BatteryCommandId,
                BatteryVoltageSubcommand, out responseStyle, out failure))
        {
            rawVoltage = 0;
            return false;
        }

        if (source[10] != 0 || source[11] != 0)
        {
            rawVoltage = 0;
            responseStyle = default;
            failure = Switch2UsbCommandFailure.NonzeroPayloadReserved;
            return false;
        }

        rawVoltage = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(8,
            sizeof(ushort)));
        return true;
    }

    public static bool TryWritePlayerLedRequest(
        Switch2PlayerLedCommand command, Span<byte> destination,
        out Switch2UsbCommandFailure failure)
    {
        if (!IsSupportedPlayerLedCommand(command))
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }

        return TryWriteRequest(PlayerLedCommandId, (byte)command, destination,
            out failure);
    }

    /// <summary>
    /// Validates the exact request tuple for the operation the caller intended.
    /// A different otherwise-allowlisted LED subcommand is a mismatch, not a
    /// substitute operation.
    /// </summary>
    public static bool TryValidatePlayerLedRequest(ReadOnlySpan<byte> source,
        Switch2PlayerLedCommand expectedCommand,
        out Switch2UsbCommandFailure failure)
    {
        if (!IsSupportedPlayerLedCommand(expectedCommand))
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }

        return TryValidateRequest(source, PlayerLedCommandId,
            (byte)expectedCommand, out failure);
    }

    public static bool TryDecodePlayerLedRequest(ReadOnlySpan<byte> source,
        out Switch2PlayerLedCommand command,
        out Switch2UsbCommandFailure failure)
    {
        command = default;
        if (source.Length != RequestLength)
        {
            failure = Switch2UsbCommandFailure.InvalidLength;
            return false;
        }

        var candidate = (Switch2PlayerLedCommand)source[3];
        if (!IsSupportedPlayerLedCommand(candidate))
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }
        if (!TryValidatePlayerLedRequest(source, candidate, out failure))
        {
            return false;
        }

        command = candidate;
        return true;
    }

    /// <summary>
    /// Validates the exact captured ACK tuple for the operation the caller sent.
    /// No response payload is admitted for these six commands.
    /// </summary>
    public static bool TryValidatePlayerLedResponse(ReadOnlySpan<byte> source,
        Switch2PlayerLedCommand expectedCommand,
        out Switch2UsbCommandFailure failure) =>
        TryValidatePlayerLedResponse(source, expectedCommand, out _,
            out failure);

    public static bool TryValidatePlayerLedResponse(ReadOnlySpan<byte> source,
        Switch2PlayerLedCommand expectedCommand,
        out Switch2UsbCommandResponseStyle responseStyle,
        out Switch2UsbCommandFailure failure)
    {
        if (!IsSupportedPlayerLedCommand(expectedCommand))
        {
            responseStyle = default;
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }

        return TryValidateCapturedOrInitializedResponseHeader(source,
            PlayerLedResponseLength, PlayerLedCommandId,
            (byte)expectedCommand, out responseStyle, out failure);
    }

    /// <summary>
    /// Encodes only one of the four allowlisted Pro USB calibration reads.
    /// The 16-byte form and USB transport byte are independently reproduced
    /// by current SDL and hid-nintendo2 implementations.
    /// </summary>
    public static bool TryWriteCalibrationReadRequest(
        Switch2UsbCalibrationRead read, Span<byte> destination,
        out Switch2UsbCommandFailure failure)
    {
        if (!TryGetCalibrationReadMetadata(read, out uint address,
                out byte length))
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }
        if (destination.Length != CalibrationReadRequestLength)
        {
            failure = Switch2UsbCommandFailure.InvalidLength;
            return false;
        }

        destination.Clear();
        destination[0] = MemoryCommandId;
        destination[1] = RequestDirection;
        destination[2] = UsbTransport;
        destination[3] = MemoryReadSubcommand;
        destination[5] = 0x08;
        destination[8] = length;
        destination[9] = MemoryReadCapturedByte;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4),
            address);
        failure = Switch2UsbCommandFailure.None;
        return true;
    }

    public static bool TryValidateCalibrationReadRequest(
        ReadOnlySpan<byte> source, Switch2UsbCalibrationRead expectedRead,
        out Switch2UsbCommandFailure failure)
    {
        if (!TryGetCalibrationReadMetadata(expectedRead, out uint address,
                out byte length))
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }
        if (!TryValidateHeaderPrefix(source, CalibrationReadRequestLength,
                MemoryCommandId, RequestDirection, MemoryReadSubcommand,
                RequestHeaderByte4, out failure))
        {
            return false;
        }
        if (source[5] != 0x08)
        {
            failure = Switch2UsbCommandFailure.InvalidRequestDataLength;
            return false;
        }
        if (!TryValidateHeaderReserved(source, out failure))
        {
            return false;
        }
        if (source[8] != length || source[9] != MemoryReadCapturedByte ||
            source[10] != 0 || source[11] != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12, 4)) !=
                address)
        {
            failure = Switch2UsbCommandFailure.InvalidRequestPayload;
            return false;
        }

        failure = Switch2UsbCommandFailure.None;
        return true;
    }

    public static bool TryGetCalibrationReadResponseLength(
        Switch2UsbCalibrationRead read, out int responseLength)
    {
        if (!TryGetCalibrationReadMetadata(read, out _, out byte length))
        {
            responseLength = 0;
            return false;
        }

        responseLength = CalibrationReadResponseHeaderLength + length;
        return true;
    }

    /// <summary>
    /// Validates either observed USB response tuple, the exact echoed
    /// length/address tuple, and copies only the requested calibration
    /// payload. Original captures use 10/78 while initialized bcdDevice 0201
    /// hardware has been measured using 00/F8. The destination must be exactly
    /// the record length so no trailing native-buffer bytes escape.
    /// </summary>
    public static bool TryCopyCalibrationReadResponse(
        ReadOnlySpan<byte> source, Switch2UsbCalibrationRead expectedRead,
        Span<byte> destination, out Switch2UsbCommandFailure failure)
    {
        if (!TryGetCalibrationReadMetadata(expectedRead, out uint address,
                out byte length))
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }
        int responseLength = CalibrationReadResponseHeaderLength + length;
        if (destination.Length != length)
        {
            failure = Switch2UsbCommandFailure.InvalidLength;
            return false;
        }
        if (!TryValidateCapturedOrInitializedResponseHeader(source,
                responseLength, MemoryCommandId, MemoryReadSubcommand,
                out _, out failure))
        {
            return false;
        }
        if (source[8] != length || source[9] != 0 || source[10] != 0 ||
            source[11] != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12, 4)) !=
                address)
        {
            failure = Switch2UsbCommandFailure.InvalidResponsePayload;
            return false;
        }

        source.Slice(CalibrationReadResponseHeaderLength, length).CopyTo(
            destination);
        failure = Switch2UsbCommandFailure.None;
        return true;
    }

    private static bool TryWriteRequest(byte command, byte subcommand,
        Span<byte> destination, out Switch2UsbCommandFailure failure)
    {
        if (destination.Length != RequestLength)
        {
            failure = Switch2UsbCommandFailure.InvalidLength;
            return false;
        }

        destination[0] = command;
        destination[1] = RequestDirection;
        destination[2] = UsbTransport;
        destination[3] = subcommand;
        destination[4] = RequestHeaderByte4;
        destination[5] = RequestDataLength;
        destination[6] = 0;
        destination[7] = 0;
        failure = Switch2UsbCommandFailure.None;
        return true;
    }

    private static bool TryValidateRequest(ReadOnlySpan<byte> source,
        byte command, byte subcommand,
        out Switch2UsbCommandFailure failure)
    {
        if (!TryValidateHeaderPrefix(source, RequestLength, command,
                RequestDirection, subcommand, RequestHeaderByte4, out failure))
        {
            return false;
        }

        if (source[5] != RequestDataLength)
        {
            failure = Switch2UsbCommandFailure.InvalidRequestDataLength;
            return false;
        }

        return TryValidateHeaderReserved(source, out failure);
    }

    private static bool TryValidateResponseHeader(ReadOnlySpan<byte> source,
        int expectedLength, byte command, byte subcommand,
        out Switch2UsbCommandFailure failure)
    {
        return TryValidateResponseHeader(source, expectedLength, command,
            subcommand, CapturedResponseHeaderByte4,
            CapturedResponseAcknowledgement, out failure);
    }

    private static bool TryValidateResponseHeader(ReadOnlySpan<byte> source,
        int expectedLength, byte command, byte subcommand,
        byte capturedHeaderByte4, byte capturedAcknowledgement,
        out Switch2UsbCommandFailure failure)
    {
        if (!TryValidateHeaderPrefix(source, expectedLength, command,
                ResponseDirection, subcommand, capturedHeaderByte4,
                out failure))
        {
            return false;
        }

        if (source[5] != capturedAcknowledgement)
        {
            failure = Switch2UsbCommandFailure.InvalidAcknowledgement;
            return false;
        }

        return TryValidateHeaderReserved(source, out failure);
    }

    private static bool TryValidateCapturedOrInitializedResponseHeader(
        ReadOnlySpan<byte> source, int expectedLength, byte command,
        byte subcommand, out Switch2UsbCommandResponseStyle responseStyle,
        out Switch2UsbCommandFailure failure)
    {
        responseStyle = default;
        if (source.Length != expectedLength)
        {
            failure = Switch2UsbCommandFailure.InvalidLength;
            return false;
        }
        if (source[0] != command)
        {
            failure = Switch2UsbCommandFailure.InvalidCommand;
            return false;
        }
        if (source[1] != ResponseDirection)
        {
            failure = Switch2UsbCommandFailure.InvalidDirection;
            return false;
        }
        if (source[2] != UsbTransport)
        {
            failure = Switch2UsbCommandFailure.InvalidTransport;
            return false;
        }
        if (source[3] != subcommand)
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }

        bool originalCaptureTuple =
            source[4] == CapturedResponseHeaderByte4 &&
            source[5] == CapturedResponseAcknowledgement;
        bool initializedHardwareTuple =
            source[4] == CapturedInitializationHeaderByte4 &&
            source[5] == CapturedInitializationAcknowledgement;
        if (!originalCaptureTuple && !initializedHardwareTuple)
        {
            failure = source[4] is not (CapturedResponseHeaderByte4 or
                    CapturedInitializationHeaderByte4)
                ? Switch2UsbCommandFailure.UnexpectedCapturedHeaderByte4
                : Switch2UsbCommandFailure.InvalidAcknowledgement;
            return false;
        }

        if (!TryValidateHeaderReserved(source, out failure))
        {
            return false;
        }
        responseStyle = originalCaptureTuple ?
            Switch2UsbCommandResponseStyle.OriginalCapture10_78 :
            Switch2UsbCommandResponseStyle.InitializedHardware00_F8;
        return true;
    }

    private static bool TryWriteFourBytePayloadRequest(byte command,
        byte subcommand, byte payload, Span<byte> destination,
        out Switch2UsbCommandFailure failure)
    {
        if (destination.Length != FourBytePayloadRequestLength)
        {
            failure = Switch2UsbCommandFailure.InvalidLength;
            return false;
        }

        destination[0] = command;
        destination[1] = RequestDirection;
        destination[2] = UsbTransport;
        destination[3] = subcommand;
        destination[4] = RequestHeaderByte4;
        destination[5] = 0x04;
        destination[6] = 0;
        destination[7] = 0;
        destination[8] = payload;
        destination[9] = 0;
        destination[10] = 0;
        destination[11] = 0;
        failure = Switch2UsbCommandFailure.None;
        return true;
    }

    private static bool TryValidateFourBytePayloadRequest(
        ReadOnlySpan<byte> source, byte command, byte subcommand,
        byte expectedPayload, out Switch2UsbCommandFailure failure)
    {
        if (!TryValidateHeaderPrefix(source, FourBytePayloadRequestLength,
                command, RequestDirection, subcommand, RequestHeaderByte4,
                out failure))
        {
            return false;
        }
        if (source[5] != 0x04)
        {
            failure = Switch2UsbCommandFailure.InvalidRequestDataLength;
            return false;
        }
        if (!TryValidateHeaderReserved(source, out failure))
        {
            return false;
        }
        if (source[8] != expectedPayload || source[9] != 0 ||
            source[10] != 0 || source[11] != 0)
        {
            failure = Switch2UsbCommandFailure.InvalidRequestPayload;
            return false;
        }

        failure = Switch2UsbCommandFailure.None;
        return true;
    }

    private static bool TryValidateHeaderPrefix(ReadOnlySpan<byte> source,
        int expectedLength, byte command, byte direction, byte subcommand,
        byte capturedHeaderByte4, out Switch2UsbCommandFailure failure)
    {
        if (source.Length != expectedLength)
        {
            failure = Switch2UsbCommandFailure.InvalidLength;
            return false;
        }
        if (source[0] != command)
        {
            failure = Switch2UsbCommandFailure.InvalidCommand;
            return false;
        }
        if (source[1] != direction)
        {
            failure = Switch2UsbCommandFailure.InvalidDirection;
            return false;
        }
        if (source[2] != UsbTransport)
        {
            failure = Switch2UsbCommandFailure.InvalidTransport;
            return false;
        }
        if (source[3] != subcommand)
        {
            failure = Switch2UsbCommandFailure.InvalidSubcommand;
            return false;
        }
        if (source[4] != capturedHeaderByte4)
        {
            failure =
                Switch2UsbCommandFailure.UnexpectedCapturedHeaderByte4;
            return false;
        }

        failure = Switch2UsbCommandFailure.None;
        return true;
    }

    private static bool TryValidateHeaderReserved(ReadOnlySpan<byte> source,
        out Switch2UsbCommandFailure failure)
    {
        if (source[6] != 0 || source[7] != 0)
        {
            failure = Switch2UsbCommandFailure.NonzeroHeaderReserved;
            return false;
        }

        failure = Switch2UsbCommandFailure.None;
        return true;
    }

    private static bool IsSupportedPlayerLedCommand(
        Switch2PlayerLedCommand command) =>
        (byte)command is >= (byte)Switch2PlayerLedCommand.Player1Only and
            <= (byte)Switch2PlayerLedCommand.AllOff;

    private static bool TryGetInitializationPayload(
        Switch2UsbInitializationStep step, out byte payload)
    {
        switch (step)
        {
            case Switch2UsbInitializationStep.EnableUsbHidReports:
                payload = 0x01;
                return true;
            case Switch2UsbInitializationStep.SelectCommonInputReport:
                payload = CommonInputReportId;
                return true;
            default:
                payload = 0;
                return false;
        }
    }

    private static bool IsSupportedFeatureStep(Switch2UsbFeatureStep step) =>
        step is Switch2UsbFeatureStep.SetFeatureMask or
            Switch2UsbFeatureStep.EnableFeatures;

    private static bool IsSupportedFeatureMask(
        Switch2UsbFeatureMask mask) =>
        mask == Switch2UsbFeatureMask.ButtonsSticksImuAndRumble;

    private static bool TryGetCalibrationReadMetadata(
        Switch2UsbCalibrationRead read, out uint address, out byte length)
    {
        switch (read)
        {
            case Switch2UsbCalibrationRead.FactoryPrimary:
                address = Switch2CalibrationCodec.
                    PrimaryFactoryStickAddress;
                length = Switch2CalibrationCodec.StickCalibrationLength;
                return true;
            case Switch2UsbCalibrationRead.FactorySecondary:
                address = Switch2CalibrationCodec.
                    SecondaryFactoryStickAddress;
                length = Switch2CalibrationCodec.StickCalibrationLength;
                return true;
            case Switch2UsbCalibrationRead.UserPrimary:
                address = Switch2CalibrationCodec.PrimaryUserStickAddress;
                length = Switch2CalibrationCodec.UserStickCalibrationLength;
                return true;
            case Switch2UsbCalibrationRead.UserSecondary:
                address = Switch2CalibrationCodec.SecondaryUserStickAddress;
                length = Switch2CalibrationCodec.UserStickCalibrationLength;
                return true;
            default:
                address = 0;
                length = 0;
                return false;
        }
    }
}
