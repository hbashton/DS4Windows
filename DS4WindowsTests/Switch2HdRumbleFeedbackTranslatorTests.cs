using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2HdRumbleFeedbackTranslatorTests
{
    [TestMethod]
    public void BodyOnlyPolicyDoesNotApproximateImpulseTriggerChannels()
    {
        ControllerFeedbackFrame frame = CreateFrame(
            ControllerFeedbackCommand.Apply, bodyLow: 10_000,
            bodyHigh: 20_000, leftTrigger: 50_000,
            rightTrigger: 60_000);

        Assert.IsTrue(Switch2HdRumbleFeedbackTranslator.TryTranslate(frame,
            Now, Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
            out Switch2HdRumbleFeedbackSynthesis synthesis));
        Assert.AreEqual(Switch2HdRumbleFeedbackFidelity.
            SdlBodyCompatibility, synthesis.Fidelity);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
            ScaleCanonicalAmplitude(20_000),
            synthesis.Left.First.Oscillator0AmplitudeCode);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
            ScaleCanonicalAmplitude(10_000),
            synthesis.Left.First.Oscillator1AmplitudeCode);
        Assert.AreEqual(synthesis.Left, synthesis.Right);
    }

    private const ulong Now = 1_000_000;

    [TestMethod]
    public void FourActuatorBasisVectorsPreserveEncodedGroupSidedness()
    {
        AssertBasis(bodyLow: ushort.MaxValue,
            expectedLeftLow: ushort.MaxValue,
            expectedRightLow: ushort.MaxValue);
        AssertBasis(bodyHigh: ushort.MaxValue,
            expectedLeftHigh: ushort.MaxValue,
            expectedRightHigh: ushort.MaxValue);
        AssertBasis(leftTrigger: ushort.MaxValue,
            expectedLeftHigh: ushort.MaxValue);
        AssertBasis(rightTrigger: ushort.MaxValue,
            expectedRightHigh: ushort.MaxValue);
    }

    [TestMethod]
    public void OverlappingBodyAndImpulsePreserveBothWithHeadroom()
    {
        ControllerFeedbackFrame frame = CreateFrame(
            bodyLow: 50_000, bodyHigh: 40_000,
            leftTrigger: 60_000, rightTrigger: 30_000);

        Assert.IsTrue(TryTranslate(frame, out var result));
        Assert.AreEqual(Switch2HdRumbleFeedbackFidelity.
            SideLocalImpulseApproximation, result.Fidelity);
        AssertImpulseSubframe(result.Left.First, bodyHigh: 40_000,
            bodyLow: 50_000, trigger: 60_000,
            Switch2HdRumbleImpulseTuning.Default);
        AssertImpulseSubframe(result.Right.First, bodyHigh: 40_000,
            bodyLow: 50_000, trigger: 30_000,
            Switch2HdRumbleImpulseTuning.Default);
        Assert.AreEqual(result.Left.First, result.Left.Second);
        Assert.AreEqual(result.Left.First, result.Left.Third);
        Assert.AreEqual(result.Right.First, result.Right.Second);
        Assert.AreEqual(result.Right.First, result.Right.Third);
    }

    [TestMethod]
    public void BodyOnlyMatchesSdlCompatibilityBasisOnBothSides()
    {
        ControllerFeedbackFrame frame = CreateFrame(bodyLow: 12_345,
            bodyHigh: 54_321);

        Assert.IsTrue(TryTranslate(frame, out var result));
        Assert.AreEqual(Switch2HdRumbleFeedbackFidelity.
            SdlBodyCompatibility, result.Fidelity);
        Assert.AreEqual(result.Left, result.Right);
        AssertSubframe(result.Left.First, high: 54_321, low: 12_345);
    }

    [TestMethod]
    public void NeutralAndStopProduceZeroAmplitudeBasisAndRetainFences()
    {
        foreach (ControllerFeedbackCommand command in new[]
            {
                ControllerFeedbackCommand.Neutral,
                ControllerFeedbackCommand.Stop,
            })
        {
            ControllerFeedbackFrame frame = CreateFrame(command: command);
            Assert.IsTrue(TryTranslate(frame, out var result));
            Assert.AreEqual(Switch2HdRumbleFeedbackFidelity.SdlLogicalNeutral,
                result.Fidelity);
            Assert.AreEqual(result.Left, result.Right);
            AssertSubframe(result.Left.First, high: 0, low: 0);
            Assert.IsFalse(result.Left.First.HasNonzeroAmplitude);
            Assert.AreEqual(result.Left.First, result.Left.Second);
            Assert.AreEqual(result.Left.First, result.Left.Third);
            Assert.AreEqual(frame.Sequence, result.Sequence);
            Assert.AreEqual(frame.DeviceGeneration, result.DeviceGeneration);
            Assert.AreEqual(frame.TransportGeneration,
                result.TransportGeneration);
            Assert.AreEqual(frame.OwnershipEpoch, result.OwnershipEpoch);
            Assert.AreEqual(frame.Source, result.Source);
            Assert.AreEqual(frame.TimestampMicroseconds,
                result.TimestampMicroseconds);
            Assert.AreEqual(frame.TimeToLiveMicroseconds,
                result.TimeToLiveMicroseconds);
            Assert.IsTrue(result.IsFreshAt(Now));
            Assert.AreEqual(command == ControllerFeedbackCommand.Stop,
                result.IsStop);
            Assert.IsTrue(result.IsNeutral);
        }
    }

    [TestMethod]
    public void ScalingPreservesLicensedIntegerTruncationAndCeiling()
    {
        ushort[] values = { 0, 1, 63, 64, 1_000, 32_768, 65_534, 65_535 };
        foreach (ushort value in values)
        {
            ushort expected = (ushort)(((uint)value * 29_000 / 65_535) >> 6);
            Assert.AreEqual(expected,
                Switch2HdRumbleFeedbackTranslator.
                    ScaleCanonicalAmplitude(value), value.ToString());
        }
        Assert.AreEqual((ushort)453,
            Switch2HdRumbleFeedbackTranslator.
                MaximumPackedCompatibilityAmplitude);
    }

    [TestMethod]
    public void ImpulseTuningHasStrictBoundsAndMonotonicFrequency()
    {
        Assert.IsFalse(Switch2HdRumbleImpulseTuning.TryCreate(true, 0, 5,
            out _));
        Assert.IsFalse(Switch2HdRumbleImpulseTuning.TryCreate(true, 10, 11,
            out _));
        Assert.IsTrue(Switch2HdRumbleImpulseTuning.TryCreate(true, 10, 5,
            out var dynamic));
        Assert.AreEqual((ushort)0, Switch2HdRumbleFeedbackTranslator.
            GetImpulseHighFrequency(0, dynamic));
        Assert.AreEqual((ushort)300, Switch2HdRumbleFeedbackTranslator.
            GetImpulseHighFrequency(1, dynamic));
        Assert.AreEqual((ushort)481, Switch2HdRumbleFeedbackTranslator.
            GetImpulseHighFrequency(ushort.MaxValue, dynamic));

        ushort previous = 0;
        for (int value = 1; value <= ushort.MaxValue; value++)
        {
            ushort current = Switch2HdRumbleFeedbackTranslator.
                GetImpulseHighFrequency((ushort)value, dynamic);
            Assert.IsTrue(current >= previous, value.ToString());
            previous = current;
        }

        Assert.IsTrue(Switch2HdRumbleImpulseTuning.TryCreate(false, 1, 5,
            out var fixedLow));
        Assert.IsTrue(Switch2HdRumbleImpulseTuning.TryCreate(false, 10, 5,
            out var fixedHigh));
        Assert.AreEqual((ushort)300, Switch2HdRumbleFeedbackTranslator.
            GetImpulseHighFrequency(1, fixedLow));
        Assert.AreEqual((ushort)481, Switch2HdRumbleFeedbackTranslator.
            GetImpulseHighFrequency(1, fixedHigh));
    }

    [TestMethod]
    public void ImpulseStrengthScalesDefaultBasisWithoutOverflow()
    {
        Assert.IsTrue(Switch2HdRumbleImpulseTuning.TryCreate(true, 10, 1,
            out var weak));
        Assert.IsTrue(Switch2HdRumbleImpulseTuning.TryCreate(true, 10, 5,
            out var normal));
        Assert.IsTrue(Switch2HdRumbleImpulseTuning.TryCreate(true, 10, 10,
            out var strong));
        ushort basis = Switch2HdRumbleFeedbackTranslator.
            ScaleCanonicalAmplitude(ushort.MaxValue);
        Assert.AreEqual((ushort)((basis + 2) / 5),
            Switch2HdRumbleFeedbackTranslator.ScaleImpulseAmplitude(
                ushort.MaxValue, weak));
        Assert.AreEqual(basis, Switch2HdRumbleFeedbackTranslator.
            ScaleImpulseAmplitude(ushort.MaxValue, normal));
        Assert.AreEqual((ushort)(basis * 2),
            Switch2HdRumbleFeedbackTranslator.ScaleImpulseAmplitude(
                ushort.MaxValue, strong));
        Assert.IsTrue(Switch2HdRumbleFeedbackTranslator.
            ScaleImpulseAmplitude(ushort.MaxValue, strong) <=
            Switch2HdRumbleFeedbackTranslator.MaximumPackedAmplitude);
    }

    [TestMethod]
    public void BodyTuningUsesExistingRumbleBoostBoundsAndDefault()
    {
        Assert.IsFalse(default(Switch2HdRumbleBodyTuning).IsValid);
        Assert.IsFalse(Switch2HdRumbleBodyTuning.TryCreate(-1, out _));
        Assert.IsFalse(Switch2HdRumbleBodyTuning.TryCreate(201, out _));
        Assert.IsFalse(Switch2HdRumbleBodyTuning.TryCreate(100, true, 0,
            out _));
        Assert.IsFalse(Switch2HdRumbleBodyTuning.TryCreate(100, true, 11,
            out _));
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(0,
            out var muted));
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(100,
            out var normal));
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(200,
            out var boosted));
        Assert.AreEqual((byte)0, muted.StrengthPercent);
        Assert.IsTrue(muted.IsValid);
        Assert.AreNotEqual(default(Switch2HdRumbleBodyTuning), muted);
        Assert.AreEqual(Switch2HdRumbleBodyTuning.Default, normal);
        Assert.AreEqual((byte)200, boosted.StrengthPercent);
        Assert.IsFalse(normal.XboxCarrierMode);
        Assert.AreEqual((byte)10, normal.XboxFrequencyLevel);
    }

    [TestMethod]
    public void XboxBodyFrequencyMatchesPinnedReferenceIntegerLaw()
    {
        ushort[] expected = { 241, 252, 264, 276, 288, 300, 312, 323,
            335, 347 };
        for (int level = 1; level <= 10; level++)
        {
            Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(100, true,
                level, out var tuning));
            Assert.IsTrue(tuning.XboxCarrierMode);
            Assert.AreEqual((byte)level, tuning.XboxFrequencyLevel);
            Assert.AreEqual(expected[level - 1],
                Switch2HdRumbleFeedbackTranslator.
                    GetXboxBodyHighControlCode(tuning));
            Assert.AreEqual(
                Switch2HdRumbleFeedbackTranslator.XboxBodyLowControlCode,
                Switch2HdRumbleFeedbackTranslator.
                    GetBodyLowControlCode(tuning));
        }

        Assert.AreEqual(
            Switch2HdRumbleFeedbackTranslator.XboxBodyHighControlMinimum,
            expected[0]);
        Assert.AreEqual(
            Switch2HdRumbleFeedbackTranslator.XboxBodyHighControlMaximum,
            expected[^1]);
    }

    [TestMethod]
    public void XboxBodyModeChangesCarriersWithoutFlatteningImpulseCarrier()
    {
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(100, true, 4,
            out var xbox));
        ControllerFeedbackFrame body = CreateFrame(bodyLow: 30_000,
            bodyHigh: 40_000);
        Assert.IsTrue(Switch2HdRumbleFeedbackTranslator.TryTranslate(body,
            Now, Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
            Switch2HdRumbleImpulseTuning.Default, xbox, out var bodyResult));
        Assert.AreEqual((ushort)276,
            bodyResult.Left.First.Oscillator0ControlCode);
        Assert.AreEqual((ushort)225,
            bodyResult.Left.First.Oscillator1ControlCode);

        ControllerFeedbackFrame impulse = CreateFrame(bodyLow: 30_000,
            bodyHigh: 40_000, leftTrigger: 50_000);
        Assert.IsTrue(Switch2HdRumbleFeedbackTranslator.TryTranslate(impulse,
            Now, Switch2HdRumbleFeedbackPolicy.
                SideLocalImpulseDualBandSaturating,
            Switch2HdRumbleImpulseTuning.Default, xbox,
            out var impulseResult));
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
                GetImpulseHighFrequency(50_000,
                    Switch2HdRumbleImpulseTuning.Default),
            impulseResult.Left.First.Oscillator0ControlCode);
        Assert.AreEqual((ushort)276,
            impulseResult.Right.First.Oscillator0ControlCode);
        Assert.AreEqual((ushort)225,
            impulseResult.Left.First.Oscillator1ControlCode);
    }

    [TestMethod]
    public void BodyStrengthScalesAndSaturatesWithoutChangingImpulseGain()
    {
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(0,
            out var muted));
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(200,
            out var boosted));
        ControllerFeedbackFrame frame = CreateFrame(
            bodyLow: ushort.MaxValue, bodyHigh: ushort.MaxValue,
            leftTrigger: ushort.MaxValue);

        Assert.IsTrue(Switch2HdRumbleFeedbackTranslator.TryTranslate(frame,
            Now, Switch2HdRumbleFeedbackPolicy.
                SideLocalImpulseDualBandSaturating,
            Switch2HdRumbleImpulseTuning.Default, muted,
            out var mutedResult));
        ushort impulse = Switch2HdRumbleFeedbackTranslator.
            ScaleImpulseAmplitude(ushort.MaxValue,
                Switch2HdRumbleImpulseTuning.Default);
        Assert.AreEqual((ushort)0,
            mutedResult.Left.First.Oscillator1AmplitudeCode);
        Assert.AreEqual(impulse,
            mutedResult.Left.First.Oscillator0AmplitudeCode,
            "Body gain must not silently disable the independent impulse lane.");
        Assert.AreEqual((ushort)0,
            mutedResult.Right.First.Oscillator0AmplitudeCode);

        Assert.IsTrue(Switch2HdRumbleFeedbackTranslator.TryTranslate(frame,
            Now, Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
            Switch2HdRumbleImpulseTuning.Default, boosted,
            out var boostedResult));
        ushort expected = (ushort)Math.Min(
            (int)Switch2HdRumbleFeedbackTranslator.MaximumPackedAmplitude,
            Switch2HdRumbleFeedbackTranslator.
                MaximumPackedCompatibilityAmplitude * 2);
        Assert.AreEqual(expected,
            boostedResult.Left.First.Oscillator0AmplitudeCode);
        Assert.AreEqual(expected,
            boostedResult.Left.First.Oscillator1AmplitudeCode);
    }

    [TestMethod]
    public void SourcePreservedScalingKeepsFrequenciesAndTemporalSubframes()
    {
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(50,
            out var tuning));
        var first = new Switch2HdRumbleSubframe(101, 100, 201, 200);
        var second = new Switch2HdRumbleSubframe(102, 300, 202, 400);
        var third = new Switch2HdRumbleSubframe(103, 1_023, 203, 1);
        var source = new Switch2HdRumbleGroup(first, second, third);

        Switch2HdRumbleGroup scaled = Switch2HdRumbleFeedbackTranslator.
            ScaleSourcePreservedGroup(source, tuning);

        Assert.AreEqual((ushort)101,
            scaled.First.Oscillator0ControlCode);
        Assert.AreEqual((ushort)201,
            scaled.First.Oscillator1ControlCode);
        Assert.AreEqual((ushort)50,
            scaled.First.Oscillator0AmplitudeCode);
        Assert.AreEqual((ushort)100,
            scaled.First.Oscillator1AmplitudeCode);
        Assert.AreEqual((ushort)102,
            scaled.Second.Oscillator0ControlCode);
        Assert.AreEqual((ushort)150,
            scaled.Second.Oscillator0AmplitudeCode);
        Assert.AreEqual((ushort)200,
            scaled.Second.Oscillator1AmplitudeCode);
        Assert.AreEqual((ushort)103,
            scaled.Third.Oscillator0ControlCode);
        Assert.AreEqual((ushort)512,
            scaled.Third.Oscillator0AmplitudeCode);
        Assert.AreEqual((ushort)1,
            scaled.Third.Oscillator1AmplitudeCode);

        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(50, true, 6,
            out var xbox));
        Switch2HdRumbleGroup xboxScaled = Switch2HdRumbleFeedbackTranslator.
            ScaleSourcePreservedGroup(source, xbox);
        foreach (Switch2HdRumbleSubframe subframe in new[]
            {
                xboxScaled.First,
                xboxScaled.Second,
                xboxScaled.Third,
            })
        {
            Assert.AreEqual((ushort)300,
                subframe.Oscillator0ControlCode);
            Assert.AreEqual((ushort)225,
                subframe.Oscillator1ControlCode);
        }
        Assert.AreEqual(scaled.First.Oscillator0AmplitudeCode,
            xboxScaled.First.Oscillator0AmplitudeCode);
        Assert.AreEqual(scaled.Second.Oscillator1AmplitudeCode,
            xboxScaled.Second.Oscillator1AmplitudeCode);
        Assert.AreEqual(scaled.Third.Oscillator0AmplitudeCode,
            xboxScaled.Third.Oscillator0AmplitudeCode);
    }

    [TestMethod]
    public void PackedAmplitudeAdditionSaturatesWithoutWrap()
    {
        Assert.AreEqual((ushort)0,
            Switch2HdRumbleFeedbackTranslator.
                AddPackedAmplitudesSaturating(0, 0));
        Assert.AreEqual((ushort)1_000,
            Switch2HdRumbleFeedbackTranslator.
                AddPackedAmplitudesSaturating(400, 600));
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
                MaximumPackedAmplitude,
            Switch2HdRumbleFeedbackTranslator.
                AddPackedAmplitudesSaturating(900, 900));
    }

    [TestMethod]
    public void EveryCanonicalAmplitudeMatchesLicensedIntegerFormula()
    {
        for (int value = 0; value <= ushort.MaxValue; value++)
        {
            ushort expected = (ushort)(((uint)value * 29_000 / 65_535) >> 6);
            Assert.AreEqual(expected,
                Switch2HdRumbleFeedbackTranslator.
                    ScaleCanonicalAmplitude((ushort)value),
                value.ToString());
        }
    }

    [TestMethod]
    public void BodyLowBasisHasFixedTranslatorToUsbReportGolden()
    {
        ControllerFeedbackFrame frame = CreateFrame(
            bodyLow: ushort.MaxValue);
        Assert.IsTrue(TryTranslate(frame, out var result));
        Span<byte> report = stackalloc byte[
            Switch2UsbHdRumbleCodec.ReportLength];
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryEncodeProController(0,
            result.Left, result.Right, report));
        Assert.AreEqual(
            "0250870120517187012051718701205171508701205171870120517187012051" +
            "7100000000000000000000000000000000000000000000000000000000000000",
            Convert.ToHexString(report));
    }

    [TestMethod]
    public void InvalidPolicyMalformedAndExpiredFramesFailClosed()
    {
        ControllerFeedbackFrame frame = CreateFrame(bodyLow: 1);
        Assert.IsFalse(Switch2HdRumbleFeedbackTranslator.TryTranslate(frame,
            Now, Switch2HdRumbleFeedbackPolicy.Invalid, out _));
        Assert.IsFalse(Switch2HdRumbleFeedbackTranslator.TryTranslate(
            default, Now,
            Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating,
            out _));
        Assert.IsFalse(Switch2HdRumbleFeedbackTranslator.TryTranslate(frame,
            Now + frame.TimeToLiveMicroseconds,
            Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating,
            out _));
    }

    [TestMethod]
    public void SynthesisMustBeRevalidatedAtActualDeliveryBoundary()
    {
        ControllerFeedbackFrame frame = CreateFrame(bodyLow: 1);
        Assert.IsTrue(TryTranslate(frame, out var result));
        Assert.IsTrue(result.IsFreshAt(Now +
            frame.TimeToLiveMicroseconds - 1));
        Assert.IsFalse(result.IsFreshAt(Now +
            frame.TimeToLiveMicroseconds));

        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            ControllerFeedbackCommand.Apply,
            ControllerFeedbackActuators.All, 1, 0, 0, 0,
            sequence: 21, deviceGeneration: 22,
            transportGeneration: 23, ownershipEpoch: 24,
            timestampMicroseconds: Now +
                ControllerFeedbackFrame.MaxFutureSkewMicroseconds,
            timeToLiveMicroseconds: 10_000,
            out ControllerFeedbackFrame future));
        Assert.IsTrue(TryTranslate(future, out var futureResult));
        Assert.IsTrue(futureResult.IsFreshAt(Now));
        Assert.IsFalse(futureResult.IsFreshAt(Now - 1));
    }

    [TestMethod]
    public void TranslateAndEncodeSteadyStateAllocateNothing()
    {
        ControllerFeedbackFrame frame = CreateFrame(bodyLow: 40_000,
            bodyHigh: 30_000, leftTrigger: 20_000, rightTrigger: 10_000);
        Span<byte> report = stackalloc byte[
            Switch2UsbHdRumbleCodec.ReportLength];
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(175, true, 7,
            out var bodyTuning));
        for (int index = 0; index < 1_000; index++)
        {
            Switch2HdRumbleFeedbackTranslator.TryTranslate(frame, Now,
                Switch2HdRumbleFeedbackPolicy.
                    SideLocalImpulseDualBandSaturating,
                Switch2HdRumbleImpulseTuning.Default, bodyTuning,
                out var warm);
            Switch2UsbHdRumbleCodec.TryEncodeProController(1, warm.Left,
                warm.Right, report);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool valid = true;
        for (int index = 0; index < 10_000; index++)
        {
            valid &= Switch2HdRumbleFeedbackTranslator.TryTranslate(frame,
                Now, Switch2HdRumbleFeedbackPolicy.
                    SideLocalImpulseDualBandSaturating,
                Switch2HdRumbleImpulseTuning.Default, bodyTuning,
                out var result);
            valid &= Switch2UsbHdRumbleCodec.TryEncodeProController(1,
                result.Left, result.Right, report);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
    }

    private static void AssertBasis(ushort bodyLow = 0,
        ushort bodyHigh = 0, ushort leftTrigger = 0,
        ushort rightTrigger = 0, ushort expectedLeftLow = 0,
        ushort expectedLeftHigh = 0, ushort expectedRightLow = 0,
        ushort expectedRightHigh = 0)
    {
        ControllerFeedbackFrame frame = CreateFrame(bodyLow: bodyLow,
            bodyHigh: bodyHigh, leftTrigger: leftTrigger,
            rightTrigger: rightTrigger);
        Assert.IsTrue(TryTranslate(frame, out var result));
        Assert.AreEqual(
            Switch2HdRumbleFeedbackTranslator.ScaleCanonicalAmplitude(
                expectedLeftHigh),
            result.Left.First.Oscillator0AmplitudeCode);
        Assert.AreEqual(
            Switch2HdRumbleFeedbackTranslator.ScaleCanonicalAmplitude(
                expectedLeftLow),
            result.Left.First.Oscillator1AmplitudeCode);
        Assert.AreEqual(
            Switch2HdRumbleFeedbackTranslator.ScaleCanonicalAmplitude(
                expectedRightHigh),
            result.Right.First.Oscillator0AmplitudeCode);
        Assert.AreEqual(
            Switch2HdRumbleFeedbackTranslator.ScaleCanonicalAmplitude(
                expectedRightLow),
            result.Right.First.Oscillator1AmplitudeCode);
        Assert.AreEqual(leftTrigger == 0 ?
                Switch2HdRumbleFeedbackTranslator.SdlHighControlCode :
                Switch2HdRumbleFeedbackTranslator.GetImpulseHighFrequency(
                    leftTrigger, Switch2HdRumbleImpulseTuning.Default),
            result.Left.First.Oscillator0ControlCode);
        Assert.AreEqual(rightTrigger == 0 ?
                Switch2HdRumbleFeedbackTranslator.SdlHighControlCode :
                Switch2HdRumbleFeedbackTranslator.GetImpulseHighFrequency(
                    rightTrigger, Switch2HdRumbleImpulseTuning.Default),
            result.Right.First.Oscillator0ControlCode);
    }

    private static void AssertSubframe(Switch2HdRumbleSubframe frame,
        ushort high, ushort low)
    {
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.SdlHighControlCode,
            frame.Oscillator0ControlCode);
        Assert.AreEqual(
            Switch2HdRumbleFeedbackTranslator.ScaleCanonicalAmplitude(high),
            frame.Oscillator0AmplitudeCode);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.SdlLowControlCode,
            frame.Oscillator1ControlCode);
        Assert.AreEqual(
            Switch2HdRumbleFeedbackTranslator.ScaleCanonicalAmplitude(low),
            frame.Oscillator1AmplitudeCode);
    }

    private static void AssertImpulseSubframe(
        Switch2HdRumbleSubframe frame, ushort bodyHigh, ushort bodyLow,
        ushort trigger, in Switch2HdRumbleImpulseTuning tuning)
    {
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
            GetImpulseHighFrequency(trigger, tuning),
            frame.Oscillator0ControlCode);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
                MixPackedAmplitudesWithHeadroom(
                    Switch2HdRumbleFeedbackTranslator.
                        ScaleCanonicalAmplitude(bodyHigh),
                    Switch2HdRumbleFeedbackTranslator.
                        ScaleImpulseAmplitude(trigger, tuning)),
            frame.Oscillator0AmplitudeCode);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.SdlLowControlCode,
            frame.Oscillator1ControlCode);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
            ScaleCanonicalAmplitude(bodyLow),
            frame.Oscillator1AmplitudeCode);
    }

    private static bool TryTranslate(in ControllerFeedbackFrame frame,
        out Switch2HdRumbleFeedbackSynthesis result) =>
        Switch2HdRumbleFeedbackTranslator.TryTranslate(frame, Now,
            Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating,
            out result);

    private static ControllerFeedbackFrame CreateFrame(
        ControllerFeedbackCommand command = ControllerFeedbackCommand.Apply,
        ushort bodyLow = 0, ushort bodyHigh = 0, ushort leftTrigger = 0,
        ushort rightTrigger = 0)
    {
        if (command == ControllerFeedbackCommand.Apply && bodyLow == 0 &&
            bodyHigh == 0 && leftTrigger == 0 && rightTrigger == 0)
        {
            bodyLow = 1;
        }
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
            ControllerFeedbackSource.XboxSeriesVirtualDevice, command,
            ControllerFeedbackActuators.All, bodyLow, bodyHigh,
            leftTrigger, rightTrigger, sequence: 11, deviceGeneration: 12,
            transportGeneration: 13, ownershipEpoch: 14,
            timestampMicroseconds: Now, timeToLiveMicroseconds: 10_000,
            out ControllerFeedbackFrame frame));
        return frame;
    }
}
