using DS4Windows.Switch2;

namespace DS4WinWPF.DS4WindowsTests;

[TestClass]
public sealed class Switch2ProUsbStartupTransactionTests
{
    private static readonly Guid ContainerA =
        new("3B7D0B61-6B2A-48AF-BE5F-48B8EB5B96B9");
    private static readonly Guid ContainerB =
        new("B16DCA8A-746A-4D11-A74B-844A9E89DAF1");

    [TestMethod]
    public void ExactFiveStepTransactionUsesClosedRequestsAndRequiresRateMeasurement()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(ContainerA, 7,
            11);
        using var lease = new FakeLease(lifetime);
        Switch2ProUsbStartupTransaction transaction = Create(lease, lifetime);

        Assert.AreEqual(Switch2ProUsbStartupInputRateStatus.Unavailable,
            transaction.InputRateStatus);
        Assert.AreEqual(Switch2ProUsbStartupStep.EnableUsbHidReports,
            transaction.NextStep);

        for (int index = 0;
                index < Switch2ProUsbStartupTransaction.RequiredStepCount;
                index++)
        {
            Assert.IsTrue(transaction.TryAdvance(31, 47,
                out Switch2ProUsbStartupAdvanceResult result),
                result.CommandFailure.ToString());
            Assert.IsTrue(result.StepCompleted);
            Assert.AreEqual(Switch2ProUsbStartupRetirementFailure.None,
                result.RetirementFailure);
        }

        Assert.AreEqual(Switch2ProUsbStartupTransactionState.Completed,
            transaction.State);
        Assert.AreEqual(Switch2ProUsbStartupStep.Invalid,
            transaction.NextStep);
        Assert.AreEqual(
            Switch2ProUsbStartupInputRateStatus.RequiresMeasurement,
            transaction.InputRateStatus,
            "Startup completion must never be promoted to a 500 Hz claim.");
        Assert.AreEqual(5, lease.ExecutionCount);
        Assert.AreEqual(0, lease.RetirementCount);
        CollectionAssert.AreEqual(new[]
        {
            Switch2ProUsbStartupStep.EnableUsbHidReports,
            Switch2ProUsbStartupStep.SetPlayerLed,
            Switch2ProUsbStartupStep.SetFeatureMask,
            Switch2ProUsbStartupStep.EnableFeatures,
            Switch2ProUsbStartupStep.SelectCommonInputReport,
        }, lease.Steps.Take(
            Switch2ProUsbStartupTransaction.RequiredStepCount).ToArray());
        CollectionAssert.AreEqual(Convert.FromHexString(
            "039100030004000001000000"), lease.Requests[0]);
        CollectionAssert.AreEqual(Convert.FromHexString(
            "0991000100000000"), lease.Requests[1]);
        CollectionAssert.AreEqual(Convert.FromHexString(
            "0C9100020004000027000000"), lease.Requests[2]);
        CollectionAssert.AreEqual(Convert.FromHexString(
            "0C9100040004000027000000"), lease.Requests[3]);
        CollectionAssert.AreEqual(Convert.FromHexString(
            "0391000A0004000005000000"), lease.Requests[4]);
        Assert.AreEqual(7UL, lease.Claims[0].DeviceGeneration);
        Assert.AreEqual(11UL, lease.Claims[0].TransportGeneration);
        Assert.AreEqual(31, lease.LastCommandTimeoutMilliseconds);

