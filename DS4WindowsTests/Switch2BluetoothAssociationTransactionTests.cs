using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothAssociationTransactionTests
{
    private static readonly byte[] HostAddress =
        Convert.FromHexString("FFEEDDCCBBAA");

    [TestMethod]
    public async Task ExecutesFourStepsSeriallyAndExactlyOnce()
    {
        var channel = new FakeChannel();
        var transaction = Create(channel);

        Switch2BluetoothAssociationResult result = await transaction.
            ExecuteAsync(HostAddress);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(Switch2BluetoothAssociationStep.Commit,
            result.LastCompletedStep);
        CollectionAssert.AreEqual(new byte[] { 1, 4, 2, 3 },
            channel.Requests.Select(request => request[3]).ToArray());
        CollectionAssert.AreEqual(new[] { 22, 25, 25, 9 },
            channel.Requests.Select(request => request.Length).ToArray());
    }

    [TestMethod]
    public async Task RejectionStopsWithoutRetryOrFollowingStep()
    {
        var channel = new FakeChannel { RejectCall = 3 };
        var transaction = Create(channel);

        Switch2BluetoothAssociationResult result = await transaction.
            ExecuteAsync(HostAddress);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(Switch2BluetoothAssociationFailure.ChannelRejected,
            result.Failure);
        Assert.AreEqual(
            Switch2BluetoothAssociationStep.WriteLongTermKeyPart1,
            result.LastCompletedStep);
        Assert.AreEqual(3, channel.Requests.Count);
    }

    [TestMethod]
    public async Task CancellationAndFaultAreAttributedAndNeverRetried()
    {
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        var channel = new FakeChannel();
        Switch2BluetoothAssociationResult cancelled = await Create(channel).
            ExecuteAsync(HostAddress, preCancelled.Token);
        Assert.AreEqual(Switch2BluetoothAssociationFailure.Cancelled,
            cancelled.Failure);
        Assert.AreEqual(0, channel.Requests.Count);

        channel = new FakeChannel { ThrowCall = 2 };
        Switch2BluetoothAssociationResult faulted = await Create(channel).
            ExecuteAsync(HostAddress);
        Assert.AreEqual(Switch2BluetoothAssociationFailure.ChannelFaulted,
            faulted.Failure);
        Assert.AreEqual(Switch2BluetoothAssociationStep.SetHostAddress,
            faulted.LastCompletedStep);
        Assert.AreEqual(2, channel.Requests.Count);
    }

    [TestMethod]
    public async Task SingleFlightRejectsConcurrentCeremony()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new FakeChannel
        {
            Override = async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                return await release.Task.WaitAsync(cancellationToken);
            },
        };
        var transaction = Create(channel);

        Task<Switch2BluetoothAssociationResult> first = transaction.
            ExecuteAsync(HostAddress).AsTask();
        await entered.Task;
        Switch2BluetoothAssociationResult concurrent = await transaction.
            ExecuteAsync(HostAddress);
        Assert.AreEqual(Switch2BluetoothAssociationFailure.Busy,
            concurrent.Failure);

        release.SetResult(true);
        Assert.IsTrue((await first).Succeeded);
    }

    [TestMethod]
    public async Task InvalidHostNeverTouchesChannel()
    {
        var channel = new FakeChannel();
        Switch2BluetoothAssociationResult result = await Create(channel).
            ExecuteAsync(new byte[6]);
        Assert.AreEqual(Switch2BluetoothAssociationFailure.InvalidArgument,
            result.Failure);
        Assert.AreEqual(0, channel.Requests.Count);
    }

    private static Switch2BluetoothAssociationTransaction Create(
        FakeChannel channel) => new(channel, TimeSpan.FromSeconds(2));

    private sealed class FakeChannel :
        ISwitch2BluetoothAssociationCommandChannel
    {
        internal List<byte[]> Requests { get; } = new();
        internal int RejectCall { get; init; }
        internal int ThrowCall { get; init; }
        internal Func<ReadOnlyMemory<byte>, CancellationToken,
            ValueTask<bool>> Override { get; init; }

        public async ValueTask<bool> ExchangeAsync(
            ReadOnlyMemory<byte> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.ToArray());
            int call = Requests.Count;
            if (call == ThrowCall)
            {
                throw new InvalidOperationException("Synthetic fault.");
            }
            if (Override != null)
            {
                return await Override(request, cancellationToken);
            }
            return call != RejectCall;
        }
    }
}
