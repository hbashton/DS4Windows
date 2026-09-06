using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothWindowsAssociationOwnerTests
{
    private static readonly byte[] HostAddress =
        Convert.FromHexString("FFEEDDCCBBAA");

    [TestMethod]
    public async Task TransientServiceDiscoveryRetriesBeforeAssociationCommands()
    {
        var graph = FakeGraph.Valid();
        graph.Device.Connected = false;
        graph.Device.ServiceQueryOverride = _ =>
        {
            if (graph.Device.ServiceQueries < 3)
                return ValueTask.FromResult(new Switch2BluetoothWindowsGattQuery<
                    ISwitch2BluetoothWindowsGattService>(graph.Device.ServiceQueries == 1 ?
                        Switch2BluetoothWindowsGattQueryStatus.Unreachable :
                        Switch2BluetoothWindowsGattQueryStatus.Success,
                        Array.Empty<ISwitch2BluetoothWindowsGattService>()));
            graph.Device.Connected = true;
            return ValueTask.FromResult(new Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattService>(true, new[] { graph.Service }));
        };

        var result = await Switch2BluetoothWindowsAssociationOwner.ExecuteAsync(
            graph.Platform, 1, Switch2BluetoothWindowsAddressType.Public,
            HostAddress, TimeSpan.FromSeconds(5), CancellationToken.None,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Failure.ToString());
        Assert.AreEqual(3, graph.Device.ServiceQueries);
        Assert.AreEqual(4, graph.Command.Writes.Count);
        Assert.IsTrue(graph.Service.Disposed);
        Assert.IsTrue(graph.Device.Disposed);
    }

    [TestMethod]
    public async Task CancelledRetryRetainsAssociationDeviceUntilLateServiceResult()
    {
        var graph = FakeGraph.Valid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattService>>(TaskCreationOptions.RunContinuationsAsynchronously);
        graph.Device.ServiceQueryOverride = _ =>
        {
            if (graph.Device.ServiceQueries == 1)
                return ValueTask.FromResult(new Switch2BluetoothWindowsGattQuery<
                    ISwitch2BluetoothWindowsGattService>(Switch2BluetoothWindowsGattQueryStatus.Unreachable,
                    Array.Empty<ISwitch2BluetoothWindowsGattService>()));
            entered.TrySetResult();
            return new(late.Task);
        };
        using var cancellation = new CancellationTokenSource();
        var operation = Switch2BluetoothWindowsAssociationOwner.ExecuteAsync(
            graph.Platform, 1, Switch2BluetoothWindowsAddressType.Public,
            HostAddress, TimeSpan.FromSeconds(5), CancellationToken.None,
            cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.Cancelled, result.Failure);
            Assert.IsFalse(graph.Device.Disposed);
            Assert.AreEqual(0, graph.Command.Writes.Count);
        }
        finally
        {
            cancellation.Cancel();
            late.TrySetResult(new(true, new[] { graph.Service }));
            await operation.WaitAsync(TimeSpan.FromSeconds(2));
        }
        await WaitUntilAsync(() => graph.Device.Disposed);
        Assert.IsTrue(graph.Service.Disposed);
        Assert.AreEqual(2, graph.Device.ServiceQueries);
        Assert.AreEqual(0, graph.Command.Writes.Count);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LazyConnectionChecksLinkOnlyAfterUncachedDiscovery(bool connects)
    {
        var graph = FakeGraph.Valid();
        graph.Device.Connected = false;
        graph.Device.ServiceQueryOverride = _ =>
        {
            graph.Device.Connected = connects;
            return ValueTask.FromResult(new Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattService>(true, new[] { graph.Service }));
        };

        var result = await Switch2BluetoothWindowsAssociationOwner.ExecuteAsync(
            graph.Platform, 1, Switch2BluetoothWindowsAddressType.Public,
            HostAddress, TimeSpan.FromSeconds(2), CancellationToken.None,
            CancellationToken.None);

        Assert.AreEqual(1, graph.Device.ServiceQueries,
            "Opening a Windows device object does not establish its GATT link.");
        Assert.AreEqual(connects, result.Succeeded, result.Failure.ToString());
        if (!connects)
        {
            Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.DeviceDisconnected,
                result.Failure);
            Assert.IsFalse(graph.Service.QueryEntered.Task.IsCompleted);
            Assert.AreEqual(0, graph.Response.EnableCalls);
        }
        Assert.AreEqual(connects ? 4 : 0, graph.Command.Writes.Count);
        Assert.IsTrue(graph.Service.Disposed);
        Assert.IsTrue(graph.Device.Disposed);
    }

    [TestMethod]
    public async Task LazyConnectionServiceFailureDoesNotRunAssociationCeremony()
    {
        var graph = FakeGraph.Valid();
        graph.Device.Connected = false;
        graph.Device.ServiceQueryOverride = _ => ValueTask.FromResult(
            new Switch2BluetoothWindowsGattQuery<ISwitch2BluetoothWindowsGattService>(
                false, new[] { graph.Service }));
        var result = await Switch2BluetoothWindowsAssociationOwner.ExecuteAsync(
            graph.Platform, 1, Switch2BluetoothWindowsAddressType.Public,
            HostAddress, TimeSpan.FromSeconds(2), CancellationToken.None,
            CancellationToken.None);
        Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.ServiceQueryFailed,
            result.Failure);
        Assert.AreEqual(1, graph.Device.ServiceQueries);
        Assert.AreEqual(0, graph.Command.Writes.Count);
        Assert.IsFalse(graph.Service.QueryEntered.Task.IsCompleted);
        Assert.IsTrue(graph.Service.Disposed);
        Assert.IsTrue(graph.Device.Disposed);
    }

    [TestMethod]
    public async Task LazyConnectionCancelledServiceQueryRetainsDeviceUntilLateCompletion()
    {
        var graph = FakeGraph.Valid();
        graph.Device.Connected = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattService>>(TaskCreationOptions.RunContinuationsAsynchronously);
        graph.Device.ServiceQueryOverride = _ => { entered.SetResult(); return new(late.Task); };
        using var cancellation = new CancellationTokenSource();
        var operation = Switch2BluetoothWindowsAssociationOwner.ExecuteAsync(
            graph.Platform, 1, Switch2BluetoothWindowsAddressType.Public,
            HostAddress, TimeSpan.FromSeconds(2), CancellationToken.None,
            cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.Cancelled, result.Failure);
            Assert.IsFalse(graph.Device.Disposed);
            Assert.AreEqual(0, graph.Command.Writes.Count);
        }
        finally
        {
            cancellation.Cancel();
            late.TrySetResult(new(true, new[] { graph.Service }));
            await operation.WaitAsync(TimeSpan.FromSeconds(2));
        }
        await WaitUntilAsync(() => graph.Device.Disposed);
        Assert.IsTrue(graph.Service.Disposed);
        Assert.AreEqual(0, graph.Command.Writes.Count);
    }

    [TestMethod]
    public async Task CompleteOwnerUsesAdvertisedAddressTypeAndExactCeremony()
    {
        var graph = FakeGraph.Valid();

        Switch2BluetoothWindowsAssociationResult result = await
            Switch2BluetoothWindowsAssociationOwner.ExecuteAsync(
                graph.Platform, 0x112233445566,
                Switch2BluetoothWindowsAddressType.Random, HostAddress,
                TimeSpan.FromSeconds(2), CancellationToken.None,
                CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0x112233445566UL,
            graph.Platform.LastOpenedAddress);
        Assert.AreEqual(Switch2BluetoothWindowsAddressType.Random,
            graph.Platform.LastOpenedAddressType);
        CollectionAssert.AreEqual(new byte[] { 1, 4, 2, 3 },
            graph.Command.Writes.Select(value => value[3]).ToArray());
        Assert.AreEqual(1, graph.Response.EnableCalls);
        Assert.AreEqual(1, graph.Response.DisableCalls);
        Assert.AreEqual(1, graph.Response.DetachCalls);
        Assert.IsTrue(graph.Command.Disposed);
        Assert.IsTrue(graph.Response.Disposed);
        Assert.IsTrue(graph.Service.Disposed);
        Assert.IsTrue(graph.Device.Disposed);
    }

    [TestMethod]
    public async Task RejectedResponseStopsAtExactStepWithoutRetry()
    {
        var graph = FakeGraph.Valid();
        graph.Command.ResponseForCall = call => call == 2 ?
            Convert.FromHexString("1500000000000000") :
            Convert.FromHexString("1501000000000000");

        Switch2BluetoothWindowsAssociationResult result = await
            Switch2BluetoothWindowsAssociationOwner.ExecuteAsync(
                graph.Platform, 1,
                Switch2BluetoothWindowsAddressType.Public, HostAddress,
                TimeSpan.FromSeconds(2), CancellationToken.None,
                CancellationToken.None);

        Assert.AreEqual(
            Switch2BluetoothWindowsAssociationFailure.CeremonyRejected,
            result.Failure);
        Assert.AreEqual(Switch2BluetoothAssociationStep.SetHostAddress,
            result.LastCompletedStep);
        Assert.AreEqual(2, graph.Command.Writes.Count);
    }

    [TestMethod]
    public async Task WrongCharacteristicIdentityFailsBeforeAnyWrite()
    {
        var graph = FakeGraph.Valid();
        graph.Response.UuidOverride = Guid.NewGuid();

        Switch2BluetoothWindowsAssociationResult result = await
            Switch2BluetoothWindowsAssociationOwner.ExecuteAsync(
                graph.Platform, 1,
                Switch2BluetoothWindowsAddressType.Public, HostAddress,
                TimeSpan.FromSeconds(2), CancellationToken.None,
                CancellationToken.None);

        Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.
            ResponseCharacteristicIdentityMismatch, result.Failure);
        Assert.AreEqual(0, graph.Command.Writes.Count);
    }

    [TestMethod]
    public async Task NonCooperativeQueryRetainsParentsUntilLateResultDisposes()
    {
        var graph = FakeGraph.Valid();
        var late = new TaskCompletionSource<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattCharacteristic>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        graph.Service.QueryOverride = (uuid, _) => uuid ==
            Switch2BluetoothAssociationCodec.CommandWriteCharacteristicUuid ?
            new ValueTask<Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattCharacteristic>>(late.Task) :
            graph.Service.QueryNormally(uuid);
        using var cancellation = new CancellationTokenSource();

        Task<Switch2BluetoothWindowsAssociationResult> operation =
            Switch2BluetoothWindowsAssociationOwner.ExecuteAsync(
                graph.Platform, 1,
                Switch2BluetoothWindowsAddressType.Public, HostAddress,
                TimeSpan.FromSeconds(2), CancellationToken.None,
                cancellation.Token).AsTask();
        await graph.Service.QueryEntered.Task;
        cancellation.Cancel();
        Switch2BluetoothWindowsAssociationResult result = await operation;
        Assert.AreEqual(Switch2BluetoothWindowsAssociationFailure.Cancelled,
            result.Failure);
        Assert.IsFalse(graph.Service.Disposed);
        Assert.IsFalse(graph.Device.Disposed);

        late.SetResult(new Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattCharacteristic>(true,
            new ISwitch2BluetoothWindowsGattCharacteristic[]
            {
                graph.Command,
            }));
        await WaitUntilAsync(() => graph.Device.Disposed);
        Assert.IsTrue(graph.Command.Disposed);
        Assert.IsTrue(graph.Service.Disposed);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class FakeGraph
    {
        private FakeGraph()
        {
            Response = FakeCharacteristic.Response();
            Command = FakeCharacteristic.Command(Response);
            Service = new FakeService(Command, Response);
            Device = new FakeDevice(Service);
            Platform = new FakePlatform(Device);
        }

        internal static FakeGraph Valid() => new();
        internal FakePlatform Platform { get; }
        internal FakeDevice Device { get; }
        internal FakeService Service { get; }
        internal FakeCharacteristic Command { get; }
        internal FakeCharacteristic Response { get; }
    }

    private sealed class FakePlatform : ISwitch2BluetoothWindowsPlatform
    {
        private readonly FakeDevice device;
        internal FakePlatform(FakeDevice device) => this.device = device;
        internal ulong LastOpenedAddress { get; private set; }
        internal Switch2BluetoothWindowsAddressType LastOpenedAddressType
            { get; private set; }

        public ISwitch2BluetoothWindowsAdvertisementWatcher
            CreateAdvertisementWatcher() => throw new NotSupportedException();

        public ValueTask<ISwitch2BluetoothWindowsDevice> OpenDeviceAsync(
            ulong bluetoothAddress,
            Switch2BluetoothWindowsAddressType addressType,
            CancellationToken cancellationToken)
        {
            LastOpenedAddress = bluetoothAddress;
            LastOpenedAddressType = addressType;
            return ValueTask.FromResult<ISwitch2BluetoothWindowsDevice>(device);
        }
    }

    private sealed class FakeDevice : ISwitch2BluetoothWindowsDevice
    {
        private readonly FakeService service;
        internal FakeDevice(FakeService service) => this.service = service;
        internal bool Disposed { get; private set; }
        internal bool Connected { get; set; } = true;
        internal int ServiceQueries { get; private set; }
        internal Func<CancellationToken, ValueTask<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattService>>> ServiceQueryOverride { get; set; }
        public bool IsConnected => Connected && !Disposed;
        public bool TryCopyStableAssociationIdentity(Span<byte> destination,
            out int bytesWritten)
        {
            ReadOnlySpan<byte> identity = "fake-association-device"u8;
            bytesWritten = 0;
            if (destination.Length < identity.Length)
            {
                return false;
            }
            identity.CopyTo(destination);
            bytesWritten = identity.Length;
            return true;
        }
        public void AttachDisconnectedHandler(
            Switch2BluetoothWindowsDisconnectedHandler disconnected) =>
            throw new NotSupportedException();
        public Task DetachDisconnectedHandlerAndDrainAsync() =>
            Task.CompletedTask;
        public ValueTask<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattService>>
            GetServicesForUuidUncachedAsync(Guid serviceUuid,
                CancellationToken cancellationToken)
        {
            ServiceQueries++;
            return ServiceQueryOverride?.Invoke(cancellationToken) ?? ValueTask.FromResult(
                new Switch2BluetoothWindowsGattQuery<
                    ISwitch2BluetoothWindowsGattService>(true,
                    new ISwitch2BluetoothWindowsGattService[] { service }));
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeService : ISwitch2BluetoothWindowsGattService
    {
        private readonly FakeCharacteristic command;
        private readonly FakeCharacteristic response;
        internal FakeService(FakeCharacteristic command,
            FakeCharacteristic response)
        {
            this.command = command;
            this.response = response;
        }
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource QueryEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<Guid, CancellationToken, ValueTask<
            Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattCharacteristic>>> QueryOverride
            { get; set; }
        public Guid Uuid => Switch2BluetoothAssociationCodec.ServiceUuid;

        public ValueTask<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattCharacteristic>>
            GetCharacteristicsForUuidUncachedAsync(Guid characteristicUuid,
                CancellationToken cancellationToken)
        {
            QueryEntered.TrySetResult();
            return QueryOverride?.Invoke(characteristicUuid,
                cancellationToken) ?? QueryNormally(characteristicUuid);
        }

        internal ValueTask<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattCharacteristic>> QueryNormally(
            Guid uuid)
        {
            ISwitch2BluetoothWindowsGattCharacteristic item = uuid ==
                Switch2BluetoothAssociationCodec.
                    CommandWriteCharacteristicUuid ? command : response;
            return ValueTask.FromResult(new Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattCharacteristic>(true,
                new[] { item }));
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeCharacteristic :
        ISwitch2BluetoothWindowsGattCharacteristic
    {
        private readonly FakeCharacteristic response;
        private Switch2BluetoothWindowsValueChangedHandler valueChanged;
        private FakeCharacteristic(Guid uuid, Switch2GattProperty properties,
            FakeCharacteristic response = null)
        {
            UuidOverride = uuid;
            EvidencedProperties = properties;
            this.response = response;
        }
        internal static FakeCharacteristic Response() => new(
            Switch2BluetoothAssociationCodec.CommandResponseCharacteristicUuid,
            Switch2GattProperty.Notify);
        internal static FakeCharacteristic Command(FakeCharacteristic response) =>
            new(Switch2BluetoothAssociationCodec.
                    CommandWriteCharacteristicUuid,
                Switch2GattProperty.WriteWithoutResponse, response);
        internal Guid UuidOverride { get; set; }
        internal List<byte[]> Writes { get; } = new();
        internal Func<int, byte[]> ResponseForCall { get; set; } = _ =>
            Convert.FromHexString("1501000000000000");
        internal bool Disposed { get; private set; }
        internal int EnableCalls { get; private set; }
        internal int DisableCalls { get; private set; }
        internal int DetachCalls { get; private set; }
        public Guid Uuid => UuidOverride;
        public Switch2GattProperty EvidencedProperties { get; }
        public bool HasOnlyReadAndNotifyProperties => false;
        public void AttachValueChangedHandler(
            Switch2BluetoothWindowsValueChangedHandler handler) =>
            valueChanged = handler;
        public Task DetachValueChangedHandlerAndDrainAsync()
        {
            DetachCalls++;
            valueChanged = null;
            return Task.CompletedTask;
        }
        public ValueTask<bool> ConfigureNotificationsAsync(bool enabled,
            CancellationToken cancellationToken)
        {
            if (enabled) EnableCalls++; else DisableCalls++;
            return ValueTask.FromResult(true);
        }
        public ValueTask<bool> WriteValueAsync(ReadOnlyMemory<byte> value,
            bool writeWithoutResponse, CancellationToken cancellationToken)
        {
            Writes.Add(value.ToArray());
            response.valueChanged?.Invoke(ResponseForCall(Writes.Count), 1);
            return ValueTask.FromResult(true);
        }
        public void Dispose() => Disposed = true;
    }
}
