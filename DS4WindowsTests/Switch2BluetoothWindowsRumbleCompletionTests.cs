using DS4Windows.Switch2;

namespace DS4WindowsTests;

public sealed partial class Switch2BluetoothWindowsAdapterTests
{
    [TestMethod]
    public async Task RumbleCompletionTimeoutRetainsOneOperationAndConsumesExactLateReceipt()
    {
        await using var fixture = await RumbleCompletionFixture.OpenAsync();
        byte[] payload = fixture.Payload(3);
        Assert.IsTrue(RumbleUncertain(await fixture.WriteAsync(payload)));
        Assert.IsTrue(RumbleUncertain(await fixture.WriteAsync(payload)));
        Assert.AreEqual(1, fixture.Output.WriteCalls,
            "An exact retry must observe the retained native operation, not submit a duplicate.");
        Assert.IsFalse(fixture.WriteToken.CanBeCanceled,
            "The host deadline must not cancel the task that proves native completion.");
        fixture.NativeCompletion.SetResult(true);
        Assert.IsTrue(RumbleSucceeded(await fixture.WriteAsync(payload)));
        Assert.AreEqual(1, fixture.Output.WriteCalls,
            "Late success is a receipt for the original payload, not permission to resend it.");
        Assert.IsTrue(RumbleSucceeded(await fixture.WriteAsync(fixture.Payload(4))));
        Assert.AreEqual(2, fixture.Output.WriteCalls);
    }

    [TestMethod]
    public async Task RumbleCompletionPendingApplyCannotBeOvertakenByStop()
    {
        await using var fixture = await RumbleCompletionFixture.OpenAsync();
        Assert.IsTrue(RumbleUncertain(await fixture.WriteAsync(fixture.Payload(3))));
        byte[] stop = fixture.Payload(4, neutral: true);
        var blocked = await fixture.WriteAsync(stop);
        Assert.AreEqual(Switch2BluetoothHdRumbleTransportWriteFailure.Busy, blocked.Failure);
        Assert.AreEqual(1, fixture.Output.WriteCalls);
        fixture.NativeCompletion.SetResult(true);
        Assert.IsTrue(RumbleSucceeded(await fixture.WriteAsync(stop)));
        Assert.AreEqual(2, fixture.Output.WriteCalls);
        CollectionAssert.AreEqual(stop, fixture.Output.LastWrite);
    }

    [TestMethod]
    public async Task RumbleCompletionTeardownWaitsForActualLateCompletionAfterHostTimeout()
    {
        await using var fixture = await RumbleCompletionFixture.OpenAsync();
        Assert.IsTrue(RumbleUncertain(await fixture.WriteAsync(fixture.Payload(3))));
        bool bounded = await fixture.Lease.BeginAndWaitForBoundedTeardownAsync(CancellationToken.None);
        Assert.IsFalse(bounded, "A cancelled host wait is not a native-write drain proof.");
        Assert.IsFalse(fixture.Device.Disposed);
        Assert.IsFalse(fixture.Output.Disposed);
        Assert.IsFalse(fixture.Lease.ResourceRelease.IsCompleted);
        fixture.NativeCompletion.SetResult(true);
        await fixture.Lease.ResourceRelease.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(fixture.Device.Disposed);
        Assert.IsTrue(fixture.Output.Disposed);
        Assert.AreEqual(1, fixture.Output.DisposeCalls);
    }

    [DataTestMethod]
    [DataRow("success")]
    [DataRow("rejected")]
    [DataRow("faulted")]
    public async Task RumbleCompletionLateTerminalResultIsConsumedWithoutResubmission(string outcome)
    {
        await using var fixture = await RumbleCompletionFixture.OpenAsync();
        byte[] payload = fixture.Payload(3);
        Assert.IsTrue(RumbleUncertain(await fixture.WriteAsync(payload)));
        if (outcome == "faulted")
            fixture.NativeCompletion.SetException(new IOException("Synthetic terminal GATT failure."));
        else
            fixture.NativeCompletion.SetResult(outcome == "success");
        var receipt = await fixture.WriteAsync(payload);
        Assert.AreEqual(1, fixture.Output.WriteCalls,
            "All actual terminal results belong to the retained operation, including failure.");
        Assert.AreEqual(outcome == "success", RumbleSucceeded(receipt));
        if (outcome == "rejected")
            Assert.AreEqual(Switch2BluetoothHdRumbleTransportWriteFailure.TransportRejected, receipt.Failure);
        if (outcome == "faulted")
            Assert.AreEqual(Switch2BluetoothHdRumbleTransportWriteFailure.DependencyThrew, receipt.Failure);
    }

