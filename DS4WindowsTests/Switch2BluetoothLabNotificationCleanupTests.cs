using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothLabNotificationCleanupTests
{
    [TestMethod]
    public async Task SuccessfulCleanupRunsInOrderAndIsNotFenced()
    {
        var calls = new List<string>();
        var result = await Switch2BluetoothLabNotificationCleanup.RunAsync(
            () => { calls.Add("headset"); return Task.FromResult("Success"); },
            () => { calls.Add("common"); return Task.FromResult("Success"); });
        CollectionAssert.AreEqual(new[] { "headset", "common" }, calls);
        Assert.AreEqual("Success", result.HeadsetStatus);
        Assert.AreEqual("Success", result.CommonInputStatus);
        Assert.IsFalse(result.Fenced);
        result.ThrowIfFailed();
    }

    [DataTestMethod]
    [DataRow("ProtocolError", "Success")]
    [DataRow("Success", "Unreachable")]
    [DataRow(null, "Success")]
    public async Task FailedStatusStillRestoresCommonInputAndFences(string headset, string common)
    {
        int restored = 0;
        var result = await Switch2BluetoothLabNotificationCleanup.RunAsync(
            () => Task.FromResult(headset),
            () => { restored++; return Task.FromResult(common); });
        Assert.AreEqual(1, restored);
        Assert.AreEqual(headset, result.HeadsetStatus);
        Assert.AreEqual(common, result.CommonInputStatus);
        Assert.IsTrue(result.Fenced);
        result.ThrowIfFailed(); // A rejected status is returned, not an exception.
    }

    [TestMethod]
    public async Task SynchronousHeadsetFailureDoesNotSkipCommonInput()
    {
        var failure = new InvalidOperationException("synthetic headset failure");
        int restored = 0;
        var result = await Switch2BluetoothLabNotificationCleanup.RunAsync(
            () => throw failure,
            () => { restored++; return Task.FromResult("Success"); });
        Assert.AreEqual(1, restored);
        Assert.IsTrue(result.Fenced);
        Assert.AreSame(failure, result.HeadsetFailure);
        Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(() => result.ThrowIfFailed()));
    }

    [TestMethod]
    public async Task CanceledHeadsetOperationStillRestoresCommonInput()
    {
        int restored = 0;
        var result = await Switch2BluetoothLabNotificationCleanup.RunAsync(
            () => Task.FromCanceled<string>(new CancellationToken(canceled: true)),
            () => { restored++; return Task.FromResult("Success"); });
        Assert.AreEqual(1, restored);
        Assert.IsTrue(result.Fenced);
        Assert.IsInstanceOfType(result.HeadsetFailure, typeof(OperationCanceledException));
        Assert.AreEqual("Success", result.CommonInputStatus);
    }

    [TestMethod]
    public async Task LateHeadsetFaultIsDrainedBeforeCommonRestoreAndCommonWriteIsRetained()
    {
        var disable = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restore = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoreEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("synthetic late failure");
        Task<Switch2BluetoothLabNotificationCleanupResult> operation = Switch2BluetoothLabNotificationCleanup.RunAsync(
            () => disable.Task,
            () => { restoreEntered.SetResult(); return restore.Task; });
        try
        {
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsFalse(restoreEntered.Task.IsCompleted, "The second write must not overlap the first.");
            disable.SetException(failure);
            await restoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsFalse(operation.IsCompleted, "Ownership remains until the common-input write actually completes.");
            restore.SetResult("Success");
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreSame(failure, result.HeadsetFailure);
            Assert.AreEqual("Success", result.CommonInputStatus);
            Assert.IsTrue(result.Fenced);
        }
        finally
        {
            disable.TrySetResult("Success");
            restore.TrySetResult("Success");
            await operation.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [TestMethod]
    public async Task CommonInputFailureIsPreservedAndFenced()
    {
        var failure = new InvalidOperationException("synthetic common-input failure");
        var result = await Switch2BluetoothLabNotificationCleanup.RunAsync(
            () => Task.FromResult("Success"), () => Task.FromException<string>(failure));
        Assert.IsTrue(result.Fenced);
        Assert.AreEqual("Success", result.HeadsetStatus);
        Assert.AreSame(failure, result.CommonInputFailure);
        Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(() => result.ThrowIfFailed()));
    }

    [TestMethod]
    public async Task BothFailuresArePreservedWithoutSkippingCompensation()
    {
        var headset = new InvalidOperationException("headset");
        var common = new InvalidOperationException("common");
        var result = await Switch2BluetoothLabNotificationCleanup.RunAsync(
            () => Task.FromException<string>(headset), () => throw common);
        Assert.IsTrue(result.Fenced);
        var aggregate = Assert.ThrowsException<AggregateException>(() => result.ThrowIfFailed());
        CollectionAssert.AreEqual(new Exception[] { headset, common }, aggregate.InnerExceptions.ToArray());
    }

    [TestMethod]
    public async Task BothCallbacksAreValidatedBeforeAnyWrite()
    {
        int writes = 0;
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() => Switch2BluetoothLabNotificationCleanup.RunAsync(
            () => { writes++; return Task.FromResult("Success"); }, null));
        Assert.AreEqual(0, writes);
    }
}
