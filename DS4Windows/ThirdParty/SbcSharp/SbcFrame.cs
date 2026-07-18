// Derived from SbcSharp commit 8fd1417b142bb1be69b119c23ccfac360ee15ef4.
// Modified for DS4Windows integration; licensed under Apache-2.0.

using System;

#nullable enable

namespace SBC;

/// <summary>
/// SBC frame configuration and parameters
/// </summary>
public class SbcFrame
{
    /// <summary>
    /// Maximum number of subbands
    /// </summary>
    public const int MaxSubbands = 8;

    /// <summary>
    /// Maximum number of blocks
    /// </summary>
    public const int MaxBlocks = 16;

    /// <summary>
    /// Maximum samples per frame
    /// </summary>
    public const int MaxSamples = MaxBlocks * MaxSubbands;

    /// <summary>
    /// SBC frame header size in bytes
    /// </summary>
    public const int HeaderSize = 4;

    /// <summary>
    /// mSBC samples per frame (fixed at 120)
    /// </summary>
    public const int MsbcSamples = 120;

    /// <summary>
    /// mSBC frame size in bytes (fixed at 57)
    /// </summary>
    public const int MsbcSize = 57;

    /// <summary>
    /// Whether this is an mSBC (Bluetooth HFP) frame
    /// </summary>
    public bool IsMsbc { get; set; }

    /// <summary>
    /// Sampling frequency
    /// </summary>
    public SbcFrequency Frequency { get; set; }

    /// <summary>
    /// Channel mode
    /// </summary>
    public SbcMode Mode { get; set; }

    /// <summary>
    /// Bit allocation method
    /// </summary>
    public SbcBitAllocationMethod AllocationMethod { get; set; }

    /// <summary>
    /// Number of blocks (4, 8, 12, or 16)
    /// </summary>
    public int Blocks { get; set; }

    /// <summary>
    /// Number of subbands (4 or 8)
    /// </summary>
    public int Subbands { get; set; }

    /// <summary>
    /// Bitpool value (controls quality/bitrate)
    /// </summary>
    public int Bitpool { get; set; }

    /// <summary>
    /// Get the sampling frequency in Hz
    /// </summary>
    public int GetFrequencyHz()
    {
        return Frequency switch
        {
            SbcFrequency.Freq16K => 16000,
            SbcFrequency.Freq32K => 32000,
            SbcFrequency.Freq44K1 => 44100,
            SbcFrequency.Freq48K => 48000,
            _ => 0
        };
    }

    /// <summary>
    /// Get the algorithmic codec delay in samples (encoding + decoding)
    /// </summary>
    public int GetDelay()
    {
        return 10 * Subbands;
    }

    /// <summary>
    /// Check if the frame configuration is valid
    /// </summary>
    public bool IsValid()
    {
        // Check number of blocks
        if (Blocks < 4 || Blocks > 16 || (!IsMsbc && Blocks % 4 != 0))
            return false;

        // Check number of subbands
        if (Subbands != 4 && Subbands != 8)
            return false;

        // Validate bitpool value
        bool twoChannels = Mode != SbcMode.Mono;
        bool dualMode = Mode == SbcMode.DualChannel;
        bool jointMode = Mode == SbcMode.JointStereo;
        bool stereoMode = jointMode || Mode == SbcMode.Stereo;

        int maxBits = ((16 * Subbands * Blocks) << (twoChannels ? 1 : 0)) -
                      (HeaderSize * 8) -
                      ((4 * Subbands) << (twoChannels ? 1 : 0)) -
                      (jointMode ? Subbands : 0);

        int maxBitpool = Math.Min(
            maxBits / (Blocks << (dualMode ? 1 : 0)),
            (16 << (stereoMode ? 1 : 0)) * Subbands
        );

        return Bitpool <= maxBitpool;
    }

    /// <summary>
    /// Get the frame size in bytes
    /// </summary>
    public int GetFrameSize()
    {
        if (!IsValid())
            return 0;

        bool twoChannels = Mode != SbcMode.Mono;
        bool dualMode = Mode == SbcMode.DualChannel;
        bool jointMode = Mode == SbcMode.JointStereo;

        int nbits = ((4 * Subbands) << (twoChannels ? 1 : 0)) +
                    ((Blocks * Bitpool) << (dualMode ? 1 : 0)) +
                    (jointMode ? Subbands : 0);

        return HeaderSize + ((nbits + 7) >> 3);
    }

    /// <summary>
    /// Get the bitrate in bits per second
    /// </summary>
    public int GetBitrate()
    {
        if (!IsValid())
            return 0;

        int nsamples = Blocks * Subbands;
        int nbits = 8 * GetFrameSize();

        return (nbits * GetFrequencyHz()) / nsamples;
    }

    /// <summary>
    /// Create a standard mSBC frame configuration
    /// </summary>
    public static SbcFrame CreateMsbc()
    {
        return new SbcFrame
        {
            IsMsbc = true,
            Mode = SbcMode.Mono,
            Frequency = SbcFrequency.Freq16K,
            AllocationMethod = SbcBitAllocationMethod.Loudness,
            Subbands = 8,
            Blocks = 15,
            Bitpool = 26
        };
    }
}
