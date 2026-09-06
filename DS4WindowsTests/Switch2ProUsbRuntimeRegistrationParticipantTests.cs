using System;
using System.Threading;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

public partial class Switch2ProUsbRuntimeOwnerTests
{
    [TestMethod]
    public void ParticipantResultsRejectMalformedOperationTagsAndNeverLoseQuarantine()
    {
        Assert.IsFalse(default(
            Switch2RuntimeRegistrationParticipantResult).IsValid);
        Assert.IsFalse(Switch2RuntimeRegistrationParticipantResult.TryCreate(
            Switch2RuntimeRegistrationParticipantOperation.Invalid,
            Switch2RuntimeRegistrationParticipantOutcome.Succeeded,
            Switch2RuntimeRegistrationParticipantFailureKind.None,
            default, default, out _));
        Assert.IsFalse(Switch2RuntimeRegistrationParticipantResult.TryCreate(
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation,
            Switch2RuntimeRegistrationParticipantOutcome.ProvenRejected,
            Switch2RuntimeRegistrationParticipantFailureKind.RemoveRejected,
            default, default, out _),
            "A failure tagged for another operation must not be normalized.");
        Assert.IsFalse(Switch2RuntimeRegistrationParticipantResult.TryCreate(
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared,
            Switch2RuntimeRegistrationParticipantOutcome.Succeeded,
            Switch2RuntimeRegistrationParticipantFailureKind.CommitRejected,
            default, default, out _));

        Assert.IsTrue(Switch2RuntimeRegistrationParticipantResult.TryCreate(
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation,
            Switch2RuntimeRegistrationParticipantOutcome.ProvenRejected,
            Switch2RuntimeRegistrationParticipantFailureKind.
                QuarantineRequired,
            default, default, out var provenQuarantine));
        Assert.IsTrue(provenQuarantine.IsValid);
        Assert.IsFalse(provenQuarantine.Succeeded);
        Assert.IsTrue(provenQuarantine.RequiresQuarantine,
            "A native QuarantineRequired result cannot become reusable merely because its reason field is default.");

        Assert.IsTrue(Switch2RuntimeRegistrationParticipantResult.TryCreate(
            Switch2RuntimeRegistrationParticipantOperation.Subscribe,
            Switch2RuntimeRegistrationParticipantOutcome.OutcomeUncertain,
            Switch2RuntimeRegistrationParticipantFailureKind.
                SubscriptionRejected,
            default, InputControllerSlotQuarantineReason.None,
            out var uncertainWithoutReason));
        Assert.IsTrue(uncertainWithoutReason.RequiresQuarantine,
            "Outcome uncertainty is independently sufficient to quarantine.");

        Assert.IsFalse(default(Switch2RuntimeRegistrationCallbacks).IsValid);
        DS4Device.ReportHandler<EventArgs> report = static (_, _) => { };
        Switch2RuntimeRegistrationLifecycleAttentionCallback attention =
            IgnoreParticipantAttention;
        var callbacks = new Switch2RuntimeRegistrationCallbacks(report,
            attention);
        Assert.IsTrue(callbacks.IsValid);
        Assert.IsTrue(callbacks.IsExact(callbacks));
        var different = new Switch2RuntimeRegistrationCallbacks(
            static (_, _) => { }, attention);
        Assert.IsFalse(callbacks.IsExact(different));
    }

