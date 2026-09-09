using DS4Windows;
using DS4Windows.InputDevices;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests;

[TestClass]
public sealed class LegacyJoyConDisconnectTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void EitherHalfDisconnectsTheExactPairOnceEvenAfterRequesterRemoval(bool rightOwns, bool requestSecondary)
    {
        var f = new Fixture(rightOwns);
        var requester = requestSecondary ? f.Peer : f.Owner;
        var peer = ReferenceEquals(requester, f.Left) ? f.Right : f.Left;
        var unrelated = new RecordingJoyCon(true);
        requester.JointDevice = unrelated;
        requester.OnDisconnect = _ => f.Links.Remove(requester);

        Assert.IsTrue(requester.DisconnectBT(callRemoval: true));
        Assert.AreEqual(1, requester.PhysicalDisconnects);
        Assert.AreEqual(0, peer.PhysicalDisconnects, "The peer shuts down on its own physical reader queue.");
        Assert.AreEqual(1, peer.Queued.Count);
        Assert.IsTrue(peer.ProfileConnection.DisconnectRequested);
        Assert.AreEqual(0, f.Links.GetStandaloneConnections().Length,
            "A pending pair disconnect must not briefly advertise its surviving half for linking.");
        Assert.IsFalse(peer.DisconnectBT(), "A simultaneous second logical request cannot recurse or duplicate shutdown.");

        var next = new RecordingJoyCon(requester.DeviceType == InputDeviceType.JoyConL);
        next.ProfileConnection = f.Links.Register(next, requester.DeviceSlotNumber);
        Assert.IsFalse(f.Links.TryLink(peer.ProfileConnection, next.ProfileConnection,
            (_, _) => Assert.Fail("A terminally claimed half cannot change logical owner."), out _));
        peer.Drain();
        Assert.AreEqual(1, peer.PhysicalDisconnects);
        Assert.IsTrue(peer.LastCallRemoval);
        Assert.AreEqual(0, requester.Queued.Count, "Peer completion calls physical shutdown, not logical disconnect again.");
        Assert.AreEqual(0, unrelated.PhysicalDisconnects);
        Assert.AreEqual(0, unrelated.Queued.Count, "The obsolete JointDevice reference is never disconnect authority.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void QueuedDisconnectCannotRetireAReplacedConnectionEvenWhenThePhysicalObjectIsReused(bool reuseObject)
    {
        var f = new Fixture();
        Assert.IsTrue(f.Owner.DisconnectBT());
        var retired = f.Peer.ProfileConnection;
        f.Links.Remove(f.Peer);
        var replacement = reuseObject ? f.Peer : new RecordingJoyCon(false);
        replacement.ProfileConnection = f.Links.Register(replacement, 1);
        Assert.AreNotSame(retired, replacement.ProfileConnection);
        f.Peer.Drain();
        Assert.AreEqual(0, f.Peer.PhysicalDisconnects);
        Assert.AreEqual(0, replacement.PhysicalDisconnects);
        Assert.IsFalse(replacement.ProfileConnection.DisconnectRequested);
    }

    [TestMethod]
    public void PendingLogicalDisconnectPreventsUnlinkUntilRetirement()
    {
        var f = new Fixture();
        var joined = f.Owner.ProfileConnection.Group;
        Assert.IsTrue(f.Owner.DisconnectBT());
        Assert.IsFalse(f.Links.TryUnlink(joined, _ => Assert.Fail("Do not restore a pad during logical shutdown.")));
        f.Peer.Drain();
        Assert.IsFalse(f.Peer.LastCallRemoval);
    }

    [TestMethod]
    public void UnrequestedPhysicalLossStillLeavesAUsableUnclaimedSurvivor()
    {
        var f = new Fixture();
        Assert.AreSame(f.Peer.ProfileConnection, f.Links.Remove(f.Owner));
        Assert.IsFalse(f.Peer.ProfileConnection.DisconnectRequested);
        CollectionAssert.AreEqual(new[] { f.Peer.ProfileConnection }, f.Links.GetStandaloneConnections());
        Assert.AreEqual(0, f.Peer.Queued.Count);
        Assert.AreEqual(0, f.Peer.PhysicalDisconnects);
        var replacement = new RecordingJoyCon(true);
        replacement.ProfileConnection = f.Links.Register(replacement, 0);
        Assert.IsTrue(f.Links.TryLink(f.Peer.ProfileConnection, replacement.ProfileConnection, (_, _) => { }, out _));
    }

    [TestMethod]
    public void PausedOrRetiredConnectionCannotStartAPhysicalDisconnect()
    {
        var f = new Fixture();
        f.Owner.ProfileConnection.Paused = true;
        Assert.IsFalse(f.Owner.DisconnectBT());
        f.Owner.ProfileConnection.Paused = false;
        f.Links.Clear();
        Assert.IsFalse(f.Owner.DisconnectBT());
        Assert.AreEqual(0, f.Owner.PhysicalDisconnects);
        Assert.AreEqual(0, f.Peer.Queued.Count);
    }

    private sealed class Fixture
    {
        internal readonly LegacyJoyConLinkCoordinator Links = new(() => new NoProjection(), (_, _, _) => { });
        internal readonly RecordingJoyCon Left = new(true), Right = new(false);
        internal readonly RecordingJoyCon Owner, Peer;
        internal Fixture(bool rightOwns = false)
        {
            Left.DeviceSlotNumber = 0; Right.DeviceSlotNumber = 1;
            Left.ProfileConnection = Links.Register(Left, 0);
            Right.ProfileConnection = Links.Register(Right, 1);
            Owner = rightOwns ? Right : Left;
            Peer = rightOwns ? Left : Right;
            Assert.IsTrue(Links.TryLink(Owner.ProfileConnection, Peer.ProfileConnection, (_, _) => { }, out _));
        }
    }

    private sealed class NoProjection : ILegacyJoyConProfileProjection
    {
        public bool TryApply(in LegacyJoyConProjectionInput input, DS4State destination) => false;
    }

    // Exercise the public disconnect entry point and real topology without
    // native Bluetooth calls, HID I/O, worker startup or OS input.
    private sealed class RecordingJoyCon : JoyConDevice
    {
        internal readonly Queue<Action> Queued = new();
        internal int PhysicalDisconnects;
        internal bool LastCallRemoval;
        internal Action<bool> OnDisconnect;
        internal RecordingJoyCon(bool left) : base(
            (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice)), "Original disconnect test")
        { deviceType = left ? InputDeviceType.JoyConL : InputDeviceType.JoyConR; }
        public override void queueEvent(Action action) => Queued.Enqueue(action);
        protected override bool DisconnectBluetoothPhysical(bool callRemoval)
        {
            PhysicalDisconnects++;
            LastCallRemoval = callRemoval;
            OnDisconnect?.Invoke(callRemoval);
            return true;
        }
        internal void Drain() { while (Queued.TryDequeue(out var action)) action(); }
    }
}
