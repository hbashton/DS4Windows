// Derived from SbcSharp commit 8fd1417b142bb1be69b119c23ccfac360ee15ef4.
// Modified for DS4Windows integration; licensed under Apache-2.0.

using System;

#nullable enable

namespace SBC;

/// <summary>
/// SBC audio encoder - converts PCM samples to SBC frames
/// </summary>
public class SbcEncoder
{
    private readonly EncoderState[] _channelStates;
    private readonly short[][] _sbSamples =
    {
        new short[SbcFrame.MaxSamples],
        new short[SbcFrame.MaxSamples],
    };
    private readonly int[][] _scaleFactors =
    {
        new int[SbcFrame.MaxSubbands],
        new int[SbcFrame.MaxSubbands],
    };
    private readonly int[][] _nbits =
    {
        new int[SbcFrame.MaxSubbands],
        new int[SbcFrame.MaxSubbands],
    };
    private readonly int[][] _bitNeeds =
    {
        new int[SbcFrame.MaxSubbands],
        new int[SbcFrame.MaxSubbands],
    };
    private readonly int[][] _singleScaleFactors = new int[1][];
    private readonly int[][] _singleNbits = new int[1][];
    private readonly SbcBitStream _dataBits = new SbcBitStream(
        Array.Empty<byte>(), 0, isReader: false);
    private readonly SbcBitStream _headerBits = new SbcBitStream(
        Array.Empty<byte>(), 0, isReader: false);

    public SbcEncoder()
    {
        _channelStates = new EncoderState[2];
        _channelStates[0] = new EncoderState();
        _channelStates[1] = new EncoderState();
        _singleScaleFactors[0] = _scaleFactors[1];
        _singleNbits[0] = _nbits[1];
        Reset();
    }

    /// <summary>
    /// Reset encoder state
    /// </summary>
    public void Reset()
    {
        _channelStates[0].Reset();
        _channelStates[1].Reset();
    }

    /// <summary>
    /// Encode PCM samples to an SBC frame
    /// </summary>
    /// <param name="pcmLeft">Input PCM samples for left channel</param>
    /// <param name="pcmRight">Input PCM samples for right channel (can be null for mono)</param>
    /// <param name="frame">Frame configuration parameters</param>
    /// <returns>Encoded SBC frame data, or null on error</returns>
    public byte[]? Encode(short[] pcmLeft, short[]? pcmRight, SbcFrame frame)
    {
        if (frame == null)
            return null;

        int frameSize = frame.IsMsbc ? SbcFrame.CreateMsbc().GetFrameSize() :
            frame.GetFrameSize();
        if (frameSize <= 0)
            return null;

        byte[] output = new byte[frameSize];
        return Encode(pcmLeft, pcmRight, frame, output) ? output : null;
    }

    /// <summary>
    /// Encodes into a caller-owned buffer. The encoder keeps all analysis and
    /// bit-allocation scratch storage, so realtime callers do not allocate a
    /// graph of temporary arrays for every four milliseconds of audio.
    /// </summary>
    public bool Encode(short[] pcmLeft, short[]? pcmRight, SbcFrame frame,
                       byte[] output)
    {
        if (pcmLeft == null || frame == null)
            return false;

        // Override with mSBC if signaled
        if (frame.IsMsbc)
            frame = SbcFrame.CreateMsbc();

        // Validate frame
        if (!frame.IsValid())
            return false;

        int frameSize = frame.GetFrameSize();
        int samplesPerChannel = frame.Blocks * frame.Subbands;

        if (pcmLeft.Length < samplesPerChannel)
            return false;

        if (frame.Mode != SbcMode.Mono && (pcmRight == null || pcmRight.Length < samplesPerChannel))
            return false;

        if (output == null || output.Length < frameSize)
            return false;

        // Analyze PCM to subband samples
        Analyze(_channelStates[0], frame, pcmLeft, 1, _sbSamples[0]);
        if (frame.Mode != SbcMode.Mono && pcmRight != null)
            Analyze(_channelStates[1], frame, pcmRight, 1, _sbSamples[1]);

        Array.Clear(output, 0, frameSize);

        // Encode frame data
        SbcBitStream dataBits = _dataBits;
        dataBits.Reset(output, frameSize, isReader: false);
        dataBits.PutBits(0, SbcFrame.HeaderSize * 8); // Reserve space for header

        EncodeFrameData(dataBits, frame, _sbSamples);
        dataBits.Flush();

        if (dataBits.HasError)
            return false;

        // Encode header
        SbcBitStream headerBits = _headerBits;
        headerBits.Reset(output, SbcFrame.HeaderSize, isReader: false);
        EncodeHeader(headerBits, frame);
        headerBits.Flush();

        if (headerBits.HasError)
            return false;

        // Compute and set CRC
        int crc = SbcTables.ComputeCrc(frame, output, frameSize);
        if (crc < 0)
            return false;

        output[3] = (byte)crc;

        return true;
    }

