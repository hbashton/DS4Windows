/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

namespace DS4Windows.Switch2;

internal enum Switch2BluetoothPlayerLedRequestFailure : byte
{
    None = 0,
    InvalidArgument,
    OutputUnavailable,
    StaleLifetime,
    Busy,
    ChannelRejected,
}

internal readonly struct Switch2BluetoothPlayerLedRequestResult
{
    private Switch2BluetoothPlayerLedRequestResult(
        Switch2BluetoothPlayerLedRequestFailure failure)
    {
        Failure = failure;
    }

    internal Switch2BluetoothPlayerLedRequestFailure Failure { get; }

    internal bool Accepted => Failure ==
        Switch2BluetoothPlayerLedRequestFailure.None;

    internal static Switch2BluetoothPlayerLedRequestResult Admit() =>
        new(Switch2BluetoothPlayerLedRequestFailure.None);

    internal static Switch2BluetoothPlayerLedRequestResult Reject(
        Switch2BluetoothPlayerLedRequestFailure failure) => new(failure);
}

/// <summary>
/// Closed player-slot LED edge on the same authenticated physical BLE lifetime
/// used by input and HD rumble. Admission is nonblocking; the lease retains and
/// observes the bounded acknowledged exchange through teardown.
/// </summary>
internal interface ISwitch2BluetoothPlayerLedTransportLease
{
    bool HasPlayerLedOutput { get; }

    Switch2BluetoothPlayerLedRequestResult TryRequestPlayerLed(
        byte playerNumber, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration);

    Switch2BluetoothPlayerLedRequestResult TryRequestPlayerLedMask(
        byte playerLedMask, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration)
    {
        if (!Switch2BluetoothPlayerLedCodec.TryGetPlayerNumber(playerLedMask,
                out byte playerNumber))
        {
            return Switch2BluetoothPlayerLedRequestResult.Reject(
                Switch2BluetoothPlayerLedRequestFailure.InvalidArgument);
        }
        return TryRequestPlayerLed(playerNumber, expectedModel,
            expectedDeviceGeneration, expectedTransportGeneration);
    }
}
