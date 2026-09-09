using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
public sealed class LegacyJoyConLinkCoordinatorTests
{
    [TestMethod]
    public void RegistrationPinsExactConnectionAndRejectsSlotOrDeviceKindConflicts()
    {
        var f = new Fixture();
        Assert.AreSame(f.Left, f.Coordinator.Register(f.Left.Device, f.Left.Slot));
        Assert.ThrowsException<InvalidOperationException>(() => f.Coordinator.Register(f.Left.Device, 7));
        Assert.ThrowsException<InvalidOperationException>(() => f.Coordinator.Register(new FakeDevice(InputDeviceType.JoyConL), f.Left.Slot));
        Assert.ThrowsException<ArgumentException>(() => f.Coordinator.Register(new FakeDevice(InputDeviceType.DualSense), 7));
        Assert.ThrowsException<ArgumentException>(() => f.Coordinator.Register(new FakeDevice(InputDeviceType.JoyConL), -1));
        CollectionAssert.AreEqual(new[] { f.Left, f.Right }, f.Coordinator.GetStandaloneConnections());
    }

    [TestMethod]
    public void BothReportStreamsPublishThroughTheFirstSelectedOwnersUnchangedSlot()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Coordinator.TryLink(f.Right, f.Left, (_, _) => { }, out var joined));
        Assert.AreSame(f.Right, joined.Owner);
        Assert.AreSame(f.Left, joined.Other);
        Assert.IsTrue(f.Right.Device.PrimaryDevice);
        Assert.IsFalse(f.Left.Device.PrimaryDevice);
        var left = new DS4State { Cross = true, PacketCounter = 11 };
        var right = new DS4State { Square = true, PacketCounter = 22 };
        Assert.IsTrue(f.Coordinator.TryPublish(f.Left, left, 10, 1000));
        Assert.IsTrue(f.Coordinator.TryPublish(f.Right, right, 20, 1000));
        Assert.AreSame(f.Right, f.LastOwner);
        Assert.IsTrue(f.LastState.State.Cross);
        Assert.IsTrue(f.LastState.State.Square);
        var projection = (FakeProjection)joined.Projection;
        Assert.AreEqual(f.Right.Slot, projection.LastInput.ProfileSlot);
        Assert.AreEqual(f.Left.Generation, projection.LastInput.LeftGeneration);
        Assert.AreEqual(f.Right.Generation, projection.LastInput.RightGeneration);
        Assert.AreEqual(10L, projection.LastInput.LeftTimestampQpc);
        Assert.AreEqual(20L, projection.LastInput.RightTimestampQpc);
        Assert.IsTrue(projection.LastInput.PairEpoch > 0);
    }

    [TestMethod]
    public void RawCaptureAndPublishedHistoryNeverAliasThePhysicalReadersMotion()
    {
        var f = new Fixture();
        var raw = new DS4State { Cross = true, PacketCounter = 1 };
        raw.Motion.gyroYawFull = 20;
        Assert.IsTrue(f.Coordinator.TryPublish(f.Left, raw, 10, 1000));
        raw.Cross = false;
        raw.Motion.gyroYawFull = 500;
        Assert.IsTrue(f.Left.Raw.State.Cross);
        Assert.AreEqual(20, f.Left.Raw.State.Motion.gyroYawFull);
        Assert.AreNotSame(raw.Motion, f.Left.Raw.State.Motion);
        raw.PacketCounter = 2;
        Assert.IsTrue(f.Coordinator.TryPublish(f.Left, raw, 20, 1000));
        Assert.IsTrue(f.LastPrevious.State.Cross);
        Assert.AreEqual(20, f.LastPrevious.State.Motion.gyroYawFull);
        Assert.IsFalse(f.LastState.State.Cross);
        Assert.AreEqual(500, f.LastState.State.Motion.gyroYawFull);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void AnyLinkProjectionFactoryFailureLeavesBothStandaloneStreamsUntouched(int offset)
    {
        var f = new Fixture();
        var leftGroup = f.Left.Group;
        var rightGroup = f.Right.Group;
        f.FailFactoryAt = f.FactoryCalls + offset;
        int prepared = 0;
        Assert.ThrowsException<InvalidOperationException>(() => f.Coordinator.TryLink(f.Left, f.Right,
            (_, _) => prepared++, out _));
        Assert.AreEqual(0, prepared, "No output pad may be retired before every fresh projection is reserved.");
        Assert.AreSame(leftGroup, f.Left.Group);
        Assert.AreSame(rightGroup, f.Right.Group);
        Assert.IsTrue(leftGroup.Active);
        Assert.IsTrue(rightGroup.Active);
        Assert.IsFalse(f.Left.Paused);
        Assert.IsFalse(f.Right.Paused);
        Assert.IsTrue(f.Coordinator.TryPublish(f.Left, new DS4State(), 10, 1000));
        Assert.IsTrue(f.Coordinator.TryPublish(f.Right, new DS4State(), 11, 1000));
    }

    [TestMethod]
    public void FailedOutputPreparationDoesNotPublishOrRetireATopology()
    {
        var f = new Fixture();
        var beforeLeft = f.Left.Group;
        var beforeRight = f.Right.Group;
        Assert.ThrowsException<IOException>(() => f.Coordinator.TryLink(f.Left, f.Right,
            (_, _) => throw new IOException("output retirement failed"), out _));
        Assert.AreSame(beforeLeft, f.Left.Group);
        Assert.AreSame(beforeRight, f.Right.Group);
        Assert.IsTrue(beforeLeft.Active);
        Assert.IsTrue(beforeRight.Active);
        Assert.IsTrue(f.Left.Device.PrimaryDevice);
        Assert.IsTrue(f.Right.Device.PrimaryDevice);
        Assert.IsFalse(f.Left.Paused || f.Right.Paused);
    }

    [TestMethod]
    public void ReportsDuringSlowLinkPreparationRefreshRawStateWithoutPublishing()
    {
        var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<bool> link = Task.Run(() => f.Coordinator.TryLink(f.Left, f.Right, (_, _) =>
        {
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException("Test did not release link preparation.");
        }, out _));
        try
        {
            Assert.IsTrue(entered.Wait(5000));
            Assert.IsFalse(f.Coordinator.TryPublish(f.Left, new DS4State { Cross = true }, 10, 1000));
            Assert.IsFalse(f.Coordinator.TryPublish(f.Right, new DS4State { Square = true }, 20, 1000));
            Assert.AreEqual(0, f.PublishCount);
            Assert.IsTrue(f.Left.Raw.State.Cross);
            Assert.IsTrue(f.Right.Raw.State.Square);
        }
        finally { release.Set(); }
        Assert.IsTrue(link.Wait(5000));
        Assert.IsTrue(link.Result);
        Assert.IsTrue(f.Coordinator.TryPublish(f.Left, new DS4State { Cross = true }, 30, 1000));
        Assert.IsTrue(f.LastState.State.Square, "The paused right report remains the freshest physical state.");
    }

    [TestMethod]
    public void UnlinkPublishesBothReservedFreshStandaloneGroupsWithoutFactoryWork()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Coordinator.TryLink(f.Left, f.Right, (_, _) => { }, out var joined));
        f.Coordinator.TryPublish(f.Left, new DS4State { Cross = true }, 10, 1000);
        int factoryCalls = f.FactoryCalls;
        f.FailFactoryAt = factoryCalls + 1;
        LegacyJoyConConnection restored = null;
        Assert.IsTrue(f.Coordinator.TryUnlink(joined, secondary => restored = secondary));
        Assert.AreSame(f.Right, restored);
        Assert.AreEqual(factoryCalls, f.FactoryCalls);
        Assert.IsFalse(joined.Active);
        Assert.AreSame(joined.LeftStandalone, f.Left.Group);
        Assert.AreSame(joined.RightStandalone, f.Right.Group);
        Assert.IsTrue(f.Left.Group.Active && f.Right.Group.Active);
        Assert.IsTrue(f.Left.Device.PrimaryDevice && f.Right.Device.PrimaryDevice);
        Assert.IsFalse(f.Left.Paused || f.Right.Paused);
        Assert.AreEqual(0u, f.Left.Group.Previous.State.PacketCounter);
        Assert.AreEqual(0u, f.Right.Group.Previous.State.PacketCounter);
        Assert.IsTrue(f.Coordinator.TryPublish(f.Right, new DS4State { Square = true }, 20, 1000));
        Assert.AreSame(f.Right, f.LastOwner);
        Assert.IsFalse(f.LastState.State.Cross, "Standalone right input cannot retain a removed left button.");
        Assert.AreEqual(0UL, ((FakeProjection)f.Right.Group.Projection).LastInput.PairEpoch);
    }

    [TestMethod]
    public void FailedSecondaryRestoreKeepsJoinedTopologyAndResumesItsReports()
    {
        var f = new Fixture();
        f.Coordinator.TryLink(f.Left, f.Right, (_, _) => { }, out var joined);
        Assert.ThrowsException<IOException>(() => f.Coordinator.TryUnlink(joined,
            _ => throw new IOException("secondary output could not be restored")));
        Assert.IsTrue(joined.Active);
        Assert.AreSame(joined, f.Left.Group);
        Assert.AreSame(joined, f.Right.Group);
        Assert.IsFalse(f.Left.Paused || f.Right.Paused);
        Assert.IsTrue(f.Coordinator.TryPublish(f.Right, new DS4State(), 20, 1000));
        Assert.AreSame(f.Left, f.LastOwner);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RemovingEitherHalfImmediatelyInvalidatesItAndPromotesAFreshSurvivorWithoutFactoryWork(bool removeOwner)
    {
        var f = new Fixture();
        f.Coordinator.TryLink(f.Right, f.Left, (_, _) => { }, out var joined);
        var removed = removeOwner ? f.Right : f.Left;
        var survivor = removeOwner ? f.Left : f.Right;
        int factoryCalls = f.FactoryCalls;
        f.FailFactoryAt = factoryCalls + 1;
        Assert.AreSame(survivor, f.Coordinator.Remove(removed.Device));
        Assert.AreEqual(factoryCalls, f.FactoryCalls);
        Assert.IsFalse(f.Coordinator.IsCurrent(removed));
        Assert.IsFalse(removed.Connected);
        Assert.IsFalse(joined.Active);
        Assert.IsTrue(f.Coordinator.IsCurrent(survivor));
        Assert.IsTrue(survivor.Group.Active);
        Assert.IsFalse(survivor.Group.Joined);
        Assert.IsTrue(survivor.Device.PrimaryDevice);
        Assert.IsFalse(f.Coordinator.TryPublish(removed, new DS4State { Cross = true }, 10, 1000));
        Assert.IsTrue(f.Coordinator.TryPublish(survivor, new DS4State(), 20, 1000));
        Assert.AreSame(survivor, f.LastOwner);
        Assert.IsFalse(f.Coordinator.TryUnlink(joined, _ => Assert.Fail("A stale group cannot restore output.")));
    }

    [TestMethod]
    public void SlotReuseDoesNotReviveAnOldConnectionOrPairSelection()
    {
        var f = new Fixture();
        f.Coordinator.Remove(f.Left.Device);
        var replacement = f.Coordinator.Register(new FakeDevice(InputDeviceType.JoyConL), f.Left.Slot);
        Assert.IsTrue(replacement.Generation > f.Left.Generation);
        Assert.IsFalse(f.Coordinator.TryLink(f.Left, f.Right, (_, _) => Assert.Fail(), out _));
        Assert.IsFalse(f.Coordinator.TryPublish(f.Left, new DS4State(), 10, 1000));
        Assert.IsTrue(f.Coordinator.TryLink(replacement, f.Right, (_, _) => { }, out var joined));
        Assert.AreSame(replacement, joined.Owner);
    }

    [TestMethod]
    public void SynchronousDisconnectDuringLinkCannotPublishAnAlreadyRemovedPair()
    {
        var f = new Fixture();
        Assert.IsFalse(f.Coordinator.TryLink(f.Left, f.Right,
            (_, _) => f.Coordinator.Remove(f.Left.Device), out var joined));
        Assert.IsNull(joined);
        Assert.IsFalse(f.Left.Connected);
        Assert.IsTrue(f.Right.Group.Active);
        Assert.IsFalse(f.Right.Paused);
        Assert.IsTrue(f.Coordinator.TryPublish(f.Right, new DS4State(), 10, 1000));
        Assert.AreSame(f.Right, f.LastOwner);
    }

    [TestMethod]
    public void DisconnectQueuedDuringSlowLinkCompletesAgainstTheNewTopology()
    {
        var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var removeStarted = new ManualResetEventSlim();
        Task<bool> link = Task.Run(() => f.Coordinator.TryLink(f.Left, f.Right, (_, _) =>
        {
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException("Test did not release preparation.");
        }, out _));
        Task<LegacyJoyConConnection> removal = null;
        try
        {
            Assert.IsTrue(entered.Wait(5000));
            removal = Task.Run(() => { removeStarted.Set(); return f.Coordinator.Remove(f.Left.Device); });
            Assert.IsTrue(removeStarted.Wait(5000));
            Assert.IsFalse(f.Coordinator.TryPublish(f.Right, new DS4State { Square = true }, 10, 1000));
        }
        finally { release.Set(); }
        Assert.IsTrue(link.Wait(5000));
        Assert.IsTrue(removal.Wait(5000));
        Assert.IsTrue(link.Result);
        Assert.AreSame(f.Right, removal.Result);
        Assert.IsFalse(f.Left.Connected);
        Assert.IsFalse(f.Right.Group.Joined);
        Assert.IsTrue(f.Coordinator.TryPublish(f.Right, new DS4State { Square = true }, 20, 1000));
        Assert.AreSame(f.Right, f.LastOwner);
    }

    [TestMethod]
    public void SynchronousDisconnectAndSlotReplacementDuringUnlinkCannotOverwriteSurvivorTopology()
    {
        var f = new Fixture();
        f.Coordinator.TryLink(f.Left, f.Right, (_, _) => { }, out var joined);
        LegacyJoyConConnection replacement = null;
        Assert.IsFalse(f.Coordinator.TryUnlink(joined, _ =>
        {
            Assert.AreSame(f.Right, f.Coordinator.Remove(f.Left.Device));
            replacement = f.Coordinator.Register(new FakeDevice(InputDeviceType.JoyConL), f.Left.Slot);
        }));
        Assert.AreSame(joined.RightStandalone, f.Right.Group);
        Assert.IsTrue(f.Right.Group.Active);
        Assert.IsFalse(f.Right.Paused);
        Assert.IsTrue(f.Coordinator.IsCurrent(replacement));
        Assert.IsFalse(f.Coordinator.TryPublish(f.Left, new DS4State(), 10, 1000));
        Assert.IsTrue(f.Coordinator.TryPublish(f.Right, new DS4State(), 20, 1000));
        Assert.AreSame(f.Right, f.LastOwner);
    }

    [TestMethod]
    public void EveryRelinkGetsNewEpochAndNeverReusesOldProjectionHistory()
    {
        var f = new Fixture();
        f.Coordinator.TryLink(f.Left, f.Right, (_, _) => { }, out var first);
        f.Coordinator.TryPublish(f.Left, new DS4State { Cross = true }, 10, 1000);
        Assert.IsTrue(f.Coordinator.TryUnlink(first, _ => { }));
        Assert.IsTrue(f.Coordinator.TryLink(f.Right, f.Left, (_, _) => { }, out var second));
        Assert.IsTrue(second.Epoch > first.Epoch);
        Assert.AreNotSame(first.Projection, second.Projection);
        Assert.AreNotSame(first.LeftStandalone.Projection, second.LeftStandalone.Projection);
        Assert.AreEqual(0u, second.Previous.State.PacketCounter);
    }

    [TestMethod]
    public void ConcurrentHalfReportsHaveOneSerializedPublisherAndCoherentPreviousState()
    {
        var f = new Fixture();
        f.Coordinator.TryLink(f.Left, f.Right, (_, _) => { }, out _);
        int inside = 0, overlap = 0;
        f.OnPublish = () =>
        {
            if (Interlocked.Increment(ref inside) != 1) Interlocked.Increment(ref overlap);
            Thread.SpinWait(20);
            Interlocked.Decrement(ref inside);
        };
        Task left = Task.Run(() => Run(f.Left));
        Task right = Task.Run(() => Run(f.Right));
        Assert.IsTrue(Task.WaitAll(new[] { left, right }, 10000));
        Assert.AreEqual(0, overlap);
        Assert.AreEqual(2000, f.PublishCount);
        Assert.AreEqual(1999u, f.LastPrevious.State.PacketCounter);
        Assert.AreEqual(2000u, f.LastState.State.PacketCounter);
        void Run(LegacyJoyConConnection entry)
        {
            var state = new DS4State();
            for (int i = 1; i <= 1000; i++)
                if (!f.Coordinator.TryPublish(entry, state, i, 1000)) throw new InvalidOperationException("Unexpected report rejection.");
        }
    }

    [TestMethod]
    public void ProjectionRejectionAndPublisherExceptionDoNotCommitFalsePreviousHistory()
    {
        var f = new Fixture();
        var raw = new DS4State { Cross = true };
        f.Coordinator.TryPublish(f.Left, raw, 10, 1000);
        var projection = (FakeProjection)f.Left.Group.Projection;
        projection.Reject = true;
        Assert.IsFalse(f.Coordinator.TryPublish(f.Left, raw, 20, 1000));
        Assert.AreEqual(1u, f.Left.Group.Previous.State.PacketCounter);
        projection.Reject = false;
        f.OnPublish = () => throw new IOException("publisher failure");
        Assert.ThrowsException<IOException>(() => f.Coordinator.TryPublish(f.Left, raw, 30, 1000));
        Assert.AreEqual(1u, f.Left.Group.Previous.State.PacketCounter);
        f.OnPublish = null;
        Assert.IsTrue(f.Coordinator.TryPublish(f.Left, raw, 40, 1000));
        Assert.AreEqual(1u, f.LastPrevious.State.PacketCounter);
    }

    [TestMethod]
    public void ClearInvalidatesEveryOldHandleWithoutPublishingOrRestoringASecondary()
    {
        var f = new Fixture();
        f.Coordinator.TryLink(f.Left, f.Right, (_, _) => { }, out var joined);
        f.Coordinator.Clear();
        Assert.IsFalse(f.Left.Connected || f.Right.Connected);
        Assert.IsFalse(joined.Active);
        Assert.AreEqual(0, f.Coordinator.GetStandaloneConnections().Length);
        Assert.IsFalse(f.Coordinator.TryPublish(f.Left, new DS4State(), 10, 1000));
        Assert.IsFalse(f.Coordinator.TryUnlink(joined, _ => Assert.Fail()));
        var replacement = f.Coordinator.Register(f.Left.Device, f.Left.Slot);
        Assert.AreNotSame(f.Left, replacement);
        Assert.IsTrue(replacement.Generation > f.Left.Generation);
    }

    [TestMethod]
    public void WarmJoinedReportHandoffAllocatesNothing()
    {
        var f = new Fixture();
        f.Coordinator.TryLink(f.Left, f.Right, (_, _) => { }, out _);
        var raw = new DS4State();
        for (int i = 1; i <= 512; i++) f.Coordinator.TryPublish(f.Left, raw, i, 1000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= 4096; i++)
        {
            f.Coordinator.TryPublish(f.Left, raw, i, 1000);
            f.Coordinator.TryPublish(f.Right, raw, i, 1000);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(512 + 8192, f.PublishCount);
    }

    private sealed class Fixture
    {
        internal readonly LegacyJoyConLinkCoordinator Coordinator;
        internal readonly LegacyJoyConConnection Left, Right;
        internal readonly DS4StateOwnedSnapshot LastState = new(), LastPrevious = new();
        internal LegacyJoyConConnection LastOwner;
        internal int FactoryCalls, FailFactoryAt, PublishCount;
        internal Action OnPublish;
        internal Fixture()
        {
            Coordinator = new LegacyJoyConLinkCoordinator(() =>
            {
                if (++FactoryCalls == FailFactoryAt) throw new InvalidOperationException("injected projection factory failure");
                return new FakeProjection();
            }, (owner, current, previous) =>
            {
                OnPublish?.Invoke();
                PublishCount++;
                LastOwner = owner;
                LastState.Capture(current);
                LastPrevious.Capture(previous);
            });
            Left = Coordinator.Register(new FakeDevice(InputDeviceType.JoyConL), 2);
            Right = Coordinator.Register(new FakeDevice(InputDeviceType.JoyConR), 5);
        }
    }

    private sealed class FakeProjection : ILegacyJoyConProfileProjection
    {
        internal LegacyJoyConProjectionInput LastInput;
        internal bool Reject;
        public bool TryApply(in LegacyJoyConProjectionInput input, DS4State destination)
        {
            LastInput = input;
            if (Reject) return false;
            destination.Cross = input.Left?.Cross ?? false;
            destination.Square = input.Right?.Square ?? false;
            destination.Motion.gyroYawFull = input.Left?.Motion.gyroYawFull ?? input.Right?.Motion.gyroYawFull ?? 0;
            destination.PacketCounter++;
            return true;
        }
    }

    // Input-only constructor: no HID object, transport, worker or output I/O.
    private sealed class FakeDevice : DS4Device
    {
        internal FakeDevice(InputDeviceType type) : base("Original Joy-Con topology test", type, ConnectionType.BT) { }
    }
}
