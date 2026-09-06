using System.Reflection;
using System.Threading;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public sealed class Switch2ControlServiceSlotRegistrationParticipantTests
{
    [TestMethod]
    public void SuccessfulLifecycleStagesBeforeCommitAndUsesOneReportSubscription()
    {
        Fixture fixture = CreateFixture(11, 101);
        var order = new List<string>();
        var inner = new FakeParticipant(fixture.Registration, order);
        var host = new FakeHost(order);
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(1, out var opened),
            opened.Kind.ToString());
        Assert.IsTrue(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000,
            out InputControllerSlotToken token, out var attached),
            attached.Kind.ToString());
        Assert.IsTrue(core.TryRemove(token, 2_000, out var removed),
            removed.Kind.ToString());

        Assert.AreEqual(1, inner.SubscribeCount,
            "The wrapped participant must remain the sole Report subscriber.");
        Assert.AreEqual(1, host.PrepareCount);
        Assert.AreEqual(1, host.RegularCount);
        Assert.AreEqual(1, host.TerminalCount);
        Assert.AreEqual(1, host.RemoveCount);
        Assert.AreEqual(0, host.AbortCount);
        Assert.IsTrue(IndexOf(order, "host.prepare") <
            IndexOf(order, "inner.prepare"));
        Assert.IsTrue(IndexOf(order, "host.prepare") <
            IndexOf(order, "inner.commit"));
        Assert.IsTrue(IndexOf(order, "host.terminal") <
            IndexOf(order, "inner.remove"));
        Assert.IsTrue(IndexOf(order, "inner.remove") <
            IndexOf(order, "host.remove"));
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[0].State);
    }

    [TestMethod]
    public void HostCallsRunOutsideDecoratorCoreAndTableGates()
    {
        Fixture fixture = CreateFixture(12, 102);
        var inner = new FakeParticipant(fixture.Registration, new());
        var host = new FakeHost(new());
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        object tableGate = typeof(InputControllerRegistrationTable).GetField(
            "gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(
                table)!;
        host.ExternalCallProbe = () =>
        {
            Assert.IsFalse(Monitor.IsEntered(decorated.LifecycleGate));
            Assert.IsFalse(Monitor.IsEntered(core.LifecycleGate));
            Assert.IsFalse(Monitor.IsEntered(tableGate));
        };

        Assert.IsTrue(core.TryOpen(2, out _));
        Assert.IsTrue(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token, out var failure),
            failure.Kind.ToString());
        Assert.IsTrue(core.TryRemove(token, 2_000, out failure),
            failure.Kind.ToString());
        Assert.AreEqual(4, host.ExternalProbeCount,
            "Prepare, regular, terminal, and remove must all be outside gates.");
    }

    [TestMethod]
    public void OccupiedLegacySlotRejectsBeforeTransportPrepareOrCommit()
    {
        Fixture fixture = CreateFixture(13, 103);
        var inner = new FakeParticipant(fixture.Registration, new());
        var host = new FakeHost(new())
        {
            PrepareResult = Switch2ControlServiceSlotHostResult.Reject(
                Switch2ControlServiceSlotHostOperation.Prepare,
                Switch2ControlServiceSlotHostFailureKind.SlotOccupied),
        };
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(3, out _));
        Assert.IsFalse(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token,
            out var failure));

        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.PrepareRejected,
            failure.Kind);
        Assert.AreEqual(0, inner.PrepareCount);
        Assert.AreEqual(0, inner.CommitCount);
        Assert.AreEqual(1, inner.AbortUnpublishedCount);
        Assert.AreEqual(0, host.AbortCount,
            "A proven Prepare rejection guarantees that no host mutation exists.");
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void UncertainPartialHostPrepareRequiresExactSymmetricAbort()
    {
        Fixture fixture = CreateFixture(14, 104);
        var inner = new FakeParticipant(fixture.Registration, new());
        var host = new FakeHost(new())
        {
            PrepareResult = Switch2ControlServiceSlotHostResult.Uncertain(
                Switch2ControlServiceSlotHostOperation.Prepare,
                Switch2ControlServiceSlotHostFailureKind.DependencyThrew),
        };
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(4, out _));
        Assert.IsFalse(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token,
            out var failure));

        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.DependencyThrew,
            failure.Kind);
        Assert.AreEqual(1, inner.AbortUnpublishedCount);
        Assert.AreEqual(1, host.AbortCount);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void UncertainAbortQuarantinesExactSlot()
    {
        Fixture fixture = CreateFixture(15, 105);
        var inner = new FakeParticipant(fixture.Registration, new());
        var host = new FakeHost(new())
        {
            PrepareResult = Switch2ControlServiceSlotHostResult.Uncertain(
                Switch2ControlServiceSlotHostOperation.Prepare,
                Switch2ControlServiceSlotHostFailureKind.DependencyThrew),
            AbortResult = Switch2ControlServiceSlotHostResult.Uncertain(
                Switch2ControlServiceSlotHostOperation.Abort,
                Switch2ControlServiceSlotHostFailureKind.CleanupRejected),
        };
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(5, out _));
        Assert.IsFalse(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token,
            out var failure));

        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void WrongSlotSenderAndGenerationNeverReachHost()
    {
        Fixture fixture = CreateFixture(16, 106);
        var inner = new FakeParticipant(fixture.Registration, new());
        var host = new FakeHost(new());
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        Assert.IsTrue(core.TryOpen(6, out _));
        Assert.IsTrue(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token, out _));
        int admittedRegular = host.RegularCount;

        Assert.ThrowsException<InvalidOperationException>(() =>
            decorated.MappingCallback(token.Slot + 1, fixture.Device,
                new Switch2RuntimeReportEventArgs(
                    Switch2RuntimeReportKind.Regular,
                    fixture.Registration.Generation)));
        Fixture foreign = CreateFixture(17, 107);
        Assert.ThrowsException<InvalidOperationException>(() =>
            decorated.MappingCallback(token.Slot, foreign.Device,
                new Switch2RuntimeReportEventArgs(
                    Switch2RuntimeReportKind.Regular,
                    fixture.Registration.Generation)));
        Assert.ThrowsException<InvalidOperationException>(() =>
            decorated.MappingCallback(token.Slot, fixture.Device,
                new Switch2RuntimeReportEventArgs(
                    Switch2RuntimeReportKind.Regular,
                    fixture.Registration.Generation + 1)));

        Assert.AreEqual(admittedRegular, host.RegularCount);
        Assert.IsTrue(core.TryRemove(token, 2_000, out _));
    }

    [TestMethod]
    public void HostCallbackReentrancyIsRejectedWithoutASecondDispatch()
    {
        Fixture fixture = CreateFixture(18, 108);
        var inner = new FakeParticipant(fixture.Registration, new());
        var host = new FakeHost(new());
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        Switch2RuntimeRegistrationParticipantResult reentrant = default;
        host.OnRegular = () => reentrant = decorated.TryUnsubscribe();
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(7, out _));
        Assert.IsTrue(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token, out _));
        Assert.IsTrue(reentrant.IsValid);
        Assert.AreEqual(
            Switch2RuntimeRegistrationParticipantOutcome.ProvenRejected,
            reentrant.Outcome);
        Assert.AreEqual(
            Switch2RuntimeRegistrationParticipantFailureKind.
                OperationAlreadyInProgress,
            reentrant.FailureKind);
        Assert.AreEqual(1, host.RegularCount);
        Assert.IsTrue(core.TryRemove(token, 2_000, out _));
    }

    [TestMethod]
    public void TerminalHostRejectionPreventsRemovalAndQuarantines()
    {
        Fixture fixture = CreateFixture(19, 109);
        var inner = new FakeParticipant(fixture.Registration, new());
        var host = new FakeHost(new())
        {
            TerminalResult = Switch2ControlServiceSlotHostResult.Reject(
                Switch2ControlServiceSlotHostOperation.
                    DispatchTerminalNeutral,
                Switch2ControlServiceSlotHostFailureKind.
                    TerminalNeutralRejected),
        };
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(8, out _));
        Assert.IsTrue(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token, out _));
        Assert.IsFalse(core.TryRemove(token, 2_000, out var failure));

        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.AreEqual(0, inner.RemoveCount);
        Assert.AreEqual(0, host.RemoveCount);
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void HostRemoveFailureCannotMakeSlotReusableAfterInnerRemoval()
    {
        Fixture fixture = CreateFixture(20, 110);
        var inner = new FakeParticipant(fixture.Registration, new());
        var host = new FakeHost(new())
        {
            RemoveResult = Switch2ControlServiceSlotHostResult.Reject(
                Switch2ControlServiceSlotHostOperation.Remove,
                Switch2ControlServiceSlotHostFailureKind.CleanupRejected),
        };
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(9, out _));
        Assert.IsTrue(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token, out _));
        Assert.IsFalse(core.TryRemove(token, 2_000, out var failure));

        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.AreEqual(1, inner.RemoveCount);
        Assert.AreEqual(1, host.RemoveCount);
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void ThrowingPrepareCanBeNeutralizedOnlyByExactAbortProof()
    {
        Fixture fixture = CreateFixture(21, 111);
        var inner = new FakeParticipant(fixture.Registration, new());
        var host = new FakeHost(new()) { ThrowPrepare = true };
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(10, out _));
        Assert.IsFalse(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token,
            out var failure));

        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.DependencyThrew,
            failure.Kind);
        Assert.AreEqual(1, host.AbortCount);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void RegularDispatchRejectionDuringCommitQuarantinesWithoutRemoval()
    {
        Fixture fixture = CreateFixture(22, 112);
        var inner = new FakeParticipant(fixture.Registration, new());
        var host = new FakeHost(new())
        {
            RegularResult = Switch2ControlServiceSlotHostResult.Reject(
                Switch2ControlServiceSlotHostOperation.DispatchRegular,
                Switch2ControlServiceSlotHostFailureKind.CallbackRejected),
        };
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(11, out _));
        Assert.IsFalse(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token,
            out var failure));

        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.AreEqual(1, inner.CommitCount);
        Assert.AreEqual(1, host.RegularCount);
        Assert.AreEqual(0, inner.RemoveCount);
        Assert.AreEqual(0, host.RemoveCount);
        Assert.AreEqual(0, host.AbortCount);
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void InnerPrepareRejectionAbortsPreviouslyPreparedHostSlot()
    {
        Fixture fixture = CreateFixture(23, 113);
        var inner = new FakeParticipant(fixture.Registration, new())
        {
            PrepareResult = Switch2RuntimeRegistrationParticipantResult.Reject(
                Switch2RuntimeRegistrationParticipantOperation.
                    PrepareActivation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    PrepareRejected),
        };
        var host = new FakeHost(new());
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(12, out _));
        Assert.IsFalse(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token,
            out var failure));

        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.PrepareRejected,
            failure.Kind);
        Assert.AreEqual(1, host.PrepareCount);
        Assert.AreEqual(1, inner.AbortUnpublishedCount);
        Assert.AreEqual(1, host.AbortCount);
        Assert.AreEqual(0, inner.CommitCount);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void WrongOperationHostResultIsUncertainAndNeverPreparesTransport()
    {
        Fixture fixture = CreateFixture(24, 114);
        var inner = new FakeParticipant(fixture.Registration, new());
        var host = new FakeHost(new())
        {
            PrepareResult = Switch2ControlServiceSlotHostResult.Success(
                Switch2ControlServiceSlotHostOperation.DispatchRegular),
        };
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(13, out _));
        Assert.IsFalse(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token,
            out var failure));

        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.DependencyThrew,
            failure.Kind);
        Assert.AreEqual(0, inner.PrepareCount);
        Assert.AreEqual(0, inner.CommitCount);
        Assert.AreEqual(1, host.AbortCount,
            "A wrong operation tag is uncertain mutation evidence and requires exact abort.");
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void InnerAbortFailurePreventsHostAbortAndQuarantines()
    {
        Fixture fixture = CreateFixture(25, 115);
        var inner = new FakeParticipant(fixture.Registration, new())
        {
            AbortUnpublishedResult =
                Switch2RuntimeRegistrationParticipantResult.Reject(
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortUnpublished,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        AbortRejected),
        };
        var host = new FakeHost(new())
        {
            PrepareResult = Switch2ControlServiceSlotHostResult.Uncertain(
                Switch2ControlServiceSlotHostOperation.Prepare,
                Switch2ControlServiceSlotHostFailureKind.DependencyThrew),
        };
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(14, out _));
        Assert.IsFalse(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token,
            out var failure));

        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.AreEqual(1, inner.AbortUnpublishedCount);
        Assert.AreEqual(0, host.AbortCount,
            "The slot host must remain installed until inner transport abort is proven.");
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void ExactRegularDispatchPathAllocatesNoManagedMemory()
    {
        Fixture fixture = CreateFixture(26, 116);
        var inner = new FakeParticipant(fixture.Registration, new());
        var host = new AllocationHost();
        var decorated = new Switch2ControlServiceSlotRegistrationParticipant(
            inner, host);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);

        Assert.IsTrue(core.TryOpen(15, out _));
        Assert.IsTrue(core.TryAttach(fixture.Registration, () => decorated,
            decorated.MappingCallback, 2_000, out var token, out _));
        var report = new Switch2RuntimeReportEventArgs(
            Switch2RuntimeReportKind.Regular,
            fixture.Registration.Generation);
        decorated.MappingCallback(token.Slot, fixture.Device, report);

        const int iterations = 20_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            decorated.MappingCallback(token.Slot, fixture.Device, report);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(iterations + 2, host.RegularCount,
            "The attach-time report and warm-up report precede the measured loop.");
        Assert.IsTrue(core.TryRemove(token, 2_000, out _));
    }

    private static int IndexOf(List<string> values, string value)
    {
        int index = values.IndexOf(value);
        Assert.IsTrue(index >= 0, $"Missing lifecycle marker {value}.");
        return index;
    }

    private static Fixture CreateFixture(ulong deviceGeneration,
        ulong transportGeneration)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(deviceGeneration,
            transportGeneration, Switch2Transport.Usb, out var device,
            out var createFailure), createFailure.ToString());
        var owner = new FakeOwner(device, deviceGeneration);
        Assert.IsTrue(InputControllerRegistration.TryCreate(device,
            deviceGeneration, InputControllerOwnershipKind.Switch2Runtime,
            hasHidInterface: false, hasPersistentIdentity: false, owner,
            out var registration, out var registrationFailure),
            registrationFailure.ToString());
        return new Fixture(device, registration);
    }

    private readonly record struct Fixture(Switch2RuntimeInputDevice Device,
        InputControllerRegistration Registration);

    private sealed class FakeOwner : IInputControllerRegistrationOwner
    {
        private readonly DS4Device device;
        private readonly ulong generation;

        internal FakeOwner(DS4Device device, ulong generation)
        {
            this.device = device;
            this.generation = generation;
        }

        public InputControllerOwnershipKind Kind =>
            InputControllerOwnershipKind.Switch2Runtime;

        public bool Authenticates(DS4Device candidate,
            ulong candidateGeneration) => ReferenceEquals(device, candidate) &&
            generation == candidateGeneration;

        public bool TryStopAndQuiesce(DS4Device candidate,
            ulong candidateGeneration, int timeoutMilliseconds,
            out InputControllerOwnerOperationFailure failure)
        {
            failure = InputControllerOwnerOperationFailure.None;
            return Authenticates(candidate, candidateGeneration);
        }

        public bool TryRemove(DS4Device candidate, ulong candidateGeneration,
            out InputControllerOwnerOperationFailure failure)
        {
            failure = InputControllerOwnerOperationFailure.None;
            return Authenticates(candidate, candidateGeneration);
        }
    }

    private sealed class FakeParticipant :
        ISwitch2RuntimeRegistrationParticipant
    {
        private readonly List<string> order;
        private Switch2RuntimeRegistrationCallbacks callbacks;

        internal FakeParticipant(InputControllerRegistration registration,
            List<string> order)
        {
            Registration = registration;
            this.order = order;
        }

        public InputControllerRegistration Registration { get; }

        internal int SubscribeCount { get; private set; }
        internal int PrepareCount { get; private set; }
        internal int CommitCount { get; private set; }
        internal int AbortUnpublishedCount { get; private set; }
        internal int RemoveCount { get; private set; }
        internal Switch2RuntimeRegistrationParticipantResult PrepareResult
        {
            get;
            set;
        } = Success(Switch2RuntimeRegistrationParticipantOperation.
            PrepareActivation);
        internal Switch2RuntimeRegistrationParticipantResult
            AbortUnpublishedResult { get; set; } = Success(
                Switch2RuntimeRegistrationParticipantOperation.
                    AbortUnpublished);

        public Switch2RuntimeRegistrationParticipantResult TryAdoptBoundSlot(
            in InputControllerSlotToken token)
        {
            order.Add("inner.adopt");
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        }

        public Switch2RuntimeRegistrationParticipantResult TrySubscribe(
            in Switch2RuntimeRegistrationCallbacks exactCallbacks)
        {
            order.Add("inner.subscribe");
            SubscribeCount++;
            callbacks = exactCallbacks;
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.Subscribe);
        }

        public Switch2RuntimeRegistrationParticipantResult
            TryPrepareActivation(int timeoutMilliseconds)
        {
            order.Add("inner.prepare");
            PrepareCount++;
            return PrepareResult;
        }

        public Switch2RuntimeRegistrationParticipantResult TryCommitPrepared(
            in InputControllerActivationCommitCredential activationCommit)
        {
            order.Add("inner.commit");
            CommitCount++;
            callbacks.ReportHandler(Registration.Device,
                new Switch2RuntimeReportEventArgs(
                    Switch2RuntimeReportKind.Regular,
                    Registration.Generation));
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.CommitPrepared);
        }

        public Switch2RuntimeRegistrationParticipantResult TryAbortPrepared(
            int timeoutMilliseconds)
        {
            order.Add("inner.abort-prepared");
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.AbortPrepared);
        }

        public Switch2RuntimeRegistrationParticipantResult
            TryAbortUnpublished(int timeoutMilliseconds)
        {
            order.Add("inner.abort-unpublished");
            AbortUnpublishedCount++;
            return AbortUnpublishedResult;
        }

        public Switch2RuntimeRegistrationParticipantResult TryArmRetirement(
            in InputControllerRetirementClaim claim)
        {
            order.Add("inner.arm-retirement");
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.ArmRetirement);
        }

        public Switch2RuntimeRegistrationParticipantResult
            TryWaitForPublicationAvailability(int timeoutMilliseconds)
        {
            order.Add("inner.wait-publication");
            return Success(Switch2RuntimeRegistrationParticipantOperation.
                WaitForPublicationAvailability);
        }

        public Switch2RuntimeRegistrationParticipantResult TryStopAndQuiesce(
            int timeoutMilliseconds)
        {
            order.Add("inner.stop");
            callbacks.ReportHandler(Registration.Device,
                new Switch2RuntimeReportEventArgs(
                    Switch2RuntimeReportKind.TerminalNeutral,
                    Registration.Generation));
            return Success(Switch2RuntimeRegistrationParticipantOperation.
                StopAndQuiesce);
        }

        public Switch2RuntimeRegistrationParticipantResult TryUnsubscribe()
        {
            order.Add("inner.unsubscribe");
            callbacks = default;
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.Unsubscribe);
        }

        public Switch2RuntimeRegistrationParticipantResult TryRemove()
        {
            order.Add("inner.remove");
            RemoveCount++;
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.Remove);
        }

        private static Switch2RuntimeRegistrationParticipantResult Success(
            Switch2RuntimeRegistrationParticipantOperation operation) =>
            Switch2RuntimeRegistrationParticipantResult.Success(operation);
    }

    private sealed class FakeHost : ISwitch2ControlServiceSlotHost
    {
        private readonly List<string> order;
        private InputControllerSlotToken preparedToken;

        internal FakeHost(List<string> order)
        {
            this.order = order;
        }

        internal Switch2ControlServiceSlotHostResult PrepareResult { get; set; }
            = Switch2ControlServiceSlotHostResult.Success(
                Switch2ControlServiceSlotHostOperation.Prepare);
        internal Switch2ControlServiceSlotHostResult AbortResult { get; set; }
            = Switch2ControlServiceSlotHostResult.Success(
                Switch2ControlServiceSlotHostOperation.Abort);
        internal Switch2ControlServiceSlotHostResult TerminalResult { get; set; }
            = Switch2ControlServiceSlotHostResult.Success(
                Switch2ControlServiceSlotHostOperation.
                    DispatchTerminalNeutral);
        internal Switch2ControlServiceSlotHostResult RegularResult { get; set; }
            = Switch2ControlServiceSlotHostResult.Success(
                Switch2ControlServiceSlotHostOperation.DispatchRegular);
        internal Switch2ControlServiceSlotHostResult RemoveResult { get; set; }
            = Switch2ControlServiceSlotHostResult.Success(
                Switch2ControlServiceSlotHostOperation.Remove);
        internal bool ThrowPrepare { get; set; }
        internal Action ExternalCallProbe { get; set; }
        internal Action OnRegular { get; set; }
        internal int ExternalProbeCount { get; private set; }
        internal int PrepareCount { get; private set; }
        internal int RegularCount { get; private set; }
        internal int TerminalCount { get; private set; }
        internal int AbortCount { get; private set; }
        internal int RemoveCount { get; private set; }

        public Switch2ControlServiceSlotHostResult TryPrepare(
            in Switch2ControlServiceSlotLease lease)
        {
            Probe();
            order.Add("host.prepare");
            PrepareCount++;
            if (ThrowPrepare)
            {
                throw new InvalidOperationException();
            }
            if (PrepareResult.Succeeded)
            {
                Assert.IsTrue(lease.IsValid);
                preparedToken = lease.Token;
            }
            return PrepareResult;
        }

        public Switch2ControlServiceSlotHostResult TryDispatch(
            in Switch2ControlServiceSlotLease lease, DS4Device sender,
            Switch2RuntimeReportEventArgs report)
        {
            Probe();
            Assert.AreEqual(preparedToken, lease.Token);
            Assert.AreSame(preparedToken.Registration.Device, sender);
            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                order.Add("host.regular");
                RegularCount++;
                OnRegular?.Invoke();
                return RegularResult;
            }

            order.Add("host.terminal");
            TerminalCount++;
            return TerminalResult;
        }

        public Switch2ControlServiceSlotHostResult TryAbort(
            in Switch2ControlServiceSlotLease lease)
        {
            Probe();
            order.Add("host.abort");
            AbortCount++;
            return AbortResult;
        }

        public Switch2ControlServiceSlotHostResult TryRemove(
            in Switch2ControlServiceSlotLease lease)
        {
            Probe();
            order.Add("host.remove");
            RemoveCount++;
            Assert.AreEqual(preparedToken, lease.Token);
            return RemoveResult;
        }

        private void Probe()
        {
            ExternalProbeCount++;
            ExternalCallProbe?.Invoke();
        }
    }

    private sealed class AllocationHost : ISwitch2ControlServiceSlotHost
    {
        private InputControllerSlotToken token;

        internal int RegularCount { get; private set; }

        public Switch2ControlServiceSlotHostResult TryPrepare(
            in Switch2ControlServiceSlotLease lease)
        {
            token = lease.Token;
            return Switch2ControlServiceSlotHostResult.Success(
                Switch2ControlServiceSlotHostOperation.Prepare);
        }

        public Switch2ControlServiceSlotHostResult TryDispatch(
            in Switch2ControlServiceSlotLease lease, DS4Device sender,
            Switch2RuntimeReportEventArgs report)
        {
            if (lease.Token != token ||
                !ReferenceEquals(sender, token.Registration.Device))
            {
                return Switch2ControlServiceSlotHostResult.Reject(
                    report.Kind == Switch2RuntimeReportKind.Regular ?
                        Switch2ControlServiceSlotHostOperation.DispatchRegular :
                        Switch2ControlServiceSlotHostOperation.
                            DispatchTerminalNeutral,
                    Switch2ControlServiceSlotHostFailureKind.InvalidCredential);
            }

            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                RegularCount++;
                return Switch2ControlServiceSlotHostResult.Success(
                    Switch2ControlServiceSlotHostOperation.DispatchRegular);
            }
            return Switch2ControlServiceSlotHostResult.Success(
                Switch2ControlServiceSlotHostOperation.
                    DispatchTerminalNeutral);
        }

        public Switch2ControlServiceSlotHostResult TryAbort(
            in Switch2ControlServiceSlotLease lease) =>
            Switch2ControlServiceSlotHostResult.Success(
                Switch2ControlServiceSlotHostOperation.Abort);

        public Switch2ControlServiceSlotHostResult TryRemove(
            in Switch2ControlServiceSlotLease lease) =>
            Switch2ControlServiceSlotHostResult.Success(
                Switch2ControlServiceSlotHostOperation.Remove);
    }
}
