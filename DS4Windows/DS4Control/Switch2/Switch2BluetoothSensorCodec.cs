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
