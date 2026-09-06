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

namespace DS4Windows
{
    /// <summary>
    /// Immutable, value-owned Switch 2 Pro state at the DS4Windows/VIIPER
    /// boundary. VIIPER owns the HID report counter and motion timestamp at
    /// actual endpoint presentation; neither is frozen in this 24-byte state.
    /// </summary>
    internal readonly struct Switch2EgressState :
        IEquatable<Switch2EgressState>,
        IOrderedEgressState<Switch2EgressState>
    {
        internal const int WireSize = 24;
        internal const uint ValidButtonsMask = 0x002FFFFF;
        internal const ushort NeutralAxis = 0x0800;

        internal Switch2EgressState(uint buttons, ushort leftStickX,
            ushort leftStickY, ushort rightStickX, ushort rightStickY,
            short accelerationX, short accelerationY, short accelerationZ,
            short gyroYaw, short gyroPitch, short gyroRoll)
        {
            if ((buttons & ~ValidButtonsMask) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(buttons),
                    buttons, "Switch 2 egress buttons contain reserved bits.");
            }
            ValidateAxis(leftStickX, nameof(leftStickX));
            ValidateAxis(leftStickY, nameof(leftStickY));
            ValidateAxis(rightStickX, nameof(rightStickX));
            ValidateAxis(rightStickY, nameof(rightStickY));

            Buttons = buttons;
            LeftStickX = leftStickX;
            LeftStickY = leftStickY;
            RightStickX = rightStickX;
            RightStickY = rightStickY;
            AccelerationX = accelerationX;
            AccelerationY = accelerationY;
            AccelerationZ = accelerationZ;
            GyroYaw = gyroYaw;
            GyroPitch = gyroPitch;
            GyroRoll = gyroRoll;
        }

        internal uint Buttons { get; }
        internal ushort LeftStickX { get; }
        internal ushort LeftStickY { get; }
        internal ushort RightStickX { get; }
        internal ushort RightStickY { get; }
        internal short AccelerationX { get; }
        internal short AccelerationY { get; }
        internal short AccelerationZ { get; }
        internal short GyroYaw { get; }
        internal short GyroPitch { get; }
        internal short GyroRoll { get; }

        internal static Switch2EgressState Neutral => new(0,
            NeutralAxis, NeutralAxis, NeutralAxis, NeutralAxis,
            0, 0, 0, 0, 0, 0);

        public bool HasOrderedTransitionTo(in Switch2EgressState current) =>
            Buttons != current.Buttons;

        public void BuildInto(Span<byte> destination)
        {
            if (destination.Length != WireSize)
            {
                throw new ArgumentException(
                    $"A Switch 2 egress payload is exactly {WireSize} bytes.",
                    nameof(destination));
            }

            BinaryPrimitives.WriteUInt32LittleEndian(destination, Buttons);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4, 2),
                LeftStickX);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6, 2),
                LeftStickY);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(8, 2),
                RightStickX);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(10, 2),
                RightStickY);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(12, 2),
                AccelerationX);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(14, 2),
                AccelerationY);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(16, 2),
                AccelerationZ);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(18, 2),
                GyroYaw);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(20, 2),
                GyroPitch);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(22, 2),
                GyroRoll);
        }

        public bool Equals(Switch2EgressState other) =>
            Buttons == other.Buttons &&
            LeftStickX == other.LeftStickX &&
            LeftStickY == other.LeftStickY &&
            RightStickX == other.RightStickX &&
            RightStickY == other.RightStickY &&
            AccelerationX == other.AccelerationX &&
            AccelerationY == other.AccelerationY &&
            AccelerationZ == other.AccelerationZ &&
            GyroYaw == other.GyroYaw &&
            GyroPitch == other.GyroPitch &&
            GyroRoll == other.GyroRoll;

        public override bool Equals(object obj) =>
            obj is Switch2EgressState other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            HashCode.Combine(Buttons, LeftStickX, LeftStickY, RightStickX,
                RightStickY, AccelerationX, AccelerationY, AccelerationZ),
            GyroYaw, GyroPitch, GyroRoll);

        private static void ValidateAxis(ushort axis, string parameterName)
        {
            if (axis > 4095)
            {
                throw new ArgumentOutOfRangeException(parameterName, axis,
                    "A Switch 2 stick axis cannot exceed 12 bits.");
            }
        }
    }
}
