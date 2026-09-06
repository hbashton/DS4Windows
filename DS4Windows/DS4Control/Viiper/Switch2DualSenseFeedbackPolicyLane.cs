/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The independent default-on conversion preferences follow Switch2Connect
61ac6642ce12fe7217e38a860b14863b18ca7e28 (GPL-3.0-or-later), src/config.py
audio_haptics_enabled/adaptive_triggers_enabled and src/gui.py
open_audio_haptics_settings. Ownership and expiry handling are local to
DS4Windows; no reference transport or bundled binary is reused.
*/

using System;
using DS4Windows.Switch2;

namespace DS4Windows;

internal readonly record struct Switch2DualSenseConversionPolicy(
    bool AudioHapticsEnabled, bool AdaptiveTriggersEnabled,
    bool OutputEnabled = true)
{
    internal static Switch2DualSenseConversionPolicy Default => new(true, true);

    internal static Switch2DualSenseConversionPolicy ReadProfile(int index) => new(
        index >= 0 && index < Global.Switch2DualSenseAudioHapticsEnabled.Length &&
            System.Threading.Volatile.Read(ref Global.Switch2DualSenseAudioHapticsEnabled[index]),
        index >= 0 && index < Global.Switch2DualSenseAdaptiveTriggersEnabled.Length &&
            System.Threading.Volatile.Read(ref Global.Switch2DualSenseAdaptiveTriggersEnabled[index]),
        index >= 0 && index < Global.EnableOutputDataToDS4.Length &&
            System.Threading.Volatile.Read(ref Global.EnableOutputDataToDS4[index]));

    internal Switch2DualSenseConversionPolicy Intersect(
        in Switch2DualSenseConversionPolicy other) => new(
            AudioHapticsEnabled && other.AudioHapticsEnabled,
            AdaptiveTriggersEnabled && other.AdaptiveTriggersEnabled,
            OutputEnabled && other.OutputEnabled);
}

/// <summary>Final no-I/O admission guard for this lane's delayed media only.</summary>
internal readonly record struct Switch2DualSenseDelayedPolicyGuard(
    bool IsBound, int DeviceIndex, long ProfileRevision,
    Switch2DualSenseConversionPolicy Policy,
    Func<long> ReadStreamGeneration = null, long StreamGeneration = 0)
{
    internal bool IsCurrent => !IsBound || (DeviceIndex >= 0 &&
        DeviceIndex < Global.TEST_PROFILE_ITEM_COUNT &&
        Global.ReadProfileSwitchRevision(DeviceIndex) == ProfileRevision &&
        Switch2DualSenseConversionPolicy.ReadProfile(DeviceIndex) == Policy &&
        (ReadStreamGeneration == null || ReadStreamGeneration() == StreamGeneration));
}

/// <summary>
/// Serializes this feedback lane with live profile edits, never with input.
/// Retains one bounded source packet so a disable can remove only its selected
/// components. Re-enabling cannot resurrect them. A refresh uses the original
/// absolute CFBK expiry and the same session owner/pump; it is not a new game
/// event or proof of a physical write. Delayed media is released, not advanced
/// to the present, at a policy-change boundary.
/// </summary>
internal sealed class Switch2DualSenseFeedbackPolicyLane
{
    private readonly object gate = new();
    private readonly byte[] retainedFeedback =
        new byte[ViiperOutDevice.DualSenseAtomicFeedbackLength];
    private readonly Func<int, Switch2DualSenseConversionPolicy> readPolicy;
    private readonly Func<ulong> readClock;
    // One cold method-group delegate per output device, never per packet.
    // The production reader is a single Volatile.Read, with no locks or I/O.
    private readonly Func<long> readStreamGeneration;
    private Switch2VirtualFeedbackSession retainedSession;
    private Switch2DualSenseConversionPolicy retainedPolicy;
    private int retainedDeviceIndex;
    private int retainedLength;
    private int retainedHapticsOffset;
    private bool retainedLeftTriggerActive;
    private bool retainedRightTriggerActive;
    private int retainedBodyStrength;
    private bool retainedXboxCarrierMode;
    private int retainedXboxFrequency;
    private int retainedDelay;
    private long retainedProfileRevision;
    private ulong retainedExpiry;
    private ulong retainedPublicationRevision;
    private long retainedStreamGeneration;

