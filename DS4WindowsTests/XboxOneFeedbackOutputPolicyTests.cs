using DS4Windows;
using DS4Windows.InputDevices;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests;

[TestClass]
public sealed class XboxOneFeedbackOutputPolicyTests
{
    [TestMethod]
    [DoNotParallelize]
    public void NonSwitch2ProfileOutputDisableWakesCurrentEffectWithoutNewPacket()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var previousHub = DS4Windows.Program.rootHub;
        var previousAppHub = DS4WinWPF.App.rootHub;
        bool previousOutput = Global.EnableOutputDataToDS4[0];
        byte previousBoost = Global.RumbleBoost[0];
        bool previousInverse = Global.InverseRumbleMotors[0];
        XboxOnePhysicalFeedbackSession session = null;
        try
        {
            var clock = new XboxOnePhysicalFeedbackWatchdogTests.ManualClock();
            var target = new TestPhysicalDevice();
            var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            var output = new ViiperOutDevice(OutContType.ViiperXboxOne, ViiperVirtualDeviceType.XboxOne);
            hub.DS4Controllers = new DS4Device[] { target };
            hub.outputDevices = new OutputDevice[] { output };
            DS4Windows.Program.rootHub = hub;
            DS4WinWPF.App.rootHub = hub;
            Global.EnableOutputDataToDS4[0] = true;
            Global.RumbleBoost[0] = 100;
            Global.InverseRumbleMotors[0] = false;
            void Set(string name, object value) => typeof(ViiperOutDevice).GetField(name, fields).SetValue(output, value);
            Set("connected", true);
            Set("feedbackDispatchStopRequested", false);
            Set("lastInputDeviceIndex", 0);
            Set("streamGeneration", 7L);
            // Identity-only callback admission; no network/native device work.
            Set("deviceStream", RuntimeHelpers.GetUninitializedObject(typeof(ViiperDeviceStream)));
            Assert.IsTrue(output.TryCreateXboxOnePhysicalFeedbackSession(new()
            {
                Source = (byte)ControllerFeedbackSource.XboxOneVirtualDevice,
                PersonaGeneration = 1, DeviceGeneration = 5, TransportGeneration = 6,
                OwnershipEpoch = 7, TimeToLiveMicroseconds = 250_000,
            }, target, 0, out session, clock));
            Set("xboxOnePhysicalFeedbackSession", session);
            var publish = (Func<byte[], int, bool>)typeof(ViiperOutDevice).GetMethod("TryApplyXboxOneFeedback", fields)
                .CreateDelegate(typeof(Func<byte[], int, bool>), output);
            var refresh = (Func<bool>)typeof(ViiperOutDevice).GetMethod("ProcessXboxFeedbackPolicyRefresh", fields)
                .CreateDelegate(typeof(Func<bool>), output);
            var profile = (DS4WinWPF.DS4Forms.ViewModels.ProfileSettingsViewModel)RuntimeHelpers.GetUninitializedObject(
                typeof(DS4WinWPF.DS4Forms.ViewModels.ProfileSettingsViewModel));
            void Publish(ulong sequence)
            {
                byte[] wire = PolicyWire(Frame(sequence, timestamp: clock.Now, ttl: 250_000), true);
                Assert.IsTrue(publish(wire, wire.Length));
            }
            Publish(1);
            Assert.IsTrue(target.LastHeavy != 0 && target.LastLight != 0);
            profile.EnableOutputDataToDS4 = false;
            Assert.IsTrue(refresh());
            Assert.AreEqual((byte)0, target.LastHeavy, "Disabling output must not wait for another game frame or expiry.");
            Assert.AreEqual((byte)0, target.LastLight);
            profile.EnableOutputDataToDS4 = true;
            Assert.IsTrue(refresh());
            Assert.AreEqual((byte)0, target.LastHeavy, "Re-enabling cannot resurrect the suppressed frame.");
            Publish(2);
            Assert.IsTrue(target.LastHeavy != 0 && target.LastLight != 0, "A fresh identical game effect may resume.");
            profile.EnableOutputDataToDS4 = false;
            profile.EnableOutputDataToDS4 = true;
            Publish(3);
            Assert.IsTrue(refresh());
            Assert.IsTrue(target.LastHeavy != 0, "An older queued restriction cannot suppress a new game frame.");
            profile.EnableOutputDataToDS4 = false;
            profile.EnableOutputDataToDS4 = true;
            Assert.IsTrue(refresh());
            Assert.AreEqual((byte)0, target.LastHeavy, "A rapid off/on still suppresses the original frame.");
            Publish(4);
            profile.EnableOutputDataToDS4 = false;
            var delayedRequest = (XboxOnePhysicalOutputSuppressionRequest)typeof(ViiperOutDevice)
                .GetField("xboxOnePhysicalOutputSuppressionRequested", fields).GetValue(output);
            Assert.IsNotNull(delayedRequest);
            profile.EnableOutputDataToDS4 = true;
            Publish(5);
            profile.EnableOutputDataToDS4 = false;
            typeof(ViiperOutDevice).GetMethod("EnqueueXboxOnePhysicalOutputSuppression", fields)
                .Invoke(output, new object[] { delayedRequest });
            profile.EnableOutputDataToDS4 = true;
            Assert.IsTrue(refresh());
            Assert.AreEqual((byte)0, target.LastHeavy, "A delayed older request cannot overwrite the newer pending restriction.");
            Publish(6);
            profile.EnableOutputDataToDS4 = false;
            profile.EnableOutputDataToDS4 = true;
            int beforeStale = target.RumbleCalls;
            Set("streamGeneration", 8L);
            Assert.IsTrue(refresh());
            Assert.AreEqual(beforeStale, target.RumbleCalls, "An old stream's queued setting must be ignored.");
            profile.EnableOutputDataToDS4 = false;
            profile.EnableOutputDataToDS4 = true;
            var replacement = new TestPhysicalDevice();
            hub.DS4Controllers[0] = replacement;
            Assert.IsTrue(refresh());
            Assert.AreEqual(beforeStale, target.RumbleCalls);
            Assert.AreEqual(0, replacement.RumbleCalls, "A same-slot replacement cannot inherit old feedback work.");
            hub.DS4Controllers[0] = target;
        }
        finally
        {
            session?.TryRetire();
            DS4Windows.Program.rootHub = previousHub;
            DS4WinWPF.App.rootHub = previousAppHub;
            Global.EnableOutputDataToDS4[0] = previousOutput;
            Global.RumbleBoost[0] = previousBoost;
            Global.InverseRumbleMotors[0] = previousInverse;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ProductionXboxFeedbackCallbackConsumesDisabledOutputWithoutBypassingSession()
    {
        const BindingFlags privateInstance = BindingFlags.Instance |
            BindingFlags.NonPublic;
        ControlService previousHub = DS4Windows.Program.rootHub;
        bool previousOutputEnabled = Global.EnableOutputDataToDS4[0];
        byte previousBoost = Global.RumbleBoost[0];
        XboxOnePhysicalFeedbackSession session = null;
        try
        {
            var target = new TestPhysicalDevice();
            var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(
                typeof(ControlService));
            hub.DS4Controllers = new DS4Device[] { target };
            DS4Windows.Program.rootHub = hub;
            Global.RumbleBoost[0] = 100;
            var output = new ViiperOutDevice(OutContType.ViiperXboxOne,
                ViiperVirtualDeviceType.XboxOne);
            Assert.IsTrue(output.TryCreateXboxOnePhysicalFeedbackSession(new()
            {
                Source = (byte)ControllerFeedbackSource.XboxOneVirtualDevice,
                PersonaGeneration = 1, DeviceGeneration = 5,
                TransportGeneration = 6, OwnershipEpoch = 7,
                TimeToLiveMicroseconds = 250_000,
            }, target, 0, out session));
            typeof(ViiperOutDevice).GetField("lastInputDeviceIndex",
                privateInstance).SetValue(output, 0);
            typeof(ViiperOutDevice).GetField("xboxOnePhysicalFeedbackSession",
                privateInstance).SetValue(output, session);
            var apply = (Func<byte[], int, bool>)typeof(ViiperOutDevice).
                GetMethod("TryApplyXboxOneFeedback", privateInstance).
                CreateDelegate(typeof(Func<byte[], int, bool>), output);

            bool Publish(ulong sequence, ControllerFeedbackCommand command =
                ControllerFeedbackCommand.Apply, ulong generation = 5)
            {
                Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(
                    out ulong now));
                ControllerFeedbackFrame frame = Frame(sequence, command,
                    deviceGeneration: generation, timestamp: now, ttl: 250_000);
                byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
                Assert.IsTrue(frame.TryWriteTo(wire));
                return apply(wire, wire.Length);
            }

            Global.EnableOutputDataToDS4[0] = true;
            Assert.IsTrue(Publish(1));
            Assert.IsTrue(target.LastHeavy != 0 && target.LastLight != 0);

            Global.EnableOutputDataToDS4[0] = false;
            Assert.IsTrue(Publish(2),
                "A valid disabled-output command must not reject the broker.");
            Assert.AreEqual((byte)0, target.LastHeavy);
            Assert.AreEqual((byte)0, target.LastLight);
            int writesAfterSuppression = target.RumbleCalls;
            Assert.IsFalse(Publish(2));
            Assert.IsFalse(Publish(3, generation: 55));
            Assert.AreEqual(writesAfterSuppression, target.RumbleCalls,
                "Replays and wrong-lifetime frames must not reach the target.");

            Global.EnableOutputDataToDS4[0] = true;
            Assert.IsTrue(Publish(3));
            Assert.IsTrue(target.LastHeavy != 0 && target.LastLight != 0);
            Global.EnableOutputDataToDS4[0] = false;
            Assert.IsTrue(Publish(4, ControllerFeedbackCommand.Stop));
            Assert.AreEqual((byte)0, target.LastHeavy);
            Assert.AreEqual((byte)0, target.LastLight);
            Assert.IsFalse(Publish(5),
                "Disabled-output policy cannot reopen terminal ownership.");
        }
        finally
        {
            session?.TryRetire();
            Global.EnableOutputDataToDS4[0] = previousOutputEnabled;
            Global.RumbleBoost[0] = previousBoost;
            DS4Windows.Program.rootHub = previousHub;
        }
    }

