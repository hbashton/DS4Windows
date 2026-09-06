using System;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Threading;

namespace DS4Windows;

internal readonly record struct UdpMotionObservationPolicy(
    bool Smooth, double MinCutoff, double Beta, int YawSensitivity);

internal delegate void UdpMotionObservationDispatch(UdpServerSession session,
    ref DualShockPadMeta metadata, DS4State state, byte[] packet);

/// <summary>
/// Optional DSU observation only; never a canonical input queue. Each exact
/// registration has a single producer and a three-buffer latest-value mailbox.
/// A single consumer owns filtering, formatting and networking. Producer and
/// consumer exchange ownership, never wait for each other or share a writable
/// snapshot. Under overload intermediate DSU observations are counted/coalesced.
/// </summary>
internal sealed class UdpMotionObservationWorker : IDisposable
{
    private readonly Source[] sources = new Source[UdpServer.NUMBER_SLOTS];
    private readonly AutoResetEvent wake = new(false);
    private readonly UdpMotionObservationDispatch dispatch;
    private readonly Thread thread;
    private int closed, publishers, workerExited, eventDisposed;
    private long dispatchFailures;

    internal UdpMotionObservationWorker(bool startWorker = true,
        UdpMotionObservationDispatch dispatch = null)
    {
        this.dispatch = dispatch ?? (static (UdpServerSession session,
            ref DualShockPadMeta metadata, DS4State state, byte[] packet) =>
                session.NewReportIncoming(ref metadata, state, packet));
        if (startWorker)
        {
            thread = new Thread(Run) { IsBackground = true, Name = "DSU motion observations" };
            thread.Start();
        }
    }

    internal long DispatchFailureCount => Interlocked.Read(ref dispatchFailures);

    // Called only by the authenticated cold host stage. Retaining the full
    // token here does not grant admission: only that host may borrow a report.
    internal Source Register(in InputControllerSlotToken token)
    {
        if (!token.IsValid || (uint)token.Slot >= sources.Length ||
            Volatile.Read(ref closed) != 0)
            return null;
        var source = new Source(this, token);
        if (Interlocked.CompareExchange(ref sources[token.Slot], source, null) != null)
            return null;
        // Dispose can race cold registration; do not leave a published orphan.
        if (Volatile.Read(ref closed) != 0)
        {
            source.Retire();
            return null;
        }
        return source;
    }

    internal bool TryGetMetadata(int slot, DS4Device device, out DualShockPadMeta metadata)
    {
        Source source = (uint)slot < sources.Length ? Volatile.Read(ref sources[slot]) : null;
        if (source != null && ReferenceEquals(source.Token.Registration.Device, device) &&
            !source.IsRetired)
        {
            metadata = source.Metadata;
            return true;
        }
        metadata = default;
        return false;
    }

    private void Run()
    {
        try
        {
            while (Volatile.Read(ref closed) == 0)
            {
                wake.WaitOne();
                if (Volatile.Read(ref closed) == 0)
                    DrainCore();
            }
        }
        finally
        {
            Volatile.Write(ref workerExited, 1);
            TryDisposeEvent();
        }
    }

    // Deterministic scheduling seam. Production has exactly one background
    // consumer; tests may manually drain only a worker created without it.
    internal int DrainOnce()
    {
        if (thread != null)
            throw new InvalidOperationException("The background worker owns this consumer.");
        return Volatile.Read(ref closed) == 0 ? DrainCore() : 0;
    }

    private int DrainCore()
    {
        int count = 0;
        for (int slot = 0; slot < sources.Length && Volatile.Read(ref closed) == 0; slot++)
        {
            Source source = Volatile.Read(ref sources[slot]);
            if (source == null || !source.TryTake(out Source.Buffer buffer))
                continue;
            // This is admission, not a claim that an already accepted UDP
            // datagram can be recalled. Retirement never waits for dispatch.
            if (source.IsRetired || !buffer.Session.IsRunning)
                continue;
            try
            {
                source.Filter(buffer);
                dispatch(buffer.Session, ref buffer.Metadata, buffer.Snapshot.State, source.Packet);
                count++;
            }
            catch
            {
                Interlocked.Increment(ref dispatchFailures);
            }
        }
        return count;
    }

