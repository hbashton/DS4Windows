using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ControllerFeedbackStateLanePumpTests
    {
        [TestMethod]
        public void Xbox360AdapterIsByteExactAndRejectsWrongLengths()
        {
            byte[] payload = { 0x12, 0xAB, 0xCC };
            Assert.IsFalse(Xbox360CanonicalFeedbackAdapter.TryDecode(null, 2,
                out _));
            Assert.IsFalse(Xbox360CanonicalFeedbackAdapter.TryDecode(payload,
                1, out _));
            Assert.IsFalse(Xbox360CanonicalFeedbackAdapter.TryDecode(payload,
                3, out _));
            Assert.IsTrue(Xbox360CanonicalFeedbackAdapter.TryDecode(payload,
                2, out ControllerFeedbackActuatorState state));
            Assert.AreEqual((ushort)0x1212, state.BodyLow);
            Assert.AreEqual((ushort)0xABAB, state.BodyHigh);
            Assert.AreEqual((ushort)0, state.LeftTrigger);
            Assert.AreEqual((ushort)0, state.RightTrigger);
            Xbox360CanonicalFeedbackAdapter.ProjectLegacy(state,
                out byte heavySlow, out byte lightFast);
            Assert.AreEqual((byte)0x12, heavySlow);
            Assert.AreEqual((byte)0xAB, lightFast);

            byte[] neutral = { 0, 0 };
            Assert.IsTrue(Xbox360CanonicalFeedbackAdapter.TryDecode(neutral,
                2, out state));
            Assert.IsTrue(state.IsNeutral);
        }

        [TestMethod]
        public void AllOriginsForOneDeviceShareOneRuntimeAndWriter()
        {
            ControllerFeedbackStateLanePump owner = Owner();
            ControllerFeedbackStateLanePump.Lane profile = Lane(owner,
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                ControllerFeedbackSource.DualShock4VirtualDevice,
                ownershipEpoch: 10);
            ControllerFeedbackStateLanePump.Lane game = Lane(owner,
                ControllerFeedbackPublicationOrigin.NativeGame,
                ControllerFeedbackSource.Xbox360VirtualDevice,
                ownershipEpoch: 20);
            ControllerFeedbackStateLanePump.Lane preview = Lane(owner,
                ControllerFeedbackPublicationOrigin.TestPreview,
                ControllerFeedbackSource.DualSenseVirtualDevice,
                ownershipEpoch: 30);
            Assert.IsFalse(owner.TryCreateLane(
                ControllerFeedbackPublicationOrigin.NativeGame,
                ControllerFeedbackSource.XboxOneVirtualDevice, 40, 250_000,
                100_000, out _),
                "One origin cannot create a second arbitration domain.");

            Assert.IsTrue(profile.TryPublish(State(10), 1_000));
            Assert.IsTrue(game.TryPublish(State(20), 1_000));
            Assert.IsTrue(preview.TryPublish(State(30), 1_000));
            CountingSink sink = new();

            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                owner.PumpOnce(1_000, sink,
                    out ControllerFeedbackDelivery first));
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.TestPreview,
                first.Origin);
            Assert.AreEqual((ushort)30, first.Frame.BodyLow);
            Assert.AreEqual(ControllerFeedbackPumpDisposition.None,
                owner.PumpOnce(1_000, sink, out _));

            Assert.IsTrue(preview.RequestStop(1_001));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                owner.PumpOnce(1_001, sink,
                    out ControllerFeedbackDelivery stop));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                stop.Disposition);
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.TestPreview,
                stop.Origin);
            Assert.AreEqual(first.DeliveryEpoch, stop.DeliveryEpoch);

            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                owner.PumpOnce(1_001, sink,
                    out ControllerFeedbackDelivery successor));
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.NativeGame,
                successor.Origin);
            Assert.AreEqual((ushort)20, successor.Frame.BodyLow);
            Assert.AreEqual(3, sink.Calls);
        }

        [TestMethod]
        public void ReusablePreviewWithdrawalStopsThenRestoresLowerOwner()
        {
            ControllerFeedbackStateLanePump owner = Owner();
            ControllerFeedbackStateLanePump.Lane game = Lane(owner,
                ControllerFeedbackPublicationOrigin.NativeGame,
                ControllerFeedbackSource.XboxOneVirtualDevice, 20);
            ControllerFeedbackStateLanePump.Lane preview = Lane(owner,
                ControllerFeedbackPublicationOrigin.TestPreview,
                ControllerFeedbackSource.Xbox360VirtualDevice, 30);
            CountingSink sink = new();

            Assert.IsTrue(game.TryPublish(State(20), 1_000));
            Assert.IsTrue(preview.TryPublish(State(30), 1_000));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                owner.PumpOnce(1_000, sink,
                    out ControllerFeedbackDelivery firstPreview));
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.TestPreview,
                firstPreview.Origin);

            Assert.IsTrue(preview.TryWithdraw(1_001));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                owner.PumpOnce(1_001, sink,
                    out ControllerFeedbackDelivery previewStop));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                previewStop.Disposition);
            Assert.AreEqual(firstPreview.DeliveryEpoch,
                previewStop.DeliveryEpoch);
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                owner.PumpOnce(1_001, sink,
                    out ControllerFeedbackDelivery gameFrame));
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.NativeGame,
                gameFrame.Origin);

            Assert.IsTrue(preview.TryPublish(State(40), 1_002),
                "A withdrawn fixed slot must accept a successor epoch.");
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                owner.PumpOnce(1_002, sink,
                    out ControllerFeedbackDelivery gameStop));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                gameStop.Disposition);
            Assert.AreEqual(gameFrame.DeliveryEpoch, gameStop.DeliveryEpoch);
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                owner.PumpOnce(1_002, sink,
                    out ControllerFeedbackDelivery secondPreview));
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.TestPreview,
                secondPreview.Origin);
            Assert.AreEqual((ushort)40, secondPreview.Frame.BodyLow);
            Assert.AreNotEqual(firstPreview.Frame.OwnershipEpoch,
                secondPreview.Frame.OwnershipEpoch);
        }

        [TestMethod]
        public void DeviceRetirementStopsEveryOriginBeforeWriterRetires()
        {
            ControllerFeedbackStateLanePump owner = Owner();
            ControllerFeedbackStateLanePump.Lane profile = Lane(owner,
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                ControllerFeedbackSource.DualShock4VirtualDevice, 10);
            ControllerFeedbackStateLanePump.Lane game = Lane(owner,
                ControllerFeedbackPublicationOrigin.NativeGame,
                ControllerFeedbackSource.Xbox360VirtualDevice, 20);
            Assert.IsTrue(profile.TryPublish(State(10), 1_000));
            Assert.IsTrue(game.TryPublish(State(20), 1_000));
            CountingSink sink = new();
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                owner.PumpOnce(1_000, sink, out _));

            Assert.IsTrue(owner.TryStopAndRetire(1_001, sink,
                maxAttempts: 1));
            Assert.IsTrue(owner.IsRetired);
            Assert.AreEqual(2, sink.Calls,
                "Retirement must emit one stop, not fall through to profile.");
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                sink.LastDelivery.Disposition);
            Assert.IsFalse(profile.TryPublish(State(11), 1_002));
            Assert.IsFalse(game.TryPublish(State(21), 1_002));
            Assert.IsFalse(owner.TryCreateLane(
                ControllerFeedbackPublicationOrigin.AudioHaptics,
                ControllerFeedbackSource.DualSenseVirtualDevice, 30,
                250_000, 100_000, out _));
        }

        [TestMethod]
        public void LaneHasNoPrivateRuntimeWriterOrSinkPumpCapability()
        {
            Type lane = typeof(ControllerFeedbackStateLanePump.Lane);
            Assert.IsFalse(lane.GetFields(BindingFlags.Instance |
                    BindingFlags.NonPublic | BindingFlags.Public).Any(field =>
                    field.FieldType == typeof(ControllerFeedbackRuntime) ||
                    field.FieldType == typeof(ControllerFeedbackWriterLease)));
            Assert.IsFalse(lane.GetMethods(BindingFlags.Instance |
                    BindingFlags.NonPublic | BindingFlags.Public).Any(method =>
                    method.Name is "PumpOnce" or "TryStopAndRetire"));

            Type owner = typeof(ControllerFeedbackStateLanePump);
            Assert.AreEqual(1, owner.GetFields(BindingFlags.Instance |
                    BindingFlags.NonPublic).Count(field =>
                    field.FieldType == typeof(ControllerFeedbackRuntime)));
            Assert.AreEqual(1, owner.GetFields(BindingFlags.Instance |
                    BindingFlags.NonPublic).Count(field =>
                    field.FieldType ==
                        typeof(ControllerFeedbackWriterLease)));
        }

        [TestMethod]
        public void SinkCanPublishAnotherOriginAcrossThreadsWithoutLocking()
        {
            ControllerFeedbackStateLanePump owner = Owner();
            ControllerFeedbackStateLanePump.Lane profile = Lane(owner,
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                ControllerFeedbackSource.DualShock4VirtualDevice, 10);
            ControllerFeedbackStateLanePump.Lane game = Lane(owner,
                ControllerFeedbackPublicationOrigin.NativeGame,
                ControllerFeedbackSource.Xbox360VirtualDevice, 20);
            Assert.IsTrue(game.TryPublish(State(20), 1_000));
            var sink = new CrossThreadPublishingSink(profile);

            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                owner.PumpOnce(1_000, sink, out _));
            Assert.IsTrue(sink.Published,
                "The callback would time out if owner/runtime locks were held.");
            Assert.AreEqual(ControllerFeedbackPumpDisposition.None,
                owner.PumpOnce(1_000, sink, out _));
        }

        [TestMethod]
        public void RenewalExtendsTtlWithoutIdlePhysicalChurn()
        {
            PumpHarness pump = Pump(ttl: 100,
                renewal: 40);
            CountingSink sink = new();
            Assert.IsTrue(pump.TryPublish(State(10), 1_000));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                pump.PumpOnce(1_000, sink, out _));
            Assert.AreEqual(1, sink.Calls);

            Assert.AreEqual(ControllerFeedbackLeaseServiceDisposition.None,
                pump.ServiceLease(1_039));
            Assert.AreEqual(ControllerFeedbackLeaseServiceDisposition.Renewed,
                pump.ServiceLease(1_040));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.None,
                pump.PumpOnce(1_040, sink, out _));
            Assert.AreEqual(1, sink.Calls,
                "An unchanged TTL renewal repeated physical output.");

            Assert.AreEqual(ControllerFeedbackLeaseServiceDisposition.Renewed,
                pump.ServiceLease(1_080));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.None,
                pump.PumpOnce(1_080, sink, out _));
            Assert.AreEqual(
                ControllerFeedbackLeaseServiceDisposition.StopRequested,
                pump.ServiceLease(1_180));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                pump.PumpOnce(1_180, sink,
                    out ControllerFeedbackDelivery stop));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                stop.Disposition);
            Assert.AreEqual(2, sink.Calls);
            Assert.AreEqual(ControllerFeedbackPumpDisposition.None,
                pump.PumpOnce(1_181, sink, out _));
            Assert.AreEqual(2, sink.Calls);
        }

        [TestMethod]
        public void StopRetriesOneLogicalNeutralAndRetirementIsBounded()
        {
            PumpHarness pump = Pump();
            CountingSink apply = new();
            Assert.IsTrue(pump.TryPublish(State(99), 1_000));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                pump.PumpOnce(1_000, apply, out _));

            RetrySink failing = new(failuresBeforeSuccess: int.MaxValue);
            Assert.IsFalse(pump.TryStopAndRetire(1_001, failing,
                maxAttempts: 3));
            Assert.AreEqual(3, failing.Calls);
            Assert.IsFalse(pump.IsRetired);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                failing.LastDisposition);
            Assert.AreNotEqual(0UL, failing.DeliveryEpoch);
            Assert.IsTrue(failing.AllEpochsMatched);

            RetrySink success = new(failuresBeforeSuccess: 0,
                expectedEpoch: failing.DeliveryEpoch);
            Assert.IsTrue(pump.TryStopAndRetire(1_002, success,
                maxAttempts: 1));
            Assert.AreEqual(1, success.Calls);
            Assert.IsTrue(success.AllEpochsMatched);
            Assert.IsTrue(pump.IsRetired);
            Assert.IsTrue(pump.TryStopAndRetire(1_003, success,
                maxAttempts: 0));
        }

        [TestMethod]
        public void SinkExceptionLeavesExactDeliveryRetryable()
        {
            PumpHarness pump = Pump();
            Assert.IsTrue(pump.TryPublish(State(25), 1_000));
            ThrowingSink throwing = new();
            Assert.ThrowsException<InvalidOperationException>(() =>
                pump.PumpOnce(1_000, throwing, out _));

            RetrySink retry = new(failuresBeforeSuccess: 0,
                expectedEpoch: throwing.DeliveryEpoch);
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                pump.PumpOnce(1_000, retry,
                    out ControllerFeedbackDelivery delivery));
            Assert.AreEqual(throwing.DeliveryEpoch,
                delivery.DeliveryEpoch);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                delivery.Disposition);
            Assert.AreEqual(1, retry.Calls);
        }

        [TestMethod]
        public void TimestampRegressionCannotReplaceNewerState()
        {
            PumpHarness pump = Pump();
            Assert.IsTrue(pump.TryPublish(State(25), 1_000));
            Assert.IsFalse(pump.TryPublish(State(50), 999));
            CountingSink sink = new();
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                pump.PumpOnce(1_000, sink,
                    out ControllerFeedbackDelivery delivery));
            Assert.AreEqual((ushort)25, delivery.Frame.BodyLow);
        }

        [TestMethod]
        public void SinkRunsOutsideLocksAndReentrantPumpCannotDuplicateWriter()
        {
            PumpHarness pump = Pump();
            ReentrantSink sink = new(pump);
            Assert.IsTrue(pump.TryPublish(State(10), 1_000));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                pump.PumpOnce(1_000, sink, out _));
            Assert.IsTrue(sink.StopRequestedInsideCallback);
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Busy,
                sink.ReentrantPumpResult);
            Assert.AreEqual(1, sink.Calls);

            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                pump.PumpOnce(1_001, sink,
                    out ControllerFeedbackDelivery stop));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                stop.Disposition);
            Assert.AreEqual(2, sink.Calls);
        }

        [TestMethod]
        public void ConcurrentPumpsInvokeOnlyOneSinkAtATime()
        {
            PumpHarness pump = Pump();
            BlockingSink sink = new();
            Assert.IsTrue(pump.TryPublish(State(10), 1_000));

            Task<ControllerFeedbackPumpDisposition> first = Task.Run(() =>
                pump.PumpOnce(1_000, sink, out _));
            Assert.IsTrue(sink.Entered.Wait(TimeSpan.FromSeconds(5)));
            Task<ControllerFeedbackPumpDisposition> second = Task.Run(() =>
                pump.PumpOnce(1_000, sink, out _));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Busy,
                second.GetAwaiter().GetResult());
            sink.Release.Set();
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                first.GetAwaiter().GetResult());
            Assert.AreEqual(1, sink.MaximumConcurrent);
            Assert.AreEqual(1, sink.Calls);
        }

        [TestMethod]
        public void NeutralIsAFrameAndStillGetsOneLifecycleStop()
        {
            PumpHarness pump = Pump();
            CountingSink sink = new();
            Assert.IsTrue(pump.TryPublish(default, 1_000));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                pump.PumpOnce(1_000, sink,
                    out ControllerFeedbackDelivery neutral));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                neutral.Disposition);
            Assert.AreEqual(ControllerFeedbackCommand.Neutral,
                neutral.Frame.Command);
            Assert.IsTrue(pump.RequestStop(1_001));
            Assert.IsFalse(pump.RequestStop(1_001));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                pump.PumpOnce(1_001, sink,
                    out ControllerFeedbackDelivery stop));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                stop.Disposition);
            Assert.AreEqual(neutral.DeliveryEpoch, stop.DeliveryEpoch);
            Assert.AreEqual(2, sink.Calls);
            Assert.AreEqual(ControllerFeedbackPumpDisposition.None,
                pump.PumpOnce(1_002, sink, out _));
        }

        [TestMethod]
        public void PublishAndPumpSteadyStateAllocateNothingAfterWarmup()
        {
            PumpHarness pump = Pump(ttl: 250_000,
                renewal: 100_000);
            CountingSink sink = new();
            ulong timestamp = 1_000;

            void Cycle()
            {
                timestamp++;
                ushort amplitude = (ushort)((timestamp & 1) + 1);
                if (!pump.TryPublish(State(amplitude), timestamp) ||
                    pump.PumpOnce(timestamp, sink, out _) !=
                        ControllerFeedbackPumpDisposition.Delivered)
                {
                    throw new InvalidOperationException(
                        "Canonical feedback state-lane cycle failed.");
                }
            }

            for (int index = 0; index < 128; index++)
            {
                Cycle();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
            {
                Cycle();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated,
                $"Canonical feedback pump allocated {allocated} bytes.");
        }

        private static PumpHarness Pump(
            ulong ttl = 250_000, ulong renewal = 100_000)
        {
            Assert.IsTrue(ControllerFeedbackStateLanePump.TryCreate(
                deviceGeneration: 7, transportGeneration: 11,
                out ControllerFeedbackStateLanePump pump));
            Assert.IsTrue(pump.TryCreateLane(
                ControllerFeedbackPublicationOrigin.NativeGame,
                ControllerFeedbackSource.Xbox360VirtualDevice,
                ownershipEpoch: 13, timeToLiveMicroseconds: ttl,
                renewalIntervalMicroseconds: renewal,
                out ControllerFeedbackStateLanePump.Lane lane));
            return new PumpHarness(pump, lane);
        }

        private static ControllerFeedbackStateLanePump Owner()
        {
            Assert.IsTrue(ControllerFeedbackStateLanePump.TryCreate(7, 11,
                out ControllerFeedbackStateLanePump owner));
            return owner;
        }

        private static ControllerFeedbackStateLanePump.Lane Lane(
            ControllerFeedbackStateLanePump owner,
            ControllerFeedbackPublicationOrigin origin,
            ControllerFeedbackSource source, ulong ownershipEpoch)
        {
            Assert.IsTrue(owner.TryCreateLane(origin, source, ownershipEpoch,
                250_000, 100_000,
                out ControllerFeedbackStateLanePump.Lane lane));
            return lane;
        }

        private static ControllerFeedbackActuatorState State(
            ushort bodyLow) => new(bodyLow, 0, 0, 0);

        private sealed class CountingSink : IControllerFeedbackDeliverySink
        {
            internal int Calls;
            internal ControllerFeedbackDelivery LastDelivery;

            public bool TryDeliver(in ControllerFeedbackDelivery delivery)
            {
                Calls++;
                LastDelivery = delivery;
                return true;
            }
        }

        private sealed class RetrySink : IControllerFeedbackDeliverySink
        {
            private readonly int failuresBeforeSuccess;
            private readonly ulong expectedEpoch;

            internal RetrySink(int failuresBeforeSuccess,
                ulong expectedEpoch = 0)
            {
                this.failuresBeforeSuccess = failuresBeforeSuccess;
                this.expectedEpoch = expectedEpoch;
            }

            internal int Calls { get; private set; }
            internal ulong DeliveryEpoch { get; private set; }
            internal bool AllEpochsMatched { get; private set; } = true;
            internal ControllerFeedbackDeliveryDisposition LastDisposition
            {
                get;
                private set;
            }

            public bool TryDeliver(in ControllerFeedbackDelivery delivery)
            {
                Calls++;
                LastDisposition = delivery.Disposition;
                if (DeliveryEpoch == 0)
                {
                    DeliveryEpoch = delivery.DeliveryEpoch;
                }
                else if (DeliveryEpoch != delivery.DeliveryEpoch)
                {
                    AllEpochsMatched = false;
                }

                if (expectedEpoch != 0 &&
                    expectedEpoch != delivery.DeliveryEpoch)
                {
                    AllEpochsMatched = false;
                }
                return Calls > failuresBeforeSuccess;
            }
        }

        private sealed class ReentrantSink : IControllerFeedbackDeliverySink
        {
            private readonly PumpHarness pump;

            internal ReentrantSink(PumpHarness pump)
            {
                this.pump = pump;
            }

            internal int Calls { get; private set; }
            internal bool StopRequestedInsideCallback { get; private set; }
            internal ControllerFeedbackPumpDisposition ReentrantPumpResult
            {
                get;
                private set;
            }

            public bool TryDeliver(in ControllerFeedbackDelivery delivery)
            {
                Calls++;
                if (Calls == 1)
                {
                    StopRequestedInsideCallback = pump.RequestStop(1_001);
                    ReentrantPumpResult = pump.PumpOnce(1_001, this,
                        out _);
                }
                return true;
            }
        }

        private sealed class PumpHarness
        {
            private readonly ControllerFeedbackStateLanePump owner;
            private readonly ControllerFeedbackStateLanePump.Lane lane;

            internal PumpHarness(ControllerFeedbackStateLanePump owner,
                ControllerFeedbackStateLanePump.Lane lane)
            {
                this.owner = owner;
                this.lane = lane;
            }

            internal bool IsRetired => owner.IsRetired;

            internal bool TryPublish(in ControllerFeedbackActuatorState state,
                ulong nowMicroseconds) =>
                lane.TryPublish(state, nowMicroseconds);

            internal ControllerFeedbackLeaseServiceDisposition ServiceLease(
                ulong nowMicroseconds) => lane.ServiceLease(nowMicroseconds);

            internal bool RequestStop(ulong nowMicroseconds) =>
                lane.RequestStop(nowMicroseconds);

            internal ControllerFeedbackPumpDisposition PumpOnce(
                ulong nowMicroseconds, IControllerFeedbackDeliverySink sink,
                out ControllerFeedbackDelivery delivery) =>
                owner.PumpOnce(nowMicroseconds, sink, out delivery);

            internal bool TryStopAndRetire(ulong nowMicroseconds,
                IControllerFeedbackDeliverySink sink, int maxAttempts) =>
                owner.TryStopAndRetire(nowMicroseconds, sink, maxAttempts);
        }

        private sealed class ThrowingSink : IControllerFeedbackDeliverySink
        {
            internal ulong DeliveryEpoch { get; private set; }

            public bool TryDeliver(in ControllerFeedbackDelivery delivery)
            {
                DeliveryEpoch = delivery.DeliveryEpoch;
                throw new InvalidOperationException("injected sink failure");
            }
        }

        private sealed class BlockingSink : IControllerFeedbackDeliverySink
        {
            internal readonly ManualResetEventSlim Entered = new(false);
            internal readonly ManualResetEventSlim Release = new(false);
            private int active;

            internal int Calls;
            internal int MaximumConcurrent;

            public bool TryDeliver(in ControllerFeedbackDelivery delivery)
            {
                int current = Interlocked.Increment(ref active);
                Interlocked.Increment(ref Calls);
                RecordMaximum(ref MaximumConcurrent, current);
                Entered.Set();
                Assert.IsTrue(Release.Wait(TimeSpan.FromSeconds(5)));
                Interlocked.Decrement(ref active);
                return true;
            }

            private static void RecordMaximum(ref int target, int candidate)
            {
                int current = Volatile.Read(ref target);
                while (candidate > current)
                {
                    int observed = Interlocked.CompareExchange(ref target,
                        candidate, current);
                    if (observed == current)
                    {
                        return;
                    }
                    current = observed;
                }
            }
        }

        private sealed class CrossThreadPublishingSink :
            IControllerFeedbackDeliverySink
        {
            private readonly ControllerFeedbackStateLanePump.Lane lane;

            internal CrossThreadPublishingSink(
                ControllerFeedbackStateLanePump.Lane lane)
            {
                this.lane = lane;
            }

            internal bool Published { get; private set; }

            public bool TryDeliver(in ControllerFeedbackDelivery delivery)
            {
                Task<bool> publish = Task.Run(() =>
                    lane.TryPublish(State(10), 1_000));
                Published = publish.Wait(TimeSpan.FromSeconds(2)) &&
                    publish.Result;
                return Published;
            }
        }
    }
}
