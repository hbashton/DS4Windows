// Derived from SbcSharp commit 8fd1417b142bb1be69b119c23ccfac360ee15ef4.
// Modified for DS4Windows integration; licensed under Apache-2.0.

using System;

#nullable enable

namespace SBC;

/// <summary>
/// Bitstream reader/writer for SBC encoding and decoding
/// </summary>
internal class SbcBitStream
{
    private byte[] _data = Array.Empty<byte>();
    private int _maxBytes;
    private bool _isReader;

    private int _bytePosition;
    private uint _accumulator;
    private int _bitsInAccumulator;
    private bool _error;

    public SbcBitStream(byte[] data, int size, bool isReader)
    {
        Reset(data, size, isReader);
    }

    public void Reset(byte[] data, int size, bool isReader)
    {
        _data = data;
        _maxBytes = size;
        _isReader = isReader;
        _bytePosition = 0;
        _accumulator = 0;
        _bitsInAccumulator = 0;
        _error = false;
    }

    public bool HasError => _error;

    // _bytePosition counts bytes already flushed; the accumulator contains
    // the remaining unwritten bits. The upstream expression counted an extra
    // 32-bit word and could over-pad 57-byte mSBC frames.
    public int BitPosition => _isReader ?
        (_bytePosition * 8) - _bitsInAccumulator :
        (_bytePosition * 8) + _bitsInAccumulator;

    /// <summary>
    /// Read bits from the stream (1-32 bits)
    /// </summary>
    public uint GetBits(int numBits)
    {
        if (numBits == 0)
            return 0;

        if (numBits < 0 || numBits > 32)
        {
            _error = true;
            return 0;
        }

        // Refill accumulator if needed
        while (_bitsInAccumulator < numBits && _bytePosition < _maxBytes)
        {
            _accumulator = (_accumulator << 8) | _data[_bytePosition++];
            _bitsInAccumulator += 8;
        }

        // Check if we have enough bits
        if (_bitsInAccumulator < numBits)
        {
            // Not enough data - return what we have padded with zeros
            uint result = _accumulator << (numBits - _bitsInAccumulator);
            _bitsInAccumulator = 0;
            _accumulator = 0;
            _error = true;
            return result & ((1u << numBits) - 1);
        }

        // Extract the requested bits
        _bitsInAccumulator -= numBits;
        uint value = (_accumulator >> _bitsInAccumulator) & ((1u << numBits) - 1);
        _accumulator &= (1u << _bitsInAccumulator) - 1;

        return value;
    }

    /// <summary>
    /// Read bits and verify they match expected value
    /// </summary>
    public void GetFixedBits(int numBits, uint expectedValue)
    {
        uint value = GetBits(numBits);
        if (value != expectedValue)
            _error = true;
    }

    /// <summary>
    /// Write bits to the stream (0-32 bits)
    /// </summary>
    public void PutBits(uint value, int numBits)
    {
        if (numBits == 0)
            return;

        if (numBits < 0 || numBits > 32)
        {
            _error = true;
            return;
        }

        // Mask the value to the requested number of bits
        value &= (1u << numBits) - 1;

        // Add to accumulator
        _accumulator = (_accumulator << numBits) | value;
        _bitsInAccumulator += numBits;

        // Flush full bytes
        while (_bitsInAccumulator >= 8)
        {
            if (_bytePosition >= _maxBytes)
            {
                _error = true;
                return;
            }

            _bitsInAccumulator -= 8;
            _data[_bytePosition++] = (byte)(_accumulator >> _bitsInAccumulator);
            _accumulator &= (1u << _bitsInAccumulator) - 1;
        }
    }

    /// <summary>
    /// Flush any remaining bits in the accumulator to the output
    /// </summary>
    public void Flush()
    {
        if (_bitsInAccumulator > 0)
        {
            if (_bytePosition >= _maxBytes)
            {
                _error = true;
                return;
            }

            // Pad with zeros and write the final byte
            _data[_bytePosition++] = (byte)(_accumulator << (8 - _bitsInAccumulator));
            _bitsInAccumulator = 0;
            _accumulator = 0;
        }
    }
}
