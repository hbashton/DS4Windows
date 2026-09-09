/* DS4Windows, Copyright (C) 2026 hbashton. GPL-3.0-or-later. */
using System;
using System.Collections.Generic;
using System.Threading;
using DS4Windows.Switch2;

namespace DS4Windows.InputDevices;

internal sealed record LegacyJoyConHdRumbleAuthority(LegacyJoyConGroup Group, long ProfileRevision);

internal enum LegacyJoyConHdRumbleSource { Generic, DualSenseControl, DualSensePcm }

/// <summary>
/// Legacy Nintendo protocol boundary for DS4Windows-authored synthesis only.
/// Never accepts raw Switch 2 reports. Original Joy-Cons have one two-band
/// oscillator description per actuator, not three timed Switch 2 subframes.
/// The strongest authored slice supplies each band's amplitude and carrier;
/// this is a peak-preserving temporal approximation, not PCM passthrough.
/// </summary>
internal static class LegacyJoyConHdRumble
{
    internal static Switch2HdRumbleSubframe Peak(in Switch2HdRumbleGroup group)
    {
        var high = group.First;
        var low = group.First;
        if (group.Second.Oscillator0AmplitudeCode > high.Oscillator0AmplitudeCode) high = group.Second;
        if (group.Third.Oscillator0AmplitudeCode > high.Oscillator0AmplitudeCode) high = group.Third;
        if (group.Second.Oscillator1AmplitudeCode > low.Oscillator1AmplitudeCode) low = group.Second;
        if (group.Third.Oscillator1AmplitudeCode > low.Oscillator1AmplitudeCode) low = group.Third;
        return new(high.Oscillator0ControlCode, high.Oscillator0AmplitudeCode,
            low.Oscillator1ControlCode, low.Oscillator1AmplitudeCode);
    }

    internal static Switch2HdRumbleGroup FoldStandalone(in Switch2HdRumbleGroup left,
        in Switch2HdRumbleGroup right) => new(Fold(left.First, right.First),
            Fold(left.Second, right.Second), Fold(left.Third, right.Third));

    private static Switch2HdRumbleSubframe Fold(in Switch2HdRumbleSubframe left,
        in Switch2HdRumbleSubframe right) => new(
            left.Oscillator0AmplitudeCode >= right.Oscillator0AmplitudeCode ? left.Oscillator0ControlCode : right.Oscillator0ControlCode,
            Math.Max(left.Oscillator0AmplitudeCode, right.Oscillator0AmplitudeCode),
            left.Oscillator1AmplitudeCode >= right.Oscillator1AmplitudeCode ? left.Oscillator1ControlCode : right.Oscillator1ControlCode,
            Math.Max(left.Oscillator1AmplitudeCode, right.Oscillator1AmplitudeCode));

    internal static bool TrySynthesize(in ControllerFeedbackActuatorState state,
        bool impulses, in Switch2HdRumbleImpulseTuning impulseTuning,
        in Switch2HdRumbleBodyTuning bodyTuning,
        out Switch2HdRumbleGroup left, out Switch2HdRumbleGroup right)
    {
        left = right = default;
        // Synthetic local math input only. The actual Xbox owner authenticates
        // lifetime, sequence and expiry BEFORE invoking this pure translator.
        bool nonzero = state.BodyLow != 0 || state.BodyHigh != 0 || state.LeftTrigger != 0 || state.RightTrigger != 0;
        return ControllerFeedbackFrame.TryCreate(ControllerFeedbackSource.XboxOneVirtualDevice,
            nonzero ? ControllerFeedbackCommand.Apply : ControllerFeedbackCommand.Neutral,
            ControllerFeedbackActuators.All, state.BodyLow, state.BodyHigh, state.LeftTrigger, state.RightTrigger,
            1, 1, 1, 1, 1, 1, out var frame) &&
            Translate(frame, impulses, impulseTuning, bodyTuning, out left, out right);
    }

