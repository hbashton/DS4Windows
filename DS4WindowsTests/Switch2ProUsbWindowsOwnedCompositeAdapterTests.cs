using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows.Switch2;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2ProUsbWindowsOwnedCompositeAdapterTests
{
    private const ulong DeviceGeneration = 71;
    private const ulong TransportGeneration = 73;

    [TestMethod]
    public void Adapter_AcquiresOneExactPairAndNeverRediscoversAfterEscape()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var events = new List<string>();
        var hid = new FakeOwnedHid(events);
        var command = new FakeOwnedCommand(events);
        var platform = new FakeOwnedPlatform(hid, command, candidate,
            candidate);
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var adapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(platform,
            reservations);
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);

        Assert.IsTrue(adapter.TryOpenOwnedComposite(lifetime.Registration,
            lifetime, out var abstractLease));
        var lease = (Switch2ProUsbWindowsOwnedCompositeLease)abstractLease;
        Assert.AreEqual(2, platform.DiscoverCalls);
        Assert.AreEqual(1, platform.HidOpenCalls);
        Assert.AreEqual(1, platform.CommandOpenCalls);
        Assert.AreEqual(lifetime.Registration, lease.Registration);
        Assert.AreEqual(lifetime, lease.Lifetime);

        // Pure identity reads and post-escape facet calls must not reopen or
        // rediscover the physical device.
        Assert.IsTrue(lease.AuthenticatesComposite(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration));
        Assert.AreEqual(2, platform.DiscoverCalls);

        RetireCommandAndDispose(lease, lifetime);
        CollectionAssert.AreEqual(new[]
        {
            "open.hid",
            "open.command",
            "dispose.command",
            "dispose.hid",
        }, events);
        Assert.IsFalse(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
    }

    [TestMethod]
    public void Adapter_RevalidationIdentityChangeCleansInReverseOrder()
    {
        Switch2ProUsbWindowsCandidate first = Candidate("A");
        Switch2ProUsbWindowsCandidate rebound = Candidate("B");
        var events = new List<string>();
        var hid = new FakeOwnedHid(events);
        var command = new FakeOwnedCommand(events);
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var adapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(
            new FakeOwnedPlatform(hid, command, first, rebound),
            reservations);
        Switch2PhysicalInputLifetime lifetime = Lifetime(first);

        Assert.IsFalse(adapter.TryOpenOwnedComposite(lifetime.Registration,
            lifetime, out _));
        CollectionAssert.AreEqual(new[]
        {
            "open.hid",
            "open.command",
            "dispose.command",
            "dispose.hid",
        }, events);
        Assert.IsFalse(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
    }

    [TestMethod]
    public void Adapter_PartialOpenCleanupReleasesReservationForExactRetry()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var failedHid = new FakeOwnedHid();
        var failedPlatform = new FakeOwnedPlatform(failedHid, null,
            candidate)
        {
            RejectCommandOpen = true,
        };
        var failedAdapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(
            failedPlatform, reservations);
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);

        Assert.IsFalse(failedAdapter.TryOpenOwnedComposite(
            lifetime.Registration, lifetime, out _));
        Assert.AreEqual(1, failedHid.DisposeCalls);
        Assert.IsFalse(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));

        var retryAdapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(
            new FakeOwnedPlatform(new FakeOwnedHid(),
                new FakeOwnedCommand(), candidate, candidate),
            reservations);
        Assert.IsTrue(retryAdapter.TryOpenOwnedComposite(
            lifetime.Registration, lifetime, out var retry));
        RetireCommandAndDispose(
            (Switch2ProUsbWindowsOwnedCompositeLease)retry, lifetime);
    }

    [TestMethod]
    public void Adapter_UnprovenPartialCleanupPermanentlyRetainsReservation()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var failedHid = new FakeOwnedHid
        {
            ThrowOnDispose = true,
        };
        var failedAdapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(
            new FakeOwnedPlatform(failedHid, null, candidate)
            {
                RejectCommandOpen = true,
            }, reservations);
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);

        Assert.IsFalse(failedAdapter.TryOpenOwnedComposite(
            lifetime.Registration, lifetime, out _));
        Assert.IsTrue(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
        Assert.IsTrue(reservations.RetainsAcquisitionQuarantine(
            lifetime.Registration.ContainerIdentity, failedHid));

        var blockedPlatform = new FakeOwnedPlatform(new FakeOwnedHid(),
            new FakeOwnedCommand(), candidate, candidate);
        var blocked = new Switch2ProUsbWindowsOwnedCompositeAdapter(
            blockedPlatform, reservations);
        Assert.IsFalse(blocked.TryOpenOwnedComposite(lifetime.Registration,
            lifetime, out _));
        Assert.AreEqual(0, blockedPlatform.DiscoverCalls,
            "Reservation must reject before metadata handles are opened.");
    }

    [TestMethod]
    public void Adapter_TrueWithNullHidOpenPermanentlyFencesContainer()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var platform = new FakeOwnedPlatform(new FakeOwnedHid(),
            new FakeOwnedCommand(), candidate)
        {
            ReturnTrueWithNullHid = true,
        };
        var adapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(platform,
            reservations);
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);

        Assert.IsFalse(adapter.TryOpenOwnedComposite(lifetime.Registration,
            lifetime, out _));
        Assert.IsTrue(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
        Assert.AreEqual(1, platform.HidOpenCalls);
        Assert.AreEqual(0, platform.CommandOpenCalls);
    }

    [TestMethod]
    public void Adapter_TrueWithNullCommandOpenCleansHidButKeepsFence()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var hid = new FakeOwnedHid();
        var platform = new FakeOwnedPlatform(hid, new FakeOwnedCommand(),
            candidate)
        {
            ReturnTrueWithNullCommand = true,
        };
        var adapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(platform,
            reservations);
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);

        Assert.IsFalse(adapter.TryOpenOwnedComposite(lifetime.Registration,
            lifetime, out _));
        Assert.AreEqual(1, hid.DisposeCalls);
        Assert.IsTrue(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
    }

    [TestMethod]
    public void Adapter_FirstDiscoveryCleanupAmbiguityRetainsReservation()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var platform = new FakeOwnedPlatform(new FakeOwnedHid(),
            new FakeOwnedCommand(), candidate)
        {
            ThrowOnDiscoverCall = 1,
        };
        var adapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(platform,
            reservations);
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);

        Assert.IsFalse(adapter.TryOpenOwnedComposite(lifetime.Registration,
            lifetime, out _));
        Assert.AreEqual(1, platform.DiscoverCalls);
        Assert.AreEqual(0, platform.HidOpenCalls);
        Assert.IsTrue(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
        Assert.IsTrue(reservations.RetainsAcquisitionQuarantine(
            lifetime.Registration.ContainerIdentity,
            platform.DiscoveryRetainedOwner));
    }

    [TestMethod]
    public void Adapter_RevalidationCleanupAmbiguityCleansHandlesButRetainsReservation()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var hid = new FakeOwnedHid();
        var command = new FakeOwnedCommand();
        var platform = new FakeOwnedPlatform(hid, command, candidate,
            candidate)
        {
            ThrowOnDiscoverCall = 2,
        };
        var adapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(platform,
            reservations);
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);

        Assert.IsFalse(adapter.TryOpenOwnedComposite(lifetime.Registration,
            lifetime, out _));
        Assert.AreEqual(2, platform.DiscoverCalls);
        Assert.AreEqual(1, command.DisposeCalls);
        Assert.AreEqual(1, hid.DisposeCalls);
        Assert.IsTrue(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
        Assert.IsTrue(reservations.RetainsAcquisitionQuarantine(
            lifetime.Registration.ContainerIdentity,
            platform.DiscoveryRetainedOwner));
    }

    [TestMethod]
    public void TerminalReservationReleaseFailureRetainsExactRetryCapability()
    {
        int releaseCalls = 0;
        var reservations = new Switch2ProUsbWindowsReservationRegistry(() =>
        {
            if (Interlocked.Increment(ref releaseCalls) == 1)
            {
                throw new InvalidOperationException(
                    "Synthetic first release failure.");
            }
        });
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var hid = new FakeOwnedHid();
        var command = new FakeOwnedCommand();
        var adapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(
            new FakeOwnedPlatform(hid, command, candidate, candidate),
            reservations);
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);
        Assert.IsTrue(adapter.TryOpenOwnedComposite(lifetime.Registration,
            lifetime, out var abstractLease));
        var lease = (Switch2ProUsbWindowsOwnedCompositeLease)abstractLease;
        var retirement = new Switch2ProUsbStartupRetirementClaim(new object(),
            lease, lifetime, Switch2ProUsbStartupRetirementReason.Explicit,
            1);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirement, 0).Outcome);
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));

        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
        Assert.IsTrue(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(
            lifetime.Registration.ContainerIdentity, lease));
        Assert.AreEqual(1, hid.DisposeCalls,
            "Physical handles are already terminal and must not be repeated.");

        lease.DisposeQuiesced();
        Assert.AreEqual(2, releaseCalls);
        Assert.IsFalse(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
        Assert.AreEqual(1, hid.DisposeCalls);
    }

    [TestMethod]
    public void RegistryStrongRootsOwnedLeaseAcrossCallerDropAndGc()
    {
        int releaseCalls = 0;
        var reservations = new Switch2ProUsbWindowsReservationRegistry(() =>
        {
            if (Interlocked.Increment(ref releaseCalls) == 1)
            {
                throw new InvalidOperationException(
                    "Synthetic terminal publication failure.");
            }
        });
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);
        WeakReference<Switch2ProUsbWindowsOwnedCompositeLease> weak =
            OpenOwnedAndDropAfterTerminalFailure(reservations, candidate,
                lifetime);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.IsTrue(weak.TryGetTarget(out var retained));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(
            lifetime.Registration.ContainerIdentity, retained));
        retained.DisposeQuiesced();
        Assert.AreEqual(2, releaseCalls);
        Assert.IsFalse(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
    }

    [TestMethod]
    public void StaleInputCallbackDuringDisposalCannotReleaseReservation()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var hid = new FakeOwnedHid();
        var command = new FakeOwnedCommand();
        var adapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(
            new FakeOwnedPlatform(hid, command, candidate, candidate),
            reservations);
        Assert.IsTrue(adapter.TryOpenOwnedComposite(lifetime.Registration,
            lifetime, out var abstractLease));
        var lease = (Switch2ProUsbWindowsOwnedCompositeLease)abstractLease;
        var readClaim = new Switch2ProUsbReadClaim(new object(),
            DeviceGeneration, TransportGeneration, 1);
        Assert.IsTrue(lease.TryBeginInputRead(new byte[64], 0, 64, readClaim,
            new RecordingReadTarget()));
        hid.CompleteInput(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));

        var retirement = new Switch2ProUsbStartupRetirementClaim(new object(),
            lease, lifetime, Switch2ProUsbStartupRetirementReason.Explicit, 1);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirement, 0).Outcome);
        hid.ReplayInputCompletionDuringDispose = true;

        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
        Assert.IsTrue(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(
            lifetime.Registration.ContainerIdentity, lease));
        Assert.AreEqual(1, hid.DisposeCalls);
    }

    [TestMethod]
    public void ReentrantCallbackFromReservationHookPreventsPublication()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);
        FakeOwnedHid hid = null;
        var reservations = new Switch2ProUsbWindowsReservationRegistry(() =>
            hid.ReplayInputCompletion(
                Switch2ProUsbNativeReadStatus.Completed, 64));
        hid = new FakeOwnedHid();
        var command = new FakeOwnedCommand();
        var adapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(
            new FakeOwnedPlatform(hid, command, candidate, candidate),
            reservations);
        Assert.IsTrue(adapter.TryOpenOwnedComposite(lifetime.Registration,
            lifetime, out var abstractLease));
        var lease = (Switch2ProUsbWindowsOwnedCompositeLease)abstractLease;
        var readClaim = new Switch2ProUsbReadClaim(new object(),
            DeviceGeneration, TransportGeneration, 1);
        Assert.IsTrue(lease.TryBeginInputRead(new byte[64], 0, 64, readClaim,
            new RecordingReadTarget()));
        hid.CompleteInput(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        var retirement = new Switch2ProUsbStartupRetirementClaim(new object(),
            lease, lifetime, Switch2ProUsbStartupRetirementReason.Explicit, 1);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirement, 0).Outcome);

        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
        Assert.IsTrue(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(
            lifetime.Registration.ContainerIdentity, lease));
        Assert.AreEqual(1, hid.DisposeCalls);
    }

    [TestMethod]
    public void ConcurrentCallbackFromReservationHookPreventsPublication()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);
        FakeOwnedHid hid = null;
        var reservations = new Switch2ProUsbWindowsReservationRegistry(() =>
        {
            Task callback = Task.Run(() => hid.ReplayInputCompletion(
                Switch2ProUsbNativeReadStatus.Completed, 64));
            if (!callback.Wait(1_000))
            {
                throw new InvalidOperationException(
                    "Concurrent callback did not cross the release hook.");
            }
        });
        hid = new FakeOwnedHid();
        var command = new FakeOwnedCommand();
        var adapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(
            new FakeOwnedPlatform(hid, command, candidate, candidate),
            reservations);
        Assert.IsTrue(adapter.TryOpenOwnedComposite(lifetime.Registration,
            lifetime, out var abstractLease));
        var lease = (Switch2ProUsbWindowsOwnedCompositeLease)abstractLease;
        var readClaim = new Switch2ProUsbReadClaim(new object(),
            DeviceGeneration, TransportGeneration, 1);
        Assert.IsTrue(lease.TryBeginInputRead(new byte[64], 0, 64, readClaim,
            new RecordingReadTarget()));
        hid.CompleteInput(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        var retirement = new Switch2ProUsbStartupRetirementClaim(new object(),
            lease, lifetime, Switch2ProUsbStartupRetirementReason.Explicit, 1);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirement, 0).Outcome);

        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
        Assert.IsTrue(reservations.IsReserved(
            lifetime.Registration.ContainerIdentity));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(
            lifetime.Registration.ContainerIdentity, lease));
        Assert.AreEqual(1, hid.DisposeCalls);
    }

    [TestMethod]
    public void SameMi00ObjectCarriesOutstandingInputAndCompletedOutput()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        Switch2PhysicalInputLifetime lifetime = Lifetime(candidate);
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(FakeIoOperation.Completed(64));
        var lease = DirectLease(lifetime, hid, new FakeOwnedCommand());
        var target = new RecordingReadTarget();
        var inputClaim = new Switch2ProUsbReadClaim(new object(),
            DeviceGeneration, TransportGeneration, 1);

        Assert.IsTrue(lease.TryBeginInputRead(new byte[64], 0, 64,
            inputClaim, target));
        Switch2ProUsbOwnedOutputWriteAttempt output =
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 100);

        Assert.AreEqual(1, hid.InputBeginCalls);
        Assert.AreEqual(1, hid.OutputBeginCalls);
        Assert.AreEqual(
            Switch2ProUsbHdRumbleTransportWriteOutcome.Completed,
            output.TransportResult.Outcome);
        Assert.IsFalse(output.RetainedClaim.IsValid);
    }

    [TestMethod]
    public void DormantFeedbackAdoptionIsNeverStartedOneShotAndSoleOutput()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(FakeIoOperation.Completed(64));
        var lease = DirectLease(lifetime, hid, new FakeOwnedCommand());
        var ownerFence = new object();

        Assert.IsFalse((object)lease is
            ISwitch2ProUsbOwnedFeedbackOutputLease,
            "A full input/startup/disposal alias must not satisfy the bridge's narrow output type.");
        Assert.IsTrue(lease.TryAdoptDormantFeedbackOutput(ownerFence,
            out ISwitch2ProUsbOwnedFeedbackOutputLease output));
        Assert.IsNotNull(output);
        Assert.IsFalse(lease.TryAdoptDormantFeedbackOutput(new object(),
            out _), "The lane can never mint an ABA/foreign adoption.");

        Switch2ProUsbOwnedOutputWriteAttempt direct =
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 100);
        Assert.AreEqual(
            Switch2ProUsbHdRumbleTransportWriteOutcome.ProvenRejected,
            direct.TransportResult.Outcome);
        Assert.AreEqual(0, hid.OutputBeginCalls,
            "A previously escaped full alias must not touch native output after adoption.");

        Switch2ProUsbOwnedOutputWriteAttempt mediated =
            output.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 100);
        Assert.AreEqual(
            Switch2ProUsbHdRumbleTransportWriteOutcome.Completed,
            mediated.TransportResult.Outcome);
        Assert.AreEqual(1, hid.OutputBeginCalls);

        RetireCommandAndDispose(lease, lifetime);
        Assert.IsFalse(output.AuthenticatesComposite(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration),
            "Terminal disposal invalidates the exact adoption capability.");
        output.TryWriteReportBounded(NeutralReport(),
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, 100);
        Assert.AreEqual(1, hid.OutputBeginCalls,
            "A copied capability cannot revive output after terminal invalidation.");
    }

    [DataTestMethod]
    [DataRow(0u, false)]
    [DataRow(31u, false)]
    [DataRow(995u, false)]
    [DataRow(997u, false)]
    [DataRow(121u, false)]
    [DataRow(433u, true)]
    [DataRow(1167u, true)]
    public void OnlyDefiniteNativeRemovalAuthorizesDisconnectedRetirement(uint error, bool expected)
    {
        Assert.AreEqual(expected, Switch2ProUsbWindowsReadStatusMap.IsDefiniteDeviceRemoval(error));
    }

    [TestMethod]
    public void DisconnectedOutputSealRequiresExactAdoptionAndBlocksFurtherWrites()
    {
        var lifetime = Lifetime(Candidate());
        var hid = new FakeOwnedHid();
        var lease = DirectLease(lifetime, hid, new FakeOwnedCommand());
        Assert.IsTrue(lease.TryAdoptDormantFeedbackOutput(new object(), out var output));
        Assert.IsFalse(output.TrySealDisconnectedOutput());
        hid.HasObservedDeviceDisconnection = true;
        Assert.IsTrue(output.TrySealDisconnectedOutput());
        Assert.IsTrue(output.TrySealDisconnectedOutput());
        var rejected = output.TryWriteReportBounded(NeutralReport(),
            Switch2ControllerModel.ProController2, DeviceGeneration, TransportGeneration, 100);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.ProvenRejected, rejected.TransportResult.Outcome);
        Assert.AreEqual(0, hid.OutputBeginCalls);
        RetireCommandAndDispose(lease, lifetime);
        Assert.IsFalse(output.TrySealDisconnectedOutput());
    }

    [TestMethod]
    public void DisconnectEvidenceCannotReleaseAnUnretiredNativeOutput()
    {
        var lifetime = Lifetime(Candidate());
        var hid = new FakeOwnedHid();
        var operation = new FakeIoOperation();
        hid.OutputOperations.Enqueue(operation);
        var lease = DirectLease(lifetime, hid, new FakeOwnedCommand());
        Assert.IsTrue(lease.TryAdoptDormantFeedbackOutput(new object(), out var output));
        var retained = output.TryWriteReportBounded(NeutralReport(),
            Switch2ControllerModel.ProController2, DeviceGeneration, TransportGeneration, 100);
        Assert.IsTrue(retained.RequiresRetirement);
        hid.HasObservedDeviceDisconnection = true;
        Assert.IsFalse(output.TrySealDisconnectedOutput());
        operation.Complete(Switch2ProUsbNativeReadStatus.DeviceRemoved, 0);
        output.TryRetireOutputOperation(retained.RetainedClaim, 100);
        Assert.IsTrue(output.TrySealDisconnectedOutput());
        Assert.AreEqual(1, operation.ReleaseCalls);
    }

    [TestMethod]
    public void CompletedDirectOutputPermanentlyRejectsDormantAdoption()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(FakeIoOperation.Completed(64));
        var lease = DirectLease(lifetime, hid, new FakeOwnedCommand());

        Assert.AreEqual(
            Switch2ProUsbHdRumbleTransportWriteOutcome.Completed,
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 100).TransportResult.Outcome);
        Assert.IsFalse(lease.TryAdoptDormantFeedbackOutput(new object(),
            out _),
            "Current quiescence cannot erase the lane's ever-started fact.");
        Assert.AreEqual(1, hid.OutputBeginCalls);
    }

    [TestMethod]
    public void DirectFullAliasCannotProbeOrRetireAdoptedNativeClaim()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var pending = new FakeIoOperation();
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(pending);
        var lease = DirectLease(lifetime, hid, new FakeOwnedCommand());
        Assert.IsTrue(lease.TryAdoptDormantFeedbackOutput(new object(),
            out ISwitch2ProUsbOwnedFeedbackOutputLease output));

        Switch2ProUsbOwnedOutputWriteAttempt attempt =
            output.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 1);
        Assert.IsTrue(attempt.RequiresRetirement);
        Assert.IsTrue(output.AuthenticatesOutputOperationClaim(
            attempt.RetainedClaim));
        Assert.IsFalse(lease.AuthenticatesOutputOperationClaim(
            attempt.RetainedClaim),
            "The participant/disposal alias cannot impersonate the adopted writer.");
        Assert.AreEqual(
            Switch2ProUsbOwnedOutputRetirementOutcome.RequestRejected,
            lease.TryRetireOutputOperation(attempt.RetainedClaim, 1).Outcome);
        Assert.AreEqual(0, pending.CancelCalls);

        pending.Complete(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.AreEqual(
            Switch2ProUsbOwnedOutputRetirementOutcome.
                ExactOperationQuiescent,
            output.TryRetireOutputOperation(attempt.RetainedClaim, 1).Outcome);
        Assert.IsFalse(output.AuthenticatesOutputOperationClaim(
            attempt.RetainedClaim));
    }

    [TestMethod]
    public void DirectStartAndDormantAdoptionHaveOneLinearizedWinner()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var hid = new FakeOwnedHid
        {
            OutputBeginEntered = new ManualResetEventSlim(false),
            AllowOutputBeginReturn = new ManualResetEventSlim(false),
        };
        hid.OutputOperations.Enqueue(FakeIoOperation.Completed(64));
        var lease = DirectLease(lifetime, hid, new FakeOwnedCommand());

        Task<Switch2ProUsbOwnedOutputWriteAttempt> direct = Task.Run(() =>
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 100));
        Assert.IsTrue(hid.OutputBeginEntered.Wait(1_000));
        Task<bool> adoption = Task.Run(() =>
            lease.TryAdoptDormantFeedbackOutput(new object(), out _));
        Assert.IsFalse(adoption.Wait(20),
            "Adoption must linearize behind the exact native-start region.");
        hid.AllowOutputBeginReturn.Set();

        Assert.AreEqual(
            Switch2ProUsbHdRumbleTransportWriteOutcome.Completed,
            direct.GetAwaiter().GetResult().TransportResult.Outcome);
        Assert.IsFalse(adoption.GetAwaiter().GetResult());
        Assert.AreEqual(1, hid.OutputBeginCalls);

        var secondHid = new FakeOwnedHid();
        var second = DirectLease(lifetime, secondHid,
            new FakeOwnedCommand());
        Assert.IsTrue(second.TryAdoptDormantFeedbackOutput(new object(),
            out _));
        second.TryWriteReportBounded(NeutralReport(),
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, 100);
        Assert.AreEqual(0, secondHid.OutputBeginCalls,
            "When adoption wins first, a direct alias cannot start native I/O.");
    }

    [TestMethod]
    public void OutputClaimProvenanceIsExactCurrentAndPureAcrossRetirement()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var pending = new FakeIoOperation();
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(pending);
        var lease = DirectLease(lifetime, hid, new FakeOwnedCommand());
        Switch2ProUsbOwnedOutputWriteAttempt attempt =
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 1);
        Switch2ProUsbOwnedOutputOperationClaim exact =
            attempt.RetainedClaim;

        Assert.IsTrue(exact.IsValid);
        Assert.IsTrue(lease.AuthenticatesOutputOperationClaim(exact));
        var foreignFence = new Switch2ProUsbOwnedOutputOperationClaim(
            new object(), DeviceGeneration, TransportGeneration,
            exact.Sequence);
        Assert.IsFalse(lease.AuthenticatesOutputOperationClaim(foreignFence),
            "Matching generations and sequence cannot forge the lane fence.");
        Assert.AreEqual(0, pending.CancelCalls,
            "The provenance probe must not perform native work.");

        pending.BlockWait = true;
        Task<Switch2ProUsbOwnedOutputRetirementResult> retirement = Task.Run(
            () => lease.TryRetireOutputOperation(exact, 1));
        Assert.IsTrue(pending.WaitEntered.Wait(1_000));
        Assert.IsTrue(lease.AuthenticatesOutputOperationClaim(exact),
            "An in-progress exact retirement does not erase provenance.");
        pending.AllowWait.Set();
        Assert.AreEqual(
            Switch2ProUsbOwnedOutputRetirementOutcome.RetainedForRetry,
            retirement.GetAwaiter().GetResult().Outcome);
        Assert.IsTrue(lease.AuthenticatesOutputOperationClaim(exact));

        pending.BlockWait = false;
        pending.Complete(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.AreEqual(
            Switch2ProUsbOwnedOutputRetirementOutcome.
                ExactOperationQuiescent,
            lease.TryRetireOutputOperation(exact, 1).Outcome);
        Assert.IsFalse(lease.AuthenticatesOutputOperationClaim(exact),
            "Exact quiescence clears the only current claim authority.");
    }

    [TestMethod]
    public void QuarantineDoesNotEraseCurrentOutputClaimProvenance()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var pending = new FakeIoOperation
        {
            ThrowOnWait = true,
        };
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(pending);
        var lease = DirectLease(lifetime, hid, new FakeOwnedCommand());

        Switch2ProUsbOwnedOutputWriteAttempt attempt =
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 1);

        Assert.IsTrue(attempt.RequiresTerminalAttention);
        Assert.IsTrue(attempt.RetainedClaim.IsValid);
        Assert.IsTrue(lease.AuthenticatesOutputOperationClaim(
            attempt.RetainedClaim),
            "A terminal fence cannot turn a still-live operation into false absence proof.");
    }

    [TestMethod]
    public void AmbiguousInputStartQuarantinesEveryOwnedCompositeFacet()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var hid = new FakeOwnedHid
        {
            ReturnFalseWithReadOperation = true,
        };
        var command = new FakeOwnedCommand();
        var lease = DirectLease(lifetime, hid, command);
        var readClaim = new Switch2ProUsbReadClaim(new object(),
            DeviceGeneration, TransportGeneration, 1);
        var target = new RecordingReadTarget();

        Assert.ThrowsException<
            Switch2ProUsbWindowsReadStartAmbiguousException>(() =>
            lease.TryBeginInputRead(new byte[64], 0, 64, readClaim,
                target));
        hid.CompleteInput(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.AreEqual(0, target.Calls,
            "A late completion cannot escape a quarantined start.");

        Switch2ProUsbOwnedOutputWriteAttempt output =
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 1);
        Assert.AreEqual(Switch2ProUsbOwnedOutputAttemptDisposition.
            LeaseQuarantined, output.Disposition);
        Assert.AreEqual(0, hid.OutputBeginCalls);

        var commandClaim = new Switch2ProUsbStartupCommandClaim(new object(),
            lease, lifetime,
            Switch2ProUsbStartupStep.EnableUsbHidReports, 1);
        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.PossiblyConsumed,
            lease.Execute(commandClaim, InitializationRequest(), 1).Outcome);
        Assert.AreEqual(0, command.WriteBeginCalls);

        var retirement = new Switch2ProUsbStartupRetirementClaim(new object(),
            lease, lifetime, Switch2ProUsbStartupRetirementReason.Explicit, 1);
        Assert.AreEqual(Switch2ProUsbStartupRetirementOutcome.PossiblyReleased,
            lease.Retire(retirement, 1).Outcome);
        Assert.AreEqual(0, command.DisposeCalls);
        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
    }

    [TestMethod]
    public void LateInputCompletionAfterPeerQuarantineIsSuppressed()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var hid = new FakeOwnedHid
        {
            ReturnTrueWithNullOutputOperation = true,
        };
        var lease = DirectLease(lifetime, hid, new FakeOwnedCommand());
        var target = new RecordingReadTarget();
        Assert.IsTrue(lease.TryBeginInputRead(new byte[64], 0, 64,
            new Switch2ProUsbReadClaim(new object(), DeviceGeneration,
                TransportGeneration, 1), target));

        Switch2ProUsbOwnedOutputWriteAttempt output =
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 100);
        Assert.AreEqual(Switch2ProUsbOwnedOutputAttemptDisposition.
            LeaseQuarantined, output.Disposition);

        hid.CompleteInput(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.AreEqual(0, target.Calls,
            "A peer-fenced input callback may drain but not publish input.");
    }

    [TestMethod]
    public void NativeInputStartThrowRetainsAndSuppressesLateCompletion()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var hid = new FakeOwnedHid
        {
            ThrowOnReadBeginWithoutPublishedOperation = true,
        };
        var lease = DirectLease(lifetime, hid, new FakeOwnedCommand());
        var target = new RecordingReadTarget();

        Assert.ThrowsException<
            Switch2ProUsbWindowsReadStartAmbiguousException>(() =>
            lease.TryBeginInputRead(new byte[64], 0, 64,
                new Switch2ProUsbReadClaim(new object(), DeviceGeneration,
                    TransportGeneration, 1), target));
        hid.CompleteInput(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.AreEqual(0, target.Calls);
        Assert.AreEqual(Switch2ProUsbOwnedOutputAttemptDisposition.
            LeaseQuarantined,
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 100).Disposition);
        Assert.AreEqual(0, hid.OutputBeginCalls);
    }

    [TestMethod]
    public void InputAmbiguityAtomicallyFencesConcurrentOutputAndCommandStarts()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var hid = new FakeOwnedHid
        {
            ReturnFalseWithReadOperation = true,
            ReadBeginEntered = new ManualResetEventSlim(false),
            AllowReadBeginReturn = new ManualResetEventSlim(false),
            OutputBeginEntered = new ManualResetEventSlim(false),
        };
        var command = new FakeOwnedCommand();
        var lease = DirectLease(lifetime, hid, command);
        var readClaim = new Switch2ProUsbReadClaim(new object(),
            DeviceGeneration, TransportGeneration, 1);

        Task<bool> input = Task.Run(() =>
        {
            try
            {
                lease.TryBeginInputRead(new byte[64], 0, 64, readClaim,
                    new RecordingReadTarget());
                return false;
            }
            catch (Switch2ProUsbWindowsReadStartAmbiguousException)
            {
                return true;
            }
        });
        Assert.IsTrue(hid.ReadBeginEntered.Wait(1_000));
        Task<Switch2ProUsbOwnedOutputWriteAttempt> output = Task.Run(() =>
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 100));
        Assert.IsFalse(hid.OutputBeginEntered.Wait(50),
            "A peer native start cannot enter during input publication.");

        hid.AllowReadBeginReturn.Set();
        Assert.IsTrue(input.GetAwaiter().GetResult());
        Assert.AreEqual(Switch2ProUsbOwnedOutputAttemptDisposition.
            LeaseQuarantined, output.GetAwaiter().GetResult().Disposition);
        Assert.AreEqual(0, hid.OutputBeginCalls);

        var commandClaim = new Switch2ProUsbStartupCommandClaim(new object(),
            lease, lifetime,
            Switch2ProUsbStartupStep.EnableUsbHidReports, 1);
        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.PossiblyConsumed,
            lease.Execute(commandClaim, InitializationRequest(), 100).Outcome);
        Assert.AreEqual(0, command.WriteBeginCalls);
    }

    [TestMethod]
    public void OutputAmbiguityAtomicallyFencesConcurrentCommandAndInputStarts()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var hid = new FakeOwnedHid
        {
            ReturnTrueWithNullOutputOperation = true,
            OutputBeginEntered = new ManualResetEventSlim(false),
            AllowOutputBeginReturn = new ManualResetEventSlim(false),
        };
        var command = new FakeOwnedCommand
        {
            WriteBeginEntered = new ManualResetEventSlim(false),
        };
        var lease = DirectLease(lifetime, hid, command);

        Task<Switch2ProUsbOwnedOutputWriteAttempt> output = Task.Run(() =>
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 100));
        Assert.IsTrue(hid.OutputBeginEntered.Wait(1_000));
        var commandClaim = new Switch2ProUsbStartupCommandClaim(new object(),
            lease, lifetime,
            Switch2ProUsbStartupStep.EnableUsbHidReports, 1);
        Task<Switch2ProUsbStartupCommandCompletion> commandAttempt = Task.Run(
            () => lease.Execute(commandClaim, InitializationRequest(), 100));
        Assert.IsFalse(command.WriteBeginEntered.Wait(50),
            "Command I/O cannot pass an output start/publication region.");

        hid.AllowOutputBeginReturn.Set();
        Assert.AreEqual(Switch2ProUsbOwnedOutputAttemptDisposition.
            LeaseQuarantined, output.GetAwaiter().GetResult().Disposition);
        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.PossiblyConsumed,
            commandAttempt.GetAwaiter().GetResult().Outcome);
        Assert.AreEqual(0, command.WriteBeginCalls);

        Assert.IsFalse(lease.TryBeginInputRead(new byte[64], 0, 64,
            new Switch2ProUsbReadClaim(new object(), DeviceGeneration,
                TransportGeneration, 1), new RecordingReadTarget()));
        Assert.AreEqual(0, hid.InputBeginCalls);
    }

    [TestMethod]
    public void CommandAmbiguityAtomicallyFencesConcurrentInputAndOutputStarts()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var hid = new FakeOwnedHid
        {
            ReadBeginEntered = new ManualResetEventSlim(false),
        };
        var command = new FakeOwnedCommand
        {
            ReturnTrueWithNullWriteOperation = true,
            WriteBeginEntered = new ManualResetEventSlim(false),
            AllowWriteBeginReturn = new ManualResetEventSlim(false),
        };
        var lease = DirectLease(lifetime, hid, command);
        var commandClaim = new Switch2ProUsbStartupCommandClaim(new object(),
            lease, lifetime,
            Switch2ProUsbStartupStep.EnableUsbHidReports, 1);

        Task<Switch2ProUsbStartupCommandCompletion> commandAttempt = Task.Run(
            () => lease.Execute(commandClaim, InitializationRequest(), 100));
        Assert.IsTrue(command.WriteBeginEntered.Wait(1_000));
        Task<bool> input = Task.Run(() => lease.TryBeginInputRead(
            new byte[64], 0, 64,
            new Switch2ProUsbReadClaim(new object(), DeviceGeneration,
                TransportGeneration, 1), new RecordingReadTarget()));
        Assert.IsFalse(hid.ReadBeginEntered.Wait(50),
            "Input I/O cannot pass a command start/publication region.");

        command.AllowWriteBeginReturn.Set();
        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.PossiblyConsumed,
            commandAttempt.GetAwaiter().GetResult().Outcome);
        Assert.IsFalse(input.GetAwaiter().GetResult());
        Assert.AreEqual(0, hid.InputBeginCalls);

        Switch2ProUsbOwnedOutputWriteAttempt output =
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 100);
        Assert.AreEqual(Switch2ProUsbOwnedOutputAttemptDisposition.
            LeaseQuarantined, output.Disposition);
        Assert.AreEqual(0, hid.OutputBeginCalls);
    }

    [TestMethod]
    public void OutputDeadlineRetainsExactClaimBlocksReplacementAndDrainsLate()
    {
        var hid = new FakeOwnedHid();
        var pending = new FakeIoOperation();
        hid.OutputOperations.Enqueue(pending);
        hid.OutputOperations.Enqueue(FakeIoOperation.Completed(64));
        var lane = new Switch2ProUsbWindowsOwnedOutputLane(hid,
            DeviceGeneration, TransportGeneration, 100);
        byte[] report = NeutralReport();

        Switch2ProUsbOwnedOutputWriteAttempt first = lane.TryWrite(report,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, 1);
        Assert.IsTrue(first.HasValidInvariants());
        Assert.IsTrue(first.RequiresRetirement);

        Switch2ProUsbOwnedOutputWriteAttempt blocked = lane.TryWrite(report,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, 1);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            ProvenRejected, blocked.TransportResult.Outcome);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteFailure.Busy,
            blocked.TransportResult.Failure);
        Assert.AreEqual(1, hid.OutputBeginCalls,
            "No replacement native write may start.");

        Switch2ProUsbOwnedOutputRetirementResult invalidBound =
            lane.TryRetire(first.RetainedClaim, -1);
        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            RetainedForRetry, invalidBound.Outcome);
        Assert.IsFalse(lane.IsQuarantined,
            "A rejected timeout must not contradict the returned state.");

        var foreign = new Switch2ProUsbOwnedOutputOperationClaim(new object(),
            DeviceGeneration, TransportGeneration,
            first.RetainedClaim.Sequence);
        Switch2ProUsbOwnedOutputRetirementResult rejected = lane.TryRetire(
            foreign, 0);
        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            RequestRejected, rejected.Outcome);
        Assert.IsFalse(lane.IsQuarantined);

        Switch2ProUsbOwnedOutputRetirementResult retained = lane.TryRetire(
            first.RetainedClaim, 0);
        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            RetainedForRetry, retained.Outcome);
        Assert.AreEqual(1, pending.CancelCalls);
        pending.Complete(Switch2ProUsbNativeReadStatus.Cancelled, 0);

        Switch2ProUsbOwnedOutputRetirementResult retired = lane.TryRetire(
            first.RetainedClaim, 0);
        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            ExactOperationQuiescent, retired.Outcome);
        Assert.IsTrue(lane.IsExactlyQuiescent);
        Assert.AreEqual(1, pending.ReleaseCalls);

        Switch2ProUsbOwnedOutputWriteAttempt replacement = lane.TryWrite(
            report, Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.Completed,
            replacement.TransportResult.Outcome);
        Assert.AreEqual(2, hid.OutputBeginCalls);
    }

    [TestMethod]
    public void OutputCancellationCompletionRaceReleasesExactlyOnce()
    {
        var operation = new FakeIoOperation
        {
            CompleteInsideCancel = true,
        };
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(operation);
        var lane = new Switch2ProUsbWindowsOwnedOutputLane(hid,
            DeviceGeneration, TransportGeneration, 100);

        Switch2ProUsbOwnedOutputWriteAttempt attempt = lane.TryWrite(
            NeutralReport(), Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);
        Switch2ProUsbOwnedOutputRetirementResult retired = lane.TryRetire(
            attempt.RetainedClaim, 10);

        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            ExactOperationQuiescent, retired.Outcome);
        Assert.AreEqual(1, operation.CancelCalls);
        Assert.AreEqual(1, operation.ReleaseCalls);
        Assert.IsTrue(lane.IsExactlyQuiescent);
    }

    [TestMethod]
    public void OutputCancelFailureRemainsRetryableUntilExactDrain()
    {
        var operation = new FakeIoOperation
        {
            CancelResult = false,
        };
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(operation);
        var lane = new Switch2ProUsbWindowsOwnedOutputLane(hid,
            DeviceGeneration, TransportGeneration, 100);
        Switch2ProUsbOwnedOutputWriteAttempt attempt = lane.TryWrite(
            NeutralReport(), Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);

        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            RetainedForRetry,
            lane.TryRetire(attempt.RetainedClaim, 0).Outcome);
        Assert.AreEqual(1, operation.CancelCalls);

        operation.CancelResult = true;
        operation.CompleteInsideCancel = true;
        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            ExactOperationQuiescent,
            lane.TryRetire(attempt.RetainedClaim, 1).Outcome);
        Assert.AreEqual(2, operation.CancelCalls,
            "A failed cancellation request is not cancellation proof.");
        Assert.AreEqual(1, operation.ReleaseCalls);
    }

    [TestMethod]
    public void OutputCancelFalseCompletionRaceStillDrainsExactlyOnce()
    {
        var operation = new FakeIoOperation
        {
            CancelResult = false,
            CompleteInsideCancel = true,
        };
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(operation);
        var lane = new Switch2ProUsbWindowsOwnedOutputLane(hid,
            DeviceGeneration, TransportGeneration, 100);
        Switch2ProUsbOwnedOutputWriteAttempt attempt = lane.TryWrite(
            NeutralReport(), Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);

        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            ExactOperationQuiescent,
            lane.TryRetire(attempt.RetainedClaim, 1).Outcome);
        Assert.AreEqual(1, operation.CancelCalls);
        Assert.AreEqual(1, operation.ReleaseCalls);
        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            RequestRejected,
            lane.TryRetire(attempt.RetainedClaim, 1).Outcome);
        Assert.AreEqual(1, operation.ReleaseCalls);
    }

    [TestMethod]
    public void ConcurrentInvalidRetirementCannotClaimStableRetention()
    {
        var operation = new FakeIoOperation();
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(operation);
        var lane = new Switch2ProUsbWindowsOwnedOutputLane(hid,
            DeviceGeneration, TransportGeneration, 100);
        Switch2ProUsbOwnedOutputWriteAttempt attempt = lane.TryWrite(
            NeutralReport(), Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);
        operation.BlockWait = true;

        Task<Switch2ProUsbOwnedOutputRetirementResult> draining = Task.Run(
            () => lane.TryRetire(attempt.RetainedClaim, 100));
        Assert.IsTrue(operation.WaitEntered.Wait(1_000));
        Switch2ProUsbOwnedOutputRetirementResult concurrent = lane.TryRetire(
            attempt.RetainedClaim, -1);
        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            RequestRejected, concurrent.Outcome,
            "An in-progress drain is not a stable retained-for-retry fact.");

        operation.Complete(Switch2ProUsbNativeReadStatus.Cancelled, 0);
        operation.AllowWait.Set();
        Assert.IsTrue(draining.Wait(1_000));
        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            ExactOperationQuiescent, draining.Result.Outcome);
        Assert.IsTrue(lane.IsExactlyQuiescent);
    }

    [TestMethod]
    public void OutputDependencyAmbiguityQuarantinesAndNeverReusesLane()
    {
        var operation = new FakeIoOperation
        {
            ThrowOnWait = true,
        };
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(operation);
        var lane = new Switch2ProUsbWindowsOwnedOutputLane(hid,
            DeviceGeneration, TransportGeneration, 100);

        Switch2ProUsbOwnedOutputWriteAttempt attempt = lane.TryWrite(
            NeutralReport(), Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);
        Assert.IsTrue(attempt.HasValidInvariants());
        Assert.IsTrue(attempt.RequiresTerminalAttention);
        Assert.IsFalse(attempt.RequiresRetirement);
        Assert.IsTrue(lane.IsQuarantined);
        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.Quarantined,
            lane.TryRetire(attempt.RetainedClaim, 1).Outcome);

        Switch2ProUsbOwnedOutputWriteAttempt blocked = lane.TryWrite(
            NeutralReport(), Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteFailure.
            TransportEnded, blocked.TransportResult.Failure);
        Assert.IsTrue(blocked.HasValidInvariants());
        Assert.IsTrue(blocked.RequiresTerminalAttention);
        Assert.AreEqual(1, hid.OutputBeginCalls);
    }

    [TestMethod]
    public void OutputThrowWithNullQuarantinesWithoutInventingOperationClaim()
    {
        var hid = new FakeOwnedHid
        {
            ThrowOnOutputBegin = true,
        };
        var lane = new Switch2ProUsbWindowsOwnedOutputLane(hid,
            DeviceGeneration, TransportGeneration, 100);

        Switch2ProUsbOwnedOutputWriteAttempt attempt = lane.TryWrite(
            NeutralReport(), Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);

        Assert.IsTrue(attempt.HasValidInvariants());
        Assert.IsTrue(attempt.RequiresTerminalAttention);
        Assert.IsFalse(attempt.RequiresRetirement);
        Assert.IsFalse(attempt.RetainedClaim.IsValid);
        Assert.IsTrue(lane.IsQuarantined);
        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            RequestRejected,
            lane.TryRetire(attempt.RetainedClaim, 0).Outcome);
        Assert.IsFalse(lane.TrySealForDisposal());
    }

    [TestMethod]
    public void OutputTrueWithNullQuarantinesWithoutInventingOperationClaim()
    {
        var hid = new FakeOwnedHid
        {
            ReturnTrueWithNullOutputOperation = true,
        };
        var lane = new Switch2ProUsbWindowsOwnedOutputLane(hid,
            DeviceGeneration, TransportGeneration, 100);

        Switch2ProUsbOwnedOutputWriteAttempt attempt = lane.TryWrite(
            NeutralReport(), Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);

        Assert.IsTrue(attempt.HasValidInvariants());
        Assert.IsTrue(attempt.RequiresTerminalAttention);
        Assert.IsFalse(attempt.RequiresRetirement);
        Assert.IsFalse(attempt.RetainedClaim.IsValid);
        Assert.IsTrue(lane.IsQuarantined);
    }

    [TestMethod]
    public void OutputFalseWithOperationQuarantinesExactOwnedOperation()
    {
        var operation = new FakeIoOperation();
        var hid = new FakeOwnedHid
        {
            ReturnFalseWithOutputOperation = true,
        };
        hid.OutputOperations.Enqueue(operation);
        var lane = new Switch2ProUsbWindowsOwnedOutputLane(hid,
            DeviceGeneration, TransportGeneration, 100);

        Switch2ProUsbOwnedOutputWriteAttempt attempt = lane.TryWrite(
            NeutralReport(), Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);

        Assert.IsTrue(attempt.HasValidInvariants());
        Assert.IsTrue(attempt.RequiresTerminalAttention);
        Assert.IsFalse(attempt.RequiresRetirement);
        Assert.IsTrue(attempt.RetainedClaim.IsValid);
        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.Quarantined,
            lane.TryRetire(attempt.RetainedClaim, 0).Outcome);
        Assert.AreEqual(0, operation.CancelCalls,
            "A contradictory dependency result is permanently fenced.");
    }

    [TestMethod]
    public void OutputCleanFalseWithNullRejectsAndKeepsLaneReusable()
    {
        var hid = new FakeOwnedHid
        {
            ReturnFalseWithNullOutputOperation = true,
        };
        var lane = new Switch2ProUsbWindowsOwnedOutputLane(hid,
            DeviceGeneration, TransportGeneration, 100);

        Switch2ProUsbOwnedOutputWriteAttempt rejected = lane.TryWrite(
            NeutralReport(), Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);
        Assert.IsTrue(rejected.HasValidInvariants());
        Assert.AreEqual(Switch2ProUsbOwnedOutputAttemptDisposition.
            NoOperationOwnedByAttempt, rejected.Disposition);
        Assert.IsFalse(lane.IsQuarantined);

        hid.ReturnFalseWithNullOutputOperation = false;
        Switch2ProUsbOwnedOutputWriteAttempt replacement = lane.TryWrite(
            NeutralReport(), Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.Completed,
            replacement.TransportResult.Outcome);
        Assert.AreEqual(2, hid.OutputBeginCalls);
    }

    [TestMethod]
    public void QuiescentNativeWriteFailureIsDeviceUncertainButNeedsNoDrain()
    {
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(FakeIoOperation.Completed(0,
            Switch2ProUsbNativeReadStatus.Failed));
        var lane = new Switch2ProUsbWindowsOwnedOutputLane(hid,
            DeviceGeneration, TransportGeneration, 100);

        Switch2ProUsbOwnedOutputWriteAttempt attempt = lane.TryWrite(
            NeutralReport(), Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration, 1);

        Assert.IsTrue(attempt.HasValidInvariants());
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, attempt.TransportResult.Outcome);
        Assert.IsFalse(attempt.RequiresRetirement);
        Assert.IsTrue(lane.IsExactlyQuiescent);
    }

    [TestMethod]
    public void FeatureCommandsUseExactBulkPairAndCodecResponseProof()
    {
        var cases = new[]
        {
            (Switch2UsbFeatureStep.SetFeatureMask,
                Switch2ProUsbStartupStep.SetFeatureMask, (byte)0x02),
            (Switch2UsbFeatureStep.EnableFeatures,
                Switch2ProUsbStartupStep.EnableFeatures, (byte)0x04),
        };
        ulong sequence = 1;
        foreach (var item in cases)
        {
            Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
            var command = new FakeOwnedCommand();
            command.WriteOperations.Enqueue(FakeIoOperation.Completed(12));
            command.ReadOperations.Enqueue(FakeIoOperation.Completed(12));
            command.Responses.Enqueue(new byte[]
            {
                0x0C, 0x01, 0x00, item.Item3, 0x00, 0xF8, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
            });
            var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
            byte[] request = new byte[
                Switch2UsbCommandCodec.FeatureRequestLength];
            Assert.IsTrue(Switch2UsbCommandCodec.TryWriteFeatureRequest(
                item.Item1,
                Switch2UsbFeatureMask.ButtonsSticksImuAndRumble, request,
                out _));
            var claim = new Switch2ProUsbStartupCommandClaim(new object(),
                lease, lifetime, item.Item2, sequence++);

            Switch2ProUsbStartupCommandCompletion result = lease.Execute(
                claim, request, 100);

            Assert.AreEqual(
                Switch2ProUsbStartupCommandOutcome.ExactResponseCompleted,
                result.Outcome);
            Assert.AreEqual(Switch2ProUsbStartupResponseProofKind.
                FeatureResponseValidatedByCodec, result.ResponseProof);
            Assert.AreEqual(1, command.WriteBeginCalls);
            Assert.AreEqual(1, command.ReadBeginCalls);
        }
    }

    [TestMethod]
    public void CalibrationCommandUsesSameOwnedBulkPairAndReturnsExactPayload()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var command = new FakeOwnedCommand();
        byte[] payload = Convert.FromHexString("34127856349A783412");
        byte[] response = Convert.FromHexString(
            "020100041078000009000000A8300100").Concat(payload).ToArray();
        command.WriteOperations.Enqueue(FakeIoOperation.Completed(
            Switch2UsbCommandCodec.CalibrationReadRequestLength));
        command.ReadOperations.Enqueue(FakeIoOperation.Completed(
            response.Length));
        command.Responses.Enqueue(response);
        var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
        Assert.IsInstanceOfType<ISwitch2ProUsbCalibrationCommandLease>(lease);
        byte[] request = new byte[
            Switch2UsbCommandCodec.CalibrationReadRequestLength];
        Assert.IsTrue(Switch2UsbCommandCodec.TryWriteCalibrationReadRequest(
            Switch2UsbCalibrationRead.FactoryPrimary, request, out _));
        var claim = new Switch2ProUsbStartupCommandClaim(new object(), lease,
            lifetime,
            Switch2ProUsbStartupStep.ReadFactoryPrimaryCalibration, 1);

        Switch2ProUsbStartupCommandCompletion result = lease.Execute(claim,
            request, 100);

        Assert.AreEqual(
            Switch2ProUsbStartupCommandOutcome.ExactResponseCompleted,
            result.Outcome);
        Assert.AreEqual(Switch2ProUsbStartupResponseProofKind.
            CalibrationReadResponseValidatedByCodec, result.ResponseProof);
        CollectionAssert.AreEqual(payload, result.ResponsePayload.ToArray());
        Assert.AreEqual(1, command.WriteBeginCalls);
        Assert.AreEqual(1, command.ReadBeginCalls);
    }

    [TestMethod]
    public void MalformedFeatureResponseRequiresCommandRetirement()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var command = new FakeOwnedCommand();
        command.WriteOperations.Enqueue(FakeIoOperation.Completed(12));
        command.ReadOperations.Enqueue(FakeIoOperation.Completed(12));
        command.Responses.Enqueue(new byte[]
        {
            0x0C, 0x01, 0x00, 0x02, 0x00, 0xF8, 0x00, 0x00,
            0x27, 0x00, 0x00, 0x00,
        });
        var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
        byte[] request = new byte[
            Switch2UsbCommandCodec.FeatureRequestLength];
        Assert.IsTrue(Switch2UsbCommandCodec.TryWriteFeatureRequest(
            Switch2UsbFeatureStep.SetFeatureMask,
            Switch2UsbFeatureMask.ButtonsSticksImuAndRumble, request,
            out _));
        var claim = new Switch2ProUsbStartupCommandClaim(new object(), lease,
            lifetime, Switch2ProUsbStartupStep.SetFeatureMask, 1);

        Switch2ProUsbStartupCommandCompletion result = lease.Execute(claim,
            request, 100);

        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.PossiblyConsumed,
            result.Outcome);
        Assert.AreEqual(Switch2ProUsbStartupResponseProofKind.Invalid,
            result.ResponseProof);
        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.PossiblyConsumed,
            lease.Execute(claim, request, 100).Outcome,
            "A malformed response proves the request may have reached the controller and must fence replay until retirement.");
    }

    [TestMethod]
    public void InitializationUsesExactBulkPairAndCodecResponseProof()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var command = new FakeOwnedCommand();
        command.WriteOperations.Enqueue(FakeIoOperation.Completed(12));
        command.ReadOperations.Enqueue(FakeIoOperation.Completed(12));
        command.Responses.Enqueue(new byte[]
        {
            0x03, 0x01, 0x00, 0x03, 0x00, 0xF8, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
        });
        var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
        byte[] request = new byte[
            Switch2UsbCommandCodec.InitializationRequestLength];
        Assert.IsTrue(Switch2UsbCommandCodec.TryWriteInitializationRequest(
            Switch2UsbInitializationStep.EnableUsbHidReports, request,
            out _));
        var claim = new Switch2ProUsbStartupCommandClaim(new object(), lease,
            lifetime, Switch2ProUsbStartupStep.EnableUsbHidReports, 1);

        Switch2ProUsbStartupCommandCompletion result = lease.Execute(claim,
            request, 100);

        Assert.AreEqual(
            Switch2ProUsbStartupCommandOutcome.ExactResponseCompleted,
            result.Outcome);
        Assert.AreEqual(Switch2ProUsbStartupResponseProofKind.
            InitializationResponseValidatedByCodec, result.ResponseProof);
        Assert.AreEqual(1, command.WriteBeginCalls);
        Assert.AreEqual(1, command.ReadBeginCalls);
    }

    [TestMethod]
    public void RuntimePlayerLedUsesExactRequestedBulkTupleAndResponseProof()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var command = new FakeOwnedCommand();
        command.WriteOperations.Enqueue(FakeIoOperation.Completed(8));
        command.ReadOperations.Enqueue(FakeIoOperation.Completed(8));
        command.Responses.Enqueue(new byte[]
        {
            0x09, 0x01, 0x00, 0x03, 0x00, 0xF8, 0x00, 0x00,
        });
        var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
        byte[] request = new byte[Switch2UsbCommandCodec.RequestLength];
        Assert.IsTrue(Switch2UsbCommandCodec.TryWritePlayerLedRequest(
            Switch2PlayerLedCommand.Player3Only, request, out _));
        var claim = new Switch2ProUsbStartupCommandClaim(new object(), lease,
            lifetime, Switch2ProUsbStartupStep.SetPlayerLed, 1);

        Switch2ProUsbStartupCommandCompletion result = lease.Execute(claim,
            request, 100);

        Assert.AreEqual(
            Switch2ProUsbStartupCommandOutcome.ExactResponseCompleted,
            result.Outcome);
        Assert.AreEqual(Switch2ProUsbStartupResponseProofKind.
            PlayerLedResponseValidatedByCodec, result.ResponseProof);
        Assert.AreEqual(1, command.WriteBeginCalls);
        Assert.AreEqual(1, command.ReadBeginCalls);
    }

    [TestMethod]
    public void CommandAndRetirementClaimsAuthenticateExactLeaseAndLifetime()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate("target"));
        Switch2PhysicalInputLifetime foreignLifetime = Lifetime(Candidate(
            "foreign", new Guid("C7CD31A6-E93C-4411-AE85-67EE6C38D78F")));
        var command = new FakeOwnedCommand();
        var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
        var foreignLease = DirectLease(foreignLifetime, new FakeOwnedHid(),
            new FakeOwnedCommand());
        byte[] request = InitializationRequest();

        var foreignLeaseClaim = new Switch2ProUsbStartupCommandClaim(
            new object(), foreignLease, foreignLifetime,
            Switch2ProUsbStartupStep.EnableUsbHidReports, 1);
        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.ProvenNotConsumed,
            lease.Execute(foreignLeaseClaim, request, 100).Outcome);

        var foreignRegistrationClaim = new Switch2ProUsbStartupCommandClaim(
            new object(), lease, foreignLifetime,
            Switch2ProUsbStartupStep.EnableUsbHidReports, 1);
        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.ProvenNotConsumed,
            lease.Execute(foreignRegistrationClaim, request, 100).Outcome);
        Assert.AreEqual(0, command.WriteBeginCalls);

        command.WriteOperations.Enqueue(FakeIoOperation.Completed(12));
        command.ReadOperations.Enqueue(FakeIoOperation.Completed(12));
        command.Responses.Enqueue(InitializationResponse());
        var exactClaim = new Switch2ProUsbStartupCommandClaim(new object(),
            lease, lifetime,
            Switch2ProUsbStartupStep.EnableUsbHidReports, 1);
        Switch2ProUsbStartupCommandClaim copiedClaim = exactClaim;
        Assert.AreEqual(
            Switch2ProUsbStartupCommandOutcome.ExactResponseCompleted,
            lease.Execute(copiedClaim, request, 100).Outcome);
        Assert.AreEqual(1, command.WriteBeginCalls);

        var foreignRetirement = new Switch2ProUsbStartupRetirementClaim(
            new object(), foreignLease, foreignLifetime,
            Switch2ProUsbStartupRetirementReason.Explicit, 1);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ProvenNotReleased,
            lease.Retire(foreignRetirement, 0).Outcome);
        var foreignRegistrationRetirement =
            new Switch2ProUsbStartupRetirementClaim(new object(), lease,
                foreignLifetime,
                Switch2ProUsbStartupRetirementReason.Explicit, 1);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ProvenNotReleased,
            lease.Retire(foreignRegistrationRetirement, 0).Outcome);
        Assert.AreEqual(0, command.DisposeCalls);

        var exactRetirement = new Switch2ProUsbStartupRetirementClaim(
            new object(), lease, lifetime,
            Switch2ProUsbStartupRetirementReason.Explicit, 1);
        Switch2ProUsbStartupRetirementClaim copiedRetirement =
            exactRetirement;
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(copiedRetirement, 0).Outcome);
        Assert.AreEqual(1, command.DisposeCalls);
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        lease.DisposeQuiesced();
    }

    [TestMethod]
    public void RetryableCommandReleaseRetainsExactRetirementCapability()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var command = new FakeOwnedCommand
        {
            RetryableDisposeFailuresRemaining = 1,
        };
        var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
        var retirement = new Switch2ProUsbStartupRetirementClaim(new object(),
            lease, lifetime, Switch2ProUsbStartupRetirementReason.Explicit, 1);

        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ProvenNotReleased,
            lease.Retire(retirement, 0).Outcome);
        Assert.AreEqual(1, command.DisposeCalls);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirement, 0).Outcome);
        Assert.AreEqual(2, command.DisposeCalls);
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        lease.DisposeQuiesced();
    }

    [TestMethod]
    public void AmbiguousCommandReleaseQuarantinesWithoutSecondDisposeAttempt()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var command = new FakeOwnedCommand
        {
            AmbiguousDisposeFailuresRemaining = 1,
        };
        var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
        var retirement = new Switch2ProUsbStartupRetirementClaim(new object(),
            lease, lifetime, Switch2ProUsbStartupRetirementReason.Explicit, 1);

        Assert.AreEqual(Switch2ProUsbStartupRetirementOutcome.PossiblyReleased,
            lease.Retire(retirement, 0).Outcome);
        Assert.AreEqual(Switch2ProUsbStartupRetirementOutcome.PossiblyReleased,
            lease.Retire(retirement, 0).Outcome);
        Assert.AreEqual(1, command.DisposeCalls,
            "An outcome-ambiguous release must never be repeated.");
        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
    }

    [TestMethod]
    public void PossiblyConsumedReadStartFailureFencesEveryLaterExecute()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var command = new FakeOwnedCommand
        {
            RejectReadStart = true,
        };
        command.WriteOperations.Enqueue(FakeIoOperation.Completed(12));
        var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
        byte[] request = InitializationRequest();
        var first = new Switch2ProUsbStartupCommandClaim(new object(), lease,
            lifetime, Switch2ProUsbStartupStep.EnableUsbHidReports, 1);

        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.PossiblyConsumed,
            lease.Execute(first, request, 100).Outcome);

        var replacement = new Switch2ProUsbStartupCommandClaim(new object(),
            lease, lifetime,
            Switch2ProUsbStartupStep.EnableUsbHidReports, 2);
        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.PossiblyConsumed,
            lease.Execute(replacement, request, 100).Outcome);
        byte[] featureRequest = FeatureRequest();
        var feature = new Switch2ProUsbStartupCommandClaim(new object(), lease,
            lifetime, Switch2ProUsbStartupStep.SetFeatureMask, 3);
        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.PossiblyConsumed,
            lease.Execute(feature, featureRequest, 100).Outcome,
            "No-op feature commands must not bypass an uncertain lane fence.");
        Assert.AreEqual(1, command.WriteBeginCalls);
        Assert.AreEqual(1, command.ReadBeginCalls);

        var retirement = new Switch2ProUsbStartupRetirementClaim(new object(),
            lease, lifetime, Switch2ProUsbStartupRetirementReason.Explicit,
            1);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirement, 0).Outcome);
        Assert.AreEqual(1, command.DisposeCalls);
    }

    [TestMethod]
    public void MalformedInitializationResponseFencesReplacementUntilRetire()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var command = new FakeOwnedCommand();
        command.WriteOperations.Enqueue(FakeIoOperation.Completed(12));
        command.ReadOperations.Enqueue(FakeIoOperation.Completed(12));
        command.Responses.Enqueue(new byte[12]);
        var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
        byte[] request = InitializationRequest();
        var first = new Switch2ProUsbStartupCommandClaim(new object(), lease,
            lifetime, Switch2ProUsbStartupStep.EnableUsbHidReports, 1);

        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.PossiblyConsumed,
            lease.Execute(first, request, 100).Outcome);
        var replacement = new Switch2ProUsbStartupCommandClaim(new object(),
            lease, lifetime,
            Switch2ProUsbStartupStep.EnableUsbHidReports, 2);
        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.PossiblyConsumed,
            lease.Execute(replacement, request, 100).Outcome);
        Assert.AreEqual(1, command.WriteBeginCalls);

        var retirement = new Switch2ProUsbStartupRetirementClaim(new object(),
            lease, lifetime, Switch2ProUsbStartupRetirementReason.Explicit,
            1);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirement, 0).Outcome);
    }

    [TestMethod]
    public void StartupDeadlineRetainsOperationAndExactRetirementCanRetry()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var pending = new FakeIoOperation();
        var command = new FakeOwnedCommand();
        command.WriteOperations.Enqueue(pending);
        var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
        byte[] request = new byte[
            Switch2UsbCommandCodec.InitializationRequestLength];
        Assert.IsTrue(Switch2UsbCommandCodec.TryWriteInitializationRequest(
            Switch2UsbInitializationStep.EnableUsbHidReports, request,
            out _));
        var commandClaim = new Switch2ProUsbStartupCommandClaim(new object(),
            lease, lifetime,
            Switch2ProUsbStartupStep.EnableUsbHidReports, 1);
        var retirementClaim = new Switch2ProUsbStartupRetirementClaim(
            new object(), lease, lifetime,
            Switch2ProUsbStartupRetirementReason.Explicit, 1);

        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.TimedOut,
            lease.Execute(commandClaim, request, 1).Outcome);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ProvenNotReleased,
            lease.Retire(retirementClaim, 0).Outcome);
        Assert.AreEqual(1, pending.CancelCalls);
        Assert.AreEqual(0, command.DisposeCalls);

        pending.Complete(Switch2ProUsbNativeReadStatus.Cancelled, 0);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirementClaim, 0).Outcome);
        Assert.AreEqual(1, pending.ReleaseCalls);
        Assert.AreEqual(1, command.DisposeCalls);
    }

    [TestMethod]
    public void StartupCancelFailureReissuesExactCancellationOnRetry()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var pending = new FakeIoOperation
        {
            CancelResult = false,
        };
        var command = new FakeOwnedCommand();
        command.WriteOperations.Enqueue(pending);
        var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
        byte[] request = InitializationRequest();
        var commandClaim = new Switch2ProUsbStartupCommandClaim(new object(),
            lease, lifetime,
            Switch2ProUsbStartupStep.EnableUsbHidReports, 1);
        var retirementClaim = new Switch2ProUsbStartupRetirementClaim(
            new object(), lease, lifetime,
            Switch2ProUsbStartupRetirementReason.Explicit, 1);

        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.TimedOut,
            lease.Execute(commandClaim, request, 1).Outcome);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ProvenNotReleased,
            lease.Retire(retirementClaim, 0).Outcome);
        Assert.AreEqual(1, pending.CancelCalls);

        pending.CancelResult = true;
        pending.CompleteInsideCancel = true;
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirementClaim, 1).Outcome);
        Assert.AreEqual(2, pending.CancelCalls);
        Assert.AreEqual(1, pending.ReleaseCalls);
        Assert.AreEqual(1, command.DisposeCalls);
    }

    [TestMethod]
    public void StartupCancelFalseCompletionRaceStillRetiresExactlyOnce()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var pending = new FakeIoOperation
        {
            CancelResult = false,
            CompleteInsideCancel = true,
        };
        var command = new FakeOwnedCommand();
        command.WriteOperations.Enqueue(pending);
        var lease = DirectLease(lifetime, new FakeOwnedHid(), command);
        var commandClaim = new Switch2ProUsbStartupCommandClaim(new object(),
            lease, lifetime,
            Switch2ProUsbStartupStep.EnableUsbHidReports, 1);
        var retirementClaim = new Switch2ProUsbStartupRetirementClaim(
            new object(), lease, lifetime,
            Switch2ProUsbStartupRetirementReason.Explicit, 1);

        Assert.AreEqual(Switch2ProUsbStartupCommandOutcome.TimedOut,
            lease.Execute(commandClaim, InitializationRequest(), 1).Outcome);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirementClaim, 1).Outcome);
        Assert.AreEqual(1, pending.CancelCalls);
        Assert.AreEqual(1, pending.ReleaseCalls);
        Assert.AreEqual(1, command.DisposeCalls);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirementClaim, 1).Outcome);
        Assert.AreEqual(1, pending.ReleaseCalls);
    }

    [TestMethod]
    public void WholeDisposeRequiresCommandInputAndOutputExactQuiescence()
    {
        Switch2PhysicalInputLifetime lifetime = Lifetime(Candidate());
        var pending = new FakeIoOperation();
        var hid = new FakeOwnedHid();
        hid.OutputOperations.Enqueue(pending);
        var command = new FakeOwnedCommand();
        var lease = DirectLease(lifetime, hid, command);
        Switch2ProUsbOwnedOutputWriteAttempt attempt =
            lease.TryWriteReportBounded(NeutralReport(),
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, 1);

        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced,
            "Open command facet must block whole disposal.");
        var retirementClaim = new Switch2ProUsbStartupRetirementClaim(
            new object(), lease, lifetime,
            Switch2ProUsbStartupRetirementReason.Explicit, 1);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirementClaim, 0).Outcome);
        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced,
            "Retained output must block MI_00 disposal.");

        pending.Complete(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.AreEqual(Switch2ProUsbOwnedOutputRetirementOutcome.
            ExactOperationQuiescent,
            lease.TryRetireOutputOperation(attempt.RetainedClaim, 0).Outcome);
        lease.DisposeQuiesced();
        Assert.AreEqual(1, hid.DisposeCalls);
        Assert.AreEqual(1, command.DisposeCalls);
    }

    [TestMethod]
    public void OwnedOpenPolicyMatchesHidSharingAndKeepsCommandWriterExact()
    {
        Assert.AreEqual(0xC0000000u,
            Switch2ProUsbWindowsOpenPolicy.OwnedHidDesiredAccess);
        Assert.AreEqual(0x00000003u,
            Switch2ProUsbWindowsOpenPolicy.OwnedHidShareMode);
        Assert.AreEqual(0xC0000000u,
            Switch2ProUsbWindowsOpenPolicy.OwnedCommandDesiredAccess);
        Assert.AreEqual(0x00000001u,
            Switch2ProUsbWindowsOpenPolicy.OwnedCommandShareMode);
        Assert.AreEqual(0x40000000u,
            Switch2ProUsbWindowsOpenPolicy.OverlappedFlag);
    }

    [TestMethod]
    public void IocpPublicationRequiresExactPointerAndNativeStorageRelease()
    {
        Assert.IsTrue(Switch2ProUsbWindowsOwnedCompletionPublication.
            CanPublishQuiescence(exactPointerTransition: true,
                nativeStorageReleased: true));
        Assert.IsFalse(Switch2ProUsbWindowsOwnedCompletionPublication.
            CanPublishQuiescence(exactPointerTransition: false,
                nativeStorageReleased: true),
            "A stale or duplicate callback cannot wake the current attempt.");
        Assert.IsFalse(Switch2ProUsbWindowsOwnedCompletionPublication.
            CanPublishQuiescence(exactPointerTransition: true,
                nativeStorageReleased: false),
            "Failed overlapped release must permanently retain the fence.");
        Assert.IsFalse(Switch2ProUsbWindowsOwnedCompletionPublication.
            CanPublishQuiescence(exactPointerTransition: false,
                nativeStorageReleased: false));
    }

    [TestMethod]
    public void QuiescenceDeadlineDeductsElapsedTimeAndNeverExpandsBudget()
    {
        Assert.AreEqual(25, Switch2ProUsbWindowsDeadline.RemainingAt(
            deadline: 1_025, originalTimeout: 100, currentTick: 1_000));
        Assert.AreEqual(100, Switch2ProUsbWindowsDeadline.RemainingAt(
            deadline: 1_500, originalTimeout: 100, currentTick: 1_000),
            "Clock arithmetic must not expand the caller's wait budget.");
        Assert.AreEqual(0, Switch2ProUsbWindowsDeadline.RemainingAt(
            deadline: 1_000, originalTimeout: 100, currentTick: 1_000));
        Assert.AreEqual(0, Switch2ProUsbWindowsDeadline.RemainingAt(
            deadline: 900, originalTimeout: 100, currentTick: 1_000));
        Assert.AreEqual(0, Switch2ProUsbWindowsDeadline.RemainingAt(
            deadline: 1_100, originalTimeout: 0, currentTick: 1_000));
    }

    [TestMethod]
    public void CommandAcquisitionThrowRemainsAmbiguousAfterObservedCleanup()
    {
        var acquisition = new InvalidOperationException(
            "Synthetic command acquisition throw.");
        Switch2ProUsbWindowsCleanupAmbiguousException exactCleanup =
            Switch2ProUsbWindowsNativePlatform.
                CreateCommandAcquisitionAmbiguity(acquisition,
                    cleanupFailure: null);
        Assert.AreSame(acquisition, exactCleanup.InnerException);
        Assert.IsNull(exactCleanup.RetainedOwner);

        var retainedOwner = new object();
        var cleanup = new Switch2ProUsbWindowsCleanupAmbiguousException(
            "Synthetic cleanup ambiguity.", retainedOwner);
        Switch2ProUsbWindowsCleanupAmbiguousException ambiguousCleanup =
            Switch2ProUsbWindowsNativePlatform.
                CreateCommandAcquisitionAmbiguity(acquisition, cleanup);
        Assert.AreSame(acquisition, ambiguousCleanup.InnerException);
        Assert.AreSame(retainedOwner, ambiguousCleanup.RetainedOwner);
    }

    [TestMethod]
    public void MultipleCleanupAmbiguitiesRetainBothExactNativeOwners()
    {
        var firstOwner = new object();
        var secondOwner = new object();
        var first = new Switch2ProUsbWindowsCleanupAmbiguousException(
            "Synthetic first cleanup ambiguity.", firstOwner);
        var second = new Switch2ProUsbWindowsCleanupAmbiguousException(
            "Synthetic second cleanup ambiguity.", secondOwner);

        Switch2ProUsbWindowsCleanupAmbiguousException combined =
            Switch2ProUsbWindowsNativePlatform.CombineCleanupAmbiguities(
                first, second);

        var owner = (Switch2ProUsbWindowsAcquisitionQuarantineOwner)
            combined.RetainedOwner;
        Assert.IsTrue(owner.Retains(firstOwner));
        Assert.IsTrue(owner.Retains(secondOwner));
        var aggregate = (AggregateException)combined.InnerException;
        Assert.AreEqual(2, aggregate.InnerExceptions.Count);
        Assert.AreSame(first, aggregate.InnerExceptions[0]);
        Assert.AreSame(second, aggregate.InnerExceptions[1]);
    }

    [TestMethod]
    public void WinUsbReleaseFailureRetainsExactHandleForSuccessfulRetry()
    {
        int calls = 0;
        var handle = new Switch2ProUsbWindowsNativePlatform.SafeWinUsbHandle(
            new IntPtr(0x1234), _ => Interlocked.Increment(ref calls) > 1);

        Assert.IsFalse(handle.TryDisposeQuiesced());
        Assert.IsFalse(handle.IsClosed);
        Assert.AreEqual(new IntPtr(0x1234), handle.DangerousGetHandle());
        Assert.IsTrue(handle.TryDisposeQuiesced());
        Assert.IsTrue(handle.IsClosed);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void WinUsbReleaseThrowPermanentlyFencesWithoutDoubleFree()
    {
        int calls = 0;
        var handle = new Switch2ProUsbWindowsNativePlatform.SafeWinUsbHandle(
            new IntPtr(0x2345), _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException(
                        "Synthetic WinUSB release exception.");
                }
                return true;
            });

        Assert.IsFalse(handle.TryDisposeQuiesced());
        Assert.IsFalse(handle.IsClosed);
        Assert.AreEqual(new IntPtr(0x2345), handle.DangerousGetHandle());
        Assert.IsFalse(handle.TryDisposeQuiesced());
        Assert.IsFalse(handle.IsClosed);
        Assert.AreEqual(1, calls,
            "A thrown native release has no safe retry fact.");
        handle.SetHandleAsInvalid();
        handle.Dispose();
    }

    [TestMethod]
    public void FileReleaseFailureRetainsExactHandleForSuccessfulRetry()
    {
        int calls = 0;
        using var handle = new SafeFileHandle(new IntPtr(0x5678),
            ownsHandle: false);
        bool Close(IntPtr exact)
        {
            Assert.AreEqual(new IntPtr(0x5678), exact);
            return Interlocked.Increment(ref calls) > 1;
        }

        Assert.IsFalse(Switch2ProUsbWindowsExactHandleRelease.
            TryReleaseFileQuiesced(handle, Close));
        Assert.IsFalse(handle.IsClosed);
        Assert.IsTrue(Switch2ProUsbWindowsExactHandleRelease.
            TryReleaseFileQuiesced(handle, Close));
        Assert.IsTrue(handle.IsClosed);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void FileReleaseThrowPermanentlyFencesWithoutDoubleClose()
    {
        int calls = 0;
        using var handle = new SafeFileHandle(new IntPtr(0x6789),
            ownsHandle: false);
        bool Close(IntPtr exact)
        {
            Assert.AreEqual(new IntPtr(0x6789), exact);
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException(
                    "Synthetic CloseHandle exception.");
            }
            return true;
        }

        Assert.IsFalse(Switch2ProUsbWindowsExactHandleRelease.
            TryReleaseFileQuiesced(handle, Close));
        Assert.IsTrue(handle.IsClosed,
            "A thrown close must suppress SafeFileHandle finalization.");
        Assert.IsTrue(Switch2ProUsbWindowsExactHandleRelease.
            IsFileNativeReleaseSuppressed(handle));
        Assert.IsFalse(Switch2ProUsbWindowsExactHandleRelease.
            TryReleaseFileQuiesced(handle, Close));
        Assert.IsTrue(handle.IsClosed);
        Assert.AreEqual(1, calls,
            "A thrown CloseHandle outcome must not close a recycled value.");
    }

    [TestMethod]
    public void ReadStartOutcomeNeverTreatsPriorBusySubmissionAsNewRejection()
    {
        Assert.AreEqual(0,
            (int)Switch2ProUsbWindowsReadStartOutcome.RejectedNoSubmission);
        Assert.AreNotEqual(Switch2ProUsbWindowsReadStartOutcome.Started,
            Switch2ProUsbWindowsReadStartOutcome.RejectedNoSubmission,
            "A completed-but-unretired prior submission remains owned; a " +
            "second begin is only a rejected new attempt.");
        Assert.AreNotEqual(
            Switch2ProUsbWindowsReadStartOutcome.RejectedSubmissionQuiescent,
            Switch2ProUsbWindowsReadStartOutcome.RejectedNoSubmission,
            "Only a newly minted native rejection can be cleaned by TryStart.");
        Assert.AreNotEqual(
            Switch2ProUsbWindowsReadStartOutcome.RejectedSubmissionFenced,
            Switch2ProUsbWindowsReadStartOutcome.RejectedNoSubmission);
    }

    private static Switch2ProUsbWindowsOwnedCompositeLease DirectLease(
        in Switch2PhysicalInputLifetime lifetime, FakeOwnedHid hid,
        FakeOwnedCommand command) => new(lifetime.Registration, lifetime,
        hid, command, reservation: null, maximumOperationMilliseconds: 100);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Switch2ProUsbWindowsOwnedCompositeLease>
        OpenOwnedAndDropAfterTerminalFailure(
            Switch2ProUsbWindowsReservationRegistry reservations,
            Switch2ProUsbWindowsCandidate candidate,
            in Switch2PhysicalInputLifetime lifetime)
    {
        var adapter = new Switch2ProUsbWindowsOwnedCompositeAdapter(
            new FakeOwnedPlatform(new FakeOwnedHid(),
                new FakeOwnedCommand(), candidate, candidate),
            reservations);
        Assert.IsTrue(adapter.TryOpenOwnedComposite(lifetime.Registration,
            lifetime, out ISwitch2ProUsbOwnedCompositeLease opened));
        var lease = (Switch2ProUsbWindowsOwnedCompositeLease)opened;
        var retirement = new Switch2ProUsbStartupRetirementClaim(new object(),
            lease, lifetime, Switch2ProUsbStartupRetirementReason.Explicit,
            1);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirement, 0).Outcome);
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
        return new WeakReference<Switch2ProUsbWindowsOwnedCompositeLease>(
            lease);
    }

    private static void RetireCommandAndDispose(
        Switch2ProUsbWindowsOwnedCompositeLease lease,
        in Switch2PhysicalInputLifetime lifetime)
    {
        var retirement = new Switch2ProUsbStartupRetirementClaim(new object(),
            lease, lifetime, Switch2ProUsbStartupRetirementReason.Explicit,
            1);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased,
            lease.Retire(retirement, 0).Outcome);
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        lease.DisposeQuiesced();
    }

    private static byte[] NeutralReport()
    {
        var report = new byte[Switch2UsbHdRumbleCodec.ReportLength];
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryEncodeProController(0,
            default, default, report));
        return report;
    }

    private static byte[] InitializationRequest()
    {
        var request = new byte[
            Switch2UsbCommandCodec.InitializationRequestLength];
        Assert.IsTrue(Switch2UsbCommandCodec.TryWriteInitializationRequest(
            Switch2UsbInitializationStep.EnableUsbHidReports, request,
            out _));
        return request;
    }

    private static byte[] InitializationResponse() => new byte[]
    {
        0x03, 0x01, 0x00, 0x03, 0x00, 0xF8, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00,
    };

    private static byte[] FeatureRequest()
    {
        var request = new byte[Switch2UsbCommandCodec.FeatureRequestLength];
        Assert.IsTrue(Switch2UsbCommandCodec.TryWriteFeatureRequest(
            Switch2UsbFeatureStep.SetFeatureMask,
            Switch2UsbFeatureMask.ButtonsSticksImuAndRumble, request,
            out _));
        return request;
    }

    private static Switch2PhysicalInputLifetime Lifetime(
        Switch2ProUsbWindowsCandidate candidate)
    {
        Assert.IsTrue(candidate.TryGetAdmittedRegistration(
            out Switch2PhysicalInputRegistration registration));
        Assert.IsTrue(Switch2PhysicalInputLifetime.TryCreate(registration,
            DeviceGeneration, TransportGeneration, 10_000_000,
            out Switch2PhysicalInputLifetime lifetime));
        return lifetime;
    }

    private static Switch2ProUsbWindowsCandidate Candidate(string suffix = "A",
        Guid? containerId = null)
    {
        Assert.IsTrue(Switch2PhysicalContainerIdentity.TryCreate(
            containerId ??
                new Guid("6A3ABFC8-2F60-4675-A46A-5C6080C5C543"),
            out Switch2PhysicalContainerIdentity container));
        var output = new Switch2UsbPipeObservation(0x02,
            Switch2UsbPipeTransferType.Bulk, 64, 0);
        var input = new Switch2UsbPipeObservation(0x82,
            Switch2UsbPipeTransferType.Bulk, 64, 0);
        var hid = new Switch2UsbHidInterfaceObservation(container, 0, 0,
            Switch2UsbBoundDriver.HidClass, 0x01, 0x05, 64, 64, 0);
        var command = new Switch2UsbCommandInterfaceObservation(container, 1,
            0, Switch2UsbBoundDriver.WinUsb, 2, output, input);
        var observation = new Switch2ProUsbCompositeObservation(0x057E,
            0x2069, 0x0201, container, 1, 1, hid, command);
        return new Switch2ProUsbWindowsCandidate(observation, 17,
            "private-hid-instance-" + suffix,
            "private-hid-parent-" + suffix,
            "private-hid-path-" + suffix, "HidUsb", 19,
            "private-command-instance-" + suffix,
            "private-command-path-" + suffix, "WinUSB");
    }

    private sealed class FakeOwnedPlatform :
        ISwitch2ProUsbWindowsOwnedCompositePlatform
    {
        private readonly Queue<IReadOnlyList<
            Switch2ProUsbWindowsCandidate>> snapshots = new();
        private readonly FakeOwnedHid hid;
        private readonly FakeOwnedCommand command;

        internal FakeOwnedPlatform(FakeOwnedHid hid,
            FakeOwnedCommand command,
            params Switch2ProUsbWindowsCandidate[] candidates)
        {
            this.hid = hid;
            this.command = command;
            foreach (Switch2ProUsbWindowsCandidate candidate in candidates)
            {
                snapshots.Enqueue(candidate == null ?
                    Array.Empty<Switch2ProUsbWindowsCandidate>() :
                    new[] { candidate });
            }
        }

        internal int DiscoverCalls { get; private set; }
        internal int HidOpenCalls { get; private set; }
        internal int CommandOpenCalls { get; private set; }
        internal bool RejectCommandOpen { get; set; }
        internal bool ReturnTrueWithNullHid { get; set; }
        internal bool ReturnTrueWithNullCommand { get; set; }
        internal int ThrowOnDiscoverCall { get; set; }
        internal object DiscoveryRetainedOwner { get; } = new();

        public bool TryDiscoverCandidates(out IReadOnlyList<
            Switch2ProUsbWindowsCandidate> candidates)
        {
            DiscoverCalls++;
            if (DiscoverCalls == ThrowOnDiscoverCall)
            {
                candidates = null;
                throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Synthetic discovery cleanup ambiguity.",
                    DiscoveryRetainedOwner);
            }
            if (snapshots.Count == 0)
            {
                candidates = Array.Empty<Switch2ProUsbWindowsCandidate>();
                return false;
            }
            candidates = snapshots.Dequeue();
            return candidates.Count != 0;
        }

        public bool TryOpenOwnedHid(
            Switch2ProUsbWindowsCandidate candidate,
            out ISwitch2ProUsbWindowsOwnedHidHandle opened)
        {
            HidOpenCalls++;
            if (ReturnTrueWithNullHid)
            {
                opened = null;
                return true;
            }
            opened = hid;
            hid?.Events?.Add("open.hid");
            return opened != null;
        }

        public bool TryOpenOwnedCommand(
            Switch2ProUsbWindowsCandidate candidate,
            out ISwitch2ProUsbWindowsOwnedCommandHandle opened)
        {
            CommandOpenCalls++;
            if (ReturnTrueWithNullCommand)
            {
                opened = null;
                return true;
            }
            opened = RejectCommandOpen ? null : command;
            command?.Events?.Add("open.command");
            return opened != null;
        }

        public bool TryRevalidateOwnedCandidate(
            Switch2ProUsbWindowsCandidate expected)
        {
            if (!TryDiscoverCandidates(out IReadOnlyList<
                    Switch2ProUsbWindowsCandidate> candidates))
            {
                return false;
            }

            int matches = 0;
            foreach (Switch2ProUsbWindowsCandidate candidate in candidates)
            {
                if (candidate?.SameIdentity(expected) == true)
                {
                    matches++;
                }
            }
            return matches == 1;
        }
    }

    private sealed class FakeOwnedHid :
        ISwitch2ProUsbWindowsOwnedHidHandle
    {
        public bool HasObservedDeviceDisconnection { get; set; }
        private Action<Switch2ProUsbWindowsReadCompletion> readCompletion;
        private FakeReadOperation readOperation;
        internal FakeOwnedHid(List<string> events = null)
        {
            Events = events;
        }

        internal Queue<FakeIoOperation> OutputOperations { get; } = new();
        internal List<string> Events { get; }
        internal int InputBeginCalls { get; private set; }
        internal int OutputBeginCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal bool ThrowOnOutputBegin { get; set; }
        internal bool ThrowOnReadBeginWithoutPublishedOperation { get; set; }
        internal bool ReturnFalseWithReadOperation { get; set; }
        internal bool ReturnTrueWithNullOutputOperation { get; set; }
        internal bool ReturnFalseWithNullOutputOperation { get; set; }
        internal bool ReturnFalseWithOutputOperation { get; set; }
        internal bool ThrowOnDispose { get; set; }
        internal bool ReplayInputCompletionDuringDispose { get; set; }
        internal ManualResetEventSlim ReadBeginEntered { get; set; }
        internal ManualResetEventSlim AllowReadBeginReturn { get; set; }
        internal ManualResetEventSlim OutputBeginEntered { get; set; }
        internal ManualResetEventSlim AllowOutputBeginReturn { get; set; }

        public bool TryBeginRead(byte[] destination, int offset, int count,
            Action<Switch2ProUsbWindowsReadCompletion> callback,
            out ISwitch2ProUsbWindowsReadOperation operation)
        {
            InputBeginCalls++;
            ReadBeginEntered?.Set();
            AllowReadBeginReturn?.Wait(5_000);
            readCompletion = callback;
            readOperation = new FakeReadOperation();
            operation = readOperation;
            if (ThrowOnReadBeginWithoutPublishedOperation)
            {
                operation = null;
                throw new InvalidOperationException(
                    "Synthetic native input-start ambiguity.");
            }
            return !ReturnFalseWithReadOperation;
        }

        internal void CompleteInput(Switch2ProUsbNativeReadStatus status,
            int bytes)
        {
            readCompletion(new Switch2ProUsbWindowsReadCompletion(bytes, 123,
                status));
            readOperation.Quiescent = true;
        }

        internal void ReplayInputCompletion(
            Switch2ProUsbNativeReadStatus status, int bytes) =>
            readCompletion(new Switch2ProUsbWindowsReadCompletion(bytes, 456,
                status));

        public bool TryBeginOutputWrite(byte[] source, int offset, int count,
            out ISwitch2ProUsbWindowsOwnedIoOperation operation)
        {
            OutputBeginCalls++;
            OutputBeginEntered?.Set();
            AllowOutputBeginReturn?.Wait(5_000);
            if (ThrowOnOutputBegin)
            {
                throw new InvalidOperationException(
                    "Synthetic output-start fault.");
            }
            if (ReturnTrueWithNullOutputOperation)
            {
                operation = null;
                return true;
            }
            if (ReturnFalseWithNullOutputOperation)
            {
                operation = null;
                return false;
            }
            FakeIoOperation next = OutputOperations.Count == 0 ?
                FakeIoOperation.Completed(count) :
                OutputOperations.Dequeue();
            operation = next;
            return !ReturnFalseWithOutputOperation;
        }

        public void DisposeQuiesced()
        {
            DisposeCalls++;
            Events?.Add("dispose.hid");
            if (ReplayInputCompletionDuringDispose)
            {
                readCompletion(new Switch2ProUsbWindowsReadCompletion(64, 456,
                    Switch2ProUsbNativeReadStatus.Completed));
            }
            if (ThrowOnDispose)
            {
                throw new InvalidOperationException("Synthetic HID dispose.");
            }
        }
    }

    private sealed class FakeOwnedCommand :
        ISwitch2ProUsbWindowsOwnedCommandHandle
    {
        internal FakeOwnedCommand(List<string> events = null)
        {
            Events = events;
        }

        internal Queue<FakeIoOperation> WriteOperations { get; } = new();
        internal Queue<FakeIoOperation> ReadOperations { get; } = new();
        internal Queue<byte[]> Responses { get; } = new();
        internal List<string> Events { get; }
        internal int WriteBeginCalls { get; private set; }
        internal int ReadBeginCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal bool RejectReadStart { get; set; }
        internal bool ReturnTrueWithNullWriteOperation { get; set; }
        internal ManualResetEventSlim WriteBeginEntered { get; set; }
        internal ManualResetEventSlim AllowWriteBeginReturn { get; set; }
        internal int RetryableDisposeFailuresRemaining { get; set; }
        internal int AmbiguousDisposeFailuresRemaining { get; set; }

        public bool TryBeginBulkWrite(byte[] source, int offset, int count,
            out ISwitch2ProUsbWindowsOwnedIoOperation operation)
        {
            WriteBeginCalls++;
            WriteBeginEntered?.Set();
            AllowWriteBeginReturn?.Wait(5_000);
            if (ReturnTrueWithNullWriteOperation)
            {
                operation = null;
                return true;
            }
            operation = WriteOperations.Count == 0 ?
                FakeIoOperation.Completed(count) :
                WriteOperations.Dequeue();
            return true;
        }

        public bool TryBeginBulkRead(byte[] destination, int offset,
            int count, out ISwitch2ProUsbWindowsOwnedIoOperation operation)
        {
            ReadBeginCalls++;
            if (RejectReadStart)
            {
                operation = null;
                return false;
            }
            byte[] response = Responses.Count == 0 ?
                Array.Empty<byte>() : Responses.Dequeue();
            response.CopyTo(destination, offset);
            operation = ReadOperations.Count == 0 ?
                FakeIoOperation.Completed(response.Length) :
                ReadOperations.Dequeue();
            return true;
        }

        public void DisposeQuiesced()
        {
            DisposeCalls++;
            Events?.Add("dispose.command");
            if (RetryableDisposeFailuresRemaining > 0)
            {
                RetryableDisposeFailuresRemaining--;
                throw new Switch2ProUsbWindowsRetryableReleaseException(
                    "Synthetic retryable command release.");
            }
            if (AmbiguousDisposeFailuresRemaining > 0)
            {
                AmbiguousDisposeFailuresRemaining--;
                throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Synthetic ambiguous command release.", this);
            }
        }
    }

    private sealed class FakeIoOperation :
        ISwitch2ProUsbWindowsOwnedIoOperation
    {
        private bool quiescent;
        private Switch2ProUsbWindowsOwnedIoCompletion completion;

        internal bool CompleteInsideCancel { get; set; }
        internal bool CancelResult { get; set; } = true;
        internal bool BlockWait { get; set; }
        internal bool ThrowOnWait { get; set; }
        internal ManualResetEventSlim WaitEntered { get; } = new(false);
        internal ManualResetEventSlim AllowWait { get; } = new(false);
        internal int CancelCalls { get; private set; }
        internal int ReleaseCalls { get; private set; }

        internal static FakeIoOperation Completed(int bytes,
            Switch2ProUsbNativeReadStatus status =
                Switch2ProUsbNativeReadStatus.Completed)
        {
            var operation = new FakeIoOperation();
            operation.Complete(status, bytes);
            return operation;
        }

        internal void Complete(Switch2ProUsbNativeReadStatus status,
            int bytes)
        {
            completion = new Switch2ProUsbWindowsOwnedIoCompletion(bytes,
                status);
            Volatile.Write(ref quiescent, true);
        }

        public bool TryCancelExact()
        {
            CancelCalls++;
            if (CompleteInsideCancel)
            {
                Complete(Switch2ProUsbNativeReadStatus.Cancelled, 0);
            }
            return CancelResult;
        }

        public bool TryWaitForNativeQuiescence(int timeoutMilliseconds)
        {
            if (ThrowOnWait)
            {
                throw new InvalidOperationException("Synthetic wait fault.");
            }
            if (BlockWait)
            {
                WaitEntered.Set();
                AllowWait.Wait(2_000);
            }
            return Volatile.Read(ref quiescent);
        }

        public bool TryGetCompletion(
            out Switch2ProUsbWindowsOwnedIoCompletion result)
        {
            result = completion;
            return Volatile.Read(ref quiescent);
        }

        public void ReleaseSubmissionQuiesced()
        {
            if (!Volatile.Read(ref quiescent))
            {
                throw new InvalidOperationException(
                    "Synthetic operation is not quiescent.");
            }
            ReleaseCalls++;
        }
    }

    private sealed class FakeReadOperation :
        ISwitch2ProUsbWindowsReadOperation
    {
        internal bool Quiescent { get; set; }

        public bool TryCancelExact() => true;

        public bool TryWaitForNativeQuiescence(int timeoutMilliseconds) =>
            Quiescent;

        public void ReleaseSubmissionQuiesced()
        {
        }
    }

    private sealed class RecordingReadTarget :
        ISwitch2ProUsbReadCompletionTarget
    {
        internal int Calls { get; private set; }

        public Switch2ProUsbReadCompletionDisposition CompleteInputRead(
            in Switch2ProUsbReadClaim claim, int bytesTransferred,
            long completionTimestampQpc,
            Switch2ProUsbNativeReadStatus status)
        {
            Calls++;
            return Switch2ProUsbReadCompletionDisposition.Published;
        }
    }
}
