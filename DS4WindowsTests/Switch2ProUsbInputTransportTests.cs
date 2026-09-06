using System.Buffers.Binary;
using System.Reflection;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2ProUsbInputTransportTests
{
    private static readonly Guid ContainerAGuid =
        Guid.Parse("27eb8d6c-af38-4927-960b-9d733720d9f7");
    private static readonly Guid ContainerBGuid =
        Guid.Parse("72ac628c-783f-4024-af4f-17316f7b3128");

    [TestMethod]
    public void FactoryAdmitsExactCompositeBeforeOpeningReadOnlyLease()
    {
        var discovery = new FakeDiscovery(CreateObservation());
        var lease = new FakeLease();
        var native = new FakeNativeAdapter(lease);
        var sink = new RecordingSink();

        Assert.IsTrue(TryCreateOwner(discovery, native, sink, 11, 29,
            out Switch2ProUsbInputTransportOwner owner,
            out Switch2ProUsbTransportCreateFailure failure),
            failure.Kind.ToString());

        Assert.AreEqual(1, discovery.ObservationCount);
        Assert.AreEqual(1, native.OpenCount);
        Assert.IsTrue(native.RequestedRegistration.IsValid);
        Assert.AreEqual(native.RequestedRegistration, lease.Registration);
        Assert.AreEqual(11UL,
            owner.Lifetime.SessionDescriptor.DeviceGeneration);
        Assert.AreEqual(29UL,
            owner.Lifetime.SessionDescriptor.TransportGeneration);
        Assert.AreEqual(Switch2ProUsbInputTransportState.Open, owner.State);
        Assert.AreEqual(0, lease.BeginCount,
            "Construction must not issue controller I/O.");
    }

    [TestMethod]
    public void FactoryFailsClosedBeforeOrAfterLeaseAtTheCorrectBoundary()
    {
        var malformedDiscovery = new FakeDiscovery(CreateObservation(
            matchingInputInterfaceCount: 2));
        var unopenedLease = new FakeLease();
        var unopenedNative = new FakeNativeAdapter(unopenedLease);

        Assert.IsFalse(TryCreateOwner(malformedDiscovery, unopenedNative,
            new RecordingSink(), 1, 1, out _,
            out Switch2ProUsbTransportCreateFailure malformed));
        Assert.AreEqual(
            Switch2ProUsbTransportCreateFailureKind.CompositeRejected,
            malformed.Kind);
        Assert.AreEqual(Switch2PhysicalAdmissionFailure.
            InputInterfaceMultiplicityMismatch, malformed.AdmissionFailure);
        Assert.AreEqual(0, unopenedNative.OpenCount,
            "A rejected topology must never reach native lease acquisition.");

        Switch2PhysicalInputRegistration wrongRegistration = Admit(
            CreateObservation(ContainerBGuid));
        var mismatchedLease = new FakeLease
        {
            ForcedRegistration = wrongRegistration,
        };
        var native = new FakeNativeAdapter(mismatchedLease);
        Assert.IsFalse(TryCreateOwner(
            new FakeDiscovery(CreateObservation()), native,
            new RecordingSink(), 1, 1, out _,
            out Switch2ProUsbTransportCreateFailure mismatch));
        Assert.AreEqual(Switch2ProUsbTransportCreateFailureKind.
            LeaseRegistrationMismatch, mismatch.Kind);
        Assert.AreEqual(1, mismatchedLease.WaitCount);
        Assert.AreEqual(1, mismatchedLease.DisposeCount,
            "A mismatched unstarted lease must be released quiescently.");
        Assert.IsFalse(mismatch.RequiresQuarantine);
        Assert.IsNull(mismatch.QuarantinedLeaseOwner);
        Assert.AreEqual(Switch2ProUsbDisposeFailure.None,
            mismatch.RejectedLeaseDisposeFailure);

        var partialLease = new FakeLease();
        var partialNative = new FakeNativeAdapter(partialLease)
        {
            OpenResult = false,
        };
        Assert.IsFalse(TryCreateOwner(
            new FakeDiscovery(CreateObservation()), partialNative,
            new RecordingSink(), 1, 1, out _,
            out Switch2ProUsbTransportCreateFailure partial));
        Assert.AreEqual(Switch2ProUsbTransportCreateFailureKind.
            NativeLeaseRejected, partial.Kind);
        Assert.AreEqual(1, partialLease.DisposeCount,
            "A partially returned native lease must not leak.");
        Assert.IsFalse(partial.RequiresQuarantine);
        Assert.IsNull(partial.QuarantinedLeaseOwner);
        Assert.AreEqual(Switch2ProUsbDisposeFailure.None,
            partial.RejectedLeaseDisposeFailure);
    }

    [TestMethod]
    public void RejectedLeaseWaitTimeoutReturnsRetainedRetryableOwner()
    {
        Switch2PhysicalInputRegistration wrongRegistration = Admit(
            CreateObservation(ContainerBGuid));
        var lease = new FakeLease
        {
            ForcedRegistration = wrongRegistration,
            WaitResult = false,
        };

        Assert.IsFalse(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), new RecordingSink(), 1, 1,
            out _, out Switch2ProUsbTransportCreateFailure failure));

        Assert.AreEqual(Switch2ProUsbTransportCreateFailureKind.
            LeaseRegistrationMismatch, failure.Kind);
        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.AreEqual(Switch2ProUsbDisposeFailure.
            NativeQuiescenceTimedOut,
            failure.RejectedLeaseDisposeFailure);
        Assert.IsNotNull(failure.QuarantinedLeaseOwner);
        Assert.AreEqual(Switch2ProUsbRejectedLeaseState.Retained,
            failure.QuarantinedLeaseOwner.State);
        Assert.AreEqual(1, lease.WaitCount);
        Assert.AreEqual(0, lease.DisposeCount);
        Assert.AreEqual(0, lease.BeginCount);

        lease.WaitResult = true;
        Assert.IsTrue(failure.QuarantinedLeaseOwner.TryQuiesceAndDispose(10,
            out Switch2ProUsbDisposeFailure retryFailure),
            retryFailure.ToString());
        Assert.AreEqual(Switch2ProUsbRejectedLeaseState.Disposed,
            failure.QuarantinedLeaseOwner.State);
        Assert.AreEqual(2, lease.WaitCount);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(0, lease.BeginCount);
    }

    [TestMethod]
    public void RejectedLeaseWaitExceptionRetainsExactOwnerForRetry()
    {
        var lease = new FakeLease { ThrowOnWait = true };
        var native = new FakeNativeAdapter(lease) { OpenResult = false };

        Assert.IsFalse(TryCreateOwner(
            new FakeDiscovery(CreateObservation()), native,
            new RecordingSink(), 1, 1, out _,
            out Switch2ProUsbTransportCreateFailure failure));

        Assert.AreEqual(Switch2ProUsbTransportCreateFailureKind.
            NativeLeaseRejected, failure.Kind);
        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.AreEqual(Switch2ProUsbDisposeFailure.
            NativeQuiescenceRejected,
            failure.RejectedLeaseDisposeFailure);
        Assert.IsNotNull(failure.QuarantinedLeaseOwner);
        Assert.AreEqual(Switch2ProUsbRejectedLeaseState.Retained,
            failure.QuarantinedLeaseOwner.State);
        Assert.AreEqual(1, lease.WaitCount);
        Assert.AreEqual(0, lease.DisposeCount);

        lease.ThrowOnWait = false;
        Assert.IsTrue(failure.QuarantinedLeaseOwner.TryQuiesceAndDispose(10,
            out Switch2ProUsbDisposeFailure retryFailure),
            retryFailure.ToString());
        Assert.AreEqual(2, lease.WaitCount);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(Switch2ProUsbRejectedLeaseState.Disposed,
            failure.QuarantinedLeaseOwner.State);
    }

    [TestMethod]
    public void RejectedLeaseDisposeExceptionQuarantinesWithoutDoubleDispose()
    {
        var lease = new FakeLease { ThrowOnDispose = true };
        var native = new FakeNativeAdapter(lease) { OpenResult = false };

        Assert.IsFalse(TryCreateOwner(
            new FakeDiscovery(CreateObservation()), native,
            new RecordingSink(), 1, 1, out _,
            out Switch2ProUsbTransportCreateFailure failure));

        Assert.AreEqual(Switch2ProUsbTransportCreateFailureKind.
            NativeLeaseRejected, failure.Kind);
        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.AreEqual(Switch2ProUsbDisposeFailure.NativeDisposeRejected,
            failure.RejectedLeaseDisposeFailure);
        Assert.IsNotNull(failure.QuarantinedLeaseOwner);
        Assert.AreEqual(Switch2ProUsbRejectedLeaseState.Quarantined,
            failure.QuarantinedLeaseOwner.State);
        Assert.AreEqual(1, lease.WaitCount);
        Assert.AreEqual(1, lease.DisposeCount);

        lease.ThrowOnDispose = false;
        Assert.IsFalse(failure.QuarantinedLeaseOwner.TryQuiesceAndDispose(10,
            out Switch2ProUsbDisposeFailure retryFailure));
        Assert.AreEqual(Switch2ProUsbDisposeFailure.NativeDisposeRejected,
            retryFailure);
        Assert.AreEqual(1, lease.WaitCount,
            "An outcome-uncertain dispose must not restart quiescence.");
        Assert.AreEqual(1, lease.DisposeCount,
            "An outcome-uncertain dispose must never be repeated.");
    }

    [TestMethod]
    public void ReadOwnerPublishesOneGenerationFencedCanonicalFrame()
    {
        CreateHarness(5, 8, out Switch2ProUsbInputTransportOwner owner,
            out FakeLease lease, out RecordingSink sink);

        Assert.IsTrue(owner.TryBeginRead(out Switch2ProUsbReadClaim claim,
            out Switch2ProUsbReadBeginFailure beginFailure),
            beginFailure.ToString());
        Assert.AreEqual(5UL, claim.DeviceGeneration);
        Assert.AreEqual(8UL, claim.TransportGeneration);
        Assert.AreEqual(1UL, claim.Sequence);
        Assert.AreEqual(64, lease.RequestedCount);
        Assert.AreEqual(0, lease.RequestedOffset);

        lease.FillPacket(100, 0x02084081, 0x123, 0x456, 0x789, 0xABC);
        Switch2ProUsbReadCompletionDisposition disposition =
            lease.Complete(claim, 64, 10_000,
                Switch2ProUsbNativeReadStatus.Completed);

        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            disposition);
        Assert.AreEqual(1, sink.PublishCount);
        Assert.AreEqual(5UL, sink.LastFrame.DeviceGeneration);
        Assert.AreEqual(8UL, sink.LastFrame.TransportGeneration);
        Assert.AreEqual(100u, sink.LastFrame.DeviceCounterRaw);
        Assert.IsTrue(sink.LastFrame.TryGetLeftStick(out var left));
        Assert.AreEqual((ushort)0x123, left.Raw.X);
        Assert.AreEqual((ushort)0x456, left.Raw.Y);
    }

    [TestMethod]
    public void ExactCompletedReadRetirementPermitsImmediateRearm()
    {
        CreateHarness(5, 8, out Switch2ProUsbInputTransportOwner owner,
            out FakeLease lease, out RecordingSink sink);

        Assert.IsTrue(owner.TryBeginRead(out var first, out _));
        lease.FillPacket(4);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            lease.Complete(first, 64, 100));
        Assert.IsTrue(owner.TryRetireCompletedRead(first, 11,
            out Switch2ProUsbReadRetirementResult result,
            out Switch2ProUsbReadRetirementFailure retired),
            retired.ToString());
        Assert.AreEqual(first, result.Claim);
        Assert.IsTrue(result.CompletionObserved);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            result.CompletionDisposition);
        Assert.IsTrue(result.PermitsRearm);
        Assert.AreEqual(1, lease.RetirementCount);
        Assert.AreEqual(11, lease.LastWaitTimeoutMilliseconds);

        Assert.IsTrue(owner.TryBeginRead(out var second, out _));
        Assert.AreEqual(2UL, second.Sequence);
        lease.FillPacket(8);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            lease.Complete(second, 64, 101));
        Assert.AreEqual(2, sink.PublishCount);
    }

    [TestMethod]
    public void CompletedClaimMustBeExplicitlyRetiredBeforeAnotherBegin()
    {
        CreateHarness(5, 8, out Switch2ProUsbInputTransportOwner owner,
            out FakeLease lease, out _);
        Assert.IsTrue(owner.TryBeginRead(out var first, out _));
        lease.FillPacket(4);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            lease.Complete(first, 64, 100));

        Assert.IsFalse(owner.TryBeginRead(out _,
            out Switch2ProUsbReadBeginFailure stillOwned));
        Assert.AreEqual(Switch2ProUsbReadBeginFailure.ReadAlreadyOutstanding,
            stillOwned);
        Assert.AreEqual(1, lease.BeginCount,
            "Begin must not retire or overwrite the native submission claim.");

        Assert.IsTrue(owner.TryRetireCompletedRead(first, 0,
            out Switch2ProUsbReadRetirementResult retired, out _));
        Assert.IsTrue(retired.PermitsRearm);
        Assert.IsTrue(owner.TryBeginRead(out var second, out _));
        Assert.AreEqual(2UL, second.Sequence);
    }

    [TestMethod]
    public void FailedReadRetirementRetainsTheExactClaimForRetry()
    {
        CreateHarness(1, 1, out Switch2ProUsbInputTransportOwner owner,
            out FakeLease lease, out _);
        Assert.IsTrue(owner.TryBeginRead(out var claim, out _));
        lease.FillPacket(4);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            lease.Complete(claim, 64, 100));

        lease.RetirementResult = false;
        Assert.IsFalse(owner.TryRetireCompletedRead(claim, 3,
            out Switch2ProUsbReadRetirementFailure timeout));
        Assert.AreEqual(Switch2ProUsbReadRetirementFailure.
            NativeQuiescenceTimedOut, timeout);

        lease.RetirementResult = true;
        Assert.IsTrue(owner.TryRetireCompletedRead(claim, 4,
            out Switch2ProUsbReadRetirementFailure retry),
            retry.ToString());
        Assert.AreEqual(2, lease.RetirementCount);
    }

    [TestMethod]
    public void NativeQuiescenceWithoutCompletionStopsFailClosed()
    {
        CreateHarness(1, 1, out Switch2ProUsbInputTransportOwner owner,
            out FakeLease lease, out RecordingSink sink);
        Assert.IsTrue(owner.TryBeginRead(out var claim, out _));

        Assert.IsTrue(owner.TryRetireCompletedRead(claim, 5,
            out Switch2ProUsbReadRetirementResult result,
            out Switch2ProUsbReadRetirementFailure retired),
            retired.ToString());
        Assert.AreEqual(claim, result.Claim);
        Assert.IsFalse(result.CompletionObserved);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Invalid,
            result.CompletionDisposition);
        Assert.IsFalse(result.PermitsRearm);
        Assert.AreEqual(Switch2ProUsbInputTransportState.StopRequested,
            owner.State);
        Assert.AreEqual(0, sink.PublishCount);
        Assert.IsFalse(owner.TryBeginRead(out _, out var closed));
        Assert.AreEqual(Switch2ProUsbReadBeginFailure.LifecycleClosed,
            closed);
    }

    [TestMethod]
    public void ReadRetirementRejectsInvalidTimeoutAndForeignClaim()
    {
        CreateHarness(1, 1, out Switch2ProUsbInputTransportOwner owner,
            out _, out _);
        CreateHarness(1, 1, out Switch2ProUsbInputTransportOwner other,
            out _, out _);
        Assert.IsTrue(owner.TryBeginRead(out var claim, out _));
        Assert.IsTrue(other.TryBeginRead(out var foreign, out _));

        Assert.IsFalse(owner.TryRetireCompletedRead(claim, -1,
            out Switch2ProUsbReadRetirementFailure timeout));
        Assert.AreEqual(Switch2ProUsbReadRetirementFailure.InvalidTimeout,
            timeout);
        Assert.IsFalse(owner.TryRetireCompletedRead(foreign, 0,
            out Switch2ProUsbReadRetirementFailure invalid));
        Assert.AreEqual(Switch2ProUsbReadRetirementFailure.InvalidClaim,
            invalid);
    }

    [TestMethod]
    public void StaleForeignAndDuplicateClaimsNeverAdvanceOrPublish()
    {
        CreateHarness(3, 4, out Switch2ProUsbInputTransportOwner firstOwner,
            out FakeLease firstLease, out RecordingSink firstSink);
        Assert.IsTrue(firstOwner.TryBeginRead(out var first, out _));
        firstLease.FillPacket(4);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            firstLease.Complete(first, 64, 100));
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.StaleClaim,
            firstLease.Complete(first, 64, 101),
            "A duplicate completion must be idempotently ignored.");
        Assert.IsTrue(firstOwner.TryRetireCompletedRead(first, 0, out _));

        Assert.IsTrue(firstOwner.TryBeginRead(out var second, out _));
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.StaleClaim,
            firstLease.Complete(first, 64, 102));
        Assert.AreEqual(1, firstSink.PublishCount);

        CreateHarness(3, 4, out Switch2ProUsbInputTransportOwner otherOwner,
            out FakeLease otherLease, out _);
        Assert.IsTrue(otherOwner.TryBeginRead(out var foreign, out _));
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.StaleClaim,
            firstLease.Complete(foreign, 64, 103),
            "The private owner fence must reject coincident generations.");

        firstLease.FillPacket(8);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            firstLease.Complete(second, 64, 104));
        Assert.AreEqual(2, firstSink.PublishCount);

        otherLease.FillPacket(4);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            otherLease.Complete(foreign, 64, 105));
    }

    [TestMethod]
    public void SoleReadOwnershipRejectsConcurrentStarts()
    {
        CreateHarness(1, 1, out Switch2ProUsbInputTransportOwner owner,
            out FakeLease lease, out _);
        int admitted = 0;

        Parallel.For(0, 32, iteration =>
        {
            if (owner.TryBeginRead(out _, out _))
            {
                Interlocked.Increment(ref admitted);
            }
        });

        Assert.AreEqual(1, admitted);
        Assert.AreEqual(1, lease.BeginCount);
        Assert.IsTrue(owner.RequestStop());
        Assert.AreEqual(1, lease.CancelCount);
    }

    [TestMethod]
    public void ReadSequenceExhaustionClosesInsteadOfReusingAClaim()
    {
        CreateHarness(1, 1, out Switch2ProUsbInputTransportOwner owner,
            out FakeLease lease, out _);
        FieldInfo sequence = typeof(Switch2ProUsbInputTransportOwner).GetField(
            "readSequence", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(sequence);
        sequence.SetValue(owner, ulong.MaxValue);

        Assert.IsFalse(owner.TryBeginRead(out Switch2ProUsbReadClaim claim,
            out Switch2ProUsbReadBeginFailure failure));
        Assert.IsFalse(claim.IsValid);
        Assert.AreEqual(Switch2ProUsbReadBeginFailure.SequenceExhausted,
            failure);
        Assert.AreEqual(Switch2ProUsbInputTransportState.StopRequested,
            owner.State);
        Assert.AreEqual(0, lease.BeginCount);
    }

    [TestMethod]
    public void StopCancelsOnceAndDisposeIsExplicitlyBoundedAndRetryable()
    {
        CreateHarness(9, 12, out Switch2ProUsbInputTransportOwner owner,
            out FakeLease lease, out RecordingSink sink);
        Assert.IsTrue(owner.TryBeginRead(out var stale, out _));

        Assert.IsTrue(owner.RequestStop());
        Assert.IsFalse(owner.RequestStop());
        Assert.AreEqual(1, lease.CancelCount);
        Assert.AreEqual(Switch2ProUsbInputTransportState.StopRequested,
            owner.State);
        Assert.IsFalse(owner.TryBeginRead(out _, out var closed));
        Assert.AreEqual(Switch2ProUsbReadBeginFailure.LifecycleClosed, closed);

        lease.WaitResult = false;
        Assert.IsFalse(owner.TryQuiesceAndDispose(7,
            out Switch2ProUsbDisposeFailure timeout));
        Assert.AreEqual(Switch2ProUsbDisposeFailure.
            NativeQuiescenceTimedOut, timeout);
        Assert.AreEqual(7, lease.LastWaitTimeoutMilliseconds);
        Assert.AreEqual(0, lease.DisposeCount);

        lease.WaitResult = true;
        Assert.IsTrue(owner.TryQuiesceAndDispose(20, out var disposed),
            disposed.ToString());
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(Switch2ProUsbInputTransportState.Disposed,
            owner.State);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.StaleClaim,
            lease.Complete(stale, 64, 1_000),
            "Native quiescence retires an uncompleted cancelled claim.");
        Assert.AreEqual(0, sink.PublishCount);

        Assert.IsTrue(owner.TryQuiesceAndDispose(0, out disposed));
        Assert.AreEqual(1, lease.DisposeCount,
            "Disposal must be idempotent.");
        Assert.IsFalse(owner.TryQuiesceAndDispose(
            Switch2ProUsbInputTransportOwner.
                MaximumDisposeTimeoutMilliseconds + 1, out var invalid));
        Assert.AreEqual(Switch2ProUsbDisposeFailure.InvalidTimeout, invalid);
    }

    [TestMethod]
    public void InvalidReportNativeFailureAndSinkRefusalFailClosed()
    {
        CreateHarness(1, 1, out Switch2ProUsbInputTransportOwner owner,
            out FakeLease lease, out RecordingSink sink);
        Assert.IsTrue(owner.TryBeginRead(out var malformed, out _));
        lease.FillPacket(100);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.InvalidReport,
            lease.Complete(malformed, 63, 100));
        Assert.AreEqual(0, sink.PublishCount);
        Assert.IsTrue(owner.TryRetireCompletedRead(malformed, 0,
            out Switch2ProUsbReadRetirementResult malformedResult, out _));
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.InvalidReport,
            malformedResult.CompletionDisposition);
        Assert.IsFalse(malformedResult.PermitsRearm);

        Assert.IsTrue(owner.TryBeginRead(out var valid, out _));
        lease.FillPacket(104);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            lease.Complete(valid, 64, 101));
        Assert.AreEqual(Switch2CounterSequenceKind.First,
            sink.LastFrame.CounterSequence,
            "A rejected report must not advance parser state.");
        Assert.IsTrue(owner.TryRetireCompletedRead(valid, 0, out _));

        Assert.IsTrue(owner.TryBeginRead(out var failed, out _));
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.NativeFailure,
            lease.Complete(failed, 0, 102,
                Switch2ProUsbNativeReadStatus.DeviceRemoved));
        Assert.AreEqual(Switch2ProUsbInputTransportState.StopRequested,
            owner.State);
        Assert.AreEqual(1, sink.PublishCount);

        var rejectingSink = new RecordingSink { Accept = false };
        var retainedLease = new FakeLease();
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(retainedLease), rejectingSink, 2, 4,
            out Switch2ProUsbInputTransportOwner rejectingOwner, out _));
        Assert.IsTrue(rejectingOwner.TryBeginRead(out var rejected, out _));
        retainedLease.FillPacket(4);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.SinkRejected,
            retainedLease.Complete(rejected, 64, 200));
        Assert.AreEqual(Switch2ProUsbInputTransportState.StopRequested,
            rejectingOwner.State);
    }

    [TestMethod]
    public void NativeStartExceptionRetainsTheClaimUntilCancelAndQuiescence()
    {
        var lease = new FakeLease { ThrowOnBegin = true };
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), new RecordingSink(), 1, 1,
            out Switch2ProUsbInputTransportOwner owner, out _));

        Assert.IsFalse(owner.TryBeginRead(out Switch2ProUsbReadClaim claim,
            out Switch2ProUsbReadBeginFailure failure));
        Assert.IsFalse(claim.IsValid);
        Assert.AreEqual(Switch2ProUsbReadBeginFailure.NativeStartRejected,
            failure);
        Assert.AreEqual(Switch2ProUsbInputTransportState.StopRequested,
            owner.State);
        Assert.AreEqual(1, lease.CancelCount,
            "A throwing native start may have submitted I/O and must cancel.");
        Assert.IsTrue(owner.TryQuiesceAndDispose(20, out var disposed),
            disposed.ToString());
        Assert.AreEqual(1, lease.DisposeCount);
    }

    [TestMethod]
    public void QuiescenceNeverOverlapsNativeBeginOrCancellationCalls()
    {
        var beginLease = new FakeLease { BlockBegin = true };
        beginLease.AllowBeginReturn.Reset();
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(beginLease), new RecordingSink(), 1, 1,
            out Switch2ProUsbInputTransportOwner beginOwner, out _));
        Task<bool> begin = Task.Run(() =>
            beginOwner.TryBeginRead(out _, out _));
        Assert.IsTrue(beginLease.BeginEntered.Wait(TimeSpan.FromSeconds(2)));

        Assert.IsFalse(beginOwner.TryQuiesceAndDispose(0,
            out Switch2ProUsbDisposeFailure beginTimeout));
        Assert.AreEqual(Switch2ProUsbDisposeFailure.NativeTransitionTimedOut,
            beginTimeout);
        Assert.AreEqual(0, beginLease.WaitCount,
            "Native wait must not overlap a still-running native begin call.");
        beginLease.AllowBeginReturn.Set();
        Assert.IsTrue(begin.GetAwaiter().GetResult());
        Assert.AreEqual(1, beginLease.CancelCount);
        Assert.IsTrue(beginOwner.TryQuiesceAndDispose(20, out _));

        var cancelLease = new FakeLease { BlockCancel = true };
        cancelLease.AllowCancelReturn.Reset();
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(cancelLease), new RecordingSink(), 2, 3,
            out Switch2ProUsbInputTransportOwner cancelOwner, out _));
        Assert.IsTrue(cancelOwner.TryBeginRead(out _, out _));
        Task<bool> stop = Task.Run(cancelOwner.RequestStop);
        Assert.IsTrue(cancelLease.CancelEntered.Wait(
            TimeSpan.FromSeconds(2)));

        Assert.IsFalse(cancelOwner.TryQuiesceAndDispose(0,
            out Switch2ProUsbDisposeFailure cancelTimeout));
        Assert.AreEqual(Switch2ProUsbDisposeFailure.NativeTransitionTimedOut,
            cancelTimeout);
        Assert.AreEqual(0, cancelLease.WaitCount,
            "Native wait must not overlap native cancellation.");
        cancelLease.AllowCancelReturn.Set();
        Assert.IsTrue(stop.GetAwaiter().GetResult());
        Assert.IsTrue(cancelOwner.TryQuiesceAndDispose(20, out _));
    }

    [TestMethod]
    public void DisposalTimesOutWhileAnOutsideLockPublicationIsStillRunning()
    {
        using var sink = new BlockingSink();
        var lease = new FakeLease();
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), sink, 1, 1,
            out Switch2ProUsbInputTransportOwner owner, out _));
        Assert.IsTrue(owner.TryBeginRead(out var claim, out _));
        lease.FillPacket(4);

        Task<Switch2ProUsbReadCompletionDisposition> completion = Task.Run(
            () => lease.Complete(claim, 64, 1_000));
        Assert.IsTrue(sink.WaitUntilEntered(TimeSpan.FromSeconds(2)));
        Assert.IsFalse(owner.TryQuiesceAndDispose(0, out var timedOut));
        Assert.AreEqual(Switch2ProUsbDisposeFailure.ManagedCallbackTimedOut,
            timedOut);
        Assert.AreEqual(0, lease.DisposeCount);

        sink.Release();
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            completion.GetAwaiter().GetResult());
        Assert.IsTrue(owner.TryQuiesceAndDispose(20, out var disposed),
            disposed.ToString());
        Assert.AreEqual(1, lease.DisposeCount);
    }

    [TestMethod]
    public void PublicationCallbackRunsOutsideOwnerLockAndMayRequestStop()
    {
        var discovery = new FakeDiscovery(CreateObservation());
        var lease = new FakeLease();
        var native = new FakeNativeAdapter(lease);
        var sink = new CrossThreadReentrantSink();
        Assert.IsTrue(TryCreateOwner(discovery, native, sink, 1, 1,
            out Switch2ProUsbInputTransportOwner owner, out _));
        sink.Owner = owner;

        Assert.IsTrue(owner.TryBeginRead(out var claim, out _));
        lease.FillPacket(4);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            lease.Complete(claim, 64, 1_000));
        Assert.IsTrue(sink.ReentryCompleted,
            "Cross-thread reentry would time out if publication held the lock.");
        Assert.AreEqual(Switch2ProUsbInputTransportState.StopRequested,
            owner.State);
    }

    [TestMethod]
    public void PerReportBeginAcceptAndPublicationAllocateNoManagedMemory()
    {
        var lease = new FakeLease
        {
            CompleteSynchronously = true,
        };
        var sink = new RecordingSink();
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), sink, 1, 1,
            out Switch2ProUsbInputTransportOwner owner, out _));

        for (int warmup = 0; warmup < 2_000; warmup++)
        {
            lease.NextCounter = (uint)(warmup * 4);
            lease.NextTimestamp = warmup;
            Assert.IsTrue(owner.TryBeginRead(out var claim, out _));
            Assert.IsTrue(owner.TryRetireCompletedRead(claim, 0,
                out Switch2ProUsbReadRetirementResult result, out _));
            Assert.IsTrue(result.PermitsRearm);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int iteration = 2_000; iteration < 22_000; iteration++)
        {
            lease.NextCounter = (uint)(iteration * 4);
            lease.NextTimestamp = iteration;
            succeeded &= owner.TryBeginRead(out var claim, out _);
            succeeded &= lease.LastDisposition ==
                Switch2ProUsbReadCompletionDisposition.Published;
            succeeded &= owner.TryRetireCompletedRead(claim, 0,
                out Switch2ProUsbReadRetirementResult result, out _);
            succeeded &= result.PermitsRearm;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(22_000, sink.PublishCount);
        Assert.AreEqual(0L, allocated,
            $"Per-report transport accept path allocated {allocated} bytes.");
    }

    [TestMethod]
    public void NativeSurfaceHasNoOutputCommandOrRawIdentityCapability()
    {
        Type lease = typeof(ISwitch2ProUsbReadOnlyCompositeLease);
        string[] forbiddenNames =
        {
            "Write", "Output", "Haptic", "Rumble", "Led", "Feature",
            "Command",
        };
        foreach (MethodInfo method in lease.GetMethods())
        {
            foreach (string forbidden in forbiddenNames)
            {
                Assert.IsFalse(method.Name.Contains(forbidden,
                        StringComparison.OrdinalIgnoreCase),
                    $"Unexpected live capability: {method.Name}");
            }
        }

        Type[] transportTypes =
        {
            typeof(Switch2ProUsbInputTransportOwner),
            typeof(ISwitch2ProUsbOsDiscoveryAdapter),
            typeof(ISwitch2ProUsbNativeAdapter),
            lease,
            typeof(Switch2ProUsbReadClaim),
            typeof(Switch2ProUsbInputReadPump),
        };
        foreach (Type type in transportTypes)
        {
            Assert.IsFalse(type.GetProperties().Any(property =>
                    property.PropertyType == typeof(string)),
                $"{type.Name} exposes a raw string identity surface.");
            Assert.IsFalse(type.GetFields(BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly).Any(field =>
                    field.FieldType == typeof(HidDevice)),
                $"{type.Name} reused legacy HidDevice assumptions.");
        }
    }

    [TestMethod]
    public void CompletionDrivenPumpRearmsWithoutASecondOwner()
    {
        var lease = new FakeLease
        {
            CompleteSynchronously = true,
        };
        var sink = new RejectAfterSink(128);
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), sink, 7, 9,
            out Switch2ProUsbInputTransportOwner owner,
            out Switch2ProUsbTransportCreateFailure createFailure),
            createFailure.Kind.ToString());
        Assert.IsTrue(Switch2ProUsbInputReadPump.TryCreate(owner, 1_000,
            out Switch2ProUsbInputReadPump pump,
            out Switch2ProUsbInputReadPumpFailure pumpFailure),
            pumpFailure.ToString());

        Assert.IsFalse(owner.TryBeginRead(out _,
            out Switch2ProUsbReadBeginFailure fenced));
        Assert.AreEqual(Switch2ProUsbReadBeginFailure.OwnershipRejected,
            fenced);
        Assert.IsFalse(Switch2ProUsbInputReadPump.TryCreate(owner, 1_000,
            out _, out Switch2ProUsbInputReadPumpFailure duplicate));
        Assert.AreEqual(Switch2ProUsbInputReadPumpFailure.OwnerRejected,
            duplicate);

        Assert.IsTrue(pump.TryStart(out var startFailure),
            startFailure.ToString());
        Assert.IsTrue(sink.WaitForRejection(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(pump.TryStopAndDispose(2_000,
            out Switch2ProUsbInputReadPumpFailure disposeFailure),
            $"{disposeFailure}; {pump.LastDisposeFailure}");

        Assert.AreEqual(128L, pump.StartedReadCount);
        Assert.AreEqual(128L, pump.RetiredReadCount);
        Assert.AreEqual(128, lease.BeginCount);
        Assert.AreEqual(128, lease.RetirementCount);
        Assert.AreEqual(Switch2ProUsbInputReadPumpState.Disposed,
            pump.State);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.SinkRejected,
            pump.LastRetirementResult.CompletionDisposition);
        Assert.IsFalse(pump.LastRetirementResult.PermitsRearm);
    }

    [TestMethod]
    public void PumpFailsClosedWhenExactReadRetirementCannotBeProven()
    {
        var lease = new FakeLease
        {
            CompleteSynchronously = true,
            RetirementResult = false,
        };
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), new RecordingSink(), 1, 1,
            out Switch2ProUsbInputTransportOwner owner, out _));
        Assert.IsTrue(Switch2ProUsbInputReadPump.TryCreate(owner, 1,
            out Switch2ProUsbInputReadPump pump, out _));
        Assert.IsTrue(pump.TryStart(out _));

        Assert.IsTrue(SpinWait.SpinUntil(() =>
                pump.State == Switch2ProUsbInputReadPumpState.Stopped,
            TimeSpan.FromSeconds(2)));
        Assert.AreEqual(Switch2ProUsbInputReadPumpFailure.
            ReadRetirementRejected, pump.TerminalFailure);
        Assert.AreEqual(Switch2ProUsbReadRetirementFailure.
            NativeQuiescenceTimedOut, pump.LastRetirementFailure);
        Assert.AreEqual(1L, pump.StartedReadCount);
        Assert.AreEqual(0L, pump.RetiredReadCount);

        Assert.IsTrue(pump.TryStopAndDispose(1_000,
            out Switch2ProUsbInputReadPumpFailure disposed),
            disposed.ToString());
    }

    [TestMethod]
    public void PumpRejectsNonPublishCompletionEvenIfOwnerRemainsOpen()
    {
        var lease = new FakeLease
        {
            CompleteSynchronously = true,
            SynchronousBytesTransferred = 63,
        };
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), new RecordingSink(), 1, 1,
            out Switch2ProUsbInputTransportOwner owner, out _));
        Assert.IsTrue(Switch2ProUsbInputReadPump.TryCreate(owner, 1_000,
            out Switch2ProUsbInputReadPump pump, out _));
        Assert.IsTrue(pump.TryStart(out _));

        Assert.IsTrue(SpinWait.SpinUntil(() =>
                pump.State == Switch2ProUsbInputReadPumpState.Stopped,
            TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, lease.BeginCount);
        Assert.AreEqual(Switch2ProUsbInputReadPumpFailure.
            ReadCompletionRejected, pump.TerminalFailure);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.InvalidReport,
            pump.LastRetirementResult.CompletionDisposition);
        Assert.IsTrue(pump.TryStopAndDispose(1_000, out var disposed),
            disposed.ToString());
    }

    [TestMethod]
    public void PumpStopTimeoutRetainsOwnerAndIsRetryable()
    {
        var lease = new FakeLease
        {
            CompleteSynchronously = true,
            BlockRetirement = true,
        };
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), new RecordingSink(), 1, 1,
            out Switch2ProUsbInputTransportOwner owner, out _));
        Assert.IsTrue(Switch2ProUsbInputReadPump.TryCreate(owner, 1_000,
            out Switch2ProUsbInputReadPump pump, out _));
        Assert.IsTrue(pump.TryStart(out _));
        Assert.IsTrue(lease.RetirementEntered.Wait(TimeSpan.FromSeconds(2)));

        Assert.IsFalse(pump.TryStopAndDispose(0,
            out Switch2ProUsbInputReadPumpFailure timeout));
        Assert.AreEqual(Switch2ProUsbInputReadPumpFailure.WorkerExitTimedOut,
            timeout);
        Assert.AreEqual(0, lease.DisposeCount);

        lease.AllowRetirementReturn.Set();
        Assert.IsTrue(pump.TryStopAndDispose(2_000,
            out Switch2ProUsbInputReadPumpFailure retry), retry.ToString());
        Assert.AreEqual(1, lease.DisposeCount);
    }

    [DataTestMethod]
    [DataRow(Switch2ProUsbNativeReadStatus.DeviceRemoved)]
    [DataRow(Switch2ProUsbNativeReadStatus.Failed)]
    [DataRow(Switch2ProUsbNativeReadStatus.Cancelled)]
    public void IntentionalPumpStopDoesNotRaiseNativeFailureAttention(
        Switch2ProUsbNativeReadStatus status)
    {
        var lease = new FakeLease { RetirementWaitsForCompletion = true };
        Assert.IsTrue(TryCreateOwner(new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), new RecordingSink(), 1, 1,
            out var owner, out _));
        Assert.IsTrue(Switch2ProUsbInputReadPump.TryCreate(owner, 1_000,
            out var pump, out _));
        int attentionCount = 0;
        Assert.IsTrue(pump.TrySetLifecycleAttentionHandler(_ =>
            Interlocked.Increment(ref attentionCount)));
        Assert.IsTrue(pump.TryStart(out _));
        Assert.IsTrue(lease.RetirementEntered.Wait(1_000));
        Assert.IsTrue(pump.RequestStop());
        lease.Complete(lease.CurrentClaim, 0, 100, status);
        Assert.IsTrue(pump.TryStopAndDispose(2_000, out var failure),
            failure.ToString());
        Assert.AreEqual(Switch2ProUsbInputReadPumpFailure.None,
            pump.TerminalFailure);
        Assert.AreEqual(0, Volatile.Read(ref attentionCount));
        Assert.AreEqual(1, lease.BeginCount);
    }

    [TestMethod]
    public void PumpDoesNotRearmBeforeExactAsynchronousCompletion()
    {
        var lease = new FakeLease
        {
            RetirementWaitsForCompletion = true,
            CancelQuiescesWithoutCallback = true,
        };
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), new RecordingSink(), 1, 1,
            out Switch2ProUsbInputTransportOwner owner, out _));
        Assert.IsTrue(Switch2ProUsbInputReadPump.TryCreate(owner, 1_000,
            out Switch2ProUsbInputReadPump pump, out _));
        Assert.IsTrue(pump.TryStart(out _));
        Assert.IsTrue(lease.WaitForBeginCount(1, TimeSpan.FromSeconds(2)));
        Assert.IsFalse(SpinWait.SpinUntil(() => lease.BeginCount > 1,
            TimeSpan.FromMilliseconds(50)),
            "A blocked exact retirement must not permit an early rearm.");

        Switch2ProUsbReadClaim first = lease.CurrentClaim;
        lease.FillPacket(4);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            lease.Complete(first, 64, 100));
        Assert.IsTrue(lease.WaitForBeginCount(2, TimeSpan.FromSeconds(2)));

        Assert.IsTrue(pump.RequestStop());
        Assert.IsTrue(SpinWait.SpinUntil(() =>
                pump.State == Switch2ProUsbInputReadPumpState.Stopped,
            TimeSpan.FromSeconds(2)));
        Assert.AreEqual(2, lease.BeginCount);
        Assert.AreEqual(1, lease.CancelCount);
        Assert.IsFalse(pump.LastRetirementResult.CompletionObserved,
            "Cancellation with native quiescence and no callback is stop-only.");
        Assert.IsFalse(pump.LastRetirementResult.PermitsRearm);
        Assert.IsTrue(pump.TryStopAndDispose(1_000, out var disposed),
            disposed.ToString());
    }

    [TestMethod]
    public void PumpRunsRepeatedAsynchronousCompletionRetirementCycles()
    {
        const int CycleCount = 128;
        var lease = new FakeLease
        {
            RetirementWaitsForCompletion = true,
        };
        var sink = new RejectAfterSink(CycleCount);
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), sink, 1, 1,
            out Switch2ProUsbInputTransportOwner owner, out _));
        Assert.IsTrue(Switch2ProUsbInputReadPump.TryCreate(owner, 1_000,
            out Switch2ProUsbInputReadPump pump, out _));
        Assert.IsTrue(pump.TryStart(out _));

        for (int cycle = 0; cycle < CycleCount; cycle++)
        {
            Assert.IsTrue(lease.WaitForBeginCount(cycle + 1,
                TimeSpan.FromSeconds(2)), $"Read {cycle + 1} did not start.");
            Switch2ProUsbReadClaim claim = lease.CurrentClaim;
            lease.FillPacket((uint)(cycle * 4));
            Switch2ProUsbReadCompletionDisposition expected =
                cycle + 1 == CycleCount ?
                    Switch2ProUsbReadCompletionDisposition.SinkRejected :
                    Switch2ProUsbReadCompletionDisposition.Published;
            Assert.AreEqual(expected, lease.Complete(claim, 64, cycle));
        }

        Assert.IsTrue(SpinWait.SpinUntil(() =>
                pump.State == Switch2ProUsbInputReadPumpState.Stopped,
            TimeSpan.FromSeconds(2)));
        Assert.AreEqual(CycleCount, lease.BeginCount);
        Assert.AreEqual(CycleCount, lease.RetirementCount);
        Assert.AreEqual(CycleCount, pump.StartedReadCount);
        Assert.AreEqual(CycleCount, pump.RetiredReadCount);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.SinkRejected,
            pump.LastRetirementResult.CompletionDisposition);
        Assert.IsTrue(pump.TryStopAndDispose(1_000, out var disposed),
            disposed.ToString());
    }

    [TestMethod]
    public void PumpStopCancelsWhileRetirementIsBlockedAndLateCallbackIsStale()
    {
        var lease = new FakeLease
        {
            RetirementWaitsForCompletion = true,
            CancelQuiescesWithoutCallback = true,
            BlockRetirement = true,
        };
        lease.AllowRetirementReturn.Reset();
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), new RecordingSink(), 1, 1,
            out Switch2ProUsbInputTransportOwner owner, out _));
        Assert.IsTrue(Switch2ProUsbInputReadPump.TryCreate(owner, 1_000,
            out Switch2ProUsbInputReadPump pump, out _));
        Assert.IsTrue(pump.TryStart(out _));
        Assert.IsTrue(lease.RetirementEntered.Wait(TimeSpan.FromSeconds(2)));
        Switch2ProUsbReadClaim cancelled = lease.CurrentClaim;

        Assert.IsFalse(pump.TryStopAndDispose(0, out var timedOut));
        Assert.AreEqual(Switch2ProUsbInputReadPumpFailure.WorkerExitTimedOut,
            timedOut);
        Assert.AreEqual(1, lease.CancelCount,
            "Stop must issue exact cancellation while retirement is waiting.");
        Assert.AreEqual(1, lease.BeginCount);

        lease.AllowRetirementReturn.Set();
        Assert.IsTrue(SpinWait.SpinUntil(() =>
                pump.State == Switch2ProUsbInputReadPumpState.Stopped,
            TimeSpan.FromSeconds(2)));
        Assert.IsFalse(pump.LastRetirementResult.CompletionObserved);
        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.StaleClaim,
            lease.Complete(cancelled, 64, 100),
            "No callback can be admitted after no-callback retirement.");
        Assert.AreEqual(1, lease.BeginCount,
            "A stop-only retirement must never rearm.");
        Assert.IsTrue(pump.TryStopAndDispose(1_000, out var disposed),
            disposed.ToString());
    }

    private static bool TryCreateOwner(FakeDiscovery discovery,
        FakeNativeAdapter native, ISwitch2ProUsbInputSink sink,
        ulong deviceGeneration, ulong transportGeneration,
        out Switch2ProUsbInputTransportOwner owner,
        out Switch2ProUsbTransportCreateFailure failure)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, deviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));
        return Switch2ProUsbInputTransportOwner.TryCreate(discovery, native,
            sink, deviceGeneration, transportGeneration, 10_000_000,
            calibration, out owner, out failure);
    }

    private static void CreateHarness(ulong deviceGeneration,
        ulong transportGeneration,
        out Switch2ProUsbInputTransportOwner owner, out FakeLease lease,
        out RecordingSink sink)
    {
        lease = new FakeLease();
        sink = new RecordingSink();
        Assert.IsTrue(TryCreateOwner(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), sink, deviceGeneration,
            transportGeneration, out owner,
            out Switch2ProUsbTransportCreateFailure failure),
            failure.Kind.ToString());
    }

    private static Switch2PhysicalInputRegistration Admit(
        in Switch2ProUsbCompositeObservation observation)
    {
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out Switch2PhysicalInputRegistration registration,
            out Switch2PhysicalAdmissionFailure failure), failure.ToString());
        return registration;
    }

    private static Switch2ProUsbCompositeObservation CreateObservation(
        Guid? containerId = null, byte matchingInputInterfaceCount = 1,
        byte matchingCommandInterfaceCount = 1)
    {
        Guid rawContainer = containerId ?? ContainerAGuid;
        Assert.IsTrue(Switch2PhysicalContainerIdentity.TryCreate(rawContainer,
            out Switch2PhysicalContainerIdentity container));
        var input = new Switch2UsbHidInterfaceObservation(container, 0, 0,
            Switch2UsbBoundDriver.HidClass, 0x0001, 0x0005, 64, 64, 0);
        var bulkOut = new Switch2UsbPipeObservation(0x02,
            Switch2UsbPipeTransferType.Bulk, 64, 0);
        var bulkIn = new Switch2UsbPipeObservation(0x82,
            Switch2UsbPipeTransferType.Bulk, 64, 0);
        var command = new Switch2UsbCommandInterfaceObservation(container, 1,
            0, Switch2UsbBoundDriver.WinUsb, 2, bulkOut, bulkIn);
        return new Switch2ProUsbCompositeObservation(0x057E, 0x2069, 0x0201,
            container, matchingInputInterfaceCount,
            matchingCommandInterfaceCount, input, command);
    }

    private sealed class FakeDiscovery : ISwitch2ProUsbOsDiscoveryAdapter
    {
        private readonly Switch2ProUsbCompositeObservation observation;

        public FakeDiscovery(in Switch2ProUsbCompositeObservation observation)
        {
            this.observation = observation;
        }

        public int ObservationCount { get; private set; }

        public bool TryObserveComposite(
            out Switch2ProUsbCompositeObservation result)
        {
            ObservationCount++;
            result = observation;
            return true;
        }
    }

    private sealed class FakeNativeAdapter : ISwitch2ProUsbNativeAdapter
    {
        private readonly FakeLease lease;

        public FakeNativeAdapter(FakeLease lease)
        {
            this.lease = lease;
        }

        public int OpenCount { get; private set; }

        public bool OpenResult { get; set; } = true;

        public Switch2PhysicalInputRegistration RequestedRegistration
        {
            get;
            private set;
        }

        public bool TryOpenReadOnlyComposite(
            in Switch2PhysicalInputRegistration registration,
            out ISwitch2ProUsbReadOnlyCompositeLease opened)
        {
            OpenCount++;
            RequestedRegistration = registration;
            lease.AdmittedRegistration = registration;
            opened = lease;
            return OpenResult;
        }
    }

    private sealed class FakeLease : ISwitch2ProUsbReadOnlyCompositeLease
    {
        private readonly object gate = new();
        private readonly ManualResetEventSlim completionQuiescent = new(true);
        private byte[] buffer;
        private ISwitch2ProUsbReadCompletionTarget completionTarget;
        private Switch2ProUsbReadClaim currentClaim;
        private int beginCount;

        public Switch2PhysicalInputRegistration AdmittedRegistration { get; set; }

        public Switch2PhysicalInputRegistration ForcedRegistration { get; set; }

        public Switch2PhysicalInputRegistration Registration =>
            ForcedRegistration.IsValid ? ForcedRegistration :
            AdmittedRegistration;

        public int BeginCount => Volatile.Read(ref beginCount);

        public int CancelCount { get; private set; }

        public int WaitCount { get; private set; }

        public int RetirementCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int RequestedOffset { get; private set; }

        public int RequestedCount { get; private set; }

        public int LastWaitTimeoutMilliseconds { get; private set; }

        public bool WaitResult { get; set; } = true;

        public bool ThrowOnWait { get; set; }

        public bool ThrowOnDispose { get; set; }

        public bool RetirementResult { get; set; } = true;

        public bool CompleteSynchronously { get; set; }

        public int SynchronousBytesTransferred { get; set; } = 64;

        public bool RetirementWaitsForCompletion { get; set; }

        public bool CancelQuiescesWithoutCallback { get; set; }

        public bool ThrowOnBegin { get; set; }

        public bool BlockBegin { get; set; }

        public bool BlockCancel { get; set; }

        public bool BlockRetirement { get; set; }

        public ManualResetEventSlim BeginEntered { get; } = new(false);

        public ManualResetEventSlim AllowBeginReturn { get; } = new(true);

        public ManualResetEventSlim CancelEntered { get; } = new(false);

        public ManualResetEventSlim AllowCancelReturn { get; } = new(true);

        public ManualResetEventSlim RetirementEntered { get; } = new(false);

        public ManualResetEventSlim AllowRetirementReturn { get; } = new(true);

        public uint NextCounter { get; set; }

        public long NextTimestamp { get; set; }

        public Switch2ProUsbReadCompletionDisposition LastDisposition
        {
            get;
            private set;
        }

        public Switch2ProUsbReadClaim CurrentClaim
        {
            get
            {
                lock (gate)
                {
                    return currentClaim;
                }
            }
        }

        public bool TryBeginInputRead(byte[] destination, int offset, int count,
            in Switch2ProUsbReadClaim claim,
            ISwitch2ProUsbReadCompletionTarget target)
        {
            lock (gate)
            {
                buffer = destination;
                completionTarget = target;
                currentClaim = claim;
                if (RetirementWaitsForCompletion)
                {
                    completionQuiescent.Reset();
                }
            }
            Interlocked.Increment(ref beginCount);
            RequestedOffset = offset;
            RequestedCount = count;
            if (BlockBegin)
            {
                BeginEntered.Set();
                AllowBeginReturn.Wait();
            }
            if (ThrowOnBegin)
            {
                throw new InvalidOperationException("Synthetic start failure.");
            }
            if (CompleteSynchronously)
            {
                FillPacket(NextCounter);
                LastDisposition = target.CompleteInputRead(claim,
                    SynchronousBytesTransferred,
                    NextTimestamp, Switch2ProUsbNativeReadStatus.Completed);
                completionQuiescent.Set();
            }
            return true;
        }

        public bool TryCancelInputRead(in Switch2ProUsbReadClaim claim)
        {
            CancelCount++;
            if (BlockCancel)
            {
                CancelEntered.Set();
                AllowCancelReturn.Wait();
            }
            if (CancelQuiescesWithoutCallback)
            {
                completionQuiescent.Set();
            }
            return true;
        }

        public bool TryWaitForInputQuiescence(int timeoutMilliseconds)
        {
            WaitCount++;
            LastWaitTimeoutMilliseconds = timeoutMilliseconds;
            if (ThrowOnWait)
            {
                throw new InvalidOperationException(
                    "Synthetic quiescence failure.");
            }
            return WaitResult;
        }

        public bool TryRetireCompletedInputRead(
            in Switch2ProUsbReadClaim claim, int timeoutMilliseconds)
        {
            RetirementCount++;
            LastWaitTimeoutMilliseconds = timeoutMilliseconds;
            RetirementEntered.Set();
            if (BlockRetirement)
            {
                AllowRetirementReturn.Wait();
            }
            return RetirementResult && (!RetirementWaitsForCompletion ||
                completionQuiescent.Wait(timeoutMilliseconds));
        }

        public void DisposeQuiesced()
        {
            DisposeCount++;
            if (ThrowOnDispose)
            {
                throw new InvalidOperationException(
                    "Synthetic dispose failure.");
            }
        }

        public Switch2ProUsbReadCompletionDisposition Complete(
            in Switch2ProUsbReadClaim claim, int bytesTransferred,
            long timestamp,
            Switch2ProUsbNativeReadStatus status =
                Switch2ProUsbNativeReadStatus.Completed)
        {
            try
            {
                LastDisposition = completionTarget.CompleteInputRead(claim,
                    bytesTransferred, timestamp, status);
                return LastDisposition;
            }
            finally
            {
                completionQuiescent.Set();
            }
        }

        public bool WaitForBeginCount(int count, TimeSpan timeout) =>
            SpinWait.SpinUntil(() => BeginCount >= count, timeout);

        public void FillPacket(uint counter, uint buttons = 0,
            ushort leftX = 1, ushort leftY = 2, ushort rightX = 3,
            ushort rightY = 4)
        {
            Span<byte> packet = buffer.AsSpan(RequestedOffset, RequestedCount);
            packet.Clear();
            packet[0] = (byte)Switch2InputReportKind.Common05;
            Span<byte> body = packet.Slice(1);
            BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
            BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(4), buttons);
            PackStick(body.Slice(0x0A, 3), leftX, leftY);
            PackStick(body.Slice(0x0D, 3), rightX, rightY);
        }
    }

    private sealed class RecordingSink : ISwitch2ProUsbInputSink
    {
        public bool Accept { get; set; } = true;

        public int PublishCount { get; private set; }

        public Switch2CanonicalInputFrame LastFrame { get; private set; }

        public bool TryPublish(in Switch2CanonicalInputFrame frame)
        {
            PublishCount++;
            LastFrame = frame;
            return Accept;
        }
    }

    private sealed class RejectAfterSink : ISwitch2ProUsbInputSink
    {
        private readonly int rejectAt;
        private readonly ManualResetEventSlim rejected = new(false);
        private int publishCount;

        public RejectAfterSink(int rejectAt)
        {
            this.rejectAt = rejectAt;
        }

        public bool TryPublish(in Switch2CanonicalInputFrame frame)
        {
            int count = Interlocked.Increment(ref publishCount);
            if (count < rejectAt)
            {
                return true;
            }
            rejected.Set();
            return false;
        }

        public bool WaitForRejection(TimeSpan timeout) =>
            rejected.Wait(timeout);
    }

    private sealed class BlockingSink : ISwitch2ProUsbInputSink, IDisposable
    {
        private readonly ManualResetEventSlim entered = new(false);
        private readonly ManualResetEventSlim release = new(false);

        public bool TryPublish(in Switch2CanonicalInputFrame frame)
        {
            entered.Set();
            release.Wait();
            return true;
        }

        public bool WaitUntilEntered(TimeSpan timeout) => entered.Wait(timeout);

        public void Release() => release.Set();

        public void Dispose()
        {
            release.Set();
            entered.Dispose();
            release.Dispose();
        }
    }

    private sealed class CrossThreadReentrantSink : ISwitch2ProUsbInputSink
    {
        public Switch2ProUsbInputTransportOwner Owner { get; set; }

        public bool ReentryCompleted { get; private set; }

        public bool TryPublish(in Switch2CanonicalInputFrame frame)
        {
            Task<bool> reentry = Task.Run(Owner.RequestStop);
            ReentryCompleted = reentry.Wait(TimeSpan.FromSeconds(2)) &&
                reentry.Result;
            return ReentryCompleted;
        }
    }

    private static void PackStick(Span<byte> destination, ushort x, ushort y)
    {
        destination[0] = (byte)x;
        destination[1] = (byte)((x >> 8) | (y << 4));
        destination[2] = (byte)(y >> 4);
    }
}
