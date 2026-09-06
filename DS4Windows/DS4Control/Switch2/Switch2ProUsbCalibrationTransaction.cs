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

namespace DS4Windows.Switch2;

internal enum Switch2ProUsbCalibrationReadFailure : byte
{
    None = 0,
    MissingLease,
    InvalidLifetime,
    StartupNotCompleted,
    InvalidTimeout,
    RequestEncodingRejected,
    ProvenNotConsumed,
    DependencyThrew,
    MalformedCompletion,
    WrongClaim,
    WrongStep,
    WrongResponseProof,
    CommandTimedOut,
    PossiblyConsumed,
    RetirementFailed,
    SnapshotRejected,
}

internal readonly struct Switch2ProUsbCalibrationReadResult
{
    internal Switch2ProUsbCalibrationReadResult(
        Switch2ProUsbCalibrationReadFailure failure,
        Switch2ProUsbStartupRetirementFailure retirementFailure,
        in Switch2InputCalibrationSnapshot calibration)
    {
        Failure = failure;
        RetirementFailure = retirementFailure;
        Calibration = calibration;
    }

    internal Switch2ProUsbCalibrationReadFailure Failure { get; }

    internal Switch2ProUsbStartupRetirementFailure RetirementFailure
    {
        get;
    }

    internal Switch2InputCalibrationSnapshot Calibration { get; }

    internal bool Succeeded =>
        Failure == Switch2ProUsbCalibrationReadFailure.None;

    /// <summary>
    /// True only when no command byte was accepted and no completion can
    /// arrive. The already-completed startup transaction may then continue
    /// with the established centered wired fallback.
    /// </summary>
    internal bool CanUseCenteredFallback =>
        Failure == Switch2ProUsbCalibrationReadFailure.ProvenNotConsumed;

    internal bool RequiresQuarantine =>
        Failure is Switch2ProUsbCalibrationReadFailure.DependencyThrew or
            Switch2ProUsbCalibrationReadFailure.MalformedCompletion or
            Switch2ProUsbCalibrationReadFailure.WrongClaim or
            Switch2ProUsbCalibrationReadFailure.WrongStep or
            Switch2ProUsbCalibrationReadFailure.WrongResponseProof or
            Switch2ProUsbCalibrationReadFailure.CommandTimedOut or
            Switch2ProUsbCalibrationReadFailure.PossiblyConsumed or
            Switch2ProUsbCalibrationReadFailure.RetirementFailed;
}

/// <summary>
/// One-shot, post-startup read of the four allowlisted Pro USB calibration
/// records. It mints exact claims against the same MI_01 lease used by startup;
/// no second handle, reader, scheduler, or runtime hot-path work is introduced.
/// </summary>
internal sealed class Switch2ProUsbCalibrationTransaction
{
    internal const int RequiredReadCount = 4;
    private const int MaximumCommandWaitMilliseconds = 1_000;

    private readonly object transactionFence = new();
    private readonly ISwitch2ProUsbCalibrationCommandLease lease;
    private readonly Switch2PhysicalInputLifetime lifetime;
    private ulong commandSequence;
    private ulong retirementSequence;
    private bool consumed;

    private Switch2ProUsbCalibrationTransaction(
        ISwitch2ProUsbCalibrationCommandLease lease,
        in Switch2PhysicalInputLifetime lifetime)
    {
        this.lease = lease;
        this.lifetime = lifetime;
    }

    internal static bool TryCreate(
        ISwitch2ProUsbCalibrationCommandLease lease,
        in Switch2PhysicalInputLifetime expectedLifetime,
        Switch2ProUsbStartupTransaction completedStartup,
        out Switch2ProUsbCalibrationTransaction transaction,
        out Switch2ProUsbCalibrationReadFailure failure)
    {
        transaction = null;
        if (lease == null)
        {
            failure = Switch2ProUsbCalibrationReadFailure.MissingLease;
            return false;
        }
        if (!expectedLifetime.IsValid || !lease.Lifetime.Equals(
                expectedLifetime))
        {
            failure = Switch2ProUsbCalibrationReadFailure.InvalidLifetime;
            return false;
        }
        if (completedStartup == null ||
            !completedStartup.AuthenticatesCompleted(lease,
                expectedLifetime))
        {
            failure = Switch2ProUsbCalibrationReadFailure.
                StartupNotCompleted;
            return false;
        }

        transaction = new Switch2ProUsbCalibrationTransaction(lease,
            expectedLifetime);
        failure = Switch2ProUsbCalibrationReadFailure.None;
        return true;
    }

