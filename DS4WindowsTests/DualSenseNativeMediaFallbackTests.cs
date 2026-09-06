using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class DualSenseNativeMediaFallbackTests
{
    // These tests use an unopened HID object and no physical/output workers.
    // The BT case holds the existing template-admission flag, deterministically
    // modelling a concurrent control update without timing or hardware I/O.
    [DataTestMethod]
    [DataRow(ConnectionType.USB, false)]
    [DataRow(ConnectionType.USB, true)]
    [DataRow(ConnectionType.BT, false)]
    [DataRow(ConnectionType.BT, true)]
    public void UnavailableNativeMediaCannotPublishCompatibilityRumble(
        ConnectionType connection, bool sidecar)
    {
        WithNativeTarget(connection, sidecar, (output, device) =>
        {
            byte[] feedback = BuildMedia();
            output.ApplyAtomicAudioHapticsFeedback(feedback, feedback.Length, 0);

            Assert.AreEqual(0, device.CompatibilityRumbleCalls,
                "A time-bearing native audio frame was reinterpreted as a " +
                "local rumble command after native transport rejection.");
            var mailbox = (DualSensePhysicalOutputStateMailbox)GetField(
                typeof(DualSenseDevice), device, "physicalOutputStateMailbox");
            Assert.AreEqual(0L, mailbox.ReadLatest().RumbleGeneration,
                "Rejected native media must not switch the physical " +
                "controller to zero-amplitude compatibility rumble.");
        });
    }

    [TestMethod]
    public void GenuineCompactControlStillReachesCompatibilityRumble()
    {
        WithNativeTarget(ConnectionType.USB, false, (output, device) =>
        {
            byte[] feedback = new byte[28];
            feedback[0] = 75;
            feedback[1] = 30;
            typeof(ViiperOutDevice).GetMethod("ApplyFeedback", PrivateInstance)!
                .Invoke(output, new object[] { feedback, feedback.Length, 0,
                    true, null, 0L });
            Assert.AreEqual(1, device.CompatibilityRumbleCalls,
                "Real compact control feedback must retain its legacy route.");
        });
    }

    private static byte[] BuildMedia()
    {
        // V5: 28-byte compatibility state, 48-byte native HID state, then
        // the 398-byte combined media carrier. Motor bytes are zero because
        // this frame carries a waveform, not compatibility-rumble motors.
        byte[] feedback = new byte[ViiperOutDevice.DualSenseAtomicFeedbackLength];
        feedback[28] = 0x02;
        const int carrier = 76;
        feedback[carrier] = 0x36;
        feedback[carrier + 11] = 0x90;
        feedback[carrier + 12] = 63;
        feedback[carrier + 76] = 0x92;
        feedback[carrier + 77] = 64;
        for (int sample = 0; sample < 64; sample++)
            feedback[carrier + 78 + sample] = (byte)(sample % 2 == 0 ? 45 : 211);
        return feedback;
    }

    private static void WithNativeTarget(ConnectionType connection, bool sidecar,
        Action<ViiperOutDevice, RecordingDualSense> action)
    {
        ControlService previousHub = DS4Windows.Program.rootHub;
        bool previousOutputEnabled = Global.EnableOutputDataToDS4[0];
        using AudioHapticsService audio = new(); // No capture is started.
        try
        {
            var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            SetField(typeof(HidDevice), hid, "_deviceAttributes",
                new HidDeviceAttributes(new NativeMethods.HIDD_ATTRIBUTES
                { VendorID = 0x054C, ProductID = 0x0CE6 }));
            var device = new RecordingDualSense(hid);
            SetField(typeof(DS4Device), device, "conType", connection);
            if (connection == ConnectionType.BT)
            {
                device.EnableSpeakerOutput = true;
                SetField(typeof(DualSenseDevice), device,
                    "bluetoothSpeakerClockActiveClaim", 1L);
                SetField(typeof(DualSenseDevice), device,
                    "bluetoothSpeakerClockLeaseExpiryTimestamp", long.MaxValue);
                SetField(typeof(DualSenseDevice), device,
                    "bluetoothCombinedTemplateUpdateClaimed", 1);
            }

            var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            hub.DS4Controllers = new DS4Device[4];
            hub.DS4Controllers[0] = device;
            SetField(typeof(ControlService), hub, "audioHapticsService", audio);
            DS4Windows.Program.rootHub = hub;
            Global.EnableOutputDataToDS4[0] = true;

            var output = new ViiperOutDevice(OutContType.None, ViiperVirtualDeviceType.DualSense);
            SetField(typeof(ViiperOutDevice), output, "lastInputDeviceIndex", 0);
            SetField(typeof(ViiperOutDevice), output, "audioOnlySidecar", sidecar);
            // Cache an already-verified test identity: do not enumerate PnP.
            SetField(typeof(ViiperOutDevice), output, "physicalDualSenseIdentityPath", string.Empty);
            SetField(typeof(ViiperOutDevice), output, "physicalDualSenseIdentityVerified", true);
            action(output, device);
        }
        finally
        {
            DS4Windows.Program.rootHub = previousHub;
            Global.EnableOutputDataToDS4[0] = previousOutputEnabled;
        }
    }

    private sealed class RecordingDualSense(HidDevice hid)
        : DualSenseDevice(hid, "Native media regression test")
    {
        public int CompatibilityRumbleCalls { get; private set; }
        public override void setRumble(byte rightLightFastMotor, byte leftHeavySlowMotor)
        {
            CompatibilityRumbleCalls++;
            base.setRumble(rightLightFastMotor, leftHeavySlowMotor);
        }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object GetField(Type type, object instance, string name) =>
        type.GetField(name, PrivateInstance)!.GetValue(instance);
    private static void SetField(Type type, object instance, string name, object value) =>
        type.GetField(name, PrivateInstance)!.SetValue(instance, value);
}
