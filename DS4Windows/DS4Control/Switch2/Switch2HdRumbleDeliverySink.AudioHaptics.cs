using System;

namespace DS4Windows.Switch2;

internal sealed partial class Switch2HdRumbleDeliverySink
{
    private readonly Func<ulong> feedbackClock;
    private bool TryGetFeedbackTimestamp(out ulong now)
    {
        if (feedbackClock == null) return ControllerFeedbackClock.TryGetTimestampMicroseconds(out now);
        try { now = feedbackClock(); return true; }
        catch { now = 0; return false; }
    }
    private ControllerFeedbackStateLanePump audioCompositionPump;
    private ControllerFeedbackFrame audioFrame;
    private Switch2HdRumbleGroup audioLeft, audioRight;
    private AudioHapticsMode audioMode;
    private ControllerFeedbackFrame consumedAudioFrame;
    private ControllerFeedbackFrame nativeRichFrame;
    private Switch2HdRumbleGroup nativeRichLeft, nativeRichRight;
    private Switch2HdRumbleFeedbackFidelity nativeRichFidelity;
    private ControllerFeedbackFrame consumedNativeStreamFrame;
    private ControllerFeedbackFrame lastAudioPresentationNativeFrame;
    private ControllerFeedbackDelivery preparedAudioDelivery;
    private Switch2HdRumbleFeedbackSynthesis preparedAudioSynthesis;
    private ControllerFeedbackFrame preparedAudioNativeFrame;
    private bool hasPreparedAudio;

    internal void EnableAudioComposition(ControllerFeedbackStateLanePump pump)
    {
        lock (gate) audioCompositionPump = pump;
        pump.EnableLocalAudioComposition();
    }

    internal bool TryStageAudioHaptics(in ControllerFeedbackFrame frame,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right,
        AudioHapticsMode mode)
    {
        lock (gate)
        {
            if (IsRetired || hasUnresolvedDelivery || audioCompositionPump == null ||
                frame.Source != ControllerFeedbackSource.LocalAudioHaptics ||
                frame.Command != ControllerFeedbackCommand.Apply || !frame.HasValidInvariants() ||
                frame.DeviceGeneration != deviceGeneration || frame.TransportGeneration != transportGeneration ||
                mode is not (AudioHapticsMode.Mix or AudioHapticsMode.Replace)) return false;
            audioFrame = frame;
            audioLeft = left;
            audioRight = right;
            audioMode = mode;
            return true;
        }
    }

    private static bool IsStreamedNative(Switch2HdRumbleFeedbackFidelity fidelity) =>
        fidelity is Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand or
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough;

    private void RememberNativeSynthesisNoLock(in ControllerFeedbackFrame frame,
        Switch2HdRumbleFeedbackFidelity fidelity,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right)
    {
        if (!IsStreamedNative(fidelity) &&
            fidelity != Switch2HdRumbleFeedbackFidelity.DualSenseAdaptiveTriggerApproximation) return;
        nativeRichFrame = frame;
        nativeRichLeft = left;
        nativeRichRight = right;
        nativeRichFidelity = fidelity;
    }

    private bool IsConsumedNativeStreamNoLock(in ControllerFeedbackFrame frame) =>
        frame == consumedNativeStreamFrame && frame.Sequence != 0;

