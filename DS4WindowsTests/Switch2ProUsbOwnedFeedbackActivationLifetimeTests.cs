using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2ProUsbOwnedFeedbackActivationLifetimeTests
{
    private const ulong DeviceGeneration = 401;
    private const ulong TransportGeneration = 409;

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExpiredXboxFeedbackWithNewTuningConsumesOrderingWithoutActuation(bool previouslyActuated)
    {
        var composition = CreateCommitted(ContainerA);
        Assert.IsTrue(composition.Feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            if (previouslyActuated)
                Assert.IsTrue(session.TryPublish(XboxWire(1, session.OwnershipEpoch, 20_000, 0, 0, 0)));
            Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
            Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
                ControllerFeedbackSource.XboxOneVirtualDevice, ControllerFeedbackCommand.Neutral,
                ControllerFeedbackActuators.All, 0, 0, 0, 0, 2,
                DeviceGeneration, TransportGeneration, session.OwnershipEpoch,
                now - 500_000, 250_000, out var frame));
            byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
            Assert.IsTrue(frame.TryWriteTo(wire));
            Assert.IsTrue(session.TryPublish(wire, mapImpulseTriggersToHdRumble: true));
            Assert.AreEqual(previouslyActuated ? 2 : 0, composition.Lease.ReportCount);
            if (previouslyActuated) AssertReport(composition.Lease.ReportAt(1), 1, true);
            Assert.IsFalse(session.TryPublish(wire));
            Assert.IsTrue(session.TryPublish(XboxWire(3, session.OwnershipEpoch, 20_000, 0, 0, 0),
                mapImpulseTriggersToHdRumble: true));
        }
        finally { _ = session.TryRetire(); }
    }

    [DataTestMethod]
    [DataRow(true, 0)]
    [DataRow(true, 150)]
    [DataRow(false, 0)]
    public void PublicationResamplesPolicyBeforeOwnedUsbWrite(bool disableAll, int delay)
    {
        var composition = CreateCommitted(ContainerA);
        Assert.IsTrue(composition.Feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            Assert.IsTrue(session.TryPublish(XboxWire(1, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000), mapImpulseTriggersToHdRumble: true));
            Assert.IsTrue(session.TryPublish(XboxWire(2, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000), rumbleDelayMilliseconds: 150));
            byte[] wire = XboxWire(3, session.OwnershipEpoch, 20_000, 30_000, 40_000, 50_000);
            Assert.IsTrue(ControllerFeedbackFrame.TryReadFrom(wire, out var original));
            int reads = 0;
            Assert.IsTrue(session.TryPublish(wire, mapImpulseTriggersToHdRumble: true,
                rumbleDelayMilliseconds: delay, readLiveXboxPolicy: () =>
                {
                    reads++;
                    Assert.IsTrue(session.TryCaptureXboxPolicyRevision(out ulong revision));
                    Assert.AreEqual(3UL, revision, "Expose this publication before reading live policy.");
                    return new(!disableAll, false);
                }));
            Assert.AreEqual(1, reads);
            Assert.AreEqual(2, composition.Lease.ReportCount,
                "Disabling output must bypass profile delay and cancel previously queued effects.");
            Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(composition.Lease.ReportAt(1),
                out byte counter, out var left, out var right, out _));
            Assert.AreEqual((byte)1, counter);
            var expected = Switch2HdRumbleFeedbackTranslator.CreateCompatibilityGroup(
                disableAll ? (ushort)0 : (ushort)20_000, disableAll ? (ushort)0 : (ushort)30_000);
            Assert.AreEqual(expected, left, "Impulse disable must preserve body feedback only.");
            Assert.AreEqual(expected, right);
            var ingress = (ControllerFeedbackIngress)typeof(Switch2VirtualFeedbackSession)
                .GetField("ingress", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .GetValue(session);
            Assert.IsTrue(ingress.TryReadPublishedFrame(out var published));
            Assert.AreEqual(original.Sequence, published.Sequence);
            Assert.AreEqual(original.TimestampMicroseconds, published.TimestampMicroseconds);
            Assert.AreEqual(original.TimeToLiveMicroseconds, published.TimeToLiveMicroseconds);
            Assert.AreEqual(original.OwnershipEpoch, published.OwnershipEpoch);
            Assert.AreEqual(disableAll ? ControllerFeedbackCommand.Neutral : ControllerFeedbackCommand.Apply,
                published.Command);
            Thread.Sleep(180);
            Assert.AreEqual(2, composition.Lease.ReportCount, "The older delayed effect must not return.");
        }
        finally { _ = session.TryRetire(); }
    }

    [TestMethod]
    public void FailedLivePolicyReadRejectsApplyButCannotBlockTerminalStop()
    {
        var composition = CreateCommitted(ContainerA);
        Assert.IsTrue(composition.Feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        int reads = 0;
        Switch2XboxFeedbackPolicy FailRead()
        {
            reads++;
            throw new InvalidOperationException("Synthetic profile read failure");
        }
        try
        {
            Assert.IsTrue(session.TryPublish(XboxWire(1, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000)));
            Assert.IsFalse(session.TryPublish(XboxWire(2, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000), readLiveXboxPolicy: FailRead));
            Assert.AreEqual(1, reads);
            Assert.AreEqual(1, composition.Lease.ReportCount, "Failure cannot acknowledge a physical write.");
            Assert.IsFalse(session.TryPublish(new byte[3], readLiveXboxPolicy: FailRead));
            Assert.IsFalse(session.TryPublish(XboxWire(1, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000), readLiveXboxPolicy: FailRead));
            Assert.IsFalse(session.TryPublish(XboxWire(2, session.OwnershipEpoch + 1,
                20_000, 30_000, 40_000, 50_000), readLiveXboxPolicy: FailRead));
            Assert.IsTrue(session.TryPublish(XboxWire(3, session.OwnershipEpoch,
                0, 0, 0, 0, ControllerFeedbackCommand.Stop),
                rumbleDelayMilliseconds: 150, readLiveXboxPolicy: FailRead));
            Assert.AreEqual(1, reads, "Rejected frames and terminal Stop must not consult profile policy.");
            AssertReport(composition.Lease.ReportAt(1), 1, true);
        }
        finally { _ = session.TryRetire(); }
    }

    [TestMethod]
    public void XboxPolicyCaptureDoesNotWaitBehindPhysicalOutput()
    {
        var composition = CreateCommitted(ContainerA);
        Assert.IsTrue(composition.Feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        composition.Lease.BlockWrite = true;
        var publish = Task.Run(() => session.TryPublish(XboxWire(1,
            session.OwnershipEpoch, 20_000, 30_000, 0, 0)));
        Task<bool> capture = null;
        try
        {
            Assert.IsTrue(composition.Lease.WriteEntered.Wait(1_000));
            capture = Task.Run(() => session.TryCaptureXboxPolicyRevision(out _));
            Assert.IsTrue(capture.Wait(500),
                "CheckProfileOptions may run on the input queue; capturing a policy wake must not wait for output I/O.");
            Assert.IsTrue(capture.Result);
        }
        finally
        {
            composition.Lease.BlockWrite = false;
            composition.Lease.AllowWrite.Set();
            Assert.IsTrue(publish.Wait(1_000));
            if (capture != null) Assert.IsTrue(capture.Wait(1_000));
            _ = session.TryRetire();
        }
    }

    [TestMethod]
    public void XboxPolicyRefreshCompletesRetainedUsbWriteBeforeZeroPresentation()
    {
        var composition = CreateCommitted(ContainerA);
        Assert.IsTrue(composition.Feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            composition.Lease.WriteModes.Enqueue(OutputWriteMode.Retained);
            Assert.IsTrue(session.TryPublish(XboxWire(1, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000), mapImpulseTriggersToHdRumble: true));
            byte[] first = composition.Lease.ReportAt(0);
            Assert.IsTrue(session.TryRefreshXboxOutputPolicy(new(false, false)));
            Assert.IsTrue(composition.Lease.RetirementCount > 0);
            Assert.IsTrue(composition.Lease.ReportCount >= 2);
            for (int i = 1; i < composition.Lease.ReportCount - 1; i++)
                CollectionAssert.AreEqual(first, composition.Lease.ReportAt(i));
            AssertCompatibilityNeutralReport(composition.Lease.Reports[^1], 1);
        }
        finally { _ = session.TryRetire(); }
    }

    [TestMethod]
    public void XboxPolicyRefreshAfterOriginalExpiryCannotRenewEffect()
    {
        var composition = CreateCommitted(ContainerA);
        Assert.IsTrue(composition.Feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            Assert.IsTrue(session.TryPublish(XboxWire(1, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000), mapImpulseTriggersToHdRumble: true));
            Thread.Sleep(275);
            Assert.IsTrue(session.TryRefreshXboxOutputPolicy(new(true, true)));
            AssertReport(composition.Lease.Reports[^1], 1, true);
            int stoppedCount = composition.Lease.ReportCount;
            Assert.IsTrue(session.TryRefreshXboxOutputPolicy(new(true, true)));
            Assert.AreEqual(stoppedCount, composition.Lease.ReportCount);
        }
        finally { _ = session.TryRetire(); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LiveXboxPolicyDisablesOwnedUsbEffectWithoutBrokerSuccessor(bool disableAll)
    {
        var composition = CreateCommitted(ContainerA);
        Assert.IsTrue(composition.Feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            Assert.IsTrue(session.TryPublish(XboxWire(1, session.OwnershipEpoch,
                0, 0, 40_000, 50_000), mapImpulseTriggersToHdRumble: true));
            Assert.IsTrue(session.TryCaptureXboxPolicyRevision(out ulong revision));
            Assert.IsTrue(session.TryRefreshXboxOutputPolicy(new(!disableAll, false), revision));
            Assert.AreEqual(2, composition.Lease.ReportCount);
            AssertCompatibilityNeutralReport(composition.Lease.ReportAt(1), 1);
            Assert.IsTrue(session.TryRefreshXboxOutputPolicy(new(true, true), revision));
            AssertCompatibilityNeutralReport(composition.Lease.ReportAt(2), 2);
            Assert.IsTrue(session.TryPublish(XboxWire(2, session.OwnershipEpoch,
                0, 0, 40_000, 50_000), mapImpulseTriggersToHdRumble: true));
            AssertReport(composition.Lease.Reports[^1], 3, false);
        }
        finally { _ = session.TryRetire(); }
    }
    private static readonly Guid ContainerA =
        Guid.Parse("0284168D-A901-4C3B-B1AB-43DA1A06A54E");
    private static readonly Guid ContainerB =
        Guid.Parse("20A66A52-574E-4AF0-B484-8D82939DD511");

    [DataTestMethod]
    [DataRow(250, false)]
    [DataRow(9_999, false)]
    [DataRow(9_999, true)]
    public void TerminalBrokerStopBypassesPresentationDelay(int delay, bool invalidTuning)
    {
        Composition composition = CreateCommitted(ContainerA);
        Assert.IsTrue(composition.Feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        try
        {
            Assert.IsTrue(session.TryPublish(XboxWire(1, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000), mapImpulseTriggersToHdRumble: true));
            Assert.IsTrue(session.TryPublish(XboxWire(2, session.OwnershipEpoch,
                40_000, 40_000, 40_000, 40_000), rumbleDelayMilliseconds: delay));
            int beforeStop = composition.Lease.ReportCount;
            Assert.IsTrue(session.TryPublish(XboxWire(3, session.OwnershipEpoch,
                0, 0, 0, 0, ControllerFeedbackCommand.Stop),
                bodyStrengthPercent: invalidTuning ? -1 : 100,
                rumbleDelayMilliseconds: delay));
            Assert.AreEqual(beforeStop + 1, composition.Lease.ReportCount,
                "Terminal Stop must reach the owned USB writer synchronously, not enter a profile delay queue.");
            AssertReport(composition.Lease.ReportAt(beforeStop),
                expectedCounter: 1, expectNeutral: true);
            Assert.IsFalse(session.TryPublish(XboxWire(4, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000)));
            Assert.IsFalse(session.TryPublish(XboxWire(4, session.OwnershipEpoch,
                20_000, 30_000, 40_000, 50_000), rumbleDelayMilliseconds: delay));
            Thread.Sleep(300);
            Assert.AreEqual(beforeStop + 1, composition.Lease.ReportCount,
                "A canceled delayed Apply must not survive terminal Stop.");
        }
        finally
        {
            _ = session.TryRetire();
        }
    }

    [TestMethod]
    public void DefiniteDisconnectRetiresActiveFeedbackWithoutClaimingNeutralDelivery()
    {
        Composition composition = CreateCommitted(ContainerA);
        Assert.IsTrue(composition.Feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        Assert.IsTrue(session.TryPublish(new ControllerFeedbackActuatorState(10_000, 20_000, 0, 0)));
        int writesBeforeDisconnect = composition.Lease.WriteCount;
        composition.Lease.DeviceDisconnected = true;

        var result = composition.Feedback.TryNeutralizeAndQuiesce(composition.Authority, 100);
        Assert.AreEqual(Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ExactDisconnectedAndQuiescent, result.Outcome);
        Assert.IsTrue(composition.Feedback.AuthenticatesQuiescenceResult(composition.Authority, result));
        Assert.AreEqual(writesBeforeDisconnect, composition.Lease.WriteCount);
        Assert.IsFalse(session.TryPublish(new ControllerFeedbackActuatorState(10_000, 0, 0, 0)));
        Assert.IsTrue(session.TryRetire());
        Assert.IsFalse(composition.Feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out _));
        Assert.AreEqual(result.Outcome,
            composition.Feedback.TryNeutralizeAndQuiesce(composition.Authority, 0).Outcome);
        Composition foreign = CreateCommitted(ContainerB);
        Assert.IsFalse(foreign.Feedback.AuthenticatesQuiescenceResult(foreign.Authority, result));
    }

    [TestMethod]
    public void DefiniteDisconnectStillWaitsForExactRetainedOutput()
    {
        Composition composition = CreateCommitted(ContainerA);
        composition.Lease.WriteModes.Enqueue(OutputWriteMode.Retained);
        Assert.IsTrue(composition.Feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        Assert.IsTrue(session.TryPublish(new ControllerFeedbackActuatorState(10_000, 20_000, 0, 0)));
        composition.Lease.DeviceDisconnected = true;
        composition.Lease.RetirementModes.Enqueue(OutputRetirementMode.Retained);
        var pending = composition.Feedback.TryNeutralizeAndQuiesce(composition.Authority, 100);
        Assert.AreEqual(Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ProvenIncomplete, pending.Outcome);
        Assert.IsFalse(composition.Feedback.AuthenticatesQuiescenceResult(composition.Authority, pending));
        composition.Lease.RetirementModes.Enqueue(OutputRetirementMode.Quiescent);
        var retired = composition.Feedback.TryNeutralizeAndQuiesce(composition.Authority, 100);
        Assert.AreEqual(Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ExactDisconnectedAndQuiescent, retired.Outcome);
        Assert.AreEqual(1, composition.Lease.WriteCount);
    }

    [TestMethod]
    public void ExpiredPolicyRefreshCannotDriveBodyOrRichUsbActuators()
    {
        Composition composition = CreateCommitted(ContainerA);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration, Switch2Transport.Usb,
            out var runtime, out _));
        Assert.IsTrue(runtime.TryAttachUsbFeedbackLifetime(composition.Authority,
            composition.Feedback));
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.DualSenseVirtualDevice,
            DeviceGeneration, TransportGeneration, out var session));
        try
        {
            var body = new ControllerFeedbackActuatorState(20_000, 30_000, 0, 0);
            var group = Switch2HdRumbleFeedbackTranslator.CreateCompatibilityGroup(20_000, 30_000);
            Assert.IsTrue(session.TryPublish(body));
            AssertReport(composition.Lease.Reports[^1], 0, false);
            Assert.IsTrue(session.TryPublish(body, expiresAtMicroseconds: 1));
            AssertCompatibilityNeutralReport(composition.Lease.Reports[^1], 1);
            Assert.IsTrue(session.TryPublish(body));
            Assert.IsTrue(session.TryPublishSourcePreserved(body,
                Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand,
                group, group, expiresAtMicroseconds: 1));
            AssertCompatibilityNeutralReport(composition.Lease.Reports[^1], 3);
        }
        finally { Assert.IsTrue(session.TryRetire()); }
    }

    [TestMethod]
    public void FactoryAdoptsOneNarrowNeverStartedOutputAndPrepareStaysSealed()
    {
        Composition composition = CreateComposition(ContainerA);

        Assert.AreEqual(0, composition.Lease.WriteCount);
        Assert.IsFalse(composition.Feedback.TryCreateLane(
            ControllerFeedbackPublicationOrigin.NativeGame,
            ControllerFeedbackSource.XboxOneVirtualDevice, 1, 250_000,
            100_000, out _));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.None,
            composition.Feedback.TryPumpOnce(1, out _));
        Assert.AreEqual(0, composition.Lease.WriteCount);

        Assert.IsTrue(composition.Feedback.TryTakeDormantQuiescenceProof(
            composition.Authority, out var proof));
        var copiedProof = proof;
        Switch2ProUsbOwnedFeedbackActivationResult prepare =
            composition.Feedback.TryPrepareActivation(composition.Authority,
                proof, 0);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            prepare.Outcome);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationState.Prepared,
            composition.Feedback.ActivationState);
        Assert.AreEqual(0, composition.Lease.WriteCount);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected,
            composition.Feedback.TryPrepareActivation(composition.Authority,
                copiedProof, 0).Outcome,
            "Every copied dormant proof is consumed at the first exact prepare.");
        Assert.IsFalse(composition.Feedback.TryCreateLane(
            ControllerFeedbackPublicationOrigin.NativeGame,
            ControllerFeedbackSource.XboxOneVirtualDevice, 1, 250_000,
            100_000, out _));

        var copiedCredential = prepare.Credential;
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            composition.Feedback.TryCommitPrepared(prepare.Credential, 0).
                Outcome);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected,
            composition.Feedback.TryCommitPrepared(copiedCredential, 0).
                Outcome);
        Assert.IsTrue(composition.Feedback.TryCreateLane(
            ControllerFeedbackPublicationOrigin.NativeGame,
            ControllerFeedbackSource.XboxOneVirtualDevice, 1, 250_000,
            100_000, out _),
            "Commit is the sole lane/PumpOnce admission point.");
        Assert.AreEqual(0, composition.Lease.WriteCount);
        Assert.AreEqual(1, composition.Lease.AdoptionCount);
        Assert.AreEqual(0, composition.Lease.DirectWriteCount);
    }

    [TestMethod]
    public void RuntimeBoundXboxSessionUsesOwnedUsbWriterAndTerminalStop()
    {
        Composition composition = CreateComposition(ContainerA);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration, Switch2Transport.Usb,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachUsbFeedbackLifetime(
            composition.Authority, composition.Feedback));
        Assert.IsTrue(runtime.TryGetFeedbackBinding(out ulong deviceGeneration,
            out ulong transportGeneration));
        Assert.AreEqual(DeviceGeneration, deviceGeneration);
        Assert.AreEqual(TransportGeneration, transportGeneration);
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            deviceGeneration, transportGeneration,
            out Switch2VirtualFeedbackSession session));

        byte[] apply = XboxWire(sequence: 1, session.OwnershipEpoch,
            bodyLow: 30_000, bodyHigh: 20_000,
            leftTrigger: 10_000, rightTrigger: 5_000);
        Assert.IsFalse(session.TryPublish(apply),
            "Profile staging may bind feedback before commit, but USB output must remain sealed.");

        Switch2ProUsbOwnedFeedbackPrepareCredential credential =
            Prepare(composition);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            composition.Feedback.TryCommitPrepared(credential, 0).Outcome);
        Assert.IsTrue(session.TryPublish(apply));
        Assert.AreEqual(1, composition.Lease.WriteCount);
        AssertReport(composition.Lease.Reports[0], expectedCounter: 0,
            expectNeutral: false);

        composition.Lease.AcceptPlayerLedCommands = true;
        Assert.IsTrue(session.TryRequestPlayerLedMask(0x07));
        Assert.AreEqual(1, composition.Lease.PlayerLedCommands.Count);
        Assert.AreEqual(Switch2PlayerLedCommand.Player3Only,
            composition.Lease.PlayerLedCommands[0]);
        Assert.IsFalse(session.TryRequestPlayerLedMask(0x09),
            "USB must not approximate a player pattern absent from its closed command contract.");

        Assert.IsTrue(session.TryRetire());
        Assert.AreEqual(2, composition.Lease.WriteCount);
        AssertReport(composition.Lease.Reports[1], expectedCounter: 1,
            expectNeutral: true);
        Assert.IsFalse(session.TryPublish(XboxWire(sequence: 2,
            session.OwnershipEpoch, bodyLow: 1, bodyHigh: 1,
            leftTrigger: 1, rightTrigger: 1)));
    }

    [TestMethod]
    public void ConfiguredDelayDefersButPreservesOwnedUsbDelivery()
    {
        Composition composition = CreateCommitted(ContainerA);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration, Switch2Transport.Usb,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachUsbFeedbackLifetime(
            composition.Authority, composition.Feedback));
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));

        Assert.IsTrue(session.TryPublish(XboxWire(sequence: 1,
            session.OwnershipEpoch, bodyLow: 30_000, bodyHigh: 20_000,
            leftTrigger: 10_000, rightTrigger: 5_000),
            rumbleDelayMilliseconds: 60, profileRevision: 7));
        Assert.AreEqual(0, composition.Lease.WriteCount,
            "A configured delay must not synchronously reach USB output.");

        ScriptedOwnedLease lease = composition.Lease;
        Assert.IsTrue(SpinWait.SpinUntil(
            () => Volatile.Read(ref lease.WriteCount) == 1, 2_000));
        Assert.AreEqual(1, lease.Reports.Count);
        AssertReport(lease.Reports[0], expectedCounter: 0,
            expectNeutral: false);

        Assert.IsTrue(session.TryRetire());
        Assert.AreEqual(2, lease.WriteCount);
        AssertReport(lease.Reports[1], expectedCounter: 1,
            expectNeutral: true);
    }

    [TestMethod]
    [DoNotParallelize]
    public void XboxImpulseStopTraversesOwnedUsbReleaseEnvelope()
    {
        Composition composition = CreateComposition(ContainerA);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration, Switch2Transport.Usb,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachUsbFeedbackLifetime(
            composition.Authority, composition.Feedback));
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));
        Switch2ProUsbOwnedFeedbackPrepareCredential credential =
            Prepare(composition);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            composition.Feedback.TryCommitPrepared(credential, 0).Outcome);

        Assert.IsTrue(session.TryPublish(XboxWire(sequence: 1,
            session.OwnershipEpoch, bodyLow: 0, bodyHigh: 0,
            leftTrigger: ushort.MaxValue, rightTrigger: 0),
            mapImpulseTriggersToHdRumble: true));
        Assert.IsTrue(session.TryPublish(XboxWire(sequence: 2,
            session.OwnershipEpoch, bodyLow: 0, bodyHigh: 0,
            leftTrigger: 0, rightTrigger: 0),
            mapImpulseTriggersToHdRumble: true));
        ScriptedOwnedLease lease = composition.Lease;
        Assert.AreEqual(2, lease.ReportCount);
        DecodeSidedAmplitude(lease.ReportAt(0), out ushort initialLeft,
            out ushort initialRight);
        Assert.IsTrue(initialLeft > 0);
        Assert.AreEqual((ushort)0, initialRight);
        DecodeSidedAmplitude(lease.ReportAt(1), out ushort releaseStartLeft,
            out ushort releaseStartRight);
        Assert.AreEqual(initialLeft, releaseStartLeft);
        Assert.AreEqual((ushort)0, releaseStartRight);

        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            int count = lease.ReportCount;
            if (count < 4)
            {
                return false;
            }
            DecodeSidedAmplitude(lease.ReportAt(count - 1),
                out ushort left, out ushort right);
            return left == 0 && right == 0;
        }, 2_000));
        int finalCount = lease.ReportCount;
        bool sawIntermediate = false;
        for (int index = 2; index < finalCount - 1; index++)
        {
            DecodeSidedAmplitude(lease.ReportAt(index), out ushort left,
                out ushort right);
            sawIntermediate |= left > 0 && left < initialLeft;
            Assert.AreEqual((ushort)0, right);
        }
        Assert.IsTrue(sawIntermediate);
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void LiveProfileSelectionControlsUsbImpulseTriggerSidedness()
    {
        Composition composition = CreateComposition(ContainerA);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration, Switch2Transport.Usb,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachUsbFeedbackLifetime(
            composition.Authority, composition.Feedback));
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));

        Switch2ProUsbOwnedFeedbackPrepareCredential credential =
            Prepare(composition);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            composition.Feedback.TryCommitPrepared(credential, 0).Outcome);

        Assert.IsTrue(session.TryPublish(XboxWire(sequence: 1,
            session.OwnershipEpoch, bodyLow: 0, bodyHigh: 0,
            leftTrigger: ushort.MaxValue, rightTrigger: 0),
            mapImpulseTriggersToHdRumble: false));
        Assert.IsTrue(session.TryPublish(XboxWire(sequence: 2,
            session.OwnershipEpoch, bodyLow: 0, bodyHigh: 0,
            leftTrigger: ushort.MaxValue, rightTrigger: 0),
            mapImpulseTriggersToHdRumble: true,
            dynamicImpulseFrequency: false, fixedImpulseFrequencyLevel: 1,
            impulseStrengthLevel: 1));
        Assert.IsTrue(session.TryPublish(XboxWire(sequence: 3,
            session.OwnershipEpoch, bodyLow: 0, bodyHigh: 0,
            leftTrigger: 0, rightTrigger: ushort.MaxValue),
            mapImpulseTriggersToHdRumble: true,
            dynamicImpulseFrequency: true, fixedImpulseFrequencyLevel: 10,
            impulseStrengthLevel: 10));
        Assert.IsTrue(session.TryPublish(XboxWire(sequence: 4,
            session.OwnershipEpoch, bodyLow: 20_000, bodyHigh: 30_000,
            leftTrigger: 0, rightTrigger: 0),
            bodyStrengthPercent: 100, xboxBodyCarrierMode: true,
            xboxBodyFrequencyLevel: 6));

        Assert.AreEqual(4, composition.Lease.Reports.Count);
        AssertImpulseSidedness(composition.Lease.Reports[0],
            expectLeft: false, expectRight: false);
        AssertImpulseSidedness(composition.Lease.Reports[1],
            expectLeft: true, expectRight: false);
        AssertImpulseSidedness(composition.Lease.Reports[2],
            expectLeft: true, expectRight: true);
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(
            composition.Lease.Reports[1], out _,
            out Switch2HdRumbleGroup weakLeft, out _, out _));
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(
            composition.Lease.Reports[2], out _, out _,
            out Switch2HdRumbleGroup strongRight, out _));
        Assert.AreEqual((ushort)300,
            weakLeft.First.Oscillator0ControlCode);
        Assert.AreEqual((ushort)481,
            strongRight.First.Oscillator0ControlCode);
        Assert.IsTrue(strongRight.First.Oscillator0AmplitudeCode >
            weakLeft.First.Oscillator0AmplitudeCode);
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(
            composition.Lease.Reports[3], out _,
            out Switch2HdRumbleGroup xboxBodyLeft, out _, out _));
        Assert.AreEqual((ushort)300,
            xboxBodyLeft.First.Oscillator0ControlCode);
        Assert.AreEqual((ushort)225,
            xboxBodyLeft.First.Oscillator1ControlCode);
    }

    [TestMethod]
    public void DualSenseAdaptiveApproximationTraversesOwnedUsbWriter()
    {
        Composition composition = CreateComposition(ContainerA);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration, Switch2Transport.Usb,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachUsbFeedbackLifetime(
            composition.Authority, composition.Feedback));
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.DualSenseVirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));
        Switch2ProUsbOwnedFeedbackPrepareCredential credential =
            Prepare(composition);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            composition.Feedback.TryCommitPrepared(credential, 0).Outcome);

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
            Switch2HdRumbleFeedbackFidelity.
                DualSenseAdaptiveTriggerApproximation,
            left, right));
        Assert.AreEqual(1, composition.Lease.Reports.Count);
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(
            composition.Lease.Reports[0], out _,
            out Switch2HdRumbleGroup deliveredLeft,
            out Switch2HdRumbleGroup deliveredRight, out _));
        Assert.AreEqual(left, deliveredLeft);
        Assert.AreEqual(right, deliveredRight);
        Assert.IsTrue(session.TryPublishSourcePreserved(
            new ControllerFeedbackActuatorState(1, 0, 0, 0),
            Switch2HdRumbleFeedbackFidelity.
                DualSenseAdaptiveTriggerApproximation,
            right, left));
        Assert.AreEqual(2, composition.Lease.Reports.Count,
            "A changed rich payload must not be deduplicated by an unchanged canonical marker.");
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(
            composition.Lease.Reports[1], out _, out deliveredLeft,
            out deliveredRight, out _));
        Assert.AreEqual(right, deliveredLeft);
        Assert.AreEqual(left, deliveredRight);
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void SourcePreservedBodyStrengthKeepsUsbCarrierAndSliceIdentity()
    {
        Composition composition = CreateComposition(ContainerA);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration, Switch2Transport.Usb,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachUsbFeedbackLifetime(
            composition.Authority, composition.Feedback));
        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.Switch2VirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession session));
        Switch2ProUsbOwnedFeedbackPrepareCredential credential =
            Prepare(composition);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            composition.Feedback.TryCommitPrepared(credential, 0).Outcome);

        var left = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(101, 100, 201, 200),
            new Switch2HdRumbleSubframe(102, 300, 202, 400),
            new Switch2HdRumbleSubframe(103, 500, 203, 600));
        var right = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(111, 700, 211, 800),
            new Switch2HdRumbleSubframe(112, 900, 212, 1_000),
            new Switch2HdRumbleSubframe(113, 1_023, 213, 1));

        Assert.IsTrue(session.TryPublishSourcePreserved(
            new ControllerFeedbackActuatorState(1, 0, 0, 0),
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough,
            left, right, bodyStrengthPercent: 50));
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(
            composition.Lease.Reports[0], out _,
            out Switch2HdRumbleGroup deliveredLeft,
            out Switch2HdRumbleGroup deliveredRight, out _));

        Assert.AreEqual((ushort)101,
            deliveredLeft.First.Oscillator0ControlCode);
        Assert.AreEqual((ushort)201,
            deliveredLeft.First.Oscillator1ControlCode);
        Assert.AreEqual((ushort)50,
            deliveredLeft.First.Oscillator0AmplitudeCode);
        Assert.AreEqual((ushort)100,
            deliveredLeft.First.Oscillator1AmplitudeCode);
        Assert.AreEqual((ushort)102,
            deliveredLeft.Second.Oscillator0ControlCode);
        Assert.AreEqual((ushort)150,
            deliveredLeft.Second.Oscillator0AmplitudeCode);
        Assert.AreEqual((ushort)113,
            deliveredRight.Third.Oscillator0ControlCode);
        Assert.AreEqual((ushort)512,
            deliveredRight.Third.Oscillator0AmplitudeCode);
        Assert.AreEqual((ushort)1,
            deliveredRight.Third.Oscillator1AmplitudeCode);
        Assert.IsTrue(session.TryPublishSourcePreserved(
            new ControllerFeedbackActuatorState(1, 0, 0, 0),
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough,
            left, right, bodyStrengthPercent: 50,
            xboxBodyCarrierMode: true, xboxBodyFrequencyLevel: 4));
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(
            composition.Lease.Reports[1], out _, out deliveredLeft,
            out deliveredRight, out _));
        Assert.AreEqual((ushort)276,
            deliveredLeft.First.Oscillator0ControlCode);
        Assert.AreEqual((ushort)225,
            deliveredLeft.First.Oscillator1ControlCode);
        Assert.AreEqual((ushort)276,
            deliveredRight.Third.Oscillator0ControlCode);
        Assert.AreEqual((ushort)225,
            deliveredRight.Third.Oscillator1ControlCode);
        Assert.AreEqual((ushort)50,
            deliveredLeft.First.Oscillator0AmplitudeCode);
        Assert.IsFalse(session.TryPublishSourcePreserved(
            new ControllerFeedbackActuatorState(1, 0, 0, 0),
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough,
            left, right, bodyStrengthPercent: 201));
        Assert.AreEqual(2, composition.Lease.Reports.Count);
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void LegacyVirtualOutputsShareOneMonotonicUsbFeedbackLifetime()
    {
        Composition composition = CreateComposition(ContainerA);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration, Switch2Transport.Usb,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachUsbFeedbackLifetime(
            composition.Authority, composition.Feedback));
        Switch2ProUsbOwnedFeedbackPrepareCredential credential =
            Prepare(composition);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            composition.Feedback.TryCommitPrepared(credential, 0).Outcome);

        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.Xbox360VirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession first));
        Assert.AreEqual(1UL, first.OwnershipEpoch);
        Assert.IsTrue(first.TryPublish(new ControllerFeedbackActuatorState(
            15_000, 25_000, 0, 0)));
        Assert.AreEqual(1, composition.Lease.WriteCount);
        Assert.IsTrue(first.TryRetire());
        Assert.AreEqual(2, composition.Lease.WriteCount);

        Assert.IsTrue(runtime.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.DualSenseVirtualDevice,
            DeviceGeneration, TransportGeneration,
            out Switch2VirtualFeedbackSession successor));
        Assert.AreEqual(2UL, successor.OwnershipEpoch);
        Assert.IsTrue(successor.TryPublish(
            new ControllerFeedbackActuatorState(35_000, 5_000, 0, 0)));
        Assert.AreEqual(3, composition.Lease.WriteCount);
        Assert.IsTrue(successor.TryRetire());
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent,
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100).Outcome);
    }

    [TestMethod]
    public void ProfileAndPreviewRumbleShareCanonicalOwnedUsbWriter()
    {
        Composition composition = CreateComposition(ContainerA);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(
            DeviceGeneration, TransportGeneration, Switch2Transport.Usb,
            out Switch2RuntimeInputDevice runtime, out _));
        Assert.IsTrue(runtime.TryAttachUsbFeedbackLifetime(
            composition.Authority, composition.Feedback));
        Switch2ProUsbOwnedFeedbackPrepareCredential credential =
            Prepare(composition);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            composition.Feedback.TryCommitPrepared(credential, 0).Outcome);
        runtime.StartUpdate();

        runtime.setRumble(rightLightFastMotor: 64,
            leftHeavySlowMotor: 128);
        Assert.AreEqual(1, composition.Lease.WriteCount);
        DecodeSustained(composition.Lease.Reports[0],
            out Switch2HdRumbleGroup profileLeft,
            out Switch2HdRumbleGroup profileRight);

        runtime.SetRumblePreview(lightMotorActive: true,
            lightMotorStrength: 200, heavyMotorActive: true,
            heavyMotorStrength: 100);
        Assert.AreEqual(3, composition.Lease.WriteCount,
            "Preview takeover must serialize the old owner's Stop and the new effect through the adopted writer.");
        DecodeSustained(composition.Lease.Reports[2],
            out Switch2HdRumbleGroup previewLeft,
            out Switch2HdRumbleGroup previewRight);
        Assert.AreNotEqual(profileLeft, previewLeft);
        Assert.AreNotEqual(profileRight, previewRight);

        runtime.ClearRumblePreview();
        Assert.AreEqual(5, composition.Lease.WriteCount);
        DecodeSustained(composition.Lease.Reports[4],
            out Switch2HdRumbleGroup restoredLeft,
            out Switch2HdRumbleGroup restoredRight);
        Assert.AreEqual(profileLeft, restoredLeft);
        Assert.AreEqual(profileRight, restoredRight);

        runtime.setRumble(rightLightFastMotor: 0,
            leftHeavySlowMotor: 0);
        Assert.AreEqual(6, composition.Lease.WriteCount);
        AssertReport(composition.Lease.Reports[5], expectedCounter: 5,
            expectNeutral: true);
    }

    [TestMethod]
    public void PreparedAbortIsTerminalSealedAndPerformsNoWrite()
    {
        Composition composition = CreateComposition(ContainerA);
        var credential = Prepare(composition);

        Switch2ProUsbOwnedFeedbackActivationResult abort =
            composition.Feedback.TryAbortPrepared(credential, 0);

        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            abort.Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedFeedbackActivationState.Aborted,
            composition.Feedback.ActivationState);
        Assert.AreEqual(0, composition.Lease.WriteCount,
            "Abort proves the never-committed no-write branch; it does not manufacture a physical report.");
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected,
            composition.Feedback.TryCommitPrepared(credential, 0).Outcome);

        Switch2ProUsbOwnedFeedbackQuiescenceResult terminal =
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 0);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent,
            terminal.Outcome);
        Assert.IsTrue(composition.Feedback.AuthenticatesQuiescenceResult(
            composition.Authority, terminal));
    }

    [TestMethod]
    public void EmptyCommittedLifetimeDeliversCanonicalStopBeforeRetirement()
    {
        Composition composition = CreateCommitted(ContainerA);

        Switch2ProUsbOwnedFeedbackQuiescenceResult result =
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100);

        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent,
            result.Outcome);
        Assert.IsTrue(composition.Feedback.AuthenticatesQuiescenceResult(
            composition.Authority, result));
        Assert.AreEqual(1, composition.Lease.WriteCount,
            "Epoch zero/empty sink retirement is not terminal-neutral proof.");
        Assert.AreEqual(1, composition.Lease.Reports.Count);
        AssertReport(composition.Lease.Reports[0], expectedCounter: 0,
            expectNeutral: true);
        CollectionAssert.AreEqual(new[] { "write.stop" },
            composition.Lease.Events);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationState.NeutralAndQuiescent,
            composition.Feedback.ActivationState);
    }

    [TestMethod]
    public void RetainedExpiredFrameDrainsBeforeExactStopAndPreservesCounter()
    {
        Composition composition = CreateCommitted(ContainerA);
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(
            out ulong now));
        Assert.IsTrue(composition.Feedback.TryCreateLane(
            ControllerFeedbackPublicationOrigin.NativeGame,
            ControllerFeedbackSource.XboxOneVirtualDevice, 7, 250_000,
            100_000, out ControllerFeedbackStateLanePump.Lane lane));
        var state = new ControllerFeedbackActuatorState(0x8000, 0x4000,
            0x2000, 0x1000);
        Assert.IsTrue(lane.TryPublish(state, now));
        composition.Lease.WriteModes.Enqueue(OutputWriteMode.Retained);
        composition.Lease.WriteModes.Enqueue(OutputWriteMode.Complete);
        composition.Lease.RetirementModes.Enqueue(
            OutputRetirementMode.Quiescent);

        Assert.AreEqual(ControllerFeedbackPumpDisposition.RetryPending,
            composition.Feedback.TryPumpOnce(now, out _));
        Assert.AreEqual(1, composition.Lease.WriteCount);
        AssertReport(composition.Lease.Reports[0], expectedCounter: 0,
            expectNeutral: false);

        Thread.Sleep(275);
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(
            out ulong expiredNow));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.RetryPending,
            composition.Feedback.TryPumpOnce(expiredNow, out _));
        Assert.AreEqual(1, composition.Lease.WriteCount,
            "The stale physical retry cannot replace/drain the retained report.");

        Switch2ProUsbOwnedFeedbackQuiescenceResult terminal =
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100);

        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent,
            terminal.Outcome);
        Assert.AreEqual(1, composition.Lease.RetirementCount);
        Assert.AreEqual(2, composition.Lease.WriteCount);
        CollectionAssert.AreEqual(new[]
        {
            "write.frame",
            "retire.frame",
            "write.stop",
        }, composition.Lease.Events,
            "The old exact operation must drain before a different Stop can start.");
        AssertReport(composition.Lease.Reports[1], expectedCounter: 1,
            expectNeutral: true);
        Assert.IsTrue(composition.Feedback.AuthenticatesQuiescenceResult(
            composition.Authority, terminal));
    }

    [TestMethod]
    public void RetainedDrainBudgetIsRetryableAndDoesNotStartStopEarly()
    {
        Composition composition = CreateCommitted(ContainerA,
            operationWaitMilliseconds: 20);
        PublishAndPumpRetained(composition);
        composition.Lease.RetirementModes.Enqueue(
            OutputRetirementMode.Quiescent);
        composition.Lease.WriteModes.Enqueue(OutputWriteMode.Complete);

        Switch2ProUsbOwnedFeedbackQuiescenceResult shortBudget =
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 10);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ProvenIncomplete,
            shortBudget.Outcome);
        Assert.AreEqual(0, composition.Lease.RetirementCount);
        Assert.AreEqual(1, composition.Lease.WriteCount);

        Switch2ProUsbOwnedFeedbackQuiescenceResult complete =
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent,
            complete.Outcome);
        Assert.AreEqual(1, composition.Lease.RetirementCount);
        Assert.AreEqual(2, composition.Lease.WriteCount);
    }

    [TestMethod]
    public void RetainedRetirementItselfCanRemainRetryableWithoutReplacement()
    {
        Composition composition = CreateCommitted(ContainerA);
        PublishAndPumpRetained(composition);
        composition.Lease.RetirementModes.Enqueue(
            OutputRetirementMode.Retained);
        composition.Lease.RetirementModes.Enqueue(
            OutputRetirementMode.Quiescent);
        composition.Lease.WriteModes.Enqueue(OutputWriteMode.Complete);

        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ProvenIncomplete,
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100).Outcome);
        Assert.AreEqual(1, composition.Lease.WriteCount);
        Assert.AreEqual(1, composition.Lease.RetirementCount);

        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent,
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100).Outcome);
        Assert.AreEqual(2, composition.Lease.WriteCount);
        Assert.AreEqual(2, composition.Lease.RetirementCount);
    }

    [TestMethod]
    public void RetainedTerminalStopDrainsThenRetriesIdenticalBytesAndCounter()
    {
        Composition composition = CreateCommitted(ContainerA);
        composition.Lease.WriteModes.Enqueue(OutputWriteMode.Retained);
        composition.Lease.WriteModes.Enqueue(OutputWriteMode.Complete);
        composition.Lease.RetirementModes.Enqueue(
            OutputRetirementMode.Quiescent);

        Switch2ProUsbOwnedFeedbackQuiescenceResult first =
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ProvenIncomplete,
            first.Outcome);
        Assert.AreEqual(1, composition.Lease.WriteCount);
        Assert.AreEqual(0, composition.Lease.RetirementCount);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationState.NeutralizeInProgress,
            composition.Feedback.ActivationState,
            "Draining a Stop is not delivery proof.");
        byte[] retainedStop = composition.Lease.Reports[0];

        Switch2ProUsbOwnedFeedbackQuiescenceResult second =
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent,
            second.Outcome);
        Assert.AreEqual(1, composition.Lease.RetirementCount);
        Assert.AreEqual(2, composition.Lease.WriteCount);
        CollectionAssert.AreEqual(retainedStop, composition.Lease.Reports[1],
            "After exact drain the writer must retry the same Stop bytes/counter, not encode a successor.");
        AssertReport(composition.Lease.Reports[1], expectedCounter: 0,
            expectNeutral: true);
        CollectionAssert.AreEqual(new[]
        {
            "write.stop",
            "retire.frame",
            "write.stop",
        }, composition.Lease.Events);
    }

    [TestMethod]
    public void ZeroBudgetSealsRawLaneBeforeAnyTerminalOutput()
    {
        Composition composition = CreateCommitted(ContainerA,
            operationWaitMilliseconds: 20);
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(
            out ulong now));
        Assert.IsTrue(composition.Feedback.TryCreateLane(
            ControllerFeedbackPublicationOrigin.TestPreview,
            ControllerFeedbackSource.DualShock4VirtualDevice, 3, 250_000,
            100_000, out var lane));
        Assert.IsTrue(lane.TryPublish(default, now));

        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ProvenIncomplete,
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 0).Outcome);
        Assert.AreEqual(0, composition.Lease.WriteCount,
            "Sealing/requesting canonical Stop is non-I/O until the lifetime-owned PumpOnce.");
        Assert.IsFalse(lane.TryPublish(new ControllerFeedbackActuatorState(
            1, 2, 3, 4), now + 1),
            "A raw copied lane cannot cross the publication seal.");

        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent,
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100).Outcome);
        Assert.AreEqual(1, composition.Lease.WriteCount);
        AssertReport(composition.Lease.Reports[0], expectedCounter: 0,
            expectNeutral: true);
    }

    [TestMethod]
    public void PostAdoptionFactoryThrowPublishesRetainedBundleForDisposal()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(ContainerA);
        var lease = new ScriptedOwnedLease(lifetime)
        {
            ThrowAdoptedBound = true,
        };
        Assert.IsTrue(Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
            lifetime, out var bundle, out _));
        Assert.IsTrue(bundle.TryTakeAuthority(out var authority));

        Assert.IsFalse(Switch2ProUsbOwnedFeedbackActivationLifetime.TryCreate(
            bundle, authority, 1, out var feedback, out var failure));
        Assert.IsNull(feedback);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationCreateFailure.
                QuarantineRequired,
            failure.Failure);
        Assert.AreSame(bundle, failure.RetainedBundle);
        Assert.IsTrue(failure.RequiresRetention);
        Assert.AreEqual(1, lease.AdoptionCount);
        Assert.AreEqual(0, lease.WriteCount);

        Assert.IsTrue(bundle.TryGetBoundedOutputLease(authority,
            out ISwitch2ProUsbOwnedCompositeLease retained));
        retained.DisposeQuiesced();
        Assert.AreEqual(1, lease.DisposeCount,
            "Losing the private adoption fence cannot strand whole-lease terminal disposal.");
    }

    [TestMethod]
    public void SameGenerationForeignProofCredentialAndTerminalResultFailClosed()
    {
        Composition first = CreateComposition(ContainerA);
        Composition second = CreateComposition(ContainerB);
        Assert.IsTrue(first.Feedback.TryTakeDormantQuiescenceProof(
            first.Authority, out var firstProof));
        Assert.IsTrue(second.Feedback.TryTakeDormantQuiescenceProof(
            second.Authority, out var secondProof));

        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected,
            first.Feedback.TryPrepareActivation(first.Authority, secondProof,
                0).Outcome);
        Switch2ProUsbOwnedFeedbackActivationResult firstPrepare =
            first.Feedback.TryPrepareActivation(first.Authority, firstProof, 0);
        Switch2ProUsbOwnedFeedbackActivationResult secondPrepare =
            second.Feedback.TryPrepareActivation(second.Authority, secondProof,
                0);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected,
            first.Feedback.TryCommitPrepared(secondPrepare.Credential, 0).
                Outcome);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            first.Feedback.TryCommitPrepared(firstPrepare.Credential, 0).
                Outcome);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            second.Feedback.TryCommitPrepared(secondPrepare.Credential, 0).
                Outcome);

        Switch2ProUsbOwnedFeedbackQuiescenceResult firstTerminal =
            first.Feedback.TryNeutralizeAndQuiesce(first.Authority, 100);
        Assert.IsTrue(first.Feedback.AuthenticatesQuiescenceResult(
            first.Authority, firstTerminal));
        Assert.IsFalse(second.Feedback.AuthenticatesQuiescenceResult(
            second.Authority, firstTerminal),
            "Same numeric generations cannot forge issuer/fence/revision proof.");
    }

    [TestMethod]
    public void StaleQuiescenceRevisionCannotAuthorizeTerminalDisposal()
    {
        Composition composition = CreateCommitted(ContainerA,
            operationWaitMilliseconds: 20);
        Switch2ProUsbOwnedFeedbackQuiescenceResult incomplete =
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 0);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ProvenIncomplete,
            incomplete.Outcome);

        Switch2ProUsbOwnedFeedbackQuiescenceResult terminal =
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100);
        Assert.IsTrue(composition.Feedback.AuthenticatesQuiescenceResult(
            composition.Authority, terminal));
        Assert.IsFalse(composition.Feedback.AuthenticatesQuiescenceResult(
            composition.Authority, incomplete));
        Assert.AreNotEqual(incomplete.StateRevision, terminal.StateRevision);
    }

    [TestMethod]
    public void CommitAndAbortCopiedCredentialHaveExactlyOneWinner()
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            Composition composition = CreateComposition(Guid.NewGuid());
            var credential = Prepare(composition);
            Switch2ProUsbOwnedFeedbackActivationResult commit = default;
            Switch2ProUsbOwnedFeedbackActivationResult abort = default;
            Parallel.Invoke(
                () => commit = composition.Feedback.TryCommitPrepared(
                    credential, 0),
                () => abort = composition.Feedback.TryAbortPrepared(
                    credential, 0));

            int successes = (commit.Outcome ==
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded ? 1 : 0) +
                (abort.Outcome ==
                    Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded ? 1 : 0);
            Assert.AreEqual(1, successes);
        }
    }

    [TestMethod]
    public void ActivePumpAndReentrantNeutralizationAreTypedIncomplete()
    {
        Composition composition = CreateCommitted(ContainerA);
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(
            out ulong now));
        Assert.IsTrue(composition.Feedback.TryCreateLane(
            ControllerFeedbackPublicationOrigin.NativeGame,
            ControllerFeedbackSource.XboxOneVirtualDevice, 1, 250_000,
            100_000, out var lane));
        Assert.IsTrue(lane.TryPublish(new ControllerFeedbackActuatorState(
            100, 200, 300, 400), now));
        composition.Lease.BlockWrite = true;
        Switch2ProUsbOwnedFeedbackQuiescenceResult reentrant = default;
        composition.Lease.DuringWrite = () => reentrant =
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100);

        Task<ControllerFeedbackPumpDisposition> pumping = Task.Run(() =>
            composition.Feedback.TryPumpOnce(now, out _));
        Assert.IsTrue(composition.Lease.WriteEntered.Wait(1_000));
        Switch2ProUsbOwnedFeedbackQuiescenceResult concurrent =
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ProvenIncomplete,
            concurrent.Outcome);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ProvenIncomplete,
            reentrant.Outcome);
        Assert.AreEqual(0, composition.Lease.RetirementCount);
        composition.Lease.AllowWrite.Set();
        pumping.GetAwaiter().GetResult();
    }

    [TestMethod]
    public void MalformedWriteAndAbaRetirementQuarantineLifetime()
    {
        Composition malformed = CreateCommitted(ContainerA);
        Publish(malformed, out ulong now);
        malformed.Lease.WriteModes.Enqueue(OutputWriteMode.Malformed);
        malformed.Feedback.TryPumpOnce(now, out _);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationState.Quarantined,
            malformed.Feedback.ActivationState);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.OutcomeUncertain,
            malformed.Feedback.TryNeutralizeAndQuiesce(
                malformed.Authority, 100).Outcome);

        Composition aba = CreateCommitted(ContainerB);
        PublishAndPumpRetained(aba);
        aba.Lease.RetirementModes.Enqueue(
            OutputRetirementMode.QuiescentWithoutClearing);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.OutcomeUncertain,
            aba.Feedback.TryNeutralizeAndQuiesce(aba.Authority, 100).Outcome);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationState.Quarantined,
            aba.Feedback.ActivationState);
        Assert.AreEqual(1, aba.Lease.WriteCount,
            "An ABA contradiction cannot admit a replacement Stop.");
    }

    [TestMethod]
    public void ManualIdlePumpWarmPathAllocatesNothingAfterWarmup()
    {
        Composition composition = CreateCommitted(ContainerA);
        for (int index = 0; index < 2_000; index++)
        {
            composition.Feedback.TryPumpOnce((ulong)index, out _);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            composition.Feedback.TryPumpOnce((ulong)index, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(0, composition.Lease.WriteCount);
    }

    [TestMethod]
    public void NativeConnectionProfileEffectUsesOwnedUsbWriterLosslessly()
    {
        Composition composition = CreateCommitted(ContainerA);
        Assert.IsTrue(composition.Feedback.TryCreateLane(
            ControllerFeedbackPublicationOrigin.ProfileEffect,
            ControllerFeedbackSource.Xbox360VirtualDevice,
            ownershipEpoch: 1, timeToLiveMicroseconds: 250_000,
            renewalIntervalMicroseconds: 100_000, out var lane));

        Assert.IsTrue(composition.Feedback.
            TryPublishNativeProfileEffectAndPump(lane,
                Switch2ConnectionHaptic.ProSharpClickMarker,
                Switch2ConnectionHaptic.ProSharpClickGroup,
                Switch2ConnectionHaptic.ProSharpClickGroup));
        Assert.AreEqual(1, composition.Lease.Reports.Count);
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(
            composition.Lease.Reports[0], out _,
            out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right, out _));
        Assert.AreEqual(Switch2ConnectionHaptic.ProSharpClickGroup, left);
        Assert.AreEqual(Switch2ConnectionHaptic.ProSharpClickGroup, right);

        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent,
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100).Outcome);
    }

    [TestMethod]
    public void NativeIdentificationPreviewUsesOwnedUsbWriterLosslessly()
    {
        Composition composition = CreateCommitted(ContainerA);
        Assert.IsTrue(composition.Feedback.TryCreateLane(
            ControllerFeedbackPublicationOrigin.TestPreview,
            ControllerFeedbackSource.Xbox360VirtualDevice,
            ownershipEpoch: 2, timeToLiveMicroseconds: 250_000,
            renewalIntervalMicroseconds: 100_000, out var lane));

        Assert.IsTrue(composition.Feedback.
            TryPublishNativePreviewAndPump(lane,
                Switch2IdentificationHaptic.ProMarker,
                Switch2IdentificationHaptic.ProPulseGroup,
                Switch2IdentificationHaptic.ProPulseGroup));
        Assert.AreEqual(1, composition.Lease.Reports.Count);
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(
            composition.Lease.Reports[0], out _,
            out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right, out _));
        Assert.AreEqual(Switch2IdentificationHaptic.ProPulseGroup, left);
        Assert.AreEqual(Switch2IdentificationHaptic.ProPulseGroup, right);

        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent,
            composition.Feedback.TryNeutralizeAndQuiesce(
                composition.Authority, 100).Outcome);
    }

    private static Composition CreateComposition(Guid containerId,
        int operationWaitMilliseconds = 1)
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(containerId);
        var lease = new ScriptedOwnedLease(lifetime);
        Assert.IsTrue(Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
            lifetime, out Switch2ProUsbOwnedCompositeLeaseBundle bundle,
            out Switch2ProUsbOwnedCompositeAdmissionFailure failure),
            failure.ToString());
        Assert.IsTrue(bundle.TryTakeAuthority(
            out Switch2ProUsbOwnedCompositeAuthority authority));
        Assert.IsTrue(Switch2ProUsbOwnedFeedbackActivationLifetime.TryCreate(
            bundle, authority, operationWaitMilliseconds,
            out Switch2ProUsbOwnedFeedbackActivationLifetime feedback,
            out Switch2ProUsbOwnedFeedbackActivationCreateResult create),
            create.Failure.ToString());
        Assert.IsTrue(create.Succeeded);
        Assert.IsFalse(create.RequiresRetention);
        return new Composition(lease, bundle, authority, feedback);
    }

    private static Composition CreateCommitted(Guid containerId,
        int operationWaitMilliseconds = 1)
    {
        Composition composition = CreateComposition(containerId,
            operationWaitMilliseconds);
        var credential = Prepare(composition);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            composition.Feedback.TryCommitPrepared(credential, 0).Outcome);
        return composition;
    }

    private static Switch2ProUsbOwnedFeedbackPrepareCredential Prepare(
        in Composition composition)
    {
        Assert.IsTrue(composition.Feedback.TryTakeDormantQuiescenceProof(
            composition.Authority, out var proof));
        Switch2ProUsbOwnedFeedbackActivationResult prepared =
            composition.Feedback.TryPrepareActivation(composition.Authority,
                proof, 0);
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            prepared.Outcome);
        return prepared.Credential;
    }

    private static void Publish(Composition composition, out ulong now)
    {
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(
            out now));
        Assert.IsTrue(composition.Feedback.TryCreateLane(
            ControllerFeedbackPublicationOrigin.NativeGame,
            ControllerFeedbackSource.XboxOneVirtualDevice, 5, 250_000,
            100_000, out var lane));
        Assert.IsTrue(lane.TryPublish(new ControllerFeedbackActuatorState(
            1_000, 2_000, 3_000, 4_000), now));
    }

    private static void PublishAndPumpRetained(Composition composition)
    {
        Publish(composition, out ulong now);
        composition.Lease.WriteModes.Enqueue(OutputWriteMode.Retained);
        Assert.AreEqual(ControllerFeedbackPumpDisposition.RetryPending,
            composition.Feedback.TryPumpOnce(now, out _));
        Assert.AreEqual(1, composition.Lease.WriteCount);
    }

    private static void AssertReport(byte[] report, byte expectedCounter,
        bool expectNeutral)
    {
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(report,
            out byte counter, out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right, out _));
        Assert.AreEqual(expectedCounter, counter);
        bool neutral = left.Equals(default(Switch2HdRumbleGroup)) &&
            right.Equals(default(Switch2HdRumbleGroup));
        Assert.AreEqual(expectNeutral, neutral);
    }

    private static void AssertCompatibilityNeutralReport(byte[] report,
        byte expectedCounter)
    {
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(report,
            out byte counter, out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right, out _));
        Assert.AreEqual(expectedCounter, counter);
        // Canonical Neutral preserves legal compatibility carrier codes
        // 0x187/0x112 with zero amplitude. Only terminal Stop is encoded as
        // all-zero groups; full default-group equality is not this contract.
        var zeroAmplitude = new Switch2HdRumbleSubframe(0x187, 0, 0x112, 0);
        var expected = new Switch2HdRumbleGroup(zeroAmplitude,
            zeroAmplitude, zeroAmplitude);
        Assert.AreEqual(expected, left);
        Assert.AreEqual(expected, right);
        Assert.IsFalse(left.First.HasNonzeroAmplitude);
        Assert.IsFalse(left.Second.HasNonzeroAmplitude);
        Assert.IsFalse(left.Third.HasNonzeroAmplitude);
        Assert.IsFalse(right.First.HasNonzeroAmplitude);
        Assert.IsFalse(right.Second.HasNonzeroAmplitude);
        Assert.IsFalse(right.Third.HasNonzeroAmplitude);
    }

    private static void AssertImpulseSidedness(byte[] report,
        bool expectLeft, bool expectRight)
    {
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(report,
            out _, out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right, out _));
        Assert.AreEqual(expectLeft, left.First.HasNonzeroAmplitude);
        Assert.AreEqual(expectRight, right.First.HasNonzeroAmplitude);
        Assert.AreEqual(left.First, left.Second);
        Assert.AreEqual(left.First, left.Third);
        Assert.AreEqual(right.First, right.Second);
        Assert.AreEqual(right.First, right.Third);
    }

    private static void DecodeSustained(byte[] report,
        out Switch2HdRumbleGroup left, out Switch2HdRumbleGroup right)
    {
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(report,
            out _, out left, out right, out _));
        Assert.IsTrue(left.First.HasNonzeroAmplitude);
        Assert.IsTrue(right.First.HasNonzeroAmplitude);
        Assert.AreEqual(left.First, left.Second);
        Assert.AreEqual(left.First, left.Third);
        Assert.AreEqual(right.First, right.Second);
        Assert.AreEqual(right.First, right.Third);
    }

    private static void DecodeSidedAmplitude(byte[] report,
        out ushort leftAmplitude, out ushort rightAmplitude)
    {
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(report,
            out _, out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right, out _));
        leftAmplitude = left.First.Oscillator0AmplitudeCode;
        rightAmplitude = right.First.Oscillator0AmplitudeCode;
    }

    private static byte[] XboxWire(ulong sequence, ulong ownershipEpoch,
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

    private static Switch2PhysicalInputLifetime CreateLifetime(Guid containerId)
    {
        Assert.IsTrue(Switch2PhysicalContainerIdentity.TryCreate(containerId,
            out Switch2PhysicalContainerIdentity container));
        var input = new Switch2UsbHidInterfaceObservation(container, 0, 0,
            Switch2UsbBoundDriver.HidClass, 0x0001, 0x0005, 64, 64, 0);
        var bulkOut = new Switch2UsbPipeObservation(0x02,
            Switch2UsbPipeTransferType.Bulk, 64, 0);
        var bulkIn = new Switch2UsbPipeObservation(0x82,
            Switch2UsbPipeTransferType.Bulk, 64, 0);
        var command = new Switch2UsbCommandInterfaceObservation(container, 1,
            0, Switch2UsbBoundDriver.WinUsb, 2, bulkOut, bulkIn);
        var observation = new Switch2ProUsbCompositeObservation(0x057E,
            0x2069, 0x0201, container, 1, 1, input, command);
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out Switch2PhysicalInputRegistration registration,
            out Switch2PhysicalAdmissionFailure admission),
            admission.ToString());
        Assert.IsTrue(Switch2PhysicalInputLifetime.TryCreate(registration,
            DeviceGeneration, TransportGeneration, 10_000_000,
            out Switch2PhysicalInputLifetime lifetime));
        return lifetime;
    }

    private readonly struct Composition
    {
        internal Composition(ScriptedOwnedLease lease,
            Switch2ProUsbOwnedCompositeLeaseBundle bundle,
            in Switch2ProUsbOwnedCompositeAuthority authority,
            Switch2ProUsbOwnedFeedbackActivationLifetime feedback)
        {
            Lease = lease;
            Bundle = bundle;
            Authority = authority;
            Feedback = feedback;
        }

        internal ScriptedOwnedLease Lease { get; }
        internal Switch2ProUsbOwnedCompositeLeaseBundle Bundle { get; }
        internal Switch2ProUsbOwnedCompositeAuthority Authority { get; }
        internal Switch2ProUsbOwnedFeedbackActivationLifetime Feedback
        {
            get;
        }
    }

    private enum OutputWriteMode : byte
    {
        Complete = 0,
        Retained,
        Malformed,
        Throw,
    }

    private enum OutputRetirementMode : byte
    {
        Quiescent = 0,
        Retained,
        QuiescentWithoutClearing,
        Malformed,
        Throw,
    }

    private sealed class ScriptedOwnedLease :
        ISwitch2ProUsbOwnedCompositeLease
    {
        private readonly object gate = new();
        private readonly object claimFence = new();
        private readonly Switch2PhysicalInputLifetime lifetime;
        private object adoptionFence;
        private Switch2ProUsbOwnedOutputOperationClaim activeClaim;
        private ulong sequence;
        private bool adopted;

        internal ScriptedOwnedLease(
            in Switch2PhysicalInputLifetime lifetime)
        {
            this.lifetime = lifetime;
        }

        internal Queue<OutputWriteMode> WriteModes { get; } = new();
        internal Queue<OutputRetirementMode> RetirementModes { get; } = new();
        internal List<byte[]> Reports { get; } = new();
        internal List<Switch2PlayerLedCommand> PlayerLedCommands { get; } =
            new();
        internal List<string> Events { get; } = new();
        internal bool AcceptPlayerLedCommands;
        internal bool DeviceDisconnected;
        internal Action DuringWrite;
        internal bool BlockWrite;
        internal bool ThrowAdoptedBound;
        internal readonly ManualResetEventSlim WriteEntered = new(false);
        internal readonly ManualResetEventSlim AllowWrite = new(false);
        internal int AdoptionCount;
        internal int WriteCount;
        internal int DirectWriteCount;
        internal int RetirementCount;
        internal int DisposeCount;

        public Switch2PhysicalInputRegistration Registration =>
            lifetime.Registration;

        internal int ReportCount
        {
            get { lock (gate) { return Reports.Count; } }
        }

        internal byte[] ReportAt(int index)
        {
            lock (gate)
            {
                return Reports[index];
            }
        }

        public Switch2PhysicalInputLifetime Lifetime => lifetime;

        public int MaximumOutputOperationMilliseconds => 100;

        public bool AuthenticatesComposite(Switch2ControllerModel model,
            ulong deviceGeneration, ulong transportGeneration) =>
            model == Switch2ControllerModel.ProController2 &&
            deviceGeneration == DeviceGeneration &&
            transportGeneration == TransportGeneration;

        public bool AuthenticatesOutputOperationClaim(
            in Switch2ProUsbOwnedOutputOperationClaim claim)
        {
            lock (gate)
            {
                return !adopted && activeClaim.Equals(claim) &&
                    claim.Authenticates(claimFence, DeviceGeneration,
                        TransportGeneration, activeClaim.Sequence);
            }
        }

        public bool TryAdoptDormantFeedbackOutput(object ownerFence,
            out ISwitch2ProUsbOwnedFeedbackOutputLease outputLease)
        {
            outputLease = null;
            if (ownerFence == null)
            {
                return false;
            }
            var candidate = new AdoptedOutput(this, ownerFence);
            lock (gate)
            {
                if (adopted || sequence != 0 || activeClaim.IsValid)
                {
                    return false;
                }
                adopted = true;
                adoptionFence = ownerFence;
                AdoptionCount++;
                outputLease = candidate;
                return true;
            }
        }

        public Switch2ProUsbOwnedOutputWriteAttempt TryWriteReportBounded(
            ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
            ulong expectedDeviceGeneration,
            ulong expectedTransportGeneration, int timeoutMilliseconds)
        {
            lock (gate)
            {
                if (adopted)
                {
                    return Reject(expectedModel, expectedDeviceGeneration,
                        expectedTransportGeneration);
                }
                DirectWriteCount++;
            }
            return Write(ownerFence: null, report, expectedModel,
                expectedDeviceGeneration, expectedTransportGeneration);
        }

        public Switch2ProUsbOwnedOutputRetirementResult
            TryRetireOutputOperation(
                in Switch2ProUsbOwnedOutputOperationClaim claim,
                int timeoutMilliseconds) =>
            Switch2ProUsbOwnedOutputRetirementResult.Reject(claim);

        private Switch2ProUsbOwnedOutputWriteAttempt Write(object ownerFence,
            ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
            ulong expectedDeviceGeneration,
            ulong expectedTransportGeneration)
        {
            OutputWriteMode mode;
            Switch2ProUsbOwnedOutputOperationClaim claim = default;
            lock (gate)
            {
                if (adopted != (ownerFence != null) ||
                    adopted && !ReferenceEquals(adoptionFence, ownerFence) ||
                    activeClaim.IsValid)
                {
                    return Reject(expectedModel, expectedDeviceGeneration,
                        expectedTransportGeneration);
                }
                mode = WriteModes.Count == 0 ? OutputWriteMode.Complete :
                    WriteModes.Dequeue();
                WriteCount++;
                byte[] copy = report.ToArray();
                Reports.Add(copy);
                Assert.IsTrue(Switch2UsbHdRumbleCodec.
                    TryDecodeProController(copy, out _, out var left,
                        out var right, out _));
                Events.Add(left.Equals(default(Switch2HdRumbleGroup)) &&
                    right.Equals(default(Switch2HdRumbleGroup)) ?
                    "write.stop" : "write.frame");
                if (mode == OutputWriteMode.Retained)
                {
                    claim = new Switch2ProUsbOwnedOutputOperationClaim(
                        claimFence, DeviceGeneration, TransportGeneration,
                        ++sequence);
                    activeClaim = claim;
                }
                else
                {
                    sequence++;
                }
            }

            DuringWrite?.Invoke();
            if (BlockWrite)
            {
                WriteEntered.Set();
                AllowWrite.Wait(5_000);
            }
            if (mode == OutputWriteMode.Throw)
            {
                throw new InvalidOperationException("Injected write throw.");
            }
            if (mode == OutputWriteMode.Malformed)
            {
                return default;
            }
            if (mode == OutputWriteMode.Retained)
            {
                return new Switch2ProUsbOwnedOutputWriteAttempt(
                    Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(
                        expectedModel, expectedDeviceGeneration,
                        expectedTransportGeneration,
                        Switch2ProUsbHdRumbleTransportWriteFailure.
                            TransportRejected), claim);
            }
            return new Switch2ProUsbOwnedOutputWriteAttempt(
                Switch2ProUsbHdRumbleTransportWriteResult.Complete(
                    expectedModel, expectedDeviceGeneration,
                    expectedTransportGeneration, report.Length), default);
        }

        private bool AuthenticatesAdopted(object ownerFence)
        {
            lock (gate)
            {
                return adopted && ReferenceEquals(adoptionFence, ownerFence);
            }
        }

        private bool AuthenticatesAdoptedClaim(object ownerFence,
            in Switch2ProUsbOwnedOutputOperationClaim claim)
        {
            lock (gate)
            {
                return adopted && ReferenceEquals(adoptionFence, ownerFence) &&
                    activeClaim.Equals(claim) && claim.Authenticates(claimFence,
                        DeviceGeneration, TransportGeneration,
                        activeClaim.Sequence);
            }
        }

        private Switch2ProUsbOwnedOutputRetirementResult Retire(
            object ownerFence,
            in Switch2ProUsbOwnedOutputOperationClaim claim)
        {
            OutputRetirementMode mode;
            lock (gate)
            {
                if (!adopted || !ReferenceEquals(adoptionFence, ownerFence) ||
                    !activeClaim.Equals(claim))
                {
                    return Switch2ProUsbOwnedOutputRetirementResult.
                        Reject(claim);
                }
                mode = RetirementModes.Count == 0 ?
                    OutputRetirementMode.Quiescent :
                    RetirementModes.Dequeue();
                RetirementCount++;
                Events.Add("retire.frame");
                if (mode == OutputRetirementMode.Quiescent)
                {
                    activeClaim = default;
                }
            }
            if (mode == OutputRetirementMode.Throw)
            {
                throw new InvalidOperationException("Injected retire throw.");
            }
            return mode switch
            {
                OutputRetirementMode.Quiescent or
                    OutputRetirementMode.QuiescentWithoutClearing =>
                    Switch2ProUsbOwnedOutputRetirementResult.Quiescent(claim),
                OutputRetirementMode.Retained =>
                    Switch2ProUsbOwnedOutputRetirementResult.Retained(claim),
                _ => default,
            };
        }

        private static Switch2ProUsbOwnedOutputWriteAttempt Reject(
            Switch2ControllerModel model, ulong deviceGeneration,
            ulong transportGeneration) => new(
            Switch2ProUsbHdRumbleTransportWriteResult.Reject(model,
                deviceGeneration, transportGeneration,
                Switch2ProUsbHdRumbleTransportWriteFailure.TransportEnded),
            default);

        public Switch2ProUsbStartupCommandCompletion Execute(
            in Switch2ProUsbStartupCommandClaim claim,
            ReadOnlySpan<byte> exactRequest, int timeoutMilliseconds)
        {
            if (AcceptPlayerLedCommands &&
                claim.Step == Switch2ProUsbStartupStep.SetPlayerLed &&
                Switch2UsbCommandCodec.TryDecodePlayerLedRequest(exactRequest,
                    out Switch2PlayerLedCommand command, out _))
            {
                PlayerLedCommands.Add(command);
                return Switch2ProUsbStartupCommandCompletion.ExactResponse(
                    claim, claim.Step,
                    Switch2ProUsbStartupResponseProofKind.
                        PlayerLedResponseValidatedByCodec);
            }
            return Switch2ProUsbStartupCommandCompletion.ProvenNotConsumed(
                claim, claim.Step);
        }

        public Switch2ProUsbStartupRetirementCompletion Retire(
            in Switch2ProUsbStartupRetirementClaim claim,
            int timeoutMilliseconds) =>
            Switch2ProUsbStartupRetirementCompletion.ProvenNotReleased(claim,
                claim.Reason);

        public bool TryBeginInputRead(byte[] destination, int offset, int count,
            in Switch2ProUsbReadClaim claim,
            ISwitch2ProUsbReadCompletionTarget completionTarget) => false;

        public bool TryCancelInputRead(
            in Switch2ProUsbReadClaim claim) => false;

        public bool TryRetireCompletedInputRead(
            in Switch2ProUsbReadClaim claim,
            int timeoutMilliseconds) => false;

        public bool TryWaitForInputQuiescence(int timeoutMilliseconds) => true;

        public void DisposeQuiesced()
        {
            DisposeCount++;
        }

        private ulong DeviceGeneration =>
            lifetime.SessionDescriptor.DeviceGeneration;

        private ulong TransportGeneration =>
            lifetime.SessionDescriptor.TransportGeneration;

        private sealed class AdoptedOutput :
            ISwitch2ProUsbOwnedFeedbackOutputLease
        {
            private readonly ScriptedOwnedLease owner;
            private readonly object ownerFence;

            public bool TrySealDisconnectedOutput() =>
                owner.AuthenticatesAdopted(ownerFence) &&
                owner.DeviceDisconnected && !owner.activeClaim.IsValid;

            internal AdoptedOutput(ScriptedOwnedLease owner,
                object ownerFence)
            {
                this.owner = owner;
                this.ownerFence = ownerFence;
            }

            public int MaximumOutputOperationMilliseconds =>
                owner.ThrowAdoptedBound ?
                    throw new InvalidOperationException(
                        "Injected post-adoption constructor failure.") :
                    owner.MaximumOutputOperationMilliseconds;

            public bool AuthenticatesComposite(Switch2ControllerModel model,
                ulong deviceGeneration, ulong transportGeneration) =>
                owner.AuthenticatesAdopted(ownerFence) &&
                owner.AuthenticatesComposite(model, deviceGeneration,
                    transportGeneration);

            public bool AuthenticatesOutputOperationClaim(
                in Switch2ProUsbOwnedOutputOperationClaim claim) =>
                owner.AuthenticatesAdoptedClaim(ownerFence, claim);

            public Switch2ProUsbOwnedOutputWriteAttempt TryWriteReportBounded(
                ReadOnlySpan<byte> report,
                Switch2ControllerModel expectedModel,
                ulong expectedDeviceGeneration,
                ulong expectedTransportGeneration,
                int timeoutMilliseconds) => owner.Write(ownerFence, report,
                expectedModel, expectedDeviceGeneration,
                expectedTransportGeneration);

            public Switch2ProUsbOwnedOutputRetirementResult
                TryRetireOutputOperation(
                    in Switch2ProUsbOwnedOutputOperationClaim claim,
                    int timeoutMilliseconds) => owner.Retire(ownerFence,
                claim);
        }
    }
}
