using DS4Windows;
using DS4Windows.InputDevices;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class LegacyJoyConDualSenseFeedbackTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CapturedControlCannotRenewStaleCompactRumbleOnEitherOriginalActuator(bool silentPcm)
    {
        using var f = new Fixture();
        byte[] feedback = Native(0x0C);
        if (silentPcm) feedback[76] = 0x36;
        for (int report = 0; report < 4; report++)
        {
            f.Now += 100;
            f.Apply(feedback);
            f.AssertNeutral();
        }
    }

    [DataTestMethod]
    [DataRow(0x03, 0x00)]
    [DataRow(0x02, 0x00)]
    [DataRow(0x00, 0x04)]
    public void CompatibilitySelectionWorksButCachedMediaCannotRestoreItAfterStop(int flag0, int flag2)
    {
        using var f = new Fixture();
        byte[] feedback = Native((byte)flag0, (byte)flag2);
        f.Apply(feedback);
        f.AssertNonzero();
        f.Apply(Native(0x0C));
        f.AssertNeutral();
        feedback[76] = 0x36;
        for (int interval = 0; interval < 4; interval++)
        {
            f.Now += 100;
            f.Apply(feedback, fresh: false);
            f.AssertNeutral();
        }
    }

    [DataTestMethod]
    [DataRow(0x03, 0x00)]
    [DataRow(0x02, 0x00)]
    [DataRow(0x00, 0x04)]
    public void ZeroMotorControlKeepsBothOriginalActuatorsStoppedAcrossStaleMedia(int flag0, int flag2)
    {
        using var f = new Fixture();
        byte[] feedback = Native((byte)flag0, (byte)flag2);
        f.Apply(feedback);
        f.AssertNonzero();
        feedback[0] = feedback[1] = 0;
        f.Apply(feedback);
        f.AssertNeutral();
        feedback[0] = 43;
        feedback[76] = 0x36;
        for (int interval = 0; interval < 4; interval++)
        {
            f.Now += 100;
            f.Apply(feedback, fresh: false);
            f.AssertNeutral();
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NewStreamOrLogicalPairCannotInheritCachedCompatibilitySelection(bool newPair)
    {
        using var f = new Fixture();
        byte[] feedback = Native(0x03);
        f.Apply(feedback);
        f.AssertNonzero();
        long stream = 11;
        if (newPair)
        {
            Assert.IsTrue(f.Links.TryUnlink(f.Left.ProfileConnection.Group, _ => { }));
            Assert.IsTrue(f.Links.TryLink(f.Left.ProfileConnection, f.Right.ProfileConnection, (_, _) => { }, out _));
        }
        else stream++;
        f.Apply(feedback, fresh: false, stream: stream);
        f.AssertNeutral();
        f.Apply(feedback, stream: stream);
        f.AssertNonzero();
    }

    [TestMethod]
    public void NativeAudioKeepsTwelveMillisecondPcmLeaseAndCompatibilityDisablesPcm()
    {
        using var f = new Fixture();
        byte[] feedback = Pcm(0x02);
        f.Apply(feedback);
        f.AssertNonzero();
        Assert.AreEqual(0L, f.Left.PcmExpiresAt, "Compatibility mode must not admit a separate PCM contribution.");
        Assert.AreEqual(0L, f.Right.PcmExpiresAt);

        feedback[29] = 0x0C;
        f.Apply(feedback);
        f.AssertNonzero();
        long deadline = f.Now + LegacyJoyConHdRumbleDelivery.StreamDurationMilliseconds;
        Assert.AreEqual(deadline, f.Left.PcmExpiresAt);
        Assert.AreEqual(deadline, f.Right.PcmExpiresAt);
        f.Now += 5;
        f.Apply(Native(0x0C));
        f.AssertNonzero();
        Assert.AreEqual(deadline, f.Left.PcmExpiresAt, "Control traffic cannot renew the PCM lease.");
        f.Now = deadline;
        f.Apply(Native(0x0C));
        f.AssertNeutral();
    }

    [TestMethod]
    public void AudioSelectionDoesNotDisableHeldAdaptiveTriggerTranslation()
    {
        using var f = new Fixture();
        byte[] feedback = Native(0x0C);
        feedback[6] = 0x26;
        feedback[7] = 0xFF; feedback[8] = 0x03;
        feedback[9] = feedback[10] = feedback[11] = 0xFF;
        feedback[12] = 0x3F; feedback[15] = 28;
        f.Right.GetRawCurrentStateRef().R2Btn = true;
        f.Apply(feedback);
        Assert.IsTrue(f.Left.IsNeutral);
        Assert.IsFalse(f.Right.IsNeutral);
        f.Right.GetRawCurrentStateRef().R2Btn = false;
        f.Apply(feedback);
        f.AssertNeutral();
    }

    [TestMethod]
    public void CompactOnlyLegacyFeedbackStillDrivesOriginalRumble()
    {
        using var f = new Fixture();
        var feedback = new byte[28];
        feedback[0] = 43;
        f.Apply(feedback);
        f.AssertNonzero();
    }

    private static byte[] Native(byte flag0, byte flag2 = 0)
    {
        var feedback = new byte[ViiperOutDevice.DualSenseAtomicFeedbackLength];
        feedback[0] = 43;
        feedback[6] = feedback[17] = 5;
        feedback[28] = 0x02; feedback[29] = flag0; feedback[30] = 0x57;
        feedback[67] = flag2;
        return feedback;
    }

    private static byte[] Pcm(byte flag0)
    {
        byte[] feedback = Native(flag0);
        feedback[76] = 0x36;
        feedback[76 + 11] = 0x90; feedback[76 + 12] = 63;
        feedback[76 + 76] = 0x92; feedback[76 + 77] = 64;
        for (int i = 0; i < 64; i++) feedback[76 + 78 + i] = (byte)(i % 2 == 0 ? 80 : 176);
        return feedback;
    }

    private sealed class Fixture : IDisposable
    {
        internal long Now = 100;
        internal readonly RecordingJoyCon Left, Right;
        internal readonly LegacyJoyConLinkCoordinator Links = new(() => new Projection(), (_, _, _) => { });
        private readonly ViiperOutDevice output = new(OutContType.None, ViiperVirtualDeviceType.DualSense);
        private readonly ControlService previousHub = DS4Windows.Program.rootHub;
        private readonly bool oldOutput = Global.EnableOutputDataToDS4[0];
        private readonly bool oldAudio = Global.Switch2DualSenseAudioHapticsEnabled[0];
        private readonly bool oldAdaptive = Global.Switch2DualSenseAdaptiveTriggersEnabled[0];
        private readonly byte oldBoost = Global.RumbleBoost[0];
        private readonly int oldDelay = Global.Switch2RumbleDelayMilliseconds[0];
        internal Fixture()
        {
            Global.EnableOutputDataToDS4[0] = true;
            Global.Switch2DualSenseAudioHapticsEnabled[0] = true;
            Global.Switch2DualSenseAdaptiveTriggersEnabled[0] = true;
            Global.RumbleBoost[0] = 100;
            Global.Switch2RumbleDelayMilliseconds[0] = 0;
            Left = new(true, () => Now); Right = new(false, () => Now);
            Left.ProfileConnection = Links.Register(Left, 0);
            Right.ProfileConnection = Links.Register(Right, 1);
            Assert.IsTrue(Links.TryLink(Left.ProfileConnection, Right.ProfileConnection, (_, _) => { }, out _));
            var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            hub.DS4Controllers = new DS4Device[] { Left, Right };
            DS4Windows.Program.rootHub = hub;
            Set(typeof(ViiperOutDevice), output, "lastInputDeviceIndex", 0);
        }
        internal void Apply(byte[] feedback, bool fresh = true, long stream = 11)
        {
            typeof(ViiperOutDevice).GetMethod("ApplyFeedback", Flags)!.Invoke(output,
                new object[] { feedback, feedback.Length, 0, fresh, null, stream });
            Left.Output.PumpOnce(); Right.Output.PumpOnce();
        }
        internal void AssertNeutral() { Assert.IsTrue(Left.IsNeutral); Assert.IsTrue(Right.IsNeutral); }
        internal void AssertNonzero() { Assert.IsFalse(Left.IsNeutral); Assert.IsFalse(Right.IsNeutral); }
        public void Dispose()
        {
            Left.Stop(); Right.Stop();
            DS4Windows.Program.rootHub = previousHub;
            Global.EnableOutputDataToDS4[0] = oldOutput;
            Global.Switch2DualSenseAudioHapticsEnabled[0] = oldAudio;
            Global.Switch2DualSenseAdaptiveTriggersEnabled[0] = oldAdaptive;
            Global.RumbleBoost[0] = oldBoost;
            Global.Switch2RumbleDelayMilliseconds[0] = oldDelay;
        }
    }

    private sealed class Projection : ILegacyJoyConProfileProjection
    { public bool TryApply(in LegacyJoyConProjectionInput input, DS4State destination) => true; }

    private sealed class RecordingJoyCon : JoyConDevice
    {
        internal readonly LegacyNintendoRumbleOutput Output;
        private readonly LegacyJoyConHdRumbleDelivery delivery;
        private readonly bool left;
        private byte[] last;
        internal long PcmExpiresAt => (long)typeof(LegacyJoyConHdRumbleDelivery).GetField("pcmExpiresAt", Flags)!.GetValue(delivery)!;
        internal bool IsNeutral => last != null && last.AsSpan(left ? 2 : 6, 4).SequenceEqual(new byte[] { 0, 1, 0x60, 0x40 });
        internal RecordingJoyCon(bool left, Func<long> clock) : base(
            (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice)), "Original feedback regression")
        {
            this.left = left;
            deviceType = left ? InputDeviceType.JoyConL : InputDeviceType.JoyConR;
            Set(typeof(JoyConDevice), this, "sideType", left ? JoyConSide.Left : JoyConSide.Right);
            Set(typeof(JoyConDevice), this, "rumbleReportBuffer", new byte[10]);
            Output = InitializeRumbleOutput();
            ((IDisposable)typeof(JoyConDevice).GetField("hdRumbleDelivery", Flags)!.GetValue(this)!).Dispose();
            var authority = (Func<object, bool>)typeof(JoyConDevice).GetMethod("IsHdRumbleAuthorityCurrent", Flags)!
                .CreateDelegate(typeof(Func<object, bool>), this);
            delivery = new(Output, 10, left, authority, clock, scheduleTimers: false);
            Set(typeof(JoyConDevice), this, "hdRumbleDelivery", delivery);
        }
        protected override bool WriteRumbleReport(byte[] report) { last = (byte[])report.Clone(); return true; }
        internal void Stop() => StopOutputUpdate();
    }

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Set(Type type, object target, string field, object value) => type.GetField(field, Flags)!.SetValue(target, value);
}
