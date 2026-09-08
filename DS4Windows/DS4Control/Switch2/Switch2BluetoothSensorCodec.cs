/*
DS4Windows
Copyright (C) 2026 hbashton
SPDX-License-Identifier: GPL-3.0-or-later

Protocol adaptation from GPL-3.0 Switch2Connect, commit
61ac6642ce12fe7217e38a860b14863b18ca7e28, src/controller.py:
SW2 startup feature selection and enableFeatures/write_command.
*/

using System;

namespace DS4Windows.Switch2;

internal enum Switch2BluetoothSensorInitializationFailure : byte
{
    None = 0,
    NotPrepared,
    Busy,
    WriteRejected,
    ResponseRejected,
    Cancelled,
    DependencyThrew,
    Retired,
}

/// <summary>
/// Closed, volatile Joy-Con sensor startup commands. The donor's 0x94 mask
/// selects motion, optical mouse and magnetometer; 0xFF is not equivalent and
/// is reported to produce phantom trigger input. This is not the USB mask.
/// No pairing, memory, firmware or arbitrary feature writes are exposed.
/// </summary>
internal static class Switch2BluetoothSensorCodec
{
    internal const byte CommandId = 0x0C;
    internal const byte SensorMask = 0x94;
    internal const int RequestLength = 12;

    internal static byte[] CreateRequest(bool enable) => new byte[]
    {
        CommandId, 0x91, 0x01, enable ? (byte)0x04 : (byte)0x02,
        0x00, 0x04, 0x00, 0x00, SensorMask, 0x00, 0x00, 0x00,
    };

    // Like the donor, correlate by command ID and success status. There is
    // no evidenced subcommand echo; do not invent one from reserved bytes.
    internal static bool IsAccepted(ReadOnlySpan<byte> response) =>
        response.Length >= 8 && response[0] == CommandId && response[1] == 1;
}

/// <summary>
/// Closed Pro Bluetooth feature startup. The pinned console sequence selects
/// and then enables 0x2F, including IMU bit 2. This is not Joy-Con's 0x94 or
/// USB's 0x27, and its acknowledgement does not prove headphone playback.
/// </summary>
internal static class Switch2BluetoothProFeatureCodec
{
    internal const byte CommandId = 0x0C;
    internal const byte FeatureMask = 0x2F;
    internal const int ResponseLength = 12;

    // ndeadly/switch2_controller_research d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92,
    // bluetooth_interface.md Pro startup and commands.md 0C/02,0C/04.
    internal static byte[] CreateRequest(bool enable) => new byte[]
    {
        CommandId, 0x91, 0x01, enable ? (byte)0x04 : (byte)0x02,
        0x00, 0x04, 0x00, 0x00, FeatureMask, 0x00, 0x00, 0x00,
    };

    // Exact documented command-only reply. Public console captures contain
    // these 12 bytes behind the combined lane's 14-byte zero prefix; that is
    // not evidence for accepting a prefix on our command-only c765 UUID.
    // Do not reuse Joy-Con's deliberately legacy/lax response predicate.
    internal static bool IsAccepted(ReadOnlySpan<byte> response, bool enable) =>
        response.Length == ResponseLength && response[0] == CommandId &&
        response[1] == 0x01 && response[2] == 0x01 &&
        response[3] == (enable ? 0x04 : 0x02) && response[4] == 0x10 &&
        response[5] == 0x78 && response[6] == 0 && response[7] == 0 &&
        response[8] == 0 && response[9] == 0 && response[10] == 0 && response[11] == 0;
}
