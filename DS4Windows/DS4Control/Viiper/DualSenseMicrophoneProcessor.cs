using System;
using System.Runtime.InteropServices;

namespace DS4Windows
{
    public enum DualSenseMicrophoneNoiseSuppression : byte
    {
        Off = 0,
        Balanced = 1,
        Strong = 2,
    }

    public sealed class DualSenseMicrophoneProcessor : IDisposable
    {
        public const int SampleRate = 48000;
        public const int FrameSize = 480;

        private const float SafeInputTrim = 0.25f;
        private const float LimiterCeiling = 0.8912509f;
        private const float LimiterReleasePerFrame = 0.10f;
        private const float StrongGateClosedGain = 0.18f;
        private const float StrongGateOpenRms = 0.0177828f;
        private const float StrongGateClosedRms = 0.0056234f;
        private static readonly float HighPassCoefficient = CalculateHighPassCoefficient(80.0f);

        private readonly object syncRoot = new object();
        private readonly float[] workingFrame = new float[FrameSize];
        private readonly float[] dryFrame = new float[FrameSize];
        private RnnoiseSuppressor noiseSuppressor;
        private bool noiseSuppressorUnavailable;
        private string noiseSuppressorFailure = string.Empty;
        private float previousInput;
        private float previousOutput;
        private float limiterGain = 1.0f;
        private float strongGateGain = 1.0f;

        public bool NoiseSuppressionAvailable
        {
            get
            {
                lock (syncRoot)
                {
                    return noiseSuppressor != null;
                }
            }
        }

        public string NoiseSuppressionFailure
        {
            get
            {
                lock (syncRoot)
                {
                    return noiseSuppressorFailure;
                }
            }
        }

