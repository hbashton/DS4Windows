/*
DS4Windows
Copyright (C) 2026 hbashton
SPDX-License-Identifier: GPL-3.0-or-later
*/

using System;
using System.Buffers.Binary;

namespace DS4Windows.Sunshine;

/// <summary>
/// Fixed-size local IPC record shared with the Vibeshine Windows input
/// backend. The packet preserves the fields Sunshine actually receives from
/// Moonlight; it is not a physical USB/Bluetooth HID report.
/// </summary>
internal readonly struct SunshineControllerBridgePacket
{
    internal const int RecordLength = 64;
    internal const int HeaderLength = 24;
    internal const int PayloadLength = RecordLength - HeaderLength;
    internal const uint Magic = 0x42435356; // ASCII "VSCB" on little endian.
    internal const ushort Version = 1;

    internal SunshineControllerBridgePacket(
        SunshineControllerBridgePacketKind kind, ulong sessionId,
        ushort controllerId, byte clientIndex, uint sequence,
        uint buttons = 0, byte leftTrigger = 0, byte rightTrigger = 0,
        short leftX = 0, short leftY = 0, short rightX = 0,
        short rightY = 0, byte controllerType = 0,
        ushort capabilities = 0, uint supportedButtons = 0,
        byte motionType = 0, float x = 0, float y = 0, float z = 0,
        byte touchEvent = 0, uint pointerId = 0, float pressure = 0,
        byte batteryState = 0, byte batteryPercent = 0,
        byte feedbackKind = 0, byte lowRumble = 0,
        byte highRumble = 0, byte red = 0, byte green = 0,
        byte blue = 0, ushort motionRate = 0,
        byte triggerFlags = 0, byte leftTriggerType = 0,
        byte rightTriggerType = 0,
        ReadOnlySpan<byte> leftTriggerData = default,
        ReadOnlySpan<byte> rightTriggerData = default)
    {
        Kind = kind;
        SessionId = sessionId;
        ControllerId = controllerId;
        ClientIndex = clientIndex;
        Sequence = sequence;
        Buttons = buttons;
        LeftTrigger = leftTrigger;
        RightTrigger = rightTrigger;
        LeftX = leftX;
        LeftY = leftY;
        RightX = rightX;
        RightY = rightY;
        ControllerType = controllerType;
        Capabilities = capabilities;
        SupportedButtons = supportedButtons;
        MotionType = motionType;
        X = x;
        Y = y;
        Z = z;
        TouchEvent = touchEvent;
        PointerId = pointerId;
        Pressure = pressure;
        BatteryState = batteryState;
        BatteryPercent = batteryPercent;
        FeedbackKind = feedbackKind;
        LowRumble = lowRumble;
        HighRumble = highRumble;
        Red = red;
        Green = green;
        Blue = blue;
        MotionRate = motionRate;
        TriggerFlags = triggerFlags;
        LeftTriggerType = leftTriggerType;
        RightTriggerType = rightTriggerType;
        LeftTriggerData = CopyTrigger(leftTriggerData);
        RightTriggerData = CopyTrigger(rightTriggerData);
    }

    internal SunshineControllerBridgePacketKind Kind { get; }
    internal ulong SessionId { get; }
    internal ushort ControllerId { get; }
    internal byte ClientIndex { get; }
    internal uint Sequence { get; }
    internal uint Buttons { get; }
    internal byte LeftTrigger { get; }
    internal byte RightTrigger { get; }
    internal short LeftX { get; }
    internal short LeftY { get; }
    internal short RightX { get; }
    internal short RightY { get; }
    internal byte ControllerType { get; }
    internal ushort Capabilities { get; }
    internal uint SupportedButtons { get; }
    internal byte MotionType { get; }
    internal float X { get; }
    internal float Y { get; }
    internal float Z { get; }
    internal byte TouchEvent { get; }
    internal uint PointerId { get; }
    internal float Pressure { get; }
    internal byte BatteryState { get; }
    internal byte BatteryPercent { get; }
    internal byte FeedbackKind { get; }
    internal byte LowRumble { get; }
    internal byte HighRumble { get; }
    internal byte Red { get; }
    internal byte Green { get; }
    internal byte Blue { get; }
    internal ushort MotionRate { get; }
    internal byte TriggerFlags { get; }
    internal byte LeftTriggerType { get; }
    internal byte RightTriggerType { get; }
    internal byte[] LeftTriggerData { get; }
    internal byte[] RightTriggerData { get; }

    internal void Write(Span<byte> destination)
    {
        if (destination.Length < RecordLength)
            throw new ArgumentException("A bridge record needs 64 bytes.", nameof(destination));

        Span<byte> record = destination[..RecordLength];
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], Version);
        record[6] = (byte)Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(record[8..], ControllerId);
        record[10] = ClientIndex;
        BinaryPrimitives.WriteUInt32LittleEndian(record[12..], Sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(record[16..], SessionId);

        Span<byte> payload = record[HeaderLength..];
        switch (Kind)
        {
            case SunshineControllerBridgePacketKind.Hello:
            case SunshineControllerBridgePacketKind.HelloAccepted:
            case SunshineControllerBridgePacketKind.Remove:
                break;
            case SunshineControllerBridgePacketKind.Arrival:
                payload[0] = ControllerType;
                BinaryPrimitives.WriteUInt16LittleEndian(payload[1..], Capabilities);
                BinaryPrimitives.WriteUInt32LittleEndian(payload[3..], SupportedButtons);
                break;
            case SunshineControllerBridgePacketKind.State:
                BinaryPrimitives.WriteUInt32LittleEndian(payload, Buttons);
                payload[4] = LeftTrigger;
                payload[5] = RightTrigger;
                BinaryPrimitives.WriteInt16LittleEndian(payload[6..], LeftX);
                BinaryPrimitives.WriteInt16LittleEndian(payload[8..], LeftY);
                BinaryPrimitives.WriteInt16LittleEndian(payload[10..], RightX);
                BinaryPrimitives.WriteInt16LittleEndian(payload[12..], RightY);
                break;
            case SunshineControllerBridgePacketKind.Motion:
                payload[0] = MotionType;
                WriteSingle(payload[4..], X);
                WriteSingle(payload[8..], Y);
                WriteSingle(payload[12..], Z);
                break;
            case SunshineControllerBridgePacketKind.Touch:
                payload[0] = TouchEvent;
                BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], PointerId);
                WriteSingle(payload[8..], X);
                WriteSingle(payload[12..], Y);
                WriteSingle(payload[16..], Pressure);
                break;
            case SunshineControllerBridgePacketKind.Battery:
                payload[0] = BatteryState;
                payload[1] = BatteryPercent;
                break;
            case SunshineControllerBridgePacketKind.Feedback:
                payload[0] = FeedbackKind;
                payload[1] = LowRumble;
                payload[2] = HighRumble;
                payload[3] = Red;
                payload[4] = Green;
                payload[5] = Blue;
                BinaryPrimitives.WriteUInt16LittleEndian(payload[6..], MotionRate);
                payload[8] = MotionType;
                payload[9] = TriggerFlags;
                payload[10] = LeftTriggerType;
                payload[11] = RightTriggerType;
                if (LeftTriggerData is { Length: 10 })
                    LeftTriggerData.CopyTo(payload[12..22]);
                if (RightTriggerData is { Length: 10 })
                    RightTriggerData.CopyTo(payload[22..32]);
                break;
            default:
                throw new InvalidOperationException("Unknown Sunshine bridge packet kind.");
        }
    }

    internal static bool TryRead(ReadOnlySpan<byte> record,
        out SunshineControllerBridgePacket packet)
    {
        packet = default;
        if (record.Length != RecordLength ||
            BinaryPrimitives.ReadUInt32LittleEndian(record) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != Version ||
            record[7] != 0 || record[11] != 0)
            return false;

        var kind = (SunshineControllerBridgePacketKind)record[6];
        if (!Enum.IsDefined(kind)) return false;
        ushort controllerId = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
        if (controllerId >= 16) return false;

        ReadOnlySpan<byte> payload = record[HeaderLength..];
        ulong sessionId = BinaryPrimitives.ReadUInt64LittleEndian(record[16..]);
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        byte clientIndex = record[10];
        switch (kind)
        {
            case SunshineControllerBridgePacketKind.Hello:
            case SunshineControllerBridgePacketKind.HelloAccepted:
            case SunshineControllerBridgePacketKind.Remove:
                if (HasNonZero(payload)) return false;
                packet = new(kind, sessionId, controllerId, clientIndex, sequence);
                return true;
            case SunshineControllerBridgePacketKind.Arrival:
                if (HasNonZero(payload[7..])) return false;
                packet = new(kind, sessionId, controllerId, clientIndex, sequence,
                    controllerType: payload[0],
                    capabilities: BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]),
                    supportedButtons: BinaryPrimitives.ReadUInt32LittleEndian(payload[3..]));
                return true;
            case SunshineControllerBridgePacketKind.State:
                if (HasNonZero(payload[14..])) return false;
                packet = new(kind, sessionId, controllerId, clientIndex, sequence,
                    buttons: BinaryPrimitives.ReadUInt32LittleEndian(payload),
                    leftTrigger: payload[4], rightTrigger: payload[5],
                    leftX: BinaryPrimitives.ReadInt16LittleEndian(payload[6..]),
                    leftY: BinaryPrimitives.ReadInt16LittleEndian(payload[8..]),
                    rightX: BinaryPrimitives.ReadInt16LittleEndian(payload[10..]),
                    rightY: BinaryPrimitives.ReadInt16LittleEndian(payload[12..]));
                return true;
            case SunshineControllerBridgePacketKind.Motion:
                if (payload[1] != 0 || payload[2] != 0 || payload[3] != 0 ||
                    HasNonZero(payload[16..]) ||
                    !TryReadSingle(payload[4..], out float mx) ||
                    !TryReadSingle(payload[8..], out float my) ||
                    !TryReadSingle(payload[12..], out float mz)) return false;
                packet = new(kind, sessionId, controllerId, clientIndex, sequence,
                    motionType: payload[0], x: mx, y: my, z: mz);
                return true;
            case SunshineControllerBridgePacketKind.Touch:
                if (payload[1] != 0 || payload[2] != 0 || payload[3] != 0 ||
                    HasNonZero(payload[20..]) ||
                    !TryReadSingle(payload[8..], out float tx) ||
                    !TryReadSingle(payload[12..], out float ty) ||
                    !TryReadSingle(payload[16..], out float pressure)) return false;
                packet = new(kind, sessionId, controllerId, clientIndex, sequence,
                    touchEvent: payload[0],
                    pointerId: BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
                    x: tx, y: ty, pressure: pressure);
                return true;
            case SunshineControllerBridgePacketKind.Battery:
                if (HasNonZero(payload[2..]) || payload[1] > 100) return false;
                packet = new(kind, sessionId, controllerId, clientIndex, sequence,
                    batteryState: payload[0], batteryPercent: payload[1]);
                return true;
            case SunshineControllerBridgePacketKind.Feedback:
                if (HasNonZero(payload[32..])) return false;
                byte[] left = payload[12..22].ToArray();
                byte[] right = payload[22..32].ToArray();
                packet = new(kind, sessionId, controllerId, clientIndex, sequence,
                    feedbackKind: payload[0], lowRumble: payload[1],
                    highRumble: payload[2], red: payload[3], green: payload[4],
                    blue: payload[5], motionRate: BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]),
                    motionType: payload[8], triggerFlags: payload[9],
                    leftTriggerType: payload[10], rightTriggerType: payload[11],
                    leftTriggerData: left, rightTriggerData: right);
                return true;
            default:
                return false;
        }
    }

    private static byte[] CopyTrigger(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty) return Array.Empty<byte>();
        if (source.Length != 10)
            throw new ArgumentException("Trigger data must contain 10 bytes.", nameof(source));
        byte[] result = new byte[10];
        source.CopyTo(result);
        return result;
    }

    private static bool HasNonZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
            if (value != 0) return true;
        return false;
    }

    private static void WriteSingle(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination,
            BitConverter.SingleToInt32Bits(value));

    private static bool TryReadSingle(ReadOnlySpan<byte> source, out float value)
    {
        value = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(source));
        return float.IsFinite(value);
    }
}

internal enum SunshineControllerBridgePacketKind : byte
{
    Hello = 1,
    HelloAccepted = 2,
    Arrival = 3,
    State = 4,
    Motion = 5,
    Touch = 6,
    Battery = 7,
    Remove = 8,
    Feedback = 9,
}

internal enum SunshineControllerBridgeFeedbackKind : byte
{
    Rumble = 1,
    RumbleTriggers = 2,
    MotionEventState = 3,
    RgbLed = 4,
    AdaptiveTriggers = 5,
}
