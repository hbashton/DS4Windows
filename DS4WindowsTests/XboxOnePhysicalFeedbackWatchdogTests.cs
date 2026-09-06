using DS4Windows;
using DS4Windows.InputDevices;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests;

[TestClass]
public sealed class XboxOnePhysicalFeedbackWatchdogTests
{
    [TestMethod]
    public void LivePolicyIsReadAfterPublishingCaptureIdentityAndNewFrameCanResume()
    {
        var clock = new ManualClock();
        bool enabled = false;
        ulong expectedSequence = 1;
        var writes = new List<bool>();
        XboxOnePhysicalFeedbackSession session = null;
        Assert.IsTrue(XboxOnePhysicalFeedbackSession.TryCreateOwned(Binding(), new TestDevice(),
            (state, _) => { writes.Add(state.IsNeutral); return true; }, out session, clock,
            isOutputEnabled: () =>
            {
                Assert.IsTrue(session.TryCaptureOutputPolicySequence(out ulong sequence));
                Assert.AreEqual(expectedSequence, sequence);
                return enabled;
            }));
        try
        {
            Assert.IsTrue(session.TryPublish(Frame(1)));
            CollectionAssert.AreEqual(new[] { true }, writes.ToArray());
            enabled = true;
            Assert.IsTrue(session.TrySuppressCurrentOutput(1));
            Assert.IsTrue(writes[^1]);
            expectedSequence = 2;
            Assert.IsTrue(session.TryPublish(Frame(2)));
            Assert.IsFalse(writes[^1], "A new identical canonical state must repaint after local suppression.");
        }
        finally { session.TryRetire(); }
    }

    [TestMethod]
    public void SuppressionKeepsAbsoluteExpiryAndNeutralRemainsRetryable()
    {
        var clock = new ManualClock();
        bool reject = false;
        var writes = new List<bool>();
        var session = Create(clock, (state, _) => { writes.Add(state.IsNeutral); return !reject; });
        try
        {
            Assert.IsTrue(session.TryPublish(Frame(1)));
            clock.Advance(500);
            reject = true;
            Assert.IsFalse(session.TrySuppressCurrentOutput(1));
            Assert.IsTrue(writes[^1]);
            reject = false;
            Assert.IsTrue(session.TrySuppressCurrentOutput(1));
            Assert.IsTrue(writes[^1]);
            Assert.IsTrue(session.TryCaptureOutputPolicySequence(out ulong sequence));
            Assert.AreEqual(1UL, sequence, "Local policy is not a new broker publication.");
            int beforeExpiry = writes.Count;
            clock.Advance(500);
            Assert.AreEqual(beforeExpiry + 1, writes.Count, "Policy refresh must not extend the original deadline.");
            Assert.IsTrue(writes[^1]);
            Assert.IsTrue(session.TryPublish(Frame(2, timestamp: clock.Now)));
            Assert.IsFalse(writes[^1]);
            int beforeStale = writes.Count;
            Assert.IsTrue(session.TrySuppressCurrentOutput(1));
            Assert.AreEqual(beforeStale, writes.Count);
        }
        finally { session.TryRetire(); }
    }

