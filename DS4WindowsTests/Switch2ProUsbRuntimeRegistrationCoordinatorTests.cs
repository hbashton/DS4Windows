using System.Threading;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

public partial class Switch2ProUsbRuntimeOwnerTests
{
    [TestMethod]
    public void CoordinatorAttachesBeforeFirstReportAndRetiresInExactOrder()
    {
        CreateScriptedCoordinator(out var coordinator, out var table,
            out var owner, out ScriptedPumpFactory factory);
        Switch2CanonicalInputFrame frame = CreateProFrame(DeviceGeneration,
            TransportGeneration);
        var kinds = new List<Switch2RuntimeReportKind>();
        var states = new List<InputControllerSlotState>();
        using ManualResetEventSlim regularSeen = new(false);
        factory.Pump.OnStart = () =>
            ((ISwitch2ProUsbInputSink)owner).TryPublish(frame);

        Assert.IsTrue(coordinator.TryAttach(owner, (slot, sender, report) =>
        {
            lock (kinds)
            {
                kinds.Add(report.Kind);
                states.Add(table.GetSnapshot()[slot].State);
            }
            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                regularSeen.Set();
            }
        }, 2_000, out InputControllerSlotToken token, out var attached),
            attached.Kind.ToString());
        Assert.IsTrue(regularSeen.Wait(2_000));

        Assert.IsTrue(coordinator.TryRemove(token, 2_000,
            out var removed), removed.Kind.ToString());
        lock (kinds)
        {
            CollectionAssert.AreEqual(new[]
            {
                Switch2RuntimeReportKind.Regular,
                Switch2RuntimeReportKind.TerminalNeutral,
            }, kinds);
            CollectionAssert.AreEqual(new[]
            {
                InputControllerSlotState.Attached,
                InputControllerSlotState.Retiring,
            }, states);
        }
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void CloseDuringParkedPrepareSeesBoundBindingAndRollsBackOnce()
    {
        using ManualResetEventSlim workerEntered = new(false);
        using ManualResetEventSlim releaseWorker = new(false);
        FakeLease lease = new();
        var factory = new ParkedPumpFactory(
            static thread => thread.Start(), () =>
            {
                workerEntered.Set();
                releaseWorker.Wait();
            });
        Assert.IsTrue(TryCreateCore(lease, factory,
            out Switch2ProUsbRuntimeOwner owner, out _, out var created),
            created.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        var coordinator =
            new Switch2ProUsbRuntimeRegistrationCoordinator(table);
        Assert.IsTrue(coordinator.TryOpen(1, out var opened),
            opened.Kind.ToString());

        InputControllerSlotToken token = default;
        Switch2ProUsbRuntimeRegistrationFailure attachFailure = default;
        Task<bool> attach = Task.Run(() => coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out token,
            out attachFailure));
        Assert.IsTrue(workerEntered.Wait(1_000));
        InputControllerSlotSnapshot bound = table.GetSnapshot().Single();
        Assert.AreEqual(InputControllerSlotState.Bound, bound.State,
            "Bind and binding insertion must be indivisible to close.");

        Switch2ProUsbRuntimeRegistrationFailure closeFailure = default;
        Task<bool> close = Task.Run(() => coordinator.TryClose(1, 2_000,
            out closeFailure));
        Assert.IsTrue(SpinWait.SpinUntil(() => !table.IsOpen, 1_000));
        Switch2ProUsbRuntimeOwnerState stateBeforeClosedAttach = owner.State;
        Assert.IsFalse(coordinator.TryAttach(owner,
            static (_, _, _) => { }, 1_000, out _,
            out var closedAttach));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            TableRejected, closedAttach.Kind);
        Assert.AreEqual(InputControllerSlotTableFailure.Closed,
            closedAttach.TableFailure);
        Assert.AreEqual(stateBeforeClosedAttach, owner.State,
            "A closed admission without a bound claim mutated the owner.");
        releaseWorker.Set();

