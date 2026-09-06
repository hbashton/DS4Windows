/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The activation policy in this file is adapted from the GPL-3.0 licensed
Switch2Connect project, commit 4487322a306f04efa27682e3f3a508635a84fd98,
src/ir_mouse_activation.py. Coordinate decoding and output injection remain
owned by DS4Windows.
*/

using System;

namespace DS4Windows.Switch2;

internal readonly struct Switch2IrCoordinate
{
    internal Switch2IrCoordinate(ushort x, ushort y)
    {
        X = x;
        Y = y;
    }

    internal ushort X { get; }

    internal ushort Y { get; }
}

internal enum Switch2IrVerificationResult : byte
{
    Invalid = 0,
    Continue,
    Reject,
    Latch,
}

internal enum Switch2IrVerificationReason : byte
{
    Invalid = 0,
    Qualified,
    InsufficientMotion,
    SustainedMotion,
    FastMotion,
}

internal readonly struct Switch2IrVerificationEvent
{
    internal Switch2IrVerificationEvent(
        Switch2IrVerificationResult result,
        Switch2IrVerificationReason reason, int samples,
        int movingSamples, int activeBins, long path, int spanX,
        int spanY, int maximumDelta, int qualifiedStreak)
    {
        Result = result;
        Reason = reason;
        Samples = samples;
        MovingSamples = movingSamples;
        ActiveBins = activeBins;
        Path = path;
        SpanX = spanX;
        SpanY = spanY;
        MaximumDelta = maximumDelta;
        QualifiedStreak = qualifiedStreak;
    }

    internal Switch2IrVerificationResult Result { get; }

    internal Switch2IrVerificationReason Reason { get; }

    internal int Samples { get; }

    internal int MovingSamples { get; }

    internal int ActiveBins { get; }

    internal long Path { get; }

    internal int SpanX { get; }

    internal int SpanY { get; }

    internal int MaximumDelta { get; }

    internal int QualifiedStreak { get; }
}

internal struct Switch2IrMouseActivationState
{
    internal bool ModeActive;
    internal long ThresholdSinceMicroseconds;
    internal bool HasThresholdSince;
    internal Switch2IrCoordinate PreviousCoordinates;
    internal bool HasPreviousCoordinates;
    internal bool Latched;
    internal long WindowSinceMicroseconds;
    internal bool HasWindowSince;
    internal Switch2IrCoordinate WindowOrigin;
    internal bool HasWindowOrigin;
    internal int Samples;
    internal int MovingSamples;
    internal byte ActiveBinsMask;
    internal long Path;
    internal int MinimumX;
    internal int MaximumX;
    internal int MinimumY;
    internal int MaximumY;
    internal int MaximumDelta;
    internal int QualifiedStreak;
}

/// <summary>
/// Motion-only activation gate for Joy-Con IR mouse output. The first 600 ms
/// are immediately responsive. Continued use must pass motion verification,
/// preventing a held threshold from latching on stationary/noisy coordinates.
/// All time values are monotonic microseconds supplied by the caller.
/// </summary>
internal static class Switch2IrMouseActivationGate
{
    internal const long FreeMicroseconds = 600_000;
    internal const long VerificationMicroseconds = 300_000;
    internal const int VerificationBins = 3;
    internal const int MinimumMovingSamples = 6;
    internal const int MinimumActiveBins = 2;
    internal const long MinimumPath = 48;
    internal const int MinimumSpan = 24;
    internal const int RequiredWindows = 2;
    internal const long FastPath = 256;
    internal const int FastSpan = 128;

