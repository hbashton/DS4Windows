using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

public partial class Switch2ProUsbRuntimeOwnerTests
{
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UsbCompositeColdStopWaitsForMaintenanceOnlyWithinItsDeadline(
        bool releaseBeforeDeadline)
    {
        CreateOwnedCreationInputs(out List<string> events, out var lifetime,
            out var calibration, out var baseLease, out _, out _,
            out var pumpFactory);
        using var lease = new HeldMaintenanceCompositeLease(baseLease, events);
        Assert.IsTrue(Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
            lifetime, out var bundle, out var admissionFailure), admissionFailure.ToString());
        Assert.IsTrue(bundle.TryTakeAuthority(out var authority));
        Assert.IsTrue(Switch2ProUsbOwnedFeedbackActivationLifetime.TryCreate(
            bundle, authority, 1, out var feedback, out var feedbackFailure),
            feedbackFailure.Failure.ToString());
        Assert.IsTrue(Switch2ProUsbOwnedCompositeRegistrationParticipant.TryCreateCore(
            bundle, authority, calibration, feedback, 500, pumpFactory,
            ImmediateOwnedTerminalScheduler.Instance, 0, out var participant,
            out var participantFailure), participantFailure.Kind.ToString());
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        Assert.IsTrue(core.TryOpen(82, out _));
        Assert.IsTrue(core.TryAttach(participant.Registration, () => participant,
            IgnoreOwnedMapping, 2_000, out var token, out var attachFailure),
            attachFailure.Kind.ToString());

