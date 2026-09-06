using System;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2ProUsbOwnedHdRumbleTransportBridgeTests
{
    private const ulong DeviceGeneration = 7;
    private const ulong TransportGeneration = 11;
    private const int WaitMilliseconds = 25;

    [TestMethod]
    public void ConstructorRequiresExactImmutableLeaseAndFixedBound()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new Switch2ProUsbOwnedHdRumbleTransportBridge(null,
                DeviceGeneration, TransportGeneration, WaitMilliseconds));

        ScriptedLease lease = new();
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2ProUsbOwnedHdRumbleTransportBridge(lease, 0,
                TransportGeneration, WaitMilliseconds));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2ProUsbOwnedHdRumbleTransportBridge(lease,
                DeviceGeneration, 0, WaitMilliseconds));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2ProUsbOwnedHdRumbleTransportBridge(lease,
                DeviceGeneration, TransportGeneration, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new Switch2ProUsbOwnedHdRumbleTransportBridge(lease,
                DeviceGeneration, TransportGeneration, 101));

        lease.Authenticated = false;
        Assert.ThrowsException<ArgumentException>(() =>
            new Switch2ProUsbOwnedHdRumbleTransportBridge(lease,
                DeviceGeneration, TransportGeneration, WaitMilliseconds));
        lease.Authenticated = true;
        lease.ThrowAuthentication = true;
        Assert.ThrowsException<ArgumentException>(() =>
            new Switch2ProUsbOwnedHdRumbleTransportBridge(lease,
                DeviceGeneration, TransportGeneration, WaitMilliseconds));
    }

    [TestMethod]
    public void CanonicalSinkRetryDrainsExactClaimAndReusesBytesAndCounter()
    {
        ScriptedLease lease = new()
        {
            WriteMode = WriteMode.Retained,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge = NewBridge(lease);
        Switch2ProUsbHdRumblePhysicalWriter writer = new(bridge,
            DeviceGeneration, TransportGeneration, initialCounter: 9);
        Switch2HdRumbleDeliverySink sink = new(writer, DeviceGeneration,
            TransportGeneration);
        ControllerFeedbackDelivery stop = StopDelivery(31);

        Assert.IsFalse(sink.TryDeliver(stop));
        Assert.IsTrue(sink.HasUncertainWrite);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.
            RetainedOperation, bridge.State);
        Assert.AreEqual(1, lease.WriteCount);
        Assert.AreEqual((byte)9, (byte)(lease.FirstReport[1] & 0x0F));

        lease.RetirementMode = RetirementMode.Quiescent;
        lease.WriteMode = WriteMode.Complete;
        Assert.IsTrue(sink.TryDeliver(stop));

        Assert.AreEqual(1, lease.RetirementCount);
        Assert.AreEqual(2, lease.WriteCount);
        CollectionAssert.AreEqual(lease.FirstReport, lease.LastReport);
        Assert.AreEqual((byte)9, (byte)(lease.LastReport[1] & 0x0F));
        Assert.AreEqual(WaitMilliseconds, lease.LastWriteTimeout);
        Assert.AreEqual(WaitMilliseconds, lease.LastRetirementTimeout);
        Assert.IsFalse(sink.HasUncertainWrite);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.
            NoRetainedOperation, bridge.State);
        Assert.IsTrue(sink.TryRetire());
    }

    [TestMethod]
    public void RetainedDrainRetriesOnlyTheExactOpaqueClaim()
    {
        ScriptedLease lease = new()
        {
            WriteMode = WriteMode.Retained,
            RetirementMode = RetirementMode.Retained,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge = NewBridge(lease);
        byte[] report = Report(counter: 3, seed: 1);

        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, Write(bridge, report).Outcome);
        Switch2ProUsbOwnedOutputOperationClaim expected = lease.FirstClaim;

        Switch2ProUsbOwnedHdRumbleDrainResult first =
            bridge.TryRetireRetainedOperation();
        Switch2ProUsbOwnedHdRumbleDrainResult second =
            bridge.TryRetireRetainedOperation();

        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.
            RetainedForRetry, first.Outcome);
        Assert.AreEqual(first.Outcome, second.Outcome);
        Assert.IsTrue(bridge.Authenticates(first));
        Assert.IsTrue(lease.LastRetirementClaim.Equals(expected));
        Assert.AreEqual(2, lease.RetirementCount);
        Assert.AreEqual(1, lease.WriteCount);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.
            RetainedOperation, bridge.State);

        lease.RetirementMode = RetirementMode.Quiescent;
        Switch2ProUsbOwnedHdRumbleDrainResult quiescent =
            bridge.TryRetireRetainedOperation();
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.
            ExactOperationQuiescent, quiescent.Outcome);
        Assert.IsTrue(bridge.Authenticates(quiescent));
        Assert.IsFalse(NewBridge(new ScriptedLease()).Authenticates(
            quiescent),
            "Matching generations from another bridge must not authenticate.");
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.
            NoRetainedOperation, bridge.State);

        Switch2ProUsbOwnedHdRumbleDrainResult empty =
            bridge.TryRetireRetainedOperation();
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.
            NoRetainedOperation, empty.Outcome);
    }

    [TestMethod]
    public void NewerReportCannotStartUntilPriorExactOperationQuiesces()
    {
        ScriptedLease lease = new()
        {
            WriteMode = WriteMode.Retained,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge = NewBridge(lease);
        Switch2ProUsbHdRumblePhysicalWriter writer = new(bridge,
            DeviceGeneration, TransportGeneration, initialCounter: 3);

        Assert.IsTrue(writer.TryWrite(StopSubmission(31)).IsUncertain);
        lease.RetirementMode = RetirementMode.Retained;
        Switch2HdRumblePhysicalWriteResult blocked = writer.TryWrite(
            StopSubmission(32));
        Assert.AreEqual(Switch2HdRumblePhysicalWriteOutcome.ProvenRejected,
            blocked.Outcome);
        Assert.AreEqual(Switch2HdRumblePhysicalWriteFailure.Busy,
            blocked.Failure);
        Assert.AreEqual(1, lease.WriteCount,
            "A replacement must not start beside the retained operation.");
        Assert.AreEqual(0, lease.RetirementCount,
            "A different report cannot authorize retirement of the old " +
            "writer cache entry.");

        lease.RetirementMode = RetirementMode.Quiescent;
        lease.WriteMode = WriteMode.Complete;
        Assert.IsTrue(writer.TryWrite(StopSubmission(31)).Succeeded);
        Assert.IsTrue(writer.TryWrite(StopSubmission(32)).Succeeded);

        Assert.AreEqual(3, lease.WriteCount);
        Assert.IsTrue(lease.LastRetirementOrder < lease.LastWriteOrder);
        Assert.AreEqual((byte)3, (byte)(lease.FirstReport[1] & 0x0F));
        Assert.AreEqual((byte)4, (byte)(lease.LastReport[1] & 0x0F));
        CollectionAssert.AreNotEqual(lease.FirstReport, lease.LastReport);
    }

    [TestMethod]
    public void ConcurrentAndReentrantWritesNeverEnterOwnedLeaseTwice()
    {
        ScriptedLease lease = new()
        {
            BlockWrite = true,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge = NewBridge(lease);
        byte[] firstReport = Report(1, 1);
        byte[] secondReport = Report(2, 2);

        Task<Switch2ProUsbHdRumbleTransportWriteResult> first = Task.Run(() =>
            Write(bridge, firstReport));
        Assert.IsTrue(lease.WriteEntered.Wait(TimeSpan.FromSeconds(5)));
        Switch2ProUsbHdRumbleTransportWriteResult busy = Write(bridge,
            secondReport);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            ProvenRejected, busy.Outcome);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteFailure.Busy,
            busy.Failure);
        lease.ReleaseWrite.Set();
        Assert.IsTrue(first.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, lease.WriteCount);
        Assert.AreEqual(1, lease.MaximumConcurrentWrites);

        lease.BlockWrite = false;
        lease.DuringWrite = () => lease.ReentrantWriteResult =
            Write(bridge, secondReport);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.Completed,
            Write(bridge, firstReport).Outcome);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteFailure.Busy,
            lease.ReentrantWriteResult.Failure);
        Assert.AreEqual(2, lease.WriteCount);
    }

    [TestMethod]
    public void ReentrantRetirementIsRejectedWhileOuterExactDrainCompletes()
    {
        ScriptedLease lease = new()
        {
            WriteMode = WriteMode.Retained,
            RetirementMode = RetirementMode.Quiescent,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge = NewBridge(lease);
        Assert.IsTrue(Write(bridge, Report(1, 1)).Outcome ==
            Switch2ProUsbHdRumbleTransportWriteOutcome.OutcomeUncertain);
        lease.DuringRetirement = () => lease.ReentrantDrainResult =
            bridge.TryRetireRetainedOperation();

        Switch2ProUsbOwnedHdRumbleDrainResult outer =
            bridge.TryRetireRetainedOperation();

        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.Busy,
            lease.ReentrantDrainResult.Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.
            ExactOperationQuiescent, outer.Outcome);
        Assert.AreEqual(1, lease.RetirementCount);
    }

    [TestMethod]
    public void RetirementContradictionPermanentlyQuarantinesWithoutReplay()
    {
        ScriptedLease lease = new()
        {
            WriteMode = WriteMode.Retained,
            RetirementMode = RetirementMode.Rejected,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge = NewBridge(lease);
        byte[] report = Report(1, 1);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, Write(bridge, report).Outcome);

        Switch2ProUsbOwnedHdRumbleDrainResult result =
            bridge.TryRetireRetainedOperation();
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined,
            result.Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            bridge.State);

        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, Write(bridge, report).Outcome);
        Assert.AreEqual(1, lease.WriteCount);
        Assert.AreEqual(1, lease.RetirementCount);
    }

    [TestMethod]
    public void InvalidOrDifferentReportCannotOperateAnExactRetainedClaim()
    {
        ScriptedLease lease = new()
        {
            WriteMode = WriteMode.Retained,
            RetirementMode = RetirementMode.Quiescent,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge = NewBridge(lease);
        byte[] exact = Report(1, 1);
        Write(bridge, exact);

        byte[] invalid = (byte[])exact.Clone();
        invalid[^1] = 1;
        Switch2ProUsbHdRumbleTransportWriteResult invalidResult =
            Write(bridge, invalid);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            ProvenRejected, invalidResult.Outcome);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteFailure.
            InvalidReport, invalidResult.Failure);

        Switch2ProUsbHdRumbleTransportWriteResult different = Write(bridge,
            Report(2, 2));
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            ProvenRejected, different.Outcome);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteFailure.Busy,
            different.Failure);
        Assert.AreEqual(1, lease.WriteCount);
        Assert.AreEqual(0, lease.RetirementCount);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.
            RetainedOperation, bridge.State);

        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.
            ExactOperationQuiescent,
            bridge.TryRetireRetainedOperation().Outcome);
    }

    [TestMethod]
    public void ThrownOrMalformedWriteQuarantinesAndNeverGuessesAClaim()
    {
        byte[] report = Report(1, 1);
        ScriptedLease throwing = new()
        {
            WriteMode = WriteMode.Throw,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge thrownBridge =
            NewBridge(throwing);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, Write(thrownBridge, report).Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            thrownBridge.State);
        Write(thrownBridge, report);
        Assert.AreEqual(1, throwing.WriteCount);
        Assert.AreEqual(0, throwing.RetirementCount);

        ScriptedLease malformed = new()
        {
            WriteMode = WriteMode.Malformed,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge malformedBridge =
            NewBridge(malformed);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, Write(malformedBridge, report).Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            malformedBridge.State);
        Write(malformedBridge, report);
        Assert.AreEqual(1, malformed.WriteCount);
    }

    [TestMethod]
    public void LeaseQuarantineAndForeignCompletionRemainTerminalUncertainty()
    {
        byte[] report = Report(1, 1);
        ScriptedLease terminal = new()
        {
            WriteMode = WriteMode.Quarantined,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge terminalBridge =
            NewBridge(terminal);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, Write(terminalBridge, report).Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            terminalBridge.State);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined,
            terminalBridge.TryRetireRetainedOperation().Outcome);
        Assert.AreEqual(0, terminal.RetirementCount,
            "Terminal quarantine must not replay cancellation or drain.");

        ScriptedLease foreign = new()
        {
            WriteMode = WriteMode.ForeignCompletion,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge foreignBridge =
            NewBridge(foreign);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, Write(foreignBridge, report).Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            foreignBridge.State);
        Write(foreignBridge, report);
        Assert.AreEqual(1, foreign.WriteCount);

        ScriptedLease foreignClaim = new()
        {
            WriteMode = WriteMode.ForeignRetainedClaim,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge foreignClaimBridge =
            NewBridge(foreignClaim);
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, Write(foreignClaimBridge, report).Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            foreignClaimBridge.State);
        Assert.AreEqual(0, foreignClaim.RetirementCount);
    }

    [TestMethod]
    public void RetirementThrowPermanentlyRetainsTerminalAttention()
    {
        ScriptedLease lease = new()
        {
            WriteMode = WriteMode.Retained,
            RetirementMode = RetirementMode.Throw,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge = NewBridge(lease);
        byte[] report = Report(1, 1);
        Write(bridge, report);

        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined,
            bridge.TryRetireRetainedOperation().Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            bridge.State);
        bridge.TryRetireRetainedOperation();
        Write(bridge, report);
        Assert.AreEqual(1, lease.RetirementCount,
            "An ambiguous cancellation/drain must never be reissued.");
        Assert.AreEqual(1, lease.WriteCount);
    }

    [TestMethod]
    public void ForeignAndAbaClaimsCannotClearOrReplaceExactAuthority()
    {
        byte[] report = Report(1, 1);
        ScriptedLease foreign = new()
        {
            WriteMode = WriteMode.Retained,
            RetirementMode = RetirementMode.ForeignQuiescent,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge foreignBridge =
            NewBridge(foreign);
        Write(foreignBridge, report);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined,
            foreignBridge.TryRetireRetainedOperation().Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            foreignBridge.State);

        ScriptedLease aba = new()
        {
            WriteMode = WriteMode.Retained,
            RetirementMode = RetirementMode.Quiescent,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge abaBridge = NewBridge(aba);
        Write(abaBridge, report);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.
            ExactOperationQuiescent,
            abaBridge.TryRetireRetainedOperation().Outcome);
        Write(abaBridge, Report(2, 2));
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.
            ExactOperationQuiescent,
            abaBridge.TryRetireRetainedOperation().Outcome);
        Write(abaBridge, Report(3, 3));
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.
            ExactOperationQuiescent,
            abaBridge.TryRetireRetainedOperation().Outcome);
        aba.ReuseFirstClaim = true;
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, Write(abaBridge, Report(4, 4)).Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            abaBridge.State);
    }

    [TestMethod]
    public void SameGenerationForeignClaimAndEchoedRetirementFailProvenance()
    {
        byte[] report = Report(1, 1);
        ScriptedLease foreign = new()
        {
            WriteMode = WriteMode.SameGenerationForeignRetainedClaim,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge foreignBridge =
            NewBridge(foreign);

        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, Write(foreignBridge, report).Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            foreignBridge.State);
        Assert.AreEqual(0, foreign.RetirementCount);
        Write(foreignBridge, report);
        Assert.AreEqual(1, foreign.WriteCount,
            "A same-generation, high-sequence foreign fence must not escape " +
            "terminal quarantine.");

        ScriptedLease throwingAdmission = new()
        {
            WriteMode = WriteMode.Retained,
            ThrowClaimAuthenticationOnCall = 1,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge throwingAdmissionBridge =
            NewBridge(throwingAdmission);
        Write(throwingAdmissionBridge, report);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            throwingAdmissionBridge.State);
        Assert.AreEqual(0, throwingAdmission.RetirementCount);

        ScriptedLease replaced = new()
        {
            WriteMode = WriteMode.Retained,
            RetirementMode = RetirementMode.Quiescent,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge replacedBridge =
            NewBridge(replaced);
        Write(replacedBridge, report);
        replaced.ReplaceActiveClaimWithForeignFence();

        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined,
            replacedBridge.TryRetireRetainedOperation().Outcome);
        Assert.AreEqual(0, replaced.RetirementCount,
            "A dependency willing to echo Quiescent must not be invoked after " +
            "the exact active provenance changed.");
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            replacedBridge.State);
        Write(replacedBridge, report);
        Assert.AreEqual(1, replaced.WriteCount);
    }

    [TestMethod]
    public void PostRetirementClaimStateMustMatchReturnedEvidence()
    {
        byte[] report = Report(1, 1);
        ScriptedLease uncleared = new()
        {
            WriteMode = WriteMode.Retained,
            RetirementMode = RetirementMode.QuiescentWithoutClearing,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge unclearedBridge =
            NewBridge(uncleared);
        Write(unclearedBridge, report);

        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined,
            unclearedBridge.TryRetireRetainedOperation().Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            unclearedBridge.State);

        ScriptedLease missing = new()
        {
            WriteMode = WriteMode.Retained,
            RetirementMode = RetirementMode.RetainedWithoutCurrent,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge missingBridge =
            NewBridge(missing);
        Write(missingBridge, report);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined,
            missingBridge.TryRetireRetainedOperation().Outcome);

        ScriptedLease throwing = new()
        {
            WriteMode = WriteMode.Retained,
            RetirementMode = RetirementMode.Quiescent,
        };
        Switch2ProUsbOwnedHdRumbleTransportBridge throwingBridge =
            NewBridge(throwing);
        Write(throwingBridge, report);
        throwing.ThrowClaimAuthenticationOnCall = 3;
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleDrainOutcome.Quarantined,
            throwingBridge.TryRetireRetainedOperation().Outcome);
        Assert.AreEqual(1, throwing.RetirementCount);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            throwingBridge.State);
    }

    [TestMethod]
    public void DrainEvidenceAuthenticatesOnlyWhileItsRevisionIsCurrent()
    {
        ScriptedLease lease = new();
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge = NewBridge(lease);
        Switch2ProUsbOwnedHdRumbleDrainResult empty =
            bridge.TryRetireRetainedOperation();
        Assert.IsTrue(bridge.Authenticates(empty));

        lease.WriteMode = WriteMode.Retained;
        Write(bridge, Report(1, 1));
        Assert.IsFalse(bridge.Authenticates(empty));
        lease.RetirementMode = RetirementMode.Quiescent;
        Switch2ProUsbOwnedHdRumbleDrainResult quiescent =
            bridge.TryRetireRetainedOperation();
        Assert.IsTrue(bridge.Authenticates(quiescent));

        lease.WriteMode = WriteMode.Complete;
        Write(bridge, Report(2, 2));
        Assert.IsFalse(bridge.Authenticates(quiescent),
            "A later attempt must invalidate old quiescence evidence.");

        lease.WriteMode = WriteMode.Retained;
        Write(bridge, Report(3, 3));
        lease.RetirementMode = RetirementMode.Rejected;
        Switch2ProUsbOwnedHdRumbleDrainResult quarantined =
            bridge.TryRetireRetainedOperation();
        Assert.IsTrue(bridge.Authenticates(quarantined));
        Assert.IsFalse(bridge.Authenticates(quiescent));
    }

    [TestMethod]
    public void ImmutableAuthenticationContradictionFailsClosedBeforeWrite()
    {
        ScriptedLease lease = new();
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge = NewBridge(lease);
        lease.Authenticated = false;

        Switch2ProUsbHdRumbleTransportWriteResult result = Write(bridge,
            Report(1, 1));

        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, result.Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            bridge.State);
        Assert.AreEqual(0, lease.WriteCount);

        ScriptedLease changedBound = new();
        Switch2ProUsbOwnedHdRumbleTransportBridge boundBridge =
            NewBridge(changedBound);
        changedBound.MaximumMilliseconds = 99;
        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.
            OutcomeUncertain, Write(boundBridge, Report(2, 2)).Outcome);
        Assert.AreEqual(Switch2ProUsbOwnedHdRumbleBridgeState.Quarantined,
            boundBridge.State);
        Assert.AreEqual(0, changedBound.WriteCount);
    }

    [TestMethod]
    public void WarmCompletedPathAllocatesZeroManagedBytes()
    {
        ScriptedLease lease = new();
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge = NewBridge(lease);
        byte[] report = Report(1, 1);
        for (int index = 0; index < 32; index++)
        {
            Write(bridge, report);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Switch2ProUsbHdRumbleTransportWriteOutcome outcome = default;
        for (int index = 0; index < 1_000; index++)
        {
            outcome = Write(bridge, report).Outcome;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(Switch2ProUsbHdRumbleTransportWriteOutcome.Completed,
            outcome);
        Assert.AreEqual(0L, allocated,
            $"Owned HD-rumble bridge allocated {allocated} bytes.");
    }

    private static Switch2ProUsbOwnedHdRumbleTransportBridge NewBridge(
        ScriptedLease lease) => new(lease, DeviceGeneration,
        TransportGeneration, WaitMilliseconds);

    private static Switch2ProUsbHdRumbleTransportWriteResult Write(
        Switch2ProUsbOwnedHdRumbleTransportBridge bridge, byte[] report) =>
        bridge.TryWriteReport(report, Switch2ControllerModel.ProController2,
            DeviceGeneration, TransportGeneration);

    private static byte[] Report(byte counter, int seed)
    {
        byte amplitude = (byte)Math.Clamp(seed, 0, byte.MaxValue);
        Switch2HdRumbleSubframe subframe = new(0x101, amplitude, 0x181,
            amplitude);
        Switch2HdRumbleGroup left = new(subframe, subframe, subframe);
        Switch2HdRumbleGroup right = new(
            new Switch2HdRumbleSubframe(0x102, amplitude, 0x182, amplitude),
            subframe, subframe);
        byte[] report = new byte[Switch2UsbHdRumbleCodec.ReportLength];
        Assert.IsTrue(Switch2UsbHdRumbleCodec.TryEncodeProController(counter,
            left, right, report));
        return report;
    }

    private static Switch2HdRumblePhysicalSubmission StopSubmission(
        ulong deliveryEpoch) =>
        Switch2HdRumblePhysicalSubmission.CreateStop(DeviceGeneration,
            TransportGeneration, deliveryEpoch);

    private static ControllerFeedbackDelivery StopDelivery(
        ulong deliveryEpoch) => new(
        ControllerFeedbackDeliveryDisposition.Stop,
        ControllerFeedbackPublicationOrigin.NativeGame, default,
        DeviceGeneration, TransportGeneration, deliveryEpoch);

    private enum WriteMode : byte
    {
        Complete = 0,
        Retained,
        Quarantined,
        ForeignCompletion,
        ForeignRetainedClaim,
        SameGenerationForeignRetainedClaim,
        Malformed,
        Throw,
    }

    private enum RetirementMode : byte
    {
        Quiescent = 0,
        Retained,
        QuiescentWithoutClearing,
        RetainedWithoutCurrent,
        Rejected,
        Quarantined,
        ForeignQuiescent,
        Throw,
    }

    private sealed class ScriptedLease : ISwitch2ProUsbOwnedCompositeLease,
        ISwitch2ProUsbOwnedFeedbackOutputLease
    {
        private readonly object claimFence = new();
        private readonly object foreignFence = new();
        private int concurrentWrites;
        private int order;
        private ulong sequence;
        private Switch2ProUsbOwnedOutputOperationClaim activeClaim;

        internal bool Authenticated = true;
        internal bool ThrowAuthentication;
        internal bool BlockWrite;
        internal bool ReuseFirstClaim;
        internal WriteMode WriteMode;
        internal RetirementMode RetirementMode;
        internal Action DuringWrite;
        internal Action DuringRetirement;
        internal readonly ManualResetEventSlim WriteEntered = new(false);
        internal readonly ManualResetEventSlim ReleaseWrite = new(false);
        internal readonly byte[] FirstReport =
            new byte[Switch2UsbHdRumbleCodec.ReportLength];
        internal readonly byte[] LastReport =
            new byte[Switch2UsbHdRumbleCodec.ReportLength];
        internal Switch2ProUsbOwnedOutputOperationClaim FirstClaim;
        internal Switch2ProUsbOwnedOutputOperationClaim LastRetirementClaim;
        internal Switch2ProUsbHdRumbleTransportWriteResult ReentrantWriteResult;
        internal Switch2ProUsbOwnedHdRumbleDrainResult ReentrantDrainResult;
        internal int WriteCount;
        internal int RetirementCount;
        internal int MaximumConcurrentWrites;
        internal int LastWriteTimeout;
        internal int LastRetirementTimeout;
        internal int LastWriteOrder;
        internal int LastRetirementOrder;
        internal int MaximumMilliseconds = 100;
        internal int ClaimAuthenticationCount;
        internal int ThrowClaimAuthenticationOnCall;

        public int MaximumOutputOperationMilliseconds => MaximumMilliseconds;

        public Switch2PhysicalInputRegistration Registration => default;

        public Switch2PhysicalInputLifetime Lifetime => default;

        public bool AuthenticatesComposite(Switch2ControllerModel model,
            ulong deviceGeneration, ulong transportGeneration)
        {
            if (ThrowAuthentication)
            {
                throw new InvalidOperationException(
                    "Injected authentication failure.");
            }
            return Authenticated &&
                model == Switch2ControllerModel.ProController2 &&
                deviceGeneration == DeviceGeneration &&
                transportGeneration == TransportGeneration;
        }

        public bool AuthenticatesOutputOperationClaim(
            in Switch2ProUsbOwnedOutputOperationClaim claim)
        {
            int call = ++ClaimAuthenticationCount;
            if (call == ThrowClaimAuthenticationOnCall)
            {
                throw new InvalidOperationException(
                    "Injected claim-authentication failure.");
            }
            return claim.Authenticates(claimFence, DeviceGeneration,
                TransportGeneration, activeClaim.Sequence) &&
                activeClaim.Equals(claim);
        }

        public Switch2ProUsbOwnedOutputWriteAttempt TryWriteReportBounded(
            ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
            ulong expectedDeviceGeneration,
            ulong expectedTransportGeneration, int timeoutMilliseconds)
        {
            int concurrent = Interlocked.Increment(ref concurrentWrites);
            MaximumConcurrentWrites = Math.Max(MaximumConcurrentWrites,
                concurrent);
            try
            {
                int call = ++WriteCount;
                LastWriteTimeout = timeoutMilliseconds;
                LastWriteOrder = ++order;
                report.CopyTo(LastReport);
                if (call == 1)
                {
                    report.CopyTo(FirstReport);
                }
                DuringWrite?.Invoke();
                if (BlockWrite)
                {
                    WriteEntered.Set();
                    ReleaseWrite.Wait(TimeSpan.FromSeconds(5));
                }

                if (WriteMode == WriteMode.Throw)
                {
                    throw new InvalidOperationException(
                        "Injected output failure.");
                }
                if (WriteMode == WriteMode.Malformed)
                {
                    return default;
                }
                if (WriteMode == WriteMode.Retained)
                {
                    Switch2ProUsbOwnedOutputOperationClaim claim =
                        ReuseFirstClaim && FirstClaim.IsValid ? FirstClaim :
                        new Switch2ProUsbOwnedOutputOperationClaim(claimFence,
                            DeviceGeneration, TransportGeneration, ++sequence);
                    if (!FirstClaim.IsValid)
                    {
                        FirstClaim = claim;
                    }
                    activeClaim = claim;
                    return new Switch2ProUsbOwnedOutputWriteAttempt(
                        Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(
                            expectedModel, expectedDeviceGeneration,
                            expectedTransportGeneration,
                            Switch2ProUsbHdRumbleTransportWriteFailure.
                                TransportRejected), claim);
                }
                if (WriteMode == WriteMode.Quarantined)
                {
                    Switch2ProUsbOwnedOutputOperationClaim claim =
                        new(claimFence, DeviceGeneration,
                            TransportGeneration, ++sequence);
                    if (!FirstClaim.IsValid)
                    {
                        FirstClaim = claim;
                    }
                    activeClaim = claim;
                    return Switch2ProUsbOwnedOutputWriteAttempt.Quarantine(
                        Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(
                            expectedModel, expectedDeviceGeneration,
                            expectedTransportGeneration,
                            Switch2ProUsbHdRumbleTransportWriteFailure.
                                TransportEnded), claim);
                }
                if (WriteMode == WriteMode.ForeignCompletion)
                {
                    return new Switch2ProUsbOwnedOutputWriteAttempt(
                        Switch2ProUsbHdRumbleTransportWriteResult.Complete(
                            expectedModel, expectedDeviceGeneration + 1,
                            expectedTransportGeneration, report.Length),
                        default);
                }
                if (WriteMode == WriteMode.ForeignRetainedClaim)
                {
                    activeClaim = new Switch2ProUsbOwnedOutputOperationClaim(
                        claimFence, DeviceGeneration + 1,
                        TransportGeneration, ++sequence);
                    return new Switch2ProUsbOwnedOutputWriteAttempt(
                        Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(
                            expectedModel, expectedDeviceGeneration,
                            expectedTransportGeneration,
                            Switch2ProUsbHdRumbleTransportWriteFailure.
                                TransportRejected),
                        activeClaim);
                }
                if (WriteMode ==
                    WriteMode.SameGenerationForeignRetainedClaim)
                {
                    activeClaim = new Switch2ProUsbOwnedOutputOperationClaim(
                        foreignFence, DeviceGeneration, TransportGeneration,
                        1_000_000UL + ++sequence);
                    return new Switch2ProUsbOwnedOutputWriteAttempt(
                        Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(
                            expectedModel, expectedDeviceGeneration,
                            expectedTransportGeneration,
                            Switch2ProUsbHdRumbleTransportWriteFailure.
                                TransportRejected),
                        activeClaim);
                }

                activeClaim = default;
                return new Switch2ProUsbOwnedOutputWriteAttempt(
                    Switch2ProUsbHdRumbleTransportWriteResult.Complete(
                        expectedModel, expectedDeviceGeneration,
                        expectedTransportGeneration, report.Length), default);
            }
            finally
            {
                Interlocked.Decrement(ref concurrentWrites);
            }
        }

        public Switch2ProUsbOwnedOutputRetirementResult
            TryRetireOutputOperation(
                in Switch2ProUsbOwnedOutputOperationClaim claim,
                int timeoutMilliseconds)
        {
            RetirementCount++;
            LastRetirementTimeout = timeoutMilliseconds;
            LastRetirementClaim = claim;
            LastRetirementOrder = ++order;
            DuringRetirement?.Invoke();
            Switch2ProUsbOwnedOutputRetirementResult result =
                RetirementMode switch
                {
                    RetirementMode.Quiescent =>
                        Switch2ProUsbOwnedOutputRetirementResult.Quiescent(claim),
                    RetirementMode.Retained =>
                        Switch2ProUsbOwnedOutputRetirementResult.Retained(claim),
                    RetirementMode.QuiescentWithoutClearing =>
                        Switch2ProUsbOwnedOutputRetirementResult.Quiescent(claim),
                    RetirementMode.RetainedWithoutCurrent =>
                        Switch2ProUsbOwnedOutputRetirementResult.Retained(claim),
                    RetirementMode.Rejected =>
                        Switch2ProUsbOwnedOutputRetirementResult.Reject(claim),
                    RetirementMode.Quarantined =>
                        Switch2ProUsbOwnedOutputRetirementResult.Quarantine(claim),
                    RetirementMode.ForeignQuiescent =>
                        Switch2ProUsbOwnedOutputRetirementResult.Quiescent(
                            new Switch2ProUsbOwnedOutputOperationClaim(foreignFence,
                                DeviceGeneration, TransportGeneration,
                                claim.Sequence)),
                    RetirementMode.Throw => throw new InvalidOperationException(
                        "Injected retirement failure."),
                    _ => default,
                };
            if ((result.Outcome ==
                    Switch2ProUsbOwnedOutputRetirementOutcome.
                        ExactOperationQuiescent && RetirementMode !=
                    RetirementMode.QuiescentWithoutClearing) ||
                RetirementMode == RetirementMode.RetainedWithoutCurrent)
            {
                activeClaim = default;
            }
            return result;
        }

        internal void ReplaceActiveClaimWithForeignFence()
        {
            activeClaim = new Switch2ProUsbOwnedOutputOperationClaim(
                foreignFence, DeviceGeneration, TransportGeneration,
                activeClaim.Sequence);
        }

        public Switch2ProUsbStartupCommandCompletion Execute(
            in Switch2ProUsbStartupCommandClaim claim,
            ReadOnlySpan<byte> exactRequest, int timeoutMilliseconds) =>
            Switch2ProUsbStartupCommandCompletion.ProvenNotConsumed(claim,
                claim.Step);

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

        public bool TryWaitForInputQuiescence(
            int timeoutMilliseconds) => true;

        public void DisposeQuiesced()
        {
        }
    }
}
