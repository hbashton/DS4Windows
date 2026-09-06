using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class LegacyNintendoRumbleOutputTests
{
    private static byte[] Packet(byte value) => Enumerable.Repeat(value, 64).ToArray();

    [TestMethod]
    public async Task PublicationCannotMutateBorrowedNativeStorageAndLatestWins()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Packet(1);
        var newest = Packet(3);
        var reports = new List<byte[]>();
        var owner = new LegacyNintendoRumbleOutput(64, report =>
        {
            if (reports.Count == 0)
            {
                entered.Set();
                release.Wait();
            }
            reports.Add((byte[])report.Clone());
            return true;
        });
        owner.Publish(first, true);
        Task writing = Task.Run(() => owner.PumpOnce());
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
            owner.Publish(Packet(2), true);
            owner.Publish(newest, true);
            Array.Fill(newest, (byte)4); // owner copied it, caller may now reuse
            Assert.IsFalse(owner.PumpOnce(), "No second concurrent writer may enter.");
        }
        finally { release.Set(); await writing.WaitAsync(TimeSpan.FromSeconds(2)); }
        Assert.IsTrue(owner.PumpOnce());
        Assert.AreEqual(2, reports.Count);
        CollectionAssert.AreEqual(first, reports[0]);
        CollectionAssert.AreEqual(Packet(3), reports[1]);
        Assert.IsNull(owner.LastWriteException);
        Assert.IsFalse(owner.PumpOnce());
    }

    [TestMethod]
    public void FailureRequiresAnotherInputWakeRatherThanBusyRetrying()
    {
        int calls = 0;
        var owner = new LegacyNintendoRumbleOutput(64, _ => { ++calls; return false; });
        owner.Publish(Packet(1), true);
        Assert.IsTrue(owner.PumpOnce());
        Assert.IsFalse(owner.PumpOnce());
        owner.RequestRetry();
        Assert.IsTrue(owner.PumpOnce());
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void RetryWakeDuringSuccessfulWriteDoesNotLeaveABusyIdleLoop()
    {
        LegacyNintendoRumbleOutput owner = null;
        owner = new LegacyNintendoRumbleOutput(64, _ => { owner.RequestRetry(); return true; });
        owner.Publish(Packet(1), true);
        Assert.IsTrue(owner.PumpOnce());
        Assert.IsFalse(owner.PumpOnce());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RejectedOrThrowingStopIsNotAcknowledgedAndHasBoundedAttempts(bool throws)
    {
        var reports = new List<byte[]>();
        var neutral = Packet(0);
        var owner = new LegacyNintendoRumbleOutput(64, report =>
        {
            reports.Add((byte[])report.Clone());
            if (report[0] != 0) return true;
            if (throws) throw new IOException("Injected stop failure.");
            return false;
        });
        owner.Publish(Packet(1), true);
        owner.PumpOnce();
        owner.RequestStop(neutral);
        for (int i = 0; i < 10; ++i) owner.PumpOnce();
        Assert.AreEqual(4, reports.Count); // active + at most three stop attempts
        foreach (byte[] report in reports.Skip(1)) CollectionAssert.AreEqual(neutral, report);
        Assert.IsFalse(owner.StopDelivered);
        if (throws) Assert.IsInstanceOfType(owner.LastWriteException, typeof(IOException));
        else Assert.IsNull(owner.LastWriteException);
        Assert.IsFalse(owner.Publish(Packet(2), true));
        owner.RequestRetry();
        Assert.IsFalse(owner.PumpOnce());
    }

    [TestMethod]
    public void StopDiscardsUnsubmittedEffectsWithoutProbingIdleHardware()
    {
        int calls = 0;
        var owner = new LegacyNintendoRumbleOutput(64, _ => { ++calls; return true; });
        owner.Publish(Packet(1), true);
        owner.RequestStop(Packet(0));
        Assert.IsTrue(owner.StopDelivered);
        Assert.IsFalse(owner.PumpOnce());
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void StopRetainsNeutralAcrossFailureThenAcknowledgesAcceptance()
    {
        int neutralCalls = 0;
        var owner = new LegacyNintendoRumbleOutput(64, report =>
            report[0] != 0 || ++neutralCalls == 2);
        owner.Publish(Packet(1), true);
        owner.PumpOnce();
        owner.RequestStop(Packet(0));
        Assert.IsTrue(owner.PumpOnce());
        Assert.IsFalse(owner.StopDelivered);
        Assert.IsTrue(owner.PumpOnce());
        Assert.IsTrue(owner.StopDelivered);
        Assert.IsFalse(owner.PumpOnce());
    }

    [TestMethod]
    public void StoppedOwnerCannotStartAnotherLifetime()
    {
        var owner = new LegacyNintendoRumbleOutput(64, _ => true);
        owner.RequestStop(Packet(0));
        Assert.ThrowsException<InvalidOperationException>(() => owner.Start("Rejected restart"));
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void StopDuringSubmissionAccountsForAmbiguousOrSuccessfulCompletion(bool inFlightActive, bool accepted)
    {
        var reports = new List<byte[]>();
        LegacyNintendoRumbleOutput owner = null;
        bool stopDuringWrite = false;
        owner = new LegacyNintendoRumbleOutput(64, report =>
        {
            reports.Add((byte[])report.Clone());
            if (!stopDuringWrite) return true;
            stopDuringWrite = false;
            owner.RequestStop(Packet(0));
            return accepted;
        });
        owner.Publish(Packet(1), true);
        owner.PumpOnce();
        stopDuringWrite = true;
        owner.Publish(Packet(inFlightActive ? (byte)2 : (byte)0), inFlightActive);
        owner.PumpOnce();
        if (!inFlightActive && accepted)
        {
            Assert.IsTrue(owner.StopDelivered);
            Assert.IsFalse(owner.PumpOnce(), "An already accepted neutral needs no duplicate stop.");
            Assert.AreEqual(2, reports.Count);
        }
        else
        {
            Assert.IsFalse(owner.StopDelivered);
            Assert.IsTrue(owner.PumpOnce());
            Assert.IsTrue(owner.StopDelivered);
            Assert.AreEqual(3, reports.Count);
        }
        CollectionAssert.AreEqual(Packet(0), reports[^1]);
        Assert.IsNull(owner.LastWriteException);
    }

    [TestMethod]
    public async Task NativeFailureRetiresRealWorkerWithoutPretendingStopSucceeded()
    {
        using var entered = new ManualResetEventSlim();
        int calls = 0;
        var owner = new LegacyNintendoRumbleOutput(64, _ =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            return false;
        });
        owner.Start("Rejected Nintendo output");
        try
        {
            owner.Publish(Packet(1), true);
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            bool stopped = await Task.Run(() => owner.StopAndJoin(Packet(0)))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(stopped);
        }
        Assert.AreEqual(4, calls);
        Assert.IsFalse(owner.StopAndJoin(Packet(0)), "Repeated stop must not revive retired native I/O.");
        Assert.AreEqual(4, calls);
    }
}
