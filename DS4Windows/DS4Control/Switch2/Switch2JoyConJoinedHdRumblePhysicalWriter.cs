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

namespace DS4Windows.Switch2;

/// <summary>
/// One joined logical output writer over the two exact Joy-Con 2 BLE output
/// lifetimes. It adds no queue or cadence: the canonical delivery call writes
/// left then right immediately. A successful first-half write followed by any
/// second-half failure is conservatively reported as uncertain because the
/// physical effect cannot be rolled back.
/// </summary>
internal sealed class Switch2JoyConJoinedHdRumblePhysicalWriter :
    ISwitch2HdRumblePhysicalWriter
{
    private readonly ISwitch2HdRumblePhysicalWriter left;
    private readonly ISwitch2HdRumblePhysicalWriter right;
    private readonly ulong logicalDeviceGeneration;
    private readonly ulong logicalTransportGeneration;
    private readonly ulong leftDeviceGeneration;
    private readonly ulong leftTransportGeneration;
    private readonly ulong rightDeviceGeneration;
    private readonly ulong rightTransportGeneration;
    private int writeActive;
    private int retirementOnly;

    // Cold physical-loss cleanup, not a successful two-actuator delivery.
    // Caller has exact native release proof for each skipped target. A live
    // survivor still receives a real framed Stop through its existing writer.
    internal bool TryStopSurvivingTargets(bool leftReleased, bool rightReleased)
    {
        if (!leftReleased && !rightReleased) return false;
        Volatile.Write(ref retirementOnly, 1);
        if (Interlocked.CompareExchange(ref writeActive, 1, 0) != 0) return false;
        try
        {
            bool leftStopped = leftReleased || left.TryWrite(Switch2HdRumblePhysicalSubmission.
                CreateStop(leftDeviceGeneration, leftTransportGeneration, ulong.MaxValue)).Succeeded;
            bool rightStopped = rightReleased || right.TryWrite(Switch2HdRumblePhysicalSubmission.
                CreateStop(rightDeviceGeneration, rightTransportGeneration, ulong.MaxValue)).Succeeded;
            return leftStopped && rightStopped;
        }
        catch { return false; }
        finally { Volatile.Write(ref writeActive, 0); }
    }

    internal Switch2JoyConJoinedHdRumblePhysicalWriter(
        ISwitch2HdRumblePhysicalWriter left,
        ISwitch2HdRumblePhysicalWriter right,
        ulong logicalDeviceGeneration, ulong logicalTransportGeneration,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration)
    {
        this.left = left ?? throw new ArgumentNullException(nameof(left));
        this.right = right ?? throw new ArgumentNullException(nameof(right));
        if (logicalDeviceGeneration == 0 ||
            logicalTransportGeneration == 0 ||
            leftDeviceGeneration == 0 || leftTransportGeneration == 0 ||
            rightDeviceGeneration == 0 || rightTransportGeneration == 0 ||
            !left.Authenticates(leftDeviceGeneration,
                leftTransportGeneration) ||
            !right.Authenticates(rightDeviceGeneration,
                rightTransportGeneration))
        {
            throw new ArgumentException(
                "The joined physical writers do not authenticate the exact pair.");
        }

        this.logicalDeviceGeneration = logicalDeviceGeneration;
        this.logicalTransportGeneration = logicalTransportGeneration;
        this.leftDeviceGeneration = leftDeviceGeneration;
        this.leftTransportGeneration = leftTransportGeneration;
        this.rightDeviceGeneration = rightDeviceGeneration;
        this.rightTransportGeneration = rightTransportGeneration;
    }

    public bool Authenticates(ulong deviceGeneration,
        ulong transportGeneration) => deviceGeneration ==
            logicalDeviceGeneration && transportGeneration ==
            logicalTransportGeneration &&
        left.Authenticates(leftDeviceGeneration, leftTransportGeneration) &&
        right.Authenticates(rightDeviceGeneration,
            rightTransportGeneration);

    public Switch2HdRumblePhysicalWriteResult TryWrite(
        in Switch2HdRumblePhysicalSubmission submission)
    {
        if (!submission.HasValidInvariants() ||
            submission.DeviceGeneration != logicalDeviceGeneration ||
            submission.TransportGeneration != logicalTransportGeneration)
        {
            return Switch2HdRumblePhysicalWriteResult.Reject(
                Switch2HdRumblePhysicalWriteFailure.InvalidSubmission);
        }
        if (Interlocked.CompareExchange(ref writeActive, 1, 0) != 0)
        {
            return Switch2HdRumblePhysicalWriteResult.Reject(
                Switch2HdRumblePhysicalWriteFailure.Busy);
        }

        try
        {
            if (Volatile.Read(ref retirementOnly) != 0)
                return Switch2HdRumblePhysicalWriteResult.Reject(Switch2HdRumblePhysicalWriteFailure.StaleLifetime);
            if (!Authenticates(logicalDeviceGeneration,
                    logicalTransportGeneration) ||
                !submission.TryRebind(leftDeviceGeneration,
                    leftTransportGeneration, out var leftSubmission) ||
                !submission.TryRebind(rightDeviceGeneration,
                    rightTransportGeneration, out var rightSubmission))
            {
                return Switch2HdRumblePhysicalWriteResult.Reject(
                    Switch2HdRumblePhysicalWriteFailure.StaleLifetime);
            }

            Switch2HdRumblePhysicalWriteResult leftResult;
            try
            {
                leftResult = left.TryWrite(leftSubmission);
            }
            catch
            {
                return Switch2HdRumblePhysicalWriteResult.Uncertain(
                    Switch2HdRumblePhysicalWriteFailure.DependencyThrew);
            }
            if (!leftResult.IsValid)
            {
                return Switch2HdRumblePhysicalWriteResult.Uncertain(
                    Switch2HdRumblePhysicalWriteFailure.DependencyThrew);
            }
            if (!leftResult.Succeeded)
            {
                return leftResult.IsUncertain ? leftResult :
                    Switch2HdRumblePhysicalWriteResult.Reject(
                        leftResult.Failure);
            }

            Switch2HdRumblePhysicalWriteResult rightResult;
            try
            {
                rightResult = right.TryWrite(rightSubmission);
            }
            catch
            {
                return Switch2HdRumblePhysicalWriteResult.Uncertain(
                    Switch2HdRumblePhysicalWriteFailure.DependencyThrew);
            }
            if (!rightResult.IsValid || !rightResult.Succeeded)
            {
                return Switch2HdRumblePhysicalWriteResult.Uncertain(
                    rightResult.IsValid ? rightResult.Failure :
                        Switch2HdRumblePhysicalWriteFailure.DependencyThrew);
            }
            return Switch2HdRumblePhysicalWriteResult.Success();
        }
        finally
        {
            Volatile.Write(ref writeActive, 0);
        }
    }
}
