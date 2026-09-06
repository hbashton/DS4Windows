using System;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ControllerFeedbackRuntimeTests
    {
        [TestMethod]
        public void TypedPublicationRejectsStaleAndPostStopResurrection()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsFalse(runtime.TryPublish(default));

            ControllerFeedbackPublication first = Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 1));
            Assert.IsTrue(runtime.TryPublish(first));
            Assert.IsFalse(runtime.TryPublish(first));
            Assert.IsFalse(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 2,
                    source: ControllerFeedbackSource.Xbox360VirtualDevice))),
                "A source changed inside one ownership epoch.");

            ControllerFeedbackPublication stop = Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 2,
                    command: ControllerFeedbackCommand.Stop));
            Assert.IsTrue(runtime.TryPublish(stop));
            Assert.IsFalse(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 3))),
                "A stopped source epoch was resurrected by sequence alone.");
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 1, ownershipEpoch: 2))));
        }

        [TestMethod]
        public void ArbitrationUsesFixedPriorityAndStopsBeforeFallback()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                Frame(sequence: 1, bodyLow: 100))));
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.AudioHaptics,
                Frame(sequence: 1, bodyLow: 200))));
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 1, bodyLow: 300))));
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.TestPreview,
                Frame(sequence: 1, bodyLow: 400))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease writer));

            ControllerFeedbackDelivery preview = DeliverNext(runtime, writer,
                1_000, delivered: true);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                preview.Disposition);
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.TestPreview,
                preview.Origin);
            Assert.AreEqual((ushort)400, preview.Frame.BodyLow);

            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.TestPreview,
                Frame(sequence: 2, command: ControllerFeedbackCommand.Stop,
                    timestampMicroseconds: 1_001))));
            ControllerFeedbackDelivery firstStop = ClaimAndAdmit(runtime,
                writer, 1_001, out ulong firstStopToken);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                firstStop.Disposition);
            Assert.AreEqual(preview.DeliveryEpoch,
                firstStop.DeliveryEpoch);
            Assert.IsTrue(runtime.Complete(writer, firstStopToken,
                delivered: false, 1_001));

            ControllerFeedbackDelivery retriedStop = ClaimAndAdmit(runtime,
                writer, 1_001, out ulong retryToken);
            Assert.AreEqual(firstStop, retriedStop,
                "A failed logical stop changed epoch or target.");
            Assert.AreNotEqual(firstStopToken, retryToken);
            Assert.IsTrue(runtime.Complete(writer, retryToken,
                delivered: true, 1_001));

            ControllerFeedbackDelivery game = DeliverNext(runtime, writer,
                1_001, delivered: true);
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.NativeGame,
                game.Origin);
            Assert.AreEqual((ushort)300, game.Frame.BodyLow);
            Assert.AreNotEqual(preview.DeliveryEpoch, game.DeliveryEpoch);
        }

        [TestMethod]
        public void ExpiryProducesOneRetryableStopPerAdmittedEpoch()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                Frame(sequence: 1, timestampMicroseconds: 1_000,
                    timeToLiveMicroseconds: 50))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease writer));
            ControllerFeedbackDelivery applied = DeliverNext(runtime, writer,
                1_049, delivered: true);

            ControllerFeedbackDelivery stop = ClaimAndAdmit(runtime, writer,
                1_050, out ulong stopToken);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                stop.Disposition);
            Assert.AreEqual(applied.DeliveryEpoch, stop.DeliveryEpoch);
            Assert.IsTrue(runtime.Complete(writer, stopToken,
                delivered: false, 1_050));

            ControllerFeedbackDelivery retry = ClaimAndAdmit(runtime, writer,
                1_050, out ulong retryToken);
            Assert.AreEqual(stop, retry);
            Assert.IsTrue(runtime.Complete(writer, retryToken,
                delivered: true, 1_050));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.None,
                runtime.Claim(2_000, writer, out _, out _));
        }

        [TestMethod]
        public void SupersededUnadmittedFrameCannotReachFinalAdmission()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                Frame(sequence: 1, bodyLow: 100))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease writer));

            using ManualResetEventSlim claimed = new(false);
            using ManualResetEventSlim published = new(false);
            ulong oldToken = 0;
            ControllerFeedbackDelivery oldDelivery = default;
            Task claimer = Task.Run(() =>
            {
                Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                    runtime.Claim(1_000, writer, out oldDelivery,
                        out oldToken));
                claimed.Set();
                Assert.IsTrue(published.Wait(TimeSpan.FromSeconds(5)));
            });
            Task publisher = Task.Run(() =>
            {
                Assert.IsTrue(claimed.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(runtime.TryPublish(Publication(
                    ControllerFeedbackPublicationOrigin.TestPreview,
                    Frame(sequence: 1, bodyLow: 900))));
                published.Set();
            });
            Task.WaitAll(claimer, publisher);

            Assert.AreEqual(ControllerFeedbackPublicationOrigin.ProfileEffect,
                oldDelivery.Origin);
            Assert.IsFalse(runtime.TryAdmit(writer, oldToken, 1_000),
                "A newly higher-priority source did not fence final admission.");
            Assert.IsTrue(runtime.Complete(writer, oldToken,
                delivered: false, 1_000));

            ControllerFeedbackDelivery preview = DeliverNext(runtime, writer,
                1_000, delivered: true);
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.TestPreview,
                preview.Origin);
            Assert.AreEqual((ushort)900, preview.Frame.BodyLow);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                preview.Disposition,
                "Unadmitted state emitted an unnecessary physical stop.");
        }

        [TestMethod]
        public void WriterLeaseIsSoleAndGenerationFenced()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 1))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease first));
            Assert.IsFalse(runtime.TryAcquireWriter(1, 1, out _));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                runtime.Claim(1_000, first, out _, out ulong token));
            Assert.IsTrue(runtime.TryAdmit(first, token, 1_000));
            Assert.IsFalse(runtime.TryRetireWriter(first),
                "An admitted writer generation was retired early.");
            Assert.IsTrue(runtime.Complete(first, token,
                delivered: true, 1_000));
            Assert.IsTrue(runtime.TryRetireWriter(first));

            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease successor));
            Assert.IsTrue(successor.WriterGeneration >
                first.WriterGeneration);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.None,
                runtime.Claim(1_000, first, out _, out _));
            Assert.IsFalse(runtime.TryAdmit(first, token, 1_000));
            Assert.IsFalse(runtime.Complete(first, token,
                delivered: false, 1_000));

            ControllerFeedbackDelivery replay = DeliverNext(runtime,
                successor, 1_000, delivered: true);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                replay.Disposition,
                "A new sole writer did not receive current canonical state.");
        }

        [TestMethod]
        public void NewTargetGenerationStopsOldAndCannotLeakToOldWriter()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                Frame(sequence: 1, deviceGeneration: 1,
                    transportGeneration: 1, bodyLow: 100))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease oldWriter));
            ControllerFeedbackDelivery old = DeliverNext(runtime, oldWriter,
                1_000, delivered: true);

            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                Frame(sequence: 1, deviceGeneration: 2,
                    transportGeneration: 1, bodyLow: 200))));
            ControllerFeedbackDelivery stop = DeliverNext(runtime, oldWriter,
                1_000, delivered: true);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                stop.Disposition);
            Assert.AreEqual(1UL, stop.DeviceGeneration);
            Assert.AreEqual(old.DeliveryEpoch, stop.DeliveryEpoch);

            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.None,
                runtime.Claim(1_000, oldWriter, out _, out _),
                "An old target writer claimed a new-generation frame.");
            Assert.IsTrue(runtime.TryRetireWriter(oldWriter));
            Assert.IsFalse(runtime.TryAcquireWriter(1, 1, out _),
                "A writer acquired a target generation different from the pending event.");
            Assert.IsTrue(runtime.TryAcquireWriter(2, 1,
                out ControllerFeedbackWriterLease newWriter));
            ControllerFeedbackDelivery current = DeliverNext(runtime,
                newWriter, 1_000, delivered: true);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                current.Disposition);
            Assert.AreEqual(2UL, current.DeviceGeneration);
            Assert.AreEqual((ushort)200, current.Frame.BodyLow);
        }

        [TestMethod]
        public void DeliveredTrueRequiresFinalAdmissionButFailureCanCancelClaim()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                Frame(sequence: 1))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease writer));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                runtime.Claim(1_000, writer, out _, out ulong token));
            Assert.IsFalse(runtime.Complete(writer, token,
                delivered: true, 1_000));
            Assert.IsTrue(runtime.Complete(writer, token,
                delivered: false, 1_000));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                runtime.Claim(1_000, writer, out _, out ulong retry));
            Assert.AreNotEqual(token, retry);
        }

        [TestMethod]
        public void PublishSelectClaimAdmitCompleteSteadyStateAllocatesNothing()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 1))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease writer));
            DeliverNext(runtime, writer, 1_000, delivered: true);

            ulong sequence = 1;
            void Cycle()
            {
                sequence++;
                ControllerFeedbackPublication publication = Publication(
                    ControllerFeedbackPublicationOrigin.NativeGame,
                    Frame(sequence: sequence,
                        timestampMicroseconds: 1_000 + sequence,
                        bodyLow: (ushort)((sequence %
                            (ushort.MaxValue - 1)) + 1)));
                if (!runtime.TryPublish(publication) ||
                    runtime.Claim(1_000 + sequence, writer,
                        out _, out ulong token) !=
                            ControllerFeedbackDeliveryDisposition.Frame ||
                    !runtime.TryAdmit(writer, token, 1_000 + sequence) ||
                    !runtime.Complete(writer, token, delivered: true,
                        1_000 + sequence))
                {
                    throw new InvalidOperationException(
                        "Feedback runtime hot-path cycle failed.");
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
                $"Feedback runtime allocated {allocated} bytes after warm-up.");
        }

        [TestMethod]
        public void IdenticalRenewalExtendsLeaseWithoutDuplicateDelivery()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 1, timestampMicroseconds: 1_000,
                    timeToLiveMicroseconds: 100, bodyLow: 55))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease writer));
            DeliverNext(runtime, writer, 1_000, delivered: true);

            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 2, timestampMicroseconds: 1_050,
                    timeToLiveMicroseconds: 100, bodyLow: 55))));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.None,
                runtime.Claim(1_050, writer, out _, out _),
                "A lease-only refresh repeated an unchanged physical effect.");
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.None,
                runtime.Claim(1_149, writer, out _, out _));

            ControllerFeedbackDelivery stop = DeliverNext(runtime, writer,
                1_150, delivered: true);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                stop.Disposition);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.None,
                runtime.Claim(1_151, writer, out _, out _));
        }

        [TestMethod]
        public void ExplicitRendererRefreshRepresentsNewestUnchangedFrameOnce()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 1, timestampMicroseconds: 1_000,
                    bodyLow: 55))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease writer));
            ControllerFeedbackDelivery first = DeliverNext(runtime, writer,
                1_000, delivered: true);

            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 2, timestampMicroseconds: 1_050,
                    bodyLow: 55))));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.None,
                runtime.Claim(1_050, writer, out _, out _));

            Assert.IsTrue(runtime.TryRefreshCurrentPresentation(writer,
                1_050));
            ControllerFeedbackDelivery refreshed = DeliverNext(runtime,
                writer, 1_050, delivered: true);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                refreshed.Disposition);
            Assert.AreEqual((ulong)2, refreshed.Frame.Sequence);
            Assert.AreEqual(first.DeliveryEpoch, refreshed.DeliveryEpoch,
                "A renderer change must not manufacture a feedback ownership transition.");
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.None,
                runtime.Claim(1_051, writer, out _, out _),
                "One explicit refresh must produce exactly one presentation.");
        }

        [TestMethod]
        public void OptionalPresentationRefreshCannotBypassWriterOrClaimFences()
        {
            ControllerFeedbackRuntime runtime = new(), foreign = new();
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1, out var writer));
            Assert.IsTrue(foreign.TryAcquireWriter(1, 1, out var foreignWriter));
            Assert.IsFalse(runtime.TryRefreshCurrentPresentation(null, 1_000, allowNoFrame: true));
            Assert.IsFalse(runtime.TryRefreshCurrentPresentation(foreignWriter, 1_000, allowNoFrame: true));
            Assert.IsFalse(runtime.TryRefreshCurrentPresentation(writer, 1_000));
            Assert.IsTrue(runtime.TryRefreshCurrentPresentation(writer, 1_000, allowNoFrame: true));
            Assert.IsTrue(runtime.TryPublish(Publication(ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 1, timestampMicroseconds: 1_000, bodyLow: 55))));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                runtime.Claim(1_000, writer, out _, out ulong claim));
            Assert.IsFalse(runtime.TryRefreshCurrentPresentation(writer, 1_000, allowNoFrame: true));
            Assert.IsTrue(runtime.Complete(writer, claim, delivered: false, 1_000));
        }

        [TestMethod]
        public void RenewalReplacesExpiredUnadmittedFrameThenBecomesQuiet()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 1, timestampMicroseconds: 1_000,
                    timeToLiveMicroseconds: 50, bodyLow: 55))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease writer));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                runtime.Claim(1_000, writer,
                    out ControllerFeedbackDelivery original,
                    out ulong originalToken));

            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 2, timestampMicroseconds: 1_040,
                    timeToLiveMicroseconds: 50, bodyLow: 55))));
            Assert.IsFalse(runtime.TryAdmit(writer, originalToken, 1_050),
                "An expired pre-renewal reservation reached admission.");
            Assert.IsTrue(runtime.Complete(writer, originalToken,
                delivered: false, 1_050));

            ControllerFeedbackDelivery renewed = DeliverNext(runtime, writer,
                1_050, delivered: true);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                renewed.Disposition);
            Assert.AreEqual(2UL, renewed.Frame.Sequence);
            Assert.AreEqual(original.DeliveryEpoch,
                renewed.DeliveryEpoch);

            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 3, timestampMicroseconds: 1_060,
                    timeToLiveMicroseconds: 50, bodyLow: 55))));
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.None,
                runtime.Claim(1_060, writer, out _, out _),
                "A completed unchanged effect churned after renewal.");
        }

        [TestMethod]
        public void SequenceWrapRequiresNewOwnershipEpochAndStopsOldFirst()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: ulong.MaxValue, ownershipEpoch: 1,
                    bodyLow: 100))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease writer));
            ControllerFeedbackDelivery old = DeliverNext(runtime, writer,
                1_000, delivered: true);

            Assert.IsFalse(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 1, ownershipEpoch: 1, bodyLow: 200))),
                "Sequence wrap resurrected one ownership epoch.");
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.NativeGame,
                Frame(sequence: 1, ownershipEpoch: 2, bodyLow: 200))));

            ControllerFeedbackDelivery stop = DeliverNext(runtime, writer,
                1_000, delivered: true);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                stop.Disposition);
            Assert.AreEqual(old.DeliveryEpoch, stop.DeliveryEpoch);

            ControllerFeedbackDelivery successor = DeliverNext(runtime,
                writer, 1_000, delivered: true);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Frame,
                successor.Disposition);
            Assert.AreEqual((ushort)200, successor.Frame.BodyLow);
            Assert.AreEqual(2UL, successor.Frame.OwnershipEpoch);
        }

        [TestMethod]
        public void StaleGenerationAndEpochCannotOutrankCurrentTarget()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                Frame(sequence: 1, deviceGeneration: 1,
                    transportGeneration: 9, ownershipEpoch: 2,
                    bodyLow: 100))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 9,
                out ControllerFeedbackWriterLease oldWriter));
            DeliverNext(runtime, oldWriter, 1_000, delivered: true);

            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                Frame(sequence: 1, deviceGeneration: 2,
                    transportGeneration: 1, ownershipEpoch: 1,
                    bodyLow: 200))));
            Assert.IsFalse(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                Frame(sequence: ulong.MaxValue, deviceGeneration: 1,
                    transportGeneration: ulong.MaxValue,
                    ownershipEpoch: ulong.MaxValue, bodyLow: 250))));
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.TestPreview,
                Frame(sequence: ulong.MaxValue, deviceGeneration: 1,
                    transportGeneration: ulong.MaxValue,
                    ownershipEpoch: ulong.MaxValue, bodyLow: 240))));

            ControllerFeedbackDelivery stop = DeliverNext(runtime, oldWriter,
                1_000, delivered: true);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop,
                stop.Disposition);
            Assert.IsTrue(runtime.TryRetireWriter(oldWriter));
            Assert.IsTrue(runtime.TryAcquireWriter(2, 1,
                out ControllerFeedbackWriterLease currentWriter));
            ControllerFeedbackDelivery current = DeliverNext(runtime,
                currentWriter, 1_000, delivered: true);
            Assert.AreEqual(2UL, current.DeviceGeneration);
            Assert.AreEqual((ushort)200, current.Frame.BodyLow);
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.ProfileEffect,
                current.Origin);
        }

        [TestMethod]
        public void FixedPriorityWinsIndependentOfSourceSequence()
        {
            ControllerFeedbackRuntime runtime = new();
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.ProfileEffect,
                Frame(sequence: ulong.MaxValue, bodyLow: 100))));
            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.TestPreview,
                Frame(sequence: 1, bodyLow: 200))));
            Assert.IsTrue(runtime.TryAcquireWriter(1, 1,
                out ControllerFeedbackWriterLease writer));
            ControllerFeedbackDelivery winner = DeliverNext(runtime, writer,
                1_000, delivered: true);
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.TestPreview,
                winner.Origin);
            Assert.AreEqual((ushort)200, winner.Frame.BodyLow);

            Assert.IsTrue(runtime.TryPublish(Publication(
                ControllerFeedbackPublicationOrigin.TestPreview,
                Frame(sequence: 2, bodyLow: 201))));
            ControllerFeedbackDelivery replacement = DeliverNext(runtime,
                writer, 1_000, delivered: true);
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.TestPreview,
                replacement.Origin);
            Assert.AreEqual((ushort)201, replacement.Frame.BodyLow);
            Assert.AreEqual(winner.DeliveryEpoch,
                replacement.DeliveryEpoch);
        }

        private static ControllerFeedbackDelivery ClaimAndAdmit(
            ControllerFeedbackRuntime runtime,
            ControllerFeedbackWriterLease writer, ulong nowMicroseconds,
            out ulong token)
        {
            ControllerFeedbackDeliveryDisposition disposition = runtime.Claim(
                nowMicroseconds, writer,
                out ControllerFeedbackDelivery delivery, out token);
            Assert.AreNotEqual(ControllerFeedbackDeliveryDisposition.None,
                disposition);
            Assert.AreEqual(disposition, delivery.Disposition);
            Assert.AreNotEqual(0UL, token);
            Assert.IsTrue(runtime.TryAdmit(writer, token, nowMicroseconds));
            return delivery;
        }

        private static ControllerFeedbackDelivery DeliverNext(
            ControllerFeedbackRuntime runtime,
            ControllerFeedbackWriterLease writer, ulong nowMicroseconds,
            bool delivered)
        {
            ControllerFeedbackDelivery delivery = ClaimAndAdmit(runtime,
                writer, nowMicroseconds, out ulong token);
            Assert.IsTrue(runtime.Complete(writer, token, delivered,
                nowMicroseconds));
            return delivery;
        }

        private static ControllerFeedbackPublication Publication(
            ControllerFeedbackPublicationOrigin origin,
            in ControllerFeedbackFrame frame)
        {
            Assert.IsTrue(ControllerFeedbackPublication.TryCreate(origin,
                frame, out ControllerFeedbackPublication publication));
            return publication;
        }

        private static ControllerFeedbackFrame Frame(
            ulong sequence,
            ControllerFeedbackCommand command =
                ControllerFeedbackCommand.Apply,
            ControllerFeedbackSource source =
                ControllerFeedbackSource.XboxOneVirtualDevice,
            ulong deviceGeneration = 1,
            ulong transportGeneration = 1,
            ulong ownershipEpoch = 1,
            ulong timestampMicroseconds = 1_000,
            ulong timeToLiveMicroseconds = 250_000,
            ushort bodyLow = 11)
        {
            ushort amplitude = command == ControllerFeedbackCommand.Apply ?
                bodyLow : (ushort)0;
            Assert.IsTrue(ControllerFeedbackFrame.TryCreate(source, command,
                ControllerFeedbackActuators.All, amplitude, 0, 0, 0,
                sequence, deviceGeneration, transportGeneration,
                ownershipEpoch, timestampMicroseconds,
                timeToLiveMicroseconds, out ControllerFeedbackFrame frame));
            return frame;
        }
    }
}
