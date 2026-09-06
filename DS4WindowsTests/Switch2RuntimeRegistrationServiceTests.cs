using System.Collections.Concurrent;
using System.Reflection;
using DS4Windows;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed partial class Switch2RuntimeRegistrationServiceTests
{
    private const ulong ServiceGeneration = 12_001;
    private const long QpcFrequency = 10_000_000;
    private const int TimeoutMilliseconds = 2_000;
    private static readonly Guid UsbContainerGuid =
        Guid.Parse("9A99CA00-8049-4B19-8F65-65AF86EBFA10");

    [TestMethod]
    public void ServiceAdoptsControlServiceOwnedTableLifetime()
    {
        var table = new InputControllerRegistrationTable(1);
        var service = new Switch2RuntimeRegistrationService(table);
        Assert.IsTrue(table.TryOpen(ServiceGeneration,
            out var tableOpenFailure), tableOpenFailure.ToString());
        Assert.IsTrue(service.TryAdoptOpen(ServiceGeneration,
            out var adoptFailure), adoptFailure.Kind.ToString());
        Assert.IsTrue(table.TryClose(ServiceGeneration,
            out InputControllerSlotSnapshot[] snapshots,
            out var tableCloseFailure), tableCloseFailure.ToString());
        Assert.IsTrue(service.TryObserveExternalTableClose(ServiceGeneration,
            snapshots, out var observeFailure),
            observeFailure.Kind.ToString());
        Assert.IsTrue(service.TryClose(ServiceGeneration,
            TimeoutMilliseconds, out var closeFailure),
            closeFailure.Kind.ToString());
    }

    [TestMethod]
    public void OneCoreHostsMixedOwnersAndOneCloseRemovesAll()
    {
        CreateUsbOwner(12_101, 12_201, out var usbOwner,
            out var usbLease);
        CreateBluetoothOwner(12_102, 12_202, 12_302,
            out var bluetoothOwner, out var bluetoothLease);
        CreateJoinedOwner(12_103, 12_303, out var joinedOwner,
            out var leftLease, out var rightLease);

        var table = new InputControllerRegistrationTable(3);
        var participants = new ConcurrentBag<GateCheckingParticipant>();
        Switch2RuntimeRegistrationService service = null;
        int factoryGateViolations = 0;
        ISwitch2RuntimeRegistrationParticipant Wrap(
            ISwitch2RuntimeRegistrationParticipant participant)
        {
            if (Monitor.IsEntered(service.LifecycleGate))
            {
                Interlocked.Increment(ref factoryGateViolations);
                throw new InvalidOperationException(
                    "Participant factory ran under the core gate.");
            }
            var wrapped = new GateCheckingParticipant(participant,
                () => service.LifecycleGate);
            participants.Add(wrapped);
            return wrapped;
        }

        service = new Switch2RuntimeRegistrationService(table, 1_000,
            owner => Wrap(
                new Switch2ProUsbRuntimeRegistrationParticipant(owner)),
            owner => Wrap(
                new Switch2BluetoothRuntimeRegistrationParticipant(owner)),
            owner => Wrap(
                new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner)));
        Assert.AreSame(table, service.Table);
        Assert.IsTrue(service.TryOpen(ServiceGeneration, out var openFailure),
            openFailure.Kind.ToString());

        int mappingGateViolations = 0;
        int mappingCalls = 0;
        int[] terminalBySlot = new int[3];
        Switch2RuntimeMappingCallback mapping = (slot, _, report) =>
        {
            if (Monitor.IsEntered(service.LifecycleGate))
            {
                Interlocked.Increment(ref mappingGateViolations);
                throw new InvalidOperationException(
                    "Mapping ran under the core gate.");
            }
            if (report.Kind != Switch2RuntimeReportKind.TerminalNeutral)
            {
                throw new InvalidOperationException(
                    "The deterministic test pumps publish no regular input.");
            }
            Interlocked.Increment(ref terminalBySlot[slot]);
            Interlocked.Increment(ref mappingCalls);
        };

        using Barrier start = new(3);
        bool usbAttached = false;
        bool bluetoothAttached = false;
        bool joinedAttached = false;
        InputControllerSlotToken usbToken = default;
        InputControllerSlotToken bluetoothToken = default;
        InputControllerSlotToken joinedToken = default;
        Switch2RuntimeRegistrationTransactionFailure usbFailure = default;
        Switch2RuntimeRegistrationTransactionFailure bluetoothFailure =
            default;
        Switch2RuntimeRegistrationTransactionFailure joinedFailure = default;
        Task usb = Task.Run(() =>
        {
            start.SignalAndWait();
            usbAttached = service.TryAttach(usbOwner, mapping,
                TimeoutMilliseconds, out usbToken, out usbFailure);
        });
        Task bluetooth = Task.Run(() =>
        {
            start.SignalAndWait();
            bluetoothAttached = service.TryAttach(bluetoothOwner, mapping,
                TimeoutMilliseconds, out bluetoothToken,
                out bluetoothFailure);
        });
        Task joined = Task.Run(() =>
        {
            start.SignalAndWait();
            joinedAttached = service.TryAttach(joinedOwner, mapping,
                TimeoutMilliseconds, out joinedToken, out joinedFailure);
        });
        Assert.IsTrue(Task.WaitAll(new[] { usb, bluetooth, joined },
            TimeSpan.FromSeconds(5)));
        Assert.IsTrue(usbAttached, usbFailure.Kind.ToString());
        Assert.IsTrue(bluetoothAttached, bluetoothFailure.Kind.ToString());
        Assert.IsTrue(joinedAttached, joinedFailure.Kind.ToString());
        Assert.AreEqual(3, participants.Count);
        Assert.AreEqual(0, factoryGateViolations);

        CollectionAssert.AreEquivalent(new[]
        {
            usbToken.Slot,
            bluetoothToken.Slot,
            joinedToken.Slot,
        }, new[] { 0, 1, 2 });
        InputControllerSlotSnapshot[] active = table.GetSnapshot();
        Assert.AreEqual(3, active.Count(snapshot => snapshot.State ==
            InputControllerSlotState.Attached));
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Active,
            usbOwner.State);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Active,
            bluetoothOwner.State);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Active,
            joinedOwner.State);

        FieldInfo[] fields = typeof(Switch2RuntimeRegistrationService).
            GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.AreEqual(1, fields.Count(field => field.FieldType ==
            typeof(Switch2RuntimeRegistrationTransactionCore)),
            "The mixed service must own exactly one transaction core.");
        Assert.AreEqual(0, fields.Count(field => field.FieldType ==
            typeof(InputControllerRegistrationTable)),
            "The table is owned only by the sole transaction core.");
        Assert.AreEqual(0, fields.Count(field => field.FieldType ==
            typeof(Switch2RuntimeMappingCallback)),
            "The service must not retain a per-transport mapping lifecycle.");

        var removedTokens = new ConcurrentBag<InputControllerSlotToken>();
        service.RuntimeRemoved += removedTokens.Add;
        Assert.IsTrue(service.TryClose(ServiceGeneration,
            TimeoutMilliseconds, out var closeFailure),
            $"{closeFailure.Kind}: {closeFailure.TableFailure}/" +
            $"{closeFailure.ParticipantResult.FailureKind}");
        Assert.IsTrue(service.TryClose(ServiceGeneration,
            TimeoutMilliseconds, out closeFailure),
            closeFailure.Kind.ToString());
        Assert.AreEqual(3, mappingCalls);
        CollectionAssert.AreEquivalent(new[] { usbToken, bluetoothToken, joinedToken },
            removedTokens.ToArray(), "USB, BLE and joined owners must each notify their exact terminal removal.");
        Assert.AreEqual(1, terminalBySlot[usbToken.Slot]);
        Assert.AreEqual(1, terminalBySlot[bluetoothToken.Slot]);
        Assert.AreEqual(1, terminalBySlot[joinedToken.Slot]);
        Assert.AreEqual(0, mappingGateViolations);
        Assert.AreEqual(0, participants.Sum(value =>
            value.GateViolationCount));
        Assert.IsTrue(participants.All(value => value.CallCount > 0));

        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Removed,
            usbOwner.State);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Removed,
            bluetoothOwner.State);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Removed,
            joinedOwner.State);
        Assert.AreEqual(1, usbLease.DisposeCount);
        Assert.AreEqual(1, bluetoothLease.UnsubscribeCount);
        Assert.AreEqual(1, bluetoothLease.ReleaseWaitCount);
        Assert.AreEqual(1, leftLease.UnsubscribeCount);
        Assert.AreEqual(1, leftLease.ReleaseWaitCount);
        Assert.AreEqual(1, rightLease.UnsubscribeCount);
        Assert.AreEqual(1, rightLease.ReleaseWaitCount);
        Assert.IsTrue(table.GetSnapshot().All(snapshot => snapshot.State ==
            InputControllerSlotState.Removed));
    }

    [TestMethod]
    public void MixedOwnersCanAttachToThreeExactExternalSlots()
    {
        CreateUsbOwner(12_111, 12_211, out var usbOwner,
            out _);
        CreateBluetoothOwner(12_112, 12_212, 12_312,
            out var bluetoothOwner, out _);
        CreateJoinedOwner(12_113, 12_313, out var joinedOwner,
            out _, out _);
        var table = new InputControllerRegistrationTable(3);
        var service = new Switch2RuntimeRegistrationService(table);
        Assert.IsTrue(service.TryOpen(ServiceGeneration + 1,
            out var openFailure), openFailure.Kind.ToString());
        Switch2RuntimeMappingCallback mapping = static (_, _, _) => { };

        Assert.IsTrue(service.TryAttachExactSlot(2, usbOwner, mapping,
            TimeoutMilliseconds, out InputControllerSlotToken usbToken,
            out var usbFailure), usbFailure.Kind.ToString());
        Assert.IsTrue(service.TryAttachExactSlot(0, bluetoothOwner, mapping,
            TimeoutMilliseconds, out InputControllerSlotToken bluetoothToken,
            out var bluetoothFailure), bluetoothFailure.Kind.ToString());
        Assert.IsTrue(service.TryAttachExactSlot(1, joinedOwner, mapping,
            TimeoutMilliseconds, out InputControllerSlotToken joinedToken,
            out var joinedFailure), joinedFailure.Kind.ToString());
        Assert.AreEqual(2, usbToken.Slot);
        Assert.AreEqual(0, bluetoothToken.Slot);
        Assert.AreEqual(1, joinedToken.Slot);

        Assert.IsTrue(service.TryClose(ServiceGeneration + 1,
            TimeoutMilliseconds, out var closeFailure),
            closeFailure.Kind.ToString());
        Assert.IsTrue(table.GetSnapshot().All(snapshot => snapshot.State ==
            InputControllerSlotState.Removed));
    }

    [TestMethod]
    public void MixedOwnersUseOneExactControlServiceHostAndRetireCleanly()
    {
        CreateUsbOwner(12_114, 12_214, out var usbOwner, out _);
        CreateBluetoothOwner(12_115, 12_215, 12_315,
            out var bluetoothOwner, out _);
        CreateJoinedOwner(12_116, 12_316, out var joinedOwner,
            out _, out _);
        var table = new InputControllerRegistrationTable(3);
        var service = new Switch2RuntimeRegistrationService(table);
        var host = new RecordingControlServiceSlotHost();
        Assert.IsTrue(service.TryOpen(ServiceGeneration + 3,
            out var openFailure), openFailure.Kind.ToString());

        Assert.IsTrue(service.TryAttachExactSlot(2, usbOwner, host,
            TimeoutMilliseconds, out var usbToken, out var usbFailure),
            usbFailure.Kind.ToString());
        Assert.IsTrue(service.TryAttachExactSlot(0, bluetoothOwner, host,
            TimeoutMilliseconds, out var bluetoothToken,
            out var bluetoothFailure), bluetoothFailure.Kind.ToString());
        Assert.IsTrue(service.TryAttachExactSlot(1, joinedOwner, host,
            TimeoutMilliseconds, out var joinedToken, out var joinedFailure),
            joinedFailure.Kind.ToString());
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2 },
            new[] { usbToken.Slot, bluetoothToken.Slot, joinedToken.Slot });
        Assert.AreEqual(3, host.PrepareCount);
        Assert.AreEqual(3, host.ActiveCount);
        Assert.AreEqual(0, host.RegularDispatchCount);

        Assert.IsTrue(service.TryClose(ServiceGeneration + 3,
            TimeoutMilliseconds, out var closeFailure),
            closeFailure.Kind.ToString());
        Assert.AreEqual(3, host.TerminalDispatchCount);
        Assert.AreEqual(3, host.RemoveCount);
        Assert.AreEqual(0, host.AbortCount);
        Assert.AreEqual(0, host.ActiveCount);
        Assert.IsTrue(table.GetSnapshot().All(snapshot => snapshot.State ==
            InputControllerSlotState.Removed));
    }

    [TestMethod]
    public void InvalidExactExternalSlotDoesNotConstructParticipant()
    {
        CreateUsbOwner(12_121, 12_221, out var usbOwner, out _);
        var table = new InputControllerRegistrationTable(2);
        int factoryCalls = 0;
        var service = new Switch2RuntimeRegistrationService(table, 1_000,
            owner =>
            {
                factoryCalls++;
                return new Switch2ProUsbRuntimeRegistrationParticipant(owner);
            },
            static owner =>
                new Switch2BluetoothRuntimeRegistrationParticipant(owner),
            static owner =>
                new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner));
        Assert.IsTrue(service.TryOpen(ServiceGeneration + 2, out _));

        Assert.IsFalse(service.TryAttachExactSlot(2, usbOwner,
            static (_, _, _) => { }, TimeoutMilliseconds,
            out InputControllerSlotToken token, out var failure));
        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.InvalidArgument,
            failure.Kind);
        Assert.AreEqual(0, factoryCalls);
        Assert.IsFalse(token.IsValid);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Created,
            usbOwner.State);
        Assert.IsTrue(table.GetSnapshot().All(snapshot =>
            snapshot.State == InputControllerSlotState.Empty));
    }

    [TestMethod]
    public void InvalidHostedExactSlotDoesNotConstructOrPrepareParticipant()
    {
        CreateUsbOwner(12_122, 12_222, out var usbOwner, out _);
        var table = new InputControllerRegistrationTable(2);
        int factoryCalls = 0;
        var service = new Switch2RuntimeRegistrationService(table, 1_000,
            owner =>
            {
                factoryCalls++;
                return new Switch2ProUsbRuntimeRegistrationParticipant(owner);
            },
            static owner =>
                new Switch2BluetoothRuntimeRegistrationParticipant(owner),
            static owner =>
                new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner));
        var host = new RecordingControlServiceSlotHost();
        Assert.IsTrue(service.TryOpen(ServiceGeneration + 4, out _));

        Assert.IsFalse(service.TryAttachExactSlot(2, usbOwner, host,
            TimeoutMilliseconds, out var token, out var failure));
        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.InvalidArgument,
            failure.Kind);
        Assert.IsFalse(token.IsValid);
        Assert.AreEqual(0, factoryCalls);
        Assert.AreEqual(0, host.PrepareCount);
        Assert.AreEqual(0, host.ActiveCount);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Created,
            usbOwner.State);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void CrossOwnerNullOrThrowingFactoryQuarantinesBoundSlot(int mode)
    {
        ulong offset = (ulong)mode * 20;
        CreateUsbOwner(13_101 + offset, 13_201 + offset,
            out var usbOwner, out _);
        CreateBluetoothOwner(13_102 + offset, 13_202 + offset,
            13_302 + offset, out var foreignOwner, out _);

        var table = new InputControllerRegistrationTable(1);
        Switch2RuntimeRegistrationService service = null;
        int gateViolations = 0;
        ISwitch2RuntimeRegistrationParticipant UsbFactory(
            Switch2ProUsbRuntimeOwner _)
        {
            if (Monitor.IsEntered(service.LifecycleGate))
            {
                Interlocked.Increment(ref gateViolations);
            }
            return mode switch
            {
                0 => new Switch2BluetoothRuntimeRegistrationParticipant(
                    foreignOwner),
                1 => null,
                _ => throw new InvalidOperationException(
                    "synthetic participant factory failure"),
            };
        }
        service = new Switch2RuntimeRegistrationService(table, 1_000,
            UsbFactory,
            static owner =>
                new Switch2BluetoothRuntimeRegistrationParticipant(owner),
            static owner =>
                new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner));
        Assert.IsTrue(service.TryOpen(ServiceGeneration + offset, out _));

        Assert.IsFalse(service.TryAttach((Switch2ProUsbRuntimeOwner)null,
            static (_, _, _) => { }, 1_000, out var nullToken,
            out var nullFailure));
        Assert.IsFalse(nullToken.IsValid);
        Assert.AreEqual(Switch2RuntimeRegistrationTransactionFailureKind.
            InvalidArgument, nullFailure.Kind);
        Assert.IsFalse(service.TryAttach(usbOwner, null, 1_000,
            out var nullMappingToken, out var nullMappingFailure));
        Assert.IsFalse(nullMappingToken.IsValid);
        Assert.AreEqual(Switch2RuntimeRegistrationTransactionFailureKind.
            InvalidArgument, nullMappingFailure.Kind);

        Assert.IsFalse(service.TryAttach(usbOwner,
            static (_, _, _) => { }, 1_000, out var token,
            out var failure));
        Assert.IsTrue(token.IsValid,
            "The quarantined setup must retain its exact bound token.");
        Assert.AreEqual(Switch2RuntimeRegistrationTransactionFailureKind.
            QuarantineRequired, failure.Kind);
        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.AreEqual(0, gateViolations,
            "Even a failing factory must run outside the sole core gate.");
        InputControllerSlotSnapshot snapshot = table.GetSnapshot()[token.Slot];
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            snapshot.State);
        Assert.AreEqual(InputControllerSlotQuarantineReason.
            ExternalLifecycleFailure, snapshot.QuarantineReason);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Created,
            usbOwner.State);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Created,
            foreignOwner.State);

        // Test-only exact owner cleanup. The core intentionally keeps the
        // table slot quarantined because no factory result proved ownership.
        DirectAbortUsb(usbOwner, token);
        DirectAbortBluetooth(foreignOwner, ServiceGeneration + 100 + offset);
    }

    [TestMethod]
    public void CrossServiceTokenCannotRemoveMixedServiceOwner()
    {
        CreateBluetoothOwner(14_101, 14_201, 14_301,
            out var firstOwner, out _);
        CreateBluetoothOwner(14_102, 14_202, 14_302,
            out var secondOwner, out _);
        var first = new Switch2RuntimeRegistrationService(
            new InputControllerRegistrationTable(1));
        var second = new Switch2RuntimeRegistrationService(
            new InputControllerRegistrationTable(1));
        Assert.IsTrue(first.TryOpen(14_401, out _));
        Assert.IsTrue(second.TryOpen(14_402, out _));
        Assert.IsTrue(first.TryAttach(firstOwner, static (_, _, _) => { },
            1_000, out var firstToken, out var firstFailure),
            firstFailure.Kind.ToString());
        Assert.IsTrue(second.TryAttach(secondOwner, static (_, _, _) => { },
            1_000, out var secondToken, out var secondFailure),
            secondFailure.Kind.ToString());

        Assert.IsFalse(first.TryRemove(secondToken, 1_000,
            out var crossFailure));
        Assert.AreEqual(Switch2RuntimeRegistrationTransactionFailureKind.
            StaleToken, crossFailure.Kind);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Active,
            firstOwner.State);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Active,
            secondOwner.State);
        Assert.IsTrue(first.TryClose(14_401, 1_000, out firstFailure),
            firstFailure.Kind.ToString());
        Assert.IsTrue(second.TryClose(14_402, 1_000, out secondFailure),
            secondFailure.Kind.ToString());
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Removed,
            firstOwner.State);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Removed,
            secondOwner.State);
        Assert.IsTrue(firstToken.IsValid);
    }

    [TestMethod]
    public void UserDisconnectRetiresStandaloneAndJoinedBluetoothOwners()
    {
        CreateBluetoothOwner(14_111, 14_211, 14_311,
            out var standalone, out var standaloneLease);
        CreateJoinedOwner(14_112, 14_312, out var joined,
            out var leftLease, out var rightLease);
        var table = new InputControllerRegistrationTable(2);
        var service = new Switch2RuntimeRegistrationService(table);
        const ulong generation = 14_411;
        Assert.IsTrue(service.TryOpen(generation, out var openFailure),
            openFailure.Kind.ToString());
        int standaloneTerminal = 0;
        int joinedTerminal = 0;
        Assert.IsFalse(standalone.RuntimeDevice.DisconnectBT(),
            "The owner is bound but not active before slot commit.");
        Assert.IsFalse(joined.RuntimeDevice.DisconnectBT());
        Assert.IsTrue(service.TryAttach(standalone, (slot, _, report) =>
        {
            if (report.Kind == Switch2RuntimeReportKind.TerminalNeutral)
            {
                Interlocked.Increment(ref standaloneTerminal);
            }
        }, TimeoutMilliseconds, out InputControllerSlotToken standaloneToken,
            out var standaloneFailure), standaloneFailure.Kind.ToString());
        Assert.IsTrue(service.TryAttach(joined, (slot, _, report) =>
        {
            if (report.Kind == Switch2RuntimeReportKind.TerminalNeutral)
            {
                Interlocked.Increment(ref joinedTerminal);
            }
        }, TimeoutMilliseconds, out InputControllerSlotToken joinedToken,
            out var joinedFailure), joinedFailure.Kind.ToString());

        Assert.IsTrue(standalone.RuntimeDevice.DisconnectBT(callRemoval: true));
        Assert.IsTrue(joined.RuntimeDevice.DisconnectWireless());
        Assert.IsTrue(standalone.RuntimeDevice.IsDisconnecting);
        Assert.IsTrue(joined.RuntimeDevice.IsDisconnecting);
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            InputControllerSlotSnapshot[] snapshots = table.GetSnapshot();
            return snapshots[standaloneToken.Slot].State ==
                    InputControllerSlotState.Removed &&
                snapshots[joinedToken.Slot].State ==
                    InputControllerSlotState.Removed;
        }, TimeSpan.FromSeconds(3)),
            "Manual disconnect did not complete the typed retirement transaction.");

        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Removed,
            standalone.State);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Removed, joined.State);
        Assert.AreEqual(1, Volatile.Read(ref standaloneTerminal));
        Assert.AreEqual(1, Volatile.Read(ref joinedTerminal));
        Assert.AreEqual(1, standaloneLease.UnsubscribeCount);
        Assert.AreEqual(1, standaloneLease.ReleaseWaitCount);
        Assert.AreEqual(1, leftLease.UnsubscribeCount);
        Assert.AreEqual(1, leftLease.ReleaseWaitCount);
        Assert.AreEqual(1, rightLease.UnsubscribeCount);
        Assert.AreEqual(1, rightLease.ReleaseWaitCount);
        Assert.IsTrue(service.TryClose(generation, TimeoutMilliseconds,
            out var closeFailure), closeFailure.Kind.ToString());
    }

    private static void DirectAbortUsb(Switch2ProUsbRuntimeOwner owner,
        in InputControllerSlotToken token)
    {
        Assert.IsTrue(owner.TryAdoptBoundSlot(token, out var adoption,
            out var adoptionFailure), adoptionFailure.ToString());
        Assert.IsTrue(owner.TryAbortUnpublished(adoption, 1_000,
            out var abortFailure), abortFailure.ToString());
    }

    private static void DirectAbortBluetooth(
        Switch2BluetoothRuntimeOwner owner, ulong tableGeneration)
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(tableGeneration, out _));
        Assert.IsTrue(table.TryReserveAndBind(owner.Registration,
            out var token, out var rollback, out _));
        Assert.IsTrue(owner.TryAdoptBoundSlot(token, out var adoption,
            out var adoptionFailure), adoptionFailure.ToString());
        Assert.IsTrue(owner.TryAbortUnpublished(adoption, 1_000,
            out var abortFailure), abortFailure.ToString());
        Assert.IsTrue(table.TryRollback(rollback, out _));
    }

    private static void CreateUsbOwner(ulong deviceGeneration,
        ulong transportGeneration, out Switch2ProUsbRuntimeOwner owner,
        out UsbLease lease)
    {
        lease = new UsbLease();
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, deviceGeneration,
            out var calibration));
        Assert.IsTrue(Switch2ProUsbRuntimeOwner.TryCreateCore(
            new UsbDiscovery(CreateUsbObservation()),
            new UsbNativeAdapter(lease), new UsbPumpFactory(),
            deviceGeneration, transportGeneration, QpcFrequency, calibration,
            200, out owner, out _, out var failure),
            failure.Kind.ToString());
    }

    private static void CreateBluetoothOwner(ulong deviceGeneration,
        ulong transportGeneration, ulong scanGeneration,
        out Switch2BluetoothRuntimeOwner owner, out BluetoothLease lease)
        => CreateBluetoothOwnerForModel(Switch2ControllerModel.ProController2,
            deviceGeneration, transportGeneration, scanGeneration, out owner, out lease);

    private static void CreateBluetoothOwnerForModel(Switch2ControllerModel model,
        ulong deviceGeneration, ulong transportGeneration, ulong scanGeneration,
        out Switch2BluetoothRuntimeOwner owner, out BluetoothLease lease)
    {
        var admission = BluetoothAdmission(model, scanGeneration);
        lease = new BluetoothLease(admission, ExactGatt(scanGeneration));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            model, deviceGeneration,
            out var calibration));
        Assert.IsTrue(Switch2BluetoothRuntimeOwner.TryCreateCore(admission,
            lease, deviceGeneration, transportGeneration, QpcFrequency,
            calibration, queueCapacity: 4, TimeoutMilliseconds,
            new BluetoothPumpFactory(1),
            Switch2RuntimeTerminalScheduler.Instance, out owner, out _,
            out var failure), failure.Kind.ToString());
    }

    private static void CreateJoinedOwner(ulong runtimeGeneration,
        ulong scanGeneration, out Switch2JoyConJoinedRuntimeOwner owner,
        out BluetoothLease leftLease, out BluetoothLease rightLease)
    {
        const ulong pairEpoch = 15_001;
        const ulong leftDeviceGeneration = 15_101;
        const ulong leftTransportGeneration = 15_201;
        const ulong rightDeviceGeneration = 15_102;
        const ulong rightTransportGeneration = 15_202;
        Switch2BluetoothConnectionAdmission leftAdmission =
            BluetoothAdmission(Switch2ControllerModel.JoyCon2Left,
                scanGeneration);
        Switch2BluetoothConnectionAdmission rightAdmission =
            BluetoothAdmission(Switch2ControllerModel.JoyCon2Right,
                scanGeneration);
        byte[] key = Enumerable.Range(1, 32).Select(value =>
            (byte)value).ToArray();
        byte[] leftIdentity = Enumerable.Range(40, 16).Select(value =>
            (byte)value).ToArray();
        byte[] rightIdentity = Enumerable.Range(90, 16).Select(value =>
            (byte)value).ToArray();
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(key, leftIdentity,
            Switch2ControllerModel.JoyCon2Left,
            Switch2AdvertisementCodec.JoyCon2LeftProductId,
            out var leftPeer));
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(key, rightIdentity,
            Switch2ControllerModel.JoyCon2Right,
            Switch2AdvertisementCodec.JoyCon2RightProductId,
            out var rightPeer));
        byte[] pairBytes = new byte[Switch2JoyConPairId.EncodedLength];
        BitConverter.TryWriteBytes(pairBytes, scanGeneration);
        Assert.IsTrue(Switch2JoyConPairId.TryRead(pairBytes, out var pairId));
        Assert.IsTrue(Switch2JoyConPairRecord.TryCreate(scanGeneration,
            pairId, leftPeer, rightPeer, out var record));
        Assert.IsTrue(Switch2JoyConPairConnectionAdmission.TryCreate(record,
            leftPeer, leftAdmission, rightPeer, rightAdmission,
            out var pairAdmission));
        leftLease = new BluetoothLease(leftAdmission,
            ExactGatt(scanGeneration));
        rightLease = new BluetoothLease(rightAdmission,
            ExactGatt(scanGeneration));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Left, leftDeviceGeneration,
            out var leftCalibration));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Right, rightDeviceGeneration,
            out var rightCalibration));
        Assert.IsTrue(Switch2JoyConJoinedRuntimeOwner.TryCreateCore(
            pairAdmission, leftLease, rightLease, runtimeGeneration,
            pairEpoch, leftDeviceGeneration, leftTransportGeneration,
            leftCalibration, 4, rightDeviceGeneration,
            rightTransportGeneration, rightCalibration, 4, QpcFrequency,
            new Switch2JoyConPairPolicy(1_000), TimeoutMilliseconds,
            new BluetoothPumpFactory(2),
            Switch2RuntimeTerminalScheduler.Instance, out owner, out _,
            out var failure), failure.Kind.ToString());
    }

    private static Switch2BluetoothConnectionAdmission BluetoothAdmission(
        Switch2ControllerModel model, ulong scanGeneration) => new(
        scanGeneration, model, model switch
        {
            Switch2ControllerModel.ProController2 =>
                Switch2AdvertisementCodec.ProController2ProductId,
            Switch2ControllerModel.JoyCon2Left =>
                Switch2AdvertisementCodec.JoyCon2LeftProductId,
            _ => Switch2AdvertisementCodec.JoyCon2RightProductId,
        });

    private static Switch2BluetoothGattSnapshot ExactGatt(
        ulong scanGeneration) => new(scanGeneration, 1, 1,
        Switch2InputCodec.ServiceUuid,
        Switch2InputCodec.Common05CharacteristicUuid,
        Switch2GattProperty.Read | Switch2GattProperty.Notify);

    private static Switch2ProUsbCompositeObservation CreateUsbObservation()
    {
        Assert.IsTrue(Switch2PhysicalContainerIdentity.TryCreate(
            UsbContainerGuid, out var container));
        var input = new Switch2UsbHidInterfaceObservation(container, 0, 0,
            Switch2UsbBoundDriver.HidClass, 0x0001, 0x0005, 64, 64, 0);
        var bulkOut = new Switch2UsbPipeObservation(0x02,
            Switch2UsbPipeTransferType.Bulk, 64, 0);
        var bulkIn = new Switch2UsbPipeObservation(0x82,
            Switch2UsbPipeTransferType.Bulk, 64, 0);
        var command = new Switch2UsbCommandInterfaceObservation(container,
            1, 0, Switch2UsbBoundDriver.WinUsb, 2, bulkOut, bulkIn);
        return new Switch2ProUsbCompositeObservation(0x057E, 0x2069,
            0x0201, container, 1, 1, input, command);
    }

    private sealed class GateCheckingParticipant :
        ISwitch2RuntimeRegistrationParticipant
    {
        private readonly ISwitch2RuntimeRegistrationParticipant inner;
        private readonly Func<object> gate;
        private int callCount;
        private int gateViolationCount;

        internal GateCheckingParticipant(
            ISwitch2RuntimeRegistrationParticipant inner,
            Func<object> gate)
        {
            this.inner = inner;
            this.gate = gate;
        }

        internal int CallCount => Volatile.Read(ref callCount);

        internal int GateViolationCount => Volatile.Read(
            ref gateViolationCount);

        public InputControllerRegistration Registration
        {
            get
            {
                Check();
                return inner.Registration;
            }
        }

        public Switch2RuntimeRegistrationParticipantResult TryAdoptBoundSlot(
            in InputControllerSlotToken token)
        {
            Check();
            var result = inner.TryAdoptBoundSlot(token);
            Check();
            return result;
        }

        public Switch2RuntimeRegistrationParticipantResult TrySubscribe(
            in Switch2RuntimeRegistrationCallbacks callbacks)
        {
            Check();
            var result = inner.TrySubscribe(callbacks);
            Check();
            return result;
        }

        public Switch2RuntimeRegistrationParticipantResult
            TryPrepareActivation(int timeoutMilliseconds)
        {
            Check();
            var result = inner.TryPrepareActivation(timeoutMilliseconds);
            Check();
            return result;
        }

        public Switch2RuntimeRegistrationParticipantResult TryCommitPrepared(
            in InputControllerActivationCommitCredential activationCommit)
        {
            Check();
            var result = inner.TryCommitPrepared(activationCommit);
            Check();
            return result;
        }

        public Switch2RuntimeRegistrationParticipantResult TryAbortPrepared(
            int timeoutMilliseconds)
        {
            Check();
            var result = inner.TryAbortPrepared(timeoutMilliseconds);
            Check();
            return result;
        }

        public Switch2RuntimeRegistrationParticipantResult
            TryAbortUnpublished(int timeoutMilliseconds)
        {
            Check();
            var result = inner.TryAbortUnpublished(timeoutMilliseconds);
            Check();
            return result;
        }

        public Switch2RuntimeRegistrationParticipantResult TryArmRetirement(
            in InputControllerRetirementClaim claim)
        {
            Check();
            var result = inner.TryArmRetirement(claim);
            Check();
            return result;
        }

        public Switch2RuntimeRegistrationParticipantResult
            TryWaitForPublicationAvailability(int timeoutMilliseconds)
        {
            Check();
            var result = inner.TryWaitForPublicationAvailability(
                timeoutMilliseconds);
            Check();
            return result;
        }

        public Switch2RuntimeRegistrationParticipantResult TryStopAndQuiesce(
            int timeoutMilliseconds)
        {
            Check();
            var result = inner.TryStopAndQuiesce(timeoutMilliseconds);
            Check();
            return result;
        }

        public Switch2RuntimeRegistrationParticipantResult TryUnsubscribe()
        {
            Check();
            var result = inner.TryUnsubscribe();
            Check();
            return result;
        }

        public Switch2RuntimeRegistrationParticipantResult TryRemove()
        {
            Check();
            var result = inner.TryRemove();
            Check();
            return result;
        }

        private void Check()
        {
            Interlocked.Increment(ref callCount);
            if (!Monitor.IsEntered(gate()))
            {
                return;
            }
            Interlocked.Increment(ref gateViolationCount);
            throw new InvalidOperationException(
                "External participant operation ran under the core gate.");
        }
    }

    private sealed class RecordingControlServiceSlotHost :
        ISwitch2ControlServiceSlotHost
    {
        private readonly object gate = new();
        private readonly Dictionary<int, Switch2ControlServiceSlotLease>
            active = new();

        internal int PrepareCount { get; private set; }
        internal int RegularDispatchCount { get; private set; }
        internal int TerminalDispatchCount { get; private set; }
        internal int AbortCount { get; private set; }
        internal int RemoveCount { get; private set; }
        internal int ActiveCount { get { lock (gate) { return active.Count; } } }

        public Switch2ControlServiceSlotHostResult TryPrepare(
            in Switch2ControlServiceSlotLease lease)
        {
            lock (gate)
            {
                if (!lease.IsValid || active.ContainsKey(lease.Slot))
                {
                    return Switch2ControlServiceSlotHostResult.Reject(
                        Switch2ControlServiceSlotHostOperation.Prepare,
                        Switch2ControlServiceSlotHostFailureKind.SlotOccupied);
                }
                active.Add(lease.Slot, lease);
                PrepareCount++;
                return Switch2ControlServiceSlotHostResult.Success(
                    Switch2ControlServiceSlotHostOperation.Prepare);
            }
        }

        public Switch2ControlServiceSlotHostResult TryDispatch(
            in Switch2ControlServiceSlotLease lease, DS4Device sender,
            Switch2RuntimeReportEventArgs report)
        {
            lock (gate)
            {
                if (!TryMatchNoLock(lease, sender) || report == null ||
                    report.RuntimeGeneration != lease.RuntimeGeneration)
                {
                    return Switch2ControlServiceSlotHostResult.Reject(
                        Switch2ControlServiceSlotHostOperation.DispatchRegular,
                        Switch2ControlServiceSlotHostFailureKind.
                            CallbackRejected);
                }
                if (report.Kind == Switch2RuntimeReportKind.Regular)
                {
                    RegularDispatchCount++;
                    return Switch2ControlServiceSlotHostResult.Success(
                        Switch2ControlServiceSlotHostOperation.
                            DispatchRegular);
                }
                if (report.Kind ==
                    Switch2RuntimeReportKind.TerminalNeutral)
                {
                    TerminalDispatchCount++;
                    return Switch2ControlServiceSlotHostResult.Success(
                        Switch2ControlServiceSlotHostOperation.
                            DispatchTerminalNeutral);
                }
                return Switch2ControlServiceSlotHostResult.Reject(
                    Switch2ControlServiceSlotHostOperation.DispatchRegular,
                    Switch2ControlServiceSlotHostFailureKind.CallbackRejected);
            }
        }

        public Switch2ControlServiceSlotHostResult TryAbort(
            in Switch2ControlServiceSlotLease lease)
        {
            lock (gate)
            {
                if (!TryMatchNoLock(lease, lease.Device))
                {
                    return Switch2ControlServiceSlotHostResult.Reject(
                        Switch2ControlServiceSlotHostOperation.Abort,
                        Switch2ControlServiceSlotHostFailureKind.SlotChanged);
                }
                active.Remove(lease.Slot);
                AbortCount++;
                return Switch2ControlServiceSlotHostResult.Success(
                    Switch2ControlServiceSlotHostOperation.Abort);
            }
        }

        public Switch2ControlServiceSlotHostResult TryRemove(
            in Switch2ControlServiceSlotLease lease)
        {
            lock (gate)
            {
                if (!TryMatchNoLock(lease, lease.Device))
                {
                    return Switch2ControlServiceSlotHostResult.Reject(
                        Switch2ControlServiceSlotHostOperation.Remove,
                        Switch2ControlServiceSlotHostFailureKind.SlotChanged);
                }
                active.Remove(lease.Slot);
                RemoveCount++;
                return Switch2ControlServiceSlotHostResult.Success(
                    Switch2ControlServiceSlotHostOperation.Remove);
            }
        }

        private bool TryMatchNoLock(
            in Switch2ControlServiceSlotLease lease, DS4Device sender) =>
            lease.IsValid && active.TryGetValue(lease.Slot,
                out Switch2ControlServiceSlotLease installed) &&
            installed.Token.Equals(lease.Token) &&
            ReferenceEquals(sender, installed.Device);
    }

    private sealed class UsbDiscovery : ISwitch2ProUsbOsDiscoveryAdapter
    {
        private readonly Switch2ProUsbCompositeObservation observation;

        internal UsbDiscovery(in Switch2ProUsbCompositeObservation observation)
            => this.observation = observation;

        public bool TryObserveComposite(
            out Switch2ProUsbCompositeObservation observed)
        {
            observed = observation;
            return true;
        }
    }

    private sealed class UsbNativeAdapter : ISwitch2ProUsbNativeAdapter
    {
        private readonly UsbLease lease;

        internal UsbNativeAdapter(UsbLease lease) => this.lease = lease;

        public bool TryOpenReadOnlyComposite(
            in Switch2PhysicalInputRegistration registration,
            out ISwitch2ProUsbReadOnlyCompositeLease opened)
        {
            lease.AdmittedRegistration = registration;
            opened = lease;
            return true;
        }
    }

    private sealed class UsbLease : ISwitch2ProUsbReadOnlyCompositeLease
    {
        internal Switch2PhysicalInputRegistration AdmittedRegistration
        { get; set; }

        internal int DisposeCount { get; private set; }

        public Switch2PhysicalInputRegistration Registration =>
            AdmittedRegistration;

        public bool TryBeginInputRead(byte[] destination, int offset,
            int count, in Switch2ProUsbReadClaim claim,
            ISwitch2ProUsbReadCompletionTarget completionTarget) =>
            throw new InvalidOperationException(
                "The deterministic service pump performs no reads.");

        public bool TryCancelInputRead(
            in Switch2ProUsbReadClaim claim) => true;

        public bool TryRetireCompletedInputRead(
            in Switch2ProUsbReadClaim claim,
            int timeoutMilliseconds) => true;

        public bool TryWaitForInputQuiescence(int timeoutMilliseconds) =>
            timeoutMilliseconds >= 0;

        public void DisposeQuiesced() => DisposeCount++;
    }

    private sealed class UsbPumpFactory : ISwitch2ProUsbRuntimePumpFactory
    {
        public bool TryCreate(Switch2ProUsbInputTransportOwner transportOwner,
            int readRetirementTimeoutMilliseconds,
            out ISwitch2ProUsbRuntimeReadPump pump,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            object fence = new();
            if (!transportOwner.TryAttachContinuousPump(fence))
            {
                pump = null;
                failure = Switch2ProUsbInputReadPumpFailure.OwnerRejected;
                return false;
            }
            pump = new UsbPump(transportOwner, fence);
            failure = default;
            return true;
        }
    }

    private sealed class UsbPump : ISwitch2ProUsbRuntimeReadPump
    {
        private readonly Switch2ProUsbInputTransportOwner transportOwner;
        private readonly object ownerFence;

        internal UsbPump(Switch2ProUsbInputTransportOwner transportOwner,
            object ownerFence)
        {
            this.transportOwner = transportOwner;
            this.ownerFence = ownerFence;
        }

        public Switch2ProUsbInputReadPumpState State { get; private set; } =
            Switch2ProUsbInputReadPumpState.Created;
        public Switch2ProUsbInputReadPumpFailure TerminalFailure => default;
        public Switch2ProUsbDisposeFailure LastDisposeFailure { get;
            private set; }
        public long StartedReadCount => 0;
        public long RetiredReadCount => 0;
        public bool TrySetLifecycleAttentionHandler(
            Action<Switch2ProUsbInputReadPumpFailure> handler) =>
            handler != null;
        public bool TryPrepareStart(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            State = Switch2ProUsbInputReadPumpState.Prepared;
            failure = default;
            return true;
        }
        public bool TryCommitPrepared(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            State = Switch2ProUsbInputReadPumpState.Running;
            failure = default;
            return true;
        }
        public bool TryAbortPrepared(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure) =>
            TryStopAndDispose(timeoutMilliseconds, out failure);
        public bool TryStart(out Switch2ProUsbInputReadPumpFailure failure)
        {
            State = Switch2ProUsbInputReadPumpState.Running;
            failure = default;
            return true;
        }
        public bool RequestStop()
        {
            transportOwner.RequestStop();
            State = Switch2ProUsbInputReadPumpState.StopRequested;
            return true;
        }
        public bool TryStopAndDispose(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            bool stopped = transportOwner.TryQuiesceAndDispose(ownerFence,
                Math.Min(timeoutMilliseconds,
                    Switch2ProUsbInputTransportOwner.
                        MaximumDisposeTimeoutMilliseconds),
                out var disposeFailure);
            LastDisposeFailure = disposeFailure;
            State = stopped ? Switch2ProUsbInputReadPumpState.Disposed :
                Switch2ProUsbInputReadPumpState.StopRequested;
            failure = stopped ? default :
                Switch2ProUsbInputReadPumpFailure.OwnerDisposeRejected;
            return stopped;
        }
    }

    private sealed class BluetoothLease : ISwitch2BluetoothInputLease,
        ISwitch2BluetoothInputLeaseReleaseProof
    {
        internal BluetoothLease(
            in Switch2BluetoothConnectionAdmission admission,
            in Switch2BluetoothGattSnapshot gatt)
        {
            Admission = admission;
            GattSnapshot = gatt;
        }

        public Switch2BluetoothConnectionAdmission Admission { get; }
        public Switch2BluetoothGattSnapshot GattSnapshot { get; }
        internal int UnsubscribeCount { get; private set; }
        internal int ReleaseWaitCount { get; private set; }

        public bool TrySubscribeCccdNotify(ulong transportGeneration,
            Switch2BluetoothInputNotification notification,
            Switch2BluetoothInputDisconnected disconnected) => true;

        public bool TryUnsubscribeCccdNone(ulong transportGeneration)
        {
            UnsubscribeCount++;
            return true;
        }

        public Switch2BluetoothInputLeaseReleaseResult WaitForRelease(
            ulong transportGeneration, int timeoutMilliseconds)
        {
            ReleaseWaitCount++;
            return timeoutMilliseconds >= 0 ?
                Switch2BluetoothInputLeaseReleaseResult.Released :
                Switch2BluetoothInputLeaseReleaseResult.Rejected;
        }
    }

    private sealed class BluetoothPumpFactory :
        ISwitch2BluetoothRuntimeDrainPumpFactory
    {
        private int remaining;

        internal BluetoothPumpFactory(int count) => remaining = count;

        public bool TryCreate(Switch2BluetoothInputOwner inputOwner,
            out ISwitch2BluetoothRuntimeDrainPump pump,
            out Switch2BluetoothInputDrainPumpFailure failure)
        {
            if (Interlocked.Decrement(ref remaining) < 0)
            {
                pump = null;
                failure = Switch2BluetoothInputDrainPumpFailure.OwnerRejected;
                return false;
            }
            pump = new BluetoothPump();
            failure = default;
            return true;
        }
    }

    private sealed class BluetoothPump :
        ISwitch2BluetoothRuntimeDrainPump
    {
        public Switch2BluetoothInputDrainPumpState State { get; private set; }
            = Switch2BluetoothInputDrainPumpState.Created;
        public Switch2BluetoothInputDrainPumpFailure TerminalFailure =>
            default;
        public bool RequiresQuarantine => false;
        public bool IsCurrentWorkerThread => false;
        public long PublishedCount => 0;
        public bool TrySetLifecycleAttentionHandler(
            Action<Switch2BluetoothInputDrainPumpAttention> handler) =>
            handler != null;
        public bool TryStartParked(int timeoutMilliseconds,
            out Switch2BluetoothInputDrainPumpFailure failure)
        {
            State = Switch2BluetoothInputDrainPumpState.Parked;
            failure = default;
            return true;
        }
        public bool TryStopAndJoin(int timeoutMilliseconds,
            out Switch2BluetoothInputDrainPumpFailure failure)
        {
            State = Switch2BluetoothInputDrainPumpState.Stopped;
            failure = default;
            return true;
        }
    }
}
