using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using DS4Windows;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2ProUsbOwnedCompositeLifecycleBlockerTests
{
    private static int reportCount;

    [TestMethod]
    public void ExistingParticipantCannotAuthenticateOwnedCompositeAuthority()
    {
        Type authorityType = typeof(Switch2ProUsbOwnedCompositeAuthority);
        Type participantContract =
            typeof(ISwitch2RuntimeRegistrationParticipant);
        Type usbParticipant =
            typeof(Switch2ProUsbRuntimeRegistrationParticipant);

        Assert.IsFalse(HasParameter(participantContract, authorityType),
            "The shared participant contract currently has no exact physical " +
            "lease-adoption proof.");
        Assert.IsFalse(HasParameter(usbParticipant, authorityType));
        Assert.IsFalse(usbParticipant.GetMethods(
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic).
            Any(method => method.Name.Contains("Authenticate",
                StringComparison.Ordinal) &&
                method.GetParameters().Any(parameter =>
                    parameter.ParameterType == authorityType)));

        ConstructorInfo[] constructors = usbParticipant.GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic |
            BindingFlags.Public);
        Assert.AreEqual(1, constructors.Length);
        ParameterInfo[] parameters = constructors[0].GetParameters();
        Assert.AreEqual(1, parameters.Length);
        Assert.AreEqual(typeof(Switch2ProUsbRuntimeOwner),
            parameters[0].ParameterType,
            "Same-generation ownership cannot substitute for a credential " +
            "issued when the bundle input facet is actually consumed.");
    }

    [TestMethod]
    public void RuntimeFactoryStillConsumesOnlyReadOnlyNativeAdapter()
    {
        MethodInfo publicFactory = typeof(Switch2ProUsbRuntimeOwner).
            GetMethods(BindingFlags.Static | BindingFlags.Public).
            Single(method => method.Name == "TryCreate");
        Type[] parameterTypes = publicFactory.GetParameters().
            Select(parameter => parameter.ParameterType.IsByRef ?
                parameter.ParameterType.GetElementType() :
                parameter.ParameterType).ToArray();

        CollectionAssert.Contains(parameterTypes,
            typeof(ISwitch2ProUsbNativeAdapter));
        CollectionAssert.DoesNotContain(parameterTypes,
            typeof(ISwitch2ProUsbOwnedCompositeNativeAdapter));
        CollectionAssert.DoesNotContain(parameterTypes,
            typeof(Switch2ProUsbOwnedCompositeLeaseBundle));
        CollectionAssert.DoesNotContain(parameterTypes,
            typeof(Switch2ProUsbOwnedCompositeAuthority));

        MethodInfo open = typeof(ISwitch2ProUsbNativeAdapter).GetMethod(
            nameof(ISwitch2ProUsbNativeAdapter.TryOpenReadOnlyComposite));
        Assert.IsNotNull(open);
        Type[] openParameters = open.GetParameters().Select(parameter =>
            parameter.ParameterType.IsByRef ?
                parameter.ParameterType.GetElementType() :
                parameter.ParameterType).ToArray();
        CollectionAssert.DoesNotContain(openParameters,
            typeof(Switch2PhysicalInputLifetime),
            "The current native handoff cannot bind both generations.");
        CollectionAssert.DoesNotContain(openParameters,
            typeof(Switch2ProUsbOwnedCompositeAuthority));
    }

    [TestMethod]
    public void OwnedCompositeNativeAdapterIsConcreteButDormant()
    {
        Type ownedAdapter = typeof(ISwitch2ProUsbOwnedCompositeNativeAdapter);
        Type[] implementations = typeof(Switch2ProUsbWindowsAdapter).Assembly.
            GetTypes().Where(type => type.IsClass && !type.IsAbstract &&
                ownedAdapter.IsAssignableFrom(type)).ToArray();

        Assert.AreEqual(1, implementations.Length);
        Assert.AreEqual(typeof(Switch2ProUsbWindowsOwnedCompositeAdapter),
            implementations[0]);
        Assert.IsFalse(implementations[0].IsPublic,
            "The transport tranche must not become a public activation seam.");
        Assert.IsFalse(typeof(Switch2ProUsbWindowsAdapter).
            IsAssignableFrom(implementations[0]),
            "The existing read-only adapter must remain a separate path.");
    }

    [TestMethod]
    public void FeedbackBaseRemainsTerminalOnlyAndActivationContractIsDormant()
    {
        MethodInfo[] methods = typeof(ISwitch2ProUsbOwnedFeedbackLifetime).
            GetMethods();
        CollectionAssert.AreEquivalent(new[]
        {
            "Authenticates",
            "AuthenticatesQuiescenceResult",
            "TryNeutralizeAndQuiesce",
        }, methods.Select(method => method.Name).ToArray());
        Assert.IsFalse(methods.Any(method =>
            method.Name.Contains("Prepare", StringComparison.Ordinal) ||
            method.Name.Contains("Commit", StringComparison.Ordinal) ||
            method.Name.Contains("Abort", StringComparison.Ordinal) ||
            method.Name.Contains("Activate", StringComparison.Ordinal)),
            "The current terminal-only hook cannot prove that output remained " +
            "sealed before the input commit linearization point.");

        MethodInfo[] activationMethods = typeof(
            ISwitch2ProUsbOwnedFeedbackActivationLifetime).GetMethods();
        CollectionAssert.Contains(activationMethods.Select(method =>
            method.Name).ToArray(), "TryPrepareActivation");
        CollectionAssert.Contains(activationMethods.Select(method =>
            method.Name).ToArray(), "TryTakeDormantQuiescenceProof");
        CollectionAssert.Contains(activationMethods.Select(method =>
            method.Name).ToArray(), "TryCommitPrepared");
        CollectionAssert.Contains(activationMethods.Select(method =>
            method.Name).ToArray(), "TryAbortPrepared");
        Type[] activationImplementations = typeof(
                Switch2ProUsbWindowsAdapter).Assembly.GetTypes().Where(
                type => type.IsClass && !type.IsAbstract && typeof(
                    ISwitch2ProUsbOwnedFeedbackActivationLifetime).
                    IsAssignableFrom(type)).ToArray();
        Assert.AreEqual(1, activationImplementations.Length);
        Assert.AreEqual(
            typeof(Switch2ProUsbOwnedFeedbackActivationLifetime),
            activationImplementations[0]);
        Assert.IsFalse(activationImplementations[0].IsPublic,
            "The implementation remains dormant/internal and has no production construction path.");

        Type bridgeParameter = typeof(
                Switch2ProUsbOwnedHdRumbleTransportBridge).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic).Single().
            GetParameters()[0].ParameterType;
        Assert.AreEqual(typeof(ISwitch2ProUsbOwnedFeedbackOutputLease),
            bridgeParameter);
        Assert.IsFalse(bridgeParameter.IsAssignableFrom(
            typeof(ISwitch2ProUsbOwnedCompositeLease)),
            "A full input/startup/disposal lease must not be passable to the bridge.");
    }

    [TestMethod]
    public void SharedReportCallbackCarrierIsExactAndZeroAllocationSteadyState()
    {
        Volatile.Write(ref reportCount, 0);
        DS4Device.ReportHandler<EventArgs> report = CountReport;
        Switch2RuntimeRegistrationLifecycleAttentionCallback attention =
            IgnoreAttention;
        var callbacks = new Switch2RuntimeRegistrationCallbacks(report,
            attention);

        Assert.IsTrue(callbacks.IsValid);
        Assert.AreSame(report, callbacks.ReportHandler,
            "A future lifecycle decorator can and must forward this exact " +
            "callback instead of inserting a second report path.");

        for (int index = 0; index < 2_000; index++)
        {
            callbacks.ReportHandler(null, EventArgs.Empty);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            callbacks.ReportHandler(null, EventArgs.Empty);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated,
            "Lifecycle composition must stay off the steady report path.");
        Assert.AreEqual(22_000, Volatile.Read(ref reportCount));
    }

    private static bool HasParameter(Type type, Type parameterType) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic).
            Any(method => method.GetParameters().Any(parameter =>
                (parameter.ParameterType.IsByRef ?
                    parameter.ParameterType.GetElementType() :
                    parameter.ParameterType) == parameterType));

    private static void CountReport(object sender, EventArgs args) =>
        Interlocked.Increment(ref reportCount);

    private static void IgnoreAttention(
        in Switch2RuntimeRegistrationLifecycleAttention attention)
    {
    }
}