    private void EncodeHeader(SbcBitStream bits, SbcFrame frame)
    {
        bits.PutBits(frame.IsMsbc ? 0xadu : 0x9cu, 8);

        if (!frame.IsMsbc)
        {
            bits.PutBits((uint)frame.Frequency, 2);
            bits.PutBits((uint)((frame.Blocks >> 2) - 1), 2);
            bits.PutBits((uint)frame.Mode, 2);
            bits.PutBits((uint)frame.AllocationMethod, 1);
            bits.PutBits((uint)((frame.Subbands >> 2) - 1), 1);
            bits.PutBits((uint)frame.Bitpool, 8);
        }
        else
        {
            bits.PutBits(0, 16); // reserved
        }

        bits.PutBits(0, 8); // CRC placeholder
    }

    private void EncodeFrameData(SbcBitStream bits, SbcFrame frame, short[][] sbSamples)
    {
        int nchannels = frame.Mode != SbcMode.Mono ? 2 : 1;
        int nsubbands = frame.Subbands;

        // Compute scale factors
        int[][] scaleFactors = _scaleFactors;
        uint mjoint = 0;

        if (frame.Mode == SbcMode.JointStereo)
            ComputeScaleFactorsJointStereo(frame, sbSamples, scaleFactors, out mjoint);
        else
            ComputeScaleFactors(frame, sbSamples, scaleFactors);

        // Write joint stereo mask
        if (frame.Mode == SbcMode.JointStereo)
        {
            if (nsubbands == 4)
            {
                uint v = ((mjoint & 0x01) << 3) | ((mjoint & 0x02) << 1) |
                        ((mjoint & 0x04) >> 1) | ((0x00u) >> 3);
                bits.PutBits(v, 4);
            }
            else
            {
                uint v = ((mjoint & 0x01) << 7) | ((mjoint & 0x02) << 5) |
                        ((mjoint & 0x04) << 3) | ((mjoint & 0x08) << 1) |
                        ((mjoint & 0x10) >> 1) | ((mjoint & 0x20) >> 3) |
                        ((mjoint & 0x40) >> 5) | ((0x00u) >> 7);
                bits.PutBits(v, 8);
            }
        }

        // Write scale factors
        for (int ch = 0; ch < nchannels; ch++)
            for (int sb = 0; sb < nsubbands; sb++)
                bits.PutBits((uint)scaleFactors[ch][sb], 4);

        // Compute bit allocation
        int[][] nbits = _nbits;

        ComputeBitAllocation(frame, scaleFactors, nbits);
        if (frame.Mode == SbcMode.DualChannel)
        {
            ComputeBitAllocation(frame, _singleScaleFactors,
                _singleNbits);
        }

        // Apply joint stereo coupling
        for (int sb = 0; sb < nsubbands; sb++)
        {
            if (((mjoint >> sb) & 1) == 0)
                continue;

            for (int blk = 0; blk < frame.Blocks; blk++)
            {
                int idx = blk * nsubbands + sb;
                short s0 = sbSamples[0][idx];
                short s1 = sbSamples[1][idx];
                sbSamples[0][idx] = (short)((s0 + s1) >> 1);
                sbSamples[1][idx] = (short)((s0 - s1) >> 1);
            }
        }

        // Quantize and write samples
        for (int blk = 0; blk < frame.Blocks; blk++)
        {
            for (int ch = 0; ch < nchannels; ch++)
            {
                for (int sb = 0; sb < nsubbands; sb++)
                {
                    int nbit = nbits[ch][sb];
                    if (nbit == 0)
                        continue;

                    int scf = scaleFactors[ch][sb];
                    int idx = blk * nsubbands + sb;
                    int sample = sbSamples[ch][idx];
                    uint range = (1u << nbit) - 1;

                    uint quantized = (uint)((
                        (((long)sample * range) >> (scf + 1)) + range) >> 1);
                    bits.PutBits(quantized, nbit);
                }
            }
        }

        // Write padding
        int paddingBits = 8 - (bits.BitPosition % 8);
        if (paddingBits < 8)
            bits.PutBits(0, paddingBits);
    }

