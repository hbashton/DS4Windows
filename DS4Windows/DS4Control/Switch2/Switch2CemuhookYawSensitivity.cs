/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The five-level yaw multiplier is adapted from the GPL-3.0
Switch2Connect project, commit
61ac6642ce12fe7217e38a860b14863b18ca7e28, src/cemuhook_udp.py. The
physical-family raw gyro scale is intentionally not copied: DS4Windows'
canonical SixAxis state is already expressed in degrees per second.
*/

using System;

namespace DS4Windows.Switch2;

internal static class Switch2CemuhookYawSensitivity
{
    internal const int MinimumLevel = 1;
    internal const int MaximumLevel = 5;
    internal const int DefaultLevel = 1;

    internal static int NormalizeLevel(int level) =>
        level >= MinimumLevel && level <= MaximumLevel ?
            level : DefaultLevel;

    internal static double MultiplierForLevel(int level) =>
        1.0 + (NormalizeLevel(level) - 1.0) / 12.0;

    internal static double ApplyYaw(double yawDegreesPerSecond, int level)
    {
        int normalizedLevel = NormalizeLevel(level);
        if (normalizedLevel == DefaultLevel ||
            !double.IsFinite(yawDegreesPerSecond))
        {
            return yawDegreesPerSecond;
        }

        double projected = yawDegreesPerSecond *
            MultiplierForLevel(normalizedLevel);
        return double.IsFinite(projected) ? projected : yawDegreesPerSecond;
    }
}
