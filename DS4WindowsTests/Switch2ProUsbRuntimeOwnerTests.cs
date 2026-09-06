using System.Buffers.Binary;
using System.Reflection;
using System.Threading;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public partial class Switch2ProUsbRuntimeOwnerTests
{
    private const ulong DeviceGeneration = 701;
    private const ulong TransportGeneration = 1701;
    private const long QpcFrequency = 10_000_000;
    private static readonly Guid ContainerGuid =
        new("147e377b-8aa4-45c8-aee7-77496c3eb1ba");

    [TestMethod]
    public void FactoryComposesDormantExactNoHidInputOnlyLifetime()
    {
        FakeLease lease = new();
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);

        Assert.AreEqual(1, lease.OpenCount);
        Assert.AreEqual(0, lease.BeginCount,
            "Factory construction must not start the native read pump.");
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Created, owner.State);
        Assert.AreSame(owner.RuntimeInputDevice, registration.Device);
        Assert.AreSame(owner, registration.Owner);
        Assert.AreEqual(DeviceGeneration, registration.Generation);
        Assert.AreEqual(InputControllerOwnershipKind.Switch2Runtime,
            registration.OwnershipKind);
        Assert.IsTrue(registration.IsOwnerAuthenticated);
        Assert.IsFalse(registration.HasHidInterface);
        Assert.IsFalse(registration.HasPersistentIdentity);
        Assert.IsNull(owner.RuntimeInputDevice.HidDevice);
        Assert.IsFalse(owner.RuntimeInputDevice.HasHidInterface);
        Assert.IsFalse(owner.RuntimeInputDevice.AllowsPersistentIdentity);
        Assert.IsFalse(owner.RuntimeInputDevice.SupportsPhysicalOutput);

        DS4HapticState haptics = default;
        DS4LightbarState lightbar = default;
        DS4ForceFeedbackState rumble = default;
        owner.RuntimeInputDevice.SetHapticState(ref haptics);
        owner.RuntimeInputDevice.SetLightbarState(ref lightbar);
        owner.RuntimeInputDevice.SetRumbleState(ref rumble);
        owner.RuntimeInputDevice.RefreshCalibration();
        Assert.AreEqual(0, lease.BeginCount);

        owner.RuntimeInputDevice.Report += (_, _) => { };
        Assert.IsTrue(registration.TryStopAndQuiesce(1_000,
            out InputControllerOwnerOperationFailure stopped),
            $"{stopped}: {owner.LastStopFailure.Kind}");
        Assert.IsTrue(registration.TryRemove(out var removed),
            removed.ToString());
    }

    [TestMethod]
    public void ActivationStartsRuntimeBeforeSynchronousReadAndMapsInOrder()
    {
        FakeLease lease = new()
        {
            CompleteSynchronously = true,
            MaximumSuccessfulBegins = 1,
        };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        List<Switch2RuntimeReportKind> reports = new();
        using ManualResetEventSlim regularSeen = new(false);
        owner.RuntimeInputDevice.Report += (sender, args) =>
        {
            Switch2RuntimeReportEventArgs envelope =
                (Switch2RuntimeReportEventArgs)args;
            lock (reports)
            {
                reports.Add(envelope.Kind);
            }
            Assert.AreEqual(DeviceGeneration, envelope.RuntimeGeneration);
            if (envelope.Kind == Switch2RuntimeReportKind.Regular)
            {
                Assert.AreEqual(Switch2RuntimeInputDeviceState.Active,
                    owner.RuntimeInputDevice.RuntimeState,
                    "The runtime device must start before the pump can publish.");
                regularSeen.Set();
            }
        };

        Assert.IsTrue(owner.TryActivate(registration,
            out Switch2ProUsbRuntimeActivationFailure activation),
            activation.ToString());
        Assert.IsTrue(regularSeen.Wait(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1u,
            owner.RuntimeInputDevice.LastPublishedPacketCounter);
        Assert.IsTrue(SpinWait.SpinUntil(() => lease.BeginCount >= 2,
            TimeSpan.FromSeconds(2)), "The completion-driven pump did not rearm.");

        Assert.IsTrue(registration.TryStopAndQuiesce(1_000,
            out InputControllerOwnerOperationFailure stopped),
            $"{stopped}: {owner.LastStopFailure.Kind}");
        lock (reports)
        {
            CollectionAssert.AreEqual(new[]
            {
                Switch2RuntimeReportKind.Regular,
                Switch2RuntimeReportKind.TerminalNeutral,
            }, reports);
        }
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.IsTrue(owner.RuntimeInputDevice.TerminalNeutralReported);
        Assert.IsTrue(registration.TryRemove(out var removed),
            removed.ToString());
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Removed, owner.State);
        Assert.IsFalse(registration.TryRemove(out var repeated));
        Assert.AreEqual(InputControllerOwnerOperationFailure.
            OwnerAuthenticationFailed, repeated);
    }

    [TestMethod]
    public void ActivationRejectsForeignRegistrationWithoutStartingEitherPump()
    {
        FakeLease firstLease = new();
        FakeLease secondLease = new();
        CreateOwner(firstLease, out Switch2ProUsbRuntimeOwner first,
            out InputControllerRegistration firstRegistration);
        CreateOwner(secondLease, out Switch2ProUsbRuntimeOwner second,
            out InputControllerRegistration secondRegistration);

        Assert.IsFalse(first.TryActivate(secondRegistration,
            out Switch2ProUsbRuntimeActivationFailure failure));
        Assert.AreEqual(Switch2ProUsbRuntimeActivationFailure.
            InvalidRegistration, failure);
        Assert.AreEqual(0, firstLease.BeginCount);
        Assert.AreEqual(0, secondLease.BeginCount);

        first.RuntimeInputDevice.Report += (_, _) => { };
        second.RuntimeInputDevice.Report += (_, _) => { };
        Assert.IsTrue(firstRegistration.TryStopAndQuiesce(1_000, out _));
        Assert.IsTrue(secondRegistration.TryStopAndQuiesce(1_000, out _));
    }

    [TestMethod]
    public void TemporaryProfileActionBackpressureWaitsAndRetriesExactFrame()
    {
        FakeLease lease = new FakeLease
        {
            CompleteSynchronously = false,
            // Keep one native read pending so this test isolates temporary
            // profile-action backpressure. A zero-begin fixture is a genuine
            // pump terminal fault and now correctly requests retirement.
            MaximumSuccessfulBegins = 1,
        };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        List<(Switch2RuntimeReportKind Kind, bool Square)> reports = new();
        owner.RuntimeInputDevice.Report += (sender, args) => reports.Add((
            ((Switch2RuntimeReportEventArgs)args).Kind,
            sender.getRawCurrentState().Square));
        Assert.IsTrue(owner.TryActivate(registration, out _));

        using ManualResetEventSlim actionEntered = new(false);
        using ManualResetEventSlim releaseAction = new(false);
        Task action = Task.Run(() => owner.RuntimeInputDevice.
            HaltReportingRunAction(() =>
            {
                actionEntered.Set();
                releaseAction.Wait();
            }));
        Assert.IsTrue(actionEntered.Wait(TimeSpan.FromSeconds(2)));

        Switch2CanonicalInputFrame exactPress = CreateProFrame(
            DeviceGeneration, TransportGeneration, counter: 11,
            buttons: (uint)Switch2ProButton.FaceWest);
        Task<bool> publish = Task.Run(() =>
            ((ISwitch2ProUsbInputSink)owner).TryPublish(exactPress));
        Assert.IsFalse(publish.Wait(30),
            "The exact frame must wait while the profile action owns admission.");
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Active, owner.State);

        releaseAction.Set();
        Assert.IsTrue(action.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(publish.Wait(TimeSpan.FromSeconds(2)) && publish.Result,
            "The same exact frame must retry after admission reopens.");
        Switch2CanonicalInputFrame exactRelease = CreateProFrame(
            DeviceGeneration, TransportGeneration, counter: 12, buttons: 0);
        Assert.IsTrue(((ISwitch2ProUsbInputSink)owner).TryPublish(
            exactRelease));
        Assert.IsTrue(registration.TryStopAndQuiesce(1_000,
            out InputControllerOwnerOperationFailure stopped),
            $"{stopped}: {owner.LastStopFailure.Kind}");
        CollectionAssert.AreEqual(new[]
        {
            (Switch2RuntimeReportKind.Regular, true),
            (Switch2RuntimeReportKind.Regular, false),
            (Switch2RuntimeReportKind.TerminalNeutral, false),
        }, reports,
            "The exact press, later release, and terminal neutral must remain ordered.");
    }

    [TestMethod]
    public void StaleGenerationAndModelFailuresAreNamedAndFailClosed()
    {
        AssertInputRejection(CreateProFrame(DeviceGeneration + 1,
                TransportGeneration),
            Switch2ProUsbRuntimeInputFailure.StaleDeviceGeneration,
            Switch2ProProfileInputFailure.None);
        AssertInputRejection(CreateJoyConFrame(DeviceGeneration,
                TransportGeneration),
            Switch2ProUsbRuntimeInputFailure.ModelMismatch,
            Switch2ProProfileInputFailure.None);

        AssertInputRejection(CreateProFrame(DeviceGeneration,
                TransportGeneration + 1),
            Switch2ProUsbRuntimeInputFailure.StaleTransportGeneration,
            Switch2ProProfileInputFailure.None);
    }

    [TestMethod]
    public void UsbCounterRolloverPublishesReleaseWithoutRetiringRuntime()
    {
        FakeLease lease = new()
        {
            CompleteSynchronously = false,
            MaximumSuccessfulBegins = 1,
        };
        CreateOwner(lease, out var owner, out var registration);
        var reports = new List<(Switch2RuntimeReportKind Kind, bool Square)>();
        int lifecycleAttentionCount = 0;
        owner.LifecycleAttention += (_, _) => Interlocked.Increment(ref lifecycleAttentionCount);
        owner.RuntimeInputDevice.Report += (sender, args) => reports.Add((
            ((Switch2RuntimeReportEventArgs)args).Kind, sender.getRawCurrentState().Square));
        Assert.IsTrue(owner.TryActivate(registration, out _));
        var initial = CreateProFrame(DeviceGeneration, TransportGeneration);
        var session = new Switch2InputSession(initial.Descriptor, initial.Calibration);
        uint[] counters = { 1_431_649, 1_431_653, 2, 6 };
        for (int index = 0; index < counters.Length; index++)
        {
            Assert.IsTrue(session.TryProcess(initial.Descriptor,
                BuildPacket(counters[index], index < 2 ? (uint)Switch2ProButton.FaceWest : 0),
                10_000 + index * 4_000, out var frame, out _));
            Assert.IsTrue(owner.TryPublish(frame), owner.LastInputFailure.ToString());
            Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Active, owner.State);
        }
        Assert.AreEqual(0, Volatile.Read(ref lifecycleAttentionCount));
        Assert.IsTrue(registration.TryStopAndQuiesce(1_000, out _), owner.LastStopFailure.Kind.ToString());
        CollectionAssert.AreEqual(new[]
        {
            (Switch2RuntimeReportKind.Regular, true),
            (Switch2RuntimeReportKind.Regular, true),
            (Switch2RuntimeReportKind.Regular, false),
            (Switch2RuntimeReportKind.Regular, false),
            (Switch2RuntimeReportKind.TerminalNeutral, false),
        }, reports);
    }

    [TestMethod]
    public void FaultySubscriberCannotSuppressLaterSubscriberAndQuarantinesTerminal()
    {
        FakeLease lease = new FakeLease
        {
            CompleteSynchronously = false,
            MaximumSuccessfulBegins = 1,
        };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        List<Switch2RuntimeReportKind> delivered = new();
        using ManualResetEventSlim regularSeen = new(false);
        owner.RuntimeInputDevice.Report += (_, _) =>
            throw new InvalidOperationException("Synthetic subscriber fault.");
        owner.RuntimeInputDevice.Report += (_, args) =>
        {
            Switch2RuntimeReportKind kind =
                ((Switch2RuntimeReportEventArgs)args).Kind;
            lock (delivered)
            {
                delivered.Add(kind);
            }
            if (kind == Switch2RuntimeReportKind.Regular)
            {
                regularSeen.Set();
            }
        };

        Assert.IsTrue(owner.TryActivate(registration, out _));
        Assert.IsFalse(((ISwitch2ProUsbInputSink)owner).TryPublish(
            CreateProFrame(DeviceGeneration, TransportGeneration)));
        Assert.IsTrue(regularSeen.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.LastInputFailure ==
                Switch2ProUsbRuntimeInputFailure.RuntimePublicationRejected,
            TimeSpan.FromSeconds(2)));
        Assert.IsFalse(registration.TryStopAndQuiesce(1_000,
            out InputControllerOwnerOperationFailure stopped));
        Assert.AreEqual(InputControllerOwnerOperationFailure.StopRejected,
            stopped);
        Assert.AreEqual(Switch2ProUsbRuntimeStopFailureKind.
            TerminalDeliveryRejected, owner.LastStopFailure.Kind);
        Assert.IsTrue(owner.RequiresQuarantine);
        lock (delivered)
        {
            CollectionAssert.AreEqual(new[]
            {
                Switch2RuntimeReportKind.Regular,
                Switch2RuntimeReportKind.TerminalNeutral,
            }, delivered);
        }
        Assert.IsFalse(registration.TryRemove(out _));
    }

    [TestMethod]
    public void BlockingTerminalSubscriberTimesOutWithoutOwnerLockAndQuarantineIsSticky()
    {
        FakeLease lease = new FakeLease
        {
            CompleteSynchronously = false,
            MaximumSuccessfulBegins = 0,
        };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        using ManualResetEventSlim terminalEntered = new(false);
        using ManualResetEventSlim releaseTerminal = new(false);
        owner.RuntimeInputDevice.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.TerminalNeutral)
            {
                terminalEntered.Set();
                releaseTerminal.Wait();
            }
        };
        Assert.IsTrue(owner.TryActivate(registration, out _));

        Task<bool> stop = Task.Run(() => registration.TryStopAndQuiesce(40,
            out _));
        Assert.IsTrue(terminalEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsFalse(stop.Result);
        Assert.AreEqual(Switch2ProUsbRuntimeStopFailureKind.
            TerminalPublicationTimedOut, owner.LastStopFailure.Kind);
        Assert.IsTrue(owner.RequiresQuarantine);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Quarantined,
            owner.State);

        releaseTerminal.Set();
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.RuntimeInputDevice.
                TerminalNeutralCompleted, TimeSpan.FromSeconds(2)));
        Assert.IsFalse(registration.TryStopAndQuiesce(1_000, out _),
            "A later callback completion must not silently clear quarantine.");
        Assert.AreEqual(Switch2ProUsbRuntimeStopFailureKind.QuarantineRequired,
            owner.LastStopFailure.Kind);
        Assert.IsFalse(registration.TryRemove(out _));
    }

    [TestMethod]
    public void BlockedNativeRetirementTimesOutBeforeTerminalAndQuarantines()
    {
        FakeLease lease = new FakeLease
        {
            CompleteSynchronously = false,
            MaximumSuccessfulBegins = 1,
            BlockRetirement = true,
        };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration,
            readRetirementTimeoutMilliseconds: 500);
        owner.RuntimeInputDevice.Report += (_, _) => { };
        Assert.IsTrue(owner.TryActivate(registration, out _));
        Assert.IsTrue(lease.RetirementEntered.Wait(TimeSpan.FromSeconds(2)));

        Assert.IsFalse(registration.TryStopAndQuiesce(30,
            out InputControllerOwnerOperationFailure stopped));
        Assert.AreEqual(InputControllerOwnerOperationFailure.StopRejected,
            stopped);
        Assert.AreEqual(Switch2ProUsbRuntimeStopFailureKind.PumpTimedOut,
            owner.LastStopFailure.Kind);
        Assert.IsTrue(owner.RequiresQuarantine);
        Assert.IsFalse(owner.RuntimeInputDevice.TerminalNeutralCompleted,
            "Terminal neutral cannot precede native/pump retirement.");
        Assert.IsFalse(registration.TryRemove(out _));

        lease.AllowRetirement.Set();
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.ReadPump.State ==
                Switch2ProUsbInputReadPumpState.Stopped,
            TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void PumpFactoryRejectionAndExceptionRollbackEveryOpenedLease()
    {
        foreach (bool throws in new[] { false, true })
        {
            FakeLease lease = new();
            FailingPumpFactory pumpFactory = new(throws);
            bool created = TryCreateCore(lease, pumpFactory,
                out Switch2ProUsbRuntimeOwner owner,
                out InputControllerRegistration registration,
                out Switch2ProUsbRuntimeCreateFailure failure);

            Assert.IsFalse(created);
            Assert.IsNull(owner);
            Assert.IsFalse(registration.IsValid);
            Assert.AreEqual(1, lease.OpenCount);
            Assert.AreEqual(1, lease.WaitCount);
            Assert.AreEqual(1, lease.DisposeCount);
            Assert.IsFalse(failure.RequiresQuarantine);
            Assert.AreEqual(throws ?
                    Switch2ProUsbRuntimeCreateFailureKind.DependencyThrew :
                    Switch2ProUsbRuntimeCreateFailureKind.PumpRejected,
                failure.Kind);
        }
    }

    [TestMethod]
    public void FailedRollbackReturnsRetainedQuarantineOwnerAndExactFailure()
    {
        FakeLease lease = new() { WaitResult = false };
        Assert.IsFalse(TryCreateCore(lease, new FailingPumpFactory(false),
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration,
            out Switch2ProUsbRuntimeCreateFailure failure));

        Assert.IsNotNull(owner,
            "An unquiesced opened lease must retain a reachable owner.");
        Assert.IsFalse(registration.IsValid);
        Assert.AreSame(owner, failure.QuarantinedOwner);
        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.AreEqual(Switch2ProUsbRuntimeCreateFailureKind.
            RollbackTimedOut, failure.Kind);
        Assert.AreEqual(Switch2ProUsbDisposeFailure.
            NativeQuiescenceTimedOut, failure.RollbackDisposeFailure);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Quarantined,
            owner.State);
        Assert.AreEqual(0, lease.DisposeCount);
        Assert.IsFalse(owner.TryActivate(owner.Registration, out var denied));
        Assert.AreEqual(Switch2ProUsbRuntimeActivationFailure.
            OwnerAuthenticationFailed, denied);
    }

    [TestMethod]
    public void RuntimeFactoryPropagatesRetainedRejectedLeaseQuarantine()
    {
        FakeLease lease = new() { WaitResult = false };
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));
        Assert.IsFalse(Switch2ProUsbRuntimeOwner.TryCreate(
            new FakeDiscovery(CreateObservation()),
            new RejectedNativeAdapter(lease), DeviceGeneration,
            TransportGeneration, QpcFrequency, calibration, 200,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration,
            out Switch2ProUsbRuntimeCreateFailure failure));

        Assert.IsNull(owner);
        Assert.IsFalse(registration.IsValid);
        Assert.AreEqual(Switch2ProUsbRuntimeCreateFailureKind.
            TransportRejected, failure.Kind);
        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.IsNull(failure.QuarantinedOwner);
        Assert.IsNotNull(failure.TransportFailure.QuarantinedLeaseOwner);
        Assert.AreEqual(Switch2ProUsbDisposeFailure.
            NativeQuiescenceTimedOut,
            failure.TransportFailure.RejectedLeaseDisposeFailure);
        Assert.AreEqual(0, lease.DisposeCount);

        lease.WaitResult = true;
        Assert.IsTrue(failure.TransportFailure.QuarantinedLeaseOwner.
            TryQuiesceAndDispose(100, out var disposeFailure),
            disposeFailure.ToString());
        Assert.AreEqual(1, lease.DisposeCount);
    }

    [TestMethod]
    public void ConcurrentActivationAndStopHaveOneExactTerminalAndNoLeak()
    {
        FakeLease lease = new FakeLease
        {
            CompleteSynchronously = false,
            MaximumSuccessfulBegins = 1,
        };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        int terminalCount = 0;
        owner.RuntimeInputDevice.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.TerminalNeutral)
            {
                Interlocked.Increment(ref terminalCount);
            }
        };

        using Barrier barrier = new(2);
        bool activated = false;
        bool stopped = false;
        Task activate = Task.Run(() =>
        {
            barrier.SignalAndWait();
            activated = owner.TryActivate(registration, out _);
        });
        Task stop = Task.Run(() =>
        {
            barrier.SignalAndWait();
            stopped = registration.TryStopAndQuiesce(1_000, out _);
        });
        Assert.IsTrue(Task.WaitAll(new[] { activate, stop },
            TimeSpan.FromSeconds(3)));

        if (!stopped)
        {
            Assert.AreEqual(Switch2ProUsbRuntimeStopFailureKind.
                OperationAlreadyInProgress, owner.LastStopFailure.Kind);
            stopped = registration.TryStopAndQuiesce(1_000, out _);
        }
        Assert.IsTrue(stopped);
        Assert.IsTrue(activated || owner.State ==
            Switch2ProUsbRuntimeOwnerState.Stopped);
        Assert.AreEqual(1, terminalCount);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.IsFalse(owner.RequiresQuarantine);
    }

    [TestMethod]
    public void ParkedCommitReturnsBeforeBlockingSubscriberAndStopRefusesCycle()
    {
        FakeLease lease = new()
        {
            CompleteSynchronously = true,
            MaximumSuccessfulBegins = 1,
        };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        using ManualResetEventSlim regularEntered = new(false);
        using ManualResetEventSlim releaseRegular = new(false);
        owner.RuntimeInputDevice.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.Regular)
            {
                regularEntered.Set();
                releaseRegular.Wait();
            }
        };

        Assert.IsTrue(owner.TryPrepareActivation(registration, 1_000,
            out Switch2ProUsbRuntimePrepareCredential credential,
            out Switch2ProUsbRuntimePrepareFailure prepareFailure),
            prepareFailure.ToString());
        Assert.AreEqual(0, lease.BeginCount);

        long started = Environment.TickCount64;
        Assert.IsTrue(owner.TryCommitPrepared(credential,
            out Switch2ProUsbRuntimeCommitFailure commitFailure),
            commitFailure.ToString());
        Assert.IsTrue(Environment.TickCount64 - started < 250,
            "Commit must only release the separately parked worker.");
        Assert.IsTrue(regularEntered.Wait(TimeSpan.FromSeconds(2)));

        Assert.IsFalse(registration.TryStopAndQuiesce(40, out _));
        Assert.AreEqual(Switch2ProUsbRuntimeStopFailureKind.
            OperationAlreadyInProgress,
            owner.LastStopFailure.Kind);
        Assert.IsFalse(owner.RequiresQuarantine);
        releaseRegular.Set();
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.ReadPump.State ==
                Switch2ProUsbInputReadPumpState.Stopped,
            TimeSpan.FromSeconds(2)));
        Assert.IsTrue(registration.TryStopAndQuiesce(1_000, out _));
        Assert.IsFalse(owner.RequiresQuarantine);
    }

    [TestMethod]
    public void TableAttachPrecedesFirstReadAndTerminalLeaseIsAcknowledged()
    {
        FakeLease lease = new()
        {
            CompleteSynchronously = true,
            MaximumSuccessfulBegins = 1,
        };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(91, out var tableFailure),
            tableFailure.ToString());
        Assert.IsTrue(table.TryReserve(registration, out var reservation,
            out tableFailure), tableFailure.ToString());
        Assert.IsTrue(table.TryBind(reservation, out var token,
            out tableFailure), tableFailure.ToString());
        InputControllerRetirementClaim retirementClaim = default;
        string callbackFailure = null;
        using ManualResetEventSlim regularSeen = new(false);
        owner.RuntimeInputDevice.Report += (sender, args) =>
        {
            Switch2RuntimeReportKind kind =
                ((Switch2RuntimeReportEventArgs)args).Kind;
            if (kind == Switch2RuntimeReportKind.Regular)
            {
                if (!table.TryAcquireReportLease(token, sender,
                        out InputControllerReportLease reportLease,
                        out InputControllerSlotTableFailure failure))
                {
                    callbackFailure = failure.ToString();
                }
                else
                {
                    reportLease.Dispose();
                }
                regularSeen.Set();
                return;
            }

            if (!retirementClaim.IsValid)
            {
                callbackFailure = "Missing retirement claim";
                return;
            }
            if (!table.TryAcquireTerminalReportLease(retirementClaim, sender,
                    out InputControllerReportLease terminalLease,
                    out InputControllerSlotTableFailure terminalFailure))
            {
                callbackFailure = terminalFailure.ToString();
                return;
            }
            if (!terminalLease.TryAcknowledgeTerminalNeutral(
                    out terminalFailure))
            {
                callbackFailure = terminalFailure.ToString();
            }
            terminalLease.Dispose();
        };

        Assert.IsTrue(owner.TryPrepareActivation(registration, 1_000,
            out Switch2ProUsbRuntimePrepareCredential credential,
            out Switch2ProUsbRuntimePrepareFailure prepareFailure),
            prepareFailure.ToString());
        Assert.AreEqual(InputControllerSlotState.Bound,
            table.GetSnapshot()[0].State);
        Assert.AreEqual(0, lease.BeginCount);
        Assert.AreEqual(0, owner.ReadPump.StartedReadCount);
        Assert.IsTrue(table.TryActivate(token, out tableFailure),
            tableFailure.ToString());
        Assert.IsTrue(owner.TryCommitPrepared(credential,
            out Switch2ProUsbRuntimeCommitFailure commitFailure),
            commitFailure.ToString());
        Assert.IsTrue(regularSeen.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsNull(callbackFailure);

        Assert.IsTrue(table.TryBeginRetire(token, out retirementClaim,
            out tableFailure), tableFailure.ToString());
        Assert.IsTrue(registration.TryStopAndQuiesce(1_000, out var stopped),
            $"{stopped}: {owner.LastStopFailure.Kind}");
        Assert.IsNull(callbackFailure);
        Assert.IsTrue(table.TryWaitForDrain(retirementClaim, 0,
            out tableFailure), tableFailure.ToString());
        Assert.IsTrue(table.TryMarkQuiesced(retirementClaim,
            out tableFailure), tableFailure.ToString());
        Assert.IsTrue(registration.TryRemove(out var removed),
            removed.ToString());
        Assert.IsTrue(table.TryCompleteRemoval(retirementClaim,
            out tableFailure), tableFailure.ToString());
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[0].State);
    }

    [TestMethod]
    public void TableCloseAbortsPreparedWorkerWithoutReadOrReport()
    {
        FakeLease lease = new();
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(92, out var tableFailure));
        Assert.IsTrue(table.TryReserve(registration, out var reservation,
            out tableFailure));
        Assert.IsTrue(table.TryBind(reservation, out _, out tableFailure));
        int reports = 0;
        owner.RuntimeInputDevice.Report += (_, _) =>
            Interlocked.Increment(ref reports);

        Assert.IsTrue(owner.TryPrepareActivation(registration, 1_000,
            out Switch2ProUsbRuntimePrepareCredential credential,
            out Switch2ProUsbRuntimePrepareFailure prepareFailure),
            prepareFailure.ToString());
        Assert.AreEqual(0, lease.BeginCount);
        Assert.IsTrue(table.TryClose(92, out var snapshots,
            out tableFailure), tableFailure.ToString());
        InputControllerSlotSnapshot bound = snapshots.Single(value =>
            value.State == InputControllerSlotState.Bound);

        Assert.IsTrue(owner.TryAbortPrepared(credential, 1_000,
            out Switch2ProUsbRuntimeUnpublishedAbortFailure abortFailure),
            abortFailure.ToString());
        Assert.AreEqual(0, lease.BeginCount);
        Assert.AreEqual(0, owner.ReadPump.StartedReadCount);
        Assert.AreEqual(0, owner.ReadPump.RetiredReadCount);
        Assert.AreEqual(0, reports);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.AbortedUnpublished,
            owner.State);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.AbortedUnpublished,
            owner.RuntimeInputDevice.RuntimeState);
        Assert.IsFalse(owner.RuntimeInputDevice.TerminalNeutralCompleted);
        Assert.IsFalse(registration.IsOwnerAuthenticated);
        Assert.IsTrue(table.TryRollback(bound.SetupRollbackClaim,
            out tableFailure), tableFailure.ToString());
    }

    [TestMethod]
    public void CreatedUnpublishedAbortDisposesAndInvalidatesRegistration()
    {
        FakeLease lease = new();
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        int reports = 0;
        owner.RuntimeInputDevice.Report += (_, _) =>
            Interlocked.Increment(ref reports);

        Assert.IsTrue(owner.TryAbortUnpublished(registration, 1_000,
            out Switch2ProUsbRuntimeUnpublishedAbortFailure abortFailure),
            abortFailure.ToString());
        Assert.AreEqual(0, lease.BeginCount);
        Assert.AreEqual(0, reports);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.IsFalse(registration.IsOwnerAuthenticated);
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(93, out var tableFailure));
        Assert.IsFalse(table.TryReserve(registration, out _,
            out tableFailure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            OwnerAuthenticationFailed, tableFailure);
    }

    [TestMethod]
    public void ExactUsbSlotAdoptionOwnsPrepareAndUnpublishedCleanup()
    {
        FakeLease lease = new();
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        var winner = new InputControllerRegistrationTable(1);
        var foreign = new InputControllerRegistrationTable(1);
        Assert.IsTrue(winner.TryOpen(201, out _));
        Assert.IsTrue(foreign.TryOpen(202, out _));
        Assert.IsTrue(winner.TryReserveAndBind(registration,
            out var winnerToken, out var winnerRollback, out _));
        Assert.IsTrue(foreign.TryReserveAndBind(registration,
            out var foreignToken, out var foreignRollback, out _));

        Assert.IsTrue(owner.TryAdoptBoundSlot(winnerToken,
            out var adoption, out var adoptionFailure),
            adoptionFailure.ToString());
        Assert.IsTrue(owner.TryAdoptBoundSlot(winnerToken,
            out var retry, out adoptionFailure), adoptionFailure.ToString());
        Assert.AreEqual(adoption, retry);
        Assert.IsFalse(owner.TryAdoptBoundSlot(foreignToken,
            out var rejected, out adoptionFailure));
        Assert.IsFalse(rejected.IsValid);
        Assert.AreEqual(Switch2ProUsbRuntimeSlotAdoptionFailure.
            DifferentSlotAlreadyAdopted, adoptionFailure);

        Assert.IsFalse(owner.TryPrepareActivation(registration, 1_000,
            out _, out var legacyPrepare));
        Assert.AreEqual(Switch2ProUsbRuntimePrepareFailure.
            InvalidSlotAdoptionCredential, legacyPrepare);
        Assert.IsFalse(owner.TryAbortUnpublished(registration, 1_000,
            out var legacyAbort));
        Assert.AreEqual(Switch2ProUsbRuntimeUnpublishedAbortFailure.
            InvalidCredential, legacyAbort);
        Assert.IsFalse(owner.TryAbortUnpublished(rejected, 1_000,
            out var foreignAbort));
        Assert.AreEqual(Switch2ProUsbRuntimeUnpublishedAbortFailure.
            InvalidCredential, foreignAbort);

        Assert.IsTrue(owner.TryAbortUnpublished(adoption, 1_000,
            out var exactAbort), exactAbort.ToString());
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.AbortedUnpublished,
            owner.State);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.IsTrue(foreign.TryRollback(foreignRollback, out _));
        Assert.IsTrue(winner.TryRollback(winnerRollback, out _));
    }

    [TestMethod]
    public void ConcurrentCrossTableUsbAdoptionHasExactlyOneWinner()
    {
        CreateOwner(new FakeLease(), out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        var first = new InputControllerRegistrationTable(1);
        var second = new InputControllerRegistrationTable(1);
        Assert.IsTrue(first.TryOpen(203, out _));
        Assert.IsTrue(second.TryOpen(204, out _));
        Assert.IsTrue(first.TryReserveAndBind(registration,
            out var firstToken, out var firstRollback, out _));
        Assert.IsTrue(second.TryReserveAndBind(registration,
            out var secondToken, out var secondRollback, out _));
        using Barrier start = new(2);
        bool firstWon = false;
        bool secondWon = false;
        Switch2ProUsbRuntimeSlotAdoptionCredential firstCredential = default;
        Switch2ProUsbRuntimeSlotAdoptionCredential secondCredential = default;
        Task firstAttempt = Task.Run(() =>
        {
            start.SignalAndWait();
            firstWon = owner.TryAdoptBoundSlot(firstToken,
                out firstCredential, out _);
        });
        Task secondAttempt = Task.Run(() =>
        {
            start.SignalAndWait();
            secondWon = owner.TryAdoptBoundSlot(secondToken,
                out secondCredential, out _);
        });
        Assert.IsTrue(Task.WaitAll(new[] { firstAttempt, secondAttempt },
            TimeSpan.FromSeconds(2)));
        Assert.AreNotEqual(firstWon, secondWon);

        Switch2ProUsbRuntimeSlotAdoptionCredential winnerCredential =
            firstWon ? firstCredential : secondCredential;
        Assert.IsTrue(owner.TryAbortUnpublished(winnerCredential, 1_000,
            out var abortFailure), abortFailure.ToString());
        Assert.IsTrue(first.TryRollback(firstRollback, out _));
        Assert.IsTrue(second.TryRollback(secondRollback, out _));
    }

    [TestMethod]
    public void WorkerStartFailureWhileBoundRollsBackUnpublishedLifetime()
    {
        FakeLease lease = new();
        ParkedPumpFactory pumpFactory = new(
            _ => throw new InvalidOperationException("Synthetic start fault."),
            beforeWorkerPark: null);
        Assert.IsTrue(TryCreateCore(lease, pumpFactory,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration,
            out Switch2ProUsbRuntimeCreateFailure createFailure),
            createFailure.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(94, out var tableFailure));
        Assert.IsTrue(table.TryReserve(registration, out var reservation,
            out tableFailure));
        Assert.IsTrue(table.TryBind(reservation, out _, out tableFailure));
        int reports = 0;
        owner.RuntimeInputDevice.Report += (_, _) =>
            Interlocked.Increment(ref reports);

        Assert.IsFalse(owner.TryPrepareActivation(registration, 1_000,
            out _, out Switch2ProUsbRuntimePrepareFailure prepareFailure));
        Assert.AreEqual(Switch2ProUsbRuntimePrepareFailure.
            PumpPrepareRejected, prepareFailure);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.AbortedUnpublished,
            owner.State);
        Assert.AreEqual(0, lease.BeginCount);
        Assert.AreEqual(0, reports);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.IsTrue(table.TryClose(94, out var snapshots,
            out tableFailure));
        Assert.IsTrue(table.TryRollback(snapshots[0].SetupRollbackClaim,
            out tableFailure), tableFailure.ToString());
    }

    [TestMethod]
    public void PrepareStopRacePreservesExactCredentialAndZeroReads()
    {
        FakeLease lease = new();
        using ManualResetEventSlim workerEntered = new(false);
        using ManualResetEventSlim releaseWorker = new(false);
        ParkedPumpFactory pumpFactory = new(static thread => thread.Start(),
            () =>
            {
                workerEntered.Set();
                releaseWorker.Wait();
            });
        Assert.IsTrue(TryCreateCore(lease, pumpFactory,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration,
            out _));
        Switch2ProUsbRuntimePrepareCredential credential = default;
        Switch2ProUsbRuntimePrepareFailure prepareFailure = default;
        Task<bool> prepare = Task.Run(() => owner.TryPrepareActivation(
            registration, 1_000, out credential, out prepareFailure));
        Assert.IsTrue(workerEntered.Wait(TimeSpan.FromSeconds(2)));
        Task<(bool Stopped, InputControllerOwnerOperationFailure Failure)>
            stop = Task.Run(() =>
            {
                bool stopped = registration.TryStopAndQuiesce(1_000,
                    out InputControllerOwnerOperationFailure failure);
                return (stopped, failure);
            });
        Assert.IsFalse(stop.Wait(30));
        releaseWorker.Set();
        Assert.IsTrue(prepare.Wait(TimeSpan.FromSeconds(2)) && prepare.Result,
            prepareFailure.ToString());
        Assert.IsTrue(stop.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsFalse(stop.Result.Stopped);
        Assert.AreEqual(InputControllerOwnerOperationFailure.StopRejected,
            stop.Result.Failure);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Prepared, owner.State);
        Assert.AreEqual(Switch2ProUsbInputTransportState.Open,
            owner.TransportOwner.State);
        Assert.AreEqual(0, lease.BeginCount);
        Assert.IsFalse(owner.ReadPump.RequestStop(),
            "A generic pump stop must not close a parked transport.");
        Assert.AreEqual(Switch2ProUsbInputTransportState.Open,
            owner.TransportOwner.State);
        Assert.IsTrue(owner.TryAbortPrepared(credential, 1_000,
            out var abortFailure), abortFailure.ToString());
    }

    [TestMethod]
    public void CopiedCredentialCommitAbortRaceHasOneWinnerAndNoReplay()
    {
        for (int iteration = 0; iteration < 20; iteration++)
        {
            FakeLease lease = new()
            {
                CompleteSynchronously = true,
                MaximumSuccessfulBegins = 1,
            };
            CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
                out InputControllerRegistration registration);
            owner.RuntimeInputDevice.Report += (_, _) => { };
            Assert.IsTrue(owner.TryPrepareActivation(registration, 1_000,
                out Switch2ProUsbRuntimePrepareCredential credential,
                out _));
            Switch2ProUsbRuntimePrepareCredential copy = credential;
            using Barrier barrier = new(2);
            Task<bool> commit = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return owner.TryCommitPrepared(credential, out _);
            });
            Task<bool> abort = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return owner.TryAbortPrepared(copy, 1_000, out _);
            });
            Assert.IsTrue(Task.WaitAll(new Task[] { commit, abort },
                TimeSpan.FromSeconds(3)));
            Assert.AreEqual(1, (commit.Result ? 1 : 0) +
                (abort.Result ? 1 : 0));
            Assert.IsFalse(owner.TryCommitPrepared(copy, out _));
            Assert.IsFalse(owner.TryAbortPrepared(credential, 1_000,
                out _));
            if (commit.Result)
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => registration.
                        TryStopAndQuiesce(1_000, out _),
                    TimeSpan.FromSeconds(2)), owner.LastStopFailure.Kind.
                    ToString());
            }
            else
            {
                Assert.AreEqual(0, lease.BeginCount);
                Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.
                    AbortedUnpublished, owner.State);
            }
        }
    }

    [TestMethod]
    public void DefaultCrossOwnerAndWrongGenerationCredentialsFailClosed()
    {
        FakeLease firstLease = new();
        FakeLease secondLease = new();
        CreateOwner(firstLease, out Switch2ProUsbRuntimeOwner first,
            out InputControllerRegistration firstRegistration);
        CreateOwner(secondLease, out Switch2ProUsbRuntimeOwner second,
            out InputControllerRegistration secondRegistration);
        Assert.IsTrue(first.TryPrepareActivation(firstRegistration, 1_000,
            out Switch2ProUsbRuntimePrepareCredential firstCredential,
            out _));
        Assert.IsTrue(second.TryPrepareActivation(secondRegistration, 1_000,
            out Switch2ProUsbRuntimePrepareCredential secondCredential,
            out _));

        Assert.IsFalse(first.TryCommitPrepared(default,
            out Switch2ProUsbRuntimeCommitFailure defaultFailure));
        Assert.AreEqual(Switch2ProUsbRuntimeCommitFailure.InvalidCredential,
            defaultFailure);
        Assert.IsFalse(second.TryCommitPrepared(firstCredential,
            out Switch2ProUsbRuntimeCommitFailure crossOwnerFailure));
        Assert.AreEqual(Switch2ProUsbRuntimeCommitFailure.InvalidCredential,
            crossOwnerFailure);
        var wrongGeneration = new Switch2ProUsbRuntimePrepareCredential(first,
            firstCredential.Fence, DeviceGeneration + 1);
        Assert.IsFalse(first.TryCommitPrepared(wrongGeneration,
            out Switch2ProUsbRuntimeCommitFailure generationFailure));
        Assert.AreEqual(Switch2ProUsbRuntimeCommitFailure.InvalidCredential,
            generationFailure);
        Assert.AreEqual(0, firstLease.BeginCount);
        Assert.AreEqual(0, secondLease.BeginCount);
        Assert.IsTrue(first.TryAbortPrepared(firstCredential, 1_000,
            out _));
        Assert.IsTrue(second.TryAbortPrepared(secondCredential, 1_000,
            out _));
    }

    [TestMethod]
    public void CommitRejectionQuarantinesAndBoundedlyDisposesParkedLifetime()
    {
        FakeLease lease = new();
        ScriptedPumpFactory pumpFactory = new() { RejectCommit = true };
        Assert.IsTrue(TryCreateCore(lease, pumpFactory,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration,
            out _));
        int reports = 0;
        owner.RuntimeInputDevice.Report += (_, _) =>
            Interlocked.Increment(ref reports);
        Assert.IsTrue(owner.TryPrepareActivation(registration, 1_000,
            out Switch2ProUsbRuntimePrepareCredential credential,
            out _));

        long started = Environment.TickCount64;
        Assert.IsFalse(owner.TryCommitPrepared(credential,
            out Switch2ProUsbRuntimeCommitFailure failure));
        Assert.AreEqual(Switch2ProUsbRuntimeCommitFailure.
            QuarantineRequired, failure);
        Assert.IsTrue(Environment.TickCount64 - started < 1_000);
        Assert.IsTrue(owner.RequiresQuarantine);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Quarantined,
            owner.State);
        Assert.AreEqual(Switch2ProUsbInputReadPumpState.Disposed,
            pumpFactory.Pump.State);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(0, reports);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.AbortedUnpublished,
            owner.RuntimeInputDevice.RuntimeState);
        Assert.IsFalse(owner.TryCommitPrepared(credential, out _));
        Assert.IsFalse(registration.TryStopAndQuiesce(1_000, out _));
    }

    [TestMethod]
    public void PrepareRejectsZeroTimeoutWithoutMutatingLifetime()
    {
        FakeLease lease = new();
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        Assert.IsFalse(owner.TryPrepareActivation(registration, 0, out _,
            out Switch2ProUsbRuntimePrepareFailure failure));
        Assert.AreEqual(Switch2ProUsbRuntimePrepareFailure.InvalidTimeout,
            failure);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Created, owner.State);
        Assert.AreEqual(0, lease.BeginCount);
        Assert.IsTrue(owner.TryAbortUnpublished(registration, 1_000,
            out _));
    }

    [TestMethod]
    public void WarmedCanonicalMapperToRuntimePublicationAllocatesNothing()
    {
        FakeLease lease = new();
        ScriptedPumpFactory pumpFactory = new();
        Assert.IsTrue(TryCreateCore(lease, pumpFactory,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration,
            out _));
        owner.RuntimeInputDevice.Report += static (_, _) => { };
        Assert.IsTrue(owner.TryActivate(registration, out _));
        Switch2CanonicalInputFrame frame = CreateProFrame(DeviceGeneration,
            TransportGeneration);
        ISwitch2ProUsbInputSink sink = owner;
        Assert.IsTrue(sink.TryPublish(frame));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 512; index++)
        {
            Assert.IsTrue(sink.TryPublish(frame));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated,
            "Canonical -> Pro profile -> runtime Report must remain allocation-free after warmup.");
        Assert.IsTrue(registration.TryStopAndQuiesce(1_000, out _));
    }

    [TestMethod]
    public void TerminalSchedulingExceptionIsBoundedAndCannotStickLifecycleOperation()
    {
        FakeLease lease = new() { CompleteSynchronously = false };
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));
        Assert.IsTrue(Switch2ProUsbRuntimeOwner.TryCreateCore(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease),
            Switch2ProUsbRuntimePumpFactory.Instance,
            new ThrowingTerminalScheduler(), DeviceGeneration,
            TransportGeneration, QpcFrequency, calibration, 200,
            out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration,
            out Switch2ProUsbRuntimeCreateFailure createFailure),
            createFailure.Kind.ToString());
        owner.RuntimeInputDevice.Report += (_, _) => { };
        Assert.IsTrue(owner.TryActivate(registration, out _));

        Assert.IsFalse(registration.TryStopAndQuiesce(1_000, out _));
        Assert.AreEqual(Switch2ProUsbRuntimeStopFailureKind.DependencyThrew,
            owner.LastStopFailure.Kind);
        Assert.IsTrue(owner.RequiresQuarantine);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.Quarantined,
            owner.State);
        Assert.IsFalse(registration.TryStopAndQuiesce(1_000, out _));
        Assert.AreEqual(Switch2ProUsbRuntimeStopFailureKind.QuarantineRequired,
            owner.LastStopFailure.Kind,
            "The scheduling exception must not leave the operation latch stuck.");
    }

    [TestMethod]
    public void ReentrantStopIsRejectedButExternalStopCanOwnBoundedRetirement()
    {
        FakeLease lease = new()
        {
            CompleteSynchronously = true,
            MaximumSuccessfulBegins = 1,
        };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        List<(bool Stopped,
            InputControllerOwnerOperationFailure Failure)> reentries = new();
        using ManualResetEventSlim regularSeen = new(false);
        owner.RuntimeInputDevice.Report += (_, args) =>
        {
            bool stopped = registration.TryStopAndQuiesce(500,
                out InputControllerOwnerOperationFailure failure);
            lock (reentries)
            {
                reentries.Add((stopped, failure));
            }
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.Regular)
            {
                regularSeen.Set();
            }
        };

        Assert.IsTrue(owner.TryActivate(registration, out _));
        Assert.IsTrue(regularSeen.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(registration.TryStopAndQuiesce(1_000, out _));
        lock (reentries)
        {
            Assert.AreEqual(2, reentries.Count);
            Assert.IsTrue(reentries.All(result => !result.Stopped));
            Assert.IsTrue(reentries.All(result => result.Failure ==
                InputControllerOwnerOperationFailure.StopRejected));
        }
        Assert.IsFalse(owner.RequiresQuarantine);
    }

    [TestMethod]
    public void CompositionSurfaceHasNoOutputOrProductionRegistrationDependency()
    {
        Type type = typeof(Switch2ProUsbRuntimeOwner);
        CollectionAssert.AreEquivalent(new[]
        {
            typeof(ISwitch2ProUsbInputSink),
            typeof(IInputControllerRegistrationOwner),
        }, type.GetInterfaces());
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance |
                     BindingFlags.NonPublic | BindingFlags.Public))
        {
            string name = field.FieldType.FullName ?? field.FieldType.Name;
            Assert.IsFalse(name.Contains("HidDevice",
                StringComparison.Ordinal));
            Assert.IsFalse(name.Contains("ControlService",
                StringComparison.Ordinal));
            Assert.IsFalse(name.Contains("DS4Devices",
                StringComparison.Ordinal));
            Assert.IsFalse(name.Contains("Feedback",
                StringComparison.Ordinal));
            Assert.IsFalse(name.Contains("Output",
                StringComparison.Ordinal));
        }
        Assert.IsFalse(type.GetMethods().Any(method =>
            method.Name.Contains("Rumble", StringComparison.Ordinal) ||
            method.Name.Contains("Haptic", StringComparison.Ordinal) ||
            method.Name.Contains("Light", StringComparison.Ordinal) ||
            method.Name.Contains("Output", StringComparison.Ordinal)));
    }

    private static void AssertInputRejection(
        in Switch2CanonicalInputFrame frame,
        Switch2ProUsbRuntimeInputFailure expected,
        Switch2ProProfileInputFailure expectedMapping)
    {
        FakeLease lease = new FakeLease
        {
            CompleteSynchronously = false,
            // Keep the native read pending until the explicit stop below.
            // Rejecting its first begin races a pump lifecycle failure against
            // the deliberately injected frame and hides its specific cause.
            MaximumSuccessfulBegins = 1,
        };
        CreateOwner(lease, out Switch2ProUsbRuntimeOwner owner,
            out InputControllerRegistration registration);
        owner.RuntimeInputDevice.Report += (_, _) => { };
        Assert.IsTrue(owner.TryActivate(registration, out _));

        Assert.IsFalse(((ISwitch2ProUsbInputSink)owner).TryPublish(frame));
        Assert.AreEqual(expected, owner.LastInputFailure);
        Assert.AreEqual(expectedMapping, owner.LastProfileMappingFailure);
        Assert.AreEqual(Switch2ProUsbRuntimeOwnerState.StopRequested,
            owner.State);
        Assert.IsTrue(registration.TryStopAndQuiesce(1_000, out _),
            owner.LastStopFailure.Kind.ToString());
    }

    private static void CreateOwner(FakeLease lease,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration,
        int readRetirementTimeoutMilliseconds = 200)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));
        bool created = Switch2ProUsbRuntimeOwner.TryCreate(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), DeviceGeneration,
            TransportGeneration, QpcFrequency, calibration,
            readRetirementTimeoutMilliseconds, out owner, out registration,
            out Switch2ProUsbRuntimeCreateFailure failure);
        Assert.IsTrue(created, failure.Kind.ToString());
    }

    private static bool TryCreateCore(FakeLease lease,
        ISwitch2ProUsbRuntimePumpFactory pumpFactory,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2ProUsbRuntimeCreateFailure failure)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.ProController2, DeviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));
        return Switch2ProUsbRuntimeOwner.TryCreateCore(
            new FakeDiscovery(CreateObservation()),
            new FakeNativeAdapter(lease), pumpFactory, DeviceGeneration,
            TransportGeneration, QpcFrequency, calibration, 200,
            out owner, out registration, out failure);
    }

    private static Switch2CanonicalInputFrame CreateProFrame(
        ulong deviceGeneration, ulong transportGeneration, uint counter = 1,
        uint buttons = 0)
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.
            TryCreateProController2Usb(
                Switch2InputProtocolIdentity.NintendoUsbVendorId,
                Switch2InputProtocolIdentity.ProController2UsbProductId,
                Switch2InputProtocolIdentity.
                    AuditedProController2UsbBcdDevice,
                out Switch2InputProtocolIdentity identity));
        return CreateFrame(identity, deviceGeneration, transportGeneration,
            counter, buttons, useUsbPrefix: true);
    }

    private static Switch2CanonicalInputFrame CreateJoyConFrame(
        ulong deviceGeneration, ulong transportGeneration)
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid,
            Switch2GattProperty.Read | Switch2GattProperty.Notify,
            Switch2ControllerModel.JoyCon2Left,
            out Switch2InputProtocolIdentity identity));
        return CreateFrame(identity, deviceGeneration, transportGeneration,
            1, 0, useUsbPrefix: false);
    }

    private static Switch2CanonicalInputFrame CreateFrame(
        in Switch2InputProtocolIdentity identity, ulong deviceGeneration,
        ulong transportGeneration, uint counter, uint buttons,
        bool useUsbPrefix)
    {
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity,
            deviceGeneration, transportGeneration, QpcFrequency,
            out Switch2InputSessionDescriptor descriptor));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            identity.Model, deviceGeneration,
            out Switch2InputCalibrationSnapshot calibration));
        Switch2InputSession session = new(descriptor, calibration);
        byte[] packet = BuildPacket(counter, buttons);
        ReadOnlySpan<byte> observation = useUsbPrefix ? packet :
            packet.AsSpan(1);
        Assert.IsTrue(session.TryProcess(descriptor, observation, counter,
            out Switch2CanonicalInputFrame frame,
            out Switch2InputSessionFailure failure), failure.ToString());
        return frame;
    }

    private static byte[] BuildPacket(uint counter, uint buttons)
    {
        byte[] packet = new byte[Switch2InputCodec.UsbPacketLength];
        packet[0] = (byte)Switch2InputReportKind.Common05;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), counter);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1 + 0x04),
            buttons);
        PackStick(packet.AsSpan(1 + 0x0A, 3), 0x800, 0x800);
        PackStick(packet.AsSpan(1 + 0x0D, 3), 0x800, 0x800);
        return packet;
    }

    private static Switch2ProUsbCompositeObservation CreateObservation()
    {
        Assert.IsTrue(Switch2PhysicalContainerIdentity.TryCreate(ContainerGuid,
            out Switch2PhysicalContainerIdentity container));
        Switch2UsbHidInterfaceObservation input = new(container, 0, 0,
            Switch2UsbBoundDriver.HidClass, 0x0001, 0x0005, 64, 64, 0);
        Switch2UsbPipeObservation bulkOut = new(0x02,
            Switch2UsbPipeTransferType.Bulk, 64, 0);
        Switch2UsbPipeObservation bulkIn = new(0x82,
            Switch2UsbPipeTransferType.Bulk, 64, 0);
        Switch2UsbCommandInterfaceObservation command = new(container, 1, 0,
            Switch2UsbBoundDriver.WinUsb, 2, bulkOut, bulkIn);
        return new Switch2ProUsbCompositeObservation(0x057E, 0x2069, 0x0201,
            container, 1, 1, input, command);
    }

    private static void PackStick(Span<byte> destination, ushort x, ushort y)
    {
        destination[0] = (byte)x;
        destination[1] = (byte)(((x >> 8) & 0x0F) |
            ((y & 0x0F) << 4));
        destination[2] = (byte)(y >> 4);
    }

    private sealed class FakeDiscovery : ISwitch2ProUsbOsDiscoveryAdapter
    {
        private readonly Switch2ProUsbCompositeObservation observation;

        public FakeDiscovery(in Switch2ProUsbCompositeObservation observation)
        {
            this.observation = observation;
        }

        public bool TryObserveComposite(
            out Switch2ProUsbCompositeObservation result)
        {
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

        public bool TryOpenReadOnlyComposite(
            in Switch2PhysicalInputRegistration registration,
            out ISwitch2ProUsbReadOnlyCompositeLease opened)
        {
            lease.OpenCount++;
            lease.AdmittedRegistration = registration;
            opened = lease;
            return true;
        }
    }

    private sealed class RejectedNativeAdapter :
        ISwitch2ProUsbNativeAdapter
    {
        private readonly FakeLease lease;

        public RejectedNativeAdapter(FakeLease lease)
        {
            this.lease = lease;
        }

        public bool TryOpenReadOnlyComposite(
            in Switch2PhysicalInputRegistration registration,
            out ISwitch2ProUsbReadOnlyCompositeLease opened)
        {
            lease.OpenCount++;
            lease.AdmittedRegistration = registration;
            opened = lease;
            return false;
        }
    }

    private sealed class FakeLease : ISwitch2ProUsbReadOnlyCompositeLease
    {
        private byte[] buffer;
        private ISwitch2ProUsbReadCompletionTarget completionTarget;
        private Switch2ProUsbReadClaim currentClaim;
        private int offset;
        private int count;
        private readonly ManualResetEventSlim completionQuiescent = new(false);

        public Switch2PhysicalInputRegistration AdmittedRegistration
        {
            get;
            set;
        }

        public Switch2PhysicalInputRegistration Registration =>
            AdmittedRegistration;

        public int OpenCount { get; set; }

        public int BeginCount { get; private set; }

        public int CancelCount { get; private set; }

        public int RetirementCount { get; private set; }

        public int WaitCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int MaximumSuccessfulBegins { get; set; } = 1;

        public bool CompleteSynchronously { get; set; } = true;

        public bool BlockRetirement { get; set; }

        public bool BlockDispose { get; set; }

        public bool WaitResult { get; set; } = true;

        public ManualResetEventSlim RetirementEntered { get; } = new(false);

        public ManualResetEventSlim AllowRetirement { get; } = new(false);

        public ManualResetEventSlim DisposeEntered { get; } = new(false);

        public ManualResetEventSlim AllowDispose { get; } = new(false);

        public bool TryBeginInputRead(byte[] destination, int offset,
            int count, in Switch2ProUsbReadClaim claim,
            ISwitch2ProUsbReadCompletionTarget completionTarget)
        {
            BeginCount++;
            if (BeginCount > MaximumSuccessfulBegins)
            {
                return false;
            }

            buffer = destination;
            this.offset = offset;
            this.count = count;
            this.completionTarget = completionTarget;
            currentClaim = claim;
            completionQuiescent.Reset();
            if (CompleteSynchronously)
            {
                FillPacket((uint)BeginCount);
                completionTarget.CompleteInputRead(claim, count, BeginCount,
                    Switch2ProUsbNativeReadStatus.Completed);
                completionQuiescent.Set();
            }
            return true;
        }

        public void CompleteNativeFailure(Switch2ProUsbNativeReadStatus status)
        {
            try
            {
                completionTarget.CompleteInputRead(currentClaim, 0, 100, status);
            }
            finally
            {
                completionQuiescent.Set();
            }
        }

        public bool TryCancelInputRead(in Switch2ProUsbReadClaim claim)
        {
            CancelCount++;
            completionQuiescent.Set();
            return true;
        }

        public bool TryRetireCompletedInputRead(
            in Switch2ProUsbReadClaim claim, int timeoutMilliseconds)
        {
            RetirementCount++;
            RetirementEntered.Set();
            if (BlockRetirement)
            {
                AllowRetirement.Wait();
            }
            return completionQuiescent.Wait(timeoutMilliseconds);
        }

        public bool TryWaitForInputQuiescence(int timeoutMilliseconds)
        {
            WaitCount++;
            return WaitResult;
        }

        public void DisposeQuiesced()
        {
            DisposeEntered.Set();
            if (BlockDispose)
            {
                AllowDispose.Wait();
            }
            DisposeCount++;
        }

        private void FillPacket(uint counter)
        {
            Span<byte> packet = buffer.AsSpan(offset, count);
            packet.Clear();
            packet[0] = (byte)Switch2InputReportKind.Common05;
            Span<byte> body = packet.Slice(1);
            BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
            PackStick(body.Slice(0x0A, 3), 0x800, 0x800);
            PackStick(body.Slice(0x0D, 3), 0x800, 0x800);
        }
    }

    private sealed class FailingPumpFactory :
        ISwitch2ProUsbRuntimePumpFactory
    {
        private readonly bool throws;

        public FailingPumpFactory(bool throws)
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
                throw new InvalidOperationException(
                    "Synthetic pump-factory fault.");
            }
            pump = null;
            failure = Switch2ProUsbInputReadPumpFailure.WorkerStartRejected;
            return false;
        }
    }

    private sealed class ParkedPumpFactory :
        ISwitch2ProUsbRuntimePumpFactory
    {
        private readonly Action<Thread> workerStarter;
        private readonly Action beforeWorkerPark;

        public ParkedPumpFactory(Action<Thread> workerStarter,
            Action beforeWorkerPark)
        {
            this.workerStarter = workerStarter;
            this.beforeWorkerPark = beforeWorkerPark;
        }

        public bool TryCreate(Switch2ProUsbInputTransportOwner transportOwner,
            int readRetirementTimeoutMilliseconds,
            out ISwitch2ProUsbRuntimeReadPump pump,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            pump = null;
            if (!Switch2ProUsbInputReadPump.TryCreateCore(transportOwner,
                    readRetirementTimeoutMilliseconds, workerStarter,
                    beforeWorkerPark,
                    out Switch2ProUsbInputReadPump concrete, out failure))
            {
                return false;
            }
            pump = new Switch2ProUsbRuntimeReadPump(concrete);
            return true;
        }
    }

    private sealed class ScriptedPumpFactory :
        ISwitch2ProUsbRuntimePumpFactory
    {
        public bool RejectCommit { get; set; }

        public Switch2ProUsbInputReadPumpFailure AttentionDuringCommit
        {
            get;
            set;
        }

        public bool BlockCommitAfterAttention { get; set; }

        public bool ThrowFirstStartedReadCount { get; set; }

        public ScriptedPump Pump { get; private set; }

        public bool TryCreate(Switch2ProUsbInputTransportOwner transportOwner,
            int readRetirementTimeoutMilliseconds,
            out ISwitch2ProUsbRuntimeReadPump pump,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            Pump = new ScriptedPump(transportOwner)
            {
                RejectCommit = RejectCommit,
                AttentionDuringCommit = AttentionDuringCommit,
                BlockCommitAfterAttention = BlockCommitAfterAttention,
                ThrowFirstStartedReadCount = ThrowFirstStartedReadCount,
            };
            pump = Pump;
            failure = Switch2ProUsbInputReadPumpFailure.None;
            return true;
        }
    }

    private sealed class ScriptedPump : ISwitch2ProUsbRuntimeReadPump
    {
        private readonly Switch2ProUsbInputTransportOwner transport;
        private Action<Switch2ProUsbInputReadPumpFailure>
            lifecycleAttentionHandler;
        private int throwFirstStartedReadCount;

        public ScriptedPump(Switch2ProUsbInputTransportOwner transport)
        {
            this.transport = transport;
        }

        public Func<bool> OnStart { get; set; }

        public bool RejectCommit { get; set; }

        public Switch2ProUsbInputReadPumpFailure AttentionDuringCommit
        {
            get;
            set;
        }

        public bool BlockCommitAfterAttention { get; set; }

        public bool BlockCommitBeforeReturn { get; set; }

        public bool ThrowFirstStartedReadCount
        {
            get => Volatile.Read(ref throwFirstStartedReadCount) != 0;
            set => Volatile.Write(ref throwFirstStartedReadCount,
                value ? 1 : 0);
        }

        public ManualResetEventSlim AttentionRaised { get; } = new(false);

        public ManualResetEventSlim CommitEntered { get; } = new(false);

        public ManualResetEventSlim AllowCommitReturn { get; } = new(false);

        public Switch2ProUsbInputReadPumpState State { get; private set; } =
            Switch2ProUsbInputReadPumpState.Created;

        public Switch2ProUsbInputReadPumpFailure TerminalFailure { get; private set; }

        public Switch2ProUsbDisposeFailure LastDisposeFailure { get; private set; }

        public long StartedReadCount
        {
            get
            {
                if (Interlocked.Exchange(ref throwFirstStartedReadCount,
                        0) != 0)
                {
                    throw new InvalidOperationException(
                        "Synthetic read-count getter fault.");
                }
                return 0;
            }
        }

        public long RetiredReadCount { get; private set; }

        public bool TrySetLifecycleAttentionHandler(
            Action<Switch2ProUsbInputReadPumpFailure> handler)
        {
            if (handler == null || lifecycleAttentionHandler != null)
            {
                return false;
            }
            lifecycleAttentionHandler = handler;
            return true;
        }

        public bool TryPrepareStart(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            State = Switch2ProUsbInputReadPumpState.Prepared;
            failure = Switch2ProUsbInputReadPumpFailure.None;
            return true;
        }

        public bool TryCommitPrepared(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            if (RejectCommit)
            {
                failure = Switch2ProUsbInputReadPumpFailure.
                    ActivationCredentialRejected;
                return false;
            }
            State = Switch2ProUsbInputReadPumpState.Running;
            if (AttentionDuringCommit !=
                Switch2ProUsbInputReadPumpFailure.None)
            {
                lifecycleAttentionHandler?.Invoke(AttentionDuringCommit);
                AttentionRaised.Set();
                if (BlockCommitAfterAttention)
                {
                    AllowCommitReturn.Wait();
                }
            }
            CommitEntered.Set();
            if (BlockCommitBeforeReturn)
            {
                AllowCommitReturn.Wait();
            }
            if (OnStart != null)
            {
                _ = Task.Run(OnStart);
            }
            failure = Switch2ProUsbInputReadPumpFailure.None;
            return true;
        }

        public bool TryAbortPrepared(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure) =>
            TryStopAndDispose(timeoutMilliseconds, out failure);

        public bool TryStart(out Switch2ProUsbInputReadPumpFailure failure)
        {
                return TryPrepareStart(timeoutMilliseconds: 1_000,
                    out failure) &&
                TryCommitPrepared(1_000, out failure);
        }

        public bool RequestStop()
        {
            transport.RequestStop();
            State = Switch2ProUsbInputReadPumpState.StopRequested;
            return true;
        }

        public bool TryStopAndDispose(int timeoutMilliseconds,
            out Switch2ProUsbInputReadPumpFailure failure)
        {
            transport.RequestStop();
            bool stopped = transport.TryQuiesceAndDispose(
                timeoutMilliseconds, out Switch2ProUsbDisposeFailure disposed);
            LastDisposeFailure = disposed;
            if (stopped)
            {
                State = Switch2ProUsbInputReadPumpState.Disposed;
                failure = Switch2ProUsbInputReadPumpFailure.None;
                return true;
            }
            TerminalFailure = Switch2ProUsbInputReadPumpFailure.
                OwnerDisposeRejected;
            failure = TerminalFailure;
            return false;
        }
    }

    private sealed class ThrowingTerminalScheduler :
        ISwitch2ProUsbRuntimeTerminalScheduler
    {
        public bool TrySchedule(
            Func<Switch2TerminalNeutralRequestResult> callback,
            out Task<Switch2TerminalNeutralRequestResult> task)
        {
            task = null;
            throw new InvalidOperationException(
                "Synthetic terminal scheduling fault.");
        }
    }
}
