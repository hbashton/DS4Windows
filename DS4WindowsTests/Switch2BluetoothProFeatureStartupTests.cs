using DS4Windows.Switch2;

namespace DS4WindowsTests;

public sealed partial class Switch2BluetoothPlayerLedCommandChannelTests
{
    [TestMethod]
    public void ProFeatureCodecUsesBluetoothMaskAndStrictStepSpecificCommandOnlyReplies()
    {
        foreach (bool enable in new[] { false, true })
        {
            CollectionAssert.AreEqual(Convert.FromHexString(enable ?
                    "0C910104000400002F000000" : "0C910102000400002F000000"),
                Switch2BluetoothProFeatureCodec.CreateRequest(enable));
            byte[] reply = ProFeatureReply(enable);
            Assert.IsTrue(Switch2BluetoothProFeatureCodec.IsAccepted(reply, enable));
            Assert.IsFalse(Switch2BluetoothProFeatureCodec.IsAccepted(reply, !enable));
            for (int index = 0; index < reply.Length; index++)
            {
                byte[] malformed = (byte[])reply.Clone();
                malformed[index] ^= 0x80;
                Assert.IsFalse(Switch2BluetoothProFeatureCodec.IsAccepted(malformed, enable),
                    $"Reply byte {index} must be validated.");
            }
            for (int length = 0; length < reply.Length; length++)
                Assert.IsFalse(Switch2BluetoothProFeatureCodec.IsAccepted(reply.AsSpan(0, length), enable));
            foreach (int length in new[] { 13, 26, 64 })
            {
                byte[] oversized = new byte[length];
                reply.CopyTo(oversized, 0);
                Assert.IsFalse(Switch2BluetoothProFeatureCodec.IsAccepted(oversized, enable));
            }
            // Public captures on the combined response endpoint include a
            // 14-byte prefix. This owner binds the command-only endpoint.
            byte[] combinedEndpointReply = new byte[26];
            reply.CopyTo(combinedEndpointReply, 14);
            Assert.IsFalse(Switch2BluetoothProFeatureCodec.IsAccepted(combinedEndpointReply, enable));
        }
        Assert.IsTrue(Switch2BluetoothSensorCodec.IsAccepted(Convert.FromHexString("0C01FFFFFFFFFFFF")),
            "The distinct legacy Joy-Con reply contract must remain unchanged.");
    }

