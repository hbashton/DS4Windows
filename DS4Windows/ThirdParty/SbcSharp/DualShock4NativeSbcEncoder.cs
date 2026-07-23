using System;
using System.Runtime.InteropServices;

#nullable enable

namespace SBC;

/// <summary>
/// Diagnostic wrapper around the reference libsbc encoder. It is selected
/// only through DS4WINDOWS_DS4_AUDIO_ENCODER=native and keeps the production
/// managed encoder available for controlled A/B captures.
/// </summary>
internal sealed class DualShock4NativeSbcEncoder : IDisposable
{
    private const string LibraryName = "libs/libsbc.dll";
    private const int SamplesPerChannel = 128;
    private readonly short[] interleaved = new short[SamplesPerChannel * 2];
    private SbcState state;

    public DualShock4NativeSbcEncoder()
    {
        if (SbcInit(ref state, 0) < 0)
        {
            throw new InvalidOperationException("libsbc initialization failed.");
        }

        state.Frequency = 0x01; // 32 kHz
        state.Blocks = 0x03; // 16 blocks
        state.Subbands = 0x01; // 8 subbands
        state.Mode = 0x03; // joint stereo
        state.Allocation = 0x01; // SNR
        state.Bitpool = 48;
        state.Endian = 0x00; // little endian PCM
        if (SbcGetCodeSize(ref state) != 512 ||
            SbcGetFrameLength(ref state) != 109)
        {
            Dispose();
            throw new InvalidOperationException(
                "libsbc did not accept the DualShock 4 SBC format.");
        }
    }

    public unsafe bool Encode(short[] left, short[] right, byte[] output)
    {
        if (left == null || right == null || output == null ||
            left.Length < SamplesPerChannel ||
            right.Length < SamplesPerChannel || output.Length < 109)
        {
            return false;
        }

        for (int sample = 0; sample < SamplesPerChannel; sample++)
        {
            interleaved[sample * 2] = left[sample];
            interleaved[sample * 2 + 1] = right[sample];
        }

        ulong written = 0;
        fixed (short* input = interleaved)
        fixed (byte* destination = output)
        {
            long consumed = SbcEncode(ref state, input, 512,
                destination, (ulong)output.Length, &written);
            return consumed == 512 && written == 109;
        }
    }

    public void Dispose()
    {
        if (state.Private != IntPtr.Zero ||
            state.PrivateAllocationBase != IntPtr.Zero)
        {
            SbcFinish(ref state);
            state = default;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SbcState
    {
        public uint Flags;
        public byte Frequency;
        public byte Blocks;
        public byte Subbands;
        public byte Mode;
        public byte Allocation;
        public byte Bitpool;
        public byte Endian;
        public IntPtr Private;
        public IntPtr PrivateAllocationBase;
    }

    [DllImport(LibraryName, EntryPoint = "sbc_init",
        CallingConvention = CallingConvention.StdCall)]
    private static extern int SbcInit(ref SbcState state, uint flags);

    [DllImport(LibraryName, EntryPoint = "sbc_encode",
        CallingConvention = CallingConvention.StdCall)]
    private static extern unsafe long SbcEncode(ref SbcState state,
        void* input, ulong inputLength, void* output, ulong outputLength,
        ulong* written);

    [DllImport(LibraryName, EntryPoint = "sbc_get_codesize",
        CallingConvention = CallingConvention.StdCall)]
    private static extern ulong SbcGetCodeSize(ref SbcState state);

    [DllImport(LibraryName, EntryPoint = "sbc_get_frame_length",
        CallingConvention = CallingConvention.StdCall)]
    private static extern ulong SbcGetFrameLength(ref SbcState state);

    [DllImport(LibraryName, EntryPoint = "sbc_finish",
        CallingConvention = CallingConvention.StdCall)]
    private static extern void SbcFinish(ref SbcState state);
}
