using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

internal enum XboxOneInteropTerminalFailure { None, RejectWrite, DropAcknowledgement, WithholdAcknowledgement }

// Combined-process test composition: the canonical Switch 2 feedback owner and
// encoder are production code; only the final BLE transport lease records bytes.
internal sealed class XboxOneInteropFeedbackTarget : IDisposable
{
    internal const ulong DeviceGeneration = 0x1020304050607080;
    internal const ulong TransportGeneration = 0x8877665544332211;
    private readonly Switch2BluetoothFeedbackLifetime feedback;
    private readonly Switch2VirtualFeedbackSession session;
    private readonly Switch2BluetoothFeedbackLifetimeTests.RecordingLease lease = new(
        Switch2ControllerModel.ProController2, DeviceGeneration, TransportGeneration);
    private readonly object gate = new();
    private readonly List<ControllerFeedbackFrame> frames = new();
    private readonly List<ControllerFeedbackFrame> effects = new();
    private readonly List<Exception> failures = new();
    private ViiperDeviceStream stream;
    private XboxOneFeedbackDeliveryDispatcher dispatcher;
    private Task reader = Task.CompletedTask;
    private volatile bool closing;
    private int acknowledgements;
    private int effectAcknowledgements;
    private int stopAcknowledgements;
    private bool lastDeliveryWasNewEffect;
    private bool lastDeliveryWasStop;
    private ControllerFeedbackFrame? previousFrame;
    private readonly TaskCompletionSource<bool> fourFeedbackAcknowledgements =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool mapImpulse;
    private readonly XboxOneInteropTerminalFailure terminalFailure;
    private int terminalRejections;
    private int droppedAcknowledgements;
    private int withheldAcknowledgements;
    private int dispatcherFaults;
    internal ManualResetEventSlim StopEntered { get; } = new();
    internal ManualResetEventSlim ReleaseStop { get; } = new();
    internal ulong OwnershipEpoch => session.OwnershipEpoch;
    internal int Acknowledgements => Volatile.Read(ref acknowledgements);
    internal int EffectAcknowledgements => Volatile.Read(ref effectAcknowledgements);
    internal int StopAcknowledgements => Volatile.Read(ref stopAcknowledgements);
    internal bool ExpectsTerminalFailure => terminalFailure != XboxOneInteropTerminalFailure.None;
    internal string Diagnostics
    {
        get { lock (gate) return $"frames={frames.Count}, acks={Acknowledgements}, failures={string.Join("\n", failures)}"; }
    }

