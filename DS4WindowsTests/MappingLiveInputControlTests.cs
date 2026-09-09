using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Forms;

namespace DS4WindowsTests;

[TestClass]
public sealed class MappingLiveInputControlTests
{
    [DataTestMethod]
    [DataRow(false, false, false, false)]
    [DataRow(false, true, true, false)]
    [DataRow(true, false, true, false)]
    [DataRow(true, true, false, false)]
    [DataRow(true, true, true, true)]
    public void PollingRequiresExplicitEnableLoadedAndVisible(bool enabled, bool loaded, bool visible, bool expected) =>
        Assert.AreEqual(expected, MappingLiveInputControl.ShouldPoll(enabled, loaded, visible));

    [TestMethod]
    public void ExactPhysicalSourceCopiesRawNotMappedInputAndOwnsItsMotion()
    {
        using var source = new FakeSource();
        source.Raw.LX = 38;
        source.Mapped.LX = 240;
        source.Raw.Motion.gyroYawFull = 77;
        var reader = new MappingLiveInputSnapshot(source);
        reader.UseDevice(0, source.Device);
        Assert.AreEqual(MappingLiveInputStatus.Live, reader.Read(100));
        Assert.AreEqual((byte)38, reader.Raw.LX);
        Assert.AreEqual(77, reader.Raw.Motion.gyroYawFull);
        Assert.AreNotSame(source.Raw.Motion, reader.Raw.Motion);
        source.Raw.LX = 55;
        source.Raw.Motion.gyroYawFull = 99;
        Assert.AreEqual((byte)38, reader.Raw.LX);
        Assert.AreEqual(77, reader.Raw.Motion.gyroYawFull);
    }

    [TestMethod]
    public void EmptyReadingsWaitRatherThanPresentingDefaultDataAsLive()
    {
        using var source = new FakeSource();
        source.Raw.PacketCounter = 0;
        source.Raw.ReportTimeStamp = default;
        var reader = new MappingLiveInputSnapshot(source);
        reader.UseDevice(0, source.Device);
        Assert.AreEqual(MappingLiveInputStatus.Waiting, reader.Read(100));
    }

    [TestMethod]
    public void BusyTicksKeepBriefContinuityButBecomeStaleWithoutFreshReports()
    {
        using var source = new FakeSource();
        var reader = new MappingLiveInputSnapshot(source);
        reader.UseDevice(0, source.Device);
        Assert.AreEqual(MappingLiveInputStatus.Live, reader.Read(100));
        source.CanCopy = false;
        Assert.AreEqual(MappingLiveInputStatus.Live, reader.Read(133));
        Assert.AreEqual(MappingLiveInputStatus.Stale, reader.Read(1100));
        source.CanCopy = true;
        Assert.AreEqual(MappingLiveInputStatus.Stale, reader.Read(1133),
            "Successfully rereading the same old packet cannot manufacture freshness.");
        source.Raw.PacketCounter++;
        Assert.AreEqual(MappingLiveInputStatus.Live, reader.Read(1166));
    }

    [TestMethod]
    public void CounterResetCanRefreshUsingTheReportTimestampWithoutSpecialFirmwareRules()
    {
        using var source = new FakeSource();
        var reader = new MappingLiveInputSnapshot(source);
        reader.UseDevice(0, source.Device);
        Assert.AreEqual(MappingLiveInputStatus.Live, reader.Read(0));
        source.Raw.PacketCounter = 0;
        source.Raw.ReportTimeStamp = source.Raw.ReportTimeStamp.AddMilliseconds(1);
        Assert.AreEqual(MappingLiveInputStatus.Live, reader.Read(2000));
    }

    [TestMethod]
    public void OfflineBindingNeverAdoptsANewControllerUntilExplicitlyRebound()
    {
        using var source = new FakeSource();
        source.Current = null;
        var reader = new MappingLiveInputSnapshot(source);
        reader.UseDevice(0, reader.ResolveDevice(0));
        source.Current = source.Device;
        Assert.AreEqual(MappingLiveInputStatus.Waiting, reader.Read(0));
        Assert.AreEqual(0, source.CopyCount);
        reader.UseDevice(0, source.Device);
        Assert.AreEqual(MappingLiveInputStatus.Live, reader.Read(10));
    }