    [TestMethod]
    public async Task PolicyCaptureDoesNotWaitForThePhysicalStateSetter()
    {
        var clock = new ManualClock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var session = Create(clock, (state, _) =>
        {
            if (!state.IsNeutral)
            {
                entered.Set();
                Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(3)));
            }
            return true;
        });
        Task<bool> publication = Task.Run(() => session.TryPublish(Frame(1)));
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
            var capture = Task.Run(() =>
            {
                Assert.IsTrue(session.TryCaptureOutputPolicySequence(out ulong sequence));
                return sequence;
            });
            Assert.AreEqual(1UL, await capture.WaitAsync(TimeSpan.FromSeconds(1)));
            release.Set();
            Assert.IsTrue(await publication.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsTrue(session.TrySuppressCurrentOutput(1));
        }
        finally
        {
            release.Set();
            await publication;
            session.TryRetire();
        }
    }

    [TestMethod]
    public void AbsoluteExpiryClearsAllActuatorsAndReleasesTriggerOwnership()
    {
        var clock = new ManualClock();
        var writes = new List<(ControllerFeedbackActuatorState State, bool Release)>();
        var session = Create(clock, (state, release) =>
        {
            writes.Add((state, release));
            return true;
        });

        Assert.IsTrue(session.TryPublish(Frame(1)));
        Assert.AreEqual(1, writes.Count);
        Assert.IsFalse(writes[0].State.IsNeutral);
        Assert.IsFalse(writes[0].Release);
        clock.Advance(999);
        Assert.AreEqual(1, writes.Count);
        clock.Advance(1);
        Assert.AreEqual(2, writes.Count);
        Assert.IsTrue(writes[1].State.IsNeutral);
        Assert.IsTrue(writes[1].Release);
        clock.Advance(1_000_000);
        Assert.AreEqual(2, writes.Count, "Expiry is a one-shot wake, not a poll loop.");
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void SameEffectRenewalExtendsOnlyToItsAbsoluteDeadline()
    {
        var clock = new ManualClock();
        var writes = new List<bool>();
        var session = Create(clock, (state, _) =>
        {
            writes.Add(state.IsNeutral);
            return true;
        });
        Assert.IsTrue(session.TryPublish(Frame(1)));
        clock.Advance(500);
        Assert.IsTrue(session.TryPublish(Frame(2, timestamp: 1_500)));
        Assert.AreEqual(1, writes.Count, "Identical state renewals reuse canonical deduplication.");
        clock.Timer.FireQueued();
        Assert.AreEqual(1, writes.Count, "A queued predecessor wake cannot expire its renewal.");
        Assert.IsFalse(session.TryPublish(Frame(2, timestamp: 1_500, ttl: 250_000)));
        clock.Advance(999);
        Assert.AreEqual(1, writes.Count);
        clock.Advance(1);
        CollectionAssert.AreEqual(new[] { false, true }, writes.ToArray());
        Assert.IsTrue(session.TryPublish(Frame(3, timestamp: 2_500)));
        Assert.AreEqual(false, writes[^1], "Expiry releases output, not the broker's sequence lifetime.");
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void SlowStateAcceptancePastExpiryIsNeutralizedBeforeReturning()
    {
        var clock = new ManualClock();
        var writes = new List<bool>();
        var session = Create(clock, (state, _) =>
        {
            writes.Add(state.IsNeutral);
            if (!state.IsNeutral)
            {
                clock.AdvanceWithoutCallbacks(1_100);
            }
            return true;
        });
        Assert.IsTrue(session.TryPublish(Frame(1)));
        CollectionAssert.AreEqual(new[] { false, true }, writes.ToArray());
        Assert.IsNull(clock.Timer, "An already-expired acceptance must not get a fresh TTL.");
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void StopAndRetirementFenceEveryQueuedTimerCallback()
    {
        var clock = new ManualClock();
        int writes = 0;
        var session = Create(clock, (_, _) => { writes++; return true; });
        Assert.IsTrue(session.TryPublish(Frame(1)));
        ManualTimer queuedTimer = clock.Timer;
        Assert.IsTrue(session.TryPublish(Frame(2, command: ControllerFeedbackCommand.Stop)));
        Assert.AreEqual(2, writes);
        Assert.IsTrue(queuedTimer.Disposed);
        queuedTimer.FireQueued();
        clock.Advance(1_000_000);
        Assert.AreEqual(2, writes);
        Assert.IsFalse(session.TryPublish(Frame(3, timestamp: clock.Now)));
        Assert.IsTrue(session.TryRetire());
        Assert.AreEqual(2, writes);
    }

    [TestMethod]
    public void UnpublishedStartupFailureDoesNotNeutralizeAnotherOwner()
    {
        var clock = new ManualClock();
        int writes = 0;
        var session = Create(clock, (_, _) => { writes++; return true; });
        Assert.IsTrue(session.TryRetire());
        Assert.IsTrue(session.TryRetire());
        Assert.AreEqual(0, writes);
        Assert.IsNull(clock.Timer);
    }

    [TestMethod]
    public void FailedApplyNeverRetriesNonNeutralAfterRejection()
    {
        var clock = new ManualClock();
        var writes = new List<bool>();
        int faults = 0;
        var session = Create(clock, (state, _) =>
        {
            writes.Add(state.IsNeutral);
            return state.IsNeutral;
        }, () => faults++);
        Assert.IsFalse(session.TryPublish(Frame(1)));
        CollectionAssert.AreEqual(new[] { false, true }, writes.ToArray());
        Assert.AreEqual(1, faults);
        Assert.IsTrue(session.TryRetire());
        Assert.IsFalse(session.TryPublish(Frame(2)));
        clock.Advance(1_000_000);
        Assert.AreEqual(2, writes.Count);
    }

    [TestMethod]
    public void FailedExpiryNeutralIsContainedAndSynchronouslyRetryable()
    {
        var clock = new ManualClock();
        bool rejectNeutral = true;
        int faults = 0;
        var writes = new List<bool>();
        var session = Create(clock, (state, _) =>
        {
            writes.Add(state.IsNeutral);
            if (state.IsNeutral && rejectNeutral)
            {
                throw new IOException("Deterministic physical state-setter failure.");
            }
            return true;
        }, () => { faults++; throw new IOException("Diagnostic failure."); });
        Assert.IsTrue(session.TryPublish(Frame(1)));
        clock.Advance(1_000);
        Assert.AreEqual(1, faults);
        int fencedCount = writes.Count;
        clock.Timer.FireQueued();
        clock.Advance(1_000_000);
        Assert.AreEqual(fencedCount, writes.Count, "No automatic write can outlive a failed retirement.");
        Assert.IsFalse(session.TryPublish(Frame(2, timestamp: clock.Now)));
        rejectNeutral = false;
        Assert.IsTrue(session.TryRetire(), "The same terminal neutral obligation remains retryable.");
        Assert.AreEqual(fencedCount + 1, writes.Count);
        Assert.IsTrue(writes[^1]);
        Assert.AreEqual(1, faults);
    }

    [TestMethod]
    public void ClockRegressionFencesAndNeutralizesInsteadOfRenewingStaleOutput()
    {
        var clock = new ManualClock();
        var writes = new List<bool>();
        var session = Create(clock, (state, _) => { writes.Add(state.IsNeutral); return true; });
        Assert.IsTrue(session.TryPublish(Frame(1)));
        clock.Now = 999;
        clock.Timer.FireQueued();
        CollectionAssert.AreEqual(new[] { false, true }, writes.ToArray());
        clock.Now = 1_100;
        Assert.IsFalse(session.TryPublish(Frame(2)));
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void TimerCreationFailureCannotLeaveAnUnboundedAcceptedApply()
    {
        var clock = new ManualClock { FailCreateTimer = true };
        var writes = new List<bool>();
        var session = Create(clock, (state, _) => { writes.Add(state.IsNeutral); return true; });
        Assert.IsFalse(session.TryPublish(Frame(1)));
        CollectionAssert.AreEqual(new[] { false, true }, writes.ToArray());
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void TimerRearmFailureFencesRenewalAndNeutralizesItsAcceptedState()
    {
        var clock = new ManualClock();
        var writes = new List<bool>();
        var session = Create(clock, (state, _) => { writes.Add(state.IsNeutral); return true; });
        Assert.IsTrue(session.TryPublish(Frame(1)));
        clock.Timer.FailChange = true;
        clock.AdvanceWithoutCallbacks(500);
        Assert.IsFalse(session.TryPublish(Frame(2, timestamp: clock.Now)));
        CollectionAssert.AreEqual(new[] { false, true }, writes.ToArray());
        clock.Timer.FireQueued();
        Assert.AreEqual(2, writes.Count);
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void NeutralSuppressionStillExpiresAndReleasesOwnership()
    {
        var clock = new ManualClock();
        var releases = new List<bool>();
        var session = Create(clock, (state, release) =>
        {
            Assert.IsTrue(state.IsNeutral);
            releases.Add(release);
            return true;
        });
        Assert.IsTrue(session.TryPublish(Frame(1, command: ControllerFeedbackCommand.Neutral)));
        clock.Advance(1_000);
        CollectionAssert.AreEqual(new[] { false, true }, releases.ToArray());
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void ShorterNewDeadlineIsNotExtendedByAnOlderTimer()
    {
        var clock = new ManualClock();
        int neutrals = 0;
        var session = Create(clock, (state, _) => { if (state.IsNeutral) neutrals++; return true; });
        Assert.IsTrue(session.TryPublish(Frame(1, ttl: 10_000)));
        clock.Advance(100);
        Assert.IsTrue(session.TryPublish(Frame(2, timestamp: clock.Now, ttl: 50)));
        clock.Advance(49);
        Assert.AreEqual(0, neutrals);
        clock.Advance(1);
        Assert.AreEqual(1, neutrals);
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void InvalidBindingsExpiredFramesAndCodecBypassDoNotAdvanceOwnership()
    {
        var clock = new ManualClock();
        int writes = 0;
        var session = Create(clock, (_, _) => { writes++; return true; });
        Assert.IsFalse(session.TryPublish(Frame(1, generation: 99)));
        Assert.IsFalse(session.TryPublish(Frame(1, transport: 99)));
        Assert.IsFalse(session.TryPublish(Frame(1, epoch: 99)));
        Assert.IsFalse(session.TryPublish(Frame(1, timestamp: 0, ttl: 1)));
        Assert.IsFalse(session.TryAccept(Frame(1), clock.Now, out _, out _));
        Assert.AreEqual(0, writes);
        Assert.IsNull(clock.Timer);
        Assert.IsTrue(session.TryPublish(Frame(1)));
        Assert.IsFalse(session.TryPublish(Frame(1)));
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    [DoNotParallelize]
    public void ProductionOwnerExpiryAndOldReaderRetirementCannotTouchSuccessor()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        ControlService previousHub = DS4Windows.Program.rootHub;
        byte previousBoost = Global.RumbleBoost[0];
        XboxOnePhysicalFeedbackSession predecessor = null;
        XboxOnePhysicalFeedbackSession successor = null;
        try
        {
            var clock = new ManualClock();
            var target = new TestDevice();
            var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            hub.DS4Controllers = new DS4Device[] { target };
            DS4Windows.Program.rootHub = hub;
            Global.RumbleBoost[0] = 100;
            var output = new ViiperOutDevice(OutContType.ViiperXboxOne, ViiperVirtualDeviceType.XboxOne);
            typeof(ViiperOutDevice).GetField("lastInputDeviceIndex", fields).SetValue(output, 0);
            FieldInfo ownerField = typeof(ViiperOutDevice).GetField("xboxOnePhysicalFeedbackSession", fields);
            Assert.IsTrue(output.TryCreateXboxOnePhysicalFeedbackSession(Binding(), target, 0, out predecessor, clock));
            ownerField.SetValue(output, predecessor);
            Assert.IsTrue(predecessor.TryPublish(Frame(1)));
            Assert.IsTrue(target.Heavy != 0 && target.Light != 0);
            clock.Advance(1_000);
            Assert.AreEqual((byte)0, target.Heavy);
            Assert.AreEqual((byte)0, target.Light);
            Assert.IsTrue(predecessor.TryRetire());
            ManualTimer staleCallback = clock.Timer;

            Assert.IsTrue(output.TryCreateXboxOnePhysicalFeedbackSession(Binding(), target, 0, out successor, clock));
            ownerField.SetValue(output, successor);
            Assert.IsTrue(successor.TryPublish(Frame(1, timestamp: clock.Now)));
            int successorWrites = target.Writes;
            // Exercise the actual reader's finally path with its captured
            // predecessor while the instance field names the live successor.
            // All device-lifetime callbacks are local no-ops: no USB/IP or HID.
            var lifetime = new ViiperVirtualDeviceLifetime(1, "watchdog-test", -1,
                (_, _) => { }, (_, _) => { }, _ => { }, () => { });
            using var readerStream = new ViiperDeviceStream(new MemoryStream(),
                new MemoryStream(), lifetime);
            typeof(ViiperOutDevice).GetMethod("FeedbackReadLoop", fields).
                Invoke(output, new object[]
                {
                    ControllerFeedbackFrame.SerializedLength, readerStream,
                    0L, predecessor,
                });
            staleCallback.FireQueued();
            Assert.AreEqual(successorWrites, target.Writes);
            Assert.IsTrue(target.Heavy != 0 && target.Light != 0);
            Assert.IsTrue(successor.TryRetire());
        }
        finally
        {
            predecessor?.TryRetire();
            successor?.TryRetire();
            DS4Windows.Program.rootHub = previousHub;
            Global.RumbleBoost[0] = previousBoost;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ProductionWatchdogNeverWritesToAReplacementControllerInTheSameSlot()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        ControlService previousHub = DS4Windows.Program.rootHub;
        byte previousBoost = Global.RumbleBoost[0];
        XboxOnePhysicalFeedbackSession session = null;
        try
        {
            var clock = new ManualClock();
            var target = new TestDevice();
            var replacement = new TestDevice();
            var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            hub.DS4Controllers = new DS4Device[] { target };
            DS4Windows.Program.rootHub = hub;
            Global.RumbleBoost[0] = 100;
            var output = new ViiperOutDevice(OutContType.ViiperXboxOne, ViiperVirtualDeviceType.XboxOne);
            typeof(ViiperOutDevice).GetField("lastInputDeviceIndex", fields).SetValue(output, 0);
            Assert.IsTrue(output.TryCreateXboxOnePhysicalFeedbackSession(Binding(), target, 0, out session, clock));
            typeof(ViiperOutDevice).GetField("xboxOnePhysicalFeedbackSession", fields).SetValue(output, session);
            Assert.IsTrue(session.TryPublish(Frame(1)));
            int acceptedWrites = target.Writes;
            hub.DS4Controllers[0] = replacement;
            clock.Advance(1_000);
            Assert.AreEqual(0, replacement.Writes);
            Assert.AreEqual(acceptedWrites, target.Writes,
                "Once the exact physical binding is gone, its successor must never inherit writes.");
            Assert.IsFalse(session.TryRetire(), "Missing target is not a confirmed physical neutral.");
            Assert.AreEqual(0, replacement.Writes);
        }
        finally
        {
            session?.TryRetire();
            DS4Windows.Program.rootHub = previousHub;
            Global.RumbleBoost[0] = previousBoost;
        }
    }

    private static XboxOnePhysicalFeedbackSession Create(ManualClock clock,
        Func<ControllerFeedbackActuatorState, bool, bool> sink, Action failure = null)
    {
        Assert.IsTrue(XboxOnePhysicalFeedbackSession.TryCreateOwned(Binding(), new TestDevice(),
            sink, out var session, clock, failure));
        return session;
    }

    private static XboxOneAuthorizedFeedbackBinding Binding() => new()
    {
        Source = (byte)ControllerFeedbackSource.XboxOneVirtualDevice,
        PersonaGeneration = 4, DeviceGeneration = 5, TransportGeneration = 6,
        OwnershipEpoch = 7, TimeToLiveMicroseconds = 1_000,
    };

    private static byte[] Frame(ulong sequence, ulong timestamp = 1_000,
        ulong ttl = 1_000, ulong generation = 5, ulong transport = 6,
        ulong epoch = 7, ControllerFeedbackCommand command = ControllerFeedbackCommand.Apply)
    {
        ushort amplitude = command == ControllerFeedbackCommand.Apply ? (ushort)257 : (ushort)0;
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(ControllerFeedbackSource.XboxOneVirtualDevice,
            command, ControllerFeedbackActuators.All, amplitude, amplitude, amplitude, amplitude,
            sequence, generation, transport, epoch, timestamp, ttl, out var frame));
        byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
        Assert.IsTrue(frame.TryWriteTo(wire));
        return wire;
    }

    private sealed class TestDevice : DS4Device
    {
        internal byte Heavy;
        internal byte Light;
        internal int Writes;

        internal TestDevice() : base("Watchdog test", InputDeviceType.DS4, ConnectionType.USB) { }

        public override void setRumble(byte lightMotor, byte heavyMotor)
        {
            Light = lightMotor;
            Heavy = heavyMotor;
            Writes++;
        }
    }

    // Timer callbacks execute only when the test explicitly advances time.
    // FireQueued models a thread-pool callback already queued before Change or Dispose.
    internal sealed class ManualClock : TimeProvider
    {
        internal ulong Now = 1_000;
        internal bool FailCreateTimer;
        internal ManualTimer Timer;
        public override long TimestampFrequency => 1_000_000;
        public override long GetTimestamp() => checked((long)Now);

        public override ITimer CreateTimer(TimerCallback callback, object state,
            TimeSpan dueTime, TimeSpan period)
        {
            if (FailCreateTimer) { throw new IOException("Deterministic timer creation failure."); }
            Timer = new ManualTimer(this, callback, state);
            Timer.Change(dueTime, period);
            return Timer;
        }

        internal void AdvanceWithoutCallbacks(ulong microseconds) => Now += microseconds;

        internal void Advance(ulong microseconds)
        {
            Now += microseconds;
            Timer?.FireDue();
        }
    }

    internal sealed class ManualTimer : ITimer
    {
        private readonly ManualClock clock;
        private readonly TimerCallback callback;
        private readonly object state;
        private ulong? due;
        internal bool Disposed;
        internal bool FailChange;

        internal ManualTimer(ManualClock clock, TimerCallback callback, object state)
        {
            this.clock = clock;
            this.callback = callback;
            this.state = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(Timeout.InfiniteTimeSpan, period);
            if (Disposed || FailChange) { return false; }
            due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + (ulong)(dueTime.Ticks / 10);
            return true;
        }

        internal void FireDue()
        {
            if (!Disposed && due.HasValue && due.Value <= clock.Now)
            {
                due = null;
                callback(state);
            }
        }

        internal void FireQueued() => callback(state);
        public void Dispose() { Disposed = true; due = null; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
