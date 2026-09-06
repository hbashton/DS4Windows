using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.DS4Control;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

/// <summary>
/// Hardware-free production composition: real runtime owner, registration
/// transaction, reversible slot host, exact Mouse, canonical profile mapper,
/// and target encoder. Only OS transports and profile persistence are fakes.
/// Normal input always starts at the audited Common05 decoder; these tests do
/// not manually subscribe Mouse or invoke its gyro handler.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Switch2ProductionGyroMappingIntegrationTests
{
    // Exercise a real DSU-visible slot as well as the canonical output path.
    private const int Slot = 3;
    private const ulong ServiceGeneration = 93_001;
    private const int Timeout = 2_000;
    private const uint CButton = 1u << 14;

    [DataTestMethod]
    [DataRow(0, DS4Controls.BLP, 0x02000000u)]
    [DataRow(0, DS4Controls.BRP, 0x01000000u)]
    [DataRow(0, DS4Controls.Capture, 0x00002000u)]
    [DataRow(1, DS4Controls.BLP, 0x02000000u)]
    [DataRow(1, DS4Controls.BRP, 0x01000000u)]
    [DataRow(1, DS4Controls.Capture, 0x00002000u)]
    public void ProExtraButtonsReachCanonicalXboxBindingAndRelease(
        int source, DS4Controls control, uint rawButton)
    {
        using var fixture = new Fixture(source, extraControl: control);
        fixture.PublishButtons(0);
        Assert.IsFalse(fixture.Last.Mapped.Cross);
        fixture.PublishButtons(rawButton);
        Assert.IsTrue(fixture.Last.Mapped.Cross,
            $"Decoded {control} must execute its saved canonical A binding.");
        Assert.AreNotEqual(0u, fixture.Last.Xbox.Buttons & 4u);
        fixture.PublishButtons(0);
        Assert.IsFalse(fixture.Last.Mapped.Cross);
        Assert.AreEqual(0u, fixture.Last.Xbox.Buttons & 4u);
        fixture.PublishButtons(rawButton);
        fixture.Remove();
        Assert.IsFalse(fixture.Last.Mapped.Cross);
        Assert.AreEqual(0u, fixture.Last.Xbox.Buttons & 4u);
        Assert.AreEqual(0, fixture.SystemInputCalls);
    }

    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    public void RawStickCalibrationReleasesCanonicalGyroAndSwipeMappingsWithoutRetiringRuntime(int source, bool swipe)
    {
        using var fixture = new Fixture(source, directionalSwipe: swipe, stickCalibration: true);
        fixture.Publish(true);
        if (swipe) Assert.IsTrue(fixture.Last.Mapped.Cross);
        else Assert.IsTrue(fixture.Last.GyroActive);
        Assert.IsTrue(fixture.Device.TryBeginRawStickCalibration(Switch2StickSide.Left, false, out var capture));
        Assert.AreEqual(Switch2RuntimeReportKind.Regular, fixture.Last.Kind);
        AssertNeutralRight(fixture.Last);
        Assert.IsFalse(fixture.Last.GyroActive);
        Assert.IsFalse(fixture.Last.Mapped.Cross);
        Assert.AreEqual((short)0, fixture.Last.Xbox.LeftStickX);
        fixture.Publish(true);
        AssertNeutralRight(fixture.Last);
        Assert.IsFalse(fixture.Last.Mapped.Cross);
        Assert.AreEqual((short)0, fixture.Last.Xbox.LeftStickX);
        Assert.IsTrue(fixture.Device.CancelRawStickCalibration(capture));
        fixture.Publish(false);
        fixture.Publish(true);
        if (swipe) Assert.IsTrue(fixture.Last.Mapped.Cross);
        else Assert.IsTrue(fixture.Last.GyroActive);
        Assert.AreEqual(0, fixture.TerminalCalls);
        Assert.AreEqual(0, fixture.SystemInputCalls);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void RegisteredRawMotionActivatesOnTheSameReportAndReleases(int source)
    {
        using var fixture = new Fixture(source);
        fixture.Publish(false);
        Assert.IsFalse(fixture.Last.GyroActive);
        AssertNeutralRight(fixture.Last);

        fixture.Publish(true);
        Assert.IsTrue(fixture.Last.GyroActive,
            "The admitted gyro stage must observe this report's C button, not the prior report.");
        Assert.IsTrue(fixture.Last.Mapped.RX != 128 || fixture.Last.Mapped.RY != 128,
            "Raw IMU must reach the actual MouseJoystick mapper without test event wiring.");
        Assert.AreEqual((byte)128, fixture.Last.Mapped.LX);
        Assert.IsTrue(fixture.Last.Mapped.LXAxis.IsHighResolution);
        Assert.AreEqual((ushort)0x801, fixture.Last.Switch.LeftStickX,
            "The gyro stage must not quantize the independent physical stick.");
        Assert.IsTrue(fixture.Last.Xbox.LeftStickX > 0);
        Assert.AreEqual(fixture.Last.Xbox.LeftStickX,
            BinaryPrimitives.ReadInt16LittleEndian(fixture.Last.XboxPacket.AsSpan(12)));
        Assert.AreEqual(fixture.Last.Xbox.RightStickX,
            BinaryPrimitives.ReadInt16LittleEndian(fixture.Last.XboxPacket.AsSpan(16)));
        Assert.AreEqual(2, fixture.RegularCalls,
            "A joined logical report must run one gyro/mapping pipeline, not one per physical half.");

        fixture.Publish(false);
        Assert.IsFalse(fixture.Last.GyroActive);
        AssertNeutralRight(fixture.Last);
        Assert.AreEqual(3, fixture.RegularCalls);
        Assert.AreEqual(0, fixture.SystemInputCalls);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void TerminalReportReleasesGyroBeforeMappingAndBeforeProfileCleanup(int source)
    {
        using var fixture = new Fixture(source);
        fixture.Publish(true);
        Assert.IsTrue(fixture.Last.GyroActive);
        fixture.Remove();
        Assert.AreEqual(1, fixture.TerminalCalls);
        Assert.AreEqual(Switch2RuntimeReportKind.TerminalNeutral, fixture.Last.Kind);
        AssertNeutralRight(fixture.Last);
        Assert.IsFalse(fixture.Last.GyroActive);
        Assert.IsFalse(fixture.Last.Mapped.Cross);
        Assert.AreEqual((short)0, fixture.Last.Xbox.LeftStickX);
        Assert.IsTrue(fixture.ProfileCleanupObservedTerminal);
        Assert.IsNull(fixture.TouchPads[Slot]);
        Assert.IsNull(fixture.Controllers[Slot]);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void TerminalReportCannotConsumePendingGyroFromAnUnmappedRegularReport(int source)
    {
        using var fixture = new Fixture(source);
        fixture.SkipNextMap = true;
        fixture.Publish(true);
        Assert.IsTrue(fixture.TouchPads[Slot].GyroMouseJoystickOutputActive);
        Assert.AreEqual(1, fixture.RegularCalls);
        Assert.IsNull(fixture.Last,
            "This fixture deliberately leaves the real producer's pending vector unconsumed.");
        fixture.Remove();
        Assert.AreEqual(1, fixture.TerminalCalls);
        AssertNeutralRight(fixture.Last);
        Assert.IsFalse(fixture.Last.GyroActive);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void TerminalReportReleasesActualDirectionalSwipeButtonMapping(int source)
    {
        using var fixture = new Fixture(source, directionalSwipe: true);
        fixture.Publish(true);
        Assert.IsTrue(fixture.Last.Mapped.Cross,
            "At least one raw-derived directional swipe must reach the canonical A binding.");
        Assert.AreNotEqual(0u, fixture.Last.Xbox.Buttons & 4u);
        fixture.Remove();
        Assert.IsFalse(fixture.Last.Mapped.Cross,
            "Terminal raw neutral alone must not replay a retained Mouse swipe flag.");
        Assert.AreEqual(0u, fixture.Last.Xbox.Buttons & 4u);
        Assert.IsTrue(fixture.ProfileCleanupObservedTerminal);
    }

    [TestMethod]
    public void RawSixAxisAndCopiedRetiredReportDelegatesCannotDriveTheSuccessor()
    {
        using var fixture = new Fixture(0);
        fixture.Publish(false);
        Mouse oldMouse = fixture.TouchPads[Slot];
        var oldDevice = fixture.Device;
        Delegate oldReport = fixture.CaptureReportDelegate();
        var oldEnvelope = fixture.LastEnvelope;
        int calls = fixture.RegularCalls;

        // Deliberately bypass production publication. Direct mode must not
        // leave a raw SixAxis subscription that grants independent admission.
        Global.SAMousestickTriggers[Slot] = "-1";
        oldDevice.SixAxis.FireSixAxisEvent(new SixAxisEventArgs(DateTime.UnixEpoch,
            new SixAxis(0, 0, 0, 0, 0, 0, 0.002)
            { gyroYawFull = 9000, gyroPitchFull = 8000, gyroRollFull = 7000 }));
        Assert.AreEqual(calls, fixture.RegularCalls);
        Assert.IsFalse(oldMouse.GyroMouseJoystickOutputActive);
        Assert.AreEqual((byte)128, Mapping.gyroStickX[Slot]);
        Assert.AreEqual((byte)128, Mapping.gyroStickY[Slot]);
        Global.SAMousestickTriggers[Slot] = "30";

        fixture.Remove();
        fixture.Attach(0, successor: true);
        fixture.Publish(false);
        Mouse successor = fixture.TouchPads[Slot];
        Assert.AreNotSame(oldMouse, successor);
        calls = fixture.RegularCalls;
        TargetInvocationException rejected = Assert.ThrowsException<TargetInvocationException>(() =>
            oldReport.DynamicInvoke(oldDevice, oldEnvelope));
        Assert.IsInstanceOfType<InvalidOperationException>(rejected.InnerException);
        Assert.AreEqual(calls, fixture.RegularCalls);
        Assert.IsFalse(successor.GyroMouseJoystickOutputActive);
        AssertNeutralRight(fixture.Last);
        Assert.IsFalse(oldDevice.TryPublishPro(ProFrame(93_101, 93_201,
            false, 10, CButton)), "Removed runtime publication must remain fenced.");
        Assert.AreEqual(calls, fixture.RegularCalls);
    }

    [TestMethod]
    public void TableRetirementRejectsRawPublicationBeforeMouseEvenWhileRuntimeIsActive()
    {
        using var fixture = new Fixture(0);
        fixture.Publish(false);
        Assert.IsTrue(fixture.Table.TryBeginRetire(fixture.Token, out _, out var failure),
            failure.ToString());
        int calls = fixture.RegularCalls;
        // The input owner has not stopped yet. Its source table admission,
        // not RuntimeState alone, is the authority for the production prelude.
        fixture.Publish(true, requireAccepted: false);
        Assert.AreEqual(calls, fixture.RegularCalls);
        Assert.IsFalse(fixture.TouchPads[Slot].GyroMouseJoystickOutputActive);
        Assert.AreEqual((byte)128, Mapping.gyroStickX[Slot]);
        Assert.AreEqual((byte)128, Mapping.gyroStickY[Slot]);
        fixture.Remove();
        AssertNeutralRight(fixture.Last);
    }

    private static void AssertNeutralRight(Observation observed)
    {
        Assert.IsNotNull(observed);
        Assert.AreEqual((byte)128, observed.Mapped.RX);
        Assert.AreEqual((byte)128, observed.Mapped.RY);
        Assert.AreEqual((short)0, observed.Xbox.RightStickX);
        Assert.AreEqual((short)0, observed.Xbox.RightStickY);
        Assert.AreEqual((ushort)2048, observed.Switch.RightStickX);
        Assert.AreEqual((ushort)2048, observed.Switch.RightStickY);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void AutomaticUdpObservationRunsAfterCanonicalMappingForEveryRuntimeFamily(int source)
    {
        using var fixture = new Fixture(source, observeUdp: true);
        Global.Switch2CemuhookYawSensitivity[Slot] = 4;
        fixture.Publish(true);
        Assert.AreEqual(1, fixture.RegularCalls);
        Assert.IsNotNull(fixture.Last);
        Assert.IsTrue(fixture.Last.GyroActive);
        Assert.AreEqual(0, fixture.UdpCalls, "Networking has not run on the physical report callback.");
        double rawYaw = fixture.Device.getCurrentStateRef().Motion.angVelYaw;
        Assert.AreNotEqual(0.0, rawYaw);
        Assert.AreEqual(DsState.Connected, fixture.GetUdpMetadata().PadState,
            "Switch 2 must not be disconnected merely because it has no Sony serial.");
        Assert.AreEqual(1, fixture.UdpWorker.DrainOnce());
        Assert.AreEqual(1, fixture.RegularCalls, "The observer must not run another mapper.");
        Assert.AreEqual(rawYaw * 1.25, fixture.LastUdp.Motion.angVelYaw, 1e-10);
        Assert.AreEqual(rawYaw, fixture.Device.getCurrentStateRef().Motion.angVelYaw);
        Assert.AreNotSame(fixture.Device.getCurrentStateRef().Motion, fixture.LastUdp.Motion);
        Assert.AreEqual(source == 0 ? DsConnection.Usb : DsConnection.Bluetooth,
            fixture.GetUdpMetadata().ConnectionType);
        Assert.AreEqual(0, fixture.SystemInputCalls);
    }

    [TestMethod]
    public void AutomaticUdpToggleRestartAndReconnectNeedNoRawEventSubscriptions()
    {
        using var fixture = new Fixture(0, observeUdp: true);
        fixture.UdpServer.Stop();
        fixture.Publish(false);
        Assert.AreEqual(0, fixture.UdpWorker.DrainOnce());
        Assert.AreEqual(DsState.Connected, fixture.GetUdpMetadata().PadState);
        fixture.UdpServer.Start(0, "127.0.0.1");
        fixture.Publish(true);
        Assert.AreEqual(1, fixture.UdpWorker.DrainOnce());
        var originalMac = fixture.GetUdpMetadata().PadMacAddress;
        fixture.Publish(false); // pending work belongs to this exact old session
        fixture.UdpServer.Start(0, "127.0.0.1");
        Assert.AreEqual(0, fixture.UdpWorker.DrainOnce());
        fixture.Publish(true);
        Assert.AreEqual(1, fixture.UdpWorker.DrainOnce());
        Assert.AreEqual(originalMac, fixture.GetUdpMetadata().PadMacAddress);
        fixture.Publish(false); // terminal removal discards this pending observation
        fixture.Remove();
        Assert.AreEqual(0, fixture.UdpWorker.DrainOnce());
        Assert.AreEqual(DsState.Disconnected, fixture.GetUdpMetadata().PadState);
        fixture.Attach(0, successor: true);
        fixture.Publish(false);
        Assert.AreEqual(1, fixture.UdpWorker.DrainOnce());
        Assert.AreNotEqual(originalMac, fixture.GetUdpMetadata().PadMacAddress);
        Assert.AreEqual(1, fixture.CaptureReportDelegate().GetInvocationList().Length);
    }

    [TestMethod]
    public void ObserverFailureCannotRejectSuccessfulCanonicalInputOrTerminalCleanup()
    {
        using var fixture = new Fixture(0, observeUdp: true);
        fixture.ThrowObserver = true;
        fixture.Publish(true);
        Assert.AreEqual(1, fixture.RegularCalls);
        Assert.IsTrue(fixture.Last.GyroActive);
        Assert.AreEqual(0, fixture.UdpWorker.DrainOnce());
        Assert.AreEqual(1L, fixture.UdpFailureCount);
        fixture.ThrowObserver = false;
        fixture.Publish(false);
        Assert.AreEqual(1, fixture.UdpWorker.DrainOnce());
        fixture.Remove();
        Assert.IsTrue(fixture.ProfileCleanupObservedTerminal);
        Assert.AreEqual(1, fixture.TerminalCalls);
    }

    internal sealed record Observation(Switch2RuntimeReportKind Kind,
        DS4State Mapped, bool GyroActive, XboxOneEgressState Xbox,
        Switch2EgressState Switch, byte[] XboxPacket);

    internal readonly record struct RawSticks(ushort LX, ushort LY, ushort RX, ushort RY);

    internal sealed class Fixture : IDisposable
    {
        private static readonly FieldInfo StoreField = typeof(Global).GetField("m_Config",
            BindingFlags.Static | BindingFlags.NonPublic);
        private readonly BackingStore previousStore = Global.store;
        private readonly VirtualKBMBase previousHandler = Global.outputKBMHandler;
        private readonly DS4StateFieldMapping previousFields = Mapping.fieldMappings[Slot];
        private readonly DS4StateFieldMapping previousOutputFields = Mapping.outputFieldMappings[Slot];
        private readonly Mapping.SyntheticState previousSynthetic = Mapping.deviceState[Slot];
        private readonly Mapping.PostMapStickData previousPostMap = Mapping.mapStickActionData[Slot];
        private readonly byte previousGyroX = Mapping.gyroStickX[Slot], previousGyroY = Mapping.gyroStickY[Slot];
        private readonly NoSystemInputHandler systemInput = new();
        private readonly Switch2RuntimeRegistrationService service;
        private readonly Switch2ControlServiceReversibleProfileSlotHost host;
        private readonly ControlService controlShell;
        private readonly bool directionalSwipe;
        private readonly bool stickCalibration;
        private readonly DS4Controls extraControl;
        private readonly bool controllerOnly;
        private bool attached;
        private int source;
        private ulong deviceGeneration, transportGeneration;
        private uint counter;
        private uint mappedCounter;
        private DS4Device mappedDevice;
        internal readonly InputControllerRegistrationTable Table = new(Global.MAX_DS4_CONTROLLER_COUNT);
        internal readonly DS4Device[] Controllers = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
        internal readonly Mouse[] TouchPads = new Mouse[Global.MAX_DS4_CONTROLLER_COUNT];
        internal Switch2RuntimeInputDevice Device { get; private set; }
        internal InputControllerSlotToken Token { get; private set; }
        internal Observation Last { get; private set; }
        internal Switch2RuntimeReportEventArgs LastEnvelope { get; private set; }
        internal int RegularCalls { get; private set; }
        internal int TerminalCalls { get; private set; }
        internal bool SkipNextMap { get; set; }
        internal bool ProfileCleanupObservedTerminal { get; private set; }
        internal int SystemInputCalls => systemInput.Calls;
        internal UdpMotionObservationWorker UdpWorker { get; }
        internal UdpServer UdpServer { get; }
        internal DS4State LastUdp { get; private set; }
        internal int UdpCalls { get; private set; }
        internal bool ThrowObserver { get; set; }
        internal long UdpFailureCount => host.UdpObservationFailureCount;

        internal Fixture(int source, bool directionalSwipe = false, bool observeUdp = false, bool stickCalibration = false,
            DS4Controls extraControl = DS4Controls.None, bool controllerOnly = false)
        {
            this.directionalSwipe = directionalSwipe;
            this.stickCalibration = stickCalibration;
            this.extraControl = extraControl;
            this.controllerOnly = controllerOnly;
            StoreField.SetValue(null, new BackingStore());
            Global.outputKBMHandler = systemInput;
            Mapping.fieldMappings[Slot] = new();
            Mapping.outputFieldMappings[Slot] = new();
            Mapping.deviceState[Slot] = new();
            Mapping.mapStickActionData[Slot] = new();
            Mapping.ResetStickFilters(Slot);
            Mapping.ResetSwitch2ModeShiftState(Slot);
            controlShell = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            controlShell.DS4Controllers = Controllers;
            service = new Switch2RuntimeRegistrationService(Table);
            Switch2UdpMotionObserver observer = null;
            if (observeUdp)
            {
                UdpWorker = new UdpMotionObservationWorker(startWorker: false,
                    dispatch: (UdpServerSession session, ref DualShockPadMeta meta, DS4State state, byte[] packet) =>
                    {
                        Assert.IsNotNull(Last, "The canonical mapper/encoder must have run before DSU dispatch.");
                        var snapshot = new DS4StateOwnedSnapshot();
                        snapshot.Capture(state);
                        LastUdp = snapshot.State;
                        UdpCalls++;
                    });
                typeof(ControlService).GetField("switch2UdpObservations", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controlShell, UdpWorker);
                UdpServer = new UdpServer(static (int slot, ref DualShockPadMeta meta) => { });
                UdpServer.Start(0, "127.0.0.1");
                observer = new Switch2UdpMotionObserver(UdpWorker, () =>
                {
                    if (ThrowObserver) throw new InvalidOperationException("Test-only observer failure");
                    // This callback runs at capture/enqueue, not at eventual
                    // worker dispatch. Moving Observe before the mapper must
                    // fail even though tests deliberately drain later.
                    Assert.AreEqual(counter, mappedCounter);
                    Assert.AreSame(Device, mappedDevice);
                    return UdpServer.CurrentSession;
                });
            }
            host = new Switch2ControlServiceReversibleProfileSlotHost(Table, service.LifecycleGate,
                Controllers, TouchPads, new ControllerSlotManager(), new ProfileStage(this), Map, observer);
            Assert.IsTrue(service.TryOpen(ServiceGeneration, out var failure), failure.Kind.ToString());
            try
            {
                Attach(source);
            }
            catch
            {
                // A red production-wiring assertion must not contaminate the
                // next test's global mapper, even before using can own us.
                try { service.TryClose(ServiceGeneration, Timeout, out _); }
                finally { UdpWorker?.Dispose(); UdpServer?.Stop(); RestoreGlobals(); }
                throw;
            }
        }

        internal DualShockPadMeta GetUdpMetadata()
        {
            object[] args = { Slot, default(DualShockPadMeta) };
            typeof(ControlService).GetMethod("GetPadDetailForIdx", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controlShell, args);
            return (DualShockPadMeta)args[1];
        }

        internal void Attach(int source, bool successor = false)
        {
            this.source = source;
            counter = 0;
            deviceGeneration = successor ? 93_102UL : 93_101UL;
            transportGeneration = successor ? 93_202UL : 93_201UL;
            InputControllerSlotToken token;
            Switch2RuntimeRegistrationTransactionFailure failure;
            bool accepted;
            // Reuse only the audited fake OS discovery/pump factories. The
            // owner, transaction and host below are the production classes.
            if (source == 0)
            {
                object[] args = { deviceGeneration, transportGeneration, null, null };
                OwnerFactory("CreateUsbOwner").Invoke(null, args);
                var owner = (Switch2ProUsbRuntimeOwner)args[2];
                Device = (Switch2RuntimeInputDevice)owner.Registration.Device;
                BindStickCalibration();
                accepted = service.TryAttachExactSlot(Slot, owner, host, Timeout, out token, out failure);
            }
            else if (source == 1)
            {
                object[] args = { deviceGeneration, transportGeneration, 93_301UL, null, null };
                OwnerFactory("CreateBluetoothOwner").Invoke(null, args);
                var owner = (Switch2BluetoothRuntimeOwner)args[3];
                Device = (Switch2RuntimeInputDevice)owner.Registration.Device;
                BindStickCalibration();
                accepted = service.TryAttachExactSlot(Slot, owner, host, Timeout, out token, out failure);
            }
            else if (source == 2)
            {
                object[] args = { deviceGeneration, 93_301UL, null, null, null };
                OwnerFactory("CreateJoinedOwner").Invoke(null, args);
                var owner = (Switch2JoyConJoinedRuntimeOwner)args[2];
                Device = (Switch2RuntimeInputDevice)owner.Registration.Device;
                BindStickCalibration();
                accepted = service.TryAttachExactSlot(Slot, owner, host, Timeout, out token, out failure);
            }
            else
            {
                var model = source is 3 or 5 ? Switch2ControllerModel.JoyCon2Left : Switch2ControllerModel.JoyCon2Right;
                object[] args = { model, deviceGeneration, transportGeneration, 93_301UL, null, null };
                OwnerFactory("CreateBluetoothOwnerForModel").Invoke(null, args);
                var owner = (Switch2BluetoothRuntimeOwner)args[4];
                Device = (Switch2RuntimeInputDevice)owner.Registration.Device;
                accepted = service.TryAttachExactSlot(Slot, owner, host, Timeout, out token, out failure);
            }
            Assert.IsTrue(accepted, failure.Kind + ":" + host.LastPreparePhase);
            Token = token;
            attached = true;
            Assert.AreSame(Device, Controllers[Slot]);
            Assert.AreSame(Device, TouchPads[Slot].BoundDevice);
            Assert.AreEqual(1, CaptureReportDelegate().GetInvocationList().Length,
                "Registration installs exactly one logical report handler.");
        }

        private void BindStickCalibration()
        {
            if (!stickCalibration) return;
            var left = new Switch2RawStickCalibrationCollectorTests.Fixture(source == 2 ?
                Switch2ControllerModel.JoyCon2Left : Switch2ControllerModel.ProController2,
                source == 0, Switch2StickSide.Left);
            var right = new Switch2RawStickCalibrationCollectorTests.Fixture(
                Switch2ControllerModel.JoyCon2Right, false, Switch2StickSide.Right, generation: 2);
            Assert.IsTrue(Device.TryBindRawStickCalibrationPersistence(new TestRawStickCalibrationStore(),
                left.Peer, source == 2 ? right.Peer : default));
        }

        private static MethodInfo OwnerFactory(string name) =>
            typeof(Switch2RuntimeRegistrationServiceTests).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Static);

        internal Delegate CaptureReportDelegate() => (Delegate)typeof(Switch2RuntimeInputDevice)
            .GetField("reportHandlers", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Device);

        internal void Publish(bool cPressed, bool requireAccepted = true)
            => PublishButtons(cPressed ? CButton : 0, requireAccepted);

        internal void PublishButtons(uint buttons, bool requireAccepted = true)
            => PublishSides(0, buttons, requireAccepted);

        internal void PublishSides(uint leftButtons, uint rightButtons, bool requireAccepted = true,
            RawSticks? sticks = null)
        {
            counter++;
            bool accepted = source switch
            {
                0 or 1 => Device.TryPublishPro(ProFrame(deviceGeneration, transportGeneration,
                    source == 1, counter, leftButtons | rightButtons, sticks)),
                2 => Device.TryPublishJoinedJoyCon(JoinedFrame(counter, rightButtons, leftButtons, sticks)),
                _ => Device.TryPublishStandaloneJoyCon(StandaloneFrame(source, deviceGeneration,
                    transportGeneration, counter, source is 3 or 5 ? leftButtons : rightButtons, sticks))
            };
            if (requireAccepted) Assert.IsTrue(accepted, "The real registered input publication was rejected.");
        }

        internal void Remove()
        {
            Assert.IsTrue(service.TryRemove(Token, Timeout, out var failure), failure.Kind.ToString());
            attached = false;
        }

        private void Configure()
        {
            if (extraControl != DS4Controls.None)
            {
                // Use the real model-specific inventory, not a test-created
                // list that could conceal missing production registration.
                var extras = (List<DS4Controls>)typeof(ControlService)
                    .GetMethod("GetKnownExtraButtons", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(controlShell, new object[] { Device });
                Global.RefreshExtrasButtons(Slot, extras);
                Global.GetDS4CSetting(Slot, extraControl).UpdateSettings(false,
                    X360Controls.A, string.Empty, DS4KeyType.None);
                Global.CacheProfileCustomsFlags(Slot);
                Assert.IsTrue(Global.containsCustomAction(Slot));
            }
            Global.GyroOutputMode[Slot] = directionalSwipe ? GyroOutMode.DirectionalSwipe : GyroOutMode.MouseJoystick;
            Global.SAMousestickTriggers[Slot] = "30";
            Global.SAMouseStickTriggerCond[Slot] = false;
            Global.store.gyroMouseStickTriggerTurns[Slot] = true;
            Global.GyroMouseStickHorizontalAxis[Slot] = 0;
            TouchPads[Slot].ToggleGyroStick = false;
            var gyro = Global.GetGyroMouseStickInfo(Slot);
            gyro.deadZone = 0; gyro.maxZone = 128;
            gyro.antiDeadX = gyro.antiDeadY = 0;
            gyro.useSmoothing = false; gyro.jitterCompensation = false;
            gyro.outputStick = GyroMouseStickInfo.OutputStick.RightStick;
            gyro.outputStickDir = GyroMouseStickInfo.OutputStickAxes.XY;
            Global.LSModInfo[Slot].Reset(); Global.RSModInfo[Slot].Reset();
            Global.Switch2DualJoyConGyroFusionEnabled[Slot] = true;
            if (controllerOnly)
            {
                Global.GyroOutputMode[Slot] = GyroOutMode.None;
                Global.Switch2JoyConStandaloneHoldMode[Slot] = source >= 5 ?
                    Switch2JoyConHoldMode.Horizontal : Switch2JoyConHoldMode.Vertical;
            }
            if (directionalSwipe)
            {
                var swipe = Global.GetGyroSwipeInfo(Slot);
                swipe.triggers = "30"; swipe.triggerTurns = true; swipe.triggerCond = false;
                swipe.deadzoneX = swipe.deadzoneY = 1; swipe.delayTime = 0;
                foreach (DS4Controls control in new[] { DS4Controls.GyroSwipeLeft, DS4Controls.GyroSwipeRight,
                    DS4Controls.GyroSwipeUp, DS4Controls.GyroSwipeDown })
                {
                    var setting = Global.store.GetDS4CSetting(Slot, control);
                    setting.actionType = DS4ControlSettings.ActionType.Button;
                    setting.action.actionBtn = X360Controls.A;
                }
            }
        }

        private void Map(DS4Device device, EventArgs args, int slot)
        {
            Assert.AreSame(Device, device);
            Assert.AreEqual(Slot, slot);
            LastEnvelope = (Switch2RuntimeReportEventArgs)args;
            if (LastEnvelope.Kind == Switch2RuntimeReportKind.Regular)
            {
                RegularCalls++;
                if (SkipNextMap) { SkipNextMap = false; return; }
            }
            else TerminalCalls++;
            var transformed = new DS4State();
            var mapped = new DS4State();
            Mapping.SetCurveAndDeadzone(slot, device.getCurrentStateRef(), transformed, device);
            Mapping.MapCustom(slot, transformed, mapped, new DS4StateExposed(transformed), TouchPads[slot], controlShell);
            transformed.CopyExtrasTo(mapped);
            Last = new Observation(LastEnvelope.Kind, mapped, TouchPads[slot].GyroMouseJoystickOutputActive,
                XboxOneEgressState.FromLegacyMappedState(mapped, -1),
                ViiperStatePacketBuilder.BuildSwitch2State(mapped, -1),
                ViiperStatePacketBuilder.Build(ViiperVirtualDeviceType.XboxOne, mapped, -1));
            mappedCounter = counter;
            mappedDevice = device;
        }

        public void Dispose()
        {
            try
            {
                if (attached) Remove();
                Assert.IsTrue(service.TryClose(ServiceGeneration, Timeout, out var failure), failure.Kind.ToString());
            }
            finally
            {
                UdpWorker?.Dispose();
                UdpServer?.Stop();
                RestoreGlobals();
            }
        }

        private void RestoreGlobals()
        {
            Mapping.ResetStickFilters(Slot);
            Mapping.ResetSwitch2ModeShiftState(Slot);
            Mapping.fieldMappings[Slot] = previousFields;
            Mapping.outputFieldMappings[Slot] = previousOutputFields;
            Mapping.deviceState[Slot] = previousSynthetic;
            Mapping.mapStickActionData[Slot] = previousPostMap;
            Mapping.gyroStickX[Slot] = previousGyroX; Mapping.gyroStickY[Slot] = previousGyroY;
            Global.outputKBMHandler = previousHandler;
            StoreField.SetValue(null, previousStore);
        }

        private sealed class ProfileStage(Fixture fixture) : ISwitch2ControlServiceReversibleProfileStage
        {
            public Switch2ControlServiceReversibleStageResult TryPrepare(
                in Switch2ControlServiceProfileStageRequest request,
                out ISwitch2ControlServiceReversibleProfileStageInverse inverse)
            {
                fixture.Configure();
                inverse = new ProfileInverse(fixture, request.Token);
                return Switch2ControlServiceReversibleStageResult.Success();
            }
        }

        private sealed class ProfileInverse(Fixture fixture, InputControllerSlotToken token) :
            ISwitch2ControlServiceReversibleProfileStageInverse
        {
            private bool consumed;
            public bool Authenticates(in Switch2ControlServiceProfileStageRequest request) =>
                !consumed && request.Token == token;
            public Switch2ControlServiceReversibleStageResult TryUndo(in Switch2ControlServiceProfileStageRequest request)
            {
                if (!Authenticates(request)) return Switch2ControlServiceReversibleStageResult.Reject(
                    Switch2ControlServiceReversibleStageFailureKind.InvalidCredential);
                fixture.ProfileCleanupObservedTerminal = fixture.Last?.Kind == Switch2RuntimeReportKind.TerminalNeutral &&
                    !fixture.TouchPads[Slot].GyroMouseJoystickOutputActive;
                consumed = true;
                return Switch2ControlServiceReversibleStageResult.Success();
            }
        }
    }

    private static Switch2ProProfileInputFrame ProFrame(ulong deviceGeneration,
        ulong transportGeneration, bool bluetooth, uint counter, uint buttons, RawSticks? sticks = null)
    {
        Switch2InputProtocolIdentity identity;
        if (bluetooth) Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid, Switch2InputCodec.Common05CharacteristicUuid,
            Switch2GattProperty.Read | Switch2GattProperty.Notify, Switch2ControllerModel.ProController2, out identity));
        else Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateProController2Usb(
            Switch2InputProtocolIdentity.NintendoUsbVendorId, Switch2InputProtocolIdentity.ProController2UsbProductId,
            Switch2InputProtocolIdentity.AuditedProController2UsbBcdDevice, out identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity, deviceGeneration,
            transportGeneration, 10_000_000, out var descriptor));
        var canonical = Decode(descriptor, counter, buttons, sticks);
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(canonical, out var mapped, out var failure), failure.ToString());
        return mapped;
    }

    private static Switch2JoyConProfileInputFrame JoinedFrame(uint counter, uint buttons, uint leftButtons = 0,
        RawSticks? sticks = null)
    {
        var left = Descriptor(Switch2ControllerModel.JoyCon2Left, 15_101, 15_201);
        var right = Descriptor(Switch2ControllerModel.JoyCon2Right, 15_102, 15_202);
        var snapshot = new Switch2JoyConPairSnapshot(15_001, Decode(left, counter, leftButtons, sticks),
            Decode(right, counter, buttons, sticks), 0);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateJoined(15_001, left, right, out var state));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapJoined(state, snapshot, out _, out var mapped,
            out var failure), failure.ToString());
        return mapped;
    }

    private static Switch2JoyConProfileInputFrame StandaloneFrame(int source, ulong device,
        ulong transport, uint counter, uint buttons, RawSticks? sticks = null)
    {
        var model = source is 3 or 5 ? Switch2ControllerModel.JoyCon2Left : Switch2ControllerModel.JoyCon2Right;
        var descriptor = Descriptor(model, device, transport);
        var mode = Switch2JoyConProfileInputMapper.StandaloneModeFor(model, source >= 5 ?
            Switch2JoyConHoldMode.Horizontal : Switch2JoyConHoldMode.Vertical);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(mode, descriptor, out var state));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(state, Decode(descriptor, counter, buttons, sticks),
            out _, out var mapped, out var failure), failure.ToString());
        return mapped;
    }

    private static Switch2InputSessionDescriptor Descriptor(Switch2ControllerModel model, ulong device, ulong transport)
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid, Switch2GattProperty.Read | Switch2GattProperty.Notify,
            model, out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity, device, transport, 10_000_000, out var descriptor));
        return descriptor;
    }

    private static Switch2CanonicalInputFrame Decode(in Switch2InputSessionDescriptor descriptor,
        uint counter, uint buttons, RawSticks? sticks = null)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(descriptor.Identity.Model,
            descriptor.DeviceGeneration, out var calibration));
        var session = new Switch2InputSession(descriptor, calibration);
        byte[] packet = new byte[Switch2InputCodec.UsbPacketLength];
        packet[0] = (byte)Switch2InputReportKind.Common05;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), counter);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), buttons);
        var axes = sticks ?? new RawSticks(0x801, 0x800, 0x800, 0x800);
        PackStick(packet, 1 + 0x0A, axes.LX, axes.LY);
        PackStick(packet, 1 + 0x0D, axes.RX, axes.RY);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x32), 4096);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x36), 1500);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x38), 1800);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + 0x3A), -2100);
        ReadOnlySpan<byte> report = descriptor.Identity.Model == Switch2ControllerModel.ProController2 &&
            descriptor.Identity.Transport == Switch2Transport.Usb ? packet : packet.AsSpan(1);
        Assert.IsTrue(session.TryProcess(descriptor, report, 100_000 + counter * 20_000L,
            out var canonical, out var failure), failure.ToString());
        return canonical;
    }

    private static void PackStick(byte[] packet, int offset, ushort x, ushort y)
    {
        packet[offset] = (byte)x;
        packet[offset + 1] = (byte)(((x >> 8) & 15) | ((y & 15) << 4));
        packet[offset + 2] = (byte)(y >> 4);
    }

    private sealed class NoSystemInputHandler : VirtualKBMBase
    {
        internal int Calls { get; private set; }
        private void Reject() { Calls++; Assert.Fail("Controller-only mapping emitted system input."); }
        public override bool Connect() { Reject(); return false; }
        public override bool Disconnect() { Reject(); return false; }
        public override void MoveRelativeMouse(int x, int y) => Reject();
        public override void MoveAbsoluteMouse(double x, double y) => Reject();
        public override void PerformMouseWheelEvent(int vertical, int horizontal) => Reject();
        public override void PerformMouseButtonEvent(uint button) => Reject();
        public override void PerformMouseButtonPress(uint button) => Reject();
        public override void PerformMouseButtonRelease(uint button) => Reject();
        public override void PerformKeyPress(uint key) => Reject();
        public override void PerformKeyPressAlt(uint key) => Reject();
        public override void PerformKeyRelease(uint key) => Reject();
        public override void PerformKeyReleaseAlt(uint key) => Reject();
        public override string GetDisplayName() => "No system input";
        public override string GetIdentifier() => "production-gyro-test-only";
        public override string GetFullDisplayName() => GetDisplayName();
    }
}
