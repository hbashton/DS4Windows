using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests;

[TestClass]
public sealed class LegacyJoyConHdRumbleTests
{
    private static Switch2HdRumbleGroup Wave(ushort high = 300, ushort low = 100,
        ushort highFrequency = 320, ushort lowFrequency = 160)
    {
        var frame = new Switch2HdRumbleSubframe(highFrequency, high, lowFrequency, low);
        return new(frame, frame, frame);
    }

    [DataTestMethod]
    [DataRow(true, 10)]
    [DataRow(false, 10)]
    [DataRow(true, 64)]
    [DataRow(false, 64)]
    public void OriginalTransportEncodesDocumentedIndependentBandsAndOnlySelectedActuator(bool left, int length)
    {
        var bytes = new byte[length];
        LegacyJoyConHdRumble.Encode(Wave(), left, bytes);
        int offset = left ? 2 : 6;
        Assert.AreEqual((byte)0x10, bytes[0]);
        Assert.AreEqual((byte)0, bytes[1], "Only the existing native writer assigns packet counters.");
        Assert.AreEqual((byte)0, bytes[offset]); // 320 Hz: HF=0x0100
        Assert.AreEqual(1, bytes[offset + 1] & 1);
        Assert.AreEqual(0x40, bytes[offset + 2] & 0x7F); // 160 Hz LF=0x40
        Assert.AreEqual(JoyConDevice.GetHdRumbleAmplitude(300).high, bytes[offset + 1] & 0xFE);
        Assert.AreEqual(JoyConDevice.GetHdRumbleAmplitude(100).low & 0xFF, bytes[offset + 3]);
        CollectionAssert.AreEqual(new byte[] { 0, 1, 0x60, 0x40 }, bytes[(left ? 6 : 2)..(left ? 10 : 6)]);
        Assert.IsTrue(bytes.Skip(10).All(value => value == 0));
    }

    [TestMethod]
    public void PeakReductionRetainsEachBandsStrongestSliceAndMatchingCarrier()
    {
        var group = new Switch2HdRumbleGroup(new(300, 50, 100, 90), new(400, 250, 200, 50), new(500, 100, 150, 200));
        var peak = LegacyJoyConHdRumble.Peak(group);
        Assert.AreEqual(new Switch2HdRumbleSubframe(400, 250, 150, 200), peak);
    }

    [TestMethod]
    public void StandaloneFoldCarriesEitherSideWithoutDoublingIdenticalBodyFeedback()
    {
        Assert.AreEqual(Wave(), LegacyJoyConHdRumble.FoldStandalone(Wave(), Wave()));
        Assert.AreEqual(Wave(), LegacyJoyConHdRumble.FoldStandalone(default, Wave()));
        Assert.AreEqual(Wave(), LegacyJoyConHdRumble.FoldStandalone(Wave(), default));
    }

