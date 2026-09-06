using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class UdpDatagramSendPoolTests
{
    private static IPEndPoint Destination() => new(IPAddress.Loopback, 26760);

    [TestMethod]
    public void SaturationDoesNotWaitOrOverwriteAnyPendingDatagram()
    {
        var pending = new List<SocketAsyncEventArgs>();
        using var pool = new UdpDatagramSendPool(2, 8, args => { pending.Add(args); return true; });
        Assert.IsTrue(pool.TrySend(new byte[] { 1, 2, 3 }, Destination()));
        Assert.IsTrue(pool.TrySend(new byte[] { 4, 5 }, Destination()));
        byte[] first = Bytes(pending[0]), second = Bytes(pending[1]);
        Task<bool> overloaded = Task.Run(() => pool.TrySend(new byte[] { 99 }, Destination()));
        Assert.IsTrue(overloaded.Wait(TimeSpan.FromSeconds(2)), "Capacity exhaustion must not wait for completion.");
        Assert.IsFalse(overloaded.Result);
        Assert.AreEqual(1L, pool.CapacityDropCount);
        Assert.AreEqual(0L, pool.FailureCount);
        Assert.AreEqual(2, pool.InFlightCount);
        CollectionAssert.AreEqual(first, Bytes(pending[0]));
        CollectionAssert.AreEqual(second, Bytes(pending[1]));
        Assert.IsTrue(pool.Return(pending[1]));
        Assert.IsTrue(pool.Return(pending[0]));
        Assert.AreEqual(0, pool.InFlightCount);
    }

    [TestMethod]
    public void ArbitraryCompletionOrderReusesOnlyTheActuallyFreeEntry()
    {
        var pending = new List<SocketAsyncEventArgs>();
        using var pool = new UdpDatagramSendPool(3, 8, args => { pending.Add(args); return true; });
        for (byte value = 1; value <= 3; value++)
            Assert.IsTrue(pool.TrySend(new[] { value }, Destination()));
        SocketAsyncEventArgs middle = pending[1];
        Assert.IsTrue(pool.Return(middle));
        Assert.IsTrue(pool.TrySend(new byte[] { 4, 5 }, Destination()));
        Assert.AreSame(middle, pending[3]);
        CollectionAssert.AreEqual(new byte[] { 1 }, Bytes(pending[0]));
        CollectionAssert.AreEqual(new byte[] { 3 }, Bytes(pending[2]));
        Assert.IsTrue(pool.Return(pending[2]));
        Assert.IsTrue(pool.Return(pending[0]));
        Assert.IsTrue(pool.Return(pending[3]));
        Assert.IsFalse(pool.Return(pending[3]), "A duplicate completion before reuse must not release twice.");
        Assert.AreEqual(0, pool.InFlightCount);
    }

    [TestMethod]
    public void SourceBytesAndEndpointObjectAreOwnedUntilCompletion()
    {
        SocketAsyncEventArgs pending = null;
        using var pool = new UdpDatagramSendPool(1, 8, args => { pending = args; return true; });
        byte[] source = { 11, 22, 33 };
        IPEndPoint recipient = Destination();
        Assert.IsTrue(pool.TrySend(source, recipient));
        Array.Fill(source, (byte)99);
        recipient.Port = 100;
        recipient.Address = IPAddress.IPv6Loopback;
        CollectionAssert.AreEqual(new byte[] { 11, 22, 33 }, Bytes(pending));
        Assert.AreNotSame(recipient, pending.RemoteEndPoint);
        Assert.AreEqual(Destination(), pending.RemoteEndPoint);
        Assert.IsTrue(pool.Return(pending));
    }

    [TestMethod]
    public void ShortAndEmptyDatagramsNeverSendThePreviousBuffersTail()
    {
        var delivered = new List<byte[]>();
        var counts = new List<int>();
        using var pool = new UdpDatagramSendPool(1, 8, args =>
        {
            counts.Add(args.Count);
            delivered.Add(Bytes(args));
            return false;
        });
        Assert.IsTrue(pool.TrySend(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Destination()));
        Assert.IsTrue(pool.TrySend(new byte[] { 9, 10 }, Destination()));
        Assert.IsTrue(pool.TrySend(ReadOnlySpan<byte>.Empty, Destination()));
        CollectionAssert.AreEqual(new[] { 8, 2, 0 }, counts);
        CollectionAssert.AreEqual(new byte[] { 9, 10 }, delivered[1]);
        Assert.AreEqual(0, delivered[2].Length);
        Assert.AreEqual(0, pool.InFlightCount);
    }

    [TestMethod]
    public void SynchronousAndAsynchronousErrorsReleaseCapacityAndCountOnce()
    {
        int behavior = 0;
        SocketAsyncEventArgs pending = null;
        using var pool = new UdpDatagramSendPool(1, 8, args =>
        {
            pending = args;
            if (behavior == 0) throw new SocketException((int)SocketError.NetworkDown);
            if (behavior == 1) { args.SocketError = SocketError.NetworkUnreachable; return false; }
            return behavior == 2;
        });
        Assert.IsFalse(pool.TrySend(new byte[] { 1 }, Destination()));
        Assert.AreEqual(1L, pool.FailureCount);
        Assert.AreEqual(0, pool.InFlightCount);
        behavior = 1;
        Assert.IsFalse(pool.TrySend(new byte[] { 2 }, Destination()));
        Assert.AreEqual(2L, pool.FailureCount);
        behavior = 2;
        Assert.IsTrue(pool.TrySend(new byte[] { 3 }, Destination()));
        pending.SocketError = SocketError.ConnectionReset;
        Assert.IsTrue(pool.Return(pending));
        Assert.IsFalse(pool.Return(pending));
        Assert.AreEqual(3L, pool.FailureCount);
        behavior = 3;
        Assert.IsTrue(pool.TrySend(new byte[] { 4 }, Destination()));
        Assert.AreEqual(3L, pool.FailureCount, "A reused args object must not retain its prior SocketError.");
        Assert.AreEqual(0, pool.InFlightCount);
    }

    [TestMethod]
    public void CompletionMayArriveBeforeTheAsynchronousSenderReturns()
    {
        UdpDatagramSendPool pool = null;
        pool = new UdpDatagramSendPool(1, 8, args => { Assert.IsTrue(pool.Return(args)); return true; });
        using (pool)
        {
            Assert.IsTrue(pool.TrySend(new byte[] { 1 }, Destination()));
            Assert.AreEqual(0, pool.InFlightCount);
            Assert.IsTrue(pool.TrySend(new byte[] { 2 }, Destination()));
            Assert.AreEqual(0L, pool.FailureCount);
        }
        Assert.AreEqual(1, pool.DisposedEntryCount);
    }

    [TestMethod]
    public void ActualCompletedEventUsesTheSameOwnedReturnPath()
    {
        SocketAsyncEventArgs pending = null;
        using var pool = new UdpDatagramSendPool(1, 8, args => { pending = args; return true; });
        Assert.IsTrue(pool.TrySend(new byte[] { 1, 2 }, Destination()));
        pending.SocketError = SocketError.HostUnreachable;
        MethodInfo complete = typeof(SocketAsyncEventArgs).GetMethod("OnCompleted",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(complete);
        complete.Invoke(pending, new object[] { pending });
        Assert.AreEqual(0, pool.InFlightCount);
        Assert.AreEqual(1L, pool.FailureCount);
        Assert.IsFalse(pool.Return(pending));
    }

    [TestMethod]
    public void BackendMayDisposeThePoolWithoutDeadlockOrPrematureArgsDisposal()
    {
        UdpDatagramSendPool pool = null;
        pool = new UdpDatagramSendPool(1, 8, args =>
        {
            pool.Dispose();
            Assert.AreEqual(0, pool.DisposedEntryCount);
            CollectionAssert.AreEqual(new byte[] { 1 }, Bytes(args));
            return false;
        });
        using (pool)
        {
            Assert.IsTrue(pool.TrySend(new byte[] { 1 }, Destination()));
            Assert.AreEqual(1, pool.DisposedEntryCount);
            Assert.AreEqual(0, pool.InFlightCount);
            Assert.IsFalse(pool.TrySend(new byte[] { 2 }, Destination()));
        }
    }

    [TestMethod]
    public void DisposeDefersAdmittedArgsUntilTheirExactCompletion()
    {
        var pending = new List<SocketAsyncEventArgs>();
        var pool = new UdpDatagramSendPool(3, 8, args => { pending.Add(args); return true; });
        Assert.IsTrue(pool.TrySend(new byte[] { 1, 2 }, Destination()));
        Assert.IsTrue(pool.TrySend(new byte[] { 3 }, Destination()));
        pool.Dispose();
        Assert.AreEqual(1, pool.DisposedEntryCount);
        Assert.AreEqual(2, pool.InFlightCount);
        Assert.IsFalse(pool.TrySend(new byte[] { 4 }, Destination()));
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, Bytes(pending[0]));
        Assert.IsTrue(pool.Return(pending[1]));
        Assert.AreEqual(2, pool.DisposedEntryCount);
        Assert.IsTrue(pool.Return(pending[0]));
        Assert.AreEqual(3, pool.DisposedEntryCount);
        Assert.AreEqual(0, pool.InFlightCount);
        pool.Dispose();
        Assert.AreEqual(3, pool.DisposedEntryCount);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StopRacingAnAdmittedBackendCannotDisposeItsArgsEarly(bool asynchronous)
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        SocketAsyncEventArgs pending = null;
        using var pool = new UdpDatagramSendPool(1, 8, args =>
        {
            pending = args;
            entered.Set();
            if (!release.Wait(2_000)) throw new TimeoutException();
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, Bytes(args));
            Assert.AreEqual(3, args.Count);
            return asynchronous;
        });
        Task<bool> publisher = Task.Run(() => pool.TrySend(new byte[] { 1, 2, 3 }, Destination()));
        try
        {
            Assert.IsTrue(entered.Wait(2_000));
            pool.Dispose();
            Assert.AreEqual(0, pool.DisposedEntryCount);
            Assert.IsFalse(pool.TrySend(new byte[] { 9 }, Destination()));
        }
        finally { release.Set(); }
        Assert.IsTrue(publisher.Wait(2_000));
        Assert.IsTrue(publisher.Result);
        if (asynchronous)
        {
            Assert.AreEqual(1, pool.InFlightCount);
            Assert.AreEqual(0, pool.DisposedEntryCount);
            Assert.IsTrue(pool.Return(pending));
        }
        Assert.AreEqual(0, pool.InFlightCount);
        Assert.AreEqual(1, pool.DisposedEntryCount);
    }

    [TestMethod]
    public void LateOldPoolCompletionNeverCreditsASuccessorPool()
    {
        SocketAsyncEventArgs oldArgs = null, successorArgs = null;
        using var old = new UdpDatagramSendPool(1, 8, args => { oldArgs = args; return true; });
        Assert.IsTrue(old.TrySend(new byte[] { 1 }, Destination()));
        old.Dispose();
        using var successor = new UdpDatagramSendPool(1, 8, args => { successorArgs = args; return true; });
        Assert.IsTrue(successor.TrySend(new byte[] { 2 }, Destination()));
        Assert.IsFalse(successor.Return(oldArgs));
        Assert.AreEqual(1, successor.InFlightCount);
        Assert.IsTrue(old.Return(oldArgs));
        Assert.AreEqual(1, old.DisposedEntryCount);
        Assert.AreEqual(1, successor.InFlightCount);
        CollectionAssert.AreEqual(new byte[] { 2 }, Bytes(successorArgs));
        Assert.IsTrue(successor.Return(successorArgs));
    }

    [TestMethod]
    public void ParallelPublishersCannotSharePendingArgsOrBuffers()
    {
        const int capacity = 8, publishers = 64;
        var submitted = new ConcurrentBag<SocketAsyncEventArgs>();
        using var pool = new UdpDatagramSendPool(capacity, 32, args => { submitted.Add(args); return true; });
        var accepted = new ConcurrentBag<int>();
        Parallel.For(0, publishers, value =>
        {
            var bytes = Enumerable.Repeat((byte)(value + 1), value % 31 + 1).ToArray();
            if (pool.TrySend(bytes, Destination())) accepted.Add(value + 1);
            Array.Fill(bytes, (byte)0);
        });
        Assert.AreEqual(capacity, submitted.Count);
        Assert.AreEqual(capacity, pool.InFlightCount);
        Assert.AreEqual((long)(publishers - capacity), pool.CapacityDropCount);
        Assert.AreEqual(capacity, submitted.Distinct().Count());
        Assert.AreEqual(capacity, submitted.Select(args => args.Buffer).Distinct().Count());
        foreach (var args in submitted)
        {
            byte[] bytes = Bytes(args);
            Assert.IsTrue(accepted.Contains(bytes[0]));
            Assert.IsTrue(bytes.All(value => value == bytes[0]));
            Assert.IsTrue(pool.Return(args));
        }
        Assert.AreEqual(0, pool.InFlightCount);
    }

    [TestMethod]
    public void ConcurrentStopReserveAndCompletionRetireEveryEntry()
    {
        // Stress the reserve/copy/backend boundary as well as the release vs.
        // Dispose scan interleaving; do not require a timing-specific winner.
        for (int round = 0; round < 32; round++)
        {
            var pending = new ConcurrentBag<SocketAsyncEventArgs>();
            var pool = new UdpDatagramSendPool(8, 1024, args =>
            {
                Assert.AreEqual(1024, args.Count);
                Assert.IsTrue(args.Buffer.Take(args.Count).All(value => value == 17));
                pending.Add(args);
                return true;
            });
            byte[] bytes = Enumerable.Repeat((byte)17, 1024).ToArray();
            Parallel.Invoke(
                () => Parallel.For(0, 16, _ => pool.TrySend(bytes, Destination())),
                pool.Dispose);
            Parallel.ForEach(pending, args => Assert.IsTrue(pool.Return(args)));
            pool.Dispose();
            Assert.AreEqual(0, pool.InFlightCount);
            Assert.AreEqual(8, pool.DisposedEntryCount);
            Assert.AreEqual(0L, pool.FailureCount);
        }
    }

    [TestMethod]
    public void InvalidInputIsRejectedWithoutBorrowingCapacity()
    {
        int calls = 0;
        using var pool = new UdpDatagramSendPool(1, 2, _ => { calls++; return false; });
        Assert.IsFalse(pool.TrySend(new byte[3], Destination()));
        Assert.IsFalse(pool.TrySend(new byte[1], null));
        Assert.AreEqual(2L, pool.FailureCount);
        Assert.AreEqual(0L, pool.CapacityDropCount);
        Assert.AreEqual(0, pool.InFlightCount);
        Assert.AreEqual(0, calls);
        Assert.IsTrue(pool.TrySend(new byte[2], Destination()));
        Assert.AreEqual(1, calls);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new UdpDatagramSendPool(0, 1, _ => false));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new UdpDatagramSendPool(1, 0, _ => false));
        Assert.ThrowsException<ArgumentNullException>(() => new UdpDatagramSendPool(1, 1, null));
    }

    [TestMethod]
    public void WarmSynchronousReuseAllocatesNothing()
    {
        using var pool = new UdpDatagramSendPool(4, 128, static _ => false);
        byte[] bytes = new byte[100];
        IPEndPoint recipient = Destination();
        for (int i = 0; i < 2_000; i++) pool.TrySend(bytes, recipient);
        int accepted = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++)
            if (pool.TrySend(bytes.AsSpan(0, i % 101), recipient)) accepted++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(20_000, accepted);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(0, pool.InFlightCount);
        Assert.AreEqual(0L, pool.CapacityDropCount);
        Assert.AreEqual(0L, pool.FailureCount);
    }

    private static byte[] Bytes(SocketAsyncEventArgs args) =>
        args.Buffer.AsSpan(args.Offset, args.Count).ToArray();
}
