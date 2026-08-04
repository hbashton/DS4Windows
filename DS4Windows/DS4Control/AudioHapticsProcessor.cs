/*
DS4Windows
Copyright (C) 2026  DS4Windows contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows
{
    /// <summary>
    /// Stateful audio-to-haptics signal processor. The output is normalized
    /// stereo PCM intended for the DualSense 3 kHz advanced-haptics lane.
    /// </summary>
    internal sealed class AudioHapticsProcessor
    {
        private const float GateThreshold = 0.003f;
        private const float GateOpenRateAt48Khz = 0.035f;
        private const float GateCloseRateAt48Khz = 0.004f;
        private const float OutputRampStepAt48Khz = 0.004f;

        private readonly AudioHapticsProfileSettings settings;
        private readonly float filterCoefficient;
        private readonly float attackCoefficient;
        private readonly float releaseCoefficient;
        private readonly float gateOpenRate;
        private readonly float gateCloseRate;
        private readonly float outputRampStep;

        private float filterLeft;
        private float filterRight;
        private float envelopeLeft;
        private float envelopeRight;
        private float gate;
        private float outputRamp;

        public AudioHapticsProcessor(AudioHapticsProfileSettings settings,
            int inputSampleRate)
        {
            this.settings = (settings ?? new AudioHapticsProfileSettings())
                .Clone();
            inputSampleRate = Math.Max(8000, inputSampleRate);

            filterCoefficient = ScaleCoefficient(FilterCoefficient(
                this.settings.BassFocus), inputSampleRate);
            attackCoefficient = ScaleCoefficient(AttackCoefficient(
                this.settings.Attack), inputSampleRate);
            releaseCoefficient = ScaleCoefficient(ReleaseCoefficient(
                this.settings.Release), inputSampleRate);
            gateOpenRate = ScaleCoefficient(GateOpenRateAt48Khz,
                inputSampleRate);
            gateCloseRate = ScaleCoefficient(GateCloseRateAt48Khz,
                inputSampleRate);
            outputRampStep = Math.Min(1.0f,
                OutputRampStepAt48Khz * 48000.0f / inputSampleRate);
        }

        public void Process(float left, float right, out float hapticLeft,
            out float hapticRight)
        {
            left = Math.Clamp(left, -1.0f, 1.0f);
            right = Math.Clamp(right, -1.0f, 1.0f);

            filterLeft += (left - filterLeft) * filterCoefficient;
            filterRight += (right - filterRight) * filterCoefficient;
            envelopeLeft = FollowEnvelope(envelopeLeft, filterLeft);
            envelopeRight = FollowEnvelope(envelopeRight, filterRight);

            float peak = Math.Max(envelopeLeft, envelopeRight);
            float gateTarget = peak > GateThreshold ? 1.0f : 0.0f;
            float gateRate = gateTarget > gate ? gateOpenRate : gateCloseRate;
            gate += (gateTarget - gate) * gateRate;
            outputRamp = Math.Min(1.0f, outputRamp + outputRampStep);

            float responsePunch = settings.Response switch
            {
                AudioHapticsResponse.Subtle => 0.0f,
                AudioHapticsResponse.Strong => 3.0f,
                _ => 1.5f,
            };
            float responseGain = settings.Response ==
                AudioHapticsResponse.Subtle ? 0.68f : 1.0f;
            float focusGain = settings.BassFocus switch
            {
                AudioHapticsBassFocus.Deep => 1.35f,
                AudioHapticsBassFocus.Punchy => 1.12f,
                AudioHapticsBassFocus.Wide => 0.92f,
                _ => 1.0f,
            };
            float gain = settings.GainPercent / 100.0f * focusGain *
                responseGain * gate * outputRamp;

            hapticLeft = SoftClip(filterLeft * gain *
                EnvelopePunch(envelopeLeft, responsePunch));
            hapticRight = SoftClip(filterRight * gain *
                EnvelopePunch(envelopeRight, responsePunch));
        }

        public static byte Quantize(float sample)
        {
            int value = (int)Math.Round(Math.Clamp(sample, -1.0f, 1.0f) *
                127.0f);
            return unchecked((byte)(sbyte)Math.Clamp(value, -128, 127));
        }

        public static byte MixSigned8(byte nativeSample, byte derivedSample)
        {
            float native = unchecked((sbyte)nativeSample) / 127.0f;
            float derived = unchecked((sbyte)derivedSample) / 127.0f;
            return Quantize(SoftClip(native + derived));
        }

        private float FollowEnvelope(float current, float value)
        {
            float target = Math.Abs(value);
            float rate = target > current ? attackCoefficient :
                releaseCoefficient;
            return current + (target - current) * rate;
        }

        private static float EnvelopePunch(float envelope, float punch) =>
            1.0f + punch * Math.Clamp(envelope, 0.0f, 1.0f);

        private static float SoftClip(float value)
        {
            float x = Math.Clamp(value, -4.0f, 4.0f);
            float x2 = x * x;
            return Math.Clamp(x * (27.0f + x2) /
                (27.0f + 9.0f * x2), -1.0f, 1.0f);
        }

        private static float ScaleCoefficient(float coefficientAt48Khz,
            int sampleRate)
        {
            // Preserve the same time constant on non-48 kHz endpoints.
            return 1.0f - (float)Math.Pow(1.0f - coefficientAt48Khz,
                48000.0 / sampleRate);
        }

        private static float FilterCoefficient(AudioHapticsBassFocus focus) =>
            focus switch
            {
                AudioHapticsBassFocus.Deep => 0.01039f,
                AudioHapticsBassFocus.Punchy => 0.03095f,
                AudioHapticsBassFocus.Wide => 0.05123f,
                _ => 0.02074f,
            };

        private static float AttackCoefficient(AudioHapticsAttack attack) =>
            attack switch
            {
                AudioHapticsAttack.Soft => 0.20f,
                AudioHapticsAttack.Fast => 0.65f,
                AudioHapticsAttack.Sharp => 0.90f,
                _ => 0.40f,
            };

        private static float ReleaseCoefficient(AudioHapticsRelease release) =>
            release switch
            {
                AudioHapticsRelease.Tight => 0.055f,
                AudioHapticsRelease.Smooth => 0.012f,
                AudioHapticsRelease.Long => 0.006f,
                _ => 0.025f,
            };
    }

}
