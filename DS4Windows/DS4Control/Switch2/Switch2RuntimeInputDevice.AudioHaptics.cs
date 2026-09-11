using System;
using System.Diagnostics;
using System.Threading;

namespace DS4Windows.Switch2;

/// <summary>One exact local audio producer. It owns no physical writer or timer.</summary>
internal sealed class Switch2AudioHapticsOutput : IDisposable
{
    private readonly Switch2RuntimeInputDevice owner;
    private int disposed;
    internal Switch2AudioHapticsOutput(Switch2RuntimeInputDevice owner) => this.owner = owner;
    internal bool IsReady => Volatile.Read(ref disposed) == 0 && owner.IsAudioHapticsReady(this);
    internal bool TryWrite(ReadOnlySpan<byte> samples, AudioHapticsMode mode, long capturedTimestamp) =>
        Volatile.Read(ref disposed) == 0 && owner.TryWriteAudioHaptics(this, samples, mode, capturedTimestamp);
    internal bool TryWithdraw() => Volatile.Read(ref disposed) == 0 && owner.TryWithdrawAudioHaptics(this);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) owner.ReleaseAudioHaptics(this);
    }
}

public sealed partial class Switch2RuntimeInputDevice
{
    private readonly Func<ulong> audioHapticsClock;

    private bool TryGetAudioHapticsTimestamp(out ulong timestamp)
    {
        // Production instances retain the host-wide QPC clock. A private
        // construction seam lets boundary tests advance time independently
        // of reflection/JIT/scheduler delays without relaxing the live TTL.
        if (audioHapticsClock == null)
            return ControllerFeedbackClock.TryGetTimestampMicroseconds(out timestamp);
        try { timestamp = audioHapticsClock(); return true; }
        catch { timestamp = 0; return false; }
    }
    private Switch2AudioHapticsOutput audioHapticsOutput;
    private ISwitch2AudioHapticsLifetime audioHapticsLifetime;
    private ControllerFeedbackStateLanePump.Lane audioHapticsLane;
    private bool audioHapticsWithdrawalPending;

    internal bool IsAudioHapticsReady(Switch2AudioHapticsOutput output)
    {
        lock (localFeedbackGate)
        {
            if (!ReferenceEquals(output, audioHapticsOutput)) return false;
            int slot = DeviceSlotNumber;
            if (slot < 0 || slot >= Global.EnableOutputDataToDS4.Length || !Global.EnableOutputDataToDS4[slot]) return false;
            Switch2BluetoothFeedbackLifetime bluetooth;
            Switch2ProUsbOwnedFeedbackActivationLifetime usb;
            if (!Monitor.TryEnter(publicationGate)) return false;
            try
            {
                if (terminalNeutralReserved || runtimeState != Switch2RuntimeInputDeviceState.Active) return false;
                bluetooth = bluetoothFeedbackLifetime;
                usb = usbFeedbackLifetime;
            }
            finally { Monitor.Exit(publicationGate); }
            return bluetooth != null && usb == null ? bluetooth.CanServiceRumbleMaintenance :
                usb != null && bluetooth == null && usb.CanServiceRumbleMaintenance;
        }
    }

    internal bool TryCreateAudioHapticsOutput(out Switch2AudioHapticsOutput output)
    {
        output = null;
        lock (localFeedbackGate)
        {
            if (!Monitor.TryEnter(publicationGate)) return false;
            try
            {
                if (terminalNeutralReserved || runtimeState is not (
                        Switch2RuntimeInputDeviceState.Created or Switch2RuntimeInputDeviceState.Active)) return false;
            }
            finally { Monitor.Exit(publicationGate); }
            if (audioHapticsOutput != null) WithdrawAudioHapticsNoLock();
            output = audioHapticsOutput = new Switch2AudioHapticsOutput(this);
            return true;
        }
    }

