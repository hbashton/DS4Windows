// Derived from SbcSharp commit 8fd1417b142bb1be69b119c23ccfac360ee15ef4.
// Modified for DS4Windows integration; licensed under Apache-2.0.

using System;

#nullable enable

namespace SBC;

/// <summary>
/// SBC audio decoder - converts SBC frames to PCM samples
/// </summary>
public class SbcDecoder
{
    private readonly DecoderState[] _channelStates;
    private int _numChannels;
    private int _numBlocks;
    private int _numSubbands;

    public SbcDecoder()
    {
        _channelStates = new DecoderState[2];
        _channelStates[0] = new DecoderState();
        _channelStates[1] = new DecoderState();
        Reset();
    }

    /// <summary>
    /// Reset decoder state
    /// </summary>
    public void Reset()
    {
        _channelStates[0].Reset();
        _channelStates[1].Reset();
        _numChannels = 0;
        _numBlocks = 0;
        _numSubbands = 0;
    }

    /// <summary>
    /// Probe SBC data and extract frame parameters without full decoding
    /// </summary>
    public SbcFrame? Probe(byte[] data)
    {
        if (data == null || data.Length < SbcFrame.HeaderSize)
            return null;

        var bits = new SbcBitStream(data, SbcFrame.HeaderSize, isReader: true);
        var frame = new SbcFrame();

        if (!DecodeHeader(bits, frame, out _))
            return null;

        return bits.HasError ? null : frame;
    }

    /// <summary>
    /// Decode an SBC frame to PCM samples
    /// </summary>
    /// <param name="sbcData">SBC encoded frame data</param>
    /// <param name="pcmLeft">Output PCM buffer for left channel</param>
    /// <param name="pcmRight">Output PCM buffer for right channel (can be null for mono)</param>
    /// <param name="frame">Output frame parameters</param>
    /// <returns>True on success, false on error</returns>
    public bool Decode(byte[] sbcData, out short[] pcmLeft, out short[]? pcmRight, out SbcFrame frame)
    {
        pcmLeft = Array.Empty<short>();
        pcmRight = null;
        frame = new SbcFrame();

        if (sbcData == null || sbcData.Length < SbcFrame.HeaderSize)
            return false;

        // Decode header
        var headerBits = new SbcBitStream(sbcData, SbcFrame.HeaderSize, isReader: true);
        if (!DecodeHeader(headerBits, frame, out int crc) || headerBits.HasError)
            return false;

        int frameSize = frame.GetFrameSize();
        if (sbcData.Length < frameSize)
            return false;

        // Verify CRC
        int computedCrc = SbcTables.ComputeCrc(frame, sbcData, sbcData.Length);
        if (computedCrc != crc)
            return false;

        // Decode frame data
        var dataBits = new SbcBitStream(sbcData, frameSize, isReader: true);
        dataBits.GetBits(SbcFrame.HeaderSize * 8); // Skip header

        short[][] sbSamples = new short[2][];
        sbSamples[0] = new short[SbcFrame.MaxSamples];
        sbSamples[1] = new short[SbcFrame.MaxSamples];
        int[] sbScale = new int[2];

        DecodeFrameData(dataBits, frame, sbSamples, sbScale);
        if (dataBits.HasError)
            return false;

        _numChannels = frame.Mode != SbcMode.Mono ? 2 : 1;
        _numBlocks = frame.Blocks;
        _numSubbands = frame.Subbands;

        // Synthesize PCM
        int samplesPerChannel = _numBlocks * _numSubbands;
        pcmLeft = new short[samplesPerChannel];

        Synthesize(_channelStates[0], _numBlocks, _numSubbands,
                   sbSamples[0], sbScale[0], pcmLeft, 1);

        if (frame.Mode != SbcMode.Mono)
        {
            pcmRight = new short[samplesPerChannel];
            Synthesize(_channelStates[1], _numBlocks, _numSubbands,
                      sbSamples[1], sbScale[1], pcmRight, 1);
        }

        return true;
    }