        // Drive the real lifetime's maintenance transaction deterministically;
        // no ThreadPool timer is needed to reproduce the collision. The actual
        // runtime/participant/core and canonical virtual feedback session remain
        // in the composition, including their production ownership locks.
        feedback.SetRumbleMaintenanceWake(null);
        Assert.IsTrue(feedback.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(
            ControllerFeedbackSource.XboxOneVirtualDevice, ControllerFeedbackCommand.Apply,
            ControllerFeedbackActuators.All, 20_000, 10_000, 0, 0, 1,
            DeviceGeneration, TransportGeneration, session.OwnershipEpoch,
            now, 250_000, out var frame));
        var wire = new byte[ControllerFeedbackFrame.SerializedLength];
        Assert.IsTrue(frame.TryWriteTo(wire));
        Assert.IsTrue(session.TryPublish(wire));
        lease.HoldNextWrite();
        var maintenance = Task.Factory.StartNew(
            () => feedback.TryServiceRumbleMaintenance(now + 12_000, null),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        using var stopStarted = new ManualResetEventSlim();
        Task<bool> stop = null;
        try
        {
            Assert.IsTrue(lease.WriteEntered.Wait(2_000), "Maintenance did not enter the physical write.");
            stop = Task.Factory.StartNew(() =>
            {
                stopStarted.Set();
                return core.TryRemove(token, releaseBeforeDeadline ? 2_000 : 100, out _);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.IsTrue(stopStarted.Wait(2_000));
            if (releaseBeforeDeadline)
            {
                Assert.IsFalse(stop.Wait(50),
                    "Transient feedback contention must not permanently quarantine the slot.");
                Assert.AreEqual(0, baseLease.StartupRetirementCount);
                Assert.AreEqual(0, baseLease.CompositeDisposeCount);
                lease.AllowWrite.Set();
                Assert.IsTrue(stop.Wait(2_000), "Removal did not finish after the admitted write completed.");
                Assert.IsTrue(stop.Result, participant.LastStopPhase);
                Assert.AreEqual(InputControllerSlotState.Removed, table.GetSnapshot()[token.Slot].State);
                Assert.AreEqual(Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ExactNeutralAndQuiescent,
                    participant.LastFeedbackQuiescenceResult.Outcome);
                Assert.AreEqual(1, baseLease.StartupRetirementCount);
                Assert.AreEqual(1, baseLease.CompositeDisposeCount);
                Assert.AreEqual(1, events.Count(value => value == "feedback.neutral"),
                    "Waiting for a held write must not duplicate the terminal physical Stop.");
                AssertOrdered(events, "feedback.frame", "feedback.neutral", "startup.retire",
                    "input.stop", "input.quiesce", "composite.dispose");
            }
            else
            {
                Assert.IsTrue(stop.Wait(2_000), "The original cleanup deadline was not honored.");
                Assert.IsFalse(stop.Result);
                Assert.AreEqual(InputControllerSlotState.Quarantined, table.GetSnapshot()[token.Slot].State);
                Assert.AreEqual(0, baseLease.StartupRetirementCount);
                Assert.AreEqual(0, baseLease.CompositeDisposeCount);
                Assert.IsFalse(events.Contains("input.stop"));
                Assert.IsFalse(events.Contains("feedback.neutral"));
            }
        }
        finally
        {
            lease.AllowWrite.Set();
            Assert.IsTrue(maintenance.Wait(2_000));
            if (stop != null) Assert.IsTrue(stop.Wait(2_000));
            // Release only this test's feedback resources after assertions; a
            // failed core removal must retain its quarantine and composite.
            feedback.TryNeutralizeAndQuiesce(authority, 100);
        }
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, maintenance.Result);
        if (!releaseBeforeDeadline)
        {
            Assert.AreEqual(InputControllerSlotState.Quarantined, table.GetSnapshot()[token.Slot].State);
            Assert.AreEqual(0, baseLease.CompositeDisposeCount,
                "Late write completion cannot retroactively authorize whole-composite disposal.");
        }
    }

    /// <summary>
    /// Reuses the existing composite startup/input fixture and replaces only
    /// its output facet with a one-shot adopted, synchronously held write.
    /// No native handle, timer, controller, or retained-I/O simulation is used.
    /// </summary>
    private sealed class HeldMaintenanceCompositeLease : ISwitch2ProUsbOwnedCompositeLease, IDisposable
    {
        private readonly OwnedCompositeLease inner;
        private readonly List<string> events;
        private object outputOwner;
        private int holdNextWrite;

        internal HeldMaintenanceCompositeLease(OwnedCompositeLease inner, List<string> events)
        {
            this.inner = inner;
            this.events = events;
        }

        internal ManualResetEventSlim WriteEntered { get; } = new();
        internal ManualResetEventSlim AllowWrite { get; } = new();
        internal void HoldNextWrite() => Volatile.Write(ref holdNextWrite, 1);
        public Switch2PhysicalInputRegistration Registration => inner.Registration;
        public Switch2PhysicalInputLifetime Lifetime => inner.Lifetime;
        public int MaximumOutputOperationMilliseconds => inner.MaximumOutputOperationMilliseconds;
        public bool AuthenticatesComposite(Switch2ControllerModel model, ulong deviceGeneration,
            ulong transportGeneration) => inner.AuthenticatesComposite(model, deviceGeneration, transportGeneration);

        public bool TryAdoptDormantFeedbackOutput(object ownerFence,
            out ISwitch2ProUsbOwnedFeedbackOutputLease outputLease)
        {
            outputLease = null;
            if (ownerFence == null || Interlocked.CompareExchange(ref outputOwner, ownerFence, null) != null)
                return false;
            outputLease = new HeldOutput(this, ownerFence);
            return true;
        }

        public Switch2ProUsbOwnedOutputWriteAttempt TryWriteReportBounded(ReadOnlySpan<byte> report,
            Switch2ControllerModel model, ulong deviceGeneration, ulong transportGeneration,
            int timeoutMilliseconds) => Reject(model, deviceGeneration, transportGeneration);
        public Switch2ProUsbOwnedOutputRetirementResult TryRetireOutputOperation(
            in Switch2ProUsbOwnedOutputOperationClaim claim, int timeoutMilliseconds) =>
            Switch2ProUsbOwnedOutputRetirementResult.Reject(claim);
        public Switch2ProUsbStartupCommandCompletion Execute(in Switch2ProUsbStartupCommandClaim claim,
            ReadOnlySpan<byte> request, int timeoutMilliseconds) => inner.Execute(claim, request, timeoutMilliseconds);
        public Switch2ProUsbStartupRetirementCompletion Retire(in Switch2ProUsbStartupRetirementClaim claim,
            int timeoutMilliseconds) => inner.Retire(claim, timeoutMilliseconds);
        public bool TryBeginInputRead(byte[] destination, int offset, int count,
            in Switch2ProUsbReadClaim claim, ISwitch2ProUsbReadCompletionTarget target) =>
            inner.TryBeginInputRead(destination, offset, count, claim, target);
        public bool TryCancelInputRead(in Switch2ProUsbReadClaim claim) => inner.TryCancelInputRead(claim);
        public bool TryRetireCompletedInputRead(in Switch2ProUsbReadClaim claim, int timeoutMilliseconds) =>
            inner.TryRetireCompletedInputRead(claim, timeoutMilliseconds);
        public bool TryWaitForInputQuiescence(int timeoutMilliseconds) => inner.TryWaitForInputQuiescence(timeoutMilliseconds);
        public void DisposeQuiesced()
        {
            inner.DisposeQuiesced();
            outputOwner = null;
        }
        public void Dispose()
        {
            WriteEntered.Dispose();
            AllowWrite.Dispose();
        }

        private static Switch2ProUsbOwnedOutputWriteAttempt Reject(Switch2ControllerModel model,
            ulong deviceGeneration, ulong transportGeneration) => new(
            Switch2ProUsbHdRumbleTransportWriteResult.Reject(model, deviceGeneration, transportGeneration,
                Switch2ProUsbHdRumbleTransportWriteFailure.TransportEnded), default);

        private sealed class HeldOutput : ISwitch2ProUsbOwnedFeedbackOutputLease
        {
            private readonly HeldMaintenanceCompositeLease owner;
            private readonly object fence;
            internal HeldOutput(HeldMaintenanceCompositeLease owner, object fence)
            {
                this.owner = owner;
                this.fence = fence;
            }
            public int MaximumOutputOperationMilliseconds => owner.MaximumOutputOperationMilliseconds;
            public bool AuthenticatesComposite(Switch2ControllerModel model, ulong deviceGeneration,
                ulong transportGeneration) => ReferenceEquals(owner.outputOwner, fence) &&
                owner.AuthenticatesComposite(model, deviceGeneration, transportGeneration);
            public bool AuthenticatesOutputOperationClaim(in Switch2ProUsbOwnedOutputOperationClaim claim) => false;
            public Switch2ProUsbOwnedOutputRetirementResult TryRetireOutputOperation(
                in Switch2ProUsbOwnedOutputOperationClaim claim, int timeoutMilliseconds) =>
                Switch2ProUsbOwnedOutputRetirementResult.Reject(claim);
            public Switch2ProUsbOwnedOutputWriteAttempt TryWriteReportBounded(ReadOnlySpan<byte> report,
                Switch2ControllerModel model, ulong deviceGeneration, ulong transportGeneration,
                int timeoutMilliseconds)
            {
                if (!AuthenticatesComposite(model, deviceGeneration, transportGeneration))
                    return Reject(model, deviceGeneration, transportGeneration);
                Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(report,
                    out _, out var left, out var right, out _));
                owner.events.Add(left.Equals(default(Switch2HdRumbleGroup)) &&
                    right.Equals(default(Switch2HdRumbleGroup)) ? "feedback.neutral" : "feedback.frame");
                if (Interlocked.Exchange(ref owner.holdNextWrite, 0) != 0)
                {
                    owner.WriteEntered.Set();
                    Assert.IsTrue(owner.AllowWrite.Wait(5_000), "The test did not release the held write.");
                }
                return new Switch2ProUsbOwnedOutputWriteAttempt(
                    Switch2ProUsbHdRumbleTransportWriteResult.Complete(model, deviceGeneration,
                        transportGeneration, report.Length), default);
            }
        }
    }
}