    private bool TryComposeAudioPresentation(in ControllerFeedbackDelivery delivery,
        in ControllerFeedbackFrame native, ulong now,
        out Switch2HdRumbleFeedbackSynthesis synthesis)
    {
        lock (gate)
        {
            if (hasUnresolvedDelivery && hasPreparedAudio && preparedAudioDelivery == delivery)
            {
                synthesis = preparedAudioSynthesis;
                return synthesis.IsFreshAt(now);
            }
            synthesis = default;
            var frame = delivery.Frame;
            if (frame != audioFrame || !frame.IsFreshAt(now)) return false;
            bool audioPending = consumedAudioFrame != frame;
            Switch2HdRumbleGroup left = audioPending ? audioLeft : default;
            Switch2HdRumbleGroup right = audioPending ? audioRight : default;
            ulong expiry = frame.TimestampMicroseconds + frame.TimeToLiveMicroseconds;
            if (audioMode == AudioHapticsMode.Mix && native.Sequence != 0 &&
                native.Command == ControllerFeedbackCommand.Apply && native.IsFreshAt(now))
            {
                Switch2HdRumbleGroup nativeLeft = default, nativeRight = default;
                bool hasNative = false;
                if (native == nativeRichFrame)
                {
                    hasNative = !IsStreamedNative(nativeRichFidelity) ||
                        !IsConsumedNativeStreamNoLock(native);
                    if (hasNative)
                    {
                        nativeLeft = Switch2HdRumbleFeedbackTranslator.ScaleSourcePreservedGroup(nativeRichLeft, selectedBodyTuning);
                        nativeRight = Switch2HdRumbleFeedbackTranslator.ScaleSourcePreservedGroup(nativeRichRight, selectedBodyTuning);
                    }
                }
                else
                {
                    var policy = selectedPolicy;
                    var tuning = selectedBodyTuning;
                    if (xboxPolicyRevision != 0 && xboxPolicyFrame == native)
                    {
                        if (!xboxPolicy.OutputEnabled || !xboxPolicy.ImpulseEnabled)
                            policy = Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility;
                        if (!xboxPolicy.OutputEnabled) Switch2HdRumbleBodyTuning.TryCreate(0, out tuning);
                    }
                    var effectiveNative = native;
                    if (hasImpulseReleasePresentation && impulseReleaseFrame == native)
                        TryCreateImpulseReleaseFrame(native, impulseReleaseLeftTrigger,
                            impulseReleaseRightTrigger, out effectiveNative);
                    hasNative = Switch2HdRumbleFeedbackTranslator.TryTranslate(effectiveNative, now,
                        policy, selectedImpulseTuning, tuning, out var nativeSynthesis);
                    if (hasNative)
                    {
                        if (nativeSynthesis.Fidelity == Switch2HdRumbleFeedbackFidelity.SdlBodyCompatibility)
                            nativeSynthesis = Switch2HeldRumblePresentation.FirstActiveFrame(nativeSynthesis);
                        nativeLeft = nativeSynthesis.Left;
                        nativeRight = nativeSynthesis.Right;
                    }
                }
                if (hasNative)
                {
                    left = DualSenseHapticsTranslator.MixSwitch2AudioGroups(nativeLeft, left);
                    right = DualSenseHapticsTranslator.MixSwitch2AudioGroups(nativeRight, right);
                    expiry = Math.Min(expiry, native.TimestampMicroseconds + native.TimeToLiveMicroseconds);
                }
            }
            if (expiry <= now || expiry <= frame.TimestampMicroseconds) return false;
            synthesis = new Switch2HdRumbleFeedbackSynthesis(frame.Source, frame.Command,
                Switch2HdRumbleFeedbackFidelity.LocalAudioHapticsStream, left, right,
                frame.Sequence, frame.DeviceGeneration, frame.TransportGeneration,
                frame.OwnershipEpoch, frame.TimestampMicroseconds, expiry - frame.TimestampMicroseconds);
            preparedAudioDelivery = delivery;
            preparedAudioSynthesis = synthesis;
            preparedAudioNativeFrame = native;
            hasPreparedAudio = true;
            return true;
        }
    }

    private void CommitAudioPresentationNoLock(in ControllerFeedbackDelivery delivery)
    {
        if (delivery.Disposition != ControllerFeedbackDeliveryDisposition.Frame)
        {
            hasPreparedAudio = false;
            return;
        }
        if (delivery.Frame.Source == ControllerFeedbackSource.LocalAudioHaptics && hasPreparedAudio)
        {
            consumedAudioFrame = delivery.Frame;
            lastAudioPresentationNativeFrame = preparedAudioNativeFrame;
            if (preparedAudioNativeFrame == nativeRichFrame && IsStreamedNative(nativeRichFidelity))
                consumedNativeStreamFrame = preparedAudioNativeFrame;
            hasPreparedAudio = false;
        }
        else if (delivery.Origin == ControllerFeedbackPublicationOrigin.NativeGame &&
            delivery.Frame == nativeRichFrame && IsStreamedNative(nativeRichFidelity))
            consumedNativeStreamFrame = delivery.Frame;
    }
}
