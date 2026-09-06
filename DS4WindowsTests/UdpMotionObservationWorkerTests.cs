using System.Collections.Concurrent;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

// Synthetic owned states and ephemeral loopback sessions only. No controller,
// Bluetooth, installed application, external listener or configured DSU port.
[TestClass]
[DoNotParallelize]
public sealed class UdpMotionObservationWorkerTests
{
    private static readonly UdpMotionObservationPolicy Raw = new(false, 0.4, 0.2, 1);
    private static readonly UdpMotionObservationPolicy Smooth = new(true, 0.4, 0.2, 4);

    [TestMethod]
    public void CoalescedSnapshotAndPreviousMotionAreOwnedAndYawDoesNotTouchProducer()
    {
        using var fixture = new Fixture();
        var source = fixture.Register();
        var state = State(1);
        Publish(source, state, fixture.Session, Smooth);
        state.Motion.angVelYaw = 12;
        state.Motion.previousAxis.angVelYaw = 15;
        state.PacketCounter = 2;
        Publish(source, state, fixture.Session, Smooth);
        state.Motion.angVelYaw = 900;
        state.Motion.previousAxis.angVelYaw = 901;
        state.PacketCounter = 3;
        Assert.AreEqual(1L, source.CoalescedCount);
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        Observation observed = fixture.Observed.Single();
        Assert.AreEqual(2u, observed.State.PacketCounter);
        Assert.AreEqual(15.0, observed.State.Motion.angVelYaw);
        Assert.AreEqual(15.0, observed.State.Motion.previousAxis.angVelYaw);
        Assert.AreEqual(900.0, state.Motion.angVelYaw);
        Assert.AreEqual(901.0, state.Motion.previousAxis.angVelYaw);
        Assert.AreEqual(0, fixture.Worker.DrainOnce());
    }

    [TestMethod]
    public void SourceIdentityIsRegistrationScopedAndStatusWorksBeforeUdpEnable()
    {
        using var fixture = new Fixture();
        var source = fixture.Register();
        Assert.AreEqual(DsState.Reserved, source.Metadata.PadState);
        Assert.IsFalse(source.TryPublish(State(1), true, 75, true, null, Raw));
        Assert.AreEqual(DsState.Connected, source.Metadata.PadState);
        Assert.AreEqual(DsBattery.Charging, source.Metadata.BatteryStatus);
        byte[] address = source.Metadata.PadMacAddress.GetAddressBytes();
        Assert.AreEqual(2, address[0] & 3);
        Assert.IsTrue(fixture.Worker.TryGetMetadata(0, source.Token.Registration.Device, out var meta));
        Assert.AreEqual(DsConnection.Usb, meta.ConnectionType);
        fixture.RestartSession();
        Publish(source, State(2), fixture.Session, Raw);
        Assert.AreEqual(meta.PadMacAddress, source.Metadata.PadMacAddress);
        source.Retire();
        Assert.IsFalse(fixture.Worker.TryGetMetadata(0, source.Token.Registration.Device, out _));
        var successor = fixture.Register(2);
        Assert.AreNotEqual(source.Metadata.PadMacAddress, successor.Metadata.PadMacAddress);
        source.Retire(); // a copied late retirement cannot detach the successor
        Assert.IsTrue(fixture.Worker.TryGetMetadata(0, successor.Token.Registration.Device, out _));
    }

    [TestMethod]
    public void PendingOldSourceAndStoppedSessionNeverRedirectToTheirSuccessors()
    {
        using var fixture = new Fixture();
        var first = fixture.Register();
        Publish(first, State(1), fixture.Session, Raw);
        first.Retire();
        var next = fixture.Register(2);
        Assert.IsFalse(first.TryPublish(State(2), true, 80, false, fixture.Session, Raw));
        Publish(next, State(3), fixture.Session, Raw);
        fixture.RestartSession();
        Assert.AreEqual(0, fixture.Worker.DrainOnce());
        Assert.AreEqual(0, fixture.Observed.Count);
        Publish(next, State(4), fixture.Session, Raw);
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        Assert.AreEqual(4u, fixture.Observed.Single().State.PacketCounter);
        Assert.AreSame(fixture.Session, fixture.Observed.Single().Session);
    }

