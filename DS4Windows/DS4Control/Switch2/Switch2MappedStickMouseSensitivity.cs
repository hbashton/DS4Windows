/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The independent left/right stick-mouse sensitivity control is adapted from
the GPL-3.0 licensed Switch2Connect project, commit
61ac6642ce12fe7217e38a860b14863b18ca7e28, src/controller.py
Controller._apply_joystick_mouse. DS4Windows applies the profile scalar at
its existing mapped-stick mouse boundary rather than creating a second mapper.
*/

namespace DS4Windows.Switch2;

internal static class Switch2MappedStickMouseSensitivity
{
    internal const double Minimum = 0.0;
    internal const double Maximum = 10.0;
    internal const double Default = 5.0;

    internal static double Normalize(double value) =>
        double.IsFinite(value) && value >= Minimum && value <= Maximum ?
            value : Default;

    internal static double ResolveGain(in Switch2RawInputStatus pro,
        in Switch2JoyConRawInputStatus joyCon, DS4Controls control,
        double leftSensitivity, double rightSensitivity)
    {
        if (!HasOneValidatedSource(pro, joyCon))
        {
            return 1.0;
        }

        double sensitivity;
        if (control is DS4Controls.LXNeg or DS4Controls.LXPos or
                DS4Controls.LYNeg or DS4Controls.LYPos)
        {
            sensitivity = Normalize(leftSensitivity);
        }
        else if (control is DS4Controls.RXNeg or DS4Controls.RXPos or
                 DS4Controls.RYNeg or DS4Controls.RYPos)
        {
            sensitivity = Normalize(rightSensitivity);
        }
        else
        {
            return 1.0;
        }

        return sensitivity / Default;
    }

    private static bool HasOneValidatedSource(in Switch2RawInputStatus pro,
        in Switch2JoyConRawInputStatus joyCon)
    {
        bool proValid = pro.IsValid && pro.ContractVersion ==
                Switch2ProProfileInputFrame.CurrentVersion &&
            pro.DeviceGeneration != 0 && pro.TransportGeneration != 0 &&
            pro.CompletionTimestampQpc > 0 && pro.QpcFrequency > 0;
        bool joyConModeValid = joyCon.Mode switch
        {
            Switch2JoyConProfileMode.Joined => joyCon.PairEpoch != 0 &&
                joyCon.LeftPresent && joyCon.RightPresent,
            Switch2JoyConProfileMode.StandaloneHorizontalLeft or
                Switch2JoyConProfileMode.StandaloneVerticalLeft =>
                    joyCon.LeftPresent && !joyCon.RightPresent,
            Switch2JoyConProfileMode.StandaloneHorizontalRight or
                Switch2JoyConProfileMode.StandaloneVerticalRight =>
                    !joyCon.LeftPresent && joyCon.RightPresent,
            _ => false,
        };
        bool joyConValid = joyCon.IsValid && joyCon.ContractVersion ==
                Switch2JoyConProfileInputFrame.CurrentVersion &&
            joyCon.CompletionTimestampQpc > 0 && joyCon.QpcFrequency > 0 &&
            joyConModeValid &&
            (!joyCon.LeftPresent || joyCon.LeftDeviceGeneration != 0 &&
                joyCon.LeftTransportGeneration != 0) &&
            (!joyCon.RightPresent || joyCon.RightDeviceGeneration != 0 &&
                joyCon.RightTransportGeneration != 0);
        return proValid != joyConValid;
    }
}
