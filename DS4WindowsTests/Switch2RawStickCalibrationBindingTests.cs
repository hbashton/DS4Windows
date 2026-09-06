using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;
using Source = DS4WindowsTests.Switch2RawStickCalibrationCollectorTests.Fixture;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2RawStickCalibrationBindingTests
{
    internal static readonly Switch2StickCalibration Calibration = new(2100, 2000, 1600, 1450, 1800, 1550);

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ProOverridePreservesFactoryEvidenceRawBytesAndSharedAxisPrecision(bool usb)
    {
        var source = new Source(usb: usb);
        var store = StoreFor(source);
        var binding = Bind(source, store);
        var original = source.Frame(2100, 2000);
        var applied = binding.Apply(original);
        Assert.AreEqual(original.Calibration, applied.Calibration);
        Assert.AreEqual(original.RawBody, applied.RawBody);
        Assert.AreEqual(original.Descriptor, applied.Descriptor);
        Assert.AreEqual(original.DeviceCounterRaw, applied.DeviceCounterRaw);
        Assert.AreEqual(original.CompletionTimestampQpc, applied.CompletionTimestampQpc);
        Assert.IsFalse(original.LocalStickCalibration.HasLeft);
        Assert.IsTrue(applied.TryGetLeftStick(out var left));
        Assert.IsTrue(left.HasLocalCalibration);
        Assert.AreEqual(0, left.OffsetX);
        Assert.AreEqual(0, left.OffsetY);
        Assert.AreEqual(original.Calibration.Left.Status, left.CalibrationStatus);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(applied, out var profile, out _));
        Assert.IsTrue(profile.HasLocalLeftCalibration);
        Assert.IsFalse(profile.HasLocalRightCalibration);
        var state = new DS4State();
        Assert.IsTrue(profile.TryWriteLegacyState(state));
        Assert.AreEqual((short)0, state.LXAxis.ToSigned16());
        Assert.AreEqual((ushort)2100, state.Switch2RawInputStatus.LeftStickXRaw);
        Assert.IsTrue(state.LXAxis.IsHighResolution);
        var adjacent = binding.Apply(source.Frame(2101, 2000));
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(adjacent, out profile, out _));
        Assert.AreEqual((byte)128, profile.LeftX.LegacyValue);
        Assert.AreEqual((short)20, profile.LeftX.SignedValue);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left, Switch2JoyConProfileMode.StandaloneHorizontalLeft)]
    [DataRow(Switch2ControllerModel.JoyCon2Left, Switch2JoyConProfileMode.StandaloneVerticalLeft)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, Switch2JoyConProfileMode.StandaloneHorizontalRight)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, Switch2JoyConProfileMode.StandaloneVerticalRight)]
    public void PhysicalCalibrationPrecedesEveryStandaloneOrientation(Switch2ControllerModel model, Switch2JoyConProfileMode mode)
    {
        bool isLeft = model == Switch2ControllerModel.JoyCon2Left;
        var source = new Source(model, false, isLeft ? Switch2StickSide.Left : Switch2StickSide.Right);
        var binding = Bind(source, StoreFor(source));
        var frame = binding.Apply(source.Frame(3700, 2000));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(mode, source.Descriptor, out var mapper));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(mapper, frame, out _, out var mapped, out _));
        var physical = isLeft ? mapped.LeftSource : mapped.RightSource;
        Assert.IsTrue(physical.HasLocalCalibration);
        Assert.AreEqual((ushort)3700, physical.PhysicalStickXRaw);
        bool horizontal = mode is Switch2JoyConProfileMode.StandaloneHorizontalLeft or Switch2JoyConProfileMode.StandaloneHorizontalRight;
        var x = isLeft || horizontal ? mapped.LeftX : mapped.RightX;
        var y = isLeft || horizontal ? mapped.LeftY : mapped.RightY;
        Assert.AreEqual(horizontal ? (short)0 : short.MaxValue, x.SignedValue);
        Assert.AreEqual(horizontal ? isLeft ? short.MinValue : short.MaxValue : (short)0, y.SignedValue);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2)]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void RealBluetoothSinkAppliesLoadedCalibrationWithoutManualProjection(Switch2ControllerModel model)
    {
        bool right = model == Switch2ControllerModel.JoyCon2Right;
        var source = new Source(model, false, right ? Switch2StickSide.Right : Switch2StickSide.Left);
        var runtime = Runtime(source);
        Assert.IsTrue(runtime.TryBindRawStickCalibrationPersistence(StoreFor(source),
            right ? default : source.Peer, right ? source.Peer : default));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreateBound(source.Descriptor, runtime, 1000,
            Switch2RuntimeTerminalScheduler.Instance, out var sink, out _, out _));
        int reports = 0;
        runtime.Report += (_, _) => reports++;
        runtime.StartUpdate();
        try
        {
            var raw = source.Frame(2100, 2000);
            if (model == Switch2ControllerModel.ProController2) sink.PublishPro(raw);
            else sink.PublishJoyCon(raw);
            Assert.AreEqual(1, reports);
            var state = runtime.getCurrentStateRef();
            Assert.AreEqual((short)0, state.LXAxis.ToSigned16());
            Assert.AreEqual((short)0, state.LYAxis.ToSigned16());
            Assert.AreEqual((short)0, state.RXAxis.ToSigned16());
            Assert.AreEqual((short)0, state.RYAxis.ToSigned16());
        }
        finally { runtime.StopUpdate(); }
    }

    [TestMethod]
    public void RealJoinedSinkAppliesBothIndependentPeersBeforePairMapping()
    {
        var left = new Source(Switch2ControllerModel.JoyCon2Left, false, Switch2StickSide.Left);
        var right = new Source(Switch2ControllerModel.JoyCon2Right, false, Switch2StickSide.Right, generation: 2);
        var store = StoreFor(left);
        var rightCalibration = new Switch2StickCalibration(1800, 1900, 1500, 1400, 1300, 1200);
        store.TryStore(right.Peer, right.Descriptor.Identity.Model, Switch2StickSide.Right, rightCalibration);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateJoinedJoyCon(3, 7, 1, 1, 2, 2, out var runtime, out _));
        Assert.IsTrue(runtime.TryBindRawStickCalibrationPersistence(store, left.Peer, right.Peer));
        Assert.IsTrue(Switch2JoyConJoinedRuntimeInputSink.TryCreateBound(7, left.Descriptor, right.Descriptor,
            runtime, new Switch2JoyConPairPolicy(1000), 1000, Switch2RuntimeTerminalScheduler.Instance,
            out var sink, out _, out _));
        int reports = 0;
        runtime.Report += (_, _) => reports++;
        runtime.StartUpdate();
        try
        {
            sink.PublishJoyCon(left.Frame(2100, 2000));
            Assert.AreEqual(0, reports);
            sink.PublishJoyCon(right.Frame(1800, 1900));
            Assert.AreEqual(1, reports);
            var state = runtime.getCurrentStateRef();
            Assert.AreEqual((short)0, state.LXAxis.ToSigned16());
            Assert.AreEqual((short)0, state.LYAxis.ToSigned16());
            Assert.AreEqual((short)0, state.RXAxis.ToSigned16());
            Assert.AreEqual((short)0, state.RYAxis.ToSigned16());
            Assert.IsTrue(runtime.HasLocalLeftStickCalibration && runtime.HasLocalRightStickCalibration);
        }
        finally { runtime.StopUpdate(); }
    }

    [TestMethod]
    public void WrongLifetimeCannotReceiveOverrideAndMalformedStoreCannotReplaceFactory()
    {
        var source = new Source();
        var store = StoreFor(source);
        var binding = Bind(source, store);
        var successor = new Source(generation: 2);
        Assert.IsFalse(binding.Apply(successor.Frame(2100, 2000)).LocalStickCalibration.HasLeft);
        var bluetooth = new Source(usb: false);
        Assert.IsFalse(binding.Apply(bluetooth.Frame(2100, 2000)).LocalStickCalibration.HasLeft);
        store.TryStore(source.Peer, Switch2ControllerModel.ProController2, Switch2StickSide.Left,
            new Switch2StickCalibration(2100, 2000, 1, 1, 1, 1));
        Assert.IsFalse(Bind(source, store).HasLeft);
        store.BeforeLoad = () => throw new IOException("Synthetic unavailable storage");
        Assert.IsFalse(Bind(source, store).HasLeft);
        Assert.IsTrue(binding.HasLeft, "An immutable loaded snapshot is unaffected by later disk changes.");
    }

    [TestMethod]
    public async Task SlowColdLoadDoesNotHoldPublicationAndCannotAdoptAfterActivation()
    {
        var source = new Source();
        var runtime = Runtime(source);
        var store = StoreFor(source);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.BeforeLoad = () => { entered.Set(); if (!release.Wait(3000)) throw new TimeoutException(); };
        runtime.Report += (_, _) => { };
        Task<bool> bind = Task.Run(() => runtime.TryBindRawStickCalibrationPersistence(store, source.Peer));
        try
        {
            Assert.IsTrue(entered.Wait(1000));
            await Task.Run(runtime.StartUpdate).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(Switch2RuntimeInputDeviceState.Active, runtime.RuntimeState);
            release.Set();
            Assert.IsFalse(await bind.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.IsFalse(runtime.HasLocalLeftStickCalibration);
        }
        finally { release.Set(); await bind.WaitAsync(TimeSpan.FromSeconds(4)); runtime.StopUpdate(); }
    }

    [TestMethod]
    public void WarmApplicationUsesNoStoreCallsOrManagedAllocations()
    {
        var source = new Source();
        var store = StoreFor(source);
        var binding = Bind(source, store);
        var frame = source.Frame(2101, 2000);
        for (int i = 0; i < 2000; i++) binding.Apply(frame);
        int reads = store.LoadCount;
        bool applied = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++) applied &= binding.Apply(frame).LocalStickCalibration.HasLeft;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(applied);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(reads, store.LoadCount);
    }

    internal static TestRawStickCalibrationStore StoreFor(Source source)
    {
        var store = new TestRawStickCalibrationStore();
        store.TryStore(source.Peer, source.Descriptor.Identity.Model, source.Collector.Side, Calibration);
        return store;
    }

    private static Switch2RawStickCalibrationBinding Bind(Source source, TestRawStickCalibrationStore store)
    {
        bool right = source.Descriptor.Identity.Model == Switch2ControllerModel.JoyCon2Right;
        InputDeviceType type = source.Descriptor.Identity.Model switch
        {
            Switch2ControllerModel.ProController2 => InputDeviceType.Switch2Pro,
            Switch2ControllerModel.JoyCon2Left => InputDeviceType.Switch2JoyConLeft,
            _ => InputDeviceType.Switch2JoyConRight,
        };
        Assert.IsTrue(Switch2RawStickCalibrationBinding.TryLoad(type, source.Descriptor.Identity.Transport,
            right ? 0UL : 1UL, right ? 0UL : 1UL, right ? 1UL : 0UL, right ? 1UL : 0UL,
            store, right ? default : source.Peer, right ? source.Peer : default, out var binding));
        return binding;
    }

    private static Switch2RuntimeInputDevice Runtime(Source source)
    {
        Switch2RuntimeInputDevice runtime;
        bool created = source.Descriptor.Identity.Model == Switch2ControllerModel.ProController2 ?
            Switch2RuntimeInputDevice.TryCreatePro(1, 1, source.Descriptor.Identity.Transport, out runtime, out _) :
            Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(source.Descriptor.Identity.Model, 1, 1, out runtime, out _);
        Assert.IsTrue(created);
        return runtime;
    }
}

internal sealed class TestRawStickCalibrationStore : ISwitch2RawStickCalibrationStore
{
    public object SerializationGate { get; } = new();
    private readonly Dictionary<(Switch2PersistentPeerId, Switch2ControllerModel, Switch2StickSide), Switch2StickCalibration> values = new();
    internal Action BeforeLoad;
    internal int LoadCount;
    public bool TryLoad(Switch2PersistentPeerId peer, Switch2ControllerModel model, Switch2StickSide side, out Switch2StickCalibration calibration)
    {
        Interlocked.Increment(ref LoadCount);
        BeforeLoad?.Invoke();
        return values.TryGetValue((peer, model, side), out calibration);
    }
    public bool TryStore(Switch2PersistentPeerId peer, Switch2ControllerModel model, Switch2StickSide side, in Switch2StickCalibration calibration)
    { values[(peer, model, side)] = calibration; return true; }
    public bool TryRemove(Switch2PersistentPeerId peer, Switch2ControllerModel model, Switch2StickSide side) => values.Remove((peer, model, side));
}
