/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Runtime.InteropServices;

namespace DS4Windows
{
    /// <summary>
    /// The fixed-layout state at the DS4Windows/VIIPER boundary. This is built
    /// after mapping and steering-wheel substitution. Transition detection and
    /// wire serialization therefore cannot observe different trigger values.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ViiperMappedInputState : IEquatable<ViiperMappedInputState>
    {
        internal const uint L2ButtonMask = 0x00000400;
        internal const uint R2ButtonMask = 0x00000800;

        public byte LX;
        public byte LY;
        public byte RX;
        public byte RY;
        public uint Buttons;
        public byte DPad;
        public byte L2;
        public byte R2;
        public ViiperMappedTouchState Touch0;
        public ViiperMappedTouchState Touch1;
        public short GyroX;
        public short GyroY;
        public short GyroZ;
        public short AccelX;
        public short AccelY;
        public short AccelZ;

        public readonly bool L2Pressed =>
            L2 != 0 && (Buttons & L2ButtonMask) != 0;

        public readonly bool R2Pressed =>
            R2 != 0 && (Buttons & R2ButtonMask) != 0;

        public readonly bool IsNeutral =>
            LX == 128 && LY == 128 && RX == 128 && RY == 128 &&
            Buttons == 0 && DPad == 0 && L2 == 0 && R2 == 0 &&
            !Touch0.IsActive && !Touch1.IsActive &&
            GyroX == 0 && GyroY == 0 && GyroZ == 0 &&
            AccelX == 0 && AccelY == 0 && AccelZ == -8192;

        internal static ViiperMappedInputState Neutral => new()
        {
            LX = 128,
            LY = 128,
            RX = 128,
            RY = 128,
            Touch0 = ViiperMappedTouchState.Inactive,
            Touch1 = ViiperMappedTouchState.Inactive,
            AccelZ = -8192,
        };

        internal void StrengthenTrigger(bool left, byte peak)
        {
            if (peak == 0)
            {
                return;
            }

            if (left)
            {
                if (peak > L2)
                {
                    L2 = peak;
                }
                Buttons |= L2ButtonMask;
            }
            else
            {
                if (peak > R2)
                {
                    R2 = peak;
                }
                Buttons |= R2ButtonMask;
            }
        }

        public readonly bool Equals(ViiperMappedInputState other)
        {
            return LX == other.LX && LY == other.LY &&
                RX == other.RX && RY == other.RY &&
                Buttons == other.Buttons && DPad == other.DPad &&
                L2 == other.L2 && R2 == other.R2 &&
                Touch0.Equals(other.Touch0) && Touch1.Equals(other.Touch1) &&
                GyroX == other.GyroX && GyroY == other.GyroY &&
                GyroZ == other.GyroZ && AccelX == other.AccelX &&
                AccelY == other.AccelY && AccelZ == other.AccelZ;
        }

        public override readonly bool Equals(object obj) =>
            obj is ViiperMappedInputState other && Equals(other);

        public override readonly int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(LX);
            hash.Add(LY);
            hash.Add(RX);
            hash.Add(RY);
            hash.Add(Buttons);
            hash.Add(DPad);
            hash.Add(L2);
            hash.Add(R2);
            hash.Add(Touch0);
            hash.Add(Touch1);
            hash.Add(GyroX);
            hash.Add(GyroY);
            hash.Add(GyroZ);
            hash.Add(AccelX);
            hash.Add(AccelY);
            hash.Add(AccelZ);
            return hash.ToHashCode();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ViiperMappedTouchState :
        IEquatable<ViiperMappedTouchState>
    {
        public ushort X;
        public ushort Y;
        public byte TrackingId;
        [MarshalAs(UnmanagedType.U1)]
        public bool IsActive;

        internal static ViiperMappedTouchState Inactive => new()
        {
            TrackingId = 0,
            IsActive = false,
        };

        public readonly bool Equals(ViiperMappedTouchState other) =>
            X == other.X && Y == other.Y &&
            TrackingId == other.TrackingId && IsActive == other.IsActive;

        public override readonly bool Equals(object obj) =>
            obj is ViiperMappedTouchState other && Equals(other);

        public override readonly int GetHashCode() =>
            HashCode.Combine(X, Y, TrackingId, IsActive);
    }
}