    [TestMethod]
    public void CanonicalXboxMathRetainsSideLocalImpulseAndAllUserTuning()
    {
        var state = new ControllerFeedbackActuatorState(10_000, 20_000, 40_000, 0);
        Switch2HdRumbleImpulseTuning.TryCreate(false, 2, 8, out var impulse);
        Switch2HdRumbleBodyTuning.TryCreate(150, true, 3, out var body);
        Assert.IsTrue(LegacyJoyConHdRumble.TrySynthesize(state, true, impulse, body, out var left, out var right));
        Assert.IsTrue(left.First.Oscillator0AmplitudeCode > right.First.Oscillator0AmplitudeCode);
        Assert.AreEqual(left.First.Oscillator1AmplitudeCode, right.First.Oscillator1AmplitudeCode);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.GetImpulseHighFrequency(40_000, impulse), left.First.Oscillator0ControlCode);
        Assert.IsTrue(LegacyJoyConHdRumble.TrySynthesize(state, false, impulse, body, out left, out right));
        Assert.AreEqual(left, right);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.CreateCompatibilityGroup(10_000, 20_000, body), left);
        Assert.IsFalse(LegacyJoyConHdRumble.TrySynthesize(state, true, default, body, out _, out _));
    }

    [TestMethod]
    public void AmplitudeCeilingNeverExceedsExistingLegacyPhysicalLimit()
    {
        Assert.AreEqual(JoyConDevice.GetHdRumbleAmplitude(500).high, JoyConDevice.GetHdRumbleAmplitude(1023).high);
        Assert.AreEqual((byte)0, JoyConDevice.GetHdRumbleAmplitude(0).high);
    }

    [TestMethod]
    public void CapturedPublicationAuthorityCannotBeReplacedByNewMailboxAuthority()
    {
        object old = new(), current = old, replacement = new();
        var reports = new List<byte[]>();
        var neutral = new byte[10]; LegacyJoyConHdRumble.Encode(default, true, neutral);
        var oldPacket = new byte[10]; LegacyJoyConHdRumble.Encode(Wave(100), true, oldPacket);
        var newPacket = new byte[10]; LegacyJoyConHdRumble.Encode(Wave(200), true, newPacket);
        LegacyNintendoRumbleOutput writer = null!;
        bool replaced = false;
        writer = new(10, bytes => { reports.Add((byte[])bytes.Clone()); return true; }, credential =>
        {
            if (!replaced)
            {
                replaced = true; current = replacement;
                writer!.Publish(newPacket, true, replacement);
            }
            return ReferenceEquals(credential, current);
        }, neutral);
        writer.Publish(oldPacket, true, old);
        Assert.IsTrue(writer.PumpOnce());
        CollectionAssert.AreEqual(neutral, reports[0]);
        Assert.IsTrue(writer.PumpOnce());
        CollectionAssert.AreEqual(newPacket, reports[1]);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RichHeldRefreshesButStreamingDoesNotReplayAndExpiresNeutral(bool stream)
    {
        using var f = new Fixture();
        Assert.IsTrue(f.Delivery.Publish(Wave(), f.Credential, stream, 0));
        f.Pump();
        f.Now = 5; f.Delivery.Service(); f.Pump();
        Assert.AreEqual(stream ? 1 : 2, f.Reports.Count);
        f.Now = LegacyJoyConHdRumbleDelivery.StreamDurationMilliseconds;
        f.Delivery.Service(); f.Pump();
        if (stream) AssertNeutral(f.Reports[^1]);
        else Assert.IsTrue((f.Reports[^1][3] & 0xFE) != 0);
    }

    [TestMethod]
    public void FailedStreamingWriteRetriesExactPacketButNeverAfterExpiry()
    {
        using var f = new Fixture();
        f.Accept = false;
        f.Delivery.Publish(Wave(), f.Credential, true, 0); f.Pump();
        f.Now = 5; f.Delivery.Service(); f.Pump();
        CollectionAssert.AreEqual(f.Reports[0], f.Reports[1]);
        f.Now = 12; f.Accept = true; f.Delivery.Service(); f.Pump();
        AssertNeutral(f.Reports[^1]);
        f.Now = 15; f.Delivery.Service(); Assert.IsFalse(f.Writer.PumpOnce());
    }

    [TestMethod]
    public void DualSenseControlReconciliationPreservesInversionStrengthAndCarrierTuning()
    {
        byte[] feedback = new byte[6]; feedback[0] = 20; feedback[1] = 90;
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(150, true, 3, out var tuning));
        Assert.IsTrue(ViiperOutDevice.TryBuildSwitch2DualSenseHdRumbleGroups(feedback, feedback.Length, 0,
            false, false, out var left, out var right, out var fidelity, false, false,
            includeCompatibilityOnly: true, sourceTuning: tuning, inverseCompatibility: true));
        var expected = Switch2HdRumbleFeedbackTranslator.CreateCompatibilityGroup(90 * 257, 20 * 257, tuning);
        Assert.AreEqual(expected, left);
        Assert.AreEqual(expected, right);
        Assert.AreEqual(Switch2HdRumbleFeedbackFidelity.SdlBodyCompatibility, fidelity);
        Assert.AreNotEqual(Switch2HdRumbleFeedbackTranslator.CreateCompatibilityGroup(20 * 257, 90 * 257), left);
    }

    [TestMethod]
    public void ControlNeutralDoesNotEraseReplayOrExtendCurrentPcm()
    {
        using var f = new Fixture();
        f.Delivery.Publish(Wave(), f.Credential, true, 0, source: LegacyJoyConHdRumbleSource.DualSensePcm);
        f.Pump();
        f.Now = 5;
        f.Delivery.Publish(default, f.Credential, false, 0, source: LegacyJoyConHdRumbleSource.DualSenseControl);
        f.Delivery.Service();
        Assert.IsFalse(f.Writer.PumpOnce(), "Identical active PCM must not be replayed by control-only traffic.");
        f.Now = 11; f.Delivery.Service(); Assert.IsFalse(f.Writer.PumpOnce());
        f.Now = 12; f.Delivery.Service(); f.Pump(); AssertNeutral(f.Reports[^1]);
        Assert.AreEqual(2, f.Reports.Count, "A control update did not renew the original PCM expiry.");
    }

    [TestMethod]
    public void ControlUpdateUsesSharedBandMixThenResumesOnlyLatestHeldControlAtPcmExpiry()
    {
        using var f = new Fixture();
        var pcm = Wave(170, 70, 480, 120);
        var control = Wave(60, 100, 320, 160);
        f.Delivery.Publish(pcm, f.Credential, true, 0, source: LegacyJoyConHdRumbleSource.DualSensePcm);
        f.Pump();
        f.Now = 5;
        f.Delivery.Publish(control, f.Credential, false, 0, source: LegacyJoyConHdRumbleSource.DualSenseControl);
        f.Pump();
        var expected = new byte[10];
        LegacyJoyConHdRumble.Encode(DualSenseAdaptiveTriggerHdRumbleTranslator.MixPcmWithCompatibility(pcm, control), true, expected);
        CollectionAssert.AreEqual(expected, f.Reports[^1]);
        f.Now = 12; f.Delivery.Service(); f.Pump();
        LegacyJoyConHdRumble.Encode(control, true, expected);
        CollectionAssert.AreEqual(expected, f.Reports[^1]);
        f.Now = 20; f.Delivery.Service(); f.Pump();
        CollectionAssert.AreEqual(expected, f.Reports[^1], "Only control, not expired PCM, may sustain.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExplicitStopOrDifferentSourceCannotRestorePriorPcmOrControl(bool terminal)
    {
        using var f = new Fixture();
        f.Delivery.Publish(Wave(), f.Credential, true, 0,
            source: LegacyJoyConHdRumbleSource.DualSensePcm, control: Wave(50, 20));
        f.Pump(); f.Now = 5;
        var replacement = terminal ? default : Wave(80, 40);
        f.Delivery.Publish(replacement, f.Credential, false, 0, terminal: terminal); f.Pump();
        f.Now = 12; f.Delivery.Service(); f.Pump();
        var expected = new byte[10]; LegacyJoyConHdRumble.Encode(replacement, true, expected);
        CollectionAssert.AreEqual(expected, f.Reports[^1]);
        f.Now = 15;
        f.Delivery.Publish(default, f.Credential, false, 0, source: LegacyJoyConHdRumbleSource.DualSenseControl); f.Pump();
        AssertNeutral(f.Reports[^1]);
    }

    [TestMethod]
    public void SilentPcmFrameExplicitlyEndsPcmWithoutErasingItsCurrentControlSource()
    {
        using var f = new Fixture();
        f.Delivery.Publish(Wave(), f.Credential, true, 0, source: LegacyJoyConHdRumbleSource.DualSensePcm); f.Pump();
        f.Now = 5;
        var control = Wave(60, 70);
        f.Delivery.Publish(default, f.Credential, true, 0,
            source: LegacyJoyConHdRumbleSource.DualSensePcm, control: control); f.Pump();
        var expected = new byte[10]; LegacyJoyConHdRumble.Encode(control, true, expected);
        CollectionAssert.AreEqual(expected, f.Reports[^1]);
        f.Now = 12; f.Delivery.Service(); f.Pump(); CollectionAssert.AreEqual(expected, f.Reports[^1]);
    }

    [TestMethod]
    public void AuthorityEnabledWriterConvertsUncredentialedActivePacketToNeutral()
    {
        var neutral = new byte[10]; LegacyJoyConHdRumble.Encode(default, true, neutral);
        var nonzero = new byte[10]; LegacyJoyConHdRumble.Encode(Wave(), true, nonzero);
        byte[] written = null;
        var guarded = new LegacyNintendoRumbleOutput(10, bytes => { written = (byte[])bytes.Clone(); return true; }, _ => true, neutral);
        Assert.IsTrue(guarded.Publish(nonzero, true)); Assert.IsTrue(guarded.PumpOnce());
        CollectionAssert.AreEqual(neutral, written);
        var original = new LegacyNintendoRumbleOutput(10, bytes => { written = (byte[])bytes.Clone(); return true; });
        Assert.IsTrue(original.Publish(nonzero, true)); Assert.IsTrue(original.PumpOnce());
        CollectionAssert.AreEqual(nonzero, written, "Existing unguarded Switch Pro writer behavior is unchanged.");
    }

    [TestMethod]
    public void ProfileDelayQueuesThenTerminalStopDiscardsPendingEffects()
    {
        using var f = new Fixture();
        f.Delivery.Publish(Wave(), f.Credential, false, 100);
        f.Now = 99; f.Delivery.Service(); Assert.IsFalse(f.Writer.PumpOnce());
        f.Now = 100; f.Delivery.Service(); f.Pump(); Assert.AreEqual(1, f.Reports.Count);
        f.Delivery.Publish(Wave(400), f.Credential, false, 100);
        f.Delivery.Publish(default, f.Credential, false, 0, terminal: true); f.Pump();
        AssertNeutral(f.Reports[^1]);
        f.Now = 500; f.Delivery.Service(); Assert.IsFalse(f.Writer.PumpOnce());
    }

    [TestMethod]
    public void StalePairOrProfileNeverReplaysQueuedOrHeldRumble()
    {
        using var f = new Fixture();
        f.Delivery.Publish(Wave(), f.Credential, false, 0); f.Pump();
        f.Delivery.Publish(Wave(400), f.Credential, false, 100);
        f.Valid = false;
        f.Now = 100; f.Delivery.Service(); f.Pump(); AssertNeutral(f.Reports[^1]);
        Assert.IsFalse(f.Delivery.Publish(Wave(), f.Credential, false, 0));
        Assert.IsTrue(f.Delivery.Service(), "An internal rejection must keep ownership of neutral instead of restoring an old classic rumble mailbox.");
        f.Delivery.Clear();
        Assert.IsFalse(f.Delivery.Service(), "Only an explicit classic/preview takeover releases the neutral latch.");
    }

    [TestMethod]
    public void DelayedQueueOverflowRemainsOwnedNeutralUntilExplicitTakeover()
    {
        using var f = new Fixture();
        for (int i = 0; i < 1024; ++i) Assert.IsTrue(f.Delivery.Publish(Wave(), f.Credential, false, 1000));
        Assert.IsFalse(f.Delivery.Publish(Wave(), f.Credential, false, 1000));
        f.Pump(); AssertNeutral(f.Reports[^1]);
        f.Now = 2000;
        Assert.IsTrue(f.Delivery.Service(), "Overflow must not resurrect an old byte-motor value.");
        Assert.IsFalse(f.Writer.PumpOnce());
    }

    [TestMethod]
    public void DisposalSealsPublicationAndLeavesNeutralOnSoleWriter()
    {
        var f = new Fixture();
        f.Delivery.Publish(Wave(), f.Credential, false, 0); f.Pump();
        f.Delivery.Dispose(); f.Pump(); AssertNeutral(f.Reports[^1]);
        Assert.IsFalse(f.Delivery.Publish(Wave(), f.Credential, false, 0));
        f.Dispose();
    }

    [TestMethod]
    public void ConnectionCueUsesTwoFinitePhasesWithoutHeldReplayAndGameCancelsSecondPhase()
    {
        using var f = new Fixture();
        Assert.IsTrue(f.Delivery.TryStartConnectionCue(f.Credential, Switch2HdRumbleBodyTuning.Default));
        f.Pump();
        f.Now = 5; f.Delivery.Service(); Assert.IsFalse(f.Writer.PumpOnce());
        f.Now = 12; f.Delivery.Service(); f.Pump(); AssertNeutral(f.Reports[^1]);
        f.Now = 210; f.Delivery.Service(); f.Pump(); Assert.IsTrue((f.Reports[^1][3] & 0xFE) != 0);
        f.Now = 222; f.Delivery.Service(); f.Pump(); AssertNeutral(f.Reports[^1]);
        Assert.AreEqual(4, f.Reports.Count);

        using var game = new Fixture();
        game.Delivery.TryStartConnectionCue(game.Credential, Switch2HdRumbleBodyTuning.Default); game.Pump();
        game.Now = 5; game.Delivery.Publish(Wave(), game.Credential, false, 0); game.Pump();
        byte[] authored = game.Reports[^1];
        game.Now = 210; game.Delivery.Service(); game.Pump();
        CollectionAssert.AreEqual(authored, game.Reports[^1]);
        Assert.IsFalse(game.Delivery.TryStartConnectionCue(game.Credential, Switch2HdRumbleBodyTuning.Default));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RealJoyConPairRoutesAuthoredSidesRegardlessOfRetainedVirtualOwner(bool rightOwns)
    {
        using var f = new PairFixture(rightOwns);
        Assert.IsTrue(f.Owner.TryPublishHdRumble(Wave(300, 0), Wave(0, 200), false, 0));
        f.Left.Output.PumpOnce(); f.Right.Output.PumpOnce();
        Assert.IsTrue((f.Left.Reports[^1][3] & 0xFE) > 0);
        Assert.AreEqual(0, f.Left.Reports[^1][5] - 0x40);
        Assert.AreEqual(0, f.Right.Reports[^1][7] & 0xFE);
        Assert.IsTrue(f.Right.Reports[^1][9] > 0x40);
        Assert.IsFalse((rightOwns ? f.Left : f.Right).TryPublishHdRumble(Wave(), Wave(), false, 0));

        f.Joined.Owner.Paused = true;
        Assert.IsTrue(f.Owner.TryPublishHdRumble(Wave(), Wave(), false, 0), "A cold pause suppresses, rather than kills, the valid Xbox owner.");
        f.Left.Output.PumpOnce(); f.Right.Output.PumpOnce();
        AssertNeutral(f.Left.Reports[^1]);
        CollectionAssert.AreEqual(new byte[] { 0, 1, 0x60, 0x40 }, f.Right.Reports[^1][6..10]);

        f.Joined.Owner.Paused = false;
        f.Joined.Other.Paused = true;
        Assert.IsTrue(f.Owner.TryPublishHdRumble(Wave(), Wave(), false, 0), "The not-yet-resumed other half also suppresses rather than rejecting the Xbox owner.");
        f.Left.Output.PumpOnce(); f.Right.Output.PumpOnce();
        AssertNeutral(f.Left.Reports[^1]);
        CollectionAssert.AreEqual(new byte[] { 0, 1, 0x60, 0x40 }, f.Right.Reports[^1][6..10]);
    }

    [TestMethod]
    public void RealPairReplacementAfterQueueingCannotAuthorizeItsPredecessorsPacket()
    {
        using var f = new PairFixture(false);
        Assert.IsTrue(f.Owner.TryPublishHdRumble(Wave(), Wave(), false, 0));
        f.Joined.Active = false;
        f.Left.Output.PumpOnce(); f.Right.Output.PumpOnce();
        AssertNeutral(f.Left.Reports[^1]);
        CollectionAssert.AreEqual(new byte[] { 0, 1, 0x60, 0x40 }, f.Right.Reports[^1][6..10]);
    }

    [TestMethod]
    public void TerminalNeutralStillRetiresPhysicalOutputWhenProfileTuningIsInvalid()
    {
        using var f = new PairFixture(false);
        Assert.IsTrue(f.Owner.TryPublishHdRumble(Wave(), Wave(), false, 0));
        f.Left.Output.PumpOnce(); f.Right.Output.PumpOnce();
        int saved = Global.Switch2XboxImpulseFrequency[0];
        try
        {
            Global.Switch2XboxImpulseFrequency[0] = 0; // a rejected/transient tuning value
            Assert.IsTrue(ViiperOutDevice.TryApplyLegacyJoyConCanonicalFeedback(f.Owner, 0,
                new ControllerFeedbackActuatorState(60000, 60000, 60000, 60000), release: true));
            f.Left.Output.PumpOnce(); f.Right.Output.PumpOnce();
            AssertNeutral(f.Left.Reports[^1]);
            CollectionAssert.AreEqual(new byte[] { 0, 1, 0x60, 0x40 }, f.Right.Reports[^1][6..10]);
        }
        finally { Global.Switch2XboxImpulseFrequency[0] = saved; }
    }

    [TestMethod]
    public void RealJoyConPreviewCannotBeStolenByGameFeedbackOrConnectionCue()
    {
        using var f = new PairFixture(false);
        f.Owner.SetRumblePreview(false, 0, true, 128);
        f.Left.WriteReport(); f.Left.Output.PumpOnce();
        var preview = f.Left.Reports[^1][2..];
        Assert.IsTrue(f.Owner.TryPublishHdRumble(Wave(400), Wave(400), false, 0));
        Assert.IsFalse(f.Owner.TryStartConnectionHaptic());
        f.Left.WriteReport(); f.Left.Output.PumpOnce();
        CollectionAssert.AreEqual(preview, f.Left.Reports[^1][2..]);
        f.Owner.ClearRumblePreview(); f.Left.WriteReport(); f.Left.Output.PumpOnce();
        AssertNeutral(f.Left.Reports[^1]);
    }

    private static void AssertNeutral(byte[] report) =>
        CollectionAssert.AreEqual(new byte[] { 0, 1, 0x60, 0x40 }, report[2..6]);

    private sealed class Fixture : IDisposable
    {
        internal long Now;
        internal bool Valid = true, Accept = true;
        internal readonly object Credential = new();
        internal readonly List<byte[]> Reports = new();
        internal readonly LegacyNintendoRumbleOutput Writer;
        internal readonly LegacyJoyConHdRumbleDelivery Delivery;
        internal Fixture()
        {
            var neutral = new byte[10]; LegacyJoyConHdRumble.Encode(default, true, neutral);
            Writer = new(10, bytes => { Reports.Add((byte[])bytes.Clone()); return Accept; },
                credential => credential == null || Valid && ReferenceEquals(credential, Credential), neutral);
            Delivery = new(Writer, 10, true, credential => Valid && ReferenceEquals(credential, Credential),
                () => Now, scheduleTimers: false);
        }
        internal void Pump() => Writer.PumpOnce();
        public void Dispose() => Delivery.Dispose();
    }

    private sealed class PairFixture : IDisposable
    {
        internal readonly RecordingJoyCon Left = new(true), Right = new(false);
        internal readonly JoyConDevice Owner;
        internal readonly LegacyJoyConGroup Joined;
        private readonly bool oldLeft = Global.EnableOutputDataToDS4[0], oldRight = Global.EnableOutputDataToDS4[1];
        internal PairFixture(bool rightOwns)
        {
            Global.EnableOutputDataToDS4[0] = Global.EnableOutputDataToDS4[1] = true;
            var links = new LegacyJoyConLinkCoordinator(() => new Projection(), (_, _, _) => { });
            Left.ProfileConnection = links.Register(Left, 0); Right.ProfileConnection = links.Register(Right, 1);
            Owner = rightOwns ? Right : Left;
            Assert.IsTrue(links.TryLink(Owner.ProfileConnection,
                (rightOwns ? Left : Right).ProfileConnection, (_, _) => { }, out Joined));
        }
        public void Dispose()
        {
            Left.Stop(); Right.Stop();
            Global.EnableOutputDataToDS4[0] = oldLeft; Global.EnableOutputDataToDS4[1] = oldRight;
        }
    }

    private sealed class Projection : ILegacyJoyConProfileProjection
    {
        public bool TryApply(in LegacyJoyConProjectionInput input, DS4State destination) => true;
    }

    private sealed class RecordingJoyCon : JoyConDevice
    {
        internal readonly LegacyNintendoRumbleOutput Output;
        internal readonly List<byte[]> Reports = new();
        internal RecordingJoyCon(bool left) : base((HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice)), "HD rumble test")
        {
            deviceType = left ? InputDeviceType.JoyConL : InputDeviceType.JoyConR;
            typeof(JoyConDevice).GetField("sideType", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(this, left ? JoyConSide.Left : JoyConSide.Right);
            typeof(JoyConDevice).GetField("rumbleReportBuffer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(this, new byte[10]);
            Output = InitializeRumbleOutput();
        }
        protected override bool WriteRumbleReport(byte[] report) { Reports.Add((byte[])report.Clone()); return true; }
        internal void Stop()
        {
            setRumble(0, 0); WriteReport(); Output.PumpOnce();
            StopOutputUpdate();
        }
    }
}
