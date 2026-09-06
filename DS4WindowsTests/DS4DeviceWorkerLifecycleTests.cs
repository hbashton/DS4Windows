using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public sealed class DS4DeviceWorkerLifecycleTests
{
    [TestMethod]
    public void AuditedSubtypePolicyOptsInOnlyBaseDs4AndDs3()
    {
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid,
            DS4DeviceWorkerLifecycleSupportPolicy.Classify(
                typeof(DS4Device), hasHidInterface: true));
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid,
            DS4DeviceWorkerLifecycleSupportPolicy.Classify(
                typeof(DS3Device), hasHidInterface: true));
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleSupport.
                UnsupportedDualSenseCompositeWorkers,
            DS4DeviceWorkerLifecycleSupportPolicy.Classify(
                typeof(DualSenseDevice), hasHidInterface: true));
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleSupport.
                UnsupportedSwitchOperationalSetup,
            DS4DeviceWorkerLifecycleSupportPolicy.Classify(
                typeof(SwitchProDevice), hasHidInterface: true));
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleSupport.
                UnsupportedSwitchOperationalSetup,
            DS4DeviceWorkerLifecycleSupportPolicy.Classify(
                typeof(JoyConDevice), hasHidInterface: true));
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleSupport.
                UnsupportedNoHidExternalOwner,
            DS4DeviceWorkerLifecycleSupportPolicy.Classify(
                typeof(Switch2RuntimeInputDevice), hasHidInterface: false));
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleSupport.UnsupportedUnknownSubtype,
            DS4DeviceWorkerLifecycleSupportPolicy.Classify(
                typeof(TestDevice), hasHidInterface: true));
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleSupport.Invalid,
            DS4DeviceWorkerLifecycleSupportPolicy.Classify(
                deviceType: null, hasHidInterface: true));
    }

    [TestMethod]
    public void UnsupportedSubtypeFailsBeforeLifecycleCall()
    {
        using var device = new TestDevice();

        Assert.IsFalse(device.TryStartWorkerLifecycle(out var lease,
            out var result));
        Assert.IsFalse(lease.IsValid);
        Assert.AreEqual(DS4DeviceWorkerLifecycleOutcome.CleanRejected,
            result.Outcome);
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.UnsupportedDevice,
            result.FailureKind);
        Assert.AreEqual(0, device.StartCalls);

        Assert.IsFalse(device.TryStopWorkerLifecycle(default, 100,
            out result));
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.UnsupportedDevice,
            result.FailureKind);
        Assert.AreEqual(0, device.StopCoreCalls);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.Created,
            device.WorkerLifecycleState);
        Assert.IsFalse(device.CurrentWorkerLifecycleLease.IsValid);
    }

    [TestMethod]
    public void EveryUnsupportedClassificationHasZeroBoundarySideEffects()
    {
        DS4DeviceWorkerLifecycleSupport[] unsupported =
        {
            DS4DeviceWorkerLifecycleSupport.Invalid,
            DS4DeviceWorkerLifecycleSupport.
                UnsupportedDualSenseCompositeWorkers,
            DS4DeviceWorkerLifecycleSupport.
                UnsupportedSwitchOperationalSetup,
            DS4DeviceWorkerLifecycleSupport.
                UnsupportedNoHidExternalOwner,
            DS4DeviceWorkerLifecycleSupport.UnsupportedUnknownSubtype,
        };

        foreach (DS4DeviceWorkerLifecycleSupport support in unsupported)
        {
            using var device = new TestDevice();
            Assert.IsFalse(device.TryStartBoundary(support, out var lease,
                out var result), support.ToString());
            Assert.IsFalse(lease.IsValid, support.ToString());
            Assert.AreEqual(
                DS4DeviceWorkerLifecycleFailureKind.UnsupportedDevice,
                result.FailureKind, support.ToString());
            Assert.AreEqual(0, device.StartCalls, support.ToString());
            Assert.AreEqual(DS4DeviceWorkerLifecycleState.Created,
                device.TestWorkerLifecycleState, support.ToString());

            Assert.IsFalse(device.TryStopBoundary(support, default, 100,
                out result), support.ToString());
            Assert.AreEqual(
                DS4DeviceWorkerLifecycleFailureKind.UnsupportedDevice,
                result.FailureKind, support.ToString());
            Assert.AreEqual(0, device.StopCoreCalls, support.ToString());
            Assert.IsFalse(device.TestCurrentWorkerLifecycleLease.IsValid,
                support.ToString());
        }
    }

    [TestMethod]
    public void CleanStartRejectionPublishesNoCleanupLease()
    {
        using var device = new TestDevice { StartMode = StartMode.Reject };

        Assert.IsFalse(device.TryStartBoundary(out var lease,
            out var result));

        Assert.IsFalse(lease.IsValid);
        Assert.AreEqual(DS4DeviceWorkerLifecycleOutcome.CleanRejected,
            result.Outcome);
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.StartRejected,
            result.FailureKind);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.Created,
            device.TestWorkerLifecycleState);
    }

    [TestMethod]
    public void StartExceptionBeforeWorkerRetainsExactCleanupLease()
    {
        using var device = new TestDevice
        {
            StartMode = StartMode.ThrowBeforeWorkers,
        };

        Assert.IsFalse(device.TryStartBoundary(out var lease,
            out var result));
        Assert.IsTrue(lease.IsValid);
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleFailureKind.StartDependencyThrew,
            result.FailureKind);
        Assert.AreEqual(DS4DeviceWorkerLifecycleOutcome.OutcomeUncertain,
            result.Outcome);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.StartUncertain,
            device.TestWorkerLifecycleState);

        Assert.IsTrue(device.TryStopBoundary(lease, 100, out result),
            $"{result.Outcome}/{result.FailureKind}");
        Assert.AreEqual(1, device.StartCalls);
        Assert.AreEqual(1, device.StopCoreCalls);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.Stopped,
            device.TestWorkerLifecycleState);
    }

    [TestMethod]
    public void InputStartThenExceptionIsPartialAndCleanupIsProved()
    {
        using var device = new TestDevice
        {
            StartMode = StartMode.InputThenThrow,
        };

        Assert.IsFalse(device.TryStartBoundary(out var lease,
            out var result));
        Assert.IsTrue(lease.IsValid);
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.PartialStart,
            result.FailureKind);
        Assert.AreEqual(lease.Generation,
            device.TestCurrentWorkerLifecycleLease.Generation);

        Assert.IsTrue(device.TryStopBoundary(lease, 2_000, out result),
            $"{result.Outcome}/{result.FailureKind}");
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.Stopped,
            device.TestWorkerLifecycleState);
    }

    [TestMethod]
    public void OutputStartThenInputFailureRetainsExactCleanupOwnership()
    {
        using var device = new TestDevice
        {
            StartMode = StartMode.PartialOutputThenThrow,
        };

        Assert.IsFalse(device.TryStartBoundary(out var lease,
            out var result));
        Assert.IsTrue(lease.IsValid);
        Assert.AreSame(device, lease.Device);
        Assert.AreEqual(DS4DeviceWorkerLifecycleOutcome.OutcomeUncertain,
            result.Outcome);
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.PartialStart,
            result.FailureKind);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.StartUncertain,
            device.TestWorkerLifecycleState);
        Assert.IsTrue(device.OutputEntered.Wait(2_000));

        Assert.IsTrue(device.TryStopBoundary(lease, 2_000,
            out result), $"{result.Outcome}/{result.FailureKind}");
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.Stopped,
            device.TestWorkerLifecycleState);
        Assert.IsFalse(device.OutputWorkerAlive);
        Assert.AreEqual(1, device.StartCalls);
        Assert.AreEqual(1, device.StopCoreCalls);
    }

    [TestMethod]
    public void StopTimeoutRetainsLeaseAndExactRetryCanProveCompletion()
    {
        using var device = new TestDevice
        {
            StartMode = StartMode.PartialOutputThenThrow,
            IgnoreOutputInterrupt = true,
        };
        Assert.IsFalse(device.TryStartBoundary(out var lease, out _));
        Assert.IsTrue(device.OutputEntered.Wait(2_000));

        Assert.IsFalse(device.TryStopBoundary(lease, 20,
            out var result));
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.StopTimedOut,
            result.FailureKind);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.StopUncertain,
            device.TestWorkerLifecycleState);
        Assert.AreEqual(lease, device.TestCurrentWorkerLifecycleLease);

        device.ReleaseOutput.Set();
        Assert.IsTrue(device.TryStopBoundary(lease, 2_000,
            out result), $"{result.Outcome}/{result.FailureKind}");
        Assert.IsFalse(device.OutputWorkerAlive);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.Stopped,
            device.TestWorkerLifecycleState);
    }

    [TestMethod]
    public void InvalidStopDeadlineCannotEnterLifecycleOrInvokeDependency()
    {
        using var device = CreateSuccessfullyStartedDevice(out var lease);

        Assert.IsFalse(device.TryStopBoundary(lease, -1,
            out var result));
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.InvalidArgument,
            result.FailureKind);
        Assert.IsFalse(device.TryStopBoundary(lease,
            InputControllerRegistration.MaximumStopTimeoutMilliseconds + 1,
            out result));
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.InvalidArgument,
            result.FailureKind);
        Assert.AreEqual(0, device.StopCoreCalls);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.Started,
            device.TestWorkerLifecycleState);
        Assert.AreEqual(lease, device.TestCurrentWorkerLifecycleLease);

        Assert.IsTrue(device.TryStopBoundary(lease, 2_000, out result),
            $"{result.Outcome}/{result.FailureKind}");
    }

    [TestMethod]
    public void StopExceptionIsUncertainAndDoesNotReleaseOwnership()
    {
        using var device = CreateSuccessfullyStartedDevice(out var lease);
        device.StopMode = StopMode.Throw;

        Assert.IsFalse(device.TryStopBoundary(lease, 100,
            out var result));
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleFailureKind.StopDependencyThrew,
            result.FailureKind);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.StopUncertain,
            device.TestWorkerLifecycleState);
        Assert.AreEqual(lease, device.TestCurrentWorkerLifecycleLease);

        device.StopMode = StopMode.Normal;
        device.PrepareAbort();
        Assert.IsTrue(device.TryStopBoundary(lease, 2_000,
            out result), $"{result.Outcome}/{result.FailureKind}");
    }

    [TestMethod]
    public void MalformedStopResultIsUncertainAndExactLeaseCanRetry()
    {
        using var device = CreateSuccessfullyStartedDevice(out var lease);
        device.StopMode = StopMode.Malformed;

        Assert.IsFalse(device.TryStopBoundary(lease, 100,
            out var result));
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleFailureKind.StopDependencyThrew,
            result.FailureKind);
        Assert.AreEqual(DS4DeviceWorkerLifecycleOutcome.OutcomeUncertain,
            result.Outcome);
        Assert.AreEqual(lease, device.TestCurrentWorkerLifecycleLease);

        device.StopMode = StopMode.Normal;
        Assert.IsTrue(device.TryStopBoundary(lease, 2_000, out result),
            $"{result.Outcome}/{result.FailureKind}");
    }

    [TestMethod]
    public void StartAndStopAreExactlyIdempotent()
    {
        using var device = CreateSuccessfullyStartedDevice(out var lease);

        Assert.IsTrue(device.TryStartBoundary(out var sameLease,
            out var result), $"{result.Outcome}/{result.FailureKind}");
        Assert.AreEqual(lease, sameLease);
        Assert.AreEqual(1, device.StartCalls);

        device.PrepareAbort();
        Assert.IsTrue(device.TryStopBoundary(lease, 2_000,
            out result), $"{result.Outcome}/{result.FailureKind}");
        Assert.IsTrue(device.TryStopBoundary(lease, 2_000,
            out result), $"{result.Outcome}/{result.FailureKind}");
        Assert.AreEqual(1, device.StopCoreCalls);
    }

    [TestMethod]
    public void ReentrantStartPoisonsOuterTransitionAndCanBeCleaned()
    {
        using var device = new TestDevice
        {
            StartMode = StartMode.ReentrantThenInput,
        };

        Assert.IsFalse(device.TryStartBoundary(out var lease,
            out var result));
        Assert.IsFalse(device.NestedStartSucceeded);
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.ReentrantCall,
            device.NestedStartResult.FailureKind);
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.ReentrantCall,
            result.FailureKind);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.StartUncertain,
            device.TestWorkerLifecycleState);

        device.PrepareAbort();
        Assert.IsTrue(device.TryStopBoundary(lease, 2_000,
            out result), $"{result.Outcome}/{result.FailureKind}");
    }

    [TestMethod]
    public void ReentrantStopPoisonsOuterTransitionAndRetainsLease()
    {
        using var device = CreateSuccessfullyStartedDevice(out var lease);
        device.StopMode = StopMode.Reentrant;
        device.ReentrantStopLease = lease;

        Assert.IsFalse(device.TryStopBoundary(lease, 100,
            out var result));
        Assert.IsFalse(device.NestedStopSucceeded);
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.ReentrantCall,
            device.NestedStopResult.FailureKind);
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.ReentrantCall,
            result.FailureKind);
        Assert.AreEqual(lease, device.TestCurrentWorkerLifecycleLease);

        device.StopMode = StopMode.Normal;
        device.PrepareAbort();
        Assert.IsTrue(device.TryStopBoundary(lease, 2_000,
            out result), $"{result.Outcome}/{result.FailureKind}");
    }

    [TestMethod]
    public void ConcurrentStartCannotOvertakeGenerationOwner()
    {
        using var device = new TestDevice
        {
            StartMode = StartMode.BlockThenInput,
        };
        DS4DeviceWorkerLifecycleLease outerLease = default;
        DS4DeviceWorkerLifecycleResult outerResult = default;
        Task<bool> outer = Task.Run(() => device.TryStartBoundary(
            out outerLease, out outerResult));
        Assert.IsTrue(device.OperationEntered.Wait(2_000));

        Assert.IsFalse(device.TryStartBoundary(out var concurrentLease,
            out var concurrentResult));
        Assert.IsFalse(concurrentLease.IsValid,
            "A clean Busy rejection must not disclose cleanup authority.");
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.Busy,
            concurrentResult.FailureKind);

        device.ReleaseOperation.Set();
        Assert.IsFalse(outer.Result);
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleFailureKind.ConcurrentInterference,
            outerResult.FailureKind);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.StartUncertain,
            device.TestWorkerLifecycleState);

        Assert.IsTrue(device.TryStopBoundary(outerLease, 2_000,
            out var cleanup), $"{cleanup.Outcome}/{cleanup.FailureKind}");
    }

    [TestMethod]
    public void ConcurrentStopCannotReleaseLeaseBehindOwner()
    {
        using var device = CreateSuccessfullyStartedDevice(out var lease);
        device.StopMode = StopMode.Block;
        DS4DeviceWorkerLifecycleResult outerResult = default;
        Task<bool> outer = Task.Run(() => device.TryStopBoundary(lease,
            2_000, out outerResult));
        Assert.IsTrue(device.OperationEntered.Wait(2_000));

        Assert.IsFalse(device.TryStopBoundary(lease, 100,
            out var concurrentResult));
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.Busy,
            concurrentResult.FailureKind);

        device.ReleaseOperation.Set();
        Assert.IsFalse(outer.Result);
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleFailureKind.ConcurrentInterference,
            outerResult.FailureKind);
        Assert.AreEqual(lease, device.TestCurrentWorkerLifecycleLease);

        device.StopMode = StopMode.Normal;
        Assert.IsTrue(device.TryStopBoundary(lease, 2_000,
            out var retry), $"{retry.Outcome}/{retry.FailureKind}");
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.Stopped,
            device.TestWorkerLifecycleState);
    }

    [TestMethod]
    public void ExistingPublicWorkerCannotBeRetroactivelyClaimed()
    {
        using var device = new TestDevice { StartMode = StartMode.Input };
        device.StartUpdate();

        Assert.IsFalse(device.TryStartBoundary(out var lease,
            out var result));
        Assert.IsFalse(lease.IsValid);
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleFailureKind.UntrackedExistingWorker,
            result.FailureKind);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.QuarantinedUntracked,
            device.TestWorkerLifecycleState);
        Assert.AreEqual(1, device.StartCalls);
    }

    [TestMethod]
    public void ForeignStartDuringTypedCallbackIsUncertainNotClaimed()
    {
        using var device = new TestDevice
        {
            StartMode = StartMode.ForeignOutput,
        };

        Assert.IsFalse(device.TryStartBoundary(out var lease,
            out var result));
        Assert.IsTrue(lease.IsValid);
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleFailureKind.UntrackedExistingWorker,
            result.FailureKind);
        Assert.AreEqual(DS4DeviceWorkerLifecycleState.StartUncertain,
            device.TestWorkerLifecycleState);
        Assert.IsTrue(device.OutputEntered.Wait(2_000));

        Assert.IsTrue(device.TryStopBoundary(lease, 2_000,
            out result), $"{result.Outcome}/{result.FailureKind}");
        Assert.IsFalse(device.OutputWorkerAlive);
    }

    [TestMethod]
    public void WrongDeviceLeaseCannotInvokeStopCore()
    {
        using var owner = CreateSuccessfullyStartedDevice(out var lease);
        using var other = new TestDevice();

        Assert.IsFalse(other.TryStopBoundary(lease, 100,
            out var result));
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.StaleCredential,
            result.FailureKind);
        Assert.AreEqual(0, other.StopCoreCalls);

        owner.PrepareAbort();
        Assert.IsTrue(owner.TryStopBoundary(lease, 2_000,
            out result), $"{result.Outcome}/{result.FailureKind}");
    }

    [TestMethod]
    public void LifecycleCallbacksRunOutsideBoundaryGate()
    {
        using var device = CreateSuccessfullyStartedDevice(out var lease);
        Assert.IsTrue(device.ExternalGateChecks >= 1);
        device.PrepareAbort();

        Assert.IsTrue(device.TryStopBoundary(lease, 2_000,
            out var result), $"{result.Outcome}/{result.FailureKind}");
        Assert.IsTrue(device.ExternalGateChecks >= 2);
    }

    [TestMethod]
    public void ResultShapeRejectsFalseCertainty()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            DS4DeviceWorkerLifecycleResult.Reject(
                DS4DeviceWorkerLifecycleOperation.Stop,
                DS4DeviceWorkerLifecycleFailureKind.StopTimedOut));
        Assert.ThrowsException<ArgumentException>(() =>
            DS4DeviceWorkerLifecycleResult.Uncertain(
                DS4DeviceWorkerLifecycleOperation.Start,
                DS4DeviceWorkerLifecycleFailureKind.StartRejected));
        Assert.ThrowsException<ArgumentException>(() =>
            DS4DeviceWorkerLifecycleResult.Uncertain(
                DS4DeviceWorkerLifecycleOperation.Stop,
                DS4DeviceWorkerLifecycleFailureKind.PartialStart));
        Assert.ThrowsException<ArgumentException>(() =>
            DS4DeviceWorkerLifecycleResult.Uncertain(
                DS4DeviceWorkerLifecycleOperation.Start,
                DS4DeviceWorkerLifecycleFailureKind.StopTimedOut));
        Assert.ThrowsException<ArgumentException>(() =>
            DS4DeviceWorkerLifecycleResult.Reject(
                DS4DeviceWorkerLifecycleOperation.Stop,
                DS4DeviceWorkerLifecycleFailureKind.GenerationExhausted));
        Assert.ThrowsException<ArgumentException>(() =>
            DS4DeviceWorkerLifecycleResult.Reject(
                DS4DeviceWorkerLifecycleOperation.Start,
                DS4DeviceWorkerLifecycleFailureKind.StaleCredential));
        Assert.ThrowsException<ArgumentException>(() =>
            DS4DeviceWorkerLifecycleResult.Reject(
                DS4DeviceWorkerLifecycleOperation.Start,
                DS4DeviceWorkerLifecycleFailureKind.ConcurrentInterference));
        Assert.IsTrue(DS4DeviceWorkerLifecycleResult.Uncertain(
            DS4DeviceWorkerLifecycleOperation.Start,
            DS4DeviceWorkerLifecycleFailureKind.ConcurrentInterference).
            IsValid);
        Assert.IsTrue(DS4DeviceWorkerLifecycleResult.Uncertain(
            DS4DeviceWorkerLifecycleOperation.Stop,
            DS4DeviceWorkerLifecycleFailureKind.ConcurrentInterference).
            IsValid);
        Assert.IsFalse(default(DS4DeviceWorkerLifecycleResult).IsValid);
    }

    private static TestDevice CreateSuccessfullyStartedDevice(
        out DS4DeviceWorkerLifecycleLease lease)
    {
        var device = new TestDevice { StartMode = StartMode.Input };
        Assert.IsTrue(device.TryStartBoundary(out lease,
            out var result), $"{result.Outcome}/{result.FailureKind}");
        return device;
    }

    private enum StartMode : byte
    {
        Input,
        Reject,
        ThrowBeforeWorkers,
        InputThenThrow,
        PartialOutputThenThrow,
        ReentrantThenInput,
        ForeignOutput,
        BlockThenInput,
    }

    private enum StopMode : byte
    {
        Normal,
        Throw,
        Reentrant,
        Malformed,
        Block,
    }

    private sealed class TestDevice : DS4Device, IDisposable
    {
        private readonly DS4DeviceWorkerLifecycleBoundary testLifecycle =
            new();

        internal TestDevice() : base(CreateHidDevice(), "Worker test")
        {
        }

        internal StartMode StartMode { get; set; }
        internal StopMode StopMode { get; set; }
        internal bool IgnoreOutputInterrupt { get; set; }
        internal ManualResetEventSlim OutputEntered { get; } = new();
        internal ManualResetEventSlim ReleaseOutput { get; } = new();
        internal ManualResetEventSlim OperationEntered { get; } = new();
        internal ManualResetEventSlim ReleaseOperation { get; } = new();
        internal int StartCalls { get; private set; }
        internal int StopCoreCalls { get; private set; }
        internal int ExternalGateChecks { get; private set; }
        internal bool NestedStartSucceeded { get; private set; }
        internal DS4DeviceWorkerLifecycleResult NestedStartResult
        {
            get;
            private set;
        }
        internal bool NestedStopSucceeded { get; private set; }
        internal DS4DeviceWorkerLifecycleResult NestedStopResult
        {
            get;
            private set;
        }
        internal DS4DeviceWorkerLifecycleLease ReentrantStopLease { get; set; }
        internal bool OutputWorkerAlive => ds4Output?.IsAlive == true;
        internal DS4DeviceWorkerLifecycleState TestWorkerLifecycleState =>
            testLifecycle.State;
        internal DS4DeviceWorkerLifecycleLease
            TestCurrentWorkerLifecycleLease => testLifecycle.CurrentLease;

        internal bool TryStartBoundary(
            out DS4DeviceWorkerLifecycleLease lease,
            out DS4DeviceWorkerLifecycleResult result) =>
            TryStartBoundary(
                DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid,
                out lease, out result);

        internal bool TryStartBoundary(
            DS4DeviceWorkerLifecycleSupport support,
            out DS4DeviceWorkerLifecycleLease lease,
            out DS4DeviceWorkerLifecycleResult result) =>
            testLifecycle.TryStart(this, support, StartUpdate, out lease,
                out result);

        internal bool TryStopBoundary(
            in DS4DeviceWorkerLifecycleLease lease, int timeoutMilliseconds,
            out DS4DeviceWorkerLifecycleResult result) =>
            TryStopBoundary(
                DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid, lease,
                timeoutMilliseconds, out result);

        internal bool TryStopBoundary(
            DS4DeviceWorkerLifecycleSupport support,
            in DS4DeviceWorkerLifecycleLease lease, int timeoutMilliseconds,
            out DS4DeviceWorkerLifecycleResult result) =>
            testLifecycle.TryStop(this, support, lease, timeoutMilliseconds,
                TryStopUpdateBoundedCore, out result);

        public override void StartUpdate()
        {
            StartCalls++;
            Assert.IsFalse(Monitor.IsEntered(testLifecycle.Gate));
            ExternalGateChecks++;
            ResetWorkerStartCommitWitnesses();
            switch (StartMode)
            {
                case StartMode.Reject:
                    return;
                case StartMode.ThrowBeforeWorkers:
                    throw new InvalidOperationException(
                        "Injected pre-worker start failure");
                case StartMode.InputThenThrow:
                    StartInputWorker();
                    throw new InvalidOperationException(
                        "Injected post-input start failure");
                case StartMode.PartialOutputThenThrow:
                    ds4Output = new Thread(OutputLoop)
                    {
                        IsBackground = true,
                    };
                    ds4Output.Start();
                    MarkOutputWorkerStartCommitted();
                    testLifecycle.WitnessWorkerStartCommit(this,
                        inputWorker: false);
                    throw new InvalidOperationException("Injected input start");
                case StartMode.ReentrantThenInput:
                    NestedStartSucceeded = TryStartBoundary(out _,
                        out var nestedStart);
                    NestedStartResult = nestedStart;
                    StartInputWorker();
                    return;
                case StartMode.ForeignOutput:
                    var foreignStart = new Thread(() =>
                    {
                        ds4Output = new Thread(OutputLoop)
                        {
                            IsBackground = true,
                        };
                        ds4Output.Start();
                        MarkOutputWorkerStartCommitted();
                        testLifecycle.WitnessWorkerStartCommit(this,
                            inputWorker: false);
                    })
                    {
                        IsBackground = true,
                    };
                    foreignStart.Start();
                    foreignStart.Join();
                    return;
                case StartMode.BlockThenInput:
                    OperationEntered.Set();
                    if (!ReleaseOperation.Wait(2_000))
                    {
                        throw new TimeoutException(
                            "The test did not release start.");
                    }
                    StartInputWorker();
                    return;
                default:
                    StartInputWorker();
                    return;
            }
        }

        internal override DS4DeviceWorkerLifecycleResult
            TryStopUpdateBoundedCore(int timeoutMilliseconds)
        {
            StopCoreCalls++;
            Assert.IsFalse(Monitor.IsEntered(testLifecycle.Gate));
            ExternalGateChecks++;
            if (StopMode == StopMode.Throw)
            {
                throw new InvalidOperationException("Injected stop failure");
            }
            if (StopMode == StopMode.Reentrant)
            {
                NestedStopSucceeded = TryStopBoundary(
                    ReentrantStopLease, timeoutMilliseconds,
                    out var nestedStop);
                NestedStopResult = nestedStop;
                return DS4DeviceWorkerLifecycleResult.Success(
                    DS4DeviceWorkerLifecycleOperation.Stop);
            }
            if (StopMode == StopMode.Malformed)
            {
                return default;
            }
            if (StopMode == StopMode.Block)
            {
                OperationEntered.Set();
                if (!ReleaseOperation.Wait(2_000))
                {
                    throw new TimeoutException(
                        "The test did not release stop.");
                }
            }
            return base.TryStopUpdateBoundedCore(timeoutMilliseconds);
        }

        public void Dispose()
        {
            ReleaseOutput.Set();
            ReleaseOperation.Set();
            if (ds4Output?.IsAlive == true)
            {
                ds4Output.Join(2_000);
            }
            if (ds4Input?.IsAlive == true)
            {
                ds4Input.Join(2_000);
            }
            OutputEntered.Dispose();
            ReleaseOutput.Dispose();
            OperationEntered.Dispose();
            ReleaseOperation.Dispose();
        }

        private void StartInputWorker()
        {
            ds4Input = new Thread(static () => { })
            {
                IsBackground = true,
            };
            ds4Input.Start();
            MarkInputWorkerStartCommitted();
            testLifecycle.WitnessWorkerStartCommit(this,
                inputWorker: true);
        }

        private void OutputLoop()
        {
            OutputEntered.Set();
            if (IgnoreOutputInterrupt)
            {
                while (!ReleaseOutput.IsSet)
                {
                    Thread.SpinWait(1_000);
                }
                return;
            }
            try
            {
                ReleaseOutput.Wait();
            }
            catch (ThreadInterruptedException)
            {
            }
        }

        private static HidDevice CreateHidDevice() =>
            (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
    }
}