    internal bool TryRead(int timeoutMilliseconds,
        out Switch2ProUsbCalibrationReadResult result)
    {
        if (consumed || timeoutMilliseconds <= 0 ||
            timeoutMilliseconds >
                Switch2ProUsbStartupTransaction.
                    MaximumOperationTimeoutMilliseconds)
        {
            result = Failed(
                Switch2ProUsbCalibrationReadFailure.InvalidTimeout);
            return false;
        }
        consumed = true;

        byte[] leftFactory = null;
        byte[] rightFactory = null;
        byte[] leftUser = null;
        byte[] rightUser = null;
        long deadline = StartDeadline(timeoutMilliseconds);
        Span<byte> request = stackalloc byte[
            Switch2UsbCommandCodec.CalibrationReadRequestLength];

        for (int index = 0; index < RequiredReadCount; index++)
        {
            Switch2UsbCalibrationRead read = (Switch2UsbCalibrationRead)
                (index + (int)Switch2UsbCalibrationRead.FactoryPrimary);
            Switch2ProUsbStartupStep step = MapStep(read);
            int remaining = RemainingMilliseconds(deadline);
            if (remaining <= 0)
            {
                result = Failed(
                    Switch2ProUsbCalibrationReadFailure.ProvenNotConsumed);
                return false;
            }

            if (!Switch2UsbCommandCodec.TryWriteCalibrationReadRequest(read,
                    request, out _) ||
                !Switch2UsbCommandCodec.TryValidateCalibrationReadRequest(
                    request, read, out _))
            {
                result = Failed(Switch2ProUsbCalibrationReadFailure.
                    RequestEncodingRejected);
                return false;
            }

            commandSequence++;
            var claim = new Switch2ProUsbStartupCommandClaim(
                transactionFence, lease, lifetime, step, commandSequence);
            Switch2ProUsbStartupCommandCompletion completion = default;
            bool dependencyThrew = false;
            try
            {
                completion = lease.Execute(claim, request,
                    Math.Min(remaining, MaximumCommandWaitMilliseconds));
            }
            catch
            {
                dependencyThrew = true;
            }

            Switch2ProUsbCalibrationReadFailure commandFailure =
                dependencyThrew ?
                    Switch2ProUsbCalibrationReadFailure.DependencyThrew :
                    Classify(claim, step, read, completion);
            if (commandFailure != Switch2ProUsbCalibrationReadFailure.None)
            {
                if (commandFailure == Switch2ProUsbCalibrationReadFailure.
                        ProvenNotConsumed)
                {
                    result = Failed(commandFailure);
                    return false;
                }

                Switch2ProUsbStartupRetirementFailure retirementFailure =
                    RetireUncertain(deadline);
                result = retirementFailure ==
                        Switch2ProUsbStartupRetirementFailure.None ?
                    Failed(commandFailure, retirementFailure) :
                    Failed(Switch2ProUsbCalibrationReadFailure.
                        RetirementFailed, retirementFailure);
                return false;
            }

            byte[] payload = completion.ResponsePayload.ToArray();
            switch (read)
            {
                case Switch2UsbCalibrationRead.FactoryPrimary:
                    leftFactory = payload;
                    break;
                case Switch2UsbCalibrationRead.FactorySecondary:
                    rightFactory = payload;
                    break;
                case Switch2UsbCalibrationRead.UserPrimary:
                    leftUser = payload;
                    break;
                case Switch2UsbCalibrationRead.UserSecondary:
                    rightUser = payload;
                    break;
            }
        }

        if (!Switch2InputCalibrationSnapshot.TryCreateProUsb(
                lifetime.SessionDescriptor.DeviceGeneration,
                leftFactory, rightFactory, leftUser, rightUser,
                out Switch2InputCalibrationSnapshot calibration))
        {
            result = Failed(
                Switch2ProUsbCalibrationReadFailure.SnapshotRejected);
            return false;
        }

        result = new Switch2ProUsbCalibrationReadResult(
            Switch2ProUsbCalibrationReadFailure.None,
            Switch2ProUsbStartupRetirementFailure.None, calibration);
        return true;
    }

