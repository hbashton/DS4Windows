/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Threading;

namespace DS4Windows.Switch2;

/// <summary>
/// Dormant Windows acquisition boundary for one full-duplex Switch 2 Pro USB
/// composite. It has no production registration site. Acquisition reserves the
/// opaque container, takes one MI_00 read/write handle and one MI_01 WinUSB
/// handle, revalidates the exact device tree while both are retained, and never
/// performs discovery again after the lease escapes.
/// </summary>
internal sealed class Switch2ProUsbWindowsOwnedCompositeAdapter :
    ISwitch2ProUsbOwnedCompositeNativeAdapter
{
    private readonly ISwitch2ProUsbWindowsOwnedCompositePlatform platform;
    private readonly Switch2ProUsbWindowsReservationRegistry reservations;

    internal Switch2ProUsbWindowsOwnedCompositeAdapter()
        : this(new Switch2ProUsbWindowsNativePlatform(),
            Switch2ProUsbWindowsReservationRegistry.ProcessWide)
    {
    }

    internal Switch2ProUsbWindowsOwnedCompositeAdapter(
        ISwitch2ProUsbWindowsOwnedCompositePlatform platform,
        Switch2ProUsbWindowsReservationRegistry reservations)
    {
        this.platform = platform ?? throw new ArgumentNullException(
            nameof(platform));
        this.reservations = reservations ?? throw new ArgumentNullException(
            nameof(reservations));
    }

    public bool TryOpenOwnedComposite(
        in Switch2PhysicalInputRegistration registration,
        in Switch2PhysicalInputLifetime lifetime,
        out ISwitch2ProUsbOwnedCompositeLease lease)
    {
        lease = null;
        if (!registration.IsValid || !lifetime.IsValid ||
            !lifetime.Registration.Equals(registration) ||
            registration.ProtocolIdentity.Model !=
                Switch2ControllerModel.ProController2 ||
            registration.ProtocolIdentity.Transport != Switch2Transport.Usb)
        {
            return false;
        }

        Switch2ProUsbWindowsReservationRegistry.
            Switch2ProUsbWindowsReservation reservation = null;
        ISwitch2ProUsbWindowsOwnedHidHandle hid = null;
        ISwitch2ProUsbWindowsOwnedCommandHandle command = null;
        bool hidReleased = true;
        bool commandReleased = true;
        bool acquisitionAmbiguous = false;
        try
        {
            if (!reservations.TryAcquire(registration.ContainerIdentity,
                    out reservation))
            {
                return false;
            }
            IReadOnlyList<Switch2ProUsbWindowsCandidate> candidates;
            try
            {
                if (!platform.TryDiscoverCandidates(out candidates))
                {
                    return false;
                }
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
            {
                reservation.RetainAcquisitionQuarantine(ex.RetainedOwner);
                acquisitionAmbiguous = true;
                return false;
            }
            catch
            {
                acquisitionAmbiguous = true;
                return false;
            }
            if (
                !TryFindExactCandidate(candidates, registration,
                    expectedIdentity: null, out var candidate))
            {
                return false;
            }

            bool hidOpened;
            try
            {
                hidOpened = platform.TryOpenOwnedHid(candidate, out hid);
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
            {
                reservation.RetainAcquisitionQuarantine(ex.RetainedOwner);
                acquisitionAmbiguous = true;
                return false;
            }
            catch
            {
                acquisitionAmbiguous = true;
                return false;
            }
            if (!hidOpened || hid == null)
            {
                acquisitionAmbiguous = hidOpened && hid == null;
                return false;
            }
            hidReleased = false;
            bool commandOpened;
            try
            {
                commandOpened = platform.TryOpenOwnedCommand(candidate,
                    out command);
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
            {
                reservation.RetainAcquisitionQuarantine(ex.RetainedOwner);
                acquisitionAmbiguous = true;
                return false;
            }
            catch
            {
                acquisitionAmbiguous = true;
                return false;
            }
            if (!commandOpened || command == null)
            {
                acquisitionAmbiguous = commandOpened && command == null;
                return false;
            }
            commandReleased = false;

            // Revalidate the device-tree identity while both exact handles are
            // retained. MI_01 deliberately denies a second command writer, so
            // this boundary must use metadata plus the topology already
            // verified through the retained WinUSB owner rather than opening
            // another WinUSB handle against itself.
            try
            {
                if (!platform.TryRevalidateOwnedCandidate(candidate))
                {
                    return false;
                }
            }
            catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
            {
                reservation.RetainAcquisitionQuarantine(ex.RetainedOwner);
                acquisitionAmbiguous = true;
                return false;
            }
            catch
            {
                acquisitionAmbiguous = true;
                return false;
            }
            lease = new Switch2ProUsbWindowsOwnedCompositeLease(registration,
                lifetime, hid, command, reservation);
            hid = null;
            command = null;
            reservation = null;
            return true;
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException ex)
        {
            reservation?.RetainAcquisitionQuarantine(ex.RetainedOwner);
            acquisitionAmbiguous = true;
            return false;
        }
        catch
        {
            // Every dependency exception after the reservation attempt is
            // treated as acquisition-ambiguous. This includes discovery and
            // revalidation cleanup failures: an unproven metadata handle or
            // WinUSB close must keep this exact container fenced.
            acquisitionAmbiguous = true;
            return false;
        }
        finally
        {
            // No operation can have been submitted before lease escape.
            // Release in reverse acquisition order. A failed release retains
            // the process reservation permanently rather than admitting a
            // second owner beside an outcome-uncertain native handle.
            if (command != null)
            {
                commandReleased = false;
                try
                {
                    command.DisposeQuiesced();
                    commandReleased = true;
                }
                catch
                {
                    reservation?.RetainAcquisitionQuarantine(command);
                }
            }
            if (hid != null)
            {
                hidReleased = false;
                try
                {
                    hid.DisposeQuiesced();
                    hidReleased = true;
                }
                catch
                {
                    reservation?.RetainAcquisitionQuarantine(hid);
                }
            }
            if (hidReleased && commandReleased && !acquisitionAmbiguous)
            {
                try
                {
                    reservation?.ReleaseAfterAbortedOpen();
                }
                catch
                {
                    // The registry invokes its release hook before removing
                    // the container. A hook failure therefore leaves the
                    // reservation fenced, which is the safe partial-open
                    // outcome.
                }
            }
        }
    }

    private static bool TryFindExactCandidate(
        IReadOnlyList<Switch2ProUsbWindowsCandidate> candidates,
        in Switch2PhysicalInputRegistration registration,
        Switch2ProUsbWindowsCandidate expectedIdentity,
        out Switch2ProUsbWindowsCandidate exact)
    {
        exact = null;
        if (candidates == null)
        {
            return false;
        }

        int matches = 0;
        for (int index = 0; index < candidates.Count; index++)
        {
            Switch2ProUsbWindowsCandidate candidate = candidates[index];
            if (candidate == null ||
                !candidate.TryGetAdmittedRegistration(
                    out Switch2PhysicalInputRegistration admitted) ||
                !admitted.Equals(registration))
            {
                continue;
            }
            matches++;
            exact = candidate;
        }

        return matches == 1 && (expectedIdentity == null ||
            exact.SameIdentity(expectedIdentity));
    }
}

internal interface ISwitch2ProUsbWindowsOwnedCompositePlatform
{
    bool TryDiscoverCandidates(
        out IReadOnlyList<Switch2ProUsbWindowsCandidate> candidates);

    bool TryOpenOwnedHid(Switch2ProUsbWindowsCandidate candidate,
        out ISwitch2ProUsbWindowsOwnedHidHandle hid);

    bool TryOpenOwnedCommand(Switch2ProUsbWindowsCandidate candidate,
        out ISwitch2ProUsbWindowsOwnedCommandHandle command);

    bool TryRevalidateOwnedCandidate(
        Switch2ProUsbWindowsCandidate expected);
}

internal interface ISwitch2ProUsbWindowsOwnedHidHandle :
    ISwitch2ProUsbWindowsInputHandle
{
    bool HasObservedDeviceDisconnection => false;

    /// <summary>
    /// A false result with a null operation proves no native submission. A
    /// non-null operation on either result must remain strongly owned and be
    /// explicitly drained; callers treat that contradictory start as
    /// outcome-uncertain.
    /// </summary>
    bool TryBeginOutputWrite(byte[] source, int offset, int count,
        out ISwitch2ProUsbWindowsOwnedIoOperation operation);
}

internal interface ISwitch2ProUsbWindowsOwnedCommandHandle
{
    /// <summary>
    /// On either start method, false with null proves no native submission.
    /// Every other result/operation contradiction is outcome-uncertain and the
    /// composite owner must remain fenced.
    /// </summary>
    bool TryBeginBulkWrite(byte[] source, int offset, int count,
        out ISwitch2ProUsbWindowsOwnedIoOperation operation);

    bool TryBeginBulkRead(byte[] destination, int offset, int count,
        out ISwitch2ProUsbWindowsOwnedIoOperation operation);

    void DisposeQuiesced();
}

internal interface ISwitch2ProUsbWindowsOwnedIoOperation
{
    /// <summary>
    /// Requests cancellation of this exact retained operation without waiting.
    /// For WinUSB, the owned command handle permits only one operation on the
    /// selected pipe, so a pipe abort is exact within that lifetime.
    /// </summary>
    bool TryCancelExact();

    /// <summary>
    /// Waits at most timeoutMilliseconds in managed waiting. True proves the
    /// exact native overlapped storage has been freed and the IOCP callback
    /// crossed its final managed-use publication boundary. The callback frame
    /// may still be unwinding, but it performs no later access to the operation,
    /// buffer, bound handle, or completion state; reuse and disposal are safe.
    /// Native begin/cancel/free calls happen outside this wait and have no hard
    /// wall-clock bound in the Win32 contract.
    /// </summary>
    bool TryWaitForNativeQuiescence(int timeoutMilliseconds);

    bool TryGetCompletion(
        out Switch2ProUsbWindowsOwnedIoCompletion completion);

    void ReleaseSubmissionQuiesced();
}

internal readonly struct Switch2ProUsbWindowsOwnedIoCompletion
{
    internal Switch2ProUsbWindowsOwnedIoCompletion(int bytesTransferred,
        Switch2ProUsbNativeReadStatus status)
    {
        BytesTransferred = bytesTransferred;
        Status = status;
    }

    internal int BytesTransferred { get; }

    internal Switch2ProUsbNativeReadStatus Status { get; }
}

internal static class Switch2ProUsbWindowsDeadline
{
    internal static long Start(int timeoutMilliseconds) =>
        Environment.TickCount64 + timeoutMilliseconds;

    internal static int Remaining(long deadline, int originalTimeout) =>
        RemainingAt(deadline, originalTimeout, Environment.TickCount64);

    internal static int RemainingAt(long deadline, int originalTimeout,
        long currentTick)
    {
        if (originalTimeout <= 0)
        {
            return 0;
        }
        long remaining = deadline - currentTick;
        return remaining <= 0 ? 0 :
            (int)Math.Min(remaining, originalTimeout);
    }
}

/// <summary>
/// One atomic terminal fence shared by every facet of an owned composite.
/// Native start admission, the native begin call, and publication of its exact
/// operation are one serialized region. Latching is terminal: existing exact
/// operations may only drain/retire, and no later input, output, or command
/// native effect can begin. The in-progress bit also closes Monitor's
/// same-thread reentrancy window around dependencies which complete inline.
/// </summary>
internal sealed class Switch2ProUsbWindowsCompositeTerminalFence
{
    internal object Gate { get; } = new();

    private bool latched;
    private bool submissionInProgress;
    private bool terminalReleaseInProgress;
    private bool terminalReleasePublished;

    internal bool IsLatched
    {
        get
        {
            lock (Gate)
            {
                return latched;
            }
        }
    }

    internal bool IsLatchedNoLock
    {
        get
        {
            RequireGate();
            return latched;
        }
    }

    internal bool TryBeginSubmissionNoLock()
    {
        RequireGate();
        if (latched || submissionInProgress || terminalReleaseInProgress ||
            terminalReleasePublished)
        {
            return false;
        }
        submissionInProgress = true;
        return true;
    }

    internal void EndSubmissionNoLock()
    {
        RequireGate();
        submissionInProgress = false;
        Monitor.PulseAll(Gate);
    }

    internal void LatchNoLock()
    {
        RequireGate();
        latched = true;
        Monitor.PulseAll(Gate);
    }

    internal void Latch()
    {
        lock (Gate)
        {
            LatchNoLock();
        }
    }

    /// <summary>
    /// Fences new submissions while a terminal reservation publication runs.
    /// The registry hook deliberately runs after this method releases
    /// <see cref="Gate"/> so a reentrant or concurrent stale callback can latch
    /// the fence before publication is committed.
    /// </summary>
    internal void BeginTerminalRelease()
    {
        lock (Gate)
        {
            if (latched || submissionInProgress || terminalReleaseInProgress ||
                terminalReleasePublished)
            {
                throw new InvalidOperationException(
                    "The composite cannot begin terminal release.");
            }
            terminalReleaseInProgress = true;
        }
    }

    /// <summary>
    /// Linearizes the final registry removal with the shared terminal fence.
    /// The supplied publication must be finite and must not invoke external
    /// code. It runs while <see cref="Gate"/> excludes callback publication and
    /// native-start admission.
    /// </summary>
    internal void PublishTerminalRelease(Action publication)
    {
        if (publication == null)
        {
            throw new ArgumentNullException(nameof(publication));
        }
        lock (Gate)
        {
            if (!terminalReleaseInProgress || terminalReleasePublished ||
                latched || submissionInProgress)
            {
                throw new InvalidOperationException(
                    "Composite quarantine prevents terminal publication.");
            }
            publication();
            terminalReleasePublished = true;
            terminalReleaseInProgress = false;
            Monitor.PulseAll(Gate);
        }
    }

    internal void AbandonTerminalRelease()
    {
        lock (Gate)
        {
            if (!terminalReleasePublished)
            {
                terminalReleaseInProgress = false;
                Monitor.PulseAll(Gate);
            }
        }
    }

    internal void PublishTerminalReleaseWithoutReservation()
    {
        BeginTerminalRelease();
        try
        {
            PublishTerminalRelease(static () => { });
        }
        catch
        {
            AbandonTerminalRelease();
            throw;
        }
    }

    private void RequireGate()
    {
        if (!Monitor.IsEntered(Gate))
        {
            throw new SynchronizationLockException(
                "The composite terminal gate is not held.");
        }
    }
}

/// <summary>
/// Retained HID output lane. A deadline can return while the native operation
/// is still pending, but the exact operation and buffer remain owned, no
/// replacement write is admitted, and only an exact-claim retirement can make
/// the lane quiescent again.
/// </summary>
internal sealed class Switch2ProUsbWindowsOwnedOutputLane
{
    private const Switch2ControllerModel Model =
        Switch2ControllerModel.ProController2;

    private readonly object gate = new();
    private readonly object leaseFence = new();
    private readonly Switch2ProUsbWindowsCompositeTerminalFence terminalFence;
    private readonly ISwitch2ProUsbWindowsOwnedHidHandle hid;
    private readonly ulong deviceGeneration;
    private readonly ulong transportGeneration;
    private readonly int maximumOperationMilliseconds;
    private readonly byte[] outputBuffer =
        new byte[Switch2UsbHdRumbleCodec.ReportLength];

    private ISwitch2ProUsbWindowsOwnedIoOperation activeOperation;
    private Switch2ProUsbOwnedOutputOperationClaim activeClaim;
    private ulong sequence;
    private bool startInProgress;
    private bool retirementInProgress;
    private bool cancellationIssued;
    private bool quarantined;
    private bool sealedForDisposal;
    private bool disconnectedOutputSealed;
    private object feedbackOutputAdoptionFence;
    private bool feedbackOutputEverAdopted;

    internal Switch2ProUsbWindowsOwnedOutputLane(
        ISwitch2ProUsbWindowsOwnedHidHandle hid,
        ulong deviceGeneration, ulong transportGeneration,
        int maximumOperationMilliseconds)
        : this(hid, deviceGeneration, transportGeneration,
            maximumOperationMilliseconds,
            new Switch2ProUsbWindowsCompositeTerminalFence())
    {
    }

    internal Switch2ProUsbWindowsOwnedOutputLane(
        ISwitch2ProUsbWindowsOwnedHidHandle hid,
        ulong deviceGeneration, ulong transportGeneration,
        int maximumOperationMilliseconds,
        Switch2ProUsbWindowsCompositeTerminalFence terminalFence)
    {
        this.hid = hid ?? throw new ArgumentNullException(nameof(hid));
        if (deviceGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceGeneration));
        }
        if (transportGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transportGeneration));
        }
        if (maximumOperationMilliseconds <= 0 ||
            maximumOperationMilliseconds >
                Switch2ProUsbInputTransportOwner.
                    MaximumDisposeTimeoutMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOperationMilliseconds));
        }

        this.deviceGeneration = deviceGeneration;
        this.transportGeneration = transportGeneration;
        this.maximumOperationMilliseconds = maximumOperationMilliseconds;
        this.terminalFence = terminalFence ?? throw new ArgumentNullException(
            nameof(terminalFence));
    }

    internal bool IsExactlyQuiescent
    {
        get
        {
            if (terminalFence.IsLatched)
            {
                return false;
            }
            lock (gate)
            {
                return !startInProgress && !retirementInProgress &&
                    activeOperation == null && !activeClaim.IsValid &&
                    !quarantined;
            }
        }
    }

    internal bool IsQuarantined
    {
        get
        {
            if (terminalFence.IsLatched)
            {
                return true;
            }
            lock (gate)
            {
                return quarantined;
            }
        }
    }

    /// <summary>
    /// Pure exact-provenance check used by the bounded canonical bridge. A
    /// numeric-generation match is insufficient: the private lane fence,
    /// current sequence, exact active operation, and stably retained state
    /// must all still agree.
    /// </summary>
    internal bool AuthenticatesOutputOperationClaim(
        in Switch2ProUsbOwnedOutputOperationClaim claim) =>
        AuthenticatesOutputOperationClaim(ownerFence: null, claim);

    internal bool AuthenticatesAdoptedOutputOperationClaim(object ownerFence,
        in Switch2ProUsbOwnedOutputOperationClaim claim) =>
        AuthenticatesOutputOperationClaim(ownerFence, claim);

    private bool AuthenticatesOutputOperationClaim(object ownerFence,
        in Switch2ProUsbOwnedOutputOperationClaim claim)
    {
        lock (gate)
        {
            // Quarantine or an in-progress exact retirement does not erase
            // provenance. False means only that this claim no longer names
            // the lane's exact retained native operation.
            return AuthenticatesOutputOwnerNoLock(ownerFence) &&
                activeOperation != null && activeClaim.Equals(claim) &&
                claim.Authenticates(leaseFence, deviceGeneration,
                    transportGeneration, activeClaim.Sequence);
        }
    }

    internal bool TryAdoptDormantFeedbackOutput(object ownerFence)
    {
        if (ownerFence == null)
        {
            return false;
        }

        lock (terminalFence.Gate)
        {
            if (terminalFence.IsLatchedNoLock)
            {
                return false;
            }
            lock (gate)
            {
                if (feedbackOutputEverAdopted || sealedForDisposal ||
                    quarantined || startInProgress || retirementInProgress ||
                    activeOperation != null || activeClaim.IsValid ||
                    sequence != 0)
                {
                    return false;
                }

                feedbackOutputAdoptionFence = ownerFence;
                feedbackOutputEverAdopted = true;
                return true;
            }
        }
    }

    internal bool AuthenticatesAdoptedOutput(object ownerFence,
        Switch2ControllerModel model, ulong candidateDeviceGeneration,
        ulong candidateTransportGeneration)
    {
        lock (gate)
        {
            return model == Model &&
                candidateDeviceGeneration == deviceGeneration &&
                candidateTransportGeneration == transportGeneration &&
                AuthenticatesOutputOwnerNoLock(ownerFence) &&
                !sealedForDisposal;
        }
    }

    internal bool TrySealDisconnectedOutput(object ownerFence)
    {
        lock (terminalFence.Gate)
        {
            lock (gate)
            {
                if (!AuthenticatesOutputOwnerNoLock(ownerFence) ||
                    terminalFence.IsLatchedNoLock || quarantined ||
                    sealedForDisposal || startInProgress || retirementInProgress ||
                    activeOperation != null || activeClaim.IsValid ||
                    !hid.HasObservedDeviceDisconnection)
                {
                    return false;
                }
                disconnectedOutputSealed = true;
                return true;
            }
        }
    }

    internal Switch2ProUsbOwnedOutputWriteAttempt TryWrite(
        ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration,
        int timeoutMilliseconds) => TryWrite(ownerFence: null, report,
        expectedModel, expectedDeviceGeneration, expectedTransportGeneration,
        timeoutMilliseconds);

    internal Switch2ProUsbOwnedOutputWriteAttempt TryWriteAdopted(
        object ownerFence, ReadOnlySpan<byte> report,
        Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration,
        int timeoutMilliseconds) => TryWrite(ownerFence, report,
        expectedModel, expectedDeviceGeneration, expectedTransportGeneration,
        timeoutMilliseconds);

    private Switch2ProUsbOwnedOutputWriteAttempt TryWrite(object ownerFence,
        ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration,
        int timeoutMilliseconds)
    {
        if (expectedModel != Model ||
            expectedDeviceGeneration != deviceGeneration ||
            expectedTransportGeneration != transportGeneration)
        {
            return TerminalReject(
                Switch2ProUsbHdRumbleTransportWriteFailure.StaleLifetime);
        }
        if (timeoutMilliseconds <= 0 ||
            timeoutMilliseconds > maximumOperationMilliseconds ||
            !Switch2UsbHdRumbleCodec.TryDecodeProController(report,
                out _, out _, out _, out _))
        {
            return TerminalReject(
                Switch2ProUsbHdRumbleTransportWriteFailure.InvalidReport);
        }
        long deadline = Switch2ProUsbWindowsDeadline.Start(
            timeoutMilliseconds);

        Switch2ProUsbOwnedOutputOperationClaim claim = default;
        ISwitch2ProUsbWindowsOwnedIoOperation operation = null;
        bool started = false;
        bool dependencyThrew = false;
        lock (terminalFence.Gate)
        {
            if (!terminalFence.TryBeginSubmissionNoLock())
            {
                lock (gate)
                {
                    if (terminalFence.IsLatchedNoLock)
                    {
                        quarantined = true;
                        return QuarantinedRejectNoLock(
                            Switch2ProUsbHdRumbleTransportWriteFailure.
                                TransportEnded);
                    }
                    return TerminalRejectNoLock(
                        Switch2ProUsbHdRumbleTransportWriteFailure.Busy);
                }
            }
            try
            {
                lock (gate)
                {
                    if (!AuthenticatesOutputOwnerNoLock(ownerFence))
                    {
                        return TerminalRejectNoLock(
                            Switch2ProUsbHdRumbleTransportWriteFailure.
                                TransportEnded);
                    }
                    if (quarantined)
                    {
                        terminalFence.LatchNoLock();
                        return QuarantinedRejectNoLock(
                            Switch2ProUsbHdRumbleTransportWriteFailure.
                                TransportEnded);
                    }
                    if (sealedForDisposal || disconnectedOutputSealed)
                    {
                        return TerminalRejectNoLock(
                            Switch2ProUsbHdRumbleTransportWriteFailure.
                                TransportEnded);
                    }
                    if (startInProgress || retirementInProgress ||
                        activeOperation != null || activeClaim.IsValid)
                    {
                        return TerminalRejectNoLock(
                            Switch2ProUsbHdRumbleTransportWriteFailure.Busy);
                    }
                    if (sequence == ulong.MaxValue)
                    {
                        sealedForDisposal = true;
                        return TerminalRejectNoLock(
                            Switch2ProUsbHdRumbleTransportWriteFailure.
                                TransportEnded);
                    }

                    report.CopyTo(outputBuffer);
                    claim = new Switch2ProUsbOwnedOutputOperationClaim(
                        leaseFence, deviceGeneration, transportGeneration,
                        ++sequence);
                    activeClaim = claim;
                    startInProgress = true;
                    cancellationIssued = false;
                }

                try
                {
                    started = hid.TryBeginOutputWrite(outputBuffer, 0,
                        outputBuffer.Length, out operation);
                }
                catch
                {
                    started = false;
                    dependencyThrew = true;
                }

                lock (gate)
                {
                    startInProgress = false;
                    if (operation != null)
                    {
                        activeOperation = operation;
                    }
                    if (!started && operation == null && !dependencyThrew)
                    {
                        activeClaim = default;
                        Monitor.PulseAll(gate);
                        return TerminalRejectNoLock(
                            Switch2ProUsbHdRumbleTransportWriteFailure.
                                TransportRejected);
                    }
                    if (!started || operation == null || dependencyThrew)
                    {
                        quarantined = true;
                        terminalFence.LatchNoLock();
                        if (operation == null)
                        {
                            // Quarantine is the terminal fact. Do not retain a
                            // claim that falsely identifies a native submission.
                            activeClaim = default;
                        }
                        Monitor.PulseAll(gate);
                        return QuarantinedUncertainNoLock(
                            operation == null ? default : claim,
                            Switch2ProUsbHdRumbleTransportWriteFailure.
                                DependencyThrew);
                    }
                    Monitor.PulseAll(gate);
                }
            }
            finally
            {
                terminalFence.EndSubmissionNoLock();
            }
        }

        bool quiesced;
        try
        {
            quiesced = operation.TryWaitForNativeQuiescence(
                Switch2ProUsbWindowsDeadline.Remaining(deadline,
                    timeoutMilliseconds));
        }
        catch
        {
            return LatchQuarantinedUncertain(claim,
                Switch2ProUsbHdRumbleTransportWriteFailure.DependencyThrew);
        }

        if (!quiesced)
        {
            // Do not issue cancellation after the write deadline has expired:
            // cancellation is a separate bounded exact-claim operation.
            return RetainedUncertain(claim,
                Switch2ProUsbHdRumbleTransportWriteFailure.
                    TransportRejected);
        }

        Switch2ProUsbWindowsOwnedIoCompletion completion;
        try
        {
            if (!operation.TryGetCompletion(out completion))
            {
                return LatchQuarantinedUncertain(claim,
                    Switch2ProUsbHdRumbleTransportWriteFailure.
                        DependencyThrew);
            }
            operation.ReleaseSubmissionQuiesced();
        }
        catch
        {
            return LatchQuarantinedUncertain(claim,
                Switch2ProUsbHdRumbleTransportWriteFailure.DependencyThrew);
        }

        bool stateMismatch;
        lock (gate)
        {
            stateMismatch = !ReferenceEquals(activeOperation, operation) ||
                !activeClaim.Equals(claim);
            if (!stateMismatch)
            {
                activeOperation = null;
                activeClaim = default;
                cancellationIssued = false;
            }
            Monitor.PulseAll(gate);
        }
        if (stateMismatch)
        {
            return LatchQuarantinedUncertain(claim,
                Switch2ProUsbHdRumbleTransportWriteFailure.DependencyThrew);
        }

        lock (terminalFence.Gate)
        {
            if (terminalFence.IsLatchedNoLock)
            {
                lock (gate)
                {
                    quarantined = true;
                    return QuarantinedRejectNoLock(
                        Switch2ProUsbHdRumbleTransportWriteFailure.
                            TransportEnded);
                }
            }
            if (completion.Status == Switch2ProUsbNativeReadStatus.Completed &&
                completion.BytesTransferred == outputBuffer.Length)
            {
                return new Switch2ProUsbOwnedOutputWriteAttempt(
                    Switch2ProUsbHdRumbleTransportWriteResult.Complete(Model,
                        deviceGeneration, transportGeneration,
                        completion.BytesTransferred), default);
            }
        }

        // Native quiescence is exact, but a queued failure does not prove how
        // much output the device consumed. No retirement claim is necessary
        // because this operation itself is already quiescent.
        return new Switch2ProUsbOwnedOutputWriteAttempt(
            Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(Model,
                deviceGeneration, transportGeneration,
                Switch2ProUsbHdRumbleTransportWriteFailure.
                    TransportRejected,
                Math.Clamp(completion.BytesTransferred, 0,
                    outputBuffer.Length)), default);
    }

    internal Switch2ProUsbOwnedOutputRetirementResult TryRetire(
        in Switch2ProUsbOwnedOutputOperationClaim claim,
        int timeoutMilliseconds) => TryRetire(ownerFence: null, claim,
        timeoutMilliseconds);

    internal Switch2ProUsbOwnedOutputRetirementResult TryRetireAdopted(
        object ownerFence,
        in Switch2ProUsbOwnedOutputOperationClaim claim,
        int timeoutMilliseconds) => TryRetire(ownerFence, claim,
        timeoutMilliseconds);

    private Switch2ProUsbOwnedOutputRetirementResult TryRetire(
        object ownerFence,
        in Switch2ProUsbOwnedOutputOperationClaim claim,
        int timeoutMilliseconds)
    {
        ISwitch2ProUsbWindowsOwnedIoOperation operation;
        bool issueCancellation;
        long deadline;
        lock (gate)
        {
            if (!AuthenticatesOutputOwnerNoLock(ownerFence) ||
                !claim.Authenticates(leaseFence, deviceGeneration,
                    transportGeneration, activeClaim.Sequence) ||
                !activeClaim.Equals(claim) || activeOperation == null)
            {
                return Switch2ProUsbOwnedOutputRetirementResult.Reject(claim);
            }
            if (quarantined)
            {
                return Switch2ProUsbOwnedOutputRetirementResult.
                    Quarantine(claim);
            }
            if (startInProgress || retirementInProgress)
            {
                return Switch2ProUsbOwnedOutputRetirementResult.Reject(claim);
            }
            if (timeoutMilliseconds < 0 ||
                timeoutMilliseconds > maximumOperationMilliseconds)
            {
                // The exact operation is stably retained and no dependency was
                // invoked, so an invalid bound cannot honestly quarantine it.
                return Switch2ProUsbOwnedOutputRetirementResult.
                    Retained(claim);
            }
            retirementInProgress = true;
            operation = activeOperation;
            issueCancellation = !cancellationIssued;
            deadline = Switch2ProUsbWindowsDeadline.Start(
                timeoutMilliseconds);
        }

        if (issueCancellation)
        {
            bool cancellationAccepted;
            try
            {
                // False can mean that completion won the race. The bounded
                // quiescence wait below, not this advisory result, is the proof.
                cancellationAccepted = operation.TryCancelExact();
            }
            catch
            {
                return QuarantineRetirement(claim);
            }
            if (cancellationAccepted)
            {
                lock (gate)
                {
                    if (ReferenceEquals(activeOperation, operation) &&
                        activeClaim.Equals(claim))
                    {
                        cancellationIssued = true;
                    }
                }
            }
        }

        bool quiesced;
        try
        {
            quiesced = operation.TryWaitForNativeQuiescence(
                Switch2ProUsbWindowsDeadline.Remaining(deadline,
                    timeoutMilliseconds));
        }
        catch
        {
            return QuarantineRetirement(claim);
        }
        if (!quiesced)
        {
            lock (gate)
            {
                retirementInProgress = false;
                Monitor.PulseAll(gate);
            }
            return Switch2ProUsbOwnedOutputRetirementResult.Retained(claim);
        }

        try
        {
            if (!operation.TryGetCompletion(out _))
            {
                return QuarantineRetirement(claim);
            }
            operation.ReleaseSubmissionQuiesced();
        }
        catch
        {
            return QuarantineRetirement(claim);
        }

        bool stateMismatch;
        lock (gate)
        {
            stateMismatch = !ReferenceEquals(activeOperation, operation) ||
                !activeClaim.Equals(claim);
            if (!stateMismatch)
            {
                activeOperation = null;
                activeClaim = default;
                cancellationIssued = false;
            }
            retirementInProgress = false;
            Monitor.PulseAll(gate);
        }
        if (stateMismatch)
        {
            return QuarantineRetirement(claim);
        }
        return Switch2ProUsbOwnedOutputRetirementResult.Quiescent(claim);
    }

    internal bool TrySealForDisposal()
    {
        lock (terminalFence.Gate)
        {
            if (terminalFence.IsLatchedNoLock)
            {
                return false;
            }
            lock (gate)
            {
                if (quarantined || startInProgress || retirementInProgress ||
                    activeOperation != null || activeClaim.IsValid)
                {
                    return false;
                }
                sealedForDisposal = true;
                // Invalidation is terminal and does not make the one-shot
                // adoption available again: feedbackOutputEverAdopted remains
                // latched, preventing an ABA capability from being minted.
                feedbackOutputAdoptionFence = null;
                return true;
            }
        }
    }

    internal Switch2ProUsbOwnedOutputWriteAttempt QuarantineFromPeerFacet()
    {
        LatchQuarantine();
        lock (gate)
        {
            return QuarantinedRejectNoLock(
                Switch2ProUsbHdRumbleTransportWriteFailure.TransportEnded);
        }
    }

    private Switch2ProUsbOwnedOutputRetirementResult QuarantineRetirement(
        in Switch2ProUsbOwnedOutputOperationClaim claim)
    {
        LatchQuarantine();
        lock (gate)
        {
            retirementInProgress = false;
            Monitor.PulseAll(gate);
        }
        return Switch2ProUsbOwnedOutputRetirementResult.Quarantine(claim);
    }

    private void LatchQuarantine()
    {
        lock (terminalFence.Gate)
        {
            terminalFence.LatchNoLock();
            lock (gate)
            {
                quarantined = true;
                Monitor.PulseAll(gate);
            }
        }
    }

    private bool AuthenticatesOutputOwnerNoLock(object ownerFence) =>
        feedbackOutputEverAdopted ?
            feedbackOutputAdoptionFence != null &&
                ReferenceEquals(feedbackOutputAdoptionFence, ownerFence) :
            ownerFence == null;

    private Switch2ProUsbOwnedOutputWriteAttempt LatchQuarantinedUncertain(
        in Switch2ProUsbOwnedOutputOperationClaim claim,
        Switch2ProUsbHdRumbleTransportWriteFailure failure)
    {
        LatchQuarantine();
        return QuarantinedUncertain(claim, failure);
    }

    private Switch2ProUsbOwnedOutputWriteAttempt TerminalReject(
        Switch2ProUsbHdRumbleTransportWriteFailure failure)
    {
        lock (gate)
        {
            if (quarantined)
            {
                return QuarantinedRejectNoLock(
                    Switch2ProUsbHdRumbleTransportWriteFailure.
                        TransportEnded);
            }
            return TerminalRejectNoLock(failure);
        }
    }

    private Switch2ProUsbOwnedOutputWriteAttempt TerminalRejectNoLock(
        Switch2ProUsbHdRumbleTransportWriteFailure failure) => new(
        Switch2ProUsbHdRumbleTransportWriteResult.Reject(Model,
            deviceGeneration, transportGeneration, failure), default);

    private Switch2ProUsbOwnedOutputWriteAttempt QuarantinedRejectNoLock(
        Switch2ProUsbHdRumbleTransportWriteFailure failure) =>
        Switch2ProUsbOwnedOutputWriteAttempt.Quarantine(
            Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(Model,
                deviceGeneration, transportGeneration, failure));

    private Switch2ProUsbOwnedOutputWriteAttempt RetainedUncertain(
        in Switch2ProUsbOwnedOutputOperationClaim claim,
        Switch2ProUsbHdRumbleTransportWriteFailure failure)
    {
        lock (gate)
        {
            return RetainedUncertainNoLock(claim, failure);
        }
    }

    private Switch2ProUsbOwnedOutputWriteAttempt RetainedUncertainNoLock(
        in Switch2ProUsbOwnedOutputOperationClaim claim,
        Switch2ProUsbHdRumbleTransportWriteFailure failure) => new(
        Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(Model,
            deviceGeneration, transportGeneration, failure), claim);

    private Switch2ProUsbOwnedOutputWriteAttempt QuarantinedUncertain(
        in Switch2ProUsbOwnedOutputOperationClaim claim,
        Switch2ProUsbHdRumbleTransportWriteFailure failure)
    {
        lock (gate)
        {
            return QuarantinedUncertainNoLock(claim, failure);
        }
    }

    private Switch2ProUsbOwnedOutputWriteAttempt QuarantinedUncertainNoLock(
        in Switch2ProUsbOwnedOutputOperationClaim claim,
        Switch2ProUsbHdRumbleTransportWriteFailure failure) =>
        Switch2ProUsbOwnedOutputWriteAttempt.Quarantine(
            Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(Model,
                deviceGeneration, transportGeneration, failure), claim);
}