    internal Switch2DualSenseFeedbackPolicyLane(
        Func<int, Switch2DualSenseConversionPolicy> readPolicy,
        Func<ulong> readClock = null, Func<long> readStreamGeneration = null)
    {
        this.readPolicy = readPolicy ?? throw new ArgumentNullException(nameof(readPolicy));
        this.readClock = readClock ?? ReadClock;
        this.readStreamGeneration = readStreamGeneration;
    }

    internal bool TryPublish(Switch2VirtualFeedbackSession session,
        int deviceIndex, byte[] feedback, int feedbackLength,
        int hapticsReportOffset, bool leftTriggerActive,
        bool rightTriggerActive, int bodyStrengthPercent,
        bool xboxBodyCarrierMode, int xboxBodyFrequencyLevel,
        int rumbleDelayMilliseconds, long profileRevision,
        long streamGeneration = 0)
    {
        if (session == null || feedback == null || feedbackLength < 6 ||
            feedbackLength > feedback.Length ||
            feedbackLength > retainedFeedback.Length)
        {
            return false;
        }
        lock (gate)
        {
            // A newer structurally valid source attempt supersedes the source
            // bytes even if its clock/publication subsequently fails. Keep
            // only an exact cleanup receipt for this same session/slot: it can
            // remove a predecessor on disable, but can never replay its media
            // or replace a newer publication.
            retainedLength = 0;
            if (!ReferenceEquals(session, retainedSession) ||
                deviceIndex != retainedDeviceIndex)
            {
                retainedSession = null;
            }
            ulong now = readClock();
            if (now == 0 || now > ulong.MaxValue -
                    ControllerFeedbackFrame.MaxTimeToLiveMicroseconds)
            {
                return false;
            }
            // Read inside the publication gate: a pre-edit callback must not
            // publish an old enable mask after the live disable has returned.
            Switch2DualSenseConversionPolicy policy = readPolicy(deviceIndex);
            bool accepted = Publish(session, feedback, feedbackLength,
                    hapticsReportOffset, leftTriggerActive,
                    rightTriggerActive, policy, bodyStrengthPercent,
                    xboxBodyCarrierMode, xboxBodyFrequencyLevel,
                    rumbleDelayMilliseconds, profileRevision, 0, 0,
                    out ulong publicationRevision, deviceIndex, streamGeneration);
            if (publicationRevision == 0)
            {
                return false;
            }
            // The owner may reject after canonical admission. Its newly
            // consumed watermark permits cleanup only, not successful replay.
            if (accepted)
            {
                feedback.AsSpan(0, feedbackLength).CopyTo(retainedFeedback);
            }
            retainedSession = session;
            retainedPolicy = policy;
            retainedDeviceIndex = deviceIndex;
            retainedLength = accepted ? feedbackLength : 0;
            retainedHapticsOffset = hapticsReportOffset;
            retainedLeftTriggerActive = leftTriggerActive;
            retainedRightTriggerActive = rightTriggerActive;
            retainedBodyStrength = bodyStrengthPercent;
            retainedXboxCarrierMode = xboxBodyCarrierMode;
            retainedXboxFrequency = xboxBodyFrequencyLevel;
            retainedDelay = rumbleDelayMilliseconds;
            retainedProfileRevision = profileRevision;
            retainedExpiry = now + ControllerFeedbackFrame.MaxTimeToLiveMicroseconds;
            retainedPublicationRevision = publicationRevision;
            retainedStreamGeneration = streamGeneration;
            return accepted;
        }
    }