    internal static void Advance(bool thresholdActive,
        in Switch2IrCoordinate coordinates, long nowMicroseconds,
        ref Switch2IrMouseActivationState state,
        out bool hasActivationOrigin,
        out Switch2IrCoordinate activationOrigin,
        out bool hasVerificationEvent,
        out Switch2IrVerificationEvent verificationEvent)
    {
        hasActivationOrigin = false;
        activationOrigin = default;
        hasVerificationEvent = false;
        verificationEvent = default;

        if (!thresholdActive)
        {
            state = default;
            return;
        }

        if (!state.HasPreviousCoordinates)
        {
            state = new Switch2IrMouseActivationState
            {
                ThresholdSinceMicroseconds = nowMicroseconds,
                HasThresholdSince = true,
                PreviousCoordinates = coordinates,
                HasPreviousCoordinates = true,
            };
            return;
        }

        long thresholdSince = state.HasThresholdSince ?
            state.ThresholdSinceMicroseconds : nowMicroseconds;
        int deltaX = LoopingDifference16Bit(state.PreviousCoordinates.X,
            coordinates.X);
        int deltaY = LoopingDifference16Bit(state.PreviousCoordinates.Y,
            coordinates.Y);
        int absoluteDeltaX = Math.Abs(deltaX);
        int absoluteDeltaY = Math.Abs(deltaY);
        bool hasDisplacement = absoluteDeltaX != 0 ||
            absoluteDeltaY != 0;

        if (state.Latched)
        {
            int latchedStreak = state.QualifiedStreak;
            state = new Switch2IrMouseActivationState
            {
                ModeActive = true,
                ThresholdSinceMicroseconds = thresholdSince,
                HasThresholdSince = true,
                PreviousCoordinates = coordinates,
                HasPreviousCoordinates = true,
                Latched = true,
                QualifiedStreak = latchedStreak,
            };
            return;
        }

        if (hasDisplacement && !state.ModeActive)
        {
            hasActivationOrigin = true;
            activationOrigin = state.PreviousCoordinates;
        }

        if (Elapsed(nowMicroseconds, thresholdSince) < FreeMicroseconds)
        {
            state = new Switch2IrMouseActivationState
            {
                ModeActive = hasDisplacement,
                ThresholdSinceMicroseconds = thresholdSince,
                HasThresholdSince = true,
                PreviousCoordinates = coordinates,
                HasPreviousCoordinates = true,
            };
            return;
        }

        if (!state.HasWindowSince || !state.HasWindowOrigin)
        {
            EmptyWindow(ref state, nowMicroseconds,
                state.PreviousCoordinates, hasDisplacement,
                state.QualifiedStreak);
        }

        long elapsed = Elapsed(nowMicroseconds,
            state.WindowSinceMicroseconds);
        long binWidth = VerificationMicroseconds / VerificationBins;
        int binIndex = Math.Min(VerificationBins - 1,
            (int)Math.Min(int.MaxValue, elapsed / binWidth));
        int relativeX = LoopingDifference16Bit(state.WindowOrigin.X,
            coordinates.X);
        int relativeY = LoopingDifference16Bit(state.WindowOrigin.Y,
            coordinates.Y);
        int samples = SaturatingIncrement(state.Samples);
        int movingSamples = hasDisplacement ?
            SaturatingIncrement(state.MovingSamples) : state.MovingSamples;
        byte activeBinsMask = (byte)(state.ActiveBinsMask |
            (hasDisplacement ? 1 << binIndex : 0));
        long path = SaturatingAdd(state.Path,
            (long)absoluteDeltaX + absoluteDeltaY);
        int minimumX = Math.Min(state.MinimumX, relativeX);
        int maximumX = Math.Max(state.MaximumX, relativeX);
        int minimumY = Math.Min(state.MinimumY, relativeY);
        int maximumY = Math.Max(state.MaximumY, relativeY);
        int maximumDelta = Math.Max(state.MaximumDelta,
            absoluteDeltaX + absoluteDeltaY);

        state.ModeActive = hasDisplacement;
        state.ThresholdSinceMicroseconds = thresholdSince;
        state.HasThresholdSince = true;
        state.PreviousCoordinates = coordinates;
        state.HasPreviousCoordinates = true;
        state.Latched = false;
        state.Samples = samples;
        state.MovingSamples = movingSamples;
        state.ActiveBinsMask = activeBinsMask;
        state.Path = path;
        state.MinimumX = minimumX;
        state.MaximumX = maximumX;
        state.MinimumY = minimumY;
        state.MaximumY = maximumY;
        state.MaximumDelta = maximumDelta;

        if (elapsed < VerificationMicroseconds)
        {
            return;
        }

        int activeBins = CountBits(activeBinsMask);
        int spanX = maximumX - minimumX;
        int spanY = maximumY - minimumY;
        int span = Math.Max(spanX, spanY);
        bool normalQualified = movingSamples >= MinimumMovingSamples &&
            activeBins >= MinimumActiveBins && path >= MinimumPath &&
            span >= MinimumSpan;
        bool fastQualified = activeBins == VerificationBins &&
            path >= FastPath && span >= FastSpan;
        int streak = normalQualified ?
            SaturatingIncrement(state.QualifiedStreak) : 0;
        bool shouldLatch = fastQualified || streak >= RequiredWindows;

        if (shouldLatch)
        {
            hasVerificationEvent = true;
            verificationEvent = new Switch2IrVerificationEvent(
                Switch2IrVerificationResult.Latch,
                fastQualified ? Switch2IrVerificationReason.FastMotion :
                    Switch2IrVerificationReason.SustainedMotion,
                samples, movingSamples, activeBins, path, spanX, spanY,
                maximumDelta, streak);
            state = new Switch2IrMouseActivationState
            {
                ModeActive = true,
                ThresholdSinceMicroseconds = thresholdSince,
                HasThresholdSince = true,
                PreviousCoordinates = coordinates,
                HasPreviousCoordinates = true,
                Latched = true,
                QualifiedStreak = streak,
            };
            return;
        }

        hasVerificationEvent = true;
        verificationEvent = new Switch2IrVerificationEvent(
            normalQualified ? Switch2IrVerificationResult.Continue :
                Switch2IrVerificationResult.Reject,
            normalQualified ? Switch2IrVerificationReason.Qualified :
                Switch2IrVerificationReason.InsufficientMotion,
            samples, movingSamples, activeBins, path, spanX, spanY,
            maximumDelta, streak);
        EmptyWindow(ref state, nowMicroseconds, coordinates,
            hasDisplacement, streak);
    }

    internal static int LoopingDifference16Bit(ushort previous,
        ushort current) => unchecked((short)(current - previous));

    private static void EmptyWindow(
        ref Switch2IrMouseActivationState state, long nowMicroseconds,
        in Switch2IrCoordinate coordinates, bool modeActive, int streak)
    {
        long thresholdSince = state.ThresholdSinceMicroseconds;
        bool hasThresholdSince = state.HasThresholdSince;
        state = new Switch2IrMouseActivationState
        {
            ModeActive = modeActive,
            ThresholdSinceMicroseconds = thresholdSince,
            HasThresholdSince = hasThresholdSince,
            PreviousCoordinates = coordinates,
            HasPreviousCoordinates = true,
            WindowSinceMicroseconds = nowMicroseconds,
            HasWindowSince = true,
            WindowOrigin = coordinates,
            HasWindowOrigin = true,
            QualifiedStreak = streak,
        };
    }

    private static long Elapsed(long now, long then) => now >= then ?
        now - then : 0;

    private static int SaturatingIncrement(int value) =>
        value == int.MaxValue ? value : value + 1;

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static int CountBits(byte value)
    {
        int count = 0;
        while (value != 0)
        {
            value &= (byte)(value - 1);
            count++;
        }
        return count;
    }
}
