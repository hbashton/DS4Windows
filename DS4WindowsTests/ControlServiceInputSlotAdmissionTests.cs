using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

public sealed partial class Switch2RuntimeRegistrationServiceTests
{
    [TestMethod]
    [DoNotParallelize]
    public void TwoSlotProductionHostReproducesOldDualSenseCollisionAndAcceptsSharedAdmission()
    {
        foreach (bool sharedAdmission in new[] { false, true })
        {
            var table = new InputControllerRegistrationTable(2);
            var controllers = new DS4Device[2];
            var touchPads = new Mouse[2];
            var slots = new ControllerSlotManager();
            var admission = new ControlServiceInputSlotAdmission(table, controllers, slots);
            var service = new Switch2RuntimeRegistrationService(table,
                slotAdmission: sharedAdmission ? admission : null);
            var profile = new CoexistenceProfileStage();
            var host = new Switch2ControlServiceReversibleProfileSlotHost(table,
                service.LifecycleGate, controllers, touchPads, slots, profile, static (_, _, _) => { });
            var savedMode = Global.GyroOutputMode[1];
            var savedPostMap = Mapping.mapStickActionData[1];
            Global.GyroOutputMode[1] = GyroOutMode.None;
            Mapping.mapStickActionData[1] = new Mapping.PostMapStickData();
            Assert.IsTrue(service.TryOpen(97_001, out _));
            var legacy = CreateLegacyDualSenseShell();
            Assert.IsTrue(admission.TryClaimLegacySlot(0, legacy));
            slots.AddController(legacy, 0);
            CreateBluetoothOwner(97_101, 97_201, 97_301, out var bluetooth, out _);
            try
            {
                bool attached = service.TryAttachToHost(bluetooth, host, TimeoutMilliseconds,
                    out var token, out var failure);
                if (!sharedAdmission)
                {
                    Assert.IsFalse(attached, "The previous first-table-free path must reproduce the bug.");
                    Assert.AreEqual(Switch2RuntimeRegistrationTransactionFailureKind.PrepareRejected, failure.Kind);
                    Assert.AreEqual(InputControllerSlotTableFailure.None, failure.TableFailure);
                    Assert.AreEqual("exact:ProvenRejected/SlotOccupied", host.LastPreparePhase);
                    Assert.AreEqual(0, profile.PrepareCount);
                    Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.AbortedUnpublished, bluetooth.State);
                }
                else
                {
                    Assert.IsTrue(attached, failure.Kind + ":" + host.LastPreparePhase);
                    Assert.AreEqual(1, token.Slot);
                    Assert.AreSame(bluetooth.RuntimeDevice, controllers[1]);
                    Assert.AreSame(bluetooth.RuntimeDevice, slots.ControllerDict[1]);
                    Assert.AreSame(bluetooth.RuntimeDevice, touchPads[1].BoundDevice);
                    Assert.AreEqual(1, profile.PrepareCount);
                }
                Assert.AreSame(legacy, controllers[0]);
                Assert.AreSame(legacy, slots.ControllerDict[0]);
            }
            finally
            {
                Assert.IsTrue(service.TryClose(97_001, TimeoutMilliseconds, out var close), close.Kind.ToString());
                Assert.AreSame(legacy, controllers[0]);
                slots.RemoveController(legacy, 0);
                Assert.IsTrue(admission.TryReleaseLegacySlot(0, legacy));
                Global.GyroOutputMode[1] = savedMode;
                Mapping.mapStickActionData[1] = savedPostMap;
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void TwoSlotProductionHostKeepsEarlierSwitch2WhenLegacyDualSenseArrives()
    {
        var table = new InputControllerRegistrationTable(2);
        var controllers = new DS4Device[2];
        var slots = new ControllerSlotManager();
        var admission = new ControlServiceInputSlotAdmission(table, controllers, slots);
        var service = new Switch2RuntimeRegistrationService(table, slotAdmission: admission);
        var host = new Switch2ControlServiceReversibleProfileSlotHost(table,
            service.LifecycleGate, controllers, new Mouse[2], slots,
            new CoexistenceProfileStage(), static (_, _, _) => { });
        var savedMode = Global.GyroOutputMode[0];
        var savedPostMap = Mapping.mapStickActionData[0];
        Global.GyroOutputMode[0] = GyroOutMode.None;
        Mapping.mapStickActionData[0] = new Mapping.PostMapStickData();
        Assert.IsTrue(service.TryOpen(97_011, out _));
        CreateBluetoothOwner(97_111, 97_211, 97_311, out var bluetooth, out _);
        var legacy = CreateLegacyDualSenseShell();
        try
        {
            Assert.IsTrue(service.TryAttachToHost(bluetooth, host, TimeoutMilliseconds,
                out var token, out var failure), failure.Kind + ":" + host.LastPreparePhase);
            Assert.AreEqual(0, token.Slot);
            Assert.IsFalse(admission.TryClaimLegacySlot(0, legacy));
            Assert.IsTrue(admission.TryClaimLegacySlot(1, legacy));
            slots.AddController(legacy, 1);
            Assert.AreSame(bluetooth.RuntimeDevice, controllers[0]);
            Assert.AreSame(bluetooth.RuntimeDevice, slots.ControllerDict[0]);
            Assert.AreSame(legacy, controllers[1]);
            Assert.AreEqual(2, slots.ControllerColl.Count);
        }
        finally
        {
            Assert.IsTrue(service.TryClose(97_011, TimeoutMilliseconds, out var close), close.Kind.ToString());
            Assert.AreSame(legacy, controllers[1]);
            slots.RemoveController(legacy, 1);
            Assert.IsTrue(admission.TryReleaseLegacySlot(1, legacy));
            Global.GyroOutputMode[0] = savedMode;
            Mapping.mapStickActionData[0] = savedPostMap;
        }
    }

    [TestMethod]
    public void DefaultMixedRuntimeAttachmentsSkipLegacyDualSenseArrayClaim()
    {
        var table = new InputControllerRegistrationTable(4);
        var controllers = new DS4Device[4];
        var slots = new ControllerSlotManager();
        var admission = new ControlServiceInputSlotAdmission(table, controllers, slots);
        var service = new Switch2RuntimeRegistrationService(table, slotAdmission: admission);
        var host = new RecordingControlServiceSlotHost();
        Assert.IsTrue(service.TryOpen(92_001, out _));
        DS4Device dualSense = CreateLegacyDualSenseShell();
        Assert.IsTrue(admission.TryClaimLegacySlot(0, dualSense));
        slots.AddController(dualSense, 0);
        Assert.AreEqual(InputControllerSlotState.Empty, table.GetSnapshot()[0].State,
            "Legacy composite workers remain outside typed worker ownership.");

        CreateUsbOwner(92_101, 92_201, out var usb, out _);
        CreateBluetoothOwner(92_102, 92_202, 92_302, out var bluetooth, out _);
        CreateJoinedOwner(92_103, 92_303, out var joined, out _, out _);
        try
        {
            Assert.IsTrue(service.TryAttachToHost(usb, host, TimeoutMilliseconds,
                out var usbToken, out var usbFailure), usbFailure.Kind.ToString());
            Assert.IsTrue(service.TryAttachToHost(bluetooth, host, TimeoutMilliseconds,
                out var bluetoothToken, out var bluetoothFailure), bluetoothFailure.Kind.ToString());
            Assert.IsTrue(service.TryAttachToHost(joined, host, TimeoutMilliseconds,
                out var joinedToken, out var joinedFailure), joinedFailure.Kind.ToString());
            CollectionAssert.AreEqual(new[] { 1, 2, 3 },
                new[] { usbToken.Slot, bluetoothToken.Slot, joinedToken.Slot });
            Assert.AreSame(dualSense, controllers[0]);
            Assert.AreSame(dualSense, slots.ControllerDict[0]);
            Assert.AreEqual(0, host.AbortCount);
        }
        finally
        {
            Assert.IsTrue(service.TryClose(92_001, TimeoutMilliseconds, out var close), close.Kind.ToString());
            slots.RemoveController(dualSense, 0);
            Assert.IsTrue(admission.TryReleaseLegacySlot(0, dualSense));
        }
    }

    [TestMethod]
    public void LegacyClaimCannotOverwriteEarlierRuntimeReservationOrAttachedSlot()
    {
        var table = new InputControllerRegistrationTable(2);
        var controllers = new DS4Device[2];
        var admission = new ControlServiceInputSlotAdmission(table, controllers, new ControllerSlotManager());
        var service = new Switch2RuntimeRegistrationService(table, slotAdmission: admission);
        Assert.IsTrue(service.TryOpen(92_011, out _));
        CreateBluetoothOwner(92_111, 92_211, 92_311, out var bluetooth, out _);
        var dualSense = CreateLegacyDualSenseShell();
        try
        {
            // The host deliberately leaves the legacy array empty. The table
            // claim must protect the slot even before a host can fill it.
            Assert.IsTrue(service.TryAttachToHost(bluetooth, new RecordingControlServiceSlotHost(),
                TimeoutMilliseconds, out var token, out var failure), failure.Kind.ToString());
            Assert.AreEqual(0, token.Slot);
            Assert.IsNull(controllers[0]);
            Assert.IsFalse(admission.TryClaimLegacySlot(0, dualSense));
            Assert.IsTrue(admission.TryClaimLegacySlot(1, dualSense));
            Assert.IsFalse(admission.TryReleaseLegacySlot(1, CreateLegacyDualSenseShell()),
                "Foreign cleanup must never clear a newer or unrelated array claim.");
            Assert.AreSame(dualSense, controllers[1]);
        }
        finally
        {
            Assert.IsTrue(service.TryClose(92_011, TimeoutMilliseconds, out _));
            Assert.IsTrue(admission.TryReleaseLegacySlot(1, dualSense));
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FullOrExactOccupiedAdmissionDoesNotConstructParticipantOrConsumeOwner(bool exact)
    {
        var table = new InputControllerRegistrationTable(1);
        var controllers = new DS4Device[1];
        var admission = new ControlServiceInputSlotAdmission(table, controllers, new ControllerSlotManager());
        int constructions = 0;
        var service = new Switch2RuntimeRegistrationService(table, 1_000,
            owner => new Switch2ProUsbRuntimeRegistrationParticipant(owner),
            owner => { constructions++; return new Switch2BluetoothRuntimeRegistrationParticipant(owner); },
            owner => new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner), admission);
        Assert.IsTrue(service.TryOpen(92_021, out _));
        var dualSense = CreateLegacyDualSenseShell();
        Assert.IsTrue(admission.TryClaimLegacySlot(0, dualSense));
        CreateBluetoothOwner(92_121, 92_221, 92_321, out var bluetooth, out _);
        var host = new RecordingControlServiceSlotHost();
        try
        {
            InputControllerSlotToken token;
            Switch2RuntimeRegistrationTransactionFailure failure;
            bool accepted = exact ? service.TryAttachExactSlot(0, bluetooth, host,
                    TimeoutMilliseconds, out token, out failure) :
                service.TryAttachToHost(bluetooth, host, TimeoutMilliseconds, out token, out failure);
            Assert.IsFalse(accepted);
            Assert.IsFalse(token.IsValid);
            Assert.AreEqual(Switch2RuntimeRegistrationTransactionFailureKind.TableRejected, failure.Kind);
            Assert.AreEqual(exact ? InputControllerSlotTableFailure.Busy : InputControllerSlotTableFailure.Full,
                failure.TableFailure);
            Assert.AreEqual(0, constructions);
            Assert.AreEqual(0, host.PrepareCount);
            Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Created, bluetooth.State);
            Assert.IsTrue(bluetooth.Registration.IsOwnerAuthenticated);

            Assert.IsTrue(admission.TryReleaseLegacySlot(0, dualSense));
            Assert.IsTrue(service.TryAttachToHost(bluetooth, host, TimeoutMilliseconds,
                out token, out failure), "The untouched owner can attach after the legacy slot is free.");
            Assert.AreEqual(0, token.Slot);
            Assert.AreEqual(1, constructions);
        }
        finally { Assert.IsTrue(service.TryClose(92_021, TimeoutMilliseconds, out _)); }
    }

    [TestMethod]
    public void RetainedDictionaryOccupancyAndConfiguredSlotLimitAreRespected()
    {
        var table = new InputControllerRegistrationTable(3);
        var controllers = new DS4Device[3];
        var slots = new ControllerSlotManager();
        var legacy = CreateLegacyDualSenseShell();
        slots.AddController(legacy, 0);
        var admission = new ControlServiceInputSlotAdmission(table, controllers, slots, slotLimit: 2);
        var service = new Switch2RuntimeRegistrationService(table, slotAdmission: admission);
        Assert.IsTrue(service.TryOpen(92_031, out _));
        CreateBluetoothOwner(92_131, 92_231, 92_331, out var bluetooth, out _);
        try
        {
            Assert.IsFalse(admission.TryClaimLegacySlot(0, legacy));
            Assert.IsFalse(admission.TryClaimLegacySlot(2, legacy));
            Assert.IsTrue(service.TryAttachToHost(bluetooth, new RecordingControlServiceSlotHost(),
                TimeoutMilliseconds, out var token, out var failure), failure.Kind.ToString());
            Assert.AreEqual(1, token.Slot);
            Assert.AreSame(legacy, slots.ControllerDict[0]);
        }
        finally
        {
            Assert.IsTrue(service.TryClose(92_031, TimeoutMilliseconds, out _));
            slots.RemoveController(legacy, 0);
        }
    }

    [TestMethod]
    public async Task ConcurrentLegacyClaimAndRuntimeReservationHaveExactlyOneWinner()
    {
        for (int iteration = 0; iteration < 50; iteration++)
        {
            var table = new InputControllerRegistrationTable(1);
            var controllers = new DS4Device[1];
            var admission = new ControlServiceInputSlotAdmission(table, controllers, new ControllerSlotManager());
            Assert.IsTrue(table.TryOpen((ulong)(93_001 + iteration), out _));
            CreateBluetoothOwner((ulong)(94_001 + iteration), (ulong)(95_001 + iteration),
                (ulong)(96_001 + iteration), out var bluetooth, out _);
            var legacy = CreateLegacyDualSenseShell();
            using var start = new Barrier(2);
            InputControllerSetupRollbackClaim rollback = default;
            InputControllerSlotToken token = default;
            bool legacyClaimed = false;
            bool runtimeReserved = false;
            Task legacyTask = Task.Run(() =>
            {
                start.SignalAndWait();
                legacyClaimed = admission.TryClaimLegacySlot(0, legacy);
            });
            Task runtimeTask = Task.Run(() =>
            {
                start.SignalAndWait();
                runtimeReserved = admission.TryReserveAndBind(-1, bluetooth.Registration,
                    out token, out rollback, out _);
            });
            await Task.WhenAll(legacyTask, runtimeTask).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(legacyClaimed ^ runtimeReserved, "Only one slot owner may win concurrent admission.");
            Assert.AreEqual(legacyClaimed, ReferenceEquals(controllers[0], legacy));
            Assert.AreEqual(runtimeReserved, token.IsValid);
            Assert.IsTrue(bluetooth.TryAbortCreated(bluetooth.Registration, TimeoutMilliseconds, out _));
            if (runtimeReserved) Assert.IsTrue(table.TryRollback(rollback, out _));
            if (legacyClaimed) Assert.IsTrue(admission.TryReleaseLegacySlot(0, legacy));
            Assert.IsTrue(table.TryClose((ulong)(93_001 + iteration), out _, out _));
        }
    }

    private static DS4Device CreateLegacyDualSenseShell()
    {
        // Identity-only shell: no HID handles, workers, or transport calls.
        var device = (DualSenseDevice)RuntimeHelpers.GetUninitializedObject(typeof(DualSenseDevice));
        typeof(DS4Device).GetField("hDevice", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(device, RuntimeHelpers.GetUninitializedObject(typeof(HidDevice)));
        typeof(DS4Device).GetField("hasHidInterface", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(device, true);
        Assert.AreEqual(DS4DeviceWorkerLifecycleSupport.UnsupportedDualSenseCompositeWorkers,
            device.WorkerLifecycleSupport);
        return device;
    }

    private sealed class CoexistenceProfileStage : ISwitch2ControlServiceReversibleProfileStage
    {
        internal int PrepareCount { get; private set; }
        public Switch2ControlServiceReversibleStageResult TryPrepare(
            in Switch2ControlServiceProfileStageRequest request,
            out ISwitch2ControlServiceReversibleProfileStageInverse inverse)
        {
            PrepareCount++;
            inverse = new CoexistenceProfileInverse(request.Token);
            return Switch2ControlServiceReversibleStageResult.Success();
        }
    }

    private sealed class CoexistenceProfileInverse(InputControllerSlotToken token) :
        ISwitch2ControlServiceReversibleProfileStageInverse
    {
        private bool consumed;
        public bool Authenticates(in Switch2ControlServiceProfileStageRequest request) =>
            !consumed && request.Token == token;
        public Switch2ControlServiceReversibleStageResult TryUndo(
            in Switch2ControlServiceProfileStageRequest request)
        {
            if (!Authenticates(request))
                return Switch2ControlServiceReversibleStageResult.Reject(
                    Switch2ControlServiceReversibleStageFailureKind.InvalidCredential);
            consumed = true;
            return Switch2ControlServiceReversibleStageResult.Success();
        }
    }
}
