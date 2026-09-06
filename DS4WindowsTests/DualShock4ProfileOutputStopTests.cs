using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Control;

namespace DS4WindowsTests;

// Actual DS4 composition and report construction; only the final transport
// admission is replaced. No PostInit, HID handle, input worker or hardware.
[TestClass]
public sealed class DualShock4ProfileOutputStopTests
{
    [ClassInitialize]
    public static void InitializeCrcTable(TestContext context) =>
        Crc32Algorithm.InitializeTable(DS4Device.DefaultPolynomial);

    [DataTestMethod]
    [DataRow(0)] // USB
    [DataRow(1)] // Sony wireless adapter
    [DataRow(2)] // Bluetooth 0x11
    [DataRow(3)] // Bluetooth clone 0x05
    [DataRow(4)] // Bluetooth speaker owner
    [DataRow(5)] // Bluetooth full-duplex audio owner
    public void ProfileDisableSendsOneNeutralThenSuppressesFurtherEffects(int transport)
    {
        var device = new RecordingDevice(transport);
        device.setRumble(120, 230);
        Assert.IsTrue(device.Pump());
        Assert.AreEqual(1, device.Reports.Count);
        Assert.AreEqual((byte)120, device.Reports[0][device.MotorOffset]);
        var features = device.FeatureSet;
        device.ConfigureDualShock4ProfileOutput(false);
        device.SetRumblePreview(true, 255, true, 255);
        Assert.IsTrue(device.Pump());
        Assert.AreEqual(2, device.Reports.Count, "The final stop must reach the existing writer.");
        device.AssertNeutral(device.Reports[1]);
        Assert.AreEqual(features, device.FeatureSet, "A profile is not a hardware capability.");
        device.setRumble(200, 200);
        device.LightBarColor = new DS4Color(100, 200, 255);
        Assert.IsTrue(device.Pump());
        Assert.AreEqual(2, device.Reports.Count, "Even forced refresh must respect disabled output.");
    }

    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(2, false)]
    [DataRow(3, true)]
    [DataRow(4, false)]
    [DataRow(5, true)]
    public void FailedNeutralIsNotAcknowledgedAndRetriesOnTheNextWriterPass(int transport, bool throws)
    {
        var device = new RecordingDevice(transport);
        device.setRumble(190, 130);
        device.Pump();
        device.ConfigureDualShock4ProfileOutput(false);
        device.Accept = false;
        device.Throw = throws;
        Assert.IsFalse(device.Pump());
        device.setRumble(255, 255);
        device.Accept = true;
        device.Throw = false;
        Assert.IsTrue(device.Pump());
        Assert.AreEqual(3, device.Reports.Count);
        device.AssertNeutral(device.Reports[1]);
        device.AssertNeutral(device.Reports[2]);
        device.Pump();
        Assert.AreEqual(3, device.Reports.Count);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(4)]
    public void ReenablingDoesNotResurrectFeedbackOrPreviewPublishedWhileDisabled(int transport)
    {
        var device = new RecordingDevice(transport);
        device.SetRumblePreview(true, 200, true, 240);
        device.Pump();
        device.ConfigureDualShock4ProfileOutput(false);
        device.Pump();
        device.setRumble(255, 255);
        device.SetRumblePreview(true, 255, true, 255);
        device.ConfigureDualShock4ProfileOutput(true);
        device.Pump();
        Assert.AreEqual((byte)0, device.Reports[^1][device.MotorOffset]);
        Assert.AreEqual((byte)0, device.Reports[^1][device.MotorOffset + 1]);
        device.setRumble(70, 140);
        device.Pump();
        Assert.AreEqual((byte)70, device.Reports[^1][device.MotorOffset]);
        Assert.AreEqual((byte)140, device.Reports[^1][device.MotorOffset + 1]);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(4)]
    public void CompletionOfAnOlderStopCannotAcknowledgeAnotherDisable(int transport)
    {
        var device = new RecordingDevice(transport);
        device.setRumble(150, 200);
        device.Pump();
        device.ConfigureDualShock4ProfileOutput(false);
        device.DuringWrite = () =>
        {
            device.ConfigureDualShock4ProfileOutput(true);
            device.ConfigureDualShock4ProfileOutput(false);
        };
        device.Pump();
        device.DuringWrite = null;
        // No new nonzero report was admitted during the rapid toggle, so the
        // accepted old zero already neutralized this same sole-writer lifetime.
        device.Pump();
        Assert.AreEqual(2, device.Reports.Count);
        device.AssertNeutral(device.Reports[1]);
        // A later enabled report is a new possible-active state and MUST stop.
        device.ConfigureDualShock4ProfileOutput(true);
        device.setRumble(100, 100);
        device.Pump();
        device.ConfigureDualShock4ProfileOutput(false);
        device.Pump();
        Assert.AreEqual(4, device.Reports.Count);
        device.AssertNeutral(device.Reports[3]);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(4)]
    public void DisablePublicationDoesNotWaitForAnInFlightWriter(int transport)
    {
        var device = new RecordingDevice(transport);
        device.setRumble(200, 100);
        device.DuringWrite = () =>
        {
            Assert.IsTrue(Task.Run(() => device.ConfigureDualShock4ProfileOutput(false))
                .Wait(TimeSpan.FromSeconds(2)), "Profile publication must not take the HID writer lock.");
        };
        device.Pump();
        device.DuringWrite = null;
        device.Pump();
        Assert.AreEqual(2, device.Reports.Count);
        device.AssertNeutral(device.Reports[1]);
    }

    [TestMethod]
    public void ProfilesCannotEnableWritesForImmutableInputOnlyHardware()
    {
        var device = new RecordingDevice(0, VidPidFeatureSet.NoOutputData);
        device.ConfigureDualShock4ProfileOutput(true);
        Assert.IsFalse(device.SupportsPhysicalOutput);
        Assert.IsTrue(device.FeatureSet.HasFlag(VidPidFeatureSet.NoOutputData));
        device.ConfigureDualShock4ProfileOutput(false);
        device.Pump();
        Assert.AreEqual(0, device.Reports.Count);
        // Even an unrelated mutable flag change cannot create the ctor capability.
        device.ModifyFeatureSetFlag(VidPidFeatureSet.NoOutputData, false);
        device.ConfigureDualShock4ProfileOutput(true);
        device.setRumble(200, 200);
        device.Pump();
        Assert.AreEqual(0, device.Reports.Count);
    }

    [TestMethod]
    public void AHardwareOutputFailureFlagStillPreventsTheFinalWrite()
    {
        var device = new RecordingDevice(0);
        device.ModifyFeatureSetFlag(VidPidFeatureSet.NoOutputData, true);
        device.ConfigureDualShock4ProfileOutput(false);
        device.Pump();
        device.ConfigureDualShock4ProfileOutput(true);
        device.Pump();
        Assert.AreEqual(0, device.Reports.Count);
        Assert.IsTrue(device.FeatureSet.HasFlag(VidPidFeatureSet.NoOutputData));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(4)]
    public void DisabledAtStartupDoesNotProbeAnOtherwiseUnprovenOutputInterface(int transport)
    {
        var device = new RecordingDevice(transport);
        device.ConfigureDualShock4ProfileOutput(false);
        device.setRumble(255, 255);
        device.SetRumblePreview(true, 255, true, 255);
        Assert.IsTrue(device.Pump());
        Assert.AreEqual(0, device.Reports.Count);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(4)]
    public void RejectedNonzeroRemainsPossiblyActiveUntilStopIsAccepted(int transport)
    {
        var device = new RecordingDevice(transport) { Accept = false };
        device.setRumble(160, 190);
        device.Pump();
        device.ConfigureDualShock4ProfileOutput(false);
        device.Accept = true;
        device.Pump();
        Assert.AreEqual(2, device.Reports.Count);
        device.AssertNeutral(device.Reports[1]);
    }

    [TestMethod]
    public void AudioMailboxOwnsRetryAfterDeviceAdmitsTheStop()
    {
        var device = new RecordingDevice(5);
        device.setRumble(160, 190);
        device.Pump();
        device.AudioMailbox = new DualShock4BluetoothEffectMailbox(78);
        device.ConfigureDualShock4ProfileOutput(false);
        device.Pump();
        byte[] claimed = new byte[78];
        Assert.IsTrue(device.AudioMailbox.TryClaim(claimed, out int length, out long version));
        Assert.AreEqual(78, length);
        device.AssertNeutral(claimed);
        Assert.AreEqual((byte)0xA1, claimed[2]);
        Assert.AreEqual((byte)0xF1, claimed[3]);
        Assert.AreEqual((byte)40, claimed[23]);
        Assert.AreEqual((byte)60, claimed[24]);
        device.AudioMailbox.Reject(version);
        device.Pump();
        Assert.AreEqual(2, device.Reports.Count);
        Assert.IsTrue(device.AudioMailbox.TryClaim(claimed, out _, out long retryVersion));
        Assert.AreEqual(version, retryVersion);
        device.AssertNeutral(claimed);
        device.AudioMailbox.Acknowledge(retryVersion);
        Assert.IsFalse(device.AudioMailbox.HasPending);
    }

    [TestMethod]
    public void AudioModeControlCanOwnActiveMotorsBeforeTheFirstOrdinaryEffectPass()
    {
        var device = new RecordingDevice(5);
        var rumble = new DS4ForceFeedbackState
        {
            RumbleMotorStrengthLeftHeavySlow = 200,
            RumbleMotorStrengthRightLightFast = 100,
        };
        device.SetRumbleState(ref rumble);
        device.PublishAudioModeControl();
        Assert.AreEqual((byte)100, device.Reports[0][6]);
        device.ConfigureDualShock4ProfileOutput(false);
        device.Pump();
        Assert.AreEqual(2, device.Reports.Count);
        device.AssertNeutral(device.Reports[1]);
        // A subsequent mode change while disabled cannot reintroduce motors.
        device.SetRumbleState(ref rumble);
        device.PublishAudioModeControl();
        device.AssertNeutral(device.Reports[2]);
    }

    [TestMethod]
    public void RuntimeInputOnlyFoundationRetainsItsImmutableNoHidPolicy()
    {
        var device = new RuntimeInputOnly();
        var features = device.FeatureSet;
        device.ConfigureDualShock4ProfileOutput(false);
        device.ConfigureDualShock4ProfileOutput(true);
        Assert.AreEqual(features, device.FeatureSet);
        Assert.IsFalse(device.HasHidInterface);
        Assert.IsFalse(device.SupportsPhysicalOutput);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(5)]
    public void WarmPolicyTransitionsAndWriterCompositionAllocateNothing(int transport)
    {
        var device = new RecordingDevice(transport) { CaptureReports = false };
        for (int i = 0; i < 2000; i++) Step();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20000; i++) Step();
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.AreEqual(0L, after - before);

        void Step()
        {
            device.ConfigureDualShock4ProfileOutput(true);
            device.setRumble(100, 200);
            device.Pump();
            device.ConfigureDualShock4ProfileOutput(false);
            device.Pump();
            device.Pump();
        }
    }

    private sealed class RuntimeInputOnly()
        : DS4Device("Runtime input-only", InputDeviceType.Switch2Pro, ConnectionType.USB);

    private sealed class RecordingDevice : DS4Device
    {
        private readonly Func<bool, bool, bool, bool> send;
        private readonly bool hasBluetoothCrc;
        internal readonly List<byte[]> Reports = new();
        internal bool Accept = true;
        internal bool CaptureReports = true;
        internal bool Throw;
        internal Action DuringWrite;
        internal DualShock4BluetoothEffectMailbox AudioMailbox;
        internal int MotorOffset { get; }

        internal RecordingDevice(int transport, VidPidFeatureSet features = VidPidFeatureSet.DefaultDS4)
            : base((HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice)),
                "Recording DS4", transport == 3 ? features | VidPidFeatureSet.OnlyOutputData0x05 : features)
        {
            deviceType = InputDeviceType.DS4;
            conType = transport == 0 ? ConnectionType.USB : transport == 1 ? ConnectionType.SONYWA : ConnectionType.BT;
            hasBluetoothCrc = transport >= 2 && transport != 3;
            MotorOffset = hasBluetoothCrc ? 6 : 4;
            outReportBuffer = new byte[hasBluetoothCrc ? 78 : 64];
            outputReport = new byte[outReportBuffer.Length];
            send = typeof(DS4Device).GetMethod("sendOutputReport", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<bool, bool, bool, bool>>(this);
            if (hasBluetoothCrc)
                typeof(DS4Device).GetField("btOutputPayloadLen", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(this, 78);
            if (transport >= 4)
            {
                var audioState = (DualShock4BluetoothAudioState)typeof(DS4Device)
                    .GetField("bluetoothAudioState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
                Assert.IsTrue(audioState.Update(true, transport == 5, 60, 50, 40, null));
                Assert.IsTrue(RegisterDualShock4BluetoothAudioControlLane(this, Record, Record));
            }
        }

        internal bool Pump() => send(true, true, false);
        internal void PublishAudioModeControl()
        {
            var build = typeof(DS4Device).GetMethod("CreateDualShock4BluetoothAudioControlReport",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<DualShock4BluetoothAudioState.Snapshot, byte[], bool, byte[]>>(this);
            Record(build(new DualShock4BluetoothAudioState.Snapshot(true, true, 60, 50, 40), null!, false));
        }
        protected override bool writeOutput() => Record(conType == ConnectionType.BT ? outputReport : outReportBuffer);
        private bool Record(byte[] report)
        {
            if (CaptureReports) Reports.Add((byte[])report.Clone());
            DuringWrite?.Invoke();
            if (Throw) throw new IOException("Injected output rejection");
            return Accept && (AudioMailbox == null || AudioMailbox.TryPublish(report));
        }

        internal void AssertNeutral(byte[] report)
        {
            Assert.AreEqual((byte)0, report[MotorOffset]);
            Assert.AreEqual((byte)0, report[MotorOffset + 1]);
            int flagsOffset = MotorOffset == 6 ? 3 : 1;
            Assert.AreEqual(1, report[flagsOffset] & 7, "Stop rumble without changing lightbar/flash.");
            if (hasBluetoothCrc)
            {
                uint expected = DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(0xA2, report, report.Length - 4);
                Assert.AreEqual(expected, BitConverter.ToUInt32(report, report.Length - 4));
            }
        }
    }
}
