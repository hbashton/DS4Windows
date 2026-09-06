/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

namespace DS4Windows.Switch2;

/// <summary>
/// Observes only proven layout changes within one standalone Joy-Con lifetime.
/// The ordinary gyro toggle owns its existing latch; this observer just tells
/// it to baseline the reprojected buttons instead of inventing a press.
/// </summary>
internal struct Switch2GyroActivationOrientation
{
    private Switch2GyroTriggerSourceIdentity previous;

    internal bool Observe(DS4State state)
    {
        if (!TryReadIdentity(state, out var current))
        {
            previous = default;
            return false;
        }

        bool changed = previous.HasSamePhysicalSource(current) &&
            previous.JoyConMode != current.JoyConMode;
        previous = current;
        return changed;
    }

    private static bool TryReadIdentity(DS4State state,
        out Switch2GyroTriggerSourceIdentity identity)
    {
        identity = default;
        if (state == null)
            return false;
        var joyCon = state.Switch2JoyConRawInputStatus;
        var pro = state.Switch2RawInputStatus;
        if (!joyCon.IsValid || joyCon.ContractVersion != Switch2JoyConProfileInputFrame.CurrentVersion ||
            (pro.IsValid && pro.ContractVersion == Switch2ProProfileInputFrame.CurrentVersion) ||
            joyCon.PairEpoch != 0)
            return false;

        bool standalone = joyCon.Mode switch {
            Switch2JoyConProfileMode.StandaloneVerticalLeft or Switch2JoyConProfileMode.StandaloneHorizontalLeft =>
                joyCon.LeftPresent && !joyCon.RightPresent &&
                joyCon.LeftDeviceGeneration != 0 && joyCon.LeftTransportGeneration != 0 &&
                joyCon.RightDeviceGeneration == 0 && joyCon.RightTransportGeneration == 0,
            Switch2JoyConProfileMode.StandaloneVerticalRight or Switch2JoyConProfileMode.StandaloneHorizontalRight =>
                joyCon.RightPresent && !joyCon.LeftPresent &&
                joyCon.RightDeviceGeneration != 0 && joyCon.RightTransportGeneration != 0 &&
                joyCon.LeftDeviceGeneration == 0 && joyCon.LeftTransportGeneration == 0,
            _ => false,
        };
        if (!standalone)
            return false;
        identity = new(true, 0, joyCon.LeftDeviceGeneration, joyCon.LeftTransportGeneration,
            joyCon.RightDeviceGeneration, joyCon.RightTransportGeneration, joyCon.Mode);
        return true;
    }
}
