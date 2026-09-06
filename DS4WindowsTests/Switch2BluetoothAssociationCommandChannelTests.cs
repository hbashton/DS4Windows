using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothAssociationCommandChannelTests
{
    [TestMethod]
    public async Task ResponseSubscriptionPrecedesWriteAndCompletesExchange()
    {
        var events = new List<string>();
        var command = FakeCharacteristic.Command(events);
        var response = FakeCharacteristic.Response(events);
        command.WriteOverride = (request, _, _) =>
        {
            events.Add("write");
            response.Emit(Convert.FromHexString("1501000000000000"));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothAssociationCommandChannel(command,
            response);

        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        Assert.IsTrue(await channel.ExchangeAsync(
            Convert.FromHexString("159101030001000000"),
            CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "attach", "notify-on", "write" },
            events);
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
        CollectionAssert.AreEqual(new[]
        {
            "attach", "notify-on", "write", "detach", "notify-off",
        }, events);
    }

    [TestMethod]
    public async Task InvalidResponseTerminallyRejectsSuccessor()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (_, _, _) =>
        {
            response.Emit(Convert.FromHexString("1500000000000000"));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothAssociationCommandChannel(command,
            response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));

        Assert.IsFalse(await channel.ExchangeAsync(
            Convert.FromHexString("159101030001000000"),
            CancellationToken.None));
        Assert.IsFalse(await channel.ExchangeAsync(
            Convert.FromHexString("159101030001000000"),
            CancellationToken.None));
        Assert.AreEqual(1, command.WriteCalls);
    }

    [TestMethod]
    public async Task NotificationBeforeAnyPendingRequestIsNotReplayed()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        var responseWritten = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        command.WriteOverride = (_, _, _) =>
        {
            responseWritten.TrySetResult();
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothAssociationCommandChannel(command,
            response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        response.Emit(Convert.FromHexString("1501000000000000"));

        using var cancellation = new CancellationTokenSource();
        Task<bool> exchange = channel.ExchangeAsync(
            Convert.FromHexString("159101030001000000"), cancellation.Token).
            AsTask();
        await responseWritten.Task;
        Assert.IsFalse(exchange.IsCompleted);
        response.Emit(Convert.FromHexString("1501000000000000"));
        Assert.IsTrue(await exchange);
    }

    [TestMethod]
    public async Task FailedWriteAndCancellationAreTerminalWithoutRetry()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (_, _, _) => ValueTask.FromResult(false);
        var channel = new Switch2BluetoothAssociationCommandChannel(command,
            response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        Assert.IsFalse(await channel.ExchangeAsync(
            Convert.FromHexString("159101030001000000"),
            CancellationToken.None));
        Assert.AreEqual(1, command.WriteCalls);
        Assert.IsFalse(await channel.ExchangeAsync(
            Convert.FromHexString("159101030001000000"),
            CancellationToken.None));
        Assert.AreEqual(1, command.WriteCalls);
    }

    [TestMethod]
    public void CharacteristicRolesFailClosed()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            new Switch2BluetoothAssociationCommandChannel(
                FakeCharacteristic.Response(), FakeCharacteristic.Response()));
        Assert.ThrowsException<ArgumentException>(() =>
            new Switch2BluetoothAssociationCommandChannel(
                FakeCharacteristic.Command(), FakeCharacteristic.Command()));
    }

    private sealed class FakeCharacteristic :
        ISwitch2BluetoothWindowsGattCharacteristic
    {
        private readonly List<string> events;
        private Switch2BluetoothWindowsValueChangedHandler valueChanged;

        private FakeCharacteristic(Guid uuid, Switch2GattProperty properties,
            List<string> events)
        {
            Uuid = uuid;
            EvidencedProperties = properties;
            this.events = events;
        }

        internal static FakeCharacteristic Command(List<string> events = null) =>
            new(Switch2BluetoothAssociationCodec.
                    CommandWriteCharacteristicUuid,
                Switch2GattProperty.WriteWithoutResponse, events);

        internal static FakeCharacteristic Response(List<string> events = null) =>
            new(Switch2BluetoothAssociationCodec.
                    CommandResponseCharacteristicUuid,
                Switch2GattProperty.Notify, events);

        internal Func<ReadOnlyMemory<byte>, bool, CancellationToken,
            ValueTask<bool>> WriteOverride { get; set; }
        internal int WriteCalls { get; private set; }
        public Guid Uuid { get; }
        public Switch2GattProperty EvidencedProperties { get; }
        public bool HasOnlyReadAndNotifyProperties => false;

        public void AttachValueChangedHandler(
            Switch2BluetoothWindowsValueChangedHandler handler)
        {
            events?.Add("attach");
            valueChanged = handler;
        }

        public Task DetachValueChangedHandlerAndDrainAsync()
        {
            events?.Add("detach");
            valueChanged = null;
            return Task.CompletedTask;
        }

        public ValueTask<bool> ConfigureNotificationsAsync(bool enabled,
            CancellationToken cancellationToken)
        {
            events?.Add(enabled ? "notify-on" : "notify-off");
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> WriteValueAsync(ReadOnlyMemory<byte> value,
            bool writeWithoutResponse, CancellationToken cancellationToken)
        {
            WriteCalls++;
            return WriteOverride?.Invoke(value, writeWithoutResponse,
                cancellationToken) ?? ValueTask.FromResult(false);
        }

        internal void Emit(byte[] value) => valueChanged?.Invoke(value, 1);

        public void Dispose()
        {
        }
    }
}
