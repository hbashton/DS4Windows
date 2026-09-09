using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
public sealed class XboxImpulseRuntimePolicyTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void MasterOnlyMasksImpulsesAndRoutingRequiresIndependentActuators(bool independent, bool adaptive)
    {
        var state = new ControllerFeedbackActuatorState(2570, 5140, 25700, 51400);
        XboxOneCanonicalFeedbackAdapter.ProjectPhysical(state, independent, false, adaptive,
            out byte heavy, out byte light, out byte left, out byte right);
        Assert.AreEqual((byte)10, heavy);
        Assert.AreEqual((byte)20, light);
        Assert.AreEqual((byte)0, left);
        Assert.AreEqual((byte)0, right);
        XboxOneCanonicalFeedbackAdapter.ProjectPhysical(state, independent, true, adaptive,
            out heavy, out light, out left, out right);
        Assert.AreEqual((byte)(independent && adaptive ? 10 : 100), heavy);
        Assert.AreEqual((byte)(independent && adaptive ? 20 : 200), light);
        Assert.AreEqual((byte)(independent && adaptive ? 100 : 0), left);
        Assert.AreEqual((byte)(independent && adaptive ? 200 : 0), right);
    }

    [TestMethod]
    public void DedicatedEffectIsBoundedAndIndependentOfSavedTriggerLabTemplate()
    {
        Assert.AreEqual((byte)0x05, XboxImpulseTriggerEffect.Encode(0).triggerMotorMode);
        uint previous = 0;
        for (int magnitude = 1; magnitude <= 255; magnitude++)
        {
            var effect = XboxImpulseTriggerEffect.Encode((byte)magnitude);
            Assert.AreEqual((byte)0x26, effect.triggerMotorMode);
            Assert.AreEqual((byte)0xFF, effect.triggerStartResistance);
            Assert.AreEqual((byte)0x03, effect.triggerEffectForce);
            Assert.AreEqual((byte)28, effect.triggerActuationFrequency);
            uint packed = (uint)(effect.triggerRangeForce |
                effect.triggerNearReleaseStrength << 8 | effect.triggerNearMiddleStrength << 16 |
                effect.triggerPressedStrength << 24);
            Assert.AreEqual(0U, packed >> 30, "No strength may extend beyond ten documented zones.");
            uint strength = packed & 7;
            Assert.IsTrue(strength >= previous);
            previous = strength;
            for (int zone = 0; zone < 10; zone++) Assert.AreEqual(strength, (packed >> (zone * 3)) & 7);
        }
        Assert.AreEqual(7U, previous);
    }

    [TestMethod]
    public void SessionImpulseRestrictionPreservesBodyUntilAbsoluteExpiryAndNeedsFreshFrameToResume()
    {
        var clock = new XboxOnePhysicalFeedbackWatchdogTests.ManualClock();
        bool enabled = true;
        var writes = new List<(ControllerFeedbackActuatorState State, bool Release)>();
        XboxOnePhysicalFeedbackSession session = null;
        Assert.IsTrue(XboxOnePhysicalFeedbackSession.TryCreateOwned(Binding(), new RecordingBodyDevice(),
            (state, release) => { writes.Add((state, release)); return true; }, out session, clock,
            isImpulseEnabled: () =>
            {
                Assert.IsTrue(session.TryCaptureOutputPolicySequence(out _));
                return enabled;
            }));
        try
        {
            Assert.IsTrue(session.TryPublish(Frame(1, clock.Now)));
            clock.Advance(500);
            enabled = false;
            Assert.IsTrue(session.TryRefreshCurrentOutput(1, false, true));
            Assert.AreEqual((ushort)2570, writes[^1].State.BodyLow);
            Assert.AreEqual((ushort)5140, writes[^1].State.BodyHigh);
            Assert.AreEqual((ushort)0, writes[^1].State.LeftTrigger);
            Assert.AreEqual((ushort)0, writes[^1].State.RightTrigger);
            enabled = true;
            Assert.IsTrue(session.TryRefreshCurrentOutput(1, false, false));
            Assert.AreEqual((ushort)0, writes[^1].State.LeftTrigger);
            clock.Advance(500);
            Assert.IsTrue(writes[^1].State.IsNeutral);
            Assert.IsTrue(writes[^1].Release, "A policy refresh must not extend the original deadline.");
            Assert.IsTrue(session.TryPublish(Frame(2, clock.Now)));
            Assert.AreEqual((ushort)25700, writes[^1].State.LeftTrigger);
        }
        finally { session?.TryRetire(); }
    }

    [TestMethod]
    public void IdenticalRenewalSamplesRoutingAfterPublishingIdentityEvenWhenQueuedWakeBecomesStale()
    {
        var clock = new XboxOnePhysicalFeedbackWatchdogTests.ManualClock();
        bool adaptive = true;
        int writes = 0;
        XboxOnePhysicalFeedbackSession session = null;
        Assert.IsTrue(XboxOnePhysicalFeedbackSession.TryCreateOwned(Binding(), new RecordingBodyDevice(),
            (_, _) => { writes++; return true; }, out session, clock,
            impulseToAdaptiveTriggers: () =>
            {
                Assert.IsTrue(session.TryCaptureOutputPolicySequence(out _));
                return adaptive;
            }));
        try
        {
            Assert.IsTrue(session.TryPublish(Frame(1, clock.Now)));
            adaptive = false;
            Assert.IsTrue(session.TryPublish(Frame(2, clock.Now)));
            Assert.AreEqual(2, writes, "An equal canonical renewal must still move the effect to its new route.");
            Assert.IsTrue(session.TryRefreshCurrentOutput(1, false, false));
            Assert.AreEqual(2, writes, "A stale local wake cannot repaint a successor sequence.");
        }
        finally { session?.TryRetire(); }
    }

    [TestMethod]
    public void OwnedOverlayRevealsLatestUnderlayPerSideAndRejectsForeignOwner()
    {
        var mailbox = new DualSensePhysicalOutputStateMailbox();
        var first = new object();
        var successor = new object();
        var left = Trigger(0x21, 11);
        var right = Trigger(0x25, 22);
        mailbox.SetTrigger(TriggerId.LeftTrigger, left);
        mailbox.SetTrigger(TriggerId.RightTrigger, right);
        Assert.IsTrue(mailbox.TrySetXboxImpulse(first, 100, 200, out bool changed));
        Assert.IsTrue(changed);
        Assert.AreEqual(left, mailbox.ReadLatest().LeftTrigger);
        Assert.AreEqual(right, mailbox.ReadLatest().RightTrigger);
        Assert.AreEqual((byte)0x26, mailbox.ReadLatest().ForLocalTriggerReport().LeftTrigger.triggerMotorMode);
        var newLeft = Trigger(0x25, 33);
        mailbox.SetTrigger(TriggerId.LeftTrigger, newLeft);
        Assert.IsTrue(mailbox.TrySetXboxImpulse(first, 0, 200, out changed));
        Assert.AreEqual(newLeft, mailbox.ReadLatest().ForLocalTriggerReport().LeftTrigger);
        Assert.AreEqual((byte)0x26, mailbox.ReadLatest().ForLocalTriggerReport().RightTrigger.triggerMotorMode);
        Assert.IsFalse(mailbox.TrySetXboxImpulse(successor, 50, 50, out changed));
        Assert.IsFalse(changed);
        Assert.IsTrue(mailbox.TrySetXboxImpulse(first, 0, 0, out _));
        Assert.AreEqual(right, mailbox.ReadLatest().ForLocalTriggerReport().RightTrigger);
        Assert.IsTrue(mailbox.TrySetXboxImpulse(successor, 50, 50, out _));
        Assert.IsTrue(mailbox.TrySetXboxImpulse(first, 0, 0, out changed));
        Assert.IsFalse(changed, "A predecessor's late release cannot clear its successor.");
        Assert.AreSame(successor, mailbox.ReadLatest().XboxImpulseOwner);
    }

    // Real production callback and cold profile wake, but unopened test HID
    // objects, cached identities, a manual clock, and no physical workers.
    [DataTestMethod]
    [DataRow(0x0CE6, ConnectionType.USB)]
    [DataRow(0x0CE6, ConnectionType.BT)]
    [DataRow(0x0DF2, ConnectionType.USB)]
    [DataRow(0x0DF2, ConnectionType.BT)]
    [DoNotParallelize]
    public void DualSenseAndEdgeRouteLiveWithoutTriggerLabAndRestoreProfileOnTeardown(int product, ConnectionType connection)
    {
        var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
        Set(typeof(HidDevice), hid, "_deviceAttributes", new HidDeviceAttributes(
            new NativeMethods.HIDD_ATTRIBUTES { VendorID = 0x054C, ProductID = (ushort)product }));
        var target = new DualSenseDevice(hid, "Xbox impulse regression test");
        Set(typeof(DS4Device), target, "conType", connection);
        var mailbox = (DualSensePhysicalOutputStateMailbox)Get(typeof(DualSenseDevice), target, "physicalOutputStateMailbox");
        var underlayLeft = Trigger(0x21, 12);
        var underlayRight = Trigger(0x25, 34);
        mailbox.SetTrigger(TriggerId.LeftTrigger, underlayLeft);
        mailbox.SetTrigger(TriggerId.RightTrigger, underlayRight);
        WithRuntime(target, (output, session, clock, refresh) =>
        {
            Assert.IsFalse(Global.store.triggerLabSettings[0].Enabled);
            Assert.IsTrue(session.TryPublish(Frame(1, clock.Now)));
            AssertBody(mailbox, 10, 20);
            Assert.AreEqual((byte)100, mailbox.ReadLatest().LeftXboxImpulse);
            Assert.AreEqual((byte)200, mailbox.ReadLatest().RightXboxImpulse);
            Assert.AreEqual((byte)0x26, mailbox.ReadLatest().ForLocalTriggerReport().LeftTrigger.triggerMotorMode);
            Global.XboxImpulseToAdaptiveTriggers[0] = false;
            refresh();
            AssertBody(mailbox, 100, 200);
            Assert.IsNull(mailbox.ReadLatest().XboxImpulseOwner);
            Assert.AreEqual(underlayLeft, mailbox.ReadLatest().ForLocalTriggerReport().LeftTrigger);
            Global.XboxImpulseToAdaptiveTriggers[0] = true;
            refresh();
            AssertBody(mailbox, 10, 20);
            Assert.AreEqual((byte)100, mailbox.ReadLatest().LeftXboxImpulse);
            Global.MapXboxImpulseTriggers[0] = false;
            refresh();
            AssertBody(mailbox, 10, 20);
            Assert.IsNull(mailbox.ReadLatest().XboxImpulseOwner);
            Global.MapXboxImpulseTriggers[0] = true;
            refresh();
            Assert.IsNull(mailbox.ReadLatest().XboxImpulseOwner, "Master re-enable requires a new game publication.");
            Assert.IsTrue(session.TryPublish(Frame(2, clock.Now)));
            var latestProfile = Trigger(0x25, 56);
            mailbox.SetTrigger(TriggerId.RightTrigger, latestProfile);
            Assert.IsTrue(session.TryRetire());
            AssertBody(mailbox, 0, 0);
            Assert.IsNull(mailbox.ReadLatest().XboxImpulseOwner);
            Assert.AreEqual(underlayLeft, mailbox.ReadLatest().ForLocalTriggerReport().LeftTrigger);
            Assert.AreEqual(latestProfile, mailbox.ReadLatest().ForLocalTriggerReport().RightTrigger);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void ConventionalBodyRouteLiveMasterOffAndRapidReenableNeverSuppressNormalRumble()
    {
        var target = new RecordingBodyDevice();
        WithRuntime(target, (output, session, clock, refresh) =>
        {
            Assert.IsTrue(session.TryPublish(Frame(1, clock.Now)));
            Assert.AreEqual((byte)100, target.Heavy);
            Global.MapXboxImpulseTriggers[0] = false;
            output.QueueXboxFeedbackPolicyRefresh(0);
            Global.MapXboxImpulseTriggers[0] = true;
            refresh();
            Assert.AreEqual((byte)10, target.Heavy);
            Assert.AreEqual((byte)20, target.Light);
            Assert.IsTrue(session.TryPublish(Frame(2, clock.Now)));
            Assert.AreEqual((byte)100, target.Heavy);
            Global.XboxImpulseToAdaptiveTriggers[0] = false;
            refresh();
            Assert.AreEqual((byte)100, target.Heavy, "Routing selection cannot remove impulses from a two-motor device.");
        });
    }

    private static void WithRuntime(DS4Device target,
        Action<ViiperOutDevice, XboxOnePhysicalFeedbackSession, XboxOnePhysicalFeedbackWatchdogTests.ManualClock, Action> action)
    {
        var previousHub = DS4Windows.Program.rootHub;
        bool previousOutput = Global.EnableOutputDataToDS4[0];
        bool previousMaster = Global.MapXboxImpulseTriggers[0];
        bool previousRoute = Global.XboxImpulseToAdaptiveTriggers[0];
        byte previousBoost = Global.RumbleBoost[0];
        bool previousInverse = Global.InverseRumbleMotors[0];
        var previousLab = Global.store.triggerLabSettings[0];
        XboxOnePhysicalFeedbackSession session = null;
        try
        {
            var clock = new XboxOnePhysicalFeedbackWatchdogTests.ManualClock();
            var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            hub.DS4Controllers = new[] { target };
            DS4Windows.Program.rootHub = hub;
            Global.EnableOutputDataToDS4[0] = true;
            Global.MapXboxImpulseTriggers[0] = true;
            Global.XboxImpulseToAdaptiveTriggers[0] = true;
            Global.RumbleBoost[0] = 100;
            Global.InverseRumbleMotors[0] = false;
            Global.store.triggerLabSettings[0] = new TriggerLabProfileSettings { Enabled = false };
            var output = new ViiperOutDevice(OutContType.ViiperXboxOne, ViiperVirtualDeviceType.XboxOne);
            Set(typeof(ViiperOutDevice), output, "lastInputDeviceIndex", 0);
            Set(typeof(ViiperOutDevice), output, "physicalDualSenseIdentityPath", string.Empty);
            Set(typeof(ViiperOutDevice), output, "physicalDualSenseIdentityVerified", true);
            Set(typeof(ViiperOutDevice), output, "connected", true);
            Set(typeof(ViiperOutDevice), output, "feedbackDispatchStopRequested", false);
            Set(typeof(ViiperOutDevice), output, "streamGeneration", 7L);
            Set(typeof(ViiperOutDevice), output, "deviceStream", RuntimeHelpers.GetUninitializedObject(typeof(ViiperDeviceStream)));
            Assert.IsTrue(output.TryCreateXboxOnePhysicalFeedbackSession(Binding(), target, 0, out session, clock));
            Set(typeof(ViiperOutDevice), output, "xboxOnePhysicalFeedbackSession", session);
            var process = (Func<bool>)typeof(ViiperOutDevice).GetMethod("ProcessXboxFeedbackPolicyRefresh", PrivateInstance)
                .CreateDelegate(typeof(Func<bool>), output);
            action(output, session, clock, () => { output.QueueXboxFeedbackPolicyRefresh(0); Assert.IsTrue(process()); });
        }
        finally
        {
            session?.TryRetire();
            DS4Windows.Program.rootHub = previousHub;
            Global.EnableOutputDataToDS4[0] = previousOutput;
            Global.MapXboxImpulseTriggers[0] = previousMaster;
            Global.XboxImpulseToAdaptiveTriggers[0] = previousRoute;
            Global.RumbleBoost[0] = previousBoost;
            Global.InverseRumbleMotors[0] = previousInverse;
            Global.store.triggerLabSettings[0] = previousLab;
        }
    }

    private static void AssertBody(DualSensePhysicalOutputStateMailbox mailbox, byte heavy, byte light)
    {
        Assert.AreEqual(heavy, mailbox.ReadLatest().RumbleState.RumbleMotorStrengthLeftHeavySlow);
        Assert.AreEqual(light, mailbox.ReadLatest().RumbleState.RumbleMotorStrengthRightLightFast);
    }

    private static XboxOneAuthorizedFeedbackBinding Binding() => new()
    {
        Source = (byte)ControllerFeedbackSource.XboxOneVirtualDevice,
        PersonaGeneration = 4, DeviceGeneration = 5, TransportGeneration = 6,
        OwnershipEpoch = 7, TimeToLiveMicroseconds = 1_000,
    };

    private static byte[] Frame(ulong sequence, ulong now)
    {
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(ControllerFeedbackSource.XboxOneVirtualDevice,
            ControllerFeedbackCommand.Apply, ControllerFeedbackActuators.All, 2570, 5140, 25700, 51400,
            sequence, 5, 6, 7, now, 1_000, out var frame));
        byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
        Assert.IsTrue(frame.TryWriteTo(wire));
        return wire;
    }

    private static DualSenseDevice.TriggerEffectData Trigger(byte mode, byte data) => new()
    { triggerMotorMode = mode, triggerStartResistance = data };

    private sealed class RecordingBodyDevice() : DS4Device("Impulse policy regression test", InputDeviceType.DS4, ConnectionType.USB)
    {
        internal byte Heavy;
        internal byte Light;
        public override void setRumble(byte lightMotor, byte heavyMotor) { Light = lightMotor; Heavy = heavyMotor; }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Get(Type type, object instance, string name) => type.GetField(name, PrivateInstance)!.GetValue(instance);
    private static void Set(Type type, object instance, string name, object value) => type.GetField(name, PrivateInstance)!.SetValue(instance, value);
}
