using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2ProUsbProductionCoordinatorTests
{
    [TestMethod]
    public void ReconciliationSelectsOnlyTheMissingPhysicalRegistration()
    {
        Assert.IsTrue(Candidate().TryGetAdmittedRegistration(out var registration));
        var exactToken = new InputControllerSlotToken(new object(), 0, 11, 13, default);
        var active = new Dictionary<Switch2PhysicalInputRegistration, InputControllerSlotToken>
        {
            [registration] = exactToken,
        };
        var present = new HashSet<Switch2PhysicalInputRegistration> { registration };
        Assert.AreEqual(0, Switch2ProUsbProductionCoordinator.FindMissingTokens(active, present).Length);
        present.Clear();
        CollectionAssert.AreEqual(new[] { exactToken },
            Switch2ProUsbProductionCoordinator.FindMissingTokens(active, present));
        active.Clear();
        Assert.AreEqual(0, Switch2ProUsbProductionCoordinator.FindMissingTokens(active, present).Length);
    }

    [TestMethod]
    public async Task ProvenUnownedOpenFailureIsRetriedAndStopsCleanly()
    {
        Switch2ProUsbWindowsCandidate candidate = Candidate();
        var discovery = new FakeDiscovery(candidate);
        var opener = new RejectingOpener();
        var registrations = new Switch2RuntimeRegistrationService(
            new InputControllerRegistrationTable(1));
        var coordinator = new Switch2ProUsbProductionCoordinator(discovery,
            opener, registrations, new UnexpectedHost(), attached: null,
            diagnostic: null, scanIntervalMilliseconds: 5);

        Assert.IsTrue(coordinator.TryStart(41));
        Assert.IsTrue(opener.SecondAttempt.Wait(2_000),
            "A proven no-owner open rejection must be eligible for a later discovery retry without requiring a physical unplug.");
        Assert.IsTrue(await coordinator.StopAsync());
        Assert.IsTrue(opener.AttemptCount >= 2);
        Assert.IsTrue(coordinator.TryStart(42),
            "A clean stop must release the coordinator generation.");
        Assert.IsTrue(await coordinator.StopAsync());
    }

    [TestMethod]
    public void NonpositiveScanIntervalIsRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2ProUsbProductionCoordinator(
                new FakeDiscovery(Candidate()), new RejectingOpener(),
                new Switch2RuntimeRegistrationService(
                    new InputControllerRegistrationTable(1)),
                new UnexpectedHost(), attached: null, diagnostic: null,
                scanIntervalMilliseconds: 0));
    }

    private static Switch2ProUsbWindowsCandidate Candidate()
    {
        Assert.IsTrue(Switch2PhysicalContainerIdentity.TryCreate(
            new Guid("7AE24EB1-52D2-40D8-9256-F4F268099034"),
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
            "private-hid-instance", "private-hid-parent",
            "private-hid-path", "HidUsb", 19,
            "private-command-instance", "private-command-path", "WinUSB");
    }

    private sealed class FakeDiscovery :
        ISwitch2ProUsbWindowsOwnedCompositePlatform
    {
        private readonly IReadOnlyList<Switch2ProUsbWindowsCandidate>
            candidates;

        internal FakeDiscovery(Switch2ProUsbWindowsCandidate candidate)
        {
            candidates = new[] { candidate };
        }

        public bool TryDiscoverCandidates(
            out IReadOnlyList<Switch2ProUsbWindowsCandidate> candidates)
        {
            candidates = this.candidates;
            return true;
        }

        public bool TryOpenOwnedHid(Switch2ProUsbWindowsCandidate candidate,
            out ISwitch2ProUsbWindowsOwnedHidHandle hid) =>
            throw new InvalidOperationException("Discovery-only fake.");

        public bool TryOpenOwnedCommand(
            Switch2ProUsbWindowsCandidate candidate,
            out ISwitch2ProUsbWindowsOwnedCommandHandle command) =>
            throw new InvalidOperationException("Discovery-only fake.");

        public bool TryRevalidateOwnedCandidate(
            Switch2ProUsbWindowsCandidate expected) =>
            throw new InvalidOperationException("Discovery-only fake.");
    }

    private sealed class RejectingOpener :
        ISwitch2ProUsbOwnedCompositeNativeAdapter
    {
        private int attemptCount;

        internal ManualResetEventSlim SecondAttempt { get; } = new(false);

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        public bool TryOpenOwnedComposite(
            in Switch2PhysicalInputRegistration registration,
            in Switch2PhysicalInputLifetime lifetime,
            out ISwitch2ProUsbOwnedCompositeLease lease)
        {
            if (Interlocked.Increment(ref attemptCount) >= 2)
            {
                SecondAttempt.Set();
            }
            lease = null;
            return false;
        }
    }

    private sealed class UnexpectedHost : ISwitch2ControlServiceSlotHost
    {
        public Switch2ControlServiceSlotHostResult TryPrepare(
            in Switch2ControlServiceSlotLease lease) =>
            throw new InvalidOperationException("Host must not be called.");

        public Switch2ControlServiceSlotHostResult TryDispatch(
            in Switch2ControlServiceSlotLease lease, DS4Device sender,
            Switch2RuntimeReportEventArgs report) =>
            throw new InvalidOperationException("Host must not be called.");

        public Switch2ControlServiceSlotHostResult TryAbort(
            in Switch2ControlServiceSlotLease lease) =>
            throw new InvalidOperationException("Host must not be called.");

        public Switch2ControlServiceSlotHostResult TryRemove(
            in Switch2ControlServiceSlotLease lease) =>
            throw new InvalidOperationException("Host must not be called.");
    }
}
