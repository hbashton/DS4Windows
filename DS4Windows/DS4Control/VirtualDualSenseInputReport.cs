/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows
{
    public static class VirtualDualSenseInputReport
    {
        public const byte UsbInputReportId = 0x01;
        public const int UsbInputReportLength = 64;
        public const int SonyVendorId = 0x054C;
        public const int DualSenseProductId = 0x0CE6;
        public const string ProductString = "Wireless Controller";

        private const int TouchpadDataOffset = 33;

        public static byte[] BuildUsbReport(DS4State state)
        {
            byte[] report = new byte[UsbInputReportLength];
            report[0] = UsbInputReportId;
            report[1] = state.LX;
            report[2] = state.LY;
            report[3] = state.RX;
            report[4] = state.RY;
            report[5] = state.L2;
            report[6] = state.R2;
            report[7] = state.FrameCounter == 255 ? (byte)(state.PacketCounter & 0x7f) : state.FrameCounter;
            report[8] = (byte)(HatValue(state) |
                (state.Square ? 0x10 : 0) |
                (state.Cross ? 0x20 : 0) |
                (state.Circle ? 0x40 : 0) |
                (state.Triangle ? 0x80 : 0));
            report[9] = (byte)(
                (state.L1 ? 0x01 : 0) |
                (state.R1 ? 0x02 : 0) |
                (state.L2Btn || state.L2 > 0 ? 0x04 : 0) |
                (state.R2Btn || state.R2 > 0 ? 0x08 : 0) |
                (state.Share ? 0x10 : 0) |
                (state.Options ? 0x20 : 0) |
                (state.L3 ? 0x40 : 0) |
                (state.R3 ? 0x80 : 0));
            report[10] = (byte)(
                (state.PS ? 0x01 : 0) |
                ((state.OutputTouchButton || state.TouchButton) ? 0x02 : 0) |
                (state.Mute ? 0x04 : 0) |
                (state.FnL ? 0x10 : 0) |
                (state.FnR ? 0x20 : 0) |
                (state.BLP ? 0x40 : 0) |
                (state.BRP ? 0x80 : 0));

            WriteInt16(report, 12, state.Motion?.gyroYawFull ?? 0);
            WriteInt16(report, 14, state.Motion?.gyroPitchFull ?? 0);
            WriteInt16(report, 16, state.Motion?.gyroRollFull ?? 0);
            WriteInt16(report, 18, state.Motion?.accelXFull ?? 0);
            WriteInt16(report, 20, state.Motion?.accelYFull ?? 0);
            WriteInt16(report, 22, state.Motion?.accelZFull ?? 0);
            WriteUInt32(report, 28, (uint)Math.Min(uint.MaxValue, state.totalMicroSec * 3UL));

            WriteTouch(report, TouchpadDataOffset, state.TrackPadTouch0);
            WriteTouch(report, TouchpadDataOffset + 4, state.TrackPadTouch1);
            report[TouchpadDataOffset + 8] = state.TouchPacketCounter == 255
                ? (byte)(state.PacketCounter & 0xff)
                : state.TouchPacketCounter;

            byte battery = (byte)Math.Clamp(state.Battery / 10, 0, 10);
            report[53] = battery;
            return report;
        }

        private static byte HatValue(DS4State state)
        {
            if (state.DpadUp && state.DpadRight) return 1;
            if (state.DpadRight && state.DpadDown) return 3;
            if (state.DpadDown && state.DpadLeft) return 5;
            if (state.DpadLeft && state.DpadUp) return 7;
            if (state.DpadUp) return 0;
            if (state.DpadRight) return 2;
            if (state.DpadDown) return 4;
            if (state.DpadLeft) return 6;
            return 8;
        }

        private static void WriteTouch(byte[] report, int offset, DS4State.TrackPadTouch touch)
        {
            int x = Math.Clamp(touch.X, 0, DS4Touchpad.RESOLUTION_X_MAX);
            int y = Math.Clamp(touch.Y, 0, DS4Touchpad.RESOLUTION_Y_MAX);

            report[offset] = (byte)((touch.IsActive ? 0x00 : 0x80) | (touch.Id & 0x7f));
            report[offset + 1] = (byte)(x & 0xff);
            report[offset + 2] = (byte)(((x >> 8) & 0x0f) | ((y & 0x0f) << 4));
            report[offset + 3] = (byte)((y >> 4) & 0xff);
        }

        private static void WriteInt16(byte[] report, int offset, int value)
        {
            short clamped = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
            report[offset] = (byte)(clamped & 0xff);
            report[offset + 1] = (byte)((clamped >> 8) & 0xff);
        }

        private static void WriteUInt32(byte[] report, int offset, uint value)
        {
            report[offset] = (byte)(value & 0xff);
            report[offset + 1] = (byte)((value >> 8) & 0xff);
            report[offset + 2] = (byte)((value >> 16) & 0xff);
            report[offset + 3] = (byte)((value >> 24) & 0xff);
        }
    }
}
