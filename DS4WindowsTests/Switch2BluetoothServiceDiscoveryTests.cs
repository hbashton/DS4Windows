using DS4Windows.Switch2;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using QueryStatus = DS4Windows.Switch2.Switch2BluetoothWindowsGattQueryStatus;
using ServiceQuery = DS4Windows.Switch2.Switch2BluetoothWindowsGattQuery<DS4Windows.Switch2.ISwitch2BluetoothWindowsGattService>;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothServiceDiscoveryTests
{
    [DataTestMethod]
    [DataRow(GattCommunicationStatus.Success, (int)QueryStatus.Success)]
    [DataRow(GattCommunicationStatus.Unreachable, (int)QueryStatus.Unreachable)]
    [DataRow(GattCommunicationStatus.ProtocolError, (int)QueryStatus.ProtocolError)]
    [DataRow(GattCommunicationStatus.AccessDenied, (int)QueryStatus.AccessDenied)]
    [DataRow((GattCommunicationStatus)255, (int)QueryStatus.Failed)]
    public void WindowsStatusIsNotFlattenedIntoAnAmbiguousBoolean(
        GattCommunicationStatus native, int expected) =>
        Assert.AreEqual((QueryStatus)expected, Switch2BluetoothWinRtPlatform.ClassifyServiceStatus(native));

    [TestMethod]
    public void MissingAndLegacyFailureStatusesCannotBeRetriedAsUnreachable()
    {
        Assert.AreEqual(QueryStatus.Failed, Switch2BluetoothWinRtPlatform.ClassifyServiceStatus(null));
        Assert.AreEqual(QueryStatus.Failed, new ServiceQuery(false, null).Status);
        Assert.IsFalse(default(ServiceQuery).Succeeded);
    }

    [DataTestMethod]
    [DataRow((int)QueryStatus.Unreachable, 0, 10)]
    [DataRow((int)QueryStatus.Unreachable, 1, 10)]
    [DataRow((int)QueryStatus.Success, 0, 10)]
    [DataRow((int)QueryStatus.Success, -1, 1)] // malformed, not an empty discovery
    [DataRow((int)QueryStatus.Success, 1, 1)]
    [DataRow((int)QueryStatus.Success, 2, 1)] // owner must reject duplicate services
    [DataRow((int)QueryStatus.AccessDenied, 0, 1)]
    [DataRow((int)QueryStatus.ProtocolError, 0, 1)]
    [DataRow((int)QueryStatus.Failed, 0, 1)]
    public async Task RetryBudgetAndSpacingPreserveFinalResultOwnership(
        int statusValue, int itemCount, int attempts)
    {
        var status = (QueryStatus)statusValue;
        var services = new List<FakeService>();
        var delays = new List<TimeSpan>();
        var device = new FakeDevice((_, _) =>
        {
            ISwitch2BluetoothWindowsGattService[] items = itemCount < 0 ? null :
                Enumerable.Range(0, itemCount).Select(_ =>
                {
                    var service = new FakeService();
                    services.Add(service);
                    return (ISwitch2BluetoothWindowsGattService)service;
                }).ToArray();
            return ValueTask.FromResult(new ServiceQuery(status, items));
        });
        ServiceQuery result = await Switch2BluetoothServiceDiscovery.QueryAsync(device,
            Switch2InputCodec.ServiceUuid, CancellationToken.None,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        Assert.AreEqual(attempts, device.QueryCalls);
        Assert.AreEqual(Switch2InputCodec.ServiceUuid, device.LastUuid);
        Assert.AreEqual(attempts - 1, delays.Count);
        Assert.IsTrue(delays.All(delay => delay == TimeSpan.FromMilliseconds(500)));
        Assert.AreEqual(status, result.Status);
        Assert.AreEqual(0, device.DisposeCalls, "The caller owns the device, not the retry helper.");
        foreach (var service in services)
            Assert.AreEqual(result.Items?.Contains(service) == true ? 0 : 1,
                service.DisposeCalls, "Only intermediate results are disposed by retry.");
        if (result.Items != null)
            foreach (var service in result.Items) service.Dispose();
        Assert.IsTrue(services.All(service => service.DisposeCalls == 1));
    }

    [TestMethod]
    public async Task IntermediateResourcesRetireBeforeAnotherQueryStarts()
    {
        var first = new FakeService();
        var final = new FakeService();
        var device = new FakeDevice((call, _) =>
        {
            if (call == 1) return ValueTask.FromResult(new ServiceQuery(QueryStatus.Unreachable, new[] { first }));
            Assert.AreEqual(1, first.DisposeCalls);
            return ValueTask.FromResult(new ServiceQuery(true, new[] { final }));
        });
        var result = await Switch2BluetoothServiceDiscovery.QueryAsync(device,
            Switch2InputCodec.ServiceUuid, CancellationToken.None, (_, _) => Task.CompletedTask);
        Assert.AreEqual(2, device.QueryCalls);
        Assert.AreSame(final, result.Items.Single());
        Assert.AreEqual(0, final.DisposeCalls);
        final.Dispose();
    }

    [TestMethod]
    public async Task CleanupFailureRetiresOtherResultsAndDoesNotRetry()
    {
        var broken = new FakeService { ThrowOnDispose = true };
        var second = new FakeService();
        var device = new FakeDevice((_, _) => ValueTask.FromResult(
            new ServiceQuery(QueryStatus.Unreachable, new[] { broken, second })));
        int delays = 0;
        await Assert.ThrowsExceptionAsync<IOException>(() => Switch2BluetoothServiceDiscovery.QueryAsync(
            device, Switch2InputCodec.ServiceUuid, CancellationToken.None,
            (_, _) => { ++delays; return Task.CompletedTask; }).AsTask());
        Assert.AreEqual(1, broken.DisposeCalls);
        Assert.AreEqual(1, second.DisposeCalls);
        Assert.AreEqual(1, device.QueryCalls);
        Assert.AreEqual(0, delays);
    }

    [TestMethod]
    public async Task ExistingDeadlineCancelsRealBackoffWithoutAnotherQuery()
    {
        using var cancellation = new CancellationTokenSource();
        var device = new FakeDevice((_, _) => ValueTask.FromResult(
            new ServiceQuery(true, Array.Empty<ISwitch2BluetoothWindowsGattService>())));
        Task<ServiceQuery> operation = Switch2BluetoothServiceDiscovery.QueryAsync(device,
            Switch2InputCodec.ServiceUuid, cancellation.Token).AsTask();
        Assert.AreEqual(1, device.QueryCalls);
        cancellation.CancelAfter(20);
        await AssertCancelled(operation, cancellation.Token);
        Assert.AreEqual(1, device.QueryCalls);
        Assert.AreEqual(0, device.DisposeCalls);
    }

    [TestMethod]
    public async Task PreCancellationDoesNotEnterNativeQuery()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var device = new FakeDevice((_, _) => throw new AssertFailedException("No query expected."));
        await AssertCancelled(Switch2BluetoothServiceDiscovery.QueryAsync(device,
            Switch2InputCodec.ServiceUuid, cancellation.Token).AsTask(), cancellation.Token);
        Assert.AreEqual(0, device.QueryCalls);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NonCooperativeNativeQueryKeepsOwnershipUntilItsResultArrives(bool successful)
    {
        using var cancellation = new CancellationTokenSource();
        var late = new TaskCompletionSource<ServiceQuery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService();
        var device = new FakeDevice((_, _) => new(late.Task));
        Task<ServiceQuery> operation = Switch2BluetoothServiceDiscovery.QueryAsync(device,
            Switch2InputCodec.ServiceUuid, cancellation.Token).AsTask();
        cancellation.Cancel();
        Assert.IsFalse(operation.IsCompleted,
            "The outer bounded observer, not this helper, transfers late native ownership.");
        Assert.AreEqual(1, device.QueryCalls);
        Assert.AreEqual(0, device.DisposeCalls);
        late.SetResult(new ServiceQuery(successful ? QueryStatus.Success : QueryStatus.Unreachable,
            new[] { service }));
        if (successful)
        {
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreSame(service, result.Items.Single());
            Assert.AreEqual(0, service.DisposeCalls);
            service.Dispose(); // existing outer late-result cleanup owns this
        }
        else
        {
            await AssertCancelled(operation, cancellation.Token);
        }
        Assert.AreEqual(1, service.DisposeCalls);
        Assert.AreEqual(1, device.QueryCalls);
        Assert.AreEqual(0, device.DisposeCalls);
    }

    private static async Task AssertCancelled(Task task, CancellationToken token)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Fail("Cancellation expected."); }
        catch (OperationCanceledException error) { Assert.AreEqual(token, error.CancellationToken); }
    }

    private sealed class FakeDevice(Func<int, CancellationToken, ValueTask<ServiceQuery>> query)
        : ISwitch2BluetoothWindowsDevice
    {
        internal int QueryCalls;
        internal int DisposeCalls;
        internal Guid LastUuid;
        public bool IsConnected => throw new NotSupportedException();
        public ValueTask<ServiceQuery> GetServicesForUuidUncachedAsync(Guid uuid, CancellationToken token)
        {
            LastUuid = uuid;
            return query(++QueryCalls, token);
        }
        public bool TryCopyStableAssociationIdentity(Span<byte> destination, out int bytesWritten) =>
            throw new NotSupportedException();
        public void AttachDisconnectedHandler(Switch2BluetoothWindowsDisconnectedHandler handler) =>
            throw new NotSupportedException();
        public Task DetachDisconnectedHandlerAndDrainAsync() => throw new NotSupportedException();
        public void Dispose() => ++DisposeCalls;
    }

    private sealed class FakeService : ISwitch2BluetoothWindowsGattService
    {
        internal int DisposeCalls;
        internal bool ThrowOnDispose;
        public Guid Uuid => Switch2InputCodec.ServiceUuid;
        public ValueTask<Switch2BluetoothWindowsGattQuery<ISwitch2BluetoothWindowsGattCharacteristic>>
            GetCharacteristicsForUuidUncachedAsync(Guid uuid, CancellationToken token) =>
            throw new NotSupportedException();
        public void Dispose()
        {
            ++DisposeCalls;
            if (ThrowOnDispose) throw new IOException("Injected service release failure.");
        }
    }
}