        public void Process(short[] samples, int sampleCount, byte volume,
            DualSenseMicrophoneNoiseSuppression suppression)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }
            if (sampleCount < 0 || sampleCount > samples.Length || sampleCount > FrameSize)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleCount));
            }

            lock (syncRoot)
            {
                float userGain = Math.Clamp(volume / 128.0f, 0.0f, 2.0f);
                float inputGain = SafeInputTrim * userGain * userGain;
                for (int i = 0; i < sampleCount; i++)
                {
                    float input = samples[i] / 32768.0f;
                    float filtered = HighPassCoefficient *
                        (previousOutput + input - previousInput);
                    previousInput = input;
                    previousOutput = filtered;

                    workingFrame[i] = filtered;
                    dryFrame[i] = filtered;
                }

                Array.Clear(workingFrame, sampleCount, FrameSize - sampleCount);
                Array.Clear(dryFrame, sampleCount, FrameSize - sampleCount);

                if (suppression != DualSenseMicrophoneNoiseSuppression.Off &&
                    EnsureNoiseSuppressor())
                {
                    try
                    {
                        noiseSuppressor.Process(workingFrame);
                    }
                    catch (Exception ex) when (IsNativeFailure(ex))
                    {
                        DisableNoiseSuppressor(ex);
                        Array.Copy(dryFrame, workingFrame, FrameSize);
                    }
                }

                for (int i = 0; i < sampleCount; i++)
                {
                    workingFrame[i] *= inputGain;
                }

                if (suppression == DualSenseMicrophoneNoiseSuppression.Strong &&
                    sampleCount > 0)
                {
                    double sumSquares = 0.0;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        sumSquares += workingFrame[i] * workingFrame[i];
                    }

                    float rms = (float)Math.Sqrt(sumSquares / sampleCount);
                    float targetGateGain = CalculateStrongGateGain(rms);
                    float gateResponse = targetGateGain > strongGateGain ? 0.65f : 0.10f;
                    strongGateGain += (targetGateGain - strongGateGain) * gateResponse;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        workingFrame[i] *= strongGateGain;
                    }
                }
                else
                {
                    strongGateGain = 1.0f;
                }

                ApplyLimiter(sampleCount);
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = (short)Math.Clamp(
                        (int)MathF.Round(workingFrame[i] * limiterGain * 32768.0f),
                        short.MinValue, short.MaxValue);
                }
            }
        }

        public void Reset()
        {
            lock (syncRoot)
            {
                noiseSuppressor?.Dispose();
                noiseSuppressor = null;
                noiseSuppressorUnavailable = false;
                noiseSuppressorFailure = string.Empty;
                previousInput = 0.0f;
                previousOutput = 0.0f;
                limiterGain = 1.0f;
                strongGateGain = 1.0f;
                Array.Clear(workingFrame, 0, workingFrame.Length);
                Array.Clear(dryFrame, 0, dryFrame.Length);
            }
        }

        public void Dispose()
        {
            Reset();
        }

        private bool EnsureNoiseSuppressor()
        {
            if (noiseSuppressor != null)
            {
                return true;
            }
            if (noiseSuppressorUnavailable)
            {
                return false;
            }

            try
            {
                noiseSuppressor = new RnnoiseSuppressor();
                noiseSuppressorFailure = string.Empty;
                return true;
            }
            catch (Exception ex) when (IsNativeFailure(ex))
            {
                DisableNoiseSuppressor(ex);
                return false;
            }
        }

        private void DisableNoiseSuppressor(Exception ex)
        {
            noiseSuppressor?.Dispose();
            noiseSuppressor = null;
            noiseSuppressorUnavailable = true;
            noiseSuppressorFailure = ex.GetBaseException().Message;
        }

        private void ApplyLimiter(int sampleCount)
        {
            float peak = 0.0f;
            for (int i = 0; i < sampleCount; i++)
            {
                peak = Math.Max(peak, Math.Abs(workingFrame[i]));
            }

            float requiredGain = peak > LimiterCeiling ? LimiterCeiling / peak : 1.0f;
            if (requiredGain < limiterGain)
            {
                limiterGain = requiredGain;
            }
            else
            {
                limiterGain += (1.0f - limiterGain) * LimiterReleasePerFrame;
            }
        }

        private static float CalculateStrongGateGain(float rms)
        {
            if (rms <= StrongGateClosedRms)
            {
                return StrongGateClosedGain;
            }
            if (rms >= StrongGateOpenRms)
            {
                return 1.0f;
            }

            float amount = (rms - StrongGateClosedRms) /
                (StrongGateOpenRms - StrongGateClosedRms);
            return StrongGateClosedGain + amount * (1.0f - StrongGateClosedGain);
        }

        private static float CalculateHighPassCoefficient(float cutoff)
        {
            float interval = 1.0f / SampleRate;
            float rc = 1.0f / (2.0f * MathF.PI * cutoff);
            return rc / (rc + interval);
        }

        private static bool IsNativeFailure(Exception ex)
        {
            return ex is DllNotFoundException ||
                ex is EntryPointNotFoundException ||
                ex is BadImageFormatException ||
                ex is SEHException ||
                ex is InvalidOperationException;
        }

        private sealed class RnnoiseSuppressor : IDisposable
        {
            private IntPtr state;

            public RnnoiseSuppressor()
            {
                if (NativeMethods.rnnoise_get_frame_size() != FrameSize)
                {
                    throw new InvalidOperationException("The RNNoise frame size is not 480 samples.");
                }

                state = NativeMethods.rnnoise_create(IntPtr.Zero);
                if (state == IntPtr.Zero)
                {
                    throw new InvalidOperationException("RNNoise could not allocate a denoiser state.");
                }
            }

            public unsafe void Process(float[] frame)
            {
                if (state == IntPtr.Zero)
                {
                    throw new ObjectDisposedException(nameof(RnnoiseSuppressor));
                }

                fixed (float* framePtr = frame)
                {
                    for (int i = 0; i < FrameSize; i++)
                    {
                        framePtr[i] *= short.MaxValue;
                    }

                    NativeMethods.rnnoise_process_frame(state, framePtr, framePtr);

                    for (int i = 0; i < FrameSize; i++)
                    {
                        framePtr[i] /= short.MaxValue;
                    }
                }
            }

            public void Dispose()
            {
                if (state != IntPtr.Zero)
                {
                    NativeMethods.rnnoise_destroy(state);
                    state = IntPtr.Zero;
                }
            }
        }

        private static class NativeMethods
        {
            private const string LibraryName = "rnnoise";

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int rnnoise_get_frame_size();

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr rnnoise_create(IntPtr model);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void rnnoise_destroy(IntPtr state);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern unsafe float rnnoise_process_frame(
                IntPtr state, float* output, float* input);
        }
    }
}