    private static bool Translate(in ControllerFeedbackFrame frame, bool impulses,
        in Switch2HdRumbleImpulseTuning impulse, in Switch2HdRumbleBodyTuning body,
        out Switch2HdRumbleGroup left, out Switch2HdRumbleGroup right)
    {
        bool accepted = Switch2HdRumbleFeedbackTranslator.TryTranslate(frame, 1,
            impulses ? Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating :
                Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility, impulse, body, out var synthesis);
        left = synthesis.Left; right = synthesis.Right;
        return accepted;
    }

    internal static void Encode(in Switch2HdRumbleGroup group, bool leftSide, Span<byte> report)
    {
        if (report.Length < 10) throw new ArgumentException("Legacy rumble requires a ten-byte header.", nameof(report));
        report.Clear(); report[0] = 0x10;
        // Preserve the existing neutral convention on both actuator slots.
        report[3] = report[7] = 1;
        report[4] = report[8] = 0x60;
        report[5] = report[9] = 0x40;
        var frame = Peak(group);
        if (!frame.HasNonzeroAmplitude) return;
        int offset = leftSide ? 2 : 6;
        // Authored control hints are mapped to Hz as a bounded legacy policy,
        // not decoded as a claim about the Switch 2 wire-frequency transfer.
        // Original frequency packing: dekuNukem/Nintendo_Switch_Reverse_Engineering,
        // rumble_data_table.md (log2(f/10)*32, HF -0x60, LF -0x40).
        int highCode = Math.Clamp((int)Math.Round(Math.Log2(Math.Clamp((int)frame.Oscillator0ControlCode, 82, 1252) / 10.0) * 32), 0x61, 0xDF);
        int lowCode = Math.Clamp((int)Math.Round(Math.Log2(Math.Clamp((int)frame.Oscillator1ControlCode, 41, 626) / 10.0) * 32), 0x41, 0xBF);
        int highFrequency = (highCode - 0x60) * 4;
        var high = JoyConDevice.GetHdRumbleAmplitude(frame.Oscillator0AmplitudeCode);
        var low = JoyConDevice.GetHdRumbleAmplitude(frame.Oscillator1AmplitudeCode);
        report[offset] = (byte)highFrequency;
        report[offset + 1] = (byte)(high.high | (highFrequency >> 8));
        report[offset + 2] = (byte)((lowCode - 0x40) | (low.low >> 8));
        report[offset + 3] = (byte)low.low;
    }
}

/// <summary>Bounded local scheduling; only LegacyNintendoRumbleOutput performs HID I/O.</summary>
internal sealed class LegacyJoyConHdRumbleDelivery : IDisposable
{
    private const int MaximumDelayedFrames = 1024;
    internal const int StreamDurationMilliseconds = 12;
    private readonly object gate = new();
    private readonly LegacyNintendoRumbleOutput output;
    private readonly bool leftSide;
    private readonly Func<object, bool> isCurrent;
    private readonly Func<long> clock;
    private readonly bool scheduleTimers;
    private readonly byte[] packet;
    private readonly Queue<Pending> delayed = new();
    private Timer timer;
    private object authority;
    private object selectedCredential;
    private bool owns, active, streaming, stopped;
    private long expiresAt;
    private int selectedDelay;
    private Switch2HdRumbleGroup pcmGroup, controlGroup, presentedGroup;
    private long pcmExpiresAt;
    private readonly record struct Pending(Switch2HdRumbleGroup Group, object Authority, bool Streaming, long Due,
        LegacyJoyConHdRumbleSource Source = LegacyJoyConHdRumbleSource.Generic,
        Switch2HdRumbleGroup ControlGroup = default);

    internal LegacyJoyConHdRumbleDelivery(LegacyNintendoRumbleOutput output, int reportLength,
        bool leftSide, Func<object, bool> isCurrent, Func<long> clock = null, bool scheduleTimers = true)
    {
        this.output = output; this.leftSide = leftSide; this.isCurrent = isCurrent;
        this.clock = clock ?? (() => Environment.TickCount64);
        this.scheduleTimers = scheduleTimers;
        packet = new byte[reportLength];
    }

