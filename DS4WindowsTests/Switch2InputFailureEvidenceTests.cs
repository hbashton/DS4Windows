using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using DS4Windows;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2InputFailureEvidenceTests
{
    [TestMethod]
    public void EvidenceRetainsTheFirstFaultAcrossConcurrentLaterFailures()
    {
        var evidence = new Switch2InputFailureEvidence();
        var first = Switch2InputFailureSnapshot.Standalone(Switch2ControllerModel.JoyCon2Left,
            Switch2BluetoothRuntimeSinkFailure.ProfileMappingRejected,
            joyCon: Switch2JoyConProfileInputFailure.BackwardOrOutOfOrder);
        evidence.Record(first);
        Parallel.For(0, 32, _ => evidence.Record(new(default, "later cleanup failure")));
        Assert.AreSame(first, evidence.First);
    }

    [TestMethod]
    public void DiagnosticsQueueOnceAndNeverIncludeExceptionMessages()
    {
        var queued = new List<Action>();
        var messages = new List<string>();
        var observer = new Switch2InputFailureDiagnostics(messages.Add, queued.Add);
        var fault = Switch2InputFailureSnapshot.Standalone(Switch2ControllerModel.JoyCon2Right,
            Switch2BluetoothRuntimeSinkFailure.DependencyThrew,
            exception: new InvalidOperationException("private controller address and path"));
        Assert.IsFalse(observer.TryReport("JoyCon2Right", 23,
            Switch2BluetoothInputEndReason.Stopped,
            Switch2BluetoothInputDrainPumpFailure.None, fault, null));
        Assert.IsTrue(observer.TryReport("JoyCon2Right", 23,
            Switch2BluetoothInputEndReason.SinkFailure,
            Switch2BluetoothInputDrainPumpFailure.SinkRejected, fault, null));
        Assert.IsFalse(observer.TryReport("JoyCon2Right", 23,
            Switch2BluetoothInputEndReason.SinkFailure,
            Switch2BluetoothInputDrainPumpFailure.SinkRejected, fault, null));
        Assert.AreEqual(0, messages.Count, "The report/lifecycle caller must not write the log.");
        Assert.AreEqual(1, queued.Count);
        queued[0]();
        Assert.AreEqual(1, messages.Count);
        StringAssert.Contains(messages[0], "runtime=23, end=SinkFailure, pump=SinkRejected");
        StringAssert.Contains(messages[0], nameof(InvalidOperationException));
        Assert.IsFalse(messages[0].Contains("private controller", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow((int)Switch2BluetoothInputEndReason.Disconnected, (int)Switch2BluetoothInputDrainPumpFailure.None, true)]
    [DataRow((int)Switch2BluetoothInputEndReason.QueueOverflow, (int)Switch2BluetoothInputDrainPumpFailure.None, true)]
    [DataRow((int)Switch2BluetoothInputEndReason.SinkFailure, (int)Switch2BluetoothInputDrainPumpFailure.SinkRejected, true)]
    [DataRow((int)Switch2BluetoothInputEndReason.Stopped, (int)Switch2BluetoothInputDrainPumpFailure.SinkRejected, true)]
    [DataRow((int)Switch2BluetoothInputEndReason.Stopped, (int)Switch2BluetoothInputDrainPumpFailure.None, false)]
    public void OnlyUnexpectedLifecycleEndsProduceOneDeferredDiagnostic(
        int reasonCode, int failureCode, bool expected)
    {
        var reason = (Switch2BluetoothInputEndReason)reasonCode;
        var failure = (Switch2BluetoothInputDrainPumpFailure)failureCode;
        var queued = new List<Action>();
        var messages = new List<string>();
        var observer = new Switch2InputFailureDiagnostics(messages.Add, queued.Add);
        Assert.AreEqual(expected, observer.TryReport("joined Joy-Con Right", 23, reason, failure, null, null));
        Assert.IsFalse(observer.TryReport("joined Joy-Con Right", 23, reason, failure, null, null));
        Assert.AreEqual(expected ? 1 : 0, queued.Count);
        Assert.AreEqual(0, messages.Count);
        if (!expected) return;
        queued[0]();
        Assert.AreEqual(reason == Switch2BluetoothInputEndReason.SinkFailure,
            messages[0].Contains("sink evidence unavailable", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void StandaloneSubscriberFailureSurvivesRecoveryAndTerminalCleanup(Switch2ControllerModel model)
    {
        var descriptor = Descriptor(model, 11, 12);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(model, 11, 12,
            out var runtime, out _));
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreate(descriptor, runtime, 100,
            out var sink, out _));
        DS4Device.ReportHandler<EventArgs> reject = (_, _) => throw new InvalidOperationException("private message");
        runtime.Report += reject;
        runtime.StartUpdate();
        var frame = Frame(descriptor, 1, 100);
        Assert.ThrowsException<InvalidOperationException>(() => sink.PublishJoyCon(frame));
        var first = sink.FirstInputFailure;
        Assert.IsNotNull(first);
        StringAssert.Contains(first.Cause, "RuntimeSubscriberRejected");
        StringAssert.Contains(runtime.FirstPublicationFailure.Describe(), "ReportSubscriber");
        runtime.Report -= reject;
        runtime.Report += (_, _) => { };
        sink.PublishJoyCon(frame);
        Assert.AreEqual(Switch2BluetoothRuntimeSinkFailure.None, sink.LastFailure);
        sink.LoseJoyConHalf(model == Switch2ControllerModel.JoyCon2Left ? Switch2StickSide.Left : Switch2StickSide.Right,
            11, 12, Switch2BluetoothInputEndReason.SinkFailure);
        Assert.IsTrue(runtime.TryPublishTerminalNeutral());
        Assert.AreSame(first, sink.FirstInputFailure);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, runtime.FirstPublicationFailure.ExceptionType);
    }

    [TestMethod]
    public void JoinedSubscriberFailureSurvivesHalfLossThatResetsOrdinaryStatus()
    {
        var left = Descriptor(Switch2ControllerModel.JoyCon2Left, 11, 12);
        var right = Descriptor(Switch2ControllerModel.JoyCon2Right, 13, 14);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateJoinedJoyCon(21, 22, 11, 12, 13, 14,
            out var runtime, out _));
        Assert.IsTrue(Switch2JoyConJoinedRuntimeInputSink.TryCreateBound(22, left, right,
            runtime, new Switch2JoyConPairPolicy(1_000), 100,
            Switch2RuntimeTerminalScheduler.Instance, out var sink, out _, out _));
        DS4Device.ReportHandler<EventArgs> reject = (_, _) => throw new InvalidOperationException("private message");
        runtime.Report += reject;
        runtime.StartUpdate();
        sink.PublishJoyCon(Frame(left, 1, 100));
        var rightFrame = Frame(right, 1, 101);
        Assert.ThrowsException<InvalidOperationException>(() => sink.PublishJoyCon(rightFrame));
        var first = sink.FirstInputFailure;
        Assert.IsNotNull(first);
        Assert.AreEqual(Switch2ControllerModel.JoyCon2Right, first.Model);
        StringAssert.Contains(first.Cause, "RuntimeSubscriberRejected");
        runtime.Report -= reject;
        runtime.Report += (_, _) => { };
        sink.PublishJoyCon(rightFrame);
        sink.LoseJoyConHalf(Switch2StickSide.Right, 13, 14, Switch2BluetoothInputEndReason.SinkFailure);
        Assert.IsTrue(runtime.TryPublishTerminalNeutral());
        Assert.AreSame(first, sink.FirstInputFailure);
        StringAssert.Contains(runtime.FirstPublicationFailure.Cause, "ReportSubscriber");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RuntimeCapturesFirstGyroOrQueuedActionFaultWithoutSuppressingLaterReports(bool queuedAction)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(41, 42, Switch2Transport.Usb,
            out var runtime, out _));
        int reports = 0;
        runtime.Report += (_, _) => reports++;
        if (queuedAction) runtime.queueEvent(() => throw new ArgumentException("private message"));
        else runtime.SixAxis.SixAccelMoved += (_, _) => throw new ArgumentException("private message");
        runtime.StartUpdate();
        Assert.AreEqual(Switch2RuntimePublicationResult.SubscriberRejected,
            runtime.TryPublishProDetailed(Switch2RuntimeInputDeviceTests.CreateProFrame(41, 42, 0)));
        Assert.AreEqual(1, reports);
        var first = runtime.FirstPublicationFailure;
        Assert.IsNotNull(first);
        Assert.AreEqual(queuedAction ? "QueuedAction" : "GyroObserver", first.Cause);
        Assert.AreEqual(typeof(ArgumentException).FullName, first.ExceptionType);
        runtime.queueEvent(() => throw new InvalidOperationException("different"));
        _ = runtime.TryPublishProDetailed(Switch2RuntimeInputDeviceTests.CreateProFrame(41, 42, 0, counter: 2, timestamp: 2));
        Assert.AreSame(first, runtime.FirstPublicationFailure);
        Assert.IsTrue(runtime.TryPublishTerminalNeutral());
        Assert.AreSame(first, runtime.FirstPublicationFailure);
    }

    private static Switch2InputSessionDescriptor Descriptor(Switch2ControllerModel model, ulong device, ulong transport)
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid, Switch2GattProperty.Read | Switch2GattProperty.Notify,
            model, out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity, device, transport, 1_000_000, out var descriptor));
        return descriptor;
    }

    private static Switch2CanonicalInputFrame Frame(Switch2InputSessionDescriptor descriptor, uint counter, long timestamp)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(descriptor.Identity.Model,
            descriptor.DeviceGeneration, out var calibration));
        var session = new Switch2InputSession(descriptor, calibration);
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
        body[0x0B] = body[0x0E] = 0x08;
        body[0x0C] = body[0x0F] = 0x80;
        Assert.IsTrue(session.TryProcess(descriptor, body, timestamp, out var frame, out _));
        return frame;
    }
}
