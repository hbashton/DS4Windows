using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Forms.ViewModels;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests;

[TestClass]
public sealed class ControllerAudioRuntimeApplicabilityTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance |
        BindingFlags.NonPublic;

    [DataTestMethod]
    [DataRow(InputDeviceType.Switch2Pro, ConnectionType.USB, 0x057E, 0x2069, false)]
    [DataRow(InputDeviceType.Switch2Pro, ConnectionType.BT, 0x057E, 0x2069, false)]
    [DataRow(InputDeviceType.Switch2JoyConLeft, ConnectionType.BT, 0x057E, 0x2067, false)]
    [DataRow(InputDeviceType.Switch2JoyConRight, ConnectionType.BT, 0x057E, 0x2066, false)]
    [DataRow(InputDeviceType.Switch2JoyConJoined, ConnectionType.BT, 0x057E, 0x2067, false)]
    [DataRow(InputDeviceType.SwitchPro, ConnectionType.USB, 0x057E, 0x2009, false)]
    [DataRow(InputDeviceType.DS3, ConnectionType.USB, 0x054C, 0x0268, false)]
    [DataRow(InputDeviceType.DS4, ConnectionType.USB, 0x054C, 0x09CC, false)]
    [DataRow(InputDeviceType.DS4, ConnectionType.BT, 0x054C, 0x09CC, true)]
    [DataRow(InputDeviceType.DS4, ConnectionType.BT, 0x054C, 0x05C4, true)]
    [DataRow(InputDeviceType.DualSense, ConnectionType.USB, 0x054C, 0x0CE6, true)]
    [DataRow(InputDeviceType.DualSense, ConnectionType.BT, 0x054C, 0x0DF2, true)]
    [DataRow(InputDeviceType.DualSense, ConnectionType.BT, 0x1234, 0x0CE6, false)]
    [DataRow(InputDeviceType.DualSense, ConnectionType.USB, 0x054C, 0xFFFF, false)]
    public void UiAndRuntimeUseTheSamePhysicalAudioEligibility(
        InputDeviceType deviceType, ConnectionType connection,
        int vendorId, int productId, bool expected)
    {
        Assert.AreEqual(expected,
            ControllerAudioCapabilityPolicy.SupportsControllerAudio(
                deviceType, connection, vendorId, productId));
        Assert.AreEqual(expected,
            ControllerUiCapabilities.For(deviceType, connection,
                vendorId, productId).SupportsControllerAudio);
        Assert.AreEqual(expected,
            ControllerAudioCapabilityPolicy.SupportsControllerAudio(
                new TestPhysicalDevice(deviceType, connection,
                    vendorId, productId)));
    }

    [DataTestMethod]
    [DoNotParallelize]
    [DataRow(InputDeviceType.Switch2Pro, ConnectionType.USB)]
    [DataRow(InputDeviceType.Switch2Pro, ConnectionType.BT)]
    [DataRow(InputDeviceType.Switch2JoyConLeft, ConnectionType.BT)]
    [DataRow(InputDeviceType.Switch2JoyConRight, ConnectionType.BT)]
    [DataRow(InputDeviceType.Switch2JoyConJoined, ConnectionType.BT)]
    [DataRow(InputDeviceType.SwitchPro, ConnectionType.USB)]
    [DataRow(InputDeviceType.DS3, ConnectionType.USB)]
    [DataRow(InputDeviceType.DS4, ConnectionType.USB)]
    public void InheritedSonySettingsDoNotMakeUnsupportedControllersNeedAttention(
        InputDeviceType deviceType, ConnectionType connection)
    {
        using var profile = new ProfileScope();
        DS4Device device = new TestPhysicalDevice(deviceType, connection,
            deviceType == InputDeviceType.DS4 ? 0x054C : 0x057E,
            deviceType == InputDeviceType.DS4 ? 0x09CC : 0x2069);
        // No audio services are installed in this fake host: querying an
        // inapplicable lane at all is a test failure, not merely a label check.
        ControlService service = Service(device, installAudioServices: false);
        ControllerRuntimeSignals signals = service.GetControllerRuntimeSignals(0);

        Assert.AreEqual(ControllerRuntimeLaneState.NotRequired, signals.Speaker);
        Assert.AreEqual(ControllerRuntimeLaneState.NotRequired, signals.Microphone);
        Assert.AreEqual(ControllerRuntimeLaneState.NotRequired, signals.AudioHaptics);
        Assert.IsTrue(ControllerRuntimeStatusPolicy.Evaluate(signals).IsReady);
        Assert.IsTrue(Global.DualSenseEnableSpeakerOutput[0]);
        Assert.IsTrue(Global.DualSenseEnableMicrophonePassthrough[0]);
        Assert.IsTrue(Global.store.audioHapticsSettings[0].Enabled);
        Assert.IsTrue(Global.store.audioHapticsSettings[0].StreamAppAudioToController,
            "Status evaluation must not rewrite a shared profile's saved choices.");
    }

    [DataTestMethod]
    [DoNotParallelize]
    [DataRow(InputDeviceType.DS4, ConnectionType.BT)]
    [DataRow(InputDeviceType.DualSense, ConnectionType.BT)]
    [DataRow(InputDeviceType.DualSense, ConnectionType.USB)]
    public void SupportedPhysicalAudioFailuresRemainVisible(
        InputDeviceType deviceType, ConnectionType connection)
    {
        using var profile = new ProfileScope();
        DS4Device device = deviceType == InputDeviceType.DualSense ?
            new TestDualSense(connection) :
            new TestPhysicalDevice(deviceType, connection, 0x054C, 0x09CC);
        ControlService service = Service(device, installAudioServices: true);
        ControllerRuntimeSignals signals = service.GetControllerRuntimeSignals(0);
        Assert.AreEqual(ControllerRuntimeLaneState.Unavailable, signals.Speaker);
        ControllerStartupStatus status = ControllerRuntimeStatusPolicy.Evaluate(signals);
        Assert.IsTrue(status.NeedsAttention);
        StringAssert.Contains(status.Detail, "speaker");

        Global.DualSenseEnableSpeakerOutput[0] = false;
        Global.store.audioHapticsSettings[0].StreamAppAudioToController = false;
        signals = service.GetControllerRuntimeSignals(0);
        Assert.AreEqual(ControllerRuntimeLaneState.NotRequired, signals.Speaker);
        Assert.AreEqual(ControllerRuntimeLaneState.Unavailable, signals.Microphone);
        status = ControllerRuntimeStatusPolicy.Evaluate(signals);
        Assert.IsTrue(status.NeedsAttention);
        StringAssert.Contains(status.Detail, "microphone");

        Global.DualSenseEnableMicrophonePassthrough[0] = false;
        signals = service.GetControllerRuntimeSignals(0);
        Assert.AreEqual(deviceType == InputDeviceType.DualSense ?
            ControllerRuntimeLaneState.Unavailable :
            ControllerRuntimeLaneState.NotRequired, signals.AudioHaptics);
        status = ControllerRuntimeStatusPolicy.Evaluate(signals);
        Assert.AreEqual(deviceType == InputDeviceType.DualSense,
            status.NeedsAttention);
    }

    [TestMethod]
    public void UnknownLiveIdentityDoesNotBorrowOfflineProfileAudioCapabilities()
    {
        Assert.IsFalse(ControllerAudioCapabilityPolicy.SupportsControllerAudio(null));
        Assert.IsFalse(ControllerAudioCapabilityPolicy.SupportsControllerAudio(
            InputDeviceType.DualSense, ConnectionType.USB, null, null));
        Assert.IsTrue(ControllerUiCapabilities.For(InputDeviceType.DualSense).
            SupportsControllerAudio, "Offline profile editing remains available.");
    }

    private static ControlService Service(DS4Device device,
        bool installAudioServices)
    {
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(
            typeof(ControlService));
        service.DS4Controllers = new[] { device };
        service.outputDevices = new OutputDevice[1];
        SetServiceField("playStationFeatureOutputLock", new object());
        SetServiceField("playStationFeatureOutputDevices", new ViiperOutDevice[1]);
        if (installAudioServices)
        {
            SetServiceField("dualSenseAudioPassthrough", new DualSenseAudioPassthrough());
            SetServiceField("dualShock4AudioPassthrough", new DualShock4AudioPassthrough());
            SetServiceField("dualSenseMicrophonePassthrough", new DualSenseMicrophonePassthrough());
            SetServiceField("audioHapticsService", new AudioHapticsService());
        }
        return service;

        void SetServiceField(string name, object value) =>
            typeof(ControlService).GetField(name, PrivateInstance).
                SetValue(service, value);
    }

    private static HidDevice Hid(int vendorId, int productId)
    {
        var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
        var attributes = new HidDeviceAttributes(new NativeMethods.HIDD_ATTRIBUTES
        {
            VendorID = (ushort)vendorId, ProductID = (ushort)productId,
        });
        typeof(HidDevice).GetField("_deviceAttributes", PrivateInstance).
            SetValue(hid, attributes);
        return hid;
    }

    private sealed class TestPhysicalDevice : DS4Device
    {
        internal TestPhysicalDevice(InputDeviceType type, ConnectionType connection,
            int vendorId, int productId)
            : base("Audio applicability test", type, connection)
        {
            hDevice = Hid(vendorId, productId);
        }

        public override bool IsAlive() => true;
    }

    private sealed class TestDualSense : DualSenseDevice
    {
        internal TestDualSense(ConnectionType connection)
            : base(Hid(0x054C, 0x0CE6), "DualSense audio applicability test")
        {
            deviceType = InputDeviceType.DualSense;
            conType = connection;
        }

        public override bool IsAlive() => true;
    }

    private sealed class ProfileScope : IDisposable
    {
        private readonly bool speaker = Global.DualSenseEnableSpeakerOutput[0];
        private readonly bool microphone = Global.DualSenseEnableMicrophonePassthrough[0];
        private readonly bool dinputOnly = Global.DinputOnly[0];
        private readonly AudioHapticsProfileSettings audio = Global.store.audioHapticsSettings[0];

        internal ProfileScope()
        {
            Global.DinputOnly[0] = true;
            Global.DualSenseEnableSpeakerOutput[0] = true;
            Global.DualSenseEnableMicrophonePassthrough[0] = true;
            Global.store.audioHapticsSettings[0] = new AudioHapticsProfileSettings
            {
                Enabled = true, Source = AudioHapticsSourceKind.AppSession,
                StreamAppAudioToController = true,
            };
        }

        public void Dispose()
        {
            Global.DinputOnly[0] = dinputOnly;
            Global.DualSenseEnableSpeakerOutput[0] = speaker;
            Global.DualSenseEnableMicrophonePassthrough[0] = microphone;
            Global.store.audioHapticsSettings[0] = audio;
        }
    }
}
