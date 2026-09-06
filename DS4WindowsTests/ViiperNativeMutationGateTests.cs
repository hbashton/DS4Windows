using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class ViiperNativeMutationGateTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void NestedAdmissionRetainsOwnershipUntilOutermostScopeExits()
    {
        var gate = new ViiperNativeMutationGate();
        using IDisposable outer = gate.Enter();
        using IDisposable inner = gate.Enter();
        var worker = StartWorker(() => { using IDisposable next = gate.Enter(); });
        try
        {
            AssertWaiting(worker.Thread);
            inner.Dispose();
            Assert.IsFalse(worker.Completion.IsCompleted);
            using (gate.Enter()) { } // The original thread still owns admission.
        }
        finally
        {
            inner.Dispose();
            outer.Dispose();
            Join(worker);
        }
    }

    [TestMethod]
    public void CanceledWaiterLeavesWithoutReleasingOwnerOrAdmittingOtherWaiter()
    {
        var gate = new ViiperNativeMutationGate();
        using var cancellation = new CancellationTokenSource();
        using IDisposable owner = gate.Enter();
        var canceled = StartWorker(() => { using IDisposable next = gate.Enter(cancellation.Token); });
        var other = StartWorker(() => { using IDisposable next = gate.Enter(); });
        try
        {
            AssertWaiting(canceled.Thread);
            AssertWaiting(other.Thread);
            cancellation.Cancel();
            OperationCanceledException error = Assert.ThrowsException<OperationCanceledException>(
                () => Join(canceled));
            Assert.AreEqual(cancellation.Token, error.CancellationToken);
            Assert.IsFalse(other.Completion.IsCompleted,
                "Cancellation must not release somebody else's native mutation lease.");
            using (gate.Enter()) { }
        }
        finally
        {
            cancellation.Cancel();
            owner.Dispose();
            JoinIgnoringCancellation(canceled);
            Join(other);
        }
    }

    [TestMethod]
    public void PreCanceledEntryDoesNotTakeOwnership()
    {
        var gate = new ViiperNativeMutationGate();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => gate.Enter(cancellation.Token));
        Join(StartWorker(() => { using IDisposable next = gate.Enter(); }));
    }

    [TestMethod]
    public void PreCanceledReentryDoesNotChangeExistingOwnerDepth()
    {
        var gate = new ViiperNativeMutationGate();
        using IDisposable owner = gate.Enter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => gate.Enter(cancellation.Token));
        var worker = StartWorker(() => { using IDisposable next = gate.Enter(); });
        try { AssertWaiting(worker.Thread); }
        finally
        {
            owner.Dispose();
            Join(worker);
        }
    }

    [TestMethod]
    public void CancelingAdmittedScopeDoesNotReleaseItsLease()
    {
        var gate = new ViiperNativeMutationGate();
        using var cancellation = new CancellationTokenSource();
        using IDisposable owner = gate.Enter(cancellation.Token);
        var worker = StartWorker(() => { using IDisposable next = gate.Enter(); });
        try
        {
            AssertWaiting(worker.Thread);
            cancellation.Cancel();
            Assert.IsFalse(worker.Completion.IsCompleted,
                "Cancellation requests are not proof that the owner's native work has completed.");
            using (gate.Enter()) { }
        }
        finally
        {
            owner.Dispose();
            Join(worker);
        }
    }

    [TestMethod]
    public void WrongThreadDisposeCannotReleaseOrPoisonOwnerScope()
    {
        var gate = new ViiperNativeMutationGate();
        using IDisposable owner = gate.Enter();
        Assert.ThrowsException<SynchronizationLockException>(() => Join(StartWorker(owner.Dispose)));
        var worker = StartWorker(() => { using IDisposable next = gate.Enter(); });
        try { AssertWaiting(worker.Thread); }
        finally
        {
            owner.Dispose();
            owner.Dispose(); // Duplicate completion cannot reduce a successor's depth.
            Join(worker);
        }
        Join(StartWorker(owner.Dispose)); // An already released lease is harmless on any thread.
    }

    [TestMethod]
    public void ThrowingReentrantOperationReleasesSharedPortManagerGate()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            ViiperUsbipPortManager.WithNativePortMutationLock(() =>
                ViiperUsbipPortManager.WithNativePortMutationLock<int>(() =>
                    throw new InvalidOperationException("test"))));
        Join(StartWorker(() => ViiperUsbipPortManager.WithNativePortMutationLock(() => true)));
    }

    [TestMethod]
    public void PreCanceledSharedOperationIsNeverInvoked()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool called = false;
        Assert.ThrowsException<OperationCanceledException>(() =>
            ViiperUsbipPortManager.WithNativePortMutationLock(() => called = true, cancellation.Token));
        Assert.IsFalse(called);
        Join(StartWorker(() => ViiperUsbipPortManager.WithNativePortMutationLock(() => true)));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LegacyDetachUsesSameReentrantAdmissionWithoutCallingNativeCode(bool registered)
    {
        Action detach = registered
            ? () => ViiperUsbipPortManager.DetachRegisteredPort(-1, "invalid test port")
            : () => ViiperUsbipPortManager.DetachDuplicateLocalViiperPorts(0, "0", -1);
        // Invalid ports return before native queries; these calls test the
        // production wrappers without touching a controller, driver or import.
        (Thread Thread, Task Completion) worker = default;
        try
        {
            ViiperUsbipPortManager.WithNativePortMutationLock(() =>
            {
                detach(); // Legacy create -> cleanup nesting must not deadlock.
                worker = StartWorker(detach);
                AssertWaiting(worker.Thread);
                Assert.IsFalse(worker.Completion.IsCompleted);
                return true;
            });
        }
        finally
        {
            if (worker.Thread != null) Join(worker);
        }
    }

    [TestMethod]
    public void CancellationAndOwnerReleaseRacesKeepExclusiveAdmissionAndLeaveNoWaiters()
    {
        var gate = new ViiperNativeMutationGate();
        int activeOwners = 0;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            using var cancellation = new CancellationTokenSource();
            using var release = new ManualResetEventSlim();
            using IDisposable owner = gate.Enter();
            Assert.AreEqual(1, Interlocked.Increment(ref activeOwners));
            var waiter = StartWorker(() =>
            {
                try
                {
                    using IDisposable next = gate.Enter(cancellation.Token);
                    try
                    {
                        Assert.AreEqual(1, Interlocked.Increment(ref activeOwners));
                        using (gate.Enter()) { }
                    }
                    finally { Interlocked.Decrement(ref activeOwners); }
                }
                catch (OperationCanceledException error)
                {
                    Assert.AreEqual(cancellation.Token, error.CancellationToken);
                }
            });
            var canceler = StartWorker(() =>
            {
                Assert.IsTrue(release.Wait(TestTimeout));
                cancellation.Cancel();
            });
            try { AssertWaiting(waiter.Thread); }
            finally
            {
                // Alternate which racer is released first; both outcomes are valid.
                if ((attempt & 1) == 0) release.Set();
                Assert.AreEqual(0, Interlocked.Decrement(ref activeOwners));
                owner.Dispose();
                release.Set();
                Join(canceler);
                Join(waiter);
            }
            Assert.AreEqual(0, Volatile.Read(ref activeOwners));
            Join(StartWorker(() => { using IDisposable next = gate.Enter(); }));
        }
    }

    private static (Thread Thread, Task Completion) StartWorker(Action action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.TrySetResult(true); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true };
        thread.Start();
        return (thread, completion.Task);
    }

    private static void AssertWaiting(Thread worker)
    {
        Assert.IsTrue(SpinWait.SpinUntil(() =>
            (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TestTimeout),
            "The worker must actually reach the admission wait before it can be canceled or released.");
    }

    private static void Join((Thread Thread, Task Completion) worker)
    {
        Assert.IsTrue(worker.Thread.Join(TestTimeout), "A gate worker did not exit within the test bound.");
        worker.Completion.GetAwaiter().GetResult();
    }

    private static void JoinIgnoringCancellation((Thread Thread, Task Completion) worker)
    {
        try { Join(worker); }
        catch (OperationCanceledException) { }
    }
}
