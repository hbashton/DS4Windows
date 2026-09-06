using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DS4Windows.Switch2;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2ProUsbWindowsAdapterTests
{
    [TestMethod]
    public void PreReservationDiscoveryAmbiguityStronglyFencesAllLaterDiscovery()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var retainedOwner = new object();
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var failedPlatform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), candidate)
        {
            BeforeDiscover = _ =>
                throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Synthetic pre-attribution cleanup ambiguity.",
                    retainedOwner),
        };
        var failed = new Switch2ProUsbWindowsAdapter(failedPlatform,
            reservations);

        Assert.IsFalse(failed.TryObserveComposite(out _));
        Assert.IsTrue(reservations.HasUnattributedAcquisitionQuarantine);
        Assert.IsTrue(reservations.RetainsUnattributedAcquisitionQuarantine(
            retainedOwner));

        var blockedPlatform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), candidate);
        var blocked = new Switch2ProUsbWindowsAdapter(blockedPlatform,
            reservations);
        Assert.IsFalse(blocked.TryObserveComposite(out _));
        Assert.AreEqual(0, blockedPlatform.DiscoverCalls,
            "A pre-attribution cleanup ambiguity must block native discovery.");
        Assert.IsFalse(reservations.TryAcquire(
            candidate.Observation.ContainerIdentity, out _));
    }

    [TestMethod]
    public void DiscoveryAndOpen_RevalidateExactCandidateAndReturnLease()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var input = new FakeInputHandle();
        var presence = new FakePresenceHandle();
        var platform = new FakePlatform(input, presence, candidate, candidate,
            candidate);
        var adapter = IsolatedAdapter(platform);

        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));
        Assert.IsTrue(adapter.TryOpenReadOnlyComposite(registration,
            out var lease));
        Assert.AreEqual(3, platform.DiscoverCalls);
        Assert.AreEqual(1, platform.InputOpenCalls);
        Assert.AreEqual(1, platform.PresenceOpenCalls);
        Assert.AreEqual(registration, lease.Registration);

        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        lease.DisposeQuiesced();
        Assert.AreEqual(1, input.DisposeCalls);
        Assert.AreEqual(1, presence.DisposeCalls);
    }

    [TestMethod]
    public void Open_ConsumesObservationAndRejectsReplay()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var platform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), candidate, candidate, candidate);
        var adapter = IsolatedAdapter(platform);
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));
        Assert.IsTrue(adapter.TryOpenReadOnlyComposite(registration,
            out var lease));
        Assert.IsFalse(adapter.TryOpenReadOnlyComposite(registration,
            out _));
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        lease.DisposeQuiesced();
    }

    [TestMethod]
    public void Discovery_MissingOrMultipleCompositeFailsClosed()
    {
        var platform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), discoveryResults: Array.Empty<
                Switch2ProUsbWindowsCandidate>());
        var adapter = IsolatedAdapter(platform);

        Assert.IsFalse(adapter.TryObserveComposite(out _));
        Assert.AreEqual(0, platform.InputOpenCalls);
        Assert.AreEqual(0, platform.PresenceOpenCalls);

        var duplicatePlatform = FakePlatform.FromSnapshots(
            new FakeInputHandle(), new FakePresenceHandle(),
            new[] { Candidate("A"), Candidate("B") });
        var duplicateAdapter = IsolatedAdapter(duplicatePlatform);
        Assert.IsFalse(duplicateAdapter.TryObserveComposite(out _));
        Assert.AreEqual(0, duplicatePlatform.InputOpenCalls);
        Assert.AreEqual(0, duplicatePlatform.PresenceOpenCalls);
    }

    [TestMethod]
    public void Discovery_BcdAndInterfaceCardinalityMismatchFailClosed()
    {
        foreach (Switch2ProUsbWindowsCandidate rejected in new[]
                 {
                     Candidate(bcdDevice: 0x0200),
                     Candidate(matchingInputCount: 2),
                     Candidate(matchingCommandCount: 0),
                 })
        {
            var platform = new FakePlatform(new FakeInputHandle(),
                new FakePresenceHandle(), rejected);
            var adapter = IsolatedAdapter(platform);
            Assert.IsFalse(adapter.TryObserveComposite(out _));
            Assert.AreEqual(0, platform.InputOpenCalls);
            Assert.AreEqual(0, platform.PresenceOpenCalls);
        }
    }

    [TestMethod]
    public void Discovery_InvalidContainerDoesNotSuppressValidContainer()
    {
        Switch2ProUsbWindowsCandidate invalid = Candidate("invalid",
            bcdDevice: 0x0200);
        Switch2ProUsbWindowsCandidate valid = Candidate("valid",
            containerGuid: new Guid(
                "815E9DB1-971F-4482-941D-52D199FAAE01"));
        var platform = FakePlatform.FromSnapshots(new FakeInputHandle(),
            new FakePresenceHandle(), new[] { invalid, valid });
        var adapter = IsolatedAdapter(platform);

        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.AreEqual(valid.Observation.ContainerIdentity,
            observation.ContainerIdentity);
    }

    [TestMethod]
    public void Discovery_UnattributableEntryFailureRejectsWholeSnapshot()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var platform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), candidate);
        platform.BeforeDiscover = call =>
        {
            if (call == 1)
            {
                throw new InvalidOperationException(
                    "Injected unattributable active-entry failure.");
            }
        };
        var adapter = IsolatedAdapter(platform);

        Assert.IsFalse(adapter.TryObserveComposite(out _));
        platform.BeforeDiscover = null;
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.AreEqual(candidate.Observation.ContainerIdentity,
            observation.ContainerIdentity);
    }

    [TestMethod]
    public void Discovery_ConcurrentFailureCannotClearNewerSuccess()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        using var firstEntered = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var platform = FakePlatform.FromSnapshots(new FakeInputHandle(),
            new FakePresenceHandle(),
            Array.Empty<Switch2ProUsbWindowsCandidate>(),
            new[] { candidate }, new[] { candidate }, new[] { candidate });
        platform.BeforeDiscover = call =>
        {
            if (call == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(1_000);
            }
        };
        var adapter = IsolatedAdapter(platform);
        bool firstResult = true;
        bool secondResult = false;
        Switch2ProUsbCompositeObservation secondObservation = default;

        Task first = Task.Run(() => firstResult =
            adapter.TryObserveComposite(out _));
        Assert.IsTrue(firstEntered.Wait(1_000));
        Task second = Task.Run(() => secondResult =
            adapter.TryObserveComposite(out secondObservation));
        releaseFirst.Set();
        Assert.IsTrue(Task.WaitAll(new[] { first, second }, 1_000));

        Assert.IsFalse(firstResult);
        Assert.IsTrue(secondResult);
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(
            secondObservation, out var registration, out _));
        Assert.IsTrue(adapter.TryOpenReadOnlyComposite(registration,
            out var lease));
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        lease.DisposeQuiesced();
    }

    [TestMethod]
    public void Open_SharingViolationEquivalentFailsBeforePresenceLease()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var platform = new FakePlatform(null, new FakePresenceHandle(),
            candidate, candidate)
        {
            RejectInputOpen = true,
        };
        var adapter = IsolatedAdapter(platform);
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));

        Assert.IsFalse(adapter.TryOpenReadOnlyComposite(registration,
            out _));
        Assert.AreEqual(1, platform.InputOpenCalls);
        Assert.AreEqual(0, platform.PresenceOpenCalls);
    }

    [TestMethod]
    public void Open_PresenceAccessFailureDisposesInputHandle()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var input = new FakeInputHandle();
        var platform = new FakePlatform(input, null, candidate, candidate);
        var adapter = IsolatedAdapter(platform);
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));

        Assert.IsFalse(adapter.TryOpenReadOnlyComposite(registration,
            out _));
        Assert.AreEqual(1, input.DisposeCalls);
        Assert.AreEqual(1, platform.PresenceOpenCalls);
    }

    [TestMethod]
    public void Open_RebindBeforeAcquisitionFailsWithoutOpeningHandles()
    {
        Switch2ProUsbWindowsCandidate first = Candidate("A");
        Switch2ProUsbWindowsCandidate rebound = Candidate("B");
        var platform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), first, rebound);
        var adapter = IsolatedAdapter(platform);
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));

        Assert.IsFalse(adapter.TryOpenReadOnlyComposite(registration,
            out _));
        Assert.AreEqual(0, platform.InputOpenCalls);
        Assert.AreEqual(0, platform.PresenceOpenCalls);
    }

    [TestMethod]
    public void Open_RebindAfterAcquisitionClosesBothPartialHandles()
    {
        Switch2ProUsbWindowsCandidate first = Candidate("A");
        Switch2ProUsbWindowsCandidate rebound = Candidate("B");
        var input = new FakeInputHandle();
        var presence = new FakePresenceHandle();
        var platform = new FakePlatform(input, presence, first, first,
            rebound);
        var adapter = IsolatedAdapter(platform);
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));

        Assert.IsFalse(adapter.TryOpenReadOnlyComposite(registration,
            out _));
        Assert.AreEqual(1, input.DisposeCalls);
        Assert.AreEqual(1, presence.DisposeCalls);
    }

    [TestMethod]
    public void Open_DescriptorTopologyChangeAfterAcquisitionFailsClosed()
    {
        Switch2ProUsbWindowsCandidate first = Candidate("A");
        Switch2ProUsbWindowsCandidate changed = Candidate("A",
            commandInterval: 1);
        var input = new FakeInputHandle();
        var presence = new FakePresenceHandle();
        var platform = new FakePlatform(input, presence, first, first,
            changed);
        var adapter = IsolatedAdapter(platform);
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));

        Assert.IsFalse(adapter.TryOpenReadOnlyComposite(registration,
            out _));
        Assert.AreEqual(1, input.DisposeCalls);
        Assert.AreEqual(1, presence.DisposeCalls);
    }

    [TestMethod]
    public void Discovery_TwoOpaqueContainersCanHoldIndependentLeases()
    {
        Switch2ProUsbWindowsCandidate first = Candidate("A");
        Switch2ProUsbWindowsCandidate second = Candidate("B",
            containerGuid: new Guid(
                "28D6D0E2-D502-45F1-B8C0-073787F6E3E7"));
        IReadOnlyList<Switch2ProUsbWindowsCandidate> snapshot =
            new[] { first, second };
        var firstInput = new FakeInputHandle();
        var secondInput = new FakeInputHandle();
        var firstPresence = new FakePresenceHandle();
        var secondPresence = new FakePresenceHandle();
        var platform = FakePlatform.FromSnapshots(null, null,
            snapshot, snapshot, snapshot, snapshot, snapshot, snapshot);
        platform.InputSelector = candidate =>
            candidate.Observation.ContainerIdentity.Equals(
                first.Observation.ContainerIdentity) ?
                firstInput : secondInput;
        platform.PresenceSelector = candidate =>
            candidate.Observation.ContainerIdentity.Equals(
                first.Observation.ContainerIdentity) ?
                firstPresence : secondPresence;
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var adapter = new Switch2ProUsbWindowsAdapter(platform, reservations);

        Assert.IsTrue(adapter.TryObserveComposite(out var firstObservation));
        Assert.AreEqual(first.Observation.ContainerIdentity,
            firstObservation.ContainerIdentity);
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(
            firstObservation, out var firstRegistration, out _));
        Assert.IsTrue(adapter.TryOpenReadOnlyComposite(firstRegistration,
            out var firstLease));

        Assert.IsTrue(adapter.TryObserveComposite(out var secondObservation));
        Assert.AreEqual(second.Observation.ContainerIdentity,
            secondObservation.ContainerIdentity);
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(
            secondObservation, out var secondRegistration, out _));
        Assert.IsTrue(adapter.TryOpenReadOnlyComposite(secondRegistration,
            out var secondLease));

        Assert.AreEqual(2, platform.InputOpenCalls);
        Assert.AreEqual(2, platform.PresenceOpenCalls);
        Assert.IsTrue(firstLease.TryWaitForInputQuiescence(0));
        Assert.IsTrue(secondLease.TryWaitForInputQuiescence(0));
        firstLease.DisposeQuiesced();
        secondLease.DisposeQuiesced();
    }

    [TestMethod]
    public void Discovery_FailedFirstContainerDoesNotStarveSecondContainer()
    {
        Switch2ProUsbWindowsCandidate first = Candidate("A");
        Switch2ProUsbWindowsCandidate second = Candidate("B",
            containerGuid: new Guid(
                "AB7D00C3-220A-4212-99CC-2935DD0569B8"));
        IReadOnlyList<Switch2ProUsbWindowsCandidate> snapshot =
            new[] { first, second };
        var platform = FakePlatform.FromSnapshots(new FakeInputHandle(),
            new FakePresenceHandle(), snapshot, snapshot, snapshot, snapshot,
            snapshot);
        platform.RejectInputOpen = true;
        var adapter = IsolatedAdapter(platform);

        Assert.IsTrue(adapter.TryObserveComposite(out var firstObservation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(
            firstObservation, out var firstRegistration, out _));
        Assert.IsFalse(adapter.TryOpenReadOnlyComposite(firstRegistration,
            out _));

        platform.RejectInputOpen = false;
        Assert.IsTrue(adapter.TryObserveComposite(out var secondObservation));
        Assert.AreEqual(second.Observation.ContainerIdentity,
            secondObservation.ContainerIdentity);
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(
            secondObservation, out var secondRegistration, out _));
        Assert.IsTrue(adapter.TryOpenReadOnlyComposite(secondRegistration,
            out var lease));
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        lease.DisposeQuiesced();
    }

    [TestMethod]
    public void Reservation_DuplicateSameContainerOpenIsRejectedWhileAlive()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var firstPlatform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), candidate, candidate, candidate);
        var secondPlatform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), candidate, candidate, candidate);
        var firstAdapter = new Switch2ProUsbWindowsAdapter(firstPlatform,
            reservations);
        var secondAdapter = new Switch2ProUsbWindowsAdapter(secondPlatform,
            reservations);

        Assert.IsTrue(firstAdapter.TryObserveComposite(out var observation));
        Assert.IsTrue(secondAdapter.TryObserveComposite(out _));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));
        Assert.IsTrue(firstAdapter.TryOpenReadOnlyComposite(registration,
            out var lease));

        Assert.IsFalse(secondAdapter.TryOpenReadOnlyComposite(registration,
            out _));
        Assert.AreEqual(1, secondPlatform.DiscoverCalls);
        Assert.AreEqual(0, secondPlatform.InputOpenCalls);
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        lease.DisposeQuiesced();
    }

    [TestMethod]
    public void Reservation_ConcurrentSameContainerAcquisitionIsAtomic()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var firstPlatform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), candidate, candidate, candidate);
        var secondPlatform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), candidate, candidate, candidate);
        var firstAdapter = new Switch2ProUsbWindowsAdapter(firstPlatform,
            reservations);
        var secondAdapter = new Switch2ProUsbWindowsAdapter(secondPlatform,
            reservations);
        Assert.IsTrue(firstAdapter.TryObserveComposite(out var observation));
        Assert.IsTrue(secondAdapter.TryObserveComposite(out _));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));

        bool firstOpened = false;
        bool secondOpened = false;
        ISwitch2ProUsbReadOnlyCompositeLease firstLease = null;
        ISwitch2ProUsbReadOnlyCompositeLease secondLease = null;
        Task.WaitAll(
            Task.Run(() => firstOpened =
                firstAdapter.TryOpenReadOnlyComposite(registration,
                    out firstLease)),
            Task.Run(() => secondOpened =
                secondAdapter.TryOpenReadOnlyComposite(registration,
                    out secondLease)));

        Assert.AreNotEqual(firstOpened, secondOpened);
        ISwitch2ProUsbReadOnlyCompositeLease winner = firstLease ?? secondLease;
        Assert.IsNotNull(winner);
        Assert.IsTrue(winner.TryWaitForInputQuiescence(0));
        winner.DisposeQuiesced();
    }

    [TestMethod]
    public void Reservation_ReleasesOnlyAtTerminalDisposeAndCanReopen()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var firstAdapter = new Switch2ProUsbWindowsAdapter(
            new FakePlatform(new FakeInputHandle(), new FakePresenceHandle(),
                candidate, candidate, candidate), reservations);
        var blockedPlatform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), candidate, candidate);
        var blockedAdapter = new Switch2ProUsbWindowsAdapter(blockedPlatform,
            reservations);
        Assert.IsTrue(firstAdapter.TryObserveComposite(out var observation));
        Assert.IsTrue(blockedAdapter.TryObserveComposite(out _));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));
        Assert.IsTrue(firstAdapter.TryOpenReadOnlyComposite(registration,
            out var firstLease));

        Assert.IsTrue(firstLease.TryWaitForInputQuiescence(0));
        Assert.IsTrue(reservations.IsReserved(
            registration.ContainerIdentity));
        Assert.IsFalse(blockedAdapter.TryOpenReadOnlyComposite(registration,
            out _));
        firstLease.DisposeQuiesced();
        Assert.IsFalse(reservations.IsReserved(
            registration.ContainerIdentity));

        var reopenedAdapter = new Switch2ProUsbWindowsAdapter(
            new FakePlatform(new FakeInputHandle(), new FakePresenceHandle(),
                candidate, candidate, candidate), reservations);
        Assert.IsTrue(reopenedAdapter.TryObserveComposite(
            out var reopenedObservation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(
            reopenedObservation, out var reopenedRegistration, out _));
        Assert.IsTrue(reopenedAdapter.TryOpenReadOnlyComposite(
            reopenedRegistration, out var reopenedLease));
        Assert.IsTrue(reopenedLease.TryWaitForInputQuiescence(0));
        reopenedLease.DisposeQuiesced();
    }

    [TestMethod]
    public void Reservation_FailedTerminalDisposeRetainsFenceUntilRetry()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var failingInput = new FakeInputHandle
        {
            DisposeFailuresRemaining = 1,
        };
        var firstAdapter = new Switch2ProUsbWindowsAdapter(
            new FakePlatform(failingInput, new FakePresenceHandle(),
                candidate, candidate, candidate), reservations);
        var blockedPlatform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), candidate, candidate);
        var blockedAdapter = new Switch2ProUsbWindowsAdapter(blockedPlatform,
            reservations);
        Assert.IsTrue(firstAdapter.TryObserveComposite(out var observation));
        Assert.IsTrue(blockedAdapter.TryObserveComposite(out _));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));
        Assert.IsTrue(firstAdapter.TryOpenReadOnlyComposite(registration,
            out var lease));
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));

        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
        Assert.IsTrue(reservations.IsReserved(
            registration.ContainerIdentity));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(
            registration.ContainerIdentity, lease));
        Assert.IsFalse(blockedAdapter.TryOpenReadOnlyComposite(registration,
            out _));

        lease.DisposeQuiesced();
        Assert.IsFalse(reservations.IsReserved(
            registration.ContainerIdentity));
    }

    [TestMethod]
    public void Reservation_ReleaseHookFailureRetainsCapabilityForExactRetry()
    {
        int releaseCalls = 0;
        var reservations = new Switch2ProUsbWindowsReservationRegistry(() =>
        {
            if (Interlocked.Increment(ref releaseCalls) == 1)
            {
                throw new InvalidOperationException(
                    "Synthetic first reservation publication failure.");
            }
        });
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var adapter = new Switch2ProUsbWindowsAdapter(
            new FakePlatform(new FakeInputHandle(), new FakePresenceHandle(),
                candidate, candidate, candidate), reservations);
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));
        Assert.IsTrue(adapter.TryOpenReadOnlyComposite(registration,
            out var lease));
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));

        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
        Assert.IsTrue(reservations.IsReserved(
            registration.ContainerIdentity));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(
            registration.ContainerIdentity, lease));

        lease.DisposeQuiesced();
        Assert.AreEqual(2, releaseCalls);
        Assert.IsFalse(reservations.IsReserved(
            registration.ContainerIdentity));
    }

    [TestMethod]
    public void Reservation_StrongRootsLegacyLeaseAcrossCallerDropAndGc()
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
        WeakReference<Switch2ProUsbWindowsReadOnlyCompositeLease> weak =
            OpenLegacyAndDropAfterTerminalFailure(reservations, candidate,
                out Switch2PhysicalInputRegistration registration);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.IsTrue(weak.TryGetTarget(out var retained));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(
            registration.ContainerIdentity, retained));
        retained.DisposeQuiesced();
        Assert.AreEqual(2, releaseCalls);
        Assert.IsFalse(reservations.IsReserved(
            registration.ContainerIdentity));
    }

    [TestMethod]
    public void Reservation_RootsAmbiguousFileAcrossGcWithoutFinalizerClose()
    {
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var closeCounter = new CloseCounter();
        (WeakReference<Switch2ProUsbWindowsReadOnlyCompositeLease> leaseWeak,
            WeakReference<SafeFileHandle> handleWeak,
            Switch2PhysicalInputRegistration registration) =
            OpenLegacyWithAmbiguousFileAndDrop(reservations, candidate,
                closeCounter);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.IsTrue(leaseWeak.TryGetTarget(out var retainedLease));
        Assert.IsTrue(handleWeak.TryGetTarget(out var retainedHandle));
        Assert.AreEqual(1, closeCounter.Calls);
        Assert.IsTrue(retainedHandle.IsClosed);
        Assert.IsTrue(Switch2ProUsbWindowsExactHandleRelease.
            IsFileNativeReleaseSuppressed(retainedHandle));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(
            registration.ContainerIdentity, retainedLease));
        Assert.IsFalse(reservations.TryAcquire(
            registration.ContainerIdentity, out _),
            "An ambiguous live file lifetime must forbid replacement ownership.");

        Assert.ThrowsException<
            Switch2ProUsbWindowsCleanupAmbiguousException>(
            retainedLease.DisposeQuiesced);
        Assert.AreEqual(1, closeCounter.Calls,
            "Retry/GC must never close an ambiguity-recycled numeric handle.");
        Assert.IsTrue(reservations.IsReserved(
            registration.ContainerIdentity));
    }

    [TestMethod]
    public void Reservation_TerminalRootsArePerContainerAndExactOwnerBound()
    {
        Assert.IsTrue(Switch2PhysicalContainerIdentity.TryCreate(
            new Guid("D519B639-C9CD-49B1-8633-76E220D78CEE"),
            out var firstContainer));
        Assert.IsTrue(Switch2PhysicalContainerIdentity.TryCreate(
            new Guid("30CA7581-FA90-4BC1-86BA-18D017EC5385"),
            out var secondContainer));
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        Assert.IsTrue(reservations.TryAcquire(firstContainer,
            out var first));
        Assert.IsTrue(reservations.TryAcquire(secondContainer,
            out var second));
        var firstOwner = new object();
        var secondOwner = new object();
        first.AdoptTerminalLifetime(firstOwner);
        second.AdoptTerminalLifetime(secondOwner);

        Assert.IsTrue(reservations.RetainsTerminalLifetime(firstContainer,
            firstOwner));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(secondContainer,
            secondOwner));
        first.ReleaseAfterTerminalDisposal(
            new Switch2ProUsbWindowsCompositeTerminalFence(), firstOwner);
        Assert.IsFalse(reservations.IsReserved(firstContainer));
        Assert.IsTrue(reservations.IsReserved(secondContainer));

        Assert.ThrowsException<InvalidOperationException>(() =>
            second.ReleaseAfterTerminalDisposal(
                new Switch2ProUsbWindowsCompositeTerminalFence(),
                firstOwner));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(secondContainer,
            secondOwner));
        second.ReleaseAfterTerminalDisposal(
            new Switch2ProUsbWindowsCompositeTerminalFence(), secondOwner);
        Assert.IsFalse(reservations.IsReserved(secondContainer));
    }

    [TestMethod]
    public void Reservation_PartialOpenCleanupFailurePoisonsOnlyItsContainer()
    {
        Switch2ProUsbWindowsCandidate first = Candidate("A");
        Switch2ProUsbWindowsCandidate rebound = Candidate("rebound");
        Switch2ProUsbWindowsCandidate second = Candidate("B",
            containerGuid: new Guid(
                "A77FCBC1-1F39-4AAE-9341-58ABEC156457"));
        var reservations = new Switch2ProUsbWindowsReservationRegistry();
        var failingInput = new FakeInputHandle
        {
            DisposeFailuresRemaining = 1,
        };
        var poisonedAdapter = new Switch2ProUsbWindowsAdapter(
            new FakePlatform(failingInput, new FakePresenceHandle(), first,
                first, rebound), reservations);
        var blockedPlatform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), first, first);
        var blockedAdapter = new Switch2ProUsbWindowsAdapter(blockedPlatform,
            reservations);
        Assert.IsTrue(poisonedAdapter.TryObserveComposite(
            out var firstObservation));
        Assert.IsTrue(blockedAdapter.TryObserveComposite(out _));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(
            firstObservation, out var firstRegistration, out _));

        Assert.IsFalse(poisonedAdapter.TryOpenReadOnlyComposite(
            firstRegistration, out _));
        Assert.IsTrue(reservations.IsReserved(
            firstRegistration.ContainerIdentity));
        Assert.IsFalse(blockedAdapter.TryOpenReadOnlyComposite(
            firstRegistration, out _));
        Assert.AreEqual(1, blockedPlatform.DiscoverCalls);
        Assert.AreEqual(0, blockedPlatform.InputOpenCalls);

        var secondAdapter = new Switch2ProUsbWindowsAdapter(
            new FakePlatform(new FakeInputHandle(), new FakePresenceHandle(),
                second, second, second), reservations);
        Assert.IsTrue(secondAdapter.TryObserveComposite(
            out var secondObservation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(
            secondObservation, out var secondRegistration, out _));
        Assert.IsTrue(secondAdapter.TryOpenReadOnlyComposite(
            secondRegistration, out var secondLease));
        Assert.IsTrue(secondLease.TryWaitForInputQuiescence(0));
        secondLease.DisposeQuiesced();
    }

    [TestMethod]
    public void Reservation_ConcurrentDisposeCannotPublishBeforeRelease()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        using var releaseEntered = new ManualResetEventSlim(false);
        using var permitRelease = new ManualResetEventSlim(false);
        var reservations = new Switch2ProUsbWindowsReservationRegistry(() =>
        {
            releaseEntered.Set();
            permitRelease.Wait(1_000);
        });
        var adapter = new Switch2ProUsbWindowsAdapter(
            new FakePlatform(new FakeInputHandle(), new FakePresenceHandle(),
                candidate, candidate, candidate), reservations);
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));
        Assert.IsTrue(adapter.TryOpenReadOnlyComposite(registration,
            out var lease));
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));

        Task firstDispose = Task.Run(lease.DisposeQuiesced);
        Assert.IsTrue(releaseEntered.Wait(1_000));
        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
        Assert.IsTrue(reservations.IsReserved(
            registration.ContainerIdentity));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(
            registration.ContainerIdentity, lease));
        permitRelease.Set();
        Assert.IsTrue(firstDispose.Wait(1_000));
        Assert.IsFalse(reservations.IsReserved(
            registration.ContainerIdentity));
    }

    [TestMethod]
    public void Reservation_ReentrantStaleCallbackInHookRetainsFence()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var input = new FakeInputHandle();
        var reservations = new Switch2ProUsbWindowsReservationRegistry(() =>
            input.ReplayCompletion(
                Switch2ProUsbNativeReadStatus.Completed, 64));
        var adapter = new Switch2ProUsbWindowsAdapter(
            new FakePlatform(input, new FakePresenceHandle(), candidate,
                candidate, candidate), reservations);
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));
        Assert.IsTrue(adapter.TryOpenReadOnlyComposite(registration,
            out var lease));
        Assert.IsTrue(lease.TryBeginInputRead(new byte[64], 0, 64, Claim(1),
            new RecordingTarget()));
        input.Complete(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));

        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
        Assert.IsTrue(reservations.IsReserved(
            registration.ContainerIdentity));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(
            registration.ContainerIdentity, lease));
        Assert.AreEqual(1, input.DisposeCalls);
    }

    [TestMethod]
    public void Reservation_ConcurrentStaleCallbackInHookRetainsFence()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var input = new FakeInputHandle();
        var reservations = new Switch2ProUsbWindowsReservationRegistry(() =>
        {
            Task callback = Task.Run(() => input.ReplayCompletion(
                Switch2ProUsbNativeReadStatus.Completed, 64));
            if (!callback.Wait(1_000))
            {
                throw new InvalidOperationException(
                    "Concurrent callback did not cross the release hook.");
            }
        });
        var adapter = new Switch2ProUsbWindowsAdapter(
            new FakePlatform(input, new FakePresenceHandle(), candidate,
                candidate, candidate), reservations);
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out var registration, out _));
        Assert.IsTrue(adapter.TryOpenReadOnlyComposite(registration,
            out var lease));
        Assert.IsTrue(lease.TryBeginInputRead(new byte[64], 0, 64, Claim(1),
            new RecordingTarget()));
        input.Complete(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));

        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
        Assert.IsTrue(reservations.IsReserved(
            registration.ContainerIdentity));
        Assert.IsTrue(reservations.RetainsTerminalLifetime(
            registration.ContainerIdentity, lease));
        Assert.AreEqual(1, input.DisposeCalls);
    }

    [TestMethod]
    public void Lease_PropagatesExactClaimAcrossSynchronousCompletion()
    {
        var input = new FakeInputHandle
        {
            CompleteSynchronously = true,
        };
        using var fixture = LeaseFixture.Create(input);
        var target = new RecordingTarget();
        Switch2ProUsbReadClaim claim = Claim(1);

        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            claim, target));
        Assert.AreEqual(1, target.Calls);
        Assert.AreEqual(claim, target.LastClaim);
        Assert.AreEqual(Switch2ProUsbNativeReadStatus.Completed,
            target.LastStatus);
        Assert.IsTrue(fixture.Lease.TryWaitForInputQuiescence(0));
    }

    [TestMethod]
    public void Lease_AllowsOnlyOneAsynchronousReadAndRejectsStaleClaim()
    {
        var input = new FakeInputHandle();
        using var fixture = LeaseFixture.Create(input);
        var target = new RecordingTarget();
        Switch2ProUsbReadClaim claim = Claim(1);
        Switch2ProUsbReadClaim stale = Claim(2);

        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            claim, target));
        Assert.IsFalse(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            stale, target));
        Assert.IsFalse(fixture.Lease.TryCancelInputRead(stale));
        Assert.IsTrue(fixture.Lease.TryCancelInputRead(claim));
        Assert.AreEqual(1, input.LastOperation.CancelCalls);

        input.Complete(Switch2ProUsbNativeReadStatus.Cancelled, 0);
        Assert.AreEqual(claim, target.LastClaim);
        Assert.AreEqual(Switch2ProUsbNativeReadStatus.Cancelled,
            target.LastStatus);
        Assert.IsTrue(fixture.Lease.TryWaitForInputQuiescence(0));
    }

    [TestMethod]
    public void Lease_CancelFalseCanRetryTheSameExactPendingRead()
    {
        var input = new FakeInputHandle
        {
            CancelResult = false,
        };
        using var fixture = LeaseFixture.Create(input);
        Switch2ProUsbReadClaim claim = Claim(1);
        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            claim, new RecordingTarget()));

        Assert.IsFalse(fixture.Lease.TryCancelInputRead(claim));
        Assert.AreEqual(1, input.LastOperation.CancelCalls);
        input.CancelResult = true;
        Assert.IsTrue(fixture.Lease.TryCancelInputRead(claim));
        Assert.AreEqual(2, input.LastOperation.CancelCalls,
            "A rejected CancelIoEx request is not a cancellation proof.");

        input.Complete(Switch2ProUsbNativeReadStatus.Cancelled, 0);
        Assert.IsTrue(fixture.Lease.TryWaitForInputQuiescence(0));
    }

    [TestMethod]
    public void Lease_CancelFalseCompletionRaceRetiresWithoutSecondCancel()
    {
        var input = new FakeInputHandle
        {
            CancelResult = false,
            CompleteInsideCancel = true,
        };
        using var fixture = LeaseFixture.Create(input);
        Switch2ProUsbReadClaim claim = Claim(1);
        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            claim, new RecordingTarget()));

        Assert.IsFalse(fixture.Lease.TryCancelInputRead(claim));
        Assert.AreEqual(1, input.LastOperation.CancelCalls);
        Assert.IsFalse(fixture.Lease.TryCancelInputRead(claim),
            "The completion callback already sealed this exact epoch.");
        Assert.AreEqual(1, input.LastOperation.CancelCalls);
        Assert.IsTrue(fixture.Lease.TryWaitForInputQuiescence(0));
    }

    [DataTestMethod]
    [DataRow((int)ContradictoryReadStartKind.FalseWithOperation)]
    [DataRow((int)ContradictoryReadStartKind.TrueWithNull)]
    [DataRow((int)ContradictoryReadStartKind.ThrowWithOperation)]
    public void Lease_ContradictoryReadStartRetainsOperationAndPermanentlyFences(
        int encodedKind)
    {
        var kind = (ContradictoryReadStartKind)encodedKind;
        var input = new ContradictoryInputHandle(kind);
        using var fixture = LeaseFixture.Create(input);
        Switch2ProUsbReadClaim claim = Claim(1);

        Assert.ThrowsException<
            Switch2ProUsbWindowsReadStartAmbiguousException>(() =>
            fixture.Lease.TryBeginInputRead(new byte[64], 0, 64, claim,
                new RecordingTarget()));
        Assert.AreEqual(0, input.Operation?.ReleaseCalls ?? 0,
            "An unauthenticated operation must never be guessed quiescent.");
        Assert.IsFalse(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            Claim(2), new RecordingTarget()));
        Assert.AreEqual(1, input.BeginCalls);
        Assert.IsFalse(fixture.Lease.TryWaitForInputQuiescence(0));
        Assert.ThrowsException<InvalidOperationException>(
            fixture.Lease.DisposeQuiesced);
    }

    [TestMethod]
    public void Lease_CompletedOperationIsRetiredBeforeNextClaimStarts()
    {
        var input = new FakeInputHandle();
        using var fixture = LeaseFixture.Create(input);
        var target = new RecordingTarget();

        Switch2ProUsbReadClaim firstClaim = Claim(21);
        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            firstClaim, target));
        FakeInputHandle.FakeOperation first = input.LastOperation;
        input.Complete(Switch2ProUsbNativeReadStatus.Completed, 64);

        Assert.IsFalse(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            Claim(22), target),
            "Begin must not implicitly retire the previous native lease.");
        Assert.AreEqual(0, first.DisposeCalls);
        Assert.IsTrue(fixture.Lease.TryRetireCompletedInputRead(firstClaim, 0));
        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            Claim(22), target));
        Assert.AreEqual(1, first.DisposeCalls);
        input.Complete(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.AreEqual(2, target.Calls);
        Assert.IsTrue(fixture.Lease.TryWaitForInputQuiescence(0));
    }

    [TestMethod]
    public void Lease_RetirementFenceBlocksBeginAndDuplicateRetirement()
    {
        var input = new FakeInputHandle
        {
            BlockNativeWait = true,
        };
        input.AllowNativeWait.Reset();
        using var fixture = LeaseFixture.Create(input);
        var target = new RecordingTarget();
        Switch2ProUsbReadClaim first = Claim(23);
        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            first, target));
        input.Complete(Switch2ProUsbNativeReadStatus.Completed, 64);

        Task<bool> retirement = Task.Run(() =>
            fixture.Lease.TryRetireCompletedInputRead(first, 1_000));
        Assert.IsTrue(input.NativeWaitEntered.Wait(1_000));
        Assert.IsFalse(fixture.Lease.TryRetireCompletedInputRead(first, 0));
        Assert.IsFalse(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            Claim(24), target));

        input.AllowNativeWait.Set();
        Assert.IsTrue(retirement.GetAwaiter().GetResult());
        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            Claim(24), target));
        input.Complete(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.IsTrue(fixture.Lease.TryWaitForInputQuiescence(0));
    }

    [TestMethod]
    public void Lease_CancelMayQuiesceWithoutCallbackDuringExactRetirement()
    {
        var input = new FakeInputHandle
        {
            BlockNativeWait = true,
            CancelCompletesWithoutCallback = true,
        };
        input.AllowNativeWait.Reset();
        using var fixture = LeaseFixture.Create(input);
        var target = new RecordingTarget();
        Switch2ProUsbReadClaim cancelled = Claim(25);
        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            cancelled, target));

        Task<bool> retirement = Task.Run(() =>
            fixture.Lease.TryRetireCompletedInputRead(cancelled, 1_000));
        Assert.IsTrue(input.NativeWaitEntered.Wait(1_000));
        Assert.IsTrue(fixture.Lease.TryCancelInputRead(cancelled),
            "Exact cancellation must remain available while retirement waits.");
        input.AllowNativeWait.Set();
        Assert.IsTrue(retirement.GetAwaiter().GetResult());
        Assert.AreEqual(0, target.Calls);

        input.ReplayCompletion(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.AreEqual(0, target.Calls,
            "Native quiescence without callback permanently retires that epoch.");
        Assert.IsTrue(fixture.Lease.IsQuarantined,
            "A callback after exact native quiescence is a terminal contradiction.");
        Assert.IsFalse(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            Claim(26), target));
    }

    [TestMethod]
    public void Lease_RetirementCannotReleaseWhileExactCancelCallIsRunning()
    {
        var input = new FakeInputHandle
        {
            BlockCancel = true,
            CancelCompletesWithoutCallback = true,
        };
        input.AllowCancelReturn.Reset();
        using var fixture = LeaseFixture.Create(input);
        var target = new RecordingTarget();
        Switch2ProUsbReadClaim claim = Claim(27);
        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            claim, target));
        FakeInputHandle.FakeOperation operation = input.LastOperation;

        Task<bool> cancel = Task.Run(() =>
            fixture.Lease.TryCancelInputRead(claim));
        Assert.IsTrue(input.CancelEntered.Wait(1_000));
        Task<bool> retirement = Task.Run(() =>
            fixture.Lease.TryRetireCompletedInputRead(claim, 1_000));
        Assert.IsFalse(retirement.Wait(50),
            "Retirement must retain storage while exact cancel is in flight.");
        Assert.AreEqual(0, operation.DisposeCalls);
        Assert.IsFalse(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            Claim(28), target));

        input.AllowCancelReturn.Set();
        Assert.IsTrue(cancel.GetAwaiter().GetResult());
        Assert.IsTrue(retirement.GetAwaiter().GetResult());
        Assert.AreEqual(1, operation.DisposeCalls);
        Assert.AreEqual(0, target.Calls);
        input.ReplayCompletion(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.AreEqual(0, target.Calls);
    }

    [TestMethod]
    public void Lease_DeviceRemovalIsDeliveredOnceAndDuplicateIsSuppressed()
    {
        var input = new FakeInputHandle();
        using var fixture = LeaseFixture.Create(input);
        var target = new RecordingTarget();
        Switch2ProUsbReadClaim claim = Claim(3);

        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            claim, target));
        input.Complete(Switch2ProUsbNativeReadStatus.DeviceRemoved, 0);
        input.ReplayCompletion(Switch2ProUsbNativeReadStatus.Completed, 64);

        Assert.AreEqual(1, target.Calls);
        Assert.AreEqual(Switch2ProUsbNativeReadStatus.DeviceRemoved,
            target.LastStatus);
        Assert.IsTrue(fixture.Lease.IsQuarantined,
            "A duplicate native callback is suppressed and fail-closed.");
        Assert.IsFalse(fixture.Lease.TryWaitForInputQuiescence(0));
    }

    [TestMethod]
    public void Lease_CancellationCompletionRaceCompletesExactlyOnce()
    {
        var input = new FakeInputHandle
        {
            CompleteInsideCancel = true,
        };
        using var fixture = LeaseFixture.Create(input);
        var target = new RecordingTarget();
        Switch2ProUsbReadClaim claim = Claim(31);

        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            claim, target));
        Assert.IsTrue(fixture.Lease.TryCancelInputRead(claim));
        input.ReplayCompletion(Switch2ProUsbNativeReadStatus.Cancelled, 0);

        Assert.AreEqual(1, target.Calls);
        Assert.AreEqual(claim, target.LastClaim);
        Assert.AreEqual(Switch2ProUsbNativeReadStatus.Cancelled,
            target.LastStatus);
        Assert.IsTrue(fixture.Lease.IsQuarantined,
            "A duplicate cancellation completion is a terminal contradiction.");
        Assert.IsFalse(fixture.Lease.TryWaitForInputQuiescence(0));
    }

    [TestMethod]
    public void Lease_QuiescenceDoesNotPassRunningManagedCallback()
    {
        var input = new FakeInputHandle();
        using var fixture = LeaseFixture.Create(input);
        using var target = new BlockingTarget();
        Switch2ProUsbReadClaim claim = Claim(32);
        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            claim, target));

        Task completion = Task.Run(() => input.Complete(
            Switch2ProUsbNativeReadStatus.Cancelled, 0));
        Assert.IsTrue(target.Entered.Wait(1_000));
        Assert.IsFalse(fixture.Lease.TryWaitForInputQuiescence(0));
        target.Release.Set();
        Assert.IsTrue(completion.Wait(1_000));
        Assert.IsTrue(fixture.Lease.TryWaitForInputQuiescence(0));
    }

    [TestMethod]
    public void Lease_CancelWithoutCallbackQuiescesAndBlocksLateCompletion()
    {
        var input = new FakeInputHandle
        {
            CancelCompletesWithoutCallback = true,
        };
        using var fixture = LeaseFixture.Create(input);
        var target = new RecordingTarget();
        Switch2ProUsbReadClaim claim = Claim(4);

        Assert.IsTrue(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            claim, target));
        Assert.IsTrue(fixture.Lease.TryCancelInputRead(claim));
        Assert.IsTrue(fixture.Lease.TryWaitForInputQuiescence(0));

        input.ReplayCompletion(Switch2ProUsbNativeReadStatus.Completed, 64);
        Assert.AreEqual(0, target.Calls);
        Assert.IsFalse(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            Claim(5), target));
    }

    [TestMethod]
    public void Lease_DisposeRequiresProvenQuiescence()
    {
        var input = new FakeInputHandle();
        var fixture = LeaseFixture.Create(input);
        Assert.ThrowsException<InvalidOperationException>(
            fixture.Lease.DisposeQuiesced);
        Assert.AreEqual(0, input.DisposeCalls);
        Assert.IsTrue(fixture.Lease.TryWaitForInputQuiescence(0));
        fixture.Lease.DisposeQuiesced();
        Assert.AreEqual(1, input.DisposeCalls);
        Assert.AreEqual(1, fixture.Presence.DisposeCalls);
    }

    [TestMethod]
    public void Lease_InputDisposeFailureIsRetriedWithoutRedisposingPresence()
    {
        var input = new FakeInputHandle
        {
            DisposeFailuresRemaining = 1,
        };
        var fixture = LeaseFixture.Create(input);
        Assert.IsTrue(fixture.Lease.TryWaitForInputQuiescence(0));

        Assert.ThrowsException<InvalidOperationException>(
            fixture.Lease.DisposeQuiesced);
        Assert.AreEqual(1, input.DisposeCalls);
        Assert.AreEqual(1, fixture.Presence.DisposeCalls);
        Assert.IsFalse(fixture.Lease.TryBeginInputRead(new byte[64], 0, 64,
            Claim(80), new RecordingTarget()));

        fixture.Lease.DisposeQuiesced();
        Assert.AreEqual(2, input.DisposeCalls);
        Assert.AreEqual(1, fixture.Presence.DisposeCalls);
    }

    [TestMethod]
    public void Lease_PresenceDisposeFailureIsRetriedWithoutRedisposingInput()
    {
        var input = new FakeInputHandle();
        var fixture = LeaseFixture.Create(input);
        fixture.Presence.DisposeFailuresRemaining = 1;
        Assert.IsTrue(fixture.Lease.TryWaitForInputQuiescence(0));

        Assert.ThrowsException<InvalidOperationException>(
            fixture.Lease.DisposeQuiesced);
        Assert.AreEqual(1, input.DisposeCalls);
        Assert.AreEqual(1, fixture.Presence.DisposeCalls);

        fixture.Lease.DisposeQuiesced();
        Assert.AreEqual(1, input.DisposeCalls);
        Assert.AreEqual(2, fixture.Presence.DisposeCalls);
    }

    [TestMethod]
    public void PublicBoundaryExposesNoRawWindowsIdentityOrOutputCapability()
    {
        Type adapter = typeof(Switch2ProUsbWindowsAdapter);
        Assert.IsFalse(typeof(Switch2ProUsbWindowsCandidate).IsPublic);
        Assert.AreEqual(0, adapter.GetFields(BindingFlags.Public |
            BindingFlags.Instance | BindingFlags.DeclaredOnly).Length);
        foreach (PropertyInfo property in adapter.GetProperties(
                     BindingFlags.Public | BindingFlags.Instance |
                     BindingFlags.DeclaredOnly))
        {
            Assert.AreNotEqual(typeof(string), property.PropertyType);
            Assert.AreNotEqual(typeof(IntPtr), property.PropertyType);
            Assert.IsFalse(typeof(SafeHandle).IsAssignableFrom(
                property.PropertyType));
        }
        foreach (MethodInfo method in adapter.GetMethods(BindingFlags.Public |
                     BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.AreNotEqual(typeof(string), method.ReturnType);
            Assert.IsFalse(method.Name.Contains("Write",
                StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(method.Name.Contains("Feature",
                StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public void NativeOpenPolicyUsesRequiredWinUsbPresenceAccessAndSharing()
    {
        Assert.AreEqual(0x80000000u,
            Switch2ProUsbWindowsOpenPolicy.InputDesiredAccess);
        Assert.AreEqual(0x00000003u,
            Switch2ProUsbWindowsOpenPolicy.InputShareMode);
        Assert.AreEqual(0xC0000000u,
            Switch2ProUsbWindowsOpenPolicy.PresenceDesiredAccess);
        Assert.AreEqual(0x00000003u,
            Switch2ProUsbWindowsOpenPolicy.PresenceShareMode);
        Assert.AreEqual(0x40000000u,
            Switch2ProUsbWindowsOpenPolicy.OverlappedFlag);
        Assert.AreEqual(0u,
            Switch2ProUsbWindowsOpenPolicy.InputDesiredAccess & 0x40000000u);
        Assert.AreEqual(0x40000000u,
            Switch2ProUsbWindowsOpenPolicy.PresenceDesiredAccess & 0x40000000u);
    }

    [TestMethod]
    public void NativeCompletionErrorsMapRemovalAndCancellationExactly()
    {
        Assert.AreEqual(Switch2ProUsbNativeReadStatus.Completed,
            Switch2ProUsbWindowsReadStatusMap.FromNativeError(0));
        Assert.AreEqual(Switch2ProUsbNativeReadStatus.Cancelled,
            Switch2ProUsbWindowsReadStatusMap.FromNativeError(995));
        foreach (uint error in new uint[] { 31, 433, 1167 })
        {
            Assert.AreEqual(Switch2ProUsbNativeReadStatus.DeviceRemoved,
                Switch2ProUsbWindowsReadStatusMap.FromNativeError(error));
        }
        Assert.AreEqual(Switch2ProUsbNativeReadStatus.Failed,
            Switch2ProUsbWindowsReadStatusMap.FromNativeError(5));
    }

    [TestMethod]
    public void LeaseSteadyStateClaimAndCompletionAllocateNoManagedMemory()
    {
        var input = new ReusableInputHandle();
        using var fixture = LeaseFixture.Create(input);
        var target = new RecordingTarget();
        var buffer = new byte[64];
        var ownerFence = new object();

        bool succeeded = true;
        for (ulong sequence = 1; sequence <= 2_000; sequence++)
        {
            var claim = new Switch2ProUsbReadClaim(ownerFence, 10, 20,
                sequence);
            succeeded &= fixture.Lease.TryBeginInputRead(buffer, 0, 64,
                claim, target);
            succeeded &= fixture.Lease.TryRetireCompletedInputRead(claim, 0);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong sequence = 2_001; sequence <= 22_000; sequence++)
        {
            var claim = new Switch2ProUsbReadClaim(ownerFence, 10, 20,
                sequence);
            succeeded &= fixture.Lease.TryBeginInputRead(buffer, 0, 64,
                claim, target);
            succeeded &= fixture.Lease.TryRetireCompletedInputRead(claim, 0);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(22_000, target.Calls);
        Assert.AreEqual(0L, allocated,
            $"Windows lease steady state allocated {allocated} bytes.");
        Assert.IsTrue(fixture.Lease.TryWaitForInputQuiescence(0));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Switch2ProUsbWindowsReadOnlyCompositeLease>
        OpenLegacyAndDropAfterTerminalFailure(
            Switch2ProUsbWindowsReservationRegistry reservations,
            Switch2ProUsbWindowsCandidate candidate,
            out Switch2PhysicalInputRegistration registration)
    {
        var adapter = new Switch2ProUsbWindowsAdapter(
            new FakePlatform(new FakeInputHandle(),
                new FakePresenceHandle(), candidate, candidate, candidate),
            reservations);
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(
            observation, out registration, out _));
        Assert.IsTrue(adapter.TryOpenReadOnlyComposite(registration,
            out ISwitch2ProUsbReadOnlyCompositeLease opened));
        var lease = (Switch2ProUsbWindowsReadOnlyCompositeLease)opened;
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        Assert.ThrowsException<InvalidOperationException>(
            lease.DisposeQuiesced);
        return new WeakReference<Switch2ProUsbWindowsReadOnlyCompositeLease>(
            lease);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<
            Switch2ProUsbWindowsReadOnlyCompositeLease> Lease,
        WeakReference<SafeFileHandle> Handle,
        Switch2PhysicalInputRegistration Registration)
        OpenLegacyWithAmbiguousFileAndDrop(
            Switch2ProUsbWindowsReservationRegistry reservations,
            Switch2ProUsbWindowsCandidate candidate,
        CloseCounter closeCounter)
    {
        var input = new AmbiguousFileInputHandle(closeCounter);
        var platform = new FakePlatform(new FakeInputHandle(),
            new FakePresenceHandle(), candidate, candidate, candidate)
        {
            InputSelector = _ => input,
        };
        var adapter = new Switch2ProUsbWindowsAdapter(
            platform, reservations);
        Assert.IsTrue(adapter.TryObserveComposite(out var observation));
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(
            observation, out Switch2PhysicalInputRegistration registration,
            out _));
        Assert.IsTrue(adapter.TryOpenReadOnlyComposite(registration,
            out ISwitch2ProUsbReadOnlyCompositeLease opened));
        var lease = (Switch2ProUsbWindowsReadOnlyCompositeLease)opened;
        Assert.IsTrue(lease.TryWaitForInputQuiescence(0));
        Assert.ThrowsException<
            Switch2ProUsbWindowsCleanupAmbiguousException>(
            lease.DisposeQuiesced);
        return (new WeakReference<
                Switch2ProUsbWindowsReadOnlyCompositeLease>(lease),
            new WeakReference<SafeFileHandle>(input.File), registration);
    }

    private static Switch2ProUsbReadClaim Claim(ulong sequence) => new(
        new object(), 10, 20, sequence);

    private static Switch2ProUsbWindowsAdapter IsolatedAdapter(
        ISwitch2ProUsbWindowsPlatform platform) => new(platform,
        new Switch2ProUsbWindowsReservationRegistry());

    private static Switch2ProUsbWindowsCandidate Candidate(string suffix = "A",
        byte commandInterval = 0, ushort bcdDevice = 0x0201,
        byte matchingInputCount = 1, byte matchingCommandCount = 1,
        Guid? containerGuid = null)
    {
        Guid candidateContainer = containerGuid ??
            new Guid("E455F721-86CA-4CF3-91AF-BC7DD3552C93");
        Assert.IsTrue(Switch2PhysicalContainerIdentity.TryCreate(
            candidateContainer,
            out var container));
        var outPipe = new Switch2UsbPipeObservation(0x02,
            Switch2UsbPipeTransferType.Bulk, 64, commandInterval);
        var inPipe = new Switch2UsbPipeObservation(0x82,
            Switch2UsbPipeTransferType.Bulk, 64, 0);
        var hid = new Switch2UsbHidInterfaceObservation(container, 0, 0,
            Switch2UsbBoundDriver.HidClass, 0x01, 0x05, 64, 64, 0);
        var command = new Switch2UsbCommandInterfaceObservation(container, 1,
            0, Switch2UsbBoundDriver.WinUsb, 2, outPipe, inPipe);
        var observation = new Switch2ProUsbCompositeObservation(0x057E, 0x2069,
            bcdDevice, container, matchingInputCount, matchingCommandCount,
            hid, command);
        return new Switch2ProUsbWindowsCandidate(observation, 11,
            "private-hid-instance-" + suffix,
            "private-hid-parent-" + suffix,
            "private-hid-path-" + suffix, "HidUsb", 12,
            "private-command-instance-" + suffix,
            "private-command-path-" + suffix, "WinUSB");
    }

    private sealed class FakePlatform : ISwitch2ProUsbWindowsPlatform
    {
        private readonly Queue<IReadOnlyList<
            Switch2ProUsbWindowsCandidate>> discoveries;
        private readonly FakeInputHandle input;
        private readonly FakePresenceHandle presence;

        internal FakePlatform(FakeInputHandle input,
            FakePresenceHandle presence,
            params Switch2ProUsbWindowsCandidate[] discoveryResults)
        {
            this.input = input;
            this.presence = presence;
            discoveries = new Queue<IReadOnlyList<
                Switch2ProUsbWindowsCandidate>>(discoveryResults.Select(
                    candidate => candidate == null ?
                        Array.Empty<Switch2ProUsbWindowsCandidate>() :
                        new[] { candidate }));
        }

        private FakePlatform(FakeInputHandle input,
            FakePresenceHandle presence,
            IEnumerable<IReadOnlyList<Switch2ProUsbWindowsCandidate>>
                discoveryResults)
        {
            this.input = input;
            this.presence = presence;
            discoveries = new Queue<IReadOnlyList<
                Switch2ProUsbWindowsCandidate>>(discoveryResults);
        }

        internal static FakePlatform FromSnapshots(FakeInputHandle input,
            FakePresenceHandle presence,
            params IReadOnlyList<Switch2ProUsbWindowsCandidate>[] snapshots) =>
            new(input, presence, snapshots);

        internal int DiscoverCalls { get; private set; }
        internal int InputOpenCalls { get; private set; }
        internal int PresenceOpenCalls { get; private set; }
        internal bool RejectInputOpen { get; set; }
        internal Action<int> BeforeDiscover { get; set; }
        internal Func<Switch2ProUsbWindowsCandidate,
            ISwitch2ProUsbWindowsInputHandle> InputSelector { get; set; }
        internal Func<Switch2ProUsbWindowsCandidate,
            ISwitch2ProUsbWindowsPresenceHandle> PresenceSelector { get; set; }

        public bool TryDiscoverCandidates(out IReadOnlyList<
            Switch2ProUsbWindowsCandidate> candidates)
        {
            DiscoverCalls++;
            BeforeDiscover?.Invoke(DiscoverCalls);
            if (discoveries.Count == 0)
            {
                candidates = Array.Empty<Switch2ProUsbWindowsCandidate>();
                return false;
            }
            candidates = discoveries.Dequeue();
            return candidates != null && candidates.Count != 0;
        }

        public bool TryOpenInput(Switch2ProUsbWindowsCandidate candidate,
            out ISwitch2ProUsbWindowsInputHandle opened)
        {
            InputOpenCalls++;
            opened = RejectInputOpen ? null :
                InputSelector?.Invoke(candidate) ?? input;
            return opened != null;
        }

        public bool TryOpenPresence(Switch2ProUsbWindowsCandidate candidate,
            out ISwitch2ProUsbWindowsPresenceHandle opened)
        {
            PresenceOpenCalls++;
            opened = PresenceSelector?.Invoke(candidate) ?? presence;
            return opened != null;
        }
    }

    private sealed class CloseCounter
    {
        private int calls;

        internal int Calls => Volatile.Read(ref calls);

        internal void Increment() => Interlocked.Increment(ref calls);
    }

    private sealed class AmbiguousFileInputHandle :
        ISwitch2ProUsbWindowsInputHandle
    {
        private static readonly IntPtr ExactHandle = new(0x7ABC);
        private readonly CloseCounter closeCounter;

        internal AmbiguousFileInputHandle(CloseCounter closeCounter)
        {
            this.closeCounter = closeCounter;
            File = new SafeFileHandle(ExactHandle, ownsHandle: true);
        }

        internal SafeFileHandle File { get; }

        public bool TryBeginRead(byte[] destination, int offset, int count,
            Action<Switch2ProUsbWindowsReadCompletion> callback,
            out ISwitch2ProUsbWindowsReadOperation operation)
        {
            operation = null;
            return false;
        }

        public void DisposeQuiesced()
        {
            if (!Switch2ProUsbWindowsExactHandleRelease.
                    TryReleaseFileQuiesced(File, CloseAmbiguously))
            {
                throw new Switch2ProUsbWindowsCleanupAmbiguousException(
                    "Synthetic terminal CloseHandle ambiguity.", this);
            }
        }

        private bool CloseAmbiguously(IntPtr exact)
        {
            Assert.AreEqual(ExactHandle, exact);
            closeCounter.Increment();
            throw new InvalidOperationException(
                "Synthetic CloseHandle call crossed native code.");
        }
    }

    private sealed class FakeInputHandle : ISwitch2ProUsbWindowsInputHandle
    {
        private Action<Switch2ProUsbWindowsReadCompletion> callback;

        internal bool CompleteSynchronously { get; set; }
        internal bool CancelCompletesWithoutCallback { get; set; }
        internal bool CompleteInsideCancel { get; set; }
        internal bool CancelResult { get; set; } = true;
        internal bool BlockNativeWait { get; set; }
        internal bool BlockCancel { get; set; }
        internal ManualResetEventSlim NativeWaitEntered { get; } = new(false);
        internal ManualResetEventSlim AllowNativeWait { get; } = new(true);
        internal ManualResetEventSlim CancelEntered { get; } = new(false);
        internal ManualResetEventSlim AllowCancelReturn { get; } = new(true);
        internal FakeOperation LastOperation { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal int DisposeFailuresRemaining { get; set; }

        public bool TryBeginRead(byte[] destination, int offset, int count,
            Action<Switch2ProUsbWindowsReadCompletion> completion,
            out ISwitch2ProUsbWindowsReadOperation operation)
        {
            callback = completion;
            LastOperation = new FakeOperation(this);
            operation = LastOperation;
            if (CompleteSynchronously)
            {
                callback(new Switch2ProUsbWindowsReadCompletion(count, 123,
                    Switch2ProUsbNativeReadStatus.Completed));
                LastOperation.Quiescent = true;
            }
            return true;
        }

        internal void Complete(Switch2ProUsbNativeReadStatus status,
            int bytes)
        {
            callback(new Switch2ProUsbWindowsReadCompletion(bytes, 456,
                status));
            LastOperation.Quiescent = true;
        }

        internal void ReplayCompletion(Switch2ProUsbNativeReadStatus status,
            int bytes) => callback(new Switch2ProUsbWindowsReadCompletion(
                bytes, 789, status));

        public void DisposeQuiesced()
        {
            DisposeCalls++;
            if (DisposeFailuresRemaining > 0)
            {
                DisposeFailuresRemaining--;
                throw new InvalidOperationException("Injected input failure.");
            }
        }

        internal sealed class FakeOperation :
            ISwitch2ProUsbWindowsReadOperation
        {
            private readonly FakeInputHandle owner;

            internal FakeOperation(FakeInputHandle owner)
            {
                this.owner = owner;
            }

            internal bool Quiescent { get; set; }
            internal int CancelCalls { get; private set; }
            internal int DisposeCalls { get; private set; }

            public bool TryCancelExact()
            {
                CancelCalls++;
                if (owner.CancelCompletesWithoutCallback)
                {
                    Quiescent = true;
                }
                if (owner.BlockCancel)
                {
                    owner.CancelEntered.Set();
                    owner.AllowCancelReturn.Wait();
                }
                if (owner.CompleteInsideCancel)
                {
                    owner.Complete(Switch2ProUsbNativeReadStatus.Cancelled, 0);
                }
                return owner.CancelResult;
            }

            public bool TryWaitForNativeQuiescence(int timeoutMilliseconds)
            {
                if (owner.BlockNativeWait)
                {
                    owner.NativeWaitEntered.Set();
                    if (!owner.AllowNativeWait.Wait(timeoutMilliseconds))
                    {
                        return false;
                    }
                }
                return Quiescent;
            }

            public void ReleaseSubmissionQuiesced()
            {
                if (!Quiescent)
                {
                    throw new InvalidOperationException();
                }
                DisposeCalls++;
            }
        }
    }

    private sealed class FakePresenceHandle :
        ISwitch2ProUsbWindowsPresenceHandle
    {
        internal int DisposeCalls { get; private set; }
        internal int DisposeFailuresRemaining { get; set; }

        public void Dispose()
        {
            DisposeCalls++;
            if (DisposeFailuresRemaining > 0)
            {
                DisposeFailuresRemaining--;
                throw new InvalidOperationException(
                    "Injected presence failure.");
            }
        }
    }

    private enum ContradictoryReadStartKind
    {
        FalseWithOperation,
        TrueWithNull,
        ThrowWithOperation,
    }

    private sealed class ContradictoryInputHandle :
        ISwitch2ProUsbWindowsInputHandle
    {
        private readonly ContradictoryReadStartKind kind;

        internal ContradictoryInputHandle(
            ContradictoryReadStartKind kind)
        {
            this.kind = kind;
            Operation = kind == ContradictoryReadStartKind.TrueWithNull ?
                null : new ContradictoryOperation();
        }

        internal int BeginCalls { get; private set; }
        internal ContradictoryOperation Operation { get; }

        public bool TryBeginRead(byte[] destination, int offset, int count,
            Action<Switch2ProUsbWindowsReadCompletion> callback,
            out ISwitch2ProUsbWindowsReadOperation operation)
        {
            BeginCalls++;
            operation = Operation;
            if (kind == ContradictoryReadStartKind.ThrowWithOperation)
            {
                throw new InvalidOperationException(
                    "Synthetic ambiguous read start.");
            }
            return kind == ContradictoryReadStartKind.TrueWithNull;
        }

        public void DisposeQuiesced()
        {
        }

        internal sealed class ContradictoryOperation :
            ISwitch2ProUsbWindowsReadOperation
        {
            internal int ReleaseCalls { get; private set; }

            public bool TryCancelExact() => false;

            public bool TryWaitForNativeQuiescence(int timeoutMilliseconds) =>
                true;

            public void ReleaseSubmissionQuiesced() => ReleaseCalls++;
        }
    }

    private sealed class ReusableInputHandle :
        ISwitch2ProUsbWindowsInputHandle
    {
        private readonly ReusableOperation operation = new();

        public bool TryBeginRead(byte[] destination, int offset, int count,
            Action<Switch2ProUsbWindowsReadCompletion> callback,
            out ISwitch2ProUsbWindowsReadOperation started)
        {
            operation.Begin();
            started = operation;
            callback(new Switch2ProUsbWindowsReadCompletion(count, 123,
                Switch2ProUsbNativeReadStatus.Completed));
            operation.Complete();
            return true;
        }

        public void DisposeQuiesced()
        {
        }

        private sealed class ReusableOperation :
            ISwitch2ProUsbWindowsReadOperation
        {
            private bool quiescent = true;

            internal void Begin() => quiescent = false;

            internal void Complete() => quiescent = true;

            public bool TryCancelExact() => false;

            public bool TryWaitForNativeQuiescence(int timeoutMilliseconds) =>
                quiescent;

            public void ReleaseSubmissionQuiesced()
            {
                if (!quiescent)
                {
                    throw new InvalidOperationException();
                }
            }
        }
    }

    private sealed class RecordingTarget :
        ISwitch2ProUsbReadCompletionTarget
    {
        internal int Calls { get; private set; }
        internal Switch2ProUsbReadClaim LastClaim { get; private set; }
        internal Switch2ProUsbNativeReadStatus LastStatus { get; private set; }

        public Switch2ProUsbReadCompletionDisposition CompleteInputRead(
            in Switch2ProUsbReadClaim claim, int bytesTransferred,
            long completionTimestampQpc, Switch2ProUsbNativeReadStatus status)
        {
            Calls++;
            LastClaim = claim;
            LastStatus = status;
            return Switch2ProUsbReadCompletionDisposition.Published;
        }
    }

    private sealed class BlockingTarget :
        ISwitch2ProUsbReadCompletionTarget, IDisposable
    {
        internal ManualResetEventSlim Entered { get; } = new(false);
        internal ManualResetEventSlim Release { get; } = new(false);

        public Switch2ProUsbReadCompletionDisposition CompleteInputRead(
            in Switch2ProUsbReadClaim claim, int bytesTransferred,
            long completionTimestampQpc, Switch2ProUsbNativeReadStatus status)
        {
            Entered.Set();
            Release.Wait(1_000);
            return Switch2ProUsbReadCompletionDisposition.NativeCancelled;
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class LeaseFixture : IDisposable
    {
        private LeaseFixture(
            Switch2ProUsbWindowsReadOnlyCompositeLease lease,
            FakePresenceHandle presence)
        {
            Lease = lease;
            Presence = presence;
        }

        internal Switch2ProUsbWindowsReadOnlyCompositeLease Lease { get; }
        internal FakePresenceHandle Presence { get; }

        internal static LeaseFixture Create(
            ISwitch2ProUsbWindowsInputHandle input)
        {
            Switch2ProUsbWindowsCandidate candidate = Candidate();
            Assert.IsTrue(candidate.TryGetAdmittedRegistration(
                out var registration));
            var presence = new FakePresenceHandle();
            return new LeaseFixture(
                new Switch2ProUsbWindowsReadOnlyCompositeLease(registration,
                    input, presence), presence);
        }

        public void Dispose()
        {
            if (Lease.TryWaitForInputQuiescence(0))
            {
                Lease.DisposeQuiesced();
            }
        }
    }
}
