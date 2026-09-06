using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothPlayerLedCommandChannelTests
{
    [TestMethod]
    public async Task SubscriptionPrecedesExactAcknowledgedLedExchange()
    {
        var events = new List<string>();
        var command = FakeCharacteristic.Command(events);
        var response = FakeCharacteristic.Response(events);
        command.WriteOverride = (request, writeWithoutResponse, _) =>
        {
            events.Add("write");
            Assert.IsFalse(writeWithoutResponse,
                "ATT-acknowledged Write must win when both are evidenced.");
            CollectionAssert.AreEqual(Convert.FromHexString(
                "09910107000400000D000000"), request.ToArray());
            response.Emit(Convert.FromHexString("0901000000000000"));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command,
            response);

        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        Switch2BluetoothPlayerLedChannelResult result = await channel.
            SetPlayerAsync(7, CancellationToken.None);
        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(new[] { "attach", "notify-on", "write" },
            events);
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
        CollectionAssert.AreEqual(new[]
        {
            "attach", "notify-on", "write", "detach", "notify-off",
        }, events);
    }

    [TestMethod]
    public async Task ExactVirtualMaskIsWrittenWithoutPlayerApproximation()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (request, _, _) =>
        {
            Assert.AreEqual((byte)0x02, request.Span[8]);
            response.Emit(Convert.FromHexString("0901000000000000"));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command,
            response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));

        Switch2BluetoothPlayerLedChannelResult result = await channel.
            SetPatternAsync(0x02, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task InvalidResponseTerminallyFencesSuccessor()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (_, _, _) =>
        {
            response.Emit(Convert.FromHexString("0900000000000000"));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command,
            response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));

        Switch2BluetoothPlayerLedChannelResult rejected = await channel.
            SetPlayerAsync(1, CancellationToken.None);
        Switch2BluetoothPlayerLedChannelResult successor = await channel.
            SetPlayerAsync(2, CancellationToken.None);
        Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.
            ResponseRejected, rejected.Failure);
        Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.Retired,
            successor.Failure);
        Assert.AreEqual(1, command.WriteCalls);
    }

    [TestMethod]
    public async Task MemoryReadUsesSameSubscriptionAndReturnsExactPayload()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (request, _, _) =>
        {
            CollectionAssert.AreEqual(Convert.FromHexString(
                "0291010400080000097E0000A8300100"), request.ToArray());
            response.Emit(Convert.FromHexString(
                "020100000000000009000000A8300100A10B2C3D4E5F607182"));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command,
            response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));

        Switch2BluetoothMemoryReadChannelResult result = await channel.
            ReadMemoryAsync(9, 0x0130A8, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(Convert.FromHexString("A10B2C3D4E5F607182"),
            result.Value.ToArray());
    }

    [TestMethod]
    public async Task UnrelatedResponseIsIgnoredUntilMatchingCommandArrives()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (_, _, _) =>
        {
            response.Emit(Convert.FromHexString("0901000000000000"));
            response.Emit(Convert.FromHexString(
                "020100000000000001000000003001005A"));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command,
            response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));

        Switch2BluetoothMemoryReadChannelResult result = await channel.
            ReadMemoryAsync(1, 0x013000, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(new byte[] { 0x5A }, result.Value.ToArray());
    }

    [TestMethod]
    public async Task RetirementWaitsForAdmittedWriteBeforeDetach()
    {
        var events = new List<string>();
        var command = FakeCharacteristic.Command(events);
        var response = FakeCharacteristic.Response(events);
        var writeEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        command.WriteOverride = async (_, _, _) =>
        {
            events.Add("write");
            writeEntered.TrySetResult();
            return await releaseWrite.Task;
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command,
            response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        Task<Switch2BluetoothPlayerLedChannelResult> exchange = channel.
            SetPlayerAsync(1, CancellationToken.None).AsTask();
        await writeEntered.Task;

        Task<bool> retirement = channel.RetireAsync(
            CancellationToken.None).AsTask();
        Assert.IsFalse(retirement.IsCompleted);
        CollectionAssert.DoesNotContain(events, "detach");
        releaseWrite.TrySetResult(true);
        Assert.IsFalse((await exchange).Succeeded);
        Assert.IsTrue(await retirement);
        Assert.IsTrue(events.IndexOf("detach") > events.IndexOf("write"));
    }

    [TestMethod]
    public async Task SensorsSelectThenEnableAndLeaveLedOwnerUsable()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        var writes = new List<string>();
        command.WriteOverride = (request, _, _) =>
        {
            writes.Add(Convert.ToHexString(request.Span));
            response.Emit(new byte[] { request.Span[0], 1, 0, 0, 0, 0, 0, 0 });
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.NotPrepared,
            await channel.InitializeJoyConSensorsAsync(CancellationToken.None));
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.None,
            await channel.InitializeJoyConSensorsAsync(CancellationToken.None));
        Assert.IsTrue((await channel.SetPlayerAsync(1, CancellationToken.None)).Succeeded);
        CollectionAssert.AreEqual(new[]
        {
            "0C9101020004000094000000", "0C9101040004000094000000",
            "099101070004000001000000",
        }, writes);
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    public async Task SensorFailureStopsSequenceAndFencesSuccessor(int failingStep, bool rejectWrite)
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (_, _, _) =>
        {
            bool fail = command.WriteCalls == failingStep;
            if (fail && rejectWrite) return ValueTask.FromResult(false);
            response.Emit(new byte[] { 0x0C, fail ? (byte)4 : (byte)1, 0, 0, 0, 0, 0, 0 });
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        Assert.AreEqual(rejectWrite ? Switch2BluetoothSensorInitializationFailure.WriteRejected :
            Switch2BluetoothSensorInitializationFailure.ResponseRejected,
            await channel.InitializeJoyConSensorsAsync(CancellationToken.None));
        Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.Retired,
            (await channel.SetPlayerAsync(1, CancellationToken.None)).Failure);
        Assert.AreEqual(failingStep, command.WriteCalls);
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task SensorExchangeIgnoresUnrelatedAckAndExcludesOtherOperations()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (_, _, _) => ValueTask.FromResult(true);
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        Task<Switch2BluetoothSensorInitializationFailure> pending = channel.
            InitializeJoyConSensorsAsync(cancellation.Token).AsTask();
        response.Emit(Convert.FromHexString("0901000000000000"));
        Assert.IsFalse(pending.IsCompleted);
        Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.Busy,
            (await channel.SetPlayerAsync(1, CancellationToken.None)).Failure);
        Assert.AreEqual(Switch2BluetoothMemoryReadChannelFailure.Busy,
            (await channel.ReadMemoryAsync(1, 0x013000, CancellationToken.None)).Failure);
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.Busy,
            await channel.InitializeJoyConSensorsAsync(CancellationToken.None));
        cancellation.Cancel();
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.Cancelled, await pending);
        Assert.AreEqual(1, command.WriteCalls);
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.Retired,
            await channel.InitializeJoyConSensorsAsync(CancellationToken.None));
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task SensorRetirementWaitsForWriteAndNeverSendsEnableAfterRetirement()
    {
        var events = new List<string>();
        var command = FakeCharacteristic.Command(events);
        var response = FakeCharacteristic.Response(events);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        command.WriteOverride = (_, _, _) => new ValueTask<bool>(release.Task);
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        Task<Switch2BluetoothSensorInitializationFailure> pending = channel.
            InitializeJoyConSensorsAsync(CancellationToken.None).AsTask();
        Task<bool> retirement = channel.RetireAsync(CancellationToken.None).AsTask();
        Assert.IsFalse(retirement.IsCompleted);
        CollectionAssert.DoesNotContain(events, "detach");
        release.SetResult(true);
        Assert.AreNotEqual(Switch2BluetoothSensorInitializationFailure.None, await pending);
        Assert.IsTrue(await retirement);
        Assert.AreEqual(1, command.WriteCalls);
    }

    [TestMethod]
    public void SensorResponseRequiresLengthCommandAndSuccessWithoutInventedEcho()
    {
        Assert.IsFalse(Switch2BluetoothSensorCodec.IsAccepted(new byte[7]));
        Assert.IsFalse(Switch2BluetoothSensorCodec.IsAccepted(Convert.FromHexString("0901000000000000")));
        Assert.IsFalse(Switch2BluetoothSensorCodec.IsAccepted(Convert.FromHexString("0C04000000000000")));
        Assert.IsTrue(Switch2BluetoothSensorCodec.IsAccepted(Convert.FromHexString("0C01FFFFFFFFFFFF")));
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
            new(Switch2BluetoothPlayerLedCodec.
                    CommandWriteCharacteristicUuid,
                Switch2GattProperty.Write |
                    Switch2GattProperty.WriteWithoutResponse, events);

        internal static FakeCharacteristic Response(List<string> events = null) =>
            new(Switch2BluetoothPlayerLedCodec.
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
