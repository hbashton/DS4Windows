using System;
using DS4Windows.InputDevices;

namespace DS4Windows
{
    /// <summary>
    /// Encodes Trigger Lab's three public effect modes into the documented
    /// DualSense 11-byte adaptive-trigger payload. This is kept independent of
    /// UI and transport code so physical USB, physical Bluetooth, and VIIPER
    /// feedback overrides produce identical effects.
    /// </summary>
    public static class TriggerLabEffectEncoder
    {
        public readonly struct Effect
        {
            public Effect(byte mode, byte zoneMaskLow, byte zoneMaskHigh,
                byte data0, byte data1, byte data2, byte data3, byte frequency)
            {
                Mode = mode;
                ZoneMaskLow = zoneMaskLow;
                ZoneMaskHigh = zoneMaskHigh;
                Data0 = data0;
                Data1 = data1;
                Data2 = data2;
                Data3 = data3;
                Frequency = frequency;
            }

            public byte Mode { get; }
            public byte ZoneMaskLow { get; }
            public byte ZoneMaskHigh { get; }
            public byte Data0 { get; }
            public byte Data1 { get; }
            public byte Data2 { get; }
            public byte Data3 { get; }
            public byte Frequency { get; }
            public bool IsOff => Mode == 0x05;
        }

        public static Effect Encode(TriggerLabEffectSettings settings, bool active)
        {
            settings = (settings ?? new TriggerLabEffectSettings()).Clone();
            int strength = StrengthFromPercent(settings.ForcePercent);
            if (!active || strength == 0) return Off();

            int start = PositionFromPercent(settings.StartPercent);
            return settings.Mode switch
            {
                TriggerLabMode.Weapon => EncodeWeapon(start,
                    PositionFromPercent(settings.WallPercent), strength),
                TriggerLabMode.Vibration => EncodeZoneEffect(0x26, start,
                    strength, FrequencyFromPercent(settings.WallPercent)),
                _ => EncodeZoneEffect(0x21, start, strength, 0),
            };
        }

        public static Effect Off() => new Effect(0x05, 0, 0, 0, 0, 0, 0, 0);

        public static void ApplyToDevice(DualSenseDevice device, TriggerId trigger,
            TriggerLabEffectSettings settings, bool active)
        {
            if (device == null) return;
            Effect effect = Encode(settings, active);
            device.PrepareRawTriggerEffect(trigger, effect.Mode, effect.ZoneMaskLow,
                effect.ZoneMaskHigh, effect.Data0, effect.Data1, effect.Data2,
                effect.Data3, effect.Frequency);
        }

        public static void ApplyPairToDevice(DualSenseDevice device,
            Effect left, Effect right)
        {
            if (device == null) return;
            device.PrepareRawTriggerEffects(ToDeviceEffect(left),
                ToDeviceEffect(right));
        }

        private static DualSenseDevice.TriggerEffectData ToDeviceEffect(
            Effect effect)
        {
            return new DualSenseDevice.TriggerEffectData
            {
                triggerMotorMode = effect.Mode,
                triggerStartResistance = effect.ZoneMaskLow,
                triggerEffectForce = effect.ZoneMaskHigh,
                triggerRangeForce = effect.Data0,
                triggerNearReleaseStrength = effect.Data1,
                triggerNearMiddleStrength = effect.Data2,
                triggerPressedStrength = effect.Data3,
                triggerActuationFrequency = effect.Frequency,
            };
        }

        public static TriggerLabEffectSettings CreateGameRumbleVibration(
            TriggerLabEffectSettings template, byte magnitude)
        {
            TriggerLabEffectSettings effect =
                (template ?? new TriggerLabEffectSettings()).Clone();
            effect.Mode = TriggerLabMode.Vibration;
            effect.ForcePercent = MagnitudeToPercent(magnitude);
            return effect;
        }

