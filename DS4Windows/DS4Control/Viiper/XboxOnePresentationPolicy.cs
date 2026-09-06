/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;
using System.Globalization;

namespace DS4Windows
{
    /// <summary>
    /// Parses the operator-declared Xbox One ordered-history policy once per
    /// virtual-device lifetime. This is the same duration-only contract used
    /// by the existing Xbox 360 and Switch 2 schedulers; each process keeps its
    /// own monotonic clock domain.
    /// </summary>
    internal static class XboxOnePresentationPolicy
    {
        internal const string MaximumOrderedAgeEnvironmentVariable =
            "DS4W_VIIPER_XBOXONE_MAX_ORDERED_AGE_MS";
        internal const int MaximumSupportedOrderedAgeMilliseconds = 60_000;

        internal static bool TryParseMaximumOrderedAgeMilliseconds(
            string value, out int milliseconds, out string error)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                milliseconds = 0;
                error = null;
                return true;
            }

            if (!int.TryParse(value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out milliseconds) ||
                milliseconds < 1 ||
                milliseconds > MaximumSupportedOrderedAgeMilliseconds)
            {
                milliseconds = 0;
                error = $"{MaximumOrderedAgeEnvironmentVariable} must be a " +
                    $"whole number from 1 through " +
                    $"{MaximumSupportedOrderedAgeMilliseconds}.";
                return false;
            }

            error = null;
            return true;
        }

        internal static long ToStopwatchTicks(int milliseconds)
        {
            if (milliseconds == 0)
            {
                return 0;
            }
            if (milliseconds < 0 ||
                milliseconds > MaximumSupportedOrderedAgeMilliseconds)
            {
                throw new ArgumentOutOfRangeException(nameof(milliseconds));
            }

            return checked((long)Math.Ceiling(
                milliseconds * (double)Stopwatch.Frequency / 1000.0));
        }
    }
}
