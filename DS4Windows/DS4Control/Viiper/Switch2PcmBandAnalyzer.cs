/*
DS4Windows - Copyright (C) 2026 hbashton
SPDX-License-Identifier: GPL-3.0-or-later

Band selection and output-frequency remapping adapted from Switch2Connect,
Copyright (C) 2026 TommyWabg, src/dualsense_haptic.py at
61ac6642ce12fe7217e38a860b14863b18ca7e28 (GPL-3.0-or-later).
The packet-local Goertzel analysis and envelope projection are local code.
*/
using System;
using DS4Windows.Switch2;

namespace DS4Windows;

/// <summary>
/// Uses exactly the 32 samples already in the 3 kHz carrier, without buffering
/// another packet, zero padding, or retaining another controller's history.
/// Resolution is 93.75 Hz, not the reference's 64-sample resolution. LF uses
/// bins 1/2; HF uses the remaining bins, retaining above-reference energy at
/// the bounded HF carrier ceiling. DC stays in the legacy envelope fallback.
/// These are synthesis control codes, not calibrated actuator frequencies.
/// </summary>
internal readonly record struct Switch2PcmBands(double LowScale, double HighScale,
    ushort LowControl, ushort HighControl);

internal readonly record struct Switch2PcmSlice(double LowAmplitude, double HighAmplitude,
    ushort LowControl, ushort HighControl);

internal static class Switch2PcmBandAnalyzer
{
    internal const int SampleCount = 32;
    private static readonly double[] Coefficients = CreateCoefficients();
    private static readonly double[] LowBasis = CreateLowBasis();

    /// <summary>
    /// Reconstructs the packet's LF component (DC and bins 1/2); its complement
    /// is HF. Each chronological slice then measures its OWN band energy and
    /// peaks, not the packet-wide proportions. No inter-packet state or wait.
    /// </summary>
    internal static void AnalyzeSlices(ReadOnlySpan<byte> interleaved, int channel,
        Span<Switch2PcmSlice> slices)
    {
        Switch2PcmBands bands = Analyze(interleaved, channel);
        Span<double> projection = stackalloc double[5];
        projection.Clear();
        for (int i = 0; i < SampleCount; i++)
        {
            double sample = unchecked((sbyte)interleaved[i * 2 + channel]);
            projection[0] += sample / SampleCount;
            for (int basis = 0; basis < 4; basis++)
                projection[basis + 1] += sample * LowBasis[basis * SampleCount + i] *
                    (2.0 / SampleCount);
        }

        Span<double> lowSamples = stackalloc double[SampleCount];
        for (int i = 0; i < SampleCount; i++)
        {
            double low = projection[0];
            for (int basis = 0; basis < 4; basis++)
                low += projection[basis + 1] * LowBasis[basis * SampleCount + i];
            lowSamples[i] = low;
        }

        for (int slice = 0; slice < 3; slice++)
        {
            int start = SampleCount * slice / 3;
            int end = SampleCount * (slice + 1) / 3;
            double sourceSquares = 0, sourcePeak = 0;
            double lowSquares = 0, highSquares = 0, lowPeak = 0, highPeak = 0;
            for (int i = start; i < end; i++)
            {
                double sample = unchecked((sbyte)interleaved[i * 2 + channel]);
                double low = lowSamples[i], high = sample - low;
                sourceSquares += sample * sample;
                sourcePeak = Math.Max(sourcePeak, Math.Abs(sample));
                lowSquares += low * low;
                highSquares += high * high;
                lowPeak = Math.Max(lowPeak, Math.Abs(low));
                highPeak = Math.Max(highPeak, Math.Abs(high));
            }

            // Finite-window reconstruction can ring across slice boundaries.
            // Never turn that into pre-rumble or a tail in an actually silent
            // slice. Normalize local band energy to that slice's source energy,
            // and never let a reconstructed peak exceed its authored peak.
            double bandSquares = lowSquares + highSquares;
            double scale = bandSquares > 0 ? Math.Sqrt(sourceSquares / bandSquares) : 0;
            int count = end - start;
            slices[slice] = new Switch2PcmSlice(
                PeakEnvelope(Math.Sqrt(lowSquares / count) * scale, lowPeak * scale, sourcePeak),
                PeakEnvelope(Math.Sqrt(highSquares / count) * scale, highPeak * scale, sourcePeak),
                bands.LowControl, bands.HighControl);
        }
    }