    private bool DecodeHeader(SbcBitStream bits, SbcFrame frame, out int crc)
    {
        crc = 0;

        uint syncword = bits.GetBits(8);
        frame.IsMsbc = (syncword == 0xad);

        if (frame.IsMsbc)
        {
            bits.GetBits(16); // reserved
            var msbcFrame = SbcFrame.CreateMsbc();
            frame.Frequency = msbcFrame.Frequency;
            frame.Mode = msbcFrame.Mode;
            frame.AllocationMethod = msbcFrame.AllocationMethod;
            frame.Blocks = msbcFrame.Blocks;
            frame.Subbands = msbcFrame.Subbands;
            frame.Bitpool = msbcFrame.Bitpool;
        }
        else if (syncword == 0x9c)
        {
            uint freq = bits.GetBits(2);
            frame.Frequency = (SbcFrequency)freq;

            uint blocks = bits.GetBits(2);
            frame.Blocks = (int)((1 + blocks) << 2);

            uint mode = bits.GetBits(2);
            frame.Mode = (SbcMode)mode;

            uint bam = bits.GetBits(1);
            frame.AllocationMethod = (SbcBitAllocationMethod)bam;

            uint subbands = bits.GetBits(1);
            frame.Subbands = (int)((1 + subbands) << 2);

            frame.Bitpool = (int)bits.GetBits(8);
        }
        else
        {
            return false;
        }

        crc = (int)bits.GetBits(8);

        return frame.IsValid();
    }

    private void DecodeFrameData(SbcBitStream bits, SbcFrame frame,
                                 short[][] sbSamples, int[] sbScale)
    {
        int nchannels = frame.Mode != SbcMode.Mono ? 2 : 1;
        int nsubbands = frame.Subbands;

        // Decode joint stereo mask
        uint mjoint = 0;
        if (frame.Mode == SbcMode.JointStereo)
        {
            uint v = bits.GetBits(nsubbands);
            if (nsubbands == 4)
            {
                mjoint = ((0x00u) << 3) | ((v & 0x02) << 1) |
                        ((v & 0x04) >> 1) | ((v & 0x08) >> 3);
            }
            else
            {
                mjoint = ((0x00u) << 7) | ((v & 0x02) << 5) |
                        ((v & 0x04) << 3) | ((v & 0x08) << 1) |
                        ((v & 0x10) >> 1) | ((v & 0x20) >> 3) |
                        ((v & 0x40) >> 5) | ((v & 0x80) >> 7);
            }
        }

        // Decode scale factors
        int[][] scaleFactors = new int[2][];
        scaleFactors[0] = new int[SbcFrame.MaxSubbands];
        scaleFactors[1] = new int[SbcFrame.MaxSubbands];

        for (int ch = 0; ch < nchannels; ch++)
            for (int sb = 0; sb < nsubbands; sb++)
                scaleFactors[ch][sb] = (int)bits.GetBits(4);

        // Compute bit allocation
        int[][] nbits = new int[2][];
        nbits[0] = new int[SbcFrame.MaxSubbands];
        nbits[1] = new int[SbcFrame.MaxSubbands];

        ComputeBitAllocation(frame, scaleFactors, nbits);
        if (frame.Mode == SbcMode.DualChannel)
        {
            int[][] scaleFactors1 = new int[][] { scaleFactors[1] };
            int[][] nbits1 = new int[][] { nbits[1] };
            ComputeBitAllocation(frame, scaleFactors1, nbits1);
        }

        // Compute scale for output samples
        for (int ch = 0; ch < nchannels; ch++)
        {
            int maxScf = 0;
            for (int sb = 0; sb < nsubbands; sb++)
            {
                int scf = scaleFactors[ch][sb] + (int)((mjoint >> sb) & 1);
                if (scf > maxScf) maxScf = scf;
            }
            sbScale[ch] = (15 - maxScf) - (17 - 16);
        }

        if (frame.Mode == SbcMode.JointStereo)
            sbScale[0] = sbScale[1] = Math.Min(sbScale[0], sbScale[1]);

        // Decode samples
        for (int blk = 0; blk < frame.Blocks; blk++)
        {
            for (int ch = 0; ch < nchannels; ch++)
            {
                for (int sb = 0; sb < nsubbands; sb++)
                {
                    int nbit = nbits[ch][sb];
                    int scf = scaleFactors[ch][sb];
                    int idx = blk * nsubbands + sb;

                    if (nbit == 0)
                    {
                        sbSamples[ch][idx] = 0;
                        continue;
                    }

                    int sample = (int)bits.GetBits(nbit);
                    sample = ((sample << 1) | 1) * SbcTables.RangeScale[nbit - 1];
                    sbSamples[ch][idx] = (short)((sample - (1 << 28)) >> (28 - ((scf + 1) + sbScale[ch])));
                }
            }
        }

        // Uncouple joint stereo
        for (int sb = 0; sb < nsubbands; sb++)
        {
            if (((mjoint >> sb) & 1) == 0)
                continue;

            for (int blk = 0; blk < frame.Blocks; blk++)
            {
                int idx = blk * nsubbands + sb;
                short s0 = sbSamples[0][idx];
                short s1 = sbSamples[1][idx];
                sbSamples[0][idx] = (short)(s0 + s1);
                sbSamples[1][idx] = (short)(s0 - s1);
            }
        }

        // Skip padding
        int paddingBits = 8 - (bits.BitPosition % 8);
        if (paddingBits < 8)
            bits.GetBits(paddingBits);
    }