    [TestMethod]
    public void DisabledOutputPreservesEveryFenceAndClearsAllFourActuators()
    {
        ControllerFeedbackFrame input = Frame(1);
        Assert.IsTrue(ViiperOutDevice.TryApplyXboxOneFeedbackOutputPolicy(
            input, outputEnabled: false, out var effective));

        Assert.AreEqual(ControllerFeedbackCommand.Neutral, effective.Command);
        Assert.AreEqual((ushort)0, effective.BodyLow);
        Assert.AreEqual((ushort)0, effective.BodyHigh);
        Assert.AreEqual((ushort)0, effective.LeftTrigger);
        Assert.AreEqual((ushort)0, effective.RightTrigger);
        Assert.AreEqual(input.Version, effective.Version);
        Assert.AreEqual(input.Source, effective.Source);
        Assert.AreEqual(input.Actuators, effective.Actuators);
        Assert.AreEqual(input.Sequence, effective.Sequence);
        Assert.AreEqual(input.DeviceGeneration, effective.DeviceGeneration);
        Assert.AreEqual(input.TransportGeneration, effective.TransportGeneration);
        Assert.AreEqual(input.OwnershipEpoch, effective.OwnershipEpoch);
        Assert.AreEqual(input.TimestampMicroseconds, effective.TimestampMicroseconds);
        Assert.AreEqual(input.TimeToLiveMicroseconds, effective.TimeToLiveMicroseconds);
    }

