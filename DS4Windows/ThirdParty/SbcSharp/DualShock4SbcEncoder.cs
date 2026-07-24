// DS4-specific SBC encoder derived from the Bluetooth A2DP SBC specification,
// Appendix B. Licensed under Apache-2.0 with the rest of the vendored codec.

using System;

#nullable enable

namespace SBC;

/// <summary>
/// Stateful SBC encoder for the fixed format required by a Bluetooth
/// DualShock 4: 16/32 kHz stereo, 8 subbands, 16 blocks, joint stereo, SNR bit
/// allocation, and bitpool 48. The analysis samples remain double precision
/// through coupling and quantization so the controller receives the same
/// standards-conformant bitstream decisions as the working reference path.
/// </summary>
internal sealed class DualShock4SbcEncoder
{
    public const int SamplesPerChannel = 128;
    public const int FrameLength = 109;

    private const int Channels = 2;
    private const int Subbands = 8;
    private const int Blocks = 16;
    private const int Bitpool = 48;
    private const int HistoryLength = 80;
    private readonly byte configuration;

    private readonly double[][] history =
    {
        new double[HistoryLength],
        new double[HistoryLength],
    };
    private readonly double[][] subbandSamples =
    {
        new double[SamplesPerChannel],
        new double[SamplesPerChannel],
    };
    private readonly int[][] scaleFactors =
    {
        new int[Subbands],
        new int[Subbands],
    };
    private readonly int[][] allocatedBits =
    {
        new int[Subbands],
        new int[Subbands],
    };
    private readonly bool[] joined = new bool[Subbands];
    private readonly SbcBitStream writer = new SbcBitStream(
        Array.Empty<byte>(), 0, isReader: false);

    public DualShock4SbcEncoder(int sampleRate = 32000)
    {
        configuration = sampleRate switch
        {
            16000 => 0x3F,
            32000 => 0x7F,
            _ => throw new ArgumentOutOfRangeException(nameof(sampleRate)),
        };
    }

    public void Reset()
    {
        for (int channel = 0; channel < Channels; channel++)
        {
            Array.Clear(history[channel], 0, history[channel].Length);
            Array.Clear(subbandSamples[channel], 0,
                subbandSamples[channel].Length);
        }
    }

    public bool Encode(short[] left, short[] right, byte[] output)
    {
        if (left == null || right == null || output == null ||
            left.Length < SamplesPerChannel ||
            right.Length < SamplesPerChannel || output.Length < FrameLength)
        {
            return false;
        }

        AnalyzeChannel(0, left);
        AnalyzeChannel(1, right);
        SelectJointStereoBands();
        CalculateScaleFactors();
        AllocateSnrBits();
        return WriteFrame(output);
    }

    private void AnalyzeChannel(int channel, short[] pcm)
    {
        double[] channelHistory = history[channel];
        double[] channelSamples = subbandSamples[channel];
        double[][] matrix = SbcEncoderTables.AnalysisMatrix8;

        for (int block = 0; block < Blocks; block++)
        {
            Array.Copy(channelHistory, 0, channelHistory, Subbands,
                HistoryLength - Subbands);
            int pcmOffset = block * Subbands;
            for (int sample = 0; sample < Subbands; sample++)
            {
                channelHistory[Subbands - sample - 1] =
                    pcm[pcmOffset + sample];
            }

            int outputOffset = block * Subbands;
            for (int band = 0; band < Subbands; band++)
            {
                double value = 0.0;
                double[] coefficients = matrix[band];
                for (int tap = 0; tap < HistoryLength; tap++)
                {
                    value += coefficients[tap] * channelHistory[tap];
                }

                channelSamples[outputOffset + band] = value;
            }
        }
    }

    private void SelectJointStereoBands()
    {
        for (int band = 0; band < Subbands - 1; band++)
        {
            double maximumLeft = 0.0;
            double maximumRight = 0.0;
            double maximumSum = 0.0;
            double maximumDifference = 0.0;

            for (int block = 0; block < Blocks; block++)
            {
                int index = block * Subbands + band;
                double left = subbandSamples[0][index];
                double right = subbandSamples[1][index];
                maximumLeft = Math.Max(maximumLeft, Math.Abs(left));
                maximumRight = Math.Max(maximumRight, Math.Abs(right));
                maximumSum = Math.Max(maximumSum,
                    Math.Abs((left + right) * 0.5));
                maximumDifference = Math.Max(maximumDifference,
                    Math.Abs((left - right) * 0.5));
            }

            bool useJoint = ScaleFactor(maximumSum) +
                ScaleFactor(maximumDifference) <
                ScaleFactor(maximumLeft) + ScaleFactor(maximumRight);
            joined[band] = useJoint;
            if (!useJoint)
            {
                continue;
            }

            for (int block = 0; block < Blocks; block++)
            {
                int index = block * Subbands + band;
                double left = subbandSamples[0][index];
                double right = subbandSamples[1][index];
                subbandSamples[0][index] = (left + right) * 0.5;
                subbandSamples[1][index] = (left - right) * 0.5;
            }
        }

        joined[Subbands - 1] = false;
    }

    private void CalculateScaleFactors()
    {
        for (int channel = 0; channel < Channels; channel++)
        {
            for (int band = 0; band < Subbands; band++)
            {
                double maximum = 0.0;
                for (int block = 0; block < Blocks; block++)
                {
                    maximum = Math.Max(maximum, Math.Abs(
                        subbandSamples[channel][block * Subbands + band]));
                }

                scaleFactors[channel][band] = ScaleFactor(maximum);
            }
        }
    }

