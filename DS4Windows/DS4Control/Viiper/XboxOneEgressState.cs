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
    /// Versioned, transport-neutral Xbox One/Series semantic state at the
    /// DS4Windows/VIIPER boundary. This is not a raw GIP packet: VIIPER owns
    /// GIP sequence, keep-alive, Guide-status, Share-extension, lifecycle,
    /// and endpoint-presentation policy.
    /// </summary>
    internal readonly struct XboxOneEgressState :
        IEquatable<XboxOneEgressState>,
        IOrderedEgressState<XboxOneEgressState>
    {
        internal const ushort ContractVersion = 1;
        internal const int WireSize = 24;

        internal const uint MenuButton = 1u << 0;
        internal const uint ViewButton = 1u << 1;
        internal const uint AButton = 1u << 2;
        internal const uint BButton = 1u << 3;
        internal const uint XButton = 1u << 4;
        internal const uint YButton = 1u << 5;
        internal const uint DpadUpButton = 1u << 6;
        internal const uint DpadDownButton = 1u << 7;
        internal const uint DpadLeftButton = 1u << 8;
        internal const uint DpadRightButton = 1u << 9;
        internal const uint LeftBumperButton = 1u << 10;
        internal const uint RightBumperButton = 1u << 11;
        internal const uint LeftStickButton = 1u << 12;
        internal const uint RightStickButton = 1u << 13;
        internal const uint GuideButton = 1u << 14;
        internal const uint ShareButton = 1u << 15;
        internal const uint ValidButtonsMask = 0x0000FFFF;

        internal XboxOneEgressState(uint buttons, ushort leftTrigger,
            ushort rightTrigger, short leftStickX, short leftStickY,
            short rightStickX, short rightStickY)
        {
            if ((buttons & ~ValidButtonsMask) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(buttons),
                    buttons,
                    "Xbox One semantic buttons contain reserved bits.");
            }
            ValidateTrigger(leftTrigger, nameof(leftTrigger));
            ValidateTrigger(rightTrigger, nameof(rightTrigger));

            Buttons = buttons;
            LeftTrigger = leftTrigger;
            RightTrigger = rightTrigger;
            LeftStickX = leftStickX;
            LeftStickY = leftStickY;
            RightStickX = rightStickX;
            RightStickY = rightStickY;
        }

        internal uint Buttons { get; }
        internal ushort LeftTrigger { get; }
        internal ushort RightTrigger { get; }
        internal short LeftStickX { get; }
        internal short LeftStickY { get; }
        internal short RightStickX { get; }
        internal short RightStickY { get; }

        internal bool IsNeutral => Buttons == 0 && LeftTrigger == 0 &&
            RightTrigger == 0 && LeftStickX == 0 && LeftStickY == 0 &&
            RightStickX == 0 && RightStickY == 0;

        internal static XboxOneEgressState Neutral => default;

        /// <summary>
        /// Adapts the existing canonical mapped DS4 state without creating a
        /// second profile-mapping stack. Stick precision is retained until
        /// final signed-16 encoding. Triggers still use the legacy eight-bit
        /// vocabulary, expanded monotonically to the Xbox ten-bit domain;
        /// that expansion does not recover lost trigger precision.
        /// </summary>
        internal static XboxOneEgressState FromLegacyMappedState(
            DS4State state, int device)
        {
            ArgumentNullException.ThrowIfNull(state);
            Xbox360EgressState legacy = ViiperStatePacketBuilder.
                BuildXbox360State(state, device);

            uint buttons = 0;
            CopyButton(legacy.Buttons, 0x0010, ref buttons, MenuButton);
            CopyButton(legacy.Buttons, 0x0020, ref buttons, ViewButton);
            CopyButton(legacy.Buttons, 0x1000, ref buttons, AButton);
            CopyButton(legacy.Buttons, 0x2000, ref buttons, BButton);
            CopyButton(legacy.Buttons, 0x4000, ref buttons, XButton);
            CopyButton(legacy.Buttons, 0x8000, ref buttons, YButton);
            CopyButton(legacy.Buttons, 0x0001, ref buttons, DpadUpButton);
            CopyButton(legacy.Buttons, 0x0002, ref buttons, DpadDownButton);
            CopyButton(legacy.Buttons, 0x0004, ref buttons, DpadLeftButton);
            CopyButton(legacy.Buttons, 0x0008, ref buttons,
                DpadRightButton);
            CopyButton(legacy.Buttons, 0x0100, ref buttons,
                LeftBumperButton);
            CopyButton(legacy.Buttons, 0x0200, ref buttons,
                RightBumperButton);
            CopyButton(legacy.Buttons, 0x0040, ref buttons,
                LeftStickButton);
            CopyButton(legacy.Buttons, 0x0080, ref buttons,
                RightStickButton);
            CopyButton(legacy.Buttons, 0x0400, ref buttons, GuideButton);
            if (state.Capture)
            {
                buttons |= ShareButton;
            }

            return new XboxOneEgressState(buttons,
                ExpandTrigger(legacy.LeftTrigger),
                ExpandTrigger(legacy.RightTrigger), legacy.LeftStickX,
                legacy.LeftStickY, legacy.RightStickX, legacy.RightStickY);
        }

        public bool HasOrderedTransitionTo(in XboxOneEgressState current) =>
            Buttons != current.Buttons ||
            (LeftTrigger == 0) != (current.LeftTrigger == 0) ||
            (RightTrigger == 0) != (current.RightTrigger == 0);

        /// <summary>
        /// Writes exactly one v1 broker semantic frame. Bytes 20..23 are
        /// mandatory zero and reserved for a future version. The destination
        /// must be exact-sized so this frame cannot be confused with the outer
        /// stream envelope or an 18-byte GIP input message.
        /// </summary>
        public void BuildInto(Span<byte> destination)
        {
            if (destination.Length != WireSize)
            {
                throw new ArgumentException(
                    $"An Xbox One semantic egress frame is exactly {WireSize} bytes.",
                    nameof(destination));
            }

            destination.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(destination,
                ContractVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2, 2),
                WireSize);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4),
                Buttons);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(8, 2),
                LeftTrigger);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(10, 2),
                RightTrigger);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(12, 2),
                LeftStickX);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(14, 2),
                LeftStickY);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(16, 2),
                RightStickX);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(18, 2),
                RightStickY);
        }

        public bool Equals(XboxOneEgressState other) =>
            Buttons == other.Buttons &&
            LeftTrigger == other.LeftTrigger &&
            RightTrigger == other.RightTrigger &&
            LeftStickX == other.LeftStickX &&
            LeftStickY == other.LeftStickY &&
            RightStickX == other.RightStickX &&
            RightStickY == other.RightStickY;

        public override bool Equals(object obj) =>
            obj is XboxOneEgressState other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Buttons,
            LeftTrigger, RightTrigger, LeftStickX, LeftStickY, RightStickX,
            RightStickY);

        private static void ValidateTrigger(ushort trigger,
            string parameterName)
        {
            if (trigger > 1023)
            {
                throw new ArgumentOutOfRangeException(parameterName, trigger,
                    "An Xbox One trigger cannot exceed ten bits.");
            }
        }

        private static ushort ExpandTrigger(byte trigger) =>
            (ushort)((trigger * 1023u + 127u) / 255u);

        private static void CopyButton(uint source, uint sourceMask,
            ref uint destination, uint destinationMask)
        {
            if ((source & sourceMask) != 0)
            {
                destination |= destinationMask;
            }
        }
    }
}
