using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

public partial class Switch2ProUsbRuntimeOwnerTests
{
    [TestMethod]
    public void ExactDisconnectedFeedbackStillPublishesVirtualNeutralBeforeSlotRemoval()
    {
        OwnedParticipantContext context = CreateOwnedParticipantContext();
        context.Feedback.TerminalOutcome = Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ExactDisconnectedAndQuiescent;
        CreateOwnedCore(context, out var core, out var table);
        int terminalReports = 0;
        Assert.IsTrue(core.TryAttach(context.Participant.Registration,
            () => context.Participant, (slot, sender, report) =>
            {
                if (report.Kind == Switch2RuntimeReportKind.TerminalNeutral)
                {
                    terminalReports++;
                }
            }, 2_000, out var token, out _));
        Assert.IsTrue(core.TryRemove(token, 2_000, out var failure), failure.Kind.ToString());
        Assert.AreEqual(1, terminalReports);
        Assert.AreEqual(InputControllerSlotState.Removed, table.GetSnapshot()[token.Slot].State);
        Assert.AreEqual(1, context.Lease.CompositeDisposeCount);
        AssertOrdered(context.Events, "feedback.disconnected", "startup.retire",
            "input.stop", "input.quiesce", "composite.dispose");
    }

    [TestMethod]
    public void OwnedCompositeActivationAndRemovalUseExactFacetOrder()
    {
        OwnedParticipantContext context = CreateOwnedParticipantContext();
        CreateOwnedCore(context, out var core, out var table);

        Assert.IsTrue(core.TryAttach(context.Participant.Registration,
            () => context.Participant, IgnoreOwnedMapping, 2_000,
            out InputControllerSlotToken token, out var attachFailure),
            attachFailure.Kind.ToString());
        Assert.AreEqual(InputControllerSlotState.Attached,
            table.GetSnapshot()[token.Slot].State);

        AssertOrdered(context.Events,
            "startup.EnableUsbHidReports",
            "startup.SetPlayerLed",
            "startup.SetFeatureMask",
            "startup.EnableFeatures",
            "startup.SelectCommonInputReport",
            "feedback.prepare",
            "input.prepare",
            "feedback.commit",
            "input.commit");

        Assert.IsTrue(core.TryRemove(token, 2_000, out var removeFailure),
            removeFailure.Kind.ToString());
        AssertOrdered(context.Events, "feedback.neutral",
            "startup.retire", "input.stop", "input.quiesce",
            "composite.dispose");
        Assert.AreEqual(1, context.Lease.CompositeDisposeCount);
        Assert.AreEqual(1, context.Lease.StartupRetirementCount);
        Assert.AreEqual(
            context.Lease.MaximumOutputOperationMilliseconds,
            context.Lease.LastStartupRetirementTimeoutMilliseconds,
            "The wider slot-lifecycle deadline escaped into the bounded command facet.");
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeParticipantState.Removed,
            context.Participant.State);
        Assert.IsTrue(context.Participant.InputRetirementProof.IsValid);
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                RuntimeRetirement,
            context.Participant.InputRetirementProof.Kind);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void AbortBeforePrepareTreatsDormantFeedbackAsSealed()
    {
        OwnedParticipantContext context = CreateOwnedParticipantContext();
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(71, out _));
        Assert.IsTrue(table.TryReserveAndBind(context.Participant.Registration,
            out InputControllerSlotToken token,
            out InputControllerSetupRollbackClaim rollback, out _));
        Assert.IsTrue(context.Participant.TryAdoptBoundSlot(token).Succeeded);
        var callbacks = new Switch2RuntimeRegistrationCallbacks(
            IgnoreOwnedReport, IgnoreOwnedAttention);
        Assert.IsTrue(context.Participant.TrySubscribe(callbacks).Succeeded);

        Assert.AreEqual(1, context.Feedback.DormantProofTakeCount);
        Assert.IsFalse(context.Feedback.TryTakeDormantProofAgain(),
            "A second coordinator acquired the already-adopted dormant feedback lifetime.");
        Switch2RuntimeRegistrationParticipantResult aborted =
            context.Participant.TryAbortUnpublished(1_000);

        Assert.IsTrue(aborted.Succeeded, aborted.FailureKind.ToString());
        CollectionAssert.DoesNotContain(context.Events, "feedback.prepare");
        CollectionAssert.DoesNotContain(context.Events, "feedback.abort");
        AssertOrdered(context.Events, "startup.retire", "input.quiesce",
            "composite.dispose");
        Assert.AreEqual(1, context.Lease.CompositeDisposeCount);
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeParticipantState.Aborted,
            context.Participant.State);
        Assert.IsTrue(context.Participant.TryUnsubscribe().Succeeded);
        Assert.IsTrue(table.TryRollback(rollback, out _));
    }

    [TestMethod]
    public void CleanInputPrepareRejectionRetiresAlreadyAbortedFacet()
    {
        OwnedParticipantContext context = CreateOwnedParticipantContext(
            rejectInputPrepare: true);
        CreateOwnedCore(context, out var core, out var table);

        Assert.IsFalse(core.TryAttach(context.Participant.Registration,
            () => context.Participant, IgnoreOwnedMapping, 2_000,
            out InputControllerSlotToken token, out var failure));

        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.PrepareRejected,
            failure.Kind);
        AssertOrdered(context.Events, "feedback.prepare", "input.prepare",
            "input.stop", "input.quiesce", "feedback.abort",
            "startup.retire", "composite.dispose");
        Assert.AreEqual(1, context.Lease.CompositeDisposeCount);
        Assert.IsTrue(context.Participant.InputRetirementProof.IsValid);
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeParticipantState.Aborted,
            context.Participant.State);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void DormantProofThrowAfterAdoptionRetainsExactFeedbackOwner()
    {
        CreateOwnedCreationInputs(out List<string> events,
            out Switch2PhysicalInputLifetime lifetime,
            out Switch2InputCalibrationSnapshot calibration,
            out OwnedCompositeLease lease,
            out Switch2ProUsbOwnedCompositeLeaseBundle bundle,
            out Switch2ProUsbOwnedCompositeAuthority authority,
            out OwnedPumpFactory pumpFactory);
        var feedback = new OwnedFeedbackLifetime(authority, lifetime, events)
        {
            PrepareOutcome =
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            CommitOutcome =
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            ThrowAfterDormantProofAdoption = true,
        };

        Assert.IsFalse(
            Switch2ProUsbOwnedCompositeRegistrationParticipant.TryCreateCore(
                bundle, authority, calibration, feedback, 500, pumpFactory,
                ImmediateOwnedTerminalScheduler.Instance,
                initialInputHandoffSequence: 0, out var participant,
                out Switch2ProUsbOwnedCompositeParticipantCreateFailure
                    failure));

        Assert.IsNull(participant);
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                DependencyThrew,
            failure.Kind);
        Assert.IsTrue(failure.RequiresRetention);
        Assert.AreSame(bundle, failure.RetainedBundle);
        Assert.IsNotNull(failure.RetainedRuntimeOwner);
        Assert.AreSame(feedback, failure.RetainedFeedbackLifetime);
        Assert.AreEqual(1, feedback.DormantProofTakeCount);
        Assert.IsFalse(feedback.TryTakeDormantProofAgain());
        Assert.AreEqual(0, lease.CompositeDisposeAttemptCount);
    }

    [TestMethod]
    public void CopiedDormantProofCannotPublishAndRetainsExactFeedbackOwner()
    {
        CreateOwnedCreationInputs(out List<string> events,
            out Switch2PhysicalInputLifetime lifetime,
            out Switch2InputCalibrationSnapshot calibration,
            out OwnedCompositeLease lease,
            out Switch2ProUsbOwnedCompositeLeaseBundle bundle,
            out Switch2ProUsbOwnedCompositeAuthority authority,
            out OwnedPumpFactory pumpFactory);
        var foreign = new OwnedFeedbackLifetime(authority, lifetime,
            new List<string>());
        Assert.IsTrue(foreign.TryTakeDormantQuiescenceProof(authority,
            out Switch2ProUsbOwnedFeedbackDormantQuiescenceProof
                copiedProof));
        var feedback = new OwnedFeedbackLifetime(authority, lifetime, events)
        {
            PrepareOutcome =
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            CommitOutcome =
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
            UseDormantProofOverride = true,
            DormantProofOverride = copiedProof,
        };

        Assert.IsFalse(
            Switch2ProUsbOwnedCompositeRegistrationParticipant.TryCreateCore(
                bundle, authority, calibration, feedback, 500, pumpFactory,
                ImmediateOwnedTerminalScheduler.Instance,
                initialInputHandoffSequence: 0, out var participant,
                out Switch2ProUsbOwnedCompositeParticipantCreateFailure
                    failure));

        Assert.IsNull(participant);
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                FeedbackDormantProofRejected,
            failure.Kind);
        Assert.IsTrue(failure.RequiresRetention);
        Assert.AreSame(bundle, failure.RetainedBundle);
        Assert.IsNotNull(failure.RetainedRuntimeOwner);
        Assert.AreSame(feedback, failure.RetainedFeedbackLifetime);
        Assert.AreEqual(1, feedback.DormantProofTakeCount);
        Assert.IsFalse(feedback.TryTakeDormantProofAgain());
        Assert.AreEqual(0, lease.CompositeDisposeAttemptCount);
    }

    [TestMethod]
    public void ProvenRejectedFeedbackPrepareRollsBackWithoutAbortCall()
    {
        OwnedParticipantContext context = CreateOwnedParticipantContext(
            feedbackPrepareOutcome:
                Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected);
        CreateOwnedCore(context, out var core, out var table);

        Assert.IsFalse(core.TryAttach(context.Participant.Registration,
            () => context.Participant, IgnoreOwnedMapping, 2_000,
            out InputControllerSlotToken token, out var failure));

        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.PrepareRejected,
            failure.Kind);
        CollectionAssert.Contains(context.Events, "feedback.prepare");
        CollectionAssert.DoesNotContain(context.Events, "feedback.abort");
        CollectionAssert.DoesNotContain(context.Events, "feedback.commit");
        AssertOrdered(context.Events, "feedback.prepare", "startup.retire",
            "input.quiesce", "composite.dispose");
        Assert.AreEqual(1, context.Lease.CompositeDisposeCount);
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeParticipantState.Aborted,
            context.Participant.State);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void FeedbackCommitUncertaintyNeverReleasesInputOrRollsBack()
    {
        OwnedParticipantContext context = CreateOwnedParticipantContext(
            feedbackCommitOutcome:
                Switch2ProUsbOwnedFeedbackActivationOutcome.OutcomeUncertain);
        CreateOwnedCore(context, out var core, out var table);

        Assert.IsFalse(core.TryAttach(context.Participant.Registration,
            () => context.Participant, IgnoreOwnedMapping, 100,
            out InputControllerSlotToken token, out var failure));

        Assert.IsTrue(failure.RequiresQuarantine);
        CollectionAssert.Contains(context.Events, "feedback.commit");
        CollectionAssert.DoesNotContain(context.Events, "input.commit");
        CollectionAssert.DoesNotContain(context.Events, "feedback.abort");
        CollectionAssert.DoesNotContain(context.Events, "startup.retire");
        CollectionAssert.DoesNotContain(context.Events,
            "composite.dispose");
        Assert.AreEqual(0, context.Lease.CompositeDisposeCount);
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[token.Slot].State);
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeParticipantState.Quarantined,
            context.Participant.State);
    }

    [TestMethod]
    public void InputCommitFailureNeutralizesFeedbackButRetainsComposite()
    {
        OwnedParticipantContext context = CreateOwnedParticipantContext(
            rejectInputCommit: true, inputCommitDelayMilliseconds: 25);
        CreateOwnedCore(context, out var core, out var table);

        Assert.IsFalse(core.TryAttach(context.Participant.Registration,
            () => context.Participant, IgnoreOwnedMapping, 100,
            out InputControllerSlotToken token, out var failure));

        Assert.IsTrue(failure.RequiresQuarantine);
        AssertOrdered(context.Events, "feedback.commit", "input.commit",
            "feedback.neutral");
        Assert.IsTrue(context.PumpFactory.Pump.
                LastCommitTimeoutMilliseconds > 0 &&
            context.PumpFactory.Pump.LastCommitTimeoutMilliseconds <= 50,
            "Input commit received more than its reserved share of the retained attach deadline.");
        Assert.IsTrue(context.Feedback.LastNeutralTimeoutMilliseconds > 0 &&
            context.Feedback.LastNeutralTimeoutMilliseconds <= 100,
            "Split-commit recovery received a fresh timeout instead of the remaining attach deadline.");
        CollectionAssert.DoesNotContain(context.Events, "startup.retire");
        CollectionAssert.DoesNotContain(context.Events,
            "composite.dispose");
        Assert.AreEqual(0, context.Lease.CompositeDisposeCount);
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void TerminalResultWithoutCurrentIssuerProofFencesLaterFacets()
    {
        OwnedParticipantContext context = CreateOwnedParticipantContext();
        context.Feedback.RejectTerminalAuthentication = true;
        CreateOwnedCore(context, out var core, out var table);
        Assert.IsTrue(core.TryAttach(context.Participant.Registration,
            () => context.Participant, IgnoreOwnedMapping, 2_000,
            out InputControllerSlotToken token, out _));

        Assert.IsFalse(core.TryRemove(token, 2_000, out var failure));

        Assert.IsTrue(failure.RequiresQuarantine);
        CollectionAssert.Contains(context.Events, "feedback.neutral");
        CollectionAssert.DoesNotContain(context.Events, "startup.retire");
        CollectionAssert.DoesNotContain(context.Events, "input.stop");
        CollectionAssert.DoesNotContain(context.Events, "composite.dispose");
        Assert.AreEqual(0, context.Lease.CompositeDisposeCount);
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void FeedbackSuccessWithContradictoryStateFailsClosed()
    {
        OwnedParticipantContext context = CreateOwnedParticipantContext(
            suppressPrepareStateTransition: true);
        CreateOwnedCore(context, out var core, out var table);

        Assert.IsFalse(core.TryAttach(context.Participant.Registration,
            () => context.Participant, IgnoreOwnedMapping, 2_000,
            out InputControllerSlotToken token, out var failure));

        Assert.IsTrue(failure.RequiresQuarantine);
        CollectionAssert.Contains(context.Events, "feedback.prepare");
        CollectionAssert.DoesNotContain(context.Events, "input.prepare");
        CollectionAssert.DoesNotContain(context.Events, "feedback.abort");
        CollectionAssert.DoesNotContain(context.Events,
            "composite.dispose");
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[token.Slot].State);
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeParticipantState.Quarantined,
            context.Participant.State);
    }

    [TestMethod]
    public void LateFeedbackCommitSuccessCannotReleaseInput()
    {
        OwnedParticipantContext context = CreateOwnedParticipantContext(
            feedbackCommitDelayMilliseconds: 150);
        CreateOwnedCore(context, out var core, out var table);

        Assert.IsFalse(core.TryAttach(context.Participant.Registration,
            () => context.Participant, IgnoreOwnedMapping, 100,
            out InputControllerSlotToken token, out var failure));

        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.IsTrue(context.Feedback.LastCommitTimeoutMilliseconds > 0 &&
            context.Feedback.LastCommitTimeoutMilliseconds <= 100);
        CollectionAssert.Contains(context.Events, "feedback.commit");
        CollectionAssert.DoesNotContain(context.Events, "input.commit");
        CollectionAssert.DoesNotContain(context.Events,
            "composite.dispose");
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void ConcurrentRemovalDisposesOwnedCompositeExactlyOnce()
    {
        OwnedParticipantContext context = CreateOwnedParticipantContext();
        CreateOwnedCore(context, out var core, out _);
        Assert.IsTrue(core.TryAttach(context.Participant.Registration,
            () => context.Participant, IgnoreOwnedMapping, 2_000,
            out InputControllerSlotToken token, out _));

        int successes = 0;
        Parallel.For(0, 64, iteration =>
        {
            if (core.TryRemove(token, 2_000, out _))
            {
                Interlocked.Increment(ref successes);
            }
        });

        Assert.IsTrue(successes >= 1);
        Assert.AreEqual(1, context.Lease.CompositeDisposeCount);
        Assert.AreEqual(1, context.Lease.StartupRetirementCount);
        Assert.AreEqual(1, context.Events.Count(value =>
            value == "feedback.neutral"));
    }

    [TestMethod]
    public void WholeCompositeDisposeThrowIsOneShotAndQuarantined()
    {
        OwnedParticipantContext context = CreateOwnedParticipantContext();
        context.Lease.ThrowOnCompositeDispose = true;
        CreateOwnedCore(context, out var core, out var table);
        Assert.IsTrue(core.TryAttach(context.Participant.Registration,
            () => context.Participant, IgnoreOwnedMapping, 2_000,
            out InputControllerSlotToken token, out _));

        Assert.IsFalse(core.TryRemove(token, 2_000, out var failure));

        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.AreEqual(1, context.Lease.CompositeDisposeAttemptCount);
        Assert.AreEqual(0, context.Lease.CompositeDisposeCount);
        Assert.IsFalse(context.Participant.TryStopAndQuiesce(100).Succeeded);
        Assert.AreEqual(1, context.Lease.CompositeDisposeAttemptCount,
            "An uncertain whole-composite disposal was retried.");
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[token.Slot].State);
    }

    private static OwnedParticipantContext CreateOwnedParticipantContext(
        Switch2ProUsbOwnedFeedbackActivationOutcome feedbackPrepareOutcome =
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
        Switch2ProUsbOwnedFeedbackActivationOutcome feedbackCommitOutcome =
            Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
        bool rejectInputPrepare = false,
        bool rejectInputCommit = false,
        int inputCommitDelayMilliseconds = 0,
        bool suppressPrepareStateTransition = false,
        int feedbackCommitDelayMilliseconds = 0)
    {
        CreateOwnedCreationInputs(out List<string> events,
            out Switch2PhysicalInputLifetime lifetime,
            out Switch2InputCalibrationSnapshot calibration,
            out OwnedCompositeLease lease,
            out Switch2ProUsbOwnedCompositeLeaseBundle bundle,
            out Switch2ProUsbOwnedCompositeAuthority authority,
            out OwnedPumpFactory pumpFactory);
        var feedback = new OwnedFeedbackLifetime(authority, lifetime, events)
        {
            PrepareOutcome = feedbackPrepareOutcome,
            CommitOutcome = feedbackCommitOutcome,
            SuppressPrepareStateTransition =
                suppressPrepareStateTransition,
            CommitDelayMilliseconds = feedbackCommitDelayMilliseconds,
        };
        pumpFactory.RejectPrepare = rejectInputPrepare;
        pumpFactory.RejectCommit = rejectInputCommit;
        pumpFactory.CommitDelayMilliseconds = inputCommitDelayMilliseconds;

        Assert.IsTrue(
            Switch2ProUsbOwnedCompositeRegistrationParticipant.TryCreateCore(
                bundle, authority, calibration, feedback, 500, pumpFactory,
                ImmediateOwnedTerminalScheduler.Instance,
                initialInputHandoffSequence: 0,
                out Switch2ProUsbOwnedCompositeRegistrationParticipant
                    participant,
                out Switch2ProUsbOwnedCompositeParticipantCreateFailure
                    failure),
            $"{failure.Kind}/{failure.InputAdoptionFailure}/" +
            failure.RuntimeAdoptionFailure.Kind);
        return new OwnedParticipantContext(events, lease, feedback,
            pumpFactory, participant);
    }

    private static void CreateOwnedCreationInputs(out List<string> events,
        out Switch2PhysicalInputLifetime lifetime,
        out Switch2InputCalibrationSnapshot calibration,
        out OwnedCompositeLease lease,
        out Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        out Switch2ProUsbOwnedCompositeAuthority authority,
        out OwnedPumpFactory pumpFactory)
    {
        events = new List<string>();
        Switch2ProUsbCompositeObservation observation = CreateObservation();
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out Switch2PhysicalInputRegistration registration, out _));
        Assert.IsTrue(Switch2PhysicalInputLifetime.TryCreate(registration,
            DeviceGeneration, TransportGeneration, QpcFrequency,
            out lifetime));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            out calibration));

        lease = new OwnedCompositeLease(lifetime, events);
        Assert.IsTrue(Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
            lifetime, out bundle, out var admissionFailure),
            admissionFailure.ToString());
        Assert.IsTrue(bundle.TryTakeAuthority(out authority));
        pumpFactory = new OwnedPumpFactory(events);
    }

    private static void CreateOwnedCore(OwnedParticipantContext context,
        out Switch2RuntimeRegistrationTransactionCore core,
        out InputControllerRegistrationTable table)
    {
        table = new InputControllerRegistrationTable(1);
        core = new Switch2RuntimeRegistrationTransactionCore(table);
        Assert.IsTrue(core.TryOpen(81, out var failure),
            failure.Kind.ToString());
    }

    private static void AssertOrdered(IReadOnlyList<string> events,
        params string[] expected)
    {
        int previous = -1;
        foreach (string value in expected)
        {
            int current = -1;
            for (int index = previous + 1; index < events.Count; index++)
            {
                if (events[index] == value)
                {
                    current = index;
                    break;
                }
            }
            Assert.IsTrue(current > previous,
                $"Missing or out-of-order '{value}': " +
                string.Join(",", events));
            previous = current;
        }
    }

    private static void IgnoreOwnedMapping(int slot, DS4Device sender,
        Switch2RuntimeReportEventArgs report)
    {
    }

    private static void IgnoreOwnedReport(DS4Device sender, EventArgs args)
    {
    }

    private static void IgnoreOwnedAttention(
        in Switch2RuntimeRegistrationLifecycleAttention attention)
    {
    }

    private sealed class OwnedParticipantContext
    {
        internal OwnedParticipantContext(List<string> events,
            OwnedCompositeLease lease, OwnedFeedbackLifetime feedback,
            OwnedPumpFactory pumpFactory,
            Switch2ProUsbOwnedCompositeRegistrationParticipant participant)
        {
            Events = events;
            Lease = lease;
            Feedback = feedback;
            PumpFactory = pumpFactory;
            Participant = participant;
        }

        internal List<string> Events { get; }

        internal OwnedCompositeLease Lease { get; }

        internal OwnedFeedbackLifetime Feedback { get; }

        internal OwnedPumpFactory PumpFactory { get; }

        internal Switch2ProUsbOwnedCompositeRegistrationParticipant
            Participant { get; }
    }

    private sealed class OwnedCompositeLease :
        ISwitch2ProUsbOwnedCompositeLease
    {
        private readonly Switch2PhysicalInputLifetime lifetime;
        private readonly List<string> events;

        internal OwnedCompositeLease(
            in Switch2PhysicalInputLifetime lifetime, List<string> events)
        {
            this.lifetime = lifetime;
            this.events = events;
        }

        public Switch2PhysicalInputRegistration Registration =>
            lifetime.Registration;

        public Switch2PhysicalInputLifetime Lifetime => lifetime;

        public int MaximumOutputOperationMilliseconds => 500;

        internal bool ThrowOnCompositeDispose { get; set; }

        internal int CompositeDisposeAttemptCount { get; private set; }

        internal int CompositeDisposeCount { get; private set; }

        internal int StartupRetirementCount { get; private set; }

        internal int LastStartupRetirementTimeoutMilliseconds
        {
            get;
            private set;
        } = -1;

        public bool AuthenticatesComposite(Switch2ControllerModel model,
            ulong deviceGeneration, ulong transportGeneration) =>
            model == Switch2ControllerModel.ProController2 &&
            deviceGeneration ==
                lifetime.SessionDescriptor.DeviceGeneration &&
            transportGeneration ==
                lifetime.SessionDescriptor.TransportGeneration;

        public Switch2ProUsbStartupCommandCompletion Execute(
            in Switch2ProUsbStartupCommandClaim claim,
            ReadOnlySpan<byte> exactRequest, int timeoutMilliseconds)
        {
            events.Add($"startup.{claim.Step}");
            Switch2ProUsbStartupResponseProofKind proof = claim.Step switch
            {
                Switch2ProUsbStartupStep.EnableUsbHidReports or
                    Switch2ProUsbStartupStep.SelectCommonInputReport =>
                    Switch2ProUsbStartupResponseProofKind.
                        InitializationResponseValidatedByCodec,
                Switch2ProUsbStartupStep.SetPlayerLed =>
                    Switch2ProUsbStartupResponseProofKind.
                        PlayerLedResponseValidatedByCodec,
                _ => Switch2ProUsbStartupResponseProofKind.
                    FeatureResponseValidatedByCodec,
            };
            return Switch2ProUsbStartupCommandCompletion.ExactResponse(claim,
                claim.Step, proof);
        }

        public Switch2ProUsbStartupRetirementCompletion Retire(
            in Switch2ProUsbStartupRetirementClaim claim,
            int timeoutMilliseconds)
        {
            StartupRetirementCount++;
            LastStartupRetirementTimeoutMilliseconds = timeoutMilliseconds;
            events.Add("startup.retire");
            if (timeoutMilliseconds < 0 || timeoutMilliseconds >
                    MaximumOutputOperationMilliseconds)
            {
                return Switch2ProUsbStartupRetirementCompletion.
                    ProvenNotReleased(claim, claim.Reason);
            }
            return Switch2ProUsbStartupRetirementCompletion.Released(claim,
                claim.Reason);
        }

        public Switch2ProUsbOwnedOutputWriteAttempt
            TryWriteReportBounded(ReadOnlySpan<byte> report,
                Switch2ControllerModel expectedModel,
                ulong expectedDeviceGeneration,
                ulong expectedTransportGeneration,
                int timeoutMilliseconds) => new(
            Switch2ProUsbHdRumbleTransportWriteResult.Complete(expectedModel,
                expectedDeviceGeneration, expectedTransportGeneration,
                report.Length), default);

        public Switch2ProUsbOwnedOutputRetirementResult
            TryRetireOutputOperation(
                in Switch2ProUsbOwnedOutputOperationClaim claim,
                int timeoutMilliseconds) =>
            Switch2ProUsbOwnedOutputRetirementResult.Quiescent(claim);

        public bool TryBeginInputRead(byte[] destination, int offset,
            int count, in Switch2ProUsbReadClaim claim,
            ISwitch2ProUsbReadCompletionTarget completionTarget) => false;

        public bool TryCancelInputRead(
            in Switch2ProUsbReadClaim claim) => true;

        public bool TryRetireCompletedInputRead(
            in Switch2ProUsbReadClaim claim,
            int timeoutMilliseconds) => true;

        public bool TryWaitForInputQuiescence(int timeoutMilliseconds)
        {
            events.Add("input.quiesce");
            return true;
        }

        public void DisposeQuiesced()
        {
            CompositeDisposeAttemptCount++;
            events.Add("composite.dispose");
            if (ThrowOnCompositeDispose)
            {
                throw new InvalidOperationException(
                    "Synthetic whole-composite disposal fault.");
            }
            CompositeDisposeCount++;
        }
    }

    private sealed class OwnedFeedbackLifetime :
        ISwitch2ProUsbOwnedFeedbackActivationLifetime
    {
        private readonly object issuer = new();
        private readonly object fence = new();
        private readonly object terminalFence = new();
        private readonly Switch2ProUsbOwnedCompositeAuthority authority;
        private readonly Switch2PhysicalInputLifetime lifetime;
        private readonly List<string> events;
        private Switch2ProUsbOwnedFeedbackPrepareCredential credential;
        private bool dormantProofTaken;
        private bool dormantProofConsumed;
        private ulong terminalRevision = 1;

        internal OwnedFeedbackLifetime(
            in Switch2ProUsbOwnedCompositeAuthority authority,
            in Switch2PhysicalInputLifetime lifetime, List<string> events)
        {
            this.authority = authority;
            this.lifetime = lifetime;
            this.events = events;
            ActivationState =
                Switch2ProUsbOwnedFeedbackActivationState.Dormant;
        }

        internal Switch2ProUsbOwnedFeedbackActivationOutcome PrepareOutcome
        {
            get;
            set;
        }

        internal Switch2ProUsbOwnedFeedbackActivationOutcome CommitOutcome
        {
            get;
            set;
        }

        internal bool SuppressPrepareStateTransition { get; set; }

        internal int CommitDelayMilliseconds { get; set; }

        internal int LastCommitTimeoutMilliseconds { get; private set; }

        internal int LastNeutralTimeoutMilliseconds { get; private set; }

        internal int DormantProofTakeCount { get; private set; }

        internal bool ThrowAfterDormantProofAdoption { get; set; }

        internal bool RejectTerminalAuthentication { get; set; }
        internal Switch2ProUsbOwnedFeedbackQuiescenceOutcome TerminalOutcome { get; set; } =
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ExactNeutralAndQuiescent;

        internal bool UseDormantProofOverride { get; set; }

        internal Switch2ProUsbOwnedFeedbackDormantQuiescenceProof
            DormantProofOverride { get; set; }

        internal bool TryTakeDormantProofAgain() =>
            TryTakeDormantQuiescenceProof(authority, out _);

        public Switch2ProUsbOwnedFeedbackActivationState ActivationState
        {
            get;
            private set;
        }

        public bool Authenticates(
            in Switch2ProUsbOwnedCompositeAuthority candidate) =>
            candidate.Equals(authority);

        public bool AuthenticatesQuiescenceResult(
            in Switch2ProUsbOwnedCompositeAuthority candidate,
            in Switch2ProUsbOwnedFeedbackQuiescenceResult result) =>
            !RejectTerminalAuthentication && candidate.Equals(authority) &&
            ActivationState ==
                Switch2ProUsbOwnedFeedbackActivationState.Aborted &&
            result.Outcome == TerminalOutcome &&
            result.AuthenticatesExact(this, terminalFence, authority,
                terminalRevision);

        public bool TryTakeDormantQuiescenceProof(
            in Switch2ProUsbOwnedCompositeAuthority candidate,
            out Switch2ProUsbOwnedFeedbackDormantQuiescenceProof proof)
        {
            DormantProofTakeCount++;
            if (dormantProofTaken || !candidate.Equals(authority) ||
                ActivationState !=
                    Switch2ProUsbOwnedFeedbackActivationState.Dormant)
            {
                proof = default;
                return false;
            }
            dormantProofTaken = true;
            proof = new Switch2ProUsbOwnedFeedbackDormantQuiescenceProof(
                this, fence, authority, lifetime, sequence: 1);
            if (UseDormantProofOverride)
            {
                proof = DormantProofOverride;
            }
            if (ThrowAfterDormantProofAdoption)
            {
                throw new InvalidOperationException(
                    "Synthetic throw after dormant feedback adoption.");
            }
            return true;
        }

        public Switch2ProUsbOwnedFeedbackActivationResult
            TryPrepareActivation(
                in Switch2ProUsbOwnedCompositeAuthority candidate,
                in Switch2ProUsbOwnedFeedbackDormantQuiescenceProof
                    dormantProof,
                int timeoutMilliseconds)
        {
            events.Add("feedback.prepare");
            if (dormantProofConsumed ||
                !dormantProof.Authenticates(this, fence, authority,
                    lifetime, expectedSequence: 1) ||
                !candidate.Equals(authority) || ActivationState !=
                    Switch2ProUsbOwnedFeedbackActivationState.Dormant)
            {
                return Switch2ProUsbOwnedFeedbackActivationResult.Uncertain(
                    Switch2ProUsbOwnedFeedbackActivationOperation.Prepare,
                    authority);
            }
            if (PrepareOutcome !=
                Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected)
            {
                dormantProofConsumed = true;
            }
            return PrepareOutcome switch
            {
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded =>
                    Prepare(candidate),
                Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected =>
                    Switch2ProUsbOwnedFeedbackActivationResult.Rejected(
                        Switch2ProUsbOwnedFeedbackActivationOperation.Prepare,
                        candidate),
                _ => Switch2ProUsbOwnedFeedbackActivationResult.Uncertain(
                    Switch2ProUsbOwnedFeedbackActivationOperation.Prepare,
                    candidate),
            };
        }

        public Switch2ProUsbOwnedFeedbackActivationResult TryCommitPrepared(
            in Switch2ProUsbOwnedFeedbackPrepareCredential candidate,
            int timeoutMilliseconds)
        {
            events.Add("feedback.commit");
            LastCommitTimeoutMilliseconds = timeoutMilliseconds;
            if (CommitDelayMilliseconds > 0)
            {
                Thread.Sleep(CommitDelayMilliseconds);
            }
            if (!candidate.Equals(credential))
            {
                return Switch2ProUsbOwnedFeedbackActivationResult.Uncertain(
                    Switch2ProUsbOwnedFeedbackActivationOperation.Commit,
                    authority);
            }
            return CommitOutcome switch
            {
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded =>
                    Commit(),
                Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected =>
                    Switch2ProUsbOwnedFeedbackActivationResult.Rejected(
                        Switch2ProUsbOwnedFeedbackActivationOperation.Commit,
                        authority),
                _ => Switch2ProUsbOwnedFeedbackActivationResult.Uncertain(
                    Switch2ProUsbOwnedFeedbackActivationOperation.Commit,
                    authority),
            };
        }

        public Switch2ProUsbOwnedFeedbackActivationResult TryAbortPrepared(
            in Switch2ProUsbOwnedFeedbackPrepareCredential candidate,
            int timeoutMilliseconds)
        {
            events.Add("feedback.abort");
            if (!candidate.Equals(credential))
            {
                return Switch2ProUsbOwnedFeedbackActivationResult.Uncertain(
                    Switch2ProUsbOwnedFeedbackActivationOperation.Abort,
                    authority);
            }
            ActivationState =
                Switch2ProUsbOwnedFeedbackActivationState.Aborted;
            return Switch2ProUsbOwnedFeedbackActivationResult.Succeeded(
                Switch2ProUsbOwnedFeedbackActivationOperation.Abort,
                authority);
        }

        public Switch2ProUsbOwnedFeedbackQuiescenceResult
            TryNeutralizeAndQuiesce(
                in Switch2ProUsbOwnedCompositeAuthority candidate,
                int timeoutMilliseconds)
        {
            events.Add(TerminalOutcome == Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ExactDisconnectedAndQuiescent ?
                "feedback.disconnected" : "feedback.neutral");
            LastNeutralTimeoutMilliseconds = timeoutMilliseconds;
            ActivationState =
                Switch2ProUsbOwnedFeedbackActivationState.Aborted;
            terminalRevision++;
            return Switch2ProUsbOwnedFeedbackQuiescenceResult.Exact(
                TerminalOutcome,
                this, terminalFence, candidate.DeviceGeneration,
                candidate.TransportGeneration, terminalRevision);
        }

        private Switch2ProUsbOwnedFeedbackActivationResult Prepare(
            in Switch2ProUsbOwnedCompositeAuthority candidate)
        {
            credential = new Switch2ProUsbOwnedFeedbackPrepareCredential(
                issuer, fence, candidate, lifetime, sequence: 1);
            if (!SuppressPrepareStateTransition)
            {
                ActivationState =
                    Switch2ProUsbOwnedFeedbackActivationState.Prepared;
            }
            return Switch2ProUsbOwnedFeedbackActivationResult.Prepared(
                candidate, credential);
        }

        private Switch2ProUsbOwnedFeedbackActivationResult Commit()
        {
            ActivationState =
                Switch2ProUsbOwnedFeedbackActivationState.Committed;
            return Switch2ProUsbOwnedFeedbackActivationResult.Succeeded(
                Switch2ProUsbOwnedFeedbackActivationOperation.Commit,
                authority);
        }
    }

    private sealed class OwnedPumpFactory :
        ISwitch2ProUsbRuntimePumpFactory
    {
        private readonly List<string> events;

        internal OwnedPumpFactory(List<string> events)
        {
            this.events = events;
        }

        internal bool RejectCommit { get; set; }

        internal bool RejectPrepare { get; set; }

        internal int CommitDelayMilliseconds { get; set; }

        internal OwnedPump Pump { get; private set; }

        public bool TryCreate(Switch2ProUsbInputTransportOwner transportOwner,
            int readRetirementTimeoutMilliseconds,
            out ISwitch2ProUsbRuntimeReadPump pump,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            Pump = new OwnedPump(transportOwner, events)
            {
                RejectPrepare = RejectPrepare,
                RejectCommit = RejectCommit,
                CommitDelayMilliseconds = CommitDelayMilliseconds,
            };
            pump = Pump;
            failure = Switch2ProUsbInputReadPumpFailure.None;
            return true;
        }
    }

    private sealed class OwnedPump : ISwitch2ProUsbRuntimeReadPump
    {
        private readonly Switch2ProUsbInputTransportOwner transport;
        private readonly List<string> events;
        private Action<Switch2ProUsbInputReadPumpFailure> attention;

        internal OwnedPump(Switch2ProUsbInputTransportOwner transport,
            List<string> events)
        {
            this.transport = transport;
            this.events = events;
            State = Switch2ProUsbInputReadPumpState.Created;
        }

        internal bool RejectCommit { get; set; }

        internal bool RejectPrepare { get; set; }

        internal int CommitDelayMilliseconds { get; set; }

        internal int LastCommitTimeoutMilliseconds { get; private set; }

        public Switch2ProUsbInputReadPumpState State { get; private set; }

        public Switch2ProUsbInputReadPumpFailure TerminalFailure
        {
            get;
            private set;
        }

        public Switch2ProUsbDisposeFailure LastDisposeFailure
        {
            get;
            private set;
        }

        public long StartedReadCount => 0;

        public long RetiredReadCount => 0;

        public bool TrySetLifecycleAttentionHandler(
            Action<Switch2ProUsbInputReadPumpFailure> handler)
        {
            if (handler == null || attention != null)
            {
                return false;
            }
            attention = handler;
            return true;
        }

        public bool TryPrepareStart(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            events.Add("input.prepare");
            if (RejectPrepare)
            {
                failure = Switch2ProUsbInputReadPumpFailure.
                    ActivationCredentialRejected;
                return false;
            }
            State = Switch2ProUsbInputReadPumpState.Prepared;
            failure = Switch2ProUsbInputReadPumpFailure.None;
            return true;
        }

        public bool TryCommitPrepared(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            events.Add("input.commit");
            LastCommitTimeoutMilliseconds = timeoutMilliseconds;
            if (CommitDelayMilliseconds > 0)
            {
                Thread.Sleep(CommitDelayMilliseconds);
            }
            if (RejectCommit)
            {
                failure = Switch2ProUsbInputReadPumpFailure.
                    ActivationCredentialRejected;
                return false;
            }
            State = Switch2ProUsbInputReadPumpState.Running;
            failure = Switch2ProUsbInputReadPumpFailure.None;
            return true;
        }

        public bool TryAbortPrepared(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            events.Add("input.abort");
            return Stop(timeoutMilliseconds, out failure);
        }

        public bool TryStart(out Switch2ProUsbInputReadPumpFailure failure) =>
            TryPrepareStart(500, out failure) &&
            TryCommitPrepared(500, out failure);

        public bool RequestStop()
        {
            transport.RequestStop();
            State = Switch2ProUsbInputReadPumpState.StopRequested;
            return true;
        }

        public bool TryStopAndDispose(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            events.Add("input.stop");
            return Stop(timeoutMilliseconds, out failure);
        }

        private bool Stop(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            transport.RequestStop();
            bool stopped = transport.TryQuiesceAndDispose(
                timeoutMilliseconds, out Switch2ProUsbDisposeFailure dispose);
            LastDisposeFailure = dispose;
            if (stopped)
            {
                State = Switch2ProUsbInputReadPumpState.Disposed;
                failure = Switch2ProUsbInputReadPumpFailure.None;
                return true;
            }
            TerminalFailure =
                Switch2ProUsbInputReadPumpFailure.OwnerDisposeRejected;
            failure = TerminalFailure;
            return false;
        }
    }

    private sealed class ImmediateOwnedTerminalScheduler :
        ISwitch2ProUsbRuntimeTerminalScheduler
    {
        internal static readonly ImmediateOwnedTerminalScheduler Instance =
            new();

        public bool TrySchedule(
            Func<Switch2TerminalNeutralRequestResult> callback,
            out Task<Switch2TerminalNeutralRequestResult> task)
        {
            task = Task.FromResult(callback());
            return true;
        }
    }
}