    [TestMethod]
    public void FilteringAccountsForCoalescedTimeAndResetsAtPolicySessionAndMissingMotion()
    {
        using var fixture = new Fixture();
        var source = fixture.Register();
        var a = State(1);
        Publish(source, a, fixture.Session, Smooth);
        fixture.Worker.DrainOnce();
        var expected = new OneEuroFilter3D();
        expected.axis1Filter.Filter(a.Motion.angVelYaw, 500);
        Publish(source, State(2), fixture.Session, Smooth);
        var c = State(3);
        Publish(source, c, fixture.Session, Smooth);
        fixture.Worker.DrainOnce();
        Assert.AreEqual(expected.axis1Filter.Filter(c.Motion.angVelYaw, 250) * 1.25,
            fixture.Observed.Last().State.Motion.angVelYaw, 1e-10);
        var d = State(4);
        Publish(source, d, fixture.Session, Smooth with { MinCutoff = 0.8 });
        fixture.Worker.DrainOnce();
        Assert.AreEqual(d.Motion.angVelYaw * 1.25, fixture.Observed.Last().State.Motion.angVelYaw);
        fixture.RestartSession();
        Publish(source, State(5), fixture.Session, Smooth);
        fixture.Worker.DrainOnce();
        Assert.AreEqual(5 * 1.25, fixture.Observed.Last().State.Motion.angVelYaw);
        Assert.IsTrue(source.TryPublish(State(6), false, 80, false, fixture.Session, Smooth));
        fixture.Worker.DrainOnce();
        Assert.IsNull(fixture.Observed.Last().State.Motion);
        Publish(source, State(7), fixture.Session, Smooth);
        fixture.Worker.DrainOnce();
        Assert.AreEqual(7 * 1.25, fixture.Observed.Last().State.Motion.angVelYaw);
    }