/// <summary>
/// Exact one-shot capability minted only after the concrete output lane proves
/// that no native output attempt has ever crossed admission. It deliberately
/// exposes no input, command, whole-lease disposal, or raw composite alias.
/// </summary>
internal sealed class Switch2ProUsbWindowsAdoptedFeedbackOutputLease :
    ISwitch2ProUsbOwnedFeedbackOutputLease
{
    private readonly Switch2ProUsbWindowsOwnedOutputLane lane;
    private readonly object ownerFence;
    private readonly ulong deviceGeneration;
    private readonly ulong transportGeneration;

    internal Switch2ProUsbWindowsAdoptedFeedbackOutputLease(
        Switch2ProUsbWindowsOwnedOutputLane lane, object ownerFence,
        ulong deviceGeneration, ulong transportGeneration,
        int maximumOutputOperationMilliseconds)
    {
        this.lane = lane ?? throw new ArgumentNullException(nameof(lane));
        this.ownerFence = ownerFence ?? throw new ArgumentNullException(
            nameof(ownerFence));
        if (deviceGeneration == 0 || transportGeneration == 0 ||
            maximumOutputOperationMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOutputOperationMilliseconds));
        }

        this.deviceGeneration = deviceGeneration;
        this.transportGeneration = transportGeneration;
        MaximumOutputOperationMilliseconds =
            maximumOutputOperationMilliseconds;
    }

    public int MaximumOutputOperationMilliseconds { get; }

    public bool TrySealDisconnectedOutput() =>
        lane.TrySealDisconnectedOutput(ownerFence);

    public bool AuthenticatesComposite(Switch2ControllerModel model,
        ulong candidateDeviceGeneration,
        ulong candidateTransportGeneration) =>
        lane.AuthenticatesAdoptedOutput(ownerFence, model,
            candidateDeviceGeneration, candidateTransportGeneration);

    public bool AuthenticatesOutputOperationClaim(
        in Switch2ProUsbOwnedOutputOperationClaim claim) =>
        lane.AuthenticatesAdoptedOutputOperationClaim(ownerFence, claim);

    public Switch2ProUsbOwnedOutputWriteAttempt TryWriteReportBounded(
        ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration,
        int timeoutMilliseconds) => lane.TryWriteAdopted(ownerFence, report,
        expectedModel, expectedDeviceGeneration, expectedTransportGeneration,
        timeoutMilliseconds);

    public Switch2ProUsbOwnedOutputRetirementResult TryRetireOutputOperation(
        in Switch2ProUsbOwnedOutputOperationClaim claim,
        int timeoutMilliseconds) => lane.TryRetireAdopted(ownerFence, claim,
        timeoutMilliseconds);
}