    private void ComputeScaleFactorsJointStereo(SbcFrame frame, short[][] sbSamples,
                                                int[][] scaleFactors, out uint mjoint)
    {
        mjoint = 0;

        for (int sb = 0; sb < frame.Subbands; sb++)
        {
            uint m0 = 0, m1 = 0;
            uint mj0 = 0, mj1 = 0;

            for (int blk = 0; blk < frame.Blocks; blk++)
            {
                int idx = blk * frame.Subbands + sb;
                int s0 = sbSamples[0][idx];
                int s1 = sbSamples[1][idx];

                uint abs0 = (uint)(s0 < 0 ? -s0 : s0);
                uint abs1 = (uint)(s1 < 0 ? -s1 : s1);
                m0 |= abs0;
                m1 |= abs1;

                int sum = (s0 + s1) >> 1;
                int diff = (s0 - s1) >> 1;
                uint absSum = (uint)(sum < 0 ? -sum : sum);
                uint absDiff = (uint)(diff < 0 ? -diff : diff);
                mj0 |= absSum;
                mj1 |= absDiff;
            }

            int scf0 = m0 != 0 ? 31 - SbcTables.CountLeadingZeros(m0) : 0;
            int scf1 = m1 != 0 ? 31 - SbcTables.CountLeadingZeros(m1) : 0;

            int js0 = mj0 != 0 ? 31 - SbcTables.CountLeadingZeros(mj0) : 0;
            int js1 = mj1 != 0 ? 31 - SbcTables.CountLeadingZeros(mj1) : 0;

            if (sb < frame.Subbands - 1 && js0 + js1 < scf0 + scf1)
            {
                mjoint |= 1u << sb;
                scf0 = js0;
                scf1 = js1;
            }

            scaleFactors[0][sb] = scf0;
            scaleFactors[1][sb] = scf1;
        }
    }

    private void ComputeScaleFactors(SbcFrame frame, short[][] sbSamples, int[][] scaleFactors)
    {
        int nchannels = frame.Mode != SbcMode.Mono ? 2 : 1;

        for (int ch = 0; ch < nchannels; ch++)
        {
            for (int sb = 0; sb < frame.Subbands; sb++)
            {
                uint m = 0;

                for (int blk = 0; blk < frame.Blocks; blk++)
                {
                    int idx = blk * frame.Subbands + sb;
                    int sample = sbSamples[ch][idx];
                    uint abs = (uint)(sample < 0 ? -sample : sample);
                    m |= abs;
                }

                int scf = m != 0 ? 31 - SbcTables.CountLeadingZeros(m) : 0;
                scaleFactors[ch][sb] = scf;
            }
        }
    }