    private Switch2ProUsbCalibrationReadFailure Classify(
        in Switch2ProUsbStartupCommandClaim claim,
        Switch2ProUsbStartupStep step, Switch2UsbCalibrationRead read,
        in Switch2ProUsbStartupCommandCompletion completion)
    {
        if (completion.Outcome == Switch2ProUsbStartupCommandOutcome.Invalid)
        {
            return Switch2ProUsbCalibrationReadFailure.MalformedCompletion;
        }
        if (!claim.Authenticates(transactionFence, lease, lifetime, step,
                claim.Sequence) || !completion.Claim.Equals(claim))
        {
            return Switch2ProUsbCalibrationReadFailure.WrongClaim;
        }
        if (completion.ReportedStep != step)
        {
            return Switch2ProUsbCalibrationReadFailure.WrongStep;
        }

        switch (completion.Outcome)
        {
            case Switch2ProUsbStartupCommandOutcome.ExactResponseCompleted:
                if (completion.ResponseProof !=
                    Switch2ProUsbStartupResponseProofKind.
                        CalibrationReadResponseValidatedByCodec)
                {
                    return Switch2ProUsbCalibrationReadFailure.
                        WrongResponseProof;
                }
                int expectedLength = read is
                    Switch2UsbCalibrationRead.FactoryPrimary or
                    Switch2UsbCalibrationRead.FactorySecondary ?
                    Switch2CalibrationCodec.StickCalibrationLength :
                    Switch2CalibrationCodec.UserStickCalibrationLength;
                return completion.ResponsePayload.Length == expectedLength ?
                    Switch2ProUsbCalibrationReadFailure.None :
                    Switch2ProUsbCalibrationReadFailure.MalformedCompletion;
            case Switch2ProUsbStartupCommandOutcome.ProvenNotConsumed:
                return completion.ResponseProof == default &&
                        completion.ResponsePayload.IsEmpty ?
                    Switch2ProUsbCalibrationReadFailure.ProvenNotConsumed :
                    Switch2ProUsbCalibrationReadFailure.MalformedCompletion;
            case Switch2ProUsbStartupCommandOutcome.TimedOut:
                return Switch2ProUsbCalibrationReadFailure.CommandTimedOut;
            case Switch2ProUsbStartupCommandOutcome.PossiblyConsumed:
                return Switch2ProUsbCalibrationReadFailure.PossiblyConsumed;
            default:
                return Switch2ProUsbCalibrationReadFailure.
                    MalformedCompletion;
        }
    }

    private Switch2ProUsbStartupRetirementFailure RetireUncertain(
        long deadline)
    {
        retirementSequence++;
        var claim = new Switch2ProUsbStartupRetirementClaim(
            transactionFence, lease, lifetime,
            Switch2ProUsbStartupRetirementReason.CommandOutcomeUncertain,
            retirementSequence);
        Switch2ProUsbStartupRetirementCompletion completion = default;
        try
        {
            completion = lease.Retire(claim,
                Math.Min(Math.Max(0, RemainingMilliseconds(deadline)),
                    MaximumCommandWaitMilliseconds));
        }
        catch
        {
            return Switch2ProUsbStartupRetirementFailure.DependencyThrew;
        }

        if (completion.Outcome ==
                Switch2ProUsbStartupRetirementOutcome.Invalid ||
            !claim.Authenticates(transactionFence, lease, lifetime,
                Switch2ProUsbStartupRetirementReason.
                    CommandOutcomeUncertain, claim.Sequence) ||
            !completion.Claim.Equals(claim) ||
            completion.ReportedReason !=
                Switch2ProUsbStartupRetirementReason.CommandOutcomeUncertain)
        {
            return Switch2ProUsbStartupRetirementFailure.
                MalformedCompletion;
        }

        return completion.Outcome switch
        {
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased =>
                Switch2ProUsbStartupRetirementFailure.None,
            Switch2ProUsbStartupRetirementOutcome.ProvenNotReleased =>
                Switch2ProUsbStartupRetirementFailure.ProvenNotReleased,
            Switch2ProUsbStartupRetirementOutcome.TimedOut =>
                Switch2ProUsbStartupRetirementFailure.TimedOut,
            Switch2ProUsbStartupRetirementOutcome.PossiblyReleased =>
                Switch2ProUsbStartupRetirementFailure.PossiblyReleased,
            _ => Switch2ProUsbStartupRetirementFailure.MalformedCompletion,
        };
    }

    private static Switch2ProUsbCalibrationReadResult Failed(
        Switch2ProUsbCalibrationReadFailure failure,
        Switch2ProUsbStartupRetirementFailure retirementFailure = default) =>
        new(failure, retirementFailure, default);

    private static Switch2ProUsbStartupStep MapStep(
        Switch2UsbCalibrationRead read) => read switch
        {
            Switch2UsbCalibrationRead.FactoryPrimary =>
                Switch2ProUsbStartupStep.ReadFactoryPrimaryCalibration,
            Switch2UsbCalibrationRead.FactorySecondary =>
                Switch2ProUsbStartupStep.ReadFactorySecondaryCalibration,
            Switch2UsbCalibrationRead.UserPrimary =>
                Switch2ProUsbStartupStep.ReadUserPrimaryCalibration,
            Switch2UsbCalibrationRead.UserSecondary =>
                Switch2ProUsbStartupStep.ReadUserSecondaryCalibration,
            _ => Switch2ProUsbStartupStep.Invalid,
        };

    private static long StartDeadline(int timeoutMilliseconds) =>
        Stopwatch.GetTimestamp() + (long)Math.Ceiling(
            timeoutMilliseconds * (double)Stopwatch.Frequency / 1_000d);

    private static int RemainingMilliseconds(long deadline)
    {
        long remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0)
        {
            return 0;
        }
        return Math.Max(1, (int)Math.Min(int.MaxValue, Math.Ceiling(
            remaining * 1_000d / Stopwatch.Frequency)));
    }
}
