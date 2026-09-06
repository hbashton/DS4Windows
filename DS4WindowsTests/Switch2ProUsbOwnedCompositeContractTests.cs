using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2ProUsbOwnedCompositeContractTests
{
    private static readonly Guid ContainerA =
        Guid.Parse("D0EEAD04-9DD5-48BC-A27A-CB0FC43CC104");
    private static readonly Guid ContainerB =
        Guid.Parse("E41997AD-CF4F-40FA-92FE-0C2C82905307");

    [TestMethod]
    public void ExactBundleIssuesOneAuthorityAndEveryFacetIsSameObject()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(7, 11);
        var lease = new FakeOwnedLease(lifetime);

        Assert.IsTrue(Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
            lifetime, out Switch2ProUsbOwnedCompositeLeaseBundle bundle,
            out Switch2ProUsbOwnedCompositeAdmissionFailure failure),
            failure.ToString());
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeAdmissionFailure.None, failure);
        Assert.IsTrue(bundle.TryTakeAuthority(
            out Switch2ProUsbOwnedCompositeAuthority authority));
        Assert.IsTrue(authority.IsValid);
        Assert.IsFalse(bundle.TryTakeAuthority(out var duplicate));
        Assert.IsFalse(duplicate.IsValid);

        Assert.IsTrue(bundle.TryGetInputLease(authority, out var input));
        Assert.IsTrue(bundle.TryGetStartupLease(authority, out var startup));
        Assert.IsTrue(bundle.TryGetBoundedOutputLease(authority,
            out var output));
        Assert.AreSame(lease, input);
        Assert.AreSame(lease, startup);
        Assert.AreSame(lease, output);
    }

    [TestMethod]
    public void AuthorityIsReferenceBoundEvenWhenGenerationsMatch()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(5, 9);
        Admit(new FakeOwnedLease(lifetime), lifetime, out var first,
            out var firstAuthority);
        Admit(new FakeOwnedLease(lifetime), lifetime, out var second,
            out var secondAuthority);

        Assert.IsFalse(firstAuthority.Equals(secondAuthority));
        Assert.IsFalse(first.TryGetInputLease(secondAuthority, out _));
        Assert.IsFalse(second.TryGetStartupLease(firstAuthority, out _));
        Assert.IsTrue(first.TryGetBoundedOutputLease(firstAuthority, out _));
        Assert.IsTrue(second.TryGetInputLease(secondAuthority, out _));
    }

    [TestMethod]
    public void ConcurrentAuthorityTakeHasExactlyOneWinner()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(13, 17);
        var lease = new FakeOwnedLease(lifetime);
        Assert.IsTrue(Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
            lifetime, out Switch2ProUsbOwnedCompositeLeaseBundle bundle,
            out _));

        int winners = 0;
        Parallel.For(0, 128, _ =>
        {
            if (bundle.TryTakeAuthority(out var authority) &&
                authority.IsValid)
            {
                Interlocked.Increment(ref winners);
            }
        });
        Assert.AreEqual(1, winners);
    }

    [TestMethod]
    public void AdmissionRejectsMismatchUnauthenticatedAndUnboundedLeases()
    {
        Switch2PhysicalInputLifetime expected = CreateLifetime(3, 4);
        Switch2PhysicalInputLifetime foreign = CreateLifetime(3, 5);

        AssertAdmissionFailure(null, expected,
            Switch2ProUsbOwnedCompositeAdmissionFailure.MissingLease);

        var wrongLifetime = new FakeOwnedLease(expected)
        {
            ReportedLifetime = foreign,
        };
        AssertAdmissionFailure(wrongLifetime, expected,
            Switch2ProUsbOwnedCompositeAdmissionFailure.
                LeaseLifetimeMismatch);

        var wrongRegistration = new FakeOwnedLease(expected)
        {
            ReportedRegistration = CreateLifetime(19, 20, ContainerB).
                Registration,
        };
        AssertAdmissionFailure(wrongRegistration, expected,
            Switch2ProUsbOwnedCompositeAdmissionFailure.
                RegistrationRejected);

        var unauthenticated = new FakeOwnedLease(expected)
        {
            AuthenticationResult = false,
        };
        AssertAdmissionFailure(unauthenticated, expected,
            Switch2ProUsbOwnedCompositeAdmissionFailure.
                AuthenticationRejected);

        var zeroBound = new FakeOwnedLease(expected)
        {
            MaximumOutputOperationMilliseconds = 0,
        };
        AssertAdmissionFailure(zeroBound, expected,
            Switch2ProUsbOwnedCompositeAdmissionFailure.
                InvalidOutputOperationBound);

        var excessiveBound = new FakeOwnedLease(expected)
        {
            MaximumOutputOperationMilliseconds =
                Switch2ProUsbInputTransportOwner.
                    MaximumDisposeTimeoutMilliseconds + 1,
        };
        AssertAdmissionFailure(excessiveBound, expected,
            Switch2ProUsbOwnedCompositeAdmissionFailure.
                InvalidOutputOperationBound);
    }

    [TestMethod]
    public void AdmissionConvertsEveryMetadataOrAuthenticationThrowToClosed()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(21, 22);
        foreach (FakeThrowSite throwSite in Enum.GetValues<FakeThrowSite>().
                     Where(site => site != FakeThrowSite.None))
        {
            var lease = new FakeOwnedLease(lifetime)
            {
                ThrowSite = throwSite,
            };
            AssertAdmissionFailure(lease, lifetime,
                Switch2ProUsbOwnedCompositeAdmissionFailure.DependencyThrew,
                throwSite.ToString());
        }
    }

    [TestMethod]
    public void AdmissionReadsOnlyPureFactsAndPerformsNoControllerOperation()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(31, 37);
        var lease = new FakeOwnedLease(lifetime);

        Assert.IsTrue(Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
            lifetime, out _, out var failure), failure.ToString());
        Assert.AreEqual(1, lease.RegistrationReadCount);
        Assert.AreEqual(1, lease.LifetimeReadCount);
        Assert.AreEqual(1, lease.OutputBoundReadCount);
        Assert.AreEqual(1, lease.AuthenticationCount);
        Assert.AreEqual(0, lease.StartupExecuteCount);
        Assert.AreEqual(0, lease.RetirementCount);
        Assert.AreEqual(0, lease.OutputWriteCount);
        Assert.AreEqual(0, lease.InputReadCount);
        Assert.AreEqual(0, lease.InputQuiescenceCount);
        Assert.AreEqual(0, lease.DisposeCount);
    }

    [TestMethod]
    public void MutableOrThrowingFactsFailClosedBeforeAViewEscapes()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(41, 43);
        Switch2PhysicalInputLifetime staleLifetime = CreateLifetime(41, 44);
        var lease = new FakeOwnedLease(lifetime);
        Assert.IsTrue(Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
            lifetime, out var bundle, out _));
        Assert.IsTrue(bundle.TryTakeAuthority(out var authority));

        lease.AuthenticationResult = false;
        Assert.IsFalse(bundle.TryGetInputLease(authority, out _));

        lease.AuthenticationResult = true;
        lease.ReportedLifetime = staleLifetime;
        Assert.IsFalse(bundle.TryGetStartupLease(authority, out _));

        lease.ReportedLifetime = lifetime;
        lease.MaximumOutputOperationMilliseconds = 0;
        Assert.IsFalse(bundle.TryGetBoundedOutputLease(authority, out _));

        lease.MaximumOutputOperationMilliseconds = 100;
        lease.ThrowSite = FakeThrowSite.Registration;
        Assert.IsFalse(bundle.TryGetInputLease(authority, out _));

        lease.ThrowSite = FakeThrowSite.None;
        Assert.IsTrue(bundle.TryGetInputLease(authority, out var input));
        Assert.AreSame(lease, input);
    }

    [TestMethod]
    public void FeedbackTerminalProofIsExactGenerationBound()
    {
        Switch2PhysicalInputLifetime lifetime = CreateLifetime(23, 29);
        Admit(new FakeOwnedLease(lifetime), lifetime, out _,
            out var authority);

        Switch2ProUsbOwnedFeedbackQuiescenceResult exact =
            Switch2ProUsbOwnedFeedbackQuiescenceResult.Complete(23, 29);
        Switch2ProUsbOwnedFeedbackQuiescenceResult stale =
            Switch2ProUsbOwnedFeedbackQuiescenceResult.Complete(23, 30);
        Switch2ProUsbOwnedFeedbackQuiescenceResult uncertain =
            Switch2ProUsbOwnedFeedbackQuiescenceResult.Uncertain(23, 29);

        Assert.IsTrue(exact.Authenticates(authority));
        Assert.AreEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent,
            exact.Outcome);
        Assert.IsFalse(stale.Authenticates(authority));
        Assert.IsTrue(uncertain.Authenticates(authority));
        Assert.AreNotEqual(
            Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                ExactNeutralAndQuiescent,
            uncertain.Outcome);
        Assert.IsFalse(default(
            Switch2ProUsbOwnedFeedbackQuiescenceResult).
            Authenticates(authority));
    }

    [TestMethod]
    public void ProductionSurfacesRemainExplicitlyBlockedAtReadOnlyBoundary()
    {
        Assert.IsFalse(typeof(ISwitch2ProUsbOwnedCompositeNativeAdapter).
            IsAssignableFrom(typeof(Switch2ProUsbWindowsAdapter)),
            "The live Windows adapter must stay read-only until one-handle " +
            "command I/O, exact response validation, and teardown are wired.");
        Assert.IsFalse(typeof(ISwitch2ProUsbOwnedCompositeLease).
            IsAssignableFrom(
                typeof(Switch2ProUsbWindowsReadOnlyCompositeLease)));
        Assert.IsFalse(typeof(ISwitch2ProUsbOwnedFeedbackLifetime).
            IsAssignableFrom(typeof(Switch2HdRumbleDeliverySink)),
            "The current sink has no bounded physical-writer retirement hook.");

        ConstructorInfo writerConstructor = typeof(
            Switch2ProUsbHdRumblePhysicalWriter).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic).Single();
        ParameterInfo firstParameter = writerConstructor.GetParameters()[0];
        Assert.AreEqual(typeof(ISwitch2ProUsbHdRumbleTransportLease),
            firstParameter.ParameterType);
        Assert.AreNotEqual(typeof(ISwitch2ProUsbOwnedCompositeLease),
            firstParameter.ParameterType,
            "The writer must not be treated as bounded until its constructor " +
            "consumes the stronger owned-composite output contract.");
    }

    private static void Admit(FakeOwnedLease lease,
        in Switch2PhysicalInputLifetime lifetime,
        out Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        out Switch2ProUsbOwnedCompositeAuthority authority)
    {
        Assert.IsTrue(Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
            lifetime, out bundle, out var failure), failure.ToString());
        Assert.IsTrue(bundle.TryTakeAuthority(out authority));
    }

    private static void AssertAdmissionFailure(
        ISwitch2ProUsbOwnedCompositeLease lease,
        in Switch2PhysicalInputLifetime lifetime,
        Switch2ProUsbOwnedCompositeAdmissionFailure expected,
        string message = null)
    {
        Assert.IsFalse(Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
            lifetime, out var bundle, out var failure), message);
        Assert.IsNull(bundle, message);
        Assert.AreEqual(expected, failure, message);
    }

    private static Switch2PhysicalInputLifetime CreateLifetime(
        ulong deviceGeneration, ulong transportGeneration,
        Guid? containerId = null)
    {
        Assert.IsTrue(Switch2PhysicalContainerIdentity.TryCreate(
            containerId ?? ContainerA,
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

    private enum FakeThrowSite : byte
    {
        None = 0,
        Registration,
        Lifetime,
        OutputBound,
        Authentication,
    }

    private sealed class FakeOwnedLease : ISwitch2ProUsbOwnedCompositeLease
    {
        private readonly Switch2PhysicalInputLifetime lifetime;

        internal FakeOwnedLease(in Switch2PhysicalInputLifetime lifetime)
        {
            this.lifetime = lifetime;
            ReportedLifetime = lifetime;
            ReportedRegistration = lifetime.Registration;
        }

        internal Switch2PhysicalInputLifetime ReportedLifetime { get; set; }

        internal Switch2PhysicalInputRegistration ReportedRegistration
        {
            get;
            set;
        }

        internal FakeThrowSite ThrowSite { get; set; }

        internal bool AuthenticationResult { get; set; } = true;

        public int MaximumOutputOperationMilliseconds
        {
            get
            {
                OutputBoundReadCount++;
                if (ThrowSite == FakeThrowSite.OutputBound)
                {
                    throw new InvalidOperationException("Synthetic bound.");
                }
                return maximumOutputOperationMilliseconds;
            }
            set => maximumOutputOperationMilliseconds = value;
        }

        private int maximumOutputOperationMilliseconds = 100;

        internal int RegistrationReadCount { get; private set; }

        internal int LifetimeReadCount { get; private set; }

        internal int OutputBoundReadCount { get; private set; }

        internal int AuthenticationCount { get; private set; }

        internal int StartupExecuteCount { get; private set; }

        internal int RetirementCount { get; private set; }

        internal int OutputWriteCount { get; private set; }

        internal int InputReadCount { get; private set; }

        internal int InputQuiescenceCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public Switch2PhysicalInputRegistration Registration
        {
            get
            {
                RegistrationReadCount++;
                return ThrowSite == FakeThrowSite.Registration ?
                    throw new InvalidOperationException(
                        "Synthetic registration.") : ReportedRegistration;
            }
        }

        public Switch2PhysicalInputLifetime Lifetime
        {
            get
            {
                LifetimeReadCount++;
                return ThrowSite == FakeThrowSite.Lifetime ?
                    throw new InvalidOperationException(
                        "Synthetic lifetime.") : ReportedLifetime;
            }
        }

        public bool AuthenticatesComposite(Switch2ControllerModel model,
            ulong deviceGeneration, ulong transportGeneration)
        {
            AuthenticationCount++;
            if (ThrowSite == FakeThrowSite.Authentication)
            {
                throw new InvalidOperationException("Synthetic auth.");
            }
            return AuthenticationResult &&
                model == Switch2ControllerModel.ProController2 &&
                deviceGeneration ==
                    lifetime.SessionDescriptor.DeviceGeneration &&
                transportGeneration ==
                    lifetime.SessionDescriptor.TransportGeneration;
        }

        public Switch2ProUsbOwnedOutputWriteAttempt
            TryWriteReportBounded(ReadOnlySpan<byte> report,
                Switch2ControllerModel expectedModel,
                ulong expectedDeviceGeneration,
                ulong expectedTransportGeneration,
                int timeoutMilliseconds)
        {
            OutputWriteCount++;
            return new Switch2ProUsbOwnedOutputWriteAttempt(
                Switch2ProUsbHdRumbleTransportWriteResult.Complete(
                    expectedModel, expectedDeviceGeneration,
                    expectedTransportGeneration, report.Length), default);
        }

        public Switch2ProUsbOwnedOutputRetirementResult
            TryRetireOutputOperation(
                in Switch2ProUsbOwnedOutputOperationClaim claim,
                int timeoutMilliseconds) =>
            Switch2ProUsbOwnedOutputRetirementResult.Quiescent(claim);

        public Switch2ProUsbStartupCommandCompletion Execute(
            in Switch2ProUsbStartupCommandClaim claim,
            ReadOnlySpan<byte> exactRequest, int timeoutMilliseconds)
        {
            StartupExecuteCount++;
            return Switch2ProUsbStartupCommandCompletion.ProvenNotConsumed(
                claim, claim.Step);
        }

        public Switch2ProUsbStartupRetirementCompletion Retire(
            in Switch2ProUsbStartupRetirementClaim claim,
            int timeoutMilliseconds)
        {
            RetirementCount++;
            return Switch2ProUsbStartupRetirementCompletion.
                ProvenNotReleased(claim, claim.Reason);
        }

        public bool TryBeginInputRead(byte[] destination, int offset, int count,
            in Switch2ProUsbReadClaim claim,
            ISwitch2ProUsbReadCompletionTarget completionTarget)
        {
            InputReadCount++;
            return false;
        }

        public bool TryCancelInputRead(
            in Switch2ProUsbReadClaim claim) => false;

        public bool TryRetireCompletedInputRead(
            in Switch2ProUsbReadClaim claim,
            int timeoutMilliseconds) => false;

        public bool TryWaitForInputQuiescence(int timeoutMilliseconds)
        {
            InputQuiescenceCount++;
            return true;
        }

        public void DisposeQuiesced()
        {
            DisposeCount++;
        }
    }
}
