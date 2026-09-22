using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;
using Policy = DS4Windows.InputDevices.DualSenseBluetoothAudioPacer.NativeRumbleUpdatePolicy;

namespace DS4WindowsTests;

[TestClass]
public sealed class DualSenseSettingsRefreshAdmissionTests
{
    [TestMethod]
    public void ClassifierRequiresEveryOriginalNonvisualByteAndAllowsVisualTail()
    {
        byte[] original = Settings();
        Assert.IsTrue(DualSenseDevice.IsNativeRumbleSettingsRefresh(original, 0));
        for (int index = 0; index < 44; index++)
        {
            original[index] ^= 1;
            Assert.IsFalse(DualSenseDevice.IsNativeRumbleSettingsRefresh(original, 0), $"Original byte {index}");
            original[index] ^= 1;
        }
        original.AsSpan(44, 4).Fill(0xFF);
        Assert.IsTrue(DualSenseDevice.IsNativeRumbleSettingsRefresh(original, 0));
        Assert.IsFalse(DualSenseDevice.IsNativeRumbleSettingsRefresh(new byte[48], 0));
        var stop = new byte[48]; stop[0] = 2;
        Assert.IsFalse(DualSenseDevice.IsNativeRumbleSettingsRefresh(stop, 0));
        stop[1] = 2; stop[39] = 4;
        Assert.IsFalse(DualSenseDevice.IsNativeRumbleSettingsRefresh(stop, 0));
    }

    [TestMethod]
    public void ClassifierReadsOnlyTheBoundedOriginalUsbViewInsideARealFeedbackEnvelope()
    {
        var envelope = new byte[ViiperOutDevice.DualSenseAtomicFeedbackLength];
        Array.Fill(envelope, (byte)0xA7);
        Settings().CopyTo(envelope, 28);
        Assert.IsTrue(DualSenseDevice.IsNativeRumbleSettingsRefresh(envelope, 28));
        foreach (int offset in new[] { -1, 0, 27, 29, envelope.Length - 47, int.MaxValue })
            Assert.IsFalse(DualSenseDevice.IsNativeRumbleSettingsRefresh(envelope, offset));
        Assert.IsFalse(DualSenseDevice.IsNativeRumbleSettingsRefresh(null, 0));
        Assert.IsFalse(DualSenseDevice.IsNativeRumbleSettingsRefresh(new byte[47], 0));
    }

    [TestMethod]
    public void WarmClassifierAllocatesNothingWithPositiveAllocationControl()
    {
        byte[] original = Settings();
        bool valid = true;
        for (int index = 0; index < 6000; index++)
            valid &= DualSenseDevice.IsNativeRumbleSettingsRefresh(original, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 6000; index++)
            valid &= DualSenseDevice.IsNativeRumbleSettingsRefresh(original, 0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
        before = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(new byte[128]);
        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before >= 128);
    }

