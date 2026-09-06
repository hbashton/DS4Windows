using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2ProUsbOwnedCompositePrerequisitesTests
{
    private const ulong DeviceGeneration = 701;
    private const ulong TransportGeneration = 709;
    private const long QpcFrequency = 10_000_000;
    private static readonly Guid ContainerId =
        Guid.Parse("34347AC5-8D1A-4F59-8D72-4F72FA73C89D");

    [TestMethod]
    public void FactoryBindsFullLifetimeAndPublishedCopiesConsumeOnce()
    {
        Context context = CreateContext();

        Assert.IsTrue(TryCreate(context,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration,
            out Switch2ProUsbOwnedCompositeInputAdoptionCredential credential,
            out Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure failure),
            Describe(failure));
        Assert.IsTrue(credential.IsValid);
        Assert.AreEqual(DeviceGeneration, credential.DeviceGeneration);
        Assert.AreEqual(TransportGeneration, credential.TransportGeneration);
        Assert.AreEqual(context.Lifetime, owner.TransportOwner.Lifetime,
            "Credential publication must follow an exact full-lifetime check, including QPC frequency.");

        Switch2ProUsbOwnedCompositeInputAdoptionCredential copy = credential;
        Assert.IsTrue(credential.TryConsume(context.Authority,
            context.Lifetime, owner, registration,
            out Switch2ProUsbOwnedCompositeInputAdoptionFailure consumeFailure),
            consumeFailure.ToString());
        Assert.IsFalse(copy.TryConsume(context.Authority, context.Lifetime,
            owner, registration, out consumeFailure));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                CredentialAlreadyConsumed,
            consumeFailure);

        Assert.IsTrue(owner.TryAbortUnpublished(registration, 1_000,
            out Switch2ProUsbRuntimeUnpublishedAbortFailure abortFailure),
            abortFailure.ToString());
        Assert.AreEqual(1, context.Lease.WaitCount);
        Assert.AreEqual(0, context.Lease.DisposeCount,
            "The runtime may retire only its mediated input facet; the shared physical composite stays retained.");
    }

    [TestMethod]
    public void ForeignSameNumericAuthorityAndRuntimeCannotForgeCredential()
    {
        Context first = CreateContext();
        Context foreign = CreateContext();
        Assert.IsTrue(TryCreate(first, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration,
            out Switch2ProUsbOwnedCompositeInputAdoptionCredential credential,
            out var failure), Describe(failure));
        Assert.IsTrue(TryCreate(foreign,
            out Switch2ProUsbRuntimeOwner foreignOwner,
            out InputControllerRegistration foreignRegistration,
            out _, out failure), Describe(failure));

        Assert.AreEqual(first.Lifetime, foreign.Lifetime);
        Assert.AreNotEqual(first.Authority, foreign.Authority);
        Assert.IsFalse(credential.TryConsume(foreign.Authority,
            foreign.Lifetime, owner, registration,
            out Switch2ProUsbOwnedCompositeInputAdoptionFailure rejection));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionFailure.InvalidCredential,
            rejection);
        Assert.IsFalse(credential.TryConsume(first.Authority, first.Lifetime,
            foreignOwner, foreignRegistration, out rejection));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionFailure.InvalidCredential,
            rejection);

        Assert.IsTrue(credential.TryConsume(first.Authority, first.Lifetime,
            owner, registration, out rejection), rejection.ToString());
        Assert.IsTrue(owner.TryAbortUnpublished(registration, 1_000, out _));
        Assert.IsTrue(foreignOwner.TryAbortUnpublished(foreignRegistration,
            1_000, out _));
    }

    [TestMethod]
    public void ConcurrentCredentialCopiesHaveExactlyOneConsumer()
    {
        Context context = CreateContext();
        Assert.IsTrue(TryCreate(context, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration,
            out Switch2ProUsbOwnedCompositeInputAdoptionCredential credential,
            out var failure), Describe(failure));

        int winners = 0;
        Parallel.For(0, 128, index =>
        {
            Switch2ProUsbOwnedCompositeInputAdoptionCredential copy =
                credential;
            if (copy.TryConsume(context.Authority, context.Lifetime, owner,
                    registration, out var ignoredFailure))
            {
                Interlocked.Increment(ref winners);
            }
        });
        Assert.AreEqual(1, winners);
        Assert.IsTrue(owner.TryAbortUnpublished(registration, 1_000, out _));
    }

    [TestMethod]
    public void ExactLeaseHasOneProcessLocalClaimAcrossCopiesBundlesAndRaces()
    {
        Context sequential = CreateContext();
        Switch2ProUsbOwnedCompositeAuthority copiedAuthority =
            sequential.Authority;
        CreateSecondBundle(sequential.Lease, sequential.Lifetime,
            out Switch2ProUsbOwnedCompositeLeaseBundle secondBundle,
            out Switch2ProUsbOwnedCompositeAuthority secondAuthority);
        Assert.IsTrue(Switch2ProUsbOwnedCompositeInputAdoptionIssuer.TryCreate(
            sequential.Bundle, sequential.Authority, out var firstIssuer,
            out var claimFailure), claimFailure.ToString());
        Assert.IsNotNull(firstIssuer);
        Assert.IsFalse(Switch2ProUsbOwnedCompositeInputAdoptionIssuer.TryCreate(
            sequential.Bundle, copiedAuthority, out _, out claimFailure));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                CompositeAlreadyClaimed,
            claimFailure);
        Assert.IsFalse(Switch2ProUsbOwnedCompositeInputAdoptionIssuer.TryCreate(
            secondBundle, secondAuthority, out _, out claimFailure));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                CompositeAlreadyClaimed,
            claimFailure);

        Context copiedRace = CreateContext();
        copiedAuthority = copiedRace.Authority;
        int copiedWinners = 0;
        var copiedFailures = new ConcurrentBag<
            Switch2ProUsbOwnedCompositeInputAdoptionFailure>();
        Parallel.Invoke(
            () => TryClaim(copiedRace.Bundle, copiedRace.Authority,
                ref copiedWinners, copiedFailures),
            () => TryClaim(copiedRace.Bundle, copiedAuthority,
                ref copiedWinners, copiedFailures));
        Assert.AreEqual(1, copiedWinners);
        Assert.AreEqual(1, copiedFailures.Count);
        Assert.IsTrue(copiedFailures.TryPeek(out claimFailure));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                CompositeAlreadyClaimed,
            claimFailure);

        Context raced = CreateContext();
        CreateSecondBundle(raced.Lease, raced.Lifetime,
            out secondBundle, out secondAuthority);
        int winners = 0;
        var failures = new ConcurrentBag<
            Switch2ProUsbOwnedCompositeInputAdoptionFailure>();
        Parallel.Invoke(
            () => TryClaim(raced.Bundle, raced.Authority, ref winners,
                failures),
            () => TryClaim(secondBundle, secondAuthority, ref winners,
                failures));
        Assert.AreEqual(1, winners);
        Assert.AreEqual(1, failures.Count);
        Assert.IsTrue(failures.TryPeek(out claimFailure));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                CompositeAlreadyClaimed,
            claimFailure);
        Assert.AreEqual(0, raced.Lease.InputReadCount);
        Assert.AreEqual(0, raced.Lease.WaitCount);
        Assert.AreEqual(0, raced.Lease.DisposeCount);
    }

    [TestMethod]
    public void ReentrantHandoffIsRejectedWithoutDeadlockOrSecondFacet()
    {
        Context context = CreateContext();
        CreateBoundIssuer(context, out var issuer,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        int reentered = 0;
        bool reentrantResult = true;
        context.Lease.OnAuthenticate = () =>
        {
            if (Interlocked.Exchange(ref reentered, 1) == 0)
            {
                reentrantResult = issuer.TryOpenReadOnlyComposite(
                    context.Lifetime.Registration, out _);
            }
        };

        Assert.IsTrue(issuer.TryOpenReadOnlyComposite(
            context.Lifetime.Registration,
            out ISwitch2ProUsbReadOnlyCompositeLease facet));
        Assert.IsFalse(reentrantResult);
        Assert.AreEqual(1, reentered);
        Assert.IsTrue(facet.TryWaitForInputQuiescence(100));
        facet.DisposeQuiesced();
        Assert.IsTrue(issuer.TryTakeInputFacetRetirementProof(
            context.Authority, context.Lifetime,
            out Switch2ProUsbOwnedCompositeInputFacetRetirementProof proof));
        Assert.IsTrue(proof.IsValid);
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                ConstructionRollback,
            proof.Kind);
        Assert.IsFalse(issuer.TryTakeInputFacetRetirementProof(
            context.Authority, context.Lifetime, out _));
        CleanupStandalone(owner, registration);
    }

    [TestMethod]
    public void ConcurrentHandoffsReturnExactlyOneMediatedFacet()
    {
        Context context = CreateContext();
        CreateBoundIssuer(context, out var issuer,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        int winners = 0;
        ISwitch2ProUsbReadOnlyCompositeLease winningFacet = null;
        Parallel.For(0, 128, _ =>
        {
            if (issuer.TryOpenReadOnlyComposite(
                    context.Lifetime.Registration, out var facet))
            {
                Interlocked.Increment(ref winners);
                Interlocked.CompareExchange(ref winningFacet, facet, null);
            }
        });
        Assert.AreEqual(1, winners);
        Assert.IsNotNull(winningFacet);
        Assert.IsTrue(winningFacet.TryWaitForInputQuiescence(100));
        winningFacet.DisposeQuiesced();
        CleanupStandalone(owner, registration);
    }

    [TestMethod]
    public void SequenceExhaustionPrecedesEveryHandoffDependencyCall()
    {
        Context context = CreateContext();
        Assert.IsTrue(
            Switch2ProUsbOwnedCompositeInputAdoptionIssuer.TryCreateCore(
                context.Bundle, context.Authority, ulong.MaxValue,
                out var issuer, out var failure), failure.ToString());
        CreateStandaloneOwner(context,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        Assert.IsTrue(issuer.TryBindRuntimeOwner(owner, registration));
        int authenticationBefore = context.Lease.AuthenticationCount;
        int registrationReadsBefore = context.Lease.RegistrationReadCount;

        Assert.IsFalse(issuer.TryOpenReadOnlyComposite(
            context.Lifetime.Registration, out _));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionState.SequenceExhausted,
            issuer.State);
        Assert.AreEqual(authenticationBefore,
            context.Lease.AuthenticationCount);
        Assert.AreEqual(registrationReadsBefore,
            context.Lease.RegistrationReadCount);
        CleanupStandalone(owner, registration);
    }

    [TestMethod]
    public void CredentialPublicationRejectsSameGenerationsWithDifferentQpc()
    {
        Context context = CreateContext();
        CreateBoundIssuer(context, out var issuer,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        Assert.IsTrue(issuer.TryOpenReadOnlyComposite(
            context.Lifetime.Registration,
            out ISwitch2ProUsbReadOnlyCompositeLease facet));
        Assert.IsTrue(Switch2PhysicalInputLifetime.TryCreate(
            context.Lifetime.Registration, DeviceGeneration,
            TransportGeneration, QpcFrequency + 1,
            out Switch2PhysicalInputLifetime wrongClockLifetime));

        Assert.IsFalse(issuer.TryPublishCredential(owner, registration,
            wrongClockLifetime, out _,
            out Switch2ProUsbOwnedCompositeInputAdoptionFailure failure));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionFailure.StaleCredential,
            failure);
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined,
            issuer.State);
        Assert.IsTrue(facet.TryWaitForInputQuiescence(100));
        facet.DisposeQuiesced();
        Assert.IsTrue(issuer.TryTakeInputFacetRetirementProof(
            context.Authority, context.Lifetime, out var proof));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                ConstructionRollback,
            proof.Kind);
        CleanupStandalone(owner, registration);
    }

    [TestMethod]
    public void PumpRejectOrThrowRetainsQuarantineAndRollbackProof()
    {
        foreach (bool throws in new[] { false, true })
        {
            Context context = CreateContext();
            Assert.IsFalse(TryCreateCore(context,
                new FailingPumpFactory(throws), out _, out _,
                out Switch2ProUsbOwnedCompositeInputAdoptionCredential
                    exportedCredential,
                out Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure failure));
            Assert.IsFalse(exportedCredential.IsValid);
            Assert.IsTrue(failure.RequiresRetention);
            Assert.IsNotNull(failure.RetainedIssuer);
            Assert.IsNotNull(failure.RetainedRuntimeOwner);
            Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Quarantined,
                failure.RetainedRuntimeOwner.State);
            Assert.AreEqual(
                Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined,
                failure.RetainedIssuer.State);
            Assert.IsTrue(failure.InputFacetRetirementProof.IsValid);
            Assert.AreEqual(
                Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                    ConstructionRollback,
                failure.InputFacetRetirementProof.Kind);
            Assert.AreEqual(1, context.Lease.WaitCount);
            Assert.AreEqual(0, context.Lease.DisposeCount);
            Assert.AreEqual(0, context.Lease.InputReadCount);
            Assert.AreEqual(0, context.Lease.OutputWriteCount);
            Assert.AreEqual(0, context.Lease.StartupCount);
            Assert.AreEqual(throws ?
                    Switch2ProUsbRuntimeCreateFailureKind.DependencyThrew :
                    Switch2ProUsbRuntimeCreateFailureKind.PumpRejected,
                failure.RuntimeFailure.Kind);

            FieldInfo pendingField = typeof(
                Switch2ProUsbOwnedCompositeInputAdoptionIssuer).GetField(
                    "credential", BindingFlags.Instance |
                    BindingFlags.NonPublic);
            Assert.IsNotNull(pendingField);
            var pendingCredential =
                (Switch2ProUsbOwnedCompositeInputAdoptionCredential)
                pendingField.GetValue(failure.RetainedIssuer);
            Assert.IsTrue(pendingCredential.IsValid,
                "The test captures the internally minted pre-publication copy.");
            int authenticationsBeforeConsume =
                context.Lease.AuthenticationCount;
            int registrationReadsBeforeConsume =
                context.Lease.RegistrationReadCount;
            Assert.IsFalse(pendingCredential.TryConsume(context.Authority,
                context.Lifetime, failure.RetainedRuntimeOwner,
                failure.RetainedRuntimeOwner.Registration,
                out Switch2ProUsbOwnedCompositeInputAdoptionFailure
                    pendingFailure));
            Assert.AreEqual(
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    QuarantineRequired,
                pendingFailure,
                "Construction failure must invalidate every pending copy before any bundle revalidation.");
            Assert.AreEqual(authenticationsBeforeConsume,
                context.Lease.AuthenticationCount);
            Assert.AreEqual(registrationReadsBeforeConsume,
                context.Lease.RegistrationReadCount,
                "A copied pending credential must fail at the sticky quarantine fence without revalidating the bundle.");

            Assert.IsFalse(
                Switch2ProUsbOwnedCompositeInputAdoptionIssuer.TryCreate(
                    context.Bundle, context.Authority, out _,
                    out Switch2ProUsbOwnedCompositeInputAdoptionFailure
                        secondFailure));
            Assert.AreEqual(
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    CompositeAlreadyClaimed,
                secondFailure);
        }
    }

    [TestMethod]
    public void AttentionInstallationFailureAlsoRetainsExactRollbackProof()
    {
        Context context = CreateContext();
        Assert.IsFalse(TryCreateCore(context,
            new RejectingAttentionPumpFactory(), out _, out _, out _,
            out Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure failure));
        Assert.AreEqual(Switch2ProUsbRuntimeCreateFailureKind.PumpRejected,
            failure.RuntimeFailure.Kind);
        Assert.IsTrue(failure.InputFacetRetirementProof.IsValid);
        Assert.AreEqual(1, context.Lease.WaitCount);
        Assert.AreEqual(0, context.Lease.DisposeCount);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Quarantined,
            failure.RetainedRuntimeOwner.State);
    }

    [TestMethod]
    public void FailedOrThrowingQuiescenceRetainsWithoutFalseProof()
    {
        foreach (bool throws in new[] { false, true })
        {
            Context context = CreateContext();
            context.Lease.WaitResult = false;
            context.Lease.ThrowOnWait = throws;
            Assert.IsFalse(TryCreateCore(context,
                new FailingPumpFactory(throws: false), out _, out _, out _,
                out Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure failure));
            Assert.IsTrue(failure.RequiresRetention);
            Assert.IsFalse(failure.InputFacetRetirementProof.IsValid);
            Assert.AreEqual(
                Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined,
                failure.RetainedIssuer.State);
            Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Quarantined,
                failure.RetainedRuntimeOwner.State);
            Assert.AreEqual(0, context.Lease.DisposeCount);
            Assert.IsFalse(failure.RetainedIssuer.IsInputFacetRetired);
            Assert.IsFalse(
                Switch2ProUsbOwnedCompositeInputAdoptionIssuer.TryCreate(
                    context.Bundle, context.Authority, out _, out _));
        }
    }

    [TestMethod]
    public void DelayedRollbackRetryRemainsConstructionPhaseAndOneShot()
    {
        Context context = CreateContext();
        context.Lease.WaitResult = false;
        Assert.IsFalse(TryCreateCore(context,
            new FailingPumpFactory(throws: false), out _, out _, out _,
            out Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure failure));
        Assert.IsFalse(failure.InputFacetRetirementProof.IsValid);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Quarantined,
            failure.RetainedRuntimeOwner.State);

        context.Lease.WaitResult = true;
        Assert.IsTrue(failure.RetainedRuntimeOwner.TransportOwner.
            TryQuiesceAndDispose(1_000,
                out Switch2ProUsbDisposeFailure retryFailure),
            retryFailure.ToString());
        Assert.IsTrue(failure.RetainedIssuer.
            TryTakeInputFacetRetirementProof(context.Authority,
                context.Lifetime,
                out Switch2ProUsbOwnedCompositeInputFacetRetirementProof
                    delayedProof));
        Assert.IsTrue(delayedProof.IsValid);
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                ConstructionRollback,
            delayedProof.Kind,
            "A credential was never published; delayed cleanup cannot be relabeled as runtime retirement.");
        Assert.IsFalse(failure.RetainedIssuer.
            TryTakeInputFacetRetirementProof(context.Authority,
                context.Lifetime, out _));
        Assert.AreEqual(2, context.Lease.WaitCount);
        Assert.AreEqual(0, context.Lease.DisposeCount);
    }

    [TestMethod]
    public void OwnedAdoptionUsesExactMediatorAndNeverReopensOrRediscovers()
    {
        Context context = CreateContext();
        var otherController = new FakeDiscovery(CreateObservation(Guid.Parse(
            "95D7DAA8-3481-4BD3-90D6-A9A7B9281318")));
        var externalWindowsAdapter = new FakeNativeAdapter(context.Lease)
        {
            ThrowOnOpen = true,
        };

        Assert.IsFalse(typeof(
                Switch2ProUsbOwnedCompositeRuntimeAdoptionFactory).
            GetMethods(BindingFlags.Static | BindingFlags.NonPublic).
            SelectMany(method => method.GetParameters()).Any(parameter =>
                parameter.ParameterType ==
                    typeof(ISwitch2ProUsbOsDiscoveryAdapter)),
            "Owned adoption must not accept a fresh OS observer.");
        MethodInfo ownedRuntimeSeam = typeof(Switch2ProUsbRuntimeOwner).
            GetMethod("TryCreateOwnedCompositeCore", BindingFlags.Static |
                BindingFlags.NonPublic);
        Assert.IsNotNull(ownedRuntimeSeam);
        ParameterInfo[] ownedParameters = ownedRuntimeSeam.GetParameters();
        Assert.AreEqual(
            typeof(Switch2ProUsbOwnedCompositeInputAdoptionIssuer),
            ownedParameters[0].ParameterType,
            "Owned construction must take one concrete combined mediator.");
        Assert.IsFalse(ownedParameters.Any(parameter =>
                parameter.ParameterType ==
                    typeof(ISwitch2ProUsbOsDiscoveryAdapter) ||
                parameter.ParameterType ==
                    typeof(ISwitch2ProUsbNativeAdapter) ||
                parameter.ParameterType ==
                    typeof(ISwitch2ProUsbRuntimeInputAdoptionBinder)),
            "Owned construction cannot accept an unrelated observer, Windows adapter, or binder.");
        Assert.IsTrue(TryCreate(context, out var owner,
            out var registration, out var credential, out var failure),
            Describe(failure));
        Assert.AreEqual(context.Lifetime, owner.TransportOwner.Lifetime);
        FieldInfo transportLeaseField =
            typeof(Switch2ProUsbInputTransportOwner).GetField("nativeLease",
                BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(transportLeaseField);
        object transportLease = transportLeaseField.GetValue(
            owner.TransportOwner);
        Assert.IsInstanceOfType(transportLease,
            typeof(Switch2ProUsbOwnedCompositeInputFacetLease));
        var exactFacet =
            (Switch2ProUsbOwnedCompositeInputFacetLease)
                transportLease;
        FieldInfo compositeLeaseField =
            typeof(Switch2ProUsbOwnedCompositeInputFacetLease).GetField(
                "compositeLease", BindingFlags.Instance |
                BindingFlags.NonPublic);
        Assert.IsNotNull(compositeLeaseField);
        Assert.AreSame(context.Lease,
            compositeLeaseField.GetValue(exactFacet),
            "The runtime must retain the issuer-mediated facet over the exact admitted composite lease.");
        Assert.AreEqual(0, otherController.CallCount,
            "A present second controller was re-observed after exact admission.");
        Assert.AreEqual(0, externalWindowsAdapter.CallCount,
            "Owned adoption must not call an external Windows open adapter.");
        Assert.IsTrue(credential.TryConsume(context.Authority,
            context.Lifetime, owner, registration, out _));
        Assert.IsTrue(owner.TryAbortUnpublished(registration, 200, out _));
    }

    [TestMethod]
    public void FacetSealsAtTrueWaitAndSuppressesCallbackAfterRetirement()
    {
        Context context = CreateContext();
        CreateBoundIssuer(context, out var issuer,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        Assert.IsTrue(issuer.TryOpenReadOnlyComposite(
            context.Lifetime.Registration,
            out ISwitch2ProUsbReadOnlyCompositeLease facet));
        var claim = new Switch2ProUsbReadClaim(new object(), DeviceGeneration,
            TransportGeneration, 1);
        var target = new CountingCompletionTarget();
        byte[] buffer = new byte[64];
        Assert.IsTrue(facet.TryBeginInputRead(buffer, 0, buffer.Length, claim,
            target));
        Assert.IsTrue(facet.TryCancelInputRead(claim));
        Assert.IsTrue(facet.TryWaitForInputQuiescence(100));

        Assert.IsFalse(facet.TryBeginInputRead(buffer, 0, buffer.Length,
            new Switch2ProUsbReadClaim(new object(), DeviceGeneration,
                TransportGeneration, 2), target));
        Assert.IsFalse(facet.TryCancelInputRead(claim));
        Assert.IsFalse(facet.TryRetireCompletedInputRead(claim, 100));
        Assert.IsFalse(facet.TryWaitForInputQuiescence(100));
        Assert.AreEqual(1, context.Lease.WaitCount,
            "A sealed/retired facet cannot manufacture a fresh native wait proof.");

        facet.DisposeQuiesced();
        Switch2ProUsbReadCompletionDisposition late =
            context.Lease.FireCompletion();
        Assert.AreEqual(
            Switch2ProUsbReadCompletionDisposition.LifecycleSuppressed, late);
        Assert.AreEqual(0, target.Count);
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionState.Quarantined,
            issuer.State,
            "A completion after native quiescence is a dependency breach and must fail closed.");
        Assert.ThrowsException<InvalidOperationException>(
            () => facet.DisposeQuiesced());
        Assert.AreEqual(0, context.Lease.DisposeCount);
        CleanupStandalone(owner, registration);
    }

    [TestMethod]
    public void BlockedWaitRejectsConcurrentBeginAndSealsBeforeDispose()
    {
        Context context = CreateContext();
        context.Lease.BlockWait = true;
        CreateBoundIssuer(context, out var issuer,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        Assert.IsTrue(issuer.TryOpenReadOnlyComposite(
            context.Lifetime.Registration,
            out ISwitch2ProUsbReadOnlyCompositeLease facet));
        Task<bool> waiting = Task.Run(() =>
            facet.TryWaitForInputQuiescence(2_000));
        Assert.IsTrue(context.Lease.WaitEntered.Wait(1_000));
        var claim = new Switch2ProUsbReadClaim(new object(), DeviceGeneration,
            TransportGeneration, 1);
        Assert.IsFalse(facet.TryBeginInputRead(new byte[64], 0, 64, claim,
            new CountingCompletionTarget()));
        context.Lease.AllowWait.Set();
        Assert.IsTrue(waiting.GetAwaiter().GetResult());
        Assert.IsFalse(facet.TryBeginInputRead(new byte[64], 0, 64, claim,
            new CountingCompletionTarget()));
        facet.DisposeQuiesced();
        CleanupStandalone(owner, registration);
    }

    [TestMethod]
    public void FalseWaitSealsNewReadsButDrainsTheOutstandingCompletion()
    {
        Context context = CreateContext();
        context.Lease.WaitResult = false;
        CreateBoundIssuer(context, out var issuer,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        Assert.IsTrue(issuer.TryOpenReadOnlyComposite(
            context.Lifetime.Registration,
            out ISwitch2ProUsbReadOnlyCompositeLease facet));

        var claim = new Switch2ProUsbReadClaim(new object(), DeviceGeneration,
            TransportGeneration, 1);
        var target = new CountingCompletionTarget();
        byte[] buffer = new byte[64];
        Assert.IsTrue(facet.TryBeginInputRead(buffer, 0, buffer.Length, claim,
            target));
        Assert.IsFalse(facet.TryWaitForInputQuiescence(100));
        Assert.IsFalse(facet.TryBeginInputRead(buffer, 0, buffer.Length,
            new Switch2ProUsbReadClaim(new object(), DeviceGeneration,
                TransportGeneration, 2), target),
            "The first retirement attempt is a permanent new-read fence even when native quiescence times out.");

        Assert.AreEqual(Switch2ProUsbReadCompletionDisposition.Published,
            context.Lease.FireCompletion());
        Assert.AreEqual(1, target.Count,
            "A completion for the already-submitted read must drain until native quiescence is proven.");
        Assert.IsTrue(facet.TryRetireCompletedInputRead(claim, 100));

        Assert.IsFalse(issuer.TryPublishCredential(owner, registration,
            context.Lifetime, out _,
            out Switch2ProUsbOwnedCompositeInputAdoptionFailure
                publicationFailure));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionFailure.StaleCredential,
            publicationFailure,
            "A failed retirement attempt cannot later be relabeled by publishing the runtime credential.");

        context.Lease.WaitResult = true;
        Assert.IsTrue(facet.TryWaitForInputQuiescence(100));
        facet.DisposeQuiesced();
        Assert.IsTrue(issuer.TryTakeInputFacetRetirementProof(
            context.Authority, context.Lifetime, out var proof));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                ConstructionRollback,
            proof.Kind);
        CleanupStandalone(owner, registration);
    }

    [TestMethod]
    public void PublishedCredentialBindsRetirementProofToRuntimePhase()
    {
        Context context = CreateContext();
        CreateBoundIssuer(context, out var issuer,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        Assert.IsTrue(issuer.TryOpenReadOnlyComposite(
            context.Lifetime.Registration,
            out ISwitch2ProUsbReadOnlyCompositeLease facet));
        Assert.IsTrue(issuer.TryPublishCredential(owner, registration,
            context.Lifetime,
            out Switch2ProUsbOwnedCompositeInputAdoptionCredential credential,
            out Switch2ProUsbOwnedCompositeInputAdoptionFailure failure),
            failure.ToString());
        Assert.IsTrue(credential.IsValid);

        Assert.IsTrue(facet.TryWaitForInputQuiescence(100));
        facet.DisposeQuiesced();
        Assert.IsTrue(issuer.TryTakeInputFacetRetirementProof(
            context.Authority, context.Lifetime, out var proof));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputFacetRetirementKind.
                RuntimeRetirement,
            proof.Kind);
        Assert.IsFalse(credential.TryConsume(context.Authority,
            context.Lifetime, owner, registration, out failure));
        Assert.AreEqual(
            Switch2ProUsbOwnedCompositeInputAdoptionFailure.StaleCredential,
            failure);
        CleanupStandalone(owner, registration);
    }

    [TestMethod]
    public void MediatedSteadyCompletionAndRetirementPathAllocatesZero()
    {
        Context context = CreateContext();
        context.Lease.CompleteSynchronously = true;
        CreateBoundIssuer(context, out var issuer,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        Assert.IsTrue(issuer.TryOpenReadOnlyComposite(
            context.Lifetime.Registration,
            out ISwitch2ProUsbReadOnlyCompositeLease facet));
        var target = new CountingCompletionTarget();
        byte[] buffer = new byte[64];
        object claimFence = new();

        RunReports(facet, target, buffer, claimFence, 1, 2_000);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        RunReports(facet, target, buffer, claimFence, 2_001, 20_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(22_000, target.Count);
        Assert.IsTrue(facet.TryWaitForInputQuiescence(100));
        facet.DisposeQuiesced();
        CleanupStandalone(owner, registration);
    }

    [TestMethod]
    public void FeedbackPrepareResultAndCredentialAreReferenceAuthorityBound()
    {
        Context first = CreateContext();
        Context foreign = CreateContext();
        object issuer = new();
        object fence = new();
        var credential = new Switch2ProUsbOwnedFeedbackPrepareCredential(
            issuer, fence, first.Authority, first.Lifetime, 1);
        Switch2ProUsbOwnedFeedbackActivationResult prepared =
            Switch2ProUsbOwnedFeedbackActivationResult.Prepared(
                first.Authority, credential);

        Assert.IsTrue(prepared.HasValidInvariants());
        Assert.IsTrue(prepared.Authenticates(first.Authority));
        Assert.IsFalse(prepared.Authenticates(foreign.Authority),
            "Same numeric generations do not authenticate a foreign bundle authority.");
        Assert.IsFalse(credential.Authenticates(new object(), fence,
            first.Authority, first.Lifetime, 1));
        Assert.IsFalse(credential.Authenticates(issuer, fence,
            foreign.Authority, foreign.Lifetime, 1));
        Assert.IsFalse(default(Switch2ProUsbOwnedFeedbackActivationResult).
            HasValidInvariants());
        Assert.IsFalse(
            Switch2ProUsbOwnedFeedbackActivationResult.Succeeded(
                Switch2ProUsbOwnedFeedbackActivationOperation.Prepare,
                first.Authority).HasValidInvariants(),
            "Prepare success without an exact credential is invalid.");
        Assert.IsTrue(
            Switch2ProUsbOwnedFeedbackActivationResult.Uncertain(
                Switch2ProUsbOwnedFeedbackActivationOperation.Commit,
                first.Authority).HasValidInvariants());
    }

    private static void RunReports(
        ISwitch2ProUsbReadOnlyCompositeLease facet,
        CountingCompletionTarget target, byte[] buffer, object claimFence,
        int firstSequence, int count)
    {
        for (int index = 0; index < count; index++)
        {
            var claim = new Switch2ProUsbReadClaim(claimFence,
                DeviceGeneration, TransportGeneration,
                (ulong)(firstSequence + index));
            if (!facet.TryBeginInputRead(buffer, 0, buffer.Length, claim,
                    target) ||
                !facet.TryRetireCompletedInputRead(claim, 100))
            {
                Assert.Fail("Synthetic steady path rejected a report.");
            }
        }
    }

    private static bool TryCreate(Context context,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2ProUsbOwnedCompositeInputAdoptionCredential credential,
        out Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure failure) =>
        Switch2ProUsbOwnedCompositeRuntimeAdoptionFactory.TryCreate(
            context.Bundle, context.Authority, context.Calibration, 200,
            out owner,
            out registration, out credential, out failure);

    private static bool TryCreateCore(Context context,
        ISwitch2ProUsbRuntimePumpFactory pumpFactory,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2ProUsbOwnedCompositeInputAdoptionCredential credential,
        out Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure failure) =>
        Switch2ProUsbOwnedCompositeRuntimeAdoptionFactory.TryCreateCore(
            context.Bundle, context.Authority, context.Calibration, 200,
            pumpFactory,
            Switch2ProUsbRuntimeTerminalScheduler.Instance, 0, out owner,
            out registration, out credential, out failure);

    private static void CreateBoundIssuer(Context context,
        out Switch2ProUsbOwnedCompositeInputAdoptionIssuer issuer,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration)
    {
        Assert.IsTrue(Switch2ProUsbOwnedCompositeInputAdoptionIssuer.TryCreate(
            context.Bundle, context.Authority, out issuer, out var failure),
            failure.ToString());
        CreateStandaloneOwner(context, out owner, out registration);
        Assert.IsTrue(issuer.TryBindRuntimeOwner(owner, registration),
            issuer.LastFailure.ToString());
    }

    private static void CreateStandaloneOwner(Context context,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration)
    {
        var lease = new FakeOwnedLease(context.Lifetime);
        Assert.IsTrue(Switch2ProUsbRuntimeOwner.TryCreate(
            new FakeDiscovery(context.Observation),
            new FakeNativeAdapter(lease), DeviceGeneration,
            TransportGeneration, QpcFrequency, context.Calibration, 200,
            out owner, out registration,
            out Switch2ProUsbRuntimeCreateFailure failure),
            failure.Kind.ToString());
    }

    private static void CleanupStandalone(Switch2ProUsbRuntimeOwner owner,
        in InputControllerRegistration registration)
    {
        Assert.IsTrue(owner.TryAbortUnpublished(registration, 1_000,
            out Switch2ProUsbRuntimeUnpublishedAbortFailure failure),
            failure.ToString());
    }

    private static void TryClaim(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority, ref int winners,
        ConcurrentBag<Switch2ProUsbOwnedCompositeInputAdoptionFailure>
            failures)
    {
        if (Switch2ProUsbOwnedCompositeInputAdoptionIssuer.TryCreate(bundle,
                authority, out _, out var failure))
        {
            Interlocked.Increment(ref winners);
        }
        else
        {
            failures.Add(failure);
        }
    }

    private static string Describe(
        in Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure failure) =>
        $"{failure.Kind}/{failure.AdoptionFailure}/" +
        failure.RuntimeFailure.Kind;

    private static Context CreateContext()
    {
        Switch2ProUsbCompositeObservation observation = CreateObservation();
        Assert.IsTrue(Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
            out Switch2PhysicalInputRegistration registration,
            out Switch2PhysicalAdmissionFailure admission),
            admission.ToString());
        Assert.IsTrue(Switch2PhysicalInputLifetime.TryCreate(registration,
            DeviceGeneration, TransportGeneration, QpcFrequency,
            out Switch2PhysicalInputLifetime lifetime));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));
        var lease = new FakeOwnedLease(lifetime);
        Assert.IsTrue(Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
            lifetime, out Switch2ProUsbOwnedCompositeLeaseBundle bundle,
            out Switch2ProUsbOwnedCompositeAdmissionFailure bundleFailure),
            bundleFailure.ToString());
        Assert.IsTrue(bundle.TryTakeAuthority(
            out Switch2ProUsbOwnedCompositeAuthority authority));
        return new Context(observation, lifetime, calibration, lease, bundle,
            authority);
    }

    private static void CreateSecondBundle(FakeOwnedLease lease,
        in Switch2PhysicalInputLifetime lifetime,
        out Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        out Switch2ProUsbOwnedCompositeAuthority authority)
    {
        Assert.IsTrue(Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
            lifetime, out bundle, out var failure), failure.ToString());
        Assert.IsTrue(bundle.TryTakeAuthority(out authority));
    }

    private static Switch2ProUsbCompositeObservation CreateObservation(
        Guid? exactContainerId = null)
    {
        Assert.IsTrue(Switch2PhysicalContainerIdentity.TryCreate(
            exactContainerId ?? ContainerId,
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
            container, 1, 1, input, command);
    }

    private sealed class Context
    {
        internal Context(in Switch2ProUsbCompositeObservation observation,
            in Switch2PhysicalInputLifetime lifetime,
            in Switch2InputCalibrationSnapshot calibration,
            FakeOwnedLease lease,
            Switch2ProUsbOwnedCompositeLeaseBundle bundle,
            in Switch2ProUsbOwnedCompositeAuthority authority)
        {
            Observation = observation;
            Lifetime = lifetime;
            Calibration = calibration;
            Lease = lease;
            Bundle = bundle;
            Authority = authority;
        }

        internal Switch2ProUsbCompositeObservation Observation { get; }
        internal Switch2PhysicalInputLifetime Lifetime { get; }
        internal Switch2InputCalibrationSnapshot Calibration { get; }
        internal FakeOwnedLease Lease { get; }
        internal Switch2ProUsbOwnedCompositeLeaseBundle Bundle { get; }
        internal Switch2ProUsbOwnedCompositeAuthority Authority { get; }
    }

    private sealed class FakeDiscovery : ISwitch2ProUsbOsDiscoveryAdapter
    {
        private readonly Switch2ProUsbCompositeObservation observation;

        internal FakeDiscovery(in Switch2ProUsbCompositeObservation observation)
        {
            this.observation = observation;
        }

        internal bool Result { get; set; } = true;

        internal int CallCount { get; private set; }

        public bool TryObserveComposite(
            out Switch2ProUsbCompositeObservation candidate)
        {
            CallCount++;
            candidate = observation;
            return Result;
        }
    }

    private sealed class FakeNativeAdapter : ISwitch2ProUsbNativeAdapter
    {
        private readonly FakeOwnedLease lease;

        internal FakeNativeAdapter(FakeOwnedLease lease)
        {
            this.lease = lease;
        }

        internal bool ThrowOnOpen { get; set; }

        internal int CallCount { get; private set; }

        public bool TryOpenReadOnlyComposite(
            in Switch2PhysicalInputRegistration registration,
            out ISwitch2ProUsbReadOnlyCompositeLease opened)
        {
            CallCount++;
            if (ThrowOnOpen)
            {
                throw new InvalidOperationException(
                    "External native open must remain unreachable.");
            }
            opened = lease;
            return true;
        }
    }

    private sealed class FakeOwnedLease : ISwitch2ProUsbOwnedCompositeLease
    {
        private readonly Switch2PhysicalInputLifetime lifetime;
        private ISwitch2ProUsbReadCompletionTarget completionTarget;
        private Switch2ProUsbReadClaim completionClaim;
        private int registrationReadCount;
        private int authenticationCount;
        private int inputReadCount;
        private int waitCount;
        private int disposeCount;
        private int outputWriteCount;
        private int startupCount;

        internal FakeOwnedLease(in Switch2PhysicalInputLifetime lifetime)
        {
            this.lifetime = lifetime;
        }

        internal Action OnAuthenticate { get; set; }
        internal bool WaitResult { get; set; } = true;
        internal bool ThrowOnWait { get; set; }
        internal bool BlockWait { get; set; }
        internal bool CompleteSynchronously { get; set; }
        internal ManualResetEventSlim WaitEntered { get; } = new(false);
        internal ManualResetEventSlim AllowWait { get; } = new(false);
        internal int RegistrationReadCount =>
            Volatile.Read(ref registrationReadCount);
        internal int AuthenticationCount =>
            Volatile.Read(ref authenticationCount);
        internal int InputReadCount => Volatile.Read(ref inputReadCount);
        internal int WaitCount => Volatile.Read(ref waitCount);
        internal int DisposeCount => Volatile.Read(ref disposeCount);
        internal int OutputWriteCount => Volatile.Read(ref outputWriteCount);
        internal int StartupCount => Volatile.Read(ref startupCount);

        public Switch2PhysicalInputRegistration Registration
        {
            get
            {
                Interlocked.Increment(ref registrationReadCount);
                return lifetime.Registration;
            }
        }

        public Switch2PhysicalInputLifetime Lifetime => lifetime;

        public int MaximumOutputOperationMilliseconds => 100;

        public bool AuthenticatesComposite(Switch2ControllerModel model,
            ulong deviceGeneration, ulong transportGeneration)
        {
            Interlocked.Increment(ref authenticationCount);
            OnAuthenticate?.Invoke();
            return model == Switch2ControllerModel.ProController2 &&
                deviceGeneration ==
                    lifetime.SessionDescriptor.DeviceGeneration &&
                transportGeneration ==
                    lifetime.SessionDescriptor.TransportGeneration;
        }

        public bool TryBeginInputRead(byte[] destination, int offset,
            int count, in Switch2ProUsbReadClaim claim,
            ISwitch2ProUsbReadCompletionTarget target)
        {
            Interlocked.Increment(ref inputReadCount);
            completionTarget = target;
            completionClaim = claim;
            if (CompleteSynchronously)
            {
                target.CompleteInputRead(claim, count, inputReadCount,
                    Switch2ProUsbNativeReadStatus.Completed);
            }
            return true;
        }

        public bool TryCancelInputRead(
            in Switch2ProUsbReadClaim claim) => true;

        public bool TryRetireCompletedInputRead(
            in Switch2ProUsbReadClaim claim,
            int timeoutMilliseconds) => true;

        public bool TryWaitForInputQuiescence(int timeoutMilliseconds)
        {
            Interlocked.Increment(ref waitCount);
            WaitEntered.Set();
            if (BlockWait)
            {
                AllowWait.Wait(timeoutMilliseconds);
            }
            if (ThrowOnWait)
            {
                throw new InvalidOperationException("Synthetic wait fault.");
            }
            return WaitResult;
        }

        public void DisposeQuiesced() =>
            Interlocked.Increment(ref disposeCount);

        internal Switch2ProUsbReadCompletionDisposition FireCompletion() =>
            completionTarget.CompleteInputRead(completionClaim, 64, 1,
                Switch2ProUsbNativeReadStatus.Completed);

        public Switch2ProUsbOwnedOutputWriteAttempt
            TryWriteReportBounded(ReadOnlySpan<byte> report,
                Switch2ControllerModel expectedModel,
                ulong expectedDeviceGeneration,
                ulong expectedTransportGeneration,
                int timeoutMilliseconds)
        {
            Interlocked.Increment(ref outputWriteCount);
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
            Interlocked.Increment(ref startupCount);
            return Switch2ProUsbStartupCommandCompletion.ProvenNotConsumed(
                claim, claim.Step);
        }

        public Switch2ProUsbStartupRetirementCompletion Retire(
            in Switch2ProUsbStartupRetirementClaim claim,
            int timeoutMilliseconds) =>
            Switch2ProUsbStartupRetirementCompletion.ProvenNotReleased(
                claim, claim.Reason);
    }

    private sealed class CountingCompletionTarget :
        ISwitch2ProUsbReadCompletionTarget
    {
        private int count;
        internal int Count => Volatile.Read(ref count);

        public Switch2ProUsbReadCompletionDisposition CompleteInputRead(
            in Switch2ProUsbReadClaim claim, int bytesTransferred,
            long completionTimestampQpc,
            Switch2ProUsbNativeReadStatus status)
        {
            Interlocked.Increment(ref count);
            return Switch2ProUsbReadCompletionDisposition.Published;
        }
    }

    private sealed class FailingPumpFactory :
        ISwitch2ProUsbRuntimePumpFactory
    {
        private readonly bool throws;

        internal FailingPumpFactory(bool throws)
        {
            this.throws = throws;
        }

        public bool TryCreate(Switch2ProUsbInputTransportOwner transportOwner,
            int readRetirementTimeoutMilliseconds,
            out ISwitch2ProUsbRuntimeReadPump pump,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            if (throws)
            {
                throw new InvalidOperationException("Synthetic pump fault.");
            }
            pump = null;
            failure = Switch2ProUsbInputReadPumpFailure.WorkerStartRejected;
            return false;
        }
    }

    private sealed class RejectingAttentionPumpFactory :
        ISwitch2ProUsbRuntimePumpFactory
    {
        public bool TryCreate(Switch2ProUsbInputTransportOwner transportOwner,
            int readRetirementTimeoutMilliseconds,
            out ISwitch2ProUsbRuntimeReadPump pump,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            pump = new RejectingAttentionPump(transportOwner);
            failure = Switch2ProUsbInputReadPumpFailure.None;
            return true;
        }
    }

    private sealed class RejectingAttentionPump : ISwitch2ProUsbRuntimeReadPump
    {
        private readonly Switch2ProUsbInputTransportOwner transport;

        internal RejectingAttentionPump(
            Switch2ProUsbInputTransportOwner transport)
        {
            this.transport = transport;
        }

        public Switch2ProUsbInputReadPumpState State =>
            Switch2ProUsbInputReadPumpState.Created;
        public Switch2ProUsbInputReadPumpFailure TerminalFailure => default;
        public Switch2ProUsbDisposeFailure LastDisposeFailure => default;
        public long StartedReadCount => 0;
        public long RetiredReadCount => 0;
        public bool TrySetLifecycleAttentionHandler(
            Action<Switch2ProUsbInputReadPumpFailure> handler) => false;
        public bool TryPrepareStart(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            failure = default;
            return false;
        }
        public bool TryCommitPrepared(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            failure = default;
            return false;
        }
        public bool TryAbortPrepared(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            failure = default;
            return false;
        }
        public bool TryStart(out Switch2ProUsbInputReadPumpFailure failure)
        {
            failure = default;
            return false;
        }
        public bool RequestStop() => transport.RequestStop();
        public bool TryStopAndDispose(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            transport.RequestStop();
            bool disposed = transport.TryQuiesceAndDispose(
                timeoutMilliseconds, out _);
            failure = disposed ? default :
                Switch2ProUsbInputReadPumpFailure.OwnerDisposeRejected;
            return disposed;
        }
    }
}