    internal bool TryWriteAudioHaptics(Switch2AudioHapticsOutput output,
        ReadOnlySpan<byte> samples, AudioHapticsMode mode, long capturedTimestamp)
    {
        if (samples.Length != 64 || mode is not (AudioHapticsMode.Mix or AudioHapticsMode.Replace) ||
            capturedTimestamp < 0 || !ControllerFeedbackClock.TryConvertQpcTicks(
                (ulong)capturedTimestamp, (ulong)Stopwatch.Frequency, out ulong captured) ||
            !TryGetAudioHapticsTimestamp(out ulong now) ||
            captured > now || now - captured >= Switch2AudioHapticsLifetime.MaximumSampleAgeMicroseconds) return false;
        lock (localFeedbackGate)
        {
            if (!ReferenceEquals(output, audioHapticsOutput) || !TryBindAudioHapticsLifetimeNoLock()) return false;
            int slot = DeviceSlotNumber;
            if (slot < 0 || slot >= Global.EnableOutputDataToDS4.Length || !Global.EnableOutputDataToDS4[slot])
            {
                WithdrawAudioHapticsNoLock();
                return false;
            }
            if (audioHapticsWithdrawalPending && !WithdrawAudioHapticsNoLock()) return false;
            if (!DualSenseHapticsTranslator.TryTranslatePcmToSwitch2Groups(samples, out var left, out var right)) return false;
            if (!HasAudioAmplitude(left) && !HasAudioAmplitude(right)) return WithdrawAudioHapticsNoLock();
            if (!TryGetAudioHapticsTimestamp(out now) ||
                captured > now || now - captured >= Switch2AudioHapticsLifetime.MaximumSampleAgeMicroseconds) return false;
            return audioHapticsLifetime.TryPublishAudio(audioHapticsLane, left, right, mode, captured, now);
        }
    }

    internal bool TryWithdrawAudioHaptics(Switch2AudioHapticsOutput output)
    {
        lock (localFeedbackGate)
            return ReferenceEquals(output, audioHapticsOutput) && WithdrawAudioHapticsNoLock();
    }

    internal void ReleaseAudioHaptics(Switch2AudioHapticsOutput output)
    {
        lock (localFeedbackGate)
        {
            if (!ReferenceEquals(output, audioHapticsOutput)) return;
            // Revoke the producer before attempting a possibly busy canonical
            // withdrawal. Only the existing output owner retries that intent.
            audioHapticsOutput = null;
            WithdrawAudioHapticsNoLock();
        }
    }

    private bool TryBindAudioHapticsLifetimeNoLock()
    {
        ISwitch2AudioHapticsLifetime current;
        if (!Monitor.TryEnter(publicationGate)) return false;
        try
        {
            if (terminalNeutralReserved || runtimeState != Switch2RuntimeInputDeviceState.Active ||
                (bluetoothFeedbackLifetime == null) == (usbFeedbackLifetime == null)) return false;
            current = (ISwitch2AudioHapticsLifetime)bluetoothFeedbackLifetime ?? usbFeedbackLifetime;
        }
        finally { Monitor.Exit(publicationGate); }
        if (audioHapticsLifetime != null && !ReferenceEquals(audioHapticsLifetime, current)) return false;
        if (audioHapticsLane == null)
        {
            if (!current.TryCreateAudioLane(out audioHapticsLane)) return false;
            audioHapticsLifetime = current;
        }
        return true;
    }

    private bool WithdrawAudioHapticsNoLock()
    {
        if (audioHapticsLane == null) return true;
        if (!TryGetAudioHapticsTimestamp(out ulong now)) return false;
        bool withdrawn = audioHapticsLifetime.TryWithdrawAudio(audioHapticsLane, now);
        audioHapticsWithdrawalPending = !withdrawn;
        if (!withdrawn) rumbleMaintenanceWorker?.Wake();
        return withdrawn;
    }

    private static bool HasAudioAmplitude(in Switch2HdRumbleGroup group) =>
        group.First.HasNonzeroAmplitude || group.Second.HasNonzeroAmplitude || group.Third.HasNonzeroAmplitude;
}