    [TestMethod]
    public void ActualRawFifoRetainsOriginalClassificationDespitePreparedTriggerAndAudioChanges()
    {
        DualSenseDevice device = Device();
        byte[] original = Settings(), prepared = Prepared(original);
        Assert.IsTrue(device.WriteRawOutputReportFromGame(prepared, 0, 48,
            out long revision, original, 0, preparedTriggerLabValidity: 0x08));
        Array.Fill(original, (byte)0xEE);
        Array.Fill(prepared, (byte)0xDD);
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
            device.ProcessNextPhysicalOutputCommand());
        Assert.AreEqual(revision, Get<long>(device, "pendingBluetoothNativeGameRevision"));
        Assert.AreEqual(Policy.SettingsRefresh, Get<Policy>(device, "pendingBluetoothNativeGameRumblePolicy"));
        byte[] retained = Get<byte[]>(device, "pendingBluetoothNativeGameExactState");
        Assert.AreEqual((byte)0x26, retained[34], "Prepared trigger bytes still accompany the same retained command.");
        Assert.IsNull(Get<object>(device, "bluetoothAudioPacer"), "The fixture must not create a helper or HID writer.");
    }

    [TestMethod]
    public void CombinedEnvelopeCarriesOriginalPolicyAndRejectedSuccessorCannotReplaceIt()
    {
        DualSenseDevice device = Device();
        var envelope = new byte[ViiperOutDevice.DualSenseAtomicFeedbackLength];
        Settings().CopyTo(envelope, 28);
        byte[] combined = Combined(Prepared(Settings()));
        Assert.IsFalse(device.WriteBluetoothCombinedHapticsAudioOutputReport(combined, 0, combined.Length,
            true, out long revision, envelope, 28, preparedTriggerLabValidity: 0x08));
        Assert.IsTrue(revision > 0);
        Assert.AreEqual(Policy.SettingsRefresh, Get<Policy>(device, "pendingBluetoothNativeGameRumblePolicy"));

        byte[] unknown = Settings(); unknown[39] = 1;
        Assert.IsFalse(device.WriteBluetoothCombinedHapticsAudioOutputReport(Combined(unknown), 0, 398,
            true, out long rejectedRevision, unknown, 0));
        Assert.AreEqual(0L, rejectedRevision);
        Assert.AreEqual(revision, Get<long>(device, "pendingBluetoothNativeGameRevision"));
        Assert.AreEqual(Policy.SettingsRefresh, Get<Policy>(device, "pendingBluetoothNativeGameRumblePolicy"));
        Invoke(device, "ClearPendingBluetoothNativeGameTransition");
        Assert.AreEqual(Policy.Authoritative, Get<Policy>(device, "pendingBluetoothNativeGameRumblePolicy"));
    }

    [DataTestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    public void UnprovenSourceOrSyntheticCombinedShapeCannotAcquireProtection(
        bool wrongVendor, bool missingOriginal, bool missingAttributes)
    {
        DualSenseDevice device = Device(wrongVendor ? (ushort)0x1234 : (ushort)0x054C, missingAttributes);
        byte[] raw = Settings(), combined = Combined(raw);
        Assert.IsFalse(device.WriteBluetoothCombinedHapticsAudioOutputReport(combined, 0, combined.Length,
            true, out long revision, missingOriginal ? null : raw, 0));
        Assert.IsTrue(revision > 0);
        Assert.AreEqual(Policy.Authoritative, Get<Policy>(device, "pendingBluetoothNativeGameRumblePolicy"));
    }

    [TestMethod]
    public void ActualParentAdmissionSerializesPolicyWithoutChangingExactCommandBytes()
    {
        using var fixture = new ParentFixture();
        byte[] exact = Combined(Prepared(Settings()));
        byte[] quiescent = (byte[])exact.Clone();
        DualSenseDevice.ConsumeNativeGameStateValidity(quiescent, 13);
        Assert.IsTrue(fixture.Pacer.UpdateGameStateAndTemplate(exact, quiescent, long.MaxValue,
            out bool unavailable, Policy.SettingsRefresh));
        Assert.IsFalse(unavailable);
        Assert.IsTrue(fixture.Pacer.UpdateGameStateAndTemplate(exact, quiescent, long.MaxValue));
        var commands = fixture.Drain();
        Assert.AreEqual(2, commands.Count);
        Assert.AreEqual((byte)Policy.SettingsRefresh, commands[0].Payload[DualSenseBluetoothAudioPacer.NativeCommandRumblePolicyOffset]);
        Assert.AreEqual((byte)Policy.Authoritative, commands[1].Payload[DualSenseBluetoothAudioPacer.NativeCommandRumblePolicyOffset]);
        foreach (var command in commands)
        {
            Assert.AreEqual(DualSenseBluetoothAudioPacer.GameStateAndTemplatePayloadLength, command.Payload.Length);
            CollectionAssert.AreEqual(exact.Skip(13).Take(47).ToArray(), command.Payload.Take(47).ToArray());
        }
        Assert.AreEqual(17, typeof(DualSenseBluetoothAudioPacer).GetField("ProtocolVersion",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue());
    }

    [DataTestMethod]
    [DataRow((byte)2)]
    [DataRow((byte)255)]
    public void UnknownParentPolicyFailsBeforeAllocatingNativeCredit(byte policy)
    {
        using var fixture = new ParentFixture();
        byte[] report = Combined(Settings());
        Assert.IsFalse(fixture.Pacer.UpdateGameStateAndTemplate(report, report, long.MaxValue,
            out bool unavailable, (Policy)policy));
        Assert.IsFalse(unavailable);
        Assert.AreEqual(0, fixture.Drain().Count);
        Assert.AreEqual(0, fixture.Credits.Count);
    }

    private static byte[] Settings() => Convert.FromHexString(
        "020C570000000000000000050000000000000000000005000000000000000000000000000000000000000000040000FF");

    private static byte[] Prepared(byte[] original)
    {
        var prepared = (byte[])original.Clone();
        prepared[1] |= 0xF0;
        prepared[22] = 0x26;
        prepared[23] = 77;
        return prepared;
    }

    private static byte[] Combined(byte[] raw)
    {
        var report = new byte[398];
        report[0] = 0x36; report[11] = 0x90; report[12] = 63;
        report[76] = 0x92; report[77] = 64;
        raw.AsSpan(1, 47).CopyTo(report.AsSpan(13));
        return report;
    }

    private static DualSenseDevice Device(ushort vendor = 0x054C, bool missingAttributes = false)
    {
        var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
        typeof(HidDevice).GetField("_deviceAttributes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(hid, new HidDeviceAttributes(new NativeMethods.HIDD_ATTRIBUTES { VendorID = vendor, ProductID = 0x0CE6 }));
        var device = new DualSenseDevice(hid, "Settings refresh admission fixture");
        if (missingAttributes)
            typeof(HidDevice).GetField("_deviceAttributes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(hid, null);
        typeof(DS4Device).GetField("conType", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(device, ConnectionType.BT);
        // Exercise durable native admission without starting any recovery,
        // helper process, or native transport on this deliberately dormant device.
        typeof(DualSenseDevice).GetField("bluetoothOutputTransportStopping", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(device, 1);
        return device;
    }

    private static T Get<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string name) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);

    // Reuse the existing queue-only parent admission fixture. Its constructor
    // creates no process, pipe, controller or physical writer.
    private sealed class ParentFixture : IDisposable
    {
        private static readonly Type FixtureType = typeof(DualSenseBluetoothLocalTriggerProofTests)
            .GetNestedType("Fixture", BindingFlags.NonPublic)!;
        private readonly object fixture = Activator.CreateInstance(FixtureType, nonPublic: true)!;
        internal DualSenseBluetoothAudioPacer Pacer => Get<DualSenseBluetoothAudioPacer>(fixture, "Pacer");
        internal DualSenseNativeCommandCredits Credits => Get<DualSenseNativeCommandCredits>(fixture, "Credits");
        internal List<(DualSenseBluetoothAudioPacer.MessageKind Kind, byte[] Payload)> Drain() =>
            (List<(DualSenseBluetoothAudioPacer.MessageKind, byte[])>)FixtureType.GetMethod("Drain",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture, null)!;
        public void Dispose() => ((IDisposable)fixture).Dispose();
    }
}