        /// <summary>
        /// Converts an Xbox One/Series impulse-motor magnitude into Sony's
        /// native Adaptive Trigger Vibration effect. Unlike Trigger Lab's
        /// authored multi-zone vibration, impulse feedback is a transient
        /// motor voice and must preserve its full 8-bit amplitude directly.
        /// </summary>
        public static Effect EncodeImpulseTriggerVibration(byte magnitude)
        {
            // Sony mode 0x06 is [frequency, strength, start position]. A
            // stable 15 Hz carrier closely matches the tactile character of
            // the Xbox trigger motors while keeping game-authored amplitude
            // changes intact.
            return magnitude == 0
                ? Off()
                : new Effect(0x06, 15, magnitude, 0, 0, 0, 0, 0);
        }

        public static void ApplyGameRumbleToDevice(DualSenseDevice device,
            TriggerId trigger, TriggerLabEffectSettings template,
            bool persistentEffectActive, byte magnitude)
        {
            if (magnitude == 0)
            {
                ApplyToDevice(device, trigger, template,
                    persistentEffectActive);
                return;
            }

            ApplyToDevice(device, trigger,
                CreateGameRumbleVibration(template, magnitude), true);
        }

        public static void WriteGameRumbleNativeBlock(byte[] destination,
            int offset, TriggerLabEffectSettings template,
            bool persistentEffectActive, byte magnitude)
        {
            if (magnitude == 0)
            {
                WriteNativeBlock(destination, offset, template,
                    persistentEffectActive);
                return;
            }

            WriteNativeBlock(destination, offset,
                CreateGameRumbleVibration(template, magnitude), true);
        }

        public static void WriteNativeBlock(byte[] destination, int offset,
            TriggerLabEffectSettings settings, bool active)
        {
            if (destination == null || offset < 0 || offset + 11 > destination.Length) return;
            Effect effect = Encode(settings, active);
            Array.Clear(destination, offset, 11);
            destination[offset] = effect.Mode;
            destination[offset + 1] = effect.ZoneMaskLow;
            destination[offset + 2] = effect.ZoneMaskHigh;
            destination[offset + 3] = effect.Data0;
            destination[offset + 4] = effect.Data1;
            destination[offset + 5] = effect.Data2;
            destination[offset + 6] = effect.Data3;
            destination[offset + 9] = effect.Frequency;
        }

        private static Effect EncodeWeapon(int start, int wall, int strength)
        {
            start = Math.Clamp(start, 2, 7);
            wall = Math.Clamp(wall, start + 1, 8);
            int zones = (1 << start) | (1 << wall);
            return new Effect(0x25, (byte)zones, (byte)(zones >> 8),
                (byte)((strength - 1) & 0x07), 0, 0, 0, 0);
        }

        private static Effect EncodeZoneEffect(byte mode, int start, int strength, int frequency)
        {
            start = Math.Clamp(start, 0, 9);
            int activeZones = 0;
            uint packedStrength = 0;
            uint value = (uint)((strength - 1) & 0x07);
            for (int zone = start; zone < 10; zone++)
            {
                activeZones |= 1 << zone;
                packedStrength |= value << (3 * zone);
            }

            return new Effect(mode, (byte)activeZones, (byte)(activeZones >> 8),
                (byte)packedStrength, (byte)(packedStrength >> 8),
                (byte)(packedStrength >> 16), (byte)(packedStrength >> 24),
                (byte)frequency);
        }

        private static int StrengthFromPercent(int percent) => percent <= 0
            ? 0
            : Math.Max(1, Math.Min(8, (Math.Min(percent, 100) * 8 + 99) / 100));

        private static int PositionFromPercent(int percent) =>
            Math.Min(9, (Math.Clamp(percent, 0, 100) + 5) / 10);

        private static int FrequencyFromPercent(int percent) =>
            Math.Max(1, (Math.Clamp(percent, 0, 100) * 28 + 50) / 100);

        internal static int MagnitudeToPercent(byte magnitude) =>
            magnitude == 0 ? 0 : Math.Max(1,
                (magnitude * 100 + 127) / byte.MaxValue);
    }
}
