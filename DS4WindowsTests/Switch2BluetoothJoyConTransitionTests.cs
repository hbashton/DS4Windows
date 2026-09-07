using System.Collections.Concurrent;
using System.Threading.Channels;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

public sealed partial class Switch2BluetoothWindowsAdapterTests
{
    [DataTestMethod]
    [DataRow("success")]
    [DataRow("automatic")]
    [DataRow("store")]
    [DataRow("stale")]
    [DataRow("cancelled")]
    [DataRow("handoff-rejected")]
    public async Task ManualJoyConLinkAndUnlinkUseExactTokensAndFirstSelectedPad(string scenario)
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        foreach (var model in new[] { Switch2ControllerModel.JoyCon2Left, Switch2ControllerModel.JoyCon2Right,
            Switch2ControllerModel.JoyCon2Left, Switch2ControllerModel.JoyCon2Right,
            Switch2ControllerModel.JoyCon2Left, Switch2ControllerModel.JoyCon2Right })
            platform.EnqueueDevice(CreateJoyConResponder(model));
        using var identities = new Switch2PersistentPeerIdentityDeriver(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var service = new Switch2RuntimeRegistrationService(new InputControllerRegistrationTable(4));
        Assert.IsTrue(service.TryOpen(1, out _));
        var slots = Channel.CreateUnbounded<InputControllerSlotToken>();
        var catalog = new TransitionPairCatalog();
        var handoffs = new List<TestJoyConOutputHandoff>();
        bool automatic = false;
        var coordinator = new Switch2BluetoothProductionCoordinator(
            CreateAdapter(platform, new Switch2BluetoothCandidateRegistry(), 2000, identities), service,
            new JoyConTransitionHost(), token => slots.Writer.TryWrite(token), null, catalog, () => automatic,
            null, beginOutputHandoff: token =>
            {
                if (scenario == "handoff-rejected") throw new InvalidOperationException("Synthetic busy output");
                var handoff = new TestJoyConOutputHandoff(token);
                handoffs.Add(handoff);
                return handoff;
            });
        Assert.IsTrue(coordinator.TryStart(1, LocalHost, out _));
        try
        {
            watcher.Emit(0x100000000041, BuildAdvertisement(Switch2AdvertisementCodec.JoyCon2LeftProductId), 1);
            var oldLeft = await slots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            watcher.Emit(0x100000000042, BuildAdvertisement(Switch2AdvertisementCodec.JoyCon2RightProductId), 2);
            var oldRight = await slots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var choices = coordinator.GetJoyConPairCandidates();
            var left = choices.Single(c => c.Model == Switch2ControllerModel.JoyCon2Left);
            var right = choices.Single(c => c.Model == Switch2ControllerModel.JoyCon2Right);
            Assert.AreEqual(oldLeft, left.SlotToken);
            Assert.AreEqual(oldRight, right.SlotToken);
            var joinedResult = await coordinator.CreateAndActivateJoyConPairAsync(left.Id, right.Id,
                preferredCandidateId: right.Id);
            if (scenario == "handoff-rejected")
            {
                Assert.IsFalse(joinedResult.Succeeded);
                Assert.AreEqual(2, ActiveSlots(service));
                Assert.AreEqual(2, coordinator.GetJoyConPairCandidates().Length,
                    "A rejected handoff must not make live singles disappear from Link choices.");
                return;
            }
            Assert.IsTrue(joinedResult.Succeeded, joinedResult.Failure.ToString());
            var pair = await slots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(oldRight, handoffs[0].Predecessor);
            Assert.AreEqual(oldRight.Slot, pair.Slot);
            Assert.AreSame(pair.Registration.Device, handoffs[0].Successor);
            Assert.AreEqual(1, handoffs[0].Disposals);
            Assert.AreEqual(pair, coordinator.GetJoinedJoyConToken(pair.Registration.Device));
            Assert.IsFalse(coordinator.GetJoinedJoyConToken(oldRight.Registration.Device).IsValid);
            if (scenario == "automatic") automatic = true;
            catalog.RejectDelete = scenario == "store";
            var result = await coordinator.UnlinkJoyConsAsync(scenario == "stale" ? oldRight : pair,
                scenario == "cancelled" ? new CancellationToken(true) : default);
            if (scenario != "success")
            {
                Assert.IsFalse(result.Succeeded);
                Assert.AreEqual(1, ActiveSlots(service));
                Assert.AreEqual(pair, coordinator.GetJoinedJoyConToken(pair.Registration.Device));
                Assert.IsTrue(catalog.TryLoadAll(out var records));
                Assert.AreEqual(1, records.Length);
                return;
            }
            Assert.IsTrue(result.Succeeded, result.Failure.ToString());
            var newLeft = await slots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var newRight = await slots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(2, ActiveSlots(service));
            Assert.AreEqual(pair.Slot, newLeft.Slot, "Unlink retains the joined pad for one half.");
            Assert.AreNotEqual(newLeft.Slot, newRight.Slot);
            Assert.AreEqual(2, coordinator.GetJoyConPairCandidates().Length);
            Assert.IsTrue(coordinator.GetJoyConPairCandidates().All(c => c.Id != left.Id && c.Id != right.Id));
            Assert.IsFalse(coordinator.GetJoinedJoyConToken(pair.Registration.Device).IsValid);
            Assert.IsTrue(catalog.TryLoadAll(out var remaining));
            Assert.AreEqual(0, remaining.Length, "Unlink must also clear remembered linking, not Bluetooth bonds.");
            Assert.IsFalse((await coordinator.UnlinkJoyConsAsync(pair)).Succeeded);
        }
        finally
        {
            Assert.IsTrue(await coordinator.StopAsync());
            Assert.IsTrue(service.TryClose(1, 5000, out var failure), failure.Kind.ToString());
        }
    }