        Assert.IsTrue(transaction.TryAdvance(0, 0, out var idempotent));
        Assert.IsTrue(idempotent.StepCompleted);
        Assert.AreEqual(5, lease.ExecutionCount,
            "Completed state must be idempotent without another write.");
    }

    [TestMethod]
    public void ProvenNotConsumedRetriesSameClaimAndBytesOnlyOnExplicitCall()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(ContainerA, 1,
            2);
        using var lease = new FakeLease(lifetime)
        {
            CommandMode = FakeCommandMode.ProvenNotConsumed,
        };
        Switch2ProUsbStartupTransaction transaction = Create(lease, lifetime);

        Assert.IsFalse(transaction.TryAdvance(20, 20,
            out Switch2ProUsbStartupAdvanceResult first));
        Assert.AreEqual(Switch2ProUsbStartupCommandFailure.ProvenNotConsumed,
            first.CommandFailure);
        Assert.IsTrue(first.ExactRetryPermitted);
        Assert.AreEqual(Switch2ProUsbStartupTransactionState.RetryableCommand,
            transaction.State);
        Assert.AreEqual(1, lease.ExecutionCount);
        Assert.AreEqual(0, lease.RetirementCount);

        lease.CommandMode = FakeCommandMode.Exact;
        Assert.IsTrue(transaction.TryAdvance(20, 20, out var retry),
            retry.CommandFailure.ToString());
        Assert.AreEqual(2, lease.ExecutionCount);
        Assert.AreEqual(lease.Claims[0], lease.Claims[1]);
        Assert.AreEqual(1UL, lease.Claims[0].Sequence);
        CollectionAssert.AreEqual(lease.Requests[0], lease.Requests[1]);
        Assert.AreEqual(Switch2ProUsbStartupStep.SetPlayerLed,
            transaction.NextStep);
        Assert.AreEqual(0, lease.RetirementCount);
    }

    [TestMethod]
    public void MalformedWrongStepAndWrongProofCompletionsRetireFailClosed()
    {
        AssertUnsafeCompletion(FakeCommandMode.Default,
            Switch2ProUsbStartupCommandFailure.MalformedCompletion);
        AssertUnsafeCompletion(FakeCommandMode.WrongStep,
            Switch2ProUsbStartupCommandFailure.WrongStep);
        AssertUnsafeCompletion(FakeCommandMode.WrongProof,
            Switch2ProUsbStartupCommandFailure.WrongResponseProof);
    }

    [TestMethod]
    public void ThrowTimeoutAndPossibleConsumptionRetireWithoutCommandRetry()
    {
        AssertUnsafeCompletion(FakeCommandMode.Throw,
            Switch2ProUsbStartupCommandFailure.DependencyThrew);
        AssertUnsafeCompletion(FakeCommandMode.TimedOut,
            Switch2ProUsbStartupCommandFailure.CommandTimedOut);
        AssertUnsafeCompletion(FakeCommandMode.PossiblyConsumed,
            Switch2ProUsbStartupCommandFailure.PossiblyConsumed);
    }

    [TestMethod]
    public void CrossOwnerAndStaleClaimsCannotAdvanceAnotherStep()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(ContainerA, 4,
            8);
        using var firstLease = new FakeLease(lifetime)
        {
            CommandMode = FakeCommandMode.ProvenNotConsumed,
        };
        Switch2ProUsbStartupTransaction first = Create(firstLease, lifetime);
        Assert.IsFalse(first.TryAdvance(10, 10, out _));
        Switch2ProUsbStartupCommandClaim foreign = firstLease.Claims[0];

        using var secondLease = new FakeLease(lifetime)
        {
            UseForcedCommandCompletion = true,
            ForcedCommandCompletion =
                Switch2ProUsbStartupCommandCompletion.ExactResponse(foreign,
                    Switch2ProUsbStartupStep.EnableUsbHidReports,
                    Switch2ProUsbStartupResponseProofKind.
                        InitializationResponseValidatedByCodec),
        };
        Switch2ProUsbStartupTransaction second = Create(secondLease, lifetime);
        Assert.IsFalse(second.TryAdvance(10, 10,
            out Switch2ProUsbStartupAdvanceResult crossOwner));
        Assert.AreEqual(Switch2ProUsbStartupCommandFailure.WrongClaim,
            crossOwner.CommandFailure);
        Assert.AreEqual(Switch2ProUsbStartupTransactionState.Retired,
            second.State);

        firstLease.CommandMode = FakeCommandMode.Exact;
        Assert.IsTrue(first.TryAdvance(10, 10, out _));
        firstLease.UseForcedCommandCompletion = true;
        firstLease.ForcedCommandCompletion =
            Switch2ProUsbStartupCommandCompletion.ExactResponse(foreign,
                Switch2ProUsbStartupStep.SetFeatureMask,
                Switch2ProUsbStartupResponseProofKind.
                    FeatureResponseValidatedByCodec);
        Assert.IsFalse(first.TryAdvance(10, 10,
            out Switch2ProUsbStartupAdvanceResult stale));
        Assert.AreEqual(Switch2ProUsbStartupCommandFailure.WrongClaim,
            stale.CommandFailure);
        Assert.AreEqual(Switch2ProUsbStartupTransactionState.Retired,
            first.State);
    }

    [TestMethod]
    public void ProvenRetirementMissRetainsAndRetriesSameExactCredential()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(ContainerA, 3,
            9);
        using var lease = new FakeLease(lifetime)
        {
            CommandMode = FakeCommandMode.TimedOut,
            RetirementMode = FakeRetirementMode.ProvenNotReleased,
        };
        Switch2ProUsbStartupTransaction transaction = Create(lease, lifetime);

        Assert.IsFalse(transaction.TryAdvance(15, 17,
            out Switch2ProUsbStartupAdvanceResult failed));
        Assert.AreEqual(Switch2ProUsbStartupCommandFailure.CommandTimedOut,
            failed.CommandFailure);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementFailure.ProvenNotReleased,
            failed.RetirementFailure);
        Assert.AreEqual(
            Switch2ProUsbStartupTransactionState.RetirementRetained,
            transaction.State);
        Switch2ProUsbStartupRetirementClaim retained =
            lease.RetirementClaims[0];

        Assert.IsFalse(transaction.TryAdvance(1, 1, out var blocked));
        Assert.AreEqual(
            Switch2ProUsbStartupCommandFailure.RetirementRequired,
            blocked.CommandFailure);
        Assert.AreEqual(1, lease.ExecutionCount);

        lease.RetirementMode = FakeRetirementMode.Released;
        Assert.IsTrue(transaction.TryRetire(19, out var released),
            released.ToString());
        Assert.AreEqual(retained, lease.RetirementClaims[1]);
        Assert.AreEqual(2, lease.RetirementCount);
        Assert.AreEqual(19, lease.LastRetirementTimeoutMilliseconds);
        Assert.AreEqual(Switch2ProUsbStartupTransactionState.Retired,
            transaction.State);
    }

    [TestMethod]
    public void UncertainRetirementVariantsQuarantineAndNeverDoubleRetire()
    {
        FakeRetirementMode[] modes =
        {
            FakeRetirementMode.Default,
            FakeRetirementMode.Throw,
            FakeRetirementMode.TimedOut,
            FakeRetirementMode.PossiblyReleased,
            FakeRetirementMode.WrongReason,
            FakeRetirementMode.ForeignClaim,
        };

        foreach (FakeRetirementMode mode in modes)
        {
            Switch2PhysicalInputLifetime lifetime = CreateLifetime(ContainerA,
                5, (ulong)mode + 20);
            using var lease = new FakeLease(lifetime)
            {
                RetirementMode = mode,
            };
            Switch2ProUsbStartupTransaction transaction = Create(lease,
                lifetime);

            Assert.IsFalse(transaction.TryRetire(23,
                out Switch2ProUsbStartupRetirementFailure failure));
            Assert.AreNotEqual(
                Switch2ProUsbStartupRetirementFailure.ProvenNotReleased,
                failure);
            Assert.AreEqual(Switch2ProUsbStartupTransactionState.Quarantined,
                transaction.State, mode.ToString());
            Assert.AreEqual(1, lease.RetirementCount);
            Assert.IsFalse(transaction.TryRetire(23, out var quarantined));
            Assert.AreEqual(
                Switch2ProUsbStartupRetirementFailure.LifetimeQuarantined,
                quarantined);
            Assert.AreEqual(1, lease.RetirementCount,
                "An outcome-uncertain release must never be repeated.");
        }
    }

    [TestMethod]
    public void OneOperationInFlightAndInlineReentryNeverObserveHeldGate()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(ContainerA, 6,
            12);
        using var lease = new FakeLease(lifetime)
        {
            BlockCommand = true,
        };
        lease.AllowCommandReturn.Reset();
        Switch2ProUsbStartupTransaction transaction = Create(lease, lifetime);

        Task<(bool Success, Switch2ProUsbStartupAdvanceResult Result)> worker =
            Task.Run(() =>
            {
                bool success = transaction.TryAdvance(100, 100,
                    out Switch2ProUsbStartupAdvanceResult result);
                return (success, result);
            });
        Assert.IsTrue(lease.CommandEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(Switch2ProUsbStartupTransactionState.CommandInFlight,
            transaction.State);
        Assert.IsFalse(transaction.TryAdvance(1, 1, out var concurrent));
        Assert.AreEqual(
            Switch2ProUsbStartupCommandFailure.OperationAlreadyInProgress,
            concurrent.CommandFailure);
        Assert.IsFalse(transaction.TryRetire(1, out var retireConcurrent));
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementFailure.OperationAlreadyInProgress,
            retireConcurrent);
        lease.AllowCommandReturn.Set();
        Assert.IsTrue(worker.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(worker.Result.Success,
            worker.Result.Result.CommandFailure.ToString());

        bool commandStateRead = false;
        bool commandReentryRejected = false;
        bool retireReentryRejected = false;
        lease.BlockCommand = false;
        lease.OnCommand = () =>
        {
            commandStateRead = transaction.State ==
                Switch2ProUsbStartupTransactionState.CommandInFlight;
            commandReentryRejected = !transaction.TryAdvance(1, 1,
                out Switch2ProUsbStartupAdvanceResult nested) &&
                nested.CommandFailure ==
                    Switch2ProUsbStartupCommandFailure.
                        OperationAlreadyInProgress;
            retireReentryRejected = !transaction.TryRetire(1,
                out Switch2ProUsbStartupRetirementFailure nestedRetire) &&
                nestedRetire == Switch2ProUsbStartupRetirementFailure.
                    OperationAlreadyInProgress;
        };
        Assert.IsTrue(transaction.TryAdvance(10, 10, out var next),
            next.CommandFailure.ToString());
        Assert.IsTrue(commandStateRead);
        Assert.IsTrue(commandReentryRejected);
        Assert.IsTrue(retireReentryRejected);

        bool retirementStateRead = false;
        bool retirementReentryRejected = false;
        lease.OnRetirement = () =>
        {
            retirementStateRead = transaction.State ==
                Switch2ProUsbStartupTransactionState.RetirementInFlight;
            retirementReentryRejected = !transaction.TryRetire(1,
                out Switch2ProUsbStartupRetirementFailure nested) &&
                nested == Switch2ProUsbStartupRetirementFailure.
                    OperationAlreadyInProgress;
        };
        Assert.IsTrue(transaction.TryRetire(10, out var retired),
            retired.ToString());
        Assert.IsTrue(retirementStateRead);
        Assert.IsTrue(retirementReentryRejected);
    }

    [TestMethod]
    public void InvalidCreationAndTimeoutsPerformNoLeaseOperation()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(ContainerA, 1,
            1);
        using var lease = new FakeLease(lifetime);

        Assert.IsFalse(Switch2ProUsbStartupTransaction.TryCreate(null,
            lifetime, out _, out var missing));
        Assert.AreEqual(Switch2ProUsbStartupCreateFailure.MissingLease,
            missing);
        Assert.IsFalse(Switch2ProUsbStartupTransaction.TryCreate(lease,
            default, out _, out var invalid));
        Assert.AreEqual(Switch2ProUsbStartupCreateFailure.InvalidLifetime,
            invalid);

        using var mismatchLease = new FakeLease(CreateLifetime(ContainerB, 1,
            1));
        Assert.IsFalse(Switch2ProUsbStartupTransaction.TryCreate(mismatchLease,
            lifetime, out _, out var mismatch));
        Assert.AreEqual(
            Switch2ProUsbStartupCreateFailure.LeaseLifetimeMismatch,
            mismatch);
        lease.ThrowOnLifetime = true;
        Assert.IsFalse(Switch2ProUsbStartupTransaction.TryCreate(lease,
            lifetime, out _, out var rejected));
        Assert.AreEqual(
            Switch2ProUsbStartupCreateFailure.LeaseLifetimeRejected,
            rejected);
        lease.ThrowOnLifetime = false;

        Switch2ProUsbStartupTransaction transaction = Create(lease, lifetime);
        Assert.IsFalse(transaction.TryAdvance(-1, 1, out var negative));
        Assert.AreEqual(Switch2ProUsbStartupCommandFailure.InvalidTimeout,
            negative.CommandFailure);
        Assert.IsFalse(transaction.TryAdvance(1,
            Switch2ProUsbStartupTransaction.
                MaximumOperationTimeoutMilliseconds + 1, out var tooLarge));
        Assert.AreEqual(Switch2ProUsbStartupCommandFailure.InvalidTimeout,
            tooLarge.CommandFailure);
        Assert.IsFalse(transaction.TryRetire(-1, out var retireInvalid));
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementFailure.InvalidTimeout,
            retireInvalid);
        Assert.AreEqual(0, lease.ExecutionCount);
        Assert.AreEqual(0, lease.RetirementCount);
    }

    [TestMethod]
    public void ExplicitRetireIsBoundedAndIdempotentAfterExactRelease()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(ContainerA, 2,
            4);
        using var lease = new FakeLease(lifetime);
        Switch2ProUsbStartupTransaction transaction = Create(lease, lifetime);

        Assert.IsTrue(transaction.TryRetire(37, out var failure),
            failure.ToString());
        Assert.AreEqual(Switch2ProUsbStartupTransactionState.Retired,
            transaction.State);
        Assert.AreEqual(1, lease.RetirementCount);
        Assert.AreEqual(37, lease.LastRetirementTimeoutMilliseconds);
        Assert.IsTrue(transaction.TryRetire(0, out failure));
        Assert.AreEqual(1, lease.RetirementCount);
        Assert.IsFalse(transaction.TryAdvance(1, 1, out var closed));
        Assert.AreEqual(Switch2ProUsbStartupCommandFailure.LifecycleClosed,
            closed.CommandFailure);
    }

    [TestMethod]
    public void CompletedSteadyStateAllocatesZeroAcrossTwentyThousandCalls()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(ContainerA, 10,
            20);
        using var lease = new FakeLease(lifetime);
        Switch2ProUsbStartupTransaction transaction = Create(lease, lifetime);
        for (int index = 0;
                index < Switch2ProUsbStartupTransaction.RequiredStepCount;
                index++)
        {
            Assert.IsTrue(transaction.TryAdvance(0, 0, out _));
        }

        bool valid = true;
        for (int warmup = 0; warmup < 1_000; warmup++)
        {
            valid &= transaction.State ==
                Switch2ProUsbStartupTransactionState.Completed;
            valid &= transaction.NextStep == Switch2ProUsbStartupStep.Invalid;
            valid &= transaction.InputRateStatus ==
                Switch2ProUsbStartupInputRateStatus.RequiresMeasurement;
            valid &= transaction.TryAdvance(0, 0, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            valid &= transaction.State ==
                Switch2ProUsbStartupTransactionState.Completed;
            valid &= transaction.NextStep == Switch2ProUsbStartupStep.Invalid;
            valid &= transaction.InputRateStatus ==
                Switch2ProUsbStartupInputRateStatus.RequiresMeasurement;
            valid &= transaction.TryAdvance(0, 0, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(5, lease.ExecutionCount);
    }

    private static void AssertUnsafeCompletion(FakeCommandMode mode,
        Switch2ProUsbStartupCommandFailure expected)
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(ContainerA, 1,
            (ulong)mode + 40);
        using var lease = new FakeLease(lifetime)
        {
            CommandMode = mode,
        };
        Switch2ProUsbStartupTransaction transaction = Create(lease, lifetime);

        Assert.IsFalse(transaction.TryAdvance(13, 29,
            out Switch2ProUsbStartupAdvanceResult result));
        Assert.AreEqual(expected, result.CommandFailure);
        Assert.AreEqual(Switch2ProUsbStartupRetirementFailure.None,
            result.RetirementFailure);
        Assert.AreEqual(Switch2ProUsbStartupTransactionState.Retired,
            transaction.State);
        Assert.IsFalse(result.ExactRetryPermitted);
        Assert.AreEqual(1, lease.ExecutionCount);
        Assert.AreEqual(1, lease.RetirementCount);
        Assert.AreEqual(29, lease.LastRetirementTimeoutMilliseconds);
        Assert.AreEqual(
            Switch2ProUsbStartupRetirementReason.CommandOutcomeUncertain,
            lease.RetirementClaims[0].Reason);
        Assert.AreEqual(Switch2ProUsbStartupInputRateStatus.Unavailable,
            transaction.InputRateStatus);
    }

    private static Switch2ProUsbStartupTransaction Create(FakeLease lease,
        in Switch2PhysicalInputLifetime lifetime)
    {
        Assert.IsTrue(Switch2ProUsbStartupTransaction.TryCreate(lease,
            lifetime, out Switch2ProUsbStartupTransaction transaction,
            out Switch2ProUsbStartupCreateFailure failure),
            failure.ToString());
        return transaction;
    }

    private static Switch2PhysicalInputLifetime CreateLifetime(Guid containerId,
        ulong deviceGeneration, ulong transportGeneration)
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
            deviceGeneration, transportGeneration, 10_000_000,
            out Switch2PhysicalInputLifetime lifetime));
        return lifetime;
    }

    private enum FakeCommandMode : byte
    {
        Exact = 0,
        ProvenNotConsumed,
        Default,
        WrongStep,
        WrongProof,
        Throw,
        TimedOut,
        PossiblyConsumed,
    }

    private enum FakeRetirementMode : byte
    {
        Released = 0,
        ProvenNotReleased,
        Default,
        Throw,
        TimedOut,
        PossiblyReleased,
        WrongReason,
        ForeignClaim,
    }

    private sealed class FakeLease : ISwitch2ProUsbStartupCommandLease,
        IDisposable
    {
        private readonly Switch2PhysicalInputLifetime lifetime;
        private readonly Switch2ProUsbStartupCommandClaim[] claims = new
            Switch2ProUsbStartupCommandClaim[16];
        private readonly Switch2ProUsbStartupRetirementClaim[] retirementClaims
            = new Switch2ProUsbStartupRetirementClaim[16];
        private readonly Switch2ProUsbStartupStep[] steps = new
            Switch2ProUsbStartupStep[16];
        private readonly byte[][] requests = new byte[16][];
        private int executionCount;
        private int retirementCount;

        public FakeLease(in Switch2PhysicalInputLifetime lifetime)
        {
            this.lifetime = lifetime;
        }

        public Switch2PhysicalInputLifetime Lifetime => ThrowOnLifetime ?
            throw new InvalidOperationException("Synthetic lifetime failure.") :
            lifetime;

        public FakeCommandMode CommandMode { get; set; } =
            FakeCommandMode.Exact;

        public FakeRetirementMode RetirementMode { get; set; } =
            FakeRetirementMode.Released;

        public bool ThrowOnLifetime { get; set; }

        public bool UseForcedCommandCompletion { get; set; }

        public Switch2ProUsbStartupCommandCompletion ForcedCommandCompletion
        {
            get;
            set;
        }

        public bool BlockCommand { get; set; }

        public ManualResetEventSlim CommandEntered { get; } = new(false);

        public ManualResetEventSlim AllowCommandReturn { get; } = new(true);

        public Action OnCommand { get; set; }

        public Action OnRetirement { get; set; }

        public int ExecutionCount => Volatile.Read(ref executionCount);

        public int RetirementCount => Volatile.Read(ref retirementCount);

        public int LastCommandTimeoutMilliseconds { get; private set; }

        public int LastRetirementTimeoutMilliseconds { get; private set; }

        public IReadOnlyList<Switch2ProUsbStartupCommandClaim> Claims => claims;

        public IReadOnlyList<Switch2ProUsbStartupRetirementClaim>
            RetirementClaims => retirementClaims;

        public IReadOnlyList<Switch2ProUsbStartupStep> Steps => steps;

        public IReadOnlyList<byte[]> Requests => requests;

        public Switch2ProUsbStartupCommandCompletion Execute(
            in Switch2ProUsbStartupCommandClaim claim,
            ReadOnlySpan<byte> exactRequest, int timeoutMilliseconds)
        {
            int index = Interlocked.Increment(ref executionCount) - 1;
            claims[index] = claim;
            steps[index] = claim.Step;
            requests[index] = exactRequest.ToArray();
            LastCommandTimeoutMilliseconds = timeoutMilliseconds;
            CommandEntered.Set();
            if (BlockCommand)
            {
                AllowCommandReturn.Wait();
            }
            OnCommand?.Invoke();
            if (UseForcedCommandCompletion)
            {
                return ForcedCommandCompletion;
            }

            Switch2ProUsbStartupStep wrongStep = claim.Step ==
                    Switch2ProUsbStartupStep.SelectCommonInputReport ?
                Switch2ProUsbStartupStep.EnableUsbHidReports :
                claim.Step + 1;
            Switch2ProUsbStartupResponseProofKind exactProof = claim.Step
                switch
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
            return CommandMode switch
            {
                FakeCommandMode.Exact =>
                    Switch2ProUsbStartupCommandCompletion.ExactResponse(claim,
                        claim.Step, exactProof),
                FakeCommandMode.ProvenNotConsumed =>
                    Switch2ProUsbStartupCommandCompletion.ProvenNotConsumed(
                        claim, claim.Step),
                FakeCommandMode.Default => default,
                FakeCommandMode.WrongStep =>
                    Switch2ProUsbStartupCommandCompletion.ExactResponse(claim,
                        wrongStep, exactProof),
                FakeCommandMode.WrongProof =>
                    Switch2ProUsbStartupCommandCompletion.ExactResponse(claim,
                        claim.Step,
                        Switch2ProUsbStartupResponseProofKind.
                            FeatureResponseValidatedByCodec),
                FakeCommandMode.Throw => throw new InvalidOperationException(
                    "Synthetic command failure."),
                FakeCommandMode.TimedOut =>
                    Switch2ProUsbStartupCommandCompletion.TimedOut(claim,
                        claim.Step),
                FakeCommandMode.PossiblyConsumed =>
                    Switch2ProUsbStartupCommandCompletion.PossiblyConsumed(
                        claim, claim.Step),
                _ => default,
            };
        }

        public Switch2ProUsbStartupRetirementCompletion Retire(
            in Switch2ProUsbStartupRetirementClaim claim,
            int timeoutMilliseconds)
        {
            int index = Interlocked.Increment(ref retirementCount) - 1;
            retirementClaims[index] = claim;
            LastRetirementTimeoutMilliseconds = timeoutMilliseconds;
            OnRetirement?.Invoke();

            Switch2ProUsbStartupRetirementReason wrongReason = claim.Reason ==
                    Switch2ProUsbStartupRetirementReason.Explicit ?
                Switch2ProUsbStartupRetirementReason.CommandOutcomeUncertain :
                Switch2ProUsbStartupRetirementReason.Explicit;
            return RetirementMode switch
            {
                FakeRetirementMode.Released =>
                    Switch2ProUsbStartupRetirementCompletion.Released(claim,
                        claim.Reason),
                FakeRetirementMode.ProvenNotReleased =>
                    Switch2ProUsbStartupRetirementCompletion.
                        ProvenNotReleased(claim, claim.Reason),
                FakeRetirementMode.Default => default,
                FakeRetirementMode.Throw => throw new InvalidOperationException(
                    "Synthetic retirement failure."),
                FakeRetirementMode.TimedOut =>
                    Switch2ProUsbStartupRetirementCompletion.TimedOut(claim,
                        claim.Reason),
                FakeRetirementMode.PossiblyReleased =>
                    Switch2ProUsbStartupRetirementCompletion.PossiblyReleased(
                        claim, claim.Reason),
                FakeRetirementMode.WrongReason =>
                    Switch2ProUsbStartupRetirementCompletion.Released(claim,
                        wrongReason),
                FakeRetirementMode.ForeignClaim =>
                    Switch2ProUsbStartupRetirementCompletion.Released(default,
                        claim.Reason),
                _ => default,
            };
        }

        public void Dispose()
        {
            AllowCommandReturn.Set();
            CommandEntered.Dispose();
            AllowCommandReturn.Dispose();
        }
    }
}
