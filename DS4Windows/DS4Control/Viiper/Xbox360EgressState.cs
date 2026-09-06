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
    /// Immutable, value-owned Xbox 360 state at the DS4Windows/VIIPER
    /// boundary. Its scalar fields map one-to-one to VIIPER's canonical
    /// 20-byte standard-controller payload; no reserved bytes are semantic
    /// state and every serialization writes them as zero.
    /// </summary>
    internal readonly struct Xbox360EgressState :
        IEquatable<Xbox360EgressState>,
        IOrderedEgressState<Xbox360EgressState>
    {
        internal const int WireSize = 20;
        internal const uint ValidButtonsMask = 0x0000F7FF;

        internal Xbox360EgressState(uint buttons, byte leftTrigger,
            byte rightTrigger, short leftStickX, short leftStickY,
            short rightStickX, short rightStickY)
        {
            if ((buttons & ~ValidButtonsMask) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(buttons),
                    buttons,
                    "Xbox 360 egress buttons contain reserved bits.");
            }

            Buttons = buttons;
            LeftTrigger = leftTrigger;
            RightTrigger = rightTrigger;
            LeftStickX = leftStickX;
            LeftStickY = leftStickY;
            RightStickX = rightStickX;
            RightStickY = rightStickY;
        }

        internal uint Buttons { get; }
        internal byte LeftTrigger { get; }
        internal byte RightTrigger { get; }
        internal short LeftStickX { get; }
        internal short LeftStickY { get; }
        internal short RightStickX { get; }
        internal short RightStickY { get; }

        internal bool IsNeutral => Buttons == 0 && LeftTrigger == 0 &&
            RightTrigger == 0 && LeftStickX == 0 && LeftStickY == 0 &&
            RightStickX == 0 && RightStickY == 0;

        internal static Xbox360EgressState Neutral => default;

        public bool HasOrderedTransitionTo(in Xbox360EgressState current)
        {
            return Buttons != current.Buttons ||
                (LeftTrigger == 0) != (current.LeftTrigger == 0) ||
                (RightTrigger == 0) != (current.RightTrigger == 0);
        }

        /// <summary>
        /// Writes exactly one canonical VIIPER Xbox 360 semantic payload.
        /// An exact-sized destination is required so callers cannot silently
        /// confuse this boundary payload with an outer stream envelope.
        /// </summary>
        public void BuildInto(Span<byte> destination)
        {
            if (destination.Length != WireSize)
            {
                throw new ArgumentException(
                    $"An Xbox 360 egress payload is exactly {WireSize} bytes.",
                    nameof(destination));
            }

            destination.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(destination, Buttons);
            destination[4] = LeftTrigger;
            destination[5] = RightTrigger;
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(6, 2),
                LeftStickX);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(8, 2),
                LeftStickY);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(10, 2),
                RightStickX);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(12, 2),
                RightStickY);
        }

        public bool Equals(Xbox360EgressState other) =>
            Buttons == other.Buttons &&
            LeftTrigger == other.LeftTrigger &&
            RightTrigger == other.RightTrigger &&
            LeftStickX == other.LeftStickX &&
            LeftStickY == other.LeftStickY &&
            RightStickX == other.RightStickX &&
            RightStickY == other.RightStickY;

        public override bool Equals(object obj) =>
            obj is Xbox360EgressState other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Buttons,
            LeftTrigger, RightTrigger, LeftStickX, LeftStickY, RightStickX,
            RightStickY);

        public static bool operator ==(Xbox360EgressState left,
            Xbox360EgressState right) => left.Equals(right);

        public static bool operator !=(Xbox360EgressState left,
            Xbox360EgressState right) => !left.Equals(right);
    }
}
