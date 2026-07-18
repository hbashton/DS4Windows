// Derived from SbcSharp commit 8fd1417b142bb1be69b119c23ccfac360ee15ef4.
// Modified for DS4Windows integration; licensed under Apache-2.0.

using System;

#nullable enable

namespace SBC;

/// <summary>
/// Windowing coefficient tables for SBC decoder synthesis filter
/// </summary>
internal static class SbcDecoderTables
{
    /// <summary>
    /// Windowing coefficients for 4 subbands (fixed-point 2.13 format)
    /// Duplicated and transposed to fit circular buffer
    /// </summary>
    public static readonly short[][] Window4 = new short[4][]
    {
        new short[20] {
               0, -126,  -358, -848, -4443, -9644, 4443,  -848,  358, -126,
               0, -126,  -358, -848, -4443, -9644, 4443,  -848,  358, -126
        },
        new short[20] {
             -18, -128,  -670, -201, -6389, -9235, 2544, -1055,  100,  -90,
             -18, -128,  -670, -201, -6389, -9235, 2544, -1055,  100,  -90
        },
        new short[20] {
             -49,  -61,  -946,  944, -8082, -8082,  944,  -946,  -61,  -49,
             -49,  -61,  -946,  944, -8082, -8082,  944,  -946,  -61,  -49
        },
        new short[20] {
             -90,  100, -1055, 2544, -9235, -6389, -201,  -670, -128,  -18,
             -90,  100, -1055, 2544, -9235, -6389, -201,  -670, -128,  -18
        }
    };

    /// <summary>
    /// Windowing coefficients for 8 subbands (fixed-point 2.13 format)
    /// Duplicated and transposed to fit circular buffer
    /// </summary>
    public static readonly short[][] Window8 = new short[8][]
    {
        new short[20] {
                0, -132,  -371, -848, -4456, -9631, 4456,  -848,  371, -132,
                0, -132,  -371, -848, -4456, -9631, 4456,  -848,  371, -132
        },
        new short[20] {
              -10, -138,  -526, -580, -5438, -9528, 3486, -1004,  229, -117,
              -10, -138,  -526, -580, -5438, -9528, 3486, -1004,  229, -117
        },
        new short[20] {
              -22, -131,  -685, -192, -6395, -9224, 2561, -1063,  108,  -97,
              -22, -131,  -685, -192, -6395, -9224, 2561, -1063,  108,  -97
        },
        new short[20] {
              -36, -106,  -835,  322, -7287, -8734, 1711, -1042,   12,  -75,
              -36, -106,  -835,  322, -7287, -8734, 1711, -1042,   12,  -75
        },
        new short[20] {
              -54,  -59,  -960,  959, -8078, -8078,  959,  -960,  -59,  -54,
              -54,  -59,  -960,  959, -8078, -8078,  959,  -960,  -59,  -54
        },
        new short[20] {
              -75,   12, -1042, 1711, -8734, -7287,  322,  -835, -106,  -36,
              -75,   12, -1042, 1711, -8734, -7287,  322,  -835, -106,  -36
        },
        new short[20] {
              -97,  108, -1063, 2561, -9224, -6395, -192,  -685, -131,  -22,
              -97,  108, -1063, 2561, -9224, -6395, -192,  -685, -131,  -22
        },
        new short[20] {
             -117,  229, -1004, 3486, -9528, -5438, -580,  -526, -138,  -10,
             -117,  229, -1004, 3486, -9528, -5438, -580,  -526, -138,  -10
        }
    };
}