    [TestMethod]
    public async Task RumbleCompletionDisconnectedLifetimeCannotRetryOrReleasePendingWrite()
    {
        await using var fixture = await RumbleCompletionFixture.OpenAsync();
        byte[] payload = fixture.Payload(3);
        Assert.IsTrue(RumbleUncertain(await fixture.WriteAsync(payload)));
        fixture.Device.EmitDisconnected();
        Assert.AreEqual(Switch2BluetoothHdRumbleTransportWriteFailure.StaleLifetime,
            (await fixture.WriteAsync(payload)).Failure);
        Task<bool> release = fixture.Lease.BeginAndWaitForResourceReleaseAsync();
        Assert.IsFalse(release.IsCompleted);
        Assert.IsFalse(fixture.Output.Disposed);
        fixture.NativeCompletion.SetResult(true);
        Assert.IsTrue(await release.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, fixture.Output.WriteCalls);
        Assert.AreEqual(0, fixture.Device.Service.Characteristic.DisableCalls,
            "Definite disconnect must not manufacture a remote CCCD write.");
    }

    [TestMethod]
    public async Task RumbleCompletionOwnsDetachedPayloadWhileInputCallbacksContinue()
    {
        await using var fixture = await RumbleCompletionFixture.OpenAsync();
        byte[] payload = fixture.Payload(3);
        byte[] expected = (byte[])payload.Clone();
        Task<Switch2BluetoothHdRumbleTransportWriteResult> write = fixture.WriteAsync(payload);
        await fixture.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Device.Service.Characteristic.Emit(new byte[64], 77);
        Assert.AreEqual(1, fixture.InputCallbacks);
        Assert.IsTrue(RumbleUncertain(await write));
        payload.AsSpan().Fill(0xff);
        CollectionAssert.AreEqual(expected, fixture.BorrowedPayload.ToArray());
        fixture.NativeCompletion.SetResult(true);
        Assert.IsTrue(RumbleSucceeded(await fixture.WriteAsync(expected)));
        Assert.AreEqual(1, fixture.Output.WriteCalls);
    }

    [DataTestMethod]
    [DataRow("success")]
    [DataRow("rejected")]
    [DataRow("task-fault")]
    [DataRow("synchronous-throw")]
    public async Task RumbleCompletionImmediateTerminalPathsReleaseAdmission(string outcome)
    {
        await using var fixture = await RumbleCompletionFixture.OpenAsync();
        fixture.Output.WriteOverride = (_, _, token) =>
        {
            Assert.IsFalse(token.CanBeCanceled);
            if (outcome == "synchronous-throw") throw new IOException("Synthetic entry failure.");
            if (outcome == "task-fault")
                return ValueTask.FromException<bool>(new IOException("Synthetic completed task failure."));
            return ValueTask.FromResult(outcome == "success");
        };
        for (byte counter = 3; counter < 5; counter++)
        {
            var result = await fixture.WriteAsync(fixture.Payload(counter));
            Assert.AreEqual(outcome == "success", RumbleSucceeded(result));
            Assert.AreNotEqual(Switch2BluetoothHdRumbleTransportWriteFailure.Busy, result.Failure);
            if (outcome is "task-fault" or "synchronous-throw")
                Assert.AreEqual(Switch2BluetoothHdRumbleTransportWriteFailure.DependencyThrew, result.Failure);
        }
        Assert.AreEqual(2, fixture.Output.WriteCalls);
        Assert.IsTrue(await fixture.Lease.BeginAndWaitForResourceReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, fixture.Output.DisposeCalls);
    }

    [TestMethod]
    public async Task RumbleCompletionAdmittedPlatformEntryDoesNotBlockInputOrEscapeTeardown()
    {
        await using var fixture = await RumbleCompletionFixture.OpenAsync();
        using var returnFromPlatform = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Output.WriteOverride = (_, _, _) =>
        {
            entered.TrySetResult(true);
            Assert.IsTrue(returnFromPlatform.Wait(TimeSpan.FromSeconds(3)), "Bounded synthetic platform-entry witness.");
            return ValueTask.FromResult(true);
        };
        Task<Switch2BluetoothHdRumbleTransportWriteResult> write = fixture.WriteAsync(fixture.Payload(3));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Run(() => fixture.Device.Service.Characteristic.Emit(new byte[64], 78))
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(1, fixture.InputCallbacks,
                "The lease state lock must not span the platform write entry or its wait.");
            Task<bool> release = fixture.Lease.BeginAndWaitForResourceReleaseAsync();
            Assert.IsFalse(release.IsCompleted);
            Assert.IsFalse(fixture.Output.Disposed);
            returnFromPlatform.Set();
            Assert.IsTrue(RumbleSucceeded(await write));
            Assert.IsTrue(await release.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(1, fixture.Output.WriteCalls);
            Assert.AreEqual(1, fixture.Output.DisposeCalls);
        }
        finally { returnFromPlatform.Set(); }
    }

