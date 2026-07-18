// Derived from SbcSharp commit 8fd1417b142bb1be69b119c23ccfac360ee15ef4.
// Modified for DS4Windows integration; licensed under Apache-2.0.

using System;

#nullable enable

namespace SBC;

/// <summary>
/// Windowing coefficient tables and matrices for SBC encoder analysis filter
/// </summary>
internal static class SbcEncoderTables
{
    /// <summary>
    /// Windowing coefficients for 4 subbands (fixed-point 2.13 format)
    /// Transposed and scrambled to fit circular buffer and DCT symmetry
    /// Extended to 20 elements to support idx range 0-4 plus offset of 5
    /// </summary>
    public static readonly short[][] Window4 = new short[4][]
    {
        new short[20] {    0,  358, 4443,-4443, -358,    0,  358, 4443,-4443, -358,    0,  358, 4443,-4443, -358,    0,  358, 4443,-4443, -358 },
        new short[20] {   49,  946, 8082, -944,   61,   49,  946, 8082, -944,   61,   49,  946, 8082, -944,   61,   49,  946, 8082, -944,   61 },
        new short[20] {   18,  670, 6389,-2544, -100,   18,  670, 6389,-2544, -100,   18,  670, 6389,-2544, -100,   18,  670, 6389,-2544, -100 },
        new short[20] {   90, 1055, 9235,  201,  128,   90, 1055, 9235,  201,  128,   90, 1055, 9235,  201,  128,   90, 1055, 9235,  201,  128 }
    };

    /// <summary>
    /// Windowing coefficients for 8 subbands (fixed-point 2.13 format)
    /// Transposed and scrambled to fit circular buffer and DCT symmetry
    /// Extended to 20 elements to support idx range 0-4 plus offset of 5
    /// </summary>
    public static readonly short[][] Window8 = new short[8][]
    {
        new short[20] {    0,  185, 2228,-2228, -185,    0,  185, 2228,-2228, -185,    0,  185, 2228,-2228, -185,    0,  185, 2228,-2228, -185 },
        new short[20] {   27,  480, 4039, -480,   30,   27,  480, 4039, -480,   30,   27,  480, 4039, -480,   30,   27,  480, 4039, -480,   30 },
        new short[20] {    5,  263, 2719,-1743, -115,    5,  263, 2719,-1743, -115,    5,  263, 2719,-1743, -115,    5,  263, 2719,-1743, -115 },
        new short[20] {   58,  502, 4764,  290,   69,   58,  502, 4764,  290,   69,   58,  502, 4764,  290,   69,   58,  502, 4764,  290,   69 },
        new short[20] {   11,  343, 3197,-1280,  -54,   11,  343, 3197,-1280,  -54,   11,  343, 3197,-1280,  -54,   11,  343, 3197,-1280,  -54 },
        new short[20] {   48,  532, 4612,   96,   65,   48,  532, 4612,   96,   65,   48,  532, 4612,   96,   65,   48,  532, 4612,   96,   65 },
        new short[20] {   18,  418, 3644, -856,   -6,   18,  418, 3644, -856,   -6,   18,  418, 3644, -856,   -6,   18,  418, 3644, -856,   -6 },
        new short[20] {   37,  521, 4367, -161,   53,   37,  521, 4367, -161,   53,   37,  521, 4367, -161,   53,   37,  521, 4367, -161,   53 }
    };

    /// <summary>
    /// Cosine matrix for 8-subband DCT (fixed-point 0.13 format)
    /// H(k,i) = sign(x(k,i)) * cos(abs(x(k,i)) * pi/16)
    /// where x(k,i) values are arranged for optimal encoding
    /// </summary>
    public static readonly short[][] CosMatrix8 = new short[8][]
    {
        new short[8] {  5793,  6811,  7568,  8035,   4551,  3135,  1598, 8192 },
        new short[8] { -5793, -1598,  3135,  6811,  -8035, -7568, -4551, 8192 },
        new short[8] { -5793, -8035, -3135,  4551,   1598,  7568,  6811, 8192 },
        new short[8] {  5793, -4551, -7568,  1598,   6811, -3135, -8035, 8192 },
        new short[8] {  5793,  4551, -7568, -1598,  -6811, -3135,  8035, 8192 },
        new short[8] { -5793,  8035, -3135, -4551,  -1598,  7568, -6811, 8192 },
        new short[8] { -5793,  1598,  3135, -6811,   8035, -7568,  4551, 8192 },
        new short[8] {  5793, -6811,  7568, -8035,  -4551,  3135, -1598, 8192 }
    };
}
