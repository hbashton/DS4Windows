using System;
using System.Diagnostics;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2BluetoothFeedbackLifetimeTests
{
    private const ulong DeviceGeneration = 17;
    private const ulong TransportGeneration = 23;

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void XboxFeedbackTuningChangePreservesFreshNeutralAndExpiredOrdering(
        bool expired, bool previouslyActuated)
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration, TransportGeneration, out var feedback));
        Assert.IsTrue(feedback.TryActivate());
        Assert.IsTrue(feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            if (previouslyActuated)
                Assert.IsTrue(session.TryPublish(Wire(1, session.OwnershipEpoch, 20_000, 0, 0, 0)));
            Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
            Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
                ControllerFeedbackSource.XboxOneVirtualDevice, ControllerFeedbackCommand.Neutral,
                ControllerFeedbackActuators.All, 0, 0, 0, 0, 2,
                DeviceGeneration, TransportGeneration, session.OwnershipEpoch,
                expired ? now - 500_000 : now, 250_000, out var frame));
            byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
            Assert.IsTrue(frame.TryWriteTo(wire));
            Assert.IsTrue(session.TryPublish(wire, mapImpulseTriggersToHdRumble: true),
                "An expired newer ordering watermark is valid even when there is no live effect to re-present.");
            if (previouslyActuated || !expired) AssertNeutral(lease.LastPayload);
            else Assert.AreEqual(0, lease.PayloadCount, "An expired first frame must never actuate.");
            Assert.IsFalse(session.TryPublish(wire), "The exact sequence remains consumed.");
            Assert.IsTrue(session.TryPublish(Wire(3, session.OwnershipEpoch, 20_000, 0, 0, 0),
                mapImpulseTriggersToHdRumble: true));
        }
        finally { _ = session.TryRetire(); _ = feedback.TryStopAndRetire(3); }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ProductionProfileSettersWakeXboxWorkerWithoutNewGamePacket()
    {
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var previousHub = DS4Windows.Program.rootHub;
        var previousAppHub = DS4WinWPF.App.rootHub;
        bool previousOutput = Global.EnableOutputDataToDS4[0];
        bool previousImpulse = Global.Switch2MapXboxImpulseTriggersToHdRumble[0];
        int previousDelay = Global.Switch2RumbleDelayMilliseconds[0];
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration, TransportGeneration, out var feedback));
        Assert.IsTrue(feedback.TryActivate());
        Assert.IsTrue(feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(DeviceGeneration,
                TransportGeneration, Switch2Transport.BluetoothLe, out var target, out _));
            var hub = (ControlService)System.Runtime.CompilerServices.RuntimeHelpers.
                GetUninitializedObject(typeof(ControlService));
            var output = new ViiperOutDevice(OutContType.ViiperXboxOne, ViiperVirtualDeviceType.XboxOne);
            hub.DS4Controllers = new DS4Device[] { target };
            hub.outputDevices = new OutputDevice[] { output };
            DS4Windows.Program.rootHub = hub;
            DS4WinWPF.App.rootHub = hub;
            Global.EnableOutputDataToDS4[0] = true;
            Global.Switch2MapXboxImpulseTriggersToHdRumble[0] = true;
            Global.Switch2RumbleDelayMilliseconds[0] = 0;
            void Set(string name, object value) => typeof(ViiperOutDevice).GetField(name, fields).SetValue(output, value);
            Set("connected", true);
            Set("feedbackDispatchStopRequested", false);
            Set("lastInputDeviceIndex", 0);
            Set("streamGeneration", 7L);
            Set("switch2FeedbackSession", session);
            // Identity-only stream fixture. No stream method, network or native
            // device API is used by these profile callback admission checks.
            Set("deviceStream", System.Runtime.CompilerServices.RuntimeHelpers.
                GetUninitializedObject(typeof(ViiperDeviceStream)));
            var deliver = (Func<byte[], int, bool>)typeof(ViiperOutDevice).
                GetMethod("TryApplyXboxOneFeedback", fields).CreateDelegate(typeof(Func<byte[], int, bool>), output);
            var refresh = (Func<bool>)typeof(ViiperOutDevice).
                GetMethod("ProcessXboxFeedbackPolicyRefresh", fields).CreateDelegate(typeof(Func<bool>), output);
            var signal = (WaitHandle)typeof(ViiperOutDevice).GetField("feedbackControlSignal", fields).GetValue(output);
            // Setter-only fixture avoids constructing an unrelated WPF view.
            var profile = (DS4WinWPF.DS4Forms.ViewModels.ProfileSettingsViewModel)
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    typeof(DS4WinWPF.DS4Forms.ViewModels.ProfileSettingsViewModel));
            int acknowledgements = 0, faults = 0;
            using var dispatcher = new XboxOneFeedbackDeliveryDispatcher(deliver,
                (_, _) => Interlocked.Increment(ref acknowledgements),
                () => Interlocked.Increment(ref faults), localPolicySignal: signal, processLocalPolicy: refresh);
            Assert.IsTrue(dispatcher.TryEnqueue(Wire(1, session.OwnershipEpoch,
                0, 0, 40_000, 50_000), 1));
            Assert.IsTrue(dispatcher.WaitForIdle(1_000));
            AssertHdRumble(lease.LastPayload, true, out _, out _);
            profile.Switch2MapXboxImpulseTriggersToHdRumble = false;
            Assert.IsTrue(SpinWait.SpinUntil(() => lease.PayloadCount >= 2, 1_000));
            AssertNeutral(lease.LastPayload);
            Assert.AreEqual(1, Volatile.Read(ref acknowledgements));
            profile.Switch2MapXboxImpulseTriggersToHdRumble = true;
            Assert.IsTrue(SpinWait.SpinUntil(() => lease.PayloadCount >= 3, 1_000));
            AssertNeutral(lease.LastPayload);
            Assert.IsTrue(dispatcher.TryEnqueue(Wire(2, session.OwnershipEpoch,
                0, 0, 40_000, 50_000), 2));
            Assert.IsTrue(dispatcher.WaitForIdle(1_000));
            AssertHdRumble(lease.LastPayload, true, out _, out _);
            int beforeDisable = lease.PayloadCount;
            profile.EnableOutputDataToDS4 = false;
            Assert.IsTrue(SpinWait.SpinUntil(() => lease.PayloadCount > beforeDisable, 1_000));
            AssertNeutral(lease.LastPayload);
            Assert.AreEqual(2, Volatile.Read(ref acknowledgements));
            Assert.AreEqual(0, Volatile.Read(ref faults));
        }
        finally
        {
            _ = session.TryRetire();
            _ = feedback.TryStopAndRetire(3);
            Global.EnableOutputDataToDS4[0] = previousOutput;
            Global.Switch2MapXboxImpulseTriggersToHdRumble[0] = previousImpulse;
            Global.Switch2RumbleDelayMilliseconds[0] = previousDelay;
            DS4Windows.Program.rootHub = previousHub;
            DS4WinWPF.App.rootHub = previousAppHub;
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LiveXboxPolicyDisablesRunningEffectWithoutBrokerSuccessor(bool disableAll)
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration, TransportGeneration, out var feedback));
        Assert.IsTrue(feedback.TryActivate());
        Assert.IsTrue(feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            Assert.IsTrue(session.TryPublish(Wire(1, session.OwnershipEpoch,
                0, 0, 40_000, 50_000), mapImpulseTriggersToHdRumble: true));
            AssertHdRumble(lease.LastPayload, true, out _, out _);
            Assert.IsTrue(session.TryPublish(Wire(2, session.OwnershipEpoch,
                0, 0, 40_000, 50_000), mapImpulseTriggersToHdRumble: true,
                rumbleDelayMilliseconds: 150));
            Assert.IsTrue(session.TryCaptureXboxPolicyRevision(out ulong revision));
            int before = lease.PayloadCount;
            Assert.IsTrue(session.TryRefreshXboxOutputPolicy(new(!disableAll, false), revision));
            Assert.AreEqual(before + 1, lease.PayloadCount);
            AssertNeutral(lease.LastPayload);
            Assert.IsTrue(session.TryRefreshXboxOutputPolicy(new(true, true), revision));
            AssertNeutral(lease.LastPayload); // Off/on must not resurrect the old impulse effect.
            int stoppedCount = lease.PayloadCount;
            Thread.Sleep(220);
            Assert.AreEqual(stoppedCount, lease.PayloadCount, "Old delayed effects must be canceled.");
            Assert.IsFalse(session.TryPublish(Wire(1, session.OwnershipEpoch, 1, 0, 0, 0)));
            Assert.IsTrue(session.TryPublish(Wire(3, session.OwnershipEpoch,
                0, 0, 40_000, 50_000), mapImpulseTriggersToHdRumble: true));
            AssertHdRumble(lease.LastPayload, true, out _, out _);
        }
        finally { _ = session.TryRetire(); _ = feedback.TryStopAndRetire(3); }
    }

    [TestMethod]
    public void LiveXboxPolicyCannotOvertakeNewerPacketAndCancelsFirstQueuedEffect()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration, TransportGeneration, out var feedback));
        Assert.IsTrue(feedback.TryActivate());
        Assert.IsTrue(feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            Assert.IsTrue(session.TryPublish(Wire(1, session.OwnershipEpoch,
                20_000, 30_000, 0, 0), rumbleDelayMilliseconds: 150));
            Assert.IsTrue(session.TryCaptureXboxPolicyRevision(out ulong oldRevision));
            Assert.IsTrue(session.TryRefreshXboxOutputPolicy(new(false, false), oldRevision));
            Thread.Sleep(220);
            Assert.AreEqual(0, lease.PayloadCount);
            Assert.IsTrue(session.TryPublish(Wire(2, session.OwnershipEpoch, 20_000, 30_000, 0, 0)));
            int before = lease.PayloadCount;
            Assert.IsTrue(session.TryRefreshXboxOutputPolicy(new(false, false), oldRevision));
            Assert.AreEqual(before, lease.PayloadCount);
            AssertHdRumble(lease.LastPayload, true, out _, out _);
        }
        finally { _ = session.TryRetire(); _ = feedback.TryStopAndRetire(3); }
    }

    [DataTestMethod]
    [DataRow(250, false)]
    [DataRow(9_999, false)]
    [DataRow(9_999, true)]
    public void TerminalBrokerStopBypassesPresentationDelay(int delay, bool invalidTuning)
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(feedback.TryActivate());
        Assert.IsTrue(feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            Assert.IsTrue(session.TryPublish(Wire(1, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000), mapImpulseTriggersToHdRumble: true));
            Assert.IsTrue(session.TryPublish(Wire(2, session.OwnershipEpoch,
                40_000, 40_000, 40_000, 40_000), rumbleDelayMilliseconds: delay));
            int beforeStop = lease.PayloadCount;
            Assert.IsTrue(session.TryPublish(Wire(3, session.OwnershipEpoch,
                0, 0, 0, 0, ControllerFeedbackCommand.Stop),
                bodyStrengthPercent: invalidTuning ? -1 : 100,
                rumbleDelayMilliseconds: delay));
            Assert.AreEqual(beforeStop + 1, lease.PayloadCount,
                "Terminal Stop must reach the sole writer before it is acknowledged, not enter a profile delay queue.");
            AssertNeutral(lease.LastPayload);
            Assert.IsFalse(session.TryPublish(Wire(4, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000)), "Terminal ownership must remain closed.");
            Assert.IsFalse(session.TryPublish(Wire(4, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000), rumbleDelayMilliseconds: delay),
                "A closed lifetime must not acknowledge a queued successor either.");
            Thread.Sleep(300);
            for (int index = beforeStop; index < lease.PayloadCount; index++)
                AssertNeutral(lease.PayloadAt(index));
        }
        finally
        {
            _ = session.TryRetire();
            _ = feedback.TryStopAndRetire(maxAttempts: 3);
        }
    }

    [DataTestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    public void RejectedBrokerFrameDoesNotCancelQueuedEffect(bool foreignLifetime, bool terminalStop)
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(feedback.TryActivate());
        Assert.IsTrue(feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            Assert.IsTrue(session.TryPublish(Wire(2, session.OwnershipEpoch,
                0, 0, 0, 0)));
            Assert.IsTrue(session.TryPublish(Wire(3, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000),
                mapImpulseTriggersToHdRumble: true, rumbleDelayMilliseconds: 150));
            int beforeRejectedStop = lease.PayloadCount;
            Assert.IsFalse(session.TryPublish(Wire(foreignLifetime ? 4UL : 1UL,
                session.OwnershipEpoch + (foreignLifetime ? 1UL : 0UL),
                terminalStop ? (ushort)0 : (ushort)1, 0, 0, 0,
                terminalStop ? ControllerFeedbackCommand.Stop : ControllerFeedbackCommand.Apply)));
            Assert.AreEqual(beforeRejectedStop, lease.PayloadCount);
            Assert.IsTrue(SpinWait.SpinUntil(
                () => lease.PayloadCount > beforeRejectedStop, 1_000),
                "A rejected broker frame must leave the accepted queue intact.");
            AssertHdRumble(lease.PayloadAt(beforeRejectedStop), true, out _, out _);
        }
        finally
        {
            _ = session.TryRetire();
            _ = feedback.TryStopAndRetire(maxAttempts: 3);
        }
    }

    [TestMethod]
    public void RejectedPhysicalStopStillFencesDelayedApplyWithoutClaimingDelivery()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(feedback.TryActivate());
        Assert.IsTrue(feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            Assert.IsTrue(session.TryPublish(Wire(1, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000)));
            Assert.IsTrue(session.TryPublish(Wire(2, session.OwnershipEpoch,
                40_000, 40_000, 40_000, 40_000), rumbleDelayMilliseconds: 150));
            lease.RejectWrites = true;
            Assert.IsFalse(session.TryPublish(Wire(3, session.OwnershipEpoch,
                0, 0, 0, 0, ControllerFeedbackCommand.Stop), rumbleDelayMilliseconds: 150),
                "Canonical admission must not be reported as physical delivery.");
            Assert.AreEqual(1, lease.PayloadCount);
            Assert.IsFalse(session.TryPublish(Wire(4, session.OwnershipEpoch,
                40_000, 40_000, 40_000, 40_000), rumbleDelayMilliseconds: 150));
            lease.RejectWrites = false;
            Thread.Sleep(220);
            for (int index = 1; index < lease.PayloadCount; index++)
                AssertNeutral(lease.PayloadAt(index));
            Assert.IsTrue(session.TryRetire(), "Retirement may retry the accepted Stop.");
            AssertNeutral(lease.LastPayload);
        }
        finally
        {
            lease.RejectWrites = false;
            _ = session.TryRetire();
            _ = feedback.TryStopAndRetire(maxAttempts: 3);
        }
    }

    [TestMethod]
    public void ExpiredPolicyRefreshCannotDriveBodyOrRichBleActuators()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(feedback.TryActivate());
        Assert.IsTrue(feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.DualSenseVirtualDevice, out var session));
        try
        {
            var body = new ControllerFeedbackActuatorState(20_000, 30_000, 0, 0);
            var group = Switch2HdRumbleFeedbackTranslator.CreateCompatibilityGroup(20_000, 30_000);
            Assert.IsTrue(session.TryPublish(body));
            AssertHdRumble(lease.LastPayload, true, out _, out _);
            Assert.IsTrue(session.TryPublish(body, expiresAtMicroseconds: 1));
            AssertHdRumble(lease.LastPayload, false, out _, out _);
            Assert.IsTrue(session.TryPublish(body));
            Assert.IsTrue(session.TryPublishSourcePreserved(body,
                Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand,
                group, group, expiresAtMicroseconds: 1));
            AssertHdRumble(lease.LastPayload, false, out _, out _);
        }
        finally
        {
            Assert.IsTrue(session.TryRetire());
            Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
        }
    }

    [TestMethod]
    public void DisabledXboxOutputNeutralizesSoleWriterAndCancelsQueuedRumble()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(feedback.TryActivate());
        Assert.IsTrue(feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            out Switch2VirtualFeedbackSession session));
        try
        {
            Assert.IsTrue(session.TryPublish(Wire(1, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000),
                mapImpulseTriggersToHdRumble: true));
            AssertHdRumble(lease.LastPayload, expectAmplitude: true,
                out _, out _);

            Assert.IsTrue(session.TryPublish(Wire(2, session.OwnershipEpoch,
                10_000, 10_000, 10_000, 10_000),
                mapImpulseTriggersToHdRumble: true,
                rumbleDelayMilliseconds: 250, profileRevision: 7));
            Assert.IsTrue(ControllerFeedbackFrame.TryReadFrom(
                Wire(3, session.OwnershipEpoch, 40_000, 40_000,
                    50_000, 50_000), out var requested));
            Assert.IsTrue(ViiperOutDevice.TryApplyXboxOneFeedbackOutputPolicy(
                requested, outputEnabled: false, out var suppressed));
            byte[] suppressedWire = new byte[
                ControllerFeedbackFrame.SerializedLength];
            Assert.IsTrue(suppressed.TryWriteTo(suppressedWire));

            // Match the production disabled-output route: zero delay and no
            // impulse-release envelope. The existing session cancels both
            // pending presentation mechanisms before delivering Neutral.
            Assert.IsTrue(session.TryPublish(suppressedWire,
                mapImpulseTriggersToHdRumble: false,
                rumbleDelayMilliseconds: 0, profileRevision: 7));
            AssertHdRumble(lease.LastPayload, expectAmplitude: false,
                out _, out _);
            int neutralIndex = lease.PayloadCount - 1;
            Thread.Sleep(350);
            for (int index = neutralIndex; index < lease.PayloadCount; index++)
            {
                AssertHdRumble(lease.PayloadAt(index), expectAmplitude: false,
                    out _, out _);
            }
            Assert.IsFalse(session.TryPublish(Wire(2,
                session.OwnershipEpoch, 10_000, 10_000, 10_000, 10_000)),
                "Disabling output must retain the anti-replay watermark.");
            Assert.IsTrue(session.TryPublish(Wire(4,
                session.OwnershipEpoch, 10_000, 10_000, 10_000, 10_000)));
            AssertHdRumble(lease.LastPayload, expectAmplitude: true,
                out _, out _);
        }
        finally
        {
            Assert.IsTrue(session.TryRetire());
            Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
        }
    }

    [TestMethod]
    public void ExactCfbkBindingDrivesSoleBleWriterAndNeutralizesRetirement()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(DeviceGeneration,
            TransportGeneration, Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, feedback));
        Assert.IsTrue(runtime.TryGetStandaloneFeedbackBinding(
            out ulong deviceGeneration, out ulong transportGeneration));
        Assert.AreEqual(DeviceGeneration, deviceGeneration);
        Assert.AreEqual(TransportGeneration, transportGeneration);

        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));
        byte[] apply = Wire(sequence: 1,
            ownershipEpoch: session.OwnershipEpoch,
            bodyLow: 20_000, bodyHigh: 30_000,
            leftTrigger: 40_000, rightTrigger: 50_000);
        Assert.IsFalse(session.TryPublish(apply,
            mapImpulseTriggersToHdRumble: true),
            "Feedback must remain fenced until runtime activation.");

        runtime.DeviceSlotNumber = 2;
        runtime.StartUpdate();
        Assert.AreEqual((byte)3, lease.LastPlayerLedNumber);
        Assert.IsTrue(session.TryPublish(apply,
            mapImpulseTriggersToHdRumble: true));
        Assert.AreEqual(1, lease.WriteCount);
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out byte applyCounter,
            out Switch2HdRumbleGroup applyLeft,
            out Switch2HdRumbleGroup applyRight, out _));
        Assert.AreEqual((byte)0, applyCounter);
        Assert.IsTrue(HasAmplitude(applyLeft));
        Assert.IsTrue(HasAmplitude(applyRight));
        Assert.AreNotEqual(applyLeft, applyRight,
            "Independent trigger channels must survive side translation.");

        Assert.IsTrue(session.TryRetire());
        Assert.AreEqual(2, lease.WriteCount);
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out byte neutralCounter,
            out Switch2HdRumbleGroup neutralLeft,
            out Switch2HdRumbleGroup neutralRight, out _));
        Assert.AreEqual((byte)1, neutralCounter);
        Assert.IsFalse(HasAmplitude(neutralLeft));
        Assert.IsFalse(HasAmplitude(neutralRight));

        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession successor));
        Assert.IsTrue(successor.TryPublish(Wire(sequence: 1,
            ownershipEpoch: successor.OwnershipEpoch, bodyLow: 10_000,
            bodyHigh: 5_000,
            leftTrigger: 0, rightTrigger: 0)));
        Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3),
            $"{feedback.LastRetirementFailure}/" +
            feedback.LastPhysicalWriteFailure);
        Assert.IsTrue(feedback.IsRetired);
        Assert.IsFalse(successor.TryPublish(Wire(sequence: 2,
            ownershipEpoch: successor.OwnershipEpoch, bodyLow: 1,
            bodyHigh: 1,
            leftTrigger: 0, rightTrigger: 0)));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out _, out neutralLeft,
            out neutralRight, out _));
        Assert.IsFalse(HasAmplitude(neutralLeft));
        Assert.IsFalse(HasAmplitude(neutralRight));
    }

    [TestMethod]
    public void UnpublishedLifetimeRetiresWithoutPhysicalWrite()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(feedback.TryAbortUnpublished());
        Assert.IsTrue(feedback.IsRetired);
        Assert.AreEqual(0, lease.WriteCount);
    }

    [TestMethod]
    public void NativeConnectionProfileEffectUsesSoleBleWriterLosslessly()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(feedback.TryActivate());
        Assert.IsTrue(feedback.TryCreateLane(
            ControllerFeedbackPublicationOrigin.ProfileEffect,
            ControllerFeedbackSource.Xbox360VirtualDevice,
            ownershipEpoch: 1, timeToLiveMicroseconds: 250_000,
            renewalIntervalMicroseconds: 100_000, out var lane));

        Assert.IsTrue(feedback.TryPublishNativeProfileEffectAndPump(lane,
            Switch2ConnectionHaptic.ProBassMarker,
            Switch2ConnectionHaptic.ProBassGroup,
            Switch2ConnectionHaptic.ProBassGroup));
        Assert.AreEqual(1, lease.WriteCount);
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.
            TryDecodeProController(lease.LastPayload, out _,
                out Switch2HdRumbleGroup left,
                out Switch2HdRumbleGroup right, out _));
        Assert.AreEqual(Switch2ConnectionHaptic.ProBassGroup, left);
        Assert.AreEqual(Switch2ConnectionHaptic.ProBassGroup, right);

        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(
            out ulong now));
        Assert.IsTrue(lane.TryWithdraw(now));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
            feedback.TryPumpOnce(now, out _));
        Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
    }

    [TestMethod]
    [DoNotParallelize]
    public void CommittedRuntimeSchedulesExactConnectionSignatureOffHotPath()
    {
        bool previous = Global.Switch2ConnectionHapticEnabled[0];
        try
        {
            Global.Switch2ConnectionHapticEnabled[0] = true;
            RecordingLease lease = new();
            Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, out var feedback));
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
                DeviceGeneration, TransportGeneration,
                Switch2Transport.BluetoothLe,
                out Switch2RuntimeInputDevice runtime, out _));
            Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, feedback));
            runtime.DeviceSlotNumber = 0;
            runtime.StartUpdate();

            Assert.IsTrue(runtime.TryStartConnectionHaptic());
            Assert.IsTrue(SpinWait.SpinUntil(
                () => lease.PayloadCount >= 1,
                TimeSpan.FromSeconds(1)));
            runtime.setRumble(0, 0);
            Assert.IsTrue(SpinWait.SpinUntil(
                () => lease.PayloadCount >= 4,
                TimeSpan.FromSeconds(3)));
            Assert.IsFalse(runtime.TryStartConnectionHaptic(),
                "One logical controller generation must play at most one connection cue.");

            AssertGroup(lease.PayloadAt(0),
                Switch2ConnectionHaptic.ProBassGroup);
            AssertNeutral(lease.PayloadAt(1));
            AssertGroup(lease.PayloadAt(2),
                Switch2ConnectionHaptic.ProSharpClickGroup);
            AssertNeutral(lease.PayloadAt(3));

            Assert.IsTrue(runtime.TryPublishTerminalNeutral());
            Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
        }
        finally
        {
            Global.Switch2ConnectionHapticEnabled[0] = previous;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ExplicitPreviewCancelsCueWithoutLaterProfileResurrection()
    {
        bool previous = Global.Switch2ConnectionHapticEnabled[0];
        try
        {
            Global.Switch2ConnectionHapticEnabled[0] = true;
            RecordingLease lease = new();
            Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, out var feedback));
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
                DeviceGeneration, TransportGeneration,
                Switch2Transport.BluetoothLe,
                out Switch2RuntimeInputDevice runtime, out _));
            Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
                Switch2ControllerModel.ProController2, DeviceGeneration,
                TransportGeneration, feedback));
            runtime.DeviceSlotNumber = 0;
            runtime.StartUpdate();
            Assert.IsTrue(runtime.TryStartConnectionHaptic());
            Assert.IsTrue(SpinWait.SpinUntil(
                () => lease.PayloadCount >= 1,
                TimeSpan.FromSeconds(1)));

            runtime.SetRumblePreview(lightMotorActive: true,
                lightMotorStrength: byte.MaxValue,
                heavyMotorActive: false, heavyMotorStrength: 0);
            Thread.Sleep(350);
            for (int index = 0; index < lease.PayloadCount; index++)
            {
                Assert.IsTrue(Switch2BluetoothHdRumbleCodec.
                    TryDecodeProController(lease.PayloadAt(index), out _,
                        out Switch2HdRumbleGroup left,
                        out Switch2HdRumbleGroup right, out _));
                Assert.AreNotEqual(Switch2ConnectionHaptic.
                    ProSharpClickGroup,
                    left);
                Assert.AreNotEqual(Switch2ConnectionHaptic.
                    ProSharpClickGroup,
                    right);
            }

            runtime.ClearRumblePreview();
            Assert.IsTrue(runtime.TryPublishTerminalNeutral());
            Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
        }
        finally
        {
            Global.Switch2ConnectionHapticEnabled[0] = previous;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void InteractiveIdentificationSchedulesTwoExactPreviewPulses()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration,
            Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, feedback));
        runtime.DeviceSlotNumber = 0;
        runtime.StartUpdate();

        Assert.IsTrue(runtime.TryStartIdentificationHaptic());
        Assert.IsTrue(SpinWait.SpinUntil(
            () => lease.PayloadCount >= 4,
            TimeSpan.FromSeconds(2)));
        AssertGroup(lease.PayloadAt(0),
            Switch2IdentificationHaptic.ProPulseGroup);
        AssertNeutral(lease.PayloadAt(1));
        AssertGroup(lease.PayloadAt(2),
            Switch2IdentificationHaptic.ProPulseGroup);
        AssertNeutral(lease.PayloadAt(3));

        Assert.IsTrue(runtime.TryPublishTerminalNeutral());
        Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
    }

    [TestMethod]
    [DoNotParallelize]
    public void StandaloneJoyConUsesDonorPostRenderAmplitudeLawForBothCues()
    {
        bool previous = Global.Switch2ConnectionHapticEnabled[0];
        try
        {
            Global.Switch2ConnectionHapticEnabled[0] = true;
            const Switch2ControllerModel model =
                Switch2ControllerModel.JoyCon2Right;
            RecordingLease lease = new(model);
            Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
                model, DeviceGeneration, TransportGeneration,
                out var feedback));
            Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(
                model, DeviceGeneration, TransportGeneration,
                out Switch2RuntimeInputDevice runtime, out _));
            Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(model,
                DeviceGeneration, TransportGeneration, feedback));
            runtime.DeviceSlotNumber = 0;
            runtime.StartUpdate();

            Assert.IsTrue(runtime.TryStartConnectionHaptic());
            Assert.IsTrue(SpinWait.SpinUntil(
                () => lease.PayloadCount >= 4,
                TimeSpan.FromSeconds(3)));
            AssertJoyConGroup(lease.PayloadAt(0),
                Switch2ConnectionHaptic.JoyConBassGroup);
            AssertJoyConNeutral(lease.PayloadAt(1));
            AssertJoyConGroup(lease.PayloadAt(2),
                Switch2ConnectionHaptic.JoyConSharpClickGroup);
            AssertJoyConNeutral(lease.PayloadAt(3));

            Assert.IsTrue(runtime.TryStartIdentificationHaptic());
            Assert.IsTrue(SpinWait.SpinUntil(
                () => lease.PayloadCount >= 8,
                TimeSpan.FromSeconds(2)));
            AssertJoyConGroup(lease.PayloadAt(4),
                Switch2IdentificationHaptic.JoyConPulseGroup);
            AssertJoyConNeutral(lease.PayloadAt(5));
            AssertJoyConGroup(lease.PayloadAt(6),
                Switch2IdentificationHaptic.JoyConPulseGroup);
            AssertJoyConNeutral(lease.PayloadAt(7));

            Assert.IsTrue(runtime.TryPublishTerminalNeutral());
            Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
        }
        finally
        {
            Global.Switch2ConnectionHapticEnabled[0] = previous;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void DelayedCfbkRefreshesTtlAtPresentationAndZeroDelayStaysDirect()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(DeviceGeneration,
            TransportGeneration, Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, feedback));
        runtime.StartUpdate();
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));

        Stopwatch watch = Stopwatch.StartNew();
        Assert.IsTrue(session.TryPublish(Wire(sequence: 1,
            ownershipEpoch: session.OwnershipEpoch, bodyLow: 20_000,
            bodyHigh: 10_000, leftTrigger: 0, rightTrigger: 0),
            rumbleDelayMilliseconds: 300, profileRevision: 1));
        Assert.AreEqual(0, lease.PayloadCount);
        Assert.IsTrue(SpinWait.SpinUntil(() => lease.PayloadCount >= 1,
            TimeSpan.FromSeconds(2)));
        Assert.IsTrue(watch.ElapsedMilliseconds >= 250,
            "The source frame's 250 ms TTL must be refreshed at delayed presentation, not expire at receipt.");
        AssertHdRumble(lease.PayloadAt(0), expectAmplitude: true,
            out _, out _);

        Assert.IsTrue(session.TryPublish(
            new ControllerFeedbackActuatorState(30_000, 15_000, 0, 0),
            rumbleDelayMilliseconds: 0, profileRevision: 2));
        Assert.AreEqual(2, lease.PayloadCount,
            "Zero remains the allocation-free direct publication behavior.");
        Assert.IsTrue(session.TryRetire());
        Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
    }

    [TestMethod]
    [DoNotParallelize]
    public void ProfileRevisionAndZeroDelayFlushStaleQueuedFeedback()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(DeviceGeneration,
            TransportGeneration, Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, feedback));
        runtime.StartUpdate();
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.Xbox360VirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));

        Assert.IsTrue(session.TryPublish(
            new ControllerFeedbackActuatorState(10_000, 0, 0, 0),
            rumbleDelayMilliseconds: 150, profileRevision: 10));
        Assert.IsTrue(session.TryPublish(
            new ControllerFeedbackActuatorState(0, 20_000, 0, 0),
            rumbleDelayMilliseconds: 150, profileRevision: 11));
        Thread.Sleep(220);
        Assert.AreEqual(1, lease.PayloadCount,
            "A profile revision must discard delayed feedback from the old mapping epoch.");

        Assert.IsTrue(session.TryPublish(
            new ControllerFeedbackActuatorState(5_000, 5_000, 0, 0),
            rumbleDelayMilliseconds: 150, profileRevision: 11));
        Assert.IsTrue(session.TryPublish(
            new ControllerFeedbackActuatorState(25_000, 25_000, 0, 0),
            rumbleDelayMilliseconds: 0, profileRevision: 12));
        Assert.AreEqual(2, lease.PayloadCount);
        Thread.Sleep(220);
        Assert.AreEqual(2, lease.PayloadCount,
            "Returning to zero delay must synchronously fence the old timer queue.");
        Assert.IsTrue(session.TryRetire());
        Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
    }

    [TestMethod]
    [DoNotParallelize]
    public void DelayedSourcePreservedFramesKeepOrderAndOscillatorIdentity()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(DeviceGeneration,
            TransportGeneration, Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, feedback));
        runtime.StartUpdate();
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.Switch2VirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));
        var first = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(101, 201, 301, 401),
            new Switch2HdRumbleSubframe(102, 202, 302, 402),
            new Switch2HdRumbleSubframe(103, 203, 303, 403));
        var second = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(111, 211, 311, 411),
            new Switch2HdRumbleSubframe(112, 212, 312, 412),
            new Switch2HdRumbleSubframe(113, 213, 313, 413));

        Assert.IsTrue(session.TryPublishSourcePreserved(
            new ControllerFeedbackActuatorState(1, 0, 0, 0),
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough,
            first, first, rumbleDelayMilliseconds: 60,
            profileRevision: 7));
        Assert.IsTrue(session.TryPublishSourcePreserved(
            new ControllerFeedbackActuatorState(2, 0, 0, 0),
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough,
            second, second, rumbleDelayMilliseconds: 60,
            profileRevision: 7));
        Assert.IsTrue(SpinWait.SpinUntil(() => lease.PayloadCount >= 2,
            TimeSpan.FromSeconds(2)));
        AssertGroup(lease.PayloadAt(0), first);
        AssertGroup(lease.PayloadAt(1), second);
        Assert.IsTrue(session.TryRetire());
        Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
    }

    [TestMethod]
    [DoNotParallelize]
    public void SessionRetirementFencesUndeliveredDelayedFeedback()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(DeviceGeneration,
            TransportGeneration, Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, feedback));
        runtime.StartUpdate();
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.Xbox360VirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));
        Assert.IsTrue(session.TryPublish(
            new ControllerFeedbackActuatorState(40_000, 20_000, 0, 0),
            rumbleDelayMilliseconds: 150, profileRevision: 1));
        Assert.IsTrue(session.TryRetire());
        Thread.Sleep(220);
        Assert.AreEqual(0, lease.PayloadCount,
            "A copied timer callback cannot cross successful session retirement.");
        Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
    }

    [TestMethod]
    [DoNotParallelize]
    public void XboxImpulseStopTraversesNinetyMillisecondBleReleaseEnvelope()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(DeviceGeneration,
            TransportGeneration, Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, feedback));
        runtime.StartUpdate();
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));

        Assert.IsTrue(session.TryPublish(Wire(sequence: 1,
            ownershipEpoch: session.OwnershipEpoch, bodyLow: 0,
            bodyHigh: 0, leftTrigger: ushort.MaxValue, rightTrigger: 0),
            mapImpulseTriggersToHdRumble: true));
        Assert.IsTrue(session.TryPublish(Wire(sequence: 2,
            ownershipEpoch: session.OwnershipEpoch, bodyLow: 0,
            bodyHigh: 0, leftTrigger: 0, rightTrigger: 0),
            mapImpulseTriggersToHdRumble: true));
        Assert.AreEqual(2, lease.PayloadCount);
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.
            TryDecodeProController(lease.PayloadAt(0), out _,
                out Switch2HdRumbleGroup initialLeft,
                out Switch2HdRumbleGroup initialRight, out _));
        Assert.IsTrue(HasAmplitude(initialLeft));
        Assert.IsFalse(HasAmplitude(initialRight));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.
            TryDecodeProController(lease.PayloadAt(1), out _,
                out Switch2HdRumbleGroup releaseStartLeft,
                out Switch2HdRumbleGroup releaseStartRight, out _));
        Assert.IsTrue(HasAmplitude(releaseStartLeft));
        Assert.AreEqual(initialLeft, releaseStartLeft);
        Assert.IsFalse(HasAmplitude(releaseStartRight));

        Stopwatch watch = Stopwatch.StartNew();
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            int count = lease.PayloadCount;
            if (count < 4)
            {
                return false;
            }
            return Switch2BluetoothHdRumbleCodec.TryDecodeProController(
                    lease.PayloadAt(count - 1), out _,
                    out Switch2HdRumbleGroup left,
                    out Switch2HdRumbleGroup right, out _) &&
                !HasAmplitude(left) && !HasAmplitude(right);
        }, TimeSpan.FromSeconds(2)));
        Assert.IsTrue(watch.ElapsedMilliseconds >= 60,
            "The release must not collapse into an immediate trigger stop.");

        int finalCount = lease.PayloadCount;
        ushort initialAmplitude = initialLeft.First.
            Oscillator0AmplitudeCode;
        bool sawIntermediate = false;
        for (int index = 2; index < finalCount - 1; index++)
        {
            Assert.IsTrue(Switch2BluetoothHdRumbleCodec.
                TryDecodeProController(lease.PayloadAt(index), out _,
                    out Switch2HdRumbleGroup left,
                    out Switch2HdRumbleGroup right, out _));
            Assert.IsTrue(HasAmplitude(left));
            ushort amplitude = left.First.Oscillator0AmplitudeCode;
            sawIntermediate |= amplitude > 0 &&
                amplitude < initialAmplitude;
            Assert.IsFalse(HasAmplitude(right));
        }
        Assert.IsTrue(sawIntermediate);
        Assert.IsTrue(session.TryRetire());
        Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
    }

    [TestMethod]
    [DoNotParallelize]
    public void SessionRetirementCancelsPendingImpulseReleaseTimer()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(DeviceGeneration,
            TransportGeneration, Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, feedback));
        runtime.StartUpdate();
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));
        Assert.IsTrue(session.TryPublish(Wire(sequence: 1,
            ownershipEpoch: session.OwnershipEpoch, bodyLow: 0,
            bodyHigh: 0, leftTrigger: ushort.MaxValue, rightTrigger: 0),
            mapImpulseTriggersToHdRumble: true));
        Assert.IsTrue(session.TryPublish(Wire(sequence: 2,
            ownershipEpoch: session.OwnershipEpoch, bodyLow: 0,
            bodyHigh: 0, leftTrigger: 0, rightTrigger: 0),
            mapImpulseTriggersToHdRumble: true));
        Assert.IsTrue(session.TryRetire());
        int afterRetirement = lease.PayloadCount;
        Thread.Sleep(150);
        Assert.AreEqual(afterRetirement, lease.PayloadCount,
            "A copied release callback cannot write after session retirement.");
        Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
    }

    private static void AssertGroup(byte[] payload,
        Switch2HdRumbleGroup expected)
    {
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.
            TryDecodeProController(payload, out _,
                out Switch2HdRumbleGroup left,
                out Switch2HdRumbleGroup right, out _));
        Assert.AreEqual(expected, left);
        Assert.AreEqual(expected, right);
    }

    private static void AssertNeutral(byte[] payload)
    {
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.
            TryDecodeProController(payload, out _,
                out Switch2HdRumbleGroup left,
                out Switch2HdRumbleGroup right, out _));
        Assert.IsFalse(HasAmplitude(left));
        Assert.IsFalse(HasAmplitude(right));
    }

    private static void AssertJoyConGroup(byte[] payload,
        Switch2HdRumbleGroup expected)
    {
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(payload,
            out _, out Switch2HdRumbleGroup group, out _));
        Assert.AreEqual(expected, group);
    }

    private static void AssertJoyConNeutral(byte[] payload)
    {
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(payload,
            out _, out Switch2HdRumbleGroup group, out _));
        Assert.IsFalse(HasAmplitude(group));
    }

    [TestMethod]
    public void LegacyVirtualOutputsShareOneMonotonicBleFeedbackLifetime()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(DeviceGeneration,
            TransportGeneration, Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, feedback));
        runtime.StartUpdate();

        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.Xbox360VirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession first));
        Assert.AreEqual(1UL, first.OwnershipEpoch);
        Assert.IsTrue(first.TryPublish(new ControllerFeedbackActuatorState(
            20_000, 30_000, 0, 0)));
        Assert.AreEqual(1, lease.WriteCount);
        Assert.IsTrue(first.TryRetire());
        Assert.AreEqual(2, lease.WriteCount,
            "Retiring one virtual output must physically neutralize it.");

        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.DualShock4VirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession successor));
        Assert.AreEqual(2UL, successor.OwnershipEpoch);
        Assert.IsTrue(successor.TryPublish(
            new ControllerFeedbackActuatorState(40_000, 10_000, 0, 0)));
        Assert.AreEqual(3, lease.WriteCount,
            "A later virtual output must not be rejected as an older owner.");
        Assert.IsTrue(successor.TryRetire());
        Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
    }

    [TestMethod]
    public void ProfileAndPreviewRumbleShareCanonicalHdRumbleWriter()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(DeviceGeneration,
            TransportGeneration, Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, feedback));
        runtime.StartUpdate();

        runtime.setRumble(rightLightFastMotor: 64,
            leftHeavySlowMotor: 128);
        Assert.AreEqual(1, lease.WriteCount);
        AssertHdRumble(lease.LastPayload, expectAmplitude: true,
            out Switch2HdRumbleGroup profileLeft,
            out Switch2HdRumbleGroup profileRight);
        AssertSustained(profileLeft);
        AssertSustained(profileRight);

        runtime.SetRumblePreview(lightMotorActive: true,
            lightMotorStrength: 200, heavyMotorActive: true,
            heavyMotorStrength: 100);
        Assert.AreEqual(3, lease.WriteCount,
            "A higher-priority preview must follow the exact Stop for the profile owner it supersedes.");
        AssertHdRumble(lease.LastPayload, expectAmplitude: true,
            out Switch2HdRumbleGroup previewLeft,
            out Switch2HdRumbleGroup previewRight);
        Assert.AreNotEqual(profileLeft, previewLeft);
        Assert.AreNotEqual(profileRight, previewRight);
        AssertSustained(previewLeft);
        AssertSustained(previewRight);

        runtime.ClearRumblePreview();
        Assert.AreEqual(5, lease.WriteCount,
            "Withdrawing preview must deliver its exact Stop before restoring the lower-priority profile effect.");
        AssertHdRumble(lease.LastPayload, expectAmplitude: true,
            out Switch2HdRumbleGroup restoredLeft,
            out Switch2HdRumbleGroup restoredRight);
        Assert.AreEqual(profileLeft, restoredLeft);
        Assert.AreEqual(profileRight, restoredRight);

        runtime.setRumble(rightLightFastMotor: 0,
            leftHeavySlowMotor: 0);
        Assert.AreEqual(6, lease.WriteCount);
        AssertHdRumble(lease.LastPayload, expectAmplitude: false,
            out _, out _);
        Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
    }

    [TestMethod]
    public void NativeSwitch2VirtualRumblePreservesAllOscillatorFields()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(DeviceGeneration,
            TransportGeneration, Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, feedback));
        runtime.StartUpdate();
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.Switch2VirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));

        var left = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(101, 201, 301, 401),
            new Switch2HdRumbleSubframe(102, 202, 302, 402),
            new Switch2HdRumbleSubframe(103, 203, 303, 403));
        var right = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(111, 211, 311, 411),
            new Switch2HdRumbleSubframe(112, 212, 312, 412),
            new Switch2HdRumbleSubframe(113, 213, 313, 413));
        Assert.IsTrue(session.TryPublishSourcePreserved(
            new ControllerFeedbackActuatorState(1, 0, 0, 0),
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough,
            left, right));

        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out _, out Switch2HdRumbleGroup deliveredLeft,
            out Switch2HdRumbleGroup deliveredRight, out _));
        Assert.AreEqual(left, deliveredLeft);
        Assert.AreEqual(right, deliveredRight);
        Assert.IsTrue(session.TryPublishSourcePreserved(
            new ControllerFeedbackActuatorState(1, 0, 0, 0),
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough,
            left, right, bodyStrengthPercent: 50));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out _, out deliveredLeft,
            out deliveredRight, out _));
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(50,
            out var halfStrength));
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
            ScaleSourcePreservedGroup(left, halfStrength), deliveredLeft);
        Assert.AreEqual(Switch2HdRumbleFeedbackTranslator.
            ScaleSourcePreservedGroup(right, halfStrength), deliveredRight);
        Assert.IsTrue(session.TryPublishSourcePreserved(
            new ControllerFeedbackActuatorState(1, 0, 0, 0),
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough,
            left, right, bodyStrengthPercent: 50,
            xboxBodyCarrierMode: true, xboxBodyFrequencyLevel: 4));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out _, out deliveredLeft,
            out deliveredRight, out _));
        AssertXboxBodyCarriers(deliveredLeft, expectedHigh: 276,
            expectedLow: 225);
        AssertXboxBodyCarriers(deliveredRight, expectedHigh: 276,
            expectedLow: 225);
        Assert.AreEqual((ushort)101,
            deliveredLeft.First.Oscillator0AmplitudeCode);
        Assert.AreEqual((ushort)201,
            deliveredLeft.First.Oscillator1AmplitudeCode);
        Assert.AreEqual((ushort)102,
            deliveredLeft.Third.Oscillator0AmplitudeCode);
        Assert.AreEqual((ushort)207,
            deliveredRight.Third.Oscillator1AmplitudeCode);
        Assert.IsTrue(session.TryRequestPlayerLedMask(0x06));
        Assert.AreEqual((byte)0x06, lease.LastPlayerLedMask);
        Assert.IsTrue(session.TryRequestPlayerLedMask(0x02));
        Assert.AreEqual((byte)0x02, lease.LastPlayerLedMask,
            "BLE must preserve every valid native four-segment mask exactly.");
        Assert.IsTrue(session.TryRequestPlayerLedMask(0x00));
        Assert.AreEqual((byte)0x00, lease.LastPlayerLedMask);
        Assert.IsFalse(session.TryRequestPlayerLedMask(0x10),
            "Bits outside the four physical player LEDs must be rejected.");
        Assert.IsTrue(session.TryRetire());
        Assert.IsFalse(session.TryRequestPlayerLedMask(0x01),
            "A retired virtual output must not retain LED authority.");

        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.DualSenseVirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession dualSense));
        Assert.IsTrue(dualSense.TryPublishSourcePreserved(
            new ControllerFeedbackActuatorState(1, 0, 0, 0),
            Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand,
            right, left));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out _, out deliveredLeft,
            out deliveredRight, out _));
        Assert.AreEqual(right, deliveredLeft);
        Assert.AreEqual(left, deliveredRight);
        Assert.IsTrue(dualSense.TryPublishSourcePreserved(
            new ControllerFeedbackActuatorState(1, 0, 0, 0),
            Switch2HdRumbleFeedbackFidelity.
                DualSenseAdaptiveTriggerApproximation,
            left, right));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out _, out deliveredLeft,
            out deliveredRight, out _));
        Assert.AreEqual(left, deliveredLeft);
        Assert.AreEqual(right, deliveredRight,
            "A changed rich payload must traverse the BLE owner even when its canonical marker is unchanged.");
        Assert.IsTrue(dualSense.TryRetire());
        Assert.IsTrue(feedback.TryStopAndRetire(maxAttempts: 3));
    }

    private static void AssertXboxBodyCarriers(
        Switch2HdRumbleGroup group, ushort expectedHigh,
        ushort expectedLow)
    {
        Assert.AreEqual(expectedHigh,
            group.First.Oscillator0ControlCode);
        Assert.AreEqual(expectedLow,
            group.First.Oscillator1ControlCode);
        Assert.AreEqual(expectedHigh,
            group.Second.Oscillator0ControlCode);
        Assert.AreEqual(expectedLow,
            group.Second.Oscillator1ControlCode);
        Assert.AreEqual(expectedHigh,
            group.Third.Oscillator0ControlCode);
        Assert.AreEqual(expectedLow,
            group.Third.Oscillator1ControlCode);
    }

    [TestMethod]
    public void LiveProfileSelectionControlsBleImpulseTriggerSidedness()
    {
        RecordingLease lease = new();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(DeviceGeneration,
            TransportGeneration, Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            TransportGeneration, feedback));
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));
        runtime.DeviceSlotNumber = 1;
        runtime.StartUpdate();

        Assert.IsTrue(session.TryPublish(Wire(sequence: 1,
            ownershipEpoch: session.OwnershipEpoch, bodyLow: 0, bodyHigh: 0,
            leftTrigger: ushort.MaxValue, rightTrigger: 0),
            mapImpulseTriggersToHdRumble: false));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out _, out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right, out _));
        Assert.IsFalse(HasAmplitude(left));
        Assert.IsFalse(HasAmplitude(right));

        Assert.IsTrue(session.TryPublish(Wire(sequence: 2,
            ownershipEpoch: session.OwnershipEpoch, bodyLow: 0, bodyHigh: 0,
            leftTrigger: ushort.MaxValue, rightTrigger: 0),
            mapImpulseTriggersToHdRumble: true,
            dynamicImpulseFrequency: false, fixedImpulseFrequencyLevel: 1,
            impulseStrengthLevel: 1));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out _, out left, out right, out _));
        Assert.IsTrue(HasAmplitude(left));
        Assert.IsFalse(HasAmplitude(right));
        Assert.AreEqual((ushort)300,
            left.First.Oscillator0ControlCode);

        Assert.IsTrue(session.TryPublish(Wire(sequence: 3,
            ownershipEpoch: session.OwnershipEpoch, bodyLow: 0, bodyHigh: 0,
            leftTrigger: 0, rightTrigger: ushort.MaxValue),
            mapImpulseTriggersToHdRumble: false));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out _, out left, out right, out _));
        Assert.IsFalse(HasAmplitude(left));
        Assert.IsFalse(HasAmplitude(right));

        Assert.IsTrue(session.TryPublish(Wire(sequence: 4,
            ownershipEpoch: session.OwnershipEpoch, bodyLow: 0, bodyHigh: 0,
            leftTrigger: 0, rightTrigger: ushort.MaxValue),
            mapImpulseTriggersToHdRumble: true,
            dynamicImpulseFrequency: true, fixedImpulseFrequencyLevel: 10,
            impulseStrengthLevel: 10));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            lease.LastPayload, out _, out left, out right, out _));
        Assert.IsFalse(HasAmplitude(left));
        Assert.IsTrue(HasAmplitude(right));
        Assert.AreEqual((ushort)481,
            right.First.Oscillator0ControlCode);
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void JoinedPairFansOneCanonicalFrameToExactLeftAndRightLifetimes()
    {
        const ulong runtimeGeneration = 101;
        const ulong pairEpoch = 102;
        const ulong leftDevice = 111;
        const ulong leftTransport = 112;
        const ulong rightDevice = 121;
        const ulong rightTransport = 122;
        var left = new RecordingLease(
            Switch2ControllerModel.JoyCon2Left, leftDevice, leftTransport);
        var right = new RecordingLease(
            Switch2ControllerModel.JoyCon2Right, rightDevice, rightTransport);
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreateJoined(left,
            right, runtimeGeneration, pairEpoch, leftDevice, leftTransport,
            rightDevice, rightTransport, out var feedback));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateJoinedJoyCon(
            runtimeGeneration, pairEpoch, leftDevice, leftTransport,
            rightDevice, rightTransport, out var runtime, out _));
        Assert.IsTrue(runtime.TryAttachJoinedBluetoothFeedbackLifetime(
            runtimeGeneration, pairEpoch, feedback));
        Assert.IsTrue(runtime.TryGetFeedbackBinding(out ulong feedbackDevice,
            out ulong feedbackTransport));
        Assert.AreEqual(runtimeGeneration, feedbackDevice);
        Assert.AreEqual(pairEpoch, feedbackTransport);
        Assert.IsFalse(runtime.TryGetStandaloneFeedbackBinding(out _, out _));
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            runtimeGeneration, pairEpoch,
            out Switch2VirtualFeedbackSession session));

        runtime.DeviceSlotNumber = 3;
        runtime.StartUpdate();
        Assert.AreEqual((byte)4, left.LastPlayerLedNumber);
        Assert.AreEqual((byte)4, right.LastPlayerLedNumber);
        Assert.IsTrue(session.TryRequestPlayerLedMask(0x0A));
        Assert.AreEqual((byte)0x0A, left.LastPlayerLedMask);
        Assert.AreEqual((byte)0x0A, right.LastPlayerLedMask,
            "A joined Joy-Con lifetime must preserve the same exact mask on both halves.");
        Assert.IsTrue(session.TryPublish(Wire(runtimeGeneration, pairEpoch,
            sequence: 1, ownershipEpoch: session.OwnershipEpoch,
            bodyLow: 10_000,
            bodyHigh: 20_000, leftTrigger: 45_000,
            rightTrigger: 55_000),
            mapImpulseTriggersToHdRumble: true));
        Assert.AreEqual(1, left.WriteCount);
        Assert.AreEqual(1, right.WriteCount);
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(
            left.LastPayload, out byte leftCounter,
            out Switch2HdRumbleGroup leftGroup, out _));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(
            right.LastPayload, out byte rightCounter,
            out Switch2HdRumbleGroup rightGroup, out _));
        Assert.AreEqual((byte)0, leftCounter);
        Assert.AreEqual((byte)0, rightCounter);
        Assert.IsTrue(HasAmplitude(leftGroup));
        Assert.IsTrue(HasAmplitude(rightGroup));
        Assert.AreNotEqual(leftGroup, rightGroup);

        Assert.IsTrue(session.TryRetire());
        Assert.AreEqual(2, left.WriteCount);
        Assert.AreEqual(2, right.WriteCount);
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(
            left.LastPayload, out _, out leftGroup, out _));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(
            right.LastPayload, out _, out rightGroup, out _));
        Assert.IsFalse(HasAmplitude(leftGroup));
        Assert.IsFalse(HasAmplitude(rightGroup));
    }

    private static bool HasAmplitude(in Switch2HdRumbleGroup group) =>
        group.First.HasNonzeroAmplitude ||
        group.Second.HasNonzeroAmplitude ||
        group.Third.HasNonzeroAmplitude;

    private static void AssertHdRumble(byte[] payload, bool expectAmplitude,
        out Switch2HdRumbleGroup left, out Switch2HdRumbleGroup right)
    {
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(
            payload, out _, out left, out right, out _));
        Assert.AreEqual(expectAmplitude, HasAmplitude(left));
        Assert.AreEqual(expectAmplitude, HasAmplitude(right));
    }

    private static void AssertSustained(in Switch2HdRumbleGroup group)
    {
        Assert.AreEqual(group.First, group.Second);
        Assert.AreEqual(group.First, group.Third);
    }

    private static byte[] Wire(ulong sequence, ulong ownershipEpoch,
        ushort bodyLow, ushort bodyHigh, ushort leftTrigger,
        ushort rightTrigger, ControllerFeedbackCommand? commandOverride = null)
    {
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(
            out ulong timestamp));
        ControllerFeedbackCommand command = commandOverride ?? (bodyLow == 0 && bodyHigh == 0 &&
            leftTrigger == 0 && rightTrigger == 0 ?
                ControllerFeedbackCommand.Neutral :
                ControllerFeedbackCommand.Apply);
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            command, ControllerFeedbackActuators.All,
            bodyLow, bodyHigh, leftTrigger, rightTrigger, sequence,
            DeviceGeneration, TransportGeneration, ownershipEpoch, timestamp,
            ControllerFeedbackFrame.MaxTimeToLiveMicroseconds,
            out ControllerFeedbackFrame frame));
        byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
        Assert.IsTrue(frame.TryWriteTo(wire));
        return wire;
    }

    private static byte[] Wire(ulong deviceGeneration,
        ulong transportGeneration, ulong sequence, ulong ownershipEpoch,
        ushort bodyLow, ushort bodyHigh, ushort leftTrigger,
        ushort rightTrigger)
    {
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(
            out ulong timestamp));
        ControllerFeedbackCommand command = bodyLow == 0 && bodyHigh == 0 &&
            leftTrigger == 0 && rightTrigger == 0 ?
                ControllerFeedbackCommand.Neutral :
                ControllerFeedbackCommand.Apply;
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            command, ControllerFeedbackActuators.All,
            bodyLow, bodyHigh, leftTrigger, rightTrigger, sequence,
            deviceGeneration, transportGeneration, ownershipEpoch, timestamp,
            ControllerFeedbackFrame.MaxTimeToLiveMicroseconds,
            out ControllerFeedbackFrame frame));
        byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
        Assert.IsTrue(frame.TryWriteTo(wire));
        return wire;
    }

    internal sealed class RecordingLease :
        ISwitch2BluetoothHdRumbleBindableTransportLease,
        ISwitch2BluetoothPlayerLedTransportLease
    {
        internal int WriteCount;
        internal volatile bool RejectWrites;
        internal byte[] LastPayload = Array.Empty<byte>();
        internal byte LastPlayerLedNumber;
        internal byte LastPlayerLedMask;
        private readonly object payloadGate = new();
        private readonly List<byte[]> payloads = new();
        private readonly Switch2ControllerModel model;
        private readonly ulong deviceGeneration;
        private readonly ulong transportGeneration;

        internal RecordingLease(
            Switch2ControllerModel model =
                Switch2ControllerModel.ProController2,
            ulong deviceGeneration = DeviceGeneration,
            ulong transportGeneration = TransportGeneration)
        {
            this.model = model;
            this.deviceGeneration = deviceGeneration;
            this.transportGeneration = transportGeneration;
        }

        public bool HasHdRumbleOutput => true;

        public bool HasPlayerLedOutput => true;

        public bool TryBindHdRumbleLifetime(Switch2ControllerModel model,
            ulong deviceGeneration, ulong transportGeneration) =>
            Authenticates(model, deviceGeneration, transportGeneration);

        public bool Authenticates(Switch2ControllerModel model,
            ulong deviceGeneration, ulong transportGeneration) =>
            model == this.model &&
            deviceGeneration == this.deviceGeneration &&
            transportGeneration == this.transportGeneration;

        public Switch2BluetoothHdRumbleTransportWriteResult TryWritePayload(
            ReadOnlySpan<byte> payload, Switch2ControllerModel expectedModel,
            ulong expectedDeviceGeneration,
            ulong expectedTransportGeneration)
        {
            if (RejectWrites)
                return Switch2BluetoothHdRumbleTransportWriteResult.Reject(
                    expectedModel, expectedDeviceGeneration, expectedTransportGeneration,
                    Switch2BluetoothHdRumbleTransportWriteFailure.TransportRejected);
            byte[] copy = payload.ToArray();
            lock (payloadGate)
            {
                WriteCount++;
                LastPayload = copy;
                payloads.Add(copy);
            }
            return Switch2BluetoothHdRumbleTransportWriteResult.Complete(
                expectedModel, expectedDeviceGeneration,
                expectedTransportGeneration, payload.Length);
        }

        internal int PayloadCount
        {
            get { lock (payloadGate) { return payloads.Count; } }
        }

        internal byte[] PayloadAt(int index)
        {
            lock (payloadGate)
            {
                return payloads[index];
            }
        }

        public Switch2BluetoothPlayerLedRequestResult TryRequestPlayerLed(
            byte playerNumber, Switch2ControllerModel expectedModel,
            ulong expectedDeviceGeneration,
            ulong expectedTransportGeneration)
        {
            if (!Authenticates(expectedModel, expectedDeviceGeneration,
                    expectedTransportGeneration))
            {
                return Switch2BluetoothPlayerLedRequestResult.Reject(
                    Switch2BluetoothPlayerLedRequestFailure.StaleLifetime);
            }
            LastPlayerLedNumber = playerNumber;
            return Switch2BluetoothPlayerLedRequestResult.Admit();
        }

        public Switch2BluetoothPlayerLedRequestResult TryRequestPlayerLedMask(
            byte playerLedMask, Switch2ControllerModel expectedModel,
            ulong expectedDeviceGeneration,
            ulong expectedTransportGeneration)
        {
            if ((playerLedMask & 0xF0) != 0)
            {
                return Switch2BluetoothPlayerLedRequestResult.Reject(
                    Switch2BluetoothPlayerLedRequestFailure.InvalidArgument);
            }
            if (!Authenticates(expectedModel, expectedDeviceGeneration,
                    expectedTransportGeneration))
            {
                return Switch2BluetoothPlayerLedRequestResult.Reject(
                    Switch2BluetoothPlayerLedRequestFailure.StaleLifetime);
            }
            LastPlayerLedMask = playerLedMask;
            return Switch2BluetoothPlayerLedRequestResult.Admit();
        }
    }
}
