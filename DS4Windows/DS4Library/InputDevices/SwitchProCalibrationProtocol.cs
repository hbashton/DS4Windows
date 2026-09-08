using System;
using System.Buffers.Binary;
using System.IO;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Original Switch controller SPI calibration, not the Switch 2 protocol.
    /// Reply matching and optional user/factory selection follow SDL's
    /// SDL_hidapi_switch.c (c71abd08605b8bb7078372307a93274725c99fe0),
    /// ReadSubcommandReply and LoadStickCalibration. Missing data is never
    /// treated as calibration, and erased/degenerate data is not installed.
    /// </summary>
    internal static class SwitchProCalibrationProtocol
    {
        internal const int SpiDataOffset = 20;
        internal const int MaximumCalibrationAttempts = 3;

        internal static bool IsMatchingReply(byte subcommand,
            ReadOnlySpan<byte> request, ReadOnlySpan<byte> report)
        {
            if (report.Length < 15 || report[0] != 0x21 ||
                (report[13] & 0x80) == 0 || report[14] != subcommand)
                return false;

            if (subcommand != 0x10)
                return true;

            return request.Length == 5 && request[4] > 0 &&
                report.Length >= SpiDataOffset + request[4] &&
                report.Slice(15, 5).SequenceEqual(request);
        }

        internal static byte[] CreateSpiRequest(ushort address, byte count)
        {
            return new byte[] { (byte)address, (byte)(address >> 8), 0, 0, count };
        }

        internal static byte[] ReadCalibration(
            Func<ushort, byte, byte[]> readSpi, ushort userMagicAddress,
            ushort userAddress, ushort factoryAddress, byte count,
            Func<byte[], bool, bool> isValid, out bool userCalibration)
        {
            userCalibration = false;
            byte[] magic = readSpi(userMagicAddress, 2);
            if (IsMatchingReply(0x10, CreateSpiRequest(userMagicAddress, 2), magic) &&
                magic[SpiDataOffset] == 0xB2 && magic[SpiDataOffset + 1] == 0xA1)
            {
                byte[] user = readSpi(userAddress, count);
                if (IsMatchingReply(0x10, CreateSpiRequest(userAddress, count), user) &&
                    isValid(user, true))
                {
                    userCalibration = true;
                    return user;
                }
            }

            // Some compatible controllers do not expose user SPI storage.
            // Fall back to their real factory block, not sample constants.
            for (int attempt = 0; attempt < MaximumCalibrationAttempts; attempt++)
            {
                byte[] factory = readSpi(factoryAddress, count);
                if (IsMatchingReply(0x10, CreateSpiRequest(factoryAddress, count), factory) &&
                    isValid(factory, false))
                    return factory;
            }

            // StartUpdate already turns an IOException into normal device
            // removal before either the input or rumble worker is started.
            throw new IOException($"Switch Pro calibration at SPI 0x{factoryAddress:X4} " +
                "was unavailable or invalid after bounded retries.");
        }

        internal static bool IsValidStick(ReadOnlySpan<byte> report,
            bool right, bool userCalibration)
        {
            if (report.Length < SpiDataOffset + 9)
                return false;

            ReadOnlySpan<byte> data = report.Slice(SpiDataOffset, 9);
            int centerOffset = right ? 0 : 3;
            int minimumOffset = right ? 3 : 6;
            int maximumOffset = right ? 6 : 0;
            for (int axis = 0; axis < 2; axis++)
            {
                int center = ReadPackedAxis(data, centerOffset, axis);
                int below = ReadPackedAxis(data, minimumOffset, axis);
                int above = ReadPackedAxis(data, maximumOffset, axis);
                if (center is 0 or 0xFFF || below is 0 or 0xFFF || above is 0 or 0xFFF ||
                    below > center || center + above > 0xFFF)
                    return false;

                int minimum = center - below;
                int maximum = center + above;
                if (!userCalibration)
                {
                    // Keep the existing DS4Windows factory cutoff behavior.
                    minimum = (int)(minimum * 1.04);
                    maximum = (int)(maximum * 0.96);
                }

                if (minimum >= center || maximum <= center)
                    return false;
            }

            return true;
        }

        internal static bool IsValidImu(ReadOnlySpan<byte> report)
        {
            if (report.Length < SpiDataOffset + 24)
                return false;

            ReadOnlySpan<byte> data = report.Slice(SpiDataOffset, 24);
            for (int offset = 0; offset < 6; offset += 2)
            {
                int accelOrigin = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));
                int accelSensitivity = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset + 6, 2));
                int gyroOrigin = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset + 12, 2));
                int gyroSensitivity = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset + 18, 2));
                if (accelSensitivity == accelOrigin || gyroSensitivity == gyroOrigin)
                    return false;
            }

            return true;
        }

        private static int ReadPackedAxis(ReadOnlySpan<byte> data, int offset, int axis)
        {
            return axis == 0 ? data[offset] | ((data[offset + 1] & 0x0F) << 8) :
                (data[offset + 1] >> 4) | (data[offset + 2] << 4);
        }
    }
}
