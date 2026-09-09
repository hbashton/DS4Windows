using System;

namespace DS4Windows;

internal readonly record struct NintendoDualSenseRumbleSource(
    bool CompatibilityAllowed, bool PcmAllowed, ushort BodyLow = 0, ushort BodyHigh = 0)
{
    internal static NintendoDualSenseRumbleSource Legacy => new(true, true);
    internal static NintendoDualSenseRumbleSource Compatibility => new(true, false);
    internal static NintendoDualSenseRumbleSource Audio => new(false, true);
}

/// <summary>
/// Tracks the source selector from actual DualSense HID control updates for
/// one Nintendo output lifetime. VIIPER's media packets contain accumulated
/// motor values and native flags; they cannot supersede a newer control mode
/// or the amplitudes of its last accepted control update (including zero).
/// Compatibility is held until a fresh control update or lifetime boundary;
/// media can prove source liveness, but is never new motor intent.
/// Call under the owning feedback lane's gate, never the physical input path.
/// </summary>
internal sealed class NintendoDualSenseRumbleSourceState
{
    internal const int NativeReportOffset = 28;
    internal const int NativeReportLength = 48;
    private object boundOwner;
    private int boundDeviceIndex;
    private long boundProfileRevision;
    private long boundStreamGeneration;
    private bool nativeContractKnown;
    private NintendoDualSenseRumbleSource selected = NintendoDualSenseRumbleSource.Audio;

    internal NintendoDualSenseRumbleSource Resolve(object owner, int deviceIndex,
        long profileRevision, long streamGeneration, ReadOnlySpan<byte> feedback,
        bool freshNativeOutput)
    {
        if (!ReferenceEquals(owner, boundOwner) || deviceIndex != boundDeviceIndex ||
            profileRevision != boundProfileRevision || streamGeneration != boundStreamGeneration)
        {
            Reset();
            boundOwner = owner;
            boundDeviceIndex = deviceIndex;
            boundProfileRevision = profileRevision;
            boundStreamGeneration = streamGeneration;
        }

        if (!HasNativeReport(feedback))
        {
            // Older compact-only feedback has no source-selector contract.
            // Once native reports exist, a truncated/unknown packet must not
            // regain that compatibility fallback or alter the current mode.
            return nativeContractKnown ? default : ReadSnapshot(feedback);
        }

        nativeContractKnown = true;
        if (freshNativeOutput) selected = ReadSnapshot(feedback);
        return selected;
    }

    internal void Reset()
    {
        boundOwner = null;
        nativeContractKnown = false;
        selected = NintendoDualSenseRumbleSource.Audio;
    }

    internal static NintendoDualSenseRumbleSource ReadSnapshot(ReadOnlySpan<byte> feedback)
    {
        if (!HasNativeReport(feedback))
            return WithCompactMotors(NintendoDualSenseRumbleSource.Legacy, feedback);
        byte flag0 = feedback[NativeReportOffset + 1];
        byte flag2 = feedback[NativeReportOffset + 39];
        // SDL upstream 4f031ea, SDL_hidapi_ps5.c UpdateEffects, and
        // Switch2Connect 61ac664, virtual_controller.py: 0x01 enables legacy
        // emulation; flag2 0x04 enables improved emulation; flag0 0x02 selects
        // rumble instead of audio haptics. The latter does not update motors.
        return (flag0 & 0x03) != 0 || (flag2 & 0x04) != 0 ?
            WithCompactMotors(NintendoDualSenseRumbleSource.Compatibility, feedback) :
            NintendoDualSenseRumbleSource.Audio;
    }

    private static NintendoDualSenseRumbleSource WithCompactMotors(
        NintendoDualSenseRumbleSource source, ReadOnlySpan<byte> feedback) =>
        feedback.Length < 2 ? source : source with
        {
            BodyLow = (ushort)(feedback[0] * 257),
            BodyHigh = (ushort)(feedback[1] * 257),
        };

    private static bool HasNativeReport(ReadOnlySpan<byte> feedback) =>
        feedback.Length >= NativeReportOffset + NativeReportLength &&
        feedback[NativeReportOffset] == 0x02;
}
