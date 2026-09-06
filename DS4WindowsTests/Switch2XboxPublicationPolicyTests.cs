using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2XboxPublicationPolicyTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProfileDisableDuringPublicationCannotFallBetweenSnapshotAndRefresh(bool disableAll)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var previousHub = DS4Windows.Program.rootHub;
        var previousAppHub = DS4WinWPF.App.rootHub;
        bool previousOutput = Global.EnableOutputDataToDS4[0];
        bool previousImpulse = Global.Switch2MapXboxImpulseTriggersToHdRumble[0];
        int previousDelay = Global.Switch2RumbleDelayMilliseconds[0];
        var lease = new Switch2BluetoothFeedbackLifetimeTests.RecordingLease();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, 17, 23, out var feedback));
        Assert.IsTrue(feedback.TryActivate());
        Assert.IsTrue(feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        Thread publisher = null;
        try
        {
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(17, 23, Switch2Transport.BluetoothLe,
                out var target, out _));
            var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            var output = new ViiperOutDevice(OutContType.ViiperXboxOne, ViiperVirtualDeviceType.XboxOne);
            hub.DS4Controllers = new DS4Device[] { target };
            hub.outputDevices = new OutputDevice[] { output };
            DS4Windows.Program.rootHub = hub;
            DS4WinWPF.App.rootHub = hub;
            Global.EnableOutputDataToDS4[0] = true;
            Global.Switch2MapXboxImpulseTriggersToHdRumble[0] = true;
            Global.Switch2RumbleDelayMilliseconds[0] = 0;
            void Set(string name, object value) => typeof(ViiperOutDevice).GetField(name, fields).SetValue(output, value);
            Set("connected", true);
            Set("feedbackDispatchStopRequested", false);
            Set("lastInputDeviceIndex", 0);
            Set("streamGeneration", 7L);
            Set("switch2FeedbackSession", session);
            Set("deviceStream", RuntimeHelpers.GetUninitializedObject(typeof(ViiperDeviceStream)));
            var deliver = (Func<byte[], int, bool>)typeof(ViiperOutDevice).GetMethod("TryApplyXboxOneFeedback", fields)
                .CreateDelegate(typeof(Func<byte[], int, bool>), output);
            var refresh = (Func<bool>)typeof(ViiperOutDevice).GetMethod("ProcessXboxFeedbackPolicyRefresh", fields)
                .CreateDelegate(typeof(Func<bool>), output);
            var profile = (DS4WinWPF.DS4Forms.ViewModels.ProfileSettingsViewModel)RuntimeHelpers.GetUninitializedObject(
                typeof(DS4WinWPF.DS4Forms.ViewModels.ProfileSettingsViewModel));
            object sessionGate = typeof(Switch2VirtualFeedbackSession).GetField("gate", fields).GetValue(session);
            bool accepted = false;
            Exception publicationError = null;
            using var entered = new ManualResetEventSlim();
            publisher = new Thread(() =>
            {
                try
                {
                    Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
                    Assert.IsTrue(ControllerFeedbackFrame.TryCreate(ControllerFeedbackSource.XboxOneVirtualDevice,
                        ControllerFeedbackCommand.Apply, ControllerFeedbackActuators.All,
                        0, 0, 40_000, 50_000, 1, 17, 23, session.OwnershipEpoch, now, 250_000, out var frame));
                    byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
                    Assert.IsTrue(frame.TryWriteTo(wire));
                    entered.Set();
                    accepted = deliver(wire, wire.Length);
                }
                catch (Exception error) { publicationError = error; }
            }) { IsBackground = true, Name = "Xbox policy publication test" };
            lock (sessionGate)
            {
                publisher.Start();
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(1)));
                Assert.IsTrue(SpinWait.SpinUntil(() =>
                    (publisher.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 1_000),
                    "Publisher must reach the session gate after reading the profile snapshot.");
                Assert.IsTrue(session.TryCaptureXboxPolicyRevision(out ulong revision));
                Assert.AreEqual(0UL, revision, "The blocked frame has not consumed its publication identity yet.");
                if (disableAll) profile.EnableOutputDataToDS4 = false;
                else profile.Switch2MapXboxImpulseTriggersToHdRumble = false;
            }
            Assert.IsTrue(publisher.Join(TimeSpan.FromSeconds(2)));
            Assert.IsNull(publicationError, publicationError?.ToString());
            Assert.IsTrue(accepted);
            Assert.IsTrue(refresh());
            Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(lease.LastPayload,
                out _, out var left, out var right, out _));
            Assert.IsFalse(left.First.HasNonzeroAmplitude || left.Second.HasNonzeroAmplitude || left.Third.HasNonzeroAmplitude ||
                right.First.HasNonzeroAmplitude || right.Second.HasNonzeroAmplitude || right.Third.HasNonzeroAmplitude,
                "A profile disable during publication must not be lost as an obsolete refresh.");
        }
        finally
        {
            if (publisher?.IsAlive == true) Assert.IsTrue(publisher.Join(TimeSpan.FromSeconds(3)));
            session.TryRetire();
            feedback.TryStopAndRetire(3);
            Global.EnableOutputDataToDS4[0] = previousOutput;
            Global.Switch2MapXboxImpulseTriggersToHdRumble[0] = previousImpulse;
            Global.Switch2RumbleDelayMilliseconds[0] = previousDelay;
            DS4Windows.Program.rootHub = previousHub;
            DS4WinWPF.App.rootHub = previousAppHub;
        }
    }
}