    [TestMethod]
    public async Task ProFeaturesSelectThenEnableAndLeaveTheSharedLedOwnerUsable()
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        var writes = new List<string>();
        command.WriteOverride = (request, _, token) =>
        {
            writes.Add(Convert.ToHexString(request.Span));
            if (request.Span[0] == 0x0C)
            {
                Assert.IsFalse(token.CanBeCanceled);
                response.Emit(Convert.FromHexString("0901000000000000"));
                response.Emit(ProFeatureReply(request.Span[3] == 0x04));
            }
            else response.Emit(Convert.FromHexString("0901000000000000"));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.NotPrepared,
            await channel.InitializeProFeaturesAsync(CancellationToken.None));
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.None,
            await channel.InitializeProFeaturesAsync(CancellationToken.None));
        Assert.IsTrue((await channel.SetPlayerAsync(1, CancellationToken.None)).Succeeded);
        CollectionAssert.AreEqual(new[] { "0C910102000400002F000000", "0C910104000400002F000000",
            "099101070004000001000000" }, writes);
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow("0C0401021078000000000000")]
    [DataRow("0C9101021078000000000000")]
    [DataRow("0C0100021078000000000000")]
    [DataRow("0C0101041078000000000000")]
    [DataRow("0C0101020078000000000000")]
    [DataRow("0C0101021078000001000000")]
    [DataRow("0C01010210780000")]
    [DataRow("0C010102107800000000000000")]
    public async Task ProMalformedOrFutureStepReplyRejectsAndFencesTheCommandLane(string malformed)
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (_, _, _) =>
        {
            response.Emit(Convert.FromHexString(malformed));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.ResponseRejected,
            await channel.InitializeProFeaturesAsync(CancellationToken.None));
        Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.Retired,
            (await channel.SetPlayerAsync(1, CancellationToken.None)).Failure);
        Assert.AreEqual(1, command.WriteCalls);
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ProWriteRejectionStopsAtTheFailedStep(int failedStep)
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        command.WriteOverride = (request, _, _) =>
        {
            if (command.WriteCalls == failedStep) return ValueTask.FromResult(false);
            response.Emit(ProFeatureReply(request.Span[3] == 0x04));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.WriteRejected,
            await channel.InitializeProFeaturesAsync(CancellationToken.None));
        Assert.AreEqual(failedStep, command.WriteCalls);
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.Retired,
            await channel.InitializeProFeaturesAsync(CancellationToken.None));
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProDuplicateMaskAcknowledgementCannotCompleteEnableOrExtendItsDeadline(bool finishEnable)
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        var enableEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        command.WriteOverride = (request, _, _) =>
        {
            response.Emit(ProFeatureReply(enable: false));
            if (request.Span[3] == 0x04) enableEntered.TrySetResult();
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        using var deadline = new CancellationTokenSource();
        Task<Switch2BluetoothSensorInitializationFailure> initialization =
            channel.InitializeProFeaturesAsync(deadline.Token).AsTask();
        await enableEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(initialization.IsCompleted);
        response.Emit(ProFeatureReply(enable: false));
        Assert.IsFalse(initialization.IsCompleted);
        if (finishEnable) response.Emit(ProFeatureReply(enable: true));
        else deadline.Cancel();
        Assert.AreEqual(finishEnable ? Switch2BluetoothSensorInitializationFailure.None :
                Switch2BluetoothSensorInitializationFailure.Cancelled,
            await initialization.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(2, command.WriteCalls);
        if (!finishEnable)
            Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.Retired,
                (await channel.SetPlayerAsync(1, CancellationToken.None)).Failure);
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ProFeatureOwnershipExcludesLedsCalibrationAndBothStartupKindsForEachStep(int heldStep)
    {
        var command = FakeCharacteristic.Command();
        var response = FakeCharacteristic.Response();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        command.WriteOverride = (request, _, _) =>
        {
            if (command.WriteCalls == heldStep) entered.TrySetResult();
            else response.Emit(ProFeatureReply(request.Span[3] == 0x04));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        using var deadline = new CancellationTokenSource();
        Task<Switch2BluetoothSensorInitializationFailure> initialization =
            channel.InitializeProFeaturesAsync(deadline.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        response.Emit(Convert.FromHexString("0901000000000000"));
        Assert.IsFalse(initialization.IsCompleted);
        Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.Busy,
            (await channel.SetPlayerAsync(1, CancellationToken.None)).Failure);
        Assert.AreEqual(Switch2BluetoothMemoryReadChannelFailure.Busy,
            (await channel.ReadMemoryAsync(1, 0x013000, CancellationToken.None)).Failure);
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.Busy,
            await channel.InitializeProFeaturesAsync(CancellationToken.None));
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.Busy,
            await channel.InitializeJoyConSensorsAsync(CancellationToken.None));
        Assert.AreEqual(heldStep, command.WriteCalls);
        deadline.Cancel();
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.Cancelled,
            await initialization.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(await channel.RetireAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    public async Task ProCancellationReturnsBeforeNonCooperativeWriteWhileRetirementRetainsOwnership(
        int heldStep, bool eventualWriteSucceeded)
    {
        var events = new List<string>();
        var command = FakeCharacteristic.Command(events);
        var response = FakeCharacteristic.Response(events);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        command.WriteOverride = (request, _, token) =>
        {
            Assert.IsFalse(token.CanBeCanceled, "Only the waiter may be cancelled, not the admitted Windows operation.");
            if (command.WriteCalls == heldStep)
            {
                // Even an inline valid ACK cannot release the actual write's
                // resource ownership or admit the next startup step early.
                response.Emit(ProFeatureReply(request.Span[3] == 0x04));
                entered.TrySetResult();
                return new ValueTask<bool>(release.Task);
            }
            response.Emit(ProFeatureReply(request.Span[3] == 0x04));
            return ValueTask.FromResult(true);
        };
        var channel = new Switch2BluetoothPlayerLedCommandChannel(command, response);
        Assert.IsTrue(await channel.PrepareAsync(CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        Task<Switch2BluetoothSensorInitializationFailure> initialization =
            channel.InitializeProFeaturesAsync(cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.Cancelled,
                await initialization.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(release.Task.IsCompleted);
            Assert.AreEqual(Switch2BluetoothPlayerLedChannelFailure.Retired,
                (await channel.SetPlayerAsync(1, CancellationToken.None)).Failure);
            Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.Retired,
                await channel.InitializeProFeaturesAsync(CancellationToken.None));
            Task<bool> retirement = channel.RetireAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(retirement.IsCompleted);
            CollectionAssert.DoesNotContain(events, "detach");
            release.TrySetResult(eventualWriteSucceeded);
            Assert.IsTrue(await retirement.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(heldStep, command.WriteCalls, "No successor may write after cancellation.");
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult(true);
            await initialization.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static byte[] ProFeatureReply(bool enable) => Convert.FromHexString(enable ?
        "0C0101041078000000000000" : "0C0101021078000000000000");
}

public sealed partial class Switch2BluetoothWindowsAdapterTests
{
    [TestMethod]
    public async Task ProDuplexInitializesItsFeaturesBeforeCommonInputNotification()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var device = FakeDevice.ValidDuplexPro();
        var writes = new List<string>();
        device.Service.CommandCharacteristic.WriteOverride = (request, _, _) =>
        {
            Assert.AreEqual(0, device.Service.Characteristic.EnableCalls);
            writes.Add(Convert.ToHexString(request.Span));
            Assert.IsTrue(AcknowledgeProFeatureRequest(device.Service, request));
            return ValueTask.FromResult(true);
        };
        platform.EnqueueDevice(device);
        var observation = StartAndObserve(platform, watcher, out var adapter);
        var opened = await adapter.OpenRememberedDuplexAsync(observation);
        Assert.IsTrue(opened.Succeeded, opened.Failure.ToString());
        Assert.IsTrue(opened.Lease.ProFeaturesInitialized);
        Assert.IsFalse(opened.Lease.JoyConSensorsInitialized);
        Assert.AreEqual(1, device.Service.Characteristic.EnableCalls);
        CollectionAssert.AreEqual(new[] { "0C910102000400002F000000", "0C910104000400002F000000" }, writes);
        await RetireLease(opened.Lease, 99);
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ProStartupBadReplyNeverPublishesInput(int failedStep)
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var device = FakeDevice.ValidDuplexPro();
        device.Service.CommandCharacteristic.WriteOverride = (request, _, _) =>
        {
            Assert.AreEqual(0, device.Service.Characteristic.EnableCalls);
            if (device.Service.CommandCharacteristic.WriteCalls == failedStep)
                device.Service.ResponseCharacteristic.Emit(Convert.FromHexString("0C0101021078000001000000"), 1);
            else Assert.IsTrue(AcknowledgeProFeatureRequest(device.Service, request));
            return ValueTask.FromResult(true);
        };
        platform.EnqueueDevice(device);
        var observation = StartAndObserve(platform, watcher, out var adapter);
        var opened = await adapter.OpenRememberedDuplexAsync(observation);
        Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.SensorInitializationFailed, opened.Failure);
        Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.ResponseRejected, opened.SensorFailure);
        Assert.IsNull(opened.Lease);
        Assert.AreEqual(0, device.Service.Characteristic.EnableCalls);
        Assert.AreEqual(failedStep, device.Service.CommandCharacteristic.WriteCalls);
        Assert.IsTrue(device.Disposed);
        Assert.IsTrue(await adapter.EndScanAsync(1));
    }

    [TestMethod]
    public async Task ProOpenCancellationReturnsBoundedlyButRetainsResourcesUntilActualFeatureWriteCompletes()
    {
        var watcher = new FakeWatcher();
        var platform = new FakePlatform(watcher);
        var device = FakeDevice.ValidDuplexPro();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Service.CommandCharacteristic.WriteOverride = (_, _, token) =>
        {
            Assert.IsFalse(token.CanBeCanceled);
            entered.TrySetResult();
            return new ValueTask<bool>(release.Task);
        };
        platform.EnqueueDevice(device);
        var observation = StartAndObserve(platform, watcher, out var adapter, timeoutMilliseconds: 100);
        using var cancellation = new CancellationTokenSource();
        var operation = adapter.OpenRememberedDuplexAsync(observation, cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            var opened = await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(Switch2BluetoothWindowsOpenFailure.SensorInitializationFailed, opened.Failure);
            Assert.AreEqual(Switch2BluetoothSensorInitializationFailure.Cancelled, opened.SensorFailure);
            Assert.IsNull(opened.Lease);
            Assert.AreEqual(0, device.Service.Characteristic.EnableCalls);
            Assert.IsFalse(device.Disposed);
            Assert.IsFalse(device.Service.Disposed);
            Assert.IsFalse(device.Service.CommandCharacteristic.Disposed);
            Assert.IsFalse(device.Service.ResponseCharacteristic.Disposed);
            Assert.AreEqual(1, device.Service.CommandCharacteristic.WriteCalls);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult(true);
            await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(await adapter.EndScanAsync(1));
        }
        Assert.IsTrue(SpinWait.SpinUntil(() => device.Disposed, TimeSpan.FromSeconds(2)));
        Assert.IsTrue(device.Service.CommandCharacteristic.Disposed);
        Assert.IsTrue(device.Service.ResponseCharacteristic.Disposed);
        Assert.AreEqual(1, device.Service.CommandCharacteristic.WriteCalls);
    }
}