    internal bool Publish(in Switch2HdRumbleGroup group, object credential,
        bool stream, int delayMilliseconds, bool terminal = false,
        LegacyJoyConHdRumbleSource source = LegacyJoyConHdRumbleSource.Generic,
        Switch2HdRumbleGroup control = default)
    {
        if (!Switch2RumbleDelay.IsValid(delayMilliseconds) || !isCurrent(credential) ||
            source < LegacyJoyConHdRumbleSource.Generic || source > LegacyJoyConHdRumbleSource.DualSensePcm) return false;
        lock (gate)
        {
            if (stopped) return false;
            long now = clock();
            if (selectedCredential != null && !ReferenceEquals(selectedCredential, credential)) ClearNoLock();
            selectedCredential = credential;
            if (delayMilliseconds != selectedDelay) delayed.Clear();
            selectedDelay = delayMilliseconds;
            if (terminal || delayMilliseconds == 0)
            {
                delayed.Clear();
                ApplyNoLock(new(group, credential, stream, now,
                    terminal ? LegacyJoyConHdRumbleSource.Generic : source, control), now);
            }
            else
            {
                if (delayed.Count >= MaximumDelayedFrames)
                {
                    ClearNoLock(retainNeutralOwnership: true); // never resume an older classic value
                    return false;
                }
                delayed.Enqueue(new(group, credential, stream, now + delayMilliseconds, source, control));
                owns = true;
            }
            ArmNoLock(now);
            return true; // queued acceptance, not physical delivery
        }
    }

    internal bool Service()
    {
        lock (gate)
        {
            if (stopped || !owns) return false;
            long now = clock();
            DrainNoLock(now);
            if (active && !streaming && isCurrent(authority)) output.Publish(packet, true, authority);
            else output.RequestRetry();
            ArmNoLock(now);
            return true;
        }
    }

    internal bool TryStartConnectionCue(object credential, in Switch2HdRumbleBodyTuning tuning)
    {
        if (!tuning.IsValid || !isCurrent(credential)) return false;
        lock (gate)
        {
            // A connection cue never interrupts already-authored feedback.
            if (stopped || owns || delayed.Count != 0) return false;
            long now = clock();
            selectedCredential = credential;
            var bass = Switch2HdRumbleFeedbackTranslator.ScaleSourcePreservedGroup(
                Switch2ConnectionHaptic.JoyConBassGroup, tuning);
            var click = Switch2HdRumbleFeedbackTranslator.ScaleSourcePreservedGroup(
                Switch2ConnectionHaptic.JoyConSharpClickGroup, tuning);
            ApplyNoLock(new(bass, credential, true, now), now);
            delayed.Enqueue(new(click, credential, true, now +
                Switch2ConnectionHaptic.BassDurationMilliseconds + Switch2ConnectionHaptic.NeutralGapMilliseconds));
            ArmNoLock(now);
            return true;
        }
    }

    internal void Clear()
    {
        lock (gate) { if (!stopped) ClearNoLock(); }
    }

