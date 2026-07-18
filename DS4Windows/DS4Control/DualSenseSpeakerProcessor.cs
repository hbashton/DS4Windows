using NAudio.Dsp;
using System;

namespace DS4Windows
{
    public enum DualSenseSpeakerCompression : byte
    {
        Off = 0,
        Balanced = 1,
        Strong = 2,
    }

    /// <summary>
    /// Low-latency mastering for the physical DualSense speaker stream.
    /// Processing is stereo-linked and does not buffer or look ahead.
    /// </summary>
    public sealed class DualSenseSpeakerProcessor
    {
        public const int SampleRate = 48000;
        public const int Channels = 2;
        public const byte MaximumBassBoostDb = 6;
        public const DualSenseSpeakerCompression RecommendedCompression =
            DualSenseSpeakerCompression.Balanced;
        public const byte RecommendedBassBoostDb = 3;

        private const float LimiterCeiling = 0.8912509f;
        private const float LimiterReleaseMs = 60.0f;
        private const float BassHighPassHz = 70.0f;
        private const float BassShelfHz = 200.0f;
        private const float FilterQ = 0.7071068f;
        private const float MinimumDetectorLevel = 0.000001f;

        private readonly DualSenseSpeakerCompression compression;
        private readonly byte bassBoostDb;
        private readonly int sampleRate;
        private readonly BiQuadFilter highPassLeft;
        private readonly BiQuadFilter highPassRight;
        private readonly BiQuadFilter bassShelfLeft;
        private readonly BiQuadFilter bassShelfRight;
        private readonly float compressorReleaseCoefficient;
        private readonly float limiterReleaseCoefficient;
        private readonly float thresholdDb;
        private readonly float ratio;
        private readonly float kneeDb;
        private readonly float makeupGainDb;

        private float detectorEnvelope;
        private float limiterGain = 1.0f;

        public DualSenseSpeakerProcessor(DualSenseSpeakerCompression compression,
            byte bassBoostDb, int sampleRate = SampleRate)
        {
            this.sampleRate = Math.Max(8000, sampleRate);
            this.compression = (DualSenseSpeakerCompression)Math.Clamp((int)compression,
                (int)DualSenseSpeakerCompression.Off,
                (int)DualSenseSpeakerCompression.Strong);
            this.bassBoostDb = Math.Min(bassBoostDb, MaximumBassBoostDb);

            if (this.bassBoostDb > 0)
            {
                highPassLeft = BiQuadFilter.HighPassFilter(this.sampleRate, BassHighPassHz, FilterQ);
                highPassRight = BiQuadFilter.HighPassFilter(this.sampleRate, BassHighPassHz, FilterQ);
                bassShelfLeft = BiQuadFilter.LowShelf(this.sampleRate, BassShelfHz, 1.0f,
                    this.bassBoostDb);
                bassShelfRight = BiQuadFilter.LowShelf(this.sampleRate, BassShelfHz, 1.0f,
                    this.bassBoostDb);
            }

            switch (this.compression)
            {
                case DualSenseSpeakerCompression.Strong:
                    thresholdDb = -20.0f;
                    ratio = 4.0f;
                    kneeDb = 8.0f;
                    makeupGainDb = 6.0f;
                    compressorReleaseCoefficient = TimeCoefficient(160.0f);
                    break;
                case DualSenseSpeakerCompression.Balanced:
                    thresholdDb = -16.0f;
                    ratio = 2.5f;
                    kneeDb = 6.0f;
                    makeupGainDb = 4.0f;
                    compressorReleaseCoefficient = TimeCoefficient(120.0f);
                    break;
                default:
                    compressorReleaseCoefficient = 0.0f;
                    break;
            }

            limiterReleaseCoefficient = TimeCoefficient(LimiterReleaseMs);
        }

        public bool Enabled => compression != DualSenseSpeakerCompression.Off || bassBoostDb > 0;

        public void Process(float[] samples, int frameCount)
        {
            if (!Enabled || samples == null || frameCount <= 0)
            {
                return;
            }

            int frames = Math.Min(frameCount, samples.Length / Channels);
            for (int frameIndex = 0; frameIndex < frames; frameIndex++)
            {
                int offset = frameIndex * Channels;
                float left = Sanitize(samples[offset]);
                float right = Sanitize(samples[offset + 1]);

                if (bassBoostDb > 0)
                {
                    left = bassShelfLeft.Transform(highPassLeft.Transform(left));
                    right = bassShelfRight.Transform(highPassRight.Transform(right));
                }

                float peak = Math.Max(Math.Abs(left), Math.Abs(right));
                float compressorGain = 1.0f;
                if (compression != DualSenseSpeakerCompression.Off)
                {
                    detectorEnvelope = peak >= detectorEnvelope ? peak :
                        compressorReleaseCoefficient * detectorEnvelope +
                        (1.0f - compressorReleaseCoefficient) * peak;
                    compressorGain = DecibelsToLinear(makeupGainDb -
                        GainReductionDb(LinearToDecibels(detectorEnvelope)));
                    left *= compressorGain;
                    right *= compressorGain;
                }

                peak = Math.Max(Math.Abs(left), Math.Abs(right));
                float requiredLimiterGain = peak > LimiterCeiling ?
                    LimiterCeiling / peak : 1.0f;
                limiterGain = requiredLimiterGain < limiterGain ? requiredLimiterGain :
                    limiterReleaseCoefficient * limiterGain +
                    (1.0f - limiterReleaseCoefficient);

                samples[offset] = Math.Clamp(left * limiterGain,
                    -LimiterCeiling, LimiterCeiling);
                samples[offset + 1] = Math.Clamp(right * limiterGain,
                    -LimiterCeiling, LimiterCeiling);
            }
        }

        private float GainReductionDb(float inputDb)
        {
            float kneeStart = thresholdDb - kneeDb * 0.5f;
            float kneeEnd = thresholdDb + kneeDb * 0.5f;
            float ratioFactor = 1.0f - (1.0f / ratio);

            if (inputDb <= kneeStart)
            {
                return 0.0f;
            }

            if (inputDb >= kneeEnd)
            {
                return (inputDb - thresholdDb) * ratioFactor;
            }

            float kneePosition = inputDb - kneeStart;
            return ratioFactor * kneePosition * kneePosition / (2.0f * kneeDb);
        }

        private float TimeCoefficient(float milliseconds)
        {
            return (float)Math.Exp(-1.0 / (sampleRate * milliseconds / 1000.0));
        }

        private static float LinearToDecibels(float value)
        {
            return 20.0f * (float)Math.Log10(Math.Max(value, MinimumDetectorLevel));
        }

        private static float DecibelsToLinear(float value)
        {
            return (float)Math.Pow(10.0, value / 20.0);
        }

        private static float Sanitize(float value)
        {
            return float.IsFinite(value) ? value : 0.0f;
        }
    }
}
