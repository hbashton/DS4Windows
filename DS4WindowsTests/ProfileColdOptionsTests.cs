using DS4Windows;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class ProfileColdOptionsTests
{
    private const int Slot = 0;

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UiRefreshRetriesBusyActionOrReportAdmissionOutsidePause(bool reportBusy)
    {
        using var fixture = new RefreshFixture();
        InputControllerActionLease action = default;
        InputControllerReportLease report = default;
        if (reportBusy)
            Assert.IsTrue(fixture.Table.TryAcquireReportLease(fixture.Token, fixture.Source, out report, out _));
        else
            Assert.IsTrue(fixture.Table.TryAcquireActionLease(fixture.Token, 0, out action, out _));
        try
        {
            Assert.IsFalse(fixture.TryRefresh(() => Assert.Fail("Busy admission applied mapping."), out bool applied));
            Assert.IsFalse(applied);
            Assert.IsTrue(fixture.Source.FireReport, "Backoff must never retain a publication pause.");
            Assert.IsFalse(fixture.Table.GetSnapshot()[Slot].ActionPending);
        }
        finally { action.Dispose(); report.Dispose(); }
        int calls = 0;
        Assert.IsTrue(fixture.TryRefresh(() =>
        {
            Assert.IsFalse(fixture.Source.FireReport);
            Assert.IsTrue(fixture.Table.GetSnapshot()[Slot].ActionActive);
            calls++;
        }, out bool completed));
        Assert.IsTrue(completed);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(fixture.Source.FireReport);
        Assert.IsFalse(fixture.Table.GetSnapshot()[Slot].ActionActive);
    }

    [TestMethod]
    public void UiRefreshRejectsSupersededRevisionAfterBusyBackend()
    {
        using var fixture = new RefreshFixture();
        Assert.IsFalse(ProfileColdWorkQueue.TryRefreshMapping(Slot, fixture.Target,
            fixture.Revision, _ => false, () => Assert.Fail("Busy backend applied."), out bool applied));
        Assert.IsFalse(applied);
        Global.BeginProfileSwitchRevision(Slot);
        Assert.IsTrue(fixture.TryRefresh(() => Assert.Fail("Superseded refresh applied."), out applied));
        Assert.IsFalse(applied);
        Assert.IsTrue(fixture.Source.FireReport);
    }

    [TestMethod]
    public void UiRefreshRejectsRetiredRegistrationAfterBusyBackend()
    {
        using var fixture = new RefreshFixture();
        Assert.IsFalse(ProfileColdWorkQueue.TryRefreshMapping(Slot, fixture.Target,
            fixture.Revision, _ => false, () => Assert.Fail("Busy backend applied."), out _));
        Assert.IsTrue(fixture.Table.TryBeginRetire(fixture.Token, out _, out _));
        Assert.IsTrue(fixture.TryRefresh(() => Assert.Fail("Retired refresh applied."), out bool applied));
        Assert.IsFalse(applied);
    }

    [TestMethod]
    public async Task PendingProfilesCoalesceWithoutConcurrentColdConsumers()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = new List<int>();
        var errors = new ConcurrentQueue<Exception>();
        var queue = new ProfileColdWorkQueue(errors.Enqueue);
        Task drain = queue.Queue(() =>
        {
            calls.Add(1);
            entered.SetResult(true);
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)));
            return true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            _ = queue.Queue(() => { calls.Add(2); return true; });
            _ = queue.Queue(() => { calls.Add(3); return true; });
            CollectionAssert.AreEqual(new[] { 1 }, calls);
        }
        finally { release.Set(); }
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, errors.Count, string.Join(Environment.NewLine, errors));
        CollectionAssert.AreEqual(new[] { 1, 3 }, calls);
    }

    [TestMethod]
    public async Task NewProfileSupersedesBusyAdmissionWithoutApplyingOldWork()
    {
        var attempted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        var queue = new ProfileColdWorkQueue(errors.Enqueue);
        int applied = 0;
        Task drain = queue.Queue(() => { attempted.TrySetResult(true); return false; });
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = queue.Queue(() => { Interlocked.Increment(ref applied); return true; });
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, errors.Count, string.Join(Environment.NewLine, errors));
        Assert.AreEqual(1, applied);
    }

    [TestMethod]
    public async Task FailureDoesNotStrandSubsequentProfile()
    {
        int failures = 0;
        var queue = new ProfileColdWorkQueue(ex => Interlocked.Increment(ref failures));
        await queue.Queue(() => throw new InvalidOperationException("fixture"))
            .WaitAsync(TimeSpan.FromSeconds(5));
        int applied = 0;
        await queue.Queue(() => { applied++; return true; })
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, failures);
        Assert.AreEqual(1, applied);
    }

    [TestMethod]
    public void StaleSourceOrRevisionNeverAdmitsColdEffects()
    {
        Assert.IsTrue(ProfileColdWorkQueue.TryApply(Slot, new object(),
            () => false, () => Assert.Fail("Stale work was applied.")));
    }

    [TestMethod]
    public void SourceIsRecheckedUnderBothGatesBeforeApplying()
    {
        int checks = 0;
        Assert.IsTrue(ProfileColdWorkQueue.TryApply(Slot, new object(),
            () => ++checks == 1, () => Assert.Fail("Retired work was applied.")));
        Assert.AreEqual(2, checks);
    }

    [TestMethod]
    public void ColdApplyOwnsProfileAndLifecycleGates()
    {
        var lifecycle = new object();
        bool applied = false;
        Assert.IsTrue(ProfileColdWorkQueue.TryApply(Slot, lifecycle, () => true, () =>
        {
            Assert.IsTrue(Monitor.IsEntered(lifecycle));
            // Profile mutation is reentrant for this owning cold thread.
            Assert.IsTrue(ProfileMutationGate.TryEnter(Slot, out var nested));
            nested.Dispose();
            applied = true;
        }));
        Assert.IsTrue(applied);
        Assert.IsFalse(Monitor.IsEntered(lifecycle));
    }

    [TestMethod]
    public async Task BusyLifecycleDoesNotWaitOrRetainProfileGate()
    {
        var lifecycle = new object();
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Task owner = Task.Run(() =>
        {
            lock (lifecycle)
            {
                held.SetResult(true);
                Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)));
            }
        });
        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.IsFalse(ProfileColdWorkQueue.TryApply(Slot, lifecycle,
                () => true, () => Assert.Fail("Busy lifetime admitted work.")));
            bool acquired = await Task.Run(() =>
            {
                bool result = ProfileMutationGate.TryEnter(Slot, out var gate);
                gate.Dispose();
                return result;
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(acquired, "A retry retained the profile gate.");
        }
        finally { release.Set(); }
        await owner.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task BusyProfileMutationDoesNotAcquireLifecycleGate()
    {
        var lifecycle = new object();
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Task owner = Task.Run(() =>
        {
            using var mutation = ProfileMutationGate.Enter(Slot);
            held.SetResult(true);
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)));
        });
        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.IsFalse(ProfileColdWorkQueue.TryApply(Slot, lifecycle,
                () => true, () => Assert.Fail("Busy mutation admitted work.")));
            Assert.IsFalse(Monitor.IsEntered(lifecycle));
        }
        finally { release.Set(); }
        await owner.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void ExceptionReleasesBothAdmissionGates()
    {
        var lifecycle = new object();
        Assert.ThrowsException<InvalidOperationException>(() =>
            ProfileColdWorkQueue.TryApply(Slot, lifecycle, () => true,
                () => throw new InvalidOperationException("fixture")));
        Assert.IsFalse(Monitor.IsEntered(lifecycle));
        Assert.IsTrue(ProfileMutationGate.TryEnter(Slot, out var mutation));
        mutation.Dispose();
    }

    private sealed class RefreshFixture : IDisposable
    {
        private readonly long oldRevision = Global.ReadProfileSwitchRevision(Slot);
        internal readonly DS4Device Source;
        internal readonly InputControllerRegistrationTable Table = new(Global.MAX_DS4_CONTROLLER_COUNT);
        internal readonly InputControllerSlotToken Token;
        internal readonly ControllerProfileActionTarget Target;
        internal readonly long Revision;

        internal RefreshFixture()
        {
            var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            Source = new DS4Device(hid, "Offline refresh admission fixture");
            Source.ReadWaitEv.Set();
            Source.FireReport = true;
            var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            service.DS4Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
            service.DS4Controllers[Slot] = Source;
            Assert.IsTrue(Table.TryOpen(1, out _));
            Assert.IsTrue(InputControllerRegistration.TryCreate(Source, 95_101,
                InputControllerOwnershipKind.LegacyHid, true, false,
                new RefreshOwner(Source), out var registration, out _));
            Assert.IsTrue(Table.TryReserveAndBindExactSlot(Slot, registration, out Token, out _, out _));
            Assert.IsTrue(Table.TryActivate(Token, out _));
            Assert.IsTrue(ControllerProfileActionTarget.TryCapture(service, Table, Slot, Source, out Target));
            Revision = Global.BeginProfileSwitchRevision(Slot);
        }

        internal bool TryRefresh(Action apply, out bool applied) =>
            ProfileColdWorkQueue.TryRefreshMapping(Slot, Target, Revision,
                action => { action(); return true; }, apply, out applied);

        public void Dispose()
        {
            var revisions = (long[])typeof(Global).GetField("profileSwitchRevisions",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Interlocked.Exchange(ref revisions[Slot], oldRevision);
            Source.ReadWaitEv.Dispose();
        }
    }

    private sealed class RefreshOwner(DS4Device device) : IInputControllerRegistrationOwner
    {
        public InputControllerOwnershipKind Kind => InputControllerOwnershipKind.LegacyHid;
        public bool Authenticates(DS4Device candidate, ulong generation) =>
            ReferenceEquals(device, candidate) && generation == 95_101;
        public bool TryStopAndQuiesce(DS4Device candidate, ulong generation, int timeoutMilliseconds,
            out InputControllerOwnerOperationFailure failure)
        { failure = InputControllerOwnerOperationFailure.StopRejected; return false; }
        public bool TryRemove(DS4Device candidate, ulong generation,
            out InputControllerOwnerOperationFailure failure)
        { failure = InputControllerOwnerOperationFailure.RemoveRejected; return false; }
    }
}
