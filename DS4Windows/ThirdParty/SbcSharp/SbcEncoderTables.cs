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
    // Bluetooth A2DP specification, SBC Appendix B, Table 8.24. SbcSharp's
    // shortened/repeated fixed-point window below does not implement the
    // complete 80-tap analysis filter and produces severe gain ripple.
    private static readonly double[] Prototype8 =
    {
        0.00000000E+00,  1.56575398E-04,  3.43256425E-04,  5.54620202E-04,
        8.23919506E-04,  1.13992507E-03,  1.47640169E-03,  1.78371725E-03,
        2.01182542E-03,  2.10371989E-03,  1.99454554E-03,  1.61656283E-03,
        9.02154502E-04, -1.78805361E-04, -1.64973098E-03, -3.49717454E-03,
        5.65949473E-03,  8.02941163E-03,  1.04584443E-02,  1.27472335E-02,
        1.46525263E-02,  1.59045603E-02,  1.62208471E-02,  1.53184106E-02,
        1.29371806E-02,  8.85757540E-03,  2.92408442E-03, -4.91578024E-03,
       -1.46404076E-02, -2.61098752E-02, -3.90751381E-02, -5.31873032E-02,
        6.79989431E-02,  8.29847578E-02,  9.75753918E-02,  1.11196689E-01,
        1.23264548E-01,  1.33264415E-01,  1.40753505E-01,  1.45389847E-01,
        1.46955068E-01,  1.45389847E-01,  1.40753505E-01,  1.33264415E-01,
        1.23264548E-01,  1.11196689E-01,  9.75753918E-02,  8.29847578E-02,
       -6.79989431E-02, -5.31873032E-02, -3.90751381E-02, -2.61098752E-02,
       -1.46404076E-02, -4.91578024E-03,  2.92408442E-03,  8.85757540E-03,
        1.29371806E-02,  1.53184106E-02,  1.62208471E-02,  1.59045603E-02,
        1.46525263E-02,  1.27472335E-02,  1.04584443E-02,  8.02941163E-03,
       -5.65949473E-03, -3.49717454E-03, -1.64973098E-03, -1.78805361E-04,
        9.02154502E-04,  1.61656283E-03,  1.99454554E-03,  2.10371989E-03,
        2.01182542E-03,  1.78371725E-03,  1.47640169E-03,  1.13992507E-03,
        8.23919506E-04,  5.54620202E-04,  3.43256425E-04,  1.56575398E-04,
    };

    public static readonly double[][] AnalysisMatrix8 = BuildAnalysisMatrix8();

    private static double[][] BuildAnalysisMatrix8()
    {
        const int subbands = 8;
        var result = new double[subbands][];
        for (int band = 0; band < subbands; band++)
        {
            result[band] = new double[Prototype8.Length];
            for (int tap = 0; tap < Prototype8.Length; tap++)
            {
                // The published table is segment-folded for SBC's optimized
                // flow graph. Undo the folding for direct-form convolution.
                double prototype = Prototype8[tap] *
                    ((((tap / 16) & 1) == 0) ? 1.0 : -1.0);
                result[band][tap] = prototype * Math.Cos(
                    (band + 0.5) * (tap - subbands / 2.0) *
                    Math.PI / subbands);
            }
        }

        return result;
    }

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
