using System.Reflection;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothProductionCoordinatorTests
{
    [TestMethod]
    public async Task CompletedNullOpenOffersOneFreshRememberedRetryAndStopRetiresIt()
    {
        var watcher = new FakeWatcher();
        var rejected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = CreateCoordinator(new FakePlatform(watcher) { ReturnNullDevice = true },
            message =>
            {
                if (message.Contains("Bluetooth open rejected")) rejected.TrySetResult(true);
            });
        Assert.IsTrue(coordinator.TryStart(1, LocalHost, out _));
        watcher.EmitRememberedPro(1);
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, coordinator.GetAssociationCandidates().Length);
        watcher.EmitRememberedPro(2);
        var candidate = coordinator.GetAssociationCandidates().Single();
        Assert.IsTrue(candidate.IsRemembered);
        watcher.EmitRememberedPro(3);
        Assert.AreEqual(1, coordinator.GetAssociationCandidates().Length);
        var result = await coordinator.AssociateAsync(candidate.Id);
        Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.DeviceOpenFailed, result.Failure);
        Assert.AreEqual(default(Switch2BluetoothAssociationStep), result.LastCompletedStep);
        watcher.EmitRememberedPro(4);
        int retryId = coordinator.GetAssociationCandidates().Single().Id;
        Assert.IsTrue(await coordinator.StopAsync());
        Assert.AreEqual(0, coordinator.GetAssociationCandidates().Length);
        Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.InvalidObservation,
            (await coordinator.AssociateAsync(retryId)).Failure);
    }

    [DataTestMethod]
    [DataRow((int)Switch2BluetoothRuntimeOwnerState.Created, false, null)]
    [DataRow((int)Switch2BluetoothRuntimeOwnerState.AbortedUnpublished, false, "transaction-complete")]
    [DataRow((int)Switch2BluetoothRuntimeOwnerState.Removed, false, "transaction-complete")]
    [DataRow((int)Switch2BluetoothRuntimeOwnerState.Active, false, "retained-Active")]
    [DataRow((int)Switch2BluetoothRuntimeOwnerState.Created, true, "quarantined")]
    [DataRow((int)Switch2BluetoothRuntimeOwnerState.AbortedUnpublished, true, "quarantined")]
    [DataRow((int)Switch2BluetoothRuntimeOwnerState.Quarantined, true, "quarantined")]
    public void RejectedActivationDoesNotAbortCompletedOrQuarantinedOwnership(
        int state, bool quarantine, string expected)
    {
        Assert.AreEqual(expected,
            Switch2BluetoothProductionCoordinator.CleanupAfterAttachRejection(
                (Switch2BluetoothRuntimeOwnerState)state, quarantine));
    }

    [TestMethod]
    public async Task InvalidAssociationRequestLogsItsResultWithoutOpeningDevice()
    {
        var messages = new List<string>();
        var coordinator = CreateCoordinator(new FakePlatform(new FakeWatcher()), messages.Add);
        var result = await coordinator.AssociateAsync(123);
        Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.InvalidObservation, result.Failure);
        CollectionAssert.AreEqual(new[]
        {
            "Switch 2 Bluetooth association requested.",
            "Switch 2 Bluetooth association failed: InvalidObservation; last completed command: 0.",
        }, messages);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AssociationLogsTypedFailureWithoutAddressesOrLoggerAffectingResult(bool loggerThrows)
    {
        var messages = new List<string>();
        var watcher = new FakeWatcher();
        var coordinator = CreateCoordinator(new FakePlatform(watcher) { ReturnNullDevice = true },
            message =>
            {
                messages.Add(message);
                if (loggerThrows && message.StartsWith("Switch 2 Bluetooth association"))
                    throw new InvalidOperationException("Diagnostic sink failed.");
            });
        Assert.IsTrue(coordinator.TryStart(1, LocalHost, out _));
        watcher.EmitUnassociatedPro();
        var candidates = coordinator.GetAssociationCandidates();
        Assert.AreEqual(1, candidates.Length);
        Assert.IsFalse(candidates[0].IsRemembered);
        messages.Clear();
        try
        {
            var result = await coordinator.AssociateAsync(candidates[0].Id);
            Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.DeviceOpenFailed, result.Failure);
            CollectionAssert.AreEqual(new[]
            {
                "Switch 2 Bluetooth association requested.",
                "Switch 2 Bluetooth association failed: DeviceOpenFailed; last completed command: 0.",
            }, messages);
        }
        finally
        {
            Assert.IsTrue(await coordinator.StopAsync());
        }
    }

    [TestMethod]
    public async Task StopAfterGateTimeoutDoesNotReleaseAnotherOperationsOwnership()
    {
        var (coordinator, watcher) = CreateStartedCoordinator();
        SemaphoreSlim gate = GetConnectionGate(coordinator);
        await gate.WaitAsync();
        try
        {
            // Hold the real production gate through its bounded timeout. No
            // synthetic wait implementation can hide a discarded bool result.
            bool stopped = await coordinator.StopAsync();

            Assert.IsFalse(stopped,
                "A stop that never acquired the connection gate is incomplete.");
            Assert.AreEqual(Switch2BluetoothDiscoveryState.Stopping,
                coordinator.GetDiscoveryStatus().State);
            Assert.IsFalse(coordinator.GetDiscoveryStatus().CanAssociate);
            Assert.AreEqual(0, gate.CurrentCount,
                "Timeout must not release the operation that still owns the gate.");
            Assert.IsTrue(watcher.Disposed,
                "Scanning still retires even when connection cleanup is incomplete.");
            using var retryCancellation = new CancellationTokenSource(100);
            Assert.IsFalse(await coordinator.StopAsync(retryCancellation.Token),
                "Retry must observe the incomplete stop, not report success.");
            Assert.IsFalse(coordinator.TryStart(2, LocalHost, out _),
                "A successor scan cannot replace an outstanding gate owner.");
        }
        finally
        {
            // Keep a red regression from obscuring its assertion with a second
            // SemaphoreFullException during fixture cleanup.
            if (gate.CurrentCount == 0)
                gate.Release();
        }
        Assert.IsTrue(await coordinator.StopAsync(),
            "The same cleanup must complete after its gate owner leaves.");
    }

    [TestMethod]
    public async Task StopWithFreeGateAcquiresAndReleasesExactlyOnce()
    {
        var (coordinator, watcher) = CreateStartedCoordinator();
        SemaphoreSlim gate = GetConnectionGate(coordinator);

        Assert.IsTrue(await coordinator.StopAsync());
        Assert.IsTrue(watcher.Disposed);
        Assert.AreEqual(1, gate.CurrentCount);
    }

    [TestMethod]
    public async Task CancelledStopDoesNotReleaseAnotherOperationsOwnership()
    {
        var (coordinator, _) = CreateStartedCoordinator();
        SemaphoreSlim gate = GetConnectionGate(coordinator);
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            Assert.IsFalse(await coordinator.StopAsync(cancellation.Token));
            Assert.AreEqual(0, gate.CurrentCount);
        }
        finally
        {
            if (gate.CurrentCount == 0)
                gate.Release();
        }
    }

    [TestMethod]
    public async Task ConcurrentStopsObserveOneWatcherDrainAndPermitCleanRestart()
    {
        var (coordinator, watcher) = CreateStartedCoordinator();
        watcher.HoldDrain = true;
        Task<bool> first = coordinator.StopAsync().AsTask();
        await watcher.DrainEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<bool> second = coordinator.StopAsync().AsTask();
        try
        {
            Assert.IsFalse(second.IsCompleted,
                "A second stop must await the existing watcher cleanup.");
            Assert.IsFalse(coordinator.TryStart(2, LocalHost, out _));
            Assert.AreEqual(1, watcher.DetachCount);
        }
        finally
        {
            watcher.ReleaseDrain.TrySetResult(true);
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.IsTrue(first.Result);
        Assert.IsTrue(second.Result);
        Assert.IsTrue(watcher.Disposed);
        Assert.IsTrue(coordinator.TryStart(2, LocalHost, out var failure),
            failure.ToString());
        Assert.IsTrue(await coordinator.StopAsync());
    }

    [TestMethod]
    public async Task CancelledStopObservationDoesNotAbandonWatcherCleanup()
    {
        var (coordinator, watcher) = CreateStartedCoordinator();
        watcher.HoldDrain = true;
        using var cancellation = new CancellationTokenSource();
        Task<bool> first = coordinator.StopAsync(cancellation.Token).AsTask();
        await watcher.DrainEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        Assert.IsFalse(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        try
        {
            Assert.IsFalse(coordinator.TryStart(2, LocalHost, out _),
                "Cancelling a wait must not release the scan's cleanup fence.");
            using var retryCancellation = new CancellationTokenSource(100);
            Assert.IsFalse(await coordinator.StopAsync(retryCancellation.Token));
            Assert.AreEqual(1, watcher.DetachCount);
        }
        finally
        {
            watcher.ReleaseDrain.TrySetResult(true);
        }
        Assert.IsTrue(await coordinator.StopAsync());
        Assert.IsTrue(watcher.Disposed);
    }

    private static readonly byte[] LocalHost =
        { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };

    [TestMethod]
    public async Task FailedStartStopWaitsForPartiallyAttachedWatcherCleanup()
    {
        var watcher = new FakeWatcher { ThrowOnAttach = true, HoldDrain = true };
        var coordinator = CreateCoordinator(new FakePlatform(watcher));
        Assert.IsFalse(coordinator.TryStart(1, LocalHost, out var failure));
        Assert.AreEqual(Switch2BluetoothWindowsScanStartFailure.
            WatcherConfigurationFailed, failure);
        await watcher.DrainEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            using var cancellation = new CancellationTokenSource(100);
            Assert.IsFalse(await coordinator.StopAsync(cancellation.Token),
                "A failed Start may still own installed native handlers.");
            Assert.IsFalse(coordinator.TryStart(2, LocalHost, out _));
            Assert.AreEqual(1, watcher.DetachCount);
            Assert.IsFalse(watcher.Disposed);
        }
        finally
        {
            watcher.ReleaseDrain.TrySetResult(true);
        }
        Assert.IsTrue(await coordinator.StopAsync());
        Assert.IsTrue(watcher.Disposed);
        Assert.IsTrue(coordinator.TryStart(3, LocalHost, out failure), failure.ToString());
        Assert.IsTrue(await coordinator.StopAsync());
    }

    [TestMethod]
    public async Task FailedStartDisposeErrorCannotBecomeSuccessfulStop()
    {
        var watcher = new FakeWatcher { ThrowOnStart = true, ThrowOnDispose = true };
        var coordinator = CreateCoordinator(new FakePlatform(watcher));
        Assert.IsFalse(coordinator.TryStart(1, LocalHost, out _));
        Assert.IsFalse(await coordinator.StopAsync(),
            "Disposal failure must survive the unsuccessful Start result.");
        Assert.AreEqual(Switch2BluetoothDiscoveryState.CleanupFailed,
            coordinator.GetDiscoveryStatus().State);
        Assert.IsFalse(await coordinator.StopAsync());
        Assert.IsFalse(coordinator.TryStart(2, LocalHost, out _));
        Assert.AreEqual(1, watcher.DetachCount);
    }

    [TestMethod]
    public async Task CleanFailedWatcherStartCanStopAndRestart()
    {
        var watcher = new FakeWatcher { ThrowOnStart = true };
        var coordinator = CreateCoordinator(new FakePlatform(watcher));
        Assert.IsFalse(coordinator.TryStart(1, LocalHost, out _));
        Assert.IsTrue(await coordinator.StopAsync());
        Assert.IsTrue(watcher.Disposed);
        Assert.AreEqual(Switch2BluetoothDiscoveryState.StartFailed,
            coordinator.GetDiscoveryStatus().State);
        Assert.AreEqual(Switch2BluetoothWindowsScanStartFailure.WatcherStartFailed,
            coordinator.GetDiscoveryStatus().Failure);
        Assert.IsTrue(coordinator.TryStart(2, LocalHost, out var failure), failure.ToString());
        Assert.AreEqual(Switch2BluetoothDiscoveryState.Scanning,
            coordinator.GetDiscoveryStatus().State);
        Assert.IsTrue(await coordinator.StopAsync());
    }

    [TestMethod]
    public async Task StopDuringFailedWatcherCreationCanProveNoResourceWasCreated()
    {
        var watcher = new FakeWatcher();
        using var createEntered = new ManualResetEventSlim();
        using var releaseCreate = new ManualResetEventSlim();
        bool first = true;
        var platform = new FakePlatform(watcher)
        {
            CreateAction = () =>
            {
                if (!first) return;
                first = false;
                createEntered.Set();
                releaseCreate.Wait();
                throw new InvalidOperationException("No watcher was returned.");
            },
        };
        var coordinator = CreateCoordinator(platform);
        Task<bool> start = Task.Run(() => coordinator.TryStart(1, LocalHost, out _));
        try
        {
            Assert.IsTrue(createEntered.Wait(TimeSpan.FromSeconds(2)));
            using var cancellation = new CancellationTokenSource(100);
            Assert.IsFalse(await coordinator.StopAsync(cancellation.Token));
        }
        finally
        {
            releaseCreate.Set();
        }
        Assert.IsFalse(await start.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(await coordinator.StopAsync(),
            "An explicit no-resource start outcome can complete Stop.");
        Assert.IsTrue(coordinator.TryStart(2, LocalHost, out var failure), failure.ToString());
        Assert.IsTrue(await coordinator.StopAsync());
    }

    [TestMethod]
    public async Task StopDuringWatcherCreationDrainsThePublishedStartAttempt()
    {
        var watcher = new FakeWatcher();
        using var createEntered = new ManualResetEventSlim();
        using var releaseCreate = new ManualResetEventSlim();
        var platform = new FakePlatform(watcher)
        {
            CreateAction = () =>
            {
                createEntered.Set();
                releaseCreate.Wait();
            },
        };
        var coordinator = CreateCoordinator(platform);
        Task<bool> start = Task.Run(() => coordinator.TryStart(1,
            LocalHost, out _));
        try
        {
            Assert.IsTrue(createEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(Switch2BluetoothDiscoveryState.Starting,
                coordinator.GetDiscoveryStatus().State);
            using var cancellation = new CancellationTokenSource(100);
            Assert.IsFalse(await coordinator.StopAsync(cancellation.Token));
            Assert.IsFalse(coordinator.TryStart(2, LocalHost, out _));
        }
        finally
        {
            releaseCreate.Set();
        }
        Assert.IsFalse(await start.WaitAsync(TimeSpan.FromSeconds(2)),
            "A start retired by Stop cannot publish success afterward.");
        Assert.IsTrue(await coordinator.StopAsync());
        Assert.IsTrue(watcher.Disposed,
            "Stop must include a watcher created after the stop request.");
    }

    [TestMethod]
    public async Task StopObservesWatcherWhichAlreadyStoppedItself()
    {
        var (coordinator, watcher) = CreateStartedCoordinator();
        watcher.HoldDrain = true;
        watcher.EmitStopped();
        Assert.AreEqual(Switch2BluetoothDiscoveryState.Interrupted,
            coordinator.GetDiscoveryStatus().State);
        await watcher.DrainEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<bool> stop = coordinator.StopAsync().AsTask();
        try
        {
            Assert.IsFalse(stop.IsCompleted);
            Assert.IsFalse(coordinator.TryStart(2, LocalHost, out _));
        }
        finally
        {
            watcher.ReleaseDrain.TrySetResult(true);
        }
        Assert.IsTrue(await stop);
        Assert.AreEqual(1, watcher.DetachCount);
        Assert.IsTrue(watcher.Disposed);
    }

    [TestMethod]
    public async Task EmptyScanStatusDoesNotClaimControllersAreReady()
    {
        var coordinator = CreateCoordinator(new FakePlatform(new FakeWatcher()));
        Assert.AreEqual(Switch2BluetoothDiscoveryState.Stopped,
            coordinator.GetDiscoveryStatus().State);
        Assert.IsTrue(coordinator.TryStart(1, LocalHost, out _));
        Assert.AreEqual(Switch2BluetoothDiscoveryState.Scanning,
            coordinator.GetDiscoveryStatus().State);
        Assert.IsTrue(coordinator.GetDiscoveryStatus().CanAssociate);
        Assert.AreEqual(0, coordinator.GetAssociationCandidates().Length);
        Assert.IsTrue(await coordinator.StopAsync());
        Assert.AreEqual(Switch2BluetoothDiscoveryState.Stopped,
            coordinator.GetDiscoveryStatus().State);
        Assert.IsFalse(coordinator.GetDiscoveryStatus().CanAssociate);
    }

    private static (Switch2BluetoothProductionCoordinator Coordinator,
        FakeWatcher Watcher) CreateStartedCoordinator()
    {
        var watcher = new FakeWatcher();
        var coordinator = CreateCoordinator(new FakePlatform(watcher));
        Assert.IsTrue(coordinator.TryStart(1, LocalHost,
            out var failure), failure.ToString());
        return (coordinator, watcher);
    }

    private static Switch2BluetoothProductionCoordinator CreateCoordinator(
        FakePlatform platform, Action<string> diagnostic = null)
    {
        var adapter = new Switch2BluetoothWindowsAdapter(
            platform, new Switch2BluetoothCandidateRegistry(),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        return new Switch2BluetoothProductionCoordinator(adapter,
            new Switch2RuntimeRegistrationService(
                new InputControllerRegistrationTable(1)),
            new UnusedSlotHost(), null, diagnostic);
    }

    private static SemaphoreSlim GetConnectionGate(
        Switch2BluetoothProductionCoordinator coordinator) =>
        (SemaphoreSlim)typeof(Switch2BluetoothProductionCoordinator).GetField(
            "connectionGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator)!;

    private sealed class FakePlatform(FakeWatcher watcher) :
        ISwitch2BluetoothWindowsPlatform
    {
        private bool first = true;
        internal Action CreateAction { get; init; }
        internal bool ReturnNullDevice { get; init; }
        public ISwitch2BluetoothWindowsAdvertisementWatcher
            CreateAdvertisementWatcher()
        {
            CreateAction?.Invoke();
            if (!first) return new FakeWatcher();
            first = false;
            return watcher;
        }

        public ValueTask<ISwitch2BluetoothWindowsDevice> OpenDeviceAsync(
            ulong address, Switch2BluetoothWindowsAddressType addressType,
            CancellationToken cancellationToken) =>
            ReturnNullDevice ? ValueTask.FromResult<ISwitch2BluetoothWindowsDevice>(null) :
                throw new AssertFailedException("No hardware open is expected.");
    }

    private sealed class FakeWatcher :
        ISwitch2BluetoothWindowsAdvertisementWatcher
    {
        private Switch2BluetoothWindowsWatcherStoppedHandler stopped;
        private Switch2BluetoothWindowsAdvertisementHandler received;
        public bool IsConfiguredForActiveScanning { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool HoldDrain { get; set; }
        internal bool ThrowOnAttach { get; init; }
        internal bool ThrowOnStart { get; init; }
        internal bool ThrowOnDispose { get; init; }
        internal int DetachCount { get; private set; }
        internal TaskCompletionSource<bool> DrainEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseDrain { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ConfigureActiveScanning() =>
            IsConfiguredForActiveScanning = true;
        public void AttachHandlers(
            Switch2BluetoothWindowsAdvertisementHandler received,
            Switch2BluetoothWindowsWatcherStoppedHandler stopped)
        {
            this.stopped = stopped;
            this.received = received;
            if (ThrowOnAttach)
                throw new InvalidOperationException("Partial native handler setup.");
        }
        internal void EmitStopped() => stopped?.Invoke();
        internal void EmitUnassociatedPro() => received?.Invoke(
            0x102030405060, Switch2BluetoothWindowsAddressType.Public,
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, 1,
            Convert.FromHexString("0100037E0569200001000000000000000F00000000000000"), 1);
        internal void EmitRememberedPro(long qpc) => received?.Invoke(
            0x102030405060, Switch2BluetoothWindowsAddressType.Public,
            Switch2AdvertisementCodec.NintendoBluetoothCompanyId, 1,
            Convert.FromHexString("0100037E0569200001006655443322110F00000000000000"), qpc);
        public void Start()
        {
            if (ThrowOnStart)
                throw new InvalidOperationException("Watcher Start failed.");
        }
        public void Stop() { }
        public Task DetachHandlersAndDrainAsync()
        {
            DetachCount++;
            DrainEntered.TrySetResult(true);
            return HoldDrain ? ReleaseDrain.Task : Task.CompletedTask;
        }
        public void Dispose()
        {
            if (ThrowOnDispose)
                throw new InvalidOperationException("Watcher Dispose failed.");
            Disposed = true;
        }
    }

    private sealed class UnusedSlotHost : ISwitch2ControlServiceSlotHost
    {
        public Switch2ControlServiceSlotHostResult TryPrepare(
            in Switch2ControlServiceSlotLease lease) => throw UnexpectedCall();
        public Switch2ControlServiceSlotHostResult TryDispatch(
            in Switch2ControlServiceSlotLease lease, DS4Device sender,
            Switch2RuntimeReportEventArgs report) => throw UnexpectedCall();
        public Switch2ControlServiceSlotHostResult TryAbort(
            in Switch2ControlServiceSlotLease lease) => throw UnexpectedCall();
        public Switch2ControlServiceSlotHostResult TryRemove(
            in Switch2ControlServiceSlotLease lease) => throw UnexpectedCall();
        private static AssertFailedException UnexpectedCall() =>
            new("An empty scan must not invoke a controller slot operation.");
    }
}
