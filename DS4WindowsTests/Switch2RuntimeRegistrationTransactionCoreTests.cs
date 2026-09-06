using System.Reflection;
using System.Threading;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public sealed class Switch2RuntimeRegistrationTransactionCoreTests
{
    [TestMethod]
    public void RemovalNotificationIsExactTerminalOutsideLocksAndObserverIsolated()
    {
        RegistrationFixture fixture = CreateRegistration(791, 891);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        var participant = new FakeParticipant(fixture.Registration);
        Assert.IsTrue(core.TryOpen(91, out _));
        Assert.IsTrue(core.TryAttach(fixture.Registration, () => participant,
            static (_, _, _) => { }, 2_000, out var token, out _));
        int calls = 0;
        bool underGate = false;
        InputControllerSlotState observedState = default;
        InputControllerSlotToken observedToken = default;
        core.RuntimeRemoved += _ => throw new InvalidOperationException("broken observer");
        core.RuntimeRemoved += removed =>
        {
            calls++;
            underGate = Monitor.IsEntered(core.LifecycleGate) ||
                Monitor.IsEntered(GetPrivateGate(table));
            observedState = table.GetSnapshot()[removed.Slot].State;
            observedToken = removed;
        };
        Assert.IsTrue(core.TryRemove(token, 2_000, out var failure), failure.Kind.ToString());
        Assert.AreEqual(1, calls);
        Assert.IsFalse(underGate);
        Assert.AreEqual(token, observedToken);
        Assert.AreEqual(InputControllerSlotState.Removed, observedState);
        core.TryRemove(token, 2_000, out _);
        Assert.AreEqual(1, calls, "A duplicate request must not repeat retirement notification.");
    }

    [TestMethod]
    public void FailedRemovalDoesNotPublishTerminalNotification()
    {
        RegistrationFixture fixture = CreateRegistration(792, 892);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        var participant = new FakeParticipant(fixture.Registration);
        Assert.IsTrue(core.TryOpen(92, out _));
        Assert.IsTrue(core.TryAttach(fixture.Registration, () => participant,
            static (_, _, _) => { }, 2_000, out var token, out _));
        participant.OverrideUnsubscribe(Switch2RuntimeRegistrationParticipantResult.Reject(
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe,
            Switch2RuntimeRegistrationParticipantFailureKind.SubscriptionRejected));
        int calls = 0;
        core.RuntimeRemoved += _ => calls++;
        Assert.IsFalse(core.TryRemove(token, 2_000, out _));
        Assert.AreEqual(0, calls);
        Assert.AreEqual(InputControllerSlotState.Quarantined, table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void AdoptedGenerationNeverDoubleOpensAndConsumesOneExternalClose()
    {
        var table = new InputControllerRegistrationTable(2);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        Assert.IsTrue(table.TryOpen(40, out var tableOpenFailure),
            tableOpenFailure.ToString());
        Assert.IsTrue(core.TryAdoptOpen(40, out var adoptFailure),
            adoptFailure.Kind.ToString());
        Assert.IsFalse(core.TryAdoptOpen(40, out var duplicateFailure));
        Assert.AreEqual(InputControllerSlotTableFailure.AlreadyOpen,
            duplicateFailure.TableFailure);

        Assert.IsTrue(table.TryClose(40,
            out InputControllerSlotSnapshot[] snapshots,
            out var tableCloseFailure), tableCloseFailure.ToString());
        Assert.IsTrue(core.TryObserveExternalTableClose(40, snapshots,
            out var observeFailure), observeFailure.Kind.ToString());
        Assert.IsFalse(table.IsOpen);
        Assert.IsTrue(core.TryClose(40, 1_000, out var closeFailure),
            closeFailure.Kind.ToString());
        Assert.IsTrue(core.TryClose(40, 1_000,
            out var duplicateCloseFailure),
            duplicateCloseFailure.Kind.ToString());
    }

    [TestMethod]
    public void SuccessUsesOneParticipantAndNoExternalCallRunsUnderCoreLocks()
    {
        RegistrationFixture fixture = CreateRegistration(701, 801);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        object tableGate = GetPrivateGate(table);
        int factoryCount = 0;
        int regularCount = 0;
        int terminalCount = 0;
        void AssertOutsideCoreLocks()
        {
            Assert.IsFalse(Monitor.IsEntered(core.LifecycleGate),
                "An external participant call ran under the core gate.");
            Assert.IsFalse(Monitor.IsEntered(tableGate),
                "An external participant call ran under the table gate.");
        }

        var participant = new FakeParticipant(fixture.Registration)
        {
            ExternalCallProbe = AssertOutsideCoreLocks,
            PublishRegularOnCommit = true,
        };
        Assert.IsTrue(core.TryOpen(41, out var opened),
            opened.Kind.ToString());
        Assert.IsTrue(core.TryAttach(fixture.Registration, () =>
        {
            AssertOutsideCoreLocks();
            factoryCount++;
            return participant;
        }, (_, _, report) =>
        {
            AssertOutsideCoreLocks();
            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                regularCount++;
            }
            else
            {
                terminalCount++;
            }
        }, 2_000, out InputControllerSlotToken token,
            out var attached), attached.Kind.ToString());
        Assert.IsTrue(core.TryRemove(token, 2_000, out var removed),
            removed.Kind.ToString());

        Assert.AreEqual(1, factoryCount);
        Assert.AreEqual(1, regularCount);
        Assert.AreEqual(1, terminalCount);
        CollectionAssert.AreEqual(ExpectedSuccessfulLifecycle,
            participant.OperationSnapshot);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void ExactSlotAttachUsesOnlyTheExternallySelectedSlot()
    {
        RegistrationFixture occupiedFixture = CreateRegistration(702, 802);
        RegistrationFixture exactFixture = CreateRegistration(703, 803);
        var table = new InputControllerRegistrationTable(3);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        Assert.IsTrue(core.TryOpen(42, out var opened),
            opened.Kind.ToString());
        var occupied = new FakeParticipant(occupiedFixture.Registration);
        Assert.IsTrue(core.TryAttachExactSlot(0,
            occupiedFixture.Registration, () => occupied,
            static (_, _, _) => { }, 2_000,
            out InputControllerSlotToken occupiedToken,
            out var firstFailure), firstFailure.Kind.ToString());

        var exact = new FakeParticipant(exactFixture.Registration);
        Assert.IsTrue(core.TryAttachExactSlot(2, exactFixture.Registration,
            () => exact, static (_, _, _) => { }, 2_000,
            out InputControllerSlotToken exactToken,
            out var exactFailure), exactFailure.Kind.ToString());
        Assert.AreEqual(0, occupiedToken.Slot);
        Assert.AreEqual(2, exactToken.Slot);
        Assert.AreEqual(InputControllerSlotState.Empty,
            table.GetSnapshot()[1].State);

        Assert.IsTrue(core.TryRemove(exactToken, 2_000,
            out var exactRemoved), exactRemoved.Kind.ToString());
        Assert.IsTrue(core.TryRemove(occupiedToken, 2_000,
            out var occupiedRemoved), occupiedRemoved.Kind.ToString());
    }

    [TestMethod]
    public void ExactSlotAttachRejectsOccupiedSlotWithoutFactoryOrFallback()
    {
        RegistrationFixture first = CreateRegistration(704, 804);
        RegistrationFixture rejected = CreateRegistration(705, 805);
        var table = new InputControllerRegistrationTable(3);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        Assert.IsTrue(core.TryOpen(43, out _));
        var firstParticipant = new FakeParticipant(first.Registration);
        Assert.IsTrue(core.TryAttachExactSlot(1, first.Registration,
            () => firstParticipant, static (_, _, _) => { }, 2_000,
            out InputControllerSlotToken firstToken, out _));
        int factoryCalls = 0;

        Assert.IsFalse(core.TryAttachExactSlot(1,
            rejected.Registration, () =>
            {
                factoryCalls++;
                return new FakeParticipant(rejected.Registration);
            }, static (_, _, _) => { }, 2_000,
            out InputControllerSlotToken rejectedToken,
            out var failure));
        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.TableRejected,
            failure.Kind);
        Assert.AreEqual(InputControllerSlotTableFailure.Busy,
            failure.TableFailure);
        Assert.AreEqual(0, factoryCalls);
        Assert.IsFalse(rejectedToken.IsValid);
        InputControllerSlotSnapshot[] snapshot = table.GetSnapshot();
        Assert.AreEqual(InputControllerSlotState.Empty, snapshot[0].State);
        Assert.AreEqual(InputControllerSlotState.Attached,
            snapshot[1].State);
        Assert.AreEqual(InputControllerSlotState.Empty, snapshot[2].State);
        Assert.IsTrue(core.TryRemove(firstToken, 2_000, out _));
    }

    [TestMethod]
    public void InvalidExactSlotAttachHasNoRegistrationSideEffects()
    {
        RegistrationFixture fixture = CreateRegistration(706, 806);
        var table = new InputControllerRegistrationTable(2);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        Assert.IsTrue(core.TryOpen(44, out _));
        int factoryCalls = 0;

        Assert.IsFalse(core.TryAttachExactSlot(2, fixture.Registration,
            () =>
            {
                factoryCalls++;
                return new FakeParticipant(fixture.Registration);
            }, static (_, _, _) => { }, 2_000, out _, out var failure));
        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.InvalidArgument,
            failure.Kind);
        Assert.AreEqual(InputControllerSlotTableFailure.InvalidArgument,
            failure.TableFailure);
        Assert.AreEqual(0, factoryCalls);
        Assert.IsTrue(table.GetSnapshot().All(slot =>
            slot.State == InputControllerSlotState.Empty));
    }

    [TestMethod]
    public void DefaultAndWrongOperationTagsNeverBecomeCoreSuccessEvidence()
    {
        Switch2RuntimeRegistrationParticipantResult[] forgedAdoption =
        {
            default,
            Switch2RuntimeRegistrationParticipantResult.Success(
                Switch2RuntimeRegistrationParticipantOperation.Subscribe),
        };
        foreach (Switch2RuntimeRegistrationParticipantResult forged in
            forgedAdoption)
        {
            RegistrationFixture fixture = CreateRegistration(711, 811);
            var table = new InputControllerRegistrationTable(1);
            var core = new Switch2RuntimeRegistrationTransactionCore(table);
            var participant = new FakeParticipant(fixture.Registration);
            participant.OverrideAdoption(forged);
            Assert.IsTrue(core.TryOpen(51, out _));

            Assert.IsFalse(core.TryAttach(fixture.Registration,
                () => participant, static (_, _, _) => { }, 2_000,
                out InputControllerSlotToken token, out var failure));
            Assert.AreEqual(
                Switch2RuntimeRegistrationTransactionFailureKind.
                    QuarantineRequired,
                failure.Kind);
            Assert.IsTrue(failure.RequiresQuarantine);
            Assert.AreEqual(InputControllerSlotState.Quarantined,
                table.GetSnapshot()[token.Slot].State);
        }

        Switch2RuntimeRegistrationParticipantResult[] forgedPrepare =
        {
            default,
            Switch2RuntimeRegistrationParticipantResult.Success(
                Switch2RuntimeRegistrationParticipantOperation.
                    CommitPrepared),
        };
        foreach (Switch2RuntimeRegistrationParticipantResult forged in
            forgedPrepare)
        {
            RegistrationFixture fixture = CreateRegistration(712, 812);
            var table = new InputControllerRegistrationTable(1);
            var core = new Switch2RuntimeRegistrationTransactionCore(table);
            var participant = new FakeParticipant(fixture.Registration);
            participant.OverridePrepare(forged);
            Assert.IsTrue(core.TryOpen(52, out _));

            Assert.IsFalse(core.TryAttach(fixture.Registration,
                () => participant, static (_, _, _) => { }, 2_000,
                out InputControllerSlotToken token, out var failure));
            Assert.AreEqual(
                Switch2RuntimeRegistrationTransactionFailureKind.
                    DependencyThrew,
                failure.Kind);
            Assert.AreEqual(InputControllerSlotState.Removed,
                table.GetSnapshot()[token.Slot].State,
                "Exact abort and unsubscribe proof should permit rollback.");
        }
    }

    [TestMethod]
    public void UncertainSubscriptionWithoutUnsubscribeProofQuarantines()
    {
        RegistrationFixture fixture = CreateRegistration(721, 821);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        var participant = new FakeParticipant(fixture.Registration)
        {
            RetainCallbacksOnSubscribeOverride = true,
        };
        participant.OverrideSubscribe(
            Switch2RuntimeRegistrationParticipantResult.Uncertain(
                Switch2RuntimeRegistrationParticipantOperation.Subscribe,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    SubscriptionRejected,
                quarantineReason: InputControllerSlotQuarantineReason.None));
        participant.OverrideUnsubscribe(
            Switch2RuntimeRegistrationParticipantResult.Uncertain(
                Switch2RuntimeRegistrationParticipantOperation.Unsubscribe,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    SubscriptionRejected,
                quarantineReason: InputControllerSlotQuarantineReason.None));
        Assert.IsTrue(core.TryOpen(61, out _));

        Assert.IsFalse(core.TryAttach(fixture.Registration,
            () => participant, static (_, _, _) => { }, 2_000,
            out InputControllerSlotToken token, out var failure));
        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.
                QuarantineRequired,
            failure.Kind);
        Assert.IsTrue(failure.RequiresQuarantine);
        InputControllerSlotSnapshot snapshot =
            table.GetSnapshot()[token.Slot];
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            snapshot.State);
        Assert.AreEqual(InputControllerSlotQuarantineReason.StopRejected,
            snapshot.QuarantineReason);
        Assert.AreEqual(1, participant.Count(
            Switch2RuntimeRegistrationParticipantOperation.
                AbortUnpublished));
        Assert.AreEqual(1, participant.Count(
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe));
    }

    [TestMethod]
    public void AttachAndCloseShareTheExactSetupEpoch()
    {
        RegistrationFixture fixture = CreateRegistration(731, 831);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        var participant = new FakeParticipant(fixture.Registration)
        {
            BlockPrepare = true,
        };
        Assert.IsTrue(core.TryOpen(71, out _));

        InputControllerSlotToken token = default;
        Switch2RuntimeRegistrationTransactionFailure attachFailure = default;
        Task<bool> attach = Task.Run(() => core.TryAttach(
            fixture.Registration, () => participant,
            static (_, _, _) => { }, 5_000, out token,
            out attachFailure));
        Assert.IsTrue(participant.PrepareEntered.Wait(1_000));
        Switch2RuntimeRegistrationTransactionFailure closeFailure = default;
        Task<bool> close = Task.Run(() => core.TryClose(71, 5_000,
            out closeFailure));
        Assert.IsTrue(SpinWait.SpinUntil(() => !table.IsOpen, 1_000));

        participant.ReleasePrepare.Set();
        Assert.IsTrue(attach.Wait(2_000));
        Assert.IsFalse(attach.Result);
        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.TableRejected,
            attachFailure.Kind);
        Assert.IsTrue(close.Wait(2_000));
        Assert.IsTrue(close.Result, closeFailure.Kind.ToString());
        Assert.AreEqual(1, participant.Count(
            Switch2RuntimeRegistrationParticipantOperation.AbortPrepared));
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void AttentionAndExplicitRetireShareOneRemovalOwner()
    {
        RegistrationFixture fixture = CreateRegistration(741, 841);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        var participant = new FakeParticipant(fixture.Registration)
        {
            BlockRetirementArm = true,
        };
        Assert.IsTrue(core.TryOpen(81, out _));
        Assert.IsTrue(core.TryAttach(fixture.Registration,
            () => participant, static (_, _, _) => { }, 2_000,
            out InputControllerSlotToken token, out var attached),
            attached.Kind.ToString());

        participant.RaiseAttention(
            Switch2RuntimeRegistrationLifecycleAttentionKind.ProducerFailed);
        Assert.IsTrue(participant.RetirementArmEntered.Wait(1_000));
        Switch2RuntimeRegistrationTransactionFailure explicitFailure =
            default;
        Task<bool> explicitRemoval = Task.Run(() => core.TryRemove(token,
            2_000, out explicitFailure));
        Assert.IsFalse(explicitRemoval.Wait(40),
            "The explicit observer should join the retained removal owner.");
        participant.ReleaseRetirementArm.Set();

        Assert.IsTrue(explicitRemoval.Wait(2_000));
        Assert.IsTrue(explicitRemoval.Result,
            explicitFailure.Kind.ToString());
        Assert.AreEqual(1, participant.Count(
            Switch2RuntimeRegistrationParticipantOperation.ArmRetirement));
        Assert.AreEqual(1, participant.Count(
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce));
        Assert.AreEqual(1, participant.Count(
            Switch2RuntimeRegistrationParticipantOperation.Remove));
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
    }

    [TestMethod]
    public void AttentionRaisedInsideMappingCallbackRetiresAfterCallbackExit()
    {
        RegistrationFixture fixture = CreateRegistration(742, 842);
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        var participant = new FakeParticipant(fixture.Registration)
        {
            PublishRegularOnCommit = true,
        };
        Assert.IsTrue(core.TryOpen(82, out _));
        int regular = 0;
        int terminal = 0;

        Assert.IsTrue(core.TryAttach(fixture.Registration,
            () => participant, (_, _, report) =>
            {
                if (report.Kind == Switch2RuntimeReportKind.Regular)
                {
                    Interlocked.Increment(ref regular);
                    participant.RaiseAttention(
                        Switch2RuntimeRegistrationLifecycleAttentionKind.
                            UserDisconnectRequested);
                }
                else
                {
                    Interlocked.Increment(ref terminal);
                }
            }, 2_000, out InputControllerSlotToken token,
            out var attachFailure), attachFailure.Kind.ToString());

        Assert.AreEqual(1, Volatile.Read(ref regular));
        Assert.IsTrue(SpinWait.SpinUntil(() => table.GetSnapshot()[token.Slot].
                State == InputControllerSlotState.Removed,
            TimeSpan.FromSeconds(2)),
            "The retained lifecycle worker did not resume after callback exit.");
        Assert.AreEqual(1, Volatile.Read(ref terminal));
        Assert.AreEqual(1, participant.Count(
            Switch2RuntimeRegistrationParticipantOperation.ArmRetirement));
        Assert.AreEqual(1, participant.Count(
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce));
        Assert.AreEqual(1, participant.Count(
            Switch2RuntimeRegistrationParticipantOperation.Remove));
    }

    [TestMethod]
    public void CrossTableAdoptionHasExactlyOnePhysicalLifetimeWinner()
    {
        RegistrationFixture fixture = CreateRegistration(751, 851);
        var firstTable = new InputControllerRegistrationTable(1);
        var secondTable = new InputControllerRegistrationTable(1);
        var first = new Switch2RuntimeRegistrationTransactionCore(firstTable);
        var second = new Switch2RuntimeRegistrationTransactionCore(
            secondTable);
        var fence = new AdoptionFence();
        var firstParticipant = new FakeParticipant(fixture.Registration,
            fence);
        var secondParticipant = new FakeParticipant(fixture.Registration,
            fence);
        Assert.IsTrue(first.TryOpen(91, out _));
        Assert.IsTrue(second.TryOpen(92, out _));
        using var start = new ManualResetEventSlim(false);
        InputControllerSlotToken firstToken = default;
        InputControllerSlotToken secondToken = default;
        Switch2RuntimeRegistrationTransactionFailure firstFailure = default;
        Switch2RuntimeRegistrationTransactionFailure secondFailure = default;
        Task<bool> firstAttach = Task.Run(() =>
        {
            start.Wait();
            return first.TryAttach(fixture.Registration,
                () => firstParticipant, static (_, _, _) => { }, 2_000,
                out firstToken, out firstFailure);
        });
        Task<bool> secondAttach = Task.Run(() =>
        {
            start.Wait();
            return second.TryAttach(fixture.Registration,
                () => secondParticipant, static (_, _, _) => { }, 2_000,
                out secondToken, out secondFailure);
        });
        start.Set();
        Assert.IsTrue(Task.WaitAll(new Task[] { firstAttach, secondAttach },
            4_000));

        Assert.AreNotEqual(firstAttach.Result, secondAttach.Result);
        Switch2RuntimeRegistrationTransactionCore winner =
            firstAttach.Result ? first : second;
        InputControllerRegistrationTable winnerTable = firstAttach.Result ?
            firstTable : secondTable;
        InputControllerRegistrationTable loserTable = firstAttach.Result ?
            secondTable : firstTable;
        InputControllerSlotToken winnerToken = firstAttach.Result ?
            firstToken : secondToken;
        Switch2RuntimeRegistrationTransactionFailure loserFailure =
            firstAttach.Result ? secondFailure : firstFailure;
        Assert.AreEqual(
            Switch2RuntimeRegistrationTransactionFailureKind.
                SlotAdoptionRejected,
            loserFailure.Kind);
        Assert.AreEqual(InputControllerSlotState.Attached,
            winnerTable.GetSnapshot().Single().State);
        Assert.AreEqual(InputControllerSlotState.Removed,
            loserTable.GetSnapshot().Single().State);
        Assert.IsTrue(winner.TryRemove(winnerToken, 2_000,
            out var removed), removed.Kind.ToString());
    }

    private static readonly Switch2RuntimeRegistrationParticipantOperation[]
        ExpectedSuccessfulLifecycle =
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
        };

    private static RegistrationFixture CreateRegistration(
        ulong deviceGeneration, ulong transportGeneration)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(deviceGeneration,
            transportGeneration, Switch2Transport.BluetoothLe,
            out Switch2RuntimeInputDevice device, out var deviceFailure),
            deviceFailure.ToString());
        var owner = new FakeRegistrationOwner(device, deviceGeneration);
        Assert.IsTrue(InputControllerRegistration.TryCreate(device,
            deviceGeneration, InputControllerOwnershipKind.Switch2Runtime,
            hasHidInterface: false, hasPersistentIdentity: false, owner,
            out InputControllerRegistration registration,
            out var registrationFailure), registrationFailure.ToString());
        return new RegistrationFixture(device, owner, registration);
    }

    private static object GetPrivateGate(
        InputControllerRegistrationTable table)
    {
        FieldInfo field = typeof(InputControllerRegistrationTable).GetField(
            "gate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        object gate = field.GetValue(table);
        Assert.IsNotNull(gate);
        return gate;
    }

    private sealed record RegistrationFixture(
        Switch2RuntimeInputDevice Device,
        FakeRegistrationOwner Owner,
        InputControllerRegistration Registration);

    private sealed class FakeRegistrationOwner :
        IInputControllerRegistrationOwner
    {
        private readonly DS4Device device;
        private readonly ulong generation;

        internal FakeRegistrationOwner(DS4Device device, ulong generation)
        {
            this.device = device;
            this.generation = generation;
        }

        public InputControllerOwnershipKind Kind =>
            InputControllerOwnershipKind.Switch2Runtime;

        public bool Authenticates(DS4Device candidate,
            ulong candidateGeneration) =>
            ReferenceEquals(candidate, device) &&
            candidateGeneration == generation;

        public bool TryStopAndQuiesce(DS4Device candidate,
            ulong candidateGeneration, int timeoutMilliseconds,
            out InputControllerOwnerOperationFailure failure)
        {
            bool authenticated = Authenticates(candidate,
                candidateGeneration);
            failure = authenticated ?
                InputControllerOwnerOperationFailure.None :
                InputControllerOwnerOperationFailure.
                    OwnerAuthenticationFailed;
            return authenticated;
        }

        public bool TryRemove(DS4Device candidate,
            ulong candidateGeneration,
            out InputControllerOwnerOperationFailure failure) =>
            TryStopAndQuiesce(candidate, candidateGeneration, 0,
                out failure);
    }

    private sealed class AdoptionFence
    {
        private readonly object gate = new();
        private FakeParticipant owner;

        internal bool TryAdopt(FakeParticipant candidate)
        {
            lock (gate)
            {
                if (owner != null && !ReferenceEquals(owner, candidate))
                {
                    return false;
                }
                owner = candidate;
                return true;
            }
        }

        internal void Release(FakeParticipant candidate)
        {
            lock (gate)
            {
                if (ReferenceEquals(owner, candidate))
                {
                    owner = null;
                }
            }
        }
    }

    private sealed class FakeParticipant :
        ISwitch2RuntimeRegistrationParticipant
    {
        private readonly object gate = new();
        private readonly InputControllerRegistration registration;
        private readonly AdoptionFence adoptionFence;
        private readonly List<
            Switch2RuntimeRegistrationParticipantOperation> operations =
            new();
        private Switch2RuntimeRegistrationCallbacks callbacks;
        private bool hasAdoptionOverride;
        private Switch2RuntimeRegistrationParticipantResult adoptionOverride;
        private bool hasSubscribeOverride;
        private Switch2RuntimeRegistrationParticipantResult subscribeOverride;
        private bool hasPrepareOverride;
        private Switch2RuntimeRegistrationParticipantResult prepareOverride;
        private bool hasUnsubscribeOverride;
        private Switch2RuntimeRegistrationParticipantResult
            unsubscribeOverride;
        private bool adoptionOwned;

        internal FakeParticipant(InputControllerRegistration registration,
            AdoptionFence adoptionFence = null)
        {
            this.registration = registration;
            this.adoptionFence = adoptionFence;
        }

        internal Action ExternalCallProbe { get; init; }

        internal bool PublishRegularOnCommit { get; init; }

        internal bool RetainCallbacksOnSubscribeOverride { get; init; }

        internal bool BlockPrepare { get; init; }

        internal bool BlockRetirementArm { get; init; }

        internal ManualResetEventSlim PrepareEntered { get; } = new(false);

        internal ManualResetEventSlim ReleasePrepare { get; } = new(false);

        internal ManualResetEventSlim RetirementArmEntered { get; } =
            new(false);

        internal ManualResetEventSlim ReleaseRetirementArm { get; } =
            new(false);

        internal Switch2RuntimeRegistrationParticipantOperation[]
            OperationSnapshot
        {
            get { lock (gate) { return operations.ToArray(); } }
        }

        public InputControllerRegistration Registration
        {
            get
            {
                Probe();
                return registration;
            }
        }

        internal void OverrideAdoption(
            Switch2RuntimeRegistrationParticipantResult result)
        {
            hasAdoptionOverride = true;
            adoptionOverride = result;
        }

        internal void OverrideSubscribe(
            Switch2RuntimeRegistrationParticipantResult result)
        {
            hasSubscribeOverride = true;
            subscribeOverride = result;
        }

        internal void OverridePrepare(
            Switch2RuntimeRegistrationParticipantResult result)
        {
            hasPrepareOverride = true;
            prepareOverride = result;
        }

        internal void OverrideUnsubscribe(
            Switch2RuntimeRegistrationParticipantResult result)
        {
            hasUnsubscribeOverride = true;
            unsubscribeOverride = result;
        }

        internal int Count(
            Switch2RuntimeRegistrationParticipantOperation operation)
        {
            lock (gate)
            {
                return operations.Count(candidate => candidate == operation);
            }
        }

        internal void RaiseAttention(
            Switch2RuntimeRegistrationLifecycleAttentionKind kind)
        {
            Switch2RuntimeRegistrationLifecycleAttentionCallback callback =
                callbacks.AttentionHandler;
            Assert.IsNotNull(callback);
            var attention = new Switch2RuntimeRegistrationLifecycleAttention(
                registration, kind);
            callback(attention);
        }

        public Switch2RuntimeRegistrationParticipantResult TryAdoptBoundSlot(
            in InputControllerSlotToken token)
        {
            Record(Switch2RuntimeRegistrationParticipantOperation.
                AdoptBoundSlot);
            if (hasAdoptionOverride)
            {
                return adoptionOverride;
            }
            if (adoptionFence != null && !adoptionFence.TryAdopt(this))
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    Switch2RuntimeRegistrationParticipantOperation.
                        AdoptBoundSlot,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        StaleCredential);
            }
            adoptionOwned = true;
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        }

        public Switch2RuntimeRegistrationParticipantResult TrySubscribe(
            in Switch2RuntimeRegistrationCallbacks exactCallbacks)
        {
            Record(Switch2RuntimeRegistrationParticipantOperation.Subscribe);
            if (!hasSubscribeOverride ||
                RetainCallbacksOnSubscribeOverride)
            {
                callbacks = exactCallbacks;
            }
            return hasSubscribeOverride ? subscribeOverride :
                Success(Switch2RuntimeRegistrationParticipantOperation.
                    Subscribe);
        }

        public Switch2RuntimeRegistrationParticipantResult
            TryPrepareActivation(int timeoutMilliseconds)
        {
            Record(Switch2RuntimeRegistrationParticipantOperation.
                PrepareActivation);
            if (BlockPrepare)
            {
                PrepareEntered.Set();
                Assert.IsTrue(ReleasePrepare.Wait(5_000));
            }
            return hasPrepareOverride ? prepareOverride :
                Success(Switch2RuntimeRegistrationParticipantOperation.
                    PrepareActivation);
        }

        public Switch2RuntimeRegistrationParticipantResult TryCommitPrepared(
            in InputControllerActivationCommitCredential activationCommit)
        {
            Record(Switch2RuntimeRegistrationParticipantOperation.
                CommitPrepared);
            if (PublishRegularOnCommit)
            {
                callbacks.ReportHandler(registration.Device,
                    new Switch2RuntimeReportEventArgs(
                        Switch2RuntimeReportKind.Regular,
                        registration.Generation));
            }
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.CommitPrepared);
        }

        public Switch2RuntimeRegistrationParticipantResult TryAbortPrepared(
            int timeoutMilliseconds)
        {
            Record(Switch2RuntimeRegistrationParticipantOperation.
                AbortPrepared);
            ReleaseAdoption();
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.AbortPrepared);
        }

        public Switch2RuntimeRegistrationParticipantResult
            TryAbortUnpublished(int timeoutMilliseconds)
        {
            Record(Switch2RuntimeRegistrationParticipantOperation.
                AbortUnpublished);
            ReleaseAdoption();
            return Success(Switch2RuntimeRegistrationParticipantOperation.
                AbortUnpublished);
        }

        public Switch2RuntimeRegistrationParticipantResult TryArmRetirement(
            in InputControllerRetirementClaim claim)
        {
            Record(Switch2RuntimeRegistrationParticipantOperation.
                ArmRetirement);
            if (BlockRetirementArm)
            {
                RetirementArmEntered.Set();
                Assert.IsTrue(ReleaseRetirementArm.Wait(5_000));
            }
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.ArmRetirement);
        }

        public Switch2RuntimeRegistrationParticipantResult
            TryWaitForPublicationAvailability(int timeoutMilliseconds)
        {
            Record(Switch2RuntimeRegistrationParticipantOperation.
                WaitForPublicationAvailability);
            return Success(Switch2RuntimeRegistrationParticipantOperation.
                WaitForPublicationAvailability);
        }

        public Switch2RuntimeRegistrationParticipantResult TryStopAndQuiesce(
            int timeoutMilliseconds)
        {
            Record(Switch2RuntimeRegistrationParticipantOperation.
                StopAndQuiesce);
            callbacks.ReportHandler(registration.Device,
                new Switch2RuntimeReportEventArgs(
                    Switch2RuntimeReportKind.TerminalNeutral,
                    registration.Generation));
            return Success(Switch2RuntimeRegistrationParticipantOperation.
                StopAndQuiesce);
        }

        public Switch2RuntimeRegistrationParticipantResult TryUnsubscribe()
        {
            Record(Switch2RuntimeRegistrationParticipantOperation.Unsubscribe);
            if (hasUnsubscribeOverride)
            {
                return unsubscribeOverride;
            }
            callbacks = default;
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.Unsubscribe);
        }

        public Switch2RuntimeRegistrationParticipantResult TryRemove()
        {
            Record(Switch2RuntimeRegistrationParticipantOperation.Remove);
            ReleaseAdoption();
            return Success(
                Switch2RuntimeRegistrationParticipantOperation.Remove);
        }

        private void Record(
            Switch2RuntimeRegistrationParticipantOperation operation)
        {
            Probe();
            lock (gate)
            {
                operations.Add(operation);
            }
        }

        private void Probe() => ExternalCallProbe?.Invoke();

        private void ReleaseAdoption()
        {
            if (!adoptionOwned)
            {
                return;
            }
            adoptionOwned = false;
            adoptionFence?.Release(this);
        }

        private static Switch2RuntimeRegistrationParticipantResult Success(
            Switch2RuntimeRegistrationParticipantOperation operation) =>
            Switch2RuntimeRegistrationParticipantResult.Success(operation);
    }
}