    private static double PeakEnvelope(double rms, double peak, double sourcePeak)
    {
        // Retain the existing 1.45 steady-tone gain, but bound it by the actual
        // peak so louder authored samples do not hit full scale prematurely.
        // A 95% peak floor preserves brief attacks that RMS alone dilutes.
        // This is a bounded synthesis policy, not a lossless waveform decoder.
        return Math.Min(sourcePeak, Math.Min(peak, Math.Max(rms * 1.45, peak * 0.95))) / 128.0;
    }

    internal static Switch2PcmBands Analyze(ReadOnlySpan<byte> interleaved, int channel)
    {
        Span<double> samples = stackalloc double[SampleCount];
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double sample = unchecked((sbyte)interleaved[i * 2 + channel]);
            samples[i] = sample;
            sum += sample;
        }
        double dcPower = sum * sum;
        double lowPower = dcPower, highPower = 0, lowPeak = 0, highPeak = 0;
        int lowBin = 0, highBin = 0;
        for (int bin = 1; bin <= SampleCount / 2; bin++)
        {
            double coefficient = Coefficients[bin], previous = 0, previous2 = 0;
            foreach (double sample in samples)
            {
                double current = sample + coefficient * previous - previous2;
                previous2 = previous;
                previous = current;
            }
            double power = Math.Max(0, previous * previous + previous2 * previous2 -
                coefficient * previous * previous2);
            // Positive/negative frequency pairs count twice, except Nyquist.
            power *= bin == SampleCount / 2 ? 1 : 2;
            if (bin <= 2)
            {
                lowPower += power;
                if (power > lowPeak) { lowPeak = power; lowBin = bin; }
            }
            else
            {
                highPower += power;
                if (power > highPeak) { highPeak = power; highBin = bin; }
            }
        }
        double total = lowPower + highPower;
        return new Switch2PcmBands(total > 0 ? Math.Sqrt(lowPower / total) : 0,
            total > 0 ? Math.Sqrt(highPower / total) : 0,
            lowBin != 0 && lowPeak > dcPower ? Remap(lowBin, 94, 234, 225, 281) :
                Switch2HdRumbleFeedbackTranslator.SdlLowControlCode,
            highBin != 0 ? Remap(highBin, 281, 609, 281, 369) :
                Switch2HdRumbleFeedbackTranslator.SdlHighControlCode);
    }

    private static ushort Remap(int bin, double inputMin, double inputMax,
        double outputMin, double outputMax) => (ushort)Math.Round(Math.Clamp(
            outputMin + (bin * 3000.0 / SampleCount - inputMin) *
            (outputMax - outputMin) / (inputMax - inputMin), outputMin, outputMax));

    private static double[] CreateCoefficients()
    {
        var result = new double[SampleCount / 2 + 1];
        for (int bin = 1; bin < result.Length; bin++)
            result[bin] = 2 * Math.Cos(2 * Math.PI * bin / SampleCount);
        return result;
    }

    private static double[] CreateLowBasis()
    {
        var result = new double[4 * SampleCount];
        for (int bin = 1; bin <= 2; bin++)
            for (int i = 0; i < SampleCount; i++)
            {
                double angle = 2 * Math.PI * bin * i / SampleCount;
                result[(bin - 1) * 2 * SampleCount + i] = Math.Cos(angle);
                result[((bin - 1) * 2 + 1) * SampleCount + i] = Math.Sin(angle);
            }
        return result;
    }
}
