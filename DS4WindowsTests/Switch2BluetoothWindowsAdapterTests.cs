using System.Buffers.Binary;
using System.Reflection;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothWindowsAdapterTests
{
    private static readonly byte[] LocalHost =
        { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task RejectedSlotOffersExplicitRememberedReconnectOnlyAfterCleanRelease(
        bool ambiguousRelease, bool uncertainHostCleanup)
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var first = CreateCalibrationResponder();
        var second = CreateCalibrationResponder();
        first.ThrowOnDispose = ambiguousRelease;
        platform.EnqueueDevice(first);
        platform.EnqueueDevice(second);
        var table = new InputControllerRegistrationTable(2);
        var registrationService = new Switch2RuntimeRegistrationService(table);
        Assert.IsTrue(registrationService.TryOpen(1, out _));
        var host = new RejectingSlotHost { UncertainCleanup = uncertainHostCleanup };
        var rejected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new Switch2BluetoothProductionCoordinator(
            CreateAdapter(platform, new Switch2BluetoothCandidateRegistry()),
            registrationService, host, null,
            message =>
            {
                if (message.Contains("slot activation rejected")) rejected.TrySetResult(true);
            });
        Assert.IsTrue(coordinator.TryStart(1, LocalHost, out _));
        const ulong address = 0x100000001171;
        byte[] advertisement = BuildAdvertisement(Switch2AdvertisementCodec.ProController2ProductId);
        Task<Switch2BluetoothWindowsAssociationResult> racedRetry = null;
        if (uncertainHostCleanup)
        {
            host.AfterPhysicalAbort = () =>
            {
                Assert.IsTrue(first.Disposed, "The physical lease released before external host cleanup failed.");
                watcher.Emit(address, advertisement, 2);
                var earlyChoice = coordinator.GetAssociationCandidates().Single();
                racedRetry = coordinator.AssociateAsync(earlyChoice.Id).AsTask();
            };
        }
        try
        {
            watcher.Emit(address, advertisement, 1);
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, host.PrepareCalls);
            Assert.AreEqual(0, coordinator.GetAssociationCandidates().Length,
                "Cleanup does not invent a new advertisement capability.");
            watcher.Emit(address, advertisement, 3);
            var candidates = coordinator.GetAssociationCandidates();
            Assert.AreEqual(ambiguousRelease || uncertainHostCleanup ? 0 : 1, candidates.Length,
                "Only a proven release may offer a remembered reconnect row.");
            Assert.AreEqual(1, platform.OpenedAddresses.Count,
                "Advertising again must not create an automatic failed-open loop.");
            if (uncertainHostCleanup)
            {
                Assert.IsNotNull(racedRetry);
                Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.CleanupAmbiguous,
                    (await racedRetry.WaitAsync(TimeSpan.FromSeconds(2))).Failure,
                    "A retry queued during physical release cannot overlap uncertain host rollback.");
                Assert.AreEqual(1, platform.OpenedAddresses.Count);
                Assert.AreEqual(1, table.GetSnapshot().Count(slot => slot.State == InputControllerSlotState.Quarantined));
            }
            if (!ambiguousRelease && !uncertainHostCleanup)
            {
                Assert.IsTrue(candidates[0].IsRemembered);
                var result = await coordinator.AssociateAsync(candidates[0].Id);
                Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.SlotActivationRejected,
                    result.Failure, "Reaching BLE is not a successful controller-slot activation.");
                Assert.AreEqual(default(Switch2BluetoothAssociationStep), result.LastCompletedStep);
                Assert.AreEqual(2, host.PrepareCalls);
                Assert.AreEqual(2, platform.OpenedAddresses.Count);
                Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.InvalidObservation,
                    (await coordinator.AssociateAsync(candidates[0].Id)).Failure,
                    "An already-consumed Settings selection cannot reconnect twice.");
                watcher.Emit(address, advertisement, 4);
                Assert.AreEqual(1, coordinator.GetAssociationCandidates().Length);
                Assert.AreEqual(2, platform.OpenedAddresses.Count);
            }
        }
        finally
        {
            await coordinator.StopAsync();
        }
        Assert.AreEqual(0, coordinator.GetAssociationCandidates().Length);
    }

    private static FakeDevice CreateCalibrationResponder()
    {
        var device = FakeDevice.ValidDuplexPro();
        device.Service.CommandCharacteristic.WriteOverride = (request, _, _) =>
        {
            byte[] bytes = request.ToArray();
            Assert.AreNotEqual((byte)0x15, bytes[0],
                "A remembered reconnect must never rewrite controller association.");
            if (bytes[0] == Switch2BluetoothMemoryReadCodec.CommandId)
            {
                int length = bytes[8];
                var response = new byte[16 + length];
                response[0] = bytes[0];
                response[1] = 1;
                response[8] = (byte)length;
                bytes.AsSpan(12, 4).CopyTo(response.AsSpan(12));
                if (length == 9)
                    Convert.FromHexString("000880000770000770").CopyTo(response, 16);
                else
                    response.AsSpan(16).Fill(0xFF);
                device.Service.ResponseCharacteristic.Emit(response, 1);
            }
            else
            {
                device.Service.ResponseCharacteristic.Emit(
                    Convert.FromHexString("0901000000000000"), 1);
            }
            return ValueTask.FromResult(true);
        };
        return device;
    }

    private sealed class RejectingSlotHost : ISwitch2ControlServiceSlotHost
    {
        internal int PrepareCalls { get; private set; }
        internal bool UncertainCleanup { get; init; }
        internal Action AfterPhysicalAbort { get; set; }
        public Switch2ControlServiceSlotHostResult TryPrepare(in Switch2ControlServiceSlotLease lease)
        {
            PrepareCalls++;
            if (UncertainCleanup)
                return Switch2ControlServiceSlotHostResult.Uncertain(
                    Switch2ControlServiceSlotHostOperation.Prepare,
                    Switch2ControlServiceSlotHostFailureKind.DependencyThrew);
            return Switch2ControlServiceSlotHostResult.Reject(
                Switch2ControlServiceSlotHostOperation.Prepare,
                Switch2ControlServiceSlotHostFailureKind.SlotOccupied);
        }
        public Switch2ControlServiceSlotHostResult TryDispatch(in Switch2ControlServiceSlotLease lease,
            DS4Device sender, Switch2RuntimeReportEventArgs report) => throw new AssertFailedException(
                "A rejected controller slot cannot dispatch input.");
        public Switch2ControlServiceSlotHostResult TryAbort(in Switch2ControlServiceSlotLease lease)
        {
            AfterPhysicalAbort?.Invoke();
            return UncertainCleanup ? Switch2ControlServiceSlotHostResult.Uncertain(
                Switch2ControlServiceSlotHostOperation.Abort,
                Switch2ControlServiceSlotHostFailureKind.CleanupRejected) :
                Switch2ControlServiceSlotHostResult.Success(Switch2ControlServiceSlotHostOperation.Abort);
        }
        public Switch2ControlServiceSlotHostResult TryRemove(in Switch2ControlServiceSlotLease lease) =>
            Switch2ControlServiceSlotHostResult.Success(Switch2ControlServiceSlotHostOperation.Remove);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DefiniteDisconnectDrainsLocallyWithoutNewCccdWrites(bool duplex)
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var device = duplex ? FakeDevice.ValidDuplexPro() : FakeDevice.Valid();
        platform.EnqueueDevice(device);
        var observation = StartAndObserve(platform, watcher, out var adapter);
        var result = duplex ? await adapter.OpenRememberedDuplexAsync(observation) :
            await adapter.OpenRememberedInputAsync(observation);
        Assert.IsTrue(result.Succeeded, result.Failure.ToString());
        Assert.IsTrue(result.Lease.TrySubscribeCccdNotify(123,
            (_, _, _, _, _) => { }, _ => { }));
        device.Service.Characteristic.DisableOverride = _ => ValueTask.FromResult(false);
        if (duplex)
            device.Service.ResponseCharacteristic.DisableOverride = _ => ValueTask.FromResult(false);
        device.EmitDisconnected();

        Assert.IsTrue(await result.Lease.BeginAndWaitForResourceReleaseAsync()
            .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(0, device.Service.Characteristic.DisableCalls);
        if (duplex) Assert.AreEqual(0, device.Service.ResponseCharacteristic.DisableCalls);
        Assert.IsTrue(device.Disposed);
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealDuplexLeaseAndRuntimeDrainDisconnectWithoutQuarantine(
        bool xbox)
    {
        var source = xbox ? ControllerFeedbackSource.XboxOneVirtualDevice :
            ControllerFeedbackSource.DualSenseVirtualDevice;
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var device = FakeDevice.ValidDuplexPro();
        platform.EnqueueDevice(device);
        var observation = StartAndObserve(platform, watcher, out var adapter);
        var opened = await adapter.OpenRememberedDuplexAsync(observation);
        Assert.IsTrue(opened.Succeeded, opened.Failure.ToString());
        var lease = opened.Lease;
        const ulong deviceGeneration = 17, transportGeneration = 23;
        const Switch2ControllerModel model = Switch2ControllerModel.ProController2;
        Assert.IsTrue(lease.TryBindHdRumbleLifetime(model, deviceGeneration, transportGeneration));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(model,
            deviceGeneration, out var calibration));
        Assert.IsTrue(Switch2BluetoothRuntimeOwner.TryCreateCore(lease.Admission,
            lease, deviceGeneration, transportGeneration, 10_000_000, calibration,
            16, 2_000, Switch2BluetoothRuntimeDrainPumpFactory.Instance,
            Switch2RuntimeTerminalScheduler.Instance, out var owner, out var registration,
            out var createFailure), createFailure.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out _));
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token, out _, out _));
        Assert.IsTrue(owner.TryAdoptBoundSlot(token, out var adoption, out _));
        Assert.IsTrue(owner.TryPrepareActivation(adoption, 2_000, out var prepared, out _));
        Assert.IsTrue(table.TryBeginActivate(token, out var activation, out _));
        Assert.IsTrue(table.TryAcquireActivationCommit(activation, out var commit, out _));
        Assert.IsTrue(owner.TryCommitPrepared(prepared, commit, out _));
        Assert.IsTrue(table.TryCompleteActivate(commit, true, out _));
        Assert.IsTrue(owner.RuntimeDevice.TryCreateVirtualFeedbackSession(source,
            deviceGeneration, transportGeneration, out var session));
        Assert.IsTrue(session.TryPublish(new ControllerFeedbackActuatorState(20_000, 10_000, 0, 0)));
        int writes = device.Service.OutputCharacteristic.WriteCalls;
        Assert.IsFalse(lease.IsDisconnectedAndReleased(model, deviceGeneration, transportGeneration));
        device.EmitDisconnected();
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.Sink.TerminalRequested, TimeSpan.FromSeconds(2)));
        InputControllerRetirementClaim claim = default;
        int terminalReports = 0;
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind != Switch2RuntimeReportKind.TerminalNeutral) return;
            if (table.TryAcquireTerminalReportLease(claim, (DS4Device)sender, out var terminal, out _))
            {
                Interlocked.Increment(ref terminalReports);
                terminal.TryAcknowledgeTerminalNeutral(out _);
                terminal.Dispose();
            }
        };
        Assert.IsTrue(table.TryBeginRetire(token, out claim, out _));
        Assert.IsTrue(owner.TryArmRetirement(claim, out _));
        Assert.IsTrue(registration.TryStopAndQuiesce(2_000, out var stopFailure),
            $"{stopFailure}/{owner.LastStopFailure.Kind}/{owner.LastStopFailure.ReleaseResult}");
        Assert.AreEqual(1, terminalReports);
        Assert.IsFalse(owner.RequiresQuarantine);
        Assert.IsTrue(lease.IsDisconnectedAndReleased(model, deviceGeneration, transportGeneration));
        Assert.IsFalse(lease.IsDisconnectedAndReleased(model, deviceGeneration, transportGeneration + 1));
        Assert.AreEqual(writes, device.Service.OutputCharacteristic.WriteCalls);
        Assert.AreEqual(0, device.Service.Characteristic.DisableCalls);
        Assert.AreEqual(0, device.Service.ResponseCharacteristic.DisableCalls);
        Assert.IsFalse(session.TryPublish(new ControllerFeedbackActuatorState(40_000, 0, 0, 0)));
        Assert.IsTrue(session.TryRetire());
        Assert.IsTrue(session.WasRetiredDisconnected,
            "Local retirement must never arm VIIPER's delivered-Stop shortcut.");
        Assert.IsTrue(registration.TryRemove(out _));
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisconnectProofDoesNotHideDisposeOrCallbackDrainFailure(bool callbackFailure)
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var device = FakeDevice.ValidDuplexPro();
        platform.EnqueueDevice(device);
        var observation = StartAndObserve(platform, watcher, out var adapter);
        var opened = await adapter.OpenRememberedDuplexAsync(observation);
        Assert.IsTrue(opened.Succeeded);
        Assert.IsTrue(opened.Lease.TryBindHdRumbleLifetime(Switch2ControllerModel.ProController2, 17, 23));
        Assert.IsTrue(opened.Lease.TrySubscribeCccdNotify(23, (_, _, _, _, _) => { }, _ => { }));
        device.ThrowOnDispose = !callbackFailure;
        device.ThrowOnDetach = callbackFailure;
        device.EmitDisconnected();
        Assert.AreNotEqual(Switch2BluetoothInputLeaseReleaseResult.Released,
            opened.Lease.WaitForRelease(23, 200));
        Assert.IsFalse(opened.Lease.IsDisconnectedAndReleased(Switch2ControllerModel.ProController2, 17, 23));
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task ActiveWatcherUsesFreshScanKeyAndRejectsStaleCallbacks()
    {
        var firstWatcher = new FakeWatcher();
        var secondWatcher = new FakeWatcher();
        var platform = new FakePlatform(firstWatcher, secondWatcher);
        var registry = new Switch2BluetoothCandidateRegistry();
        var observations = new List<Switch2BluetoothCandidateObservation>();
        var adapter = CreateAdapter(platform, registry);

        Assert.IsTrue(adapter.TryStartScan(1, LocalHost, observations.Add,
            out var failure), failure.ToString());
        Assert.IsTrue(firstWatcher.ActiveConfigured);
        Assert.IsTrue(firstWatcher.Started);
        firstWatcher.Emit(0x112233445566,
            BuildAdvertisement(
                Switch2AdvertisementCodec.ProController2ProductId), 10);
        Assert.AreEqual(1, observations.Count);
        Switch2BluetoothPeerToken firstToken = observations[0].PeerToken;

        Assert.IsTrue(await adapter.EndScanAsync(1));
        Assert.IsTrue(firstWatcher.Stopped);
        Assert.IsTrue(firstWatcher.Detached);
        Assert.IsTrue(firstWatcher.Disposed);

        // Simulate a platform callback which was captured before handler
        // removal. The retired adapter generation must still reject it.
        firstWatcher.EmitCaptured(0x112233445566,
            BuildAdvertisement(
                Switch2AdvertisementCodec.ProController2ProductId), 11);
        Assert.AreEqual(1, observations.Count);

        Assert.IsTrue(adapter.TryStartScan(2, LocalHost, observations.Add,
            out failure), failure.ToString());
        firstWatcher.EmitCaptured(0x112233445566,
            BuildAdvertisement(
                Switch2AdvertisementCodec.ProController2ProductId), 12);
        Assert.AreEqual(1, observations.Count,
            "An old watcher cannot publish into the successor scan.");
        secondWatcher.Emit(0x112233445566,
            BuildAdvertisement(
                Switch2AdvertisementCodec.ProController2ProductId), 13);
        Assert.AreEqual(2, observations.Count);
        Assert.AreNotEqual(firstToken, observations[1].PeerToken,
            "A new scan must generate a fresh private HMAC key.");
        Assert.IsTrue(await adapter.EndScanAsync(2));
    }

    [TestMethod]
    public void WatcherStopCallbackRetiresExactGenerationAndDrains()
    {
        var watcher = new FakeWatcher();
        var registry = new Switch2BluetoothCandidateRegistry();
        var adapter = CreateAdapter(new FakePlatform(watcher), registry);
        Assert.IsTrue(adapter.TryStartScan(3, LocalHost, _ => { }, out _));

        watcher.EmitStopped();
        Assert.IsTrue(SpinWait.SpinUntil(() => !adapter.IsScanning &&
            watcher.Disposed, TimeSpan.FromSeconds(2)));
        Assert.IsFalse(registry.IsScanActive);
        Assert.AreEqual((ulong)3, registry.ScanGeneration,
            "Retirement must preserve the monotonic scan fence.");
    }

    [TestMethod]
    public void WatcherConfigurationAndStartExceptionsRollbackFailClosed()
    {
        var configureFailure = new FakeWatcher { ThrowOnConfigure = true };
        var configureRegistry = new Switch2BluetoothCandidateRegistry();
        var configureAdapter = CreateAdapter(
            new FakePlatform(configureFailure), configureRegistry);
        Assert.IsFalse(configureAdapter.TryStartScan(1, LocalHost, _ => { },
            out var failure));
        Assert.AreEqual(Switch2BluetoothWindowsScanStartFailure.
            WatcherConfigurationFailed, failure);
        Assert.IsFalse(configureRegistry.IsScanActive);
        Assert.IsTrue(SpinWait.SpinUntil(() => configureFailure.Disposed,
            TimeSpan.FromSeconds(2)));

        var startFailure = new FakeWatcher { ThrowOnStart = true };
        var startRegistry = new Switch2BluetoothCandidateRegistry();
        var startAdapter = CreateAdapter(new FakePlatform(startFailure),
            startRegistry);
        Assert.IsFalse(startAdapter.TryStartScan(1, LocalHost, _ => { },
            out failure));
        Assert.AreEqual(Switch2BluetoothWindowsScanStartFailure.
            WatcherStartFailed, failure);
        Assert.IsFalse(startRegistry.IsScanActive);
        Assert.IsTrue(SpinWait.SpinUntil(() => startFailure.Disposed,
            TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task FailedWatcherConfigurationRetainsItsHandlerDrainFence()
    {
        var drain = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new FakeWatcher
        {
            ThrowOnAttach = true,
            DrainOverride = drain.Task,
        };
        var adapter = CreateAdapter(new FakePlatform(watcher, new FakeWatcher()),
            new Switch2BluetoothCandidateRegistry(), 20);
        Assert.IsFalse(adapter.TryStartScan(1, LocalHost, _ => { }, out var failure));
        Assert.AreEqual(Switch2BluetoothWindowsScanStartFailure.
            WatcherConfigurationFailed, failure);
        Task<bool> exactDrain = adapter.EndScanAndDrainAsync(1);
        try
        {
            Assert.IsFalse(exactDrain.IsCompleted);
            Assert.IsFalse(await adapter.EndScanAsync(1));
            Assert.IsFalse(adapter.TryStartScan(2, LocalHost, _ => { }, out failure));
            Assert.AreEqual(Switch2BluetoothWindowsScanStartFailure.ScanAlreadyActive,
                failure);
            Assert.IsFalse(watcher.Disposed);
        }
        finally
        {
            drain.TrySetResult(true);
        }
        Assert.IsTrue(await exactDrain.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(watcher.Disposed);
        Assert.IsTrue(adapter.TryStartScan(3, LocalHost, _ => { }, out failure),
            failure.ToString());
        Assert.IsTrue(await adapter.EndScanAsync(3));
        Assert.IsFalse(await adapter.EndScanAsync(1),
            "The old generation cannot satisfy or retire a new scan.");
    }

    [TestMethod]
    public void WatcherStartMustCommitBeforeCandidatePublication()
    {
        var throwingWatcher = new FakeWatcher
        {
            ThrowOnStart = true,
        };
        var observations = new List<Switch2BluetoothCandidateObservation>();
        throwingWatcher.StartAction = () => throwingWatcher.Emit(
            0x112233445566, BuildAdvertisement(
                Switch2AdvertisementCodec.ProController2ProductId), 1);
        var throwingRegistry = new Switch2BluetoothCandidateRegistry();
        var throwingAdapter = CreateAdapter(new FakePlatform(throwingWatcher),
            throwingRegistry);

        Assert.IsFalse(throwingAdapter.TryStartScan(1, LocalHost,
            observations.Add, out var failure));
        Assert.AreEqual(Switch2BluetoothWindowsScanStartFailure.
            WatcherStartFailed, failure);
        Assert.AreEqual(0, observations.Count,
            "A watcher which never commits Start cannot publish a peer.");
        Assert.IsFalse(throwingRegistry.IsScanActive);

        var stoppedWatcher = new FakeWatcher();
        stoppedWatcher.StartAction = stoppedWatcher.EmitStopped;
        var stoppedRegistry = new Switch2BluetoothCandidateRegistry();
        var stoppedAdapter = CreateAdapter(new FakePlatform(stoppedWatcher),
            stoppedRegistry);
        Assert.IsFalse(stoppedAdapter.TryStartScan(1, LocalHost, _ => { },
            out failure));
        Assert.AreEqual(Switch2BluetoothWindowsScanStartFailure.
            WatcherStartFailed, failure);
        Assert.IsFalse(stoppedAdapter.IsScanning,
            "An inline Stopped event cannot be reported as a started scan.");
        Assert.IsFalse(stoppedRegistry.IsScanActive);
        Assert.IsTrue(SpinWait.SpinUntil(() => stoppedWatcher.Disposed,
            TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task EndDuringStartSerializesStopDetachAndDisposal()
    {
        var watcher = new FakeWatcher();
        using var startEntered = new ManualResetEventSlim(false);
        using var releaseStart = new ManualResetEventSlim(false);
        watcher.StartAction = () =>
        {
            startEntered.Set();
            releaseStart.Wait();
        };
        var registry = new Switch2BluetoothCandidateRegistry();
        var adapter = CreateAdapter(new FakePlatform(watcher), registry);

        Task<(bool Started, Switch2BluetoothWindowsScanStartFailure Failure)>
            start = Task.Run(() =>
            {
                bool started = adapter.TryStartScan(1, LocalHost, _ => { },
                    out Switch2BluetoothWindowsScanStartFailure failure);
                return (started, failure);
            });
        Assert.IsTrue(startEntered.Wait(TimeSpan.FromSeconds(2)));
        Task<bool> end = Task.Run(async () => await adapter.EndScanAsync(1));
        await Task.Delay(30);
        Assert.IsFalse(watcher.Stopped);
        Assert.IsFalse(watcher.Detached);
        Assert.IsFalse(watcher.Disposed,
            "Stop/detach/dispose cannot race the still-running Start call.");

        releaseStart.Set();
        var startResult = await start.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(startResult.Started);
        Assert.AreEqual(Switch2BluetoothWindowsScanStartFailure.
            WatcherStartFailed, startResult.Failure);
        Assert.IsTrue(await end.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(watcher.Stopped);
        Assert.IsTrue(watcher.Detached);
        Assert.IsTrue(watcher.Disposed);
        Assert.IsFalse(registry.IsScanActive);
    }

    [TestMethod]
    public async Task DuplicateAndRotatedAddressesRemainSeparateCapabilities()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var firstDevice = FakeDevice.Valid();
        var secondDevice = FakeDevice.Valid();
        platform.EnqueueDevice(firstDevice);
        platform.EnqueueDevice(secondDevice);
        var observations = new List<Switch2BluetoothCandidateObservation>();
        using var identityDeriver =
            new Switch2PersistentPeerIdentityDeriver(
                Enumerable.Range(1,
                    Switch2PersistentPeerId.InstallKeyLength).
                    Select(value => (byte)value).ToArray());
        var adapter = CreateAdapter(platform,
            new Switch2BluetoothCandidateRegistry(4),
            identityDeriver: identityDeriver);
        Assert.IsTrue(adapter.TryStartScan(7, LocalHost, observations.Add,
            out _));
        byte[] advertisement = BuildAdvertisement(
            Switch2AdvertisementCodec.JoyCon2LeftProductId);

        watcher.Emit(0x100000000001, advertisement, 20);
        watcher.Emit(0x100000000001, advertisement, 21);
        watcher.Emit(0x100000000002, advertisement, 22);
        Assert.AreEqual(3, observations.Count);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            observations[0].Disposition);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Duplicate,
            observations[1].Disposition);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            observations[2].Disposition);
        Assert.AreNotEqual(observations[0].PeerToken,
            observations[2].PeerToken);

        Switch2BluetoothWindowsOpenResult first = await adapter.
            OpenRememberedInputAsync(observations[0]);
        Switch2BluetoothWindowsOpenResult duplicate = await adapter.
            OpenRememberedInputAsync(observations[1]);
        Switch2BluetoothWindowsOpenResult rotated = await adapter.
            OpenRememberedInputAsync(observations[2]);
        Assert.IsTrue(first.Succeeded, first.Failure.ToString());
        Assert.AreEqual(
            Switch2BluetoothWindowsOpenFailure.InvalidObservation,
            duplicate.Failure);
        Assert.IsTrue(rotated.Succeeded, rotated.Failure.ToString());
        CollectionAssert.AreEqual(new ulong[]
        {
            0x100000000001,
            0x100000000002,
        }, platform.OpenedAddresses.ToArray());

        await RetireLease(first.Lease, 701);
        await RetireLease(rotated.Lease, 702);
        Assert.IsTrue(await adapter.EndScanAsync(7));
    }

    [TestMethod]
    public async Task CleanLeaseReleaseRearmsSamePeerForReconnect()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        platform.EnqueueDevice(FakeDevice.Valid());
        platform.EnqueueDevice(FakeDevice.Valid());
        var observations = new List<Switch2BluetoothCandidateObservation>();
        var adapter = CreateAdapter(platform,
            new Switch2BluetoothCandidateRegistry());
        Assert.IsTrue(adapter.TryStartScan(8, LocalHost, observations.Add,
            out _));
        const ulong address = 0x100000000008;
        byte[] advertisement = BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId);

        watcher.Emit(address, advertisement, 1);
        Switch2BluetoothWindowsOpenResult first = await adapter.
            OpenRememberedInputAsync(observations.Single());
        Assert.IsTrue(first.Succeeded, first.Failure.ToString());
        await RetireLease(first.Lease, 801);

        watcher.Emit(address, advertisement, 2);
        Assert.AreEqual(2, observations.Count);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            observations[1].Disposition,
            "A proven clean release must publish one fresh reconnect candidate.");
        Switch2BluetoothWindowsOpenResult second = await adapter.
            OpenRememberedInputAsync(observations[1]);
        Assert.IsTrue(second.Succeeded, second.Failure.ToString());
        CollectionAssert.AreEqual(new[] { address, address },
            platform.OpenedAddresses);

        await RetireLease(second.Lease, 802);
        Assert.IsTrue(await adapter.EndScanAsync(8));
    }

    [TestMethod]
    public async Task DeferredReconnectKeepsAddressUntilLaterAdvertisement()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        platform.EnqueueDevice(FakeDevice.Valid());
        platform.EnqueueDevice(FakeDevice.Valid());
        var observations = new List<Switch2BluetoothCandidateObservation>();
        var adapter = CreateAdapter(platform,
            new Switch2BluetoothCandidateRegistry());
        Assert.IsTrue(adapter.TryStartScan(11, LocalHost, observations.Add,
            out _));
        const ulong address = 0x100000000011;
        byte[] advertisement = BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId);

        watcher.Emit(address, advertisement, 1);
        Switch2BluetoothWindowsOpenResult first = await adapter.
            OpenRememberedInputAsync(observations.Single());
        Assert.IsTrue(first.Succeeded, first.Failure.ToString());
        await RetireLease(first.Lease, 1_101);

        watcher.Emit(address, advertisement, 2);
        Assert.IsTrue(adapter.TryDeferRememberedInputCandidate(
            observations[1]));
        watcher.Emit(address, advertisement, 3);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            observations[2].Disposition);
        Switch2BluetoothWindowsOpenResult second = await adapter.
            OpenRememberedInputAsync(observations[2]);
        Assert.IsTrue(second.Succeeded, second.Failure.ToString());
        CollectionAssert.AreEqual(new[] { address, address },
            platform.OpenedAddresses,
            "Deferral must preserve, not consume or duplicate, address authority.");

        await RetireLease(second.Lease, 1_102);
        Assert.IsTrue(await adapter.EndScanAsync(11));
    }

    [TestMethod]
    public async Task AmbiguousLeaseReleaseDoesNotRearmReconnect()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        device.Service.Characteristic.DisableOverride = _ =>
            ValueTask.FromResult(false);
        platform.EnqueueDevice(device);
        var observations = new List<Switch2BluetoothCandidateObservation>();
        var adapter = CreateAdapter(platform,
            new Switch2BluetoothCandidateRegistry());
        Assert.IsTrue(adapter.TryStartScan(9, LocalHost, observations.Add,
            out _));
        const ulong address = 0x100000000009;
        byte[] advertisement = BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId);

        watcher.Emit(address, advertisement, 1);
        Switch2BluetoothWindowsOpenResult opened = await adapter.
            OpenRememberedInputAsync(observations.Single());
        Assert.IsTrue(opened.Succeeded, opened.Failure.ToString());
        Assert.IsTrue(opened.Lease.TrySubscribeCccdNotify(901,
            (_, _, _, _, _) => { }, _ => { }));
        var proof = (ISwitch2BluetoothInputLeaseReleaseProof)opened.Lease;
        Assert.AreEqual(Switch2BluetoothInputLeaseReleaseResult.Rejected,
            proof.WaitForRelease(901, 2_000));

        watcher.Emit(address, advertisement, 2);
        Assert.AreEqual(2, observations.Count);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.Duplicate,
            observations[1].Disposition);
        Assert.IsFalse(observations[1].IsConnectionCandidate,
            "A false teardown proof must keep reconnect admission burned.");
        CollectionAssert.AreEqual(new[] { address },
            platform.OpenedAddresses);
        Assert.IsTrue(await adapter.EndScanAsync(9));
    }

    [TestMethod]
    public async Task IdentityQuarantineOutlivesCleanLeaseRelease()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        platform.EnqueueDevice(FakeDevice.Valid());
        var observations = new List<Switch2BluetoothCandidateObservation>();
        var adapter = CreateAdapter(platform,
            new Switch2BluetoothCandidateRegistry());
        Assert.IsTrue(adapter.TryStartScan(10, LocalHost, observations.Add,
            out _));
        const ulong address = 0x100000000010;

        watcher.Emit(address, BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId), 1);
        Switch2BluetoothWindowsOpenResult opened = await adapter.
            OpenRememberedInputAsync(observations.Single());
        Assert.IsTrue(opened.Succeeded, opened.Failure.ToString());

        watcher.Emit(address, BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId,
            new byte[] { 7, 8, 9, 10, 11, 12 }), 2);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            observations[1].Disposition);
        Assert.IsTrue(opened.Lease.TrySubscribeCccdNotify(1_001,
            (_, _, _, _, _) => { }, _ => { }));
        var proof = (ISwitch2BluetoothInputLeaseReleaseProof)opened.Lease;
        Assert.AreEqual(Switch2BluetoothInputLeaseReleaseResult.Released,
            proof.WaitForRelease(1_001, 2_000),
            "Registry quarantine must not falsify a clean WinRT release proof.");

        watcher.Emit(address, BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId), 3);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            observations[2].Disposition);
        Assert.IsFalse(observations[2].IsConnectionCandidate,
            "Clean resource release must never clear identity quarantine.");
        CollectionAssert.AreEqual(new[] { address },
            platform.OpenedAddresses);
        Assert.IsTrue(await adapter.EndScanAsync(10));
    }

    [TestMethod]
    public void ExactCompanyProductAndManufacturerShapeAreRequired()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var observations = new List<Switch2BluetoothCandidateObservation>();
        var adapter = CreateAdapter(platform,
            new Switch2BluetoothCandidateRegistry());
        Assert.IsTrue(adapter.TryStartScan(1, LocalHost, observations.Add,
            out _));

        watcher.Emit(1, BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId), 1,
            companyId: 0xFFFF);
        watcher.Emit(2, BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId), 2,
            matchingCompanySections: 2);
        watcher.Emit(3, new byte[23], 3);
        watcher.Emit(4, BuildAdvertisement(0xFFFF), 4);
        Assert.AreEqual(0, observations.Count);

        watcher.Emit(5, BuildAdvertisement(
            Switch2AdvertisementCodec.JoyCon2RightProductId), 5);
        watcher.Emit(6, BuildAdvertisement(
            Switch2AdvertisementCodec.JoyCon2LeftProductId), 6);
        watcher.Emit(7, BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId), 7);
        CollectionAssert.AreEqual(new[]
        {
            Switch2ControllerModel.JoyCon2Right,
            Switch2ControllerModel.JoyCon2Left,
            Switch2ControllerModel.ProController2,
        }, observations.Select(value => value.Model).ToArray());
    }

    [TestMethod]
    public async Task OpenUsesOnlyUncachedExactServiceAndCommon05()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);

        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.IsTrue(result.Succeeded, result.Failure.ToString());
        Assert.AreEqual(1, device.UncachedServiceQueries);
        Assert.AreEqual(Switch2InputCodec.ServiceUuid,
            device.LastServiceQueryUuid);
        Assert.AreEqual(1, device.Service.UncachedCharacteristicQueries);
        Assert.AreEqual(Switch2InputCodec.Common05CharacteristicUuid,
            device.Service.LastCharacteristicQueryUuid);
        Assert.AreEqual(1, device.Service.Characteristic.EnableCalls);
        Assert.AreEqual(0, device.Service.Characteristic.DisableCalls);

        await RetireLease(result.Lease, 400);
        Assert.AreEqual(1, device.Service.Characteristic.DisableCalls);
        Assert.IsTrue(device.Disposed);
        Assert.IsTrue(device.Service.Disposed);
        Assert.IsTrue(device.Service.Characteristic.Disposed);
    }

    [TestMethod]
    public async Task DuplicateOrNonExactGattShapesFailClosedAndDispose()
    {
        await AssertGattFailure(FakeDevice.WithDuplicateServices(),
            Switch2BluetoothWindowsOpenFailure.ServiceIdentityMismatch);
        await AssertGattFailure(FakeDevice.WithDuplicateCharacteristics(),
            Switch2BluetoothWindowsOpenFailure.
                CharacteristicIdentityMismatch);
        await AssertGattFailure(FakeDevice.WithWrongServiceUuid(),
            Switch2BluetoothWindowsOpenFailure.ServiceIdentityMismatch);
        await AssertGattFailure(FakeDevice.WithWrongCharacteristicUuid(),
            Switch2BluetoothWindowsOpenFailure.
                CharacteristicIdentityMismatch);
        await AssertGattFailure(FakeDevice.WithWritableCommon05(),
            Switch2BluetoothWindowsOpenFailure.
                CharacteristicPropertiesMismatch);
        await AssertGattFailure(FakeDevice.WithExtraNativeProperty(),
            Switch2BluetoothWindowsOpenFailure.
                CharacteristicPropertiesMismatch);
    }

    [TestMethod]
    public async Task StartupExceptionsAndTimeoutsAreBoundedAndBurnCapability()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var never = new TaskCompletionSource<
            ISwitch2BluetoothWindowsDevice>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        platform.OpenOverride = _ => new ValueTask<
            ISwitch2BluetoothWindowsDevice>(never.Task);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter, timeoutMilliseconds: 20);

        Switch2BluetoothWindowsOpenResult timedOut = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.StartupTimedOut,
            timedOut.Failure);
        Switch2BluetoothWindowsOpenResult repeated = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.InvalidObservation,
            repeated.Failure);

        FakeDevice late = FakeDevice.Valid();
        never.SetResult(late);
        Assert.IsTrue(SpinWait.SpinUntil(() => late.Disposed,
            TimeSpan.FromSeconds(2)),
            "A device returned after the deadline must be disposed.");
    }

    [TestMethod]
    public async Task PreCancelledOpenBurnsCapabilityWithoutCallingPlatform()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Switch2BluetoothWindowsOpenResult cancelled = await adapter.
            OpenRememberedInputAsync(observation, cancellation.Token);
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.Cancelled,
            cancelled.Failure);
        Assert.AreEqual(0, platform.OpenedAddresses.Count,
            "A pre-cancelled open must not enter WinRT.");
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.InvalidObservation,
            (await adapter.OpenRememberedInputAsync(observation)).Failure,
            "The one-shot admission remains burned fail-closed.");
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task SourceCancellationIsAStageFailureNotDeadlineExpiry()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher)
        {
            OpenOverride = _ => ValueTask.FromCanceled<
                ISwitch2BluetoothWindowsDevice>(new CancellationToken(true)),
        };
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);

        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.DeviceOpenFailed,
            result.Failure);
        Assert.IsTrue(await adapter.EndScanAsync(1));

        var timeoutWatcher = new FakeWatcher();
        var timeoutPlatform = new FakePlatform(timeoutWatcher)
        {
            OpenOverride = _ => ValueTask.FromException<
                ISwitch2BluetoothWindowsDevice>(new TimeoutException(
                    "Synthetic platform timeout.")),
        };
        observation = StartAndObserve(timeoutPlatform, timeoutWatcher,
            out adapter);
        result = await adapter.OpenRememberedInputAsync(observation);
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.DeviceOpenFailed,
            result.Failure,
            "Only the configured deadline may report StartupTimedOut.");
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task LateServiceQueryRetainsOwnerUntilCompletion()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        var serviceResult = new TaskCompletionSource<
            Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattService>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        device.ServiceQueryOverride = _ => new(serviceResult.Task);
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter, timeoutMilliseconds: 20);

        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.StartupTimedOut,
            result.Failure);
        Assert.IsFalse(device.Disposed,
            "The device owns the still-running uncached service query.");

        serviceResult.SetResult(new Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattService>(true,
                new ISwitch2BluetoothWindowsGattService[] { device.Service }));
        Assert.IsTrue(SpinWait.SpinUntil(() => device.Disposed &&
            device.Service.Disposed, TimeSpan.FromSeconds(2)));
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task LateCharacteristicQueryRetainsOwnersUntilCompletion()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        var characteristicResult = new TaskCompletionSource<
            Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattCharacteristic>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        device.Service.CharacteristicQueryOverride = _ =>
            new(characteristicResult.Task);
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter, timeoutMilliseconds: 20);

        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.StartupTimedOut,
            result.Failure);
        Assert.IsFalse(device.Disposed);
        Assert.IsFalse(device.Service.Disposed,
            "The service owns the still-running uncached characteristic query.");

        characteristicResult.SetResult(new Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattCharacteristic>(true,
                new ISwitch2BluetoothWindowsGattCharacteristic[]
                {
                    device.Service.Characteristic,
                }));
        Assert.IsTrue(SpinWait.SpinUntil(() => device.Disposed &&
            device.Service.Disposed && device.Service.Characteristic.Disposed,
            TimeSpan.FromSeconds(2)));
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task NotificationSetupTimeoutIsBoundedAndNeverPublishesLease()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        var never = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.Service.Characteristic.EnableOverride = _ =>
            new ValueTask<bool>(never.Task);
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter, timeoutMilliseconds: 20);

        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.AreEqual(
            Switch2BluetoothWindowsOpenFailure.StartupTimedOut,
            result.Failure);
        Assert.IsNull(result.Lease);
        never.SetResult(false);
        Assert.IsTrue(SpinWait.SpinUntil(() => device.Disposed,
            TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task InlineDisconnectBeforeNotifyNeverEnablesCccd()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        device.EmitDisconnectInlineOnAttach = true;
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);

        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.
            NotificationSetupFailed, result.Failure);
        Assert.AreEqual(0, device.Service.Characteristic.EnableCalls,
            "A terminal disconnect observed during handler publication must " +
            "not be followed by Notify enablement.");
        Assert.IsTrue(device.Disposed);
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task LateNotifyIsSerializedBeforeCccdNoneAndResourceRelease()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        var enable = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int disableOrder = 0;
        device.Service.Characteristic.EnableOverride = _ =>
            new ValueTask<bool>(enable.Task);
        device.Service.Characteristic.DisableOverride = _ =>
        {
            Interlocked.Exchange(ref disableOrder,
                enable.Task.IsCompletedSuccessfully ? 1 : -1);
            return ValueTask.FromResult(true);
        };
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter, timeoutMilliseconds: 20);

        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.StartupTimedOut,
            result.Failure);
        Assert.IsNull(result.Lease);
        Assert.AreEqual(0, device.Service.Characteristic.DisableCalls,
            "CCCD None must not race ahead of a non-cooperative Notify.");
        Assert.IsFalse(device.Disposed,
            "The WinRT object graph must retain the late Notify operation.");

        enable.SetResult(true);
        Assert.IsTrue(SpinWait.SpinUntil(() => device.Disposed,
            TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, Volatile.Read(ref disableOrder),
            "CCCD None must execute only after late Notify completes.");
        Assert.AreEqual(1, device.Service.Characteristic.DisableCalls);
    }

    [TestMethod]
    public async Task PlatformExceptionsAreAttributedToTheirStartupStage()
    {
        await AssertGattFailure(FakeDevice.WithThrowingServiceQuery(),
            Switch2BluetoothWindowsOpenFailure.ServiceQueryFailed);
        await AssertGattFailure(FakeDevice.WithThrowingCharacteristicQuery(),
            Switch2BluetoothWindowsOpenFailure.CharacteristicQueryFailed);
        await AssertGattFailure(FakeDevice.WithThrowingNotificationSetup(),
            Switch2BluetoothWindowsOpenFailure.NotificationSetupFailed);

        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher)
        {
            OpenOverride = _ => ValueTask.FromException<
                ISwitch2BluetoothWindowsDevice>(
                    new InvalidOperationException("Synthetic open failure.")),
        };
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);
        Switch2BluetoothWindowsOpenResult failed = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.AreEqual(
            Switch2BluetoothWindowsOpenFailure.DeviceOpenFailed,
            failed.Failure);
    }

    [TestMethod]
    public async Task OnlyConnectionCandidatesGetAnAddressCapability()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var observations = new List<Switch2BluetoothCandidateObservation>();
        var adapter = CreateAdapter(platform,
            new Switch2BluetoothCandidateRegistry());
        Assert.IsTrue(adapter.TryStartScan(1, LocalHost, observations.Add,
            out _));

        watcher.Emit(1, BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId,
            Array.Empty<byte>()), 1);
        watcher.Emit(2, BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId,
            new byte[] { 1, 2, 3, 4, 5, 6 }), 2);
        Assert.AreEqual(2, observations.Count);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.
            RequiresExplicitAssociation, observations[0].Disposition);
        Assert.AreEqual(Switch2BluetoothObservationDisposition.
            IgnoredForeignHost, observations[1].Disposition);
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.
            InvalidObservation, (await adapter.OpenRememberedInputAsync(
                observations[0])).Failure);
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.
            InvalidObservation, (await adapter.OpenRememberedInputAsync(
                observations[1])).Failure);
        Assert.AreEqual(0, platform.OpenedAddresses.Count);
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task AssociationCandidateConsumesExactTypedAddressOnce()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        platform.EnqueueDevice(FakeDevice.Valid());
        var observations = new List<Switch2BluetoothCandidateObservation>();
        var adapter = CreateAdapter(platform,
            new Switch2BluetoothCandidateRegistry());
        Assert.IsTrue(adapter.TryStartScan(1, LocalHost, observations.Add,
            out _));
        watcher.Emit(0x112233445566, BuildAdvertisement(
                Switch2AdvertisementCodec.ProController2ProductId,
                Array.Empty<byte>()), 1,
            addressType: Switch2BluetoothWindowsAddressType.Random);

        Switch2BluetoothWindowsAssociationResult result = await adapter.
            AssociateAsync(observations.Single());
        Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.
            CommandCharacteristicIdentityMismatch, result.Failure);
        CollectionAssert.AreEqual(new[] { 0x112233445566UL },
            platform.OpenedAddresses);
        CollectionAssert.AreEqual(new[]
        {
            Switch2BluetoothWindowsAddressType.Random,
        }, platform.OpenedAddressTypes);

        Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.
            InvalidObservation,
            (await adapter.AssociateAsync(observations.Single())).Failure);
        Assert.AreEqual(1, platform.OpenedAddresses.Count);

        watcher.Emit(0x112233445566, BuildAdvertisement(
                Switch2AdvertisementCodec.ProController2ProductId,
                LocalHost), 2,
            addressType: Switch2BluetoothWindowsAddressType.Random);
        Assert.AreEqual(2, observations.Count);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.IdentityConflict,
            observations[1].Disposition,
            "A failed or ambiguous association must not authorize a host transition.");
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task CleanAssociationPromotesReadvertisementToRememberedOpen()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice associationDevice = FakeDevice.ValidDuplexPro();
        associationDevice.Service.CommandCharacteristic.WriteOverride =
            (_, _, _) =>
            {
                associationDevice.Service.ResponseCharacteristic.Emit(
                    Convert.FromHexString("1501000000000000"), 1);
                return ValueTask.FromResult(true);
            };
        platform.EnqueueDevice(associationDevice);
        platform.EnqueueDevice(FakeDevice.Valid());
        var observations = new List<Switch2BluetoothCandidateObservation>();
        var adapter = CreateAdapter(platform,
            new Switch2BluetoothCandidateRegistry());
        Assert.IsTrue(adapter.TryStartScan(20, LocalHost, observations.Add,
            out _));
        const ulong address = 0x112233445566;
        watcher.Emit(address, BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId,
            Array.Empty<byte>()), 1,
            addressType: Switch2BluetoothWindowsAddressType.Random);

        Switch2BluetoothWindowsAssociationResult associated = await adapter.
            AssociateAsync(observations.Single());
        Assert.IsTrue(associated.Succeeded, associated.Failure.ToString());
        Assert.AreEqual(Switch2BluetoothAssociationStep.Commit,
            associated.LastCompletedStep);

        watcher.Emit(address, BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId, LocalHost), 2,
            addressType: Switch2BluetoothWindowsAddressType.Random);
        Assert.AreEqual(2, observations.Count);
        Assert.AreEqual(
            Switch2BluetoothObservationDisposition.RememberedThisHost,
            observations[1].Disposition);

        Switch2BluetoothWindowsOpenResult opened = await adapter.
            OpenRememberedInputAsync(observations[1]);
        Assert.IsTrue(opened.Succeeded, opened.Failure.ToString());
        CollectionAssert.AreEqual(new[] { address, address },
            platform.OpenedAddresses);
        await RetireLease(opened.Lease, 2001);
        Assert.IsTrue(await adapter.EndScanAsync(20));
    }

    [TestMethod]
    public async Task LeaseFencesCallbackGenerationAndDisconnect()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);
        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.IsTrue(result.Succeeded);

        ulong notifiedGeneration = 0;
        ulong disconnectedGeneration = 0;
        int notifications = 0;
        byte observedByte = 0;
        Assert.IsTrue(result.Lease.TrySubscribeCccdNotify(123,
            (generation, service, characteristic, body, qpc) =>
            {
                notifiedGeneration = generation;
                notifications++;
                observedByte = body[0];
                Assert.AreEqual(Switch2InputCodec.ServiceUuid, service);
                Assert.AreEqual(
                    Switch2InputCodec.Common05CharacteristicUuid,
                    characteristic);
            }, generation => disconnectedGeneration = generation));
        byte[] body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        body[0] = 0xA5;
        device.Service.Characteristic.Emit(body, 900);
        Assert.AreEqual(1, notifications);
        Assert.AreEqual((ulong)123, notifiedGeneration);
        Assert.AreEqual((byte)0xA5, observedByte);

        device.EmitDisconnected();
        Assert.AreEqual((ulong)123, disconnectedGeneration);
        Assert.IsTrue(result.Lease.TryUnsubscribeCccdNone(123));
        await result.Lease.ResourceRelease.WaitAsync(TimeSpan.FromSeconds(2));
        device.Service.Characteristic.EmitCaptured(body, 901);
        device.EmitCapturedDisconnected();
        Assert.AreEqual(1, notifications);
        Assert.AreEqual((ulong)123, disconnectedGeneration);
    }

    [TestMethod]
    public async Task DisconnectWinsSubscriptionSuccessLinearization()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        // Reads 1-3 cover adapter open and lease preparation. Read 4 is the
        // final connection observation inside TrySubscribe.
        device.EmitDisconnectOnConnectedRead = 4;
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);
        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.IsTrue(result.Succeeded);

        ulong disconnectedGeneration = 0;
        Assert.IsFalse(result.Lease.TrySubscribeCccdNotify(124,
            (_, _, _, _, _) => { },
            generation => disconnectedGeneration = generation));
        Assert.AreEqual((ulong)124, disconnectedGeneration);
        Assert.IsTrue(result.Lease.TryUnsubscribeCccdNone(124));
        await result.Lease.ResourceRelease.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task TeardownRetainsObjectsUntilInFlightCallbackDrains()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);
        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        Assert.IsTrue(result.Lease.TrySubscribeCccdNotify(77,
            (generation, service, characteristic, body, qpc) =>
            {
                entered.Set();
                release.Wait();
            }, _ => { }));
        Task callback = Task.Run(() => device.Service.Characteristic.Emit(
            new byte[Switch2InputCodec.BluetoothLeBodyLength], 1));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(result.Lease.TryUnsubscribeCccdNone(77));
        await Task.Delay(30);
        Assert.IsFalse(result.Lease.ResourceRelease.IsCompleted);
        Assert.IsFalse(device.Disposed);
        Assert.IsFalse(device.Service.Disposed);
        Assert.IsFalse(device.Service.Characteristic.Disposed);

        release.Set();
        await callback.WaitAsync(TimeSpan.FromSeconds(2));
        await result.Lease.ResourceRelease.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(device.Disposed);
        Assert.IsTrue(device.Service.Disposed);
        Assert.IsTrue(device.Service.Characteristic.Disposed);
    }

    [TestMethod]
    public async Task TeardownTimeoutRetainsCccdOperationUntilItCompletes()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        var disable = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.Service.Characteristic.DisableOverride = _ =>
            new ValueTask<bool>(disable.Task);
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter, timeoutMilliseconds: 20);
        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.IsTrue(result.Lease.TrySubscribeCccdNotify(9,
            (_, _, _, _, _) => { }, _ => { }));

        bool bounded = await result.Lease.
            BeginAndWaitForBoundedTeardownAsync(CancellationToken.None);
        Assert.IsFalse(bounded);
        Assert.IsFalse(device.Disposed);
        Task<bool> exactRelease = result.Lease.BeginAndWaitForResourceReleaseAsync();
        Assert.IsFalse(exactRelease.IsCompleted,
            "A durable cleanup must not reuse the expired bounded observer.");
        disable.SetResult(true);
        Assert.IsTrue(await exactRelease.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreSame(exactRelease,
            result.Lease.BeginAndWaitForResourceReleaseAsync());
        await result.Lease.ResourceRelease.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(device.Disposed);
    }

    [TestMethod]
    public async Task ExactReleaseProofRetainsTimedOutGenerationUntilTrueRelease()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        var disable = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.Service.Characteristic.DisableOverride = _ =>
            new ValueTask<bool>(disable.Task);
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter, timeoutMilliseconds: 20);
        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.IsTrue(result.Lease.TrySubscribeCccdNotify(11,
            (_, _, _, _, _) => { }, _ => { }));
        var proof = (ISwitch2BluetoothInputLeaseReleaseProof)result.Lease;

        Assert.AreEqual(Switch2BluetoothInputLeaseReleaseResult.Invalid,
            proof.WaitForRelease(12, 0),
            "A foreign transport generation cannot initiate teardown.");
        Assert.AreEqual(0,
            device.Service.Characteristic.DisableCalls);
        Assert.AreEqual(Switch2BluetoothInputLeaseReleaseResult.TimedOut,
            proof.WaitForRelease(11, 0));
        Assert.AreEqual(1,
            device.Service.Characteristic.DisableCalls);
        Assert.IsFalse(device.Disposed,
            "A zero-timeout observer cannot release a live WinRT graph.");

        disable.SetResult(true);
        Assert.AreEqual(Switch2BluetoothInputLeaseReleaseResult.Released,
            proof.WaitForRelease(11, 2_000));
        await result.Lease.ResourceRelease.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(device.Disposed);
        Assert.AreEqual(1,
            device.Service.Characteristic.DisableCalls,
            "Retry must observe the one exact release task, not compensate twice.");
    }

    [TestMethod]
    public async Task ExactReleaseProofPreservesFalseCccdCompletion()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        device.Service.Characteristic.DisableOverride = _ =>
            ValueTask.FromResult(false);
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);
        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.IsTrue(result.Lease.TrySubscribeCccdNotify(13,
            (_, _, _, _, _) => { }, _ => { }));
        var proof = (ISwitch2BluetoothInputLeaseReleaseProof)result.Lease;

        Assert.AreEqual(Switch2BluetoothInputLeaseReleaseResult.Rejected,
            proof.WaitForRelease(13, 2_000),
            "Resource disposal cannot upgrade a false CCCD-None result.");
        Assert.IsFalse(await result.Lease.BeginAndWaitForResourceReleaseAsync(),
            "The durable cleanup result must preserve the failed unsubscribe.");
        await result.Lease.ResourceRelease.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(device.Disposed);
        Assert.AreEqual(1,
            device.Service.Characteristic.DisableCalls);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ExactReleaseProofPreservesEveryDisposeFailure(int stage)
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);
        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.Lease.TrySubscribeCccdNotify(14,
            (_, _, _, _, _) => { }, _ => { }));

        device.Service.Characteristic.ThrowOnDispose = stage == 0;
        device.Service.ThrowOnDispose = stage == 1;
        device.ThrowOnDispose = stage == 2;
        var proof = (ISwitch2BluetoothInputLeaseReleaseProof)result.Lease;

        Assert.AreEqual(Switch2BluetoothInputLeaseReleaseResult.Rejected,
            proof.WaitForRelease(14, 2_000));
        Assert.AreEqual(Switch2BluetoothInputLeaseReleaseResult.Rejected,
            proof.WaitForRelease(14, 2_000),
            "Retry must replay the same false exact-release task.");
        await result.Lease.ResourceRelease.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1,
            device.Service.Characteristic.DisableCalls);
        Assert.AreEqual(1, device.Service.Characteristic.DisposeCalls);
        Assert.AreEqual(1, device.Service.DisposeCalls);
        Assert.AreEqual(1, device.DisposeCalls,
            "A disposal failure must not skip later graph owners.");
        Assert.AreEqual(stage != 0,
            device.Service.Characteristic.Disposed);
        Assert.AreEqual(stage != 1, device.Service.Disposed);
        Assert.AreEqual(stage != 2, device.Disposed);
    }

    [TestMethod]
    public async Task AmbiguousHandlerDetachStillAttemptsNoneAndQuarantines()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        device.ThrowOnDetach = true;
        device.Service.Characteristic.ThrowOnDetach = true;
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter, timeoutMilliseconds: 20);
        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.IsTrue(result.Lease.TrySubscribeCccdNotify(10,
            (_, _, _, _, _) => { }, _ => { }));

        bool bounded = await result.Lease.
            BeginAndWaitForBoundedTeardownAsync(CancellationToken.None);
        Assert.IsFalse(bounded);
        Assert.AreEqual(1, device.DetachCalls);
        Assert.AreEqual(1, device.Service.Characteristic.DetachCalls);
        Assert.AreEqual(1, device.Service.Characteristic.DisableCalls,
            "CCCD None remains the sole best-effort compensation.");
        Assert.IsFalse(device.Disposed);
        Assert.IsFalse(device.Service.Disposed);
        Assert.IsFalse(device.Service.Characteristic.Disposed,
            "Ambiguous handler removal must quarantine the complete graph.");
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task WatcherTeardownWaitsForCandidateCallbackAndSwallowsIt()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var adapter = CreateAdapter(platform,
            new Switch2BluetoothCandidateRegistry(), 20);
        Assert.IsTrue(adapter.TryStartScan(1, LocalHost, _ =>
        {
            entered.Set();
            release.Wait();
            throw new InvalidOperationException("Synthetic callback failure.");
        }, out _));
        Task callback = Task.Run(() => watcher.Emit(1,
            BuildAdvertisement(
                Switch2AdvertisementCodec.ProController2ProductId), 1));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));

        Assert.IsFalse(await adapter.EndScanAsync(1));
        Assert.IsFalse(watcher.Disposed);
        Assert.IsFalse(adapter.TryStartScan(2, LocalHost, _ => { }, out var failure));
        Assert.AreEqual(Switch2BluetoothWindowsScanStartFailure.ScanAlreadyActive,
            failure, "A retired watcher still owns its entered callbacks.");
        Task<bool> exactDrain = adapter.EndScanAndDrainAsync(1);
        Assert.IsFalse(exactDrain.IsCompleted);
        release.Set();
        await callback.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await exactDrain.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(await adapter.EndScanAsync(1),
            "A retry observes the exact late drain, not a lost activeScan.");
        Assert.IsTrue(SpinWait.SpinUntil(() => watcher.Disposed,
            TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1L, adapter.CandidateCallbackFailureCount);
        entered.Dispose();
        release.Dispose();
    }

    [TestMethod]
    public async Task SteadyStateDuplicateAdmissionAllocatesNothingAfterWarmup()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var counter = new ObservationCounter();
        var adapter = CreateAdapter(platform,
            new Switch2BluetoothCandidateRegistry());
        Assert.IsTrue(adapter.TryStartScan(1, LocalHost, counter.Accept,
            out _));
        byte[] advertisement = BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId);
        for (int index = 0; index < 32; index++)
        {
            watcher.Emit(1, advertisement, index);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; index++)
        {
            watcher.Emit(1, advertisement, 100 + index);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated,
            "The adapter's steady duplicate path must remain allocation-free.");
        Assert.AreEqual(1_032, counter.Count);
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public void PlatformSurfaceCannotPairBondOrReconnect()
    {
        string[] names = typeof(ISwitch2BluetoothWindowsPlatform).
            GetMethods().Concat(typeof(ISwitch2BluetoothWindowsDevice).
                GetMethods()).Concat(typeof(
                ISwitch2BluetoothWindowsGattCharacteristic).GetMethods()).
            Select(method => method.Name).ToArray();
        foreach (string forbidden in new[]
        {
            "Pair", "Unpair", "Bond", "Associate", "Remember", "Reconnect",
            "Output", "Rumble", "Led", "Command",
        })
        {
            Assert.IsFalse(names.Any(name => name.Contains(forbidden,
                StringComparison.OrdinalIgnoreCase)), forbidden);
        }
    }

    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public async Task LazyConnectionDiscoveryPrecedesLinkValidation(bool duplex, bool connects)
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.ValidDuplexPro();
        device.Connected = false;
        device.ServiceQueryOverride = _ =>
        {
            device.Connected = connects;
            return ValueTask.FromResult(new Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattService>(true, new[] { device.Service }));
        };
        platform.EnqueueDevice(device);
        var observation = StartAndObserve(platform, watcher, out var adapter);
        var result = duplex ? await adapter.OpenRememberedDuplexAsync(observation) :
            await adapter.OpenRememberedInputAsync(observation);
        try
        {
            Assert.AreEqual(1, device.UncachedServiceQueries);
            Assert.AreEqual(connects, result.Succeeded, result.Failure.ToString());
            Assert.AreEqual(connects ? 1 : 0, device.ThroughputRequestCalls);
            if (connects)
            {
                Assert.AreEqual(duplex, result.Lease.HasHdRumbleOutput);
                Assert.AreEqual(duplex, result.Lease.HasPlayerLedOutput);
            }
            else
            {
                Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.DeviceDisconnected, result.Failure);
                Assert.AreEqual(0, device.Service.UncachedCharacteristicQueries);
                Assert.AreEqual(0, device.Service.Characteristic.EnableCalls);
                Assert.IsTrue(device.Service.Disposed);
                Assert.IsTrue(device.Disposed);
            }
        }
        finally
        {
            if (result.Lease != null) await RetireLease(result.Lease, 401);
            Assert.IsTrue(await adapter.EndScanAsync(1));
        }
    }

    [TestMethod]
    public async Task LazyConnectionServiceFailureDoesNotRequestThroughputOrCharacteristics()
    {
        var device = FakeDevice.Valid();
        device.Connected = false;
        device.ServiceQueryOverride = _ => ValueTask.FromResult(
            new Switch2BluetoothWindowsGattQuery<ISwitch2BluetoothWindowsGattService>(
                false, new[] { device.Service }));
        await AssertGattFailure(device, Switch2BluetoothWindowsOpenFailure.ServiceQueryFailed);
        Assert.AreEqual(1, device.UncachedServiceQueries);
        Assert.AreEqual(0, device.ThroughputRequestCalls);
        Assert.AreEqual(0, device.Service.UncachedCharacteristicQueries);
        Assert.IsTrue(device.Service.Disposed);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransientServiceDiscoveryRetriesBeforeRememberedInputOrDuplex(bool duplex)
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.ValidDuplexPro();
        device.Connected = false;
        device.ServiceQueryOverride = _ =>
        {
            if (device.UncachedServiceQueries < 3)
                return ValueTask.FromResult(new Switch2BluetoothWindowsGattQuery<
                    ISwitch2BluetoothWindowsGattService>(device.UncachedServiceQueries == 1 ?
                        Switch2BluetoothWindowsGattQueryStatus.Unreachable :
                        Switch2BluetoothWindowsGattQueryStatus.Success,
                        Array.Empty<ISwitch2BluetoothWindowsGattService>()));
            device.Connected = true;
            return ValueTask.FromResult(new Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattService>(true, new[] { device.Service }));
        };
        platform.EnqueueDevice(device);
        var observation = StartAndObserve(platform, watcher, out var adapter,
            timeoutMilliseconds: 5_000);
        var result = duplex ? await adapter.OpenRememberedDuplexAsync(observation) :
            await adapter.OpenRememberedInputAsync(observation);
        try
        {
            Assert.IsTrue(result.Succeeded, result.Failure.ToString());
            Assert.AreEqual(3, device.UncachedServiceQueries);
            Assert.AreEqual(1, device.ThroughputRequestCalls);
            Assert.AreEqual(duplex, result.Lease.HasHdRumbleOutput);
            Assert.AreEqual(duplex, result.Lease.HasPlayerLedOutput);
        }
        finally
        {
            if (result.Lease != null) await RetireLease(result.Lease, 402);
            Assert.IsTrue(await adapter.EndScanAsync(1));
        }
    }

    [TestMethod]
    public async Task CancelledRetryRetainsRememberedDeviceUntilLateServiceResult()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var device = FakeDevice.Valid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattService>>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ServiceQueryOverride = _ =>
        {
            if (device.UncachedServiceQueries == 1)
                return ValueTask.FromResult(new Switch2BluetoothWindowsGattQuery<
                    ISwitch2BluetoothWindowsGattService>(Switch2BluetoothWindowsGattQueryStatus.Unreachable,
                    Array.Empty<ISwitch2BluetoothWindowsGattService>()));
            entered.TrySetResult();
            return new(late.Task);
        };
        platform.EnqueueDevice(device);
        var observation = StartAndObserve(platform, watcher, out var adapter,
            timeoutMilliseconds: 5_000);
        using var cancellation = new CancellationTokenSource();
        var operation = adapter.OpenRememberedDuplexAsync(observation, cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.Cancelled, result.Failure);
            Assert.IsFalse(device.Disposed);
            Assert.AreEqual(0, device.Service.UncachedCharacteristicQueries);
        }
        finally
        {
            cancellation.Cancel();
            late.TrySetResult(new(true, new[] { device.Service }));
            await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(await adapter.EndScanAsync(1));
        }
        Assert.IsTrue(SpinWait.SpinUntil(() => device.Disposed, TimeSpan.FromSeconds(2)));
        Assert.IsTrue(device.Service.Disposed);
        Assert.AreEqual(2, device.UncachedServiceQueries);
        Assert.AreEqual(0, device.Service.UncachedCharacteristicQueries);
    }

    private static async Task AssertGattFailure(FakeDevice device,
        Switch2BluetoothWindowsOpenFailure expected)
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);
        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedInputAsync(observation);
        Assert.AreEqual(expected, result.Failure);
        Assert.IsTrue(device.Disposed);
    }

    [TestMethod]
    public async Task DuplexOpenBindsAndWritesExactProVibrationCharacteristic()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.ValidDuplexPro();
        byte[] calibrationRecord = Convert.FromHexString(
            "000880000770000770");
        var calibrationAddresses = new List<uint>();
        device.Service.CommandCharacteristic.WriteOverride = (request, _, _) =>
        {
            byte[] detachedRequest = request.ToArray();
            if (detachedRequest[0] ==
                    Switch2BluetoothMemoryReadCodec.CommandId)
            {
                byte length = detachedRequest[8];
                uint address = BinaryPrimitives.ReadUInt32LittleEndian(
                    detachedRequest.AsSpan(12, 4));
                calibrationAddresses.Add(address);
                var response = new byte[16 + length];
                response[0] = Switch2BluetoothMemoryReadCodec.CommandId;
                response[1] = 0x01;
                response[8] = length;
                BinaryPrimitives.WriteUInt32LittleEndian(
                    response.AsSpan(12, 4), address);
                if (length == calibrationRecord.Length)
                {
                    calibrationRecord.CopyTo(response, 16);
                }
                else
                {
                    response.AsSpan(16, length).Fill(0xFF);
                }
                device.Service.ResponseCharacteristic.Emit(response, 1);
            }
            else
            {
                device.Service.ResponseCharacteristic.Emit(
                    Convert.FromHexString("0901000000000000"), 1);
            }
            return ValueTask.FromResult(true);
        };
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);

        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedDuplexAsync(observation);

        Assert.IsTrue(result.Succeeded, result.Failure.ToString());
        Assert.AreEqual(4, device.Service.UncachedCharacteristicQueries);
        Assert.AreEqual(1, device.ThroughputRequestCalls);
        Assert.IsTrue(result.Lease.ThroughputOptimizedRequested);
        Assert.IsTrue(result.Lease.HasHdRumbleOutput);
        Assert.IsTrue(result.Lease.HasPlayerLedOutput);
        Switch2BluetoothCalibrationReadResult calibrationRead = await
            result.Lease.ReadCalibrationAsync(
                Switch2ControllerModel.ProController2, 17,
                CancellationToken.None);
        Assert.IsTrue(calibrationRead.Succeeded,
            $"{calibrationRead.Failure}/{calibrationRead.CommandFailure}");
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.AdoptedFactory,
            calibrationRead.Calibration.Left.Status);
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.AdoptedFactory,
            calibrationRead.Calibration.Right.Status);
        Assert.AreEqual(Switch2BluetoothMemoryReadChannelFailure.None,
            calibrationRead.OptionalUserCommandFailure);
        CollectionAssert.AreEqual(new uint[]
        {
            Switch2CalibrationCodec.PrimaryFactoryStickAddress,
            Switch2CalibrationCodec.SecondaryFactoryStickAddress,
            Switch2CalibrationCodec.PrimaryUserStickAddress,
            Switch2CalibrationCodec.SecondaryUserStickAddress,
        }, calibrationAddresses);
        Assert.AreEqual(4,
            device.Service.CommandCharacteristic.WriteCalls);
        Assert.IsTrue(result.Lease.TrySubscribeCccdNotify(23,
            (_, _, _, _, _) => { }, _ => { }));
        Assert.IsTrue(result.Lease.TryBindHdRumbleLifetime(
            Switch2ControllerModel.ProController2, 17, 23));

        Switch2BluetoothHdRumblePhysicalWriter writer = new(result.Lease,
            Switch2ControllerModel.ProController2, 17, 23,
            initialCounter: 9);
        Switch2HdRumblePhysicalSubmission stop =
            Switch2HdRumblePhysicalSubmission.CreateStop(17, 23,
                deliveryEpoch: 31);
        Assert.IsTrue(writer.TryWrite(stop).Succeeded);
        Assert.AreEqual(1, device.Service.OutputCharacteristic.WriteCalls);
        Assert.IsTrue(device.Service.OutputCharacteristic.
            LastWriteWithoutResponse);
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            device.Service.OutputCharacteristic.LastWrite,
            out byte counter, out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right, out _));
        Assert.AreEqual((byte)9, counter);
        Assert.AreEqual(default(Switch2HdRumbleGroup), left);
        Assert.AreEqual(default(Switch2HdRumbleGroup), right);

        Assert.IsTrue(result.Lease.TryRequestPlayerLed(3,
            Switch2ControllerModel.ProController2, 17, 23).Accepted);
        await result.Lease.PlayerLedOperation;
        Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.None,
            result.Lease.LastPlayerLedFailure);
        CollectionAssert.AreEqual(Convert.FromHexString(
            "099101070004000007000000"),
            device.Service.CommandCharacteristic.LastWrite);
        Assert.AreEqual(5,
            device.Service.CommandCharacteristic.WriteCalls);

        Assert.IsTrue(result.Lease.TryUnsubscribeCccdNone(23));
        await result.Lease.ResourceRelease.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.IsTrue(device.Service.OutputCharacteristic.Disposed);
        Assert.IsTrue(device.Service.CommandCharacteristic.Disposed);
        Assert.IsTrue(device.Service.ResponseCharacteristic.Disposed);
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task PlayerLedCommandCoalescesNewestStateBehindActiveRequest()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.ValidDuplexPro();
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);

        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedDuplexAsync(observation);
        Assert.IsTrue(result.Succeeded, result.Failure.ToString());
        Assert.IsTrue(result.Lease.TrySubscribeCccdNotify(23,
            (_, _, _, _, _) => { }, _ => { }));
        Assert.IsTrue(result.Lease.TryBindHdRumbleLifetime(
            Switch2ControllerModel.ProController2, 17, 23));

        var writes = new List<byte[]>();
        var firstWritten = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.Service.CommandCharacteristic.WriteOverride =
            (request, _, _) =>
            {
                writes.Add(request.ToArray());
                if (writes.Count == 1)
                {
                    firstWritten.TrySetResult(true);
                }
                else
                {
                    device.Service.ResponseCharacteristic.Emit(
                        Convert.FromHexString("0901000000000000"), 2);
                }
                return ValueTask.FromResult(true);
            };

        Assert.IsTrue(result.Lease.TryRequestPlayerLed(1,
            Switch2ControllerModel.ProController2, 17, 23).Accepted);
        await firstWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(result.Lease.TryRequestPlayerLedMask(0x02,
            Switch2ControllerModel.ProController2, 17, 23).Accepted,
            "The newest LED state must be retained rather than rejected busy.");

        device.Service.ResponseCharacteristic.Emit(
            Convert.FromHexString("0901000000000000"), 1);
        await result.Lease.PlayerLedOperation.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, writes.Count);
        Assert.AreEqual((byte)0x01, writes[0][8]);
        Assert.AreEqual((byte)0x02, writes[1][8],
            "The newest arbitrary four-segment state must remain exact.");
        Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.None,
            result.Lease.LastPlayerLedFailure);

        Assert.IsTrue(result.Lease.TryUnsubscribeCccdNone(23));
        await result.Lease.ResourceRelease.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task DuplexOpenRejectsMissingModelSpecificOutput()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        FakeDevice device = FakeDevice.Valid();
        platform.EnqueueDevice(device);
        Switch2BluetoothCandidateObservation observation = StartAndObserve(
            platform, watcher, out var adapter);

        Switch2BluetoothWindowsOpenResult result = await adapter.
            OpenRememberedDuplexAsync(observation);

        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.
            OutputCharacteristicIdentityMismatch, result.Failure);
        Assert.IsTrue(device.Disposed);
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    private static Switch2BluetoothCandidateObservation StartAndObserve(
        FakePlatform platform, FakeWatcher watcher,
        out Switch2BluetoothWindowsAdapter adapter,
        int timeoutMilliseconds = 100)
    {
        var observations = new List<Switch2BluetoothCandidateObservation>();
        adapter = CreateAdapter(platform,
            new Switch2BluetoothCandidateRegistry(), timeoutMilliseconds);
        Assert.IsTrue(adapter.TryStartScan(1, LocalHost, observations.Add,
            out var failure), failure.ToString());
        watcher.Emit(0x112233445566, BuildAdvertisement(
            Switch2AdvertisementCodec.ProController2ProductId), 1);
        Assert.AreEqual(1, observations.Count);
        return observations[0];
    }

    private static Switch2BluetoothWindowsAdapter CreateAdapter(
        FakePlatform platform, Switch2BluetoothCandidateRegistry registry,
        int timeoutMilliseconds = 100,
        ISwitch2PersistentPeerIdentityDeriver identityDeriver = null) =>
        new(platform, registry,
            TimeSpan.FromMilliseconds(timeoutMilliseconds),
            TimeSpan.FromMilliseconds(timeoutMilliseconds), identityDeriver);

    private static async Task RetireLease(
        Switch2BluetoothWindowsInputLease lease, ulong generation)
    {
        Assert.IsTrue(lease.TrySubscribeCccdNotify(generation,
            (_, _, _, _, _) => { }, _ => { }));
        Assert.IsTrue(lease.TryUnsubscribeCccdNone(generation));
        await lease.ResourceRelease.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static byte[] BuildAdvertisement(ushort productId,
        byte[] rememberedHost = null)
    {
        byte[] value = new byte[
            Switch2AdvertisementCodec.ManufacturerValueLength];
        value[0] = 0x01;
        value[2] = 0x03;
        value[3] = 0x7E;
        value[4] = 0x05;
        value[5] = (byte)productId;
        value[6] = (byte)(productId >> 8);
        value[8] = 0x01;
        value[16] = 0x0F;
        rememberedHost ??= LocalHost;
        for (int index = 0; index < rememberedHost.Length; index++)
        {
            value[10 + index] =
                rememberedHost[rememberedHost.Length - 1 - index];
        }
        return value;
    }

    private sealed class ObservationCounter
    {
        internal int Count { get; private set; }
        internal void Accept(Switch2BluetoothCandidateObservation value) =>
            Count++;
    }

    private sealed class FakePlatform : ISwitch2BluetoothWindowsPlatform
    {
        private readonly Queue<FakeWatcher> watchers;
        private readonly Queue<ISwitch2BluetoothWindowsDevice> devices = new();

        internal FakePlatform(params FakeWatcher[] watchers)
        {
            this.watchers = new Queue<FakeWatcher>(watchers);
        }

        internal List<ulong> OpenedAddresses { get; } = new();
        internal List<Switch2BluetoothWindowsAddressType> OpenedAddressTypes
            { get; } = new();
        internal Func<CancellationToken,
            ValueTask<ISwitch2BluetoothWindowsDevice>> OpenOverride { get; set; }

        internal void EnqueueDevice(ISwitch2BluetoothWindowsDevice device) =>
            devices.Enqueue(device);

        public ISwitch2BluetoothWindowsAdvertisementWatcher
            CreateAdvertisementWatcher() => watchers.Dequeue();

        public ValueTask<ISwitch2BluetoothWindowsDevice> OpenDeviceAsync(
            ulong bluetoothAddress,
            Switch2BluetoothWindowsAddressType addressType,
            CancellationToken cancellationToken)
        {
            OpenedAddresses.Add(bluetoothAddress);
            OpenedAddressTypes.Add(addressType);
            return OpenOverride != null ? OpenOverride(cancellationToken) :
                ValueTask.FromResult(devices.Dequeue());
        }
    }

    private sealed class FakeWatcher :
        ISwitch2BluetoothWindowsAdvertisementWatcher
    {
        private Switch2BluetoothWindowsAdvertisementHandler received;
        private Switch2BluetoothWindowsWatcherStoppedHandler stopped;
        private Switch2BluetoothWindowsAdvertisementHandler capturedReceived;

        public bool IsConfiguredForActiveScanning => ActiveConfigured;
        internal bool ActiveConfigured { get; private set; }
        internal bool Started { get; private set; }
        internal bool Stopped { get; private set; }
        internal bool Detached { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool ThrowOnConfigure { get; set; }
        internal bool ThrowOnStart { get; set; }
        internal bool ThrowOnAttach { get; init; }
        internal Task DrainOverride { get; init; }
        internal Action StartAction { get; set; }

        public void ConfigureActiveScanning()
        {
            if (ThrowOnConfigure)
            {
                throw new InvalidOperationException(
                    "Synthetic watcher configuration failure.");
            }
            ActiveConfigured = true;
        }

        public void AttachHandlers(
            Switch2BluetoothWindowsAdvertisementHandler received,
            Switch2BluetoothWindowsWatcherStoppedHandler stopped)
        {
            this.received = received;
            capturedReceived = received;
            this.stopped = stopped;
            if (ThrowOnAttach)
                throw new InvalidOperationException("Synthetic partial handler install.");
        }

        public void Start()
        {
            StartAction?.Invoke();
            if (ThrowOnStart)
            {
                throw new InvalidOperationException(
                    "Synthetic watcher start failure.");
            }
            Started = true;
        }
        public void Stop() => Stopped = true;

        public Task DetachHandlersAndDrainAsync()
        {
            Detached = true;
            received = null;
            stopped = null;
            return DrainOverride ?? Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;

        internal void Emit(ulong address, byte[] value, long qpc,
            ushort companyId = Switch2AdvertisementCodec.
                NintendoBluetoothCompanyId,
            byte matchingCompanySections = 1,
            Switch2BluetoothWindowsAddressType addressType =
                Switch2BluetoothWindowsAddressType.Public) => received?.Invoke(
                address, addressType, companyId, matchingCompanySections,
                value, qpc);

        internal void EmitCaptured(ulong address, byte[] value, long qpc) =>
            capturedReceived?.Invoke(address,
                Switch2BluetoothWindowsAddressType.Public,
                Switch2AdvertisementCodec.NintendoBluetoothCompanyId, 1,
                value, qpc);

        internal void EmitStopped() => stopped?.Invoke();
    }

    private sealed class FakeDevice : ISwitch2BluetoothWindowsDevice
    {
        private Switch2BluetoothWindowsDisconnectedHandler disconnected;
        private Switch2BluetoothWindowsDisconnectedHandler capturedDisconnected;
        private ISwitch2BluetoothWindowsGattService[] services;

        private FakeDevice(FakeService service)
        {
            Service = service;
            services = new ISwitch2BluetoothWindowsGattService[] { service };
        }

        internal FakeService Service { get; }
        internal bool Connected { get; set; } = true;
        internal bool Disposed { get; private set; }
        internal int UncachedServiceQueries { get; private set; }
        internal Guid LastServiceQueryUuid { get; private set; }
        internal bool ThrowServiceQuery { get; set; }
        internal bool ThrowOnDetach { get; set; }
        internal bool ThrowOnDispose { get; set; }
        internal int DetachCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal bool EmitDisconnectInlineOnAttach { get; set; }
        internal int EmitDisconnectOnConnectedRead { get; set; }
        internal int ConnectedReadCount { get; private set; }
        internal int ThroughputRequestCalls { get; private set; }
        internal bool ThroughputRequestSucceeds { get; set; }
        internal Func<CancellationToken, ValueTask<
            Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattService>>>
            ServiceQueryOverride { get; set; }
        public bool IsConnected
        {
            get
            {
                ConnectedReadCount++;
                if (ConnectedReadCount == EmitDisconnectOnConnectedRead)
                {
                    disconnected?.Invoke();
                }
                return Connected;
            }
        }

        public bool TryCopyStableAssociationIdentity(Span<byte> destination,
            out int bytesWritten)
        {
            ReadOnlySpan<byte> identity = "fake-switch2-device"u8;
            bytesWritten = 0;
            if (destination.Length < identity.Length)
            {
                return false;
            }
            identity.CopyTo(destination);
            bytesWritten = identity.Length;
            return true;
        }

        public bool TryRequestThroughputOptimized()
        {
            ThroughputRequestCalls++;
            return ThroughputRequestSucceeds;
        }

        internal static FakeDevice Valid() => new(FakeService.Valid());

        internal static FakeDevice ValidDuplexPro()
        {
            FakeService service = FakeService.Valid();
            service.OutputCharacteristic = FakeCharacteristic.Output(
                Switch2BluetoothHdRumblePhysicalWriter.
                    ProController2CharacteristicUuid);
            service.CommandCharacteristic = FakeCharacteristic.Command();
            service.ResponseCharacteristic = FakeCharacteristic.Response();
            return new FakeDevice(service)
            {
                ThroughputRequestSucceeds = true,
            };
        }

        internal static FakeDevice WithDuplicateServices()
        {
            var device = Valid();
            device.services = new ISwitch2BluetoothWindowsGattService[]
            {
                device.Service,
                FakeService.Valid(),
            };
            return device;
        }

        internal static FakeDevice WithWrongServiceUuid()
        {
            var service = FakeService.Valid();
            service.UuidOverride = Guid.NewGuid();
            return new FakeDevice(service);
        }

        internal static FakeDevice WithDuplicateCharacteristics()
        {
            var service = FakeService.Valid();
            service.Characteristics = new
                ISwitch2BluetoothWindowsGattCharacteristic[]
            {
                service.Characteristic,
                FakeCharacteristic.Valid(),
            };
            return new FakeDevice(service);
        }

        internal static FakeDevice WithWrongCharacteristicUuid()
        {
            var service = FakeService.Valid();
            service.Characteristic.UuidOverride = Guid.NewGuid();
            return new FakeDevice(service);
        }

        internal static FakeDevice WithWritableCommon05()
        {
            var service = FakeService.Valid();
            service.Characteristic.Properties = Switch2GattProperty.Read |
                Switch2GattProperty.Notify | Switch2GattProperty.Write;
            service.Characteristic.OnlyReadNotify = false;
            return new FakeDevice(service);
        }

        internal static FakeDevice WithExtraNativeProperty()
        {
            var service = FakeService.Valid();
            service.Characteristic.OnlyReadNotify = false;
            return new FakeDevice(service);
        }

        internal static FakeDevice WithThrowingServiceQuery()
        {
            var device = Valid();
            device.ThrowServiceQuery = true;
            return device;
        }

        internal static FakeDevice WithThrowingCharacteristicQuery()
        {
            var service = FakeService.Valid();
            service.ThrowCharacteristicQuery = true;
            return new FakeDevice(service);
        }

        internal static FakeDevice WithThrowingNotificationSetup()
        {
            var service = FakeService.Valid();
            service.Characteristic.EnableOverride = _ =>
                ValueTask.FromException<bool>(new InvalidOperationException(
                    "Synthetic CCCD failure."));
            return new FakeDevice(service);
        }

        public void AttachDisconnectedHandler(
            Switch2BluetoothWindowsDisconnectedHandler disconnected)
        {
            this.disconnected = disconnected;
            capturedDisconnected = disconnected;
            if (EmitDisconnectInlineOnAttach)
            {
                disconnected();
            }
        }

        public Task DetachDisconnectedHandlerAndDrainAsync()
        {
            DetachCalls++;
            if (ThrowOnDetach)
            {
                throw new InvalidOperationException(
                    "Synthetic disconnect-detach ambiguity.");
            }
            disconnected = null;
            return Task.CompletedTask;
        }

        public ValueTask<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattService>>
            GetServicesForUuidUncachedAsync(Guid serviceUuid,
                CancellationToken cancellationToken)
        {
            UncachedServiceQueries++;
            LastServiceQueryUuid = serviceUuid;
            if (ThrowServiceQuery)
            {
                throw new InvalidOperationException(
                    "Synthetic service query failure.");
            }
            if (ServiceQueryOverride != null)
            {
                return ServiceQueryOverride(cancellationToken);
            }
            return ValueTask.FromResult(new Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattService>(true, services));
        }

        internal void EmitDisconnected()
        {
            Connected = false;
            disconnected?.Invoke();
        }

        internal void EmitCapturedDisconnected() =>
            capturedDisconnected?.Invoke();

        public void Dispose()
        {
            DisposeCalls++;
            if (ThrowOnDispose)
            {
                throw new InvalidOperationException(
                    "Synthetic device disposal ambiguity.");
            }
            Disposed = true;
        }
    }

    private sealed class FakeService : ISwitch2BluetoothWindowsGattService
    {
        private FakeService(FakeCharacteristic characteristic)
        {
            Characteristic = characteristic;
            Characteristics = new
                ISwitch2BluetoothWindowsGattCharacteristic[]
            {
                characteristic,
            };
        }

        internal static FakeService Valid() =>
            new(FakeCharacteristic.Valid());

        internal FakeCharacteristic Characteristic { get; }
        internal FakeCharacteristic OutputCharacteristic { get; set; }
        internal FakeCharacteristic CommandCharacteristic { get; set; }
        internal FakeCharacteristic ResponseCharacteristic { get; set; }
        internal Guid UuidOverride { get; set; } =
            Switch2InputCodec.ServiceUuid;
        internal ISwitch2BluetoothWindowsGattCharacteristic[]
            Characteristics { get; set; }
        internal bool Disposed { get; private set; }
        internal int UncachedCharacteristicQueries { get; private set; }
        internal Guid LastCharacteristicQueryUuid { get; private set; }
        internal bool ThrowCharacteristicQuery { get; set; }
        internal bool ThrowOnDispose { get; set; }
        internal int DisposeCalls { get; private set; }
        internal Func<CancellationToken, ValueTask<
            Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattCharacteristic>>>
            CharacteristicQueryOverride { get; set; }
        public Guid Uuid => UuidOverride;

        public ValueTask<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattCharacteristic>>
            GetCharacteristicsForUuidUncachedAsync(Guid characteristicUuid,
                CancellationToken cancellationToken)
        {
            UncachedCharacteristicQueries++;
            LastCharacteristicQueryUuid = characteristicUuid;
            if (ThrowCharacteristicQuery)
            {
                throw new InvalidOperationException(
                    "Synthetic characteristic query failure.");
            }
            if (CharacteristicQueryOverride != null)
            {
                return CharacteristicQueryOverride(cancellationToken);
            }
            if (OutputCharacteristic != null &&
                characteristicUuid == OutputCharacteristic.Uuid)
            {
                return ValueTask.FromResult(
                    new Switch2BluetoothWindowsGattQuery<
                        ISwitch2BluetoothWindowsGattCharacteristic>(true,
                        new ISwitch2BluetoothWindowsGattCharacteristic[]
                        {
                            OutputCharacteristic,
                        }));
            }
            if (CommandCharacteristic != null &&
                characteristicUuid == CommandCharacteristic.Uuid)
            {
                return ValueTask.FromResult(
                    new Switch2BluetoothWindowsGattQuery<
                        ISwitch2BluetoothWindowsGattCharacteristic>(true,
                        new ISwitch2BluetoothWindowsGattCharacteristic[]
                        {
                            CommandCharacteristic,
                        }));
            }
            if (ResponseCharacteristic != null &&
                characteristicUuid == ResponseCharacteristic.Uuid)
            {
                return ValueTask.FromResult(
                    new Switch2BluetoothWindowsGattQuery<
                        ISwitch2BluetoothWindowsGattCharacteristic>(true,
                        new ISwitch2BluetoothWindowsGattCharacteristic[]
                        {
                            ResponseCharacteristic,
                        }));
            }
            IReadOnlyList<ISwitch2BluetoothWindowsGattCharacteristic> result =
                characteristicUuid == Switch2InputCodec.
                    Common05CharacteristicUuid ? Characteristics :
                    Array.Empty<ISwitch2BluetoothWindowsGattCharacteristic>();
            return ValueTask.FromResult(new Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattCharacteristic>(true, result));
        }

        public void Dispose()
        {
            DisposeCalls++;
            if (ThrowOnDispose)
            {
                throw new InvalidOperationException(
                    "Synthetic service disposal ambiguity.");
            }
            Disposed = true;
        }
    }

    private sealed class FakeCharacteristic :
        ISwitch2BluetoothWindowsGattCharacteristic
    {
        private Switch2BluetoothWindowsValueChangedHandler valueChanged;
        private Switch2BluetoothWindowsValueChangedHandler capturedValueChanged;

        internal static FakeCharacteristic Valid() => new();

        internal static FakeCharacteristic Output(Guid uuid) => new()
        {
            UuidOverride = uuid,
            Properties = Switch2GattProperty.WriteWithoutResponse,
            OnlyReadNotify = false,
            WriteSucceeds = true,
        };

        internal static FakeCharacteristic Command() => new()
        {
            UuidOverride = Switch2BluetoothPlayerLedCodec.
                CommandWriteCharacteristicUuid,
            Properties = Switch2GattProperty.Write,
            OnlyReadNotify = false,
            WriteSucceeds = true,
        };

        internal static FakeCharacteristic Response() => new()
        {
            UuidOverride = Switch2BluetoothPlayerLedCodec.
                CommandResponseCharacteristicUuid,
            Properties = Switch2GattProperty.Notify,
            OnlyReadNotify = false,
        };

        internal Guid UuidOverride { get; set; } =
            Switch2InputCodec.Common05CharacteristicUuid;
        internal Switch2GattProperty Properties { get; set; } =
            Switch2GattProperty.Read | Switch2GattProperty.Notify;
        internal bool OnlyReadNotify { get; set; } = true;
        internal bool Disposed { get; private set; }
        internal bool ThrowOnDetach { get; set; }
        internal bool ThrowOnDispose { get; set; }
        internal int DetachCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal int EnableCalls { get; private set; }
        internal int DisableCalls { get; private set; }
        internal bool WriteSucceeds { get; set; }
        internal int WriteCalls { get; private set; }
        internal bool LastWriteWithoutResponse { get; private set; }
        internal byte[] LastWrite { get; private set; } = Array.Empty<byte>();
        internal Func<CancellationToken, ValueTask<bool>>
            EnableOverride { get; set; }
        internal Func<CancellationToken, ValueTask<bool>>
            DisableOverride { get; set; }
        internal Func<ReadOnlyMemory<byte>, bool, CancellationToken,
            ValueTask<bool>> WriteOverride { get; set; }
        public Guid Uuid => UuidOverride;
        public Switch2GattProperty EvidencedProperties => Properties;
        public bool HasOnlyReadAndNotifyProperties => OnlyReadNotify;

        public void AttachValueChangedHandler(
            Switch2BluetoothWindowsValueChangedHandler valueChanged)
        {
            this.valueChanged = valueChanged;
            capturedValueChanged = valueChanged;
        }

        public Task DetachValueChangedHandlerAndDrainAsync()
        {
            DetachCalls++;
            if (ThrowOnDetach)
            {
                throw new InvalidOperationException(
                    "Synthetic value-detach ambiguity.");
            }
            valueChanged = null;
            return Task.CompletedTask;
        }

        public ValueTask<bool> ConfigureNotificationsAsync(bool enabled,
            CancellationToken cancellationToken)
        {
            if (enabled)
            {
                EnableCalls++;
                return EnableOverride?.Invoke(cancellationToken) ??
                    ValueTask.FromResult(true);
            }
            DisableCalls++;
            return DisableOverride?.Invoke(cancellationToken) ??
                ValueTask.FromResult(true);
        }

        public ValueTask<bool> WriteValueAsync(ReadOnlyMemory<byte> value,
            bool writeWithoutResponse, CancellationToken cancellationToken)
        {
            WriteCalls++;
            LastWriteWithoutResponse = writeWithoutResponse;
            LastWrite = value.ToArray();
            return WriteOverride?.Invoke(value, writeWithoutResponse,
                cancellationToken) ?? ValueTask.FromResult(WriteSucceeds);
        }

        internal void Emit(byte[] body, long qpc) =>
            valueChanged?.Invoke(body, qpc);

        internal void EmitCaptured(byte[] body, long qpc) =>
            capturedValueChanged?.Invoke(body, qpc);

        public void Dispose()
        {
            DisposeCalls++;
            if (ThrowOnDispose)
            {
                throw new InvalidOperationException(
                    "Synthetic characteristic disposal ambiguity.");
            }
            Disposed = true;
        }
    }
}
