/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows.Switch2;

/// <summary>
/// Deterministic side-local Xbox impulse-trigger release state. It owns no
/// timer, canonical publication, or physical output. The exact virtual
/// feedback session supplies the clock and presents resolved values through
/// its existing sole owner.
/// </summary>
internal struct Switch2ImpulseReleaseEnvelope
{
    internal const ulong ReleaseDurationMicroseconds = 90_000;
    internal const int PresentationIntervalMilliseconds = 16;

    private SideState left;
    private SideState right;

    internal bool HasPendingRelease => left.IsReleasing || right.IsReleasing;

    internal void Update(ushort leftInput, ushort rightInput,
        ulong nowMicroseconds, out ushort leftOutput,
        out ushort rightOutput)
    {
        leftOutput = UpdateSide(ref left, leftInput, nowMicroseconds);
        rightOutput = UpdateSide(ref right, rightInput, nowMicroseconds);
    }

    internal void Resolve(ulong nowMicroseconds, out ushort leftOutput,
        out ushort rightOutput)
    {
        leftOutput = ResolveSide(ref left, nowMicroseconds);
        rightOutput = ResolveSide(ref right, nowMicroseconds);
    }

    internal void Clear()
    {
        left = default;
        right = default;
    }

    private static ushort UpdateSide(ref SideState side, ushort input,
        ulong nowMicroseconds)
    {
        if (input != 0)
        {
            side = new SideState(input, 0);
            return input;
        }
        if (side.Amplitude == 0)
        {
            side = default;
            return 0;
        }
        if (!side.IsReleasing)
        {
            // QPC-derived timestamps can validly start at zero in tests or
            // immediately after boot. One is the smallest nonzero sentinel.
            side = new SideState(side.Amplitude,
                Math.Max(1UL, nowMicroseconds));
        }
        return ResolveSide(ref side, nowMicroseconds);
    }

    private static ushort ResolveSide(ref SideState side,
        ulong nowMicroseconds)
    {
        if (side.Amplitude == 0)
        {
            return 0;
        }
        if (!side.IsReleasing)
        {
            return side.Amplitude;
        }

        ulong elapsed = nowMicroseconds > side.ReleaseStartedMicroseconds ?
            nowMicroseconds - side.ReleaseStartedMicroseconds : 0;
        if (elapsed >= ReleaseDurationMicroseconds)
        {
            side = default;
            return 0;
        }
        ulong remaining = ReleaseDurationMicroseconds - elapsed;
        return (ushort)((side.Amplitude * remaining) /
            ReleaseDurationMicroseconds);
    }

    private readonly struct SideState
    {
        internal SideState(ushort amplitude,
            ulong releaseStartedMicroseconds)
        {
            Amplitude = amplitude;
            ReleaseStartedMicroseconds = releaseStartedMicroseconds;
        }

        internal ushort Amplitude { get; }
        internal ulong ReleaseStartedMicroseconds { get; }
        internal bool IsReleasing => Amplitude != 0 &&
            ReleaseStartedMicroseconds != 0;
    }
}