    private void ClearNoLock(bool retainNeutralOwnership = false)
    {
        delayed.Clear(); active = streaming = false; expiresAt = 0;
        pcmGroup = controlGroup = presentedGroup = default; pcmExpiresAt = 0;
        LegacyJoyConHdRumble.Encode(default, leftSide, packet);
        output.Publish(packet, false);
        // An internal rejection is an owned neutral, not permission for the
        // caller to resume a pre-rich byte-motor mailbox. Only an explicit
        // classic/preview takeover or disposal releases this ownership latch.
        owns = retainNeutralOwnership; authority = selectedCredential = null;
        timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void ApplyNoLock(in Pending pending, long now)
    {
        if (!isCurrent(pending.Authority)) { ClearNoLock(retainNeutralOwnership: true); return; }
        var effective = pending.Group;
        if (pending.Source == LegacyJoyConHdRumbleSource.Generic)
        {
            pcmGroup = controlGroup = default; pcmExpiresAt = 0;
        }
        else
        {
            if (pending.Source == LegacyJoyConHdRumbleSource.DualSensePcm)
            {
                pcmGroup = pending.Group;
                controlGroup = pending.ControlGroup;
                pcmExpiresAt = LegacyJoyConHdRumble.Peak(pcmGroup).HasNonzeroAmplitude ?
                    pending.Due + StreamDurationMilliseconds : 0;
            }
            else controlGroup = pending.Group;
            if (pcmExpiresAt <= now) { pcmGroup = default; pcmExpiresAt = 0; }
            effective = pcmExpiresAt != 0 ? DualSenseAdaptiveTriggerHdRumbleTranslator.
                MixPcmWithCompatibility(pcmGroup, controlGroup) : controlGroup;
            // Control-only traffic cannot replay the same PCM packet or renew
            // its lease. New control synthesis can change the mix once; PCM
            // still expires at its own original due time.
            if (pending.Source == LegacyJoyConHdRumbleSource.DualSenseControl && pcmExpiresAt != 0 &&
                effective.Equals(presentedGroup)) return;
        }
        owns = true; authority = pending.Authority; streaming = pending.Streaming;
        if (pending.Source != LegacyJoyConHdRumbleSource.Generic) streaming = pcmExpiresAt != 0;
        active = LegacyJoyConHdRumble.Peak(effective).HasNonzeroAmplitude;
        expiresAt = pending.Source != LegacyJoyConHdRumbleSource.Generic ? pcmExpiresAt :
            streaming && active ? pending.Due + StreamDurationMilliseconds : 0;
        presentedGroup = expiresAt != 0 && now >= expiresAt ? default : effective;
        LegacyJoyConHdRumble.Encode(presentedGroup, leftSide, packet);
        if (expiresAt != 0 && now >= expiresAt) active = false;
        output.Publish(packet, active, authority);
    }

    private void DrainNoLock(long now)
    {
        while (delayed.TryPeek(out var pending) && pending.Due <= now)
        {
            delayed.Dequeue(); ApplyNoLock(pending, now);
        }
        if (owns && authority != null && !isCurrent(authority))
        {
            ClearNoLock(retainNeutralOwnership: true);
        }
        else if (pcmExpiresAt != 0 && now >= pcmExpiresAt)
        {
            // Reconcile the latest control source, never stretch or replay the
            // expired PCM contribution. Shared math above retains both bands.
            ApplyNoLock(new(controlGroup, authority, false, now,
                LegacyJoyConHdRumbleSource.DualSenseControl), now);
        }
        else if (active && expiresAt != 0 && now >= expiresAt)
        {
            active = false; expiresAt = 0;
            presentedGroup = default;
            LegacyJoyConHdRumble.Encode(default, leftSide, packet);
            output.Publish(packet, false);
        }
    }

    private void ArmNoLock(long now)
    {
        if (!scheduleTimers) return;
        long next = expiresAt > now ? expiresAt : long.MaxValue;
        if (delayed.TryPeek(out var pending)) next = Math.Min(next, pending.Due);
        if (next == long.MaxValue) { timer?.Change(Timeout.Infinite, Timeout.Infinite); return; }
        timer ??= new Timer(_ => { lock (gate) { if (stopped) return; long timestamp = clock(); DrainNoLock(timestamp); ArmNoLock(timestamp); } });
        timer.Change((int)Math.Clamp(next - now, 1, int.MaxValue), Timeout.Infinite);
    }

    public void Dispose()
    {
        lock (gate) { if (stopped) return; ClearNoLock(); stopped = true; timer?.Dispose(); timer = null; }
    }
}