    private void ComputeBitAllocation(SbcFrame frame, int[][] scaleFactors, int[][] nbits)
    {
        int[] loudnessOffset = frame.Subbands == 4
            ? SbcTables.LoudnessOffset4[(int)frame.Frequency]
            : SbcTables.LoudnessOffset8[(int)frame.Frequency];

        bool stereoMode = frame.Mode == SbcMode.Stereo || frame.Mode == SbcMode.JointStereo;
        int nsubbands = frame.Subbands;
        int nchannels = stereoMode ? 2 : 1;

        int[][] bitneeds = _bitNeeds;
        int maxBitneed = 0;

        for (int ch = 0; ch < nchannels; ch++)
        {
            for (int sb = 0; sb < nsubbands; sb++)
            {
                int scf = scaleFactors[ch][sb];
                int bitneed;

                if (frame.AllocationMethod == SbcBitAllocationMethod.Loudness)
                {
                    bitneed = scf != 0 ? scf - loudnessOffset[sb] : -5;
                    bitneed >>= (bitneed > 0) ? 1 : 0;
                }
                else
                {
                    bitneed = scf;
                }

                if (bitneed > maxBitneed)
                    maxBitneed = bitneed;

                bitneeds[ch][sb] = bitneed;
            }
        }

        // Bit distribution
        int bitpool = frame.Bitpool;
        int bitcount = 0;
        int bitslice = maxBitneed + 1;

        for (int bc = 0; bc < bitpool; )
        {
            int bs = bitslice--;
            bitcount = bc;
            if (bitcount == bitpool)
                break;

            for (int ch = 0; ch < nchannels; ch++)
            {
                for (int sb = 0; sb < nsubbands; sb++)
                {
                    int bn = bitneeds[ch][sb];
                    bc += (bn >= bs && bn < bs + 15 ? 1 : 0) + (bn == bs ? 1 : 0);
                }
            }
        }

        // Assign bits
        for (int ch = 0; ch < nchannels; ch++)
        {
            for (int sb = 0; sb < nsubbands; sb++)
            {
                int nbit = bitneeds[ch][sb] - bitslice;
                nbits[ch][sb] = nbit < 2 ? 0 : nbit > 16 ? 16 : nbit;
            }
        }

        // Allocate remaining bits
        for (int sb = 0; sb < nsubbands && bitcount < bitpool; sb++)
        {
            for (int ch = 0; ch < nchannels && bitcount < bitpool; ch++)
            {
                int n = (nbits[ch][sb] > 0 && nbits[ch][sb] < 16) ? 1 :
                       (bitneeds[ch][sb] == bitslice + 1 && bitpool > bitcount + 1) ? 2 : 0;
                nbits[ch][sb] += n;
                bitcount += n;
            }
        }

        for (int sb = 0; sb < nsubbands && bitcount < bitpool; sb++)
        {
            for (int ch = 0; ch < nchannels && bitcount < bitpool; ch++)
            {
                int n = nbits[ch][sb] < 16 ? 1 : 0;
                nbits[ch][sb] += n;
                bitcount += n;
            }
        }
    }

    private void Analyze(EncoderState state, SbcFrame frame, short[] input, int pitch, short[] output)
    {
        for (int blk = 0; blk < frame.Blocks; blk++)
        {
            int inOffset = blk * frame.Subbands * pitch;
            int outOffset = blk * frame.Subbands;

            if (frame.Subbands == 4)
                Analyze4(state, input, inOffset, pitch, output, outOffset);
            else
                Analyze8(state, input, inOffset, pitch, output, outOffset);
        }
    }