    internal XboxOneInteropFeedbackTarget(bool mapImpulse = true,
        XboxOneInteropTerminalFailure terminalFailure = XboxOneInteropTerminalFailure.None)
    {
        this.mapImpulse = mapImpulse;
        this.terminalFailure = terminalFailure;
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration, TransportGeneration, out feedback));
        Assert.IsTrue(feedback.TryActivate());
        Assert.IsTrue(feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out session));
    }

    internal void Start(ViiperDeviceStream source)
    {
        stream = source;
        dispatcher = new XboxOneFeedbackDeliveryDispatcher(Deliver,
            (correlation, accepted) =>
            {
                bool expectedRejection = lastDeliveryWasStop &&
                    terminalFailure == XboxOneInteropTerminalFailure.RejectWrite;
                Assert.AreEqual(!expectedRejection, accepted,
                    "The semantic ACK must report actual canonical physical delivery.");
                if (lastDeliveryWasStop && terminalFailure == XboxOneInteropTerminalFailure.DropAcknowledgement)
                {
                    Interlocked.Increment(ref droppedAcknowledgements);
                    throw new IOException("Injected test-only ACK write failure after actual neutral delivery.");
                }
                if (lastDeliveryWasStop && terminalFailure == XboxOneInteropTerminalFailure.WithholdAcknowledgement)
                {
                    Assert.IsFalse(source.IsTransportClosed);
                    Interlocked.Increment(ref withheldAcknowledgements);
                    return; // Keep the socket open: the real server deadline must resolve removal.
                }
                source.AcknowledgeXboxOneFeedback(correlation, accepted);
                Interlocked.Increment(ref acknowledgements);
                if (lastDeliveryWasStop && accepted) Interlocked.Increment(ref stopAcknowledgements);
                if (expectedRejection) Interlocked.Increment(ref terminalRejections);
                if (lastDeliveryWasNewEffect && Interlocked.Increment(ref effectAcknowledgements) == 4)
                    fourFeedbackAcknowledgements.TrySetResult(true);
            }, () =>
            {
                Interlocked.Increment(ref dispatcherFaults);
                if (!ExpectsTerminalFailure || !lastDeliveryWasStop)
                    lock (gate) failures.Add(new IOException("The production feedback dispatcher faulted."));
                source.CloseTransport();
            });
        reader = Task.Run(() =>
        {
            byte[] payload = new byte[ControllerFeedbackFrame.SerializedLength];
            try
            {
                while (!source.IsTransportClosed)
                {
                    int length = source.ReadXboxOneBrokerFrame(out byte kind, out ulong correlation, payload);
                    if (kind == ViiperDeviceStream.XboxOneBrokerSemanticInputAck)
                        source.AcceptXboxOneInputAck(correlation, payload[0]);
                    else
                    {
                        Assert.AreEqual(ViiperDeviceStream.XboxOneBrokerCanonicalFeedback, kind);
                        Assert.AreEqual(ControllerFeedbackFrame.SerializedLength, length);
                        Assert.IsTrue(dispatcher.TryEnqueue(payload.AsSpan(0, length), correlation));
                    }
                }
            }
            catch (Exception error) when (closing && error is IOException or ObjectDisposedException) { }
            catch (Exception error)
            {
                lock (gate) failures.Add(error);
                source.CloseTransport();
            }
        });
    }

    private bool Deliver(byte[] wire, int length)
    {
        try { return DeliverCore(wire, length); }
        catch (Exception error)
        {
            lock (gate) failures.Add(error);
            throw;
        }
    }

    private bool DeliverCore(byte[] wire, int length)
    {
        Assert.IsTrue(ControllerFeedbackFrame.TryReadFrom(wire.AsSpan(0, length), out var frame));
        bool nonzero = frame.BodyLow != 0 || frame.BodyHigh != 0 || frame.LeftTrigger != 0 || frame.RightTrigger != 0;
        bool refresh = previousFrame is { } previous && previous.Command == frame.Command &&
            previous.Actuators == frame.Actuators && previous.BodyLow == frame.BodyLow &&
            previous.BodyHigh == frame.BodyHigh && previous.LeftTrigger == frame.LeftTrigger &&
            previous.RightTrigger == frame.RightTrigger;
        lastDeliveryWasNewEffect = nonzero && !frame.IsStop && !refresh;
        lastDeliveryWasStop = frame.IsStop;
        if (frame.IsStop)
        {
            StopEntered.Set();
            Assert.IsTrue(ReleaseStop.Wait(TimeSpan.FromSeconds(5)), "Test did not release the exact terminal delivery.");
        }
        int before = lease.PayloadCount;
        if (frame.IsStop && terminalFailure == XboxOneInteropTerminalFailure.RejectWrite)
        {
            // Reject through the existing physical lease, not a substituted
            // canonical return value. No successful physical Stop is claimed.
            lease.RejectWrites = true;
            Assert.IsFalse(session.TryPublish(wire.AsSpan(0, length), mapImpulseTriggersToHdRumble: mapImpulse));
            Assert.AreEqual(before, lease.PayloadCount);
            lock (gate) frames.Add(frame);
            previousFrame = frame;
            return false;
        }
        Assert.IsTrue(session.TryPublish(wire.AsSpan(0, length), mapImpulseTriggersToHdRumble: mapImpulse));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(lease.LastPayload,
            out _, out var left, out var right, out _));
        if (lastDeliveryWasNewEffect || frame.IsStop)
            Assert.IsTrue(lease.PayloadCount > before,
                $"No new HD write: command={frame.Command}, sequence={frame.Sequence}, body={frame.BodyLow}/{frame.BodyHigh}, impulse={frame.LeftTrigger}/{frame.RightTrigger}, existingAmplitude={HasAmplitude(left) || HasAmplitude(right)}.");
        bool expectedAmplitude = !frame.IsStop && (frame.BodyLow != 0 || frame.BodyHigh != 0 ||
            mapImpulse && (frame.LeftTrigger != 0 || frame.RightTrigger != 0));
        // Timed ordinary neutral may use the existing impulse-release envelope.
        // A terminal Stop may not. Refreshes still have to preserve the selected
        // amplitude policy, even if the physical sink deduplicates its write.
        if (nonzero || frame.IsStop)
            Assert.AreEqual(expectedAmplitude, HasAmplitude(left) || HasAmplitude(right),
                "The profile impulse-conversion choice must govern encoded output, not the canonical channel values.");
        lock (gate)
        {
            frames.Add(frame);
            if (lastDeliveryWasNewEffect) effects.Add(frame);
        }
        previousFrame = frame;
        return true;
    }

    internal async Task AssertFourActuatorsAsync()
    {
        // USB OUT completion proves packet acceptance, not that the broker
        // reader has already received its later feedback. An idle dispatcher
        // alone cannot establish completion of an as-yet-unreceived successor.
        await fourFeedbackAcknowledgements.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(dispatcher.WaitForIdle(1_000));
        lock (gate)
        {
            Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
            Assert.AreEqual(4, effects.Count);
            var values = effects.Select(frame => new[] { frame.BodyLow, frame.BodyHigh, frame.LeftTrigger, frame.RightTrigger }).ToArray();
            for (int index = 0; index < 4; index++)
            {
                for (int channel = 0; channel < 4; channel++)
                    Assert.AreEqual(index == channel, values[index][channel] > 0,
                        "The real GIP motor channel must survive the canonical wire without remapping.");
                Assert.IsFalse(effects[index].IsStop);
                if (index != 0) Assert.IsTrue(values[index][index] > values[index - 1][index - 1]);
            }
        }
        Assert.AreEqual(4, EffectAcknowledgements);
        Assert.AreEqual(0, StopAcknowledgements);
    }

    internal void BeginClose() => closing = true;

    internal async Task AssertTerminalFailureAsync()
    {
        Assert.IsTrue(ExpectsTerminalFailure);
        Assert.IsTrue(dispatcher.WaitForIdle(1_000));
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        lock (gate)
        {
            Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
            Assert.AreEqual(4, effects.Count);
            Assert.AreEqual(1, frames.Count(frame => frame.IsStop));
            Assert.IsTrue(frames[^1].IsStop);
        }
        Assert.AreEqual(terminalFailure == XboxOneInteropTerminalFailure.WithholdAcknowledgement ? 0 : 1,
            Volatile.Read(ref dispatcherFaults));
        Assert.AreEqual(4, EffectAcknowledgements);
        Assert.AreEqual(0, StopAcknowledgements);
        Assert.AreEqual(terminalFailure == XboxOneInteropTerminalFailure.RejectWrite ? 1 : 0,
            Volatile.Read(ref terminalRejections));
        Assert.AreEqual(terminalFailure == XboxOneInteropTerminalFailure.DropAcknowledgement ? 1 : 0,
            Volatile.Read(ref droppedAcknowledgements));
        Assert.AreEqual(terminalFailure == XboxOneInteropTerminalFailure.WithholdAcknowledgement ? 1 : 0,
            Volatile.Read(ref withheldAcknowledgements));
        Assert.IsTrue(stream.IsTransportClosed);

        // If the physical lease becomes writable again, its existing owner
        // must still be able to deliver local terminal neutral and retire.
        // That recovery does not retroactively acknowledge the failed broker Stop.
        lease.RejectWrites = false;
        Assert.IsTrue(feedback.TryStopAndRetire(3));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(lease.LastPayload,
            out _, out var left, out var right, out _));
        Assert.IsFalse(HasAmplitude(left) || HasAmplitude(right));
        int count = lease.PayloadCount;
        await Task.Delay(120);
        Assert.AreEqual(count, lease.PayloadCount, "No effect may return after local retirement.");
    }

    internal async Task AssertTerminalAsync()
    {
        Assert.IsTrue(dispatcher.WaitForIdle(1_000));
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        lock (gate)
        {
            Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
            Assert.AreEqual(4, effects.Count);
            Assert.AreEqual(1, frames.Count(frame => frame.IsStop));
            Assert.IsTrue(frames[^1].IsStop);
        }
        Assert.AreEqual(4, EffectAcknowledgements);
        Assert.AreEqual(1, StopAcknowledgements);
        int count = lease.PayloadCount;
        // Cover the existing 90 ms impulse-release timer after terminal Stop.
        await Task.Delay(120);
        Assert.AreEqual(count, lease.PayloadCount, "No delayed impulse release may escape terminal Stop.");
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(lease.LastPayload,
            out _, out var left, out var right, out _));
        Assert.IsFalse(HasAmplitude(left) || HasAmplitude(right));
    }

    private static bool HasAmplitude(Switch2HdRumbleGroup group) =>
        group.First.HasNonzeroAmplitude || group.Second.HasNonzeroAmplitude || group.Third.HasNonzeroAmplitude;

    public void Dispose()
    {
        closing = true;
        ReleaseStop.Set();
        stream?.CloseTransport();
        try { reader.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        finally
        {
            dispatcher?.Dispose();
            lease.RejectWrites = false;
            session.TryRetire();
            feedback.TryStopAndRetire(3);
            StopEntered.Dispose();
            ReleaseStop.Dispose();
        }
    }
}