    private sealed class TestJoyConOutputHandoff(InputControllerSlotToken predecessor) : ISwitch2JoyConOutputHandoff
    {
        internal InputControllerSlotToken Predecessor => predecessor;
        internal Switch2RuntimeInputDevice Successor;
        internal int Disposals;
        public int InputSlot => predecessor.Slot;
        public void PrepareSuccessor(Switch2RuntimeInputDevice device) { Assert.IsNull(Successor); Successor = device; }
        public void Dispose() => Disposals++;
    }
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DataRow(false, true)]
    public async Task JoyConAppearsStandaloneThenPairsThroughExactRetirement(bool automatic, bool manual = false)
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var left = CreateJoyConResponder(Switch2ControllerModel.JoyCon2Left);
        var right = CreateJoyConResponder(Switch2ControllerModel.JoyCon2Right);
        var replacementLeft = CreateJoyConResponder(Switch2ControllerModel.JoyCon2Left);
        var replacementRight = CreateJoyConResponder(Switch2ControllerModel.JoyCon2Right);
        platform.EnqueueDevice(left);
        platform.EnqueueDevice(right);
        platform.EnqueueDevice(replacementLeft);
        if (!automatic) platform.EnqueueDevice(replacementRight);
        using var identities = new Switch2PersistentPeerIdentityDeriver(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var adapter = CreateAdapter(platform, new Switch2BluetoothCandidateRegistry(), 2000, identities);
        var service = new Switch2RuntimeRegistrationService(new InputControllerRegistrationTable(4));
        Assert.IsTrue(service.TryOpen(1, out _));
        var slots = Channel.CreateUnbounded<InputControllerSlotToken>();
        var host = new JoyConTransitionHost();
        var messages = new ConcurrentQueue<string>();
        bool autoEnabled = automatic;
        var coordinator = new Switch2BluetoothProductionCoordinator(adapter, service, host,
            token => slots.Writer.TryWrite(token), messages.Enqueue, new TransitionPairCatalog(), () => autoEnabled);
        Assert.IsTrue(coordinator.TryStart(1, LocalHost, out _));
        try
        {
            watcher.Emit(0x100000000011, BuildAdvertisement(Switch2AdvertisementCodec.JoyCon2LeftProductId), 1);
            var standalone = await slots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, ActiveSlots(service));
            Assert.AreEqual(1, coordinator.GetJoyConPairCandidates().Length,
                "An automatically active half must remain selectable for later pairing.");
            Assert.IsTrue((await coordinator.ActivateJoyConSeparatelyAsync(
                coordinator.GetJoyConPairCandidates()[0].Id)).Succeeded,
                "The old separate action is an idempotent no-op for an active half.");
            watcher.Emit(0x100000000012, BuildAdvertisement(Switch2AdvertisementCodec.JoyCon2RightProductId), 2);
            var next = await slots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (!automatic)
            {
                Assert.AreEqual(2, ActiveSlots(service), string.Join("\n", messages));
                Assert.AreEqual(2, coordinator.GetJoyConPairCandidates().Length);
                Assert.AreEqual(0, host.Terminals);
                if (manual)
                {
                    var choices = coordinator.GetJoyConPairCandidates();
                    int leftId = choices.Single(c => c.Model == Switch2ControllerModel.JoyCon2Left).Id;
                    int rightId = choices.Single(c => c.Model == Switch2ControllerModel.JoyCon2Right).Id;
                    Assert.IsTrue((await coordinator.CreateAndActivateJoyConPairAsync(leftId, rightId)).Succeeded);
                    Assert.IsFalse((await coordinator.CreateAndActivateJoyConPairAsync(leftId, rightId)).Succeeded,
                        "Old UI selections must not pair the successor again.");
                }
                else
                {
                    autoEnabled = true;
                    Assert.AreEqual(1, await coordinator.ReconcileAutomaticJoyConPairsAsync());
                }
                next = await slots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(right.Disposed);
            }
            Assert.AreNotEqual(standalone, next);
            Assert.AreEqual(1, ActiveSlots(service), string.Join("\n", messages));
            Assert.AreEqual(0, coordinator.GetJoyConPairCandidates().Length);
            Assert.IsTrue(left.Disposed, "Former native owner must fully release before pair publication.");
            Assert.AreEqual(automatic ? 1 : 2, host.Terminals);
            Assert.AreEqual(automatic ? 1 : 2, host.Removals);
            Assert.AreEqual(automatic ? 3 : 4, platform.OpenedAddresses.Count);
            Assert.IsFalse(replacementLeft.Disposed);
        }
        finally
        {
            Assert.IsTrue(await coordinator.StopAsync());
            Assert.IsTrue(service.TryClose(1, 5000, out var failure), failure.Kind.ToString());
        }
    }

    [TestMethod]
    public async Task JoinedPhysicalDisconnectAutomaticallyRestoresLiveHalf()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var left = CreateJoyConResponder(Switch2ControllerModel.JoyCon2Left);
        var right = CreateJoyConResponder(Switch2ControllerModel.JoyCon2Right);
        var joinedLeft = CreateJoyConResponder(Switch2ControllerModel.JoyCon2Left);
        var survivor = CreateJoyConResponder(Switch2ControllerModel.JoyCon2Left);
        foreach (var device in new[] {left, right, joinedLeft, survivor}) platform.EnqueueDevice(device);
        using var identities = new Switch2PersistentPeerIdentityDeriver(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var service = new Switch2RuntimeRegistrationService(new InputControllerRegistrationTable(4));
        Assert.IsTrue(service.TryOpen(1, out _));
        var slots = Channel.CreateUnbounded<InputControllerSlotToken>();
        var coordinator = new Switch2BluetoothProductionCoordinator(
            CreateAdapter(platform, new Switch2BluetoothCandidateRegistry(), 2000, identities), service,
            new JoyConTransitionHost(), token => slots.Writer.TryWrite(token), null, null, () => true);
        Assert.IsTrue(coordinator.TryStart(1, LocalHost, out _));
        try
        {
            watcher.Emit(0x100000000021, BuildAdvertisement(Switch2AdvertisementCodec.JoyCon2LeftProductId), 1);
            await slots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            watcher.Emit(0x100000000022, BuildAdvertisement(Switch2AdvertisementCodec.JoyCon2RightProductId), 2);
            var pair = await slots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            right.EmitDisconnected();
            var single = await slots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreNotEqual(pair, single);
            Assert.AreEqual(1, ActiveSlots(service));
            Assert.AreEqual(Switch2ControllerModel.JoyCon2Left, coordinator.GetJoyConPairCandidates().Single().Model);
            Assert.IsTrue(joinedLeft.Disposed);
            Assert.IsFalse(survivor.Disposed);
            Assert.AreEqual(4, platform.OpenedAddresses.Count,
                "The disconnected side must not be reopened by survivor recovery.");
        }
        finally
        {
            Assert.IsTrue(await coordinator.StopAsync());
            Assert.IsTrue(service.TryClose(1, 5000, out var failure), failure.Kind.ToString());
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task JoyConReopenRequiresExactCleanReleaseAndCannotReplay(bool ambiguous)
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var first = CreateJoyConResponder(Switch2ControllerModel.JoyCon2Left);
        first.ThrowOnDispose = ambiguous;
        platform.EnqueueDevice(first);
        platform.EnqueueDevice(CreateJoyConResponder(Switch2ControllerModel.JoyCon2Left));
        using var identities = new Switch2PersistentPeerIdentityDeriver(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var adapter = CreateAdapter(platform, new Switch2BluetoothCandidateRegistry(), 2000, identities);
        var observations = new List<Switch2BluetoothCandidateObservation>();
        Assert.IsTrue(adapter.TryStartScan(1, LocalHost, observations.Add, out _));
        watcher.Emit(0x100000000031, BuildAdvertisement(Switch2AdvertisementCodec.JoyCon2LeftProductId), 1);
        var opened = await adapter.OpenRememberedDuplexAsync(observations.Single());
        Assert.IsTrue(opened.Succeeded);
        Assert.IsFalse((await adapter.ReopenReleasedJoyConAsync(opened.Lease, default)).Succeeded);
        bool released = await opened.Lease.BeginAndWaitForResourceReleaseAsync();
        Assert.AreEqual(!ambiguous, released);
        var reopened = await adapter.ReopenReleasedJoyConAsync(opened.Lease, default);
        Assert.AreEqual(!ambiguous, reopened.Succeeded);
        if (!ambiguous)
        {
            Assert.AreNotEqual(opened.Lease.Admission, reopened.Lease.Admission);
            Assert.AreEqual(opened.Lease.PersistentPeerId, reopened.Lease.PersistentPeerId);
            Assert.IsFalse((await adapter.ReopenReleasedJoyConAsync(opened.Lease, default)).Succeeded);
            Assert.IsTrue(await reopened.Lease.BeginAndWaitForResourceReleaseAsync());
        }
        Assert.AreEqual(ambiguous ? 1 : 2, platform.OpenedAddresses.Count);
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    private static int ActiveSlots(Switch2RuntimeRegistrationService service) =>
        service.Table.GetSnapshot().Count(slot => slot.State == InputControllerSlotState.Attached);

    private static FakeDevice CreateJoyConResponder(Switch2ControllerModel model)
    {
        var device = CreateCalibrationResponder();
        device.Service.OutputCharacteristic = FakeCharacteristic.Output(
            Switch2BluetoothHdRumblePhysicalWriter.CharacteristicUuidFor(model));
        return device;
    }

    private sealed class JoyConTransitionHost : ISwitch2ControlServiceSlotHost
    {
        internal int Terminals;
        internal int Removals;
        public Switch2ControlServiceSlotHostResult TryPrepare(in Switch2ControlServiceSlotLease lease) =>
            Switch2ControlServiceSlotHostResult.Success(Switch2ControlServiceSlotHostOperation.Prepare);
        public Switch2ControlServiceSlotHostResult TryAbort(in Switch2ControlServiceSlotLease lease) =>
            Switch2ControlServiceSlotHostResult.Success(Switch2ControlServiceSlotHostOperation.Abort);
        public Switch2ControlServiceSlotHostResult TryRemove(in Switch2ControlServiceSlotLease lease)
        {
            Interlocked.Increment(ref Removals);
            return Switch2ControlServiceSlotHostResult.Success(Switch2ControlServiceSlotHostOperation.Remove);
        }
        public Switch2ControlServiceSlotHostResult TryDispatch(in Switch2ControlServiceSlotLease lease,
            DS4Device sender, Switch2RuntimeReportEventArgs report)
        {
            Assert.AreSame(lease.Token.Registration.Device, sender);
            if (report.Kind == Switch2RuntimeReportKind.TerminalNeutral) Interlocked.Increment(ref Terminals);
            return Switch2ControlServiceSlotHostResult.Success(report.Kind == Switch2RuntimeReportKind.TerminalNeutral ?
                Switch2ControlServiceSlotHostOperation.DispatchTerminalNeutral : Switch2ControlServiceSlotHostOperation.DispatchRegular);
        }
    }

    private sealed class TransitionPairCatalog : ISwitch2JoyConPairCatalog
    {
        internal bool RejectDelete;
        private readonly Dictionary<Switch2JoyConPairId, Switch2JoyConPairRecord> pairs = new();
        public bool TryLoadAll(out Switch2JoyConPairRecord[] records) { records = pairs.Values.ToArray(); return true; }
        public bool TryLoad(Switch2JoyConPairId id, out Switch2JoyConPairRecord record) => pairs.TryGetValue(id, out record);
        public bool TryReplace(in Switch2JoyConPairRecord record, ulong revision)
        {
            if (revision != 0 || pairs.ContainsKey(record.PairId)) return false;
            pairs.Add(record.PairId, record); return true;
        }
        public bool TryDelete(Switch2JoyConPairId id, ulong revision) =>
            !RejectDelete && pairs.TryGetValue(id, out var record) && record.Revision == revision && pairs.Remove(id);
    }
}