    [TestMethod]
    public void SlotReplacementBeforeOrDuringCopyNeverAppearsAsTheBoundController()
    {
        using var source = new FakeSource();
        using var replacement = new FakeDevice();
        var reader = new MappingLiveInputSnapshot(source);
        reader.UseDevice(0, source.Device);
        source.Current = replacement;
        Assert.AreEqual(MappingLiveInputStatus.Replaced, reader.Read(0));
        Assert.AreEqual(0, source.CopyCount);
        source.Current = source.Device;
        source.AfterCopy = () => source.Current = replacement;
        Assert.AreEqual(MappingLiveInputStatus.Replaced, reader.Read(10));
        Assert.AreEqual(1, source.CopyCount);
    }

    [TestMethod]
    public void RemovedOrDisposedPhysicalSourcesDoNotKeepPressedReadingsLive()
    {
        using var source = new FakeSource();
        var reader = new MappingLiveInputSnapshot(source);
        reader.UseDevice(0, source.Device);
        Assert.AreEqual(MappingLiveInputStatus.Live, reader.Read(0));
        source.Device.IsRemoving = true;
        Assert.AreEqual(MappingLiveInputStatus.Disconnected, reader.Read(10));
        source.Device.IsRemoving = false;
        source.ThrowDisposed = true;
        Assert.AreEqual(MappingLiveInputStatus.Disconnected, reader.Read(20));
        source.ThrowDisposed = false;
        source.Current = null;
        Assert.AreEqual(MappingLiveInputStatus.Disconnected, reader.Read(30));
    }