    private static int ScaleFactor(double maximumMagnitude)
    {
        int factor = 0;
        while (factor < 15 && (1 << (factor + 1)) <= maximumMagnitude)
        {
            factor++;
        }

        return factor;
    }

    /// <summary>
    /// Combined-channel SNR allocation from A2DP SBC B.6.3.2.
    /// </summary>
    private void AllocateSnrBits()
    {
        int maximumNeed = 0;
        for (int channel = 0; channel < Channels; channel++)
        {
            for (int band = 0; band < Subbands; band++)
            {
                maximumNeed = Math.Max(maximumNeed,
                    scaleFactors[channel][band]);
                allocatedBits[channel][band] = 0;
            }
        }

        int bitCount = 0;
        int sliceCount = 0;
        int bitSlice = maximumNeed + 1;
        do
        {
            bitSlice--;
            bitCount += sliceCount;
            sliceCount = 0;
            for (int channel = 0; channel < Channels; channel++)
            {
                for (int band = 0; band < Subbands; band++)
                {
                    int need = scaleFactors[channel][band];
                    if (need > bitSlice + 1 && need < bitSlice + 16)
                    {
                        sliceCount++;
                    }
                    else if (need == bitSlice + 1)
                    {
                        sliceCount += 2;
                    }
                }
            }
        }
        while (bitCount + sliceCount < Bitpool);

        if (bitCount + sliceCount == Bitpool)
        {
            bitCount += sliceCount;
            bitSlice--;
        }

        for (int channel = 0; channel < Channels; channel++)
        {
            for (int band = 0; band < Subbands; band++)
            {
                int need = scaleFactors[channel][band];
                allocatedBits[channel][band] = need < bitSlice + 2 ? 0 :
                    Math.Min(need - bitSlice, 16);
            }
        }

        int nextChannel = 0;
        int nextBand = 0;
        while (bitCount < Bitpool && nextBand < Subbands)
        {
            int bits = allocatedBits[nextChannel][nextBand];
            if (bits >= 2 && bits < 16)
            {
                allocatedBits[nextChannel][nextBand]++;
                bitCount++;
            }
            else if (scaleFactors[nextChannel][nextBand] == bitSlice + 1 &&
                bitCount + 1 < Bitpool)
            {
                allocatedBits[nextChannel][nextBand] = 2;
                bitCount += 2;
            }

            AdvanceAllocationCursor(ref nextChannel, ref nextBand);
        }

        nextChannel = 0;
        nextBand = 0;
        while (bitCount < Bitpool && nextBand < Subbands)
        {
            if (allocatedBits[nextChannel][nextBand] < 16)
            {
                allocatedBits[nextChannel][nextBand]++;
                bitCount++;
            }

            AdvanceAllocationCursor(ref nextChannel, ref nextBand);
        }
    }

    private static void AdvanceAllocationCursor(ref int channel, ref int band)
    {
        if (++channel == Channels)
        {
            channel = 0;
            band++;
        }
    }

    private bool WriteFrame(byte[] output)
    {
        Array.Clear(output, 0, FrameLength);
        writer.Reset(output, FrameLength, isReader: false);
        writer.PutBits(0x9C, 8);
        writer.PutBits(configuration, 8);
        writer.PutBits(Bitpool, 8);
        writer.PutBits(0, 8);

        for (int band = 0; band < Subbands - 1; band++)
        {
            writer.PutBits(joined[band] ? 1u : 0u, 1);
        }
        writer.PutBits(0, 1);

        for (int channel = 0; channel < Channels; channel++)
        {
            for (int band = 0; band < Subbands; band++)
            {
                writer.PutBits((uint)scaleFactors[channel][band], 4);
            }
        }

        for (int block = 0; block < Blocks; block++)
        {
            for (int channel = 0; channel < Channels; channel++)
            {
                for (int band = 0; band < Subbands; band++)
                {
                    int bits = allocatedBits[channel][band];
                    if (bits == 0)
                    {
                        continue;
                    }

                    uint levels = (1u << bits) - 1;
                    double limit = 1 << (scaleFactors[channel][band] + 1);
                    double normalized = subbandSamples[channel][
                        block * Subbands + band] / limit;
                    long quantized = (long)((normalized + 1.0) * levels * 0.5);
                    quantized = Math.Max(0, Math.Min(quantized, levels));
                    writer.PutBits((uint)quantized, bits);
                }
            }
        }

        writer.Flush();
        if (writer.HasError)
        {
            return false;
        }

        output[3] = CalculateCrc(output);
        return true;
    }

    private static byte CalculateCrc(byte[] frame)
    {
        byte crc = 0x0F;
        UpdateCrc(ref crc, frame[1]);
        UpdateCrc(ref crc, frame[2]);
        for (int index = 4; index <= 12; index++)
        {
            UpdateCrc(ref crc, frame[index]);
        }

        return crc;
    }

    private static void UpdateCrc(ref byte crc, byte value)
    {
        for (int shift = 7; shift >= 0; shift--)
        {
            bool highBit = (((value >> shift) & 1) ^ (crc >> 7)) != 0;
            crc <<= 1;
            if (highBit)
            {
                crc ^= 0x1D;
            }
        }
    }
}
