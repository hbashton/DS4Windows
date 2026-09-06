/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The axis selection and subtractive soft-deadzone law are adapted from the
GPL-3.0 Switch2Connect project, commit
61ac6642ce12fe7217e38a860b14863b18ca7e28, src/controller.py. This
allocation-free implementation remains inside DS4Windows' existing Switch 2
motion projection and canonical mapping path.
*/

using System;
using System.Numerics;

namespace DS4Windows.Switch2;

internal static class Switch2MotionSoftDeadzone
{
    internal const double Minimum = 0.0;
    internal const double Maximum = 100.0;
    internal const double Default = 0.0;

    internal static double Normalize(double value) =>
        double.IsFinite(value) && value >= Minimum && value <= Maximum ?
            value : Default;

    internal static Vector3 Apply(in Vector3 gyroscope,
        double deadzone, bool horizontal)
    {
        float threshold = (float)Normalize(deadzone);
        if (threshold <= 0.0f)
        {
            return gyroscope;
        }

        return horizontal ? new Vector3(gyroscope.X,
            ApplyAxis(gyroscope.Y, threshold),
            ApplyAxis(gyroscope.Z, threshold)) :
            new Vector3(ApplyAxis(gyroscope.X, threshold), gyroscope.Y,
                ApplyAxis(gyroscope.Z, threshold));
    }

    private static float ApplyAxis(float value, float deadzone) =>
        value > deadzone ? value - deadzone :
            value < -deadzone ? value + deadzone : 0.0f;
}