    public void Dispose()
    {
        // A publisher increments before reading closed. The final signal and
        // all admitted producer signals finish before the event is disposed.
        Interlocked.Increment(ref publishers);
        try
        {
            if (Interlocked.Exchange(ref closed, 1) != 0)
                return;
            for (int slot = 0; slot < sources.Length; slot++)
                Interlocked.Exchange(ref sources[slot], null)?.Retire();
            wake.Set();
            if (thread == null)
                Volatile.Write(ref workerExited, 1);
        }
        finally { EndPublish(); }
        // No join: optional network work must not hold controller teardown.
        // An admitted dispatch retains only its old source/session until done.
    }

    private void EndPublish()
    {
        Interlocked.Decrement(ref publishers);
        if (Volatile.Read(ref closed) != 0)
            TryDisposeEvent();
    }

    private void TryDisposeEvent()
    {
        if (Volatile.Read(ref closed) != 0 && Volatile.Read(ref workerExited) != 0 &&
            Volatile.Read(ref publishers) == 0 && Interlocked.Exchange(ref eventDisposed, 1) == 0)
            wake.Dispose();
    }

    internal sealed class Source
    {
        internal sealed class Buffer
        {
            internal readonly DS4StateOwnedSnapshot Snapshot = new();
            internal UdpServerSession Session;
            internal UdpMotionObservationPolicy Policy;
            internal DualShockPadMeta Metadata;
        }

        private const int Dirty = 4, IndexMask = 3;
        private readonly UdpMotionObservationWorker owner;
        private readonly Buffer[] buffers = { new(), new(), new() };
        private readonly PhysicalAddress address;
        private readonly DsConnection connection;
        private int writeIndex, readIndex = 1, middle = 2, retired, status;
        private long coalesced;
        private UdpServerSession filterSession;
        private UdpMotionObservationPolicy filterPolicy;
        private OneEuroFilter3D accel, gyro;
        private ulong previousMicroseconds;
        internal readonly byte[] Packet = new byte[UdpServer.DATA_RSP_PACKET_LEN];

        internal Source(UdpMotionObservationWorker owner, in InputControllerSlotToken token)
        {
            this.owner = owner;
            Token = token;
            byte[] bytes = RandomNumberGenerator.GetBytes(6);
            bytes[0] = (byte)((bytes[0] | 2) & 0xfe); // local, unicast; not a hardware address
            address = new PhysicalAddress(bytes);
            connection = token.Registration.Device.getConnectionType() == ConnectionType.USB ?
                DsConnection.Usb : DsConnection.Bluetooth;
        }

        internal InputControllerSlotToken Token { get; }
        internal bool IsRetired => Volatile.Read(ref retired) != 0;
        internal long CoalescedCount => Interlocked.Read(ref coalesced);
        internal DualShockPadMeta Metadata
        {
            get
            {
                int current = Volatile.Read(ref status);
                return new DualShockPadMeta
                {
                    PadId = (byte)Token.Slot, PadMacAddress = address,
                    ConnectionType = connection, Model = DsModel.DS4,
                    PadState = IsRetired ? DsState.Disconnected : current == 0 ? DsState.Reserved : DsState.Connected,
                    IsActive = !IsRetired && current != 0, BatteryStatus = (DsBattery)(current & 255),
                };
            }
        }