    [TestMethod]
    public void EnabledOutputAndTerminalStopAreUnchanged()
    {
        ControllerFeedbackFrame input = Frame(1);
        Assert.IsTrue(ViiperOutDevice.TryApplyXboxOneFeedbackOutputPolicy(
            input, outputEnabled: true, out var effective));
        Assert.AreEqual(input, effective);

        foreach (ControllerFeedbackCommand command in new[]
        {
            ControllerFeedbackCommand.Neutral, ControllerFeedbackCommand.Stop,
        })
        {
            ControllerFeedbackFrame terminal = Frame(2, command);
            Assert.IsTrue(ViiperOutDevice.TryApplyXboxOneFeedbackOutputPolicy(
                terminal, outputEnabled: false, out effective));
            Assert.AreEqual(terminal, effective,
                "Suppression cannot change ownership-lifecycle intent.");
        }
    }

    [TestMethod]
    public void DisabledOutputDoesNotAdmitMalformedOrUnrelatedSources()
    {
        Assert.IsFalse(ViiperOutDevice.TryApplyXboxOneFeedbackOutputPolicy(
            default, outputEnabled: false, out _));
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
            ControllerFeedbackSource.Xbox360VirtualDevice,
            ControllerFeedbackCommand.Apply, ControllerFeedbackActuators.All,
            1, 2, 3, 4, 1, 5, 6, 7, 1_000, 1_000,
            out var unrelated));
        Assert.IsFalse(ViiperOutDevice.TryApplyXboxOneFeedbackOutputPolicy(
            unrelated, outputEnabled: false, out _));
    }

    [TestMethod]
    public void DisabledOutputStillRequiresExactFreshNonReplayedPhysicalBinding()
    {
        XboxOnePhysicalFeedbackSession session = Session();
        Assert.IsTrue(session.TryAccept(PolicyWire(Frame(1), enabled: true),
            1_010, out var initial, out _));
        Assert.IsFalse(initial.IsNeutral);

        Assert.IsFalse(session.TryAccept(PolicyWire(Frame(2,
            deviceGeneration: 55), enabled: false), 1_010, out _, out _));
        Assert.IsFalse(session.TryAccept(PolicyWire(Frame(2,
            transportGeneration: 66), enabled: false), 1_010, out _, out _));
        Assert.IsFalse(session.TryAccept(PolicyWire(Frame(2,
            ownershipEpoch: 77), enabled: false), 1_010, out _, out _));
        Assert.IsFalse(session.TryAccept(PolicyWire(Frame(2,
            timestamp: 900, ttl: 100), enabled: false),
            1_010, out _, out _));
        Assert.IsFalse(session.TryAccept(PolicyWire(Frame(2,
            timestamp: 10_000), enabled: false), 1_010, out _, out _));

        Assert.IsTrue(session.TryAccept(PolicyWire(Frame(2), enabled: false),
            1_010, out var suppressed, out bool terminal));
        Assert.IsTrue(suppressed.IsNeutral);
        Assert.IsFalse(terminal);
        Assert.IsFalse(session.TryAccept(PolicyWire(Frame(2), enabled: true),
            1_010, out _, out _),
            "Re-enabling output must not resurrect a suppressed sequence.");
        Assert.IsTrue(session.TryAccept(PolicyWire(Frame(3), enabled: true),
            1_010, out var resumed, out _));
        Assert.IsFalse(resumed.IsNeutral);

        Assert.IsTrue(session.TryAccept(PolicyWire(Frame(4,
            ControllerFeedbackCommand.Stop), enabled: false),
            1_010, out var stopped, out terminal));
        Assert.IsTrue(stopped.IsNeutral);
        Assert.IsTrue(terminal);
        Assert.IsFalse(session.TryAccept(PolicyWire(Frame(5), enabled: false),
            1_010, out _, out _));
    }

    [TestMethod]
    public void SuppressionProjectionAndSerializationAllocateNothing()
    {
        ControllerFeedbackFrame input = Frame(1);
        Span<byte> wire = stackalloc byte[ControllerFeedbackFrame.SerializedLength];
        bool succeeded = true;
        for (int index = 0; index < 2_000; index++)
        {
            succeeded &= ViiperOutDevice.TryApplyXboxOneFeedbackOutputPolicy(
                input, outputEnabled: false, out var effective);
            succeeded &= effective.TryWriteTo(wire);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            succeeded &= ViiperOutDevice.TryApplyXboxOneFeedbackOutputPolicy(
                input, outputEnabled: false, out var effective);
            succeeded &= effective.TryWriteTo(wire);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    private static XboxOnePhysicalFeedbackSession Session()
    {
        Assert.IsTrue(XboxOnePhysicalFeedbackSession.TryCreate(new()
        {
            Source = (byte)ControllerFeedbackSource.XboxOneVirtualDevice,
            PersonaGeneration = 1, DeviceGeneration = 5,
            TransportGeneration = 6, OwnershipEpoch = 7,
            TimeToLiveMicroseconds = 1_000,
        }, new TestPhysicalDevice(), out var session));
        return session;
    }

    private static ControllerFeedbackFrame Frame(ulong sequence,
        ControllerFeedbackCommand command = ControllerFeedbackCommand.Apply,
        ulong deviceGeneration = 5, ulong transportGeneration = 6,
        ulong ownershipEpoch = 7, ulong timestamp = 1_000, ulong ttl = 1_000)
    {
        bool apply = command == ControllerFeedbackCommand.Apply;
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
            ControllerFeedbackSource.XboxOneVirtualDevice, command,
            ControllerFeedbackActuators.All,
            apply ? (ushort)257 : (ushort)0, apply ? (ushort)514 : (ushort)0,
            apply ? (ushort)771 : (ushort)0, apply ? (ushort)1028 : (ushort)0,
            sequence, deviceGeneration, transportGeneration, ownershipEpoch,
            timestamp, ttl, out var frame));
        return frame;
    }

    private static byte[] PolicyWire(in ControllerFeedbackFrame frame,
        bool enabled)
    {
        Assert.IsTrue(ViiperOutDevice.TryApplyXboxOneFeedbackOutputPolicy(
            frame, enabled, out var effective));
        byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
        Assert.IsTrue(effective.TryWriteTo(wire));
        return wire;
    }

    private sealed class TestPhysicalDevice : DS4Device
    {
        internal byte LastLight;
        internal byte LastHeavy;
        internal int RumbleCalls;

        internal TestPhysicalDevice()
            : base("Xbox output policy test controller", InputDeviceType.DS4,
                ConnectionType.USB)
        {
        }

        public override void setRumble(byte rightLightFastMotor,
            byte leftHeavySlowMotor)
        {
            LastLight = rightLightFastMotor;
            LastHeavy = leftHeavySlowMotor;
            RumbleCalls++;
        }
    }
}