    private void ComputeBitAllocation(SbcFrame frame, int[][] scaleFactors, int[][] nbits)
    {
        int[] loudnessOffset = frame.Subbands == 4
            ? SbcTables.LoudnessOffset4[(int)frame.Frequency]
            : SbcTables.LoudnessOffset8[(int)frame.Frequency];

        bool stereoMode = frame.Mode == SbcMode.Stereo || frame.Mode == SbcMode.JointStereo;
        int nsubbands = frame.Subbands;
        int nchannels = stereoMode ? 2 : 1;

        int[][] bitneeds = new int[2][];
        bitneeds[0] = new int[SbcFrame.MaxSubbands];
        bitneeds[1] = new int[SbcFrame.MaxSubbands];
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

    private void Synthesize(DecoderState state, int nblocks, int nsubbands,
                           short[] input, int scale, short[] output, int pitch)
    {
        for (int blk = 0; blk < nblocks; blk++)
        {
            int inOffset = blk * nsubbands;
            int outOffset = blk * nsubbands * pitch;

            if (nsubbands == 4)
                Synthesize4(state, input, inOffset, scale, output, outOffset, pitch);
            else
                Synthesize8(state, input, inOffset, scale, output, outOffset, pitch);
        }
    }

    private void Synthesize4(DecoderState state, short[] input, int inOffset,
                            int scale, short[] output, int outOffset, int pitch)
    {
        // Perform DCT and windowing for 4 subbands
        int dctIdx = state.Index != 0 ? 10 - state.Index : 0;
        int odd = dctIdx & 1;

        Dct4(input, inOffset, scale, state.V[odd], state.V[1 - odd], dctIdx);
        ApplyWindow4(state.V[odd], state.Index, output, outOffset, pitch);

        state.Index = state.Index < 9 ? state.Index + 1 : 0;
    }

    private void Synthesize8(DecoderState state, short[] input, int inOffset,
                            int scale, short[] output, int outOffset, int pitch)
    {
        // Perform DCT and windowing for 8 subbands
        int dctIdx = state.Index != 0 ? 10 - state.Index : 0;
        int odd = dctIdx & 1;

        Dct8(input, inOffset, scale, state.V[odd], state.V[1 - odd], dctIdx);
        ApplyWindow8(state.V[odd], state.Index, output, outOffset, pitch);

        state.Index = state.Index < 9 ? state.Index + 1 : 0;
    }

    private void Dct4(short[] input, int offset, int scale,
                     short[][] out0, short[][] out1, int idx)
    {
        var cos8 = SbcTables.Cos8;

        short s03 = (short)((input[offset + 0] + input[offset + 3]) >> 1);
        short d03 = (short)((input[offset + 0] - input[offset + 3]) >> 1);
        short s12 = (short)((input[offset + 1] + input[offset + 2]) >> 1);
        short d12 = (short)((input[offset + 1] - input[offset + 2]) >> 1);

        int a0 = (s03 - s12) * cos8[2];
        int b1 = -(s03 + s12) << 13;
        int a1 = d03 * cos8[3] - d12 * cos8[1];
        int b0 = -d03 * cos8[1] - d12 * cos8[3];

        int shr = 12 + scale;
        a0 = (a0 + (1 << (shr - 1))) >> shr;
        b0 = (b0 + (1 << (shr - 1))) >> shr;
        a1 = (a1 + (1 << (shr - 1))) >> shr;
        b1 = (b1 + (1 << (shr - 1))) >> shr;

        out0[0][idx] = SbcTables.Saturate16(a0);
        out0[3][idx] = SbcTables.Saturate16(-a1);
        out0[1][idx] = SbcTables.Saturate16(a1);
        out0[2][idx] = SbcTables.Saturate16(0);

        out1[0][idx] = SbcTables.Saturate16(-a0);
        out1[3][idx] = SbcTables.Saturate16(b0);
        out1[1][idx] = SbcTables.Saturate16(b0);
        out1[2][idx] = SbcTables.Saturate16(b1);
    }

    private void Dct8(short[] input, int offset, int scale,
                     short[][] out0, short[][] out1, int idx)
    {
        var cos16 = SbcTables.Cos16;

        short s07 = (short)((input[offset + 0] + input[offset + 7]) >> 1);
        short d07 = (short)((input[offset + 0] - input[offset + 7]) >> 1);
        short s16 = (short)((input[offset + 1] + input[offset + 6]) >> 1);
        short d16 = (short)((input[offset + 1] - input[offset + 6]) >> 1);
        short s25 = (short)((input[offset + 2] + input[offset + 5]) >> 1);
        short d25 = (short)((input[offset + 2] - input[offset + 5]) >> 1);
        short s34 = (short)((input[offset + 3] + input[offset + 4]) >> 1);
        short d34 = (short)((input[offset + 3] - input[offset + 4]) >> 1);

        int a0 = ((s07 + s34) - (s25 + s16)) * cos16[4];
        int b3 = (-(s07 + s34) - (s25 + s16)) << 13;
        int a2 = (s07 - s34) * cos16[6] + (s25 - s16) * cos16[2];
        int b1 = (s34 - s07) * cos16[2] + (s25 - s16) * cos16[6];
        int a1 = d07 * cos16[5] - d16 * cos16[1] + d25 * cos16[7] + d34 * cos16[3];
        int b2 = -d07 * cos16[1] - d16 * cos16[3] - d25 * cos16[5] - d34 * cos16[7];
        int a3 = d07 * cos16[7] - d16 * cos16[5] + d25 * cos16[3] - d34 * cos16[1];
        int b0 = -d07 * cos16[3] + d16 * cos16[7] + d25 * cos16[1] + d34 * cos16[5];

        int shr = 12 + scale;
        a0 = (a0 + (1 << (shr - 1))) >> shr; b0 = (b0 + (1 << (shr - 1))) >> shr;
        a1 = (a1 + (1 << (shr - 1))) >> shr; b1 = (b1 + (1 << (shr - 1))) >> shr;
        a2 = (a2 + (1 << (shr - 1))) >> shr; b2 = (b2 + (1 << (shr - 1))) >> shr;
        a3 = (a3 + (1 << (shr - 1))) >> shr; b3 = (b3 + (1 << (shr - 1))) >> shr;

        out0[0][idx] = SbcTables.Saturate16(a0); out0[7][idx] = SbcTables.Saturate16(-a1);
        out0[1][idx] = SbcTables.Saturate16(a1); out0[6][idx] = SbcTables.Saturate16(-a2);
        out0[2][idx] = SbcTables.Saturate16(a2); out0[5][idx] = SbcTables.Saturate16(-a3);
        out0[3][idx] = SbcTables.Saturate16(a3); out0[4][idx] = SbcTables.Saturate16(0);

        out1[0][idx] = SbcTables.Saturate16(-a0); out1[7][idx] = SbcTables.Saturate16(b0);
        out1[1][idx] = SbcTables.Saturate16(b0);  out1[6][idx] = SbcTables.Saturate16(b1);
        out1[2][idx] = SbcTables.Saturate16(b1);  out1[5][idx] = SbcTables.Saturate16(b2);
        out1[3][idx] = SbcTables.Saturate16(b2);  out1[4][idx] = SbcTables.Saturate16(b3);
    }

    private void ApplyWindow4(short[][] input, int index, short[] output, int offset, int pitch)
    {
        var window = SbcDecoderTables.Window4;

        for (int i = 0; i < 4; i++)
        {
            int s = 0;
            for (int j = 0; j < 10; j++)
                s += input[i][j] * window[i][index + j];

            output[offset + i * pitch] = SbcTables.Saturate16((s + (1 << 12)) >> 13);
        }
    }

    private void ApplyWindow8(short[][] input, int index, short[] output, int offset, int pitch)
    {
        var window = SbcDecoderTables.Window8;

        for (int i = 0; i < 8; i++)
        {
            int s = 0;
            for (int j = 0; j < 10; j++)
                s += input[i][j] * window[i][index + j];

            output[offset + i * pitch] = SbcTables.Saturate16((s + (1 << 12)) >> 13);
        }
    }

    private class DecoderState
    {
        public int Index;
        public short[][][] V; // [2][MaxSubbands][10]

        public DecoderState()
        {
            V = new short[2][][];
            V[0] = new short[SbcFrame.MaxSubbands][];
            V[1] = new short[SbcFrame.MaxSubbands][];
            for (int i = 0; i < SbcFrame.MaxSubbands; i++)
            {
                V[0][i] = new short[10];
                V[1][i] = new short[10];
            }
            Reset();
        }

        public void Reset()
        {
            Index = 0;
            for (int odd = 0; odd < 2; odd++)
                for (int sb = 0; sb < SbcFrame.MaxSubbands; sb++)
                    Array.Clear(V[odd][sb], 0, 10);
        }
    }
}
