using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2ProfileOutputOwnershipTests
{
    [TestMethod]
    public void NoVirtualPadProfileSurvivesJoyConHandoffWithoutCreatingOutput()
    {
        using var fixture = new Fixture(initialOutput: false);
        Global.store.dinputOnly[0] = true;
        fixture.Attach();
        using var handoff = fixture.Service.BeginJoyConOutputHandoff(fixture.Token);
        fixture.RetireAndRemove();
        fixture.AdoptSuccessor(handoff);
        Assert.IsNull(fixture.Service.outputDevices[0]);
        Assert.AreEqual(0, fixture.Manager.NumAttachedDevices);
        Assert.AreEqual(0, fixture.Initial.ConnectCount);
        Assert.IsTrue(Global.useDInputOnly[0]);
    }

    [TestMethod]
    public void JoyConHandoffRetainsExactVirtualPadAndProfileForSuccessor()
    {
        using var fixture = new Fixture();
        fixture.Attach();
        using var handoff = fixture.Service.BeginJoyConOutputHandoff(fixture.Token);
        Assert.IsNotNull(handoff);
        fixture.RetireAndRemove();
        Assert.AreEqual(0, fixture.Initial.DisconnectCount);
        Assert.AreEqual(1, fixture.Manager.NumAttachedDevices);
        Assert.IsNull(fixture.Manager.FindExistUnboundSlotType(fixture.Initial.Type), "A reserved pad cannot be stolen by another input.");
        Assert.IsNull(fixture.Service.outputDevices[0]);
        fixture.AdoptSuccessor(handoff);
        Assert.AreSame(fixture.Initial, fixture.Service.outputDevices[0]);
        Assert.AreEqual(1, fixture.Initial.ConnectCount, "Adoption must not call Connect again.");
        Assert.AreEqual(0, fixture.Initial.DisconnectCount);
        Assert.AreEqual(OutContType.ViiperX360, Global.activeOutDevType[0]);
        handoff.Dispose();
        Assert.AreSame(fixture.Initial, fixture.Service.outputDevices[0]);
    }

    [TestMethod]
    public void AbandonedJoyConHandoffRetiresOnlyItsReservedOutput()
    {
        using var fixture = new Fixture();
        fixture.Attach();
        var handoff = fixture.Service.BeginJoyConOutputHandoff(fixture.Token);
        fixture.RetireAndRemove();
        Assert.AreEqual(0, fixture.Initial.DisconnectCount);
        handoff.Dispose();
        handoff.Dispose();
        Assert.AreEqual(1, fixture.Initial.DisconnectCount);
        Assert.AreEqual(0, fixture.Manager.NumAttachedDevices);
    }

    [TestMethod]
    public void FailedRetainedOutputCleanupKeepsExactReservationForRetry()
    {
        using var fixture = new Fixture();
        fixture.Attach();
        var handoff = fixture.Service.BeginJoyConOutputHandoff(fixture.Token);
        fixture.RetireAndRemove();
        fixture.Initial.ThrowOnDisconnect = true;
        Assert.ThrowsException<IOException>(handoff.Dispose);
        Assert.AreEqual(1, fixture.Manager.NumAttachedDevices);
        Assert.AreSame(fixture.Initial, fixture.Manager.GetOutSlotDevice(fixture.Initial).OutputDevice);
        Assert.IsNull(fixture.Manager.FindExistUnboundSlotType(fixture.Initial.Type));
        fixture.Initial.ThrowOnDisconnect = false;
        handoff.Dispose();
        Assert.AreEqual(0, fixture.Manager.NumAttachedDevices);
        Assert.AreEqual(2, fixture.Initial.DisconnectCount);
    }

    [TestMethod]
    public void CancelBeforeRetirementRestoresNormalOutputOwnership()
    {
        using var fixture = new Fixture();
        fixture.Attach();
        var handoff = fixture.Service.BeginJoyConOutputHandoff(fixture.Token);
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Service.BeginJoyConOutputHandoff(fixture.Token));
        handoff.Dispose();
        fixture.RetireAndRemove();
        Assert.AreEqual(1, fixture.Initial.DisconnectCount);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public void ProductionProfileInverseHandlesInitialAndLaterOutput(bool initialOutput, bool laterOutput)
    {
        using var fixture = new Fixture(initialOutput);
        fixture.Attach();
        if (laterOutput) fixture.BindOutput(fixture.Replacement);

        fixture.RetireAndRemove();

        Assert.AreEqual(initialOutput ? 1 : 0, fixture.Initial.DisconnectCount);
        Assert.AreEqual(laterOutput ? 1 : 0, fixture.Replacement.DisconnectCount);
        Assert.IsNull(fixture.Service.outputDevices[0]);
        Assert.IsNull(fixture.Service.DS4Controllers[0]);
        Assert.AreEqual(0, fixture.Manager.NumAttachedDevices);
        Assert.AreEqual(InputControllerSlotState.Removed, fixture.Table.GetSnapshot()[0].State);
    }

    [TestMethod]
    public void ProductionProfileInverseRemovesXboxOneReplacementAndReleasesPhysicalSlot()
    {
        using var fixture = new Fixture();
        fixture.Attach();
        fixture.Service.UnplugOutDev(0, fixture.Device, force: true);
        fixture.BindOutput(fixture.Replacement);
        Assert.AreEqual(1, fixture.Initial.DisconnectCount);
        Assert.AreSame(fixture.Replacement, fixture.Service.outputDevices[0]);

        fixture.RetireAndRemove();

        Assert.AreEqual(1, fixture.Replacement.DisconnectCount);
        Assert.IsNull(fixture.Service.outputDevices[0]);
        Assert.IsNull(fixture.Service.DS4Controllers[0]);
        Assert.AreEqual(0, fixture.Manager.NumAttachedDevices);
        Assert.AreEqual(InputControllerSlotState.Removed, fixture.Table.GetSnapshot()[0].State);
    }

    [TestMethod]
    public void ThrowingBindingObserverPreservesProducedOutputForExactCleanup()
    {
        using var fixture = new Fixture(initialOutput: false);
        fixture.Attach();
        fixture.Manager.DeferredPlugin(fixture.Replacement, -1, "",
            fixture.Service.outputDevices, fixture.Replacement.Type);
        var slot = fixture.Manager.GetOutSlotDevice(fixture.Replacement);
        slot.CurrentInputBoundChanged += (_, _) =>
        {
            if (slot.CurrentInputBound == DS4WinWPF.DS4Control.OutSlotDevice.InputBound.Bound)
                throw new IOException("Synthetic observer failure after binding publication.");
        };
        Assert.ThrowsException<IOException>(() =>
            fixture.Service.PluginOutDev(0, fixture.Device, fixture.Replacement.Type));
        Assert.AreSame(fixture.Replacement, fixture.Service.outputDevices[0]);
        var attempts = (ControllerVirtualOutputAttempt[])typeof(ControlService)
            .GetField("virtualOutputAttempts", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(fixture.Service);
        Assert.IsTrue(attempts[0].Failed);

        fixture.RetireAndRemove();

        Assert.AreEqual(1, fixture.Replacement.DisconnectCount);
        Assert.IsNull(fixture.Service.DS4Controllers[0]);
        Assert.AreEqual(InputControllerSlotState.Removed, fixture.Table.GetSnapshot()[0].State);
    }

    [TestMethod]
    public void CreationThrowAfterConnectBeforePublicationRetainsUncertainExactCandidate()
    {
        using var fixture = new Fixture(initialOutput: false, allocateReplacement: true);
        fixture.Attach();
        EventHandler<DebugEventArgs> observer = (_, _) =>
            throw new IOException("Synthetic observer failure after Connect before publication.");
        AppLogger.GuiLog += observer;
        bool threw = false;
        try
        {
            try { fixture.Service.PluginOutDev(0, fixture.Device, fixture.Replacement.Type); }
            catch (Exception failure) when (failure is IOException or InvalidOperationException)
            { threw = true; }
        }
        finally { AppLogger.GuiLog -= observer; }
        Assert.IsTrue(threw);
        Assert.AreEqual(1, fixture.Replacement.ConnectCount);
        Assert.AreEqual(0, fixture.Replacement.DisconnectCount);
        Assert.IsNull(fixture.Service.outputDevices[0]);
        Assert.AreEqual(0, fixture.Manager.NumAttachedDevices);
        Assert.IsFalse(fixture.TryRetireAndRemove().Succeeded,
            "An unpublished connected candidate cannot be treated as successfully retired.");
        Assert.AreSame(fixture.Device, fixture.Service.DS4Controllers[0]);
        Array ownership = (Array)typeof(ControlService).GetField(
            "switch2ProfileOutputOwners", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(fixture.Service);
        object retained = ownership.GetValue(0);
        Assert.AreSame(fixture.Replacement, retained.GetType().GetField(
            "uncertainProducedOutput", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(retained));
    }

    [TestMethod]
    public void SuccessfulRemovalThenFailedReplacementLeavesNoOutputToRetire()
    {
        using var fixture = new Fixture();
        fixture.Attach();
        fixture.Service.UnplugOutDev(0, fixture.Device, force: true);
        for (int index = 0; index < fixture.Manager.OutputSlots.Length; index++)
            fixture.Manager.DeferredPlugin(new FakeOutput(OutContType.ViiperX360),
                -1, "", fixture.Service.outputDevices, OutContType.ViiperX360);
        fixture.Service.PluginOutDev(0, fixture.Device, OutContType.ViiperXboxOne);
        Assert.IsNull(fixture.Service.outputDevices[0]);
        fixture.RetireAndRemove();
        Assert.IsNull(fixture.Service.DS4Controllers[0]);
        Assert.AreEqual(0, fixture.Replacement.DisconnectCount);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AdmittedProfileTransitionCompletesWhenRetirementBegins(bool duringDetach)
    {
        using var fixture = new Fixture();
        fixture.Attach();
        if (duringDetach) fixture.Initial.OnDisconnect = fixture.BeginRetire;
        fixture.Device.queueEvent(() =>
        {
            Assert.IsTrue(fixture.Table.TryAcquireActionLease(fixture.Token, 0,
                out var actionLease, out _));
            using (actionLease)
                fixture.Device.RunVirtualOutputTransition(() =>
                {
                    if (!duringDetach) fixture.BeginRetire();
                    fixture.Service.UnplugOutDev(0, fixture.Device, force: true);
                    fixture.BindOutput(fixture.Replacement);
                });
        });
        Assert.IsTrue(fixture.Device.TryPublishPro(
            Switch2RuntimeInputDeviceTests.CreateProFrame(702, 703, 0, timestamp: 100_000)));
        Assert.AreSame(fixture.Replacement, fixture.Service.outputDevices[0]);
        fixture.RetireAndRemove();
        Assert.AreEqual(1, fixture.Replacement.DisconnectCount);
        Assert.IsNull(fixture.Service.DS4Controllers[0]);
    }

    [TestMethod]
    public void RetirementDoesNotAdmitAnUnrelatedNewOutputChange()
    {
        using var fixture = new Fixture();
        fixture.Attach();
        fixture.BeginRetire();
        Assert.ThrowsException<InvalidOperationException>(() =>
            fixture.Service.UnplugOutDev(0, fixture.Device));
        Assert.AreEqual(0, fixture.Initial.DisconnectCount);
        fixture.RetireAndRemove();
    }

    [TestMethod]
    public void ForeignOutputAndReplacementPhysicalDeviceCannotBeAdopted()
    {
        using var fixture = new Fixture();
        fixture.Attach();
        fixture.Service.outputDevices[0] = fixture.Replacement;
        Assert.ThrowsException<InvalidOperationException>(() =>
            fixture.Service.UnplugOutDev(0, fixture.Device));
        Assert.AreEqual(0, fixture.Initial.DisconnectCount);
        Assert.AreEqual(0, fixture.Replacement.DisconnectCount);
        fixture.Service.outputDevices[0] = fixture.Initial;
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(800, 801,
            Switch2Transport.Usb, out var successor, out _));
        fixture.Service.DS4Controllers[0] = successor;
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() =>
                fixture.Service.UnplugOutDev(0, fixture.Device));
            Assert.ThrowsException<InvalidOperationException>(() =>
                fixture.Service.PluginOutDev(0, successor, OutContType.ViiperXboxOne));
            Assert.AreSame(successor, fixture.Service.DS4Controllers[0]);
            Assert.AreSame(fixture.Initial, fixture.Service.outputDevices[0]);
        }
        finally
        {
            fixture.Service.DS4Controllers[0] = fixture.Device;
            successor.ReadWaitEv.Dispose();
        }
        fixture.RetireAndRemove();
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnprovenDetachCannotBecomeCleanBecauseLegacyArrayWasCleared(bool disconnectThrows)
    {
        using var fixture = new Fixture();
        fixture.Attach();
        if (disconnectThrows)
        {
            fixture.Initial.ThrowOnDisconnect = true;
            Assert.ThrowsException<IOException>(() =>
                fixture.Service.UnplugOutDev(0, fixture.Device, force: true));
            Assert.IsNull(fixture.Service.outputDevices[0]);
            fixture.Initial.ThrowOnDisconnect = false;
        }
        else
        {
            fixture.Manager.GetOutSlotDevice(fixture.Initial).InputIndex = 7;
            Assert.ThrowsException<InvalidOperationException>(() =>
                fixture.Service.UnplugOutDev(0, fixture.Device, force: true));
            Assert.AreSame(fixture.Initial, fixture.Service.outputDevices[0]);
            Assert.AreEqual(0, fixture.Initial.DisconnectCount);
            fixture.Manager.GetOutSlotDevice(fixture.Initial).InputIndex = 0;
        }
        int warnings = 0;
        fixture.Service.Debug += (_, _) => warnings++;
        Assert.IsFalse(fixture.TryRetireAndRemove().Succeeded);
        Assert.IsFalse(fixture.RetryHostRemoval().Succeeded);
        Assert.AreEqual(1, warnings, "Repeated uncertain cleanup must report once per exact input lifetime.");
        Assert.AreSame(fixture.Device, fixture.Service.DS4Controllers[0]);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool oldDInput = Global.getDInputOnly(0);
        private readonly bool oldUseDInput = Global.useDInputOnly[0];
        private readonly OutContType oldActive = Global.activeOutDevType[0];
        private readonly OutContType oldRequested = Global.OutContType[0];
        private readonly OutContType oldTemporary = Global.outDevTypeTemp[0];
        private readonly GyroOutMode oldGyro = Global.GyroOutputMode[0];
        private readonly Mapping.PostMapStickData oldPostMap = Mapping.mapStickActionData[0];
        private readonly Switch2RuntimeRegistrationService registrationService;
        private readonly Switch2ControlServiceReversibleProfileSlotHost host;
        private readonly Switch2ControlServiceSlotLease lease;
        private readonly InputControllerSlotToken token;
        private InputControllerRetirementClaim retirementClaim;
        private readonly ControllerSlotManager controllers = new();
        private readonly bool initialOutput;
        internal readonly ControlService Service;
        internal readonly Switch2RuntimeInputDevice Device;
        internal readonly InputControllerRegistrationTable Table = new(1);
        internal readonly OutputSlotManager Manager;
        internal readonly FakeOutput Initial = new(OutContType.ViiperX360);
        internal readonly FakeOutput Replacement = new(OutContType.ViiperXboxOne);
        internal InputControllerSlotToken Token => token;

        internal Fixture(bool initialOutput = true, bool allocateReplacement = false)
        {
            this.initialOutput = initialOutput;
            Manager = allocateReplacement ? new OutputSlotManager(null, _ => Replacement) : new OutputSlotManager();
            Assert.IsTrue(Table.TryOpen(701, out _));
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(702, 703,
                Switch2Transport.Usb, out Device, out _));
            Assert.IsTrue(InputControllerRegistration.TryCreate(Device, Device.RuntimeGeneration,
                InputControllerOwnershipKind.Switch2Runtime, false, false,
                new FakeOwner(Device), out var registration, out _));
            Assert.IsTrue(Table.TryReserveAndBind(registration, out token, out _, out _));
            registrationService = new Switch2RuntimeRegistrationService(Table);
            Service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            Service.DS4Controllers = new DS4Device[1];
            Service.touchPad = new Mouse[1];
            Service.outputDevices = new OutputDevice[1];
            SetField(Service, "inputRegistrationTable", Table);
            SetField(Service, "switch2RuntimeRegistrationService", registrationService);
            SetField(Service, "outputslotMan", Manager);
            SetField(Service, "virtualOutputAttempts", new ControllerVirtualOutputAttempt[1]);
            Global.store.dinputOnly[0] = false;
            Global.useDInputOnly[0] = true;
            Global.activeOutDevType[0] = OutContType.None;
            Global.OutContType[0] = OutContType.ViiperX360;
            Global.GyroOutputMode[0] = GyroOutMode.None;
            Mapping.mapStickActionData[0] = new Mapping.PostMapStickData();
            host = new Switch2ControlServiceReversibleProfileSlotHost(Table,
                registrationService.LifecycleGate, Service.DS4Controllers, Service.touchPad,
                controllers, new RealInverseStage(this), static (_, _, _) => { });
            lease = new Switch2ControlServiceSlotLease(new object(), token);
        }

        internal void Attach()
        {
            Assert.IsTrue(host.TryPrepare(lease).Succeeded);
            Assert.IsTrue(Table.TryActivate(token, out _));
            Device.StartUpdate();
            Device.Report += OnReport;
        }

        private void OnReport(DS4Device sender, EventArgs args) =>
            Assert.IsTrue(host.TryDispatch(lease, sender,
                (Switch2RuntimeReportEventArgs)args).Succeeded);

        internal void BindOutput(FakeOutput output)
        {
            // A pre-created unbound output exercises ControlService's real
            // binding path without creating a VIIPER client or native driver.
            Manager.DeferredPlugin(output, -1, "", Service.outputDevices, output.Type);
            Service.PluginOutDev(0, Device, output.Type);
        }

        internal void RetireAndRemove()
        {
            var removed = TryRetireAndRemove();
            Assert.IsTrue(removed.Succeeded, $"{removed.Operation}/{removed.Outcome}/{removed.FailureKind}");
        }

        internal void AdoptSuccessor(ISwitch2JoyConOutputHandoff handoff)
        {
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(802, 803, Switch2Transport.Usb, out var successor, out _));
            Assert.IsTrue(InputControllerRegistration.TryCreate(successor, successor.RuntimeGeneration,
                InputControllerOwnershipKind.Switch2Runtime, false, false, new FakeOwner(successor), out var registration, out _));
            Assert.IsTrue(Table.TryReserveAndBind(registration, out var successorToken, out _, out _));
            handoff.PrepareSuccessor(successor);
            Assert.ThrowsException<InvalidOperationException>(() => handoff.PrepareSuccessor(successor));
            var stageType = typeof(ControlService).GetNestedType("Switch2ControlServiceProfileStage", BindingFlags.NonPublic);
            var stage = (ISwitch2ControlServiceReversibleProfileStage)Activator.CreateInstance(stageType,
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { Service }, null);
            var successorHost = new Switch2ControlServiceReversibleProfileSlotHost(Table,
                registrationService.LifecycleGate, Service.DS4Controllers, Service.touchPad, controllers,
                stage, static (_, _, _) => { });
            var result = successorHost.TryPrepare(new Switch2ControlServiceSlotLease(new object(), successorToken));
            Assert.IsTrue(result.Succeeded, result.FailureKind.ToString());
            successor.ReadWaitEv.Dispose();
        }

        internal void BeginRetire()
        {
            Assert.IsTrue(Table.TryBeginRetire(token, out retirementClaim, out _));
        }

        internal Switch2ControlServiceSlotHostResult TryRetireAndRemove()
        {
            if (!retirementClaim.IsValid) BeginRetire();
            Assert.IsTrue(Device.TryPublishTerminalNeutral());
            Assert.IsTrue(Table.TryAcquireTerminalReportLease(retirementClaim, Device, out var terminal, out _));
            using (terminal) Assert.IsTrue(terminal.TryAcknowledgeTerminalNeutral(out _));
            Assert.IsTrue(Table.TryMarkQuiesced(retirementClaim, out _));
            var removed = host.TryRemove(lease);
            if (removed.Succeeded) Assert.IsTrue(Table.TryCompleteRemoval(retirementClaim, out _));
            return removed;
        }

        internal Switch2ControlServiceSlotHostResult RetryHostRemoval() => host.TryRemove(lease);

        public void Dispose()
        {
            Device.Report -= OnReport;
            Initial.OnDisconnect = null;
            Initial.ThrowOnDisconnect = false;
            foreach (var slot in Manager.OutputSlots.ToArray())
                if (slot.OutputDevice != null && Manager.GetOutSlotDevice(slot.OutputDevice) != null)
                    Manager.DeferredRemoval(slot.OutputDevice, -1, Service.outputDevices, true);
            if (Replacement.IsConnected) Replacement.Disconnect();
            Global.store.dinputOnly[0] = oldDInput;
            Global.useDInputOnly[0] = oldUseDInput;
            Global.activeOutDevType[0] = oldActive;
            Global.OutContType[0] = oldRequested;
            Global.outDevTypeTemp[0] = oldTemporary;
            Global.GyroOutputMode[0] = oldGyro;
            Mapping.mapStickActionData[0] = oldPostMap;
            Device.ReadWaitEv.Dispose();
        }

        private sealed class RealInverseStage(Fixture fixture) : ISwitch2ControlServiceReversibleProfileStage
        {
            public Switch2ControlServiceReversibleStageResult TryPrepare(
                in Switch2ControlServiceProfileStageRequest request,
                out ISwitch2ControlServiceReversibleProfileStageInverse inverse)
            {
                Type type = typeof(ControlService).GetNestedType(
                    "Switch2ControlServiceProfileStageInverse", BindingFlags.NonPublic);
                inverse = (ISwitch2ControlServiceReversibleProfileStageInverse)Activator.CreateInstance(
                    type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { fixture.Service, request, default(DS4Color), true, OutContType.None }, null);
                if (fixture.initialOutput) fixture.BindOutput(fixture.Initial);
                type.GetProperty("PreparedOutput", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(inverse, fixture.initialOutput ? fixture.Initial : null);
                type.GetProperty("Prepared", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(inverse, true);
                return Switch2ControlServiceReversibleStageResult.Success();
            }
        }
    }

    private sealed class FakeOwner(Switch2RuntimeInputDevice device) : IInputControllerRegistrationOwner
    {
        public InputControllerOwnershipKind Kind => InputControllerOwnershipKind.Switch2Runtime;
        public bool Authenticates(DS4Device candidate, ulong generation) =>
            ReferenceEquals(candidate, device) && generation == device.RuntimeGeneration;
        public bool TryStopAndQuiesce(DS4Device candidate, ulong generation, int timeoutMilliseconds,
            out InputControllerOwnerOperationFailure failure)
        { failure = default; return Authenticates(candidate, generation); }
        public bool TryRemove(DS4Device candidate, ulong generation,
            out InputControllerOwnerOperationFailure failure)
        { failure = default; return Authenticates(candidate, generation); }
    }

    private sealed class FakeOutput(OutContType type) : OutputDevice
    {
        internal OutContType Type => type;
        internal int ConnectCount { get; private set; }
        internal bool IsConnected => connected;
        internal int DisconnectCount { get; private set; }
        internal Action OnDisconnect { get; set; }
        internal bool ThrowOnDisconnect { get; set; }
        public override void Connect() { ConnectCount++; connected = true; }
        public override void Disconnect()
        {
            DisconnectCount++;
            OnDisconnect?.Invoke();
            if (ThrowOnDisconnect) throw new IOException("Synthetic uncertain output removal.");
            connected = false;
        }
        public override string GetDeviceType() => type.ToString();
        public override void ConvertandSendReport(DS4State state, int device) { }
        public override void ResetState(bool submit = true) { }
        public override void RemoveFeedbacks() { }
        public override void RemoveFeedback(int inIdx) { }
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .SetValue(target, value);
}