    [TestMethod]
    public void SnapshotCopiesDoNotAllocateInSteadyState()
    {
        using var source = new FakeSource();
        var reader = new MappingLiveInputSnapshot(source);
        reader.UseDevice(0, source.Device);
        for (int i = 0; i < 100; i++) reader.Read(0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) reader.Read(0);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [TestMethod]
    public void StandardAxesAndTriggerPercentagesDescribePhysicalInput()
    {
        var projection = new MappingLiveInputProjection();
        projection.Update(new DS4State { LX = 12, LY = 234, R2 = 255, L2 = 128, Cross = true }, InputDeviceType.DualSense);
        Assert.IsTrue(projection.Valid);
        Assert.AreEqual(12, projection.LeftX);
        Assert.AreEqual(234, projection.LeftY);
        Assert.AreEqual(255, projection.AxisMaximum);
        Assert.AreEqual(100.0, projection.RightTrigger);
        Assert.AreEqual(128.0 * 100 / 255, projection.LeftTrigger, 0.0001);
        StringAssert.Contains(projection.PressedButtons, "Cross");
        Assert.AreEqual("L2", projection.LeftTriggerName);
    }

    [TestMethod]
    public void ProDisplaysFullPrecisionSourceButtonsInsteadOfCanonicalAliases()
    {
        var state = new DS4State { Mute = true, LX = 7 };
        state.Switch2RawInputStatus = new()
        {
            IsValid = true, ContractVersion = Switch2ProProfileInputFrame.CurrentVersion,
            LeftStickXRaw = 3501, LeftStickYRaw = 2010, RightStickXRaw = 99, RightStickYRaw = 2001,
            RawButtonBits = (uint)(Switch2ProButton.FaceEast | Switch2ProButton.LeftPaddle | Switch2ProButton.LeftTrigger),
            CButton = true,
        };
        var projection = new MappingLiveInputProjection();
        projection.Update(state, InputDeviceType.Switch2Pro);
        Assert.AreEqual(3501, projection.LeftX);
        Assert.AreEqual(4095, projection.AxisMaximum);
        Assert.IsTrue(projection.InvertPlotY);
        Assert.AreEqual("A  ·  ZL  ·  GL  ·  C", projection.PressedButtons);
        Assert.AreEqual(100.0, projection.LeftTrigger);
        Assert.AreEqual("ZL", projection.LeftTriggerName);
    }

    [TestMethod]
    public void SidewaysLeftJoyConCaptureAndStickRemainPhysicalNotGuideOrRotatedAxes()
    {
        var state = new DS4State { PS = true, LX = 255, LY = 0 };
        state.Switch2JoyConRawInputStatus = new()
        {
            IsValid = true, ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
            Mode = Switch2JoyConProfileMode.StandaloneHorizontalLeft,
            LeftPresent = true, LeftPhysicalStickXRaw = 3011, LeftPhysicalStickYRaw = 1234,
            LeftRawButtonBits = 1u << 13, LeftRailSL = true,
        };
        var projection = new MappingLiveInputProjection();
        projection.Update(state, InputDeviceType.Switch2JoyConLeft);
        Assert.AreEqual("Capture  ·  L SL", projection.PressedButtons);
        Assert.AreEqual(3011, projection.LeftX);
        Assert.AreEqual(1234, projection.LeftY);
        Assert.IsFalse(projection.RightPresent);
        Assert.AreEqual(0.0, projection.RightTrigger);
    }

    [TestMethod]
    public void JoinedJoyConReportsBothPhysicalHalvesAndRightC()
    {
        var state = new DS4State();
        state.Switch2JoyConRawInputStatus = new()
        {
            IsValid = true, ContractVersion = Switch2JoyConProfileInputFrame.CurrentVersion,
            LeftPresent = true, RightPresent = true, LeftRawButtonBits = 1u << 22,
            RightRawButtonBits = 1u << 3, RightRailSR = true, CButton = true,
        };
        var projection = new MappingLiveInputProjection();
        projection.Update(state, InputDeviceType.Switch2JoyConJoined);
        Assert.AreEqual("L  ·  A  ·  R SR  ·  C", projection.PressedButtons);
        Assert.IsTrue(projection.LeftPresent && projection.RightPresent);
    }

    [TestMethod]
    public void AmbiguousOrUnknownSwitch2MetadataNeverProducesPhysicalButtonClaims()
    {
        var state = new DS4State { Cross = true };
        state.Switch2RawInputStatus = new() { IsValid = true, ContractVersion = ushort.MaxValue, CButton = true };
        var projection = new MappingLiveInputProjection();
        projection.Update(state, InputDeviceType.Switch2Pro);
        Assert.IsFalse(projection.Valid);
        Assert.AreEqual(string.Empty, projection.PressedButtons);
        state.Switch2RawInputStatus.ContractVersion = Switch2ProProfileInputFrame.CurrentVersion;
        state.Switch2JoyConRawInputStatus.IsValid = true;
        projection.Update(state, InputDeviceType.Switch2Pro);
        Assert.IsFalse(projection.Valid);
    }

    [TestMethod]
    public void UnchangedButtonPresentationReusesItsString()
    {
        var projection = new MappingLiveInputProjection();
        var state = new DS4State { Cross = true, L1 = true };
        projection.Update(state, InputDeviceType.DS4);
        string first = projection.PressedButtons;
        projection.Update(state, InputDeviceType.DS4);
        Assert.AreSame(first, projection.PressedButtons);
    }

    private sealed class FakeDevice : DS4Device, IDisposable
    {
        internal FakeDevice() : base("Live input fixture", InputDeviceType.DS4, ConnectionType.USB) => ReadWaitEv.Set();
        public void Dispose() => ReadWaitEv.Dispose();
    }

    private sealed class FakeSource : IMappingLiveInputSource, IDisposable
    {
        internal readonly FakeDevice Device = new();
        internal readonly DS4State Raw = new() { PacketCounter = 1, ReportTimeStamp = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc) };
        internal readonly DS4State Mapped = new();
        internal DS4Device Current;
        internal bool CanCopy = true;
        internal bool ThrowDisposed;
        internal int CopyCount;
        internal Action AfterCopy;
        internal FakeSource() => Current = Device;
        public DS4Device DeviceAt(int index) => index == 0 ? Current : null;
        public bool TryCopy(int index, DS4Device expected, DS4StateOwnedSnapshot raw, DS4StateOwnedSnapshot mapped)
        {
            CopyCount++;
            if (ThrowDisposed) throw new ObjectDisposedException("test input owner");
            bool result = CanCopy && expected.TryCopyControllerReadings(Raw, Mapped, raw, mapped);
            AfterCopy?.Invoke();
            return result;
        }
        public void Dispose() => Device.Dispose();
    }
}
