using System.Buffers.Binary;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothRuntimeInputSinkTests
{
    private const ulong DeviceGeneration = 41;
    private const ulong TransportGeneration = 73;
    private const long QpcFrequency = 10_000_000;
    private const int PublicationAdmissionTimeoutMilliseconds = 200;
    private const Switch2GattProperty InputProperties =
        Switch2GattProperty.Read | Switch2GattProperty.Notify;

    [TestMethod]
    public void ProCanonicalFrameUsesExistingProfileRuntimeAndTerminalNeutral()
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(
            Switch2ControllerModel.ProController2);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration,
            Switch2Transport.BluetoothLe, out var runtime, out var created),
            created.ToString());
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreateBound(
            descriptor, runtime, PublicationAdmissionTimeoutMilliseconds,
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var terminalCredential, out var failure), failure.ToString());

        int regular = 0;
        int terminal = 0;
        runtime.Report += (_, args) =>
        {
            var report = (Switch2RuntimeReportEventArgs)args;
            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                regular++;
            }
            else
            {
                terminal++;
            }
        };
        runtime.StartUpdate();

        sink.PublishPro(Frame(descriptor, counter: 1, timestamp: 10));
        Assert.AreEqual(1, regular);
        Assert.AreEqual(1L, sink.PublishedCount);
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.None,
            sink.LastFailure);

        sink.ClearPro(DeviceGeneration, TransportGeneration,
            Switch2BluetoothInputEndReason.Disconnected);
        sink.ClearPro(DeviceGeneration, TransportGeneration,
            Switch2BluetoothInputEndReason.Disconnected);
        Assert.AreEqual(0, terminal,
            "A physical-lifetime callback only latches retirement; the service has not retired the table yet.");
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Active,
            runtime.RuntimeState);
        Assert.IsTrue(sink.TryCompleteTerminalNeutral(terminalCredential,
            1_000, out var terminalFailure), terminalFailure.ToString());
        Assert.AreEqual(1, terminal,
            "Repeated exact retirement cannot create another neutral.");
        Assert.IsTrue(sink.TerminalRequested);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Terminal,
            runtime.RuntimeState);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void StandaloneJoyConUsesExistingStatefulProfileRuntime(
        Switch2ControllerModel model)
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(model);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(
            model, DeviceGeneration, TransportGeneration, out var runtime,
            out var created), created.ToString());
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreateBound(
            descriptor, runtime, PublicationAdmissionTimeoutMilliseconds,
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var terminalCredential, out var failure), failure.ToString());

        int regular = 0;
        int terminal = 0;
        runtime.Report += (_, args) =>
        {
            var report = (Switch2RuntimeReportEventArgs)args;
            regular += report.Kind == Switch2RuntimeReportKind.Regular ? 1 : 0;
            terminal += report.Kind ==
                Switch2RuntimeReportKind.TerminalNeutral ? 1 : 0;
        };
        runtime.StartUpdate();

        sink.PublishJoyCon(Frame(descriptor, counter: 1, timestamp: 10));
        sink.PublishJoyCon(Frame(descriptor, counter: 2, timestamp: 20));
        Assert.AreEqual(2, regular);
        Assert.AreEqual(2L, sink.PublishedCount);

        Switch2StickSide side = model == Switch2ControllerModel.JoyCon2Left ?
            Switch2StickSide.Left : Switch2StickSide.Right;
        sink.LoseJoyConHalf(side, DeviceGeneration, TransportGeneration,
            Switch2BluetoothInputEndReason.Disconnected);
        Assert.AreEqual(0, terminal);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Active,
            runtime.RuntimeState);
        Assert.IsTrue(sink.TryCompleteTerminalNeutral(terminalCredential,
            1_000, out var terminalFailure), terminalFailure.ToString());
        Assert.AreEqual(1, terminal);
        Assert.IsTrue(sink.TerminalRequested);
    }

    [TestMethod]
    public void FactoryRejectsCrossModelAndAlreadyActiveRuntime()
    {
        Switch2InputSessionDescriptor pro = Descriptor(
            Switch2ControllerModel.ProController2);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(
            Switch2ControllerModel.JoyCon2Left, DeviceGeneration,
            TransportGeneration, out var left, out _));
        Assert.IsFalse(Switch2BluetoothRuntimeInputSink.TryCreate(pro, left,
            PublicationAdmissionTimeoutMilliseconds, out _,
            out var crossModel));
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.
            RuntimeDeviceMismatch, crossModel);

        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration,
            Switch2Transport.BluetoothLe, out var active, out _));
        active.StartUpdate();
        Assert.IsFalse(Switch2BluetoothRuntimeInputSink.TryCreate(pro, active,
            PublicationAdmissionTimeoutMilliseconds, out _,
            out var activeFailure));
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.
            RuntimeDeviceMismatch, activeFailure);
    }

    [TestMethod]
    public void FactoryRejectsSameDeviceGenerationWithDifferentTransportGeneration()
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(
            Switch2ControllerModel.ProController2);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration + 1,
            Switch2Transport.BluetoothLe, out var wrongLifetime, out _));

        Assert.IsFalse(Switch2BluetoothRuntimeInputSink.TryCreate(descriptor,
            wrongLifetime, PublicationAdmissionTimeoutMilliseconds, out _,
            out var failure));
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.
            RuntimeDeviceMismatch, failure);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Created,
            wrongLifetime.RuntimeState,
            "A rejected descriptor cannot terminal-neutralize another transport lifetime.");
    }

    [TestMethod]
    public void FactoryRejectsNonCommon05DescriptorBeforeItCanOwnTerminal()
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.ProController2_09CharacteristicUuid,
            InputProperties, Switch2ControllerModel.ProController2,
            out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity,
            DeviceGeneration, TransportGeneration, QpcFrequency,
            out var descriptor));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration,
            Switch2Transport.BluetoothLe, out var runtime, out _));

        Assert.IsFalse(Switch2BluetoothRuntimeInputSink.TryCreate(descriptor,
            runtime, PublicationAdmissionTimeoutMilliseconds, out _,
            out var failure));
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.DescriptorMismatch,
            failure);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Created,
            runtime.RuntimeState);
    }

    [TestMethod]
    public void StaleCanonicalAndTerminalIdentitiesFailClosed()
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(
            Switch2ControllerModel.ProController2);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration,
            Switch2Transport.BluetoothLe, out var runtime, out _));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreateBound(
            descriptor, runtime, PublicationAdmissionTimeoutMilliseconds,
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var terminalCredential, out _));
        runtime.StartUpdate();

        Switch2InputSessionDescriptor stale = Descriptor(
            Switch2ControllerModel.ProController2,
            deviceGeneration: DeviceGeneration + 1);
        Assert.ThrowsException<InvalidOperationException>(() =>
            sink.PublishPro(Frame(stale, counter: 1, timestamp: 10)));
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.
            CanonicalFrameMismatch, sink.LastFailure);
        Assert.AreEqual(0L, sink.PublishedCount);

        Assert.ThrowsException<InvalidOperationException>(() =>
            sink.ClearPro(DeviceGeneration, TransportGeneration + 1,
                Switch2BluetoothInputEndReason.Stopped));
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.
            TerminalIdentityMismatch, sink.LastFailure);
        Assert.IsFalse(sink.TerminalRequested);
    }

    [TestMethod]
    public void PostTerminalPublicationIsRejectedWithoutChangingRuntimeState()
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(
            Switch2ControllerModel.ProController2);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration,
            Switch2Transport.BluetoothLe, out var runtime, out _));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreateBound(
            descriptor, runtime, PublicationAdmissionTimeoutMilliseconds,
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var terminalCredential, out _));
        runtime.Report += (_, _) => { };
        runtime.StartUpdate();
        sink.ClearPro(DeviceGeneration, TransportGeneration,
            Switch2BluetoothInputEndReason.Stopped);

        Assert.ThrowsException<InvalidOperationException>(() =>
            sink.PublishPro(Frame(descriptor, counter: 1, timestamp: 10)));
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.LifecycleClosed,
            sink.LastFailure);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Active,
            runtime.RuntimeState);
        Assert.IsTrue(sink.TryCompleteTerminalNeutral(terminalCredential,
            1_000, out var terminalFailure), terminalFailure.ToString());
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Terminal,
            runtime.RuntimeState);
    }

    [TestMethod]
    public void TerminalRequiresActiveRuntimeExactKindAndPostCommitReason()
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(
            Switch2ControllerModel.ProController2);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration,
            Switch2Transport.BluetoothLe, out var runtime, out _));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreateBound(
            descriptor, runtime, PublicationAdmissionTimeoutMilliseconds,
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var terminalCredential, out _));

        Assert.ThrowsException<InvalidOperationException>(() => sink.ClearPro(
            DeviceGeneration, TransportGeneration,
            Switch2BluetoothInputEndReason.Disconnected));
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.LifecycleClosed,
            sink.LastFailure);
        Assert.IsFalse(sink.TerminalRequested);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Created,
            runtime.RuntimeState);

        runtime.Report += (_, _) => { };
        runtime.StartUpdate();
        Assert.ThrowsException<InvalidOperationException>(() =>
            sink.LoseJoyConHalf((Switch2StickSide)0, DeviceGeneration,
                TransportGeneration,
                Switch2BluetoothInputEndReason.Disconnected));
        Assert.ThrowsException<InvalidOperationException>(() => sink.ClearPro(
            DeviceGeneration, TransportGeneration,
            Switch2BluetoothInputEndReason.ActivationAborted));
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.
            TerminalIdentityMismatch, sink.LastFailure);
        Assert.IsFalse(sink.TerminalRequested);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Active,
            runtime.RuntimeState);

        sink.ClearPro(DeviceGeneration, TransportGeneration,
            Switch2BluetoothInputEndReason.Stopped);
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.None,
            sink.LastFailure,
            "A successful exact terminal latch must clear stale non-terminal evidence.");
        Assert.IsTrue(sink.TryCompleteTerminalNeutral(terminalCredential,
            1_000, out var terminalFailure), terminalFailure.ToString());
    }

    [TestMethod]
    public void TerminalSubscriberFailureIsVisibleAndCannotBeErasedByRetry()
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(
            Switch2ControllerModel.ProController2);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration,
            Switch2Transport.BluetoothLe, out var runtime, out _));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreateBound(
            descriptor, runtime, PublicationAdmissionTimeoutMilliseconds,
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var terminalCredential, out _));
        runtime.StartUpdate();

        sink.ClearPro(DeviceGeneration, TransportGeneration,
            Switch2BluetoothInputEndReason.Stopped);
        Assert.IsFalse(sink.TryCompleteTerminalNeutral(terminalCredential,
            1_000, out var terminalFailure));
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.
            TerminalDeliveryRejected, terminalFailure);
        Assert.IsTrue(sink.TerminalRequested);
        sink.ClearPro(DeviceGeneration, TransportGeneration,
            Switch2BluetoothInputEndReason.Stopped);
        Assert.IsFalse(sink.TryCompleteTerminalNeutral(terminalCredential,
            1_000, out terminalFailure));
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.
            TerminalDeliveryRejected, terminalFailure);
    }

    [TestMethod]
    public void TransientRuntimeAdmissionBusyWaitsWithoutRetiringProOrJoyCon()
    {
        AssertTransientProAdmissionBusy();
        AssertTransientJoyConAdmissionBusy();
    }

    [TestMethod]
    public void RuntimeAdmissionTimeoutAndSubscriberRejectionStayDistinct()
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(
            Switch2ControllerModel.ProController2);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration,
            Switch2Transport.BluetoothLe, out var runtime, out _));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreate(descriptor,
            runtime, runtimeOperationTimeoutMilliseconds: 20,
            out var sink, out _));
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        runtime.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.Regular)
            {
                entered.Set();
                release.Wait(2_000);
            }
        };
        runtime.StartUpdate();
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(
            Frame(descriptor, counter: 1, timestamp: 10), out var occupying,
            out _));
        Task<bool> occupied = Task.Run(() =>
            runtime.TryPublishPro(occupying));
        Assert.IsTrue(entered.Wait(1_000));
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() =>
                sink.PublishPro(Frame(descriptor, counter: 2,
                    timestamp: 20)));
            Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.
                RuntimePublicationAdmissionTimedOut, sink.LastFailure);
            Assert.IsTrue(sink.PublicationAdmissionWaitCount > 0);
        }
        finally
        {
            release.Set();
        }
        Assert.IsTrue(occupied.GetAwaiter().GetResult());

        Switch2InputSessionDescriptor joyDescriptor = Descriptor(
            Switch2ControllerModel.JoyCon2Left);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(
            Switch2ControllerModel.JoyCon2Left, DeviceGeneration,
            TransportGeneration, out var joyRuntime, out _));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreate(joyDescriptor,
            joyRuntime, PublicationAdmissionTimeoutMilliseconds,
            out var joySink, out _));
        DS4Device.ReportHandler<EventArgs> reject = (_, _) =>
            throw new InvalidOperationException("subscriber rejected");
        joyRuntime.Report += reject;
        joyRuntime.StartUpdate();
        Switch2CanonicalInputFrame frame = Frame(joyDescriptor,
            counter: 1, timestamp: 10);
        Assert.ThrowsException<InvalidOperationException>(() =>
            joySink.PublishJoyCon(frame));
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.
            RuntimeSubscriberRejected, joySink.LastFailure);
        Assert.AreEqual(0L, joySink.PublicationAdmissionWaitCount,
            "Subscriber rejection is terminal, not retryable backpressure.");

        joyRuntime.Report -= reject;
        joyRuntime.Report += (_, _) => { };
        joySink.PublishJoyCon(frame);
        Assert.AreEqual(1L, joySink.PublishedCount,
            "Rejected runtime delivery must not advance Joy-Con mapper state.");
    }

    [TestMethod]
    public void ConcurrentTerminalWaitsForRegularPublicationAndPreservesOrder()
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(
            Switch2ControllerModel.ProController2);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration,
            Switch2Transport.BluetoothLe, out var runtime, out _));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreateBound(
            descriptor, runtime, PublicationAdmissionTimeoutMilliseconds,
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var terminalCredential, out _));
        var regularEntered = new ManualResetEventSlim();
        var releaseRegular = new ManualResetEventSlim();
        var order = new List<Switch2RuntimeReportKind>();
        runtime.Report += (_, args) =>
        {
            var report = (Switch2RuntimeReportEventArgs)args;
            lock (order)
            {
                order.Add(report.Kind);
            }
            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                regularEntered.Set();
                releaseRegular.Wait(2_000);
            }
        };
        runtime.StartUpdate();

        Task publication = Task.Run(() => sink.PublishPro(
            Frame(descriptor, counter: 1, timestamp: 10)));
        Assert.IsTrue(regularEntered.Wait(1_000));
        Task terminal = Task.Run(() =>
        {
            sink.ClearPro(DeviceGeneration, TransportGeneration,
                Switch2BluetoothInputEndReason.Disconnected);
            Assert.IsTrue(sink.TryCompleteTerminalNeutral(terminalCredential,
                1_000, out var failure), failure.ToString());
        });
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() => sink.TerminalRequested,
                1_000));
        }
        finally
        {
            releaseRegular.Set();
        }

        publication.GetAwaiter().GetResult();
        terminal.GetAwaiter().GetResult();
        lock (order)
        {
            CollectionAssert.AreEqual(new[]
            {
                Switch2RuntimeReportKind.Regular,
                Switch2RuntimeReportKind.TerminalNeutral,
            }, order);
        }
        Assert.AreEqual(1L, sink.PublishedCount);
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.None,
            sink.LastFailure);
    }

    [TestMethod]
    [DoNotParallelize]
    public void ProfileHoldModeRotatesStandaloneJoyConWithoutRebinding()
    {
        const int slot = 0;
        Switch2JoyConHoldMode previous =
            Global.Switch2JoyConStandaloneHoldMode[slot];
        try
        {
            Switch2InputSessionDescriptor descriptor = Descriptor(
                Switch2ControllerModel.JoyCon2Left);
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(
                Switch2ControllerModel.JoyCon2Left, DeviceGeneration,
                TransportGeneration, out var runtime, out var created),
                created.ToString());
            runtime.DeviceSlotNumber = slot;
            Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreateBound(
                descriptor, runtime,
                PublicationAdmissionTimeoutMilliseconds,
                Switch2RuntimeTerminalScheduler.Instance, out var sink,
                out _, out var failure), failure.ToString());
            runtime.StartUpdate();

            Global.Switch2JoyConStandaloneHoldMode[slot] =
                Switch2JoyConHoldMode.Vertical;
            Global.BeginProfileSwitchRevision(slot);
            sink.PublishJoyCon(Frame(descriptor, counter: 1,
                timestamp: 10, buttons: 1u << 16));
            DS4State state = runtime.getRawCurrentState();
            Assert.IsTrue(state.DpadDown);
            Assert.IsFalse(state.Square);
            Assert.AreEqual(Switch2JoyConProfileMode.StandaloneVerticalLeft,
                state.Switch2JoyConRawInputStatus.Mode);

            Global.Switch2JoyConStandaloneHoldMode[slot] =
                Switch2JoyConHoldMode.Horizontal;
            Global.BeginProfileSwitchRevision(slot);
            sink.PublishJoyCon(Frame(descriptor, counter: 2,
                timestamp: 20, buttons: 1u << 16));
            state = runtime.getRawCurrentState();
            Assert.IsFalse(state.DpadDown);
            Assert.IsTrue(state.Square);
            Assert.AreEqual(
                Switch2JoyConProfileMode.StandaloneHorizontalLeft,
                state.Switch2JoyConRawInputStatus.Mode);
            Assert.AreEqual(2L, sink.PublishedCount);
            Assert.AreEqual(Switch2RuntimeInputDeviceState.Active,
                runtime.RuntimeState);
            Assert.AreEqual(DeviceGeneration, runtime.RuntimeGeneration);
            Assert.IsTrue(runtime.HasExactStandaloneBluetoothBinding(
                Switch2ControllerModel.JoyCon2Left, DeviceGeneration,
                TransportGeneration));
        }
        finally
        {
            Global.Switch2JoyConStandaloneHoldMode[slot] = previous;
            Global.BeginProfileSwitchRevision(slot);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ControllerHoldModeOverrideWinsProfileOnNextPhysicalReport()
    {
        const int slot = 0;
        Switch2JoyConHoldMode previous =
            Global.Switch2JoyConStandaloneHoldMode[slot];
        string root = Path.Combine(Path.GetTempPath(), "ds4w-s2-sink-hold-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.IsTrue(Switch2JoyConHoldModeFileStore.TryOpen(root,
                out var store));
            byte[] key = Enumerable.Range(1, 32).Select(value =>
                (byte)value).ToArray();
            byte[] identity = Enumerable.Repeat((byte)0x5A, 16).ToArray();
            Assert.IsTrue(Switch2PersistentPeerId.TryDerive(key, identity,
                Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId,
                out var peer));
            Assert.IsTrue(store.TryStore(peer,
                Switch2JoyConHoldMode.Horizontal));

            Switch2InputSessionDescriptor descriptor = Descriptor(
                Switch2ControllerModel.JoyCon2Left);
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(
                Switch2ControllerModel.JoyCon2Left, DeviceGeneration,
                TransportGeneration, out var runtime, out var created),
                created.ToString());
            Assert.IsTrue(runtime.TryBindJoyConHoldModePersistence(store,
                peer));
            runtime.DeviceSlotNumber = slot;
            Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreateBound(
                descriptor, runtime,
                PublicationAdmissionTimeoutMilliseconds,
                Switch2RuntimeTerminalScheduler.Instance, out var sink,
                out _, out var failure), failure.ToString());
            runtime.StartUpdate();

            Global.Switch2JoyConStandaloneHoldMode[slot] =
                Switch2JoyConHoldMode.Vertical;
            sink.PublishJoyCon(Frame(descriptor, counter: 1,
                timestamp: 10, buttons: 1u << 16));
            Assert.IsTrue(runtime.getRawCurrentState().Square,
                "The persisted horizontal override must win the vertical profile default.");

            Assert.IsTrue(runtime.TrySetStandaloneJoyConHoldMode(
                Switch2JoyConHoldMode.Vertical, out bool persisted));
            Assert.IsTrue(persisted);
            Global.Switch2JoyConStandaloneHoldMode[slot] =
                Switch2JoyConHoldMode.Horizontal;
            sink.PublishJoyCon(Frame(descriptor, counter: 2,
                timestamp: 20, buttons: 1u << 16));
            DS4State state = runtime.getRawCurrentState();
            Assert.IsTrue(state.DpadDown,
                "The live vertical override must be visible on the next report.");
            Assert.IsFalse(state.Square);
            Assert.AreEqual(Switch2JoyConProfileMode.StandaloneVerticalLeft,
                state.Switch2JoyConRawInputStatus.Mode);
            Assert.AreEqual(2L, sink.PublishedCount);
        }
        finally
        {
            Global.Switch2JoyConStandaloneHoldMode[slot] = previous;
            Global.BeginProfileSwitchRevision(slot);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ProSteadyStateMappingAndPublicationAllocateNothing()
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(
            Switch2ControllerModel.ProController2);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration,
            Switch2Transport.BluetoothLe, out var runtime, out _));
        byte[] key = Enumerable.Range(1, 32).Select(value =>
            (byte)value).ToArray();
        byte[] identity = Enumerable.Repeat((byte)0x4A, 16).ToArray();
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(key, identity,
            Switch2ControllerModel.ProController2,
            Switch2AdvertisementCodec.ProController2ProductId,
            out var peer));
        Assert.IsTrue(Switch2GyroCalibrationRecord.TryCreate(
            new System.Numerics.Vector3(0.1f, -0.05f, 0.025f),
            out var calibration));
        var gyroStore = new PreloadedGyroCalibrationStore(peer,
            calibration);
        Assert.IsTrue(runtime.TryBindGyroCalibrationPersistence(gyroStore,
            peer));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreate(descriptor,
            runtime, PublicationAdmissionTimeoutMilliseconds, out var sink,
            out _));
        runtime.StartUpdate();

        var session = Session(descriptor);
        byte[] body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        for (int index = 0; index < 1_000; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)index);
            Assert.IsTrue(session.TryProcess(descriptor, body, index,
                out var frame, out _));
            sink.PublishPro(frame);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool valid = true;
        for (int index = 1_000; index < 11_000; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)index);
            valid &= session.TryProcess(descriptor, body, index,
                out var frame, out _);
            sink.PublishPro(frame);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(11_000L, sink.PublishedCount);
        Assert.AreEqual(0, gyroStore.QueuedCount,
            "Adopting a persisted bias must not rewrite it from the report path.");
    }

    [TestMethod]
    public void JoyConSteadyStateMappingAndPublicationAllocateNothing()
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(
            Switch2ControllerModel.JoyCon2Left);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(
            Switch2ControllerModel.JoyCon2Left, DeviceGeneration,
            TransportGeneration, out var runtime, out _));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreate(descriptor,
            runtime, PublicationAdmissionTimeoutMilliseconds, out var sink,
            out _));
        Assert.IsTrue(runtime.TrySetStandaloneJoyConHoldMode(
            Switch2JoyConHoldMode.Horizontal, out bool persisted));
        Assert.IsFalse(persisted);
        runtime.StartUpdate();

        var session = Session(descriptor);
        byte[] body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        for (int index = 0; index < 1_000; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)index);
            Assert.IsTrue(session.TryProcess(descriptor, body, index,
                out var frame, out _));
            sink.PublishJoyCon(frame);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool valid = true;
        for (int index = 1_000; index < 11_000; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)index);
            valid &= session.TryProcess(descriptor, body, index,
                out var frame, out _);
            sink.PublishJoyCon(frame);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(11_000L, sink.PublishedCount);
    }

    private static void AssertTransientProAdmissionBusy()
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(
            Switch2ControllerModel.ProController2);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration,
            Switch2Transport.BluetoothLe, out var runtime, out _));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreate(descriptor,
            runtime, PublicationAdmissionTimeoutMilliseconds, out var sink,
            out _));
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        int regular = 0;
        runtime.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                    Switch2RuntimeReportKind.Regular &&
                Interlocked.Increment(ref regular) == 1)
            {
                entered.Set();
                release.Wait(2_000);
            }
        };
        runtime.StartUpdate();
        Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(
            Frame(descriptor, counter: 1, timestamp: 10), out var occupying,
            out _));
        Task<bool> occupied = Task.Run(() =>
            runtime.TryPublishPro(occupying));
        Assert.IsTrue(entered.Wait(1_000));
        Task publication = Task.Run(() => sink.PublishPro(
            Frame(descriptor, counter: 2, timestamp: 20)));
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                sink.PublicationAdmissionWaitCount > 0, 1_000));
        }
        finally
        {
            release.Set();
        }
        Assert.IsTrue(occupied.GetAwaiter().GetResult());
        publication.GetAwaiter().GetResult();
        Assert.AreEqual(1L, sink.PublishedCount);
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.None,
            sink.LastFailure);
    }

    private static void AssertTransientJoyConAdmissionBusy()
    {
        Switch2InputSessionDescriptor descriptor = Descriptor(
            Switch2ControllerModel.JoyCon2Left);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(
            Switch2ControllerModel.JoyCon2Left, DeviceGeneration,
            TransportGeneration, out var runtime, out _));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreate(descriptor,
            runtime, PublicationAdmissionTimeoutMilliseconds, out var sink,
            out _));
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        int regular = 0;
        runtime.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                    Switch2RuntimeReportKind.Regular &&
                Interlocked.Increment(ref regular) == 1)
            {
                entered.Set();
                release.Wait(2_000);
            }
        };
        runtime.StartUpdate();
        Switch2CanonicalInputFrame first = Frame(descriptor,
            counter: 1, timestamp: 10);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(
            Switch2JoyConProfileMode.StandaloneHorizontalLeft, descriptor,
            out var mapper));
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(mapper,
            first, out _, out var occupying, out _));
        Task<bool> occupied = Task.Run(() =>
            runtime.TryPublishStandaloneJoyCon(occupying));
        Assert.IsTrue(entered.Wait(1_000));
        Task publication = Task.Run(() => sink.PublishJoyCon(
            Frame(descriptor, counter: 2, timestamp: 20)));
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                sink.PublicationAdmissionWaitCount > 0, 1_000));
        }
        finally
        {
            release.Set();
        }
        Assert.IsTrue(occupied.GetAwaiter().GetResult());
        publication.GetAwaiter().GetResult();
        Assert.AreEqual(1L, sink.PublishedCount);
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.None,
            sink.LastFailure);
    }

    private static Switch2InputSessionDescriptor Descriptor(
        Switch2ControllerModel model,
        ulong deviceGeneration = DeviceGeneration)
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid, InputProperties,
            model, out Switch2InputProtocolIdentity identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity,
            deviceGeneration, TransportGeneration, QpcFrequency,
            out Switch2InputSessionDescriptor descriptor));
        return descriptor;
    }

    private static Switch2InputSession Session(
        in Switch2InputSessionDescriptor descriptor)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            descriptor.Identity.Model, descriptor.DeviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));
        return new Switch2InputSession(descriptor, calibration);
    }

    private static Switch2CanonicalInputFrame Frame(
        in Switch2InputSessionDescriptor descriptor, uint counter,
        long timestamp, uint buttons = 0)
    {
        var session = Session(descriptor);
        byte[] body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), counter);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), buttons);
        Assert.IsTrue(session.TryProcess(descriptor, body, timestamp,
            out Switch2CanonicalInputFrame frame,
            out Switch2InputSessionFailure failure), failure.ToString());
        return frame;
    }

    private sealed class PreloadedGyroCalibrationStore :
        ISwitch2GyroCalibrationStore
    {
        private readonly Switch2PersistentPeerId expectedPeer;
        private readonly Switch2GyroCalibrationRecord calibration;

        internal PreloadedGyroCalibrationStore(
            Switch2PersistentPeerId expectedPeer,
            in Switch2GyroCalibrationRecord calibration)
        {
            this.expectedPeer = expectedPeer;
            this.calibration = calibration;
        }

        internal int QueuedCount { get; private set; }

        public bool TryLoad(Switch2PersistentPeerId peerId,
            out Switch2GyroCalibrationRecord loaded)
        {
            loaded = calibration;
            return peerId == expectedPeer;
        }

        public bool TryQueueStore(Switch2PersistentPeerId peerId,
            in Switch2GyroCalibrationRecord value)
        {
            QueuedCount++;
            return peerId == expectedPeer && value.IsValid;
        }
    }
}
