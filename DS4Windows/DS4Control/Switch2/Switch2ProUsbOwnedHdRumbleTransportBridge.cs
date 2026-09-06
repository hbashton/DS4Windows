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
/// Optional local fence implemented by a transport which must keep the
/// canonical writer's exact cached report alive. This property is pure and
/// performs no acquisition or I/O. The legacy transport contract remains
/// unchanged for transports with no retained native-operation lifetime.
/// </summary>
internal interface ISwitch2ProUsbHdRumblePendingReportFence
{
    bool MustRetainPendingReport { get; }
}

internal enum Switch2ProUsbOwnedHdRumbleBridgeState : byte
{
    Invalid = 0,
    NoRetainedOperation,
    RetainedOperation,
    Quarantined,
}

internal enum Switch2ProUsbOwnedHdRumbleDrainOutcome : byte
{
    Invalid = 0,
    NoRetainedOperation,
    ExactOperationQuiescent,
    RetainedForRetry,
    Busy,
    Quarantined,
}

internal enum Switch2ProUsbOwnedHdRumbleClaimProbe : byte
{
    DependencyThrew = 0,
    Rejected,
    Authenticated,
}

/// <summary>
/// Exact, point-in-time evidence from one bridge. The private bridge fence and
/// both generations prevent a numerically identical result from another
/// lifetime from authenticating here. This is output-operation evidence only;
/// it does not seal canonical feedback or prove terminal neutralization.
/// </summary>
internal readonly struct Switch2ProUsbOwnedHdRumbleDrainResult
{
    private readonly object bridgeFence;

    internal Switch2ProUsbOwnedHdRumbleDrainResult(object bridgeFence,
        Switch2ProUsbOwnedHdRumbleDrainOutcome outcome,
        ulong deviceGeneration, ulong transportGeneration,
        ulong stateRevision)
    {
        this.bridgeFence = bridgeFence;
        Outcome = outcome;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
        StateRevision = stateRevision;
    }

    internal Switch2ProUsbOwnedHdRumbleDrainOutcome Outcome { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal ulong StateRevision { get; }

    internal bool HasValidInvariants() => bridgeFence != null &&
        Outcome is >=
            Switch2ProUsbOwnedHdRumbleDrainOutcome.NoRetainedOperation and <=
            Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined &&
        DeviceGeneration != 0 && TransportGeneration != 0 &&
        StateRevision != 0;

    internal bool MatchesIdentity(object expectedBridgeFence,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration) =>
        HasValidInvariants() &&
        ReferenceEquals(bridgeFence, expectedBridgeFence) &&
        DeviceGeneration == expectedDeviceGeneration &&
        TransportGeneration == expectedTransportGeneration;
}

/// <summary>
/// Dormant compatibility bridge from the canonical synchronous USB HD-rumble
/// writer to one exact owned-composite output lease. It adds no mapping,
/// cadence, acquisition, registration, or hardware path.
///
/// One native operation claim may be retained after a bounded write. Before a
/// later report can start, the bridge cancels and drains that exact claim. An
/// exact logical retry therefore reuses the writer's cached 64 bytes and
/// counter; a newer report can supersede it only after the older operation is
/// exactly quiescent. A thrown, malformed, foreign, or claim-inconsistent
/// dependency result permanently quarantines this bridge and is never retried
/// as a clean rejection.
/// </summary>
internal sealed class Switch2ProUsbOwnedHdRumbleTransportBridge :
    ISwitch2ProUsbHdRumbleTransportLease,
    ISwitch2ProUsbHdRumblePendingReportFence
{
    private const Switch2ControllerModel Model =
        Switch2ControllerModel.ProController2;

    private readonly object bridgeFence = new();
    private readonly ISwitch2ProUsbOwnedFeedbackOutputLease lease;
    private readonly ulong deviceGeneration;
    private readonly ulong transportGeneration;
    private readonly int maximumOperationMilliseconds;
    private readonly int operationWaitMilliseconds;
    private readonly byte[] retainedReport =
        new byte[Switch2UsbHdRumbleCodec.ReportLength];

    private Switch2ProUsbOwnedOutputOperationClaim retainedClaim;
    private Switch2ProUsbHdRumbleTransportWriteResult retainedResult;
    private ulong acceptedClaimSequence;
    private long stateRevision = 1;
    private int operationActive;
    private int retained;
    private int quarantined;
    private int disconnectedOutputSealed;

    internal bool TrySealDisconnectedOutput()
    {
        if (Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return false;
        }
        try
        {
            if (State != Switch2ProUsbOwnedHdRumbleBridgeState.NoRetainedOperation)
            {
                return false;
            }
            if (Volatile.Read(ref disconnectedOutputSealed) != 0)
            {
                return true;
            }
            if (!DependencyStillAuthenticates() || !lease.TrySealDisconnectedOutput())
            {
                return false;
            }
            Volatile.Write(ref disconnectedOutputSealed, 1);
            if (!TryAdvanceRevision())
            {
                LatchQuarantine();
                return false;
            }
            return true;
        }
        catch
        {
            LatchQuarantine();
            return false;
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    internal Switch2ProUsbOwnedHdRumbleTransportBridge(
        ISwitch2ProUsbOwnedFeedbackOutputLease lease, ulong deviceGeneration,
        ulong transportGeneration, int operationWaitMilliseconds)
    {
        this.lease = lease ?? throw new ArgumentNullException(nameof(lease));
        if (deviceGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceGeneration));
        }
        if (transportGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transportGeneration));
        }

        int maximumOperationMilliseconds;
        bool authenticates;
        try
        {
            maximumOperationMilliseconds =
                lease.MaximumOutputOperationMilliseconds;
            authenticates = lease.AuthenticatesComposite(Model,
                deviceGeneration, transportGeneration);
        }
        catch (Exception exception)
        {
            throw new ArgumentException(
                "The owned composite lease threw while authenticating the " +
                "USB Pro Controller 2 output lifetime.", nameof(lease),
                exception);
        }

        if (maximumOperationMilliseconds <= 0 ||
            maximumOperationMilliseconds >
                Switch2ProUsbInputTransportOwner.
                    MaximumDisposeTimeoutMilliseconds)
        {
            throw new ArgumentException(
                "The owned composite lease exposes an invalid output wait " +
                "budget.", nameof(lease));
        }
        if (!authenticates)
        {
            throw new ArgumentException(
                "The owned composite lease does not authenticate the USB " +
                "Pro Controller 2 output lifetime.", nameof(lease));
        }
        if (operationWaitMilliseconds <= 0 ||
            operationWaitMilliseconds > maximumOperationMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationWaitMilliseconds));
        }

        this.deviceGeneration = deviceGeneration;
        this.transportGeneration = transportGeneration;
        this.maximumOperationMilliseconds = maximumOperationMilliseconds;
        this.operationWaitMilliseconds = operationWaitMilliseconds;
    }

    internal Switch2ProUsbOwnedHdRumbleBridgeState State =>
        Volatile.Read(ref quarantined) != 0 ?
            Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined :
        Volatile.Read(ref retained) != 0 ?
            Switch2ProUsbOwnedHdRumbleBridgeState.RetainedOperation :
            Switch2ProUsbOwnedHdRumbleBridgeState.NoRetainedOperation;

    /// <summary>
    /// Pure construction-time bound used by the dormant activation lifetime to
    /// reserve enough caller-managed quiescence budget before it enters this
    /// bridge. Synchronous Win32 phases still have no hard wall-clock bound.
    /// </summary>
    internal int OperationWaitMilliseconds => operationWaitMilliseconds;

    public bool MustRetainPendingReport =>
        Volatile.Read(ref retained) != 0 ||
        Volatile.Read(ref quarantined) != 0;

    internal bool Authenticates(
        in Switch2ProUsbOwnedHdRumbleDrainResult result)
    {
        if (!result.MatchesIdentity(bridgeFence, deviceGeneration,
                transportGeneration) ||
            Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            if (result.StateRevision !=
                (ulong)Interlocked.Read(ref stateRevision))
            {
                return false;
            }

            Switch2ProUsbOwnedHdRumbleBridgeState state = State;
            return result.Outcome switch
            {
                Switch2ProUsbOwnedHdRumbleDrainOutcome.
                        NoRetainedOperation or
                    Switch2ProUsbOwnedHdRumbleDrainOutcome.
                        ExactOperationQuiescent => state ==
                    Switch2ProUsbOwnedHdRumbleBridgeState.
                        NoRetainedOperation,
                Switch2ProUsbOwnedHdRumbleDrainOutcome.RetainedForRetry =>
                    state == Switch2ProUsbOwnedHdRumbleBridgeState.
                        RetainedOperation,
                Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined =>
                    state ==
                        Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
                _ => false,
            };
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    /// <summary>
    /// Pure, local identity authentication. Construction already proved the
    /// exact owned lease reference and immutable generations. Keeping this
    /// local lets a canonical retry enter the bridge to drain an exact retained
    /// claim even if a later dependency query would contradict that immutable
    /// proof; that contradiction is then quarantined inside the serialized
    /// operation lane.
    /// </summary>
    public bool Authenticates(Switch2ControllerModel model,
        ulong candidateDeviceGeneration,
        ulong candidateTransportGeneration) => model == Model &&
        candidateDeviceGeneration == deviceGeneration &&
        candidateTransportGeneration == transportGeneration;

    public Switch2ProUsbHdRumbleTransportWriteResult TryWriteReport(
        ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration)
    {
        if (expectedModel != Model ||
            expectedDeviceGeneration != deviceGeneration ||
            expectedTransportGeneration != transportGeneration)
        {
            return Reject(
                Switch2ProUsbHdRumbleTransportWriteFailure.StaleLifetime);
        }
        if (!Switch2UsbHdRumbleCodec.TryDecodeProController(report,
                out _, out _, out _, out _))
        {
            return Reject(
                Switch2ProUsbHdRumbleTransportWriteFailure.InvalidReport);
        }
        if (Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return Reject(Switch2ProUsbHdRumbleTransportWriteFailure.Busy);
        }

        try
        {
            if (Volatile.Read(ref disconnectedOutputSealed) != 0)
            {
                return Reject(Switch2ProUsbHdRumbleTransportWriteFailure.TransportEnded);
            }
            if (Volatile.Read(ref quarantined) != 0)
            {
                return Uncertain(
                    Switch2ProUsbHdRumbleTransportWriteFailure.
                        TransportEnded);
            }

            if (Volatile.Read(ref retained) != 0)
            {
                if (!report.SequenceEqual(retainedReport))
                {
                    return Reject(
                        Switch2ProUsbHdRumbleTransportWriteFailure.Busy);
                }
                Switch2ProUsbOwnedHdRumbleDrainOutcome drainOutcome =
                    RetireExactNoFence();
                if (drainOutcome ==
                    Switch2ProUsbOwnedHdRumbleDrainOutcome.RetainedForRetry)
                {
                    return retainedResult;
                }
                if (drainOutcome !=
                    Switch2ProUsbOwnedHdRumbleDrainOutcome.
                        ExactOperationQuiescent)
                {
                    return Uncertain(
                        Switch2ProUsbHdRumbleTransportWriteFailure.
                            DependencyThrew);
                }
            }

            if (!DependencyStillAuthenticates())
            {
                LatchQuarantine();
                return Uncertain(
                    Switch2ProUsbHdRumbleTransportWriteFailure.
                        DependencyThrew);
            }

            Switch2ProUsbOwnedOutputWriteAttempt attempt;
            try
            {
                // Invalidate older point-in-time quiescence evidence before
                // the dependency can observe or start this new attempt.
                if (!TryAdvanceRevision())
                {
                    LatchQuarantine();
                    return Uncertain(
                        Switch2ProUsbHdRumbleTransportWriteFailure.
                            TransportEnded);
                }
                attempt = lease.TryWriteReportBounded(report, Model,
                    deviceGeneration, transportGeneration,
                    operationWaitMilliseconds);
            }
            catch
            {
                LatchQuarantine();
                return Uncertain(
                    Switch2ProUsbHdRumbleTransportWriteFailure.
                        DependencyThrew);
            }

            if (!attempt.HasValidInvariants() ||
                !attempt.TransportResult.Authenticates(Model,
                    deviceGeneration, transportGeneration))
            {
                LatchQuarantine();
                return Uncertain(
                    Switch2ProUsbHdRumbleTransportWriteFailure.
                        DependencyThrew);
            }

            if (attempt.RequiresTerminalAttention)
            {
                if (ClaimIsFresh(attempt.RetainedClaim) &&
                    ProbeDependencyClaim(attempt.RetainedClaim) ==
                    Switch2ProUsbOwnedHdRumbleClaimProbe.Authenticated)
                {
                    Retain(report, attempt.TransportResult,
                        attempt.RetainedClaim);
                }
                LatchQuarantine();
                return attempt.TransportResult;
            }

            if (attempt.RequiresRetirement)
            {
                if (!ClaimIsFresh(attempt.RetainedClaim) ||
                    ProbeDependencyClaim(attempt.RetainedClaim) !=
                    Switch2ProUsbOwnedHdRumbleClaimProbe.Authenticated)
                {
                    LatchQuarantine();
                    return Uncertain(
                        Switch2ProUsbHdRumbleTransportWriteFailure.
                            DependencyThrew);
                }

                Retain(report, attempt.TransportResult,
                    attempt.RetainedClaim);
            }

            return attempt.TransportResult;
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    /// <summary>
    /// Makes at most one bounded cancel/drain attempt for the exact retained
    /// claim. The result authenticates this bridge and lifetime, but it is only
    /// point-in-time output-operation evidence; it does not seal the writer or
    /// prove a terminal neutral report.
    /// </summary>
    internal Switch2ProUsbOwnedHdRumbleDrainResult
        TryRetireRetainedOperation()
    {
        if (Interlocked.CompareExchange(ref operationActive, 1, 0) != 0)
        {
            return DrainResult(
                Switch2ProUsbOwnedHdRumbleDrainOutcome.Busy);
        }

        try
        {
            if (Volatile.Read(ref quarantined) != 0)
            {
                return DrainResult(
                    Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined);
            }
            if (Volatile.Read(ref retained) == 0)
            {
                return DrainResult(
                    Switch2ProUsbOwnedHdRumbleDrainOutcome.
                        NoRetainedOperation);
            }

            return DrainResult(RetireExactNoFence());
        }
        finally
        {
            Volatile.Write(ref operationActive, 0);
        }
    }

    private Switch2ProUsbOwnedHdRumbleDrainOutcome RetireExactNoFence()
    {
        Switch2ProUsbOwnedOutputOperationClaim claim = retainedClaim;
        if (Volatile.Read(ref retained) == 0 || !claim.IsValid ||
            claim.DeviceGeneration != deviceGeneration ||
            claim.TransportGeneration != transportGeneration ||
            ProbeDependencyClaim(claim) !=
                Switch2ProUsbOwnedHdRumbleClaimProbe.Authenticated)
        {
            LatchQuarantine();
            return Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined;
        }

        Switch2ProUsbOwnedOutputRetirementResult retirement;
        try
        {
            retirement = lease.TryRetireOutputOperation(claim,
                operationWaitMilliseconds);
        }
        catch
        {
            LatchQuarantine();
            return Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined;
        }

        if (!retirement.HasValidInvariants() ||
            !retirement.Claim.Equals(claim))
        {
            LatchQuarantine();
            return Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined;
        }

        switch (retirement.Outcome)
        {
            case Switch2ProUsbOwnedOutputRetirementOutcome.
                    ExactOperationQuiescent:
                if (ProbeDependencyClaim(claim) !=
                    Switch2ProUsbOwnedHdRumbleClaimProbe.Rejected)
                {
                    LatchQuarantine();
                    return Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined;
                }
                retainedClaim = default;
                retainedResult = default;
                retainedReport.AsSpan().Clear();
                Volatile.Write(ref retained, 0);
                TryAdvanceRevision();
                return Switch2ProUsbOwnedHdRumbleDrainOutcome.
                    ExactOperationQuiescent;

            case Switch2ProUsbOwnedOutputRetirementOutcome.RetainedForRetry:
                if (ProbeDependencyClaim(claim) !=
                    Switch2ProUsbOwnedHdRumbleClaimProbe.Authenticated)
                {
                    LatchQuarantine();
                    return Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined;
                }
                return Switch2ProUsbOwnedHdRumbleDrainOutcome.
                    RetainedForRetry;

            case Switch2ProUsbOwnedOutputRetirementOutcome.Quarantined:
            case Switch2ProUsbOwnedOutputRetirementOutcome.RequestRejected:
            default:
                LatchQuarantine();
                return Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined;
        }
    }

    private bool DependencyStillAuthenticates()
    {
        try
        {
            int currentMaximum = lease.MaximumOutputOperationMilliseconds;
            return currentMaximum == maximumOperationMilliseconds &&
                lease.AuthenticatesComposite(Model, deviceGeneration,
                    transportGeneration);
        }
        catch
        {
            return false;
        }
    }

    private Switch2ProUsbOwnedHdRumbleClaimProbe ProbeDependencyClaim(
        in Switch2ProUsbOwnedOutputOperationClaim claim)
    {
        try
        {
            return lease.AuthenticatesOutputOperationClaim(claim) ?
                Switch2ProUsbOwnedHdRumbleClaimProbe.Authenticated :
                Switch2ProUsbOwnedHdRumbleClaimProbe.Rejected;
        }
        catch
        {
            return Switch2ProUsbOwnedHdRumbleClaimProbe.DependencyThrew;
        }
    }

    private bool ClaimMatchesLifetime(
        in Switch2ProUsbOwnedOutputOperationClaim claim) => claim.IsValid &&
        claim.DeviceGeneration == deviceGeneration &&
        claim.TransportGeneration == transportGeneration;

    private bool ClaimIsFresh(
        in Switch2ProUsbOwnedOutputOperationClaim claim) =>
        ClaimMatchesLifetime(claim) &&
        claim.Sequence > acceptedClaimSequence;

    private void Retain(ReadOnlySpan<byte> report,
        in Switch2ProUsbHdRumbleTransportWriteResult result,
        in Switch2ProUsbOwnedOutputOperationClaim claim)
    {
        report.CopyTo(retainedReport);
        retainedResult = result;
        retainedClaim = claim;
        acceptedClaimSequence = claim.Sequence;
        Volatile.Write(ref retained, 1);
        TryAdvanceRevision();
    }

    private void LatchQuarantine()
    {
        if (Interlocked.Exchange(ref quarantined, 1) == 0)
        {
            TryAdvanceRevision();
        }
    }

    private bool TryAdvanceRevision()
    {
        long observed = Interlocked.Read(ref stateRevision);
        while (observed != long.MaxValue)
        {
            long previous = Interlocked.CompareExchange(ref stateRevision,
                observed + 1, observed);
            if (previous == observed)
            {
                return true;
            }
            observed = previous;
        }
        return false;
    }

    private Switch2ProUsbOwnedHdRumbleDrainResult DrainResult(
        Switch2ProUsbOwnedHdRumbleDrainOutcome outcome) => new(bridgeFence,
        outcome, deviceGeneration, transportGeneration,
        (ulong)Interlocked.Read(ref stateRevision));

    private Switch2ProUsbHdRumbleTransportWriteResult Reject(
        Switch2ProUsbHdRumbleTransportWriteFailure failure) =>
        Switch2ProUsbHdRumbleTransportWriteResult.Reject(Model,
            deviceGeneration, transportGeneration, failure);

    private Switch2ProUsbHdRumbleTransportWriteResult Uncertain(
        Switch2ProUsbHdRumbleTransportWriteFailure failure) =>
        Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(Model,
            deviceGeneration, transportGeneration, failure);
}
