using System.Runtime.CompilerServices;
using System.Threading;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public sealed class LegacyHidInputControllerRegistrationOwnerTests
{
    [TestMethod]
    public void ExactLegacyRegistrationCreationPerformsNoHardwareLifecycleCall()
    {
        var host = new FakeHost();
        TestLegacyDevice device = CreateLegacyDevice("01:02:03:04:05:06");

        Assert.IsTrue(TryCreate(device, 101, hasPersistentIdentity: true, host,
            out var owner, out var failure, out var registrationFailure),
            $"{failure}/{registrationFailure}");

        InputControllerRegistration registration = owner.Registration;
        Assert.AreSame(device, registration.Device);
        Assert.AreSame(owner, registration.Owner);
        Assert.AreEqual(101UL, registration.Generation);
        Assert.AreEqual(InputControllerOwnershipKind.LegacyHid,
            registration.OwnershipKind);
        Assert.IsTrue(registration.HasHidInterface);
        Assert.IsTrue(registration.HasPersistentIdentity);
        Assert.IsTrue(registration.IsOwnerAuthenticated);
        Assert.AreEqual(0, device.StartCalls);
        Assert.AreEqual(0, device.StopCalls);
        Assert.AreEqual(0, host.StopCalls);
        Assert.AreEqual(0, host.RemoveCalls);
    }

    [TestMethod]
    public void CreationRejectsNonHidInvalidGenerationAndRemovingLifetime()
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(201, 301,
            Switch2Transport.Usb, out var runtime, out _));
        var host = new FakeHost();
        Assert.IsFalse(TryCreate(runtime, 201, false, host, out _,
            out var failure, out _));
        Assert.AreEqual(
            LegacyHidInputControllerCreateFailure.MissingHidInterface,
            failure);

        TestLegacyDevice device = CreateLegacyDevice(
            "01:02:03:04:05:07");
        Assert.IsFalse(TryCreate(device, 0, false, host, out _, out failure,
            out _));
        Assert.AreEqual(LegacyHidInputControllerCreateFailure.InvalidGeneration,
            failure);

        device.IsRemoving = true;
        Assert.IsFalse(TryCreate(device, 202, false, host, out _, out failure,
            out _));
        Assert.AreEqual(LegacyHidInputControllerCreateFailure.InvalidDeviceState,
            failure);
    }

    [TestMethod]
    public void PersistentIdentityRequiresConcreteNonBlankSerial()
    {
        TestLegacyDevice blank = CreateLegacyDevice(DS4Device.BLANK_SERIAL);
        var host = new FakeHost();

        Assert.IsFalse(TryCreate(blank, 203, true, host, out _,
            out var failure, out _));
        Assert.AreEqual(
            LegacyHidInputControllerCreateFailure.PersistentIdentityNotProven,
            failure);
        Assert.IsTrue(TryCreate(blank, 203, false, host, out var owner,
            out failure, out var registrationFailure),
            $"{failure}/{registrationFailure}");
        Assert.IsFalse(owner.Registration.HasPersistentIdentity);
    }

    [TestMethod]
    public void HostAuthenticationRejectionAndThrowAreTypedCreationFailures()
    {
        TestLegacyDevice device = CreateLegacyDevice(
            "01:02:03:04:05:08");
        var rejected = new FakeHost { AuthenticationAccepted = false };
        Assert.IsFalse(TryCreate(device, 204, false, rejected, out _,
            out var failure, out var registrationFailure));
        Assert.AreEqual(
            LegacyHidInputControllerCreateFailure.HostAuthenticationRejected,
            failure);
        Assert.AreEqual(
            InputControllerRegistrationFailure.OwnerAuthenticationFailed,
            registrationFailure);

        var throwing = new FakeHost { ThrowAuthentication = true };
        Assert.IsFalse(TryCreate(device, 205, false, throwing, out _,
            out failure, out registrationFailure));
        Assert.AreEqual(LegacyHidInputControllerCreateFailure.HostThrew,
            failure);
        Assert.AreEqual(InputControllerRegistrationFailure.OwnerThrew,
            registrationFailure);
    }

    [TestMethod]
    public void MatchingFieldsCannotForgeHostIssuedLifetimeLease()
    {
        TestLegacyDevice device = CreateLegacyDevice(
            "01:02:03:04:05:18");
        var host = new FakeHost();
        LegacyHidInputControllerLifetimeLease authentic = host.Issue(device,
            221, hasPersistentIdentity: false);
        var forged = new LegacyHidInputControllerLifetimeLease(new object(),
            device, 221, hasPersistentIdentity: false);

        Assert.IsFalse(LegacyHidInputControllerRegistrationOwner.TryCreate(
            forged, host, out _, out var failure,
            out var registrationFailure));
        Assert.AreEqual(
            LegacyHidInputControllerCreateFailure.HostAuthenticationRejected,
            failure);
        Assert.AreEqual(
            InputControllerRegistrationFailure.OwnerAuthenticationFailed,
            registrationFailure);
        Assert.IsTrue(LegacyHidInputControllerRegistrationOwner.TryCreate(
            authentic, host, out var owner, out failure,
            out registrationFailure), $"{failure}/{registrationFailure}");
        Assert.AreEqual(221UL, owner.Registration.Generation);
    }

    [TestMethod]
    public void AuthenticationRequiresExactReferenceAndGeneration()
    {
        TestLegacyDevice device = CreateLegacyDevice(
            "01:02:03:04:05:09");
        var host = new FakeHost();
        Assert.IsTrue(TryCreate(device, 206, false, host, out var owner,
            out _, out _));
        TestLegacyDevice foreign = CreateLegacyDevice(
            "01:02:03:04:05:0A");

        Assert.IsTrue(owner.Authenticates(device, 206));
        Assert.IsFalse(owner.Authenticates(device, 207));
        Assert.IsFalse(owner.Authenticates(foreign, 206));
        Assert.IsTrue(owner.LifetimeLease.IsValid);
        Assert.AreSame(device, owner.LifetimeLease.Device);
        Assert.AreEqual(206UL, owner.LifetimeLease.Generation);
    }

    [TestMethod]
    public void SuccessfulStopAndRemoveDelegateExactlyOnceInOrder()
    {
        var order = new List<string>();
        var host = new FakeHost(order);
        TestLegacyDevice device = CreateLegacyDevice(
            "01:02:03:04:05:0B");
        Assert.IsTrue(TryCreate(device, 208, false, host, out var owner,
            out _, out _));

        Assert.IsTrue(owner.Registration.TryStopAndQuiesce(1_000,
            out var stopFailure), stopFailure.ToString());
        Assert.AreEqual(LegacyHidInputControllerOwnerState.Quiesced,
            owner.State);
        Assert.IsTrue(owner.Registration.TryRemove(out var removeFailure),
            removeFailure.ToString());
        Assert.AreEqual(LegacyHidInputControllerOwnerState.Removed,
            owner.State);

        CollectionAssert.AreEqual(new[] { "stop", "remove" }, order);
        Assert.AreEqual(1, host.StopCalls);
        Assert.AreEqual(1, host.RemoveCalls);
        Assert.AreEqual(0, device.StartCalls);
        Assert.AreEqual(0, device.StopCalls,
            "Only the future existing-lifecycle host may call StopUpdate.");
        Assert.IsFalse(owner.Registration.IsOwnerAuthenticated);
    }

    [TestMethod]
    public void RemoveBeforeQuiescenceNeverCallsLegacyHost()
    {
        var host = new FakeHost();
        TestLegacyDevice device = CreateLegacyDevice(
            "01:02:03:04:05:0C");
        Assert.IsTrue(TryCreate(device, 209, false, host, out var owner,
            out _, out _));

        Assert.IsFalse(owner.Registration.TryRemove(out var failure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.RemoveRejected,
            failure);
        Assert.AreEqual(0, host.RemoveCalls);
        Assert.AreEqual(LegacyHidInputControllerOwnerState.Created,
            owner.State);
    }

    [TestMethod]
    public void ProvenStopRejectionAllowsExactRetry()
    {
        var host = new FakeHost();
        host.StopResults.Enqueue(
            LegacyHidInputControllerLifecycleResult.Reject(
                LegacyHidInputControllerLifecycleOperation.StopAndQuiesce,
                LegacyHidInputControllerLifecycleFailureKind.StopRejected));
        host.StopResults.Enqueue(
            LegacyHidInputControllerLifecycleResult.Success(
                LegacyHidInputControllerLifecycleOperation.StopAndQuiesce));
        TestLegacyDevice device = CreateLegacyDevice(
            "01:02:03:04:05:0D");
        Assert.IsTrue(TryCreate(device, 210, false, host, out var owner,
            out _, out _));

        Assert.IsFalse(owner.Registration.TryStopAndQuiesce(100,
            out var failure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.StopRejected,
            failure);
        Assert.AreEqual(LegacyHidInputControllerOwnerState.Created,
            owner.State);
        Assert.IsTrue(owner.Registration.TryStopAndQuiesce(100, out failure),
            failure.ToString());
        Assert.AreEqual(2, host.StopCalls);
    }

    [TestMethod]
    public void CredentialOrGenerationRejectionQuarantinesExactLifetime()
    {
        foreach (LegacyHidInputControllerLifecycleFailureKind failureKind in
            new[]
            {
                LegacyHidInputControllerLifecycleFailureKind.InvalidCredential,
                LegacyHidInputControllerLifecycleFailureKind.StaleGeneration,
                LegacyHidInputControllerLifecycleFailureKind.InvalidState,
            })
        {
            var host = new FakeHost();
            host.StopResults.Enqueue(
                LegacyHidInputControllerLifecycleResult.Reject(
                    LegacyHidInputControllerLifecycleOperation.StopAndQuiesce,
                    failureKind));
            TestLegacyDevice device = CreateLegacyDevice(
                $"01:02:03:04:06:{(byte)failureKind:X2}");
            Assert.IsTrue(TryCreate(device, 300 + (ulong)failureKind, false,
                host, out var owner, out _, out _));

            Assert.IsFalse(owner.Registration.TryStopAndQuiesce(100,
                out var failure));
            Assert.IsTrue(failure is
                InputControllerOwnerOperationFailure.OwnerAuthenticationFailed
                or InputControllerOwnerOperationFailure.StopRejected);
            Assert.IsTrue(owner.RequiresQuarantine);
            Assert.AreEqual(
                LegacyHidInputControllerOwnerState.Quarantined, owner.State);
            Assert.AreEqual(1, host.StopCalls);
        }
    }

    [TestMethod]
    public void HostAuthenticationLossBeforeLifecycleCallQuarantines()
    {
        var host = new FakeHost();
        TestLegacyDevice device = CreateLegacyDevice(
            "01:02:03:04:05:1A");
        Assert.IsTrue(TryCreate(device, 223, false, host, out var owner,
            out _, out _));
        host.AuthenticationResults.Enqueue(true);
        host.AuthenticationResults.Enqueue(false);

        Assert.IsFalse(owner.Registration.TryStopAndQuiesce(100,
            out var failure));
        Assert.AreEqual(
            InputControllerOwnerOperationFailure.OwnerAuthenticationFailed,
            failure);
        Assert.AreEqual(0, host.StopCalls);
        Assert.IsTrue(owner.RequiresQuarantine);
    }

    [TestMethod]
    public void UncertainStopQuarantinesAndCannotBeRetried()
    {
        var host = new FakeHost();
        host.StopResults.Enqueue(
            LegacyHidInputControllerLifecycleResult.Uncertain(
                LegacyHidInputControllerLifecycleOperation.StopAndQuiesce,
                LegacyHidInputControllerLifecycleFailureKind.StopTimedOut));
        TestLegacyDevice device = CreateLegacyDevice(
            "01:02:03:04:05:0E");
        Assert.IsTrue(TryCreate(device, 211, false, host, out var owner,
            out _, out _));

        Assert.IsFalse(owner.Registration.TryStopAndQuiesce(100,
            out var failure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.StopRejected,
            failure);
        Assert.IsTrue(owner.RequiresQuarantine);
        Assert.AreEqual(LegacyHidInputControllerOwnerState.Quarantined,
            owner.State);
        Assert.IsFalse(owner.Registration.TryStopAndQuiesce(100, out failure));
        Assert.AreEqual(
            InputControllerOwnerOperationFailure.OwnerAuthenticationFailed,
            failure);
        Assert.AreEqual(1, host.StopCalls);
    }

    [TestMethod]
    public void WrongOperationAndThrownStopBecomeUncertain()
    {
        TestLegacyDevice wrongDevice = CreateLegacyDevice(
            "01:02:03:04:05:0F");
        var wrongHost = new FakeHost();
        wrongHost.StopResults.Enqueue(
            LegacyHidInputControllerLifecycleResult.Success(
                LegacyHidInputControllerLifecycleOperation.Remove));
        Assert.IsTrue(TryCreate(wrongDevice, 212, false, wrongHost,
            out var wrongOwner, out _, out _));
        Assert.IsFalse(wrongOwner.Registration.TryStopAndQuiesce(100,
            out var failure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.OwnerThrew,
            failure);
        Assert.IsTrue(wrongOwner.RequiresQuarantine);

        TestLegacyDevice throwingDevice = CreateLegacyDevice(
            "01:02:03:04:05:10");
        var throwingHost = new FakeHost { ThrowStop = true };
        Assert.IsTrue(TryCreate(throwingDevice, 213, false, throwingHost,
            out var throwingOwner, out _, out _));
        Assert.IsFalse(throwingOwner.Registration.TryStopAndQuiesce(100,
            out failure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.OwnerThrew,
            failure);
        Assert.IsTrue(throwingOwner.RequiresQuarantine);

        TestLegacyDevice malformedDevice = CreateLegacyDevice(
            "01:02:03:04:05:19");
        var malformedHost = new FakeHost();
        malformedHost.StopResults.Enqueue(default);
        Assert.IsTrue(TryCreate(malformedDevice, 222, false, malformedHost,
            out var malformedOwner, out _, out _));
        Assert.IsFalse(malformedOwner.Registration.TryStopAndQuiesce(100,
            out failure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.OwnerThrew,
            failure);
        Assert.IsTrue(malformedOwner.RequiresQuarantine);
    }

    [TestMethod]
    public void ProvenRemoveRejectionAllowsRetryButUncertaintyQuarantines()
    {
        TestLegacyDevice retryDevice = CreateLegacyDevice(
            "01:02:03:04:05:11");
        var retryHost = new FakeHost();
        retryHost.RemoveResults.Enqueue(
            LegacyHidInputControllerLifecycleResult.Reject(
                LegacyHidInputControllerLifecycleOperation.Remove,
                LegacyHidInputControllerLifecycleFailureKind.RemoveRejected));
        retryHost.RemoveResults.Enqueue(
            LegacyHidInputControllerLifecycleResult.Success(
                LegacyHidInputControllerLifecycleOperation.Remove));
        Assert.IsTrue(TryCreate(retryDevice, 214, false, retryHost,
            out var retryOwner, out _, out _));
        Assert.IsTrue(retryOwner.Registration.TryStopAndQuiesce(100, out _));
        Assert.IsFalse(retryOwner.Registration.TryRemove(out var failure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.RemoveRejected,
            failure);
        Assert.AreEqual(LegacyHidInputControllerOwnerState.Quiesced,
            retryOwner.State);
        Assert.IsTrue(retryOwner.Registration.TryRemove(out failure),
            failure.ToString());

        TestLegacyDevice uncertainDevice = CreateLegacyDevice(
            "01:02:03:04:05:12");
        var uncertainHost = new FakeHost();
        uncertainHost.RemoveResults.Enqueue(
            LegacyHidInputControllerLifecycleResult.Uncertain(
                LegacyHidInputControllerLifecycleOperation.Remove,
                LegacyHidInputControllerLifecycleFailureKind.DependencyThrew));
        Assert.IsTrue(TryCreate(uncertainDevice, 215, false, uncertainHost,
            out var uncertainOwner, out _, out _));
        Assert.IsTrue(uncertainOwner.Registration.TryStopAndQuiesce(100,
            out _));
        Assert.IsFalse(uncertainOwner.Registration.TryRemove(out failure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.OwnerThrew,
            failure);
        Assert.IsTrue(uncertainOwner.RequiresQuarantine);
    }

    [TestMethod]
    public void HostCallsRunOutsideOwnerGateAndReentrancyFailsClosed()
    {
        TestLegacyDevice device = CreateLegacyDevice(
            "01:02:03:04:05:13");
        var host = new FakeHost();
        Assert.IsTrue(TryCreate(device, 216, false, host, out var owner,
            out _, out _));
        InputControllerOwnerOperationFailure reentrantFailure = default;
        bool reentrantResult = true;
        host.ExternalCallProbe = () =>
            Assert.IsFalse(Monitor.IsEntered(owner.LifecycleGate));
        host.OnStop = () => reentrantResult = owner.TryRemove(device, 216,
            out reentrantFailure);

        Assert.IsTrue(owner.Registration.TryStopAndQuiesce(100, out _));
        Assert.IsFalse(reentrantResult);
        Assert.AreEqual(InputControllerOwnerOperationFailure.RemoveRejected,
            reentrantFailure);
        Assert.IsTrue(host.ExternalProbeCalls >= 2,
            "Authentication and stop must both run outside the owner gate.");
    }

    [TestMethod]
    public void ConcurrentStopHasOneExternalOwner()
    {
        TestLegacyDevice device = CreateLegacyDevice(
            "01:02:03:04:05:14");
        var host = new FakeHost();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        host.OnStop = () =>
        {
            entered.Set();
            release.Wait(2_000);
        };
        Assert.IsTrue(TryCreate(device, 217, false, host, out var owner,
            out _, out _));

        Task<bool> first = Task.Run(() => owner.TryStopAndQuiesce(device, 217,
            2_000, out _));
        Assert.IsTrue(entered.Wait(2_000));
        Assert.IsFalse(owner.TryStopAndQuiesce(device, 217, 100,
            out var secondFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.StopRejected,
            secondFailure);
        release.Set();
        Assert.IsTrue(first.Result);
        Assert.AreEqual(1, host.StopCalls);
    }

    [TestMethod]
    public void LegacyAndRuntimeRegistrationsShareTableSlotSelectionOffline()
    {
        TestLegacyDevice legacyDevice = CreateLegacyDevice(
            "01:02:03:04:05:15");
        var host = new FakeHost();
        Assert.IsTrue(TryCreate(legacyDevice, 218, false, host,
            out var legacyOwner, out _, out _));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(219, 319,
            Switch2Transport.Usb, out var runtimeDevice, out _));
        var runtimeOwner = new RuntimeOwner(runtimeDevice, 219);
        Assert.IsTrue(InputControllerRegistration.TryCreate(runtimeDevice, 219,
            InputControllerOwnershipKind.Switch2Runtime,
            hasHidInterface: false, hasPersistentIdentity: false, runtimeOwner,
            out var runtimeRegistration, out _));
        var table = new InputControllerRegistrationTable(2);
        Assert.IsTrue(table.TryOpen(1, out _));

        Assert.IsTrue(table.TryReserveAndBind(legacyOwner.Registration,
            out var legacyToken, out var legacyRollback, out var failure),
            failure.ToString());
        Assert.IsTrue(table.TryReserveAndBind(runtimeRegistration,
            out var runtimeToken, out var runtimeRollback, out failure),
            failure.ToString());

        Assert.AreEqual(0, legacyToken.Slot);
        Assert.AreEqual(1, runtimeToken.Slot);
        Assert.AreEqual(InputControllerOwnershipKind.LegacyHid,
            legacyToken.Registration.OwnershipKind);
        Assert.AreEqual(InputControllerOwnershipKind.Switch2Runtime,
            runtimeToken.Registration.OwnershipKind);
        Assert.IsTrue(table.TryRollback(runtimeRollback, out failure),
            failure.ToString());
        Assert.IsTrue(table.TryRollback(legacyRollback, out failure),
            failure.ToString());
        Assert.AreEqual(0, host.StopCalls);
        Assert.AreEqual(0, host.RemoveCalls);
        Assert.AreEqual(0, legacyDevice.StartCalls);
        Assert.AreEqual(0, legacyDevice.StopCalls);
    }

    [TestMethod]
    public void TableRejectsDuplicateExactLegacyLifetime()
    {
        TestLegacyDevice device = CreateLegacyDevice(
            "01:02:03:04:05:16");
        Assert.IsTrue(TryCreate(device, 220, false, new FakeHost(),
            out var owner, out _, out _));
        var table = new InputControllerRegistrationTable(2);
        Assert.IsTrue(table.TryOpen(1, out _));
        Assert.IsTrue(table.TryReserveAndBind(owner.Registration, out _,
            out var rollback, out _));
        Assert.IsFalse(table.TryReserveAndBind(owner.Registration, out _,
            out _, out var failure));
        Assert.AreEqual(InputControllerSlotTableFailure.DuplicateRegistration,
            failure);
        Assert.IsTrue(table.TryRollback(rollback, out _));
    }

    [TestMethod]
    public void ResultShapeForbidsOutcomeAndOperationMismatches()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            LegacyHidInputControllerLifecycleResult.Reject(
                LegacyHidInputControllerLifecycleOperation.StopAndQuiesce,
                LegacyHidInputControllerLifecycleFailureKind.StopTimedOut));
        Assert.ThrowsException<ArgumentException>(() =>
            LegacyHidInputControllerLifecycleResult.Uncertain(
                LegacyHidInputControllerLifecycleOperation.StopAndQuiesce,
                LegacyHidInputControllerLifecycleFailureKind.StopRejected));
        Assert.ThrowsException<ArgumentException>(() =>
            LegacyHidInputControllerLifecycleResult.Reject(
                LegacyHidInputControllerLifecycleOperation.StopAndQuiesce,
                LegacyHidInputControllerLifecycleFailureKind.RemoveRejected));
        Assert.ThrowsException<ArgumentException>(() =>
            LegacyHidInputControllerLifecycleResult.Reject(
                LegacyHidInputControllerLifecycleOperation.Remove,
                LegacyHidInputControllerLifecycleFailureKind.StopRejected));
        Assert.ThrowsException<ArgumentException>(() =>
            LegacyHidInputControllerLifecycleResult.Uncertain(
                LegacyHidInputControllerLifecycleOperation.Remove,
                LegacyHidInputControllerLifecycleFailureKind.StopTimedOut));
        Assert.IsFalse(default(LegacyHidInputControllerLifecycleResult).
            IsValid);
    }

    private static bool TryCreate(DS4Device device, ulong generation,
        bool hasPersistentIdentity, FakeHost host,
        out LegacyHidInputControllerRegistrationOwner owner,
        out LegacyHidInputControllerCreateFailure failure,
        out InputControllerRegistrationFailure registrationFailure)
    {
        LegacyHidInputControllerLifetimeLease lease = host.Issue(device,
            generation, hasPersistentIdentity);
        return LegacyHidInputControllerRegistrationOwner.TryCreate(lease, host,
            out owner, out failure, out registrationFailure);
    }

    private static TestLegacyDevice CreateLegacyDevice(string serial)
    {
        var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(
            typeof(HidDevice));
        return new TestLegacyDevice(hid, serial);
    }

    private sealed class TestLegacyDevice : DS4Device
    {
        internal TestLegacyDevice(HidDevice hidDevice, string serial) :
            base(hidDevice, "Offline legacy HID")
        {
            Mac = serial;
        }

        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }

        public override void StartUpdate() => StartCalls++;

        public override void StopUpdate() => StopCalls++;
    }

    private sealed class FakeHost :
        ILegacyHidInputControllerLifecycleHost
    {
        private readonly object lifetimeIssuer = new();
        private readonly List<string> order;
        private LegacyHidInputControllerLifetimeLease exactLease;

        internal FakeHost(List<string> order = null)
        {
            this.order = order;
        }

        internal bool AuthenticationAccepted { get; set; } = true;
        internal Queue<bool> AuthenticationResults { get; } = new();
        internal bool ThrowAuthentication { get; set; }
        internal bool ThrowStop { get; set; }
        internal Queue<LegacyHidInputControllerLifecycleResult> StopResults
        {
            get;
        } = new();
        internal Queue<LegacyHidInputControllerLifecycleResult> RemoveResults
        {
            get;
        } = new();
        internal Action ExternalCallProbe { get; set; }
        internal Action OnStop { get; set; }
        internal int ExternalProbeCalls { get; private set; }
        internal int StopCalls { get; private set; }
        internal int RemoveCalls { get; private set; }

        internal LegacyHidInputControllerLifetimeLease Issue(DS4Device device,
            ulong generation, bool hasPersistentIdentity)
        {
            exactLease = new LegacyHidInputControllerLifetimeLease(
                lifetimeIssuer, device, generation, hasPersistentIdentity);
            return exactLease;
        }

        public bool Authenticates(
            in LegacyHidInputControllerLifetimeLease lease)
        {
            Probe();
            if (ThrowAuthentication)
            {
                throw new InvalidOperationException();
            }
            bool authenticationAccepted = AuthenticationResults.Count > 0 ?
                AuthenticationResults.Dequeue() : AuthenticationAccepted;
            if (!authenticationAccepted || !lease.Authenticates(lifetimeIssuer,
                    exactLease.Device, exactLease.Generation))
            {
                return false;
            }
            return lease == exactLease;
        }

        public LegacyHidInputControllerLifecycleResult TryStopAndQuiesce(
            in LegacyHidInputControllerLifetimeLease lease,
            int timeoutMilliseconds)
        {
            Probe();
            StopCalls++;
            order?.Add("stop");
            if (ThrowStop)
            {
                throw new InvalidOperationException();
            }
            if (lease != exactLease)
            {
                return LegacyHidInputControllerLifecycleResult.Reject(
                    LegacyHidInputControllerLifecycleOperation.StopAndQuiesce,
                    LegacyHidInputControllerLifecycleFailureKind.
                        InvalidCredential);
            }
            OnStop?.Invoke();
            return StopResults.Count == 0 ?
                LegacyHidInputControllerLifecycleResult.Success(
                    LegacyHidInputControllerLifecycleOperation.
                        StopAndQuiesce) : StopResults.Dequeue();
        }

        public LegacyHidInputControllerLifecycleResult TryRemove(
            in LegacyHidInputControllerLifetimeLease lease)
        {
            Probe();
            RemoveCalls++;
            order?.Add("remove");
            if (lease != exactLease)
            {
                return LegacyHidInputControllerLifecycleResult.Reject(
                    LegacyHidInputControllerLifecycleOperation.Remove,
                    LegacyHidInputControllerLifecycleFailureKind.
                        InvalidCredential);
            }
            return RemoveResults.Count == 0 ?
                LegacyHidInputControllerLifecycleResult.Success(
                    LegacyHidInputControllerLifecycleOperation.Remove) :
                RemoveResults.Dequeue();
        }

        private void Probe()
        {
            ExternalProbeCalls++;
            ExternalCallProbe?.Invoke();
        }
    }

    private sealed class RuntimeOwner : IInputControllerRegistrationOwner
    {
        private readonly DS4Device device;
        private readonly ulong generation;

        internal RuntimeOwner(DS4Device device, ulong generation)
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
            bool result = Authenticates(candidate, candidateGeneration);
            failure = result ? InputControllerOwnerOperationFailure.None :
                InputControllerOwnerOperationFailure.StopRejected;
            return result;
        }

        public bool TryRemove(DS4Device candidate,
            ulong candidateGeneration,
            out InputControllerOwnerOperationFailure failure)
        {
            bool result = Authenticates(candidate, candidateGeneration);
            failure = result ? InputControllerOwnerOperationFailure.None :
                InputControllerOwnerOperationFailure.RemoveRejected;
            return result;
        }
    }
}
