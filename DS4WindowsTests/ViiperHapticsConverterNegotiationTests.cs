using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Control;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ViiperHapticsConverterNegotiationTests
{
    [DataTestMethod]
    [DataRow(ViiperVirtualDeviceType.DualSense, false, true, ConnectionType.BT, true, true)]
    [DataRow(ViiperVirtualDeviceType.DualSenseEdge, false, true, ConnectionType.BT, true, true)]
    [DataRow(ViiperVirtualDeviceType.DualSense, false, true, ConnectionType.USB, true, false)]
    [DataRow(ViiperVirtualDeviceType.DualSense, false, false, ConnectionType.BT, true, false)]
    [DataRow(ViiperVirtualDeviceType.DualSense, true, true, ConnectionType.BT, true, false)]
    [DataRow(ViiperVirtualDeviceType.DualSenseEdge, false, true, ConnectionType.BT, false, false)]
    [DataRow(ViiperVirtualDeviceType.DualShock4, false, true, ConnectionType.BT, true, false)]
    [DataRow(ViiperVirtualDeviceType.Switch2Pro, false, true, ConnectionType.BT, true, false)]
    [DataRow(ViiperVirtualDeviceType.Xbox360, false, true, ConnectionType.BT, true, false)]
    public void OnlyVerifiedCompatibleSonyBluetoothMediaRequestsConversion(
        ViiperVirtualDeviceType type, bool gamepadOnly, bool verified,
        ConnectionType connection, bool edgeCompatible, bool expected) =>
        Assert.AreEqual(expected, ViiperHapticsConverter.ShouldRequest(type,
            gamepadOnly, verified, connection, edgeCompatible));

    [DataTestMethod]
    [DataRow("{}", true, "box16")]
    [DataRow("null", true, "box16")]
    [DataRow("{\"hapticsConverter\":\"box16\"}", true, "box16")]
    [DataRow("{\"hapticsConverter\":\"box16\"}", false, "box16")]
    [DataRow("{\"hapticsConverter\":\"sony-bt-wdl-sinc64-v1\"}", true, "sony-bt-wdl-sinc64-v1")]
    public void ActualSelectionIsExplicitAndOldBrokerResponseRemainsLegacy(
        string json, bool requested, string expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual(expected, ViiperHapticsConverter.ParseSelection(document.RootElement, requested));
        Assert.AreEqual(ViiperHapticsConverter.Legacy,
            ViiperHapticsConverter.ParseSelection(default, requested));
    }

    [DataTestMethod]
    [DataRow("{\"hapticsConverter\":\"sony-bt-wdl-sinc64-v1\"}", false)]
    [DataRow("{\"hapticsConverter\":\"unknown\"}", true)]
    [DataRow("{\"hapticsConverter\":true}", true)]
    [DataRow("{\"hapticsConverter\":null}", true)]
    [DataRow("[]", true)]
    public void InvalidOrUnsolicitedSelectionNeverClaimsWdl(string json, bool requested)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.ThrowsException<IOException>(() =>
            ViiperHapticsConverter.ParseSelection(document.RootElement, requested));
    }

    [DataTestMethod]
    [DataRow("box16", false, false, true)]
    [DataRow("box16", false, true, false)]
    [DataRow("box16", true, true, true)]
    [DataRow("box16", true, false, true)]
    [DataRow("sony-bt-wdl-sinc64-v1", true, true, true)]
    [DataRow("sony-bt-wdl-sinc64-v1", true, false, false)]
    public void ReuseUsesEffectiveConverterWithoutOldBrokerRecreateLoop(
        string actual, bool attempted, bool desired, bool expected) =>
        Assert.AreEqual(expected, ViiperHapticsConverter.CanReuse(actual, attempted, desired));

    [TestMethod]
    public void ExplicitOptionSurvivesExistingAliasFallbackWithoutChangingAliases()
    {
        const string raw = "dualsensecombinedaudioduplexv5rawinputevents";
        const string events = "dualsensecombinedaudioduplexv5events";
        const string legacy = "dualsensecombinedaudioduplexv5";
        var attempts = new List<string>();
        ViiperDeviceStream Open(string alias)
        {
            attempts.Add(alias);
            using JsonDocument request = JsonDocument.Parse(ViiperClient.SerializeDeviceCreateRequest(
                alias, deviceSpecific: new ViiperHapticsConverter.CreateOptions()));
            Assert.AreEqual(alias, request.RootElement.GetProperty("type").GetString());
            Assert.AreEqual(ViiperHapticsConverter.SonyBluetooth,
                request.RootElement.GetProperty("deviceSpecific").GetProperty("hapticsConverter").GetString());
            if (alias != legacy)
                throw new ViiperApiException(400, "Bad Request", "unknown device type: " + alias);
            return null;
        }
        ViiperOutDevice.OpenRawInputV5StreamWithFallback(Open, raw, events, legacy, true,
            out bool rawStatus, out bool eventsStatus);
        CollectionAssert.AreEqual(new[] { raw, events, legacy }, attempts);
        Assert.IsFalse(rawStatus);
        Assert.IsFalse(eventsStatus);
        using JsonDocument unchanged = JsonDocument.Parse(ViiperClient.SerializeDeviceCreateRequest(raw));
        Assert.IsFalse(unchanged.RootElement.TryGetProperty("deviceSpecific", out _));
    }

    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void RealSlotBindingRejectsBothIncompatibleConverterDirections(bool wdl, bool sony)
    {
        using var fixture = new Fixture(wdl, requested: wdl, sony: sony);
        Assert.IsFalse(fixture.Manager.TryBindExistingUnboundOutput(fixture.Slot,
            fixture.Bound, 0, "Target", OutContType.ViiperDualSense, out OutputDevice produced));
        Assert.IsNull(produced);
        Assert.IsNull(fixture.Bound[0]);
        Assert.AreEqual(OutSlotDevice.InputBound.Unbound, fixture.Slot.CurrentInputBound);
        Assert.AreEqual(-1, (int)Get(fixture.Output, "lastInputDeviceIndex"));
    }

    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void CompatibleOrOldBrokerSlotBindsWithoutRecreation(bool wdl, bool sony)
    {
        using var fixture = new Fixture(wdl, requested: true, sony: sony);
        Assert.IsTrue(fixture.Manager.TryBindExistingUnboundOutput(fixture.Slot,
            fixture.Bound, 0, "Target", OutContType.ViiperDualSense, out OutputDevice produced));
        Assert.AreSame(fixture.Output, produced);
        Assert.AreSame(fixture.Output, fixture.Bound[0]);
        Assert.AreEqual(1, fixture.Manager.NumAttachedDevices);
        Assert.AreEqual(0, (int)Get(fixture.Output, "lastInputDeviceIndex"));
        if (wdl) Assert.AreSame(fixture.Source, Get(fixture.Output, "hapticsConverterTarget"));
    }

    [TestMethod]
    public void StaleSlotHintCannotRetireANewlyBoundOutput()
    {
        using var fixture = new Fixture(true, true, true);
        Assert.IsTrue(fixture.Manager.TryBindExistingUnboundOutput(fixture.Slot,
            fixture.Bound, 0, "Target", OutContType.ViiperDualSense, out _));
        fixture.Hub.DS4Controllers[0] = null;
        Assert.IsFalse(fixture.Manager.TryRetireIncompatibleHapticsOutput(
            fixture.Slot, fixture.Output, 0));
        Assert.AreSame(fixture.Output, fixture.Slot.OutputDevice);
        Assert.AreSame(fixture.Output, fixture.Bound[0]);
    }

    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void ExactIncompatibleUnboundSlotIsRetiredBeforeReplacement(bool wdl, bool sony)
    {
        using var fixture = new Fixture(wdl, wdl, sony);
        Assert.IsTrue(fixture.Manager.TryRetireIncompatibleHapticsOutput(
            fixture.Slot, fixture.Output, 0));
        Assert.AreEqual(0, fixture.Manager.NumAttachedDevices);
        Assert.IsNull(fixture.Manager.GetOutSlotDevice(fixture.Output));
        Assert.IsNull(fixture.Slot.OutputDevice);
        Assert.IsNull(fixture.Bound[0]);
        Assert.IsFalse(fixture.Manager.TryRetireIncompatibleHapticsOutput(
            fixture.Slot, fixture.Output, 0), "A consumed hint must not retire a successor.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WdlStreamCannotEnterEitherNintendoPcmInterpreter(bool switch2)
    {
        using var fixture = new Fixture(true, true, true);
        fixture.Output.BindPhysicalController(0);
        // The Nintendo shells deliberately have no runtime/session/actuator
        // state. Entering their interpreter would dereference that absent
        // state; the exact-owner gate must reject before either adapter.
        DS4Device nintendo = (DS4Device)RuntimeHelpers.GetUninitializedObject(
            switch2 ? typeof(DS4Windows.Switch2.Switch2RuntimeInputDevice) : typeof(JoyConDevice));
        fixture.Hub.DS4Controllers[0] = nintendo;
        byte[] media = new byte[474];
        media[76] = 0x36;
        media[28] = 0x02;
        media[154] = 0x7f;
        fixture.Output.ApplyAtomicAudioHapticsFeedback(media, media.Length, 0);
        Assert.IsFalse(fixture.Output.CanReuseForPhysicalController(0));
        Assert.AreEqual(76, fixture.Output.GetHapticsCompatibleFeedbackLength(
            nintendo, media.Length, freshNativeOutput: true),
            "The full compact and original native control prefix must survive independently from PCM.");
        DualSenseHapticsTranslator.Translate(media,
            fixture.Output.GetHapticsCompatibleFeedbackLength(nintendo, media.Length, true),
            76, out byte light, out byte heavy);
        Assert.AreEqual((byte)0, light);
        Assert.AreEqual((byte)0, heavy);
    }

    [TestMethod]
    public void ExactReplacementKeepsItsSlotEvenWhenAnEarlierSlotIsEmpty()
    {
        var manager = new OutputSlotManager();
        var bound = new OutputDevice[2];
        var earlier = new FakeOutput();
        var original = new FakeOutput();
        var replacement = new FakeOutput();
        manager.DeferredPlugin(earlier, -1, "", bound, OutContType.ViiperX360);
        OutSlotDevice chosen = manager.DeferredPlugin(original, -1, "", bound, OutContType.ViiperX360);
        manager.DeferredRemoval(earlier, -1, bound, true);
        manager.DeferredRemoval(original, -1, bound, true);
        Assert.AreEqual(0, manager.FindOpenSlot().Index);
        Assert.AreEqual(1, chosen.Index);
        Assert.AreSame(chosen, manager.DeferredPlugin(replacement, -1, "", bound,
            OutContType.ViiperX360, preferredSlot: chosen));
        Assert.AreSame(replacement, chosen.OutputDevice);
        var rejected = new FakeOutput();
        Assert.IsNull(manager.DeferredPlugin(rejected, -1, "", bound,
            OutContType.ViiperX360, preferredSlot: chosen));
        Assert.AreEqual(0, rejected.ConnectCount);
        manager.DeferredRemoval(replacement, -1, bound, true);
    }

    [TestMethod]
    public void ReportThreadBindingCannotAuthorizeSuccessorForWdlMedia()
    {
        using var fixture = new Fixture(true, true, true);
        fixture.Output.BindPhysicalController(0);
        DualSenseDevice successor = CreateSony();
        fixture.Hub.DS4Controllers[0] = successor;
        // Exercise the real hot binding path, not a toy owner helper. The
        // fixture is disconnected, so this publishes no socket/input data.
        fixture.Output.ConvertandSendReport(new DS4State(), 0);
        Assert.AreSame(successor, Get(fixture.Output, "publishedPhysicalControllerTargetDevice"));
        Assert.AreSame(fixture.Source, Get(fixture.Output, "hapticsConverterTarget"));
        byte[] media = new byte[474];
        media[76] = 0x36;
        // No audio service is installed on the shell: reaching that downstream
        // callback would throw. Exact-target rejection occurs before it.
        fixture.Output.ApplyAtomicAudioHapticsFeedback(media, media.Length, 0);
        Assert.AreEqual(0L, (long)Get(successor, "bluetoothCombinedHapticsGeneration"));
        fixture.Output.BindPhysicalController(0);
        Assert.AreSame(successor, Get(fixture.Output, "hapticsConverterTarget"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ControlService previousHub = DS4Windows.Program.rootHub;
        private readonly bool previousOutput = Global.EnableOutputDataToDS4[0];
        internal readonly ControlService Hub = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        internal readonly OutputSlotManager Manager = new();
        internal readonly OutputDevice[] Bound = new OutputDevice[2];
        internal readonly ViiperOutDevice Output = new(OutContType.ViiperDualSense, ViiperVirtualDeviceType.DualSense);
        internal readonly DualSenseDevice Source;
        internal readonly OutSlotDevice Slot;

        internal Fixture(bool wdl, bool requested, bool sony)
        {
            Source = sony ? CreateSony() : null;
            Hub.DS4Controllers = new DS4Device[4];
            Hub.DS4Controllers[0] = Source;
            DS4Windows.Program.rootHub = Hub;
            Global.EnableOutputDataToDS4[0] = true;
            Set(Output, "activeHapticsConverter", wdl ? ViiperHapticsConverter.SonyBluetooth : ViiperHapticsConverter.Legacy);
            Set(Output, "requestedSonyBluetoothHaptics", requested);
            Set(Output, "physicalDualSenseIdentityPath", string.Empty);
            Set(Output, "physicalDualSenseIdentityVerified", true);
            Slot = Manager.OutputSlots[0];
            Slot.AttachedDevice(Output, OutContType.ViiperDualSense, -1, "");
            ((OutputDevice[])Get(Manager, "outputDevices"))[0] = Output;
            ((Dictionary<int, OutputDevice>)Get(Manager, "deviceDict")).Add(0, Output);
            ((Dictionary<OutputDevice, int>)Get(Manager, "revDeviceDict")).Add(Output, 0);
        }

        public void Dispose()
        {
            DS4Windows.Program.rootHub = previousHub;
            Global.EnableOutputDataToDS4[0] = previousOutput;
            // No actual Connect, HID, USB/IP, worker or endpoint is created.
        }
    }

    private static DualSenseDevice CreateSony()
    {
        var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
        Set(hid, "_deviceAttributes", new HidDeviceAttributes(new NativeMethods.HIDD_ATTRIBUTES
            { VendorID = 0x054C, ProductID = 0x0CE6 }));
        var result = new DualSenseDevice(hid, "Converter negotiation fixture");
        Set(result, "conType", ConnectionType.BT);
        return result;
    }

    private sealed class FakeOutput : OutputDevice
    {
        internal int ConnectCount;
        public override void Connect() { ConnectCount++; connected = true; }
        public override void Disconnect() { connected = false; }
        public override void ConvertandSendReport(DS4State state, int device) { }
        public override void ResetState(bool submit = true) { }
        public override string GetDeviceType() => OutContType.ViiperX360.ToString();
        public override void RemoveFeedbacks() { }
        public override void RemoveFeedback(int index) { }
    }

    private static FieldInfo Field(object instance, string name)
    {
        for (Type type = instance.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null) return field;
        }
        throw new MissingFieldException(instance.GetType().FullName, name);
    }
    private static object Get(object instance, string name) => Field(instance, name).GetValue(instance);
    private static void Set(object instance, string name, object value) => Field(instance, name).SetValue(instance, value);
}
