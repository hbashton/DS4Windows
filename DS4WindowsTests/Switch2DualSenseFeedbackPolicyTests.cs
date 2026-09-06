using System.Collections;
using System.Reflection;
using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.Switch2;
using DS4WinWPF.DS4Control.DTOXml;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2DualSenseFeedbackPolicyTests
{
    private const int HapticsOffset = 76;

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void IndependentControlsPreserveBodyAndOnlySelectedRichComponents(
        bool audio, bool adaptive)
    {
        byte[] feedback = Feedback();
        Switch2HdRumbleGroup body = Switch2HdRumbleFeedbackTranslator.
            CreateCompatibilityGroup((ushort)(feedback[0] * 257),
                (ushort)(feedback[1] * 257));
        Switch2HdRumbleGroup expectedLeft = body;
        Switch2HdRumbleGroup expectedRight = body;
        if (audio)
        {
            Assert.IsTrue(DualSenseHapticsTranslator.TryTranslateToSwitch2Groups(
                feedback, feedback.Length, HapticsOffset,
                out var pcmLeft, out var pcmRight));
            expectedLeft = DualSenseAdaptiveTriggerHdRumbleTranslator.MixPcmWithCompatibility(pcmLeft, body);
            expectedRight = DualSenseAdaptiveTriggerHdRumbleTranslator.MixPcmWithCompatibility(pcmRight, body);
        }
        if (adaptive)
        {
            Assert.IsTrue(DualSenseAdaptiveTriggerHdRumbleTranslator.TryTranslate(
                feedback.AsSpan(6, 11), out var trigger));
            expectedRight = DualSenseAdaptiveTriggerHdRumbleTranslator.Mix(expectedRight, trigger);
        }
        bool rich = ViiperOutDevice.TryBuildSwitch2DualSenseHdRumbleGroups(
            feedback, feedback.Length, HapticsOffset, false, true,
            out var left, out var right, out var fidelity, audio, adaptive);
        Assert.AreEqual(audio || adaptive, rich);
        if (rich)
        {
            Assert.AreEqual(expectedLeft, left);
            Assert.AreEqual(expectedRight, right);
            Assert.AreEqual(adaptive ? Switch2HdRumbleFeedbackFidelity.
                DualSenseAdaptiveTriggerApproximation :
                Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand, fidelity);
        }

        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(
            _ => new(audio, adaptive), () => owner.Now);
        Assert.IsTrue(Publish(lane, session, feedback));
        Assert.AreEqual(rich, owner.Rich);
        if (rich)
        {
            Assert.AreEqual(expectedLeft, owner.Left);
            Assert.AreEqual(expectedRight, owner.Right);
        }
        else
        {
            Assert.AreEqual((ushort)(feedback[0] * 257), owner.State.BodyLow);
            Assert.AreEqual((ushort)(feedback[1] * 257), owner.State.BodyHigh);
            Assert.IsFalse(owner.State.IsNeutral);
        }
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void LiveDisableKeepsBodyAndOtherLaneWithoutRenewalOrResurrection()
    {
        var policy = Switch2DualSenseConversionPolicy.Default;
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(_ => policy, () => owner.Now);
        byte[] feedback = Feedback();
        Assert.IsTrue(Publish(lane, session, feedback));
        ulong expiry = owner.ExpiresAt;
        byte[] original = (byte[])feedback.Clone();
        Array.Clear(feedback); // The callback buffer is borrowed, never retained.

        owner.Now += 50_000;
        policy = new(false, true);
        Assert.IsTrue(lane.TryRefresh(session, 0, 7));
        Assert.IsTrue(ViiperOutDevice.TryBuildSwitch2DualSenseHdRumbleGroups(
            original, original.Length, HapticsOffset, false, true,
            out var left, out var right, out _, false, true));
        Assert.AreEqual(left, owner.Left);
        Assert.AreEqual(right, owner.Right);
        Assert.AreEqual(expiry, owner.ExpiresAt);

        owner.Now += 50_000;
        policy = new(false, false);
        Assert.IsTrue(lane.TryRefresh(session, 0, 7));
        Assert.IsFalse(owner.Rich);
        Assert.AreEqual((ushort)(original[0] * 257), owner.State.BodyLow);
        Assert.AreEqual((ushort)(original[1] * 257), owner.State.BodyHigh);
        Assert.AreEqual(expiry, owner.ExpiresAt);
        int deliveries = owner.Deliveries;
        policy = Switch2DualSenseConversionPolicy.Default;
        Assert.IsTrue(lane.TryRefresh(session, 0, 7));
        Assert.AreEqual(deliveries, owner.Deliveries,
            "Enabling a lane must not replay an old game event.");

        Assert.IsTrue(Publish(lane, session, original));
        Assert.IsTrue(owner.Rich, "Only fresh feedback may enable the components again.");
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void DisabledPcmCannotReturnThroughCompatibilityDownmix()
    {
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(_ => new(false, true), () => owner.Now);
        byte[] feedback = Feedback();
        feedback[0] = feedback[1] = feedback[6] = 0;
        Assert.IsTrue(Publish(lane, session, feedback));
        Assert.IsFalse(owner.Rich);
        Assert.IsTrue(owner.State.IsNeutral,
            "Disabled audio must not be downmixed back into compatibility body rumble.");
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void MasterDisableReleasesCurrentAndQueuedMediaWithoutOtherPreferenceChange()
    {
        var policy = Switch2DualSenseConversionPolicy.Default;
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(_ => policy, () => owner.Now);
        try
        {
            Assert.IsTrue(Publish(lane, session, Feedback()));
            Assert.IsTrue(Publish(lane, session, Feedback(), delay: 9_999));
            policy = new(true, true, false);
            Assert.IsTrue(lane.TryRefresh(session, 0, 7));
            Assert.IsTrue(owner.State.IsNeutral);
            Assert.AreEqual(0, PendingCount(session));
            policy = Switch2DualSenseConversionPolicy.Default;
            Assert.IsFalse(lane.TryRefresh(session, 0, 7));
            Assert.IsTrue(owner.State.IsNeutral);
        }
        finally { Assert.IsTrue(session.TryRetire()); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NewerSameSessionNeutralEvenFailedDeliveryRevokesCachedRefresh(bool failure)
    {
        var policy = Switch2DualSenseConversionPolicy.Default;
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(_ => policy, () => owner.Now);
        Assert.IsTrue(Publish(lane, session, Feedback()));
        owner.RejectAfterRecord = failure;
        Assert.AreEqual(!failure, session.TryPublish(default(ControllerFeedbackActuatorState)));
        Assert.IsTrue(owner.State.IsNeutral);
        owner.RejectAfterRecord = false;
        int deliveries = owner.Deliveries;
        policy = new(false, true);
        Assert.IsFalse(lane.TryRefresh(session, 0, 7));
        Assert.AreEqual(deliveries, owner.Deliveries);
        Assert.IsTrue(owner.State.IsNeutral);
        Assert.IsTrue(session.TryRetire());
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(9_999)]
    public void FailedClockRetainsOnlyCleanupAuthorityForImmediateDisable(int delay)
    {
        var policy = Switch2DualSenseConversionPolicy.Default;
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(_ => policy, () => owner.Now);
        Assert.IsTrue(Publish(lane, session, Feedback()));
        if (delay != 0)
        {
            Assert.IsTrue(Publish(lane, session, Feedback(), delay));
            Assert.AreEqual(1, PendingCount(session));
        }
        owner.Now = 0;
        Assert.IsFalse(Publish(lane, session, new byte[6]));
        policy = new(false, true);
        int deliveries = owner.Deliveries;
        Assert.IsTrue(lane.TryRefresh(session, 0, 7));
        Assert.AreEqual(deliveries + 1, owner.Deliveries);
        Assert.IsTrue(owner.State.IsNeutral);
        Assert.IsFalse(owner.Rich);
        Assert.AreEqual(0, PendingCount(session));
        owner.Now = 1_010_000;
        policy = Switch2DualSenseConversionPolicy.Default;
        Assert.IsFalse(lane.TryRefresh(session, 0, 7));
        Assert.AreEqual(deliveries + 1, owner.Deliveries);
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void FailedClockCleanupCannotTouchNewerSameSessionPublication()
    {
        var policy = Switch2DualSenseConversionPolicy.Default;
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(_ => policy, () => owner.Now);
        Assert.IsTrue(Publish(lane, session, Feedback()));
        owner.Now = 0;
        Assert.IsFalse(Publish(lane, session, new byte[6]));
        var newer = new ControllerFeedbackActuatorState(23_000, 17_000, 0, 0);
        Assert.IsTrue(session.TryPublish(newer));
        policy = new(false, false);
        int deliveries = owner.Deliveries;
        Assert.IsFalse(lane.TryRefresh(session, 0, 7));
        Assert.AreEqual(deliveries, owner.Deliveries);
        Assert.AreEqual(newer, owner.State);
        Assert.IsTrue(session.TryRetire());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedAdmittedSourceRetainsCleanupButNeverReplay(bool changePolicy)
    {
        var policy = Switch2DualSenseConversionPolicy.Default;
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(_ => policy, () => owner.Now);
        owner.RejectAfterRecord = true;
        Assert.IsFalse(Publish(lane, session, Feedback()));
        Assert.IsTrue(owner.Rich);
        owner.RejectAfterRecord = false;
        if (changePolicy)
        {
            policy = new(false, true);
        }
        Assert.IsTrue(lane.TryRefresh(session, 0, 7));
        Assert.IsFalse(owner.Rich);
        Assert.IsTrue(owner.State.IsNeutral);
        int deliveries = owner.Deliveries;
        policy = Switch2DualSenseConversionPolicy.Default;
        Assert.IsFalse(lane.TryRefresh(session, 0, 7));
        Assert.AreEqual(deliveries, owner.Deliveries);
        Assert.IsTrue(session.TryRetire());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedAdmittedRefreshRetainsNewCleanupAuthorityForMasterDisable(bool disableMaster)
    {
        var policy = Switch2DualSenseConversionPolicy.Default;
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(_ => policy, () => owner.Now);
        Assert.IsTrue(Publish(lane, session, Feedback()));
        owner.RejectAfterRecord = true;
        policy = new(false, true);
        Assert.IsFalse(lane.TryRefresh(session, 0, 7));
        Assert.IsTrue(owner.Rich);
        owner.RejectAfterRecord = false;
        if (disableMaster)
        {
            policy = new(false, true, false);
        }
        Assert.IsTrue(lane.TryRefresh(session, 0, 7));
        Assert.IsFalse(owner.Rich);
        Assert.IsTrue(owner.State.IsNeutral);
        int deliveries = owner.Deliveries;
        policy = Switch2DualSenseConversionPolicy.Default;
        Assert.IsFalse(lane.TryRefresh(session, 0, 7));
        Assert.AreEqual(deliveries, owner.Deliveries);
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void UnadmittedSourceFailureCannotAcquireUnrelatedPublicationWatermark()
    {
        var policy = Switch2DualSenseConversionPolicy.Default;
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(_ => policy, () => owner.Now);
        Assert.IsTrue(Publish(lane, session, Feedback()));
        var newer = new ControllerFeedbackActuatorState(23_000, 17_000, 0, 0);
        Assert.IsTrue(session.TryPublish(newer));
        byte[] feedback = Feedback();
        Assert.IsFalse(lane.TryPublish(session, 0, feedback, feedback.Length,
            HapticsOffset, false, true, -1, false, 10, 0, 7));
        policy = new(false, false);
        int deliveries = owner.Deliveries;
        Assert.IsFalse(lane.TryRefresh(session, 0, 7));
        Assert.AreEqual(deliveries, owner.Deliveries);
        Assert.AreEqual(newer, owner.State);
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void StreamRecoveryRevokesPacketEvenWhenPhysicalSessionSurvives()
    {
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(
            _ => Switch2DualSenseConversionPolicy.Default, () => owner.Now);
        byte[] feedback = Feedback();
        Assert.IsTrue(lane.TryPublish(session, 0, feedback, feedback.Length,
            HapticsOffset, false, true, 100, false, 10, 0, 7, streamGeneration: 1));
        Assert.IsTrue(lane.TryRefresh(session, 0, 7, streamGeneration: 2));
        Assert.IsTrue(owner.State.IsNeutral);
        Assert.IsFalse(lane.TryRefresh(session, 0, 7, streamGeneration: 2));
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void ReleasedPhysicalTriggerIsRemovedOnRefreshWithoutReenabling()
    {
        var policy = Switch2DualSenseConversionPolicy.Default;
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(_ => policy, () => owner.Now);
        byte[] feedback = Feedback();
        Assert.IsTrue(Publish(lane, session, feedback));
        Assert.IsTrue(lane.TryRefresh(session, 0, 7, rightTriggerActive: false));
        Assert.IsTrue(ViiperOutDevice.TryBuildSwitch2DualSenseHdRumbleGroups(
            feedback, feedback.Length, HapticsOffset, false, false,
            out var left, out var right, out _));
        Assert.AreEqual(left, owner.Left);
        Assert.AreEqual(right, owner.Right);
        int deliveries = owner.Deliveries;
        Assert.IsTrue(lane.TryRefresh(session, 0, 7, rightTriggerActive: true));
        Assert.AreEqual(deliveries, owner.Deliveries);
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void FinalDelayedAdmissionRejectsChangedProfilePolicyBeforeWorkerRuns()
    {
        const int slot = Global.TEST_PROFILE_INDEX;
        bool oldAudio = Global.Switch2DualSenseAudioHapticsEnabled[slot];
        bool oldAdaptive = Global.Switch2DualSenseAdaptiveTriggersEnabled[slot];
        bool oldOutput = Global.EnableOutputDataToDS4[slot];
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        try
        {
            Global.Switch2DualSenseAudioHapticsEnabled[slot] = true;
            Global.Switch2DualSenseAdaptiveTriggersEnabled[slot] = true;
            Global.EnableOutputDataToDS4[slot] = true;
            var lane = new Switch2DualSenseFeedbackPolicyLane(
                Switch2DualSenseConversionPolicy.ReadProfile, () => owner.Now);
            byte[] feedback = Feedback();
            Assert.IsTrue(lane.TryPublish(session, slot, feedback, feedback.Length,
                HapticsOffset, false, true, 100, false, 10, 9_999,
                Global.ReadProfileSwitchRevision(slot)));
            object queue = typeof(Switch2VirtualFeedbackSession).GetField("delayedFeedback",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            object scheduled = queue.GetType().GetMethod("Peek")!.Invoke(queue, null)!;
            object captured = scheduled.GetType().GetProperty("Feedback",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scheduled)!;

            // Match DTO/cold profile load: final profile storage changed, but
            // no VM setter or feedback worker callback has run yet.
            Global.EnableOutputDataToDS4[slot] = false;
            Assert.IsTrue((bool)typeof(Switch2VirtualFeedbackSession).GetMethod(
                "DispatchDelayedNoLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(session, new[] { captured })!);
            Assert.IsTrue(owner.State.IsNeutral);
            Assert.IsFalse(owner.Rich);
            Assert.AreEqual(0, PendingCount(session));
        }
        finally
        {
            Assert.IsTrue(session.TryRetire());
            Global.Switch2DualSenseAudioHapticsEnabled[slot] = oldAudio;
            Global.Switch2DualSenseAdaptiveTriggersEnabled[slot] = oldAdaptive;
            Global.EnableOutputDataToDS4[slot] = oldOutput;
        }
    }

    [TestMethod]
    public void FinalDelayedAdmissionRejectsRecoveredStreamBeforeQueueCleanupRuns()
    {
        const int slot = Global.TEST_PROFILE_INDEX;
        bool oldAudio = Global.Switch2DualSenseAudioHapticsEnabled[slot];
        bool oldAdaptive = Global.Switch2DualSenseAdaptiveTriggersEnabled[slot];
        bool oldOutput = Global.EnableOutputDataToDS4[slot];
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        long currentGeneration = 11;
        try
        {
            Global.Switch2DualSenseAudioHapticsEnabled[slot] = true;
            Global.Switch2DualSenseAdaptiveTriggersEnabled[slot] = true;
            Global.EnableOutputDataToDS4[slot] = true;
            var lane = new Switch2DualSenseFeedbackPolicyLane(
                Switch2DualSenseConversionPolicy.ReadProfile, () => owner.Now,
                () => System.Threading.Volatile.Read(ref currentGeneration));
            byte[] feedback = Feedback();
            long profileRevision = Global.ReadProfileSwitchRevision(slot);
            Assert.IsTrue(lane.TryPublish(session, slot, feedback, feedback.Length,
                HapticsOffset, false, true, 100, false, 10, 9_999,
                profileRevision, streamGeneration: 11));
            object queue = typeof(Switch2VirtualFeedbackSession).GetField("delayedFeedback",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            object scheduled = queue.GetType().GetMethod("Peek")!.Invoke(queue, null)!;
            object captured = scheduled.GetType().GetProperty("Feedback",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scheduled)!;

            // Model the exact window after recovery publishes its successor
            // generation but before callback drain / cache refresh can run.
            // The same physical session and unchanged profile remain active.
            System.Threading.Volatile.Write(ref currentGeneration, 12);
            Assert.IsTrue((bool)typeof(Switch2VirtualFeedbackSession).GetMethod(
                "DispatchDelayedNoLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(session, new[] { captured })!);
            Assert.IsTrue(owner.State.IsNeutral);
            Assert.IsFalse(owner.Rich);
            Assert.AreEqual(0, PendingCount(session));
            int deliveries = owner.Deliveries;
            Assert.IsFalse(lane.TryRefresh(session, slot, profileRevision,
                streamGeneration: 12));
            Assert.AreEqual(deliveries, owner.Deliveries);
        }
        finally
        {
            Assert.IsTrue(session.TryRetire());
            Global.Switch2DualSenseAudioHapticsEnabled[slot] = oldAudio;
            Global.Switch2DualSenseAdaptiveTriggersEnabled[slot] = oldAdaptive;
            Global.EnableOutputDataToDS4[slot] = oldOutput;
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FreshDelayedPolicyOrStreamSupersedesOlderQueuedGeneration(bool streamChange)
    {
        const int slot = Global.TEST_PROFILE_INDEX;
        bool oldAudio = Global.Switch2DualSenseAudioHapticsEnabled[slot];
        bool oldAdaptive = Global.Switch2DualSenseAdaptiveTriggersEnabled[slot];
        bool oldOutput = Global.EnableOutputDataToDS4[slot];
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        long currentGeneration = 11;
        try
        {
            Global.Switch2DualSenseAudioHapticsEnabled[slot] = true;
            Global.Switch2DualSenseAdaptiveTriggersEnabled[slot] = true;
            Global.EnableOutputDataToDS4[slot] = true;
            var lane = new Switch2DualSenseFeedbackPolicyLane(
                Switch2DualSenseConversionPolicy.ReadProfile, () => owner.Now,
                () => System.Threading.Volatile.Read(ref currentGeneration));
            byte[] feedback = Feedback();
            long profileRevision = Global.ReadProfileSwitchRevision(slot);
            Assert.IsTrue(lane.TryPublish(session, slot, feedback, feedback.Length,
                HapticsOffset, false, true, 100, false, 10, 9_999,
                profileRevision, streamGeneration: 11));
            if (streamChange)
            {
                System.Threading.Volatile.Write(ref currentGeneration, 12);
            }
            else
            {
                Global.Switch2DualSenseAudioHapticsEnabled[slot] = false;
            }

            // Fresh B wins the feedback gate before the UI/recovery cleanup.
            // Same delay/profile must not append it behind stale generation A.
            Assert.IsTrue(lane.TryPublish(session, slot, feedback, feedback.Length,
                HapticsOffset, false, true, 100, false, 10, 9_999,
                profileRevision, currentGeneration));
            Assert.AreEqual(1, PendingCount(session));
            Assert.IsTrue(lane.TryRefresh(session, slot, profileRevision, currentGeneration));
            object queue = typeof(Switch2VirtualFeedbackSession).GetField("delayedFeedback",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            object scheduled = queue.GetType().GetMethod("Dequeue")!.Invoke(queue, null)!;
            object captured = scheduled.GetType().GetProperty("Feedback",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scheduled)!;
            Assert.IsTrue((bool)typeof(Switch2VirtualFeedbackSession).GetMethod(
                "DispatchDelayedNoLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(session, new[] { captured })!);
            Assert.IsTrue(owner.Rich);
            Assert.IsFalse(owner.State.IsNeutral);
            Assert.IsTrue(ViiperOutDevice.TryBuildSwitch2DualSenseHdRumbleGroups(
                feedback, feedback.Length, HapticsOffset, false, true,
                out var expectedLeft, out var expectedRight, out _,
                audioHapticsEnabled: streamChange, adaptiveTriggersEnabled: true));
            Assert.AreEqual(expectedLeft, owner.Left);
            Assert.AreEqual(expectedRight, owner.Right);
            Assert.AreEqual(0, PendingCount(session));
        }
        finally
        {
            Assert.IsTrue(session.TryRetire());
            Global.Switch2DualSenseAudioHapticsEnabled[slot] = oldAudio;
            Global.Switch2DualSenseAdaptiveTriggersEnabled[slot] = oldAdaptive;
            Global.EnableOutputDataToDS4[slot] = oldOutput;
        }
    }

    [TestMethod]
    public void ExpiredOrProfileChangedRefreshReleasesInsteadOfRenewing()
    {
        foreach (bool changedProfile in new[] { false, true })
        {
            var policy = Switch2DualSenseConversionPolicy.Default;
            var owner = new RecordingOwner();
            var session = owner.CreateSession();
            var lane = new Switch2DualSenseFeedbackPolicyLane(_ => policy, () => owner.Now);
            Assert.IsTrue(Publish(lane, session, Feedback()));
            if (!changedProfile)
            {
                owner.Now = owner.ExpiresAt;
            }
            policy = new(false, true);
            Assert.IsTrue(lane.TryRefresh(session, 0, changedProfile ? 8 : 7));
            Assert.IsTrue(owner.State.IsNeutral);
            Assert.IsFalse(owner.Rich);
            int deliveries = owner.Deliveries;
            policy = Switch2DualSenseConversionPolicy.Default;
            Assert.IsFalse(lane.TryRefresh(session, 0, changedProfile ? 8 : 7));
            Assert.AreEqual(deliveries, owner.Deliveries);
            Assert.IsTrue(session.TryRetire());
        }
    }

    [TestMethod]
    public void DelayedPolicyChangeClearsQueueAndNeverAdvancesFutureMedia()
    {
        var policy = Switch2DualSenseConversionPolicy.Default;
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(_ => policy, () => owner.Now);
        try
        {
            Assert.IsTrue(Publish(lane, session, Feedback()));
            Assert.IsTrue(Publish(lane, session, Feedback(), delay: 9_999));
            Assert.AreEqual(1, PendingCount(session));
            policy = new(false, true);
            Assert.IsTrue(lane.TryRefresh(session, 0, 7));
            Assert.AreEqual(0, PendingCount(session));
            Assert.IsTrue(owner.State.IsNeutral);
            int deliveries = owner.Deliveries;
            // Exercise the real timer callback after cancellation, without a
            // sleep or scheduler dependency. It must find no obsolete work.
            typeof(Switch2VirtualFeedbackSession).GetMethod("DelayedTimerTick",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session,
                    new object[] { null });
            Assert.AreEqual(deliveries, owner.Deliveries);
            policy = Switch2DualSenseConversionPolicy.Default;
            Assert.IsFalse(lane.TryRefresh(session, 0, 7));
            Assert.AreEqual(deliveries, owner.Deliveries);
        }
        finally
        {
            Assert.IsTrue(session.TryRetire());
        }
    }

    [TestMethod]
    public void StaleSessionCannotRefreshSuccessorOrDifferentProfileSlot()
    {
        var policy = Switch2DualSenseConversionPolicy.Default;
        var owner = new RecordingOwner();
        var old = owner.CreateSession();
        var lane = new Switch2DualSenseFeedbackPolicyLane(_ => policy, () => owner.Now);
        Assert.IsTrue(Publish(lane, old, Feedback()));
        Assert.IsTrue(old.TryRetire());
        var successor = owner.CreateSession();
        policy = new(false, false);
        int deliveries = owner.Deliveries;
        Assert.IsFalse(lane.TryRefresh(successor, 0, 7));
        Assert.IsFalse(lane.TryRefresh(old, 0, 7));
        Assert.IsFalse(lane.TryRefresh(old, 1, 7));
        Assert.AreEqual(deliveries, owner.Deliveries);
        Assert.IsTrue(Publish(lane, successor, Feedback()));
        Assert.IsFalse(owner.Rich);
        Assert.IsTrue(successor.TryRetire());
    }

    [TestMethod]
    public void AbsoluteExpiryCannotBeCombinedWithDelayOrExtendedAtFinalOwner()
    {
        Assert.AreEqual(50UL, Switch2VirtualFeedbackSession.RemainingLifetime(100, 150));
        Assert.AreEqual(0UL, Switch2VirtualFeedbackSession.RemainingLifetime(150, 150));
        Assert.AreEqual(0UL, Switch2VirtualFeedbackSession.RemainingLifetime(151, 150));
        Assert.AreEqual(250_000UL, Switch2VirtualFeedbackSession.RemainingLifetime(100, 0));
        var owner = new RecordingOwner();
        var session = owner.CreateSession();
        Assert.IsFalse(session.TryPublish(new ControllerFeedbackActuatorState(1, 0, 0, 0),
            rumbleDelayMilliseconds: 1, expiresAtMicroseconds: 100));
        Assert.AreEqual(0, owner.Deliveries);
        Assert.IsTrue(session.TryRetire());
    }

    [TestMethod]
    public void DefaultOnSchemaMigrationRoundTripAndVmRemainIndependent()
    {
        var store = new BackingStore();
        Assert.IsTrue(store.switch2DualSenseAudioHapticsEnabled.All(value => value));
        Assert.IsTrue(store.switch2DualSenseAdaptiveTriggersEnabled.All(value => value));
        var serializer = new XmlSerializer(typeof(ProfileDTO), ProfileDTO.GetAttributeOverrides());
        using var reader = new StringReader("<DS4Windows config_version=\"5\" />");
        var legacy = (ProfileDTO)serializer.Deserialize(reader)!;
        Assert.IsTrue(legacy.Switch2DualSenseAudioHapticsEnabled);
        Assert.IsTrue(legacy.Switch2DualSenseAdaptiveTriggersEnabled);
        foreach (bool audio in new[] { false, true })
        foreach (bool adaptive in new[] { false, true })
        {
            var input = new ProfileDTO { DeviceIndex = 0,
                Switch2DualSenseAudioHapticsEnabled = audio,
                Switch2DualSenseAdaptiveTriggersEnabled = adaptive };
            input.MapTo(store);
            var output = new ProfileDTO { DeviceIndex = 0, SerializeAppAttrs = false };
            output.MapFrom(store);
            using var writer = new StringWriter();
            serializer.Serialize(writer, output);
            using var roundTripReader = new StringReader(writer.ToString());
            var roundTrip = (ProfileDTO)serializer.Deserialize(roundTripReader)!;
            Assert.AreEqual(audio, roundTrip.Switch2DualSenseAudioHapticsEnabled);
            Assert.AreEqual(adaptive, roundTrip.Switch2DualSenseAdaptiveTriggersEnabled);
        }
        Assert.IsNotNull(typeof(ProfileSettingsViewModel).GetProperty(
            nameof(ProfileSettingsViewModel.Switch2DualSenseAudioHapticsEnabled)));
        Assert.IsNotNull(typeof(ProfileSettingsViewModel).GetProperty(
            nameof(ProfileSettingsViewModel.Switch2DualSenseAdaptiveTriggersEnabled)));
    }

    private static int PendingCount(Switch2VirtualFeedbackSession session) =>
        ((ICollection)typeof(Switch2VirtualFeedbackSession).GetField("delayedFeedback",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session))?.Count ?? 0;

    private static bool Publish(Switch2DualSenseFeedbackPolicyLane lane,
        Switch2VirtualFeedbackSession session, byte[] feedback, int delay = 0) =>
        lane.TryPublish(session, 0, feedback, feedback.Length, HapticsOffset,
            false, true, 100, false, 10, delay, 7);

    private static byte[] Feedback()
    {
        byte[] feedback = new byte[HapticsOffset + 141];
        feedback[0] = 15;
        feedback[1] = 10;
        feedback[6] = 0x26;
        feedback[7] = 0xFF;
        feedback[8] = 0x03;
        feedback[9] = feedback[10] = feedback[11] = 0xFF;
        feedback[12] = 0x3F;
        feedback[15] = 28;
        feedback[HapticsOffset] = 0x32;
        for (int sample = 0; sample < 32; sample++)
        {
            feedback[HapticsOffset + 13 + sample * 2] = (byte)(sample < 10 ? 80 : 0);
            feedback[HapticsOffset + 14 + sample * 2] = (byte)(sample >= 21 ? 80 : 0);
        }
        return feedback;
    }

    private sealed class RecordingOwner : ISwitch2VirtualFeedbackSessionOwner
    {
        internal ulong Now = 1_000_000;
        internal int Deliveries;
        internal bool Rich;
        internal ulong ExpiresAt;
        internal bool RejectAfterRecord;
        internal ControllerFeedbackActuatorState State;
        internal Switch2HdRumbleGroup Left;
        internal Switch2HdRumbleGroup Right;
        private Switch2VirtualFeedbackSession active;
        private ulong epoch;

        internal Switch2VirtualFeedbackSession CreateSession() =>
            active = new(this, null, ++epoch);

        public bool TryPublishAndPump(Switch2VirtualFeedbackSession session,
            ControllerFeedbackIngress ingress, ReadOnlySpan<byte> wire,
            Switch2HdRumbleFeedbackPolicy policy,
            in Switch2HdRumbleImpulseTuning impulseTuning,
            in Switch2HdRumbleBodyTuning bodyTuning) => false;

        public bool TryPublishAndPump(Switch2VirtualFeedbackSession session,
            ControllerFeedbackIngress ingress, in ControllerFeedbackActuatorState state,
            Switch2HdRumbleFeedbackPolicy policy,
            in Switch2HdRumbleImpulseTuning impulseTuning,
            in Switch2HdRumbleBodyTuning bodyTuning, ulong expiresAtMicroseconds = 0) =>
            Record(session, state, false, default, default, expiresAtMicroseconds);

        public bool TryPublishSourcePreservedAndPump(Switch2VirtualFeedbackSession session,
            ControllerFeedbackIngress ingress, in ControllerFeedbackActuatorState state,
            Switch2HdRumbleFeedbackFidelity fidelity, in Switch2HdRumbleGroup left,
            in Switch2HdRumbleGroup right, in Switch2HdRumbleBodyTuning bodyTuning,
            ulong expiresAtMicroseconds = 0) =>
            Record(session, state, true, left, right, expiresAtMicroseconds);

        private bool Record(Switch2VirtualFeedbackSession session,
            in ControllerFeedbackActuatorState state, bool rich,
            in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right, ulong expiry)
        {
            if (!ReferenceEquals(active, session)) return false;
            ulong lifetime = Switch2VirtualFeedbackSession.RemainingLifetime(Now, expiry);
            State = lifetime == 0 ? default : state;
            Rich = rich && lifetime != 0;
            Left = left;
            Right = right;
            ExpiresAt = Now + lifetime;
            Deliveries++;
            return !RejectAfterRecord;
        }

        public bool TryStageImpulseReleasePresentation(Switch2VirtualFeedbackSession session,
            in ControllerFeedbackFrame frame, ushort left, ushort right, ulong revision) => false;
        public bool TryRefreshCurrentPresentation(Switch2VirtualFeedbackSession session,
            in ControllerFeedbackFrame frame, ulong revision) => false;
        public bool TryClearImpulseReleasePresentation(Switch2VirtualFeedbackSession session) =>
            ReferenceEquals(active, session);
        public bool TryRequestPlayerLedMask(Switch2VirtualFeedbackSession session, byte mask) => false;
        public bool TryRefreshXboxOutputPolicy(Switch2VirtualFeedbackSession session,
            in ControllerFeedbackFrame frame, Switch2XboxFeedbackPolicy policy) => false;
        public bool TryRetireSession(Switch2VirtualFeedbackSession session,
            ControllerFeedbackIngress ingress)
        {
            if (!ReferenceEquals(active, session)) return false;
            active = null;
            return true;
        }
    }
}
