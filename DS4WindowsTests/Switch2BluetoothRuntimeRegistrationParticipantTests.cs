using System.Buffers.Binary;
using DS4Windows;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothRuntimeRegistrationParticipantTests
{
    private const ulong DeviceGeneration = 7_401;
    private const ulong TransportGeneration = 7_607;
    private const long QpcFrequency = 10_000_000;
    private const int LifecycleTimeoutMilliseconds = 2_000;

    [TestMethod]
    public void ExactLifecycleRejectsForeignCapabilitiesAndDeliversOneTerminal()
    {
        TestLease lease = CreateLease(Switch2ControllerModel.ProController2,
            scanGeneration: 701);
        lease.InlineNotificationCount = 1;
        CreateOwner(lease, DeviceGeneration, out var owner,
            out var registration);
        var participant =
            new Switch2BluetoothRuntimeRegistrationParticipant(owner);
        Assert.AreEqual(registration, participant.Registration);

        InputControllerRegistrationTable table = OpenTable(1, 901);
        InputControllerRegistrationTable foreignTable = OpenTable(1, 902);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out _, out var tableFailure), tableFailure.ToString());
        Assert.IsTrue(foreignTable.TryReserveAndBind(registration,
            out var foreignToken, out _, out var foreignFailure),
            foreignFailure.ToString());

        AssertParticipantSuccess(participant.TryAdoptBoundSlot(token));
        InputControllerSlotToken copiedToken = token;
        AssertParticipantSuccess(participant.TryAdoptBoundSlot(copiedToken));
        Switch2RuntimeRegistrationParticipantResult foreignAdoption =
            participant.TryAdoptBoundSlot(foreignToken);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            StaleCredential, foreignAdoption.FailureKind);
        Assert.IsTrue(participant.HasAdoptedSlot);

        InputControllerRetirementClaim retirement = default;
        string callbackFailure = null;
        int regularCount = 0;
        int terminalCount = 0;
        Switch2RuntimeRegistrationLifecycleAttention observedAttention =
            default;
        using ManualResetEventSlim regularSeen = new(false);
        using ManualResetEventSlim attentionSeen = new(false);
        DS4Device.ReportHandler<EventArgs> report = (sender, args) =>
        {
            if (args is not Switch2RuntimeReportEventArgs runtimeReport ||
                runtimeReport.RuntimeGeneration != registration.Generation)
            {
                callbackFailure = "wrong report envelope";
                return;
            }
            if (runtimeReport.Kind == Switch2RuntimeReportKind.Regular)
            {
                if (!table.TryAcquireReportLease(token, sender,
                        out var reportLease, out var leaseFailure))
                {
                    callbackFailure = leaseFailure.ToString();
                }
                else
                {
                    reportLease.Dispose();
                    Interlocked.Increment(ref regularCount);
                }
                regularSeen.Set();
                return;
            }

            InputControllerSlotTableFailure terminalFailure = default;
            InputControllerReportLease terminalLease = default;
            if (!retirement.IsValid ||
                !table.TryAcquireTerminalReportLease(retirement, sender,
                    out terminalLease, out terminalFailure))
            {
                callbackFailure = retirement.IsValid ?
                    terminalFailure.ToString() : "missing retirement claim";
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
        Switch2RuntimeRegistrationLifecycleAttentionCallback attention =
            (in Switch2RuntimeRegistrationLifecycleAttention evidence) =>
            {
                observedAttention = evidence;
                attentionSeen.Set();
            };
        var callbacks = new Switch2RuntimeRegistrationCallbacks(report,
            attention);
        AssertParticipantSuccess(participant.TrySubscribe(callbacks));
        AssertParticipantSuccess(participant.TrySubscribe(callbacks));
        var differentCallbacks = new Switch2RuntimeRegistrationCallbacks(
            static (_, _) => { }, attention);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            InvalidCredential,
            participant.TrySubscribe(differentCallbacks).FailureKind);

        AssertParticipantSuccess(participant.TryPrepareActivation(1_000));
        Assert.IsTrue(participant.HasPreparedCredential);

        Assert.IsTrue(foreignTable.TryBeginActivate(foreignToken,
            out var foreignActivation, out foreignFailure),
            foreignFailure.ToString());
        Assert.IsTrue(foreignTable.TryAcquireActivationCommit(
            foreignActivation, out var foreignCommit, out foreignFailure),
            foreignFailure.ToString());
        Switch2RuntimeRegistrationParticipantResult rejectedCommit =
            participant.TryCommitPrepared(foreignCommit);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            InvalidCredential, rejectedCommit.FailureKind);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Prepared,
            owner.State);

        Assert.IsTrue(table.TryBeginActivate(token, out var activation,
            out tableFailure), tableFailure.ToString());
        Assert.IsTrue(table.TryAcquireActivationCommit(activation,
            out var activationCommit, out tableFailure),
            tableFailure.ToString());
        InputControllerActivationCommitCredential copiedCommit =
            activationCommit;
        AssertParticipantSuccess(participant.TryCommitPrepared(copiedCommit));
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            AlreadyConsumed,
            participant.TryCommitPrepared(activationCommit).FailureKind);
        Assert.IsTrue(table.TryCompleteActivate(activationCommit,
            externalCommitSucceeded: true, out tableFailure),
            tableFailure.ToString());
        Assert.IsTrue(regularSeen.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsNull(callbackFailure);

        lease.Disconnect();
        Assert.IsTrue(attentionSeen.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(observedAttention.IsValid);
        Assert.AreEqual(registration, observedAttention.Registration);
        Assert.AreEqual(Switch2RuntimeRegistrationLifecycleAttentionKind.
            TransportEnded, observedAttention.Kind);

        Assert.IsTrue(table.TryBeginRetire(token, out retirement,
            out tableFailure), tableFailure.ToString());
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            InvalidCredential,
            participant.TryArmRetirement(default).FailureKind);
        var foreignClaim = new InputControllerRetirementClaim(foreignToken);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            StaleCredential,
            participant.TryArmRetirement(foreignClaim).FailureKind);
        AssertParticipantSuccess(participant.TryArmRetirement(retirement));
        InputControllerRetirementClaim copiedRetirement = retirement;
        AssertParticipantSuccess(participant.TryArmRetirement(
            copiedRetirement));
        Assert.IsTrue(table.TryWaitForDrain(retirement, 1_000,
            out tableFailure), tableFailure.ToString());
        AssertParticipantSuccess(participant.
            TryWaitForPublicationAvailability(1_000));
        AssertParticipantSuccess(participant.TryStopAndQuiesce(1_000));
        AssertParticipantSuccess(participant.TryStopAndQuiesce(1_000));
        Assert.IsTrue(owner.LeaseReleaseProven);
        Assert.AreEqual(Switch2BluetoothRuntimeTerminalState.Delivered,
            owner.Sink.TerminalState);
        Assert.AreEqual(1, terminalCount);
        Assert.IsNull(callbackFailure);

        Assert.IsTrue(table.TryWaitForDrain(retirement, 1_000,
            out tableFailure), tableFailure.ToString());
        Assert.IsTrue(table.TryMarkQuiesced(retirement, out tableFailure),
            tableFailure.ToString());
        AssertParticipantSuccess(participant.TryUnsubscribe());
        AssertParticipantSuccess(participant.TryUnsubscribe());
        AssertParticipantSuccess(participant.TryRemove());
        AssertParticipantSuccess(participant.TryRemove());
        Assert.IsTrue(table.TryCompleteRemoval(retirement, out tableFailure),
            tableFailure.ToString());
        Assert.AreEqual(1, regularCount);
        Assert.AreEqual(1, lease.UnsubscribeCount);
        Assert.AreEqual(1, lease.ReleaseWaitCount);
    }

    [TestMethod]
    public void CrossOwnerAndCrossGenerationTokensNeverReachTheOwner()
    {
        TestLease firstLease = CreateLease(
            Switch2ControllerModel.ProController2, 711);
        TestLease secondLease = CreateLease(
            Switch2ControllerModel.ProController2, 712);
        CreateOwner(firstLease, DeviceGeneration, out var first,
            out var firstRegistration);
        CreateOwner(secondLease, DeviceGeneration + 1, out var second,
            out var secondRegistration);
        var firstOperations = new ScriptedOperations(first);
        var participant = new Switch2BluetoothRuntimeRegistrationParticipant(
            first, firstOperations);

        InputControllerRegistrationTable firstTable = OpenTable(1, 911);
        InputControllerRegistrationTable secondTable = OpenTable(1, 912);
        Assert.IsTrue(firstTable.TryReserveAndBind(firstRegistration,
            out var firstToken, out var firstRollback, out _));
        Assert.IsTrue(secondTable.TryReserveAndBind(secondRegistration,
            out var secondToken, out var secondRollback, out _));

        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            InvalidCredential,
            participant.TryAdoptBoundSlot(default).FailureKind);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            InvalidCredential,
            participant.TryAdoptBoundSlot(secondToken).FailureKind);
        Assert.AreEqual(0, firstOperations.AdoptCallCount,
            "Foreign generations must be rejected before the owner seam.");

        AssertParticipantSuccess(participant.TryAdoptBoundSlot(firstToken));
        Assert.AreEqual(1, firstOperations.AdoptCallCount);
        AssertParticipantSuccess(participant.TryAbortUnpublished(1_000));
        AssertParticipantSuccess(participant.TryAbortUnpublished(1_000));
        Assert.IsTrue(firstTable.TryRollback(firstRollback, out _));

        Switch2BluetoothRuntimeSlotAdoptionCredential secondAdoption;
        Assert.IsTrue(second.TryAdoptBoundSlot(secondToken,
            out secondAdoption, out var adoptionFailure),
            adoptionFailure.ToString());
        Assert.IsTrue(second.TryAbortUnpublished(secondAdoption, 1_000,
            out var abortFailure), abortFailure.ToString());
        Assert.IsTrue(secondTable.TryRollback(secondRollback, out _));
    }

    [TestMethod]
    public void PreparedAndCreatedAbortRetainExactProofAndAreIdempotent()
    {
        TestLease preparedLease = CreateLease(
            Switch2ControllerModel.JoyCon2Left, 721);
        CreateOwner(preparedLease, DeviceGeneration, out var preparedOwner,
            out var preparedRegistration);
        var prepared = new Switch2BluetoothRuntimeRegistrationParticipant(
            preparedOwner);
        InputControllerRegistrationTable preparedTable = OpenTable(1, 921);
        Assert.IsTrue(preparedTable.TryReserveAndBind(preparedRegistration,
            out var preparedToken, out var preparedRollback, out _));
        AssertParticipantSuccess(prepared.TryAdoptBoundSlot(preparedToken));
        AssertParticipantSuccess(prepared.TrySubscribe(
            NoopCallbacks()));
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            InvalidTimeout, prepared.TryPrepareActivation(0).FailureKind);
        AssertParticipantSuccess(prepared.TryPrepareActivation(1_000));
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            InvalidTimeout, prepared.TryAbortPrepared(-1).FailureKind);
        AssertParticipantSuccess(prepared.TryAbortPrepared(1_000));
        AssertParticipantSuccess(prepared.TryAbortPrepared(1_000));
        Assert.IsTrue(preparedOwner.LeaseReleaseProven);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.AbortedUnpublished,
            preparedOwner.State);
        AssertParticipantSuccess(prepared.TryUnsubscribe());
        Assert.IsTrue(preparedTable.TryRollback(preparedRollback, out _));

        TestLease createdLease = CreateLease(
            Switch2ControllerModel.JoyCon2Right, 722);
        CreateOwner(createdLease, DeviceGeneration + 2, out var createdOwner,
            out var createdRegistration);
        var created = new Switch2BluetoothRuntimeRegistrationParticipant(
            createdOwner);
        InputControllerRegistrationTable createdTable = OpenTable(1, 922);
        Assert.IsTrue(createdTable.TryReserveAndBind(createdRegistration,
            out var createdToken, out var createdRollback, out _));
        AssertParticipantSuccess(created.TryAdoptBoundSlot(createdToken));
        AssertParticipantSuccess(created.TryAbortUnpublished(1_000));
        AssertParticipantSuccess(created.TryAbortUnpublished(1_000));
        Assert.IsTrue(createdOwner.LeaseReleaseProven);
        Assert.IsTrue(createdTable.TryRollback(createdRollback, out _));
    }

    [TestMethod]
    public void DirectReportDelegateIsExactAndSteadyInvocationAllocatesNothing()
    {
        TestLease lease = CreateLease(Switch2ControllerModel.ProController2,
            731);
        CreateOwner(lease, DeviceGeneration, out var owner,
            out var registration);
        var operations = new ScriptedOperations(owner);
        var participant = new Switch2BluetoothRuntimeRegistrationParticipant(
            owner, operations);
        InputControllerRegistrationTable table = OpenTable(1, 931);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out var rollback, out _));
        AssertParticipantSuccess(participant.TryAdoptBoundSlot(token));

        DS4Device.ReportHandler<EventArgs> report = StaticReport;
        Switch2RuntimeRegistrationLifecycleAttentionCallback attention =
            IgnoreAttention;
        var callbacks = new Switch2RuntimeRegistrationCallbacks(report,
            attention);
        AssertParticipantSuccess(participant.TrySubscribe(callbacks));
        Assert.IsTrue(ReferenceEquals(report, operations.CapturedReport));
        operations.CapturedReport(null, EventArgs.Empty);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            operations.CapturedReport(null, EventArgs.Empty);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated,
            "The participant must not install an allocating report wrapper.");

        AssertParticipantSuccess(participant.TryUnsubscribe());
        Assert.AreEqual(1, operations.RemoveReportCount);
        Assert.AreEqual(1, operations.RemoveAttentionCount);
        AssertParticipantSuccess(participant.TryAbortUnpublished(1_000));
        Assert.IsTrue(table.TryRollback(rollback, out _));
    }

    [TestMethod]
    public void ThrowingSubscriptionAccessorCompensatesButRemainsUncertain()
    {
        TestLease lease = CreateLease(Switch2ControllerModel.ProController2,
            741);
        CreateOwner(lease, DeviceGeneration, out var owner,
            out var registration);
        var operations = new ScriptedOperations(owner)
        {
            ThrowAttentionAfterAdd = true,
        };
        var participant = new Switch2BluetoothRuntimeRegistrationParticipant(
            owner, operations);
        InputControllerRegistrationTable table = OpenTable(1, 941);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out var rollback, out _));
        AssertParticipantSuccess(participant.TryAdoptBoundSlot(token));

        Switch2RuntimeRegistrationParticipantResult result =
            participant.TrySubscribe(NoopCallbacks());
        AssertUncertainQuarantine(result,
            Switch2RuntimeRegistrationParticipantOperation.Subscribe);
        Assert.AreEqual(1, operations.RemoveReportCount);
        Assert.AreEqual(1, operations.RemoveAttentionCount);
        Assert.IsFalse(participant.IsSubscribed);
        AssertUncertainQuarantine(participant.TryUnsubscribe(),
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe);

        // Test-only cleanup cannot convert the prior accessor ambiguity into
        // table rollback proof, but it prevents a parked transport leak.
        AssertParticipantSuccess(participant.TryAbortUnpublished(1_000));
        Assert.IsTrue(table.TryRollback(rollback, out _));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ThrowingOrMalformedAdoptionIsUncertainAndNeverOwned(bool throws)
    {
        TestLease lease = CreateLease(Switch2ControllerModel.ProController2,
            throws ? 751UL : 752UL);
        CreateOwner(lease, DeviceGeneration, out var owner,
            out var registration);
        var operations = new ScriptedOperations(owner)
        {
            ThrowAdopt = throws,
            MalformedAdoptSuccess = !throws,
        };
        var participant = new Switch2BluetoothRuntimeRegistrationParticipant(
            owner, operations);
        InputControllerRegistrationTable table = OpenTable(1,
            throws ? 951UL : 952UL);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out var rollback, out _));

        Switch2RuntimeRegistrationParticipantResult result =
            participant.TryAdoptBoundSlot(token);
        AssertUncertainQuarantine(result,
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        Assert.IsFalse(participant.HasAdoptedSlot);

        DirectAbortCreated(owner, token);
        Assert.IsTrue(table.TryRollback(rollback, out _));
    }

    [TestMethod]
    public void CrossOwnerPrepareCredentialIsRejectedAsUncertainEvidence()
    {
        TestLease firstLease = CreateLease(
            Switch2ControllerModel.ProController2, 761);
        TestLease secondLease = CreateLease(
            Switch2ControllerModel.ProController2, 762);
        CreateOwner(firstLease, DeviceGeneration, out var first,
            out var firstRegistration);
        CreateOwner(secondLease, DeviceGeneration, out var second,
            out var secondRegistration);

        InputControllerRegistrationTable firstTable = OpenTable(1, 961);
        InputControllerRegistrationTable secondTable = OpenTable(1, 962);
        Assert.IsTrue(firstTable.TryReserveAndBind(firstRegistration,
            out var firstToken, out var firstRollback, out _));
        Assert.IsTrue(secondTable.TryReserveAndBind(secondRegistration,
            out var secondToken, out var secondRollback, out _));
        Assert.IsTrue(second.TryAdoptBoundSlot(secondToken,
            out var secondAdoption, out var secondAdoptionFailure),
            secondAdoptionFailure.ToString());
        Assert.IsTrue(second.TryPrepareActivation(secondAdoption, 1_000,
            out var foreignPrepare, out var secondPrepareFailure),
            secondPrepareFailure.ToString());

        var operations = new ScriptedOperations(first)
        {
            ForcePrepareSuccess = true,
            ForcedPrepareCredential = foreignPrepare,
        };
        var participant = new Switch2BluetoothRuntimeRegistrationParticipant(
            first, operations);
        AssertParticipantSuccess(participant.TryAdoptBoundSlot(firstToken));
        AssertParticipantSuccess(participant.TrySubscribe(NoopCallbacks()));
        Switch2RuntimeRegistrationParticipantResult result =
            participant.TryPrepareActivation(1_000);
        AssertUncertainQuarantine(result,
            Switch2RuntimeRegistrationParticipantOperation.
                PrepareActivation);
        Assert.IsFalse(participant.HasPreparedCredential);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Created,
            first.State);

        AssertParticipantSuccess(participant.TryUnsubscribe());
        AssertParticipantSuccess(participant.TryAbortUnpublished(1_000));
        Assert.IsTrue(firstTable.TryRollback(firstRollback, out _));
        Assert.IsTrue(second.TryAbortPrepared(foreignPrepare, 1_000,
            out var abortFailure), abortFailure.ToString());
        Assert.IsTrue(secondTable.TryRollback(secondRollback, out _));
    }

    [TestMethod]
    public void StructurallyValidForeignAdoptionCredentialFailsOwnerAttestation()
    {
        const ulong scanGeneration = 766;
        TestLease firstLease = CreateLease(
            Switch2ControllerModel.ProController2, scanGeneration);
        TestLease secondLease = CreateLease(
            Switch2ControllerModel.ProController2, scanGeneration + 1);
        CreateOwner(firstLease, DeviceGeneration, out var first,
            out var firstRegistration);
        CreateOwner(secondLease, DeviceGeneration, out var second,
            out var secondRegistration);
        InputControllerRegistrationTable firstTable = OpenTable(1, 966);
        InputControllerRegistrationTable secondTable = OpenTable(1, 967);
        Assert.IsTrue(firstTable.TryReserveAndBind(firstRegistration,
            out var firstToken, out var firstRollback, out _));
        Assert.IsTrue(secondTable.TryReserveAndBind(secondRegistration,
            out var secondToken, out var secondRollback, out _));

        // This value passes every public-field comparison, but its private
        // issuer is the foreign owner. The participant's exact owner retry
        // must detect that mismatch before retaining it.
        var forged = new Switch2BluetoothRuntimeSlotAdoptionCredential(
            second, new object(), firstToken,
            Switch2ControllerModel.ProController2, scanGeneration,
            DeviceGeneration, TransportGeneration);
        Assert.IsTrue(forged.IsValid);
        var operations = new ScriptedOperations(first)
        {
            ForceAdoptSuccess = true,
            ForcedAdoptionCredential = forged,
        };
        var participant = new Switch2BluetoothRuntimeRegistrationParticipant(
            first, operations);
        Switch2RuntimeRegistrationParticipantResult result =
            participant.TryAdoptBoundSlot(firstToken);
        AssertUncertainQuarantine(result,
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        Assert.IsFalse(participant.HasAdoptedSlot);

        DirectAbortCreated(first, firstToken);
        DirectAbortCreated(second, secondToken);
        Assert.IsTrue(firstTable.TryRollback(firstRollback, out _));
        Assert.IsTrue(secondTable.TryRollback(secondRollback, out _));
    }

    [TestMethod]
    public void PostCommitThrowCannotBeRetriedOrMisreportedAsRejection()
    {
        TestLease lease = CreateLease(Switch2ControllerModel.ProController2,
            771);
        CreateOwner(lease, DeviceGeneration, out var owner,
            out var registration);
        var operations = new ScriptedOperations(owner)
        {
            ThrowCommitAfterOwnerCall = true,
        };
        var participant = new Switch2BluetoothRuntimeRegistrationParticipant(
            owner, operations);
        InputControllerRegistrationTable table = OpenTable(1, 971);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out _, out _));
        AssertParticipantSuccess(participant.TryAdoptBoundSlot(token));

        InputControllerRetirementClaim retirement = default;
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind !=
                    Switch2RuntimeReportKind.TerminalNeutral ||
                !retirement.IsValid)
            {
                return;
            }
            if (table.TryAcquireTerminalReportLease(retirement, sender,
                    out var terminalLease, out _))
            {
                terminalLease.TryAcknowledgeTerminalNeutral(out _);
                terminalLease.Dispose();
            }
        };
        AssertParticipantSuccess(participant.TrySubscribe(NoopCallbacks()));
        AssertParticipantSuccess(participant.TryPrepareActivation(1_000));
        Assert.IsTrue(table.TryBeginActivate(token, out var activation,
            out _));
        Assert.IsTrue(table.TryAcquireActivationCommit(activation,
            out var commit, out _));

        Switch2RuntimeRegistrationParticipantResult result =
            participant.TryCommitPrepared(commit);
        AssertUncertainQuarantine(result,
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared);
        Assert.IsFalse(participant.HasPreparedCredential,
            "A possibly consumed native credential must never be retried.");
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Active, owner.State);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            InvalidState, participant.TryCommitPrepared(commit).FailureKind);

        Assert.IsTrue(table.TryCompleteActivate(commit,
            externalCommitSucceeded: true, out _));
        Assert.IsTrue(table.TryBeginRetire(token, out retirement, out _));
        Assert.IsTrue(owner.TryArmRetirement(retirement, out var armFailure),
            armFailure.ToString());
        Assert.IsTrue(registration.TryStopAndQuiesce(1_000,
            out var stopFailure), $"{stopFailure}: {owner.LastStopFailure.Kind}");
        AssertParticipantSuccess(participant.TryUnsubscribe());
        Assert.IsTrue(table.TryWaitForDrain(retirement, 1_000, out _));
        Assert.IsTrue(table.TryMarkQuiesced(retirement, out _));
        Assert.IsTrue(registration.TryRemove(out var removeFailure),
            removeFailure.ToString());
        Assert.IsTrue(table.TryCompleteRemoval(retirement, out _));
    }

    [TestMethod]
    public void FalseStopOrRemoveSuccessCannotOutrunExactOwnerProof()
    {
        TestLease lease = CreateLease(Switch2ControllerModel.ProController2,
            781);
        CreateOwner(lease, DeviceGeneration, out var owner,
            out var registration);
        var operations = new ScriptedOperations(owner);
        var participant = new Switch2BluetoothRuntimeRegistrationParticipant(
            owner, operations);
        InputControllerRegistrationTable table = OpenTable(1, 981);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out _, out _));
        AssertParticipantSuccess(participant.TryAdoptBoundSlot(token));

        InputControllerRetirementClaim retirement = default;
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                    Switch2RuntimeReportKind.TerminalNeutral &&
                table.TryAcquireTerminalReportLease(retirement, sender,
                    out var terminalLease, out _))
            {
                terminalLease.TryAcknowledgeTerminalNeutral(out _);
                terminalLease.Dispose();
            }
        };
        AssertParticipantSuccess(participant.TrySubscribe(NoopCallbacks()));
        AssertParticipantSuccess(participant.TryPrepareActivation(1_000));
        Assert.IsTrue(table.TryBeginActivate(token, out var activation,
            out _));
        Assert.IsTrue(table.TryAcquireActivationCommit(activation,
            out var commit, out _));
        AssertParticipantSuccess(participant.TryCommitPrepared(commit));
        Assert.IsTrue(table.TryCompleteActivate(commit, true, out _));
        Assert.IsTrue(table.TryBeginRetire(token, out retirement, out _));
        AssertParticipantSuccess(participant.TryArmRetirement(retirement));
        AssertParticipantSuccess(participant.
            TryWaitForPublicationAvailability(1_000));

        operations.LieStopSuccess = true;
        Switch2RuntimeRegistrationParticipantResult falseStop =
            participant.TryStopAndQuiesce(1_000);
        AssertUncertainQuarantine(falseStop,
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Active, owner.State);
        operations.LieStopSuccess = false;
        operations.MalformedStopFailure = true;
        AssertUncertainQuarantine(participant.TryStopAndQuiesce(1_000),
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Active, owner.State);
        operations.MalformedStopFailure = false;
        AssertParticipantSuccess(participant.TryStopAndQuiesce(1_000));
        AssertParticipantSuccess(participant.TryUnsubscribe());
        Assert.IsTrue(table.TryWaitForDrain(retirement, 1_000, out _));
        Assert.IsTrue(table.TryMarkQuiesced(retirement, out _));

        operations.LieRemoveSuccess = true;
        Switch2RuntimeRegistrationParticipantResult falseRemove =
            participant.TryRemove();
        AssertUncertainQuarantine(falseRemove,
            Switch2RuntimeRegistrationParticipantOperation.Remove);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Stopped,
            owner.State);
        operations.LieRemoveSuccess = false;
        operations.MalformedRemoveFailure = true;
        AssertUncertainQuarantine(participant.TryRemove(),
            Switch2RuntimeRegistrationParticipantOperation.Remove);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Stopped,
            owner.State);
        operations.MalformedRemoveFailure = false;
        AssertParticipantSuccess(participant.TryRemove());
        Assert.IsTrue(table.TryCompleteRemoval(retirement, out _));
    }

    private static void StaticReport(DS4Device _, EventArgs __)
    {
    }

    private static void IgnoreAttention(
        in Switch2RuntimeRegistrationLifecycleAttention _)
    {
    }

    private static Switch2RuntimeRegistrationCallbacks NoopCallbacks() =>
        new(StaticReport, IgnoreAttention);

    private static void AssertParticipantSuccess(
        Switch2RuntimeRegistrationParticipantResult result)
    {
        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.Succeeded,
            $"{result.Operation}: {result.Outcome}/{result.FailureKind}");
        Assert.IsFalse(result.RequiresQuarantine);
    }

    private static void AssertUncertainQuarantine(
        Switch2RuntimeRegistrationParticipantResult result,
        Switch2RuntimeRegistrationParticipantOperation operation)
    {
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(operation, result.Operation);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantOutcome.
            OutcomeUncertain, result.Outcome);
        Assert.IsTrue(result.RequiresQuarantine);
    }

    private static InputControllerRegistrationTable OpenTable(int slots,
        ulong generation)
    {
        var table = new InputControllerRegistrationTable(slots);
        Assert.IsTrue(table.TryOpen(generation, out var failure),
            failure.ToString());
        return table;
    }

    private static void DirectAbortCreated(Switch2BluetoothRuntimeOwner owner,
        in InputControllerSlotToken token)
    {
        Assert.IsTrue(owner.TryAdoptBoundSlot(token, out var adoption,
            out var adoptionFailure), adoptionFailure.ToString());
        Assert.IsTrue(owner.TryAbortUnpublished(adoption, 1_000,
            out var abortFailure), abortFailure.ToString());
    }

    private static void CreateOwner(TestLease lease, ulong deviceGeneration,
        out Switch2BluetoothRuntimeOwner owner,
        out InputControllerRegistration registration)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            lease.Admission.Model, deviceGeneration, out var calibration));
        Assert.IsTrue(Switch2BluetoothRuntimeOwner.TryCreateCore(
            lease.Admission, lease, deviceGeneration, TransportGeneration,
            QpcFrequency, calibration, queueCapacity: 4,
            LifecycleTimeoutMilliseconds,
            Switch2BluetoothRuntimeDrainPumpFactory.Instance,
            Switch2RuntimeTerminalScheduler.Instance, out owner,
            out registration, out var failure), failure.Kind.ToString());
    }

    private static TestLease CreateLease(Switch2ControllerModel model,
        ulong scanGeneration)
    {
        ushort productId = model switch
        {
            Switch2ControllerModel.ProController2 =>
                Switch2AdvertisementCodec.ProController2ProductId,
            Switch2ControllerModel.JoyCon2Left =>
                Switch2AdvertisementCodec.JoyCon2LeftProductId,
            _ => Switch2AdvertisementCodec.JoyCon2RightProductId,
        };
        var admission = new Switch2BluetoothConnectionAdmission(
            scanGeneration, model, productId);
        const Switch2GattProperty properties = Switch2GattProperty.Read |
            Switch2GattProperty.Notify;
        var gatt = new Switch2BluetoothGattSnapshot(scanGeneration, 1, 1,
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid, properties);
        return new TestLease(admission, gatt);
    }

    private static byte[] Body(uint counter)
    {
        byte[] body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
        return body;
    }

    private sealed class TestLease : ISwitch2BluetoothInputLease,
        ISwitch2BluetoothInputLeaseReleaseProof
    {
        private Switch2BluetoothInputNotification notification;
        private Switch2BluetoothInputDisconnected disconnected;

        internal TestLease(in Switch2BluetoothConnectionAdmission admission,
            in Switch2BluetoothGattSnapshot gatt)
        {
            Admission = admission;
            GattSnapshot = gatt;
        }

        public Switch2BluetoothConnectionAdmission Admission { get; }

        public Switch2BluetoothGattSnapshot GattSnapshot { get; }

        internal int InlineNotificationCount { get; set; }

        internal int UnsubscribeCount { get; private set; }

        internal int ReleaseWaitCount { get; private set; }

        public bool TrySubscribeCccdNotify(ulong transportGeneration,
            Switch2BluetoothInputNotification notification,
            Switch2BluetoothInputDisconnected disconnected)
        {
            this.notification = notification;
            this.disconnected = disconnected;
            for (int index = 0; index < InlineNotificationCount; index++)
            {
                notification(transportGeneration,
                    Switch2InputCodec.ServiceUuid,
                    Switch2InputCodec.Common05CharacteristicUuid,
                    Body((uint)index + 1), index + 1);
            }
            return true;
        }

        public bool TryUnsubscribeCccdNone(ulong transportGeneration)
        {
            UnsubscribeCount++;
            return transportGeneration == TransportGeneration;
        }

        public Switch2BluetoothInputLeaseReleaseResult WaitForRelease(
            ulong transportGeneration, int timeoutMilliseconds)
        {
            ReleaseWaitCount++;
            return transportGeneration == TransportGeneration &&
                    timeoutMilliseconds >= 0 ?
                Switch2BluetoothInputLeaseReleaseResult.Released :
                Switch2BluetoothInputLeaseReleaseResult.Rejected;
        }

        internal void Disconnect() => disconnected?.Invoke(
            TransportGeneration);
    }

    private sealed class ScriptedOperations :
        ISwitch2BluetoothRuntimeRegistrationParticipantOperations
    {
        private readonly
            Switch2BluetoothRuntimeRegistrationParticipantOperations inner;

        internal ScriptedOperations(Switch2BluetoothRuntimeOwner owner)
        {
            Owner = owner;
            inner = new
                Switch2BluetoothRuntimeRegistrationParticipantOperations(
                    owner);
        }

        public Switch2BluetoothRuntimeOwner Owner { get; }

        internal bool ThrowAdopt { get; set; }

        internal bool MalformedAdoptSuccess { get; set; }

        internal bool ForceAdoptSuccess { get; set; }

        internal Switch2BluetoothRuntimeSlotAdoptionCredential
            ForcedAdoptionCredential { get; set; }

        internal bool ThrowAttentionAfterAdd { get; set; }

        internal bool ForcePrepareSuccess { get; set; }

        internal Switch2BluetoothRuntimePrepareCredential
            ForcedPrepareCredential { get; set; }

        internal bool ThrowCommitAfterOwnerCall { get; set; }

        internal bool LieStopSuccess { get; set; }

        internal bool MalformedStopFailure { get; set; }

        internal bool LieRemoveSuccess { get; set; }

        internal bool MalformedRemoveFailure { get; set; }

        internal int AdoptCallCount { get; private set; }

        internal int RemoveReportCount { get; private set; }

        internal int RemoveAttentionCount { get; private set; }

        internal DS4Device.ReportHandler<EventArgs> CapturedReport
        { get; private set; }

        public bool TryAdoptBoundSlot(in InputControllerSlotToken token,
            out Switch2BluetoothRuntimeSlotAdoptionCredential credential,
            out Switch2BluetoothRuntimeSlotAdoptionFailure failure)
        {
            AdoptCallCount++;
            if (ThrowAdopt)
            {
                throw new InvalidOperationException("synthetic adoption");
            }
            if (MalformedAdoptSuccess)
            {
                credential = default;
                failure = default;
                return true;
            }
            if (ForceAdoptSuccess)
            {
                credential = ForcedAdoptionCredential;
                failure = default;
                return true;
            }
            return inner.TryAdoptBoundSlot(token, out credential,
                out failure);
        }

        public void AddReport(DS4Device.ReportHandler<EventArgs> handler)
        {
            CapturedReport = handler;
            inner.AddReport(handler);
        }

        public void RemoveReport(DS4Device.ReportHandler<EventArgs> handler)
        {
            RemoveReportCount++;
            inner.RemoveReport(handler);
        }

        public void AddAttention(EventHandler<
            Switch2BluetoothRuntimeLifecycleAttentionEventArgs> handler)
        {
            inner.AddAttention(handler);
            if (ThrowAttentionAfterAdd)
            {
                throw new InvalidOperationException("synthetic attention add");
            }
        }

        public void RemoveAttention(EventHandler<
            Switch2BluetoothRuntimeLifecycleAttentionEventArgs> handler)
        {
            RemoveAttentionCount++;
            inner.RemoveAttention(handler);
        }

        public bool TryPrepareActivation(
            in Switch2BluetoothRuntimeSlotAdoptionCredential adoptionCredential,
            int timeoutMilliseconds,
            out Switch2BluetoothRuntimePrepareCredential credential,
            out Switch2BluetoothRuntimePrepareFailure failure)
        {
            if (ForcePrepareSuccess)
            {
                credential = ForcedPrepareCredential;
                failure = default;
                return true;
            }
            return inner.TryPrepareActivation(adoptionCredential,
                timeoutMilliseconds, out credential, out failure);
        }

        public bool TryCommitPrepared(
            in Switch2BluetoothRuntimePrepareCredential prepareCredential,
            in InputControllerActivationCommitCredential activationCommit,
            out Switch2BluetoothRuntimeCommitFailure failure)
        {
            bool result = inner.TryCommitPrepared(prepareCredential,
                activationCommit, out failure);
            if (ThrowCommitAfterOwnerCall)
            {
                throw new InvalidOperationException("synthetic post-commit");
            }
            return result;
        }

        public bool TryAbortPrepared(
            in Switch2BluetoothRuntimePrepareCredential prepareCredential,
            int timeoutMilliseconds,
            out Switch2BluetoothRuntimeAbortFailure failure) =>
            inner.TryAbortPrepared(prepareCredential, timeoutMilliseconds,
                out failure);

        public bool TryAbortUnpublished(
            in Switch2BluetoothRuntimeSlotAdoptionCredential adoptionCredential,
            int timeoutMilliseconds,
            out Switch2BluetoothRuntimeAbortFailure failure) =>
            inner.TryAbortUnpublished(adoptionCredential,
                timeoutMilliseconds, out failure);

        public bool TryArmRetirement(in InputControllerRetirementClaim claim,
            out Switch2BluetoothRuntimeRetirementArmFailure failure) =>
            inner.TryArmRetirement(claim, out failure);

        public bool TryWaitForPublicationAvailability(
            int timeoutMilliseconds) =>
            inner.TryWaitForPublicationAvailability(timeoutMilliseconds);

        public bool TryStopAndQuiesce(int timeoutMilliseconds,
            out InputControllerOwnerOperationFailure failure)
        {
            if (LieStopSuccess)
            {
                failure = default;
                return true;
            }
            if (MalformedStopFailure)
            {
                failure = (InputControllerOwnerOperationFailure)byte.MaxValue;
                return false;
            }
            return inner.TryStopAndQuiesce(timeoutMilliseconds, out failure);
        }

        public Switch2BluetoothRuntimeStopFailure LastStopFailure =>
            inner.LastStopFailure;

        public bool TryRemove(
            out InputControllerOwnerOperationFailure failure)
        {
            if (LieRemoveSuccess)
            {
                failure = default;
                return true;
            }
            if (MalformedRemoveFailure)
            {
                failure = (InputControllerOwnerOperationFailure)byte.MaxValue;
                return false;
            }
            return inner.TryRemove(out failure);
        }
    }
}
