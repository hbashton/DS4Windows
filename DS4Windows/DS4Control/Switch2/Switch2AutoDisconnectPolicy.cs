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
/// Append-only Switch 2 automatic-disconnect policy. LegacyProfile preserves
/// the existing DS4Windows Idle Disconnect setting until a profile explicitly
/// chooses the Switch2Connect-compatible three-way control.
/// </summary>
public enum Switch2AutoDisconnectMode : byte
{
    LegacyProfile = 0,
    Off = 1,
    Inactive = 2,
    Absolute = 3,
}

internal readonly struct Switch2AutoDisconnectPolicy
{
    internal Switch2AutoDisconnectPolicy(Switch2AutoDisconnectMode mode,
        long timeoutSeconds)
    {
        Mode = mode;
        TimeoutSeconds = timeoutSeconds;
    }

    internal Switch2AutoDisconnectMode Mode { get; }
    internal long TimeoutSeconds { get; }
    internal bool Enabled =>
        (Mode is Switch2AutoDisconnectMode.Inactive or
            Switch2AutoDisconnectMode.Absolute) && TimeoutSeconds > 0;
}

internal static class Switch2AutoDisconnectPolicyResolver
{
    internal const long SecondsPerMinute = 60;
    internal const long SecondsPerHour = 60 * SecondsPerMinute;
    internal const long SecondsPerDay = 24 * SecondsPerHour;

    internal static Switch2AutoDisconnectMode NormalizeMode(
        Switch2AutoDisconnectMode mode) => mode is
            Switch2AutoDisconnectMode.LegacyProfile or
            Switch2AutoDisconnectMode.Off or
            Switch2AutoDisconnectMode.Inactive or
            Switch2AutoDisconnectMode.Absolute ?
            mode : Switch2AutoDisconnectMode.LegacyProfile;

    internal static long NormalizeTimeoutSeconds(long timeoutSeconds) =>
        timeoutSeconds < 0 ? 0 : timeoutSeconds;

    internal static Switch2AutoDisconnectPolicy Resolve(
        Switch2AutoDisconnectMode configuredMode,
        long configuredTimeoutSeconds, int legacyIdleTimeoutSeconds)
    {
        Switch2AutoDisconnectMode mode = NormalizeMode(configuredMode);
        if (mode == Switch2AutoDisconnectMode.LegacyProfile)
        {
            return legacyIdleTimeoutSeconds > 0 ?
                new Switch2AutoDisconnectPolicy(
                    Switch2AutoDisconnectMode.Inactive,
                    legacyIdleTimeoutSeconds) :
                new Switch2AutoDisconnectPolicy(
                    Switch2AutoDisconnectMode.Off, 0);
        }

        return new Switch2AutoDisconnectPolicy(mode,
            NormalizeTimeoutSeconds(configuredTimeoutSeconds));
    }

    internal static long ComposeTimeoutSeconds(long days, int hours,
        int minutes)
    {
        if (days < 0 || hours < 0 || hours > 23 || minutes < 0 ||
            minutes > 59)
        {
            return 0;
        }

        if (days > (long.MaxValue -
                hours * SecondsPerHour -
                minutes * SecondsPerMinute) / SecondsPerDay)
        {
            return long.MaxValue;
        }

        return days * SecondsPerDay + hours * SecondsPerHour +
            minutes * SecondsPerMinute;
    }

    internal static void DecomposeTimeoutSeconds(long timeoutSeconds,
        out long days, out int hours, out int minutes)
    {
        long normalized = NormalizeTimeoutSeconds(timeoutSeconds);
        days = normalized / SecondsPerDay;
        long remainder = normalized % SecondsPerDay;
        hours = (int)(remainder / SecondsPerHour);
        minutes = (int)(remainder % SecondsPerHour / SecondsPerMinute);
    }

    internal static long ToQpcTicks(long timeoutSeconds, long qpcFrequency)
    {
        if (timeoutSeconds <= 0 || qpcFrequency <= 0)
        {
            return 0;
        }
        return timeoutSeconds > long.MaxValue / qpcFrequency ?
            long.MaxValue : timeoutSeconds * qpcFrequency;
    }
}