/// <summary>
/// One concrete owned composite. Input delegates to the already-reviewed
/// read-lease state machine using the same MI_00 object that owns output. The
/// MI_01 command lane is retired independently. Whole disposal is rejected
/// unless output, command, and input have each crossed exact quiescence.
/// </summary>
internal sealed class Switch2ProUsbWindowsOwnedCompositeLease :
    ISwitch2ProUsbOwnedCompositeLease,
    ISwitch2ProUsbCalibrationCommandLease
{
    internal const int DefaultMaximumOperationMilliseconds = 1_000;

    private readonly object commandGate = new();
    private readonly object disposalGate = new();
    private readonly Switch2ProUsbWindowsCompositeTerminalFence terminalFence;
    private readonly ISwitch2ProUsbWindowsOwnedCommandHandle command;
    private readonly Switch2ProUsbWindowsReadOnlyCompositeLease inputLease;
    private readonly Switch2ProUsbWindowsOwnedOutputLane outputLane;
    private readonly Switch2ProUsbWindowsReservationRegistry.
        Switch2ProUsbWindowsReservation reservation;
    private readonly byte[] commandRequest = new byte[
        Switch2UsbCommandCodec.CalibrationReadRequestLength];
    private readonly byte[] commandResponse = new byte[
        Switch2PhysicalInputRegistration.ProUsbReportByteLength];

    private ISwitch2ProUsbWindowsOwnedIoOperation activeCommandOperation;
    private Switch2ProUsbStartupCommandClaim activeCommandClaim;
    private bool commandOperationIsRead;
    private bool commandCancellationIssued;
    private bool commandCallInProgress;
    private bool commandRetirementInProgress;
    private bool commandRetirementRequired;
    private Switch2ProUsbStartupRetirementClaim retirementClaim;
    private bool commandRetired;
    private bool commandQuarantined;
    private bool inputDisposed;
    private bool reservationReleased;
    private bool disposalInProgress;
    private string lastCommandRetirementDiagnostic = "never-entered";

    internal Switch2ProUsbWindowsOwnedCompositeLease(
        in Switch2PhysicalInputRegistration registration,
        in Switch2PhysicalInputLifetime lifetime,
        ISwitch2ProUsbWindowsOwnedHidHandle hid,
        ISwitch2ProUsbWindowsOwnedCommandHandle command,
        Switch2ProUsbWindowsReservationRegistry.
            Switch2ProUsbWindowsReservation reservation,
        int maximumOperationMilliseconds =
            DefaultMaximumOperationMilliseconds)
    {
        if (!registration.IsValid || !lifetime.IsValid ||
            !lifetime.Registration.Equals(registration))
        {
            throw new ArgumentException("Invalid owned lifetime.",
                nameof(lifetime));
        }
        if (hid == null)
        {
            throw new ArgumentNullException(nameof(hid));
        }
        if (command == null)
        {
            throw new ArgumentNullException(nameof(command));
        }
        if (maximumOperationMilliseconds <= 0 ||
            maximumOperationMilliseconds >
                Switch2ProUsbInputTransportOwner.
                    MaximumDisposeTimeoutMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOperationMilliseconds));
        }

        Registration = registration;
        Lifetime = lifetime;
        this.command = command;
        this.reservation = reservation;
        terminalFence = new Switch2ProUsbWindowsCompositeTerminalFence();
        MaximumOutputOperationMilliseconds = maximumOperationMilliseconds;
        inputLease = new Switch2ProUsbWindowsReadOnlyCompositeLease(
            registration, hid, Switch2ProUsbWindowsNoOpPresenceHandle.Instance,
            reservation: null, terminalFence: terminalFence);
        outputLane = new Switch2ProUsbWindowsOwnedOutputLane(hid,
            lifetime.SessionDescriptor.DeviceGeneration,
            lifetime.SessionDescriptor.TransportGeneration,
            maximumOperationMilliseconds, terminalFence);
        reservation?.AdoptTerminalLifetime(this);
    }

    public Switch2PhysicalInputRegistration Registration { get; }

    public Switch2PhysicalInputLifetime Lifetime { get; }

    public int MaximumOutputOperationMilliseconds { get; }

    public bool AuthenticatesComposite(Switch2ControllerModel model,
        ulong deviceGeneration, ulong transportGeneration) =>
        model == Switch2ControllerModel.ProController2 &&
        deviceGeneration == Lifetime.SessionDescriptor.DeviceGeneration &&
        transportGeneration ==
            Lifetime.SessionDescriptor.TransportGeneration;

    public bool AuthenticatesOutputOperationClaim(
        in Switch2ProUsbOwnedOutputOperationClaim claim) =>
        outputLane.AuthenticatesOutputOperationClaim(claim);

    public bool TryBeginInputRead(byte[] destination, int offset, int count,
        in Switch2ProUsbReadClaim claim,
        ISwitch2ProUsbReadCompletionTarget completionTarget) =>
        inputLease.TryBeginInputRead(destination, offset, count, claim,
            completionTarget);

    public bool TryCancelInputRead(in Switch2ProUsbReadClaim claim) =>
        inputLease.TryCancelInputRead(claim);

    public bool TryRetireCompletedInputRead(
        in Switch2ProUsbReadClaim claim, int timeoutMilliseconds) =>
        inputLease.TryRetireCompletedInputRead(claim, timeoutMilliseconds);

    public bool TryWaitForInputQuiescence(int timeoutMilliseconds) =>
        inputLease.TryWaitForInputQuiescence(timeoutMilliseconds);

    public Switch2ProUsbOwnedOutputWriteAttempt TryWriteReportBounded(
        ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration,
        int timeoutMilliseconds)
    {
        return outputLane.TryWrite(report, expectedModel,
            expectedDeviceGeneration, expectedTransportGeneration,
            timeoutMilliseconds);
    }

    public Switch2ProUsbOwnedOutputRetirementResult
        TryRetireOutputOperation(
            in Switch2ProUsbOwnedOutputOperationClaim claim,
            int timeoutMilliseconds) => outputLane.TryRetire(claim,
        timeoutMilliseconds);

    public bool TryAdoptDormantFeedbackOutput(object ownerFence,
        out ISwitch2ProUsbOwnedFeedbackOutputLease outputLease)
    {
        outputLease = null;
        if (ownerFence == null)
        {
            return false;
        }

        // Allocate every managed object before crossing the one-shot adoption
        // point. After TryAdoptDormantFeedbackOutput succeeds this exact
        // capability must escape; no post-adoption constructor may throw.
        var candidate =
            new Switch2ProUsbWindowsAdoptedFeedbackOutputLease(outputLane,
                ownerFence, Lifetime.SessionDescriptor.DeviceGeneration,
                Lifetime.SessionDescriptor.TransportGeneration,
                MaximumOutputOperationMilliseconds);
        if (!outputLane.TryAdoptDormantFeedbackOutput(ownerFence))
        {
            return false;
        }

        outputLease = candidate;
        return true;
    }

    public Switch2ProUsbStartupCommandCompletion Execute(
        in Switch2ProUsbStartupCommandClaim claim,
        ReadOnlySpan<byte> exactRequest, int timeoutMilliseconds)
    {
        Switch2PlayerLedCommand playerLedCommand = default;
        if (!claim.IsValid || timeoutMilliseconds <= 0 ||
            timeoutMilliseconds > MaximumOutputOperationMilliseconds ||
            !RequestMatchesClaim(claim.Step, exactRequest,
                out playerLedCommand))
        {
            return Switch2ProUsbStartupCommandCompletion.ProvenNotConsumed(
                claim, claim.Step);
        }

        lock (terminalFence.Gate)
        {
            if (terminalFence.IsLatchedNoLock)
            {
                lock (commandGate)
                {
                    commandQuarantined = true;
                    commandRetirementRequired = true;
                    return Switch2ProUsbStartupCommandCompletion.
                        PossiblyConsumed(claim, claim.Step);
                }
            }
        }

        lock (commandGate)
        {
            if (!claim.AuthenticatesLease(this, Lifetime))
            {
                return Switch2ProUsbStartupCommandCompletion.
                    ProvenNotConsumed(claim, claim.Step);
            }
            if (commandRetired || commandQuarantined || commandCallInProgress ||
                commandRetirementInProgress || commandRetirementRequired ||
                activeCommandOperation != null)
            {
                return Switch2ProUsbStartupCommandCompletion.PossiblyConsumed(
                    claim, claim.Step);
            }

            exactRequest.CopyTo(commandRequest);
            commandCallInProgress = true;
            activeCommandClaim = claim;
            commandOperationIsRead = false;
            commandCancellationIssued = false;
        }

        long deadline = Switch2ProUsbWindowsDeadline.Start(
            timeoutMilliseconds);
        try
        {
            if (!TryBeginCommandWrite(claim, exactRequest.Length))
            {
                return FinishCommandCall(claim, provenNotConsumed: true);
            }
            if (!TryCompleteCommandPhase(deadline, timeoutMilliseconds,
                    exactRequest.Length, out bool writeCompleted))
            {
                return RetainTimedOutCommand(claim);
            }
            if (!writeCompleted)
            {
                return FinishCommandCall(claim, provenNotConsumed: false);
            }

            lock (commandGate)
            {
                commandOperationIsRead = true;
            }
            if (!TryBeginCommandRead(claim))
            {
                return FinishCommandCall(claim, provenNotConsumed: false);
            }
            int remaining = Switch2ProUsbWindowsDeadline.Remaining(deadline,
                timeoutMilliseconds);
            if (!TryCompleteCommandPhase(deadline, remaining,
                    expectedBytes: -1, out bool readCompleted))
            {
                return RetainTimedOutCommand(claim);
            }
            if (!readCompleted)
            {
                return FinishCommandCall(claim, provenNotConsumed: false);
            }

            int responseLength = GetStartupResponseLength(claim.Step);
            Switch2ProUsbStartupResponseProofKind proof =
                Switch2ProUsbStartupResponseProofKind.Invalid;
            byte[] responsePayload = null;
            bool validated = responseLength > 0 &&
                TryValidateStartupResponse(claim.Step,
                    commandResponse.AsSpan(0, responseLength),
                    playerLedCommand, out proof, out responsePayload);
            if (!validated)
            {
                return FinishCommandCall(claim,
                    provenNotConsumed: false);
            }
            if (!TryClearCompletedCommandCall())
            {
                return Switch2ProUsbStartupCommandCompletion.PossiblyConsumed(
                    claim, claim.Step);
            }
            return responsePayload == null ?
                Switch2ProUsbStartupCommandCompletion.ExactResponse(claim,
                    claim.Step, proof) :
                Switch2ProUsbStartupCommandCompletion.ExactResponse(claim,
                    claim.Step, proof, responsePayload);
        }
        catch
        {
            LatchCommandQuarantine(clearCallInProgress: true);
            return Switch2ProUsbStartupCommandCompletion.PossiblyConsumed(
                claim, claim.Step);
        }
    }

    public Switch2ProUsbStartupRetirementCompletion Retire(
        in Switch2ProUsbStartupRetirementClaim claim,
        int timeoutMilliseconds)
    {
        lastCommandRetirementDiagnostic = "entered";
        if (!claim.AuthenticatesLease(this, Lifetime) ||
            timeoutMilliseconds < 0 ||
            timeoutMilliseconds > MaximumOutputOperationMilliseconds)
        {
            lastCommandRetirementDiagnostic = "invalid-claim-or-timeout";
            return Switch2ProUsbStartupRetirementCompletion.
                ProvenNotReleased(claim, claim.Reason);
        }

        ISwitch2ProUsbWindowsOwnedIoOperation operation;
        bool issueCancellation;
        long deadline;
        lock (terminalFence.Gate)
        {
            lock (commandGate)
            {
                if (commandRetired)
                {
                    lastCommandRetirementDiagnostic = "already-retired";
                    return Switch2ProUsbStartupRetirementCompletion.Released(
                        claim, claim.Reason);
                }
                if (commandQuarantined || commandRetirementInProgress)
                {
                    lastCommandRetirementDiagnostic =
                        "quarantined-or-retirement-in-progress";
                    return Switch2ProUsbStartupRetirementCompletion.
                        PossiblyReleased(claim, claim.Reason);
                }
                if (commandCallInProgress)
                {
                    lastCommandRetirementDiagnostic =
                        "command-call-in-progress";
                    return Switch2ProUsbStartupRetirementCompletion.
                        ProvenNotReleased(claim, claim.Reason);
                }
                if (retirementClaim.IsValid &&
                    !retirementClaim.Equals(claim))
                {
                    lastCommandRetirementDiagnostic =
                        "conflicting-retirement-claim";
                    commandQuarantined = true;
                    terminalFence.LatchNoLock();
                    return Switch2ProUsbStartupRetirementCompletion.
                        PossiblyReleased(claim, claim.Reason);
                }
                retirementClaim = claim;
                commandRetirementInProgress = true;
                operation = activeCommandOperation;
                issueCancellation = operation != null &&
                    !commandCancellationIssued;
                deadline = Switch2ProUsbWindowsDeadline.Start(
                    timeoutMilliseconds);
            }
        }

        if (operation != null)
        {
            try
            {
                if (issueCancellation)
                {
                    bool cancellationAccepted = operation.TryCancelExact();
                    if (cancellationAccepted)
                    {
                        lock (commandGate)
                        {
                            if (ReferenceEquals(activeCommandOperation,
                                    operation))
                            {
                                commandCancellationIssued = true;
                            }
                        }
                    }
                }
                if (!operation.TryWaitForNativeQuiescence(
                        Switch2ProUsbWindowsDeadline.Remaining(deadline,
                            timeoutMilliseconds)))
                {
                    lastCommandRetirementDiagnostic =
                        "operation-quiescence-timeout";
                    ReleaseCommandRetirement();
                    return Switch2ProUsbStartupRetirementCompletion.
                        ProvenNotReleased(claim, claim.Reason);
                }
                if (!operation.TryGetCompletion(out _))
                {
                    lastCommandRetirementDiagnostic =
                        "operation-completion-rejected";
                    return QuarantineCommandRetirement(claim);
                }
                operation.ReleaseSubmissionQuiesced();
            }
            catch
            {
                lastCommandRetirementDiagnostic =
                    "operation-retirement-threw";
                return QuarantineCommandRetirement(claim);
            }

            bool stateMismatch;
            lock (commandGate)
            {
                stateMismatch = !ReferenceEquals(activeCommandOperation,
                    operation);
                if (!stateMismatch)
                {
                    activeCommandOperation = null;
                    activeCommandClaim = default;
                    commandCallInProgress = false;
                    commandCancellationIssued = false;
                }
            }
            if (stateMismatch)
            {
                lastCommandRetirementDiagnostic =
                    "operation-state-mismatch";
                return QuarantineCommandRetirement(claim);
            }
        }

        try
        {
            command.DisposeQuiesced();
        }
        catch (Switch2ProUsbWindowsRetryableReleaseException)
        {
            lastCommandRetirementDiagnostic =
                "command-dispose-retryable:" +
                (command as Switch2ProUsbWindowsOwnedCommandHandle)?
                    .LastDisposeDiagnostic;
            ReleaseCommandRetirement();
            return Switch2ProUsbStartupRetirementCompletion.
                ProvenNotReleased(claim, claim.Reason);
        }
        catch
        {
            lastCommandRetirementDiagnostic =
                "command-dispose-uncertain:" +
                (command as Switch2ProUsbWindowsOwnedCommandHandle)?
                    .LastDisposeDiagnostic;
            return QuarantineCommandRetirement(claim);
        }

        lock (commandGate)
        {
            commandRetired = true;
            commandRetirementRequired = false;
            commandRetirementInProgress = false;
            activeCommandClaim = default;
            retirementClaim = default;
            Monitor.PulseAll(commandGate);
        }
        lastCommandRetirementDiagnostic = "succeeded";
        return Switch2ProUsbStartupRetirementCompletion.Released(claim,
            claim.Reason);
    }

    internal string LastCommandRetirementDiagnostic =>
        lastCommandRetirementDiagnostic;

    public void DisposeQuiesced()
    {
        lock (disposalGate)
        {
            if (disposalInProgress)
            {
                throw new InvalidOperationException(
                    "Owned-composite disposal is already in progress.");
            }
        }
        lock (terminalFence.Gate)
        {
            if (terminalFence.IsLatchedNoLock)
            {
                throw new InvalidOperationException(
                    "The owned composite is terminally quarantined.");
            }
            lock (disposalGate)
            {
                if (reservationReleased)
                {
                    return;
                }
                if (disposalInProgress)
                {
                    throw new InvalidOperationException(
                        "Owned-composite disposal is already in progress.");
                }
                lock (commandGate)
                {
                    if (!commandRetired || commandQuarantined ||
                        commandCallInProgress ||
                        commandRetirementInProgress ||
                        activeCommandOperation != null)
                    {
                        throw new InvalidOperationException(
                            "The command facet is not exactly retired.");
                    }
                }
                if (!outputLane.TrySealForDisposal())
                {
                    throw new InvalidOperationException(
                        "The output facet is not exactly quiescent.");
                }
                disposalInProgress = true;
            }
        }

        Exception failure = null;
        try
        {
            if (!inputDisposed)
            {
                inputLease.DisposeQuiesced();
                lock (disposalGate)
                {
                    inputDisposed = true;
                }
            }
            if (!reservationReleased)
            {
                if (reservation != null)
                {
                    reservation.ReleaseAfterTerminalDisposal(terminalFence,
                        this);
                }
                else
                {
                    terminalFence.PublishTerminalReleaseWithoutReservation();
                }
                lock (disposalGate)
                {
                    reservationReleased = true;
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            lock (disposalGate)
            {
                disposalInProgress = false;
                Monitor.PulseAll(disposalGate);
            }
        }
        if (failure != null)
        {
            throw failure;
        }
    }

    private bool TryBeginCommandWrite(
        in Switch2ProUsbStartupCommandClaim claim, int count)
    {
        ISwitch2ProUsbWindowsOwnedIoOperation operation = null;
        bool started = false;
        bool dependencyThrew = false;
        lock (terminalFence.Gate)
        {
            if (!terminalFence.TryBeginSubmissionNoLock())
            {
                if (terminalFence.IsLatchedNoLock)
                {
                    lock (commandGate)
                    {
                        commandQuarantined = true;
                        commandRetirementRequired = true;
                    }
                }
                return false;
            }
            try
            {
                try
                {
                    started = command.TryBeginBulkWrite(commandRequest, 0,
                        count, out operation);
                }
                catch
                {
                    dependencyThrew = true;
                }
                return PublishCommandOperation(claim, operation, started,
                    dependencyThrew);
            }
            finally
            {
                terminalFence.EndSubmissionNoLock();
            }
        }
    }

    private bool TryBeginCommandRead(
        in Switch2ProUsbStartupCommandClaim claim)
    {
        Array.Clear(commandResponse);
        ISwitch2ProUsbWindowsOwnedIoOperation operation = null;
        bool started = false;
        bool dependencyThrew = false;
        lock (terminalFence.Gate)
        {
            if (!terminalFence.TryBeginSubmissionNoLock())
            {
                if (terminalFence.IsLatchedNoLock)
                {
                    lock (commandGate)
                    {
                        commandQuarantined = true;
                        commandRetirementRequired = true;
                    }
                }
                return false;
            }
            try
            {
                try
                {
                    started = command.TryBeginBulkRead(commandResponse, 0,
                        commandResponse.Length, out operation);
                }
                catch
                {
                    dependencyThrew = true;
                }
                return PublishCommandOperation(claim, operation, started,
                    dependencyThrew);
            }
            finally
            {
                terminalFence.EndSubmissionNoLock();
            }
        }
    }

    private bool PublishCommandOperation(
        in Switch2ProUsbStartupCommandClaim claim,
        ISwitch2ProUsbWindowsOwnedIoOperation operation, bool started,
        bool dependencyThrew)
    {
        lock (commandGate)
        {
            if (!commandCallInProgress || !activeCommandClaim.Equals(claim) ||
                activeCommandOperation != null)
            {
                commandQuarantined = true;
                commandRetirementRequired = true;
                terminalFence.LatchNoLock();
                return false;
            }
            if (!started && operation == null && !dependencyThrew)
            {
                return false;
            }
            if (!started || operation == null || dependencyThrew)
            {
                activeCommandOperation = operation;
                commandQuarantined = true;
                commandRetirementRequired = true;
                terminalFence.LatchNoLock();
                return false;
            }
            activeCommandOperation = operation;
            return true;
        }
    }

    private bool TryCompleteCommandPhase(long deadline,
        int timeoutMilliseconds, int expectedBytes, out bool completed)
    {
        ISwitch2ProUsbWindowsOwnedIoOperation operation;
        lock (commandGate)
        {
            operation = activeCommandOperation;
        }
        if (operation == null)
        {
            LatchCommandQuarantine(clearCallInProgress: false);
            completed = false;
            return true;
        }

        int remaining = Switch2ProUsbWindowsDeadline.Remaining(deadline,
            timeoutMilliseconds);
        if (!operation.TryWaitForNativeQuiescence(remaining))
        {
            completed = false;
            return false;
        }
        if (!operation.TryGetCompletion(out var completion))
        {
            LatchCommandQuarantine(clearCallInProgress: false);
            completed = false;
            return true;
        }
        operation.ReleaseSubmissionQuiesced();
        bool stateMismatch;
        lock (commandGate)
        {
            stateMismatch = !ReferenceEquals(activeCommandOperation,
                operation);
            if (!stateMismatch)
            {
                activeCommandOperation = null;
            }
        }
        if (stateMismatch)
        {
            LatchCommandQuarantine(clearCallInProgress: false);
            completed = false;
            return false;
        }

        completed = completion.Status ==
                Switch2ProUsbNativeReadStatus.Completed &&
            completion.BytesTransferred >= 0 &&
            (expectedBytes < 0 ||
             completion.BytesTransferred == expectedBytes);
        if (commandOperationIsRead && completed)
        {
            int required = GetStartupResponseLength(
                activeCommandClaim.Step);
            completed = completion.BytesTransferred == required;
        }
        return true;
    }

    private Switch2ProUsbStartupCommandCompletion RetainTimedOutCommand(
        in Switch2ProUsbStartupCommandClaim claim)
    {
        lock (commandGate)
        {
            commandCallInProgress = false;
            commandRetirementRequired = true;
            Monitor.PulseAll(commandGate);
        }
        return Switch2ProUsbStartupCommandCompletion.TimedOut(claim,
            claim.Step);
    }

    private Switch2ProUsbStartupCommandCompletion FinishCommandCall(
        in Switch2ProUsbStartupCommandClaim claim, bool provenNotConsumed)
    {
        lock (terminalFence.Gate)
        {
            lock (commandGate)
            {
                bool hasRetainedOperation = activeCommandOperation != null;
                commandCallInProgress = false;
                if (hasRetainedOperation)
                {
                    commandQuarantined = true;
                    terminalFence.LatchNoLock();
                }
                if (!provenNotConsumed || hasRetainedOperation ||
                    commandQuarantined)
                {
                    commandRetirementRequired = true;
                }
                if (!hasRetainedOperation && !commandRetirementRequired)
                {
                    activeCommandClaim = default;
                }
                Monitor.PulseAll(commandGate);
                return provenNotConsumed && !hasRetainedOperation &&
                        !commandQuarantined ?
                    Switch2ProUsbStartupCommandCompletion.ProvenNotConsumed(
                        claim, claim.Step) :
                    Switch2ProUsbStartupCommandCompletion.PossiblyConsumed(
                        claim, claim.Step);
            }
        }
    }

    private bool TryClearCompletedCommandCall()
    {
        lock (terminalFence.Gate)
        {
            lock (commandGate)
            {
                if (terminalFence.IsLatchedNoLock)
                {
                    commandQuarantined = true;
                    commandRetirementRequired = true;
                    commandCallInProgress = false;
                    Monitor.PulseAll(commandGate);
                    return false;
                }
                commandCallInProgress = false;
                activeCommandClaim = default;
                commandCancellationIssued = false;
                commandRetirementRequired = false;
                Monitor.PulseAll(commandGate);
                return true;
            }
        }
    }

    private void ReleaseCommandRetirement()
    {
        lock (commandGate)
        {
            commandRetirementInProgress = false;
            Monitor.PulseAll(commandGate);
        }
    }

    private Switch2ProUsbStartupRetirementCompletion
        QuarantineCommandRetirement(
            in Switch2ProUsbStartupRetirementClaim claim)
    {
        LatchCommandQuarantine(clearCallInProgress: false,
            clearRetirementInProgress: true);
        return Switch2ProUsbStartupRetirementCompletion.PossiblyReleased(
            claim, claim.Reason);
    }

    private void LatchCommandQuarantine(bool clearCallInProgress,
        bool clearRetirementInProgress = false)
    {
        lock (terminalFence.Gate)
        {
            terminalFence.LatchNoLock();
            lock (commandGate)
            {
                commandQuarantined = true;
                commandRetirementRequired = true;
                if (clearCallInProgress)
                {
                    commandCallInProgress = false;
                }
                if (clearRetirementInProgress)
                {
                    commandRetirementInProgress = false;
                }
                Monitor.PulseAll(commandGate);
            }
        }
    }

    private static bool RequestMatchesClaim(Switch2ProUsbStartupStep step,
        ReadOnlySpan<byte> request,
        out Switch2PlayerLedCommand playerLedCommand)
    {
        playerLedCommand = default;
        if (step == Switch2ProUsbStartupStep.SetPlayerLed)
        {
            return Switch2UsbCommandCodec.TryDecodePlayerLedRequest(request,
                out playerLedCommand, out _);
        }

        return step switch
        {
            Switch2ProUsbStartupStep.EnableUsbHidReports =>
                Switch2UsbCommandCodec.TryValidateInitializationRequest(
                    request,
                    Switch2UsbInitializationStep.EnableUsbHidReports,
                    out _),
            Switch2ProUsbStartupStep.SetFeatureMask =>
                Switch2UsbCommandCodec.TryValidateFeatureRequest(request,
                    Switch2UsbFeatureStep.SetFeatureMask,
                    Switch2UsbFeatureMask.ButtonsSticksImuAndRumble,
                    out _),
            Switch2ProUsbStartupStep.EnableFeatures =>
                Switch2UsbCommandCodec.TryValidateFeatureRequest(request,
                    Switch2UsbFeatureStep.EnableFeatures,
                    Switch2UsbFeatureMask.ButtonsSticksImuAndRumble,
                    out _),
            Switch2ProUsbStartupStep.SelectCommonInputReport =>
                Switch2UsbCommandCodec.TryValidateInitializationRequest(
                    request,
                    Switch2UsbInitializationStep.SelectCommonInputReport,
                    out _),
            Switch2ProUsbStartupStep.ReadFactoryPrimaryCalibration =>
                Switch2UsbCommandCodec.TryValidateCalibrationReadRequest(
                    request, Switch2UsbCalibrationRead.FactoryPrimary,
                    out _),
            Switch2ProUsbStartupStep.ReadFactorySecondaryCalibration =>
                Switch2UsbCommandCodec.TryValidateCalibrationReadRequest(
                    request, Switch2UsbCalibrationRead.FactorySecondary,
                    out _),
            Switch2ProUsbStartupStep.ReadUserPrimaryCalibration =>
                Switch2UsbCommandCodec.TryValidateCalibrationReadRequest(
                    request, Switch2UsbCalibrationRead.UserPrimary, out _),
            Switch2ProUsbStartupStep.ReadUserSecondaryCalibration =>
                Switch2UsbCommandCodec.TryValidateCalibrationReadRequest(
                    request, Switch2UsbCalibrationRead.UserSecondary, out _),
            _ => false,
        };
    }

    private static Switch2UsbInitializationStep MapInitializationStep(
        Switch2ProUsbStartupStep step) => step switch
        {
            Switch2ProUsbStartupStep.EnableUsbHidReports =>
                Switch2UsbInitializationStep.EnableUsbHidReports,
            Switch2ProUsbStartupStep.SelectCommonInputReport =>
                Switch2UsbInitializationStep.SelectCommonInputReport,
            _ => default,
        };

    private static int GetStartupResponseLength(
        Switch2ProUsbStartupStep step) => step switch
        {
            Switch2ProUsbStartupStep.SetPlayerLed =>
                Switch2UsbCommandCodec.PlayerLedResponseLength,
            Switch2ProUsbStartupStep.SetFeatureMask or
                Switch2ProUsbStartupStep.EnableFeatures =>
                Switch2UsbCommandCodec.FeatureResponseLength,
            Switch2ProUsbStartupStep.ReadFactoryPrimaryCalibration =>
                GetCalibrationResponseLength(
                    Switch2UsbCalibrationRead.FactoryPrimary),
            Switch2ProUsbStartupStep.ReadFactorySecondaryCalibration =>
                GetCalibrationResponseLength(
                    Switch2UsbCalibrationRead.FactorySecondary),
            Switch2ProUsbStartupStep.ReadUserPrimaryCalibration =>
                GetCalibrationResponseLength(
                    Switch2UsbCalibrationRead.UserPrimary),
            Switch2ProUsbStartupStep.ReadUserSecondaryCalibration =>
                GetCalibrationResponseLength(
                    Switch2UsbCalibrationRead.UserSecondary),
            _ => Switch2UsbCommandCodec.TryGetInitializationResponseLength(
                MapInitializationStep(step), out int length) ? length : 0,
        };

    private static bool TryValidateStartupResponse(
        Switch2ProUsbStartupStep step, ReadOnlySpan<byte> response,
        Switch2PlayerLedCommand playerLedCommand,
        out Switch2ProUsbStartupResponseProofKind proof,
        out byte[] payload)
    {
        payload = null;
        switch (step)
        {
            case Switch2ProUsbStartupStep.EnableUsbHidReports:
            case Switch2ProUsbStartupStep.SelectCommonInputReport:
                proof = Switch2ProUsbStartupResponseProofKind.
                    InitializationResponseValidatedByCodec;
                return Switch2UsbCommandCodec.
                    TryValidateInitializationResponse(response,
                        MapInitializationStep(step), out _);
            case Switch2ProUsbStartupStep.SetPlayerLed:
                proof = Switch2ProUsbStartupResponseProofKind.
                    PlayerLedResponseValidatedByCodec;
                return Switch2UsbCommandCodec.TryValidatePlayerLedResponse(
                    response, playerLedCommand, out _);
            case Switch2ProUsbStartupStep.SetFeatureMask:
            case Switch2ProUsbStartupStep.EnableFeatures:
                proof = Switch2ProUsbStartupResponseProofKind.
                    FeatureResponseValidatedByCodec;
                return Switch2UsbCommandCodec.TryValidateFeatureResponse(
                    response, step ==
                        Switch2ProUsbStartupStep.SetFeatureMask ?
                        Switch2UsbFeatureStep.SetFeatureMask :
                        Switch2UsbFeatureStep.EnableFeatures, out _);
            case Switch2ProUsbStartupStep.ReadFactoryPrimaryCalibration:
                return TryCopyCalibrationResponse(response,
                    Switch2UsbCalibrationRead.FactoryPrimary, out proof,
                    out payload);
            case Switch2ProUsbStartupStep.ReadFactorySecondaryCalibration:
                return TryCopyCalibrationResponse(response,
                    Switch2UsbCalibrationRead.FactorySecondary, out proof,
                    out payload);
            case Switch2ProUsbStartupStep.ReadUserPrimaryCalibration:
                return TryCopyCalibrationResponse(response,
                    Switch2UsbCalibrationRead.UserPrimary, out proof,
                    out payload);
            case Switch2ProUsbStartupStep.ReadUserSecondaryCalibration:
                return TryCopyCalibrationResponse(response,
                    Switch2UsbCalibrationRead.UserSecondary, out proof,
                    out payload);
            default:
                proof = Switch2ProUsbStartupResponseProofKind.Invalid;
                return false;
        }
    }

    private static int GetCalibrationResponseLength(
        Switch2UsbCalibrationRead read) =>
        Switch2UsbCommandCodec.TryGetCalibrationReadResponseLength(read,
            out int length) ? length : 0;

    private static bool TryCopyCalibrationResponse(
        ReadOnlySpan<byte> response, Switch2UsbCalibrationRead read,
        out Switch2ProUsbStartupResponseProofKind proof, out byte[] payload)
    {
        int length = read is Switch2UsbCalibrationRead.FactoryPrimary or
            Switch2UsbCalibrationRead.FactorySecondary ?
            Switch2CalibrationCodec.StickCalibrationLength :
            Switch2CalibrationCodec.UserStickCalibrationLength;
        payload = new byte[length];
        proof = Switch2ProUsbStartupResponseProofKind.
            CalibrationReadResponseValidatedByCodec;
        if (Switch2UsbCommandCodec.TryCopyCalibrationReadResponse(response,
                read, payload, out _))
        {
            return true;
        }

        payload = null;
        proof = Switch2ProUsbStartupResponseProofKind.Invalid;
        return false;
    }

}

internal sealed class Switch2ProUsbWindowsNoOpPresenceHandle :
    ISwitch2ProUsbWindowsPresenceHandle
{
    internal static Switch2ProUsbWindowsNoOpPresenceHandle Instance { get; }
        = new();

    private Switch2ProUsbWindowsNoOpPresenceHandle()
    {
    }

    public void Dispose()
    {
    }
}