    [TestMethod]
    public void BlockedOldDispatchCannotHoldProducerRetirementOrUseReplacementStorage()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var delivered = new ConcurrentQueue<(uint Counter, double Yaw, string Address)>();
        using var fixture = new Fixture(background: true, dispatch: (UdpServerSession session,
            ref DualShockPadMeta meta, DS4State state, byte[] packet) =>
        {
            if (state.PacketCounter == 1)
            {
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException();
            }
            delivered.Enqueue((state.PacketCounter, state.Motion.angVelYaw, meta.PadMacAddress.ToString()));
            if (state.PacketCounter == 100) finished.Set();
        });
        var source = fixture.Register();
        var old = State(1);
        try
        {
            Publish(source, old, fixture.Session, Raw);
            Assert.IsTrue(entered.Wait(2000));
            for (uint i = 2; i < 20; i++) Publish(source, State(i), fixture.Session, Raw);
            old.Motion.angVelYaw = 999;
            source.Retire();
            var successor = fixture.Register(2);
            Publish(successor, State(100), fixture.Session, Raw);
            source.Retire();
            Assert.IsFalse(finished.IsSet);
            release.Set();
            Assert.IsTrue(finished.Wait(2000));
            var actual = delivered.ToArray();
            CollectionAssert.AreEqual(new uint[] { 1, 100 }, actual.Select(x => x.Counter).ToArray());
            Assert.AreEqual(1.0, actual[0].Yaw);
            Assert.AreNotEqual(actual[0].Address, actual[1].Address);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public void DispatchFailureIsCountedAndDoesNotPoisonFollowingObservations()
    {
        int calls = 0;
        using var fixture = new Fixture(dispatch: (UdpServerSession session,
            ref DualShockPadMeta meta, DS4State state, byte[] packet) =>
        {
            if (++calls == 1) throw new InvalidOperationException();
        });
        var source = fixture.Register();
        Publish(source, State(1), fixture.Session, Raw);
        Assert.AreEqual(0, fixture.Worker.DrainOnce());
        Publish(source, State(2), fixture.Session, Raw);
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        Assert.AreEqual(1L, fixture.Worker.DispatchFailureCount);
    }

    [TestMethod]
    public void AlreadyAdmittedDispatchRetainsOldSessionAcrossPortRestart()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var actual = new ConcurrentQueue<(uint Counter, UdpServerSession Session, string Address)>();
        using var fixture = new Fixture(background: true, dispatch: (UdpServerSession session,
            ref DualShockPadMeta meta, DS4State state, byte[] packet) =>
        {
            if (state.PacketCounter == 1)
            {
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException();
            }
            actual.Enqueue((state.PacketCounter, session, meta.PadMacAddress.ToString()));
            if (state.PacketCounter == 2) finished.Set();
        });
        var source = fixture.Register();
        UdpServerSession oldSession = fixture.Session;
        try
        {
            Publish(source, State(1), oldSession, Smooth);
            Assert.IsTrue(entered.Wait(2000));
            fixture.RestartSession();
            Assert.IsFalse(oldSession.IsRunning);
            Publish(source, State(2), fixture.Session, Smooth);
            release.Set();
            Assert.IsTrue(finished.Wait(2000));
            var delivered = actual.ToArray();
            Assert.AreEqual(2, delivered.Length);
            Assert.AreSame(oldSession, delivered[0].Session);
            Assert.AreSame(fixture.Session, delivered[1].Session);
            Assert.AreEqual(delivered[0].Address, delivered[1].Address,
                "A port restart does not replace this source's registration identity.");
            Assert.AreEqual(0L, fixture.Worker.DispatchFailureCount);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public void DisposeDoesNotWaitForBlockedDispatchAndClosesWakeAfterItFinishes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var fixture = new Fixture(background: true, dispatch: (UdpServerSession session,
            ref DualShockPadMeta meta, DS4State state, byte[] packet) =>
        {
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException();
            Assert.AreEqual(1u, state.PacketCounter);
            finished.Set();
        });
        var source = fixture.Register();
        try
        {
            Publish(source, State(1), fixture.Session, Raw);
            Assert.IsTrue(entered.Wait(2000));
            Task disposing = Task.Run(fixture.Worker.Dispose);
            Assert.IsTrue(disposing.Wait(1000), "Disposal must not join blocked optional networking.");
            Assert.IsFalse(finished.IsSet);
            Assert.IsFalse(source.TryPublish(State(2), true, 80, false, fixture.Session, Raw));
            release.Set();
            Assert.IsTrue(finished.Wait(2000));
            // Test-only observation of the private consumer, never a join in
            // controller teardown. All outstanding event users have exited.
            var thread = (Thread)typeof(UdpMotionObservationWorker).GetField("thread",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(fixture.Worker);
            Assert.IsTrue(thread.Join(2000));
            Assert.AreEqual(1, typeof(UdpMotionObservationWorker).GetField("eventDisposed",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(fixture.Worker));
            Assert.AreEqual(0L, fixture.Worker.DispatchFailureCount);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public void WarmProducerDoesNotAllocateEvenWhenConsumerIsNotRunning()
    {
        using var fixture = new Fixture();
        var source = fixture.Register();
        var state = State(1);
        bool accepted = true;
        for (int i = 0; i < 2000; i++) accepted &= source.TryPublish(state, true, 80, false, fixture.Session, Raw);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20000; i++) accepted &= source.TryPublish(state, true, 80, false, fixture.Session, Raw);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(accepted);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(21999L, source.CoalescedCount);
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
    }

    [TestMethod]
    public void DisposalRacingActiveProducerClosesAdmissionWithoutSignalUseAfterDispose()
    {
        using var fixture = new Fixture(background: true);
        var source = fixture.Register();
        using var started = new ManualResetEventSlim();
        var publishing = Task.Run(() =>
        {
            var state = State(1);
            started.Set();
            while (source.TryPublish(state, true, 80, false, fixture.Session, Raw)) { }
        });
        Assert.IsTrue(started.Wait(2000));
        fixture.Worker.Dispose();
        Assert.IsTrue(publishing.Wait(2000));
        fixture.Worker.Dispose();
        Assert.IsNull(fixture.Worker.Register(source.Token));
        Assert.IsFalse(source.TryPublish(State(2), true, 80, false, fixture.Session, Raw));
    }

    private static void Publish(UdpMotionObservationWorker.Source source, DS4State state,
        UdpServerSession session, UdpMotionObservationPolicy policy) =>
        Assert.IsTrue(source.TryPublish(state, true, 80, false, session, policy));

    private static DS4State State(uint sequence) => new()
    {
        PacketCounter = sequence, totalMicroSec = sequence * 2000UL, elapsedTime = 0.002,
        Motion = new SixAxis(0, 0, 0, 0, 0, 0, 0.002)
        {
            angVelYaw = sequence, accelXG = sequence,
            previousAxis = new SixAxis(0, 0, 0, 0, 0, 0, 0.002) { angVelYaw = sequence * 2 },
        },
    };

    private sealed record Observation(UdpServerSession Session, DualShockPadMeta Metadata, DS4State State);

    private sealed class Fixture : IDisposable
    {
        internal readonly UdpMotionObservationWorker Worker;
        internal readonly ConcurrentQueue<Observation> Observed = new();
        private readonly UdpServer server = new(static (int slot, ref DualShockPadMeta meta) => { });
        internal UdpServerSession Session => server.CurrentSession;

        internal Fixture(bool background = false, UdpMotionObservationDispatch dispatch = null)
        {
            server.Start(0, "127.0.0.1");
            Worker = new UdpMotionObservationWorker(background, dispatch ?? Capture);
        }

        private void Capture(UdpServerSession session, ref DualShockPadMeta meta, DS4State state, byte[] packet)
        {
            var snapshot = new DS4StateOwnedSnapshot();
            snapshot.Capture(state);
            Observed.Enqueue(new Observation(session, meta, snapshot.State));
        }

        internal UdpMotionObservationWorker.Source Register(ulong generation = 1)
        {
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(generation, generation + 100,
                Switch2Transport.Usb, out var device, out _));
            var owner = new TestOwner(device, generation);
            Assert.IsTrue(InputControllerRegistration.TryCreate(device, generation,
                InputControllerOwnershipKind.Switch2Runtime, false, false, owner, out var registration, out _));
            var table = new InputControllerRegistrationTable(1);
            Assert.IsTrue(table.TryOpen(generation, out _));
            Assert.IsTrue(table.TryReserveAndBind(registration, out var token, out _, out _));
            var source = Worker.Register(token);
            Assert.IsNotNull(source);
            return source;
        }

        internal void RestartSession() { server.Stop(); server.Start(0, "127.0.0.1"); }
        public void Dispose() { Worker.Dispose(); server.Stop(); }
    }

    private sealed class TestOwner(DS4Device device, ulong generation) : IInputControllerRegistrationOwner
    {
        public InputControllerOwnershipKind Kind => InputControllerOwnershipKind.Switch2Runtime;
        public bool Authenticates(DS4Device candidate, ulong candidateGeneration) =>
            ReferenceEquals(device, candidate) && generation == candidateGeneration;
        public bool TryStopAndQuiesce(DS4Device candidate, ulong candidateGeneration, int timeoutMilliseconds,
            out InputControllerOwnerOperationFailure failure)
        { failure = InputControllerOwnerOperationFailure.None; return Authenticates(candidate, candidateGeneration); }
        public bool TryRemove(DS4Device candidate, ulong candidateGeneration,
            out InputControllerOwnerOperationFailure failure)
        { failure = InputControllerOwnerOperationFailure.None; return Authenticates(candidate, candidateGeneration); }
    }
}
