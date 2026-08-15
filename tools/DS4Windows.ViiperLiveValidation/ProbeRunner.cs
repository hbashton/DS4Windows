using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DS4Windows.ViiperLiveValidation;

internal sealed class ProbeResult
{
    internal int ExitCode { get; init; }
    internal string StandardOutput { get; init; } = string.Empty;
    internal string StandardError { get; init; } = string.Empty;
}

internal static class ProbeRunner
{
    private const int MaximumCapturedCharacters = 16384;

    internal static async Task<ProbeResult> RunAsync(
        ImmutableProbeExecutable executable,
        IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(timeout);
        using Process process = CreateProcess(executable, arguments);
        try
        {
            if (!process.Start())
            {
                throw new IOException(
                    $"Could not start exact probe '{executable.Path}'.");
            }
            executable.VerifyStartedProcess(process);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(
                deadline.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(
                deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var result = new ProbeResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = EvidenceLimits.Truncate(await stdout,
                    MaximumCapturedCharacters).Trim(),
                StandardError = EvidenceLimits.Truncate(await stderr,
                    MaximumCapturedCharacters).Trim(),
            };
            if (result.ExitCode != 0)
            {
                throw new IOException(
                    $"Exact probe '{Path.GetFileName(executable.Path)}' exited {result.ExitCode}: {result.StandardError} {result.StandardOutput}".Trim());
            }
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            throw new TimeoutException(
                $"Exact probe '{Path.GetFileName(executable.Path)}' exceeded its {timeout.TotalSeconds:F0}-second bound.");
        }
        catch
        {
            KillProcessTree(process);
            throw;
        }
    }

    internal static async Task<InputEvidence> MeasureInputAsync(
        ImmutableProbeExecutable executable, string snapshotPath,
        ControllerSpec spec,
        int samples, ViiperOutDevice outputDevice,
        CancellationToken cancellationToken)
    {
        // This is the packaged ViiperUdeInputProbe qpc-v1 handshake used by
        // VIIPER's native_live_windows_test.go reference gate.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using Process process = CreateProcess(executable, new[]
        {
            "measure", snapshotPath, spec.Vid, spec.Pid,
            spec.MarkerOffset.ToString(CultureInfo.InvariantCulture),
            samples.ToString(CultureInfo.InvariantCulture), "qpc-v1",
        });
        try
        {
            if (!process.Start())
            {
                throw new IOException("Could not start the exact input probe.");
            }
            executable.VerifyStartedProcess(process);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(
                deadline.Token);
            string readyLine = await ReadRequiredLineAsync(process,
                deadline.Token, "READY").ConfigureAwait(false);
            string[] ready = readyLine.Split(' ', 4,
                StringSplitOptions.RemoveEmptyEntries);
            if (ready.Length != 4 || ready[0] != "READY" ||
                !long.TryParse(ready[1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out long frequency) ||
                frequency <= 0 || frequency != Stopwatch.Frequency ||
                !int.TryParse(ready[2], NumberStyles.None,
                    CultureInfo.InvariantCulture, out int reportLength) ||
                reportLength <= spec.MarkerOffset ||
                string.IsNullOrWhiteSpace(ready[3]))
            {
                throw new IOException(
                    $"The input probe returned a non-canonical READY receipt: '{readyLine}'.");
            }

            var evidence = new InputEvidence
            {
                ObserverPath = ready[3],
                QpcFrequency = frequency,
                HidInputReportLength = reportLength,
            };
            var state = ViiperStatePacketBuilder.CreateNeutralState();
            for (int index = 0; index < samples; index++)
            {
                byte marker = (byte)(0xFD + (index & 1));
                if (spec.MarkerUsesRightStick)
                {
                    state.RX = marker;
                }
                else
                {
                    state.LX = marker;
                }
                long published = Stopwatch.GetTimestamp();
                outputDevice.ConvertandSendReport(state, 0);
                string matchLine = await ReadRequiredLineAsync(process,
                    deadline.Token, "MATCH").ConfigureAwait(false);
                string[] match = matchLine.Split(' ',
                    StringSplitOptions.RemoveEmptyEntries);
                if (match.Length != 3 || match[0] != "MATCH" ||
                    !byte.TryParse(match[1], NumberStyles.None,
                        CultureInfo.InvariantCulture, out byte observedMarker) ||
                    observedMarker != marker ||
                    !long.TryParse(match[2], NumberStyles.None,
                        CultureInfo.InvariantCulture, out long observed) ||
                    observed < published)
                {
                    throw new IOException(
                        $"The input probe returned a non-canonical MATCH receipt: '{matchLine}'.");
                }
                evidence.Samples.Add(new InputSampleEvidence
                {
                    Sequence = index + 1,
                    Marker = marker,
                    PublishedQpc = published,
                    ObservedQpc = observed,
                    LatencyMicroseconds = QpcDeltaMicroseconds(
                        observed - published, frequency),
                });
            }

            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string stderr = EvidenceLimits.Truncate(await stderrTask,
                MaximumCapturedCharacters).Trim();
            if (process.ExitCode != 0)
            {
                throw new IOException(
                    $"The exact input probe exited {process.ExitCode}: {stderr}");
            }
            evidence.Summary = Summarize(evidence.Samples);
            if (!evidence.Summary.Passed)
            {
                throw new IOException(
                    $"{spec.Name} input latency exceeded the native gate: p95={evidence.Summary.P95Microseconds}us p99={evidence.Summary.P99Microseconds}us max={evidence.Summary.MaximumMicroseconds}us.");
            }
            return evidence;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            throw new TimeoutException(
                $"The {spec.Name} input probe exceeded its 45-second bound.");
        }
        catch
        {
            KillProcessTree(process);
            throw;
        }
    }

    internal static LatencySummaryEvidence Summarize(
        IReadOnlyList<InputSampleEvidence> samples)
    {
        if (samples.Count == 0 || samples.Count >
            LiveValidationOptions.MaximumSamples)
        {
            throw new ArgumentOutOfRangeException(nameof(samples));
        }
        long[] sorted = samples.Select(sample => sample.LatencyMicroseconds)
            .OrderBy(value => value).ToArray();
        var summary = new LatencySummaryEvidence
        {
            Samples = sorted.Length,
            P50Microseconds = Percentile(sorted, 50),
            P95Microseconds = Percentile(sorted, 95),
            P99Microseconds = Percentile(sorted, 99),
            MaximumMicroseconds = sorted[^1],
        };
        summary.Passed = summary.P95Microseconds <= 4000 &&
            summary.P99Microseconds <= 8000 &&
            summary.MaximumMicroseconds <= 20000;
        return summary;
    }

    internal static SortedDictionary<string, string> ParseMetrics(
        string output)
    {
        var result = new SortedDictionary<string, string>(
            StringComparer.Ordinal);
        foreach (string field in output.Split((char[]?)null,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = field.IndexOf('=');
            if (separator <= 0 || separator == field.Length - 1 ||
                !result.TryAdd(field[..separator], field[(separator + 1)..]))
            {
                throw new IOException(
                    "The media probe returned a non-canonical metric receipt.");
            }
        }
        if (result.Count < 8 || result.Count > 32)
        {
            throw new IOException(
                "The media probe metric receipt has an unexpected field count.");
        }
        return result;
    }

    private static async Task<string> ReadRequiredLineAsync(Process process,
        CancellationToken cancellationToken, string expectedPrefix)
    {
        string? line = await process.StandardOutput.ReadLineAsync(
            cancellationToken).ConfigureAwait(false);
        if (line == null || line.Length > 8192 ||
            !line.StartsWith(expectedPrefix + " ", StringComparison.Ordinal))
        {
            throw new IOException(
                $"The exact input probe did not return a bounded {expectedPrefix} receipt.");
        }
        return line;
    }

    private static long QpcDeltaMicroseconds(long ticks, long frequency)
    {
        if (ticks < 0 || frequency <= 0 ||
            ticks > long.MaxValue / 1_000_000L)
        {
            throw new IOException("The input probe returned an invalid QPC delta.");
        }
        return (ticks * 1_000_000L + frequency / 2) / frequency;
    }

    private static long Percentile(long[] sorted, int percentile)
    {
        int index = (sorted.Length - 1) * percentile / 100;
        return sorted[index];
    }

    private static Process CreateProcess(ImmutableProbeExecutable executable,
        IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable.Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable.Path) ??
                AppContext.BaseDirectory,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return new Process { StartInfo = start };
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
        }
    }
}
