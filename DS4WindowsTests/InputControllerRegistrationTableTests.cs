using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class InputControllerRegistrationTableTests
{
    [TestMethod]
    public void ColdActionCaptureRequiresExactAttachedSenderWithoutOwnerCallback()
    {
        var table = new InputControllerRegistrationTable(1);
        var fixture = CreateRegistration(91_000, 91_100);
        Assert.IsFalse(table.TryCaptureAttachedToken(0, fixture.Device, out _, out var failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Closed, failure);
        Assert.IsTrue(table.TryOpen(1, out failure));
        Assert.IsTrue(table.TryReserveAndBind(fixture.Registration, out var token, out _, out failure));
        Assert.IsFalse(table.TryCaptureAttachedToken(0, fixture.Device, out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.WrongState, failure);
        Assert.IsTrue(table.TryActivate(token, out failure));
        fixture.Owner.ThrowOnAuthenticate = true;
        Assert.IsTrue(table.TryCaptureAttachedToken(0, fixture.Device, out var captured, out failure));
        Assert.AreEqual(token, captured);
        Assert.IsFalse(table.TryCaptureAttachedToken(0,
            CreateRegistration(91_001, 91_101).Device, out captured, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.WrongSender, failure);
        Assert.IsFalse(captured.IsValid);
        Assert.IsFalse(table.TryCaptureAttachedToken(-1, fixture.Device, out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.InvalidArgument, failure);
        Assert.IsFalse(table.TryCaptureAttachedToken(0, null, out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.InvalidArgument, failure);
    }

    [TestMethod]
    public void ColdCapturedTokenCannotAcquireActionAfterSlotReuse()
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out var failure));
        var first = CreateRegistration(91_002, 91_102);
        var token = Attach(table, first);
        Assert.IsTrue(table.TryCaptureAttachedToken(0, first.Device, out var captured, out failure));
        Assert.IsTrue(table.TryBeginRetire(token, out var claim, out failure));
        Assert.IsFalse(table.TryCaptureAttachedToken(0, first.Device, out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.WrongState, failure);
        CompleteRetirement(table, claim, first.Device);
        var next = CreateRegistration(91_003, 91_103);
        var nextToken = Attach(table, next);
        Assert.AreNotEqual(captured, nextToken);
        Assert.IsFalse(table.TryAcquireActionLease(captured, 0, out var staleLease, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential, failure);
        Assert.IsFalse(staleLease.IsValid);
        Assert.IsFalse(table.TryCaptureAttachedToken(0, first.Device, out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.WrongSender, failure);
        Assert.IsTrue(table.TryCaptureAttachedToken(0, next.Device, out var nextCapture, out failure));
        Assert.AreEqual(nextToken, nextCapture);
    }

    [TestMethod]
    public void CapturingAnActionTokenDoesNotPauseReportsOrAllocate()
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out var failure));
        var fixture = CreateRegistration(91_004, 91_104);
        var token = Attach(table, fixture);
        for (int index = 0; index < 1000; index++)
            Assert.IsTrue(table.TryCaptureAttachedToken(0, fixture.Device, out _, out _));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allAccepted = true;
        for (int index = 0; index < 100_000; index++)
            allAccepted &= table.TryCaptureAttachedToken(0, fixture.Device, out _, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allAccepted);
        Assert.AreEqual(0L, allocated);
        Assert.IsTrue(table.TryAcquireReportLease(token, fixture.Device, out var report, out failure));
        Assert.IsTrue(table.TryCaptureAttachedToken(0, fixture.Device, out _, out failure));
        report.Dispose();
        Assert.IsFalse(table.GetSnapshot()[0].ActionActive);
        Assert.IsFalse(table.GetSnapshot()[0].ActionPending);
    }

    [TestMethod]
    public void AtomicReserveAndBindPublishesOnlyAnExactBoundClaim()
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out var failure), failure.ToString());
        RegistrationFixture fixture = CreateRegistration(9_000, 9_100);

        Assert.IsTrue(table.TryReserveAndBind(fixture.Registration,
            out InputControllerSlotToken token,
            out InputControllerSetupRollbackClaim rollbackClaim,
            out failure), failure.ToString());
        InputControllerSlotSnapshot snapshot = table.GetSnapshot().Single();
        Assert.AreEqual(InputControllerSlotState.Bound, snapshot.State);
        Assert.AreEqual(token, snapshot.Token);
        Assert.AreEqual(token, rollbackClaim.Token);
        Assert.AreEqual(rollbackClaim, snapshot.SetupRollbackClaim);

        Assert.IsFalse(table.TryReserveAndBind(fixture.Registration,
            out InputControllerSlotToken duplicateToken,
            out InputControllerSetupRollbackClaim duplicateClaim,
            out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            DuplicateRegistration, failure);
        Assert.IsFalse(duplicateToken.IsValid);
        Assert.IsFalse(duplicateClaim.IsValid);
        Assert.AreEqual(InputControllerSlotState.Bound,
            table.GetSnapshot().Single().State);
        Assert.IsTrue(table.TryRollback(rollbackClaim, out failure),
            failure.ToString());
    }

    [TestMethod]
    public void ExactSlotReserveAndBindNeverFallsBackOrMutatesOnRejection()
    {
        var table = new InputControllerRegistrationTable(3);
        Assert.IsTrue(table.TryOpen(1, out var failure), failure.ToString());
        RegistrationFixture first = CreateRegistration(9_030, 9_130);
        RegistrationFixture second = CreateRegistration(9_031, 9_131);

        Assert.IsTrue(table.TryReserveAndBindExactSlot(2,
            first.Registration, out InputControllerSlotToken firstToken,
            out InputControllerSetupRollbackClaim firstRollback,
            out failure), failure.ToString());
        Assert.AreEqual(2, firstToken.Slot);
        Assert.AreEqual(firstToken, firstRollback.Token);
        InputControllerSlotSnapshot[] before = table.GetSnapshot();

        Assert.IsFalse(table.TryReserveAndBindExactSlot(2,
            second.Registration, out InputControllerSlotToken rejectedToken,
            out InputControllerSetupRollbackClaim rejectedRollback,
            out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Busy, failure);
        Assert.IsFalse(rejectedToken.IsValid);
        Assert.IsFalse(rejectedRollback.IsValid);
        InputControllerSlotSnapshot[] after = table.GetSnapshot();
        Assert.AreEqual(InputControllerSlotState.Empty, after[0].State);
        Assert.AreEqual(InputControllerSlotState.Empty, after[1].State);
        Assert.AreEqual(before[2].Token, after[2].Token);
        Assert.AreEqual(before[2].SlotGeneration,
            after[2].SlotGeneration);

        Assert.IsTrue(table.TryReserveAndBindExactSlot(0,
            second.Registration, out InputControllerSlotToken secondToken,
            out _, out failure), failure.ToString());
        Assert.AreEqual(0, secondToken.Slot);
    }

    [TestMethod]
    public void ExactSlotReserveAndBindRejectsInvalidSlotBeforeOwnerInspection()
    {
        var table = new InputControllerRegistrationTable(2);
        Assert.IsTrue(table.TryOpen(1, out var failure), failure.ToString());
        RegistrationFixture fixture = CreateRegistration(9_032, 9_132);
        int authenticationCalls = 0;
        fixture.Owner.OnAuthenticate = () => authenticationCalls++;

        Assert.IsFalse(table.TryReserveAndBindExactSlot(-1,
            fixture.Registration,
            out InputControllerSlotToken token,
            out InputControllerSetupRollbackClaim rollback, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.InvalidArgument,
            failure);
        Assert.IsFalse(token.IsValid);
        Assert.IsFalse(rollback.IsValid);
        Assert.AreEqual(0, authenticationCalls);
        Assert.IsTrue(table.GetSnapshot().All(snapshot =>
            snapshot.State == InputControllerSlotState.Empty));
    }

    [TestMethod]
    public void SetupAndPostCommitQuarantineCredentialsAreStateExact()
    {
        var table = new InputControllerRegistrationTable(2);
        Assert.IsTrue(table.TryOpen(1, out var failure), failure.ToString());
        RegistrationFixture bound = CreateRegistration(9_001, 9_101);
        RegistrationFixture attached = CreateRegistration(9_002, 9_102);
        Assert.IsTrue(table.TryReserve(bound.Registration,
            out InputControllerReservation reservation, out failure),
            failure.ToString());
        Assert.IsTrue(table.TryBind(reservation, out var boundToken,
            out failure), failure.ToString());
        InputControllerSlotToken attachedToken = Attach(table, attached);

        Assert.IsTrue(table.TryQuarantine(attachedToken,
            InputControllerSlotQuarantineReason.ExternalLifecycleFailure,
            out failure), failure.ToString());
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[attachedToken.Slot].State);
        Assert.IsTrue(table.TryQuarantine(reservation.SetupRollbackClaim,
            InputControllerSlotQuarantineReason.StopRejected,
            out failure), failure.ToString());
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[boundToken.Slot].State);

        Assert.IsFalse(table.TryRollback(reservation.SetupRollbackClaim,
            out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Quarantined, failure);
        Assert.IsFalse(table.TryBeginRetire(attachedToken, out _,
            out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Quarantined, failure);
    }

    [TestMethod]
    public void ExactLifecycleRequiresActivationTerminalNeutralAndDrain()
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out var failure), failure.ToString());
        RegistrationFixture fixture = CreateRegistration(11, 101);

        Assert.IsTrue(table.TryReserve(fixture.Registration,
            out var reservation, out failure), failure.ToString());
        InputControllerReservation canceled = reservation;
        Assert.IsTrue(table.TryCancel(canceled, out failure),
            failure.ToString());
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[0].State);
        Assert.IsTrue(table.TryReserve(fixture.Registration,
            out reservation, out failure), failure.ToString());
        Assert.IsTrue(reservation.Token.SlotGeneration >
            canceled.Token.SlotGeneration);
        Assert.IsFalse(table.TryBind(canceled, out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);
        Assert.AreEqual(InputControllerSlotState.Reserved,
            table.GetSnapshot()[0].State);
        Assert.IsFalse(table.TryAcquireReportLease(reservation.Token,
            fixture.Device, out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.WrongState, failure);

        Assert.IsTrue(table.TryBind(reservation, out var token, out failure),
            failure.ToString());
        Assert.AreEqual(reservation.Token, token);
        Assert.AreEqual(InputControllerSlotState.Bound,
            table.GetSnapshot()[0].State);
        Assert.IsFalse(table.TryAcquireReportLease(token, fixture.Device,
            out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.WrongState, failure);

        Assert.IsTrue(table.TryActivate(token, out failure),
            failure.ToString());
        RegistrationFixture foreign = CreateRegistration(12, 102);
        Assert.IsFalse(table.TryAcquireReportLease(token, foreign.Device,
            out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.WrongSender, failure);
        Assert.IsTrue(table.TryAcquireReportLease(token, fixture.Device,
            out var reportLease, out failure), failure.ToString());
        Assert.IsFalse(reportLease.TryAcknowledgeTerminalNeutral(out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.WrongLeaseKind,
            failure);
        reportLease.Dispose();

        Assert.IsTrue(table.TryBeginRetire(token, out var claim, out failure),
            failure.ToString());
        Assert.IsFalse(table.TryAcquireReportLease(token, fixture.Device,
            out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.WrongState, failure);
        Assert.IsFalse(table.TryMarkQuiesced(claim, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            TerminalNeutralRequired, failure);

        Assert.IsTrue(table.TryAcquireTerminalReportLease(claim,
            fixture.Device, out var terminalLease, out failure),
            failure.ToString());
        Assert.IsTrue(terminalLease.TryAcknowledgeTerminalNeutral(
            out failure), failure.ToString());
        InputControllerReportLease terminalCopy = terminalLease;
        Assert.IsFalse(terminalCopy.TryAcknowledgeTerminalNeutral(
            out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.AlreadyAcknowledged,
            failure);
        Assert.IsFalse(table.TryMarkQuiesced(claim, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Busy, failure);
        terminalLease.Dispose();
        terminalCopy.Dispose();

        Assert.IsTrue(table.TryWaitForDrain(claim, 0, out failure),
            failure.ToString());
        Assert.IsTrue(table.TryMarkQuiesced(claim, out failure),
            failure.ToString());
        Assert.AreEqual(InputControllerSlotState.Quiesced,
            table.GetSnapshot()[0].State);
        Assert.IsTrue(table.TryCompleteRemoval(claim, out failure),
            failure.ToString());
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[0].State);

        Assert.IsTrue(table.TryClose(1, out var closeSnapshot, out failure),
            failure.ToString());
        Assert.AreEqual(InputControllerSlotState.Removed,
            closeSnapshot[0].State);
        Assert.IsTrue(table.TryOpen(2, out failure), failure.ToString());
        Assert.AreEqual(InputControllerSlotState.Empty,
            table.GetSnapshot()[0].State);
        Assert.AreEqual(0, fixture.Owner.StopCalls);
        Assert.AreEqual(0, fixture.Owner.RemoveCalls);
    }

    [TestMethod]
    public void ParallelBlankIdentityRegistrationsReceiveUniqueSlots()
    {
        const int count = 16;
        var table = new InputControllerRegistrationTable(count);
        Assert.IsTrue(table.TryOpen(7, out var failure), failure.ToString());
        RegistrationFixture[] fixtures = Enumerable.Range(0, count).
            Select(index => CreateRegistration((ulong)index + 1,
                (ulong)index + 101)).ToArray();
        var reservations = new InputControllerReservation[count];
        var failures = new InputControllerSlotTableFailure[count];
        var succeeded = new bool[count];

        Parallel.For(0, count, index => succeeded[index] = table.TryReserve(
            fixtures[index].Registration, out reservations[index],
            out failures[index]));

        Assert.IsTrue(succeeded.All(value => value), string.Join(", ",
            failures.Select(value => value.ToString())));
        Assert.AreEqual(count, reservations.Select(value => value.Token.Slot).
            Distinct().Count());
        Assert.IsTrue(fixtures.All(value => value.Device.MacAddress ==
            DS4Device.BLANK_SERIAL));
        Assert.AreNotSame(fixtures[0].Device, fixtures[1].Device);

        Assert.IsFalse(table.TryReserve(fixtures[0].Registration, out _,
            out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            DuplicateRegistration, failure);

        var sameDeviceOwner = new TestOwner(fixtures[0].Device,
            fixtures[0].Registration.Generation);
        Assert.IsTrue(InputControllerRegistration.TryCreate(
            fixtures[0].Device, fixtures[0].Registration.Generation,
            InputControllerOwnershipKind.Switch2Runtime,
            hasHidInterface: false, hasPersistentIdentity: false,
            sameDeviceOwner, out var sameDeviceRegistration,
            out var registrationFailure), registrationFailure.ToString());
        Assert.IsFalse(table.TryReserve(sameDeviceRegistration, out _,
            out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            DuplicateRegistration, failure);

        for (int index = 0; index < count; index++)
        {
            Assert.IsTrue(table.TryBind(reservations[index], out var token,
                out failure), failure.ToString());
            Assert.IsTrue(table.TryActivate(token, out failure),
                failure.ToString());
        }
    }

    [TestMethod]
    public void CrossTableAndStaleAbaCredentialsAlwaysFailClosed()
    {
        RegistrationFixture first = CreateRegistration(21, 121);
        var left = new InputControllerRegistrationTable(1);
        var right = new InputControllerRegistrationTable(1);
        Assert.IsTrue(left.TryOpen(1, out var failure), failure.ToString());
        Assert.IsTrue(right.TryOpen(1, out failure), failure.ToString());
        Assert.IsTrue(left.TryReserve(first.Registration,
            out var leftReservation, out failure), failure.ToString());
        Assert.IsTrue(right.TryReserve(first.Registration,
            out var rightReservation, out failure), failure.ToString());
        Assert.IsFalse(right.TryBind(leftReservation, out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);
        Assert.IsFalse(right.TryRollback(
            leftReservation.SetupRollbackClaim, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);
        Assert.IsTrue(left.TryBind(leftReservation, out var leftToken,
            out failure), failure.ToString());
        Assert.IsTrue(right.TryBind(rightReservation, out var rightToken,
            out failure), failure.ToString());

        Assert.AreEqual(leftToken.Slot, rightToken.Slot);
        Assert.AreEqual(leftToken.ServiceGeneration,
            rightToken.ServiceGeneration);
        Assert.AreEqual(leftToken.SlotGeneration, rightToken.SlotGeneration);
        Assert.AreNotEqual(leftToken, rightToken,
            "Private issuer identity must distinguish colliding public data.");
        Assert.IsFalse(right.TryActivate(leftToken, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);

        Assert.IsFalse(left.TryCancel(leftReservation, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.WrongState, failure,
            "Bound setup must not bypass its explicit cleanup claim.");
        Assert.IsTrue(left.TryRollback(leftReservation.SetupRollbackClaim,
            out failure), failure.ToString());
        RegistrationFixture replacement = CreateRegistration(22, 122);
        Assert.IsTrue(left.TryReserve(replacement.Registration,
            out var replacementReservation, out failure), failure.ToString());
        Assert.AreEqual(leftToken.Slot,
            replacementReservation.Token.Slot);
        Assert.IsTrue(replacementReservation.Token.SlotGeneration >
            leftToken.SlotGeneration);
        Assert.IsFalse(left.TryActivate(leftToken, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);
        Assert.IsFalse(left.TryRollback(
            leftReservation.SetupRollbackClaim, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);
        Assert.IsTrue(left.TryBind(replacementReservation,
            out var replacementToken, out failure), failure.ToString());
        Assert.IsTrue(left.TryActivate(replacementToken, out failure),
            failure.ToString());
        Assert.IsFalse(left.TryAcquireReportLease(leftToken, first.Device,
            out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);
    }

    [TestMethod]
    public void CopiedDefaultAndReusedReportLeasesCannotUnderflowOrReleaseAba()
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out var failure), failure.ToString());
        RegistrationFixture fixture = CreateRegistration(31, 131);
        InputControllerSlotToken token = Attach(table, fixture);
        var leases = new InputControllerReportLease[64];
        for (int index = 0; index < leases.Length; index++)
        {
            Assert.IsTrue(table.TryAcquireReportLease(token, fixture.Device,
                out leases[index], out failure), failure.ToString());
        }
        Assert.IsFalse(table.TryAcquireReportLease(token, fixture.Device,
            out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.ReportLeaseLimit,
            failure);

        InputControllerReportLease staleCopy = leases[0];
        leases[0].Dispose();
        Assert.IsTrue(table.TryAcquireReportLease(token, fixture.Device,
            out var replacement, out failure), failure.ToString());
        staleCopy.Dispose();
        Assert.AreEqual(64, table.GetSnapshot()[0].ActiveReportLeases,
            "A copied stale lease released a newer cell generation.");

        Assert.IsTrue(table.TryBeginRetire(token, out var claim, out failure),
            failure.ToString());
        Assert.IsFalse(table.TryWaitForDrain(claim, 0, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.TimedOut, failure);
        for (int index = 1; index < leases.Length; index++)
        {
            leases[index].Dispose();
            leases[index].Dispose();
        }
        replacement.Dispose();
        replacement.Dispose();
        default(InputControllerReportLease).Dispose();
        Assert.AreEqual(0, table.GetSnapshot()[0].ActiveReportLeases);
        Assert.IsTrue(table.TryWaitForDrain(claim, 0, out failure),
            failure.ToString());
    }

    [TestMethod]
    public void ActionLeaseBoundedlyDrainsAndCopiedLeaseCannotReleaseSuccessor()
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out var failure), failure.ToString());
        RegistrationFixture fixture = CreateRegistration(41, 141);
        InputControllerSlotToken token = Attach(table, fixture);
        Assert.IsTrue(table.TryAcquireReportLease(token, fixture.Device,
            out var reportLease, out failure), failure.ToString());
        Assert.IsFalse(table.TryAcquireActionLease(token, 0, out _,
            out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.TimedOut, failure);
        Assert.IsFalse(table.GetSnapshot()[0].ActionPending,
            "Timed-out action admission did not roll back deterministically.");

        InputControllerActionLease firstAction = default;
        InputControllerSlotTableFailure actionFailure = default;
        Task<bool> waiter = Task.Run(() => table.TryAcquireActionLease(token,
            2_000, out firstAction, out actionFailure));
        Assert.IsTrue(WaitUntil(() => table.GetSnapshot()[0].ActionPending,
            1_000), "Action waiter never closed report admission.");
        Assert.IsFalse(table.TryAcquireReportLease(token, fixture.Device,
            out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Busy, failure);
        reportLease.Dispose();
        Assert.IsTrue(waiter.Wait(1_000));
        Assert.IsTrue(waiter.Result, actionFailure.ToString());

        InputControllerActionLease staleCopy = firstAction;
        firstAction.Dispose();
        Assert.IsTrue(table.TryAcquireActionLease(token, 0,
            out var secondAction, out failure), failure.ToString());
        staleCopy.Dispose();
        Assert.IsFalse(table.TryAcquireReportLease(token, fixture.Device,
            out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Busy, failure,
            "A stale copied action lease released its successor.");
        secondAction.Dispose();
        secondAction.Dispose();
        default(InputControllerActionLease).Dispose();
        Assert.IsTrue(table.TryAcquireReportLease(token, fixture.Device,
            out reportLease, out failure), failure.ToString());
        reportLease.Dispose();
    }

    [TestMethod]
    public void CloseRevokesRawReservationsAndReturnsBoundRollbackClaims()
    {
        var table = new InputControllerRegistrationTable(3);
        Assert.IsTrue(table.TryOpen(10, out var failure), failure.ToString());
        RegistrationFixture raw = CreateRegistration(51, 151);
        RegistrationFixture bound = CreateRegistration(52, 152);
        RegistrationFixture active = CreateRegistration(53, 153);
        Assert.IsTrue(table.TryReserve(raw.Registration,
            out var rawReservation, out failure), failure.ToString());
        Assert.IsTrue(table.TryReserve(bound.Registration,
            out var boundReservation, out failure), failure.ToString());
        Assert.IsTrue(table.TryBind(boundReservation, out var boundToken,
            out failure), failure.ToString());
        InputControllerSlotToken activeToken = Attach(table, active);

        Assert.IsTrue(table.TryClose(10, out var snapshots, out failure),
            failure.ToString());
        InputControllerSlotSnapshot rawSnapshot = snapshots.Single(value =>
            value.Slot == rawReservation.Token.Slot);
        InputControllerSlotSnapshot boundSnapshot = snapshots.Single(value =>
            value.Slot == boundToken.Slot);
        InputControllerSlotSnapshot activeSnapshot = snapshots.Single(value =>
            value.Slot == activeToken.Slot);
        Assert.AreEqual(InputControllerSlotState.Removed, rawSnapshot.State);
        Assert.AreEqual(rawReservation.Token.ServiceGeneration,
            rawSnapshot.ServiceGeneration);
        Assert.AreEqual(rawReservation.Token.SlotGeneration,
            rawSnapshot.SlotGeneration);
        Assert.AreEqual(InputControllerSlotState.Bound, boundSnapshot.State);
        Assert.IsTrue(boundSnapshot.SetupRollbackClaim.IsValid);
        Assert.AreEqual(InputControllerSlotState.Retiring,
            activeSnapshot.State);
        Assert.IsTrue(activeSnapshot.RetirementClaim.IsValid);

        Assert.IsFalse(table.TryBind(rawReservation, out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Closed, failure);
        Assert.IsFalse(table.TryActivate(boundToken, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Closed, failure);
        Assert.IsFalse(table.TryOpen(11, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Busy, failure);

        Assert.IsTrue(table.TryRollback(boundSnapshot.SetupRollbackClaim,
            out failure), failure.ToString());
        CompleteRetirement(table, activeSnapshot.RetirementClaim,
            active.Device);
        Assert.IsTrue(table.TryOpen(11, out failure), failure.ToString());
    }

    [TestMethod]
    public void AuthenticationIsOutsideLockAndCloseWinsActivationRace()
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out var failure), failure.ToString());
        RegistrationFixture fixture = CreateRegistration(61, 161);
        fixture.Owner.OnAuthenticate = () => table.GetSnapshot();
        Assert.IsTrue(table.TryReserve(fixture.Registration,
            out var reservation, out failure), failure.ToString());
        Assert.IsTrue(table.TryBind(reservation, out var token, out failure),
            failure.ToString());

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        fixture.Owner.OnAuthenticate = () =>
        {
            entered.Set();
            release.Wait(2_000);
        };
        InputControllerSlotTableFailure activationFailure = default;
        Task<bool> activation = Task.Run(() => table.TryActivate(token,
            out activationFailure));
        Assert.IsTrue(entered.Wait(1_000));
        Assert.IsTrue(table.TryClose(1, out var snapshots, out failure),
            failure.ToString());
        release.Set();
        Assert.IsTrue(activation.Wait(1_000));
        Assert.IsFalse(activation.Result);
        Assert.AreEqual(InputControllerSlotTableFailure.Closed,
            activationFailure);
        InputControllerSlotSnapshot bound = snapshots.Single(value =>
            value.State == InputControllerSlotState.Bound);
        Assert.IsTrue(table.TryRollback(bound.SetupRollbackClaim,
            out failure), failure.ToString());
    }

    [TestMethod]
    public void CloseCancelsActionWaiterWithoutDiscardingExistingLease()
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out var failure), failure.ToString());
        RegistrationFixture fixture = CreateRegistration(71, 171);
        InputControllerSlotToken token = Attach(table, fixture);
        Assert.IsTrue(table.TryAcquireReportLease(token, fixture.Device,
            out var reportLease, out failure), failure.ToString());
        InputControllerSlotTableFailure waiterFailure = default;
        Task<bool> waiter = Task.Run(() => table.TryAcquireActionLease(token,
            2_000, out _, out waiterFailure));
        Assert.IsTrue(WaitUntil(() => table.GetSnapshot()[0].ActionPending,
            1_000));

        Assert.IsTrue(table.TryClose(1, out var snapshots, out failure),
            failure.ToString());
        Assert.IsTrue(waiter.Wait(1_000),
            "Close did not wake the bounded action waiter.");
        Assert.IsFalse(waiter.Result);
        Assert.AreEqual(InputControllerSlotTableFailure.Closed,
            waiterFailure);
        Assert.AreEqual(1, snapshots[0].ActiveReportLeases);
        reportLease.Dispose();
        InputControllerRetirementClaim claim = table.GetSnapshot()[0].
            RetirementClaim;
        Assert.IsTrue(table.TryWaitForDrain(claim, 0, out failure),
            failure.ToString());
    }

    [TestMethod]
    public void QuarantinePersistsPerSlotWhileHealthySlotsCanReopen()
    {
        var table = new InputControllerRegistrationTable(2);
        Assert.IsTrue(table.TryOpen(1, out var failure), failure.ToString());
        RegistrationFixture uncertain = CreateRegistration(81, 181);
        RegistrationFixture healthy = CreateRegistration(82, 182);
        InputControllerSlotToken uncertainToken = Attach(table, uncertain);
        InputControllerSlotToken healthyToken = Attach(table, healthy);
        Assert.IsTrue(table.TryAcquireReportLease(uncertainToken,
            uncertain.Device, out var uncertainLease, out failure),
            failure.ToString());
        Assert.IsTrue(table.TryBeginRetire(uncertainToken,
            out var uncertainClaim, out failure), failure.ToString());
        uncertain.Owner.ThrowOnKind = true;
        Assert.IsFalse(uncertain.Registration.TryStopAndQuiesce(0,
            out var ownerFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.OwnerThrew,
            ownerFailure);
        Assert.IsTrue(uncertainToken.IsValid,
            "Issued credentials must remain structural after owner failure.");
        Assert.IsTrue(uncertainClaim.IsValid);
        Assert.IsTrue(table.TryQuarantine(uncertainClaim,
            InputControllerSlotQuarantineReason.OwnerThrew, out failure),
            failure.ToString());
        Assert.IsFalse(table.TryQuarantine(uncertainClaim,
            InputControllerSlotQuarantineReason.ExternalLifecycleFailure,
            out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Quarantined, failure);
        Assert.IsFalse(table.TryCompleteRemoval(uncertainClaim, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Quarantined, failure);

        Assert.IsTrue(table.TryBeginRetire(healthyToken,
            out var healthyClaim, out failure), failure.ToString());
        CompleteRetirement(table, healthyClaim, healthy.Device);
        Assert.IsTrue(table.TryClose(1, out _, out failure),
            failure.ToString());
        Assert.IsFalse(table.TryOpen(2, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Busy, failure,
            "A quarantined old-service callback must drain before reopen.");
        uncertainLease.Dispose();
        Assert.IsTrue(table.TryWaitForDrain(uncertainClaim, 0, out failure),
            failure.ToString());
        Assert.IsTrue(table.TryOpen(2, out failure), failure.ToString());
        InputControllerSlotSnapshot quarantined = table.GetSnapshot().Single(
            value => value.State == InputControllerSlotState.Quarantined);
        Assert.AreEqual(uncertainToken, quarantined.Token);
        Assert.AreEqual(InputControllerSlotQuarantineReason.OwnerThrew,
            quarantined.QuarantineReason);

        RegistrationFixture replacement = CreateRegistration(83, 183);
        Assert.IsTrue(table.TryReserve(replacement.Registration,
            out var reservation, out failure), failure.ToString());
        Assert.AreNotEqual(quarantined.Slot, reservation.Token.Slot);
        uncertain.Owner.ThrowOnKind = false;
        Assert.IsFalse(table.TryReserve(uncertain.Registration, out _,
            out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            DuplicateRegistration, failure);
    }

    [TestMethod]
    public void ServiceAndSlotGenerationExhaustionNeverWrapsOrMutates()
    {
        var slotExhausted = new InputControllerRegistrationTable(1,
            lastServiceGeneration: 0, lastSlotGeneration: ulong.MaxValue);
        Assert.IsTrue(slotExhausted.TryOpen(1, out var failure),
            failure.ToString());
        RegistrationFixture fixture = CreateRegistration(91, 191);
        Assert.IsFalse(slotExhausted.TryReserve(fixture.Registration,
            out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            SlotGenerationExhausted, failure);
        Assert.AreEqual(InputControllerSlotState.Empty,
            slotExhausted.GetSnapshot()[0].State);

        var serviceExhausted = new InputControllerRegistrationTable(1);
        Assert.IsTrue(serviceExhausted.TryOpen(ulong.MaxValue, out failure),
            failure.ToString());
        Assert.IsTrue(serviceExhausted.TryClose(ulong.MaxValue, out _,
            out failure), failure.ToString());
        Assert.IsFalse(serviceExhausted.TryOpen(ulong.MaxValue, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            ServiceGenerationExhausted, failure);

        var monotonic = new InputControllerRegistrationTable(1);
        Assert.IsTrue(monotonic.TryOpen(5, out failure), failure.ToString());
        Assert.IsTrue(monotonic.TryClose(5, out _, out failure),
            failure.ToString());
        Assert.IsFalse(monotonic.TryOpen(5, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            ServiceGenerationNotMonotonic, failure);
        Assert.IsFalse(monotonic.TryOpen(0, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            InvalidServiceGeneration, failure);
    }

    [TestMethod]
    public void ServiceRetainsOneReadonlyTableWhileDiscoveryRemainsIndependent()
    {
        Type tableType = typeof(InputControllerRegistrationTable);
        FieldInfo[] fields = typeof(ControlService).GetFields(BindingFlags.Instance |
            BindingFlags.NonPublic).Where(field => field.FieldType == tableType).ToArray();
        Assert.AreEqual(1, fields.Length, "Profile capture must reuse the service's shared table.");
        Assert.IsTrue(fields[0].IsInitOnly);
        Assert.IsFalse(TypeReferences(typeof(DS4Devices), tableType));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RetirementAndCloseWaitForAnAlreadyAdmittedAction(bool close)
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out _));
        RegistrationFixture fixture = CreateRegistration(92_001, 92_002);
        var token = Attach(table, fixture);
        Assert.IsTrue(table.TryAcquireActionLease(token, 0, out var action, out _));
        InputControllerRetirementClaim claim;
        try
        {
            if (close)
            {
                Assert.IsTrue(table.TryClose(1, out var snapshots, out _));
                claim = snapshots[0].RetirementClaim;
            }
            else
                Assert.IsTrue(table.TryBeginRetire(token, out claim, out _));
            Assert.IsFalse(table.TryWaitForDrain(claim, 0, out _));
            Assert.IsFalse(table.TryAcquireTerminalReportLease(claim, fixture.Device, out _, out _));
            Assert.IsFalse(table.TryMarkQuiesced(claim, out _));
            Assert.IsFalse(table.TryCompleteRemoval(claim, out _));
            Assert.IsTrue(table.GetSnapshot()[0].ActionActive);
        }
        finally { action.Dispose(); }
        CompleteRetirement(table, claim, fixture.Device);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PausedActionReleasesBeforeConcurrentPendingTerminalEvenOnException(bool throws)
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out _));
        RegistrationFixture fixture = CreateRegistration(93_001, 93_002);
        var token = Attach(table, fixture);
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        service.DS4Controllers = new DS4Device[] { fixture.Device };
        Assert.IsTrue(ControllerProfileActionTarget.TryCapture(service, table, 0,
            fixture.Device, out var target));
        fixture.Device.StartUpdate();
        InputControllerRetirementClaim claim = default;
        bool acknowledged = false;
        fixture.Device.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind != Switch2RuntimeReportKind.TerminalNeutral)
                return;
            Assert.IsTrue(table.TryAcquireTerminalReportLease(claim, sender, out var terminal, out _));
            using (terminal)
                acknowledged = terminal.TryAcknowledgeTerminalNeutral(out _);
        };
        void Run() => fixture.Device.TryHaltReportingRunAction(() =>
        {
            Assert.IsTrue(target.TryAcquire(out var lease));
            using (lease)
            {
                Task retire = Task.Run(() =>
                {
                    Assert.IsTrue(table.TryBeginRetire(token, out claim, out _));
                    Assert.AreEqual(Switch2TerminalNeutralRequestResult.AcceptedPending,
                        fixture.Device.RequestTerminalNeutral());
                });
                Assert.IsTrue(retire.Wait(2000));
                Assert.IsFalse(table.TryWaitForDrain(claim, 0, out _));
                if (throws)
                    throw new InvalidOperationException("Synthetic profile failure");
            }
        });
        if (throws)
            Assert.ThrowsException<InvalidOperationException>(Run);
        else
            Run();
        Assert.IsTrue(acknowledged, "The terminal must be admitted after the action's finally.");
        Assert.IsTrue(fixture.Device.TerminalNeutralReported);
        Assert.IsTrue(table.TryWaitForDrain(claim, 0, out _));
        Assert.IsFalse(target.TryAcquire(out _));
    }

    [TestMethod]
    public void PreCommitClaimCannotAssertExternalSuccess()
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out var failure), failure.ToString());
        RegistrationFixture fixture = CreateRegistration(9_005, 9_105);
        Assert.IsTrue(table.TryReserveAndBind(fixture.Registration,
            out InputControllerSlotToken token, out _, out failure),
            failure.ToString());
        Assert.IsTrue(table.TryBeginActivate(token,
            out InputControllerActivationClaim activation, out failure),
            failure.ToString());

        Assert.IsFalse(table.TryCompleteActivate(activation,
            externalCommitSucceeded: true, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            ActivationCommitRejected, failure);
        InputControllerSlotSnapshot snapshot = table.GetSnapshot().Single();
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            snapshot.State);
        Assert.AreEqual(InputControllerSlotQuarantineReason.
            ExternalLifecycleFailure, snapshot.QuarantineReason);
        Assert.IsFalse(snapshot.ActivationPending);
        Assert.IsFalse(table.TryAcquireActivationCommit(activation,
            out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);
    }

    [TestMethod]
    public void PendingActivationAdmitsOnlyExactRegularReports()
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(1, out var failure), failure.ToString());
        RegistrationFixture fixture = CreateRegistration(9_010, 9_110);
        Assert.IsTrue(table.TryReserveAndBind(fixture.Registration,
            out InputControllerSlotToken token, out _, out failure),
            failure.ToString());
        Assert.IsTrue(table.TryBeginActivate(token,
            out InputControllerActivationClaim activation, out failure),
            failure.ToString());

        InputControllerSlotSnapshot pending = table.GetSnapshot().Single();
        Assert.AreEqual(InputControllerSlotState.Attached, pending.State);
        Assert.IsTrue(pending.ActivationPending);
        Assert.IsTrue(table.TryAcquireReportLease(token, fixture.Device,
            out InputControllerReportLease reportLease, out failure),
            failure.ToString());
        Assert.IsFalse(table.TryAcquireActionLease(token, 0, out _,
            out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Busy, failure);
        Assert.IsFalse(table.TryBeginRetire(token, out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Busy, failure);
        Assert.IsFalse(table.TryClose(1, out var closeSnapshots,
            out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Busy, failure);
        Assert.AreEqual(0, closeSnapshots.Length);

        Assert.IsTrue(table.TryAcquireActivationCommit(activation,
            out InputControllerActivationCommitCredential commit,
            out failure), failure.ToString());
        Assert.IsFalse(table.TryAcquireActivationCommit(activation,
            out _, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.Busy, failure);
        Assert.IsFalse(table.TryCompleteActivate(activation,
            externalCommitSucceeded: true, out failure),
            "A copied pre-commit claim cannot expire an acquired commit.");
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);
        Assert.IsTrue(table.TryCompleteActivate(commit,
            externalCommitSucceeded: true, out failure), failure.ToString());
        Assert.IsFalse(table.GetSnapshot().Single().ActivationPending);
        Assert.IsFalse(table.TryCompleteActivate(commit,
            externalCommitSucceeded: true, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);

        reportLease.Dispose();
        Assert.IsTrue(table.TryAcquireActionLease(token, 0,
            out InputControllerActionLease actionLease, out failure),
            failure.ToString());
        actionLease.Dispose();
        Assert.IsTrue(table.TryBeginRetire(token,
            out InputControllerRetirementClaim retirement, out failure),
            failure.ToString());
        CompleteRetirement(table, retirement, fixture.Device);
        Assert.IsTrue(table.TryClose(1, out _, out failure),
            failure.ToString());
    }

    [TestMethod]
    public void PendingActivationCredentialIsExactAndQuarantineInvalidatesIt()
    {
        var first = new InputControllerRegistrationTable(1);
        var foreign = new InputControllerRegistrationTable(1);
        Assert.IsTrue(first.TryOpen(1, out var failure), failure.ToString());
        Assert.IsTrue(foreign.TryOpen(1, out failure), failure.ToString());
        RegistrationFixture firstFixture = CreateRegistration(9_020, 9_120);
        RegistrationFixture foreignFixture = CreateRegistration(9_021,
            9_121);
        Assert.IsTrue(first.TryReserveAndBind(firstFixture.Registration,
            out InputControllerSlotToken firstToken, out _, out failure),
            failure.ToString());
        Assert.IsTrue(foreign.TryReserveAndBind(foreignFixture.Registration,
            out InputControllerSlotToken foreignToken, out _, out failure),
            failure.ToString());
        Assert.IsTrue(first.TryBeginActivate(firstToken,
            out InputControllerActivationClaim firstActivation, out failure),
            failure.ToString());
        Assert.IsTrue(foreign.TryBeginActivate(foreignToken,
            out InputControllerActivationClaim foreignActivation,
            out failure), failure.ToString());

        Assert.IsFalse(first.TryCompleteActivate(foreignActivation,
            externalCommitSucceeded: true, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);
        Assert.IsTrue(first.TryQuarantine(firstToken,
            InputControllerSlotQuarantineReason.ExternalLifecycleFailure,
            out failure), failure.ToString());
        Assert.IsFalse(first.TryCompleteActivate(firstActivation,
            externalCommitSucceeded: true, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);
        InputControllerSlotSnapshot quarantined =
            first.GetSnapshot().Single();
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            quarantined.State);
        Assert.IsFalse(quarantined.ActivationPending);

        Assert.IsTrue(foreign.TryAcquireActivationCommit(foreignActivation,
            out InputControllerActivationCommitCredential foreignCommit,
            out failure), failure.ToString());
        Assert.IsFalse(first.TryCompleteActivate(foreignCommit,
            externalCommitSucceeded: true, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            failure);
        Assert.IsFalse(foreign.TryCompleteActivate(foreignCommit,
            externalCommitSucceeded: false, out failure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            ActivationCommitRejected, failure);
        quarantined = foreign.GetSnapshot().Single();
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            quarantined.State);
        Assert.IsFalse(quarantined.ActivationPending);
    }

    private static InputControllerSlotToken Attach(
        InputControllerRegistrationTable table,
        RegistrationFixture fixture)
    {
        Assert.IsTrue(table.TryReserve(fixture.Registration,
            out var reservation, out var failure), failure.ToString());
        Assert.IsTrue(table.TryBind(reservation, out var token, out failure),
            failure.ToString());
        Assert.IsTrue(table.TryActivate(token, out failure),
            failure.ToString());
        return token;
    }

    private static void CompleteRetirement(
        InputControllerRegistrationTable table,
        InputControllerRetirementClaim claim, DS4Device device)
    {
        Assert.IsTrue(table.TryAcquireTerminalReportLease(claim, device,
            out var terminalLease, out var failure), failure.ToString());
        Assert.IsTrue(terminalLease.TryAcknowledgeTerminalNeutral(out failure),
            failure.ToString());
        terminalLease.Dispose();
        Assert.IsTrue(table.TryWaitForDrain(claim, 0, out failure),
            failure.ToString());
        Assert.IsTrue(table.TryMarkQuiesced(claim, out failure),
            failure.ToString());
        Assert.IsTrue(table.TryCompleteRemoval(claim, out failure),
            failure.ToString());
    }

    private static RegistrationFixture CreateRegistration(
        ulong deviceGeneration, ulong transportGeneration)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(deviceGeneration,
            transportGeneration, Switch2Transport.Usb, out var device,
            out var deviceFailure), deviceFailure.ToString());
        var owner = new TestOwner(device, deviceGeneration);
        Assert.IsTrue(InputControllerRegistration.TryCreate(device,
            deviceGeneration, InputControllerOwnershipKind.Switch2Runtime,
            hasHidInterface: false, hasPersistentIdentity: false, owner,
            out var registration, out var registrationFailure),
            registrationFailure.ToString());
        return new RegistrationFixture(device, owner, registration);
    }

    private static bool WaitUntil(Func<bool> predicate,
        int timeoutMilliseconds)
    {
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        do
        {
            if (predicate())
            {
                return true;
            }
            Thread.Yield();
        }
        while (Environment.TickCount64 < deadline);

        return predicate();
    }

    private static bool TypeReferences(Type inspected, Type target)
    {
        const BindingFlags flags = BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.Public |
            BindingFlags.NonPublic;
        return inspected.GetFields(flags).Any(field =>
                TypeContains(field.FieldType, target)) ||
            inspected.GetMethods(flags).Any(method =>
                TypeContains(method.ReturnType, target) ||
                method.GetParameters().Any(parameter =>
                    TypeContains(parameter.ParameterType, target)));
    }

    private static bool TypeContains(Type inspected, Type target)
    {
        if (inspected == target)
        {
            return true;
        }
        if (inspected.HasElementType)
        {
            return TypeContains(inspected.GetElementType(), target);
        }
        return inspected.IsGenericType && inspected.GetGenericArguments().Any(
            argument => TypeContains(argument, target));
    }

    private readonly record struct RegistrationFixture(
        Switch2RuntimeInputDevice Device, TestOwner Owner,
        InputControllerRegistration Registration);

    private sealed class TestOwner : IInputControllerRegistrationOwner
    {
        private readonly DS4Device device;
        private readonly ulong generation;

        public TestOwner(DS4Device device, ulong generation)
        {
            this.device = device;
            this.generation = generation;
        }

        public Action OnAuthenticate { get; set; }

        public bool ThrowOnKind { get; set; }

        public bool ThrowOnAuthenticate { get; set; }

        public int StopCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public InputControllerOwnershipKind Kind => ThrowOnKind ?
            throw new InvalidOperationException() :
            InputControllerOwnershipKind.Switch2Runtime;

        public bool Authenticates(DS4Device candidate, ulong candidateGeneration)
        {
            if (ThrowOnAuthenticate)
            {
                throw new InvalidOperationException();
            }
            OnAuthenticate?.Invoke();
            return ReferenceEquals(device, candidate) &&
                generation == candidateGeneration;
        }

        public bool TryStopAndQuiesce(DS4Device candidate,
            ulong candidateGeneration, int timeoutMilliseconds,
            out InputControllerOwnerOperationFailure failure)
        {
            StopCalls++;
            bool accepted = Authenticates(candidate, candidateGeneration);
            failure = accepted ? InputControllerOwnerOperationFailure.None :
                InputControllerOwnerOperationFailure.StopRejected;
            return accepted;
        }

        public bool TryRemove(DS4Device candidate,
            ulong candidateGeneration,
            out InputControllerOwnerOperationFailure failure)
        {
            RemoveCalls++;
            bool accepted = Authenticates(candidate, candidateGeneration);
            failure = accepted ? InputControllerOwnerOperationFailure.None :
                InputControllerOwnerOperationFailure.RemoveRejected;
            return accepted;
        }
    }
}