    internal bool TryRefresh(Switch2VirtualFeedbackSession session,
        int deviceIndex, long profileRevision, long streamGeneration = 0,
        bool leftTriggerActive = true, bool rightTriggerActive = true)
    {
        lock (gate)
        {
            if (session == null || !ReferenceEquals(session, retainedSession) ||
                deviceIndex != retainedDeviceIndex)
            {
                return false;
            }
            Switch2DualSenseConversionPolicy policy = retainedPolicy.Intersect(
                readPolicy(deviceIndex));
            bool leftActive = retainedLeftTriggerActive && leftTriggerActive;
            bool rightActive = retainedRightTriggerActive && rightTriggerActive;
            if (retainedLength != 0 && policy == retainedPolicy &&
                profileRevision == retainedProfileRevision &&
                streamGeneration == retainedStreamGeneration &&
                leftActive == retainedLeftTriggerActive && rightActive == retainedRightTriggerActive)
            {
                return true;
            }
            ulong now = readClock();
            bool release = retainedLength == 0 || !policy.OutputEnabled ||
                now == 0 || now >= retainedExpiry ||
                retainedDelay != 0 || profileRevision != retainedProfileRevision ||
                streamGeneration != retainedStreamGeneration;
            ulong publicationRevision;
            bool accepted = release ? session.TryPublishPolicyFeedback(
                default, false, Switch2HdRumbleFeedbackFidelity.Invalid,
                default, default, retainedBodyStrength, retainedXboxCarrierMode,
                retainedXboxFrequency, 0, profileRevision, 0,
                retainedPublicationRevision, out publicationRevision) :
                Publish(session, retainedFeedback, retainedLength,
                    retainedHapticsOffset, leftActive,
                    rightActive, policy, retainedBodyStrength,
                    retainedXboxCarrierMode, retainedXboxFrequency, 0,
                    profileRevision, retainedExpiry, retainedPublicationRevision,
                    out publicationRevision, deviceIndex, streamGeneration);
            if (accepted)
            {
                retainedPolicy = policy;
                retainedPublicationRevision = publicationRevision;
                retainedLeftTriggerActive = leftActive;
                retainedRightTriggerActive = rightActive;
                if (release)
                {
                    retainedLength = 0;
                    retainedSession = null;
                }
            }
            else if (publicationRevision != 0)
            {
                // A narrowed refresh may also fail after owner admission.
                // Retain its new cleanup watermark, never the old replayable
                // packet, so a following master-disable can neutralize exactly
                // this attempted state without touching a newer producer.
                retainedLength = 0;
                retainedPublicationRevision = publicationRevision;
                retainedPolicy = policy;
                retainedProfileRevision = profileRevision;
                retainedStreamGeneration = streamGeneration;
                retainedLeftTriggerActive = leftActive;
                retainedRightTriggerActive = rightActive;
            }
            return accepted;
        }
    }

    internal void Invalidate()
    {
        lock (gate)
        {
            retainedLength = 0;
            retainedSession = null;
        }
    }

    private bool Publish(Switch2VirtualFeedbackSession session,
        byte[] feedback, int feedbackLength, int hapticsReportOffset,
        bool leftTriggerActive, bool rightTriggerActive,
        in Switch2DualSenseConversionPolicy policy, int bodyStrengthPercent,
        bool xboxBodyCarrierMode, int xboxBodyFrequencyLevel,
        int rumbleDelayMilliseconds, long profileRevision,
        ulong expiresAtMicroseconds, ulong expectedPublicationRevision,
        out ulong resultingPublicationRevision, int deviceIndex,
        long streamGeneration)
    {
        bool rich = ViiperOutDevice.TryBuildSwitch2DualSenseHdRumbleGroups(
            feedback, feedbackLength, hapticsReportOffset,
            leftTriggerActive, rightTriggerActive, out var left, out var right,
            out var fidelity, policy.AudioHapticsEnabled,
            policy.AdaptiveTriggersEnabled);
        rich &= policy.OutputEnabled && (HasAmplitude(left) || HasAmplitude(right));
        // Only compact compatibility bytes belong to the body fallback here.
        // The generic translator can downmix PCM into body rumble, which would
        // bypass an explicitly disabled audio-haptic conversion lane.
        ControllerFeedbackActuatorState effectiveState = !policy.OutputEnabled ?
            default : rich ? new ControllerFeedbackActuatorState(1, 0, 0, 0) :
            new ControllerFeedbackActuatorState((ushort)(feedback[0] * 257),
                (ushort)(feedback[1] * 257), 0, 0);
        return session.TryPublishPolicyFeedback(effectiveState, rich, fidelity,
            left, right, bodyStrengthPercent, xboxBodyCarrierMode,
            xboxBodyFrequencyLevel, policy.OutputEnabled ? rumbleDelayMilliseconds : 0,
            profileRevision, expiresAtMicroseconds, expectedPublicationRevision,
            out resultingPublicationRevision, new Switch2DualSenseDelayedPolicyGuard(
                true, deviceIndex, profileRevision, policy,
                readStreamGeneration, streamGeneration));
    }

    private static bool HasAmplitude(in Switch2HdRumbleGroup group) =>
        group.First.HasNonzeroAmplitude || group.Second.HasNonzeroAmplitude ||
        group.Third.HasNonzeroAmplitude;

    private static ulong ReadClock() =>
        ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now) ? now : 0;
}