    [TestMethod]
    public async Task RumbleCompletionPhysicalWriterRetainsCounterAndConsumesNativeReceiptOnce()
    {
        await using var fixture = await RumbleCompletionFixture.OpenAsync();
        var writer = new Switch2BluetoothHdRumblePhysicalWriter(fixture.Lease,
            Switch2ControllerModel.ProController2, 17, 23, initialCounter: 9);
        var stop = Switch2HdRumblePhysicalSubmission.CreateStop(17, 23, 31);
        Assert.IsTrue((await Task.Run(() => writer.TryWrite(stop)).WaitAsync(TimeSpan.FromSeconds(2))).IsUncertain);
        byte[] original = (byte[])fixture.Output.LastWrite.Clone();
        fixture.NativeCompletion.SetResult(true);
        Assert.IsTrue(writer.TryWrite(stop).Succeeded);
        Assert.AreEqual(1, fixture.Output.WriteCalls,
            "The physical writer must finish its original counter without another native send.");
        CollectionAssert.AreEqual(original, fixture.Output.LastWrite);
        Assert.IsTrue(writer.TryWrite(Switch2HdRumblePhysicalSubmission.CreateStop(17, 23, 32)).Succeeded);
        Assert.AreEqual(2, fixture.Output.WriteCalls);
        Assert.AreEqual((byte)0x5a, fixture.Output.LastWrite[1]);
    }

    private static bool RumbleUncertain(Switch2BluetoothHdRumbleTransportWriteResult result) =>
        result.Outcome == Switch2BluetoothHdRumbleTransportWriteOutcome.OutcomeUncertain;

    private static bool RumbleSucceeded(Switch2BluetoothHdRumbleTransportWriteResult result) =>
        result.Outcome == Switch2BluetoothHdRumbleTransportWriteOutcome.Completed;

    private sealed class RumbleCompletionFixture : IAsyncDisposable
    {
        internal readonly FakeDevice Device = FakeDevice.ValidDuplexPro();
        internal FakeCharacteristic Output => Device.Service.OutputCharacteristic;
        internal readonly TaskCompletionSource<bool> NativeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Switch2BluetoothWindowsInputLease Lease;
        internal CancellationToken WriteToken;
        internal ReadOnlyMemory<byte> BorrowedPayload;
        internal int InputCallbacks;
        private Switch2BluetoothWindowsAdapter adapter;

        internal static async Task<RumbleCompletionFixture> OpenAsync()
        {
            var fixture = new RumbleCompletionFixture();
            var watcher = new FakeWatcher();
            var platform = new FakePlatform(watcher);
            platform.EnqueueDevice(fixture.Device);
            var observation = StartAndObserve(platform, watcher, out fixture.adapter);
            var opened = await fixture.adapter.OpenRememberedDuplexAsync(observation);
            Assert.IsTrue(opened.Succeeded, opened.Failure.ToString());
            fixture.Lease = opened.Lease;
            Assert.IsTrue(fixture.Lease.TrySubscribeCccdNotify(23,
                (_, _, _, _, _) => Interlocked.Increment(ref fixture.InputCallbacks), _ => { }));
            Assert.IsTrue(fixture.Lease.TryBindHdRumbleLifetime(Switch2ControllerModel.ProController2, 17, 23));
            fixture.Output.WriteOverride = (payload, withoutResponse, token) =>
            {
                Assert.IsTrue(withoutResponse);
                fixture.WriteToken = token;
                fixture.BorrowedPayload = payload;
                fixture.WriteEntered.TrySetResult(true);
                // Match CsWinRT's cancellable proxy: cancellation can finish the
                // caller's task while the independently owned native task lives on.
                return new ValueTask<bool>(fixture.NativeCompletion.Task.WaitAsync(token));
            };
            return fixture;
        }

        internal byte[] Payload(byte counter, bool neutral = false)
        {
            var frame = neutral ? default : new Switch2HdRumbleSubframe(0x187, 40, 0x112, 80);
            var group = new Switch2HdRumbleGroup(frame, frame, frame);
            var payload = new byte[Switch2BluetoothHdRumbleCodec.ProControllerPayloadLength];
            Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryEncodeProController(counter, group, group, payload));
            return payload;
        }

        internal Task<Switch2BluetoothHdRumbleTransportWriteResult> WriteAsync(byte[] payload) =>
            Task.Run(() => Lease.TryWritePayload(payload, Switch2ControllerModel.ProController2, 17, 23))
                .WaitAsync(TimeSpan.FromSeconds(2));

        public async ValueTask DisposeAsync()
        {
            NativeCompletion.TrySetResult(true);
            if (Lease != null)
                Assert.IsTrue(await Lease.BeginAndWaitForResourceReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            if (adapter != null) Assert.IsTrue(await adapter.EndScanAsync(1));
        }
    }
}