    private void Analyze4(EncoderState state, short[] input, int inOffset, int pitch, short[] output, int outOffset)
    {
        var window = SbcEncoderTables.Window4;
        var cos8 = SbcTables.Cos8;

        int idx = state.Index >> 1;
        int odd = state.Index & 1;
        int inIdx = idx != 0 ? 5 - idx : 0;

        // Load PCM samples into circular buffer (check bounds)
        state.X[odd][0][inIdx] = inOffset + 3 * pitch < input.Length ? input[inOffset + 3 * pitch] : (short)0;
        state.X[odd][1][inIdx] = inOffset + 1 * pitch < input.Length ? input[inOffset + 1 * pitch] : (short)0;
        state.X[odd][2][inIdx] = inOffset + 2 * pitch < input.Length ? input[inOffset + 2 * pitch] : (short)0;
        state.X[odd][3][inIdx] = inOffset + 0 * pitch < input.Length ? input[inOffset + 0 * pitch] : (short)0;

        // Apply window and process
        int y0 = 0, y1 = 0, y2 = 0, y3 = 0;

        for (int j = 0; j < 5; j++)
        {
            y0 += state.X[odd][0][j] * window[0][idx + j];
            y1 += state.X[odd][2][j] * window[2][idx + j] + state.X[odd][3][j] * window[3][idx + j];
            y3 += state.X[odd][1][j] * window[1][idx + j];
        }

        y0 += state.Y[0];
        state.Y[0] = 0;
        for (int j = 0; j < 5; j++)
            state.Y[0] += state.X[odd][0][j] * window[0][idx + 5 + j];

        y2 = state.Y[1];
        state.Y[1] = 0;
        for (int j = 0; j < 5; j++)
            state.Y[1] += state.X[odd][2][j] * window[2][idx + 5 + j] - state.X[odd][3][j] * window[3][idx + 5 + j];

        for (int j = 0; j < 5; j++)
            y3 += state.X[odd][1][j] * window[1][idx + 5 + j];

        short[] y = new short[4];
        y[0] = SbcTables.Saturate16((y0 + (1 << 14)) >> 15);
        y[1] = SbcTables.Saturate16((y1 + (1 << 14)) >> 15);
        y[2] = SbcTables.Saturate16((y2 + (1 << 14)) >> 15);
        y[3] = SbcTables.Saturate16((y3 + (1 << 14)) >> 15);

        state.Index = state.Index < 9 ? state.Index + 1 : 0;

        // DCT to get subband samples
        int s0 = y[0] * cos8[2] + y[1] * cos8[1] + y[2] * cos8[3] + (y[3] << 13);
        int s1 = -y[0] * cos8[2] + y[1] * cos8[3] - y[2] * cos8[1] + (y[3] << 13);
        int s2 = -y[0] * cos8[2] - y[1] * cos8[3] + y[2] * cos8[1] + (y[3] << 13);
        int s3 = y[0] * cos8[2] - y[1] * cos8[1] - y[2] * cos8[3] + (y[3] << 13);

        output[outOffset + 0] = SbcTables.Saturate16((s0 + (1 << 12)) >> 13);
        output[outOffset + 1] = SbcTables.Saturate16((s1 + (1 << 12)) >> 13);
        output[outOffset + 2] = SbcTables.Saturate16((s2 + (1 << 12)) >> 13);
        output[outOffset + 3] = SbcTables.Saturate16((s3 + (1 << 12)) >> 13);
    }

    private void Analyze8(EncoderState state, short[] input, int inOffset, int pitch, short[] output, int outOffset)
    {
        const int subbands = 8;
        double[] history = state.AnalysisHistory8;
        Array.Copy(history, 0, history, subbands,
            history.Length - subbands);
        for (int sample = 0; sample < subbands; sample++)
        {
            int source = inOffset + sample * pitch;
            history[subbands - 1 - sample] = source < input.Length ?
                input[source] : 0;
        }

        double[][] matrix = SbcEncoderTables.AnalysisMatrix8;
        for (int band = 0; band < subbands; band++)
        {
            double value = 0.0;
            for (int tap = 0; tap < history.Length; tap++)
                value += matrix[band][tap] * history[tap];

            output[outOffset + band] = SbcTables.Saturate16(
                (int)Math.Round(value));
        }
    }

    private class EncoderState
    {
        public int Index;
        public short[][][] X; // [2][MaxSubbands][5]
        public int[] Y;       // [4]
        public double[] AnalysisHistory8;

        public EncoderState()
        {
            X = new short[2][][];
            X[0] = new short[SbcFrame.MaxSubbands][];
            X[1] = new short[SbcFrame.MaxSubbands][];
            for (int i = 0; i < SbcFrame.MaxSubbands; i++)
            {
                X[0][i] = new short[5];
                X[1][i] = new short[5];
            }
            Y = new int[4];
            AnalysisHistory8 = new double[80];
            Reset();
        }

        public void Reset()
        {
            Index = 0;
            for (int odd = 0; odd < 2; odd++)
                for (int sb = 0; sb < SbcFrame.MaxSubbands; sb++)
                    Array.Clear(X[odd][sb], 0, 5);
            Array.Clear(Y, 0, 4);
            Array.Clear(AnalysisHistory8, 0, AnalysisHistory8.Length);
        }
    }
}
