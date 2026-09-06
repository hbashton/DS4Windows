/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using DS4Windows.Switch2;

namespace DS4Windows
{
    /// <summary>
    /// Converts supported DualSense adaptive-trigger programs into a bounded,
    /// side-local Switch 2 HD-rumble approximation. Switch 2 controllers do
    /// not have an adaptive trigger actuator, so this translator preserves the
    /// source program's side, strength envelope, frequency hint, and up to
    /// three temporal regions without claiming mechanical equivalence.
    /// </summary>
    internal static class DualSenseAdaptiveTriggerHdRumbleTranslator
    {
        internal const int EffectLength = 11;
        internal const ushort MinimumControlCode = 0x00E1;
        internal const ushort MaximumControlCode = 0x01E1;

        private const byte OffMode = 0x00;
        private const byte RigidMode = 0x01;
        private const byte LegacyPulseMode = 0x02;
        private const byte NoResistanceMode = 0x05;
        private const byte FeedbackMode = 0x21;
        private const byte BowMode = 0x22;
        private const byte GallopingMode = 0x23;
        private const byte WeaponMode = 0x25;
        private const byte VibrationMode = 0x26;
        private const byte MachineMode = 0x27;
        private const int MaximumStrengthUnits = 8;

        /// <summary>
        /// Translates one native-spaced 11-byte effect block. False means that
        /// the effect is off, malformed, unsupported, or contains no usable
        /// energy; callers must not synthesize an effect in those cases.
        /// </summary>
        internal static bool TryTranslate(ReadOnlySpan<byte> effect,
            out Switch2HdRumbleGroup group)
        {
            group = default;
            if (effect.Length != EffectLength)
            {
                return false;
            }

            switch (effect[0])
            {
                case OffMode:
                case NoResistanceMode:
                    return false;
                case RigidMode:
                    group = CreateRepeatedGroup(
                        Switch2HdRumbleFeedbackTranslator.
                            MaximumPackedCompatibilityAmplitude / 2,
                        Switch2HdRumbleFeedbackTranslator.
                            SdlHighControlCode);
                    return true;
                case LegacyPulseMode:
                    group = CreateLegacyPulseGroup(effect);
                    return HasAmplitude(group);
                case FeedbackMode:
                case VibrationMode:
                    group = CreatePackedZoneGroup(effect,
                        effect[0] == VibrationMode);
                    return HasAmplitude(group);
                case BowMode:
                case GallopingMode:
                case WeaponMode:
                case MachineMode:
                    group = CreateMaskedPatternGroup(effect);
                    return HasAmplitude(group);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Adds an approximation to an already translated PCM/body group. The
        /// incoming non-silent trigger has carrier priority, following Switch2Connect's
        /// _vib_merge_ble_source_aware at 61ac6642ce12fe7217e38a860b14863b18ca7e28.
        /// Soft amplitude saturation retains headroom for overlapping effects.
        /// </summary>
        internal static Switch2HdRumbleGroup Mix(
            in Switch2HdRumbleGroup basis,
            in Switch2HdRumbleGroup addition) => new(
                Mix(basis.First, addition.First),
                Mix(basis.Second, addition.Second),
                Mix(basis.Third, addition.Third));

        internal static Switch2HdRumbleSubframe Mix(
            in Switch2HdRumbleSubframe basis,
            in Switch2HdRumbleSubframe addition) => Mix(basis, addition, false);

        /// <summary>
        /// PCM and compatibility rumble share two carriers per side. Follow
        /// the stronger source on each carrier, with PCM winning a tie, so a
        /// tiny compatibility motor cannot retune an authored stereo effect.
        /// Trigger overlays retain their separate explicit priority above.
        /// </summary>
        internal static Switch2HdRumbleGroup MixPcmWithCompatibility(
            in Switch2HdRumbleGroup pcm, in Switch2HdRumbleGroup compatibility) => new(
                Mix(compatibility.First, pcm.First, true),
                Mix(compatibility.Second, pcm.Second, true),
                Mix(compatibility.Third, pcm.Third, true));

        private static Switch2HdRumbleSubframe Mix(
            in Switch2HdRumbleSubframe basis,
            in Switch2HdRumbleSubframe addition, bool chooseDominantCarrier)
        {
            ushort oscillator0Control =
                addition.Oscillator0AmplitudeCode != 0 &&
                (!chooseDominantCarrier || addition.Oscillator0AmplitudeCode >= basis.Oscillator0AmplitudeCode) ?
                    addition.Oscillator0ControlCode :
                    basis.Oscillator0ControlCode;
            ushort oscillator1Control =
                addition.Oscillator1AmplitudeCode != 0 &&
                (!chooseDominantCarrier || addition.Oscillator1AmplitudeCode >= basis.Oscillator1AmplitudeCode) ?
                    addition.Oscillator1ControlCode :
                    basis.Oscillator1ControlCode;
            return new Switch2HdRumbleSubframe(
                oscillator0Control,
                Switch2HdRumbleFeedbackTranslator.
                    MixPackedAmplitudesWithHeadroom(
                        basis.Oscillator0AmplitudeCode,
                        addition.Oscillator0AmplitudeCode),
                oscillator1Control,
                Switch2HdRumbleFeedbackTranslator.
                    MixPackedAmplitudesWithHeadroom(
                        basis.Oscillator1AmplitudeCode,
                        addition.Oscillator1AmplitudeCode));
        }

        private static Switch2HdRumbleGroup CreatePackedZoneGroup(
            ReadOnlySpan<byte> effect, bool useSourceFrequency)
        {
            ushort mask = (ushort)(effect[1] | effect[2] << 8);
            uint strengths = (uint)(effect[3] | effect[4] << 8 |
                effect[5] << 16 | effect[6] << 24);
            ushort fixedControl = useSourceFrequency ?
                MapFrequency(effect[9]) : (ushort)0;
            return new Switch2HdRumbleGroup(
                CreatePackedZoneSubframe(mask, strengths, 0, 3,
                    fixedControl),
                CreatePackedZoneSubframe(mask, strengths, 3, 6,
                    fixedControl),
                CreatePackedZoneSubframe(mask, strengths, 6, 10,
                    fixedControl));
        }

        private static Switch2HdRumbleSubframe CreatePackedZoneSubframe(
            ushort mask, uint packedStrengths, int firstZone,
            int endZone, ushort fixedControl)
        {
            int strengthSum = 0;
            int weightedPosition = 0;
            for (int zone = firstZone; zone < endZone; zone++)
            {
                if ((mask & 1 << zone) == 0)
                {
                    continue;
                }
                int strength = (int)((packedStrengths >> (zone * 3)) &
                    0x07) + 1;
                strengthSum += strength;
                weightedPosition += zone * strength;
            }

            int zoneCount = endZone - firstZone;
            ushort amplitude = ScaleStrength(strengthSum,
                zoneCount * MaximumStrengthUnits);
            ushort control = amplitude == 0 ? (ushort)0 :
                fixedControl != 0 ? fixedControl :
                MapPosition(weightedPosition, strengthSum);
            return CreateHighBandSubframe(control, amplitude);
        }

        private static Switch2HdRumbleGroup CreateMaskedPatternGroup(
            ReadOnlySpan<byte> effect)
        {
            ushort mask = (ushort)(effect[1] | effect[2] << 8);
            int strength = Math.Min(MaximumStrengthUnits, effect[3] + 1);
            if (mask == 0)
            {
                return default;
            }

            byte frequency = effect[0] is GallopingMode or MachineMode ?
                effect[4] : (byte)0;
            ushort fixedControl = frequency == 0 ? (ushort)0 :
                MapFrequency(frequency);
            return new Switch2HdRumbleGroup(
                CreateMaskedPatternSubframe(mask, strength, 0, 3,
                    fixedControl),
                CreateMaskedPatternSubframe(mask, strength, 3, 6,
                    fixedControl),
                CreateMaskedPatternSubframe(mask, strength, 6, 10,
                    fixedControl));
        }

        private static Switch2HdRumbleSubframe
            CreateMaskedPatternSubframe(ushort mask, int strength,
                int firstZone, int endZone, ushort fixedControl)
        {
            int activeZones = 0;
            int positionSum = 0;
            for (int zone = firstZone; zone < endZone; zone++)
            {
                if ((mask & 1 << zone) == 0)
                {
                    continue;
                }
                activeZones++;
                positionSum += zone;
            }
            if (activeZones == 0)
            {
                return default;
            }

            ushort amplitude = ScaleStrength(activeZones * strength,
                (endZone - firstZone) * MaximumStrengthUnits);
            ushort control = fixedControl != 0 ? fixedControl :
                MapPosition(positionSum, activeZones);
            return CreateHighBandSubframe(control, amplitude);
        }

        private static Switch2HdRumbleGroup CreateLegacyPulseGroup(
            ReadOnlySpan<byte> effect)
        {
            ushort first = ScaleByte(effect[1]);
            ushort second = ScaleByte(effect[2]);
            ushort third = ScaleByte(effect[3]);
            if (first == 0 && second == 0 && third == 0)
            {
                ushort fallback = (ushort)(
                    Switch2HdRumbleFeedbackTranslator.
                        MaximumPackedCompatibilityAmplitude / 2);
                first = second = third = fallback;
            }
            return new Switch2HdRumbleGroup(
                CreateHighBandSubframe(MapPosition(0, 1), first),
                CreateHighBandSubframe(MapPosition(4, 1), second),
                CreateHighBandSubframe(MapPosition(9, 1), third));
        }

        private static Switch2HdRumbleGroup CreateRepeatedGroup(
            int amplitude, ushort control)
        {
            var subframe = CreateHighBandSubframe(control,
                (ushort)amplitude);
            return new Switch2HdRumbleGroup(subframe, subframe, subframe);
        }

        private static Switch2HdRumbleSubframe CreateHighBandSubframe(
            ushort control, ushort amplitude) => new(
                control, amplitude,
                Switch2HdRumbleFeedbackTranslator.SdlLowControlCode, 0);

        private static ushort ScaleByte(byte value) => (ushort)(
            (uint)value * Switch2HdRumbleFeedbackTranslator.
                MaximumPackedCompatibilityAmplitude / byte.MaxValue);

        private static ushort ScaleStrength(int numerator, int denominator)
        {
            if (numerator <= 0 || denominator <= 0)
            {
                return 0;
            }
            uint scaled = (uint)(numerator *
                Switch2HdRumbleFeedbackTranslator.
                    MaximumPackedCompatibilityAmplitude + denominator / 2) /
                (uint)denominator;
            return (ushort)Math.Min(
                Switch2HdRumbleFeedbackTranslator.
                    MaximumPackedCompatibilityAmplitude, scaled);
        }

        private static ushort MapFrequency(byte frequency)
        {
            if (frequency == 0)
            {
                return Switch2HdRumbleFeedbackTranslator.
                    SdlHighControlCode;
            }
            return (ushort)(MinimumControlCode +
                (uint)(frequency - 1) *
                (MaximumControlCode - MinimumControlCode) /
                (byte.MaxValue - 1));
        }

        private static ushort MapPosition(int weightedPosition,
            int totalWeight)
        {
            if (totalWeight <= 0)
            {
                return Switch2HdRumbleFeedbackTranslator.
                    SdlHighControlCode;
            }
            uint normalizedPosition = (uint)Math.Clamp(
                (weightedPosition * 256 + totalWeight / 2) /
                    totalWeight, 0, 9 * 256);
            return (ushort)(MinimumControlCode +
                normalizedPosition *
                (MaximumControlCode - MinimumControlCode) /
                (9 * 256));
        }

        private static bool HasAmplitude(in Switch2HdRumbleGroup group) =>
            group.First.HasNonzeroAmplitude ||
            group.Second.HasNonzeroAmplitude ||
            group.Third.HasNonzeroAmplitude;
    }
}