        internal bool TryPublish(DS4State state, bool hasMotion, int batteryPercentage, bool charging,
            UdpServerSession session, in UdpMotionObservationPolicy policy)
        {
            // Host guarantees a single, already admitted producer for this
            // exact registration and keeps the physical state borrowed here.
            Interlocked.Increment(ref owner.publishers);
            try
            {
                if (IsRetired || Volatile.Read(ref owner.closed) != 0)
                    return false;
                Volatile.Write(ref status, 256 | (byte)Battery(batteryPercentage, charging));
                if (session == null || !session.IsRunning)
                    return false;
                Buffer buffer = buffers[writeIndex];
                buffer.Snapshot.Capture(state);
                if (!hasMotion) buffer.Snapshot.State.Motion = null;
                buffer.Session = session;
                buffer.Policy = policy;
                buffer.Metadata = Metadata;
                if (IsRetired) return false;
                int previous = Interlocked.Exchange(ref middle, writeIndex | Dirty);
                writeIndex = previous & IndexMask;
                if ((previous & Dirty) != 0) Interlocked.Increment(ref coalesced);
                owner.wake.Set();
                return true;
            }
            finally { owner.EndPublish(); }
        }

        internal void Retire()
        {
            Interlocked.Exchange(ref retired, 1);
            // Detach this handle only. Never clear a successor's pending data.
            Interlocked.CompareExchange(ref owner.sources[Token.Slot], null, this);
        }

        internal bool TryTake(out Buffer buffer)
        {
            buffer = null;
            if (IsRetired || (Volatile.Read(ref middle) & Dirty) == 0)
                return false;
            int claimed = Interlocked.Exchange(ref middle, readIndex);
            readIndex = claimed & IndexMask;
            buffer = buffers[readIndex];
            return true;
        }

        internal void Filter(Buffer buffer)
        {
            DS4State state = buffer.Snapshot.State;
            SixAxis motion = state.Motion;
            if (motion == null)
            {
                // There is no sample to advance the filter with. Re-prime on
                // resume instead of using the absent report's timestamp.
                filterSession = null;
                previousMicroseconds = 0;
                return;
            }
            UdpMotionObservationPolicy policy = buffer.Policy;
            if (!ReferenceEquals(filterSession, buffer.Session) || filterPolicy != policy)
            {
                // Consumer-owned cold transitions, never a UI mutation of a
                // filter being used by the physical report thread.
                filterSession = buffer.Session;
                filterPolicy = policy;
                previousMicroseconds = 0;
                accel = new OneEuroFilter3D(); gyro = new OneEuroFilter3D();
                double cutoff = double.IsFinite(policy.MinCutoff) && policy.MinCutoff > 0 ? policy.MinCutoff : 0.4;
                double beta = double.IsFinite(policy.Beta) && policy.Beta >= 0 ? policy.Beta : 0.2;
                accel.SetFilterAttrs(cutoff, beta); gyro.SetFilterAttrs(cutoff, beta);
            }
            // Latest-value coalescing changes the interval between observed
            // samples. Use controller time, not worker scheduling time.
            double elapsed = previousMicroseconds != 0 && state.totalMicroSec > previousMicroseconds ?
                (state.totalMicroSec - previousMicroseconds) / 1_000_000.0 : state.elapsedTime;
            previousMicroseconds = state.totalMicroSec;
            if (policy.Smooth && double.IsFinite(elapsed) && elapsed > 0)
            {
                double rate = 1.0 / elapsed;
                motion.accelXG = accel.axis1Filter.Filter(motion.accelXG, rate);
                motion.accelYG = accel.axis2Filter.Filter(motion.accelYG, rate);
                motion.accelZG = accel.axis3Filter.Filter(motion.accelZG, rate);
                motion.angVelYaw = gyro.axis1Filter.Filter(motion.angVelYaw, rate);
                motion.angVelPitch = gyro.axis2Filter.Filter(motion.angVelPitch, rate);
                motion.angVelRoll = gyro.axis3Filter.Filter(motion.angVelRoll, rate);
            }
            motion.angVelYaw = Switch2.Switch2CemuhookYawSensitivity.ApplyYaw(
                motion.angVelYaw, policy.YawSensitivity);
        }

        private static DsBattery Battery(int percentage, bool charging) => charging ?
            percentage >= 100 ? DsBattery.Charged : DsBattery.Charging :
            percentage >= 95 ? DsBattery.Full : percentage >= 70 ? DsBattery.High :
            percentage >= 50 ? DsBattery.Medium : percentage >= 20 ? DsBattery.Low :
            percentage >= 5 ? DsBattery.Dying : DsBattery.None;
    }
}
