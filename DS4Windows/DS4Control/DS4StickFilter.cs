/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Threading;
using DS4Windows.Switch2;

namespace DS4Windows;

/// <summary>
/// The existing stick anti-snapback geometry and radial fuzz policy, operating
/// on mapping-owned coordinates. Single input-thread owner; no transport I/O.
/// </summary>
internal sealed class DS4StickFilter
{
    // Includes a full one-second UI window at 8,000 samples/s plus its boundary.
    // Outside this bound, bypass only anti-snapback until missing history expires;
    // never invent a suppression decision from a silently shortened window.
    internal const int HistoryCapacity = 8192;
    private readonly Sample[] history = new Sample[HistoryCapacity];
    private int head, count;
    private long lastTimestamp, bypassThrough = -1;
    private bool snapbackEnabled;
    private double snapbackDelta;
    private int snapbackTimeout, fuzzDelta;
    private DS4MappedStickAxis lastX, lastY;

    private readonly record struct Sample(double X, double Y, long Timestamp);

    internal int HistoryCount => count;
    internal long HistoryOverflowCount { get; private set; }

    internal void Reset()
    {
        head = count = 0;
        lastTimestamp = 0;
        bypassThrough = -1;
        snapbackEnabled = false;
        snapbackDelta = snapbackTimeout = fuzzDelta = 0;
        lastX = lastY = default;
    }

    internal void ApplySnapback(bool enabled, double delta, int timeoutMs,
        long timestampMs, ref DS4MappedStickAxis x, ref DS4MappedStickAxis y)
    {
        enabled &= double.IsFinite(delta) && delta >= 0.0 && delta <= 256.0;
        int timeout = Math.Clamp(timeoutMs, 0, 1000);
        if (snapbackEnabled != enabled || !snapbackDelta.Equals(delta) ||
            snapbackTimeout != timeout || timestampMs < lastTimestamp)
        {
            head = count = 0;
            bypassThrough = -1;
        }
        snapbackEnabled = enabled;
        snapbackDelta = delta;
        snapbackTimeout = timeout;
        lastTimestamp = timestampMs;
        if (!enabled || timestampMs < 0)
            return;

        long cutoff = timestampMs - timeout;
        while (count != 0 && history[head].Timestamp < cutoff)
        {
            head = (head + 1) % HistoryCapacity;
            count--;
        }
        if (count == HistoryCapacity)
        {
            head = count = 0;
            HistoryOverflowCount++;
            bypassThrough = timestampMs > long.MaxValue - timeout ?
                long.MaxValue : timestampMs + timeout;
        }

        double currentX = x.ProfileCoordinate, currentY = y.ProfileCoordinate;
        double thresholdSquared = delta * delta;
        // Keep collecting during bypass so resumption has a complete live window.
        // A further overflow moves the bypass boundary forward again.
        for (int i = 0; timestampMs > bypassThrough && i < count; i++)
        {
            Sample previous = history[(head + i) % HistoryCapacity];
            double dx = previous.X - currentX, dy = previous.Y - currentY;
            double distanceSquared = dx * dx + dy * dy;
            // A zero-length segment never suppressed in the original formula
            // (its division yielded NaN). Handle it explicitly.
            if (distanceSquared == 0.0 || distanceSquared < thresholdSquared)
                continue;
            double t = Math.Clamp(((128.0 - currentX) * dx +
                (128.0 - currentY) * dy) / distanceSquared, 0.0, 1.0);
            double centerX = 128.0 - (currentX + t * dx);
            double centerY = 128.0 - (currentY + t * dy);
            if (centerX * centerX + centerY * centerY <= 15.0 * 15.0)
            {
                bool precise = x.IsHighResolution || y.IsHighResolution;
                x = y = precise ? DS4MappedStickAxis.FromSigned(0) : default;
                break;
            }
        }
        history[(head + count) % HistoryCapacity] = new Sample(currentX, currentY, timestampMs);
        count++;
    }

    internal void ApplyFuzz(int delta, ref DS4MappedStickAxis x, ref DS4MappedStickAxis y)
    {
        delta = Math.Max(0, delta);
        if (fuzzDelta != delta)
        {
            lastX = lastY = default;
            fuzzDelta = delta;
        }
        if (delta == 0)
            return;
        double dx = x.ProfileCoordinate - lastX.ProfileCoordinate;
        double dy = y.ProfileCoordinate - lastY.ProfileCoordinate;
        bool moved = dx * dx + dy * dy > (double)delta * delta;
        if (x.ProfileCoordinate == 0.0 || x.ProfileCoordinate == 255.0 || moved)
            lastX = x;
        if (y.ProfileCoordinate == 0.0 || y.ProfileCoordinate == 255.0 || moved)
            lastY = y;
        x = lastX;
        y = lastY;
    }
}

/// <summary>Slot-local history with source/profile/coordinate-system fences.</summary>
internal sealed class DS4StickFilterSet
{
    internal readonly DS4StickFilter Left = new();
    internal readonly DS4StickFilter Right = new();
    private readonly WeakReference<object> source = new(null);
    private bool hasSourceOwner;
    private SourceBoundary boundary;
    private long revision, resetRequest, observedReset;
    private double leftRotation, rightRotation;

    // Profile/UI code requests a reset; only the input owner mutates the filters.
    internal void RequestReset() => Interlocked.Increment(ref resetRequest);

    internal void Prepare(object sourceOwner, DS4State state, long profileRevision,
        double lsRotation, double rsRotation)
    {
        var next = SourceBoundary.From(state);
        long reset = Volatile.Read(ref resetRequest);
        bool sourceAlive = source.TryGetTarget(out object previousSource);
        bool sourceChanged = (hasSourceOwner && !sourceAlive) || !ReferenceEquals(previousSource, sourceOwner);
        if (sourceChanged || revision != profileRevision ||
            observedReset != reset || !boundary.Equals(next) ||
            !leftRotation.Equals(lsRotation) || !rightRotation.Equals(rsRotation))
        {
            Left.Reset();
            Right.Reset();
        }
        if (sourceChanged) source.SetTarget(sourceOwner);
        hasSourceOwner = sourceOwner != null;
        revision = profileRevision;
        observedReset = reset;
        boundary = next;
        leftRotation = lsRotation;
        rightRotation = rsRotation;
    }

    private readonly record struct SourceBoundary(bool Pro, ushort ProVersion,
        ulong ProDevice, ulong ProTransport, bool JoyCon, ushort JoyConVersion,
        Switch2JoyConProfileMode Mode, ulong Pair, bool LeftPresent, ulong LeftDevice,
        ulong LeftTransport, bool RightPresent, ulong RightDevice, ulong RightTransport)
    {
        // Metadata is used only to fence history, never as mapped axis output.
        internal static SourceBoundary From(DS4State state)
        {
            var pro = state.Switch2RawInputStatus;
            var joyCon = state.Switch2JoyConRawInputStatus;
            return new(pro.IsValid, pro.ContractVersion, pro.DeviceGeneration,
                pro.TransportGeneration, joyCon.IsValid, joyCon.ContractVersion,
                joyCon.Mode, joyCon.PairEpoch, joyCon.LeftPresent,
                joyCon.LeftDeviceGeneration, joyCon.LeftTransportGeneration,
                joyCon.RightPresent, joyCon.RightDeviceGeneration, joyCon.RightTransportGeneration);
        }
    }
}