    [TestMethod]
    public void UsbParticipantRetainsExactCredentialsCallbacksAndRetirementClaim()
    {
        FakeLease lease = new()
        {
            CompleteSynchronously = true,
            MaximumSuccessfulBegins = 1,
        };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        var participant =
            new Switch2ProUsbRuntimeRegistrationParticipant(owner);
        Assert.AreEqual(registration, participant.Registration);

        var table = new InputControllerRegistrationTable(1);
        var foreignTable = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(801, out var tableFailure),
            tableFailure.ToString());
        Assert.IsTrue(foreignTable.TryOpen(802, out var foreignFailure),
            foreignFailure.ToString());
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out _, out tableFailure), tableFailure.ToString());
        Assert.IsTrue(foreignTable.TryReserveAndBind(registration,
            out var foreignToken, out _, out foreignFailure),
            foreignFailure.ToString());

        AssertParticipantSuccess(participant.TryAdoptBoundSlot(token));
        AssertParticipantSuccess(participant.TryAdoptBoundSlot(token));
        Assert.IsTrue(participant.HasAdoptedSlot);
        Switch2RuntimeRegistrationParticipantResult foreignAdoption =
            participant.TryAdoptBoundSlot(foreignToken);
        Assert.IsTrue(foreignAdoption.IsValid);
        Assert.IsFalse(foreignAdoption.Succeeded);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            StaleCredential, foreignAdoption.FailureKind);

        InputControllerRetirementClaim retirementClaim = default;
        string callbackFailure = null;
        int regularCount = 0;
        int terminalCount = 0;
        Switch2RuntimeRegistrationLifecycleAttention observedAttention =
            default;
        using ManualResetEventSlim regularSeen = new(false);
        using ManualResetEventSlim attentionSeen = new(false);
        DS4Device.ReportHandler<EventArgs> reportHandler = (sender, args) =>
        {
            if (args is not Switch2RuntimeReportEventArgs report ||
                report.RuntimeGeneration != registration.Generation)
            {
                callbackFailure = "Wrong report envelope";
                return;
            }
            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                if (!table.TryAcquireReportLease(token, sender,
                        out InputControllerReportLease leaseToken,
                        out InputControllerSlotTableFailure leaseFailure))
                {
                    callbackFailure = leaseFailure.ToString();
                }
                else
                {
                    leaseToken.Dispose();
                    Interlocked.Increment(ref regularCount);
                }
                regularSeen.Set();
                return;
            }

            if (!retirementClaim.IsValid)
            {
                callbackFailure = "Missing retirement claim";
                return;
            }
            InputControllerSlotTableFailure terminalFailure = default;
            if (!table.TryAcquireTerminalReportLease(retirementClaim, sender,
                    out InputControllerReportLease terminalLease,
                    out terminalFailure))
            {
                callbackFailure = terminalFailure.ToString();
                return;
            }
            if (!terminalLease.TryAcknowledgeTerminalNeutral(
                    out terminalFailure))
            {
                callbackFailure = terminalFailure.ToString();
            }
            terminalLease.Dispose();
            Interlocked.Increment(ref terminalCount);
        };
        Switch2RuntimeRegistrationLifecycleAttentionCallback attentionHandler =
            (in Switch2RuntimeRegistrationLifecycleAttention attention) =>
            {
                observedAttention = attention;
                attentionSeen.Set();
            };
        var callbacks = new Switch2RuntimeRegistrationCallbacks(reportHandler,
            attentionHandler);
        AssertParticipantSuccess(participant.TrySubscribe(callbacks));
        AssertParticipantSuccess(participant.TrySubscribe(callbacks));
        Assert.IsTrue(participant.IsSubscribed);
        var differentCallbacks = new Switch2RuntimeRegistrationCallbacks(
            static (_, _) => { }, attentionHandler);
        Switch2RuntimeRegistrationParticipantResult differentSubscription =
            participant.TrySubscribe(differentCallbacks);
        Assert.IsFalse(differentSubscription.Succeeded);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            InvalidCredential, differentSubscription.FailureKind);

        AssertParticipantSuccess(participant.TryPrepareActivation(1_000));
        Assert.IsTrue(participant.HasPreparedCredential);
        Assert.IsTrue(table.TryBeginActivate(token, out var activation,
            out tableFailure), tableFailure.ToString());
        Assert.IsTrue(table.TryAcquireActivationCommit(activation,
            out var activationCommit, out tableFailure),
            tableFailure.ToString());
        Assert.IsTrue(foreignTable.TryBeginActivate(foreignToken,
            out var foreignActivation, out foreignFailure),
            foreignFailure.ToString());
        Assert.IsTrue(foreignTable.TryAcquireActivationCommit(
            foreignActivation, out var foreignCommit, out foreignFailure),
            foreignFailure.ToString());

        Switch2RuntimeRegistrationParticipantResult foreignCommitResult =
            participant.TryCommitPrepared(foreignCommit);
        Assert.IsTrue(foreignCommitResult.IsValid);
        Assert.IsFalse(foreignCommitResult.Succeeded);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            InvalidCredential, foreignCommitResult.FailureKind);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Prepared, owner.State,
            "A foreign table capability must not release the native worker.");
        Assert.AreEqual(0, lease.BeginCount);
        Assert.IsFalse(foreignTable.TryCompleteActivate(foreignCommit,
            externalCommitSucceeded: false, out foreignFailure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            ActivationCommitRejected, foreignFailure);

        AssertParticipantSuccess(participant.TryCommitPrepared(
            activationCommit));
        Assert.IsTrue(table.TryCompleteActivate(activationCommit,
            externalCommitSucceeded: true, out tableFailure),
            tableFailure.ToString());
        Assert.IsTrue(regularSeen.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(attentionSeen.Wait(TimeSpan.FromSeconds(2)),
            "The failed second native begin should become normalized producer attention.");
        Assert.IsTrue(observedAttention.IsValid);
        Assert.AreEqual(registration, observedAttention.Registration);
        Assert.AreEqual(Switch2RuntimeRegistrationLifecycleAttentionKind.
            ProducerFailed, observedAttention.Kind);
        Assert.IsNull(callbackFailure);

        Assert.IsTrue(table.TryBeginRetire(token, out retirementClaim,
            out tableFailure), tableFailure.ToString());
        var foreignClaim = new InputControllerRetirementClaim(foreignToken);
        Switch2RuntimeRegistrationParticipantResult foreignArm =
            participant.TryArmRetirement(foreignClaim);
        Assert.IsFalse(foreignArm.Succeeded);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            StaleCredential, foreignArm.FailureKind);
        AssertParticipantSuccess(participant.TryArmRetirement(
            retirementClaim));
        AssertParticipantSuccess(participant.TryArmRetirement(
            retirementClaim));
        Assert.IsTrue(table.TryWaitForDrain(retirementClaim, 1_000,
            out tableFailure), tableFailure.ToString());
        AssertParticipantSuccess(participant.
            TryWaitForPublicationAvailability(1_000));
        AssertParticipantSuccess(participant.TryStopAndQuiesce(1_000));
        Assert.IsNull(callbackFailure);
        Assert.IsTrue(table.TryWaitForDrain(retirementClaim, 1_000,
            out tableFailure), tableFailure.ToString());
        Assert.IsTrue(table.TryMarkQuiesced(retirementClaim,
            out tableFailure), tableFailure.ToString());
        AssertParticipantSuccess(participant.TryUnsubscribe());
        AssertParticipantSuccess(participant.TryUnsubscribe());
        Assert.IsFalse(participant.IsSubscribed);
        AssertParticipantSuccess(participant.TryRemove());
        AssertParticipantSuccess(participant.TryRemove());
        Assert.IsTrue(table.TryCompleteRemoval(retirementClaim,
            out tableFailure), tableFailure.ToString());
        Assert.AreEqual(1, regularCount);
        Assert.AreEqual(1, terminalCount);
    }

    [TestMethod]
    public void CoordinatorUsesOneUsbParticipantForTheExactOwnerLifecycle()
    {
        var pumpFactory = new ScriptedPumpFactory();
        Assert.IsTrue(TryCreateCore(new FakeLease(), pumpFactory,
            out Switch2ProUsbRuntimeOwner owner, out _, out var created),
            created.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        RecordingParticipant participant = null;
        int participantCount = 0;
        var coordinator = new Switch2ProUsbRuntimeRegistrationCoordinator(
            table, 5_000, exactOwner =>
            {
                participantCount++;
                participant = new RecordingParticipant(
                    new Switch2ProUsbRuntimeRegistrationParticipant(
                        exactOwner));
                return participant;
            });
        Assert.IsTrue(coordinator.TryOpen(17, out var opened),
            opened.Kind.ToString());

        Assert.IsTrue(coordinator.TryAttach(owner, static (_, _, _) => { },
            2_000, out InputControllerSlotToken token, out var attached),
            attached.Kind.ToString());
        Assert.IsTrue(coordinator.TryRemove(token, 2_000, out var removed),
            removed.Kind.ToString());

        Assert.AreEqual(1, participantCount,
            "One attached owner binding must retain one participant instance.");
        CollectionAssert.AreEqual(new[]
        {
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot,
            Switch2RuntimeRegistrationParticipantOperation.Subscribe,
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation,
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared,
            Switch2RuntimeRegistrationParticipantOperation.ArmRetirement,
            Switch2RuntimeRegistrationParticipantOperation.
                WaitForPublicationAvailability,
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce,
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe,
            Switch2RuntimeRegistrationParticipantOperation.Remove,
        }, participant.Operations);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Removed, owner.State);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void CoordinatorQuarantinesMalformedOrWrongTagAdoptionEvidence()
    {
        Switch2RuntimeRegistrationParticipantResult[] forgedResults =
        {
            default,
            Switch2RuntimeRegistrationParticipantResult.Success(
                Switch2RuntimeRegistrationParticipantOperation.Subscribe),
        };
        foreach (Switch2RuntimeRegistrationParticipantResult forged in
            forgedResults)
        {
            var pumpFactory = new ScriptedPumpFactory();
            Assert.IsTrue(TryCreateCore(new FakeLease(), pumpFactory,
                out Switch2ProUsbRuntimeOwner owner, out _, out var created),
                created.Kind.ToString());
            var table = new InputControllerRegistrationTable(1);
            RecordingParticipant participant = null;
            var coordinator =
                new Switch2ProUsbRuntimeRegistrationCoordinator(table, 5_000,
                    exactOwner => participant = new RecordingParticipant(
                        new Switch2ProUsbRuntimeRegistrationParticipant(
                            exactOwner), overrideAdoption: true, forged));
            Assert.IsTrue(coordinator.TryOpen(23, out _));

            Assert.IsFalse(coordinator.TryAttach(owner,
                static (_, _, _) => { }, 2_000,
                out InputControllerSlotToken token, out var failure));
            Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
                QuarantineRequired, failure.Kind);
            Assert.IsTrue(failure.RequiresQuarantine);
            CollectionAssert.AreEqual(new[]
            {
                Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot,
            }, participant.Operations);
            InputControllerSlotSnapshot snapshot =
                table.GetSnapshot()[token.Slot];
            Assert.AreEqual(InputControllerSlotState.Quarantined,
                snapshot.State);
            Assert.AreEqual(InputControllerSlotQuarantineReason.
                ExternalLifecycleFailure, snapshot.QuarantineReason);
            Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Created,
                owner.State,
                "Invalid evidence must not be mistaken for adoption proof.");
        }
    }

    [TestMethod]
    public void CoordinatorRejectsWrongPrepareTagAndExactlyCleansAdoption()
    {
        Switch2RuntimeRegistrationParticipantResult[] forgedResults =
        {
            default,
            Switch2RuntimeRegistrationParticipantResult.Success(
                Switch2RuntimeRegistrationParticipantOperation.
                    CommitPrepared),
        };
        foreach (Switch2RuntimeRegistrationParticipantResult forged in
            forgedResults)
        {
            var pumpFactory = new ScriptedPumpFactory();
            Assert.IsTrue(TryCreateCore(new FakeLease(), pumpFactory,
                out Switch2ProUsbRuntimeOwner owner, out _, out var created),
                created.Kind.ToString());
            var table = new InputControllerRegistrationTable(1);
            RecordingParticipant participant = null;
            var coordinator =
                new Switch2ProUsbRuntimeRegistrationCoordinator(table, 5_000,
                    exactOwner => participant = new RecordingParticipant(
                        new Switch2ProUsbRuntimeRegistrationParticipant(
                            exactOwner), overridePrepare: true,
                        prepareResult: forged));
            Assert.IsTrue(coordinator.TryOpen(29, out _));

            Assert.IsFalse(coordinator.TryAttach(owner,
                static (_, _, _) => { }, 2_000,
                out InputControllerSlotToken token, out var failure));
            Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
                DependencyThrew, failure.Kind);
            CollectionAssert.AreEqual(new[]
            {
                Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot,
                Switch2RuntimeRegistrationParticipantOperation.Subscribe,
                Switch2RuntimeRegistrationParticipantOperation.
                    PrepareActivation,
                Switch2RuntimeRegistrationParticipantOperation.
                    AbortUnpublished,
                Switch2RuntimeRegistrationParticipantOperation.Unsubscribe,
            }, participant.Operations);
            Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.
                AbortedUnpublished, owner.State);
            Assert.AreEqual(InputControllerSlotState.Removed,
                table.GetSnapshot()[token.Slot].State,
                "Exact abort and unsubscribe proof permits Bound rollback.");
        }
    }

    private sealed class RecordingParticipant :
        ISwitch2RuntimeRegistrationParticipant
    {
        private readonly ISwitch2RuntimeRegistrationParticipant inner;
        private readonly bool overrideAdoption;
        private readonly Switch2RuntimeRegistrationParticipantResult
            adoptionResult;
        private readonly bool overridePrepare;
        private readonly Switch2RuntimeRegistrationParticipantResult
            prepareResult;

        internal RecordingParticipant(
            ISwitch2RuntimeRegistrationParticipant inner,
            bool overrideAdoption = false,
            Switch2RuntimeRegistrationParticipantResult adoptionResult =
                default,
            bool overridePrepare = false,
            Switch2RuntimeRegistrationParticipantResult prepareResult =
                default)
        {
            this.inner = inner;
            this.overrideAdoption = overrideAdoption;
            this.adoptionResult = adoptionResult;
            this.overridePrepare = overridePrepare;
            this.prepareResult = prepareResult;
        }

        internal List<Switch2RuntimeRegistrationParticipantOperation>
            Operations { get; } = new();

        public InputControllerRegistration Registration =>
            inner.Registration;

        public Switch2RuntimeRegistrationParticipantResult TryAdoptBoundSlot(
            in InputControllerSlotToken token)
        {
            Operations.Add(Switch2RuntimeRegistrationParticipantOperation.
                AdoptBoundSlot);
            return overrideAdoption ? adoptionResult :
                inner.TryAdoptBoundSlot(token);
        }

        public Switch2RuntimeRegistrationParticipantResult TrySubscribe(
            in Switch2RuntimeRegistrationCallbacks callbacks)
        {
            Operations.Add(Switch2RuntimeRegistrationParticipantOperation.
                Subscribe);
            return inner.TrySubscribe(callbacks);
        }

        public Switch2RuntimeRegistrationParticipantResult
            TryPrepareActivation(int timeoutMilliseconds)
        {
            Operations.Add(Switch2RuntimeRegistrationParticipantOperation.
                PrepareActivation);
            return overridePrepare ? prepareResult :
                inner.TryPrepareActivation(timeoutMilliseconds);
        }

        public Switch2RuntimeRegistrationParticipantResult TryCommitPrepared(
            in InputControllerActivationCommitCredential activationCommit)
        {
            Operations.Add(Switch2RuntimeRegistrationParticipantOperation.
                CommitPrepared);
            return inner.TryCommitPrepared(activationCommit);
        }

        public Switch2RuntimeRegistrationParticipantResult TryAbortPrepared(
            int timeoutMilliseconds)
        {
            Operations.Add(Switch2RuntimeRegistrationParticipantOperation.
                AbortPrepared);
            return inner.TryAbortPrepared(timeoutMilliseconds);
        }

        public Switch2RuntimeRegistrationParticipantResult
            TryAbortUnpublished(int timeoutMilliseconds)
        {
            Operations.Add(Switch2RuntimeRegistrationParticipantOperation.
                AbortUnpublished);
            return inner.TryAbortUnpublished(timeoutMilliseconds);
        }

        public Switch2RuntimeRegistrationParticipantResult TryArmRetirement(
            in InputControllerRetirementClaim claim)
        {
            Operations.Add(Switch2RuntimeRegistrationParticipantOperation.
                ArmRetirement);
            return inner.TryArmRetirement(claim);
        }

        public Switch2RuntimeRegistrationParticipantResult
            TryWaitForPublicationAvailability(int timeoutMilliseconds)
        {
            Operations.Add(Switch2RuntimeRegistrationParticipantOperation.
                WaitForPublicationAvailability);
            return inner.TryWaitForPublicationAvailability(
                timeoutMilliseconds);
        }

        public Switch2RuntimeRegistrationParticipantResult TryStopAndQuiesce(
            int timeoutMilliseconds)
        {
            Operations.Add(Switch2RuntimeRegistrationParticipantOperation.
                StopAndQuiesce);
            return inner.TryStopAndQuiesce(timeoutMilliseconds);
        }

        public Switch2RuntimeRegistrationParticipantResult TryUnsubscribe()
        {
            Operations.Add(Switch2RuntimeRegistrationParticipantOperation.
                Unsubscribe);
            return inner.TryUnsubscribe();
        }

        public Switch2RuntimeRegistrationParticipantResult TryRemove()
        {
            Operations.Add(Switch2RuntimeRegistrationParticipantOperation.
                Remove);
            return inner.TryRemove();
        }
    }

    private static void IgnoreParticipantAttention(
        in Switch2RuntimeRegistrationLifecycleAttention attention)
    {
    }

    private static void AssertParticipantSuccess(
        in Switch2RuntimeRegistrationParticipantResult result)
    {
        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.Succeeded,
            $"{result.Operation}: {result.Outcome}/{result.FailureKind} " +
            $"owner={result.OwnerFailure} quarantine={result.QuarantineReason}");
        Assert.IsFalse(result.RequiresQuarantine);
    }
}
