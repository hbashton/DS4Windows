using System;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2HdRumbleDeliverySinkTests
{
    [TestMethod]
    public void XboxPolicyRefreshKeepsSequenceExpiryBodyAndOtherOrigins()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11,
            Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating);
        var delivery = FrameDelivery(31, 1, 20_000, 30_000, 40_000, 50_000);
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.IsTrue(sink.TryStageXboxOutputPolicy(delivery.Frame, new(true, false), out ulong revision));
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.IsTrue(sink.HasPresentedXboxOutputPolicy(delivery.Frame, revision));
        var expected = Switch2HdRumbleFeedbackTranslator.CreateCompatibilityGroup(20_000, 30_000);
        Assert.AreEqual(expected, writer.Last.Left);
        Assert.AreEqual(expected, writer.Last.Right);
        Assert.AreEqual(delivery.Frame.Sequence, writer.Last.Sequence);
        Assert.AreEqual(delivery.Frame.TimestampMicroseconds, writer.Last.TimestampMicroseconds);
        Assert.AreEqual(delivery.Frame.TimeToLiveMicroseconds, writer.Last.TimeToLiveMicroseconds);

        Assert.IsTrue(sink.TryDeliver(StopDelivery(31)));
        var other = FrameDelivery(32, 2, 20_000, 30_000, 40_000, 50_000);
        Assert.IsTrue(sink.TryStageXboxOutputPolicy(other.Frame, new(false, false), out _));
        var profileDelivery = new ControllerFeedbackDelivery(other.Disposition,
            ControllerFeedbackPublicationOrigin.ProfileEffect, other.Frame,
            other.DeviceGeneration, other.TransportGeneration, other.DeliveryEpoch);
        Assert.IsTrue(sink.TryDeliver(profileDelivery));
        Assert.AreEqual(ControllerFeedbackCommand.Apply, writer.Last.Command,
            "A game-frame policy must not mute a different arbitration origin.");
    }

    [TestMethod]
    public void XboxPolicyRefreshRetainsUncertainBytesBeforeApplyingRestriction()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11,
            Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating);
        var delivery = FrameDelivery(31, 1, 20_000, 30_000, 40_000, 50_000);
        writer.Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
            Switch2HdRumblePhysicalWriteFailure.Busy);
        Assert.IsFalse(sink.TryDeliver(delivery));
        var retained = writer.Last;
        Assert.IsTrue(sink.TryStageXboxOutputPolicy(delivery.Frame, new(false, false), out ulong revision));
        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.AreEqual(retained, writer.Last, "A retained output is immutable until its exact completion.");
        Assert.IsFalse(sink.HasPresentedXboxOutputPolicy(delivery.Frame, revision));
        Assert.IsTrue(sink.TryDeliver(delivery));
        var zero = Switch2HdRumbleFeedbackTranslator.CreateCompatibilityGroup(0, 0);
        Assert.AreEqual(zero, writer.Last.Left);
        Assert.AreEqual(zero, writer.Last.Right);
        Assert.IsTrue(sink.HasPresentedXboxOutputPolicy(delivery.Frame, revision));
    }

    [TestMethod]
    public void FourCanonicalActuatorsRemainSideSeparatedAtWriterBoundary()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11,
            Switch2HdRumbleFeedbackPolicy.
                SideLocalImpulseDualBandSaturating);
        ControllerFeedbackDelivery delivery = FrameDelivery(
            deliveryEpoch: 31, sequence: 1, bodyLow: 10_000,
            bodyHigh: 20_000, leftTrigger: 30_000,
            rightTrigger: 40_000);

        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.AreEqual(1, writer.Calls);
        Switch2HdRumblePhysicalSubmission submission = writer.Last;
        Assert.IsTrue(submission.HasValidInvariants());
        Assert.AreEqual(ControllerFeedbackCommand.Apply,
            submission.Command);
        Assert.AreEqual(Switch2HdRumbleFeedbackFidelity.
            SideLocalImpulseApproximation, submission.Fidelity);
        Assert.AreEqual((ulong)31, submission.DeliveryEpoch);
        Assert.AreEqual((ulong)7, submission.DeviceGeneration);
        Assert.AreEqual((ulong)11, submission.TransportGeneration);

        Switch2HdRumbleSubframe left = submission.Left.First;
        Switch2HdRumbleSubframe right = submission.Right.First;
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
            ScaleCanonicalAmplitude(10_000),
            left.Oscillator1AmplitudeCode);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
            MixPackedAmplitudesWithHeadroom(
                Switch2HdRumbleFeedbackTranslator.
                    ScaleCanonicalAmplitude(20_000),
                Switch2HdRumbleFeedbackTranslator.
                    ScaleCanonicalAmplitude(30_000)),
            left.Oscillator0AmplitudeCode);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
            ScaleCanonicalAmplitude(10_000),
            right.Oscillator1AmplitudeCode);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
            MixPackedAmplitudesWithHeadroom(
                Switch2HdRumbleFeedbackTranslator.
                    ScaleCanonicalAmplitude(20_000),
                Switch2HdRumbleFeedbackTranslator.
                    ScaleCanonicalAmplitude(40_000)),
            right.Oscillator0AmplitudeCode);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
            GetImpulseHighFrequency(30_000,
                Switch2HdRumbleImpulseTuning.Default),
            left.Oscillator0ControlCode);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
            GetImpulseHighFrequency(40_000,
                Switch2HdRumbleImpulseTuning.Default),
            right.Oscillator0ControlCode);
        Assert.AreNotEqual(left, right);
        Assert.AreEqual(left, submission.Left.Second);
        Assert.AreEqual(left, submission.Left.Third);
        Assert.AreEqual(right, submission.Right.Second);
        Assert.AreEqual(right, submission.Right.Third);
    }

    [TestMethod]
    public void SameEpochUpdatesAdvanceButExactRetryDoesNotRewrite()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        ControllerFeedbackDelivery first = FrameDelivery(31, 1, 1_000);
        ControllerFeedbackDelivery second = FrameDelivery(31, 2, 2_000);

        Assert.IsTrue(sink.TryDeliver(first));
        Assert.IsTrue(sink.TryDeliver(first));
        Assert.AreEqual(1, writer.Calls,
            "An exact delivered retry must be idempotent.");
        Assert.IsTrue(sink.TryDeliver(second));
        Assert.AreEqual(2, writer.Calls);

        ControllerFeedbackDelivery regressed = FrameDelivery(31, 1, 3_000);
        Assert.IsFalse(sink.TryDeliver(regressed));
        Assert.AreEqual(2, writer.Calls);
    }

    [TestMethod]
    public void ImpulseReleasePresentationRefreshesExactCanonicalFrame()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11,
            Switch2HdRumbleFeedbackPolicy.
                SideLocalImpulseDualBandSaturating);
        ControllerFeedbackDelivery delivery = FrameDelivery(31, 1,
            bodyLow: 1, leftTrigger: 0, rightTrigger: 0);

        Assert.IsTrue(sink.TryStageImpulseReleasePresentation(
            delivery.Frame, 40_000, 20_000, 1));
        Assert.IsTrue(sink.TryDeliver(delivery));
        ushort firstLeft = writer.Last.Left.First.
            Oscillator0AmplitudeCode;
        ushort firstRight = writer.Last.Right.First.
            Oscillator0AmplitudeCode;
        ushort firstLeftFrequency = writer.Last.Left.First.
            Oscillator0ControlCode;
        ushort firstBodyAmplitude = writer.Last.Left.First.
            Oscillator1AmplitudeCode;
        Assert.IsTrue(firstLeft > firstRight);

        Assert.IsTrue(sink.TryStageImpulseReleasePresentation(
            delivery.Frame, 10_000, 5_000, 2));
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.AreEqual(2, writer.Calls);
        Assert.IsTrue(writer.Last.Left.First.Oscillator0AmplitudeCode <
            firstLeft);
        Assert.IsTrue(writer.Last.Right.First.Oscillator0AmplitudeCode <
            firstRight);
        Assert.IsTrue(writer.Last.Left.First.Oscillator0ControlCode <
            firstLeftFrequency,
            "Dynamic impulse frequency must follow the decaying strength.");
        Assert.AreEqual(firstBodyAmplitude,
            writer.Last.Left.First.Oscillator1AmplitudeCode,
            "The release overlay must not alter the canonical body motor.");

        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.AreEqual(2, writer.Calls,
            "An unchanged presentation revision remains idempotent.");
        Assert.IsFalse(sink.TryStageImpulseReleasePresentation(
            delivery.Frame, 1, 1, 2));
    }

    [TestMethod]
    public void UncertainImpulseRefreshRetriesExactPriorPresentation()
    {
        FakeWriter writer = new(7, 11)
        {
            Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
                Switch2HdRumblePhysicalWriteFailure.TransportRejected),
        };
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11,
            Switch2HdRumbleFeedbackPolicy.
                SideLocalImpulseDualBandSaturating);
        ControllerFeedbackDelivery delivery = FrameDelivery(31, 1,
            bodyLow: 1, leftTrigger: 0, rightTrigger: 0);
        Assert.IsTrue(sink.TryStageImpulseReleasePresentation(
            delivery.Frame, 40_000, 20_000, 1));
        Assert.IsFalse(sink.TryDeliver(delivery));
        Switch2HdRumbleGroup uncertainLeft = writer.Last.Left;
        Switch2HdRumbleGroup uncertainRight = writer.Last.Right;

        Assert.IsTrue(sink.TryStageImpulseReleasePresentation(
            delivery.Frame, 10_000, 5_000, 2));
        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.AreEqual(uncertainLeft, writer.Last.Left);
        Assert.AreEqual(uncertainRight, writer.Last.Right);

        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.AreEqual(3, writer.Calls);
        Assert.IsTrue(writer.Last.Left.First.Oscillator0AmplitudeCode <
            uncertainLeft.First.Oscillator0AmplitudeCode);
        Assert.IsTrue(writer.Last.Right.First.Oscillator0AmplitudeCode <
            uncertainRight.First.Oscillator0AmplitudeCode);
    }

    [TestMethod]
    public void StopIsOneLogicalNeutralAndFencesSuccessorEpoch()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        ControllerFeedbackDelivery frame = FrameDelivery(31, 1, 1_000);
        ControllerFeedbackDelivery stop = StopDelivery(31);

        Assert.IsTrue(sink.TryDeliver(frame));
        Assert.IsFalse(sink.TryDeliver(FrameDelivery(32, 1, 2_000)),
            "A successor must not overtake terminal neutral.");
        Assert.IsTrue(sink.TryDeliver(stop));
        Assert.AreEqual(2, writer.Calls);
        Assert.IsTrue(writer.Last.IsStop);
        Assert.IsTrue(writer.Last.IsNeutral);
        Assert.AreEqual(default(Switch2HdRumbleGroup), writer.Last.Left);
        Assert.AreEqual(default(Switch2HdRumbleGroup), writer.Last.Right);

        Assert.IsTrue(sink.TryDeliver(stop));
        Assert.AreEqual(2, writer.Calls,
            "A successful Stop retry is the same logical neutral.");
        Assert.IsFalse(sink.TryDeliver(FrameDelivery(31, 2, 2_000)),
            "A stopped epoch cannot publish another frame.");
        Assert.IsTrue(sink.TryDeliver(FrameDelivery(32, 1, 2_000)));
        Assert.AreEqual(3, writer.Calls);
    }

    [TestMethod]
    public void RejectedAndUncertainWritesRetainExactDeliveryForRetry()
    {
        FakeWriter writer = new(7, 11)
        {
            Result = Switch2HdRumblePhysicalWriteResult.Reject(
                Switch2HdRumblePhysicalWriteFailure.TransportRejected),
        };
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        ControllerFeedbackDelivery delivery = FrameDelivery(31, 1, 1_000);

        Assert.IsFalse(sink.TryDeliver(delivery));
        Assert.IsFalse(sink.HasUncertainWrite);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.
            TransportRejected, sink.LastFailure);
        writer.Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
            Switch2HdRumblePhysicalWriteFailure.TransportEnded);
        Assert.IsFalse(sink.TryDeliver(delivery));
        Assert.IsTrue(sink.HasUncertainWrite);
        Assert.AreEqual(2, writer.Calls);

        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.AreEqual(3, writer.Calls);
        Assert.IsFalse(sink.HasUncertainWrite);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.None,
            sink.LastFailure);
    }

    [TestMethod]
    public void LiveProfilePolicyAppliesOnlyToNewDeliveriesAndNotExactRetry()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        ControllerFeedbackDelivery disabled = FrameDelivery(31, 1,
            bodyLow: 0, leftTrigger: ushort.MaxValue);

        Assert.IsTrue(sink.TryDeliver(disabled));
        Assert.AreEqual(Switch2HdRumbleFeedbackFidelity.
            SdlBodyCompatibility, writer.Last.Fidelity);
        Assert.IsFalse(writer.Last.Left.First.HasNonzeroAmplitude);
        Assert.IsFalse(writer.Last.Right.First.HasNonzeroAmplitude);

        Assert.IsTrue(sink.TrySelectPolicy(Switch2HdRumbleFeedbackPolicy.
            SideLocalImpulseDualBandSaturating));
        ControllerFeedbackDelivery enabled = FrameDelivery(31, 2,
            bodyLow: 0, leftTrigger: ushort.MaxValue);
        writer.Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
            Switch2HdRumblePhysicalWriteFailure.TransportEnded);
        Assert.IsFalse(sink.TryDeliver(enabled));
        Switch2HdRumblePhysicalSubmission uncertain = writer.Last;
        Assert.AreEqual(Switch2HdRumbleFeedbackFidelity.
            SideLocalImpulseApproximation, uncertain.Fidelity);
        Assert.IsTrue(uncertain.Left.First.HasNonzeroAmplitude);
        Assert.IsFalse(uncertain.Right.First.HasNonzeroAmplitude);

        Assert.IsTrue(sink.TrySelectPolicy(Switch2HdRumbleFeedbackPolicy.
            SdlBodyOnlyCompatibility));
        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.TryDeliver(enabled));
        Assert.AreEqual(uncertain.Fidelity, writer.Last.Fidelity);
        Assert.AreEqual(uncertain.Left, writer.Last.Left);
        Assert.AreEqual(uncertain.Right, writer.Last.Right,
            "An exact uncertain retry must not be retranslated under the new profile policy.");

        Assert.IsTrue(sink.TryDeliver(FrameDelivery(31, 3,
            bodyLow: 0, leftTrigger: ushort.MaxValue)));
        Assert.AreEqual(Switch2HdRumbleFeedbackFidelity.
            SdlBodyCompatibility, writer.Last.Fidelity);
        Assert.IsFalse(writer.Last.Left.First.HasNonzeroAmplitude);
        Assert.IsFalse(writer.Last.Right.First.HasNonzeroAmplitude);
        Assert.IsFalse(sink.TrySelectPolicy(
            Switch2HdRumbleFeedbackPolicy.Invalid));
    }

    [TestMethod]
    public void LiveImpulseTuningRefreshesNewFramesButNotUncertainRetry()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        Assert.IsTrue(Switch2HdRumbleImpulseTuning.TryCreate(false, 1, 1,
            out var weakFixed));
        Assert.IsTrue(Switch2HdRumbleImpulseTuning.TryCreate(true, 10, 10,
            out var strongDynamic));
        Assert.IsTrue(sink.TrySelectConfiguration(
            Switch2HdRumbleFeedbackPolicy.
                SideLocalImpulseDualBandSaturating,
            weakFixed, out bool initialRefresh));
        Assert.IsTrue(initialRefresh);

        ControllerFeedbackDelivery first = FrameDelivery(31, 1,
            bodyLow: 0, leftTrigger: ushort.MaxValue);
        writer.Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
            Switch2HdRumblePhysicalWriteFailure.TransportEnded);
        Assert.IsFalse(sink.TryDeliver(first));
        Switch2HdRumblePhysicalSubmission uncertain = writer.Last;
        Assert.AreEqual((ushort)300,
            uncertain.Left.First.Oscillator0ControlCode);

        Assert.IsTrue(sink.TrySelectConfiguration(
            Switch2HdRumbleFeedbackPolicy.
                SideLocalImpulseDualBandSaturating,
            strongDynamic, out bool refreshRequired));
        Assert.IsTrue(refreshRequired);
        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.TryDeliver(first));
        Assert.AreEqual(uncertain.Left, writer.Last.Left,
            "An uncertain exact retry must retain its original tuning bytes.");

        Assert.IsTrue(sink.TryDeliver(FrameDelivery(31, 2,
            bodyLow: 0, leftTrigger: ushort.MaxValue)));
        Assert.AreEqual((ushort)481,
            writer.Last.Left.First.Oscillator0ControlCode);
        Assert.IsTrue(writer.Last.Left.First.Oscillator0AmplitudeCode >
            uncertain.Left.First.Oscillator0AmplitudeCode);

        Assert.IsTrue(sink.TrySelectConfiguration(
            Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
            weakFixed, out bool bodyOnlyRefresh));
        Assert.IsTrue(bodyOnlyRefresh);
        Assert.IsTrue(sink.TrySelectConfiguration(
            Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
            strongDynamic, out bool tuningOnlyBodyRefresh));
        Assert.IsTrue(tuningOnlyBodyRefresh,
            "The pending refresh remains required until a body-only frame is delivered.");
        Assert.IsTrue(sink.TryDeliver(FrameDelivery(31, 3,
            bodyLow: 1_000, leftTrigger: ushort.MaxValue)));
        Assert.IsTrue(sink.TrySelectConfiguration(
            Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
            weakFixed, out tuningOnlyBodyRefresh));
        Assert.IsFalse(tuningOnlyBodyRefresh,
            "Tuning is intentionally inert under body-only policy.");
    }

    [TestMethod]
    public void LiveBodyStrengthRefreshesNewFramesButNotUncertainRetry()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(50,
            out var quiet));
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(200, true, 2,
            out var boosted));
        Assert.IsTrue(sink.TrySelectConfiguration(
            Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
            Switch2HdRumbleImpulseTuning.Default, quiet,
            out bool initialRefresh));
        Assert.IsTrue(initialRefresh);

        ControllerFeedbackDelivery first = FrameDelivery(31, 1, 60_000);
        writer.Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
            Switch2HdRumblePhysicalWriteFailure.TransportEnded);
        Assert.IsFalse(sink.TryDeliver(first));
        Switch2HdRumblePhysicalSubmission uncertain = writer.Last;

        Assert.IsTrue(sink.TrySelectConfiguration(
            Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
            Switch2HdRumbleImpulseTuning.Default, boosted,
            out bool refreshRequired));
        Assert.IsTrue(refreshRequired);
        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.TryDeliver(first));
        Assert.AreEqual(uncertain.Left, writer.Last.Left,
            "An uncertain exact retry must retain its original body gain and carriers.");

        Assert.IsTrue(sink.TryDeliver(FrameDelivery(31, 2, 60_000)));
        Assert.IsTrue(writer.Last.Left.First.Oscillator1AmplitudeCode >
            uncertain.Left.First.Oscillator1AmplitudeCode);
        ushort expected = Switch2HdRumbleFeedbackTranslator.
            ScalePackedBodyAmplitude(
                Switch2HdRumbleFeedbackTranslator.
                    ScaleCanonicalAmplitude(60_000), boosted);
        Assert.AreEqual(expected,
            writer.Last.Left.First.Oscillator1AmplitudeCode);
        Assert.AreEqual((ushort)252,
            writer.Last.Left.First.Oscillator0ControlCode);
        Assert.AreEqual((ushort)225,
            writer.Last.Left.First.Oscillator1ControlCode);
    }

    [TestMethod]
    public void FirstUncertainFrameBlocksRetirementUntilExactRetrySucceeds()
    {
        FakeWriter writer = new(7, 11)
        {
            Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
                Switch2HdRumblePhysicalWriteFailure.TransportEnded),
        };
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        ControllerFeedbackDelivery delivery = FrameDelivery(31, 1, 1_000);

        Assert.IsFalse(sink.TryDeliver(delivery));
        Assert.IsTrue(sink.HasUncertainWrite);
        Assert.IsFalse(sink.TryRetire(),
            "A possibly-applied first write prevents lifetime retirement.");

        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.IsFalse(sink.HasUncertainWrite);
        Assert.AreEqual(2, writer.Calls);
    }

    [TestMethod]
    public void UncertainSuccessorEpochBlocksRetirementAndFurtherEpochs()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        Assert.IsTrue(sink.TryDeliver(FrameDelivery(31, 1, 1_000)));
        Assert.IsTrue(sink.TryDeliver(StopDelivery(31)));

        writer.Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
            Switch2HdRumblePhysicalWriteFailure.TransportEnded);
        Assert.IsFalse(sink.TryDeliver(FrameDelivery(32, 1, 2_000)));
        Assert.IsFalse(sink.TryRetire());
        Assert.IsFalse(sink.TryDeliver(FrameDelivery(33, 1, 3_000)),
            "A different epoch cannot overtake a possibly-applied frame.");
        Assert.AreEqual(3, writer.Calls);
    }

    [TestMethod]
    public void SameOwnerStopResolvesUncertainFrameAndPermitsRetirement()
    {
        FakeWriter writer = new(7, 11)
        {
            Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
                Switch2HdRumblePhysicalWriteFailure.TransportEnded),
        };
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        Assert.IsFalse(sink.TryDeliver(FrameDelivery(31, 1, 1_000)));

        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.TryDeliver(StopDelivery(31)));
        Assert.IsFalse(sink.HasUncertainWrite);
        Assert.IsTrue(writer.Last.IsNeutral);
        Assert.IsTrue(sink.TryRetire());
    }

    [TestMethod]
    public void NewerCompleteFrameFromSameOwnerResolvesUncertainty()
    {
        FakeWriter writer = new(7, 11)
        {
            Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
                Switch2HdRumblePhysicalWriteFailure.TransportEnded),
        };
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        Assert.IsFalse(sink.TryDeliver(FrameDelivery(31, 1, 1_000)));

        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.TryDeliver(FrameDelivery(31, 2, 2_000)));
        Assert.IsFalse(sink.HasUncertainWrite);
        Assert.AreEqual(2, writer.Calls);
        Assert.AreEqual((ulong)2, writer.Last.Sequence);
    }

    [TestMethod]
    public void UncertainStopRequiresExactRetryBeforeRetirement()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        ControllerFeedbackDelivery stop = StopDelivery(31);
        Assert.IsTrue(sink.TryDeliver(FrameDelivery(31, 1, 1_000)));

        writer.Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
            Switch2HdRumblePhysicalWriteFailure.TransportEnded);
        Assert.IsFalse(sink.TryDeliver(stop));
        Assert.IsFalse(sink.TryRetire());
        Assert.IsFalse(sink.TryDeliver(StopDelivery(31,
            ControllerFeedbackPublicationOrigin.ProfileEffect)));
        Assert.AreEqual(2, writer.Calls);

        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.TryDeliver(stop));
        Assert.IsFalse(sink.HasUncertainWrite);
        Assert.IsTrue(sink.TryRetire());
        Assert.AreEqual(3, writer.Calls);
    }

    [TestMethod]
    public void ProvenRejectionDoesNotEraseEarlierUncertainWrite()
    {
        FakeWriter writer = new(7, 11)
        {
            Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
                Switch2HdRumblePhysicalWriteFailure.TransportEnded),
        };
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        ControllerFeedbackDelivery first = FrameDelivery(31, 1, 1_000);
        Assert.IsFalse(sink.TryDeliver(first));

        writer.Result = Switch2HdRumblePhysicalWriteResult.Reject(
            Switch2HdRumblePhysicalWriteFailure.TransportRejected);
        Assert.IsFalse(sink.TryDeliver(FrameDelivery(31, 2, 2_000)));
        Assert.IsTrue(sink.HasUncertainWrite);
        Assert.IsFalse(sink.TryRetire());

        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.TryDeliver(first));
        Assert.IsFalse(sink.HasUncertainWrite);
        Assert.AreEqual(3, writer.Calls);
    }

    [TestMethod]
    public void StopMustAuthenticateLiveAndStoppedOwnerOrigin()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        ControllerFeedbackDelivery nativeStop = StopDelivery(31);
        ControllerFeedbackDelivery profileStop = StopDelivery(31,
            ControllerFeedbackPublicationOrigin.ProfileEffect);

        Assert.IsTrue(sink.TryDeliver(FrameDelivery(31, 1, 1_000)));
        Assert.IsFalse(sink.TryDeliver(profileStop));
        Assert.AreEqual(1, writer.Calls);
        Assert.IsTrue(sink.TryDeliver(nativeStop));
        Assert.IsFalse(sink.TryDeliver(profileStop));
        Assert.IsTrue(sink.TryDeliver(nativeStop));
        Assert.AreEqual(2, writer.Calls,
            "Only the exact successful Stop is idempotent.");
    }

    [TestMethod]
    public void AuthenticationExceptionsStayInsideTypedBoundary()
    {
        FakeWriter constructorWriter = new(7, 11)
        {
            ThrowAuthentication = true,
        };
        Assert.ThrowsException<ArgumentException>(() =>
            new Switch2HdRumbleDeliverySink(constructorWriter, 7, 11));

        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        writer.ThrowAuthentication = true;
        ControllerFeedbackDelivery delivery = FrameDelivery(31, 1, 1_000);
        Assert.IsFalse(sink.TryDeliver(delivery));
        Assert.AreEqual(0, writer.Calls);
        Assert.IsFalse(sink.HasUncertainWrite,
            "Authentication is specified as no-I/O and cannot imply actuation.");
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.DependencyThrew,
            sink.LastFailure);

        writer.ThrowAuthentication = false;
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.AreEqual(1, writer.Calls);
    }

    [TestMethod]
    public void SubmissionShapeRejectsContradictoryCommandAndFidelity()
    {
        Switch2HdRumbleSubframe nonzero = new(0x10, 1, 0x20, 2);
        Switch2HdRumbleSubframe controlsOnly = new(0x10, 0, 0x20, 0);
        Switch2HdRumbleGroup nonzeroGroup = new(nonzero, default, default);
        Switch2HdRumbleGroup controlsOnlyGroup = new(controlsOnly, default,
            default);

        Assert.IsFalse(TryCreateSubmission(ControllerFeedbackCommand.Apply,
            Switch2HdRumbleFeedbackFidelity.SdlLogicalNeutral,
            default, default));
        Assert.IsFalse(TryCreateSubmission(ControllerFeedbackCommand.Neutral,
            Switch2HdRumbleFeedbackFidelity.SdlBodyCompatibility,
            default, default));
        Assert.IsFalse(TryCreateSubmission(ControllerFeedbackCommand.Neutral,
            Switch2HdRumbleFeedbackFidelity.SdlLogicalNeutral,
            nonzeroGroup, default));
        Assert.IsFalse(TryCreateSubmission(
            (ControllerFeedbackCommand)byte.MaxValue,
            Switch2HdRumbleFeedbackFidelity.SdlBodyCompatibility,
            default, default));

        Assert.IsTrue(TryCreateSubmission(ControllerFeedbackCommand.Apply,
            Switch2HdRumbleFeedbackFidelity.SdlBodyCompatibility,
            default, default),
            "A small valid Apply may quantize to zero amplitude.");
        Assert.IsTrue(TryCreateSubmission(ControllerFeedbackCommand.Neutral,
            Switch2HdRumbleFeedbackFidelity.SdlLogicalNeutral,
            controlsOnlyGroup, controlsOnlyGroup),
            "Neutral may retain control codes but not amplitude.");
    }

    [TestMethod]
    public void DefaultResultAndWriterThrowAreUncertainNotSuccess()
    {
        FakeWriter writer = new(7, 11) { Result = default };
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        ControllerFeedbackDelivery delivery = FrameDelivery(31, 1, 1_000);

        Assert.IsFalse(sink.TryDeliver(delivery));
        Assert.IsTrue(sink.HasUncertainWrite);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.DependencyThrew,
            sink.LastFailure);

        writer.Throw = true;
        Assert.IsFalse(sink.TryDeliver(delivery));
        Assert.AreEqual(2, writer.Calls);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.DependencyThrew,
            sink.LastFailure);
    }

    [TestMethod]
    public void GenerationIdentityAndFreshnessFailClosedBeforePhysicalWrite()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);

        Assert.IsFalse(sink.TryDeliver(FrameDelivery(31, 1, 1_000,
            deviceGeneration: 8)));
        Assert.IsFalse(sink.TryDeliver(FrameDelivery(31, 1, 1_000,
            transportGeneration: 12)));
        Assert.IsFalse(sink.TryDeliver(default));
        Assert.AreEqual(0, writer.Calls);

        Assert.ThrowsException<ArgumentException>(() =>
            new Switch2HdRumbleDeliverySink(new FakeWriter(8, 11), 7, 11));
        writer.Authenticated = false;
        Assert.IsFalse(sink.TryDeliver(FrameDelivery(31, 1, 1_000)));
        Assert.AreEqual(0, writer.Calls);
    }

    [TestMethod]
    public void PhysicalWriterRunsOutsideGateAndOnlyOneCallCanBeInFlight()
    {
        BlockingWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        ControllerFeedbackDelivery delivery = FrameDelivery(31, 1, 1_000);

        Task<bool> first = Task.Run(() => sink.TryDeliver(delivery));
        Assert.IsTrue(writer.Entered.Wait(TimeSpan.FromSeconds(5)));
        Task<Switch2HdRumblePhysicalWriteFailure> gateProbe = Task.Run(
            () => sink.LastFailure);
        Assert.IsTrue(gateProbe.Wait(TimeSpan.FromSeconds(1)),
            "The external writer was invoked while holding the sink gate.");
        Assert.IsFalse(sink.TryDeliver(delivery));
        Assert.IsFalse(sink.TryRetire());
        writer.Release.Set();
        Assert.IsTrue(first.GetAwaiter().GetResult());
        Assert.AreEqual(1, writer.Calls);
        Assert.AreEqual(1, writer.MaximumConcurrent);
    }

    [TestMethod]
    public void RetirementRequiresTerminalNeutralAndThenClosesAdmission()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        Assert.IsTrue(sink.TryDeliver(FrameDelivery(31, 1, 1_000)));
        Assert.IsFalse(sink.TryRetire());
        Assert.IsTrue(sink.TryDeliver(StopDelivery(31)));
        Assert.IsTrue(sink.TryRetire());
        Assert.IsTrue(sink.IsRetired);
        Assert.IsFalse(sink.TryDeliver(FrameDelivery(32, 1, 2_000)));
    }

    [TestMethod]
    public void DeliveryHotPathAllocatesNothingAfterWarmup()
    {
        FakeWriter writer = new(7, 11);
        Switch2HdRumbleDeliverySink sink = new(writer, 7, 11);
        ulong epoch = 1;
        ulong sequence = 1;

        for (int index = 0; index < 128; index++)
        {
            ControllerFeedbackDelivery frame = FrameDelivery(epoch,
                sequence++, (ushort)(index + 1));
            Assert.IsTrue(sink.TryDeliver(frame));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool valid = true;
        for (int index = 0; index < 10_000; index++)
        {
            valid &= sink.TryDeliver(FrameDelivery(epoch, sequence++,
                (ushort)((index & 0x7FFF) + 1)));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated,
            $"Switch 2 feedback delivery allocated {allocated} bytes.");
    }

    private static ControllerFeedbackDelivery FrameDelivery(
        ulong deliveryEpoch, ulong sequence, ushort bodyLow,
        ushort bodyHigh = 0, ushort leftTrigger = 0,
        ushort rightTrigger = 0, ulong deviceGeneration = 7,
        ulong transportGeneration = 11)
    {
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(
            out ulong now));
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            ControllerFeedbackCommand.Apply,
            ControllerFeedbackActuators.All, bodyLow, bodyHigh,
            leftTrigger, rightTrigger, sequence, deviceGeneration,
            transportGeneration, ownershipEpoch: 19,
            timestampMicroseconds: now,
            timeToLiveMicroseconds: 250_000,
            out ControllerFeedbackFrame frame));
        return new ControllerFeedbackDelivery(
            ControllerFeedbackDeliveryDisposition.Frame,
            ControllerFeedbackPublicationOrigin.NativeGame, frame,
            deviceGeneration, transportGeneration, deliveryEpoch);
    }

    private static ControllerFeedbackDelivery StopDelivery(
        ulong deliveryEpoch,
        ControllerFeedbackPublicationOrigin origin =
            ControllerFeedbackPublicationOrigin.NativeGame) => new(
            ControllerFeedbackDeliveryDisposition.Stop,
            origin, default,
            deviceGeneration: 7, transportGeneration: 11, deliveryEpoch);

    private static bool TryCreateSubmission(ControllerFeedbackCommand command,
        Switch2HdRumbleFeedbackFidelity fidelity,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right)
    {
        Switch2HdRumbleFeedbackSynthesis synthesis = new(
            ControllerFeedbackSource.XboxOneVirtualDevice, command, fidelity,
            left, right, sequence: 1, deviceGeneration: 7,
            transportGeneration: 11, ownershipEpoch: 19,
            timestampMicroseconds: 1, timeToLiveMicroseconds: 250_000);
        return Switch2HdRumblePhysicalSubmission.TryCreateFrame(synthesis,
            deliveryEpoch: 31, out _);
    }

    private sealed class FakeWriter : ISwitch2HdRumblePhysicalWriter
    {
        private readonly ulong deviceGeneration;
        private readonly ulong transportGeneration;

        internal FakeWriter(ulong deviceGeneration,
            ulong transportGeneration)
        {
            this.deviceGeneration = deviceGeneration;
            this.transportGeneration = transportGeneration;
        }

        internal bool Authenticated = true;
        internal bool ThrowAuthentication;
        internal bool Throw;
        internal int Calls;
        internal Switch2HdRumblePhysicalSubmission Last;
        internal Switch2HdRumblePhysicalWriteResult Result =
            Switch2HdRumblePhysicalWriteResult.Success();

        public bool Authenticates(ulong candidateDeviceGeneration,
            ulong candidateTransportGeneration)
        {
            if (ThrowAuthentication)
            {
                throw new InvalidOperationException(
                    "injected authentication failure");
            }
            return Authenticated &&
                candidateDeviceGeneration == deviceGeneration &&
                candidateTransportGeneration == transportGeneration;
        }

        public Switch2HdRumblePhysicalWriteResult TryWrite(
            in Switch2HdRumblePhysicalSubmission submission)
        {
            Calls++;
            Last = submission;
            if (Throw)
            {
                throw new InvalidOperationException("injected");
            }
            return Result;
        }
    }

    private sealed class BlockingWriter : ISwitch2HdRumblePhysicalWriter
    {
        private readonly ulong deviceGeneration;
        private readonly ulong transportGeneration;
        private int concurrent;

        internal BlockingWriter(ulong deviceGeneration,
            ulong transportGeneration)
        {
            this.deviceGeneration = deviceGeneration;
            this.transportGeneration = transportGeneration;
        }

        internal readonly ManualResetEventSlim Entered = new(false);
        internal readonly ManualResetEventSlim Release = new(false);
        internal int Calls;
        internal int MaximumConcurrent;

        public bool Authenticates(ulong candidateDeviceGeneration,
            ulong candidateTransportGeneration) =>
            candidateDeviceGeneration == deviceGeneration &&
            candidateTransportGeneration == transportGeneration;

        public Switch2HdRumblePhysicalWriteResult TryWrite(
            in Switch2HdRumblePhysicalSubmission submission)
        {
            int active = Interlocked.Increment(ref concurrent);
            MaximumConcurrent = Math.Max(MaximumConcurrent, active);
            Interlocked.Increment(ref Calls);
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Decrement(ref concurrent);
            return Switch2HdRumblePhysicalWriteResult.Success();
        }
    }
}