        Assert.IsTrue(attach.Wait(2_000));
        Assert.IsFalse(attach.Result);
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            TableRejected, attachFailure.Kind);
        Assert.IsTrue(close.Wait(2_000));
        Assert.IsTrue(close.Result, closeFailure.Kind.ToString());
        Assert.AreEqual(0, lease.BeginCount);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.AbortedUnpublished,
            owner.State);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot().Single().State);
    }

    [TestMethod]
    public void DuplicateAttachCannotAbortTheBoundWinningLifetime()
    {
        using ManualResetEventSlim workerEntered = new(false);
        using ManualResetEventSlim releaseWorker = new(false);
        var factory = new ParkedPumpFactory(
            static thread => thread.Start(), () =>
            {
                workerEntered.Set();
                releaseWorker.Wait();
            });
        var lease = new FakeLease { CompleteSynchronously = false };
        Assert.IsTrue(TryCreateCore(lease, factory,
            out Switch2ProUsbRuntimeOwner owner, out _, out var created),
            created.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        var coordinator =
            new Switch2ProUsbRuntimeRegistrationCoordinator(table);
        Assert.IsTrue(coordinator.TryOpen(1, out _));
        InputControllerSlotToken winningToken = default;
        Switch2ProUsbRuntimeRegistrationFailure winningFailure = default;
        Task<bool> winner = Task.Run(() => coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out winningToken,
            out winningFailure));
        Assert.IsTrue(workerEntered.Wait(1_000));
        Assert.AreEqual(InputControllerSlotState.Bound,
            table.GetSnapshot().Single().State);
        Switch2ProUsbRuntimeOwnerState stateBeforeDuplicate = owner.State;

        Assert.IsFalse(coordinator.TryAttach(owner,
            static (_, _, _) => { }, 1_000, out _,
            out var duplicate));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            TableRejected, duplicate.Kind);
        Assert.AreEqual(InputControllerSlotTableFailure.
            DuplicateRegistration, duplicate.TableFailure);
        Assert.AreEqual(stateBeforeDuplicate, owner.State,
            "An admission without a bound claim mutated the winner's owner.");

        releaseWorker.Set();
        Assert.IsTrue(winner.Wait(2_000));
        Assert.IsTrue(winner.Result, winningFailure.Kind.ToString());
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Active, owner.State);
        Assert.IsTrue(coordinator.TryRemove(winningToken, 2_000,
            out var removed), removed.Kind.ToString());
    }

    [TestMethod]
    public void CloseWinningAtomicAdmissionLeavesNoReservationOrOwnerMutation()
    {
        CreateScriptedCoordinator(out var coordinator, out var table,
            out var owner, out _);
        object lifecycleGate = GetLifecycleGate(coordinator);
        using ManualResetEventSlim attemptStarted = new(false);
        Switch2ProUsbRuntimeRegistrationFailure firstFailure = default;
        Task<bool> first;

        lock (lifecycleGate)
        {
            first = Task.Run(() =>
            {
                attemptStarted.Set();
                return coordinator.TryAttach(owner,
                    static (_, _, _) => { }, 2_000, out _,
                    out firstFailure);
            });
            Assert.IsTrue(attemptStarted.Wait(1_000));
            Assert.AreEqual(InputControllerSlotState.Empty,
                table.GetSnapshot().Single().State,
                "Coordinator admission leaked a raw reservation outside its gate.");
            Assert.IsTrue(coordinator.TryClose(1, 2_000,
                out var closed), closed.Kind.ToString());
        }

        Assert.IsTrue(first.Wait(2_000));
        Assert.IsFalse(first.Result);
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            TableRejected, firstFailure.Kind);
        Assert.AreEqual(InputControllerSlotTableFailure.Closed,
            firstFailure.TableFailure);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Created, owner.State,
            "A failed atomic admission without a claim mutated the owner.");

        Assert.IsTrue(coordinator.TryOpen(2, out var reopened),
            reopened.Kind.ToString());
        Assert.IsTrue(coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000,
            out InputControllerSlotToken replacement,
            out var attached), attached.Kind.ToString());
        Assert.IsTrue(coordinator.TryRemove(replacement, 2_000,
            out var removed), removed.Kind.ToString());
    }

    [TestMethod]
    public void BoundRollbackAndBindingClearAreOneLifecycleTransaction()
    {
        using ManualResetEventSlim workerEntered = new(false);
        using ManualResetEventSlim releaseWorker = new(false);
        var lease = new FakeLease { BlockDispose = true };
        var factory = new ParkedPumpFactory(
            static thread => thread.Start(), () =>
            {
                workerEntered.Set();
                releaseWorker.Wait();
            });
        Assert.IsTrue(TryCreateCore(lease, factory,
            out Switch2ProUsbRuntimeOwner owner, out _, out var created),
            created.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        var coordinator =
            new Switch2ProUsbRuntimeRegistrationCoordinator(table);
        Assert.IsTrue(coordinator.TryOpen(1, out _));

        Switch2ProUsbRuntimeRegistrationFailure attachFailure = default;
        Task<bool> attach = Task.Run(() => coordinator.TryAttach(owner,
            static (_, _, _) => { }, 5_000, out _, out attachFailure));
        Assert.IsTrue(workerEntered.Wait(1_000));
        Assert.AreEqual(InputControllerSlotState.Bound,
            table.GetSnapshot().Single().State);
        Switch2ProUsbRuntimeRegistrationFailure closeFailure = default;
        Task<bool> close = Task.Run(() => coordinator.TryClose(1, 5_000,
            out closeFailure));
        Assert.IsTrue(SpinWait.SpinUntil(() => !table.IsOpen, 1_000));
        releaseWorker.Set();
        Assert.IsTrue(lease.DisposeEntered.Wait(1_000));

        object lifecycleGate = GetLifecycleGate(coordinator);
        lock (lifecycleGate)
        {
            lease.AllowDispose.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.State ==
                Switch2ProUsbRuntimeOwnerState.AbortedUnpublished, 1_000));
            Assert.IsFalse(SpinWait.SpinUntil(() =>
                table.GetSnapshot().Single().State !=
                    InputControllerSlotState.Bound, 100),
                "Rollback exposed a reusable table slot before clearing its binding.");
            Assert.AreEqual(InputControllerSlotState.Bound,
                table.GetSnapshot().Single().State);
        }

        Assert.IsTrue(attach.Wait(2_000));
        Assert.IsFalse(attach.Result);
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            TableRejected, attachFailure.Kind);
        Assert.IsTrue(close.Wait(2_000));
        Assert.IsTrue(close.Result, closeFailure.Kind.ToString());
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot().Single().State);

        var replacementFactory = new ScriptedPumpFactory();
        Assert.IsTrue(TryCreateCore(new FakeLease(), replacementFactory,
            out Switch2ProUsbRuntimeOwner replacement, out _,
            out var replacementCreated), replacementCreated.Kind.ToString());
        Assert.IsTrue(coordinator.TryOpen(2, out var reopened),
            reopened.Kind.ToString());
        Assert.IsTrue(coordinator.TryAttach(replacement,
            static (_, _, _) => { }, 2_000,
            out InputControllerSlotToken replacementToken,
            out var replacementAttached), replacementAttached.Kind.ToString());
        Assert.IsTrue(coordinator.TryRemove(replacementToken, 2_000,
            out var replacementRemoved),
            replacementRemoved.Kind.ToString());
    }

    [TestMethod]
    public void RemovalCompletionAndBindingClearAreOneLifecycleTransaction()
    {
        CreateScriptedCoordinator(out var coordinator, out var table,
            out var owner, out _);
        using ManualResetEventSlim terminalEntered = new(false);
        using ManualResetEventSlim releaseTerminal = new(false);
        Assert.IsTrue(coordinator.TryAttach(owner, (_, _, report) =>
        {
            if (report.Kind == Switch2RuntimeReportKind.TerminalNeutral)
            {
                terminalEntered.Set();
                releaseTerminal.Wait();
            }
        }, 2_000, out InputControllerSlotToken token, out _));

        Switch2ProUsbRuntimeRegistrationFailure removalFailure = default;
        Task<bool> removal = Task.Run(() => coordinator.TryRemove(token,
            5_000, out removalFailure));
        Assert.IsTrue(terminalEntered.Wait(1_000));
        object lifecycleGate = GetLifecycleGate(coordinator);
        lock (lifecycleGate)
        {
            releaseTerminal.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.State ==
                Switch2ProUsbRuntimeOwnerState.Removed, 1_000));
            Assert.IsFalse(SpinWait.SpinUntil(() =>
                table.GetSnapshot().Single().State !=
                    InputControllerSlotState.Quiesced, 100),
                "Removal exposed a reusable table slot before clearing its binding.");
            Assert.AreEqual(InputControllerSlotState.Quiesced,
                table.GetSnapshot().Single().State);
        }

        Assert.IsTrue(removal.Wait(2_000));
        Assert.IsTrue(removal.Result, removalFailure.Kind.ToString());
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot().Single().State);

        var replacementFactory = new ScriptedPumpFactory();
        Assert.IsTrue(TryCreateCore(new FakeLease(), replacementFactory,
            out Switch2ProUsbRuntimeOwner replacement, out _,
            out var replacementCreated), replacementCreated.Kind.ToString());
        Assert.IsTrue(coordinator.TryAttach(replacement,
            static (_, _, _) => { }, 2_000,
            out InputControllerSlotToken replacementToken,
            out var replacementAttached), replacementAttached.Kind.ToString());
        Assert.IsTrue(coordinator.TryRemove(replacementToken, 2_000,
            out var replacementRemoved),
            replacementRemoved.Kind.ToString());
    }

    [TestMethod]
    public void SelfCleanedPrepareFailureRollsBackBoundSlotExactlyOnce()
    {
        FakeLease lease = new();
        var factory = new ParkedPumpFactory(
            _ => throw new InvalidOperationException(
                "Synthetic worker-start failure."),
            beforeWorkerPark: null);
        Assert.IsTrue(TryCreateCore(lease, factory,
            out Switch2ProUsbRuntimeOwner owner, out _, out var created),
            created.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        var coordinator =
            new Switch2ProUsbRuntimeRegistrationCoordinator(table);
        Assert.IsTrue(coordinator.TryOpen(1, out _));

        Assert.IsFalse(coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out var token,
            out var failure));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            PrepareRejected, failure.Kind);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.AbortedUnpublished,
            owner.State);
        Assert.AreEqual(1, lease.DisposeCount,
            "Coordinator cleanup must reuse the owner's retained proof, not dispose twice.");
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void HostilePrepareCounterGetterIsContainedAndRolledBack()
    {
        FakeLease lease = new();
        var factory = new ScriptedPumpFactory
        {
            ThrowFirstStartedReadCount = true,
        };
        Assert.IsTrue(TryCreateCore(lease, factory,
            out Switch2ProUsbRuntimeOwner owner, out _, out var created),
            created.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        var coordinator =
            new Switch2ProUsbRuntimeRegistrationCoordinator(table);
        Assert.IsTrue(coordinator.TryOpen(1, out _));

        Assert.IsFalse(coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out var token,
            out var failure));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            PrepareRejected, failure.Kind);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.AbortedUnpublished,
            owner.State);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void AttentionDuringActiveCallbackRetriesAfterCallbackExit()
    {
        CreateScriptedCoordinator(out var coordinator, out var table,
            out var owner, out _);
        using ManualResetEventSlim callbackEntered = new(false);
        using ManualResetEventSlim releaseCallback = new(false);
        Assert.IsTrue(coordinator.TryAttach(owner, (_, _, report) =>
        {
            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                callbackEntered.Set();
                releaseCallback.Wait();
            }
        }, 2_000, out var token, out _));

        Task<bool> first = Task.Run(() =>
            ((ISwitch2ProUsbInputSink)owner).TryPublish(CreateProFrame(
                DeviceGeneration, TransportGeneration, 1)));
        Assert.IsTrue(callbackEntered.Wait(1_000));
        Assert.IsFalse(((ISwitch2ProUsbInputSink)owner).TryPublish(
            CreateProFrame(DeviceGeneration, TransportGeneration, 2)));
        Assert.AreEqual(InputControllerSlotState.Attached,
            table.GetSnapshot()[token.Slot].State,
            "Retirement cannot consume the callback's live report lease.");

        releaseCallback.Set();
        Assert.IsTrue(first.Wait(1_000) && first.Result);
        Assert.IsTrue(WaitForSlotState(table, token.Slot,
            InputControllerSlotState.Removed, 2_000),
            "The retained attention intent was not retried after callback exit.");
    }

    [TestMethod]
    public void AttentionOutlivingPolicyTimeoutRetriesAfterActivationCommit()
    {
        var lease = new FakeLease();
        var factory = new ScriptedPumpFactory
        {
            AttentionDuringCommit = Switch2ProUsbInputReadPumpFailure.
                ReadStartRejected,
            BlockCommitAfterAttention = true,
        };
        Assert.IsTrue(TryCreateCore(lease, factory,
            out Switch2ProUsbRuntimeOwner owner, out _, out var created),
            created.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        var coordinator =
            new Switch2ProUsbRuntimeRegistrationCoordinator(table, 20);
        Assert.IsTrue(coordinator.TryOpen(1, out _));

        InputControllerSlotToken token = default;
        Switch2ProUsbRuntimeRegistrationFailure attachFailure = default;
        Task<bool> attach = Task.Run(() => coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out token,
            out attachFailure));
        Assert.IsTrue(factory.Pump.AttentionRaised.Wait(1_000));
        Assert.IsFalse(attach.Wait(80),
            "The commit should remain blocked beyond the attention timeout.");
        InputControllerSlotSnapshot pending = table.GetSnapshot().Single();
        Assert.AreEqual(InputControllerSlotState.Attached, pending.State);
        Assert.IsTrue(pending.ActivationPending);

        factory.Pump.AllowCommitReturn.Set();
        Assert.IsTrue(attach.Wait(2_000));
        Assert.IsTrue(attach.Result, attachFailure.Kind.ToString());
        Assert.IsTrue(WaitForSlotState(table, token.Slot,
            InputControllerSlotState.Removed, 2_000),
            "Setup completion did not reschedule retained lifecycle attention.");
    }

    [TestMethod]
    public void NativeAttentionAtCommitCannotBeatBindingAdmission()
    {
        var lease = new FakeLease();
        var factory = new ScriptedPumpFactory
        {
            AttentionDuringCommit = Switch2ProUsbInputReadPumpFailure.
                ReadStartRejected,
        };
        Assert.IsTrue(TryCreateCore(lease, factory,
            out Switch2ProUsbRuntimeOwner owner, out _, out var created),
            created.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        var coordinator =
            new Switch2ProUsbRuntimeRegistrationCoordinator(table);
        Assert.IsTrue(coordinator.TryOpen(1, out _));
        int attentionKind = 0;
        owner.LifecycleAttention += (_, evidence) => Interlocked.Exchange(
            ref attentionKind, (int)evidence.Kind);
        factory.Pump.BlockCommitAfterAttention = true;

        InputControllerSlotToken token = default;
        Switch2ProUsbRuntimeRegistrationFailure attached = default;
        Task<bool> attach = Task.Run(() => coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out token, out attached));
        Assert.IsTrue(factory.Pump.AttentionRaised.Wait(1_000));
        object lifecycleGate = GetLifecycleGate(coordinator);
        Assert.IsTrue(Task.Run(() =>
        {
            lock (lifecycleGate)
            {
                return true;
            }
        }).Wait(1_000),
            "A fallible owner commit must not execute under the coordinator lifecycle lock.");
        Assert.AreEqual(InputControllerSlotState.Attached,
            table.GetSnapshot().Single().State,
            "Attention may queue at commit, but cannot retire before setup commits.");
        Assert.IsFalse(attach.Wait(50));
        factory.Pump.AllowCommitReturn.Set();
        Assert.IsTrue(attach.Wait(2_000));
        Assert.IsTrue(attach.Result, attached.Kind.ToString());
        Assert.IsTrue(WaitForSlotState(table, token.Slot,
            InputControllerSlotState.Removed, 2_000));
        Assert.AreEqual((int)Switch2ProUsbRuntimeLifecycleAttentionKind.
            NativeReadFailure, Volatile.Read(ref attentionKind));
    }

    [TestMethod]
    public void TimedOutCloseRetainsIntentAndRetryJoinsPendingActivation()
    {
        CreateScriptedCoordinator(out var coordinator, out var table,
            out var owner, out ScriptedPumpFactory factory);
        factory.Pump.BlockCommitBeforeReturn = true;

        InputControllerSlotToken token = default;
        Switch2ProUsbRuntimeRegistrationFailure attachFailure = default;
        Task<bool> attach = Task.Run(() => coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out token,
            out attachFailure));
        Assert.IsTrue(factory.Pump.CommitEntered.Wait(1_000));
        InputControllerSlotSnapshot pending = table.GetSnapshot().Single();
        Assert.AreEqual(InputControllerSlotState.Attached, pending.State);
        Assert.IsTrue(pending.ActivationPending);

        Assert.IsFalse(coordinator.TryClose(1, 30,
            out var timedOutClose));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            TableRejected, timedOutClose.Kind);
        Assert.AreEqual(InputControllerSlotTableFailure.TimedOut,
            timedOutClose.TableFailure);

        var replacementFactory = new ScriptedPumpFactory();
        Assert.IsTrue(TryCreateCore(new FakeLease(), replacementFactory,
            out Switch2ProUsbRuntimeOwner replacement, out _,
            out var replacementCreated), replacementCreated.Kind.ToString());
        Assert.IsFalse(coordinator.TryAttach(replacement,
            static (_, _, _) => { }, 200, out _,
            out var rejectedDuringClose));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            TableRejected, rejectedDuringClose.Kind);
        Assert.AreEqual(InputControllerSlotTableFailure.Closed,
            rejectedDuringClose.TableFailure);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Created,
            replacement.State,
            "A retained close intent cannot mutate a later owner's lifetime.");

        Switch2ProUsbRuntimeRegistrationFailure retryFailure = default;
        Task<bool> retry = Task.Run(() => coordinator.TryClose(1, 2_000,
            out retryFailure));
        Assert.IsFalse(retry.Wait(50));
        factory.Pump.AllowCommitReturn.Set();
        Assert.IsTrue(attach.Wait(2_000));
        Assert.IsTrue(attach.Result, attachFailure.Kind.ToString());
        Assert.IsTrue(retry.Wait(2_000));
        Assert.IsTrue(retry.Result, retryFailure.Kind.ToString());
        Assert.IsFalse(table.IsOpen);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void PostLinearizationCloseTimeoutRetainsExactEpochForRetry()
    {
        using var terminalScheduler = new BlockingTerminalScheduler();
        var lease = new FakeLease();
        var factory = new ScriptedPumpFactory();
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));
        Assert.IsTrue(Switch2ProUsbRuntimeOwner.TryCreateCore(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), factory, terminalScheduler,
            DeviceGeneration, TransportGeneration, QpcFrequency,
            calibration, 2_000, out Switch2ProUsbRuntimeOwner owner,
            out _, out Switch2ProUsbRuntimeCreateFailure created),
            created.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        var coordinator =
            new Switch2ProUsbRuntimeRegistrationCoordinator(table);
        Assert.IsTrue(coordinator.TryOpen(1, out var opened),
            opened.Kind.ToString());
        Assert.IsTrue(coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out var token,
            out var attached), attached.Kind.ToString());

        Switch2ProUsbRuntimeRegistrationFailure removalFailure = default;
        Task<bool> removal = Task.Run(() => coordinator.TryRemove(token,
            5_000, out removalFailure));
        try
        {
            Assert.IsTrue(terminalScheduler.Scheduled.Wait(1_000),
                "Explicit removal never reached terminal publication.");
            Assert.AreEqual(InputControllerSlotState.Retiring,
                table.GetSnapshot()[token.Slot].State);

            Assert.IsFalse(coordinator.TryClose(1, 40,
                out var observerTimeout));
            Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
                DrainTimedOut, observerTimeout.Kind);
            Assert.IsFalse(observerTimeout.RequiresQuarantine,
                "A close observer timeout must not quarantine removal owned by another caller.");
            Assert.IsFalse(table.IsOpen,
                "The close timeout occurred after table-close linearization.");

            Assert.IsFalse(coordinator.TryOpen(2,
                out var fencedOpen));
            Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
                TableRejected, fencedOpen.Kind);
            Assert.AreEqual(InputControllerSlotTableFailure.Busy,
                fencedOpen.TableFailure,
                "An incomplete exact close epoch must fence the next service generation.");

            terminalScheduler.Release.Set();
            Assert.IsTrue(removal.Wait(2_000),
                "Explicit removal did not finish after terminal publication was released.");
            Assert.IsTrue(removal.Result, removalFailure.Kind.ToString());

            Assert.IsTrue(coordinator.TryClose(1, 2_000,
                out var resumedClose), resumedClose.Kind.ToString());
            Assert.IsTrue(coordinator.TryClose(1, 2_000,
                out var cachedClose), cachedClose.Kind.ToString());
            Assert.IsTrue(coordinator.TryOpen(2, out var reopened),
                reopened.Kind.ToString());
            Assert.IsTrue(coordinator.TryClose(2, 2_000,
                out var finalClose), finalClose.Kind.ToString());
        }
        finally
        {
            terminalScheduler.Release.Set();
            removal.Wait(2_000);
        }
    }

    [TestMethod]
    public void CallbackRetryCannotCancelPreviouslyRetainedCloseIntent()
    {
        CreateScriptedCoordinator(out var coordinator, out var table,
            out var owner, out ScriptedPumpFactory factory);
        factory.Pump.BlockCommitBeforeReturn = true;
        using ManualResetEventSlim callbackEntered = new(false);
        using ManualResetEventSlim releaseCallback = new(false);
        InputControllerSlotToken token = default;
        Switch2ProUsbRuntimeRegistrationFailure attachFailure = default;
        Task<bool> attach = Task.Run(() => coordinator.TryAttach(owner,
            (_, _, report) =>
            {
                if (report.Kind == Switch2RuntimeReportKind.Regular)
                {
                    callbackEntered.Set();
                    releaseCallback.Wait();
                }
            }, 2_000, out token,
            out attachFailure));
        Assert.IsTrue(factory.Pump.CommitEntered.Wait(1_000));
        Assert.IsFalse(coordinator.TryClose(1, 20, out var timedOut));
        Assert.AreEqual(InputControllerSlotTableFailure.TimedOut,
            timedOut.TableFailure);
        factory.Pump.AllowCommitReturn.Set();
        Assert.IsTrue(attach.Wait(2_000) && attach.Result,
            attachFailure.Kind.ToString());

        Task<bool> publish = Task.Run(() =>
            ((ISwitch2ProUsbInputSink)owner).TryPublish(CreateProFrame(
                DeviceGeneration, TransportGeneration, 3)));
        Assert.IsTrue(callbackEntered.Wait(1_000));
        Assert.IsFalse(coordinator.TryClose(1, 100,
            out var callbackClose));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            CallbackActive, callbackClose.Kind);

        var replacementFactory = new ScriptedPumpFactory();
        Assert.IsTrue(TryCreateCore(new FakeLease(), replacementFactory,
            out Switch2ProUsbRuntimeOwner replacement, out _,
            out var replacementCreated), replacementCreated.Kind.ToString());
        Assert.IsFalse(coordinator.TryAttach(replacement,
            static (_, _, _) => { }, 100, out _, out var rejected));
        Assert.AreEqual(InputControllerSlotTableFailure.Closed,
            rejected.TableFailure,
            "A callback-active retry cannot cancel the earlier close epoch.");

        releaseCallback.Set();
        Assert.IsTrue(publish.Wait(1_000) && publish.Result);
        Assert.IsTrue(coordinator.TryClose(1, 2_000, out var closed),
            closed.Kind.ToString());
        Assert.IsFalse(table.IsOpen);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void RealPumpNativeFailureAutomaticallyRetiresAttachedSlot()
    {
        FakeLease lease = new()
        {
            CompleteSynchronously = false,
            MaximumSuccessfulBegins = 0,
        };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner, out _);
        var table = new InputControllerRegistrationTable(1);
        var coordinator =
            new Switch2ProUsbRuntimeRegistrationCoordinator(table);
        Assert.IsTrue(coordinator.TryOpen(1, out _));
        int attentionKind = 0;
        int terminalCount = 0;
        owner.LifecycleAttention += (_, evidence) => Interlocked.Exchange(
            ref attentionKind, (int)evidence.Kind);

        Assert.IsTrue(coordinator.TryAttach(owner, (_, _, report) =>
        {
            if (report.Kind == Switch2RuntimeReportKind.TerminalNeutral)
            {
                Interlocked.Increment(ref terminalCount);
            }
        }, 2_000, out var token, out var attached),
            attached.Kind.ToString());
        Assert.IsTrue(WaitForSlotState(table, token.Slot,
            InputControllerSlotState.Removed, 2_000));
        Assert.AreEqual(1, Volatile.Read(ref terminalCount));
        Assert.AreEqual((int)Switch2ProUsbRuntimeLifecycleAttentionKind.
            NativeReadFailure, Volatile.Read(ref attentionKind));
    }

    [DataTestMethod]
    [DataRow(Switch2ProUsbNativeReadStatus.DeviceRemoved)]
    [DataRow(Switch2ProUsbNativeReadStatus.Failed)]
    [DataRow(Switch2ProUsbNativeReadStatus.Cancelled)]
    public void RealPumpCompletedNativeFailureRetiresAttachedSlot(
        Switch2ProUsbNativeReadStatus status)
    {
        var lease = new FakeLease { CompleteSynchronously = false };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner, out _);
        var table = new InputControllerRegistrationTable(1);
        var coordinator = new Switch2ProUsbRuntimeRegistrationCoordinator(table);
        Assert.IsTrue(coordinator.TryOpen(1, out _));
        int terminalCount = 0;
        Assert.IsTrue(coordinator.TryAttach(owner, (_, _, report) =>
        {
            if (report.Kind == Switch2RuntimeReportKind.TerminalNeutral)
                Interlocked.Increment(ref terminalCount);
        }, 2_000, out var token, out var attached), attached.Kind.ToString());
        Assert.IsTrue(lease.RetirementEntered.Wait(1_000));
        try
        {
            // A native completion closes the transport before the real pump
            // sees its retirement result, exactly as in the b53 unplug dump.
            lease.CompleteNativeFailure(status);
            Assert.IsTrue(WaitForSlotState(table, token.Slot,
                InputControllerSlotState.Removed, 2_000),
                "A failed completed read must not leave an active controller slot.");
            Assert.AreEqual(1, Volatile.Read(ref terminalCount));
            Assert.AreEqual(1, lease.BeginCount, "Failed input must not rearm.");
            Assert.AreEqual(1, lease.DisposeCount);
        }
        finally
        {
            coordinator.TryClose(1, 2_000, out _);
        }
    }

    [TestMethod]
    public void MalformedFrameAttentionAutomaticallyRetiresAttachedSlot()
    {
        CreateScriptedCoordinator(out var coordinator, out var table,
            out var owner, out _);
        int attentionKind = 0;
        owner.LifecycleAttention += (_, evidence) => Interlocked.Exchange(
            ref attentionKind, (int)evidence.Kind);
        Assert.IsTrue(coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out var token, out _));

        Assert.IsFalse(((ISwitch2ProUsbInputSink)owner).TryPublish(
            CreateProFrame(DeviceGeneration + 1, TransportGeneration)));
        Assert.IsTrue(WaitForSlotState(table, token.Slot,
            InputControllerSlotState.Removed, 2_000));
        Assert.AreEqual((int)Switch2ProUsbRuntimeLifecycleAttentionKind.
            InputRejected, Volatile.Read(ref attentionKind));
    }

    [TestMethod]
    public void MappingExceptionSignalsSubscriberAttentionAndStillNeutralizes()
    {
        CreateScriptedCoordinator(out var coordinator, out var table,
            out var owner, out _);
        int attentionKind = 0;
        int terminalCount = 0;
        owner.LifecycleAttention += (_, evidence) => Interlocked.Exchange(
            ref attentionKind, (int)evidence.Kind);
        Assert.IsTrue(coordinator.TryAttach(owner, (_, _, report) =>
        {
            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                throw new InvalidOperationException("synthetic mapping fault");
            }
            Interlocked.Increment(ref terminalCount);
        }, 2_000, out var token, out _));

        Assert.IsFalse(((ISwitch2ProUsbInputSink)owner).TryPublish(
            CreateProFrame(DeviceGeneration, TransportGeneration)));
        Assert.IsTrue(WaitForSlotState(table, token.Slot,
            InputControllerSlotState.Removed, 2_000));
        Assert.AreEqual(1, Volatile.Read(ref terminalCount));
        Assert.AreEqual((int)Switch2ProUsbRuntimeLifecycleAttentionKind.
            SubscriberRejected, Volatile.Read(ref attentionKind));
    }

    [TestMethod]
    public void TerminalMappingExceptionCannotAcknowledgeAndQuarantines()
    {
        CreateScriptedCoordinator(out var coordinator, out var table,
            out var owner, out _);
        Assert.IsTrue(coordinator.TryAttach(owner, (_, _, report) =>
        {
            if (report.Kind == Switch2RuntimeReportKind.TerminalNeutral)
            {
                throw new InvalidOperationException(
                    "synthetic terminal mapping fault");
            }
        }, 2_000, out var token, out _));

        Assert.IsFalse(coordinator.TryRemove(token, 2_000,
            out var removal));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            TerminalNeutralRejected, removal.Kind);
        InputControllerSlotSnapshot snapshot =
            table.GetSnapshot()[token.Slot];
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            snapshot.State);
        Assert.IsFalse(snapshot.TerminalNeutralAcknowledged);
        Assert.AreEqual(InputControllerSlotQuarantineReason.
            TerminalNeutralNotObserved, snapshot.QuarantineReason);
    }

    [TestMethod]
    public void PostTableCommitRejectionNeverRollsBackAttachedSlot()
    {
        var lease = new FakeLease();
        var factory = new ScriptedPumpFactory { RejectCommit = true };
        Assert.IsTrue(TryCreateCore(lease, factory,
            out Switch2ProUsbRuntimeOwner owner, out _, out var created),
            created.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        var coordinator =
            new Switch2ProUsbRuntimeRegistrationCoordinator(table);
        Assert.IsTrue(coordinator.TryOpen(1, out _));

        Assert.IsFalse(coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out var token,
            out var attachment));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            CommitRejected, attachment.Kind);
        Assert.IsTrue(attachment.RequiresQuarantine);
        InputControllerSlotSnapshot snapshot =
            table.GetSnapshot()[token.Slot];
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            snapshot.State,
            "A post-Attached commit failure must never use Bound rollback.");
        Assert.AreEqual(token, snapshot.Token);
        Assert.AreEqual(InputControllerSlotQuarantineReason.
            ExternalLifecycleFailure, snapshot.QuarantineReason);
    }

    [TestMethod]
    public void ReentrantAndCrossThreadCallbackRemovalAreRejectedBoundedly()
    {
        CreateScriptedCoordinator(out var coordinator, out _, out var owner,
            out _);
        InputControllerSlotToken token = default;
        Switch2ProUsbRuntimeRegistrationFailureKind sameThread = default;
        Switch2ProUsbRuntimeRegistrationFailureKind otherThread = default;
        Assert.IsTrue(coordinator.TryAttach(owner, (_, _, report) =>
        {
            if (report.Kind != Switch2RuntimeReportKind.Regular)
            {
                return;
            }
            Assert.IsFalse(coordinator.TryRemove(token, 1_000,
                out var reentrant));
            sameThread = reentrant.Kind;
            Task task = Task.Run(() =>
            {
                Assert.IsFalse(coordinator.TryRemove(token, 1_000,
                    out var crossThread));
                otherThread = crossThread.Kind;
            });
            Assert.IsTrue(task.Wait(1_000),
                "Cross-thread removal waited on the callback's own lease.");
        }, 2_000, out token, out _));

        Assert.IsTrue(((ISwitch2ProUsbInputSink)owner).TryPublish(
            CreateProFrame(DeviceGeneration, TransportGeneration)));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            ReentrantRemoval, sameThread);
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            CallbackActive, otherThread);
        Assert.IsTrue(coordinator.TryRemove(token, 2_000, out var removed),
            removed.Kind.ToString());
    }

    [TestMethod]
    public void RemovalWaitsForRuntimeTailAfterTableLeaseDrains()
    {
        CreateScriptedCoordinator(out var coordinator, out _, out var owner,
            out _);
        Assert.IsTrue(coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out var token, out _));
        using ManualResetEventSlim tailEntered = new(false);
        using ManualResetEventSlim releaseTail = new(false);
        owner.RuntimeInputDevice.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.Regular)
            {
                tailEntered.Set();
                releaseTail.Wait();
            }
        };

        Task<bool> publish = Task.Run(() =>
            ((ISwitch2ProUsbInputSink)owner).TryPublish(CreateProFrame(
                DeviceGeneration, TransportGeneration)));
        Assert.IsTrue(tailEntered.Wait(1_000));
        Switch2ProUsbRuntimeRegistrationFailure removalFailure = default;
        Task<bool> removal = Task.Run(() => coordinator.TryRemove(token,
            2_000, out removalFailure));
        Assert.IsFalse(removal.Wait(50),
            "Removal skipped the runtime post-handler publication tail.");
        releaseTail.Set();
        Assert.IsTrue(publish.Wait(1_000) && publish.Result);
        Assert.IsTrue(removal.Wait(2_000));
        Assert.IsTrue(removal.Result, removalFailure.Kind.ToString());
    }

    [TestMethod]
    public void CloseWhileMappingCallbackActiveRejectsWithoutClosingTable()
    {
        CreateScriptedCoordinator(out var coordinator, out var table,
            out var owner, out _);
        using ManualResetEventSlim callbackEntered = new(false);
        using ManualResetEventSlim releaseCallback = new(false);
        Assert.IsTrue(coordinator.TryAttach(owner, (_, _, report) =>
        {
            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                callbackEntered.Set();
                releaseCallback.Wait();
            }
        }, 2_000, out var token, out _));
        Task<bool> publish = Task.Run(() =>
            ((ISwitch2ProUsbInputSink)owner).TryPublish(CreateProFrame(
                DeviceGeneration, TransportGeneration)));
        Assert.IsTrue(callbackEntered.Wait(1_000));

        Assert.IsFalse(coordinator.TryClose(1, 1_000,
            out var closeFailure));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            CallbackActive, closeFailure.Kind);
        Assert.IsTrue(table.IsOpen);
        Assert.AreEqual(InputControllerSlotState.Attached,
            table.GetSnapshot()[token.Slot].State);
        releaseCallback.Set();
        Assert.IsTrue(publish.Wait(1_000) && publish.Result);
        Assert.IsTrue(coordinator.TryClose(1, 2_000, out var closed),
            closed.Kind.ToString());
    }

    [TestMethod]
    public void RuntimeTailTimeoutQuarantinesExactSlot()
    {
        CreateScriptedCoordinator(out var coordinator, out var table,
            out var owner, out _);
        Assert.IsTrue(coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out var token, out _));
        using ManualResetEventSlim tailEntered = new(false);
        using ManualResetEventSlim releaseTail = new(false);
        owner.RuntimeInputDevice.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.Regular)
            {
                tailEntered.Set();
                releaseTail.Wait();
            }
        };
        Task<bool> publish = Task.Run(() =>
            ((ISwitch2ProUsbInputSink)owner).TryPublish(CreateProFrame(
                DeviceGeneration, TransportGeneration)));
        Assert.IsTrue(tailEntered.Wait(1_000));

        Assert.IsFalse(coordinator.TryRemove(token, 40,
            out var removal));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            DrainTimedOut, removal.Kind);
        InputControllerSlotSnapshot quarantined =
            table.GetSnapshot()[token.Slot];
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            quarantined.State);
        Assert.AreEqual(InputControllerSlotQuarantineReason.DrainTimedOut,
            quarantined.QuarantineReason);
        releaseTail.Set();
        Assert.IsTrue(publish.Wait(1_000));
    }

    [TestMethod]
    public void StaleCrossGenerationAndZeroTimeoutOperationsFailClosed()
    {
        CreateScriptedCoordinator(out var coordinator, out _, out var owner,
            out _);
        Assert.IsTrue(coordinator.TryAttach(owner,
            static (_, _, _) => { }, 2_000, out var token, out _));
        var foreignTable = new InputControllerRegistrationTable(1);
        var foreign = new Switch2ProUsbRuntimeRegistrationCoordinator(
            foreignTable);
        Assert.IsTrue(foreign.TryOpen(1, out _));

        Assert.IsFalse(foreign.TryRemove(token, 1_000, out var cross));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            StaleToken, cross.Kind);
        Assert.IsFalse(coordinator.TryRemove(token, 0,
            out var zeroRemove));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            InvalidTimeout, zeroRemove.Kind);
        Assert.IsFalse(coordinator.TryClose(1, 0, out var zeroClose));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            InvalidTimeout, zeroClose.Kind);
        Assert.IsTrue(coordinator.TryRemove(token, 2_000, out _));
        Assert.IsFalse(coordinator.TryRemove(token, 1_000, out var stale));
        Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
            StaleToken, stale.Kind);
    }

    [TestMethod]
    public void ConcurrentCrossTableAttachHasOneSlotAdoptionWinner()
    {
        for (int iteration = 0; iteration < 20; iteration++)
        {
            var factory = new ScriptedPumpFactory();
            Assert.IsTrue(TryCreateCore(new FakeLease(), factory,
                out Switch2ProUsbRuntimeOwner owner, out _, out var created),
                created.Kind.ToString());
            var firstTable = new InputControllerRegistrationTable(1);
            var secondTable = new InputControllerRegistrationTable(1);
            var first = new Switch2ProUsbRuntimeRegistrationCoordinator(
                firstTable);
            var second = new Switch2ProUsbRuntimeRegistrationCoordinator(
                secondTable);
            Assert.IsTrue(first.TryOpen(301, out _));
            Assert.IsTrue(second.TryOpen(302, out _));

            InputControllerSlotToken firstToken = default;
            InputControllerSlotToken secondToken = default;
            Switch2ProUsbRuntimeRegistrationFailure firstFailure = default;
            Switch2ProUsbRuntimeRegistrationFailure secondFailure = default;
            using var start = new ManualResetEventSlim(false);
            Task<bool> firstAttach = Task.Run(() =>
            {
                start.Wait();
                return first.TryAttach(owner, static (_, _, _) => { }, 2_000,
                    out firstToken, out firstFailure);
            });
            Task<bool> secondAttach = Task.Run(() =>
            {
                start.Wait();
                return second.TryAttach(owner, static (_, _, _) => { }, 2_000,
                    out secondToken, out secondFailure);
            });
            start.Set();
            Assert.IsTrue(Task.WaitAll(new Task[] { firstAttach, secondAttach },
                4_000));

            Assert.AreNotEqual(firstAttach.Result, secondAttach.Result,
                $"Iteration {iteration}: exactly one coordinator must own " +
                "the physical lifetime.");
            Switch2ProUsbRuntimeRegistrationCoordinator winner =
                firstAttach.Result ? first : second;
            InputControllerRegistrationTable winnerTable = firstAttach.Result ?
                firstTable : secondTable;
            InputControllerRegistrationTable loserTable = firstAttach.Result ?
                secondTable : firstTable;
            InputControllerSlotToken winnerToken = firstAttach.Result ?
                firstToken : secondToken;
            Switch2ProUsbRuntimeRegistrationFailure loserFailure =
                firstAttach.Result ? secondFailure : firstFailure;

            Assert.AreEqual(Switch2ProUsbRuntimeRegistrationFailureKind.
                SlotAdoptionRejected, loserFailure.Kind,
                $"Iteration {iteration}: the foreign table must lose at the " +
                "owner-adoption boundary.");
            Assert.AreEqual(InputControllerSlotState.Attached,
                winnerTable.GetSnapshot().Single().State);
            Assert.AreEqual(InputControllerSlotState.Removed,
                loserTable.GetSnapshot().Single().State,
                "The losing table must roll back only its local Bound slot.");
            Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Active, owner.State,
                "The losing coordinator aborted the winning owner.");
            Assert.IsTrue(winner.TryRemove(winnerToken, 2_000,
                out var removed), removed.Kind.ToString());
        }
    }

    [TestMethod]
    public void WarmedTypedRegularReportPathAllocatesZeroBytes()
    {
        CreateScriptedCoordinator(out var coordinator, out _, out var owner,
            out _);
        int mapped = 0;
        Assert.IsTrue(coordinator.TryAttach(owner, (_, _, report) =>
        {
            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                mapped++;
            }
        }, 2_000, out var token, out _));
        var frames = new Switch2CanonicalInputFrame[65];
        for (uint counter = 1; counter <= frames.Length; counter++)
        {
            frames[counter - 1] = CreateProFrame(DeviceGeneration,
                TransportGeneration, counter);
        }

        Assert.IsTrue(((ISwitch2ProUsbInputSink)owner).TryPublish(frames[0]));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = true;
        for (int index = 1; index < frames.Length; index++)
        {
            accepted &= ((ISwitch2ProUsbInputSink)owner).TryPublish(
                frames[index]);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(accepted);
        Assert.AreEqual(frames.Length, mapped);
        Assert.AreEqual(0L, allocated,
            "The warmed typed adapter path must not allocate per report.");
        Assert.IsTrue(coordinator.TryRemove(token, 2_000, out _));
    }

    private static void CreateScriptedCoordinator(
        out Switch2ProUsbRuntimeRegistrationCoordinator coordinator,
        out InputControllerRegistrationTable table,
        out Switch2ProUsbRuntimeOwner owner,
        out ScriptedPumpFactory factory)
    {
        factory = new ScriptedPumpFactory();
        Assert.IsTrue(TryCreateCore(new FakeLease(), factory, out owner,
            out _, out Switch2ProUsbRuntimeCreateFailure created),
            created.Kind.ToString());
        table = new InputControllerRegistrationTable(1);
        coordinator = new Switch2ProUsbRuntimeRegistrationCoordinator(table);
        Assert.IsTrue(coordinator.TryOpen(1, out var opened),
            opened.Kind.ToString());
    }

    private static bool WaitForSlotState(
        InputControllerRegistrationTable table, int slot,
        InputControllerSlotState expected, int timeoutMilliseconds) =>
        SpinWait.SpinUntil(() => table.GetSnapshot()[slot].State == expected,
            timeoutMilliseconds);

    private static object GetLifecycleGate(
        Switch2ProUsbRuntimeRegistrationCoordinator coordinator)
    {
        System.Reflection.FieldInfo field = typeof(
            Switch2ProUsbRuntimeRegistrationCoordinator).GetField(
                "lifecycleGate", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        object gate = field.GetValue(coordinator);
        Assert.IsNotNull(gate);
        return gate;
    }

    private sealed class BlockingTerminalScheduler :
        ISwitch2ProUsbRuntimeTerminalScheduler, IDisposable
    {
        internal ManualResetEventSlim Scheduled { get; } = new(false);

        internal ManualResetEventSlim Release { get; } = new(false);

        public bool TrySchedule(
            Func<Switch2TerminalNeutralRequestResult> callback,
            out Task<Switch2TerminalNeutralRequestResult> task)
        {
            task = Task.Run(() =>
            {
                Scheduled.Set();
                Release.Wait();
                return callback();
            });
            return true;
        }

        public void Dispose()
        {
            Release.Set();
            Scheduled.Dispose();
            Release.Dispose();
        }
    }

}
